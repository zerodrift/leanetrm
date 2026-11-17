import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const browser={skip:!browserAvailable,timeout:browserTestTimeout};
const setup=`(async()=>{
 window.calls=[];window.errors=[];window.actor='synthetic-capacity-operator';window.zone='UTC';window.rights=['ReadGasOperations','ConfigureGasServices','RecordGasOperations'];
 window.context={effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'};
 const resolved={contextId:'synthetic-capacity-context',effectiveOn:'2032-03-14',knownAt:'2032-03-14T20:00:00Z'};
 const text=(name,label)=>({name,label,kind:'text',nullable:false}),instant=(name,label)=>({name,label,kind:'instant',nullable:false});
 const base=kind=>({kind,entitlementId:'synthetic-entitlement',expectedStateVersion:9,expectedPreviousId:null});
 const define={label:'Publish capacity revision',permission:'ConfigureGasServices',base:base('define'),fields:[text('evidence','Capacity evidence')]};
 const plan={label:'Review chosen capacity claims',permission:'RecordGasOperations',base:{...base('plan'),versionId:'synthetic-capacity-version',admissionPolicy:'complete_feasible_replacement_v1'},fields:[text('businessKey','Plan reference'),{name:'previous',label:'Previous plan revision',kind:'reference',nullable:true,members:['expectedPreviousId'],choices:[]},{name:'claims',label:'Complete chosen claims',kind:'array',nullable:false,minItems:0,fields:[{name:'path',label:'Exact eligible path',kind:'reference',nullable:false,members:['pathId'],choices:[{id:'path-a',label:'Synthetic pipeline / receipt A / delivery B',value:{pathId:'synthetic-path'}}]},instant('receiptFrom','Receipt starts'),instant('receiptTo','Receipt ends'),text('receiptQuantity','Gross receipt quantity'),instant('deliveryFrom','Delivery starts'),instant('deliveryTo','Delivery ends'),text('deliveryQuantity','Gross delivery quantity')]},text('evidence','Chosen-plan evidence')]};
 window.view={ownerId:'opaque-capacity-owner',context:resolved,at:'2032-03-14T23:59:59.999999Z',entitlementId:'synthetic-entitlement',entitlements:[{id:'synthetic-entitlement',label:'Synthetic transport entitlement',revision:2}],historical:false,canConfigure:true,canRecord:true,configuration:{define},forms:{plan},overviewSections:[{label:'Commercial capacity',values:[{label:'Assessment',value:'Feasible'},{label:'Physical transport',value:'Not recorded'}]}],tables:[{label:'Complete capacity assessment',columns:['From','Through','Exact used rate','Maximum rate','Evidence'],instantColumns:[0,1],rows:[['2032-03-14T08:00:00Z','2032-03-14T09:00:00Z','100 / 3','50','2032-03-14T08:00:00Z']]}]};
 window.api=async(path,body)=>{calls.push({path,body:structuredClone(body)});
  if(path==='time/inspect')return{candidates:[{instant:body.localTime+(body.localTime.length===16?':00':'')+'Z',offset:'+00:00'}]};
  if(path==='gas/transport/capacity/directory'){if(window.denied)throw Object.assign(Error('Synthetic capacity read denied'),{code:'access_denied'});return{...view,forms:{},configuration:{define:{...define,base:{...define.base,entitlementId:null,expectedStateVersion:0}}}};}
  if(path==='gas/transport/capacity/read')return{...view,entitlementId:window.wrongRead?'another-entitlement':body.entitlementId};
  if(path==='gas/transport/capacity/preview'){if(window.futureSource)throw Object.assign(Error('Selected capacity source is future-effective for this business date.'),{code:'gas_transport_capacity_future_source'});const entitlementId=window.wrongPreview?'another-entitlement':body.command.entitlementId??'synthetic-new-entitlement';return{...view,operationId:body.operationId,entitlementId,kind:body.command.kind,effectiveAt:body.effectiveAt,command:{...body.command,entitlementId,claims:body.command.claims?.map((row,index)=>({...row,claimId:'synthetic-canonical-claim-'+index}))}};}
  if(path==='gas/transport/capacity/record'){window.original=structuredClone(body);if(window.lost)throw new TypeError('Synthetic lost capacity acknowledgement');return{operationId:body.operationId,entitlementId:body.command.entitlementId,kind:body.command.kind,outcome:{revisionId:'synthetic-plan-revision',revision:1,changed:true}};}
  if(path==='gas/transport/capacity/status')return window.absent?{found:false}:{operationId:original.operationId,entitlementId:original.command.entitlementId,kind:original.command.kind,effectiveAt:original.effectiveAt,command:window.wrongReceipt?{...original.command,evidence:'Different plan'}:original.command,outcome:{revisionId:'synthetic-plan-revision',revision:1,changed:true}};
  throw Error('Unexpected capacity call '+path);
 };
 window.until=async test=>{for(let i=0;i<300&&!test();i++)await new Promise(resolve=>setTimeout(resolve,10));if(!test())throw Error('Capacity controls not ready '+JSON.stringify(errors));};
 window.mount=()=>window.capacity=(window.createCapacity)(document.querySelector('#host'),{api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=>zone,identity:()=>actor,context:()=>context,permission:name=>rights.includes(name),changed(){}});
 window.createCapacity=(await import('/transport-capacity-view.js')).setupTransportCapacity;mount();await capacity.load('synthetic-entitlement');
 window.pick=(name,value)=>{const input=document.querySelector('[name='+name+']');input.value=value;input.dispatchEvent(new Event('change',{bubbles:true}));};
 window.plan=()=>{document.querySelector('[data-transport-command=plan]').click();pick('businessKey','synthetic-chosen-plan');pick('previous','@null');pick('claims','empty');pick('evidence','Synthetic complete chosen plan');pick('operationEffectiveAt','2032-03-14T10:00:00');};
})()`;

