import test from 'node:test';
import assert from 'node:assert/strict';
import {lifecycleRequest,lifecycleKey,lifecycleRows,lifecycleReason,restoreLifecycleRequest} from '../dist/lifecycle-model.js';
import {pageGroup,pageAvailable} from '../dist/navigation-groups.js';
import {pageReport,webPage,lookingAt} from '../dist/assistant-pages.js';

const context={contextId:'10000000-0000-0000-0000-000000000001',asOf:'2026-11-02T01:30:00Z',businessDate:'2026-11-01',snapshotId:'20000000-0000-0000-0000-000000000001'};
const offer={target:{handler:'listed_option',scopeId:'30000000-0000-0000-0000-000000000001',bindingId:'40000000-0000-0000-0000-000000000001'},action:'option.exercise',label:'Exercise',occurrenceId:'50000000-0000-0000-0000-000000000001',at:context.asOf,before:null,eligible:true,reason:null,revision:1,stateVersion:2,subject:{reference:'TR-000001',product:'Synthetic gas call',bookId:'60000000-0000-0000-0000-000000000001',book:'Synthetic book'},policyId:'70000000-0000-0000-0000-000000000001',policyRevision:3,selection:{evidenceId:'00000000-0000-0000-0000-000000000000',lots:null,expectedEventVersion:2,profileRevision:4},requiredInputs:['evidenceId','lots'],availableQuantity:'2'};
test('Lifecycle has one Scheduling destination for authorized readers and an exact assistant page',()=>{
 assert.equal(pageGroup('lifecycle'),'Scheduling');assert.equal(pageAvailable('lifecycle',['ReadMarketData','ReadTrades']),true);assert.equal(pageAvailable('lifecycle',['ReadMarketData']),false);
 assert.deepEqual(pageReport({page:'lifecycle'}),{page:'lifecycle',selection:{}});assert.equal(webPage('lifecycle'),'lifecycle');
});
test('review carries the certified business date and explicit observed terms without modifying the native offer',()=>{
 const request=lifecycleRequest(context,offer,{policyId:offer.policyId,policyRevision:3,evidenceId:'80000000-0000-0000-0000-000000000001',lots:'1.25'},'90000000-0000-0000-0000-000000000001');
 assert.deepEqual(request.context,context);assert.equal(request.automatic,false);assert.equal(request.selection.lots,'1.25');assert.equal(request.selection.expectedEventVersion,2);assert.equal(request.previewHash,'0'.repeat(64));
 request.context.businessDate='2030-01-01';assert.equal(context.businessDate,'2026-11-01');assert.equal(offer.selection.lots,null);
});
test('review requires a selected policy and every observed input, and refuses unsupported input contracts',()=>{
 const draft={policyId:offer.policyId,policyRevision:3,evidenceId:'80000000-0000-0000-0000-000000000001',lots:'1'};
 for(const replacement of [{policyId:''},{policyRevision:0},{evidenceId:''},{lots:''}])assert.throws(()=>lifecycleRequest(context,offer,{...draft,...replacement},'request'),/Choose|Enter/);
 assert.throws(()=>lifecycleRequest(context,{...offer,selection:null},draft,'request'),/Configure/);
 assert.throws(()=>lifecycleRequest(context,{...offer,requiredInputs:['unknownFinancialTerm']},draft,'request'),/supported/);
});
test('inactive contracts retain a readable reason and distinct occurrence identity in the grid',()=>{
 const inactive={...offer,eligible:false,reason:'option_holding_closed'};
 assert.equal(lifecycleRows([inactive],at=>at)[0][5],'Closed');
 assert.match(lifecycleReason(inactive.reason),/closed/i);
 assert.notEqual(lifecycleKey(offer),lifecycleKey({...offer,occurrenceId:'different'}));
});
test('independent swap memberships sharing one payment template retain distinct saved-profile identities',()=>{
 const shared={...offer,target:{...offer.target,handler:'financial_swap'},action:'swap.determine',occurrenceId:'shared-side-period'};
 const first={...shared,profileId:'first-membership-profile'},second={...shared,profileId:'second-membership-profile'};
 assert.notEqual(lifecycleKey(first),lifecycleKey(second));
 assert.equal(lifecycleKey(first),lifecycleKey(structuredClone(first)));
 assert.notEqual(lifecycleKey(first),lifecycleKey({...first,profileId:second.profileId}));
});
test('uncertain lifecycle commands restore the exact source, hash, selection and identity while rejecting reads and arbitrary writes',()=>{
 const request=lifecycleRequest(context,offer,{policyId:offer.policyId,policyRevision:3,evidenceId:'80000000-0000-0000-0000-000000000001',lots:'1'},'90000000-0000-0000-0000-000000000001');request.previewHash='a'.repeat(64);
 const saved={path:'lifecycle/execute',operationId:request.operationId,body:request,uncertain:true,label:'Exercise TR-000001'};
 const restored=restoreLifecycleRequest(JSON.parse(JSON.stringify(saved)));assert.deepEqual(restored,saved);
 restored.body.selection.lots='2';assert.equal(saved.body.selection.lots,'1');
 for(const path of ['lifecycle/preview','lifecycle/discover','admin/change'])assert.equal(restoreLifecycleRequest({...saved,path}),undefined);
 assert.equal(restoreLifecycleRequest({...saved,body:{...request,operationId:'different'}}),undefined);
});

