import {zonedInput} from './user-time.js';
import {displayExactInstant} from './market-view.js';
import {crudeOperationBase,crudeReceipt,crudeAllocation,crudeDispatch,crudeInvoice,crudePayment,crudeSaleProfile,crudeOperationKinds,crudeRoundingPolicy,crudeRecordCorrection,crudeRecordCorrectionPolicy} from './crude-operations-model.js';
import type {RequestCommand} from './pending-command.js';
type Row=Record<string,string|number|boolean|null>;
type Detail={tradeId:string;profile:string;allocations:Row[];head:Row;version:number;canWrite:boolean;receipts:Row[];dispatches:Row[];invoices:Row[];payments:Row[];paymentReversals:Row[];invoiceVoids:Row[];sales:({trade_id:string;revision:number}&Row)[];balances:{acceptedQuantity:string;remainingQuantity:string;ownedInventoryQuantity:string;ownedQuantity:string;reservedQuantity:string;dispatchedQuantity:string;invoices:{invoiceId:string;state:string;amount:string;paid:string;open:string;dueAt:string}[];cash:{component:string;original:string;invoiced:string;paid:string;outstanding:string;uninvoiced:string}[]}};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export function setupCrudeOperations(o:{api:Api;act:(f:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;context:()=>Record<string,unknown>;actor:()=>string;zone:()=>string;onDenied:()=>void;queue:(c:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined;host:()=>HTMLElement;unsavedChanged:()=>void}){
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text)n.textContent=text;return n;};
 const shelf=document.getElementById('editor-shelf')!,root=el('section');root.id='crude-operations';root.className='editing';root.hidden=true;shelf.append(root);
 const title=el('h3','Crude delivery and cash'),status=el('p'),ledger=el('div'),form=el('form'),review=el('p');status.setAttribute('role','status');review.setAttribute('role','status');root.append(title,status,form,review,ledger);form.id='crude-operation-form';ledger.id='crude-operation-ledger';
 const fields:Record<string,HTMLInputElement|HTMLSelectElement>={};
 function field(name:string,label:string,choices?:[string,string][]){const n=choices?el('select'):el('input');n.id=`crude-operation-${name}`;if(n instanceof HTMLSelectElement)n.append(new Option('Choose…',''),...choices!.map(([v,t])=>new Option(t,v)));const l=el('label',label);l.htmlFor=n.id;const wrap=el('div');wrap.append(l,n);form.append(wrap);fields[name]=n;return n;}
 field('kind','Operation',[]);
 field('profile','Operational profile',[]);
 field('businessKey','Business reference');const effective=field('effectiveAt','Effective instant') as HTMLInputElement;effective.type='datetime-local';effective.step='1';const effectiveTime=zonedInput(effective,o.zone,o.api);field('evidence','Evidence reference');
 field('quantity','Certified / reserved / dispatched standard volume');field('disposition','Receipt disposition',[['owned_inventory','Owned inventory at retained custody'],['onward_delivery','Reserved for exact onward sale']]);
 field('sale','Booked onward sale revision',[]);field('receipt','Accepted receipt lot',[]);field('allocation','Owned inventory reservation',[]);field('dispatch','Title-transferred sale dispatch',[]);
 (field('title','Title transferred at this acceptance / dispatch') as HTMLInputElement).type='checkbox';
 field('component','Original cash component',[['consideration','Consideration'],['handling_fee','Handling fee']]);
 field('precision','Contract currency minor units',[]);field('rounding','Cumulative allocation rounding',[[crudeRoundingPolicy,'Cumulative component allocation · ties to even v1']]);
 field('invoice','Issued invoice',[]);field('amount','Partial payment amount');field('direction','Payment direction',[['pay','Pay'],['receive','Receive']]);
 field('recordPolicy','Record correction policy',[[crudeRecordCorrectionPolicy,'Correct the recorded fact · no bank refund']]);
 field('paymentRecord','Eligible payment record',[]);field('invoiceRecord','Eligible invoice record',[]);
 const correctionNote=el('p','These actions correct recorded facts. Reversing a payment record restores its invoice balance; it does not return money through a bank. Only the latest active invoice for a cash component can be voided, after its active payment records are reversed.');form.append(correctionNote);correctionNote.id='crude-record-correction-note';
 const submit=el('button','Record operation');submit.type='submit';submit.id='crude-operation-submit';const issue=el('button','Issue reviewed invoice');issue.type='button';issue.id='crude-operation-issue';issue.hidden=true;form.append(submit,issue);
 const refresh=el('button','Refresh delivery and cash');refresh.type='button';title.after(refresh);
 let detail:Detail|undefined,tradeId='',generation=0,editVersion=0,scope='',dirty=false,displayZone=o.zone(),prepared:Record<string,unknown>|undefined;
 const saleView=()=>detail?.profile===crudeSaleProfile;
 const key=()=>JSON.stringify([o.actor(),o.context()]);const mounted=()=>root.parentElement!==shelf;const val=(k:string)=>fields[k].value;
 const writable=()=>!!detail?.canWrite&&scope===key()&&!o.pending();
 function sync(){if(displayZone!==o.zone()){displayZone=o.zone();effectiveTime.refresh();editVersion++;prepared=undefined;issue.hidden=true;review.textContent='';renderLedger();}effective.parentElement!.querySelector('label')!.textContent='Effective instant · '+o.zone();for(const n of form.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))n.disabled=!writable()||o.busy();refresh.disabled=o.busy()||!!o.pending();}
 function offer(name:string,rows:[string,string][]){const n=fields[name] as HTMLSelectElement,old=n.value;n.replaceChildren(new Option('Choose…',''),...rows.map(([v,t])=>new Option(t,v)));n.value=rows.some(([v])=>v===old)?old:'';}
 const direction=(r:Row)=>String(r.direction)==='-1'?'Pay':String(r.direction)==='1'?'Receive':String(r.direction??'—');
 function choices(){
  if(!detail)return;
  const kind=val('kind'),rows=detail.receipts;
  const eligible=kind==='allocation'?rows.filter(r=>r.canAllocate===true):kind==='dispatch'?rows.filter(r=>r.canDispatch===true||detail!.allocations.some(a=>a.receipt_id===r.id&&a.canDispatch===true)):rows;
  offer('receipt',eligible.map(r=>[String(r.id),`${r.business_key} · ${r.quantity} · ${r.disposition}${r.availableQuantity===undefined?'':` · available ${r.availableQuantity}`}`]));
  offer('allocation',detail.allocations.filter(a=>a.receipt_id===val('receipt')&&a.canDispatch===true).map(a=>[String(a.id),`${a.business_key} · sale ${a.sale_trade_id} revision ${a.sale_revision} · remaining ${a.remainingQuantity}`]));
 }
 function showFields(){
  const k=val('kind'),ownedDispatch=k==='dispatch'&&detail?.receipts.find(r=>r.id===val('receipt'))?.disposition==='owned_inventory';
  const visible=new Set(['kind','profile','businessKey','effectiveAt','evidence',...(k==='receipt'?['quantity','disposition','title',...(val('disposition')==='onward_delivery'?['sale']:[])]:k==='allocation'?['receipt','quantity','sale']:k==='dispatch'?['receipt','quantity','title',...(ownedDispatch?['allocation']:[])]:k==='invoice'?[saleView()?'dispatch':'receipt','component','precision','rounding']:k==='payment'?['invoice','amount','direction']:k==='payment_reverse'?['paymentRecord','recordPolicy']:k==='invoice_void'?['invoiceRecord','recordPolicy']:[])]);
  for(const [name,n]of Object.entries(fields))n.parentElement!.hidden=!visible.has(name);
  correctionNote.hidden=!['payment_reverse','invoice_void'].includes(k);
  submit.textContent=k==='invoice'?'Preview invoice':k==='payment_reverse'?'Reverse payment record':k==='invoice_void'?'Void invoice record':'Record operation';
 }
 const changed=(event:Event)=>{
  if(['kind','receipt','allocation','sale','quantity','effectiveAt','disposition','profile'].some(name=>fields[name]===event.target))(fields.title as HTMLInputElement).checked=false;
  editVersion++;dirty=true;prepared=undefined;issue.hidden=true;review.textContent='';choices();showFields();o.unsavedChanged();
 };form.oninput=changed;form.onchange=changed;
 async function load(){prepared=undefined;issue.hidden=true;review.textContent='';const epoch=++generation,started=key();detail=undefined;ledger.replaceChildren();status.textContent='Reading delivery and cash…';sync();const result=await o.api<Detail>('crude/operations/read',{context:{...o.context(),convention:'physical_crude_fixed_volume_v1'},tradeId}).catch(error=>{if(epoch===generation&&started===key()){status.textContent='Delivery and cash could not be read. Refresh to try again.';if((error as {code?:string}).code==='access_denied')o.onDenied();}throw error;});if(epoch!==generation||started!==key())return false;crudeOperationKinds(result.profile);detail=result;scope=started;title.textContent=saleView()?'Crude sale delivery and cash':'Crude inventory, delivery and cash';offer('kind',crudeOperationKinds(result.profile));offer('profile',[[result.profile,saleView()?'Delivered 60°F volume · sale cash v1':'Certified 60°F volume · receipt and cash v1']]);status.textContent=`Revision ${result.head.revision} · operation version ${result.version} · ${result.canWrite?'Current writable view':'Read only'} · currency ${result.head.currency_code}`;
  renderLedger();
  offer('paymentRecord',result.payments.filter(r=>r.canReverse===true).map(r=>[String(r.id),`${r.business_key} · ${r.amount} · ${direction(r)}`]));
  offer('invoiceRecord',result.invoices.filter(r=>r.canVoid===true).map(r=>[String(r.id),`${r.business_key} · ${r.amount} ${r.currency_code}`]));
  offer('component',[['consideration','Consideration'],...(result.head.handling_fee===null||result.head.handling_fee===undefined?[]:[['handling_fee','Handling fee'] as [string,string]])]);
  choices();offer('dispatch',result.dispatches.map(r=>[String(r.id),`${r.business_key} · ${r.quantity} · ${displayExactInstant(String(r.effective_at),o.zone())}`]));offer('invoice',result.invoices.filter(r=>r.active===true).map(r=>[String(r.id),`${r.business_key} · ${r.amount} ${r.currency_code} · due ${displayExactInstant(String(r.due_at),o.zone())}`]));offer('sale',(result.sales??[]).map(r=>[`${r.trade_id}:${r.revision}`,`${r.trade_id} · revision ${r.revision} · ${r.quantity}`]));offer('precision',[[String(result.head.decimal_places),`${result.head.currency_code} · ${result.head.decimal_places} decimal places`]]);showFields();sync();return true;
 }
 function renderLedger(){if(!detail)return;const result=detail;
  ledger.replaceChildren();
  const table=(title:string,headers:string[],rows:string[][])=>{const section=el('section'),heading=el('h4',title),table=el('table'),head=el('tr'),body=el('tbody');table.className='reference-table';for(const h of headers)head.append(el('th',h));table.append(head);for(const row of rows){const tr=el('tr');for(const cell of row)tr.append(el('td',cell));body.append(tr);}table.append(body);section.append(heading,table);ledger.append(section);};
  const text=(r:Row,k:string)=>String(r[k]??'—');
  const b=result.balances,unit=String(result.head.quantity_unit);
  table('Certified volume at 60°F',['Measure',unit],saleView()?[['Delivered against sale',b.acceptedQuantity],['Remaining sale contract',b.remainingQuantity]]:[['Accepted',b.acceptedQuantity],['Remaining contract',b.remainingQuantity],['Available owned inventory',b.ownedInventoryQuantity],['Total owned',b.ownedQuantity],['Reserved for onward sale',b.reservedQuantity],['Dispatched',b.dispatchedQuantity]]);
  table('Contract cash · signed amounts',['Component','Original','Invoiced','Paid','Outstanding','Uninvoiced'],b.cash.map(c=>[c.component==='handling_fee'?'Handling fee':'Consideration',c.original,c.invoiced,c.paid,c.outstanding,c.uninvoiced]));
  if(!saleView())table('Accepted receipts',['Reference','Effective · '+o.zone()+'',unit,'Disposition','Evidence'],result.receipts.map(r=>[text(r,'business_key'),displayExactInstant(text(r,'effective_at'),o.zone()),text(r,'quantity'),r.disposition==='owned_inventory'?'Owned inventory':`Onward sale ${r.sale_trade_id} · revision ${r.sale_revision}`,text(r,'evidence')]));
  table('Inventory reservations',['Reference','Effective · '+o.zone()+'',unit,'Receipt','Sale revision',...(saleView()?[]:['Remaining to dispatch'])],result.allocations.map(r=>[text(r,'business_key'),displayExactInstant(text(r,'effective_at'),o.zone()),text(r,'quantity'),text(r,'receipt_id'),`${r.sale_trade_id} · revision ${r.sale_revision}`,...(saleView()?[]:[text(r,'remainingQuantity')])]));
  table('Title-transferred dispatches',['Reference','Effective · '+o.zone()+'',unit,'Receipt','Evidence'],result.dispatches.map(r=>[text(r,'business_key'),displayExactInstant(text(r,'effective_at'),o.zone()),text(r,'quantity'),text(r,'receipt_id'),text(r,'evidence')]));
  table('Issued invoices',['Reference','Record state','Component','Cash direction','Amount','Currency','Due · '+o.zone()+''],result.invoices.map(r=>[text(r,'business_key'),r.active===true?'Active':r.active===false?'Voided':'Unavailable',r.slot===1?'Consideration':'Handling fee',direction(r),text(r,'amount'),text(r,'currency_code'),displayExactInstant(text(r,'due_at'),o.zone())]));
  table('Invoice balances',['Invoice','State','Amount','Paid','Open','Due · '+o.zone()+''],b.invoices.map(i=>[i.invoiceId,i.state,i.amount,i.paid,i.open,displayExactInstant(i.dueAt,o.zone())]));
  table('Payments',['Reference','Record state','Effective · '+o.zone()+'','Cash direction','Amount','Invoice','Evidence'],result.payments.map(r=>[text(r,'business_key'),r.active===true?'Active':r.active===false?'Reversed':'Unavailable',displayExactInstant(text(r,'effective_at'),o.zone()),direction(r),text(r,'amount'),text(r,'invoice_id'),text(r,'evidence')]));
  table('Payment record reversals · no bank refund',['Reference','Effective · '+o.zone()+'','Original payment','Policy','Evidence'],result.paymentReversals.map(r=>[text(r,'business_key'),displayExactInstant(text(r,'effective_at'),o.zone()),text(r,'payment_id'),text(r,'reversal_policy'),text(r,'evidence')]));
  table('Invoice record voids',['Reference','Effective · '+o.zone()+'','Original invoice','Policy','Evidence'],result.invoiceVoids.map(r=>[text(r,'business_key'),displayExactInstant(text(r,'effective_at'),o.zone()),text(r,'invoice_id'),text(r,'reversal_policy'),text(r,'evidence')]));
  offer('dispatch',result.dispatches.map(r=>[String(r.id),`${r.business_key} · ${r.quantity} · ${displayExactInstant(String(r.effective_at),o.zone())}`]));offer('invoice',result.invoices.filter(r=>r.active===true).map(r=>[String(r.id),`${r.business_key} · ${r.amount} ${r.currency_code} · due ${displayExactInstant(String(r.due_at),o.zone())}`]));
 }
 async function send(path:string,body:Record<string,unknown>){if(!writable())throw Error('Refresh the current writable lifecycle view.');await o.queue({path:`crude/operations/${path}`,operationId:String(body.operationId),body});}
 async function record(){
  if(!writable()||!detail)throw Error('Refresh the current writable lifecycle view.');
  const epoch=generation,edited=editVersion,started=key(),zone=o.zone(),effectiveAt=await effectiveTime.instant();
  if(epoch!==generation||edited!==editVersion||started!==key()||zone!==o.zone()||!writable()||!detail)throw Error('The operation or display timezone changed. Resolve the current terms again.');
  const base=crudeOperationBase(o.context(),{tradeId,revision:Number(detail.head.revision),version:detail.version},
   {profile:val('profile'),businessKey:val('businessKey'),effectiveAt,evidence:val('evidence')});
  if(base.profile!==detail.profile||!crudeOperationKinds(detail.profile).some(([kind])=>kind===val('kind')))throw Error('Choose an operation and profile for this trade.');
  switch(val('kind')){
   case 'receipt':
    await send('receipt',crudeReceipt(base,{quantity:val('quantity'),titleTransferred:(fields.title as HTMLInputElement).checked,disposition:val('disposition')},detail.sales.find(s=>`${s.trade_id}:${s.revision}`===val('sale'))));return;
   case 'allocation':
    await send('allocation',crudeAllocation(base,{receiptId:val('receipt'),quantity:val('quantity')},detail.sales.find(s=>`${s.trade_id}:${s.revision}`===val('sale'))));return;
   case 'dispatch':{
    const receipt=detail.receipts.find(r=>r.id===val('receipt'));
    if(!receipt)throw Error('Choose an accepted receipt lot.');
    await send('dispatch',crudeDispatch(base,{receiptId:val('receipt'),quantity:val('quantity'),titleTransferred:(fields.title as HTMLInputElement).checked,...(receipt.disposition==='owned_inventory'?{allocationId:val('allocation')}:{})}));return;
   }
   case 'payment':
    await send('payment',crudePayment(base,{invoiceId:val('invoice'),amount:val('amount'),direction:val('direction')}));return;
   case 'payment_reverse':case 'invoice_void':{
    const payment=val('kind')==='payment_reverse',record=(payment?detail.payments:detail.invoices).find(r=>r.id===val(payment?'paymentRecord':'invoiceRecord'));
    const body=crudeRecordCorrection(base,payment?'payment':'invoice',val('recordPolicy'),record?{id:String(record.id),canReverse:record.canReverse===true,canVoid:record.canVoid===true}:undefined);
    await send(payment?'payment/reverse':'invoice/void',body);return;
   }
   case 'invoice':{
    const body=crudeInvoice(base,{...(saleView()?{dispatchId:val('dispatch')}:{receiptId:val('receipt')}),component:val('component'),decimalPlaces:val('precision'),roundingPolicy:val('rounding')}),epoch=generation,edited=editVersion,started=key();
    const p=await o.api<Record<string,unknown>>('crude/operations/invoice/preview',body);
    if(epoch!==generation||edited!==editVersion||started!==key())return;
    prepared={...body,previewHash:p.previewHash};review.textContent=`${p.direction} ${p.amount} ${p.currency} · exact allocation ${p.exactAmount} · due ${displayExactInstant(String(p.dueAt),o.zone())}`;issue.hidden=false;return;
   }
   default:throw Error('Choose an operation.');
  }
 }
 form.onsubmit=e=>{e.preventDefault();void o.act(record);};
 issue.onclick=()=>void o.act(async()=>{if(!prepared)throw Error('Preview the invoice first.');await send('invoice/issue',prepared);});refresh.onclick=()=>void o.act(async()=>{await load();});
 function reset(){generation++;detail=undefined;prepared=undefined;tradeId='';dirty=false;ledger.replaceChildren();status.textContent='';form.reset();effectiveTime.clear();for(const name of ['kind','profile','receipt','allocation','dispatch','invoice','sale','precision','paymentRecord','invoiceRecord'])offer(name,[]);issue.hidden=true;review.textContent='';root.hidden=true;shelf.append(root);showFields();}
 showFields();return{async open(id:string){reset();tradeId=id;if(!await load())return false;o.host().append(root);root.hidden=false;sync();return true;},async reload(){if(!mounted())return;dirty=false;prepared=undefined;form.reset();effectiveTime.clear();issue.hidden=true;showFields();await load();o.unsavedChanged();},reset,sync,shown:mounted,unsaved:()=>mounted()&&dirty,refresh(){generation++;detail=undefined;prepared=undefined;issue.hidden=true;review.textContent='';ledger.replaceChildren();status.textContent='The context changed. Refresh delivery and cash.';sync();}};
}
