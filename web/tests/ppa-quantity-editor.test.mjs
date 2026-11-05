import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

test('reopening an independently composed fee preserves reversed links and sources through a no-change review',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<section id="side"><p class="ppa-link"></p></section>',async evaluate=>{
  await evaluate(`(async()=>{
   const {ppaQuantityEditor}=await import('/ppa-quantity-editor.js');
   const row=code=>({code,name:code,clock_code:'clock',interval_minutes:'60'});
   const catalog={references:{quantityGroups:{contract:[{...row('mw'),unit_code:'megawatt'}],energy:[{...row('mwh'),unit_code:'megawatt_hour'}]},units:[{code:'megawatt',symbol:'MW'},{code:'megawatt_hour',symbol:'MWh'}],calendars:[row('calendar')]},authoringPolicies:{quantityStages:['generated'],hourMemberships:['all']},definitionsMetadata:{volumeSources:[row('source-a'),row('source-b')],generationAssets:[row('asset-a'),row('asset-b')]},touVersions:[],contextTouVersions:[]};
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

test('a retained TOU stays exact but clearing it requires an explicit new choice',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<section id="side"><p class="ppa-link"></p></section>',async evaluate=>{
  await evaluate(`(async()=>{
   const {ppaQuantityEditor}=await import('/ppa-quantity-editor.js');
   const old={id:'synthetic-product',version:1,code:'synthetic_tou',clock:{code:'clock'}},current={...old,version:2};
   const catalog={references:{quantityGroups:{contract:[{code:'mw',unit_code:'megawatt'}],energy:[{code:'mwh',unit_code:'megawatt_hour'}]},units:[{code:'megawatt',symbol:'MW'},{code:'megawatt_hour',symbol:'MWh'}],calendars:[{code:'calendar'}]},authoringPolicies:{quantityStages:[],hourMemberships:['tou']},definitionsMetadata:{volumeSources:[],generationAssets:[]},touVersions:[old,current],contextTouVersions:[current]};
   window.editor=ppaQuantityEditor(document.querySelector('#side'),catalog,{role:'energy_delivery',quantityKinds:['profile'],linkRoles:[]},'energy',()=>({clock:'clock',cadence:'60'}),()=>[],{kind:'profile',sources:[],stage:null,capacity:'mw',energy:'mwh',hours:{membership:'tou',calendar:'calendar',touProduct:old.id,touVersion:1},slots:[],assets:[]});
  })()`);
  assert.equal(await evaluate('editor.read().hours.touVersion'),1);
  assert.equal(await evaluate("[...document.querySelector('[data-q=tou]').options].find(o=>JSON.parse(o.value||'null')?.[1]===1).disabled"),true);
  await evaluate("document.querySelector('[data-q=tou]').value='';editor.sync()");
  assert.equal(await evaluate("document.querySelector('[data-q=tou]').value"),'');
  assert.match(await evaluate("(()=>{try{editor.read();return 'accepted';}catch(e){return e.message;}})()"),/Complete/);
 });
});
