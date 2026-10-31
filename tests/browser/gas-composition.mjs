import {run as gasDailyQuantities} from './gas-daily-quantities.mjs';
export async function run(actions){
 const {command,evaluate,wait,click,set,submit,persona,check,idle,url,shot}=actions;
 const field=(n,key)=>`#gc-physical [data-physical="${n}"] [data-field="${key}"]`;
 const cash=(n,key)=>`#gc-cash [data-cash="${n}"] [data-field="${key}"]`;
 await persona('trader');
 const frame=await evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>w.initialFrameIds.includes(f.id)&&f.at.startsWith('2020-12-18')).id;})()`);
 await set('#snapshot',frame);await wait(idle);await click('#nav-gas');await wait("document.querySelector('#gc-book').options.length>1&&"+idle);
 for(const id of ['gc-book','gc-entity','gc-party'])await set('#'+id,await evaluate(`document.querySelector('#${id}').options[1].value`));
 await set('#gc-policy','no_discounting');
 const physical=async(n,zone,point,direction,mode)=>{
  await set(field(n,'direction'),direction);await set(field(n,'product'),'test_gas');await set(field(n,'pipeline'),'test_pipeline_a');await set(field(n,'zone'),zone);await set(field(n,'deliveryPoint'),point);
  await set(field(n,'deliveryFrom'),'2021-01-01');await set(field(n,'deliveryTo'),'2021-02-01');await set(field(n,'quantityBasis'),'period_total_equal_days');await set(field(n,'mode'),mode);
 };
 await physical(0,'synthetic_gas_zone_a','synthetic_gas_point_a','receive','independent');await set(field(0,'quantity'),'1000');await click('#gc-add-physical');
 await physical(1,'synthetic_gas_zone_b','synthetic_gas_point_b','deliver','linked');await set(field(1,'source'),'gas_1');await set(field(1,'formula'),'gross_less_fraction');await set(field(1,'fraction'),'0.02');
 await check('composition puts independent directions and locations on each physical side',"document.querySelectorAll('#gc-physical [data-physical]').length===2&&!document.querySelector('#gc-form > fieldset [data-field=direction]')");
 await click('#gc-add-cash');await set(cash(0,'role'),'fee');await set(cash(0,'direction'),'pay');await set(cash(0,'source'),'gas_2');await set(cash(0,'stage'),'delivery');await set(cash(0,'tariffCode'),'manual');await set(cash(0,'price'),'0.1');await set(cash(0,'paymentAt'),'2021-02-02T00:00');
 await check('all active composition terms are complete before preview',"document.querySelector('#gc-form').checkValidity()","[...document.querySelector('#gc-form').elements].filter(e=>e.willValidate&&!e.validity.valid).map(e=>({id:e.id,field:e.dataset.field,value:e.value,disabled:e.disabled,reason:e.validationMessage}))");
 await submit('#gc-form');await wait(idle);
 await check('two-point gas preview reconciles 1000 receipt, 980 delivery and 20 fuel',"document.querySelector('#gc-total').textContent==='Present value: 64 USD'&&document.querySelectorAll('#gc-links tbody tr').length===1&&document.querySelector('#gc-links').textContent.includes('980')&&document.querySelector('#gc-links').textContent.includes('1000')", "({error:document.querySelector('#error').textContent,value:document.querySelector('#gc-total').textContent})");
 await set(cash(0,'stage'),'receipt');await check('changing a fee quantity link invalidates the reviewed booking',"document.querySelector('#gc-book-submit').disabled&&document.querySelector('#gc-result').hidden");
 await submit('#gc-form');await check('a fee on receipt rather than delivery changes PV to62',"document.querySelector('#gc-total').textContent==='Present value: 62 USD'");
 await set(cash(0,'stage'),'delivery');await submit('#gc-form');
 await evaluate(`{const original=window.fetch;window.fetch=async(...args)=>{const response=await original(...args);if(String(args[0]).endsWith('/gas/composition/book')&&response.ok)throw new TypeError('Synthetic lost composition booking response');return response;};}`);
 await click('#gc-book-submit');await check('an uncertain composition booking retains every side and link',"!document.querySelector('#gc-pending').hidden&&document.querySelector('#gc-policy').disabled");
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await set('#snapshot',frame);await wait(idle);await click('#nav-gas');await wait("!document.querySelector('#gc-pending').hidden&&"+idle);await click('#gc-check');
 await check('recovery reopens both retained physical schedules and the original PV',"document.querySelector('#gc-pending').hidden&&document.querySelector('#gc-total').textContent==='Present value: 64 USD'&&document.querySelectorAll('#gc-schedules tbody tr').length===62");
 await check('composed bookings have a durable directory entry',"document.querySelectorAll('#gc-directory button').length>=1");
 await check('the multiple-side ticket fits its viewport',"document.documentElement.scrollWidth<=innerWidth");await shot('gas-composition');
 await gasDailyQuantities(actions);
}
