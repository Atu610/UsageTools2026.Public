<?php
declare(strict_types=1);
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');
header('Referrer-Policy: no-referrer');
const PREFIX = "<?php exit; ?>\n";
require __DIR__ . '/notification-history.php';
$documentRoot = __DIR__;
set_exception_handler(function (Throwable $error): void { reply(400, ['error' => 'invalid_request']); });
function reply(int $status, array $data): void {
    http_response_code($status); echo json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES); exit;
}
function readDoc(string $name): array {
    global $documentRoot;
    $raw = @file_get_contents($documentRoot . '/' . $name . '.php');
    if ($raw === false || substr($raw, 0, strlen(PREFIX)) !== PREFIX) return [];
    return json_decode(substr($raw, strlen(PREFIX)), true) ?: [];
}
function writeDoc(string $name, array $data): void {
    global $documentRoot;
    $path = $documentRoot . '/' . $name . '.php';
    $tmp = $documentRoot . '/.mobile-' . bin2hex(random_bytes(16)) . '.php';
    if (!$tmp || file_put_contents($tmp, PREFIX . json_encode($data, JSON_UNESCAPED_UNICODE | JSON_THROW_ON_ERROR)) === false)
        reply(503, ['error' => 'storage_unavailable']);
    chmod($tmp, 0600);
    if (!rename($tmp, $path)) { @unlink($tmp); reply(503, ['error' => 'storage_unavailable']); }
}
function body(): array {
    $raw = file_get_contents('php://input', false, null, 0, 16385);
    if (strlen($raw) > 16384) reply(413, ['error' => 'too_large']);
    $value = json_decode($raw, true);
    if (!is_array($value)) reply(400, ['error' => 'invalid_json']);
    return $value;
}
function validDate($value): bool {
    return $value === null || (is_string($value) && strlen($value) < 40 &&
        preg_match('/^\d{4}-\d{2}-\d{2}T/', $value) && strtotime($value) !== false);
}
function allowedEndpoint($value): bool {
    if (!is_string($value) || strlen($value) > 2048) return false;
    $url = parse_url($value);
    if (!$url || ($url['scheme'] ?? '') !== 'https' || isset($url['user']) || isset($url['pass']) || isset($url['port'])) return false;
    $host = $url['host'] ?? '';
    return in_array($host, ['fcm.googleapis.com', 'updates.push.services.mozilla.com', 'web.push.apple.com', 'wns.windows.com'], true)
        || preg_match('/\.(push\.apple\.com|notify\.windows\.com)$/D', $host) === 1;
}
$authorization = $_SERVER['HTTP_AUTHORIZATION'] ?? '';
$method = $_SERVER['REQUEST_METHOD'];
$op = $_GET['op'] ?? 'usage';
$server = readDoc('server-config');
$multi = ($server['mode'] ?? '') === 'multi';
$account = '';
$cookieName = '__Secure-UsageToolsReader';
$cookiePath = rtrim(dirname($_SERVER['SCRIPT_NAME']), '/') . '/';
// TLS terminates upstream; Secure cookies enforce browser HTTPS without trusting forwarded headers.
$cookieAuthentication = $authorization === '';
if ($cookieAuthentication) {
    $token = $_COOKIE[$cookieName] ?? '';
    if ($multi && is_string($token) && preg_match('/^([a-f0-9]{64})\.([a-f0-9]{64})$/D', $token, $parts)) {
        $account = $parts[1]; $token = $parts[2];
    }
    if (!is_string($token) || !preg_match('/^[a-f0-9]{64}$/D', $token)) reply(401, ['error' => 'unauthorized']);
} else {
    if (!preg_match('/^Bearer ([a-f0-9]{64})$/D', $authorization, $match)) reply(401, ['error' => 'unauthorized']);
    $token = $match[1];
    if ($multi) $account = $_SERVER['HTTP_X_USAGE_ACCOUNT'] ?? '';
}
$hash = hash('sha256', $token);
if ($multi) {
    require __DIR__ . '/tenancy.php';
    if ($op === 'register' && $method === 'POST' && !$cookieAuthentication) registerTenant($server, $hash);
    if (!is_string($account) || !preg_match('/^[a-f0-9]{64}$/D', $account)) reply(401, ['error' => 'unauthorized']);
    $documentRoot = __DIR__ . '/accounts/' . $account;
    if (!is_dir($documentRoot) || is_link($documentRoot)) reply(401, ['error' => 'unauthorized']);
}
$config = readDoc('config');
if (!$config) reply($multi ? 401 : 503, ['error' => $multi ? 'unauthorized' : 'not_configured']);
$writer = !$cookieAuthentication && hash_equals($config['writeHash'], $hash);
$reader = hash_equals($config['readHash'], $hash);
if (!$writer && !$reader) reply(401, ['error' => 'unauthorized']);
$csrf = $reader ? hash_hmac('sha256', 'usage-mobile-csrf-v1', $token) : '';
// Cookie pairing is read-only; mutations additionally require same-origin and CSRF proof.
if ($cookieAuthentication && $method !== 'GET') {
    if (($_SERVER['HTTP_ORIGIN'] ?? '') !== 'https://' . $_SERVER['HTTP_HOST'] ||
        !hash_equals($csrf, (string)($_SERVER['HTTP_X_USAGE_CSRF'] ?? ''))) reply(403, ['error' => 'csrf_required']);
}
if ($method === 'DELETE' && $op === 'session' && $reader) {
    setcookie($cookieName, '', ['expires' => time() - 3600, 'path' => $cookiePath,
        'secure' => true, 'httponly' => true, 'samesite' => 'Strict']);
    reply(200, ['ok' => true]);
}
if ($method === 'GET' && $op === 'usage' && $reader) {
    setcookie($cookieName, ($multi ? $account . '.' : '') . $token, ['expires' => time() + 180 * 86400, 'path' => $cookiePath,
        'secure' => true, 'httponly' => true, 'samesite' => 'Strict']);
    $usage = readDoc('usage');
    $usage['notifications'] = mergeNotifications($usage['notifications'] ?? [], [], time());
    if (($config['fixture'] ?? false) === true) $usage['fixture'] = true;
    reply(200, ['usage' => $usage, 'publicKey' => $config['publicKey'], 'account' => $account,
        'notifications' => $usage['notifications'],
        'serverAt' => gmdate('c'), 'session' => true, 'csrf' => $csrf]);
}
if ($method === 'GET' && $op === 'subscriptions' && $writer) reply(200, ['subscriptions' => array_values(readDoc('subscriptions'))]);
if ($method === 'GET' && $op === 'reset-news' && ($reader || $writer)) {
    require __DIR__ . '/reset-news.php';
    reply(200, resetNewsFeed(__DIR__));
}
if ($method !== 'POST' && $method !== 'DELETE') reply(405, ['error' => 'method_not_allowed']);
$input = body();
if ($method === 'POST' && $op === 'usage' && $writer) {
    // Only quota fields are stored; arbitrary prose, paths and account identifiers are excluded.
    if (!in_array($input['schemaVersion'] ?? null, [1,2], true) || !validDate($input['sentAt'] ?? null) ||
        !isset($input['sentAt']) || abs(time() - strtotime($input['sentAt'])) > 300 ||
        count($input['providers'] ?? []) !== 2) reply(400, ['error' => 'invalid_snapshot']);
    $providers = [];
    foreach ($input['providers'] as $index => $p) {
        $expected = $index === 0 ? 'Codex' : 'Claude Code';
        if (($p['name'] ?? '') !== $expected || !is_bool($p['enabled'] ?? null) ||
            !in_array($p['state'] ?? '', ['Fresh','Cached','Stale','Unavailable','Disabled'], true) ||
            !validDate($p['syncedAt'] ?? null) || !is_array($p['windows'] ?? null) || count($p['windows']) > 2 ||
            !in_array($p['plan'] ?? null, PLAN_WHITELIST, true)) reply(400, ['error' => 'invalid_provider']);
        $windows = [];
        $labels = [];
        foreach ($p['windows'] as $w) {
            $used = $w['used'] ?? null;
            if (!preg_match('/^\d{1,5}[HDM]$/D', $w['label'] ?? '') || !validDate($w['resetsAt'] ?? null) ||
                ($used !== null && (!is_numeric($used) || $used < 0 || $used > 100))) reply(400, ['error' => 'invalid_window']);
            if (in_array($w['label'], $labels, true)) reply(400, ['error' => 'duplicate_window']);
            $labels[] = $w['label'];
            $windows[] = ['label' => $w['label'], 'used' => $used === null ? null : (float)$used, 'resetsAt' => $w['resetsAt'] ?? null];
        }
        $providers[] = ['name' => $expected, 'enabled' => $p['enabled'], 'state' => $p['state'],
            'plan' => $p['plan'] ?? null, 'syncedAt' => $p['syncedAt'] ?? null, 'windows' => $windows];
    }
    $themes = ['deep-night','day','classic','aquarium','cloud_observation','black_hole','prism_arcade','matrix','newspaper','win95','world_cup','cartoon_platformer'];
    $theme = in_array($input['themeId'] ?? null, $themes, true) ? $input['themeId'] : 'deep-night';
    $incoming = cleanNotifications($input['notifications'] ?? [], time());
    $credit = $input['resetCredits'] ?? null;
    if ($credit !== null && (!is_array($credit) || !is_bool($credit['stale'] ?? null) ||
        !validDate($credit['syncedAt'] ?? null) || (isset($credit['available']) &&
        (!is_int($credit['available']) || $credit['available'] < 0 || $credit['available'] > 10000)))) reply(400, ['error' => 'invalid_credits']);
    $cleanCredit = $credit === null || !$providers[0]['enabled'] ? null : [
        'available' => $credit['available'] ?? null, 'syncedAt' => $credit['syncedAt'] ?? null, 'stale' => $credit['stale'],
        'expirations' => cleanCreditExpirations($credit['expirations'] ?? [])];
    $cleanClaudeDetails = $providers[1]['enabled'] ? cleanClaudeDetails($input['claudeDetails'] ?? null) : null;
    $lock = fopen($documentRoot . '/usage-lock.php', 'c');
    $balance = $input['creditBalance'] ?? null;
    if ($balance !== null && (!is_array($balance) || !is_bool($balance['unlimited'] ?? null) ||
        !is_bool($balance['stale'] ?? null) || !validDate($balance['syncedAt'] ?? null) ||
        (isset($balance['balance']) && ((!is_int($balance['balance']) && !is_float($balance['balance'])) ||
        !is_finite((float)$balance['balance']) || $balance['balance'] < 0 || $balance['balance'] > 1e12))))
        reply(400, ['error' => 'invalid_balance']);
    $cleanBalance = !$providers[0]['enabled'] || $balance === null ? null : [
        'balance' => $balance['balance'] ?? null, 'unlimited' => $balance['unlimited'],
        'syncedAt' => $balance['syncedAt'] ?? null, 'stale' => $balance['stale']];
    if (!$lock || !flock($lock, LOCK_EX)) reply(503, ['error' => 'busy']);
    $previous = readDoc('usage');
    $notifications = mergeNotifications($previous['notifications'] ?? [], $incoming, time());
    writeDoc('usage', ['schemaVersion' => 2, 'themeId' => $theme, 'sentAt' => $input['sentAt'],
        'receivedAt' => gmdate('c'), 'providers' => $providers, 'resetCredits' => $cleanCredit,
        'creditBalance' => $cleanBalance, 'planConfirmed' => $providers[0]['enabled'] && ($input['planConfirmed'] ?? false) === true,
        'claudeDetails' => $cleanClaudeDetails, 'notifications' => $notifications]);
    flock($lock, LOCK_UN); fclose($lock);
    reply(200, ['ok' => true, 'historyAccepted' => true]);
}
if ($op === 'subscriptions' && (($method === 'POST' && $reader) || ($method === 'DELETE' && ($writer || $reader)))) {
    if (!allowedEndpoint($input['endpoint'] ?? null)) reply(400, ['error' => 'invalid_endpoint']);
    $endpoint = $input['endpoint'];
    $lock = fopen($documentRoot . '/subscriptions-lock.php', 'c');
    if (!$lock || !flock($lock, LOCK_EX)) reply(503, ['error' => 'busy']);
    $subscriptions = readDoc('subscriptions');
    $id = hash('sha256', $endpoint);
    if ($method === 'DELETE') unset($subscriptions[$id]);
    else {
        $keys = $input['keys'] ?? [];
        if (!preg_match('/^[A-Za-z0-9_-]{80,100}$/D', $keys['p256dh'] ?? '') ||
            !preg_match('/^[A-Za-z0-9_-]{20,30}$/D', $keys['auth'] ?? '')) reply(400, ['error' => 'invalid_keys']);
        if (!isset($subscriptions[$id]) && count($subscriptions) >= 8) reply(409, ['error' => 'device_limit']);
        $preferences = [];
        foreach (['codex','claude','low','reset','milestones','images','resetNews'] as $name) $preferences[$name] = ($input['preferences'][$name] ?? ($name === 'images')) === true;
        $newsSince = $preferences['resetNews'] ? (($subscriptions[$id]['preferences']['resetNews'] ?? false) ? ($subscriptions[$id]['newsSince'] ?? gmdate('c')) : gmdate('c')) : null;
        $subscriptions[$id] = ['endpoint' => $endpoint, 'keys' => ['p256dh' => $keys['p256dh'], 'auth' => $keys['auth']], 'preferences' => $preferences, 'newsSince' => $newsSince];
    }
    writeDoc('subscriptions', $subscriptions);
    flock($lock, LOCK_UN); fclose($lock);
    reply(200, ['ok' => true]);
}
reply(403, ['error' => 'forbidden']);
