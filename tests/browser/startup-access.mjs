export async function run({command,evaluate,wait,click,persona,check,idle,url}){
  await command('Runtime.enable');await command('Page.enable');
  const {identifier}=await command('Page.addScriptToEvaluateOnNewDocument',{source:`
    window.__startupAccessReads=[];window.__startupAccessModes=[];
    const send=window.fetch;
    window.fetch=async(input,init)=>{
      const request=new URL(typeof input==='string'?input:input.url,location.href),path=request.pathname;
      window.__startupAccessReads.push(path);
      const mode=new URLSearchParams(location.search).get('access-case');
      if(path==='/api/workspace/navigation'&&mode==='revoked')return new Response(JSON.stringify({code:'access_denied'}),{status:403,headers:{'Content-Type':'application/json'}});
      const response=await send(input,init);
      if(path!=='/api/bootstrap'||!response.ok)return response;
      window.__startupAccessModes.push(request.searchParams.get('includeAccess'));
      const data=await response.json();
      if(mode==='missing')delete data.access;
      if(mode==='null')data.access=null;
      if(mode==='mismatch')data.access={profile:'another_identity',permissions:['ReadMarketData']};
      if(mode==='malformed')data.access={profile:data.profile,permissions:'ReadMarketData'};
      return new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});
    };
  `});
  const navigate=async(mode='')=>{await command('Page.navigate',{url:url+'/'+(mode?'?access-case='+mode:'')});await wait(idle+"&&(!document.querySelector('#error').hidden||performance.getEntriesByName('leanetrm:workspace-ready','mark').length===1)");};
  try{
    await command('Page.navigate',{url:url+'/'});await wait(idle+"&&!!document.querySelector('#profile-label')");
    await persona('trader');await navigate();
    await check('selected-profile startup hydrates from its explicit access projection without a second access request',"window.__startupAccessModes[0]==='true'&&!window.__startupAccessReads.includes('/api/access')&&window.__startupAccessReads.includes('/api/workspace/navigation')&&document.querySelectorAll('.curve-button').length>0&&document.querySelector('#error').hidden");
    await click('#profile-open');
    await check('Profile refresh checks current permissions and requests ordinary compact metadata',"window.__startupAccessReads.filter(path=>path==='/api/access').length===1&&JSON.stringify(window.__startupAccessModes)===JSON.stringify(['true',null])&&document.querySelector('#profile-dialog').open");
    await click('#profile-close');
    for(const mode of ['missing','null','mismatch','malformed']){
      await navigate(mode);
      await check('invalid startup access fails before market reads or readiness: '+mode,"!document.querySelector('#error').hidden&&!window.__startupAccessReads.includes('/api/workspace/navigation')&&!window.__startupAccessReads.includes('/api/access')&&performance.getEntriesByName('leanetrm:workspace-ready','mark').length===0&&document.querySelectorAll('.curve-button').length===0");
    }
    await navigate('revoked');
    await check('startup access cannot substitute for a subsequently denied business read',"window.__startupAccessReads.includes('/api/workspace/navigation')&&!document.querySelector('#error').hidden&&performance.getEntriesByName('leanetrm:workspace-ready','mark').length===0&&document.querySelectorAll('.curve-button').length===0");
    await persona('admin');await navigate();
    await check('admin-only startup opens administration without access or market-data round trips',"!window.__startupAccessReads.includes('/api/access')&&!window.__startupAccessReads.includes('/api/workspace/navigation')&&window.__startupAccessReads.includes('/api/admin')&&!document.querySelector('#admin-page').hidden&&document.querySelector('#error').hidden");
    await persona('viewer');await navigate();
    await check('later identity changes discard prior startup permissions',"!document.querySelector('#nav-market').hidden&&document.querySelector('#nav-admin').hidden&&document.querySelectorAll('.curve-button').length>0&&document.querySelector('#error').hidden");
  }finally{await command('Page.removeScriptToEvaluateOnNewDocument',{identifier});}
}
