import {componentQuantity} from './ppa-view.js';
import {friendly} from './market-view.js';
import type {Catalog,Member,Quantity,Side,Template,DataRow} from './ppa-contract.js';
import type {PaymentInput} from './swap-contract.js';
export const code=(r:DataRow,key='code')=>String(r[key]??'');
export const choices=(rows:DataRow[])=>rows.map(r=>({value:code(r),label:code(r,'name')||friendly(code(r))}));
export function selectChoices(node:HTMLSelectElement,rows:{value:string;label:string}[],value=''){
 node.replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(r.label,r.value)));node.value=value;
}
export function componentEditor(host:HTMLElement,catalog:Catalog,template:Template['members'][number],index:number,changed:()=>void,initial?:Member){
 const prefix=`ppa-component-${index}`;
 host.className='card ppa-component';host.dataset.role=template.role;
 // Only static labels and generated numeric IDs enter markup; reference values use text/Option nodes.
 host.innerHTML=`<h3></h3><p class="ppa-link field-help"></p><div class="schedule-fields">
 <label data-section="quantity-choice">Amendment quantity source<select data-field="quantity-choice"></select></label><details class="ppa-retained-quantity" hidden><summary>Retained component quantity</summary><pre class="ppa-evidence"></pre></details><label>Component type<select data-field="side-kind"></select></label><label>Direction<select data-field="coefficient"><option value="">Choose…</option><option value="1">Receive</option><option value="-1">Pay</option></select></label>
 <label data-section="location">Valuation location<select data-field="location"></select></label>
 <label data-section="fixed">Fixed price convention<select data-field="price-kind"></select></label>
 <label data-section="priced">Price quantity specification<select data-field="price-quantity"></select></label>
 <label data-section="priced">Quote multiplier<input data-field="multiplier" inputmode="decimal"></label>
 <label data-section="flat">Contract price<input data-field="price" inputmode="decimal"></label>
 <label data-section="escalated">Initial price<input data-field="initial-price" inputmode="decimal"></label>
 <label data-section="escalated">Annual escalation rate<input data-field="rate" inputmode="decimal"></label>
 <label data-section="escalated">Anniversary base date<input data-field="base-date" type="date"></label>
 <label data-section="escalated">Compounding convention<select data-field="basis"></select></label>
 <label data-section="stepped">Price steps · one operating date,price per line<textarea data-field="steps" rows="3" placeholder="2031-11-01,40"></textarea></label>
 <label data-section="indexed">Price curve<select data-field="curve"></select></label>
 <label data-section="indexed">Index multiplier<input data-field="index-multiplier" inputmode="decimal"></label>
 <label data-section="indexed">Index adder<input data-field="adder" inputmode="decimal"></label>
 <label data-section="attribute">Attribute quantity specification<select data-field="attribute-quantity"></select></label>
 <label data-section="certificate">Certificates per energy unit<input data-field="conversion" inputmode="decimal"></label>
 <label data-section="certificate">Eligibility<select data-field="eligibility"></select></label>
 <label data-section="certificate">Holding policy<select data-field="holding"></select></label>
 <label data-section="certificate">Disposition<select data-field="disposition"></select></label>
 <label data-section="capacity">Capacity entitlement rule<select data-field="capacity-rule"></select></label>
 </div><h4>Payment dates · original delivery periods</h4><div class="schedule-fields">
 <label>Frequency<select data-field="frequency"></select></label><label>Date rule<select data-field="payment-rule"></select></label>
 <label>Days / day of following month<input data-field="payment-number" type="number" min="0" max="366"></label>
 <label data-section="lag">Day basis<select data-field="day-basis"></select></label>
 <label data-section="next">Short month<select data-field="short-month"></select></label>
 <label>Adjustment<select data-field="adjustment"></select></label>
 <label data-section="calendar">Payment calendar<select data-field="payment-calendar"></select></label></div>`;
 const field=(name:string)=>host.querySelector<HTMLInputElement|HTMLSelectElement|HTMLTextAreaElement>(`[data-field="${name}"]`)!;
 const value=(name:string)=>{const v=field(name).value;if(!v)throw Error(`Complete ${friendly(template.role)}: ${friendly(name)}.`);return v;};
 host.querySelector('h3')!.textContent=friendly(template.role);
 host.querySelector('.ppa-link')!.textContent=template.linkedSlots.length?`Quantity follows ${template.linkedSlots.map(s=>friendly(s.role)+' '+s.ordinal).join(', ')}. Positive quantities retain their units; direction signs the value.`:'Uses the explicitly selected delivery quantity. Direction signs the value.';
 host.querySelectorAll<HTMLInputElement>('input,select,textarea').forEach(n=>{n.id=prefix+'-'+n.dataset.field;n.autocomplete='off';});
 const pick=(name:string,rows:{value:string;label:string}[])=>selectChoices(field(name) as HTMLSelectElement,rows);
 const enums=(name:string,values:string[])=>pick(name,values.map(value=>({value,label:friendly(value)})));
 pick('location',choices(catalog.references.locations));pick('price-quantity',choices(catalog.references.quantitySpecs));pick('attribute-quantity',choices(catalog.references.quantitySpecs));
 enums('quantity-choice',['retained','selected']);field('quantity-choice').value=initial?'retained':'selected';if(initial&&!template.linkedSlots.length){host.querySelector<HTMLElement>('.ppa-retained-quantity')!.hidden=false;host.querySelector('.ppa-retained-quantity pre')!.textContent=JSON.stringify(initial.side.quantity,null,2);}
 enums('side-kind',template.sideKinds);field('side-kind').value=initial?.side.kind??(template.sideKinds.length===1?template.sideKinds[0]:'');
 enums('price-kind',catalog.priceKinds.filter(k=>k!=='indexed'));pick('curve',choices(catalog.definitionsMetadata.priceCurves));
 const policies=catalog.authoringPolicies;
 for(const [name,values] of [['basis',policies.escalationBases],['eligibility',policies.certificateEligibility],['holding',policies.certificateHolding],['disposition',policies.certificateDisposition],['capacity-rule',policies.capacityRules]] as const)enums(name,values);
 const pay=catalog.paymentOptions;
 for(const [name,rows] of [['frequency',pay.frequencies],['payment-rule',pay.rules],['day-basis',pay.dayBases],['short-month',pay.shortMonths],['adjustment',pay.adjustments]] as const)pick(name,rows);
 pick('payment-calendar',choices(catalog.references.calendars));
 function sync(busy=false){
  const kind=field('side-kind').value,price=field('price-kind').value,rule=field('payment-rule').value;
  const active:Record<string,boolean>={'quantity-choice':!!initial&&!template.linkedSlots.length,location:kind==='energy',fixed:kind==='fixed',priced:kind==='fixed'||kind==='indexed',flat:kind==='fixed'&&price==='flat',escalated:kind==='fixed'&&price==='escalated',stepped:kind==='fixed'&&price==='stepped',indexed:kind==='indexed',attribute:kind==='certificate'||kind==='capacity',certificate:kind==='certificate',capacity:kind==='capacity',lag:rule==='days_after_period_end',next:rule==='day_of_following_month',calendar:!!field('adjustment').value&&(field('adjustment').value!=='unadjusted'||rule==='days_after_period_end'&&field('day-basis').value==='business_days')};
  for(const label of host.querySelectorAll<HTMLElement>('[data-section]')){label.hidden=!active[label.dataset.section!];label.querySelectorAll<HTMLInputElement>('input,select,textarea').forEach(n=>n.disabled=busy||!!label.hidden);}
 }
 function read(quantity:Quantity):Member{
  const rule=value('payment-rule'),lag=rule==='days_after_period_end',adjustment=value('adjustment'),dayBasis=lag?value('day-basis'):null;
  const payment={frequency:value('frequency'),rule,days:lag?Number(value('payment-number')):null,dayOfMonth:lag?null:Number(value('payment-number')),dayBasis,shortMonth:lag?null:value('short-month'),adjustment,calendar:adjustment!=='unadjusted'||dayBasis==='business_days'?value('payment-calendar'):null,implementation:pay.implementation} as PaymentInput;
  const kind=value('side-kind'),side:Side={kind,quantity:componentQuantity<Quantity>(template.linkedSlots.length?{kind:'slots',slots:template.linkedSlots,sources:[],assets:[],stage:null,capacity:null,energy:null,hours:null}:quantity,initial?.side.quantity,initial?value('quantity-choice'):'selected'),paymentRule:payment,location:kind==='energy'?value('location'):null,fixedPrice:null,indexPrice:null,attribute:null};
  if(kind==='fixed'){
   const mode=value('price-kind');side.fixedPrice={kind:mode,quantity:value('price-quantity'),multiplier:value('multiplier'),price:mode==='stepped'?null:value(mode==='escalated'?'initial-price':'price'),rate:mode==='escalated'?value('rate'):null,baseDate:mode==='escalated'?value('base-date'):null,basis:mode==='escalated'?value('basis'):null,steps:mode==='stepped'?value('steps').split('\n').filter(Boolean).map(line=>{const parts=line.split(',');if(parts.length!==2)throw Error('Write each price step as operating date,price.');return{from:parts[0].trim(),price:parts[1].trim()};}):[]};
  }
  if(kind==='indexed')side.indexPrice={curve:value('curve'),quantity:value('price-quantity'),multiplier:value('multiplier'),indexMultiplier:value('index-multiplier'),adder:value('adder')};
  if(kind==='certificate'||kind==='capacity')side.attribute={quantity:value('attribute-quantity'),conversion:kind==='certificate'?value('conversion'):null,eligibility:kind==='certificate'?value('eligibility'):null,holding:kind==='certificate'?value('holding'):null,disposition:kind==='certificate'?value('disposition'):null,rule:kind==='capacity'?value('capacity-rule'):null};
  return{role:template.role,ordinal:template.ordinal,coefficient:value('coefficient'),side};
 }
 if(initial){
  const s=initial.side,p=s.paymentRule,f=s.fixedPrice,i=s.indexPrice,a=s.attribute;
  const values:Record<string,unknown>={coefficient:initial.coefficient,location:s.location,'price-kind':f?.kind,'price-quantity':f?.quantity??i?.quantity,multiplier:f?.multiplier??i?.multiplier,price:f?.price,'initial-price':f?.price,rate:f?.rate,'base-date':f?.baseDate,basis:f?.basis,steps:f?.steps.map(p=>p.from+','+p.price).join('\n'),curve:i?.curve,'index-multiplier':i?.indexMultiplier,adder:i?.adder,'attribute-quantity':a?.quantity,conversion:a?.conversion,eligibility:a?.eligibility,holding:a?.holding,disposition:a?.disposition,'capacity-rule':a?.rule,frequency:p.frequency,'payment-rule':p.rule,'payment-number':p.days??p.dayOfMonth,'day-basis':p.dayBasis,'short-month':p.shortMonth,adjustment:p.adjustment,'payment-calendar':p.calendar};
  for(const [name,v] of Object.entries(values))field(name).value=v==null?'':String(v);
 }
 host.addEventListener('input',()=>{changed();sync();});host.addEventListener('change',()=>{changed();sync();});sync();
 return{read,sync,template};
}
