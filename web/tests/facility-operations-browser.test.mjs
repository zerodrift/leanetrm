import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const browser={skip:!browserAvailable,timeout:browserTestTimeout};
const setup=`(async()=>{
 window.calls=[];window.errors=[];window.openedLedgers=[];window.lost=false;window.currentContext={effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'};
 window.identity='synthetic-operator';window.historyMode=false;window.displayZone='UTC';
 const context={contextId:'synthetic-context',effectiveOn:'2032-03-14',knownAt:'2032-03-14T20:00:00Z',knownThrough:7};
 const text=(name,label)=>({name,label,kind:'text',nullable:false});
 const form=(kind,label,fields,permission='RecordFacilityQuantities')=>({label,permission,base:{kind,facilityId:'synthetic-facility',expectedStateVersion:3},fields});
 const define=form('define','Publish physical facility definition',[
  {name:'profile',label:'Physical rule profile',kind:'select',nullable:false,choices:[{id:'step',label:'Exact step ratchets',value:'storage_total_physical_step_ratchets_v1'}]},
  {name:'ratchets',label:'Ratchet bands',kind:'array',nullable:false,minItems:1,fields:[text('lower','Lower inventory'),text('upper','Upper inventory'),text('injectionPerHour','Injection per elapsed hour'),text('withdrawalPerHour','Withdrawal per elapsed hour')]},text('evidence','Definition evidence')],'ConfigureFacilityLimits');
 window.reply={ownerId:'opaque-owner',context,at:'2032-03-14T23:59:59.999999Z',facilities:[{id:'synthetic-facility',label:'Synthetic storage',versionId:'synthetic-version',revision:2}],canRecord:true,canConfigure:true,historical:false,
  configuration:{define},forms:{opening:form('opening','Record dated opening evidence',[{name:'observedAt',label:'Opening observation time',kind:'instant',nullable:false},text('quantity','Physical opening level'),text('evidence','Opening evidence')]),
   activity:form('activity','Record measured activity',[{name:'port',label:'Exact injection port',kind:'reference',nullable:false,members:['versionId','portId','direction'],choices:[{id:'port-1',label:'Synthetic pipeline point / inject',value:{versionId:'synthetic-version',portId:'synthetic-port',direction:'inject'}}]},text('quantity','Observed quantity'),text('evidence','Measurement evidence')]),
   plan:form('plan','Admit complete chosen plan',[{name:'coverageStatement',label:'Complete chosen-plan coverage',kind:'select',nullable:false,choices:[{id:'idle',label:'Listed activities complete; gaps idle',value:'chosen_plan_idle_between_activities_v1'}]},text('evidence','Plan review evidence')])},
  allocationLinks:[{ledgerId:'synthetic-source-ledger',label:'Synthetic reported flow / exact source'}],overviewSections:[{label:'Physical activity',values:[{label:'Measured coverage',value:'Complete observed window'},{label:'Inventory assessment',value:'Breached'},{label:'Closing physical level',value:'1010 native units'}]}],tables:[{label:'Native intervals',columns:['Track','Opening','Closing','Breach'],rows:[['Measured','990','1010','Inventory maximum exceeded by 10']]}]};
 window.api=async(path,body)=>{calls.push({path,body:structuredClone(body)});
  if(path==='time/inspect')return{candidates:[{instant:'2032-03-14T08:00:00Z',offset:'+00:00'}]};
  if(path==='facilities/operations/directory'){if(window.holdDirectory)await new Promise(resolve=>window.releaseDirectory=resolve);if(window.readDenied)throw Object.assign(Error('Synthetic facility access revoked'),{code:'access_denied'});return{...reply,context:{...context,effectiveOn:body.context.effectiveOn},forms:{},configuration:{define:{...define,base:{...define.base,facilityId:null,expectedStateVersion:0}}},historical:historyMode};}
  if(path==='facilities/operations/read'){const pins=forms=>Object.fromEntries(Object.entries(forms).map(([key,form])=>[key,{...form,base:{...form.base,facilityId:window.changedFormFacility?'another-synthetic-facility':body.facilityId}}]));const forms=pins(reply.forms);if(window.searchChoices)forms.activity.fields=[...forms.activity.fields,{name:'source',label:'Exact reported flow',kind:'reference',nullable:true,members:['sourceRevisionId'],choices:searchChoices[body.sourceSearch??'']??[]}];return{...reply,facilityId:window.changedReadFacility?'another-synthetic-facility':body.facilityId,context:{...context,effectiveOn:body.context.effectiveOn},forms,configuration:pins(reply.configuration),historical:historyMode,sourceSearch:window.changedSearchEcho?'different synthetic query':body.sourceSearch??'',sourceChoicesTruncated:!!window.truncatedSearch};}
  if(path==='facilities/operations/preview'){const facilityId=window.changedPreviewFacility?'another-synthetic-facility':body.command.facilityId??'synthetic-new-facility';return{...reply,operationId:body.operationId,facilityId,kind:body.command.kind,effectiveAt:body.effectiveAt,command:{...body.command,facilityId}};}
  if(path==='facilities/operations/record'){window.original=structuredClone(body);if(window.staleRetry)throw Object.assign(Error('Synthetic stale native source'),{code:'storage_facility_stale'});if(lost)throw Error('Synthetic lost acknowledgement');return{operationId:body.operationId,facilityId:body.command.facilityId,kind:body.command.kind,outcome:{revisionId:'synthetic-revision',revision:1,changed:true}};}
  if(path==='facilities/operations/status'){if(window.noReceipt)return{found:false};return{found:true,operationId:original.operationId,facilityId:original.command.facilityId,kind:original.command.kind,command:window.changedReceipt?{...original.command,evidence:'Different synthetic fact'}:original.command,effectiveAt:original.effectiveAt,outcome:{revisionId:'synthetic-revision',revision:1,changed:true}};}
  throw Error('Unexpected '+path);
 };
 window.until=async test=>{for(let i=0;i<300&&!test();i++)await new Promise(resolve=>setTimeout(resolve,10));if(!test())throw Error('Facility action did not settle: '+JSON.stringify({errors,calls:calls.map(call=>call.path)}));};
 window.operations=(await import('/facility-operations-view.js')).setupFacilityOperations(document.querySelector('#host'),{api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=>displayZone,identity:()=>identity,context:()=>currentContext,permission:()=>true,changed(){},openAllocations:async id=>openedLedgers.push(id)});
 window.pick=(name,value)=>{const input=document.querySelector('[name='+name+']');input.value=value;input.dispatchEvent(new Event('change',{bubbles:true}));};
 await operations.load();
})()`;

