import {dispose,init,registerIndicator,type Chart,type DeepPartial,type KLineData,type Styles} from './vendor/klinecharts.js';
import {palette} from './chart-view.js';
import {displayInstant,timeLabels} from './market-view.js';
import {plotNumber} from './plot-number.js';
// Prices over time. The backend states every value (and every overlay value); this module only places them. It computes no
// statistic and no overlay: an overlay is drawn from the series the server returned, found by instant.

export type TimePoint={at:string;value:string|null;label?:string};
export type OverlayPoint={at:string;value:string|null;reason?:string|null};
export type OverlaySeries={code:string;label:string;parameters:Record<string,number>;policy:string;series:Record<string,OverlayPoint[]>};
export type OverlayCatalog={policy:string;overlays:{code:string;label:string;parameters:{name:string;label:string;minimum:number;maximum:number}[];seriesNames:string[]}[]};
export type OverlayRequest={code:string;parameters:Record<string,number>};
export type TimeChartSpec={title:string;unit:string;points:TimePoint[];overlays:OverlaySeries[];zone:string};
export type TimeChart={update(spec:TimeChartSpec):void;dispose():void};
export type TimeBar={timestamp:number;close:number|null};

const instantOf=(at:string)=>{const time=Date.parse(at);if(!Number.isFinite(time))throw Error(`A time chart needs an instant, not "${at}".`);return time;};

/** One bar per backend point, in time order. A point without a price keeps a null close, which the chart draws as a gap. */
export function timeChartData(points:TimePoint[]):TimeBar[]{
  let previous=-Infinity;
  return points.map(point=>{
    const timestamp=instantOf(point.at);
    if(timestamp<=previous)throw Error('A time chart needs points in ascending order with one point per instant.');
    previous=timestamp;
    return {timestamp,close:point.value===null?null:plotNumber(point.value)};
  });
}

/** Finds the values the backend returned for an instant. It does no arithmetic and never fills a missing value. */
export function overlayLookup(overlay:OverlaySeries):(timestamp:number)=>Record<string,number|undefined>{
  const named=Object.entries(overlay.series).map(([name,points])=>{
    const byInstant=new Map<number,number|undefined>();
    for(const point of points){
      const timestamp=instantOf(point.at);
      if(byInstant.has(timestamp))throw Error(`The ${overlay.label} series ${name} repeats an instant.`);
      byInstant.set(timestamp,point.value===null?undefined:plotNumber(point.value));
    }
    return [name,byInstant] as const;
  });
  return timestamp=>Object.fromEntries(named.map(([name,byInstant])=>[name,byInstant.get(timestamp)]));
}

/** The definition and the parameters the read sent, for example "Simple moving average · 5". */
export function overlayLegend(overlay:Pick<OverlaySeries,'label'|'parameters'>){
  const entries=Object.entries(overlay.parameters).sort(([a],[b])=>a<b?-1:1);
  return `${overlay.label} · ${entries.length===1?entries[0][1]:entries.map(([name,value])=>`${name} ${value}`).join(', ')}`;
}

const reasonText=(reason:string|null|undefined)=>reason?`No value (${reason.replaceAll('_',' ')})`:'No value';
/** The latest price and each overlay's latest value as text, exactly as the backend states them. */
export function latestValues(spec:Pick<TimeChartSpec,'unit'|'points'|'overlays'>){
  const last=spec.points.at(-1);
  return [{name:'Price',value:last?.value==null?'No price':`${last.value} ${spec.unit}`},
    ...spec.overlays.flatMap(overlay=>Object.entries(overlay.series).map(([series,points])=>{
      const point=points.at(-1);
      return {name:`${overlayLegend(overlay)} · ${series}`,value:point?.value==null?reasonText(point?.reason):point.value};
    }))];
}

export type OverlayMenuState={requests:OverlayRequest[];canApply:boolean;problem:string|null};
/**
 * What the menu would send. A definition with no entered parameter is not requested; one with a missing, non-integer or
 * out-of-range parameter blocks Apply. Nothing is defaulted.
 */
export function overlayMenuState(catalog:OverlayCatalog,entered:Record<string,Record<string,string>>):OverlayMenuState{
  const requests:OverlayRequest[]=[];let problem:string|null=null;
  for(const definition of catalog.overlays){
    const texts=definition.parameters.map(p=>entered[definition.code]?.[p.name]??'');
    if(texts.every(text=>text===''))continue;
    const parameters:Record<string,number>={};
    definition.parameters.forEach((p,i)=>{
      const value=/^\d+$/.test(texts[i])?Number(texts[i]):NaN;
      if(value>=p.minimum&&value<=p.maximum)parameters[p.name]=value;
      else problem??=`${definition.label}: ${p.label} is a whole number from ${p.minimum} to ${p.maximum}.`;
    });
    if(Object.keys(parameters).length===definition.parameters.length)requests.push({code:definition.code,parameters});
  }
  return {requests,canApply:problem===null&&requests.length>0,problem};
}

