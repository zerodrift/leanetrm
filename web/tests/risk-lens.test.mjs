import test from 'node:test';
import assert from 'node:assert/strict';
import {changeChart,changeScales,gapsText,largestMove,lensCells,madeHere,positionKey,positionMoves,runBar,runExclusions,runGaps,runSummary,tileWords,unmovedNote} from '../dist/risk-lens.js';

// Synthetic reports as a portfolio risk run states them: each factor lists every holding it was run over, an unaffected one with
// unchanged values, and the run lists the positions its close cannot value.
const usd={revision:{catalogId:'c1',revisionId:'r1'},code:'usd'},eur={revision:{catalogId:'c1',revisionId:'r1'},code:'eur'};
const gas='1 USD / MMBtu (IT)',power='1 USD / MWh';
const values=(upPnl,downPnl,o={})=>({base:'100',up:'100',down:'100',upPnl,downPnl,delta:'0',gamma:'0',display:{base:'100.000000000000',up:'100.000000000000',down:'100.000000000000',upPnl:'0',downPnl:'0',delta:'0',gamma:'0'},
 currency:'USD',currencyReference:usd,deltaUnit:'USD / (1 USD / MMBtu (IT))',gammaUnit:'USD / (1 USD / MMBtu (IT))²',...o});
const holding=(n,v,o={})=>({tradeId:`t${n}`,bindingId:`b${n}`,bookId:'book-a',book:'Book A',valuationId:`v${n}`,tradeReference:`TR-00000${n}`,instrumentReference:`IN-00000${n}`,
 instrumentType:'future',measure:'futures_execution_pnl',measureLabel:'Futures execution P&L',values:v,...o});
const factor=(id,unit,bump,positions)=>({factor:{id,label:`Synthetic ${id}`,coordinate:'market_quote',kind:'contract',unit},bump,totals:[],positions});
const left=[{tradeId:'t9',bindingId:'b9',bookId:'book-b',tradeReference:'TR-000009',instrumentType:'future',reason:'source_expired'}];
const report=(factors,excludedPositions=left)=>({factors,excludedPositions,scenario:{depth:0,isBase:true},context:{contextId:'x1'},snapshotId:'s1'});
const key=n=>positionKey({tradeId:`t${n}`,bindingId:`b${n}`});

test('a position moved by two factors has two shocks, each with its own results, and no value combined from them',()=>{
 const cells=lensCells([report([
  factor('jan',gas,'0.05',[holding(1,values('25','-25',{up:'125',down:'75',delta:'500',gamma:'0'})),holding(2,values('0','0'))]),
  factor('feb',gas,'0.05',[holding(1,values('7','-7',{delta:'140'})),holding(2,values('-32','32'))]),
 ])]);
 const cell=cells.get(key(1));
 assert.deepEqual(Object.keys(cell).sort(),['shocks','tradeReference'],'a cell states its shocks, and nothing that combines them');
 assert.equal(cell.tradeReference,'TR-000001');
 assert.deepEqual(cell.shocks.map(s=>[s.factor,s.label,s.bump,s.unit,s.up,s.down,s.delta]),[['jan','Synthetic jan','0.05',gas,'25','-25','500'],['feb','Synthetic feb','0.05',gas,'7','-7','140']]);
 assert.deepEqual([cell.shocks[0].base,cell.shocks[0].upValue,cell.shocks[0].downValue,cell.shocks[0].gamma],['100','125','75','0'],'each shock keeps its own base, up and down values and gamma');
 assert.deepEqual([cell.shocks[0].deltaUnit,cell.shocks[0].gammaUnit,cell.shocks[0].measureLabel,cell.shocks[0].currency],['USD / (1 USD / MMBtu (IT))','USD / (1 USD / MMBtu (IT))²','Futures execution P&L','USD']);
 assert.deepEqual(cells.get(key(2)).shocks.map(s=>s.factor),['feb'],'a factor that leaves a position unchanged is not one of its shocks');
});

test('a position the run values but no factor moves has a cell without shocks; one the run left out has none',()=>{
 const cells=lensCells([report([factor('jan',gas,'0.05',[holding(1,values('25','-25')),holding(3,values('0','0'))])])]);
 assert.deepEqual(cells.get(key(3)).shocks,[]);
 assert.equal(cells.has(key(9)),false);
});

