import test from 'node:test';
import assert from 'node:assert/strict';
import {exerciseStateText,measureLines,lifecycleLines,quantityBasisText,valueMeasureText} from '../dist/lifecycle-text.js';
const instant=at=>'@'+at.slice(0,10);
const measures={unrealized:'1,445',realizedUnsettled:'-10855',total:'-9410',label:'Unrealized + realized unsettled',discounting:'undiscounted',
 expectedCash:[{dueAt:'2026-11-10T00:00:00Z',amount:'-9300'},{dueAt:'2026-12-10T00:00:00Z',amount:'-9648'}]};
const row=patch=>({deliveryFrom:'2026-10-15',paymentAt:'2026-11-10T00:00:00Z',paidAt:null,paid:null,outstanding:null,accrued:null,finalDetermination:null,quantityState:null,
 ...patch,lifecycle:{delivery:'Ended',due:null,cash:null,economicClass:null,coverage:null,unavailableReason:null,fulfillment:null,...patch?.lifecycle}});

test('measures are separate figures with their labelled sum, their discounting basis, and expected cash marked as not added',()=>{
 const lines=measureLines({...measures,unrealized:'1445'},'USD',instant);
 assert.deepEqual(lines.slice(0,3).map(l=>[l.label,l.value]),[['Unrealized (remaining delivery)','1,445 USD'],['Realized, unsettled (delivered, not paid)','-10,855 USD'],['Unrealized + realized unsettled','-9,410 USD']]);
 assert.deepEqual(lines[3],{label:'Discounting',value:'undiscounted'});
 assert.deepEqual(lines.slice(4).map(l=>[l.label,l.value]),[['Expected cash by due date · not added to the total',''],['Due Nov 10, 2026','-9,300 USD'],['Due Dec 10, 2026','-9,648 USD']]);
});

test('cash coverage follows its due date, including an incomplete known zero',()=>{
 const expectedCash=[{dueAt:'2026-11-10T00:00:00Z',amount:'0',unassessedDays:1},measures.expectedCash[1]];
 const lines=measureLines({...measures,unrealized:'1445',expectedCash},'USD',instant);
 assert.deepEqual(lines.slice(-2),[{label:'Due Nov 10, 2026',value:'0 USD · incomplete: 1 unassessed day'},
  {label:'Due Dec 10, 2026',value:'-9,648 USD'}]);
});

test('a measure that is not a decimal is refused, never shown as a guess',()=>{
 assert.throws(()=>measureLines({...measures,total:'n/a'},'USD',instant),/exact number/);
});

test('the quantity basis reads as an estimate with its allocation pending; other states keep their own words',()=>{
 assert.equal(quantityBasisText('contract_estimate_allocation_pending_v1'),'estimated from contract; allocation pending');
 assert.equal(quantityBasisText('Contractual'),'Contractual');
 assert.equal(quantityBasisText(null),'—');
});

test('an unpaid cash row shows its due date and never a payment date; a paid row shows only its recorded payment date',()=>{
 const unpaid=lifecycleLines(row({lifecycle:{cash:'AccruedUnpaid',coverage:'RealizedUnsettled'},accrued:'-300',outstanding:'-300'}),instant);
 assert.ok(unpaid.includes('Due date: Nov 10, 2026')&&unpaid.includes('Cash: Accrued unpaid')&&unpaid.includes('Realized, unsettled (delivered, not paid)')&&!unpaid.some(l=>l.startsWith('Paid on')),unpaid);
 const paid=lifecycleLines(row({lifecycle:{cash:'Paid'},paid:'-290',outstanding:'0',paidAt:'2026-10-10T00:00:00Z'}),instant);
 assert.ok(paid.includes('Paid on @2026-10-10')&&!paid.some(l=>l.startsWith('Due date')),paid);
 const part=lifecycleLines(row({lifecycle:{cash:'PartPaid'},paid:'-100',outstanding:'-190',paidAt:'2026-10-10T00:00:00Z'}),instant);
 assert.ok(part.includes('Paid on @2026-10-10')&&part.includes('Due date: Nov 10, 2026')&&part.includes('Cash: Part paid'),part);
 const remaining=lifecycleLines(row({lifecycle:{delivery:'NotStarted',cash:'Remaining',coverage:'Unrealized'},paidAt:'2026-10-10T00:00:00Z'}),instant);
 assert.ok(remaining.includes('Due date: Nov 10, 2026')&&remaining.includes('Unrealized (remaining delivery)')&&!remaining.some(l=>l.startsWith('Paid on')),remaining);
});

