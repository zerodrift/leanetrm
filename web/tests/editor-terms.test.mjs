import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {tradeTerms,tradeHistoryTable,tradeDetailView} from '../dist/trade-editor.js';
import {swapTerms,swapHistoryTable,swapDetailView,sideTitle} from '../dist/swap-editor.js';
import {setCurveNames} from '../dist/market-view.js';
const details=JSON.parse(readFileSync(new URL('./fixtures/trade-details.json',import.meta.url),'utf8'));
setCurveNames([{code:'future',name:'Gas future'},{code:'daily_gas',name:'Daily gas'},{code:'daily_power',name:'Daily power'}]);
const clone=value=>structuredClone(value);
// The backend's display text is the caller's: each kind is tagged so a test sees which one a row asked for.
const formats={money:v=>`$:${v}`,quantity:v=>`q:${v}`,price:v=>`p:${v}`,mark:v=>`m:${v}`,markMinusPrice:v=>`d:${v}`};
const rowsOf=(sections,title)=>Object.fromEntries(sections.find(s=>s.title===title).rows);

test('a future\'s terms are its booking facts, its position and the contract terms of the editor, each exact',()=>{
 const sections=tradeTerms(details.future,'UTC',formats);
 assert.deepEqual(sections.map(s=>s.title),['Trade','Position','Contract']);
 assert.deepEqual(rowsOf(sections,'Trade'),{Trade:'TR-000001 · IN-000001',Revision:'1 · Booked',Book:'Synthetic portfolio · Book A',Trader:'Synthetic trader','Trade date':'2026-10-24','Legal entity':'Synthetic entity 1',Counterparty:'Synthetic party 1',Executed:'Oct 24, 2026, 5:53 AM',Memo:'None'});
 assert.deepEqual(rowsOf(sections,'Position'),{Lots:'2','Execution price':'p:2.95'});
 const contract=rowsOf(sections,'Contract');
 assert.equal(contract.Product,'Synthetic fixed-lot gas future');assert.equal(contract['Contract size'],'40 MMBtu (IT) per lot');assert.equal(contract['Generated price'],'m:3');
});

test('a future\'s executed time follows the user\'s zone, and a contract the catalog no longer offers is said to be unavailable',()=>{
 assert.equal(rowsOf(tradeTerms(details.future,'America/Chicago',formats),'Trade').Executed,'Oct 24, 2026, 12:53 AM');
 const retained={...details.future,catalog:{...details.future.catalog,contracts:[]}};
 assert.deepEqual(rowsOf(tradeTerms(retained,'UTC',formats),'Contract'),{'Contract terms':'Retained contract terms are unavailable in this selectable catalog.'});
});

test('a future\'s revisions are the editor\'s revisions table: every event, its lots and price exact, and the numeric columns named',()=>{
 const table=tradeHistoryTable(details.future,'UTC');
 assert.deepEqual(table.headers,['Revision','Event','Effective','Recorded','Book','Lots','Execution price','Trader ID','Memo','Trade date','Legal entity','Counterparty','Reason']);
 assert.deepEqual(table.numeric,[0,5,6]);
 assert.equal(table.rows.length,details.future.history.length);
 assert.deepEqual(table.rows[0].filter((_,i)=>[0,1,4,5,6,7,8,9,10,11,12].includes(i)),['1','Booked','Book A','2','2.95','trader','','2026-10-24','Synthetic entity 1','Synthetic party 1','Initial booking']);
});

test('the view of a future joins its terms and revisions, and says when the revision cannot be amended and why',()=>{
 const view=tradeDetailView(details.future,'UTC',formats);
 assert.deepEqual(view.terms,tradeTerms(details.future,'UTC',formats));assert.deepEqual(view.history,tradeHistoryTable(details.future,'UTC'));
 assert.deepEqual(view.amend,{allowed:true,reason:null});
 const locked={...details.future,canAmend:false,amendUnavailableReason:'Settlement has been processed.'};
 assert.deepEqual(tradeDetailView(locked,'UTC',formats).amend,{allowed:false,reason:'Settlement has been processed.'});
 assert.deepEqual(tradeDetailView({...locked,amendUnavailableReason:null},'UTC',formats).amend,{allowed:false,reason:'Read-only trade revision'});
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

test('a swap\'s revisions are the editor\'s revisions table', ()=>{
 const table=swapHistoryTable(details.swap,'UTC');
 assert.deepEqual(table.headers,['Revision','Event','Effective','Recorded','Quantity','Trader ID','Memo','Trade date','Legal entity','Counterparty','Reason']);
 assert.deepEqual(table.numeric,[0,4]);
 assert.deepEqual(table.rows[0].filter((_,i)=>[0,1,4,5,6,7,8,9,10].includes(i)),['1','booked','100','trader','','2026-10-24','Synthetic entity 1','Synthetic party 1','Initial booking']);
});

test('the view of a swap joins its terms and revisions and its amendment state',()=>{
 const view=swapDetailView(details.swap,'UTC',formats);
 assert.deepEqual(view.terms,swapTerms(details.swap,'UTC',formats));assert.deepEqual(view.history,swapHistoryTable(details.swap,'UTC'));
 assert.deepEqual(view.amend,{allowed:true,reason:null});
 assert.deepEqual(swapDetailView({...details.swap,canAmend:false,amendUnavailableReason:'Only the latest eligible trade revision can be amended.'},'UTC',formats).amend,{allowed:false,reason:'Only the latest eligible trade revision can be amended.'});
});
