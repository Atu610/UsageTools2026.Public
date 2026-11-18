'use strict';
// Optional Claude contract (Reports/CLAUDE_DATA_CONTRACT.md). Older backends omit claudeDetails;
// every missing field renders as "未提供", never as 0.
const creditKindNames={full:'完整重置券',weekly:'週重置券',five_hour:'5H 重置券',fable:'Fable 重置券',other:'重置券'};
function creditKindName(kind){return creditKindNames[kind]||'重置券';}
function poolName(pool){return (pool.label==='5H'?'5 小時':'每週 7D')+(pool.model?' · '+pool.model:'');}
function validPercent(value){return Number.isFinite(value)&&value>=0&&value<=100;}
function claudeDetails(){const d=current?.usage?.claudeDetails;return d&&typeof d==='object'?d:null;}
function claudeDetailsFresh(details){const age=details?.syncedAt?(Date.now()-new Date(details.syncedAt))/60000:Infinity;return current?.usage?.providers?.find(p=>p.name==='Claude Code')?.state==='Fresh'&&age>=-1&&age<=3;}
function claudePoolValid(pool){const details=claudeDetails(),age=details?.syncedAt?(Date.now()-new Date(details.syncedAt))/60000:Infinity;return validPercent(pool.used)&&age>=-1&&age<(pool.label==='5H'?300:10080)&&(!pool.resetsAt||new Date(pool.resetsAt)>new Date());}
function claudeSummaryButton(online){
 const details=claudeDetails();if(!details)return null;
 const fresh=online&&claudeDetailsFresh(details),parts=[];
 const scoped=(Array.isArray(details.pools)?details.pools:[]).filter(p=>p&&p.model);
 for(const pool of scoped.slice(0,2))parts.push(pool.model+' '+(claudePoolValid(pool)?Math.round(100-pool.used)+'%':'已過期／未同步'));
 if(!fresh)parts.push('快取 '+date(details.syncedAt));
 const resets=details.resets;
 parts.push(resets?'重置券 '+(Number.isInteger(resets.available)?resets.available+' 張':'數量未提供'):'重置券 未提供');
 const button=element('button','claude-summary');button.type='button';button.setAttribute('aria-haspopup','dialog');
 button.append(element('span','claude-summary-text',parts.join(' · ')),element('span','claude-summary-more','詳情'));
 button.setAttribute('aria-label','Claude Code 額度與重置券詳情：'+parts.join('，'));
 button.onclick=()=>openClaudeDetails();return button;
}
function claudePoolRow(pool,fresh){
 const valid=claudePoolValid(pool),remaining=valid?Math.round(100-pool.used):null;
 const row=element('li','claude-pool'+(remaining!==null&&remaining<=20?' alarm':''));
 const head=element('div','claude-pool-head');head.append(element('span','',poolName(pool)),element('strong','',remaining===null?'未提供':remaining+'%'));row.append(head);
 const bar=element('div','bar'),fill=element('div','fill');fill.style.width=(remaining??0)+'%';bar.append(fill);bar.setAttribute('role','progressbar');bar.setAttribute('aria-label','Claude Code '+poolName(pool)+' 剩餘額度');bar.setAttribute('aria-valuemin','0');bar.setAttribute('aria-valuemax','100');if(remaining!==null)bar.setAttribute('aria-valuenow',String(remaining));else bar.setAttribute('aria-valuetext','未提供');row.append(bar);
 const meta=[pool.resetsAt?'重置 '+date(pool.resetsAt):'重置時間未提供'];if(validPercent(pool.allowance))meta.push('最多占每週額度 '+Math.round(pool.allowance)+'%');
 row.append(element('p','claude-meta',meta.join(' · ')));return row;
}
function claudeGrantRows(grants){
 const list=element('ul','claude-grants');let index=0;
 const sorted=(Array.isArray(grants)?grants:[]).filter(g=>g&&typeof g==='object'&&g.left!==0&&(!g.expiresAt||new Date(g.expiresAt)>new Date())).sort((a,b)=>(a.expiresAt?new Date(a.expiresAt):Infinity)-(b.expiresAt?new Date(b.expiresAt):Infinity));
 for(const grant of sorted){
  const units=Number.isInteger(grant.left)&&grant.left>0?Math.min(grant.left,20):1;
  for(let i=0;i<units;i++){
   index++;const row=element('li','claude-grant');row.append(element('span','claude-grant-index',Number.isInteger(grant.left)?'第 '+index+' 張':'數量未知'),element('span','claude-grant-kind',creditKindName(grant.kind)));
   const time=element('time','',grant.expiresAt?date(grant.expiresAt)+' 到期':'到期時間未提供');if(grant.expiresAt)time.dateTime=grant.expiresAt;row.append(time);
   if(!Number.isInteger(grant.left))row.append(element('span','hint','可用次數未提供'));
   else if(!grant.usableNow)row.append(element('span','hint','目前不可使用'));
   list.append(row);
  }
  if(grant.left>20)list.append(element('li','hint','另有 '+(grant.left-20)+' 張，皆於上述時間到期'));
 }
 if(!index)list.append(element('li','hint','目前沒有可用的重置券'));
 return list;
}
function money(value,currency){return Number.isFinite(value)?(currency?currency+' ':'')+value.toLocaleString('zh-TW',{maximumFractionDigits:2}):'未提供';}
function renderClaudeDetails(content){
 const details=claudeDetails(),provider=current?.usage?.providers?.find(p=>p.name==='Claude Code');content.replaceChildren();
 if(!details||!provider?.enabled){content.append(element('p','empty-events','Claude Code 詳細資料尚未同步，或電腦端已關閉讀取。'));return;}
 const fresh=!offline&&claudeDetailsFresh(details),card=element('article','claude-details');
 card.append(element('p','event-plan',(provider.plan||'方案未提供')+' · '+(fresh?'官方資料 ':'快取資料 · ')+date(details.syncedAt)));
 const pools=element('ul','claude-pools');for(const pool of (Array.isArray(details.pools)?details.pools:[]).slice(0,8))pools.append(claudePoolRow(pool,fresh));
 if(!pools.childElementCount)pools.append(element('li','hint','官方未提供額度池'));
 card.append(element('h3','','額度'),pools);
 const resets=details.resets;card.append(element('h3','','重置券'));
 if(!resets)card.append(element('p','hint','官方未提供重置券資料'));
 else{card.append(element('p','claude-meta','可用 '+(Number.isInteger(resets.available)?resets.available+' 張':'數量未提供')+(resets.eligible===false?' · 目前帳號不符合資格':'')+' · 僅供查看，請到 Claude 設定頁使用'));card.append(claudeGrantRows(resets.grants));}
 const extra=details.extraUsage;card.append(element('h3','','額外用量（usage credits）'));
 card.append(element('p','claude-meta',!extra?'官方未提供':extra.enabled?'已開啟 · 本月 '+money(extra.used,extra.currency)+(Number.isFinite(extra.limit)?' / 上限 '+money(extra.limit,extra.currency):' · 上限未提供')+(extra.limitReached?' · 已達上限':''):'未開啟 · 與重置券分開計算'));
 const models=details.models;card.append(element('h3','','此帳號可用模型'));
 if(!models)card.append(element('p','hint','模型清單尚未同步'));
 else{const list=element('p','claude-meta',(Array.isArray(models.primary)&&models.primary.length?models.primary.join('、'):'主要模型未提供')+(Number.isInteger(models.count)?' · 共 '+models.count+' 個可選':''));card.append(list);}
 content.append(card);
}
function openClaudeDetails(){
 const dialog=$('eventDialog'),content=$('eventDialogContent');detailReturnEvent='';dialog.dataset.event='';dialog.dataset.panel='claude';
 text($('eventDialogTitle'),'Claude Code 詳情');renderClaudeDetails(content);
 if(!dialog.open){history.pushState({...history.state,usageNotice:true},'',location.href);dialog.showModal();document.documentElement.classList.add('notice-open');}
 $('eventDialogClose').focus();
}
function refreshClaudeDetails(){const dialog=$('eventDialog');if(dialog.open&&dialog.dataset.panel==='claude')renderClaudeDetails($('eventDialogContent'));}
