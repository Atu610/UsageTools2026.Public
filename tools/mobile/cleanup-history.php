<?php
declare(strict_types=1);
// CLI only. No credentials or per-account details are printed or archived.
if (PHP_SAPI !== 'cli') { http_response_code(404); exit; }
const PREFIX = "<?php exit; ?>\n";
require __DIR__ . '/../../Mobile/notification-history.php';
$options = getopt('', ['root:', 'apply']);
$roots = $options['root'] ?? [];
if (!is_array($roots)) $roots = [$roots];
if (!$roots) { fwrite(STDERR, "Usage: php cleanup-history.php --root=/service/mobile --root=/service/personal [--apply]\n"); exit(2); }
$apply = array_key_exists('apply', $options);
$result = ['apply' => $apply, 'files' => 0, 'removed' => 0, 'errors' => 0];
foreach ($roots as $root) {
    $base = realpath($root);
    if (!$base || !is_dir($base) || is_link($root)) { $result['errors']++; continue; }
    $dirs = [$base];
    if (is_dir($base . '/accounts') && !is_link($base . '/accounts')) {
        foreach (new DirectoryIterator($base . '/accounts') as $entry) {
            if ($entry->isDir() && !$entry->isLink() && preg_match('/^[a-f0-9]{64}$/D', $entry->getFilename()))
                $dirs[] = $entry->getPathname();
        }
    }
    foreach ($dirs as $dir) {
        $file = $dir . '/usage.php';
        if (!is_file($file)) continue;
        if (is_link($file) || is_link($dir . '/usage-lock.php')) { $result['errors']++; continue; }
        $lock = fopen($dir . '/usage-lock.php', 'c');
        if (!$lock || !flock($lock, LOCK_EX)) { if ($lock) fclose($lock); $result['errors']++; continue; }
        $temp = null;
        try {
            $raw = file_get_contents($file);
            if (substr($raw, 0, strlen(PREFIX)) !== PREFIX) throw new RuntimeException('invalid_document');
            $doc = json_decode(substr($raw, strlen(PREFIX)), true, 512, JSON_THROW_ON_ERROR);
            if (!is_array($doc) || !is_array($doc['notifications'] ?? [])) throw new RuntimeException('invalid_document');
            $old = $doc['notifications'] ?? [];
            $doc['notifications'] = mergeNotifications($old, [], time());
            $removed = count($old) - count($doc['notifications']);
            if ($apply && $removed > 0) {
                $temp = $dir . '/.mobile-' . bin2hex(random_bytes(16)) . '.php';
                if (file_put_contents($temp, PREFIX . json_encode($doc, JSON_UNESCAPED_UNICODE | JSON_THROW_ON_ERROR)) === false)
                    throw new RuntimeException('write_failed');
                chmod($temp, 0600);
                if (!rename($temp, $file)) throw new RuntimeException('replace_failed');
                $temp = null;
            }
            $result['files']++; $result['removed'] += $removed;
        } catch (Throwable $error) { $result['errors']++; }
        finally { if ($temp !== null) @unlink($temp); flock($lock, LOCK_UN); fclose($lock); }
    }
}
echo json_encode($result) . "\n";
exit($result['errors'] ? 1 : 0);
