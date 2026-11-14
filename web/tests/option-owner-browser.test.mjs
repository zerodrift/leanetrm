import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

test('a late earlier option status settles only its submitting actor and cannot clear another actor request',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {readOptionView}=await import('/futures-option-view.js');let actor='synthetic-a';const replies={},callbacks=[];
  for(const person of ['synthetic-a','synthetic-b'])sessionStorage.setItem('option-lifecycle-request:'+person+':synthetic-trade',JSON.stringify({operationId:person+'-operation',path:'options/exercise',body:{tradeId:'synthetic-trade',lots:'1'},uncertain:true}));
  const api=async(path,body)=>path==='options/holding'?{tradeId:'synthetic-trade',lots:'2',openLots:'2',events:[]}:new Promise(resolve=>replies[actor]=resolve);
  const h={tradeId:'synthetic-trade',component:'option',tradeReference:'T-1',instrumentReference:'I-1',book:'Synthetic book',revision:'1',executedAt:'2026-11-04T00:00:00Z',booking:{tradeDate:'2026-11-04'},currency:'USD'};
  const mount=async()=>{const owner=actor,read=await readOptionView(api,{snapshotId:'synthetic-close'},h,{writes:()=>({enabled:true,reason:null}),actor:()=>actor,recorded:()=>callbacks.push(owner),surface:null});return read.view('UTC').termsContent(document.querySelector('#host'));};
  const tick=()=>new Promise(resolve=>setTimeout(resolve,0)),check=()=>document.querySelector('#option-lifecycle-status').click();
  const disposeA=await mount();check();await tick();disposeA();actor='synthetic-b';const disposeB=await mount();check();await tick();
  const key='option-lifecycle-request:synthetic-b:synthetic-trade',before=sessionStorage.getItem(key);
  replies['synthetic-a']({operationId:'synthetic-a-operation',tradeId:h.tradeId,kind:'exercise'});await tick();const after=sessionStorage.getItem(key);disposeB();await mount();
  const restored=!document.querySelector('#option-lifecycle-status').hidden,observed=[...callbacks];
  replies['synthetic-b']({operationId:'synthetic-b-operation',tradeId:h.tradeId,kind:'exercise'});await tick();return {retained:before!==null&&before===after,restored,callbacks:observed,settledA:sessionStorage.getItem('option-lifecycle-request:synthetic-a:synthetic-trade')===null};
 })()`);
 assert.deepEqual(result,{retained:true,restored:true,callbacks:[],settledA:true});
}));
