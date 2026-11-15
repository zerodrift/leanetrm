import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const context={contextId:'source-loan',snapshotId:'snapshot-loan',asOf:'2026-11-02T15:00:00Z',businessDate:'2026-11-02'};
const action='gas.loan.reconcile_quantity',retainFees='retain_fixed_fee_memberships_v1',preserve='preserve_unallocated_source_quantity_v1',withdraw='withdraw_all_source_allocations_v1';
const offer={target:{handler:'gas_loan_quantity_reconciliation',scopeId:'account',bindingId:'loan'},action,label:'Correct commodity loan quantity',occurrenceId:'loan',
 at:context.asOf,before:null,eligible:true,reason:null,revision:0,stateVersion:0,policyId:'policy',policyRevision:1,selection:null,
 subject:{reference:'Synthetic account / observed loan',product:'Title-transfer gas loan',bookId:'book',book:'Synthetic book'}};
const discovery={context,actions:[offer],policies:[],optionProfiles:[],swapProfiles:[],marginPolicies:[],choices:{actions:[{code:action,automatic:false,configurationPermission:'ConfigureGasOwnedHoldings',executionPermission:'RecordGasOwnedHoldings'}],modes:['manual'],policyImplementation:'explicit_lifecycle_action_policy_v1'}};
const choice=(id,label,value)=>({id,label,value});
const reference=(name,label,members,id,value)=>({name,label,kind:'reference',nullable:false,members,choices:[choice(id,label+' · current native revision',value)]});
const allocation={name:'sourceAllocation',label:'Source quantity allocation',kind:'select',nullable:false,choices:[choice('preserve','Preserve unused source quantity',preserve),choice('withdraw','Withdraw every source allocation',withdraw)]};
const childTarget={handler:'gas_loan_consumption_quantity_source',scopeId:'loan',bindingId:'use'};
const setup={context,target:offer.target,accounts:[{id:'account',label:'Synthetic account'}],policies:[{id:'policy',revision:1,accountId:'account',action,label:'Observed loan correction'}],configuration:{},forms:{[action]:{label:offer.label,permission:'RecordGasOwnedHoldings',fields:[
 reference('observedLoan','Loan, stock and return revisions',['loanId','expectedRevision','stockExpectedRevision','returnExpectedRevision'],'head',{loanId:'loan',expectedRevision:3,stockExpectedRevision:3,returnExpectedRevision:3}),
 {name:'quantity',label:'Stated gross loan quantity',kind:'text',nullable:false},
 {name:'titleEvidenceId',label:'Observed title head',kind:'select',nullable:false,choices:[choice('title','Synthetic title source head','title-head')]},
 {name:'agreementEvidenceId',label:'Observed agreement head',kind:'select',nullable:false,choices:[choice('agreement','Synthetic agreement source head','agreement-head')]},allocation,
 {name:'feeDisposition',label:'Existing fixed fees',kind:'select',nullable:false,choices:[choice(retainFees,'Retain fixed cash memberships and bank payments',retainFees)]},
 {name:'consumptions',label:'Recorded uses',kind:'array',nullable:false,fields:[],memberLabel:'Recorded use or repayment',variants:[{id:'use',label:'Synthetic dispatch · revision 2',target:childTarget,parentLabel:'Synthetic account · recorded loan agreement'}]},
 {name:'evidence',label:'Correction evidence',kind:'text',nullable:false}]}}};
const child={...setup,target:childTarget,forms:{[action]:{label:'Correct recorded use',permission:'RecordGasOwnedHoldings',fields:[
 reference('observedQuantity','Recorded use revision',['consumptionId','expectedRevision'],'use-head',{consumptionId:'use',expectedRevision:2}),
 reference('identifiedStock','Identified stock and reservation',['lotId','reservationId'],'stock',{lotId:'stock',reservationId:null}),
 {name:'quantity',label:'Stated use quantity',kind:'text',nullable:false},
 {name:'retracted',label:'Withdraw recorded use',kind:'select',nullable:false,choices:[choice('false','Keep active quantity',false),choice('true','Withdraw quantity',true)]},
 {name:'evidenceId',label:'Observed use head',kind:'select',nullable:false,choices:[choice('use-evidence','Synthetic use source head','use-evidence-head')]},allocation]}}};
