export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url}){
 await command('Page.enable');await command('Runtime.enable');await command('Page.navigate',{url});await wait(idle);await persona('admin');
 await click('#nav-admin');await click('#access-policy-page > details > summary');
 await check('access settings read their complete current policy without a market selection',"document.querySelectorAll('#access-policy-page [data-policy-field]').length===5&&document.querySelector('#access-policy-page [data-policy-field=sessionMinutes]').value==='30'&&document.querySelector('#error').hidden");
 await evaluate("window.__policySend=window.fetch;window.__policyRequests=[];window.__dropPolicy=true;window.fetch=async(...args)=>{const response=await window.__policySend(...args);if(String(args[0])==='/api/settings/operating-policies'&&args[1]?.method==='POST'){window.__policyRequests.push(JSON.parse(args[1].body));if(window.__dropPolicy){window.__dropPolicy=false;throw new TypeError('Synthetic lost policy acknowledgement');}}return response;}");
 await set('#access-policy-page [data-policy-field=sessionMinutes]','29');await submit('#access-policy-page form');
 await check('an unconfirmed policy save retains its exact request and offers retry without a second save',"!document.querySelector('#access-policy-page button:nth-child(2)').hidden&&document.querySelector('#access-policy-page button[type=submit]').disabled&&window.__policyRequests.length===1");
 await click('#access-policy-page button:nth-child(2)');
 await check('retry confirms the same operation after a lost committed response',"window.__policyRequests.length===2&&JSON.stringify(window.__policyRequests[0])===JSON.stringify(window.__policyRequests[1])&&document.querySelector('#access-policy-page [data-policy-field=sessionMinutes]').value==='29'&&document.querySelector('#error').hidden");
 await click('#access-policy-page > details > summary');await set('#access-policy-page [data-policy-field=sessionMinutes]','30');await submit('#access-policy-page form');
 await check('a deliberate subsequent policy revision restores the selected access operating value',"document.querySelector('#access-policy-page [data-policy-field=sessionMinutes]').value==='30'&&document.querySelector('#error').hidden");
 await click('#nav-ai');await click('#assistant-policy-page > details > summary');
 await check('the same policy widget renders assistant limits from server field metadata',"document.querySelectorAll('#assistant-policy-page [data-policy-field]').length===6&&document.querySelector('#assistant-policy-page [data-policy-field=pinBytes]').max==='262144'&&document.querySelector('#assistant-policy-page [data-policy-field=toolResultRows]').value==='500'");
 await click('#nav-admin');
 await persona('viewer');await check('a reader has no policy administration entry or retained policy form',"document.querySelector('#nav-admin').hidden&&document.querySelector('#access-policy-page').hidden&&document.querySelector('#assistant-policy-page').hidden");
}
