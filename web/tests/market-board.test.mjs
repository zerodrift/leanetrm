import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {marketBoard,shapeReads} from '../dist/market-board.js';
import {navigationGroups} from '../dist/market-navigation.js';
import {setCurveNames} from '../dist/market-view.js';

const fixture=JSON.parse(readFileSync(new URL('./fixtures/desk.json',import.meta.url)));
const curves=[
 {code:'gas_forward',name:'Gas forward',marketType:'forwards',category:{code:'test_gas',name:'Synthetic gas'}},
 {code:'gas_futures',name:'Gas futures',marketType:'futures',category:{code:'test_gas',name:'Synthetic gas'}},
 {code:'power_forward',name:'Power forward',marketType:'forwards',category:{code:'test_power',name:'Synthetic power'}},
 {code:'usd_rates',name:'USD rates',marketType:'rates',category:null},
];
const navigation={kind:'market_navigation',profile:'viewer',context:null,curves,curveNames:curves.map(({code,name})=>({code,name}))};
setCurveNames(navigation.curveNames);
const all={by:'commodity',commodity:null,type:'all'};
const [priced]=fixture.shapes;
const card=(board,code)=>board.flatMap(g=>g.cards).find(c=>c.curve===code);

test('the board groups and orders its cards exactly as the library groups its curves, under every filter',()=>{
 for(const filters of [all,{by:'marketType',commodity:null,type:'all'},{by:'commodity',commodity:'test_gas',type:'all'},{by:'marketType',commodity:null,type:'forwards'}]){
  const board=marketBoard(navigation,filters,new Map());
  assert.deepEqual(board.map(g=>[g.key,g.name,g.cards.map(c=>c.curve)]),
   navigationGroups(navigation,filters.by,filters.commodity,filters.type).map(g=>[g.key,g.name,g.curves.map(c=>c.code)]));
 }
});

test('a card names its curve, its commodity and market type, and takes its commodity identity color',()=>{
 const board=marketBoard(navigation,all,new Map());
 assert.deepEqual([card(board,'gas_futures').name,card(board,'gas_futures').kind],['Gas futures','Gas · Futures']);
 assert.equal(card(board,'gas_futures').color,'#eb6834');assert.equal(card(board,'power_forward').color,'#2a78d6');
 assert.equal(card(board,'usd_rates').color,null,'a curve without a commodity has no commodity color');
});

test('a priced card shows the backend\'s front contract, exact price, unit and contract count, and its curve with gaps kept',()=>{
 const shown=card(marketBoard(navigation,all,new Map([['gas_forward',priced]])),'gas_forward').figures;
 assert.equal(shown.state,'priced');
 assert.deepEqual([shown.unit,shown.front.delivery,shown.front.price,shown.count],['USD/MMBtu (IT)','Jan 2021','3.49','4 contracts']);
 assert.deepEqual(shown.line.points.map(p=>p.y),['3.49','3.41',null,'3.02'],'a contract without a price stays a gap');
 assert.deepEqual(shown.line.marks,[0],'the front contract is marked');
 assert.equal(shown.line.title,'Gas forward · forward curve, Jan 2021 to Apr 2021\nFront: Jan 2021 · 3.49 USD/MMBtu (IT)');
 // The backend's front is shown whatever the points hold: the browser never picks it.
 const stated={...priced,front:{...priced.front,contractId:priced.points[1].contractId,deliveryFrom:'2021-02-01',deliveryTo:'2021-03-01',price:'3.41'},contractCount:9};
 const restated=card(marketBoard(navigation,all,new Map([['gas_forward',stated]])),'gas_forward').figures;
 assert.deepEqual([restated.front.delivery,restated.front.price,restated.count,restated.line.marks],['Feb 2021','3.41','9 contracts',[1]]);
});

test('a curve with no remaining contracts says so on its card, with no price, count or line',()=>{
 const none={...priced,points:[],contractCount:0,front:null,availability:'no_remaining_contracts'};
 assert.deepEqual(card(marketBoard(navigation,all,new Map([['gas_forward',none]])),'gas_forward').figures,
  {state:'unavailable',unit:'USD/MMBtu (IT)',count:null,reason:'No remaining contracts'});
});

test('a curve the close prices no contract of says so, with the count the backend states',()=>{
 const unpriced={...priced,points:priced.points.map(p=>({...p,price:null,unavailableReason:'missing_observation'})),front:null,availability:'no_priced_contract'};
 assert.deepEqual(card(marketBoard(navigation,all,new Map([['gas_forward',unpriced]])),'gas_forward').figures,
  {state:'unavailable',unit:'USD/MMBtu (IT)',count:'4 contracts',reason:'No priced contract at this close'});
});

test('a curve the close cannot read, or a read that failed, says why on its card',()=>{
 const shapes=new Map([['gas_forward',{...fixture.shapes[1],curve:'gas_forward'}],['power_forward','Curve shape unavailable · the curve read failed']]);
 const board=marketBoard(navigation,all,shapes);
 assert.deepEqual(card(board,'gas_forward').figures,{state:'unavailable',unit:null,count:null,reason:"Curve shape unavailable · this market's curve is not in the definitions of this close"});
 assert.deepEqual(card(board,'power_forward').figures,{state:'unavailable',unit:null,count:null,reason:'Curve shape unavailable · the curve read failed'});
 const inconsistent=card(marketBoard(navigation,all,new Map([['gas_forward',{...priced,front:null}]])),'gas_forward').figures;
 assert.deepEqual(inconsistent,{state:'unavailable',unit:null,count:null,reason:'Curve shape unavailable · the curve read is inconsistent'});
});

test('a curve not read yet waits for its read, and a rates curve, which has no curve shape, says where its rates are',()=>{
 const board=marketBoard(navigation,all,new Map());
 assert.deepEqual(card(board,'gas_futures').figures,{state:'unread'});
 assert.deepEqual(card(board,'usd_rates').figures,{state:'rates'});
});

test('the board reads every listed curve it has not read, but no rates curve, in reads of at most 64 curves',()=>{
 const many=Array.from({length:130},(_,i)=>({code:`curve_${String(i).padStart(3,'0')}`,name:`Curve ${String(i).padStart(3,'0')}`,marketType:'forwards',category:{code:'test_gas',name:'Synthetic gas'}}));
 const wide={...navigation,curves:[...many,curves[3]],curveNames:[...many,curves[3]].map(({code,name})=>({code,name}))};
 setCurveNames(wide.curveNames);
 try{
  const reads=shapeReads(marketBoard(wide,all,new Map([['curve_000',priced]])));
  assert.deepEqual(reads.map(r=>r.length),[64,64,1]);
  assert.deepEqual(reads.flat(),many.slice(1).map(c=>c.code),'each curve once, in the library\'s order, without the one already read or the rates curve');
  assert.deepEqual(shapeReads(marketBoard({...wide,curves:[curves[3]]},all,new Map())),[],'a board of rates only makes no read');
 }finally{setCurveNames(navigation.curveNames);}
});