test('the parts of a run merge by factor in the order they were run, without stating a factor twice',()=>{
 const jan=factor('jan',gas,'0.05',[holding(1,values('25','-25'))]),feb=factor('feb',power,'0.5',[holding(1,values('-3','3'))]);
 const cells=lensCells([report([jan]),report([feb,structuredClone(jan)])]);
 assert.deepEqual(cells.get(key(1)).shocks.map(s=>[s.factor,s.unit]),[['jan',gas],['feb',power]]);
});

test('a factor two parts state with different results is refused, never chosen between',()=>{
 const jan=factor('jan',gas,'0.05',[holding(1,values('25','-25'))]),other=factor('jan',gas,'0.05',[holding(1,values('26','-26'))]);
 assert.throws(()=>lensCells([report([jan]),report([other])]),{message:'The parts of this run state one factor twice with different results, so its results are not shown. Run it again.'});
});

test('a run counts each factor once, and its largest move is chosen by exact comparison: −32 over 25',()=>{
 const reports=[report([factor('jan',gas,'0.05',[holding(1,values('25','-25')),holding(2,values('-32','32'))])]),report([factor('feb',gas,'0.05',[holding(1,values('31.9999999999999999999','-31.9999999999999999999'))])])];
 const summary=runSummary(reports,left);
 assert.equal(summary.factors,2);
 assert.equal(summary.largest.length,1);
 assert.deepEqual([summary.largest[0].change,summary.largest[0].tradeReference,summary.largest[0].direction,summary.largest[0].shock.factor,summary.largest[0].currency],['-32','TR-000002','up','jan','USD']);
 assert.equal(summary.excluded,1,'the run says how many positions it leaves out');
});

test('a rational change is compared exactly, and a tie keeps the move the run states first',()=>{
 const reports=[report([factor('jan',gas,'0.05',[holding(1,values('97/3','-97/3')),holding(2,values('-32.3','32.3'))])])];
 assert.equal(runSummary(reports,[]).largest[0].change,'97/3','97/3 is larger than 32.3, which a rounded number could not tell');
 assert.deepEqual([runSummary([report([factor('jan',gas,'0.05',[holding(1,values('5','-5'))])])],[]).largest[0].direction],['up']);
});

test('the largest move is chosen within each currency, never across them, and a run that moves nothing has none',()=>{
 const euro=values('-40','40',{currency:'EUR',currencyReference:eur});
 const summary=runSummary([report([factor('jan',gas,'0.05',[holding(1,values('25','-25')),holding(2,euro)])])],[]);
 assert.deepEqual(summary.largest.map(m=>[m.currency,m.change]),[['EUR','-40'],['USD','25']]);
 assert.deepEqual(runSummary([report([factor('jan',gas,'0.05',[holding(1,values('0','0'))])])],[]).largest,[]);
 assert.equal(runSummary([report([factor('jan',gas,'0.05',[holding(1,values('0','0'))])])],[]).excluded,0);
});

test('a position\'s own largest move is one of its shocks, chosen, never combined',()=>{
 const cells=lensCells([report([factor('jan',gas,'0.05',[holding(1,values('5','-5'))]),factor('feb',gas,'0.05',[holding(1,values('-8','8.5'))])])]);
 const move=largestMove(key(1),cells.get(key(1)));
 assert.deepEqual([move.change,move.direction,move.shock.factor,move.positionKey,move.tradeReference],['8.5','down','feb',key(1),'TR-000001']);
 assert.equal(largestMove(key(3),{tradeReference:'TR-000003',shocks:[]}),null);
});

test('every part of a run states which positions it leaves out, and every part leaves out the same ones',()=>{
 assert.deepEqual(runExclusions([report([]),report([])]),left);
 const missing=report([]);delete missing.excludedPositions;
 assert.throws(()=>runExclusions([report([]),missing]),{message:'A result of this run does not say which positions it leaves out, so its results are not shown. Run it again.'});
 assert.throws(()=>runExclusions([report([]),report([],[])]),{message:'The parts of this run leave out different positions, so its results are not shown. Run it again.'});
});

