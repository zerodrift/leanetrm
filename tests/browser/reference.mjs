export async function run({command,evaluate,wait,click,set,submit,check,idle,url}){
  await command('Runtime.enable');await command('Page.enable');
  const installed=await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.__referenceReads=[];window.__referenceSearches=[];const original=window.fetch;window.fetch=async(...args)=>{const path=String(args[0]);if(path.startsWith('/api/reference/catalog'))window.__referenceReads.push(path);const response=await original(...args);if(path.startsWith('/api/reference/search'))window.__referenceSearches.push({path,body:await response.clone().json()});return response;};`});
  const read=key=>`new URL(window.__referenceReads.at(-1),location.origin).searchParams.get(${JSON.stringify(key)})`;
  const selected=list=>`document.querySelector('#${list} [role=tab][aria-selected=true]')?.textContent`;
  const tab=async(list,name)=>{await evaluate(`[...document.querySelectorAll('#${list} [role=tab]')].find(t=>t.textContent===${JSON.stringify(name)}).click()`);await wait(idle);};
  const key=async name=>{await evaluate(`document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:${JSON.stringify(name)},bubbles:true}))`);await wait(idle);};
  // A fresh load at the address: navigating within the same document would only change its hash.
  const open=async address=>{await command('Page.navigate',{url:'about:blank'});await command('Page.navigate',{url:url+'/'+address});await wait(`document.querySelector('#reference-page')&&!document.querySelector('#reference-page').hidden&&(document.querySelector('#reference tbody tr')||!document.querySelector('#error').hidden)&&${idle}`);};
  try{
    await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
    await check('ordinary navigation does not fetch an unused reference inspector',"window.__referenceReads.length===0&&document.querySelector('#reference').children.length===0");
    await click('#nav-reference');
    await check('reference navigation reads only the explicit initial currency page',`window.__referenceReads.length===1&&${read('table')}==='currencies'&&document.querySelector('#reference tbody tr')&&document.querySelector('#error').hidden`);
    await check('the initial category is shown as its group and tab and addressed',`${selected('reference-groups')}==='Commodities & units'&&${selected('reference-sections')}==='Currencies'&&location.hash==='#reference/currencies'&&document.querySelector('#reference-panel').getAttribute('aria-labelledby')===document.querySelector('#reference-sections [aria-selected=true]').id`);
    await tab('reference-groups','Locations');await tab('reference-sections','Locations');
    await check('choosing a group and its tab reads that category and addresses it',`${read('table')}==='locations'&&location.hash==='#reference/locations'&&${selected('reference-groups')}==='Locations'&&${selected('reference-sections')}==='Locations'&&document.querySelector('#data-title').textContent==='Locations'`);
    await evaluate("document.querySelector('#reference-sections [aria-selected=true]').focus()");await key('ArrowRight');
    await check('arrow keys move between tabs and read the reached category',`${read('table')}==='location_types'&&location.hash==='#reference/location_types'&&document.activeElement.textContent==='Location types'&&document.activeElement.tabIndex===0`);
    await key('ArrowLeft');
    await check('arrow keys move back to the previous tab',`${read('table')}==='locations'&&${selected('reference-sections')}==='Locations'`);
    await set('#reference-search-all-text','Tail %_');await submit('#reference-search-all');
    await check('the global search sends the literal text across categories without a category read',`window.__referenceSearches.length===1&&new URL(window.__referenceSearches[0].path,location.origin).searchParams.get('q')==='Tail %_'&&${read('table')}==='locations'&&document.querySelector('#reference-search-hits').textContent.includes('No reference data matches')`);
    await set('#reference-search-all-text','test_gas');await submit('#reference-search-all');
    await check('global search hits are listed by category name with links to their items',`(()=>{const hits=window.__referenceSearches.at(-1).body.hits,links=[...document.querySelectorAll('#reference-search-hits a')],names=[...document.querySelectorAll('#reference-search-hits h3')].map(h=>h.textContent);
      return hits.length>1&&links.length===hits.length&&new Set(hits.map(h=>h.table==='futures_series'?'futures_products':h.table)).size===names.length&&names.includes('Futures')&&names.includes('Commodities')
        &&hits.some(h=>h.table==='futures_series')&&hits.every(h=>links.some(a=>a.getAttribute('href')===(h.table==='futures_series'?'#reference/futures_products/'+encodeURIComponent(h.productCode):'#reference/'+h.table+'/'+encodeURIComponent(h.code))));})()`);
    await evaluate("[...document.querySelectorAll('#reference-search-hits a')].find(a=>a.getAttribute('href').startsWith('#reference/commodities/')).click()");
    await wait(`${read('table')}==='commodities'&&${idle}`);
    await check('a search hit selects its category tab',`${selected('reference-groups')}==='Commodities & units'&&${selected('reference-sections')}==='Commodities'&&document.querySelector('#reference tbody tr')&&document.querySelector('#error').hidden`);
    const series=await evaluate("window.__referenceSearches.at(-1).body.hits.find(h=>h.table==='futures_series').productCode");
    await evaluate(`document.querySelector('#reference-search-hits a[href=${JSON.stringify('#reference/futures_products/'+encodeURIComponent(series))}]').click()`);
    await wait(`${read('table')}==='futures_products'&&${idle}`);
    await check('a futures contract hit selects the Futures tab',`${selected('reference-groups')}==='Markets & contracts'&&${selected('reference-sections')}==='Futures'&&document.querySelector('#error').hidden`);
    await open('#reference/locations');
    await check('reloading at a category address opens that tab with one read',`window.__referenceReads.length===1&&${read('table')}==='locations'&&${selected('reference-sections')}==='Locations'&&document.querySelector('#error').hidden&&document.querySelector('#reference tbody tr')`);
    await open('#reference/not_a_table');
    await check('an address naming an unknown category shows the error and no rows',"window.__referenceReads.length===0&&!document.querySelector('#error').hidden&&document.querySelector('#reference').children.length===0");
    await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await click('#nav-reference');
    await evaluate(`window.__referenceFetch=window.fetch;window.__referenceFailure='';window.fetch=async(...args)=>{const response=await window.__referenceFetch(...args),u=new URL(String(args[0]),location.origin);if(u.pathname!=='/api/reference/catalog')return response;const data=await response.clone().json();if(window.__referenceFailure==='denied')return new Response(JSON.stringify({code:'access_denied'}),{status:403});if(window.__referenceFailure==='context')data.context={contextId:'inconsistent'};if(window.__referenceFailure==='pin')data.referenceRevision.revisionId='inconsistent';if(u.searchParams.get('table')==='locations'&&response.ok){const row=window.__referenceLocationTemplate??=data.rows[0],offset=Number(u.searchParams.get('offset')),search=u.searchParams.get('search');const labels=window.__referenceLocationReferences??=data.references;data.rows=Array.from({length:window.__referenceCeiling?200:search||offset?1:200},(_,i)=>({...row,code:'page_location_'+(offset+i),name:search?'Tail %_ match':'Paged location '+(offset+i)}));data.references={...labels,locations:data.rows};data.hasMore=window.__referenceCeiling||!search&&!offset;data.maximumOffset=window.__referenceCeiling?200:data.maximumOffset??100000;}if(window.__referenceFailure==='bound')delete data.maximumOffset;return new Response(JSON.stringify(data),{status:response.status,headers:{'Content-Type':'application/json'}});};`);
    await tab('reference-groups','Locations');
    await check('large categories render a bounded first page and resolved relationship labels',"document.querySelectorAll('#reference tbody tr').length===200&&!document.querySelector('#reference-next').disabled&&document.querySelector('#reference-previous').disabled&&document.querySelector('#reference tbody tr td:nth-child(3)').textContent==='Delivery Point'&&document.querySelector('#reference .table-search').getAttribute('aria-label')==='Filter this page'");
    await click('#reference-next');
    await check('next page requests an explicit offset without accumulating prior rows',`${read('offset')}==='200'&&document.querySelectorAll('#reference tbody tr').length===1&&document.querySelector('#reference').textContent.includes('Paged location 200')&&document.querySelector('#reference-next').disabled&&!document.querySelector('#reference-previous').disabled`);
    await evaluate('window.__referenceCeiling=true');await click('#nav-reference');await click('#reference-next');
    await check('the server paging ceiling disables Next and asks for a narrower search',`document.querySelector('#reference-next').disabled&&document.querySelector('#reference-page-status').textContent.includes('Refine your search')&&${read('offset')}==='200'`);
    await evaluate('window.__referenceCeiling=false');await click('#reference-previous');
    await set('#reference-search','Tail %_');await submit('#reference-search-form');
    await check('whole-category search sends the literal query, resets pagination and is addressed',`${read('table')}==='locations'&&${read('search')}==='Tail %_'&&${read('offset')}==='0'&&document.querySelectorAll('#reference tbody tr').length===1&&document.querySelector('#reference').textContent.includes('Tail %_ match')&&location.hash==='#reference/locations?q='+encodeURIComponent('Tail %_')`);
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
