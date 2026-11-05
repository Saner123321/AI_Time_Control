import {denies, normalized} from './rules.js';
import {blockingReasons} from './reasons.js';
import {NetworkRecovery} from './network-recovery.js';
import {navigationRules,FAST_ALLOW_ID} from './navigation.js';
const recovery=new NetworkRecovery();let pendingSince=0,urgent=false;
let port, pending=false, applied='', plan={groups:[],required:false}, profileId='', attemptedDomain=null, lastReadback=0;
let operations=Promise.resolve(),nextGrant=1000000;const grants=new Map();
async function checkedRules(next){
  let expected=navigationRules(next,chrome.runtime.getURL('gate.html'));const supported=await Promise.all(expected.map(r=>chrome.declarativeNetRequest.isRegexSupported({regex:r.condition.regexFilter,isCaseSensitive:false,requireCapturing:r.action.type==='redirect'})));
  if(expected.some((r,i)=>r.action.type==='redirect'&&!supported[i].isSupported)){expected=navigationRules(next,chrome.runtime.getURL('gate.html'),{universal:true});const checks=await Promise.all(expected.map(r=>chrome.declarativeNetRequest.isRegexSupported({regex:r.condition.regexFilter,isCaseSensitive:false,requireCapturing:r.action.type==='redirect'})));return expected.filter((r,i)=>{if(!checks[i].isSupported&&r.id<FAST_ALLOW_ID)throw Error('浏览器不支持当前封锁规则');return checks[i].isSupported;});}
  return expected.filter((r,i)=>{if(!supported[i].isSupported&&r.id<FAST_ALLOW_ID)throw Error('浏览器不支持当前封锁规则');return supported[i].isSupported;});
}
function serial(f){const run=operations.then(f);operations=run.catch(()=>{});return run;}
async function clearGrants(){const old=await chrome.declarativeNetRequest.getSessionRules();await chrome.declarativeNetRequest.updateSessionRules({removeRuleIds:old.map(r=>r.id)});grants.clear();}
async function init(){
  const saved=await chrome.storage.local.get('profileId');profileId=saved.profileId || crypto.randomUUID().replaceAll('-','');
  await chrome.storage.local.set({profileId});connect();
}
function connect(){
  if(port)return;
  try {
    const connection=chrome.runtime.connectNative('com.aict.beta');port=connection;
    connection.onMessage.addListener(response=>serial(async()=>{
      if(port!==connection)return;
      try {
        if(response.ok && response.code==='UNINSTALLED'){
          const before=await chrome.declarativeNetRequest.getDynamicRules();await chrome.declarativeNetRequest.updateDynamicRules({removeRuleIds:before.map(r=>r.id)});
          await clearGrants();plan={groups:[],required:false};applied='';await chrome.management.uninstallSelf({showConfirmDialog:false});return;
        }
        if(!response.ok || !response.browserPlan)throw new Error(response.message || '后台没有返回计划');
        const next=response.browserPlan;
        if(next.fingerprint!==applied || Date.now()-lastReadback>5000){
          const expected=await checkedRules(next),before=await chrome.declarativeNetRequest.getDynamicRules();if(port!==connection)return;
          if(next.fingerprint!==applied)await clearGrants();
          if(normalized(before)!==normalized(expected))await chrome.declarativeNetRequest.updateDynamicRules({removeRuleIds:before.map(r=>r.id),addRules:expected});
          const actual=await chrome.declarativeNetRequest.getDynamicRules();
          if(normalized(actual)!==normalized(expected))throw new Error('声明式封锁规则读回不一致');
          plan=next;await blockExistingTabs();applied=next.fingerprint;lastReadback=Date.now();
        }
        await chrome.action.setBadgeText({text:next.required?'保护':''});
        await recovery.recover(response.browserNetwork,plan,applied,chrome.tabs);
      }catch(e){applied='';await chrome.action.setBadgeText({text:'!'});console.error(e);}
      finally{if(port===connection){pending=false;if(urgent)queueMicrotask(()=>void pulse());}}
    }));
    connection.onDisconnect.addListener(()=>{void chrome.runtime.lastError;if(port!==connection)return;port=null;pending=false;setTimeout(connect,1000);});
    pulse();
  }catch(e){port=null;pending=false;setTimeout(connect,1000);}
}
async function pulse(){
  if(!port){connect();return;}
  if(pending&&performance.now()-pendingSince>6000){const old=port;port=null;pending=false;try{old.disconnect();}catch{}setTimeout(connect,0);return;}
  if(pending || !profileId)return;urgent=false;const connection=port;pending=true;pendingSince=performance.now();
  try {
    const window=await chrome.windows.getLastFocused();
    const tabs=await chrome.tabs.query({active:true,lastFocusedWindow:true});
    const idle=await chrome.idle.queryState(60);
    let domain=null,url=null;try {const u=new URL(tabs[0]?.url || '');if(['http:','https:'].includes(u.protocol) && !u.username && !u.password && u.href.length<=8192){domain=u.hostname;u.hash='';url=u.href;}}catch{}
    if(port!==connection)return;connection.postMessage({browser:'Edge',profileId,appliedPlan:applied,focused:!!window.focused,idle:idle!=='active',domain,url,patternVersion:1,blockedDomain:attemptedDomain});attemptedDomain=null;
  }catch(e){if(port===connection)pending=false;}
}
async function protect(tab){
  try {const u=new URL(tab.url || '');if(['http:','https:'].includes(u.protocol) && denies(plan,u.href)){attemptedDomain=u.hostname;await chrome.tabs.update(tab.id,{url:chrome.runtime.getURL('blocked.html')+'#'+encodeURIComponent(u.href)});}}catch{}
}
async function blockExistingTabs(){for(const tab of await chrome.tabs.query({}))await protect(tab);}
chrome.tabs.onUpdated.addListener((id,change,tab)=>{if(change.url){recovery.navigated(id,change.url);void protect(tab);}});
chrome.tabs.onRemoved?.addListener(id=>recovery.forget(id));
function requestPulse(){urgent=true;void pulse();}
chrome.windows.onFocusChanged?.addListener(requestPulse);
chrome.tabs.onActivated?.addListener(requestPulse);
chrome.webNavigation?.onErrorOccurred.addListener(e=>{recovery.error(e);requestPulse();});
chrome.webNavigation?.onCompleted.addListener(e=>{if(e.frameId===0)recovery.forget(e.tabId);});
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
