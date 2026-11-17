import assert from 'node:assert/strict';
import {withOwnedBrowser} from './owned-browser.mjs';

const q=JSON.stringify,R='transport-routes',C='transport-capacity';
const selector=(consumer,name)=>`[data-${consumer}=${name}]`;
async function run(a){
 const {command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,fixture:f}=a;
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle,60000);await persona(f.profile);
 await evaluate(`window.routeCalls=[];window.routeSource=null;window.capacitySource=null;window.confirm=()=>true;{
  const send=window.fetch.bind(window);window.fetch=async(input,init)=>{const path=new URL(typeof input==='string'?input:input.url,location.href).pathname;
   const tracked=path.startsWith('/api/');
   const call=tracked?{path,body:init?.body?JSON.parse(init.body):null,status:null,result:null}:null;if(call)routeCalls.push(call);
   const response=await send(input,init);if(call){call.status=response.status;if(response.headers.get('content-type')?.includes('json'))call.result=await response.clone().json();}
   if(response.ok&&['/api/gas/transport/routes/directory','/api/gas/transport/routes/read'].includes(path))routeSource=call.result;
   if(response.ok&&['/api/gas/transport/capacity/directory','/api/gas/transport/capacity/read'].includes(path))capacitySource=call.result;
   if(response.ok&&path==='/api/workspace/navigation')window.routeNavigation=call.result;
   if(response.ok&&path==='/api/gas/transport/routes/record'&&window.routeLoseReply){routeLoseReply=false;throw new TypeError('Synthetic route acknowledgement lost after commit');}return response;
  };
 }`);
 await click('#profile-open');await set('#zone','UTC');await click('#profile-save',60000);await click('#nav-facilities',60000);
 await click('[data-facilities-view=route]',60000);
 const source=consumer=>evaluate(consumer===R?'routeSource':'capacitySource');
 const path=consumer=>consumer===R?'gas/transport/routes':'gas/transport/capacity';
 const latest=(consumer,kind)=>evaluate(`routeCalls.filter(call=>call.path===${q('/api/'+path(consumer)+'/'+kind)}).at(-1)`);
 async function operatingDate(day){
  await set('#date-context-on',day);await wait(idle,60000);
  await wait("!document.querySelector('#use-business-date-current').disabled",60000);await click('#use-business-date-current',60000);
  await check('the explicit route operating date needs no financial close, title account or physical facility read',`(()=>{const read=routeCalls.filter(call=>call.path==='/api/gas/transport/routes/directory').at(-1);return document.querySelector('#snapshot').value===''&&routeNavigation.context==null&&document.querySelector('#workspace-mode-label').textContent.includes('Working data')&&routeCalls.every(call=>!call.path.startsWith('/api/facilities/')&&!call.path.startsWith('/api/gas/owned/'))&&read.status===200&&read.result.historical===false&&read.body.context.effectiveOn===${q(day)}&&read.body.context.knowledgeMode==='current';})()`);
 }
 await check('the Route plans tab is independent from unauthorized physical, stock and financial entry',"!document.querySelector('[data-facilities-view=route]').disabled&&['definitions','activity','quantities'].every(view=>document.querySelector('[data-facilities-view='+view+']').disabled)&&document.querySelector('[data-transport-routes=source-search]')===null");
 await operatingDate(f.day);
 async function pick(host,name,value){
  await evaluate(`(()=>{const input=(${host}).querySelector('[name='+${q(name)}+']');if(!input)throw Error('Missing exact operation field '+${q(name)});input.value=${q(value)};if(input instanceof HTMLSelectElement&&input.value!==${q(value)})throw Error('Missing exact operation choice '+${q(name)});input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));})()`);
 }
 async function fill(host,fields,values){
  for(const field of fields){
   if(field.kind==='array'){
    const entries=values[field.name];assert.ok(Array.isArray(entries),'Missing explicit array '+field.name);await pick(host,field.name,entries.length?'entries':'empty');
    for(const [index,value]of entries.entries()){
     await evaluate(`(${host}).querySelector('[name='+${q(field.name)}+']').closest('.operation-field').querySelector(':scope > button').click()`);
     await fill(`(${host}).querySelectorAll('[data-operation-entry='+${q(field.name)}+']')[${index}]`,field.fields,value);
    }
   }else if(field.kind==='object'){
    assert.ok(Object.hasOwn(values,field.name),'Missing explicit nested terms '+field.name);
    if(field.nullable)await pick(host,field.name,values[field.name]===null?'none':'terms');
    if(values[field.name]!==null){
     const nested=`[...(${host}).children].find(label=>label.tagName==='LABEL'&&label.firstChild?.textContent===${q(field.label)}).querySelector(':scope > .operation-field > .schedule-fields')`;
     await fill(nested,field.fields,values[field.name]);
    }
   }else if(field.kind==='reference'){
    const empty=field.nullable&&field.members.every(name=>values[name]===null),choices=field.choices.filter(choice=>field.members.every(name=>JSON.stringify(choice.value[name])===JSON.stringify(values[name])));
    assert.ok(empty||choices.length===1,'Missing exact reference '+field.name);await pick(host,field.name,empty?'@null':choices[0].id);
   }else if(field.kind==='select'){
    const choices=field.choices.filter(choice=>JSON.stringify(choice.value??choice.id)===JSON.stringify(values[field.name]));assert.equal(choices.length,1,'Missing exact selection '+field.name);await pick(host,field.name,choices[0].id);
   }else{
    assert.ok(Object.hasOwn(values,field.name),'Missing explicit term '+field.name);await pick(host,field.name,field.kind==='instant'?values[field.name].replace(/(?:\.0+)?(?:Z|\+00:00)$/,''):String(values[field.name]));
   }
  }
 }
 async function review(consumer,kind,values,effectiveAt=f.effectiveAt,{refusal}={}){
  const button=consumer===R?'route-command':'transport-command',form=selector(consumer,'editor');
  await wait(`!!document.querySelector('[data-${button}=${kind}]')&&!document.querySelector('[data-${button}=${kind}]').disabled&&${idle}`,60000);
  const current=await source(consumer),metadata=current.configuration[kind]??current.forms[kind];await click(`[data-${button}=${kind}]`,60000);
  await check('described entry keeps every source, law and array selection explicit',`[...document.querySelectorAll(${q(form+' select[name]')})].every(input=>input.value==='')`);
  await fill(`document.querySelector(${q(form)})`,metadata.fields,values);await pick(`document.querySelector(${q(form)})`,'operationEffectiveAt',effectiveAt.replace('Z',''));
  await check('the authored operation contains valid local clocks and all required explicit choices',`[...document.querySelectorAll(${q(form+' input,'+form+' select')})].every(input=>input.disabled||input.checkValidity())`);
  if(consumer===R&&kind==='plan')await check('route source mappings contain no editable source quantity or stage clock',`!document.querySelector(${q(form+' [name=receiptQuantity],'+form+' [name=deliveryQuantity],'+form+' [name=receiptFrom],'+form+' [name=deliveryFrom]')})`);
  await submit(form,60000);const preview=await latest(consumer,'preview');assert.ok(preview,'The actual review endpoint was not called.');
  if(refusal){assert.ok(preview.status>=400&&preview.status<500,JSON.stringify(preview));assert.equal(preview.result.code,refusal);await check('invalid complete route mapping has no record control',`document.querySelector(${q(selector(consumer,'record'))})===null`);return preview;}
  assert.equal(preview.status,200,JSON.stringify(preview.result));await wait(`!!document.querySelector(${q(selector(consumer,'record'))})&&${idle}`,60000);
  assert.equal(preview.result.command.expectedStateVersion,preview.body.command.expectedStateVersion);return preview;
 }
 async function record(consumer,{lost=false}={}){
  if(lost)await evaluate('window.routeLoseReply=true');await click(selector(consumer,'record'),60000);const call=await latest(consumer,'record');assert.equal(call.status,200,JSON.stringify(call.result));
  if(lost){
   await check('a lost committed route response retains the reviewed original request',`!document.querySelector(${q(selector(R,'pending'))}).hidden`);await click(selector(R,'status'),60000);
   const status=await latest(R,'status');assert.equal(status.status,200);assert.deepEqual(status.body,{ownerId:call.body.ownerId,operationId:call.body.operationId});assert.deepEqual(status.result.command,call.body.command);
   await check('route status recovers exact original source selections without a second record',`routeCalls.filter(call=>call.path==='/api/gas/transport/routes/record'&&call.body.operationId===${q(call.body.operationId)}).length===1&&document.querySelector('[data-transport-routes=pending]').hidden`);
  }
  const key=consumer===R?'routeId':'entitlementId';await wait(`${consumer===R?'routeSource':'capacitySource'}?.${key}===${q(call.result[key])}&&${idle}`,60000);return call;
 }
 async function selectRoute(id){await set(selector(R,'route'),id);await wait(`routeSource?.routeId===${q(id)}&&!document.querySelector('[data-transport-routes=route]').disabled&&${idle}`,60000);}
 async function refreshRoute(id){await click('[data-facilities-view=route]',60000);await selectRoute(id);await click(selector(R,'refresh'),60000);await wait(`routeSource?.routeId===${q(id)}&&${idle}`,60000);}
 const definition=structuredClone(f.definition);delete definition.routeId;
 definition.nodes=definition.nodes.map(({nodeId,...row})=>row);
 definition.segments=definition.segments.map(({segmentId,fromNodeId,toNodeId,...row})=>row);
 const firstPreview=await review(R,'define',definition);await evaluate("document.querySelector('[data-transport-routes=editor]').scrollIntoView({block:'start'})");await shot('transport-route-definition');
 assert.ok(firstPreview.body.command.nodes.every(row=>!Object.hasOwn(row,'nodeId')));assert.ok(firstPreview.body.command.segments.every(row=>!Object.hasOwn(row,'segmentId')));
 const firstRecord=await record(R,{lost:true}),id=firstRecord.result.routeId;
 await check('saved graph geometry and explicit fuel, loss and transit are inspectable before the first adoption',"(()=>{const nodes=routeSource.tables.find(table=>table.label==='Saved route nodes'),segments=routeSource.tables.find(table=>table.label==='Saved immutable route segments'),ah=segments.rows.find(row=>row[0]==='ah'),text=document.querySelector('[data-transport-routes=inspection]').textContent;return nodes.rows.length===6&&segments.rows.length===7&&ah[5]==='1/50'&&ah[6]==='0/1'&&ah[7]==='3600000000'&&text.includes('synthetic_route_entry')&&text.includes('1/50')&&!text.includes('internal record')&&routeSource.facts.plans.length===0&&Object.keys(routeSource.configuration).length===0;})()");
 await evaluate("document.querySelector('[data-transport-routes=inspection]').scrollIntoView({block:'start'})");await shot('transport-route-graph');
 function plan(facts,key){
  const sources=[],sourcePlans=[],flows=[];
  for(const capacity of facts.sourceCapacities){
   sources.push({entitlementId:capacity.entitlement.id,capacityVersionId:capacity.version.id,capacityGeneration:capacity.stateVersion});
   for(const stream of capacity.sourcePlans)sourcePlans.push({entitlementId:capacity.entitlement.id,firstPlanRevisionId:stream.firstPlanRevisionId,planRevisionId:stream.planRevisionId,expectedMembershipGeneration:stream.membershipGeneration});
   for(const claim of capacity.claims){const segment=facts.segments.find(s=>s.entitlement_id===claim.entitlement_id&&s.path_id===claim.path_id);assert.ok(segment);flows.push({segmentId:segment.id,claimId:claim.id});}
  }
  return {businessKey:key,expectedPreviousId:facts.plans.find(p=>p.business_key===key)?.id??null,sources,sourcePlans,flows,evidence:'Synthetic complete conserving route over all explicitly selected native source streams'};
 }
 const values=plan((await source(R)).facts,'synthetic_browser_route_plan_a');assert.equal(values.sources.length,4);assert.equal(values.sourcePlans.length,4);assert.equal(values.flows.length,7);
 const hm=(await source(R)).facts.segments.find(s=>s.segment_key==='hm').id;
 await review(R,'plan',{...values,flows:values.flows.filter(flow=>flow.segmentId!==hm)},f.effectiveAt,{refusal:'gas_transport_route_source'});
 const duplicate=structuredClone(values);duplicate.flows[1]={...duplicate.flows[0]};await review(R,'plan',duplicate,f.effectiveAt,{refusal:'gas_transport_route_input'});
 const adoptedPreview=await review(R,'plan',values);
 await check('backend review shows one hundred receipts, two fuel and ninety-eight terminal delivery with the explicit idle segment',"(()=>{const tables=routeCalls.filter(call=>call.path==='/api/gas/transport/routes/preview').at(-1).result.tables,rows=tables.find(table=>table.label.endsWith('Segment quantities')).rows,ah=rows.find(row=>row[0]==='ah'),md=rows.find(row=>row[0]==='md'),hm=rows.find(row=>row[0]==='hm');return rows.length===7&&ah[1]==='100'&&ah[2]==='2'&&ah[3]==='0'&&ah[4]==='98'&&md[5]==='98'&&hm[1]==='0'&&hm[5]==='0'&&tables.find(table=>table.label.endsWith('Intermediate node balance')).rows.every(row=>row[6]==='0')&&document.querySelector('[data-transport-routes=review]').textContent.includes('conserve exactly');})()");
 await evaluate("document.querySelector('[data-transport-routes=review]').scrollIntoView({block:'start'})");await shot('transport-route-review');
 const adoptedRecord=await record(R,{lost:true});assert.deepEqual(adoptedRecord.body.command,adoptedPreview.result.command);
 await check('accepted route retains four exclusive streams and exactly seven native claim references',"routeSource.facts.memberships.length===4&&routeSource.facts.plans.length===1&&routeSource.facts.plans[0].flows.length===7&&routeSource.tables.some(table=>table.label.endsWith('Segment quantities'))");await shot('transport-route-current');
 async function post(consumer,kind,body){
  return evaluate(`(async()=>{const boot=await(await fetch('/api/bootstrap?includeFrames=false')).json(),response=await fetch(${q('/api/'+path(consumer)+'/'+kind)},{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify(${q(body)})});return {status:response.status,value:await response.json()};})()`);
 }
 async function acknowledge(previewCall,recordCall,{fresh=false}={}){
  const before=(await source(R)).facts,request=structuredClone(previewCall.body);if(fresh)request.operationId=await evaluate('crypto.randomUUID()');
  const preview=await post(R,'preview',request);assert.equal(preview.status,200,JSON.stringify(preview));assert.equal(preview.value.acknowledgement,true);assert.deepEqual(preview.value.tables,[]);
  assert.ok(JSON.stringify(preview.value.overviewSections).includes('Acknowledge an already recorded fact'));
  const recorded=await post(R,'record',{...recordCall.body,operationId:request.operationId});assert.equal(recorded.status,200,JSON.stringify(recorded));
  if(fresh)assert.equal(recorded.value.outcome.changed,false);else assert.deepEqual(recorded.value,recordCall.result);
  await click(selector(R,'refresh'),60000);const after=(await source(R)).facts;assert.equal(after.stateVersion,before.stateVersion);assert.deepEqual(after.plans,before.plans);assert.deepEqual(after.memberships,before.memberships);
  await check((fresh?'fresh semantic acknowledgement':'original-operation replay')+' preserves current heads and memberships without a synthetic zero assessment',`routeSource.facts.stateVersion===${q(before.stateVersion)}`);
 }
 await acknowledge(firstPreview,firstRecord,{fresh:true});await acknowledge(firstPreview,firstRecord);
 await set(selector(R,'route'),'');await wait(`!routeSource?.routeId&&${idle}`,60000);
 const secondDefinition=structuredClone(definition);secondDefinition.businessKey='synthetic_browser_route_b';await review(R,'define',secondDefinition);const secondRecord=await record(R),other=secondRecord.result.routeId;
 await check('another immutable route cannot adopt logical source streams already owned by the first route',"!Object.hasOwn(routeSource.forms,'plan')&&routeSource.facts.memberships.length===0&&routeSource.facts.plans.length===0");
 const occupied=await source(R),occupiedRequest={context:firstPreview.body.context,ownerId:occupied.ownerId,operationId:await evaluate('crypto.randomUUID()'),effectiveAt:f.effectiveAt,command:{kind:'plan',routeId:other,definitionId:occupied.facts.definition.id,expectedStateVersion:occupied.facts.stateVersion,admissionPolicy:'complete_current_sources_and_conserving_route_v1',...plan(occupied.facts,'synthetic_browser_route_plan_b')}};
 const denied=await post(R,'preview',occupiedRequest);assert.ok(denied.status>=400&&denied.status<500,JSON.stringify(denied));assert.equal(denied.value.code,'gas_transport_route_membership');
 const entry=(await source(R)).facts.sourceCapacities.find(c=>c.entitlement.business_key==='synthetic_route_entry');
 async function capacity(id){await click('[data-facilities-view=transport]',60000);await set(selector(C,'entitlement'),id);await wait(`capacitySource?.entitlementId===${q(id)}&&!document.querySelector('[data-transport-capacity=entitlement]').disabled&&${idle}`,60000);return (await source(C)).facts;}
 const oldCapacity=await capacity(entry.entitlement.id),head=oldCapacity.plans[0];
 const claims=oldCapacity.claims.map(c=>({pathId:c.path_id,receiptFrom:c.receipt_from,receiptTo:c.receipt_to,receiptQuantity:c.receipt_quantity,deliveryFrom:c.delivery_from,deliveryTo:c.delivery_to,deliveryQuantity:c.delivery_quantity}));
 await review(C,'plan',{businessKey:head.business_key,expectedPreviousId:head.id,claims,evidence:'Synthetic same-quantity source correction with a new native revision'});await record(C);
 await refreshRoute(id);
 await check('source correction withholds a current success while retaining graph, exact accepted claims and labeled historical witness',"(()=>{const text=document.querySelector('[data-transport-routes=inspection]').textContent;return routeSource.facts.memberships.length===4&&routeSource.tables.some(t=>t.label==='Saved immutable route segments')&&routeSource.tables.some(t=>t.label.includes('Recorded historical evidence')&&t.label.endsWith('Accepted source claims')&&t.rows.length===7)&&!routeSource.tables.some(t=>t.label.endsWith('Segment quantities'))&&Object.hasOwn(routeSource.forms,'plan')&&text.includes('Recorded historical evidence')&&!text.includes('complete route conserves exactly');})()");
 await evaluate("[...document.querySelectorAll('[data-transport-routes=inspection] h3')].find(heading=>heading.textContent.includes('Recorded historical evidence')).closest('section').scrollIntoView({block:'start'})");await shot('transport-route-historical-evidence');
 const revisedValues=plan((await source(R)).facts,values.businessKey);assert.notEqual(revisedValues.sourcePlans.find(p=>p.entitlementId===entry.entitlement.id).planRevisionId,values.sourcePlans.find(p=>p.entitlementId===entry.entitlement.id).planRevisionId);
 assert.equal(revisedValues.sourcePlans.find(p=>p.entitlementId===entry.entitlement.id).firstPlanRevisionId,values.sourcePlans.find(p=>p.entitlementId===entry.entitlement.id).firstPlanRevisionId);
 await review(R,'plan',revisedValues);await record(R);
 await check('explicit fresh re-adoption uses current source heads and restores a current conserving assessment',"routeSource.facts.plans[0].revision===2&&routeSource.facts.memberships.length===4&&routeSource.tables.some(t=>t.label.endsWith('Segment quantities'))&&!routeSource.tables.some(t=>t.label.includes('Recorded historical evidence'))");
 await operatingDate(f.catchupDay);const currentCapacity=await capacity(entry.entitlement.id);
 const limits=rows=>rows.map(row=>({from:row.from,through:row.through,maximumPerHour:row.maximum_per_hour}));
 const capacityValues={agreement:limits(currentCapacity.agreement).map(row=>({...row,maximumPerHour:'201'})),paths:currentCapacity.paths.map(p=>({pathId:p.id,pathKey:p.path_key,pipeline:p.pipeline_code,receiptZone:p.receipt_zone_code,receiptPoint:p.receipt_point_code,deliveryZone:p.delivery_zone_code,deliveryPoint:p.delivery_point_code,product:p.product_code,limits:limits(p.limits)})),receipts:currentCapacity.receipts.map(p=>({point:p.point_code,limits:limits(p.limits)})),deliveries:currentCapacity.deliveries.map(p=>({point:p.point_code,limits:limits(p.limits)})),evidence:'Synthetic future-effective complete capacity revision'};
 await review(C,'define',capacityValues,f.futureEffectiveAt);await record(C);
 await click('[data-facilities-view=route]',60000);await operatingDate(f.day);await selectRoute(id);
 await check('a current source that is future-effective withholds adoption but preserves explicit own release',"routeSource.facts.hasFutureHeads===false&&routeSource.facts.sourceCapacities.some(c=>c.entitlement.business_key==='synthetic_route_entry'&&c.hasFutureHeads===true&&c.version===null)&&!Object.hasOwn(routeSource.forms,'plan')&&Object.hasOwn(routeSource.forms,'release')&&routeSource.canRecord===true&&routeSource.tables.some(t=>t.label.includes('Recorded historical evidence'))");
 const beforeRelease=(await source(R)).facts,own=beforeRelease.plans[0];
 const release=await review(R,'release',{businessKey:own.business_key,expectedPreviousId:own.id,evidence:'Synthetic explicit release of only this own route-plan membership'});
 assert.deepEqual(release.result.tables,[]);await check('release review does not claim assessed zero flows or change source claims',"document.querySelector('[data-transport-routes=review]').textContent.includes('no route flow is assessed')");
 await evaluate("document.querySelector('[data-transport-routes=review]').scrollIntoView({block:'start'})");await shot('transport-route-release');await record(R);
 const afterRelease=(await source(R)).facts;
 const capacityFacts=facts=>facts.sourceCapacities.map(capacity=>Object.fromEntries(
  ['entitlement','version','paths','receipts','deliveries','agreement','plans','claims','stateVersion','hasFutureHeads'].map(key=>[key,capacity[key]])));
 assert.deepEqual(capacityFacts(afterRelease),capacityFacts(beforeRelease));
 await check('own release under a stale capacity source clears only memberships and leaves no assessed route',"routeSource.facts.plans[0].kind==='release'&&routeSource.facts.plans[0].revision===3&&routeSource.facts.memberships.length===0&&!routeSource.tables.some(t=>t.label.endsWith('Segment quantities')||t.label.includes('Recorded historical evidence'))&&document.querySelector('[data-transport-routes=inspection]').textContent.includes('no chosen route is currently assessed')");
 await operatingDate(f.catchupDay);await selectRoute(other);await review(R,'plan',plan((await source(R)).facts,'synthetic_browser_route_plan_b'),f.futureEffectiveAt);await record(R);
 await check('after explicit source catch-up the second route adopts the released complete streams',"routeSource.facts.memberships.length===4&&routeSource.facts.plans[0].revision===1&&routeSource.facts.plans[0].flows.length===7&&routeSource.tables.some(t=>t.label.endsWith('Segment quantities'))");
 await selectRoute(id);await acknowledge(adoptedPreview,adoptedRecord);await acknowledge(adoptedPreview,adoptedRecord,{fresh:true});
 await check('receipt-first replay of old accepted adoption does not reinstall released memberships',"routeSource.facts.plans[0].kind==='release'&&routeSource.facts.plans[0].revision===3&&routeSource.facts.memberships.length===0");
 await selectRoute(other);await click('#profile-open');await set('#zone','America/Chicago');await click('#profile-save',60000);
 await check('only backend-declared instant columns and the knowledge cut use the selected local zone',"[...document.querySelectorAll('[data-transport-routes=inspection] td')].some(cell=>cell.title.startsWith('2020-12-20T01:00:00')&&cell.textContent.includes('2020-12-19'))&&document.querySelector('[data-transport-routes=context]').title===routeSource.context.knownAt&&document.querySelector('[data-transport-routes=context]').textContent.includes('America/Chicago')&&document.querySelector('[data-transport-routes=inspection]').textContent.includes('3600000000')");
 await evaluate("[...document.querySelectorAll('[data-transport-routes=inspection] h3')].find(heading=>heading.textContent.endsWith('Delivery timing and rate')).closest('section').scrollIntoView({block:'start'})");await shot('transport-route-local-time');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await click('[data-route-command=plan]',60000);
 await pick("document.querySelector('[data-transport-routes=editor]')",'flows','entries');await evaluate("document.querySelector('[name=flows]').closest('.operation-field').querySelector(':scope > button').click();document.querySelector('[data-operation-entry=flows]').scrollIntoView({block:'start'})");
 await check('phone source mapping keeps the exact saved segment and native claim controls visible without copied financial terms',`${fitsWidth}&&document.querySelector('[data-operation-entry=flows] [name=segmentId]')&&document.querySelector('[data-operation-entry=flows] [name=claimId]')&&!document.querySelector('[data-transport-routes=editor] [name=receiptQuantity]')`);await shot('transport-route-mobile');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});await click('#as-of-context>summary');await click('#date-context-latest',60000);await click('#apply-view-context',60000);
 await wait(`routeSource?.historical===true&&!document.querySelector('[data-transport-routes=route]').disabled&&${idle}`,60000);await selectRoute(other);
 await check('retained Route inspection is read only under the selected exact graph and business context',`routeSource.routeId===${q(other)}&&routeSource.historical===true&&[...document.querySelectorAll('[data-route-command]')].every(button=>button.disabled)&&routeCalls.filter(call=>call.path==='/api/gas/transport/routes/read').at(-1).body.context.knowledgeMode==='retained'&&document.querySelector('#snapshot').value===''`);
 await check('the full route workflow has no hidden physical facility, owned-stock, financial ticket or price evaluation read',"routeCalls.every(call=>!call.path.startsWith('/api/facilities/')&&!call.path.startsWith('/api/gas/owned/')&&!call.path.startsWith('/api/tickets/')&&!['/api/evaluate','/api/history','/api/price-inspection'].includes(call.path))");
}

if(process.argv.length!==4)throw Error('Use the owned Transport route browser qualifier.');
console.log(`${await withOwnedBrowser(process.argv[2],process.argv[3],async driver=>{try{await run(driver);}catch(error){console.error('Owned route UI state',JSON.stringify(await driver.evaluate("({error:document.querySelector('#error')?.textContent,source:window.routeSource,capacity:window.capacitySource,invalid:[...document.querySelectorAll('[data-transport-routes=editor] :invalid,[data-transport-capacity=editor] :invalid')].map(input=>({name:input.name,value:input.value,message:input.validationMessage})),calls:window.routeCalls?.map(call=>({path:call.path,status:call.status,code:call.result?.code,kind:call.body?.command?.kind}))})")));throw error;}})} full-app Transport route browser checks passed.`);
