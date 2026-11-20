import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
import {operatingFieldLayouts} from './side-layout-fixtures.mjs';

const markup=readFileSync(new URL('../src/index.html',import.meta.url),'utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,'');
const pins={definitionCatalogId:'dc',definitionRevisionId:'dr',referenceCatalogId:'rc',referenceRevisionId:'rr'};
const parameters={maximumChargeMw:'10',maximumDischargeMw:'10',minimumStoredMwh:'0',maximumStoredMwh:'10',chargeEfficiency:'0.9',dischargeEfficiency:'0.9',meterBoundary:'grid_side_ac',lossPolicy:'zero_auxiliary_and_self_discharge',capacityPolicy:'fixed',simultaneousPolicy:'prohibited',chargingOrigin:'grid',throughputPolicy:'uncapped',maximumThroughputMwh:null};
const facility={id:'facility',version:1,effectiveFrom:'2031-01-01T00:00:00Z',effectiveTo:'2032-01-01T00:00:00Z',terms:{...pins,facilityCode:'existing',clockCode:'utc',intervalMinutes:60,powerQuantityCode:'mw',energyQuantityCode:'mwh',fuelQuantityCode:null,kind:'battery',battery:parameters,thermal:null,solar:null,implementation:'structured_power_facility_v1'}};
const catalog={pins,locations:[{code:'existing',label:'Synthetic existing facility'}],clocks:[{code:'utc',label:'UTC'}],kinds:[{code:'battery',label:'Battery storage',intervalMinutes:[60]}],powerQuantities:[{code:'mw',label:'Native MW'}],energyQuantities:[{code:'mwh',label:'Native MWh'}],fuelQuantities:[],rightQuantities:[{code:'right',label:'One complete resource right'}],currencies:[{code:'usd',label:'USD'}],quotes:[{code:'energy',label:'USD per MWh',quantityCode:'mwh',currencyCode:'usd',multiplier:'1'}],priceContracts:[{curveCode:'synthetic_curve',contractId:'january',quoteCode:'energy',quantityCode:'mwh',currencyCode:'usd',label:'Synthetic January contract'}],sideChoices:[{kind:'battery',code:'discharging_energy',label:'Discharging energy',quantityKind:'energy',allowedKinds:['payment','delivery']},{kind:'battery',code:'charging_energy',label:'Charging energy',quantityKind:'energy',allowedKinds:['payment','delivery']},{kind:'battery',code:'grid_throughput',label:'Grid throughput',quantityKind:'energy',allowedKinds:['payment','delivery']},{kind:'battery',code:'horizon_right',label:'One complete horizon right',quantityKind:'right',allowedKinds:['payment']}],maximumSides:8,compositionImplementation:'operating_side_composition_v2',paymentImplementation:'monthly_payment_date_rules_v1',canSave:true,canPreview:true};
catalog.valuationFieldGroups=operatingFieldLayouts;

test('the operating workspace preserves the selected display zone in its saved Side terms',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {setupStructuredPower}=await import('/structured-power-view.js');const host=document.getElementById('physical-power-page');host.hidden=false;let last=Promise.resolve();
  const booked={tradeId:'native-trade',instrumentId:'native-binding',bindingId:'native-binding',revision:1,tradeReference:'TR000001',book:'Synthetic book',facilityName:'Synthetic facility',facilityVersion:1,resourceKind:'battery',
   sideGroups:[{instrumentId:'native-binding',bindingId:'native-binding',label:'Retained operating composition',sides:[{slot:0,sideId:'native-side',label:'Contract output',roleLabel:'Operating-right payment',directionLabel:'Company receives proceeds',overviewSections:[{label:'Terms',values:[{label:'Through',value:'2032-02-01T12:00:00Z',kind:'instant'}]}]}]}]};
  const api=async path=>{
   if(path==='physical/power-facilities/catalog')return ${JSON.stringify(catalog)};
   if(path==='physical/power-facilities/list')return [];
   if(path==='physical/power-trades/catalog')return {books:[{id:'book',name:'Synthetic book'}],legalEntities:[],counterparties:[],convention:'supplied_operating_right_trade_v1',canBook:false};
   if(path==='physical/power-trades/directory')return {trades:[booked],total:1,nextOffset:null};
   if(path==='physical/power-trades/get')return booked;
   throw Error('Unexpected read '+path);
  };
  const widget=setupStructuredPower(host,{api,act:work=>last=work(),busy:()=>false,source:()=>({snapshotId:'frame',contextKey:'working'}),identity:()=> 'synthetic:alpha',zone:()=> 'America/Chicago',trading:{available:()=>true,canHedge:()=>false,openTrade:async()=>{}}});await widget.load();
  const field=title=>[...host.querySelectorAll('label')].find(n=>n.firstChild.textContent===title).querySelector('select');
  field('Saved operating trade book').value='all';[...host.querySelectorAll('button')].find(n=>n.textContent==='Show operating trades').click();await last;
  const choice=field('Saved operating trade');choice.value='native-trade';choice.dispatchEvent(new Event('change',{bubbles:true}));await last;
  const saved=host.querySelector('[data-operating-side-composition]');return {text:saved.textContent,raw:saved.querySelector('[title="2032-02-01T12:00:00Z"]')?.title};
 })()`);
 assert.match(result.text,/CST|America\/Chicago/,'the parent workspace must forward its selected zone to saved Side rendering');
 assert.equal(result.raw,'2032-02-01T12:00:00Z');
}));

test('operating Side cards start empty and preserve repeated sources, independent payments and exact ordered transport',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {setupStructuredPower}=await import('/structured-power-view.js');const host=document.getElementById('physical-power-page');host.hidden=false;let last=Promise.resolve();
  const widget=setupStructuredPower(host,{api:async path=>path.endsWith('/catalog')?${JSON.stringify(catalog)}:path.endsWith('/list')?[${JSON.stringify(facility)}]:${JSON.stringify(facility)},act:work=>last=work(),busy:()=>false,source:()=>({snapshotId:'frame',contextKey:'working'}),identity:()=> 'synthetic:alpha'});await widget.load();
  const field=(parent,title)=>[...parent.querySelectorAll('label')].find(n=>n.firstChild.textContent===title)?.querySelector('input,select,textarea');
  const set=(parent,title,value)=>{const n=field(parent,title);if(!n)throw Error('Missing '+title);n.value=value;n.dispatchEvent(new Event('input',{bubbles:true}));n.dispatchEvent(new Event('change',{bubbles:true}));};
  set(host,'Saved operating facility version','facility/1');await last;set(host,'Valuation','fixed');
  const click=async(parent,title)=>{const n=[...parent.querySelectorAll('button')].find(n=>n.textContent===title);if(!n)throw Error('Missing '+title);n.click();await last;};
  const add=[...host.querySelectorAll('button')].find(n=>n.textContent==='Add Side');if(!add)return {addPresent:false};
  const cards=()=>[...host.querySelectorAll('[data-operating-side]')];const initiallyEmpty=cards().length===0;await click(host,'Add Side');
  const first=cards()[0],blank=['Side name','Quantity source','Sign','Side kind','Pricing','Fixed-price quote','Fixed price','Monthly payment convention','Payment days after delivery month'].every(title=>field(first,title).value==='');
  for(const [title,value]of Object.entries({'Side key':'fee_a','Side name':'Throughput fee','Quantity source':'grid_throughput','Sign':'-1','Side kind':'payment','Pricing':'fixed','Fixed-price quote':'energy','Fixed price':'1.000000000001','Monthly payment convention':'monthly_calendar_unadjusted','Payment days after delivery month':'18'}))set(first,title,value);
  await click(first,'Duplicate Side');const second=cards()[1],differentKeys=field(first,'Side key').value!==field(second,'Side key').value;
  set(second,'Side key','fee_b');set(second,'Side name','Other throughput fee');set(second,'Payment days after delivery month','7');set(second,'Pricing','selected_contract_flat_v1');set(second,'Market contract',JSON.stringify(['synthetic_curve','january']));
  await click(second,'Move Side up');await click(host,'Add Side');await click(cards()[2],'Remove Side');
  for(const [title,value]of Object.entries({'First operating date':'2031-01-01','First native interval (one-based)':'1','One complete resource-right quantity for valuation':'right','Valuation currency':'usd','Perspective':'resource_right_holder_v1','Value measure':'remaining_operating_value_v1','Discounting':'undiscounted','Supplied interval plan':'0,0,1'}))set(host,title,value);
  for(const [key,value]of Object.entries({at:'2031-01-01T00:00:00Z',storedMwh:'0',cumulativeGridThroughputMwh:'0',terminalMinimumMwh:'0',terminalMaximumMwh:'0'})){const n=host.querySelector('[data-field='+key+']');n.value=value;n.dispatchEvent(new Event('input',{bubbles:true}));}
  const request=widget.plan();return {addPresent:true,initiallyEmpty,blank,differentKeys,request,groups:[...host.querySelectorAll('[data-field-layout-group]')].map(group=>group.dataset.fieldLayoutGroup),grouped:cards().every(card=>card.closest('[data-field-layout-group]')?.dataset.fieldLayoutGroup==='sides')};
 })()`);
 assert.equal(result.addPresent,true,'the actual operating form must expose arbitrary Side entry');
 assert.equal(result.initiallyEmpty,true,'loading a facility or enabling valuation must not apply a preset');
 assert.equal(result.blank,true,'adding a Side must not infer its financial terms');assert.equal(result.differentKeys,true);
 assert.deepEqual(result.groups,['valuation','sides']);assert.equal(result.grouped,true,'every independent dynamic Side must stay inside its exact backend-declared field group');
 assert.equal(result.request.valuation,null);const composition=result.request.composition;
 assert.equal(composition.implementation,'operating_side_composition_v2');
 assert.deepEqual(composition.sides.map(s=>[s.key,s.name,s.quantitySource,s.coefficient,s.kind,s.paymentRule.days]),[['fee_b','Other throughput fee','grid_throughput','-1','payment',7],['fee_a','Throughput fee','grid_throughput','-1','payment',18]]);
 assert.deepEqual(composition.sides[0].price,{mode:'selected_contract_flat_v1',quoteCode:'energy',fixedPrice:null,curveCode:'synthetic_curve',contractId:'january'});
 assert.deepEqual(composition.sides[1].price,{mode:'fixed',quoteCode:'energy',fixedPrice:'1.000000000001',curveCode:null,contractId:null});
}));

