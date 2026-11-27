import {cloneRead} from './read-references.js';
import {zonedInput} from './user-time.js';
import {renderRegularTable} from './regular-table.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Choices={canEnroll:boolean;books:{id:string;name:string}[];policies:{id:string;revision:number;mode:'review'|'automatic';from_at:string;until_at:string}[];
 discoveryPolicy:string;processingConvention:string;knowledgePolicy:string;scheduledPolicy:string;profiles:string[];selectionPolicies:string[];roundingPolicy:string;
 maximumPage:number;maximumTimeoutSeconds:number;maximumAttempts:number};
type Page={context:Record<string,unknown>;items:{selection:{tradeId:string;cashSlot:number;day:string};state:string;amount:string|null;currency:string|null;reason:string|null;
 reconciliation?:{contractual:string;scheduled:string|null;final:string|null}}[];hasMore:boolean;next:unknown};
const element=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};

/** Operating selections only. The backend discovers native days, reconciles
 * quantities, calculates cash and authorizes every scheduled action. */
export function setupGasBillingScope(host:HTMLElement,o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;identity:()=>string;zone:()=>string;
 pending:()=>boolean;mutate:(path:string,body:Record<string,unknown>)=>Promise<void>}){
 const root=element('details');root.className='card';root.append(element('summary','Daily gas processing'));
 root.append(element('p','Select books and delivery dates, then review the candidate page. Review mode produces proposals. Automatic mode records only reconciled original bills; corrections require review.'));
 const form=element('form');form.className='lifecycle-form';root.append(form);host.append(root);
 const fields=element('div');fields.className='schedule-fields';form.append(fields);
 function field(key:string,label:string,type='text'){
  const wrap=element('label',label),input=element('input');input.type=type;input.dataset.gasScope=key;input.required=true;wrap.append(input);fields.append(wrap);return input;
 }
 function choice(key:string,label:string,multiple=false){
  const wrap=element('label',label),select=element('select');select.dataset.gasScope=key;select.required=true;select.multiple=multiple;wrap.append(select);fields.append(wrap);return select;
 }
 function options(select:HTMLSelectElement,values:[string,string][]){select.replaceChildren(...select.multiple?[]:[new Option('Choose…','')],...values.map(([v,label])=>new Option(label,v)));}
 const business=field('business-date','First processing business date (UTC)','date'),due=field('due','First processing time','datetime-local');due.step='1';
 const dueTime=zonedInput(due,o.zone,o.api),load=element('button','Load books and policies');load.type='button';load.dataset.gasScope='choices';form.append(load);
 const books=choice('books','Books',true),from=field('from','First delivery day','date'),to=field('to','Delivery end (not included)','date');
 const policy=choice('policy','Saved action policy and mode'),final=choice('final','Final quantity source'),profile=choice('profile','Billing calculation');
 const rounding=choice('rounding','Currency rounding'),discovery=choice('discovery','Discovery'),knowledge=choice('knowledge','Knowledge'),scheduled=choice('scheduled','Scheduled quantity source');
 const name=field('name','Job name'),expires=field('expires','Stop processing at','datetime-local');expires.step='1';name.maxLength=100;
 const expiryTime=zonedInput(expires,o.zone,o.api),cadence=field('cadence','Repeat scan every (seconds)','number');cadence.required=false;cadence.min='60';cadence.max='31536000';
 const delay=field('delay','Wait after native day ends (seconds)','number'),pageSize=field('page-size','Occurrences per page','number'),dispatch=field('dispatch','New bill jobs per page','number');
 const timeout=field('timeout','Page and bill timeout (seconds)','number'),attempts=field('attempts','Maximum attempts','number');
 for(const input of [delay,pageSize,dispatch,timeout,attempts,cadence])input.step='1';delay.min='0';delay.max='86400';
 for(const input of [pageSize,dispatch,timeout,attempts])input.min='1';
 const timeNote=element('p');form.append(timeNote);
 function showZone(){timeNote.textContent='Times display in '+o.zone()+'. Each scan captures its inputs when it starts and retains them across pages. Subsequent scans revisit the full selected date window, including late quantities.';}showZone();
 const actions=element('div');actions.className='operations-form-actions';form.append(actions);
 function button(key:string,text:string){const b=element('button',text);b.type='button';b.dataset.gasScope=key;actions.append(b);return b;}
 const preview=button('preview','Preview scope'),next=button('next','Next candidate page'),once=button('once','Run once'),enroll=button('enroll','Enroll recurring');
 const status=element('p');status.setAttribute('role','status');root.append(status);const table=element('div');root.append(table);
 const hint=element('p','Pause, cancel and view retained results in the jobs list below. A finished scan can contain waiting or correction items; it does not mean the scope is fully billed.');root.append(hint);
 let choices:Choices|undefined,page:Page|undefined,source:Record<string,unknown>|undefined,reviewed=false,generation=0;
 function invalidate(){generation++;reviewed=false;page=undefined;source=undefined;table.replaceChildren();sync();}
 form.addEventListener('input',invalidate);form.addEventListener('change',invalidate);
 business.addEventListener('input',()=>{choices=undefined;options(books,[]);options(policy,[]);sync();});
 due.addEventListener('input',()=>{choices=undefined;options(books,[]);options(policy,[]);sync();});
 form.onsubmit=e=>e.preventDefault();
 function sync(){
  const blocked=o.busy()||o.pending();
  for(const input of form.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))input.disabled=blocked;
  preview.disabled=blocked||!choices;next.hidden=!page?.hasMore;next.disabled=blocked||!page?.hasMore;
  once.disabled=enroll.disabled=blocked||!choices?.canEnroll||!reviewed;
 }
 async function context(){if(!business.value||!due.value)throw Error('Choose the processing business date and time.');return{convention:'gas_processing_instant_utc_v1',businessDate:business.value,evaluationAt:await dueTime.instant(),knowledgeMode:'current',retained:null};}
 load.onclick=()=>void o.act(async()=>{
  const version=++generation,actor=o.identity(),value=await o.api<Choices>('gas/billing/enrollment-choices',await context());
  if(version!==generation||actor!==o.identity())return;choices=value;reviewed=false;page=undefined;source=undefined;table.replaceChildren();
  options(books,value.books.map(b=>[b.id,b.name]));options(policy,value.policies.map(p=>[JSON.stringify([p.id,p.revision,p.mode]),`${p.mode==='review'?'Review proposals':'Automatic original bills'} · ${p.id} · revision ${p.revision} · ${p.from_at} to ${p.until_at}`]));
  options(final,value.selectionPolicies.map(p=>[p,p==='final_allocated_billing_v1'?'Manual final allocations':'Accepted facility allocations']));
  options(profile,value.profiles.map(p=>[p,p==='gas_final_daily_entry_cash_v1'?'Daily entry cash: retained share and quantity part':'Whole daily fixed cash']));
  options(rounding,[[value.roundingPolicy,'Currency minor units, ties to even']]);options(discovery,[[value.discoveryPolicy,'Reassess the complete selected scope']]);
  options(knowledge,[[value.knowledgePolicy,'Fresh cut per scan, retained across its pages']]);options(scheduled,[[value.scheduledPolicy,'Recorded manual schedule']]);
  pageSize.max=dispatch.max=String(value.maximumPage);timeout.max=String(value.maximumTimeoutSeconds);attempts.max=String(value.maximumAttempts);
  status.textContent=value.policies.length?'Choose every source and execution policy before previewing.':'No applicable gas billing policy is saved for this processing time. Configure a policy in Lifecycle first.';sync();
 });
 function selection(){
  if(!choices||!books.selectedOptions.length||![policy,final,profile,rounding,discovery,knowledge,scheduled].every(input=>input.value)||![name,from,to,business,due,expires].every(input=>input.value)||!form.reportValidity())throw Error('Complete the scope, policies and execution limits.');
  const [policyId,policyRevision,mode]=JSON.parse(policy.value);
  const number=(input:HTMLInputElement)=>{if(!input.value)throw Error('Enter every execution limit explicitly.');return Number(input.value);};
  return{bookIds:[...books.selectedOptions].map(v=>v.value),from:from.value,to:to.value,discoveryPolicy:discovery.value,selectionPolicy:final.value,billingProfile:profile.value,
   roundingPolicy:rounding.value,scheduledPolicy:scheduled.value,processingConvention:choices.processingConvention,knowledgePolicy:knowledge.value,policyId,policyRevision,mode,
   readinessDelaySeconds:number(delay),pageLimit:number(pageSize),dispatchLimit:number(dispatch),childTimeoutSeconds:number(timeout),childMaximumAttempts:number(attempts)};
 }
 async function inspect(more:boolean){
  const selected=more&&source?source:selection(),version=++generation,actor=o.identity(),processing=await context();
  const result=await o.api<Page>('gas/billing/enrollment-preview',{source:selected,context:more&&page?{...processing,knowledgeMode:'retained',retained:page.context}:processing,after:more?page?.next:null});
  if(version!==generation||actor!==o.identity())return;source=cloneRead(selected);page=result;reviewed=true;
  renderRegularTable(table,'gas-billing-scope',actor,['Day','Trade','Payment','Status','Contract','Scheduled','Final','Amount','Currency'],result.items.map(i=>[
   i.selection.day,i.selection.tradeId,String(i.selection.cashSlot),i.state.replaceAll('_',' '),i.reconciliation?.contractual??'',i.reconciliation?.scheduled??'Unavailable',
   i.reconciliation?.final??'Unavailable',i.amount??'',i.currency??'']),[2,4,5,6,7],o.busy);
  status.textContent=`${result.items.length} candidates on this page${result.hasMore?'; more pages remain.':'; end of selected scope.'} Review the mode and limits before scheduling.`;sync();
 }
 preview.onclick=()=>void o.act(()=>inspect(false));next.onclick=()=>void o.act(()=>inspect(true));
 async function save(recurring:boolean){
  if(!reviewed||!source||!choices?.canEnroll||o.pending())throw Error('Review the current scope first.');
  if(recurring&&(!cadence.value||!cadence.checkValidity()))throw Error('Choose an explicit recurrence of at least 60 seconds.');
  const version=generation,actor=o.identity(),selected=cloneRead(source),dueAt=await dueTime.instant(),expiresAt=await expiryTime.instant();
  if(version!==generation||actor!==o.identity())return;
  await o.mutate('gas/billing/enroll',{enrollmentId:crypto.randomUUID(),expectedRevision:0,jobId:null,expectedJobVersion:null,source:selected,
   schedule:{name:name.value,operation:'lifecycle_action_v1',dueAt,expiresAt,intervalSeconds:recurring?Number(cadence.value):null,timeZone:'UTC',misfire:'run_once',overlap:'forbid',
    timeoutSeconds:Number(timeout.value),maximumAttempts:Number(attempts.value),profileId:null,profileVersion:null}});
  reviewed=false;status.textContent='Processing is scheduled. Use its job row to pause or view results.';sync();
 }
 once.onclick=()=>void o.act(()=>save(false));enroll.onclick=()=>void o.act(()=>save(true));sync();
 return{sync,reset(){generation++;choices=undefined;page=undefined;source=undefined;reviewed=false;form.reset();dueTime.clear();expiryTime.clear();table.replaceChildren();status.textContent='';root.open=false;sync();},refresh(){dueTime.refresh();expiryTime.refresh();showZone();invalidate();}};
}
