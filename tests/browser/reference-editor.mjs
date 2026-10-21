export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url}){
 await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.referenceRequests=[];{
  const original=window.fetch;window.fetch=async(...args)=>{
   const path=String(args[0]);
   if(path.includes('/reference/editor?')&&window.failReferenceRefresh){window.failReferenceRefresh=false;return new Response(JSON.stringify({code:'synthetic_refresh',message:'Synthetic reference refresh failure'}),{status:503});}
   const response=await original(...args);
   if(path.includes('/reference/')){
    window.referenceRequests.push({path,body:args[1]?.body?JSON.parse(args[1].body):null,status:response.status,payload:await response.clone().json()});
    if(path.endsWith('/reference/save')&&window.dropReferenceSave){window.dropReferenceSave=false;throw Error('Synthetic lost reference save response');}
    if(path.endsWith('/reference/apply')&&window.dropReferenceApply){window.dropReferenceApply=false;throw Error('Synthetic lost reference apply response');}
    if(path.endsWith('/reference/save')&&window.malformedReferenceSave){window.malformedReferenceSave=false;return new Response(JSON.stringify({revisionId:'inconsistent',state:'draft'}),{status:200});}
    if(path.endsWith('/reference/save')&&window.failRefreshAfterSave){window.failRefreshAfterSave=false;window.failReferenceRefresh=true;}
   }
   return response;
  };
 }`});
 const ready=()=>wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 const editor=async(table='units')=>{await click('#nav-reference');await set('#reference-table',table);await wait(idle);await click('#reference-editor>button');};
 await command('Page.navigate',{url:url+'/'});await ready();await persona('editor');await editor();await set('#reference-edit-row','megawatt_hour');await wait(idle);
 const originalSymbol=await evaluate("document.querySelector('#reference-edit-symbol').value"),sample=originalSymbol+' browser';
 await check('reference authors edit display metadata while identity and financial fields remain absent',"!document.querySelector('#reference-edit-symbol').disabled&&!document.querySelector('#reference-edit-code')&&!document.querySelector('#reference-edit-factor_numerator')&&!document.querySelector('#reference-edit-dimension_code')");
 await check('reference inputs describe the server Unicode character limit without a UTF16 cap',"(()=>{const field=window.referenceRequests.filter(r=>r.path.includes('/reference/editor?')).at(-1).payload.fields.find(f=>f.key==='symbol'),input=document.querySelector('#reference-edit-symbol');return field.maxLength===80&&input.maxLength===-1&&document.querySelector('#'+input.getAttribute('aria-describedby')).textContent.includes('80');})()");
 await set('#reference-edit-symbol',sample);await evaluate('window.dropReferenceSave=true');await click('#reference-edit-save');
 const originalSave=await evaluate("window.referenceRequests.find(r=>r.path.endsWith('/reference/save')).body");
 await check('a lost committed reference save retains its exact request and blocks a new edit',"!document.querySelector('#reference-edit-retry').disabled&&document.querySelector('#reference-edit-save').disabled&&!document.querySelector('#error').hidden");
 await command('Page.navigate',{url:url+'/'});await ready();await click('#nav-reference');await set('#reference-table','units');await wait(idle);
 await check('reload restores the unconfirmed reference request for reconciliation',"document.querySelector('#reference-edit-retry')&&!document.querySelector('#reference-edit-retry').disabled");await click('#reference-edit-retry');
 await check('reference save retry after reload uses the same identity and exact payload',`JSON.stringify(window.referenceRequests.find(r=>r.path.endsWith('/reference/save')).body)===${JSON.stringify(JSON.stringify(originalSave))}&&document.querySelector('#error').hidden`);
 const saved=await evaluate("window.referenceRequests.find(r=>r.path.endsWith('/reference/save')).payload");
 await check('authors can review the saved version but cannot apply it',`document.querySelector('#reference-edit-version').value===${JSON.stringify(saved.revisionId)}&&document.querySelector('#reference-editor').textContent.includes(${JSON.stringify(sample)})&&document.querySelector('#reference-edit-apply').disabled`);
 await persona('approver');await click('#example');await editor();await set('#reference-edit-version',saved.revisionId);
 await check('a publisher can review exact saved display changes before applying',`!document.querySelector('#reference-edit-apply').disabled&&document.querySelector('#reference-edit-save').disabled&&document.querySelector('#reference-editor').textContent.includes(${JSON.stringify(sample)})`);await click('#reference-edit-apply');await click('#nav-market');
 await check('reviewed activation refreshes the current curve unit label',`document.querySelector('#unit').textContent.includes(${JSON.stringify(sample)})&&document.querySelector('#error').hidden`);
 // Restore the original label through another immutable save, then lose the
 // committed apply response to qualify publisher recovery without write rights.
 await persona('editor');await editor();await set('#reference-edit-row','megawatt_hour');await wait(idle);await set('#reference-edit-symbol',originalSymbol);await click('#reference-edit-save');
 const restored=await evaluate("window.referenceRequests.filter(r=>r.path.endsWith('/reference/save')).at(-1).payload");
 await persona('approver');await editor();await set('#reference-edit-version',restored.revisionId);await evaluate('window.dropReferenceApply=true');await click('#reference-edit-apply');
 const originalApply=await evaluate("window.referenceRequests.filter(r=>r.path.endsWith('/reference/apply')).at(-1).body");
 await check('a lost committed apply retains publisher reconciliation',"!document.querySelector('#reference-edit-retry').disabled&&!document.querySelector('#error').hidden");
 await command('Page.navigate',{url:url+'/'});await ready();await click('#nav-reference');await set('#reference-table','units');await wait(idle);await click('#reference-edit-retry');
 await check('publisher retry after reload reconciles the exact retained apply request',`JSON.stringify(window.referenceRequests.find(r=>r.path.endsWith('/reference/apply')).body)===${JSON.stringify(JSON.stringify(originalApply))}&&document.querySelector('#error').hidden`);await click('#nav-market');await click('#example');
 await check('confirmed recovery displays the restored label',`document.querySelector('#unit').textContent.includes(${JSON.stringify(originalSymbol)})&&!document.querySelector('#unit').textContent.includes(${JSON.stringify(sample)})&&document.querySelector('#error').hidden`);
 await persona('editor');await editor();await set('#reference-edit-row','megawatt_hour');await wait(idle);
 const scalarSymbol='𝛼'.repeat(80);await set('#reference-edit-symbol',scalarSymbol);await click('#reference-edit-save');
 await check('a valid 80-scalar symbol is saved with all supplementary characters intact',`window.referenceRequests.filter(r=>r.path.endsWith('/reference/save')).at(-1).status===200&&window.referenceRequests.filter(r=>r.path.endsWith('/reference/save')).at(-1).body.values.symbol===${JSON.stringify(scalarSymbol)}&&document.querySelector('#error').hidden`);
 await set('#reference-edit-symbol',scalarSymbol+'𝛼');await click('#reference-edit-save');
 await check('the backend rejects an 81-scalar symbol before retaining a metadata draft',"window.referenceRequests.filter(r=>r.path.endsWith('/reference/save')).at(-1).status===422&&!document.querySelector('#error').hidden&&document.querySelector('#reference-edit-retry').disabled");
 await set('#reference-edit-symbol','Synthetic oversized symbol '+'x'.repeat(32*1024));await click('#reference-edit-save');
 await check('a symbol past the request size limit is refused before any write, and no request is kept',"(()=>{const r=window.referenceRequests.filter(r=>r.path.endsWith('/reference/save')).at(-1);return r.status===413&&r.payload.code==='request_too_large'&&!document.querySelector('#error').hidden&&document.querySelector('#reference-edit-retry').disabled&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.reference.request.'));})()");
 // Both responses follow actual successful commits. Capture each boundary's
 // result before assertions so a failing case does not hide the other one.
 await persona('editor');await editor();await set('#reference-edit-row','megawatt_hour');await wait(idle);
 await evaluate('window.malformedReferenceSave=true');await click('#reference-edit-save');
 const malformedRetained=await evaluate("!document.querySelector('#reference-edit-retry').disabled&&!document.querySelector('#error').hidden");
 const malformedRequest=await evaluate("window.referenceRequests.filter(r=>r.path.endsWith('/reference/save')).at(-1).body");
 if(malformedRetained){await click('#reference-edit-retry');}
 await editor();await set('#reference-edit-row','megawatt_hour');await wait(idle);
 await evaluate('window.failRefreshAfterSave=true');await click('#reference-edit-save');
 const refreshFailure=await evaluate("!document.querySelector('#error').hidden&&document.querySelector('#reference-edit-retry').disabled&&document.querySelector('#reference-editor').textContent.includes('Reference editing is unavailable')");
 const malformedReconciled=await evaluate(`window.referenceRequests.filter(r=>r.path.endsWith('/reference/save')).some((r,i,all)=>i>0&&JSON.stringify(r.body)===${JSON.stringify(JSON.stringify(malformedRequest))}&&JSON.stringify(all[i-1].body)===JSON.stringify(r.body))`);
 console.log('Reference transport boundary results:',JSON.stringify({malformedRetained,malformedReconciled,refreshFailure}));
 await check('a malformed committed save confirmation retains the exact request for reconciliation',String(malformedRetained&&malformedReconciled));
 await check('a failed refresh after confirmed save is visible without retaining a confirmed mutation',String(refreshFailure));
 const point={latitude:'41.123456789012345678',longitude:'-87.987654321098765432'};
 await persona('editor');await editor('locations');await set('#reference-edit-row','test_hub_a');await wait(idle);
 const originalPoint=await evaluate("Object.fromEntries(['name','latitude','longitude'].map(key=>[key,document.querySelector('#reference-edit-'+key).value]))");
 await set('#reference-edit-latitude',point.latitude);await set('#reference-edit-longitude',point.longitude);await click('#reference-edit-save');
 const pointVersion=await evaluate("window.referenceRequests.filter(r=>r.path.endsWith('/reference/save')).at(-1).payload.revisionId");
 await persona('approver');await editor('locations');await set('#reference-edit-version',pointVersion);await click('#reference-edit-apply');
 await check('activated location grids preserve the complete coordinate strings',`(()=>{const row=[...document.querySelectorAll('#reference tbody tr')].find(r=>r.textContent.includes(${JSON.stringify(point.latitude)}));return row&&row.textContent.includes(${JSON.stringify(point.longitude)});})()`,"document.querySelector('#reference').textContent");
 await persona('editor');await editor('locations');await set('#reference-edit-row','test_hub_a');await wait(idle);
 await check('location editors receive exact canonical coordinate strings',`(()=>{const item=window.referenceRequests.filter(r=>r.path.includes('/reference/editor?')).at(-1).payload.item;return item.latitude===${JSON.stringify(point.latitude)}&&item.longitude===${JSON.stringify(point.longitude)}&&document.querySelector('#reference-edit-latitude').value===item.latitude&&document.querySelector('#reference-edit-longitude').value===item.longitude;})()`);
 await set('#reference-edit-name',originalPoint.name+' browser');await click('#reference-edit-save');
 await check('editing only a location name preserves both 18-place coordinates',`(()=>{const values=window.referenceRequests.filter(r=>r.path.endsWith('/reference/save')).at(-1).body.values;return values.latitude===${JSON.stringify(point.latitude)}&&values.longitude===${JSON.stringify(point.longitude)}&&document.querySelector('#error').hidden;})()`);
 // Restore through another reviewed version, preserving the immutable audit.
 for(const key of ['name','latitude','longitude'])await set('#reference-edit-'+key,originalPoint[key]);await click('#reference-edit-save');
 const originalPointVersion=await evaluate("window.referenceRequests.filter(r=>r.path.endsWith('/reference/save')).at(-1).payload.revisionId");
 await persona('approver');await editor('locations');await set('#reference-edit-version',originalPointVersion);await click('#reference-edit-apply');
 await click('#date-context>summary');await set('#date-context-on','2020-12-18');await click('#date-context-latest');await click('#apply-view-context');await editor();
 await check('historical reference views are read-only and retain their original metadata',"document.querySelector('#reference-edit-save').disabled&&document.querySelector('#reference-edit-apply').disabled&&document.querySelector('#reference-editor').textContent.includes('Historical references')&&document.querySelector('#error').hidden");
 await persona('viewer');await editor();await check('ordinary readers cannot save or activate reference versions',"document.querySelector('#reference-edit-save').disabled&&document.querySelector('#reference-edit-apply').disabled");
}
