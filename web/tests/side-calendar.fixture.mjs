// Native entry controls and request payloads only; synthetic catalogs, no writes.
export async function sideCalendarChecks(){
 const results=[],check=(name,passed)=>results.push({name,passed:!!passed});
 const change=(field,value)=>{field.value=value;field.dispatchEvent(new Event('input',{bubbles:true}));field.dispatchEvent(new Event('change',{bubbles:true}));};
 const refused=read=>{try{read();return false;}catch{return true;}};
 const host=document.createElement('main');document.body.append(host);
 const {componentEditor}=await import('/ppa-components.js');
 const {calendarKey}=await import('/side-calendar.js');
 const ref=code=>({revision:{catalogId:'synthetic',revisionId:code==='beta'?'independent-calendar-version':'synthetic'},code});
 const policy=(delivery,observation,payment)=>({implementation:'side_calendar_policy_v1',delivery:{applicable:!!delivery,calendar:delivery?ref(delivery):null},observation:{applicable:!!observation,calendar:observation?ref(observation):null},payment:{applicable:!!payment,calendar:payment?ref(payment):null}});
 const row=code=>({code,name:code});
 const catalog={reference:ref('alpha').revision,references:{locations:[row('synthetic_hub')],quantityGroups:{contract:[{code:'capacity',unit_code:'mw'}],energy:[{code:'energy',unit_code:'mwh'}],capacity:[],certificate:[]},units:[{code:'mw',symbol:'MW'},{code:'mwh',symbol:'MWh'}],calendars:['alpha','beta'].map(row),priceIndices:[]},
  priceKinds:['flat'],definitionsMetadata:{priceCurves:[],volumeSources:[],generationAssets:[]},authoringPolicies:{escalationBases:[],certificateEligibility:[],certificateHolding:[],certificateDisposition:[],capacityRules:[],quantityStages:[],hourMemberships:['all']},contextTouVersions:[],touVersions:[],
  paymentOptions:{implementation:'payment_rule_v1',frequencies:[{value:'monthly',label:'Monthly'}],rules:[{value:'day_of_following_month',label:'Following month'}],dayBases:[],shortMonths:[{value:'last_day',label:'Last day'}],adjustments:['following','unadjusted'].map(value=>({value,label:value}))}};
 catalog.sideCalendars=['alpha','beta'].map(code=>({code,reference:ref(code),coverageFrom:'2020-01-01',coverageTo:'2040-01-01',weekendMask:48,exceptions:[{date:'2031-01-02',open:true}]}));
 const quantity={kind:'profile',sources:[],assets:[],slots:[],stage:null,capacity:'capacity',energy:'energy',hours:{membership:'all',calendar:null,touProduct:null,touVersion:null}};
 const payment={frequency:'monthly',rule:'day_of_following_month',days:null,dayOfMonth:5,dayBasis:null,shortMonth:'last_day',adjustment:'following',calendar:null,implementation:'payment_rule_v1'};
 const template={role:'energy',ordinal:1,sideKind:'energy',sideKinds:['energy'],linkedSlots:[],quantityKinds:['profile','slots'],linkRoles:['energy']};
 const member={role:'energy',ordinal:1,coefficient:'1',side:{calendarPolicy:policy('alpha',null,'alpha'),kind:'energy',location:'synthetic_hub',quantity,paymentRule:payment,fixedPrice:null,indexPrice:null,attribute:null}};
 let ordinal=20;
 const mount=(initial,composition)=>{const card=document.createElement('section');host.append(card);const editor=componentEditor(card,catalog,template,ordinal++,()=>{},initial,composition);return{card,editor,calendar:card.querySelector('[data-side-calendar]')};};
 const preset=mount(structuredClone(member));change(preset.calendar,calendarKey(ref('beta')));
 let submitted=preset.editor.read(quantity);
 check('PPA delivery changes leave payment binding independent',submitted.side.calendarPolicy.delivery.calendar.code==='beta'&&submitted.side.calendarPolicy.payment.calendar.code==='alpha');
 check('Calendar inspection exposes exact independent version, weekend and exception terms',preset.card.textContent.includes('independent-calendar-version')&&preset.card.textContent.includes('2020-01-01 to 2040-01-01')&&preset.card.textContent.includes('Friday, Saturday')&&preset.card.textContent.includes('2031-01-02: open'));
 check('PPA nested rules carry no independent calendars',submitted.side.quantity.hours.calendar===null&&submitted.side.paymentRule.calendar===null);
 check('Editing leaves retained inputs unchanged',member.side.calendarPolicy.delivery.calendar.code==='alpha');
 const composed=mount(structuredClone(member),{delivery:()=>({clock:'synthetic',cadence:'60'}),links:()=>[]});
 const shared=composed.card.querySelector('select[id$="-shared"]');change(shared,calendarKey(ref('beta')));
 [...composed.card.querySelectorAll('button')].find(b=>b.textContent==='Use one calendar for all applicable schedules').click();submitted=composed.editor.read(quantity);
 check('Explicit shared action expands independent concrete bindings',submitted.side.calendarPolicy.delivery.calendar.code==='beta'&&submitted.side.calendarPolicy.payment.calendar.code==='beta');
 const mixed=structuredClone(member);mixed.side.calendarPolicy.payment.calendar=ref('beta');const restored=mount(mixed);submitted=restored.editor.read(quantity);
 check('Different saved purpose bindings remain valid and visible',submitted.side.calendarPolicy.delivery.calendar.code==='alpha'&&submitted.side.calendarPolicy.payment.calendar.code==='beta'&&restored.card.textContent.includes('different calendar versions'));
 const linked=structuredClone(member);linked.side.quantity={...quantity,kind:'slots',hours:null,slots:[{role:'energy',ordinal:1}]};linked.side.calendarPolicy=policy(null,null,'alpha');const cash=mount(linked);submitted=cash.editor.read(quantity);
 check('Linked quantities retain source ownership',submitted.side.quantity.hours===null&&submitted.side.quantity.slots[0].role==='energy'&&!submitted.side.calendarPolicy.delivery.applicable);
 change(cash.card.querySelector('[data-side-calendar=payment]'),calendarKey(ref('beta')));
 check('Changing one Side leaves another unchanged',preset.editor.read(quantity).side.calendarPolicy.payment.calendar.code==='alpha');
 const {setupScheduledSwapEditor}=await import('/scheduled-swap-editor.js');
 const {setCurveNames}=await import('/market-view.js');setCurveNames([{code:'curve',name:'Synthetic power'}]);
 const shelf=document.createElement('div');shelf.id='editor-shelf';document.body.append(shelf);
 const swapHost=document.createElement('section');host.append(swapHost);
 const mode={resetFrequency:'daily',averagingMethod:'observation_arithmetic',label:'Business-day observations',requiresObservationCalendar:true,requiresQuantityWeights:false};
 const index={definitionRevisionId:'definition',curve:'curve',index:'index',commodity:'power',currency:'USD',currencyCode:'usd',quantityCode:'energy',unit:'MWh',quoteMultiplier:'1',quantityChoices:[{specification:ref('energy'),unit:'MWh'}],samplings:[mode],customProjections:[],settlementIndices:[],contracts:[],powerQuantitySupported:true};
 const calendarRows=['alpha','beta'].map(code=>({definitionRevisionId:'definition',code,reference:ref(code)}));
 const swapCatalog={bookingActor:{name:'Synthetic trader'},books:[],legalEntities:[],counterparties:[],commodities:[{code:'power',name:'Power'}],context:{contextId:'00000000-0000-0000-0000-000000000001',effectiveOn:'2020-12-18',knownAt:'2026-01-01T00:00:00Z'},canBook:true,indices:[index],sideCalendars:catalog.sideCalendars,paymentCalendars:calendarRows,paymentOptions:catalog.paymentOptions,
  scheduledOptions:{implementation:'scheduled_financial_swap_v1',priceApplication:'period_average_times_period_quantity_v1',powerQuantities:[{definitionRevisionId:'definition',specification:ref('power'),unit:'MW',commodity:'power'}],clocks:[{definitionRevisionId:'definition',reference:ref('clock'),label:'Synthetic clock'}]},entryTemplate:{payment},factorOptions:{}};
 let jobs=[],captured;
 const flush=async()=>{for(let n=0;n<16;n++){await Promise.resolve();await Promise.all(jobs.splice(0));}};
 const set=async(field,value)=>{change(field,value);await flush();};
 const labelled=(parent,label)=>[...parent.querySelectorAll('select,input')].find(field=>field.labels[0]?.textContent===label);
 const scheduled=setupScheduledSwapEditor({api:async(path,body)=>{
  if(path==='swaps/catalog')return swapCatalog;
  if(path==='swaps/scheduled-periods')return{periods:[{deliveryFrom:'2031-11-01',deliveryTo:'2031-12-01'},{deliveryFrom:'2031-12-01',deliveryTo:'2032-01-01'}]};
  if(path==='swaps/observation-options')return{indices:[index],samplings:[mode],selectedSampling:mode,choices:[],observations:[{}],observationCalendars:calendarRows.map(c=>({...c,name:c.code})),availability:null,customProjectionUnavailableReason:null};
  if(path==='swaps/scheduled-price'){captured=body.terms;throw Error('Synthetic preview payload captured.');}
  throw Error('Unexpected request: '+path);
 },act:work=>{const pending=work();jobs.push(pending);return pending;},busy:()=>false,zone:()=>'UTC',context:()=>({}),queue:async()=>{throw Error('No writes');},pending:()=>undefined,onDenied(){},host:()=>swapHost,unsavedChanged(){},reopen(){},onSettlement(){},ask:async()=>true});
 await scheduled.openNew();
 for(const [id,value] of [['scheduled-kind','fixed_floating'],['scheduled-commodity','power'],['scheduled-market','definition'],['scheduled-direction','-1'],['scheduled-discounting','none'],['scheduled-price-mode','agreed']])await set(document.getElementById(id),value);
 const cards=[...swapHost.querySelectorAll('.entry-side-card')];
 for(const [slot,card] of cards.entries()){
  for(const field of card.querySelectorAll('[data-side-calendar]'))if(!field.disabled)await set(field,calendarKey(ref(slot?'beta':'alpha')));
  await set(card.querySelector('[data-role=quantity-basis]'),'period_total');await set(card.querySelector('[data-role=quantity-unit]'),JSON.stringify(ref('energy')));
  card.querySelector('[data-role=delivery-from]').value='2031-11-01';card.querySelector('[data-role=delivery-to]').value='2032-01-01';
  [...card.querySelectorAll('button')].find(b=>b.textContent==='Generate monthly period rows').click();await flush();
  for(const period of card.querySelectorAll('.scheduled-period')){
   period.querySelector('[data-role=period-amount]').value='100';
   if(slot){for(const [label,value] of [['Floating price source','index'],['Price observation frequency','daily'],['Price averaging','observation_arithmetic'],['Index and projection curve',JSON.stringify(['definition','curve','index'])],['Price settlement','curve_target'],['Projected price source','ordinary']]){const field=labelled(period,label);if(!field)throw Error('Missing field: '+label);await set(field,value);}labelled(period,'Agreed floating spread').value='0';}
   else period.querySelector('[data-role=fixed-price]').value='2.50';
   for(const [label,value] of [['Payment date rule','day_of_following_month'],['Day of following month','5'],['Missing day','last_day'],['Date adjustment','following']])await set(labelled(period,label),value);
  }
 }
 const submit=async()=>{captured=undefined;document.getElementById('scheduled-swap-form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));await flush();if(!captured)throw Error('No scheduled preview request: '+swapHost.querySelector('[role=alert]').textContent);return captured.scheduled;};
 let request=await submit();
 check('Fixed periods use one whole-Side payment binding',request.first.calendarPolicy.payment.calendar.code==='alpha'&&request.first.periods.every(p=>p.fixed.paymentSchedule.calendar===null&&!p.fixed.calendarPolicy));
 check('Floating periods retain whole-Side observation and payment bindings',request.second.calendarPolicy.observation.calendar.code==='beta'&&request.second.calendarPolicy.payment.calendar.code==='beta'&&request.second.periods.every(p=>p.floating.paymentSchedule.calendar===null&&p.floating.observationSchedule.calendar===null));
 await set(cards[1].querySelector('[data-side-calendar=observation]'),calendarKey(ref('alpha')));request=await submit();
 check('Observation edits retain independent payment calendars and refresh periods',request.second.calendarPolicy.observation.calendar.code==='alpha'&&request.second.calendarPolicy.payment.calendar.code==='beta'&&request.second.periods.every(p=>p.floating.observationSchedule.calendar===null));
 await set(cards[1].querySelector('[data-role=quantity-basis]'),'selected_interval_power');await set(cards[1].querySelector('[data-role=quantity-unit]'),JSON.stringify(ref('power')));
 for(const [label,value] of [['Contract time zone / market clock',JSON.stringify(ref('clock'))],['Delivery product','weekday_block'],['Local hour beginning · inclusive','8'],['Local hour end · exclusive','20'],['Include Saturdays','false']])await set(labelled(cards[1],label),value);
 request=await submit();check('Power quantity generation uses the Side delivery binding',request.second.power.calendar===null&&request.second.calendarPolicy.delivery.calendar.code==='beta');
 await set(labelled(cards[1],'Delivery product'),'all_hours');for(const period of cards[1].querySelectorAll('.scheduled-period'))await set(labelled(period,'Date adjustment'),'unadjusted');request=await submit();
 check('All-hours delivery and unadjusted payments retain null calendars',request.second.power.calendar===null&&request.second.periods.every(p=>p.floating.paymentSchedule.calendar===null));
 // A retained amendment has only its pinned definition in the market selector.
 document.getElementById('scheduled-market').replaceChildren(new Option('Synthetic retained definition','definition'));
 scheduled.reset();scheduled.sync();
 check('Closing a retained ticket disposes calendar controls before global synchronization',document.getElementById('scheduled-swap-editor').hidden&&!document.querySelector('#scheduled-swap-editor [data-side-calendar]'));
 const report=document.createElement('section'),heading=document.createElement('h2');heading.textContent=`${results.filter(r=>r.passed).length} calendar checks passed; ${results.filter(r=>!r.passed).length} failed`;report.append(heading);
 for(const result of results){const line=document.createElement('p');line.textContent=(result.passed?'PASS: ':'FAIL: ')+result.name;report.append(line);}document.body.prepend(report);
 return results;
}
