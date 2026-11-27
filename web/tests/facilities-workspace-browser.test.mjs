import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
const markup=(await readFile(new URL('../src/index.html',import.meta.url),'utf8')).replace(/<script\b[^>]*>[\s\S]*?<\/script>/g,'');

for(const transition of ['first load','identity reset'])test('Facilities waits for hydrated permissions before choosing its initial workspace: '+transition,{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(`(async()=>{
  window.calls=[];window.errors=[];window.rights=[];
  const context={contextId:'synthetic-context',effectiveOn:'2032-03-14',knownAt:'2032-03-14T20:00:00Z'};
  const api=async(path,body)=>{calls.push({path,body});if(path==='facilities/quantities/directory')return{ownerId:'opaque-owner',context,at:'2032-03-14T23:59:59Z',ledgers:[],choices:[],hasMore:false,choicesTruncated:false,canRecord:true,canConfigure:true};throw Error('Unexpected initial consumer '+path);};
  window.workspace=(await import('/facilities-workspace.js')).setupFacilities({api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=> 'UTC',identity:()=> 'synthetic-operator',context:()=>({effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'}),permission:name=>rights.includes(name),changed(){},tradeInspection:()=>null,openTrade:async()=>{}});
  rights=['ReadMarketData','ReadTrades','ReadFacilityQuantities','ReadGasOperations'];
  ${transition==='identity reset'?'workspace.reset();':''}await workspace.load().catch(error=>errors.push(error.message));
 })()`);
 assert.deepEqual(await evaluate('errors'),[]);
 assert.equal(await evaluate('workspace.section()'),'quantities');
 assert.deepEqual(await evaluate('calls.map(call=>call.path)'),['facilities/quantities/directory']);
 assert.equal(await evaluate("document.querySelector('[data-facilities-view=quantities]').getAttribute('aria-selected')"),'true');
}));

test('Facilities keeps existing quantity work and lazily opens physical definitions and activity',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async(evaluate,command)=>{
 await evaluate(`(async()=>{
  window.calls=[];window.errors=[];
  const context={contextId:'synthetic-context',effectiveOn:'2032-03-14',knownThrough:1,knownAt:'2032-03-14T20:00:00Z'};
  const api=async(path,body)=>{calls.push({path,body});if(path==='facilities/quantities/directory')return{ownerId:'opaque-owner',context,at:'2032-03-14T23:59:59Z',ledgers:[],choices:[],hasMore:false,choicesTruncated:false,canRecord:true,canConfigure:true};
   if(path==='facilities/operations/directory')return{ownerId:'opaque-owner',context,at:'2032-03-14T23:59:59Z',facilities:[],forms:{},configuration:{define:{label:'Publish facility definition',permission:'ConfigureFacilityLimits',base:{kind:'define',facilityId:null},fields:[{name:'evidence',label:'Definition evidence',kind:'text',nullable:false}]}},overviewSections:[],tables:[],canRecord:true,canConfigure:true,historical:false};throw Error('Unexpected '+path);};
  window.workspace=(await import('/facilities-workspace.js')).setupFacilities({api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=> 'UTC',identity:()=> 'synthetic-operator',context:()=>({effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'}),permission:()=>true,changed(){},tradeInspection:()=>null,openTrade:async()=>{}});
  await workspace.load();
 })()`);
 assert.deepEqual(await evaluate('calls.map(call=>call.path)'),['facilities/quantities/directory']);
 await evaluate("document.querySelector('[data-facilities-view=definitions]').click();new Promise(resolve=>setTimeout(resolve,30))");
 assert.deepEqual(await evaluate('calls.map(call=>call.path)'),['facilities/quantities/directory','facilities/operations/directory']);
 assert.equal(await evaluate("document.querySelector('#facility-quantities-workspace').hidden"),true);
 await evaluate("document.querySelector('[data-facility-command=define]').click();const evidence=document.querySelector('[name=evidence]');evidence.value='Synthetic unsaved rule';evidence.dispatchEvent(new Event('input',{bubbles:true}));window.confirm=()=>false;document.querySelector('[data-facilities-view=activity]').click();new Promise(resolve=>setTimeout(resolve,30))");
 assert.equal(await evaluate('workspace.section()'),'definitions');
 assert.equal(await evaluate("document.querySelector('[name=evidence]').value"),'Synthetic unsaved rule');
 assert.equal(await evaluate('calls.length'),2,'a refused move cannot read or mark the other workspace');
 assert.deepEqual(await evaluate('errors'),[]);
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await evaluate("document.querySelector('#facilities-page').hidden=false;document.querySelector('#market-page').hidden=true");
 const geometry=await evaluate("({width:document.documentElement.clientWidth,outside:[...document.querySelectorAll('[data-facilities-view]')].filter(button=>{const box=button.getBoundingClientRect();return box.left<0||box.right>document.documentElement.clientWidth+1}).map(button=>button.textContent)})");
 assert.deepEqual(geometry.outside,[],JSON.stringify(geometry));
}));

