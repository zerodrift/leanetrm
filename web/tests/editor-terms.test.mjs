import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {tradeTerms,tradeHistory,tradeDetailView,tradeAmendState,readTradeView,holdsBinding} from '../dist/trade-editor.js';
import {swapTerms,swapHistory,swapDetailView,swapAmendState,readSwapView,retainedIndex,sideTitle,holdsInstrument} from '../dist/swap-editor.js';
import {setCurveNames} from '../dist/market-view.js';
const details=JSON.parse(readFileSync(new URL('./fixtures/trade-details.json',import.meta.url),'utf8'));
setCurveNames([{code:'future',name:'Gas future'},{code:'daily_gas',name:'Daily gas'},{code:'daily_power',name:'Daily power'}]);
const clone=value=>structuredClone(value);
// The backend's display text is the caller's: each kind is tagged so a test sees which one a row asked for.
const formats={money:v=>`$:${v}`,quantity:v=>`q:${v}`,price:v=>`p:${v}`,mark:v=>`m:${v}`,markMinusPrice:v=>`d:${v}`};
const rowsOf=(sections,title)=>Object.fromEntries(sections.find(s=>s.title===title).rows);
// The price unit of the position a future's terms are read for: its currency per its quantity unit, as the desk row states it.
const unit='USD/MMBtu (IT)';

test('a future\'s terms are its booking facts, its position and the contract terms of the editor, each exact, and its price in its unit',()=>{
 const sections=tradeTerms(details.future,'UTC',formats,unit);
 assert.deepEqual(sections.map(s=>s.title),['Trade','Position','Contract']);
 assert.deepEqual(rowsOf(sections,'Trade'),{Trade:'TR-000001 · IN-000001',Revision:'1 · Booked',Book:'Synthetic portfolio · Book A',Trader:'Synthetic trader','Trade date':'2026-10-24','Legal entity':'Synthetic entity 1',Counterparty:'Synthetic party 1',Executed:'Oct 24, 2026, 5:53 AM',Memo:'None'});
 assert.deepEqual(rowsOf(sections,'Position'),{Lots:'2','Execution price':'p:2.95 USD/MMBtu (IT)'});
 const contract=rowsOf(sections,'Contract');
 assert.equal(contract.Product,'Synthetic fixed-lot gas future');assert.equal(contract['Contract size'],'40 MMBtu (IT) per lot');assert.equal(contract['Generated price'],'m:3');
});

test('a future\'s executed time follows the user\'s zone, and a contract the catalog no longer offers is said to be unavailable',()=>{
 assert.equal(rowsOf(tradeTerms(details.future,'America/Chicago',formats,unit),'Trade').Executed,'Oct 24, 2026, 12:53 AM');
 const retained={...details.future,catalog:{...details.future.catalog,contracts:[]}};
 assert.deepEqual(rowsOf(tradeTerms(retained,'UTC',formats,unit),'Contract'),{'Contract terms':'Retained contract terms are unavailable in this selectable catalog.'});
});

test('a future\'s revisions are listed one by one with their facts, each exact, and what a revision lacks is said so',()=>{
 const amended=clone(details.future);
 amended.history.push({...clone(details.future.history[0]),revision:2,kind:'amended',lots:'3',executionPrice:'2.975',memo:'Synthetic note',reason:'Synthetic correction',traderCode:null,booking:{...details.future.history[0].booking,legalEntity:null,tradeDate:null}});
 const history=tradeHistory(amended,'UTC');
 assert.deepEqual(history.map(entry=>entry.title),['Revision 1 · Booked','Revision 2 · Amended']);
 assert.deepEqual(Object.fromEntries(history[0].rows),{Effective:'Dec 15, 2020, 4:00 PM',Recorded:'Oct 24, 2026, 5:53 AM',Book:'Book A',Lots:'2','Execution price':'2.95','Trader ID':'trader',Memo:'None','Trade date':'2026-10-24','Legal entity':'Synthetic entity 1',Counterparty:'Synthetic party 1',Reason:'Initial booking'});
 assert.deepEqual(Object.fromEntries(history[1].rows),{Effective:'Dec 15, 2020, 4:00 PM',Recorded:'Oct 24, 2026, 5:53 AM',Book:'Book A',Lots:'3','Execution price':'2.975','Trader ID':'None',Memo:'Synthetic note','Trade date':'Not recorded','Legal entity':'None',Counterparty:'Synthetic party 1',Reason:'Synthetic correction'});
});