test('a bounded facility directory states when additional assets are not displayed',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate('reply.hasMore=true;operations.load()');
 assert.match(await evaluate("document.querySelector('[data-facility-ops=message]').textContent"),/more physical facilities/i);
 assert.equal(await evaluate("calls.some(call=>call.path.endsWith('/record'))"),false);
 await evaluate('reply.hasMore=false;operations.load()');
 assert.doesNotMatch(await evaluate("document.querySelector('[data-facility-ops=message]').textContent"),/more physical facilities/i);
}));

test('Activity searches exact reported-flow choices before entry and keeps its query after a confirmed refresh',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=source-search]')?.hidden??true"),true);
 await evaluate("(async()=>{window.query=' 2032-03-14 synthetic point ';window.searchChoices={[query]:[{id:'synthetic-exact-source',label:'Synthetic point / reported operating day / revision4',value:{sourceRevisionId:'synthetic-source-revision'}}]};window.truncatedSearch=true;operations.show('activity');const facility=document.querySelector('[data-facility-ops=facility]');facility.value='synthetic-facility';facility.dispatchEvent(new Event('change'));await until(()=>!!document.querySelector('[data-facility-command=activity]'));})()");
 assert.equal(await evaluate("!!document.querySelector('[data-facility-ops=source-search]')&&!document.querySelector('[data-facility-ops=source-search]').hidden"),true);
 await evaluate("(async()=>{document.querySelector('[data-facility-ops=source-query]').value=query;document.querySelector('[data-facility-ops=source-search]').requestSubmit();await until(()=>calls.some(call=>call.path==='facilities/operations/read'&&call.body.sourceSearch===query)&&!!document.querySelector('[data-facility-command=activity]'));document.querySelector('[data-facility-command=activity]').click();})()");
 assert.deepEqual(await evaluate("[...document.querySelector('[name=source]').options].map(option=>option.value)"),['','@null','synthetic-exact-source']);
 assert.match(await evaluate("document.querySelector('[data-facility-ops=source-hint]').textContent"),/refine/i);
 await evaluate("(async()=>{document.querySelector('[data-facility-command=opening]').click();pick('observedAt','2032-03-14T08:00:00');pick('quantity','400');pick('evidence','Synthetic explicit level');pick('operationEffectiveAt','2032-03-14T08:00:00');document.querySelector('[data-facility-ops=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-facility-ops=record]'));const reads=calls.filter(call=>call.path==='facilities/operations/read').length;document.querySelector('[data-facility-ops=record]').click();await until(()=>calls.filter(call=>call.path==='facilities/operations/read').length>reads);})()");
 assert.equal(await evaluate("calls.filter(call=>call.path==='facilities/operations/read').at(-1).body.sourceSearch"),' 2032-03-14 synthetic point ');
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=source-query]').value"),' 2032-03-14 synthetic point ');
 await evaluate("currentContext={...currentContext,effectiveOn:'2032-03-15'};operations.load()");
 assert.equal(await evaluate("calls.filter(call=>call.path==='facilities/operations/read').at(-1).body.sourceSearch"),'','a new effective context cannot reuse an old search');
 await evaluate("operations.load('synthetic-facility',query)");
 await evaluate("reply.facilities.push({id:'another-synthetic-facility',label:'Another physical facility',versionId:'another-version',revision:1});operations.load()");
 await evaluate("(async()=>{const facility=document.querySelector('[data-facility-ops=facility]');facility.value='another-synthetic-facility';facility.dispatchEvent(new Event('change'));await until(()=>calls.filter(call=>call.path==='facilities/operations/read').at(-1).body.facilityId==='another-synthetic-facility');})()");
 assert.equal(await evaluate("calls.filter(call=>call.path==='facilities/operations/read').at(-1).body.sourceSearch"),'');
 assert.deepEqual(await evaluate('errors'),[]);
}));

