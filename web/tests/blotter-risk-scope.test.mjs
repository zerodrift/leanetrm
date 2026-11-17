import {test} from 'node:test';
import assert from 'node:assert/strict';
import {blotterRiskScope,outsideRunScope,sameRiskScope,scopeChangeNotice,tradeScopeNote} from '../dist/risk-scope.js';

const view={bookIds:['b1'],portfolioIds:['p1']},context={snapshotId:'s',knowledgeMode:'current'};

test('the Blotter asks for risk on the books on screen, and names a trade only when one was asked for',()=>{
  const held=()=>true,desk=blotterRiskScope(context,view,null,held),one=blotterRiskScope(context,view,{tradeId:'t1',reference:'TRD-7'},held);
  assert.deepEqual(desk,{context,bookIds:['b1'],portfolioIds:['p1']});
  assert.equal('tradeIds' in desk,false,'every trade is stated by naming none');
  assert.deepEqual(one,{context,bookIds:['b1'],portfolioIds:['p1'],tradeIds:['t1']});
  assert.notEqual(desk.context,context,'the scope is its own copy');
  assert.equal(sameRiskScope(desk,one),false,'a run for one trade is not the run for its books');
});

test('a position is outside a run only when the run names trades and not this one',()=>{
  assert.equal(outsideRunScope({tradeIds:['t1']},'t2'),true);
  assert.equal(outsideRunScope({tradeIds:['t1']},'t1'),false);
  for(const whole of [undefined,{},{tradeIds:null}])assert.equal(outsideRunScope(whole,'t2'),false);
});

test('a run that names trades says what it covers, by the trade\'s reference where the page knows it',()=>{
  const reference=id=>id==='t1'?'TRD-7':undefined;
  assert.equal(tradeScopeNote({tradeIds:['t1']},reference),'This run covers trade TRD-7 only; no other position is in it.');
  assert.equal(tradeScopeNote({tradeIds:['t9']},reference),'This run covers one chosen trade only; no other position is in it.');
  assert.equal(tradeScopeNote({tradeIds:['t1','t2']},reference),'This run covers 2 chosen trades only; no other position is in it.');
  for(const whole of [undefined,{},{tradeIds:null}])assert.equal(tradeScopeNote(whole,reference),null);
});

test('a trade that is no longer among the positions on screen is refused by name, never widened to its books',()=>{
  assert.throws(()=>blotterRiskScope(context,view,{tradeId:'t1',reference:'TRD-7'},id=>id!=='t1'),/Trade TRD-7 is no longer among the positions on screen\. Choose Run risk to set up risk for the books on screen\./);
});

test('changing what risk is set up for says so when it takes a run off the desk, and says nothing when there was none',()=>{
  assert.equal(scopeChangeNotice(true,{reference:'TRD-7'}),'The risk run on the desk was cleared: risk is now set up for trade TRD-7 only.');
  assert.equal(scopeChangeNotice(true,null),'The risk run on the desk was cleared: risk is now set up for the books on screen.');
  assert.equal(scopeChangeNotice(false,{reference:'TRD-7'}),null);
});
