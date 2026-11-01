import test from 'node:test';
import assert from 'node:assert/strict';
import {acceptOffer,acceptPreview,addSide,applyLookup,draftOf,dropLookup,newTicket,nextPreview,removeSide,setValue,ticketOf} from '../dist/ticket-model.js';

const f=(id,label,over={})=>({id,label,meaning:'',type:'choice',unit:null,state:'editable',required:false,band:null,choices:null,dependsOn:[],lookup:null,soon:null,rule:null,...over});
const description={product:'synthetic',version:3,rowShape:'flow',levels:['month'],tabs:[],modes:['new'],tradeColumns:[],
 tradeFields:[f('book','Book'),f('trade_date','Trade date',{state:'info',lookup:'trade_date'}),f('later','Later',{state:'soon',soon:'Not yet'})],
 sides:[{kind:'leg',pill:'Leg',colour:'blue',min:1,initial:1,max:3,columns:[],fields:[f('region','Region',{lookup:'region'}),f('zone','Zone',{lookup:'zone',dependsOn:['region']}),f('point','Point',{dependsOn:['zone']}),
   f('price','Price',{state:'info',lookup:'market_price'}),f('note','Note',{state:'soon',soon:'Not yet'}),f('total','Total',{state:'derived',rule:'sum'})]},
  {kind:'cash',pill:'Cash',colour:'green',min:0,initial:0,max:2,columns:[],fields:[f('amount','Amount')]},
  {kind:'fee',pill:'Fee',colour:'red',min:0,initial:0,max:2,columns:[],fields:[f('rate','Rate')]}],
 sideLimits:[{kinds:['cash','fee'],max:3}]};
const leg='leg-1';
const looked={value:'4.5',source:{kind:'market',label:'Market close'}};

test('a new ticket opens with each kind’s initial sides, coded by kind',()=>{
 const s=newTicket(description);
 assert.deepEqual(s.sides.map(x=>x.code),['leg-1']);
 assert.equal(s.previewSerial,0);assert.deepEqual(s.notices,[]);
 const paid={...description,sides:description.sides.map(k=>k.kind==='cash'?{...k,initial:1}:k)};
 assert.deepEqual(newTicket(paid).sides.map(x=>x.code),['leg-1','cash-1']);
});

test('editing a looked-up field removes its chip, and a later lookup offers instead of replacing it',()=>{
 let s=applyLookup(newTicket(description),leg,'region',{value:'R1',source:looked.source});
 assert.deepEqual(s.sides[0].values.region,{value:'R1',looked:looked.source});
 s=setValue(s,leg,'region','R2');
 assert.deepEqual(s.sides[0].values.region,{value:'R2'});
 s=applyLookup(s,leg,'region',{value:'R9',source:looked.source});
 assert.deepEqual(s.sides[0].values.region,{value:'R2',offered:{value:'R9',...looked.source}});
 s=applyLookup(s,leg,'price',looked);
 assert.deepEqual(s.sides[0].values.price,{value:'4.5',looked:looked.source});
});

test('a still-looked value is replaced by a newer lookup, and an offer is accepted explicitly',()=>{
 let s=applyLookup(newTicket(description),null,'trade_date',{value:'2021-01-01',source:looked.source});
 s=applyLookup(s,null,'trade_date',{value:'2021-01-02',source:looked.source});
 assert.equal(s.trade.trade_date.value,'2021-01-02');
 s=setValue(s,leg,'region','R1');s=applyLookup(s,leg,'region',{value:'R2',source:looked.source});
 s=acceptOffer(s,leg,'region');
 assert.deepEqual(s.sides[0].values.region,{value:'R2',looked:looked.source});
 assert.throws(()=>acceptOffer(s,leg,'region'),Error);
});

test('changing a parent clears its set dependents transitively with a notice each and picks nothing',()=>{
 let s=newTicket(description);
 for(const [k,v] of [['region','R1'],['zone','Z1'],['point','P1']])s=setValue(s,leg,k,v);
 s=setValue(s,leg,'region','R2');
 assert.equal(s.sides[0].values.zone.value,'');assert.equal(s.sides[0].values.point.value,'');
 assert.deepEqual(s.notices,['Zone was cleared because Region changed.','Point was cleared because Zone changed.']);
 const before=s.notices.length;
 s=setValue(s,leg,'region','R3');
 assert.equal(s.notices.length,before);
});

