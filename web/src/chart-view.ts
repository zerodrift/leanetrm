import {init,type Chart} from './vendor/echarts.js';
// The application bundle imports the chart library once, here; views that compose their own chart options draw with it from here.
export {init,type Chart};
import {publicText} from './market-view.js';
import {plotNumber} from './plot-number.js';
/**
 * A chart of exact server values. Optional per-point presentation: `color` fills that bar, `marker` puts a dot of that color
 * before its category label, `text` is the display text a horizontal chart lists beside its bars (and every tooltip adds),
 * and `direction` places an unsigned amount on the side of zero the backend states apart from it (for example a
 * receive-fixed swap's booked quantity).
 */
export type ChartPoint={x:string;y:string|null;label?:string;color?:string;marker?:string;text?:string;direction?:'positive'|'negative'};
/**
 * `marked` names the points (by `x`) a chart emphasizes, such as the contracts of a position's delivery period. `reference` is a
 * dashed line across a vertical chart at an exact value, labeled with its own text, and the value axis always includes it.
 */
export type ChartSpec={title:string;subtitle?:string;axis:'delivery'|'observation'|'maturity'|'category';categoryTitle?:string;axisName?:string;dense?:boolean;unit:string;exportImage?:(download:()=>void)=>void;kind?:'line'|'bar'|'heatmap';
  onSelect?:(category:string)=>void;formatValueAxis?:(coordinate:number)=>string;
  orientation?:'vertical'|'horizontal';colorBySign?:boolean;positiveTone?:'gain'|'loss';marked?:string[];reference?:{value:string;label:string};series:{id?:string;name:string;points:ChartPoint[]}[]};
// The theme's tokens a chart is drawn in, as app.css states them (a test holds the two together): a chart's options are composed
// apart from the page, where the stylesheet cannot be read.
export const chartTokens={bg:'#070a13',surface:'#0d1220','surface-2':'#111728','surface-3':'#182035',border:'#1e2740','border-strong':'#2f3b5c',
  text:'#f3f5fa','text-2':'#b9c1d6','text-3':'#8d97b0','text-disabled':'#626d8a',accent:'#3462ff','accent-text':'#a9c8ff',
  gain:'#00a17d',loss:'#f45a51','gain-ink':'#8fe0b4','loss-ink':'#ff9d8a'} as const;
