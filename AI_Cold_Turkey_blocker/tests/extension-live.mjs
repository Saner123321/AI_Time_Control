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
let stderr='';browser.stderr.on('data',b=>stderr+=b);let seq=0,buffer='',waiting=new Map();
browser.stdio[4].on('data',b=>{buffer+=b.toString();for(let i;(i=buffer.indexOf('\0'))>=0;){const text=buffer.slice(0,i);buffer=buffer.slice(i+1);try{const m=JSON.parse(text);if(m.id){const p=waiting.get(m.id);if(p){waiting.delete(m.id);m.error?p.reject(Error(JSON.stringify(m.error))):p.resolve(m.result);}}}catch{}}});
function cdp(method,params={},sessionId){return new Promise((resolve,reject)=>{const id=++seq;const timer=setTimeout(()=>{waiting.delete(id);reject(Error('CDP timeout '+method));},15000);waiting.set(id,{resolve:r=>{clearTimeout(timer);resolve(r);},reject:e=>{clearTimeout(timer);reject(e);}});browser.stdio[3].write(JSON.stringify({id,method,params,...(sessionId?{sessionId}:{})})+'\0');});}
const sleep=ms=>new Promise(r=>setTimeout(r,ms));let result={passed:false,out,cases:[]};
try{
  result.loaded=await cdp('Extensions.loadUnpacked',{path:ext});
  let worker;for(let i=0;i<40;i++){const t=await cdp('Target.getTargets');worker=t.targetInfos.find(t=>t.type==='service_worker' && t.url.includes('/test-worker.js'));if(worker)break;await sleep(250);}if(!worker)throw Error('Isolated Edge did not load test extension');
  const ws=(await cdp('Target.attachToTarget',{targetId:worker.targetId,flatten:true})).sessionId;
  const run=async(expression,session=ws)=>(await cdp('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true},session));
  async function plan(groups,key){const p={groups,fingerprint:key,required:groups.length>0};const r=await run(`(async()=>{globalThis.__plan=${JSON.stringify(p)};await __listeners[0]({ok:true,browserPlan:__plan});return await chrome.declarativeNetRequest.getDynamicRules();})()`);if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));}
  const page=(await cdp('Target.createTarget',{url:'about:blank'})).targetId,ps=(await cdp('Target.attachToTarget',{targetId:page,flatten:true})).sessionId;
  async function nav(path,expectAllowed){requests=[];await cdp('Page.navigate',{url:base+path},ps);let href='',confirmed=false;for(let i=0;i<40;i++){await sleep(150);const r=await run('({href:location.href,text:document.body?.innerText})',ps);href=r.result?.value?.href||'';confirmed=expectAllowed?href===base+path:r.result?.value?.text?.includes('当前页面受到专注计划保护');if(confirmed)break;}
    const seen=requests.includes(path);if(!confirmed || (expectAllowed?!seen:seen)){result.page=await run('document.body.innerText',ps);result.debug=await run('(async()=>({errors:globalThis.__errors,dynamic:await chrome.declarativeNetRequest.getDynamicRules(),pulse:globalThis.__lastPulse,session:await chrome.declarativeNetRequest.getSessionRules(),plan:globalThis.__plan}))()');throw Error('Request assertion failed '+path+' href='+href+' requests='+requests);}result.cases.push({path,expectAllowed,serverReached:seen,href});}
  const group={id:'1',mode:'BlockSelected',domains:['*127.0.0.1*'],exceptions:['127.0.0.1:'+server.address().port+'/allowed','*favlist*'],reasons:['Always']};
  await plan([group],'a');await nav('/blocked',false);await nav('/allowed?tracking=one',true);await nav('/other',false);await nav('/favlist',true);
  await nav('/allowed',true);await run("history.pushState({},'', '/blocked-spa')",ps);await sleep(650);const spa=(await run('location.href',ps)).result.value;if(!spa.includes('/blocked.html#') && !spa.includes('/gate.html#'))throw Error('SPA URL change was not blocked');result.cases.push({spa:true,blocked:true});
  await plan([group,{...group,id:'2',domains:['*favlist*'],exceptions:[]}],'b');await nav('/favlist',false);
  await plan([{...group,exceptions:[]}],'c');await nav('/allowed?tracking=one',false);
  await plan([],'d');await nav('/allowed?tracking=one',true);await nav('/blocked',true);
  result.passed=true;result.browser=await cdp('Browser.getVersion');
}catch(e){result.error=e.stack;}finally{await writeFile(join(out,'result.json'),JSON.stringify(result,null,2));await writeFile(join(out,'browser-stderr.txt'),stderr);try{await cdp('Browser.close');}catch{}browser.kill();server.close();}
console.log(JSON.stringify(result,null,2));if(!result.passed)process.exitCode=1;
