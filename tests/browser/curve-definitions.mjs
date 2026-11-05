// D318: Curves & prices defines a curve from the mapping tools/check_curve_definitions.py publishes and previews its rolling contract
// window at the close the header selects.
export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url}){
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await persona('viewer');
 const close=at=>evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith(${JSON.stringify(at)})).id;})()`);
 await set('#snapshot',await close('2021-01-15T16:00:00'));await wait(idle,30000);
 await click('#curve-define');await wait("document.querySelectorAll('#curve-definition-mapping option').length>1&&"+idle);
 await check('the editor replaces the board on Curves & prices, and Schedules keeps only schedules and date mappings',
  "!document.querySelector('#curve-definition').hidden&&document.querySelector('#market-board').hidden&&document.querySelector('#curve-detail').hidden&&!document.querySelector('#schedule-tab-curves')");
 await check('Coming soon business cases are listed and cannot be chosen',
  "[...document.querySelectorAll('#curve-definition-target option')].some(o=>o.textContent.includes('Hourly physical price')&&o.disabled)&&[...document.querySelectorAll('#curve-definition-rule option')].some(o=>o.textContent.includes('Formula contracts')&&o.disabled)&&document.querySelector('#curve-definition-save').disabled");
 const choose=(id,text)=>`(()=>{const field=document.querySelector('#curve-definition-${id}');field.value=[...field.options].find(o=>o.textContent.includes(${JSON.stringify(text)})&&!o.disabled).value;field.dispatchEvent(new Event('change',{bubbles:true}));})()`;
 await set('#curve-definition-code','browser_gas_curve');await set('#curve-definition-name','Synthetic browser gas curve');
 for(const [id,text] of [['location','test_gas at test_hub_a'],['kind','Exchange futures'],['product','test_gas_future'],['quote','test_gas_outright'],['profile','test_uniform'],
  ['mapping','curve_months'],['target','test_future_target'],['rule','Direct mark'],['role','Daily settlement'],['source','fixture'],['missing','Refuse'],['interpolation','None']])await evaluate(choose(id,text));
 await set('#curve-definition-count','3');
 const series="[...document.querySelectorAll('.curve-definition-editor tbody tr')].map(r=>r.cells[5].textContent).join()";
 const observed=`({error:document.querySelector('#error').textContent,rows:${series}})`;
 await submit('.curve-definition-editor form');await wait(idle);
 await check('a futures curve previews its three positions at the selected close',
  `document.querySelector('#error').hidden&&${series}==='test_gas_202102,test_gas_202103,test_gas_202104'`,observed);
 // Another close makes the shown window stale; previewing again rolls it past the expired February contract.
 await set('#snapshot',await close('2021-02-15T16:00:00'));await wait(idle,30000);
 await check('a changed close clears the previous window',"document.querySelector('#curve-definition-result').hidden&&!document.querySelector('#curve-definition').hidden");
 await submit('.curve-definition-editor form');await wait(idle);
 await check('the window rolls with the header close',`document.querySelector('#error').hidden&&${series}==='test_gas_202103,test_gas_202104,test_gas_202105'`,observed);
 await set('#curve-definition-count','5');await submit('.curve-definition-editor form');await wait(idle);
 await check('a refused definition shows the server refusal and no stale window',
  "!document.querySelector('#error').hidden&&document.querySelector('#curve-definition-result').hidden");
 await check('the curve form stays inside the market content',"(()=>{const content=document.querySelector('.market-content');return content.scrollWidth<=content.clientWidth+1;})()");
 await click('#curve-definition-back');await wait(idle);
 await check('All markets returns to the board',"document.querySelector('#curve-definition').hidden&&!document.querySelector('#market-board').hidden");
}
