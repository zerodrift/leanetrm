import {cloneRead} from './read-references.js';
import {renderRegularTable,tableFilterControls} from './regular-table.js';
import {trackEdits} from './edit-tracking.js';
import {displayInstant} from './market-view.js';
import type {GasBillingWorkflow} from './gas-billing.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Payment={cashSlot:number;code:string;covered:boolean;reason:string|null};
type Line={determinationId:string;cashSlot:number;day:string;amount:string};
type Issued={id:string;ordinal:number;kind:string;payTotal:string;receiveTotal:string;currency:string;effectiveAt:string;evidence:string;lines:Line[]};
type Missing={cashSlot:number;day:string};
type Month={month:string;state:string;refusal:string|null;missing:Missing[];documents:Issued[]};
type View={expectedRevision:number;canWrite:boolean;scopePolicy:string;payments:Payment[];months:Month[]};
type Preview={state:string;missing:Missing[];previousId:string|null;previewHash:string|null;contentDigest:string|null;
 document:{kind:string;lines:Line[];payTotal:string;receiveTotal:string;currency:string}|null};

// Why final billing determines nothing for a payment, in the reader's terms. The backend names the reason.
const notCovered:Record<string,string>={payment_kind:'charges and cash-outs have no daily determination',stage:'receipt and fuel stages are not billed',
 rights:'an exercise or swing right governs it',index_convention:'its index convention is retired'};
const refused:Record<string,string>={gas_bill_later_document:'a document takes effect after the selected business time',
 gas_bill_revision:'it holds determinations of an earlier trade revision',gas_bill_scope:'no billable payment day'};
// Names the backend's own terms. A term this screen does not know is shown as the backend named it, never under a known name.
const kinds:Record<string,string>={bill:'Bill',adjustment_note:'Adjustment note'};
const scopes:Record<string,string>={gas_trade_month_gross_v1:'One trade, one month · gross'};
const named=(kind:string)=>kinds[kind]??kind;
const plural=(count:number,one:string,many:string)=>`${count} ${count===1?one:many}`;

