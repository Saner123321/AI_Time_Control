import {renderReasons} from './reasons.js';
try {
  let address=decodeURIComponent(location.hash.slice(1));if(!address.includes('://'))address='https://'+address+'/';
  const u=new URL(address);if(['http:','https:'].includes(u.protocol) && !u.username && !u.password){
    const target=document.getElementById('target'),retry=document.getElementById('retry');retry.href=u.href;retry.hidden=false;
    async function refresh(){try{const s=await chrome.runtime.sendMessage({type:'block-status',domain:u.href});renderReasons(document.getElementById('reasons'),s?.known&&s.blocked?s.reasons:[]);target.textContent=!s?.known?u.hostname+'：等待浏览器组件确认规则。':s.blocked?u.hostname+u.pathname+' 当前受到专注计划保护。':'当前页面限制已解除，可以点击下方重新打开。';}catch{renderReasons(document.getElementById('reasons'),[]);target.textContent='组件状态尚未确认，请在专注计划中查看后台状态。';}}
    void refresh();setInterval(refresh,1000);
  }
}catch{}
