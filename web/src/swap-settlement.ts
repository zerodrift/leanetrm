import {renderRegularTable} from './regular-table.js';
import {trackEdits} from './edit-tracking.js';
import {displayInstant} from './market-view.js';
import type {RequestCommand} from './pending-command.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Context=Record<string,unknown>;
type Payment={membershipOrdinal:number;ordinal:number;deliveryFrom:string;deliveryTo:string;paymentAt:string;completionAt:string;state:string;final:boolean;realized:boolean;contractualAmount:string;remainingValue:string};
type Balance={determinationId:string;membershipOrdinal:number;deliveryFrom:string;deliveryTo:string;ordinal:number;dueAt:string;amount:string;applied:string;outstanding:string};
type Cash={id:string;determinationId:string;amount:string;reversalAmount:string;effectiveAt:string;reference:string;canReverse:boolean};
type Detail={state:{tradeId:string;revision:number;tradeReference:string};context:{contextId:string};asOf:string;canAmend:boolean;currency:unknown;currencyLabel:string;decimalPlaces:number;roundingPolicy:string;
 settlement:{version:number;occurrences:unknown[];determinations:unknown[]};balances:Balance[];cashMovements:Cash[];
 valuation:Record<string,string|Payment[]>&{payments:Payment[]}};
type Preview={previewHash:string;expectedVersion:number;previousTotal:string;proposedContribution:string;isCorrection:boolean;inputsChanged:boolean;calculation:{amount:string;exactAmount:string;roundingEffect:string;resolvedQuantity:string;paymentAt:string;prices:unknown[]}};
const duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1',correctionPolicy='swap_explicit_correction_due_v1';
/** The measures a settlement lifecycle states (D215), in the words its view and the panel's Terms give them. */
export const lifecycleMeasures=[['Remaining contractual value','remainingValue'],['Determined outstanding','determinedOutstanding'],['PV of determined outstanding','determinedPv'],['Due / overdue balance','dueBalance'],['Realized trading P&L','realized'],['Unrealized trading P&L','unrealized'],['Actual net cash received','netCash'],['Corrections','corrections'],['Rounding effect','roundingEffect'],['Estimated remaining PV','estimatedRemainingPv']] as const;

/**
 * A financial swap's settlement and P&L view (D215), which the side panel hosts as it hosts the swap ticket (D218): its occurrences,
 * determinations, balances, cash and P&L bridge, and the commands that write them. `host` is the panel's form part; the view is kept on
 * the page's shelf until it opens there, and goes back when it is reset. What is typed into its payment form is an edit.
 */