/** Monthly bills of one gas trade. The backend assembles and totals every document; this draws its reads and sends the reviewed request. */
export function setupGasBills(host:HTMLElement,o:{api:Api;context:()=>Record<string,unknown>;tradeId:string;actor:()=>string;zone:()=>string;workflow?:GasBillingWorkflow}){
 const root=document.createElement('section');root.className='gas-bills';root.dataset.ownControls='true';
 root.innerHTML=`<header class="operations-section-heading"><div><h3>Monthly bills</h3><p>Gather a complete month’s daily determinations into a bill. A bill adds no amount of its own and is not proof of payment.</p></div><button class="secondary" type="button" data-bills="refresh">Refresh bills</button></header>
 <p data-bills="coverage"></p>
 <form class="billing-form"><fieldset><legend>Month &amp; scope</legend><div class="schedule-fields">
 <label>Delivery month<select data-bills="month" required></select><small>A bill needs a determination for every billable payment day of the month.</small></label>
 <label>Bill scope<select data-bills="scope" required></select><small>Payments we pay and payments we receive are totalled separately and never netted.</small></label></div></fieldset>
 <fieldset><legend>Evidence</legend><div class="schedule-fields">
 <label>Review evidence<input data-bills="evidence" maxlength="500" required pattern=".*\\S.*"><small>The review supporting this document.</small></label></div></fieldset>
 <div class="operations-form-actions"><button data-bills="preview">Review document</button><p>Review the lines and totals before issuing.</p></div></form>
 <p data-bills="message" role="status"></p><pre class="operation-review" data-bills="review" hidden></pre><button type="button" data-bills="confirm" hidden></button>
 <section class="operations-activity"><header><h3>Issued documents</h3><p>Bills and the adjustment notes linked to them. Issued documents never change.</p></header><div data-bills="documents"></div></section>
 <section class="operations-activity"><header><h3>Document lines</h3><p>Each line is one daily determination, on one document only.</p></header><div data-bills="lines"></div></section>`;
 host.append(root);
 const get=<T extends HTMLElement>(name:string)=>root.querySelector<T>(`[data-bills="${name}"]`)!;
 const form=root.querySelector('form')!,message=get('message'),confirm=get<HTMLButtonElement>('confirm');
 const owner=o.actor(),workflow=o.workflow;
 let view:View|undefined,reviewed:{command:Record<string,unknown>;label:string}|undefined,busy=false,generation=0,disposed=false;
 const edits=trackEdits(form,[tableFilterControls],()=>workflow?.changed());
 const payment=(slot:number)=>view?.payments.find(p=>p.cashSlot===slot)?.code??String(slot);
 const days=(missing:Missing[])=>missing.map(m=>`${payment(m.cashSlot)} ${m.day}`).join(', ');
 function writable(){return !disposed&&o.actor()===owner&&!!view?.canWrite&&!!workflow&&!workflow.pending();}
 function sync(){
  const blocked=busy||workflow?.busy()===true;
  for(const control of form.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))control.disabled=blocked||!writable();
  get<HTMLButtonElement>('refresh').disabled=blocked||!!workflow?.pending();
  get('refresh').textContent=edits.edited()?'Discard inputs & refresh':'Refresh bills';
  confirm.hidden=!reviewed;confirm.disabled=blocked||!writable();
 }
 function options(name:string,values:[string,string,boolean?][]){
  const select=get<HTMLSelectElement>(name),previous=select.value;
  select.replaceChildren(new Option('Choose…',''),...values.map(([value,label,disabled])=>{const option=new Option(label,value);option.disabled=!!disabled;return option;}));
  if(values.some(([value,,disabled])=>value===previous&&!disabled))select.value=previous;
 }
 const monthLabel=(m:Month)=>m.month.slice(0,7)+' · '+(m.state==='ready'?'ready for its bill':m.state==='adjustment_ready'?'adjustment note ready':m.state==='issued'?'issued':
  m.state==='incomplete'?`incomplete · ${plural(m.missing.length,'payment day','payment days')} without a determination`:
  m.state==='refused'?`unavailable in this view · ${refused[m.refusal??'']??m.refusal}`:m.state);
 async function read(){
  if(disposed||o.actor()!==owner)return;
  const epoch=++generation;busy=true;sync();
  try{
   const data=await o.api<View>('gas/bills/read',{context:o.context(),tradeId:o.tradeId});if(disposed||o.actor()!==owner||epoch!==generation)return;
   view=data;
   get('coverage').textContent='Payments on this trade\n'+data.payments.map(p=>`${p.code} · ${p.covered?'covered':'not covered · '+(notCovered[p.reason??'']??p.reason)}`).join('\n');
   options('month',data.months.map(m=>[m.month,monthLabel(m),m.state==='issued'||m.state==='refused']));
   options('scope',[[data.scopePolicy,scopes[data.scopePolicy]??data.scopePolicy]]);
   // Sequence is the document's retained ordinal within its trade and month. It is not a document number.
   const issued=data.months.flatMap(m=>m.documents.map(d=>({month:m.month.slice(0,7),name:named(d.kind),sequence:String(d.ordinal),...d})));
   renderRegularTable(get('documents'),'gas-bills:'+o.tradeId,o.tradeId,['Month','Document','Sequence','Payments we pay','Payments we receive','Currency','Lines','Issued as of','Evidence'],
    issued.map(d=>[d.month,d.name,d.sequence,d.payTotal,d.receiveTotal,d.currency,String(d.lines.length),displayInstant(d.effectiveAt,o.zone()),d.evidence]),[2,3,4,6],()=>busy);
   renderRegularTable(get('lines'),'gas-bill-lines:'+o.tradeId,o.tradeId,['Month','Document','Sequence','Payment','Gas day','Amount','Currency'],
    issued.flatMap(d=>d.lines.map(l=>[d.month,d.name,d.sequence,payment(l.cashSlot),l.day,l.amount,d.currency])),[2,5],()=>busy);
   if(!workflow?.pending())message.textContent=data.canWrite?'Select a month and review its document.':'Bills are read only in this context.';
  }catch(error){if(!disposed&&epoch===generation){view=undefined;message.textContent=String(error);}}
  finally{busy=false;if(!disposed)sync();}
 }
 function changed(){generation++;reviewed=undefined;get('review').hidden=true;sync();}
 form.addEventListener('input',changed);form.addEventListener('change',changed);
 form.onsubmit=event=>{event.preventDefault();void preview();};
 async function preview(){
  if(!view||!writable()||busy||workflow?.busy())return;
  // An earlier review is withdrawn before another is asked for: what comes back may offer no document.
  changed();
  const epoch=generation,month=get<HTMLSelectElement>('month').value,evidence=get<HTMLInputElement>('evidence').value,expectedRevision=view.expectedRevision;
  busy=true;sync();
  try{
   const request={context:cloneRead(o.context()),tradeId:o.tradeId,expectedRevision,month,scopePolicy:get<HTMLSelectElement>('scope').value};
   const p=await o.api<Preview>('gas/bills/preview',request);if(disposed||o.actor()!==owner||epoch!==generation)return;
   if(!p.document||!p.previewHash||!p.contentDigest){
    message.textContent=p.state==='incomplete'?`${plural(p.missing.length,'payment day has','payment days have')} no determination: ${days(p.missing)}. Determine them in Billing & corrections first.`
     :p.state==='issued'?'Every determination of this month is already on a document.':`No document can be issued for this month: ${p.state}.`;return;}
   const d=p.document;
   reviewed={command:{preview:request,previewHash:p.previewHash,contentDigest:p.contentDigest,expectedPreviousId:p.previousId,evidence},label:'Gas '+named(d.kind).toLowerCase()};
   const review=get('review');review.hidden=false;
   review.textContent=`${named(d.kind)} for ${month.slice(0,7)} · ${plural(d.lines.length,'line','lines')}\nPayments we pay: ${d.payTotal} ${d.currency}\nPayments we receive: ${d.receiveTotal} ${d.currency}\n`+
    d.lines.map(l=>`${payment(l.cashSlot)} · ${l.day} · ${l.amount} ${d.currency}`).join('\n')+`\nEvidence: ${evidence}`;
   confirm.textContent='Issue reviewed '+named(d.kind).toLowerCase();
   message.textContent='Review the lines and both totals, then issue.';
  }catch(error){if(!disposed)message.textContent=String(error);}
  finally{busy=false;if(!disposed)sync();}
 }
 confirm.onclick=()=>{
  if(!reviewed||!writable()||busy||!workflow||workflow.busy())return;
  const command={path:'gas/bills/issue',operationId:crypto.randomUUID(),body:cloneRead(reviewed.command),label:reviewed.label};
  busy=true;sync();
  void(async()=>{try{await workflow.queue(command);}catch(error){if(!disposed)message.textContent=String(error);}finally{busy=false;if(!disposed){reviewed=undefined;get('review').hidden=true;sync();}}})();
 };
 // Reads again under the current context. A review is withdrawn either way; the inputs are kept unless they are discarded.
 function refresh(discard:boolean){if(discard){form.reset();edits.clear();}changed();return read();}
 get('refresh').onclick=()=>{if(busy||workflow?.busy()||workflow?.pending())return;void refresh(edits.edited());};
 if(workflow?.pending())message.textContent='A trade request awaits its original outcome. Use Check status or Retry above this workspace.';
 void read();
 return {sync,refresh,unsaved:()=>edits.edited(),destroy(){disposed=true;generation++;root.remove();}};
}