test('a changed reported-flow search echo withholds the returned metadata and facts',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{operations.show('activity');await operations.load('synthetic-facility');})()");
 assert.equal(await evaluate("!!document.querySelector('[data-facility-ops=source-search]')&&!document.querySelector('[data-facility-ops=source-search]').hidden"),true);
 await evaluate("(async()=>{changedSearchEcho=true;document.querySelector('[data-facility-ops=source-query]').value='synthetic pipeline';document.querySelector('[data-facility-ops=source-search]').requestSubmit();await until(()=>errors.length>0);})()");
 assert.match((await evaluate('errors'))[0],/search/i);
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=inspection]').textContent"),'');
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=actions]').textContent"),'');
}));

test('searching reported flows obeys the same unsaved facility-input guard',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{operations.show('activity');await operations.load('synthetic-facility');document.querySelector('[data-facility-command=opening]').click();pick('evidence','Synthetic unsaved level evidence');window.confirm=()=>false;})()");
 assert.equal(await evaluate("!!document.querySelector('[data-facility-ops=source-search]')&&!document.querySelector('[data-facility-ops=source-search]').hidden"),true);
 const before=await evaluate("calls.filter(call=>call.path==='facilities/operations/read').length");
 await evaluate("document.querySelector('[data-facility-ops=source-query]').value='synthetic later point';document.querySelector('[data-facility-ops=source-search]').requestSubmit()");
 assert.equal(await evaluate("calls.filter(call=>call.path==='facilities/operations/read').length"),before);
 assert.equal(await evaluate("document.querySelector('[name=evidence]').value"),'Synthetic unsaved level evidence');
 await evaluate("(async()=>{window.confirm=()=>true;document.querySelector('[data-facility-ops=source-search]').requestSubmit();await until(()=>calls.filter(call=>call.path==='facilities/operations/read').length>"+before+");})()");
 assert.equal(await evaluate("calls.filter(call=>call.path==='facilities/operations/read').at(-1).body.sourceSearch"),'synthetic later point');
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=editor]').hidden"),true);
 assert.equal(await evaluate("calls.some(call=>call.path==='facilities/operations/record')"),false);
}));

