const fs=require('node:fs'),http=require('node:http'),path=require('node:path'),assert=require('node:assert/strict'),vm=require('node:vm');
const {chromium,webkit}=require('playwright');
const output=process.argv[2],root=__dirname;
if(!output)throw new Error('Evidence output directory required');
fs.mkdirSync(output,{recursive:true});
let fail=false;
const now=new Date().toISOString(),later=new Date(Date.now()+7200000).toISOString();
const fixture=()=>({usage:{schemaVersion:2,fixture:true,themeId:'deep-night',receivedAt:now,providers:[
 {name:'Codex',enabled:true,state:'Fresh',plan:'Plus',syncedAt:now,windows:[{label:'5H',used:38,resetsAt:later},{label:'7D',used:76,resetsAt:later}]},
 {name:'Claude Code',enabled:true,state:'Fresh',plan:'Pro',syncedAt:now,windows:[{label:'5H',used:85,resetsAt:later},{label:'7D',used:41,resetsAt:later}]}
]},publicKey:'fixture'});
let payload=fixture();
const server=http.createServer((req,res)=>{
 if(req.url.startsWith('/api.php')){res.writeHead(fail?503:200,{'Content-Type':'application/json'});res.end(JSON.stringify(payload));return;}
 const file=path.resolve(root,req.url.split('?')[0]==='/'?'index.html':'.'+decodeURIComponent(req.url.split('?')[0]));
 if(!file.startsWith(root+path.sep)||!fs.existsSync(file)){res.writeHead(404);res.end();return;}
 res.setHeader('Content-Type',file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':file.endsWith('.png')?'image/png':file.endsWith('.jpg')?'image/jpeg':'text/html');res.end(fs.readFileSync(file));
});
const results=[];
function pass(name){results.push('PASS '+name);console.log(results.at(-1));}
async function workerTests(browser){
 const page=await browser.newPage(),notifications=[];
 const cases=[{name:'text_image',mode:'image'},{name:'no_canvas_fallback',mode:'no-canvas'},{name:'image_failure_fallback',mode:'reject-image'},{name:'image_opt_out',mode:'image',optOut:true},{name:'invalid_quota_no_image',mode:'image',invalid:true}];
 for(const test of cases){
  const captured=[],callbacks={};let sandbox;
  const Canvas=function(w,h){this.width=w;this.height=h;this.getContext=()=>({fillRect(){},fillText(){}});this.convertToBlob=async()=>({arrayBuffer:async()=>Uint8Array.from([137,80,78,71]).buffer});};
  sandbox={self:{clients:{matchAll:async()=>[]},location:{origin:'https://fixture.test'},registration:{scope:'https://fixture.test/personal/',showNotification:async(title,opts)=>{if(test.mode==='reject-image'&&opts.image)throw new Error('image not supported');captured.push({title,...opts});}},addEventListener:(kind,callback)=>callbacks[kind]=callback},URL,btoa:s=>Buffer.from(s,'binary').toString('base64'),OffscreenCanvas:test.mode==='no-canvas'?undefined:Canvas,Uint8Array,console};
  vm.runInNewContext(fs.readFileSync(path.join(root,'sw.js'),'utf8'),sandbox);
  let pending;callbacks.push({data:{json:()=>({title:'Codex 7D',body:'剩餘 [██░░░░░░░░] 20%',provider:'codex',quota:{remaining:test.invalid?999:20,provider:'Codex'},imageEnabled:!test.optOut})},waitUntil:p=>pending=p});await pending;
  assert.equal(captured.length,1);assert.equal(captured[0].body,'剩餘 [██░░░░░░░░] 20%');assert.equal(!!captured[0].image,test.mode==='image'&&!test.optOut&&!test.invalid);notifications.push(captured[0]);pass('push_'+test.name);
 }
 {
  const scope='https://fixture.test/staging/mobile/',prefix='usagetools-scope-'+encodeURIComponent(scope)+'-',deleted=[],callbacks={};
  const currentVersion=fs.readFileSync(path.join(root,'sw.js'),'utf8').match(/cacheName=cachePrefix\+'(v\d+)'/)[1];
  const keys=[prefix+'v4',prefix+'v5',prefix+'v6',prefix+'v7',prefix+'v8',prefix+'v9',prefix+'v10',prefix+currentVersion,prefix+'identity','usagetools-scope-'+encodeURIComponent('https://fixture.test/personal/')+'-v5','usagetools-personal-v2'];
  const sandbox={self:{registration:{scope},clients:{claim:async()=>{}},addEventListener:(kind,callback)=>callbacks[kind]=callback},caches:{keys:async()=>keys,delete:async key=>deleted.push(key)},URL};
  vm.runInNewContext(fs.readFileSync(path.join(root,'sw.js'),'utf8'),sandbox);
  let pending;callbacks.activate({waitUntil:p=>pending=p});await pending;
  assert.deepEqual(deleted,[prefix+'v4',prefix+'v5',prefix+'v6',prefix+'v7',prefix+'v8',prefix+'v9',prefix+'v10'].filter(k=>k!==prefix+currentVersion));pass('worker_cache_scope_isolation');
 }
 // Real browser canvas PNG and pixel checks, without claiming native-device delivery.
 await page.goto('http://127.0.0.1:'+server.address().port);
 const image=await page.evaluate(async source=>{const localSelf={addEventListener(){},registration:{scope:location.href}};return await new Function('self',source+';return quotaImage({remaining:20,provider:"Codex"});')(localSelf);},fs.readFileSync(path.join(root,'sw.js'),'utf8'));
 assert.ok(image.startsWith('data:image/png;base64,'));fs.writeFileSync(path.join(output,'notification-bar.png'),Buffer.from(image.split(',')[1],'base64'));
 const pixels=await page.evaluate(async image=>{const img=new Image();img.src=image;await img.decode();const canvas=document.createElement('canvas');canvas.width=img.width;canvas.height=img.height;const ctx=canvas.getContext('2d');ctx.drawImage(img,0,0);return {width:img.width,height:img.height,filled:Array.from(ctx.getImageData(40,100,1,1).data),empty:Array.from(ctx.getImageData(140,100,1,1).data)};},image);
 assert.deepEqual(pixels.filled,[255,115,115,255]);assert.deepEqual(pixels.empty,[69,77,73,255]);fs.writeFileSync(path.join(output,'pixel_checks.json'),JSON.stringify(pixels,null,2));pass('push_real_canvas_pixels');
 fs.writeFileSync(path.join(output,'push-options.json'),JSON.stringify(notifications,null,2));await page.close();
}
(async()=>{
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const browser=await chromium.launch({channel:'chrome',headless:true});
 try{
  for(const width of [320,360,390,430,1024]){
   payload=fixture();const context=await browser.newContext({viewport:{width,height:844}}),page=await context.newPage();
   const errors=[];page.on('pageerror',e=>errors.push(e.message));await page.goto('http://127.0.0.1:'+server.address().port+'/#key='+'a'.repeat(64));await page.locator('.codex .remaining').first().getByText('62%',{exact:true}).waitFor();
   assert.equal(await page.locator('.provider').count(),2);assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
   assert.equal(await page.evaluate(()=>Array.from(document.querySelectorAll('.app-title img,.provider-header img')).every(i=>i.complete&&i.naturalWidth>0)),true);
   assert.equal(await page.locator('.codex .fill').first().evaluate(n=>n.style.width),'62%');
   await page.screenshot({path:path.join(output,'dual-'+width+'.png'),fullPage:true});pass('layout_'+width);
   await page.getByRole('button',{name:'設定',exact:true}).click();await page.getByRole('switch',{name:'Claude Code 顯示與通知'}).uncheck();
   await page.getByRole('button',{name:'用量',exact:true}).click();assert.equal(await page.locator('.claude').count(),0);pass('hidden_provider_'+width);
   payload.usage.providers[0].plan='Pro';payload.usage.providers[0].windows=payload.usage.providers[0].windows.filter(w=>w.label==='7D');await page.getByRole('button',{name:'重新整理'}).click();await page.waitForFunction(()=>document.querySelectorAll('.codex .window').length===1);
   assert.equal(await page.locator('.codex [data-window="5H"]').count(),0);await page.screenshot({path:path.join(output,'pro-'+width+'.png'),fullPage:true});pass('pro_weekly_only_'+width);
   payload.usage.themeId='matrix';await page.getByRole('button',{name:'重新整理'}).click();await page.waitForFunction(()=>document.body.dataset.theme==='matrix');
   await page.screenshot({path:path.join(output,'classic-'+width+'.png'),fullPage:true});pass('desktop_theme_sync_'+width);
   await page.getByRole('button',{name:'設定',exact:true}).click();await page.getByRole('switch',{name:'跟隨電腦主題'}).uncheck();await page.getByLabel('手機主題',{exact:true}).selectOption('day');
   await page.getByRole('button',{name:'用量',exact:true}).click();await page.getByRole('button',{name:'重新整理'}).click();assert.equal(await page.evaluate(()=>document.body.dataset.theme),'day');pass('local_theme_override_'+width);
   assert.deepEqual(errors,[]);await context.close();
  }
  payload=fixture();const context=await browser.newContext({viewport:{width:390,height:844}}),page=await context.newPage();await page.goto('http://127.0.0.1:'+server.address().port+'/#key='+'a'.repeat(64));await page.locator('.provider').first().waitFor();
  async function reload(){await page.getByRole('button',{name:'重新整理'}).click();await page.waitForFunction(()=>!document.getElementById('refresh').disabled);}
  payload.usage.providers[1].enabled=false;await reload();assert.equal(await page.locator('.claude').count(),0);pass('desktop_disabled_hidden');
  payload.usage.providers[1].enabled=true;payload.usage.providers[1].windows[0]={label:'5H',used:0,resetsAt:null};await reload();await page.getByText('從發送訊息時開始',{exact:true}).waitFor();pass('zero_session_start');
  payload.usage.providers[0].windows[0].used=100;await reload();assert.equal(await page.locator('.codex .remaining').first().textContent(),'0%');assert.equal(await page.locator('.codex .fill').first().evaluate(n=>n.style.width),'0%');pass('full_usage_not_frozen');
  payload.usage.providers[0].windows[0].resetsAt=new Date(Date.now()-1000).toISOString();await reload();assert.equal(await page.locator('.codex .remaining').first().textContent(),'尚未同步');pass('reset_requires_official_confirmation');
  payload.usage.providers[0].plan='Pro';payload.usage.providers[0].windows=[{label:'5H',used:null,resetsAt:null},{label:'7D',used:16,resetsAt:later}];await reload();assert.equal(await page.locator('.codex .window').count(),1);pass('legacy_schema_placeholder_removed');
  payload.usage.themeId='<img src=x onerror="window.leaked=true">';payload.usage.providers[1].plan='<script>window.leaked=true</script>';await reload();assert.equal(await page.evaluate(()=>document.body.dataset.theme),'deep-night');assert.equal(await page.evaluate(()=>window.leaked),undefined);pass('dynamic_text_and_theme_injection_blocked');
  payload.notifications=[{id:'c'.repeat(32),at:now,provider:'codex',kind:'ResetCreditsAdded',window:'',remaining:0,creditCount:3,addedCount:1}];
  payload.usage.resetCredits={available:3,syncedAt:now,stale:false};await reload();
  await page.getByRole('button',{name:'通知',exact:true}).click();
  await page.locator('#history li').filter({hasText:'新增 1 張重置券'}).waitFor();pass('server_credit_history_visible_without_push_permission');
  assert.ok((await page.locator('#creditSummary').textContent()).includes('可用 3 張'));pass('current_credits_separate_from_event_count');
  await reload();assert.equal(await page.locator('#history li').filter({hasText:'新增 1 張'}).count(),1);pass('history_refresh_deduplicated');
  await page.screenshot({path:path.join(output,'reset-credit-history-fixture.png'),fullPage:true});
  await page.reload();await page.locator('.provider').first().waitFor();await page.getByRole('button',{name:'通知',exact:true}).click();
  assert.equal(await page.locator('#history li').filter({hasText:'新增 1 張'}).count(),1);pass('closed_page_reload_restores_server_history');
  payload.usage.providers[0].enabled=false;await reload();assert.equal(await page.locator('#history li').filter({hasText:'Codex'}).count(),0);assert.equal(await page.locator('#creditSummary').isVisible(),false);pass('disabled_provider_history_hidden');
  payload.usage.providers[0].enabled=true;
  fail=true;await reload();await page.getByText('連線失敗 (503)',{exact:true}).waitFor();pass('offline_cache_is_marked');await context.close();fail=false;
  await workerTests(browser);
  fs.writeFileSync(path.join(output,'ui-tests.txt'),results.join('\n')+'\nALL PASS\n');console.log('ALL PASS checks='+results.length);
 }finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);fs.writeFileSync(path.join(output,'ui-tests.txt'),results.join('\n')+'\nFAIL '+e.message+'\n');server.close();process.exitCode=1;});
