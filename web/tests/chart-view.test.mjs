import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {chartOptions,palette} from '../dist/chart-view.js';
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
