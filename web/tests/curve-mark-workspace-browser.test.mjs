import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,pressKey,withBrowserPage} from './browser-page.mjs';

const page='<link rel="stylesheet" href="/app.css"><link rel="stylesheet" href="/curve-mark.css"><section id="marks"></section><button id="outside">Outside</button>';
const options={skip:!browserAvailable,timeout:browserTestTimeout};
const wait=async(evaluate,condition)=>{
 for(let attempt=0;attempt<100;attempt++){
  if(await evaluate(condition))return;
  await new Promise(resolve=>setTimeout(resolve,20));
 }
 assert.fail('Expected browser condition '+condition);
};
const load=async evaluate=>{
 await evaluate(`(async()=>{
  const {setupCurveMarks}=await import('/curve-mark-view.js');
  const id=n=>'00000000-0000-4000-8000-'+String(n).padStart(12,'0');
  const source={definitionRevisionId:id(1),referenceCatalogId:id(2),referenceRevisionId:id(3)};
  const asOf='2026-01-01T00:00:00.000002Z',knownAt='2026-01-02T00:00:00Z',businessDate='2026-01-01';
  const contextId=id(4),ownerId='synthetic-owner',convention='curve_marks_log_return_held_v1';
  const choices=[{code:'synthetic_a',name:'Synthetic A',currency:'USD'},{code:'synthetic_b',name:'Synthetic B',currency:'USD'}];
  const cells=choices.map(({code},row)=>({address:{row,column:0},key:{firstCurve:code,secondCurve:null,month:'2026-01'},editable:true}));
  const baseline={cells:cells.map(({key})=>({state:{key,selected:null,writeHead:null,expectedRevision:0,editBlock:null},
   enteredValue:null,proposedValue:null,difference:null,isChanged:false,isNewMark:false,error:null})),coverage:{marked:0,missing:2,changed:0,invalid:0}};
  const draftFor=edits=>{
   const draft=structuredClone(baseline);
   for(const edit of edits){
    const row=draft.cells.find(item=>item.state.key.firstCurve===edit.key.firstCurve);
    row.enteredValue=edit.value;
    if(edit.value===''){row.error={code:'curve_mark_value',message:'Supply a mark.'};draft.coverage.invalid++;}
    else{row.proposedValue=edit.value;row.isChanged=row.isNewMark=true;draft.coverage.changed++;}
   }
   if(window.changedEvidence){
    const selected={id:id(10),revision:1,value:'0.6',effectiveAt:asOf,knownAt};
    draft.cells[1].state={...draft.cells[1].state,selected,writeHead:selected,expectedRevision:1};
    draft.coverage.marked=1;draft.coverage.missing=1;
   }
   return draft;
  };
  const receiptFor=(body,cell)=>({policy:body.policy,ownerId,operationId:body.operationId,kind:body.kind,source,
   effectiveAt:asOf,reason:body.reason,cells:[cell]});
  const api=async(path,body)=>{
   const context={selected:body.context,knownAt,mode:'working'};
   if(path==='date-schedules/context')return {context:{contextId,knownAt,effectiveOn:businessDate}};
   switch(path.split('/').at(-1)){
    case 'setup':return {source,context,ownerId,canPublish:true,convention,limits:{gridCurves:20,volatilityGridMonths:12,stripMonths:240,batchCells:240,pageSize:50,reasonCharacters:500},
     curves:choices,hasMoreCurves:false,view:body.view,layout:body.view?{rowCount:2,columnCount:1,cells}:null,draft:body.view?baseline:null};
    case 'history':return {source,context,kind:body.kind,key:body.key,revisions:[],hasMore:false};
    case 'validate':{
     const result={source,context,view:body.view,draft:draftFor(body.edits),addressed:[]};
     if(window.holdValidation)await new Promise(resolve=>{
      window.releaseValidation=()=>{window.holdValidation=false;resolve();};
     });
     return result;
    }
    case 'review':return {batch:body,alreadyRecorded:false,changes:body.changes.map(change=>({
     key:cells.find(cell=>cell.key.firstCurve===change.firstCurve).key,selectedValue:null,proposedValue:change.value,expectedRevision:change.expectedRevision,
     wouldChange:true,acknowledgedRevision:null,headAfterReview:{revision:1,value:change.value,effectiveAt:asOf},
    }))};
    case 'record':{
     window.original=structuredClone(body);
     const cell={key:cells[0].key,markId:id(8),revision:7,value:body.changes[0].value,changed:true};
     return receiptFor(body,{...cell,headAtPublication:{markId:cell.markId,revision:cell.revision,value:cell.value,effectiveAt:asOf}});
    }
    case 'status':{
     window.statusIdentity=structuredClone(body);
     const older={key:cells[0].key,markId:id(8),revision:1,value:original.changes[0].value,changed:false,
      headAtPublication:{markId:id(9),revision:2,value:'0.6',effectiveAt:window.statusInstant??'2026-01-01T00:00:00.000001Z'}};
     return {status:'committed',receipt:receiptFor(original,older)};
    }
   }
   throw Error('Unexpected fixture route '+path);
  };
  window.workspace=setupCurveMarks(document.querySelector('#marks'),{api,actor:()=> 'synthetic-actor',clock:()=>({stamp:'working',unavailable:false}),
   active:()=>true,busy:()=>false,permission:()=>true,sources:()=>[{source,label:'Synthetic source'}],changed:()=>{}});
  window.at=name=>document.querySelector('[data-mark="'+name+'"]');
  window.field=name=>document.querySelector('[name="'+name+'"]');
  window.gridCell=row=>document.querySelector('[data-mark-cell="'+row+':0"]');
  window.editInput=()=>document.querySelector('.mark-cell-input');
  window.setEdit=value=>{editInput().value=value;editInput().dispatchEvent(new Event('input',{bubbles:true}));};
  window.metric=name=>document.querySelector('[data-metric="'+name+'"] strong').textContent;
  window.inspectorAction=label=>[...at('inspector').querySelectorAll('button')].find(button=>button.textContent===label);
  workspace.load();
  field('markSource').selectedIndex=1;field('markDate').value=businessDate;field('markAsOf').value=asOf;
  at('clock-form').requestSubmit();
 })()`);
 await wait(evaluate,"!at('selector').hidden");
 await evaluate(`for(const choice of at('curves').querySelectorAll('input'))choice.click();
 field('markFrom').value=field('markTo').value='2026-01';at('view-form').requestSubmit();`);
 await wait(evaluate,"gridCell(0)?.dataset.editable==='true'");
};
const enterEdit=async(evaluate,command,row,value)=>{
 await evaluate(`gridCell(${row}).focus()`);
 await pressKey(command,'Enter');
 await evaluate(`setEdit(${JSON.stringify(value)})`);
 await pressKey(command,'Enter');
};

