import test from 'node:test';
import assert from 'node:assert/strict';
import {measureLines,lifecycleLines,quantityBasisText} from '../dist/lifecycle-text.js';
const instant=at=>'@'+at.slice(0,10);
const measures={remainingDeliveryMtm:'1,445',outstandingSettlement:'-10855',total:'-9410',label:'Remaining contract value + outstanding settlement',discounting:'undiscounted',
 expectedCash:[{dueAt:'2026-11-10T00:00:00Z',amount:'-9300'},{dueAt:'2026-12-10T00:00:00Z',amount:'-9648'}]};
const row=patch=>({deliveryFrom:'2026-10-15',paymentAt:'2026-11-10T00:00:00Z',paidAt:null,paid:null,outstanding:null,accrued:null,finalDetermination:null,quantityState:null,
 ...patch,lifecycle:{delivery:'Ended',due:null,cash:null,economicClass:null,coverage:null,unavailableReason:null,fulfillment:null,...patch?.lifecycle}});

test('measures are separate figures with their labelled sum, their discounting basis, and expected cash marked as not added',()=>{
 const lines=measureLines({...measures,remainingDeliveryMtm:'1445'},'USD',instant);
 assert.deepEqual(lines.slice(0,3).map(l=>[l.label,l.value]),[['Remaining delivery MTM','1,445 USD'],['Outstanding settlement','-10,855 USD'],['Remaining contract value + outstanding settlement','-9,410 USD']]);
 assert.deepEqual(lines[3],{label:'Discounting',value:'undiscounted'});
 assert.deepEqual(lines.slice(4).map(l=>[l.label,l.value]),[['Expected cash by due date · not added to the total',''],['Due @2026-11-10','-9,300 USD'],['Due @2026-12-10','-9,648 USD']]);
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
 const unpaid=lifecycleLines(row({lifecycle:{cash:'AccruedUnpaid',coverage:'OutstandingSettlement'},accrued:'-300',outstanding:'-300'}),instant);
 assert.ok(unpaid.includes('Due date: @2026-11-10')&&unpaid.includes('Cash: Accrued unpaid')&&unpaid.includes('Outstanding settlement')&&!unpaid.some(l=>l.startsWith('Paid on')),unpaid);
 const paid=lifecycleLines(row({lifecycle:{cash:'Paid'},paid:'-290',outstanding:'0',paidAt:'2026-10-10T00:00:00Z'}),instant);
 assert.ok(paid.includes('Paid on @2026-10-10')&&!paid.some(l=>l.startsWith('Due date')),paid);
 const part=lifecycleLines(row({lifecycle:{cash:'PartPaid'},paid:'-100',outstanding:'-190',paidAt:'2026-10-10T00:00:00Z'}),instant);
 assert.ok(part.includes('Paid on @2026-10-10')&&part.includes('Cash: Part paid'),part);
 const remaining=lifecycleLines(row({lifecycle:{delivery:'NotStarted',cash:'Remaining',coverage:'RemainingDeliveryMtm'},paidAt:'2026-10-10T00:00:00Z'}),instant);
 assert.ok(remaining.includes('Due date: @2026-11-10')&&remaining.includes('Remaining delivery MTM')&&!remaining.some(l=>l.startsWith('Paid on')),remaining);
});

test('a physical in-term row is not cash: settlement is not applicable, never Paid',()=>{
 const lines=lifecycleLines(row({lifecycle:{delivery:'InProgress'}}),instant);
 assert.ok(lines.includes('Settlement: not applicable (physical delivery)')&&!lines.some(l=>/Paid|Due date/.test(l)),lines);
});

test('a final determination is shown beside the accrual, which it does not replace',()=>{
 const lines=lifecycleLines(row({lifecycle:{cash:'AccruedUnpaid'},accrued:'-310',outstanding:'-310',finalDetermination:'-303.8'}),instant);
 assert.ok(lines.includes('Accrued: -310')&&lines.includes('Final determination: -303.8 · accrual unchanged'),lines);
});

test('rows without in-term lifecycle fields render as before',()=>{
 const legacy={...row({}),lifecycle:{delivery:'Scheduled',due:'NotDue',cash:'NotTracked',economicClass:null,coverage:'Discounted',unavailableReason:null,fulfillment:null}};
 assert.deepEqual(lifecycleLines(legacy,instant),['Payment: Not due','Cash: Not tracked','Discounted']);
});
