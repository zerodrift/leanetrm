import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const browser={skip:!browserAvailable,timeout:browserTestTimeout};
const setup=`(async()=>{
 window.calls=[];window.errors=[];window.actor='synthetic-route-operator';window.zone='UTC';window.rights=['ReadGasOperations','ConfigureGasServices','RecordGasOperations'];
 window.context={effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'};
 const resolved={contextId:'synthetic-route-context',effectiveOn:'2032-03-14',knownAt:'2032-03-14T20:00:00Z'};
 const text=(name,label)=>({name,label,kind:'text',nullable:false}),reference=(name,label,members,choices,nullable=false)=>({name,label,kind:'reference',nullable,members,choices});
 const base=kind=>({kind,routeId:'synthetic-route',definitionId:'synthetic-definition',expectedStateVersion:9});
 const previous=reference('previous','Previous chosen-route head',['expectedPreviousId'],[],true);
 const plan={label:'Adopt complete chosen route',permission:'RecordGasOperations',base:{...base('plan'),admissionPolicy:'synthetic-complete-source-policy'},fields:[text('businessKey','Route-plan reference'),previous,
  {name:'sources',label:'Exact complete capacity sources',kind:'array',nullable:false,minItems:1,fields:[reference('capacity','Exact capacity head',['entitlementId','capacityVersionId','capacityGeneration'],[{id:'capacity',label:'Synthetic capacity version2',value:{entitlementId:'synthetic-entitlement',capacityVersionId:'synthetic-capacity-version',capacityGeneration:7}}])]},
  {name:'sourcePlans',label:'Whole native source-plan streams',kind:'array',nullable:false,minItems:1,fields:[reference('stream','Exact logical stream and current head',['entitlementId','firstPlanRevisionId','planRevisionId','expectedMembershipGeneration'],[{id:'stream',label:'Synthetic source plan revision3',value:{entitlementId:'synthetic-entitlement',firstPlanRevisionId:'synthetic-first-plan',planRevisionId:'synthetic-current-plan',expectedMembershipGeneration:4}}])]},
  {name:'flows',label:'Map each exact native claim',kind:'array',nullable:false,minItems:1,fields:[reference('mapping','Exact segment and native claim',['segmentId','claimId'],[
   {id:'a',label:'Synthetic source segment / first native claim',value:{segmentId:'synthetic-segment-a',claimId:'synthetic-claim-a'}},
   {id:'b',label:'Synthetic sink segment / second native claim',value:{segmentId:'synthetic-segment-b',claimId:'synthetic-claim-b'}}])]},text('evidence','Route-plan evidence')]};
 const release={label:'Release only this route-plan membership',permission:'RecordGasOperations',base:{...base('release'),admissionPolicy:'synthetic-own-release-policy'},fields:[reference('head','Exact own current route-plan head',['businessKey','expectedPreviousId'],[{id:'own-head',label:'Synthetic own route-plan revision2',value:{businessKey:'synthetic-route-plan',expectedPreviousId:'synthetic-own-plan-head'}}]),text('evidence','Membership release evidence')]};
 window.view={ownerId:'opaque-route-owner',context:resolved,at:'2032-03-14T23:59:59.999999Z',routeId:'synthetic-route',routes:[{id:'synthetic-route',label:'Synthetic chosen route',revision:1}],historical:false,canConfigure:true,canRecord:true,configuration:{},forms:{plan,release},overviewSections:[{label:'Current route assessment',values:[{label:'Source',value:'Exact native claims, current source selection'}]}],tables:[{label:'Native source claims',columns:['Receipt starts','Delivery starts','Gross receipt','Gross delivery','Evidence'],instantColumns:[0,1],rows:[['2032-03-14T08:00:00Z','2032-03-14T09:00:00Z','100.000000000000000001','98','2032-03-14T08:00:00Z']]}]};
 window.api=async(path,body)=>{calls.push({path,body:structuredClone(body)});
  if(path==='time/inspect')return{candidates:[{instant:body.localTime+(body.localTime.length===16?':00':'')+'Z',offset:'+00:00'}]};
  if(path==='gas/transport/routes/directory'){if(window.denied)throw Object.assign(Error('Synthetic route source denied'),{code:'access_denied'});return{...view,forms:{}};}
  if(path==='gas/transport/routes/read')return{...view,routeId:window.wrongRead?'another-route':body.routeId,context:window.wrongContext?{...resolved,effectiveOn:'2032-03-13'}:resolved};
  if(path==='gas/transport/routes/preview'){
   const routeId=window.wrongPreview?'another-route':body.command.routeId??'synthetic-route',command={...body.command,routeId};
   if(command.flows)command.flows=command.flows.map((flow,index)=>({...flow,flowId:'synthetic-canonical-flow-'+index}));
   if(window.normalizedGraph)command.segments=command.segments.map(segment=>({...segment,fuelFraction:{n:'1',d:'2'}}));
   return{...view,operationId:body.operationId,routeId,kind:command.kind,effectiveAt:body.effectiveAt,command};
  }
  if(path==='gas/transport/routes/record'){window.original=structuredClone(body);if(window.lost)throw new TypeError('Synthetic lost route acknowledgement');if(body.command.kind==='define')view.configuration={};return{operationId:body.operationId,routeId:body.command.routeId,kind:body.command.kind,outcome:{planRevisionId:'synthetic-recorded-route-plan',revision:1,changed:true}};}
  if(path==='gas/transport/routes/status')return{operationId:original.operationId,routeId:original.command.routeId,kind:original.command.kind,effectiveAt:original.effectiveAt,command:window.wrongReceipt?{...original.command,evidence:'A different route selection'}:original.command,outcome:{planRevisionId:'synthetic-recorded-route-plan',revision:1,changed:true}};
  throw Error('Unexpected route consumer '+path);
 };
 window.until=async test=>{for(let i=0;i<300&&!test();i++)await new Promise(resolve=>setTimeout(resolve,10));if(!test())throw Error('Route controls not ready '+JSON.stringify(errors));};
 window.mount=()=>window.route=(window.createRoute)(document.querySelector('#host'),{api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=>zone,identity:()=>actor,context:()=>context,permission:name=>rights.includes(name),changed(){}});
 window.createRoute=(await import('/transport-routes-view.js')).setupTransportRoutes;mount();await route.load('synthetic-route');
 window.pick=(name,value,host=document.querySelector('[data-transport-routes=editor]'))=>{const input=host.querySelector('[name='+name+']');input.value=value;input.dispatchEvent(new Event('change',{bubbles:true}));};
 window.row=(name,index)=>document.querySelectorAll('[data-operation-entry='+name+']')[index];
 window.add=name=>{const input=document.querySelector('[name='+name+']');if(input.value!=='entries')pick(name,'entries');input.closest('.operation-field').querySelector(':scope > button').click();};
 window.plan=()=>{document.querySelector('[data-route-command=plan]').click();pick('businessKey','synthetic-route-plan');pick('previous','@null');add('sources');pick('capacity','capacity',row('sources',0));add('sourcePlans');pick('stream','stream',row('sourcePlans',0));add('flows');pick('mapping','a',row('flows',0));add('flows');pick('mapping','b',row('flows',1));pick('evidence','Synthetic complete source adoption');pick('operationEffectiveAt','2032-03-14T10:00:00');};
})()`;

