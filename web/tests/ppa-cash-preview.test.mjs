import test from 'node:test';
import assert from 'node:assert/strict';
import {paymentLineageRequest} from '../dist/ppa-cash-preview.js';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
const terms={referenceCatalogId:'c',referenceRevisionId:'r',currency:'gbp'};
const instrument=(id,side)=>({instrumentId:id,terms,sideIds:[{role:'strike_payment',ordinal:0,sideId:side}]});
const editor={tradeId:'t',head:{revision:3},instruments:[instrument('i1','s1'),instrument('i2','s2')],bindings:[{binding_id:'b1',instrument_id:'i1'},{binding_id:'b2',instrument_id:'i2'}],revisionBindings:[{revision:1,bindings:[{bindingId:'b1',instrumentId:'i1'}]},{revision:2,bindings:[{bindingId:'b1',instrumentId:'i1'},{bindingId:'b2',instrumentId:'i2'}]},{revision:3,bindings:[{bindingId:'b2',instrumentId:'i2'}]}]};
const policies={contract:'ppa_monthly_gross_fixed_v1',evidence:'verified_meter_head_v1',rounding:'cash_explicit_increment_per_occurrence_v1',approval:'two_principals_exact_action_24h_v1',correction:'restate_rounded_occurrence_v1',adjustmentDue:'original_due_for_adjustment_v1'};
const capability={policies,indexedPolicies:{...policies,contract:'ppa_monthly_gross_indexed_v1'}};
const rounding={currency:{revision:{catalogId:'c',revisionId:'r'},code:'gbp'},increment:'0.05',tieRule:'to_even',stage:'payment_total'};
test('payment continuity names original and successor exact memberships without choosing a currency or rounding default',()=>{
 const request=paymentLineageRequest(editor,'b1','s1','snapshot',capability,rounding);
 assert.deepEqual(request.original,{bindingId:'b1',originRevision:1,instrumentId:'i1',sideId:'s1',role:'strike_payment',ordinal:0});
 assert.deepEqual(request.mappings.map(m=>[m.bindingId,m.originRevision,m.sideId]),[['b1',1,'s1'],['b2',2,'s2']]);
 assert.equal(request.roundingTerms,rounding);assert.equal(request.expectedRevision,3);
 assert.throws(()=>paymentLineageRequest(editor,'b1','s1','snapshot',capability,{...rounding,increment:''}),/explicitly/);
 assert.throws(()=>paymentLineageRequest(editor,'b1','s1','snapshot',capability,{...rounding,currency:{...rounding.currency,revision:{catalogId:'c',revisionId:'other'}}}),/original payment currency/);
});
const setup=`
 const {mountPpaCashPreview}=await import('/ppa-cash-preview.js');
 const editor=${JSON.stringify(editor)},capability=${JSON.stringify(capability)};
 const lineage={id:'l',original_binding_id:'b1',original_side_id:'s1',rounding:'cash_explicit_increment_per_occurrence_v1',rounding_increment:'0.05',rounding_tie_rule:'to_even',rounding_stage:'payment_total',currency_code:'gbp',reference_catalog_id:'c',reference_revision_id:'r'};
 let busy=false,known='old',pending=Promise.resolve(),saved=false,previewBody,readGate,refreshes=0;
 const host=document.querySelector('#host');
 const api=async(path,body)=>{
  if(path==='settlement/payment-lineages/read'){if(readGate)await readGate;return saved?[lineage]:[];}
  if(path==='settlement/payment-lineages/plan'){saved=true;return {lineage};}
  if(path==='settlement/ppa/preview'){previewBody=body;return {exactCash:{numerator:'1',denominator:'8'},roundedCash:{numerator:'1',denominator:'10'},roundingDifference:{numerator:'-1',denominator:'40'},roundingTerms:body.roundingTerms??${JSON.stringify(rounding)}};}
  if(path==='amounts/display')return body.map(r=>r.value);
  throw Error(path);
 };
 let mounted;
 const act=work=>{pending=(async()=>{busy=true;mounted.sync();try{await work();}finally{busy=false;host.querySelectorAll('input,select,button').forEach(n=>n.disabled=false);mounted.sync();}})();};
 mounted=mountPpaCashPreview(host,{api,act,editor,snapshot:()=>'snapshot',known:async()=>known,capability,can:true,busy:()=>busy,useCurrentKnowledge:async()=>{known='new';refreshes++;}});
 const controls=()=>({side:host.querySelectorAll('select')[0],tie:host.querySelectorAll('select')[1],increment:host.querySelector('input:not([type=month])'),month:host.querySelector('[type=month]')});
 const button=text=>[...host.querySelectorAll('button')].find(b=>b.textContent.includes(text));
 const choose=()=>{const c=controls();c.side.value=c.side.options[1].value;c.side.dispatchEvent(new Event('change'));c.increment.value='0.05';c.tie.value='to_even';};
`;
test('saving explicitly refreshes knowledge before immediate preview and retained rounding stays disabled after global busy synchronization',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{${setup}choose();button('Save terms').click();await pending;const immutable=[controls().increment.disabled,controls().tie.disabled];controls().month.value='2020-11';button('Preview complete').click();await pending;return {immutable,known:previewBody.knownAt,refreshes,rounding:controls().increment.value,output:host.textContent.includes('rounded cash')};})()`);
 assert.deepEqual(result,{immutable:[true,true],known:'new',refreshes:1,rounding:'0.05',output:true});
}));
test('disposing a panel prevents a late retained read from changing the current page',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 assert.deepEqual(await evaluate(`(async()=>{${setup}let release;readGate=new Promise(r=>release=r);button('Read retained').click();mounted.dispose();host.replaceChildren(document.createTextNode('replacement'));release();await pending;return {text:host.textContent,known};})()`),{text:'replacement',known:'old'});
}));
