const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const {spawn,spawnSync}=require('node:child_process'),net=require('node:net');
const {chromium}=require('playwright');
const php=process.argv[2],output=path.resolve(process.argv[3]||'');
if(!php||!process.argv[3])throw Error('PHP executable and evidence directory required');
const root=path.join(output,'fixture'),prefix='<?php exit; ?>\n',results=[];
const hash=s=>crypto.createHash('sha256').update(s).digest('hex');
function identity(){const ec=crypto.createECDH('prime256v1');ec.generateKeys();const writer=crypto.randomBytes(32).toString('hex');return {writer,reader:crypto.randomBytes(32).toString('hex'),id:hash(writer),publicKey:ec.getPublicKey().toString('base64url')};}
function pass(name){results.push('PASS '+name);console.log(results.at(-1));}
const a=identity(),b=identity(),legacy=identity();
function event(i=1){return {id:i.toString(16).padStart(32,'0'),at:new Date().toISOString(),provider:'codex',window:'',kind:'ResetCreditsAdded',remaining:0,creditCount:3,addedCount:1};}
function payload(events=[]){const now=new Date().toISOString();return {schemaVersion:2,sentAt:now,themeId:'deep-night',resetCredits:{available:3,syncedAt:now,stale:false,secret:'NEVER_STORE'},notifications:events,providers:[{name:'Codex',enabled:true,state:'Fresh',syncedAt:now,plan:'Plus',windows:[{label:'7D',used:20,resetsAt:new Date(Date.now()+86400000).toISOString()}]},{name:'Claude Code',enabled:false,state:'Disabled',syncedAt:null,plan:null,windows:[]}]};}
let server,browser,base,serverLog='';
async function request(user,method='GET',body,service='mobile',role=method==='GET'?'reader':'writer',target=user.id){return fetch(base+'/'+service+'/api.php',{method,headers:{Authorization:'Bearer '+user[role],'X-Usage-Account':target,'Content-Type':'application/json'},body:body?JSON.stringify(body):undefined,signal:AbortSignal.timeout(5000)});}
(async()=>{
 fs.mkdirSync(root,{recursive:true});
 for(const service of ['mobile','personal']){
  const dir=path.join(root,service);fs.mkdirSync(dir,{recursive:true});
  for(const file of ['api.php','tenancy.php','notification-history.php','reset-news.php','reset-news-translation.php']){const lint=spawnSync(php,['-n','-l',path.join(__dirname,file)],{encoding:'utf8',windowsHide:true});assert.equal(lint.status,0,lint.stderr||lint.stdout);fs.copyFileSync(path.join(__dirname,file),path.join(dir,file));}
  for(const file of ['index.html','app.js','sw.js','style.css','notifications.js','claude-details.js','notifications.css','reset-news.js','reset-news.css','manifest.php','manifest.webmanifest','pulse-icon-180-v1.png','pulse-icon-192-v1.png','pulse-icon-512-v1.png','badge.png','codex-icon.png','claude-icon.png'])fs.copyFileSync(path.join(__dirname,file),path.join(dir,file));
 }
 pass('PHP_7_3_syntax');
 fs.writeFileSync(path.join(root,'mobile','server-config.php'),prefix+JSON.stringify({mode:'multi',registrationEnabled:true,fixture:true}));
 fs.writeFileSync(path.join(root,'personal','config.php'),prefix+JSON.stringify({writeHash:hash(legacy.writer),readHash:hash(legacy.reader),publicKey:legacy.publicKey,fixture:true}));
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));base='http://127.0.0.1:'+port;
 server=spawn(php,['-n','-S','127.0.0.1:'+port,'-t',root],{windowsHide:true,stdio:['ignore','ignore','pipe']});server.stderr.on('data',d=>serverLog+=d);
 for(let i=0;i<50;i++){try{await fetch(base);break;}catch{await new Promise(r=>setTimeout(r,100));}}
 for(const user of [a,b]){const r=await fetch(base+'/mobile/api.php?op=register',{method:'POST',headers:{Authorization:'Bearer '+user.writer,'Content-Type':'application/json'},body:JSON.stringify({readHash:hash(user.reader),publicKey:user.publicKey})});assert.equal(r.status,201);}
 pass('two_isolated_fixture_accounts_registered');
 const balancePayload=payload();balancePayload.creditBalance={balance:61740.53,unlimited:false,stale:false,syncedAt:new Date().toISOString(),secret:'NEVER_STORE'};balancePayload.planConfirmed=true;balancePayload.providers[0].plan='Pro US$200/mo';
 assert.equal((await request(a,'POST',balancePayload)).status,200);let balanceRead=await(await request(a)).json();assert.equal(balanceRead.usage.creditBalance.balance,61740.53);assert.equal(balanceRead.usage.planConfirmed,true);assert.ok(!JSON.stringify(balanceRead).includes('NEVER_STORE'));
 assert.equal((await request(a,'GET',undefined,'mobile','reader',b.id)).status,401);pass('balance_and_price_plan_whitelisted_account_isolated');
 for(const value of [-1,1e13,'61740',{}]){balancePayload.creditBalance.balance=value;assert.equal((await request(a,'POST',balancePayload)).status,400);}pass('invalid_balance_rejected');
 balancePayload.creditBalance.balance=0;assert.equal((await request(a,'POST',balancePayload)).status,200);balanceRead=await(await request(a)).json();assert.equal(balanceRead.usage.creditBalance.balance,0);pass('real_zero_balance_preserved');
 balancePayload.providers[0].enabled=false;assert.equal((await request(a,'POST',balancePayload)).status,200);balanceRead=await(await request(a)).json();assert.equal(balanceRead.usage.creditBalance,null);assert.equal(balanceRead.usage.planConfirmed,false);pass('disabled_balance_hidden');
 const newsItem={id:'1111111111111111111',at:new Date().toISOString(),kind:'regular',text:'Synthetic news',url:'https://x.com/thsottiaux/status/1111111111111111111'};
 fs.writeFileSync(path.join(root,'mobile','reset-news-cache.php'),prefix+JSON.stringify({nextAttempt:Date.now()/1000+300,fetchedAt:new Date().toISOString(),failed:false,items:[newsItem]}));
 const newsHeaders=user=>({Authorization:'Bearer '+user.reader,'X-Usage-Account':user.id});
 assert.equal((await fetch(base+'/mobile/api.php?op=reset-news')).status,401);
 assert.equal((await fetch(base+'/mobile/api.php?op=reset-news',{headers:{...newsHeaders(a),'X-Usage-Account':b.id}})).status,401);
 for(const user of [a,b]){const news=await(await fetch(base+'/mobile/api.php?op=reset-news',{headers:newsHeaders(user)})).json();assert.deepEqual(news.items,[newsItem]);assert.ok(!JSON.stringify(news).includes(user.reader));}pass('public_news_authenticated_no_private_account_fields');
 const subscription={endpoint:'https://web.push.apple.com/synthetic-news',keys:{p256dh:a.publicKey,auth:'a'.repeat(22)},preferences:{resetNews:true}};
 const subscribe=body=>fetch(base+'/mobile/api.php?op=subscriptions',{method:'POST',headers:{...newsHeaders(a),'Content-Type':'application/json'},body:JSON.stringify(body)});
 assert.equal((await subscribe(subscription)).status,200);
 const subFile=path.join(root,'mobile','accounts',a.id,'subscriptions.php');const savedSub=()=>Object.values(JSON.parse(fs.readFileSync(subFile,'utf8').slice(prefix.length)))[0];
 const since=savedSub().newsSince;assert.ok(Number.isFinite(Date.parse(since)));await subscribe(subscription);assert.equal(savedSub().newsSince,since);
 await subscribe({...subscription,preferences:{reset:true}});assert.equal(savedSub().preferences.resetNews,false);assert.equal(savedSub().newsSince,null);pass('news_opt_in_timestamp_stable_and_opt_out_cleared');
 const expirations=[new Date(Date.now()+86400000).toISOString(),new Date(Date.now()+86400000).toISOString(),new Date(Date.now()+7*86400000).toISOString()];
 const upload=payload([{...event(),plan:'Pro 25x',creditExpirations:expirations,secret:'NEVER_STORE'}]);upload.providers[0].plan='Pro 25x';upload.resetCredits.expirations=expirations;
 let r=await request(a,'POST',upload);assert.equal(r.status,200);assert.equal((await r.json()).historyAccepted,true);pass('history_persisted_without_push_subscription');
 for(let i=0;i<3;i++)assert.equal((await request(a,'POST',upload)).status,200);
 let data=await(await request(a)).json();assert.equal(data.notifications.length,1);assert.equal(data.notifications[0].addedCount,1);assert.equal(data.usage.resetCredits.available,3);assert.ok(!JSON.stringify(data).includes('NEVER_STORE'));pass('retry_deduplicated_and_privacy_whitelisted');
 assert.equal(data.usage.resetCredits.expirations.length,3);assert.equal(data.notifications[0].creditExpirations.length,3);assert.equal(data.notifications[0].creditExpirations[0],data.notifications[0].creditExpirations[1]);pass('per_credit_dates_preserved_including_same_expiry');
 const pro25=payload([{...event(),plan:'Pro 25x'}]);pro25.providers[0].plan='Pro 25x';assert.equal((await request(a,'POST',pro25)).status,200);data=await(await request(a)).json();assert.equal(data.usage.providers[0].plan,'Pro 25x');assert.equal(data.usage.providers[0].windows.length,1);assert.equal(data.notifications[0].plan,'Pro 25x');pass('pro25_plan_and_history_accepted_without_phantom_window');
 const pro10=payload([{...event(2),plan:'Pro 10x'}]);pro10.providers[0].plan='Pro 10x';assert.equal((await request(b,'POST',pro10)).status,200);data=await(await request(b)).json();assert.equal(data.usage.providers[0].plan,'Pro 10x');assert.equal(data.notifications[0].plan,'Pro 10x');pass('new_customer_pro10_plan_accepted');
 for(const dates of [['bad-date'],Array(101).fill(expirations[0]),[{id:'secret'}]]){const invalid=payload([{...event(999),creditExpirations:dates}]);assert.equal((await request(a,'POST',invalid)).status,400);invalid.notifications=[];invalid.resetCredits.expirations=dates;assert.equal((await request(a,'POST',invalid)).status,400);}pass('invalid_or_oversized_credit_expirations_rejected');
 let oldClient=payload();delete oldClient.notifications;delete oldClient.resetCredits;
 assert.equal((await request(a,'POST',oldClient)).status,200);data=await(await request(a)).json();assert.equal(data.notifications.length,1);pass('old_desktop_upload_does_not_erase_history');
 assert.equal((await request(b,'POST',payload([event(2)]))).status,200);
 data=await(await request(b)).json();assert.equal(data.notifications.length,1);assert.equal(data.notifications[0].id,event(2).id);pass('other_account_has_only_own_history');
 for(const [user,target] of [[a,b],[b,a]])for(const role of ['reader','writer'])for(const method of ['GET','POST'])assert.equal((await request(user,method,method==='POST'?upload:undefined,'mobile',role,target.id)).status,401);
 pass('eight_cross_account_history_read_write_attacks_rejected');
 assert.equal((await request(a,'POST',upload,'mobile','reader')).status,403);pass('reader_cannot_forge_history');
 for(const invalid of [{id:'../x'},{provider:'other'},{kind:'arbitrary-text'},{remaining:101},{at:new Date(Date.now()+3600000).toISOString()},{creditCount:'3'},{window:'7D'},{addedCount:-1}])assert.equal((await request(a,'POST',payload([{...event(8),...invalid}]))).status,400);
 pass('eight_invalid_event_shapes_rejected');
 assert.equal((await request(a,'POST',payload(Array.from({length:51},(_,i)=>event(i+100))))).status,400);pass('oversized_event_batch_rejected');
 const stale={...event(9),at:new Date(Date.now()-8*86400000).toISOString()};assert.equal((await request(a,'POST',payload([stale]))).status,200);data=await(await request(a)).json();assert.equal(data.notifications.length,1);pass('history_older_than_7_days_excluded');
 const usageFile=path.join(root,'mobile','accounts',a.id,'usage.php');const stored=JSON.parse(fs.readFileSync(usageFile,'utf8').slice(prefix.length));stored.notifications.push(stale);fs.writeFileSync(usageFile,prefix+JSON.stringify(stored));
 data=await(await request(a)).json();assert.equal(data.notifications.length,1);assert.equal(data.usage.notifications.length,1);pass('expired_records_never_leak_in_nested_usage');
 const cleaner=path.join(__dirname,'../tools/mobile/cleanup-history.php'),before=fs.readFileSync(usageFile,'utf8');
 // Scope this assertion to this run's synthetic account, not earlier runs' retained evidence.
 let cleanup=spawnSync(php,['-n',cleaner,'--root='+path.dirname(usageFile)],{encoding:'utf8',windowsHide:true});assert.equal(cleanup.status,0,cleanup.stderr);assert.equal(JSON.parse(cleanup.stdout).removed,1);assert.equal(fs.readFileSync(usageFile,'utf8'),before);pass('cleanup_dry_run_changes_no_usage');
 cleanup=spawnSync(php,['-n',cleaner,'--root='+path.dirname(usageFile),'--apply'],{encoding:'utf8',windowsHide:true});assert.equal(cleanup.status,0,cleanup.stderr);const cleaned=JSON.parse(fs.readFileSync(usageFile,'utf8').slice(prefix.length));assert.equal(cleaned.notifications.length,1);delete stored.notifications;delete cleaned.notifications;assert.deepEqual(cleaned,stored);pass('scheduled_cleanup_preserves_snapshot_and_identity');
 for(let batch=0;batch<3;batch++)assert.equal((await request(a,'POST',payload(Array.from({length:50},(_,i)=>event(100+batch*50+i))))).status,200);
 data=await(await request(a)).json();assert.equal(data.notifications.length,100);pass('history_retention_capped_at_100');
 assert.equal((await request(legacy,'POST',upload,'personal')).status,200);data=await(await request(legacy,'GET',undefined,'personal')).json();assert.equal(data.notifications.length,1);assert.equal(data.account,'');pass('legacy_owner_pairing_compatible');
 const disabled=payload();disabled.providers[0].enabled=false;assert.equal((await request(legacy,'POST',disabled,'personal')).status,200);data=await(await request(legacy,'GET',undefined,'personal')).json();assert.equal(data.usage.resetCredits,null);pass('disabled_credits_not_exposed');
 for(const p of ['notification-history.php','accounts/'+a.id+'/usage.php']){r=await fetch(base+'/mobile/'+p);assert.ok(!(await r.text()).includes('ResetCreditsAdded'));}pass('direct_private_history_not_readable');
 const visibleB=payload([{...event(3),creditCount:7,addedCount:4}]);visibleB.resetCredits.available=7;assert.equal((await request(b,'POST',visibleB)).status,200);
 browser=await chromium.launch({channel:'chrome',headless:true});const context=await browser.newContext({viewport:{width:390,height:844}}),page=await context.newPage(),errors=[];
 page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base+'/mobile/#key='+b.id+'.'+b.reader);await page.locator('.provider').waitFor();await page.getByRole('button',{name:'通知',exact:true}).click();
 await page.locator('#history li').filter({hasText:'新增 4 張重置券'}).waitFor();pass('real_PHP_history_renders_on_phone');
 await page.screenshot({path:path.join(output,'php-phone-history-fixture.png'),fullPage:true});
 await page.reload();await page.locator('.provider').waitFor();await page.getByRole('button',{name:'通知',exact:true}).click();await page.locator('#history li').filter({hasText:'新增 4 張重置券'}).waitFor();pass('real_PHP_history_survives_phone_reopen');
 await page.goto(base+'/mobile/#key='+a.id+'.'+a.reader);await page.locator('.provider').waitFor();await page.getByRole('button',{name:'通知',exact:true}).click();
 assert.equal(await page.locator('#history li').filter({hasText:'可用 7 張'}).count(),0);assert.ok((await page.locator('#history li').count())>0);pass('phone_account_switch_removes_other_member_history');
 await page.goto(base+'/mobile/#key='+a.id+'.'+b.reader);await page.locator('#pairing').waitFor();assert.equal(await page.locator('.provider').count(),0);assert.equal(await page.locator('#history li').filter({hasText:'張'}).count(),0);pass('wrong_reader_cannot_fall_back_to_prior_account_cookie');
 for(const invalid of ['invalid','']){await page.goto(base+'/mobile/#key='+invalid);await page.getByText('配對碼格式錯誤',{exact:true}).waitFor();assert.equal(await page.locator('.provider').count(),0);assert.equal(new URL(page.url()).hash,'');}pass('invalid_and_empty_QR_do_not_restore_old_member');
 assert.deepEqual(errors,[]);pass('real_backend_phone_has_no_script_errors');await context.close();
 console.log('ALL PASS checks='+results.length);
})().catch(e=>{results.push('FAIL '+e.message);console.error(e);process.exitCode=1;}).finally(async()=>{
 if(browser)await browser.close();
 if(server&&server.exitCode===null){server.kill();await new Promise(r=>server.once('exit',r));}
 fs.writeFileSync(path.join(output,'notification-api-tests.txt'),results.join('\n')+'\n');
 fs.writeFileSync(path.join(output,'php-server.log'),serverLog);
});
