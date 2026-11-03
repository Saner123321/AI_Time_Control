// Requested URLs stay in tab fragments and session rules, never in local storage.
let checking=false,blocked=false;
async function check(){if(checking)return;checking=true;try{
  const requested=location.hash,u=new URL(requested.slice(1));if(!['http:','https:'].includes(u.protocol)||u.username||u.password)throw Error('仅支持 HTTP(S) 网址');
  document.getElementById('target').textContent=u.hostname+u.pathname;
  const s=await chrome.runtime.sendMessage({type:'gate',url:u.href});
  if(location.hash!==requested)return;
  if(!s?.known){document.getElementById('state').textContent='组件尚未确认规则，暂不访问目标网站。';return;}
  if(s.blocked){blocked=true;document.getElementById('heading').textContent='当前页面受到专注计划保护';document.getElementById('state').textContent='例外只影响所属规则；其他规则仍可能要求封锁。解除后可点击重试。';return;}
  const retry=document.getElementById('retry');retry.href=s.url;retry.hidden=false;
  document.getElementById('state').textContent='当前规则允许访问。';
  if(!blocked)location.replace(s.url);
}catch{document.getElementById('state').textContent='无法确认网址。请在专注计划中查看浏览器组件状态。';}finally{checking=false;}}
addEventListener('hashchange',()=>{blocked=false;document.getElementById('retry').hidden=true;void check();});
void check();setInterval(check,1500);
