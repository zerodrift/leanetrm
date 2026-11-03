import {init,type Chart} from './chart-view.js';
import {publicText} from './market-view.js';
import {plotNumber} from './plot-number.js';
import {PendingCommand} from './pending-command.js';
import {valuationReason,type ValuationReason} from './valuation-status.js';
import {surfaceLink,type SurfaceLink} from './vol-surface-link.js';
import {drawSurface3d,levelRange} from './vol-surface-3d.js';
import {richCheapColour,richCheapTone,volTheme} from './vol-surface-theme.js';

// Vol surfaces (Markets): the stored volatility surface of one option product at the selected context, fitted by a job on the
// risk queue, shown as a rich/cheap heat map, a rotatable 3D surface and one smile per expiry. Every value, fit, rich/cheap and reason
// is the backend's; the page maps them to pixels, colours and display text. Wire
// values are decimal fractions, shown as volatility points (0.012 is 1.20).
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
/** The trading context a read is made at, as the option operations state it. */
export type FuturesReadRequest={snapshotId:string;convention:string;knowledgeMode:'current'|'retained';contextId?:string};
type Right='call'|'put';
export type SurfaceSeries={series:{code:string;right:Right;strike:string;expiryAt:string;underlyingCode:string;settlement:string|null;outcome:string;marketVolatility:string|null};
 logMoneyness:string|null;fitted:string|null;richCheap:string|null;fitPoint:boolean};
export type SurfaceExpiry={expiryAt:string;underlyingCode:string;futures:string|null;status:string;points:number;rmsResidual:string|null;maxAbsResidual:string|null;
 grid:{strike:string;logMoneyness:string;fitted:string}[]};
export type VolSurfaceResult={modelId:string;strikeAxis:string[];expiries:SurfaceExpiry[];series:SurfaceSeries[]};
type Job={id:string;state:string;errorCode:string|null};
type SurfaceRead={snapshotId:string;model:string;state:'fitted'|'notFitted';revision:{id:string;knownFrom:string;inputsRevised:boolean}|null;job:Job|null;canFit:boolean;
 surface:VolSurfaceResult|null};

// The only vol model: an explicit input of every read and fit, shown read-only.
const model='smile_quadratic_log_moneyness_v1';
// How often the page reads again while a fit job is queued or running.
const checkBackMs=1500;
const active=(job:Job|null)=>!!job&&['queued','running','cancellation_requested'].includes(job.state);
// A fit refused with one of these codes queued nothing; any other failure leaves its outcome unknown until it is retried.
const definite=(code:string|undefined)=>!!code&&(code.startsWith('vol_')||['access_denied','historical_read_only','request_format','risk_job_denied'].includes(code));

/** Exact decimal text with its point moved `shift` places right, kept to `places` decimals, rounding half to even. Display only. */
export function decimalText(value:string,{shift=0,places,signed=false}:{shift?:number;places:number;signed?:boolean}):string{
 const match=/^(-?)(\d+)(?:\.(\d+))?$/.exec(value);
 if(!match)throw Error('The surface returned a value that is not exact decimal text.');
 const [,minus,whole,fraction='']=match,kept=shift+places,digits=fraction.padEnd(kept,'0'),rest=digits.slice(kept);
 let scaled=BigInt(whole+digits.slice(0,kept));
 if(rest[0]>'5'||rest[0]==='5'&&(/[1-9]/.test(rest.slice(1))||scaled%2n===1n))scaled++;
 const text=scaled.toString().padStart(places+1,'0');
 return (scaled===0n?'':minus?'-':signed?'+':'')+(places?text.slice(0,-places)+'.'+text.slice(-places):text);
}
const points=(value:string,signed=false)=>decimalText(value,{shift:2,places:2,signed});
/** A rich/cheap value as the views name it: rich or cheap by its signed volatility points, or exactly on the smile. Display only. */
export function richCheapText(value:string):string{
 const tone=richCheapTone(value);
 return tone==='neutral'?'On the smile':`${tone==='rich'?'Rich':'Cheap'} ${points(value,true)} vol pts`;
}
// Trailing zeros of an exact price, as a label shows it.
const trimmed=(value:string)=>value.includes('.')?value.replace(/0+$/,'').replace(/\.$/,''):value;

const expiryReasons:Record<string,string>={vol_fit_insufficient_strikes:'Too few solved strikes on both sides of the futures price to fit this expiry',
 vol_fit_nonpositive:'The fitted smile is not positive across its strikes'};
