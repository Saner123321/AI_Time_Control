import {denies} from './rules.js';
// Only retry main-frame failures within a confirmed interval of OUR network gate.
// No browsing history is stored, and unrelated firewall failures never trigger a loop.
export class NetworkRecovery {
  constructor(now=()=>Date.now()){this.now=now;this.errors=new Map();this.used=new Set();}
  error(e){
    if(e.frameId!==0||e.error!=='net::ERR_NETWORK_ACCESS_DENIED')return;
    let u;try{u=new URL(e.url);if(!['http:','https:'].includes(u.protocol)||u.username||u.password||u.href.length>8192)return;}catch{return;}
    this.errors.set(e.tabId,{url:u.href,at:Number(e.timeStamp)||this.now()});if(this.errors.size>64)this.errors.delete(this.errors.keys().next().value);
  }
  navigated(id,url){if(this.errors.get(id)?.url!==url)this.errors.delete(id);}
  forget(id){this.errors.delete(id);}
  async recover(state,plan,applied,tabs){
    if(!state?.confirmed||state.restricted||!applied||applied!==plan.fingerprint)return;
    const start=Date.parse(state.restrictedSinceUtc),end=Date.parse(state.releasedUtc);
    if(!Number.isFinite(start)||!Number.isFinite(end)||end<start||this.now()-end>20000||end-this.now()>1500)return;
    for(const [id,error] of this.errors){
      const key=id+'|'+state.generation+'|'+state.releasedUtc+'|'+error.url;
      if(this.used.has(key)||error.at<start-1500||error.at>end+1500||denies(plan,error.url))continue;
      try{const tab=await tabs.get(id);if(tab.url!==error.url){this.errors.delete(id);continue;}
        this.used.add(key);if(this.used.size>128)this.used.delete(this.used.values().next().value);this.errors.delete(id);await tabs.reload(id);
      }catch{this.errors.delete(id);}
    }
  }
}
