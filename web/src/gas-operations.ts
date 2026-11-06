import {emptyGridLayout,type GridHandle} from './data-grid.js';
import {gasDeliveryKey} from './physical-operations-grid.js';
import {readPhysicalDelivery} from './physical-delivery.js';
import {renderPhysicalDeliveryGrid} from './physical-delivery-grid.js';
import {renderRegularTable} from './regular-table.js';
import {setupTabs} from './side-panel.js';
import {trackEdits} from './edit-tracking.js';
import {zonedInput} from './user-time.js';
import {quantityRecord,type GasQuantityView} from './gas-daily-quantities.js';
import type {GasBillingWorkflow} from './gas-billing.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Row=Record<string,string|number|boolean|null>;
type State={accounts:Row[];nominations:Row[];statements:Row[];agreements:Row[];responses:Row[];movements:Row[];nominationApplications:Row[];imbalanceApplications:Row[];heads:Row[]};
type Review={action?:string;phase?:string;quantity?:string;unit?:string;confirmed?:string;scheduled?:string;buyerBalanceAfter?:string;sellerBalanceAfter?:string;cash?:string;currency?:string;priceMultiplier?:string;scope?:string};
type View={permissions:{canConfigure:boolean;canRecord:boolean};state:State;balances:{accountId:string;amount:string|null;refusal:string|null}[];nominationProfile:string;imbalanceProfile:string;roundingPolicy:string;statementPolicies:string[]};
type Field={name:string;label:string;meaning:string;type?:string;options?:[string,string][];optional?:boolean};
const stages=['Before flow','Scheduling','After flow','Reconciliation'];
const actions=[
 ['service','Service account & terms',0,'Accounts, availability and statement policy'],
 ['nomination','Submitted nomination / retro request',0,'A nomination or retro request sent externally'],
 ['response','External acknowledgment',1,'A counterparty or pipeline decision'],
 ['nomination_apply','Apply accepted schedule',1,'Review confirmed and scheduled quantities'],
 ['daily_quantity','Allocated delivery quantity',2,'Provisional, final or withdrawn delivery'],
 ['statement','Operator balance statement',2,'The operator’s reported account balance'],
 ['imbalance','New imbalance agreement',3,'An explicitly priced transfer agreement'],
 ['imbalance_apply','Apply accepted imbalance transfer',3,'Apply an approved paired transfer']
] as const;
const text=(name:string,label:string,meaning:string,type='text',optional=false):Field=>({name,label,meaning,type,optional});
const choice=(name:string,label:string,meaning:string,options:[string,string][]):Field=>({name,label,meaning,options});
const evidence=text('evidence','Source / review evidence','Identify the external statement, acknowledgment or review. This records manual evidence; it does not contact the operator.');
const reference=text('externalReference','External reference','The external account, request, statement or agreement identifier; core scope comes from exact retained references.');
const latest=(rows:Row[])=>rows.reduce<Row|undefined>((head,r)=>!head||Number(r.revision)>Number(head.revision)?r:head,undefined);

/** A trade's manual desk. Stage and form definitions are presentation only;
 * native operations resolve calendar bounds, account capacity, cash and acceptance. */
