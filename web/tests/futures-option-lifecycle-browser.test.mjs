import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage} from './browser-page.mjs';

// A lifecycle request asks its host again whether events may be recorded before it is sent. While that question is open the request is
// being sent, so it is not sent twice; when the question fails, the request is not sent and the status line says why.
test('an exercise whose recording authority cannot be read again is not sent, says why and is sent only once',{skip:!browserAvailable,timeout:30000},async()=>{
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