test('the change chart draws a bar for each position one shock moves, signed, and names those several shocks move and those none moves',()=>{
 const cells=lensCells([report([
  factor('jan',gas,'0.05',[holding(1,values('25','-25')),holding(2,values('-4','4')),holding(3,values('0','0')),holding(4,values('-32','32'))]),
  factor('feb',gas,'0.05',[holding(1,values('0','0')),holding(2,values('-1','1')),holding(3,values('0','0')),holding(4,values('0','0'))]),
 ])]);
 const rows=[1,2,3,4].map(n=>({key:key(n),reference:`TR-00000${n}`,market:`Market ${n}`,commodity:n%2?'test_gas':'test_power'}));
 const colors=new Map([['test_gas','#eb6834'],['test_power','#2a78d6']]);
 // Stands in for the backend's display text of each position: it keeps the exact value's own leading sign.
 const {panels,unmoved}=changeChart(rows,cells,colors,(index,value)=>value.replace(/^(-?)/,`$1#${index}:`));
 assert.equal(panels.length,1);
 const [panel]=panels;
 assert.equal(panel.title,'USD · bar right = gain');
 assert.equal(panel.unit,'USD');assert.equal(panel.orientation,'horizontal');assert.equal(panel.colorBySign,true);
 assert.deepEqual(panel.series[0].points.map(p=>[p.label,p.y]),[['TR-000001 · Market 1','25'],['TR-000004 · Market 4','-32']],'largest gain first, and a loss to the left');
 assert.deepEqual(panel.series[0].points.map(p=>p.text),['+#0:25','−#3:32'],'each bar states its signed change in its own position\'s format');
 assert.equal(panel.subtitle,'Not drawn: TR-000002 · 2 shocks');
 assert.deepEqual(unmoved,['TR-000003']);
});

test('the change bars of the lens share one scale per currency, the largest magnitude the lens draws, chosen exactly',()=>{
 const euro=values('-40','40',{currency:'EUR',currencyReference:eur});
 const cells=lensCells([report([factor('jan',gas,'0.05',[holding(1,values('25','-25.5')),holding(2,euro),holding(3,values('-90','90'))]),factor('feb',gas,'0.05',[holding(3,values('1','-1'))])])]);
 const scales=changeScales(cells);
 assert.deepEqual([...scales.values()].sort(),['25.5','40'],'a position several shocks move draws no bar, so it sets no scale');
});

// A run as the report hands it to the desk: its setup's words, its parts and what became of each, and where and when it was made.
const at='2020-12-18T21:00:00Z',context={contextId:'x1',effectiveOn:'2020-12-18',knownAt:'2026-10-25T15:41:00Z',knownThrough:3};
const run=o=>({key:1,queued:false,retained:false,setup:{report:'held',label:'Shock every held contract',sizes:{[gas]:'0.05'},withoutTarget:[]},
 setupText:'Shock every held contract · ±0.05 1 USD / MMBtu (IT) · every held price shocked',context,asOf:at,finishedAt:'2026-10-25T15:42:00Z',factors:65,
 parts:[{report:report([]),unknown:false},{report:report([]),unknown:false}],...o});

test('the run bar says what ran: the report and its sizes, how many it shocked, the close, the knowledge and when, and what it leaves out',()=>{
 const bar=runBar(run({}),{zone:'UTC',knowledge:'current knowledge',results:{state:'shown',excluded:left}});
 assert.equal(bar.title,'Risk report · Shock every held contract · ±0.05 1 USD / MMBtu (IT) · every held price shocked');
 assert.equal(bar.detail,'65 held contracts · Dec 18, 2020, 9:00 PM close · current knowledge · ran Oct 25, 2026, 3:42 PM · 2 parts');
 assert.deepEqual(bar.notes,[{kind:'note',text:'Leaves out 1 position this close cannot value: TR-000009 (Contract expired · awaiting settlement).'}]);
 assert.deepEqual([bar.exportable,bar.exportNote,bar.exportShown],[true,null,true]);
});

test('a queued run says how its jobs stand, and once they are done when they finished',()=>{
 const job=(state,finishedAt=null)=>({state,finishedAt});
 const running=runBar(run({queued:true,finishedAt:undefined,parts:[{report:report([]),job:job('completed','2026-10-25T15:43:00Z'),unknown:false},{job:job('running'),unknown:false},{job:job('queued'),unknown:false}]}),
  {zone:'UTC',knowledge:'current knowledge',results:{state:'none'}});
 assert.ok(running.detail.endsWith(' · queued as 3 jobs: 1 completed, 1 running, 1 queued'),running.detail);
 assert.deepEqual([running.exportable,running.exportNote],[false,'The download needs the results of every part of the run.']);
 const done=runBar(run({queued:true,finishedAt:undefined,parts:[{report:report([]),job:job('completed','2026-10-25T15:43:00Z'),unknown:false},{report:report([]),job:job('completed','2026-10-25T15:44:00Z'),unknown:false}]}),
  {zone:'UTC',knowledge:'current knowledge',results:{state:'shown',excluded:[]}});
 assert.ok(done.detail.endsWith(' · queued as 2 jobs, completed Oct 25, 2026, 3:44 PM'),done.detail);
});

