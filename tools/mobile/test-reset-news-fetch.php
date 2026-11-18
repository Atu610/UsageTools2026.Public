<?php
declare(strict_types=1);
require __DIR__ . '/../../Mobile/reset-news.php';
if (!isset($argv[1])) throw new RuntimeException('Evidence path required');
$root = sys_get_temp_dir() . '/reset-news-live-' . bin2hex(random_bytes(8));
mkdir($root);
try {
    $feed = resetNewsFeed($root);
    file_put_contents($argv[1], json_encode($feed, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES));
    if ($feed['stale'] || !$feed['fetchedAt']) throw new RuntimeException('Live HTTPS collector unavailable');
    $before = file_get_contents($root . '/reset-news-cache.php');
    $second = resetNewsFeed($root);
    if ($second !== $feed || file_get_contents($root . '/reset-news-cache.php') !== $before)
        throw new RuntimeException('Cache did not prevent second refresh');
    echo 'PASS live_https_collector records=' . count($feed['items']) . "\nPASS cached_second_request\n";
} finally {
    foreach (['reset-news-cache.php', 'reset-news-lock.php'] as $name)
        if (is_file($root . '/' . $name)) unlink($root . '/' . $name);
    rmdir($root);
}