test('capacity controls separate configuration and recording rights, with explicit empty-plan release',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 assert.match(await evaluate("document.querySelector('[data-transport-capacity=actions]').textContent"),/Entitlement definitions.*Chosen capacity claims/s);
 assert.equal(await evaluate("document.querySelector('[data-transport-capacity=source-search]')"),null);
 await evaluate("rights=['ReadGasOperations','RecordGasOperations'];capacity.sync()");
 assert.equal(await evaluate("document.querySelector('[data-transport-command=define]').disabled"),true);
 assert.equal(await evaluate("document.querySelector('[data-transport-command=plan]').disabled"),false);
 await evaluate("rights=['ReadGasOperations','ConfigureGasServices'];capacity.sync()");
 assert.equal(await evaluate("document.querySelector('[data-transport-command=define]').disabled"),false);
 assert.equal(await evaluate("document.querySelector('[data-transport-command=plan]').disabled"),true);
 await evaluate("rights=['ReadGasOperations','RecordGasOperations'];capacity.sync()");
 await evaluate("plan()");assert.equal(await evaluate("document.querySelector('[name=claims]').value"),'empty');
 await evaluate("(async()=>{document.querySelector('[data-transport-capacity=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-transport-capacity=record]'));document.querySelector('[data-transport-capacity=record]').click();await until(()=>calls.some(call=>call.path.endsWith('/record')));})()");
 const recorded=await evaluate("calls.find(call=>call.path.endsWith('/record')).body");assert.deepEqual(recorded.command.claims,[]);assert.equal(recorded.command.expectedPreviousId,null);assert.equal(recorded.command.versionId,'synthetic-capacity-version');assert.equal(recorded.command.admissionPolicy,'complete_feasible_replacement_v1');
 assert.equal(await evaluate("calls.every(call=>call.path.startsWith('gas/transport/capacity/')||call.path==='time/inspect')"),true);
}));

