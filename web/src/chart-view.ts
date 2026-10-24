import {init,type Chart} from './vendor/echarts.js';
import {publicText} from './market-view.js';
import {plotNumber} from './plot-number.js';
/**
 * A chart of exact server values. Optional per-point presentation: `color` fills that bar, `marker` puts a dot of that color
 * before its category label, `text` is the display text a horizontal chart lists beside its bars (and every tooltip adds),
 * and `direction` places an unsigned amount on the side of zero the backend states apart from it (for example a
 * receive-fixed swap's booked quantity).
 */
export type ChartPoint={x:string;y:string|null;label?:string;color?:string;marker?:string;text?:string;direction?:'positive'|'negative'};
export type ChartSpec={title:string;subtitle?:string;axis:'delivery'|'observation'|'maturity'|'category';categoryTitle?:string;axisName?:string;dense?:boolean;unit:string;exportImage?:(download:()=>void)=>void;kind?:'line'|'bar'|'heatmap';
  orientation?:'vertical'|'horizontal';colorBySign?:boolean;series:{id?:string;name:string;points:ChartPoint[]}[]};
// Validated categorical order. The last two sit under 3:1 contrast on white, so a chart that reaches them keeps its table view.
export const palette=['#2f8a66','#2a78d6','#eb6834','#6250d6','#e87ba4','#eda100'];
// Gains, losses and neither, as the CSS tokens --gain, --loss and --text-3 name them, and the --gain-ink and --loss-ink
// text steps. A bar's sign and direction always accompany its color.
export const polarity={gain:'#1f8a5b',loss:'#d6453d',none:'#68776f'};
const ink={gain:'#0f6b3c',loss:'#b3261e'};
// The pixel extent of a diverging axis: the largest magnitude, rounded up to a round step of its power of ten.
function axisEnd(range:{min:number;max:number}){
  const largest=Math.max(Math.abs(range.min),Math.abs(range.max));if(!(largest>0))return 1;
  const power=10**Math.floor(Math.log10(largest));return [1,1.2,1.5,2,2.5,3,4,5,6,8,10].map(step=>step*power).find(end=>end>=largest)!;
}
const escape=(value:string)=>value.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#39;');
export function chartOptions(spec:ChartSpec){
  for(const series of spec.series)if(new Set(series.points.map(p=>p.x)).size!==series.points.length)throw Error('A chart series needs distinct category identities.');
  const categories=[...new Set(spec.series.flatMap(s=>s.points.map(p=>p.x)))];
  const horizontal=spec.orientation==='horizontal';
  const zoom=!horizontal&&categories.length>6;
  // Each series' points and each category's label are found by their category, from Maps built once, not searched for at every draw.
  const points=spec.series.map(s=>new Map(s.points.map(p=>[p.x,p])));
  const labels=new Map<string,string>();for(const series of spec.series)for(const p of series.points)if(!labels.has(p.x))labels.set(p.x,p.label??p.x);
  const categoryLabel=(key:string)=>publicText(labels.get(key)??key);
  // The marker and the listed text a category's points state, for the labels drawn beside the bars.
  const all=spec.series.flatMap(s=>s.points);
  const markerOf=new Map<string,string>(),texted=new Map<string,ChartPoint>();
  for(const p of all){if(p.marker&&!markerOf.has(p.x))markerOf.set(p.x,p.marker);if(p.text!==undefined&&!texted.has(p.x))texted.set(p.x,p);}
  // A dot of each marker color before the labels of the categories that name one.
  const markers=[...new Set(markerOf.values())];
  const markedLabel=(key:string)=>{const marker=markerOf.get(key);return marker?`{m${markers.indexOf(marker)}|●} ${categoryLabel(key)}`:categoryLabel(key);};
  // Approximate coordinates position pixels only. Tooltips retain server strings.
  const coordinate=(value:string|null)=>value===null?null:plotNumber(value);
  const placed=(point:ChartPoint|undefined)=>{
    const value=coordinate(point?.y??null);
    if(value===null||!point?.direction)return value;
    if(point.y!.startsWith('-'))throw Error('A chart point with a stated direction needs an unsigned amount.');
    return point.direction==='negative'?-value:value;
  };
  const tone=(value:number|null)=>value===null||value===0?'none':value>0?'gain':'loss';
  // A point drawn with its own color or direction; other points stay plain values.
  const styled=spec.colorBySign||all.some(p=>p.color||p.direction);
  const datum=(point:ChartPoint|undefined)=>{
    const value=placed(point);if(!styled||value===null||!point)return value;
    const color=spec.colorBySign?polarity[tone(value)]:point.color;
    return {value,...(color?{itemStyle:{color,...(spec.kind==='bar'?{borderRadius:horizontal?(value<0?[3,0,0,3]:[0,3,3,0]):(value<0?[0,0,3,3]:[3,3,0,0])}:{})}}:{})};
  };
  // A horizontal chart lists its points' texts in a column right of the bars, where they never meet a bar or an axis.
  const texts=horizontal&&texted.size>0;
  const textOf=(key:string)=>{
    const point=texted.get(key);if(!point)return '';
    const shown=publicText(point.text!).replace(/[{}|]/g,''),sign=spec.colorBySign?tone(placed(point)):'none';
    return sign==='none'?shown:`{${sign}|${shown}}`;
  };
  const seriesName=(index:number)=>publicText(spec.series[index].name)+(spec.series.filter(s=>s.name===spec.series[index].name).length>1?' · '+(index+1):'');
  const categoryAxis={type:'category' as const,name:spec.categoryTitle??spec.axisName??(spec.axis==='delivery'?'Delivery period':spec.axis==='maturity'?'Maturity date':spec.axis==='observation'?'Observation date':'Book'),nameLocation:'middle' as const,nameGap:32,nameTextStyle:{color:'#64776d',fontSize:11},data:categories,axisLine:{lineStyle:{color:'#cbd9d1'}},axisTick:{show:false},axisPointer:{label:{formatter:(p:{value:string})=>categoryLabel(String(p.value))}},
    axisLabel:{color:'#64776d',fontSize:11,hideOverlap:true,formatter:markers.length?markedLabel:categoryLabel,...(markers.length?{rich:Object.fromEntries(markers.map((color,i)=>[`m${i}`,{color,fontSize:11}]))}:{})}};
  const valueAxis={type:'value' as const,name:spec.unit,nameLocation:'end' as const,nameGap:18,scale:spec.kind!=='bar',nameTextStyle:{color:'#607457',align:'left' as const,fontSize:11},axisLabel:{color:'#6c7e63',fontSize:10},splitLine:{lineStyle:{color:'#e8eee3'}},
    // Diverging bars keep zero in the middle, so a gain and a loss of one size are drawn alike, between round axis ends.
    ...(horizontal&&spec.kind==='bar'?{min:(v:{min:number;max:number})=>-axisEnd(v),max:axisEnd}:{}),
    // A horizontal chart's caption states its unit, so the axis has no name. Bars with their values listed beside them
    // need no tick labels; others keep a few, apart from each other.
    ...(horizontal?{name:'',splitNumber:4,axisLabel:texts?{show:false}:{color:'#6c7e63',fontSize:10,hideOverlap:true}}:{})};
  const textAxis={type:'category' as const,data:categories,inverse:true,position:'right' as const,axisLine:{show:false},axisTick:{show:false},axisPointer:{show:false},
    axisLabel:{color:'#2c3a32',fontSize:11,fontWeight:600,formatter:textOf,rich:{gain:{color:ink.gain,fontSize:11,fontWeight:600},loss:{color:ink.loss,fontSize:11,fontWeight:600}}}};
  const option={
    // The HTML caption names the chart on screen; the canvas title is shown only in saved images.
    title:{show:false,text:publicText(spec.title),subtext:publicText(spec.subtitle??''),left:8,top:0,textStyle:{fontSize:12,fontWeight:500,color:'#3b5841'},subtextStyle:{fontSize:10,color:'#738469'}},
    animation:false,color:palette,backgroundColor:'transparent',textStyle:{fontFamily:'Inter, -apple-system, BlinkMacSystemFont, sans-serif'},aria:{enabled:true,description:spec.title},
    grid:horizontal?{top:8,right:12,bottom:texts?8:24,left:16,containLabel:true}:{top:spec.series.length>1?56:36,right:28,bottom:zoom?80:48,left:36,containLabel:true},
    legend:{show:spec.series.length>1,top:4,type:'scroll',textStyle:{color:'#526650',fontSize:11}},
    tooltip:{trigger:'axis',confine:true,renderMode:'html',backgroundColor:'#fff',borderColor:'#dbe5df',padding:12,textStyle:{color:'#263e34',fontSize:12},axisPointer:{type:spec.kind==='bar'?'shadow':'line'},formatter:(items:{seriesIndex:number;dataIndex:number}[])=>items.map(item=>{
      const series=spec.series[item.seriesIndex],category=categories[item.dataIndex],point=points[item.seriesIndex].get(category);
      return escape(publicText(`${point?.label??category} · ${seriesName(item.seriesIndex)}: ${point?.y??'Unavailable'} ${spec.unit}${point?.text!==undefined?' · '+point.text:''}`));
    }).join('<br>')},
    xAxis:horizontal?valueAxis:categoryAxis,
    yAxis:horizontal?(texts?[{...categoryAxis,inverse:true,nameGap:0,name:''},textAxis]:{...categoryAxis,inverse:true,nameGap:0,name:''}):valueAxis,
    dataZoom:[{type:'inside',filterMode:'none',disabled:!zoom},{type:'slider',show:zoom,labelFormatter:(_value:number,label:string)=>categoryLabel(label),height:16,bottom:10,borderColor:'#e1e8da',backgroundColor:'#f6f8f4',fillerColor:'#256b521a',handleStyle:{color:'#256b52'},moveHandleStyle:{color:'#256b52',opacity:.35},
      dataBackground:{lineStyle:{color:'#a9bea4'},areaStyle:{color:'#e3ece0'}},selectedDataBackground:{lineStyle:{color:'#256b52'},areaStyle:{color:'#256b5226'}},emphasis:{handleStyle:{color:'#1d5641'},moveHandleStyle:{color:'#256b52',opacity:.6}},textStyle:{color:'#6c7e63',fontSize:10}}],
    series:spec.series.map((s,index)=>({id:s.id??String(index),name:seriesName(index),type:spec.kind??'line',connectNulls:false,showSymbol:!spec.dense,...(spec.dense?{sampling:'lttb'}:{}),symbolSize:7,lineStyle:{width:2.5},barMaxWidth:horizontal?18:44,itemStyle:{borderRadius:spec.kind==='bar'&&!horizontal?[3,3,0,0]:0},emphasis:{focus:'series'},data:categories.map(x=>datum(points[index].get(x)))})),
  };
  if(spec.kind!=='heatmap')return option;
  const cells:number[][]=[];let magnitude=0,negative=false;
  spec.series.forEach((s,y)=>categories.forEach((x,i)=>{const value=coordinate(points[y].get(x)?.y??null);if(value!==null){cells.push([i,y,value]);magnitude=Math.max(magnitude,Math.abs(value));negative||=value<0;}}));
  return {...option,grid:{...option.grid,top:24,bottom:zoom?114:94,left:16},legend:{show:false},
    xAxis:{...categoryAxis,splitArea:{show:true,areaStyle:{color:['#f4f7f5','#f4f7f5']}}},
    yAxis:{type:'category',data:spec.series.map((_,i)=>seriesName(i)),axisLabel:{color:'#52665b',fontSize:11,width:180,overflow:'truncate'},axisLine:{show:false},axisTick:{show:false},splitArea:{show:true,areaStyle:{color:['#f4f7f5','#f4f7f5']}}},
    // Signed cells shade from loss through neutral to gain; others in one hue of the palette's first color.
    visualMap:{min:negative?-(magnitude||1):0,max:magnitude||1,calculable:true,orient:'horizontal',left:'center',bottom:zoom?32:10,itemHeight:150,itemWidth:10,text:['Higher','Lower'],textStyle:{color:'#64776d',fontSize:10},inRange:{color:negative?[polarity.loss,'#f7f8f7',polarity.gain]:['#eaf3ee','#8cc0a8',palette[0]]}},
    tooltip:{...option.tooltip,trigger:'item',formatter:(item:{value:number[]})=>{const [x,y]=item.value,series=spec.series[y],point=points[y].get(categories[x]);return escape(publicText(`${point?.label??categories[x]} · ${seriesName(y)}: ${point?.y??'Unavailable'} ${spec.unit}`));}},
    series:[{type:'heatmap',data:cells,itemStyle:{borderWidth:3,borderColor:'#fff',borderRadius:4},emphasis:{itemStyle:{borderColor:'#203d30',borderWidth:2}},label:{show:cells.length<=48,fontSize:11,rich:{light:{color:'#fff'},dark:{color:'#183c2d'}},formatter:(item:{value:number[]})=>{const [x,y,coordinate]=item.value,value=points[y].get(categories[x])?.y??'';return value.length<=12?`{${Math.abs(coordinate)>magnitude*.55?'light':'dark'}|${value}}`:'';}}}],
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
  // Horizontal bars show every category and have no zoom to reset.
  if(spec.orientation!=='horizontal')action('Reset zoom',()=>chart.dispatchAction({type:'dataZoom',start:0,end:100}));
  if(spec.exportImage)action('Save image',()=>spec.exportImage!(()=>{if(active.get(host)?.chart!==chart||!host.isConnected)throw Error('Refresh this chart before exporting.');const a=document.createElement('a');a.download='leanetrm-chart.png';chart.setOption({title:{show:true},legend:{top:48},grid:{top:82}});try{a.href=chart.getDataURL({type:'png',pixelRatio:2,backgroundColor:'#fff'});}finally{chart.setOption({title:{show:false},legend:{top:4},grid:{top:option.grid.top}});}a.click();}));
}

// Widget updates keep the canvas and its zoom for the same data scope. Export-enabled
// charts retain the full renderer because their callbacks own the original specification.
export function updateChart(host:HTMLElement,spec:ChartSpec){
 const existing=active.get(host);
 if(!existing||spec.exportImage){renderChart(host,spec);return;}
 const option=chartOptions(spec);
 // New zoom options omit start/end, so ECharts retains the current window while updating availability.
 existing.chart.setOption(option,{replaceMerge:['series']});
 host.setAttribute('aria-label',publicText(spec.title));host.dataset.chartUnit=spec.unit;host.dataset.chartAxis=spec.axis;host.dataset.chartKind=spec.kind??'line';
 const title=host.querySelector('.chart-toolbar strong');if(title)title.textContent=publicText(spec.title);
 const caption=title?.parentElement;caption?.querySelector('small')?.remove();
 if(caption&&spec.subtitle){const subtitle=document.createElement('small');subtitle.textContent=publicText(spec.subtitle);caption.append(subtitle);}
 host.querySelector('[role="img"]')?.setAttribute('aria-label',publicText(spec.title));
}
