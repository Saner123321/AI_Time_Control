import {test} from 'node:test';
import assert from 'node:assert/strict';
import {buildRules} from '../browser-extension/beta/rules.js';
test('actual worker clears previous blocks, confirms readback, and never acknowledges a failed update',async()=>{
  const saved={chrome:globalThis.chrome,setInterval:globalThis.setInterval,setTimeout:globalThis.setTimeout};
  let listener,pulse,statusListener,fail=false,session=[],writes=0,supported=()=>true;const messages=[];
  let installed=buildRules({groups:[{mode:'BlockSelected',domains:['example.com'],exceptions:[]}]});
  const port={onMessage:{addListener:f=>listener=f},onDisconnect:{addListener:()=>{}},postMessage:m=>messages.push(m)};
  globalThis.setInterval=f=>{pulse=f;return 1;};globalThis.setTimeout=()=>1;
  globalThis.chrome={storage:{local:{get:async()=>({profileId:'1'.repeat(32)}),set:async()=>{}}},runtime:{connectNative:()=>port,getURL:p=>'chrome-extension://test/'+p,onMessage:{addListener:f=>statusListener=f}},windows:{getLastFocused:async()=>({focused:true})},tabs:{query:async()=>[{id:1,url:'https://example.com/'}],update:async()=>{},onUpdated:{addListener:()=>{}},create:async()=>{}},idle:{queryState:async()=>'active'},declarativeNetRequest:{isRegexSupported:async({regex})=>({isSupported:supported(regex)}),getSessionRules:async()=>structuredClone(session),updateSessionRules:async({addRules=[],removeRuleIds=[]})=>{session=[...session.filter(r=>!removeRuleIds.includes(r.id)),...structuredClone(addRules)];},getDynamicRules:async()=>structuredClone(installed),updateDynamicRules:async({addRules=[],removeRuleIds=[]})=>{writes++;if(fail)throw Error('simulated browser update failure');assert.ok(addRules.every(r=>r.condition.requestDomains===undefined || r.condition.requestDomains.length>0));installed=[...installed.filter(r=>!removeRuleIds.includes(r.id)),...structuredClone(addRules)];}},action:{setBadgeText:async()=>{},onClicked:{addListener:()=>{}}},alarms:{create:()=>{},onAlarm:{addListener:()=>{}}}};
  try{
    await import('../browser-extension/beta/worker.js?release-test');await new Promise(resolve=>setImmediate(resolve));
    await listener({ok:true,browserPlan:{fingerprint:'empty',required:false,groups:[{mode:'BlockSelected',domains:[],exceptions:[]}]}});
    assert.deepEqual(installed,[]);await pulse();assert.equal(messages.at(-1).appliedPlan,'empty');
    let status;statusListener({type:'block-status',domain:'example.com'},{url:'chrome-extension://test/blocked.html'},r=>status=r);assert.deepEqual(status,{known:true,blocked:false,reasons:[]});
    await listener({ok:true,browserPlan:{fingerprint:'all',required:true,groups:[{mode:'AllowSelected',domains:[],exceptions:[]}]}});
    assert.equal(installed.length,2);await pulse();assert.equal(messages.at(-1).appliedPlan,'all');
    await listener({ok:true,browserPlan:{fingerprint:'pages',required:true,groups:[{mode:'BlockSelected',domains:['*example*'],exceptions:['example.com/allowed']}]}});
    assert.equal(installed[0].action.type,'redirect');
    const gate=(url,tab=1)=>new Promise(resolve=>assert.equal(statusListener({type:'gate',url},{url:'chrome-extension://test/gate.html#'+url,tab:{id:tab}},resolve),true));
    assert.equal((await gate('https://example.com/blocked')).blocked,true);
    assert.equal(session.length,0);
    assert.equal((await gate('https://example.com/allowed?tracking=1')).blocked,false);
    assert.equal(session.length,1);assert.deepEqual(session[0].condition.tabIds,[1]);
    assert.equal(new RegExp(session[0].condition.regexFilter).test('https://example.com/allowed?tracking=1'),true);
    assert.equal(new RegExp(session[0].condition.regexFilter).test('https://example.com/blocked'),false);
    await listener({ok:true,browserPlan:{fingerprint:'overlap',required:true,groups:[{mode:'BlockSelected',domains:['*example*'],exceptions:['example.com/allowed']},{mode:'BlockSelected',domains:['example.com/allowed'],exceptions:[]}]}});
    assert.deepEqual(session,[]);assert.equal((await gate('https://example.com/allowed')).blocked,true);
    assert.equal(statusListener({type:'gate',url:'https://example.com/'},{url:'https://evil.test/',tab:{id:1}},()=>{}),false);
    const writesBefore=writes,now=Date.now;try{Date.now=()=>now()+6000;await listener({ok:true,browserPlan:{fingerprint:'overlap',required:true,groups:[{mode:'BlockSelected',domains:['*example*'],exceptions:['example.com/allowed']},{mode:'BlockSelected',domains:['example.com/allowed'],exceptions:[]}]}});}finally{Date.now=now;}
    assert.equal(writes,writesBefore,'periodic readback must not reinstall unchanged rules');
    supported=regex=>regex==='(^https?://.*$)';
    await listener({ok:true,browserPlan:{fingerprint:'fallback',required:true,groups:[{mode:'BlockSelected',domains:['*example*'],exceptions:['example.com/allowed']}]}});
    assert.equal(installed.length,1);assert.equal(installed[0].condition.regexFilter,'(^https?://.*$)');
    await pulse();assert.equal(messages.at(-1).appliedPlan,'fallback');
    supported=()=>false;const quietError=console.error;console.error=()=>{};try{await listener({ok:true,browserPlan:{fingerprint:'unsupported',required:true,groups:[{mode:'BlockSelected',domains:['example.com'],exceptions:[]}]}});}finally{console.error=quietError;}
    await pulse();assert.equal(messages.at(-1).appliedPlan,'','unsupported mandatory guard must not be acknowledged');assert.equal(installed.length,1);
    supported=()=>true;await listener({ok:true,browserPlan:{fingerprint:'restore-overlap',required:true,groups:[{mode:'BlockSelected',domains:['*example*'],exceptions:[]},{mode:'BlockSelected',domains:['example.com/allowed'],exceptions:[]}]}});
    fail=true;const originalError=console.error;console.error=()=>{};try{await listener({ok:true,browserPlan:{fingerprint:'failed',required:false,groups:[]}});}finally{console.error=originalError;}
    await pulse();assert.equal(messages.at(-1).appliedPlan,'');assert.equal(installed.length,2);
    statusListener({type:'block-status',domain:'example.com'},{url:'chrome-extension://test/blocked.html'},r=>status=r);assert.equal(status.known,false);
  }finally{globalThis.chrome=saved.chrome;globalThis.setInterval=saved.setInterval;globalThis.setTimeout=saved.setTimeout;}
});


