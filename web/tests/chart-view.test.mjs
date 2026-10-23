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