const theme=chartTokens;
// Validated categorical order on the theme's surfaces: blue, orange, green, violet, magenta and yellow, each 3:1 or better on them.
export const palette=['#5893ee','#e26d28','#2dac7c','#9b7ee5','#db6190','#c08a11'];
// Gains, losses and neither, and the text steps of a gain and a loss. A bar's sign and direction always accompany its color.
export const polarity={gain:theme.gain,loss:theme.loss,none:theme['text-3']};
const ink={gain:theme['gain-ink'],loss:theme['loss-ink']};
// The dot of a marked point and the line of a reference: the theme's brightest ink, which no series is drawn in.
const markColor=theme.text;
// A heatmap of amounts on one side of zero rises from the surface through one hue: the palette's first, or the loss tone where a
// positive amount is a loss. Its cell labels are written in the surface's ink up to this share of the largest amount and in the
// page's dark ground beyond it, where the cell is the lighter of the two.
const ramps={magnitude:['#132643','#305a9a',palette[0]],loss:['#3f1916','#93332e',theme.loss]},darkLabelFrom=.76;
// The extent of an axis that reaches a reference line: the data and the line, with a little room beyond them.
function reach(range:{min:number;max:number},reference:number){
  const low=Math.min(range.min,reference),high=Math.max(range.max,reference),room=((high-low)||Math.abs(high)||1)*.08;
  return {low:low-room,high:high+room};
}
// The pixel extent of a diverging axis: the largest magnitude, rounded up to a round step of its power of ten.
function axisEnd(range:{min:number;max:number}){
  const largest=Math.max(Math.abs(range.min),Math.abs(range.max));if(!(largest>0))return 1;
  const power=10**Math.floor(Math.log10(largest));return [1,1.2,1.5,2,2.5,3,4,5,6,8,10].map(step=>step*power).find(end=>end>=largest)!;
}
const escape=(value:string)=>value.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#39;');
// The shares of a horizontal chart's width its category labels and its listed texts may take, beyond which they are cut short
// (their tooltips and text alternatives keep them whole), so the bars always keep the rest.
const labelShares=[.4,.22],labelWidth=(width:number,share:number)=>Math.floor(width*share);
export function chartOptions(spec:ChartSpec,size?:{width:number}){
  for(const series of spec.series)if(new Set(series.points.map(p=>p.x)).size!==series.points.length)throw Error('A chart series needs distinct category identities.');
  const categories=[...new Set(spec.series.flatMap(s=>s.points.map(p=>p.x)))];
  const marked=new Set(spec.marked);
  if([...marked].some(x=>!categories.includes(x)))throw Error('A chart can only mark points it draws.');
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
  const bounded=(share:number)=>horizontal&&size?{width:labelWidth(size.width,share),overflow:'truncate' as const}:{};
  const markedLabel=(key:string)=>{const marker=markerOf.get(key);return marker?`{m${markers.indexOf(marker)}|●} ${categoryLabel(key)}`:categoryLabel(key);};
  // Approximate coordinates position pixels only. Tooltips retain server strings.
  const coordinate=(value:string|null)=>value===null?null:plotNumber(value);
  const placed=(point:ChartPoint|undefined)=>{
    const value=coordinate(point?.y??null);
    if(value===null||!point?.direction)return value;
    if(point.y!.startsWith('-'))throw Error('A chart point with a stated direction needs an unsigned amount.');
    return point.direction==='negative'?-value:value;
  };
  const tone=(value:number|null)=>value===null||value===0?'none':value>0?(spec.positiveTone??'gain'):(spec.positiveTone==='loss'?'gain':'loss');
  const reference=spec.reference&&plotNumber(spec.reference.value);
  // A point drawn with its own color or direction; other points stay plain values.
  const styled=spec.colorBySign||all.some(p=>p.color||p.direction);
  const datum=(point:ChartPoint|undefined)=>{
    const value=placed(point);if(value===null||!point||!styled&&!marked.has(point.x))return value;
    const color=spec.colorBySign?polarity[tone(value)]:point.color;
    // A marked point is a larger dot with a ring, drawn over its neighbors.
    const emphasis=marked.has(point.x)?{symbol:'circle',symbolSize:12,z:3,itemStyle:{color:markColor,borderColor:theme.surface,borderWidth:2}}:undefined;
    return {value,...(color?{itemStyle:{color,...(spec.kind==='bar'?{borderRadius:horizontal?(value<0?[3,0,0,3]:[0,3,3,0]):(value<0?[0,0,3,3]:[3,3,0,0])}:{})}}:{}),...emphasis};
  };
  // A horizontal chart lists its points' texts in a column right of the bars, where they never meet a bar or an axis.
  const texts=horizontal&&texted.size>0;
  const textOf=(key:string)=>{
    const point=texted.get(key);if(!point)return '';
    const shown=publicText(point.text!).replace(/[{}|]/g,''),sign=spec.colorBySign?tone(placed(point)):'none';
    return sign==='none'?shown:`{${sign}|${shown}}`;
  };
  const seriesName=(index:number)=>publicText(spec.series[index].name)+(spec.series.filter(s=>s.name===spec.series[index].name).length>1?' · '+(index+1):'');
  const categoryAxis={type:'category' as const,name:spec.categoryTitle??spec.axisName??(spec.axis==='delivery'?'Delivery period':spec.axis==='maturity'?'Maturity date':spec.axis==='observation'?'Observation date':'Book'),nameLocation:'middle' as const,nameGap:32,nameTextStyle:{color:theme['text-3'],fontSize:11},data:categories,axisLine:{lineStyle:{color:theme['border-strong']}},axisTick:{show:false},axisPointer:{label:{formatter:(p:{value:string})=>categoryLabel(String(p.value))}},
    axisLabel:{color:theme['text-3'],fontSize:11,hideOverlap:true,...bounded(labelShares[0]),formatter:markers.length?markedLabel:categoryLabel,...(markers.length?{rich:Object.fromEntries(markers.map((color,i)=>[`m${i}`,{color,fontSize:11}]))}:{})}};
  const valueAxis={type:'value' as const,name:spec.unit,nameLocation:'end' as const,nameGap:18,scale:spec.kind!=='bar',nameTextStyle:{color:theme['text-3'],align:'left' as const,fontSize:11},axisLabel:{color:theme['text-3'],fontSize:10,...(spec.formatValueAxis?{formatter:spec.formatValueAxis}:{})},splitLine:{lineStyle:{color:theme.border}},
    // Diverging bars keep zero in the middle, so a gain and a loss of one size are drawn alike, between round axis ends.
    ...(horizontal&&spec.kind==='bar'?{min:(v:{min:number;max:number})=>-axisEnd(v),max:axisEnd}:{}),
    // A reference line is part of the picture: the axis reaches it however far the data lies from it.
    ...(reference!==undefined?{min:(v:{min:number;max:number})=>reach(v,reference).low,max:(v:{min:number;max:number})=>reach(v,reference).high}:{}),
    // A horizontal chart's caption states its unit, so the axis has no name. Bars with their values listed beside them
    // need no tick labels; others keep a few, apart from each other.
    ...(horizontal?{name:'',splitNumber:4,axisLabel:texts?{show:false}:{color:theme['text-3'],fontSize:10,hideOverlap:true}}:{})};
  const textAxis={type:'category' as const,data:categories,inverse:true,position:'right' as const,axisLine:{show:false},axisTick:{show:false},axisPointer:{show:false},
    axisLabel:{color:theme.text,fontSize:11,fontWeight:600,...bounded(labelShares[1]),formatter:textOf,rich:{gain:{color:ink.gain,fontSize:11,fontWeight:600},loss:{color:ink.loss,fontSize:11,fontWeight:600}}}};
  const option={
    // The HTML caption names the chart on screen; the canvas title is shown only in saved images.
    title:{show:false,text:publicText(spec.title),subtext:publicText(spec.subtitle??''),left:8,top:0,textStyle:{fontSize:12,fontWeight:500,color:theme.text},subtextStyle:{fontSize:10,color:theme['text-3']}},
    animation:false,color:palette,backgroundColor:'transparent',textStyle:{fontFamily:'Inter, -apple-system, BlinkMacSystemFont, sans-serif'},aria:{enabled:true,description:spec.title},
    grid:horizontal?{top:8,right:12,bottom:texts?8:24,left:16,containLabel:true}:{top:spec.series.length>1?56:36,right:28,bottom:zoom?80:48,left:36,containLabel:true},
    legend:{show:spec.series.length>1,top:4,type:'scroll',textStyle:{color:theme['text-2'],fontSize:11},inactiveColor:theme['text-disabled'],pageIconColor:theme['text-2'],pageIconInactiveColor:theme['text-disabled'],pageTextStyle:{color:theme['text-3']}},
    tooltip:{trigger:'axis',confine:true,renderMode:'html',backgroundColor:theme['surface-3'],borderColor:theme['border-strong'],padding:12,textStyle:{color:theme.text,fontSize:12},axisPointer:{type:spec.kind==='bar'?'shadow':'line'},formatter:(items:{seriesIndex:number;dataIndex:number}[])=>items.map(item=>{
      const series=spec.series[item.seriesIndex],category=categories[item.dataIndex],point=points[item.seriesIndex].get(category);
      return escape(publicText(`${point?.label??category} · ${seriesName(item.seriesIndex)}: ${point?.y??'Unavailable'} ${spec.unit}${point?.text!==undefined?' · '+point.text:''}`));
    }).join('<br>')},
    xAxis:horizontal?valueAxis:categoryAxis,
    yAxis:horizontal?(texts?[{...categoryAxis,inverse:true,nameGap:0,name:''},textAxis]:{...categoryAxis,inverse:true,nameGap:0,name:''}):valueAxis,
    dataZoom:[{type:'inside',filterMode:'none',disabled:!zoom},{type:'slider',show:zoom,labelFormatter:(_value:number,label:string)=>categoryLabel(label),height:16,bottom:10,borderColor:theme.border,backgroundColor:theme['surface-2'],fillerColor:theme.accent+'33',handleStyle:{color:theme.accent},moveHandleStyle:{color:theme.accent,opacity:.35},
      dataBackground:{lineStyle:{color:theme['border-strong']},areaStyle:{color:theme['surface-3']}},selectedDataBackground:{lineStyle:{color:theme.accent},areaStyle:{color:theme.accent+'40'}},emphasis:{handleStyle:{color:theme['accent-text']},moveHandleStyle:{color:theme.accent,opacity:.6}},textStyle:{color:theme['text-3'],fontSize:10}}],
    series:spec.series.map((s,index)=>({id:s.id??String(index),name:seriesName(index),type:spec.kind??'line',connectNulls:false,symbol:'circle',showSymbol:!spec.dense,...(spec.dense?{sampling:'lttb'}:{}),symbolSize:7,lineStyle:{width:2.5},barMaxWidth:horizontal?18:44,itemStyle:{borderRadius:spec.kind==='bar'&&!horizontal?[3,3,0,0]:0},emphasis:{focus:'series'},data:categories.map(x=>datum(points[index].get(x))),
      ...(index===0&&reference!==undefined?{markLine:{silent:true,symbol:'none',animation:false,lineStyle:{type:'dashed',color:markColor,width:1.5},label:{show:true,position:'insideEndTop',color:markColor,fontSize:11,formatter:publicText(spec.reference!.label).replace(/[{}|]/g,'')},data:[{yAxis:reference}]}}:{})})),
  };
  if(spec.kind!=='heatmap')return option;
  const cells:number[][]=[];let magnitude=0,negative=false;
  spec.series.forEach((s,y)=>categories.forEach((x,i)=>{const value=coordinate(points[y].get(x)?.y??null);if(value!==null){cells.push([i,y,value]);magnitude=Math.max(magnitude,Math.abs(value));negative||=value<0;}}));
  return {...option,grid:{...option.grid,top:24,bottom:zoom?114:94,left:16},legend:{show:false},
    xAxis:{...categoryAxis,splitArea:{show:true,areaStyle:{color:[theme['surface-2'],theme['surface-2']]}}},
    yAxis:{type:'category',data:spec.series.map((_,i)=>seriesName(i)),axisLabel:{color:theme['text-2'],fontSize:11,width:180,overflow:'truncate'},axisLine:{show:false},axisTick:{show:false},splitArea:{show:true,areaStyle:{color:[theme['surface-2'],theme['surface-2']]}}},
    // The caller states whether a positive coordinate is a gain or a loss; plotting never changes its sign.
    visualMap:{min:negative?-(magnitude||1):0,max:magnitude||1,calculable:true,orient:'horizontal',left:'center',bottom:zoom?32:10,itemHeight:150,itemWidth:10,text:['Higher','Lower'],textStyle:{color:theme['text-3'],fontSize:10},inRange:{color:negative?[polarity[tone(-1)],theme['surface-3'],polarity[tone(1)]]:ramps[spec.positiveTone==='loss'?'loss':'magnitude']}},
    tooltip:{...option.tooltip,trigger:'item',formatter:(item:{value:number[]})=>{const [x,y]=item.value,series=spec.series[y],point=points[y].get(categories[x]);return escape(publicText(`${point?.label??categories[x]} · ${seriesName(y)}: ${point?.y??'Unavailable'} ${spec.unit}`));}},
    series:[{type:'heatmap',data:cells,itemStyle:{borderWidth:3,borderColor:theme.surface,borderRadius:4},emphasis:{itemStyle:{borderColor:theme.text,borderWidth:2}},label:{show:cells.length<=48,fontSize:11,rich:{light:{color:theme.text},dark:{color:theme.bg}},formatter:(item:{value:number[]})=>{const [x,y,coordinate]=item.value,value=points[y].get(categories[x])?.y??'';return value.length<=12?`{${Math.abs(coordinate)>magnitude*darkLabelFrom?'dark':'light'}|${value}}`:'';}}}],
  };
}
const active=new Map<HTMLElement,{chart:Chart;resize:ResizeObserver;interactive:boolean}>();
/** Return the exact category identity the rendered bars or heatmap cells refer to. */
export function selectedChartCategory(spec:ChartSpec,event:{componentType?:string;dataIndex?:number;value?:unknown}):string|undefined{
 if(event.componentType!=='series')return undefined;
 const categories=[...new Set(spec.series.flatMap(s=>s.points.map(p=>p.x)))];
 const index=spec.kind==='heatmap'&&Array.isArray(event.value)?event.value[0]:event.dataIndex;
 return Number.isInteger(index)?categories[index!]:undefined;
}
export function clearChart(host:HTMLElement,message=''){
  const existing=active.get(host);if(existing){existing.resize.disconnect();existing.chart.dispose();active.delete(host);}host.replaceChildren();if(message)host.textContent=message;
}
// Bounds the labels of a drawn horizontal chart to its new width: its categories', and its listed texts' when it has them.
function boundLabels(chart:Chart,width:number){
  const axes=(chart.getOption().yAxis as unknown[]).length;
  chart.setOption({yAxis:labelShares.slice(0,axes).map(share=>({axisLabel:{width:labelWidth(width,share)}}))});
}
export function renderChart(host:HTMLElement,spec:ChartSpec){
  clearChart(host);host.classList.remove('empty');host.setAttribute('aria-label',publicText(spec.title));host.dataset.chartAxis=spec.axis;host.dataset.chartUnit=spec.unit;host.dataset.chartKind=spec.kind??'line';
  const toolbar=document.createElement('div');toolbar.className='chart-toolbar';
  const caption=document.createElement('span'),name=document.createElement('strong');name.textContent=publicText(spec.title);caption.append(name);
  if(spec.subtitle){const subtitle=document.createElement('small');subtitle.textContent=publicText(spec.subtitle);caption.append(subtitle);}toolbar.append(caption);
  const plot=document.createElement('div');plot.className='standard-chart-plot';plot.setAttribute('role','img');plot.setAttribute('aria-label',publicText(spec.title));
  host.append(toolbar,plot);
  const horizontal=spec.orientation==='horizontal';
  let option;try{option=chartOptions(spec,horizontal?{width:plot.clientWidth}:undefined);}catch(error){host.replaceChildren();throw error;}
  if(!spec.series.some(s=>s.points.some(p=>p.y!==null))){plot.textContent='No values available for this selection.';return;}
  const chart=init(plot,undefined,{renderer:'canvas'});try{chart.setOption(option);}catch(error){chart.dispose();host.replaceChildren();throw error;}
  if(spec.onSelect)chart.on('click',event=>{
    const category=selectedChartCategory(spec,event);if(category!==undefined)spec.onSelect!(category);
  });
  // A horizontal chart's labels are bounded by its width, so they are bounded again whenever it resizes.
  const resize=new ResizeObserver(()=>{if(horizontal)boundLabels(chart,plot.clientWidth);chart.resize();});resize.observe(plot);active.set(host,{chart,resize,interactive:!!spec.onSelect});
  function action(label:string,callback:()=>void){const button=document.createElement('button');button.type='button';button.className='text-button';button.textContent=label;button.onclick=callback;toolbar.append(button);}
  // Horizontal bars show every category and have no zoom to reset.
  if(!horizontal)action('Reset zoom',()=>chart.dispatchAction({type:'dataZoom',start:0,end:100}));
  if(spec.exportImage)action('Save image',()=>spec.exportImage!(()=>{if(active.get(host)?.chart!==chart||!host.isConnected)throw Error('Refresh this chart before exporting.');const a=document.createElement('a');a.download='leanetrm-chart.png';chart.setOption({title:{show:true},legend:{top:48},grid:{top:82}});try{a.href=chart.getDataURL({type:'png',pixelRatio:2,backgroundColor:theme.surface});}finally{chart.setOption({title:{show:false},legend:{top:4},grid:{top:option.grid.top}});}a.click();}));
}