test('reloading Facilities exposes an original native request even when its selected source cannot be read',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(`(async()=>{
  const context={effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'},command={path:'facilities/operations/record',operationId:'synthetic-operation',label:'Original measured activity',body:{context,ownerId:'original-owner',effectiveAt:'2032-03-14T08:00:00Z',command:{kind:'activity',facilityId:'synthetic-facility'}},uncertain:true};
  sessionStorage.setItem('openetrm.facility.operations.request.synthetic-operator',JSON.stringify(command));window.errors=[];
  const api=async()=>{throw Object.assign(Error('Synthetic source unavailable'),{code:'access_denied'});};
  window.workspace=(await import('/facilities-workspace.js')).setupFacilities({api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=> 'UTC',identity:()=> 'synthetic-operator',context:()=>context,permission:()=>true,changed(){},tradeInspection:()=>null,openTrade:async()=>{}});
  await workspace.load().catch(error=>errors.push(error.message));workspace.sync();
 })()`);
 assert.equal(await evaluate('workspace.section()'),'activity');
 assert.equal(await evaluate("document.querySelector('#facility-operations-workspace').hidden"),false);
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=pending]').hidden"),false);
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=status]').disabled"),false);
 assert.equal(await evaluate("document.querySelector('[data-facility-ops=retry]').disabled"),false,'retry retains its own original source and native authority independently of a new read');
}));

test('a Transport-only reader opens entitlement capacity without hidden physical-facility reads',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(`(async()=>{
  window.calls=[];window.errors=[];
  const context={contextId:'synthetic-transport-context',effectiveOn:'2032-03-14',knownAt:'2032-03-14T20:00:00Z'};
  const api=async(path,body)=>{calls.push({path,body});if(path==='gas/transport/capacity/directory')return{ownerId:'opaque-transport-owner',context,at:'2032-03-14T23:59:59.999999Z',entitlements:[],forms:{},configuration:{},overviewSections:[],tables:[],canRecord:false,canConfigure:false,historical:false};throw Error('Unexpected hidden read '+path);};
  window.workspace=(await import('/facilities-workspace.js')).setupFacilities({api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=> 'UTC',identity:()=> 'synthetic-transport-reader',context:()=>({effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'}),permission:name=>['ReadMarketData','ReadTrades','ReadGasOperations'].includes(name),changed(){},tradeInspection:()=>null,openTrade:async()=>{}});
  await workspace.load().catch(error=>errors.push(error.message));
 })()`);
 assert.deepEqual(await evaluate('errors'),[]);
 assert.equal(await evaluate('workspace.section()'),'transport');
 assert.deepEqual(await evaluate('calls.map(call=>call.path)'),['gas/transport/capacity/directory']);
 assert.equal(await evaluate("document.querySelector('#transport-capacity-workspace').hidden"),false);
 assert.match(await evaluate("document.querySelector('[data-facilities-view=transport]').textContent"),/Capacity/);
 const denied=await evaluate("workspace.open('definitions').then(()=>'',error=>error.message)");
 assert.match(denied,/permission/i);
 assert.deepEqual(await evaluate('calls.map(call=>call.path)'),['gas/transport/capacity/directory']);
}));