export function setupSwapSettlement(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;context:()=>Context;
 queue:(command:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined;host:()=>HTMLElement;unsavedChanged:()=>void}){
 const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
 const shelf=document.getElementById('editor-shelf')!,root=node('div');root.id='swap-settlement';root.className='editing swap-settlement-editing';root.hidden=true;root.setAttribute('aria-labelledby','swap-settlement-title');
 const mounted=()=>root.parentElement!==shelf;
 const heading=node('div');heading.className='swap-settlement-heading';const title=node('h3','Settlement and P&L');title.id='swap-settlement-title';heading.append(title);
 const status=node('p');status.setAttribute('role','status');const error=node('p');error.setAttribute('role','alert');
 const refresh=node('button','Refresh settlement');refresh.type='button';refresh.id='swap-settlement-refresh';
 const summary=node('div'),payments=node('div'),evidence=node('section'),balances=node('div'),cashHistory=node('div'),pnl=node('section');
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
 const baseline=node('button','Use this view as opening');baseline.id='swap-settlement-opening';baseline.type='button';
 const reconcile=node('button','Reconcile opening to this view');reconcile.id='swap-settlement-reconcile';reconcile.type='button';
 const openingLabel=node('p'),bridge=node('div');pnl.append(node('h3','Trading P&L reconciliation'),baseline,openingLabel,reconcile,bridge);
 root.append(heading,status,error,refresh,summary,node('h3','Contractual payment occurrences'),payments,evidence,node('h3','Determined balances and adjustments'),balances,
  form,node('h3','Cash history'),cashHistory,pnl);shelf.append(root);
 const edits=trackEdits(form,[],options.unsavedChanged);
 let detail:Detail|undefined,tradeId:string|undefined,selectedKey='',generation=0;
 let opening:{tradeId:string;context:Context;asOf:string}|undefined;
 const key=()=>JSON.stringify(options.context()),context=()=>({...options.context(),convention:'independent_side_financial_swap_v2'});
 const current=()=>{if(!detail||selectedKey!==key())throw Error('The financial context changed. Refresh and review settlement.');return detail;};
 const canWrite=()=>!!detail?.canAmend&&selectedKey===key()&&!options.busy()&&!options.pending();
 function sync(){
  submit.disabled=!canWrite();reverse.disabled=!canWrite()||!reversal.value;refresh.disabled=options.busy();baseline.disabled=!detail||selectedKey!==key()||options.busy();
  reconcile.disabled=!detail||selectedKey!==key()||!opening||opening.tradeId!==tradeId||options.busy();
  for(const field of [target,amount,effective,reference,reversal])field.disabled=!canWrite();
  payments.querySelectorAll<HTMLButtonElement>('button[data-eligible]').forEach(button=>button.disabled=!canWrite()||button.dataset.eligible!=='true');
  const determine=evidence.querySelector<HTMLButtonElement>('#swap-settlement-determine');if(determine)determine.disabled=!canWrite()||determine.dataset.eligible!=='true';
 }
 function invalidate(){generation++;evidence.replaceChildren();bridge.replaceChildren();sync();}
 async function run(work:()=>Promise<void>){await options.act(async()=>{error.textContent='';try{await work();}catch(e){invalidate();error.textContent=(e as Error).message;}});sync();}
 function table(host:HTMLElement,id:string,headers:string[],rows:(string|HTMLElement)[][],numeric:number[]=[]){host.replaceChildren();renderRegularTable(host,id,JSON.stringify(detail?.context),headers,rows,numeric,options.busy);}
 async function load(){
  if(!tradeId)return;invalidate();const version=generation,scope=key();const result=await options.api<Detail>('swaps/settlement/inspect',{context:context(),tradeId,revision:null,duePolicy,recognitionPolicy});
  if(version!==generation||scope!==key())return;detail=result;selectedKey=scope;title.textContent=`Settlement and P&L · ${result.state.tradeReference}`;
  status.textContent=`As of ${displayInstant(result.asOf,options.zone())}. ${result.canAmend?'Working view.':'Read only historical view.'} Currency: ${result.currencyLabel}. Contractual precision: ${result.decimalPlaces} decimal places, ties to even.`;
  summary.replaceChildren();
  table(summary,'swap-settlement-summary',['Measure','Amount'],lifecycleMeasures.map(([name,field])=>[name,String(result.valuation[field])]),[1]);
  summary.append(node('p','Amounts retain the contractual currency. Determined PV is included in remaining value; due cash is part of outstanding. Corrections and rounding are overlapping detail. Payments do not recognize profit again.'));
  table(payments,'swap-settlement-payments',['Side','Delivery start','Delivery end · exclusive','Delivery completion','Payment instant','State',`Signed cash · ${result.currencyLabel}`,`Remaining PV · ${result.currencyLabel}`,'Realized','Action'],
   result.valuation.payments.map(p=>{const calculate=node('button',p.state==='Undetermined'?'Calculate final payment':'Review correction');calculate.type='button';calculate.dataset.membership=String(p.membershipOrdinal);calculate.dataset.payment=String(p.ordinal);calculate.dataset.eligible=String(p.final&&result.canAmend);calculate.disabled=!p.final||!result.canAmend;
    calculate.onclick=()=>void run(()=>preview(p));return[String(p.membershipOrdinal+1),p.deliveryFrom,p.deliveryTo,displayInstant(p.completionAt,options.zone()),displayInstant(p.paymentAt,options.zone()),p.state,p.contractualAmount,p.remainingValue,p.realized?'Yes':'No',calculate];}),[0,6,7]);
  table(balances,'swap-settlement-balances',['Side','Delivery start','Contribution','Due instant','Determined','Applied','Outstanding'],result.balances.map(b=>[String(b.membershipOrdinal+1),b.deliveryFrom,b.ordinal===1?'Original':'Adjustment',displayInstant(b.dueAt,options.zone()),b.amount,b.applied,b.outstanding]),[0,4,5,6]);
  target.replaceChildren(new Option('Choose a contribution…',''),...result.balances.filter(b=>b.outstanding!=='0').map(b=>new Option(`Side ${b.membershipOrdinal+1} · ${b.deliveryFrom} · ${b.ordinal===1?'original':'adjustment'} · outstanding ${b.outstanding}`,b.determinationId)));
  reversal.replaceChildren(new Option('Choose an original cash movement…',''),...result.cashMovements.filter(p=>p.canReverse).map(p=>new Option(`${p.reference} · ${p.amount} · ${displayInstant(p.effectiveAt,options.zone())}`,p.id)));
  table(cashHistory,'swap-settlement-cash',['Effective instant','Signed cash','Reference'],result.cashMovements.map(p=>[displayInstant(p.effectiveAt,options.zone()),p.amount,p.reference]),[1]);
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
   await options.queue({operationId:crypto.randomUUID(),path:'swaps/settlement/determine',label:`Settlement ${correction?'correction':'determination'} of ${report.state.tradeReference}`,body:{preview:request,expectedVersion:report.settlement.version,previewHash:value.previewHash,
    duePolicy,recognitionPolicy,correctionPolicy,correctionDueAt:correction?due.value:null}});await load();});
  const details=node('details'),caption=node('summary','Calculation and final fixing evidence'),raw=node('pre');raw.className='swap-evidence';raw.textContent=JSON.stringify(value.calculation,null,2);details.append(caption,raw);
  evidence.append(heading,text);if(!value.inputsChanged)evidence.append(node('p','These final fixing inputs are already determined.'));if(correction)label(evidence,'Correction due instant · UTC',due);else evidence.append(node('p',`Contractual payment: ${displayInstant(value.calculation.paymentAt,options.zone())}.`));evidence.append(details,determine);
 }
 async function record(reversing:boolean){
  const report=current();if(!report.canAmend||options.pending())throw Error('Refresh an authorized working view before recording cash.');
  if(!effective.value||!reference.value)throw Error('Enter a cash effective instant and reference.');
  const movement=reversing?report.cashMovements.find(p=>p.id===reversal.value&&p.canReverse):undefined;
  if(reversing&&!movement)throw Error('Choose an original cash movement.');
  if(!reversing&&(!target.value||!amount.value))throw Error('Choose a contribution and enter the signed cash amount.');
  await options.queue({operationId:crypto.randomUUID(),path:'swaps/settlement/payment',label:`Settlement ${reversing?'cash reversal':'cash'} of ${report.state.tradeReference}`,body:{context:context(),tradeId:report.state.tradeId,expectedRevision:report.state.revision,
   expectedVersion:report.settlement.version,determinationId:movement?.determinationId??target.value,amount:movement?.reversalAmount??amount.value,currency:report.currency,
   effectiveAt:effective.value,reference:reference.value,reversesId:movement?.id??null}});amount.value='';reference.value='';effective.value='';edits.clear();await load();
 }
 form.onsubmit=e=>{e.preventDefault();void run(()=>record(false));};reverse.onclick=()=>void run(()=>record(true));reversal.onchange=sync;
 baseline.onclick=()=>{const report=current();opening={tradeId:report.state.tradeId,asOf:report.asOf,context:{...context(),knowledgeMode:'retained',contextId:report.context.contextId}};openingLabel.textContent=`Opening: ${displayInstant(report.asOf,options.zone())} · retained knowledge context.`;sync();};
 reconcile.onclick=()=>void run(async()=>{const report=current();if(!opening||opening.tradeId!==tradeId)throw Error('Capture the opening view first.');
  const value=await options.api<{report:{trades:{bridge:Record<string,string>;transitions:{membershipOrdinal:number;deliveryFrom:string;deliveryTo:string;openingState:string;closingState:string;forecastToFinal:boolean;openingPv:string;closingPv:string}[]}[];overlaps:string}}>('swaps/settlement/pnl',{opening:opening.context,closing:{...context(),knowledgeMode:'retained',contextId:report.context.contextId},tradeIds:[tradeId],bookIds:[],contractChanges:'reject_contract_changes_v1',duePolicy,recognitionPolicy});
  const labels:Record<string,string>={economicPnl:'Economic P&L',remainingValueMovement:'Change in remaining value',netCashMovement:'Change in cumulative contractual cash',periodCashReceived:'Contractual cash received during the period',priorCashKnowledgeAdjustment:'Prior cash · change in knowledge',realizedTransfer:'Realized trading transfer',unrealizedMovement:'Change in unrealized trading P&L',contractualCashMovement:'Change in contractual cash',discountEffectMovement:'Change in discount effect',correctionsMovement:'Correction movement · overlapping detail',roundingEffectMovement:'Rounding movement · overlapping detail'};
  table(bridge,'swap-settlement-bridge',['Bridge measure',`Amount · ${report.currencyLabel}`],Object.entries(value.report.trades[0].bridge).map(([key,amount])=>[labels[key]??key,amount]),[1]);bridge.append(node('p',value.report.overlaps));
  const transitions=node('div');bridge.append(node('h3','Period transitions'),transitions);
  table(transitions,'swap-settlement-transitions',['Side','Delivery start','Delivery end · exclusive','Opening state','Closing state','Forecast became final','Opening PV','Closing PV'],value.report.trades[0].transitions.map(p=>[String(p.membershipOrdinal+1),p.deliveryFrom,p.deliveryTo,p.openingState,p.closingState,p.forecastToFinal?'Yes':'No',p.openingPv,p.closingPv]),[0,6,7]);});
 refresh.onclick=()=>void run(load);
 return {async open(id:string){if(tradeId!==id){form.reset();edits.clear();if(opening?.tradeId!==id)opening=undefined;}tradeId=id;error.textContent='';detail=undefined;await load();if(detail&&tradeId===id&&!mounted()){options.host().append(root);root.hidden=false;}},
  reset(){invalidate();detail=undefined;tradeId=undefined;form.reset();edits.clear();shelf.append(root);root.hidden=true;},sync,
  refresh(){invalidate();if(mounted()){detail=undefined;status.textContent='Financial context changed. Refresh settlement before continuing.';}},
  async liveRefresh(){if(mounted())await load();},isOpen:mounted,
  unsaved:()=>mounted()&&edits.edited(),
  revision:()=>mounted()?detail?.state.revision:undefined};
}
