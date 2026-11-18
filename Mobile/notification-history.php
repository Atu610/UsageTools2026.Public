<?php
declare(strict_types=1);
if (!defined('PREFIX')) { http_response_code(404); exit; }
const PLAN_WHITELIST = [null,'Free','Go','Plus','Pro','Pro US$100/mo','Pro US$200/mo','Pro US$500/mo','Pro 5x','Pro 10x','Pro 20x','Pro 25x','Max','Max 5x','Max 20x','Ultra','Team','Business','Enterprise','Edu'];
const CREDIT_KINDS = [null,'full','weekly','five_hour','fable','other'];

function safeName($value): ?string {
    return is_string($value) && preg_match('/^[\p{L}\p{N} ._\-]{1,40}$/uD', $value) ? $value : null;
}

function optionalNumber($value, float $min, float $max): ?float {
    if ($value === null) return null;
    if ((!is_int($value) && !is_float($value)) || !is_finite((float)$value) || $value < $min || $value > $max)
        throw new InvalidArgumentException('number');
    return (float)$value;
}

function optionalInt($value, int $max): ?int {
    if ($value === null) return null;
    if (!is_int($value) || $value < 0 || $value > $max) throw new InvalidArgumentException('int');
    return $value;
}

function optionalDate($value): ?string {
    if ($value === null) return null;
    if (!is_string($value) || strlen($value) > 40 || !preg_match('/^\d{4}-\d{2}-\d{2}T/', $value) || ($at = strtotime($value)) === false)
        throw new InvalidArgumentException('date');
    return gmdate('c', $at);
}

// Optional Claude contract (Reports/CLAUDE_DATA_CONTRACT.md): whitelisted quota facts only.
function cleanClaudeDetails($value): ?array {
    if ($value === null) return null;
    if (!is_array($value)) throw new InvalidArgumentException('claude_details');
    $pools = [];
    foreach (($value['pools'] ?? []) as $pool) {
        if (count($pools) >= 8) break;
        if (!is_array($pool) || !in_array($pool['label'] ?? null, ['5H', '7D'], true)) throw new InvalidArgumentException('claude_pool');
        $pools[] = ['label' => $pool['label'], 'model' => safeName($pool['model'] ?? null),
            'used' => optionalNumber($pool['used'] ?? null, 0, 100), 'resetsAt' => optionalDate($pool['resetsAt'] ?? null),
            'allowance' => optionalNumber($pool['allowance'] ?? null, 0, 100)];
    }
    $resets = null;
    if (isset($value['resets'])) {
        $r = $value['resets'];
        if (!is_array($r) || (isset($r['eligible']) && !is_bool($r['eligible']))) throw new InvalidArgumentException('claude_resets');
        $grants = [];
        foreach (($r['grants'] ?? []) as $grant) {
            if (count($grants) >= 20) break;
            if (!is_array($grant) || !in_array($grant['kind'] ?? null, CREDIT_KINDS, true) || !is_bool($grant['usableNow'] ?? false))
                throw new InvalidArgumentException('claude_grant');
            $grants[] = ['kind' => $grant['kind'], 'left' => optionalInt($grant['left'] ?? null, 10000),
                'total' => optionalInt($grant['total'] ?? null, 10000), 'expiresAt' => optionalDate($grant['expiresAt'] ?? null),
                'usableNow' => ($grant['usableNow'] ?? false) === true];
        }
        $resets = ['available' => optionalInt($r['available'] ?? null, 10000), 'eligible' => $r['eligible'] ?? null,
            'syncedAt' => optionalDate($r['syncedAt'] ?? null), 'grants' => $grants];
    }
    $models = null;
    if (isset($value['models'])) {
        $m = $value['models'];
        if (!is_array($m) || !is_array($m['primary'] ?? [])) throw new InvalidArgumentException('claude_models');
        $models = ['count' => optionalInt($m['count'] ?? null, 100), 'syncedAt' => optionalDate($m['syncedAt'] ?? null),
            'primary' => array_values(array_filter(array_map('safeName', array_slice($m['primary'] ?? [], 0, 12))))];
    }
    $extra = null;
    if (isset($value['extraUsage'])) {
        $e = $value['extraUsage'];
        if (!is_array($e) || !is_bool($e['enabled'] ?? null) ||
            (isset($e['currency']) && !preg_match('/^[A-Z]{3}$/D', (string)$e['currency']))) throw new InvalidArgumentException('claude_extra');
        $extra = ['enabled' => $e['enabled'], 'used' => optionalNumber($e['used'] ?? null, 0, 1e9),
            'limit' => optionalNumber($e['limit'] ?? null, 0, 1e9), 'currency' => $e['currency'] ?? null,
            'limitReached' => ($e['limitReached'] ?? false) === true];
    }
    return ['syncedAt' => optionalDate($value['syncedAt'] ?? null), 'pools' => $pools, 'resets' => $resets,
        'models' => $models, 'extraUsage' => $extra];
}

