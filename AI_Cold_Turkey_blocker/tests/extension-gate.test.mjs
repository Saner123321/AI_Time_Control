import {test} from 'node:test';
import assert from 'node:assert/strict';
test('permitted fallback navigation leaves immediately without painting a permission card',async()=>{
  const keys=['document','location','chrome','addEventListener','setInterval','setTimeout','clearTimeout'];const saved=Object.fromEntries(keys.map(k=>[k,globalThis[k]]));
  const nodes=new Map();const node=()=>({hidden:true,textContent:'',href:'',replaceChildren(){},append(){}});
  const root={dataset:{state:'checking'}},target='https://example.com/lesson';let replaced='',cancelled=0,timer;
  globalThis.document={documentElement:root,getElementById:id=>{if(!nodes.has(id))nodes.set(id,node());return nodes.get(id);},createElement:node};
  globalThis.location={hash:'#'+target,replace:url=>replaced=url};
  globalThis.chrome={runtime:{sendMessage:async()=>({known:true,blocked:false,url:target})}};
  globalThis.setTimeout=f=>{timer=f;return 9;};globalThis.clearTimeout=id=>{if(id===9)cancelled++;};globalThis.setInterval=()=>1;globalThis.addEventListener=()=>{};
  try{
    await import('../browser-extension/beta/gate.js?paint-test');await new Promise(r=>setImmediate(r));
    assert.equal(replaced,target);assert.equal(root.dataset.state,'checking');assert.ok(cancelled);assert.equal(nodes.get('state')?.textContent||'','');assert.equal(typeof timer,'function');
  }finally{for(const k of keys){if(saved[k]===undefined)delete globalThis[k];else globalThis[k]=saved[k];}}
});
