import assert from 'node:assert/strict';
import test from 'node:test';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

test('the applicable agreement dialog identifies the confirmation and requires a selection', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<main></main>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {selectBookingAgreement}=await import('/bilateral-desk.js');
  const pending=selectBookingAgreement([{id:'A',name:'Agreement A',revision:1},{id:'B',name:'Agreement B',revision:2}],'sale');
  const dialog=document.querySelector('dialog'),select=dialog.querySelector('select'),ok=dialog.querySelector('button[value=accept]');
  const before={text:dialog.innerText,selected:select.value,blocked:ok.disabled||!select.checkValidity()};
  select.value='B';select.dispatchEvent(new Event('change'));ok.click();return {...before,result:await pending};
 })()`);
 assert.match(result.text,/Sale confirmation/);assert.equal(result.selected,'');assert.equal(result.blocked,true);assert.equal(result.result,'B');
}));

test('the facility desk shows a superseded allocation and opens its exact booked revision',
 {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<main></main>',async evaluate=>{
  const result=await evaluate(`(async()=>{
   const {setupFacilityTrades}=await import('/facility-trades.js');
   const root=document.querySelector('main');let opened;
   const profile={id:'river',name:'River storage',kind:'storage',inputLocation:'Inlet',inputCurve:'Gas index',inputAdjustment:'0',
    outputLocation:'Outlet',outputCurve:'Gas index',outputAdjustment:'0',hourlyRatchets:[],ratchets:[]};
   const widget=setupFacilityTrades(root,{context:()=>({effectiveOn:'2021-01-15'}),busy:()=>false,permission:()=>false,act:fn=>fn(),
    tradeInspection:()=>null,openTrade:async(id,revision)=>{opened=[id,revision];},api:async path=>path==='desk/facilities'?{profiles:[profile],working:true}:{
    totals:[{stage:'Measured inject',unit:'MMBtu',numerator:'90',denominator:'1'}],
    meters:[{day:'2021-01-01',point:'Inlet',unit:'MMBtu',measured:'90',allocated:'92',unallocated:'-2',currentSource:false}],
    trades:[{tradeId:'purchase',reference:32,revision:2,product:'physical_gas',book:'Gas',agreement:'Supply agreement',confirmation:'matched'}]}});
   await widget.load();[...root.querySelectorAll('button')].find(b=>b.textContent==='32').click();
   return {opened,text:root.textContent,canLink:[...root.querySelectorAll('button')].some(b=>b.textContent==='Link trade')};
  })()`);
  assert.deepEqual(result.opened,['purchase',2]);assert.equal(result.canLink,false);
  assert.match(result.text,/Earlier meter revision/);assert.match(result.text,/-2/);assert.match(result.text,/Supply agreement/);
 }));

test('opening a commercial profile reads only its own report or explains its absence', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<main></main>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {setupFacilityTrades}=await import('/facility-trades.js'),root=document.querySelector('main'),reports=[];
  const widget=setupFacilityTrades(root,{context:()=>({effectiveOn:'2030-04-21'}),busy:()=>false,permission:()=>false,
   api:async(path,body)=>path==='desk/facilities'?{profiles:['first','selected'].map(id=>({id,name:id,kind:'storage',inputAdjustment:'0',outputAdjustment:'0'}))}:(reports.push(body.profileId),{totals:[],meters:[],trades:[]})});
  await widget.load('selected');const current=root.querySelector('select').value;
  await widget.load('retired');await widget.load();return {reports,current,missing:root.innerText.includes('unavailable for the selected business date')};
 })()`);
 assert.deepEqual(result,{reports:['selected'],current:'selected',missing:true});
}));

test('the credit officer can record a decision independently of legal document access',
 {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<main id="bilateral-desk"></main>',async evaluate=>{
  const result=await evaluate(`(async()=>{
   const {setupBilateralDesk}=await import('/bilateral-desk.js');
   const root=document.querySelector('main'),party={id:'party',revision:1,name:'Party'};
   const widget=setupBilateralDesk({source:()=>'frame',busy:()=>false,act:fn=>fn(),api:async()=>({
     products:['physical_gas'],entities:[{...party,id:'buyer'}],parties:[party],trades:[],agreements:[],clearances:[],
     canEdit:false,canCredit:true,canDownload:false,canConfirm:false,working:true})});
   await widget.load();const buttons=[...root.querySelectorAll('button')];buttons.find(b=>b.textContent==='Record credit decision').click();
   return {agreement:buttons.some(b=>b.textContent==='New agreement'),fields:[...root.querySelectorAll('form label')].map(l=>l.firstChild.textContent)};
  })()`);
  assert.equal(result.agreement,false);assert.ok(result.fields.includes('Decision reason'));
 }));

test('a revision that carries earlier commercial terms shows their confirmation and records against the revision that set them',
 {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<main id="bilateral-desk"></main>',async evaluate=>{
  const result=await evaluate(`(async()=>{
   const {setupBilateralDesk}=await import('/bilateral-desk.js');
   const root=document.querySelector('main'),party={id:'party',revision:1,name:'Party'},sent=[];
   const matched={revision:1,status:'matched',reference:'CP-7',evidence:'Recorded call',effective_at:'2020-12-02T00:00:00Z'};
   const trade=(revision,carried_from,confirmations)=>({trade_id:'swap',revision,carried_from,product:'financial_swap',agreement_id:'master',
    executed_at:'2020-12-01T00:00:00Z',confirmations});
   const widget=setupBilateralDesk({source:()=>'frame',busy:()=>false,act:fn=>fn(),api:async(path,body)=>path==='desk/directory'?{
     products:['financial_swap'],entities:[{...party,id:'buyer'}],parties:[party],agreements:[],clearances:[],
     trades:[trade(4,null,[]),trade(3,2,[matched]),trade(2,undefined,[matched])],
     canEdit:false,canCredit:false,canDownload:false,canConfirm:true,working:true}:(sent.push(body.record),{revision:2})});
   await widget.load();
   const rows=[...root.querySelectorAll('tbody tr')].map(row=>[...row.cells].map(cell=>cell.textContent));
   const carried=[...root.querySelectorAll('tbody tr')].find(row=>row.cells[0].textContent==='swap / 3');
   carried.querySelector('button').click();
   const form=root.querySelector('form'),heading=form.querySelector('p').textContent;
   form.querySelector('input[type=text]').value='CP-8';form.querySelector('textarea').value='Disputed by letter';
   form.requestSubmit();await new Promise(resolve=>setTimeout(resolve,50));
   return {rows:rows.map(cells=>cells.slice(0,5)),heading,sent};
  })()`);
  assert.deepEqual(result.rows,[
   ['swap / 4','Set by this revision','financial swap','Filed','Awaiting confirmation'],
   ['swap / 3','Carried from revision 2','financial swap','Filed','matched'],
   ['swap / 2','Set by this revision','financial swap','Filed','matched']]);
  assert.equal(result.heading,'swap / revision 2');
  assert.equal(result.sent.length,1);
  assert.deepEqual([result.sent[0].tradeId,result.sent[0].tradeRevision,result.sent[0].expectedRevision],['swap',2,1]);
 }));
