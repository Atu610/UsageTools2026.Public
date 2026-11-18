const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const output=process.argv[2];if(!output)throw Error('Evidence directory required');fs.mkdirSync(output,{recursive:true});
const callbacks={},notices=[],data=new Map(),results=[];let closed=0;
const messages=[];
const sandbox={self:{clients:{matchAll:async()=>[{url:'https://fixture.test/mobile/',postMessage:value=>messages.push(value)},{url:'https://fixture.test/personal/',postMessage:()=>{throw Error('cross-scope message');}}]},registration:{scope:'https://fixture.test/mobile/',showNotification:async(title,options)=>notices.push({title,options}),getNotifications:async()=>[{close:()=>closed++}]},addEventListener:(name,handler)=>(callbacks[name]??=[]).push(handler)},caches:{open:async()=>({match:async key=>data.get(key)?.clone(),put:async(key,value)=>data.set(key,value.clone())})},Response,URL,Uint8Array};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'sw.js'),'utf8'),sandbox);
async function emit(name,payload){const pending=[];for(const handler of callbacks[name])handler({...payload,waitUntil:p=>pending.push(p)});await Promise.all(pending);}
function pass(name){results.push('PASS '+name);console.log(results.at(-1));}
(async()=>{
 const a='a'.repeat(64),b='b'.repeat(64),push=account=>emit('push',{data:{json:()=>({account,title:'Quota',body:'Remaining',imageEnabled:false})}});
 await push(a);assert.equal(notices.length,0);pass('unpaired_worker_drops_member_payload');
 await emit('message',{data:{type:'paired-account',account:a}});await push(a);assert.equal(notices.length,1);pass('paired_account_receives_own_notification');
 await push(b);assert.equal(notices.length,1);pass('other_account_payload_is_not_displayed');
 await emit('message',{data:{type:'paired-account',account:a}});assert.equal(closed,0);pass('refresh_does_not_dismiss_current_notifications');
 await emit('message',{data:{type:'paired-account',account:b}});assert.equal(closed,1);await push(a);assert.equal(notices.length,1);pass('account_switch_dismisses_old_and_blocks_queued_old_push');
 await push(b);assert.equal(notices.length,2);pass('new_account_notification_displayed');
 await emit('message',{data:{type:'paired-account',account:''}});await push(b);assert.equal(notices.length,2);pass('unpair_blocks_further_member_notifications');
 assert.equal([...data.keys()].join(','),'paired-account');assert.equal(await data.get('paired-account').clone().text(),'');pass('worker_cache_contains_no_usage_or_pairing_secret');
 assert.equal(messages.length,2);assert.ok(messages.every(m=>m.type==='history-updated'&&Object.keys(m).length===1));pass('push_refreshes_only_same_scope_without_private_payload');
 let focused=0;const opened=[];sandbox.self.clients.matchAll=async()=>[{url:'https://fixture.test/mobile/',postMessage:value=>messages.push(value),focus:async()=>focused++}];sandbox.self.clients.openWindow=async url=>opened.push(url);
 await emit('message',{data:{type:'paired-account',account:b}});
 await emit('push',{data:{json:()=>({account:b,title:'Credit',body:'Dates',tag:'event-123',provider:'codex',imageEnabled:false})}});
 const notice=notices.at(-1);assert.equal(notice.options.data.eventId,'event-123');
 await emit('notificationclick',{notification:{data:notice.options.data,close(){}}});assert.equal(messages.at(-1).type,'open-notification');assert.equal(messages.at(-1).eventId,'event-123');assert.equal(focused,1);pass('notification_click_opens_exact_event_in_existing_page');
 sandbox.self.clients.matchAll=async()=>[];await emit('notificationclick',{notification:{data:notice.options.data,close(){}}});assert.equal(opened.at(-1),'https://fixture.test/mobile/#event=event-123');pass('notification_click_cold_launch_uses_event_deep_link');
 await emit('notificationclick',{notification:{data:{account:b,provider:'codex',eventId:'../../?key=secret'},close(){}}});assert.equal(opened.at(-1),'https://fixture.test/mobile/#provider=codex');pass('invalid_event_identifier_not_used_as_url');
 const count=opened.length;await emit('message',{data:{type:'paired-account',account:a}});await emit('notificationclick',{notification:{data:notice.options.data,close(){}}});assert.equal(opened.length,count);pass('old_account_notification_click_is_blocked');
 const newsId='2109873287853707513',beforeNews=notices.length;
 await emit('push',{data:{json:()=>({kind:'ResetNews',newsId,title:'Tibo',tag:'tibo-'+newsId,imageEnabled:false})}});assert.equal(notices.length,beforeNews);pass('news_requires_paired_account');
 await emit('push',{data:{json:()=>({account:a,kind:'ResetNews',newsId,title:'Tibo',tag:'tibo-'+newsId,imageEnabled:false})}});const news=notices.at(-1);assert.equal(news.options.data.newsId,newsId);assert.equal(news.options.data.eventId,null);assert.equal(news.options.image,undefined);pass('news_payload_separate_from_private_quota');
 await emit('notificationclick',{notification:{data:news.options.data,close(){}}});assert.equal(opened.at(-1),'https://fixture.test/mobile/#news='+newsId);pass('news_cold_launch_exact_deep_link');
 sandbox.self.clients.matchAll=async()=>[{url:'https://fixture.test/mobile/',postMessage:value=>messages.push(value),focus:async()=>focused++}];await emit('notificationclick',{notification:{data:news.options.data,close(){}}});assert.equal(messages.at(-1).type,'open-reset-news');assert.equal(messages.at(-1).newsId,newsId);pass('news_existing_page_exact_deep_link');
 await emit('message',{data:{type:'paired-account',account:b}});const beforeSwitch=messages.length;await emit('notificationclick',{notification:{data:news.options.data,close(){}}});assert.equal(messages.length,beforeSwitch);pass('old_account_news_click_blocked');
 console.log('ALL PASS checks='+results.length);
})().catch(e=>{results.push('FAIL '+e.message);console.error(e);process.exitCode=1;}).finally(()=>fs.writeFileSync(path.join(output,'member-worker-tests.txt'),results.join('\n')+'\n'));
