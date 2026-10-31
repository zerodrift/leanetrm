import {test} from 'node:test';
import assert from 'node:assert/strict';
import {closeOutPriceLabel,closeOutRefusal,closeOutRequest,priceSourceNote,readCloseOutDraft} from '../dist/close-out-draft.js';
import {createBlotterOperations} from '../dist/blotter-operations.js';

const price={source:'curve_price',closeAt:'2020-12-18T08:00:00Z',closeLabel:'18 Dec 2020 08:00 UTC'};
const future={kind:'future',canBook:true,context:{contextId:'c1'},terms:{bookId:'b',curve:'future',contractId:'k',lots:'-40',executionPrice:'2.95'},price:{...price,value:'2.95',unit:'USD/MMBtu'}};
const swap={kind:'financial_swap',canBook:true,context:{contextId:'c1'},terms:{swapType:'scheduled_financial_swap'},settledPeriodsExcluded:2,price,
  periods:[{from:'2021-03-01',to:'2021-04-01',quantity:'1000/3',price:'3.1'},{from:'2021-04-01',to:'2021-05-01',quantity:'400',price:'3.2'}]};

test('the price is labelled with the close it was read at, in the zone the market date selector shows',()=>{
  assert.equal(closeOutPriceLabel(price,'UTC'),'Curve price at Dec 18, 2020, 8:00 AM close');
  assert.equal(closeOutPriceLabel(price,'America/New_York'),'Curve price at Dec 18, 2020, 3:00 AM close');
});

test('a close the browser cannot read as an instant is labelled as the backend labels it',()=>{
  assert.equal(closeOutPriceLabel({...price,closeAt:'not an instant'},'UTC'),'Curve price at 18 Dec 2020 08:00 UTC close');
});

test('the draft of a position is read for its revision in the context of the rows on screen',()=>{
  assert.deepEqual(closeOutRequest({snapshotId:'s',contextId:'c'},{tradeId:'t',bindingId:'b',revision:'3',instrumentType:'future'}),
    {context:{snapshotId:'s',convention:'trading_workspace_v1',knowledgeMode:'retained',contextId:'c'},tradeId:'t',bindingId:'b',revision:3,instrumentType:'future'});
});

test('a draft is the instrument of the position it closes, its periods exact as the backend states them',()=>{
  assert.equal(readCloseOutDraft(future,'future').terms.lots,'-40');
  const read=readCloseOutDraft(swap,'financial_swap');
  assert.deepEqual(read.periods.map(p=>p.quantity),['1000/3','400']);
  assert.equal(read.settledPeriodsExcluded,2);
  assert.throws(()=>readCloseOutDraft(future,'financial_swap'),/another instrument/);
  assert.throws(()=>readCloseOutDraft(swap,'future'),/another instrument/);
  assert.throws(()=>readCloseOutDraft({...future,price:{...future.price,source:'mark'}},'future'),/curve price/);
  assert.throws(()=>readCloseOutDraft({...future,terms:{...future.terms,executionPrice:''}},'future'),/lots and price/);
  assert.throws(()=>readCloseOutDraft({...swap,periods:[{...swap.periods[0],quantity:0.333}]},'financial_swap'),/period/);
  assert.throws(()=>readCloseOutDraft({...swap,periods:[]},'financial_swap'),/period/);
});

test('only the backend’s close-out refusal is said in the details; it still rejects, marked shown so no banner repeats it',async()=>{
  const said=closeOutRefusal(Object.assign(Error('Close-out drafts are not available for ppa yet.'),{code:'close_out'}),'PPA-1');
  assert.equal(said.message,'Close-out of PPA-1 was not drafted. Close-out drafts are not available for ppa yet.');
  assert.equal(said.code,'close_out');assert.equal(said.shown,true);
  for(const other of [Object.assign(Error('The app could not complete that request.'),{code:'request_failed'}),Error('The close-out draft is of another instrument than the position it closes.'),'text'])
    assert.equal(closeOutRefusal(other,'PPA-1'),undefined);
  // A caller of the Blotter's operations hears the refusal as a rejection.
  const handlers={draftCloseOut:async()=>{throw said;}};
  await assert.rejects(createBlotterOperations(handlers,{busy:()=>false})({kind:'draftCloseOut',key:'71000000-0000-4000-8000-000000000009:72000000-0000-4000-8000-000000000008'},{placed:false}),error=>error===said);
});

// A form that hears its listeners' events, and a note that holds what it is shown.
function fakeForm(){const listeners=[];return {addEventListener:(type,listener)=>listeners.push([type,listener]),fire:(type,target)=>{for(const [t,l] of listeners)if(t===type)l({target});}};}
test('the price label names its close in the zone shown, again when the zone changes, until a price is edited',()=>{
  let zone='UTC';const form=fakeForm(),note={hidden:true,textContent:''},price1={},other={};
  const source=priceSourceNote(form,target=>target===price1,note,()=>zone);
  source.show(price);
  assert.deepEqual([note.hidden,note.textContent],[false,'Curve price at Dec 18, 2020, 8:00 AM close']);
  zone='America/New_York';source.refresh();
  assert.equal(note.textContent,'Curve price at Dec 18, 2020, 3:00 AM close');
  form.fire('input',other);assert.equal(note.hidden,false,'an edit elsewhere keeps the label');
  form.fire('change',price1);assert.deepEqual([note.hidden,note.textContent],[true,'']);
  source.refresh();assert.equal(note.hidden,true,'a cleared label stays cleared when the zone changes');
});
