import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {positionRows} from '../dist/desk-model.js';
import {positionSummary,curveView,openCurveTarget,positionTabs,riskEmpty,riskTabView,amendNote,amendControl,amendRefusal,curveNote} from '../dist/position-panel.js';
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
 assert.deepEqual([swap.label,swap.text,swap.exact,swap.unit],['Mark − fixed','−0.01','-0.01','USD/MMBtu (IT)']);
 assert.equal(summary('TR-000002').markMinusPrice.label,'Mark − executed');
 assert.equal(summary('TR-000004').markMinusPrice,null,'a multi-side swap has none');
 assert.equal(summary('TR-000003').markMinusPrice,null,'a position the close cannot value has none');
 assert.equal(summary('TR-000006').markMinusPrice,null);
});

test('mark minus price is formatted from its exact string, a rational included, and never from a number',()=>{
 const rational={...held['TR-000001'],markMinusPrice:'-1/3'},seen=[];
 const shown=positionSummary(rational,rows['TR-000001'],{...formats,markMinusPrice:v=>{seen.push(v);return '-0.333';}}).markMinusPrice;
 assert.deepEqual(seen,['-1/3']);
 assert.deepEqual([shown.text,shown.exact],['−0.333','-1/3']);
});

test('mark minus price is a price move, not a gain or a loss: it has its sign in its text and no tone of its own',()=>{
 // Which way a move of the mark cuts depends on the position's direction, and the browser does not combine them into a gain.
 for(const reference of ['TR-000001','TR-000002']){
  const delta=summary(reference).markMinusPrice;
  assert.deepEqual(Object.keys(delta).sort(),['exact','label','text','unit']);
 }
 assert.deepEqual(summary('TR-000001').value.amount.sign,'negative','the value keeps its tone');
});

test('a mark minus price on a position the close cannot value is a conflict the panel refuses',()=>{
 const conflict={...held['TR-000003'],markMinusPrice:'0.1'};
 assert.throws(()=>positionSummary(conflict,rows['TR-000003'],formats),{message:'The Blotter returned a mark minus price for a position it cannot value.'});
});

