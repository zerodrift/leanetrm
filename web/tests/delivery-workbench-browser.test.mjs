import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const browser={skip:!browserAvailable,timeout:browserTestTimeout};
const markup=(await readFile(new URL('../src/index.html',import.meta.url),'utf8')).replace(/<script\b[^>]*>[\s\S]*?<\/script>/g,'');
const fixture=`(async()=>{
 window.calls=[];window.errors=[];window.actor='synthetic-delivery-operator';window.zone='UTC';
 window.context={effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'};
 const resolved={contextId:'synthetic-delivery-context',effectiveOn:'2032-03-14',knownAt:'2032-03-14T20:00:00Z'};
 const ref=code=>({revision:{catalogId:'synthetic-catalog',revisionId:'synthetic-revision'},code});
 const geometry={kind:'interval',from:'2032-03-14T08:00:00Z',through:'2032-03-14T09:00:00Z'};
 const form={label:'Allocate reviewed delivery',permission:'RecordGasOperations',base:{kind:'allocate',scopeId:'synthetic-route',expectedRouteStateVersion:4},fields:[{name:'quantity',label:'Exact allocation quantity',kind:'text',nullable:false},{name:'evidence',label:'Allocation evidence',kind:'text',nullable:false}]};
 const make=kind=>({profile:kind==='gas-transport'?'gas_transport_delivery_v1':'crude_receipt_delivery_v1',ownerId:'synthetic-owner',context:resolved,at:'2032-03-14T23:59:59.999999Z',scopeId:kind==='gas-transport'?'synthetic-route':'synthetic-crude-trade',scopes:[{id:kind==='gas-transport'?'synthetic-route':'synthetic-crude-trade',label:kind==='gas-transport'?'Synthetic route':'Synthetic crude receipt',revision:1}],historical:false,canConfigure:false,canRecord:kind==='gas-transport',configuration:{},forms:kind==='gas-transport'?{allocate:form}:{},overviewSections:[{label:'Delivery ownership',values:[{label:'Source',value:kind==='gas-transport'?'Chosen route plan':'Native receipt and dispatch evidence'}]}],tables:[],assessmentQuantityLabel:kind==='gas-transport'?'MMBtu (IT)':'standard barrels',assessment:{profile:kind==='gas-transport'?'gas_transport_interval_v1':'crude_receipt_transfer_v1',quantitySpecification:ref('native_quantity'),activities:[{sourceId:'synthetic-source',stage:kind==='gas-transport'?'chosen':'accepted',label:kind==='gas-transport'?'Chosen receipt':'Accepted receipt',geometry:kind==='gas-transport'?geometry:{kind:'transfer',from:'2032-03-14T08:30:00Z',through:null},location:ref('synthetic-point'),quantity:kind==='gas-transport'?'100/3':'25.5'}],measures:[{code:'unassigned',label:'Explicitly unassigned',value:'7.25'}],constraints:[{code:'source',label:'Source capacity',state:kind==='gas-transport'?'within_limit':'unassessed',sourceId:null,geometry:null,observed:null,limit:null,residual:null,basis:'quantity'}],capabilities:[{code:'inspect',label:'Inspect retained delivery evidence'}]}});
 window.views={'gas-transport':make('gas-transport'),'crude-receipt':make('crude-receipt')};
 window.api=async(path,body)=>{calls.push({path,body:structuredClone(body)});
  if(path==='time/inspect')return{candidates:[{instant:body.localTime+(body.localTime.length===16?':00':'')+'Z',offset:'+00:00'}]};
  if(path==='facilities/quantities/directory')return{ownerId:'synthetic-owner',context:resolved,at:'2032-03-14T23:59:59Z',ledgers:[],choices:[],hasMore:false,choicesTruncated:false,canRecord:true,canConfigure:true};
  const found=/^delivery\\/workbench\\/(gas-transport|crude-receipt)\\/(directory|read|preview|record|status)$/.exec(path);if(!found)throw Error('Unexpected workbench call '+path);
  const kind=found[1],action=found[2],view=views[kind];
  if(window.denied&&['directory','read'].includes(action))throw Object.assign(Error('Synthetic source denied'),{code:'access_denied'});
  if(action==='directory')return{...view,scopeId:undefined,forms:{},assessment:null};
  if(action==='read')return{...view,profile:window.wrongProfile?'unqualified_profile':view.profile,scopeId:body.scopeId};
  if(action==='preview')return{...view,operationId:body.operationId,scopeId:body.command.scopeId,kind:body.command.kind,command:window.wrongRouteState?{...body.command,expectedRouteStateVersion:5}:body.command,effectiveAt:body.effectiveAt};
  if(action==='record'){window.original=structuredClone(body);if(window.lost)throw new TypeError('Synthetic lost delivery reply');return{profile:view.profile,operationId:body.operationId,scopeId:body.command.scopeId,kind:body.command.kind,outcome:{changed:true}};}
  if(action==='status')return{profile:view.profile,operationId:original.operationId,scopeId:original.command.scopeId,kind:original.command.kind,command:original.command,effectiveAt:original.effectiveAt,outcome:{changed:true}};
 };
 window.until=async test=>{for(let i=0;i<300&&!test();i++)await new Promise(resolve=>setTimeout(resolve,10));if(!test())throw Error('Delivery controls not ready '+JSON.stringify(errors));};
 window.workspace=(await import('/facilities-workspace.js')).setupFacilities({api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=>zone,identity:()=>actor,context:()=>context,permission:()=>true,changed(){},tradeInspection:()=>null,openTrade:async()=>{}});
 await workspace.load();
})()`;