export function setupGasOperations(host:HTMLElement,o:{api:Api;context:()=>Record<string,unknown>;tradeId:string;actor:()=>string;zone:()=>string;readOnly?:boolean;workflow?:GasBillingWorkflow}){
 const root=document.createElement('section');root.className='gas-operations';root.dataset.ownControls='true';
 root.innerHTML=`<header class="operations-section-heading"><div><h3>Delivery workspace</h3><p>Every contract day and physical side, with its independent quantity tracks.</p><p class="operations-mode">Manual entry <span>Live pipeline connection · TBD</span></p></div><button class="secondary" type="button" data-ops="refresh">Refresh delivery rows</button></header>
 <section class="operations-deliveries" aria-label="Delivery rows"><div data-ops="rows"></div><p class="operations-grid-note">Quantities are native interval totals. A blank track has no record; withdrawn records stay identified. Times use <span data-ops="zone"></span>.</p></section>
 <section data-ops="workflow" hidden><div class="operations-row-context" data-ops="selection"></div><div class="gas-stages" data-ops="stages"></div><div data-ops="stage-panel"><p class="gas-stage-description" data-ops="stage-description"></p><div data-ops="actions"></div><div class="operation-editor" data-ops="editor" hidden><h4 data-ops="title"></h4><form class="schedule-fields"></form><pre class="operation-review" data-ops="review" hidden></pre><button type="button" data-ops="confirm" hidden>Accept reviewed operation</button></div><div data-ops="allocations"></div></div></section><p data-ops="message" role="status"></p>
 <section class="operations-activity" data-ops="activity" hidden><header><h3>Activity &amp; evidence</h3><p>Recorded facts across all four stages for the selected day.</p></header><div data-ops="history"></div></section>`;
 host.append(root);const get=<T extends HTMLElement>(name:string)=>root.querySelector<T>(`[data-ops="${name}"]`)!;
 const descriptions=['Set up service terms or record a request sent to the operator.','Record external responses, then review their adoption into the schedule.','Record allocated delivery and operator statements. Facility allocations remain separate evidence.','Agree and apply an explicit imbalance transfer. Commercial cash remains nonportfolio and unposted.'];
 const panels=stages.map(()=>{const panel=document.createElement('div');panel.className='gas-stage';get('actions').append(panel);return panel;});
 const afterFlowHost=get('allocations');
 const form=root.querySelector('form')!,message=get('message'),owner=o.actor(),workflow=o.workflow;
 let grid:GridHandle|undefined,layout=emptyGridLayout(),filters:{query:string;columns:(string|null)[]}|undefined,selectedKey:string|undefined;
 let view:View|undefined,daily:GasQuantityView|undefined,kind:string|undefined,stage=0,busy=false,disposed=false,generation=0,fields:Field[]=[],reviewed:Record<string,unknown>|undefined;
 const selectedDelivery=()=>daily?.rows.find(r=>gasDeliveryKey(r)===selectedKey);
 const day=()=>selectedDelivery()?.day??'';
 const edits=trackEdits(form,[],()=>workflow?.changed());const times=new Map<string,ReturnType<typeof zonedInput>>();
 const stageList=get('stages'),stagePanel=get('stage-panel');stageList.id='gas-stages-'+crypto.randomUUID();stagePanel.id=stageList.id+'-panel';
 const stageTabs=setupTabs(stageList,stages.map((title,i)=>[String(i),title]),{label:'Delivery stage',panel:stagePanel,selected:'0',select:next=>{
  stageTabs.mark(String(stage));if(Number(next)===stage||busy||workflow?.busy()||workflow?.pending())return;
  if(edits.edited()){message.textContent='Finish this operation, or discard inputs with Refresh operations before changing stage.';return;}
  stage=Number(next);kind=undefined;draw();showStage();
 }});
 for(const [i,title] of stages.entries()){const button=stageList.children[i] as HTMLButtonElement;button.dataset.stage=String(i);button.innerHTML=`<span class="gas-stage-number" aria-hidden="true">${i+1}</span><span data-stage-title="${i}">${title}</span>`;}
 function showStage(){stageTabs.mark(String(stage));panels.forEach((p,i)=>p.hidden=i!==stage);afterFlowHost.hidden=stage!==2;get('stage-description').textContent=descriptions[stage];}

 for(const [id,label,at,meaning] of actions){const button=document.createElement('button');button.type='button';button.dataset.operation=id;button.innerHTML='<span class="gas-action-title"></span><span class="gas-action-meaning"></span><span class="gas-action-arrow" aria-hidden="true">→</span>';button.querySelector('.gas-action-title')!.textContent=label;button.querySelector('.gas-action-meaning')!.textContent=meaning;button.onclick=()=>{
  if(panels[at].hidden||busy||workflow?.busy()||workflow?.pending())return;
  if(edits.edited()){message.textContent='Keep editing, or discard inputs with Refresh operations before choosing another action.';return;}kind=id;draw();
 };panels[at].append(button);}
 showStage();
 function writable(){return !disposed&&!o.readOnly&&o.actor()===owner&&o.context().knowledgeMode==='current'&&!o.context().contextId&&!!workflow&&!workflow.pending();}
 function sync(){const blocked=busy||workflow?.busy()===true||!!workflow?.pending();for(const control of form.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))control.disabled=blocked||!writable()||!(kind==='service'?view?.permissions.canConfigure:kind==='daily_quantity'?daily?.canWrite:view?.permissions.canRecord);
  for(const button of stageList.querySelectorAll<HTMLButtonElement>('button'))button.disabled=blocked;
  for(const button of root.querySelectorAll<HTMLButtonElement>('[data-operation]')){button.setAttribute('aria-pressed',String(button.dataset.operation===kind));button.disabled=blocked||!selectedDelivery()||!writable()||!(button.dataset.operation==='service'?view?.permissions.canConfigure:button.dataset.operation==='daily_quantity'?daily?.canWrite:view?.permissions.canRecord);}
  get('rows').inert=blocked;get('workflow').hidden=!selectedDelivery();get('activity').hidden=!view;get('selection').textContent=selectedDelivery()?`${day()} · ${selectedDelivery()!.sideCode} · ${selectedDelivery()!.deliveryPoint??''}`:'';get<HTMLButtonElement>('refresh').disabled=blocked;get('refresh').textContent=edits.edited()?'Discard inputs & refresh':'Refresh delivery rows';get<HTMLButtonElement>('confirm').disabled=blocked||!writable();get('confirm').hidden=!reviewed;
 }
 const ownAccounts=()=>view?.state.accounts.filter(a=>a.trade_id===o.tradeId&&a.origin_obligation_id===selectedDelivery()?.obligationId)??[];
 const nominations=()=>view?.state.nominations.filter(n=>ownAccounts().some(a=>a.id===n.account_id))??[];
 const ownedTransferAccounts=()=>ownAccounts().filter(a=>a.holder_role==='legal_entity');
 const ownAgreements=()=>view?.state.agreements.filter(t=>ownedTransferAccounts().some(a=>a.id===t.buyer_account_id||a.id===t.seller_account_id))??[];
 const transferAccounts=()=>view?.state.accounts.filter(a=>a.holder_role==='counterparty'||ownedTransferAccounts().some(owned=>owned.id===a.id))??[];
 const choices=(rows:Row[],label:(r:Row)=>string):[string,string][]=>rows.map(r=>[String(r.id),label(r)]);
 function specs():Field[]{
  const anchor=selectedDelivery(),anchors:[string,string][]=anchor?[[anchor.obligationId,anchor.sideCode]]:[];
  const account=()=>choice('accountId','Service account','Latest terms under the header context; prior versions are read by changing that context.',choices(ownAccounts(),a=>`${a.external_reference} · ${a.holder_role} · terms ${a.revision}`));
  const nominationChoice=()=>choice('nominationId','Submitted request','Select the recorded external request whose accepted response will be applied.',choices(nominations(),n=>`${n.external_reference} · ${n.quantity} · revision ${n.revision}`));
  const agreements=()=>choice('imbalanceId','Imbalance agreement','One immutable agreement; its full accepted transfer can be applied once.',choices(ownAgreements(),t=>`${t.external_reference} · ${t.quantity} @ ${t.price}`));
  switch(kind){
   case 'service':return [choice('obligationId','Booked delivery anchor','Scope, native calendar, units and exact party/reference versions come from this booked physical side.',anchors),choice('holderRole','Account holder','Choose whether the external service account belongs to the booked legal entity or counterparty.',[['legal_entity','Our legal entity'],['counterparty','Counterparty']]),reference,text('validFrom','First supported day','Inclusive service day.','date'),text('validTo','End of support','Exclusive service day.','date'),choice('active','Availability','Retiring terms stops new operations; history remains retained.',[['true','Active'],['false','Inactive']]),choice('statementPolicy','Eligible balance statements','Select whether provisional operator evidence can be used or final statements are required.',view!.statementPolicies.map(p=>[p,p==='final_statement_balance_v1'?'Final statements only':'Reported provisional or final statements'])),text('termsReference','Service terms reference','A locator for the reviewed agreement; no real operator tariff is supplied by the application.'),evidence];
   case 'nomination':return [account(),reference,text('quantity','Requested total','Absolute native delivery quantity; submission updates nominated quantity only.'),text('submittedAt','Actual submission time',`When the request was sent externally, shown in ${o.zone()}. Before/intra/retro classification is determined by the backend.`,'datetime-local'),choice('channel','External channel','Records how the request was submitted; this screen does not transmit it.',[['portal','Operator portal'],['edi','EDI'],['email','Email'],['phone','Phone'],['manual_import','Manual import']]),text('reason','Reason','Explain the new request or retro correction.'),evidence];
   case 'response':return [choice('parent','Request / agreement','Choose the exact external transaction being acknowledged.',[...choices(nominations(),n=>`Nomination · ${n.external_reference}`).map(([id,label])=>['n:'+id,label] as [string,string]),...choices(ownAgreements(),t=>`Imbalance · ${t.external_reference}`).map(([id,label])=>['t:'+id,label] as [string,string])]),choice('side','Responding party','Counterparty and pipeline acknowledgment are separate facts. Request records a withdrawal.',[['counterparty','Counterparty'],['pipeline','Pipeline'],['request','Request withdrawal']]),choice('decision','Decision','An approval must state an explicit quantity, including zero. Rejection/withdrawal carries no quantity.',[['approved','Approved'],['rejected','Rejected'],['withdrawn','Withdrawn']]),text('quantity','Approved total','Required for approval; leave blank for rejection or withdrawal.','text',true),reference,text('respondedAt','External response time',`Actual response time in ${o.zone()}; local recording/knowledge time stays separate.`,'datetime-local'),evidence];
   case 'nomination_apply':return [nominationChoice(),choice('profile','Acceptance policy','Schedule must fit within both requested and counterparty-confirmed quantities. A newer independent track blocks adoption.',[[view!.nominationProfile,'Manual confirmed ceiling']]),evidence];
   case 'daily_quantity':return [choice('obligationId','Delivery side','Select a retained physical obligation.',anchors),choice('status','Allocation status','Final quantities require completed delivery. Withdrawal preserves prior revisions.',[['provisional','Provisional'],['final','Final'],['withdrawn','Withdrawn']]),text('quantity','Allocated total','Native delivery quantity; blank only when withdrawing the source.','text',true),evidence];
   case 'statement':return [account(),reference,choice('status','Statement status','Source withdrawal preserves all accepted transfers.',[['provisional','Provisional'],['final','Final'],['withdrawn','Withdrawn']]),text('balance','Signed reported balance','Positive: gas owed to holder. Negative: holder owes gas. Blank only for withdrawal.','text',true),text('reportedAt','Statement report time',`Actual operator report time in ${o.zone()}.`,'datetime-local'),choice('includedMovementId','Transfers already included','Explicitly declare whether the reported number excludes transfers or includes through an exact accepted movement.',[]),evidence];
   case 'imbalance':return [choice('buyerAccountId','Buyer account','Buyer must have an eligible negative balance. One account must be owned and the other a counterparty.',choices(transferAccounts(),a=>`${a.external_reference} · ${a.holder_role}`)),choice('sellerAccountId','Seller account','Seller must have compatible positive capacity. Transfer cannot cross zero.',choices(transferAccounts(),a=>`${a.external_reference} · ${a.holder_role}`)),reference,text('quantity','Transfer quantity','Positive native quantity; equal and opposite movements are applied only after both approvals.'),text('price','Fixed unit price','Explicit signed price in the owned account’s retained quote specification. No market-price fallback.'),text('agreedAt','Agreement time',`After-flow commercial agreement time in ${o.zone()}.`,'datetime-local'),text('dueAt','Cash due time',`Explicit contractual due time in ${o.zone()}.`,'datetime-local'),text('decimalPlaces','Currency decimal places','Must match the exact retained currency’s minor-unit precision.','number'),choice('roundingPolicy','Cash rounding','Native exact calculation rounds the signed cash with ties to even.',[[view!.roundingPolicy,'Currency minor units · ties to even']]),choice('profile','Transfer profile','Offset existing opposite balances within compatible native scope; cross-zone/FX and partial reversal are TBD.',[[view!.imbalanceProfile,'Paired offsetting imbalance']]),evidence];
   case 'imbalance_apply':return [agreements(),evidence];default:return [];
  }
 }
 function selectOptions(input:HTMLSelectElement,options:[string,string][]){input.replaceChildren(new Option('Choose…',''),...options.map(([value,label])=>new Option(label,value)));if(options.length===1)input.value=options[0][0];}
 function updatePrefix(){if(kind!=='statement')return;const account=(form.elements.namedItem('accountId') as HTMLSelectElement).value,input=form.elements.namedItem('includedMovementId') as HTMLSelectElement;selectOptions(input,[['none','Excludes all recorded transfers'],...view!.state.movements.filter(m=>m.account_id===account).map(m=>[String(m.id),`Includes through transfer ${m.ordinal} · ${m.quantity}`] as [string,string])]);}
 function draw(){get('editor').hidden=!kind;reviewed=undefined;get('review').hidden=true;form.replaceChildren();times.clear();fields=specs();get('title').textContent=actions.find(a=>a[0]===kind)?.[1]??'Choose an operation';
  for(const f of fields){const label=document.createElement('label');label.append(document.createTextNode(f.label));let input:HTMLInputElement|HTMLSelectElement;if(f.options){input=document.createElement('select');selectOptions(input,f.options);}else{input=document.createElement('input');input.type=f.type??'text';if(f.type==='datetime-local')input.step='1';input.maxLength=f.name==='evidence'?500:300;}
   input.name=f.name;input.required=!f.optional;label.append(input);const small=document.createElement('small');small.textContent=f.meaning;label.append(small);form.append(label);if(f.type==='datetime-local')times.set(f.name,zonedInput(input as HTMLInputElement,o.zone,o.api));}
  if(fields.length){const button=document.createElement('button');button.textContent='Review operation';form.append(button);}
  updatePrefix();edits.clear();if(kind)get('editor').scrollIntoView({block:'nearest'});message.textContent=!kind?'':!writable()?'Read only in this context, or a retained command needs reconciliation.':!(kind==='service'?view?.permissions.canConfigure:kind==='daily_quantity'?daily?.canWrite:view?.permissions.canRecord)?'Your current role cannot record this operation.':'Select explicit terms and evidence, then preview.';sync();
 }
 function value(name:string){return(form.elements.namedItem(name) as HTMLInputElement|HTMLSelectElement).value;}
 function invalidate(){generation++;reviewed=undefined;get('review').hidden=true;sync();}
 form.addEventListener('input',invalidate);form.addEventListener('change',e=>{if((e.target as HTMLInputElement).name==='accountId')updatePrefix();invalidate();});
 form.onsubmit=event=>{event.preventDefault();void preview();};
 async function preview(){if(!kind||(!view&&kind!=='daily_quantity')||!daily||!writable()||busy||workflow?.busy())return;const epoch=++generation;busy=true;sync();try{
  const d:Record<string,unknown>={};for(const f of fields)d[f.name]=times.has(f.name)?await times.get(f.name)!.instant():value(f.name);if(disposed||epoch!==generation||o.actor()!==owner)return;
  const own=ownAccounts(),account=own.find(a=>a.id===d.accountId);
  if(kind==='daily_quantity'){const row=daily.rows.find(r=>r.obligationId===d.obligationId&&r.day===day());if(!row)throw Error('Choose a retained day and side.');reviewed={path:'gas/quantities/record',body:quantityRecord(o.context(),daily,row,{kind:'allocated',status:String(d.status),quantity:String(d.quantity),evidence:String(d.evidence)})};}
  else{
   if(!view)throw Error('Operational source data is unavailable under your current authority.');
   if(kind==='service'){d.active=d.active==='true';d.expectedPreviousId=own.find(a=>a.origin_obligation_id===d.obligationId&&a.holder_role===d.holderRole&&a.external_reference===d.externalReference)?.version_id??null;}
   if(kind==='nomination'||kind==='statement'){if(!account)throw Error('Choose a current service account.');d.serviceVersionId=account.version_id;d.expectedPreviousId=latest((kind==='nomination'?view.state.nominations:view.state.statements).filter(r=>r.account_id===d.accountId&&(kind!=='nomination'||r.external_reference===d.externalReference)))?.id??null;}
   if(kind==='nomination_apply'){const responses=view.state.responses.filter(r=>r.nomination_id===d.nominationId);d.counterpartyResponseId=latest(responses.filter(r=>r.side==='counterparty'))?.id??null;d.pipelineResponseId=latest(responses.filter(r=>r.side==='pipeline'))?.id??null;d.expectedPreviousId=view.state.nominationApplications.filter(a=>a.nomination_id===d.nominationId).at(-1)?.id??null;}
   if(kind==='statement'){d.includedMovementId=d.includedMovementId==='none'?null:d.includedMovementId;if(d.status==='withdrawn')d.balance=null;}
   if(kind==='response'){const parent=String(d.parent);delete d.parent;d.nominationId=parent.startsWith('n:')?parent.slice(2):null;d.imbalanceId=parent.startsWith('t:')?parent.slice(2):null;d.expectedPreviousId=latest(view.state.responses.filter(r=>r.nomination_id===d.nominationId&&r.imbalance_id===d.imbalanceId&&r.side===d.side))?.id??null;if(d.decision!=='approved')d.quantity=null;}
   if(kind==='imbalance'){d.buyerVersionId=view.state.accounts.find(a=>a.id===d.buyerAccountId)?.version_id;d.sellerVersionId=view.state.accounts.find(a=>a.id===d.sellerAccountId)?.version_id;d.decimalPlaces=Number(d.decimalPlaces);}
   const request={context:structuredClone(o.context()),tradeId:o.tradeId,expectedRevision:daily.expectedRevision,day:day(),kind,details:d};const result=await o.api<{previewHash:string;review:Review}>('gas/operations/preview',request);if(disposed||epoch!==generation||o.actor()!==owner)return;
   reviewed={path:kind==='service'?'gas/operations/configure':'gas/operations/record',body:{preview:request,previewHash:result.previewHash}};get('review').textContent=reviewText(result.review);
  }
  if(kind==='daily_quantity')get('review').textContent=reviewText({action:'Record allocated delivery quantity',unit:daily.rows.find(r=>r.obligationId===d.obligationId)?.quantitySpecification.code});get('review').hidden=false;message.textContent='Review the native result and evidence before accepting. Operational cash is nonportfolio and unposted.';
 }catch(error){if(!disposed)message.textContent=String(error);}finally{busy=false;if(!disposed)sync();}}
 function reviewText(r:Review){const lines=[r.action??'Review operation',`Delivery day: ${day()}`];
  for(const [key,label] of [['phase','Request phase'],['quantity','Requested total'],['confirmed','Confirmed total'],['scheduled','Scheduled total'],['buyerBalanceAfter','Buyer balance after'],['sellerBalanceAfter','Seller balance after']] as const)if(r[key]!==undefined)lines.push(`${label}: ${r[key]}${key==='phase'?'':' '+(r.unit??'')}`);
  if(r.cash!==undefined)lines.push(`Signed cash: ${r.cash} ${r.currency??''}`);if(r.priceMultiplier)lines.push(`Retained quote multiplier: ${r.priceMultiplier}`);
  for(const f of fields){const input=form.elements.namedItem(f.name) as HTMLInputElement|HTMLSelectElement;const shown=input instanceof HTMLSelectElement?input.selectedOptions[0]?.textContent:input.value;lines.push(`${f.label}: ${shown||'None'}`);}
  if(r.scope)lines.push('Commercial cash: nonportfolio, unposted');return lines.join('\n');}
 get('confirm').onclick=()=>{if(!reviewed||!writable()||busy||!workflow||workflow.busy())return;const command={path:String(reviewed.path),body:structuredClone(reviewed.body as Record<string,unknown>),operationId:crypto.randomUUID(),label:actions.find(a=>a[0]===kind)?.[1]??'Gas delivery operation'};busy=true;sync();void(async()=>{try{await workflow.queue(command);}catch(error){if(!disposed)message.textContent=String(error);}finally{busy=false;if(!disposed){reviewed=undefined;get('review').hidden=true;sync();}}})();};
 function history(){if(!view||!daily)return;const s=view.state;renderRegularTable(get('history'),'gas-operations:'+o.tradeId,o.tradeId,['Record','Reference','State / value','Evidence'],[
  ...s.accounts.filter(a=>a.trade_id===o.tradeId).map(a=>['Service',String(a.external_reference),`Terms ${a.revision} · ${a.active?'active':'inactive'}`,String(a.version_evidence??'')]),
  ...daily.rows.filter(r=>r.day===day()).flatMap(r=>r.heads.map(h=>[r.sideCode,h.kind,`${h.status} · ${h.quantity??'withdrawn'}`,h.evidence])),
  ...s.nominations.map(n=>['Submitted request',String(n.external_reference),String(n.quantity),String(n.evidence)]),...s.responses.map(r=>['Acknowledgment',String(r.external_reference),`${r.side} · ${r.decision} · ${r.quantity??''}`,String(r.evidence)]),
  ...s.statements.map(s=>['Statement',String(s.external_reference),`${s.status} · ${s.balance??'withdrawn'}`,String(s.evidence)]),...view.balances.map(b=>['Current balance',String(s.accounts.find(a=>a.id===b.accountId)?.external_reference??b.accountId),b.amount??'Unassessed',b.refusal??'Reported balance plus later accepted movements']),
  ...s.agreements.map(t=>['Imbalance agreement',String(t.external_reference),`${t.quantity} @ ${t.price} · nonportfolio`,String(t.evidence)]),...s.imbalanceApplications.map(a=>['Accepted transfer',String(a.agreement_id),`${a.cash_amount} · unposted cash`,String(a.evidence)])],[],()=>busy);}
 function clearRows(keep=true){if(keep&&grid){filters=grid.state();if(grid.ready())layout=grid.layout();}grid?.destroy();grid=undefined;get('rows').replaceChildren();}
 async function chooseDelivery(index:number){const next=daily?.rows[index];if(!next||busy||workflow?.busy()||workflow?.pending())return false;const key=gasDeliveryKey(next);if(key===selectedKey)return true;
  if(edits.edited()){message.textContent='Keep editing, or discard inputs with Refresh delivery rows before choosing another side or day.';return false;}
  selectedKey=key;kind=undefined;const epoch=beginRead();grid?.selectRow(index);await loadOperations(epoch);if(valid(epoch)){busy=false;sync();}return valid(epoch)&&selectedKey===key;
 }
 function drawRows(){if(!daily)return;const view=readPhysicalDelivery(daily.delivery,{tradeId:o.tradeId,revision:daily.expectedRevision});get('zone').textContent=o.zone();
  grid=renderPhysicalDeliveryGrid(get('rows'),'gas-delivery-rows',o.tradeId,view,o.zone(),()=>busy||workflow?.busy()===true||!!workflow?.pending(),{
   initial:filters,layout,onLayout:next=>layout=next,onRowOpen:chooseDelivery,onRowSelect:chooseDelivery,
  });if(selectedKey)grid.selectRow(daily.rows.findIndex(r=>gasDeliveryKey(r)===selectedKey));
 }

 const valid=(epoch:number)=>!disposed&&epoch===generation&&o.actor()===owner;
 function beginRead(){busy=true;view=undefined;reviewed=undefined;get('history').replaceChildren();
  // Removing a focused input can emit a final change. Start the new read after cleanup.
  form.replaceChildren();edits.clear();const epoch=++generation;get('editor').hidden=!kind;get('review').textContent='';get('review').hidden=true;sync();return epoch;
 }
 async function loadOperations(epoch:number){try{
  const operations=await o.api<View>('gas/operations/read',{context:o.context(),tradeId:o.tradeId,day:day()});if(!valid(epoch))return;view=operations;history();if(kind)draw();else message.textContent=writable()?'Choose an action in this stage.':'This delivery history is read only in the selected context.';
 }catch(error){if(valid(epoch)){view=undefined;message.textContent=String(error);}}}
 async function read(){
  if(disposed||o.actor()!==owner)return;daily=undefined;clearRows();const epoch=beginRead();
  try{const data=await o.api<GasQuantityView>('gas/quantities/read',{context:o.context(),tradeId:o.tradeId,selectionPolicy:'operational_projection_v1',includeHistory:false});if(!valid(epoch))return;
   const delivery=readPhysicalDelivery(data.delivery,{tradeId:o.tradeId,revision:data.expectedRevision});
   if(delivery.rows.length!==data.rows.length||delivery.rows.some((r,i)=>r.key!==gasDeliveryKey(data.rows[i])))throw Error('The delivery rows do not match their native recording scope.');
   daily=data;
   selectedKey=data.rows.some(r=>gasDeliveryKey(r)===selectedKey)?selectedKey:data.rows.length===1?gasDeliveryKey(data.rows[0]):undefined;drawRows();
   if(!selectedDelivery()){message.textContent='Select a delivery row to work with its day and side.';return;}await loadOperations(epoch);
  }catch(error){if(valid(epoch)){view=undefined;message.textContent=String(error);}}
  finally{if(valid(epoch)){busy=false;sync();}}
 }
 get('refresh').onclick=()=>{if(busy||workflow?.busy()||workflow?.pending())return;edits.clear();invalidate();void read();};void read();
 return{afterFlowHost,sync,pending:()=>!!workflow?.pending()||busy,unsaved:()=>edits.edited(),destroy(){disposed=true;generation++;clearRows(false);root.remove();}};
}