test('changing the operating context preserves the selected Transport tab and reads only that consumer',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(`(async()=>{
  window.calls=[];window.errors=[];const context={contextId:'synthetic-context',effectiveOn:'2032-03-14',knownAt:'2032-03-14T20:00:00Z'};
  const api=async(path,body)=>{calls.push({path,body});if(path==='facilities/quantities/directory')return{ownerId:'owner',context,at:'2032-03-14T23:59:59Z',ledgers:[],choices:[],hasMore:false,choicesTruncated:false,canRecord:true,canConfigure:true};if(path==='gas/transport/capacity/directory')return{ownerId:'owner',context,at:'2032-03-14T23:59:59.999999Z',entitlements:[],forms:{},configuration:{},overviewSections:[],tables:[],canRecord:true,canConfigure:true,historical:false};throw Error('Unexpected '+path);};
  window.workspace=(await import('/facilities-workspace.js')).setupFacilities({api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=> 'UTC',identity:()=> 'operator',context:()=>({effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'}),permission:()=>true,changed(){},tradeInspection:()=>null,openTrade:async()=>{}});
  await workspace.load();document.querySelector('[data-facilities-view=transport]').click();await new Promise(resolve=>setTimeout(resolve,30));workspace.reset();await workspace.load();
 })()`);
 assert.equal(await evaluate('workspace.section()'),'transport');
 assert.deepEqual(await evaluate('calls.map(call=>call.path)'),['facilities/quantities/directory','gas/transport/capacity/directory','gas/transport/capacity/directory']);
 assert.deepEqual(await evaluate('errors'),[]);
}));

test('Route plans open and retain their operating context without hidden capacity or physical reads',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(`(async()=>{
  window.calls=[];window.errors=[];const context={contextId:'synthetic-route-context',effectiveOn:'2032-03-14',knownAt:'2032-03-14T20:00:00Z'};
  const api=async(path,body)=>{calls.push({path,body});if(path==='gas/transport/routes/directory')return{ownerId:'opaque-route-owner',context,at:'2032-03-14T23:59:59.999999Z',routes:[],forms:{},configuration:{},overviewSections:[],tables:[],canRecord:false,canConfigure:false,historical:false};throw Error('Unexpected hidden consumer '+path);};
  window.workspace=(await import('/facilities-workspace.js')).setupFacilities({api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=> 'UTC',identity:()=> 'synthetic-route-reader',context:()=>({effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'}),permission:name=>['ReadMarketData','ReadTrades','ReadGasOperations'].includes(name),changed(){},tradeInspection:()=>null,openTrade:async()=>{}});
  await workspace.open('route').catch(error=>errors.push(error.message));workspace.reset();await workspace.load().catch(error=>errors.push(error.message));
 })()`);
 assert.deepEqual(await evaluate('errors'),[]);
 assert.equal(await evaluate('workspace.section()'),'route');
 assert.deepEqual(await evaluate('calls.map(call=>call.path)'),['gas/transport/routes/directory','gas/transport/routes/directory']);
 assert.equal(await evaluate("document.querySelector('#transport-routes-workspace').hidden"),false);
 assert.match(await evaluate("document.querySelector('[data-facilities-view=route]').textContent"),/Route plans/);
}));

