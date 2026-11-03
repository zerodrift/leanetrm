import {node,table} from './risk-report-elements.js';
import {renderJointResults,newJointView} from './joint-risk-results-view.js';
import {renderHistorical,newHistoricalView} from './historical-risk-view.js';
import {renderParametric,newParametricView} from './parametric-risk-view.js';
import {renderChart,clearChart} from './chart-view.js';
import {signOf} from './desk-model.js';
import {moved,movedTotals,reportPresentation,riskMetricLabels,type RiskMetric} from './risk-results.js';
import type {RunView} from './risk-view.js';
import type {RiskFactorResult,RiskValues} from './risk-report.js';
import {publicText,displayInstant} from './market-view.js';
import {amountText} from './amount-display.js';
import {valuationReason} from './valuation-status.js';

/** The same report widget is used in Risk and the Blotter. All financial amounts and their display strings come from the report. */
/** A risk amount at the selected display precision: sensitivities as factors, values and P&L as money. */
// A value at the display precision, with the backend's exact amount beneath it (D114), as the Blotter shows its figures.
const shown=(v:RiskValues,key:'base'|'upPnl'|'downPnl'|'delta'|'gamma')=>{const exact=v[key],cell=node('span',amountText(exact,key==='delta'||key==='gamma'?'factor':'money'));cell.title=exact;return cell;};

