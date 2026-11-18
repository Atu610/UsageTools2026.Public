<?php
declare(strict_types=1);
require_once __DIR__ . '/reset-news-translation.php';

// Public third-party announcements, never account quota data or private tokens.
function cleanResetNews(array $data, int $now): array {
    if (!isset($data['data']) || !is_array($data['data']) || count($data['data']) > 100)
        throw new RuntimeException('Invalid reset feed');
    $items = [];
    foreach ($data['data'] as $item) {
        $source = $item['source'] ?? [];
        if (($source['type'] ?? '') !== 'x_post') continue;
        $id = $item['id'] ?? '';
        $at = $item['announced_at'] ?? '';
        $text = $item['text'] ?? '';
        if (!is_string($id) || !preg_match('/^[0-9]{10,24}$/D', $id) ||
            ($source['author'] ?? '') !== 'thsottiaux' ||
            ($source['url'] ?? '') !== 'https://x.com/thsottiaux/status/' . $id ||
            !is_string($text) || strlen($text) > 16000 ||
            !is_string($at) || strlen($at) > 40 || !preg_match('/^\d{4}-\d{2}-\d{2}T/', $at) || strtotime($at) === false ||
            !in_array($item['reset_type'] ?? '', ['regular', 'banked', 'scheduled', 'watch'], true))
            throw new RuntimeException('Invalid reset announcement');
        $time = strtotime($at);
        if ($time > $now + 60 || $time < $now - 7 * 86400) continue;
        $items[$id] = ['id' => $id, 'at' => gmdate('c', $time), 'kind' => $item['reset_type'],
            'text' => $text, 'url' => $source['url']];
    }
    usort($items, function ($a, $b) { return strcmp($b['at'], $a['at']); });
    return array_slice($items, 0, 30);
}

function resetNewsStatus(): ?array {
    if (!function_exists('curl_init')) return null;
    $raw = ''; $curl = curl_init('https://codex-resets.com/api/v1/status');
    curl_setopt_array($curl, [CURLOPT_FOLLOWLOCATION=>false, CURLOPT_CONNECTTIMEOUT=>1, CURLOPT_TIMEOUT=>2,
        CURLOPT_SSL_VERIFYPEER=>true, CURLOPT_SSL_VERIFYHOST=>2, CURLOPT_PROTOCOLS=>CURLPROTO_HTTPS,
        CURLOPT_USERAGENT=>'UsageTools-ResetNews/1.0', CURLOPT_WRITEFUNCTION=>function($c,$chunk)use(&$raw){
            if(strlen($raw)+strlen($chunk)>128000)return 0;$raw.=$chunk;return strlen($chunk);
        }]);
    $ok=curl_exec($curl);$status=curl_getinfo($curl,CURLINFO_RESPONSE_CODE);curl_close($curl);
    $value=json_decode($raw,true);
    return $ok!==false && $status===200 && is_array($value['data']??null) ? $value['data'] : null;
}

function resetStatusPosts(array $status, int $now): array {
    $items=[];
    $scheduled=$status['scheduled_reset']??null;
    if(is_array($scheduled) && ($scheduled['status']??'')==='scheduled'){
        $scheduled['reset_type']='scheduled';$items[]=$scheduled;
    }
    $watch=$status['active_watch']??null;
    if(is_array($watch) && strtotime($watch['expires_at']??'')>$now &&
        preg_match('~^https://x\.com/thsottiaux/status/([0-9]{10,24})$~D',$watch['source']['url']??'',$parts)){
        $watch['id']=$parts[1];$watch['reset_type']='watch';$watch['announced_at']=$watch['observed_at']??'';$items[]=$watch;
    }
    return cleanResetNews(['data'=>$items],$now);
}

function readResetNewsCache(string $path): array {
    $raw = @file_get_contents($path, false, null, 0, 524289);
    $prefix = "<?php exit; ?>\n";
    if ($raw === false || strlen($raw) > 524288 || substr($raw, 0, strlen($prefix)) !== $prefix) return [];
    return json_decode(substr($raw, strlen($prefix)), true) ?: [];
}