function expiryReason(status:string):string|null{
 if(status==='fitted')return null;
 if(!Object.hasOwn(expiryReasons,status))throw Error('The surface returned an unknown expiry status.');
 return `${expiryReasons[status]} (${status})`;
}
function outcomeReason(outcome:string):string{
 if(outcome==='at_intrinsic')return 'Settles at intrinsic value, so no volatility solves it';
 try{return valuationReason(outcome as ValuationReason,'futures_option');}catch{throw Error('The surface returned an unknown series outcome.');}
}
const jobFailures:Record<string,string>={vol_surface_no_settlements:'No settlements for this date',option_expired:'Every series has expired',
 discount_coverage:'No live expiry is inside the discount curve'};

/** The surface as the views draw it: every expiry whose series have all expired is left out with its series, and returned to be named. */
export function liveSurface(surface:VolSurfaceResult):{surface:VolSurfaceResult;expired:SurfaceExpiry[]}{
 const expired=surface.expiries.filter(e=>e.status==='option_expired'),gone=new Set(expired.map(e=>e.expiryAt));
 if(surface.series.some(s=>gone.has(s.series.expiryAt)&&s.series.outcome!=='option_expired'))throw Error('The surface lists a live series under an expired expiry.');
 return {surface:{...surface,expiries:surface.expiries.filter(e=>!gone.has(e.expiryAt)),series:surface.series.filter(s=>!gone.has(s.series.expiryAt))},expired};
}

/** One half-cell of the heat map: a series' rich/cheap, a series without one and why, or a strike its expiry does not list. */
export type HeatCell={row:number;strike:string;right:Right;kind:'value'|'gap'|'unlisted';series?:SurfaceSeries;reasons:string[]};
export function heatCells(surface:VolSurfaceResult):HeatCell[]{
 const rows=new Map(surface.expiries.map((e,i)=>[e.expiryAt,i])),reasons=surface.expiries.map(e=>expiryReason(e.status));
 const listed=new Map<string,SurfaceSeries>();
 for(const s of surface.series){
  const row=rows.get(s.series.expiryAt);
  if(row===undefined||!surface.strikeAxis.includes(s.series.strike))throw Error('The surface lists a series outside its listed expiry and strike axis.');
  const key=[row,s.series.strike,s.series.right].join('|');
  if(listed.has(key))throw Error('The surface lists a series twice.');
  listed.set(key,s);
 }
 return surface.expiries.flatMap((_,row)=>surface.strikeAxis.flatMap(strike=>(['call','put'] as const).map((right):HeatCell=>{
  const series=listed.get([row,strike,right].join('|'));
  if(!series)return {row,strike,right,kind:'unlisted',reasons:[]};
  const why=[...(series.series.outcome==='solved'?[]:[outcomeReason(series.series.outcome)]),...(reasons[row]?[reasons[row]!]:[])];
  if(series.richCheap===null&&!why.length)throw Error('The surface gives a series no rich/cheap and no reason.');
  return {row,strike,right,kind:series.richCheap===null?'gap':'value',series,reasons:series.richCheap===null?why:[]};
 })));
}

function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
 const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=publicText(text);return node;
}
const escape=(value:string)=>publicText(value).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;');
const rightLabel=(right:Right)=>right==='call'?'Call':'Put';
const codeOf=(error:unknown)=>(error as {code?:string}).code;
const messageOf=(error:unknown)=>error instanceof Error?error.message:String(error);

/** A drawn view of the surface: it highlights the linked series and is released when the surface is drawn again. */
type View={highlight(code:string|null):void;dispose():void};
// A chart in its host, resized with it; `draw` is called again whenever the host's width changes.
function mount(host:HTMLElement,draw:(chart:Chart,width:number)=>void){
 const chart=init(host,undefined,{renderer:'canvas'});let width=host.clientWidth;draw(chart,width);
 const resize=new ResizeObserver(()=>{if(host.clientWidth===width)return;width=host.clientWidth;chart.resize();draw(chart,width);});resize.observe(host);
 return {chart,dispose(){resize.disconnect();chart.dispose();}};
}
// The series of the hovered data, by its name.
const hovered=(link:SurfaceLink)=>(event:{name?:string})=>{if(event.name)link.set(event.name);};