export function setupRiskResults(host:HTMLElement,zone:()=>string,reportUrl:(id:string)=>string=id=>location.pathname+location.search+'#risk/job/'+encodeURIComponent(id)){
 let metric:RiskMetric='delta',kind:'bar'|'heatmap'='bar',current:RunView|undefined,selected:string|undefined,factorPage=0,bookPage=0,groupKey:string|undefined;
 const plots:HTMLElement[]=[];
 let jointState=newJointView();
 let parametricState=newParametricView(),historicalState=newHistoricalView();
 function clear(){for(const plot of plots)clearChart(plot);plots.length=0;host.replaceChildren();}
 function detail(target:HTMLElement,factor:RiskFactorResult){
  target.replaceChildren(node('h3',factor.factor.label),node('p',`Independent ±${factor.bump} ${factor.factor.unit} shock · ${factor.factor.coordinate==='market_quote'?'Shared market factor':'Computed output'}`));
  target.append(table(['Book','Measure','Currency','Base','Up P&L','Down P&L','Delta','Delta unit','Gamma','Gamma unit'],movedTotals(factor).map(t=>[t.book,t.measureLabel,t.values.currency,shown(t.values,'base'),shown(t.values,'upPnl'),shown(t.values,'downPnl'),shown(t.values,'delta'),t.values.deltaUnit,shown(t.values,'gamma'),t.values.gammaUnit])));
  if(!movedTotals(factor).length)target.append(node('p','This shock moves no book measure.'));
  const held=factor.positions.filter(p=>moved(p.values)),positions=node('details'),title=node('summary',`Positions this shock moves · ${held.length}`);positions.append(title);
  positions.append(table(['Trade','Instrument','Book','Measure','Base','Up P&L','Down P&L','Delta','Gamma','Currency / sensitivity units'],held.map(p=>[p.tradeReference,p.instrumentReference,p.book,p.measureLabel,shown(p.values,'base'),shown(p.values,'upPnl'),shown(p.values,'downPnl'),shown(p.values,'delta'),shown(p.values,'gamma'),`${p.values.currency} · ${p.values.deltaUnit} · ${p.values.gammaUnit}`])));
  if(!factor.positions.length)positions.append(node('p','This report retains book totals only. Position detail was not requested.'));
  target.append(positions);
 }
 function draw(){
  clear();if(!current){host.append(node('p','No report selected. Run risk here or open a retained report from Reports and runs.'));return;}
  let view:ReturnType<typeof reportPresentation>;try{view=reportPresentation(current,metric,{offset:factorPage*64,limit:64});}catch(error){const errorNode=node('p',error instanceof Error?error.message:'This report cannot be displayed.');errorNode.setAttribute('role','alert');host.append(errorNode);return;}
  const head=node('div');head.className='risk-result-heading';
  const title=view.historical?'Historical simulation VaR / ES':view.parametric?'Parametric VaR / ES':view.joint?.kind==='scenarios'?'Joint scenario report':view.joint?'Selected Greeks and cross-gamma':'Sensitivity report';
  head.append(node('h3',view.state==='complete'?title:view.state==='incomplete'?title+' · incomplete coverage':view.state==='running'?'Risk run in progress':'Risk results unavailable'));
  const context=node('p');context.className='field-help';context.textContent=publicText(`${current.asOf?displayInstant(current.asOf,zone()):'Market time not recorded'}${current.context?.knownAt?` · known at ${displayInstant(current.context.knownAt,zone())}`:''} · ${current.parts.length} part${current.parts.length===1?'':'s'}`);head.append(context,node('p',current.setupText));host.append(head);
  const retained=current.parts.find(p=>p.job?.id)?.job?.id;
  if(retained){const link=node('a','Open retained report in Risk');link.href=reportUrl(retained);link.className='risk-report-link';head.append(link);}
  const status=node('div');status.className='risk-coverage';status.dataset.state=view.state;status.setAttribute('role','status');
  const counted=view.historical?`${view.historical.factors.length} factor${view.historical.factors.length===1?'':'s'} · ${view.historical.result.samples} full-PV samples`:view.parametric?`${view.parametric.result.calibration.columns.length} factors · ${view.parametric.result.calibration.samples} common changes`:view.joint?(view.joint.kind==='scenarios'?`${view.joint.samples.length} named scenarios`:`${view.joint.derivatives.length} selected derivatives`):`${view.factors.length} reported factors`;
  status.append(node('strong',view.state==='running'?'Waiting for every running part':`${counted} · ${view.excluded.length} excluded positions · ${view.gaps.length} unavailable parts`));
  if(!current.scope)status.append(node('p','The original book and portfolio scope is not recorded. Complete scope coverage cannot be confirmed.'));
  else head.append(node('p',`Captured scope · ${current.scope.portfolioIds.length?`${current.scope.portfolioIds.length} selected portfolio filters`:'no portfolio filter'} · ${current.scope.bookIds.length?`${current.scope.bookIds.length} selected book filters`:'all readable books at submission'} · ${current.scope.tradeIds==null?'all trades in those books':`${current.scope.tradeIds.length} selected trade${current.scope.tradeIds.length===1?'':'s'}`}. Book totals below keep their retained names.`));
  if(view.excluded.length){const list=node('ul');for(const p of view.excluded)list.append(node('li',`${p.tradeReference} · ${valuationReason(p.reason,p.instrumentType)}`));status.append(list);}
  for(const gap of view.gaps)status.append(node('p',gap.text));host.append(status);
  if(view.historical){renderHistorical(host,view.historical,historicalState,draw,plots);return;}
  if(view.parametric){renderParametric(host,view.parametric,parametricState,draw,plots);return;}
  if(view.joint){renderJointResults(host,view.joint,jointState,draw,plots);return;}
  if(!view.factors.length)return;
  const controls=node('div');controls.className='risk-result-controls';
  for(const [value,label] of Object.entries(riskMetricLabels)){const b=node('button',label);b.type='button';b.className='secondary';b.dataset.riskMetric=value;b.setAttribute('aria-pressed',String(metric===value));b.onclick=()=>{metric=value as RiskMetric;draw();};controls.append(b);}
  const chartLabel=node('label','Graphic '),chartType=node('select');chartType.setAttribute('aria-label','Risk graphic');chartType.append(new Option('Sensitivity ladder','bar'),new Option('Exposure heatmap','heatmap'));chartType.value=kind;chartType.onchange=()=>{kind=chartType.value as typeof kind;draw();};chartLabel.append(chartType);controls.append(chartLabel);host.append(controls);
  function paging(label:string,page:number,size:number,total:number,set:(page:number)=>void){const bar=node('div');bar.className='risk-report-paging';for(const [text,next,disabled] of [['Previous '+label,page-1,page===0],['Next '+label,page+1,(page+1)*size>=total]] as const){const b=node('button',text);b.type='button';b.className='secondary';b.disabled=disabled;b.onclick=()=>{set(next);draw();};bar.append(b);}bar.append(node('span',`${label} ${total?page*size+1:0}–${Math.min(total,(page+1)*size)} of ${total} · display window only`));return bar;}
  if(view.factors.length>64)host.append(paging('factors',factorPage,64,view.factors.length,page=>{factorPage=page;bookPage=0;}));
  const group=view.charts.find(c=>c.key===groupKey)??view.charts.find(c=>c.spec.series.some(s=>s.points.some(p=>p.y!==null&&signOf(p.y)!=='zero')))??view.charts[0];groupKey=group?.key;
  if(view.charts.length>1){const label=node('label','Measure and native unit '),choose=node('select');choose.setAttribute('aria-label','Risk measure and native unit');choose.append(...view.charts.map(c=>new Option(`${c.spec.title} · ${c.spec.unit}`,c.key)));choose.value=groupKey??'';choose.onchange=()=>{groupKey=choose.value;bookPage=0;draw();};label.append(choose);controls.append(label);}
  const charts=node('div');charts.className='risk-report-charts';host.append(charts);
  if(group){const {spec}=group;if(bookPage*16>=spec.series.length)bookPage=0;if(spec.series.length>16)host.append(paging('books in graphic',bookPage,16,spec.series.length,page=>{bookPage=page;}));const plot=node('div');plot.className='chart risk-result-chart';charts.append(plot);plots.push(plot);renderChart(plot,{...spec,series:spec.series.slice(bookPage*16,bookPage*16+16),kind,onSelect:factor=>selectFactor(factor)});}
  const evidence=node('details');evidence.open=true;evidence.className='risk-result-table';evidence.append(node('summary',`${riskMetricLabels[metric]} data · choose a factor for position detail · book measures a shock leaves unchanged are not listed`));
  evidence.append(table(['Factor','Shock','Book','Measure','Value','Unit'],view.factors.slice(factorPage*64,factorPage*64+64).flatMap(f=>{
   const factorButton=()=>{const button=node('button',f.factor.label);button.type='button';button.className='text-button';button.dataset.riskFactor=f.factor.id;button.onclick=()=>selectFactor(f.factor.id);return button;};
   const totals=movedTotals(f);
   if(!totals.length)return [()=>[factorButton(),`±${f.bump} ${f.factor.unit}`,'—','Moves no book measure','','']];
   return totals.map(t=>()=>[factorButton(),`±${f.bump} ${f.factor.unit}`,t.book,t.measureLabel,shown(t.values,metric),metric==='delta'?t.values.deltaUnit:metric==='gamma'?t.values.gammaUnit:t.values.currency]);
  })));host.append(evidence);
  const selectedHost=node('section');selectedHost.className='risk-factor-detail';selectedHost.tabIndex=-1;host.append(selectedHost);
  function selectFactor(id:string){const factor=view.factors.find(f=>f.factor.id===id);if(!factor)return;selected=id;detail(selectedHost,factor);selectedHost.focus({preventScroll:true});selectedHost.scrollIntoView({block:'nearest'});}
  const chosen=view.factors.find(f=>f.factor.id===selected)??view.factors[0];selected=chosen.factor.id;detail(selectedHost,chosen);
 }
 return {show(run:RunView|undefined){if(current?.key!==run?.key){factorPage=0;bookPage=0;jointState=newJointView();parametricState=newParametricView();historicalState=newHistoricalView();}current=run;draw();},reset(){current=undefined;selected=undefined;factorPage=0;bookPage=0;jointState=newJointView();parametricState=newParametricView();historicalState=newHistoricalView();clear();},refresh:draw};
}

/** Namespace a second instance of the existing run editor; labels and status references continue to point to their own controls. */
export function copyRiskEditor(host:HTMLElement,prefix:string){
 const source=document.getElementById('risk-report');if(!source)throw Error('The risk editor is unavailable. Refresh the page.');
 const clone=source.cloneNode(true) as HTMLElement;
 for(const element of [clone,...clone.querySelectorAll<HTMLElement>('[id]')])element.id=prefix+element.id;
 for(const element of clone.querySelectorAll<HTMLElement>('[for],[aria-describedby],[aria-labelledby]'))for(const attribute of ['for','aria-describedby','aria-labelledby']){
  const value=element.getAttribute(attribute);if(value)element.setAttribute(attribute,value.split(' ').map(id=>prefix+id).join(' '));
 }
 const diagnostic=document.createElement('details'),summary=document.createElement('summary');summary.textContent='Calculation diagnostics';diagnostic.append(summary,clone.querySelector('#'+prefix+'risk-work')!);clone.append(diagnostic);
 host.append(clone);
}
