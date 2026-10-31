import test from 'node:test';
import assert from 'node:assert/strict';
import {parseBlotterViews,readSavedViews,newBlotterView,sidebarViews,defaultBlotterArrangement,pivotColumns,withoutColumns,deskFields} from '../dist/blotter-layout.js';
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
test('a new trade view shows the desk columns, and instrument references and revision stay optional columns (D91)',()=>{
 const view=newBlotterView();
 assert.deepEqual(view.columns,[...deskFields]);assert.ok(view.columns.includes('tradeReference'));
 const detailed={...view,columns:[...deskFields,'instrumentReference','revision','counterparty']};
 assert.deepEqual(parseBlotterViews(JSON.stringify([detailed]))[0].columns,detailed.columns);
 const chosen={...view,columns:['book','lots','executionPnl']};
 assert.deepEqual(parseBlotterViews(JSON.stringify([chosen]))[0].columns,chosen.columns);
});

test('price pivot layouts retain explicit statistics, units and exact arithmetic filters',()=>{
 const view={...newBlotterView(),mode:'pivot',groupBy:['book'],measures:['fixedPriceMean'],columns:['book','fixedPriceMean','unit','currency','priceUnit'],columnFilters:{fixedPriceMean:'>= 3.000000000000000001'}};
 assert.deepEqual(parseBlotterViews(JSON.stringify([view]))[0],view);
 assert.throws(()=>parseBlotterViews(JSON.stringify([{...view,columns:view.columns.filter(k=>k!=='priceUnit')}])));
 assert.throws(()=>parseBlotterViews(JSON.stringify([{...newBlotterView(),columns:['fixedPriceMean']}])));
});

test('a saved view may group by commodity and show it beside trade rows',()=>{
 const pivot={...newBlotterView(),mode:'pivot',groupBy:['commodity'],measures:['tradeCount'],columns:['commodity','tradeCount','instrumentType','unit','currency']};
 assert.deepEqual(parseBlotterViews(JSON.stringify([pivot]))[0],pivot);
 const rows={...newBlotterView(),columns:['commodity','market','mark']};
 assert.deepEqual(parseBlotterViews(JSON.stringify([rows]))[0].columns,['commodity','market','mark']);
});

test('chart layouts retain plot style and measures',()=>{
 const view={...newBlotterView(),chart:{visible:true,measure:'presentValue',kind:'heatmap'}};
 assert.deepEqual(parseBlotterViews(JSON.stringify([view]))[0],view);
 for(const chart of [{...view.chart,measure:'guessed'},{...view.chart,kind:'pie'}])assert.throws(()=>parseBlotterViews(JSON.stringify([{...view,chart}])));
});
test('columns shown for one read leave no trace in the layout a view saves',()=>{
 const layout={order:['book','unvaluedPositions','presentValue'],widths:{book:120,unvaluedPositions:90},sort:[{key:'unvaluedPositions',direction:'desc'},{key:'book',direction:'asc'}]};
 assert.deepEqual(withoutColumns(layout,new Set(['unvaluedPositions'])),{order:['book','presentValue'],widths:{book:120},sort:[{key:'book',direction:'asc'}]});
 assert.deepEqual(withoutColumns(layout,new Set()),layout);
});

