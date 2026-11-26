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
  const {attachReadReferences}=await import('/read-references.js');
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
   if(window.overrideApi){
    const override=await window.overrideApi(path,body);
    if(override!==undefined)return override;
   }
   const context={selected:body.context,knownAt,mode:'working'};
   if(path==='date-schedules/context')return attachReadReferences({context:{contextId,knownAt,effectiveOn:businessDate}},window.omitProof?{}:{[contextId]:'synthetic-read-proof'});
   switch(path.split('/').at(-1)){
    case 'setup':return {source,context,ownerId,canPublish:true,convention,limits:{gridCurves:20,volatilityGridMonths:12,stripMonths:240,batchCells:240,pageSize:50,reasonCharacters:500},
     curves:choices,hasMoreCurves:false,view:body.view,layout:body.view?{rowCount:2,columnCount:1,cells}:null,draft:body.view?baseline:null};
    case 'history':return {source,context,kind:body.kind,key:body.key,revisions:[],hasMore:false};
    case 'validate':{
     const addressed=body.paste?[{address:cells[1].address,key:cells[1].key,value:'0'}]:[];
     const result={source,context,view:body.view,draft:draftFor([...body.edits,...addressed]),addressed};
     if(body.paste){window.pasteRequest=body;if(window.badPaste)result.draft.cells[0].enteredValue='0.8';}
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
  window.workspace=setupCurveMarks(document.querySelector('#marks'),{api,actor:()=>window.actor??'synthetic-actor',clock:()=>({stamp:window.clockStamp??'working',unavailable:false}),
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
  assert.equal(await evaluate("document.activeElement===at('status')"),true,'uncertain publication directs the operator to recovery');
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

test('TSV preview preserves existing edits, rejects inconsistent evidence and applies only on confirmation',options,async()=>{
 await withBrowserPage(page,async(evaluate,command)=>{
  await load(evaluate);
  await enterEdit(evaluate,command,0,'0.5');
  await wait(evaluate,"gridCell(0).dataset.state==='draft'");
  const paste=()=>evaluate(`gridCell(0).focus();
   {const data=new DataTransfer();data.setData('text/plain','0.00\\n');
    gridCell(1).dispatchEvent(new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:data}));}`);
  await paste();
  await wait(evaluate,"at('paste-apply')&&!at('paste-apply').disabled");
  assert.deepEqual(await evaluate('pasteRequest.paste'),{start:{row:1,column:0},text:'0.00\n',mode:'rectangle'});
  assert.equal(await evaluate('gridCell(1).dataset.state'),'missing');
  assert.match(await evaluate("at('paste-details').textContent"),/synthetic_b.*2026-01.*0/s);
  await evaluate("at('paste-dialog').close();window.badPaste=true");
  await paste();
  await wait(evaluate,"at('paste-status').textContent.includes('match')");
  assert.equal(await evaluate("at('paste-apply').disabled"),true);
  await evaluate("at('paste-dialog').close();window.badPaste=false");
  await paste();
  await wait(evaluate,"!at('paste-apply').disabled");
  await evaluate("at('paste-apply').click()");
  assert.deepEqual(await evaluate("[gridCell(0),gridCell(1)].map(cell=>cell.textContent)"),['0.5Validated draft','0Validated draft']);
 });
});

test('A checked absence is scoped to the original actor and context generation',options,async()=>{
 await withBrowserPage(page,async(evaluate,command)=>{
  await load(evaluate);
  await enterEdit(evaluate,command,0,'0.5');
  await wait(evaluate,"gridCell(0).dataset.state==='draft'");
  await evaluate("at('review').click();field('markReason').value='Synthetic absence';at('reason-form').requestSubmit()");
  await wait(evaluate,"at('publish')&&!at('publish').disabled");
  await evaluate("at('publish').click()");
  await wait(evaluate,"window.original&&!at('status').disabled");
  await evaluate("window.overrideApi=async path=>{if(path.endsWith('/status'))return {status:'absent',receipt:null}};at('status').click()");
  await wait(evaluate,"!at('dismiss').disabled");
  await evaluate("window.clockStamp='later';workspace.sync()");
  assert.equal(await evaluate("at('dismiss').disabled"),true);
  await evaluate("window.actor='someone-else';workspace.sync()");
  assert.equal(await evaluate("workspace.pending()"),false);
  await evaluate("window.actor='synthetic-actor';workspace.sync()");
  assert.equal(await evaluate("workspace.pending()&&at('dismiss').disabled"),true);
  assert.doesNotMatch(await evaluate("at('pending-status').textContent"),/no committed/);
  for(const expected of await evaluate('[original.operationId,original.reason,original.context.asOf]'))
   assert.ok((await evaluate("at('pending').textContent")).includes(expected),'original publication identifies '+expected);
  await evaluate("window.overrideApi=async path=>{if(path.endsWith('/status'))throw Object.assign(Error('Source denied'),{code:'curve_mark_denied'})};at('status').click()");
  await wait(evaluate,"at('pending-status').textContent.includes('Source denied')");
  assert.equal(await evaluate("workspace.pending()&&at('dismiss').disabled"),true);
 });
});

async function reviewOne(evaluate,command){
 await enterEdit(evaluate,command,0,'0.5');
 await wait(evaluate,"gridCell(0).dataset.state==='draft'");
 await evaluate("at('review').click();field('markReason').value='Synthetic recovery';at('reason-form').requestSubmit()");
 await wait(evaluate,"at('publish')&&!at('publish').disabled");
}

for(const failure of ['proof write','command write after proof','changed stored proof','changed stored command','missing context proof']){
 test('Publication stays in review with zero record requests on '+failure,options,async()=>{
  await withBrowserPage(page,async(evaluate,command)=>{
   if(failure==='missing context proof')await evaluate('window.omitProof=true');
   await load(evaluate);
   await evaluate(`window.records=[];window.overrideApi=async(path,body)=>{
    if(path.endsWith('/review'))window.reviewedBatch=structuredClone(body);
    if(path.endsWith('/record'))records.push(structuredClone(body));
   }`);
   await reviewOne(evaluate,command);
   await evaluate(`{const failure=${JSON.stringify(failure)};window.storageWrites=[];window.originalSetter=Storage.prototype.setItem;
    Storage.prototype.setItem=function(key,value){
     if(key.startsWith('curve-mark-publication:')){
      const proof=key.endsWith(':read-references');storageWrites.push(proof?'proof':'command');
      if((proof&&failure==='proof write')||(!proof&&failure==='command write after proof'))
       throw new DOMException('Storage full','QuotaExceededError');
      if(proof&&failure==='changed stored proof')value=value.replace('synthetic-read-proof','different-proof');
      if(!proof&&failure==='changed stored command'){
       const changed=JSON.parse(value);changed.body.changes[0].value='0.6';value=JSON.stringify(changed);
      }
     }
     originalSetter.call(this,key,value);
    };at('publish').click();}`);
   assert.deepEqual(await evaluate('records'),[]);
   if(failure==='command write after proof')assert.deepEqual(await evaluate('storageWrites'),['proof','command']);
   assert.match(await evaluate("at('review-status').textContent"),/could not save/i);
   assert.equal(await evaluate("at('publish').closest('dialog').open&&!at('publish').disabled&&!workspace.pending()"),true);
   assert.equal(await evaluate("field('markReason').value"),'Synthetic recovery');
   assert.equal(await evaluate("gridCell(0).textContent"),'0.5Validated draft');
   if(failure==='missing context proof')return;
   await evaluate("Storage.prototype.setItem=originalSetter;at('publish').click()");
   await wait(evaluate,'records.length===1');
   assert.deepEqual(await evaluate('records[0]'),await evaluate('reviewedBatch'));
   assert.deepEqual(await evaluate("JSON.parse(sessionStorage.getItem('curve-mark-publication:synthetic-actor:read-references'))"),
    {[await evaluate('reviewedBatch.context.contextId')]:'synthetic-read-proof'});
  });
 });
}

test('A retry blocked by storage keeps the original uncertain publication',options,async()=>{
 await withBrowserPage(page,async(evaluate,command)=>{
  await load(evaluate);
  await reviewOne(evaluate,command);
  await evaluate(`window.records=[];window.overrideApi=async(path,body)=>{
   if(path.endsWith('/record')){records.push(structuredClone(body));throw Error('Reply lost');}
  };at('publish').click()`);
  await wait(evaluate,"at('pending-status').textContent.includes('Reply lost')");
  await evaluate(`sessionStorage.removeItem('curve-mark-publication:synthetic-actor');
   window.originalSetter=Storage.prototype.setItem;Storage.prototype.setItem=function(){throw Error('Storage unavailable');};
   at('retry').click()`);
  assert.equal(await evaluate('records.length'),1);
  assert.equal(await evaluate("workspace.pending()&&at('review').disabled"),true);
  assert.match(await evaluate("at('pending-status').textContent"),/could not save/i);
  await evaluate("Storage.prototype.setItem=originalSetter;at('retry').click()");
  await wait(evaluate,'records.length===2');
  assert.deepEqual(await evaluate('records[1]'),await evaluate('records[0]'));
 });
});

test('A denied retry retains an absent original, and a refresh failure preserves its verified receipt',options,async()=>{
 await withBrowserPage(page,async(evaluate,command)=>{
  await load(evaluate);
  await reviewOne(evaluate,command);
  await evaluate(`window.sends=[];window.overrideApi=async(path,body)=>{
   if(path.endsWith('/status'))return {status:'absent',receipt:null};
   if(path==='date-schedules/context')throw Error('Synthetic refresh unavailable');
   if(!path.endsWith('/record'))return;
   sends.push(structuredClone(body));
   if(sends.length===1)throw Error('No reply');
   if(sends.length===2)throw Object.assign(Error('Publish denied'),{code:'curve_mark_denied'});
   const {changes,context,...header}=body,change=changes[0],{expectedRevision,value,...key}=change;
   const markId='00000000-0000-4000-8000-000000000009';
   return {...header,effectiveAt:context.asOf,cells:[{key,markId,value,revision:1,changed:true,headAtPublication:{markId,value,revision:1,effectiveAt:context.asOf}}]};
  };at('publish').click()`);
  await wait(evaluate,"at('pending-status').textContent.includes('No reply')");
  await evaluate("at('status').click()");
  await wait(evaluate,"!at('dismiss').disabled");
  await evaluate("at('retry').click()");
  await wait(evaluate,"at('pending-status').textContent.includes('Publish denied')");
  assert.equal(await evaluate("workspace.pending()&&at('dismiss').disabled"),true);
  await evaluate("at('retry').click()");
  await wait(evaluate,"at('message').textContent.includes('Mark refresh failed')");
  assert.equal(await evaluate("!workspace.pending()&&!at('receipt').hidden"),true);
  assert.deepEqual(await evaluate('sends'),Array(3).fill(await evaluate('sends[0]')));
 });
});

test('Late validation and publication replies stay with their original owner and editor generation',options,async()=>{
 await withBrowserPage(page,async(evaluate,command)=>{
  await load(evaluate);
  await evaluate('window.holdValidation=true');
  await enterEdit(evaluate,command,0,'0.5');
  await wait(evaluate,"typeof window.releaseValidation==='function'");
  await evaluate("window.actor='another-actor';workspace.sync();releaseValidation()");
  assert.equal(await evaluate("at('desk').hidden&&at('draft-bar').hidden"),true);
  await evaluate("window.actor='synthetic-actor'");
  await load(evaluate);
  await reviewOne(evaluate,command);
  await evaluate(`window.overrideApi=async(path,body)=>{
   if(!path.endsWith('/record'))return;
   window.sent=structuredClone(body);
   await new Promise(resolve=>window.releaseRecord=resolve);
   const error=Object.assign(Error('Late refusal from original source'),{code:'curve_mark_stale'});throw error;
  };at('publish').click()`);
  await wait(evaluate,"typeof window.releaseRecord==='function'");
  await evaluate("window.actor='another-actor';workspace.sync();releaseRecord()");
  await wait(evaluate,"at('pending').hidden&&at('receipt').hidden");
  assert.doesNotMatch(await evaluate("at('message').textContent"),/Late refusal/);
  await evaluate("window.actor='synthetic-actor';workspace.sync()");
  assert.equal(await evaluate("workspace.pending()&&at('dismiss').disabled"),true);
  assert.deepEqual(await evaluate("JSON.parse(sessionStorage.getItem('curve-mark-publication:synthetic-actor')).body"),await evaluate('sent'));
 });
});

test('An addressed paste refusal names its grid destination, and late preview cannot affect the next source',options,async()=>{
 await withBrowserPage(page,async(evaluate)=>{
  await load(evaluate);
  await evaluate(`window.overrideApi=async(path,body)=>{
   if(body.paste)throw Object.assign(Error('Supply a plain decimal.'),{details:{address:{row:1,column:0}}});
  };
  gridCell(1).focus();at('paste').click();field('markPasteText').value='=1';document.querySelector('.mark-paste-form').requestSubmit();`);
  await wait(evaluate,"at('paste-status').textContent.includes('plain decimal')");
  assert.match(await evaluate("at('paste-status').textContent"),/Row 2, column 1/);
  await evaluate(`window.overrideApi=async(path,body)=>{
   if(body.paste)await new Promise(resolve=>window.releasePaste=resolve);
  };field('markPasteText').value='0';document.querySelector('.mark-paste-form').requestSubmit();`);
  await wait(evaluate,"typeof window.releasePaste==='function'");
  await evaluate("window.clockStamp='new context';workspace.sync();releasePaste()");
  assert.equal(await evaluate("!at('paste-dialog').open&&at('desk').hidden&&at('paste-apply').disabled"),true);
 });
});

test('Review keeps the exact head date and time readable at desktop and phone widths',options,async()=>{
 await withBrowserPage(page,async(evaluate,command)=>{
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1000,deviceScaleFactor:1,mobile:false});
  await load(evaluate);await reviewOne(evaluate,command);
  for(const width of [1440,768,390]){
   await command('Emulation.setDeviceMetricsOverride',{width,height:900,deviceScaleFactor:1,mobile:false});
   const result=await evaluate(`(()=>{
    const cell=at('review-details').querySelector('tbody tr').lastElementChild;
    const clock=cell.querySelector('time');
    const headers=[...cell.closest('table').tHead.rows[0].cells];
    if(!clock)return {text:cell.textContent,height:cell.offsetHeight};
    const chunks=[...clock.querySelectorAll('span')];
    return {instant:clock.dateTime,text:clock.textContent,lines:chunks.map(chunk=>{
     const range=new Range();range.selectNodeContents(chunk);return range.getClientRects().length;
    }),fits:cell.scrollWidth<=cell.clientWidth+1,clipped:headers.filter(th=>th.scrollWidth>th.clientWidth+1).map(th=>th.textContent),height:cell.offsetHeight};
   })()`);
   assert.equal(result.instant,'2026-01-01T00:00:00.000002Z',`Exact head instant at ${width}px: ${JSON.stringify(result)}`);
   assert.equal(result.text,result.instant);
   assert.deepEqual(result.lines,[1,1]);
   assert.equal(result.fits,true);
   if(width>600)assert.deepEqual(result.clipped,[],'Headers fit their columns at '+width+'px');
   assert.ok(result.height<75,JSON.stringify(result));
  }
 });
});
