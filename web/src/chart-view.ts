import {init,type Chart} from './vendor/echarts.js';
import {publicText} from './market-view.js';
export type ChartSpec={title:string;subtitle?:string;axis:'delivery'|'observation'|'maturity'|'category';categoryTitle?:string;axisName?:string;dense?:boolean;unit:string;exportImage?:(download:()=>void)=>void;kind?:'line'|'bar'|'heatmap';series:{id?:string;name:string;points:{x:string;y:string|null;label?:string}[]}[]};
// Validated categorical order. The last two sit under 3:1 contrast on white, so a chart that reaches them keeps its table view.
export const palette=['#2f8a66','#2a78d6','#eb6834','#6250d6','#e87ba4','#eda100'];
const escape=(value:string)=>value.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#39;');
export function chartOptions(spec:ChartSpec){
  for(const series of spec.series)if(new Set(series.points.map(p=>p.x)).size!==series.points.length)throw Error('A chart series needs distinct category identities.');
  const categories=[...new Set(spec.series.flatMap(s=>s.points.map(p=>p.x)))];
  const zoom=categories.length>6;
  const categoryLabel=(key:string)=>publicText(spec.series.flatMap(s=>s.points).find(p=>p.x===key)?.label??key);
  const coordinate=(value:string|null)=>{
    if(value===null)return null;
    const [n,d]=value.split('/');
    // Scale significant digits together: either retained integer may exceed
    // Number's range even when their ratio fits ordinary plot coordinates.
    const absolute=n.replace(/^-/,'').replace(/^0+/, '')||'0',head=absolute.slice(0,16),divisor=d?.slice(0,16);
    const result=d?Number(`${(n.startsWith('-')?-1:1)*(Number('0.'+head)/Number('0.'+divisor))}e${absolute.length-d.length}`):Number(n);
    if(!/^-?\d+(?:\.\d+|\/[1-9]\d*)?$/.test(value)||!Number.isFinite(result))throw Error('A chart needs exact finite server values.');
    // Approximate coordinates position pixels only. Tooltips retain server strings.
    return result;
  };
  const seriesName=(index:number)=>publicText(spec.series[index].name)+(spec.series.filter(s=>s.name===spec.series[index].name).length>1?' · '+(index+1):'');
  const option={
    // The HTML caption names the chart on screen; the canvas title is shown only in saved images.
    title:{show:false,text:publicText(spec.title),subtext:publicText(spec.subtitle??''),left:8,top:0,textStyle:{fontSize:12,fontWeight:500,color:'#3b5841'},subtextStyle:{fontSize:10,color:'#738469'}},
    animation:false,color:palette,backgroundColor:'transparent',textStyle:{fontFamily:'Inter, -apple-system, BlinkMacSystemFont, sans-serif'},aria:{enabled:true,description:spec.title},
    grid:{top:spec.series.length>1?56:36,right:28,bottom:zoom?80:48,left:36,containLabel:true},
    legend:{show:spec.series.length>1,top:4,type:'scroll',textStyle:{color:'#526650',fontSize:11}},
    tooltip:{trigger:'axis',confine:true,renderMode:'html',backgroundColor:'#fff',borderColor:'#dbe5df',padding:12,textStyle:{color:'#263e34',fontSize:12},axisPointer:{type:spec.kind==='bar'?'shadow':'line'},formatter:(items:{seriesIndex:number;dataIndex:number}[])=>items.map(item=>{
      const series=spec.series[item.seriesIndex],category=categories[item.dataIndex],point=series.points.find(p=>p.x===category);
      return escape(publicText(`${point?.label??category} · ${seriesName(item.seriesIndex)}: ${point?.y??'Unavailable'} ${spec.unit}`));
    }).join('<br>')},
    xAxis:{type:'category',name:spec.categoryTitle??spec.axisName??(spec.axis==='delivery'?'Delivery period':spec.axis==='maturity'?'Maturity date':spec.axis==='observation'?'Observation date':'Book'),nameLocation:'middle',nameGap:32,nameTextStyle:{color:'#64776d',fontSize:11},data:categories,axisLine:{lineStyle:{color:'#cbd9d1'}},axisTick:{show:false},axisPointer:{label:{formatter:(p:{value:string})=>categoryLabel(String(p.value))}},axisLabel:{color:'#64776d',fontSize:11,hideOverlap:true,formatter:categoryLabel}},
    yAxis:{type:'value',name:spec.unit,nameLocation:'end',nameGap:18,scale:spec.kind!=='bar',nameTextStyle:{color:'#607457',align:'left',fontSize:11},axisLabel:{color:'#6c7e63',fontSize:10},splitLine:{lineStyle:{color:'#e8eee3'}}},
    dataZoom:[{type:'inside',filterMode:'none',disabled:!zoom},{type:'slider',show:zoom,labelFormatter:(_value:number,label:string)=>categoryLabel(label),height:16,bottom:10,borderColor:'#e1e8da',backgroundColor:'#f6f8f4',fillerColor:'#256b521a',handleStyle:{color:'#256b52'},moveHandleStyle:{color:'#256b52',opacity:.35},
      dataBackground:{lineStyle:{color:'#a9bea4'},areaStyle:{color:'#e3ece0'}},selectedDataBackground:{lineStyle:{color:'#256b52'},areaStyle:{color:'#256b5226'}},emphasis:{handleStyle:{color:'#1d5641'},moveHandleStyle:{color:'#256b52',opacity:.6}},textStyle:{color:'#6c7e63',fontSize:10}}],
    series:spec.series.map((s,index)=>({id:s.id??String(index),name:seriesName(index),type:spec.kind??'line',connectNulls:false,showSymbol:!spec.dense,...(spec.dense?{sampling:'lttb'}:{}),symbolSize:7,lineStyle:{width:2.5},barMaxWidth:44,itemStyle:{borderRadius:spec.kind==='bar'?[3,3,0,0]:0},emphasis:{focus:'series'},data:categories.map(x=>coordinate(s.points.find(p=>p.x===x)?.y??null))})),
  };
  if(spec.kind!=='heatmap')return option;
  const cells:number[][]=[];let magnitude=0,negative=false;
  spec.series.forEach((s,y)=>categories.forEach((x,i)=>{const value=coordinate(s.points.find(p=>p.x===x)?.y??null);if(value!==null){cells.push([i,y,value]);magnitude=Math.max(magnitude,Math.abs(value));negative||=value<0;}}));
  return {...option,grid:{...option.grid,top:24,bottom:zoom?114:94,left:16},legend:{show:false},
    xAxis:{...option.xAxis,splitArea:{show:true,areaStyle:{color:['#f4f7f5','#f4f7f5']}}},
    yAxis:{type:'category',data:spec.series.map((_,i)=>seriesName(i)),axisLabel:{color:'#52665b',fontSize:11,width:180,overflow:'truncate'},axisLine:{show:false},axisTick:{show:false},splitArea:{show:true,areaStyle:{color:['#f4f7f5','#f4f7f5']}}},
    visualMap:{min:negative?-(magnitude||1):0,max:magnitude||1,calculable:true,orient:'horizontal',left:'center',bottom:zoom?32:10,itemHeight:150,itemWidth:10,text:['Higher','Lower'],textStyle:{color:'#64776d',fontSize:10},inRange:{color:negative?['#b95b68','#f8f9f7','#287a65']:['#edf4ef','#8abaaa','#287a65']}},
    tooltip:{...option.tooltip,trigger:'item',formatter:(item:{value:number[]})=>{const [x,y]=item.value,series=spec.series[y],point=series.points.find(p=>p.x===categories[x]);return escape(publicText(`${point?.label??categories[x]} · ${seriesName(y)}: ${point?.y??'Unavailable'} ${spec.unit}`));}},
    series:[{type:'heatmap',data:cells,itemStyle:{borderWidth:3,borderColor:'#fff',borderRadius:4},emphasis:{itemStyle:{borderColor:'#203d30',borderWidth:2}},label:{show:cells.length<=48,fontSize:11,rich:{light:{color:'#fff'},dark:{color:'#183c2d'}},formatter:(item:{value:number[]})=>{const [x,y,coordinate]=item.value,value=spec.series[y].points.find(p=>p.x===categories[x])?.y??'';return value.length<=12?`{${Math.abs(coordinate)>magnitude*.55?'light':'dark'}|${value}}`:'';}}}],
  };
}
const active=new Map<HTMLElement,{chart:Chart;resize:ResizeObserver}>();
export function clearChart(host:HTMLElement,message=''){
  const existing=active.get(host);if(existing){existing.resize.disconnect();existing.chart.dispose();active.delete(host);}host.replaceChildren();if(message)host.textContent=message;
}
export function renderChart(host:HTMLElement,spec:ChartSpec){
  clearChart(host);const option=chartOptions(spec);host.classList.remove('empty');host.setAttribute('aria-label',publicText(spec.title));host.dataset.chartAxis=spec.axis;host.dataset.chartUnit=spec.unit;host.dataset.chartKind=spec.kind??'line';
  const toolbar=document.createElement('div');toolbar.className='chart-toolbar';
  const caption=document.createElement('span'),name=document.createElement('strong');name.textContent=publicText(spec.title);caption.append(name);
  if(spec.subtitle){const subtitle=document.createElement('small');subtitle.textContent=publicText(spec.subtitle);caption.append(subtitle);}toolbar.append(caption);
  const plot=document.createElement('div');plot.className='standard-chart-plot';plot.setAttribute('role','img');plot.setAttribute('aria-label',publicText(spec.title));
  host.append(toolbar,plot);
  if(!spec.series.some(s=>s.points.some(p=>p.y!==null))){plot.textContent='No values available for this selection.';return;}
  const chart=init(plot,undefined,{renderer:'canvas'});try{chart.setOption(option);}catch(error){chart.dispose();host.replaceChildren();throw error;}
  const resize=new ResizeObserver(()=>chart.resize());resize.observe(plot);active.set(host,{chart,resize});
  function action(label:string,callback:()=>void){const button=document.createElement('button');button.type='button';button.className='text-button';button.textContent=label;button.onclick=callback;toolbar.append(button);}
  action('Reset zoom',()=>chart.dispatchAction({type:'dataZoom',start:0,end:100}));
  if(spec.exportImage)action('Save image',()=>spec.exportImage!(()=>{if(active.get(host)?.chart!==chart||!host.isConnected)throw Error('Refresh this chart before exporting.');const a=document.createElement('a');a.download='leanetrm-chart.png';chart.setOption({title:{show:true},legend:{top:48},grid:{top:82}});try{a.href=chart.getDataURL({type:'png',pixelRatio:2,backgroundColor:'#fff'});}finally{chart.setOption({title:{show:false},legend:{top:4},grid:{top:option.grid.top}});}a.click();}));
}
