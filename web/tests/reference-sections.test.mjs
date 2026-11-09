import test from 'node:test';
import assert from 'node:assert/strict';
import {dataSections,referenceGroups,foldedIntoProduct} from '../dist/data-sections.js';

test('reference navigation describes every offered category by its current consumer',()=>{
  const offered=['units','unit_conversions','currencies','commodities','locations','location_types','exchanges','clearing_houses','calendars','calendar_days','futures_products','futures_series','futures_milestones','price_index_definitions','futures_execution_policies','interest_rate_definitions','sources','clock_rules','delivery_profiles','settlement_targets','quote_specifications','quantity_specifications','measurement_bases','value_specifications','cash_rounding_conventions'];
  const actual=dataSections(offered).flatMap(g=>g.sections);
  assert.equal(actual.length,21);
  assert.deepEqual(foldedIntoProduct,['futures_series','futures_milestones','futures_execution_policies']);
  assert.ok(!actual.some(s=>s.value==='cash_rounding_conventions'));
  for(const section of actual)assert.ok(typeof section.consumer==='string'&&section.consumer.length>25,section.name+' needs a named current use');
  assert.match(actual.find(s=>s.value==='unit_conversions').consumer,/quantity converter/i);
  assert.match(actual.find(s=>s.value==='calendar_days').consumer,/schedules|power/i);
});

test('internal storage does not become a reference category merely because it is available',()=>{
  const groups=dataSections(['revisions','commodity_units','reference_import_receipts','custom_curve_nodes','currencies']);
  assert.deepEqual(groups.flatMap(g=>g.sections).map(s=>s.value),['currencies']);
});

test('categories are grouped with their setup area and futures products are one Futures entry',()=>{
  assert.deepEqual(referenceGroups.map(g=>g.name),['Curves & pricing','Time','Products','Commodities & units','Physical']);
  assert.deepEqual(referenceGroups.map(g=>g.sections.map(s=>s.value)),[['price_index_definitions','interest_rate_definitions','sources'],['calendars','calendar_days','clock_rules'],['futures_products','exchanges','clearing_houses','quote_specifications','settlement_targets'],['commodities','units','unit_conversions','currencies','measurement_bases','quantity_specifications','value_specifications'],['locations','location_types','delivery_profiles']]);
  assert.equal(referenceGroups[2].sections[0].name,'Futures');
  assert.match(referenceGroups[2].sections[0].consumer,/contract economics/);
});
