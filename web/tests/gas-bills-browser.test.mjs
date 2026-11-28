import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
const line=(id,cashSlot,day,amount)=>({determinationId:id,cashSlot,day,amount,ordinal:1,dueAt:'2021-02-02T00:00:00Z'});
const bill={id:'bill',ordinal:1,kind:'bill',previousId:null,payTotal:'-594',receiveTotal:'20',currency:'usd',effectiveAt:'2021-01-15T08:00:00Z',evidence:'Synthetic bill',
 lines:[line('a',1,'2021-01-01','-300'),line('b',2,'2021-01-01','10')]};
const view={expectedRevision:1,canWrite:true,scopePolicy:'gas_trade_month_gross_v1',amountPolicy:'sum_of_daily_determinations_v1',
 payments:[{cashSlot:1,code:'payment',role:'consideration',direction:-1,covered:true,reason:null},{cashSlot:2,code:'fee',role:'fee',direction:1,covered:true,reason:null},
  {cashSlot:3,code:'capacity',role:'charge',direction:-1,covered:false,reason:'payment_kind'}],
 months:[{month:'2021-01-01',state:'adjustment_ready',refusal:null,missing:[],documents:[bill]},
  {month:'2021-02-01',state:'incomplete',refusal:null,missing:[{cashSlot:1,day:'2021-02-02'},{cashSlot:2,day:'2021-02-02'}],documents:[]},
  {month:'2021-03-01',state:'refused',refusal:'gas_bill_later_document',missing:[],documents:[]}]};
const note={state:'adjustment_ready',missing:[],previousId:'bill',previewHash:'reviewed',contentDigest:'digest',
 document:{kind:'adjustment_note',lines:[line('c',2,'2021-01-02','-0.2')],payTotal:'0',receiveTotal:'-0.2',currency:'usd'}};
const mount=`const {setupGasBills}=await import('/gas-bills.js');const tick=()=>new Promise(r=>setTimeout(r,0));`;
const choose=`const pick=async(name,value)=>{const field=root.querySelector('[data-bills='+name+']');field.value=value;field.dispatchEvent(new Event('change',{bubbles:true}));await tick();};`;

