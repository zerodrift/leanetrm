import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

async function fixture(work,amend=false,lateForecast=false){
 await withBrowserPage('<div id="host"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupTicket}=await import('/ticket-controller.js');
   const field=(id,type='choice',dependsOn=[],required=true)=>({id,label:id,meaning:'',type,unit:null,state:'editable',required,band:null,choices:type==='choice'?id:null,dependsOn,lookup:null,soon:null,rule:null});
   const description={product:'synthetic_choices',version:1,rowShape:'flow',tradeFields:[],sideLimits:[],levels:['day'],modes:['new','booked','amend'],tabs:[],tradeColumns:[],
    sides:[{kind:'physical',pill:'Physical',colour:'gas',min:1,initial:1,max:1,soon:null,columns:[{id:'quantity',label:'Quantity',unit:'quantity',measure:'quantity',input:[]}],fields:[field('end','date'),{...field('quantity','decimal'),lookup:'entry_quantity'},field('pipeline'),field('zone','choice',['pipeline']),field('point','choice',['zone']),field('forecast','choice',['end','point'],false)]}]};
   const draft={product:description.product,version:1,trade:{},sides:[{kind:'physical',code:'physical-1',values:{end:'2041-03-01',pipeline:'A',zone:'ZA',point:'PA',forecast:'CURVE'}}]};
   if(${lateForecast}){delete draft.sides[0].values.forecast;draft.sides[0].values.quantity='100';}
   const cells={'physical-1.quantity':{value:'100',sources:[]}};
   const preview={schedule:{levels:['day'],valuationAt:'2041-01-01T00:00:00Z',deliveredLeaves:0,leaves:1,delivered:null,remaining:null,totals:cells,rows:[{key:'2041-02-01',parent:null,level:'day',start:'',end:'',operatingDate:null,cells,delivered:false}]},quantityUnit:'MMBtu',summaries:[],links:[],scheduleRefusal:null};
   window.calls=[];window.held=[];window.hold=false;window.holdForecast=${lateForecast};window.releaseForecast=null;window.holdPreview=false;window.releasePreview=null;
   const api=async(path,body)=>{
    calls.push({path,body});
    if(path==='tickets/description')return description;
    if(path==='tickets/trade')return {...preview,draft,definitionRevisionId:'definition',actions:{amend:{allowed:true,reason:null},closeOut:{allowed:false,reason:'No'},record:{allowed:false,reason:'No'}}};
    if(path==='tickets/preview'||path==='tickets/amend-preview')return window.holdPreview?new Promise(resolve=>window.releasePreview=()=>resolve(preview)):preview;
    if(path==='tickets/amend')return {operationId:body.operationId,tradeId:'trade',instrumentId:'replacement',revision:2};
    if(path==='tickets/lookup')return {value:'100',source:{kind:'suggestion',label:'Suggested quantity'}};
    if(path==='tickets/choices'){
     const v=body.draft.sides[0].values;
     const values=body.field==='pipeline'?['A','B']:body.field==='zone'?[v.pipeline==='B'?'ZB':'ZA']:body.field==='point'?[v.zone==='ZB'?'PB':'PA']:${lateForecast}?['assigned']:['CURVE','OTHER'];
     const result={choices:values.map(value=>({value,label:value})),startingValue:values.length===1?{value:values[0],source:{kind:'suggestion',label:'Only eligible choice'}}:null};
     if(window.holdForecast&&body.field==='forecast')return new Promise(resolve=>window.releaseForecast=()=>resolve(result));
     if(window.hold&&body.field==='zone')return new Promise(resolve=>held.push(()=>resolve(result)));
     return result;
    }
    throw Error('Unexpected '+path);
   };
   window.until=async predicate=>{for(let i=0;i<400&&!predicate();i++)await new Promise(r=>setTimeout(r,10));if(!predicate())throw Error('Choice cascade did not settle');};
   window.value=id=>document.querySelector('[data-field='+id+'] .ticket-control')?.value;
   window.edit=(id,value)=>{const input=document.querySelector('[data-field='+id+'] .ticket-control');input.value=value;input.onchange();};
   window.ticket=setupTicket(document.querySelector('#host'),{api,context:()=>({snapshotId:'frame'}),product:description.product,instant:x=>x,zone:()=>'UTC',actor:()=>'actor',booked(){},
    ...${amend}?{amending:{tradeId:'trade',bindingId:'binding',revision:1}}:{}});
   await until(()=>!!document.querySelector('.deal-ticket'));
  })()`);
  await work(evaluate);
 });
}

test('an amendment term extension retains a still-eligible forecast and location selections',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{await until(()=>calls.some(c=>c.path==='tickets/choices'&&c.body.field==='forecast'));edit('end','2041-06-01');await until(()=>calls.filter(c=>c.path==='tickets/choices'&&c.body.field==='forecast').length>=2);await new Promise(r=>setTimeout(r,50));})()");
  assert.deepEqual(await evaluate("['pipeline','zone','point','forecast'].map(value)"),['A','ZA','PA','CURVE']);
 },true);
});

test('a late forecast suggestion marks the amendment preview updating before its replacement arrives',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{
   await until(()=>typeof releaseForecast==='function');
   const reason=document.querySelector('[data-role=amendment-reason]');reason.value='Synthetic choice correction';reason.dispatchEvent(new Event('input'));
   await until(()=>calls.some(c=>c.path==='tickets/amend-preview')&&document.querySelector('.ticket-status')?.dataset.kind==='current');
   window.holdPreview=true;
   // Capture the first rendered changed draft, independently of the preview debounce duration.
   window.changedChoiceStatus=null;
   const observer=new MutationObserver(()=>{if(value('forecast')==='assigned'){observer.disconnect();window.changedChoiceStatus=document.querySelector('.ticket-status').dataset.kind;}});
   observer.observe(document.querySelector('#host'),{childList:true,subtree:true,characterData:true});
   releaseForecast();await until(()=>changedChoiceStatus!==null);
  })()`);
  assert.equal(await evaluate('changedChoiceStatus'),'updating','the old successful preview must stop claiming to match changed terms');
  await evaluate(`(async()=>{await until(()=>typeof releasePreview==='function');document.querySelector('.ticket-book').click();})()`);
  assert.equal(await evaluate("calls.filter(c=>c.path==='tickets/amend').length"),0,'saving cannot reuse the preview from before the choice changed');
  await evaluate(`(async()=>{releasePreview();await until(()=>document.querySelector('.ticket-status')?.dataset.kind==='current');document.querySelector('.ticket-book').click();await until(()=>calls.some(c=>c.path==='tickets/amend'));})()`);
  assert.equal(await evaluate("calls.filter(c=>c.path==='tickets/amend').length"),1);
  assert.equal(await evaluate("calls.find(c=>c.path==='tickets/amend').body.draft.sides[0].values.forecast"),'assigned');
  assert.equal(await evaluate("calls.find(c=>c.path==='tickets/amend').body.reason"),'Synthetic choice correction');
 },true,true);
});

