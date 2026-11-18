'use strict';
// Local Lucide outlines, consistent with the existing navigation (LUCIDE_LICENSE.txt).
const eventPaths={
 ticket:'<path d="M2 9a3 3 0 0 1 0 6v3a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-3a3 3 0 0 1 0-6V6a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2Z"/><path d="M13 5v2m0 4v2m0 4v2"/>',
 reset:'<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5"/><path d="m9 12 2 2 4-4"/>'
};
function eventSymbol(type){const span=element('span','event-symbol');span.setAttribute('aria-hidden','true');span.innerHTML='<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">'+eventPaths[type]+'</svg>';return span;}
function creditExpiryList(values,count,earliest=null){
 const dates=(Array.isArray(values)?values:[]).filter(at=>typeof at==='string'&&Number.isFinite(new Date(at).getTime())).sort((a,b)=>new Date(a)-new Date(b));
 const list=element('ul','credit-expirations');
 dates.forEach((at,index)=>{const row=element('li'),time=element('time','',date(at));time.dateTime=at;row.append(element('span','','第 '+(index+1)+' 張'),time);if(new Date(at)<=Date.now())row.append(element('span','expired','已到期'));list.append(row);});
 if(count>dates.length)list.append(element('li','hint',dates.length?'其餘 '+(count-dates.length)+' 張到期時間未提供':earliest?'僅提供最早到期 '+date(earliest)+'；各張期限待同步':'各張到期時間尚未提供'));
 return list;
}
function creditTitle(e){return e.provider==='claude'&&typeof creditKindName==='function'?creditKindName(e.creditKind):'重置券';}
function quotaTitle(e){return e.window+(typeof e.pool==='string'&&e.pool?' '+e.pool:'');}
function eventCard(e,animate=false,detail=false){
 if(!detail)return eventRow(e,animate);
 const li=element(detail?'article':'li','event '+e.kind+(e.remaining<=10&&e.kind==='LowRemaining'?' critical':'')+(animate?' new-event':''));li.dataset.event=e.id||'';
 const provider=e.provider==='codex'?'Codex':'Claude Code',credit=e.kind==='ResetCreditsAdded',reset=e.kind==='UsageReset';
 const top=element('div','event-top'),logo=element('img');logo.src=e.provider+'-icon.png?v=2026.11.18.1';logo.alt='';
 const time=element('time','',shortEventDate(e.at));time.dateTime=e.at;time.title=date(e.at);
 top.append(logo,element('strong','',provider),time);li.append(top);
 li.append(element('h3','',credit?'新增 '+e.addedCount+' 張'+creditTitle(e):quotaTitle(e)+' '+(reset?'額度已重置':e.kind==='LowRemaining'?'低額度提醒':'用量更新')));
 li.append(element('p','event-plan',(e.plan||'方案未提供')+' · '+(credit?'重置額度':reset?'已偵測到新額度週期':'額度快照')));
 if(credit){
  const visual=element('div','event-visual'),details=element('div');details.append(element('strong','','+'+e.addedCount),element('p','','當時可用 '+e.creditCount+' 張'));visual.append(eventSymbol('ticket'),details);li.append(visual);
  li.append(creditExpiryList(e.creditExpirations,e.creditCount,e.expiresAt));
 }else{
  if(reset){const visual=element('div','event-visual');visual.append(eventSymbol('reset'),element('span','event-kind','新週期已開始'));li.append(visual);}
  const value=element('div','event-value'),strong=element('strong','',String(e.remaining));strong.append(element('small','','%'));value.append(strong,element('span','','剩餘額度'));li.append(value);
  const bar=element('div','energy-track'),fill=element('div','energy-fill');fill.style.setProperty('--remaining',e.remaining+'%');bar.append(fill);bar.setAttribute('role','progressbar');bar.setAttribute('aria-label',provider+' '+e.window+' 通知時剩餘');bar.setAttribute('aria-valuenow',String(e.remaining));bar.setAttribute('aria-valuemin','0');bar.setAttribute('aria-valuemax','100');li.append(bar);
  li.append(element('p','event-detail',e.resetsAt?'官方重置 '+date(e.resetsAt):'官方重置時間未提供'));
 }
 if(!detail){li.tabIndex=0;li.setAttribute('role','button');li.setAttribute('aria-haspopup','dialog');li.setAttribute('aria-label',provider+' '+li.querySelector('h3').textContent+'，查看詳情');li.onclick=()=>openEventDetail(e);li.onkeydown=event=>{if(event.key==='Enter'||event.key===' '){event.preventDefault();openEventDetail(e);}};}
 return li;
}
let detailReturnEvent='';
function replayDetailEffect(content){content.classList.remove('detail-arrival');void content.offsetWidth;content.classList.add('detail-arrival');}
function detailReplayButton(content){const button=element('button','detail-replay','重播頁內效果');button.type='button';button.onclick=()=>replayDetailEffect(content);return button;}
function eventRow(e,animate=false){
 const row=element('li','event event-row '+e.kind+(e.remaining<=10&&e.kind==='LowRemaining'?' critical':'')+(animate?' new-event':''));row.dataset.event=e.id||'';
 const provider=e.provider==='codex'?'Codex':'Claude Code',credit=e.kind==='ResetCreditsAdded',reset=e.kind==='UsageReset';
 const logo=element('img');logo.src=e.provider+'-icon.png?v=2026.11.18.1';logo.alt='';
 const copy=element('div','event-row-copy'),title=credit?'新增 '+e.addedCount+' 張'+creditTitle(e):quotaTitle(e)+' '+(reset?'額度已重置':e.kind==='LowRemaining'?'低額度提醒':'用量更新');
 copy.append(element('h3','',provider+' · '+title),element('p','', (e.plan||'方案未提供')+' · '+shortEventDate(e.at)));
 const value=element('span','event-row-value',credit?'+'+e.addedCount:e.remaining+'%');value.setAttribute('aria-label',credit?'新增重置券':'通知時剩餘 '+e.remaining+'%');
 const arrow=element('span','event-row-arrow');arrow.setAttribute('aria-hidden','true');arrow.innerHTML='<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="m9 18 6-6-6-6"/></svg>';
 row.append(logo,copy,value,arrow);row.tabIndex=0;row.setAttribute('role','button');row.setAttribute('aria-haspopup','dialog');row.setAttribute('aria-label',provider+' '+title+'，'+(credit?'目前可用 '+e.creditCount+' 張':'剩餘 '+e.remaining+'%')+'，查看詳情');
 row.onclick=()=>openEventDetail(e);row.onkeydown=event=>{if(event.key==='Enter'||event.key===' '){event.preventDefault();openEventDetail(e);}};return row;
}
function openEventDetail(event){
 const dialog=$('eventDialog'),content=$('eventDialogContent');detailReturnEvent=event?.id||'';dialog.dataset.event=detailReturnEvent;delete dialog.dataset.panel;
 text($('eventDialogTitle'),'通知詳情');content.replaceChildren();
 if(event){const card=eventCard(event,false,true);content.append(card,detailReplayButton(card),element('p','event-timestamp','通知時間 '+date(event.at)));replayDetailEffect(card);}
 else content.append(element('p','empty-events','這則通知尚未同步或已超過保留期限。'));
 if(!dialog.open){history.pushState({...history.state,usageNotice:true},'',location.href);dialog.showModal();document.documentElement.classList.add('notice-open');}
 $('eventDialogClose').focus();
}
function dismissEventDetail(){if(history.state?.usageNotice)history.back();else $('eventDialog').close();}
function clearEventDetail(){
 const dialog=$('eventDialog');detailReturnEvent='';dialog.close();$('eventDialogContent').replaceChildren();delete dialog.dataset.event;
 document.documentElement.classList.remove('notice-open');
 if(history.state?.usageNotice){const state={...history.state};delete state.usageNotice;history.replaceState(state,'',location.href);}
}
function shortEventDate(at){const d=new Date(at);return Number.isFinite(d.getTime())?new Intl.DateTimeFormat('zh-TW',{month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hour12:false}).format(d):'時間未提供';}
let audioContext=null,lastSound=0;
async function unlockSound(){const Audio=window.AudioContext||window.webkitAudioContext;if(!Audio)throw Error('此瀏覽器未提供音效');audioContext??=new Audio();await audioContext.resume();if(audioContext.state!=='running')throw Error('請再次點擊試聽以啟用聲音');}
function playEventSound(kind,force=false){
 if(!audioContext||audioContext.state!=='running'||document.hidden||(!force&&(!prefs.sound||Date.now()-lastSound<10000)))return false;
 lastSound=Date.now();const notes=kind==='ResetCreditsAdded'?[659,831,988]:kind==='UsageReset'?[523,784]:[440,330];
 notes.forEach((frequency,i)=>{const start=audioContext.currentTime+i*.15,osc=audioContext.createOscillator(),gain=audioContext.createGain();osc.type='sine';osc.frequency.value=frequency;gain.gain.setValueAtTime(0,start);gain.gain.linearRampToValueAtTime(.09,start+.015);gain.gain.exponentialRampToValueAtTime(.001,start+.13);osc.connect(gain);gain.connect(audioContext.destination);osc.start(start);osc.stop(start+.14);osc.onended=()=>{osc.disconnect();gain.disconnect();};});return true;
}
