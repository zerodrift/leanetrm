import test from 'node:test';
import assert from 'node:assert/strict';
import {chartOptions,chartTokens,palette,polarity,selectedChartCategory} from '../dist/chart-view.js';
import {contrast,css,resolved,token} from './theme-tokens.mjs';
test('bar and heatmap drill-through use the plotted exact factor identity, including sparse series',()=>{
 const spec={title:'Risk',axis:'category',unit:'USD',kind:'bar',series:[{name:'A',points:[{x:'jan',y:'0'},{x:'feb',y:'1'}]},{name:'B',points:[{x:'mar',y:'2'}]}]};
 assert.equal(selectedChartCategory(spec,{componentType:'series',dataIndex:1}),'feb');
 assert.equal(selectedChartCategory({...spec,kind:'heatmap'},{componentType:'series',value:[2,1,2]}),'mar');
 for(const event of [{componentType:'axis',dataIndex:0},{componentType:'series',dataIndex:99},{componentType:'series',dataIndex:-1},{componentType:'series',dataIndex:1.5}])assert.equal(selectedChartCategory(spec,event),undefined);
});
// A series without its own color takes the palette entry for its index, as ECharts does.
const colorOf=(option,index)=>{const s=option.series[index];return s.itemStyle?.color??s.lineStyle?.color??option.color[index%option.color.length];};
test('series take the shared palette in fixed order',()=>{
 const o=chartOptions({title:'t',axis:'category',unit:'USD',series:[{name:'a',points:[{x:'1',y:'1'}]},{name:'b',points:[{x:'1',y:'2'}]}]});
 assert.deepEqual(o.series.map((_,i)=>colorOf(o,i)),['#5893ee','#e26d28']);
});
test('the shared palette keeps its validated order, each colour 3:1 or better on every surface a chart is drawn on',()=>{
 assert.deepEqual(palette,['#5893ee','#e26d28','#2dac7c','#9b7ee5','#db6190','#c08a11']);
 for(const colour of palette)for(const ground of ['--bg','--surface','--surface-2','--surface-3']){
  const ratio=contrast(colour,token(ground));assert.ok(ratio>=3,`${colour} on ${ground} is ${ratio.toFixed(2)}:1`);
 }
});
test('design tokens carry the specified commodity, polarity and radius values',()=>{
 const names=['--gas','--power','--gain','--loss','--gain-ink','--loss-ink','--radius-lg'];
 assert.deepEqual(Object.fromEntries(names.map(name=>[name,token(name)])),
  {'--gas':'#e26d28','--power':'#5893ee','--gain':'#00a17d','--loss':'#f45a51','--gain-ink':'#8fe0b4','--loss-ink':'#ff9d8a','--radius-lg':'12px'});
});
test('the commodity colors in CSS are the chart palette entries for power and gas, and the calendar marks are palette entries too',()=>{
 assert.equal(token('--power'),palette[0]);assert.equal(token('--gas'),palette[1]);
 assert.deepEqual(['--event-start','--event-end','--event-expiry','--event-date'].map(token),[palette[2],palette[0],palette[1],palette[3]]);
});
// The shared `th` rule's declarations as a property map.
const sharedHeader=Object.fromEntries((css.match(/^th\{([^}]*)\}/m)?.[1]??'').split(';').filter(Boolean).map(d=>[d.slice(0,d.indexOf(':')).trim(),d.slice(d.indexOf(':')+1).trim()]));
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
 assert.deepEqual(o.series[0].data.map(d=>d.itemStyle.color),['#00a17d','#f45a51']);
});
test('horizontal bars list categories top down, and their texts in a column beside the bars, toned by sign',()=>{
 const o=chartOptions({title:'PV',axis:'category',unit:'USD',kind:'bar',orientation:'horizontal',colorBySign:true,series:[{name:'PV',points:[{x:'a',y:'271',text:'+271.00'},{x:'b',y:'-284',text:'−284.00'},{x:'c',y:'0',text:'0.00'}]}]});
 const [categories,texts]=o.yAxis;
 assert.deepEqual(categories.data,['a','b','c']);assert.equal(categories.inverse,true);
 assert.equal(texts.position,'right');assert.deepEqual(texts.data,['a','b','c']);assert.equal(texts.inverse,true);
 assert.deepEqual(['a','b','c'].map(texts.axisLabel.formatter),['{gain|+271.00}','{loss|−284.00}','0.00']);
 assert.deepEqual(o.series[0].data.map(d=>d.value),[271,-284,0]);
 assert.equal(o.series[0].data[2].itemStyle.color,'#8d97b0','a zero is neither a gain nor a loss');
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
test('heatmaps shade with the shared palette family: a diverging ramp from loss to gain through the surface, else a single-hue ramp rising from it',()=>{
 const spec=y=>({title:'t',axis:'category',unit:'USD',kind:'heatmap',series:[{name:'a',points:[{x:'1',y}]}]});
 assert.deepEqual(chartOptions(spec('-3')).visualMap.inRange.color,['#f45a51',token('--surface-3'),'#00a17d']);
 assert.deepEqual(chartOptions(spec('3')).visualMap.inRange.color,['#132643','#305a9a','#5893ee']);
});
// A cell's label is written in one of two inks, by how far along its ramp the cell is. Where the two inks read equally well neither can
// reach AA (the most any pair of inks can hold across a continuous ramp is about 4.6:1), so the labels keep 4:1 or better everywhere.
test('a heatmap cell label stays 4:1 or better on its cell along the whole ramp',()=>{
 const channels=hex=>hex.slice(1).match(/../g).map(c=>parseInt(c,16)),hex=rgb=>'#'+rgb.map(c=>Math.round(c).toString(16).padStart(2,'0')).join('');
 // The cell's colour at a coordinate, as the library shades between the ramp's stops.
 const shade=(stops,share)=>{const at=share*(stops.length-1),i=Math.min(stops.length-2,Math.floor(at)),[from,to]=[channels(stops[i]),channels(stops[i+1])];return hex(from.map((c,k)=>c+(to[k]-c)*(at-i)));};
 for(const [low,high,tone] of [['-100','100','gain'],['0','100','gain'],['0','100','loss']]){
  const o=chartOptions({title:'t',axis:'category',unit:'USD',kind:'heatmap',positiveTone:tone,series:[{name:'a',points:[{x:'low',y:low},{x:'mid',y:'1'},{x:'high',y:high}]}]});
  const {min,max,inRange:{color:stops}}=o.visualMap,label=o.series[0].label;
  for(let step=0;step<=200;step++){
   const coordinate=min+(max-min)*step/200,ink=/^\{(\w+)\|/.exec(label.formatter({value:[1,0,coordinate]}))[1],ratio=contrast(label.rich[ink].color,shade(stops,step/200));
   assert.ok(ratio>=4,`a ${tone} cell at ${coordinate} labels ${ratio.toFixed(2)}:1`);
  }
 }
});
test('a chart draws its text, lines and grounds in the tokens of the shared stylesheet',()=>{
 for(const [name,value] of Object.entries(chartTokens))assert.equal(value,token('--'+name),name);
 // Every colour a line, bar or heatmap option states is one of those tokens (some at an alpha), a palette entry or a ramp step.
 const known=new Set([...Object.values(chartTokens),...palette,'#132643','#305a9a','#3f1916','#93332e']);
 const stated=value=>typeof value==='string'?(/^#[0-9a-f]{6,8}$/i.test(value)?[value.slice(0,7)]:[]):value&&typeof value==='object'?Object.values(value).flatMap(stated):[];
 const spec={title:'t',subtitle:'s',axis:'category',unit:'USD',colorBySign:true,marked:['a'],reference:{value:'1',label:'r'},series:[{name:'a',points:[{x:'a',y:'1',text:'+1'},{x:'b',y:'-2',text:'−2'}]},{name:'b',points:[{x:'a',y:'3'}]}]};
 for(const option of [chartOptions(spec),chartOptions({...spec,kind:'bar',orientation:'horizontal',reference:undefined}),chartOptions({...spec,kind:'heatmap'}),chartOptions({...spec,kind:'heatmap',positiveTone:'loss',series:[spec.series[1]],marked:[]})])
  assert.deepEqual([...new Set(stated(option))].filter(colour=>!known.has(colour)),[]);
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
 assert.equal(o.series[0].symbol,'circle','a point is a dot of its series, not a ring the library fills white');
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
 const grounds={row:token('--surface'),'hovered row':token('--accent-soft'),'group header':token('--surface-2'),'hovered group header':token('--surface-3')};
 for(const selector of ['.desk-sub','.desk-unit,.desk-muted','#desk-positions .tabulator-group span.desk-group-count']){
  const color=colorOf(selector);assert.ok(color,`${selector} sets no color`);
  for(const [name,background] of Object.entries(grounds)){const ratio=contrast(color,background);assert.ok(ratio>=4.5,`${selector} on a ${name} is ${ratio.toFixed(2)}:1`);}
 }
});

test('a loss chart shades positive losses red without reversing exact plotted values',()=>{
 const spec={title:'Scenario loss',axis:'category',unit:'USD',kind:'bar',colorBySign:true,positiveTone:'loss',series:[{name:'Book',points:[{x:'loss',y:'3'},{x:'gain',y:'-2'}]}]};
 const bars=chartOptions(spec).series[0].data;
 assert.equal(bars[0].value,3);assert.equal(bars[0].itemStyle.color,polarity.loss);assert.equal(bars[1].itemStyle.color,polarity.gain);
 const heat=chartOptions({...spec,kind:'heatmap'});
 assert.deepEqual(heat.visualMap.inRange.color,[polarity.gain,token('--surface-3'),polarity.loss]);
 assert.equal(heat.series[0].data[0][2],3);
 assert.equal(chartOptions({...spec,kind:'heatmap',series:[{name:'Book',points:[{x:'loss',y:'3'}]}]}).visualMap.inRange.color.at(-1),polarity.loss);
});
