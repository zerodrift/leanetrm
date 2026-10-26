import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {summaryTiles,positionsTile,positionRows,deskSummaryViews,deskGroups,pvChartPanels,positionChartPanels,curvePoints,curveChart,commodityColors,signOf,valueFields,priceUnit,holdingFormats,quantityText} from '../dist/desk-model.js';
import {prepareAmountDisplay,selectDisplayPrecision,initialDisplayPrecision} from '../dist/amount-display.js';
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
test('a currency or unit that collides names its reference revision by the same short id as a market label, not by its catalog and revision',()=>{
 const catalogId='10000000-0000-0000-0000-000000000001',second='e5f6a7b8-0000-4000-8000-000000000002';
 // The fixture's own references are in revision 20000000-...; these two share their codes with them and differ in the revision.
 const currency={...fixture.summaries[0].groups[0].currencyReference,revision:{catalogId,revisionId:second}};
 const tiles=summaryTiles(changed(changed(fixture.summaries,0,groups=>[...groups,{...groups[0],currencyReference:currency,presentValue:'7',tradeCount:1,unvaluedPositions:0}]),1,groups=>[...groups,{...groups[0],currencyReference:currency,presentValue:'7',tradeCount:1,unvaluedPositions:0}]));
 assert.deepEqual(tiles[0].rows.map(r=>r.currency),['EUR','USD · revision 20000000','USD · revision e5f6a7b8']);
 const unit={...fixture.summaries[2].groups[0].quantitySpecification,revision:{catalogId,revisionId:second}};
 const mix=positionsTile(changed(fixture.summaries,2,groups=>[...groups,{...groups[0],quantitySpecification:unit,tradeCount:1}]),fixture.holdings.length,0);
 assert.deepEqual(mix.segments.map(s=>s.label).filter(label=>/MMBtu/.test(label)),['Synthetic gas · Swaps · MMBtu (IT) · revision 20000000','Synthetic gas · Swaps · MMBtu (IT) · revision e5f6a7b8']);
 for(const label of [...tiles[0].rows.map(r=>r.currency),...mix.segments.map(s=>s.label)])assert.doesNotMatch(label,/[0-9a-f]{8}-[0-9a-f]{4}-.*:/,'a catalog is not part of what names a revision');
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
test('a row states the price unit its price and mark are in, with a quote multiplier other than one',()=>{
 const [swap,future,,,euro]=positionRows(fixture.holdings);
 assert.deepEqual([swap.priceUnit,future.priceUnit,euro.priceUnit],['USD/MMBtu (IT)','USD/MMBtu (IT)','EUR/MWh']);
 assert.equal(positionRows([{...fixture.holdings[1],quoteMultiplier:'100'}])[0].priceUnit,'USD/MMBtu (IT) × 100');
 assert.equal(positionRows([{...fixture.holdings[1],quoteMultiplier:'1'}])[0].priceUnit,'USD/MMBtu (IT)');
 assert.equal(priceUnit({currency:'EUR',unit:'MWh',quoteMultiplier:'0.001'}),'EUR/MWh × 0.001');
});
test('a valued swap without one mark says so, and a valued position without its value or mark is refused',()=>{
 const rows=positionRows(fixture.holdings);
 assert.deepEqual([rows[0].markNote,rows[1].markNote,rows[3].markNote,rows[5].markNote],[null,null,'Several marks',null]);
 assert.throws(()=>positionRows([{...fixture.holdings[0],presentValue:null}]),{message:'The Blotter returned a position without a value or a reason.'});
 assert.throws(()=>positionRows([{...fixture.holdings[1],mark:null}]),{message:'The Blotter returned a valued future without its mark.'});
});
test('the sign of an amount and the value cells of an instrument come from one definition each',()=>{
 assert.deepEqual(['-1','0','0.000','-0','1/3','-1/3','0/5'].map(signOf),['negative','zero','zero','zero','positive','negative','zero']);
 assert.deepEqual([valueFields('future'),valueFields('financial_swap')],[['mark','executionPnl'],['mark','presentValue']]);
 assert.throws(()=>valueFields('option'),{message:'The Blotter returned an unknown instrument type.'});
});
test('a row carries the words its cells draw, so the row search finds what the desk shows',()=>{
 const rows=positionRows(fixture.holdings);
 assert.equal(rows[0].words,'SWAP Jan 2021 MMBtu (IT) Pay fixed Fixed · USD/MMBtu (IT) PV · USD');
 assert.equal(rows[1].words,'FUT Jan 2021 MMBtu (IT) +1 lot Executed · USD/MMBtu (IT) P&L · USD');
 assert.equal(rows[2].words,'FUT Jan 2021 MMBtu (IT) −1 lot Executed · USD/MMBtu (IT) Not valued Contract expired · awaiting settlement');
 assert.equal(rows[3].words,'SWAP Jan 2021 MWh Multi-side No fixed price Several marks PV · USD');
 assert.equal(rows[4].words,'SWAP Jan 2021 MWh Receive fixed Fixed · EUR/MWh PV · EUR');
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
test('market groups are keyed on market and definition revision, and only colliding names say which, by the revision itself',()=>{
 const groups=deskGroups('market',fixture.summaries,fixture.holdings);
 assert.deepEqual(groups.headers.map(h=>[h.label,h.subtotals.map(s=>[s.measure,s.amount,s.currency])]),[
  ['Gas forward',[['PV','-10','USD']]],['Gas future',[['P&L','-37.5','USD']]],['Power euro forward',[['PV','15','EUR']]],['Power forward',[['PV','230','USD']]]]);
 assert.equal(groups.keys[0],'gas_forward|60000000-0000-0000-0000-000000000001');
 const later='60000000-0000-0000-0000-000000000002';
 const holdings=[...fixture.holdings,{...fixture.holdings[0],tradeId:'t7',bindingId:'b7',definitionRevisionId:later}];
 const summaries=changed(fixture.summaries,3,groups=>[...groups,{...groups[0],definitionRevisionId:later,presentValue:'4',tradeCount:1,unvaluedPositions:0}]);
 const split=deskGroups('market',summaries,holdings);
 // These revisions differ only at the end of their ids, so the short id runs to where they differ; it is never a position among them.
 assert.deepEqual(split.headers.map(h=>h.label).slice(0,2),['Gas forward · definition 60000000-0000-0000-0000-000000000001','Gas forward · definition 60000000-0000-0000-0000-000000000002']);
 assert.equal(split.keys.at(-1),'gas_forward|'+later);
});
test('a colliding market label names its revision by the shortest start of its id that no other shares, and a market of another code by its code',()=>{
 const [first,second]=['a1b2c3d4-0000-4000-8000-000000000001','e5f6a7b8-0000-4000-8000-000000000002'];
 const holdings=[{...fixture.holdings[0],definitionRevisionId:first},{...fixture.holdings[0],tradeId:'t7',bindingId:'b7',definitionRevisionId:second}];
 const group=fixture.summaries[3].groups[0];
 const summaries=changed(fixture.summaries,3,()=>[{...group,definitionRevisionId:first},{...group,definitionRevisionId:second,presentValue:'4'}]);
 const heldAt=deskGroups('market',summaries,holdings).headers.map(h=>h.label);
 assert.deepEqual(heldAt,['Gas forward · definition a1b2c3d4','Gas forward · definition e5f6a7b8']);
 for(const label of heldAt)assert.doesNotMatch(label,/definition \d$/,'an ordinal is not an identity');
 // Two markets that share a name in one revision are told apart by their codes.
 setCurveNames([...fixture.curveNames,{code:'gas_forward_b',name:'Gas forward'}]);
 try{
  const twin=changed(fixture.summaries,3,groups=>[groups[0],{...groups[0],market:'gas_forward_b'}]);
  const held=[fixture.holdings[0],{...fixture.holdings[0],tradeId:'t8',bindingId:'b8',curve:'gas_forward_b'}];
  assert.deepEqual(deskGroups('market',twin,held).headers.map(h=>h.label),['Gas forward · gas_forward','Gas forward · gas_forward_b']);
 }finally{setCurveNames(fixture.curveNames);}
});
test('one instrument type of a market held in more than one quantity specification or currency would state its measure twice, so its header is refused with the desk\'s message',()=>{
 const message='The trading workspace returned an inconsistent desk summary. Refresh and try again.';
 const [swap,future]=fixture.summaries[3].groups;
 const otherUnit={...swap.quantitySpecification,code:'other_quantity'},otherCurrency={...swap.currencyReference,code:'other'};
 const heldAgain=(group,change)=>changed(fixture.summaries,3,groups=>[...groups,{...group,...change}]);
 const inUnit={quantitySpecification:otherUnit},inCurrency={currencyReference:otherCurrency,currency:'OTH'};
 // Swaps and futures alike: the market summary keeps units and currencies apart, so a second group of one type is a second subtotal of its measure.
 for(const group of [swap,future])for(const change of [inUnit,inCurrency])assert.throws(()=>deskGroups('market',heldAgain(group,change),fixture.holdings),{message});
 // Books and instrument types read the money totals, which add one currency across units: they are not affected.
 assert.equal(deskGroups('book',heldAgain(swap,inUnit),fixture.holdings).headers.length,2);
 assert.equal(deskGroups('instrumentType',heldAgain(swap,inUnit),fixture.holdings).headers.length,2);
});
test('a market whose futures and swaps sit in different units or currencies states each measure once in its header, and its long and short bars panel by unit',()=>{
 const [swap,future]=fixture.summaries[3].groups,colors=commodityColors(['test_gas','test_power']);
 const futuresOf=change=>changed(fixture.summaries,3,groups=>[...groups,{...future,market:swap.market,...change}]);
 const subtotals=summaries=>deskGroups('market',summaries,fixture.holdings).headers.find(h=>h.label==='Gas forward').subtotals.map(s=>[s.measure,s.amount,s.currency]);
 // The futures in the swaps' unit and currency, in another unit, and in another currency.
 assert.deepEqual(subtotals(futuresOf({})),[['PV','-10','USD'],['P&L','-37.5','USD']]);
 const inUnit=futuresOf({unit:'therm',quantitySpecification:{...future.quantitySpecification,code:'other_quantity'}});
 assert.deepEqual(subtotals(inUnit),[['PV','-10','USD'],['P&L','-37.5','USD']]);
 assert.deepEqual(subtotals(futuresOf({currency:'OTH',currencyReference:{...future.currencyReference,code:'other'}})),[['PV','-10','USD'],['P&L','-37.5','OTH']]);
 // The charts take the same market: the futures' bar and the swaps' bar are in the panels of their own units.
 assert.deepEqual(positionChartPanels(inUnit,fixture.holdings,colors,format).map(p=>p.unit),['MMBtu (IT)','MWh','therm']);
 assert.deepEqual(pvChartPanels(inUnit,colors,format).map(p=>p.title),['EUR · bar right = gain','USD · bar right = gain']);
});
test('a group header states how many positions the group holds, so a filtered count can be shown against it',()=>{
 assert.deepEqual(deskGroups('book',fixture.summaries,fixture.holdings).headers.map(h=>[h.label,h.positions]),[['Book A',2],['Book B',4]]);
 assert.deepEqual(deskGroups('instrumentType',fixture.summaries,fixture.holdings).headers.map(h=>[h.label,h.positions]),[['Futures',2],['Swaps',4]]);
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
 // The card's heading is drawn once by the desk; each panel is captioned by its currency.
 assert.deepEqual(panels.map(p=>p.title),['EUR · bar right = gain','USD · bar right = gain']);
 const usd=panels[1].series[0].points;
 assert.deepEqual(usd.map(p=>[p.label,p.y,p.text,p.marker]),[['Power forward','230','+#230','#2a78d6'],['Gas forward','-10','−#10','#eb6834']]);
 assert.equal(panels[1].subtitle,"Excludes 1 position that can't be valued at this close");
 assert.equal(panels[0].subtitle,undefined);
});
test('long and short by market is one panel per quantity unit; swaps are drawn only where every swap of the market shares a direction',()=>{
 const colors=commodityColors(['test_gas','test_power']);
 const panels=positionChartPanels(fixture.summaries,fixture.holdings,colors,format);
 assert.deepEqual(panels.map(p=>p.unit),['MMBtu (IT)','MWh']);
 assert.deepEqual(panels.map(p=>p.title),['MMBtu (IT) · bar right = long','MWh · bar right = long']);
 const [gas,power]=panels.map(p=>p.series[0].points);
 assert.deepEqual(gas.map(p=>[p.label,p.y,p.color,p.direction??null]),[['Gas forward · Swaps · mixed directions',null,'#eb6834',null],['Gas future · Futures','0','#eb6834',null]]);
 assert.deepEqual(power.map(p=>[p.label,p.y,p.direction??null,p.text]),[['Power euro forward · Swaps','50','negative','#50 receive fixed'],['Power forward · Swaps · multi-side',null,null,undefined]]);
 assert.equal(gas[1].text,'#0');
 assert.deepEqual(panels.map(p=>p.subtitle),['Not drawn: Gas forward · Swaps · mixed directions','Not drawn: Power forward · Swaps · multi-side'],'each panel names the markets it draws no bar for');
});
test('a market held in more than one unit or currency is refused with a message of the desk, not drawn as one bar',()=>{
 const message='The trading workspace returned an inconsistent desk summary. Refresh and try again.';
 const colors=commodityColors(['test_gas','test_power']);
 const otherUnit={...fixture.summaries[3].groups[0].quantitySpecification,code:'other_quantity'};
 const twoUnits=changed(fixture.summaries,3,groups=>[...groups,{...groups[0],quantitySpecification:otherUnit}]);
 assert.throws(()=>pvChartPanels(twoUnits,colors,format),{message});
 const otherCurrency={...fixture.summaries[3].groups[0].currencyReference,code:'other'};
 const twoCurrencies=changed(fixture.summaries,3,groups=>[...groups,{...groups[0],currencyReference:otherCurrency,currency:'OTH'}]);
 const held=[...fixture.holdings,{...fixture.holdings[0],tradeId:'t7',bindingId:'b7',currencyReference:otherCurrency,currency:'OTH'}];
 assert.throws(()=>positionChartPanels(twoCurrencies,held,colors,format),{message});
});
test('the direction of every swap of a market is found in one pass over the positions, not one pass per market',()=>{
 let reads=0;
 const counted=new Proxy([...fixture.holdings],{get(target,key,receiver){if(typeof key==='string'&&/^\d+$/.test(key))reads++;return Reflect.get(target,key,receiver);}});
 positionChartPanels(fixture.summaries,counted,commodityColors(['test_gas','test_power']),format);
 assert.ok(reads<=2*fixture.holdings.length,`read ${reads} positions for ${fixture.holdings.length}`);
});
test('commodity colors follow the design identity and stay distinct for other commodities',()=>{
 const colors=commodityColors(['test_power','other_b','test_gas','other_a']);
 assert.equal(colors.get('test_gas'),'#eb6834');assert.equal(colors.get('test_power'),'#2a78d6');
 assert.equal(new Set(colors.values()).size,4);
 assert.deepEqual([colors.get('other_a'),colors.get('other_b')],['#2f8a66','#6250d6']);
});
test('the explicit energy codes of the imported catalog take the same identity colors as the synthetic ones',()=>{
 const colors=commodityColors(['electricity','gas','test_gas','test_power','other']);
 assert.deepEqual(['gas','test_gas'].map(code=>colors.get(code)),['#eb6834','#eb6834']);
 assert.deepEqual(['electricity','test_power'].map(code=>colors.get(code)),['#2a78d6','#2a78d6']);
 assert.equal(colors.get('other'),'#2f8a66');
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

test('a position formats its amounts by its own deal: its swap price by the fixed side\'s precision, its marks by the clearing increment',async()=>{
 selectDisplayPrecision(initialDisplayPrecision());
 const deal={money:2,price:4,quantity:1,factor:6};
 const future={...fixture.holdings[1],priceTick:'0.025',markTick:'0.001',amountFormat:deal,fixedAmountFormat:{...deal,price:2}};
 const swap={...fixture.holdings[0],fixedAmountFormat:{...deal,price:6},amountFormat:deal};
 // Stands in for the backend: it states the places and increment each request asked for.
 await prepareAmountDisplay({holdings:[future,swap]},async body=>body.map(r=>`${r.value}@${r.decimalPlaces}/${r.tick}`));
 const f=holdingFormats(future),s=holdingFormats(swap);
 assert.deepEqual([f.price('3.315'),f.mark('3.3'),f.markMinusPrice('-0.015'),f.money('-37.5'),f.quantity('2500')],['3.315@4/0.025','3.3@4/0.001','-0.015@4/0.001','-37.5@2/null','2500@1/null']);
 assert.deepEqual([s.price('3.5'),s.mark('3.49'),s.markMinusPrice('-0.01')],['3.5@6/null','3.49@4/null','-0.01@4/null']);
});

test('a size is signed for a future and unsigned for a swap, whose direction its side label states',()=>{
 const rows=positionRows(fixture.holdings);
 assert.equal(quantityText(rows[1],v=>v),'+2500');
 assert.equal(quantityText(rows[2],v=>v),'−2500');
 assert.equal(quantityText(rows[0],v=>v),'1000');
 assert.equal(quantityText({...rows[0],quantity:'-1000'},v=>v),'−1000');
 assert.equal(quantityText({...rows[1],quantity:'0'},v=>v),'0');
});

test('a trade that holds several bindings is a position for each of them: the Blotter lists trade bindings, and none is refused',()=>{
 const second={...fixture.holdings[0],bindingId:'b0000000-0000-0000-0000-0000000000b2',quantity:'250'};
 const rows=positionRows([fixture.holdings[0],second]);
 assert.deepEqual(rows.map(r=>[r.tradeId,r.revision,r.quantity]),[[fixture.holdings[0].tradeId,'1','1000'],[fixture.holdings[0].tradeId,'1','250']]);
 assert.equal(positionRows([fixture.holdings[0],{...fixture.holdings[0],revision:'2'}]).length,2,'two revisions of one trade are two positions');
});

// A market and the delivery period a draft is for, as the Price before booking section states them: no position, no trade price.
const draftTarget={market:'Gas forward',delivery:'Jan 2021',deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01'};

test('the curve of a draft\'s market is drawn from the curve shape and marks the contracts of the delivery period it is for',()=>{
 const view=curveChart(fixture.shapes[0],draftTarget);
 assert.equal(view.state,'chart');assert.equal(view.marked,true);
 assert.deepEqual([view.spec.title,view.spec.axis,view.spec.unit,view.spec.kind],['Gas forward · forward curve','delivery','USD/MMBtu (IT)','line']);
 assert.deepEqual(view.spec.series[0].points.map(p=>[p.x,p.y]),fixture.shapes[0].points.map(p=>[p.contractId,p.price]),'every contract of the curve, a contract without a price kept as a gap');
 assert.deepEqual(view.spec.marked,['k0000000-0000-0000-0000-000000000001']);assert.equal(view.spec.subtitle,'Marked: delivery Jan 2021');
 assert.equal(view.spec.reference,undefined,'a draft has no trade price to draw');
});

test('a delivery period over several contracts marks each, and one the curve does not reach marks none and says so',()=>{
 const quarter=curveChart(fixture.shapes[0],{...draftTarget,delivery:'Feb–Apr 2021',deliveryFrom:'2021-02-01',deliveryTo:'2021-05-01'});
 assert.deepEqual(quarter.spec.marked,['k0000000-0000-0000-0000-000000000002','k0000000-0000-0000-0000-000000000003','k0000000-0000-0000-0000-000000000004']);
 const outside=curveChart(fixture.shapes[0],{...draftTarget,delivery:'Jan 2022',deliveryFrom:'2022-01-01',deliveryTo:'2022-02-01'});
 assert.equal(outside.state,'chart');assert.equal(outside.marked,false);assert.deepEqual(outside.spec.marked,[]);assert.equal(outside.spec.subtitle,undefined);
});

test('a curve the close cannot draw says why, in the words a row says it in',()=>{
 assert.deepEqual(curveChart(fixture.shapes[1],draftTarget),{state:'unavailable',reason:"Curve shape unavailable · this market's curve is not in the definitions of this close"});
 assert.deepEqual(curveChart({...fixture.shapes[0],points:[]},draftTarget),{state:'unavailable',reason:'Curve shape unavailable · no contracts at this close'});
});

// A scheduled swap (D214) holds quantities and prices by period, and a swap whose settlement lifecycle is under way is valued by it.
// The desk states what the backend gives for them and, where the backend gives no single figure, says why: it never makes one up.
const scheduled=changes=>({...fixture.holdings[0],tradeId:'t0000000-0000-0000-0000-000000000008',bindingId:'b0000000-0000-0000-0000-000000000008',tradeReference:'TR-000008',
 contractIdentityKind:'scheduled_swap_instrument',quantity:null,fixedPrice:null,mark:null,markMinusPrice:null,deliveryTo:'2021-03-01',projectionCurves:['gas_forward'],...changes});

test('a scheduled swap has no single quantity, fixed price or mark: its row says each is by period, draws no size bar, and keeps its side',()=>{
 const [row]=positionRows([scheduled()]);
 assert.deepEqual([row.quantity,row.quantityNote,row.exposure,row.side],[null,'By period',null,'Pay fixed']);
 assert.deepEqual([row.price,row.priceNote,row.mark,row.markNote,row.value],[null,'By period',null,'By period','-10']);
 assert.equal(row.words,'SWAP Jan 1, 2021 – Feb 28, 2021 By period MMBtu (IT) Pay fixed By period Fixed · USD/MMBtu (IT) By period PV · USD');
 const common=positionRows([scheduled({fixedPrice:'3'})])[0];
 assert.deepEqual([common.price,common.priceNote],['3',null],'a fixed price every fixed period shares is the row\'s price');
 const floating=positionRows([scheduled({direction:'multi_side'})])[0];
 assert.deepEqual([floating.price,floating.priceNote],[null,null],'two floating sides have no fixed price at all');
});

test('a swap is refused without the backend\'s quantity, or with a single quantity its kind does not have, or without its kind',()=>{
 assert.throws(()=>positionRows([{...fixture.holdings[0],quantity:null}]),{message:'The Blotter returned a swap without its quantity.'});
 assert.throws(()=>positionRows([scheduled({quantity:'100'})]),{message:'The Blotter returned a single quantity for a scheduled swap.'});
 assert.throws(()=>positionRows([{...fixture.holdings[0],contractIdentityKind:undefined}]),{message:'The Blotter returned a swap without its instrument kind.'});
 assert.throws(()=>positionRows([{...fixture.holdings[0],settlement:'settled'}]),{message:'The Blotter returned a swap in an unknown valuation state.'});
});

test('a swap valued by its settlement lifecycle states no curve mark, and says so rather than claiming several',()=>{
 const [row]=positionRows([{...fixture.holdings[0],settlement:'Active',mark:null,markMinusPrice:null,presentValue:'-5'}]);
 assert.deepEqual([row.mark,row.markNote,row.value,row.price,row.quantity],[null,'Settlement lifecycle','-5','3.5','1000']);
});

test('a scheduled swap whose periods project from several curves is one market of those curves together, never its first period\'s',()=>{
 const key=JSON.stringify(['gas_forward','power_forward']);
 const held=scheduled({marketKey:key,marketLabel:'Gas forward / Power forward',projectionCurves:['gas_forward','power_forward']});
 const [row]=positionRows([held]);
 assert.deepEqual([row.market,row.curve,row.severalCurves],['Gas forward / Power forward','gas_forward',true]);
 assert.equal(positionRows([scheduled()])[0].severalCurves,false);
 const summaries=changed(fixture.summaries,3,groups=>[...groups,{...groups[0],market:key,marketLabel:'Gas forward / Power forward',quantity:null,presentValue:'-10',tradeCount:1,unvaluedPositions:0}]);
 const groups=deskGroups('market',summaries,[...fixture.holdings,held]);
 assert.equal(groups.keys.at(-1),`${key}|${held.definitionRevisionId}`);
 assert.deepEqual(groups.headers.filter(h=>h.key===groups.keys.at(-1)).map(h=>[h.label,h.positions]),[['Gas forward / Power forward',1]]);
 const pv=pvChartPanels(summaries,commodityColors(['test_gas','test_power']),format).find(p=>p.unit==='USD').series[0].points;
 assert.ok(pv.some(p=>p.label==='Gas forward / Power forward'&&p.y==='-10'));
});

test('a market the backend gives no single quantity, when none of its held swaps lacks one, is refused rather than said to have none',()=>{
 const colors=commodityColors(['test_gas','test_power']),message='The trading workspace returned an inconsistent desk summary. Refresh and try again.';
 const summaries=changed(fixture.summaries,3,groups=>groups.map(g=>g.market==='power_euro'?{...g,quantity:null}:g));
 assert.throws(()=>positionChartPanels(summaries,fixture.holdings,colors,format),{message});
});

test('the long and short chart draws no bar for a market the backend gives no single quantity, and says why; a future is never without one',()=>{
 const colors=commodityColors(['test_gas','test_power']),message='The trading workspace returned an inconsistent desk summary. Refresh and try again.';
 const summaries=changed(fixture.summaries,3,groups=>groups.map(g=>g.market==='power_euro'?{...g,quantity:null}:g));
 const held=fixture.holdings.map(h=>h.curve==='power_euro'?{...h,contractIdentityKind:'scheduled_swap_instrument',quantity:null,fixedPrice:null,mark:null,markMinusPrice:null,projectionCurves:['power_euro']}:h);
 const panel=positionChartPanels(summaries,held,colors,format).find(p=>p.unit==='MWh'),euro=panel.series[0].points.find(p=>p.x.startsWith('power_euro'));
 assert.deepEqual([euro.y,euro.label,euro.text,euro.direction],[null,'Power euro forward · Swaps · no single quantity',undefined,undefined]);
 assert.equal(panel.subtitle,'Not drawn: Power euro forward · Swaps · no single quantity; Power forward · Swaps · multi-side','a panel with no bar left still says why');
 assert.throws(()=>positionChartPanels(changed(fixture.summaries,3,groups=>groups.map(g=>g.market==='gas_future'?{...g,quantity:null}:g)),fixture.holdings,colors,format),{message});
});