test('facility definitions use explicit backend ratchet fields and record the exact canonical preview command',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("document.querySelector('[data-facility-command=define]').click()");
 assert.equal(await evaluate("document.querySelector('[name=profile]').value"),'');
 assert.equal(await evaluate("document.querySelector('[name=ratchets]').value"),'');
 assert.equal(await evaluate("document.querySelector('input[name=facilityId]')===null"),true);
 await evaluate("(async()=>{pick('profile','step');pick('ratchets','entries');[...document.querySelectorAll('button')].find(button=>button.textContent==='Add entry').click();for(const[name,value]of Object.entries({lower:'0',upper:'1000',injectionPerHour:'25',withdrawalPerHour:'100',evidence:'Synthetic rule evidence',operationEffectiveAt:'2032-03-14T08:00:00'}))pick(name,value);document.querySelector('[data-facility-ops=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-facility-ops=review]'));})()");
 assert.equal(await evaluate("calls.filter(call=>call.path==='facilities/operations/record').length"),0);
 await evaluate("(async()=>{document.querySelector('[data-facility-ops=record]').click();await until(()=>calls.some(call=>call.path==='facilities/operations/record'));})()");
 const preview=await evaluate("calls.find(call=>call.path==='facilities/operations/preview').body");
 const record=await evaluate("calls.find(call=>call.path==='facilities/operations/record').body");
 assert.equal(record.operationId,preview.operationId);
 assert.deepEqual(record.command,{...preview.command,facilityId:'synthetic-new-facility'});
 assert.deepEqual(record.command.ratchets,[{lower:'0',upper:'1000',injectionPerHour:'25',withdrawalPerHour:'100'}]);
 assert.deepEqual(await evaluate('errors'),[]);
}));

test('measured breaches stay readable and a lost action acknowledgement recovers only its original actor-bound receipt',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{operations.show('activity');const facility=document.querySelector('[data-facility-ops=facility]');facility.value='synthetic-facility';facility.dispatchEvent(new Event('change'));await until(()=>!!document.querySelector('[data-facility-command=activity]'));document.querySelector('[data-facility-command=activity]').click();})()");
 assert.match(await evaluate("document.querySelector('[data-facility-ops=inspection]').textContent"),/1010.*Inventory maximum exceeded by 10/s);
 await evaluate("(async()=>{pick('port','port-1');pick('quantity','20');pick('evidence','Synthetic observed flow');pick('operationEffectiveAt','2032-03-14T08:00:00');document.querySelector('[data-facility-ops=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-facility-ops=review]'));lost=true;document.querySelector('[data-facility-ops=record]').click();await until(()=>errors.length===1);})()");
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=pending]').hidden"),false);
 const original=await evaluate('original');
 await evaluate("(async()=>{currentContext={...currentContext,effectiveOn:'2032-03-15'};operations.reset();await operations.load();document.querySelector('[data-facility-ops=status]').click();await until(()=>document.querySelector('[data-facility-ops=pending]').hidden);})()");
 assert.deepEqual(await evaluate("calls.find(call=>call.path==='facilities/operations/status').body"),{ownerId:original.ownerId,operationId:original.operationId});
 assert.equal(await evaluate("calls.filter(call=>call.path==='facilities/operations/record').length"),1);
}));