test('route adoption submits exact whole source streams and native claim mappings without editable source quantities or clocks',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);await evaluate('plan()');
 assert.deepEqual(await evaluate("[...document.querySelectorAll('[data-transport-routes=editor] input')].map(input=>input.name).sort()"),['businessKey','evidence','operationEffectiveAt']);
 await evaluate("(async()=>{document.querySelector('[data-transport-routes=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-transport-routes=record]'));document.querySelector('[data-transport-routes=record]').click();await until(()=>calls.some(call=>call.path.endsWith('/record')));})()");
 const calls=await evaluate('calls'),preview=calls.find(call=>call.path.endsWith('/preview')).body,record=calls.find(call=>call.path.endsWith('/record')).body;
 assert.deepEqual(preview.command.sources,[{entitlementId:'synthetic-entitlement',capacityVersionId:'synthetic-capacity-version',capacityGeneration:7}]);
 assert.deepEqual(preview.command.sourcePlans,[{entitlementId:'synthetic-entitlement',firstPlanRevisionId:'synthetic-first-plan',planRevisionId:'synthetic-current-plan',expectedMembershipGeneration:4}]);
 assert.deepEqual(preview.command.flows,[{segmentId:'synthetic-segment-a',claimId:'synthetic-claim-a'},{segmentId:'synthetic-segment-b',claimId:'synthetic-claim-b'}]);
 assert.deepEqual(record.command.flows,preview.command.flows.map((flow,index)=>({...flow,flowId:'synthetic-canonical-flow-'+index})));
 assert.equal(record.operationId,preview.operationId);assert.equal(record.command.admissionPolicy,'synthetic-complete-source-policy');
 assert.ok(calls.every(call=>call.path.startsWith('gas/transport/routes/')||call.path==='time/inspect'));
}));

