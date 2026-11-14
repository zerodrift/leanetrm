// Native entry controls and request payloads only; synthetic catalogs, no writes.
export async function sideCalendarChecks(){
 const results=[],check=(name,passed)=>results.push({name,passed:!!passed});
 const change=(field,value)=>{field.value=value;field.dispatchEvent(new Event('input',{bubbles:true}));field.dispatchEvent(new Event('change',{bubbles:true}));};
 const refused=read=>{try{read();return false;}catch{return true;}};
 const host=document.createElement('main');document.body.append(host);
 const {componentEditor}=await import('/ppa-components.js');
 const row=code=>({code,name:code});
 const catalog={references:{locations:[row('synthetic_hub')],quantityGroups:{contract:[{code:'capacity',unit_code:'mw'}],energy:[{code:'energy',unit_code:'mwh'}],capacity:[],certificate:[]},units:[{code:'mw',symbol:'MW'},{code:'mwh',symbol:'MWh'}],calendars:['alpha','beta'].map(row),priceIndices:[]},
  priceKinds:['flat'],definitionsMetadata:{priceCurves:[],volumeSources:[],generationAssets:[]},authoringPolicies:{escalationBases:[],certificateEligibility:[],certificateHolding:[],certificateDisposition:[],capacityRules:[],quantityStages:[],hourMemberships:['all']},contextTouVersions:[],touVersions:[],
  paymentOptions:{implementation:'payment_rule_v1',frequencies:[{value:'monthly',label:'Monthly'}],rules:[{value:'day_of_following_month',label:'Following month'}],dayBases:[],shortMonths:[{value:'last_day',label:'Last day'}],adjustments:['following','unadjusted'].map(value=>({value,label:value}))}};
 const quantity={kind:'profile',sources:[],assets:[],slots:[],stage:null,capacity:'capacity',energy:'energy',hours:{membership:'all',calendar:'alpha',touProduct:null,touVersion:null}};
 const payment={frequency:'monthly',rule:'day_of_following_month',days:null,dayOfMonth:5,dayBasis:null,shortMonth:'last_day',adjustment:'following',calendar:'alpha',implementation:'payment_rule_v1'};
 const template={role:'energy',ordinal:1,sideKind:'energy',sideKinds:['energy'],linkedSlots:[],quantityKinds:['profile','slots'],linkRoles:['energy']};
 const member={role:'energy',ordinal:1,coefficient:'1',side:{kind:'energy',location:'synthetic_hub',quantity,paymentRule:payment,fixedPrice:null,indexPrice:null,attribute:null}};
 let ordinal=20;
 const mount=(initial,composition)=>{const card=document.createElement('section');host.append(card);const editor=componentEditor(card,catalog,template,ordinal++,()=>{},initial,composition);return{card,editor,calendar:card.querySelector('[data-side-calendar]')};};
 const preset=mount(structuredClone(member));change(preset.calendar,'beta');
 let submitted=preset.editor.read(quantity);
 check('PPA preset applies one calendar to retained profile and payment',submitted.side.quantity.hours.calendar==='beta'&&submitted.side.paymentRule.calendar==='beta');
 check('PPA calendar edits leave original retained inputs unchanged',quantity.hours.calendar==='alpha'&&payment.calendar==='alpha');
 change(preset.card.querySelector('[data-field=quantity-choice]'),'selected');submitted=preset.editor.read(quantity);
 check('PPA selected profile uses the side calendar',submitted.side.quantity.hours.calendar==='beta'&&quantity.hours.calendar==='alpha');
 const composed=mount(structuredClone(member),{delivery:()=>({clock:'synthetic',cadence:'60'}),links:()=>[]});change(composed.calendar,'beta');submitted=composed.editor.read(quantity);
 check('Composed PPA profile and payment share the calendar',submitted.side.quantity.hours.calendar==='beta'&&submitted.side.paymentRule.calendar==='beta');
 check('Composed PPA exposes exactly one visible calendar selector',[...composed.card.querySelectorAll('select')].filter(field=>field.labels[0]?.textContent.includes('calendar')&&field.getClientRects().length).length===1);
 const mixed=structuredClone(member);mixed.side.paymentRule.calendar='beta';const conflict=mount(mixed);
 check('Mixed saved PPA calendars require explicit resolution',!conflict.calendar.value&&refused(()=>conflict.editor.read(quantity))&&conflict.card.textContent.includes('different saved calendars'));
 change(conflict.calendar,'beta');submitted=conflict.editor.read(quantity);
 check('Resolving a PPA conflict updates both owned schedules',submitted.side.quantity.hours.calendar==='beta'&&submitted.side.paymentRule.calendar==='beta');
 const unadjusted=structuredClone(member);unadjusted.side.paymentRule={...payment,adjustment:'unadjusted',calendar:null};const plain=mount(unadjusted);change(plain.calendar,'beta');
 check('Unadjusted PPA payments retain a null calendar',plain.editor.read(quantity).side.paymentRule.calendar===null);
 const linked=structuredClone(member);linked.side.quantity={...quantity,kind:'slots',hours:null,slots:[{role:'energy',ordinal:1}]};const cash=mount(linked);change(cash.calendar,'beta');submitted=cash.editor.read(quantity);
 check('Linked quantities remain source-side references',submitted.side.quantity.hours===null&&submitted.side.quantity.slots[0].role==='energy'&&quantity.hours.calendar==='alpha');
 change(cash.calendar,'alpha');check('Changing one side leaves other side selections intact',cash.calendar.value==='alpha'&&preset.calendar.value==='beta');
 const {setupScheduledSwapEditor}=await import('/scheduled-swap-editor.js'),{calendarKey}=await import('/side-calendar.js');
 const {setCurveNames}=await import('/market-view.js');setCurveNames([{code:'curve',name:'Synthetic power'}]);
 const shelf=document.createElement('div');shelf.id='editor-shelf';document.body.append(shelf);
 const swapHost=document.createElement('section');host.append(swapHost);
 const ref=code=>({revision:{catalogId:'synthetic',revisionId:'synthetic'},code}),mode={resetFrequency:'daily',averagingMethod:'observation_arithmetic',label:'Business-day observations',requiresObservationCalendar:true,requiresQuantityWeights:false};
 const index={definitionRevisionId:'definition',curve:'curve',index:'index',commodity:'power',currency:'USD',currencyCode:'usd',quantityCode:'energy',unit:'MWh',quoteMultiplier:'1',quantityChoices:[{specification:ref('energy'),unit:'MWh'}],samplings:[mode],customProjections:[],settlementIndices:[],contracts:[],powerQuantitySupported:true};
 const calendarRows=['alpha','beta'].map(code=>({definitionRevisionId:'definition',code,reference:ref(code)}));
 const swapCatalog={bookingActor:{name:'Synthetic trader'},books:[],legalEntities:[],counterparties:[],commodities:[{code:'power',name:'Power'}],context:{},canBook:true,indices:[index],paymentCalendars:calendarRows,paymentOptions:catalog.paymentOptions,
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
  await set(card.querySelector('[data-side-calendar]'),calendarKey(ref(slot?'beta':'alpha')));
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
 check('Every fixed payment period uses its side calendar',request.first.periods.every(p=>p.fixed.paymentSchedule.calendar==='alpha'));
 check('Every floating observation and payment uses its side calendar',request.second.periods.every(p=>p.floating.paymentSchedule.calendar==='beta'&&p.floating.observationSchedule.calendar.code==='beta'));
 await set(cards[1].querySelector('[data-side-calendar]'),calendarKey(ref('alpha')));request=await submit();
 check('Changing a whole-side calendar refreshes every floating period',request.second.periods.every(p=>p.floating.paymentSchedule.calendar==='alpha'&&p.floating.observationSchedule.calendar.code==='alpha'));
 await set(cards[1].querySelector('[data-role=quantity-basis]'),'selected_interval_power');await set(cards[1].querySelector('[data-role=quantity-unit]'),JSON.stringify(ref('power')));
 for(const [label,value] of [['Contract time zone / market clock',JSON.stringify(ref('clock'))],['Delivery product','weekday_block'],['Local hour beginning · inclusive','8'],['Local hour end · exclusive','20'],['Include Saturdays','false']])await set(labelled(cards[1],label),value);
 request=await submit();check('Holiday-aware delivery uses the same exact side calendar',calendarKey(request.second.power.calendar)===calendarKey(ref('alpha')));
 await set(labelled(cards[1],'Delivery product'),'all_hours');for(const period of cards[1].querySelectorAll('.scheduled-period'))await set(labelled(period,'Date adjustment'),'unadjusted');request=await submit();
 check('All-hours delivery and unadjusted payments retain null calendars',request.second.power.calendar===null&&request.second.periods.every(p=>p.floating.paymentSchedule.calendar===null));
 const report=document.createElement('section'),heading=document.createElement('h2');heading.textContent=`${results.filter(r=>r.passed).length} calendar checks passed; ${results.filter(r=>!r.passed).length} failed`;report.append(heading);
 for(const result of results){const line=document.createElement('p');line.textContent=(result.passed?'PASS: ':'FAIL: ')+result.name;report.append(line);}document.body.prepend(report);
 return results;
}
