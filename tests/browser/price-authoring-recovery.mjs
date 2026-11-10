/** Navigation and uncertain writes over the actual authorized publication endpoints. */
export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url}){
 await command('Runtime.enable');await command('Page.enable');await command('Emulation.setFocusEmulationEnabled',{enabled:true});
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.authoringRequests=[];{const send=window.fetch;window.fetch=async(input,init)=>{const path=new URL(typeof input==='string'?input:input.url,location.href).pathname;let response;
   if(window.refuseAuthoringToken&&path==='/api/price-publications/save'){window.refuseAuthoringToken=false;response=await send(input,{...init,headers:{...init.headers,'X-Preview-CSRF':'synthetic-invalid-token'}});}else response=await send(input,init);
   if(path.includes('price-publications')){const data=await response.clone().json();window.authoringRequests.push({path,body:init?.body?JSON.parse(init.body):null,status:response.status,data});
    if(path==='/api/price-publications/save'&&response.ok&&window.loseAuthoringReply){window.loseAuthoringReply=false;throw new TypeError('Synthetic lost response after the server committed');}}
   return response;};}`});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelector('#nav-configuration')&&"+idle,30000);
 await persona('editor');await click('#nav-configuration');await click('#price-setup-tabs-publications');
 const choose=async(selector,match)=>{const value=await evaluate(`Array.from(document.querySelector(${JSON.stringify(selector)}).options).find(o=>o.textContent.includes(${JSON.stringify(match)}))?.value`);if(!value)throw Error('Missing authoring choice: '+match);await set(selector,value);await wait(idle);};
 await choose('#price-publications-library','Synthetic API monthly report');
 await set('#price-publications-name','Synthetic recovery control test');
 const unload="(()=>{const event=new Event('beforeunload',{cancelable:true});window.dispatchEvent(event);return event.defaultPrevented;})()";
 await check('unsaved publication edits register a browser-close warning',unload);
 // Leaving any editor asks before a context or page change can discard it.
 await evaluate("document.querySelector('#curve-price-contracts').click()");await wait("!document.querySelector('#price-publications-editor-prompt').hidden");
 await click('#price-publications-editor-prompt button:first-child');
 await check('keeping edits preserves the current editor and browser-close warning',"!document.querySelector('#price-setup-page').hidden&&document.querySelector('#price-publications-name').value==='Synthetic recovery control test'&&"+unload);
 await evaluate('window.refuseAuthoringToken=true');await submit('#price-publications-form');
 await check('an expired request token is a confirmed refusal, not an uncertain save',"window.authoringRequests.filter(r=>r.path==='/api/price-publications/save').at(-1).data.code==='request_token'&&document.querySelector('#price-publications-editor > .price-authoring-receipt').hidden&&!document.querySelector('#error').hidden");
 await evaluate('window.loseAuthoringReply=true');await submit('#price-publications-form');
 await check('a lost accepted reply retains an exact blocked request',"!document.querySelector('#price-publications-editor > .price-authoring-receipt').hidden&&document.querySelector('#price-publications-name').matches(':disabled')");
 await evaluate("window.originalAuthoringWrite=window.authoringRequests.filter(r=>r.path==='/api/price-publications/save'&&r.status===200).at(-1)");
 await click('#price-publications-editor > .price-authoring-receipt button:first-of-type');
 await check('retry sends the original body and resolves the committed publication version',"(()=>{const a=window.originalAuthoringWrite,b=window.authoringRequests.filter(r=>r.path==='/api/price-publications/save'&&r.status===200).at(-1);return JSON.stringify(a.body)===JSON.stringify(b.body)&&JSON.stringify(a.data)===JSON.stringify(b.data)&&document.querySelector('#price-publications-editor > .price-authoring-receipt').hidden&&document.querySelector('#error').hidden;})()");
 await check('a confirmed save removes the browser-close warning',`!${unload}`);
 // Structural edits to a schedule also count, even without typing into an input.
 await click('#price-publications-add-period');
 await check('adding a covered period registers unsaved changes',unload);
 await evaluate("document.querySelector('#price-setup-tabs-contracts').click()");await wait("!document.querySelector('#price-publications-occurrence-prompt').hidden||!document.querySelector('#price-publications-editor-prompt').hidden");
 await evaluate("document.querySelector('#price-setup-tabs-publications').click()");
 await evaluate("[...document.querySelectorAll('#price-publications-editor .side-panel-prompt:not([hidden]) button')].at(-1).click()");await wait(idle);
 await check('discarding a structural edit removes the browser-close warning',`!${unload}&&!document.querySelector('#contract-price-terms-editor').hidden`);
 await check('a second tab click cannot replace the pending navigation or skip its read',"document.querySelector('#price-setup-tabs-contracts').getAttribute('aria-selected')==='true'&&document.querySelector('#schedules-page').hidden&&document.querySelector('#error').hidden");
 await click('#price-setup-tabs-dates');await set('#curve-dates-name','Synthetic unsaved date rule');
 await check('typing into linked Date mappings registers the existing browser-close warning',unload);
 await evaluate("document.querySelector('#nav-configuration').click()");await wait("!document.querySelector('#schedules-page-prompt').hidden");
 await click('#schedules-page-prompt button:first-child');
 await check('keeping a Date mapping edit retains its Schedules page and browser-close warning',`!document.querySelector('#schedules-page').hidden&&document.querySelector('#curve-dates-name').value==='Synthetic unsaved date rule'&&${unload}`);
 await evaluate("document.querySelector('#nav-configuration').click()");await wait("!document.querySelector('#schedules-page-prompt').hidden");
 await click('#schedules-page-prompt button:last-child');
 await check('discarding a Date mapping edit returns to Price setup without a browser-close warning',`!${unload}&&!document.querySelector('#price-setup-page').hidden&&!document.querySelector('#contract-price-terms-editor').hidden`);
}
