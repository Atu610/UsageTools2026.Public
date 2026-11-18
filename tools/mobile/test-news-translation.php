<?php
require __DIR__ . '/../../Mobile/reset-news-translation.php';
$uri='https://api.mymemory.translated.net/get?'.http_build_query(['q'=>'A new reset is available.','langpair'=>'en|zh-TW']);
$curl=curl_init($uri);
curl_setopt_array($curl,[CURLOPT_RETURNTRANSFER=>true,CURLOPT_TIMEOUT=>5,CURLOPT_USERAGENT=>'UsageTools-PublicNewsTranslation/1.0']);
$raw=curl_exec($curl);$result=['http'=>curl_getinfo($curl,CURLINFO_RESPONSE_CODE),'error'=>curl_error($curl),'seconds'=>curl_getinfo($curl,CURLINFO_TOTAL_TIME),'translated'=>is_string($raw)?parseNewsTranslation($raw):null];
curl_close($curl);echo json_encode($result,JSON_UNESCAPED_UNICODE|JSON_PRETTY_PRINT)."\n";
if(!$result['translated'])exit(1);
