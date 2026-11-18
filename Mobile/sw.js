'use strict';
const appIconRevision='?v=2026.11.18.2';
const cachePrefix='usagetools-scope-'+encodeURIComponent(self.registration.scope)+'-',cacheName=cachePrefix+'v16',identityCache=cachePrefix+'identity',shell=['./','index.html','style.css?v=2026.11.18.3','notifications.css?v=2026.11.4.1','reset-news.css?v=2026.11.4.1','reset-news.js?v=2026.11.4.1','notifications.js?v=2026.11.18.1','claude-details.js?v=2026.11.11.2','app.js?v=2026.11.18.2','manifest.php?v=2026.11.18.2','pulse-icon-180-v1.png'+appIconRevision,'pulse-icon-192-v1.png'+appIconRevision,'pulse-icon-512-v1.png'+appIconRevision,'badge.png','codex-icon.png?v=2026.11.18.1','claude-icon.png?v=2026.11.18.1'];
self.addEventListener('install',event=>event.waitUntil(caches.open(cacheName).then(cache=>cache.addAll(shell)).then(()=>self.skipWaiting())));
self.addEventListener('activate',event=>event.waitUntil(caches.keys().then(keys=>Promise.all(keys.filter(k=>k.startsWith(cachePrefix)&&k!==cacheName&&k!==identityCache).map(k=>caches.delete(k)))).then(()=>self.clients.claim())));
self.addEventListener('fetch',event=>{const url=new URL(event.request.url);if(event.request.method!=='GET'||url.origin!==self.location.origin||url.pathname.endsWith('api.php')||!shell.some(p=>new URL(p,self.registration.scope).pathname===url.pathname))return;event.respondWith(fetch(event.request).catch(()=>caches.match(event.request)));});
async function quotaImage(quota) {
 if(typeof OffscreenCanvas==='undefined'||!quota||!Number.isFinite(quota.remaining)||quota.remaining<0||quota.remaining>100)return undefined;
 const canvas=new OffscreenCanvas(420,144),ctx=canvas.getContext('2d');if(!ctx)return undefined;
 ctx.fillStyle='#202426';ctx.fillRect(0,0,420,144);ctx.fillStyle='#abb5b0';ctx.font='15px sans-serif';ctx.fillText('剩餘額度',20,29);
 ctx.fillStyle='#edf3f0';ctx.font='bold 35px sans-serif';ctx.fillText(quota.remaining+'%',20,71);
 ctx.font='14px sans-serif';ctx.fillText('已使用 '+(100-quota.remaining)+'%',286,68);
 ctx.fillStyle='#454d49';ctx.fillRect(20,94,380,12);
 ctx.fillStyle=quota.remaining<=20?'#ff7373':quota.remaining<=30?'#e9b65d':quota.provider==='Claude Code'?'#f08f7c':'#68e8c5';ctx.fillRect(20,94,380*quota.remaining/100,12);
 ctx.fillStyle='#abb5b0';ctx.font='11px sans-serif';ctx.fillText('通知發送時的用量',20,127);
 const blob=await canvas.convertToBlob({type:'image/png'}),bytes=new Uint8Array(await blob.arrayBuffer());
 let binary='';for(const byte of bytes)binary+=String.fromCharCode(byte);
 return 'data:image/png;base64,'+btoa(binary);
}
async function showUsageNotification(data) {
 const options={body:String(data.body||'用量資料已更新').slice(0,250),icon:'pulse-icon-192-v1.png'+appIconRevision,badge:'badge.png',tag:String(data.tag||'usage').slice(0,100),data:{provider:['codex','claude'].includes(data.provider)?data.provider:null,eventId:selectedEvent(data.tag),account:typeof data.account==='string'?data.account:null}};
 if(data.kind==='ResetNews'){options.data.newsId=selectedNews(data.newsId);options.data.eventId=null;options.data.provider=null;}
 if(data.imageEnabled!==false)try{const image=data.kind==='ResetCreditsAdded'?await creditImage(data):await quotaImage(data.quota);if(image)options.image=image;}catch{}
 const title=String(data.title||'UsageTools').slice(0,80);
 try{await self.registration.showNotification(title,options);}catch(error){if(!options.image)throw error;delete options.image;await self.registration.showNotification(title,options);}
}
async function creditImage(data){
 if(typeof OffscreenCanvas==='undefined'||!Number.isInteger(data.creditCount)||!Number.isInteger(data.addedCount)||data.creditCount<0||data.addedCount<0)return undefined;
 const canvas=new OffscreenCanvas(420,144),ctx=canvas.getContext('2d');if(!ctx)return undefined;
 ctx.fillStyle='#202426';ctx.fillRect(0,0,420,144);ctx.fillStyle='#64cad5';ctx.fillRect(20,25,78,94);
 ctx.fillStyle='#202426';for(let y=31;y<118;y+=12)ctx.fillRect(75,y,3,6);
 ctx.font='bold 27px sans-serif';ctx.fillText('+'+data.addedCount,26,80);
 ctx.fillStyle='#edf3f0';ctx.font='bold 23px sans-serif';ctx.fillText(data.provider==='claude'?'Claude 新增重置券':'Codex 新增重置券',116,57);
 ctx.font='18px sans-serif';ctx.fillText('目前可用 '+data.creditCount+' 張',116,94);
 const blob=await canvas.convertToBlob({type:'image/png'}),bytes=new Uint8Array(await blob.arrayBuffer());let binary='';for(const b of bytes)binary+=String.fromCharCode(b);return 'data:image/png;base64,'+btoa(binary);
}
self.addEventListener('push',event=>{let data={};try{data=event.data.json();}catch{}event.waitUntil((async()=>{if(data?.kind==='ResetNews'&&(!selectedNews(data.newsId)||!data.account))return;if(data?.account){const cache=await caches.open(identityCache),saved=await cache.match('paired-account');if(!saved||await saved.text()!==data.account)return;}await showUsageNotification(data||{});for(const client of await self.clients.matchAll({type:'window',includeUncontrolled:true}))if(client.url.startsWith(self.registration.scope))client.postMessage({type:'history-updated'});})());});
// Store only an opaque account identifier, never a pairing token or quota snapshot.
self.addEventListener('message',event=>{if(event.data?.type==='paired-account'&&typeof event.data.account==='string'&&/^(?:[a-f0-9]{64}|legacy|)$/.test(event.data.account))event.waitUntil((async()=>{const cache=await caches.open(identityCache),previous=await cache.match('paired-account');if(previous&&await previous.text()!==event.data.account)for(const notice of await self.registration.getNotifications())notice.close();await cache.put('paired-account',new Response(event.data.account));})());});
const selectedProvider=provider=>['codex','claude'].includes(provider)?provider:null;
const selectedEvent=value=>typeof value==='string'&&/^[a-zA-Z0-9:_-]{1,100}$/.test(value)?value:null;
const selectedNews=value=>typeof value==='string'&&/^[0-9]{10,24}$/.test(value)?value:null;
self.addEventListener('message',event=>{if(event.data?.type==='test-notification')event.waitUntil(showUsageNotification({title:'UsageTools 血條測試',body:'測試資料：剩餘 20%\n🟥🟥⬜⬜⬜⬜⬜⬜⬜⬜\n這不是你的實際額度',tag:'local-test',imageEnabled:event.data.imageEnabled,quota:{remaining:20,provider:'Codex'}}));});
self.addEventListener('notificationclick',event=>{event.notification.close();event.waitUntil((async()=>{const data=event.notification.data||{};if(data.account){const cache=await caches.open(identityCache),saved=await cache.match('paired-account');if(!saved||await saved.text()!==data.account)return;}const clients=await self.clients.matchAll({type:'window',includeUncontrolled:true}),existing=clients.find(c=>c.url.startsWith(self.registration.scope)),provider=selectedProvider(data.provider),eventId=selectedEvent(data.eventId),newsId=selectedNews(data.newsId);if(existing){existing.postMessage(newsId?{type:'open-reset-news',newsId,account:data.account||null}:eventId?{type:'open-notification',eventId,account:data.account||null}:{type:'open-provider',provider});return existing.focus();}return self.clients.openWindow(self.registration.scope+(newsId?'#news='+newsId:eventId?'#event='+encodeURIComponent(eventId):provider?'#provider='+provider:''));})());});
