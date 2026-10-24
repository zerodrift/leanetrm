export async function run({command,evaluate,wait,click,set,submit,check,idle,url}){
  await command('Runtime.enable');await command('Page.enable');
  const installed=await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.__referenceReads=[];const original=window.fetch;window.fetch=async(...args)=>{if(String(args[0]).startsWith('/api/reference/catalog'))window.__referenceReads.push(String(args[0]));return original(...args);};`});
  try{
    await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
    await check('ordinary navigation does not fetch an unused reference inspector',"window.__referenceReads.length===0&&document.querySelector('#reference').children.length===0");
    await click('#nav-reference');
    await check('reference navigation reads only the explicit initial currency page',"window.__referenceReads.length===1&&new URL(window.__referenceReads[0],location.origin).searchParams.get('table')==='currencies'&&document.querySelector('#reference tbody tr')&&document.querySelector('#error').hidden");
    await evaluate(`window.__referenceFetch=window.fetch;window.__referenceFailure='';window.fetch=async(...args)=>{const response=await window.__referenceFetch(...args),u=new URL(String(args[0]),location.origin);if(u.pathname!=='/api/reference/catalog')return response;const data=await response.clone().json();if(window.__referenceFailure==='denied')return new Response(JSON.stringify({code:'access_denied'}),{status:403});if(window.__referenceFailure==='context')data.context={contextId:'inconsistent'};if(window.__referenceFailure==='pin')data.referenceRevision.revisionId='inconsistent';if(u.searchParams.get('table')==='locations'&&response.ok){const row=window.__referenceLocationTemplate??=data.rows[0],offset=Number(u.searchParams.get('offset')),search=u.searchParams.get('search');const labels=window.__referenceLocationReferences??=data.references;data.rows=Array.from({length:window.__referenceCeiling?200:search||offset?1:200},(_,i)=>({...row,code:'page_location_'+(offset+i),name:search?'Tail %_ match':'Paged location '+(offset+i)}));data.references={...labels,locations:data.rows};data.hasMore=window.__referenceCeiling||!search&&!offset;data.maximumOffset=window.__referenceCeiling?200:data.maximumOffset??100000;}if(window.__referenceFailure==='bound')delete data.maximumOffset;return new Response(JSON.stringify(data),{status:response.status,headers:{'Content-Type':'application/json'}});};`);
    await set('#reference-table','locations');await wait(idle);
    await check('large categories render a bounded first page and resolved relationship labels',"document.querySelectorAll('#reference tbody tr').length===200&&!document.querySelector('#reference-next').disabled&&document.querySelector('#reference-previous').disabled&&document.querySelector('#reference tbody tr td:nth-child(3)').textContent==='Delivery Point'&&document.querySelector('#reference .table-search').getAttribute('aria-label')==='Filter this page'");
    await click('#reference-next');
    await check('next page requests an explicit offset without accumulating prior rows',"new URL(window.__referenceReads.at(-1),location.origin).searchParams.get('offset')==='200'&&document.querySelectorAll('#reference tbody tr').length===1&&document.querySelector('#reference').textContent.includes('Paged location 200')&&document.querySelector('#reference-next').disabled&&!document.querySelector('#reference-previous').disabled");
    await evaluate('window.__referenceCeiling=true');await click('#nav-reference');await click('#reference-next');
    await check('the server paging ceiling disables Next and asks for a narrower search',"document.querySelector('#reference-next').disabled&&document.querySelector('#reference-page-status').textContent.includes('Refine your search')&&new URL(window.__referenceReads.at(-1),location.origin).searchParams.get('offset')==='200'");
    await evaluate('window.__referenceCeiling=false');await click('#reference-previous');
    await set('#reference-search','Tail %_');await submit('#reference-search-form');
    await check('whole-category search sends the literal query and resets pagination',"new URL(window.__referenceReads.at(-1),location.origin).searchParams.get('search')==='Tail %_'&&new URL(window.__referenceReads.at(-1),location.origin).searchParams.get('offset')==='0'&&document.querySelectorAll('#reference tbody tr').length===1&&document.querySelector('#reference').textContent.includes('Tail %_ match')");
    await evaluate("window.__referenceFailure='context'");await click('#nav-reference');
    await check('an inconsistent reference context clears prior rows visibly',"!document.querySelector('#error').hidden&&document.querySelector('#reference').children.length===0&&document.querySelector('#unit-converter').hidden");
    await evaluate("window.__referenceFailure='pin'");await click('#nav-reference');
    await check('a mismatched exact reference revision cannot display stale rows',"!document.querySelector('#error').hidden&&document.querySelector('#reference').children.length===0");
    await evaluate("window.__referenceFailure='bound'");await click('#nav-reference');
    await check('missing reference paging bounds fail without retaining rows',"!document.querySelector('#error').hidden&&document.querySelector('#reference').children.length===0&&document.querySelector('#reference-next').disabled");
    await evaluate("window.__referenceFailure='denied'");await click('#nav-reference');
    await check('reference navigation rechecks current authority without stale fallback',"!document.querySelector('#error').hidden&&document.querySelector('#reference').children.length===0");
    await evaluate("window.__referenceFailure='';window.fetch=window.__referenceFetch");await click('#nav-reference');
    await check('a later authorized reference read restores only its selected page',"document.querySelector('#error').hidden&&document.querySelector('#reference tbody tr')");
  }finally{
    await command('Page.removeScriptToEvaluateOnNewDocument',{identifier:installed.identifier});
  }
}
