import test from 'node:test';
import assert from 'node:assert/strict';
import {productSections,monthlyRows,milestoneColumns} from '../dist/futures-product-model.js';

const revision={catalogId:'catalog',revisionId:'original'};
const product={code:'gas_future',name:'Synthetic gas future',definition_state:'synthetic',exchange_code:'exchange_a',clearing_house_code:'clearing_a',settlement_method:'physical',
  commodity_code:'gas',location_code:'hub_a',market_clock_code:'clock_a',contract_size:10000,lot_unit_code:'mmbtu',lot_basis:'fixed_total',quote_currency_code:'usd',quote_unit_code:'mmbtu',
  quote_multiplier:1,outright_tick_size:0.001,trading_calendar_code:'calendar_a',delivery_calendar_code:'calendar_a',settlement_calendar_code:'calendar_b'};
const references={exchanges:[{code:'exchange_a',name:'Exchange A'}],clearing_houses:[{code:'clearing_a',name:'Clearing A'}],commodities:[{code:'gas',name:'Natural gas'}],
  locations:[{code:'hub_a',name:'Hub A'}],clock_rules:[{code:'clock_a'}],units:[{code:'mmbtu',symbol:'MMBtu'}],currencies:[{code:'usd',display_code:'USD',name:'US dollar'}],
  calendars:[{code:'calendar_a'},{code:'calendar_b'}]};
const series=[{code:'gas_204102',series_label:'2041-02',delivery_from:'2041-02-01',delivery_to:'2041-03-01'},{code:'gas_204101',series_label:'2041-01',delivery_from:'2041-01-01',delivery_to:'2041-02-01'}];
const milestones=[
  {series_code:'gas_204101',milestone:'last_trading',value_state:'known',business_date:'2040-12-28'},
  {series_code:'gas_204101',milestone:'first_trading',value_state:'known',business_date:'2036-01-02'},
  {series_code:'gas_204102',milestone:'first_trading',value_state:'unknown',business_date:null},
  {series_code:'gas_204102',milestone:'last_trading',value_state:'not_applicable',business_date:null}];
const policies=[{series_code:'gas_204101',implementation:'contract_lot_execution_pnl_v2',eligible_from:'2036-01-02T14:00:00Z'}];
const read=(changes={})=>({product,series,milestones,executionPolicies:policies,references,referenceRevision:revision,referenceDisplay:null,...changes});
const instant=value=>`at ${value}`;

test('product terms are grouped into venue, underlying, economics and calendars in a fixed order with labels',()=>{
  const sections=productSections(read());
  assert.deepEqual(sections.map(s=>s.title),['Venue','Underlying','Economics','Calendars']);
  assert.deepEqual(sections.map(s=>s.fields.map(f=>f.key)),[
    ['exchange_code','clearing_house_code','settlement_method'],
    ['commodity_code','location_code','market_clock_code'],
    ['contract_size','lot_unit_code','lot_basis','quote_currency_code','quote_unit_code','quote_multiplier','outright_tick_size'],
    ['trading_calendar_code','delivery_calendar_code','settlement_calendar_code']]);
  assert.deepEqual(sections[0].fields.map(f=>[f.label,f.value]),[['Exchange','Exchange A'],['Clearing house','Clearing A'],['Settlement method','Physical']]);
  assert.deepEqual(sections[3].fields.map(f=>f.label),['Trading calendar','Delivery calendar','Settlement calendar']);
});

test('a linked code shows its referenced row label and links to that item',()=>{
  const [venue,underlying,economics,calendars]=productSections(read());
  assert.deepEqual(venue.fields[0].linkTo,{table:'exchanges',code:'exchange_a'});
  assert.deepEqual(underlying.fields.map(f=>[f.value,f.linkTo]),[['Natural gas',{table:'commodities',code:'gas'}],['Hub A',{table:'locations',code:'hub_a'}],['Clock A',{table:'clock_rules',code:'clock_a'}]]);
  assert.deepEqual(economics.fields.map(f=>f.value),['10000','MMBtu','Fixed Total','US dollar','MMBtu','1','0.001']);
  assert.equal(economics.fields[0].linkTo,undefined);
  assert.deepEqual(calendars.fields.map(f=>f.linkTo.code),['calendar_a','calendar_a','calendar_b']);
});

test('applied display labels replace the source labels of the product and its references',()=>{
  const referenceDisplay={sourceRevision:revision,displayRevision:{catalogId:'catalog',revisionId:'labels'},version:1,references:{exchanges:[{code:'exchange_a',name:'Renamed exchange'}]}};
  assert.equal(productSections(read({referenceDisplay}))[0].fields[0].value,'Renamed exchange');
});

test('a missing optional term reads Not supplied, never a default',()=>{
  const sections=productSections(read({product:{...product,clearing_house_code:null,contract_size:null,settlement_calendar_code:null}}));
  const field=key=>sections.flatMap(s=>s.fields).find(f=>f.key===key);
  for(const key of ['clearing_house_code','contract_size','settlement_calendar_code']){assert.equal(field(key).value,'Not supplied');assert.equal(field(key).linkTo,undefined);}
});

test('a linked code whose referenced row is absent is an error',()=>{
  assert.throws(()=>productSections(read({references:{...references,locations:[]}})));
  assert.throws(()=>productSections(read({references:{...references,calendars:[{code:'calendar_a'}]}})));
});

test('a term the read omits entirely is an error, not Not supplied',()=>{
  const {settlement_method,...partial}=product;
  assert.throws(()=>productSections(read({product:partial})));
});

test('monthly rows keep the backend series order and pivot each milestone into its column',()=>{
  const rows=monthlyRows(read(),instant);
  assert.deepEqual(rows.map(r=>r.code),['gas_204102','gas_204101']);
  assert.deepEqual(rows[1],{code:'gas_204101',label:'2041-01',deliveryFrom:'2041-01-01',deliveryTo:'2041-02-01',milestones:{first_trading:'2036-01-02',last_trading:'2040-12-28'},eligibility:'From at 2036-01-02T14:00:00Z'});
  assert.deepEqual(milestoneColumns(read()),[{key:'first_trading',label:'First trading'},{key:'last_trading',label:'Last trading'}]);
});

test('unknown and not-applicable milestones read by their state labels',()=>{
  const [first]=monthlyRows(read(),instant);
  assert.deepEqual(first.milestones,{first_trading:'Unknown',last_trading:'Not applicable'});
  assert.equal(first.eligibility,'No execution policy');
});

test('a milestone or policy of a contract outside the product, or a repeated one, is an error',()=>{
  assert.throws(()=>monthlyRows(read({milestones:[...milestones,{series_code:'other',milestone:'first_trading',value_state:'unknown',business_date:null}]}),instant));
  assert.throws(()=>monthlyRows(read({milestones:[...milestones,milestones[0]]}),instant));
  assert.throws(()=>monthlyRows(read({executionPolicies:[{...policies[0],series_code:'other'}]}),instant));
  assert.throws(()=>monthlyRows(read({milestones:[{...milestones[0],value_state:'estimated'}]}),instant));
});
