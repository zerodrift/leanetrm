import {withOwnedBrowser} from './owned-browser.mjs';
import {startNewTrade} from './new-trade.mjs';
import {gasTicket} from './gas-ticket.mjs';
import {sideCards,serviceFieldGroups,sidePhone} from './instrument-side-qualification.mjs';

const q=JSON.stringify,panel='[data-lifecycle=panel]',product='physical_gas_park_loan';
// Compare independent literal oracles with the report's exact transport; this
// test sums separate measure totals without binary-number rounding.
function fraction(value){
 if(/^-?\d+\/[1-9]\d*$/.test(value))return value.split('/').map(BigInt);
 if(!/^-?\d+(?:\.\d+)?$/.test(value))throw Error('Invalid exact report amount '+value);
 const [whole,part='']=value.split('.'),negative=whole.startsWith('-');
 return[(negative?-1n:1n)*BigInt(whole.replace('-','')+part),10n**BigInt(part.length)];
}
function exactTotal(values,expected){
 const [n,d]=values.map(fraction).reduce(([a,b],[c,e])=>[a*e+c*b,b*e],[0n,1n]),[x,y]=fraction(expected);
 return n*y===x*d;
}

async function run(driver){
 const {command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,fixture:f}=driver;
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle,60000);await persona('trader');
 await evaluate(`(async()=>{window.calls=[];window.agreements=[];const send=window.fetch;window.fetch=async(input,init)=>{const path=String(input),r=await send(input,init);if(path.startsWith('/api/')&&init?.body&&r.headers.get('content-type')?.includes('application/json'))calls.push({path,body:JSON.parse(init.body),status:r.status,result:await r.clone().json()});if(window.loseBooking&&path==='/api/tickets/book'&&r.ok){window.loseBooking=false;throw new TypeError('Synthetic lost balance-fee booking reply');}if(window.loseDetermination&&path==='/api/lifecycle/execute'&&r.ok){window.loseDetermination=false;throw new TypeError('Synthetic lost accepted fee determination reply');}return r;};
 const token=(await(await window.workspaceFetch('/api/bootstrap?includeFrames=false')).json()).csrfToken;window.post=async(path,body)=>{const response=await window.workspaceFetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)}),value=await response.json();if(!response.ok)throw Error(path+': '+JSON.stringify(value));return value;};})()`);
 await click('#profile-open');await set('#zone','America/Chicago');await click('#profile-save',60000);
 const {ticket,control,choose,setInstant,current,until}=gasTicket(driver,'#park-loan-ticket .deal-ticket');
 const at=async label=>{await set('#close-session',f.session);await wait(idle);await set('#snapshot',f.closes[label]);await wait(`document.querySelector('#snapshot').value===${q(f.closes[label])}&&${idle}`,60000);};
 const context=label=>({snapshotId:f.closes[label],convention:'physical_gas_fixed_quantity_v4',knowledgeMode:'current'});
 const readTrade=(c,label)=>evaluate(`post('tickets/trade',{context:${q(context(label))},product:${q(product)},tradeId:${q(c.tradeId)},bindingId:${q(c.instrumentId)},revision:1})`);
 const amount=async(c,stage,label='flow')=>{
  const read=await readTrade(c,label);
  if(read.presentValue!==c.expected[stage].service)throw Error(`${c.serviceKind} ${c.perspective} ${c.feeDirection} ${stage}: expected remaining service${c.expected[stage].service}, received${read.presentValue}`);
  await check(`${c.serviceKind} ${c.perspective} ${c.feeDirection} ${stage} retains the exact remaining service value`,`${q(read.draft.trade.profile)}===${q(f.profile)}&&${q(Object.hasOwn(read.draft.trade,'fixed_fee_amount'))}===false`);
  return read;
 };
 const onlyChoice=async name=>{
  const selector=panel+' [name="'+name+'"]';await wait(`document.querySelector(${q(selector)})?.options.length>1`,60000);
  const values=await evaluate(`[...document.querySelector(${q(selector)}).options].filter(option=>option.value!==''&&option.value!=='@null').map(option=>option.value)`);
  if(values.length!==1)throw Error('Expected one exact backend choice for '+name+': '+JSON.stringify(values));
  await set(selector,values[0]);return values[0];
 };
 const local=instant=>evaluate(`(()=>{const p=Object.fromEntries(new Intl.DateTimeFormat('en-CA',{timeZone:'America/Chicago',hourCycle:'h23',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit'}).formatToParts(new Date(${q(instant)})).map(p=>[p.type,p.value]));return p.year+'-'+p.month+'-'+p.day+'T'+p.hour+':'+p.minute+':'+p.second;})()`);
 const action=async(c,key,values,{lost=false}={})=>{
  await click('#nav-lifecycle',60000);await wait(`!!document.querySelector('[data-lifecycle=rows] .table-search')&&${idle}`,60000);
  const discovery=await evaluate("(()=>{const snapshot=document.querySelector('#snapshot').value;return calls.findLast(call=>call.path==='/api/lifecycle/view'&&call.status===200&&call.body.snapshotId===snapshot);})()");
  const offers=discovery?.result?.actions?.filter(offer=>offer.target.handler==='gas_park_loan'&&offer.target.scopeId===c.tradeId&&offer.target.bindingId===c.instrumentId&&offer.action==='gas.park_loan.'+key)??[];
  if(offers.length!==1||!offers[0].subject?.reference)throw Error('Missing exact balance-fee Lifecycle action '+key+': '+JSON.stringify(discovery?.result));
  await set('[data-lifecycle=rows] .table-search',offers[0].subject.reference);
  const labels={advance:'Accept principal advance',return:'Accept principal return',fee_determine:'Determine accrued service fee',fee_payment:'Record service fee payment'};
  const row=`[...document.querySelectorAll('[data-lifecycle=rows] .tabulator-row[data-row-key]')].find(row=>{const k=JSON.parse(row.dataset.rowKey);return k[0]==='gas_park_loan'&&k[1]===${q(c.tradeId)}&&k[2]===${q(c.instrumentId)}&&row.textContent.includes(${q(labels[key])});})`;
  await wait(`!!(${row})&&${idle}`,60000);await evaluate(`(${row}).click()`);await wait(`!!document.querySelector('${panel} [name=serviceVersion]')&&${idle}`,60000);
  await check('native profile actions require explicit captured version and policy',`document.querySelector('${panel} [name=serviceVersion]').value===''&&document.querySelector('${panel} [name=policyId]').value===''`);
  await onlyChoice('policyId');await onlyChoice('serviceVersion');
  if(key==='fee_determine'){
   const nullable=await evaluate(`!!document.querySelector('${panel} [name=previousDetermination] option[value="@null"]')`);
   if(nullable)await set(panel+' [name=previousDetermination]','@null');else{
    await onlyChoice('previousDetermination');
    await check('the previous determination choice names its retained rounded fee and exact currency',`document.querySelector('${panel} [name=previousDetermination]').selectedOptions[0].textContent===${q('Rounded cumulative fee '+c.firstCash+' '+f.currency)}`);
   }
  }
  if(key==='fee_payment')await onlyChoice('feeId');
  for(const [name,value]of Object.entries(values))await set(panel+' [name="'+name+'"]',['effectiveAt','paidAt','through'].includes(name)?await local(value):value);
  await submit(panel+' form',60000);await wait(`!!document.querySelector('[data-lifecycle-review]')&&${idle}`,60000);
  const review=await evaluate("calls.filter(call=>call.path==='/api/lifecycle/preview').at(-1)");
  if(review.status!==200||review.body.target.scopeId!==c.tradeId||review.body.target.bindingId!==c.instrumentId)throw Error('The fee review lost its exact native target.');
  if(key==='fee_determine'){
   const first=values.through===f.partialAt,expected={previous:first?'0':c.firstCash,total:first?c.firstCash:c.cumulative,contribution:first?c.firstCash:c.finalCash};
   const effect=review.result.details.effects;
   if(effect.previousCumulativeRoundedFee!==expected.previous||effect.cumulativeRoundedFee!==expected.total||effect.independentCash!==expected.contribution||Date.parse(effect.accruedThrough)!==Date.parse(values.through)||Date.parse(effect.dueAt)!==Date.parse(f.feeDueAt))throw Error('The backend fee review changed its explicit cumulative totals, contribution or times: '+JSON.stringify(effect));
   const display=async at=>evaluate(`new Intl.DateTimeFormat('en-US',{timeZone:'America/Chicago',dateStyle:'medium',timeStyle:'short'}).format(new Date(${q(at)}))`);
   const facts=await evaluate("Object.fromEntries([...document.querySelectorAll('[data-lifecycle-review] dt')].map(label=>[label.textContent,label.nextElementSibling.textContent]))");
   if(facts['Previous rounded cumulative fee']!==expected.previous||facts['New rounded cumulative fee']!==expected.total||facts['Signed fee contribution']!==expected.contribution||facts['Observed accrual through']!==await display(values.through)||facts['Fee due']!==await display(f.feeDueAt))throw Error('The person cannot review the exact fee contribution and user-zone times: '+JSON.stringify(facts));
   if(!first&&facts['Previous cumulative determination']!=='Rounded cumulative fee '+c.firstCash+' '+f.currency)throw Error('The selected previous fee label changed between entry and review: '+JSON.stringify(facts));
   await check('fee review shows prior and new cumulative totals, signed cash and explicit user-zone times',"document.querySelector('[data-lifecycle-execute]')?.disabled===false");
   await evaluate("document.querySelector('[data-lifecycle-review]').scrollIntoView({block:'center'})");await shot('gas-balance-fee-review');
   if(lost){
    await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
    await evaluate("new Promise(resolve=>requestAnimationFrame(()=>{[...document.querySelectorAll('[data-lifecycle-review] dt')].find(label=>label.textContent==='Observed accrual through').scrollIntoView({block:'start'});requestAnimationFrame(resolve);}))");
    await check('the live monetary fee review is visible and fits a phone viewport',`${fitsWidth}&&(()=>{const label=[...document.querySelectorAll('[data-lifecycle-review] dt')].find(label=>label.textContent==='Signed fee contribution'),r=label.getBoundingClientRect();return r.top>=0&&r.bottom<=innerHeight&&document.querySelector('[data-lifecycle-execute]').disabled===false;})()`);
    await shot('gas-balance-fee-mobile');
    await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});await evaluate("document.querySelector('[data-lifecycle-review]').scrollIntoView({block:'center'})");
   }
  }
  if(lost)await evaluate('window.loseDetermination=true');
  await wait(`document.querySelector('[data-lifecycle-execute]')?.disabled===false&&${idle}`,60000);await click('[data-lifecycle-execute]',60000);
  if(lost){
   await wait(`!document.querySelector('[data-lifecycle=pending]').hidden&&${idle}`,60000);await click('[data-lifecycle=retry]',60000);
   await check('a lost fee acknowledgement retries its exact original command and cash outcome',"(()=>{const calls=window.calls.filter(call=>call.path==='/api/lifecycle/execute').slice(-2);return calls.length===2&&JSON.stringify(calls[0].body)===JSON.stringify(calls[1].body)&&JSON.stringify(calls[0].result)===JSON.stringify(calls[1].result)&&document.querySelector('[data-lifecycle=pending]').hidden;})()");
  }
  return evaluate("calls.filter(call=>call.path==='/api/lifecycle/execute'&&call.status===200).at(-1).result");
 };
 const workspaceRisk=async c=>{
  await click('#nav-risk',60000);await wait(`!![...document.querySelector('#risk-workspace-books').options].find(option=>option.value===${q(c.bookId)})&&${idle}`,60000);
  const picker='#risk-workspace-books-picker';await click(picker+'>summary');
  const previous=await evaluate(`[...document.querySelectorAll('${picker} input[type=checkbox]:checked')].map(input=>input.value).filter(id=>id!==${q(c.bookId)})`);
  for(const id of previous)await click(picker+' input[type=checkbox][value="'+id+'"]',60000);
  if(!await evaluate(`document.querySelector('${picker} input[type=checkbox][value="${c.bookId}"]').checked`))await click(picker+' input[type=checkbox][value="'+c.bookId+'"]',60000);
  if(await evaluate(`document.querySelector('${picker}').open`))await click(picker+'>summary');
  await wait(`!!document.querySelector('#risk-workspace-pricing-policy')&&${idle}`,60000);
  const policy=await evaluate("(()=>{const choices=calls.filter(call=>call.path==='/api/portfolio/risk/setup'&&call.status===200).at(-1)?.result.pricingPolicies.filter(choice=>choice.wholeBooksOnly);if(choices?.length!==1)throw Error('Expected an explicit installed whole-book ownership policy');return choices[0].id;})()");
  await set('#risk-workspace-pricing-policy',policy);await wait(idle,60000);await click('#risk-jobs-new',60000);await wait(`document.querySelector('#workspace-risk-fields')?.disabled===false&&${idle}`,60000);
  const catalog=await evaluate(`calls.findLast(call=>call.path==='/api/portfolio/risk/catalog'&&call.status===200&&call.body.bookIds?.length===1&&call.body.bookIds[0]===${q(c.bookId)}&&call.body.pricingPolicy?.gasOwned)`);
  const target=catalog?.result.scenarioTargets.find(target=>target.kind==='contract'&&target.factor?.target?.curve?.code==='future'&&target.factor?.target?.contract?.id===f.contractId);
  if(!target)throw Error('The normal Risk catalog omitted the exact native principal factor.');
  await click('#workspace-risk-form input[value=custom]');await set('#workspace-risk-factor',target.id);await set('#workspace-risk-bump','1');
  const count=await evaluate("calls.filter(call=>call.path==='/api/portfolio/risk').length");
  await wait(`document.querySelector('#workspace-risk-run').disabled===false&&${idle}`,60000);await click('#workspace-risk-run',60000);
  await wait(`calls.filter(call=>call.path==='/api/portfolio/risk').length>${count}&&document.querySelector('#risk-workspace-results [data-state=complete]')&&${idle}`,60000);
  const run=await evaluate("calls.filter(call=>call.path==='/api/portfolio/risk').at(-1)");
  if(run.status!==200||JSON.stringify(run.body.pricingPolicy)!==JSON.stringify(catalog.result.pricingPolicy)||run.body.bookIds.length!==1||run.body.bookIds[0]!==c.bookId||run.body.tradeIds!==null)throw Error('The normal Risk run lost its explicit policy or whole-book scope.');
  const totals=run.result.factors[0].totals;
  if(!totals.length||totals.some(row=>row.bookId!==c.bookId||!row.values.currencyReference)||new Set(totals.map(row=>JSON.stringify(row.values.currencyReference))).size!==1)throw Error('Expected exact measure totals for one native currency/book: '+JSON.stringify(totals));
  const serviceMeasure='scheduled_plan_present_value_v1',nativeMeasure='gas_owned_present_value_v1';
  const measures=new Map([[serviceMeasure,'Remaining scheduled plan PV'],[nativeMeasure,'Independent gas holdings and cash PV']]);
  if(totals.length!==2||totals.some(row=>row.measureLabel!==measures.get(row.measure))||new Set(totals.map(row=>row.measure)).size!==2)throw Error('Expected separately labelled remaining service and independent native measure totals: '+JSON.stringify(totals));
  for(const row of totals)for(const key of ['base','up','down']){
   const expected=row.measure===serviceMeasure?c.expected.determined_first.service:c.expected.determined_first.native[key];
   if(!exactTotal([row.values[key]],expected))throw Error(`Independent ${row.measure} ${key}: expected${expected}, received${JSON.stringify(row)}`);
  }
  for(const key of ['base','up','down'])if(!exactTotal(totals.map(row=>row.values[key]),c.expected.determined_first.book[key]))throw Error(`Independent whole-book ${key}: expected${c.expected.determined_first.book[key]}, received${JSON.stringify(totals)}`);
  const positions=run.result.inputs.positions,owners=positions.map(position=>JSON.stringify([position.owner,position.bindingId]));
  if(new Set(owners).size!==owners.length)throw Error('The remaining service and determined native cash reused an ownership position.');
  const service=positions.filter(position=>position.instrumentType===product),cash=positions.filter(position=>position.instrumentType==='cash_payment'),physical=positions.filter(position=>position.instrumentType!==product&&position.instrumentType!=='cash_payment');
  if(service.length!==1||service[0].owner.kind!==1||service[0].owner.id!==c.tradeId||service[0].measure!==serviceMeasure||service[0].quantity.amount!==c.expected.determined_first.service||cash.length!==1||cash[0].owner.kind!==2||cash[0].owner.id!==(c.receiver?c.returnId:c.claimId)||cash[0].bindingId!==c.firstFeeId||cash[0].measure!==nativeMeasure||cash[0].quantity.amount!==c.firstCash||physical.length!==2||physical.some(position=>position.owner.kind!==2||position.measure!==nativeMeasure))throw Error('Expected one exact source owner, one determined cash binding, and the two independent physical owners: '+JSON.stringify(positions));
  const principal=c.receiver?[[c.stockId,'owned_stock','450'],[c.returnId,'loan_return','-450']]:[[c.claimId,'operator_claim','450'],[null,'owned_stock','270']];
  for(const[id,type,quantity]of principal)if(physical.filter(position=>(id===null||position.owner.id===id)&&position.instrumentType===type&&position.quantity.amount===quantity).length!==1)throw Error('Missing exact accepted principal owner '+id+': '+JSON.stringify(physical));
  // The existing sensitivity view explicitly omits unchanged book measures.
  // These nominal fees do not move with the gas mark; receiver principal nets
  // to zero, while giver principal retains its independent mark exposure.
  const visibleMeasure=c.receiver?'This shock moves no book measure.':measures.get(nativeMeasure);
  await check('normal Risk counts remaining service and each independent native owner once',`document.querySelector('#risk-workspace-results .risk-result-heading').textContent.includes('Sensitivity report')&&document.querySelector('#risk-workspace-results').textContent.includes(${q(visibleMeasure)})&&document.querySelector('#risk-workspace-results').textContent.includes('book measures a shock leaves unchanged are not listed')`);
  await evaluate("document.querySelector('#risk-workspace-results').scrollIntoView({block:'start'})");await shot('gas-balance-fee-risk');
 };

 const bookAgreement=async(input,{entryContext='before',advanceAt=f.advanceAt,returnAt=f.returnAt,cutoffAt=f.balanceFee.accrualThroughAt,feeDueAt=f.feeDueAt}={})=>{
  await at(entryContext);await click('#nav-futures',60000);await startNewTrade(driver);await click('#new-trade-park-loan',60000);
  await wait(`!!document.querySelector(${q(control(null,'profile'))})&&${idle}`,60000);
  await check('an unselected commercial profile exposes no assumed fee law',`document.querySelector(${q(ticket+' [data-field=fixed_fee_amount]')})===null&&document.querySelector(${q(ticket+' [data-field=fee_rate_amount]')})===null`);
  await choose(null,'profile',f.profile);await until(`!!document.querySelector(${q(control(null,'fee_rate_amount'))})&&!document.querySelector(${q(control(null,'fee_rate_amount'))}).disabled`);
  await check('balance-time entry has no inert fixed fee or no-date shortcut',`document.querySelector(${q(ticket+' [data-field=fixed_fee_amount]')})===null&&document.querySelector(${q(ticket+' [data-field=fee_due_at] [data-role=not-applicable]')})===null&&document.querySelector(${q(control(null,'fee_rate_amount'))}).value===''&&document.querySelector(${q(control(null,'fee_rounding_increment'))}).value===''`);
  for(const[id,value]of [['book',input.bookId],['legal_entity',input.legalEntityId+'@1'],['counterparty',input.counterpartyId+'@1'],['service_kind',input.serviceKind],['perspective',input.perspective],['owned_account',input.ownedAccountId],['claim_account',input.claimAccountId??'none'],['title_policy','fungible_gas_title_transfer_return_v1'],['return_policy','same_quantity_partial_returns_v1'],['return_timing','accepted_advance_then_returns_v1'],['carrying','nominal_mark_v1'],['fee_direction',input.feeDirection]])await choose(null,id,value);
  for(const[field,member]of Object.entries({fee_basis:'basis',fee_time_basis:'timeBasis',fee_start_policy:'startPolicy',fee_end_policy:'endPolicy',fee_overdue_policy:'overduePolicy',fee_projection_policy:'projectionPolicy',fee_unadvanced_policy:'unadvancedPolicy',fee_determination_policy:'determinationPolicy',fee_rounding_tie_rule:'roundingTieRule',fee_rounding_stage:'roundingStage'}))await choose(null,field,f.balanceFee[member]);
  await set(control(null,'principal_quantity'),'720');await set(control(null,'fee_rate_amount'),'0');await set(control(null,'fee_rounding_increment'),f.balanceFee.roundingIncrement);
  await setInstant(null,'advance_at',advanceAt);await setInstant(null,'return_at',returnAt);await setInstant(null,'fee_accrual_through_at',cutoffAt);await setInstant(null,'fee_due_at',feeDueAt);await until(current());
  await serviceFieldGroups(driver,ticket,['agreement','principal','fee'],'balance-fee edit distinguishes agreement, principal Sides and the independent selected fee law');
  await sideCards(driver,ticket,await evaluate("calls.findLast(call=>call.path==='/api/tickets/preview'&&call.status===200).result"),{label:'zero balance-time rate retains genuine principal Sides without inventing cash',roles:['Gas principal claim','Gas principal claim']});
  await check('a zero rate still retains the explicit due time and selected laws',"(()=>{const call=calls.filter(call=>call.path==='/api/tickets/preview'&&call.status===200).at(-1);return call.body.draft.trade.fee_rate_amount==='0'&&call.body.draft.trade.fee_due_at!=='none'&&!Object.hasOwn(call.body.draft.trade,'fixed_fee_amount');})()");
  await set(control(null,'fee_rate_amount'),f.balanceFee.rateAmount);await until(current());
  await evaluate(`document.querySelector(${q(ticket+' [data-field=fee_rate_amount]')}).scrollIntoView({block:'center'})`);await shot('gas-balance-fee-entry');
  if(await evaluate('agreements.length')===0)await evaluate('window.loseBooking=true');
  await wait(`document.querySelector(${q(ticket+' .ticket-book')})?.disabled===false&&${idle}`,60000);
  await click(ticket+' .ticket-book',60000);
  const recovery=`[...document.querySelectorAll('#park-loan-ticket .ticket-booking button')].find(button=>button.textContent==='Check booking status'&&!button.disabled)`;
  if(await evaluate('agreements.length')===0){await wait(`!!(${recovery})`,60000);await check('lost booking recovery keeps the selected fee profile and original terms locked',"document.querySelector('#park-loan-ticket > div').inert");await evaluate(`(${recovery}).click()`);await wait(idle,60000);}
  const matchingBooking=`call=>call.path==='/api/tickets/book'&&call.body.draft.trade.owned_account===${q(input.ownedAccountId)}`;
  await wait(`calls.some(${matchingBooking})`,60000);
  const booking=await evaluate(`calls.findLast(${matchingBooking})`);
  if(booking.status!==200)throw Error('The normal shared ticket booking was refused: '+JSON.stringify(booking));
  const c={...input,...booking.result};
  await evaluate(`agreements.push(${q(c)})`);await wait(`!!document.querySelector('#position-details .deal-ticket[data-mode=booked] [data-field=fee_rate_amount] .ticket-value')&&${idle}`,60000);
  const reopened=await amount(c,'before',entryContext);
  await sideCards(driver,'#position-details',reopened,{label:'saved balance-time contract retains exact principal and undetermined fee Side identities',retained:true,roles:['Gas principal claim','Gas principal claim','Ordinary cash payment']});
  if(input===f.cases[0])await sidePhone(driver,'#position-details','gas-balance-fee-sides-mobile');
  if(reopened.draft.trade.fee_rate_amount!==f.balanceFee.rateAmount||Date.parse(reopened.draft.trade.fee_accrual_through_at)!==Date.parse(cutoffAt)||reopened.draft.trade.fee_direction!==input.feeDirection)throw Error('Reopening changed an exact selected fee term.');
  return c;
 };

 const agreements=[];
 for(const input of f.cases)agreements.push(await bookAgreement(input));
 await at('flow');
 for(const [index,c]of agreements.entries()){
  const advanced=await action(c,'advance',{agreementEvidenceId:c.agreementEvidenceId,evidenceId:c.evidenceId,claimEvidenceId:c.claimEvidenceId??'@null',identifiedStock:c.receiver?'@null':c.fundingLotId+':none',effectiveAt:f.advanceAt});
  if(advanced.feeId!==null)throw Error('The balance-time advance manufactured a fixed-fee cash owner.');c.stockId=advanced.stockId;c.returnId=advanced.returnId;c.claimId=advanced.claimId;await amount(c,'advanced');
  await action(c,'return',{businessKey:'synthetic_balance_partial',evidenceId:c.partialEvidenceId,identifiedStock:c.receiver?c.stockId+':none':'@null',quantity:'270',effectiveAt:f.partialAt});await amount(c,'partial');
  const first=await action(c,'fee_determine',{businessKey:'synthetic_balance_first',through:f.partialAt,effectiveAt:'2020-12-20T11:05:00Z',evidence:'Synthetic explicitly reviewed first fee'},{lost:index===0});
  if(first.contribution!==c.firstCash||first.roundedTotal!==c.firstCash||!first.feeId)throw Error('The first independently determined fee is not the literal2.88 oracle.');
  c.firstFeeId=first.feeId;
  await amount(c,'determined_first');await workspaceRisk(c);
  await action(c,'fee_payment',{businessKey:'synthetic_balance_first_bank',expectedRevision:'0',paidAt:'2020-12-20T11:10:00Z',amount:c.firstCash,evidence:'Synthetic first independent bank payment'});await amount(c,'paid_first');
  await action(c,'return',{businessKey:'synthetic_balance_final',evidenceId:c.finalEvidenceId,identifiedStock:c.receiver?c.stockId+':none':'@null',quantity:'450',effectiveAt:f.returnAt});await amount(c,'returned');
  const final=await action(c,'fee_determine',{businessKey:'synthetic_balance_final_fee',through:f.returnAt,effectiveAt:'2020-12-20T12:05:00Z',evidence:'Synthetic explicitly reviewed final fee'});
  if(final.contribution!==c.finalCash||final.roundedTotal!==c.cumulative||final.feeId===first.feeId)throw Error('The final fee must create only its independent1.80 contribution with cumulative4.68.');
  await amount(c,'determined_final');await action(c,'fee_payment',{businessKey:'synthetic_balance_final_bank',expectedRevision:'0',paidAt:'2020-12-20T12:10:00Z',amount:c.finalCash,evidence:'Synthetic final independent bank payment'});await amount(c,'paid_final');
  if(index===0){
   // Current knowledge includes the accepted checkpoint. Rewinding only its
   // business time must refuse, while the next ordinary booking stays at flow.
   const rewind=await evaluate(`post('tickets/trade',{context:${q(context('before'))},product:${q(product)},tradeId:${q(c.tradeId)},bindingId:${q(c.instrumentId)},revision:1}).then(()=>({accepted:true}),()=>({accepted:false,call:calls.findLast(call=>call.path==='/api/tickets/trade')}))`);
   if(rewind.accepted||rewind.call.status!==409||rewind.call.result.code!=='gas_balance_fee_context')throw Error('A known business-future checkpoint was silently replaced by an earlier head: '+JSON.stringify(rewind));
   await check('rewinding business time refuses a known future checkpoint without changing the operating close',`document.querySelector('#snapshot').value===${q(f.closes.flow)}`);
   await bookAgreement(f.futureEntry,{entryContext:'flow',advanceAt:'2020-12-21T10:00:00Z',returnAt:'2020-12-21T12:00:00Z',cutoffAt:'2020-12-21T13:00:00Z',feeDueAt:'2020-12-22T16:00:00Z'});
   const workspace=await evaluate(`calls.findLast(call=>call.path==='/api/trading/workspace'&&call.body.context.snapshotId===${q(f.closes.flow)}&&call.status===200).result.blotter`);
   const unavailable=workspace.holdings.filter(holding=>holding.unavailableReason==='gas_balance_fee_advance_window');
   if(workspace.unvaluedPositions!==7||unavailable.length!==7||unavailable.some(holding=>holding.presentValue!==null)||JSON.stringify(unavailable.map(holding=>holding.tradeId).sort())!==JSON.stringify(agreements.slice(1).map(agreement=>agreement.tradeId).sort()))throw Error('The Blotter must keep each closed unadvanced plan explicitly unavailable: '+JSON.stringify(workspace));
   await check('a completed agreement does not poison an ordinary future booking at the current operating close',`document.querySelector('#snapshot').value===${q(f.closes.flow)}&&calls.findLast(call=>call.path==='/api/tickets/book').status===200`);
  }
 }
}

if(process.argv.length!==4)throw Error('Use the owned balance-time fee browser qualifier.');
console.log(`${await withOwnedBrowser(process.argv[2],process.argv[3],async driver=>{
 try{await run(driver);}catch(error){console.error('Owned balance-fee state',JSON.stringify(await driver.evaluate("({error:document.querySelector('#error')?.textContent,ticket:document.querySelector('#park-loan-ticket .ticket-status')?.textContent,panel:document.querySelector('[data-lifecycle=panel]')?.textContent,calls:window.calls?.slice(-12).map(call=>({path:call.path,status:call.status,code:call.result?.code}))})")));throw error;}
})} full-app balance-time fee browser checks passed.`);
