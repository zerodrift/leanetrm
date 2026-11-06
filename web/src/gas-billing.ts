import {renderRegularTable,tableFilterControls} from './regular-table.js';
import type {RequestCommand} from './pending-command.js';
import {trackEdits} from './edit-tracking.js';
import {zonedInput} from './user-time.js';
import {displayInstant} from './market-view.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Claim={cashSlot:number;code:string;role:string;stage:string;sideCode:string;day:string;price:string;currency:string;decimalPlaces:number;supported:boolean;completed:boolean};
type Fact={id:string;cash_slot:number;day:string;ordinal:number;rounded_total:string;contribution:string;currency_code:string;due_at:string;reason:string|null;evidence:string};
type View={expectedRevision:number;canWrite:boolean;profile:string;roundingPolicy:string;quantityPolicies:string[];correctionReasons:string[];claims:Claim[];determinations:Fact[]};
type Preview={previewHash:string;sourceId:string;previousId:string|null;amount:string;contribution:string;currency:string;contractualDueAt:string;alreadyDetermined:boolean};
export type GasBillingWorkflow={busy:()=>boolean;pending:()=>RequestCommand|undefined;queue:(command:RequestCommand)=>Promise<void>;changed:()=>void};

export function setupGasBilling(host:HTMLElement,o:{api:Api;context:()=>Record<string,unknown>;tradeId:string;actor:()=>string;zone:()=>string;readOnly?:boolean;workflow?:GasBillingWorkflow}){
 const root=document.createElement('section');root.className='gas-billing';root.dataset.ownControls='true';
 root.innerHTML=`<header class="operations-section-heading"><div><h3>Billing &amp; corrections</h3><p>Review cash from final delivery quantities. Invoices and payments stay separate.</p></div><button class="secondary" type="button" data-billing="refresh">Refresh billing</button></header>
 <form class="billing-form"><fieldset><legend>Delivery &amp; source</legend><div class="schedule-fields">
 <label>Delivery claim<select data-billing="claim" required></select><small>A booked payment or fee and its delivery day.</small></label>
 <label>Final quantity source<select data-billing="policy" required></select><small>Choose final manual allocation or an accepted facility allocation.</small></label></div></fieldset>
 <fieldset><legend>Calculation</legend><div class="schedule-fields">
 <label>Billing profile<select data-billing="profile" required></select><small>Calculate each day and cash component, then its change from the prior determination.</small></label>
 <label>Currency rounding<select data-billing="rounding" required></select><small>Use the booked currency’s minor units, with ties to even.</small></label></div></fieldset>
 <fieldset class="billing-evidence"><legend>Evidence &amp; correction</legend><div class="schedule-fields">
 <label class="billing-evidence-source">Review evidence<input data-billing="evidence" maxlength="500" required><small>The statement or review supporting this determination.</small></label>
 <label>Correction reason<select data-billing="reason"></select><small>Required for corrections. This does not select accounting treatment.</small></label>
 <label>Correction due time<input data-billing="due" type="datetime-local" step="1"><small data-billing="zone"></small></label></div></fieldset>
 <div class="operations-form-actions"><button data-billing="preview">Review billing</button><p>Review the result before recording it.</p></div></form>
 <p data-billing="message" role="status"></p><pre class="operation-review" data-billing="review" hidden></pre><button type="button" data-billing="confirm" hidden>Accept reviewed determination</button>
 <section class="operations-activity"><header><h3>Cash history</h3><p>Original determinations and subsequent corrections.</p></header><div data-billing="history"></div></section>`;
 host.append(root);
 const get=<T extends HTMLElement>(name:string)=>root.querySelector<T>(`[data-billing="${name}"]`)!;
 const form=root.querySelector('form')!,message=get('message'),confirm=get<HTMLButtonElement>('confirm');
 const owner=o.actor(),workflow=o.workflow;
 let view:View|undefined,reviewed:Record<string,unknown>|undefined,busy=false,generation=0,disposed=false;
 const edits=trackEdits(form,[tableFilterControls],()=>workflow?.changed());
 const due=get<HTMLInputElement>('due'),time=zonedInput(due,o.zone,o.api);
 function writable(){return !disposed&&o.actor()===owner&&!!view?.canWrite&&!o.readOnly&&!!workflow&&!workflow.pending();}
 function sync(){
  const blocked=busy||workflow?.busy()===true;
  for(const control of form.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))control.disabled=blocked||!writable();
  get<HTMLButtonElement>('refresh').disabled=blocked||!!workflow?.pending();
  get('refresh').textContent=edits.edited()?'Discard inputs & refresh':'Refresh billing';
  confirm.hidden=!reviewed;confirm.disabled=blocked||!writable();

 }
 function options(name:string,values:[string,string,boolean?][],previous=''){
  const select=get<HTMLSelectElement>(name);select.replaceChildren(new Option('Choose…',''),...values.map(([value,label,disabled])=>{const option=new Option(label,value);option.disabled=!!disabled;return option;}));
  if(values.some(([value])=>value===previous))select.value=previous;
 }
 async function read(){
  if(disposed||o.actor()!==owner)return;
  const epoch=++generation;busy=true;sync();
  try{
   const data=await o.api<View>('gas/billing/read',{context:o.context(),tradeId:o.tradeId});if(disposed||o.actor()!==owner||epoch!==generation)return;
   view=data;options('claim',data.claims.map((c,i)=>[String(i),`${c.day} · ${c.sideCode} / ${c.code} · ${c.price} ${c.currency}${!c.supported?' · TBD: '+c.stage+' billing':!c.completed?' · delivery not completed':''}`,!c.supported||!c.completed]),get<HTMLSelectElement>('claim').value);
   options('policy',data.quantityPolicies.map(p=>[p,p==='final_allocated_billing_v1'?'Manual final allocation':'Accepted facility allocation']),get<HTMLSelectElement>('policy').value);
   options('profile',[[data.profile,'Final daily fixed cash']],get<HTMLSelectElement>('profile').value);
   options('rounding',[[data.roundingPolicy,'Currency minor units · ties to even']],get<HTMLSelectElement>('rounding').value);
   options('reason',data.correctionReasons.map(r=>[r,r.replaceAll('_',' ')]),get<HTMLSelectElement>('reason').value);
   get('zone').textContent='Display timezone: '+o.zone()+'. Corrections require an explicit due time.';
   renderRegularTable(get('history'),'gas-billing:'+o.tradeId,o.tradeId,['Day','Cash component','Revision','Restated total','New contribution','Currency','Due','Cause','Evidence'],data.determinations.map(d=>[d.day,data.claims.find(c=>c.cashSlot===d.cash_slot)?.code??String(d.cash_slot),String(d.ordinal),d.rounded_total,d.contribution,d.currency_code,displayInstant(d.due_at,o.zone()),d.reason??'Original',d.evidence]),[2,3,4],()=>busy);
   if(!workflow?.pending())message.textContent=data.canWrite&&!o.readOnly?'Select and preview one completed delivery claim.':'Billing history is read only in this context.';
  }catch(error){if(!disposed)message.textContent=String(error);}
  finally{busy=false;if(!disposed)sync();}
 }
 function changed(){generation++;reviewed=undefined;get('review').hidden=true;sync();}
 form.addEventListener('input',changed);form.addEventListener('change',changed);
 form.onsubmit=event=>{event.preventDefault();void preview();};
 async function preview(){
  if(!view||!writable()||busy||workflow?.busy())return;
  const epoch=++generation,context=structuredClone(o.context()),claim=view.claims[Number(get<HTMLSelectElement>('claim').value)];
  if(!claim||!claim.supported||!claim.completed)return;
  const request={context,tradeId:o.tradeId,expectedRevision:view.expectedRevision,cashSlot:claim.cashSlot,day:claim.day,
   selectionPolicy:get<HTMLSelectElement>('policy').value,profile:get<HTMLSelectElement>('profile').value,decimalPlaces:claim.decimalPlaces,roundingPolicy:get<HTMLSelectElement>('rounding').value};
  busy=true;sync();
  try{
   const p=await o.api<Preview>('gas/billing/preview',request);if(disposed||o.actor()!==owner||epoch!==generation)return;
   if(p.alreadyDetermined){message.textContent='These accepted inputs already have a cash determination. Its retained amount is shown in history.';return;}
   const reason=get<HTMLSelectElement>('reason').value,evidence=get<HTMLInputElement>('evidence').value;
   if(p.previousId&&(!reason||!due.value))throw Error('Choose the correction reason and its due time, then preview again.');
   const correctionDueAt=p.previousId?await time.instant():null;if(disposed||o.actor()!==owner||epoch!==generation)return;
   reviewed={preview:request,previewHash:p.previewHash,sourceId:p.sourceId,expectedPreviousId:p.previousId,correctionDueAt,reason:p.previousId?reason:null,evidence};
   const review=get('review');review.hidden=false;review.textContent=`Restated amount: ${p.amount} ${p.currency}\nNew contribution: ${p.contribution} ${p.currency}\nDue: ${displayInstant(correctionDueAt??p.contractualDueAt,o.zone())}\nCause: ${p.previousId?reason:'Original'}\nEvidence: ${evidence}`;
   message.textContent='Review the amount and supporting terms, then accept.';
  }catch(error){if(!disposed)message.textContent=String(error);}
  finally{busy=false;if(!disposed)sync();}
 }
 confirm.onclick=()=>{
  if(!reviewed||!writable()||busy||!workflow||workflow.busy())return;
  const command={path:'gas/billing/determine',operationId:crypto.randomUUID(),body:structuredClone(reviewed),label:'Gas cash determination'};
  busy=true;sync();
  void(async()=>{try{await workflow.queue(command);}catch(error){if(!disposed)message.textContent=String(error);}finally{busy=false;if(!disposed){reviewed=undefined;get('review').hidden=true;sync();}}})();
 };
 get('refresh').onclick=()=>{if(busy||workflow?.busy()||workflow?.pending())return;if(edits.edited()){form.reset();edits.clear();changed();}void read();};
 if(workflow?.pending())message.textContent='A trade request awaits its original outcome. Use Check status or Retry above this workspace.';
 void read();
 return {sync,pending:()=>!!workflow?.pending()||busy,unsaved:()=>edits.edited(),destroy(){disposed=true;generation++;root.remove();}};
}
