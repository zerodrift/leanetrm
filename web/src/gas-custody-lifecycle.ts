import {mountOperationFields} from './operation-fields.js';
import type {GasForm,GasOwnedSetup,GasOwnedUiOptions} from './gas-owned-lifecycle.js';

function mountForm(root:HTMLElement,definition:GasForm,o:GasOwnedUiOptions,kind:'custodyObservation'|'custodyPayment'|'transportPayment',label:string,record:(selection:Record<string,unknown>)=>Promise<void>){
 const section=document.createElement('details'),heading=document.createElement('summary');heading.textContent=definition.label;section.append(heading);
 const form=document.createElement('form');form.className='lifecycle-form';form.dataset[kind]='';form.dataset.lifecyclePermission=definition.permission;section.append(form);
 const fields=document.createElement('fieldset');fields.className='schedule-fields';fields.disabled=!o.permission(definition.permission);form.append(fields);
 const editor=mountOperationFields(fields,definition.fields,o);
 const save=document.createElement('button');save.type='submit';save.textContent=label;form.append(save);root.append(section);
 form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{
  if(!o.permission(definition.permission))throw Error('Current '+definition.label.toLowerCase()+' permission is required.');
  const selection=await editor.read();if(!o.current())return;
  if(!o.permission(definition.permission))throw Error('Restore the current permission before recording.');
  await record(selection);
 });};
 return{edited:editor.dirty,refresh:editor.refresh};
}

export function mountCustodyObservation(root:HTMLElement,setup:GasOwnedSetup,o:GasOwnedUiOptions){
 const definition=setup.configuration.evidence,target=setup.target;
 if(!definition||target?.handler!=='gas_park_custody')throw Error('Read the exact custody observation fields and agreement target.');
 return mountForm(root,definition,o,'custodyObservation','Record custody observation',async selection=>
  o.queue('gas/park-custody/evidence',{operationId:crypto.randomUUID(),context:structuredClone(setup.context),target:structuredClone(target),selection},'Record custody observation'));
}

export function mountCustodyPayment(root:HTMLElement,setup:GasOwnedSetup,o:GasOwnedUiOptions){
 return mountPayment(root,setup,o,'gas_park_custody');
}

export function mountTransportPayment(root:HTMLElement,setup:GasOwnedSetup,o:GasOwnedUiOptions){
 return mountPayment(root,setup,o,'gas_transport_financial');
}

function mountPayment(root:HTMLElement,setup:GasOwnedSetup,o:GasOwnedUiOptions,handler:'gas_park_custody'|'gas_transport_financial'){
 const definition=setup.configuration.payment,target=setup.target;
 if(!definition||definition.permission!=='AmendTrade'||target?.handler!==handler)throw Error('Read the exact independent cash owner and ordinary payment fields.');
 const transport=handler==='gas_transport_financial';
 return mountForm(root,definition,o,transport?'transportPayment':'custodyPayment','Record observed payment',async selection=>{
  const keys=['snapshotId','knowledgeMode','tradeId','bindingId','allocationId','expectedRevision','paidAt','amount','policy'];
  if(Object.keys(selection).length!==keys.length||keys.some(key=>!Object.hasOwn(selection,key))||selection.snapshotId!==setup.context.snapshotId||
   selection.knowledgeMode!=='current'||selection.tradeId!==target.scopeId||typeof selection.bindingId!=='string'||!selection.bindingId||
   typeof selection.allocationId!=='string'||!selection.allocationId||!Number.isSafeInteger(selection.expectedRevision)||(selection.expectedRevision as number)<0)
   throw Error('Reread this agreement’s exact accepted cash owner, payment allocation and captured source.');
  await o.queue('cash-payments/record',{...selection,operationId:crypto.randomUUID()},transport?'Record observed Transport fee payment':'Record observed custody fee payment');
 });
}
