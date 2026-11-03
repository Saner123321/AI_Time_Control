import {buildRules, denies, normalized} from './rules.js';
import {blockingReasons} from './reasons.js';
let port, pending=false, applied='', plan={groups:[],required:false}, profileId='', attemptedDomain=null, lastReadback=0;
let operations=Promise.resolve(),nextGrant=10000;const grants=new Map();
function serial(f){const run=operations.then(f);operations=run.catch(()=>{});return run;}
async function clearGrants(){const old=await chrome.declarativeNetRequest.getSessionRules();await chrome.declarativeNetRequest.updateSessionRules({removeRuleIds:old.map(r=>r.id)});grants.clear();}
async function init(){
  const saved=await chrome.storage.local.get('profileId');profileId=saved.profileId || crypto.randomUUID().replaceAll('-','');
  await chrome.storage.local.set({profileId});connect();
}
function connect(){
  if(port)return;
  try {
    port=chrome.runtime.connectNative('com.aict.beta');
    port.onMessage.addListener(response=>serial(async()=>{
      try {
        if(response.ok && response.code==='UNINSTALLED'){
          const before=await chrome.declarativeNetRequest.getDynamicRules();await chrome.declarativeNetRequest.updateDynamicRules({removeRuleIds:before.map(r=>r.id)});
          await clearGrants();plan={groups:[],required:false};applied='';await chrome.management.uninstallSelf({showConfirmDialog:false});return;
        }
        if(!response.ok || !response.browserPlan)throw new Error(response.message || '后台没有返回计划');
        const next=response.browserPlan;
        if(next.fingerprint!==applied || Date.now()-lastReadback>5000){
          const expected=buildRules(next,chrome.runtime.getURL('gate.html')),before=await chrome.declarativeNetRequest.getDynamicRules();
          if(next.fingerprint!==applied)await clearGrants();
          await chrome.declarativeNetRequest.updateDynamicRules({removeRuleIds:before.map(r=>r.id),addRules:expected});
          const actual=await chrome.declarativeNetRequest.getDynamicRules();
          if(normalized(actual)!==normalized(expected))throw new Error('声明式封锁规则读回不一致');
          plan=next;await blockExistingTabs();applied=next.fingerprint;lastReadback=Date.now();
        }
        await chrome.action.setBadgeText({text:next.required?'保护':''});
      }catch(e){applied='';await chrome.action.setBadgeText({text:'!'});console.error(e);}
      finally{pending=false;}
    }));
    port.onDisconnect.addListener(()=>{void chrome.runtime.lastError;port=null;pending=false;setTimeout(connect,1000);});
    pulse();
  }catch(e){port=null;pending=false;setTimeout(connect,1000);}
}
async function pulse(){
  if(!port){connect();return;}if(pending || !profileId)return;
  try {
    const window=await chrome.windows.getLastFocused();
    const tabs=await chrome.tabs.query({active:true,lastFocusedWindow:true});
    const idle=await chrome.idle.queryState(60);
    let domain=null,url=null;try {const u=new URL(tabs[0]?.url || '');if(['http:','https:'].includes(u.protocol) && !u.username && !u.password && u.href.length<=8192){domain=u.hostname;u.hash='';url=u.href;}}catch{}
    pending=true;port.postMessage({browser:'Edge',profileId,appliedPlan:applied,focused:!!window.focused,idle:idle!=='active',domain,url,patternVersion:1,blockedDomain:attemptedDomain});attemptedDomain=null;
  }catch(e){pending=false;}
}
async function protect(tab){
  try {const u=new URL(tab.url || '');if(['http:','https:'].includes(u.protocol) && denies(plan,u.href)){attemptedDomain=u.hostname;await chrome.tabs.update(tab.id,{url:chrome.runtime.getURL('blocked.html')+'#'+encodeURIComponent(u.href)});}}catch{}
}
async function blockExistingTabs(){for(const tab of await chrome.tabs.query({}))await protect(tab);}
chrome.tabs.onUpdated.addListener((id,change,tab)=>{if(change.url)void protect(tab);});
chrome.action.onClicked.addListener(()=>chrome.tabs.create({url:chrome.runtime.getURL('blocked.html')}));
chrome.runtime.onMessage.addListener((message,sender,reply)=>{
  if(message?.type==='block-status' && typeof message.domain==='string' && sender.url?.split('#')[0]===chrome.runtime.getURL('blocked.html')){const known=!!port && !!applied;reply({known,blocked:denies(plan,message.domain),reasons:known?blockingReasons(plan,message.domain):[]});return false;}
  if(message?.type!=='gate' || typeof message.url!=='string' || sender.url?.split('#')[0]!==chrome.runtime.getURL('gate.html') || !Number.isInteger(sender.tab?.id))return false;
  serial(async()=>{
    const u=new URL(message.url);if(!['http:','https:'].includes(u.protocol)||u.username||u.password||u.href.length>8192)throw Error('网址格式无效');u.hash='';
    if(!port||!applied)return {known:false};
    if(denies(plan,u.href)){attemptedDomain=u.hostname;return {known:true,blocked:true,reasons:blockingReasons(plan,u.href)};}
    const key=sender.tab.id+'|'+u.href,existing=grants.get(key);if(existing)return {known:true,blocked:false,url:u.href};
    const remove=[];if(grants.size>=256){const [k,id]=grants.entries().next().value;grants.delete(k);remove.push(id);}
    const id=nextGrant++;const rule={id,priority:1000,action:{type:'allow'},condition:{tabIds:[sender.tab.id],regexFilter:'^'+u.href.replace(/[.*+?^${}()|[\]\\]/g,'\\$&')+'$',isUrlFilterCaseSensitive:true,resourceTypes:['main_frame','sub_frame']}};
    await chrome.declarativeNetRequest.updateSessionRules({removeRuleIds:remove,addRules:[rule]});
    const readback=await chrome.declarativeNetRequest.getSessionRules();if(!readback.some(r=>normalized([r])===normalized([rule])))throw Error('页面放行规则读回失败');
    grants.set(key,id);return {known:true,blocked:false,url:u.href};
  }).then(reply,e=>{applied='';console.error(e);reply({known:false});});return true;
});
chrome.alarms.create('bridge-retry',{periodInMinutes:0.5});chrome.alarms.onAlarm.addListener(()=>{connect();void pulse();});
setInterval(pulse,1000);void init();
