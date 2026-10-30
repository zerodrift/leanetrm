export async function run({command,evaluate,wait,click,set,submit,check,idle,url}){
  await command('Runtime.enable');await command('Page.enable');
  const installed=await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.__referenceReads=[];window.__referenceSearches=[];window.__productReads=[];window.__referenceRefusals=[];const original=window.fetch;window.fetch=async(...args)=>{const path=String(args[0]);if(path.startsWith('/api/reference/catalog'))window.__referenceReads.push(path);const response=await original(...args);if(path.startsWith('/api/reference/search'))window.__referenceSearches.push({path,body:await response.clone().json()});if(path.startsWith('/api/reference/futures-product')&&response.ok)window.__productReads.push({path,body:await response.clone().json()});if(path.startsWith('/api/reference/')&&!response.ok)window.__referenceRefusals.push((await response.clone().json()).code);return response;};`});
  const read=key=>`new URL(window.__referenceReads.at(-1),location.origin).searchParams.get(${JSON.stringify(key)})`;
  const selected=list=>`document.querySelector('#${list} [role=tab][aria-selected=true]')?.textContent`;
  const tab=async(list,name)=>{await evaluate(`[...document.querySelectorAll('#${list} [role=tab]')].find(t=>t.textContent===${JSON.stringify(name)}).click()`);await wait(idle);};
  const key=async name=>{await evaluate(`document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:${JSON.stringify(name)},bubbles:true}))`);await wait(idle);};
  // A fresh load at the address: navigating within the same document would only change its hash.
  const open=async address=>{await command('Page.navigate',{url:'about:blank'});await command('Page.navigate',{url:url+'/'+address});await wait(`document.querySelector('#reference-page')&&!document.querySelector('#reference-page').hidden&&(document.querySelector('#reference tbody tr')||document.querySelector('#reference-item h2')||!document.querySelector('#error').hidden)&&${idle}`);};
  // An item's page in place of its category's list, with a breadcrumb back to the list.
  const itemShown="!document.querySelector('#reference-item').hidden&&document.querySelector('#reference-list').hidden&&document.querySelector('#error').hidden";
  const itemCleared="document.querySelector('#reference-item').hidden&&document.querySelector('#reference-item').children.length===0";
  const facts="Object.fromEntries([...document.querySelectorAll('#reference-item .item-facts div')].map(d=>[d.querySelector('dt').textContent,d.querySelector('dd').textContent]))";
  const product=`(()=>{const read=window.__productReads.at(-1).body,named=read.referenceDisplay?.references?.futures_products?.find(r=>r.code===read.product.code)?.name??read.product.name;
    return ${itemShown}&&document.querySelector('#reference-item h2').textContent===named&&${facts}.Code===read.product.code&&${facts}.Status===({placeholder:'Placeholder',candidate:'Candidate',synthetic:'Synthetic'})[read.product.definition_state]
      &&location.hash==='#reference/futures_products/'+encodeURIComponent(read.product.code)&&JSON.stringify([...document.querySelectorAll('#reference-item .item-section h3')].map(h=>h.textContent))===JSON.stringify(['Venue','Underlying','Economics','Calendars','Monthly contracts'])
      &&document.querySelectorAll('#reference-item .product-contracts tbody tr').length===read.series.length&&read.series.length>0
      &&[...document.querySelectorAll('#reference-item .product-contracts tbody tr')].every((tr,i)=>tr.cells[0].textContent===read.series[i].series_label);})()`;
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
    const commodity=await evaluate("window.__referenceSearches.at(-1).body.hits.find(h=>h.table==='commodities').code");
    await evaluate(`document.querySelector('#reference-search-hits a[href=${JSON.stringify('#reference/commodities/'+encodeURIComponent(commodity))}]').click()`);
    await wait(`${read('code')}===${JSON.stringify(commodity)}&&${idle}`);
    await check('a search hit opens its item page under its category tab',`${selected('reference-groups')}==='Commodities & units'&&${selected('reference-sections')}==='Commodities'&&${read('table')}==='commodities'&&${itemShown}&&${facts}.Code===${JSON.stringify(commodity)}&&location.hash==='#reference/commodities/'+encodeURIComponent(${JSON.stringify(commodity)})&&document.querySelector('#reference-item .breadcrumb a').getAttribute('href')==='#reference/commodities'`);
    const series=await evaluate("window.__referenceSearches.at(-1).body.hits.find(h=>h.table==='futures_series').productCode");
    await evaluate(`document.querySelector('#reference-search-hits a[href=${JSON.stringify('#reference/futures_products/'+encodeURIComponent(series))}]').click()`);
    await wait(`window.__productReads.length===1&&${idle}`);
    await check('a futures contract hit opens its product page under the Futures tab',`${selected('reference-groups')}==='Markets & contracts'&&${selected('reference-sections')}==='Futures'&&new URL(window.__productReads[0].path,location.origin).searchParams.get('code')===${JSON.stringify(series)}&&${product}`);
    await evaluate("location.hash='#reference/futures_products/absent_code'");await wait(`window.__referenceRefusals.length===1&&${idle}`);
    await check('an absent product in the address shows reference_item and none of the product shown before',`window.__referenceRefusals[0]==='reference_item'&&!document.querySelector('#error').hidden&&${itemCleared}&&document.querySelector('#reference').children.length===0`);
    await tab('reference-sections','Futures');
    await evaluate("document.querySelector('#reference tbody tr').click()");await wait(`window.__productReads.length===2&&${idle}`);
    await check('clicking a Futures row opens its product page with its terms in sections and one row per monthly contract',product);
    await evaluate(`[...document.querySelectorAll('#reference-item .item-section a')].find(a=>a.getAttribute('href').startsWith('#reference/exchanges/')).click()`);
    await wait(`${read('table')}==='exchanges'&&${idle}`);
    await check('a referenced item on the product page links to its own item page',`${itemShown}&&${selected('reference-sections')}==='Exchanges'&&${facts}.Code===window.__productReads.at(-1).body.product.exchange_code`);
    await tab('reference-groups','Commodities & units');await tab('reference-sections','Unit conversions');
    await check('a category without item codes lists rows that open no item page',"document.querySelector('#reference tbody tr')&&!document.querySelector('#reference table.activatable-rows')");
    await tab('reference-groups','Locations');
    await evaluate("document.querySelector('#reference tbody tr').click()");await wait(`${read('code')}!==null&&${idle}`);
    await check('clicking a location row opens its item page with its fields and a link to its type',`${itemShown}&&${read('table')}==='locations'&&location.hash==='#reference/locations/'+encodeURIComponent(${read('code')})&&${facts}.Code===${read('code')}&&document.querySelector('#reference-item .item-grid a[href^="#reference/location_types/"]')`);
    await open('#reference/locations');
    await check('reloading at a category address opens that tab with one read',`window.__referenceReads.length===1&&${read('table')}==='locations'&&${selected('reference-sections')}==='Locations'&&document.querySelector('#error').hidden&&document.querySelector('#reference tbody tr')`);
    await open('#reference/locations/absent_code');
    await check('an absent item in the address shows reference_item and no rows',`window.__referenceRefusals.at(-1)==='reference_item'&&!document.querySelector('#error').hidden&&${itemCleared}&&document.querySelector('#reference').children.length===0`);
    await open('#reference/not_a_table');
    await check('an address naming an unknown category shows the error and no rows',"window.__referenceReads.length===0&&!document.querySelector('#error').hidden&&document.querySelector('#reference').children.length===0");
    await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await click('#nav-reference');
    await evaluate(`window.__referenceFetch=window.fetch;window.__referenceFailure='';window.fetch=async(...args)=>{const u=new URL(String(args[0]),location.origin),synthetic=u.searchParams.get('code')?.startsWith('page_location_');
      if(u.pathname==='/api/reference/editor'&&synthetic){const read=await(await window.__referenceFetch('/api/reference/editor?'+new URLSearchParams({table:'locations',code:window.__referenceLocationTemplate.code}))).json();return new Response(JSON.stringify({...read,item:{...read.item,code:u.searchParams.get('code')}}),{status:200,headers:{'Content-Type':'application/json'}});}
      const response=await window.__referenceFetch(...args);if(u.pathname!=='/api/reference/catalog')return response;const data=await response.clone().json();if(window.__referenceFailure==='denied')return new Response(JSON.stringify({code:'access_denied'}),{status:403});if(window.__referenceFailure==='context')data.context={contextId:'inconsistent'};if(window.__referenceFailure==='pin')data.referenceRevision.revisionId='inconsistent';if(u.searchParams.get('table')==='locations'&&u.searchParams.has('code')){const page=window.__referenceLocationPage,row={...window.__referenceLocationTemplate,code:u.searchParams.get('code'),name:'Paged item'};return new Response(JSON.stringify({...page,rows:[row],references:{...page.references,locations:[row]},offset:0,search:'',hasMore:false}),{status:200,headers:{'Content-Type':'application/json'}});}if(u.searchParams.get('table')==='locations'&&response.ok){window.__referenceLocationPage??=JSON.parse(JSON.stringify(data));const row=window.__referenceLocationTemplate??=data.rows[0],offset=Number(u.searchParams.get('offset')),search=u.searchParams.get('search');const labels=window.__referenceLocationReferences??=data.references;data.rows=Array.from({length:window.__referenceCeiling?200:search||offset?1:200},(_,i)=>({...row,code:'page_location_'+(offset+i),name:search?'Tail %_ match':'Paged location '+(offset+i)}));data.references={...labels,locations:data.rows};data.hasMore=window.__referenceCeiling||!search&&!offset;data.maximumOffset=window.__referenceCeiling?200:data.maximumOffset??100000;}if(window.__referenceFailure==='bound')delete data.maximumOffset;return new Response(JSON.stringify(data),{status:response.status,headers:{'Content-Type':'application/json'}});};`);
    await tab('reference-groups','Locations');
    await check('large categories render a bounded first page and resolved relationship labels',"document.querySelectorAll('#reference tbody tr').length===200&&!document.querySelector('#reference-next').disabled&&document.querySelector('#reference-previous').disabled&&document.querySelector('#reference tbody tr td:nth-child(3)').textContent==='Delivery Point'&&document.querySelector('#reference .table-search').getAttribute('aria-label')==='Filter this page'");
    await click('#reference-next');
    await check('next page requests an explicit offset without accumulating prior rows',`${read('offset')}==='200'&&document.querySelectorAll('#reference tbody tr').length===1&&document.querySelector('#reference').textContent.includes('Paged location 200')&&document.querySelector('#reference-next').disabled&&!document.querySelector('#reference-previous').disabled`);
    await evaluate("document.querySelector('#reference tbody tr').click()");await wait(`${read('code')}==='page_location_200'&&${idle}`);
    await check('a row on a later page opens its item page with a breadcrumb to its list',`${itemShown}&&document.querySelector('#reference-item h2').textContent==='Paged item'&&document.querySelector('#reference-item .breadcrumb a').getAttribute('href')==='#reference/locations'`);
    await click('#reference-item .breadcrumb a');await wait(`${read('offset')}==='200'&&${idle}`);
    await check('the breadcrumb returns to the list at the page the item was opened from',`document.querySelector('#reference-item').hidden&&!document.querySelector('#reference-list').hidden&&location.hash==='#reference/locations'&&document.querySelector('#reference').textContent.includes('Paged location 200')`);
    await evaluate('window.__referenceCeiling=true');await click('#nav-reference');await click('#reference-next');
    await check('the server paging ceiling disables Next and asks for a narrower search',`document.querySelector('#reference-next').disabled&&document.querySelector('#reference-page-status').textContent.includes('Refine your search')&&${read('offset')}==='200'`);
    await evaluate('window.__referenceCeiling=false');await click('#reference-previous');
    await set('#reference-search','Tail %_');await submit('#reference-search-form');
    await check('whole-category search sends the literal query, resets pagination and is addressed',`${read('table')}==='locations'&&${read('search')}==='Tail %_'&&${read('offset')}==='0'&&document.querySelectorAll('#reference tbody tr').length===1&&document.querySelector('#reference').textContent.includes('Tail %_ match')&&location.hash==='#reference/locations?q='+encodeURIComponent('Tail %_')`);
    await evaluate("document.querySelector('#reference tbody tr').click()");await wait(`${read('code')}==='page_location_0'&&${idle}`);
    await click('#reference-item .breadcrumb a');await wait(`${read('code')}===null&&${idle}`);
    await check('the breadcrumb returns to the list with its search',`${read('search')}==='Tail %_'&&location.hash==='#reference/locations?q='+encodeURIComponent('Tail %_')&&document.querySelector('#reference-search').value==='Tail %_'&&document.querySelector('#reference').textContent.includes('Tail %_ match')`);
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