test('the facts are the position\'s own price, mark, size and settlement, each in its unit, and a mark the close cannot give is left out',()=>{
 const facts=reference=>Object.fromEntries(summary(reference).facts.map(f=>[f.label,[f.text,f.unit]]));
 assert.deepEqual(facts('TR-000001'),{'Fixed price':['3.5','USD/MMBtu (IT)'],Mark:['3.49','USD/MMBtu (IT)'],Quantity:['1000','MMBtu (IT)'],Settlement:['Projection',null]});
 assert.deepEqual(facts('TR-000002'),{'Executed price':['3.315','USD/MMBtu (IT)'],Mark:['3.3','USD/MMBtu (IT)'],Quantity:['+2500','MMBtu (IT)'],Settlement:['Unprocessed',null]});
 assert.deepEqual(facts('TR-000004'),{'Fixed price':['None',null],Mark:['Several marks',null],Quantity:['100','MWh'],Settlement:['Projection',null]});
 assert.deepEqual(facts('TR-000003'),{'Executed price':['3.4','USD/MMBtu (IT)'],Quantity:['−2500','MMBtu (IT)'],Settlement:['Unprocessed',null]});
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

test('the tabs are Curve, Terms, Risk and History, and the Risk tab says its results come from the desk\'s run, which is not there yet',()=>{
 assert.deepEqual(positionTabs.map(([id,label])=>[id,label]),[['curve','Curve'],['terms','Terms'],['risk','Risk'],['history','History']]);
 assert.equal(riskEmpty,"This position's risk comes from the risk run on the desk, and there is none yet. Run one from the Risk report.");
});

// One shock of a run that moves a position, as the lens states it, in exact strings; and a part of the run that has no results.
const shock=o=>({factor:'jan',label:'Synthetic future · Jan 2021',bump:'0.05',unit:'1 USD / MMBtu (IT)',up:'25',down:'-25',delta:'500',deltaUnit:'USD / (1 USD / MMBtu (IT))',
 gamma:'0',gammaUnit:'USD / (1 USD / MMBtu (IT))²',base:'-37.5',upValue:'-12.5',downValue:'-62.5',measureLabel:'Futures execution P&L',currency:'USD',currencyKey:'usd',...o});
const riskFormats={...formats,factor:v=>v};
const gap={part:2,parts:5,text:'Part 2 of 5 failed: Synthetic refusal'},unlisted={part:4,parts:5,text:'Part 4 of 5 is missing from the job history'};

test('the Risk tab lists each shock of the desk\'s run that moves the position, with its base, up and down values, changes, delta and gamma, never combined',()=>{
 const view=riskTabView({state:'shown',factors:65,gaps:[],cell:{tradeReference:'TR-000002',shocks:[shock(),shock({factor:'feb',label:'Synthetic future · Feb 2021',up:'7',down:'-7',delta:'140',upValue:'-30.5',downValue:'-44.5'})]}},riskFormats);
 assert.equal(view.note,"From the risk run on the desk: 2 of its 65 shocks move this position, each on its own: nothing is added across them.");
 assert.deepEqual(view.shocks.map(s=>s.title),['Synthetic future · Jan 2021','Synthetic future · Feb 2021']);
 assert.equal(view.shocks[0].detail,'Shocked up and down by ±0.05 1 USD / MMBtu (IT), everything else held · Futures execution P&L in USD');
 assert.deepEqual(view.shocks[0].facts.map(f=>[f.label,f.text,f.unit,f.exact]),[['Base','−37.5','USD','-37.5'],['Up','−12.5','USD','-12.5'],['Down','−62.5','USD','-62.5'],
  ['Up change','+25','USD','25'],['Down change','−25','USD','-25'],['Delta','+500','USD / (1 USD / MMBtu (IT))','500'],['Gamma','0','USD / (1 USD / MMBtu (IT))²','0']]);
 assert.deepEqual(view.shocks[0].facts.map(f=>f.sign??null),[null,null,null,'positive','negative',null,null],'only a change is told as a gain or a loss');
 assert.equal(riskTabView({state:'shown',factors:65,gaps:[],cell:{tradeReference:'TR-000002',shocks:[shock()]}},riskFormats).note,"From the risk run on the desk: one of its 65 shocks moves this position.");
 assert.equal(riskTabView({state:'shown',factors:65,gaps:[gap],cell:{tradeReference:'TR-000002',shocks:[shock()]}},riskFormats).note,
  "From the risk run on the desk: one shock of the parts with results moves this position. Part 2 of 5 failed: Synthetic refusal, so no shock of it is listed.",'a run with a part missing says so, as a shock of that part may move the position too');
 assert.equal(riskTabView({state:'shown',factors:65,gaps:[gap,unlisted],cell:{tradeReference:'TR-000002',shocks:[shock(),shock({factor:'feb'})]}},riskFormats).note,
  "From the risk run on the desk: 2 shocks of the parts with results move this position, each on its own: nothing is added across them. Parts 2 and 4 of 5 have no results, so no shock of them is listed.");
});

test('the Risk tab says why it lists no results: no run, a run under way, made elsewhere or refused, a position left out, one no shock moves, or one not known',()=>{
 const note=risk=>{const view=riskTabView(risk,riskFormats);assert.deepEqual(view.shocks,[]);return view.note;};
 assert.equal(note({state:'none'}),riskEmpty);
 assert.equal(note({state:'running'}),"The risk run on the desk is under way: this position's results show here once it completes.");
 assert.equal(note({state:'elsewhere'}),'The risk run on the desk was made at another close, context or books than this Blotter read, so its results are not shown for this position.');
 assert.equal(note({state:'failed',reason:'The parts of this run leave out different positions, so its results are not shown. Run it again.'}),'The parts of this run leave out different positions, so its results are not shown. Run it again.');
 assert.equal(note({state:'left',reason:'Contract expired · awaiting settlement'}),'The risk run on the desk leaves this position out, as this close cannot value it: Contract expired · awaiting settlement.');
 assert.equal(note({state:'shown',factors:65,gaps:[],cell:{tradeReference:'TR-000002',shocks:[]}}),"From the risk run on the desk: none of its 65 shocks moves this position.");
 assert.equal(note({state:'shown',factors:1,gaps:[],cell:{tradeReference:'TR-000002',shocks:[]}}),"From the risk run on the desk: its one shock does not move this position.");
 assert.equal(note({state:'shown',factors:49,gaps:[gap],cell:{tradeReference:'TR-000002',shocks:[]}}),
  "From the risk run on the desk: whether its shocks move this position is not known, as part 2 of 5 failed: Synthetic refusal.",'missing results are never read as moving nothing');
 assert.equal(note({state:'empty',reason:'Parts 1 and 2 of 2 have no results'}),'The risk run on the desk has no results. Parts 1 and 2 of 2 have no results.',
  'a run with no results is stated as it stands, never as no run');
});

test('Amend says in words why it is not offered: for the account first, then for the revision once the editor has been read',()=>{
 assert.equal(amendNote(true,undefined),null,'before the editor has been read there is nothing against it');
 assert.equal(amendNote(true,{allowed:true,reason:null}),null);
 const account='This account cannot amend trades in this market context.';
 assert.equal(amendNote(false,undefined),account);
 assert.equal(amendNote(false,{allowed:false,reason:'Settlement has been processed.'}),account,'the account is the first reason');
 assert.equal(amendNote(true,{allowed:false,reason:'Settlement has been processed.'}),'This position cannot be amended: Settlement has been processed.');
});

test('Amend is shown only when the account may amend, and while the form is open it is how the person closes it',()=>{
 assert.deepEqual(amendControl({accountCanAmend:true,revision:undefined,editing:false}),{label:'Amend',note:null});
 assert.deepEqual(amendControl({accountCanAmend:true,revision:{allowed:true,reason:null},editing:false}),{label:'Amend',note:null});
 const account='This account cannot amend trades in this market context.';
 assert.deepEqual(amendControl({accountCanAmend:false,revision:undefined,editing:false}),{label:null,note:account},'no button, and the reason stays in view');
 assert.deepEqual(amendControl({accountCanAmend:true,revision:{allowed:false,reason:'Settlement has been processed.'},editing:false}),{label:null,note:'This position cannot be amended: Settlement has been processed.'});
 assert.deepEqual(amendControl({accountCanAmend:true,revision:{allowed:true,reason:null},editing:true}),{label:'Close editor',note:null});
 assert.deepEqual(amendControl({accountCanAmend:false,revision:undefined,editing:true}),{label:'Close editor',note:null},'a form that is open can always be closed, whatever opened it');
});

test('Open curve says in words when there is no curve to open: for a market Curves & prices does not list',()=>{
 assert.equal(curveNote(true),null);
 assert.equal(curveNote(false),"Curves & prices does not list this position's market, so there is no curve to open.");
});

// A scheduled swap (D214) holds its quantities, fixed prices and marks by period; the panel says so where the desk row does, and
// a position whose periods project from several curves has no one curve to draw or to open.
const scheduledHeld=changes=>({...held['TR-000001'],contractIdentityKind:'scheduled_swap_instrument',quantity:null,fixedPrice:null,mark:null,markMinusPrice:null,projectionCurves:['gas_forward'],...changes});
const scheduledSummary=changes=>{const h=scheduledHeld(changes);return positionSummary(h,positionRows([h])[0],formats);};

test('a scheduled swap\'s title and facts say its quantity, fixed price and mark are by period, each in its unit',()=>{
 const view=scheduledSummary();
 assert.equal(view.subtitle,'Jan 2021 · Pay fixed · By period MMBtu (IT)');
 assert.deepEqual(view.facts,[{label:'Fixed price',text:'By period',unit:'USD/MMBtu (IT)'},{label:'Mark',text:'By period',unit:null},{label:'Quantity',text:'By period',unit:'MMBtu (IT)'},{label:'Settlement',text:'Projection',unit:null}]);
 assert.equal(view.markMinusPrice,null);
 assert.equal(scheduledSummary({fixedPrice:'3'}).facts[0].text,'3','a price every fixed period shares is stated');
});

test('a swap valued by its settlement lifecycle says it has no curve mark, and its settlement state',()=>{
 const h={...held['TR-000001'],settlement:'Active',mark:null,markMinusPrice:null};
 const facts=positionSummary(h,positionRows([h])[0],formats).facts;
 assert.deepEqual(facts.find(f=>f.label==='Mark'),{label:'Mark',text:'Settlement lifecycle',unit:null});
 assert.deepEqual(facts.at(-1),{label:'Settlement',text:'Active',unit:null});
});

test('a position that projects from several curves draws none of them, and Open curve says there is no one curve to open',()=>{
 const h=scheduledHeld({marketKey:JSON.stringify(['gas_forward','power_forward']),marketLabel:'Gas forward / Power forward',projectionCurves:['gas_forward','power_forward']});
 const row=positionRows([h])[0];
 assert.deepEqual(curveView('Curve shape unavailable · this position projects from several curves',h,row,formats),{state:'unavailable',reason:'Curve shape unavailable · this position projects from several curves'});
 assert.equal(curveNote(true,true),'This position projects from several curves, so there is no one curve to open.');
 assert.equal(curveNote(false,true),'This position projects from several curves, so there is no one curve to open.');
 assert.equal(curveNote(true,false),null);
});

test('a PPA the close cannot value states its booked facts and none of its valuation measures',()=>{
 const base={...fixture.holdings[0],instrumentType:'ppa',contractIdentityKind:'ppa_instrument',direction:'multi_side',quantity:'80',unit:'MW',fixedPrice:null,mark:null,
  markMinusPrice:null,measure:'ppa_value_excluding_overdue'};
 const valued={...base,presentValue:'14420',completePv:'15000',overdueKnown:'1200',overdueEstimated:'0',unavailableReason:null};
 const unvalued={...base,presentValue:null,completePv:null,overdueKnown:null,overdueEstimated:null,unavailableReason:'other_definition_versions'};
 const labels=h=>positionSummary(h,positionRows([h])[0],formats).facts.map(f=>f.label);
 assert.ok(['Complete PV','Overdue known','Overdue estimated'].every(l=>labels(valued).includes(l)));
 assert.ok(labels(unvalued).includes('Quantity')&&!['Complete PV','Overdue known','Overdue estimated'].some(l=>labels(unvalued).includes(l)));
});

test('why a position cannot be amended here: a PPA in PPA bookings; another as its note says once its revision is read',()=>{
  assert.equal(amendRefusal('ppa',true,{allowed:true,reason:null}),'PPA amendments and terminations are made in PPA bookings.');
  assert.equal(amendRefusal('future',true,{allowed:false,reason:'Read-only trade revision'}),'This position cannot be amended: Read-only trade revision');
  assert.equal(amendRefusal('financial_swap',false,undefined),'This account cannot amend trades in this market context.');
  assert.equal(amendRefusal('future',true,{allowed:true,reason:null}),null);
  // Before its revision is read nothing is known against it: the form's own read decides.
  assert.equal(amendRefusal('future',true,undefined),null);
});

test('a physical crude position labels its value as contractual PV and offers no invented cash spread',()=>{
 const h={...fixture.holdings[0],instrumentType:'physical_crude',contractIdentityKind:'crude_instrument',direction:'deliver',quantity:'1000',measure:'crude_contractual_present_value',presentValue:'9900',mark:'80',fixedPrice:'70',markMinusPrice:null};
 const shown=positionSummary(h,positionRows([h])[0],formats);
 assert.equal(shown.value.label,'Contractual PV');assert.equal(shown.markMinusPrice,null);assert.match(shown.subtitle,/Deliver/);
});
