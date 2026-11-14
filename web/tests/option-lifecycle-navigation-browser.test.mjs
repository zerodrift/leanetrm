import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

test('option details open the policy-bound lifecycle workspace and offer no legacy event writes',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {readOptionView}=await import('/futures-option-view.js');const sent=[];let opened=0;
  const allowed={allowed:true,reason:null},h={tradeId:'synthetic-trade',component:'option',tradeReference:'TR-1',instrumentReference:'IN-1',book:'Synthetic book',revision:'1',executedAt:'2026-11-02T15:00:00Z',booking:{tradeDate:'2026-11-02'},currency:'USD',actions:{exercise:allowed,assignment:allowed,instruction:allowed}};
  const read=await readOptionView(async(path)=>{sent.push(path);return {tradeId:h.tradeId,lots:'2',openLots:'2',events:[]};},{snapshotId:'synthetic-frame'},h,{writes:()=>({enabled:true,reason:null}),actor:()=>'synthetic-actor',recorded(){},surface:null,lifecycle(){opened++;}});
  read.view('America/Chicago').termsContent(document.querySelector('#host'));
  document.querySelector('#option-open-lifecycle')?.click();
  return {opened,sent,legacy:!!document.querySelector('#option-exercise,#option-assign,#option-abandon,#option-instruct-exercise'),text:document.querySelector('#host').textContent};
 })()`);
 assert.equal(result.opened,1);assert.equal(result.legacy,false);assert.deepEqual(result.sent,['options/holding']);assert.match(result.text,/saved action policies/i);
}));
