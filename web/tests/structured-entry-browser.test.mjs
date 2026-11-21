import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const page='<meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/app.css"><main><section id="gas-storage-page"></section><section id="heat-rate-call-page"></section></main>';
const settings={skip:!browserAvailable,timeout:browserTestTimeout};
const profile={id:'cavern',name:'North cavern',code:'NORTH',kind:'storage',facilityId:'site',version:7,revision:2,clock:'UTC',dayStartMinute:360,
 minimum:'10',maximum:'1000',quantity:'gas',inputLocation:'North receipt',outputLocation:'South delivery',inputCurve:'supply',outputCurve:'delivered',inputAdjustment:'-0.125',outputAdjustment:'0.05',
 ratchets:[{lower:'10',upper:'1000',maximumDailyInjection:'120',maximumDailyWithdrawal:'180'}]};
const gas=(code,name)=>({code,name,quote:'gas-quote',quantity:'gas',commodity:'gas',currency:'USD',priceUnit:'USD/MMBtu',quantityUnit:'MMBtu',contracts:[]});
const party={id:'party',name:'Synthetic purchaser',code:'BUY',revision:2};
const base={definitionRevisionId:'definitions',referenceCatalogId:'references',referenceRevisionId:'published',bookingActor:party,books:[{id:'book',name:'Gas book',portfolio:'Desk'}],legalEntities:[party],counterparties:[party],canBook:true,clocks:['UTC']};
const storage={...base,curves:[gas('supply','Supply'),gas('delivered','Delivered')],facilityProfiles:[profile],maximumRatchets:8,maximumDays:366,
 payment:{frequency:'monthly',rule:'days_after_period_end',dayBasis:'calendar_days',adjustment:'unadjusted',implementation:'payment_v1',maximumDays:31},
 policies:{ratchetBases:['bands'],flowPolicies:['daily'],fuelBases:['in_kind'],planSearches:[]}};
const thermal={...profile,id:'thermal',name:'North generator',kind:'thermal',inputLocation:'Fuel gate',outputLocation:'East bus',inputCurve:'supply',outputCurve:'electricity',ratchets:null};
const call={...base,facilityProfiles:[thermal],curves:[gas('supply','Fuel index'),{...gas('electricity','Power index'),commodity:'power',quantityUnit:'MWh',priceUnit:'USD/MWh'}],
 capacityQuantities:[{code:'power-capacity',commodity:'power',unit:'MW'}],blocks:[],calendars:[],intervalMinutes:[60],memberships:['all'],
 paymentOptions:{frequencies:[],rules:[],dayBases:[],shortMonths:[],adjustments:[]},policies:{models:[],dayCounts:[],exerciseStyles:[],exerciseRules:[]}};
const setup=(module,setupName,catalog)=>`const {${setupName}}=await import('/${module}.js');
 window.sent=[];window.openedProfiles=[];window.widget=${setupName}({api:async(path,body)=>{if(path.endsWith('/catalog'))return ${JSON.stringify(catalog)};sent.push(body);return {tradeId:'saved',operationId:body.operationId,revision:1};},
 source:()=>({snapshotId:'cut'}),actor:()=>'synthetic-trader',historical:()=>false,facilityActivity:{available:()=>true,open:id=>openedProfiles.push(id)},onBooked:async()=>{}});
 await widget.begin();
 window.set=(id,value)=>{const c=document.getElementById(id);c.value=value;c.dispatchEvent(new Event('change',{bubbles:true}));};
 window.stage=id=>document.getElementById(id).closest('section')?.querySelector('h3')?.textContent;
`;