test('unknown sides and fields, and values for fields the user cannot edit, are refused',()=>{
 const s=newTicket(description);
 assert.throws(()=>setValue(s,'nope','region','x'),Error);
 assert.throws(()=>setValue(s,leg,'missing','x'),Error);
 assert.throws(()=>setValue(s,leg,'note','x'),/not editable/);
 assert.throws(()=>setValue(s,null,'region','x'),Error);
});

test('sides are added and removed within each kind\'s limits and the shared limit',()=>{
 let s=newTicket(description);
 s=addSide(s,'cash');s=addSide(s,'cash');
 assert.throws(()=>addSide(s,'cash'),Error);
 s=addSide(s,'fee');
 assert.throws(()=>addSide(s,'fee'),/together/);
 assert.deepEqual(s.sides.map(x=>x.code),['leg-1','cash-1','cash-2','fee-1']);
 s=removeSide(s,'cash-1');s=addSide(s,'cash');
 assert.deepEqual(s.sides.map(x=>x.code),['leg-1','cash-2','fee-1','cash-1']);
 assert.throws(()=>removeSide(s,'leg-1'),Error);
 assert.throws(()=>removeSide(s,'nope'),Error);
 assert.throws(()=>addSide(s,'other'),Error);
});

test('the draft carries editable, non-empty values only, whatever the state object holds',()=>{
 let s=newTicket(description);
 s=setValue(s,null,'book','B1');s=setValue(s,leg,'region','R1');s=setValue(s,leg,'zone','Z1');s=setValue(s,leg,'region','R2');
 s=applyLookup(s,leg,'price',looked);
 s.trade.later={value:'forced'};s.sides[0].values.note={value:'forced'};s.sides[0].values.total={value:'9'};
 assert.deepEqual(draftOf(s),{product:'synthetic',version:3,trade:{book:'B1'},sides:[{kind:'leg',code:'leg-1',values:{region:'R2'}}]});
});

test('only the latest preview request may be shown',()=>{
 const first=nextPreview(newTicket(description));
 const second=nextPreview(first.state);
 assert.equal(acceptPreview(second.state,first.serial),false);
 assert.equal(acceptPreview(second.state,second.serial),true);
 assert.equal(acceptPreview(setValue(second.state,null,'book','B'),second.serial),true);
});

test('a lookup that proposes the person\'s own value offers nothing',()=>{
 const s=applyLookup(setValue(newTicket(description),leg,'region','R1'),leg,'region',{value:'R1',source:looked.source});
 assert.deepEqual(s.sides[0].values.region,{value:'R1'});
});

test('a lookup that no longer resolves empties the value it filled and withdraws its offer, keeping the person\'s value',()=>{
 let s=applyLookup(newTicket(description),leg,'price',looked);
 s=dropLookup(s,leg,'price');
 assert.deepEqual(s.sides[0].values.price,{value:''});
 s=applyLookup(setValue(s,leg,'region','R1'),leg,'region',{value:'R2',source:looked.source});
 s=dropLookup(s,leg,'region');
 assert.deepEqual(s.sides[0].values.region,{value:'R1'});
 assert.equal(dropLookup(s,leg,'point'),s);
});

test('a draft opens as a ticket holding its sides and values, each the person\'s',()=>{
 const draft={product:'synthetic',version:3,trade:{book:'B1'},sides:[{kind:'leg',code:'L',values:{region:'R1',zone:'Z1'}},{kind:'fee',code:'F',values:{rate:'0.1'}}]};
 const s=ticketOf(description,draft);
 assert.deepEqual(s.trade,{book:{value:'B1'}});
 assert.deepEqual(s.sides,[{kind:'leg',code:'L',values:{region:{value:'R1'},zone:{value:'Z1'}}},{kind:'fee',code:'F',values:{rate:{value:'0.1'}}}]);
 assert.deepEqual(draftOf(s),draft);
 assert.throws(()=>ticketOf(description,{...draft,version:2}),/version/);
 assert.throws(()=>ticketOf(description,{...draft,sides:[{kind:'swap',code:'S',values:{}}]}),/swap/);
});