test('a described explicit release keeps its pinned policy and chosen plan while other backend claims remain breached',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{view.overviewSections=[{label:'Other chosen claims',values:[{label:'Retained breach',value:'Other plan remains 60 against capacity 50'}]}];view.forms.release={label:'Release only the selected plan claims',permission:'RecordGasOperations',base:{kind:'plan',entitlementId:'synthetic-entitlement',expectedStateVersion:9,versionId:'synthetic-capacity-version',admissionPolicy:'explicit_empty_release_v1',claims:[]},fields:[{name:'plan',label:'Current plan to release',kind:'reference',nullable:false,members:['businessKey','expectedPreviousId'],choices:[{id:'plan-a',label:'Synthetic plan A revision3',value:{businessKey:'synthetic-plan-a',expectedPreviousId:'synthetic-plan-a-revision3'}}]},{name:'evidence',label:'Release evidence',kind:'text',nullable:false}]};await capacity.load('synthetic-entitlement');document.querySelector('[data-transport-command=release]').click();})()");
 assert.equal(await evaluate("document.querySelector('[name=plan]').value"),'');
 assert.equal(await evaluate("document.querySelector('[name=claims]')"),null);
 await evaluate("(async()=>{pick('plan','plan-a');pick('evidence','Synthetic explicit release');pick('operationEffectiveAt','2032-03-14T10:00:00');document.querySelector('[data-transport-capacity=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-transport-capacity=record]'));})()");
 assert.match(await evaluate("document.querySelector('[data-transport-capacity=review]').textContent"),/Other plan remains 60 against capacity 50/);
 await evaluate("(async()=>{document.querySelector('[data-transport-capacity=record]').click();await until(()=>calls.some(call=>call.path.endsWith('/record')));})()");
 const body=await evaluate("calls.find(call=>call.path.endsWith('/record')).body");
 assert.deepEqual(body.command,{kind:'plan',entitlementId:'synthetic-entitlement',expectedStateVersion:9,versionId:'synthetic-capacity-version',admissionPolicy:'explicit_empty_release_v1',claims:[],businessKey:'synthetic-plan-a',expectedPreviousId:'synthetic-plan-a-revision3',evidence:'Synthetic explicit release'});
}));

test('capacity claims retain distinct gross stage quantities and clocks and submit only the canonical reviewed command',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{plan();pick('claims','entries');[...document.querySelectorAll('#host button')].find(button=>button.textContent==='Add entry').click();for(const[name,value]of Object.entries({path:'path-a',receiptFrom:'2032-03-14T08:00:00',receiptTo:'2032-03-14T09:00:00',receiptQuantity:'100.000000000000000001',deliveryFrom:'2032-03-14T10:00:00',deliveryTo:'2032-03-14T12:00:00',deliveryQuantity:'85'}))pick(name,value);document.querySelector('[data-transport-capacity=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-transport-capacity=record]'));document.querySelector('[data-transport-capacity=record]').click();await until(()=>calls.some(call=>call.path.endsWith('/record')));})()");
 const calls=await evaluate('calls'),preview=calls.find(call=>call.path.endsWith('/preview')).body,record=calls.find(call=>call.path.endsWith('/record')).body;
 const expected={pathId:'synthetic-path',receiptFrom:'2032-03-14T08:00:00Z',receiptTo:'2032-03-14T09:00:00Z',receiptQuantity:'100.000000000000000001',deliveryFrom:'2032-03-14T10:00:00Z',deliveryTo:'2032-03-14T12:00:00Z',deliveryQuantity:'85'};
 assert.deepEqual(preview.command.claims,[expected]);
 assert.deepEqual(record.command.claims,[{...expected,claimId:'synthetic-canonical-claim-0'}]);
 assert.equal(record.operationId,preview.operationId);assert.equal(record.command.expectedStateVersion,9);
}));

