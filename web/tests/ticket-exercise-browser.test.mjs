import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

for(const kind of ['exercise','swing'])for(const code of [kind+'_election_conflict',kind==='swing'?'swing_feasibility':'exercise_election'])
test(`a lost ${kind} refusal releases its exact retry after ${code}`,{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<main></main>',async evaluate=>{
  const result=await evaluate(`(async()=>{
   const kind=${JSON.stringify(kind)},code=${JSON.stringify(code)},day='2041-02-01',ctx={snapshotId:'frame',knowledgeMode:'current'};
   const {setupExerciseAction}=await import('/ticket-exercise-action.js');
   const base={allowed:true,tradeId:'trade',instrumentId:'binding',expectedRevision:1,readContextId:'held',label:'Record',
    preparePath:'/api/gas/'+kind+'/prepare',recordPath:'/api/gas/'+kind+'/record'};
   const action=kind==='exercise'?{...base,periods:[{right:0,from:day,to:day,exerciseAt:day,previousId:null}],outcomes:[{value:'exercised',label:'Exercised'}]}:
    {...base,right:0,days:[{day,exerciseAt:day,state:'undecided',head:null,effectiveRevision:null,evidence:null,electedQuantity:null}],
     periods:[],planningStep:'1',dailyMinimum:'0',dailyMaximum:'2',referenceQuantity:'1',
     decisions:[{value:'elect',label:'Elect'}],parties:[{value:'us',label:'Us'}]};
   const host=document.querySelector('main'),writes=[];
   let editor,reads=0;
   function mount(){
    editor=setupExerciseAction({kind,context:()=>ctx,current:()=>true,signal:new AbortController().signal,owner:'actor',tradeId:'trade',bindingId:'binding',revision:1,
     writable:true,reread:async()=>{reads++;editor.select(action,ctx,'MMBtu');},api:async(path,body)=>{
      if(path.endsWith('/prepare'))return {command:{...body,...kind==='exercise'?{right:undefined,elections:[{right:0,fromDate:day}]}:
       {expectedHeads:[{day,revisionId:null}],elections:[{day,quantity:'0'}]}}};
      writes.push(body);
      if(writes.length===1)throw Error('Synthetic lost reply');
      if(writes.length===2)throw Object.assign(Error('Synthetic authority refusal'),{code:'access_denied',status:403});
      throw Object.assign(Error('Synthetic authoritative refusal'),{code,status:code.endsWith('conflict')?409:422});
     }});
    host.replaceChildren();editor.select(action,ctx,'MMBtu');editor.mount(host);
   }
   const tick=()=>new Promise(r=>setTimeout(r,10));
   mount();host.querySelector('.ticket-record-'+kind).click();const form=host.querySelector('form');
   const values=kind==='exercise'?{right:'0',firstPeriod:day,lastPeriod:day,outcome:'exercised'}:
    {firstDay:day,lastDay:day,decision:'elect',quantity:'0',exercisingParty:'us',submittedAt:'2041-01-01T00:00:00Z'};
   for(const [key,val] of Object.entries({...values,effectiveAt:'2041-01-01T00:00:00Z',evidence:'Synthetic notice'})){
    const ctrl=form.elements.namedItem(key);ctrl.value=val;ctrl.dispatchEvent(new Event('change'));
   }
   form.requestSubmit();await tick();mount();const retry=()=>host.querySelector('.ticket-retry-'+kind);
   retry().click();await tick();const deniedPending=editor.pending()&&!!retry();
   retry().click();await tick();const pending=editor.pending();host.querySelector('.ticket-refresh-'+kind)?.click();await tick();
   return {deniedPending,pending,reads,writes,available:!host.querySelector('.ticket-record-'+kind).disabled};
  })()`);
  assert.ok(result.deniedPending);assert.deepEqual(result.writes,[result.writes[0],result.writes[0],result.writes[0]]);
  assert.deepEqual([result.pending,result.reads,result.available],[false,1,true]);
 });
});

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