/** The overlay menu. It collects explicit parameters only; the page sends them with the next read. */
export function overlayMenu(host:HTMLElement,catalog:OverlayCatalog,onApply:(requests:OverlayRequest[])=>void){
  const form=document.createElement('form');form.className='overlay-menu';
  const entered:Record<string,Record<string,string>>={};
  const status=document.createElement('p');status.className='fine-print';status.setAttribute('role','status');
  const apply=document.createElement('button');apply.type='submit';apply.textContent='Apply';
  const remove=document.createElement('button');remove.type='button';remove.className='secondary';remove.textContent='Remove overlays';
  const refresh=()=>{const state=overlayMenuState(catalog,entered);apply.disabled=!state.canApply;status.textContent=state.problem??'Leave a definition blank to skip it.';return state;};
  for(const definition of catalog.overlays){
    const group=document.createElement('fieldset');
    const name=document.createElement('legend');name.textContent=definition.label;group.append(name);
    for(const parameter of definition.parameters){
      const label=document.createElement('label');label.textContent=`${parameter.label} (${parameter.minimum} to ${parameter.maximum})`;
      const input=document.createElement('input');input.type='text';input.inputMode='numeric';input.autocomplete='off';
      input.addEventListener('input',()=>{(entered[definition.code]??={})[parameter.name]=input.value;refresh();});
      label.append(input);group.append(label);
    }
    form.append(group);
  }
  form.addEventListener('submit',event=>{event.preventDefault();const state=refresh();if(state.canApply)onApply(state.requests);});
  remove.addEventListener('click',()=>onApply([]));
  form.append(apply,remove,status);host.replaceChildren(form);refresh();
}

const tokenNames=['--font','--text','--text-2','--text-3','--border','--surface','--accent'] as const;
export type ChartTokens=Record<typeof tokenNames[number],string>;
/** The chart's colors, fonts and grid, from the page's CSS tokens (so light and dark follow the page). */
export function chartStyles(tokens:Partial<ChartTokens>):DeepPartial<Styles>{
  const token=(name:typeof tokenNames[number])=>tokens[name]||(()=>{throw Error(`The time chart needs the ${name} token.`);})();
  const [font,text,text2,text3,border,surface,accent]=tokenNames.map(token);
  const line={color:border},axis={axisLine:line,tickLine:line,tickText:{color:text3,family:font}};
  const label={color:surface,family:font,backgroundColor:text2,borderColor:text2};
  return {
    grid:{horizontal:line,vertical:line},
    // Prices are drawn by the registered indicators below. The library's own candles are hidden because it cannot break a line at a gap.
    candle:{type:'area',area:{value:'unplotted',lineSize:0,backgroundColor:'transparent',point:{show:false}},priceMark:{show:false},tooltip:{showRule:'none'}},
    indicator:{lines:[{color:accent,size:2,smooth:false}],lastValueMark:{show:false},
      tooltip:{title:{color:text2,family:font,showParams:false},legend:{color:text2,family:font}}},
    xAxis:axis,yAxis:axis,separator:{color:border},
    crosshair:{horizontal:{line:{color:text3},text:label},vertical:{line:{color:text3},text:label}},
    overlay:{line:{color:accent},text:{color:text,family:font},point:{color:accent,borderColor:surface,activeColor:accent,activeBorderColor:surface}},
  };
}
const readTokens=(element:Element)=>{
  const computed=getComputedStyle(element);
  return Object.fromEntries(tokenNames.map(name=>[name,computed.getPropertyValue(name).trim()])) as ChartTokens;
};

type Bar=KLineData&{price:number|null};
type Lookup={lookup:(timestamp:number)=>Record<string,number|undefined>};
const priceIndicator='openetrm_price',overlayIndicator='openetrm_overlay';
let registered=false;
function registerIndicators(){
  if(registered)return;registered=true;
  registerIndicator<{price:number|null},string,undefined>({name:priceIndicator,figures:[{key:'price',title:'Price: ',type:'line'}],
    calc:bars=>bars.map(bar=>({price:(bar as Bar).price}))});
  // The calc only looks values up by instant; calcParams name the series so the figures follow the definition.
  registerIndicator<Record<string,number|undefined>,string,Lookup>({name:overlayIndicator,calcParams:[],
    regenerateFigures:names=>names.map(name=>({key:name,title:`${name}: `,type:'line'})),
    calc:(bars,indicator)=>bars.map(bar=>indicator.extendData.lookup(bar.timestamp))});
}
const decimals=(points:TimePoint[])=>Math.min(6,Math.max(0,...points.map(p=>p.value?.split('.')[1]?.length??0)));