test('the run bar says which part failed or has no result, and why its results are not shown when they are not',()=>{
 const bar=runBar(run({parts:[{failure:'Synthetic refusal.',unknown:false},{report:report([]),unknown:false},{unknown:true}]}),
  {zone:'UTC',knowledge:'current knowledge',results:{state:'failed',reason:'The parts of this run leave out different positions, so its results are not shown. Run it again.'}});
 assert.deepEqual(bar.notes,[{kind:'failed',text:'Part 1 of 3 failed: Synthetic refusal.'},{kind:'missing',text:'Part 3 of 3 has no results: its submission has no known outcome.'},
  {kind:'alert',text:'The parts of this run leave out different positions, so its results are not shown. Run it again.'}]);
 assert.equal(bar.exportShown,false,'results that are refused are not offered for download');
 const retained=runBar(run({retained:true,setup:undefined,setupText:'A retained run: its report and sizes are not recorded with it, and each shock states its size.',factors:null,finishedAt:undefined,queued:true,
  parts:[{report:report([factor('jan',gas,'0.05',[])]),job:{state:'completed',finishedAt:'2026-10-25T15:44:00Z'},unknown:false}]}),{zone:'UTC',knowledge:null,results:{state:'elsewhere'}});
 assert.equal(retained.detail,'1 shock · Dec 18, 2020, 9:00 PM close · known at Oct 25, 2026, 3:41 PM · queued as a job, completed Oct 25, 2026, 3:44 PM');
 assert.deepEqual(retained.notes,[{kind:'note',text:'This run was made at another close, context or books than the Blotter read on screen, so its results are not shown in the positions. Export downloads them.'}]);
 const stressed=report([]);stressed.scenario={depth:2,isBase:false};
 assert.deepEqual(runBar(run({parts:[{report:stressed,unknown:false}]}),{zone:'UTC',knowledge:'current knowledge',results:{state:'pivot',excluded:[]}}).notes,
  [{kind:'note',text:'Around 2 nested stresses: the base, up and down values all include them.'},
   {kind:'note',text:"Risk results are by position, which totals do not show. Export downloads them for every factor; a run made with Display set to Individual trades shows each position's shocks in its row and in the Risk tab of its panel."}]);
});

test('a run is drawn only against a read of its own close, context and books: anything else is made elsewhere',()=>{
 const here={contextId:'x1',asOf:at,snapshotId:'s1',bookIds:['b2','b1'],portfolioIds:[]};
 const close=snapshotId=>({...report([]),snapshotId});
 const made=o=>run({scope:{bookIds:['b1','b2'],portfolioIds:[]},parts:[{report:close('s1'),unknown:false},{report:close('s1'),unknown:false}],...o});
 assert.equal(madeHere(made({}),here),true,'the same books in another order are the same books');
 assert.equal(madeHere(made({parts:[{report:close('s1'),unknown:false},{report:close('s2'),unknown:false}]}),here),false,'a part made at another close of the date and context');
 assert.equal(madeHere(made({asOf:'2020-12-17T21:00:00Z'}),here),false,'another close time');
 assert.equal(madeHere(made({context:{...context,contextId:'x2'}}),here),false,'another market context');
 assert.equal(madeHere(made({scope:{bookIds:['b1'],portfolioIds:[]}}),here),false,'other books');
 assert.equal(madeHere(made({scope:{bookIds:['b1','b2'],portfolioIds:['p1']}}),here),false,'other portfolios');
 assert.equal(madeHere(made({scope:undefined}),here),false,'a run that does not state its books is never taken for this read\'s');
 assert.equal(madeHere(made({parts:[{report:close('s1'),unknown:false},{failure:'Synthetic refusal.',unknown:false}]}),here),true,'a part with no report states no close');
});

