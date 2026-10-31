import {test} from 'node:test';
import assert from 'node:assert/strict';
import {closeOutPriceLabel,closeOutRequest,futuresSide,readCloseOutDraft} from '../dist/close-out-draft.js';

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

test('a future’s side is the sign of its drafted lots',()=>{
  assert.deepEqual(futuresSide('-40'),{side:'Sell',lots:'40'});
  assert.deepEqual(futuresSide('15'),{side:'Buy',lots:'15'});
  for(const lots of ['0','-0','','4.5','x'])assert.throws(()=>futuresSide(lots),/lots/);
});
