const http=require('node:http'),fs=require('node:fs'),path=require('node:path');
const root=__dirname,port=Number(process.argv[2]||8766),key='a'.repeat(64);
const {fixture,notification}=require('./preview-data.cjs');let data=fixture(),sequence=10;
const server=http.createServer((req,res)=>{
 const requested=new URL(req.url,'http://localhost');
 if(requested.pathname==='/fixture'){
  if(req.method!=='POST'||req.headers.origin!=='http://127.0.0.1:'+port){res.writeHead(403);res.end();return;}
  const scenario=requested.searchParams.get('scenario'),kind=requested.searchParams.get('event');
  if(['weekly','dual','hidden'].includes(scenario))data=fixture(scenario);
  if(['LowRemaining','ResetCreditsAdded','UsageReset'].includes(kind)){data.notifications.unshift(notification(kind,sequence++));data.notifications=data.notifications.slice(0,100);}
  if(requested.searchParams.has('theme'))data.usage.themeId=requested.searchParams.get('theme');
  res.setHeader('Content-Type','application/json');res.end(JSON.stringify({fixture:true}));return;
 }
 if(req.url.startsWith('/api.php')){
  res.setHeader('Content-Type','application/json');res.setHeader('Cache-Control','no-store');
  if(requested.searchParams.get('op')==='reset-news'){res.end(JSON.stringify({items:[{id:'2109873287853707513',kind:'regular',at:new Date(Date.now()-3600000).toISOString(),text:'[LOCAL DEMO] The reset has been rolled out. Please check your account for the actual allowance.',zhText:'【示例】用量重置已陸續推出，請查看自己的帳戶確認最新額度。',translationStatus:'translated',url:'https://x.com/thsottiaux/status/2109873287853707513'},{id:'2109132167112782291',kind:'banked',at:new Date(Date.now()-86400000).toISOString(),text:'[LOCAL DEMO] New reset credits are available. Check the expiration date before using them.',zhText:'【示例】新增重置券已開放領取，使用前請留意每張券的到期時間。',translationStatus:'translated',url:'https://x.com/thsottiaux/status/2109132167112782291'},{id:'1111111111111111111',kind:'scheduled',at:new Date(Date.now()-2*86400000).toISOString(),text:'[LOCAL DEMO] Another reset is planned. The timing is not confirmed yet.',zhText:'【示例】預計安排下一次重置，實際時間仍待確認。',translationStatus:'translated',url:'https://x.com/thsottiaux/status/1111111111111111111'}],fetchedAt:new Date().toISOString(),stale:false}));return;}
  data.usage.receivedAt=new Date().toISOString();for(const p of data.usage.providers)p.syncedAt=data.usage.receivedAt;if(data.usage.creditBalance)data.usage.creditBalance.syncedAt=data.usage.receivedAt;if(data.usage.claudeDetails)data.usage.claudeDetails.syncedAt=data.usage.receivedAt;
  res.end(JSON.stringify(data));return;
 }
 const url=new URL(req.url,'http://localhost'),file=path.resolve(root,url.pathname==='/'?'index.html':'.'+decodeURIComponent(url.pathname));
 if(!file.startsWith(root+path.sep)||!fs.existsSync(file)||!fs.statSync(file).isFile()||!['.html','.js','.css','.png','.jpg','.webmanifest'].includes(path.extname(file))&&!file.endsWith('manifest.php')){res.writeHead(404);res.end();return;}
 const actual=file.endsWith('manifest.php')?path.join(root,'manifest.webmanifest'):file;
 res.setHeader('Content-Type',actual.endsWith('.js')?'text/javascript':actual.endsWith('.css')?'text/css':actual.endsWith('.png')?'image/png':actual.endsWith('.jpg')?'image/jpeg':actual.endsWith('.webmanifest')?'application/manifest+json':'text/html');
 res.setHeader('Cache-Control','no-cache');res.end(fs.readFileSync(actual));
});
server.listen(port,'127.0.0.1',()=>console.log('FIXTURE ONLY http://127.0.0.1:'+port+'/preview.html'));
