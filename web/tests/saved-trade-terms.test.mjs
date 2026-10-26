import test from 'node:test';
import assert from 'node:assert/strict';
import {changeReviewRows,unavailableValueLabel,changeFieldLabel} from '../dist/saved-trade-terms.js';
test('an amendment review keeps booked and proposed values distinct, including an absent forecast binding',()=>{
 const rows=changeReviewRows({action:'amend_trade',bookedRevision:3,changes:[{category:'forecast_source',field:'Floating side / Projected price source',before:null,after:'Saved output v2'},{category:'contract_terms',field:'Averaging',before:'Monthly index',after:'Daily arithmetic'}]});
 assert.deepEqual(rows.map(r=>[r.before,r.after]),[['None','Saved output v2'],['Monthly index','Daily arithmetic']]);
 assert.equal(rows[0].category,'Forecast source saved on this trade');
});
test('an unrecognized action cannot offer acceptance without an amendment',()=>{
 assert.throws(()=>changeReviewRows({action:'accept_without_amendment',bookedRevision:3,changes:[]}),/unavailable/);
 assert.throws(()=>changeReviewRows({action:'amend_trade',bookedRevision:0,changes:[]}),/unavailable/);
});
test('an unavailable choice is described as the booked value without claiming it is obsolete',()=>{
 assert.equal(unavailableValueLabel('Calendar-day arithmetic'),'Calendar-day arithmetic · booked value (unavailable here)');
});

test('a backend decision to keep unchanged booked terms offers no amendment',()=>{
 assert.deepEqual(changeReviewRows({action:'keep_booked_terms',bookedRevision:2,changes:[]}),[]);
 assert.throws(()=>changeReviewRows({action:'amend_trade',bookedRevision:2,changes:[]}),/unavailable/);
});

test('an unavailable draft selection is not described as a booked trade term',()=>{
 assert.equal(unavailableValueLabel('Daily arithmetic',false),'Daily arithmetic · entered value (unavailable here)');
});

test('scheduled fields name the side, delivery period and quantity in business language',()=>{
 assert.equal(changeFieldLabel('Scheduled / first / periods / 2 / amount'),'First side / Period 2 / Quantity');
});