for(const firstRefusal of [true,false])
test(firstRefusal?'a first swing payload refusal allows editing and recording a smaller range':
 'a swing election keeps zero explicit and retries the original range after a lost reply and payload refusal',
 {skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<div id="host"></div>',async evaluate=>{
  const result=await evaluate(`(async()=>{
   const {setupExerciseAction}=await import('/ticket-exercise-action.js');
   const context={snapshotId:'frame',knowledgeMode:'current'};
   const firstRefusal=${firstRefusal};
   const head='saved-head',days=['2041-02-01','2041-02-02','2041-02-03'].map((day,i)=>({day,exerciseAt:'2041-01-31T16:00:00Z',
    state:'undecided',head:i===2?head:null,effectiveRevision:null,electedQuantity:null,evidence:null}));
   const action={allowed:true,label:'Record swing election',preparePath:'/api/gas/swing/prepare',recordPath:'/api/gas/swing/record',
    tradeId:'trade',instrumentId:'binding',expectedRevision:1,readContextId:'cutoff',right:0,holder:'us',periods:[],planningStep:'100',dailyMinimum:'0',
    dailyMaximum:'1000',referenceQuantity:'1000',
    decisions:[{value:'elect',label:'Elect a quantity'},{value:'withdraw',label:'Withdraw an election'}],parties:[{value:'us',label:'Us'}],days};
   const writes=[],prepares=[],host=document.querySelector('#host');
   const until=async f=>{for(let i=0;i<200&&!f();i++)await new Promise(r=>setTimeout(r,10));if(!f())throw Error('Missing election control');};
   const mount=()=>{
    const editor=setupExerciseAction({kind:'swing',owner:'actor',tradeId:'trade',bindingId:'binding',revision:1,context:()=>context,current:()=>true,
     writable:true,signal:new AbortController().signal,reread:async()=>{},api:async(path,body)=>{
      if(path==='gas/swing/prepare'){
       if(body.lastDay<body.firstDay)throw Object.assign(Error('Reversed range'),{details:{field:'lastDay',termRow:0}});
       prepares.push(body);return {command:{context,tradeId:'trade',instrumentId:'binding',expectedRevision:1,
       operationId:body.operationId,right:0,expectedHeads:days.map(d=>({day:d.day,revisionId:d.head})),elections:days.filter(d=>d.day>=body.firstDay&&d.day<=body.lastDay).map(d=>({day:d.day,quantity:body.quantity}))}};}
      if(path==='gas/swing/record'){writes.push(body);if(writes.length===(firstRefusal?1:2))
       throw Object.assign(Error('Command too large'),{status:413,code:'swing_payload',details:{field:'elections'}});
       if(!firstRefusal&&writes.length===1)throw Error('Synthetic lost response');return {...body,elections:body.elections.map(e=>({...e,revisionId:'receipt'}))};}
      throw Error(path);
     }});
    host.replaceChildren();editor.select(action,context,'MMBtu');editor.mount(host);
   };
   mount();host.querySelector('.ticket-record-swing').click();
   const form=host.querySelector('form'),empty=[...form.elements].filter(c=>c.tagName==='SELECT'||c.tagName==='INPUT').every(c=>c.value==='');
   const fill=(k,v)=>{const c=host.querySelector('form').elements.namedItem(k);c.value=v;c.dispatchEvent(new Event('change'));};
   const values={firstDay:days[0].day,lastDay:days[1].day,decision:'elect',quantity:'0',exercisingParty:'us',
    submittedAt:'2041-01-01T16:00:00Z',effectiveAt:'2041-01-02T16:00:00Z',evidence:'Synthetic range'};
   for(const [k,v] of Object.entries(values))fill(k,v);
   fill('firstDay',days[1].day);fill('lastDay',days[0].day);form.requestSubmit();
   const end=form.elements.namedItem('lastDay');await until(()=>end===document.activeElement&&end.getAttribute('aria-invalid')==='true');
   fill('firstDay',days[0].day);fill('lastDay',days[1].day);const corrected=!end.hasAttribute('aria-invalid');
   form.requestSubmit();await until(()=>writes.length===1&&!form.inert||!!host.querySelector('.ticket-retry-swing'));
   if(firstRefusal){
    if(host.querySelector('form')!==form||host.querySelector('.ticket-retry-swing'))throw Error('The first definite refusal did not restore the editable range');
    mount();host.querySelector('.ticket-record-swing').click();
    for(const [key,value] of Object.entries({...values,lastDay:days[0].day}))fill(key,value);
    host.querySelector('form').requestSubmit();
   }else{
    mount();host.querySelector('.ticket-retry-swing').click();await until(()=>writes.length===2);
    mount();host.querySelector('.ticket-retry-swing').click();
   }
   await until(()=>!host.querySelector('.ticket-record-swing').disabled);
   return {empty,corrected,prepares,writes};
  })()`);
  assert.ok(result.empty);assert.ok(result.corrected);
  assert.equal(result.prepares.length,firstRefusal?2:1);assert.equal(result.prepares[0].quantity,'0');
  if(firstRefusal){
   assert.equal(result.prepares[1].lastDay,result.prepares[1].firstDay);
   assert.deepEqual(result.writes.map(w=>w.elections.length),[2,1]);
   assert.notEqual(result.writes[0].operationId,result.writes[1].operationId);
  }
  else assert.deepEqual(result.writes,[result.writes[0],result.writes[0],result.writes[0]]);
  assert.equal(result.writes[0].expectedHeads[2].revisionId,'saved-head');
 });
});

test('the election ticket labels one gas day and multiple gas days',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<main></main>',async evaluate=>{
  const labels=await evaluate(`(async()=>{
   const {swingContractDetails}=await import('/ticket-swing.js');
   const first={from:'2041-01-31',through:'2041-01-31',days:1,limitPeriod:'delivery_month',changesUsed:0,changeCount:null,
    appliedLower:null,appliedUpper:null,remainingLower:null,remainingUpper:null,elected:'0',remainingMinimum:'0',remainingMaximum:'100',
    planned:null,plannedChanges:null,missingDays:0,blockedDays:0,nextDeadline:null};
   const action={referenceQuantity:'100',dailyMinimum:'0',dailyMaximum:'100',planningStep:'1',days:[],
    periods:[first,{...first,from:'2041-02-01',through:'2041-02-02',days:2}]};
   const ticket=swingContractDetails({source:'Gas'},'MMBtu',s=>s,undefined,action);
   return Array.from(ticket.querySelectorAll('[aria-label="Period limits and elections"] tbody tr'),r=>r.cells[0].textContent.split(' · ').at(-1));
  })()`);
  assert.deepEqual(labels,['1 gas day','2 gas days']);
 });
});
