'use strict';
let resetNewsItems=[],newsLoadedAt=0,newsBusy=false,newsGeneration=0,newsPending=null;
const validNewsId=id=>typeof id==='string'&&/^[0-9]{10,24}$/.test(id);
const newsLabel=item=>({banked:'贈送重置券',regular:'額度重置消息',scheduled:'重置預告',watch:'重置相關討論'}[item.kind]||'重置消息');
const newsConfidence=item=>({banked:'公開公告 · 待確認入帳',regular:'公開公告 · 非帳戶確認',scheduled:'重置預告 · 尚未完成',watch:'相關討論 · 尚非重置公告'}[item.kind]||'公開消息');
const newsDisclaimer=item=>({banked:'公告不代表你的帳戶已收到券；各張到期日以官方帳戶資料為準，未提供時不自行推算。',regular:'公告不代表你的帳戶已完成重置；實際剩餘額度以官方帳戶資料為準。',scheduled:'預告尚未完成，不依歷史平均值推算時間；實際用量以官方帳戶資料為準。',watch:'相關討論不是已完成重置的證據；實際用量以官方帳戶資料為準。'}[item.kind]);
const hasNewsTranslation=item=>item.translationStatus==='translated'&&typeof item.zhText==='string'&&item.zhText.length<=16000&&/[\u3400-\u9FFF]/.test(item.zhText);
function newsIcon(kind){
 const icon=element('span','news-icon '+kind);icon.setAttribute('aria-hidden','true');
 const paths=kind==='banked'?eventPaths.ticket:kind==='regular'?eventPaths.reset:kind==='scheduled'?'<circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/>':'<path d="M21 15a4 4 0 0 1-4 4H7l-5 3V6a4 4 0 0 1 4-4h11a4 4 0 0 1 4 4z"/><path d="M8 8h8M8 12h6"/>';
 icon.innerHTML='<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">'+paths+'</svg>';return icon;
}
function resetNewsState(){newsGeneration++;resetNewsItems=[];newsLoadedAt=0;$('newsList').replaceChildren();$('newsLatest').replaceChildren();$('newsLatest').hidden=true;text($('newsState'),'尚未載入');}
function openNewsDetail(id){
 const item=resetNewsItems.find(item=>item.id===id),dialog=$('eventDialog'),content=$('eventDialogContent');
 delete dialog.dataset.event;detailReturnEvent='news-'+id;text($('eventDialogTitle'),'Tibo · '+(item?newsLabel(item):'重置消息'));content.replaceChildren();
 if(item){const article=element('article','news-detail'),link=element('a','','在 X 查看原文');link.href=item.url;link.target='_blank';link.rel='noopener noreferrer';
  const translated=hasNewsTranslation(item),source=element('a','','來源：Codex Resets');source.href='https://codex-resets.com/zh-TW';source.target='_blank';source.rel='noopener noreferrer';
  article.append(newsIcon(item.kind),element('span','news-kind','@thsottiaux'),element('p','news-confidence',newsConfidence(item)),element('h3','',newsLabel(item)),element('p','',(item.kind==='watch'?'來源收錄 ':'發文 ')+date(item.at)+'（本地時間）'),element('blockquote','',translated?item.zhText:item.text),element('p','translation-status',translated?'自動翻譯 · 內容以原文為準':'中文翻譯暫不可用，先顯示原文'));
  if(translated){const original=element('details','news-original');original.append(element('summary','','查看英文原文'),element('blockquote','',item.text));article.append(original);}
  article.append(element('p','news-proof',newsDisclaimer(item)),detailReplayButton(article),link,source);content.append(article);replayDetailEffect(article);
 }else content.append(element('p','empty-events','這則消息尚未載入或已超過保留期限。'));
 if(!dialog.open){history.pushState({...history.state,usageNotice:true},'',location.href);dialog.showModal();document.documentElement.classList.add('notice-open');}$('eventDialogClose').focus();
}
function renderResetNews(){
 $('newsList').replaceChildren();
 const latest=$('newsLatest'),announcement=resetNewsItems.filter(item=>item.kind==='regular').sort((a,b)=>new Date(b.at)-new Date(a.at))[0];latest.replaceChildren();latest.hidden=!announcement;
 if(announcement){const summary=element('div');summary.append(element('span','news-latest-label','最新重置公告'),element('strong','','已宣布，帳戶狀態待確認'),element('time','',date(announcement.at)+'（本地時間）'));latest.append(newsIcon('regular'),summary);}
 for(const item of resetNewsItems){const row=element('li','news-row'),copy=element('div','news-copy'),top=element('div','news-top'),time=element('time','',shortEventDate(item.at));time.dateTime=item.at;time.title=date(item.at);top.append(element('span','news-kind',newsLabel(item)),time);const translated=hasNewsTranslation(item);copy.append(top,element('h3','',translated?item.zhText:item.text),element('span','translation-status',newsConfidence(item)+' · '+(translated?'中文':'原文')));const arrow=element('span','news-arrow','›');arrow.setAttribute('aria-hidden','true');row.append(newsIcon(item.kind),copy,arrow);row.dataset.event='news-'+item.id;row.tabIndex=0;row.setAttribute('role','button');row.setAttribute('aria-haspopup','dialog');row.setAttribute('aria-label','Tibo '+newsLabel(item)+' '+shortEventDate(item.at)+'，查看原文');row.onclick=()=>openNewsDetail(item.id);row.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();openNewsDetail(item.id);}};$('newsList').append(row);}
 if(!resetNewsItems.length)$('newsList').append(element('li','empty-events','最近 7 天尚無可顯示的 Tibo 重置消息。'));
}
function loadResetNews(force=false){
 if(!paired){text($('newsState'),'請先配對此裝置');return;}
 if(newsBusy)return newsPending;
 if(!force&&Date.now()-newsLoadedAt<300000)return;
 const generation=newsGeneration;newsBusy=true;text($('newsState'),'讀取消息中');
 newsPending=(async()=>{
 try{const feed=await api('reset-news');if(generation!==newsGeneration)return;
  if(!Array.isArray(feed.items))throw Error('消息服務尚未更新');
  const ids=new Set();resetNewsItems=feed.items.filter(item=>item&&validNewsId(item.id)&&!ids.has(item.id)&&ids.add(item.id)&&['regular','banked','scheduled','watch'].includes(item.kind)&&typeof item.text==='string'&&item.text.length<=16000&&item.url==='https://x.com/thsottiaux/status/'+item.id&&Number.isFinite(new Date(item.at).getTime())&&new Date(item.at)>=Date.now()-7*86400000&&new Date(item.at)<=Date.now()+60000).slice(0,30);
  newsLoadedAt=Date.now();renderResetNews();if(!feed.fetchedAt&&!resetNewsItems.length)$('newsList').replaceChildren();text($('newsState'),feed.fetchedAt?(feed.stale?'來源暫時無法更新 · 快取 ':feed.partial?'部分來源尚未更新 · ':'來源更新 ')+date(feed.fetchedAt):'來源暫時無法取得，請稍後再試');
 }catch(error){if(generation===newsGeneration)text($('newsState'),resetNewsItems.length?'消息更新失敗，保留先前內容':error.message);}
 finally{newsBusy=false;}
 })();return newsPending;
}
async function openResetNews(id){selectView('news');await loadResetNews(true);if(validNewsId(id)&&paired)openNewsDetail(id);}
function setupResetNews(){
 const button=element('button');button.dataset.view='news';button.innerHTML='<svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M7 7h10M7 11h10M7 15h4M7 18h4M15 15h2v3h-2z"/></svg>';button.append(document.createTextNode('重置消息'));document.querySelector('[data-view="settings"]').before(button);
 $('resetNews').checked=prefs.resetNews===true;
 text($('newsPreferenceStatus'),'電腦工具開啟並連網時推播；首次開啟不補發舊消息。');
 $('resetNews').onchange=async()=>{prefs.resetNews=$('resetNews').checked;await savePreferences();text($('newsPreferenceStatus'),prefs.resetNews?'已選擇新消息推播，請確認設定頁的手機通知已連接。':'新消息推播已關閉');};
}
