<?php
header('Content-Type: application/manifest+json; charset=utf-8');
header('Cache-Control: no-cache');
header('X-Content-Type-Options: nosniff');
$manifest = json_decode(file_get_contents(__DIR__ . '/manifest.webmanifest'), true);
$manifest['id'] = rtrim(dirname($_SERVER['SCRIPT_NAME']), '/') . '/';
echo json_encode($manifest, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
