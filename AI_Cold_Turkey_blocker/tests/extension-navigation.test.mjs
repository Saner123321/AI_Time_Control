import {test} from 'node:test';
import assert from 'node:assert/strict';
import {denies,matches} from '../browser-extension/beta/rules.js';
import {navigationRules,directRegex} from '../browser-extension/beta/navigation.js';
const gate='chrome-extension://test/gate.html';
const group=(domains,exceptions=[],mode='BlockSelected')=>({domains,exceptions,mode});
const hits=(r,url)=>new RegExp(r.condition.regexFilter,'i').test(new URL(url).href.split('#')[0]);
const direct=(rules,url)=>rules.some(r=>r.action.type==='allow'&&hits(r,url));
const redirected=(rules,url)=>rules.some(r=>r.action.type==='redirect'&&hits(r,url));

test('unrelated pages and proven exceptions skip the intermediary',()=>{
  const rules=navigationRules({groups:[group(['*example*'],['example.com/lesson','*favlist*'])]},gate);
  assert.equal(redirected(rules,'https://ordinary.test/'),false);
  assert.equal(direct(rules,'https://example.com/lesson?tracking=1'),true);
  assert.equal(direct(rules,'https://example.com/favlist'),true);
  assert.equal(direct(rules,'https://example.com/lesson-more'),false);
  assert.equal(redirected(rules,'https://example.com/blocked'),true);
  assert.ok(rules.filter(r=>r.action.type==='allow').every(r=>r.condition.resourceTypes.join(',')==='main_frame,sub_frame'));
});
test('another group denial always wins over an exception shortcut',()=>{
  for(const second of [group(['example.com/lesson']),group(['ordinary.test'],[],'AllowSelected'),group(['*lesson*'])]){
    const rules=navigationRules({groups:[group(['example.com'],['example.com/lesson']),second]},gate);
    assert.equal(direct(rules,'https://example.com/lesson'),false);
    assert.equal(redirected(rules,'https://example.com/lesson'),true);
  }
  const rules=navigationRules({groups:[group(['example.com'],['example.com/lesson']),group(['ordinary.test'])]},gate);
  assert.equal(direct(rules,'https://example.com/lesson'),true);
});
test('two allowlists use only their proven intersection',()=>{
  const rules=navigationRules({groups:[group(['example.com'],[],'AllowSelected'),group(['example.com/lesson'],[],'AllowSelected')]},gate);
  assert.equal(direct(rules,'https://example.com/lesson'),true);
  assert.equal(direct(rules,'https://example.com/other'),false);
  assert.equal(redirected(rules,'https://ordinary.test/'),true);
});
test('explicit default ports are preserved and unusual patterns fall back safely',()=>{
  assert.equal(new RegExp(directRegex('example.com:443/lesson'),'i').test('http://example.com:443/lesson'),true);
  assert.equal(new RegExp(directRegex('example.com:443/lesson'),'i').test('https://example.com/lesson'),false);
  const rules=navigationRules({groups:[group(['[::1]/lesson'])]},gate);
  assert.equal(redirected(rules,'http://[::1]/lesson'),true);
  assert.equal(directRegex('https://example.com/lesson'),null);
  assert.equal(directRegex('example.com/lesson?unused'),null);
});
test('large or complex candidate plans retain a universal navigation guard',()=>{
  for(const domains of [Array.from({length:257},(_,i)=>`site${i}.test`),['*'+ 'a'.repeat(600)+'*']]){
    const rules=navigationRules({groups:[group(domains)]},gate);
    assert.equal(rules.filter(r=>r.action.type==='redirect').length,1);
    assert.equal(redirected(rules,'https://any.test/'),true);
  }
  assert.deepEqual(navigationRules({groups:[group([])]},gate),[]);
});
test('sampled URL and multi-group combinations never shortcut a denial',()=>{
  const patterns=['example.com','sub.example.com','other.test','example.com/lesson','example.com:443/lesson','example.com:80/lesson','*example*','*lesson*','*favlist*','example.com/*','*.example.com/*','https://example.com/*','*://example.com/*','example.com/*lesson*','[::1]/lesson'];
  const urls=[];
  for(const scheme of ['http','https'])for(const host of ['example.com','example.com.','sub.example.com','other.test','myexample.test','[::1]'])for(const port of ['',':80',':443',':1234'])for(const path of ['/','/lesson','/LESSON/','/lesson//','/lesson-more','/x/favlist','/other?lesson=1','/other?next=example.com','/lesson?tracking=1'])urls.push(`${scheme}://${host}${port}${path}`);
  urls.push('https://example.com@other.test/lesson','https://other.test@example.com/lesson','https://example.com/lesson#ignored');
  const plans=patterns.flatMap(p=>[ {groups:[group(['*example*'],[p])]}, {groups:[group([p])]}, {groups:[group([p],[],'AllowSelected')]} ]);
  for(const a of patterns)for(const b of patterns)plans.push({groups:[group(['*example*'],[a]),group([b])]});
  let checks=0;
  for(const plan of plans){const rules=navigationRules(plan,gate);for(const url of urls){
    if(direct(rules,url))assert.equal(denies(plan,url),false,JSON.stringify({plan,url}));
    if(denies(plan,url))assert.equal(redirected(rules,url),true,JSON.stringify({missingGuard:plan,url}));checks++;
  }}
  for(const p of patterns){const regex=directRegex(p);if(regex)for(const url of urls)if(new RegExp(regex,'i').test(new URL(url).href.split('#')[0]))assert.equal(matches(p,url),true,JSON.stringify({exception:p,url}));}
  assert.ok(checks>100000);
});
