import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const browser={skip:!browserAvailable,timeout:browserTestTimeout};
const fixture=`(async()=>{
 window.groups=[{instrumentId:'synthetic-rights',bindingId:'synthetic-rights-binding',label:'Retained service rights',sides:[{slot:0,sideId:'shared-side',label:'Remaining rights',roleLabel:'Commercial entitlement',directionLabel:'Signed whole-package value',overviewSections:[{label:'Rights terms',values:[{label:'Whole packages',value:'1'},{label:'End',value:'2032-03-14T08:00:00.123456Z',kind:'instant'},{label:'Evidence',value:'2032-03-14T08:00:00Z'}]}]}]},
 {instrumentId:'synthetic-cash',bindingId:'synthetic-cash-binding',label:'Independent fee cash',sides:[{slot:0,sideId:'shared-side',label:'Recorded fee',roleLabel:'Ordinary cash payment',directionLabel:'Company pays',overviewSections:[{label:'Cash terms',values:[{label:'Amount',value:'-12.50 USD'},{label:'Payment state',value:'Recorded separately'}]}]}]}];
 const module=await import('/instrument-sides.js');window.renderSides=module.renderInstrumentSides;window.requireSides=module.requireInstrumentSideGroups;
 window.draw=(zone='America/Chicago',retained=true)=>renderSides(document.querySelector('#host'),groups,{zone,retained});
})()`;

test('actual Side memberships remain visibly grouped under their independent bound instruments',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(fixture);await evaluate('draw()');
 const groups=await evaluate("[...document.querySelectorAll('[data-instrument-side-group]')].map(group=>({label:group.querySelector('h3').textContent,sides:[...group.querySelectorAll('[data-instrument-side-slot]')].map(side=>({slot:side.dataset.instrumentSideSlot,text:side.textContent}))}))");
 assert.equal(groups.length,2);assert.equal(groups[0].label,'Retained service rights');assert.equal(groups[1].label,'Independent fee cash');
 assert.equal(groups[0].sides[0].slot,'0');assert.equal(groups[1].sides[0].slot,'0','membership slots belong to their instrument group');
 assert.match(groups[0].sides[0].text,/Side · Remaining rights/);assert.match(groups[0].sides[0].text,/Commercial entitlement/);assert.match(groups[0].sides[0].text,/Signed whole-package value/);
 assert.doesNotMatch(groups[0].sides[0].text,/12\.50/);assert.match(groups[1].sides[0].text,/Company pays/);assert.match(groups[1].sides[0].text,/-12\.50 USD/);
 assert.equal(await evaluate("document.querySelectorAll('button,input,select').length"),0,'inspection does not infer or expose an action');
}));

test('independent Sides compare alongside each other in a wide container and stack without clipping on a phone-width pane',browser,()=>withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="host" style="width:900px"></div>',async evaluate=>{
 await evaluate(fixture);await evaluate("groups.splice(1);const returned=structuredClone(groups[0].sides[0]);returned.slot=1;returned.sideId='synthetic-return-side';returned.label='Return claim';groups[0].sides.push(returned);draw()");
 const boxes=()=>"[...document.querySelectorAll('[data-instrument-side-slot]')].map(side=>{const box=side.getBoundingClientRect();return{x:box.x,y:box.y,width:box.width};})";
 const wide=await evaluate(boxes());assert.equal(wide[0].y,wide[1].y);assert.ok(wide[1].x>wide[0].x);
 await evaluate("document.querySelector('#host').style.width='320px'");const phone=await evaluate(boxes());assert.ok(phone[1].y>phone[0].y);assert.equal(phone[0].x,phone[1].x);assert.ok(phone.every(box=>box.width<=320));
}));

test('Side terms format only backend-marked instants in the user zone and retain exact raw time titles',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(fixture);await evaluate('window.remove=draw()');
 const before=await evaluate("document.querySelector('#host').textContent");assert.match(before,/CDT|CST|America\/Chicago/);assert.match(before,/2032-03-14T08:00:00Z/,'ordinary evidence remains literal');
 assert.equal(await evaluate("document.querySelector('[title=\"2032-03-14T08:00:00.123456Z\"]')!==null"),true);
 await evaluate("remove();draw('Asia/Tokyo')");const after=await evaluate("document.querySelector('#host').textContent");assert.notEqual(before,after);assert.match(after,/JST|GMT\+9|Asia\/Tokyo/);
}));

