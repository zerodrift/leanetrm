import test from 'node:test';
import assert from 'node:assert/strict';
import {gasInstrumentSections} from '../dist/physical-instrument.js';

test('gas overview shows saved quantity or explicit linked-side terms, and a tariff fee without an invented fixed price',()=>{
 const physical={pipeline_code:'synthetic_pipe',zone_code:'synthetic_zone',point_code:'synthetic_point',product_code:'gas',unit:'MMBtu',currency:'USD',multiplier:'1',curve_name:'Synthetic basis'};
 const sections=gasInstrumentSections({head:{},physical:[{...physical,slot:0},{...physical,slot:1}],components:[
  {slot:0,code:'receipt',role:'physical',direction:1,quantity_basis:'per_operating_day',delivery_from:'2021-01-01',delivery_to:'2021-02-01',independent_quantity:'100.000000000000000001',source_slot:null},
  {slot:1,code:'delivery',role:'physical',direction:-1,quantity_basis:'per_operating_day',delivery_from:'2021-01-01',delivery_to:'2021-02-01',independent_quantity:null,source_slot:0,formula:'gross_less_fraction',fraction:'0.02'},
  {slot:2,code:'fee',role:'fee',direction:-1,source_slot:1,stage:'delivery',price:null,tariff_code:'synthetic_usage',payment_at:'2021-02-02T00:00:00Z'}]},'UTC');
 assert.deepEqual(sections[0].rows.find(([name])=>name==='Quantity'),['Quantity','100.000000000000000001 MMBtu']);
 assert.deepEqual(sections[1].rows.find(([name])=>name==='Quantity source'),['Quantity source','receipt']);
 assert.deepEqual(sections[1].rows.find(([name])=>name==='Link formula'),['Link formula','gross_less_fraction']);
 assert.deepEqual(sections[1].rows.find(([name])=>name==='Link fraction'),['Link fraction','0.02']);
 assert.deepEqual(sections[0].rows.find(([name])=>name==='Quantity basis'),['Quantity basis','Per Operating Day']);
 assert.deepEqual(sections[2].rows.find(([name])=>name==='Price unit'),['Price unit','USD/MMBtu']);
 assert.deepEqual(sections[2].rows.find(([name])=>name==='Tariff'),['Tariff','synthetic_usage']);
 assert.deepEqual(sections[2].rows.find(([name])=>name==='Quantity source'),['Quantity source','delivery']);
 assert.equal(sections[2].rows.some(([name])=>name==='Fixed unit price'),false);
});


test('indexed gas overview keeps native index, spread and payment terms without fabricating a fixed price or instant',()=>{
 const f={head:{},physical:[{slot:0,unit:'MMBtu',currency:'USD',multiplier:'1'}],components:[{slot:0,code:'gas',role:'physical',direction:1,independent_quantity:'100'},
  {slot:1,code:'index payment',role:'indexed_consideration',direction:-1,source_slot:0,stage:'delivery',price:null,payment_at:null}],indexedCash:[
  {slot:'1',index_code:'synthetic_daily_index',observation_convention:'daily_published_index_v1',payment_convention:'next_month_day_utc_v1',payment_day:'10',spread:'0.000000000000000001',projection_id:null}]};
 const rows=gasInstrumentSections(f,'UTC')[1].rows;
 for(const expected of [['Pricing index','synthetic_daily_index'],['Spread','0.000000000000000001'],['Observation convention','daily_published_index_v1'],['Payment convention','next_month_day_utc_v1'],['Payment day','10']])assert.deepEqual(rows.find(([label])=>label===expected[0]),expected);
 assert.equal(rows.some(([label])=>['Fixed unit price','Payment'].includes(label)),false);
 assert.throws(()=>gasInstrumentSections({...f,indexedCash:[]},'UTC'),/indexed.*terms/i);
});
