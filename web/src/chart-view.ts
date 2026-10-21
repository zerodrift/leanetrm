import {init,type Chart} from './vendor/echarts.js';
import {publicText} from './market-view.js';
// A dense series (thousands of native intervals) draws no point symbols and samples for display only.
export type ChartSpec={title:string;subtitle?:string;axis:'delivery'|'observation'|'maturity'|'category';axisName?:string;unit:string;exportImage?:(download:()=>void)=>void;kind?:'line'|'bar';dense?:boolean;series:{name:string;points:{x:string;y:string|null;label?:string}[]}[]};
const palette=['#256b52','#bf7142','#416da5','#9671ad','#9a8530','#4b8996'];
const escape=(value:string)=>value.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#39;');
export function chartOptions(spec:ChartSpec){
  for(const series of spec.series)if(new Set(series.points.map(p=>p.x)).size!==series.points.length)throw Error('A chart series needs distinct category identities.');
  const categories=[...new Set(spec.series.flatMap(s=>s.points.map(p=>p.x)))];
  const categoryLabel=(key:string)=>publicText(spec.series.flatMap(s=>s.points).find(p=>p.x===key)?.label??key);
  return {
    // The HTML caption names the chart on screen; the canvas title is shown only in saved images.
    title:{show:false,text:publicText(spec.title),subtext:publicText(spec.subtitle??''),left:8,top:0,textStyle:{fontSize:12,fontWeight:500,color:'#3b5841'},subtextStyle:{fontSize:10,color:'#738469'}},
    animation:false,color:palette,aria:{enabled:true,description:spec.title},
    grid:{top:spec.series.length>1?56:32,right:30,bottom:78,left:76,containLabel:true},
    legend:{show:spec.series.length>1,top:4,type:'scroll',textStyle:{color:'#526650',fontSize:11}},
    tooltip:{trigger:'axis',confine:true,renderMode:'html',formatter:(items:{seriesIndex:number;dataIndex:number}[])=>items.map(item=>{
      const series=spec.series[item.seriesIndex],category=categories[item.dataIndex],point=series.points.find(p=>p.x===category);
      return escape(publicText(`${point?.label??category} · ${series.name}: ${point?.y??'Unavailable'} ${spec.unit}`));
    }).join('<br>')},
    xAxis:{type:'category',name:spec.axisName??(spec.axis==='delivery'?'Delivery period':spec.axis==='maturity'?'Maturity date':spec.axis==='observation'?'Observation date':'Book'),nameLocation:'middle',nameGap:30,data:categories,axisLine:{lineStyle:{color:'#b7c8b0'}},axisPointer:{label:{formatter:(p:{value:string})=>categoryLabel(String(p.value))}},axisLabel:{color:'#6c7e63',fontSize:10,hideOverlap:true,formatter:categoryLabel}},
    yAxis:{type:'value',name:spec.unit,...(spec.axis==='category'?{nameLocation:'middle',nameRotate:90,nameGap:50}:{}),scale:spec.kind!=='bar',nameTextStyle:{color:'#607457'},axisLabel:{color:'#6c7e63',fontSize:10},splitLine:{lineStyle:{color:'#e8eee3'}}},
    dataZoom:[{type:'inside',filterMode:'none'},{type:'slider',labelFormatter:(_value:number,label:string)=>categoryLabel(label),height:16,bottom:10,borderColor:'#e1e8da',backgroundColor:'#f6f8f4',fillerColor:'#256b521a',handleStyle:{color:'#256b52'},moveHandleStyle:{color:'#256b52',opacity:.35},
      dataBackground:{lineStyle:{color:'#a9bea4'},areaStyle:{color:'#e3ece0'}},selectedDataBackground:{lineStyle:{color:'#256b52'},areaStyle:{color:'#256b5226'}},emphasis:{handleStyle:{color:'#1d5641'},moveHandleStyle:{color:'#256b52',opacity:.6}},textStyle:{color:'#6c7e63',fontSize:10}}],
    series:spec.series.map(s=>({name:publicText(s.name),type:spec.kind??'line',connectNulls:false,showSymbol:!spec.dense,...(spec.dense?{sampling:'lttb'}:{}),symbolSize:6,lineStyle:{width:2},barMaxWidth:40,data:categories.map(x=>{
      const value=s.points.find(p=>p.x===x)?.y??null;
      if(value===null)return null;
      if(!/^-?\d+(\.\d+)?$/.test(value)||!Number.isFinite(Number(value)))throw Error('A chart needs exact finite server values.');
      // Approximate coordinates position pixels only. Tooltips retain server strings.
      return Number(value);
    })})),
  };
}
const active=new Map<HTMLElement,{chart:Chart;resize:ResizeObserver}>();
export function clearChart(host:HTMLElement,message=''){
  const existing=active.get(host);if(existing){existing.resize.disconnect();existing.chart.dispose();active.delete(host);}host.replaceChildren();if(message)host.textContent=message;
}
export function renderChart(host:HTMLElement,spec:ChartSpec){
  clearChart(host);const option=chartOptions(spec);host.classList.remove('empty');host.setAttribute('aria-label',publicText(spec.title));host.dataset.chartAxis=spec.axis;host.dataset.chartUnit=spec.unit;
  const toolbar=document.createElement('div');toolbar.className='chart-toolbar';
  const caption=document.createElement('span'),name=document.createElement('strong');name.textContent=publicText(spec.title);caption.append(name);
  if(spec.subtitle)caption.append(' · '+publicText(spec.subtitle));toolbar.append(caption);
  const plot=document.createElement('div');plot.className='standard-chart-plot';plot.setAttribute('role','img');plot.setAttribute('aria-label',publicText(spec.title));
  host.append(toolbar,plot);
  if(!spec.series.some(s=>s.points.some(p=>p.y!==null))){plot.textContent='No values available for this selection.';return;}
  const chart=init(plot,undefined,{renderer:'canvas'});try{chart.setOption(option);}catch(error){chart.dispose();host.replaceChildren();throw error;}
  const resize=new ResizeObserver(()=>chart.resize());resize.observe(plot);active.set(host,{chart,resize});
  function action(label:string,callback:()=>void){const button=document.createElement('button');button.type='button';button.className='text-button';button.textContent=label;button.onclick=callback;toolbar.append(button);}
  action('Reset zoom',()=>chart.dispatchAction({type:'dataZoom',start:0,end:100}));
  if(spec.exportImage)action('Save image',()=>spec.exportImage!(()=>{if(active.get(host)?.chart!==chart||!host.isConnected)throw Error('Refresh this chart before exporting.');const a=document.createElement('a');a.download='leanetrm-chart.png';chart.setOption({title:{show:true},legend:{top:48},grid:{top:82}});try{a.href=chart.getDataURL({type:'png',pixelRatio:2,backgroundColor:'#fff'});}finally{chart.setOption({title:{show:false},legend:{top:4},grid:{top:option.grid.top}});}a.click();}));
}
