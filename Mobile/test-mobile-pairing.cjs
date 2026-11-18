const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
const output=process.argv[2];if(!output)throw Error('Evidence directory required');fs.mkdirSync(output,{recursive:true});
const origin='https://fixture.test',base=origin+'/personal/',token='a'.repeat(64),cookieName='__Secure-UsageToolsReader',csrf='c'.repeat(64),results=[];
const fixture=()=>({session:true,csrf,publicKey:'fixture',usage:{schemaVersion:2,fixture:true,themeId:'deep-night',receivedAt:new Date().toISOString(),providers:[{name:'Codex',enabled:true,state:'Fresh',plan:'Pro',syncedAt:new Date().toISOString(),windows:[{label:'7D',used:16,resetsAt:new Date(Date.now()+86400000).toISOString()}]},{name:'Claude Code',enabled:false,state:'Disabled',plan:null,syncedAt:null,windows:[]}]}});
function pass(name){results.push('PASS '+name);console.log(results.at(-1));}
async function prepare(context,state={}) {
 await context.route('**/*',async route=>{
  const url=new URL(route.request().url());if(url.origin!==origin)return route.abort();
  if(url.pathname.endsWith('/api.php')){
   const req=route.request(),headers=req.headers(),bearer=headers.authorization,cookie=headers.cookie||'';
   const authorized=bearer?'Bearer '+token===bearer:cookie.split(';').some(c=>c.trim()===cookieName+'='+token);
   if(!authorized)return route.fulfill({status:401,json:{error:'unauthorized'}});
   if(req.method()==='DELETE'){
    if(state.failLogout)return route.fulfill({status:503,json:{error:'fixture_failure'}});
    if(!bearer){assert.equal(headers['x-usage-csrf'],csrf);assert.equal(headers.origin,origin);}
    return route.fulfill({json:{ok:true},headers:{'Set-Cookie':cookieName+'=; Max-Age=0; Path=/personal/; Secure; HttpOnly; SameSite=Strict'}});
   }
   return route.fulfill({json:fixture(),headers:{'Set-Cookie':cookieName+'='+token+'; Max-Age=15552000; Path=/personal/; Secure; HttpOnly; SameSite=Strict'}});
  }
  const relative=url.pathname.replace(/^\/personal\//,'')||'index.html',file=path.join(__dirname,relative==='manifest.php'?'manifest.webmanifest':relative);
  if(!file.startsWith(__dirname+path.sep)||!fs.existsSync(file)||!fs.statSync(file).isFile())return route.fulfill({status:404,body:''});
  return route.fulfill({body:fs.readFileSync(file),contentType:file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':file.endsWith('.png')?'image/png':file.endsWith('.jpg')?'image/jpeg':'text/html'});
 });
}
(async()=>{
 const browser=await chromium.launch({channel:'chrome',headless:true});
 try{
  const options={viewport:{width:390,height:844},serviceWorkers:'block'};
  const safari=await browser.newContext(options);await prepare(safari);const page=await safari.newPage();
  await page.goto(base);await page.getByText('尚未配對此裝置',{exact:true}).waitFor();assert.equal(await page.locator('#pairing').isVisible(),true);pass('anonymous_requires_pairing_not_fake_connecting');
  await page.goto(base+'#key='+token);await page.getByText('84%',{exact:true}).waitFor();assert.ok(!page.url().includes(token));pass('qr_bootstrap_strips_token_from_address');
  const cookies=await safari.cookies(base),reader=cookies.find(c=>c.name===cookieName);assert.ok(reader&&reader.secure&&reader.httpOnly&&reader.sameSite==='Strict'&&reader.path==='/personal/');pass('secure_httponly_strict_scoped_cookie');
  const installed=await browser.newContext(options),state={};await installed.addCookies(cookies);await prepare(installed,state);const home=await installed.newPage();
  await home.goto(base);await home.getByText('84%',{exact:true}).waitFor();assert.equal(await home.evaluate(()=>localStorage.getItem('usagetools-personal-key')),null);assert.equal(await home.locator('#pairing').isVisible(),false);pass('empty_localstorage_cookie_transfer_reads_usage');
  assert.equal(await home.evaluate(()=>document.cookie.includes('__Secure-UsageToolsReader')),false);pass('javascript_cannot_read_pairing_cookie');
  assert.equal(await home.locator('.app-title img').getAttribute('src'),'pulse-icon-192-v1.png?v=2026.11.18.2');assert.equal(await home.locator('.app-title img').evaluate(i=>i.complete&&i.naturalWidth===192),true);assert.equal(await home.locator('link[rel="apple-touch-icon"]').getAttribute('href'),'pulse-icon-180-v1.png?v=2026.11.18.2');pass('new_icon_filename_and_image_loaded');
  const manifest=JSON.parse(fs.readFileSync(path.join(__dirname,'manifest.webmanifest'),'utf8'));assert.equal(manifest.start_url,'./');assert.ok(manifest.icons.every(i=>i.src.startsWith('pulse-icon-')&&!i.src.includes(token)));pass('manifest_has_new_icons_no_private_key');
  for(let i=0;i<3;i++){await home.reload();await home.getByText('84%',{exact:true}).waitFor();assert.equal(await home.locator('#pairing').isVisible(),false);}pass('repeated_reopen_retains_pairing');
  await home.screenshot({path:path.join(output,'home-cookie-transfer-390.png'),fullPage:true});
  state.failLogout=true;await home.getByRole('button',{name:'設定',exact:true}).click();await home.getByRole('button',{name:'解除本機配對',exact:true}).click();await home.getByText('解除配對未完成，請連線後重試',{exact:true}).waitFor();assert.ok((await installed.cookies(base)).some(c=>c.name===cookieName));pass('failed_logout_does_not_claim_revocation');
  state.failLogout=false;await home.getByRole('button',{name:'解除本機配對',exact:true}).click();await home.getByText('已解除配對',{exact:true}).waitFor();assert.ok(!(await installed.cookies(base)).some(c=>c.name===cookieName));await home.reload();await home.getByText('尚未配對此裝置',{exact:true}).waitFor();pass('logout_revokes_cookie_and_reopen_is_unauthorized');
  const restricted=await browser.newContext(options);await prepare(restricted);await restricted.addInitScript(()=>{Storage.prototype.getItem=()=>{throw Error('Storage disabled')};Storage.prototype.setItem=()=>{throw Error('Storage disabled')};});const blocked=await restricted.newPage(),errors=[];blocked.on('pageerror',e=>errors.push(e.message));await blocked.goto(base+'#key='+token);await blocked.getByText('84%',{exact:true}).waitFor();await blocked.reload();await blocked.getByText('84%',{exact:true}).waitFor();assert.deepEqual(errors,[]);pass('blocked_localstorage_cookie_pairing_still_works');
  await safari.close();await installed.close();await restricted.close();fs.writeFileSync(path.join(output,'pairing-ui-tests.txt'),results.join('\n')+'\nALL PASS checks='+results.length+'\n');console.log('ALL PASS checks='+results.length);
 }finally{await browser.close();}
})().catch(e=>{console.error(e);fs.writeFileSync(path.join(output,'pairing-ui-tests.txt'),results.join('\n')+'\nFAIL '+e.message+'\n');process.exitCode=1;});
