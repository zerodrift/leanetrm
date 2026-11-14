// Listed futures options from New trade (D288): the series picked through lists that cascade from the backend catalog, one field per row;
// the coming-soon items drawn and never sent; the backend's refusal of margined premium shown; a booking whose response is lost retried
// under its own request identity into one trade; the trade in the Blotter with its settlement and PV; the fallback series labelled with its
// calibration mode; and one lot exercised from the trade's own window, whose instruction actions show the backend's reason they are off.
import {mkdtemp,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {newTrade} from './new-trade.mjs';
import {newTarget,press} from './blotter-window.mjs';
import {setupWorkspaceProbe} from './workspace-probe.mjs';

const q=value=>JSON.stringify(value);
// A term the Details or the trade's window list, by its label.
const term=label=>`[...document.querySelectorAll('.side-term')].find(t=>t.querySelector('dt').textContent===${JSON.stringify(label)})?.querySelector('dd').textContent`;
// The request members a futures option booking states; a coming-soon control adds none.
const bookingMembers=['operationId','snapshotId','convention','knowledgeMode','bookId','definitionRevisionId','series','side','lots','premium','premiumPaymentDate','premiumStyle','memo','legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision'];

export async function run({command,evaluate,wait,click,set,persona,check,idle,url,tab}){
 const artifacts=await mkdtemp(join(tmpdir(),'openetrm-futures-options-'));console.log('FUTURES_OPTION_VISUAL_ARTIFACTS='+artifacts);
 const picture=async(driver,name)=>{const shot=await driver.command('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});await writeFile(join(artifacts,name+'.png'),Buffer.from(shot.data,'base64'));};
 async function openBlotter(){
  await command('Page.navigate',{url:url+'/?example=exchange'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
  await setupWorkspaceProbe(evaluate);
  // Every option request and its answer, the workspace reads' rows, and a switch that loses the next booking's response after the
  // backend has answered it, as a dropped connection would.
  await evaluate(`(()=>{const send=window.fetch;window.optionCalls=[];window.surfaceReads=[];window.blotterRows=null;window.loseNextBooking=false;
   window.fetch=window.__workspaceProbe(send,async(input,init,send)=>{const path=String(input);const response=await send(input,init);
    if(path.endsWith('/trading/workspace')&&response.ok)window.blotterRows=(await response.clone().json()).blotter.holdings;
    if(/[/]vol-surfaces[/]read$/.test(path))window.surfaceReads.push({body:JSON.parse(init.body),status:response.status});
    const option=/[/]options[/]([a-z]+)$/.exec(path);
    if(option){const body=init?.body?JSON.parse(init.body):null,answer=await response.clone().json().catch(()=>null);window.optionCalls.push({path:option[1],body,status:response.status,answer});
     if(option[1]==='book'&&window.loseNextBooking){window.loseNextBooking=false;throw new TypeError('Failed to fetch');}}
    return response;});})()`);
  await persona('trader');await click('#example');await click('#nav-futures');
  await wait("!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&!!window.blotterRows&&"+idle,60000);
 }
 // The last booking answered with its trade, which the Blotter, read again once the trade is saved, holds.
 const booked=after=>`(()=>{const calls=window.optionCalls.filter(c=>c.path==='book'),last=calls.at(-1);return calls.length>${after}&&last.status===200&&!!window.blotterRows?.some(r=>r.tradeId===last.answer.tradeId);})()`;
 const bookings=()=>evaluate("window.optionCalls.filter(c=>c.path==='book').length");
 // A wait that, should it time out, says what the booking said and what the option operations answered.
 const settled=(expression,ms)=>wait(expression,ms).catch(async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate(
  "({status:document.querySelector('#option-booking-status')?.textContent,calls:window.optionCalls.slice(-3).map(c=>[c.path,c.status,c.answer]),notice:document.querySelector('#notice')?.textContent,error:document.querySelector('#error')?.textContent})")));});
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await openBlotter();
 await newTrade({click,wait},'options');
 await wait("window.optionCalls.some(c=>c.path==='catalog'&&c.status===200)&&document.querySelector('#option-exchange').options.length>1&&"+idle);
 const catalog=await evaluate("window.optionCalls.find(c=>c.path==='catalog').answer");
 const fallback=catalog.options.find(o=>o.series==='nymex_on_20261201_c2.00');
 // A call on the first gas underlying that no other check books, so the Blotter holds only this check's trade of it.
 const chosen=catalog.options.find(o=>o.product==='nymex_on'&&o.right==='call'&&o.underlyingSeries===fallback.underlyingSeries&&o.series!==fallback.series&&!/c3[.]05$/.test(o.series));
 if(!fallback||!chosen)throw Error('The exchange example lists no gas call series to book.');
 const values=id=>`[...document.querySelector(${q('#option-'+id)}).options].map(o=>o.value).filter(Boolean)`;
 const empty=id=>`(document.querySelector(${q('#option-'+id)}).disabled&&${values(id)}.length===0)`;

 await check('the option ticket stacks its sections, Trade to More, with one field per row',
  `(()=>{const titles=[...document.querySelectorAll('#option-page .option-section-title')].map(t=>t.textContent);
   const rows=[...document.querySelectorAll('#option-page .option-fields label, #option-page .option-fields .option-fact-label')].filter(n=>n.getClientRects().length).map(n=>Math.round(n.getBoundingClientRect().top));
   return titles.join()==='Trade,Contract,Execution,From the backend,More'&&rows.length>15&&new Set(rows).size===rows.length;})()`,
  "[...document.querySelectorAll('#option-page .option-fields label, #option-page .option-fields .option-fact-label')].filter(n=>n.getClientRects().length).map(n=>n.textContent+'@'+Math.round(n.getBoundingClientRect().top)+'/'+Math.round(n.getBoundingClientRect().left))");
 const labelsIn=title=>`[...[...document.querySelectorAll('#option-page .option-section')].find(s=>s.querySelector('.option-section-title').textContent===${q(title)}).querySelectorAll('label,.option-fact-label')].map(l=>l.textContent)`;
 await check('Trade holds the book, entity and trader; Execution holds the counterparty, trade date, execution time and memo',
  `${labelsIn('Trade')}.slice(0,3).join()==='Book,Legal entity,Trader'&&['Counterparty','Trade date','Memo'].every(l=>${labelsIn('Execution')}.includes(l))&&${labelsIn('Execution')}.some(l=>l.startsWith('Execution time'))&&!${labelsIn('Trade')}.includes('Counterparty')`,
  `[${labelsIn('Trade')},${labelsIn('Execution')}]`);
 await check('Exchange and Commodity come above the underlying future, and nothing is chosen for the person',
  `(()=>{const order=['exchange','commodity','underlying','right','strike'].map(id=>document.querySelector('#option-'+id).getBoundingClientRect().top);
   return order.every((top,i)=>i===0||top>order[i-1])&&${values('exchange')}.join()==='nymex'&&document.querySelector('#option-exchange').value===''&&${empty('commodity')}&&${empty('underlying')}&&${empty('right')}&&${empty('strike')};})()`,
  "['exchange','commodity','underlying','right','strike'].map(id=>{const s=document.querySelector('#option-'+id);return [id,s.value,s.disabled,[...s.options].map(o=>o.value).join('|'),Math.round(s.getBoundingClientRect().top)];})");
 await set('#option-exchange','nymex');
 await check('the commodities are those the catalog lists on the exchange, and the underlying waits for one',
  `${values('commodity')}.join()===${q([...new Set(catalog.options.map(o=>o.commodity.code))].join())}&&${empty('underlying')}`);
 await set('#option-commodity',chosen.commodity.code);
 await check('the underlying futures are exactly those of the commodity\'s listed series, labelled with their delivery and product name from the backend',
  `${values('underlying')}.join()===${q([...new Set(catalog.options.filter(o=>o.commodity.code===chosen.commodity.code).map(o=>o.underlyingSeries))].join())}&&
   [...document.querySelector('#option-underlying').options].some(o=>o.value===${q(chosen.underlyingSeries)}&&o.textContent==='Dec 2026 · '+${q(catalog.underlyings.find(u=>u.series===chosen.underlyingSeries).name)})&&${empty('right')}`);
 await set('#option-underlying',chosen.underlyingSeries);await set('#option-right','call');
 const strikes=[...new Set(catalog.options.filter(o=>o.underlyingSeries===chosen.underlyingSeries&&o.right==='call').map(o=>o.strike))];
 await check('call and put and then the strikes list only the chosen contract\'s series',
  `${values('right')}.join()==='call,put'&&[...${values('strike')}].sort().join()===${q(strikes.sort().join())}`);
 await check('the ticket offers no volatility surface until a series is chosen',"document.querySelector('#option-ticket-vol-surface').hidden");
 await set('#option-strike',chosen.strike);
 await check('the chosen series offers its product\'s volatility surface',"!document.querySelector('#option-ticket-vol-surface').hidden&&document.querySelector('#option-ticket-vol-surface').textContent==='Volatility surface'");
 await check('expiry, contract and the backend\'s settlement are read-only values of the picked series',
  `(()=>{const facts=Object.fromEntries([...document.querySelectorAll('#option-page .option-fact')].map(f=>[f.querySelector('.option-fact-label').textContent,f.querySelector('.option-fact-value').textContent]));
   return facts.Series===${q(chosen.series)}&&facts['Exchange contract'].endsWith(${q(chosen.product)})&&facts['Settlement at this close']===${q(chosen.settlementPrice+' '+chosen.currency)}&&
    facts.Expiry!=='—'&&facts['Contrary instructions']==='Not accepted'&&!document.querySelector('#option-page .option-fact input');})()`);
 await set('#option-commodity',[...new Set(catalog.options.map(o=>o.commodity.code))].find(c=>c!==chosen.commodity.code));
 await check('changing the commodity clears the underlying future, call or put and strike below it',
  `document.querySelector('#option-underlying').value===''&&${empty('right')}&&${empty('strike')}&&document.querySelector('#option-page .option-fact-value').textContent==='—'`);
 async function pick(series){
  for(const [id,value] of [['commodity',series.commodity.code],['underlying',series.underlyingSeries],['right',series.right],['strike',series.strike]])await set('#option-'+id,value);
 }
 async function execution(series,lots){
  await pick(series);
  for(const id of ['book','legal-entity','counterparty'])await evaluate(`(()=>{const s=document.querySelector(${q('#option-'+id)});s.value=s.options[1].value;s.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  await set('#option-book',catalog.books.at(-1).id);
  await set('#option-side','buy');await set('#option-lots',lots);await set('#option-premium',series.settlementPrice);await set('#option-premium-date','2026-10-19');await set('#option-premium-style','premium_paid_up_front');
 }
 await execution(chosen,'2');
 await check('the coming-soon items are drawn disabled and tagged, and margined premium is offered only as coming soon',
  `(()=>{const soon=[...document.querySelectorAll('#option-page .ticket-soon')],labels=soon.map(s=>s.textContent);
   const margined=[...document.querySelector('#option-premium-style').options].find(o=>o.value==='futures_style_margin');
   return soon.length===10&&soon.every(s=>(s.matches('button')?s:s.querySelector('input')).disabled&&s.querySelector('.ticket-soon-tag')?.textContent==='Coming soon')&&
    ['Custom trade fields','Executing broker','Comments','Broker fees','Settlement instructions','Credit check','Risk-limit check','Compare versions','Import / export','Print'].every(l=>labels.some(t=>t.startsWith(l)))&&
    margined.disabled&&margined.textContent==='Margined daily — Coming soon'&&[...document.querySelector('#option-premium-style').options].find(o=>o.value==='premium_paid_up_front').textContent==='Paid up front';})()`);
 await picture({command},'option-ticket');

 // A margined premium sent anyway is the backend's to refuse, and its words are shown; nothing waits to be retried.
 await evaluate("(()=>{const s=document.querySelector('#option-premium-style');s.querySelector('[value=futures_style_margin]').disabled=false;s.value='futures_style_margin';})()");
 await evaluate("document.querySelector('#option-book-submit').click()");
 await wait("document.querySelector('#option-booking-status').textContent.startsWith('Booking refused')&&"+idle);
 await check('submitting margined premium shows the backend\'s unsupported refusal and retains no request',
  "document.querySelector('#option-booking-status').textContent==='Booking refused: Margined premium is not supported; book the option with premium paid up front.'&&document.querySelector('#option-retry').hidden&&window.optionCalls.filter(c=>c.path==='book').at(-1).answer.code==='option_unsupported'");
 await evaluate("(()=>{const s=document.querySelector('#option-premium-style');s.value='premium_paid_up_front';s.querySelector('[value=futures_style_margin]').disabled=true;})()");

 // The booking's response is lost after the backend wrote it: the request is retained, and retried under the same identity.
 await evaluate("window.loseNextBooking=true;document.querySelector('#option-book-submit').click()");
 await wait("document.querySelector('#option-booking-status').textContent.startsWith('The booking is not confirmed')&&!document.querySelector('#option-retry').hidden&&"+idle);
 await check('a lost booking response keeps the request to check or retry, with the form held',"document.querySelector('#option-page .option-fields').disabled&&document.querySelector('#option-book-submit').disabled");
 const before=await bookings();await click('#option-retry');
 await settled(booked(before)+'&&'+idle,60000);
 const sent=await evaluate("window.optionCalls.filter(c=>c.path==='book').slice(-2)");
 const receipt=sent[1].answer;
 await check('the retry sends the original request under its own identity and the backend answers its first outcome',
  q(sent[0].body.operationId===sent[1].body.operationId&&JSON.stringify(sent[0].body)===JSON.stringify(sent[1].body)&&sent[0].status===200&&sent[0].answer.tradeId===receipt.tradeId&&
   Object.keys(sent[1].body).every(k=>bookingMembers.includes(k))&&sent[1].body.side==='buy'&&sent[1].body.lots==='2'&&sent[1].body.definitionRevisionId===chosen.definitionRevisionId&&sent[1].body.series===chosen.series&&sent[1].body.premiumStyle==='premium_paid_up_front'));

 await wait(`window.blotterRows.some(r=>r.tradeId===${q(receipt.tradeId)})&&`+idle,60000);
 const rows=await evaluate(`window.blotterRows.filter(r=>r.series===${q(chosen.series)})`);
 const option=rows.find(r=>r.component==='option');
 await check('the retried booking is one trade: the Blotter holds its option and premium rows once',
  q(rows.length===2&&rows.every(r=>r.tradeId===receipt.tradeId)&&option.bindingId===receipt.optionInstrumentId));
 const row=`#desk-positions .tabulator-row[data-row-key=${q(`${receipt.tradeId}:${receipt.optionInstrumentId}`)}]`;
 // The grid draws the rows in view: the trade is searched for, as a person finds it.
 const find=async trade=>{
  const reference=await evaluate(`window.blotterRows.find(r=>r.tradeId===${q(trade)}).tradeReference`);
  await set('#blotter-filters .table-search',reference);
  await wait(`(()=>{const shown=[...document.querySelectorAll('#desk-positions .tabulator-row[data-row-key]')].filter(r=>r.getClientRects().length);return shown.length===2&&shown.every(r=>r.dataset.rowKey.startsWith(${q(trade+':')}));})()&&`+idle,30000);
 };
 const details=async selector=>{await press({command,evaluate},selector+' .desk-reference',1);await wait("!!document.querySelector('.side-panel-figure .kpi-value')&&"+idle,30000).catch(async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate(
  "({mode:document.querySelector('#position-details')?.dataset.mode,prompt:document.querySelector('#position-details-prompt')?.hidden===false,title:document.querySelector('#details-title')?.textContent,rows:[...document.querySelectorAll('#desk-positions .tabulator-row[data-row-key]')].filter(r=>r.getClientRects().length).map(r=>r.dataset.rowKey),view:document.querySelector('#position-details')?.innerText.slice(0,600),notice:document.querySelector('#notice')?.textContent,error:document.querySelector('#error')?.textContent})")));});};
 await find(receipt.tradeId);
 await check('the Blotter shows the option with its pill, right, strike and lots, and its settlement price from the backend',
  `(()=>{const text=document.querySelector(${q(row)}).textContent;return text.includes('OPT')&&text.includes('Call '+${q(chosen.strike)}+' · +2 lots')&&text.includes(${q(option.settlement)});})()`,
  `document.querySelector(${q(row)}).textContent`);
 const terms=async()=>{await evaluate("[...document.querySelectorAll('#position-details [role=tab]')].find(t=>t.textContent==='Terms')?.click()");await wait(`${term('Calibration mode')}!==undefined&&!!document.querySelector('.option-lifecycle')&&`+idle,30000);};
 await details(row);await terms();
 await check('its Details state the backend\'s PV, settlement price and value per contract, and that the series is solved',
  `document.querySelector('.side-panel-figure .kpi-value').title===${q(option.presentValue+' '+option.currency)}&&${q(option.calibrationMode)}==='solved'&&
   ${term('Settlement price')}===${q(chosen.settlementPrice)}&&${term('Value per contract')}===${q(option.unitPresentValue+' '+option.currency)}`,
  `[document.querySelector('.side-panel-figure .kpi-value')?.title,${term('Settlement price')},${term('Value per contract')}]`);
 await check('working Details sends new exercise, assignment and expiry actions to the saved-policy workspace',
  "!!document.querySelector('#option-open-lifecycle')&&!document.querySelector('#option-exercise')&&!document.querySelector('#option-exercise-lots')&&document.querySelector('.option-lifecycle').textContent.includes('Scheduling → Lifecycle')");
 await picture({command},'option-blotter');
 // A switched account selects no market date by default (D334), so it opens the same example inputs.
 // A role without booking authority reads the same holding with every lifecycle action off, and the backend's reason why.
 await evaluate('window.blotterRows=null');await persona('risk');await click('#example');await click('#nav-futures');await wait("!!window.blotterRows&&"+idle,60000);await find(receipt.tradeId);await details(row);await terms();
 await check('a reader keeps the option facts and shared-workspace navigation without a retired financial form',
  "!!document.querySelector('#option-open-lifecycle')&&!document.querySelector('#option-exercise')&&!document.querySelector('#option-abandon')&&document.querySelector('.option-lifecycle').textContent.includes('saved action policies')");
 await evaluate('window.blotterRows=null');await persona('trader');await click('#example');await click('#nav-futures');await wait("!!window.blotterRows&&"+idle,60000);

 // The series that settles at intrinsic is booked as a person books it, and its position says which mode values it.
 await newTrade({click,wait},'options');
 await wait("document.querySelector('#option-exchange').options.length>1&&"+idle);
 await set('#option-exchange','nymex');await execution(fallback,'1');
 const earlier=await bookings();await evaluate("document.querySelector('#option-book-submit').click()");
 await settled(booked(earlier)+'&&'+idle,60000);
 const deep=(await evaluate("window.optionCalls.filter(c=>c.path==='book').at(-1)")).answer;
 await wait(`window.blotterRows.some(r=>r.tradeId===${q(deep.tradeId)})&&`+idle,60000);
 const deepRow=`#desk-positions .tabulator-row[data-row-key=${q(deep.tradeId+':'+deep.optionInstrumentId)}]`;
 await find(deep.tradeId);
 await details(deepRow);await terms();
 await check('the fallback position is labelled with its calibration mode and states no implied volatility',
  `${term('Calibration mode')}==='option_settlement_at_intrinsic_v1'&&${term('Implied volatility')}==='None'`,`[${term('Calibration mode')},${term('Implied volatility')}]`);

 // A separate position window keeps its retained option facts and directs
 // new financial actions back to the shared policy workspace.
 await openBlotter();await find(receipt.tradeId);
 const driver={command,evaluate};
 const target=await newTarget(command,async()=>{await press(driver,row+' .desk-reference',1);await press(driver,row+' .desk-reference',2);},url+'/position.html#');
 if(!target)throw Error('A double click on the option opened no trade window.');
 await check('the window opened from working data says so in its selection',q(new URLSearchParams(new URL(target.url).hash.slice(1)).get('lifecycle')==='working'));
 const popup=await tab(target.targetId);await popup.command('Emulation.setDeviceMetricsOverride',{width:1180,height:1400,deviceScaleFactor:1,mobile:false});
 await popup.wait("document.body.getAttribute('aria-busy')==='false'&&!!document.querySelector('.option-lifecycle')",30000);
 await check('the position window retains open lots while new actions use the shared workspace',q(await popup.evaluate(
  `${term('Open lots')}==='2'&&!document.querySelector('#option-exercise')&&!document.querySelector('#option-exercise-lots')&&document.querySelector('.option-lifecycle').textContent.includes('Scheduling')`)));
 await picture(popup,'option-window');await command('Target.closeTarget',{targetId:target.targetId});await popup.close();
 // Reuse one explicitly saved native v2 policy for this exact future, book and
 // legal owner. Configuration is separate from the observed exercise below.
 await evaluate(`(async()=>{const token=(await(await fetch('/examples/exchange/api/bootstrap')).json()).csrfToken;
  const post=async(path,body)=>{const response=await fetch('/examples/exchange/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)});if(!response.ok)throw Error(path+': '+await response.text());return response.json();};
  const selected={snapshotId:document.querySelector('#snapshot').value,knowledgeMode:'current',convention:'trading_workspace_v1'},d=await post('lifecycle/view',selected);
  const source=d.optionSources.find(s=>s.trade_id===${q(receipt.tradeId)}&&s.curve_code==='nymex_ng'&&s.future_mark_eligible);if(!source)throw Error('Missing exact option-generated future mark.');
  const compatible=d.marginPolicies.filter(m=>m.implementation==='dated_futures_margin_policy_v2'&&m.definition_revision_id===source.definition_revision_id&&m.contract_id===source.underlying_contract_id&&
   d.deliveryProfiles.some(p=>p.profile.id===m.delivery_profile_id&&p.profile.revision===m.delivery_profile_revision&&d.deliveryAccounts.some(a=>a.id===p.gas.account_id&&a.book_id===${q(sent[1].body.bookId)}&&a.legal_entity_id===${q(sent[1].body.legalEntityId)})));
  if(compatible.length!==1)throw Error('Select one exact configured synthetic physical policy; found '+compatible.length);const margin=compatible[0];
  const prior=d.optionProfiles.filter(p=>p.action==='option.exercise'&&p.margin_policy_id===margin.id),policies=[...new Set(prior.map(p=>p.policy_id))];if(policies.length!==1)throw Error('Select one explicit synthetic review policy.');
  await post('lifecycle/options/configure',{operationId:crypto.randomUUID(),tradeId:${q(receipt.tradeId)},action:'option.exercise',expectedRevision:0,policyId:policies[0],policyRevision:1,marginPolicyId:margin.id,marginPolicyRevision:margin.revision,futureMarkCurve:source.curve_code,expiryCurve:null});
  window.lifecycleOptionWrites=[];window.lifecycleOptionReads=[];const send=window.fetch;window.loseLifecycleOptionReply=true;window.fetch=async(...args)=>{const response=await send(...args);if(String(args[0]).endsWith('/lifecycle/view'))window.lifecycleOptionReads.push({status:response.status,value:await response.clone().json()});if(String(args[0]).endsWith('/lifecycle/execute')){window.lifecycleOptionWrites.push({body:JSON.parse(args[1].body),status:response.status,answer:await response.clone().json()});if(response.ok&&window.loseLifecycleOptionReply){window.loseLifecycleOptionReply=false;throw new TypeError('Synthetic lifecycle reply lost after commit');}}return response;};
 })()`);
 await find(receipt.tradeId);await details(row);await terms();await click('#option-open-lifecycle');await wait("!document.querySelector('#lifecycle-page').hidden&&"+idle,60000);
 const exerciseRow=`[...document.querySelectorAll('#lifecycle-page .tabulator-row[data-row-key]')].find(r=>{const k=JSON.parse(r.dataset.rowKey);return k[1]===${q(receipt.tradeId)}&&k[3]==='option.exercise';})`;
 await wait("!!document.querySelector('#lifecycle-page [data-lifecycle=rows] .table-search')&&"+idle,60000);
 await set('#lifecycle-page [data-lifecycle=rows] .table-search',option.tradeReference);
 await wait(`!!(${exerciseRow})&&${idle}`,60000).catch(async error=>{throw Error(error.message+' · lifecycle '+JSON.stringify(await evaluate(`({notice:document.querySelector('#lifecycle-page [data-lifecycle=message]')?.textContent,error:document.querySelector('#error')?.textContent,read:window.lifecycleOptionReads?.map(r=>({status:r.status,context:r.value.context,actions:r.value.actions?.filter(a=>a.target.scopeId===${q(receipt.tradeId)}),error:r.value.code})),rows:[...document.querySelectorAll('#lifecycle-page .tabulator-row[data-row-key]')].map(r=>r.dataset.rowKey)})`)));});await evaluate(`(${exerciseRow}).click()`);await wait("!!document.querySelector('#lifecycle-page [data-lifecycle=panel] form.lifecycle-form')&&"+idle);
 await evaluate(`(()=>{const labels=[...document.querySelectorAll('#lifecycle-page [data-lifecycle=panel] label')];for(const [label,value]of [['Observed evidence ID',crypto.randomUUID()],['Lots','1']]){const field=labels.find(l=>l.firstChild.textContent.startsWith(label))?.querySelector('input');if(!field)throw Error('Missing observed option field '+label);field.value=value;field.dispatchEvent(new Event('input',{bubbles:true}));}document.querySelector('#lifecycle-page [data-lifecycle=panel] form').requestSubmit();})()`);
 await wait("!!document.querySelector('#lifecycle-page [data-lifecycle-review]')&&"+idle,60000);
 await check('shared exercise review displays the native strike and linked-future quantity before recording',`(()=>{const text=document.querySelector('#lifecycle-page [data-lifecycle-review]').textContent;return text.includes('Lots affected1')&&text.includes('Linked futures lots1')&&text.includes(${q(chosen.strike)});})()`);
 await click('#lifecycle-page [data-lifecycle-execute]',60000);await wait("!document.querySelector('#lifecycle-page [data-lifecycle=pending]').hidden&&"+idle,60000);
 await check('a lost shared option reply retains its exact reviewed financial request',"window.lifecycleOptionWrites.length===1&&window.lifecycleOptionWrites[0].status===200&&!document.querySelector('#lifecycle-page [data-lifecycle=retry]').hidden");
 await click('#lifecycle-page [data-lifecycle=retry]',60000);await wait("document.querySelector('#lifecycle-page [data-lifecycle=pending]').hidden&&"+idle,60000);
 const exercises=await evaluate('window.lifecycleOptionWrites'),exercised=exercises.at(-1);
 await check('shared option retry returns one native event and linked future under the original request',q(exercises.length===2&&exercises.every(c=>c.status===200&&c.answer.eventId===exercises[0].answer.eventId)&&JSON.stringify(exercises[0].body)===JSON.stringify(exercises[1].body)&&exercised.body.selection.lots==='1'&&typeof exercised.answer.futuresTradeId==='string'));
 await openBlotter();
 const after=await evaluate(`window.blotterRows.filter(r=>r.tradeId===${q(receipt.tradeId)}||r.tradeId===${q(exercised.answer.futuresTradeId)})`);
 await check('the Blotter holds one linked future and the option remaining lot after the shared event',q(after.filter(r=>r.instrumentType==='future').length===1&&after.some(r=>r.tradeId===exercised.answer.futuresTradeId&&r.instrumentType==='future'&&r.lots==='1')&&after.some(r=>r.component==='option'&&r.openLots==='1'&&r.lifecycleState==='partially_closed'&&r.linkedFuturesTradeIds.join()===exercised.answer.futuresTradeId)));

 // An option, booked or being entered, links to its product's volatility surface: Vol surfaces opens on that product, read at the
 // context the option was read at.
 const surfaceOpened=async(name,product,context,definition)=>{
  await settled(`document.querySelector('#page-title').textContent==='Vol surfaces'&&window.surfaceReads.length>0&&!['idle','loading'].includes(document.querySelector('#vol-surfaces-page').dataset.state)&&${idle}`,30000);
  const read=await evaluate('window.surfaceReads.at(-1)');
  await check(name,q(read.status===200&&read.body.product===product&&read.body.definitionRevisionId===definition&&read.body.context.snapshotId===context.snapshotId&&read.body.context.knowledgeMode===context.knowledgeMode&&
   await evaluate(`document.querySelector('.vol-product-card[aria-pressed=true]')?.dataset.product===${q(product)}&&['notFitted','fitted'].includes(document.querySelector('#vol-surfaces-page').dataset.state)`)),
   `({read:window.surfaceReads.at(-1),state:document.querySelector('#vol-surfaces-page').dataset.state,error:document.querySelector('#vol-surface-error').textContent,product:document.querySelector('.vol-product-card[aria-pressed=true]')?.dataset.product})`);
 };
 await newTrade({click,wait},'options');
 await wait("document.querySelector('#option-exchange').options.length>1&&"+idle);
 await set('#option-exchange','nymex');await pick(chosen);
 const ticketRead=(await evaluate("window.optionCalls.filter(c=>c.path==='catalog').at(-1)")).body;
 // The person types the lots, which is an edit of the ticket that leaving would discard.
 await evaluate("document.querySelector('#option-lots').focus()");await command('Input.insertText',{text:'1'});
 await evaluate("window.surfaceReads=[]");await click('#option-ticket-vol-surface');
 // Leaving the ticket's entries is the person's word, asked as any page change asks it.
 const discard="[...document.querySelectorAll('button')].find(b=>b.textContent==='Discard changes'&&b.getClientRects().length)";
 await wait(`!!${discard}||document.querySelector('#page-title').textContent==='Vol surfaces'`,30000);
 await check('leaving the ticket with typed entries asks first whether to discard them',`!!${discard}&&document.querySelector('#page-title').textContent!=='Vol surfaces'`);
 await evaluate(`${discard}.click()`);
 await surfaceOpened('the ticket\'s Volatility surface opens Vol surfaces on the chosen series\' product at the ticket\'s context',chosen.product,ticketRead,chosen.definitionRevisionId);
 await click('#nav-futures');await wait("document.querySelector('#page-title').textContent==='Blotter'&&!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&"+idle,60000);
 await find(receipt.tradeId);await details(row);
 await evaluate("[...document.querySelectorAll('#position-details [role=tab]')].find(t=>t.textContent==='Terms')?.click()");
 await wait("!!document.querySelector('#option-trade-vol-surface')&&"+idle,30000);
 const holdingRead=(await evaluate(`window.optionCalls.filter(c=>c.path==='holding'&&c.body.tradeId===${q(receipt.tradeId)}).at(-1)`)).body.context;
 await check('the option trade offers its product\'s volatility surface',`document.querySelector('#option-trade-vol-surface').textContent==='Volatility surface'&&${term('Product')}===${q(chosen.name)}`);
 await evaluate("window.surfaceReads=[]");await click('#option-trade-vol-surface');
 await surfaceOpened('the trade\'s Volatility surface opens Vol surfaces on its product at the context the trade was read at',chosen.product,holdingRead,chosen.definitionRevisionId);
}
