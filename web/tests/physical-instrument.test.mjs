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

test('retained independent gas charges show the authored amount or rate and their own payment coordinates',()=>{
 const daily={slot:2,code:'capacity',role:'charge',direction:-1,clock_slot:0,source_slot:null,stage:null,charge_basis:'capacity',price:'0.02',
  charge_quantity_n:'20001',charge_quantity_d:'2',delivery_from:'2021-04-01',delivery_to:'2021-06-11',payment_at:null,payment_day:2,payment_convention:'next_month_day_utc_v1'};
 const lump={...daily,slot:1,code:'premium',charge_basis:'lump_sum',price:'5000.000000000000000002',charge_quantity_n:'1',charge_quantity_d:'1',
  payment_at:'2021-04-01T00:00:00Z',payment_day:null,payment_convention:null};
 const sections=gasInstrumentSections({head:{},physical:[{slot:0,unit:'MMBtu',currency:'USD',multiplier:'2'}],components:[
  {slot:0,code:'delivery',role:'physical',direction:1,independent_quantity:'10000'},lump,daily]},'America/Chicago');
 const premium=Object.fromEntries(sections.find(section=>section.title==='premium').rows);
 const capacity=Object.fromEntries(sections.find(section=>section.title==='capacity').rows);
 assert.deepEqual(premium,{'Side':'Charge','Direction':'Pay','Gas day of':'delivery','Charged on':'A lump sum','Amount':'5000.000000000000000002 USD','Payment date':'Apr 1, 2021'});
 assert.deepEqual(capacity,{'Side':'Charge','Direction':'Pay','Gas day of':'delivery','Charged on':'A daily capacity','Rate per unit per day':'0.02 USD/MMBtu/day',
  'Daily quantity':'20001/2 MMBtu','From':'2021-04-01','To · exclusive':'2021-06-11','Payment day':'2','Payment convention':'next_month_day_utc_v1'});
});

test('cash-out overview shows reported-balance terms and the named daily payment convention',()=>{
 const member={slot:1,code:'balance.to_us',role:'imbalance_cashout',direction:1,source_slot:0,stage:'delivery',price:null,payment_at:null,
  balance_side:'owed_to_us',cashout_profile:'daily_index_cashout_v1',statement_basis:'daily_imbalance_v1',account_netting:'net_accounts_v1',statement_policy:'final_statement_balance_v1'};
 const facts={head:{},physical:[{slot:0,unit:'MMBtu',currency:'USD',multiplier:'1'}],components:[{slot:0,code:'gas',role:'physical',direction:1,independent_quantity:'100'},member],
  indexedCash:[{slot:'1',index_code:'synthetic_daily_index',observation_convention:'named_index_daily_v1',payment_convention:'next_month_day_utc_v1',payment_day:'15',spread:'0',projection_id:null}]};
 const rows=Object.fromEntries(gasInstrumentSections(facts,'America/Chicago')[1].rows);
 assert.equal(rows.Side,'Imbalance cash-out');assert.equal(rows['Quantity source'],'gas · reported service-account balance');
 assert.equal(rows['Statements used'],'Final only');assert.equal(rows['A statement reports'],"That day's imbalance");
 assert.equal(rows['Several accounts'],'Net them, then settle');assert.equal(rows['Payment day'],'15');
 assert.equal(rows['Forecast projection'],'Named index curve');assert.equal(rows.Payment,undefined);assert.equal(rows['Fixed unit price'],undefined);
 assert.throws(()=>gasInstrumentSections({...facts,components:[facts.components[0],{...member,statement_policy:null}]},'UTC'),/cash-out.*terms/i);
});
