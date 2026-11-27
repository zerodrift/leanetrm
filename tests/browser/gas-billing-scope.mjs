// The real Jobs surface uses the same explicit selection and command recovery.
export async function run(a){
 const {click,set,evaluate,wait,check,idle,fixture,command,shot}=a;
 await click('#profile-open');await set('#zone','UTC');await click('#profile-save');await wait(idle,60000);
 await click('#nav-jobs');await wait(idle);
 const field=k=>'[data-gas-scope="'+k+'"]';
 await evaluate(`document.querySelector(${JSON.stringify(field('business-date'))}).closest('details').open=true`);
 const now=new Date(Date.now()+60000),until=new Date(now.getTime()+3600000);
 for(const [key,value]of [['business-date',now.toISOString().slice(0,10)],['due',now.toISOString().slice(0,19)]])await set(field(key),value);
 await click(field('choices'));await wait(`document.querySelector(${JSON.stringify(field('policy'))}).options.length>1&&`+idle);
 await check('gas processing does not preselect a financial policy',`document.querySelector(${JSON.stringify(field('policy'))}).value===''&&document.querySelector(${JSON.stringify(field('once'))}).disabled`);
 await evaluate(`(()=>{const books=document.querySelector(${JSON.stringify(field('books'))});for(const option of books.options)option.selected=option.value===${JSON.stringify(fixture.gasScope.bookId)};books.dispatchEvent(new Event('change',{bubbles:true}));})()`);
 const selected=JSON.stringify([fixture.gasScope.policyId,1,'review']);
 for(const [key,value]of Object.entries({policy:selected,from:'2021-01-01',to:'2021-01-03',final:'final_allocated_billing_v1',profile:'gas_final_daily_entry_cash_v1',rounding:'currency_minor_units_to_even_v1',discovery:'gas_billing_scope_reassessment_v1',knowledge:'current_knowledge_at_execution_v1',scheduled:'manual_scheduled_quantity_v1',name:'Synthetic browser gas processing',expires:until.toISOString().slice(0,19),delay:'0','page-size':'8',dispatch:'2',timeout:'30',attempts:'2'}))await set(field(key),value);
 await click(field('preview'));await wait(`!document.querySelector(${JSON.stringify(field('once'))}).disabled&&`+idle);
 await check('preview explains old-day corrections before enrollment',`document.querySelector(${JSON.stringify(field('once'))}).closest('details').textContent.includes('needs correction')`);
 await evaluate(`(()=>{const send=window.fetch;window.__scopeFetch=send;window.__scopeLost=false;window.__scopeRequests=[];window.fetch=async(...args)=>{const enroll=String(args[0]).endsWith('/gas/billing/enroll');if(enroll)window.__scopeRequests.push(JSON.parse(args[1].body));const response=await send(...args);if(enroll&&response.ok&&!window.__scopeLost){window.__scopeLost=true;throw new TypeError('Synthetic lost enrollment reply');}return response;};})()`);
 await click(field('once'));await wait("!document.querySelector('#job-pending').hidden&&"+idle);
 await check('uncertain enrollment blocks replacement work',`document.querySelector(${JSON.stringify(field('once'))}).disabled`);
 await click('#job-retry-request');await wait("document.querySelector('#job-pending').hidden&&document.querySelector('#jobs-table').textContent.includes('Synthetic browser gas processing')&&"+idle);
 await check('enrollment retry preserves the exact request',"window.__scopeRequests.length===2&&JSON.stringify(window.__scopeRequests[0])===JSON.stringify(window.__scopeRequests[1])");
 await evaluate('window.fetch=window.__scopeFetch');
 const row="[...document.querySelectorAll('#jobs-table tbody tr')].find(r=>r.textContent.includes('Synthetic browser gas processing'))";
 await evaluate(`${row}.querySelectorAll('button').forEach(b=>{if(b.textContent==='Pause')b.click()})`);await wait(idle);
 await check('new gas processing can be paused in Jobs',`${row}.textContent.includes('paused')`);
 await evaluate(`[...${row}.querySelectorAll('button')].find(b=>b.textContent==='View lifecycle outcome').click()`);await wait(idle);
 await check('a saved scope is reported separately from executed bills',"document.body.textContent.includes('Executed bills: 0')");
 await check('gas processing fields fit the desktop viewport','document.documentElement.scrollWidth<=innerWidth');
 await command('Emulation.setDeviceMetricsOverride',{width:640,height:1100,deviceScaleFactor:1,mobile:false});
 await check('gas processing choices fit a narrow viewport','document.documentElement.scrollWidth<=innerWidth');
 await evaluate(`document.querySelector(${JSON.stringify(field('business-date'))}).closest('details').scrollIntoView({block:'start'})`);
 await shot('gas-processing-scope');
}
