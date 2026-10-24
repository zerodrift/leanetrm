import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {summaryTiles,positionsTile,positionRows,deskSummaryViews,deskGroups,pvChartPanels,positionChartPanels,curvePoints,commodityColors} from '../dist/desk-model.js';
import {setCurveNames} from '../dist/market-view.js';
const fixture=JSON.parse(readFileSync(new URL('./fixtures/desk.json',import.meta.url),'utf8'));
setCurveNames(fixture.curveNames);
const clone=value=>structuredClone(value);
// Summaries as the workspace returns them, with one group changed.
const changed=(summaries,index,change)=>summaries.map((s,i)=>i===index?{...s,groups:change(clone(s.groups))}:s);
// Stands in for the backend's display text: it keeps the exact value's own leading sign.
const format=value=>value.replace(/^(-?)/,'$1#');

test('tiles keep measures and currencies apart and read totals from backend groups',()=>{
 const tiles=summaryTiles(fixture.summaries); // two currencies, two books
 const pv=tiles.find(t=>t.key==='swapPv');
 assert.deepEqual(pv.rows.map(r=>r.currency),['EUR','USD']);
 assert.equal(pv.rows[1].total,'220');assert.deepEqual(pv.rows[1].byBook.map(b=>b.value),['-10','230']);
});
test('a close with nothing valued says so instead of showing zero',()=>{
 const tiles=summaryTiles(fixture.allUnvalued);
 assert.equal(tiles.find(t=>t.key==='swapPv').rows[0].total,null);
 assert.equal(tiles.find(t=>t.key==='swapPv').note,'No valued positions · 12 excluded');
});
test('rows carry direction, measure and unavailable reasons from the backend',()=>{
 const [swap,future,expired,multi]=positionRows(fixture.holdings);
 assert.equal(swap.side,'Pay fixed');assert.equal(swap.measure,'PV');
 assert.equal(future.side,'+1 lot');assert.equal(future.measure,'P&L');
 assert.equal(expired.value,null);assert.equal(expired.unavailable,'Contract expired · awaiting settlement');
 assert.equal(multi.side,'Multi-side');
});

test('futures execution P&L is its own tile, with each currency row noting what it leaves out',()=>{
 const tiles=summaryTiles(fixture.summaries);
 assert.deepEqual(tiles.map(t=>t.key),['swapPv','futuresPnl']);
 const pnl=tiles[1];
 assert.deepEqual(pnl.rows.map(r=>[r.currency,r.total,r.trades,r.byBook.map(b=>[b.book,b.value])]),[['USD','-37.5',2,[['Book B','-37.5']]]]);
 assert.equal(pnl.rows[0].note,"Excludes 1 position that can't be valued at this close");
 assert.equal(pnl.note,pnl.rows[0].note);
});
test('a tile with several currencies notes each currency on its own row and never adds the counts',()=>{
 const pv=summaryTiles(fixture.summaries)[0];
 assert.deepEqual(pv.rows.map(r=>r.note),[null,"Excludes 1 position that can't be valued at this close"]);
 assert.equal(pv.note,null);
});
test('tile rows are keyed on the exact currency reference, never on its display code',()=>{
 const other={...fixture.summaries[0].groups[0].currencyReference,revision:{catalogId:'10000000-0000-0000-0000-000000000001',revisionId:'20000000-0000-0000-0000-000000000002'}};
 const summaries=changed(changed(fixture.summaries,0,groups=>[...groups,{...groups[0],currencyReference:other,presentValue:'7',tradeCount:1,unvaluedPositions:0}]),1,groups=>[...groups,{...groups[0],currencyReference:other,presentValue:'7',tradeCount:1,unvaluedPositions:0}]);
 const pv=summaryTiles(summaries)[0];
 assert.equal(pv.rows.length,3);
 assert.deepEqual(pv.rows.filter(r=>r.total==='220'||r.total==='7').map(r=>r.total).sort(),['220','7']);
 assert.equal(new Set(pv.rows.map(r=>r.currency)).size,3,'two references that share a display code are labeled apart');
});
test('a book without positions of a measure gets no split, and a book that values none says so',()=>{
 const tiles=summaryTiles(fixture.allUnvalued);
 assert.deepEqual(tiles[0].rows[0].byBook.map(b=>[b.book,b.value,b.excluded]),[['Book A',null,12]]);
 assert.deepEqual(tiles[1].rows[0].byBook.map(b=>b.book),['Book B']);
});
test('a tile with no positions of its instrument type says so',()=>{
 const summaries=changed(changed(fixture.summaries,0,groups=>groups.filter(g=>g.instrumentType!=='future')),1,groups=>groups.filter(g=>g.instrumentType!=='future'));
 const pnl=summaryTiles(summaries)[1];
 assert.deepEqual(pnl.rows,[]);assert.equal(pnl.note,'No futures in the selected books');
});
test('the desk refuses summaries it did not ask for, under another convention, or with a total that has no value and no reason',()=>{
 const message='The trading workspace did not return the desk summaries. Refresh and try again.';
 assert.throws(()=>summaryTiles(fixture.summaries.slice(1)),{message});
 const totals=fixture.summaries.map((s,i)=>i===1?{...s,view:{...s.view,pivotConvention:'trading_blotter_totals_v1'}}:s);
 assert.throws(()=>summaryTiles(totals),{message});
 assert.throws(()=>summaryTiles([...fixture.summaries,fixture.summaries[0]]),{message});
 const silent=changed(fixture.summaries,0,groups=>groups.map(g=>g.instrumentType==='future'?{...g,executionPnl:null,unvaluedPositions:0}:g));
 assert.throws(()=>summaryTiles(silent),{message:'The Blotter returned a total without a value or a reason.'});
 const counted=changed(fixture.summaries,0,groups=>groups.map(g=>({...g,unvaluedPositions:'1'})));
 assert.throws(()=>summaryTiles(counted),{message:'The Blotter returned an invalid count of positions it cannot value.'});
});
test('the desk asks for its four summaries of the view it reads, in order, under the conventions its figures need',()=>{
 const books=['d1000000-0000-0000-0000-00000000000a'];
 const views=deskSummaryViews(books,[]);
 assert.deepEqual(views.map(v=>[v.groupBy,v.measures,v.pivotConvention]),[
  [['instrumentType'],['presentValue','executionPnl','tradeCount'],'trading_blotter_money_totals_v1'],
  [['book'],['presentValue','executionPnl','tradeCount'],'trading_blotter_money_totals_v1'],
  [['commodity','instrumentType'],['tradeCount','quantity'],'trading_blotter_totals_v1'],
  [['commodity','market'],['presentValue','executionPnl','quantity','tradeCount'],'trading_blotter_totals_v1']]);
 assert.ok(views.every(v=>v.mode==='pivot'&&v.priceNumericalPolicy===null&&JSON.stringify(v.bookIds)===JSON.stringify(books)&&v.portfolioIds.length===0));
});