// The rich/cheap heat map: a row per expiry and a column per strike of the backend's axis, each split into its call and put half.
function heatMap(host:HTMLElement,surface:VolSurfaceResult,cells:HeatCell[],label:(e:SurfaceExpiry)=>string,extent:number,link:SurfaceLink):View{
 const colors=volTheme(host),halves=surface.strikeAxis.flatMap(k=>[`${k}|call`,`${k}|put`]);
 const rows=surface.expiries.map(e=>label(e)+(e.status==='fitted'?'':'\nnot fitted'));
 const x=(cell:HeatCell)=>halves.indexOf(`${cell.strike}|${cell.right}`);
 const values=cells.filter(c=>c.kind==='value'),gaps=cells.filter(c=>c.kind==='gap'),unlisted=cells.filter(c=>c.kind==='unlisted');
 const at=(cell:HeatCell)=>`<b>${rightLabel(cell.right)} ${escape(cell.strike)}</b> · ${escape(label(surface.expiries[cell.row]))}`;
 function tip(params:{seriesId:string;dataIndex:number}){
  if(params.seriesId==='values'){const cell=values[params.dataIndex],rc=cell.series!.richCheap!;
   return `${at(cell)}<br>${richCheapText(rc)} · ${escape(cell.series!.series.code)}`;}
  if(params.seriesId==='gaps'){const cell=gaps[params.dataIndex];return `${at(cell)}<br>No rich/cheap: ${cell.reasons.map(escape).join('; ')}`;}
  return `${at(unlisted[params.dataIndex])}<br>Not listed for this expiry`;
 }
 host.style.height=`${surface.expiries.length*38+76}px`;
 const {chart,dispose}=mount(host,(chart,width)=>{
  const cell=(width-96)/Math.max(1,halves.length),labelled=cell>=40;
  chart.setOption({
   textStyle:{fontFamily:colors.font},animation:false,
   grid:{left:8,right:8,top:24,bottom:32,containLabel:true},
   tooltip:{trigger:'item',className:'vol-tooltip',confine:true,backgroundColor:colors.surface,borderColor:colors.border,textStyle:{color:colors.text,fontSize:12},formatter:tip},
   xAxis:[
    {id:'halves',type:'category',data:halves,position:'top',axisLine:{show:false},axisTick:{show:false},
     axisLabel:{show:cell>=12,interval:0,color:colors.text3,fontSize:10,formatter:(v:string)=>v.endsWith('|call')?'C':'P'}},
    {id:'strikes',type:'category',data:surface.strikeAxis,position:'bottom',z:3,axisLine:{show:false},axisTick:{show:false},
     axisLabel:{color:colors.text2,fontSize:11,hideOverlap:true},splitLine:{show:true,interval:0,lineStyle:{color:colors.surface,width:4}},name:'Strike',nameLocation:'middle',nameGap:22,nameTextStyle:{color:colors.text3,fontSize:11}}],
   yAxis:[{type:'category',data:rows,inverse:true,axisLine:{show:false},axisTick:{show:false},axisLabel:{color:colors.text2,fontSize:11,lineHeight:14}}],
   visualMap:{type:'continuous',seriesIndex:0,show:false,min:-extent,max:extent,inRange:{color:[colors.cheap,colors.surface3,colors.rich]}},
   series:[
    {id:'values',name:'Rich / cheap',type:'heatmap',data:values.map(c=>({name:c.series!.series.code,value:[x(c),c.row,plotNumber(c.series!.richCheap!)]})),
     itemStyle:{borderColor:colors.surface,borderWidth:1,borderRadius:3},emphasis:{itemStyle:{borderColor:colors.text,borderWidth:2}},
     label:{show:labelled,fontSize:10,formatter:(p:{dataIndex:number;value:number[]})=>`{${Math.abs(p.value[2])>extent*.55?'light':'dark'}|${points(values[p.dataIndex].series!.richCheap!,true)}}`,
      rich:{light:{color:colors.surface,fontSize:10},dark:{color:colors.text,fontSize:10}}}},
    {id:'gaps',name:'No rich/cheap',type:'heatmap',data:gaps.map(c=>({name:c.series!.series.code,value:[x(c),c.row,0]})),
     itemStyle:{color:colors.surface2,borderColor:colors.surface,borderWidth:1,borderRadius:3,decal:{symbol:'rect',symbolSize:1,dashArrayX:[1,0],dashArrayY:[1,3],rotation:-Math.PI/4,color:colors.borderStrong}},
     emphasis:{itemStyle:{borderColor:colors.text,borderWidth:2}}},
    {id:'unlisted',name:'Not listed',type:'heatmap',data:unlisted.map(c=>({value:[x(c),c.row,0]})),
     itemStyle:{color:'transparent',borderColor:colors.border,borderWidth:1,borderType:'dashed',borderRadius:3},emphasis:{disabled:true}}],
  },{notMerge:true});
 });
 chart.on('mouseover',hovered(link));
 return {
  highlight(code){
   chart.dispatchAction({type:'downplay'});
   for(const [index,list] of [values,gaps].entries()){const dataIndex=list.findIndex(c=>c.series!.series.code===code);if(dataIndex>=0)chart.dispatchAction({type:'highlight',seriesIndex:index,dataIndex});}
  },
  dispose,
 };
}