test('a lost capacity request recovers its original actor, source and canonical command after source denial',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{lost=true;plan();document.querySelector('[data-transport-capacity=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-transport-capacity=record]'));document.querySelector('[data-transport-capacity=record]').click();await until(()=>!!window.original);capacity.reset();denied=true;mount();await capacity.load().catch(error=>errors.push(error.message));capacity.sync();})()");
 assert.equal(await evaluate("document.querySelector('[data-transport-capacity=pending]').hidden"),false);
 assert.equal(await evaluate("document.querySelector('[data-transport-capacity=status]').disabled"),false);
 await evaluate("wrongReceipt=true;document.querySelector('[data-transport-capacity=status]').click();new Promise(resolve=>setTimeout(resolve,30))");
 assert.equal(await evaluate("document.querySelector('[data-transport-capacity=pending]').hidden"),false);
 await evaluate("actor='another-operator';capacity.reset();mount();capacity.sync()");assert.equal(await evaluate("document.querySelector('[data-transport-capacity=pending]').hidden"),true);
 await evaluate("(async()=>{actor='synthetic-capacity-operator';capacity.reset();mount();capacity.sync();wrongReceipt=false;document.querySelector('[data-transport-capacity=status]').click();await until(()=>document.querySelector('[data-transport-capacity=pending]').hidden);})()");
 const calls=await evaluate('calls'),original=calls.find(call=>call.path.endsWith('/record')).body;
 assert.equal(calls.filter(call=>call.path.endsWith('/record')).length,1);
 for(const call of calls.filter(call=>call.path.endsWith('/status')))assert.deepEqual(call.body,{ownerId:original.ownerId,operationId:original.operationId});
 assert.deepEqual(original.context,{effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'});
}));

test('capacity historical reads show exact backend rates and only marked times follow the display zone',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);await evaluate("zone='America/Chicago';capacity.refresh()");
 const displayed=await evaluate("document.querySelector('[data-transport-capacity=inspection]').textContent");
 assert.match(displayed,/100 \/ 3/);assert.match(displayed,/2032-03-14T08:00:00Z/,'Unmarked source evidence remains literal.');
 assert.doesNotMatch(displayed,/08:00:00 UTC/);
 await evaluate("context={...context,knowledgeMode:'retained',retained:{contextId:'synthetic-capacity-context'}};view.historical=true;capacity.load('synthetic-entitlement')");
 assert.equal(await evaluate("document.querySelector('[data-transport-command=plan]').disabled"),true);
 assert.equal(await evaluate("document.querySelector('[data-transport-command=define]').disabled"),true);
 assert.match(await evaluate("document.querySelector('[data-transport-capacity=context]').textContent"),/Historical, read only/);
}));

test('capacity read and preview cannot replace the explicitly selected entitlement',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);await evaluate("wrongRead=true;capacity.load('synthetic-entitlement').catch(error=>errors.push(error.message))");
 assert.equal(await evaluate("document.querySelector('[data-transport-capacity=inspection]').textContent"),'');
 await evaluate("(async()=>{wrongRead=false;await capacity.load('synthetic-entitlement');plan();wrongPreview=true;document.querySelector('[data-transport-capacity=editor]').requestSubmit();await until(()=>errors.some(error=>error.includes('exact transport capacity request')));})()");
 assert.equal(await evaluate("document.querySelector('[data-transport-capacity=record]')"),null);
 assert.equal(await evaluate("calls.some(call=>call.path.endsWith('/record'))"),false);
}));

test('future-source admission refusal creates no reviewed claim or write and backend availability controls remain authoritative',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{plan();futureSource=true;document.querySelector('[data-transport-capacity=editor]').requestSubmit();await until(()=>errors.some(error=>error.includes('future-effective')));view.canRecord=false;view.canConfigure=false;view.overviewSections=[{label:'Capacity source',values:[{label:'Availability',value:'Latest source is future-effective; choose its operating context before new admission.'}]}];await capacity.load('synthetic-entitlement');})()");
 assert.equal(await evaluate("document.querySelector('[data-transport-capacity=record]')"),null);
 assert.equal(await evaluate("document.querySelector('[data-transport-command=plan]').disabled"),true);
 assert.equal(await evaluate("document.querySelector('[data-transport-command=define]').disabled"),true);
 assert.match(await evaluate("document.querySelector('[data-transport-capacity=inspection]').textContent"),/Latest source is future-effective/);
 assert.equal(await evaluate("calls.some(call=>call.path.endsWith('/record'))"),false);
}));
