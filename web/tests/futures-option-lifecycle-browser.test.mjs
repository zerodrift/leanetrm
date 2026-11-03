import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

test('an open option ticket refreshes its clock without changing edits or an uncertain booking',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<div id="host"><div id="option-page"></div></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupOptionTicket}=await import('/futures-option-view.js'),{displayInstant}=await import('/market-view.js');
   const party={id:'synthetic-party',revision:1,code:'synthetic',name:'Synthetic party'};
   const series={series:'synthetic_call',product:'synthetic_option',name:'Synthetic option',exchange:{code:'synthetic_exchange',name:'Synthetic exchange'},
    commodity:{code:'gas',name:'Gas'},underlyingSeries:'synthetic_future',right:'call',strike:'3',expiryAt:'2026-12-01T20:30:00Z',
    settlementPrice:'0.2',currency:'USD',multiplier:'1000',premiumTick:'0.01',minimumPremium:'0',maximumPremium:'100',exerciseStyle:'American',contraryInstructionsAllowed:false};
   const catalog={options:[series],underlyings:[{series:'synthetic_future',deliveryFrom:'2026-12-01',deliveryTo:'2027-01-01'}],
    books:[{id:'synthetic-book',name:'Synthetic book',portfolio:'Synthetic portfolio'}],bookingActor:party,legalEntities:[party],counterparties:[party],canBook:true};
   window.optionZone='UTC';window.optionSent=[];
   const api=async(path,body)=>{if(path==='options/catalog')return catalog;optionSent.push(structuredClone(body));throw new TypeError('Synthetic lost response');};
   window.ticket=setupOptionTicket({api,source:()=>({snapshotId:'synthetic-close'}),historical:()=>false,actor:()=>'synthetic-trader',
    instant:at=>displayInstant(at,optionZone),zone:()=>optionZone,onBooked:async()=>{}});
   await ticket.begin();
   for(const [id,value] of [['exchange','synthetic_exchange'],['commodity','gas'],['underlying','synthetic_future'],['right','call'],['strike','3']]){
    const control=document.getElementById('option-'+id);control.value=value;control.dispatchEvent(new Event('change',{bubbles:true}));
   }
   for(const id of ['book','legal-entity','counterparty'])document.getElementById('option-'+id).selectedIndex=1;
   for(const [id,value] of [['side','buy'],['lots','2'],['premium','0.2'],['premium-date','2026-11-02'],['premium-style','premium_paid_up_front'],['memo','Synthetic clock check']])
    document.getElementById('option-'+id).value=value;
   window.optionExpiry=()=>[...document.querySelectorAll('#option-page .option-fact')].find(row=>row.querySelector('.option-fact-label').textContent==='Expiry').querySelector('.option-fact-value').textContent;
   window.optionBefore=optionExpiry();optionZone='America/Chicago';ticket.sync();
  })()`);
  assert.equal(await evaluate('optionBefore'),'Dec 1, 2026, 8:30 PM');
  assert.equal(await evaluate('optionExpiry()'),'Dec 1, 2026, 2:30 PM');
  assert.equal(await evaluate("document.querySelector('label[for=option-executed]').textContent"),'Execution time · America/Chicago');
  assert.equal(await evaluate("document.querySelector('#option-lots').value"),'2');
  assert.equal(await evaluate("document.querySelector('#option-premium-date').value"),'2026-11-02');
  assert.equal(await evaluate("document.querySelector('#option-memo').value"),'Synthetic clock check');
  await evaluate("document.querySelector('#option-page form').requestSubmit();new Promise(resolve=>setTimeout(resolve,20))");
  assert.equal(await evaluate('optionSent.length'),1);
  assert.equal(await evaluate("document.querySelector('#option-page fieldset').disabled"),true);
  const retained=await evaluate('Object.fromEntries(Object.entries(sessionStorage))');
  assert.equal(Object.keys(retained).length,1);
  const command=JSON.parse(Object.values(retained)[0]);
  assert.equal(command.path,'options/book');
  assert.equal(command.body.snapshotId,'synthetic-close');
  assert.equal(command.body.lots,'2');
  assert.equal(command.body.premiumPaymentDate,'2026-11-02');
  await evaluate("optionZone='UTC';ticket.sync()");
  assert.equal(await evaluate('optionExpiry()'),'Dec 1, 2026, 8:30 PM');
  assert.equal(await evaluate("document.querySelector('label[for=option-executed]').textContent"),'Execution time · UTC');
  assert.equal(await evaluate('optionSent.length'),1);
  assert.deepEqual(await evaluate('Object.fromEntries(Object.entries(sessionStorage))'),retained);
  assert.equal(await evaluate("document.querySelector('#option-page fieldset').disabled"),true);
  assert.equal(await evaluate("document.querySelector('#option-retry').hidden"),false);
 });
});

// A lifecycle request asks its host again whether events may be recorded before it is sent. While that question is open the request is
// being sent, so it is not sent twice; when the question fails, the request is not sent and the status line says why.
test('an exercise whose recording authority cannot be read again is not sent, says why and is sent only once',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<div id="host"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {readOptionView}=await import('/futures-option-view.js');
   window.sent=[];window.asked=0;
   const api=async(path,body)=>{if(path==='options/holding')return {tradeId:'t1',lots:'2',openLots:'2',events:[]};sent.push(path);return {};};
   let refuse;
   const writes=()=>{asked++;return asked===1?{enabled:true,reason:null}:new Promise((_,reject)=>{refuse=()=>reject(Error('Your permissions could not be read.'));});};
   const allowed={allowed:true,reason:null},not={allowed:false,reason:'Not here.'};
   const h={tradeId:'t1',component:'option',tradeReference:'T-1',instrumentReference:'I-1',book:'Book A',revision:'1',executedAt:'2026-10-16T18:00:00Z',
    booking:{tradeDate:'2026-10-16'},currency:'USD',closableLots:'2',actions:{exercise:allowed,assignment:not,instruction:not}};
   const read=await readOptionView(api,{snapshotId:'s1'},h,{writes,actor:()=>'trader',recorded:()=>{}});
   read.view('UTC').schedules(document.querySelector('#host'));
   document.querySelector('#option-exercise-lots').value='1';
   document.querySelector('#option-exercise').click();
   await new Promise(r=>setTimeout(r,20));
   window.during={asked,exercise:document.querySelector('#option-exercise').disabled,retry:document.querySelector('#option-lifecycle-retry').disabled};
   document.querySelector('#option-lifecycle-retry').click();
   await new Promise(r=>setTimeout(r,20));
   window.twice=asked;
   refuse();
   await new Promise(r=>setTimeout(r,20));
  })()`);
  assert.deepEqual(await evaluate('during'),{asked:2,exercise:true,retry:true});
  assert.equal(await evaluate('twice'),2);
  assert.deepEqual(await evaluate('sent'),[]);
  assert.match(await evaluate("document.querySelector('.option-lifecycle [role=status]').textContent"),/^Not sent: Your permissions could not be read\./);
  assert.equal(await evaluate("document.querySelector('#option-lifecycle-retry').disabled"),false);
 });
});
