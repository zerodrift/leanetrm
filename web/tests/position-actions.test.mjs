import {test} from 'node:test';
import assert from 'node:assert/strict';
import {adjustLabels,adjustUnit,positionActions} from '../dist/position-actions.js';

const future=lots=>({instrumentType:'future',lots});
const all={book:true,amend:true,risk:true};
const ids=['closeOut','amend','increase','reduce','risk','terminate','novate'];

test('a position\'s actions are listed in the order a trader reads them, the unsupported events last and marked as not yet offered',()=>{
  const actions=positionActions(future('40'),all);
  assert.deepEqual(actions.map(a=>a.id),ids);
  assert.deepEqual(actions.map(a=>a.label),['Close out…','Amend','Buy more…','Sell…','Risk…','Terminate','Novate']);
  assert.deepEqual(actions.map(a=>a.state.kind),['enabled','enabled','enabled','enabled','enabled','soon','soon']);
});

test('the adjustments are named from the direction the position states, and neutrally where it states none',()=>{
  assert.deepEqual(adjustLabels(future('40')),{increase:'Buy more…',reduce:'Sell…'});
  assert.deepEqual(adjustLabels(future('-15')),{increase:'Sell more…',reduce:'Buy back…'});
  assert.deepEqual(adjustLabels({instrumentType:'financial_swap',lots:null}),{increase:'Increase…',reduce:'Reduce…'});
  assert.deepEqual(adjustLabels(future(null)),{increase:'Increase…',reduce:'Reduce…'},'a future that states no lots is not guessed long');
  assert.deepEqual(adjustLabels({instrumentType:'physical_gas',lots:'5'}),{increase:'Increase…',reduce:'Reduce…'});
});

test('an adjustment\'s quantity is asked in lots where the position states lots, and in its own unit otherwise',()=>{
  assert.equal(adjustUnit({lots:'40',unit:'MMBtu'}),'lots');
  assert.equal(adjustUnit({lots:null,unit:'MWh'}),'MWh');
});

test('an action the account may not take is listed with its reason: booking\'s for the drafts, each other its own',()=>{
  const why={book:'Return to working data before booking or amending.',amend:'This account cannot amend trades in this market context.',risk:'Your role cannot run scenarios.'};
  const actions=positionActions(future('40'),{book:false,amend:false,risk:false,why});
  assert.deepEqual(actions.map(a=>a.state.kind==='disabled'?a.state.reason:a.state.kind),[why.book,why.amend,why.book,why.book,why.risk,'soon','soon']);
});

test('a row whose values are not current offers no action, each saying why, and the events not yet offered stay so',()=>{
  const stale='Positions open once the Blotter has valued them.';
  assert.deepEqual(positionActions(future('40'),{...all,stale}).map(a=>a.state.kind==='disabled'?a.state.reason:a.state.kind),[stale,stale,stale,stale,stale,'soon','soon']);
});

test('a refused action without its reason is an error, never a silent default',()=>{
  for(const can of [{...all,book:false},{...all,amend:false,why:{book:'x'}},{...all,risk:false,why:{}}])
    assert.throws(()=>positionActions(future('40'),can),/reason/);
});
