import {amountText,prepareAmountDisplay} from './amount-display.js';
import {renderRegularTable} from './regular-table.js';

type Cash={numerator:string;denominator:string;currency:unknown};
type Report={reportId:string;runId:string;close:{id:string;name:string;at:string;kind:string};
 totals:{measure:string;bookId:string;book:string;currencyLabel:string;value:Cash}[];
 risk:{measure:string;bookId:string;book:string;currencyLabel:string;factor:{label:string;unit:string;bumpSize:string};up:Cash;down:Cash;upPnl:Cash;downPnl:Cash;delta:{value:Cash};gamma:{value:Cash}}[];
 work:{positionCount:number;uniqueValuations:number;scenarioCount:number;unitPricerCalls:number;quantityApplications:number;reusedUnitScenarioValues:number}};
function exact(value:Cash){
 if(!value||!/^[-]?\d+$/.test(value.numerator)||!/^\d+$/.test(value.denominator)||/^0+$/.test(value.denominator))throw Error('The report contains an incomplete exact amount.');
 return value.denominator==='1'?value.numerator:`${value.numerator}/${value.denominator}`;
}
const measure=(value:string)=>value==='futures_execution_pnl'?'Futures execution P&L':value==='swap_present_value'?'Swap PV':value;
export function validatePilotReport(result:Report,runId:string){
 if(!result||result.runId!==runId||result.reportId!==runId||result.close?.id!==runId||!Array.isArray(result.totals)||!Array.isArray(result.risk))throw Error('The requested close report is incomplete. Refresh its run status.');
 return result;
}

export function setupPilotCloseReport(options:{api:<T>(path:string,body?:unknown)=>Promise<T>;identity:()=>string;instant:(at:string)=>string;busy:()=>boolean}){
 const get=(id:string)=>document.getElementById(id)!;
 let current:Report|undefined,generation=0;
 function render(){
  if(!current)return;
  const report=current;
  get('pilot-report-title').textContent=`${report.close.name} · ${options.instant(report.close.at)}`;
  get('pilot-report-work').textContent=`${report.work.positionCount} holdings · ${report.work.uniqueValuations} unique instruments · ${report.work.scenarioCount} scenarios · ${report.work.unitPricerCalls} unit valuations · ${report.work.quantityApplications} quantity applications · ${report.work.reusedUnitScenarioValues} reused unit values.`;
  renderRegularTable(get('pilot-report-totals'),'pilot-report-totals',options.identity(),['Book','Measure','Currency','Base value'],report.totals.map(t=>[
   t.book,measure(t.measure),t.currencyLabel,amountText(exact(t.value),'money')]),[3],options.busy);
  renderRegularTable(get('pilot-report-risk'),'pilot-report-risk',options.identity(),['Book','Measure','Market factor','Currency','Quote unit','Bump','Up change','Down change','Delta','Gamma'],report.risk.map(r=>[
   r.book,measure(r.measure),r.factor.label,r.currencyLabel,r.factor.unit,r.factor.bumpSize,
   amountText(exact(r.upPnl),'money'),amountText(exact(r.downPnl),'money'),amountText(exact(r.delta.value),'factor'),amountText(exact(r.gamma.value),'factor')]),[5,6,7,8,9],options.busy);
 }
 async function load(runId:string){
  const request=++generation,identity=options.identity();current=undefined;get('pilot-report').hidden=true;
  const result=await options.api<Report>('jobs/pilot-close/results?runId='+encodeURIComponent(runId));
  if(request!==generation||identity!==options.identity())return;
  validatePilotReport(result,runId);
  await prepareAmountDisplay({totals:result.totals.map(t=>({presentValue:exact(t.value)})),risk:result.risk.map(r=>({upPnl:exact(r.upPnl),downPnl:exact(r.downPnl),delta:exact(r.delta.value),gamma:exact(r.gamma.value)}))},body=>options.api<string[]>('amounts/display',body));
  if(request!==generation||identity!==options.identity())return;
  current=result;render();get('pilot-report').hidden=false;get('pilot-report').scrollIntoView({block:'nearest'});
 }
 return{load,refresh:render,reset(){generation++;current=undefined;get('pilot-report').hidden=true;for(const id of ['pilot-report-title','pilot-report-work','pilot-report-totals','pilot-report-risk'])get(id).replaceChildren();}};
}
