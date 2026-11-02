import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage} from './browser-page.mjs';

test('reopening an independently composed fee preserves reversed links and sources through a no-change review',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<section id="side"><p class="ppa-link"></p></section>',async evaluate=>{
  await evaluate(`(async()=>{
   const {ppaQuantityEditor}=await import('/ppa-quantity-editor.js');
   const row=code=>({code,name:code,clock_code:'clock',interval_minutes:'60'});
   const catalog={references:{quantityGroups:{contract:[row('mw')],energy:[row('mwh')]},calendars:[row('calendar')]},authoringPolicies:{quantityStages:['generated'],hourMemberships:['all']},definitionsMetadata:{volumeSources:[row('source-a'),row('source-b')],generationAssets:[row('asset-a'),row('asset-b')]},touVersions:[]};
   window.links=[];window.initial={kind:'slots',slots:[{role:'energy_delivery',ordinal:1},{role:'energy_delivery',ordinal:0}],sources:[],stage:null,capacity:null,energy:null,hours:null,assets:[]};
   window.editor=ppaQuantityEditor(document.querySelector('#side'),catalog,{role:'fixed_charge',quantityKinds:['volume','profile','accredited','slots'],linkRoles:['energy_delivery']},'fee',()=>({clock:'clock',cadence:'60'}),()=>links,initial);
   links=[{role:'energy_delivery',ordinal:0},{role:'energy_delivery',ordinal:1}];editor.sync();
   window.read=()=>editor.read();window.catalog=catalog;
  })()`);
  assert.deepEqual(await evaluate('read()'),{kind:'slots',sources:[],stage:null,capacity:null,energy:null,hours:null,slots:[{role:'energy_delivery',ordinal:1},{role:'energy_delivery',ordinal:0}],assets:[]});
  await evaluate(`(async()=>{const {ppaQuantityEditor}=await import('/ppa-quantity-editor.js');document.querySelector('#side').innerHTML='<p class="ppa-link"></p>';window.editor=ppaQuantityEditor(document.querySelector('#side'),catalog,{role:'energy_delivery',quantityKinds:['volume','profile'],linkRoles:[]},'energy',()=>({clock:'clock',cadence:'60'}),()=>[],{...initial,kind:'volume',slots:[],sources:['source-b','source-a'],stage:'generated'});})()`);
  assert.deepEqual(await evaluate('read().sources'),['source-b','source-a']);
 });
});
