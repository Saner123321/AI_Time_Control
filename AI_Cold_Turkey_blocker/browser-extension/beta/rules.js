export function isDomain(p) { return !/[*/:?#]/.test(p); }
export function glob(p,s) {
  p=p.toLowerCase();s=s.toLowerCase();let i=0,j=0,star=-1,retry=0;
  while(j<s.length){if(i<p.length && p[i]===s[j] && p[i]!=='*'){i++;j++;}else if(p[i]==='*'){star=i++;retry=j;}else if(star>=0){i=star+1;j=++retry;}else return false;}
  while(p[i]==='*')i++;return i===p.length;
}
export function matches(pattern,address) {
  if(pattern==='file://*.*')return false;
  let u;try{u=new URL(address.includes('://')?address:'https://'+address+'/');}catch{return false;}
  if(!['http:','https:'].includes(u.protocol)||u.username||u.password)return false;
  u.hash='';const host=u.hostname.toLowerCase().replace(/\.$/,'');
  if(isDomain(pattern))return host===pattern || host.endsWith('.'+pattern);
  if(pattern.includes('*'))return glob(pattern.includes('://')?pattern:'*://'+pattern,u.href);
  return (host+(u.port?':'+u.port:'')+u.pathname.replace(/\/+$/,'')).toLowerCase()===pattern.toLowerCase();
}
export function needsGate(plan) { return plan.groups.some(g=>[...g.domains,...g.exceptions].some(p=>!isDomain(p))); }
export function buildRules(plan,gateUrl='') {
  const scoped=plan.groups.filter(g=>g.mode==='AllowSelected'||g.mode==='BlockSelected'&&g.domains.length>0);
  if(scoped.length===0)return [];
  if(gateUrl && !gateUrl.startsWith('chrome-extension://'))throw Error('确认页必须来自本组件');
  if(gateUrl && !needsGate(plan)) {
    const navigation={id:1,priority:1,action:{type:'redirect',redirect:{regexSubstitution:gateUrl+'#\\1'}},condition:{regexFilter:'^(https?://.*)$',resourceTypes:['main_frame','sub_frame']}};
    const resources=buildRules(plan).map((r,i)=>({...r,id:i+2,condition:{...r.condition,resourceTypes:r.condition.resourceTypes.filter(t=>t!=='main_frame'&&t!=='sub_frame')}}));
    return [navigation,...resources];
  }
  if(needsGate(plan)) {
    if(!gateUrl.startsWith('chrome-extension://'))throw Error('网址规则需要浏览器确认页');
    // Navigate through a local decision page BEFORE the request reaches the server.
    // Exact, tab-scoped grants are issued only after evaluating ALL rule groups.
    return [{id:1,priority:1,action:{type:'redirect',redirect:{regexSubstitution:gateUrl+'#\\1'}},condition:{regexFilter:'^(https?://.*)$',resourceTypes:['main_frame','sub_frame']}}];
  }
  return plan.groups.filter(group => group.mode === 'AllowSelected' || group.mode === 'BlockSelected' && group.domains.length > 0).map((group, index) => {
    const condition = { regexFilter: '^(https?|wss?)://', resourceTypes: ['main_frame','sub_frame','xmlhttprequest','script','image','media','websocket','other','font','stylesheet','ping','object','csp_report'] };
    const exceptions = [...new Set([...group.exceptions, ...(group.mode === 'AllowSelected' ? group.domains : [])])].sort();
    if (group.mode === 'BlockSelected') condition.requestDomains = [...new Set(group.domains)].sort();
    if (exceptions.length) condition.excludedRequestDomains = exceptions;
    return { id: index + 1, priority: 1, action: { type: 'block' }, condition };
  });
}
export function denies(plan, host) {
  const includes = (domains) => domains.some(d => matches(d,host));
  return plan.groups.some(g => !includes(g.exceptions) && (g.mode === 'AllowSelected' ? !includes(g.domains) : includes(g.domains)));
}
export function normalized(rules) {
  const action=a=>Object.fromEntries(Object.entries(a).sort(([a],[b])=>a.localeCompare(b)).map(([k,v])=>[k,v && typeof v==='object'?action(v):v]));
  return JSON.stringify(rules.map(r => ({ id:r.id,priority:r.priority,action:action(r.action),regex:r.condition.regexFilter,sensitive:r.condition.isUrlFilterCaseSensitive??false,tabs:r.condition.tabIds,
    domains:[...(r.condition.requestDomains || [])].sort(),exceptions:[...(r.condition.excludedRequestDomains || [])].sort(),types:[...(r.condition.resourceTypes || [])].sort() })).sort((a,b)=>a.id-b.id));
}
