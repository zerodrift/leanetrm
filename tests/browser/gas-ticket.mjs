// How the browser checks drive the physical gas deal ticket (docs/deal-ticket.md): its selectors, choices the backend lists, and the January
// terms at a synthetic point the scenarios enter.
import {idle} from './owned-browser.mjs';

export const q=JSON.stringify;
export const newTicket='#gas-ticket .deal-ticket',bookedTicket='#position-details .deal-ticket[data-mode=booked]';

export function gasTicket({evaluate,wait,set},ticket=newTicket){
 const card=(code,root=ticket)=>`${root} .ticket-card[data-side="${code}"]`;
 const field=(code,id)=>`${code===null?ticket+' .ticket-strip':card(code)} .ticket-field[data-field="${id}"]`;
 const control=(code,id)=>field(code,id)+' .ticket-control';
 // Choices use the ticket settling window: an API request may outlast the ordinary action wait.
 // A value is chosen only once the backend lists it, with field diagnostics if it never arrives.
 const choose=async(code,id,value)=>{await until(`[...document.querySelectorAll(${q(control(code,id)+' option')})].some(o=>o.value===${q(value)})`);await set(control(code,id),value);};
 const first=async(code,id)=>{await until(`document.querySelectorAll(${q(control(code,id)+' option')}).length>1`);await set(control(code,id),await evaluate(`document.querySelector(${q(control(code,id))}).options[1].value`));};
 const status=(root=ticket)=>`document.querySelector(${q(root+' .ticket-status')})`;
 const current=(root=ticket)=>`${status(root)}?.dataset.kind==='current'&&!!document.querySelector(${q(root+' .tabulator-row')})`;
 const row=(key,root=ticket)=>`(document.querySelector(${q(root+' .tabulator-row[data-row-key="'+key+'"]')})?.textContent??'')`;
 const month=(root=ticket)=>row('2021-01',root);
 const observed=`({status:${status()}?.textContent,kind:${status()}?.dataset.kind,errors:[...document.querySelectorAll(${q(ticket+' .ticket-error')})].map(e=>e.closest('[data-field]').dataset.field+': '+e.textContent),month:${month()}})`;
 // A wait that says, when it times out, what the ticket showed.
 const until=(expression,detail=observed)=>wait(expression,30000).catch(async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate(detail)));});
 const tradeTerms=async()=>{
  for(const id of ['book','legal_entity','counterparty'])await first(null,id);
  // A context that resolves a published gas setup lists it, and the person chooses it before the sides list their choices; none is chosen
  // for them. Without one the sides list their choices at once.
  const setups=`document.querySelectorAll(${q(control(null,'gas_setup')+' option')}).length>1`;
  await until(`${setups}||[...document.querySelectorAll(${q(control('physical-1','product')+' option')})].some(o=>o.value==='test_gas')`);
  if(await evaluate(setups))await first(null,'gas_setup');
 };
 // One January delivery per operating day at a synthetic point (A unless named); a quantity of null leaves it to the side's link.
 const physicalTerms=async(code,direction,quantity,{zone='synthetic_gas_zone_a',point='synthetic_gas_point_a'}={})=>{
  for(const [id,value] of [['delivery_from','2021-01-01'],['delivery_to','2021-02-01'],...quantity===null?[]:[['quantity',quantity]]])await set(control(code,id),value);
  for(const [id,value] of [['direction',direction],['product','test_gas'],['quantity_basis','per_operating_day'],['pipeline','test_pipeline_a'],['zone',zone],['delivery_point',point]])
   await choose(code,id,value);
 };
 // A payment or fee side charged on a physical side's delivery quantity at a manual price, paid on 5 February.
 const cashTerms=async(code,kind,direction,source,price)=>{
  for(const [id,value] of [['direction',direction],['source',source],['stage','delivery']])await choose(code,`${kind}_${id}`,value);
  for(const [id,value] of [['price',price],['payment_at','2021-02-05T00:00:00Z']])await set(control(code,`${kind}_${id}`),value);
 };
 const addSide=async(pill,code)=>{
  await evaluate(`[...document.querySelectorAll(${q(ticket+' .ticket-add-kind')})].find(b=>b.textContent===${q(pill)}).click()`);
  await wait(`!!document.querySelector(${q(card(code))})`);
 };
 return {ticket,card,field,control,choose,first,status,current,row,month,observed,until,tradeTerms,physicalTerms,cashTerms,addSide};
}

/** The 18 December 2020 close of the seeded workspace, which every gas scenario prices at. */
export const decemberClose=evaluate=>evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>w.initialFrameIds.includes(f.id)&&f.at.startsWith('2020-12-18')).id;})()`);

/** Opens the Blotter at the December close. */
export async function blotterAt({set,wait,click},frame){
 await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait("document.querySelector('#page-title').textContent==='Blotter'&&"+idle);
}