test('graph fractions and lag remain exact strings and recording keeps the backend canonical graph',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate(`(async()=>{
  view.configuration.define={label:'Define exact immutable route',permission:'ConfigureGasServices',base:{kind:'define',routeId:null,expectedStateVersion:0,expectedPreviousId:null},fields:[{name:'segments',label:'Explicit graph segments',kind:'array',nullable:false,minItems:1,fields:[{name:'segmentKey',label:'Local segment key',kind:'text',nullable:false},{name:'fuelFraction',label:'Gross-receipt fuel fraction',kind:'object',nullable:false,fields:[{name:'n',label:'Numerator',kind:'text',nullable:false},{name:'d',label:'Denominator',kind:'text',nullable:false}]},{name:'lossFraction',label:'Gross-receipt loss fraction',kind:'object',nullable:false,fields:[{name:'n',label:'Numerator',kind:'text',nullable:false},{name:'d',label:'Denominator',kind:'text',nullable:false}]},{name:'transitMicroseconds',label:'Fixed elapsed microseconds',kind:'text',nullable:false}]}]};await route.load('');document.querySelector('[data-route-command=define]').click();
 })()`);
 assert.equal(await evaluate("document.querySelector('[name=segments]').value"),'');
 await evaluate("add('segments');pick('segmentKey','synthetic-segment',row('segments',0));const objects=row('segments',0).querySelectorAll('.operation-field > .schedule-fields');pick('n','9007199254740993',objects[0]);pick('d','18014398509481986',objects[0]);pick('n','0',objects[1]);pick('d','1',objects[1]);pick('transitMicroseconds','9007199254740993',row('segments',0));pick('operationEffectiveAt','2032-03-14T10:00:00');normalizedGraph=true");
 await evaluate("(async()=>{document.querySelector('[data-transport-routes=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-transport-routes=record]'));document.querySelector('[data-transport-routes=record]').click();await until(()=>calls.some(call=>call.path.endsWith('/record')));})()");
 const calls=await evaluate('calls'),entered=calls.find(call=>call.path.endsWith('/preview')).body.command.segments[0],recorded=calls.find(call=>call.path.endsWith('/record')).body.command.segments[0];
 assert.deepEqual(entered.fuelFraction,{n:'9007199254740993',d:'18014398509481986'});assert.deepEqual(entered.lossFraction,{n:'0',d:'1'});assert.equal(entered.transitMicroseconds,'9007199254740993');
 assert.deepEqual(recorded.fuelFraction,{n:'1',d:'2'});assert.equal(recorded.transitMicroseconds,entered.transitMicroseconds);
}));

test('stale capacity withholds adoption while an explicitly selected own-membership release stays available',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{view.forms={release:view.forms.release};view.overviewSections=[{label:'Current route assessment',values:[{label:'Availability',value:'Unavailable: selected capacity source changed'}]},{label:'Retained accepted assessment',values:[{label:'Status',value:'Historical accepted conservation, not current'}]}];await route.load('synthetic-route');document.querySelector('[data-route-command=release]').click();})()");
 assert.equal(await evaluate("document.querySelector('[data-route-command=plan]')"),null);
 assert.equal(await evaluate("document.querySelector('[data-route-command=release]').disabled"),false);
 assert.equal(await evaluate("document.querySelector('[name=head]').value"),'');
 await evaluate("(async()=>{pick('head','own-head');pick('evidence','Synthetic explicit own membership release');pick('operationEffectiveAt','2032-03-14T10:00:00');document.querySelector('[data-transport-routes=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-transport-routes=record]'));})()");
 assert.match(await evaluate("document.querySelector('[data-transport-routes=review]').textContent"),/Unavailable: selected capacity source changed/);
 await evaluate("(async()=>{document.querySelector('[data-transport-routes=record]').click();await until(()=>calls.some(call=>call.path.endsWith('/record')));})()");
 assert.deepEqual(await evaluate("calls.find(call=>call.path.endsWith('/record')).body.command"),{kind:'release',routeId:'synthetic-route',definitionId:'synthetic-definition',expectedStateVersion:9,admissionPolicy:'synthetic-own-release-policy',businessKey:'synthetic-route-plan',expectedPreviousId:'synthetic-own-plan-head',evidence:'Synthetic explicit own membership release'});
}));

