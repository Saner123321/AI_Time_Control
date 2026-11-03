import {test} from 'node:test';
import assert from 'node:assert/strict';
import {buildRules,denies,normalized,matches} from '../browser-extension/beta/rules.js';
const block={mode:'BlockSelected',domains:['example.com'],exceptions:['mail.example.com']};
test('group exceptions never become global allow actions',()=>{const p={groups:[block,{mode:'BlockSelected',domains:['mail.example.com'],exceptions:[]}]};assert.equal(denies(p,'mail.example.com'),true);const r=buildRules(p);assert.ok(r.every(x=>x.action.type==='block'));assert.deepEqual(r[0].condition.excludedRequestDomains,['mail.example.com']);assert.equal(r[1].condition.excludedRequestDomains,undefined);});
test('allowlists block outside their own allowed domain and include subdomains',()=>{const p={groups:[{mode:'AllowSelected',domains:['school.edu'],exceptions:['work.example.com']}]};assert.equal(denies(p,'learn.school.edu'),false);assert.equal(denies(p,'work.example.com'),false);assert.equal(denies(p,'example.com'),true);const r=buildRules(p)[0];assert.equal(r.condition.requestDomains,undefined);assert.deepEqual(r.condition.excludedRequestDomains,['school.edu','work.example.com']);});
test('two allowlist groups enforce their intersection',()=>{const p={groups:[{mode:'AllowSelected',domains:['example.com'],exceptions:[]},{mode:'AllowSelected',domains:['work.example.com'],exceptions:[]}]};assert.equal(denies(p,'work.example.com'),false);assert.equal(denies(p,'mail.example.com'),true);});
test('URL filtering includes web sockets and page/subresource navigation',()=>{const r=buildRules({groups:[block]})[0];assert.equal(new RegExp(r.condition.regexFilter).test('wss://example.com/live'),true);assert.ok(r.condition.resourceTypes.includes('main_frame'));assert.ok(r.condition.resourceTypes.includes('xmlhttprequest'));});
test('readback normalization tolerates property ordering and catches changed scope',()=>{const a=buildRules({groups:[block]});const b=structuredClone(a);b[0].condition.resourceTypes.reverse();assert.equal(normalized(a),normalized(b));b[0].condition.excludedRequestDomains=[];assert.notEqual(normalized(a),normalized(b));});
test('a released plan removes all dynamic rules; no substring matching of unrelated domains',()=>{assert.deepEqual(buildRules({groups:[]}),[]);assert.equal(denies({groups:[block]},'notexample.com'),false);assert.equal(denies({groups:[block]},'EXAMPLE.COM.'),true);});
test('empty blacklist produces no DNR rule while an empty allowlist still blocks all sites',()=>{assert.deepEqual(buildRules({groups:[{mode:'BlockSelected',domains:[],exceptions:[]}]}),[]);assert.equal(buildRules({groups:[{mode:'AllowSelected',domains:[],exceptions:[]}]}).length,1);});
test('removing the last site yields a valid empty replacement rather than invalid requestDomains',()=>{let installed=buildRules({groups:[block]});assert.equal(installed.length,1);installed=buildRules({groups:[{...block,domains:[]}]});assert.deepEqual(installed,[]);});

test('page exceptions preserve boundaries, ignore query, and never release the root',()=>{
 const p={groups:[{mode:'BlockSelected',domains:['*bilibili*','*youtube*'],exceptions:['*favlist*','bilibili.com/video/BV1ABC','file://*.*']}]};
 assert.equal(denies(p,'https://www.bilibili.com/'),true);
 assert.equal(denies(p,'https://bilibili.com/video/bv1abc/?spm_id_from=123'),false);
 assert.equal(denies(p,'https://bilibili.com/video/BV1OTHER'),true);
 assert.equal(denies(p,'https://space.bilibili.com/favlist?fid=1'),false);
 assert.equal(matches('file://*.*','https://bilibili.com/'),false);
 const rules=buildRules(p,'chrome-extension://test/gate.html');
 assert.equal(rules[0].action.type,'redirect');assert.deepEqual(rules[0].condition.resourceTypes,['main_frame','sub_frame']);
 assert.throws(()=>buildRules(p));
});
test('page exceptions cannot defeat another group or a second allowlist',()=>{
 const g={mode:'BlockSelected',domains:['*bilibili*'],exceptions:['bilibili.com/video/BV1ABC']};
 assert.equal(denies({groups:[g,{mode:'BlockSelected',domains:['bilibili.com/video/BV1ABC'],exceptions:[]}]},'https://bilibili.com/video/BV1ABC'),true);
 assert.equal(denies({groups:[{mode:'AllowSelected',domains:['bilibili.com/video/BV1ABC'],exceptions:[]}]},'https://bilibili.com/video/BV1XYZ'),true);
});