// Widget updates keep the canvas and its zoom for the same data scope. Export-enabled
// charts retain the full renderer because their callbacks own the original specification.
export function updateChart(host:HTMLElement,spec:ChartSpec){
 const existing=active.get(host);
 if(!existing||spec.exportImage||spec.onSelect||existing.interactive){renderChart(host,spec);return;}
 const plot=host.querySelector<HTMLElement>('.standard-chart-plot');
 const option=chartOptions(spec,spec.orientation==='horizontal'&&plot?{width:plot.clientWidth}:undefined);
 // New zoom options omit start/end, so ECharts retains the current window while updating availability.
 existing.chart.setOption(option,{replaceMerge:['series']});
 host.setAttribute('aria-label',publicText(spec.title));host.dataset.chartUnit=spec.unit;host.dataset.chartAxis=spec.axis;host.dataset.chartKind=spec.kind??'line';
 const title=host.querySelector('.chart-toolbar strong');if(title)title.textContent=publicText(spec.title);
 const caption=title?.parentElement;caption?.querySelector('small')?.remove();
 if(caption&&spec.subtitle){const subtitle=document.createElement('small');subtitle.textContent=publicText(spec.subtitle);caption.append(subtitle);}
 host.querySelector('[role="img"]')?.setAttribute('aria-label',publicText(spec.title));
}
