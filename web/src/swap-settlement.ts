import {renderRegularTable} from './regular-table.js';
import {displayInstant} from './market-view.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Context=Record<string,unknown>;
type Payment={membershipOrdinal:number;ordinal:number;deliveryFrom:string;deliveryTo:string;paymentAt:string;completionAt:string;state:string;final:boolean;realized:boolean;contractualAmount:string;remainingValue:string};
type Balance={determinationId:string;membershipOrdinal:number;deliveryFrom:string;deliveryTo:string;ordinal:number;dueAt:string;amount:string;applied:string;outstanding:string};
type Cash={id:string;determinationId:string|null;recordKind:string;allocations:{determinationId:string;amount:string}[];amount:string;reversalAmount:string;effectiveAt:string;reference:string;canReverse:boolean};
type Detail={state:{tradeId:string;revision:number;tradeReference:string};context:{contextId:string};asOf:string;canAmend:boolean;currency:unknown;currencyLabel:string;decimalPlaces:number;roundingPolicy:string;
 settlement:{version:number;occurrences:unknown[];determinations:unknown[]};balances:Balance[];cashMovements:Cash[];
 valuation:Record<string,string|Payment[]>&{payments:Payment[]}};
const duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1';
/** The measures a settlement lifecycle states (D215), in the words its view and the panel's Terms give them. */
export const lifecycleMeasures=[['Remaining contractual value','remainingValue'],['Determined outstanding','determinedOutstanding'],['PV of determined outstanding','determinedPv'],['Due / overdue balance','dueBalance'],['Realized trading P&L','realized'],['Unrealized trading P&L','unrealized'],['Actual net cash received','netCash'],['Corrections','corrections'],['Rounding effect','roundingEffect'],['Estimated remaining PV','estimatedRemainingPv']] as const;

