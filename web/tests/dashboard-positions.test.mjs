import test from 'node:test';
import assert from 'node:assert/strict';
import {deltaLadder} from '../dist/delta-ladder.js';
import {positionsView,runNowOffered,noRunYet,sizeUnits,definitionTerms,barWidths,readLatest} from '../dist/dashboard-positions.js';
import {setCurveNames,curveName} from '../dist/market-view.js';

setCurveNames([{code:'future',name:'Gas futures'}]);
const amounts=(delta,up,down)=>({base:'1',up:'2',down:'0',upPnl:up,downPnl:down,delta,gamma:'0'});
const values=delta=>({...amounts(delta,delta,'-'+delta),display:amounts('d:'+delta,'u:'+delta,'n:'+delta),currency:'USD',currencyReference:{code:'usd'},deltaUnit:'USD / (1 USD / MMBtu (IT))',gammaUnit:'USD'});
const factor=(id,from,to,delta)=>({factor:{id,label:id,coordinate:'market_quote',kind:'contract',unit:'1 USD / MMBtu (IT)',curves:['future'],deliveryFrom:from,deliveryTo:to},bump:'0.05',
  totals:[{bookId:'b1',book:'Book A',measure:'futures_execution_pnl',measureLabel:'Futures execution P&L',values:values(delta)}],positions:[]});
const result=factors=>({factors,excludedPositions:[],scenario:{depth:0,isBase:true},context:{contextId:'c1'},snapshotId:'s1'});
const parts=[{result:result([factor('jan','2021-01-01','2021-02-01','200')])},{result:result([factor('feb','2021-02-01','2021-03-01','-80')])}];
const run={runId:'r1',version:1,valuationAt:'2021-01-15T21:00:00Z',knownAt:'2021-01-15T21:05:00Z',parts};

test('a latest run draws the ladder from its parts unchanged',()=>{
  const view=positionsView({state:'latest',run,newer:null,incomplete:null});
  assert.deepEqual(view.ladder,deltaLadder(parts.map(p=>p.result),'delta',curveName));
  assert.deepEqual(view.freshness,{state:'latest',observedAt:run.valuationAt,source:'Risk run'});
  assert.equal(view.newer,null);assert.equal(view.incomplete,null);
});

test('newer_available and an incomplete newer run are both stated',()=>{
  const newer={runId:'r2',valuationAt:'2021-01-16T21:00:00Z'},incomplete={runId:'r3',valuationAt:'2021-01-15T22:00:00Z',failedParts:[2]};
  const view=positionsView({state:'newer_available',run,newer,incomplete});
  assert.equal(view.freshness.state,'newer_available');assert.deepEqual(view.newer,newer);assert.deepEqual(view.incomplete,incomplete);
});

test('a tie lists the tied runs and draws nothing',()=>{
  const runs=[{runId:'r1',valuationAt:'2021-01-15T21:00:00Z'},{runId:'r2',valuationAt:'2021-01-15T21:00:00Z'}];
  assert.deepEqual(positionsView({state:'tie',runs}),{tie:runs});
});

test('a 409 risk_run_tie from the latest read is a tie with its runs; other refusals are not hidden',async()=>{
  const runs=[{runId:'r1',valuationAt:'x'},{runId:'r2',valuationAt:'x'}];
  const tied=await readLatest(async()=>{throw Object.assign(Error('tie'),{code:'risk_run_tie',details:{runs}});},'d1',{});
  assert.deepEqual(tied,{state:'tie',runs});
  await assert.rejects(readLatest(async()=>{throw Object.assign(Error('denied'),{code:'access_denied'});},'d1',{}),/denied/);
});

test('none reads "No completed run yet" with Run now offered',()=>{
  const view=positionsView({state:'none'});
  assert.deepEqual(view,{none:true});assert.equal(noRunYet,'No completed run yet');assert.equal(runNowOffered(false),true);
});

test('historical context hides Run now, and labels the figures historical',()=>{
  assert.equal(runNowOffered(true),false);
  assert.equal(positionsView({state:'latest',run,newer:null,incomplete:null},true).freshness.state,'historical');
});

test('a part without a result, or a ladder the backend contradicts, is a refusal and not a partial ladder',()=>{
  assert.match(positionsView({state:'latest',run:{...run,parts:[parts[0],{}]},newer:null,incomplete:null}).refusal,/result/);
  assert.match(positionsView({state:'latest',run:{...run,parts:[parts[0],parts[0]]},newer:null,incomplete:null}).refusal,/more than once/);
});

const unit=(code,multiplier='1')=>({currency:{revision:{catalogId:'c',revisionId:'r'},code:'usd'},quantity:{revision:{catalogId:'c',revisionId:'r'},code},multiplier});
const targets=[{id:'t1',unit:'USD / MMBtu',priceUnit:unit('mmbtu')},{id:'t2',unit:'USD / MWh',priceUnit:unit('mwh')},{id:'t3',unit:'USD / MMBtu',priceUnit:unit('mmbtu')}];

test('sizes are asked for once per exact price unit, in the order the targets name them',()=>{
  assert.deepEqual(sizeUnits(targets).map(u=>u.label),['USD / MMBtu','USD / MWh']);
  assert.deepEqual(sizeUnits(targets).map(u=>u.priceUnit),[targets[0].priceUnit,targets[1].priceUnit]);
  assert.throws(()=>sizeUnits([{id:'x',unit:'USD / MWh'}]),/price unit/);
});

test('the same label on two different exact units asks for two sizes',()=>{
  assert.equal(sizeUnits([targets[0],{...targets[2],priceUnit:unit('mmbtu','1000')}]).length,2);
});

test('a definition is saved with a size for every unit and the exact references; a missing size is refused, never defaulted',()=>{
  const units=sizeUnits(targets);
  const terms=definitionTerms({name:' Held ',bookIds:['b1'],preset:'held_contracts',units,sizes:units.map(()=> ' 0.1 ')});
  assert.deepEqual(terms,{name:'Held',bookIds:['b1'],preset:'held_contracts',sizes:units.map(u=>({unit:u.priceUnit,size:'0.1'}))});
  assert.throws(()=>definitionTerms({name:'Held',bookIds:['b1'],preset:'held_contracts',units,sizes:['0.1','']}),/USD \/ MWh/);
  assert.throws(()=>definitionTerms({name:'',bookIds:['b1'],preset:'held_contracts',units,sizes:['1','1']}),/name/i);
  assert.throws(()=>definitionTerms({name:'Held',bookIds:[],preset:'held_contracts',units,sizes:['1','1']}),/book/);
});

test('bars are each cell\'s exact amount scaled to the largest in its column',()=>{
  assert.deepEqual(barWidths(['200','-80','0']),[1,0.4,0]);
  assert.deepEqual(barWidths(['0','0']),[0,0]);
  assert.deepEqual(barWidths([null,'5']),[0,1]);
});
