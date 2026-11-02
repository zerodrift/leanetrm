import {node,table,type Cells} from './risk-report-elements.js';
import {renderChart} from './chart-view.js';
import {jointCharts,jointLabels,type JointReport,type JointMetric,type JointDerivative} from './joint-risk.js';

export type JointViewState={metric:JointMetric;kind:'bar'|'heatmap';selected?:string;group?:string;bookPage:number};
export const newJointView=():JointViewState=>({metric:'loss',kind:'bar',bookPage:0});
const amount=(exact:string,display:string)=>{const value=node('span',display);value.dataset.exact=exact;if(exact!==display){const receipt=node('small','Exact: '+exact);receipt.className='risk-exact-value';value.append(receipt);}return value;};
const formulas={delta:'central_first_difference_v1',gamma:'central_diagonal_second_difference_v1',mixedGamma:'central_mixed_second_difference_v1'};
export function renderJointResults(host:HTMLElement,joint:JointReport,state:JointViewState,redraw:()=>void,plots:HTMLElement[]){
 const scenarios=joint.kind==='scenarios';
 const metrics:JointMetric[]=scenarios?['loss','pnl']:['delta','gamma','mixedGamma'];
 if(!metrics.includes(state.metric))state.metric=metrics[0];
 const controls=node('div');controls.className='risk-result-controls';
 for(const metric of metrics){const button=node('button',jointLabels[metric]);button.type='button';button.className='secondary';button.dataset.jointMetric=metric;button.setAttribute('aria-pressed',String(metric===state.metric));button.onclick=()=>{state.metric=metric;state.selected=undefined;state.bookPage=0;redraw();};controls.append(button);}
 const graphic=node('label','Graphic '),kind=node('select');kind.setAttribute('aria-label','Joint risk graphic');kind.append(new Option('Scenario / derivative ladder','bar'),new Option('Book heatmap','heatmap'));kind.value=state.kind;kind.onchange=()=>{state.kind=kind.value as typeof state.kind;redraw();};graphic.append(kind);controls.append(graphic);host.append(controls);
 host.append(node('p',scenarios?'Each named scenario moves its selected factors together from the same captured parent. These deterministic stresses have no assigned probabilities.':'Half, nominal and double bump estimates use the selected absolute and relative tolerances. Agreement describes these three steps; full repricing remains available separately.'));
 const charts=jointCharts(joint,state.metric),group=charts.find(g=>g.key===state.group)??charts[0];state.group=group?.key;
 if(charts.length>1){const label=node('label','Measure and native unit '),choose=node('select');choose.setAttribute('aria-label','Joint risk measure and native unit');choose.append(...charts.map(c=>new Option(c.spec.title+' · '+c.spec.unit,c.key)));choose.value=group!.key;choose.onchange=()=>{state.group=choose.value;state.bookPage=0;redraw();};label.append(choose);controls.append(label);}
 if(group){
  if(state.bookPage*16>=group.spec.series.length)state.bookPage=0;
  if(group.spec.series.length>16){const page=node('div');page.className='actions';for(const [label,direction,disabled] of [['Previous books',-1,state.bookPage===0],['Next books',1,(state.bookPage+1)*16>=group.spec.series.length]] as const){const button=node('button',label);button.type='button';button.className='secondary';button.disabled=disabled;button.onclick=()=>{state.bookPage+=direction;redraw();};page.append(button);}page.append(node('span','Graphic displays up to 16 books at a time; tables retain every reported total.'));host.append(page);}
  const plot=node('div');plot.className='chart risk-result-chart';host.append(plot);plots.push(plot);
  renderChart(plot,{...group.spec,kind:state.kind,series:group.spec.series.slice(state.bookPage*16,state.bookPage*16+16),onSelect:id=>{state.selected=id;redraw();}});
 }else host.append(node('p','No selected derivative of this kind was requested. Empty cells are unavailable, not zero.'));
 const base=node('details');base.append(node('summary','Captured base values by book and measure'),table(['Book','Measure','Base value','Currency'],joint.baseTotals.map(v=>[v.book,v.measureLabel,amount(v.value,v.display),v.currency])));host.append(base);
 const detail=node('section');detail.className='risk-factor-detail';
 const choose=(id:string,label:string)=>{const button=node('button',label);button.type='button';button.className='text-button';button.dataset.jointResult=id;button.onclick=()=>{state.selected=id;redraw();};return button;};
 if(scenarios){
  host.append(table(['Scenario','Book','Measure','Base','Stressed value',jointLabels[state.metric],'Currency'],joint.samples.flatMap(s=>s.totals.map(t=>[
   choose(s.id,s.label),t.book,t.measureLabel,amount(t.values.base,t.values.display.base),amount(t.values.value,t.values.display.value),amount(t.values[state.metric==='pnl'?'pnl':'loss'],t.values.display[state.metric==='pnl'?'pnl':'loss']),t.values.currency]))));
  const selected=joint.samples.find(s=>s.id===state.selected)??joint.samples[0];state.selected=selected?.id;
  if(selected){detail.append(node('h3',selected.label),table(['Target','Coordinate','Signed shift','Native price unit'],selected.shifts.map(s=>[s.factor.label,s.factor.coordinate==='market_quote'?'Shared market factor':'Computed output',s.size,s.factor.unit])));
   const positions=node('details');positions.append(node('summary',`Trade and instrument bindings · ${selected.positions.length}`),table(['Trade','Instrument / binding','Book','Measure','Base','Stressed value','P&L','Loss','Currency'],selected.positions.map(p=>[p.tradeReference,`${p.instrumentReference} · ${p.bindingId}`,p.book,p.measureLabel,amount(p.values.base,p.values.display.base),amount(p.values.value,p.values.display.value),amount(p.values.pnl,p.values.display.pnl),amount(p.values.loss,p.values.display.loss),p.values.currency])));detail.append(positions);}
 }else{
  const derivatives=joint.derivatives.filter(d=>d.formula===formulas[state.metric as keyof typeof formulas]);
  const label=(d:JointDerivative)=>d.axes.map(id=>joint.axes.find(a=>a.id===id)?.factor.label??id).join(' × ');
  const rows=derivatives.flatMap(d=>d.totals.map(t=>[choose(d.formula+'/'+d.id,label(d)),t.book,t.measureLabel,amount(t.values.half,t.values.display.half),amount(t.values.nominal,t.values.display.nominal),amount(t.values.double,t.values.display.double),t.values.stable?'Agrees':'Does not agree',t.values.unit,`${t.values.tolerance.absolute} absolute + ${t.values.tolerance.relative} relative`] satisfies Cells));
  host.append(table(['Axis / selected pair','Book','Measure','Half step','Nominal step','Double step','Step agreement','Native sensitivity unit','Selected tolerance'],rows));
  const selected=derivatives.find(d=>d.formula+'/'+d.id===state.selected)??derivatives[0];state.selected=selected?selected.formula+'/'+selected.id:undefined;
  if(selected){detail.append(node('h3',label(selected)));
   const positions=node('details');positions.append(node('summary',`Trade and instrument bindings · ${selected.positions.length}`),table(['Trade','Instrument / binding','Book','Measure','Half step','Nominal step','Double step','Step agreement','Native unit'],selected.positions.map(p=>[p.tradeReference,`${p.instrumentReference} · ${p.bindingId}`,p.book,p.measureLabel,amount(p.values.half,p.values.display.half),amount(p.values.nominal,p.values.display.nominal),amount(p.values.double,p.values.display.double),p.values.stable?'Agrees':'Does not agree',p.values.unit])));detail.append(positions);}
 }
 if((scenarios?joint.samples[0]?.positions.length:joint.derivatives[0]?.positions.length)===0)detail.append(node('p','This report retains book totals only. Position detail was not requested.'));
 host.append(detail);
}
