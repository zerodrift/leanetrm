import test from 'node:test';
import assert from 'node:assert/strict';
// The chart library reads the browser's user agent when it loads; the pure functions under test never draw.
globalThis.window??={navigator:{userAgent:'node'}};
const {timeChartData,overlayLookup,overlayLegend,overlayMenuState,latestValues,chartStyles,exactTexts,isolatedPoints,withIsolatedPoints}=await import('../dist/time-chart.js');
import {plotNumber} from '../dist/plot-number.js';

const at=n=>`2027-03-${String(n).padStart(2,'0')}T00:00:00Z`;
const ms=n=>Date.parse(at(n));

test('a missing price stays a gap in the chart data',()=>{
  const data=timeChartData([{at:at(1),value:'10.5'},{at:at(2),value:null},{at:at(3),value:'11'}]);
  assert.deepEqual(data.map(bar=>bar.timestamp),[ms(1),ms(2),ms(3)]);
  assert.deepEqual(data.map(bar=>bar.close),[10.5,null,11]);
});

test('exact strings become pixels only through plotNumber',()=>{
  const value='12345678901234567.123456789012345678';
  assert.equal(timeChartData([{at:at(1),value}])[0].close,plotNumber(value));
  assert.throws(()=>timeChartData([{at:at(1),value:'1e3'}]),/exact finite decimal/);
});

test('unsorted or repeated instants are refused',()=>{
  assert.throws(()=>timeChartData([{at:at(2),value:'1'},{at:at(1),value:'2'}]),/ascending/);
  assert.throws(()=>timeChartData([{at:at(1),value:'1'},{at:at(1),value:'2'}]),/ascending/);
  assert.throws(()=>timeChartData([{at:'not a time',value:'1'}]),/instant/);
});

const sma={code:'simple_moving_average_v1',label:'Simple moving average',parameters:{window:5},policy:'exact_rational_half_even_18_v1',
  series:{average:[{at:at(1),value:null,reason:'window_incomplete'},{at:at(2),value:'10.25'},{at:at(3),value:'10.5'}]}};

test('the lookup returns backend values by instant and nothing elsewhere',()=>{
  const find=overlayLookup(sma);
  assert.deepEqual(find(ms(2)),{average:10.25});
  assert.deepEqual(find(ms(3)),{average:10.5});
  assert.equal(find(ms(1)).average,undefined);
  assert.equal(find(ms(9)).average,undefined);
});

test('a lookup refuses an overlay that repeats an instant',()=>{
  assert.throws(()=>overlayLookup({...sma,series:{average:[{at:at(2),value:'1'},{at:at(2),value:'2'}]}}),/repeats/);
});

test('the legend names the definition and its parameters',()=>{
  assert.equal(overlayLegend(sma),'Simple moving average · 5');
  assert.equal(overlayLegend({...sma,label:'Band',parameters:{window:5,step:2}}),'Band · step 2, window 5');
});

const catalog={policy:'exact_rational_half_even_18_v1',overlays:[
  {code:'simple_moving_average_v1',label:'Simple moving average',parameters:[{name:'window',label:'Window',minimum:2,maximum:250}],seriesNames:['average']},
  {code:'rolling_range_v1',label:'Rolling range',parameters:[{name:'window',label:'Window',minimum:2,maximum:250}],seriesNames:['high','low']}]};

test('Apply is disabled while no window is entered',()=>{
  assert.equal(overlayMenuState(catalog,{}).canApply,false);
  assert.equal(overlayMenuState(catalog,{simple_moving_average_v1:{window:''}}).canApply,false);
});

test('Apply is disabled for a window out of bounds or not a whole number',()=>{
  for(const window of ['1','251','2.5','abc','-3','1e1',' '])
    assert.equal(overlayMenuState(catalog,{simple_moving_average_v1:{window}}).canApply,false,window);
});

test('Apply sends explicit parameters for every filled definition',()=>{
  const state=overlayMenuState(catalog,{simple_moving_average_v1:{window:'5'},rolling_range_v1:{window:'20'}});
  assert.equal(state.canApply,true);
  assert.deepEqual(state.requests,[{code:'simple_moving_average_v1',parameters:{window:5}},{code:'rolling_range_v1',parameters:{window:20}}]);
});

test('one invalid definition blocks Apply even when another is valid',()=>{
  assert.equal(overlayMenuState(catalog,{simple_moving_average_v1:{window:'5'},rolling_range_v1:{window:'1'}}).canApply,false);
});

test('latest values show the last point as the backend states it',()=>{
  const rows=latestValues({unit:'USD / MWh',points:[{at:at(1),value:'10'},{at:at(2),value:'10.50'}],overlays:[sma]});
  assert.deepEqual(rows,[{name:'Price',value:'10.50 USD / MWh'},{name:'Simple moving average · 5 · average',value:'10.5'}]);
  const gap=latestValues({unit:'u',points:[{at:at(1),value:null}],overlays:[{...sma,series:{average:[{at:at(1),value:null,reason:'window_incomplete'}]}}]});
  assert.deepEqual(gap,[{name:'Price',value:'No price'},{name:'Simple moving average · 5 · average',value:'No value (window incomplete)'}]);
});

test('chart styles come from the page tokens',()=>{
  const tokens={'--font':'Inter','--text':'#111111','--text-2':'#222222','--text-3':'#333333','--border':'#444444','--surface':'#555555','--accent':'#666666'};
  const styles=chartStyles(tokens);
  assert.equal(styles.yAxis.tickText.color,'#333333');
  assert.equal(styles.grid.horizontal.color,'#444444');
  assert.equal(styles.indicator.lines[0].color,'#666666');
  assert.throws(()=>chartStyles({}),/--font token/);
});

test('the text shown for an instant is the exact string the backend stated, never a rounded number',()=>{
  const shown=exactTexts({price:[{at:at(1),value:'10'},{at:at(2),value:'10.50'},{at:at(3),value:null}],
    average:[{at:at(2),value:'-2.416666666666666667'},{at:at(3),value:null,reason:'window_incomplete'}]});
  assert.deepEqual(shown(ms(2)),{price:'10.50',average:'-2.416666666666666667'});
  assert.deepEqual(shown(ms(1)),{price:'10',average:undefined});
  assert.deepEqual(shown(ms(3)),{price:undefined,average:undefined});
  assert.deepEqual(shown(ms(9)),{price:undefined,average:undefined});
});

test('a point is isolated when neither neighbour has a value',()=>{
  assert.deepEqual(isolatedPoints([1,undefined,2,3,undefined,4,null]),[0,5]);
  assert.deepEqual(isolatedPoints([undefined,1]),[1]);
  assert.deepEqual(isolatedPoints([1,undefined]),[0]);
  assert.deepEqual(isolatedPoints([undefined,5,undefined]),[1]);
  assert.deepEqual(isolatedPoints([null,undefined]),[]);
  assert.deepEqual(isolatedPoints([7]),[0]);
  assert.deepEqual(isolatedPoints([]),[]);
});

test('marker values appear only at isolated points',()=>{
  const rows=withIsolatedPoints([{price:1},{price:undefined},{price:2},{price:3}],['price']);
  assert.deepEqual(rows.map(row=>row.price_point),[1,undefined,undefined,undefined]);
});
