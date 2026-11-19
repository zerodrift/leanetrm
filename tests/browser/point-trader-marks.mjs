import {withOwnedBrowser} from './owned-browser.mjs';
import {writeFile} from 'node:fs/promises';
import {dirname,join} from 'node:path';
const q=JSON.stringify,root='#point-trader-mark',entry=root+' [data-point-value=entry]';
const stable=value=>JSON.stringify(value,(_key,item)=>item&&typeof item==='object'&&!Array.isArray(item)?Object.fromEntries(Object.entries(item).sort(([a],[b])=>a<b?-1:a>b?1:0)):item);
const quoted=reply=>Object.entries({Currency:'USD','Quote quantity':'package','Quote multiplier':'1'}).every(([label,value])=>reply.overviewSections?.flatMap(section=>section.values).some(fact=>fact.label===label&&fact.value===value));
await withOwnedBrowser(process.argv[2],process.argv[3],async d=>{
 const {command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,tab,fixture:f}=d;
 await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle,60000);await persona('editor');
 await evaluate(`(async()=>{window.pointCalls=[];const send=window.fetch;window.fetch=async(input,init)=>{const response=await send(input,init);if(String(input).startsWith('/api/')&&init?.body&&response.headers.get('content-type')?.includes('application/json'))pointCalls.push({path:String(input),body:JSON.parse(init.body),status:response.status,result:await response.clone().json()});if(window.losePoint&&String(input)==='/api/market-values/record'&&response.ok){window.losePoint=false;throw new TypeError('Synthetic lost accepted point response');}return response;};const token=(await(await window.workspaceFetch('/api/bootstrap?includeFrames=false')).json()).csrfToken;window.pointPost=async(path,body)=>{const response=await window.workspaceFetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)});return {status:response.status,body:await response.json()};};})()`);
 const latest=path=>evaluate(`pointCalls.findLast(call=>call.path===${q('/api/'+path)})`);
 const post=(path,body)=>evaluate(`pointPost(${q(path)},${q(body)})`);
 const review=await post('date-schedules/context',{effectiveOn:f.businessDate,mode:'current',knownAt:null});
 if(review.status!==200)throw Error('Could not prepare a workspace cutoff for the ordinary point.');
 const sourceContext={contextId:review.body.context.contextId,businessDate:f.businessDate,asOf:f.at,snapshotId:null};
 const query={context:sourceContext,source:f.source,curveCode:'transport_rights',contractId:f.contractId,search:'',offset:0,limit:10,quoteFilter:null,eligibility:null};
 const setup=await post('market-values/setup',query);
 if(setup.status!==200||!setup.body.canPublish||setup.body.choices.length!==1||setup.body.choices[0].previous!==null)
  throw Error('Unpublished workspace point setup failed: '+JSON.stringify(setup));
 const proposed={operationId:await evaluate('crypto.randomUUID()'),ownerId:setup.body.ownerId,context:sourceContext,policy:f.policy,
  target:setup.body.choices[0].target,value:'-12.500',evidence:'Synthetic negative preview only',originalInputs:null};
 const previewOnly=await post('market-values/preview',proposed);
 if(previewOnly.status!==200||previewOnly.body.outcome.value!=='-12.5')throw Error('Exact signed preview failed: '+JSON.stringify(previewOnly));
 const missing=await post('market-values/record',{...proposed,value:null});
 if(missing.status!==422||missing.body.code!=='point_trader_mark_input')throw Error('Missing value did not refuse publication: '+JSON.stringify(missing));
 const mismatch=await post('market-values/record',{...proposed,target:{...proposed.target,contractId:await evaluate('crypto.randomUUID()')}});
 if(mismatch.status!==422||mismatch.body.code!=='point_trader_mark_source')throw Error('Unknown member did not refuse the command: '+JSON.stringify(mismatch));
 const absent=await post('market-values/status',{operationId:proposed.operationId,ownerId:proposed.ownerId});
 if(absent.status!==200||absent.body.found!==false)throw Error('A preview or failed write persisted a point receipt.');
 const historical=await post('market/context',{effectiveOn:f.businessDate,mode:'at',knownAt:review.body.context.knownAt,selection:'latest_correction'});
 if(historical.status!==200)throw Error('Could not select the historical point view: '+JSON.stringify(historical));
 for(const action of ['preview','record']){
  const denied=await post('market-values/'+action,proposed);
  if(denied.status!==422||denied.body.code!=='historical_read_only')throw Error('Historical point write acquired current authority: '+JSON.stringify(denied));
 }
 if((await post('market/current',{})).status!==200)throw Error('Could not leave historical point view.');
 // A snapshot is an optional view hint. The exact market certificate survives
 // even when this session has never held that navigation frame.
 const noFrame=await evaluate(`(async()=>{const setup=await pointPost('market-values/setup',{context:${q(f.noFrameContext)},source:${q(f.source)},curveCode:'transport_rights',contractId:${q(f.contractId)},search:'',offset:0,limit:10,quoteFilter:null,eligibility:null});if(setup.status!==200||setup.body.originalInputs!==null||!setup.body.canPublish||setup.body.choices.length!==1)throw Error('Missing-frame setup: '+JSON.stringify(setup));const request={operationId:crypto.randomUUID(),ownerId:setup.body.ownerId,context:setup.body.context,policy:setup.body.policy.code,target:setup.body.choices[0].target,value:'18',evidence:'Synthetic unavailable optional session frame',originalInputs:null};const preview=await pointPost('market-values/preview',request);const facts=preview.body.overviewSections?.flatMap(section=>section.values)??[];if(!Object.entries({Currency:'USD','Quote quantity':'package','Quote multiplier':'1'}).every(([label,value])=>facts.some(fact=>fact.label===label&&fact.value===value)))throw Error('The publication review must disclose the exact currency, quantity unit and quote multiplier: '+JSON.stringify(preview));const absent=await pointPost('market-values/status',{operationId:request.operationId,ownerId:request.ownerId}),record=await pointPost('market-values/record',request),status=await pointPost('market-values/status',{operationId:request.operationId,ownerId:request.ownerId});return{setup,request,preview,absent,record,status};})()`);
 if(noFrame.preview.status!==200||noFrame.record.status!==200||noFrame.status.status!==200)throw Error('Missing optional frame blocked publication: '+JSON.stringify(noFrame));
 if(noFrame.absent.status!==200||noFrame.absent.body.found!==false||noFrame.preview.body.context.snapshotId!==null||noFrame.record.body.context.snapshotId!==null||noFrame.status.body.context.snapshotId!==null||noFrame.record.body.outcome.value!=='18'||noFrame.status.body.command.originalInputs!==null||stable(noFrame.record.body.context)!==stable(noFrame.status.body.context)||stable(noFrame.record.body.outcome)!==stable(noFrame.status.body.outcome)||noFrame.record.body.editedSelection?.state!=='unavailable')throw Error('Missing-frame publication lost canonical receipt provenance: '+JSON.stringify(noFrame));
 if(![noFrame.preview,noFrame.record,noFrame.status].every(reply=>quoted(reply.body)))throw Error('Preview, record and retained status must disclose the same immutable quote metadata.');
 await check('missing optional frame permits preview and publication with canonical null view provenance and exact readonly status','true');
 await click('#profile-open');await set('#zone','America/Chicago');await click('#profile-save',60000);
 await set('#close-session',f.session);await wait(idle,60000);await set('#snapshot',f.closeId);await wait(idle,60000);await click('#nav-market',60000);
 await wait("!!document.querySelector('.curve-button[data-curve=transport_rights]')&&"+idle,60000);await click('.curve-button[data-curve=transport_rights]',60000);
 const local=at=>evaluate(`(()=>{const p=Object.fromEntries(new Intl.DateTimeFormat('en-CA',{timeZone:'America/Chicago',hourCycle:'h23',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit'}).formatToParts(new Date(${q(at)})).map(p=>[p.type,p.value]));return p.year+'-'+p.month+'-'+p.day+'T'+p.hour+':'+p.minute+':'+p.second;})()`);
 async function read(){await set(root+' [name=businessDate]',f.businessDate);await set(root+' [name=valueAt]',await local(f.at));await set(root+' [name=knowledgeMode]','current');await submit(root+' [data-point-value=read-form]',60000);const found=await latest('market-values/setup');if(found?.status!==200||found.body.context.businessDate!==f.businessDate||new Date(found.body.context.asOf).getTime()!==new Date(f.at).getTime()||found.result.source.definitionRevisionId!==f.source.definitionRevisionId||!found.result.canPublish||found.result.choices.length!==1)throw Error('Exact ordinary setup failed: '+JSON.stringify(found));return found;}
 async function publish(value,{lost=false,apply=false,phone=false}={}){
  const setup=await read();await check('every new publication requires an explicit point, policy and signed value',`document.querySelector('${entry} [name=point]').value===''&&document.querySelector('${entry} [name=policy]').value===''&&document.querySelector('${entry} [name=value]').value===''`);
  await set(entry+' [name=point]','0');await set(entry+' [name=policy]',f.policy);await set(entry+' [name=value]',value);await set(entry+' [name=evidence]','Synthetic browser-authored final point');
  await check('selected value entry discloses backend currency and the exact quote unit and multiplier',`document.querySelector('${entry} [data-point-value=selected-unit]').textContent==='Currency: USD · Quote unit: package · quote multiplier 1'&&document.querySelector('${entry} [name=value]').getAttribute('aria-describedby')===document.querySelector('${entry} [data-point-value=selected-unit]').id`);
  await submit(entry,60000);const preview=await latest('market-values/preview');if(preview?.status!==200||preview.result.outcome.value!==value)throw Error('Publication review failed: '+JSON.stringify(preview));
  if(!quoted(preview.result))throw Error('The review omitted immutable quote metadata.');
  const absence=await evaluate(`pointPost('market-values/status',{operationId:${q(preview.body.operationId)},ownerId:${q(preview.body.ownerId)}})`);if(absence.status!==200||absence.body.found!==false)throw Error('Preview persisted a receipt.');
  await check('review rolls back the proposed fact and retains its exact source and local value time',`!!document.querySelector('${root} [data-point-value=review]')&&document.querySelector('${root} [data-point-value=zone]').textContent==='America/Chicago'`);
  if(phone){await command('Emulation.setDeviceMetricsOverride',{width:390,height:1000,deviceScaleFactor:1,mobile:true});await evaluate(`document.querySelector('${root} [data-point-value=publish]').scrollIntoView({block:'end'})`);await shot('point-trader-mark-mobile');await check('phone publication review fits and keeps its action visible',`${fitsWidth}&&(()=>{const r=document.querySelector('${root} [data-point-value=publish]').getBoundingClientRect();return r.top>=0&&r.bottom<=innerHeight;})()`);await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});}
  if(lost)await evaluate('window.losePoint=true');const selectedBefore=await evaluate("document.querySelector('#snapshot').value");await click(root+' [data-point-value=publish]',60000);
  if(lost){await wait(`!document.querySelector('${root} [data-point-value=pending]').hidden&&${idle}`,60000);await click(root+' [data-point-value=status]',60000);}
  await wait(`!document.querySelector('${root} [data-point-value=receipt]').hidden&&${idle}`,60000);
  const recorded=await latest('market-values/record'),status=lost?await latest('market-values/status'):null;
  if(recorded?.status!==200||recorded.result.outcome.value!==value||lost&&(status?.status!==200||stable(status.result.outcome)!==stable(recorded.result.outcome)))throw Error('Accepted publication/status lost its original fact.');
  if(!quoted(recorded.result)||lost&&!quoted(status.result))throw Error('The accepted receipt omitted immutable quote metadata.');
  await check('publication does not silently select edited inputs',`document.querySelector('#snapshot').value===${q(selectedBefore)}`);
  if(apply){if(!setup.result.originalInputs||recorded.result.editedSelection?.state!=='available')throw Error('The complete original input matrix was not retained: '+JSON.stringify({setup:setup.result,recorded:recorded.result}));await click(root+' [data-point-value=inputs]',60000);const edited=await latest('market-values/inputs');if(edited?.status!==200||edited.result.selectionId!==recorded.result.editedSelection.selectionId||edited.result.frame.originCloseId||edited.result.frame.close)throw Error('Explicit edited inputs did not retain the accepted identity: '+JSON.stringify(edited));await check('only the explicit working-input action changes the selected frame',`document.querySelector('#snapshot').value===${q(edited.result.frame.id)}&&document.querySelector('#snapshot').value!==${q(selectedBefore)}`);}
  return recorded;
 }
 const first=await publish('20',{lost:true,phone:true});await shot('point-trader-mark-recorded');
 const originalCut=(await latest('date-schedules/context')).result.context;
 if(originalCut.contextId!==first.body.context.contextId)throw Error('Publication changed the displayed source identity.');
 const second=await publish('21',{apply:true});if(second.result.outcome.previousObservationId!==first.result.outcome.observationId)throw Error('Correction did not retain its exact predecessor.');
 const third=await publish('20');if(third.result.outcome.previousObservationId!==second.result.outcome.observationId||third.result.outcome.observationId===first.result.outcome.observationId)throw Error('A genuine new20 was deduplicated against historical20.');
 const equal=await publish('20');if(equal.result.outcome.changed!==false||equal.result.outcome.observationId!==third.result.outcome.observationId||!equal.result.acknowledgement)throw Error('Equal predecessor value materialized a new observation.');
 await check('correction and equal-value acknowledgement retain immutable lineage without reserving a prospective revision','true');
 await persona('viewer');
 const readonly=await evaluate(`(async()=>{const setup=await pointPost('market-values/setup',{context:${q(f.noFrameContext)},source:${q(f.source)},curveCode:'transport_rights',contractId:${q(f.contractId)},search:'',offset:0,limit:10,quoteFilter:null,eligibility:null});const status=await pointPost('market-values/status',{operationId:${q(first.body.operationId)},ownerId:${q(first.body.ownerId)}});return{setup,status};})()`);
 if(readonly.setup.status!==200||readonly.setup.body.canPublish!==false||readonly.status.status===200)throw Error('Readonly actor acquired publication or another actor receipt: '+JSON.stringify(readonly));
 const writeDenied=await post('market-values/record',first.body);
 if(writeDenied.status!==403||writeDenied.body.code!=='point_trader_mark_denied')throw Error('A retry bypassed current import authority: '+JSON.stringify(writeDenied));
 await check('a market reader inspects eligible points without publishing or reading another actor receipt','true');
 // A new cookie and page have no copy of the cut or the optional input frame.
 await command('Network.clearBrowserCookies');
 const fresh=await tab();
 try{
  await fresh.command('Page.navigate',{url});await fresh.wait(idle,60000);
  await fresh.click('#profile-open');await fresh.set('#profile','editor');await fresh.click('#open',60000);
  const recovered=await fresh.evaluate(`(async()=>{
   const token=(await(await workspaceFetch('/api/bootstrap?includeFrames=false')).json()).csrfToken;
   async function call(path,body){const r=await workspaceFetch('/api/market-values/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)});return{status:r.status,body:await r.json()};}
   return {status:await call('status',${q({operationId:first.body.operationId,ownerId:first.body.ownerId})}),retry:await call('record',${q(first.body)}),unpublished:await call('setup',${q(query)})};
  })()`);
  for(const reply of [recovered.status,recovered.retry])if(reply.status!==200||stable(reply.body.context)!==stable(first.result.context)||
   stable(reply.body.outcome)!==stable(first.result.outcome))throw Error('Fresh-session status or retry changed the accepted evidence: '+JSON.stringify(reply));
  if(recovered.unpublished.status!==422||recovered.unpublished.body.code!=='temporal_context')throw Error('Another session recovered an uncommitted preview source: '+JSON.stringify(recovered.unpublished));
 }finally{await fresh.close();}
 await writeFile(join(dirname(process.argv[2]),'point-source-cuts.json'),JSON.stringify({previewOnly:review.body.context,accepted:originalCut,operation:first.body.operationId}),{mode:0o600});
 await check('fresh-session retry and status preserve exact published evidence while a preview remains private','true');
});