test('a physical in-term row is not cash: settlement is not applicable, never Paid',()=>{
 const lines=lifecycleLines(row({lifecycle:{delivery:'InProgress',cash:'NotApplicable'}}),instant);
 assert.deepEqual(lines,['Settlement: not applicable (physical delivery)']);
});

test('a final determination is shown beside the accrual, which it does not replace',()=>{
 const lines=lifecycleLines(row({lifecycle:{cash:'AccruedUnpaid'},accrued:'-310',outstanding:'-310',finalDetermination:'-303.8'}),instant);
 assert.ok(lines.includes('Accrued: -310')&&lines.includes('Final determination: -303.8 · accrual unchanged'),lines);
});

test('rows without in-term lifecycle fields render as before',()=>{
 const legacy={...row({}),lifecycle:{delivery:'Scheduled',due:'NotDue',cash:'NotTracked',economicClass:null,coverage:'Discounted',unavailableReason:null,fulfillment:null}};
 assert.deepEqual(lifecycleLines(legacy,instant),['Payment: Not due','Cash: Not tracked','Discounted']);
});

test('a legacy in-progress row without a cash state renders exactly as before, never as not applicable',()=>{
 assert.deepEqual(lifecycleLines(row({lifecycle:{delivery:'InProgress',due:'NotDue',coverage:'Discounted'}}),instant),['Payment: Not due','Discounted']);
});

test('the value measure names its discounting basis, or neither; a half-present pair is refused',()=>{
 assert.equal(valueMeasureText('Unrealized + realized unsettled','undiscounted'),'Unrealized + realized unsettled · undiscounted');
 assert.equal(valueMeasureText(undefined,undefined),'Contractual PV');
 assert.throws(()=>valueMeasureText('label',undefined),/come together/);
 assert.throws(()=>valueMeasureText(undefined,'undiscounted'),/come together/);
});


test('selected quantities name their saved basis without an internal version identifier',()=>{
 assert.equal(quantityBasisText('selected_delivery_quantity_v1'),'follows selected delivery quantity');
 assert.equal(quantityBasisText('allocated_final_v1'),'allocated, final');
 assert.equal(quantityBasisText('allocated_provisional_v1'),'allocated, provisional');
 assert.equal(quantityBasisText('scheduled_v1'),'scheduled');
 assert.equal(quantityBasisText('contractual_quantity_v1'),'contract quantity');
});

test('quantity parts use plain labels on the native schedule',()=>{
 assert.equal(quantityBasisText('band_v1'),'part of the day quantity');
 assert.equal(quantityBasisText('shortfall_v1'),'shortfall of the day quantity');
});

test('exercise groups show the recorded outcome or the specific decision still awaited',()=>{
 assert.equal(exerciseStateText('exercised'),'exercised');
 assert.equal(exerciseStateText('abandoned'),'abandoned');
 assert.equal(exerciseStateText('undecided'),'not yet exercised');
 assert.equal(exerciseStateText('undecided_forecast'),'awaiting published price');
});

test('cash schedules show the supplied unrealized present value to come separately from accrued and outstanding, including zero',()=>{
 const partial=lifecycleLines(row({lifecycle:{cash:'PartPaid'},accrued:'-2000',paid:'-1000',outstanding:'-1000',amount:'-6000',remaining:'-3925.75'}),instant);
 assert.deepEqual(partial.filter(line=>/^(Accrued|Paid|Outstanding|To come \(unrealized present value\)):/.test(line)),
  ['Accrued: -2,000','Paid: -1,000','Outstanding: -1,000','To come (unrealized present value): -3,925.75']);
 assert.ok(lifecycleLines(row({remaining:'0'}),instant).includes('To come (unrealized present value): 0'));
 assert.ok(!lifecycleLines(row({remaining:null}),instant).some(line=>line.startsWith('To come')));
});

test('reported balance states never label an unknown accrual as zero or paid',async()=>{
 const {imbalanceStateText,incompleteText}=await import('../dist/lifecycle-text.js');
 assert.equal(imbalanceStateText('assessed'),'');
 assert.equal(imbalanceStateText('unassessed'),'no statement yet');
 assert.equal(imbalanceStateText('not_applicable'),'day not ended');
 assert.equal(imbalanceStateText('unassessed','preceding_day_unavailable'),'preceding day unassessed');
 assert.equal(incompleteText(4),'incomplete: 4 unassessed days');
 assert.equal(incompleteText(0),'');
 const paid=lifecycleLines(row({lifecycle:{cash:'UnassessedPaid'},paid:'900',accrued:null,outstanding:null}),instant);
 assert.ok(paid.includes('Paid: 900')&&!paid.some(text=>text.startsWith('Accrued:')||text.startsWith('Outstanding:')));
});