const earlierVersion='Saved blotter view is from an earlier version.';
// What earlier releases stored: version 4 had no sidebar choice, version 3 no row grouping, version 2 no plot style, version 1 no grid,
// column split or chart.
const stored=(()=>{
 const {panes,...v5}={...newBlotterView(),version:5};
 const {sidebar,...v4}={...v5,version:4};
 const {rowGroup,...v3}={...v4,version:3};
 const v2={...v3,version:2,chart:{visible:false,measure:'quantity'}};
 const {splitBy,grid,chart,...v1}={...v2,version:1};
 return [v1,v2,v3,v4,v5];
})();
test('a new view groups its trade rows by book and is not listed in the sidebar',()=>{
 const view=newBlotterView();
 assert.equal(view.version,6);assert.equal(view.rowGroup,'book');assert.equal(view.sidebar,false);
});
const group=(tab,tabs=[tab])=>({kind:'group',tabs,active:tab});
const split=(ratio,a,b,direction='column')=>({kind:'split',direction,ratio,children:[a,b]});
test('a new view starts with the default pane arrangement',()=>{
 assert.deepEqual(newBlotterView().panes,defaultBlotterArrangement());
 assert.deepEqual(defaultBlotterArrangement(),{layout:split(0.06,group('summary'),split(0.6,group('grid'),split(0.5,group('details'),group('charts')))),collapsed:['details']});
 assert.deepEqual(parseBlotterViews(JSON.stringify([newBlotterView()])),[newBlotterView()]);
});
test('a saved view keeps a custom pane arrangement',()=>{
 const panes={layout:split(0.3,group('grid'),group('summary',['summary','details','charts']),'row'),collapsed:['charts','details']};
 const view={...newBlotterView(),panes};
 assert.deepEqual(parseBlotterViews(JSON.stringify([view])),[view]);
});
test('a pane arrangement the Blotter cannot show is refused, not repaired',()=>{
 const [summary,grid,details,charts]=['summary','grid','details','charts'].map(t=>group(t));
 const full=defaultBlotterArrangement().layout;
 const bad=[
  {layout:split(0.5,summary,split(0.5,grid,details)),collapsed:[]},
  {layout:split(0.5,summary,split(0.5,grid,split(0.5,details,group('reports')))),collapsed:[]},
  {layout:split(0.5,summary,split(0.5,grid,split(0.5,details,group('summary')))),collapsed:[]},
  {...defaultBlotterArrangement(),collapsed:['grid']},
  {...defaultBlotterArrangement(),collapsed:['details','details']},
  {...defaultBlotterArrangement(),collapsed:['reports']},
  {layout:split(0.5,split(0.5,split(0.5,split(0.5,summary,grid),details),charts),group('x')),collapsed:[]},
  {layout:{...full,ratio:0.97},collapsed:[]},
  {layout:{...full,ratio:0.04},collapsed:[]},
  {layout:{kind:'group',tabs:['summary','grid','details','charts'],active:'reports'},collapsed:[]},
  {layout:full},null,'panes'];
 for(const panes of bad)assert.throws(()=>parseBlotterViews(JSON.stringify([{...newBlotterView(),panes}])),{message:'Invalid saved pane arrangement.'},JSON.stringify(panes));
 const {panes,...without}=newBlotterView();
 assert.throws(()=>parseBlotterViews(JSON.stringify([without])),{message:'Invalid saved pane arrangement.'});
});
test('a saved view states whether the sidebar lists it, and the sidebar lists those views in their saved order',()=>{
 const listed={...newBlotterView(),name:'Gas desk',sidebar:true},unlisted={...newBlotterView(),name:'Scratch'},power={...newBlotterView(),name:'Power desk',sidebar:true};
 assert.deepEqual(parseBlotterViews(JSON.stringify([listed,unlisted])),[listed,unlisted]);
 assert.deepEqual(sidebarViews([listed,unlisted,power]),['Gas desk','Power desk']);
 for(const sidebar of [undefined,null,'true',1])assert.throws(()=>parseBlotterViews(JSON.stringify([{...newBlotterView(),sidebar}])),{message:'Invalid saved blotter view.'});
});
test('a saved view records its desk grouping, or none for a flat list',()=>{
 for(const rowGroup of ['book','market','instrumentType',null]){
  const view={...newBlotterView(),rowGroup};
  assert.deepEqual(parseBlotterViews(JSON.stringify([view])),[view]);
 }
});
test('a desk grouping the Blotter does not offer is refused, and so is leaving it out',()=>{
 for(const rowGroup of ['portfolio','contract','commodity','','Book',0,false,['book']])assert.throws(()=>parseBlotterViews(JSON.stringify([{...newBlotterView(),rowGroup}])),{message:'Invalid saved view choices.'});
 const {rowGroup,...unrecorded}=newBlotterView();
 assert.throws(()=>parseBlotterViews(JSON.stringify([unrecorded])),{message:'Invalid saved view choices.'});
});
test('views saved in an earlier format are unavailable and never migrated',()=>{
 for(const old of stored)assert.throws(()=>parseBlotterViews(JSON.stringify([old])),{message:earlierVersion});
 // Whatever else the layout holds, the version it claims decides.
 assert.throws(()=>parseBlotterViews(JSON.stringify([{...newBlotterView(),version:4}])),{message:earlierVersion});
 // A version this release does not know is invalid, not an earlier one.
 for(const version of [0,7,'6',null])assert.throws(()=>parseBlotterViews(JSON.stringify([{...newBlotterView(),version}])),{message:'Invalid saved blotter view.'});
});
test('the saved views a person sees are the current ones, with the earlier ones named as unavailable',()=>{
 const current={...newBlotterView(),name:'Current review'};
 const earlier=stored.map((view,i)=>({...view,name:'Earlier review '+(i+1)}));
 assert.deepEqual(readSavedViews([earlier[0],current,...earlier.slice(1)]),{views:[current],earlier:earlier.map(v=>v.name)});
 // Any other layout this release cannot read still fails the whole read.
 assert.throws(()=>readSavedViews([current,{...newBlotterView(),name:'Broken',rowGroup:'portfolio'}]),{message:'Invalid saved view choices.'});
 assert.throws(()=>readSavedViews([{...newBlotterView(),version:7}]),{message:'Invalid saved blotter view.'});
});
test('a pivot by instrument type lists each of its columns once and saves as a view',()=>{
 assert.deepEqual(pivotColumns(['instrumentType','book'],['tradeCount']),['instrumentType','book','tradeCount','unit','currency']);
 assert.deepEqual(pivotColumns(['book'],['fixedPriceMean','presentValue']),['book','fixedPriceMean','presentValue','instrumentType','unit','currency','priceUnit']);
 const view={...newBlotterView(),mode:'pivot',groupBy:['instrumentType'],measures:['tradeCount'],columns:pivotColumns(['instrumentType'],['tradeCount'])};
 assert.deepEqual(parseBlotterViews(JSON.stringify([view]))[0],view);
});