test('swap direction and futures lots read in words, and a position without them is refused',()=>{
 const rows=positionRows(fixture.holdings);
 assert.deepEqual(rows.map(r=>r.side),['Pay fixed','+1 lot','−1 lot','Multi-side','Receive fixed','Receive fixed']);
 assert.equal(positionRows([{...fixture.holdings[1],lots:'-2'}])[0].side,'−2 lots');
 assert.throws(()=>positionRows([{...fixture.holdings[0],direction:null}]),{message:'The Blotter returned a swap without its direction.'});
 assert.throws(()=>positionRows([{...fixture.holdings[1],direction:'pay_fixed'}]),{message:'The Blotter returned a future with a swap direction.'});
 assert.throws(()=>positionRows([{...fixture.holdings[0],instrumentType:'option'}]),{message:'The Blotter returned an unknown instrument type.'});
});
test('a row shows the price it was traded at, its mark and its own value measure, exactly as the backend states them',()=>{
 const [swap,future,,multi,euro]=positionRows(fixture.holdings);
 assert.deepEqual([swap.pill,swap.reference,swap.price,swap.priceLabel,swap.mark,swap.value,swap.currency],['SWAP','TR-000001','3.5','Fixed','3.49','-10','USD']);
 assert.deepEqual([future.pill,future.price,future.priceLabel,future.mark,future.value,future.quantity,future.unit],['FUT','3.315','Executed','3.3','-37.5','2500','MMBtu (IT)']);
 assert.deepEqual([multi.price,multi.value,euro.currency],[null,'230','EUR']);
 assert.deepEqual([swap.market,swap.delivery,swap.commodity,swap.curve,swap.deliveryFrom],['Gas forward','Jan 2021','test_gas','gas_forward','2021-01-01']);
});
test('a row carries the words its cells draw, so the row search finds what the desk shows',()=>{
 const rows=positionRows(fixture.holdings);
 assert.equal(rows[0].words,'SWAP Jan 2021 MMBtu (IT) Pay fixed Fixed PV · USD');
 assert.equal(rows[1].words,'FUT Jan 2021 MMBtu (IT) +1 lot Executed P&L · USD');
 assert.equal(rows[2].words,'FUT Jan 2021 MMBtu (IT) −1 lot Executed Not valued Contract expired · awaiting settlement');
 assert.equal(rows[3].words,'SWAP Jan 2021 MWh Multi-side No fixed price PV · USD');
 assert.equal(rows[4].words,'SWAP Jan 2021 MWh Receive fixed Fixed PV · EUR');
});
test('reasons a close cannot value a position depend on the instrument',()=>{
 const rows=positionRows(fixture.holdings);
 assert.equal(rows[5].unavailable,'Missing price at this close');assert.equal(rows[5].value,null);
 const expiredSwap=positionRows([{...fixture.holdings[0],presentValue:null,mark:null,unavailableReason:'source_expired'}])[0];
 assert.equal(expiredSwap.unavailable,'Past marking cutoff');
 assert.throws(()=>positionRows([{...fixture.holdings[0],presentValue:'1',unavailableReason:'source_expired'}]),{message:'The Blotter returned a value for a position it cannot value.'});
});

