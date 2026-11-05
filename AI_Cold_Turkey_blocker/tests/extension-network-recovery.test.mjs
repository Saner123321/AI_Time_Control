import {test} from 'node:test';import assert from 'node:assert/strict';
import {NetworkRecovery} from '../browser-extension/beta/network-recovery.js';
const url='https://ordinary.test/lesson',now=Date.parse('2026-11-05T02:00:05Z');
const state={confirmed:true,restricted:false,generation:2,restrictedSinceUtc:new Date(now-3000).toISOString(),releasedUtc:new Date(now-1000).toISOString()};
const plan={fingerprint:'ok',groups:[]};
const error=(tabId=1)=>({tabId,frameId:0,url,error:'net::ERR_NETWORK_ACCESS_DENIED',timeStamp:now-2000});
test('confirmed own-gate release retries the failed allowed page once',async()=>{
 const r=new NetworkRecovery(()=>now);let count=0;const tabs={get:async()=>({url}),reload:async()=>count++};r.error(error());await r.recover(state,plan,'ok',tabs);assert.equal(count,1);r.error(error());await r.recover(state,plan,'ok',tabs);assert.equal(count,1);
});
test('unconfirmed gates, plan mismatch and unrelated firewall errors never reload',async()=>{
 for(const change of [{confirmed:false},{restricted:true},{restrictedSinceUtc:null},{releasedUtc:new Date(now-60000).toISOString()}]){
 const r=new NetworkRecovery(()=>now);r.error(error());let n=0;await r.recover({...state,...change},plan,'ok',{get:async()=>({url}),reload:async()=>n++});assert.equal(n,0);}
 const r=new NetworkRecovery(()=>now);r.error(error());let n=0;await r.recover(state,plan,'old',{get:async()=>({url}),reload:async()=>n++});assert.equal(n,0);
 r.error({...error(),timeStamp:now-30000});await r.recover(state,plan,'ok',{get:async()=>({url}),reload:async()=>n++});assert.equal(n,0);
});
test('blocked targets, subframes and a user-navigated tab are never retried',async()=>{
 const r=new NetworkRecovery(()=>now);let n=0;const tabs={get:async()=>({url:'https://new.test/'}),reload:async()=>n++};r.error(error());await r.recover(state,plan,'ok',tabs);assert.equal(n,0);
 r.error({...error(),frameId:2});assert.equal(r.errors.size,0);
 r.error(error());await r.recover(state,{...plan,groups:[{mode:'BlockSelected',domains:['ordinary.test'],exceptions:[]}]},'ok',{get:async()=>({url}),reload:async()=>n++});assert.equal(n,0);
});
test('tab-close and changed navigation discard recovery state',()=>{const r=new NetworkRecovery(()=>now);r.error(error());r.navigated(1,'https://new.test/');assert.equal(r.errors.size,0);r.error(error());r.forget(1);assert.equal(r.errors.size,0);});