test('changing a parent refills its sole valid children through the entire dependency chain',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{edit('pipeline','A');await until(()=>calls.some(c=>c.path==='tickets/choices'&&c.body.field==='zone'));await new Promise(r=>setTimeout(r,50));})()");
  assert.deepEqual(await evaluate("['pipeline','zone','point'].map(value)"),['A','ZA','PA']);
  await evaluate("(async()=>{edit('pipeline','B');await new Promise(r=>setTimeout(r,150));})()");
  assert.deepEqual(await evaluate("['pipeline','zone','point'].map(value)"),['B','ZB','PB']);
 });
});

test('a stale child response cannot replace the selection under a newer parent',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{hold=true;edit('pipeline','A');await until(()=>held.length===1);edit('pipeline','B');held.shift()();await until(()=>held.length===1);hold=false;held.shift()();await new Promise(r=>setTimeout(r,100));})()");
  assert.deepEqual(await evaluate("['pipeline','zone','point'].map(value)"),['B','ZB','PB']);
 });
});

test('an explicit empty choice survives later parent edits and singleton suggestions',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{edit('pipeline','A');await until(()=>value('zone')==='ZA');edit('zone','');edit('pipeline','B');await new Promise(r=>setTimeout(r,150));})()");
  assert.equal(await evaluate("value('zone')"),'');
 });
});

test('an explicitly cleared editable lookup stays empty through later lookup refreshes',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{edit('end','2041-03-01');await until(()=>value('quantity')==='100');edit('quantity','');edit('end','2041-04-01');await new Promise(r=>setTimeout(r,900));})()");
  assert.equal(await evaluate("value('quantity')"),'');
  assert.equal(await evaluate("calls.filter(c=>c.path==='tickets/lookup'&&!c.body.draft.sides[0].values.quantity).length"),1);
 });
});