test('a revision of a book the catalog no longer holds says so, and an hour the clock repeats is told apart',()=>{
 const gone=clone(details.future);gone.catalog.books=[];
 assert.equal(Object.fromEntries(tradeHistory(gone,'UTC')[0].rows).Book,'Unavailable book');
 // Clocks in Chicago show 1:30 a.m. twice on 1 November 2026: once in daylight time, once an hour later in standard time.
 const repeated=clone(details.future);
 repeated.history[0].recordedAt='2026-11-01T06:30:00+00:00';
 repeated.history.push({...clone(repeated.history[0]),revision:2,kind:'amended',recordedAt:'2026-11-01T07:30:00+00:00'});
 const [first,second]=tradeHistory(repeated,'America/Chicago').map(entry=>Object.fromEntries(entry.rows).Recorded);
 assert.notEqual(first,second,'the same clock time at two instants is not given one label');
});

test('the view of a future joins its terms and revisions, and its amendment state is its own',()=>{
 const view=tradeDetailView(details.future,'UTC',formats,unit);
 assert.deepEqual(Object.keys(view),['terms','history']);
 assert.deepEqual(view.terms,tradeTerms(details.future,'UTC',formats,unit));assert.deepEqual(view.history,tradeHistory(details.future,'UTC'));
 assert.deepEqual(tradeAmendState(details.future),{allowed:true,reason:null});
 const locked={...details.future,canAmend:false,amendUnavailableReason:'Settlement has been processed.'};
 assert.deepEqual(tradeAmendState(locked),{allowed:false,reason:'Settlement has been processed.'});
 assert.deepEqual(tradeAmendState({...locked,amendUnavailableReason:null}),{allowed:false,reason:'Read-only trade revision'});
});

// What the desk row of each position says of it: the keys the position panel reads a trade revision by.
const heldFuture=changes=>({tradeId:details.future.state.tradeId,bindingId:details.future.state.bindingId,contractId:details.future.state.contractId,revision:'1',currency:'USD',unit:'MMBtu (IT)',quoteMultiplier:'1',...changes});
const heldSwap=changes=>({tradeId:details.swap.state.tradeId,bindingId:'b0000000-0000-0000-0000-0000000000b1',contractId:details.swap.state.instrumentId,revision:'1',currency:'USD',unit:'MMBtu (IT)',quoteMultiplier:'1',...changes});

test('a future is read from its own trade revision and binding, and shows its price in the unit the desk row states',async()=>{
 const seen=[],api=async(path,body)=>{seen.push([path,body]);return details.future;};
 const read=await readTradeView(api,{market:'context'},heldFuture(),formats);
 assert.deepEqual(seen,[['futures/editor',{context:{market:'context'},tradeId:details.future.state.tradeId,revision:1}]]);
 assert.deepEqual(read.amend,{allowed:true,reason:null});
 assert.equal(rowsOf(read.view('UTC').terms,'Position')['Execution price'],'p:2.95 USD/MMBtu (IT)');
 const scaled=await readTradeView(api,{},heldFuture({quoteMultiplier:'100'}),formats);
 assert.equal(rowsOf(scaled.view('UTC').terms,'Position')['Execution price'],'p:2.95 USD/MMBtu (IT) × 100','a quote multiplier is part of the unit');
});

test('the editor\'s read of a trade is never shown for another binding of it: the position names its binding, and the read must too',async()=>{
 const api=async()=>details.future;
 await assert.rejects(readTradeView(api,{},heldFuture({bindingId:'b0000000-0000-0000-0000-0000000000b2'}),formats),{message:'The editor read holds another binding of the trade than the position.'});
});