const details={quantityUnit:'synthetic_unit',currency:'usd',willChange:true,alreadyRecorded:false,
 loan:{id:'loan',quantity:'1.000000000000000001',expectedRevision:3},stock:{positionId:'stock',quantity:'1.000000000000000001',financialRevision:3},
 returnObligation:{positionId:'return',quantity:'-1.000000000000000001',financialRevision:3},
 funding:[{positionId:'funding',quantity:'0.25',financialRevision:1}],
 consumptions:[{id:'use',lot_id:'stock',quantity:'0.250000000000000001',financialRevision:2,effective_at:context.asOf}],
 repaymentFacts:[{repaymentId:'repayment',loanId:'loan',consumptionId:'repaid-use',originalQuantity:'0.25',currentQuantity:'0.125',currentRevision:2,lotId:'funding',effectiveAt:context.asOf}],
 fees:[{id:'fee',amount:'-0.10',currency_code:'usd',due_at:'2026-11-04T18:00:00Z',effective_at:context.asOf}],
 payments:[{id:'bank-1',fee_id:'fee',business_key:'synthetic_loan_bank',revision:1,amount:'-0.05',paid_at:context.asOf,evidence:'Synthetic original bank fact'},
  {id:'bank-2',fee_id:'fee',business_key:'synthetic_loan_bank',revision:2,amount:'-0.075',paid_at:context.asOf,evidence:'Synthetic bank correction'}],
 outcome:{acknowledgedRevision:4,currentRevision:4,currentQuantity:'0.750000000000000001',semanticAcknowledgement:false,consumptions:[{consumptionId:'use',acknowledgedRevision:3,currentRevision:3,currentQuantity:'0.125000000000000001',changed:true}]}};
const settle=()=>new Promise(resolve=>setTimeout(resolve,40));
const fill=`window.setField=(host,name,value)=>{const field=host.querySelector('[name="'+name+'"]');field.value=value;field.dispatchEvent(new Event('input',{bubbles:true}));field.dispatchEvent(new Event('change',{bubbles:true}));};`;

for(const retired of [false,true])test('loan quantity form retains exact source revisions, fixed fees and bank history with '+(retired?'explicit withdrawal':'exact revised quantities'),
 {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/app.css"><div id="host"></div>',async(evaluate,command)=>{
 const quantity=retired?'0':'0.750000000000000001',used=retired?'0':'0.125000000000000001';
 const reviewed=structuredClone(details);reviewed.outcome.currentQuantity=quantity;reviewed.outcome.consumptions[0].currentQuantity=used;
 await evaluate(`(async()=>{${fill}window.sent=[];window.previews=[];window.reads=[];window.errors=[];const {setupLifecycle}=await import('/lifecycle-view.js');const api=async(path,body)=>{
  if(path==='lifecycle/view')return ${JSON.stringify(discovery)};
  if(path==='lifecycle/gas/setup'){reads.push(body);return body.target.handler==='gas_loan_consumption_quantity_source'?${JSON.stringify(child)}:${JSON.stringify(setup)};}
  if(path==='lifecycle/preview'){previews.push(body);return{hash:'a'.repeat(64),offer:${JSON.stringify(offer)},details:${JSON.stringify(reviewed)}};}
  if(path==='lifecycle/execute'){sent.push(body);if(sent.length===1)throw Error('Synthetic lost successful reply');return{operationId:body.operationId,changed:true};}throw Error('Unexpected '+path);};
  window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>{try{await f();}catch(e){errors.push(e.message);}},busy:()=>false,identity:()=> 'operator',context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'UTC',permission:()=>true,changed(){}});await desk.load();})()`);
 await settle();await evaluate("document.querySelector('.tabulator-row').click()");await settle();
 assert.equal(await evaluate("!!document.querySelector('[name=observedLoan]')"),true,'The loan handler must open its backend-defined form.');
 for(const name of ['observedLoan','quantity','sourceAllocation','feeDisposition'])assert.equal(await evaluate(`document.querySelector('[name=${name}]').value`),'',name+' must remain an explicit choice.');
 assert.equal(await evaluate("document.querySelector('[name=effectiveAt]')===null"),true,'A quantity correction must not offer a replacement event time.');
 await evaluate(`(()=>{const form=document.querySelector('[data-lifecycle=panel] form');for(const[name,value]of Object.entries({policyId:'policy:1',observedLoan:'head',quantity:'${quantity}',titleEvidenceId:'title',agreementEvidenceId:'agreement',sourceAllocation:'${retired?'withdraw':'preserve'}',feeDisposition:'${retainFees}',consumptions:'entries',evidence:'Synthetic exact correction'}))setField(form,name,value);form.querySelector('[name=consumptions]').parentElement.querySelector(':scope > button').click();setField(form,'consumptions-member','use');})()`);await settle();
 assert.deepEqual((await evaluate('reads')).at(-1),{context,target:childTarget});
 await evaluate(`(()=>{const row=document.querySelector('[data-gas-entry=consumptions]');for(const[name,value]of Object.entries({observedQuantity:'use-head',identifiedStock:'stock',quantity:'${used}',retracted:'${retired}',evidenceId:'use-evidence',sourceAllocation:'${retired?'withdraw':'preserve'}'}))setField(row,name,value);document.querySelector('[data-lifecycle=panel] form').requestSubmit();})()`);await settle();
 assert.deepEqual(await evaluate('errors'),[]);const text=await evaluate("document.querySelector('[data-lifecycle-review]').textContent");
 for(const value of ['Source gross loan quantity','Stated received stock quantity','Source return obligation quantity','Original repayment quantity','Current recorded use quantity','Fixed fee cash memberships','Recorded bank payment history','synthetic_loan_bank','0.250000000000000001','-1.000000000000000001','-0.05','-0.075','USD',quantity,used])assert.ok(text.includes(value),value);
 assert.match(text,/Related positionSynthetic account · recorded loan agreement/);assert.doesNotMatch(text,/Delivery obligation/);
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await settle();
 assert.equal(await evaluate("(()=>{const facts=document.querySelector('.lifecycle-review-facts'),label=[...facts.querySelectorAll('dt')].find(n=>n.textContent==='Source gross loan quantity'),value=label.nextElementSibling,a=label.getBoundingClientRect(),b=value.getBoundingClientRect();return b.top>=a.bottom&&b.width>=facts.clientWidth*.9;})()"),true,'Narrow loan review values must use the full line below their labels.');
 assert.doesNotMatch(text,/available stock quantity/i);assert.equal(await evaluate('sent.length'),0);
 await evaluate("document.querySelector('[data-lifecycle-execute]').click()");await settle();assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),false);
 await evaluate("document.querySelector('[data-lifecycle=retry]').click()");await settle();const sent=await evaluate('sent');assert.equal(sent.length,2);assert.deepEqual(sent[0],sent[1]);
 assert.deepEqual(sent[0].selection,{loanId:'loan',expectedRevision:3,stockExpectedRevision:3,returnExpectedRevision:3,quantity,titleEvidenceId:'title-head',agreementEvidenceId:'agreement-head',sourceAllocation:retired?withdraw:preserve,feeDisposition:retainFees,
  consumptions:[{consumptionId:'use',expectedRevision:2,lotId:'stock',reservationId:null,quantity:used,retracted:retired,evidenceId:'use-evidence-head',sourceAllocation:retired?withdraw:preserve}],evidence:'Synthetic exact correction'});
 assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);
}));

