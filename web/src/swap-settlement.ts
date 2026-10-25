import {renderRegularTable} from './regular-table.js';
import {displayInstant} from './market-view.js';
import type {RequestCommand} from './pending-command.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Context=Record<string,unknown>;
type Payment={membershipOrdinal:number;ordinal:number;deliveryFrom:string;deliveryTo:string;paymentAt:string;completionAt:string;state:string;final:boolean;realized:boolean;contractualAmount:string;remainingValue:string};
type Balance={determinationId:string;membershipOrdinal:number;deliveryFrom:string;deliveryTo:string;ordinal:number;dueAt:string;amount:string;applied:string;outstanding:string};
type Cash={id:string;determinationId:string|null;recordKind:string;allocations:{determinationId:string;amount:string}[];amount:string;reversalAmount:string;effectiveAt:string;reference:string;canReverse:boolean};
type Detail={state:{tradeId:string;revision:number;tradeReference:string};context:{contextId:string};asOf:string;canAmend:boolean;currency:unknown;currencyLabel:string;decimalPlaces:number;roundingPolicy:string;
 settlement:{version:number;occurrences:unknown[];determinations:unknown[]};balances:Balance[];cashMovements:Cash[];
 valuation:Record<string,string|Payment[]>&{payments:Payment[]}};
type Preview={previewHash:string;expectedVersion:number;previousTotal:string;proposedContribution:string;isCorrection:boolean;inputsChanged:boolean;calculation:{amount:string;exactAmount:string;roundingEffect:string;resolvedQuantity:string;paymentAt:string;prices:unknown[]}};
const duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1',correctionPolicy='swap_explicit_correction_due_v1';

