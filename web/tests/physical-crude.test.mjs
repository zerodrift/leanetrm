import test from 'node:test';
import assert from 'node:assert/strict';
import {crudeRequest,restoreCrudeCommand} from '../dist/physical-crude-model.js';
import {PendingCommand} from '../dist/pending-command.js';
const assignment={code:'synthetic_assignment',definitionRevisionId:'exact-definition'};
const profile={code:'physical_crude_standard_volume_v1',quantityBasis:'standard_volume_total',tolerance:'0',discounting:false};
const fields={deliveryFrom:'2027-01-04T09:00:00Z',deliveryTo:'2027-01-05T09:00:00Z',quantity:'1000.125',direction:'receive',fixedPrice:'70.25',considerationDirection:'receive',paymentAt:'2027-02-01T12:00:00Z',feeMode:'none',handlingFee:'99',handlingFeeDirection:'pay'};
const header={bookId:'book',legalEntityId:'entity',legalEntityRevision:3,counterpartyId:'party',counterpartyRevision:4};
test('crude request retains references, decimal strings, independent cash direction and explicit selected profile',()=>{
 const r=crudeRequest({snapshotId:'snapshot'},header,assignment,profile,fields);
 assert.equal(r.definitionRevisionId,'exact-definition');assert.equal(r.assignmentCode,'synthetic_assignment');assert.equal(r.quantity,'1000.125');
 assert.equal(r.direction,'receive');assert.equal(r.considerationDirection,'receive');assert.equal(r.deliveryFrom,'2027-01-04T09:00:00Z');assert.equal(r.deliveryTo,'2027-01-05T09:00:00Z');
 assert.equal(r.tolerance,'0');assert.equal(r.discounting,false);assert.equal(r.handlingFee,null);assert.equal(r.handlingFeeDirection,null);assert.equal(r.counterpartyRevision,4);
 assert.ok(!('schedule'in r));assert.ok(!('presentValue'in r));
});
test('missing profile, assignment and ambiguous fee choice refuse instead of defaulting economics',()=>{
 assert.throws(()=>crudeRequest({},header,assignment,undefined,fields),/profile/);
 assert.throws(()=>crudeRequest({},header,undefined,profile,fields),/delivery/);
 assert.throws(()=>crudeRequest({},header,assignment,profile,{...fields,feeMode:''}),/fee/);
 const r=crudeRequest({},header,assignment,profile,{...fields,feeMode:'per_unit',handlingFee:'0.125',handlingFeeDirection:'receive'});
 assert.equal(r.handlingFee,'0.125');assert.equal(r.handlingFeeDirection,'receive');
});
test('uncertain crude request restores original identity and body; denied retry cannot create a fresh booking',()=>{
 const p=new PendingCommand(c=>c==='access_denied');p.begin('crude/book',{quantity:'1000'},()=> 'original');p.sending();
 const restored=restoreCrudeCommand(JSON.parse(JSON.stringify(p.command)));assert.deepEqual(restored,{...p.command,uncertain:true});
 p.command=restored;const previous=p.sending();p.failed('access_denied',previous);assert.equal(p.command.operationId,'original');assert.equal(p.command.body.quantity,'1000');
 assert.equal(restoreCrudeCommand({...restored,path:'swaps/book'}),undefined);
});

test('wall-clock input cannot be silently interpreted as UTC',()=>{assert.throws(()=>crudeRequest({},header,assignment,profile,{...fields,paymentAt:'2027-02-01T12:00'}),/Resolve/);});

test('managed UTC offset notation retains its exact resolved instant and fractional precision',()=>{const r=crudeRequest({},header,assignment,profile,{...fields,paymentAt:'2027-02-01T12:00:00.1234567+00:00'});assert.equal(r.paymentAt,'2027-02-01T12:00:00.1234567Z');});