test('historical facility inspection keeps facts and disables native authoring',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("historyMode=true;currentContext={...currentContext,knowledgeMode:'retained',retained:reply.context};operations.reset();operations.load()");
 assert.equal(await evaluate("document.querySelector('[data-facility-command=define]').disabled"),true);
 assert.equal(await evaluate("calls.filter(call=>call.path==='facilities/operations/preview'||call.path==='facilities/operations/record').length"),0);
}));

test('a refused facility refresh withholds prior facility identities and physical facts',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 assert.match(await evaluate("document.querySelector('[data-facility-ops=facility]').textContent"),/Synthetic storage/);
 await evaluate('readDenied=true;operations.load().catch(error=>errors.push(error.message))');
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=facility]').options.length"),1);
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=inspection]').textContent"),'');
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=context]').textContent"),'');
}));

test('facility ratchet entry remains usable at phone width with complete backend reference labels',browser,()=>withBrowserPage('<meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/app.css"><main style="margin-left:0"><div id="host"></div></main>',async(evaluate,command)=>{
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await evaluate(setup);
 await evaluate("reply.configuration.define.fields[0].choices[0].label='Synthetic_'.padEnd(180,'x');document.querySelector('[data-facility-command=define]').click();pick('ratchets','entries');[...document.querySelectorAll('button')].find(button=>button.textContent==='Add entry').click()");
 const geometry=await evaluate("({width:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth,outside:[...document.querySelectorAll('fieldset,input,select,button')].filter(node=>{const box=node.getBoundingClientRect();return node.getClientRects().length&&(box.left<0||box.right>document.documentElement.clientWidth+1);}).map(node=>node.tagName+':'+node.getAttribute('name'))})");
 assert.ok(geometry.scroll<=geometry.width+1,JSON.stringify(geometry));
 assert.deepEqual(geometry.outside,[]);
}));

test('physical activity allocation links remain usable at phone width',browser,()=>withBrowserPage('<meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/app.css"><main style="margin-left:0"><div id="host"></div></main>',async(evaluate,command)=>{
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await evaluate(setup);
 await evaluate("reply.allocationLinks[0].label='synthetic_browser_linked_flow';operations.show('activity');operations.load('synthetic-facility')");
 const geometry=await evaluate("({width:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth,outside:[...document.querySelectorAll('[data-facility-allocation]')].filter(node=>node.getBoundingClientRect().right>document.documentElement.clientWidth+1).map(node=>node.textContent)})");
 assert.ok(geometry.scroll<=geometry.width+1,JSON.stringify(geometry));assert.deepEqual(geometry.outside,[]);
}));

test('a late facility source cannot publish identities after an account reset',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("holdDirectory=true;window.oldRead=operations.load();identity='another-synthetic-operator';operations.reset();releaseDirectory();oldRead");
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=facility]').options.length"),1);
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=inspection]').textContent"),'');
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=actions]').textContent"),'');
}));

