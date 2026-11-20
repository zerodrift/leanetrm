import {chartTokens,init,palette,type Chart} from './chart-view.js';
import {amountText} from './amount-display.js';
import type {StrategyChart} from './options-strategy.js';

// The strategy's profit and loss against the futures price, drawn in the theme's tokens. The expiry payoff is one solid
// line, filled in the gain tone above zero and the loss tone below; each dated curve is a dashed line with a dash of its
// own as well as a colour. A fill only clips the stated payoff at zero: no value is computed here.

// One dash per dated curve, so the curves are told apart without colour.
export const curveDashes:number[][]=[[8,5],[2,4],[12,4,2,4]];
// A stated profit and loss at its prepared managed display precision, signed when it is a gain.
const signed=(amount:string)=>{const text=amountText(amount,'money');return (!text.startsWith('-')&&/[1-9]/.test(text)?'+':'')+text;};
// An axis tick is the chart's own number, not a stated figure.
const tick=new Intl.NumberFormat('en',{maximumFractionDigits:2,signDisplay:'exceptZero'});
const escape=(value:string)=>value.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;');

/** The ECharts options of a strategy chart. */
export function strategyChartOptions(chart:StrategyChart){
 const t=chartTokens,expiry=chart.curves.find(c=>c.kind==='expiry')!,dated=chart.curves.filter(c=>c.kind==='dated');
 // The fills are not curves of their own: they have no legend entry and no readout.
 const fill=(id:string,color:string,clip:(y:number)=>number)=>({id,name:id,type:'line',data:expiry.points.map(({x,y})=>[x,clip(y)]),symbol:'none',silent:true,
  lineStyle:{width:0},areaStyle:{color,opacity:.16,origin:'auto'},tooltip:{show:false},emphasis:{disabled:true},z:1});
 const line={color:t['border-strong'],type:'solid' as const,width:1};
 // A plotted point carries the text its readout states.
 const data=(points:StrategyChart['curves'][number]['points'])=>points.map(p=>({value:[p.x,p.y],price:p.price,profitAndLoss:p.profitAndLoss}));
 return {
  backgroundColor:'transparent',animation:false,textStyle:{color:t['text-2'],fontFamily:'inherit'},
  grid:{left:12,right:24,top:52,bottom:8,containLabel:true},
  // A legend entry is its curve's own line, dash included, with no marker.
  legend:{top:0,left:0,textStyle:{color:t['text-2']},itemWidth:34,itemStyle:{opacity:0},data:chart.curves.map(c=>c.name)},
  tooltip:{trigger:'axis',axisPointer:{type:'cross',label:{backgroundColor:t['surface-3'],color:t.text},lineStyle:{color:t['text-3']},crossStyle:{color:t['text-3']}},
   backgroundColor:t['surface-2'],borderColor:t['border-strong'],textStyle:{color:t.text},
   formatter:(items:{seriesName:string;data:{price:string;profitAndLoss:string};marker:string}[])=>items.map(item=>
    `${item.marker}${escape(item.seriesName)} at ${escape(item.data.price)} ${escape(chart.priceUnit)}: <b>${escape(signed(item.data.profitAndLoss))}</b> ${escape(chart.currency)}`).join('<br>')},
  xAxis:{type:'value',name:'Futures price · '+chart.priceUnit,nameLocation:'middle',nameGap:28,min:expiry.points[0].x,max:expiry.points[expiry.points.length-1].x,
   axisLine:{lineStyle:line},axisLabel:{color:t['text-2']},splitLine:{lineStyle:{color:t.border}},nameTextStyle:{color:t['text-3']}},
  yAxis:{type:'value',scale:true,axisLine:{lineStyle:line},axisLabel:{color:t['text-2'],formatter:(value:number)=>tick.format(value)},
   splitLine:{lineStyle:{color:t.border}}},
  series:[
   fill('gain',t.gain,y=>Math.max(y,0)),fill('loss',t.loss,y=>Math.min(y,0)),
   {id:'expiry',name:expiry.name,type:'line',data:data(expiry.points),symbol:'none',lineStyle:{color:t.text,width:2.5,type:'solid'},itemStyle:{color:t.text},z:3,
    markLine:{silent:true,symbol:'none',label:{color:t['text-2'],fontSize:11},
     data:[{yAxis:0,lineStyle:{color:t['text-3'],type:'solid',width:1},label:{show:false}},
      ...chart.marks.map(mark=>({xAxis:mark.price,name:mark.label,lineStyle:{color:mark.kind==='futures'?t['accent-text']:t['text-3'],type:mark.kind==='futures'?'solid':'dotted',width:1},
       label:{formatter:mark.label,position:'end',distance:mark.kind==='futures'?16:3}}))]}},
   ...dated.map((curve,n)=>({id:curve.id,name:curve.name,type:'line',data:data(curve.points),symbol:'none',smooth:false,
    lineStyle:{color:palette[n%palette.length],width:2,type:curveDashes[n%curveDashes.length]},itemStyle:{color:palette[n%palette.length]},z:2}))]};
}

const drawn=new WeakMap<HTMLElement,{chart:Chart;resize:ResizeObserver}>();
/** Removes a host's chart. */
export function clearStrategyChart(host:HTMLElement){
 const existing=drawn.get(host);if(existing){existing.resize.disconnect();existing.chart.dispose();drawn.delete(host);}host.replaceChildren();
}
/** Draws a strategy chart in a host, replacing any it held, with a text alternative that names its curves and marks. */
export function renderStrategyChart(host:HTMLElement,chart:StrategyChart){
 clearStrategyChart(host);
 const caption=document.createElement('h3');caption.className='options-strategy-caption';caption.textContent=`Profit and loss · ${chart.currency}`;
 const plot=document.createElement('div');plot.className='options-strategy-plot';plot.setAttribute('role','img');
 plot.setAttribute('aria-label',`Profit and loss against the futures price: ${chart.curves.map(c=>c.name).join(', ')}. ${chart.marks.map(m=>m.label).join(', ')}`);
 host.append(caption,plot);
 const instance=init(plot,undefined,{renderer:'canvas'});instance.setOption(strategyChartOptions(chart));
 const resize=new ResizeObserver(()=>instance.resize());resize.observe(plot);drawn.set(host,{chart:instance,resize});
}
