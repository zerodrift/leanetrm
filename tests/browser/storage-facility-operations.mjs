import {withOwnedBrowser} from './owned-browser.mjs';

const q=JSON.stringify,F='[data-facility-ops=editor]',I='[data-facility-ops=inspection]';
const form=`document.querySelector(${q(F)})`;
const row=(field,index)=>`${form}.querySelectorAll('[data-operation-entry=${field}]')[${index}]`;

async function run(a){
 const {command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,fixture:f}=a;
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle,60000);await persona(f.profile);
 await evaluate(`window.facilityCalls=[];window.facilitySource=null;{
  const send=window.fetch.bind(window);window.fetch=async(input,init)=>{
   const path=new URL(typeof input==='string'?input:input.url,location.href).pathname;
   const tracked=path.startsWith('/api/facilities/')||path==='/api/workspace/navigation',body=init?.body?JSON.parse(init.body):null;
   const call=tracked?{path,body,status:null,result:null,delivered:true}:null;if(call)facilityCalls.push(call);
   if(path==='/api/facilities/operations/record'&&window.storageUndelivered){window.storageUndelivered=false;call.delivered=false;throw new TypeError('Synthetic facility request not delivered');}
   const response=await send(input,init);if(call){call.status=response.status;if(response.headers.get('content-type')?.includes('json'))call.result=await response.clone().json();}
   if(response.ok&&['/api/facilities/operations/directory','/api/facilities/operations/read'].includes(path))facilitySource=call.result;
   if(response.ok&&path==='/api/workspace/navigation')window.lastFacilityNavigation=call.result;
   if(path==='/api/facilities/operations/record'&&response.ok&&window.storageLoseReply){window.storageLoseReply=false;throw new TypeError('Synthetic facility acknowledgement lost after commit');}
   return response;
  };
 }
 window.pick=(host,name,value)=>{const input=host.querySelector('[name="'+name+'"]');if(!input)throw Error('Missing facility field '+name);input.value=value;if(input instanceof HTMLSelectElement&&input.value!==value)throw Error('Missing exact facility choice '+name+' / '+value);input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));};
 window.operationForm=kind=>(kind==='define'?facilitySource.configuration:facilitySource.forms)[kind];
 window.lastFacilityRecord=kind=>facilityCalls.filter(call=>call.path==='/api/facilities/operations/record'&&call.body.command.kind===kind&&call.status===200).at(-1);
 window.confirm=()=>true;`);
 const pick=async(host,name,value)=>evaluate(`pick(${host},${q(name)},${q(value)})`);
 const choice=async(kind,path,predicate)=>evaluate(`(()=>{let field=operationForm(${q(kind)});for(const name of ${q(path)})field=field.fields.find(field=>field.name===name);const found=field.choices.filter(choice=>(${predicate})(choice.value));if(found.length!==1)throw Error('Expected one exact facility choice '+${q(path.join('/'))}+', found '+found.length);return found[0].id;})()`);
 const add=async(host,field)=>evaluate(`(()=>{const mode=${host}.querySelector('[name=${field}]');pick(${host},${q(field)},'entries');mode.parentElement.querySelector(':scope > button').click();})()`);
 const action=async kind=>{await wait(`!!document.querySelector('[data-facility-command=${kind}]')&&!document.querySelector('[data-facility-command=${kind}]').disabled&&${idle}`,60000);await click(`[data-facility-command=${kind}]`,60000);await wait(`!document.querySelector(${q(F)}).hidden&&${idle}`,60000);};
 const effective=async value=>pick(form,'operationEffectiveAt',value.replace('Z','').replace('+00:00',''));
 const preview=async()=>{await submit(F,60000);await wait(idle,60000);};
 const record=async()=>{await wait(`!!document.querySelector('[data-facility-ops=record]')&&!document.querySelector('[data-facility-ops=record]').disabled&&${idle}`,60000);await click('[data-facility-ops=record]',60000);};
 const selectFacility=async id=>{await set('[data-facility-ops=facility]',id);await wait(`facilitySource?.facilityId===${q(id)}&&${idle}`,60000);};
 const rawFields=async(host,values)=>{for(const [name,value]of Object.entries(values))await pick(host,name,value);};

 await click('#profile-open');await set('#zone','UTC');await click('#profile-save',60000);
 await click('#nav-facilities',60000);
 const operatingDate=async day=>{
  await check('Facilities exposes the normal operating business-date input',"document.querySelector('#date-context-on').type==='date'&&document.querySelector('#date-context-on').checkVisibility()");
  await set('#date-context-on',day);await wait(idle,60000);
  await check('the normal header offers an explicit current-knowledge operating date without a pricing close',"!!document.querySelector('#use-business-date-current')&&!document.querySelector('#use-business-date-current').hidden");
  await click('#use-business-date-current',60000);
  const section=await evaluate("document.querySelector('[data-facilities-view][aria-selected=true]').dataset.facilitiesView");
  if(!['quantities','definitions','activity'].includes(section))throw Error('Expected the selected physical Facilities section.');
  const path=section==='quantities'?'/api/facilities/quantities/directory':'/api/facilities/operations/directory',authority=section==='definitions'?'canConfigure':'canRecord';
  await check('working physical scope has no financial close or historical navigation context',`(()=>{const read=facilityCalls.filter(call=>call.path===${q(path)}).at(-1);return document.querySelector('#snapshot').value===''&&!!window.lastFacilityNavigation&&lastFacilityNavigation.context==null&&document.querySelector('#workspace-mode-label').textContent.includes('Working data')&&read.status===200&&read.result[${q(authority)}]===true&&read.body.context.effectiveOn===${q(day)}&&read.body.context.knowledgeMode==='current';})()`);
 };
 await operatingDate(f.definitionDay);await click('[data-facilities-view=definitions]',60000);await action('define');
 await check('native physical authoring has current authority without a pricing source',"document.querySelector('#snapshot').value===''&&facilitySource.historical===false&&facilitySource.canConfigure===true");
 await check('new physical definitions require every policy, port and ratchet explicitly',`['implementation','inventoryBasis','rateBasis','simultaneousFlow','lossTreatment','ports','ratchets'].every(name=>${form}.querySelector('[name="'+name+'"]').value==='')&&${form}.querySelector('[name=facilityId]')===null`);
 await pick(form,'facility',await choice('define',['facility'],`value=>value.facilityCode===${q(f.source.point)}&&value.commodity===${q(f.source.product)}`));
 await pick(form,'quantity',await choice('define',['quantity'],`value=>value===${q(f.source.quantityCode)}`));
 await pick(form,'clock',await choice('define',['clock'],`value=>value===${q(f.source.clock)}`));
 await rawFields(form,{minimum:'0',maximum:'1000',evidence:'Synthetic browser physical facility rules'});
 for(const [name,value]of Object.entries({implementation:'storage_total_physical_step_ratchets_v1',inventoryBasis:'total_physical_inventory_v1',rateBasis:'native_quantity_per_elapsed_hour_v1',simultaneousFlow:'separate_injection_withdrawal_v1',lossTreatment:'no_loss_v1'}))await pick(form,name,await choice('define',[name],`value=>value===${q(value)}`));
 for(const [index,point]of f.points.entries()){
  if(index===0)await add(form,'ports');else await evaluate(`${form}.querySelector('[name=ports]').parentElement.querySelector(':scope > button').click()`);
  await pick(row('ports',index),'connection',await choice('define',['ports','connection'],`value=>value.pipeline===${q(point.pipeline_code)}&&value.zone===${q(point.zone_code)}&&value.point===${q(point.point_code)}&&value.product===${q(point.product_code)}`));
  await pick(row('ports',index),'direction',index===0?'inject':'withdraw');await pick(row('ports',index),'contractDirection',index===0?'receive':'deliver');
 }
 for(const [index,band]of [{lower:'0',upper:'500',injectionPerHour:'100',withdrawalPerHour:'50'},{lower:'500',upper:'1000',injectionPerHour:'25',withdrawalPerHour:'100'}].entries()){
  if(index===0)await add(form,'ratchets');else await evaluate(`${form}.querySelector('[name=ratchets]').parentElement.querySelector(':scope > button').click()`);
  await rawFields(row('ratchets',index),band);
 }
 await effective(f.definitionAt);await preview();
 await check('physical definition preview resolves technical identities without publishing',"(()=>{const preview=facilityCalls.filter(call=>call.path==='/api/facilities/operations/preview').at(-1);return preview.status===200&&preview.result.command.ports.length===2&&preview.result.command.ratchets.length===2&&!facilityCalls.some(call=>call.path==='/api/facilities/operations/record');})()");
 await record();
 const asset=await evaluate("lastFacilityRecord('define').result.facilityId");
 await check('published rules retain two exact ports and no implicit physical opening',`facilitySource.facilityId===${q(asset)}&&facilitySource.facts.ports.length===2&&facilitySource.facts.opening===null&&document.querySelector(${q(I)}).textContent.includes('Unassessed')`);
 await evaluate(`(async()=>{
  const first=facilityCalls.find(call=>call.path==='/api/facilities/operations/preview'&&call.body.command.kind==='define');
  const versionId=facilitySource.facts.version.id,request=structuredClone(first.body);request.operationId=crypto.randomUUID();
  const boot=await(await fetch('/api/bootstrap?includeFrames=false')).json();
  const post=async(path,body)=>{const response=await fetch('/api/facilities/operations/'+path,{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify(body)});return{status:response.status,value:await response.json()};};
  const result=window.duplicateDefinition={operationId:request.operationId,originalOperationId:first.body.operationId,versionId,
   rawFields:request.command.facilityId===null&&request.command.ports.every(port=>!Object.hasOwn(port,'portId'))&&request.effectiveAt===first.body.effectiveAt};
  result.preview=await post('preview',request);if(result.preview.status!==200)return;
  result.record=await post('record',{...request,effectiveAt:result.preview.value.effectiveAt,command:result.preview.value.command});if(result.record.status!==200)return;
  result.directory=await post('directory',{context:request.context});
  result.read=await post('read',{context:request.context,facilityId:${q(asset)},sourceSearch:''});
 })()`);
 await check('a fresh request for the same physical definition returns its existing fact without another asset or version',`(()=>{const d=duplicateDefinition;return d.rawFields&&d.operationId!==d.originalOperationId&&d.preview.status===200&&d.preview.value.operationId===d.operationId&&d.preview.value.facilityId===${q(asset)}&&d.record?.status===200&&d.record.value.operationId===d.operationId&&d.record.value.facilityId===${q(asset)}&&d.record.value.outcome.changed===false&&d.record.value.outcome.versionId===d.versionId&&d.directory?.status===200&&d.directory.value.facilities.length===1&&d.directory.value.facilities[0].id===${q(asset)}&&d.read?.status===200&&d.read.value.facts.version.id===d.versionId&&d.read.value.facts.versionBundles.length===1;})()`,"({previewStatus:duplicateDefinition.preview?.status,previewCode:duplicateDefinition.preview?.value?.code,recordStatus:duplicateDefinition.record?.status,record:duplicateDefinition.record?.value})");

 await operatingDate(f.day);await click('[data-facilities-view=activity]',60000);await selectFacility(asset);
 await action('opening');await rawFields(form,{businessKey:'synthetic_browser_opening',previous:'@null',observedAt:f.planFrom.replace('Z',''),quantity:'400',evidence:'Synthetic dated physical level'});await effective(f.at);await preview();await record();
 const opening=await evaluate("lastFacilityRecord('opening').result.outcome.revisionId");
 const injection=await evaluate(`facilitySource.facts.ports.find(port=>port.point_code===${q(f.source.point)}&&port.direction==='inject').id`);
 await check('dated opening evidence records400 physical units without a commercial position',`facilitySource.facts.opening.id===${q(opening)}&&facilitySource.facts.opening.quantity==='400'&&facilitySource.facts.opening.observed_at.startsWith(${q(f.planFrom.slice(0,19))})`);

 await action('plan');await pick(form,'opening',opening);await rawFields(form,{from:f.planFrom.replace('Z',''),through:f.planThrough.replace('Z',''),coverageStatement:'chosen_plan_idle_between_activities_v1',evidence:'Synthetic complete chosen plan'});await add(form,'activities');
 await pick(row('activities',0),'port',await choice('plan',['activities','port'],`value=>value.portId===${q(injection)}`));await rawFields(row('activities',0),{from:f.planFrom.replace('Z',''),through:f.planThrough.replace('Z',''),quantity:'200'});await effective(f.at);await preview();
 await check('an endpoint600 plan refuses because its100/hour crosses the25/hour upper band',"(()=>{const call=facilityCalls.filter(call=>call.path==='/api/facilities/operations/preview').at(-1);return call.status>=400&&call.result.code==='storage_facility_infeasible'&&!document.querySelector('[data-facility-ops=record]')&&!facilityCalls.some(call=>call.path==='/api/facilities/operations/record'&&call.body.command.kind==='plan');})()","({error:document.querySelector('#error').textContent,last:facilityCalls.at(-1)})");
 await rawFields(row('activities',0),{through:f.planMiddle.replace('Z',''),quantity:'100'});await preview();
 await check('exact boundary arrival500 and explicit idle remainder are reviewed as feasible',"(()=>{const call=facilityCalls.filter(call=>call.path==='/api/facilities/operations/preview').at(-1);return call.status===200&&call.result.tables.some(table=>table.rows.some(row=>row.includes('500')))&&document.querySelector('[data-facility-ops=review]').textContent.includes('WithinLimit');})()");
 await shot('storage-facility-plan');await check('complete plan entry and review fit desktop width',fitsWidth);await record();

 const activity=async(key,linked)=>{
  if(linked){await set('[data-facility-ops=source-query]',f.source.day);await submit('[data-facility-ops=source-search]',60000);await check('reported flows are discovered through the exact selected-asset operating-day search',`facilitySource.sourceSearch===${q(f.source.day)}&&facilitySource.sourceChoicesTruncated===false&&facilityCalls.filter(call=>call.path==='/api/facilities/operations/read').at(-1).body.sourceSearch===${q(f.source.day)}`);}
  await action('activity');await rawFields(form,{businessKey:key,previous:'@null',from:f.planFrom.replace('Z',''),through:f.actualThrough.replace('Z',''),evidence:'Synthetic browser measured physical flow'});
  await pick(form,'port',await choice('activity',['port'],`value=>value.portId===${q(injection)}`));
  await pick(form,'quantity-disposition',linked?'none':'value');if(!linked)await pick(form,'quantity','700');
  await pick(form,'source',linked?f.source.sourceRevisionId:'@null');await effective(f.at);await preview();
 };
 await activity('synthetic_browser_linked_flow',true);await evaluate('window.storageLoseReply=true');await record();
 await check('a committed source-backed flow retains its original request after a lost acknowledgement',"!document.querySelector('[data-facility-ops=pending]').hidden&&lastFacilityRecord('activity').body.command.quantity===null");
 const linked=await evaluate("lastFacilityRecord('activity').result.outcome.revisionId");
 await click('[data-facility-ops=status]',60000);
 await check('status acknowledges the exact original source100 without another physical write',`(()=>{const status=facilityCalls.filter(call=>call.path==='/api/facilities/operations/status').at(-1),record=lastFacilityRecord('activity');return status.status===200&&status.body.operationId===record.body.operationId&&status.result.command.sourceRevisionId===${q(f.source.sourceRevisionId)}&&status.result.command.quantity===null&&facilityCalls.filter(call=>call.path==='/api/facilities/operations/record'&&call.body.command.kind==='activity').length===1&&document.querySelector('[data-facility-ops=pending]').hidden&&facilitySource.sourceSearch===${q(f.source.day)};})()`);
 await activity('synthetic_browser_measured_flow',false);await evaluate('window.storageUndelivered=true');await record();await click('[data-facility-ops=status]',60000);
 await check('an undelivered physical record has no receipt and keeps its exact retry',"!document.querySelector('[data-facility-ops=pending]').hidden&&facilityCalls.filter(call=>call.path==='/api/facilities/operations/status').at(-1).result.found===false");
 await click('[data-facility-ops=retry]',60000);
 const measured=await evaluate("lastFacilityRecord('activity').result.outcome.revisionId");
 await check('retry sends the exact original700-unit command once rather than minting another fact',"(()=>{const calls=facilityCalls.filter(call=>call.path==='/api/facilities/operations/record'&&call.body.command.businessKey==='synthetic_browser_measured_flow');return calls.length===2&&calls[0].delivered===false&&calls[1].status===200&&JSON.stringify(calls[0].body)===JSON.stringify(calls[1].body)&&document.querySelector('[data-facility-ops=pending]').hidden;})()");
 await check('measurements without explicit coverage remain unassessed rather than assuming idle gaps',"facilitySource.facts.coverage===null&&document.querySelector('[data-facility-ops=inspection]').textContent.includes('Unassessed')&&document.querySelector('[data-facility-ops=inspection]').textContent.includes('Unavailable')");

 const coverage=async(previous,statement)=>{
  await action('coverage');await rawFields(form,{businessKey:'synthetic_browser_reporting_window',previous:previous??'@null',opening,from:f.planFrom.replace('Z',''),through:f.actualThrough.replace('Z',''),statement,evidence:'Synthetic explicit physical reporting coverage'});
  await check('measured completeness requires an explicit exact physical rule version',`${form}.querySelector('[name=rules]').value===''`);
  await pick(form,'rules',await choice('coverage',['rules'],`value=>value.versionId===facilitySource.facts.version.id`));
  await add(form,'activityRevisionIds');await pick(row('activityRevisionIds',0),'id',linked);await evaluate(`${form}.querySelector('[name=activityRevisionIds]').parentElement.querySelector(':scope > button').click()`);await pick(row('activityRevisionIds',1),'id',measured);await effective(f.at);await preview();await record();
 };
 await coverage(null,'incomplete');const incomplete=await evaluate("lastFacilityRecord('coverage').result.outcome.revisionId");
 await check('an explicitly incomplete observed window keeps closing inventory unavailable',"facilitySource.facts.coverage.statement==='incomplete_observed_window_v1'&&document.querySelector('[data-facility-ops=inspection]').textContent.includes('Unassessed')&&document.querySelector('[data-facility-ops=inspection]').textContent.includes('Unavailable')");
 await coverage(incomplete,'complete');
 await check('complete measured evidence preserves1200 inventory and both physical breaches',"(()=>{const text=document.querySelector('[data-facility-ops=inspection]').textContent;return facilitySource.facts.activities.length===2&&facilitySource.facts.coverage.activityRevisionIds.length===2&&text.includes('1200')&&text.includes('Inventory above maximum')&&text.includes('Injection rate exceeded')&&facilitySource.facts.planAssessment.closing.n==='500';})()","({error:document.querySelector('#error').textContent,facts:facilitySource.facts,inspection:document.querySelector('[data-facility-ops=inspection]').textContent})");
 await shot('storage-facility-measured');
 await click(`[data-facility-allocation=${q(f.source.ledgerId)}]`,60000);
 await check('the linked activity opens its existing100/60/40 contract allocation ledger',`document.querySelector('#facility-ledger').value===${q(f.source.ledgerId)}&&document.querySelector('#facility-balance').textContent.includes('100')&&document.querySelector('#facility-balance').textContent.includes('60')&&document.querySelector('#facility-balance').textContent.includes('40')&&document.querySelector('#facility-allocation-table [data-facility-allocation]').value==='60'`);
 await click('[data-facilities-view=activity]',60000);await selectFacility(asset);
 await click('#profile-open');await set('#zone','America/Chicago');await click('#profile-save',60000);
 await check('declared source instants display in the selected user zone while keeping exact UTC evidence',"[...document.querySelectorAll('[data-facility-ops=inspection] tbody td')].some(cell=>cell.title.startsWith('2021-01-01T00:00:00')&&(cell.textContent.includes('Dec 31')||cell.textContent.includes('2020-12-31'))&&(cell.textContent.includes('CST')||cell.textContent.includes('UTC-06:00')))");
 await check('the certified facility knowledge cut follows the user zone and preserves its original instant',"(()=>{const context=document.querySelector('[data-facility-ops=context]');return context.title===facilitySource.context.knownAt&&!context.textContent.includes(facilitySource.context.knownAt)&&(context.textContent.includes('America/Chicago')||context.textContent.includes('CST'));})()");
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await click('#as-of-context>summary');await check('physical rules, operating-date actions, source links and assessments fit phone width',fitsWidth,"({width:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth,outside:[...document.querySelectorAll('header *,#facilities-page *')].filter(node=>{const box=node.getBoundingClientRect();return node.getClientRects().length&&(box.left<0||box.right>document.documentElement.clientWidth+1)}).map(node=>({tag:node.tagName,id:node.id,class:node.className,right:node.getBoundingClientRect().right,text:node.textContent.slice(0,120)})).slice(0,25)})");
 await evaluate("document.querySelector('#facilities-workspace-tabs').scrollIntoView({block:'start'})");await shot('storage-facility-mobile');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await click('#date-context-latest',60000);await click('#apply-view-context',60000);await click('[data-facilities-view=activity]',60000);await selectFacility(asset);
 await check('historical facility reads preserve their exact selected asset and disable all native authoring',`facilitySource.facilityId===${q(asset)}&&facilitySource.historical===true&&[...document.querySelectorAll('[data-facility-command]')].every(button=>button.disabled)&&document.querySelector('[data-facility-ops=inspection]').textContent.includes('1200')&&facilityCalls.filter(call=>call.path==='/api/facilities/operations/read').at(-1).body.context.knowledgeMode==='retained'`);
}

if(process.argv.length!==4)throw Error('Use the owned storage facility browser qualifier.');
console.log(`${await withOwnedBrowser(process.argv[2],process.argv[3],async driver=>{
 try{await run(driver);}catch(error){console.error('Owned storage facility UI state',JSON.stringify(await driver.evaluate("({error:document.querySelector('#error')?.textContent,context:document.querySelector('[data-facility-ops=context]')?.textContent,form:document.querySelector('[data-facility-ops=editor]')?.textContent,invalid:[...document.querySelectorAll('[data-facility-ops=editor] :invalid')].map(field=>({name:field.name,value:field.value,message:field.validationMessage})),calls:window.facilityCalls?.map(call=>({path:call.path,status:call.status,code:call.result?.code,kind:call.body?.command?.kind})),source:window.facilitySource})")));throw error;}
})} full-app storage facility browser checks passed.`);
