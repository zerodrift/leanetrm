import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {positionRows} from '../dist/desk-model.js';
import {positionSummary,curveView,openCurveTarget,positionTabs,riskEmpty} from '../dist/position-panel.js';
import {chartOptions} from '../dist/chart-view.js';
import {setCurveNames} from '../dist/market-view.js';
const fixture=JSON.parse(readFileSync(new URL('./fixtures/desk.json',import.meta.url),'utf8'));
setCurveNames(fixture.curveNames);
const [gasShape,unreadShape]=fixture.shapes;
// The fixture's rows: a valued pay-fixed swap, a valued future, an expired future, a multi-side swap, a receive-fixed swap
// in euros whose curve cannot be read, and a swap the close cannot value.
const held=Object.fromEntries(fixture.holdings.map(h=>[h.tradeReference,h]));
const rows=Object.fromEntries(positionRows(fixture.holdings).map(r=>[r.reference,r]));
// The exact strings the backend formats, shown as they are: what the panel adds to them is only signs, words and units.
const formats={money:v=>v,quantity:v=>v,price:v=>v,mark:v=>v,markMinusPrice:v=>v};
const summary=reference=>positionSummary(held[reference],rows[reference],formats);
// The fixture's one readable curve, as the shape of the market a row names.
const shapeOf=curve=>({...gasShape,curve});

test('the header names the instrument, trade, revision and book, and the title the market, delivery, side and size',()=>{
 const swap=summary('TR-000001');
 assert.deepEqual([swap.pill,swap.reference,swap.revision,swap.book],['SWAP','TR-000001','1','Book A']);
 assert.equal(swap.market,'Gas forward');assert.deepEqual(swap.commodity,{code:'test_gas',name:'Synthetic gas'});
 assert.equal(swap.subtitle,'Jan 2021 · Pay fixed · 1000 MMBtu (IT)');
 const future=summary('TR-000002');
 assert.deepEqual([future.pill,future.book],['FUT','Book B']);
 assert.equal(future.subtitle,'Jan 2021 · +1 lot · +2500 MMBtu (IT)');
});

test('a multi-side swap says so in its header, and a short future its sign',()=>{
 assert.equal(summary('TR-000004').subtitle,'Jan 2021 · Multi-side · 100 MWh');
 assert.equal(summary('TR-000003').subtitle,'Jan 2021 · −1 lot · −2500 MMBtu (IT)');
});

test('the value carries its measure, sign and currency, and a position the close cannot value shows its reason instead',()=>{
 const swap=summary('TR-000001').value;
 assert.deepEqual([swap.label,swap.currency,swap.amount.text,swap.amount.exact,swap.amount.sign,swap.unavailable],['Present value','USD','−10','-10','negative',null]);
 const future=summary('TR-000002').value;
 assert.deepEqual([future.label,future.amount.text,future.amount.sign],['Execution P&L','−37.5','negative']);
 const expired=summary('TR-000003').value;
 assert.deepEqual([expired.label,expired.amount,expired.unavailable],['Execution P&L',null,'Contract expired · awaiting settlement']);
 assert.equal(summary('TR-000006').value.unavailable,'Missing price at this close');
 assert.equal(summary('TR-000005').value.amount.text,'+15');
});

test('mark minus price is the backend row value, labeled by what the position was done at, and omitted when the backend has none',()=>{
 const swap=summary('TR-000001').markMinusPrice;
 assert.deepEqual([swap.label,swap.text,swap.exact,swap.sign,swap.unit],['Mark − fixed','−0.01','-0.01','negative','USD/MMBtu (IT)']);
 assert.equal(summary('TR-000002').markMinusPrice.label,'Mark − executed');
 assert.equal(summary('TR-000004').markMinusPrice,null,'a multi-side swap has none');
 assert.equal(summary('TR-000003').markMinusPrice,null,'a position the close cannot value has none');
 assert.equal(summary('TR-000006').markMinusPrice,null);
});