test('an unavailable retained selection keeps backend-authorized re-adoption of exact current sources',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{view.overviewSections=[{label:'Current retained selection',values:[{label:'Assessment',value:'Unavailable: previously selected source generation changed'}]}];const plan=view.forms.plan;plan.fields.find(field=>field.name==='sources').fields[0].choices[0].value={entitlementId:'synthetic-entitlement',capacityVersionId:'synthetic-current-capacity-version',capacityGeneration:8};plan.fields.find(field=>field.name==='sourcePlans').fields[0].choices[0].value.planRevisionId='synthetic-current-plan-revision4';const choices=plan.fields.find(field=>field.name==='flows').fields[0].choices;choices[0].value.claimId='synthetic-current-claim-a';choices[1].value.claimId='synthetic-current-claim-b';await route.load('synthetic-route');})()");
 assert.equal(await evaluate("document.querySelector('[data-route-command=plan]').disabled"),false);
 assert.match(await evaluate("document.querySelector('[data-transport-routes=inspection]').textContent"),/Unavailable: previously selected source generation changed/);
 await evaluate("(async()=>{plan();document.querySelector('[data-transport-routes=editor]').requestSubmit();await until(()=>calls.some(call=>call.path.endsWith('/preview')));})()");
 const body=await evaluate("calls.find(call=>call.path.endsWith('/preview')).body.command");
 assert.deepEqual(body.sources,[{entitlementId:'synthetic-entitlement',capacityVersionId:'synthetic-current-capacity-version',capacityGeneration:8}]);assert.equal(body.sourcePlans[0].planRevisionId,'synthetic-current-plan-revision4');assert.deepEqual(body.flows.map(flow=>flow.claimId),['synthetic-current-claim-a','synthetic-current-claim-b']);
}));

test('unknown route writes recover only the original actor and canonical request after source denial',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{lost=true;plan();document.querySelector('[data-transport-routes=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-transport-routes=record]'));document.querySelector('[data-transport-routes=record]').click();await until(()=>!!window.original);route.reset();denied=true;mount();await route.load().catch(error=>errors.push(error.message));route.sync();})()");
 assert.equal(await evaluate("document.querySelector('[data-transport-routes=pending]').hidden"),false);
 assert.equal(await evaluate("document.querySelector('[data-transport-routes=status]').disabled"),false);
 await evaluate("wrongReceipt=true;document.querySelector('[data-transport-routes=status]').click();new Promise(resolve=>setTimeout(resolve,30))");
 assert.equal(await evaluate("document.querySelector('[data-transport-routes=pending]').hidden"),false);
 await evaluate("actor='another-route-reader';route.reset();mount();route.sync()");assert.equal(await evaluate("document.querySelector('[data-transport-routes=pending]').hidden"),true);
 await evaluate("(async()=>{actor='synthetic-route-operator';context={...context,effectiveOn:'2032-03-15'};route.reset();mount();route.sync();wrongReceipt=false;document.querySelector('[data-transport-routes=status]').click();await until(()=>document.querySelector('[data-transport-routes=pending]').hidden);})()");
 const calls=await evaluate('calls'),original=calls.find(call=>call.path.endsWith('/record')).body;
 assert.equal(calls.filter(call=>call.path.endsWith('/record')).length,1);
 for(const status of calls.filter(call=>call.path.endsWith('/status')))assert.deepEqual(status.body,{ownerId:original.ownerId,operationId:original.operationId});
 assert.equal(original.context.effectiveOn,'2032-03-14');assert.equal(original.command.sourcePlans[0].planRevisionId,'synthetic-current-plan');
}));

test('route reads and previews refuse another route or business context',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);await evaluate("wrongRead=true;route.load('synthetic-route').catch(error=>errors.push(error.message))");
 assert.equal(await evaluate("document.querySelector('[data-transport-routes=inspection]').textContent"),'');
 await evaluate("wrongRead=false;wrongContext=true;route.load('synthetic-route').catch(error=>errors.push(error.message))");
 assert.equal(await evaluate("document.querySelector('[data-transport-routes=inspection]').textContent"),'');
 await evaluate("(async()=>{wrongContext=false;await route.load('synthetic-route');plan();wrongPreview=true;document.querySelector('[data-transport-routes=editor]').requestSubmit();await until(()=>errors.some(error=>error.includes('exact transport route request')));})()");
 assert.equal(await evaluate("document.querySelector('[data-transport-routes=record]')"),null);assert.equal(await evaluate("calls.some(call=>call.path.endsWith('/record'))"),false);
}));

test('route history keeps backend source values literal and formats only declared instants in the selected zone',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);await evaluate("zone='America/Chicago';route.refresh()");
 const text=await evaluate("document.querySelector('[data-transport-routes=inspection]').textContent");assert.match(text,/100\.000000000000000001/);assert.match(text,/2032-03-14T08:00:00Z/);assert.doesNotMatch(text,/08:00:00 UTC/);
 await evaluate("context={...context,knowledgeMode:'retained',retained:{contextId:'synthetic-route-context'}};view.historical=true;route.load('synthetic-route')");
 assert.equal(await evaluate("document.querySelector('[data-route-command=plan]').disabled"),true);assert.equal(await evaluate("document.querySelector('[data-route-command=release]').disabled"),true);
 assert.match(await evaluate("document.querySelector('[data-transport-routes=context]').textContent"),/Historical, read only/);
}));
