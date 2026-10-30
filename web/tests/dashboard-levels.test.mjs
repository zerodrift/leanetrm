import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {levelsTiles,levelsFreshness,levelsFooter,readableCurves,contractChoices} from '../dist/dashboard-levels.js';
import {setCurveNames} from '../dist/market-view.js';

const fixture=JSON.parse(readFileSync(new URL('./fixtures/desk.json',import.meta.url)));
const [priced,unreadable]=fixture.shapes;
setCurveNames([{code:'gas_forward',name:'Gas forward'},{code:'power_forward',name:'Power forward'},{code:'gas_futures',name:'Gas futures'}]);

test('tiles follow configured order with backend front, price and unit unchanged',()=>{
  const shapes=new Map([['gas_forward',priced],['power_forward',{...priced,curve:'power_forward',front:{...priced.front,price:'41.250'},unit:'USD/MWh'}]]);
  const tiles=levelsTiles(shapes,['power_forward','gas_forward']);
  assert.deepEqual(tiles.map(t=>t.curve),['power_forward','gas_forward']);
  assert.deepEqual([tiles[0].name,tiles[0].front,tiles[0].price,tiles[0].unit],['Power forward','Jan 2021','41.250','USD/MWh']);
  assert.deepEqual([tiles[1].name,tiles[1].price,tiles[1].unit],['Gas forward','3.49','USD/MMBtu (IT)']);
  assert.equal(tiles[1].sparkline.state,'drawn');
});

test('a configured curve missing from the read becomes a refusal tile, not a gap',()=>{
  const tiles=levelsTiles(new Map([['gas_forward',priced]]),['gas_forward','gas_futures']);
  assert.equal(tiles.length,2);
  assert.equal(tiles[1].curve,'gas_futures');assert.match(tiles[1].refusal,/did not return/);
});

test('a curve the close cannot price shows the backend\'s reason',()=>{
  const unpriced={...priced,front:null,availability:'no_priced_contract'};
  const tiles=levelsTiles(new Map([['gas_forward',unpriced],['power_forward',{...unreadable,curve:'power_forward'}],['gas_futures','Curve shape unavailable · the curve read failed']]),['gas_forward','power_forward','gas_futures']);
  assert.equal(tiles[0].refusal,'No prices at this close');
  assert.equal(tiles[1].refusal,"Curve shape unavailable · this market's curve is not in the definitions of this close");
  assert.equal(tiles[2].refusal,'Curve shape unavailable · the curve read failed');
});

test('freshness is latest, newer_available or historical from the close badge, with the close instant',()=>{
  const at='2026-10-30T15:00:00Z';
  assert.deepEqual(levelsFreshness('latest',at,'Evening close'),{state:'latest',observedAt:at,source:'Evening close'});
  assert.equal(levelsFreshness('newer',at,'Evening close').state,'newer_available');
  assert.equal(levelsFreshness('historical',at,'Evening close').state,'historical');
  assert.equal(levelsFreshness('latest',at).source,'Settlement close','a close without a label is named for what it is');
  assert.throws(()=>levelsFreshness('choose',at,'x'),/close/);
});

test('intraday reads "Intraday not configured"',()=>{
  const footer=levelsFooter({state:'latest',observedAt:'2026-10-30T15:00:00Z',source:'Evening close'},'UTC');
  assert.match(footer,/Evening close/);assert.match(footer,/Latest/);assert.match(footer,/Intraday not configured/);
});

test('pickers offer only the navigation\'s curves that a curve-shapes read can price, and the chosen curve\'s own contracts',()=>{
  const curves=[{code:'gas_forward',marketType:'forwards'},{code:'usd_rates',marketType:'rates'}];
  assert.deepEqual(readableCurves({curves}).map(c=>c.code),['gas_forward']);
  const choices=contractChoices(priced);
  assert.equal(choices[0].contractId,priced.points[0].contractId);assert.equal(choices[0].label,'Jan 2021');
  assert.deepEqual(contractChoices(unreadable),[]);
});