/** Retained settlement, cash and P&L reads. New actions use the shared policy-bound workspace. */
export function setupSwapSettlement(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;context:()=>Context;
 host:()=>HTMLElement;lifecycle?:()=>void;pending:()=>unknown}){
 const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
 const shelf=document.getElementById('editor-shelf')!,root=node('div');root.id='swap-settlement';root.className='editing swap-settlement-editing';root.hidden=true;root.setAttribute('aria-labelledby','swap-settlement-title');
 const mounted=()=>root.parentElement!==shelf;
 const heading=node('div');heading.className='swap-settlement-heading';const title=node('h3','Settlement and P&L');title.id='swap-settlement-title';heading.append(title);
 const status=node('p');status.setAttribute('role','status');const error=node('p');error.setAttribute('role','alert');
 const refresh=node('button','Refresh settlement');refresh.type='button';refresh.id='swap-settlement-refresh';
 const summary=node('div'),payments=node('div'),evidence=node('section'),balances=node('div'),cashHistory=node('div'),pnl=node('section');payments.id='swap-settlement-payments';balances.id='swap-settlement-balances';cashHistory.id='swap-settlement-cash';
 const navigate=node('button','Open Lifecycle');navigate.id='swap-open-lifecycle';navigate.type='button';navigate.onclick=()=>{if(!options.busy()&&!options.pending())options.lifecycle?.();};
 const actionHelp=node('p','Determinations and payments use saved action policies in Lifecycle.');
 const label=(host:HTMLElement,text:string,field:HTMLElement)=>{const wrap=node('div'),caption=node('label',text);caption.htmlFor=field.id;wrap.append(caption,field);host.append(wrap);};
 const baseline=node('button','Use this view as opening');baseline.id='swap-settlement-opening';baseline.type='button';
 const reconcile=node('button','Reconcile opening to this view');reconcile.id='swap-settlement-reconcile';reconcile.type='button';
 const openingSnapshot=node('select');openingSnapshot.id='swap-pnl-opening-snapshot';const captureOpening=node('button','Capture selected opening snapshot');captureOpening.type='button';captureOpening.id='swap-pnl-capture-opening';
 const contractPolicy=node('select');contractPolicy.id='swap-pnl-contract-policy';contractPolicy.append(new Option('Choose a contractual-change policy…',''),new Option('Unchanged contracts · bridge v1','reject_contract_changes_v1'),new Option('New trades / future amendments · closing-context v1','closing_context_contract_changes_v1'));
 const openingLabel=node('p'),bridge=node('div');bridge.id='swap-settlement-bridge';pnl.append(node('h3','Trading P&L reconciliation'),baseline);label(pnl,'Opening market snapshot',openingSnapshot);pnl.append(captureOpening,openingLabel);label(pnl,'Contractual changes valued under',contractPolicy);pnl.append(reconcile,bridge);
 pnl.append(node('p','Closing-context v1 values prior and revised terms in the closing market. New-trade economics, amendments and continuing economics reconcile to total trading P&L; payments and netting do not create earnings. Book transfers and unsupported transformations are refused.'));
 root.append(heading,status,error,refresh,summary,node('h3','Contractual payment occurrences'),payments,evidence,node('h3','Determined balances and adjustments'),balances,
  actionHelp,...(options.lifecycle?[navigate]:[node('p','Open Lifecycle under Scheduling in the main workspace.')]),node('h3','Cash history'),cashHistory,pnl);shelf.append(root);
 let detail:Detail|undefined,tradeId:string|undefined,selectedKey='',generation=0;
 let opening:{tradeId:string;context:Context;asOf:string}|undefined;
 const key=()=>JSON.stringify(options.context()),context=()=>({...options.context(),convention:'independent_side_financial_swap_v2'});
 const current=()=>{if(!detail||selectedKey!==key())throw Error('The financial context changed. Refresh and review settlement.');return detail;};
 function sync(){
  navigate.disabled=options.busy()||!!options.pending();refresh.disabled=options.busy();baseline.disabled=!detail||selectedKey!==key()||options.busy();
  reconcile.disabled=!detail||selectedKey!==key()||!opening||opening.tradeId!==tradeId||options.busy();
 }
 function invalidate(){generation++;evidence.replaceChildren();bridge.replaceChildren();sync();}
 async function run(work:()=>Promise<void>,keep=false){await options.act(async()=>{error.textContent='';try{await work();}catch(e){if(!keep)invalidate();error.textContent=(e as Error).message;}});sync();}
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
   result.valuation.payments.map(p=>{
    const occurrence=node('div');occurrence.className='swap-settlement-stack';
    occurrence.append(node('strong',`Side ${p.membershipOrdinal+1} · ${p.deliveryFrom} to ${p.deliveryTo}, end exclusive`),node('span',`${p.state} · ${p.realized?'P&L realized':'P&L not realized'}`),
     node('span',`Delivery completes ${displayInstant(p.completionAt,options.zone())} · payment ${displayInstant(p.paymentAt,options.zone())}`));
    return[occurrence,p.contractualAmount,p.remainingValue];}),[1,2],[0]);
  table(balances,'swap-settlement-balances',['Contribution','Determined','Outstanding'],result.balances.map(b=>{const contribution=node('div');contribution.className='swap-settlement-stack';
   contribution.append(node('strong',`Side ${b.membershipOrdinal+1} · ${b.deliveryFrom} · ${b.ordinal===1?'original':'adjustment'}`),node('span',`Due ${displayInstant(b.dueAt,options.zone())} · applied ${b.applied}`));return[contribution,b.amount,b.outstanding];}),[1,2],[0]);
  table(cashHistory,'swap-settlement-cash',['Cash movement','Signed cash'],result.cashMovements.map(p=>{const movement=node('div');movement.className='swap-settlement-stack';
   movement.append(node('strong',p.reference),node('span',`${displayInstant(p.effectiveAt,options.zone())} · ${p.recordKind==='zero_net_discharge_v1'?'Noncash zero-net discharge':'Cash movement'}`),allocationEvidence(result,p.allocations));return[movement,p.amount];}),[1],[0]);
  const lineage=node('details'),caption=node('summary','Accepted settlement and fixing evidence'),raw=node('pre');raw.className='swap-evidence';raw.textContent=JSON.stringify(result.settlement,null,2);lineage.append(caption,raw);evidence.append(lineage);
  const workspace=await options.api<{frames:{id:string;at:string}[]}>('workspace/navigation');
  if(version!==generation||scope!==key())return;
  offer(openingSnapshot,new Option('Choose…',''),...workspace.frames.map(f=>new Option(displayInstant(f.at,options.zone()),f.id)));
  openingLabel.textContent=opening&&opening.tradeId===tradeId?`Opening: ${displayInstant(opening.asOf,options.zone())} · retained knowledge context.`:'Capture a view, then select the closing financial context and refresh.';
  sync();
 }
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
 refresh.onclick=()=>{if(!options.busy())void run(load);};
 return {async open(id:string){if(tradeId!==id){contractPolicy.value='';openingSnapshot.value='';if(opening?.tradeId!==id)opening=undefined;}tradeId=id;error.textContent='';detail=undefined;await load();if(detail&&tradeId===id&&!mounted()){options.host().append(root);root.hidden=false;}},
  reset(){invalidate();detail=undefined;tradeId=undefined;contractPolicy.value='';openingSnapshot.value='';shelf.append(root);root.hidden=true;},sync,
  refresh(){invalidate();if(mounted()){detail=undefined;status.textContent='Financial context changed. Refresh settlement before continuing.';}},
  async liveRefresh(_settled?:string){if(mounted())await load();},isOpen:mounted,unsaved:()=>false,
  revision:()=>mounted()?detail?.state.revision:undefined};
}
