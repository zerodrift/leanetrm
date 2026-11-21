import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
import {operatingFieldLayouts} from './side-layout-fixtures.mjs';
const markup=readFileSync(new URL('../src/index.html',import.meta.url),'utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,'');
const pins={definitionCatalogId:'dc',definitionRevisionId:'dr',referenceCatalogId:'rc',referenceRevisionId:'rr'};
const catalog={pins,locations:[{code:'existing',label:'Synthetic existing facility'}],clocks:[{code:'utc',label:'UTC'}],kinds:[{code:'battery',label:'Battery storage',intervalMinutes:[60]},{code:'thermal',label:'Thermal tolling',intervalMinutes:[60]},{code:'solar',label:'Solar output right',intervalMinutes:[60]}],powerQuantities:[{code:'mw',label:'Native MW'}],energyQuantities:[{code:'mwh',label:'Native MWh'}],fuelQuantities:[{code:'fuel',label:'Fuel energy'}],rightQuantities:[{code:'right',label:'One complete resource right'}],currencies:[{code:'usd',label:'USD'}],quotes:[{code:'energy',label:'USD per MWh',quantityCode:'mwh',currencyCode:'usd',multiplier:'1'}],maximumSides:8,compositionImplementation:'operating_side_composition_v2',sideChoices:[{kind:'battery',code:'discharging_energy',label:'Discharging energy',quantityKind:'energy',allowedKinds:['payment','delivery']},{kind:'battery',code:'charging_energy',label:'Charging energy',quantityKind:'energy',allowedKinds:['payment','delivery']},{kind:'battery',code:'grid_throughput',label:'Grid throughput',quantityKind:'energy',allowedKinds:['payment','delivery']}],paymentImplementation:'monthly_payment_date_rules_v1',canSave:true,canPreview:true};

catalog.valuationFieldGroups=operatingFieldLayouts;

test('a thermal plan shows the selected operating version and pricing profile before its interval instructions', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {setupStructuredPower}=await import('/structured-power-view.js'),host=document.getElementById('physical-power-page');host.hidden=false;
  const facility={id:'plant',version:4,effectiveFrom:'2031-01-01T00:00:00Z',effectiveTo:'2032-01-01T00:00:00Z',terms:{kind:'thermal',facilityCode:'existing',clockCode:'utc',intervalMinutes:60,powerQuantityCode:'mw',energyQuantityCode:'mwh',fuelQuantityCode:'fuel',thermal:{minimumOutputMw:'12',maximumOutputMw:'85',minimumUpMinutes:90}}};
  const profile={id:'east',kind:'thermal',facilityId:'plant',version:4,name:'East profile',inputLocation:'Gate A',inputCurve:'Fuel A',outputLocation:'Bus E',outputCurve:'Power E'};
  const trades={books:[],legalEntities:[],counterparties:[],convention:'right',canBook:true,facilityProfiles:[profile,{...profile,id:'west',name:'West profile',outputLocation:'Bus W',outputCurve:'Power W'}]};
  let last,authorized=true;const widget=setupStructuredPower(host,{api:async path=>path.endsWith('power-trades/catalog')?trades:path.endsWith('/catalog')?${JSON.stringify(catalog)}:path.endsWith('/list')?[facility]:facility,
   act:work=>(last=work()),busy:()=>false,source:()=>({snapshotId:'cut',contextKey:'scope'}),identity:()=>'synthetic',trading:{available:()=>authorized,openTrade:async()=>{}}});
  await widget.load();const choice=host.querySelector('select');choice.value='plant/4';choice.dispatchEvent(new Event('change'));await last;
  const field=title=>[...host.querySelectorAll('label')].find(x=>x.firstChild.textContent===title)?.querySelector('select,textarea');
  const profileSelect=field('Thermal facility pricing profile'),plan=field('Supplied interval plan'),summary=host.querySelector('.operating-plan-facility');
  const before=!!(profileSelect.compareDocumentPosition(plan)&Node.DOCUMENT_POSITION_FOLLOWING),empty=profileSelect.value;
  [...host.querySelectorAll('button')].find(b=>b.textContent==='Enter supplied plan')?.click();const focused=document.activeElement===profileSelect;
  profileSelect.value='west';profileSelect.dispatchEvent(new Event('change',{bubbles:true}));
  const terms=summary?.innerText??'',form=profileSelect.form===field('Operating trade book').form;
  trades.facilityProfiles=[profile];await widget.load();
  const sole=profileSelect.value==='east'&&profileSelect.required&&![...profileSelect.options].some(x=>x.value==='');
  authorized=false;widget.sync();
  const revoked=!summary.innerText.includes('Gate A')&&!profileSelect.value&&!host.innerText.includes('East profile');
  widget.contextChanged();return {before,empty,terms,form,focused,sole,revoked,cleared:!host.innerText.includes('Bus W')};
 })()`);
 assert.ok(result.before,'choose the facility profile before filling the plan');assert.equal(result.empty,'');assert.ok(result.form);assert.ok(result.focused,'enter the supplied plan from the facility chooser');
 assert.match(result.terms,/Synthetic existing facility/);assert.match(result.terms,/Version 4/);assert.match(result.terms,/85/);
 assert.match(result.terms,/Gate A/);assert.match(result.terms,/Bus W/);assert.doesNotMatch(result.terms,/Bus E/);assert.ok(result.cleared);assert.ok(result.sole,'the sole eligible profile is the one attached at booking');assert.ok(result.revoked,'revoking trade visibility clears its references from the physical plan');
}));

test('operating controls retain explicit disabled states through global busy updates',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 const state=await evaluate(`(async()=>{const {setupStructuredPower}=await import('/structured-power-view.js');const host=document.getElementById('physical-power-page');host.hidden=false;let busy=false;const widget=setupStructuredPower(host,{api:async(path)=>path.endsWith('/catalog')?${JSON.stringify(catalog)}:[],act:async work=>work(),busy:()=>busy,source:()=>({snapshotId:'frame',contextKey:'working'}),identity:()=> 'workspace:alpha'});await widget.load();
 const kind=[...host.querySelectorAll('label')].find(n=>n.firstChild.textContent==='Operating kind').querySelector('select');kind.value='battery';kind.dispatchEvent(new Event('change'));widget.sync();
 const planned=()=>[...host.querySelectorAll('input')].filter(n=>n.placeholder==='Coming soon');
 document.querySelectorAll('button,input,select,textarea').forEach(n=>{if(!n.closest('[data-own-controls]'))n.disabled=false;});widget.sync();const preserved=planned().every(n=>n.disabled);
 busy=true;widget.sync();const blocked=host.inert;busy=false;widget.sync();
 return {sideGroups:[...host.querySelectorAll('[data-field-layout-group]')].map(group=>group.dataset.fieldLayoutGroup),preserved,blocked,restored:!host.inert,planned:planned().length,masters:document.querySelectorAll('#nav-facilities').length,groups:document.querySelectorAll('#nav-group-scheduling').length};})()`);
 assert.deepEqual(state,{sideGroups:['valuation','sides'],preserved:true,blocked:true,restored:true,planned:3,masters:1,groups:1});
}));

test('facility form preserves exact transport, backend side values and actor-specific uncertain saves',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async(evaluate,command)=>{
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1080,deviceScaleFactor:1,mobile:false});
 const submitted=await evaluate(`(async()=>{
  const {setupStructuredPower}=await import('/structured-power-view.js');
  const host=document.getElementById('physical-power-page');host.hidden=false;
  const state=window.operatingTest={identity:'alpha',context:'working',stored:[],requests:[],receipts:{},uncertain:false,busy:false,last:Promise.resolve()};
  const quantity=(numerator,denominator='1')=>({numerator,denominator,specification:{code:'mwh'}}),money=numerator=>({numerator,denominator:'1',currency:{code:'usd'}});
  const interval={start:'2031-01-01T01:00:00Z',end:'2031-01-01T02:00:00Z',operatingDate:'2031-01-01',hourBeginning:1,minute:0,occurrence:1};
  const row=(q,price,amount)=>({interval,paymentAt:'2031-02-18T00:00:00Z',quantity:q,price:{numerator:price,denominator:'1',unit:{currency:{revision:{catalogId:'rc',revisionId:'rr'},code:'usd'},quantity:{revision:{catalogId:'rc',revisionId:'rr'},code:'mwh'},multiplier:'1'}},priceUnitLabel:'USD/MWh',amount:money(amount),presentValue:money(amount),state:1,evidenceId:null,knownAt:null});
  const api=async(path,body)=>{state.requests.push({path,body:structuredClone(body)});
   if(path.endsWith('/catalog'))return ${JSON.stringify(catalog)};
   if(path.endsWith('/list'))return state.stored;
   if(path.endsWith('/get'))return state.stored.find(x=>x.id===body.facilityId&&x.version===body.version);
   if(path.endsWith('/save')){const receipt={requestId:body.requestId,changed:true,facility:structuredClone(body.facility)};state.stored.push(receipt.facility);state.receipts[body.requestId]=receipt;if(state.uncertain){state.uncertain=false;throw Object.assign(Error('Unknown transport outcome'),{code:'response_unknown'});}return receipt;}
   if(path.endsWith('/status'))return state.receipts[body.requestId]??null;
   if(path==='amounts/display')return body.map(x=>x.value);
   if(path.endsWith('/preview')){if(state.holdPreview)await new Promise(resolve=>{state.releasePreview=resolve;});return {previewId:'preview',kind:'battery',clockFingerprint:'captured-clock',knownAt:'2026-11-16T00:00:00Z',intervals:[{interval,quantities:[{name:'grid_discharge',quantity:quantity('81','10')}]}],closingState:{at:interval.end,commitment:null,elapsedMinutes:null,startsUsed:null,quantities:[{name:'stored_energy',quantity:quantity('0')}]},valuation:{currency:{code:'usd'},value:money('610'),sides:[{id:'sell',name:'discharging_energy',coefficient:'1',quote:{code:'energy'},grossValue:money('810'),contribution:money('810'),rows:[row(quantity('81','10'),'100','810')]},{id:'buy',name:'charging_energy',coefficient:'-1',quote:{code:'energy'},grossValue:money('200'),contribution:money('-200'),rows:[row(quantity('10'),'20','200')]}]},limitations:['Hypothetical supplied plan; no booking.']};}
   throw Error('Unexpected test operation '+path);
  };
  state.widget=setupStructuredPower(host,{api,act:work=>state.last=(async()=>{state.busy=true;state.widget.sync();try{await work();}catch(error){state.error=error.message;}finally{state.busy=false;state.widget.sync();}})(),busy:()=>state.busy,source:()=>({snapshotId:'frame',contextKey:state.context}),identity:()=> 'workspace:'+state.identity});
  state.field=(title,parent=host)=>[...parent.querySelectorAll('label')].find(n=>n.firstChild.textContent===title)?.querySelector('input,select,textarea');
  state.set=(title,value,parent=host)=>{const control=state.field(title,parent);if(!control)throw Error('Missing control '+title);control.value=value;control.dispatchEvent(new Event('input',{bubbles:true}));control.dispatchEvent(new Event('change',{bubbles:true}));};
  state.click=async text=>{const button=[...host.querySelectorAll('button')].find(n=>n.textContent===text);if(!button)throw Error('Missing button '+text);button.click();await state.last;};
  await state.widget.load();await state.click('Add operating parameters');
  state.set('Operating kind','battery');
  for(const [title,value] of Object.entries({'Existing facility location':'existing','Operating clock':'utc','Native interval':'60','Power quantity (MW)':'mw','Energy quantity (MWh)':'mwh','Effective from (UTC, ending in Z)':'2031-01-01T00:00:00Z','Effective to (UTC, ending in Z)':'2032-01-01T00:00:00Z'}))state.set(title,value);
  const params={maximumChargeMw:'10.000000000001',maximumDischargeMw:'10',minimumStoredMwh:'0',maximumStoredMwh:'10',chargeEfficiency:'0.9',dischargeEfficiency:'0.9',meterBoundary:'grid_side_ac',lossPolicy:'zero_auxiliary_and_self_discharge',capacityPolicy:'fixed',simultaneousPolicy:'prohibited',chargingOrigin:'grid',throughputPolicy:'uncapped'};
  for(const [key,value] of Object.entries(params)){const control=host.querySelector('[data-field='+key+']');control.value=value;control.dispatchEvent(new Event(control.tagName==='SELECT'?'change':'input',{bubbles:true}));}
  const forms=host.querySelectorAll('form');if(!forms[0].reportValidity())throw Error('The completed physical parameters did not validate.');forms[0].requestSubmit();await state.last;
  for(const [title,value] of Object.entries({'First operating date':'2031-01-01','First native interval (one-based)':'1','Valuation':'fixed','One complete resource-right quantity for valuation':'right','Valuation currency':'usd','Perspective':'resource_right_holder_v1','Value measure':'remaining_operating_value_v1','Discounting':'undiscounted'}))state.set(title,value);
  await state.click('Use battery sale/purchase payment preset');
  for(const [index,side]of [...host.querySelectorAll('[data-operating-side]')].entries())for(const [title,value]of Object.entries({'Pricing':'fixed','Fixed-price quote':'energy','Fixed price':index===0?'100':'20','Monthly payment convention':'monthly_calendar_unadjusted','Payment days after delivery month':'18'}))state.set(title,value,side);
  for(const [key,value] of Object.entries({at:'2031-01-01T00:00:00Z',storedMwh:'0',cumulativeGridThroughputMwh:'0',terminalMinimumMwh:'0',terminalMaximumMwh:'0'})){const control=host.querySelector('[data-field='+key+']');control.value=value;control.dispatchEvent(new Event('input',{bubbles:true}));}
  state.set('Supplied interval plan','10,0,1\\n0,8.1,1');if(!forms[1].reportValidity())throw Error('The completed supplied plan did not validate.');forms[1].requestSubmit();await state.last;
  return {error:state.error??null,save:state.requests.find(x=>x.path.endsWith('/save')).body,preview:state.requests.find(x=>x.path.endsWith('/preview')).body,closing:host.textContent.includes('Closing state quantity')&&host.textContent.includes('stored energy'),shown:host.textContent.includes('Remaining operating value: 610 usd'),highlighted:host.querySelector('.operating-plan-value strong')?.textContent==='610 usd',priceUnits:host.textContent.includes('100 USD/MWh')&&host.textContent.includes('20 USD/MWh'),gross:host.textContent.includes('810')&&host.textContent.includes('-200'),overflow:document.documentElement.scrollWidth>innerWidth,timestamps:[...host.querySelectorAll('th')].map(n=>n.textContent)};
 })()`);
 assert.equal(submitted.error,null);assert.equal(submitted.save.facility.terms.facilityCode,'existing');assert.equal(submitted.save.facility.terms.battery.maximumChargeMw,'10.000000000001');assert.equal(Object.hasOwn(submitted.save.facility.terms,'rightQuantityCode'),false);
 assert.equal(submitted.highlighted,true,'the remaining value retains its structured entry emphasis');assert.equal(submitted.priceUnits,true,'each native price retains its complete managed unit');
 assert.equal(submitted.preview.composition.rightQuantityCode,'right');assert.equal(submitted.preview.composition.sides[1].price.fixedPrice,'20');assert.equal(submitted.preview.batteryDispatch[1].dischargeMw,'8.1');assert.equal(submitted.shown,true);assert.equal(submitted.closing,true);assert.equal(submitted.gross,true);assert.equal(submitted.overflow,false);assert.equal(submitted.timestamps.includes('Value UTC'),true,'a mixed Side schedule must describe delivery or payment value timing without inventing cash');assert.equal(submitted.timestamps.includes('Payment UTC'),false);
 if(process.env.STRUCTURED_POWER_SCREENSHOT){const {writeFile}=await import('node:fs/promises');await evaluate("document.getElementById('physical-power-page').scrollIntoView({block:'start'});new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");const shot=await command('Page.captureScreenshot',{format:'png'});await writeFile(process.env.STRUCTURED_POWER_SCREENSHOT+'-desktop.png',Buffer.from(shot.data,'base64'));}
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:false});
 const phone=await evaluate(`({width:innerWidth,scroll:document.documentElement.scrollWidth,overflow:[...document.querySelectorAll('#physical-power-page *')].filter(n=>n.getBoundingClientRect().width&&n.getBoundingClientRect().right>innerWidth+1).slice(0,10).map(n=>({tag:n.tagName,cls:n.className,text:n.textContent?.slice(0,45),width:n.getBoundingClientRect().width,right:n.getBoundingClientRect().right}))})`);
 assert.equal(phone.scroll<=phone.width,true,JSON.stringify(phone));
 if(process.env.STRUCTURED_POWER_SCREENSHOT){const {writeFile}=await import('node:fs/promises');await evaluate("document.querySelector('#physical-power-page form').scrollIntoView({block:'start'});new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");const shot=await command('Page.captureScreenshot',{format:'png'});await writeFile(process.env.STRUCTURED_POWER_SCREENSHOT+'-phone.png',Buffer.from(shot.data,'base64'));}
 const stale=await evaluate(`(async()=>{const s=operatingTest,host=document.getElementById('physical-power-page');s.holdPreview=true;host.querySelectorAll('form')[1].requestSubmit();while(!s.releasePreview)await new Promise(r=>setTimeout(r,0));const side=host.querySelector('[data-operating-side]');s.set('Fixed price','101',side);s.releasePreview();await s.last;const result=[...host.children].find(n=>n.querySelector('h3')?.textContent==='Preview result');return result.hidden;})()`);
 assert.equal(stale,true,'a late preview must not publish after Side terms changed');
 const recovery=await evaluate(`(async()=>{const s=operatingTest,host=document.getElementById('physical-power-page');const maximum=host.querySelector('[data-field=maximumChargeMw]');maximum.value='11';maximum.dispatchEvent(new Event('input',{bubbles:true}));s.uncertain=true;host.querySelector('form').requestSubmit();await s.last;const original=s.requests.filter(x=>x.path.endsWith('/save')).at(-1).body.requestId;
  const visible=text=>[...host.querySelectorAll('button')].find(n=>n.textContent===text)?.parentElement.hidden===false;
  s.identity='beta';s.widget.reset();const cleared=[...host.querySelectorAll('input,textarea')].every(n=>n.value==='');await s.widget.load();const isolated=!visible('Retry original save');
  s.identity='alpha';s.widget.reset();await s.widget.load();const restored=visible('Retry original save');await s.click('Check outstanding save');
  const checked=s.requests.filter(x=>x.path.endsWith('/status')).at(-1).body.requestId;
  return {cleared,isolated,restored,same:original===checked,pending:s.widget.unconfirmed(),revision:s.stored.at(-1).version};})()`);
 assert.deepEqual(recovery,{cleared:true,isolated:true,restored:true,same:true,pending:false,revision:2});
}));

test('each explicitly added Side chooses fixed or an exact flat market contract independently',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 const choices={...catalog,priceContracts:[{curveCode:'market_power',contractId:'january-contract',quoteCode:'energy',quantityCode:'mwh',currencyCode:'usd',deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01',label:'Synthetic power January 2021 · USD/MWh',definitionRevisionId:'dr'}]};
 const result=await evaluate(`(async()=>{const {setupStructuredPower}=await import('/structured-power-view.js');const host=document.getElementById('physical-power-page');host.hidden=false;const widget=setupStructuredPower(host,{api:async path=>path.endsWith('/catalog')?${JSON.stringify(choices)}:[],act:async work=>work(),busy:()=>false,source:()=>({snapshotId:'frame',contextKey:'working'}),identity:()=> 'workspace:alpha'});await widget.load();
 const field=(title,parent=host)=>[...parent.querySelectorAll('label')].find(n=>n.firstChild.textContent===title).querySelector('input,select');const set=(title,value,parent=host)=>{const n=field(title,parent);n.value=value;n.dispatchEvent(new Event('change',{bubbles:true}));};
 set('Operating kind','battery');set('Energy quantity (MWh)','mwh');set('Valuation currency','usd');set('Valuation','fixed');[...host.querySelectorAll('button')].find(n=>n.textContent==='Use battery sale/purchase payment preset').click();const [sale,charge]=host.querySelectorAll('[data-operating-side]');set('Pricing','selected_contract_flat_v1',sale);set('Market contract',JSON.stringify(['market_power','january-contract']),sale);set('Pricing','fixed',charge);
 return {selected:field('Market contract',sale).value,market:!field('Market contract',sale).disabled,fixedHidden:field('Fixed price',sale).disabled,chargeFixed:!field('Fixed price',charge).disabled,chargeMarketHidden:field('Market contract',charge).disabled,label:field('Pricing',sale).selectedOptions[0].textContent};})()`);
 assert.deepEqual(result,{selected:JSON.stringify(['market_power','january-contract']),market:true,fixedHidden:true,chargeFixed:true,chargeMarketHidden:true,label:'Selected market contract applied flat across supplied intervals'});
}));