test('mark minus price is formatted from its exact string, a rational included, and never from a number',()=>{
 const rational={...held['TR-000001'],markMinusPrice:'-1/3'},seen=[];
 const shown=positionSummary(rational,rows['TR-000001'],{...formats,markMinusPrice:v=>{seen.push(v);return '-0.333';}}).markMinusPrice;
 assert.deepEqual(seen,['-1/3']);
 assert.deepEqual([shown.text,shown.exact,shown.sign],['−0.333','-1/3','negative']);
});

test('a mark minus price on a position the close cannot value is a conflict the panel refuses',()=>{
 const conflict={...held['TR-000003'],markMinusPrice:'0.1'};
 assert.throws(()=>positionSummary(conflict,rows['TR-000003'],formats),{message:'The Blotter returned a mark minus price for a position it cannot value.'});
});

test('the facts are the position\'s own price, mark, size and settlement, each in its unit, and a mark the close cannot give is left out',()=>{
 const facts=reference=>Object.fromEntries(summary(reference).facts.map(f=>[f.label,[f.text,f.unit]]));
 assert.deepEqual(facts('TR-000001'),{'Fixed price':['3.5','USD/MMBtu (IT)'],Mark:['3.49','USD/MMBtu (IT)'],Quantity:['1000','MMBtu (IT)']});
 assert.deepEqual(facts('TR-000002'),{'Executed price':['3.315','USD/MMBtu (IT)'],Mark:['3.3','USD/MMBtu (IT)'],Quantity:['+2500','MMBtu (IT)']});
 assert.deepEqual(facts('TR-000004'),{'Fixed price':['None',null],Mark:['Several marks',null],Quantity:['100','MWh']});
 assert.deepEqual(facts('TR-000003'),{'Executed price':['3.4','USD/MMBtu (IT)'],Quantity:['−2500','MMBtu (IT)']});
 const settled={...held['TR-000002'],settlement:'unprocessed'};
 assert.deepEqual(positionSummary(settled,rows['TR-000002'],formats).facts.at(-1),{label:'Settlement',text:'Unprocessed',unit:null});
});

test('the curve tab draws the desk\'s curve shape, marks the delivery period and puts the trade price on the same axis',()=>{
 const view=curveView(gasShape,held['TR-000001'],rows['TR-000001'],formats);
 assert.equal(view.state,'chart');
 const {spec}=view;
 assert.deepEqual([spec.axis,spec.unit,spec.kind],['delivery','USD/MMBtu (IT)','line']);
 assert.deepEqual(spec.series[0].points.map(p=>[p.x,p.label,p.y]),gasShape.points.map(p=>[p.contractId,{ '2021-01-01':'Jan 2021','2021-02-01':'Feb 2021','2021-03-01':'Mar 2021','2021-04-01':'Apr 2021'}[p.deliveryFrom],p.price]));
 assert.deepEqual(spec.marked,['k0000000-0000-0000-0000-000000000001']);assert.equal(spec.subtitle,'Marked: delivery Jan 2021');
 assert.deepEqual(spec.reference,{value:'3.5',label:'Fixed 3.5 USD/MMBtu (IT)'});
 assert.equal(view.note,null);
 assert.doesNotThrow(()=>chartOptions(spec));
});

test('a position over several contracts marks each of them, and one outside the curve marks none',()=>{
 const quarter={...rows['TR-000001'],deliveryFrom:'2021-02-01',deliveryTo:'2021-05-01'};
 assert.deepEqual(curveView(gasShape,held['TR-000001'],quarter,formats).spec.marked,['k0000000-0000-0000-0000-000000000002','k0000000-0000-0000-0000-000000000003','k0000000-0000-0000-0000-000000000004']);
 const outside={...rows['TR-000001'],deliveryFrom:'2022-01-01',deliveryTo:'2022-02-01'};
 const view=curveView(gasShape,held['TR-000001'],outside,formats);
 assert.deepEqual(view.spec.marked,[]);assert.equal(view.spec.subtitle,undefined);assert.match(view.note,/delivery is not on the curve/);
});