test('Validation arriving during another cell edit reaches the grid on Escape and untouched blur',options,async()=>{
 await withBrowserPage(page,async(evaluate,command)=>{
  await load(evaluate);
  for(const exit of ['Escape','outside blur','grid blur','Shift-Tab']){
   await evaluate('window.holdValidation=true;window.releaseValidation=undefined');
   await enterEdit(evaluate,command,0,'');
   await wait(evaluate,"typeof window.releaseValidation==='function'");
   assert.equal(await evaluate('document.activeElement.dataset.markCell'),'1:0','Enter moves down from A to B');
   await pressKey(command,'Enter');
   await evaluate('window.originalInput=editInput();window.releaseValidation()');
   await wait(evaluate,"metric('invalid')==='1'");
   assert.equal(await evaluate('document.activeElement===originalInput&&editInput()===originalInput'),true,'validation keeps the active input in place');
   if(exit==='Escape')await command('Input.dispatchKeyEvent',{type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
   if(exit==='outside blur')await evaluate("document.querySelector('#outside').focus()");
   if(exit==='grid blur')await evaluate('gridCell(0).focus()');
   if(exit==='Shift-Tab')for(const type of ['keyDown','keyUp'])await command('Input.dispatchKeyEvent',{type,key:'Tab',code:'Tab',windowsVirtualKeyCode:9,modifiers:8});
   assert.equal(await evaluate("gridCell(0).querySelector('.mark-cell-state').textContent"),'Invalid');
   if(exit==='Escape')assert.equal(await evaluate('document.activeElement.dataset.markCell'),'1:0');
   if(exit==='outside blur')assert.equal(await evaluate('document.activeElement.id'),'outside');
   if(exit==='grid blur'||exit==='Shift-Tab')assert.equal(await evaluate('document.activeElement.dataset.markCell'),'0:0');
   await evaluate("at('discard').click()");
  }
 });
});

test('Discard and last-cell reset restore the clean setup evidence',options,async()=>{
 await withBrowserPage(page,async(evaluate,command)=>{
  await load(evaluate);
  await evaluate('window.changedEvidence=true');
  for(const reset of ['cell','all']){
   await enterEdit(evaluate,command,0,'0.5');
   await wait(evaluate,"metric('marked')==='1'");
   assert.equal(await evaluate('gridCell(1).dataset.state'),'published');
   if(reset==='cell')await evaluate("gridCell(0).focus();inspectorAction('Reset cell').click()");
   else await evaluate("at('discard').click()");
   assert.equal(await evaluate("metric('marked')"),'0','coverage returns to the exact initial setup evidence');
   assert.equal(await evaluate('gridCell(1).dataset.state'),'missing');
  }
 });
});

test('A bad record and a bad recovered receipt leave the exact intended publication pending',options,async()=>{
 await withBrowserPage(page,async(evaluate,command)=>{
  await load(evaluate);
  await enterEdit(evaluate,command,0,'0.5');
  await wait(evaluate,"gridCell(0).dataset.state==='draft'");
  await evaluate("at('review').click();field('markReason').value='Synthetic evidence';at('reason-form').requestSubmit()");
  await wait(evaluate,"at('publish')&&!at('publish').disabled");
  await evaluate("at('publish').click()");
  await wait(evaluate,"window.original&&(!at('status').disabled||!at('receipt').hidden)");
  assert.equal(await evaluate("workspace.pending()&&at('receipt').hidden"),true);
  assert.deepEqual(await evaluate("JSON.parse(sessionStorage.getItem('curve-mark-publication:synthetic-actor')).body"),await evaluate('original'));
  await evaluate("at('status').click()");
  await wait(evaluate,"window.statusIdentity&&(!at('status').disabled||!at('receipt').hidden)");
  assert.equal(await evaluate("workspace.pending()&&at('receipt').hidden"),true);
  assert.equal(await evaluate("at('dismiss').disabled"),true);
  assert.deepEqual(await evaluate("JSON.parse(sessionStorage.getItem('curve-mark-publication:synthetic-actor')).body"),await evaluate('original'));
  assert.deepEqual(await evaluate('statusIdentity'),await evaluate('({ownerId:original.ownerId,operationId:original.operationId,kind:original.kind})'));
  await evaluate("window.statusInstant='2026-01-01T00:00:00.000003Z';at('status').click()");
  await wait(evaluate,"!at('receipt').hidden&&!workspace.pending()");
  assert.match(await evaluate("at('receipt').textContent"),/Acknowledged revision 1.*Revision 2/s);
 });
});