test('an unknown route request keeps its recovery and blocks new capacity writes after the route source is denied',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(`(async()=>{
  window.calls=[];window.errors=[];const context={effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'};
  sessionStorage.setItem('openetrm.transport.routes.request.synthetic-route-operator',JSON.stringify({path:'gas/transport/routes/record',operationId:'synthetic-original-operation',uncertain:true,body:{context,ownerId:'opaque-route-owner',effectiveAt:'2032-03-14T10:00:00Z',command:{kind:'plan',routeId:'synthetic-original-route'}}}));
  const api=async(path,body)=>{calls.push({path,body});if(path==='gas/transport/routes/directory')throw Object.assign(Error('Synthetic route source denied'),{code:'access_denied'});if(path==='gas/transport/capacity/directory')return{ownerId:'opaque-capacity-owner',context:{contextId:'synthetic-capacity-context',effectiveOn:'2032-03-14',knownAt:'2032-03-14T20:00:00Z'},at:'2032-03-14T23:59:59.999999Z',entitlements:[],configuration:{define:{label:'Define capacity',permission:'ConfigureGasServices',base:{kind:'define',entitlementId:null},fields:[{name:'evidence',label:'Evidence',kind:'text',nullable:false}]}},forms:{},overviewSections:[],tables:[],canRecord:true,canConfigure:true,historical:false};throw Error('Unexpected hidden read '+path);};
  window.workspace=(await import('/facilities-workspace.js')).setupFacilities({api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=> 'UTC',identity:()=> 'synthetic-route-operator',context:()=>context,permission:()=>true,changed(){},tradeInspection:()=>null,openTrade:async()=>{}});
  await workspace.load().catch(error=>errors.push(error.message));workspace.sync();
 })()`);
 assert.equal(await evaluate('workspace.section()'),'route');
 assert.equal(await evaluate("document.querySelector('[data-transport-routes=pending]').hidden"),false);
 assert.equal(await evaluate("document.querySelector('[data-transport-routes=status]').disabled"),false);
 await evaluate("workspace.open('transport')");
 assert.equal(await evaluate("document.querySelector('[data-transport-command=define]').disabled"),true);
 assert.equal(await evaluate("document.querySelector('[data-transport-routes=status]').disabled"),false);
 assert.deepEqual(await evaluate('calls.map(call=>call.path)'),['gas/transport/routes/directory','gas/transport/capacity/directory']);
}));

test('Facilities shows the tabs of the view its section belongs to',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(`(async()=>{
  window.errors=[];
  const context={contextId:'synthetic-context',effectiveOn:'2032-03-14',knownAt:'2032-03-14T20:00:00Z'};
  const api=async path=>{if(path==='facilities/quantities/directory')return{ownerId:'opaque-owner',context,at:'2032-03-14T23:59:59Z',ledgers:[],choices:[],hasMore:false,choicesTruncated:false,canRecord:true,canConfigure:true};throw Error('Unexpected '+path);};
  window.workspace=(await import('/facilities-workspace.js')).setupFacilities({api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=> 'UTC',identity:()=> 'synthetic-operator',context:()=>({effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'}),permission:()=>true,changed(){},tradeInspection:()=>null,openTrade:async()=>{}});
  await workspace.load();
  window.tabs=()=>[...document.querySelectorAll('[data-facilities-view]')].filter(b=>!b.hidden).map(b=>b.dataset.facilitiesView);
  window.picks=()=>[...document.querySelector('#facilities-section-picker').options].map(o=>o.value);
 })()`);
 assert.equal(await evaluate('workspace.view()'),'facilities');
 assert.deepEqual(await evaluate('tabs()'),['quantities','activity','trades','definitions']);
 assert.deepEqual(await evaluate('picks()'),['quantities','activity','trades','definitions']);
 assert.equal(await evaluate("document.querySelector('#facilities-workspace-tabs').hidden"),false);
 await evaluate("workspace.prepareView('facilities-transport')");
 assert.equal(await evaluate('workspace.section()'),'gas-delivery');
 assert.equal(await evaluate('workspace.view()'),'facilities-transport');
 assert.deepEqual(await evaluate('tabs()'),['gas-delivery','route','transport']);
 await evaluate("workspace.prepareView('facilities-position')");
 assert.equal(await evaluate('workspace.section()'),'outlook');
 assert.equal(await evaluate("document.querySelector('#facilities-workspace-tabs').hidden"),true,'one section needs no tab row');
 assert.equal(await evaluate("document.querySelector('#facilities-section-picker').hidden"),true);
 assert.equal(await evaluate("workspace.viewAvailable('facilities-crude')"),true);
 assert.deepEqual(await evaluate('errors'),[]);
}));
