import test from 'node:test';
import assert from 'node:assert/strict';
import {crudeRequest,restoreCrudeCommand,crudePreview,crudeClockHint} from '../dist/physical-crude-model.js';
import {prepareAmountDisplay,selectDisplayPrecision,initialDisplayPrecision} from '../dist/amount-display.js';
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


test('a selected maintained delivery setup is sent exactly and imported requests keep their original shape',()=>{
 const mapped=crudeRequest({},header,{...assignment,crudeSetupId:'exact-setup-version'},profile,fields);
 assert.equal(mapped.crudeSetupId,'exact-setup-version');
 assert.equal('crudeSetupId' in crudeRequest({},header,assignment,profile,fields),false);
});
test('a crude preview names its pricing source, profile, directions and sides, and shows amounts at the display precision',async()=>{
 selectDisplayPrecision(initialDisplayPrecision());
 const value={presentValue:'292500',currency:'USD',quantity:'50000',quantityUnit:'bbl',deliveryFrom:'2026-12-01T00:00:00Z',deliveryTo:'2027-01-01T00:00:00Z',
  pricingSource:'Synthetic crude monthly curve',priceUnit:'USD / bbl',profileName:'Fixed standard volume at 60°F · zero tolerance · undiscounted',
  sides:[{role:'delivery',direction:'receive',presentValue:'3505000'},{role:'consideration',direction:'pay',presentValue:'-3212500'}]};
 await prepareAmountDisplay(value,async body=>body.map(r=>({'292500':'292,500.000','50000':'50,000','3505000':'3,505,000.000','-3212500':'-3,212,500.000'})[r.value]));
 const terms={assignmentCode:'cs_07e77acf',direction:'receive',considerationDirection:'pay',fixedPrice:'64.25',paymentAt:'2027-01-20T18:00:00Z',handlingFee:null,profile:'physical_crude_standard_volume_v1',tolerance:'0'};
 const shown=crudePreview(value,terms,'America/Chicago');
 assert.equal(shown.total,'Contractual present value: 292,500.000 USD');
 assert.deepEqual(shown.terms,[['Delivery window','Nov 30, 2026, 6:00 PM CST to Dec 31, 2026, 6:00 PM CST · exclusive'],['Contract volume','50,000 bbl'],
  ['Pricing source','Synthetic crude monthly curve'],['Physical direction','Receive crude'],['Consideration direction','Pay consideration'],['Fixed price','64.25 USD / bbl'],
  ['Payment · America/Chicago','Jan 20, 2027, 12:00 PM CST'],['Handling fee','None'],['Profile','Fixed standard volume at 60°F · zero tolerance · undiscounted']]);
 assert.deepEqual(shown.columns,['Side','Direction','Signed PV · USD']);
 assert.deepEqual(crudePreview(value,{...terms,handlingFee:'0.15',handlingFeeDirection:'receive'},'UTC').terms.find(([name])=>name==='Handling fee'),['Handling fee','0.15 USD / bbl · Receive fee']);
 assert.deepEqual(shown.sides,[['Delivery','Receive','3,505,000.000'],['Consideration','Pay','-3,212,500.000']]);
});
test('a delivery clock unlike the entry time zone is named where delivery times are entered',()=>{
 assert.equal(crudeClockHint('UTC','America/Chicago'),'Delivery months start at midnight on the UTC delivery clock. Enter times here in America/Chicago.');
 assert.equal(crudeClockHint('America/Chicago','America/Chicago'),'');
 assert.equal(crudeClockHint(undefined,'America/Chicago'),'');
});
