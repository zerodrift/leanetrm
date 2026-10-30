import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {marketBoard,renderBoard,shapeReads,commodityIcon} from '../dist/market-board.js';
import {curvePoints} from '../dist/desk-model.js';
import {navigationGroups} from '../dist/market-navigation.js';
import {setCurveNames} from '../dist/market-view.js';

const fixture=JSON.parse(readFileSync(new URL('./fixtures/desk.json',import.meta.url)));
const curves=[
 {code:'gas_forward',name:'Gas forward',marketType:'forwards',category:{code:'test_gas',name:'Synthetic gas'},kind:'weighted_blend'},
 {code:'gas_futures',name:'Gas futures',marketType:'futures',category:{code:'test_gas',name:'Synthetic gas'},kind:'market_settlement'},
 {code:'power_forward',name:'Power forward',marketType:'forwards',category:{code:'test_power',name:'Synthetic power'},kind:'basis_spread'},
 {code:'usd_rates',name:'USD rates',marketType:'rates',category:null,kind:'interest_rate'},
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

test('a card states the backend\'s availability in the words the Blotter uses for it',()=>{
 const none={...priced,points:[],contractCount:0,front:null,availability:'no_remaining_contracts'},rows=[{market:'Gas forward',deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01'}];
 const unpriced={...priced,front:null,availability:'no_priced_contract'};
 for(const shape of [none,unpriced])
  assert.equal(card(marketBoard(navigation,all,new Map([['gas_forward',shape]])),'gas_forward').figures.reason.toLowerCase(),curvePoints(shape,rows[0]).reason.replace('Curve shape unavailable · ',''));
});
test('a curve with no remaining contracts says so on its card, with no price, count or line',()=>{
 const none={...priced,points:[],contractCount:0,front:null,availability:'no_remaining_contracts'};
 assert.deepEqual(card(marketBoard(navigation,all,new Map([['gas_forward',none]])),'gas_forward').figures,
  {state:'unavailable',unit:'USD/MMBtu (IT)',count:null,reason:'No contracts at this close'});
});

test('a curve the close prices no contract of says so, with the count the backend states',()=>{
 const unpriced={...priced,points:priced.points.map(p=>({...p,price:null,unavailableReason:'missing_observation'})),front:null,availability:'no_priced_contract'};
 assert.deepEqual(card(marketBoard(navigation,all,new Map([['gas_forward',unpriced]])),'gas_forward').figures,
  {state:'unavailable',unit:'USD/MMBtu (IT)',count:'4 contracts',reason:'No prices at this close'});
});

test('a curve the close cannot read, or a read that failed, says why on its card',()=>{
 const shapes=new Map([['gas_forward',{...fixture.shapes[1],curve:'gas_forward'}],['power_forward','Curve shape unavailable · the curve read failed']]);
 const board=marketBoard(navigation,all,shapes);
 assert.deepEqual(card(board,'gas_forward').figures,{state:'unavailable',unit:null,count:null,reason:"Curve shape unavailable · this market's curve is not in the definitions of this close"});
 assert.deepEqual(card(board,'power_forward').figures,{state:'failed',reason:'Curve shape unavailable · the curve read failed'},'a read that failed is not what the backend states of the curve');
 const inconsistent=card(marketBoard(navigation,all,new Map([['gas_forward',{...priced,front:null}]])),'gas_forward').figures;
 assert.deepEqual(inconsistent,{state:'unavailable',unit:null,count:null,reason:'Curve shape unavailable · the curve read is inconsistent'});
});

test('a curve not read yet waits for its read, and a rates curve, which has no curve shape, says where its rates are',()=>{
 const board=marketBoard(navigation,all,new Map());
 assert.deepEqual(card(board,'gas_futures').figures,{state:'unread'});
 assert.deepEqual(card(board,'usd_rates').figures,{state:'rates'});
});

test('a curve whose read failed is read again by the next read; one the backend stated a shape for, even an unavailable one, is not',()=>{
 const shapes=new Map([['gas_forward','Curve shape unavailable · the curve read failed'],['gas_futures',{...fixture.shapes[1],curve:'gas_futures'}],['power_forward',priced]]);
 assert.deepEqual(shapeReads(marketBoard(navigation,all,shapes)),[['gas_forward']]);
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

// A minimal DOM for the board's cards: elements with a class, text, dataset, attributes, style properties and children.
class Text{constructor(text){this.textContent=text;}}
class Element{
 constructor(tag){this.tag=tag;this.className='';this.dataset={};this.children=[];this.attributes={};this.style={setProperty:(k,v)=>{this.style[k]=v;}};this.classList={add:c=>{this.className+=' '+c;}};this._text='';}
 get textContent(){return this._text+this.children.map(c=>c.textContent).join('');}
 set textContent(value){this._text=value;this.children=[];}
 setAttribute(k,v){this.attributes[k]=v;}
 getAttribute(k){return this.attributes[k]??null;}
 append(...items){this.children.push(...items);}
 replaceChildren(...items){this.children=items;}
}
const dom={createElement:tag=>new Element(tag),createElementNS:(_,tag)=>new Element(tag),createTextNode:text=>new Text(text)};
const walk=(e,found=[])=>{found.push(e);for(const c of e.children??[])if(c instanceof Element)walk(c,found);return found;};
test('a card\'s button is disabled while the page is busy, as the library\'s rows are',()=>{
 globalThis.document=dom;
 try{
  const board=marketBoard(navigation,all,new Map([['gas_forward','Curve shape unavailable · the curve read failed']]));
  for(const busy of [true,false]){
   const target=new Element('div');renderBoard(target,board,()=>{},busy);
   const buttons=walk(target).filter(e=>e.className==='market-card-open');
   assert.equal(buttons.length,board.flatMap(g=>g.cards).length);
   assert.ok(buttons.every(b=>b.disabled===busy));
  }
 }finally{delete globalThis.document;}
});

test('each card shows the backend\'s curve kind as a badge named by its label beside the curve\'s name',()=>{
 globalThis.document=dom;
 try{
  const target=new Element('div');renderBoard(target,marketBoard(navigation,all,new Map()),()=>{},false);
  const kinds=walk(target).filter(e=>e.className==='market-card').map(item=>{const head=item.children[0],badge=walk(head).find(e=>e.className?.startsWith('curve-kind '));return [item.dataset.curve,badge?.getAttribute('aria-label')];});
  assert.deepEqual(kinds.sort(),[['gas_forward','Weighted blend'],['gas_futures','Market settlement'],['power_forward','Basis / spread'],['usd_rates','Interest rate']]);
 }finally{delete globalThis.document;}
});


test('commodity icons use distinct gas and power shapes with names, no hollow marker for rates',()=>{
 globalThis.document=dom;
 try{
  const board=marketBoard(navigation,all,new Map()),gas=commodityIcon(card(board,'gas_futures')),power=commodityIcon(card(board,'power_forward'));
  assert.equal(gas.getAttribute('aria-label'),'Synthetic gas');assert.equal(power.getAttribute('aria-label'),'Synthetic power');
  assert.notEqual(JSON.stringify(gas.children),JSON.stringify(power.children));
  assert.equal(gas.dataset.commodityIcon,'gas');assert.equal(power.dataset.commodityIcon,'power');
  assert.equal(commodityIcon(card(board,'usd_rates')),null);
  const other=commodityIcon({...card(board,'gas_futures'),commodity:{code:'synthetic_other',name:'Other commodity'}});
  assert.equal(other.dataset.commodityIcon,'other');assert.equal(other.getAttribute('aria-label'),'Other commodity');
 }finally{delete globalThis.document;}
});
