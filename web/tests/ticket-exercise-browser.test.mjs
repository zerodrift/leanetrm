import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

for(const failure of ['write','removal'])test(`election recovery survives a ticket remount after a storage ${failure} failure`,
 {skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<div id="host"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupTicket}=await import('/ticket-controller.js');
   const storage=Storage.prototype,blocked=${JSON.stringify(failure)}==='write'?'setItem':'removeItem',original=storage[blocked];
   window.failures=0;
   storage[blocked]=function(key,...args){
    if(key.startsWith('deal-ticket-exercise:')&&!key.endsWith(':read-references')){
     failures++;throw new DOMException('Synthetic storage refusal','QuotaExceededError');
    }
    return original.call(this,key,...args);
   };
   const host=document.querySelector('#host'),product='physical_gas';
   const scope={actor:'actor-a',tradeId:'trade-a',bindingId:'binding-a',revision:1,snapshotId:'frame-a'};
   let ticket;
   window.writes=[];window.prepares=[];
   const description={product,version:1,rowShape:'flow',tradeFields:[],sideLimits:[],levels:['day'],modes:['booked'],tabs:[],tradeColumns:[],sides:[]};
   const until=async test=>{for(let i=0;i<300&&!test();i++)await new Promise(r=>setTimeout(r,10));if(!test())throw Error('Ticket condition not ready');};
   const find=selector=>document.querySelector(selector);
   const retry='.ticket-retry-exercise',record='.ticket-record-exercise';
   const controls=()=>({record:!find(record).disabled,retry:!!find(retry)});
   const mount=async(patch={})=>{
    ticket?.destroy();
    const {actor,snapshotId,...opened}={...scope,...patch};
    const context={snapshotId,knowledgeMode:'current'};
    const action={allowed:true,label:'Record exercise',preparePath:'/api/gas/exercise/prepare',recordPath:'/api/gas/exercise/record',
     tradeId:opened.tradeId,instrumentId:opened.bindingId,expectedRevision:opened.revision,readContextId:'retained-exercise-cutoff',
     periods:[{right:0,from:'2041-02-01',to:'2041-02-02',exerciseAt:'2041-01-31T16:00:00Z',previousId:null,previousOutcome:null}],
     outcomes:[{value:'exercised',label:'Exercised'}]};
    const api=async(path,body)=>{
     if(path==='tickets/description')return description;
     if(path==='tickets/trade')return {draft:{product,version:1,trade:{},sides:[]},schedule:null,summaries:null,
      groups:[{right:0,from:'2041-02-01',to:'2041-02-02',state:'exercised',forwardSum:'2000',counted:'2000',inputs:null}],currency:'EUR',
      scheduleRefusal:{code:'schedule_view',message:'The sides keep different gas-day clocks.'},quantityUnit:'MMBtu',links:[],context:{contextId:action.readContextId},
      actions:{amend:{allowed:false},closeOut:{allowed:false},record:{allowed:false},recordExercise:action}};
     if(path==='gas/exercise/prepare'){
      prepares.push(body);
      const {context,tradeId,instrumentId,expectedRevision,operationId,right,firstPeriod,outcome,effectiveAt,evidence}=body;
      return {command:{context,tradeId,instrumentId,expectedRevision,operationId,
       elections:[{right,fromDate:firstPeriod,outcome,effectiveAt,evidence,expectedPreviousId:null}]}};
     }
     if(path==='gas/exercise/record'){
      writes.push(body);
      if(writes.length===1)throw Error('Lost reply');
      return {tradeId:body.tradeId,instrumentId:body.instrumentId,operationId:body.operationId,elections:[{right:0,fromDate:'2041-02-01',revisionId:'written-revision'}]};
     }
     throw Error('Unexpected '+path);
    };
    ticket=setupTicket(host,{api,context:()=>context,product,instant:at=>at,zone:()=>'UTC',actor:()=>actor,opened,exerciseWritable:true});
    await until(()=>!!find(record));
    return controls();
   };
   window.mount=mount;
   await mount();find(record).click();
   const form=find('.ticket-exercise-action'),fill=(name,value)=>{const control=form.elements.namedItem(name);control.value=value;control.dispatchEvent(new Event('change'));};
   for(const [name,value] of Object.entries({right:'0',firstPeriod:'2041-02-01',lastPeriod:'2041-02-01',outcome:'exercised',
    effectiveAt:'2041-01-01T16:00:00Z',evidence:'Synthetic retained election'}))fill(name,value);
   form.requestSubmit();await until(()=>!!find(retry));
   window.reconcile=async()=>{find(retry).click();await until(()=>writes.length===2&&controls().record);};
  })()`);
  assert.match(await evaluate("document.querySelector('.ticket-exercise-groups')?.textContent??''"),/Gross counted \(EUR\).*2,000/);
  const ready={record:true,retry:false};
  for(const scope of [{actor:'actor-b'},{tradeId:'trade-b'},{bindingId:'binding-b'},{revision:2},{snapshotId:'frame-b'}])
   assert.deepEqual(await evaluate(`mount(${JSON.stringify(scope)})`),ready);
  assert.deepEqual(await evaluate('mount()'),{record:false,retry:true});
  await evaluate('reconcile()');
  assert.deepEqual(await evaluate('writes[1]'),await evaluate('writes[0]'));
  assert.equal(await evaluate('prepares.length'),1);
  assert.ok(await evaluate('failures>0'));
  if(failure==='removal'){
   const stored=await evaluate("JSON.parse(sessionStorage.getItem(Object.keys(sessionStorage).find(key=>key.startsWith('deal-ticket-exercise:'))))");
   assert.deepEqual(stored,await evaluate('writes[0]'),'storage still contains the original command when its removal fails');
  }
  assert.deepEqual(await evaluate('mount()'),ready);
 });
});

test('an explicit reread clears a stale exercise message and a recorded reread keeps its receipt status',
 {skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<div id="host"></div>',async evaluate=>{
  const statuses=await evaluate(`(async()=>{
   const {setupExerciseAction}=await import('/ticket-exercise-action.js');
   const context={snapshotId:'frame',knowledgeMode:'current'},day='2041-02-01';
   const action={allowed:true,label:'Record exercise',preparePath:'/api/gas/exercise/prepare',recordPath:'/api/gas/exercise/record',
    tradeId:'trade',instrumentId:'binding',expectedRevision:1,readContextId:'read',
    periods:[{right:0,from:day,to:'2041-02-02',exerciseAt:'2041-01-31T16:00:00Z'}],outcomes:[{value:'exercised',label:'Exercised'}]};
   let stale=true;
   const controller=setupExerciseAction({context:()=>context,current:()=>true,signal:new AbortController().signal,owner:'owner',
    tradeId:'trade',bindingId:'binding',revision:1,writable:true,reread:async()=>controller.select(action,context),
    api:async(path,body)=>{
     if(path.endsWith('/prepare'))return {command:{...body,elections:[{right:0,fromDate:day}]}};
     if(stale){stale=false;throw Object.assign(Error('Synthetic conflicting instruction'),{code:'exercise_election_conflict'});}
     return {...body,elections:[{right:0,fromDate:day,revisionId:'recorded'}]};
    }});
   controller.select(action,context);controller.mount(document.querySelector('#host'));
   const find=selector=>document.querySelector(selector);
   async function submit(){
    find('.ticket-record-exercise').click();
    const form=find('form');
    for(const [key,value] of Object.entries({right:'0',firstPeriod:day,lastPeriod:day,outcome:'exercised',evidence:'Synthetic notice',effectiveAt:'2041-01-01T00:00:00Z'})){
     const input=form.elements.namedItem(key);input.value=value;input.dispatchEvent(new Event('change'));
    }
    form.requestSubmit();await new Promise(resolve=>setTimeout(resolve,0));
   }
   const message=()=>find('.ticket-exercise-controls [role=status]')?.textContent??'';
   await submit();const conflict=message();
   find('.ticket-refresh-exercise').click();await new Promise(resolve=>setTimeout(resolve,0));const refreshed=message();
   await submit();return [conflict,refreshed,message()];
  })()`);
  assert.match(statuses[0],/Refresh the trade/);
  assert.doesNotMatch(statuses[1],/Refresh the trade/);
  assert.equal(statuses[2],'Exercise election recorded.');
 });
});
