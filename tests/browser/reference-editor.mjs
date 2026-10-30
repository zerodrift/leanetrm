import {sideSession} from './side-session.mjs';
// Editing a reference item's display labels in place on its own page: one Edit, a confirmed Save that commits each changed item,
// retained uncertain requests, exact coordinate strings, the futures product page's sequential commits, History and As-of contexts.
export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,fitsWidth,url}){
 await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.referenceRequests=[];{
  const original=window.fetch;window.fetch=async(...args)=>{
   const path=String(args[0]);
   if(path.includes('/reference/search?')&&window.holdReferenceSearch){window.holdReferenceSearch=false;await new Promise(release=>{window.releaseReferenceSearch=release;});}
   // An editor read answering for a later display version than the page read before it.
   if(path.includes('/reference/editor?')&&window.laterEditorReads>0){window.laterEditorReads--;const read=await(await original(...args)).json();read.displayVersion++;
    return new Response(JSON.stringify(read),{status:200,headers:{'Content-Type':'application/json'}});}
   if(path.includes('/reference/editor?')&&window.failReferenceRefresh){window.failReferenceRefresh=false;return new Response(JSON.stringify({code:'synthetic_refresh',message:'Synthetic reference refresh failure'}),{status:503});}
   const response=await original(...args);
   if(path.includes('/reference/')){
    window.referenceRequests.push({path,body:args[1]?.body?JSON.parse(args[1].body):null,status:response.status,payload:await response.clone().json()});
    if(path.endsWith('/reference/commit')&&window.dropReferenceCommit){window.dropReferenceCommit=false;throw Error('Synthetic lost reference commit response');}
    if(path.endsWith('/reference/commit')&&window.malformedReferenceCommit){window.malformedReferenceCommit=false;return new Response(JSON.stringify({revisionId:'inconsistent',displayRevisionId:'inconsistent',version:0}),{status:200});}
    if(path.endsWith('/reference/commit')&&window.failRefreshAfterCommit){window.failRefreshAfterCommit=false;window.failReferenceRefresh=true;}
   }
   return response;
  };
 }`});
 const commits="window.referenceRequests.filter(r=>r.path.endsWith('/reference/commit'))";
 // The latest editor read of a category: a product page also reads its contracts' category.
 const editorRead=(table='units|locations|futures_products')=>`window.referenceRequests.filter(r=>r.path.includes('/reference/editor?')&&/[?&]table=(${table})(&|$)/.test(r.path)).at(-1).payload`;
 const facts="Object.fromEntries([...document.querySelectorAll('#reference-item .item-facts div')].map(d=>[d.querySelector('dt').textContent,d.querySelector('dd').textContent]))";
 const editing="[...document.querySelectorAll('#reference-item input')]";
 const shown=field=>`document.querySelector('#reference-item [data-edit-field=${field}]')?.textContent`;
 const visible=id=>`(document.querySelector('#${id}')&&!document.querySelector('#${id}').hidden)`;
 const quiet="document.querySelector('#error').hidden";
 // A fresh load at an item's address, once its page and editor controls are shown.
 const open=async address=>{await command('Page.navigate',{url:'about:blank'});await command('Page.navigate',{url:url+'/#reference/'+address});
  await wait(`(document.querySelector('#reference-edit-actions')||document.querySelector('#error')?.hidden===false)&&${idle}`);};
 const save=async()=>{await click('#reference-edit-save');await click('#reference-edit-confirm');};
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);

 await persona('viewer');await open('units/megawatt_hour');
 await check('a reader sees the item and its display version with no Edit',`!${visible('reference-edit')}&&${facts}['Display version']!==undefined&&!('Reference revision' in ${facts})&&${quiet}`);
 await persona('editor');await open('units/megawatt_hour');
 await check('Write without Seal offers no Edit and says both are needed',`!${visible('reference-edit')}&&document.querySelector('#reference-edit-reason').textContent.includes('both Write and Seal')`);

 // One-step Save needs both permissions, which no seeded role holds: the approver is given the editor role for these checks.
 const authority=await sideSession(url),approver=(await authority.call('admin')).members.find(m=>m.login==='approver');
 await authority.replaceRoles(approver,[...approver.roles,'market_data_editor']);
 try{
  await persona('approver');await open('units/megawatt_hour');
  const originalSymbol=await evaluate(`${editorRead()}.item.symbol`),sample=originalSymbol+' browser';
  await check('a person holding Write and Seal sees Edit on the item page',`${visible('reference-edit')}&&!${visible('reference-edit-save')}&&${editing}.length===0`);
  await click('#reference-edit');
  await check('Edit turns exactly the descriptor fields into inputs in place and marks the code permanent',
   `(()=>{const fields=${editorRead()}.fields,inputs=${editing};return inputs.length===fields.length&&fields.every(f=>document.querySelector('#reference-item [data-edit-field='+f.key+'] input')?.value===${editorRead()}.item[f.key])
    &&!document.querySelector('#reference-item [data-edit-field=factor_numerator] input')&&!document.querySelector('#reference-item .item-facts input')&&[...document.querySelectorAll('#reference-item .item-facts div')].find(d=>d.querySelector('dt').textContent==='Code').querySelector('.permanent')
    &&${visible('reference-edit-save')}&&${visible('reference-edit-cancel')};})()`);
  await check('inputs describe the server Unicode character limit without a UTF16 cap',"(()=>{const input=document.querySelector('#reference-edit-symbol');return input.maxLength===-1&&document.querySelector('#'+input.getAttribute('aria-describedby')).textContent.includes('80');})()");
  await set('#reference-edit-symbol',sample);await click('#reference-edit-cancel');
  await check('Cancel restores the shown values and sends nothing',`${editing}.length===0&&${shown('symbol')}===${JSON.stringify(originalSymbol)}&&${commits}.length===0&&!document.querySelector('#reference-item .permanent')`);

  await click('#reference-edit');await set('#reference-edit-symbol',sample);await click('#reference-edit-save');
  await check('Save lists the changed fields for confirmation before sending anything',
   `${visible('reference-edit-review')}&&document.querySelector('#reference-edit-review').textContent.includes('Display symbol')&&document.querySelector('#reference-edit-review').textContent.includes(${JSON.stringify(sample)})&&document.querySelectorAll('#reference-edit-review li').length===1&&${commits}.length===0`);
  await evaluate('window.dropReferenceCommit=true');await click('#reference-edit-confirm');
  const sent=await evaluate(`${commits}.at(-1).body`);
  await check('confirming posts one commit of only the changed field at the shown display version',
   `(()=>{const b=${commits}.at(-1).body,read=${editorRead()};return ${commits}.length===1&&b.table==='units'&&b.code==='megawatt_hour'&&JSON.stringify(b.values)===${JSON.stringify(JSON.stringify({symbol:sample}))}&&b.expectedVersion===read.displayVersion&&b.baseRevisionId===read.baseRevisionId&&typeof b.operationId==='string';})()`);
  await check('a lost commit response retains its exact request and blocks a new edit',`${visible('reference-edit-pending')}&&!document.querySelector('#reference-edit-retry').disabled&&!document.querySelector('#error').hidden`);
  // The reload opens the market page first, so a curve whose unit the committed label names is shown in the same session.
  await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await click('#example');
  await evaluate("location.hash='#reference/units/megawatt_hour'");await wait(`document.querySelector('#reference-edit-actions')&&${idle}`);
  await check('a reload restores the unconfirmed request for reconciliation',`${visible('reference-edit-pending')}&&!document.querySelector('#reference-edit-retry').disabled&&document.querySelector('#reference-edit').disabled`);
  await click('#reference-edit-retry');
  await check('Retry re-sends the identical body and the page then shows the committed label',
   `JSON.stringify(${commits}.at(-1).body)===${JSON.stringify(JSON.stringify(sent))}&&${commits}.at(-1).status===200&&${shown('symbol')}===${JSON.stringify(sample)}&&!${visible('reference-edit-pending')}&&${quiet}`);
  const committed=await evaluate(`${commits}.at(-1).payload`);
  await check('the header shows the current display version',`${facts}['Display version']===String(${committed.version})`);
  await click('#reference-history-open');
  await check('History lists the item\'s applied display versions with time, author and changed fields',
   `(()=>{const v=${editorRead()}.versions[0],first=document.querySelector('#reference-history li');return v.version===${committed.version}&&first&&first.textContent.includes(v.author)&&first.textContent.includes('Display symbol')&&first.textContent.includes(${JSON.stringify(sample)})&&first.querySelector('time')?.dateTime===v.recordedAt;})()`);
  await click('#nav-market');
  await check('a committed label refreshes the current curve unit',`document.querySelector('#unit').textContent.includes(${JSON.stringify(sample)})&&${quiet}`,"document.querySelector('#unit').textContent");

  await open('units/megawatt_hour');await click('#reference-edit');await set('#reference-edit-symbol','𝛼'.repeat(81));await save();
  await check('the backend refuses an 81-scalar symbol; nothing is retained and the edit stays open',
   `${commits}.at(-1).status===422&&!document.querySelector('#error').hidden&&!${visible('reference-edit-pending')}&&document.querySelector('#reference-edit-symbol').value==='𝛼'.repeat(81)`);
  await set('#reference-edit-symbol','Synthetic oversized symbol '+'x'.repeat(32*1024));await save();
  await check('a symbol past the request size limit is refused before any write, and no request is kept',
   `(()=>{const r=${commits}.at(-1);return r.status===413&&r.payload.code==='request_too_large'&&!document.querySelector('#error').hidden&&!${visible('reference-edit-pending')}&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.reference.request.'));})()`);
  await set('#reference-edit-symbol',originalSymbol+' malformed');await evaluate('window.malformedReferenceCommit=true');await save();
  const malformed=await evaluate(`${commits}.at(-1).body`);
  await check('a malformed commit confirmation retains the exact request',`${visible('reference-edit-pending')}&&!document.querySelector('#error').hidden`);
  await click('#reference-edit-retry');
  await check('Retry reconciles the malformed confirmation with the identical body',`JSON.stringify(${commits}.at(-1).body)===${JSON.stringify(JSON.stringify(malformed))}&&${commits}.at(-1).status===200&&${quiet}&&${shown('symbol')}===${JSON.stringify(originalSymbol+' malformed')}`);
  await click('#reference-edit');await set('#reference-edit-symbol',originalSymbol);await evaluate('window.failRefreshAfterCommit=true');await save();
  await check('a failed refresh after a confirmed commit is visible without retaining it',
   `!document.querySelector('#error').hidden&&${commits}.at(-1).status===200&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.reference.request.'))`);
  await open('units/megawatt_hour');
  await check('the restored label is committed',`${shown('symbol')}===${JSON.stringify(originalSymbol)}&&${quiet}`);

  // A move within the page or to another page asks before giving up unsaved edits; keeping them keeps the item, its address and its inputs.
  const prompt='#reference-item-prompt',keep=`document.querySelector('${prompt} button.secondary').click()`,discard=`document.querySelector('${prompt} button:not(.secondary)').click()`;
  const kept=`location.hash==='#reference/units/megawatt_hour'&&document.querySelector('#reference-edit-symbol')?.value===${JSON.stringify(originalSymbol+' unsaved')}&&!${visible(prompt.slice(1))}`;
  await click('#reference-edit');await set('#reference-edit-symbol',originalSymbol+' unsaved');
  await evaluate("document.querySelector('#reference-item .breadcrumb a').click()");await wait(visible(prompt.slice(1)));
  await check('following a link while editing asks before discarding the unsaved edits',`document.querySelector('${prompt}').textContent.includes('unsaved edits')&&location.hash==='#reference/units/megawatt_hour'`);
  await evaluate(keep);await wait(idle);
  await check('keeping the edits returns the address to the item and keeps its inputs',kept);
  await evaluate("[...document.querySelectorAll('#reference-sections [role=tab]')].find(t=>t.textContent==='Unit conversions').click()");await wait(visible(prompt.slice(1)));
  await evaluate(keep);await wait(idle);
  await check('choosing another tab while editing asks, and keeping the edits keeps the item\'s tab',`${kept}&&document.querySelector('#reference-sections [aria-selected=true]').textContent==='Units'`);
  await evaluate("document.querySelector('#nav-market').click()");await wait(visible(prompt.slice(1)));
  await evaluate(keep);await wait(idle);
  await check('opening another page while editing asks, and keeping the edits stays on the item',`${kept}&&!document.querySelector('#reference-page').hidden`);
  await evaluate("document.querySelector('#reference-item .breadcrumb a').click()");await wait(visible(prompt.slice(1)));
  await evaluate(discard);await wait(`location.hash==='#reference/units'&&document.querySelector('#reference tbody tr')&&${idle}`);
  await check('discarding the edits follows the link to the list',`!document.querySelector('#reference-item input')&&${quiet}`);

  // An address followed while a read is in progress is not opened, so the address returns to the page shown.
  await open('units/megawatt_hour');
  await evaluate('window.holdReferenceSearch=true');await set('#reference-search-all-text','gas');
  await evaluate("document.querySelector('#reference-search-all').requestSubmit()");await wait('!!window.releaseReferenceSearch');
  await evaluate("location.hash='#reference/units'");await evaluate('window.releaseReferenceSearch()');await wait(`!document.querySelector('#reference-search-hits').hidden&&${idle}`);
  await check('an address followed during a read returns to the item shown',`location.hash==='#reference/units/megawatt_hour'&&${facts}.Code==='megawatt_hour'&&${quiet}`);
  await check('search results show each hit\'s one-line summary from the backend',
   `(()=>{const hit=window.referenceRequests.filter(r=>r.path.includes('/reference/search?')).at(-1).payload.hits.find(h=>h.table==='futures_products');
    return hit&&[...document.querySelectorAll('#reference-search-hits li')].some(li=>li.textContent.includes(hit.label)&&li.querySelector('p')?.textContent===hit.summary.map(f=>f.label+': '+(f.value??'Not supplied')).join(' · '));})()`);

  // The editor read must name the display version the page read showed; a later one means the page changed in between.
  await evaluate("window.laterEditorReads=1;location.hash='#reference/locations/test_hub_a'");await wait(`!document.querySelector('#error').hidden&&${idle}`);
  await check('an editor read of another display version than the page shows asks for a refresh and offers no Edit',
   `document.querySelector('#error').textContent.includes('changed')&&!${visible('reference-edit')}`);

  const point={latitude:'41.123456789012345678',longitude:'-87.987654321098765432'};
  await open('locations/test_hub_a');
  const originalPoint=await evaluate(`(()=>{const item=${editorRead()}.item;return {name:item.name,latitude:item.latitude??'',longitude:item.longitude??''};})()`);
  await click('#reference-edit');await set('#reference-edit-latitude',point.latitude);await set('#reference-edit-longitude',point.longitude);await save();
  await open('locations/test_hub_a');await click('#reference-edit');
  await check('location inputs hold the exact canonical coordinate strings',`(()=>{const item=${editorRead()}.item;return item.latitude===${JSON.stringify(point.latitude)}&&item.longitude===${JSON.stringify(point.longitude)}&&document.querySelector('#reference-edit-latitude').value===item.latitude&&document.querySelector('#reference-edit-longitude').value===item.longitude;})()`);
  await set('#reference-edit-name',originalPoint.name+' browser');await save();
  await check('editing only a location name sends only the name, keeping both 18-place coordinates',`JSON.stringify(${commits}.at(-1).body.values)===${JSON.stringify(JSON.stringify({name:originalPoint.name+' browser'}))}&&${commits}.at(-1).status===200&&${quiet}`);
  await click('#reference-edit');for(const key of ['name','latitude','longitude'])await set('#reference-edit-'+key,originalPoint[key]);await save();
  await check('the original location values are committed again',`${commits}.at(-1).status===200&&${quiet}`);

  // The product page: its name and every contract label from one Edit, committed in order, stopping at the first refusal.
  // A product with at least two monthly contracts.
  const product=await evaluate("(async()=>{for(const row of (await(await fetch('/api/reference/catalog?table=futures_products')).json()).rows){const read=await(await fetch('/api/reference/futures-product?code='+encodeURIComponent(row.code))).json();if(read.series?.length>=2)return row.code;}})()");
  await open('futures_products/'+encodeURIComponent(product));
  const labels=await evaluate("[...document.querySelectorAll('#reference-item [data-edit-table=futures_series]')].map(c=>({code:c.dataset.editCode,label:c.textContent}))");
  const productName=await evaluate(`${editorRead()}.item.name`);
  await check('the product header shows its display version, not a revision identifier',`${facts}['Display version']!==undefined&&!('Reference revision' in ${facts})&&${labels.length}>=2`);
  await click('#reference-edit');
  const cell=code=>`#reference-item [data-edit-table=futures_series][data-edit-code=${JSON.stringify(code)}] input`;
  await check('one Edit turns the product name and each contract label into inputs',`${editing}.length===${labels.length+1}&&document.querySelector('#reference-item h2 input')?.value===${JSON.stringify(productName)}&&${JSON.stringify(labels)}.every(l=>document.querySelector('#reference-item [data-edit-code="'+l.code+'"] input')?.value===l.label)`);
  await set('#reference-item h2 input',productName+' browser');await set(cell(labels[0].code),'x'.repeat(301));await set(cell(labels[1].code),labels[1].label+' browser');
  const before=await evaluate(`${commits}.length`);
  await save();
  await check('product commits run in order and stop at the first refusal, keeping later edits unsent and shown',
   `(()=>{const sent=${commits}.slice(${before});return sent.length===2&&sent[0].body.table==='futures_products'&&sent[0].status===200&&sent[1].body.table==='futures_series'&&sent[1].body.code===${JSON.stringify(labels[0].code)}&&sent[1].status===422
    &&sent[1].body.baseRevisionId===sent[0].payload.displayRevisionId&&sent[1].body.expectedVersion===sent[0].payload.version&&!document.querySelector('#error').hidden&&!${visible('reference-edit-pending')}
    &&document.querySelector(${JSON.stringify(cell(labels[1].code))})?.value===${JSON.stringify(labels[1].label+' browser')};})()`);
  await set(cell(labels[0].code),labels[0].label+' browser');await click('#reference-edit-save');
  await check('Save again lists only the edits not yet committed',`document.querySelectorAll('#reference-edit-review li').length===2&&!document.querySelector('#reference-edit-review').textContent.includes(${JSON.stringify(productName+' browser')})`);
  await click('#reference-edit-confirm');
  await check('the remaining contract labels commit in order and the page shows every committed label',
   `(()=>{const sent=${commits}.slice(${before});return sent.length===4&&sent.slice(2).every(s=>s.status===200&&s.body.table==='futures_series')&&sent[3].body.expectedVersion===sent[2].payload.version
    &&document.querySelector('#reference-item h2').textContent===${JSON.stringify(productName+' browser')}&&${editing}.length===0&&${facts}['Display version']===String(sent[3].payload.version)
    &&${JSON.stringify(labels.slice(0,2))}.every(l=>document.querySelector('#reference-item [data-edit-code="'+l.code+'"]').textContent===l.label+' browser')&&${quiet};})()`);
  await click('#reference-history-open');
  await check('the product History lists its own and its contracts\' label versions',`(()=>{const v=${editorRead()}.versions;return v.length>=3&&v.some(x=>x.table==='futures_products')&&v.some(x=>x.table==='futures_series')&&document.querySelectorAll('#reference-history li').length===v.length&&document.querySelector('#reference-history').textContent.includes('Contract label');})()`);
  await click('#reference-edit');await set('#reference-item h2 input',productName);for(const l of labels.slice(0,2))await set(cell(l.code),l.label);await save();
  await check('the original product labels are committed again',`document.querySelector('#reference-item h2').textContent===${JSON.stringify(productName)}&&${quiet}`);

  // At phone width no view scrolls the page sideways; the monthly contracts scroll inside their own frame.
  await command('Emulation.setDeviceMetricsOverride',{width:375,height:812,deviceScaleFactor:1,mobile:true});
  try{
   await command('Page.navigate',{url:'about:blank'});await command('Page.navigate',{url:url+'/#reference/units'});await wait(`document.querySelector('#reference tbody tr')&&${idle}`);
   await check('a category list fits a 375px screen',fitsWidth);
   await set('#reference-search-all-text','gas');await submit('#reference-search-all');await wait(`!document.querySelector('#reference-search-hits').hidden&&${idle}`);
   await check('search results fit a 375px screen',`${fitsWidth}&&document.querySelectorAll('#reference-search-hits li').length>0`);
   await open('units/megawatt_hour');await click('#reference-edit');
   await check('an item in Edit fits a 375px screen',`${fitsWidth}&&${editing}.length>0`);await click('#reference-edit-cancel');
   await open('futures_products/'+encodeURIComponent(product));await click('#reference-history-open');
   await check('the product page with History open fits a 375px screen',`${fitsWidth}&&${visible('reference-history')}&&document.querySelectorAll('#reference-history li').length>0`);
   await click('#reference-edit');
   await check('the product page in Edit fits a 375px screen and its contracts scroll within their frame',
    `${fitsWidth}&&${editing}.length>1&&(f=>f.scrollWidth>f.clientWidth&&['auto','scroll'].includes(getComputedStyle(f).overflowX))(document.querySelector('#reference-item .product-contracts'))`);
   await click('#reference-edit-cancel');
  }finally{await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});}

  await click('#nav-market');await set('#date-context-on','2020-12-18');await wait(idle);await click('#date-context-latest');await click('#apply-view-context');
  await evaluate("location.hash='#reference/units/megawatt_hour'");await wait(`document.querySelector('#reference-edit-actions')&&${idle}`);
  await check('under an As-of context Edit is absent and the read-only reason shows',`!${visible('reference-edit')}&&document.querySelector('#reference-edit-reason').textContent.includes('Historical references')&&${quiet}`);
 }finally{
  await authority.replaceRoles((await authority.call('admin')).members.find(m=>m.login==='approver'),approver.roles);
 }
}