test('a part with no results says why, and several say which; a part still under way is not a gap',()=>{
 const job=state=>({state,finishedAt:null});
 const gaps=runGaps(run({parts:[{failure:'Synthetic refusal.',unknown:false},{report:report([]),unknown:false},{unknown:true},{unknown:false,unlisted:true},{unknown:false},{job:job('running'),unknown:false}]}));
 assert.deepEqual(gaps.map(g=>[g.part,g.parts,g.text]),[[1,6,'Part 1 of 6 failed: Synthetic refusal'],[3,6,'Part 3 of 6 has no known outcome'],[4,6,'Part 4 of 6 is missing from the job history'],[5,6,'Part 5 of 6 is not yet queued']]);
 assert.equal(gapsText(gaps.slice(0,1)),'Part 1 of 6 failed: Synthetic refusal');
 assert.equal(gapsText(gaps),'Parts 1, 3, 4 and 5 of 6 have no results');
 assert.equal(gapsText(gaps.slice(1,3)),'Parts 3 and 4 of 6 have no results');
 assert.deepEqual(runGaps(run({parts:[{failure:'Synthetic refusal.',unknown:false}]})).map(g=>g.text),['The run failed: Synthetic refusal']);
 assert.deepEqual(runGaps(run({})),[]);
});

test('the run bar names a part the job history no longer lists as missing from it, never as not yet queued',()=>{
 const bar=runBar(run({parts:[{report:report([]),unknown:false},{unknown:false,unlisted:true},{unknown:false}]}),{zone:'UTC',knowledge:'current knowledge',results:{state:'none'}});
 assert.deepEqual(bar.notes,[{kind:'missing',text:'Part 2 of 3 has no results: it is missing from the job history.'},{kind:'missing',text:'Part 3 of 3 has no results: it is not yet queued.'}]);
 const retained=runBar(run({queued:true,retained:true,finishedAt:undefined,parts:[{report:report([]),job:{state:'completed',finishedAt:'2026-10-25T15:44:00Z'},unknown:false},{unknown:false,unlisted:true}]}),
  {zone:'UTC',knowledge:null,results:{state:'none'}});
 assert.ok(retained.detail.endsWith(' · queued as 2 jobs: 1 completed, 1 missing from the job history'),retained.detail);
});

test('a position no shock with results moves is not known while a part has no results, and not moved only when every part has results',()=>{
 const [gap]=runGaps(run({parts:[{report:report([]),unknown:false},{failure:'Synthetic refusal.',unknown:false}]}));
 const moved=lensCells([report([factor('jan',gas,'0.05',[holding(1,values('25','-25'))])])]).get(key(1));
 assert.deepEqual(positionMoves(moved,[gap]),{state:'shocks',shocks:moved.shocks});
 assert.deepEqual(positionMoves({tradeReference:'TR-000003',shocks:[]},[gap]),{state:'unknown',reason:'Part 2 of 2 failed: Synthetic refusal'});
 assert.deepEqual(positionMoves({tradeReference:'TR-000003',shocks:[]},[]),{state:'unmoved'});
 assert.equal(unmovedNote(['TR-000003','TR-000004'],[]),'Not moved by any shock: TR-000003, TR-000004');
 assert.equal(unmovedNote(['TR-000003'],[gap]),'Not known, as part 2 of 2 failed: Synthetic refusal: TR-000003');
 assert.equal(unmovedNote([],[gap]),null);
});

test('the risk tile says a move is the largest known one while a part has no results, and says so when nothing moves',()=>{
 const [gap]=runGaps(run({parts:[{report:report([]),unknown:false},{unknown:false,unlisted:true}]}));
 assert.deepEqual(tileWords({largest:1,gaps:[],unknown:0,excluded:0}),{lead:'Largest move',empty:null,note:null});
 assert.deepEqual(tileWords({largest:0,gaps:[],unknown:0,excluded:2}),{lead:'Largest move',empty:"No position moves by this run's shocks.",note:'Leaves out 2 positions this close cannot value'});
 assert.deepEqual(tileWords({largest:2,gaps:[gap],unknown:3,excluded:1}),{lead:'Largest known move',empty:null,note:'Leaves out 1 position this close cannot value · Not known for 3 positions · Part 2 of 2 is missing from the job history'});
 assert.deepEqual(tileWords({largest:0,gaps:[gap],unknown:24,excluded:0}),{lead:'Largest known move',empty:'No known move: Part 2 of 2 is missing from the job history',note:'Not known for 24 positions · Part 2 of 2 is missing from the job history'});
 assert.equal(tileWords({largest:1,gaps:[gap],unknown:0,excluded:0}).note,'Part 2 of 2 is missing from the job history');
});
