import test from 'node:test';
import assert from 'node:assert/strict';
import {sideDirection,swapPaymentQuote} from '../dist/swap-trade-review.js';

const quotes=[
 {currencyCode:'synthetic_usd',quantityCode:'synthetic_energy',currency:'USD',unit:'MWh',quoteMultiplier:'100'},
 {currencyCode:'synthetic_usd',quantityCode:'synthetic_energy',currency:'USD',unit:'MWh',quoteMultiplier:'1'}
];
const side=multiplier=>({payments:[{resets:[{currency:{code:'synthetic_usd'},quantity:{code:'synthetic_energy'},multiplier}]}]});
test('independent floating sides display their own contractual quote multiplier',()=>{
 assert.deepEqual(swapPaymentQuote(side('100'),quotes),{currency:'USD',unit:'MWh',quoteMultiplier:'100'});
 assert.deepEqual(swapPaymentQuote(side('1'),quotes),{currency:'USD',unit:'MWh',quoteMultiplier:'1'});
});
test('retained quote evidence stays visible when friendly labels are unavailable',()=>{
 assert.deepEqual(swapPaymentQuote(side('100'),[]),{currency:'synthetic_usd',unit:'synthetic_energy',quoteMultiplier:'100'});
});

test('a side reads Pay or Receive for the backend\'s coefficients of -1 and 1, and states any other coefficient as it is',()=>{
 assert.deepEqual(['-1','1','2','-0.5'].map(sideDirection),['Pay','Receive','coefficient 2','coefficient -0.5']);
});
