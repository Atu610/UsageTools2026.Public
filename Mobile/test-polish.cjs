const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),net=require('node:net');
const {spawn}=require('node:child_process');
const {chromium,webkit}=require('playwright');
const {fixture}=require('./preview-data.cjs');
const out=path.resolve(process.argv[2]);fs.mkdirSync(out,{recursive:true});const results=[];let server,browser;
function pass(name){results.push('PASS '+name);console.log(results.at(-1));}
(async()=>{
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
 server=spawn(process.execPath,[path.join(__dirname,'preview-server.cjs'),String(port)],{windowsHide:true,stdio:['ignore','pipe','pipe']});
 await new Promise((resolve,reject)=>{server.stdout.once('data',resolve);server.once('error',reject);server.once('exit',code=>reject(Error('server exit '+code)));});
 const base='http://127.0.0.1:'+port;
 browser=await chromium.launch({channel:'chrome',headless:true});
 const context=await browser.newContext({viewport:{width:390,height:844},serviceWorkers:'block'}),page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base+'/#key='+'a'.repeat(64));await page.locator('.provider').first().waitFor();
 assert.equal(await page.locator('.credit-value').textContent(),'61,740');assert.ok((await page.locator('.plan-evidence').textContent()).includes('使用者確認'));pass('purchased_balance_separate_from_reset_vouchers');
 for(const width of [320,390,560]){
  await page.setViewportSize({width,height:844});
  for(const scenario of ['zero','unknown','stale','large','weekly']){
   await page.evaluate(scenario=>{const c=current.usage.creditBalance;c.balance=scenario==='zero'?0:scenario==='unknown'?null:scenario==='large'?1e12:61740.53;c.stale=scenario==='stale';render(current);},scenario);
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
   assert.equal(await page.locator('.credit-value').textContent(),scenario==='zero'?'0':scenario==='unknown'?'未提供':scenario==='large'?'1,000,000,000,000':'61,740');
   assert.equal(await page.locator('.credit-balance.cached').count(),scenario==='stale'?1:0);
  }
  await page.screenshot({path:path.join(out,'mobile-balance-'+width+'.png'),fullPage:true});
 }pass('balance_zero_unknown_stale_large_and_responsive');
 await page.setViewportSize({width:390,height:844});
 async function change(params){await page.evaluate(async params=>{await fetch('/fixture?'+new URLSearchParams(params),{method:'POST'});await load(true);},params);}
 await page.getByRole('button',{name:'通知',exact:true}).click();
 const initialEvents=fixture().notifications;
 const historyIds=await page.locator('#history .event').evaluateAll(rows=>rows.map(row=>row.dataset.event));
 assert.deepEqual(historyIds,initialEvents.map(event=>event.id));
 assert.equal(await page.locator('#history .energy-track').count(),0);assert.equal(await page.locator('#history .event-symbol').count(),0);pass('compact_quota_credit_reset_rows');
 assert.equal(await page.locator('#creditSummary .credit-expirations li').count(),3);assert.equal(await page.locator('#history .ResetCreditsAdded .credit-expirations time').count(),0);pass('credit_details_not_duplicated_in_compact_history');
 assert.ok(await page.locator('#creditSummary .credit-expirations time').evaluateAll(nodes=>nodes.every(n=>/\d{4}\/\d{2}\/\d{2}/.test(n.textContent))));pass('credit_expirations_include_year_date_and_time');
 assert.equal(await page.locator('#history .new-event').count(),0);pass('old_history_does_not_replay_alerts');
 const codexCredit=initialEvents.find(event=>event.provider==='codex'&&event.kind==='ResetCreditsAdded');
 for(const event of initialEvents){
  await page.locator('#history [data-event="'+event.id+'"]').click();await page.locator('#eventDialog[open]').waitFor();
  assert.equal(await page.locator('#eventDialog .'+event.kind).count(),1);
  if(event.kind==='ResetCreditsAdded')assert.equal(await page.locator('#eventDialog .credit-expirations time').count(),event.creditExpirations.length);
  if(event.id===codexCredit.id)await page.screenshot({path:path.join(out,'credit-detail-390.png')});
  await page.getByRole('button',{name:'關閉訊息',exact:true}).click();await page.locator('#eventDialog').waitFor({state:'hidden'});
  assert.equal(await page.locator('#history .event').count(),initialEvents.length);
 }pass('all_fixture_events_open_close_without_deleting_history');
 await page.locator('#history .event').first().focus();await page.keyboard.press('Enter');await page.locator('#eventDialog[open]').waitFor();await page.keyboard.press('Escape');await page.locator('#eventDialog').waitFor({state:'hidden'});pass('keyboard_open_escape_close');
 await page.locator('#history .event').first().click();await page.locator('#eventDialog[open]').waitFor();await page.goBack();await page.locator('#eventDialog').waitFor({state:'hidden'});assert.ok(await page.locator('#notificationsView').isVisible());pass('browser_back_closes_details_stays_in_app');
 await page.locator('#history .event').first().click();await page.locator('#eventDialog[open]').waitFor();await page.mouse.click(2,2);await page.locator('#eventDialog').waitFor({state:'hidden'});pass('backdrop_closes_detail');
 await page.evaluate(()=>{selectView('notifications');openEventDetail(syncedHistory().find(e=>e.kind==='ResetCreditsAdded'));current=null;clearHistory();});assert.equal(await page.locator('#eventDialog').isVisible(),false);assert.equal(await page.locator('#eventDialogContent').textContent(),'');await page.evaluate(()=>load(true));pass('account_clear_removes_open_detail');
 const pushedId=codexCredit.id;await page.goto(base+'/#event='+pushedId);await page.locator('#eventDialog[open] .ResetCreditsAdded').waitFor();await page.getByRole('button',{name:'關閉訊息'}).click();await page.locator('#eventDialog').waitFor({state:'hidden'});pass('push_deep_link_opens_exact_event');
 await page.evaluate(()=>openNotification('missing-event'));assert.ok((await page.locator('#eventDialogContent').textContent()).includes('尚未同步'));await page.getByRole('button',{name:'關閉訊息'}).click();await page.locator('#eventDialog').waitFor({state:'hidden'});pass('missing_event_not_replaced_with_another_notice');
 for(const width of [320,390,1024]){
  await page.setViewportSize({width,height:640});await page.evaluate(()=>{const e={...syncedHistory().find(e=>e.kind==='ResetCreditsAdded'),creditCount:100,creditExpirations:Array(100).fill(new Date(Date.now()+86400000).toISOString())};openEventDetail(e);});
  assert.equal(await page.locator('#eventDialog .credit-expirations time').count(),100);assert.ok(await page.locator('#eventDialog').evaluate(e=>e.scrollWidth<=e.clientWidth&&e.getBoundingClientRect().bottom<=innerHeight));assert.ok(await page.getByRole('button',{name:'關閉訊息'}).isVisible());await page.screenshot({path:path.join(out,'credit-detail-100-'+width+'.png')});await page.getByRole('button',{name:'關閉訊息'}).click();await page.locator('#eventDialog').waitFor({state:'hidden'});
 }pass('hundred_credit_details_scroll_with_visible_close');await page.setViewportSize({width:390,height:844});
 await change({event:'LowRemaining'});assert.equal(await page.locator('#history .new-event').count(),1);pass('new_event_animates_once');
 await page.evaluate(()=>load(true));assert.equal(await page.locator('#history .new-event').count(),0);pass('refresh_does_not_reanimate');
 await page.emulateMedia({reducedMotion:'reduce'});await change({event:'UsageReset'});assert.equal(await page.locator('#history .new-event').first().evaluate(e=>getComputedStyle(e).animationName),'none');pass('reduced_motion_disables_effects');await page.emulateMedia({reducedMotion:'no-preference'});
 for(const width of [320,360,390,430,1024]){
  await page.setViewportSize({width,height:844});await change({scenario:'weekly'});
  for(const theme of ['deep-night','day','matrix','cloud_observation']){
   await change({theme});
   assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
   const bounds=await page.locator('#history .event').evaluateAll(es=>es.map(e=>({width:e.clientWidth,scroll:e.scrollWidth,text:e.textContent,children:Array.from(e.children).map(c=>({width:c.getBoundingClientRect().width,scroll:c.scrollWidth,grid:getComputedStyle(c).gridColumn}))})));assert.ok(bounds.every(e=>e.scroll<=e.width),JSON.stringify({width,theme,bounds}));
   assert.ok(await page.locator('#history img').evaluateAll(es=>es.every(e=>e.complete&&e.naturalWidth>0)));
   await page.screenshot({path:path.join(out,`notifications-${theme}-${width}.png`)});
   pass(`notification_layout_${theme}_${width}`);
  }
 }
 await page.setViewportSize({width:390,height:844});await change({theme:'deep-night',scenario:'weekly'});await page.getByRole('button',{name:'用量',exact:true}).click();
 assert.equal(await page.locator('.codex .window').count(),1);assert.equal(await page.locator('.codex [data-window="5H"]').count(),0);await page.screenshot({path:path.join(out,'usage-weekly-390.png'),fullPage:true});pass('weekly_only_no_5h');
 await page.evaluate(()=>{current.usage.providers[0].plan='Pro 25x';render(current);});assert.equal(await page.locator('.codex .plan').textContent(),'Pro 25x');assert.equal(await page.locator('.codex .window').count(),1);await page.screenshot({path:path.join(out,'usage-pro25-weekly-390.png'),fullPage:true});pass('pro25_label_with_only_official_weekly_window');
 await change({scenario:'dual'});assert.equal(await page.locator('.codex .window').count(),2);await page.screenshot({path:path.join(out,'usage-dual-390.png'),fullPage:true});pass('plus_dual_windows');
 await page.evaluate(()=>{current.usage.providers[0].plan='Pro';render(current);});assert.equal(await page.locator('.codex .window').count(),2);pass('pro_real_5h_not_hardcoded_hidden');
 await change({scenario:'hidden'});assert.equal(await page.locator('.claude').count(),0);assert.equal(await page.locator('#history .event').filter({hasText:'Claude Code'}).count(),0);pass('disabled_claude_usage_and_events_hidden');
 await page.getByRole('button',{name:'設定',exact:true}).click();await page.getByRole('switch',{name:'頁面提示音',exact:true}).check();await page.getByText('頁面提示音已啟用',{exact:true}).waitFor();
 assert.equal(await page.evaluate(()=>audioContext.state),'running');
 for(const kind of ['LowRemaining','UsageReset','ResetCreditsAdded']){await page.getByLabel('試聽事件').selectOption(kind);await page.getByRole('button',{name:'試聽',exact:true}).click();assert.equal(await page.evaluate(()=>audioContext.state),'running');}pass('three_foreground_sounds_from_user_gesture');
 await page.getByRole('switch',{name:'頁面提示音',exact:true}).uncheck();assert.equal(await page.evaluate(()=>playEventSound('UsageReset')),false);pass('sound_opt_out');
 await page.screenshot({path:path.join(out,'settings-390.png'),fullPage:true});
 assert.equal(await page.evaluate(()=>Object.keys(localStorage).some(k=>k.endsWith('-events')||k.endsWith('-seen'))),false);pass('no_phone_local_history_archive');
 await change({scenario:'weekly'});await page.getByRole('button',{name:'通知',exact:true}).click();
 await change({event:'ResetCreditsAdded'});await page.screenshot({path:path.join(out,'event-glow-start.png'),fullPage:true});await page.waitForTimeout(500);await page.screenshot({path:path.join(out,'event-glow-500ms.png'),fullPage:true});
 assert.notDeepEqual(fs.readFileSync(path.join(out,'event-glow-start.png')),fs.readFileSync(path.join(out,'event-glow-500ms.png')));pass('animation_frames_differ');
 await page.goto(base+'/preview.html');await page.frameLocator('#phone').locator('.credit-value').waitFor();await page.setViewportSize({width:1200,height:1120});await page.getByRole('button',{name:'通知中心',exact:true}).click();await page.frameLocator('#phone').locator('#history .event').first().waitFor();await page.screenshot({path:path.join(out,'preview-desktop.png'),fullPage:true});pass('interactive_preview_rendered');
 const iconResponse=await page.request.get(base+'/pulse-icon-512-v1.png?v=2026.11.18.2');
 assert.ok(iconResponse.ok());
 const mobileIcon=await iconResponse.body();
 assert.deepEqual(mobileIcon,fs.readFileSync(path.join(__dirname,'..','Assets','UsageTools.png')));
 const dimensions={width:mobileIcon.readUInt32BE(16),height:mobileIcon.readUInt32BE(20)};
 assert.deepEqual(dimensions,{width:512,height:512});
 fs.writeFileSync(path.join(out,'icon-pixels.json'),JSON.stringify(dimensions,null,2));
 pass('mobile_icon_matches_original_tool_logo');
 assert.deepEqual(errors,[]);pass('no_browser_script_errors');await context.close();
 // Desktop WebKit is supplementary coverage, not an iPhone push-delivery test.
 let webkitBrowser;try{webkitBrowser=await webkit.launch({headless:true});}catch(e){results.push('NOT_CHECKED desktop_webkit_runtime_unavailable');}
 if(webkitBrowser){try{const p=await webkitBrowser.newPage({viewport:{width:390,height:844}});await p.goto(base+'/#key='+'a'.repeat(64));await p.locator('.provider').first().waitFor();await p.getByRole('button',{name:'通知',exact:true}).click();assert.equal(await p.locator('#history .event').count(),initialEvents.length+1);await p.screenshot({path:path.join(out,'webkit-notifications.png'),fullPage:true});pass('desktop_webkit_notification_render');}finally{await webkitBrowser.close();}}
 console.log('ALL PASS checks='+results.filter(x=>x.startsWith('PASS')).length);
})().catch(e=>{results.push('FAIL '+e.stack);console.error(e);process.exitCode=1;}).finally(async()=>{if(browser)await browser.close();if(server&&server.exitCode===null){server.kill();await new Promise(r=>server.once('exit',r));}fs.writeFileSync(path.join(out,'polish-tests.txt'),results.join('\n')+'\n');});
