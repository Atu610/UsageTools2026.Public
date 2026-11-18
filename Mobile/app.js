'use strict';
const $ = id => document.getElementById(id), scopePath = new URL('.',location.href).pathname,
 storage = scopePath==='/personal/' ? 'usagetools-personal-' : 'usagetools-'+encodeURIComponent(scopePath)+'-';
const validPairingKey = value => /^(?:[a-f0-9]{64}\.)?[a-f0-9]{64}$/.test(value);
function readStored(name) { try { return localStorage.getItem(storage + name); } catch { return null; } }
function writeStored(name, value) { try { localStorage.setItem(storage + name, value); return true; } catch { return false; } }
function removeStored(name) { try { localStorage.removeItem(storage + name); } catch {} }
function stored(name, fallback) { try { return JSON.parse(readStored(name)) ?? fallback; } catch { return fallback; } }
const pairingFromUrl = new URLSearchParams(location.hash.slice(1)).get('key');
let key = pairingFromUrl ?? readStored('key') ?? '';
let explicitPairing = pairingFromUrl !== null;
let invalidPairing = explicitPairing && !validPairingKey(key);
if(key&&!validPairingKey(key))key='';
let requestedProvider = new URLSearchParams(location.hash.slice(1)).get('provider');
let requestedEvent = new URLSearchParams(location.hash.slice(1)).get('event');
let requestedNews = new URLSearchParams(location.hash.slice(1)).get('news');
if (key || explicitPairing) { if(key)writeStored('key', key); history.replaceState(null, '', location.pathname + location.search); }
const prefs = Object.assign({codex:true, claude:true, low:true, reset:true, milestones:false, images:true, sound:false, resetNews:false}, stored('preferences', {}));
const appearance = Object.assign({follow:true, theme:'deep-night'}, stored('appearance', {}));
let vapid, busy = false, registration, current = null, offline = false, paired = false, pairingRequired = false, csrf = '';
const seen = {}, events = [];
removeStored('events');removeStored('seen');
let observedHistory=null;
const text = (node, value) => node.textContent = value;
const themes = {
 'deep-night':['深夜','#151719','#edf3f0','#abb5b0','#39413e','#343c39','#68e8c5','#f08f7c','#202426'],
 day:['白天','#f3f7f6','#18212a','#64717b','#c4ced4','#d9e0e3','#166dcc','#b85520','#ffffff'],
 classic:['經典','#08201f','#eafbf8','#81a6a6','#23555b','#183d40','#2fb7d7','#4fe6c6','#0d2a2a'],
 aquarium:['水族箱','#06233b','#e4f8ff','#84bed2','#157c98','#0b4860','#5fcbff','#2ef3c0','#082d4a'],
 cloud_observation:['天衡星盤','#0b0a12','#f3deb0','#b89a68','#8b6a3a','#211b27','#e8c878','#b99254','#12101a'],
 black_hole:['黑洞','#050505','#f7eee8','#a47c6b','#3a241c','#201614','#ffb14a','#ff8c66','#0a0706'],
 prism_arcade:['稜鏡街機','#120326','#fff7ff','#bda7ff','#6931b6','#2f1548','#00e7ff','#ff46d8','#1b0737'],
 matrix:['經典版','#14161c','#eee5d5','#b4aa9a','#907343','#35363d','#78aee8','#e1844b','#1b1d24'],
 newspaper:['報紙','#f5f0e5','#161616','#5c5c5c','#2c2c2c','#d4ccbb','#111111','#3d3d3d','#fffdf4'],
 win95:['Windows 95','#c0c0c0','#000000','#303030','#404040','#a0a0a0','#000080','#800000','#dedede'],
 world_cup:['賽博朋克','#0b0821','#f7f8ff','#a7b4ff','#ff2bd6','#23143a','#00e7ff','#ff88d6','#13102a'],
 cartoon_platformer:['卡通平台','#fff9e6','#111111','#4a3423','#111111','#e0d3a0','#166dcc','#ba2525','#ffe66d']
};
function applyTheme() {
 const requested = appearance.follow ? current?.usage?.themeId : appearance.theme;
 const id = Object.hasOwn(themes, requested || '') ? requested : 'deep-night', theme = themes[id];
 document.body.dataset.theme = id;
 ['bg','text','muted','border','track','codex','claude','surface'].forEach((name, i) => document.documentElement.style.setProperty('--'+name, theme[i+1]));
 const light = ['day','newspaper','win95','cartoon_platformer'].includes(id);
 document.documentElement.style.colorScheme = light ? 'light' : 'dark';
 document.documentElement.style.setProperty('--danger', light ? '#bf3030' : '#ff7373');
 document.documentElement.style.setProperty('--warning', light ? '#9b5b00' : '#e9b65d');
 document.querySelector('meta[name=theme-color]').content = theme[1];
 text($('themeStatus'), theme[0] + (appearance.follow ? Object.hasOwn(themes,requested||'') ? ' · 跟隨電腦' : ' · 電腦主題尚未同步' : ' · 手機設定'));
 $('theme').disabled = appearance.follow;
}
function date(value) { const d = new Date(value); if (!value || !Number.isFinite(d.getTime())) return '未提供'; return new Intl.DateTimeFormat('zh-TW',{year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hour12:false}).format(d); }
function countdown(value) { const minutes = Math.ceil((new Date(value)-Date.now())/60000); if (!value || !Number.isFinite(minutes)) return ''; if (minutes<=0) return '等待官方確認重置'; const days=Math.floor(minutes/1440), hours=Math.floor(minutes%1440/60), mins=minutes%60; return '還有 '+(days?days+' 天 ':'')+(hours?hours+' 小時 ':'')+mins+' 分'; }
function element(tag, className, value) { const node=document.createElement(tag); if(className)node.className=className; if(value!==undefined)text(node,value); return node; }
function log(message) { events.unshift({message,at:new Date().toISOString()}); events.splice(20); renderHistory(); }
function notificationMessage(e) {
 const name=e.provider==='codex'?'Codex':e.provider==='claude'?'Claude Code':null;
 if(!name)return null;
 if(e.kind==='ResetCreditsAdded')return name+' '+(e.addedCount>0?'新增 '+e.addedCount+' 張'+(e.provider==='claude'&&typeof creditKindName==='function'?creditKindName(e.creditKind):'重置券')+'；':'')+'目前可用 '+e.creditCount+' 張';
 if(!['UsageMilestone','LowRemaining','UsageReset'].includes(e.kind)||!/^\d{1,5}[HDM]$/.test(e.window))return null;
 return name+' '+e.window+(typeof e.pool==='string'&&e.pool?' '+e.pool:'')+(e.kind==='UsageReset'?' 額度已重置；':' ')+(e.kind==='LowRemaining'?'低額度警報；':'')+'剩餘 '+e.remaining+'%';
}
function syncedHistory() {
 const ids=new Set();
 return (Array.isArray(current?.notifications)?current.notifications:[]).filter(e=>{
  if(!e||typeof e.id!=='string'||ids.has(e.id)||!Number.isInteger(e.remaining)||e.remaining<0||e.remaining>100||new Date(e.at)<=Date.now()-7*86400000||!Number.isFinite(new Date(e.at).getTime()))return false;
  ids.add(e.id);return true;
 }).map(e=>({...e,message:notificationMessage(e)})).filter(e=>e.message).slice(0,100);
}
function renderHistory() {
 const history=syncedHistory(),combined=history.concat(events.filter(e=>!history.some(h=>h.id&&h.id===e.id))).sort((a,b)=>new Date(b.at)-new Date(a.at));
 const visible = combined.filter(e => { const name=String(e.message).startsWith('Claude Code')?'Claude Code':String(e.message).startsWith('Codex')?'Codex':null;if(!name)return true;const id=name==='Codex'?'codex':'claude',provider=current?.usage?.providers?.find(p=>p.name===name);return prefs[id]&&provider?.enabled; });
 const arrivals=visible.filter(e=>e.id&&observedHistory&&!observedHistory.has(e.id)&&Date.now()-new Date(e.at)<120000);
 if(current)observedHistory=new Set(history.map(e=>e.id));
 for(const id of ['history','recent']) {
  $(id).replaceChildren();
  for(const e of visible.slice(0,id==='recent'?3:100)) {
   if(e.kind)$(id).append(eventCard(e,arrivals.includes(e)));
   else{const li=element('li','',e.message);li.append(element('time','',date(e.at)));$(id).append(li);}
  }
  if(!visible.length)$(id).append(element('li','empty-events','尚無通知紀錄'));
 }
 text($('historyCount'),visible.length+' 則');
 const audible=arrivals.find(e=>e.kind==='LowRemaining'?prefs.low:e.kind==='UsageMilestone'?prefs.milestones:prefs.reset);
 if($('eventDialog').open&&$('eventDialog').dataset.event&&!visible.some(e=>e.id===$('eventDialog').dataset.event))clearEventDetail();
 if(audible&&!offline)playEventSound(audible.kind);
 const credit=current?.usage?.resetCredits,enabled=prefs.codex&&current?.usage?.providers?.some(p=>p.name==='Codex'&&p.enabled);
 const summary=$('creditSummary');summary.hidden=!enabled||!Number.isInteger(credit?.available);
 if(!summary.hidden){summary.replaceChildren(element('span','','Codex 重置券：可用 '+credit.available+' 張'+(credit.stale?'（快取）':'')+' · 更新 '+date(credit.syncedAt)));summary.append(creditExpiryList(credit.expirations,credit.available));}
}
async function api(op='usage',method='GET',body) {
 const parts=key.split('.'), token=parts.at(-1), account=parts.length===2?parts[0]:'';
 const response=await fetch('api.php?op='+op,{method,cache:'no-store',credentials:'same-origin',headers:{...(key?{Authorization:'Bearer '+token}:{}),...(account?{'X-Usage-Account':account}:{}),...(csrf&&method!=='GET'?{'X-Usage-CSRF':csrf}:{}),...(body?{'Content-Type':'application/json'}:{})},body:body?JSON.stringify(body):undefined,signal:AbortSignal.timeout(12000)});
 if(!response.ok){const error=new Error(response.status===401?'尚未配對此裝置':'連線失敗 ('+response.status+')');error.status=response.status;throw error;}
 const data=await response.json();if(typeof data.csrf==='string')csrf=data.csrf;return data;
}
function windowsFor(p) {
 const windows=Array.isArray(p.windows)?p.windows:[];
 // Older desktop versions sent a nonexistent 5H placeholder for weekly-only Pro.
 return windows.filter((w,i) => w && typeof w.label==='string' && windows.findIndex(other=>other?.label===w.label)===i &&
   !(p.name!=='Claude Code' && w.used==null && !w.resetsAt && windows.some(other=>other?.label!==w.label && other.used!=null))).slice(0,2);
}
function showProvider(p,online) {
 const id=p.name==='Codex'?'codex':'claude', section=element('article','provider '+id);
 section.dataset.provider=id;
 const header=element('header','provider-header'), img=element('img'); img.src=id+'-icon.png?v=2026.11.18.1'; img.alt='';
 header.append(img,element('h2','',p.name),element('span','plan',p.plan||'方案未提供')); section.append(header);
 const age=p.syncedAt?(Date.now()-new Date(p.syncedAt))/60000:Infinity, fresh=online&&p.state==='Fresh'&&age<=3&&age>=-1;
 section.classList.toggle('stale',!fresh);
 const windows=windowsFor(p), grid=element('div','windows'+(windows.length===1?' single':''));
 for(const w of windows) {
  const maxAge=id==='claude'?(w.label==='5H'?300:10080):10;
  const valid=Number.isFinite(w.used)&&w.used>=0&&w.used<=100&&age<maxAge&&age>=-1&&(!w.resetsAt||new Date(w.resetsAt)>new Date());
  const remaining=valid?Math.round(100-w.used):null, row=element('div','window'); row.dataset.window=w.label;
  if(fresh&&remaining!==null)row.classList.add(remaining<=20?'alarm':remaining<=30?'caution':'normal');
  row.append(element('div','window-label',w.label==='5H'?'5 小時':w.label==='7D'?'每週 7D':w.label));
  const q=element('div','quota'); q.append(element('strong','remaining'+(remaining===null?' unknown':''),remaining===null?'尚未同步':remaining+'%'),element('small','',remaining===null?'':'剩餘')); row.append(q);
  const bar=element('div','bar'),fill=element('div','fill'); fill.style.width=(remaining??0)+'%'; bar.append(fill);
  bar.setAttribute('role','progressbar');bar.setAttribute('aria-label',p.name+' '+w.label+' 剩餘額度');bar.setAttribute('aria-valuemin','0');bar.setAttribute('aria-valuemax','100');if(remaining!==null)bar.setAttribute('aria-valuenow',String(remaining));else bar.setAttribute('aria-valuetext','尚未同步');
  row.append(bar,element('div','meta',valid?(fresh?'':'快取 · ')+'已使用 '+Math.round(w.used)+'%':'已過期／未同步'));
  const resetText=w.resetsAt?'重置 '+date(w.resetsAt):p.name==='Claude Code'&&w.label==='5H'&&w.used===0&&valid?'從發送訊息時開始':'重置時間未提供';
  row.append(element('div','reset',resetText));const counter=element('div','countdown',countdown(w.resetsAt));if(w.resetsAt)counter.dataset.reset=w.resetsAt;row.append(counter);grid.append(row);
  if(fresh&&valid&&!Array.isArray(current?.notifications)) { const identity=id+':'+w.label,threshold=[5,10,15,20].find(t=>remaining<=t),marker=(w.resetsAt||'')+':'+(threshold||0); if(threshold&&prefs.low&&seen[identity]!==marker){log(p.name+' '+w.label+' 剩餘 '+remaining+'%');seen[identity]=marker;} }
 }
 if(!windows.length)grid.append(element('p','hint','額度資料尚未提供'));
 const claudeSummary=id==='claude'&&typeof claudeSummaryButton==='function'?claudeSummaryButton(online):null;
 section.append(grid);if(claudeSummary)section.append(claudeSummary);
 section.append(element('p','freshness',(fresh?current?.usage?.fixture?'示例資料更新 ':'官方資料更新 ':p.syncedAt?'資料停在 ':'官方資料未提供 · ')+date(p.syncedAt)+(fresh||!p.syncedAt?'':' · 快取／同步未完成')));
 if(id==='codex') {
  if(current?.usage?.planConfirmed)section.append(element('p','plan-evidence','方案金額：使用者確認 · 官方回傳 Pro'));
  const c=current?.usage?.creditBalance, panel=element('div','credit-balance');
  const valid=typeof c?.balance==='number'&&Number.isFinite(c.balance)&&c.balance>=0&&c.balance<=1e12;
  const value=c?.unlimited===true?'不限額':valid?Math.floor(c.balance).toLocaleString('zh-TW'):'未提供';
  const age=c?.syncedAt?(Date.now()-new Date(c.syncedAt))/60000:Infinity, live=online&&!c?.stale&&age>=-1&&age<=3;
  panel.append(element('span','credit-symbol','C'),element('span','credit-label','可用點數'),element('strong','credit-value',value));
  panel.append(element('small','credit-time',c?(live?'官方同步 ':'快取 · ')+date(c.syncedAt):'等待官方點數資料'));
  panel.classList.toggle('cached',!live);section.append(panel);
 }
 return section;
}
function render(data,forceOffline=false) {
 if(!data)return;current=data;offline=forceOffline;const usage=data.usage,age=Date.now()-new Date(usage?.receivedAt),online=!forceOffline&&age>=-60000&&age<90000;
 text($('connection'),usage?.fixture?'本機示例資料':online?'電腦已連線':'電腦離線／尚未同步');$('connection').parentElement.classList.toggle('off',!online);text($('updated'),usage?.receivedAt?'同步 '+date(usage.receivedAt):'尚未同步');$('providers').replaceChildren();
 $('notify').disabled=!!usage?.fixture;$('test').disabled=!!usage?.fixture;
 for(const p of usage?.providers||[]) {
  if(!['Codex','Claude Code'].includes(p.name))continue;
  const id=p.name==='Codex'?'codex':'claude';text($(id+'Status'),p.enabled?'電腦資料讀取已開啟':'電腦資料讀取已關閉');
  if(p.enabled&&prefs[id])$('providers').append(showProvider(p,online));
 }
 $('empty').hidden=$('providers').childElementCount!==0;
 applyTheme();renderHistory();if(typeof refreshClaudeDetails==='function')refreshClaudeDetails();
 if(requestedEvent){const id=requestedEvent;requestedEvent=null;requestedProvider=null;history.replaceState(history.state,'',location.pathname+location.search);openNotification(id);}
 else if(requestedProvider){openProvider(requestedProvider);requestedProvider=null;}
}
async function load(force=false) {
 if(invalidPairing){paired=false;pairingRequired=true;current=null;clearHistory();$('providers').replaceChildren();$('pairing').hidden=false;text($('connection'),'配對碼格式錯誤');return;}
 if(busy||pairingRequired&&!key&&!force)return;
 const attemptKey=key;busy=true;$('refresh').disabled=true;
 try {
  let data;
  try{data=await api();}catch(error){if(error.status!==401||!attemptKey||key!==attemptKey||explicitPairing)throw error;key='';removeStored('key');data=await api();}
  if(invalidPairing||key&&key!==attemptKey)return;
  const identity=String(data.account||'legacy');
  if(key.includes('.')&&identity!==key.split('.')[0])throw new Error('配對身分驗證失敗');
  if(readStored('account')!==identity)clearHistory();
  writeStored('account',identity);explicitPairing=false;
  navigator.serviceWorker?.ready.then(r=>r.active?.postMessage({type:'paired-account',account:identity})).catch(()=>{});
  paired=true;pairingRequired=false;vapid=data.publicKey;render(data);$('pairing').hidden=true;
  if(requestedNews){const id=requestedNews;requestedNews=null;history.replaceState(history.state,'',location.pathname+location.search);openResetNews(id);}
  else if(!$('newsView').hidden)loadResetNews();
 } catch(e) {
  if(current)render(current,true);
  if(e.status===401){paired=false;pairingRequired=true;$('pairing').hidden=false;current=null;$('providers').replaceChildren();clearHistory();}
  text($('connection'),e.message);$('connection').parentElement.classList.add('off');
 } finally{busy=false;$('refresh').disabled=false;if(key&&key!==attemptKey)queueMicrotask(()=>load(true));}
}
function bytes(base64) { const value=atob(base64.replace(/-/g,'+').replace(/_/g,'/')); return Uint8Array.from(value,c=>c.charCodeAt(0)); }
function clearHistory(){clearEventDetail();resetNewsState();observedHistory=null;events.splice(0);for(const id of Object.keys(seen))delete seen[id];removeStored('events');removeStored('seen');renderHistory();}
async function pairWith(value){
 if(!validPairingKey(value)){invalidPairing=true;key='';removeStored('key');load(true);return;}
 invalidPairing=false;
 try{
  const r=await navigator.serviceWorker?.getRegistration(),sub=await r?.pushManager.getSubscription();
  if(sub&&value!==key){if(paired)await api('subscriptions','DELETE',sub.toJSON());await sub.unsubscribe();}
 }catch{ text($('connection'),'舊通知裝置尚未解除，請連線後重試');return; }
 key=value;explicitPairing=true;paired=false;csrf='';current=null;clearHistory();$('providers').replaceChildren();writeStored('key',key);load(true);
}
async function savePreferences() { writeStored('preferences',JSON.stringify(prefs));try{const ready=await navigator.serviceWorker?.getRegistration();const sub=await ready?.pushManager.getSubscription();if(sub)await api('subscriptions','POST',{...sub.toJSON(),preferences:prefs});}catch(e){text($('notificationStatus'),'設定尚未同步：'+e.message);} }
async function enable() { if(!paired||!vapid)throw new Error('請先完成配對與同步');if(!('serviceWorker'in navigator)||!('PushManager'in window))throw new Error('請先加入手機主畫面，再從主畫面開啟');const permission=await Notification.requestPermission();if(permission!=='granted')throw new Error('尚未允許通知，請在手機設定中開啟');registration=await navigator.serviceWorker.ready;let sub=await registration.pushManager.getSubscription();const expected=bytes(vapid);if(sub&&(!sub.options?.applicationServerKey||!new Uint8Array(sub.options.applicationServerKey).every((v,i)=>v===expected[i])||sub.options.applicationServerKey.byteLength!==expected.length)){await sub.unsubscribe();sub=null;}sub=sub||await registration.pushManager.subscribe({userVisibleOnly:true,applicationServerKey:expected});await api('subscriptions','POST',{...sub.toJSON(),preferences:prefs});text($('notificationStatus'),'手機通知已連接'); }
for(const name of ['codex','claude','low','reset','milestones','images']) { $(name).checked=prefs[name];$(name).onchange=async()=>{prefs[name]=$(name).checked;render(current,offline);await savePreferences();}; }
$('sound').checked=prefs.sound;
$('sound').onchange=async()=>{prefs.sound=$('sound').checked;writeStored('preferences',JSON.stringify(prefs));if(prefs.sound)try{await unlockSound();playEventSound('UsageReset',true);text($('soundStatus'),'頁面提示音已啟用');}catch(e){text($('soundStatus'),e.message);}else text($('soundStatus'),'頁面提示音已關閉');};
$('soundTest').onclick=async()=>{try{await unlockSound();playEventSound($('soundKind').value,true);text($('soundStatus'),'試聽完成');}catch(e){text($('soundStatus'),e.message);}};
text($('soundStatus'),prefs.sound?'點擊試聽以恢復此頁聲音':'頁面提示音已關閉');
for(const [id,theme] of Object.entries(themes)) { const option=element('option','',theme[0]);option.value=id;$('theme').append(option); }
$('followTheme').checked=appearance.follow;$('theme').value=appearance.theme;
$('followTheme').onchange=()=>{appearance.follow=$('followTheme').checked;writeStored('appearance',JSON.stringify(appearance));applyTheme();};
$('theme').onchange=()=>{appearance.theme=$('theme').value;writeStored('appearance',JSON.stringify(appearance));applyTheme();};
function selectView(name) { clearEventDetail();for(const id of ['usage','notifications','news','settings'])$(id+'View').hidden=id!==name;for(const button of document.querySelectorAll('[data-view]')){if(button.dataset.view===name)button.setAttribute('aria-current','page');else button.removeAttribute('aria-current');}window.scrollTo(0,0); }
function openProvider(id){selectView('usage');if(['codex','claude'].includes(id))document.querySelector('[data-provider="'+id+'"]')?.scrollIntoView({block:'start'});}
function openNotification(id){selectView('notifications');const event=syncedHistory().find(e=>e.id===id&&prefs[e.provider]&&current?.usage?.providers?.some(p=>p.name===(e.provider==='codex'?'Codex':'Claude Code')&&p.enabled));openEventDetail(event);}
$('eventDialogClose').onclick=dismissEventDetail;
$('eventDialog').addEventListener('cancel',event=>{event.preventDefault();dismissEventDetail();});
$('eventDialog').addEventListener('click',event=>{const rect=event.currentTarget.getBoundingClientRect();if(event.target===event.currentTarget&&(event.clientX<rect.left||event.clientX>rect.right||event.clientY<rect.top||event.clientY>rect.bottom))dismissEventDetail();});
$('eventDialog').addEventListener('close',()=>{if($('eventDialog').open)return;delete $('eventDialog').dataset.panel;document.documentElement.classList.remove('notice-open');$('eventDialogContent').replaceChildren();if(detailReturnEvent){const view=document.querySelector('.view:not([hidden])');Array.from(view?.querySelectorAll('[data-event]')||[]).find(e=>e.dataset.event===detailReturnEvent)?.focus({preventScroll:true});}detailReturnEvent='';});
window.addEventListener('popstate',()=>{if($('eventDialog').open)$('eventDialog').close();});
if('serviceWorker'in navigator)navigator.serviceWorker.addEventListener('message',event=>{if(event.data?.type==='open-notification'){if(event.data.account&&event.data.account!==readStored('account'))return;requestedEvent=event.data.eventId;load(true);}if(event.data?.type==='open-provider'){openProvider(event.data.provider);load();}if(event.data?.type==='history-updated')load();});
// QR links can reuse an existing tab via same-document navigation. Bootstrap the new identity.
window.addEventListener('hashchange',()=>{const params=new URLSearchParams(location.hash.slice(1));if(params.has('key'))location.reload();else if(params.has('event')){requestedEvent=params.get('event');load(true);}else if(params.has('provider'))openProvider(params.get('provider'));});
setupResetNews();
for(const button of document.querySelectorAll('[data-view]'))button.onclick=()=>{selectView(button.dataset.view);if(button.dataset.view==='news')loadResetNews();};
if('serviceWorker'in navigator)navigator.serviceWorker.addEventListener('message',event=>{if(event.data?.type==='open-reset-news'&&event.data.account===readStored('account')){requestedNews=event.data.newsId;load(true);}});
window.addEventListener('hashchange',()=>{const id=new URLSearchParams(location.hash.slice(1)).get('news');if(validNewsId(id)){requestedNews=id;load(true);}});
$('notify').onclick=()=>enable().catch(e=>text($('notificationStatus'),e.message));
$('test').onclick=async()=>{try{await enable();registration.active?.postMessage({type:'test-notification',imageEnabled:prefs.images});log('測試通知（示例 20%）');}catch(e){text($('notificationStatus'),e.message);}};
$('refresh').onclick=()=>{load(true);if(!$('newsView').hidden)loadResetNews(true);};
$('pair').onclick=()=>pairWith($('key').value.trim());
$('unpair').onclick=async()=>{
 try{
  const ready=await navigator.serviceWorker?.getRegistration();const sub=await ready?.pushManager.getSubscription();
  if(sub){await api('subscriptions','DELETE',sub.toJSON());await sub.unsubscribe();}
  await api('session','DELETE');
 }catch(e){text($('notificationStatus'),'解除配對未完成，請連線後重試');return;}
 removeStored('key');removeStored('account');key='';csrf='';paired=false;pairingRequired=true;current=null;clearHistory();navigator.serviceWorker?.ready.then(r=>r.active?.postMessage({type:'paired-account',account:''}));$('providers').replaceChildren();$('pairing').hidden=false;text($('connection'),'已解除配對');selectView('usage');
};
if('serviceWorker'in navigator)navigator.serviceWorker.register('sw.js?v=20261118.2-local').catch(()=>text($('notificationStatus'),'通知服務無法啟動'));
window.addEventListener('hashchange',()=>{
 const value=new URLSearchParams(location.hash.slice(1)).get('key');
 if(value&&validPairingKey(value)){history.replaceState(null,'',location.pathname+location.search);pairWith(value);}
});
renderHistory();applyTheme();load();setInterval(()=>{if(!document.hidden)load();},15000);
setInterval(()=>{if(!document.hidden){document.querySelectorAll('[data-reset]').forEach(node=>text(node,countdown(node.dataset.reset)));if(current?.usage?.providers?.some(p=>p.windows?.some(w=>w.resetsAt&&new Date(w.resetsAt)<=new Date())))render(current,offline);}},60000);
document.addEventListener('visibilitychange',()=>{if(!document.hidden)load();});