const tools=[{name:'horizontalStraightLine',label:'Horizontal line'},{name:'segment',label:'Trend line'},{name:'rayLine',label:'Ray'}];

/** Draws prices over time, with drawing tools. Drawings belong to the open chart and are not saved. */
export function renderTimeChart(host:HTMLElement,spec:TimeChartSpec):TimeChart{
  registerIndicators();
  const toolbar=document.createElement('div');toolbar.className='time-chart-tools';toolbar.setAttribute('role','toolbar');toolbar.setAttribute('aria-label','Drawing tools');
  const canvas=document.createElement('div');canvas.className='time-chart-canvas';
  const latest=document.createElement('dl');latest.className='time-chart-latest';
  const body=document.createElement('div');body.className='time-chart-body';body.append(canvas,latest);
  const note=document.createElement('p');note.className='fine-print';note.textContent='Drawings are not saved.';
  host.replaceChildren(toolbar,body,note);
  const started=init(canvas,{timezone:spec.zone,styles:chartStyles(readTokens(host))});
  if(!started)throw Error('The time chart could not start.');
  const chart:Chart=started;
  let bars:Bar[]=[];
  const button=(label:string,click:()=>void)=>{const b=document.createElement('button');b.type='button';b.className='secondary';b.textContent=label;b.addEventListener('click',click);toolbar.append(b);return b;};
  for(const tool of tools)button(tool.label,()=>{chart.createOverlay(tool.name);});
  const noteText=document.createElement('input');noteText.type='text';noteText.placeholder='Note text';noteText.setAttribute('aria-label','Note text');
  const addNote=button('Text',()=>{chart.createOverlay({name:'simpleAnnotation',extendData:noteText.value});});
  addNote.disabled=true;noteText.addEventListener('input',()=>{addNote.disabled=noteText.value.trim()==='';});
  toolbar.insertBefore(noteText,addNote);
  button('Clear drawings',()=>{chart.removeOverlay();});
  chart.setDataLoader({getBars:({callback})=>callback(bars,false)});
  const restyle=()=>chart.setStyles(chartStyles(readTokens(host)));
  const scheme=matchMedia('(prefers-color-scheme: dark)');scheme.addEventListener('change',restyle);
  const resize=new ResizeObserver(()=>chart.resize());resize.observe(canvas);
  function update(next:TimeChartSpec){
    const data=timeChartData(next.points);
    const names=timeLabels(next.points.map(p=>p.at),next.zone),labels=new Map(data.map((bar,i)=>[bar.timestamp,names[i]]));
    chart.setFormatter({formatDate:({timestamp})=>labels.get(timestamp)??displayInstant(new Date(timestamp).toISOString(),next.zone)});
    // The library needs numeric open, high, low and close on each bar; they are never drawn. Only `price` (null at a gap) is.
    bars=data.map(bar=>({timestamp:bar.timestamp,open:bar.close??0,high:bar.close??0,low:bar.close??0,close:bar.close??0,price:bar.close}));
    chart.setSymbol({ticker:next.title,pricePrecision:decimals(next.points),volumePrecision:0});
    chart.setPeriod({type:'minute',span:1});
    chart.removeIndicator();
    chart.resetData();
    chart.createIndicator({name:priceIndicator,shortName:`${next.title} (${next.unit})`,precision:decimals(next.points),paneId:'candle_pane'},true);
    let color=0;
    for(const overlay of next.overlays){
      const series=Object.keys(overlay.series),colors=series.map(()=>palette[1+color++%(palette.length-1)]);
      chart.createIndicator({name:overlayIndicator,shortName:overlayLegend(overlay),calcParams:series,precision:decimals(next.points),paneId:'candle_pane',
        extendData:{lookup:overlayLookup(overlay)},
        figures:series.map((name,i)=>({key:name,title:`${name}: `,type:'line',styles:()=>({color:colors[i]})}))},true);
    }
    latest.replaceChildren(...latestValues(next).flatMap(row=>{const term=document.createElement('dt'),value=document.createElement('dd');term.textContent=row.name;value.textContent=row.value;return [term,value];}));
  }
  update(spec);
  return {update,dispose(){scheme.removeEventListener('change',restyle);resize.disconnect();dispose(canvas);host.replaceChildren();}};
}
