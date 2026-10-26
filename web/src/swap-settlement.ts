import {renderRegularTable,tableFilterControls} from './regular-table.js';
import {trackEdits} from './edit-tracking.js';
import {knownTradeRejection} from './trade-requests.js';
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
/** The measures a settlement lifecycle states (D215), in the words its view and the panel's Terms give them. */
export const lifecycleMeasures=[['Remaining contractual value','remainingValue'],['Determined outstanding','determinedOutstanding'],['PV of determined outstanding','determinedPv'],['Due / overdue balance','dueBalance'],['Realized trading P&L','realized'],['Unrealized trading P&L','unrealized'],['Actual net cash received','netCash'],['Corrections','corrections'],['Rounding effect','roundingEffect'],['Estimated remaining PV','estimatedRemainingPv']] as const;

/**
 * A financial swap's settlement and P&L view (D215), which the side panel hosts as it hosts the swap ticket (D221): its occurrences,
 * determinations, balances, cash, net settlement within the trade and P&L bridge (D216), and the commands that write them. `host` is the
 * panel's form part; the view is kept on the page's shelf until it opens there, and goes back when it is reset. What is typed into its
 * payment and net forms, and a correction's due instant, are edits. A command it sends that goes through, or one the page settles for it,
 * makes the page read the view again.
 */
