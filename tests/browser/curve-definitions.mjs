// D312: the Curves step previews a curve's rolling contract window from the mapping tools/check_curve_definitions.py publishes.
export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url}){
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await persona('viewer');await click('#nav-schedules');await set('#date-context-on','2021-01-15');await wait(idle);await click('#date-context-latest');
 await click('#schedule-tab-curves');await wait("document.querySelectorAll('#curve-definition-mapping option').length>1&&"+idle);
 await check('Coming soon business cases are listed and cannot be chosen',
  "[...document.querySelectorAll('#curve-definition-target option')].some(o=>o.textContent.includes('Hourly physical price')&&o.disabled)&&[...document.querySelectorAll('#curve-definition-rule option')].some(o=>o.textContent.includes('Formula contracts')&&o.disabled)&&document.querySelector('#curve-definition-save').disabled");
 const choose=(id,text)=>`(()=>{const field=document.querySelector('#curve-definition-${id}');field.value=[...field.options].find(o=>o.textContent.includes(${JSON.stringify(text)})&&!o.disabled).value;field.dispatchEvent(new Event('change',{bubbles:true}));})()`;
 await set('#curve-definition-code','browser_gas_curve');await set('#curve-definition-name','Synthetic browser gas curve');
 for(const [id,text] of [['location','test_gas at test_hub_a'],['kind','Exchange futures'],['product','test_gas_future'],['quote','test_gas_outright'],['profile','test_uniform'],
  ['mapping','curve_months'],['target','test_future_target'],['rule','Direct mark'],['role','Daily settlement'],['source','fixture'],['missing','Refuse'],['interpolation','None']])await evaluate(choose(id,text));
 await set('#curve-definition-count','3');
 await submit('.curve-definition-editor form');await wait(idle);
 await check('a futures curve previews its three positions with their exchange series',
  "document.querySelector('#error').hidden&&[...document.querySelectorAll('.curve-definition-editor tbody tr')].map(r=>r.cells[5].textContent).join()==='test_gas_202102,test_gas_202103,test_gas_202104'",
  "({error:document.querySelector('#error').textContent,rows:[...document.querySelectorAll('.curve-definition-editor tbody tr')].map(r=>r.textContent)})");
 await set('#curve-definition-count','5');await submit('.curve-definition-editor form');await wait(idle);
 await check('a refused definition shows the server refusal and no stale window',
  "!document.querySelector('#error').hidden&&document.querySelector('#curve-definition-result').hidden");
 await check('the curve form stays inside the editor beside the calendar',"(()=>{const editor=document.querySelector('.schedule-workspace-editor');return editor.scrollWidth<=editor.clientWidth+1;})()");
}