test('a selected storage profile puts each booked curve and basis at its own point, with editable contract ratchets',settings,()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${setup('gas-storage-ticket','setupGasStorageTicket',storage)}set('storage-facilityProfileId','cavern');})()`);
 assert.deepEqual(await evaluate(`[stage('storage-injectionCurve'),stage('storage-maximumInventory'),stage('storage-withdrawalCurve')]`),['Injection','Storage entitlement','Withdrawal']);
 assert.deepEqual(await evaluate(`['storage-injectionCurve','storage-withdrawalCurve'].map(id=>{const field=document.getElementById(id);return [field.value,field.disabled,field.closest('section').querySelector('dl')?.textContent];})`),
  [['supply',true,'Pricing pointNorth receiptLocation adjustment-0.125 USD/MMBtu'],['delivered',true,'Pricing pointSouth deliveryLocation adjustment0.05 USD/MMBtu']]);
 assert.match(await evaluate("document.querySelector('label[for=storage-maximumInventory]').textContent"),/MMBtu/);
 assert.equal(await evaluate("document.querySelector('#storage-ratchet-rows [data-cell=injection]').value"),'120');
 await evaluate("[...document.querySelectorAll('button')].find(b=>b.textContent==='View trades and volumes')?.click()");
 assert.deepEqual(await evaluate("openedProfiles"),['cavern']);
 await evaluate(`set('storage-maximumInventory','950');set('storage-openingInventory','30');set('storage-terminalMinimum','10');set('storage-terminalMaximum','950');
  for(const [k,v] of Object.entries({ratchetBasis:'bands',flowPolicy:'daily',fuelBasis:'in_kind',injectionFuel:'0',withdrawalFuel:'0',injectionCharge:'0.03',withdrawalCharge:'0',monthlyCapacityCharge:'0',paymentDays:'15',termFrom:'2031-01-01',termTo:'2031-02-01',book:'book',legalEntity:JSON.stringify(['party',2]),counterparty:JSON.stringify(['party',2]),reason:'Synthetic capacity right'}))set('storage-'+k,v);
  document.querySelector('#storage-book-submit').click();`);
 const terms=await evaluate("sent[0]?.terms");
 assert.deepEqual([terms.facilityProfileId,terms.injectionCurve,terms.withdrawalCurve,terms.maximumInventory,terms.ratchets[0].upper],['cavern','supply','delivered','950','950']);
 await evaluate("set('storage-facilityProfileId','')");
 assert.equal(await evaluate("document.getElementById('gas-storage-page').innerText.includes('North receipt')"),false);
}));

test('a cash-settled call shows plant references immediately and also supports independent curve selection',settings,()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${setup('heat-rate-call-ticket','setupHeatRateCallTicket',call)}})()`);
 assert.deepEqual(await evaluate("[stage('toll-powerCurve'),stage('toll-capacity'),stage('toll-fuelCurve')]"),['Power reference','Cash-settled option','Fuel reference']);
 assert.equal(await evaluate("document.getElementById('toll-fuelCurve').value"),'');
 await evaluate("set('toll-facilityProfileId','thermal')");
 const result=await evaluate(`({power:document.getElementById('toll-powerCurve').value,fuel:document.getElementById('toll-fuelCurve').value,
 locked:document.getElementById('toll-fuelCurve').disabled,capacity:[...document.getElementById('toll-capacityQuantity').options].map(o=>o.value),
 rate:document.querySelector('label[for=toll-heatRate]').textContent,text:document.getElementById('heat-rate-call-page').innerText})`);
 assert.deepEqual([result.power,result.fuel,result.locked,result.capacity],['electricity','supply',true,['','power-capacity']]);
 assert.match(result.rate,/MMBtu\/MWh/);assert.match(result.text,/East bus/);assert.match(result.text,/Fuel gate/);
 await evaluate("set('toll-facilityProfileId','');set('toll-powerCurve','');set('toll-powerCurve','electricity');set('toll-fuelCurve','supply')");
 assert.deepEqual(await evaluate("[document.getElementById('toll-fuelCurve').disabled,document.getElementById('toll-fuelCurve').value,document.getElementById('heat-rate-call-page').innerText.includes('Fuel gate')]"),[false,'supply',false]);
}));

