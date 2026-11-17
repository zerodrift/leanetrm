import assert from 'node:assert/strict';
import {writeFile} from 'node:fs/promises';
import {withOwnedBrowser} from './owned-browser.mjs';

const q=JSON.stringify, gas='delivery/workbench/gas-transport', crude='delivery/workbench/crude-receipt', batch=gas+'/nominations';
async function run(a){
 const {command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,fixture:f}=a;
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle,60000);await persona(f.profile);
 await evaluate(`window.deliveryCalls=[];{
  const send=window.fetch.bind(window);window.fetch=async(input,init)=>{const path=new URL(typeof input==='string'?input:input.url,location.href).pathname;
   const response=await send(input,init);if(path.startsWith('/api/')){const call={path,body:init?.body?JSON.parse(init.body):null,status:response.status};
    if(response.headers.get('content-type')?.includes('json'))call.result=await response.clone().json();deliveryCalls.push(call);
    if(response.ok&&path==='/api/${gas}/read')window.deliveryGas=call.result;if(response.ok&&path==='/api/${crude}/read')window.deliveryCrude=call.result;
   }if(response.ok&&path==='/api/${gas}/record'&&window.deliveryLoseReply){deliveryLoseReply=false;throw new TypeError('Synthetic committed delivery reply lost');}if(response.ok&&path==='/api/${batch}/record'&&window.deliveryLoseBatch){deliveryLoseBatch=false;throw new TypeError('Synthetic committed batch reply lost');}return response;};
  window.deliveryApi=async(path,body)=>{const bootstrap=await (await send('/api/bootstrap')).json();const response=await fetch('/api/'+path,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':bootstrap.csrfToken},body:JSON.stringify(body)});return {status:response.status,value:await response.json()};};
 }`);
 async function api(path,body,expected=200){
  const result=await evaluate(`deliveryApi(${q(path)},${body===undefined?'undefined':q(body)})`);
  assert.ok((Array.isArray(expected)?expected:[expected]).includes(result.status),`${path}: ${result.status}: ${JSON.stringify(result.value).slice(0,1500)}`);return result.value;
 }
 const id=()=>crypto.randomUUID();
 const workspace=await api('workspace');
 const frame=at=>{const matches=workspace.frames.filter(frame=>workspace.initialFrameIds.includes(frame.id)&&Date.parse(frame.at)===Date.parse(at));assert.equal(matches.length,1,'One exact synthetic input frame');return matches[0].id;};
 const financial={snapshotId:frame('2020-12-18T16:00:00Z'),convention:'physical_gas_fixed_quantity_v4',knowledgeMode:'current'};
 const operational={...financial,snapshotId:frame('2021-01-15T08:00:00Z')};
 const business={effectiveOn:f.day,knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'};
 const catalog=await api('gas/catalog',financial), targets=[];
 for(const [index,direction,point] of [[0,'receive','synthetic_gas_point_a'],[1,'deliver','synthetic_gas_point_b']]){
  const location=catalog.locations.find(row=>row.deliveryPoint===point);assert.ok(location,'Executable native point '+point);
  const booked=await api('gas/composition/book',{...financial,...f.parties,operationId:id(),bookId:f.bookId,
   definitionRevisionId:location.definitionRevisionId,gasSetupId:catalog.gasSetupId,discounting:false,profile:'fixed_gas_composition_v1',
   physicalSides:[{code:'delivery',pipeline:location.pipeline,zone:location.zone,deliveryPoint:location.deliveryPoint,product:location.product,
    direction,deliveryFrom:'2021-01-01',deliveryTo:'2021-01-02',quantityBasis:'per_operating_day',quantity:'100',link:null,calendarPolicy:f.physicalCalendar}],
   cashSides:[{code:'consideration',role:'consideration',direction:direction==='receive'?'pay':'receive',source:'delivery',stage:'delivery',price:'3',tariffCode:null,paymentAt:'2021-02-02T00:00:00Z',calendarPolicy:f.cashCalendar}]});
  const quantity=await api('gas/quantities/read',{context:operational,tradeId:booked.tradeId,selectionPolicy:'operational_projection_v1'});
  assert.equal(quantity.rows.length,1);const obligation=quantity.rows[0];
  assert.equal(obligation.day,'2021-01-01');assert.equal(new Date(obligation.start).toISOString(),'2021-01-01T00:00:00.000Z');assert.equal(new Date(obligation.end).toISOString(),'2021-01-02T00:00:00.000Z');
  const preview={context:operational,tradeId:booked.tradeId,expectedRevision:1,day:obligation.day,kind:'service',details:{obligationId:obligation.obligationId,holderRole:'legal_entity',
   externalReference:'synthetic-delivery-service-'+index,expectedPreviousId:null,validFrom:'2021-01-01',validTo:'2021-02-01',active:true,
   statementPolicy:'final_statement_balance_v1',termsReference:'Synthetic manual service agreement',evidence:'Synthetic exact native delivery service'}};
  const review=await api('gas/operations/preview',preview);
  const configured=await api('gas/operations/configure',{preview,operationId:id(),previewHash:review.previewHash});
  const state=await api('gas/operations/read',{context:operational,tradeId:booked.tradeId,day:obligation.day});
  const account=state.state.accounts.find(row=>row.id===configured.accountId);assert.ok(account);
  targets.push({nodeId:index===0?f.receiptNode:f.deliveryNode,obligationId:obligation.obligationId,tradeRevision:1,day:obligation.day,quantity:'100',tradeId:booked.tradeId,account});
 }
 const directory=await api(gas+'/directory',{context:business});assert.ok(directory.scopes.some(scope=>scope.id===f.routeId));
 const empty=await api(gas+'/read',{context:business,scopeId:f.routeId});
 assert.equal(empty.assessment.profile,'gas_transport_interval_v1');assert.ok(empty.assessment.activities.every(row=>row.geometry.kind==='interval'&&typeof row.quantity==='string'));
 assert.equal(empty.assessment.measures.find(row=>row.code==='external_receipt').value,'100');assert.equal(empty.assessment.measures.find(row=>row.code==='external_delivery').value,'100');
 console.log('PASS actual HTTP bookings: opposite-direction whole native gas intervals, eligible services and exact shared route assessment');
 let historical={...business,knowledgeMode:'retained',retained:empty.context};
 let allocation={context:business,ownerId:empty.ownerId,operationId:id(),effectiveAt:f.effectiveAt,command:{kind:'allocate',scopeId:f.routeId,routePlanId:f.routePlanId,
  expectedPreviousId:null,expectedRouteStateVersion:f.routeStateVersion,policy:'exact_terminal_native_interval_v1',
  allocations:targets.map(({nodeId,obligationId,tradeRevision,day,quantity})=>({nodeId,obligationId,tradeRevision,day,quantity})),evidence:'Synthetic exact whole-day terminal allocation'}};
 await api(gas+'/preview',{...allocation,command:{...allocation.command,allocations:[{...allocation.command.allocations[0],quantity:'101'},allocation.command.allocations[1]]}},[409,422]);
 await api(gas+'/preview',{...allocation,command:{...allocation.command,allocations:allocation.command.allocations.map(row=>({...row,nodeId:f.receiptNode}))}},[409,422]);
 await click('#profile-open');await set('#zone','UTC');await click('#profile-save',60000);await click('#nav-facilities',60000);
 await set('#date-context-on',f.day);await wait(idle,60000);await wait("!document.querySelector('#use-business-date-current').disabled",60000);await click('#use-business-date-current',60000);
 await click('[data-facilities-view=gas-delivery]',60000);await set('[data-delivery-workbench-gas=scope]',f.routeId);
 await wait(`deliveryGas?.scopeId===${q(f.routeId)}&&${idle}`,60000);
 async function pick(host,name,value){await evaluate(`(()=>{const input=(${host}).querySelector('[name='+${q(name)}+']');if(!input)throw Error('Missing delivery field '+${q(name)});input.value=${q(value)};if(input instanceof HTMLSelectElement&&input.value!==${q(value)})throw Error('Missing delivery choice');input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));})()`);}
 async function fill(host,fields,values){for(const field of fields){
  if(field.kind==='array'){const entries=values[field.name];await pick(host,field.name,entries.length?'entries':'empty');for(const [index,value] of entries.entries()){
   await evaluate(`(${host}).querySelector('[name='+${q(field.name)}+']').closest('.operation-field').querySelector(':scope > button').click()`);
   await fill(`(${host}).querySelectorAll('[data-operation-entry='+${q(field.name)}+']')[${index}]`,field.fields,value);
  }}else if(field.kind==='reference'){const choices=field.choices.filter(choice=>field.members.every(name=>JSON.stringify(choice.value[name])===JSON.stringify(values[name])));assert.equal(choices.length,1,'Explicit exact delivery reference '+field.name);await pick(host,field.name,choices[0].id);
  }else{assert.ok(Object.hasOwn(values,field.name),'Explicit delivery term '+field.name);await pick(host,field.name,String(values[field.name]));}
 }}
 const metadata=await evaluate('deliveryGas.forms.allocate');await click('[data-delivery-command=allocate]',60000);
 await check('all native reference selections begin explicit and empty',"[...document.querySelectorAll('[data-delivery-workbench-gas=editor] select')].every(input=>input.value==='')");
 await fill("document.querySelector('[data-delivery-workbench-gas=editor]')",metadata.fields,allocation.command);
 await pick("document.querySelector('[data-delivery-workbench-gas=editor]')",'operationEffectiveAt',f.effectiveAt.replace('Z',''));
 await submit('[data-delivery-workbench-gas=editor]',60000);await wait(`!!document.querySelector('[data-delivery-workbench-gas=record]')&&${idle}`,60000);
 const uiPreview=await evaluate(`deliveryCalls.filter(call=>call.path==='/api/${gas}/preview').at(-1)`);assert.equal(uiPreview.status,200,JSON.stringify(uiPreview.result));allocation=uiPreview.body;
 await evaluate('window.deliveryLoseReply=true');await click('[data-delivery-workbench-gas=record]',60000);
 const uiRecord=await evaluate(`deliveryCalls.filter(call=>call.path==='/api/${gas}/record').at(-1)`);assert.equal(uiRecord.status,200,JSON.stringify(uiRecord.result));
 await check('a lost committed reply keeps the original delivery command pending',"!document.querySelector('[data-delivery-workbench-gas=pending]').hidden");
 await click('[data-delivery-workbench-gas=status]',60000);await check('delivery status reconciles one original request without a duplicate record',`document.querySelector('[data-delivery-workbench-gas=pending]').hidden&&deliveryCalls.filter(call=>call.path==='/api/${gas}/record'&&call.body.operationId===${q(allocation.operationId)}).length===1`);
 const allocated=uiRecord.result;const replayed=await api(gas+'/record',allocation);
 assert.deepEqual(replayed.outcome,allocated.outcome,'Original request retry returns its first native outcome');assert.deepEqual(replayed.command,allocated.command);assert.equal(replayed.acknowledgement,true);
 const status=await api(gas+'/status',{ownerId:empty.ownerId,operationId:allocation.operationId});assert.deepEqual(status.command,allocated.command);
 await api(gas+'/record',{...allocation,command:{...allocation.command,evidence:'Synthetic conflicting retry'}},409);
 await api(gas+'/record',{...allocation,operationId:id(),command:{...allocation.command,evidence:'Synthetic stale predecessor revision'}},[409,422]);
 const current=await api(gas+'/read',{context:business,scopeId:f.routeId});
 const allocationId=allocated.outcome.allocationPlanId;
 assert.equal(current.nominationBatch.allocationPlanId,allocationId);assert.equal(current.nominationBatch.entries.length,2);assert.ok(current.nominationBatch.entries.every(entry=>entry.accounts.length===1));
 assert.ok(allocationId,'Allocation receipt exposes exact plan identity');
 const selectedHistory=await api('market/context',{effectiveOn:f.day,knownAt:empty.context.knownAt,mode:'at',selection:'latest_correction'});
 historical={...historical,retained:selectedHistory.context};
 const retained=await api(gas+'/read',{context:historical,scopeId:f.routeId});assert.equal(retained.historical,true);assert.equal(retained.canRecord,false);
 assert.deepEqual(retained.assessment,empty.assessment,'Retained sources must preserve the original exact movement assessment');
 await api(gas+'/record',{...allocation,operationId:id(),context:historical},[409,422]);
 await api('market/current',{});console.log('PASS actual delivery allocation replay, stale predecessor, conflicting request and retained context refusal');
 function batchRequest(prefix){return {operationId:id(),ownerId:empty.ownerId,input:{context:operational,allocationPlanId:allocationId,profile:'complete_allocation_nomination_batch_v1',
  entries:targets.map((target,index)=>({obligationId:target.obligationId,tradeId:target.tradeId,expectedRevision:1,day:target.day,
   details:{accountId:target.account.id,serviceVersionId:target.account.version_id,externalReference:prefix+(index===0?'first':'second'),expectedPreviousId:null,
    quantity:'100',submittedAt:'2020-12-31T12:00:00Z',channel:'portal',reason:'Synthetic manually submitted request',evidence:'Synthetic reviewed operator submission'}}))}};}
 const failed=batchRequest('synthetic-delivery-rollback-');
 // The second canonical child is faulted by the disposable fixture after the
 // first child has written, independent of browser or managed calculation logic.
 const sorted=[...failed.input.entries].sort((x,y)=>x.obligationId.localeCompare(y.obligationId));
 sorted[0].details.externalReference='synthetic-delivery-rollback-first';sorted[1].details.externalReference='synthetic-delivery-rollback-second';
 const failedReview=await api(batch+'/preview',failed);const faulted=await api(batch+'/record',{...failed,previewHash:failedReview.previewHash},[400,409,422]);assert.equal(faulted.code,'gas_operations_input','The deliberate second native insert must be reached before rollback');
 const failedStatus=await api(batch+'/status',{operationId:failed.operationId,ownerId:empty.ownerId});assert.equal(failedStatus.found,false);
 for(const target of targets){const state=await api('gas/operations/read',{context:operational,tradeId:target.tradeId,day:target.day});assert.equal(state.state.nominations.length,0,'No first child survives rollback');}
 console.log('PASS actual second-child nomination fault rolls back both native requests and batch receipt');
 let nomination=batchRequest('synthetic-delivery-success-');
 await api(batch+'/preview',{...nomination,input:{...nomination.input,entries:nomination.input.entries.slice(0,1)}},422);
 await set('#snapshot',operational.snapshotId);await wait(idle,60000);await click('[data-facilities-view=gas-delivery]',60000);
 await set('[data-delivery-workbench-gas=scope]',f.routeId);await wait(`deliveryGas?.nominationBatch?.allocationPlanId===${q(allocationId)}&&${idle}`,60000);
 await wait("!!document.querySelector('[data-delivery-nominations=form]')&&!document.querySelector('[data-delivery-nominations=form] fieldset').disabled",60000);
 const batchSource=await evaluate('deliveryGas.nominationBatch');
 for(const [index,entry] of batchSource.entries.entries()){
  await set(`[data-delivery-nominations=form] [name=account_${index}]`,entry.accounts[0].id);await set(`[data-delivery-nominations=form] [name=reference_${index}]`,'synthetic-delivery-success-'+index);
 }
 await set('[data-delivery-nominations=form] [name=submittedAt]','2020-12-31T12:00:00');await set('[data-delivery-nominations=form] [name=channel]','portal');
 await set('[data-delivery-nominations=form] [name=reason]','Synthetic manually submitted request');await set('[data-delivery-nominations=form] [name=evidence]','Synthetic reviewed operator submission');
 await submit('[data-delivery-nominations=form]',60000);await wait(`!!document.querySelector('[data-delivery-nominations=record]')&&${idle}`,60000);
 const batchPreview=await evaluate(`deliveryCalls.filter(call=>call.path==='/api/${batch}/preview').at(-1)`);assert.equal(batchPreview.status,200,JSON.stringify(batchPreview.result));
 const nominationReview=batchPreview.result;nomination={operationId:batchPreview.body.operationId,ownerId:batchPreview.body.ownerId,input:nominationReview.input};
 await evaluate('window.deliveryLoseBatch=true');await click('[data-delivery-nominations=record]',60000);
 const batchRecord=await evaluate(`deliveryCalls.filter(call=>call.path==='/api/${batch}/record').at(-1)`);assert.equal(batchRecord.status,200,JSON.stringify(batchRecord.result));
 await check('a lost committed batch reply retains one exact reviewed request',"!document.querySelector('[data-delivery-nominations=pending]').hidden");await click('[data-delivery-nominations=status]',60000);
 await check('batch status reconciles without a second submission',`document.querySelector('[data-delivery-nominations=pending]').hidden&&deliveryCalls.filter(call=>call.path==='/api/${batch}/record'&&call.body.operationId===${q(nomination.operationId)}).length===1`);
 const recorded=batchRecord.result;
 assert.deepEqual(await api(batch+'/record',{...nomination,previewHash:nominationReview.previewHash}),recorded,'Original atomic batch receipt survives replay');
 const recovered=await api(batch+'/status',{operationId:nomination.operationId,ownerId:empty.ownerId});assert.equal(recovered.found,true);
 for(const target of targets){const state=await api('gas/operations/read',{context:operational,tradeId:target.tradeId,day:target.day});
  assert.equal(state.state.nominations.length,1);assert.equal(state.state.nominations[0].quantity,'100');assert.ok(!state.state.heads.some(head=>head.kind==='scheduled'),'Manual requests do not adopt a schedule');}
 const crudeContext={...financial,convention:'physical_crude_fixed_volume_v1'}, crudeCatalog=await api('crude/catalog',crudeContext), assignment=crudeCatalog.assignments[0], profile=crudeCatalog.profiles[0];
 const crudeTrade=await api('crude/book',{...crudeContext,...f.parties,operationId:id(),bookId:f.bookId,definitionRevisionId:assignment.definitionRevisionId,assignmentCode:assignment.code,
  profile:profile.code,quantityBasis:profile.quantityBasis,tolerance:profile.tolerance,discounting:profile.discounting,
  deliveryFrom:'2021-01-01T00:00:00Z',deliveryTo:'2021-02-01T00:00:00Z',paymentAt:'2021-02-01T00:00:00Z',quantity:'1000',direction:'receive',considerationDirection:'pay',fixedPrice:'70',handlingFee:'0.1',handlingFeeDirection:'receive',
  deliveryCalendarPolicy:f.physicalCalendar,considerationCalendarPolicy:f.cashCalendar,feeCalendarPolicy:f.cashCalendar,handlingFeePaymentAt:'2021-02-05T00:00:00Z'});
 await api('crude/operations/receipt',{context:{...operational,convention:'physical_crude_fixed_volume_v1'},tradeId:crudeTrade.tradeId,expectedRevision:1,expectedVersion:0,
  operationId:id(),businessKey:'synthetic-delivery-crude-receipt',profile:'physical_crude_receipt_cash_v1',effectiveAt:'2021-01-10T12:00:00Z',evidence:'Synthetic accepted standard-volume receipt',quantity:'400',titleTransferred:true,disposition:'owned_inventory',saleTradeId:null,saleRevision:null});
 const crudeRead=await api(crude+'/read',{context:business,scopeId:crudeTrade.tradeId});assert.equal(crudeRead.assessment.profile,'crude_receipt_transfer_v1');
 assert.equal(crudeRead.assessment.activities.length,1);assert.equal(crudeRead.assessment.activities[0].geometry.kind,'transfer');assert.equal(crudeRead.assessment.activities[0].geometry.through,null);
 assert.equal(crudeRead.assessment.measures.find(row=>row.code==='accepted').value,'400');
 await api('profile',{profile:'viewer'});
 await api(gas+'/record',allocation,[403,409]);await api(batch+'/record',{...nomination,previewHash:nominationReview.previewHash},[403,409]);
 await api(gas+'/status',{ownerId:empty.ownerId,operationId:allocation.operationId},[403,409]);
 await persona('trader');
 await click('#profile-open');await set('#zone','UTC');await click('#profile-save',60000);await click('#nav-facilities',60000);
 await set('#date-context-on',f.day);await wait(idle,60000);await wait("!document.querySelector('#use-business-date-current').disabled",60000);await click('#use-business-date-current',60000);
 await click('[data-facilities-view=gas-delivery]',60000);await set('[data-delivery-workbench-gas=scope]',f.routeId);
 await wait(`deliveryGas?.scopeId===${q(f.routeId)}&&${idle}`,60000);
 await check('gas interval timeline and native constraints render through the common workbench',"!!document.querySelector('#gas-delivery-workspace [data-delivery-timeline]')&&!!document.querySelector('#gas-delivery-workspace [data-delivery-constraints]')&&document.querySelector('#snapshot').value===''");
 await shot('delivery-workbench-gas');
 await click('[data-facilities-view=crude-delivery]',60000);await set('[data-delivery-workbench-crude=scope]',crudeTrade.tradeId);
 await wait(`deliveryCrude?.scopeId===${q(crudeTrade.tradeId)}&&${idle}`,60000);
 await check('crude point transfer events render in the same workbench',"!!document.querySelector('#crude-delivery-workspace [data-delivery-timeline]')&&document.querySelector('#crude-delivery-workspace').textContent.includes('400')");
 await shot('delivery-workbench-crude');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await evaluate("document.querySelector('#crude-delivery-workspace').scrollIntoView({block:'start'})");await check('the shared delivery assessment fits a narrow viewport',fitsWidth);await shot('delivery-workbench-mobile');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await check('native crude operations require an explicit financial close',"document.querySelector('#crude-delivery-workspace [data-native-delivery]').disabled&&document.querySelector('#snapshot').value===''");
 await set('#snapshot',operational.snapshotId);await wait(idle,60000);await click('[data-facilities-view=crude-delivery]',60000);await set('[data-delivery-workbench-crude=scope]',crudeTrade.tradeId);
 await wait(`deliveryCrude?.scopeId===${q(crudeTrade.tradeId)}&&document.querySelector('#crude-delivery-workspace [data-native-delivery]')?.disabled===false&&${idle}`,60000);
 await click('#crude-delivery-workspace [data-native-delivery]',60000);
 await check('native crude handoff retains the exact trade revision, binding and applied financial input',`(()=>{const terms=deliveryCalls.filter(call=>call.path==='/api/trading/crude').at(-1),receipt=deliveryCalls.filter(call=>call.path==='/api/crude/operations/read').at(-1);return !document.querySelector('#physical-operations-page').hidden&&!document.querySelector('#crude-operations').hidden&&!!document.querySelector('#crude-operation-form')&&!document.querySelector('#crude-operation-kind').disabled&&[...document.querySelector('#crude-operation-kind').options].some(option=>option.value==='receipt')&&terms?.body.tradeId===${q(crudeTrade.tradeId)}&&terms.body.bindingId===${q(crudeTrade.instrumentId)}&&terms.body.revision===1&&terms.body.instrumentType==='physical_crude'&&receipt?.body.tradeId===${q(crudeTrade.tradeId)}&&receipt.body.context.snapshotId===${q(operational.snapshotId)}&&receipt.body.context.knowledgeMode==='current'&&document.querySelector('#snapshot').value===${q(operational.snapshotId)};})()`);
 await shot('delivery-workbench-crude-native');
 await writeFile(f.reportPath,JSON.stringify({ownerId:empty.ownerId,allocationId,batchId:nomination.operationId,failedBatchId:failed.operationId}));
 console.log('PASS real delivery workbench HTTP and browser: exact interval allocations, transfer events, atomic native nominations, replay/history/permissions and responsive shared presentation');
}
await withOwnedBrowser(process.argv[2],process.argv[3],run);
