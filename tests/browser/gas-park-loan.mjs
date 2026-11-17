import {withOwnedBrowser} from './owned-browser.mjs';
import {startNewTrade} from './new-trade.mjs';
import {gasTicket} from './gas-ticket.mjs';

const q=JSON.stringify,panel='[data-lifecycle=panel]';

async function run(driver){
 const {command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,fixture:f}=driver;
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(`!!document.querySelector('#profile-open')&&${idle}`,60000);await persona('trader');
 await evaluate(`(async()=>{window.calls=[];window.agreements=[];const send=window.fetch;window.fetch=async(input,init)=>{const path=String(input),r=await send(input,init);if(path.startsWith('/api/')&&init?.body&&r.headers.get('content-type')?.includes('application/json'))calls.push({path,body:JSON.parse(init.body),status:r.status,result:await r.clone().json()});if(window.loseBooking&&path==='/api/tickets/book'&&r.ok){window.loseBooking=false;throw new TypeError('Synthetic lost successful service booking reply');}if(window.loseAdvance&&path==='/api/lifecycle/execute'&&r.ok){window.loseAdvance=false;throw new TypeError('Synthetic lost accepted service advance reply');}return r;};
 const token=(await(await fetch('/api/bootstrap?includeFrames=false')).json()).csrfToken;window.post=async(path,body)=>{const r=await fetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)}),v=await r.json();if(!r.ok)throw Error(path+': '+JSON.stringify(v));return v;};})()`);
 await click('#profile-open');await set('#zone','America/Chicago');await click('#profile-save',30000);
 const at=async label=>{
  await set('#close-session',f.session);await wait(idle);await set('#snapshot',f.closes[label]);
  await wait(`document.querySelector('#snapshot').value===${q(f.closes[label])}&&${idle}`,60000);
 };
 const expected=(c,fee)=>c.receiver?[fee,fee,fee]:[2160+fee,2880+fee,1440+fee];
 const context=label=>({snapshotId:f.closes[label],convention:'physical_gas_fixed_quantity_v4',knowledgeMode:'current'});
 const {ticket,control,choose,setInstant,current,until}=gasTicket(driver,'#park-loan-ticket .deal-ticket');
 const firstChoice=async name=>{
  const selector=panel+' [name="'+name+'"]';await wait(`document.querySelector(${q(selector)})?.options.length>1`,60000);
  await set(selector,await evaluate(`document.querySelector(${q(selector)}).options[1].value`));
 };
 const local=async instant=>evaluate(`(()=>{const p=Object.fromEntries(new Intl.DateTimeFormat('en-CA',{timeZone:'America/Chicago',hourCycle:'h23',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit'}).formatToParts(new Date(${q(instant)})).map(p=>[p.type,p.value]));return p.year+'-'+p.month+'-'+p.day+'T'+p.hour+':'+p.minute+':'+p.second;})()`);
 const readTrade=async(c,label)=>evaluate(`post('tickets/trade',{context:${q(context(label))},product:'physical_gas_park_loan',tradeId:${q(c.tradeId)},bindingId:${q(c.instrumentId)},revision:1})`);
 let catalog;
 const value=async(c,label,expectedBase,expectedUp=expectedBase,expectedDown=expectedBase)=>{
  if(!catalog)catalog=await evaluate(`post('portfolio/risk/catalog',{context:{...${q(context(label))},convention:'trading_workspace_v1'},bookIds:[${q(c.bookId)}],portfolioIds:[]})`);
  const factor=catalog.scenarioTargets.find(t=>t.kind==='contract'&&t.factor?.target?.curve?.code==='future'&&t.factor?.target?.contract?.id===f.contractId)?.factor;
  if(!factor)throw Error('The native catalog omitted the exact saved principal factor.');
  const pricingPolicy={...catalog.pricingPolicy,gasOwned:{implementation:'gas_owned_mark_to_market_v1',scope:'all_independent_owners_in_selected_books_v1',cashDiscounting:'captured_currency_default_v1',timingPolicy:'linear_utc_day_discount_ratio_v1'}};
  const report=await evaluate(`post('portfolio/risk',{context:{...${q(context(label))},convention:'trading_workspace_v1'},bookIds:[${q(c.bookId)}],portfolioIds:[],bumps:[{factor:${q(factor)},size:'1',convention:${q(catalog.bumpConvention)}}],stress:[],convention:${q(catalog.convention)},numericalPolicy:${q(catalog.numericalPolicy)},pricingPolicy:${q(pricingPolicy)},detail:'holdings'})`);
  const totals=report.factors[0].totals;
  for(const [name,expected]of [['base',expectedBase],['up',expectedUp],['down',expectedDown]]){
   const actual=totals.reduce((sum,row)=>sum+Number(row.values[name]),0);
   if(actual!==expected)throw Error(`${c.perspective} ${label} whole-book ${name}: expected ${expected}, received ${actual}`);
  }
  await check(`${c.serviceKind} ${c.perspective} ${label} whole-book value and generic factor scenarios conserve the separate owners`,`${q(totals.every(row=>row.bookId===c.bookId))}===true`);
  return report;
 };
 const workspaceValue=async(c,expectedBase,expectedUp,expectedDown)=>{
  await click('#nav-risk',60000);await wait(`!![...document.querySelector('#risk-workspace-books').options].find(o=>o.value===${q(c.bookId)})&&${idle}`,60000);
  await check('normal Risk offers a backend-owned pricing scope selector',"!!document.querySelector('#risk-workspace-pricing-policy')");
  const picker='#risk-workspace-books-picker';await click(picker+'>summary');
  const previous=await evaluate(`[...document.querySelectorAll('${picker} input[type=checkbox]:checked')].map(n=>n.value).filter(id=>id!==${q(c.bookId)})`);
  for(const id of previous)await click(picker+' input[type=checkbox][value="'+id+'"]',60000);
  if(!await evaluate(`document.querySelector('${picker} input[type=checkbox][value="${c.bookId}"]').checked`))await click(picker+' input[type=checkbox][value="'+c.bookId+'"]',60000);
  if(await evaluate(`document.querySelector('${picker}').open`))await click(picker+'>summary');
  await wait("[...document.querySelector('#risk-workspace-pricing-policy').options].some(o=>o.value==='native-gas-v1')&&"+idle,60000);
  await set('#risk-workspace-pricing-policy','native-gas-v1');await wait(idle,60000);
  await click('#risk-jobs-new',60000);await wait("document.querySelector('#workspace-risk-fields')?.disabled===false&&"+idle,60000);
  const captured=await evaluate(`calls.findLast(c=>c.path==='/api/portfolio/risk/catalog'&&c.status===200&&c.body.bookIds?.length===1&&c.body.bookIds[0]===${q(c.bookId)}&&c.body.pricingPolicy?.gasOwned)`);
  if(!captured)throw Error('The normal Risk catalog did not retain the explicitly selected independent ownership policy.');
  const target=captured.result.scenarioTargets.find(t=>t.kind==='contract'&&t.factor?.target?.curve?.code==='future'&&t.factor?.target?.contract?.id===f.contractId);
  if(!target)throw Error('The normal Risk catalog omitted the held exact gas factor.');
  await click('#workspace-risk-form input[value=custom]');await set('#workspace-risk-factor',target.id);await set('#workspace-risk-bump','1');
  const count=await evaluate("calls.filter(c=>c.path==='/api/portfolio/risk').length");
  await wait("document.querySelector('#workspace-risk-run').disabled===false&&"+idle,60000);
  await click('#workspace-risk-run',60000);await wait(`calls.filter(c=>c.path==='/api/portfolio/risk').length>${count}&&document.querySelector('#risk-workspace-results [data-state=complete]')&&${idle}`,60000);
  const actual=await evaluate("calls.filter(c=>c.path==='/api/portfolio/risk').at(-1)");
  if(actual.status!==200||actual.body.bookIds.length!==1||actual.body.bookIds[0]!==c.bookId||actual.body.tradeIds!==null||
   JSON.stringify(actual.body.pricingPolicy)!==JSON.stringify(captured.result.pricingPolicy))throw Error('The normal Risk run lost its exact whole-book policy or scope.');
  const positions=actual.result.inputs.positions;
  if(positions.length!==(c.receiver?3:2)||positions.some(p=>p.owner.kind!==2||p.instrumentType==='physical_gas_park_loan')||
   new Set(positions.map(p=>p.owner.id+':'+p.bindingId)).size!==positions.length)throw Error('The normal Risk run did not hand off the service exactly once to independent native owners.');
  for(const [name,expected]of [['base',expectedBase],['up',expectedUp],['down',expectedDown]]){
   const total=actual.result.factors[0].totals.reduce((sum,row)=>sum+Number(row.values[name]),0);
   if(total!==expected)throw Error(`Normal Risk ${c.serviceKind} ${c.perspective} ${name}: expected ${expected}, received ${total}`);
  }
  await check(`${c.serviceKind} ${c.perspective} normal Risk controls retain native owners and exact scenario values`,"document.querySelector('#risk-workspace-pricing-policy').value==='native-gas-v1'&&document.querySelector('#risk-workspace-results .risk-result-heading').textContent.includes('Sensitivity report')");
  await evaluate("document.querySelector('#risk-workspace-results').scrollIntoView({block:'start'})");await shot('gas-park-loan-risk');
 };

 for(const input of f.cases){
  await at('before');await click('#nav-futures',60000);await startNewTrade(driver);await click('#new-trade-park-loan',60000);
  await wait(`!!document.querySelector(${q(control(null,'profile'))})&&${idle}`,60000);
  await check('fee entry waits for an explicit backend profile selection',`document.querySelector(${q(ticket+' [data-field=fixed_fee_amount]')})===null&&document.querySelector(${q(ticket+' [data-field=fee_rate_amount]')})===null`);
  await choose(null,'profile','physical_gas_park_loan_v1');
  await until(`!!document.querySelector(${q(control(null,'fixed_fee_amount'))})&&!document.querySelector(${q(control(null,'fixed_fee_amount'))}).disabled`);
  for(const[id,selected]of [['book',input.bookId],['legal_entity',input.legalEntityId+'@1'],['counterparty',input.counterpartyId+'@1'],
   ['service_kind',input.serviceKind],['perspective',input.perspective],['owned_account',input.ownedAccountId],
   ['claim_account',input.claimAccountId??'none'],['title_policy','fungible_gas_title_transfer_return_v1'],['return_policy','same_quantity_partial_returns_v1'],
   ['return_timing','accepted_advance_then_returns_v1'],['carrying','nominal_mark_v1'],['fee_direction',input.feeDirection]])await choose(null,id,selected);
  await set(control(null,'principal_quantity'),'720');await setInstant(null,'advance_at','2020-12-20T10:00:00Z');await setInstant(null,'return_at','2020-12-21T12:00:00Z');
  await set(control(null,'fixed_fee_amount'),'0');
  await evaluate(`(()=>{const n=document.querySelector(${q(ticket+' [data-field=fee_due_at] [data-role=not-applicable]')});n.checked=true;n.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  await until(current());
  await check('an explicit zero fee retains the independently selected direction and Not applicable due date',`(()=>{const p=calls.filter(c=>c.path==='/api/tickets/preview'&&c.status===200).at(-1);return p.body.draft.trade.fixed_fee_amount==='0'&&p.body.draft.trade.fee_due_at==='none'&&p.body.draft.trade.fee_direction===${q(input.feeDirection)};})()`);
  await set(control(null,'fixed_fee_amount'),'12.5');
  await evaluate(`(()=>{const n=document.querySelector(${q(ticket+' [data-field=fee_due_at] [data-role=not-applicable]')});n.checked=false;n.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  await setInstant(null,'fee_due_at','2020-12-22T12:00:00Z');await until(current());
  if(input.receiver)await evaluate('window.loseBooking=true');
  await click(ticket+' .ticket-book',60000);
  if(input.receiver){
   const recover=`[...document.querySelectorAll('#park-loan-ticket .ticket-booking button')].find(b=>b.textContent==='Check booking status'&&!b.disabled)`;
   await wait(`!!(${recover})`,60000);await check('a lost booking reply keeps the original reviewed request locked',"document.querySelector('#park-loan-ticket > div').inert");
   await evaluate(`(${recover}).click()`);await wait(idle,60000);
  }
  const bookedCall=`calls.findLast(c=>c.path==='/api/tickets/book'&&c.status===200&&c.body.draft.trade.owned_account===${q(input.ownedAccountId)})`;
  await wait(`!!(${bookedCall})`,60000);
  const receipt=await evaluate(`(${bookedCall}).result`);
  const c={...input,...receipt};await evaluate(`agreements.push(${q(c)})`);
  await wait(`document.querySelector('#position-details').dataset.mode==='position'&&${idle}`,60000);
  await wait("!!document.querySelector('#position-details .deal-ticket[data-mode=booked]')",60000);
  const reopened=await readTrade(c,'before');
  if(reopened.draft.trade.principal_quantity!=='720'||reopened.draft.trade.fixed_fee_amount!=='12.5'||reopened.draft.trade.perspective!==input.perspective)throw Error('Reopening changed saved commercial terms: '+JSON.stringify({expected:input.perspective,trade:c.tradeId,draft:reopened.draft}));
  await value(c,'before',...expected(c,c.feeAmount));
  await shot('gas-park-loan-booked');
 }

 const action=async(c,key,values,{lost=false}={})=>{
  await click('#nav-lifecycle',60000);await wait(idle,60000);
  const label={advance:'Accept principal advance',return:'Accept principal return',fee_payment:'Record service fee payment'}[key];
  await wait("!!document.querySelector('[data-lifecycle=rows] .table-search')&&"+idle,60000);
  const discovery=await evaluate("(()=>{const snapshot=document.querySelector('#snapshot').value;return calls.findLast(c=>c.path==='/api/lifecycle/view'&&c.status===200&&c.body.snapshotId===snapshot);})()");
  const offered=discovery?.result?.actions?.filter(offer=>offer.target.handler==='gas_park_loan'&&offer.target.scopeId===c.tradeId&&
   offer.target.bindingId===c.instrumentId&&offer.action==='gas.park_loan.'+key)??[];
  if(discovery?.result?.recordedCloseId!==discovery?.body?.snapshotId||offered.length!==1||!offered[0].subject?.reference)
   throw Error('The real Lifecycle view did not discover the exact service action: '+JSON.stringify({target:{tradeId:c.tradeId,instrumentId:c.instrumentId,key},
    snapshotId:discovery?.body?.snapshotId,recordedCloseId:discovery?.result?.recordedCloseId,count:discovery?.result?.actions?.length,
    serviceActions:discovery?.result?.actions?.filter(offer=>offer.target.handler==='gas_park_loan').map(offer=>({target:offer.target,action:offer.action,reference:offer.subject?.reference,eligible:offer.eligible,reason:offer.reason}))}));
  await set('[data-lifecycle=rows] .table-search',offered[0].subject.reference);
  const row=`[...document.querySelectorAll('[data-lifecycle=rows] .tabulator-row[data-row-key]')].find(r=>{const k=JSON.parse(r.dataset.rowKey);return k[0]==='gas_park_loan'&&k[1]===${q(c.tradeId)}&&k[2]===${q(c.instrumentId)}&&r.textContent.includes(${q(label)});})`;
  await wait(`!!(${row})&&${idle}`,60000);await evaluate(`(${row}).click()`);await wait(`!!document.querySelector('${panel} [name=serviceVersion]')&&${idle}`,60000);
  await check('the actual service form requires an explicit captured version and native policy',`document.querySelector('${panel} [name=serviceVersion]').value===''&&document.querySelector('${panel} [name=policyId]').value===''`);
  await firstChoice('policyId');await firstChoice('serviceVersion');
  for(const[name,v]of Object.entries(values))await set(panel+' [name="'+name+'"]',name==='effectiveAt'||name==='paidAt'?await local(v):v);
  if(key==='fee_payment')await firstChoice('feeId');
  await submit(panel+' form',60000);await wait(`!!document.querySelector('[data-lifecycle-review]')&&${idle}`,60000);
  const review=await evaluate("calls.filter(c=>c.path==='/api/lifecycle/preview').at(-1)");
  if(review.status!==200||review.body.target.scopeId!==c.tradeId||review.body.target.bindingId!==c.instrumentId)throw Error('The service review lost its exact native binding.');
  await evaluate("document.querySelector('[data-lifecycle-review]').scrollIntoView({block:'center'})");await shot('gas-park-loan-review');await check('the native service review fits the desktop viewport',fitsWidth);
  if(lost)await evaluate('window.loseAdvance=true');
  await wait("document.querySelector('[data-lifecycle-execute]')?.disabled===false&&"+idle,60000);
  await click('[data-lifecycle-execute]',60000);
  if(lost){
   await wait("!document.querySelector('[data-lifecycle=pending]').hidden&&"+idle,60000);
   await check('a lost principal acknowledgement retains the original operation',"!document.querySelector('[data-lifecycle=pending]').hidden");await click('[data-lifecycle=retry]',60000);
   await check('retry returns exactly the accepted native advance once',"(()=>{const c=calls.filter(c=>c.path==='/api/lifecycle/execute').slice(-2);return c.length===2&&JSON.stringify(c[0].body)===JSON.stringify(c[1].body)&&JSON.stringify(c[0].result)===JSON.stringify(c[1].result)&&document.querySelector('[data-lifecycle=pending]').hidden;})()");
  }
  const executed=await evaluate("calls.filter(c=>c.path==='/api/lifecycle/execute'&&c.status===200).at(-1).result");
  return executed;
 };

 const cases=await evaluate('agreements');
 await at('advance');
 for(const c of cases){
  const accepted=await action(c,'advance',{agreementEvidenceId:c.agreementEvidenceId,evidenceId:c.evidenceId,
   claimEvidenceId:c.claimEvidenceId??'@null',identifiedStock:c.receiver?'@null':c.fundingLotId+':none',effectiveAt:'2020-12-20T10:00:00Z'},{lost:c.receiver});
  c.stockId=accepted.stockId;
  await value(c,'advance',...expected(c,c.feeAmount));
  await workspaceValue(c,...expected(c,c.feeAmount));
  await action(c,'return',{businessKey:'synthetic_browser_partial_'+c.perspective,evidenceId:c.partialEvidenceId,
   identifiedStock:c.receiver?c.stockId+':none':'@null',quantity:'270',effectiveAt:'2020-12-20T12:00:00Z'});
  await action(c,'fee_payment',{businessKey:'synthetic_browser_payment_'+c.serviceKind+'_'+c.perspective,expectedRevision:'0',paidAt:'2020-12-20T14:00:00Z',amount:c.feeAmount<0?'-5':'5',evidence:'Synthetic independently observed fee payment'});
  const reopened=await readTrade(c,'advance');
  if(!JSON.stringify(reopened.overviewSections).includes('450'))throw Error('The partial return did not leave the explicit 450-unit principal.');
  await value(c,'advance',...expected(c,c.feeAmount<0?-7.5:7.5));
 }
 await at('after');
 for(const c of cases){
  const overdue=await readTrade(c,'after');if(!JSON.stringify(overdue.overviewSections).includes('450'))throw Error('A due date manufactured principal performance.');
  await action(c,'return',{businessKey:'synthetic_browser_final_'+c.perspective,evidenceId:c.finalEvidenceId,
   identifiedStock:c.receiver?c.stockId+':none':'@null',quantity:'450',effectiveAt:'2020-12-23T10:00:00Z'});
  await value(c,'after',...expected(c,c.feeAmount<0?-7.5:7.5));
  const reopened=await readTrade(c,'after');
  if(!JSON.stringify(reopened.overviewSections).includes(c.feeAmount<0?'-7.5':'7.5'))throw Error('Principal completion erased the independent unpaid fee.');
 }
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await check('the completed service workflow fits a narrow viewport',fitsWidth);await shot('gas-park-loan-mobile');
 await check('all real service requests use explicit native profiles and no browser pricing fallback',`calls.filter(c=>c.path==='/api/tickets/book').length===${f.cases.length}&&calls.filter(c=>c.path==='/api/lifecycle/execute'&&c.body.target.handler==='gas_park_loan').every(c=>c.status===200)`);
}

if(process.argv.length!==4)throw Error('Use the owned Park and Loan browser qualifier.');
console.log(`${await withOwnedBrowser(process.argv[2],process.argv[3],async driver=>{
 try{await run(driver);}catch(error){console.error('Owned Park and Loan state',JSON.stringify(await driver.evaluate("({error:document.querySelector('#error')?.textContent,ticket:document.querySelector('#gas-ticket .ticket-status')?.textContent,panel:document.querySelector('[data-lifecycle=panel]')?.textContent,invalid:[...document.querySelectorAll(':invalid')].filter(n=>n.getClientRects().length).map(n=>({name:n.name,value:n.value,message:n.validationMessage})),calls:window.calls?.slice(-15).map(c=>({path:c.path,status:c.status,code:c.result?.code,result:c.status>=400?c.result:undefined}))})")));throw error;}
})} full-app Park and Loan browser checks passed.`);
