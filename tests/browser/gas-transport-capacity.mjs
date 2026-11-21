import assert from 'node:assert/strict';
import {withOwnedBrowser} from './owned-browser.mjs';

const q=JSON.stringify,F='[data-transport-capacity=editor]',I='[data-transport-capacity=inspection]';
async function run(a){
 const {command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,fixture:f}=a;
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle,60000);await persona(f.profile);
 await evaluate(`window.capacityCalls=[];window.capacitySource=null;window.confirm=()=>true;{
  const send=window.fetch.bind(window);window.fetch=async(input,init)=>{const path=new URL(typeof input==='string'?input:input.url,location.href).pathname;
   const tracked=path.startsWith('/api/gas/transport/capacity/')||path.startsWith('/api/facilities/')||path.startsWith('/api/tickets/')||path==='/api/workspace/navigation';
   const call=tracked?{path,body:init?.body?JSON.parse(init.body):null,status:null,result:null}:null;if(call)capacityCalls.push(call);
   const response=await send(input,init);if(call){call.status=response.status;if(response.headers.get('content-type')?.includes('json'))call.result=await response.clone().json();}
   if(response.ok&&['/api/gas/transport/capacity/directory','/api/gas/transport/capacity/read'].includes(path))capacitySource=call.result;
   if(response.ok&&path==='/api/workspace/navigation')window.capacityNavigation=call.result;
   if(response.ok&&path==='/api/gas/transport/capacity/record'&&window.capacityLoseReply){capacityLoseReply=false;throw new TypeError('Synthetic capacity acknowledgement lost after commit');}return response;
  };
 }`);
 await click('#profile-open');await set('#zone','UTC');await click('#profile-save',60000);await click('#nav-facilities',60000);
 const source=()=>evaluate('capacitySource');
 const latest=kind=>evaluate(`capacityCalls.filter(call=>call.path==='/api/gas/transport/capacity/${kind}').at(-1)`);
 const operatingDate=async day=>{
  await set('#date-context-on',day);await wait(idle,60000);
  await wait("!document.querySelector('#use-business-date-current').disabled",60000);await click('#use-business-date-current',60000);
  await check('the explicit capacity business date uses current knowledge without a financial close or hidden facility read',`(()=>{const read=capacityCalls.filter(call=>call.path==='/api/gas/transport/capacity/directory').at(-1);return document.querySelector('#snapshot').value===''&&!!window.capacityNavigation&&capacityNavigation.context==null&&document.querySelector('#workspace-mode-label').textContent.includes('Working data')&&capacityCalls.every(call=>!call.path.startsWith('/api/facilities/'))&&read.status===200&&read.result.historical===false&&read.body.context.effectiveOn===${q(day)}&&read.body.context.knowledgeMode==='current';})()`);
 };
 await check('Transport-only authority opens capacity without facility, stock or booking controls',"!document.querySelector('[data-facilities-view=transport]').disabled&&['definitions','activity','quantities'].every(view=>document.querySelector('[data-facilities-view='+view+']').disabled)&&document.querySelector('[data-transport-capacity=source-search]')===null");
 await operatingDate(f.day);
 async function pick(host,name,value){
  await evaluate(`(()=>{const input=(${host}).querySelector('[name='+${q(name)}+']');if(!input)throw Error('Missing exact capacity field '+${q(name)});input.value=${q(value)};if(input instanceof HTMLSelectElement&&input.value!==${q(value)})throw Error('Missing exact capacity choice '+${q(name)});input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));})()`);
 }
 async function fill(host,fields,values){
  for(const field of fields){
   if(field.kind==='array'){
    const entries=values[field.name];if(!Array.isArray(entries))throw Error('Missing explicit capacity array '+field.name);await pick(host,field.name,entries.length?'entries':'empty');
    for(const [index,value]of entries.entries()){
     await evaluate(`(${host}).querySelector('[name='+${q(field.name)}+']').closest('.operation-field').querySelector(':scope > button').click()`);
     await fill(`(${host}).querySelectorAll('[data-operation-entry='+${q(field.name)}+']')[${index}]`,field.fields,value);
    }
   }else if(field.kind==='reference'){
    const empty=field.nullable&&field.members.every(name=>values[name]===null),choice=field.choices.filter(choice=>field.members.every(name=>JSON.stringify(choice.value[name])===JSON.stringify(values[name])));
    if(!empty&&choice.length!==1)throw Error('Missing exact capacity reference '+field.name);await pick(host,field.name,empty?'@null':choice[0].id);
   }else if(field.kind==='select'){
    const choices=field.choices.filter(choice=>JSON.stringify(choice.value??choice.id)===JSON.stringify(values[field.name]));if(choices.length!==1)throw Error('Missing exact capacity selection '+field.name);await pick(host,field.name,choices[0].id);
   }else{
    if(!Object.hasOwn(values,field.name))throw Error('Missing explicit capacity term '+field.name);
    await pick(host,field.name,field.kind==='instant'?values[field.name].replace('Z',''):String(values[field.name]));
   }
  }
 }
 async function review(kind,values,effectiveAt=f.effectiveAt,{refusal=false}={}){
  await wait(`!!document.querySelector('[data-transport-command=${kind}]')&&!document.querySelector('[data-transport-command=${kind}]').disabled&&${idle}`,60000);
  const metadata=await evaluate(`(capacitySource.configuration[${q(kind)}]??capacitySource.forms[${q(kind)}])`);await click(`[data-transport-command=${kind}]`,60000);
  await check('capacity entry leaves every reference, policy and array choice explicit',`[...document.querySelectorAll(${q(F+' select[name]')})].every(input=>input.value==='')`);
  await fill(`document.querySelector(${q(F)})`,metadata.fields,values);await pick(`document.querySelector(${q(F)})`,'operationEffectiveAt',effectiveAt.replace('Z',''));
  await submit(F,60000);const preview=await latest('preview');
  if(refusal){
   assert.equal(preview.status,422);assert.equal(preview.result.code,'gas_transport_capacity_exceeded');
   await check('complete-scope capacity admission refuses an infeasible replacement without clipping or recording claims',"document.querySelector('[data-transport-capacity=record]')===null");return preview;
  }
  assert.equal(preview.status,200,JSON.stringify(preview.result));await wait(`!!document.querySelector('[data-transport-capacity=record]')&&${idle}`,60000);
  assert.equal(preview.result.command.expectedStateVersion,preview.body.command.expectedStateVersion);
  await check('capacity review shows backend assessment without recording physical transport or a financial trade',`document.querySelector('[data-transport-capacity=review]').textContent.includes('Transport capacity')&&${fitsWidth}`);
  return preview;
 }
 async function record({lost=false}={}){
  if(lost)await evaluate('window.capacityLoseReply=true');await click('[data-transport-capacity=record]',60000);
  const call=await latest('record');assert.equal(call.status,200,JSON.stringify(call.result));
  if(lost){
   await check('a lost committed capacity acknowledgement retains its original request',"!document.querySelector('[data-transport-capacity=pending]').hidden");await click('[data-transport-capacity=status]',60000);
   const status=await latest('status');assert.equal(status.status,200);assert.deepEqual(status.body,{ownerId:call.body.ownerId,operationId:call.body.operationId});assert.deepEqual(status.result.command,call.body.command);
   await check('capacity status recovers the original canonical command without a second write',`capacityCalls.filter(call=>call.path==='/api/gas/transport/capacity/record'&&call.body.operationId===${q(call.body.operationId)}).length===1&&document.querySelector('[data-transport-capacity=pending]').hidden`);
  }
  await wait(`capacitySource?.entitlementId===${q(call.result.entitlementId)}&&${idle}`,60000);return call;
 }
 const definition=structuredClone(f.definition);delete definition.entitlementId;
 const first=await review('define',definition);await shot('transport-capacity-definition');
 await evaluate("document.querySelector('[data-transport-capacity=review]').scrollIntoView({block:'start'})");await shot('transport-capacity-review');
 const admitted=await record({lost:true}),id=admitted.result.entitlementId;
 await check('the new entitlement retains two exact paths and no chosen claims',`capacitySource.facts.paths.length===2&&capacitySource.facts.plans.length===0&&capacitySource.facts.claims.length===0&&capacitySource.entitlementId===${q(id)}`);
 assert.deepEqual(first.result.command.paths.map(row=>row.pathKey).sort(),['synthetic_path_a','synthetic_path_d']);
 const path=async key=>(await source()).facts.paths.find(row=>row.path_key===key).id;
 const claim=async(key,receipt,delivery)=>({pathId:await path(key),receiptFrom:'2020-12-20T00:00:00Z',receiptTo:'2020-12-20T01:00:00Z',receiptQuantity:receipt,deliveryFrom:'2020-12-20T01:00:00Z',deliveryTo:'2020-12-20T02:00:00Z',deliveryQuantity:delivery});
 async function plan(key,claims,{refusal=false,lost=false,effectiveAt=f.effectiveAt}={}){
  const current=await source(),previous=current.facts.plans.find(row=>row.business_key===key)?.id??null;
  const preview=await review('plan',{businessKey:key,expectedPreviousId:previous,claims,evidence:'Synthetic complete chosen capacity claims'},effectiveAt,{refusal});
  if(refusal)return;
  assert.equal(preview.body.command.claims.length,claims.length);
  for(const [index,expected]of claims.entries()){
   const entered=preview.body.command.claims[index];assert.equal(entered.pathId,expected.pathId);assert.equal(entered.receiptQuantity,expected.receiptQuantity);assert.equal(entered.deliveryQuantity,expected.deliveryQuantity);
   for(const field of ['receiptFrom','receiptTo','deliveryFrom','deliveryTo'])assert.equal(new Date(entered[field]).toISOString(),new Date(expected[field]).toISOString());
  }
  const result=await record({lost});assert.deepEqual(result.body.command,preview.result.command);return result;
 }
 await plan('synthetic_browser_plan_a',[await claim('synthetic_path_a','60','50')]);
 await plan('synthetic_browser_plan_b',[await claim('synthetic_path_d','50','40')],{refusal:true});
 await plan('synthetic_browser_plan_b',[await claim('synthetic_path_d','40','30')],{lost:true});
 await check('alternate paths share the exact parent boundary while retaining unequal independent stage quantities',"capacitySource.facts.plans.length===2&&capacitySource.facts.claims.length===2&&capacitySource.tables.some(table=>table.rows.some(row=>row[0]==='Agreement'&&row[4]==='100'&&row[5]==='100'))");
 await check('capacity inspection names each exact path and its independent contributing plans',"(()=>{const table=capacitySource.tables.find(table=>table.label==='Gross capacity by interval'),paths=table.rows.filter(row=>row[0]==='Path'),text=document.querySelector('[data-transport-capacity=inspection]').textContent;return table.columns[7]==='Chosen plans'&&JSON.stringify([...new Set(paths.map(row=>row[1]))].sort())===JSON.stringify(['synthetic_path_a','synthetic_path_d'])&&table.rows.some(row=>row[0]==='Agreement'&&row[4]==='100'&&row[7]==='synthetic_browser_plan_a, synthetic_browser_plan_b')&&paths.some(row=>row[1]==='synthetic_path_a'&&row[4]==='60'&&row[7]==='synthetic_browser_plan_a')&&paths.some(row=>row[1]==='synthetic_path_d'&&row[4]==='40'&&row[7]==='synthetic_browser_plan_b')&&['synthetic_path_a','synthetic_path_d','synthetic_browser_plan_a','synthetic_browser_plan_b'].every(label=>text.includes(label))&&!text.includes('internal record')&&capacitySource.facts.paths.every(path=>/^[0-9a-f-]{36}$/.test(path.id))&&capacitySource.facts.claims.every(claim=>/^[0-9a-f-]{36}$/.test(claim.id));})()");await shot('transport-capacity-plan');
 const beforeAcknowledgement=await source();await set('[data-transport-capacity=entitlement]','');
 await wait(`!capacitySource?.entitlementId&&!document.querySelector('[data-transport-capacity=entitlement]').disabled&&${idle}`,60000);
 const duplicate=await review('define',definition);assert.notEqual(duplicate.body.operationId,first.body.operationId);assert.deepEqual(duplicate.body.command,first.body.command);assert.equal(new Date(duplicate.body.effectiveAt).toISOString(),new Date(first.body.effectiveAt).toISOString());
 await check('a fresh duplicate definition reviews its existing acknowledgement without inventing zero chosen capacity',"(()=>{const text=document.querySelector('[data-transport-capacity=review]').textContent.toLowerCase(),preview=capacityCalls.filter(call=>call.path==='/api/gas/transport/capacity/preview').at(-1).result;return preview.acknowledgement===true&&text.includes('already recorded')&&text.includes('acknowledg')&&!text.includes('within the saved limits')&&preview.tables.length===0;})()","(()=>{const preview=capacityCalls.filter(call=>call.path==='/api/gas/transport/capacity/preview').at(-1).result;return{acknowledgement:preview.acknowledgement,overview:preview.overviewSections,tables:preview.tables};})()");
 await evaluate("document.querySelector('[data-transport-capacity=review]').scrollIntoView({block:'start'})");await shot('transport-capacity-acknowledgement');
 const acknowledged=await record();assert.equal(acknowledged.result.outcome.changed,false);assert.equal(acknowledged.result.entitlementId,id);assert.equal(acknowledged.result.outcome.versionId,beforeAcknowledgement.facts.version.id);
 const afterAcknowledgement=await source();assert.equal(afterAcknowledgement.facts.stateVersion,beforeAcknowledgement.facts.stateVersion);assert.deepEqual(afterAcknowledgement.facts.plans,beforeAcknowledgement.facts.plans);assert.deepEqual(afterAcknowledgement.facts.claims,beforeAcknowledgement.facts.claims);
 await check('an acknowledged duplicate definition preserves both independent current claims',"capacitySource.facts.claims.length===2&&capacitySource.tables.some(table=>table.rows.some(row=>row[0]==='Agreement'&&row[4]==='100'&&row[5]==='100'))");
 await evaluate(`(async()=>{
  const firstPreview=capacityCalls.find(call=>call.path==='/api/gas/transport/capacity/preview'),firstRecord=capacityCalls.find(call=>call.path==='/api/gas/transport/capacity/record'&&call.body.operationId===firstPreview.body.operationId);
  const boot=await(await window.workspaceFetch('/api/bootstrap?includeFrames=false')).json();
  window.capacityOriginalReplay={firstRequest:structuredClone(firstPreview.body),recordRequest:structuredClone(firstRecord.body),firstOutcome:structuredClone(firstRecord.result)};
  const response=await window.workspaceFetch('/api/gas/transport/capacity/preview',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify(capacityOriginalReplay.firstRequest)});
  capacityOriginalReplay.preview={status:response.status,value:await response.json()};
 })()`);
 await check('the original accepted request also reviews as an acknowledgement after later plans',"(()=>{const {status,value}=capacityOriginalReplay.preview,text=JSON.stringify(value.overviewSections).toLowerCase();return status===200&&value.operationId===capacityOriginalReplay.firstRequest.operationId&&value.acknowledgement===true&&text.includes('already recorded')&&text.includes('acknowledg')&&value.tables.length===0;})()","capacityOriginalReplay.preview");
 await evaluate(`(async()=>{const boot=await(await window.workspaceFetch('/api/bootstrap?includeFrames=false')).json(),response=await window.workspaceFetch('/api/gas/transport/capacity/record',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify(capacityOriginalReplay.recordRequest)});capacityOriginalReplay.record={status:response.status,value:await response.json()};})()`);
 const originalReplay=await evaluate('capacityOriginalReplay');assert.equal(originalReplay.record.status,200);assert.equal(originalReplay.firstOutcome.outcome.changed,true);assert.deepEqual(originalReplay.record.value,originalReplay.firstOutcome);
 await click('[data-transport-capacity=refresh]',60000);const afterOriginalReplay=await source();assert.equal(afterOriginalReplay.facts.stateVersion,beforeAcknowledgement.facts.stateVersion);assert.deepEqual(afterOriginalReplay.facts.plans,beforeAcknowledgement.facts.plans);assert.deepEqual(afterOriginalReplay.facts.claims,beforeAcknowledgement.facts.claims);
 await check('exact replay returns its first changed outcome while current chosen capacity remains one hundred',"capacitySource.tables.some(table=>table.rows.some(row=>row[0]==='Agreement'&&row[4]==='100'&&row[5]==='100'))");
 await plan('synthetic_browser_plan_a',[await claim('synthetic_path_a','50','45')]);await plan('synthetic_browser_plan_a',[]);
 await check('explicit empty replacement releases only its own plan and retains the other plan',"(()=>{const a=capacitySource.facts.plans.find(plan=>plan.business_key==='synthetic_browser_plan_a'),b=capacitySource.facts.plans.find(plan=>plan.business_key==='synthetic_browser_plan_b');return a.revision===3&&b.revision===1&&capacitySource.facts.claims.length===1&&capacitySource.facts.claims[0].plan_id===b.id&&capacitySource.facts.claims[0].receipt_quantity==='40';})()");
 async function revise(maximum,effectiveAt){
  const current=await source(),values=structuredClone(f.definition);
  values.paths=values.paths.map(row=>({...row,pathId:current.facts.paths.find(path=>path.path_key===row.pathKey).id}));
  values.agreement=values.agreement.map(row=>({...row,maximumPerHour:maximum}));values.evidence='Synthetic complete revised capacity limits';
  await review('define',values,effectiveAt);await record();
 }
 await revise('35',f.effectiveAt);
 await check('a lower capacity revision retains accepted claims and visibly reports their breach',"capacitySource.facts.claims.length===1&&capacitySource.facts.claims[0].receipt_quantity==='40'&&document.querySelector('[data-transport-capacity=inspection]').textContent.includes('Capacity exceeded')");await shot('transport-capacity-breach');
 await plan('synthetic_browser_plan_b',[await claim('synthetic_path_d','30','20')]);
 await operatingDate(f.catchupDay);await set('[data-transport-capacity=entitlement]',id);await wait(`capacitySource.entitlementId===${q(id)}&&${idle}`,60000);await revise('60',f.futureEffectiveAt);
 await operatingDate(f.day);await set('[data-transport-capacity=entitlement]',id);await wait(`capacitySource.entitlementId===${q(id)}&&${idle}`,60000);
 await check('a source-known future head has no earlier-version fallback and withholds all new admission',"capacitySource.facts.hasFutureHeads===true&&capacitySource.facts.version===null&&capacitySource.canRecord===false&&capacitySource.canConfigure===false&&document.querySelector('[data-transport-capacity=inspection]').textContent.includes('future-effective')&&document.querySelectorAll('[data-transport-command]').length===0");await shot('transport-capacity-future');
 await operatingDate(f.catchupDay);await set('[data-transport-capacity=entitlement]',id);await wait(`capacitySource.entitlementId===${q(id)}&&${idle}`,60000);
 await plan('synthetic_browser_plan_c',[await claim('synthetic_path_d','10','5')],{effectiveAt:f.futureEffectiveAt});
 await check('the explicit later business context admits a new plan against the complete surviving claims',"capacitySource.facts.hasFutureHeads===false&&capacitySource.facts.plans.length===3&&capacitySource.facts.claims.length===2&&capacitySource.tables.some(table=>table.rows.some(row=>row[0]==='Agreement'&&row[4]==='40'&&row[5]==='60'))");
 await operatingDate(f.day);await set('[data-transport-capacity=entitlement]','');
 const releasing=structuredClone(f.definition);releasing.businessKey='synthetic_browser_transport_release';releasing.agreement=releasing.agreement.map(row=>({...row,maximumPerHour:'120'}));
 await review('define',releasing);await record();
 await plan('synthetic_browser_release_a',[await claim('synthetic_path_a','60','60')]);await plan('synthetic_browser_release_b',[await claim('synthetic_path_d','60','40')]);
 await revise('50',f.effectiveAt);
 await check('a lower limit retains both independent sixty-unit claims as a capacity breach',"capacitySource.facts.claims.length===2&&capacitySource.facts.claims.every(claim=>claim.receipt_quantity==='60')&&document.querySelector('[data-transport-capacity=inspection]').textContent.includes('Capacity exceeded')");
 await plan('synthetic_browser_release_a',[],{refusal:true});
 async function release(key){
  const current=await source(),previous=current.facts.plans.find(plan=>plan.business_key===key);assert.ok(previous);
  const preview=await review('release',{businessKey:key,expectedPreviousId:previous.id,evidence:'Synthetic explicit release of only this identified plan'});
  assert.equal(preview.body.command.kind,'plan');assert.equal(preview.body.command.admissionPolicy,'explicit_empty_release_v1');assert.deepEqual(preview.body.command.claims,[]);await record();
 }
 await release('synthetic_browser_release_a');
 await check('explicit release clears only A while B sixty remains a truthful breach against fifty',"(()=>{const b=capacitySource.facts.plans.find(plan=>plan.business_key==='synthetic_browser_release_b');return capacitySource.facts.claims.length===1&&capacitySource.facts.claims[0].plan_id===b.id&&capacitySource.facts.claims[0].receipt_quantity==='60'&&document.querySelector('[data-transport-capacity=inspection]').textContent.includes('Capacity exceeded');})()");
 await release('synthetic_browser_release_b');await check('explicit release of B leaves no chosen claims without creating physical performance',"capacitySource.facts.claims.length===0&&capacitySource.facts.plans.every(plan=>plan.revision===2)");
 await operatingDate(f.catchupDay);await set('[data-transport-capacity=entitlement]',id);await wait(`capacitySource.entitlementId===${q(id)}&&${idle}`,60000);
 await click('#profile-open');await set('#zone','America/Chicago');await click('#profile-save',60000);
 await check('capacity table instants and the knowledge cut follow the chosen zone while exact UTC evidence is retained',"[...document.querySelectorAll('[data-transport-capacity=inspection] td')].some(cell=>cell.title.startsWith('2020-12-20T00:00:00')&&cell.textContent.includes('2020-12-19'))&&document.querySelector('[data-transport-capacity=context]').title===capacitySource.context.knownAt&&document.querySelector('[data-transport-capacity=context]').textContent.includes('America/Chicago')");
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await click('[data-transport-command=define]',60000);
 await pick(`document.querySelector(${q(F)})`,'paths','entries');await evaluate(`document.querySelector(${q(F)}).querySelector('[name=paths]').closest('.operation-field').querySelector(':scope > button').click()`);
 const phonePath=`document.querySelector(${q(F)}).querySelector('[data-operation-entry=paths]')`;await pick(phonePath,'limits','entries');await evaluate(`(${phonePath}).querySelector('[name=limits]').closest('.operation-field').querySelector(':scope > button').click();(${phonePath}).scrollIntoView({block:'start'})`);
 await check('nested exact path and dated-capacity entry fits the phone viewport without invented values',`${fitsWidth}&&[...document.querySelectorAll(${q(F+' [data-operation-entry=paths] input')})].every(input=>input.value==='')`);await shot('transport-capacity-mobile');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});await click('#as-of-context>summary');await click('#date-context-latest',60000);await click('#apply-view-context',60000);
 await wait(`capacitySource?.historical===true&&!document.querySelector('[data-transport-capacity=entitlement]').disabled&&${idle}`,60000);
 await set('[data-transport-capacity=entitlement]',id);await wait(`capacitySource?.entitlementId===${q(id)}&&capacitySource.historical===true&&${idle}`,60000);
 await check('historical capacity inspection remains read only under the exact selected entitlement',`capacitySource.entitlementId===${q(id)}&&capacitySource.historical===true&&[...document.querySelectorAll('[data-transport-command]')].every(button=>button.disabled)&&capacityCalls.filter(call=>call.path==='/api/gas/transport/capacity/read').at(-1).body.context.knowledgeMode==='retained'`);
 await click('[data-facilities-view=outlook]',60000);await wait("document.querySelector('#outlook-read')&&"+idle);
 await set('#outlook-from','2020-12-19');await set('#outlook-to','2020-12-22');await click('#outlook-read');
 const picker='select[aria-label="Transport entitlement"]';await wait(`!!document.querySelector(${q(picker+' option[value="'+id+'"]')})&&`+idle);
 await set(picker,id);await wait("document.querySelector('#facilities-workspace-content').innerText.includes('Native capacity intervals')&&"+idle,60000);
 await set('#outlook-from','2021-01-01');await set('#outlook-to','2021-01-02');await click('#outlook-read');
 await wait("document.querySelector('#facilities-workspace-content').innerText.includes('Native capacity intervals')&&"+idle);
 await check('outlook shows the native time interval, rate headroom and chosen plan for the independently selected historical entitlement',
   "['Chosen / hour','Maximum / hour','Remaining / hour','Chosen plans'].every(label=>document.querySelector('#facilities-workspace-content').innerText.includes(label))");

}

if(process.argv.length!==4)throw Error('Use the owned Transport capacity browser qualifier.');
console.log(`${await withOwnedBrowser(process.argv[2],process.argv[3],async driver=>{try{await run(driver);}catch(error){console.error('Owned capacity UI state',JSON.stringify(await driver.evaluate("({error:document.querySelector('#error')?.textContent,source:window.capacitySource,invalid:[...document.querySelectorAll('[data-transport-capacity=editor] :invalid')].map(input=>({name:input.name,value:input.value,message:input.validationMessage})),calls:window.capacityCalls?.map(call=>({path:call.path,status:call.status,code:call.result?.code,kind:call.body?.command?.kind}))})")));throw error;}})} full-app Transport capacity browser checks passed.`);
