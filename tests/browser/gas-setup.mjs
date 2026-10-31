export async function run({command,evaluate,wait,click,set,persona,check,shot,idle,url}){
 await persona('editor');await click('#nav-gas-setup');await wait("document.querySelectorAll('#gs-row option').length>0&&"+idle);
 await check('gas setup uses reference selectors and separate configuration categories',"!document.querySelector('#gas-setup-page').hidden&&document.querySelectorAll('#gs-tab option').length===4&&document.querySelector('#gs-field-pipeline').tagName==='SELECT'");
 await set('#gs-tab','groups');await set('#gs-row',await evaluate("[...document.querySelector('#gs-row').options].find(o=>o.textContent==='synthetic_gas_group_a').value"));
 await set('#gs-field-adjustment','-0.15');await click('#nav-reference');
 await check('navigation protects unsaved economic setup edits',"document.querySelector('#gs-leave').open&&!document.querySelector('#gas-setup-page').hidden");
 await click('#gs-keep');await click('#gs-save');
 const draft=await evaluate("document.querySelector('#gs-version').value");
 await check('saving creates a reviewable draft and leaves publication to the approver',"document.querySelector('#gs-status').textContent.includes('Draft saved')&&document.querySelector('#gs-publish').disabled");
 await set('#gs-from','2021-01-01');await set('#gs-to','2021-02-01');await click('#gs-validate');
 await check('validation previews shared pricing and meter coverage for each point',"document.querySelector('#gs-validation').textContent.includes('passed')&&document.querySelectorAll('#gs-preview tbody tr').length===5&&document.querySelector('#gs-preview').textContent.includes('local_gas')&&document.querySelector('#gs-preview').textContent.includes('-0.15')");
 await persona('approver');await click('#nav-gas-setup');await set('#gs-version',draft);await click('#gs-open');
 await set('#gs-from','2021-01-01');await set('#gs-to','2021-02-01');await click('#gs-validate');
 await check('the approver reviews immutable fields and can publish the validated version',"document.querySelector('#gs-field-adjustment').disabled&&!document.querySelector('#gs-publish').disabled");
 await set('#gs-to','2021-01-31');
 await check('changing the delivery period invalidates the previous coverage preview',"document.querySelector('#gs-publish').disabled&&document.querySelectorAll('#gs-preview tbody tr').length===0");
 await set('#gs-to','2021-02-01');await click('#gs-validate');
 await evaluate(`{const original=window.fetch;window.fetch=async(...args)=>{const response=await original(...args);if(String(args[0]).endsWith('/gas-setup/publish')&&response.ok)throw new TypeError('Synthetic lost publication response');return response;};}`);
 await click('#gs-publish');await check('an uncertain publication retains the original command',"!document.querySelector('#gs-retry').hidden&&document.querySelector('#gs-tab').disabled");
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await click('#nav-gas-setup');await click('#gs-retry');
 await check('reload and exact retry recover the single published version',`document.querySelector('#gs-active').textContent.includes(${JSON.stringify(draft)})&&document.querySelector('#gs-retry').hidden&&document.querySelector('#gs-status').textContent.includes('Version published')`);
 await check('the configuration editor fits its viewport',"document.documentElement.scrollWidth<=innerWidth");
 await shot('gas-delivery-setup');
}
