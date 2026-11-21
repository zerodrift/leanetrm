// The scheduled swap ticket opened on a running swap (D441): native controls and the preview request only; a synthetic catalog, no writes.
export async function runningTicketChecks(){
 const results=[],check=(name,passed)=>results.push({name,passed:!!passed});
 const change=(field,value)=>{field.value=value;field.dispatchEvent(new Event('input',{bubbles:true}));field.dispatchEvent(new Event('change',{bubbles:true}));};
 const ref=code=>({revision:{catalogId:'synthetic',revisionId:'synthetic'},code});
 const {setupScheduledSwapEditor}=await import('/scheduled-swap-editor.js');
 const {setCurveNames}=await import('/market-view.js');setCurveNames([{code:'curve',name:'Synthetic power'}]);
 const shelf=document.createElement('div');shelf.id='editor-shelf';document.body.append(shelf);
 const host=document.createElement('section');document.body.append(host);
 const mode={resetFrequency:'daily',averagingMethod:'observation_arithmetic',label:'Business-day observations',requiresObservationCalendar:true,requiresQuantityWeights:false};
 const index={definitionRevisionId:'definition',curve:'curve',index:'index',commodity:'power',currency:'USD',currencyCode:'usd',quantityCode:'energy',unit:'MWh',quoteMultiplier:'1',quantityChoices:[{specification:ref('energy'),unit:'MWh'}],samplings:[mode],customProjections:[],settlementIndices:[],contracts:[],powerQuantitySupported:false};
 const calendars=['alpha'].map(code=>({definitionRevisionId:'definition',code,reference:ref(code)}));
 const payment={frequency:'monthly',rule:'day_of_following_month',days:null,dayOfMonth:5,dayBasis:null,shortMonth:'last_day',adjustment:'following',calendar:null,implementation:'payment_rule_v1'};
 const catalog={bookingActor:{name:'Synthetic trader'},books:[{id:'book',portfolio:'Synthetic',name:'Book'}],legalEntities:[],counterparties:[],commodities:[{code:'power',name:'Power'}],
  context:{contextId:'00000000-0000-0000-0000-000000000001',effectiveOn:'2031-11-15',knownAt:'2031-11-15T00:00:00Z'},canBook:true,indices:[index],
  sideCalendars:[{code:'alpha',reference:ref('alpha'),coverageFrom:'2020-01-01',coverageTo:'2040-01-01',weekendMask:48,exceptions:[]}],paymentCalendars:calendars,
  paymentOptions:{implementation:'payment_rule_v1',frequencies:[{value:'monthly',label:'Monthly'}],rules:[{value:'day_of_following_month',label:'Following month'}],dayBases:[],shortMonths:[{value:'last_day',label:'Last day'}],adjustments:['following','unadjusted'].map(value=>({value,label:value}))},
  scheduledOptions:{implementation:'scheduled_financial_swap_v1',priceApplication:'period_average_times_period_quantity_v1',powerQuantities:[],clocks:[]},entryTemplate:{payment},factorOptions:{}};
 const months=(from,to)=>{const periods=[];for(let at=new Date(from+'T00:00:00Z');at<new Date(to+'T00:00:00Z');){const next=new Date(at);next.setUTCMonth(next.getUTCMonth()+1);periods.push({deliveryFrom:at.toISOString().slice(0,10),deliveryTo:next.toISOString().slice(0,10)});at=next;}return periods;};
 let jobs=[],captured,asked=0;
 const flush=async()=>{for(let n=0;n<16;n++){await Promise.resolve();await Promise.all(jobs.splice(0));}};
 const set=async(field,value)=>{change(field,value);await flush();};
 const labelled=(parent,label)=>[...parent.querySelectorAll('select,input')].find(field=>field.labels[0]?.textContent===label);
 const scheduled=setupScheduledSwapEditor({api:async(path,body)=>{
  if(path==='swaps/catalog')return catalog;
  if(path==='swaps/scheduled-periods')return{periods:months(body.from,body.to)};
  if(path==='swaps/observation-options')return{indices:[index],samplings:[mode],selectedSampling:mode,choices:[],observations:[{}],observationCalendars:calendars.map(c=>({...c,name:c.code})),availability:null,customProjectionUnavailableReason:null};
  if(path==='trade-formats/read')return{roles:[]};
  if(path==='swaps/scheduled-price'){captured=body.terms;throw Error('Synthetic preview payload captured.');}
  throw Error('Unexpected request: '+path);
 },act:work=>{const pending=work();jobs.push(pending);return pending;},busy:()=>false,zone:()=>'UTC',context:()=>({}),queue:async()=>{throw Error('No writes');},pending:()=>undefined,onDenied(){},host:()=>host,unsavedChanged(){},reopen(){},onSettlement(){},ask:async()=>{asked++;return true;}});
 const sides=()=>[...host.querySelectorAll('.entry-side-card')];
 const periods=side=>[...side.querySelectorAll('.scheduled-period')];
 const enter=async(period,slot)=>{
  period.querySelector('[data-role=period-amount]').value='100';
  if(slot){for(const [label,value] of [['Floating price source','index'],['Price observation frequency','daily'],['Price averaging','observation_arithmetic'],['Index and projection curve',JSON.stringify(['definition','curve','index'])],['Price settlement','curve_target'],['Projected price source','ordinary']])await set(labelled(period,label),value);labelled(period,'Agreed floating spread').value='0';}
  else period.querySelector('[data-role=fixed-price]').value='2.50';
  for(const [label,value] of [['Payment date rule','day_of_following_month'],['Day of following month','5'],['Missing day','last_day'],['Date adjustment','following']])await set(labelled(period,label),value);
 };
 const submit=async()=>{captured=undefined;document.getElementById('scheduled-swap-form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));await flush();if(!captured)throw Error('No preview request: '+host.querySelector('[role=alert]')?.textContent);return captured;};
 // Book-shaped terms come from the ticket itself, so the running ticket is opened on what the page would have sent.
 await scheduled.openNew();
 for(const [id,value] of [['scheduled-kind','fixed_floating'],['scheduled-commodity','power'],['scheduled-market','definition'],['scheduled-direction','-1'],['scheduled-discounting','none'],['scheduled-price-mode','agreed']])await set(document.getElementById(id),value);
 document.getElementById('scheduled-book').value='book';
 for(const [slot,side] of sides().entries()){
  for(const field of side.querySelectorAll('[data-side-calendar]'))if(!field.disabled)await set(field,(await import('/side-calendar.js')).calendarKey(ref('alpha')));
  await set(side.querySelector('[data-role=quantity-basis]'),'period_total');await set(side.querySelector('[data-role=quantity-unit]'),JSON.stringify(ref('energy')));
  side.querySelector('[data-role=delivery-from]').value='2031-11-01';side.querySelector('[data-role=delivery-to]').value='2032-01-01';
  [...side.querySelectorAll('button')].find(b=>b.textContent==='Generate monthly period rows').click();await flush();
  for(const period of periods(side))await enter(period,slot);
 }
 const booked=structuredClone((await submit()).scheduled);
 scheduled.reset();
 const lifecycle={remainingValue:'25',determinedOutstanding:'0',determinedPv:'0',dueBalance:'0',realized:'0',unrealized:'25',netCash:'0',corrections:'0',roundingEffect:'0',estimatedRemainingPv:'25',
  payments:[{membershipOrdinal:0,deliveryFrom:'2031-11-01',deliveryTo:'2031-12-01',state:'Paid',remainingValue:'0'},{membershipOrdinal:1,deliveryFrom:'2031-12-01',deliveryTo:'2032-01-01',state:'Projected',remainingValue:'25'}]};
 await scheduled.openDetail({state:{tradeId:'00000000-0000-0000-0000-00000000000a',revision:1,tradeReference:'TR-SYNTHETIC',
   terms:{scheduled:booked,bookId:'book',discounting:false,commodity:'power',firstCoefficient:'-1',definitionRevisionId:'definition'}},history:[{revision:1,kind:'booked'}],catalog,valuation:null,
  settlementValuation:lifecycle,lockedPeriods:[{deliveryFrom:'2031-11-01',deliveryTo:'2031-12-01',reason:'settled'}],canAmend:true,asOf:'2031-11-15T00:00:00Z',context:catalog.context,amendUnavailableReason:null});
 await flush();scheduled.sync();
 const controls=period=>[...period.querySelectorAll('input,select,button')];
 check('A settled month states that it cannot change on both sides',sides().every(side=>periods(side)[0].querySelector('h4').textContent.includes('Settled · cannot change')&&!periods(side)[1].querySelector('h4').textContent.includes('cannot change')));
 check('Every control of a settled month is disabled',sides().every(side=>controls(periods(side)[0]).length>3&&controls(periods(side)[0]).every(control=>control.disabled)));
 check('A later month stays editable',sides().every(side=>!periods(side)[1].querySelector('[data-role=period-amount]').disabled)&&!periods(sides()[0])[1].querySelector('[data-role=fixed-price]').disabled);
 check('A running swap keeps its delivery start, quantity basis and unit',sides().every(side=>['delivery-from','quantity-basis','quantity-unit'].every(role=>side.querySelector(`[data-role=${role}]`).disabled)&&!side.querySelector('[data-role=delivery-to]').disabled));
 check('A running swap keeps its product, market, direction, pricing mode and discounting',['scheduled-kind','scheduled-commodity','scheduled-market','scheduled-direction','scheduled-price-mode','scheduled-discounting'].every(id=>document.getElementById(id).disabled));
 check('A running swap opens on its lifecycle value',host.textContent.includes('Value under the settlement lifecycle')&&host.textContent.includes('Remaining contractual value'));
 periods(sides()[0])[1].querySelector('[data-role=fixed-price]').value='2.75';
 for(const side of sides()){side.querySelector('[data-role=delivery-to]').value='2032-02-01';[...side.querySelectorAll('button')].find(b=>b.textContent==='Generate monthly period rows').click();await flush();}
 check('Extending the term keeps every entered month without asking to replace them',asked===0&&sides().every(side=>periods(side).length===3)&&
  periods(sides()[0])[1].querySelector('[data-role=fixed-price]').value==='2.75'&&periods(sides()[0])[0].querySelector('[data-role=period-amount]').value==='100');
 check('An added month is empty and editable, and the settled month stays disabled',sides().every(side=>periods(side)[2].querySelector('[data-role=period-amount]').value===''&&
  !periods(side)[2].querySelector('[data-role=period-amount]').disabled&&controls(periods(side)[0]).every(control=>control.disabled)));
 for(const [slot,side] of sides().entries())await enter(periods(side)[2],slot);
 [...host.querySelectorAll('input,textarea')].find(field=>field.labels?.[0]?.textContent?.includes('reason')).value='Synthetic extension';
 const request=await submit();
 check('The amendment request keeps the settled month exactly and carries the trade, revision and effective instant',
  ['first','second'].every(side=>JSON.stringify(request.scheduled[side].periods[0])===JSON.stringify(booked[side].periods[0])&&request.scheduled[side].periods.length===3)&&
  request.scheduled.first.periods[1].fixed.price==='2.75'&&request.tradeId==='00000000-0000-0000-0000-00000000000a'&&request.expectedRevision===1&&request.effectiveAt==='2031-11-15T00:00:00Z'&&request.reason==='Synthetic extension');
 return results;
}
