export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url,shot}){
 await check('daily quantities use all retained physical gas days',"document.querySelectorAll('#gq-grid tbody tr').length===62&&document.querySelector('#gq-grid').textContent.includes('1000/31')");
 await click('#gq-history-details summary');
 await set('#gq-row','0');await set('#gq-kind','nominated');await set('#gq-status','provisional');await set('#gq-quantity','31');await set('#gq-evidence','Synthetic daily nomination');await submit('#gq-form');
 await check('recording a nomination preserves the booked contract and selects one quantity',"document.querySelector('#gq-grid tbody tr').textContent.includes('1000/31')&&document.querySelector('#gq-grid tbody tr').textContent.includes('31 · provisional')&&document.querySelectorAll('#gq-history tbody tr').length===1&&document.querySelector('#gc-total').textContent==='Present value: 64 USD'");
 await set('#gq-quantity','32');await set('#gq-evidence','Synthetic corrected daily nomination');
 await evaluate(`{const original=window.fetch;window.fetch=async(...args)=>{const response=await original(...args);if(String(args[0]).endsWith('/gas/quantities/record')&&response.ok)throw new TypeError('Synthetic lost quantity response');return response;};}`);
 await submit('#gq-form');await check('an uncertain daily quantity command is retained without allowing a replacement',"!document.querySelector('#gq-pending').hidden&&document.querySelector('#gq-quantity').disabled");
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await click('#nav-gas');await wait("!document.querySelector('#gq-pending').hidden&&"+idle);await click('#gq-check');await click('#gq-history-details summary');
 await check('reload and status recover the original daily quantity revision once',"document.querySelector('#gq-pending').hidden&&document.querySelectorAll('#gq-history tbody tr').length===2&&document.querySelector('#gq-grid tbody tr').textContent.includes('32 · provisional')");
 await set('#gq-policy','final_allocated_billing_v1');await click('#gq-refresh');
 await check('billing refuses missing final evidence and clears previous quantities',"document.querySelectorAll('#gq-grid tbody tr').length===0&&document.querySelector('#gq-record').disabled&&document.querySelector('#error').textContent.length>0");
 await click('#gq-history-details summary');
 await set('#gq-policy','operational_projection_v1');await click('#gq-refresh');
 await evaluate(`{const original=window.fetch;window.fetch=async(...args)=>{if(String(args[0]).endsWith('/gas/composition/directory'))await new Promise(resolve=>setTimeout(resolve,120));if(String(args[0]).endsWith('/gas/quantities/history'))window.quantityHistoryRequest=JSON.parse(args[1].body);return original(...args);};document.querySelector('#gc-refresh').click();document.querySelector('#gq-history-details').open=true;}`);await wait(idle);
 await check('history opened during another read loads under the grid retained context',"document.querySelectorAll('#gq-history tbody tr').length===2&&window.quantityHistoryRequest.context.knowledgeMode==='retained'&&typeof window.quantityHistoryRequest.context.contextId==='string'");
 await check('daily quantity workspace fits its viewport',"document.documentElement.scrollWidth<=innerWidth");await shot('gas-daily-quantities');
 await persona('trader');await check('switching to a trade reader opens gas when the shared blotter scope is unsupported',"!document.querySelector('#profile-dialog').open&&!document.querySelector('#gas-page').hidden");
}