test('explicit presets leave price/payment choices blank and a delivery Side carries no cash payment rule',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<main id="host"></main>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {setupOperatingSideCards}=await import('/structured-power-sides-view.js');const host=document.getElementById('host');let changes=0;
  const widget=setupOperatingSideCards(host,{catalog:()=>(${JSON.stringify(catalog)}),context:()=>({kind:'battery',energy:'mwh',fuel:'',right:'right',currency:'usd'}),changed:()=>changes++});widget.setEnabled(true);
  const before=host.querySelectorAll('[data-operating-side]').length;
  const click=title=>[...host.querySelectorAll('button')].find(n=>n.textContent===title).click();click('Use battery sale/purchase payment preset');
  const field=(parent,title)=>[...parent.querySelectorAll('label')].find(n=>n.firstChild.textContent===title).querySelector('input,select');const set=(parent,title,value)=>{const n=field(parent,title);n.value=value;n.dispatchEvent(new Event('change',{bubbles:true}));};
  const [delivery,payment]=host.querySelectorAll('[data-operating-side]');
  const unselected=[delivery,payment].every(c=>['Pricing','Fixed-price quote','Fixed price','Monthly payment convention','Payment days after delivery month'].every(title=>field(c,title).value===''));
  for(const c of [delivery,payment]){set(c,'Pricing','fixed');set(c,'Fixed-price quote','energy');set(c,'Fixed price','5');set(c,'Monthly payment convention','monthly_calendar_unadjusted');set(c,'Payment days after delivery month','18');}
  set(delivery,'Side kind','delivery');const hidden=field(delivery,'Payment days after delivery month').disabled&&field(delivery,'Monthly payment convention').disabled;
  const rows=widget.capture();widget.reset();return {before,unselected,hidden,rows,reset:host.querySelectorAll('[data-operating-side]').length,changed:changes>0};
 })()`);
 assert.equal(result.before,0);assert.equal(result.unselected,true);assert.equal(result.hidden,true);assert.equal(result.rows[0].kind,'delivery');assert.equal(result.rows[0].paymentRule,null);
 assert.equal(result.rows[1].kind,'payment');assert.equal(result.rows[1].paymentRule.days,18);assert.equal(result.reset,0);assert.equal(result.changed,true);
}));

test('the server-declared composition bound disables add and duplicate until a Side is removed',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<main id="host"></main>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {setupOperatingSideCards}=await import('/structured-power-sides-view.js');const host=document.getElementById('host');
  const widget=setupOperatingSideCards(host,{catalog:()=>({...${JSON.stringify(catalog)},maximumSides:2}),context:()=>({kind:'battery',energy:'mwh',fuel:'',right:'right',currency:'usd'}),changed:()=>{}});widget.setEnabled(true);
  const button=title=>[...host.querySelectorAll('button')].find(n=>n.textContent===title);button('Add Side').click();button('Add Side').click();button('Add Side').click();
  const count=host.querySelectorAll('[data-operating-side]').length,disabled=button('Add Side').disabled&&[...host.querySelectorAll('button')].filter(n=>n.textContent==='Duplicate Side').every(n=>n.disabled),shown=host.textContent.includes('Maximum 2 Sides');
  button('Remove Side').click();return {count,disabled,shown,enabled:!button('Add Side').disabled&&!button('Duplicate Side').disabled};
 })()`);
 assert.deepEqual(result,{count:2,disabled:true,shown:true,enabled:true});
}));

