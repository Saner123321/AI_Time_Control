// Runs only an isolated browser profile and a loopback HTTP fixture. No Windows policy writes.
import {spawn} from 'node:child_process';
import {mkdir,cp,writeFile,readFile} from 'node:fs/promises';
import {resolve,join} from 'node:path';
import http from 'node:http';
const root=resolve(import.meta.dirname,'..'),out=join(root,'artifacts','extension-live-'+Date.now()),ext=join(out,'Extension');
await mkdir(out,{recursive:true});await cp(join(root,'browser-extension/beta'),ext,{recursive:true});
const mock=`globalThis.__errors=[];console.error=(...e)=>__errors.push(e.map(x=>x?.stack||String(x)).join(' '));globalThis.__listeners=[];globalThis.__fakePort={onMessage:{addListener:f=>__listeners.push(f)},onDisconnect:{addListener:()=>{}},postMessage:m=>{globalThis.__lastPulse=m; if(globalThis.__plan)queueMicrotask(()=>__listeners[0]({ok:true,browserPlan:__plan}));}};chrome.runtime.connectNative=()=>__fakePort;\n`;
await writeFile(join(ext,'mock.js'),mock);await writeFile(join(ext,'test-worker.js'),`import './mock.js';import './worker.js';`);
const manifest=JSON.parse(await readFile(join(ext,'manifest.json'),'utf8'));manifest.background.service_worker='test-worker.js';await writeFile(join(ext,'manifest.json'),JSON.stringify(manifest));
let requests=[];const server=http.createServer((req,res)=>{requests.push(req.url);res.setHeader('Content-Type','text/html');res.end('<title>AICT fixture</title><body>Allowed fixture '+req.url+'</body>');});await new Promise(r=>server.listen(0,'127.0.0.1',r));const base='http://127.0.0.1:'+server.address().port;
const edge=process.argv.includes('--chrome')?'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe':'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const browser=spawn(edge,['--headless=new','--no-first-run','--disable-gpu','--user-data-dir='+join(out,'Profile'),'--remote-debugging-pipe','--enable-unsafe-extension-debugging','about:blank'],{stdio:['ignore','pipe','pipe','pipe','pipe'],windowsHide:true});
let stderr='';browser.stderr.on('data',b=>stderr+=b);let seq=0,buffer='',waiting=new Map(),navigations=[];
browser.stdio[4].on('data',b=>{buffer+=b.toString();for(let i;(i=buffer.indexOf('\0'))>=0;){const text=buffer.slice(0,i);buffer=buffer.slice(i+1);try{const m=JSON.parse(text);if(m.method==='Page.frameNavigated'&&!m.params.frame.parentId)navigations.push({session:m.sessionId,url:m.params.frame.url});if(m.id){const p=waiting.get(m.id);if(p){waiting.delete(m.id);m.error?p.reject(Error(JSON.stringify(m.error))):p.resolve(m.result);}}}catch{}}});
function cdp(method,params={},sessionId){return new Promise((resolve,reject)=>{const id=++seq;const timer=setTimeout(()=>{waiting.delete(id);reject(Error('CDP timeout '+method));},15000);waiting.set(id,{resolve:r=>{clearTimeout(timer);resolve(r);},reject:e=>{clearTimeout(timer);reject(e);}});browser.stdio[3].write(JSON.stringify({id,method,params,...(sessionId?{sessionId}:{})})+'\0');});}
const sleep=ms=>new Promise(r=>setTimeout(r,ms));let result={passed:false,out,cases:[]};
try{
  result.loaded=await cdp('Extensions.loadUnpacked',{path:ext});
  let worker;for(let i=0;i<40;i++){const t=await cdp('Target.getTargets');worker=t.targetInfos.find(t=>t.type==='service_worker' && t.url.includes('/test-worker.js'));if(worker)break;await sleep(250);}if(!worker)throw Error('Isolated Edge did not load test extension');
  const ws=(await cdp('Target.attachToTarget',{targetId:worker.targetId,flatten:true})).sessionId;
  const run=async(expression,session=ws)=>(await cdp('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true},session));
  let ready=false;for(let i=0;i<40;i++){ready=(await run('typeof globalThis.__listeners?.[0]==="function"')).result?.value===true;if(ready)break;await sleep(50);}if(!ready)throw Error('Test native bridge listener did not initialize');
  async function plan(groups,key){const p={groups,fingerprint:key,required:groups.length>0};const r=await run(`(async()=>{globalThis.__plan=${JSON.stringify(p)};await __listeners[0]({ok:true,browserPlan:__plan});return await chrome.declarativeNetRequest.getDynamicRules();})()`);if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));}
  const page=(await cdp('Target.createTarget',{url:'about:blank'})).targetId,ps=(await cdp('Target.attachToTarget',{targetId:page,flatten:true})).sessionId;
  await cdp('Page.enable',{},ps);
  await cdp('Emulation.setDeviceMetricsOverride',{width:1100,height:850,deviceScaleFactor:1,mobile:false},ps);
  async function nav(path,expectAllowed,noGate=false){requests=[];navigations=[];await cdp('Page.navigate',{url:base+path},ps);let href='',confirmed=false;for(let i=0;i<40;i++){await sleep(150);const r=await run('({href:location.href,text:document.body?.innerText})',ps);href=r.result?.value?.href||'';confirmed=expectAllowed?href===base+path:r.result?.value?.text?.includes('当前页面受到专注计划保护');if(confirmed)break;}
    const seen=requests.includes(path);if(!confirmed || (expectAllowed?!seen:seen)){result.page=await run('document.body.innerText',ps);result.debug=await run('(async()=>({errors:globalThis.__errors,dynamic:await chrome.declarativeNetRequest.getDynamicRules(),pulse:globalThis.__lastPulse,session:await chrome.declarativeNetRequest.getSessionRules(),plan:globalThis.__plan}))()');throw Error('Request assertion failed '+path+' href='+href+' requests='+requests);}const hops=navigations.filter(n=>n.session===ps).map(n=>n.url);if(noGate&&(!hops.includes(base+path)||hops.some(u=>u.startsWith('chrome-extension://'))))throw Error('Unnecessary extension intermediary '+path+' '+JSON.stringify(hops));result.cases.push({path,expectAllowed,serverReached:seen,href,noGate,hops});}
  const group={id:'1',name:'测试专注规则',mode:'BlockSelected',domains:['*127.0.0.1*'],exceptions:['127.0.0.1:'+server.address().port+'/allowed','*favlist*'],reasons:['QuotaExhausted'],details:['上午 09:00—12:00 的20分钟额度已用完。']};
  await plan([group],'a');await nav('/blocked',false);const reasonText=(await run('document.body.innerText',ps)).result.value;if(!reasonText.includes('命中规则：测试专注规则')||!reasonText.includes('20分钟额度已用完'))throw Error('Specific block reason was not displayed');
  const shot=await cdp('Page.captureScreenshot',{format:'png'},ps);await writeFile(join(out,'block-reasons.png'),Buffer.from(shot.data,'base64'));
  await nav('/allowed?tracking=one',true);await nav('/other',false);await nav('/favlist',true);
  await nav('/allowed',true);await run("history.pushState({},'', '/blocked-spa')",ps);await sleep(650);const spa=(await run('location.href',ps)).result.value;if(!spa.includes('/blocked.html#') && !spa.includes('/gate.html#'))throw Error('SPA URL change was not blocked');result.cases.push({spa:true,blocked:true});
  await plan([group,{...group,id:'2',domains:['*favlist*'],exceptions:[]}],'b');await nav('/favlist',false);
  await plan([{...group,exceptions:[]}],'c');await nav('/allowed?tracking=one',false);
  await plan([],'d');await nav('/allowed?tracking=one',true);await nav('/blocked',true);
  await plan([{...group,name:'整站时段规则',domains:['127.0.0.1'],exceptions:[],reasons:['WeeklySchedule'],details:['处于每周硬封锁时段：09:00—12:00。']}],'e');await nav('/domain-only',false);const domainReason=(await run('document.body.innerText',ps)).result.value;if(!domainReason.includes('整站时段规则')||!domainReason.includes('每周硬封锁时段'))throw Error('Domain rule reason was not displayed');await plan([],'f');await nav('/domain-only',true);
  const smooth={...group,domains:['*distraction*'],exceptions:['127.0.0.1:'+server.address().port+'/distraction/lesson','*favlist*']};
  await plan([smooth],'smooth');await nav('/ordinary',true,true);await nav('/distraction/lesson?tracking=one',true,true);await nav('/distraction/favlist',true,true);await nav('/distraction/blocked',false);
  await plan([smooth,{...group,id:'other',domains:['*lesson*'],exceptions:[]}],'smooth-overlap');await nav('/distraction/lesson?tracking=one',false);
  await plan([{...group,mode:'AllowSelected',domains:['127.0.0.1:'+server.address().port+'/distraction/lesson'],exceptions:[]}],'smooth-allowlist');await nav('/distraction/lesson?tracking=one',true,true);await nav('/ordinary',false);
  await plan([],'smooth-clear');await nav('/ordinary',true,true);
  result.passed=true;result.browser=await cdp('Browser.getVersion');
}catch(e){result.error=e.stack;}finally{await writeFile(join(out,'result.json'),JSON.stringify(result,null,2));await writeFile(join(out,'browser-stderr.txt'),stderr);try{await cdp('Browser.close');}catch{}browser.kill();server.close();}
console.log(JSON.stringify(result,null,2));if(!result.passed)process.exitCode=1;