test('unpriced draft Sides show their real structure without a fabricated binding or zero valuation',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(fixture);await evaluate("groups[0].bindingId=null;groups.splice(1);draw('UTC',false)");
 const text=await evaluate("document.querySelector('#host').textContent");assert.match(text,/Remaining rights/);assert.match(text,/Commercial entitlement/);assert.doesNotMatch(text,/0 USD|PV|Not valued/);
 assert.equal(await evaluate("document.querySelector('[data-instrument-side-group]').dataset.bindingId"),'');
}));

for(const reason of ['This zero-fee custody agreement has no financial instrument. Custody performance is retained separately.','No principal remains held by this service. Separate native stock and return owners retain their own instruments.'])
 test('an explicit financial-instrument absence remains readable: '+reason,browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
  await evaluate(fixture);await evaluate(`renderSides(document.querySelector('#host'),[],{zone:'UTC',retained:true,reason:${JSON.stringify(reason)}})`);
  assert.equal(await evaluate("document.querySelectorAll('[data-instrument-side-group]').length"),0);
  assert.match(await evaluate("document.querySelector('#host').textContent"),new RegExp(reason.replace(/[.*+?^${}()|[\]\\]/g,'\\$&')));
  assert.doesNotMatch(await evaluate("document.querySelector('#host').textContent"),/0 USD|PV.*0/);
 }));

test('missing roles, invented retained bindings, repeated slots and invalid marked facts refuse the entire composition',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(fixture);
 const mutations=["delete groups[0].sides[0].roleLabel","groups[0].bindingId=null","groups[0].sides.push(structuredClone(groups[0].sides[0]))","groups.push(structuredClone(groups[0]))","groups[0].sides[0].overviewSections[0].values[1].value='invalid time'","groups[0].sides[0].overviewSections[0].values[0].kind='inferred'","groups=[]"];
 for(const mutation of mutations){
  const refused=await evaluate(`(()=>{const saved=structuredClone(groups);try{${mutation};draw();return false;}catch{return true;}finally{groups=saved;}})()`);assert.equal(refused,true,mutation);
  assert.equal(await evaluate("document.querySelector('#host').children.length"),0,'no partial Side composition is presented');
 }
}));

test('a gapped or truncated membership list cannot present a complete actual composition',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(fixture);
 for(const slots of [[1],[0,2],[1,2]]){
  assert.equal(await evaluate(`(()=>{const saved=structuredClone(groups);groups[0].sides=${JSON.stringify(slots)}.map(slot=>({...structuredClone(saved[0].sides[0]),slot}));try{draw();return false;}catch{return true;}finally{groups=saved;}})()`),true);
  assert.equal(await evaluate("document.querySelector('#host').children.length"),0);
 }
}));

const ticket=`(async()=>{
 const {renderTicket}=await import('/deal-ticket.js'),{newTicket}=await import('/ticket-model.js');
 window.description={product:'synthetic_service',version:1,rowShape:'flow',tradeFields:[],sideLimits:[],levels:['term'],modes:['new','booked'],tabs:[],tradeColumns:[],sides:[],sideGroupsRequired:true};
 window.ticket=(mode='booked')=>renderTicket(document.querySelector('#host'),{state:newTicket(description),mode,presentation:mode==='booked'?'terms':undefined,actions:null,schedule:null,quantityUnit:null,instant:at=>at,zone:'America/Chicago',previous:null,summaries:[],links:[],sideGroups:groups,sideGroupsReason:window.sideGroupsReason,entry:null,status:{kind:'current',text:'Retained terms.'},fieldErrors:{},choices:{},selectedSide:null,tab:'instrument',level:'term',closed:new Set(),on:new Proxy({},{get:()=>()=>{}})});
})()`;

test('a flat managed ticket exposes actual retained Side groups beside its saved terms',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(fixture);await evaluate(ticket);await evaluate('window.shown=ticket()');
 assert.equal(await evaluate("document.querySelectorAll('[data-instrument-side-group]').length"),2);
 assert.match(await evaluate("document.querySelector('#host').textContent"),/Side · Remaining rights/);
 assert.equal(await evaluate("document.querySelectorAll('button,input,select').length"),0);
 await evaluate('shown.destroy()');assert.equal(await evaluate("document.querySelector('#host').children.length"),0);
}));

test('a required retained ticket cannot silently omit its Side groups',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(fixture);await evaluate(ticket);
 assert.equal(await evaluate("(()=>{groups=undefined;try{ticket();return false;}catch{return true;}})()"),true);
 assert.equal(await evaluate("document.querySelector('#host').children.length"),0);
}));

