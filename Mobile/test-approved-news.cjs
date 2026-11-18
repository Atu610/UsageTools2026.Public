const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
const base=process.argv[2],out=path.resolve(process.argv[3]);fs.mkdirSync(out,{recursive:true});
let browser;const results=[],pass=s=>{results.push('PASS '+s);console.log(results.at(-1));};
(async()=>{
 browser=await chromium.launch({channel:'chrome',headless:true});
 const page=await browser.newPage({viewport:{width:1200,height:1080},serviceWorkers:'block'}),errors=[];
 page.on('pageerror',e=>errors.push(e.message));await page.goto(base+'/preview.html');
 const frame=page.frameLocator('#phone');
 await frame.locator('.provider').first().waitFor();
 await frame.getByRole('button',{name:'重置消息',exact:true}).click();
 await frame.locator('.news-row').first().waitFor();
 assert.match(await frame.locator('#newsLatest').textContent(),/帳戶狀態待確認/);pass('latest_announcement_not_account_reset');
 await page.locator('#newsPreviewKind').selectOption('banked');await page.locator('#newsPreview').click();
 await frame.locator('#eventDialog[open]').waitFor();assert.match(await frame.locator('.news-proof').textContent(),/不代表你的帳戶已收到券/);pass('push_preview_opens_matching_credit_announcement');
 await frame.getByRole('button',{name:'重播頁內效果'}).click();
 assert.match(await frame.locator('.news-detail>.news-icon').evaluate(el=>getComputedStyle(el).animationName),/detail-symbol/);pass('detail_replay_animation');
 await page.screenshot({path:path.join(out,'preview-push-detail.png'),fullPage:true});
 await frame.getByRole('button',{name:'關閉訊息'}).click();await frame.locator('#eventDialog').waitFor({state:'hidden'});pass('preview_close_returns_to_list');
 await page.goto(base+'/#key='+'a'.repeat(64));await page.getByRole('button',{name:'重置消息',exact:true}).click();await page.locator('.news-row').first().waitFor();
 for(const width of [320,390,430]){
  await page.setViewportSize({width,height:844});
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
  await page.locator('.news-row').first().click();await page.locator('.news-original summary').click();
  assert.ok(await page.locator('#eventDialogClose').isVisible());await page.getByRole('button',{name:'關閉訊息'}).click();await page.locator('#eventDialog').waitFor({state:'hidden'});pass('news_list_and_detail_fit_'+width);
 }
 await page.setViewportSize({width:390,height:844});await page.screenshot({path:path.join(out,'news-final.png'),fullPage:true});
 await page.locator('.news-row').nth(1).click();await page.screenshot({path:path.join(out,'credit-announcement-final.png')});
 await page.emulateMedia({reducedMotion:'reduce'});await page.getByRole('button',{name:'重播頁內效果'}).click();assert.equal(await page.locator('.news-detail>.news-icon').evaluate(el=>getComputedStyle(el).animationName),'none');pass('news_motion_respects_accessibility');
 await page.keyboard.press('Escape');await page.locator('#eventDialog').waitFor({state:'hidden'});
 const fixture={items:[{id:'1111111111111111111',kind:'scheduled',at:new Date().toISOString(),text:'Schedule only',url:'https://x.com/thsottiaux/status/1111111111111111111'}],fetchedAt:new Date().toISOString(),stale:false};
 await page.route('**/api.php?op=reset-news',route=>route.fulfill({json:fixture}));await page.evaluate(()=>loadResetNews(true));assert.equal(await page.locator('#newsLatest').isHidden(),true);pass('schedule_not_latest_completed_reset');
 await page.locator('.news-row').click();assert.match(await page.locator('.news-proof').textContent(),/尚未完成/);assert.match(await page.locator('#eventDialog').textContent(),/翻譯暫不可用/);pass('translation_failure_honest_and_schedule_pending');
 await page.keyboard.press('Escape');await page.locator('#eventDialog').waitFor({state:'hidden'});
 fixture.items[0].kind='watch';await page.evaluate(()=>loadResetNews(true));await page.locator('.news-row').click();assert.match(await page.locator('.news-proof').textContent(),/不是已完成重置/);pass('discussion_not_reset_confirmation');
 await page.keyboard.press('Escape');await page.locator('#eventDialog').waitFor({state:'hidden'});await page.evaluate(()=>resetNewsState());assert.equal(await page.locator('#newsLatest').isHidden(),true);assert.equal(await page.locator('#newsLatest').textContent(),'');pass('identity_clear_removes_latest_summary');
 assert.deepEqual(errors,[]);pass('no_browser_errors');
})().catch(e=>{results.push('FAIL '+e.stack);console.error(e);process.exitCode=1;}).finally(async()=>{if(browser)await browser.close();fs.writeFileSync(path.join(out,'approved-news-tests.txt'),results.join('\n')+'\n');});