test('swap net review presents explicit gross lines and reverses an exact original without inventing amounts',()=>{
 const net={...offer,target:{...offer.target,handler:'financial_swap_net'},action:'swap.net',selection:{snapshotId:context.snapshotId,profileId:'profile',profileRevision:1,net:{preview:{amount:'',allocations:[],reversesId:null,expectedVersion:2},effectiveAt:'',reference:''}},requiredInputs:['netSettlement','effectiveAt','reference']};
 const draft={policyId:offer.policyId,policyRevision:3,amount:'50',allocations:[{determinationId:'fixed',amount:'-300'},{determinationId:'floating',amount:'350'}],reversesId:null,effectiveAt:context.asOf,reference:'Synthetic observed net'};
 const ordinary=lifecycleRequest(context,net,draft,'new');assert.deepEqual(ordinary.selection.net.preview.allocations,draft.allocations);assert.equal(ordinary.selection.net.preview.amount,'50');assert.equal(ordinary.selection.net.reference,draft.reference);
 const reversal=lifecycleRequest(context,net,{...draft,amount:null,allocations:[],reversesId:'original-payment'},'reverse');assert.equal(reversal.selection.net.preview.reversesId,'original-payment');assert.equal(reversal.selection.net.preview.amount,null);assert.deepEqual(reversal.selection.net.preview.allocations,[]);
 assert.equal(net.selection.net.preview.amount,'');assert.deepEqual(net.selection.net.preview.allocations,[]);
});
test('single swap reversal retains the backend-selected native payment and amount',()=>{
 const payment={...offer,target:{...offer.target,handler:'financial_swap'},action:'swap.payment',selection:{payment:{determinationId:'',amount:'',effectiveAt:'',reference:'',reversesId:null}},requiredInputs:['determinationId','amount','effectiveAt','reference']};
 const result=lifecycleRequest(context,payment,{policyId:offer.policyId,policyRevision:3,determinationId:'native-contribution',amount:'-100',effectiveAt:context.asOf,reference:'Synthetic reversal',reversesId:'native-payment'},'reverse');
 assert.equal(result.selection.payment.reversesId,'native-payment');assert.equal(result.selection.payment.amount,'-100');
});

test('only committed financial-source operations invalidate current valuation inputs',async()=>{
 const {lifecycleChangesFinancialSource}=await import('../dist/lifecycle-model.js');
 for(const path of ['lifecycle/execute','lifecycle/futures/adopt','lifecycle/options/configure','lifecycle/swaps/configure'])assert.equal(lifecycleChangesFinancialSource({path,body:{}}),true);
 for(const action of ['evidence','mark_binding','projection_profile'])assert.equal(lifecycleChangesFinancialSource({path:'lifecycle/gas/configure',body:{action}}),true);
 for(const path of ['lifecycle/policies/save','lifecycle/futures/policies/save','lifecycle/swaps/net/configure','lifecycle/futures/delivery/configure'])assert.equal(lifecycleChangesFinancialSource({path,body:{}}),false);
 for(const action of ['account','bind_policy'])assert.equal(lifecycleChangesFinancialSource({path:'lifecycle/gas/configure',body:{action}}),false);
});
