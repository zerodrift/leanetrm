import test from 'node:test';
import assert from 'node:assert/strict';
import {contractPositions,deltaLadder,ladderExport} from '../dist/delta-ladder.js';

const gas='1 USD / MMBtu (IT)',power='1 USD / MWh';
const amounts=(delta,upPnl,downPnl,gamma='0')=>({base:'1',up:'2',down:'0',upPnl,downPnl,delta,gamma});
const values=(delta,upPnl=delta,downPnl=delta.startsWith('-')?delta.slice(1):'-'+delta,gamma='0',unit=gas,currency='USD')=>
 ({...amounts(delta,upPnl,downPnl,gamma),display:amounts(`d:${delta}`,`u:${upPnl}`,`n:${downPnl}`,gamma),currency,currencyReference:{code:currency.toLowerCase()},deltaUnit:`${currency} / (${unit})`,gammaUnit:`${currency} / (${unit})²`});
const total=(book,measure,v)=>({bookId:`id-${book}`,book,measure,measureLabel:{futures_execution_pnl:'Futures execution P&L',swap_present_value:'Swap PV'}[measure],values:v});
const factor=(id,curves,coordinate,unit,from,to,label,totals)=>({factor:{id,label,coordinate,kind:'contract',unit,curves,deliveryFrom:from,deliveryTo:to},bump:'0.05',totals,positions:[]});
const report=factors=>({factors,excludedPositions:[],scenario:{depth:0,isBase:true},context:{contextId:'c1'},snapshotId:'s1'});
const names=new Map([['future','Gas futures'],['base_forward','Gas forwards'],['daily_gas','Daily gas'],['hub_power','Hub power forwards']]);
const curveName=code=>{if(!names.has(code))throw Error('unnamed '+code);return names.get(code);};

// Two parts of one run, as a queued run returns them: a futures quote, a computed point built from it, and daily gas.
const run=[
 report([
  factor('f-jan','future'.split(),'market_quote',gas,'2021-01-01','2021-02-01','Gas futures · 2021-01-01 to 2021-02-01',[
   total('Book A','futures_execution_pnl',values('200','10','-10')),total('Book B','futures_execution_pnl',values('-80','-4','4')),total('Book B','swap_present_value',values('31753799.37'))]),
  factor('fwd-jan',['base_forward'],'computed_output',gas,null,null,'Gas forwards · 2021-01-01 to 2021-02-01',[
   total('Book A','futures_execution_pnl',values('0','0','0')),total('Book B','futures_execution_pnl',values('0','0','0')),total('Book B','swap_present_value',values('31753799.37'))]),
 ]),
 report([
  factor('d-1',['daily_gas'],'market_quote',gas,'2021-01-01','2021-01-02','Daily gas · 2021-01-01 to 2021-01-02',[
   total('Book A','futures_execution_pnl',values('0','0','0')),total('Book B','futures_execution_pnl',values('0','0','0')),total('Book B','swap_present_value',values('12.9','0.645','-0.640','0.1'))]),
 ]),
];

test('the ladder has a row for each shocked factor, grouped by its curve, and a column for each book and measure the server totals',()=>{
 const ladder=deltaLadder(run,'delta',curveName);
 assert.deepEqual(ladder.columns.map(c=>c.title),['Book A · Futures execution P&L · USD','Book B · Futures execution P&L · USD','Book B · Swap PV · USD']);
 assert.deepEqual(ladder.groups.map(g=>[g.title,g.rows.map(r=>r.factorId)]),[['Gas futures',['f-jan']],['Gas forwards',['fwd-jan']],['Daily gas',['d-1']]],'groups follow the order the run returns its factors in');
 const [jan]=ladder.groups[0].rows;
 assert.equal(jan.period,'Jan 2021');assert.equal(jan.coordinate,'Quote');assert.equal(jan.shock,'±0.05 1 USD / MMBtu (IT)');assert.equal(jan.priceUnit,gas);
 assert.equal(ladder.groups[2].rows[0].period,'Jan 1, 2021','a daily contract is named by its day');
});

test('each cell is the server\'s own display of the chosen value, with its exact amount and unit, and nothing is computed',()=>{
 const [jan]=deltaLadder(run,'delta',curveName).groups[0].rows;
 assert.deepEqual(jan.cells.map(c=>[c.text,c.exact,c.unit]),[['d:200','200','USD / (1 USD / MMBtu (IT))'],['d:-80','-80','USD / (1 USD / MMBtu (IT))'],['d:31753799.37','31753799.37','USD / (1 USD / MMBtu (IT))']]);
 const up=deltaLadder(run,'upPnl',curveName).groups[0].rows[0];
 assert.deepEqual(up.cells.map(c=>[c.text,c.unit]),[['u:10','USD'],['u:-4','USD'],['u:31753799.37','USD']]);
 assert.deepEqual(deltaLadder(run,'downPnl',curveName).groups[0].rows[0].cells.map(c=>c.text),['n:-10','n:4','n:-31753799.37']);
});

test('a zero is shown as the zero the server returned, and a cell whose up and down are not mirror images is marked nonlinear',()=>{
 const ladder=deltaLadder(run,'delta',curveName);
 const forward=ladder.groups[1].rows[0];
 assert.deepEqual(forward.cells.map(c=>[c.text,c.zero]),[['d:0',true],['d:0',true],['d:31753799.37',false]]);
 assert.equal(forward.coordinate,'Computed point');
 assert.equal(forward.period,'Gas forwards · 2021-01-01 to 2021-02-01','a factor without delivery dates is named by the server\'s label, never a parsed one');
 const daily=ladder.groups[2].rows[0];
 assert.deepEqual(daily.cells.map(c=>c.nonlinear),[false,false,true]);
 assert.equal(daily.cells[2].up,'u:0.645');assert.equal(daily.cells[2].down,'n:-0.640');
 assert.deepEqual([daily.cells[2].upExact,daily.cells[2].downExact],['0.645','-0.640']);
});