test('delivery profiles share one workbench and read only the explicitly selected consumer',browser,()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(fixture);
 await evaluate("document.querySelector('[data-facilities-view=gas-delivery]').click();until(()=>calls.some(call=>call.path==='delivery/workbench/gas-transport/directory'))");
 assert.equal(await evaluate('workspace.section()'),'gas-delivery');
 assert.equal(await evaluate("calls.some(call=>call.path.includes('crude-receipt'))"),false);
 await evaluate("document.querySelector('[data-facilities-view=crude-delivery]').click();until(()=>calls.some(call=>call.path==='delivery/workbench/crude-receipt/directory'))");
 assert.equal(await evaluate('workspace.section()'),'crude-delivery');
 assert.deepEqual(await evaluate('errors'),[]);
 assert.deepEqual(await evaluate('calls.map(call=>call.path)'),['facilities/quantities/directory','delivery/workbench/gas-transport/directory','delivery/workbench/crude-receipt/directory']);
}));

test('interval flow and discrete transfer retain their server quantities and independent states',browser,()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(fixture);
 await evaluate("workspace.open('gas-delivery','synthetic-route')");
 const gas=await evaluate("document.querySelector('#gas-delivery-workspace').textContent");
 assert.match(gas,/Interval flow/);assert.match(gas,/100\/3/);assert.match(gas,/MMBtu \(IT\)/);assert.match(gas,/Within limit/);
 await evaluate("workspace.open('crude-delivery','synthetic-crude-trade')");
 const crude=await evaluate("document.querySelector('#crude-delivery-workspace').textContent");
 assert.match(crude,/Transfer event/);assert.match(crude,/25.5/);assert.match(crude,/standard barrels/);assert.match(crude,/Unassessed/);
 assert.doesNotMatch(crude,/Delivery completed|Cash accepted|Title transferred/);
 assert.deepEqual(await evaluate('errors'),[]);
}));

test('a workbench source cannot substitute another installed delivery profile',browser,()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(fixture);await evaluate("wrongProfile=true;workspace.open('gas-delivery','synthetic-route').catch(error=>errors.push(error.message))");
 assert.match(await evaluate('errors.join()'),/profile/i);
 assert.equal(await evaluate("document.querySelector('[data-delivery-workbench-gas=inspection]').textContent"),'');
 assert.equal(await evaluate("document.querySelector('[data-delivery-command=allocate]')"),null);
}));

test('native crude commands require an explicit close and carry the exact source revision to the existing workspace',browser,()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(fixture.replace('openTrade:async()=>{}','openTrade:async()=>{},nativeDeliveryReason:()=>window.closeReady?null:"Choose a financial close.",openNativeDelivery:async target=>{window.nativeTarget=target;}'));
 await evaluate("views['crude-receipt'].nativeActions=[{label:'Open receipt and dispatch operations',tradeId:'synthetic-crude-trade',bindingId:'synthetic-crude-binding',revision:7,instrumentType:'physical_crude',tab:'delivery'}];workspace.open('crude-delivery','synthetic-crude-trade')");
 assert.equal(await evaluate("document.querySelector('[data-native-delivery]').disabled"),true);
 assert.match(await evaluate("document.querySelector('#crude-delivery-workspace').textContent"),/Choose a financial close/);
 await evaluate("closeReady=true;workspace.sync();document.querySelector('[data-native-delivery]').click();until(()=>!!window.nativeTarget)");
 assert.deepEqual(await evaluate('nativeTarget'),{tradeId:'synthetic-crude-trade',bindingId:'synthetic-crude-binding',revision:7,instrumentType:'physical_crude',tab:'delivery'});
}));