export function setupSwapSettlement(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;context:()=>Context;
 queue:(command:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined;host:()=>HTMLElement;unsavedChanged:()=>void;
 ask:(question:{text:string;keep:string;go:string})=>Promise<boolean>}){
 const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
 const shelf=document.getElementById('editor-shelf')!,root=node('div');root.id='swap-settlement';root.className='editing swap-settlement-editing';root.hidden=true;root.setAttribute('aria-labelledby','swap-settlement-title');
 const mounted=()=>root.parentElement!==shelf;
 const heading=node('div');heading.className='swap-settlement-heading';const title=node('h3','Settlement and P&L');title.id='swap-settlement-title';heading.append(title);
 const status=node('p');status.setAttribute('role','status');const error=node('p');error.setAttribute('role','alert');
 const refresh=node('button','Refresh settlement');refresh.type='button';refresh.id='swap-settlement-refresh';
 const summary=node('div'),payments=node('div'),evidence=node('section'),balances=node('div'),cashHistory=node('div'),pnl=node('section');payments.id='swap-settlement-payments';balances.id='swap-settlement-balances';cashHistory.id='swap-settlement-cash';
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
 root.append(heading,status,error,refresh,summary,node('h3','Contractual payment occurrences'),payments,evidence,node('h3','Determined balances and adjustments'),balances,
  form,netForm,netReview,node('h3','Cash history'),cashHistory,pnl);shelf.append(root);
 const edits=trackEdits(form,[],options.unsavedChanged),netEdits=trackEdits(netForm,[tableFilterControls],options.unsavedChanged);
 // A correction's due instant is entered in the calculation it was asked for, and goes with it: an action of the view that replaces
 // the calculation, another payment's, a refresh or a cash or net record that reads the view again, asks first, before the page is busy.
 const dueEdits=trackEdits(evidence,[],options.unsavedChanged);
 const dueGivenUp=async()=>!dueEdits.edited()||await options.ask({text:'Discard the correction due instant you entered?',keep:'Keep it',go:'Discard it'});
 function replacing(work:()=>Promise<void>){void (async()=>{if(options.busy()||!await dueGivenUp())return;await run(work);})();}
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
 function invalidate(){generation++;netReview.replaceChildren();evidence.replaceChildren();dueEdits.clear();bridge.replaceChildren();sync();}
 // A failed action drops the calculation shown, as its inputs may have changed. A determination that fails keeps the calculation it was
 // asked from, with its due instant, when it was not sent or the backend refused it for good, and says why; any other failure of it has
 // read the view again (`sendCommand`). A net review, an opening or a reconciliation only reads: one that fails keeps the calculation.
 async function run(work:()=>Promise<void>,keep=false){await options.act(async()=>{error.textContent='';try{await work();}catch(e){if(!keep)invalidate();error.textContent=(e as Error).message;}});sync();}
 // What each command sent from the view consumes of its forms, by operation: cleared once the command is known to have gone through,
 // at once or when the page settles it later, as Check status or Retry does for one whose outcome was not known (`settle`). A form left
 // holding nothing holds no edit.
 const consumption=new Map<string,()=>void>();
 const paymentEntered=()=>[target,amount,effective,reference,reversal].some(field=>field.value);
 const netEntered=()=>[netAmount,netEffective,netReference].some(field=>field.value)||netRows.some(r=>r.selected.checked||r.amount.value);
 function settle(operationId:string|undefined){
  if(operationId===undefined)return;const consumed=consumption.get(operationId);if(!consumed)return;
  consumption.delete(operationId);consumed();if(!paymentEntered())edits.clear();if(!netEntered())netEdits.clear();
 }
 // Sends a settlement command through the page. A refusal for good wrote nothing, and consumed nothing; any other failure may follow a
 // command that went through, as when the Blotter could not be read after it, so the view is read again before the failure is said.
 async function sendCommand(command:RequestCommand,consumes:()=>void=()=>{}){
  consumption.set(command.operationId,consumes);
  try{await options.queue(command);}
  catch(failure){const code=(failure as {code?:string}).code;if(knownTradeRejection(code))consumption.delete(command.operationId);else await load().catch(()=>undefined);throw failure;}
 }
 function contributionName(report:Detail,id:string){const b=report.balances.find(b=>b.determinationId===id);return b?`Side ${b.membershipOrdinal+1} · ${b.deliveryFrom} to ${b.deliveryTo} · ${b.ordinal===1?'original':'adjustment '+String(b.ordinal-1)}`:id;}
 function allocationEvidence(report:Detail,allocations:Cash['allocations']){const details=node('details'),summary=node('summary',allocations.map(a=>`${contributionName(report,a.determinationId)}: ${a.amount}`).join('; ')),raw=node('pre');raw.className='swap-evidence';raw.textContent=JSON.stringify(allocations,null,2);details.append(summary,raw);return details;}
 // Offers a select new choices, keeping the one chosen while it is still offered.
 function offer(select:HTMLSelectElement,...choices:HTMLOptionElement[]){const chosen=select.value;select.replaceChildren(...choices);select.value=choices.some(o=>o.value===chosen)?chosen:'';}
 // A column that stacks several facts of a row (`stacked`) has no column filter: its rows' text is searched instead.
 function table(host:HTMLElement,id:string,headers:string[],rows:(string|HTMLElement)[][],numeric:number[]=[],stacked:number[]=[]){host.replaceChildren();renderRegularTable(host,id,JSON.stringify(detail?.context),headers,rows,numeric,options.busy,{noColumnFilter:stacked});}
 async function load(){
  if(!tradeId)return;invalidate();const version=generation,scope=key();const result=await options.api<Detail>('swaps/settlement/inspect',{context:context(),tradeId,revision:null,duePolicy,recognitionPolicy});
  if(version!==generation||scope!==key())return;detail=result;selectedKey=scope;title.textContent=`Settlement and P&L · ${result.state.tradeReference}`;
  status.textContent=`As of ${displayInstant(result.asOf,options.zone())}. ${result.canAmend?'Working view.':'Read only historical view.'} Currency: ${result.currencyLabel}. Contractual precision: ${result.decimalPlaces} decimal places, ties to even.`;
  summary.replaceChildren();
  table(summary,'swap-settlement-summary',['Measure','Amount'],lifecycleMeasures.map(([name,field])=>[name,String(result.valuation[field])]),[1]);
  summary.append(node('p','Amounts retain the contractual currency. Determined PV is included in remaining value; due cash is part of outstanding. Corrections and rounding are overlapping detail. Payments do not recognize profit again.'));
  // The panel has no room for a column per fact (D221): what identifies a payment or a contribution is read together, with a payment's
  // action, beside the figures.
  table(payments,'swap-settlement-payments',['Payment',`Signed cash · ${result.currencyLabel}`,`Remaining PV · ${result.currencyLabel}`],
   result.valuation.payments.map(p=>{const calculate=node('button',p.state==='Undetermined'?'Calculate final payment':'Review correction');calculate.type='button';calculate.dataset.membership=String(p.membershipOrdinal);calculate.dataset.payment=String(p.ordinal);calculate.dataset.eligible=String(p.final&&result.canAmend);calculate.disabled=!p.final||!result.canAmend;
    calculate.onclick=()=>replacing(()=>preview(p));
    const occurrence=node('div');occurrence.className='swap-settlement-stack';
    occurrence.append(node('strong',`Side ${p.membershipOrdinal+1} · ${p.deliveryFrom} to ${p.deliveryTo}, end exclusive`),node('span',`${p.state} · ${p.realized?'P&L realized':'P&L not realized'}`),
     node('span',`Delivery completes ${displayInstant(p.completionAt,options.zone())} · payment ${displayInstant(p.paymentAt,options.zone())}`),calculate);
    return[occurrence,p.contractualAmount,p.remainingValue];}),[1,2],[0]);
  table(balances,'swap-settlement-balances',['Contribution','Determined','Outstanding'],result.balances.map(b=>{const contribution=node('div');contribution.className='swap-settlement-stack';
   contribution.append(node('strong',`Side ${b.membershipOrdinal+1} · ${b.deliveryFrom} · ${b.ordinal===1?'original':'adjustment'}`),node('span',`Due ${displayInstant(b.dueAt,options.zone())} · applied ${b.applied}`));return[contribution,b.amount,b.outstanding];}),[1,2],[0]);
  // What is chosen or typed in the forms is kept when the view is read again: each candidate's selection and allocation, by its
  // contribution, and a chosen contribution, cash movement or opening that the new read still offers.
  const typed=new Map(netRows.map(r=>[r.id,{selected:r.selected.checked,amount:r.amount.value}]));netRows=[];
  table(netSelection,'swap-net-candidates',['Select','Contribution','Gross outstanding','Explicit signed allocation'],result.balances.filter(b=>b.outstanding!=='0').map(b=>{
   const selected=node('input');selected.type='checkbox';selected.setAttribute('aria-label',`Select side ${b.membershipOrdinal+1} ${b.deliveryFrom} contribution ${b.ordinal}`);
   const allocation=node('input');allocation.type='text';allocation.inputMode='decimal';allocation.setAttribute('aria-label',`Allocation to side ${b.membershipOrdinal+1} ${b.deliveryFrom} contribution ${b.ordinal}`);
   const kept=typed.get(b.determinationId);if(kept){selected.checked=kept.selected;allocation.value=kept.amount;}
   const contribution=node('div');contribution.className='swap-settlement-stack';contribution.append(node('strong',`Side ${b.membershipOrdinal+1} · ${b.deliveryFrom} · ${b.ordinal}`),node('span',`Due ${displayInstant(b.dueAt,options.zone())}`));
   netRows.push({id:b.determinationId,selected,amount:allocation});return[selected,contribution,b.outstanding,allocation];}),[2],[1]);
  offer(target,new Option('Choose a contribution…',''),...result.balances.filter(b=>b.outstanding!=='0').map(b=>new Option(`Side ${b.membershipOrdinal+1} · ${b.deliveryFrom} · ${b.ordinal===1?'original':'adjustment'} · outstanding ${b.outstanding}`,b.determinationId)));
  offer(reversal,new Option('Choose an original cash movement…',''),...result.cashMovements.filter(p=>p.canReverse).map(p=>new Option(`${p.reference} · ${p.amount} · ${displayInstant(p.effectiveAt,options.zone())}`,p.id)));
  table(cashHistory,'swap-settlement-cash',['Cash movement','Signed cash'],result.cashMovements.map(p=>{const movement=node('div');movement.className='swap-settlement-stack';
   movement.append(node('strong',p.reference),node('span',`${displayInstant(p.effectiveAt,options.zone())} · ${p.recordKind==='zero_net_discharge_v1'?'Noncash zero-net discharge':'Cash movement'}`),allocationEvidence(result,p.allocations));return[movement,p.amount];}),[1],[0]);
  const workspace=await options.api<{frames:{id:string;at:string}[]}>('workspace');
  if(version!==generation||scope!==key())return;
  offer(openingSnapshot,new Option('Choose…',''),...workspace.frames.map(f=>new Option(displayInstant(f.at,options.zone()),f.id)));
  openingLabel.textContent=opening&&opening.tradeId===tradeId?`Opening: ${displayInstant(opening.asOf,options.zone())} · retained knowledge context.`:'Capture a view, then select the closing financial context and refresh.';
  sync();
 }
 async function preview(payment:Payment){
  const report=current(),version=generation,scope=key();evidence.replaceChildren();dueEdits.clear();
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
   await sendCommand({operationId:crypto.randomUUID(),path:'swaps/settlement/determine',label:`Settlement ${correction?'correction':'determination'} of ${report.state.tradeReference}`,body:{preview:request,expectedVersion:report.settlement.version,previewHash:value.previewHash,
    duePolicy,recognitionPolicy,correctionPolicy,correctionDueAt:correction?due.value:null}});},true);
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
  await sendCommand({operationId:crypto.randomUUID(),path:'swaps/settlement/payment',label:`Settlement ${reversing?'cash reversal':'cash'} of ${report.state.tradeReference}`,body:{context:context(),tradeId:report.state.tradeId,expectedRevision:report.state.revision,
   expectedVersion:report.settlement.version,determinationId:movement?.determinationId??target.value,amount:movement?.reversalAmount??amount.value,currency:report.currency,
   effectiveAt:effective.value,reference:reference.value,reversesId:movement?.id??null}},()=>{effective.value='';reference.value='';if(movement)reversal.value='';else{target.value='';amount.value='';}});
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
  // What is recorded is the review the button is in: one removed while the person was asked first, as an edit removes it, is not.
  record.onclick=()=>replacing(async()=>{current();if(!record.isConnected||version!==generation||scope!==key()||!report.canAmend||options.pending())throw Error('The allocation review changed; preview again.');
   // A net settlement consumes the net form's header and the contributions it allocates; a reversal, the instant and reference of the
   // payment form and the movement chosen there.
   const allocated=new Set(request.allocations.map(a=>a.determinationId));
   await sendCommand({operationId:crypto.randomUUID(),path:'swaps/settlement/net',label:`Net settlement ${reversing?'reversal ':''}of ${report.state.tradeReference}`,body:{preview:request,previewHash:value.previewHash,effectiveAt:at,reference:ref}},
    reversing?()=>{effective.value='';reference.value='';reversal.value='';}:()=>{for(const field of [netAmount,netEffective,netReference])field.value='';for(const r of netRows)if(allocated.has(r.id)){r.selected.checked=false;r.amount.value='';}});});netReview.append(record);const lineage=node('details'),caption=node('summary','Exact allocation lineage and policy'),raw=node('pre');raw.className='swap-evidence';raw.textContent=JSON.stringify(value,null,2);lineage.append(caption,raw);netReview.append(lineage);
 }
 netForm.onsubmit=e=>{e.preventDefault();void run(()=>previewNet(),true);};
 // A prepared allocation review is of the entries it was asked with: an edit to the net form, or to the payment form whose instant and
 // reference a reversal is recorded with, removes it; a filter of the candidates is no entry.
 for(const event of ['input','change'])for(const entries of [form,netForm])entries.addEventListener(event,e=>{if(!(e.target as Element).closest(tableFilterControls))netReview.replaceChildren();});
 // A net settlement is reversed by reviewing its exact allocations first, which reads nothing again.
 const netReversal=()=>!!detail?.cashMovements.some(p=>p.id===reversal.value&&!p.determinationId);
 form.onsubmit=e=>{e.preventDefault();replacing(()=>record(false));};reverse.onclick=()=>netReversal()?void run(()=>record(true),true):replacing(()=>record(true));reversal.onchange=sync;
 // A bridge is of the opening and the contractual-change policy it was reconciled under; choosing either again removes it.
 const dropBridge=()=>bridge.replaceChildren();
 baseline.onclick=()=>{const report=current();dropBridge();opening={tradeId:report.state.tradeId,asOf:report.asOf,context:{...context(),knowledgeMode:'retained',contextId:report.context.contextId}};openingLabel.textContent=`Opening: ${displayInstant(report.asOf,options.zone())} · retained knowledge context.`;sync();};
 captureOpening.onclick=()=>void run(async()=>{const report=current();dropBridge();const version=generation,scope=key();if(!openingSnapshot.value)throw Error('Select the opening market snapshot.');const selected={...context(),snapshotId:openingSnapshot.value,knowledgeMode:'current',contextId:null};const saved=await options.api<{context:{contextId:string};asOf:string}>('swaps/catalog',selected);if(version!==generation||scope!==key())return;opening={tradeId:report.state.tradeId,asOf:saved.asOf,context:{...selected,knowledgeMode:'retained',contextId:saved.context.contextId}};openingLabel.textContent=`Opening: ${displayInstant(saved.asOf,options.zone())} · retained knowledge context.`;sync();},true);
 contractPolicy.onchange=dropBridge;openingSnapshot.onchange=dropBridge;
 reconcile.onclick=()=>void run(async()=>{const report=current();dropBridge();if(!contractPolicy.value)throw Error('Choose the explicit contractual-change valuation policy.');if(!opening||opening.tradeId!==tradeId)throw Error('Capture the opening view first.');const version=generation,scope=key(),policy=contractPolicy.value;
  const value=await options.api<{report:{trades:{bridge:Record<string,string>;transitions:{membershipOrdinal:number;deliveryFrom:string;deliveryTo:string;openingState:string;closingState:string;forecastToFinal:boolean;openingPv:string;closingPv:string}[]}[];overlaps:string}}>('swaps/settlement/pnl',{opening:opening.context,closing:{...context(),knowledgeMode:'retained',contextId:report.context.contextId},tradeIds:[tradeId],bookIds:[],contractChanges:contractPolicy.value,duePolicy,recognitionPolicy});
  if(version!==generation||scope!==key()||policy!==contractPolicy.value)return;
  const labels:Record<string,string>={newTradeEconomics:'New-trade economics at closing',contractualAmendment:'Contractual amendment at closing',continuingEconomics:'Continuing economics under prior terms',economicPnl:'Economic P&L',remainingValueMovement:'Change in remaining value',netCashMovement:'Change in cumulative contractual cash',periodCashReceived:'Contractual cash received during the period',priorCashKnowledgeAdjustment:'Prior cash · change in knowledge',realizedTransfer:'Realized trading transfer',unrealizedMovement:'Change in unrealized trading P&L',contractualCashMovement:'Change in contractual cash',discountEffectMovement:'Change in discount effect',correctionsMovement:'Correction movement · overlapping detail',roundingEffectMovement:'Rounding movement · overlapping detail'};
  table(bridge,'swap-settlement-bridge',['Bridge measure',`Amount · ${report.currencyLabel}`],Object.entries(value.report.trades[0].bridge).map(([key,amount])=>[labels[key]??key,amount]),[1]);bridge.append(node('p',value.report.overlaps));
  const transitions=node('div');bridge.append(node('h3','Period transitions'),transitions);
  table(transitions,'swap-settlement-transitions',['Period','Opening PV','Closing PV'],value.report.trades[0].transitions.map(p=>{const period=node('div');period.className='swap-settlement-stack';
   period.append(node('strong',`Side ${p.membershipOrdinal+1} · ${p.deliveryFrom} to ${p.deliveryTo}, end exclusive`),node('span',`Opening ${p.openingState} · closing ${p.closingState}${p.forecastToFinal?' · forecast became final':''}`));return[period,p.openingPv,p.closingPv];}),[1,2],[0]);},true);
 refresh.onclick=()=>replacing(load);
 return {async open(id:string){if(tradeId!==id){form.reset();edits.clear();netForm.reset();netEdits.clear();netRows=[];consumption.clear();contractPolicy.value='';openingSnapshot.value='';if(opening?.tradeId!==id)opening=undefined;}tradeId=id;error.textContent='';detail=undefined;await load();if(detail&&tradeId===id&&!mounted()){options.host().append(root);root.hidden=false;}},
  reset(){invalidate();detail=undefined;tradeId=undefined;form.reset();edits.clear();netForm.reset();netEdits.clear();netRows=[];consumption.clear();shelf.append(root);root.hidden=true;},sync,
  refresh(){invalidate();if(mounted()){detail=undefined;status.textContent='Financial context changed. Refresh settlement before continuing.';}},
  /** Reads the view again when it is open; after the command `settled` went through, what it consumed is cleared first. */
  async liveRefresh(settled?:string){if(!mounted())return;settle(settled);await load();},isOpen:mounted,
  unsaved:()=>mounted()&&(edits.edited()||netEdits.edited()||dueEdits.edited()),
  revision:()=>mounted()?detail?.state.revision:undefined};
}