// One expiry's smile: its fitted grid line, its market points shaped by right (hollow where not fitted) and its rich/cheap bars below.
function smile(host:HTMLElement,expiry:SurfaceExpiry,members:SurfaceSeries[],extent:number,link:SurfaceLink):View{
 const colors=volTheme(host),solved=members.filter(s=>s.series.marketVolatility!==null);
 const strikes=members.map(s=>plotNumber(s.series.strike)),low=Math.min(...strikes),high=Math.max(...strikes),room=(high-low||1)*.06;
 const span={min:low-room,max:high+room};
 const byRight=(right:Right)=>solved.filter(s=>s.series.right===right);
 const market=(right:Right)=>byRight(right).map(s=>({name:s.series.code,value:[plotNumber(s.series.strike),plotNumber(s.series.marketVolatility!)*100],
  itemStyle:s.fitPoint?{color:colors.text2}:{color:colors.surface,borderColor:colors.text2,borderWidth:1.5}}));
 const bars=(right:Right)=>byRight(right).filter(s=>s.richCheap!==null).map(s=>({name:s.series.code,value:[plotNumber(s.series.strike),plotNumber(s.richCheap!)*100],
  itemStyle:{color:richCheapColour(colors,s.richCheap!),opacity:right==='call'?1:.6}}));
 const tip=(p:{name:string})=>{const s=members.find(m=>m.series.code===p.name);if(!s)return '';
  return `<b>${rightLabel(s.series.right)} ${escape(s.series.strike)}</b><br>Market ${points(s.series.marketVolatility!)} · fitted ${s.fitted===null?'—':points(s.fitted)}`+
   (s.richCheap===null?'':`<br>${richCheapText(s.richCheap)}`);};
 const series=[
  {id:'fitted',type:'line',data:expiry.grid.map(g=>[plotNumber(g.strike),plotNumber(g.fitted)*100]),showSymbol:false,silent:true,lineStyle:{color:colors.accent,width:2},z:1},
  {id:'calls',type:'scatter',symbol:'circle',symbolSize:8,data:market('call'),emphasis:{scale:1.8,itemStyle:{borderColor:colors.text,borderWidth:2}},z:3},
  {id:'puts',type:'scatter',symbol:'triangle',symbolSize:9,data:market('put'),emphasis:{scale:1.8,itemStyle:{borderColor:colors.text,borderWidth:2}},z:3},
  {id:'call-bars',type:'bar',xAxisIndex:1,yAxisIndex:1,barMaxWidth:6,data:bars('call'),emphasis:{itemStyle:{borderColor:colors.text,borderWidth:1}}},
  {id:'put-bars',type:'bar',xAxisIndex:1,yAxisIndex:1,barMaxWidth:6,data:bars('put'),emphasis:{itemStyle:{borderColor:colors.text,borderWidth:1}}}];
 const axis={axisLine:{lineStyle:{color:colors.border}},axisTick:{show:false},splitLine:{lineStyle:{color:colors.border,type:'dashed'}}};
 const {chart,dispose}=mount(host,chart=>chart.setOption({
  textStyle:{fontFamily:colors.font},animation:false,
  tooltip:{trigger:'item',className:'vol-tooltip',confine:true,backgroundColor:colors.surface,borderColor:colors.border,textStyle:{color:colors.text,fontSize:12},formatter:tip},
  grid:[{left:40,right:12,top:12,height:148},{left:40,right:12,top:176,height:44}],
  xAxis:[{...axis,type:'value',gridIndex:0,...span,axisLabel:{show:false}},{...axis,type:'value',gridIndex:1,...span,splitLine:{show:false},axisLabel:{color:colors.text3,fontSize:10,hideOverlap:true,showMinLabel:false,showMaxLabel:false}}],
  yAxis:[{...axis,type:'value',gridIndex:0,scale:true,axisLabel:{color:colors.text3,fontSize:10}},
   {...axis,type:'value',gridIndex:1,min:-extent*100,max:extent*100,splitNumber:1,axisLabel:{show:false},splitLine:{show:false},axisLine:{show:true,onZero:true,lineStyle:{color:colors.borderStrong}}}],
  series,
 },{notMerge:true}));
 chart.on('mouseover',hovered(link));
 const indexed=series.map(s=>(s.data as {name?:string}[]).map(d=>d.name));
 return {
  highlight(code){
   chart.dispatchAction({type:'downplay'});if(code===null)return;
   indexed.forEach((names,seriesIndex)=>{const dataIndex=names.indexOf(code);if(dataIndex>=0)chart.dispatchAction({type:'highlight',seriesIndex,dataIndex});});
  },
  dispose,
 };
}

