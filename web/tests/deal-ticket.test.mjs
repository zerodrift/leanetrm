import test from 'node:test';
import assert from 'node:assert/strict';

// A minimal DOM: the ticket view builds its elements through document.createElement and the properties below only.
class Element{
 constructor(tag){this.tagName=tag.toUpperCase();this.children=[];this.dataset={};this.attributes={};this.className='';this.text='';this.value='';this.disabled=false;this.title='';
  this.style={setProperty:(name,value)=>{this.style[name]=value;}};}
 get classList(){const names=()=>this.className.split(' ').filter(Boolean),set=list=>{this.className=list.join(' ');};
  return {add:(...n)=>set([...new Set([...names(),...n])]),remove:(...n)=>set(names().filter(x=>!n.includes(x))),contains:n=>names().includes(n),
   toggle:(n,on=!names().includes(n))=>{set(on?[...new Set([...names(),n])]:names().filter(x=>x!==n));return on;}};}
 append(...children){for(const child of children){const node=typeof child==='string'?new Text(child):child;node.parentElement=this;this.children.push(node);}}
 replaceChildren(...children){this.children=[];this.append(...children);}
 set textContent(value){this.children=[];this.text=String(value);}
 get textContent(){return this.text+this.children.map(c=>c.textContent).join('');}
 setAttribute(name,value){this.attributes[name]=String(value);}
 getAttribute(name){return this.attributes[name]??null;}
 closest(selector){const tags=selector.split(',').map(t=>t.toUpperCase());for(let n=this;n;n=n.parentElement)if(tags.includes(n.tagName))return n;return null;}
}
class Text{constructor(value){this.textContent=value;this.children=[];this.className='';this.dataset={};this.attributes={};}getAttribute(){return null;}}
const oldDocument=globalThis.document;
globalThis.document={createElement:tag=>new Element(tag),createTextNode:value=>new Text(value)};
const {bandRows,renderTicket}=await import('../dist/deal-ticket.js');
const {applyLookup,newTicket,setValue}=await import('../dist/ticket-model.js');
test.after(()=>{globalThis.document=oldDocument;});

const all=root=>[root,...root.children.flatMap(all)];
const byClass=(root,name)=>all(root).filter(n=>n.className.split(' ').includes(name));
const one=(root,name)=>{const found=byClass(root,name);assert.equal(found.length,1,`one .${name}`);return found[0];};
const controls=root=>all(root).filter(n=>['INPUT','SELECT','TEXTAREA'].includes(n.tagName));

const f=(id,label,over={})=>({id,label,meaning:`${label} meaning`,type:'text',unit:null,state:'editable',required:false,band:null,choices:null,dependsOn:[],lookup:null,soon:null,rule:null,...over});
const description={product:'synthetic_flow',version:2,rowShape:'flow',levels:['month','day'],modes:['new','booked'],tradeColumns:[],tabs:[{id:'notes',label:'Notes'}],
 tradeFields:[f('book','Book',{type:'choice',choices:'books'}),f('agreement','Agreement',{state:'soon',soon:'Will name the master agreement.'})],
 sides:[{kind:'leg',pill:'Physical',colour:'gas',min:1,initial:1,max:2,columns:[],fields:[f('direction','Direction',{band:'direction',type:'choice',choices:'directions'}),
   f('start','From',{band:'start',type:'date'}),f('quantity','Quantity',{band:'quantity',type:'decimal',unit:'quantity'}),
   f('price','Price',{band:'price',state:'info',lookup:'market_price'}),f('point','Point',{lookup:'point'}),f('meter','Meter',{state:'soon',soon:'Will name the meter.'})]},
  {kind:'cash',pill:'Payment',colour:'cash',min:1,initial:1,max:2,columns:[],fields:[f('pay_direction','Direction',{band:'direction',type:'choice',choices:'pay'}),
   f('pay_start','From',{band:'start',state:'derived',rule:'Follows the source side.'}),f('pay_price','Price',{band:'price',type:'decimal'}),f('pay_at','Payment at',{band:'payment'})]}],
 sideLimits:[]};
const status={kind:'current',text:'Up to date'};
const noEvents=()=>new Proxy({},{get:(_,name)=>()=>assert.fail(`unexpected ${String(name)} event`)});
const draw=(over={})=>{
 const host=new Element('section');
 renderTicket(host,{state:newTicket(description),mode:'new',actions:null,schedule:null,quantityUnit:null,instant:at=>at,previous:null,summaries:[],links:[],entry:null,status,fieldErrors:{},choices:{},selectedSide:null,tab:'instrument',level:'month',closed:new Set(),on:noEvents(),...over});
 return host;
};

