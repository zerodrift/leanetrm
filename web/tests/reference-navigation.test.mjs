import test from 'node:test';
import assert from 'node:assert/strict';
import {groupReferenceHits} from '../dist/reference-navigation.js';
import {referenceGroups} from '../dist/data-sections.js';
import {parseReferenceAddress} from '../dist/reference-address.js';

const hit=(table,code,label,productCode=null)=>({table,code,label,productCode});

test('search hits are listed under their category names in tab order, each linking to its item',()=>{
  const grouped=groupReferenceHits([hit('locations','zone a/1','Zone A'),hit('currencies','test_currency_a','Test currency'),hit('locations','hub_b','Hub B')],referenceGroups);
  assert.deepEqual(grouped.map(g=>g.name),['Currencies','Locations']);
  assert.deepEqual(grouped[1].hits.map(h=>h.label),['Zone A','Hub B']);
  assert.deepEqual(parseReferenceAddress(grouped[1].hits[0].href),{table:'locations',code:'zone a/1'});
});

test('a futures contract hit opens its product under Futures',()=>{
  const [futures]=groupReferenceHits([hit('futures_series','gas_204101','2041-01','gas_future'),hit('futures_products','gas_future','Gas future','gas_future')],referenceGroups);
  assert.equal(futures.name,'Futures');
  assert.deepEqual(futures.hits.map(h=>parseReferenceAddress(h.href)),[{table:'futures_products',code:'gas_future'},{table:'futures_products',code:'gas_future'}]);
});

test('a hit outside the offered categories or a contract without its product is refused',()=>{
  const offered=referenceGroups.filter(g=>g.name==='Locations');
  assert.throws(()=>groupReferenceHits([hit('currencies','c','C')],offered));
  assert.throws(()=>groupReferenceHits([hit('futures_series','s','S',null)],referenceGroups));
  assert.throws(()=>groupReferenceHits([hit('cash_rounding_conventions','x','X')],referenceGroups));
});