test('monthly bills show each month, what is not covered and the issued documents without computing an amount',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{${mount}
  const api=async()=>(${JSON.stringify(view)}),workflow={busy:()=>false,pending:()=>undefined,queue:async()=>{},changed(){}};
  setupGasBills(document.querySelector('#host'),{api,context:()=>({}),tradeId:'synthetic',actor:()=> 'operator',zone:()=> 'UTC',workflow});await tick();
  const root=document.querySelector('.gas-bills'),month=root.querySelector('[data-bills=month]');
  return {months:[...month.options].filter(o=>o.value).map(o=>[o.value,o.textContent,o.disabled]),scope:[...root.querySelector('[data-bills=scope]').options].map(o=>o.value),
   coverage:root.querySelector('[data-bills=coverage]').textContent,documents:root.querySelector('[data-bills=documents]').textContent,text:root.textContent};
 })()`);
 assert.deepEqual(result.months,[['2021-01-01','2021-01 · adjustment note ready',false],['2021-02-01','2021-02 · incomplete · 2 payment days without a determination',false],
  ['2021-03-01','2021-03 · unavailable in this view · a document takes effect after the selected business time',true]]);
 assert.deepEqual(result.scope,['','gas_trade_month_gross_v1']);
 assert.match(result.coverage,/payment · covered/);assert.match(result.coverage,/capacity · not covered · charges and cash-outs have no daily determination/);
 for(const shown of ['Bill','-594','20','Synthetic bill'])assert.ok(result.documents.includes(shown),shown);
 assert.doesNotMatch(result.text,/-574|net total/i);
}));

test('a state, document kind or scope policy the screen does not know is shown as the backend named it',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const unknown={...view,scopePolicy:'synthetic_scope_v9',months:[{month:'2021-01-01',state:'synthetic_state',refusal:null,missing:[],
  documents:[bill,{...bill,id:'second',ordinal:2,kind:'synthetic_kind'},{...bill,id:'third',ordinal:3,kind:'adjustment_note'}]}]};
 const result=await evaluate(`(async()=>{${mount}
  const api=async()=>(${JSON.stringify(unknown)}),workflow={busy:()=>false,pending:()=>undefined,queue:async()=>{},changed(){}};
  setupGasBills(document.querySelector('#host'),{api,context:()=>({}),tradeId:'synthetic',actor:()=> 'operator',zone:()=> 'UTC',workflow});await tick();
  const root=document.querySelector('.gas-bills');
  return {month:[...root.querySelector('[data-bills=month]').options].filter(o=>o.value).map(o=>o.textContent),scope:[...root.querySelector('[data-bills=scope]').options].filter(o=>o.value).map(o=>o.textContent),
   headers:[...root.querySelectorAll('[data-bills=documents] thead th')].map(h=>h.textContent.trim()),
   documents:[...root.querySelectorAll('[data-bills=documents] tbody tr')].map(r=>[...r.cells].slice(0,3).map(c=>c.textContent.trim()))};
 })()`);
 assert.deepEqual(result.month,['2021-01 · synthetic_state']);assert.deepEqual(result.scope,['synthetic_scope_v9']);
 assert.deepEqual(result.headers.slice(0,3),['Month','Document','Sequence']);
 assert.deepEqual(result.documents,[['2021-01','Bill','1'],['2021-01','synthetic_kind','2'],['2021-01','Adjustment note','3']]);
}));

test('a reviewed adjustment note states both totals and its lines, and issuing sends exactly the reviewed request',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{${mount}let sent,asked;
  const api=async(path,body)=>{if(path==='gas/bills/read')return ${JSON.stringify(view)};asked=body;return ${JSON.stringify(note)};};
  setupGasBills(document.querySelector('#host'),{api,context:()=>({snapshotId:'frame'}),tradeId:'synthetic',actor:()=> 'operator',zone:()=> 'UTC',workflow:{busy:()=>false,pending:()=>undefined,changed(){},async queue(command){sent=command;}}});
  await tick();const root=document.querySelector('.gas-bills');${choose}
  await pick('month','2021-01-01');await pick('scope','gas_trade_month_gross_v1');await pick('evidence','Synthetic note review');
  root.querySelector('form').requestSubmit();await tick();
  const review=root.querySelector('[data-bills=review]').textContent,confirm=root.querySelector('[data-bills=confirm]');const label=confirm.textContent,hidden=confirm.hidden;
  confirm.click();await tick();
  return {asked,review,label,hidden,path:sent.path,body:sent.body,commandLabel:sent.label};
 })()`);
 assert.deepEqual(result.asked,{context:{snapshotId:'frame'},tradeId:'synthetic',expectedRevision:1,month:'2021-01-01',scopePolicy:'gas_trade_month_gross_v1'});
 assert.match(result.review,/^Adjustment note for 2021-01 · 1 line\nPayments we pay: 0 usd\nPayments we receive: -0\.2 usd\nfee · 2021-01-02 · -0\.2 usd/);
 assert.deepEqual([result.label,result.hidden,result.path,result.commandLabel],['Issue reviewed adjustment note',false,'gas/bills/issue','Gas adjustment note']);
 assert.deepEqual(result.body,{preview:result.asked,previewHash:'reviewed',contentDigest:'digest',expectedPreviousId:'bill',evidence:'Synthetic note review'});
}));