test('a quantity field shows the unit the preview resolved, and none before',()=>{
 const unit=host=>byClass(all(host).find(n=>n.dataset.field==='quantity'&&n.dataset.band),'ticket-unit').map(n=>n.textContent);
 assert.deepEqual(unit(draw()),[]);
 assert.deepEqual(unit(draw({quantityUnit:'GJ'})),['GJ']);
});

test('band rows are the rows any shown kind uses, in the common band order',()=>{
 assert.deepEqual(bandRows(description,['leg','cash']),['direction','start','quantity','price','payment']);
 assert.deepEqual(bandRows(description,['cash']),['direction','start','price','payment']);
 assert.deepEqual(bandRows(description,[]),[]);
});

test('every card has one row per band row, in order, and an unused slot is drawn as an empty grey slot',()=>{
 const host=draw();
 const cards=byClass(host,'ticket-card');
 assert.deepEqual(cards.map(c=>c.dataset.side),['leg-1','cash-1']);
 for(const card of cards)assert.deepEqual(all(card).filter(n=>n.dataset.band).map(n=>n.dataset.band),['direction','start','quantity','price','payment']);
 const [leg,cash]=cards;
 assert.ok(all(leg).find(n=>n.dataset.band==='payment').classList.contains('ticket-slot-empty'));
 assert.ok(all(cash).find(n=>n.dataset.band==='quantity').classList.contains('ticket-slot-empty'));
 assert.equal(all(leg).find(n=>n.dataset.band==='price').dataset.state,'info');
 assert.equal(byClass(leg,'ticket-band-title')[0].textContent,'Common to all sides');
 assert.deepEqual(all(byClass(leg,'ticket-own')[0]).filter(n=>n.dataset.field).map(n=>n.dataset.field),['point','meter'],'the side\'s own fields follow the band');
});

test('a coming-soon field is a disabled control with a Coming soon tag, its hover text and no value',()=>{
 let state=newTicket(description);state={...state,sides:state.sides.map(s=>s.kind==='leg'?{...s,values:{meter:{value:'M-1'}}}:s)};
 const host=draw({state});
 const meter=all(host).find(n=>n.dataset.field==='meter');
 const [control]=controls(meter);
 assert.equal(control.disabled,true);assert.equal(control.getAttribute('aria-disabled'),'true');assert.equal(control.value,'');
 assert.equal(control.title,'Will name the meter.');
 assert.equal(one(meter,'ticket-soon-tag').textContent,'Coming soon');
 const agreement=all(one(host,'ticket-strip')).find(n=>n.dataset.field==='agreement');
 assert.equal(controls(agreement)[0].title,'Will name the master agreement.');
});

test('a looked-up value shows its chip, and an offered value is accepted only by its button',()=>{
 let state=applyLookup(newTicket(description),'leg-1','point',{value:'P-7',source:{kind:'reference',label:'Hub list'}});
 state=applyLookup(state,'leg-1','price',{value:'3.25',source:{kind:'market',label:'Hub A · close 2041-01-02'}});
 let accepted=[];
 let host=draw({state,on:{...noEvents(),accept:(side,field)=>accepted.push([side,field])}});
 const point=all(host).find(n=>n.dataset.field==='point');
 assert.equal(one(point,'ticket-chip').textContent,'Hub list');assert.equal(controls(point)[0].value,'P-7');
 assert.equal(one(all(host).find(n=>n.dataset.field==='price'),'ticket-chip').textContent,'Hub A · close 2041-01-02');
 state=applyLookup(setValue(state,'leg-1','point','P-1'),'leg-1','point',{value:'P-9',source:{kind:'reference',label:'Hub list'}});
 host=draw({state,on:{...noEvents(),accept:(side,field)=>accepted.push([side,field])}});
 const offer=one(all(host).find(n=>n.dataset.field==='point'),'ticket-offer');
 assert.equal(offer.textContent,'Use P-9 (Hub list)');
 assert.deepEqual(accepted,[]);offer.onclick();
 assert.deepEqual(accepted,[['leg-1','point']]);
});

test('editing a control sends the side, field and the control\'s value; a field error shows under it',()=>{
 const edits=[];
 const host=draw({fieldErrors:{'leg-1.quantity':'Quantity must be positive.',book:'Choose a book.'},choices:{book:[{value:'b-1',label:'East book'}]},
  on:{...noEvents(),edit:(...a)=>edits.push(a)}});
 const quantity=all(host).find(n=>n.dataset.field==='quantity'&&n.dataset.band);
 const [input]=controls(quantity);input.value='10000';input.onchange();
 assert.equal(one(quantity,'ticket-error').textContent,'Quantity must be positive.');
 const book=all(host).find(n=>n.dataset.field==='book');
 assert.deepEqual(controls(book)[0].children.map(o=>[o.value,o.textContent]),[['','Choose…'],['b-1','East book']]);
 assert.equal(one(book,'ticket-error').textContent,'Choose a book.');
 assert.deepEqual(edits,[['leg-1','quantity','10000']]);
});