/**
 * The Vol surfaces page. `refresh` reads the products listed at the context and the chosen product's surface; `open` shows a
 * product's surface, as an option's link to it does; `reset` clears what an earlier context showed.
 */
export function setupVolSurfaces(o:{api:Api;context:()=>FuturesReadRequest;historical:()=>boolean;zone:()=>string}){
 const root=document.getElementById('vol-surfaces-page')!;root.dataset.ownControls='';
 const link=surfaceLink(),pending=new PendingCommand(definite);
 const productSelect=el('select');productSelect.id='vol-product';
 const productRow=el('div','vol-field'),productLabel=el('label',undefined,'Option product');productLabel.htmlFor='vol-product';productRow.append(productLabel,productSelect);
 const modelRow=el('div','vol-field'),modelValue=el('code','vol-model',model);modelValue.id='vol-model';modelRow.append(el('span','vol-field-label','Vol model'),modelValue);
 const legend=el('p','vol-legend','Rich: the market’s implied volatility is above the fitted smile. Cheap: below it. On the smile: exactly on it. In volatility points.');legend.id='vol-legend';
 const controls=el('div','vol-controls');controls.append(productRow,modelRow,legend);
 const status=el('p','vol-status');status.id='vol-fit-status';status.setAttribute('role','status');
 const actions=el('div','vol-fit-actions');
 const fit=el('button');fit.id='vol-fit';fit.type='button';
 const failure=el('span','vol-fit-failure');failure.id='vol-fit-failure';
 const retry=el('button','secondary','Retry the fit request');retry.id='vol-fit-retry';retry.type='button';
 const note=el('span','vol-fit-note');
 actions.append(fit,note,failure,retry);
 const fitBar=el('div','vol-fit-bar');fitBar.append(status,actions);
 const error=el('p','vol-error');error.id='vol-surface-error';error.setAttribute('role','alert');
 const heat=el('section','vol-card vol-heat');heat.id='vol-heatmap';
 const heatHead=el('div','vol-card-head'),scale=el('div','vol-scale'),halves=el('p','vol-halves');
 halves.append('Each strike: ',el('b',undefined,'C'),' Call · ',el('b',undefined,'P'),' Put · ',el('span','vol-key-gap'),' no rich/cheap · ',el('span','vol-key-unlisted'),' not listed');
 heatHead.append(el('h3',undefined,'Rich / cheap'),scale);
 const heatChart=el('div','vol-chart');heatChart.tabIndex=0;heatChart.setAttribute('role','img');heatChart.setAttribute('aria-label','Rich/cheap heat map. Use the arrow keys to move between series.');
 heat.append(heatHead,halves,heatChart);
 const readout=el('aside','vol-card vol-readout');readout.id='vol-readout';readout.setAttribute('aria-live','polite');
 // The rotatable 3D surface: its canvas is drawn afresh for each stored revision, with a legend whose shading scale is that revision's.
 const surface3d=el('section','vol-card vol-surface-3d');surface3d.id='vol-surface-3d';
 const resetView=el('button','secondary','Reset view');resetView.id='vol-surface-reset';resetView.type='button';
 const head3d=el('div','vol-card-head'),tools3d=el('div','vol-3d-tools');tools3d.append(el('span','vol-3d-hint','Drag or use the arrow keys to rotate'),resetView);
 head3d.append(el('h3',undefined,'Surface'),tools3d);
 const canvasHost=el('div','vol-3d-canvas'),levelScale=el('span','vol-3d-levels');
 const legend3d=el('p','vol-3d-legend');
 // Each key item keeps its swatch with its own words when the legend wraps.
 const item=(...parts:(Node|string)[])=>{const span=el('span','vol-3d-key');span.append(...parts);return span;};
 legend3d.append(item(el('span','vol-key-row'),'Rows: the fitted smile at each expiry'),
  item(el('span','vol-key-join'),'Straight panels between expiries: a visual join, not a fit, shaded by volatility level',levelScale),
  item('Stalks: from the fitted smile to the market’s implied volatility,',el('span','vol-key-rich'),'rich above,',el('span','vol-key-cheap'),'cheap below'));
 surface3d.append(head3d,canvasHost,legend3d);
 const smiles=el('section','vol-smiles');smiles.id='vol-smiles';
 const views=el('div','vol-views');views.append(heat,readout,surface3d,smiles);
 root.replaceChildren(controls,fitBar,error,views);

 // The generations of the reads under way: only a read of the current product and context stands for the next one.
 const reading:number[]=[];
 let products:{code:string;name:string}[]=[],product='',shown:SurfaceRead|undefined,drawn:View[]=[],view3d:{reset():void;dispose():void}|undefined,cells:HeatCell[]=[],drawnFor:VolSurfaceResult|undefined,drawnRevision='';
 let generation=0,timer:ReturnType<typeof setTimeout>|undefined,submitting=false,fitMessage='';
 const expiryLabel=(e:SurfaceExpiry)=>new Intl.DateTimeFormat('en',{month:'short',day:'numeric',year:'numeric',timeZone:o.zone()}).format(new Date(e.expiryAt));
 const instant=(at:string)=>new Intl.DateTimeFormat('en',{month:'short',day:'numeric',year:'numeric',hour:'numeric',minute:'2-digit',timeZone:o.zone()}).format(new Date(at));

 function state(next:'idle'|'loading'|'noProducts'|'notFitted'|'fitted'|'error',message=''){
  root.dataset.state=next;error.textContent=next==='error'?publicText(message):'';error.hidden=next!=='error';
  views.hidden=next!=='fitted';if(next!=='fitted')clearViews();
  if(next!=='notFitted'&&next!=='fitted'){status.textContent=next==='loading'?'Reading the surface…':publicText(next==='error'?'':message);actions.hidden=true;}
 }
 function clearViews(){
  for(const view of drawn)view.dispose();view3d?.dispose();drawn=[];view3d=undefined;cells=[];drawnFor=undefined;drawnRevision='';link.set(null);
  smiles.replaceChildren();scale.replaceChildren();canvasHost.replaceChildren();levelScale.replaceChildren();renderReadout(null);
 }
 function stop(){clearTimeout(timer);timer=undefined;}
 const scopeOf=()=>JSON.stringify([o.context(),product]);
 function failed(error:unknown){state('error',`${messageOf(error)}${codeOf(error)&&!messageOf(error).includes(codeOf(error)!)?` (${codeOf(error)})`:''}`);}

 // Each read is numbered: an answer to a read that a newer one replaced is dropped. While the read reports a fit job queued or
 // running, one timer reads again, unless the product, context or page changed or the page is hidden.
 async function read(number:number){
  const context=o.context(),scope=scopeOf();
  let answer:SurfaceRead;
  reading.push(number);try{answer=await o.api<SurfaceRead>('vol-surfaces/read',{context,product,model});}finally{reading.splice(reading.indexOf(number),1);}
  if(number!==generation)return;
  if(answer.model!==model||answer.snapshotId!==context.snapshotId||(answer.state==='fitted')!==!!answer.surface||answer.surface&&answer.surface.modelId!==model)
   throw Error('The surface read does not match the selected product, model and context. Refresh and try again.');
  shown=answer;render();stop();
  if(active(answer.job)&&!document.hidden)timer=setTimeout(()=>{timer=undefined;
   let same=false;try{same=scopeOf()===scope;}catch{/* an unapplied context reads nothing */}
   if(number===generation&&same&&!root.hidden)void read(number).catch(error=>{if(number===generation)failed(error);});
  },checkBackMs);
 }
 // A newly chosen product or context: what was shown and any read or check-back under way are given up.
 function restart(){stop();generation++;pending.clear();fitMessage='';shown=undefined;return generation;}
 async function readProduct(){
  const number=restart();
  if(!products.length){state('noProducts','No option products in the reference data at this date.');return;}
  if(!product){state('idle','Choose an option product to see its surface.');return;}
  state('loading');
  try{await read(number);}catch(error){if(number===generation)failed(error);}
 }
 // The option products of the reference data in force at the context, every page of them. A product's surface is read whether or not it
 // lists series at the close; the read says when none is fitted.
 async function optionProducts(){
  const found:{code:string;name:string}[]=[];
  for(let offset=0;;){
   const page=await o.api<{rows:Record<string,string|number|null>[];offset:number;pageSize:number;hasMore:boolean}>('reference/catalog?'+new URLSearchParams({table:'futures_option_products',offset:String(offset)}));
   found.push(...page.rows.map(row=>({code:String(row.code),name:String(row.name)})));
   if(!page.hasMore)return found;
   offset=page.offset+page.pageSize;
  }
 }
 async function refresh(){
  const number=restart();state('loading');
  try{
   o.context(); // an unapplied context reads nothing
   const listed=await optionProducts();
   if(number!==generation)return;
   products=listed;
   productSelect.replaceChildren(new Option(products.length?'Choose…':'No option products',''),...products.map(p=>new Option(publicText(`${p.name} · ${p.code}`),p.code)));
   productSelect.disabled=!products.length;
   // A product asked for, or chosen before, that the context does not list is said, never swapped for another or for none.
   const missing=product&&!products.some(p=>p.code===product)?product:'';
   if(missing)product='';
   productSelect.value=product;
   if(missing){failed(Error(`Option product ${missing} is not in the reference data at this context.`));return;}
   await readProduct();
  }catch(error){if(number===generation)failed(error);}
 }
 productSelect.onchange=()=>{product=productSelect.value;void readProduct();};
 // A hidden page stops checking back; shown again, it reads at once unless a read is already under way.
 document.addEventListener('visibilitychange',()=>{
  if(document.hidden){stop();return;}
  if(!root.hidden&&!reading.includes(generation)&&active(shown?.job??null)){const number=generation;void read(number).catch(error=>{if(number===generation)failed(error);});}
 });

 function render(){
  const read=shown!;
  const job=read.job,running=active(job),revision=read.revision;
  status.replaceChildren();
  if(running)status.append(el('span','vol-spinner'),`Fit ${job!.state==='cancellation_requested'?'cancelling':job!.state} · checking again every few seconds`);
  else if(read.state==='fitted'&&revision){
   status.append(`Fitted · known from ${instant(revision.knownFrom)}`);
   if(revision.inputsRevised)status.append(' ',el('span','vol-chip vol-chip-warn','Inputs revised after this fit'));
  }else status.append('Not fitted for this date');
  // Fitting is offered only to those who may, and only in the current workspace, where a fit is admitted.
  const historical=o.historical();
  fit.hidden=!read.canFit||historical||running||!!pending.command;fit.textContent=read.state==='fitted'?'Refit':'Fit now';
  note.textContent=submitting?'Submitting the fit…':!read.canFit?'Fitting needs Publish market data and Read trades.':historical&&!running?'Fit in the current workspace':'';
  const ended=!running&&job&&(job.state==='failed'||job.state==='cancelled');
  failure.textContent=fitMessage||(ended?`Last fit ${job!.state==='failed'?'failed':'was cancelled'}${job!.errorCode?` · ${jobFailures[job!.errorCode]?jobFailures[job!.errorCode]+' ':''}${job!.errorCode}`:''}`:'');
  retry.hidden=!pending.command||submitting;
  actions.hidden=false;
  root.dataset.state=read.state;error.hidden=true;views.hidden=read.state!=='fitted';
  if(read.state==='fitted')drawSurface(read.surface!,revision!.id);else clearViews();
 }

 async function submit(){
  if(submitting)return;
  let command;try{const c=o.context();command=pending.begin('vol-surfaces/fit',{snapshotId:c.snapshotId,product,model});}catch(error){fitMessage=messageOf(error);render();return;}
  const number=generation;submitting=true;fitMessage='';render();
  const previouslyUncertain=pending.sending();
  let queued=false;
  try{await o.api('vol-surfaces/fit',{requestId:command.operationId,...command.body});pending.clear();queued=true;}
  catch(error){
   pending.failed(codeOf(error),previouslyUncertain);
   fitMessage=pending.command?`The fit request is not confirmed: ${messageOf(error)} Retry it to submit the same request.`:`Fit refused: ${messageOf(error)}`;
  }finally{submitting=false;}
  if(number!==generation)return;
  // The job is read back as the surface reports it, and checked on from there.
  if(queued)await read(number).catch(error=>{if(number===generation)failed(error);});else render();
 }
 fit.onclick=()=>void submit();retry.onclick=()=>void submit();
 resetView.onclick=()=>view3d?.reset();

 // The surface's views, drawn for each stored revision a read returns; reading the same revision again, as checking on a refit
 // does, keeps them as drawn.
 function drawSurface(listed:VolSurfaceResult,revision:string){
  if(drawnRevision===revision)return;
  const keep=link.current();clearViews();const {surface,expired}=liveSurface(listed);drawnFor=surface;drawnRevision=revision;
  cells=heatCells(surface);
  const rich=surface.series.filter(s=>s.richCheap!==null);
  const largest=rich.reduce<SurfaceSeries|undefined>((top,s)=>!top||Math.abs(plotNumber(s.richCheap!))>Math.abs(plotNumber(top.richCheap!))?s:top,undefined);
  const extent=largest?Math.abs(plotNumber(largest.richCheap!))||1:1;
  if(largest){const end=points(largest.richCheap!.replace(/^-/,''));scale.append(el('span',undefined,'Cheap'),el('span','vol-scale-end','-'+end),el('span','vol-scale-bar'),el('span','vol-scale-end','+'+end),el('span',undefined,'Rich'));}
  drawn.push(heatMap(heatChart,surface,cells,expiryLabel,extent,link));
  const levels=levelRange(surface);
  if(levels){
   const canvas=el('canvas');canvasHost.append(canvas);
   levelScale.append(el('span','vol-scale-end',points(levels.low)),el('span','vol-3d-level-bar'),el('span','vol-scale-end',`${points(levels.high)} vol pts`));
   view3d=drawSurface3d(canvas,surface,link,expiryLabel);
  }else canvasHost.append(el('p','vol-gap','No expiry is fitted at this date, so there is no surface to draw.'));
  for(const expiry of surface.expiries){
   const members=surface.series.filter(s=>s.series.expiryAt===expiry.expiryAt),card=el('article','vol-card vol-smile');card.dataset.expiry=expiry.expiryAt;
   const head=el('header','vol-smile-head'),title=el('h4',undefined,expiryLabel(expiry)),sub=el('p','vol-smile-sub',`${expiry.underlyingCode}${expiry.futures===null?' · no futures settlement':` · futures ${trimmed(expiry.futures)}`}`);
   head.append(title,sub);card.append(head);
   if(expiry.status==='fitted'){
    head.append(el('p','vol-smile-stats',`RMS ${points(expiry.rmsResidual!)} · Max ${points(expiry.maxAbsResidual!)} vol pts · ${expiry.points} fit points`));
    const host=el('div','vol-chart');card.append(host);smiles.append(card);
    drawn.push(smile(host,expiry,members,extent,link));
   }else{card.append(el('p','vol-gap',`Not fitted: ${expiryReason(expiry.status)}`));smiles.append(card);}
  }
  smiles.prepend(el('p','vol-smiles-key','● call · ▲ put · hollow: not a fit point · line: fitted smile · bars: rich/cheap'));
  if(expired.length)smiles.prepend(el('p','vol-expired',`Expired, not shown: ${expired.map(expiryLabel).join(', ')}`));
  if(keep&&surface.series.some(s=>s.series.code===keep))link.set(keep);
 }

 function renderReadout(code:string|null){
  const s=code===null?undefined:drawnFor?.series.find(m=>m.series.code===code);
  readout.replaceChildren(el('h3',undefined,'Selected series'));
  if(!s){readout.append(el('p','vol-readout-empty','Hover or focus a cell or point to compare one series across the views.'));return;}
  const list=el('dl'),row=(term:string,value:string)=>list.append(el('dt',undefined,term),el('dd',undefined,value));
  row('Series',s.series.code);row('Right',rightLabel(s.series.right));row('Strike',s.series.strike);
  row('k',s.logMoneyness===null?'—':decimalText(s.logMoneyness,{places:4}));
  row('Market',s.series.marketVolatility===null?'—':`${points(s.series.marketVolatility)} vol pts`);
  row('Fitted',s.fitted===null?'—':`${points(s.fitted)} vol pts`);
  row('Rich / cheap',s.richCheap===null?'—':`${points(s.richCheap,true)} vol pts`);
  readout.append(list);
  const why=cells.find(c=>c.series===s)?.reasons??[];
  if(why.length)readout.append(el('p','vol-readout-reason',why.join('; ')));
 }
 link.subscribe(code=>{
  renderReadout(code);for(const view of drawn)view.highlight(code);
  for(const card of smiles.querySelectorAll<HTMLElement>('.vol-smile')){
   const owns=!!code&&drawnFor?.series.some(s=>s.series.code===code&&s.series.expiryAt===card.dataset.expiry);
   if(owns)card.dataset.linked=code!;else delete card.dataset.linked;
  }
 });
 // The arrow keys move the linked series between the heat map's listed cells.
 heatChart.onkeydown=event=>{
  const moves:Record<string,[number,number]>={ArrowLeft:[0,-1],ArrowRight:[0,1],ArrowUp:[-1,0],ArrowDown:[1,0]};
  const move=moves[event.key],listed=cells.filter(c=>c.series);if(!move||!listed.length)return;
  event.preventDefault();
  const halves=drawnFor!.strikeAxis.flatMap(k=>[`${k}|call`,`${k}|put`]),column=(c:HeatCell)=>halves.indexOf(`${c.strike}|${c.right}`);
  const from=listed.find(c=>c.series!.series.code===link.current());
  if(!from){link.set(listed[0].series!.series.code);return;}
  const ahead=listed.filter(c=>move[0]?Math.sign(c.row-from.row)===move[0]:c.row===from.row&&Math.sign(column(c)-column(from))===move[1]);
  const distance=(c:HeatCell)=>Math.abs(c.row-from.row)*1000+Math.abs(column(c)-column(from));
  const next=ahead.sort((a,b)=>distance(a)-distance(b))[0];if(next)link.set(next.series!.series.code);
 };

 state('idle','Choose an option product to see its surface.');
 return {
  refresh,
  async open(code:string){product=code;await refresh();},
  // Left for another page: what was drawn is released, and no read or check-back under way draws it again while away.
  leave(){restart();clearViews();},
  reset(){restart();state('idle','Apply the selected context to see its surfaces.');},
 };
}