test('an incomplete month names its missing payment days and offers nothing to issue; a view that cannot write cannot review',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{${mount}
  const waiting={state:'incomplete',missing:${JSON.stringify(view.months[1].missing)},document:null,previousId:null,previewHash:null,contentDigest:null};
  const api=async path=>path==='gas/bills/read'?${JSON.stringify(view)}:waiting,workflow={busy:()=>false,pending:()=>undefined,queue:async()=>{},changed(){}};
  const widget=setupGasBills(document.querySelector('#host'),{api,context:()=>({}),tradeId:'synthetic',actor:()=> 'operator',zone:()=> 'UTC',workflow});
  await tick();const root=document.querySelector('.gas-bills');${choose}
  await pick('month','2021-02-01');await pick('scope','gas_trade_month_gross_v1');await pick('evidence','Synthetic review');
  root.querySelector('form').requestSubmit();await tick();
  const message=root.querySelector('[data-bills=message]').textContent,confirmHidden=root.querySelector('[data-bills=confirm]').hidden,reviewHidden=root.querySelector('[data-bills=review]').hidden;
  widget.destroy();const closed=async()=>({...${JSON.stringify(view)},canWrite:false});
  setupGasBills(document.querySelector('#host'),{api:closed,context:()=>({}),tradeId:'synthetic',actor:()=> 'operator',zone:()=> 'UTC',workflow});await tick();
  return {message,confirmHidden,reviewHidden,readonly:document.querySelector('[data-bills=month]').disabled,said:document.querySelector('[data-bills=message]').textContent};
 })()`);
 assert.match(result.message,/2 payment days have no determination: payment 2021-02-02, fee 2021-02-02/);
 assert.deepEqual([result.confirmHidden,result.reviewHidden,result.readonly],[true,true,true]);assert.match(result.said,/read only/);
}));

test('a review is withdrawn by an edit, by a later review that offers no document, and by a refresh; blank evidence is not reviewed',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{${mount}let answer=${JSON.stringify(note)},previews=0,sent=0;
  const api=async path=>{if(path==='gas/bills/read')return ${JSON.stringify(view)};previews++;if(answer instanceof Error)throw answer;return answer;};
  const widget=setupGasBills(document.querySelector('#host'),{api,context:()=>({}),tradeId:'synthetic',actor:()=> 'operator',zone:()=> 'UTC',workflow:{busy:()=>false,pending:()=>undefined,changed(){},async queue(){sent++;}}});
  await tick();const root=document.querySelector('.gas-bills'),confirm=root.querySelector('[data-bills=confirm]'),form=root.querySelector('form');${choose}
  const review=async()=>{form.requestSubmit();await tick();return !confirm.hidden;};
  await pick('month','2021-01-01');await pick('scope','gas_trade_month_gross_v1');await pick('evidence','   ');const blank=await review(),blankPreviews=previews;
  await pick('evidence','Synthetic review');const first=await review();
  await pick('evidence','Synthetic review, edited');const edited=!confirm.hidden;
  const again=await review();answer={state:'issued',missing:[],document:null,previousId:'bill',previewHash:null,contentDigest:null};
  const nothing=await review(),nothingText=root.querySelector('[data-bills=message]').textContent;confirm.click();await tick();
  answer=${JSON.stringify(note)};const third=await review();answer=Error('synthetic refusal');const failed=await review();
  answer={state:'synthetic_state',missing:[],document:null,previousId:null,previewHash:null,contentDigest:null};await review();const other=root.querySelector('[data-bills=message]').textContent;
  answer=${JSON.stringify(note)};const fourth=await review();await widget.refresh(false);
  const kept=[root.querySelector('[data-bills=month]').value,root.querySelector('[data-bills=evidence]').value,!confirm.hidden];
  await widget.refresh(true);
  const cleared=[root.querySelector('[data-bills=month]').value,root.querySelector('[data-bills=evidence]').value,!confirm.hidden];
  return {blank,blankPreviews,first,edited,again,nothing,nothingText,third,failed,other,fourth,kept,cleared,sent};
 })()`);
 assert.deepEqual([result.blank,result.blankPreviews],[false,0]);
 assert.deepEqual([result.first,result.edited,result.again,result.nothing,result.third,result.failed,result.fourth],[true,false,true,false,true,false,true]);
 assert.match(result.nothingText,/already on a document/);assert.match(result.other,/synthetic_state/);
 assert.deepEqual(result.kept,['2021-01-01','Synthetic review, edited',false]);assert.deepEqual(result.cleared,['','',false]);
 assert.equal(result.sent,0);
}));