test('clicking a card selects its side, and the selected side\'s tab is marked selected',()=>{
 const selected=[];
 let host=draw({on:{...noEvents(),selectSide:code=>selected.push(code)}});
 const cash=byClass(host,'ticket-card')[1];
 cash.onclick({target:controls(cash)[0]});assert.deepEqual(selected,[],'a click on a control is the control\'s');
 cash.onclick({target:byClass(cash,'ticket-band-title')[0]});
 assert.deepEqual(selected,['cash-1']);
 host=draw({selectedSide:'cash-1',tab:'cash-1'});
 const tabs=all(host).filter(n=>n.getAttribute('role')==='tab');
 assert.deepEqual(tabs.map(t=>[t.dataset.tab,t.getAttribute('aria-selected')]),[['instrument','false'],['leg-1','false'],['cash-1','true'],['notes','false']]);
 assert.ok(byClass(host,'ticket-card')[1].classList.contains('selected'));
 assert.deepEqual(byClass(one(host,'ticket-chips'),'ticket-side-chip').map(c=>c.textContent),['Physical · leg-1','Payment · cash-1']);
});

test('in booked mode nothing is editable, no chip is drawn and Amend and Close out are disabled with their reasons',()=>{
 let state=applyLookup(setValue(newTicket(description),'leg-1','quantity','5000'),'leg-1','point',{value:'P-7',source:{kind:'reference',label:'Hub list'}});
 state=applyLookup(state,'leg-1','price',{value:'3.25',source:{kind:'market',label:'Hub A'}});
 const actions={amend:{allowed:false,reason:'Physical gas has no amendment yet.'},closeOut:{allowed:false,reason:'Close-out is not offered for this trade.'}};
 const host=draw({state,mode:'booked',actions,quantityUnit:'MMBtu'});
 for(const control of controls(host).filter(c=>c.dataset.role!=='level'))assert.equal(control.disabled,true,`${control.tagName} is disabled`);
 assert.equal(byClass(host,'ticket-chip').length,0);assert.equal(byClass(host,'ticket-add').length,0);
 assert.equal(one(all(host).find(n=>n.dataset.field==='quantity'&&n.dataset.band),'ticket-value').textContent,'5000 MMBtu');
 const buttons=all(one(host,'ticket-foot')).filter(n=>n.tagName==='BUTTON');
 assert.deepEqual(buttons.map(b=>[b.textContent,b.disabled,b.title]),[['Amend',true,actions.amend.reason],['Close out',true,actions.closeOut.reason]]);
 assert.equal(one(host,'ticket-state').textContent,'Booked');
});

test('the strip shows the status and the model\'s notices, and the ghost card offers the side kinds the model admits',()=>{
 const added=[];
 const state={...newTicket(description),notices:['Point was cleared because Zone changed.']};
 const host=draw({state,status:{kind:'refused',text:'Preview refused: no point.'},on:{...noEvents(),addSide:kind=>added.push(kind)}});
 const line=one(host,'ticket-status');assert.equal(line.dataset.kind,'refused');assert.equal(line.textContent,'Preview refused: no point.');
 assert.deepEqual(byClass(host,'ticket-notice').map(n=>n.textContent),['Point was cleared because Zone changed.']);
 const offers=all(one(host,'ticket-add')).filter(n=>n.tagName==='BUTTON');
 assert.deepEqual(offers.map(b=>[b.textContent,b.disabled]),[['Physical',false],['Payment',false]]);
 offers[1].onclick();assert.deepEqual(added,['cash']);
 const full=draw({state:{...state,sides:[...state.sides,{kind:'cash',code:'cash-2',values:{}}]}});
 const payment=all(one(full,'ticket-add')).filter(n=>n.tagName==='BUTTON')[1];
 assert.equal(payment.disabled,true);assert.match(payment.title,/at most 2 cash sides/);
});

test('a side summary from the preview shows its reported figures, and a missing figure is not drawn',()=>{
 const host=draw({summaries:[{side:'cash-1',billedQuantity:'310000',amount:'-1057720.00',againstMarket:null}]});
 const summary=one(byClass(host,'ticket-card')[1],'ticket-summary');
 assert.deepEqual(byClass(summary,'ticket-summary-line').map(n=>n.textContent),['Billed quantity310,000','Amount-1,057,720.00']);
 assert.equal(byClass(byClass(host,'ticket-card')[0],'ticket-summary').length,0);
});