test('the positions tile counts the rows read and mixes them by the backend commodity and instrument type groups',()=>{
 const tile=positionsTile(fixture.summaries,fixture.holdings.length,2);
 assert.equal(tile.count,6);
 assert.deepEqual(tile.segments.map(s=>[s.label,s.trades,s.commodity]),[
  ['Synthetic gas · Futures',2,'test_gas'],['Synthetic gas · Swaps',2,'test_gas'],
  ['Synthetic power · Swaps · EUR',1,'test_power'],['Synthetic power · Swaps · USD',1,'test_power']]);
 assert.equal(tile.note,'Includes 2 positions not valued at this close');
 assert.equal(positionsTile(fixture.summaries,6,0).note,null);
});

test('book groups read their subtotals from the backend book groups, one per measure and currency',()=>{
 const groups=deskGroups('book',fixture.summaries,fixture.holdings);
 assert.deepEqual(groups.keys,['d1000000-0000-0000-0000-00000000000a','d1000000-0000-0000-0000-00000000000b','d1000000-0000-0000-0000-00000000000b','d1000000-0000-0000-0000-00000000000b','d1000000-0000-0000-0000-00000000000b','d1000000-0000-0000-0000-00000000000a']);
 assert.deepEqual(groups.headers.map(h=>[h.label,h.subtotals.map(s=>[s.measure,s.amount,s.currency,s.excluded])]),[
  ['Book A',[['PV','-10','USD',1]]],
  ['Book B',[['PV','15','EUR',0],['PV','230','USD',0],['P&L','-37.5','USD',1]]]]);
});
test('market groups are keyed on market and definition revision, and only colliding names say which',()=>{
 const groups=deskGroups('market',fixture.summaries,fixture.holdings);
 assert.deepEqual(groups.headers.map(h=>[h.label,h.subtotals.map(s=>[s.measure,s.amount,s.currency])]),[
  ['Gas forward',[['PV','-10','USD']]],['Gas future',[['P&L','-37.5','USD']]],['Power euro forward',[['PV','15','EUR']]],['Power forward',[['PV','230','USD']]]]);
 assert.equal(groups.keys[0],'gas_forward|60000000-0000-0000-0000-000000000001');
 const later='60000000-0000-0000-0000-000000000002';
 const holdings=[...fixture.holdings,{...fixture.holdings[0],tradeId:'t7',bindingId:'b7',definitionRevisionId:later}];
 const summaries=changed(fixture.summaries,3,groups=>[...groups,{...groups[0],definitionRevisionId:later,presentValue:'4',tradeCount:1,unvaluedPositions:0}]);
 const split=deskGroups('market',summaries,holdings);
 assert.deepEqual(split.headers.map(h=>h.label).slice(0,2),['Gas forward · definition 1','Gas forward · definition 2']);
 assert.equal(split.keys.at(-1),'gas_forward|'+later);
});
test('instrument type groups read the money totals per currency, and a flat list has no groups',()=>{
 const groups=deskGroups('instrumentType',fixture.summaries,fixture.holdings);
 assert.deepEqual(groups.headers.map(h=>[h.label,h.subtotals.map(s=>[s.measure,s.amount,s.currency])]),[
  ['Futures',[['P&L','-37.5','USD']]],['Swaps',[['PV','15','EUR'],['PV','220','USD']]]]);
 assert.equal(deskGroups(null,fixture.summaries,fixture.holdings),null);
});
test('a position whose group the summaries do not state is refused rather than shown under a guessed header',()=>{
 const summaries=changed(fixture.summaries,1,groups=>groups.filter(g=>g.bookId!=='d1000000-0000-0000-0000-00000000000a'));
 assert.throws(()=>deskGroups('book',summaries,fixture.holdings),{message:'The trading workspace did not return the desk summaries. Refresh and try again.'});
});