test('a book and measure a factor has no total for is said to be not reported, never zero',()=>{
 const partial=[report([factor('f-jan',['future'],'market_quote',gas,'2021-01-01','2021-02-01','Gas futures',[total('Book A','futures_execution_pnl',values('200'))]),
  factor('d-1',['daily_gas'],'market_quote',gas,'2021-01-01','2021-01-02','Daily gas',[total('Book B','swap_present_value',values('12.9'))])])];
 const ladder=deltaLadder(partial,'delta',curveName);
 assert.deepEqual(ladder.groups.map(g=>g.rows[0].cells.map(c=>c.missing?'not reported':c.text)),[['d:200','not reported'],['not reported','d:12.9']]);
});

test('totals in other currencies are columns of their own, never added to another currency\'s',()=>{
 const euro=[report([factor('f-jan',['future'],'market_quote',gas,'2021-01-01','2021-02-01','Gas futures',[
  total('Book B','swap_present_value',values('5')),total('Book B','swap_present_value',values('7','7','-7','0',gas,'EUR'))])])];
 assert.deepEqual(deltaLadder(euro,'delta',curveName).columns.map(c=>c.title),['Book B · Swap PV · USD','Book B · Swap PV · EUR']);
});

test('a run that returns one factor twice, two totals for one column, or a curve with no name is refused, not merged or guessed',()=>{
 const again=[report([run[0].factors[0]]),report([run[0].factors[0]])];
 assert.throws(()=>deltaLadder(again,'delta',curveName),{message:'This run returns a shocked factor more than once, so its ladder is not shown. Run it again.'});
 const doubled=[report([factor('f-jan',['future'],'market_quote',gas,'2021-01-01','2021-02-01','Gas futures',[total('Book A','futures_execution_pnl',values('1')),total('Book A','futures_execution_pnl',values('2'))])])];
 assert.throws(()=>deltaLadder(doubled,'delta',curveName),{message:'This run returns two totals for one book, measure and currency of a factor, so its ladder is not shown. Run it again.'});
 const unnamed=[report([factor('x',['mystery'],'market_quote',power,'2021-01-01','2021-02-01','Mystery',[])])];
 assert.throws(()=>deltaLadder(unnamed,'delta',curveName),/unnamed mystery/);
});

test('the ladder exports as the rows and columns it shows, with the unit of each value and exact amounts',()=>{
 const ladder=deltaLadder(run,'delta',curveName);
 const {headers,rows,numeric}=ladderExport(ladder,'delta');
 assert.deepEqual(headers,['Curve','Contract','Coordinate','Shock','Price unit','Value','Book A · Futures execution P&L · USD','Book B · Futures execution P&L · USD','Book B · Swap PV · USD']);
 assert.deepEqual(rows[0],['Gas futures','Jan 2021','Quote','±0.05 1 USD / MMBtu (IT)','1 USD / MMBtu (IT)','Delta per 1 price unit','200','-80','31753799.37']);
 assert.deepEqual(numeric,[6,7,8]);
 assert.equal(rows.length,3);
 const partial=deltaLadder([report([factor('f-jan',['future'],'market_quote',gas,'2021-01-01','2021-02-01','Gas futures',[total('Book A','futures_execution_pnl',values('200'))]),
  factor('d-1',['daily_gas'],'market_quote',gas,'2021-01-01','2021-01-02','Daily gas',[total('Book B','swap_present_value',values('12.9'))])])],'upPnl',curveName);
 assert.deepEqual(ladderExport(partial,'upPnl').rows.map(r=>r.slice(5)),[['Up P&L','200','Not reported'],['Up P&L','Not reported','12.9']]);
});

test('a ladder row opens the positions its shock moves, each by book and measure, as the server values them, largest first; none moved says so',()=>{
 const position=(n,book,measure,v)=>({tradeId:`t${n}`,bindingId:`b${n}`,bookId:`id-${book}`,book,tradeReference:`TR-00000${n}`,instrumentReference:`IN-00000${n}`,instrumentType:'financial_swap',measure,measureLabel:'Swap PV',values:v});
 const shocked={...run[0].factors[0],positions:[position(1,'Book B','swap_present_value',values('3971724.92')),position(2,'Book B','swap_present_value',values('0','0','0')),
  position(3,'Book B','swap_present_value',values('-12.5')),position(4,'Book B','swap_present_value',values('7938000'))]};
 const listed=contractPositions(shocked,'delta');
 assert.deepEqual(listed.positions.map(p=>[p.tradeReference,p.text,p.key]),[['TR-000004','d:7938000','["t4","b4"]'],['TR-000001','d:3971724.92','["t1","b1"]'],['TR-000003','d:-12.5','["t3","b3"]']],'by magnitude, compared exactly; the one it does not move is left out');
 assert.equal(listed.unmoved,1);
 assert.deepEqual(listed.positions.map(p=>p.exact),['7938000','3971724.92','-12.5'],'each with its exact amount, for the desk to display in the position\'s own format');
 assert.equal(listed.positions[0].label,'Book B · Swap PV');
 assert.equal(contractPositions({...shocked,positions:[position(2,'Book B','swap_present_value',values('0','0','0'))]},'delta').positions.length,0);
});
