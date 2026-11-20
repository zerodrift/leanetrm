import {withOwnedBrowser} from './owned-browser.mjs';
import {startNewTrade} from './new-trade.mjs';
import {gasTicket} from './gas-ticket.mjs';
import {sideCards,serviceFieldGroups,sidePhone} from './instrument-side-qualification.mjs';

const q=JSON.stringify,product='physical_gas_transport',handler='gas_transport_financial',panel='[data-lifecycle=panel]';
const stable=value=>JSON.stringify(value,(_key,item)=>item&&typeof item==='object'&&!Array.isArray(item)?Object.fromEntries(Object.entries(item).sort(([a],[b])=>a<b?-1:a>b?1:0)):item);
const same=(left,right)=>stable(left)===stable(right);

async function run(driver){
 const {command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,fixture:f}=driver;
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle,60000);await persona('trader');
 await evaluate(`(async()=>{window.calls=[];window.financialCases=[];window.workspaceStreams=[];window.fixtureReadReferences={};const send=window.fetch;window.fetch=async(input,init)=>{
  const path=String(input),reply=await send(input,init);
  Object.assign(window.fixtureReadReferences,JSON.parse(reply.headers.get('X-OpenETRM-Read-References')||'{}'));
  if(path==='/api/trading/workspace/stream'){const observed={status:reply.status,body:JSON.parse(init.body),finished:false};workspaceStreams.push(observed);void reply.clone().text().then(raw=>{observed.frames=raw.trim().split('\\n').filter(Boolean).map(line=>JSON.parse(line));}).catch(error=>{observed.error=error.message;}).finally(()=>{observed.finished=true;});}
  if(path.startsWith('/api/')&&init?.body&&reply.headers.get('content-type')?.includes('application/json'))calls.push({path,body:JSON.parse(init.body),status:reply.status,result:await reply.clone().json()});
  if(reply.ok&&((window.loseBooking&&path==='/api/tickets/book')||(window.loseFinancial&&path==='/api/gas/transport/financial/record')||(window.losePayment&&path==='/api/cash-payments/record'))){window.loseBooking=false;window.loseFinancial=false;window.losePayment=false;throw new TypeError('Synthetic lost accepted response');}return reply;};
  const token=(await(await window.workspaceFetch('/api/bootstrap?includeFrames=false')).json()).csrfToken;
  window.post=async(path,body)=>{const payload=JSON.stringify(body),references=Object.fromEntries(Object.entries(window.fixtureReadReferences).filter(([id])=>payload.includes(id)));const response=await window.workspaceFetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token,...(Object.keys(references).length?{'X-OpenETRM-Read-References':JSON.stringify(references)}:{})},body:payload}),value=await response.json();if(!response.ok)throw Error(path+': '+JSON.stringify(value));return value;};})()`);
 await click('#profile-open');await set('#zone','America/Chicago');await click('#profile-save',60000);
 const ticket=gasTicket(driver,'#transport-ticket .deal-ticket'),cases=[];
 const local=at=>evaluate(`(()=>{const p=Object.fromEntries(new Intl.DateTimeFormat('en-CA',{timeZone:'America/Chicago',hourCycle:'h23',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit'}).formatToParts(new Date(${q(at)})).map(p=>[p.type,p.value]));return p.year+'-'+p.month+'-'+p.day+'T'+p.hour+':'+p.minute+':'+p.second;})()`);
 const at=async label=>{await set('#close-session',f.sessions[label]);await wait(idle,60000);if(label==='expiry')f.closes.expiry=await evaluate(`(async()=>{const navigation=await(await window.workspaceFetch('/api/workspace/navigation')).json(),frames=navigation.frames.filter(frame=>!frame.originCloseId&&frame.at.startsWith(${q(f.through.slice(0,-1))}));if(frames.length!==1)throw Error('Select the one explicit working Through frame.');return frames[0].id;})()`);await set('#snapshot',f.closes[label]);await wait(`document.querySelector('#snapshot').value===${q(f.closes[label])}&&${idle}`,60000);};
 const context=label=>({snapshotId:f.closes[label],convention:'physical_gas_fixed_quantity_v4',knowledgeMode:'current'});
 const read=async(c,label='initial',retained=null)=>evaluate(`post('tickets/trade',{context:${q(retained??context(label))},product:${q(product)},tradeId:${q(c.tradeId)},bindingId:${q(c.instrumentId)},revision:1})`);
 const priced=async(c,expected,label='initial')=>{const value=await read(c,label);if(value.presentValue!==expected)throw Error(`${c.businessKey}: expected remaining PV ${expected}, got ${JSON.stringify(value)}`);return value;};
 const latest=(path)=>evaluate(`calls.findLast(call=>call.path===${q('/api/'+path)})`);
 async function asActor(id){
  const before=await evaluate("calls.filter(call=>call.path==='/api/profile').length");await persona(id);
  const discard="[...document.querySelectorAll('#lifecycle-page-prompt button')].find(button=>button.textContent==='Discard edits')";
  if(await evaluate(`!!(${discard})`)){await evaluate(`(${discard}).click()`);await wait(idle,60000);}
  await wait(`calls.filter(call=>call.path==='/api/profile').length>${before}&&calls.findLast(call=>call.path==='/api/profile').status===200&&calls.findLast(call=>call.path==='/api/profile').body.profile===${q(id)}&&${idle}`,60000);
  await wait(`(!document.querySelector('#profile-dialog').open||!document.querySelector('#profile-error').hidden)&&${idle}`,60000);
  if(!await evaluate(`document.querySelector('#profile-label').textContent===[...document.querySelector('#profile').options].find(option=>option.value===${q(id)})?.textContent`))throw Error('The header does not identify the actor whose switch was accepted.');
  if(await evaluate("document.querySelector('#profile-dialog').open")){
   const error=await evaluate("document.querySelector('#profile-error').textContent");if(error!=='Choose a market date above.')throw Error('The actor switch could not initialize its workspace: '+error);
   await click('#profile-close',60000);await wait(`!document.querySelector('#profile-dialog').open&&${idle}`,60000);
  }
 }
 async function openTrade(c){
  await click('#nav-futures',60000);await wait(idle,60000);
  const row=`document.querySelector('#blotter-pane-grid [data-row-key="${c.tradeId}:${c.instrumentId}"]')`;
  await wait(`!!(${row})&&${idle}`,60000);const before=await evaluate("calls.filter(call=>call.path==='/api/tickets/trade').length");await evaluate(`(${row}).click()`);
  await wait(`calls.filter(call=>call.path==='/api/tickets/trade').length>${before}&&calls.findLast(call=>call.path==='/api/tickets/trade').body.tradeId===${q(c.tradeId)}&&calls.findLast(call=>call.path==='/api/tickets/trade').body.bindingId===${q(c.instrumentId)}&&document.querySelector('#position-details').dataset.mode==='position'&&!!document.querySelector('#position-details .deal-ticket[data-mode=booked]')&&${idle}`,60000);
 }
 async function workbench(c){
  await click('#nav-lifecycle',60000);await wait(`!!document.querySelector('[data-lifecycle=rows] .table-search')&&${idle}`,60000);
  const found=await latest('lifecycle/view'),offers=found?.result.actions.filter(offer=>offer.target.handler===handler&&offer.target.scopeId===c.tradeId&&offer.target.bindingId===c.instrumentId)??[];
  if(offers.length!==1)throw Error('Expected one exact financial workbench: '+JSON.stringify({c,offers,discovery:found}));
  if(typeof offers[0].subject?.reference!=='string'||!offers[0].subject.reference||offers[0].subject.bookId!==c.bookId||offers[0].subject.book!==c.bookName)throw Error('The ordinary lifecycle picker must name this exact Transport agreement and book: '+JSON.stringify(offers[0]));
  await set('[data-lifecycle=rows] .table-search',offers[0].subject.reference);
  const row=`[...document.querySelectorAll('[data-lifecycle=rows] .tabulator-row[data-row-key]')].find(row=>{const key=JSON.parse(row.dataset.rowKey);return key[0]===${q(handler)}&&key[1]===${q(c.tradeId)}&&key[2]===${q(c.instrumentId)};})`;
  await wait(`!!(${row})&&${idle}`,60000);const before=await evaluate("calls.filter(call=>call.path==='/api/gas/transport/financial/setup').length");await evaluate(`(${row}).click()`);
  await wait(`calls.filter(call=>call.path==='/api/gas/transport/financial/setup').length>${before}&&calls.findLast(call=>call.path==='/api/gas/transport/financial/setup').status===200&&calls.findLast(call=>call.path==='/api/gas/transport/financial/setup').body.target.scopeId===${q(c.tradeId)}&&calls.findLast(call=>call.path==='/api/gas/transport/financial/setup').body.target.bindingId===${q(c.instrumentId)}&&${idle}`,60000);
  const setup=await latest('gas/transport/financial/setup');if(setup?.status!==200||setup.body.target.scopeId!==c.tradeId||setup.body.target.bindingId!==c.instrumentId)throw Error('The financial setup lost its target.');
  return setup.result;
 }
 async function pick(host,name,value){
  await evaluate(`(()=>{const input=(${host}).querySelector('[name='+${q(name)}+']');if(!input)throw Error('Missing exact described field '+${q(name)});input.value=${q(value)};if(input instanceof HTMLSelectElement&&input.value!==${q(value)})throw Error('Missing exact choice '+${q(name)});input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));})()`);
 }
 async function fill(host,fields,values){
  for(const field of fields){
   if(field.kind==='reference'){
    const selected=field.choices.filter(choice=>field.members.every(name=>!Object.hasOwn(values,name)||same(choice.value[name],values[name])));
    if(selected.length!==1)throw Error('Expected one exact described reference '+field.name+': '+JSON.stringify(selected));await pick(host,field.name,selected[0].id);
   }else if(field.kind==='select'){
    const selected=field.choices.filter(choice=>same(choice.value??choice.id,values[field.name]));if(selected.length!==1)throw Error('Expected explicit choice '+field.name);await pick(host,field.name,selected[0].id);
   }else if(field.kind==='instant'){
    if(typeof values[field.name]!=='string')throw Error('Missing explicit instant '+field.name);await pick(host,field.name,await local(values[field.name]));
   }else if(field.kind==='array'){
    const rows=values[field.name];if(!Array.isArray(rows))throw Error('Missing explicit array '+field.name);await pick(host,field.name,rows.length?'entries':'empty');
    for(const[index,row]of rows.entries()){await evaluate(`(${host}).querySelector('[name='+${q(field.name)}+']').closest('.operation-field').querySelector(':scope > button').click()`);await fill(`(${host}).querySelectorAll('[data-operation-entry='+${q(field.name)}+']')[${index}]`,field.fields,row);}
   }else{if(!Object.hasOwn(values,field.name))throw Error('Missing explicit value '+field.name);await pick(host,field.name,String(values[field.name]));}
  }
 }
 async function financial(c,kind,values,{lost=false,phone=false}={}){
  const setup=await workbench(c),form=setup.configuration[kind];if(!form)throw Error('Missing admitted financial form '+kind);
  if(setup.configuration.payment){const owners=setup.configuration.payment.fields.find(field=>field.name==='cashOwner')?.choices;if(owners?.length!==1||owners[0].value.tradeId!==c.tradeId||owners[0].value.bindingId===c.instrumentId)throw Error('The fee cash owner is not an independent exact binding.');c.cashBindingId=owners[0].value.bindingId;}
  const selector=panel+' [data-transport-financial-command='+kind+']',host=`document.querySelector(${q(selector)})`;
  await evaluate(`(${host}).closest('details').open=true`);await fill(host,form.fields,values);await submit(selector,60000);
  await wait(`!!document.querySelector('[data-transport-financial-record]')&&${idle}`,60000);
  const preview=await latest('gas/transport/financial/preview');if(preview?.status!==200||preview.body.command.tradeId!==c.tradeId||preview.result.kind!==kind)throw Error('The financial review lost its exact selected command.');
  await evaluate("document.querySelector('[data-transport-financial-review]').scrollIntoView({block:'start'})");await shot('transport-financial-review');
  if(kind==='adopt_rights_mark')await check('a rights review shows the exact signed published value and currency',`(()=>{const values=[...document.querySelectorAll('[data-transport-financial-review] dd')].map(node=>node.textContent.toUpperCase());return values.includes(${q(c.rightsValue+' USD')})||values.includes(${q(c.rightsValue)})&&values.includes('USD');})()`);
  if(phone){
   await command('Emulation.setDeviceMetricsOverride',{width:390,height:1000,deviceScaleFactor:1,mobile:true});
   await evaluate("document.querySelector('[data-transport-financial-record]').scrollIntoView({block:'end'})");
   await shot('transport-financial-mobile');
   await check('phone review retains readable financial facts and its record control',`${fitsWidth}&&(()=>{const r=document.querySelector('[data-transport-financial-record]').getBoundingClientRect();return r.top>=0&&r.bottom<=innerHeight;})()`,"(()=>{const r=document.querySelector('[data-transport-financial-record]').getBoundingClientRect();return{width:document.documentElement.clientWidth,scrollWidth:document.documentElement.scrollWidth,innerHeight,record:{top:r.top,bottom:r.bottom,left:r.left,right:r.right},overflow:[...document.querySelectorAll('body *')].filter(node=>node.getClientRects().length&&node.getBoundingClientRect().right>document.documentElement.clientWidth+1).slice(0,12).map(node=>({tag:node.tagName,id:node.id,class:node.className,right:node.getBoundingClientRect().right}))};})()");
   await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
  }
  if(lost)await evaluate('window.loseFinancial=true');
  await wait(`document.querySelector('[data-transport-financial-record]')?.disabled===false&&${idle}`,60000);await click('[data-transport-financial-record]',60000);
  if(lost){
   await wait(`!document.querySelector('[data-lifecycle=pending]').hidden&&${idle}`,60000);
   await asActor('viewer');await check('another actor cannot see the retained financial request',"document.querySelector('[data-lifecycle=pending]').hidden");
   await asActor('trader');await click('#nav-lifecycle',60000);await wait(`!document.querySelector('[data-lifecycle=check-status]').disabled&&${idle}`,60000);await click('[data-lifecycle=check-status]',60000);
   await wait(`document.querySelector('[data-lifecycle=pending]').hidden&&${idle}`,60000);
   const original=await latest('gas/transport/financial/record'),status=await latest('gas/transport/financial/status');
   if(!same(status.body,{operationId:original.body.operationId,ownerId:original.body.ownerId})||!same(status.result.command,original.body.command))throw Error('Financial status did not recover the exact original command.');
   await at('initial');
  }
  const recorded=await latest('gas/transport/financial/record');if(recorded?.status!==200||recorded.result.tradeId!==c.tradeId||recorded.result.kind!==kind)throw Error('The accepted financial command has another target.');
  if(recorded.result.outcome.financialStateId)c.stateId=recorded.result.outcome.financialStateId;
  return recorded;
 }

 await at('initial');await click('#nav-futures',60000);await wait(idle,60000);
 await wait(`workspaceStreams.at(-1)?.finished===true&&${idle}`,60000);
 const empty=await evaluate('workspaceStreams.at(-1)'),terminal=empty.frames?.at(-1);
 if(empty.status!==200||empty.body.context.snapshotId!==f.closes.initial||empty.frames?.[0]?.type!=='start'||empty.frames[0].positions!==0||terminal?.type!=='complete'||terminal.positions!==0||terminal.valued!==0||empty.frames.some(frame=>frame.type==='error'))throw Error('The empty exact R2 workspace must complete without a substituted source: '+JSON.stringify(empty));
 await check('an empty book completes under its exact whole-package source before any booking',"document.querySelector('#futures-page').dataset.pricingState==='complete'&&document.querySelector('#blotter-load-status').hidden");
 for(const [index,input]of f.cases.entries()){
  await startNewTrade(driver);await click('#new-trade-transport',60000);await wait(`!!document.querySelector('#transport-ticket .ticket-control')&&${idle}`,60000);
  await check('Transport starts without financial defaults or a gas quantity editor',`document.querySelector(${q(ticket.control(null,'profile'))}).value===''&&document.querySelector(${q(ticket.control(null,'fixed_fee_amount'))}).value===''&&document.querySelector(${q(ticket.control(null,'package_quantity'))}).tagName==='SELECT'&&!document.querySelector('#transport-ticket [data-field=quantity]')&&!document.querySelector('#transport-ticket [data-field=owned_account]')`);
  for(const[field,value]of Object.entries({profile:f.profile,perspective:input.perspective,book:input.bookId,legal_entity:f.legalEntity,counterparty:f.counterparty,entitlement:input.entitlementId,capacity_version:input.capacityVersionId,
   valuation_definition:f.valuationDefinition,valuation_reference_bundle:f.valuationBundle,package_quantity:f.packageQuantity,currency:f.currency,rights_valuation_policy:f.rightsPolicy,fee_profile:f.feeProfile,fee_direction:input.feeDirection,expiry_policy:f.expiryPolicy}))await ticket.choose(null,field,value);
  await set(ticket.control(null,'business_key'),input.businessKey);await set(ticket.control(null,'evidence'),'Synthetic explicit Transport agreement');await set(ticket.control(null,'fixed_fee_amount'),input.fixedFeeAmount);
  if(input.fixedFeeAmount==='0')await click(ticket.field(null,'fee_due_at')+' [data-role=not-applicable]');else await ticket.setInstant(null,'fee_due_at',f.feeDueAt);
  await ticket.until(ticket.current());
  const roles=['Commercial entitlement rights',...(input.fixedFeeAmount==='0'?[]:['Ordinary cash payment'])];
  await serviceFieldGroups(driver,ticket.ticket,['agreement','rights','fee'],'Transport edits distinguish whole-rights Side terms from independent fee terms');
  await sideCards(driver,ticket.ticket,await evaluate("calls.findLast(call=>call.path==='/api/tickets/preview'&&call.status===200).result"),{label:'Transport review exposes one whole-rights Side and only the explicitly selected fee Instrument',roles});
  if(index===0){await evaluate("document.querySelector('#transport-ticket').scrollIntoView({block:'start'})");await shot('transport-financial-entry');await evaluate('window.loseBooking=true');}
  await click(ticket.ticket+' .ticket-book',60000);
  if(index===0){const recover="[...document.querySelectorAll('#transport-ticket .ticket-booking button')].find(button=>button.textContent==='Check booking status'&&!button.disabled)";await wait(`!!(${recover})&&${idle}`,60000);await evaluate(`(${recover}).click()`);}
  await wait(`document.querySelector('#position-details').dataset.mode==='position'&&!!document.querySelector('#position-details .deal-ticket[data-mode=booked]')&&${idle}`,60000);
  const booked=await evaluate(`calls.findLast(call=>call.path==='/api/tickets/book'&&call.status===200&&call.body.draft.trade.book===${q(input.bookId)})`);
  if(!booked||booked.body.draft.product!==product||booked.body.draft.trade.valuation_reference_bundle!==f.valuationBundle||booked.body.draft.trade.capacity_version!==input.capacityVersionId||Object.hasOwn(booked.body.draft.trade,'quantity'))throw Error('Booking changed the whole-package or independent source pins.');
  const c={...input,...booked.result,stateId:booked.result.financialStateId};cases.push(c);await evaluate(`financialCases.push(${q(c)})`);
  const reopened=await read(c);if(reopened.presentValue!==null||reopened.unavailableReason!=='rights_mark_required'||reopened.draft.trade.valuation_definition!==f.valuationDefinition)throw Error('An unadopted rights package claimed a complete value.');
  await sideCards(driver,'#position-details',reopened,{label:'unpriced saved Transport retains its true state-owned rights Side and independent native cash binding',retained:true,roles});
  if(index===0)await sidePhone(driver,'#position-details','transport-financial-sides-mobile');
  await check('retained Transport uses its backend labels without a gas price or Side schedule action',"!document.querySelector('#position-details .ticket-add-kind')&&[...document.querySelectorAll('#position-details button')].filter(button=>button.textContent==='Open curve').every(button=>button.hidden)");
 }
 let originalMark;
 for(const[index,c]of cases.entries()){
  const marked=await financial(c,'adopt_rights_mark',{kind:'adopt_rights_mark',tradeId:c.tradeId,financialStateId:c.stateId,observationId:c.observationId,evidence:'Synthetic exact published rights value'},{lost:index===0,phone:index===0});
  if(index===0)originalMark=marked;await priced(c,c.expectedValue);
 }
 // The viewer's exact read authority excludes operating and owned-gas reads.
 await asActor('viewer');await at('initial');await evaluate('window.viewerReadStart=calls.length');await openTrade(cases[0]);
 await wait(`!!document.querySelector('#position-details [data-field=package_quantity] .ticket-value')&&${idle}`,60000);
 await check('ordinary financial-only authority inspects saved whole-package terms without operating discovery',"document.querySelector('#position-details [data-field=package_quantity] .ticket-value').textContent.length>0&&document.querySelector('#futures-new').disabled&&calls.slice(viewerReadStart).every(call=>call.path!=='/api/tickets/choices')");
 await click('#blotter-dock [data-maximize=details]');await evaluate("document.querySelector('#position-details').scrollIntoView({block:'start'})");await shot('transport-financial-reader');await click('#blotter-dock [data-maximize=details]');await asActor('trader');await at('initial');

 async function risk(c,expected){
  await click('#nav-risk',60000);await wait(`!![...document.querySelector('#risk-workspace-books').options].find(option=>option.value===${q(c.bookId)})&&${idle}`,60000);
  const picker='#risk-workspace-books-picker';await click(picker+'>summary');
  for(const id of await evaluate(`[...document.querySelectorAll('${picker} input[type=checkbox]:checked')].map(input=>input.value).filter(id=>id!==${q(c.bookId)})`))await click(picker+' input[type=checkbox][value="'+id+'"]',60000);
  if(!await evaluate(`document.querySelector('${picker} input[value="${c.bookId}"]').checked`))await click(picker+' input[value="'+c.bookId+'"]',60000);
  if(await evaluate(`document.querySelector('${picker}').open`))await click(picker+'>summary');
  await wait(`document.querySelector('#risk-workspace-pricing-policy')&&${idle}`,60000);await set('#risk-workspace-pricing-policy','base');await wait(idle,60000);
  await click('#risk-jobs-new',60000);await wait(`document.querySelector('#workspace-risk-fields')?.disabled===false&&${idle}`,60000);
  const catalog=await evaluate(`calls.findLast(call=>call.path==='/api/portfolio/risk/catalog'&&call.status===200&&call.body.bookIds?.length===1&&call.body.bookIds[0]===${q(c.bookId)})`);
  const target=catalog?.result.scenarioTargets.find(target=>target.kind==='contract'&&target.factor?.target?.curve?.code===c.curveCode&&target.factor?.target?.contract?.id===c.contractId);
  if(!target)throw Error('The ordinary Risk catalog omitted the exact whole-package factor.');
  await click('#workspace-risk-form input[value=custom]');await set('#workspace-risk-factor',target.id);await set('#workspace-risk-bump','1');
  const count=await evaluate("calls.filter(call=>call.path==='/api/portfolio/risk').length");await wait(`document.querySelector('#workspace-risk-run')?.disabled===false&&${idle}`,60000);await click('#workspace-risk-run',60000);
  await wait(`calls.filter(call=>call.path==='/api/portfolio/risk').length>${count}&&document.querySelector('#risk-workspace-results [data-state=complete]')&&${idle}`,60000);
  const report=await latest('portfolio/risk'),measure='transport_remaining_rights_and_fee_present_value_v1',positions=report.result.inputs.positions,totals=report.result.factors[0].totals;
  if(report.status!==200||!same(report.body.bookIds,[c.bookId])||report.body.tradeIds!==null||!same(report.body.pricingPolicy,catalog.result.pricingPolicy))throw Error('Risk changed its exact selected policy or book.');
  if(positions.length!==2||positions.some(position=>position.owner.kind!==1||position.owner.id!==c.tradeId||position.instrumentType!==product||position.measure!==measure)||new Set(positions.map(position=>position.bindingId)).size!==2)throw Error('Risk input evidence lost exact separate rights/fee ownership or its public measure: '+JSON.stringify(positions));
  const rights=positions.find(position=>position.bindingId===c.instrumentId),fee=positions.find(position=>position.bindingId===c.cashBindingId);
  if(!rights||!fee||rights.quantity.amount!=='1'||rights.quantity.specification.code!==f.packageQuantity||rights.quantity.specification.revision.catalogId!==f.valuationReferenceCatalogId||rights.quantity.specification.revision.revisionId!==f.valuationReferenceRevisionId||fee.quantity.amount!==c.signedFee||fee.quantity.specification.code!==f.currency)throw Error('Risk resized the whole package or changed its independent signed fee quantity.');
  if(positions.some(position=>position.instrumentId!==c.instrumentId)||!report.result.modelContracts.some(contract=>contract.id===rights.valuationId)||rights.valuationId===c.instrumentId||c.riskStateId&&c.riskStateId!==c.stateId&&c.rightsValuationId===rights.valuationId||c.feeValuationId&&c.feeValuationId!==fee.valuationId)throw Error('Risk changed a native owner or reused the earlier financial state valuation identity.');
  c.riskStateId=c.stateId;c.rightsValuationId=rights.valuationId;c.feeValuationId=fee.valuationId;
  if(totals.length!==1||totals[0].bookId!==c.bookId||totals[0].measure!==measure||totals[0].measureLabel!=='Remaining Transport rights and fee PV'||!totals[0].values.currencyReference||totals[0].values.currencyReference.revision.catalogId!==f.valuationReferenceCatalogId||totals[0].values.currencyReference.revision.revisionId!==f.valuationReferenceRevisionId)throw Error('Risk changed exact measure or currency reference.');
  for(const[key,value]of Object.entries(expected))if(totals[0].values[key]!==value)throw Error('Whole-package Risk '+key+' expected '+value+', got '+totals[0].values[key]);
  await check('ordinary Risk reprices the whole rights package and independent fee without gas scaling',"document.querySelector('#risk-workspace-results .risk-result-heading').textContent.includes('Sensitivity report')");await evaluate("document.querySelector('#risk-workspace-results').scrollIntoView({block:'start'})");await shot('transport-financial-risk');
 }
 await risk(cases[0],{base:'7.5',up:'8.5',down:'6.5'});
 const cash=await workbench(cases[0]),payment=cash.configuration.payment;if(!payment)throw Error('The independent fee payment form is unavailable.');
 const paymentHost=`document.querySelector('${panel} [data-transport-payment]')`;await evaluate(`(${paymentHost}).closest('details').open=true`);
 await fill(paymentHost,payment.fields,{snapshotId:f.closes.initial,knowledgeMode:'current',tradeId:cases[0].tradeId,expectedRevision:0,paidAt:f.valuationAt,amount:'-5',policy:'explicit_paid_cash_nominal_due_v1'});
 await evaluate("document.querySelector('[data-transport-payment]').scrollIntoView({block:'start'})");await shot('transport-financial-payment');await evaluate('window.losePayment=true');await submit(panel+' [data-transport-payment]',60000);
 await wait(`!document.querySelector('[data-lifecycle=check-status]').disabled&&${idle}`,60000);await click('[data-lifecycle=check-status]',60000);await wait(`document.querySelector('[data-lifecycle=pending]').hidden&&${idle}`,60000);
 const paid=await latest('cash-payments/record'),paymentStatus=await latest('cash-payments/status');if(!same(paid.body,paymentStatus.body)||paid.body.bindingId===cases[0].instrumentId||paid.body.amount!=='-5')throw Error('Payment recovery changed its independent cash owner or original request.');
 await priced(cases[0],'12.5');const history=await read(cases[0]);
 // Operating definitions are revised through the ordinary capacity workspace.
 await click('#nav-facilities',60000);await click('[data-facilities-view=transport]',60000);await wait(`!!document.querySelector('[data-transport-capacity=entitlement] option[value="${cases[0].entitlementId}"]')&&!document.querySelector('[data-transport-capacity=entitlement]').disabled&&${idle}`,60000);
 await set('[data-transport-capacity=entitlement]',cases[0].entitlementId);await wait(`calls.findLast(call=>call.path==='/api/gas/transport/capacity/read')?.result.entitlementId===${q(cases[0].entitlementId)}&&${idle}`,60000);
 const capacity=await latest('gas/transport/capacity/read'),revision=structuredClone(cases[0].definition);revision.evidence='Synthetic revised complete capacity';
 for(const scope of [revision,...revision.paths,...revision.receipts,...revision.deliveries])for(const limit of scope.limits??scope.agreement??[])limit.maximumPerHour='150';
 await click('[data-transport-command=define]',60000);const capacityHost="document.querySelector('[data-transport-capacity=editor]')";
 await fill(capacityHost,capacity.result.configuration.define.fields,revision);await pick(capacityHost,'operationEffectiveAt',await local(f.valuationAt));
 await submit('[data-transport-capacity=editor]',60000);await wait(`document.querySelector('[data-transport-capacity=record]')?.disabled===false&&${idle}`,60000);await click('[data-transport-capacity=record]',60000);
 const updated=await latest('gas/transport/capacity/record'),version=updated.result.outcome.versionId;
 const unchanged=await priced(cases[0],'12.5');if(unchanged.draft.trade.capacity_version!==cases[0].capacityVersionId)throw Error('An operating revision silently changed the financial agreement.');
 await financial(cases[0],'adopt_capacity_version',{kind:'adopt_capacity_version',tradeId:cases[0].tradeId,expectedFinancialStateId:cases[0].stateId,capacityVersionId:version,effectiveAt:f.valuationAt,evidence:'Synthetic explicit financial capacity adoption'});
 const unpriced=await read(cases[0]);if(unpriced.presentValue!==null||unpriced.unavailableReason!=='rights_mark_required'||unpriced.draft.trade.capacity_version!==version)throw Error('A new financial state reused the old mark or claimed fee-only complete value.');
 cases[0].capacityVersionId=version;await financial(cases[0],'adopt_rights_mark',{kind:'adopt_rights_mark',tradeId:cases[0].tradeId,financialStateId:cases[0].stateId,observationId:cases[0].observationId,evidence:'Synthetic equal-money mark for the new exact state'});await priced(cases[0],'12.5');await risk(cases[0],{base:'12.5',up:'13.5',down:'11.5'});
 await at('expiry');
 for(const[index,c]of cases.entries()){
  const before=await read(c,'expiry');if(before.presentValue!==null||before.unavailableReason!=='expiry_transition_required')throw Error('A live expired package silently became zero or cash-only.');
  const setup=await workbench(c),definition=setup.forms['gas.transport_financial.expire'];if(!definition)throw Error('Explicit reviewed expiry is missing.');
  const host=`document.querySelector('${panel} > form')`;await fill(host,definition.fields,{expectedFinancialStateId:c.stateId,evidence:'Synthetic reviewed contractual rights expiry'});
  const policies=setup.policies.filter(policy=>policy.id===f.expiryPolicyId&&policy.accountId===c.tradeId);if(policies.length!==1)throw Error('Expected the exact explicitly saved expiry policy.');
  await pick(host,'policyId',policies[0].id+':'+policies[0].revision);await submit(panel+' > form',60000);await wait(`document.querySelector('[data-lifecycle-execute]')?.disabled===false&&${idle}`,60000);await click('[data-lifecycle-execute]',60000);
  await wait(`document.querySelector('[data-lifecycle=pending]').hidden&&${idle}`,60000);
  const expired=await latest('lifecycle/execute');if(expired.status!==200||expired.result.operationId!==expired.body.operationId||expired.body.target.scopeId!==c.tradeId||expired.body.target.bindingId!==c.instrumentId)throw Error('Expiry did not acknowledge its exact retained lifecycle request.');
  if(index===0){
   await evaluate(`delete window.fixtureReadReferences[${q(expired.body.context.contextId)}]`);
   const review=await evaluate(`post('lifecycle/preview',${q(expired.body)})`);
   if(!review.details.acknowledgement)throw Error('An exact expiry retry presented a new rights effect.');
   await evaluate(`delete window.fixtureReadReferences[${q(expired.body.context.contextId)}]`);
   const replay=await evaluate(`post('lifecycle/execute',${q(expired.body)})`);
   if(!same(replay,expired.result))throw Error('Expiry recovery changed its original outcome.');
  }
  await priced(c,index===0?'-7.5':c.signedFee,'expiry');
 }
 await openTrade(cases[0]);await click('#blotter-dock [data-maximize=details]');await evaluate("document.querySelector('#position-details').scrollIntoView({block:'start'})");await shot('transport-financial-closed');await click('#blotter-dock [data-maximize=details]');
 const selected=await evaluate(`post('date-schedules/context',${q({effectiveOn:history.context.effectiveOn,knownAt:history.context.knownAt,mode:'at'})})`);
 if(['contextId','effectiveOn','knownAt','knownThrough'].some(key=>selected.context[key]!==history.context[key]))throw Error('The original financial input coordinates changed.');
 const old=await read(cases[0],'initial',{...context('initial'),knowledgeMode:'retained',contextId:history.context.contextId});if(old.presentValue!=='12.5'||old.draft.trade.capacity_version===version)throw Error('Recorded knowledge did not retain its original rights and payment state.');
 for(const exact of [true,false]){
  const body=structuredClone(originalMark.body);if(!exact)body.operationId=await evaluate('crypto.randomUUID()');
  if(exact){
   await evaluate(`delete window.fixtureReadReferences[${q(body.context.contextId)}]`);
   const missing={...body,operationId:await evaluate('crypto.randomUUID()')};
   if(!await evaluate(`(async()=>{try{await post('gas/transport/financial/preview',${q(missing)});return false;}catch(error){return error.message.includes('temporal_context');}})()`))throw Error('A new acknowledgement borrowed another command\'s owned inputs.');
   const wrongDate=structuredClone(body);wrongDate.context.businessDate='2020-12-17';
   if(!await evaluate(`(async()=>{try{await post('gas/transport/financial/preview',${q(wrongDate)});return false;}catch(error){return error.message.includes('gas_transport_financial_context');}})()`))throw Error('Recovery accepted a different financial business date.');
  }
  const preview=await evaluate(`post('gas/transport/financial/preview',${q(body)})`);if(!preview.acknowledgement)throw Error('A replay presented a fresh financial effect.');
  if(exact)await evaluate(`delete window.fixtureReadReferences[${q(body.context.contextId)}]`);
  const outcome=await evaluate(`post('gas/transport/financial/record',${q(body)})`);
  if(exact&&!same(outcome.outcome,originalMark.result.outcome)||!exact&&outcome.outcome.changed!==false)throw Error('Financial recovery changed its first outcome.');
  await priced(cases[0],'-7.5','expiry');
 }
 await check('recorded knowledge and both acknowledgement variants preserve closed rights and independent fee cash',`document.querySelector('#snapshot').value===${q(f.closes.expiry)}`);
 // Market authoring uses the ordinary source editor, independently of financial adoption.
 await asActor('viewer');await at('initial');await click('#nav-market',60000);await click('.curve-button[data-curve=provider_transport_rights]',60000);await click('[data-view=inputs]',60000);
 await wait("!!document.querySelector('#inputs tbody tr button')&&"+idle,60000);await click('#inputs tbody tr button',60000);
 await check('financial read authority cannot correct the separately published whole-package source',"document.querySelector('#correction-form').hidden&&document.querySelector('#changes-title').textContent.includes('Versions')");
 await asActor('editor');await at('initial');await click('#nav-market',60000);await click('.curve-button[data-curve=provider_transport_rights]',60000);await click('[data-view=inputs]',60000);
 await wait("!!document.querySelector('#inputs tbody tr button')&&"+idle,60000);await click('#inputs tbody tr button',60000);
 await check('ordinary market authoring exposes the exact signed R2 source without a financial adoption control',`!document.querySelector('#correction-form').hidden&&document.querySelector('#correction-value').value==='-20'&&[...document.querySelectorAll('[data-transport-financial-command]')].every(form=>!form.getClientRects().length)&&calls.findLast(call=>call.path==='/api/evaluate').result.inputSelection.source.definitionRevisionId===${q(f.valuationDefinition)}`);
 await set('#correction-value','-21');await submit('#correction-form',60000);
 const corrected=await latest('corrections');if(corrected?.status!==200||corrected.body.observationId!==cases[1].observationId||corrected.body.value!=='-21'||corrected.result.observationId===cases[1].observationId)throw Error('Ordinary source correction changed another observation or lost the retained original.');
 await check('the genuine R2 correction retains both signed versions and its exact source identity',`document.querySelector('#revisions').textContent.includes('-21')&&document.querySelector('#revisions').textContent.includes('-20')&&calls.findLast(call=>call.path==='/api/evaluate').result.inputSelection.source.definitionRevisionId===${q(f.valuationDefinition)}&&document.querySelector('#snapshot').value===${q(corrected.result.frame.id)}`);await evaluate("document.querySelector('#revision-card').scrollIntoView({block:'center'})");await shot('transport-financial-market-source');
}

if(process.argv.length!==4)throw Error('Use the owned financial Transport browser qualifier.');
console.log(`${await withOwnedBrowser(process.argv[2],process.argv[3],async driver=>{try{await run(driver);}catch(error){console.error('Owned financial Transport state',JSON.stringify(await driver.evaluate("({error:document.querySelector('#error')?.textContent,panel:document.querySelector('[data-lifecycle=panel]')?.textContent,ticket:document.querySelector('#transport-ticket .ticket-status')?.textContent,invalid:[...document.querySelectorAll(':invalid')].filter(node=>node.getClientRects().length).map(node=>({name:node.name,value:node.value,message:node.validationMessage})),calls:window.calls?.slice(-12).map(call=>({path:call.path,status:call.status,result:call.status>=400?call.result:undefined}))})").catch(failure=>({unavailable:failure.message}))));throw error;}})} full-app financial Transport browser checks passed.`);