function resetNewsFeed(string $root): array {
    $deadline = microtime(true) + 9;
    $now = time(); $path = $root . '/reset-news-cache.php';
    $cache = readResetNewsCache($path);
    if (($cache['nextAttempt'] ?? 0) <= $now) {
        // Shared across tenants in this service; nonblocking lock prevents a refresh stampede.
        $lock = @fopen($root . '/reset-news-lock.php', 'c');
        if ($lock && flock($lock, LOCK_EX | LOCK_NB)) {
            try {
                $cache = readResetNewsCache($path);
                $previousItems = $cache['items'] ?? [];
                if (($cache['nextAttempt'] ?? 0) <= $now) {
                    $cache['nextAttempt'] = $now + 300;
                    $cache['failed'] = true;
                    if (function_exists('curl_init')) {
                        $raw = ''; $etag = ''; $retry = 300;
                        $curl = curl_init('https://codex-resets.com/api/v1/resets?limit=30');
                        $headers = ['Accept: application/json'];
                        if (is_string($cache['etag'] ?? null) && !preg_match('/[\r\n]/', $cache['etag']))
                            $headers[] = 'If-None-Match: ' . $cache['etag'];
                        curl_setopt_array($curl, [CURLOPT_FOLLOWLOCATION => false, CURLOPT_CONNECTTIMEOUT => 3,
                            CURLOPT_TIMEOUT => 4, CURLOPT_HTTPHEADER => $headers,
                            CURLOPT_USERAGENT => 'UsageTools-ResetNews/1.0', CURLOPT_SSL_VERIFYPEER => true,
                            CURLOPT_SSL_VERIFYHOST => 2, CURLOPT_PROTOCOLS => CURLPROTO_HTTPS,
                            CURLOPT_WRITEFUNCTION => function ($c, $chunk) use (&$raw) {
                                if (strlen($raw) + strlen($chunk) > 524288) return 0;
                                $raw .= $chunk; return strlen($chunk);
                            }, CURLOPT_HEADERFUNCTION => function ($c, $line) use (&$etag, &$retry) {
                                if (stripos($line, 'ETag:') === 0) $etag = substr(trim(substr($line, 5)), 0, 200);
                                if (stripos($line, 'Retry-After:') === 0) $retry = max(300, min(3600, (int)trim(substr($line, 12))));
                                return strlen($line);
                            }]);
                        $ok = curl_exec($curl); $status = curl_getinfo($curl, CURLINFO_RESPONSE_CODE); curl_close($curl);
                        if ($ok !== false && $status === 200) {
                            $decoded = json_decode($raw, true);
                            if (!is_array($decoded)) throw new RuntimeException('Invalid JSON');
                            $cache['items'] = cleanResetNews($decoded, $now);
                            $cache['fetchedAt'] = gmdate('c', $now); $cache['etag'] = $etag; $cache['failed'] = false;
                        } elseif ($ok !== false && $status === 304 && isset($cache['fetchedAt'])) {
                            $cache['fetchedAt'] = gmdate('c', $now); $cache['failed'] = false;
                        } elseif ($status === 429) $cache['nextAttempt'] = $now + $retry;
                    }
                }
            } catch (Throwable $error) { $cache['failed'] = true; }
            $status = resetNewsStatus(); $cache['partial'] = $status === null;
            if ($status !== null) {
                try {
                    $extra = resetStatusPosts($status, $now); $byId = [];
                    foreach ($extra as $item) $byId[$item['id']] = $item;
                    foreach ($cache['items'] ?? [] as $item) if (!in_array($item['kind'], ['scheduled','watch'], true)) $byId[$item['id']] = $item;
                    $cache['items'] = array_values($byId);
                    usort($cache['items'], function($a,$b){return strcmp($b['at'],$a['at']);});
                    $cache['items'] = array_slice($cache['items'],0,30);
                } catch (Throwable $error) { $cache['partial'] = true; }
            }
            $retryAt = (int)($cache['translationRetryAt'] ?? 0);
            $cache['items'] = translateNewsItems($cache['items'] ?? [], $previousItems ?? [], $deadline, $retryAt);
            $cache['translationRetryAt'] = $retryAt;
            $cache['items'] = array_values(array_filter($cache['items'] ?? [], function ($item) use ($now) {
                return strtotime($item['at'] ?? '') >= $now - 7 * 86400;
            }));
            $tmp = $root . '/.reset-news-' . bin2hex(random_bytes(8)) . '.php';
            if (@file_put_contents($tmp, "<?php exit; ?>\n" . json_encode($cache, JSON_UNESCAPED_UNICODE)) !== false) {
                @chmod($tmp, 0600); if (!@rename($tmp, $path)) @unlink($tmp);
            }
            flock($lock, LOCK_UN);
        }
        if ($lock) fclose($lock);
    }
    $items = array_values(array_filter($cache['items'] ?? [], function ($item) use ($now) {
        return strtotime($item['at'] ?? '') >= $now - 7 * 86400;
    }));
    return ['items' => $items, 'fetchedAt' => $cache['fetchedAt'] ?? null,
        'stale' => ($cache['failed'] ?? true) || strtotime($cache['fetchedAt'] ?? '') < $now - 900,
        'partial' => $cache['partial'] ?? false, 'source' => 'https://codex-resets.com/zh-TW'];
}
