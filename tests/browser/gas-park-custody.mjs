import {withOwnedBrowser} from './owned-browser.mjs';
import {startNewTrade} from './new-trade.mjs';
import {gasTicket} from './gas-ticket.mjs';
import {sideCards,serviceFieldGroups,sidePhone} from './instrument-side-qualification.mjs';

const q=JSON.stringify,panel='[data-lifecycle=panel]',family='physical_gas_park_loan',native='physical_gas_park_custody';

async function run(driver){
 const {command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,fixture:f}=driver;
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle,60000);await persona('trader');
 await evaluate(`(async()=>{window.calls=[];window.custodyCases=[];const send=window.fetch;window.fetch=async(input,init)=>{const path=String(input),reply=await send(input,init);if(path.startsWith('/api/')&&init?.body&&reply.headers.get('content-type')?.includes('application/json'))calls.push({path,body:JSON.parse(init.body),status:reply.status,result:await reply.clone().json()});if(window.loseCustodyBooking&&path==='/api/tickets/book'&&reply.ok){window.loseCustodyBooking=false;throw new TypeError('Synthetic lost custody booking response');}if(window.loseCustodyEvidence&&path==='/api/gas/park-custody/evidence'&&reply.ok){window.loseCustodyEvidence=false;throw new TypeError('Synthetic lost accepted custody observation response');}if(window.loseCustodyCash&&path==='/api/cash-payments/record'&&reply.ok){window.loseCustodyCash=false;throw new TypeError('Synthetic lost accepted ordinary cash payment response');}if(window.loseCustodyAction&&path==='/api/lifecycle/execute'&&reply.ok){window.loseCustodyAction=false;throw new TypeError('Synthetic lost accepted custody response');}return reply;};
 const token=(await(await window.workspaceFetch('/api/bootstrap?includeFrames=false')).json()).csrfToken;window.post=async(path,body)=>{const response=await window.workspaceFetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)}),value=await response.json();if(!response.ok)throw Error(path+': '+JSON.stringify(value));return value;};})()`);
 await click('#profile-open');await set('#zone','America/Chicago');await click('#profile-save',60000);
 const {ticket,control,choose,setInstant,current,until}=gasTicket(driver,'#park-loan-ticket .deal-ticket');
 const context=label=>({snapshotId:f.closes[label],convention:'physical_gas_fixed_quantity_v4',knowledgeMode:'current'});
 const at=async label=>{await set('#close-session',f.session);await wait(idle);await set('#snapshot',f.closes[label]);await wait(`document.querySelector('#snapshot').value===${q(f.closes[label])}&&${idle}`,60000);};
 const readTrade=async(c,label='flow')=>evaluate(`post('tickets/trade',{context:${q(context(label))},product:${q(native)},tradeId:${q(c.tradeId)},bindingId:${q(c.instrumentId)},revision:1})`);
 const local=async instant=>evaluate(`(()=>{const p=Object.fromEntries(new Intl.DateTimeFormat('en-CA',{timeZone:'America/Chicago',hourCycle:'h23',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit'}).formatToParts(new Date(${q(instant)})).map(p=>[p.type,p.value]));return p.year+'-'+p.month+'-'+p.day+'T'+p.hour+':'+p.minute+':'+p.second;})()`);
 const display=async instant=>evaluate(`new Intl.DateTimeFormat('en-US',{timeZone:'America/Chicago',dateStyle:'medium',timeStyle:'short'}).format(new Date(${q(instant)}))`);
 const selectOnly=async selector=>{await wait(`document.querySelector(${q(selector)})?.options.length>1`,60000);const values=await evaluate(`[...document.querySelector(${q(selector)}).options].map(o=>o.value).filter(Boolean)`);if(values.length!==1)throw Error('Expected one exact captured choice: '+selector+' '+JSON.stringify(values));await set(selector,values[0]);return values[0];};
 const source=async(c,stage,expected,label='flow')=>{
  const read=await readTrade(c,label);if(read.presentValue!==expected)throw Error(`${c.perspective} ${c.feeDirection} ${stage}: expected service value ${expected}, received ${read.presentValue}`);
  if(read.draft.product!==family||read.draft.trade.profile!==f.profile||read.draft.trade.owned_account!==(c.ownedAccountId??'none')||read.draft.trade.point!==c.point)throw Error('The exact saved custody identity or independent point changed.');
  await check(`${c.perspective} ${c.feeDirection} ${stage} retains its independent fee and exact custody terms`,`${q(read.measureLabel)}==='Remaining custody service PV'&&${q(read.lifecycleTarget.handler)}==='gas_park_custody'&&${q(read.lifecycleTarget.scopeId)}===${q(c.tradeId)}&&${q(read.lifecycleTarget.bindingId)}===${q(c.instrumentId)}`);
  return read;
 };
 for(const input of f.cases){
  await at('before');await click('#nav-futures',60000);await startNewTrade(driver);await click('#new-trade-park-loan',60000);
  await until(`!!document.querySelector(${q(control(null,'profile'))})`);
  await check('Park custody is an explicit Park and Loan family profile, with no assumed fee law',`document.querySelector('#new-trade-park-custody')===null&&document.querySelector(${q(ticket+' [data-field=fixed_fee_amount]')})===null&&document.querySelector(${q(control(null,'profile'))}).value===''`);
  await choose(null,'profile',f.profile);await until(`!!document.querySelector(${q(control(null,'fixed_fee_amount'))})`);
  for(const [id,value]of [['book',input.bookId],['legal_entity',input.legalEntityId+'@1'],['counterparty',input.counterpartyId+'@1'],['service_kind','park'],['perspective',input.perspective],['reference_bundle',f.referenceBundle],['point',input.point],['quantity_specification',f.quantitySpecification],['currency',f.currency],['owned_account',input.ownedAccountId??'none'],['title_policy','customer_title_retained_v1'],['return_policy','same_point_same_basis_partial_custody_returns_v1'],['return_timing','accepted_custody_then_returns_v1'],['return_order','nondecreasing_accepted_custody_return_instants_v1'],['use_policy','no_use_or_sale_of_custody_v1'],['loss_policy','unqualified_loss_requires_reconciliation_v1'],['physical_policy','custody_only_no_inferred_flow_v1'],['plan_policy','unaccepted_custody_plan_from_later_of_earliest_or_evaluation_v1'],['fee_direction',input.feeDirection],['fee_recognition','accepted_custody_v1']])await choose(null,id,value);
  await set(control(null,'custody_quantity'),'720');await set(control(null,'fixed_fee_amount'),'12.5');
  for(const [id,value]of [['park_at',f.parkAt],['return_at',f.returnAt],['fee_due_at',f.feeDueAt]])await setInstant(null,id,value);
  await until(current());
  await serviceFieldGroups(driver,ticket,['agreement','custody','fee'],'custody edits distinguish retained-title performance from independent financial fee terms');
  if(input===f.cases[0]){
   await set(control(null,'fixed_fee_amount'),'0');
   await evaluate(`(()=>{const n=document.querySelector(${q(ticket+' [data-field=fee_due_at] [data-role=not-applicable]')});n.checked=true;n.dispatchEvent(new Event('change',{bubbles:true}));})()`);
   await until(current());
   await sideCards(driver,ticket,await evaluate("calls.findLast(c=>c.path==='/api/tickets/preview'&&c.status===200).result"),{label:'explicit zero-fee custody reports no financial Instrument and creates no gas Side',roles:[]});
   await set(control(null,'fixed_fee_amount'),'12.5');
   await evaluate(`(()=>{const n=document.querySelector(${q(ticket+' [data-field=fee_due_at] [data-role=not-applicable]')});n.checked=false;n.dispatchEvent(new Event('change',{bubbles:true}));})()`);
   await setInstant(null,'fee_due_at',f.feeDueAt);await until(current());
  }
  await sideCards(driver,ticket,await evaluate("calls.findLast(c=>c.path==='/api/tickets/preview'&&c.status===200).result"),{label:'conditional custody fee review contains one actual cash Side without invented principal/title Sides',roles:['Ordinary cash payment']});
  await check('the custody profile exposes no title-transfer principal mark, claim account or elapsed fee fields',`['mark_curve','claim_account','principal_quantity','fee_rate_amount'].every(id=>document.querySelector(${q(ticket)}+' [data-field="'+id+'"]')===null)`);
  if(input.perspective==='provider')await check('provider explicitly selects foreign custody without any inventory account',`document.querySelector(${q(control(null,'owned_account'))}).value==='none'&&document.querySelectorAll(${q(control(null,'owned_account')+' option')}).length===2`);
  await evaluate("document.querySelector('#park-loan-ticket .deal-ticket').scrollIntoView({block:'start'})");await shot(f.artifactPrefix+'-entry');
  await evaluate('window.loseCustodyBooking=true');await click(ticket+' .ticket-book',60000);
  const recover=`[...document.querySelectorAll('#park-loan-ticket .ticket-booking button')].find(b=>b.textContent==='Check booking status'&&!b.disabled)`;
  await wait(`!!(${recover})&&${idle}`,60000);await evaluate(`(${recover}).click()`);await wait(`document.querySelector('#position-details').dataset.mode==='position'&&${idle}`,60000);
  const booked=await evaluate(`calls.findLast(c=>c.path==='/api/tickets/book'&&c.status===200&&c.body.draft.trade.book===${q(input.bookId)})`);
  if(!booked)throw Error('The real custody booking was not acknowledged.');const c={...input,...booked.result};await evaluate(`custodyCases.push(${q(c)})`);
  const status=await evaluate("calls.findLast(c=>c.path==='/api/tickets/status')");
  if(status.body.product!==family||status.body.profile!==f.profile||status.body.operationId!==booked.body.operationId)throw Error('Booking recovery selected a different native family or profile.');
  await wait("!!document.querySelector('#position-details .deal-ticket[data-mode=booked]')&&"+idle,60000);
  await check('retained custody inspection has no commodity price navigation',"[...document.querySelectorAll('#position-details button')].filter(b=>b.textContent==='Open curve').every(b=>b.hidden)&&!document.querySelector('#position-details .ticket-add-kind')");
  const retained=await source(c,'booked conditional plan',c.signedFee,'before');
  await sideCards(driver,'#position-details',retained,{label:'saved custody retains only its exact independent cash Side and binding',retained:true,roles:['Ordinary cash payment']});
  if(input===f.cases[0])await sidePhone(driver,'#position-details',f.artifactPrefix+'-sides-mobile');
 }
 const cases=await evaluate('custodyCases');await at('flow');
 async function openAction(c,kind){
  await click('#nav-lifecycle',60000);await wait("!!document.querySelector('[data-lifecycle=rows] .table-search')&&"+idle,60000);
  const current=await evaluate("calls.findLast(c=>c.path==='/api/lifecycle/view'&&c.status===200)");
  const offers=current?.result?.actions?.filter(o=>o.target.handler==='gas_park_custody'&&o.target.scopeId===c.tradeId&&o.target.bindingId===c.instrumentId&&o.action==='gas.park_custody.'+kind)??[];
  if(offers.length!==1)throw Error('Missing exact custody action: '+JSON.stringify({c,kind,discovery:current?.result}));
  await set('[data-lifecycle=rows] .table-search',offers[0].subject.reference);
  const row=`[...document.querySelectorAll('[data-lifecycle=rows] .tabulator-row[data-row-key]')].find(row=>{const key=JSON.parse(row.dataset.rowKey);return key[0]==='gas_park_custody'&&key[1]===${q(c.tradeId)}&&key[2]===${q(c.instrumentId)}&&key[3]===${q('gas.park_custody.'+kind)};})`;
  await wait(`!!(${row})&&${idle}`,60000);await evaluate(`(${row}).click()`);await wait(`!!document.querySelector('${panel} [name=serviceVersion],${panel} [data-custody-observation]')&&${idle}`,60000);
  const read=await evaluate("calls.findLast(c=>c.path==='/api/gas/park-custody/setup'&&c.status===200)");
  if(!read||read.body.target.scopeId!==c.tradeId||read.body.target.bindingId!==c.instrumentId)throw Error('The provider-safe setup lost the immutable custody target.');
  return read.result;
 }
 // Drive the exact current metadata, including explicit empty arrays. Field
 // values below are observed fixture facts, never frontend business defaults.
 async function fillFields(host,fields,values){
  for(const field of fields){
   const input=host+' [name="'+field.name+'"]';
   if(field.kind==='array'){
    const entries=values[field.name];if(!Array.isArray(entries))throw Error('Missing explicit '+field.name+' fixture entries.');await set(input,entries.length?'entries':'empty');
    for(let i=0;i<entries.length;i++){
     await evaluate(`(()=>{const box=document.querySelector(${q(input)}).closest('.operation-field');[...box.children].find(node=>node.tagName==='BUTTON'&&node.textContent==='Add entry').click();})()`);
     const member=host+' [data-operation-entry="'+field.name+'"]';await fillFields(member+':nth-of-type('+(i+1)+')',field.fields,entries[i]);
    }
   }else if(field.kind==='reference'){
    const selected=field.choices.filter(choice=>field.members.every(key=>JSON.stringify(choice.value[key])===JSON.stringify(values[key])));
    if(selected.length!==1)throw Error('Missing exact '+field.name+' native reference '+JSON.stringify({values,choices:field.choices}));await set(input,selected[0].id);
   }else if(field.kind==='select'){
    const selected=field.choices.filter(choice=>JSON.stringify(choice.value??choice.id)===JSON.stringify(values[field.name]));
    if(selected.length!==1)throw Error('Missing exact '+field.name+' native selection.');await set(input,selected[0].id);
   }else{
    if(!Object.hasOwn(values,field.name))throw Error('Missing explicit '+field.name+' observation.');
    if(field.nullable)await set(host+' [name="'+field.name+'-disposition"]',values[field.name]===null?'none':'value');
    if(values[field.name]!==null)await set(input,field.kind==='instant'?await local(values[field.name]):String(values[field.name]));
   }
  }
 }
 async function observe(c,key,kind,quantity,observedAt,{lost=false,revision=0}={}){
  const setup=await openAction(c,kind==='custody_return'?'return':'accept'),host=panel+' [data-custody-observation]';
  await evaluate(`document.querySelector(${q(host)}).closest('details').open=true`);
  await fillFields(host,setup.configuration.evidence.fields,{sourceKey:key,expectedRevision:revision,kind,quantity,observedAt,evidence:kind==='custody_agreement'?'2020-12-19T08:00:00Z':'Synthetic independently observed custody evidence'});
  if(lost)await evaluate('window.loseCustodyEvidence=true');await submit(host,60000);
  if(lost){
   await wait("!document.querySelector('[data-lifecycle=pending]').hidden&&"+idle,60000);await click('[data-lifecycle=check-status]',60000);
   await check('lost custody evidence recovers its original target and source without a second observation write',"(()=>{const write=calls.filter(c=>c.path==='/api/gas/park-custody/evidence').at(-1),status=calls.filter(c=>c.path==='/api/gas/park-custody/evidence/status').at(-1);return status.status===200&&status.result.found===true&&JSON.stringify(write.body)===JSON.stringify(status.body)&&document.querySelector('[data-lifecycle=pending]').hidden;})()");
  }
  const result=await evaluate("calls.findLast(c=>c.path==='/api/gas/park-custody/evidence'&&c.status===200)");if(!result||result.body.target.scopeId!==c.tradeId)throw Error('The recorded observation lost its selected agreement.');return result.result.id;
 }
 async function perform(c,kind,values,{lost=false,expectRefusal=false}={}){
  const setup=await openAction(c,kind),form=panel+' > form',metadata=setup.forms['gas.park_custody.'+kind];
  await check('custody performance requires an explicit policy and source head',`document.querySelector('${form} [name=policyId]').value===''&&document.querySelector('${form} [name=serviceVersion]').value===''`);
  await set(form+' [name=policyId]',f.policies[kind]+':1');
  const head=metadata.fields.find(field=>field.name==='serviceVersion');if(head?.choices.length!==1)throw Error('Custody setup did not pin one exact current service head.');
  await fillFields(form,metadata.fields,{...values,...head.choices[0].value});
  const previews=await evaluate("calls.filter(call=>call.path==='/api/lifecycle/preview').length");await submit(form,60000);
  if(expectRefusal){
   await wait(`calls.filter(call=>call.path==='/api/lifecycle/preview').length>${previews}&&${idle}`,60000);
   const refused=await evaluate("calls.findLast(call=>call.path==='/api/lifecycle/preview')");
   if(refused.status!==422||!refused.result.code?.startsWith('gas_custody_'))throw Error('The out-of-limit raw receipt was not refused by its custody admission: '+JSON.stringify(refused));
   await check('an out-of-limit raw receipt remains recorded without becoming accepted custody',"document.querySelector('[data-lifecycle-execute]')===null&&calls.filter(call=>call.path==='/api/lifecycle/execute').every(call=>call.body.target.scopeId!=="+q(c.tradeId)+")");
   await evaluate("document.querySelector('[data-lifecycle=refresh]').click()");
   await wait("!document.querySelector('#lifecycle-page-prompt').hidden",60000);
   await evaluate("[...document.querySelectorAll('#lifecycle-page-prompt button')].find(button=>button.textContent==='Discard edits').click()");await wait(idle,60000);return;
  }
  await wait(`!!document.querySelector('[data-lifecycle-review]')&&${idle}`,60000);
  const review=await evaluate("calls.findLast(c=>c.path==='/api/lifecycle/preview')");
  if(review.status!==200||review.body.target.scopeId!==c.tradeId||review.result.details.willChange!==true)throw Error('The native custody preview did not retain this exact change.');
  const effect=review.result.details.effects,facts=await evaluate("Object.fromEntries([...document.querySelectorAll('[data-lifecycle-review] dt')].map(t=>[t.textContent,t.nextElementSibling.textContent]))");
  for(const [label,value]of [['Performed custody quantity',kind==='accept'?'720':values.quantity],['Custody after',kind==='accept'?'720':values.quantity==='270'?'450':'0'],['Owned quantity change','0'],['New signed fee cash',kind==='accept'?c.signedFee:'0']])if(facts[label]!==value)throw Error('The custody review changed '+label+': '+JSON.stringify(facts));
  if(facts['Observed custody time']!==await display(values.effectiveAt)||facts['Contractual return due']!==await display(f.returnAt)||facts['Fee due']!==await display(f.feeDueAt)||!facts['Title ownership'].includes('Retained by customer'))throw Error('The custody review lost its user-zone times or retained title.');
  if(c.perspective==='provider'&&(effect.allocations.length||effect.reservedAfter!=='0'))throw Error('Foreign custody manufactured owned reservations.');
  if(c.perspective==='customer'&&(effect.allocations.length!==1||effect.allocations[0].lotId!==c.lotId))throw Error('The customer custody review did not retain its identified stock lot.');
  await check(`${c.perspective} ${kind} reviews exact custody, reservation and independent fee effects before recording`,"document.querySelector('[data-lifecycle-execute]')?.disabled===false");
  await evaluate("document.querySelector('[data-lifecycle-review]').scrollIntoView({block:'center'})");await shot(f.artifactPrefix+'-review');
  if(kind==='accept'){
   await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
   await evaluate("new Promise(resolve=>requestAnimationFrame(()=>{[...document.querySelectorAll('[data-lifecycle-review] dt')].find(t=>t.textContent==='Custody before').scrollIntoView({block:'start'});requestAnimationFrame(resolve);}))");
   await check('the live custody review is readable within the phone viewport',`${fitsWidth}&&document.querySelector('[data-lifecycle-review]').getBoundingClientRect().bottom>0`);await shot(f.artifactPrefix+'-mobile');
   await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
  }
  if(lost)await evaluate('window.loseCustodyAction=true');await click('[data-lifecycle-execute]',60000);
  if(lost){await wait("!document.querySelector('[data-lifecycle=pending]').hidden&&"+idle,60000);await click('[data-lifecycle=retry]',60000);await check('a lost accepted custody acknowledgement retries the same reviewed command once',"(()=>{const pair=calls.filter(c=>c.path==='/api/lifecycle/execute').slice(-2);return pair.length===2&&JSON.stringify(pair[0].body)===JSON.stringify(pair[1].body)&&JSON.stringify(pair[0].result)===JSON.stringify(pair[1].result)&&document.querySelector('[data-lifecycle=pending]').hidden;})()");}
  return evaluate("calls.findLast(c=>c.path==='/api/lifecycle/execute'&&c.status===200).result");
 }
 async function pay(c){
  const setup=await openAction(c,'return'),form=panel+' [data-custody-payment]',metadata=setup.configuration.payment;
  await check('completed custody retains independent cash controls without another custody performance',`document.querySelector('${panel} > form')===null&&document.querySelector('[data-lifecycle-execute]')===null&&!!document.querySelector(${q(form)})&&document.querySelector('${panel}').textContent.includes('fully returned')`);

  if(!metadata||metadata.permission!=='AmendTrade')throw Error('The accepted independent fee has no ordinary shared payment metadata.');
  const owner=metadata.fields.find(field=>field.name==='cashOwner'),allocation=metadata.fields.find(field=>field.name==='allocation');
  if(owner?.choices.length!==1||owner.choices[0].value.tradeId!==c.tradeId||owner.choices[0].value.bindingId!==c.cashBindingId||owner.choices[0].value.snapshotId!==f.closes.flow||owner.choices[0].value.knowledgeMode!=='current')throw Error('Payment metadata did not pin the accepted cash owner and exact source.');
  const fresh=allocation?.choices.filter(choice=>choice.value.expectedRevision===0)??[];if(fresh.length!==1)throw Error('Choose one explicit new recorded payment allocation.');
  await evaluate(`document.querySelector(${q(form)}).closest('details').open=true`);
  await check('ordinary fee payment requires explicit cash owner, allocation and recorded-cash policy',`[...document.querySelectorAll(${q(form+' select[name]')})].every(select=>select.value==='')`);
  await fillFields(form,metadata.fields,{...owner.choices[0].value,...fresh[0].value,paidAt:'2020-12-20T14:00:00Z',amount:c.signedPayment,policy:'explicit_paid_cash_nominal_due_v1'});
  await evaluate('window.loseCustodyCash=true');await submit(form,60000);
  await wait("!document.querySelector('[data-lifecycle=pending]').hidden&&"+idle,60000);await check('the lost ordinary payment exposes shared receipt recovery',"!document.querySelector('[data-lifecycle=check-status]').hidden&&!document.querySelector('[data-lifecycle=check-status]').disabled");await click('[data-lifecycle=check-status]',60000);
  await wait("document.querySelector('[data-lifecycle=pending]').hidden&&"+idle,60000);
  const recovered=await evaluate("calls.findLast(call=>call.path==='/api/cash-payments/status')");
  const recorded=await evaluate("calls.findLast(call=>call.path==='/api/cash-payments/record')");
  if(recorded?.status!==200||recorded.body.tradeId!==c.tradeId||recorded.body.bindingId!==c.cashBindingId||recorded.body.amount!==c.signedPayment||recorded.body.allocationId!==fresh[0].value.allocationId||recorded.body.expectedRevision!==0)throw Error('The ordinary cash operation changed its explicit owner, amount or allocation.');
  if(recovered?.status!==200||recovered.result.found!==true||JSON.stringify(recovered.body)!==JSON.stringify(recorded.body)||recovered.result.outcome.operationId!==recorded.body.operationId||recovered.result.outcome.tradeId!==c.tradeId||recovered.result.outcome.bindingId!==c.cashBindingId||recovered.result.outcome.allocationId!==recorded.body.allocationId||!Number.isSafeInteger(recovered.result.outcome.revision)||recovered.result.outcome.revision<1)throw Error('Shared payment recovery did not attest the exact retained cash request and original revision.');
  await check('shared cash status confirms the original payment without another write',`calls.filter(call=>call.path==='/api/cash-payments/record'&&call.body.operationId===${q(recorded.body.operationId)}).length===1`);
  await source(c,'independently recorded bank payment',c.expectedUnpaid);
 }
 async function inspectHistory(c){
  await click('#nav-futures',60000);await wait(`document.querySelector('#blotter-books')?.options.length>0&&${idle}`,60000);
  if(!await evaluate("document.querySelector('#blotter-scope').open"))await click('#blotter-scope>summary');
  if(!await evaluate("document.querySelector('#blotter-books-picker').open"))await click('#blotter-books-picker>summary');
  const selected=await evaluate("[...document.querySelectorAll('#blotter-books-picker input[type=checkbox]:checked')].map(input=>input.value)");
  for(const id of selected)if(id!==c.bookId)await click('#blotter-books-picker input[type=checkbox][value="'+id+'"]',60000);
  if(!await evaluate(`document.querySelector('#blotter-books-picker input[type=checkbox][value="${c.bookId}"]').checked`))await click('#blotter-books-picker input[type=checkbox][value="'+c.bookId+'"]',60000);
  await click('#blotter-scope-apply',60000);
  const row='#desk-positions .tabulator-row[data-row-key="'+c.tradeId+':'+c.instrumentId+'"]';
  await wait(`!!document.querySelector(${q(row)})&&document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`,60000);await click(row,60000);
  await wait(`!!document.querySelector('#position-details .deal-ticket[data-mode=booked]')&&document.querySelectorAll('#position-details .physical-instrument dd[title]').length>0&&${idle}`,60000);
  const read=await evaluate(`calls.findLast(call=>call.path==='/api/tickets/trade'&&call.body.tradeId===${q(c.tradeId)}&&call.status===200).result`);
  const sections=read.overviewSections,marked=sections.flatMap(section=>section.values).filter(value=>value.kind==='instant');
  if(!sections.some(section=>section.label==='Accepted custody')||sections.filter(section=>section.label.startsWith('Accepted return ')).length!==2||sections.filter(section=>section.label.startsWith('Retained observation ')).length!==6||!f.restricted&&!sections.some(section=>section.label.startsWith('Retained bank payment '))||!marked.length)throw Error('Retained raw observations, accepted performance or independent bank history is missing.');
  const values=()=>evaluate("[...document.querySelectorAll('#position-details .physical-instrument .side-term dd')].map(cell=>({text:cell.textContent,title:cell.title}))");
  const chicago=await values();
  if(!marked.every(value=>chicago.some(cell=>cell.title===value.value&&cell.text!==value.value&&/CST|CDT|America\/Chicago/.test(cell.text))))throw Error('A declared native instant was not displayed in the chosen Chicago zone with exact retained evidence.');
  if(!sections.some(section=>section.label.includes('_unapplied_receipt')&&section.values.some(value=>value.label==='Status'&&value.value.includes('not applied'))))throw Error('An out-of-limit raw observation was displayed as accepted performance.');
  const unapplied=sections.filter(section=>section.label.includes('_unapplied_receipt'));
  if(unapplied.length!==2||!unapplied.some(section=>section.values.some(value=>value.label==='Status'&&value.value.includes('Superseded observation; not applied')))||!unapplied.some(section=>section.values.some(value=>value.label==='Status'&&value.value.includes('Current observation; not applied'))))throw Error('The retained raw correction lost its old revision or was shown as applied custody.');
  const plain='2020-12-19T08:00:00Z';if(!chicago.some(cell=>cell.text===plain&&cell.title===''))throw Error('ISO-looking raw evidence was guessed to be a timestamp.');
  await check(`${c.perspective} retained raw observations and accepted performance keep their distinct status and exact local times`,`${q(sections.some(section=>section.values.some(value=>value.label==='Status'&&value.value.includes('Pinned by accepted'))))}===true`);
  await click('#blotter-dock [data-maximize=details]');
  await evaluate("[...document.querySelectorAll('#position-details .physical-instrument h3')].find(label=>label.textContent==='Accepted custody').scrollIntoView({block:'start'})");await shot(f.artifactPrefix+'-'+c.perspective+'-history');
  await evaluate("[...document.querySelectorAll('#position-details .physical-instrument h3')].find(label=>label.textContent.includes('_unapplied_receipt')).scrollIntoView({block:'start'})");await shot(f.artifactPrefix+'-'+c.perspective+'-raw-history');
  await click('#blotter-dock [data-maximize=details]');
  await click('#profile-open');await set('#zone','UTC');await click('#profile-save',60000);
  await wait(`document.querySelectorAll('#position-details .physical-instrument dd[title]').length>=${new Set(marked.map(value=>value.value)).size}&&${idle}`,60000);
  const utc=await values();
  if(!marked.every(value=>utc.some(cell=>cell.title===value.value&&cell.text.includes('UTC')&&!/CST|CDT|America\/Chicago/.test(cell.text)))||!utc.some(cell=>cell.title&&chicago.some(before=>before.title===cell.title&&before.text!==cell.text))||!utc.some(cell=>cell.text===plain&&cell.title===''))throw Error('Changing display preferences did not redraw typed history times while preserving plain evidence.');
  await check('changing the actual user preference reformats only marked history instants',"document.querySelector('#snapshot').value==="+q(f.closes.flow));
  await click('#profile-open');await set('#zone','America/Chicago');await click('#profile-save',60000);
 }
 for(const c of cases){
  const key='synthetic_'+c.perspective+'_'+c.feeDirection;
  const agreement=await observe(c,key+'_agreement','custody_agreement','720','2020-12-19T08:00:00Z',{lost:true});
  const unapplied=await observe(c,key+'_unapplied_receipt','custody_receipt','730',f.parkAt);
  await source(c,'unapplied out-of-limit raw receipt',c.signedFee);
  await perform(c,'accept',{agreementEvidenceId:agreement,receiptEvidenceId:unapplied,effectiveAt:f.parkAt,allocations:c.lotId?[{lotId:c.lotId,quantity:'720'}]:[]},{expectRefusal:true});
  await observe(c,key+'_unapplied_receipt','custody_receipt','740',f.parkAt,{revision:1});
  await source(c,'corrected unapplied observation without financial performance',c.signedFee);
  const receipt=await observe(c,key+'_receipt','custody_receipt','720',f.parkAt);
  await source(c,'raw observations without acceptance',c.signedFee);
  const accepted=await perform(c,'accept',{agreementEvidenceId:agreement,receiptEvidenceId:receipt,effectiveAt:f.parkAt,allocations:c.lotId?[{lotId:c.lotId,quantity:'720'}]:[]},{lost:true});
  c.cashBindingId=accepted.cashBindingId;if(typeof c.cashBindingId!=='string'||!c.cashBindingId||c.cashBindingId===c.instrumentId)throw Error('Acceptance omitted its distinct ordinary fee cash owner.');
  let reservation=accepted.allocations[0]?.reservationId;
  await source(c,'accepted custody',c.signedFee);
  const partial=await observe(c,key+'_partial','custody_return','270',f.partialAt);
  const returned=await perform(c,'return',{businessKey:key+'_partial',evidenceId:partial,quantity:'270',effectiveAt:f.partialAt,allocations:reservation?[{reservationId:reservation,quantity:'270'}]:[]});
  reservation=returned.allocations[0]?.remainderReservationId;
  await source(c,'partial custody return',c.signedFee);
  const final=await observe(c,key+'_final','custody_return','450',f.finalAt);
  await perform(c,'return',{businessKey:key+'_final',evidenceId:final,quantity:'450',effectiveAt:f.finalAt,allocations:reservation?[{reservationId:reservation,quantity:'450'}]:[]});
  await source(c,'completed custody with independent unpaid fee',c.signedFee);
 }
 if(!f.restricted)for(const c of cases)await pay(c);
 for(const perspective of ['customer','provider']){const c=cases.find(value=>value.perspective===perspective);if(c)await inspectHistory(c);}
 if(f.restricted)await check('provider custody works without owned-gas or bank-payment authority',"!document.querySelector('[data-lifecycle=gas-settings]')||document.querySelector('[data-lifecycle=gas-settings]').hidden");
 await click('#as-of-context>summary');await click('#date-context-latest',60000);await click('#apply-view-context',60000);await click('#nav-lifecycle',60000);
 await openAction(cases.at(-1),'return');
 await check('historical custody inspection remains read only',"document.querySelector('#workspace-mode-label').textContent.includes('Historical')&&!!document.querySelector('[data-lifecycle=panel] form')&&[...document.querySelectorAll('[data-lifecycle=panel] form')].every(form=>form.inert)&&[...document.querySelectorAll('[data-lifecycle=panel] button')].every(button=>button.disabled)");
}

if(process.argv.length!==4)throw Error('Use the owned custody browser qualifier.');
console.log(`${await withOwnedBrowser(process.argv[2],process.argv[3],async driver=>{try{await run(driver);}catch(error){console.error('Owned custody state',JSON.stringify(await driver.evaluate("({error:document.querySelector('#error')?.textContent,ticket:document.querySelector('#park-loan-ticket .ticket-status')?.textContent,panel:document.querySelector('[data-lifecycle=panel]')?.textContent,invalid:[...document.querySelectorAll(':invalid')].filter(n=>n.getClientRects().length).map(n=>({name:n.name,value:n.value,message:n.validationMessage})),calls:window.calls?.slice(-12).map(c=>({path:c.path,status:c.status,result:c.status>=400?c.result:undefined}))})")));throw error;}})} full-app custody browser checks passed.`);
