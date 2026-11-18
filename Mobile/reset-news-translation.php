<?php
declare(strict_types=1);

function parseNewsTranslation(string $raw): ?string {
    if (strlen($raw) > 128000) return null;
    $data = json_decode($raw, true);
    if ((int)($data['responseStatus'] ?? 0) !== 200 || ($data['quotaFinished'] ?? false) === true ||
        !is_string($data['responseData']['translatedText'] ?? null)) return null;
    $text = trim(html_entity_decode($data['responseData']['translatedText'], ENT_QUOTES | ENT_HTML5, 'UTF-8'));
    return $text !== '' && strlen($text) <= 16000 && preg_match('/[\x{3400}-\x{9FFF}]/u', $text) ? $text : null;
}

function translatePublicNews(string $text, int $timeoutMs, int &$retryAt): ?string {
    if (!function_exists('curl_init') || strlen($text) > 500 || $timeoutMs < 300) return null;
    // Documented anonymous MyMemory GET API, en -> Traditional Chinese, max 500 UTF-8 bytes.
    // No quota, pairing token, tenant ID or push endpoint is sent to this service.
    $curl = curl_init('https://api.mymemory.translated.net/get?' . http_build_query(['q'=>$text,'langpair'=>'en|zh-TW']));
    $raw = ''; $retrySeconds = 300;
    curl_setopt_array($curl, [CURLOPT_FOLLOWLOCATION => false, CURLOPT_TIMEOUT_MS => $timeoutMs, CURLOPT_CONNECTTIMEOUT_MS => min(1000, $timeoutMs),
        CURLOPT_SSL_VERIFYPEER => true, CURLOPT_SSL_VERIFYHOST => 2, CURLOPT_PROTOCOLS => CURLPROTO_HTTPS,
        CURLOPT_USERAGENT => 'UsageTools-PublicNewsTranslation/1.0',
        CURLOPT_HEADERFUNCTION => function($c,$line)use(&$retrySeconds){if(stripos($line,'Retry-After:')===0){$value=trim(substr($line,12));$seconds=ctype_digit($value)?(int)$value:((strtotime($value)?:time()+300)-time());$retrySeconds=max(300,min(86400,$seconds));}return strlen($line);},
        CURLOPT_WRITEFUNCTION => function ($c, $chunk) use (&$raw) { if (strlen($raw) + strlen($chunk) > 128000) return 0; $raw .= $chunk; return strlen($chunk); }]);
    $ok = curl_exec($curl); $status = curl_getinfo($curl, CURLINFO_RESPONSE_CODE); curl_close($curl);
    $data = json_decode($raw,true);
    if (($data['quotaFinished'] ?? false) === true) $retryAt = time() + 86400;
    if ($status === 429) $retryAt = time() + $retrySeconds;
    return $ok !== false && $status === 200 ? parseNewsTranslation($raw) : null;
}

function translateNewsItems(array $items, array $previous, float $deadline, int &$retryAt): array {
    $known = [];
    foreach ($previous as $item) if (is_string($item['zhText'] ?? null) && ($item['translationStatus'] ?? '') === 'translated')
        $known[hash('sha256', $item['text'])] = $item['zhText'];
    $attempts = 0;
    foreach ($items as &$item) {
        $key = hash('sha256', $item['text']);
        $translated = $known[$key] ?? null;
        if ($translated === null && $attempts < 3 && time() >= $retryAt && microtime(true) < $deadline - .3) {
            $attempts++;
            $translated = translatePublicNews($item['text'], (int)min(2000, ($deadline - microtime(true)) * 1000), $retryAt);
        }
        $item['zhText'] = $translated;
        $item['translationStatus'] = $translated === null ? 'unavailable' : 'translated';
    }
    unset($item);
    return $items;
}
