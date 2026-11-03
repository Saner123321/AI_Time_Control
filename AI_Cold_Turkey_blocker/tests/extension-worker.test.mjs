import {test} from 'node:test';
import assert from 'node:assert/strict';
import {buildRules} from '../browser-extension/beta/rules.js';
test('actual worker clears previous blocks, confirms readback, and never acknowledges a failed update',async()=>{
  const saved={chrome:globalThis.chrome,setInterval:globalThis.setInterval,setTimeout:globalThis.setTimeout};
  let listener,pulse,statusListener,fail=false,session=[];const messages=[];
  let installed=buildRules({groups:[{mode:'BlockSelected',domains:['example.com'],exceptions:[]}]});
  const port={onMessage:{addListener:f=>listener=f},onDisconnect:{addListener:()=>{}},postMessage:m=>messages.push(m)};
  globalThis.setInterval=f=>{pulse=f;return 1;};globalThis.setTimeout=()=>1;
  globalThis.chrome={storage:{local:{get:async()=>({profileId:'1'.repeat(32)}),set:async()=>{}}},runtime:{connectNative:()=>port,getURL:p=>'chrome-extension://test/'+p,onMessage:{addListener:f=>statusListener=f}},windows:{getLastFocused:async()=>({focused:true})},tabs:{query:async()=>[{id:1,url:'https://example.com/'}],update:async()=>{},onUpdated:{addListener:()=>{}},create:async()=>{}},idle:{queryState:async()=>'active'},declarativeNetRequest:{getSessionRules:async()=>structuredClone(session),updateSessionRules:async({addRules=[],removeRuleIds=[]})=>{session=[...session.filter(r=>!removeRuleIds.includes(r.id)),...structuredClone(addRules)];},getDynamicRules:async()=>structuredClone(installed),updateDynamicRules:async({addRules=[],removeRuleIds=[]})=>{if(fail)throw Error('simulated browser update failure');assert.ok(addRules.every(r=>r.condition.requestDomains===undefined || r.condition.requestDomains.length>0));installed=[...installed.filter(r=>!removeRuleIds.includes(r.id)),...structuredClone(addRules)];}},action:{setBadgeText:async()=>{},onClicked:{addListener:()=>{}}},alarms:{create:()=>{},onAlarm:{addListener:()=>{}}}};
  try{
    await import('../browser-extension/beta/worker.js?release-test');await new Promise(resolve=>setImmediate(resolve));
    await listener({ok:true,browserPlan:{fingerprint:'empty',required:false,groups:[{mode:'BlockSelected',domains:[],exceptions:[]}]}});
    assert.deepEqual(installed,[]);await pulse();assert.equal(messages.at(-1).appliedPlan,'empty');
    let status;statusListener({type:'block-status',domain:'example.com'},{},r=>status=r);assert.deepEqual(status,{known:true,blocked:false});
    await listener({ok:true,browserPlan:{fingerprint:'all',required:true,groups:[{mode:'AllowSelected',domains:[],exceptions:[]}]}});
    assert.equal(installed.length,1);await pulse();assert.equal(messages.at(-1).appliedPlan,'all');
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
    fail=true;const originalError=console.error;console.error=()=>{};try{await listener({ok:true,browserPlan:{fingerprint:'failed',required:false,groups:[]}});}finally{console.error=originalError;}
    await pulse();assert.equal(messages.at(-1).appliedPlan,'');assert.equal(installed.length,1);
    statusListener({type:'block-status',domain:'example.com'},{},r=>status=r);assert.equal(status.known,false);
  }finally{globalThis.chrome=saved.chrome;globalThis.setInterval=saved.setInterval;globalThis.setTimeout=saved.setTimeout;}
});
