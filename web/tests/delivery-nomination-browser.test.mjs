import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
const browser={skip:!browserAvailable,timeout:browserTestTimeout};
const setup=`(async()=>{
 window.calls=[];window.errors=[];window.actor='synthetic-scheduler';window.financial={snapshotId:'synthetic-explicit-close',convention:'physical_gas_fixed_quantity_v4',knowledgeMode:'current'};
 window.source={profile:'complete_allocation_nomination_batch_v1',allocationPlanId:'synthetic-allocation-plan',channels:[{value:'portal',label:'Operator portal'}],entries:[{obligationId:'synthetic-obligation',tradeId:'synthetic-trade',expectedRevision:3,day:'2032-03-15',quantity:'100/3',label:'Synthetic purchase',accounts:[{id:'synthetic-account',versionId:'synthetic-account-version',label:'Synthetic service agreement',expectedPreviousId:null}]}]};
 window.api=async(path,body)=>{calls.push({path,body:structuredClone(body)});if(path==='time/inspect')return{candidates:[{instant:body.localTime+(body.localTime.length===16?':00':'')+'Z',offset:'+00:00'}]};
  if(path.endsWith('/preview'))return{operationId:body.operationId,ownerId:body.ownerId,allocationPlanId:body.input.allocationPlanId,requestedInput:body.input,input:window.wrongPreview?{...body.input,allocationPlanId:'another-plan'}:body.input,previewHash:'synthetic-hash',overviewSections:[{label:'Submitted requests',values:[{label:'Transmission',value:'Manually recorded external submission'}]}],tables:[{label:'Nomination requests',columns:['Trade','Quantity'],rows:[['Synthetic purchase','100/3']]}]};
  if(path.endsWith('/record')){window.original=structuredClone(body);if(window.lost)throw new TypeError('Synthetic lost nomination reply');return{found:true,ownerId:body.ownerId,operationId:body.operationId,allocationPlanId:body.input.allocationPlanId,input:body.input,previewHash:body.previewHash,outcome:{changed:true}};}
  if(path.endsWith('/status'))return{found:true,ownerId:original.ownerId,operationId:original.operationId,allocationPlanId:original.input.allocationPlanId,input:original.input,previewHash:original.previewHash,outcome:{changed:true}};throw Error('Unexpected nomination request '+path);
 };
 window.until=async test=>{for(let i=0;i<300&&!test();i++)await new Promise(resolve=>setTimeout(resolve,10));if(!test())throw Error('Nomination controls unavailable '+JSON.stringify(errors));};
 window.create=(await import('/delivery-nomination-batch.js')).setupDeliveryNominationBatch;
 window.mount=()=>window.batch=create(document.querySelector('#host'),{api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=> 'UTC',identity:()=>actor,permission:()=>true,financialContext:()=>financial,changed(){},recorded:async()=>{}});
 mount();batch.show({ownerId:'synthetic-owner',nominationBatch:source});
 window.fill=()=>{for(const [name,value]of Object.entries({account_0:'synthetic-account',reference_0:'synthetic-new-request',submittedAt:'2032-03-14T10:00:00',channel:'portal',reason:'Synthetic planned fulfillment',evidence:'Synthetic operator portal receipt'})){const input=document.querySelector('[name='+name+']');input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));}};
})()`;

test('nomination batch requires a selected financial context and preserves exact allocated quantities and source choices',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);await evaluate("financial=null;batch.sync()");
 assert.equal(await evaluate("document.querySelector('[data-delivery-nominations=review]').disabled"),true);
 assert.match(await evaluate("document.querySelector('[data-delivery-nominations=message]').textContent"),/close|context/i);
 await evaluate("(async()=>{financial={snapshotId:'synthetic-explicit-close',convention:'physical_gas_fixed_quantity_v4',knowledgeMode:'current'};batch.sync();fill();document.querySelector('[data-delivery-nominations=form]').requestSubmit();await until(()=>!!document.querySelector('[data-delivery-nominations=record]'));})()");
 const preview=await evaluate("calls.find(call=>call.path.endsWith('/preview')).body");
 assert.equal(preview.input.entries[0].details.quantity,'100/3');
 assert.deepEqual(preview.input.entries[0].details,{accountId:'synthetic-account',serviceVersionId:'synthetic-account-version',expectedPreviousId:null,externalReference:'synthetic-new-request',quantity:'100/3',submittedAt:'2032-03-14T10:00:00Z',channel:'portal',reason:'Synthetic planned fulfillment',evidence:'Synthetic operator portal receipt'});
 assert.equal(preview.input.context.snapshotId,'synthetic-explicit-close');
 assert.match(await evaluate("document.querySelector('[data-delivery-nominations=review-host]').textContent"),/Manually recorded external submission/);
}));

test('a changed nomination source cannot be accepted as the original reviewed batch',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);await evaluate("(async()=>{fill();wrongPreview=true;document.querySelector('[data-delivery-nominations=form]').requestSubmit();await until(()=>errors.length>0);})()");
 assert.match(await evaluate('errors.join()'),/exact|original/i);
 assert.equal(await evaluate("document.querySelector('[data-delivery-nominations=record]')"),null);
 assert.equal(await evaluate("calls.some(call=>call.path.endsWith('/record'))"),false);
}));

test('unknown nomination outcome restores original inputs and status identity without requiring a new source or close',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);await evaluate("(async()=>{fill();document.querySelector('[data-delivery-nominations=form]').requestSubmit();await until(()=>!!document.querySelector('[data-delivery-nominations=record]'));lost=true;document.querySelector('[data-delivery-nominations=record]').click();await until(()=>batch.unconfirmed());document.querySelector('#host').replaceChildren();mount();financial=null;batch.show(null);batch.sync();document.querySelector('[data-delivery-nominations=status]').click();await until(()=>!batch.unconfirmed());})()");
 const original=await evaluate('original'),status=await evaluate("calls.find(call=>call.path.endsWith('/status')).body");
 assert.deepEqual(status,{ownerId:original.ownerId,operationId:original.operationId});
 assert.equal(await evaluate("calls.filter(call=>call.path.endsWith('/record')).length"),1);
 assert.match(await evaluate("document.querySelector('[data-delivery-nominations=message]').textContent"),/recorded|confirmed/i);
}));
