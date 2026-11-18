using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

public sealed class CodexUsageReader
{
    private const int MaxSessionFiles = 500;
    private const int ReverseReadBufferBytes = 64 * 1024;
    private const int MaxRelevantLineBytes = 512 * 1024;
    private const long MaxReverseScanBytes = 128L * 1024 * 1024;
    private const long MaxIncrementalReadBytes = 16L * 1024 * 1024;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    private static int _lastLoggedActiveWindowCount = -1;

    private readonly CodexOfficialUsageReader _officialReader = new();
    private readonly Dictionary<string, SessionCacheEntry> _sessionCache = new(StringComparer.OrdinalIgnoreCase);
    private int _performanceSamplesRemaining = 3;

    public CodexReadResult Read()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "sessions");

        if (!Directory.Exists(root))
        {
            return CodexReadResult.Empty;
        }

        var stopwatch = Stopwatch.StartNew();
        var now = DateTimeOffset.Now;
        var today = now.Date;
        var activeCutoff = now.AddHours(-4);
        var readCutoff = activeCutoff.LocalDateTime < today
            ? activeCutoff.LocalDateTime
            : today;
        var files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.LastWriteTime >= readCutoff)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(MaxSessionFiles)
            .ToList();
        var sessionNames = ReadSessionNames();
        var sessionStats = new SessionReadStats();

        var projectTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var total = TokenTotals.Empty;
        CodexUsage latestUsage = CodexUsage.Empty;
        DateTimeOffset? latestTokenEvent = null;
        var sessionCount = 0;
        var workWindows = new List<CodexWorkWindow>();

        foreach (var file in files)
        {
            var session = ReadSession(file.FullName, today, sessionStats);
            if (session is null)
            {
                continue;
            }

            // Any session with a live context window; we keep the most recent few below.
            if (session.LatestTokenTimestamp is not null &&
                session.ContextWindow > 0)
            {
                var fullSessionId = ExtractFullSessionId(file.Name);
                sessionNames.TryGetValue(fullSessionId, out var workName);
                workWindows.Add(new CodexWorkWindow(
                    ShortenProjectName(string.IsNullOrWhiteSpace(session.Project) ? "(unknown)" : session.Project!),
                    ShortenSessionId(fullSessionId),
                    session.ContextUsed,
                    session.ContextWindow,
                    session.LatestTokenTimestamp,
                    session.Model,
                    session.Effort,
                    workName));
            }

            if (session.LatestTokenTimestamp is not null &&
                (latestTokenEvent is null || session.LatestTokenTimestamp > latestTokenEvent))
            {
                latestTokenEvent = session.LatestTokenTimestamp;
                latestUsage = session.ToCodexUsage(IsStale(session.LatestTokenTimestamp.Value));
            }

            if (!session.HasTodayUsage)
            {
                continue;
            }

            sessionCount++;
            total = total with
            {
                InputTokens = total.InputTokens + session.Today.InputTokens,
                CachedInputTokens = total.CachedInputTokens + session.Today.CachedInputTokens,
                OutputTokens = total.OutputTokens + session.Today.OutputTokens,
                ReasoningOutputTokens = total.ReasoningOutputTokens + session.Today.ReasoningOutputTokens,
                TotalTokens = total.TotalTokens + session.Today.TotalTokens,
                SessionCount = sessionCount
            };

            var project = string.IsNullOrWhiteSpace(session.Project)
                ? "(unknown)"
                : session.Project;

            projectTotals[project] = projectTotals.GetValueOrDefault(project) + session.Today.TotalTokens;
        }

        var currentPaths = files
            .Select(file => file.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var cachedPath in _sessionCache.Keys.Where(path => !currentPaths.Contains(path)).ToArray())
        {
            _sessionCache.Remove(cachedPath);
        }

        var topProjects = projectTotals
            .OrderByDescending(pair => pair.Value)
            .Take(8)
            .Select(pair => new ProjectUsage(ShortenProjectName(pair.Key), pair.Value))
            .ToList();

        // Prefer the OFFICIAL account usage (matches the ChatGPT "remaining usage" panel) over the
        // local per-model bucket, which only reflects whichever model was used last (e.g. Spark = 0%).
        var activeWindows = workWindows
            .Where(window => window.LastActivity >= activeCutoff)
            .OrderByDescending(window => window.LastActivity)
            .Take(20)
            .ToList();

        if (_lastLoggedActiveWindowCount != activeWindows.Count)
        {
            _lastLoggedActiveWindowCount = activeWindows.Count;
            DiagnosticsService.Write($"Codex work windows: {activeWindows.Count} active within 4h.");
        }

        stopwatch.Stop();
        if (_performanceSamplesRemaining > 0 || stopwatch.ElapsedMilliseconds >= 750)
        {
            DiagnosticsService.Write(
                $"Codex local scan: {stopwatch.ElapsedMilliseconds} ms, files={files.Count}, " +
                $"full={sessionStats.FullReads}, incremental={sessionStats.IncrementalReads}, " +
                $"cached={sessionStats.CacheHits}, appended={sessionStats.AppendedBytes / 1024} KB, " +
                $"today={total.TotalTokens}, projects={topProjects.Count}.");
            if (_performanceSamplesRemaining > 0)
            {
                _performanceSamplesRemaining--;
            }
        }

        var official = _officialReader.TryRead(out var officialFailure);
        if (officialFailure is not null)
        {
            DiagnosticsService.Write("Codex official: " + officialFailure + (official is not null ? " Using cached value." : string.Empty));
        }
        var usage = official is not null
            ? new CodexUsage(
                official.PrimaryPercent, official.PrimaryWindowMinutes, official.PrimaryResetsAt,
                official.SecondaryPercent, official.SecondaryWindowMinutes, official.SecondaryResetsAt,
                official.LastAttemptOk != true, true,
                SubscriptionPlanCatalog.ResolveCodexPlan(official.PlanType, official.AccountKey, AppSettingsService.Load()), official.SyncedAt,
                official.Credits, official.AccountKey)
            : CodexUsage.Empty;

        return new CodexReadResult(latestTokenEvent, usage, total, topProjects, activeWindows);
    }

    private static string ExtractFullSessionId(string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            fileName,
            "([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
        return match.Success ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(fileName);
    }

    private static string ShortenSessionId(string sessionId) =>
        sessionId.Length > 8 ? sessionId[..8] : sessionId;

    private static IReadOnlyDictionary<string, string> ReadSessionNames()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "session_index.jsonl");
        var entries = new Dictionary<string, SessionNameEntry>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            foreach (var line in ReadSharedLines(path))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var id = GetString(root, "id");
                    var name = GetString(root, "thread_name");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var rawUpdatedAt = GetString(root, "updated_at");
                    var updatedAt = DateTimeOffset.TryParse(rawUpdatedAt, out var parsed)
                        ? parsed
                        : DateTimeOffset.MinValue;
                    if (!entries.TryGetValue(id, out var existing) || updatedAt >= existing.UpdatedAt)
                    {
                        entries[id] = new SessionNameEntry(name.Trim(), updatedAt);
                    }
                }
                catch
                {
                    // Ignore a partially-written index line and keep the last valid name.
                }
            }
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return entries.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Name,
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsStale(DateTimeOffset timestamp) =>
        DateTimeOffset.Now - timestamp.ToLocalTime() > StaleAfter;

    private CodexSession? ReadSession(string path, DateTime today, SessionReadStats stats)
    {
        try
        {
            var completeLength = GetCompleteLength(path, out var physicalLength);
            if (_sessionCache.TryGetValue(path, out var cached) && cached.Day == today)
            {
                if (physicalLength >= cached.CompleteLength && completeLength == 0)
                {
                    stats.CacheHits++;
                    return cached.Session;
                }

                if (completeLength >= cached.CompleteLength)
                {
                    var appendedBytes = completeLength - cached.CompleteLength;
                    if (appendedBytes == 0)
                    {
                        stats.CacheHits++;
                        return cached.Session;
                    }

                    if (appendedBytes <= MaxIncrementalReadBytes)
                    {
                        var updated = ReadSessionIncremental(
                            path,
                            cached.CompleteLength,
                            completeLength,
                            today,
                            cached.Session);
                        _sessionCache[path] = new SessionCacheEntry(today, completeLength, updated);
                        stats.IncrementalReads++;
                        stats.AppendedBytes += appendedBytes;
                        return updated;
                    }
                }
            }

            var session = ReadSessionFull(path, completeLength, today);
            stats.FullReads++;
            if (session is null)
            {
                _sessionCache.Remove(path);
                return null;
            }

            _sessionCache[path] = new SessionCacheEntry(today, completeLength, session);
            return session;
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write(
                $"Codex session parse failed for {Path.GetFileName(path)}: " +
                $"{ex.GetType().Name}. Parsed data retained.");
            if (_sessionCache.TryGetValue(path, out var cached) && cached.Day == today)
            {
                return cached.Session;
            }

            return null;
        }
    }

    private static CodexSession? ReadSessionFull(string path, long completeLength, DateTime today)
    {
        var project = ReadSessionProject(path);
        string? model = null;
        string? effort = null;
        CodexSession? latest = null;
        var todayTotals = TokenTotals.Empty;
        var hasTodayUsage = false;

        try
        {
            foreach (var line in ReadRelevantLinesReverse(path, completeLength))
            {
                if (line.Length < 10 || line[0] != '{')
                {
                    continue;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    try
                    {
                        var root = document.RootElement;
                        if (root.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var timestamp = ParseTimestamp(root);
                        ReadWorkSettings(root, ref model, ref effort);

                        if (!line.Contains("\"token_count\"", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (!root.TryGetProperty("payload", out var payload) ||
                            payload.ValueKind != JsonValueKind.Object ||
                            GetString(payload, "type") != "token_count")
                        {
                            continue;
                        }

                        latest ??= ParseTokenCount(
                            payload,
                            timestamp,
                            project,
                            TokenTotals.Empty,
                            false,
                            model,
                            effort);

                        if (timestamp?.LocalDateTime.Date == today)
                        {
                            var lastUsage = ParseLastTokenUsage(payload);
                            todayTotals = todayTotals with
                            {
                                InputTokens = todayTotals.InputTokens + lastUsage.InputTokens,
                                CachedInputTokens = todayTotals.CachedInputTokens + lastUsage.CachedInputTokens,
                                OutputTokens = todayTotals.OutputTokens + lastUsage.OutputTokens,
                                ReasoningOutputTokens = todayTotals.ReasoningOutputTokens + lastUsage.ReasoningOutputTokens,
                                TotalTokens = todayTotals.TotalTokens + lastUsage.TotalTokens
                            };
                            hasTodayUsage = true;
                            continue;
                        }

                        if (timestamp?.LocalDateTime.Date < today)
                        {
                            break;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // A newer event may use a different JSON shape. Skip only that event so
                        // one incompatible record cannot erase the entire active-work list.
                        continue;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write(
                $"Codex session parse failed for {Path.GetFileName(path)}: " +
                $"{ex.GetType().Name}. Parsed data retained.");
        }

        return latest is null
            ? null
            : latest with
            {
                Project = project ?? latest.Project,
                Model = model ?? latest.Model,
                Effort = effort ?? latest.Effort,
                Today = todayTotals,
                HasTodayUsage = hasTodayUsage
            };
    }

    private static CodexSession ReadSessionIncremental(
        string path,
        long startOffset,
        long endOffset,
        DateTime today,
        CodexSession cached)
    {
        var latest = cached;
        var model = cached.Model;
        var effort = cached.Effort;
        var todayTotals = cached.Today;
        var hasTodayUsage = cached.HasTodayUsage;

        foreach (var line in ReadCompleteLines(path, startOffset, endOffset))
        {
            if (line.Length < 10 || line[0] != '{' || !IsRelevantSessionLine(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                try
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var timestamp = ParseTimestamp(root);
                    ReadWorkSettings(root, ref model, ref effort, overwriteExisting: true);

                    if (!root.TryGetProperty("payload", out var payload) ||
                        payload.ValueKind != JsonValueKind.Object ||
                        GetString(payload, "type") != "token_count")
                    {
                        continue;
                    }

                    if (timestamp?.LocalDateTime.Date == today)
                    {
                        todayTotals = AddTokenTotals(todayTotals, ParseLastTokenUsage(payload));
                        hasTodayUsage = true;
                    }

                    var parsed = ParseTokenCount(
                        payload,
                        timestamp,
                        cached.Project,
                        todayTotals,
                        hasTodayUsage,
                        model,
                        effort);
                    latest = MergeIncrementalSession(
                        latest,
                        parsed,
                        todayTotals,
                        hasTodayUsage,
                        model,
                        effort);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
            }
        }

        return latest with
        {
            Model = model ?? latest.Model,
            Effort = effort ?? latest.Effort,
            Today = todayTotals,
            HasTodayUsage = hasTodayUsage
        };
    }

    private static CodexSession MergeIncrementalSession(
        CodexSession current,
        CodexSession parsed,
        TokenTotals todayTotals,
        bool hasTodayUsage,
        string? model,
        string? effort)
    {
        return current with
        {
            LastTokenTimestamp = parsed.LastTokenTimestamp ?? current.LastTokenTimestamp,
            Total = HasTokenTotals(parsed.Total) ? parsed.Total : current.Total,
            Today = todayTotals,
            HasTodayUsage = hasTodayUsage,
            Primary = HasRateLimit(parsed.Primary) ? parsed.Primary : current.Primary,
            Secondary = HasRateLimit(parsed.Secondary) ? parsed.Secondary : current.Secondary,
            ContextWindow = parsed.ContextWindow > 0 ? parsed.ContextWindow : current.ContextWindow,
            ContextUsed = parsed.ContextWindow > 0 ? parsed.ContextUsed : current.ContextUsed,
            Model = model ?? parsed.Model ?? current.Model,
            Effort = effort ?? parsed.Effort ?? current.Effort
        };
    }

    private static TokenTotals AddTokenTotals(TokenTotals current, TokenTotals added) => current with
    {
        InputTokens = current.InputTokens + added.InputTokens,
        CachedInputTokens = current.CachedInputTokens + added.CachedInputTokens,
        OutputTokens = current.OutputTokens + added.OutputTokens,
        ReasoningOutputTokens = current.ReasoningOutputTokens + added.ReasoningOutputTokens,
        TotalTokens = current.TotalTokens + added.TotalTokens
    };

    private static bool HasTokenTotals(TokenTotals totals) =>
        totals.InputTokens != 0 ||
        totals.CachedInputTokens != 0 ||
        totals.OutputTokens != 0 ||
        totals.ReasoningOutputTokens != 0 ||
        totals.TotalTokens != 0;

    private static bool HasRateLimit(RateLimit limit) =>
        limit.UsedPercent is not null || limit.WindowMinutes is not null || limit.ResetsAt is not null;

    private static void ReadWorkSettings(
        JsonElement root,
        ref string? model,
        ref string? effort,
        bool overwriteExisting = false)
    {
        if (!root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var currentModel = GetString(payload, "model");
        var currentEffort = GetString(payload, "effort") ?? GetString(payload, "reasoning_effort");

        if (payload.TryGetProperty("thread_settings", out var threadSettings) &&
            threadSettings.ValueKind == JsonValueKind.Object)
        {
            currentModel = GetString(threadSettings, "model") ?? currentModel;
            currentEffort = GetString(threadSettings, "reasoning_effort")
                ?? GetString(threadSettings, "effort")
                ?? currentEffort;
        }

        if (payload.TryGetProperty("collaboration_mode", out var collaborationMode) &&
            collaborationMode.ValueKind == JsonValueKind.Object &&
            collaborationMode.TryGetProperty("settings", out var collaborationSettings) &&
            collaborationSettings.ValueKind == JsonValueKind.Object)
        {
            currentModel = GetString(collaborationSettings, "model") ?? currentModel;
            currentEffort = GetString(collaborationSettings, "reasoning_effort")
                ?? GetString(collaborationSettings, "effort")
                ?? currentEffort;
        }

        if ((overwriteExisting || model is null) && !string.IsNullOrWhiteSpace(currentModel))
        {
            model = currentModel;
        }

        if ((overwriteExisting || effort is null) && !string.IsNullOrWhiteSpace(currentEffort))
        {
            effort = currentEffort;
        }
    }

    private static string? ReadSessionProject(string path)
    {
        try
        {
            foreach (var line in ReadSharedLines(path).Take(8))
            {
                if (!line.Contains("\"session_meta\"", StringComparison.Ordinal) ||
                    line.Length > MaxRelevantLineBytes)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("payload", out var payload))
                {
                    return GetString(payload, "cwd");
                }
            }
        }
        catch
        {
            // Project metadata is optional; token/rate-limit parsing can continue without it.
        }

        return null;
    }

    private static long GetCompleteLength(string path, out long physicalLength)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        physicalLength = stream.Length;
        if (physicalLength == 0)
        {
            return 0;
        }

        var scanLength = (int)Math.Min(physicalLength, MaxRelevantLineBytes + 1L);
        var buffer = new byte[scanLength];
        var bufferStart = physicalLength - scanLength;
        stream.Position = bufferStart;
        var read = 0;
        while (read < scanLength)
        {
            var current = stream.Read(buffer, read, scanLength - read);
            if (current <= 0)
            {
                break;
            }

            read += current;
        }

        for (var index = read - 1; index >= 0; index--)
        {
            if (buffer[index] == (byte)'\n')
            {
                return bufferStart + index + 1;
            }
        }

        return 0;
    }

    private static IEnumerable<string> ReadCompleteLines(string path, long startOffset, long endOffset)
    {
        var length = endOffset - startOffset;
        if (length <= 0)
        {
            yield break;
        }

        if (length > MaxIncrementalReadBytes)
        {
            throw new InvalidDataException("Codex incremental range exceeded the safety limit.");
        }

        var bytes = new byte[(int)length];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length < endOffset)
            {
                throw new EndOfStreamException("Codex session changed during incremental read.");
            }

            stream.Position = startOffset;
            var read = 0;
            while (read < bytes.Length)
            {
                var current = stream.Read(bytes, read, bytes.Length - read);
                if (current <= 0)
                {
                    throw new EndOfStreamException("Codex session ended during incremental read.");
                }

                read += current;
            }
        }

        var text = Encoding.UTF8.GetString(bytes);
        var lineStart = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            var lineLength = index - lineStart;
            if (lineLength > 0 && text[index - 1] == '\r')
            {
                lineLength--;
            }

            if (lineLength > 0 && lineLength <= MaxRelevantLineBytes)
            {
                yield return text.Substring(lineStart, lineLength);
            }

            lineStart = index + 1;
        }
    }

    private static IEnumerable<string> ReadRelevantLinesReverse(string path, long endPosition)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[ReverseReadBufferBytes];
        var reversedLine = new List<byte>(4096);
        var discardLine = false;
        var position = Math.Min(stream.Length, Math.Max(0, endPosition));
        long scanned = 0;

        while (position > 0 && scanned < MaxReverseScanBytes)
        {
            var readLength = (int)Math.Min(buffer.Length, Math.Min(position, MaxReverseScanBytes - scanned));
            position -= readLength;
            stream.Position = position;
            var read = 0;
            while (read < readLength)
            {
                var current = stream.Read(buffer, read, readLength - read);
                if (current <= 0)
                {
                    break;
                }

                read += current;
            }

            scanned += read;
            for (var index = read - 1; index >= 0; index--)
            {
                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    if (!discardLine && reversedLine.Count > 0)
                    {
                        reversedLine.Reverse();
                        var line = Encoding.UTF8.GetString(reversedLine.ToArray());
                        if (IsRelevantSessionLine(line))
                        {
                            yield return line;
                        }
                    }

                    reversedLine.Clear();
                    discardLine = false;
                    continue;
                }

                if (value == (byte)'\r' || discardLine)
                {
                    continue;
                }

                if (reversedLine.Count >= MaxRelevantLineBytes)
                {
                    reversedLine.Clear();
                    discardLine = true;
                    continue;
                }

                reversedLine.Add(value);
            }
        }

        if (position == 0 && !discardLine && reversedLine.Count > 0)
        {
            reversedLine.Reverse();
            var line = Encoding.UTF8.GetString(reversedLine.ToArray());
            if (IsRelevantSessionLine(line))
            {
                yield return line;
            }
        }
    }

    private static bool IsRelevantSessionLine(string line) =>
        line.Contains("\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"", StringComparison.Ordinal) ||
        line.Contains("\"type\":\"turn_context\"", StringComparison.Ordinal) ||
        line.Contains("\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"", StringComparison.Ordinal);

    private static CodexSession ParseTokenCount(
        JsonElement payload,
        DateTimeOffset? timestamp,
        string? project,
        TokenTotals today,
        bool hasTodayUsage,
        string? model,
        string? effort)
    {
        var info = payload.TryGetProperty("info", out var infoElement) ? infoElement : default;
        var totals = info.ValueKind == JsonValueKind.Object &&
                     info.TryGetProperty("total_token_usage", out var totalUsage)
            ? totalUsage
            : default;

        var rateLimits = payload.TryGetProperty("rate_limits", out var rateLimitElement)
            ? rateLimitElement
            : default;

        var contextWindow = info.ValueKind == JsonValueKind.Object ? GetLong(info, "model_context_window") : 0;
        var contextUsed = 0L;
        if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("last_token_usage", out var lastTokenUsage))
        {
            // The last request's input tokens = how full the live context window is.
            contextUsed = GetLong(lastTokenUsage, "input_tokens");
        }

        return new CodexSession(
            timestamp,
            project,
            new TokenTotals(
                GetLong(totals, "input_tokens"),
                GetLong(totals, "cached_input_tokens"),
                GetLong(totals, "output_tokens"),
                GetLong(totals, "reasoning_output_tokens"),
                GetLong(totals, "total_tokens"),
                1),
            today,
            hasTodayUsage,
            ParseLimit(rateLimits, "primary"),
            ParseLimit(rateLimits, "secondary"),
            contextWindow,
            contextUsed,
            model,
            effort);
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    private static TokenTotals ParseLastTokenUsage(JsonElement payload)
    {
        if (!payload.TryGetProperty("info", out var info) ||
            info.ValueKind != JsonValueKind.Object ||
            !info.TryGetProperty("last_token_usage", out var lastUsage))
        {
            return TokenTotals.Empty;
        }

        return new TokenTotals(
            GetLong(lastUsage, "input_tokens"),
            GetLong(lastUsage, "cached_input_tokens"),
            GetLong(lastUsage, "output_tokens"),
            GetLong(lastUsage, "reasoning_output_tokens"),
            GetLong(lastUsage, "total_tokens"),
            0);
    }

    private static RateLimit ParseLimit(JsonElement rateLimits, string name)
    {
        if (rateLimits.ValueKind != JsonValueKind.Object ||
            !rateLimits.TryGetProperty(name, out var limit))
        {
            return RateLimit.Empty;
        }

        return new RateLimit(
            GetDouble(limit, "used_percent"),
            GetInt(limit, "window_minutes"),
            ParseUnixSeconds(limit, "resets_at"));
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement root)
    {
        var raw = GetString(root, "timestamp");
        return DateTimeOffset.TryParse(raw, out var result) ? result.ToLocalTime() : null;
    }

    private static DateTimeOffset? ParseUnixSeconds(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        }

        return null;
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long GetLong(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var result)
            ? result
            : 0;
    }

    private static int? GetInt(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static double? GetDouble(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out var result)
            ? result
            : null;
    }

    private static string ShortenProjectName(string path)
    {
        if (path is "(unknown)")
        {
            return path;
        }

        try
        {
            return new DirectoryInfo(path).Name;
        }
        catch
        {
            return path;
        }
    }

    private sealed record CodexSession(
        DateTimeOffset? LastTokenTimestamp,
        string? Project,
        TokenTotals Total,
        TokenTotals Today,
        bool HasTodayUsage,
        RateLimit Primary,
        RateLimit Secondary,
        long ContextWindow = 0,
        long ContextUsed = 0,
        string? Model = null,
        string? Effort = null)
    {
        public DateTimeOffset? LatestTokenTimestamp => LastTokenTimestamp;

        public CodexUsage ToCodexUsage(bool isStale) => new(
            Primary.UsedPercent,
            Primary.WindowMinutes,
            Primary.ResetsAt,
            Secondary.UsedPercent,
            Secondary.WindowMinutes,
            Secondary.ResetsAt,
            isStale,
            true);
    }

    private sealed record RateLimit(double? UsedPercent, int? WindowMinutes, DateTimeOffset? ResetsAt)
    {
        public static RateLimit Empty { get; } = new(null, null, null);
    }

    private sealed record SessionCacheEntry(DateTime Day, long CompleteLength, CodexSession Session);

    private sealed class SessionReadStats
    {
        public int FullReads { get; set; }
        public int IncrementalReads { get; set; }
        public int CacheHits { get; set; }
        public long AppendedBytes { get; set; }
    }

    private sealed record SessionNameEntry(string Name, DateTimeOffset UpdatedAt);
}

public sealed record CodexReadResult(
    DateTimeOffset? LastUpdatedLocal,
    CodexUsage Usage,
    TokenTotals Today,
    IReadOnlyList<ProjectUsage> TopProjects,
    IReadOnlyList<CodexWorkWindow> WorkWindows)
{
    public static CodexReadResult Empty { get; } = new(null, CodexUsage.Empty, TokenTotals.Empty, Array.Empty<ProjectUsage>(), Array.Empty<CodexWorkWindow>());
}