export function setupSwapSettlement(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;context:()=>Context;
 queue:(command:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined}){
 const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
 const dialog=node('dialog');dialog.id='swap-settlement';dialog.className='trade-editor swap-editor';dialog.setAttribute('aria-labelledby','swap-settlement-title');
 const heading=node('div');heading.className='dialog-heading';const title=node('h2','Swap settlement');title.id='swap-settlement-title';
 const close=node('button','Close');close.type='button';heading.append(title,close);
 const status=node('p');status.setAttribute('role','status');const error=node('p');error.setAttribute('role','alert');
 const refresh=node('button','Refresh settlement');refresh.type='button';refresh.id='swap-settlement-refresh';
 const summary=node('div'),payments=node('div'),evidence=node('section'),balances=node('div'),cashHistory=node('div'),pnl=node('section');balances.id='swap-settlement-balances';cashHistory.id='swap-settlement-cash';
 const form=node('form');form.id='swap-settlement-payment-form';const target=node('select');target.id='swap-settlement-contribution';
 const amount=node('input');amount.id='swap-settlement-amount';amount.inputMode='decimal';amount.required=true;
 const effective=node('input');effective.id='swap-settlement-effective';effective.placeholder='YYYY-MM-DDTHH:mm:ssZ';effective.required=true;
 const reference=node('input');reference.id='swap-settlement-reference';reference.required=true;reference.maxLength=200;
 const label=(host:HTMLElement,text:string,field:HTMLElement)=>{const wrap=node('div'),caption=node('label',text);caption.htmlFor=field.id;wrap.append(caption,field);host.append(wrap);};
 form.append(node('h3','Record contractual cash'));label(form,'Apply to original payment or adjustment',target);label(form,'Signed cash amount',amount);label(form,'Cash effective instant · UTC',effective);label(form,'Payment reference',reference);
 const submit=node('button','Record payment');submit.id='swap-settlement-pay';submit.type='submit';form.append(submit);
 const reversal=node('select');reversal.id='swap-settlement-reversal';label(form,'Original cash movement to reverse',reversal);
 const reverse=node('button','Reverse selected movement');reverse.id='swap-settlement-reverse';reverse.type='button';form.append(reverse);
 form.append(node('p','A reversal uses the entered effective instant and reference and records the opposite cash amount. Each entry is fully allocated to one selected contribution.'));
 const netForm=node('form');netForm.id='swap-net-form';netForm.append(node('h3','Settle selected gross contributions within this trade'),node('p','Select each contribution and enter its signed allocation explicitly. The backend requires the same exact parties, currency, due instant and supported terms. A zero net amount records a noncash discharge.'));
 const netSelection=node('div');netSelection.id='swap-net-selection';netForm.append(netSelection);
 const netAmount=node('input');netAmount.id='swap-net-amount';netAmount.inputMode='decimal';netAmount.required=true;
 const netEffective=node('input');netEffective.id='swap-net-effective';netEffective.placeholder='YYYY-MM-DDTHH:mm:ssZ';netEffective.required=true;
 const netReference=node('input');netReference.id='swap-net-reference';netReference.required=true;netReference.maxLength=200;
 label(netForm,'Signed net cash amount · enter 0 for a zero-net discharge',netAmount);label(netForm,'Settlement effective instant · UTC',netEffective);label(netForm,'Settlement reference',netReference);
 const netPreview=node('button','Preview gross / net allocation');netPreview.type='submit';netForm.append(netPreview);
 const netReview=node('section');netReview.id='swap-net-review';
 let netRows:{id:string;selected:HTMLInputElement;amount:HTMLInputElement}[]=[];
 const baseline=node('button','Use this view as opening');baseline.id='swap-settlement-opening';baseline.type='button';
 const reconcile=node('button','Reconcile opening to this view');reconcile.id='swap-settlement-reconcile';reconcile.type='button';
 const openingSnapshot=node('select');openingSnapshot.id='swap-pnl-opening-snapshot';const captureOpening=node('button','Capture selected opening snapshot');captureOpening.type='button';captureOpening.id='swap-pnl-capture-opening';
 const contractPolicy=node('select');contractPolicy.id='swap-pnl-contract-policy';contractPolicy.append(new Option('Choose a contractual-change policy…',''),new Option('Unchanged contracts · bridge v1','reject_contract_changes_v1'),new Option('New trades / future amendments · closing-context v1','closing_context_contract_changes_v1'));
 const openingLabel=node('p'),bridge=node('div');bridge.id='swap-settlement-bridge';pnl.append(node('h3','Trading P&L reconciliation'),baseline);label(pnl,'Opening market snapshot',openingSnapshot);pnl.append(captureOpening,openingLabel);label(pnl,'Contractual changes valued under',contractPolicy);pnl.append(reconcile,bridge);
 pnl.append(node('p','Closing-context v1 values prior and revised terms in the closing market. New-trade economics, amendments and continuing economics reconcile to total trading P&L; payments and netting do not create earnings. Book transfers and unsupported transformations are refused.'));
 dialog.append(heading,status,error,refresh,summary,node('h3','Contractual payment occurrences'),payments,evidence,node('h3','Determined balances and adjustments'),balances,
  form,netForm,netReview,node('h3','Cash history'),cashHistory,pnl);document.body.append(dialog);
 let detail:Detail|undefined,tradeId:string|undefined,selectedKey='',generation=0;
 let opening:{tradeId:string;context:Context;asOf:string}|undefined;
 const key=()=>JSON.stringify(options.context()),context=()=>({...options.context(),convention:'independent_side_financial_swap_v2'});
 const current=()=>{if(!detail||selectedKey!==key())throw Error('The financial context changed. Refresh and review settlement.');return detail;};
 const canWrite=()=>!!detail?.canAmend&&selectedKey===key()&&!options.busy()&&!options.pending();
 function sync(){
  submit.disabled=!canWrite();netPreview.disabled=!canWrite();for(const field of [netAmount,netEffective,netReference,...netRows.flatMap(r=>[r.selected,r.amount])])field.disabled=!canWrite();const netRecord=netReview.querySelector<HTMLButtonElement>('#swap-net-record');if(netRecord)netRecord.disabled=!canWrite();reverse.disabled=!canWrite()||!reversal.value;refresh.disabled=options.busy();baseline.disabled=!detail||selectedKey!==key()||options.busy();
  reconcile.disabled=!detail||selectedKey!==key()||!opening||opening.tradeId!==tradeId||options.busy();
  for(const field of [target,amount,effective,reference,reversal])field.disabled=!canWrite();
  payments.querySelectorAll<HTMLButtonElement>('button[data-eligible]').forEach(button=>button.disabled=!canWrite()||button.dataset.eligible!=='true');
  const determine=evidence.querySelector<HTMLButtonElement>('#swap-settlement-determine');if(determine)determine.disabled=!canWrite()||determine.dataset.eligible!=='true';
 }
 function invalidate(){generation++;netReview.replaceChildren();evidence.replaceChildren();bridge.replaceChildren();sync();}
 async function run(work:()=>Promise<void>){await options.act(async()=>{error.textContent='';try{await work();}catch(e){invalidate();error.textContent=(e as Error).message;}});sync();}
 function contributionName(report:Detail,id:string){const b=report.balances.find(b=>b.determinationId===id);return b?`Side ${b.membershipOrdinal+1} · ${b.deliveryFrom} to ${b.deliveryTo} · ${b.ordinal===1?'original':'adjustment '+String(b.ordinal-1)}`:id;}
 function allocationEvidence(report:Detail,allocations:Cash['allocations']){const details=node('details'),summary=node('summary',allocations.map(a=>`${contributionName(report,a.determinationId)}: ${a.amount}`).join('; ')),raw=node('pre');raw.className='swap-evidence';raw.textContent=JSON.stringify(allocations,null,2);details.append(summary,raw);return details;}
 function table(host:HTMLElement,id:string,headers:string[],rows:(string|HTMLElement)[][],numeric:number[]=[]){host.replaceChildren();renderRegularTable(host,id,JSON.stringify(detail?.context),headers,rows,numeric,options.busy);}
 async function load(){
  if(!tradeId)return;invalidate();const version=generation,scope=key();const result=await options.api<Detail>('swaps/settlement/inspect',{context:context(),tradeId,revision:null,duePolicy,recognitionPolicy});
  if(version!==generation||scope!==key())return;detail=result;selectedKey=scope;title.textContent=`Swap settlement · ${result.state.tradeReference}`;
  status.textContent=`As of ${displayInstant(result.asOf,options.zone())}. ${result.canAmend?'Working view.':'Read only historical view.'} Currency: ${result.currencyLabel}. Contractual precision: ${result.decimalPlaces} decimal places, ties to even.`;
  summary.replaceChildren();const metrics=[['Remaining contractual value','remainingValue'],['Determined outstanding','determinedOutstanding'],['PV of determined outstanding','determinedPv'],['Due / overdue balance','dueBalance'],['Realized trading P&L','realized'],['Unrealized trading P&L','unrealized'],['Actual net cash received','netCash'],['Corrections','corrections'],['Rounding effect','roundingEffect'],['Estimated remaining PV','estimatedRemainingPv']];
  table(summary,'swap-settlement-summary',['Measure','Amount'],metrics.map(([name,field])=>[name,String(result.valuation[field])]),[1]);
  summary.append(node('p','Amounts retain the contractual currency. Determined PV is included in remaining value; due cash is part of outstanding. Corrections and rounding are overlapping detail. Payments do not recognize profit again.'));
  table(payments,'swap-settlement-payments',['Side','Delivery start','Delivery end · exclusive','Delivery completion','Payment instant','State',`Signed cash · ${result.currencyLabel}`,`Remaining PV · ${result.currencyLabel}`,'Realized','Action'],
   result.valuation.payments.map(p=>{const calculate=node('button',p.state==='Undetermined'?'Calculate final payment':'Review correction');calculate.type='button';calculate.dataset.membership=String(p.membershipOrdinal);calculate.dataset.payment=String(p.ordinal);calculate.dataset.eligible=String(p.final&&result.canAmend);calculate.disabled=!p.final||!result.canAmend;
    calculate.onclick=()=>void run(()=>preview(p));return[String(p.membershipOrdinal+1),p.deliveryFrom,p.deliveryTo,displayInstant(p.completionAt,options.zone()),displayInstant(p.paymentAt,options.zone()),p.state,p.contractualAmount,p.remainingValue,p.realized?'Yes':'No',calculate];}),[0,6,7]);
  table(balances,'swap-settlement-balances',['Side','Delivery start','Contribution','Due instant','Determined','Applied','Outstanding'],result.balances.map(b=>[String(b.membershipOrdinal+1),b.deliveryFrom,b.ordinal===1?'Original':'Adjustment',displayInstant(b.dueAt,options.zone()),b.amount,b.applied,b.outstanding]),[0,4,5,6]);
  netRows=[];
  table(netSelection,'swap-net-candidates',['Select','Side / period / contribution','Due instant','Gross outstanding','Explicit signed allocation'],result.balances.filter(b=>b.outstanding!=='0').map(b=>{
   const selected=node('input');selected.type='checkbox';selected.setAttribute('aria-label',`Select side ${b.membershipOrdinal+1} ${b.deliveryFrom} contribution ${b.ordinal}`);
   const allocation=node('input');allocation.type='text';allocation.inputMode='decimal';allocation.setAttribute('aria-label',`Allocation to side ${b.membershipOrdinal+1} ${b.deliveryFrom} contribution ${b.ordinal}`);
   netRows.push({id:b.determinationId,selected,amount:allocation});return[selected,`Side ${b.membershipOrdinal+1} · ${b.deliveryFrom} · ${b.ordinal}`,displayInstant(b.dueAt,options.zone()),b.outstanding,allocation];}),[3]);
  target.replaceChildren(new Option('Choose a contribution…',''),...result.balances.filter(b=>b.outstanding!=='0').map(b=>new Option(`Side ${b.membershipOrdinal+1} · ${b.deliveryFrom} · ${b.ordinal===1?'original':'adjustment'} · outstanding ${b.outstanding}`,b.determinationId)));
  reversal.replaceChildren(new Option('Choose an original cash movement…',''),...result.cashMovements.filter(p=>p.canReverse).map(p=>new Option(`${p.reference} · ${p.amount} · ${displayInstant(p.effectiveAt,options.zone())}`,p.id)));
  table(cashHistory,'swap-settlement-cash',['Effective instant','Record','Signed cash','Reference','Gross allocations'],result.cashMovements.map(p=>[displayInstant(p.effectiveAt,options.zone()),p.recordKind==='zero_net_discharge_v1'?'Noncash zero-net discharge':'Cash movement',p.amount,p.reference,allocationEvidence(result,p.allocations)]),[2]);
  const workspace=await options.api<{frames:{id:string;at:string}[]}>('workspace');
  if(version!==generation||scope!==key())return;
  const selectedOpening=openingSnapshot.value;openingSnapshot.replaceChildren(new Option('Choose…',''),...workspace.frames.map(f=>new Option(displayInstant(f.at,options.zone()),f.id)));openingSnapshot.value=selectedOpening;
  openingLabel.textContent=opening&&opening.tradeId===tradeId?`Opening: ${displayInstant(opening.asOf,options.zone())} · retained knowledge context.`:'Capture a view, then select the closing financial context and refresh.';
  sync();
 }
 async function preview(payment:Payment){
  const report=current(),version=generation,scope=key();evidence.replaceChildren();
  const request={context:context(),tradeId:report.state.tradeId,expectedRevision:report.state.revision,membershipOrdinal:payment.membershipOrdinal,paymentOrdinal:payment.ordinal,
   currency:report.currency,decimalPlaces:report.decimalPlaces,roundingPolicy:report.roundingPolicy};
  const value=await options.api<Preview>('swaps/settlement/preview',request);if(version!==generation||scope!==key())return;
  if(value.expectedVersion!==report.settlement.version){await load();throw Error('Settlement history changed. Recalculate and review the current contribution.');}
  const correction=value.isCorrection;
  const heading=node('h3',correction?'Correction calculation':'Final gross payment calculation');
  const text=node('p',`${correction?`Previous determined total ${value.previousTotal} · revised total ${value.calculation.amount} · adjustment ${value.proposedContribution}`:`Signed gross cash ${value.proposedContribution}`} ${report.currencyLabel} · exact ${value.calculation.exactAmount} · rounding ${value.calculation.roundingEffect} · quantity ${value.calculation.resolvedQuantity}.`);
  const due=node('input');due.id='swap-settlement-correction-due';due.placeholder='YYYY-MM-DDTHH:mm:ssZ';due.required=correction;
  const determine=node('button',correction?'Record correction adjustment':'Determine gross payment');determine.id='swap-settlement-determine';determine.type='button';determine.dataset.eligible=String(value.inputsChanged);determine.disabled=!value.inputsChanged;
  determine.onclick=()=>void run(async()=>{current();if(!value.inputsChanged||!report.canAmend||version!==generation||scope!==key()||options.pending())throw Error('The preview changed or has no new final inputs. Recalculate before submitting.');if(correction&&!due.value)throw Error('Enter an explicit correction due instant.');
   await options.queue({operationId:crypto.randomUUID(),path:'swaps/settlement/determine',body:{preview:request,expectedVersion:report.settlement.version,previewHash:value.previewHash,
    duePolicy,recognitionPolicy,correctionPolicy,correctionDueAt:correction?due.value:null}});await load();});
  const details=node('details'),caption=node('summary','Calculation and final fixing evidence'),raw=node('pre');raw.className='swap-evidence';raw.textContent=JSON.stringify(value.calculation,null,2);details.append(caption,raw);
  evidence.append(heading,text);if(!value.inputsChanged)evidence.append(node('p','These final fixing inputs are already determined.'));if(correction)label(evidence,'Correction due instant · UTC',due);else evidence.append(node('p',`Contractual payment: ${displayInstant(value.calculation.paymentAt,options.zone())}.`));evidence.append(details,determine);
 }
 async function record(reversing:boolean){
  const report=current();if(!report.canAmend||options.pending())throw Error('Refresh an authorized working view before recording cash.');
  if(!effective.value||!reference.value)throw Error('Enter a cash effective instant and reference.');
  const movement=reversing?report.cashMovements.find(p=>p.id===reversal.value&&p.canReverse):undefined;
  if(movement&&!movement.determinationId){await previewNet(movement);return;}
  if(reversing&&!movement)throw Error('Choose an original cash movement.');
  if(!reversing&&(!target.value||!amount.value))throw Error('Choose a contribution and enter the signed cash amount.');
  await options.queue({operationId:crypto.randomUUID(),path:'swaps/settlement/payment',body:{context:context(),tradeId:report.state.tradeId,expectedRevision:report.state.revision,
   expectedVersion:report.settlement.version,determinationId:movement?.determinationId??target.value,amount:movement?.reversalAmount??amount.value,currency:report.currency,
   effectiveAt:effective.value,reference:reference.value,reversesId:movement?.id??null}});amount.value='';reference.value='';effective.value='';await load();
 }
 async function previewNet(reversing?:Cash){
  const report=current(),version=generation,scope=key();netReview.replaceChildren();
  const request={context:context(),tradeId:report.state.tradeId,expectedRevision:report.state.revision,expectedVersion:report.settlement.version,
   currency:report.currency,amount:reversing?null:netAmount.value,allocations:reversing?[]:netRows.filter(r=>r.selected.checked).map(r=>({determinationId:r.id,amount:r.amount.value})),policy:'explicit_same_due_swap_net_v1',reversesId:reversing?.id??null};
  const value=await options.api<{previewHash:string;amount:string;grossPaid:string;grossReceived:string;recordKind:string;allocations:{determinationId:string;amount:string;remaining:string}[]}>('swaps/settlement/net-preview',request);
  if(version!==generation||scope!==key())return;
  netReview.append(node('h3',reversing?'Review exact settlement reversal':'Review selected gross allocations'),node('p',`Gross paid ${value.grossPaid} · gross received ${value.grossReceived} · net ${value.amount} ${report.currencyLabel}. ${value.recordKind==='zero_net_discharge_v1'?'Noncash discharge; no cash movement.':''}`));
  const lines=node('div');netReview.append(lines);table(lines,'swap-net-lines',['Contribution identity','Signed allocation','Remaining balance'],value.allocations.map(a=>[contributionName(report,a.determinationId),a.amount,a.remaining]),[1,2]);
  const at=reversing?effective.value:netEffective.value,ref=reversing?reference.value:netReference.value;
  const record=node('button',reversing?'Record reviewed reversal':'Record reviewed net settlement');record.type='button';record.id='swap-net-record';
  record.onclick=()=>void run(async()=>{current();if(version!==generation||scope!==key()||!report.canAmend||options.pending())throw Error('The allocation review changed; preview again.');
   await options.queue({operationId:crypto.randomUUID(),path:'swaps/settlement/net',body:{preview:request,previewHash:value.previewHash,effectiveAt:at,reference:ref}});netForm.reset();if(reversing){amount.value='';effective.value='';reference.value='';}await load();});netReview.append(record);const lineage=node('details'),caption=node('summary','Exact allocation lineage and policy'),raw=node('pre');raw.className='swap-evidence';raw.textContent=JSON.stringify(value,null,2);lineage.append(caption,raw);netReview.append(lineage);
 }
 netForm.onsubmit=e=>{e.preventDefault();void run(()=>previewNet());};
 for(const event of ['input','change']){netForm.addEventListener(event,()=>invalidate());form.addEventListener(event,()=>{generation++;netReview.replaceChildren();});}
 form.onsubmit=e=>{e.preventDefault();void run(()=>record(false));};reverse.onclick=()=>void run(()=>record(true));reversal.onchange=sync;
 baseline.onclick=()=>{const report=current();invalidate();opening={tradeId:report.state.tradeId,asOf:report.asOf,context:{...context(),knowledgeMode:'retained',contextId:report.context.contextId}};openingLabel.textContent=`Opening: ${displayInstant(report.asOf,options.zone())} · retained knowledge context.`;sync();};
 captureOpening.onclick=()=>void run(async()=>{const report=current();invalidate();const version=generation,scope=key();if(!openingSnapshot.value)throw Error('Select the opening market snapshot.');const selected={...context(),snapshotId:openingSnapshot.value,knowledgeMode:'current',contextId:null};const saved=await options.api<{context:{contextId:string};asOf:string}>('swaps/catalog',selected);if(version!==generation||scope!==key())return;opening={tradeId:report.state.tradeId,asOf:saved.asOf,context:{...selected,knowledgeMode:'retained',contextId:saved.context.contextId}};openingLabel.textContent=`Opening: ${displayInstant(saved.asOf,options.zone())} · retained knowledge context.`;sync();});
 contractPolicy.onchange=invalidate;openingSnapshot.onchange=invalidate;
 reconcile.onclick=()=>void run(async()=>{const report=current();if(!contractPolicy.value)throw Error('Choose the explicit contractual-change valuation policy.');if(!opening||opening.tradeId!==tradeId)throw Error('Capture the opening view first.');const version=generation,scope=key(),policy=contractPolicy.value;
  const value=await options.api<{report:{trades:{bridge:Record<string,string>;transitions:{membershipOrdinal:number;deliveryFrom:string;deliveryTo:string;openingState:string;closingState:string;forecastToFinal:boolean;openingPv:string;closingPv:string}[]}[];overlaps:string}}>('swaps/settlement/pnl',{opening:opening.context,closing:{...context(),knowledgeMode:'retained',contextId:report.context.contextId},tradeIds:[tradeId],bookIds:[],contractChanges:contractPolicy.value,duePolicy,recognitionPolicy});
  if(version!==generation||scope!==key()||policy!==contractPolicy.value)return;
  const labels:Record<string,string>={newTradeEconomics:'New-trade economics at closing',contractualAmendment:'Contractual amendment at closing',continuingEconomics:'Continuing economics under prior terms',economicPnl:'Economic P&L',remainingValueMovement:'Change in remaining value',netCashMovement:'Change in cumulative contractual cash',periodCashReceived:'Contractual cash received during the period',priorCashKnowledgeAdjustment:'Prior cash · change in knowledge',realizedTransfer:'Realized trading transfer',unrealizedMovement:'Change in unrealized trading P&L',contractualCashMovement:'Change in contractual cash',discountEffectMovement:'Change in discount effect',correctionsMovement:'Correction movement · overlapping detail',roundingEffectMovement:'Rounding movement · overlapping detail'};
  table(bridge,'swap-settlement-bridge',['Bridge measure',`Amount · ${report.currencyLabel}`],Object.entries(value.report.trades[0].bridge).map(([key,amount])=>[labels[key]??key,amount]),[1]);bridge.append(node('p',value.report.overlaps));
  const transitions=node('div');bridge.append(node('h3','Period transitions'),transitions);
  table(transitions,'swap-settlement-transitions',['Side','Delivery start','Delivery end · exclusive','Opening state','Closing state','Forecast became final','Opening PV','Closing PV'],value.report.trades[0].transitions.map(p=>[String(p.membershipOrdinal+1),p.deliveryFrom,p.deliveryTo,p.openingState,p.closingState,p.forecastToFinal?'Yes':'No',p.openingPv,p.closingPv]),[0,6,7]);});
 close.onclick=()=>dialog.close();refresh.onclick=()=>void run(load);
 return {async open(id:string){if(tradeId!==id){form.reset();netForm.reset();netReview.replaceChildren();netRows=[];if(opening?.tradeId!==id)opening=undefined;}tradeId=id;error.textContent='';detail=undefined;await load();if(!dialog.open)dialog.showModal();},
  reset(){invalidate();detail=undefined;tradeId=undefined;form.reset();netForm.reset();netReview.replaceChildren();netRows=[];dialog.close();},sync,
  refresh(){invalidate();if(dialog.open){detail=undefined;status.textContent='Financial context changed. Refresh settlement before continuing.';}},
  async liveRefresh(){if(dialog.open)await load();},isOpen:()=>dialog.open,
  unsaved:()=>dialog.open&&!!(amount.value||effective.value||reference.value||netAmount.value||netEffective.value||netReference.value||netRows.some(r=>r.selected.checked||r.amount.value)||netReview.childElementCount),
  shown:()=>dialog.open&&detail?{tradeId:detail.state.tradeId,instrumentType:'financial_swap' as const,revision:detail.state.revision,reference:detail.state.tradeReference}:undefined};
}