test('a mismatched status receipt keeps custody and another actor cannot adopt its request',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{operations.show('activity');const facility=document.querySelector('[data-facility-ops=facility]');facility.value='synthetic-facility';facility.dispatchEvent(new Event('change'));await until(()=>!!document.querySelector('[data-facility-command=activity]'));document.querySelector('[data-facility-command=activity]').click();pick('port','port-1');pick('quantity','20');pick('evidence','Synthetic observed flow');pick('operationEffectiveAt','2032-03-14T08:00:00');document.querySelector('[data-facility-ops=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-facility-ops=record]'));lost=true;document.querySelector('[data-facility-ops=record]').click();await until(()=>errors.length===1);changedReceipt=true;document.querySelector('[data-facility-ops=status]').click();await until(()=>errors.length===2);})()");
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=pending]').hidden"),false);
 assert.match((await evaluate('errors'))[1],/original facility action/);
 await evaluate("identity='another-synthetic-operator';operations.reset();operations.load()");
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=pending]').hidden"),true);
 await evaluate("identity='synthetic-operator';operations.reset();operations.load()");
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=pending]').hidden"),false);
 assert.equal(await evaluate("calls.filter(call=>call.path==='facilities/operations/record').length"),1);
}));

test('a canonical preview cannot replace the selected existing physical facility',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{operations.show('activity');const facility=document.querySelector('[data-facility-ops=facility]');facility.value='synthetic-facility';facility.dispatchEvent(new Event('change'));await until(()=>!!document.querySelector('[data-facility-command=activity]'));document.querySelector('[data-facility-command=activity]').click();pick('port','port-1');pick('quantity','20');pick('evidence','Synthetic observed flow');pick('operationEffectiveAt','2032-03-14T08:00:00');changedPreviewFacility=true;document.querySelector('[data-facility-ops=editor]').requestSubmit();await until(()=>errors.length||document.querySelector('[data-facility-ops=record]'));})()");
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=record]')===null"),true);
 assert.match((await evaluate('errors'))[0],/exact facility request/);
}));

test('physical activity opens the backend-selected existing source ledger for contract allocations',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{operations.show('activity');const facility=document.querySelector('[data-facility-ops=facility]');facility.value='synthetic-facility';facility.dispatchEvent(new Event('change'));await until(()=>!!document.querySelector('[data-facility-allocation]'));document.querySelector('[data-facility-allocation]').click();})()");
 assert.deepEqual(await evaluate('openedLedgers'),['synthetic-source-ledger']);
 assert.equal(await evaluate("calls.filter(call=>call.path==='facilities/operations/record').length"),0);
}));

test('only backend-declared UTC table cells change display zone and keep their exact source text',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("displayZone='America/Chicago';reply.tables=[{label:'Declared activity coordinates',columns:['Observed at','Opaque source reference','Exact quantity'],rows:[['2032-03-14T08:00:00.000001Z','2032-03-14T08:00:00Z','1/3']],instantColumns:[0]}];operations.load()");
 const cells=await evaluate("[...document.querySelectorAll('[data-facility-ops=inspection] tbody td')].map(cell=>({text:cell.textContent,title:cell.title}))");
 assert.match(cells[0].text,/03:00:00\.000001 UTC-05:00/);
 assert.equal(cells[0].title,'2032-03-14T08:00:00.000001Z');
 assert.equal(cells[1].text,'2032-03-14T08:00:00Z','an ordinary source string must not be guessed to be a timestamp');
 assert.equal(cells[2].text,'1/3');
 await evaluate("displayZone='UTC';operations.refresh()");
 assert.match(await evaluate("document.querySelector('[data-facility-ops=inspection] tbody td').textContent"),/08:00:00\.000001 UTC/);
}));

test('knowledge summaries use readable local time and keep machine precision in the source title',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{reply.context.knownAt='2032-03-14T20:00:17.123456Z';displayZone='America/Chicago';await operations.load();})()");
 const context=await evaluate("({text:document.querySelector('[data-facility-ops=context]').textContent,title:document.querySelector('[data-facility-ops=context]').title})");
 assert.match(context.text,/Mar 14, 2032/);
 assert.match(context.text,/3:00\s*PM/);
 assert.match(context.text,/America\/Chicago/);
 assert.doesNotMatch(context.text,/123456|UTC-0[56]/);
 assert.equal(context.title,'2032-03-14T20:00:17.123456Z');
}));