test('worker reconnects a hung native bridge and sends focus changes without waiting for the timer',async()=>{
  const saved={chrome:globalThis.chrome,setInterval:globalThis.setInterval,setTimeout:globalThis.setTimeout,performance:globalThis.performance};
  let pulse,focus,now=0;const timers=[],ports=[];
  const event=()=>({addListener:f=>{event.last=f;}});
  const newPort=()=>{const p={messages:[],disconnected:false,onMessage:{addListener:f=>p.reply=f},onDisconnect:{addListener:f=>p.closed=f},postMessage:m=>p.messages.push(m),disconnect:()=>{p.disconnected=true;p.closed();}};ports.push(p);return p;};
  globalThis.performance={now:()=>now};globalThis.setInterval=f=>{pulse=f;return 1;};globalThis.setTimeout=(f,ms)=>{timers.push({f,ms});return timers.length;};
  globalThis.chrome={storage:{local:{get:async()=>({profileId:'2'.repeat(32)}),set:async()=>{}}},runtime:{connectNative:newPort,getURL:p=>'chrome-extension://test/'+p,onMessage:event()},windows:{getLastFocused:async()=>({focused:true}),onFocusChanged:{addListener:f=>focus=f}},tabs:{query:async()=>[],onUpdated:event(),onActivated:event(),onRemoved:event()},idle:{queryState:async()=>'active'},declarativeNetRequest:{getDynamicRules:async()=>[],getSessionRules:async()=>[],updateSessionRules:async()=>{}},action:{setBadgeText:async()=>{},onClicked:event()},alarms:{create:()=>{},onAlarm:event()}};
  const flush=()=>new Promise(resolve=>setImmediate(resolve));
  try{
    await import('../browser-extension/beta/worker.js?native-watchdog-test');await flush();
    assert.equal(ports[0].messages.length,1);now=6001;await pulse();assert.equal(ports[0].disconnected,true);
    const reconnect=timers.find(t=>t.ms===0);assert.ok(reconnect);reconnect.f();await flush();assert.equal(ports.length,2);assert.equal(ports[1].messages.length,1);
    await ports[0].reply({ok:true,browserPlan:{fingerprint:'stale',required:false,groups:[]}});await pulse();assert.equal(ports[1].messages.length,1,'stale reply must not release the current request');
    const empty={fingerprint:'empty',required:false,groups:[]};
    // Same-plan reply after the initial rule readback avoids needing a native browser.
    globalThis.chrome.declarativeNetRequest.isRegexSupported=async()=>({isSupported:true});
    await ports[1].reply({ok:true,browserPlan:empty});focus();await flush();assert.equal(ports[1].messages.length,2);assert.equal(ports[1].messages.at(-1).appliedPlan,'empty');
    focus();await ports[1].reply({ok:true,browserPlan:empty});await flush();assert.equal(ports[1].messages.length,3,'a focus event during a pending reply must not get lost');
  }finally{Object.assign(globalThis,saved);}
});
