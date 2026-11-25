import {chartTokens,init} from './chart-view.js';
import {keyId,type DraftCell,type MarkKind} from './curve-mark-model.js';
import {plotNumber} from './plot-number.js';

export const correlationScale={min:-1,max:1,labels:['−1','0','+1']} as const;
export function correlationInk(value:string){
 const coordinate=plotNumber(value);
 const intensity=Math.max(0,Math.min(1,Math.abs(coordinate)/correlationScale.max))*58;
 return `color-mix(in srgb, var(--${coordinate<0?'vol-cheap':'vol-rich'}) ${intensity}%, var(--surface-3))`;
}
type TermDatum={value:number;exact:string;month:string;revision:string;label:string;convention:string;series:string};
const names=['Published','Validated draft'];
const colors=[chartTokens['accent-text'],chartTokens['gain-ink']];
const axisLabel={color:chartTokens['text-3'],fontSize:10,hideOverlap:true};
export function termOptions(cells:DraftCell[],edits:ReadonlyMap<string,string>,label:string,convention:string,kind:MarkKind){
 const axisData=cells.map(cell=>cell.state.key.month);
 const datum=(cell:DraftCell,exact:string|null,series:number):TermDatum|null=>exact===null?null:{
  value:plotNumber(exact),exact,month:cell.state.key.month,label,convention,series:names[series],
  revision:series===0?`Revision ${cell.state.selected!.revision}`:cell.state.selected?`Based on revision ${cell.state.selected.revision}`:'New mark',
 };
 const published=cells.map(cell=>datum(cell,cell.state.selected?.value??null,0));
 const drafted=cells.map(cell=>{
  const edit=edits.get(keyId(cell.state.key));
  const valid=cell.enteredValue!==null&&(edit===undefined||edit===cell.enteredValue)&&!cell.error;
  return datum(cell,valid?cell.proposedValue:null,1);
 });
 return {
  animation:false, color:colors, grid:{left:44,right:15,bottom:35,top:42},
  legend:{data:names,left:5,top:3,textStyle:{color:chartTokens['text-2'],fontSize:10},itemWidth:18,itemHeight:6,selectedMode:false},
  tooltip:{trigger:'axis' as const,renderMode:'richText' as const,backgroundColor:chartTokens.surface,borderColor:chartTokens['border-strong'],
   textStyle:{color:chartTokens.text,fontSize:11},
   formatter:(params:unknown)=>{
    const lines=(Array.isArray(params)?params:[params]).flatMap((p:{data?:TermDatum|null})=>{
     const point=p.data;
     return point?[`${point.series} · ${point.exact} · ${point.revision}`]:[];
    });
    const point=(Array.isArray(params)?params:[params]).find((p:{data?:TermDatum|null})=>p.data)?.data as TermDatum|undefined;
    return point?[point.label,point.month,...lines,point.convention].join('\n'):'';
   }},
  xAxis:{type:'category' as const,data:axisData,axisLabel,axisTick:{show:false},axisLine:{lineStyle:{color:chartTokens['border-strong']}}},
  yAxis:{type:'value' as const,min:kind==='correlation'?correlationScale.min:0,...kind==='correlation'?{max:correlationScale.max}:{},axisLabel,splitNumber:4,splitLine:{lineStyle:{color:chartTokens.border}}},
  series:[published,drafted].map((data,index)=>({type:'line' as const,name:names[index],data,connectNulls:false,
   symbol:'circle',symbolSize:data.length>24?3:5,showSymbol:true,lineStyle:{type:index===0?'solid' as const:'dashed' as const,width:1.8}})),
 };
}

export function mountMarkTerm(target:HTMLElement,options:ReturnType<typeof termOptions>){
 const chart=init(target);
 chart.setOption(options);
 const observer=new ResizeObserver(()=>{if(target.clientWidth&&target.clientHeight)chart.resize();});
 observer.observe(target);
 return ()=>{observer.disconnect();chart.dispose();};
}
