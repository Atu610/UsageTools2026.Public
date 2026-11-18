const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
const base=process.argv[2],out=path.resolve(process.argv[3]);fs.mkdirSync(out,{recursive:true});
const results=[];let browser;const pass=name=>{results.push('PASS '+name);console.log(results.at(-1));};
(async()=>{
 browser=await chromium.launch({channel:'chrome',headless:true});const page=await browser.newPage({viewport:{width:390,height:844},serviceWorkers:'block'}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base+'/#key='+'a'.repeat(64));await page.locator('.provider').first().waitFor();
 await page.evaluate(async()=>{await fetch('/fixture?scenario=hidden&theme=deep-night',{method:'POST'});await load(true);});
 assert.equal(await page.locator('.provider').count(),1);assert.equal(await page.locator('.window').count(),1);pass('disabled_claude_hidden_and_weekly_only');
 assert.equal(await page.locator('#recent .energy-track').count(),0);assert.equal(await page.locator('#recent .event-row').count(),2);
 assert.ok(await page.locator('#recent .event-row').evaluateAll(rows=>rows.every(row=>row.getBoundingClientRect().height<105)));pass('recent_compact_rows');
 await page.screenshot({path:path.join(out,'01-usage-compact.png'),fullPage:true});
 await page.getByRole('button',{name:'通知',exact:true}).click();assert.equal(await page.locator('#history .energy-track').count(),0);pass('notification_compact_rows');
 await page.screenshot({path:path.join(out,'02-notifications-compact.png'),fullPage:true});
 await page.locator('#history .ResetCreditsAdded').click();assert.equal(await page.locator('#eventDialog .credit-expirations time').count(),3);await page.screenshot({path:path.join(out,'03-credit-detail.png')});await page.getByRole('button',{name:'關閉訊息'}).click();await page.locator('#eventDialog').waitFor({state:'hidden'});pass('each_credit_expiry_in_dialog');
 await page.locator('#history .LowRemaining').click();assert.equal(await page.locator('#eventDialog .energy-track').count(),1);await page.screenshot({path:path.join(out,'04-quota-detail.png')});await page.keyboard.press('Escape');await page.locator('#eventDialog').waitFor({state:'hidden'});pass('full_quota_detail_retained');
 await page.getByRole('button',{name:'重置消息',exact:true}).click();await page.locator('.news-row').first().waitFor();assert.equal(await page.locator('.news-row').count(),3);assert.equal(await page.getByRole('switch',{name:'Tibo 新消息推播'}).isChecked(),false);pass('fourth_tab_and_separate_opt_in');
 assert.equal(await page.locator('.news-row .news-icon svg').count(),3);assert.ok((await page.locator('.news-row h3').first().textContent()).includes('用量重置'));pass('chinese_first_with_distinct_event_icons');
 await page.screenshot({path:path.join(out,'05-tibo-news.png'),fullPage:true});await page.locator('.news-row').first().click();assert.ok((await page.locator('#eventDialog').textContent()).includes('官方帳戶資料為準'));await page.screenshot({path:path.join(out,'06-tibo-detail.png')});await page.goBack();await page.locator('#eventDialog').waitFor({state:'hidden'});pass('news_detail_disclaimer_back_close');
 for(const width of [320,360,390,430,1024]){await page.setViewportSize({width,height:844});for(const tab of ['用量','通知','重置消息']){await page.getByRole('button',{name:tab,exact:true}).click();assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));}pass('no_overflow_'+width);}
 await page.setViewportSize({width:390,height:844});await page.evaluate(async()=>{appearance.follow=false;appearance.theme='newspaper';applyTheme();});await page.getByRole('button',{name:'通知',exact:true}).click();await page.screenshot({path:path.join(out,'07-newspaper-compact.png'),fullPage:true});
 await page.route('**/api.php?op=reset-news',route=>route.fulfill({json:{items:[{id:'1111111111111111111',at:new Date().toISOString(),kind:'regular',text:'<img src=x onerror=alert(1)>',url:'https://x.com/thsottiaux/status/1111111111111111111'},{id:'2222222222222222222',at:new Date().toISOString(),kind:'regular',text:'bad link',url:'javascript:alert(1)'}],stale:false,fetchedAt:new Date().toISOString()}}));
 await page.evaluate(async()=>{selectView('news');await loadResetNews(true);});assert.equal(await page.locator('.news-row').count(),1);assert.equal(await page.locator('.news-row img').count(),0);await page.locator('.news-row').click();assert.equal(await page.locator('#eventDialog blockquote img').count(),0);pass('untrusted_feed_text_and_links_safe');await page.keyboard.press('Escape');await page.locator('#eventDialog').waitFor({state:'hidden'});
 await page.route('**/api.php?op=reset-news',route=>route.fulfill({status:503,json:{error:'down'}}));await page.evaluate(()=>loadResetNews(true));assert.equal(await page.locator('.news-row').count(),1);assert.ok((await page.locator('#newsState').textContent()).includes('保留'));pass('feed_failure_preserves_existing_list');
 await page.evaluate(()=>{current=null;paired=false;clearHistory();});assert.equal(await page.locator('.news-row').count(),0);pass('account_clear_removes_news_view_state');
 assert.deepEqual(errors,[]);pass('no_browser_errors');
})().catch(e=>{results.push('FAIL '+e.stack);console.error(e);process.exitCode=1;}).finally(async()=>{if(browser)await browser.close();fs.writeFileSync(path.join(out,'ui-tests.txt'),results.join('\n')+'\n');});
