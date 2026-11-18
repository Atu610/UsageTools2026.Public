const now=()=>new Date().toISOString(),later=hours=>new Date(Date.now()+hours*3600000).toISOString();
function notification(kind='LowRemaining',i=1){return {id:i.toString(16).padStart(32,'0'),at:now(),provider:kind==='UsageReset'?'claude':'codex',kind,window:kind==='ResetCreditsAdded'?'':kind==='UsageReset'?'5H':'7D',remaining:kind==='UsageReset'?100:20,creditCount:3,addedCount:1,plan:'Pro',resetsAt:later(kind==='UsageReset'?5:72),expiresAt:later(24*12),creditExpirations:kind==='ResetCreditsAdded'?[later(24*12),later(24*19),later(24*27)]:[]};}
function fixture(scenario='weekly'){
 const codexWindows=[{label:'7D',used:80,resetsAt:later(72)}];if(scenario==='dual')codexWindows.unshift({label:'5H',used:45,resetsAt:later(3)});
 const claudeGift={...notification('ResetCreditsAdded',4),provider:'claude',creditKind:'full',creditCount:3,addedCount:1,creditExpirations:[later(130),later(24*29),later(24*29)]};
 const fableLow={...notification('LowRemaining',5),provider:'claude',window:'7D',pool:'Fable',remaining:18};
 const notifications=[claudeGift,fableLow,notification('LowRemaining'),notification('ResetCreditsAdded',2),notification('UsageReset',3)];
 notifications.forEach((e,i)=>e.at=new Date(Date.now()-i*300000).toISOString());
 return {publicKey:'fixture',account:'preview',notifications,usage:{schemaVersion:2,fixture:true,themeId:'deep-night',receivedAt:now(),resetCredits:{available:3,syncedAt:now(),stale:false,expirations:[later(24*12),later(24*19),later(24*27)]},providers:[
  {name:'Codex',enabled:true,plan:scenario==='dual'?'Plus':'Pro US$200/mo',state:'Fresh',syncedAt:now(),windows:codexWindows},
  {name:'Claude Code',enabled:scenario!=='hidden',plan:'Pro',state:scenario==='hidden'?'Disabled':'Fresh',syncedAt:now(),windows:[{label:'5H',used:0,resetsAt:later(5)},{label:'7D',used:28,resetsAt:later(110)}]}
 ],creditBalance:{balance:61740.53,unlimited:false,syncedAt:now(),stale:false},planConfirmed:scenario!=='dual',
  claudeDetails:scenario==='hidden'?null:claudeDetails()}};
}
// FIXTURE ONLY: mirrors the 2026-11-11 Pro response shape (two grants, Fable weekly pool).
function claudeDetails(){return {syncedAt:now(),pools:[{label:'5H',model:null,used:0,resetsAt:later(5),allowance:null},{label:'7D',model:null,used:28,resetsAt:later(110),allowance:null},{label:'7D',model:'Fable',used:12,resetsAt:later(110),allowance:50}],
 resets:{available:3,eligible:true,syncedAt:now(),grants:[{kind:'fable',left:1,total:1,expiresAt:later(130),usableNow:true},{kind:'full',left:2,total:2,expiresAt:later(24*29),usableNow:true}]},
 models:{count:13,primary:['Claude Fable 5.2','Claude Opus 5.5','Claude Sonnet 5.5','Claude Haiku 5.5'],syncedAt:now()},
 extraUsage:{enabled:false,used:0,limit:null,currency:'USD',limitReached:false}};}
module.exports={fixture,notification};
