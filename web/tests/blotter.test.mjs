import test from 'node:test';
import assert from 'node:assert/strict';
import {parseBlotterViews,newBlotterView} from '../dist/blotter-layout.js';
import {guardedCsv} from '../dist/csv-export.js';
test('named views retain layout and filters without retaining market inputs or permissions',()=>{
 const view={...newBlotterView(),name:'Book review',columns:['book','contract','executionPnl'],query:'Jan',columnFilters:{book:'Book A'}};
 assert.deepEqual(parseBlotterViews(JSON.stringify([{...view,snapshotId:'private',role:'admin'}])),[view]);
});
test('saved views reject empty columns duplicates unknown fields and unsupported pivot rules',()=>{
 const base=newBlotterView();
 for(const patch of [{columns:[]},{columns:['book','book']},{columns:['unknown']},{columns:['tradeCount']},{mode:'guess'},{groupBy:['book','book']},{mode:'pivot',groupBy:['book'],measures:['lots']}])assert.throws(()=>parseBlotterViews(JSON.stringify([{...base,...patch}])));
 assert.throws(()=>parseBlotterViews(JSON.stringify(Array.from({length:21},()=>base))));
});
test('snapshot export retains exact decimal strings and guards textual spreadsheet formulas',()=>{
 const csv=guardedCsv([['Book','Quantity','Execution P&L'],['=untrusted','-40','0.000000000000000001'],['A,"B"','80','-4']],[1,2]);
 assert.ok(csv.includes("\"'=untrusted\""));assert.ok(csv.includes('"-40","0.000000000000000001"'));assert.ok(csv.includes('"A,""B""","80","-4"'));
});
test('new trade layouts expose stable references and revision without changing existing saved layouts',()=>{
 const view=newBlotterView();
 for(const key of ['tradeReference','instrumentReference','revision'])assert.ok(view.columns.includes(key));
 const legacy={...view,columns:['book','lots','executionPnl']};
 assert.deepEqual(parseBlotterViews(JSON.stringify([legacy]))[0].columns,legacy.columns);
});

test('price pivot layouts retain explicit statistics, units and exact arithmetic filters',()=>{
 const view={...newBlotterView(),mode:'pivot',groupBy:['book'],measures:['fixedPriceMean'],columns:['book','fixedPriceMean','unit','currency','priceUnit'],columnFilters:{fixedPriceMean:'>= 3.000000000000000001'}};
 assert.deepEqual(parseBlotterViews(JSON.stringify([view]))[0],view);
 assert.throws(()=>parseBlotterViews(JSON.stringify([{...view,columns:view.columns.filter(k=>k!=='priceUnit')}])));
 assert.throws(()=>parseBlotterViews(JSON.stringify([{...newBlotterView(),columns:['fixedPriceMean']}])));
});

test('chart layouts retain plot style and measures while older views upgrade presentation only',()=>{
 const view={...newBlotterView(),version:3,chart:{visible:true,measure:'presentValue',kind:'heatmap'}};
 assert.deepEqual(parseBlotterViews(JSON.stringify([view]))[0],view);
 const legacy={...view,version:2,chart:{visible:true,measure:'executionPnl'}};
 const restored=parseBlotterViews(JSON.stringify([legacy]))[0];
 assert.deepEqual(restored.chart,{visible:true,measure:'executionPnl',kind:'bar'});
 assert.deepEqual(restored.bookIds,legacy.bookIds);assert.equal(restored.version,3);
 for(const chart of [{...view.chart,measure:'guessed'},{...view.chart,kind:'pie'}])assert.throws(()=>parseBlotterViews(JSON.stringify([{...view,chart}])));
});
