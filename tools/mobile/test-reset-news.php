<?php
declare(strict_types=1);
require __DIR__ . '/../../Mobile/reset-news.php';
$now = time(); $count = 0;
function check(string $name, bool $value): void { global $count; if (!$value) throw new RuntimeException('FAIL ' . $name); $count++; echo 'PASS ' . $name . "\n"; }
$item = ['id'=>'1111111111111111111','reset_type'=>'regular','announced_at'=>gmdate('c',$now),'text'=>'<script>fixture</script>',
    'source'=>['type'=>'x_post','author'=>'thsottiaux','url'=>'https://x.com/thsottiaux/status/1111111111111111111']];
$clean = cleanResetNews(['data'=>[$item,$item]],$now);
check('source_dedup_and_plain_text',count($clean)===1 && $clean[0]['text']===$item['text']);
$observed=$item;$observed['source']=['type'=>'observed'];
check('observed_reset_not_misrepresented_as_tibo_post',count(cleanResetNews(['data'=>[$observed]],$now))===0);
$old=$item;$old['announced_at']=gmdate('c',$now-8*86400);$future=$item;$future['announced_at']=gmdate('c',$now+3600);
check('seven_day_and_future_filter',count(cleanResetNews(['data'=>[$old,$future]],$now))===0);
$planned=$item;$planned['status']='scheduled';$planned['scheduled_for']=gmdate('c',$now-60);
$status=resetStatusPosts(['scheduled_reset'=>$planned],$now);check('past_schedule_never_assumed_completed',$status[0]['kind']==='scheduled');
check('malformed_translation_not_used',parseNewsTranslation('{"text":"bad"}')===null);
check('translation_response_validated',parseNewsTranslation('{"responseStatus":200,"responseData":{"translatedText":"額度已重置"}}')==='額度已重置');
check('translation_daily_limit_not_shown_as_content',parseNewsTranslation('{"responseStatus":200,"quotaFinished":true,"responseData":{"translatedText":"額度已重置"}}')===null);
$prior=$clean;$prior[0]['zhText']='額度已重置';$prior[0]['translationStatus']='translated';$retry=time()+300;
$translated=translateNewsItems($clean,$prior,microtime(true)-1,$retry);check('translation_cache_reused_without_network',$translated[0]['zhText']==='額度已重置');
$changed=$clean;$changed[0]['text']='changed public text';$translated=translateNewsItems($changed,$prior,microtime(true)-1,$retry);check('source_edit_invalidates_translation',$translated[0]['zhText']===null);
foreach(['author','url'] as $field){$bad=$item;$bad['source'][$field]='evil';$rejected=false;try{cleanResetNews(['data'=>[$bad]],$now);}catch(Throwable $e){$rejected=true;}check('reject_bad_'.$field,$rejected);}
$folder=sys_get_temp_dir().'/reset-news-test-'.bin2hex(random_bytes(8));mkdir($folder);
$cacheFile=$folder.'/reset-news-cache.php';
try{
 file_put_contents($cacheFile,"<?php exit; ?>\n".json_encode(['items'=>$clean,'fetchedAt'=>gmdate('c',$now),'nextAttempt'=>$now+300,'failed'=>false]));
 $feed=resetNewsFeed($folder);check('shared_cache_read_no_upstream',count($feed['items'])===1 && !$feed['stale']);
 file_put_contents($cacheFile,"<?php exit; ?>\n".json_encode(['items'=>$clean,'fetchedAt'=>gmdate('c',$now-3600),'nextAttempt'=>$now+300,'failed'=>true]));
 $feed=resetNewsFeed($folder);check('failed_source_retains_marked_cache',count($feed['items'])===1 && $feed['stale']);
 check('response_excludes_internal_etag_and_retry',!isset($feed['nextAttempt']) && !isset($feed['etag']));
 if(isset($argv[1])){$live=json_decode(file_get_contents($argv[1]),true);$items=cleanResetNews($live,$now);check('real_public_api_shape_accepted',is_array($items));echo 'LIVE_PUBLIC_RECORDS='.count($items)."\n";}
 echo 'ALL PASS checks='.$count."\n";
}finally{unlink($cacheFile);rmdir($folder);}
