import {test} from 'node:test';
import assert from 'node:assert/strict';
import {blockingReasons,renderReasons} from '../browser-extension/beta/reasons.js';
import {buildRules} from '../browser-extension/beta/rules.js';
test('only rules actually denying this URL contribute their names and detailed causes',()=>{
 const base={mode:'BlockSelected',domains:['*example*'],exceptions:[],name:'工作',reasons:['WeeklySchedule'],details:['处于每周硬封锁时段：09:00—12:00。']};
 const plan={groups:[{...base,exceptions:['example.com/video']},{...base,name:'上午额度',reasons:['QuotaExhausted'],details:['上午20分钟额度已用完。']},{...base,domains:['elsewhere.org']}]};
 assert.deepEqual(blockingReasons(plan,'https://example.com/video'),[{ruleName:'上午额度',details:['上午20分钟额度已用完。']}]);
 assert.equal(blockingReasons(plan,'https://elsewhere.test/').length,0);
});
test('fallback reason codes remain understandable with an older backend plan',()=>{
 const values=blockingReasons({groups:[{mode:'AllowSelected',domains:[],exceptions:[],reasons:['Always','Cycle','TimedBlock','UsageGap','ClockUntrusted']}]},'https://example.com/');
 assert.equal(values[0].ruleName,'未命名规则');assert.equal(values[0].details.length,5);assert.ok(values[0].details.every(s=>s.endsWith('。')));
});
test('reason rendering uses literal text and clears stale reasons after release',()=>{
 const before=globalThis.document;class Element{children=[];hidden=false;textContent='';append(...values){this.children.push(...values);}replaceChildren(){this.children=[];}set innerHTML(v){throw Error('unsafe HTML renderer');}}
 globalThis.document={createElement:()=>new Element()};try{const root=new Element();renderReasons(root,[{ruleName:'<img onerror=alert(1)>',details:['<script>example</script>']}]);assert.equal(root.hidden,false);assert.equal(root.children[0].children[0].textContent,'命中规则：<img onerror=alert(1)>');assert.equal(root.children[0].children[1].children[0].textContent,'<script>example</script>');renderReasons(root,[]);assert.equal(root.hidden,true);assert.deepEqual(root.children,[]);}finally{globalThis.document=before;}
});
test('domain navigation goes to the reason page while subresource protection remains intact',()=>{
 const r=buildRules({groups:[{mode:'BlockSelected',domains:['example.com'],exceptions:[]}]},'chrome-extension://test/gate.html');assert.equal(r.length,2);assert.equal(r[0].action.type,'redirect');assert.ok(r[0].condition.resourceTypes.includes('main_frame'));assert.equal(r[1].action.type,'block');assert.ok(!r[1].condition.resourceTypes.includes('main_frame'));assert.ok(r[1].condition.resourceTypes.includes('media'));
});
