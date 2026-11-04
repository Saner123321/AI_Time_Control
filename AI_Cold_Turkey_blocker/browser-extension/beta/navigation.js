// Conservative DNR shortcuts: redirects cover every possible denial; direct allows
// are emitted only when every active group proves the candidate is allowed.
import {isDomain,needsGate,buildRules} from './rules.js';
export const FAST_ALLOW_ID=10000;
const frames=['main_frame','sub_frame'];
const escape=s=>s.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
const glob=s=>s.split('*').map(escape).join('.*');
const simpleWord=p=>/^\*([a-z0-9_.-]+)\*$/i.exec(p)?.[1]?.toLowerCase();
function literal(p){try{if(p.includes('*')||p==='file://*.*')return null;return new URL(p.includes('://')?p:'https://'+p);}catch{return null;}}
function domainRegex(p){if(!/^[a-z0-9.-]+$/i.test(p)||p.endsWith('.'))return null;return '^https?://(?:[^/?#:@]+\\.)*'+escape(p)+'\\.?(?::[0-9]+)?(?:[/?]|$)';}
// Preserve explicit ports: URL() would discard :443 or :80 depending on its scheme.
function pageParts(p){const m=/^([a-z0-9.-]+)(:[0-9]+)?(\/[^?#]*)?$/i.exec(p);return m&&!m[1].endsWith('.')&&!p.endsWith('/')?{host:m[1].toLowerCase(),port:m[2]||'',path:(m[3]||'').toLowerCase()}:null;}
function pageRegex(p){const a=pageParts(p);return a?'^https?://'+escape(a.host)+'\\.?'+escape(a.port)+escape(a.path)+'/*(?:\\?[^#]*)?$':null;}
export function candidateRegex(p){
  if(p==='file://*.*')return null;
  if(isDomain(p))return domainRegex(p);
  if(!p.includes('*'))return pageRegex(p);
  return '^'+glob(p.includes('://')?p:'*://'+p)+'$';
}
// This may match a subset of a complex exception. Misses still go through the gate.
export function directRegex(p){
  if(p==='file://*.*')return null;
  if(isDomain(p))return domainRegex(p);
  if(!p.includes('*'))return pageRegex(p);
  const word=simpleWord(p);if(word)return '^https?://(?:[^/?#@]*'+escape(word)+'[^/?#@]*(?:[/?][^#]*)?|[^/?#@]+[/?][^#]*'+escape(word)+'[^#]*)$';
  const full=p.includes('://')?p:'*://'+p,match=/^(https?|\*):\/\/([^/?#]+)([/?][^#]*)?$/i.exec(full);
  if(!match || !/^[a-z0-9.*:[\]-]+$/i.test(match[2]))return null;
  const scheme=match[1]==='*'?'https?':match[1].toLowerCase();
  return '^'+scheme+'://'+match[2].split('*').map(escape).join('[^/?#@]*')+glob(match[3]||'')+'$';
}
function covers(allow,proposed){
  if(allow.toLowerCase()===proposed.toLowerCase())return true;
  const word=simpleWord(allow),inside=simpleWord(proposed);if(word && inside)return inside.includes(word);
  if(isDomain(allow) && !proposed.includes('*')){const u=literal(proposed);return !!u && (u.hostname===allow||u.hostname.endsWith('.'+allow));}
  return false;
}
function disjoint(block,proposed){
  if(block.includes('*')||proposed.includes('*'))return false;
  const a=literal(block),b=literal(proposed);if(!a||!b)return false;
  if(isDomain(block))return b.hostname!==block && !b.hostname.endsWith('.'+block) && (!isDomain(proposed)||!block.endsWith('.'+proposed));
  if(isDomain(proposed))return a.hostname!==proposed && !a.hostname.endsWith('.'+proposed);
  const x=pageParts(block),y=pageParts(proposed);return !!x&&!!y&&(x.host!==y.host||x.port!==y.port||x.path!==y.path);
}
function provenAllowed(group,p){
  const allows=[...group.exceptions,...(group.mode==='AllowSelected'?group.domains:[])];
  return allows.some(a=>covers(a,p)) || group.mode==='BlockSelected'&&group.domains.every(b=>disjoint(b,p));
}
export function navigationRules(plan,gateUrl,{universal=false}={}){
  if(!gateUrl.startsWith('chrome-extension://'))throw Error('确认页必须来自本组件');
  const groups=plan.groups.filter(g=>g.mode==='AllowSelected'||g.mode==='BlockSelected'&&g.domains.length);
  if(!groups.length)return [];
  let patterns=[...new Set(groups.flatMap(g=>g.domains))];
  let regexes=patterns.filter(p=>p!=='file://*.*').map(candidateRegex);
  if(universal||regexes.some(r=>!r)||groups.some(g=>g.mode==='AllowSelected')||regexes.length>256||regexes.some(r=>r.length>512))regexes=['^https?://.*$'];
  const redirects=regexes.map((r,i)=>({id:i+1,priority:1,action:{type:'redirect',redirect:{regexSubstitution:gateUrl+'#\\1'}},condition:{regexFilter:'('+r+')',resourceTypes:frames}}));
  const candidates=[...new Set(groups.flatMap(g=>[...g.exceptions,...(g.mode==='AllowSelected'?g.domains:[])]))];
  const direct=[];
  for(const p of candidates){if(direct.length>=256)break;if(!groups.every(g=>provenAllowed(g,p)))continue;const regex=directRegex(p);if(!regex||regex.length>512)continue;direct.push({id:FAST_ALLOW_ID+direct.length,priority:1000,action:{type:'allow'},condition:{regexFilter:regex,resourceTypes:frames}});}
  // Preserve the existing domain subresource policy; page/path rules remain navigation-scoped.
  const resources=needsGate(plan)?[]:buildRules(plan).map((r,i)=>({...r,id:1000+i,condition:{...r.condition,resourceTypes:r.condition.resourceTypes.filter(t=>!frames.includes(t))}}));
  return [...redirects,...resources,...direct];
}
