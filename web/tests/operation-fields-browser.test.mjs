import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const browser={skip:!browserAvailable,timeout:browserTestTimeout};
const setup=`(async()=>{
 const {mountOperationFields}=await import('/operation-fields.js');
 window.clock=[];window.pendingClocks=[];window.slow=false;window.current=true;
 const fields=[
  {name:'profile',label:'Physical rule profile',kind:'select',nullable:false,choices:[{id:'step-ratchets',label:'Exact physical step ratchets',value:'storage_total_physical_step_ratchets_v1'}]},
  {name:'port',label:'Facility pipeline port',kind:'reference',nullable:false,members:['portId','referenceCatalogId','referenceRevisionId','pointCode'],choices:[{id:'port-choice',label:'Synthetic pipeline / exact point',value:{portId:'synthetic-port',referenceCatalogId:'synthetic-catalog',referenceRevisionId:'synthetic-reference',pointCode:'synthetic_point'}}]},
  {name:'bands',label:'Ratchet bands',kind:'array',nullable:false,minItems:1,fields:[
   {name:'lower',label:'Lower inventory',kind:'text',nullable:false},{name:'upper',label:'Upper inventory',kind:'text',nullable:false},
   {name:'injection',label:'Injection per elapsed hour',kind:'text',nullable:false}]},
  {name:'at',label:'Opening observation time',kind:'instant',nullable:false}];
 const options={zone:()=> 'UTC',current:()=>current,changed(){},act:async work=>work(),api:async(path,body)=>{clock.push(body);if(slow)await new Promise(resolve=>pendingClocks.push(resolve));return{candidates:[{instant:'2032-03-14T08:00:00Z',offset:'+00:00'}]};}};
 window.editor=mountOperationFields(document.querySelector('#fields'),fields,options);
 window.pick=(name,value)=>{const input=document.querySelector('[name='+name+']');input.value=value;input.dispatchEvent(new Event('change',{bubbles:true}));};
 window.fill=()=>{pick('profile','step-ratchets');pick('port','port-choice');pick('bands','entries');[...document.querySelectorAll('button')].find(button=>button.textContent==='Add entry').click();pick('lower','0');pick('upper','0.333333333333333333');pick('injection','1/3');pick('at','2032-03-14T08:00:00');};
})()`;

test('shared operation fields keep facility ratchets, policy and exact port references explicit',browser,()=>withBrowserPage('<div id="fields"></div>',async evaluate=>{
 await evaluate(setup);
 assert.deepEqual(await evaluate("['profile','port','bands','at'].map(name=>document.querySelector('[name='+name+']').value)"),['','','','']);
 assert.equal(await evaluate("document.querySelector('[name=bands] option[value=empty]')===null"),true);
 await evaluate('fill()');
 assert.deepEqual(await evaluate('editor.read()'),{profile:'storage_total_physical_step_ratchets_v1',portId:'synthetic-port',referenceCatalogId:'synthetic-catalog',referenceRevisionId:'synthetic-reference',pointCode:'synthetic_point',bands:[{lower:'0',upper:'0.333333333333333333',injection:'1/3'}],at:'2032-03-14T08:00:00Z'});
 assert.equal(await evaluate('editor.dirty()'),true);
 assert.match(await evaluate('JSON.stringify(editor.references())'),/Synthetic pipeline/);
}));

test('editing facility fields during clock resolution cannot produce a mixed operation',browser,()=>withBrowserPage('<div id="fields"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("fill();slow=true;window.result=editor.read().then(value=>({value}),error=>({error:error.message}));pick('upper','9');for(const resolve of pendingClocks)resolve()");
 assert.match((await evaluate('result')).error,/changed|current/i);
 assert.equal(await evaluate("document.querySelector('[name=upper]').value"),'9');
}));
