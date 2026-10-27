import test from 'node:test';
import assert from 'node:assert/strict';
import {exactText,measureRows,inspectionRows,refusalText,ReviewedPpaCommand,componentQuantity,quantityLabel,labelText} from '../dist/ppa-view.js';
const q=(numerator,denominator='1')=>({numerator,denominator});
test('PPA measures preserve exact fractions and unavailable PV beside holdings and overdue amounts',()=>{
 const rows=measureRows({valueExcludingOverdue:q('100'),completePv:null,unavailableReason:'due_cash_policy_required',overdueKnownBalance:q('-860'),overdueEstimated:q('1','3')});
 assert.deepEqual(rows,[['Complete PV','Unavailable · due_cash_policy_required'],['Value excl. overdue cash (undue PV + holdings)','100'],['Overdue balance (known)','-860'],['Overdue (estimated)','1/3']]);
 assert.equal(exactText(q('9007199254740993000001','7')),'9007199254740993000001/7');assert.equal(exactText(q('0')),'0');
});
test('inspection distinguishes repeated hours and shows server prices without arithmetic',()=>{
 const rows=inspectionRows([{interval:{start:'2031-11-02T05:00:00Z',end:'2031-11-02T06:00:00Z'},quantity:q('2'),amount:q('80'),kind:'Payment',quantityState:'Metered',economicClass:'Held',coverage:'HoldingValue',valueAt:'2031-11-02T06:00:00Z',priceComponents:[{price:q('40'),state:'Actual'}]},
 {interval:{start:'2031-11-02T06:00:00Z',end:'2031-11-02T07:00:00Z'},quantity:q('0'),amount:q('0'),kind:'Payment',quantityState:'Projected',economicClass:'Unrealised',coverage:'Discounted',valueAt:'2031-11-02T07:00:00Z',priceComponents:[{price:q('99','7'),state:'Projected'}]}],'America/New_York');
 assert.equal(rows[0][0],'Nov 2, 2031, 1:00 AM GMT-4');assert.equal(rows[1][0],'Nov 2, 2031, 1:00 AM GMT-5');assert.ok(rows[1].includes('99/7 · Projected'));assert.ok(rows[1].includes('0'));
});
test('revision refusal retains authorized scope and both mismatch reasons',()=>{
 const value=refusalText({code:'ppa_definition_revision',message:'No valuation',scope:{scope:'books',selectedIds:['book']},affectedBindings:[{tradeId:'trade',bindingId:'binding',reasons:['ppa_definition_revision','ppa_reference_revision']}]});
 for(const word of ['ppa_definition_revision','ppa_reference_revision','book','trade','binding'])assert.ok(value.includes(word));
});
test('an edited draft cannot submit stale reviewed economics and an uncertain write retains original identity',()=>{
 const state=new ReviewedPpaCommand();state.review('ppa/book',{quantity:'80'},()=> 'op');assert.equal(state.command.operationId,'op');state.invalidate();assert.equal(state.command,undefined);
 state.review('ppa/book',{quantity:'80'},()=> 'op2');state.sending();state.invalidate();assert.deepEqual(state.command,{operationId:'op2',path:'ppa/book',body:{quantity:'80'},uncertain:true});assert.throws(()=>state.review('ppa/book',{quantity:'20'},()=> 'op3'),/outstanding/);state.complete();assert.equal(state.command,undefined);
});

test('pending PPA writes survive reload, reject foreign paths, and distinguish first refusals from uncertain retries',()=>{
 const memory=new Map(),storage={getItem:k=>memory.get(k)??null,setItem:(k,v)=>memory.set(k,v),removeItem:k=>memory.delete(k)};
 const first=new ReviewedPpaCommand(storage,'actor-A');first.review('ppa/book',{quantity:'80'},()=> 'op');first.sending();
 const restored=new ReviewedPpaCommand(storage,'actor-A');assert.equal(restored.command.operationId,'op');assert.equal(new ReviewedPpaCommand(storage,'actor-B').command,undefined);
 restored.failed('access_denied',true);assert.equal(restored.command.operationId,'op');restored.complete();assert.equal(new ReviewedPpaCommand(storage,'actor-A').command,undefined);
 first.complete();first.review('ppa/book',{quantity:'80'},()=> 'new');const was=first.sending();first.failed('ppa_conflict',was);assert.equal(first.command,undefined);
 storage.setItem('bad',JSON.stringify({path:'admin/update',operationId:'op',body:{}}));assert.equal(new ReviewedPpaCommand(storage,'bad').command,undefined);
});

test('amendment retains each independent quantity source and stage until explicitly replaced',()=>{
 const selected={kind:'volume',sources:['delivered'],stage:'delivered'},retained={kind:'volume',sources:['generated'],stage:'generated'};
 assert.deepEqual(componentQuantity(selected,retained,'retained'),retained);
 assert.deepEqual(componentQuantity(selected,retained,'selected'),selected);
 const links={kind:'slots',slots:[{role:'certificate_delivery',ordinal:0}]};assert.deepEqual(componentQuantity({kind:'slots',slots:[{role:'energy_delivery',ordinal:0}]},links,'retained'),links);
 assert.throws(()=>componentQuantity(selected,retained,''),/Choose/);
});

test('inspection resolves physical unit labels only within the exact reference revision',()=>{
 const catalog={reference:{catalogId:'c',revisionId:'r'},references:{quantitySpecs:[{code:'energy',unit_code:'mwh'}],units:[{code:'mwh',symbol:'MWh'}]}};
 assert.equal(quantityLabel({revision:{catalogId:'c',revisionId:'r'},code:'energy'},catalog),'MWh [energy]');
 assert.throws(()=>quantityLabel({revision:{catalogId:'c',revisionId:'other'},code:'energy'},catalog),/revision/);
 assert.throws(()=>quantityLabel({revision:{catalogId:'c',revisionId:'r'},code:'missing'},catalog),/unit/);
});
test('a field is named by its label\'s own words, never by the choices of the control inside it',()=>{
 const text=t=>({nodeName:'#text',textContent:t}),node=(nodeName,...childNodes)=>({nodeName,childNodes,textContent:childNodes.map(c=>c.textContent).join('')});
 const pending=node('LABEL',text('Pending delivered actuals'),node('SELECT',node('OPTION',text('Choose…')),node('OPTION',text('Require Actuals V1'))));
 assert.equal(labelText(pending),'Pending delivered actuals');
 assert.equal(labelText(node('LABEL',text('Termination boundary · '),node('SPAN',text('America/Chicago')),node('INPUT'))),'Termination boundary · America/Chicago','words in the label beside the control are kept');
 assert.equal(labelText(node('LABEL',text('  Memo  '),node('TEXTAREA',text('draft')))),'Memo');
});
