<?php
declare(strict_types=1);
if (!defined('PREFIX')) { http_response_code(404); exit; }

function registerTenant(array $server, string $writerHash): void {
    global $documentRoot;
    if (($server['registrationEnabled'] ?? false) !== true) reply(503, ['error' => 'registration_closed']);
    $input = body();
    $readHash = $input['readHash'] ?? '';
    $publicKey = $input['publicKey'] ?? '';
    if (!is_string($readHash) || !preg_match('/^[a-f0-9]{64}$/D', $readHash) || hash_equals($readHash, $writerHash) ||
        !is_string($publicKey) || !preg_match('/^[A-Za-z0-9_-]{87}$/D', $publicKey)) reply(400, ['error' => 'invalid_registration']);
    $decoded = base64_decode(strtr($publicKey, '-_', '+/') . '=', true);
    if ($decoded === false || strlen($decoded) !== 65 || ord($decoded[0]) !== 4) reply(400, ['error' => 'invalid_public_key']);
    $lock = fopen(__DIR__ . '/registration-lock.php', 'c');
    if (!$lock || !flock($lock, LOCK_EX)) reply(503, ['error' => 'busy']);
    $accounts = __DIR__ . '/accounts';
    if (is_link($accounts)) reply(503, ['error' => 'storage_unavailable']);
    if (!is_dir($accounts) && !mkdir($accounts, 0700)) reply(503, ['error' => 'storage_unavailable']);
    $root = $accounts . '/' . $writerHash;
    if (is_link($root)) reply(503, ['error' => 'storage_unavailable']);
    if (is_dir($root)) {
        $documentRoot = $root;
        $existing = readDoc('config');
        if ($existing) {
            if (!hash_equals($existing['readHash'], $readHash) || !hash_equals($existing['publicKey'], $publicKey) ||
                !hash_equals($existing['writeHash'], $writerHash)) reply(409, ['error' => 'registration_conflict']);
            reply(200, ['account' => $writerHash]);
        }
    }
    $documentRoot = __DIR__;
    $rate = readDoc('registration-rate');
    $hour = gmdate('Y-m-d-H');
    $count = ($rate['hour'] ?? '') === $hour ? (int)($rate['count'] ?? 0) : 0;
    $limit = max(1, min(1000, (int)($server['maxRegistrationsPerHour'] ?? 100)));
    if ($count >= $limit) reply(429, ['error' => 'registration_rate_limited']);
    $total = count(glob($accounts . '/*', GLOB_ONLYDIR) ?: []);
    if (!is_dir($root) && $total >= max(1, min(10000, (int)($server['maxAccounts'] ?? 1000)))) reply(503, ['error' => 'account_capacity']);
    writeDoc('registration-rate', ['hour' => $hour, 'count' => $count + 1]);
    if (!is_dir($root) && !mkdir($root, 0700)) reply(503, ['error' => 'storage_unavailable']);
    $documentRoot = $root;
    writeDoc('config', ['writeHash' => $writerHash, 'readHash' => $readHash, 'publicKey' => $publicKey,
        'fixture' => ($server['fixture'] ?? false) === true, 'createdAt' => gmdate('c')]);
    flock($lock, LOCK_UN); fclose($lock);
    reply(201, ['account' => $writerHash]);
}
