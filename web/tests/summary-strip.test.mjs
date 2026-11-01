import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {summaryStripItems,staleStripItems,waitingStripItems} from '../dist/desk-model.js';
const fixture=JSON.parse(readFileSync(new URL('./fixtures/desk.json',import.meta.url),'utf8'));
// Stands in for the backend's display text: it keeps the exact value's own leading sign.
const money=value=>value.replace(/^(-?)/,'$1#');
const notRun={state:'none',reason:'Revalue the books on screen under shared market shocks.'};
const items=(summaries=fixture.summaries,positions={count:6,unvalued:2},risk=notRun)=>summaryStripItems(summaries,positions,risk,money);
const labelled=(list,label)=>list.filter(item=>item.label===label);

test('each currency of swap PV is an item of its own, never added to another',()=>{
 const pv=labelled(items(),'Swap PV');
 assert.deepEqual(pv.map(i=>[i.currency,i.value,i.sign,i.state]),[['EUR','+#15','pos','complete'],['USD','+#220','pos','complete']]);
 assert.equal(pv[1].reason,"Excludes 1 position that can't be valued at this close");
 assert.equal(pv[0].reason,undefined);
});
test('each money total and largest risk move carries the backend exact amount beside its display text',()=>{
 assert.deepEqual(labelled(items(),'Swap PV').map(i=>[i.value,i.exact]),[['+#15','15'],['+#220','220']]);
 assert.equal(labelled(items(),'Futures execution P&L')[0].exact,'-37.5');
 const risk=items(undefined,undefined,{state:'results',label:'Risk · custom scenario · 1 shock',moves:[{currency:'USD',text:'−#4',exact:'-4',sign:'negative',hint:'Largest move: T-1'}],empty:null,note:null}).at(-1);
 assert.deepEqual([risk.value,risk.exact],['−#4','-4']);
 assert.equal(labelled(items(fixture.allUnvalued),'Swap PV')[0].exact,undefined);
 assert.equal(labelled(items(),'Positions')[0].exact,undefined);
});
test('swap PV and futures execution P&L are separate items, also in one currency',()=>{
 const list=items(),usd=list.filter(i=>i.currency==='USD');
 assert.deepEqual(usd.map(i=>[i.label,i.value,i.sign]),[['Swap PV','+#220','pos'],['Futures execution P&L','−#37.5','neg']]);
});
test('the strip reads swap PV, futures execution P&L, positions, not valued and risk, in that order',()=>{
 assert.deepEqual(items().map(i=>i.label),['Swap PV','Swap PV','Futures execution P&L','Positions','Not valued','Risk']);
 const [positions,unvalued]=labelled(items(),'Positions').concat(labelled(items(),'Not valued'));
 assert.deepEqual([positions.value,positions.state,unvalued.value,unvalued.state],['6','complete','2','complete']);
});
test('a count not valued comes with the valued scope, and only with it',()=>{
 assert.throws(()=>items(undefined,{count:6,unvalued:null}),/inconsistent desk summary/);
 assert.throws(()=>items({state:'pending',reason:'Complete selected scope is being valued',totals:['swapPv','futuresPnl']},{count:6,unvalued:0}),/inconsistent desk summary/);
});
test('a total the close cannot state says so with its reason, and is never shown as zero',()=>{
 const pv=labelled(items(fixture.allUnvalued),'Swap PV')[0];
 assert.deepEqual([pv.value,pv.state,pv.reason,pv.sign],['Not valued','unavailable','No valued positions · 12 excluded',undefined]);
});
test('pending summaries are pending with the reason, while the positions read are counted',()=>{
 const list=items({state:'pending',reason:'Complete selected scope is being valued',totals:['swapPv','futuresPnl','ppaValue']},{count:120,unvalued:null});
 for(const label of ['Swap PV','Futures execution P&L','PPA value excluding overdue cash','Not valued'])
  assert.deepEqual(labelled(list,label).map(i=>[i.value,i.state,i.reason]),[['Pending','pending','Complete selected scope is being valued']]);
 assert.deepEqual(labelled(list,'Positions').map(i=>[i.value,i.state]),[['120','complete']]);
 assert.equal(labelled(list,'Heat-rate PV').length,0);
 const failed=items({state:'unavailable',reason:'Refresh to retry valuation',totals:['swapPv','futuresPnl']},{count:120,unvalued:null});
 assert.deepEqual(labelled(failed,'Swap PV').map(i=>[i.value,i.state,i.reason]),[['Unavailable','unavailable','Refresh to retry valuation']]);
});
test('risk that has not run says Not run, with what running it does',()=>{
 assert.deepEqual(labelled(items(),'Risk'),[{label:'Risk',value:'Not run',state:'complete',reason:notRun.reason}]);
});
test('a run states its largest move per currency and its shock count, or why it shows none',()=>{
 const moves=[{currency:'EUR',text:'−#4',exact:'-4',sign:'negative',hint:'Largest move: T-1 · Gas, if Gas moves up'},{currency:'USD',text:'+#9',exact:'9',sign:'positive',hint:'Largest move: T-2 · Power, if Power moves down'}];
 const risk=items(undefined,undefined,{state:'results',label:'Risk · every held contract · 3 shocks',moves,empty:null,note:'Leaves out 2 positions this close cannot value'});
 assert.deepEqual(risk.slice(-2).map(i=>[i.label,i.value,i.currency,i.sign,i.reason]),[
  ['Risk · every held contract · 3 shocks','−#4','EUR','neg','Largest move: T-1 · Gas, if Gas moves up · Leaves out 2 positions this close cannot value'],
  ['Risk · every held contract · 3 shocks','+#9','USD','pos','Largest move: T-2 · Power, if Power moves down · Leaves out 2 positions this close cannot value']]);
 const still=items(undefined,undefined,{state:'results',label:'Risk · custom scenario · 1 shock',moves:[],empty:"No position moves by this run's shocks.",note:null}).at(-1);
 assert.deepEqual([still.value,still.state,still.currency],["No position moves by this run's shocks.",'complete',undefined]);
 const refused=items(undefined,undefined,{state:'unavailable',value:'Results not shown',reason:'Run it again.'}).at(-1);
 assert.deepEqual(refused,{label:'Risk',value:'Results not shown',state:'unavailable',reason:'Run it again.'});
});
test('heat-rate and PPA values are items of their own, per currency, when the books hold them',()=>{
 const summaries=structuredClone(fixture.summaries);
 for(const index of [0,1,3])for(const group of summaries[index].groups)if(group.instrumentType==='financial_swap'&&group.currency==='EUR')group.instrumentType='ppa';
 assert.deepEqual(items(summaries).filter(i=>i.label.startsWith('PPA')).map(i=>[i.label,i.currency,i.value]),[['PPA value excluding overdue cash','EUR','+#15']]);
});
test('a strip kept on screen while the Blotter is read again keeps every last value and its own reason, each flagged stale with why',()=>{
 const reason='Reading the Blotter again · these values are from the previous read.';
 for(const last of [items(),items(fixture.allUnvalued)]){
  const stale=staleStripItems(last,reason);
  assert.deepEqual(stale.map(({stale,...item})=>item),last);
  assert.ok(stale.every(i=>i.stale===reason));
 }
 const excluding=staleStripItems(items(),reason).find(i=>i.label==='Swap PV'&&i.currency==='USD');
 assert.deepEqual([excluding.state,excluding.reason],['complete',"Excludes 1 position that can't be valued at this close"]);
 const unavailable=staleStripItems(items(fixture.allUnvalued),reason).find(i=>i.state==='unavailable');
 assert.ok(unavailable.reason);
});
test('before any values, the strip lists every money total the Blotter can show, waiting, never a figure',()=>{
 for(const state of ['pending','unavailable']){
  const strip=waitingStripItems({state,reason:'Retry to value again'},15,money);
  assert.deepEqual(strip.filter(i=>i.state===state&&i.label!=='Not valued').map(i=>i.label),['Swap PV','Futures execution P&L','Heat-rate PV','PPA value excluding overdue cash','Gas contractual PV','Crude contractual PV']);
  assert.ok(strip.filter(i=>i.state===state).every(i=>i.value===(state==='pending'?'Pending':'Unavailable')&&i.exact===undefined));
  assert.equal(strip.find(i=>i.label==='Positions').value,'15');
 }
});