test('ordinary trade readers enter crude delivery without reading gas routes or facility pools',browser,()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(fixture.replace('permission:()=>true',"permission:right=>['ReadMarketData','ReadTrades'].includes(right)"));
 assert.equal(await evaluate('workspace.section()'),'crude-delivery');
 assert.deepEqual(await evaluate('calls.map(call=>call.path)'),['delivery/workbench/crude-receipt/directory']);
 await evaluate('workspace.sync()');
 assert.equal(await evaluate("document.querySelector('[data-facilities-view=gas-delivery]').disabled"),true);
 assert.equal(await evaluate("document.querySelector('[data-facilities-view=quantities]').disabled"),true);
}));

test('an unsaved allocation cannot be silently replaced by recording a nomination batch',browser,()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(fixture.replace('tradeInspection:()=>null',"nominationContext:()=>({snapshotId:'synthetic-financial-close',convention:'physical_gas_fixed_quantity_v4',knowledgeMode:'current'}),tradeInspection:()=>null"));
 await evaluate("views['gas-transport'].nominationBatch={profile:'complete_allocation_nomination_batch_v1',allocationPlanId:'synthetic-plan',channels:[{value:'portal',label:'Portal'}],entries:[{obligationId:'synthetic-obligation',tradeId:'synthetic-trade',expectedRevision:1,day:'2032-03-14',quantity:'25',label:'Synthetic receipt',accounts:[{id:'synthetic-account',versionId:'synthetic-version',label:'Synthetic service',expectedPreviousId:null}]}]};workspace.open('gas-delivery','synthetic-route')");
 assert.equal(await evaluate("document.querySelector('[data-delivery-nominations=form] fieldset').disabled"),false);
 await evaluate("document.querySelector('[data-delivery-command=allocate]').click();const quantity=document.querySelector('[name=quantity]');quantity.value='25';quantity.dispatchEvent(new Event('input',{bubbles:true}));workspace.sync()");
 assert.equal(await evaluate("document.querySelector('[data-delivery-nominations=form] fieldset').disabled"),true);
 assert.equal(await evaluate("document.querySelector('[name=quantity]').value"),'25');
}));

test('allocation review retains the exact expected route state version',browser,()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(fixture);
 await evaluate("(async()=>{await workspace.open('gas-delivery','synthetic-route');document.querySelector('[data-delivery-command=allocate]').click();for(const [name,value]of Object.entries({quantity:'25',evidence:'Synthetic allocation',operationEffectiveAt:'2032-03-14T10:00:00'})){const input=document.querySelector('[name='+name+']');input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));}wrongRouteState=true;document.querySelector('[data-delivery-workbench-gas=editor]').requestSubmit();await until(()=>errors.length>0||!!document.querySelector('[data-delivery-workbench-gas=record]'));})()");
 assert.equal(await evaluate("document.querySelector('[data-delivery-workbench-gas=record]')"),null);
 assert.match(await evaluate('errors.join()'),/exact|review/i);
}));

test('a lost reviewed allocation preserves its exact original command across profile changes and reload',browser,()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(fixture);
 await evaluate("(async()=>{await workspace.open('gas-delivery','synthetic-route');document.querySelector('[data-delivery-command=allocate]').click();for(const [name,value]of Object.entries({quantity:'100/3',evidence:'Synthetic source allocation',operationEffectiveAt:'2032-03-14T10:00:00'})){const input=document.querySelector('[name='+name+']');input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));}document.querySelector('[data-delivery-workbench-gas=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-delivery-workbench-gas=record]'));lost=true;document.querySelector('[data-delivery-workbench-gas=record]').click();await until(()=>errors.some(error=>error.includes('lost delivery')));})()");
 const original=await evaluate('original');assert.equal(original.command.quantity,'100/3');
 const reviewIds=await evaluate("[...document.querySelectorAll('#gas-delivery-workspace [id]')].map(element=>element.id)");assert.equal(new Set(reviewIds).size,reviewIds.length,'saved evidence and reviewed evidence need independent filter controls');
 await evaluate("workspace.open('crude-delivery','synthetic-crude-trade')");
 assert.equal(await evaluate("document.querySelector('[data-delivery-workbench-gas=status]').disabled"),false);
 await evaluate("workspace.reset();denied=true;workspace.load().catch(error=>errors.push(error.message));until(()=>workspace.section()==='gas-delivery')");
 assert.equal(await evaluate("document.querySelector('[data-delivery-workbench-gas=pending]').hidden"),false);
 await evaluate("document.querySelector('[data-delivery-workbench-gas=status]').click();until(()=>!document.querySelector('[data-delivery-workbench-gas=pending]').hidden===false)");
 const statuses=await evaluate("calls.filter(call=>call.path.endsWith('/status'))");
 assert.deepEqual(statuses.at(-1).body,{ownerId:original.ownerId,operationId:original.operationId});
 assert.equal(await evaluate("calls.filter(call=>call.path.endsWith('/record')).length"),1);
}));
