import test from 'node:test';
import assert from 'node:assert/strict';
import {custodyReview} from '../dist/gas-custody-review.js';

const details={willChange:true,effects:{acknowledgementOutsideSource:false,performedQuantity:'270.000000000000000001',quantitySpecification:'synthetic_native_quantity',custodyBefore:'720',custodyAfter:'449.999999999999999999',reservedBefore:'720',reservedAfter:'449.999999999999999999',ownedQuantityChange:'0',feeAmount:'0',currency:'usd',effectiveAt:'2026-11-02T15:00:00Z',returnAt:'2026-11-04T15:00:00Z',feeDueAt:'2026-11-05T15:00:00Z',titleRetainedByCustomer:true,physicalActivityUnchanged:true,feeCashUnchanged:true,allocations:[{lotId:'synthetic-owned-lot',reservationId:'synthetic-original-reservation',quantity:'270.000000000000000001',remainderReservationId:'synthetic-remainder-reservation',remainderQuantity:'449.999999999999999999'}]}};
test('custody review keeps exact independently calculated release and remainder rows, without a new fee or title transfer',()=>{
 const before=structuredClone(details),review=custodyReview(details,at=>'Local '+at),facts=Object.fromEntries(review.facts);
 assert.equal(facts['Performed custody quantity'],'270.000000000000000001');assert.equal(facts['Custody after'],'449.999999999999999999');assert.equal(facts['Reserved after'],'449.999999999999999999');assert.equal(facts['New signed fee cash'],'0');assert.equal(facts['Existing fee cash'],'Unchanged');
 assert.deepEqual(review.tables[0].rows,[['synthetic-owned-lot','synthetic-original-reservation','270.000000000000000001','synthetic-remainder-reservation','449.999999999999999999']]);assert.deepEqual(details,before);
});
test('an older custody acknowledgement displays current balances and the original acknowledged observation with no new allocation',()=>{
 const acknowledged={willChange:false,effects:{...details.effects,performedQuantity:'0',acknowledgedQuantity:'720',custodyBefore:'450',custodyAfter:'450',reservedBefore:'450',reservedAfter:'450',allocations:[],feeDueAt:null}};
 const review=custodyReview(acknowledged,at=>at),facts=Object.fromEntries(review.facts);
 assert.match(facts.Effect,/acknowledged.*no new effect/);assert.equal(facts['Acknowledged custody quantity'],'720');assert.equal(facts['Custody after'],'450');assert.equal(facts['Fee due'],'None');assert.deepEqual(review.tables[0].rows,[]);
});
test('incomplete or lossy custody, ownership, cash and allocation review fields never become executable display data',()=>{
 for(const edit of [d=>delete d.willChange,d=>delete d.effects.feeDueAt,d=>d.effects.custodyAfter=450,d=>d.effects.feeAmount=0,d=>d.effects.titleRetainedByCustomer=false,d=>delete d.effects.physicalActivityUnchanged,d=>d.effects.allocations[0].quantity=270,d=>delete d.effects.allocations[0].remainderReservationId,d=>{d.willChange=false;}]){
  const changed=structuredClone(details);edit(changed);assert.throws(()=>custodyReview(changed,at=>at),/complete custody.*review/);
 }
});


test('an authenticated acknowledgement beyond the selected knowledge cut leaves its pinned balances and explains that view boundary',()=>{
 const outside={willChange:false,effects:{...details.effects,acknowledgementOutsideSource:true,effectiveAt:'2026-11-03T15:00:00Z',performedQuantity:'0',acknowledgedQuantity:'270.000000000000000001',custodyBefore:'720',custodyAfter:'720',reservedBefore:'720',reservedAfter:'720',feeAmount:'0',allocations:[]}};
 const reviewed=custodyReview(outside,at=>at),facts=Object.fromEntries(reviewed.facts);
 assert.equal(facts['Custody before'],'720');assert.equal(facts['Custody after'],'720');assert.equal(facts['New signed fee cash'],'0');assert.equal(facts['Observed custody time'],'2026-11-03T15:00:00Z');assert.deepEqual(reviewed.tables[0].rows,[]);
 assert.match(facts['Selected view'],/Existing fact acknowledged beyond this selected business or knowledge view; this view was not refreshed\./);
 const missing=structuredClone(outside);delete missing.effects.acknowledgementOutsideSource;assert.throws(()=>custodyReview(missing,at=>at),/complete custody/);
 const contradictory=structuredClone(outside);contradictory.willChange=true;assert.throws(()=>custodyReview(contradictory,at=>at),/complete custody/);
});
