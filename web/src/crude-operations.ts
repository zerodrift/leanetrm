import {crudeOperationBase,crudeReceipt,crudeDispatch,crudeInvoice,crudePayment,crudeReceiptProfile,crudeRoundingPolicy,crudeRecordCorrection,crudeRecordCorrectionPolicy} from './crude-operations-model.js';
import type {RequestCommand} from './pending-command.js';
type Row=Record<string,string|number|boolean|null>;
type Detail={tradeId:string;head:Row;version:number;canWrite:boolean;receipts:Row[];dispatches:Row[];invoices:Row[];payments:Row[];paymentReversals:Row[];invoiceVoids:Row[];sales:({trade_id:string;revision:number}&Row)[];balances:{acceptedQuantity:string;remainingQuantity:string;ownedInventoryQuantity:string;ownedQuantity:string;reservedQuantity:string;dispatchedQuantity:string;invoices:{invoiceId:string;state:string;amount:string;paid:string;open:string;dueAt:string}[];cash:{component:string;original:string;invoiced:string;paid:string;outstanding:string;uninvoiced:string}[]}};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export function setupCrudeOperations(o:{api:Api;act:(f:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;context:()=>Record<string,unknown>;actor:()=>string;onDenied:()=>void;queue:(c:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined;host:()=>HTMLElement;unsavedChanged:()=>void}){
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text)n.textContent=text;return n;};
 const shelf=document.getElementById('editor-shelf')!,root=el('section');root.id='crude-operations';root.className='editing';root.hidden=true;shelf.append(root);
 const title=el('h3','Crude delivery and cash'),status=el('p'),ledger=el('div'),form=el('form'),review=el('p');status.setAttribute('role','status');review.setAttribute('role','status');root.append(title,status,form,review,ledger);form.id='crude-operation-form';ledger.id='crude-operation-ledger';
 const fields:Record<string,HTMLInputElement|HTMLSelectElement>={};
 function field(name:string,label:string,choices?:[string,string][]){const n=choices?el('select'):el('input');n.id=`crude-operation-${name}`;if(n instanceof HTMLSelectElement)n.append(new Option('Choose…',''),...choices!.map(([v,t])=>new Option(t,v)));const l=el('label',label);l.htmlFor=n.id;const wrap=el('div');wrap.append(l,n);form.append(wrap);fields[name]=n;return n;}
 field('kind','Operation',[['receipt','Accept certified receipt'],['dispatch','Record onward dispatch'],['invoice','Invoice accepted receipt'],['payment','Record invoice payment'],['payment_reverse','Reverse payment record'],['invoice_void','Void invoice record']]);
 field('profile','Acceptance profile',[[crudeReceiptProfile,'Certified 60°F volume · receipt and cash v1']]);
 field('businessKey','Business reference');field('effectiveAt','Effective instant · UTC (YYYY-MM-DDTHH:mm:ssZ)');field('evidence','Evidence reference');
 field('quantity','Certified / dispatched standard volume');field('disposition','Receipt disposition',[['owned_inventory','Owned inventory at retained custody'],['onward_delivery','Reserved for exact onward sale']]);
 field('sale','Booked onward sale revision',[]);field('receipt','Accepted receipt',[]);
 (field('title','Title transferred at this acceptance / dispatch') as HTMLInputElement).type='checkbox';
 field('component','Original cash component',[['consideration','Consideration'],['handling_fee','Handling fee']]);
 field('precision','Contract currency minor units',[]);field('rounding','Cumulative allocation rounding',[[crudeRoundingPolicy,'Cumulative receipt allocation · ties to even v1']]);
 field('invoice','Issued invoice',[]);field('amount','Partial payment amount');field('direction','Payment direction',[['pay','Pay'],['receive','Receive']]);
 field('recordPolicy','Record correction policy',[[crudeRecordCorrectionPolicy,'Correct the recorded fact · no bank refund']]);
 field('paymentRecord','Eligible payment record',[]);field('invoiceRecord','Eligible invoice record',[]);
 const correctionNote=el('p','These actions correct recorded facts. Reversing a payment record restores its invoice balance; it does not return money through a bank. Only the latest active invoice for a cash component can be voided, after its active payment records are reversed.');form.append(correctionNote);correctionNote.id='crude-record-correction-note';
 const submit=el('button','Record operation');submit.type='submit';submit.id='crude-operation-submit';const issue=el('button','Issue reviewed invoice');issue.type='button';issue.id='crude-operation-issue';issue.hidden=true;form.append(submit,issue);
 const refresh=el('button','Refresh delivery and cash');refresh.type='button';title.after(refresh);
 let detail:Detail|undefined,tradeId='',generation=0,editVersion=0,scope='',dirty=false,prepared:Record<string,unknown>|undefined;
 const key=()=>JSON.stringify([o.actor(),o.context()]);const mounted=()=>root.parentElement!==shelf;const val=(k:string)=>fields[k].value;
 const writable=()=>!!detail?.canWrite&&scope===key()&&!o.pending();
 function sync(){for(const n of form.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))n.disabled=!writable()||o.busy();refresh.disabled=o.busy()||!!o.pending();}
 function offer(name:string,rows:[string,string][]){const n=fields[name] as HTMLSelectElement,old=n.value;n.replaceChildren(new Option('Choose…',''),...rows.map(([v,t])=>new Option(t,v)));n.value=rows.some(([v])=>v===old)?old:'';}
 function showFields(){const k=val('kind'),visible=new Set(['kind','profile','businessKey','effectiveAt','evidence',...(k==='receipt'?['quantity','disposition','title',...(val('disposition')==='onward_delivery'?['sale']:[])]:k==='dispatch'?['receipt','quantity','title']:k==='invoice'?['receipt','component','precision','rounding']:k==='payment'?['invoice','amount','direction']:k==='payment_reverse'?['paymentRecord','recordPolicy']:k==='invoice_void'?['invoiceRecord','recordPolicy']:[])]);for(const [name,n]of Object.entries(fields))n.parentElement!.hidden=!visible.has(name);correctionNote.hidden=!['payment_reverse','invoice_void'].includes(k);submit.textContent=k==='invoice'?'Preview invoice':k==='payment_reverse'?'Reverse payment record':k==='invoice_void'?'Void invoice record':'Record operation';}
 const changed=()=>{editVersion++;dirty=true;prepared=undefined;issue.hidden=true;review.textContent='';showFields();o.unsavedChanged();};form.oninput=changed;form.onchange=changed;
 async function load(){prepared=undefined;issue.hidden=true;review.textContent='';const epoch=++generation,started=key();detail=undefined;ledger.replaceChildren();status.textContent='Reading delivery and cash…';sync();const result=await o.api<Detail>('crude/operations/read',{context:{...o.context(),convention:'physical_crude_fixed_volume_v1'},tradeId}).catch(error=>{if(epoch===generation&&started===key()){status.textContent='Delivery and cash could not be read. Refresh to try again.';if((error as {code?:string}).code==='access_denied')o.onDenied();}throw error;});if(epoch!==generation||started!==key())return false;detail=result;scope=started;status.textContent=`Revision ${result.head.revision} · operation version ${result.version} · ${result.canWrite?'Current writable view':'Read only'} · currency ${result.head.currency_code}`;
  ledger.replaceChildren();
  const table=(title:string,headers:string[],rows:string[][])=>{const section=el('section'),heading=el('h4',title),table=el('table'),head=el('tr'),body=el('tbody');table.className='reference-table';for(const h of headers)head.append(el('th',h));table.append(head);for(const row of rows){const tr=el('tr');for(const cell of row)tr.append(el('td',cell));body.append(tr);}table.append(body);section.append(heading,table);ledger.append(section);};
  const text=(r:Row,k:string)=>String(r[k]??'—');
  const direction=(r:Row)=>String(r.direction)==='-1'?'Pay':String(r.direction)==='1'?'Receive':text(r,'direction');
  const b=result.balances,unit=String(result.head.quantity_unit);
  table('Certified volume at 60°F',['Measure',unit],[['Accepted',b.acceptedQuantity],['Remaining contract',b.remainingQuantity],['Owned at retained custody',b.ownedInventoryQuantity],['Total owned',b.ownedQuantity],['Reserved for onward sale',b.reservedQuantity],['Dispatched',b.dispatchedQuantity]]);
  table('Contract cash · signed amounts',['Component','Original','Invoiced','Paid','Outstanding','Uninvoiced'],b.cash.map(c=>[c.component==='handling_fee'?'Handling fee':'Consideration',c.original,c.invoiced,c.paid,c.outstanding,c.uninvoiced]));
  table('Accepted receipts',['Reference','Effective UTC',unit,'Disposition','Evidence'],result.receipts.map(r=>[text(r,'business_key'),text(r,'effective_at'),text(r,'quantity'),r.disposition==='owned_inventory'?'Owned inventory':`Onward sale ${r.sale_trade_id} · revision ${r.sale_revision}`,text(r,'evidence')]));
  table('Title-transferred dispatches',['Reference','Effective UTC',unit,'Receipt','Evidence'],result.dispatches.map(r=>[text(r,'business_key'),text(r,'effective_at'),text(r,'quantity'),text(r,'receipt_id'),text(r,'evidence')]));
  table('Issued invoices',['Reference','Record state','Component','Cash direction','Amount','Currency','Due UTC'],result.invoices.map(r=>[text(r,'business_key'),r.active===true?'Active':r.active===false?'Voided':'Unavailable',r.slot===1?'Consideration':'Handling fee',direction(r),text(r,'amount'),text(r,'currency_code'),text(r,'due_at')]));
  table('Invoice balances',['Invoice','State','Amount','Paid','Open','Due UTC'],b.invoices.map(i=>[i.invoiceId,i.state,i.amount,i.paid,i.open,i.dueAt]));
  table('Payments',['Reference','Record state','Effective UTC','Cash direction','Amount','Invoice','Evidence'],result.payments.map(r=>[text(r,'business_key'),r.active===true?'Active':r.active===false?'Reversed':'Unavailable',text(r,'effective_at'),direction(r),text(r,'amount'),text(r,'invoice_id'),text(r,'evidence')]));
  table('Payment record reversals · no bank refund',['Reference','Effective UTC','Original payment','Policy','Evidence'],result.paymentReversals.map(r=>[text(r,'business_key'),text(r,'effective_at'),text(r,'payment_id'),text(r,'reversal_policy'),text(r,'evidence')]));
  table('Invoice record voids',['Reference','Effective UTC','Original invoice','Policy','Evidence'],result.invoiceVoids.map(r=>[text(r,'business_key'),text(r,'effective_at'),text(r,'invoice_id'),text(r,'reversal_policy'),text(r,'evidence')]));
  offer('paymentRecord',result.payments.filter(r=>r.canReverse===true).map(r=>[String(r.id),`${r.business_key} · ${r.amount} · ${direction(r)}`]));
  offer('invoiceRecord',result.invoices.filter(r=>r.canVoid===true).map(r=>[String(r.id),`${r.business_key} · ${r.amount} ${r.currency_code}`]));
  offer('receipt',result.receipts.map(r=>[String(r.id),`${r.business_key} · ${r.quantity} · ${r.disposition}`]));offer('invoice',result.invoices.filter(r=>r.active===true).map(r=>[String(r.id),`${r.business_key} · ${r.amount} ${r.currency_code} · due ${r.due_at}`]));offer('sale',(result.sales??[]).map(r=>[`${r.trade_id}:${r.revision}`,`${r.trade_id} · revision ${r.revision} · ${r.quantity}`]));offer('precision',[[String(result.head.decimal_places),`${result.head.currency_code} · ${result.head.decimal_places} decimal places`]]);sync();return true;
 }
 async function send(path:string,body:Record<string,unknown>){if(!writable())throw Error('Refresh the current writable lifecycle view.');await o.queue({path:`crude/operations/${path}`,operationId:String(body.operationId),body});}
 async function record(){
  if(!writable()||!detail)throw Error('Refresh the current writable lifecycle view.');
  const base=crudeOperationBase(o.context(),{tradeId,revision:Number(detail.head.revision),version:detail.version},
   {profile:val('profile'),businessKey:val('businessKey'),effectiveAt:val('effectiveAt'),evidence:val('evidence')});
  switch(val('kind')){
   case 'receipt':
    await send('receipt',crudeReceipt(base,{quantity:val('quantity'),titleTransferred:(fields.title as HTMLInputElement).checked,disposition:val('disposition')},detail.sales.find(s=>`${s.trade_id}:${s.revision}`===val('sale'))));return;
   case 'dispatch':
    await send('dispatch',crudeDispatch(base,{receiptId:val('receipt'),quantity:val('quantity'),titleTransferred:(fields.title as HTMLInputElement).checked}));return;
   case 'payment':
    await send('payment',crudePayment(base,{invoiceId:val('invoice'),amount:val('amount'),direction:val('direction')}));return;
   case 'payment_reverse':case 'invoice_void':{
    const payment=val('kind')==='payment_reverse',record=(payment?detail.payments:detail.invoices).find(r=>r.id===val(payment?'paymentRecord':'invoiceRecord'));
    const body=crudeRecordCorrection(base,payment?'payment':'invoice',val('recordPolicy'),record?{id:String(record.id),canReverse:record.canReverse===true,canVoid:record.canVoid===true}:undefined);
    await send(payment?'payment/reverse':'invoice/void',body);return;
   }
   case 'invoice':{
    const body=crudeInvoice(base,{receiptId:val('receipt'),component:val('component'),decimalPlaces:val('precision'),roundingPolicy:val('rounding')}),epoch=generation,edited=editVersion,started=key();
    const p=await o.api<Record<string,unknown>>('crude/operations/invoice/preview',body);
    if(epoch!==generation||edited!==editVersion||started!==key())return;
    prepared={...body,previewHash:p.previewHash};review.textContent=`${p.direction} ${p.amount} ${p.currency} · exact allocation ${p.exactAmount} · due ${p.dueAt}`;issue.hidden=false;return;
   }
   default:throw Error('Choose an operation.');
  }
 }
 form.onsubmit=e=>{e.preventDefault();void o.act(record);};
 issue.onclick=()=>void o.act(async()=>{if(!prepared)throw Error('Preview the invoice first.');await send('invoice/issue',prepared);});refresh.onclick=()=>void o.act(async()=>{await load();});
 function reset(){generation++;detail=undefined;prepared=undefined;tradeId='';dirty=false;ledger.replaceChildren();status.textContent='';form.reset();for(const name of ['receipt','invoice','sale','precision','paymentRecord','invoiceRecord'])offer(name,[]);issue.hidden=true;review.textContent='';root.hidden=true;shelf.append(root);showFields();}
 showFields();return{async open(id:string){reset();tradeId=id;if(!await load())return false;o.host().append(root);root.hidden=false;sync();return true;},async reload(){if(!mounted())return;dirty=false;prepared=undefined;form.reset();issue.hidden=true;showFields();await load();o.unsavedChanged();},reset,sync,unsaved:()=>mounted()&&dirty,refresh(){generation++;detail=undefined;prepared=undefined;issue.hidden=true;review.textContent='';ledger.replaceChildren();status.textContent='The context changed. Refresh delivery and cash.';sync();}};
}
