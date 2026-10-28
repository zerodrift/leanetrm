import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {chartOptions,palette,polarity} from '../dist/chart-view.js';
const css=readFileSync(new URL('../dist/app.css',import.meta.url),'utf8');
const token=name=>css.match(new RegExp(`${name}:\\s*([^;}]+)`))?.[1].trim();
// A series without its own color takes the palette entry for its index, as ECharts does.
const colorOf=(option,index)=>{const s=option.series[index];return s.itemStyle?.color??s.lineStyle?.color??option.color[index%option.color.length];};
test('series take the shared palette in fixed order',()=>{
 const o=chartOptions({title:'t',axis:'category',unit:'USD',series:[{name:'a',points:[{x:'1',y:'1'}]},{name:'b',points:[{x:'1',y:'2'}]}]});
 assert.deepEqual(o.series.map((_,i)=>colorOf(o,i)),['#2f8a66','#2a78d6']);
});
test('the shared palette keeps its validated order',()=>{
 assert.deepEqual(palette,['#2f8a66','#2a78d6','#eb6834','#6250d6','#e87ba4','#eda100']);
});
test('design tokens carry the specified commodity, polarity and radius values',()=>{
 const names=['--gas','--power','--gain','--loss','--gain-ink','--loss-ink','--radius-lg'];
 assert.deepEqual(Object.fromEntries(names.map(name=>[name,token(name)])),
  {'--gas':'#eb6834','--power':'#2a78d6','--gain':'#1f8a5b','--loss':'#d6453d','--gain-ink':'#0f6b3c','--loss-ink':'#b3261e','--radius-lg':'12px'});
});
test('the commodity colors in CSS are the chart palette entries for power and gas',()=>{
 assert.equal(token('--power'),palette[1]);assert.equal(token('--gas'),palette[2]);
});
// The shared `th` rule's declarations as a property map.
const sharedHeader=Object.fromEntries((css.match(/^th\{([^}]*)\}/m)?.[1]??'').split(';').filter(Boolean).map(d=>[d.slice(0,d.indexOf(':')).trim(),d.slice(d.indexOf(':')+1).trim()]));
const resolved=value=>value.startsWith('var(')?token(value.slice(4,-1)):value;
// WCAG 2.x contrast ratio of two hex colors.
const channels=hex=>(hex.length===4?[...hex.slice(1)].map(c=>c+c):hex.slice(1).match(/../g)).map(c=>parseInt(c,16)/255);
const luminance=hex=>{const [r,g,b]=channels(hex).map(c=>c<=0.03928?c/12.92:((c+0.055)/1.055)**2.4);return 0.2126*r+0.7152*g+0.0722*b;};
const contrast=(a,b)=>{const [light,dark]=[luminance(a),luminance(b)].sort((x,y)=>y-x);return (light+0.05)/(dark+0.05);};
// Headers name units such as MMBtu or kWh; they must read as the backend names them.
test('shared table headers leave the case of unit symbols alone',()=>{
 assert.ok(Object.keys(sharedHeader).length,'the shared th rule is missing');
 assert.ok([undefined,'none'].includes(sharedHeader['text-transform']),`th sets text-transform:${sharedHeader['text-transform']}`);
});
test('shared table header text meets AA contrast on its own background',()=>{
 assert.ok(Object.keys(sharedHeader).length,'the shared th rule is missing');
 const ratio=contrast(resolved(sharedHeader.color),resolved(sharedHeader.background));
 assert.ok(ratio>=4.5,`th text contrast is ${ratio.toFixed(2)}:1`);
});
test('sign-colored horizontal bars mark gains and losses',()=>{
 const o=chartOptions({title:'PV',axis:'category',unit:'USD',kind:'bar',orientation:'horizontal',colorBySign:true,series:[{name:'PV',points:[{x:'waha',y:'271'},{x:'chi',y:'-284'}]}]});
 assert.equal(o.yAxis.type,'category');assert.equal(o.xAxis.type,'value');
 assert.deepEqual(o.series[0].data.map(d=>d.itemStyle.color),['#1f8a5b','#d6453d']);
});
test('horizontal bars list categories top down, and their texts in a column beside the bars, toned by sign',()=>{
 const o=chartOptions({title:'PV',axis:'category',unit:'USD',kind:'bar',orientation:'horizontal',colorBySign:true,series:[{name:'PV',points:[{x:'a',y:'271',text:'+271.00'},{x:'b',y:'-284',text:'−284.00'},{x:'c',y:'0',text:'0.00'}]}]});
 const [categories,texts]=o.yAxis;
 assert.deepEqual(categories.data,['a','b','c']);assert.equal(categories.inverse,true);
 assert.equal(texts.position,'right');assert.deepEqual(texts.data,['a','b','c']);assert.equal(texts.inverse,true);
 assert.deepEqual(['a','b','c'].map(texts.axisLabel.formatter),['{gain|+271.00}','{loss|−284.00}','0.00']);
 assert.deepEqual(o.series[0].data.map(d=>d.value),[271,-284,0]);
 assert.equal(o.series[0].data[2].itemStyle.color,'#68776f','a zero is neither a gain nor a loss');
});
test('a horizontal chart that lists its values beside its bars needs no value-axis labels, and one that does not keeps them',()=>{
 const spec=text=>({title:'t',axis:'category',unit:'USD',kind:'bar',orientation:'horizontal',series:[{name:'s',points:[{x:'a',y:'3',...(text?{text:'+3.00'}:{})}]}]});
 assert.equal(chartOptions(spec(true)).xAxis.axisLabel.show,false);
 assert.notEqual(chartOptions(spec(false)).xAxis.axisLabel.show,false);
});
test('long labels and listed texts of a horizontal chart are cut short so its bars keep at least a third of the width',()=>{
 const long='Houston Ship Channel delivery-month forwards · generated prices · Swaps';
 const spec={title:'Long and short',axis:'category',unit:'MMBtu',kind:'bar',orientation:'horizontal',series:[{name:'Quantity',points:[{x:'h',label:long,y:'1000',text:'1,000 receive fixed swaps and more words'}]}]};
 for(const width of [900,600,343]){
  const [categories,texts]=chartOptions(spec,{width}).yAxis;
  assert.equal(categories.axisLabel.overflow,'truncate');assert.equal(texts.axisLabel.overflow,'truncate');
  assert.ok(categories.axisLabel.width+texts.axisLabel.width<=width*2/3,`labels take ${categories.axisLabel.width}+${texts.axisLabel.width} of ${width}`);
 }
});
test('bar colors are the polarity tokens of the shared stylesheet',()=>{
 assert.deepEqual([polarity.gain,polarity.loss,polarity.none],[token('--gain'),token('--loss'),token('--text-3')]);
});
test('a bar takes the color its point names, and a category label carries the marker its point names',()=>{
 const o=chartOptions({title:'Long and short',axis:'category',unit:'MWh',kind:'bar',orientation:'horizontal',series:[{name:'Quantity',points:[{x:'p',label:'Power',y:'100',color:'#2a78d6',marker:'#2a78d6'},{x:'g',label:'Gas',y:'-50',color:'#eb6834'}]}]});
 assert.deepEqual(o.series[0].data.map(d=>d.itemStyle.color),['#2a78d6','#eb6834']);
 assert.equal(o.yAxis.axisLabel.formatter('p'),'{m0|●} Power');assert.equal(o.yAxis.axisLabel.formatter('g'),'Gas');
 assert.equal(o.yAxis.axisLabel.rich.m0.color,'#2a78d6');
});
test('an unsigned amount whose direction is stated apart is drawn on that side, while its tooltip keeps the exact amount',()=>{
 const spec={title:'Long and short',axis:'category',unit:'MWh',kind:'bar',orientation:'horizontal',series:[{name:'Quantity',points:[{x:'s',label:'Swap',y:'50',direction:'negative',text:'50 receive fixed'}]}]};
 const o=chartOptions(spec);
 assert.equal(o.series[0].data[0].value,-50);assert.equal(o.yAxis[1].axisLabel.formatter('s'),'50 receive fixed');
 assert.equal(o.tooltip.formatter([{seriesIndex:0,dataIndex:0}]),'Swap · Quantity: 50 MWh · 50 receive fixed');
 assert.throws(()=>chartOptions({...spec,series:[{name:'Quantity',points:[{x:'s',y:'-50',direction:'negative'}]}]}),{message:'A chart point with a stated direction needs an unsigned amount.'});
});
test('heatmaps shade with the shared palette family: a diverging ramp from loss to gain, else a single-hue ramp',()=>{
 const spec=y=>({title:'t',axis:'category',unit:'USD',kind:'heatmap',series:[{name:'a',points:[{x:'1',y}]}]});
 assert.deepEqual(chartOptions(spec('-3')).visualMap.inRange.color,['#d6453d','#f7f8f7','#1f8a5b']);
 assert.deepEqual(chartOptions(spec('3')).visualMap.inRange.color,['#eaf3ee','#8cc0a8','#2f8a66']);
});
test('a diverging axis keeps zero in the middle between round ends',()=>{
 const o=chartOptions({title:'PV',axis:'category',unit:'USD',kind:'bar',orientation:'horizontal',colorBySign:true,series:[{name:'PV',points:[{x:'a',y:'369019.597'},{x:'b',y:'-51610623.41849047'}]}]});
 const range={min:-51610623.41849047,max:369019.597};
 assert.deepEqual([o.xAxis.min(range),o.xAxis.max(range)],[-60000000,60000000]);
 assert.deepEqual([o.xAxis.min({min:0,max:4000000}),o.xAxis.max({min:0,max:4000000})],[-4000000,4000000]);
 assert.deepEqual([o.xAxis.min({min:-2.5,max:1}),o.xAxis.max({min:-2.5,max:1})],[-2.5,2.5]);
});
test('a line chart emphasizes the points it marks and draws a reference line at an exact value, which its value axis then includes',()=>{
 const spec={title:'Gas forward',axis:'delivery',unit:'USD/MMBtu (IT)',kind:'line',series:[{name:'Curve',points:[{x:'jan',label:'Jan 2021',y:'3.1'},{x:'feb',label:'Feb 2021',y:'3.15'},{x:'mar',label:'Mar 2021',y:null}]}],
  marked:['feb'],reference:{value:'3.5',label:'Fixed 3.500 USD/MMBtu (IT)'}};
 const o=chartOptions(spec),[jan,feb,mar]=o.series[0].data;
 assert.equal(jan,3.1,'a point that is not marked stays a plain value');assert.equal(mar,null,'a gap stays a gap');
 assert.equal(feb.value,3.15);assert.ok(feb.symbolSize>o.series[0].symbolSize,'a marked point is drawn larger');
 const line=o.series[0].markLine;
 assert.deepEqual(line.data,[{yAxis:3.5}]);assert.equal(line.label.formatter,'Fixed 3.500 USD/MMBtu (IT)');assert.equal(line.symbol,'none');
 // The axis holds the data and the line with a little room beyond them, whichever side the line is on.
 const reaching=(range,low,high)=>{const [min,max]=[o.yAxis.min(range),o.yAxis.max(range)],room=(high-low)*.1;return min<=low&&min>low-room&&max>=high&&max<high+room;};
 assert.ok(reaching({min:3.1,max:3.15},3.1,3.5));
 assert.ok(reaching({min:3.8,max:4},3.5,4));
 assert.ok(o.yAxis.max({min:3.5,max:3.5})>o.yAxis.min({min:3.5,max:3.5}),'a line on the only value still has room around it');
});
test('a chart without marks or a reference line draws neither, and its axis is the data axis',()=>{
 const o=chartOptions({title:'Gas forward',axis:'delivery',unit:'USD/MMBtu (IT)',kind:'line',series:[{name:'Curve',points:[{x:'jan',y:'3.1'},{x:'feb',y:'3.15'}]}]});
 assert.deepEqual(o.series[0].data,[3.1,3.15]);assert.equal(o.series[0].markLine,undefined);
 assert.equal(o.yAxis.min,undefined);assert.equal(o.yAxis.max,undefined);
});
test('a chart marks only points it draws, and a reference line needs an exact value',()=>{
 const spec={title:'t',axis:'delivery',unit:'USD',kind:'line',series:[{name:'a',points:[{x:'jan',y:'3.1'}]}]};
 assert.throws(()=>chartOptions({...spec,marked:['feb']}),{message:'A chart can only mark points it draws.'});
 assert.throws(()=>chartOptions({...spec,reference:{value:'three',label:'x'}}),{message:'A plot needs exact finite decimal or rational strings.'});
});
test('a reference line given as a rational is placed by its ratio, and its label stays text',()=>{
 const o=chartOptions({title:'t',axis:'delivery',unit:'USD',kind:'line',series:[{name:'a',points:[{x:'jan',y:'1'}]}],reference:{value:'7/2',label:'Fixed 3.5 {not|rich}'}});
 // Plot coordinates place pixels only: the ratio is within rounding of its exact value.
 assert.equal(o.series[0].markLine.data.length,1);assert.ok(Math.abs(o.series[0].markLine.data[0].yAxis-3.5)<1e-9);assert.equal(o.series[0].markLine.label.formatter,'Fixed 3.5 notrich');
});
// The muted text of the desk's rows and group headers, on every background it sits on, hovered rows and headers included.
test('the muted text of the desk meets AA contrast on the rows and group headers it sits on, hovered or not',()=>{
 const colorOf=selector=>{const body=css.match(new RegExp(selector.replace(/[.*+?^${}()|[\]\\]/g,'\\$&')+'\\{([^}]*)\\}'))?.[1];return body&&resolved(body.match(/(?:^|[;\s])color:\s*([^;]+)/)?.[1].trim());};
 const grounds={row:'#ffffff','hovered row':token('--accent-soft'),'group header':token('--surface-2'),'hovered group header':token('--surface-3')};
 for(const selector of ['.desk-sub','.desk-unit,.desk-muted','#desk-positions .tabulator-group span.desk-group-count']){
  const color=colorOf(selector);assert.ok(color,`${selector} sets no color`);
  for(const [name,background] of Object.entries(grounds)){const ratio=contrast(color,background);assert.ok(ratio>=4.5,`${selector} on a ${name} is ${ratio.toFixed(2)}:1`);}
 }
});