test('swap PV by market is one horizontal, sign-colored panel per currency, largest gain first',()=>{
 const colors=commodityColors(['test_gas','test_power']);
 const panels=pvChartPanels(fixture.summaries,colors,format);
 assert.deepEqual(panels.map(p=>[p.unit,p.orientation,p.colorBySign,p.kind]),[['EUR','horizontal',true,'bar'],['USD','horizontal',true,'bar']]);
 const usd=panels[1].series[0].points;
 assert.deepEqual(usd.map(p=>[p.label,p.y,p.text,p.marker]),[['Power forward','230','+#230','#2a78d6'],['Gas forward','-10','−#10','#eb6834']]);
 assert.match(panels[1].subtitle,/Excludes 1 position that can't be valued at this close/);
 assert.doesNotMatch(panels[0].subtitle,/Excludes/);
});
test('long and short by market is one panel per quantity unit; swaps are drawn only where every swap of the market shares a direction',()=>{
 const colors=commodityColors(['test_gas','test_power']);
 const panels=positionChartPanels(fixture.summaries,fixture.holdings,colors,format);
 assert.deepEqual(panels.map(p=>p.unit),['MMBtu (IT)','MWh']);
 const [gas,power]=panels.map(p=>p.series[0].points);
 assert.deepEqual(gas.map(p=>[p.label,p.y,p.color,p.direction??null]),[['Gas forward · Swaps · mixed directions',null,'#eb6834',null],['Gas future · Futures','0','#eb6834',null]]);
 assert.deepEqual(power.map(p=>[p.label,p.y,p.direction??null,p.text]),[['Power euro forward · Swaps','50','negative','#50 receive fixed'],['Power forward · Swaps · multi-side',null,null,undefined]]);
 assert.equal(gas[1].text,'#0');
});
test('commodity colors follow the design identity and stay distinct for other commodities',()=>{
 const colors=commodityColors(['test_power','other_b','test_gas','other_a']);
 assert.equal(colors.get('test_gas'),'#eb6834');assert.equal(colors.get('test_power'),'#2a78d6');
 assert.equal(new Set(colors.values()).size,4);
 assert.deepEqual([colors.get('other_a'),colors.get('other_b')],['#2f8a66','#6250d6']);
});

test('a curve shape becomes sparkline points in its order, with gaps kept and the delivery period marked',()=>{
 const [shape]=fixture.shapes,[swap]=positionRows(fixture.holdings);
 const drawn=curvePoints(shape,swap);
 assert.equal(drawn.state,'drawn');
 assert.deepEqual(drawn.points.map(p=>[p.x,p.y]),[[0,'3.49'],[1,'3.41'],[2,null],[3,'3.02']]);
 assert.deepEqual(drawn.marks,[0]);
 assert.match(drawn.title,/Jan 2021 · 3\.49 USD\/MMBtu \(IT\)/);
 const quarter=curvePoints(shape,{...swap,deliveryFrom:'2021-02-01',deliveryTo:'2021-05-01'});
 assert.deepEqual(quarter.marks,[1,2,3]);
 assert.deepEqual(curvePoints(shape,{...swap,deliveryFrom:'2022-01-01',deliveryTo:'2022-02-01'}).marks,[]);
});
test('a curve the close cannot shape says why, quietly, and never draws a default line',()=>{
 const [,unread]=fixture.shapes,rows=positionRows(fixture.holdings);
 assert.deepEqual(curvePoints(unread,rows[4]),{state:'unavailable',reason:"Curve shape unavailable · this market's curve is not in the definitions of this close"});
 assert.deepEqual(curvePoints({...unread,unavailableReason:'missing_curve_view'},rows[4]),{state:'unavailable',reason:'Curve shape unavailable · no curve view at this close'});
 assert.deepEqual(curvePoints({...unread,unavailableReason:'other_code'},rows[4]),{state:'unavailable',reason:'Curve shape unavailable'});
 assert.deepEqual(curvePoints({...fixture.shapes[0],points:[]},rows[0]),{state:'unavailable',reason:'Curve shape unavailable · no contracts at this close'});
 assert.deepEqual(curvePoints({...fixture.shapes[0],points:fixture.shapes[0].points.map(p=>({...p,price:null,unavailableReason:'missing_observation'}))},rows[0]),{state:'unavailable',reason:'Curve shape unavailable · no prices at this close'});
});