test('a swap\'s terms are its booking facts, its structure and one section for each side, titled as the editor titles them',()=>{
 const sections=swapTerms(details.swap,'UTC',formats);
 assert.deepEqual(sections.map(s=>s.title),['Trade','Structure','Fixed side · Pay','Floating side · Receive']);
 assert.deepEqual(rowsOf(sections,'Trade'),{Trade:'TR-000011 · IN-000004',Revision:'1 · Booked',Book:'Synthetic portfolio · Book B',Trader:'Synthetic trader','Trade date':'2026-10-24','Legal entity':'Synthetic entity 1',Counterparty:'Synthetic party 1',Executed:'Oct 24, 2026, 5:53 AM',Memo:'None'});
 assert.deepEqual(rowsOf(sections,'Structure'),{Type:'Fixed for floating',Commodity:'Synthetic gas',Quantity:'q:100 MMBtu (IT)',Discounting:'Explicitly undiscounted'});
 assert.deepEqual(rowsOf(sections,'Fixed side · Pay'),{'Fixed price':'p:3 USD/MMBtu (IT)',Currency:'USD',Delivery:'Jan 2021','Payment frequency':'Monthly','Payment rule':'Day of the following month · day 18','Short months':'Reject a missing day',Adjustment:'Unadjusted'});
 assert.deepEqual(rowsOf(sections,'Floating side · Receive'),{Index:'Price','Projection curve':'Daily gas','Reset frequency':'Daily',Averaging:'Arithmetic',Spread:'0 USD/MMBtu (IT)','Projected prices':'Contractual projection curve',Currency:'USD',Delivery:'Jan 2021','Payment frequency':'Monthly','Payment rule':'Days after last delivery day · 46 days','Day basis':'Calendar days',Adjustment:'Unadjusted'});
});

test('a swap with two floating sides shows each side with its own terms and direction',()=>{
 const pair=clone(details.swap),terms=pair.state.terms;
 terms.swapType='financial_floating_floating';terms.fixed=null;terms.fixedCoefficient=null;terms.firstCoefficient='-1';
 terms.firstFloating={...clone(terms.floating),curve:'daily_power',spread:'0.1'};terms.floating={...terms.floating,spread:'0.2'};
 pair.catalog.indices.push({...pair.catalog.indices[0],curve:'daily_power',unit:'MWh'});
 pair.valuation.sides=[{slot:0,kind:'floating',coefficient:'-1',payments:[]},{slot:1,kind:'floating',coefficient:'1',payments:[]}];
 const sections=swapTerms(pair,'UTC',formats);
 assert.deepEqual(sections.map(s=>s.title),['Trade','Structure','First floating side · Pay','Second floating side · Receive']);
 assert.equal(rowsOf(sections,'Structure').Type,'Floating for floating');
 assert.equal(rowsOf(sections,'First floating side · Pay')['Projection curve'],'Daily power');assert.equal(rowsOf(sections,'First floating side · Pay').Spread,'0.1 USD/MWh');
 assert.equal(rowsOf(sections,'Second floating side · Receive')['Projection curve'],'Daily gas');assert.equal(rowsOf(sections,'Second floating side · Receive').Spread,'0.2 USD/MMBtu (IT)');
});

test('side titles name the side and its direction from the side\'s own coefficient, and show a coefficient they do not know as it is',()=>{
 assert.equal(sideTitle({slot:0,kind:'fixed',coefficient:'-1'},false),'Fixed side · Pay');
 assert.equal(sideTitle({slot:1,kind:'floating',coefficient:'1'},false),'Floating side · Receive');
 assert.equal(sideTitle({slot:0,kind:'floating',coefficient:'1'},true),'First floating side · Receive');
 assert.equal(sideTitle({slot:1,kind:'floating',coefficient:'-1'},true),'Second floating side · Pay');
 assert.equal(sideTitle({slot:0,kind:'fixed',coefficient:'2'},false),'Fixed side · coefficient 2');
});

test('a swap whose retained index the catalog no longer holds says so instead of guessing a unit',()=>{
 const gone=clone(details.swap);gone.catalog.indices=[];
 assert.throws(()=>swapTerms(gone,'UTC',formats),{message:'The retained exact index definition is unavailable.'});
});

test('payment rows follow the schedule\'s own rule: a following-month rule names its day and short-month choice, a lag its days and basis',()=>{
 const adjusted=clone(details.swap);
 Object.assign(adjusted.state.terms.fixed.paymentSchedule,{adjustment:'following',calendar:'holiday_calendar'});
 assert.deepEqual(Object.entries(rowsOf(swapTerms(adjusted,'UTC',formats),'Fixed side · Pay')).filter(([label])=>['Adjustment','Calendar'].includes(label)),[['Adjustment','Following'],['Calendar','Holiday Calendar']]);
});

