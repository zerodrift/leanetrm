import test from 'node:test';
import assert from 'node:assert/strict';
import {deliveryPriceChoices,latestDeliverySource} from '../dist/delivery-price-filters.js';

const source=(id,commodityKey,commodityName,provider,market,locationKey,locationName)=>({id,name:id,commodityKey,commodityName,provider,market,marketName:market==='DA'?'Day ahead':'Real time',locationKey,locationName,minutes:60,marketClock:'America/New_York',priceUnit:'USD / MWh',availableDates:['2026-11-02'],availableThrough:{'2026-11-02':'2026-11-02'}});
const sources=[source('east_da','r1:power','Power','PJM','DA','r1:hub','Eastern hub'),source('east_rt','r1:power','Power','PJM','RT','r1:hub','Eastern hub'),source('south_da','r1:power','Power','ERCOT','DA','r1:south','Southern hub'),source('gas_da','r1:gas','Gas','Other','DA','r1:gas_hub','Gas hub'),source('other_version','r2:power','Power','PJM','DA','r2:hub','Eastern hub')];

test('delivery filters begin with commodity and require explicit choices at each stage',()=>{
  const first=deliveryPriceChoices(sources,{});
  assert.equal(first.commodities.length,3);
  assert.deepEqual(first.providers,[]);
  assert.deepEqual(first.markets,[]);
  assert.deepEqual(first.locations,[]);
  assert.deepEqual(first.sources,[]);
  assert.deepEqual(deliveryPriceChoices(sources,{commodityKey:'r1:power'}).providers.map(x=>x.value),['ERCOT','PJM']);
  assert.deepEqual(deliveryPriceChoices(sources,{commodityKey:'r1:power',provider:'PJM'}).markets.map(x=>x.value),['DA','RT']);
});

test('delivery filters retain exact reference identities and never expose a mismatched source',()=>{
  const path={commodityKey:'r1:power',provider:'PJM',market:'DA',locationKey:'r1:hub'};
  assert.deepEqual(deliveryPriceChoices(sources,path).sources.map(x=>x.value),['east_da']);
  assert.deepEqual(deliveryPriceChoices(sources,{...path,commodityKey:'r2:power'}).sources,[]);
  assert.deepEqual(deliveryPriceChoices(sources,{...path,provider:'ERCOT'}).sources,[]);
  assert.deepEqual(deliveryPriceChoices(sources,{commodityKey:'unknown'}).providers,[]);
});

test('conflicting labels for one exact reference fail instead of silently selecting one',()=>{
  assert.throws(()=>deliveryPriceChoices([...sources,{...sources[0],id:'bad',commodityName:'Different commodity'}],{}),/inconsistent/i);
});


test('a source without saved delivery dates contributes no choices at any level',()=>{
 const empty={...sources[0],id:'empty',commodityKey:'empty',commodityName:'Empty',availableDates:[],availableThrough:{}};
 assert.deepEqual(deliveryPriceChoices([...sources,empty],{}),deliveryPriceChoices(sources,{}));
 assert.deepEqual(deliveryPriceChoices([empty],{commodityKey:'empty',provider:'PJM',market:'DA',locationKey:'r1:hub'}).sources,[]);
});

test('initial browsing picks the newest available delivery date and handles no records',()=>{
 const older={...sources[0],availableDates:['2026-10-01']};
 const newer={...sources[1],availableDates:['2026-12-01','2026-11-01']};
 assert.equal(latestDeliverySource([older,newer]),newer);
 assert.equal(latestDeliverySource([{...older,availableDates:[]}]),undefined);
});