function cleanCreditExpirations($value): array {
    if (!is_array($value) || count($value) > 100) throw new InvalidArgumentException('credit_expirations');
    $dates = [];
    foreach ($value as $date) {
        if (!is_string($date) || strlen($date) > 40 || !preg_match('/^\d{4}-\d{2}-\d{2}T/', $date) ||
            ($at = strtotime($date)) === false) throw new InvalidArgumentException('credit_expiry');
        $dates[] = gmdate('c', $at);
    }
    sort($dates, SORT_STRING);
    return $dates;
}

// Keep only structured quota events; never accept arbitrary notification text or identity data.
function cleanNotifications($value, int $now): array {
    if (!is_array($value) || count($value) > 50) throw new InvalidArgumentException('notifications');
    $events = [];
    foreach ($value as $event) {
        if (!is_array($event) || !is_string($event['id'] ?? null) ||
            !preg_match('/^[a-f0-9]{32}$/D', $event['id']) ||
            !is_string($event['at'] ?? null) || strlen($event['at']) > 40 ||
            !preg_match('/^\d{4}-\d{2}-\d{2}T/', $event['at']) || ($at = strtotime($event['at'])) === false ||
            $at > $now + 60 || !in_array($event['provider'] ?? null, ['codex', 'claude'], true) ||
            !in_array($event['kind'] ?? null, ['UsageMilestone','LowRemaining','UsageReset','ResetCreditsAdded'], true) ||
            !is_string($event['window'] ?? null) ||
            !is_int($event['remaining'] ?? null) || $event['remaining'] < 0 || $event['remaining'] > 100 ||
            !is_int($event['creditCount'] ?? null) || $event['creditCount'] < 0 || $event['creditCount'] > 10000 ||
            !is_int($event['addedCount'] ?? null) || $event['addedCount'] < 0 || $event['addedCount'] > 10000)
            throw new InvalidArgumentException('notification');
        if ($event['kind'] === 'ResetCreditsAdded') {
            if ($event['window'] !== '') throw new InvalidArgumentException('credit_event');
        } elseif (!preg_match('/^\d{1,5}[HDM]$/D', $event['window'])) throw new InvalidArgumentException('window');
        if (!in_array($event['plan'] ?? null, PLAN_WHITELIST, true))
            throw new InvalidArgumentException('event_plan');
        if (isset($event['pool']) && safeName($event['pool']) === null) throw new InvalidArgumentException('event_pool');
        if (!in_array($event['creditKind'] ?? null, CREDIT_KINDS, true)) throw new InvalidArgumentException('event_credit_kind');
        foreach (['resetsAt', 'expiresAt'] as $field) {
            if (isset($event[$field]) && (!is_string($event[$field]) || strlen($event[$field]) > 40 ||
                !preg_match('/^\d{4}-\d{2}-\d{2}T/', $event[$field]) || strtotime($event[$field]) === false))
                throw new InvalidArgumentException('event_date');
        }
        if ($at <= $now - 7 * 86400) continue;
        $events[] = ['id' => $event['id'], 'at' => gmdate('c', $at), 'provider' => $event['provider'],
            'kind' => $event['kind'], 'window' => $event['window'], 'remaining' => $event['remaining'],
            'creditCount' => $event['creditCount'], 'addedCount' => $event['addedCount'],
            'plan' => $event['plan'] ?? null, 'pool' => $event['pool'] ?? null,
            'creditKind' => $event['creditKind'] ?? null, 'resetsAt' => $event['resetsAt'] ?? null,
            'expiresAt' => $event['expiresAt'] ?? null,
            'creditExpirations' => cleanCreditExpirations($event['creditExpirations'] ?? [])];
    }
    return $events;
}
function mergeNotifications(array $old, array $incoming, int $now): array {
    $merged = [];
    foreach (array_merge($old, $incoming) as $event) {
        if (strtotime($event['at']) <= $now - 7 * 86400) continue;
        // Retries cannot rewrite an already accepted event with the same ID.
        if (!isset($merged[$event['id']])) $merged[$event['id']] = $event;
    }
    $result = array_values($merged);
    usort($result, function (array $a, array $b): int { return strcmp($b['at'], $a['at']); });
    return array_slice($result, 0, 100);
}