test('a swap\'s revisions are listed one by one with their facts, and its quantity exact',()=>{
 const amended=clone(details.swap);
 amended.history.push({...clone(details.swap.history[0]),revision:2,kind:'amended',quantity:'200',memo:'Synthetic note',reason:'Synthetic correction'});
 const history=swapHistory(amended,'UTC');
 assert.deepEqual(history.map(entry=>entry.title),['Revision 1 · Booked','Revision 2 · Amended']);
 assert.deepEqual(Object.fromEntries(history[0].rows),{Effective:Object.fromEntries(history[0].rows).Effective,Recorded:Object.fromEntries(history[0].rows).Recorded,Quantity:'100','Trader ID':'trader',Memo:'None','Trade date':'2026-10-24','Legal entity':'Synthetic entity 1',Counterparty:'Synthetic party 1',Reason:'Initial booking'});
 assert.deepEqual(history[1].rows.filter(([label])=>['Quantity','Memo','Reason'].includes(label)),[['Quantity','200'],['Memo','Synthetic note'],['Reason','Synthetic correction']]);
});

test('the view of a swap joins its terms and revisions, and its amendment state is its own',()=>{
 const view=swapDetailView(details.swap,'UTC',formats);
 assert.deepEqual(Object.keys(view),['terms','history']);
 assert.deepEqual(view.terms,swapTerms(details.swap,'UTC',formats));assert.deepEqual(view.history,swapHistory(details.swap,'UTC'));
 assert.deepEqual(swapAmendState(details.swap),{allowed:true,reason:null});
 assert.deepEqual(swapAmendState({...details.swap,canAmend:false,amendUnavailableReason:'Only the latest eligible trade revision can be amended.'}),{allowed:false,reason:'Only the latest eligible trade revision can be amended.'});
 assert.deepEqual(swapAmendState({...details.swap,canAmend:false,amendUnavailableReason:null}),{allowed:false,reason:'Read-only revision'});
});

test('a swap is read from its own trade revision, and the read must hold the instrument the position is in',async()=>{
 const seen=[],api=async(path,body)=>{seen.push([path,body]);return details.swap;};
 const read=await readSwapView(api,{market:'context'},heldSwap(),formats);
 assert.deepEqual(seen,[['swaps/editor',{context:{market:'context',convention:'independent_side_financial_swap_v2'},tradeId:details.swap.state.tradeId,revision:1}]]);
 assert.deepEqual(read.amend,{allowed:true,reason:null});
 assert.deepEqual(read.view('UTC').terms.map(s=>s.title),['Trade','Structure','Fixed side · Pay','Floating side · Receive']);
 await assert.rejects(readSwapView(api,{},heldSwap({contractId:'c0000000-0000-0000-0000-0000000000c2'}),formats),{message:'The editor read holds another instrument of the trade than the position.'});
});

test('the retained index of a side is the catalog\'s index of the swap\'s own definition revision, curve and index, and its absence is said',()=>{
 const terms=details.swap.state.terms,catalog=details.swap.catalog;
 const found=retainedIndex(catalog,terms,terms.floating);
 assert.deepEqual([found.curve,found.index,found.definitionRevisionId],[terms.floating.curve,terms.floating.index,terms.definitionRevisionId]);
 assert.throws(()=>retainedIndex({...catalog,indices:[]},terms,terms.floating),{message:'The retained exact index definition is unavailable.'});
 assert.throws(()=>retainedIndex(catalog,{...terms,definitionRevisionId:'another'},terms.floating),{message:'The retained exact index definition is unavailable.'});
});

test('an editor read of a future holds the position\'s binding, or the editor refuses it, as the panel\'s own read does',()=>{
 assert.doesNotThrow(()=>holdsBinding(details.future,heldFuture()));
 assert.throws(()=>holdsBinding(details.future,heldFuture({bindingId:'b0000000-0000-0000-0000-0000000000b2'})),{message:'The editor read holds another binding of the trade than the position.'});
});

test('an editor read of a swap holds the position\'s instrument, or the editor refuses it, as the panel\'s own read does',()=>{
 assert.doesNotThrow(()=>holdsInstrument(details.swap,heldSwap()));
 assert.throws(()=>holdsInstrument(details.swap,heldSwap({contractId:'c0000000-0000-0000-0000-0000000000c2'})),{message:'The editor read holds another instrument of the trade than the position.'});
});
