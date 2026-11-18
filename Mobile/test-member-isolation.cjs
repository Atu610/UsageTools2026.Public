const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
const base=process.argv[2],output=process.argv[3],url=new URL('api.php',base);
if(url.origin!=='https://usage.example.invalid'||!['/staging/mobile-2026.10.1.3/api.php','/mobile/api.php'].includes(url.pathname)||!output)throw Error('Explicit deployment test URL required');
fs.mkdirSync(output,{recursive:true});const results=[],hash=s=>crypto.createHash('sha256').update(s).digest('hex');
const pass=name=>{results.push('PASS '+name);console.log(results.at(-1));};
function identity(){const ec=crypto.createECDH('prime256v1');ec.generateKeys();const writer=crypto.randomBytes(32).toString('hex'),reader=crypto.randomBytes(32).toString('hex');return {writer,reader,id:hash(writer),publicKey:ec.getPublicKey().toString('base64url')};}
const a=identity(),b=identity();
const headers=(user,role='writer')=>({Authorization:'Bearer '+user[role],'X-Usage-Account':user.id});
async function request(method='GET',op='usage',h={},body){return fetch(url+'?op='+op,{method,headers:{...h,...(body?{'Content-Type':'application/json'}:{})},body:body?JSON.stringify(body):undefined,redirect:'error',signal:AbortSignal.timeout(15000)});}
const register=u=>request('POST','register',headers(u),{readHash:hash(u.reader),publicKey:u.publicKey});
function payload(used){const now=new Date().toISOString();return {schemaVersion:2,themeId:'matrix',sentAt:now,secret:'DO_NOT_STORE@example.com C:\\private\\auth.json',providers:[{name:'Codex',enabled:true,state:'Fresh',plan:'Plus',syncedAt:now,windows:[{label:'5H',used,resetsAt:new Date(Date.now()+3600000).toISOString()},{label:'7D',used:used+1,resetsAt:new Date(Date.now()+86400000).toISOString()}]},{name:'Claude Code',enabled:false,state:'Disabled',plan:null,syncedAt:null,windows:[]}]};}
let browser;
(async()=>{
 assert.equal((await request()).status,401);pass('anonymous_denied');
 for(const [label,u] of [['A',a],['B',b]]){let r=await register(u);assert.equal(r.status,201);assert.equal((await r.json()).account,u.id);pass('register_independent_'+label);}
 for(const r of await Promise.all(Array.from({length:4},()=>register(a))))assert.equal(r.status,200);pass('concurrent_idempotent_registration');
 let r=await request('POST','register',headers(a),{readHash:hash(b.reader),publicKey:b.publicKey});assert.equal(r.status,409);pass('reregister_cannot_replace_credentials');
 r=await request('POST','register',headers(a),{readHash:hash(a.writer),publicKey:a.publicKey});assert.equal(r.status,400);pass('reader_writer_separation_required');
 for(const [u,used] of [[a,11],[b,71]]){r=await request('POST','usage',headers(u),payload(used));assert.equal(r.status,200);}
 pass('independent_usage_uploaded');
 for(const [u,used] of [[a,11],[b,71]]){r=await request('GET','usage',headers(u,'reader'));assert.equal(r.status,200);const d=await r.json();assert.equal(d.account,u.id);assert.equal(d.usage.providers[0].windows[0].used,used);assert.ok(!JSON.stringify(d).includes('DO_NOT_STORE'));assert.ok(!JSON.stringify(d).includes(u.reader)&&!JSON.stringify(d).includes(u.writer));}
 pass('isolated_usage_and_privacy_whitelist');
 for(const [u,target] of [[a,b],[b,a]])for(const role of ['reader','writer'])for(const [method,op,body] of [['GET','usage'],['GET','subscriptions'],['POST','usage',payload(99)],['DELETE','subscriptions',{endpoint:'https://fcm.googleapis.com/fixture'}]]){r=await request(method,op,{...headers(u,role),'X-Usage-Account':target.id},body);assert.equal(r.status,401);}
 pass('16_cross_account_read_write_subscription_attacks_denied');
 for(const invalid of ['../personal',a.id+'/../'+b.id,'%2e%2e',a.id.toUpperCase()]){r=await request('GET','usage',{...headers(a,'reader'),'X-Usage-Account':invalid});assert.equal(r.status,401);}pass('tenant_path_traversal_rejected');
 assert.equal((await request('POST','usage',headers(a,'reader'),payload(99))).status,403);pass('reader_cannot_write_usage');
 assert.equal((await request('GET','usage',headers(a))).status,405);pass('writer_cannot_open_reader_view');
 assert.equal((await request('GET','usage',{Authorization:'Bearer '+a.reader})).status,401);pass('missing_account_rejected');
 r=await request('GET','usage',headers(a,'reader'));const d=await r.json(),rawCookie=r.headers.get('set-cookie'),cookie=rawCookie.split(';')[0];
 assert.ok(rawCookie.includes('Secure')&&rawCookie.includes('HttpOnly')&&rawCookie.includes('SameSite=Strict'));assert.ok(rawCookie.includes('path='+new URL('.',url).pathname));pass('secure_scoped_cookie');
 r=await request('GET','usage',{Cookie:cookie});assert.equal((await r.json()).account,a.id);pass('cookie_only_restore_keeps_identity');
 r=await request('GET','usage',{Cookie:cookie,'X-Usage-Account':b.id});assert.equal((await r.json()).account,a.id);pass('cookie_cannot_be_retargeted_by_header');
 r=await request('GET','usage',{Cookie:cookie,...headers(b,'reader'),Authorization:'Bearer '+a.reader});assert.equal(r.status,401);pass('bad_explicit_pairing_not_masked_by_cookie');
 assert.equal((await request('GET','usage',{Cookie:'__Secure-UsageToolsReader='+a.id+'.'+a.writer})).status,401);pass('writer_not_accepted_as_reader_cookie');
 const sub=u=>({endpoint:'https://fcm.googleapis.com/fcm/send/fixture-'+u.id,keys:{p256dh:u.publicKey,auth:crypto.randomBytes(16).toString('base64url')},preferences:{codex:true,claude:false,low:true,reset:true,images:true}}),sa=sub(a),sb=sub(b);
 assert.equal((await request('POST','subscriptions',{Cookie:cookie,Origin:url.origin},sa)).status,403);
 assert.equal((await request('POST','subscriptions',{Cookie:cookie,Origin:'https://evil.test','X-Usage-CSRF':d.csrf},sa)).status,403);pass('cookie_csrf_and_origin_enforced');
 assert.equal((await request('POST','subscriptions',{Cookie:cookie,Origin:url.origin,'X-Usage-CSRF':d.csrf},sa)).status,200);
 assert.equal((await request('POST','subscriptions',headers(b,'reader'),sb)).status,200);pass('each_member_registers_own_notification_target');
 for(const [u,s] of [[a,sa],[b,sb]]){r=await request('GET','subscriptions',headers(u));const list=(await r.json()).subscriptions;assert.equal(list.length,1);assert.equal(list[0].endpoint,s.endpoint);}pass('desktop_can_only_retrieve_own_push_targets');
 r=await request('DELETE','subscriptions',headers(a,'reader'),sb);assert.equal(r.status,200);r=await request('GET','subscriptions',headers(b));assert.equal((await r.json()).subscriptions.length,1);pass('deleting_other_endpoint_does_not_affect_other_member');
 for(const relative of ['server-config.php','accounts/'+a.id+'/config.php','accounts/'+a.id+'/usage.php','accounts/'+a.id+'/subscriptions.php']){r=await fetch(new URL(relative,base),{redirect:'error'});assert.ok(r.status===200||r.status===403||r.status===404);assert.ok(!(await r.text()).includes(a.publicKey));}pass('protected_storage_not_publicly_readable');
 r=await fetch(new URL('manifest.php',base));assert.equal((await r.json()).id,new URL('.',url).pathname);pass('pwa_identity_is_deployment_scoped');
 browser=await chromium.launch({headless:true,channel:'chrome'});const context=await browser.newContext({viewport:{width:390,height:844}}),page=await context.newPage();
 await page.goto(base+'#key='+a.id+'.'+a.reader);await page.locator('.remaining').first().filter({hasText:'89%'}).waitFor();assert.equal(await page.locator('[data-provider=claude]').count(),0);assert.equal(new URL(page.url()).hash,'');pass('member_A_phone_own_usage_disabled_provider_hidden');
 await page.screenshot({path:path.join(output,'member-A-phone-fixture.png'),fullPage:true});
 const cookies=await context.cookies(),home=await browser.newContext({viewport:{width:390,height:844}});await home.addCookies(cookies);const homePage=await home.newPage();await homePage.goto(base);await homePage.locator('.remaining').first().filter({hasText:'89%'}).waitFor();pass('new_home_screen_context_restores_own_cookie');
 await page.goto(base+'#key='+b.id+'.'+b.reader);await page.locator('.remaining').first().filter({hasText:'29%'}).waitFor();assert.ok(!(await page.locator('#providers').innerText()).includes('89%'));pass('explicit_account_switch_replaces_previous_usage');
 await page.screenshot({path:path.join(output,'member-B-phone-fixture.png'),fullPage:true});
 await page.goto(base+'#key='+b.id+'.'+a.reader);await page.locator('#pairing').waitFor({state:'visible'});assert.equal(await page.locator('#providers').textContent(),'');pass('invalid_pairing_never_falls_back_to_previous_member');
 await home.close();await context.close();
 for(const [u,s] of [[a,sa],[b,sb]])assert.equal((await request('DELETE','subscriptions',headers(u),s)).status,200);pass('synthetic_push_subscriptions_removed');
 console.log('ALL PASS checks='+results.length);
})().catch(e=>{results.push('FAIL '+e.message);console.error(e);process.exitCode=1;}).finally(async()=>{if(browser)await browser.close();fs.writeFileSync(path.join(output,'member-isolation-tests.txt'),results.join('\n')+'\n');fs.writeFileSync(path.join(output,'synthetic-account-ids.json'),JSON.stringify({base,accounts:[a.id,b.id]}));});