test('a curve still being read says it is loading, and one that cannot be shown says why, as the row does',()=>{
 assert.deepEqual(curveView(undefined,held['TR-000001'],rows['TR-000001'],formats),{state:'loading'});
 const reason='Curve shape unavailable · the curve read failed';
 assert.deepEqual(curveView(reason,held['TR-000001'],rows['TR-000001'],formats),{state:'unavailable',reason});
 assert.deepEqual(curveView(unreadShape,held['TR-000005'],rows['TR-000005'],formats),{state:'unavailable',reason:"Curve shape unavailable · this market's curve is not in the definitions of this close"});
});

test('the trade price is not drawn on a curve quoted in another unit, nor when the position has no price, and the panel says so',()=>{
 const euro={...gasShape,unit:'EUR/MMBtu (IT)'};
 const other=curveView(euro,held['TR-000001'],rows['TR-000001'],formats);
 assert.equal(other.state,'chart');assert.equal(other.spec.reference,undefined);
 assert.equal(other.note,'The fixed price is in USD/MMBtu (IT) and the curve in EUR/MMBtu (IT), so the price is not drawn on it.');
 const none=curveView(shapeOf('power_forward'),held['TR-000004'],rows['TR-000004'],formats);
 assert.equal(none.spec.reference,undefined);assert.equal(none.note,'This position has no fixed price to draw.');
});

test('a shape of another market is refused instead of drawn',()=>{
 assert.throws(()=>curveView(gasShape,held['TR-000002'],rows['TR-000002'],formats),{message:'The curve shape belongs to another market than the position.'});
 assert.throws(()=>openCurveTarget(held['TR-000001'],rows['TR-000001'],shapeOf('gas_future')),{message:'The curve shape belongs to another market than the position.'});
});

test('a price is labeled with its own text and unit, not the curve\'s', ()=>{
 const view=curveView(shapeOf('gas_future'),held['TR-000002'],rows['TR-000002'],{...formats,price:v=>`${v}0`});
 assert.equal(view.spec.reference.label,'Executed 3.3150 USD/MMBtu (IT)');assert.equal(view.spec.reference.value,'3.315');
});

test('Open curve names the curve and the one contract the position is in, or the curve alone when there is none',()=>{
 assert.deepEqual(openCurveTarget(held['TR-000002'],rows['TR-000002'],shapeOf('gas_future')),{curve:'gas_future',contractId:'c0000000-0000-0000-0000-000000000002'},'a future is in its own contract, curve read or not');
 assert.deepEqual(openCurveTarget(held['TR-000002'],rows['TR-000002'],undefined),{curve:'gas_future',contractId:'c0000000-0000-0000-0000-000000000002'});
 assert.deepEqual(openCurveTarget(held['TR-000001'],rows['TR-000001'],gasShape),{curve:'gas_forward',contractId:'k0000000-0000-0000-0000-000000000001'},'a swap in one contract names it');
 const quarter={...rows['TR-000001'],deliveryFrom:'2021-02-01',deliveryTo:'2021-05-01'};
 assert.deepEqual(openCurveTarget(held['TR-000001'],quarter,gasShape),{curve:'gas_forward',contractId:null},'a swap over several contracts names none');
 assert.deepEqual(openCurveTarget(held['TR-000001'],rows['TR-000001'],undefined),{curve:'gas_forward',contractId:null});
 assert.deepEqual(openCurveTarget(held['TR-000001'],rows['TR-000001'],'Curve shape unavailable'),{curve:'gas_forward',contractId:null});
});

test('the tabs are Curve, Terms, Risk and History, and the Risk tab says its results are not here yet',()=>{
 assert.deepEqual(positionTabs.map(([id,label])=>[id,label]),[['curve','Curve'],['terms','Terms'],['risk','Risk'],['history','History']]);
 assert.equal(riskEmpty,"A risk run's results for this position will show here.");
});
