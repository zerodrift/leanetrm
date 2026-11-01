import test from 'node:test';
import assert from 'node:assert/strict';
import {crudeOperationBase,crudeReceipt,crudeDispatch,crudeInvoice,crudePayment} from '../dist/crude-operations-model.js';
const fields={profile:'physical_crude_receipt_cash_v1',businessKey:'receipt-1',effectiveAt:'2030-01-04T12:00:00.123456Z',evidence:'Custody certificate 1'};
const base=()=>crudeOperationBase({snapshotId:'snapshot',knowledgeMode:'current'},{tradeId:'trade',revision:1,version:3},fields);
test('receipt keeps exact certified quantity and explicit title and disposition without deriving a sale',()=>{
 const result=crudeReceipt(base(),{quantity:'100.123456',titleTransferred:true,disposition:'owned_inventory'},undefined);
 assert.equal(result.quantity,'100.123456');assert.equal(result.titleTransferred,true);assert.equal(result.disposition,'owned_inventory');assert.equal(result.saleTradeId,null);assert.equal(result.saleRevision,null);
 assert.equal(result.expectedVersion,3);assert.equal(result.effectiveAt,fields.effectiveAt);assert.equal(result.context.convention,'physical_crude_fixed_volume_v1');
 assert.throws(()=>crudeReceipt(base(),{quantity:'100',titleTransferred:false,disposition:'owned_inventory'}),/title/);
 assert.throws(()=>crudeReceipt(base(),{quantity:'100',titleTransferred:true,disposition:''}),/disposition/);
 assert.throws(()=>crudeReceipt(base(),{quantity:'100',titleTransferred:true,disposition:'onward_delivery'}),/sale/);
 const onward=crudeReceipt(base(),{quantity:'100',titleTransferred:true,disposition:'onward_delivery'},{trade_id:'sale',revision:2});assert.equal(onward.saleTradeId,'sale');assert.equal(onward.saleRevision,2);
});
test('lifecycle requires selected profile, effective UTC and evidence; dispatch needs its own title confirmation',()=>{
 for(const change of [{profile:''},{effectiveAt:'2030-01-04T12:00'},{businessKey:''},{evidence:''}])assert.throws(()=>crudeOperationBase({}, {tradeId:'trade',revision:1,version:3},{...fields,...change}));
 assert.throws(()=>crudeDispatch(base(),{receiptId:'receipt',quantity:'25',titleTransferred:false}),/title/);
 assert.deepEqual(Object.keys(crudeDispatch(base(),{receiptId:'receipt',quantity:'25',titleTransferred:true})).slice(-3),['receiptId','quantity','titleTransferred']);
});
test('invoice and partial payment retain explicit component, rounding and cash direction; no client amounts inferred',()=>{
 const invoice=crudeInvoice(base(),{receiptId:'receipt',component:'handling_fee',decimalPlaces:'2',roundingPolicy:'crude_cumulative_receipt_cash_v1'});
 assert.equal(invoice.decimalPlaces,2);assert.equal(invoice.component,'handling_fee');assert.ok(!('amount'in invoice));assert.ok(!('dueAt'in invoice));
 assert.throws(()=>crudeInvoice(base(),{receiptId:'receipt',component:'',decimalPlaces:'2',roundingPolicy:'crude_cumulative_receipt_cash_v1'}));
 assert.throws(()=>crudeInvoice(base(),{receiptId:'receipt',component:'consideration',decimalPlaces:'',roundingPolicy:''}));
 const paid=crudePayment(base(),{invoiceId:'invoice',amount:'10.01',direction:'pay'});assert.equal(paid.amount,'10.01');assert.equal(paid.direction,'pay');
 assert.throws(()=>crudePayment(base(),{invoiceId:'invoice',amount:'10',direction:''}),/direction/);
});
test('accepted lifecycle refresh preserves receipt readback when contractual valuation refuses',async()=>{
 const {refreshAcceptedCrudeOperation}=await import('../dist/crude-operations-model.js');const events=[];
 const failure=Error('Delivery has started');
 const result=await refreshAcceptedCrudeOperation(async()=>{events.push('valuation');throw failure;},async()=>{events.push('ledger');});
 assert.equal(result,failure);assert.deepEqual(events,['valuation','ledger']);
 await assert.rejects(refreshAcceptedCrudeOperation(async()=>{},async()=>{throw Error('Ledger inaccessible');}),/Ledger inaccessible/);
});
test('facts-based lifecycle opens the exact position mode before mounting and revealing its form',async()=>{
 const {openCrudeOperationPanel}=await import('../dist/crude-operations-model.js');const events=[];
 const selected={tradeId:'trade',bindingId:'instrument',revision:2};
 await openCrudeOperationPanel(selected,async mode=>{events.push(mode);return true;},async()=>{events.push('mount');return true;},()=>true,()=>events.push('reveal'));
 assert.deepEqual(events,[{kind:'position',...selected},'mount','reveal']);
 let current=true;const stale=[];await openCrudeOperationPanel(selected,async()=>{current=false;return true;},async()=>{stale.push('mount');return true;},()=>current,()=>stale.push('reveal'));assert.deepEqual(stale,[]);
});
test('record corrections require explicit policy and native eligibility and never invent a refund cash leg',async()=>{
 const {crudeRecordCorrection}=await import('../dist/crude-operations-model.js');
 const payment={id:'payment',canReverse:true};
 assert.throws(()=>crudeRecordCorrection(base(),'payment','',payment),/policy/);
 assert.throws(()=>crudeRecordCorrection(base(),'payment','crude_record_correction_v1',{...payment,canReverse:false}),/eligible/);
 assert.throws(()=>crudeRecordCorrection(base(),'invoice','crude_record_correction_v1',undefined),/eligible/);
 const reversed=crudeRecordCorrection(base(),'payment','crude_record_correction_v1',payment);
 assert.equal(reversed.paymentId,'payment');assert.equal(reversed.reversalPolicy,'crude_record_correction_v1');
 assert.ok(!('amount' in reversed));assert.ok(!('direction' in reversed));assert.ok(!('invoiceId' in reversed));
 const voided=crudeRecordCorrection(base(),'invoice','crude_record_correction_v1',{id:'invoice',canVoid:true});
 assert.equal(voided.invoiceId,'invoice');assert.ok(!('paymentId' in voided));
});
