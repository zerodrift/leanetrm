import test from 'node:test';
import assert from 'node:assert/strict';
import {futuresContractDescription} from '../dist/futures-contract.js';
import {setCurveNames} from '../dist/market-view.js';

const contract={curve:'synthetic_future',name:'Synthetic February contract',contractSize:'100',unit:'MWh',nominalSize:'100',nominalUnit:'MWh',lotBasis:'fixed_total',settlementMethod:'cash',mark:'12.5',quoteMultiplier:'1',currency:'USD',tick:'0.1',clearingIncrement:'0.1',minimumPrice:'-1000',maximumPrice:'1000',firstTrading:null,eligibleFrom:'2026-01-05T09:00:00Z',lastTrading:'2026-01-30T17:00:00Z',eligibilityNote:'Explicit sample trading eligibility; generated marks.'};
setCurveNames([{code:contract.curve,name:'Synthetic futures'}]);

test('unknown actual first trading stays unknown beside explicit sample eligibility',()=>{
  const text=futuresContractDescription(contract,'UTC');
  assert.match(text,/Actual first trading: Source time unavailable\./);
  assert.match(text,/Trading eligibility interval Jan 5, 2026, 9:00 AM to Jan 30, 2026, 5:00 PM/);
  assert.match(text,/Explicit sample trading eligibility; generated marks\./);
  assert.doesNotMatch(text,/1970/);
});

test('actual first trading and a different eligibility start remain separate facts',()=>{
  const text=futuresContractDescription({...contract,firstTrading:'2025-12-01T08:30:00Z'},'UTC');
  assert.match(text,/Actual first trading: Dec 1, 2025, 8:30 AM\./);
  assert.match(text,/Trading eligibility interval Jan 5, 2026, 9:00 AM/);
});

test('missing explicit eligibility never borrows the actual first trading time',()=>{
  const {eligibleFrom,...missing}=contract;
  assert.throws(()=>futuresContractDescription({...missing,firstTrading:'2025-12-01T08:30:00Z'},'UTC'));
});

test('contract description keeps unknown exchange first trading distinct from execution eligibility and generated marks',()=>{
 setCurveNames([{code:'gas',name:'Gas'}]);
 const text=futuresContractDescription({curve:'gas',name:'Contract',contractSize:'10000',unit:'MMBtu',nominalSize:'10000',nominalUnit:'MMBtu',lotBasis:'fixed_total',settlementMethod:'physical',mark:'3.2',quoteMultiplier:'1',currency:'USD',tick:'0.001',clearingIncrement:'0.001',minimumPrice:'-100',maximumPrice:'100',firstTrading:null,firstTradingDate:'2020-11-30',eligibleFrom:'2020-12-01T12:30:00Z',lastTrading:'2020-12-29T19:30:00Z',priceSource:{kind:'synthetic'},priceRole:'trader_mark'},'America/Chicago');
 assert.match(text,/First trading date Nov 30, 2020; exact time unknown/);
 assert.match(text,/Trading eligibility interval Dec 1, 2020, 6:30 AM/);
 assert.match(text,/Generated price/);
 assert.doesNotMatch(text,/1970|Invalid Date/);
});