test('the facility knowledge cut follows the selected user zone and retains its exact source instant',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 assert.match(await evaluate("document.querySelector('[data-facility-ops=context]').textContent"),/8:00\s*PM · UTC/);
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=context]').title"),'2032-03-14T20:00:00Z');
 const reads=await evaluate('calls.length');
 await evaluate("displayZone='America/Chicago';operations.refresh()");
 const context=await evaluate("({text:document.querySelector('[data-facility-ops=context]').textContent,title:document.querySelector('[data-facility-ops=context]').title})");
 assert.match(context.text,/Business date 2032-03-14/);
 assert.match(context.text,/3:00\s*PM · America\/Chicago/);
 assert.equal(context.title,'2032-03-14T20:00:00Z');
 assert.equal(await evaluate('calls.length'),reads,'a display preference does not replace the certified source');
}));

for(const knownAt of [undefined,null,'synthetic-invalid-instant'])test('facility reads withhold identities without a valid certified knowledge instant '+knownAt,browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate(`reply.context.knownAt=${JSON.stringify(knownAt)};operations.load().catch(error=>errors.push(error.message))`);
 assert.equal(await evaluate('errors.length'),1);
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=facility]').options.length"),1);
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=context]').textContent"),'');
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=context]').title"),'');
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=inspection]').textContent"),'');
}));

for(const changed of ['changedReadFacility','changedFormFacility'])test('facility inspection refuses '+changed+' instead of displaying another asset',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate(`(async()=>{window.${changed}=true;const facility=document.querySelector('[data-facility-ops=facility]');facility.value='synthetic-facility';facility.dispatchEvent(new Event('change'));await until(()=>errors.length||!facility.disabled);})()`);
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=inspection]').textContent"),'');
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=actions]').textContent"),'');
 assert.match((await evaluate('errors'))[0],/selected facility|exact facility/i);
}));

test('a checked absent receipt can dismiss a stale uncertain request locally after explicit confirmation',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{operations.show('activity');const facility=document.querySelector('[data-facility-ops=facility]');facility.value='synthetic-facility';facility.dispatchEvent(new Event('change'));await until(()=>!!document.querySelector('[data-facility-command=activity]'));document.querySelector('[data-facility-command=activity]').click();pick('port','port-1');pick('quantity','20');pick('evidence','Synthetic observed flow');pick('operationEffectiveAt','2032-03-14T08:00:00');document.querySelector('[data-facility-ops=editor]').requestSubmit();await until(()=>!!document.querySelector('[data-facility-ops=record]'));lost=true;document.querySelector('[data-facility-ops=record]').click();await until(()=>errors.length===1);})()");
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=dismiss]').disabled"),true);
 await evaluate("(async()=>{noReceipt=true;document.querySelector('[data-facility-ops=status]').click();await until(()=>!document.querySelector('[data-facility-ops=dismiss]').disabled);staleRetry=true;document.querySelector('[data-facility-ops=retry]').click();await until(()=>errors.length===2);})()");
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=pending]').hidden"),false);
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=dismiss]').disabled"),true,'sending the retry invalidates the earlier absence check');
 await evaluate("(async()=>{document.querySelector('[data-facility-ops=status]').click();await until(()=>!document.querySelector('[data-facility-ops=dismiss]').disabled);window.confirm=message=>{window.dismissWarning=message;return true;};document.querySelector('[data-facility-ops=dismiss]').click();await until(()=>document.querySelector('[data-facility-ops=pending]').hidden);})()");
 assert.match(await evaluate('dismissWarning'),/only local recovery/i);
 assert.match(await evaluate('dismissWarning'),/does not cancel|doesn't cancel/i);
 assert.match(await evaluate('dismissWarning'),/prove.*absen|prove.*no fact/i);
 assert.equal(await evaluate("calls.filter(call=>call.path==='facilities/operations/record').length"),2);
 assert.equal(await evaluate("calls.at(-1).path"),'facilities/operations/read','physical records refresh before another review is available');
}));