test('loan review distinguishes older acknowledgement quantities and rejects lossy or incomplete counterpart rows',
 {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div></div>',async evaluate=>{
 const selection={quantity:'0',sourceAllocation:withdraw,feeDisposition:retainFees,evidence:'Synthetic old quantity fact',
  consumptions:[{consumptionId:'use',quantity:'0',retracted:true,sourceAllocation:withdraw}]};
 const source=structuredClone(details);source.willChange=false;source.alreadyRecorded=true;source.outcome.acknowledgedRevision=1;
 source.outcome.consumptions[0].acknowledgedRevision=1;source.outcome.consumptions[0].changed=false;
 const result=await evaluate(`(async()=>{const {loanQuantityReview}=await import('/gas-loan-quantity-review.js'),source=${JSON.stringify(source)},selection=${JSON.stringify(selection)};
  const reviewed=loanQuantityReview(source,selection,value=>value),rejected=[];
  for(const[name,edit]of Object.entries({loan:d=>d.loan.quantity=1,stock:d=>d.stock.quantity=1,debt:d=>d.returnObligation.quantity=-1,
   funding:d=>d.funding[0].quantity=.25,use:d=>d.consumptions[0].quantity=.25,originalRepayment:d=>d.repaymentFacts[0].originalQuantity=.25,
   currentRepayment:d=>d.repaymentFacts[0].currentQuantity=.125,fee:d=>d.fees[0].amount=-.1,bank:d=>d.payments[1].amount=-.075,
   currency:d=>d.fees[0].currency_code='eur',current:d=>d.outcome.currentQuantity=.75,missingRepayments:d=>delete d.repaymentFacts,
   wrongUse:d=>d.outcome.consumptions[0].consumptionId='other',duplicateUse:d=>d.outcome.consumptions.push(d.outcome.consumptions[0])})){
   const changed=structuredClone(source);edit(changed);try{loanQuantityReview(changed,selection,value=>value);}catch{rejected.push(name);}}
  for(const[name,edit]of Object.entries({quantity:s=>s.quantity=0,feeLaw:s=>s.feeDisposition='guess',allocation:s=>s.sourceAllocation='',
   retraction:s=>s.consumptions[0].retracted='true'})){const changed=structuredClone(selection);edit(changed);try{loanQuantityReview(source,changed,value=>value);}catch{rejected.push(name);}}
  return{reviewed,rejected};})()`);
 const facts=Object.fromEntries(result.reviewed.facts);assert.equal(facts['Acknowledged gross loan quantity'],'0');assert.equal(facts['Acknowledged loan revision'],'1');
 assert.equal(facts['Retained current loan revision'],'4');assert.equal(facts['Retained current gross loan quantity'],'0.750000000000000001');
 assert.equal(result.reviewed.tables.find(row=>row.label==='Recorded repayments').rows[0][2],'0.25');
 assert.equal(result.reviewed.tables.find(row=>row.label==='Recorded repayments').rows[0][3],'0.125');
 assert.deepEqual(result.rejected,['loan','stock','debt','funding','use','originalRepayment','currentRepayment','fee','bank','currency','current','missingRepayments','wrongUse','duplicateUse','quantity','feeLaw','allocation','retraction']);
}));