test('changing to a payment-only source clears a prior delivery kind without choosing payment',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<main id="host"></main>',async evaluate=>{
 const choices={...catalog,sideChoices:[{kind:'battery',code:'discharging_energy',label:'Discharging energy',quantityKind:'energy',allowedKinds:['payment','delivery']},{kind:'battery',code:'horizon_right',label:'Once per horizon',quantityKind:'right',allowedKinds:['payment']}]};
 const result=await evaluate(`(async()=>{
  const {setupOperatingSideCards}=await import('/structured-power-sides-view.js');const host=document.getElementById('host');
  const widget=setupOperatingSideCards(host,{catalog:()=>(${JSON.stringify(choices)}),context:()=>({kind:'battery',energy:'mwh',fuel:'',right:'right',currency:'usd'}),changed:()=>{}});widget.setEnabled(true);[...host.querySelectorAll('button')].find(n=>n.textContent==='Add Side').click();
  const field=title=>[...host.querySelectorAll('label')].find(n=>n.firstChild.textContent===title).querySelector('select'),set=(title,value)=>{const n=field(title);n.value=value;n.dispatchEvent(new Event('change',{bubbles:true}));};
  set('Quantity source','discharging_energy');set('Side kind','delivery');set('Quantity source','horizon_right');const kind=field('Side kind');
  return {value:kind.value,options:[...kind.options].map(n=>n.value)};
 })()`);
 assert.deepEqual(result,{value:'',options:['','payment']});
}));
test('thermal Side contract choices follow the selected facility inlet and outlet', {skip:!browserAvailable, timeout:browserTestTimeout},()=>withBrowserPage('<main id="host"></main>',async evaluate=>{
 const values=await evaluate(`(async()=>{
  const {setupOperatingSideCards}=await import('/structured-power-sides-view.js');
  const host=document.querySelector('main');
  const markets=['north','south'].map(curve=>({curveCode:curve,contractId:curve,quantityCode:'gas',currencyCode:'usd',label:curve}));
  const side=setupOperatingSideCards(host,{catalog:()=>({maximumSides:3,quotes:[],priceContracts:markets,
   sideChoices:[{kind:'thermal',code:'guaranteed_fuel',label:'Fuel',quantityKind:'fuel',allowedKinds:['payment']}] }),
   context:()=>({kind:'thermal',fuel:'gas',currency:'usd'}),profile:()=>({inputCurve:'south',outputCurve:'power'}),changed:()=>{}});
  side.setEnabled(true);[...host.querySelectorAll('button')].find(x=>x.textContent==='Add Side').click();
  const field=name=>[...host.querySelectorAll('label')].find(x=>x.firstChild.textContent===name).querySelector('select');
  const source=field('Quantity source');source.value='guaranteed_fuel';source.dispatchEvent(new Event('change',{bubbles:true}));
  return [...field('Market contract').options].map(x=>x.text);
 })()`);
 assert.deepEqual(values,['Choose…','south']);
}));
