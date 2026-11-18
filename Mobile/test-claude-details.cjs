// FIXTURE ONLY: local preview checks for the optional Claude details; not a physical phone test.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),net=require('node:net');
const {spawn}=require('node:child_process');
const {chromium}=require('playwright');
const out=path.resolve(process.argv[2]||'');if(!process.argv[2])throw Error('evidence directory required');fs.mkdirSync(out,{recursive:true});
const results=[];let server,browser;
function pass(name){results.push('PASS '+name);console.log(results.at(-1));}
(async()=>{
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
 server=spawn(process.execPath,[path.join(__dirname,'preview-server.cjs'),String(port)],{windowsHide:true,stdio:['ignore','pipe','pipe']});
 await new Promise((resolve,reject)=>{server.stdout.once('data',resolve);server.once('error',reject);server.once('exit',code=>reject(Error('server exit '+code)));});
 const base='http://127.0.0.1:'+port;
 browser=await chromium.launch({channel:'chrome',headless:true});
 for(const width of [320,390,430]){
  const context=await browser.newContext({viewport:{width,height:844},serviceWorkers:'block'}),page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(base+'/#key='+'a'.repeat(64));await page.locator('.provider.claude').waitFor();
  const summary=page.locator('.claude-summary');
  assert.equal(await summary.count(),1);
  const summaryText=await summary.textContent();
  assert.ok(summaryText.includes('Fable 88%')&&summaryText.includes('重置券 3 張'),summaryText);
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'horizontal overflow at '+width);
  await page.locator('.provider.claude').screenshot({path:path.join(out,'mobile-claude-card-'+width+'.png')});
  await summary.click();await page.locator('#eventDialog[open] .claude-details').waitFor();
  const grants=page.locator('.claude-grant');assert.equal(await grants.count(),3);
  const times=await grants.locator('time').allTextContents();assert.ok(times.every(t=>t.includes('到期')),times.join('|'));
  assert.equal(new Set(times).size,2,'same-expiry grants listed separately');
  assert.equal(await page.locator('.claude-pool').count(),3);
  assert.ok((await page.locator('.claude-details').textContent()).includes('最多占每週額度 50%'));
  assert.ok((await page.locator('.claude-details').textContent()).includes('與重置券分開計算'));
  assert.ok(await page.evaluate(()=>{const d=document.querySelector('#eventDialog');return d.scrollWidth<=d.clientWidth+1;}),'dialog overflow at '+width);
  await page.screenshot({path:path.join(out,'mobile-claude-detail-'+width+'.png'),fullPage:false});
  await page.locator('#eventDialogClose').click();await page.waitForFunction(()=>!document.querySelector('#eventDialog').open);
  assert.deepEqual(errors,[]);
  pass('claude_summary_and_detail_dialog_'+width);
  if(width===390){
   await page.locator('[data-view=notifications]').click();
   const rows=await page.locator('#history .event-row h3').allTextContents();
   assert.ok(rows.some(t=>t.includes('新增 1 張完整重置券'))&&rows.some(t=>t.includes('7D Fable 低額度提醒')),rows.join('|'));
   await page.locator('#history').screenshot({path:path.join(out,'mobile-claude-history-390.png')});
   pass('claude_history_titles_name_pool_and_credit_kind');
   await page.evaluate(async()=>{await fetch('/fixture?theme=day',{method:'POST'});});
   await page.locator('[data-view=usage]').click();await page.locator('#refresh').click();await page.waitForFunction(()=>document.body.dataset.theme==='day');
   await page.locator('.claude-summary').click();await page.locator('#eventDialog[open] .claude-details').waitFor();
   await page.screenshot({path:path.join(out,'mobile-claude-detail-day-390.png')});
   pass('claude_detail_follows_desktop_theme');
   const boundaries=await page.evaluate(()=>{
    const future=new Date(Date.now()+86400000).toISOString(),past=new Date(Date.now()-86400000).toISOString();
    const list=claudeGrantRows([{left:0,expiresAt:future},{left:1,expiresAt:past},{left:null,expiresAt:future},{left:22,expiresAt:future}]);
    current.usage.claudeDetails.extraUsage={enabled:true,used:null,limit:null,currency:null};
    current.usage.claudeDetails.pools=[{label:'7D',model:'Fable',used:12,resetsAt:past}];
    current.usage.claudeDetails.syncedAt=new Date().toISOString();
    const content=document.createElement('div');renderClaudeDetails(content);
    return {rows:list.querySelectorAll('.claude-grant').length,first:list.querySelector('.claude-grant-index').textContent,text:list.textContent,detail:content.textContent,summary:claudeSummaryButton(true).textContent};
   });
   assert.equal(boundaries.rows,21);assert.equal(boundaries.first,'數量未知');assert.ok(boundaries.text.includes('另有 2 張'));
   assert.ok(boundaries.detail.includes('本月 未提供')&&boundaries.detail.includes('上限未提供')&&!boundaries.detail.includes('未設上限'));
   assert.ok(boundaries.summary.includes('Fable 已過期／未同步'));
   pass('exhausted_expired_unknown_and_large_grants_and_missing_money');
   const stale=await page.evaluate(()=>{
    const provider=current.usage.providers.find(p=>p.name==='Claude Code');
    provider.state='Stale';provider.syncedAt=new Date(Date.now()-11*3600000).toISOString();
    provider.windows=[{label:'5H',used:0,resetsAt:null},{label:'7D',used:84,resetsAt:new Date(Date.now()+86400000).toISOString()}];
    current.usage.claudeDetails.syncedAt=provider.syncedAt;
    const node=showProvider(provider,true);
    document.querySelector('.provider.claude').replaceWith(node);
    return {text:node.textContent,windows:node.querySelectorAll('.window').length,fresh:claudeDetailsFresh(current.usage.claudeDetails)};
   });
   assert.equal(stale.windows,2);assert.equal(stale.fresh,false);
   assert.ok(stale.text.includes('已過期／未同步')&&stale.text.includes('16%')&&stale.text.includes('資料停在'),stale.text);
   assert.ok(!stale.text.includes('從發送訊息時開始'));
   await page.locator('#eventDialogClose').click();
   await page.locator('.provider.claude').screenshot({path:path.join(out,'mobile-claude-stale-390.png')});
   pass('eleven_hour_cache_no_false_zero_keeps_weekly_with_timestamp');
  }
  await context.close();
 }
 const context=await browser.newContext({viewport:{width:390,height:844},serviceWorkers:'block'}),page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/api.php?op=usage',async route=>{const response=await route.fetch(),body=await response.json();delete body.usage.claudeDetails;await route.fulfill({response,json:body});});
 await page.goto(base+'/#key='+'a'.repeat(64));await page.locator('.provider.claude').waitFor();
 assert.equal(await page.locator('.claude-summary').count(),0);assert.deepEqual(errors,[]);
 pass('old_backend_without_claude_details_renders_unchanged');
 await page.evaluate(async()=>{await fetch('/fixture?scenario=hidden',{method:'POST'});});
 await page.unroute('**/api.php?op=usage');await page.locator('#refresh').click();
 await page.waitForFunction(()=>!document.querySelector('.provider.claude'));
 assert.equal(await page.locator('.claude-summary').count(),0);
 pass('claude_disabled_hides_details');
 await context.close();
 fs.writeFileSync(path.join(out,'claude-details-results.txt'),'FIXTURE ONLY - local preview, not a physical phone\n'+results.join('\n')+'\nSUMMARY passed=true checks='+results.length+'\n');
})().catch(error=>{console.error('FAIL',error.stack||error.message);process.exitCode=1;}).finally(async()=>{await browser?.close();server?.kill();});