test('the entry flow fits its panel and keeps the same field and focus while resizing',settings,()=>withBrowserPage(page,async(evaluate,command)=>{
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await evaluate(`(async()=>{${setup('gas-storage-ticket','setupGasStorageTicket',storage)}set('storage-facilityProfileId','cavern');document.getElementById('storage-maximumInventory').focus()})()`);
 const positions=()=>evaluate(`['storage-injectionCurve','storage-maximumInventory','storage-withdrawalCurve'].map(id=>{const r=document.getElementById(id).closest('section').getBoundingClientRect();return [r.left,r.top];})`);
 const wide=await positions();assert.ok(wide[0][0]<wide[1][0]&&wide[1][0]<wide[2][0]);assert.equal(wide[0][1],wide[2][1]);
 await evaluate("document.querySelector('main').style.width='340px';document.body.offsetWidth");
 const narrow=await positions();assert.equal(narrow[0][0],narrow[2][0]);assert.ok(narrow[0][1]<narrow[1][1]&&narrow[1][1]<narrow[2][1]);
 assert.deepEqual(await evaluate(`[document.activeElement.id,document.activeElement.value,document.getElementById('gas-storage-page').scrollWidth<=document.getElementById('gas-storage-page').clientWidth+1]`),['storage-maximumInventory','1000',true]);
}));


for(const [type,module,setupName,catalog,storePrefix,bookPath] of [
 ['storage','gas-storage-ticket','setupGasStorageTicket',storage,'gas-storage-ticket-request:','gas-storage/book'],
 ['toll','heat-rate-call-ticket','setupHeatRateCallTicket',call,'heat-rate-call-ticket-request:','heat-rate-calls/book']
]){
 test(type+' keeps delayed catalogs and booking receipts with the actor who requested them',settings,()=>withBrowserPage(page,async evaluate=>{
  const result=await evaluate(`(async()=>{
   const {${setupName}}=await import('/${module}.js'),catalog=${JSON.stringify(catalog)};
   let owner='A',lateCatalog,lateReceipt,last,opened=[];
   const wait=()=>new Promise(resolve=>setTimeout(resolve,0));
   const key=who=>${JSON.stringify(storePrefix)}+who;
   for(const who of ['A','B'])sessionStorage.setItem(key(who),JSON.stringify({operationId:who,path:${JSON.stringify(bookPath)},body:{},uncertain:true}));
   const widget=${setupName}({actor:()=>owner,historical:()=>false,source:()=>({snapshotId:'cut'}),onBooked:async id=>opened.push(id),
    api:async path=>{if(path.endsWith('/status'))return new Promise(resolve=>lateReceipt=resolve);
     if(owner==='A'&&!lateCatalog)return new Promise(resolve=>lateCatalog=resolve);
     return catalog;}});
   last=widget.begin();widget.reset();owner='B';await widget.begin();
   lateCatalog({...catalog,facilityProfiles:[{...catalog.facilityProfiles[0],name:'Private A'}]});await last;
   const picker=document.getElementById('${type}-facilityProfileId'),stale=picker.textContent.includes('Private A');
   widget.reset();owner='A';await widget.begin();document.getElementById('${type}-check').click();await wait();
   widget.reset();owner='B';await widget.begin();lateReceipt({operationId:'A',tradeId:'A-trade',revision:1});await wait();
   const result={stale,opened,pending:widget.pending(),remaining:JSON.parse(sessionStorage.getItem(key('B')))?.operationId,cleared:sessionStorage.getItem(key('A'))===null};
   document.getElementById('${type}-check').click();await wait();await widget.begin();
   lateReceipt({operationId:'B',tradeId:'B-trade',revision:1});await wait();
   return {...result,reopened:document.getElementById('${type}-booking-status').textContent,finished:!widget.pending()};
  })()`);
  assert.deepEqual(result,{stale:false,opened:[],pending:true,remaining:'B',cleared:true,reopened:'Original booking confirmed.',finished:true});
 }));
}
