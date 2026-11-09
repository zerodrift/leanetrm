export async function run({command,evaluate,wait,click,set,persona,check,shot,idle,url}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelector('#nav-configuration')&&"+idle,30000);
 await persona('editor');await click('#nav-configuration');
 await check('contract pricing is the default editor without a wizard',"!document.querySelector('#contract-price-terms-editor').hidden&&document.querySelector('#price-setup-tabs-contracts').getAttribute('aria-selected')==='true'&&document.querySelector('#error').hidden");
 const choose=async(selector,match)=>{const value=await evaluate(`Array.from(document.querySelector(${JSON.stringify(selector)}).options).find(o=>o.textContent.includes(${JSON.stringify(match)}))?.value`);if(!value)throw Error('Missing authoring choice: '+match);await set(selector,value);await wait(idle);};
 // The HTTP fixture saved this exact draft, including its terms and source mappings.
 await choose('#contract-terms-curve','Synthetic exchange contract terms');
 const source=await evaluate("document.querySelector('#contract-terms-source').options[1].value");await set('#contract-terms-source',source);await wait(idle);
 await set('#contract-terms-scope','curve');await wait("document.querySelector('#contract-terms-settlement').value==='exchange_final_v1'");
 await check('saved pricing terms round-trip through the real catalogue',"document.querySelector('#contract-terms-mapping').value==='same_delivery_period_v1'&&document.querySelector('#contract-terms-pending').value==='require_final_v1'");
 await click('#price-setup-tabs-publications');
 await choose('#price-publications-library','Synthetic API monthly report');
 await check('publication editor loads exact saved indices and calendar occurrences',"document.querySelector('#price-publications-indices input:checked')!==null&&document.querySelector('#price-publications-occurrences').textContent.includes('2021-01-01')&&document.querySelector('#error').hidden");
 await shot('price-publications-editor');
 // A changed form is retained until the user explicitly leaves it.
 await set('#price-publications-name','Unsaved publication name');
 await evaluate("document.querySelector('#price-setup-tabs-contracts').click()");await wait("!document.querySelector('#price-publications-editor-prompt').hidden");
 await click('#price-publications-editor-prompt button:first-child');
 await check('tabs keep unsaved publication edits when the person stays',"!document.querySelector('#price-publications-editor').hidden&&document.querySelector('#price-publications-name').value==='Unsaved publication name'");
 await evaluate("document.querySelector('#price-setup-tabs-contracts').click()");await wait("!document.querySelector('#price-publications-editor-prompt').hidden");await click('#price-publications-editor-prompt button:last-child');await wait(idle);
 await check('discarding opens contract pricing and clears the unsaved form',"!document.querySelector('#contract-price-terms-editor').hidden&&document.querySelector('#error').hidden");
 await shot('contract-pricing-editor');
 await click('#price-setup-tabs-dates');
 await check('Date rules opens the existing Schedules date-mapping editor',"!document.querySelector('#schedules-page').hidden&&!document.querySelector('#curve-date-editor').hidden&&document.querySelector('#curve-dates-saved').textContent.length>0&&document.querySelector('#error').hidden");
 await persona('viewer');await click('#nav-configuration');await click('#price-setup-tabs-publications');
 await check('readers can inspect publications while writes stay disabled',"document.querySelector('#price-publications-save').disabled&&document.querySelector('#price-publications-new').disabled&&document.querySelector('#error').hidden");
}