test('existing described Side cards remain the Side layout without a duplicate composition deck',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(fixture);await evaluate(ticket);
 await evaluate("description.sides=[{kind:'physical',pill:'Physical Side',colour:'gas',min:1,initial:1,max:1,fields:[],columns:[]}];ticket()");
 assert.equal(await evaluate("document.querySelectorAll('.ticket-card[data-side]').length"),1);
 assert.equal(await evaluate("document.querySelectorAll('.instrument-composition').length"),0);
}));

const fieldGroups=`(()=>{
 const field=(id,label)=>({id,label,meaning:'Explicit retained synthetic terms.',type:'decimal',unit:null,state:'editable',required:true,band:null,choices:null,dependsOn:[],lookup:null,soon:null,rule:null});
 description.tradeFields=[field('profile','Agreement profile'),field('quantity','Package quantity basis'),field('fee','Independent fee amount')];
 description.fieldGroups=[{id:'agreement',label:'Agreement and parties',kind:'agreement',fields:['profile']},{id:'rights',label:'Rights Side terms',kind:'side_terms',fields:['quantity']},{id:'fee',label:'Independent fee terms',kind:'independent_instrument_terms',fields:['fee']}];
 if(groups.length){groups[0].fieldGroupId='rights';groups[1].fieldGroupId='fee';}
})()`;

test('entry groups editable terms once by declared Side and keeps the actual reviewed memberships inside that same card',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(fixture);await evaluate(ticket);await evaluate(fieldGroups);await evaluate("ticket('new')");
 assert.deepEqual(await evaluate("[...document.querySelectorAll('[data-ticket-field-group]')].map(card=>card.dataset.ticketFieldGroup)"),['agreement','rights','fee']);
 for(const field of ['profile','quantity','fee'])assert.equal(await evaluate(`document.querySelectorAll('[data-field="${field}"] input').length`),1);
 await evaluate("document.querySelector('[aria-label=\"Agreement components\"] button:last-child').click()");
 assert.equal(await evaluate("document.activeElement.dataset.ticketFieldGroup"),'fee');
 assert.match(await evaluate("document.querySelector('[data-ticket-field-group=rights]').textContent"),/Rights Side terms.*Remaining rights.*Commercial entitlement/s);
 assert.match(await evaluate("document.querySelector('[data-ticket-field-group=fee]').textContent"),/Independent fee terms.*Recorded fee.*Company pays/s);
 assert.equal(await evaluate("document.querySelectorAll('[data-instrument-side-group]').length"),2,'there is no duplicate summary deck below the editable cards');
}));

test('saved Side cards retain every exact declared account, policy and source field alongside actual membership identity',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(fixture);await evaluate(ticket);await evaluate(fieldGroups);await evaluate("ticket('booked')");
 for(const id of ['profile','quantity','fee'])assert.equal(await evaluate(`document.querySelectorAll('[data-field="${id}"]').length`),1,'retained '+id+' is not hidden by structural metadata');
 assert.match(await evaluate("document.querySelector('[data-ticket-field-group=rights]').textContent"),/Package quantity basis.*Remaining rights/s);
 assert.equal(await evaluate("document.querySelectorAll('[data-instrument-side-group]').length"),2);
 assert.equal(await evaluate("document.querySelectorAll('input,select').length"),0);
}));

test('a valid zero-fee absence shows independent fee terms without inventing an editable cash Side',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(fixture);await evaluate(ticket);await evaluate(fieldGroups);
 await evaluate("groups=[];sideGroupsReason='No financial instrument exists for this zero-fee custody agreement.';ticket('new')");
 assert.match(await evaluate("document.querySelector('[data-ticket-field-group=fee]').textContent"),/Independent fee terms/);
 assert.equal(await evaluate("document.querySelectorAll('[data-instrument-side-slot]').length"),0);
 assert.match(await evaluate("document.querySelector('#host').textContent"),/No financial instrument exists/);
}));

test('returned Side mappings from another description cannot replace a current grouped ticket',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(fixture);await evaluate(ticket);await evaluate(fieldGroups);await evaluate("groups[0].fieldGroupId='another-profile'");
 assert.equal(await evaluate("(()=>{try{ticket('new');return false;}catch{return true;}})()"),true);
 assert.equal(await evaluate("document.querySelector('#host').children.length"),0);
}));
