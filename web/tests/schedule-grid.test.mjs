import test from 'node:test';
import assert from 'node:assert/strict';
import {scheduleGridData,scheduleTabs,sourceLabels} from '../dist/schedule-grid.js';

// A synthetic flow product: one physical side kind and one cash kind, with trade measures and a links tab.
const col=(id,label,unit,measure)=>({id,label,unit,measure,input:[]});
const description={product:'synthetic_flow',version:1,rowShape:'flow',tradeFields:[],sideLimits:[],levels:['month','day'],modes:['new'],
 sides:[{kind:'physical',pill:'Physical',colour:'gas',min:1,initial:1,max:2,fields:[],columns:[col('quantity','Quantity','quantity','quantity'),col('market_price','Market price',null,'price'),col('amount','Amount',null,'money')]},
  {kind:'cash',pill:'Payment',colour:'cash',min:0,initial:0,max:2,fields:[],columns:[col('quantity','Quantity','quantity','quantity'),col('price','Price',null,'price'),col('amount','Amount',null,'money')]}],
 tradeColumns:[col('net_cash','Net cash',null,'money'),col('payment_at','Payment at',null,'time')],
 tabs:[{id:'links',label:'Quantity links'}]};
const sides=[{kind:'physical',code:'physical-1',name:'Leg A'},{kind:'cash',code:'cash-1',name:'Payment A'}];
// The page's own instant formatter; the grid only hands it the backend's instants.
const shown={quantityUnit:'MMBtu',instant:at=>`[${at}]`};
const cell=(value,sources=[])=>({value,sources});
const cells=(q,p,a,net,pay)=>({'physical-1.quantity':cell(q,['contract']),'physical-1.market_price':cell(p,['fwd']),'physical-1.amount':cell(a,['contract','fwd']),
 'cash-1.quantity':cell(q==null?null:q.replace(/^-/,''),['contract']),'cash-1.price':cell('3.25',['fixed']),'cash-1.amount':cell(net),
 'trade.net_cash':cell(net),'trade.payment_at':cell(pay)});
const row=(key,parent,level,c,delivered=false)=>({key,parent,level,start:'2026-11-01T14:00:00+00:00',end:'2026-12-01T14:00:00+00:00',operatingDate:level==='day'?key:null,cells:c,delivered});
const view=(over={})=>({levels:['month','day'],valuationAt:'2026-11-01T00:00:00+00:00',delivered:null,remaining:null,deliveredLeaves:0,leaves:2,
 rows:[row('2026-11',null,'month',cells('-2000','4.5','-9000','6500',null)),
  row('2026-11-01','2026-11','day',cells('-1000','4.5','-4500','3250','2026-11-25T14:00:00.0000000+00:00'),true),
  row('2026-11-02','2026-11','day',cells('-1000','4.5','-4500','3250','2026-11-25T14:00:00.0000000+00:00'))],
 // Deliberately not the sum of the rows: the grid must show the backend's totals, never add them up.
 totals:cells('-1234567.5','4.5','-10/3','99','2026-11-25T14:00:00.0000000+00:00'),...over});

test('the instrument tab comes first, one tab per side in order, then the description tabs',()=>{
 assert.deepEqual(scheduleTabs(description,sides),[
  {id:'instrument',label:'Instrument',colour:'neutral'},{id:'physical-1',label:'Leg A',colour:'gas'},
  {id:'cash-1',label:'Payment A',colour:'cash'},{id:'links',label:'Quantity links',colour:'neutral'}]);
});

test('the instrument tab groups each side’s columns under its name and colour, then the trade measures',()=>{
 const g=scheduleGridData(view(),description,sides,'instrument','month',shown);
 assert.deepEqual(g.headers,['Period','Quantity (MMBtu)','Market price','Amount','Quantity (MMBtu)','Price','Amount','Net cash','Payment at']);
 assert.deepEqual(g.headerGroups,[{title:'Leg A',colour:'gas',columns:[1,2,3]},{title:'Payment A',colour:'cash',columns:[4,5,6]},{title:'Trade',colour:'neutral',columns:[7,8]}]);
 assert.deepEqual(g.columns,['period','physical-1.quantity','physical-1.market_price','physical-1.amount','cash-1.quantity','cash-1.price','cash-1.amount','trade.net_cash','trade.payment_at']);
 assert.deepEqual(g.numeric,[1,2,3,4,5,6,7]);
});

test('a unit column shows the unit the preview resolved, and a unit the ticket does not know is refused',()=>{
 assert.deepEqual(scheduleGridData(view(),description,sides,'cash-1','day',{...shown,quantityUnit:'GJ'}).headers,['Period','Quantity (GJ)','Price','Amount']);
 const literal={...description,sides:description.sides.map(k=>({...k,columns:k.columns.map(c=>c.unit?{...c,unit:'MMBtu'}:c)}))};
 assert.throws(()=>scheduleGridData(view(),literal,sides,'cash-1','day',shown),/unit 'MMBtu'/);
});

test('a side tab shows only that side’s columns',()=>{
 const g=scheduleGridData(view(),description,sides,'cash-1','day',shown);
 assert.deepEqual(g.headers,['Period','Quantity (MMBtu)','Price','Amount']);
 assert.deepEqual(g.rows[1],['2026-11-01','1000','3.25','3250']);
 assert.throws(()=>scheduleGridData(view(),description,sides,'links','day',shown),/Tab 'links' does not show a schedule/);
});

test('the row level chooses the rows, and finer rows name their parent row',()=>{
 const month=scheduleGridData(view(),description,sides,'instrument','month',shown);
 assert.deepEqual(month.keys,['2026-11']);assert.deepEqual(month.parents,[null]);
 const day=scheduleGridData(view(),description,sides,'instrument','day',shown);
 assert.deepEqual(day.keys,['2026-11','2026-11-01','2026-11-02']);
 assert.deepEqual(day.parents,[null,'2026-11','2026-11']);
 assert.throws(()=>scheduleGridData(view(),description,sides,'instrument','hour',shown),/The schedule has no hour rows/);
});

test('rows keep the exact strings; only the display text is formatted, by the page’s instant formatter for times, and totals are the view’s own',()=>{
 const g=scheduleGridData(view(),description,sides,'instrument','day',shown);
 assert.deepEqual(g.rows[1],['2026-11-01','-1000','4.5','-4500','1000','3.25','3250','3250','2026-11-25T14:00:00.0000000+00:00']);
 assert.equal(g.rows[0][8],'');
 assert.deepEqual(g.displayRows[1],['2026-11-01','-1,000','4.5','-4,500','1,000','3.25','3,250','3,250','[2026-11-25T14:00:00.0000000+00:00]']);
 assert.deepEqual(g.totals,[['Total','-1,234,567.5','4.5','-10/3','1,234,567.5','3.25','99','99','[2026-11-25T14:00:00.0000000+00:00]']]);
});

test('in delivery the totals are the view’s delivered, remaining and total rows, in that order',()=>{
 const split=cells('-1000','4.5','-4500','3250',null),rest=cells('-1000','4.5','-4500','3250','2026-11-25T14:00:00.0000000+00:00');
 const g=scheduleGridData(view({delivered:split,remaining:rest,deliveredLeaves:1}),description,sides,'cash-1','day',shown);
 assert.deepEqual(g.totals,[['Delivered','1,000','3.25','3,250'],['Remaining','1,000','3.25','3,250'],['Total','1,234,567.5','3.25','99']]);
 assert.throws(()=>scheduleGridData(view({delivered:split}),description,sides,'cash-1','day',shown),/delivered and remaining together/);
});

test('changed marks exactly the cells whose value differs from the previous view',()=>{
 const before=view();
 const after=view({rows:before.rows.map(r=>r.key==='2026-11-02'?{...r,cells:{...r.cells,'physical-1.market_price':cell('4.75',['fwd'])}}:r)});
 const g=scheduleGridData(after,description,sides,'instrument','day',shown,before);
 const changed=g.decorations.flatMap((r,i)=>r.flatMap((d,j)=>d?.changed?[[g.keys[i],j]]:[]));
 assert.deepEqual(changed,[['2026-11-02',2]]);
 assert.ok(scheduleGridData(after,description,sides,'instrument','day',shown).decorations.flat().every(d=>!d?.changed));
});

test('decorations carry the backend’s source labels and delivered rows',()=>{
 const g=scheduleGridData(view(),description,sides,'instrument','day',shown);
 assert.equal(g.decorations[1][0],null);
 assert.deepEqual(g.decorations[1][3],{sources:['Contract','Fwd'],changed:false,delivered:true});
 assert.deepEqual(g.decorations[2][5],{sources:['Fixed'],changed:false,delivered:false});
 assert.equal(sourceLabels.estimated,'Estimated');
});

test('an unknown value source is refused',()=>{
 const v=view();v.rows[0].cells['physical-1.quantity']=cell('-2000',['guessed']);
 assert.throws(()=>scheduleGridData(v,description,sides,'instrument','month',shown),{message:'Unknown value source: guessed'});
});
