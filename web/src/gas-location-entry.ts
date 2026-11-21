import {setupTicket,definiteTicketRefusal,type TicketHandle,type TicketOptions} from './ticket-controller.js';
import {actorTransport,PendingCommand,restoreTradeRequest} from './pending-command.js';
import type {TicketDraft} from './ticket-types.js';

type Options=Pick<TicketOptions,'api'|'instant'|'zone'|'actor'>&{source:()=>{snapshotId:string};historical:()=>boolean;onBooked:(trade:string)=>Promise<void>};
type Receipt={operationId:string;agreements:string;purchaseTradeId:string;saleTradeId:string;purchaseReference:number;saleReference:number};
const path='tickets/gas-locations/book';
function element<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string){const node=document.createElement(tag);if(text)node.textContent=text;return node;}
function button(label:string,run:()=>void){const b=element('button',label);b.type='button';b.onclick=run;return b;}
function choice(host:HTMLElement,label:string,entries:string[][]){
 const field=element('label',label),select=element('select');select.required=true;select.add(new Option('Choose…',''));
 entries.forEach(([id,text])=>select.add(new Option(text,id)));field.append(select);host.append(field);return select;
}

export function setupGasLocationTicket(o:Options){
 const root=document.getElementById('gas-locations-page')!;
 const stored=actorTransport<ReturnType<PendingCommand['begin']>>(()=>'openetrm.gas.locations.'+o.actor(),()=>sessionStorage);
 const restore=()=>{const cmd=restoreTradeRequest(stored.read());return cmd?.path===path?cmd:undefined;};
 const context=()=>({...o.source(),convention:'physical_gas_fixed_quantity_v4',knowledgeMode:'current'});
 let editors:TicketHandle[]=[],generation=0,started=false,sending=false,edited=false,owner='',contextId='';
 const pending=new PendingCommand(definiteTicketRefusal);
 const reset=()=>{generation++;for(const e of editors)e.destroy();editors=[];started=edited=sending=false;pending.clear();root.replaceChildren();};
 function begin(){
  reset();owner=o.actor();contextId=o.source().snapshotId;
  const g=generation,alive=()=>g===generation&&owner===o.actor();
  const saved=restore();if(saved)pending.command={...saved,uncertain:true};
  if(!saved&&o.historical())throw Error('Return to working data to enter a gas trade.');
  const fields=element('fieldset'),bar=element('div'),panels=element('div'),actions=element('div'),status=element('p');
  bar.className=actions.className='schedule-fields';status.setAttribute('role','status');fields.append(element('legend','Purchase and sale'),bar);
  const pricing=[['fixed','Fixed rate'],['indexed','Index and spread']];
  const agreement=choice(bar,'Confirmations',[['one_agreement','One bilateral agreement'],['separate_confirmations','Separate purchase and sale confirmations']]);
  const purchase=choice(bar,'Purchase price',pricing),sale=choice(bar,'Sale price',pricing);
  agreement.id='gas-location-agreements';purchase.id='gas-location-purchase-price';sale.id='gas-location-sale-price';
  const start=button('Assemble Sides',()=>void assemble());start.id='gas-location-assemble';start.disabled=true;bar.append(start);
  root.append(fields,panels,actions,status);
  function sync(){
   start.disabled=sending||started||!agreement.value||!purchase.value||!sale.value;
   fields.disabled=sending||started||!!pending.command;
   actions.replaceChildren();
   if(pending.command){const check=button('Check booking status',()=>void submit(true)),retry=button('Retry original booking',()=>void submit(false));
    check.disabled=retry.disabled=sending;actions.append(check,retry);panels.inert=true;return;}
   panels.inert=sending;
   if(started){const save=button(agreement.value==='one_agreement'?'Book bilateral trade':'Book both confirmations',()=>void book());
    save.disabled=sending||!editors.length||!editors.every(e=>e.reviewed?.());actions.append(save);}
  }
  fields.onchange=()=>{edited=true;sync();};
  async function assemble(){
   if(start.disabled)return;sending=true;sync();
   try{
    const drafts=await o.api<TicketDraft[]>('tickets/gas-locations/start',{context:context(),agreements:agreement.value,purchasePrice:purchase.value,salePrice:sale.value});
    if(!alive())return;
    if(drafts.length!==(agreement.value==='one_agreement'?1:2))throw Error('The returned Sides do not match the selected agreement structure.');
    started=true;
    drafts.forEach((draft,i)=>{
     const section=element('section'),host=element('div');section.append(element('h3',drafts.length===1?'Bilateral purchase and sale':i===0?'Purchase confirmation':'Sale confirmation'),host);panels.append(section);
     editors.push(setupTicket(host,{...o,context,product:'physical_gas',initialDraft:draft,groupReview:sync,booked:()=>{}}));
    });
    status.textContent='Preview each ticket with its own locations, quantities, parties and payment terms.';
   }catch(error){if(alive())status.textContent=String(error);}
   finally{if(alive()){sending=false;sync();}}
  }
  async function book(){
   if(sending||o.historical()||o.source().snapshotId!==contextId){status.textContent='Reopen entry in the selected working context.';return;}
   const tickets=editors.map(e=>e.reviewed?.());if(tickets.some(t=>!t))return;
   pending.begin(path,{context:context(),agreements:agreement.value,purchasePrice:purchase.value,salePrice:sale.value,tickets});stored.write(pending.command);await submit(false);
  }
  async function submit(check:boolean){
   const cmd=pending.command;if(!cmd||sending||!alive())return;
   sending=true;const uncertain=check?true:pending.sending();stored.write(pending.command);sync();
   try{
    const r=await o.api<Receipt|{found:false}>(check?'tickets/gas-locations/status':path,check?{requestId:cmd.operationId}:{...cmd.body,operationId:cmd.operationId});
    if(!alive())return;
    if('found' in r){status.textContent='No completed booking was found. The original request is available to retry.';return;}
    if(r.operationId!==cmd.operationId||r.agreements!==cmd.body.agreements||!r.purchaseTradeId||!r.saleTradeId||
       (r.purchaseTradeId===r.saleTradeId)!==(r.agreements==='one_agreement'))throw Error('The receipt does not identify the original confirmations.');
    pending.clear();stored.write(undefined);for(const e of editors)e.destroy();editors=[];started=edited=false;panels.replaceChildren();fields.hidden=true;
    status.textContent=r.agreements==='one_agreement'?'Bilateral trade booked.':'Both confirmations booked.';
    const open=(label:string,id:string)=>button(label,()=>void o.onBooked(id));
    panels.append(open((r.purchaseTradeId===r.saleTradeId?'Trade ':'Purchase ')+r.purchaseReference,r.purchaseTradeId));
    if(r.saleTradeId!==r.purchaseTradeId)panels.append(open('Sale '+r.saleReference,r.saleTradeId));
   }catch(error){if(alive()){pending.failed((error as {code?:string}).code,uncertain);stored.write(pending.command);status.textContent=String(error);}}
   finally{if(alive()){sending=false;sync();}}
  }
  if(saved){status.textContent='The original booking is awaiting confirmation.';fields.hidden=true;}sync();
 }
 return {root,begin:async()=>begin(),inspect:async()=>{throw Error('Open each booked gas trade from its Blotter reference.');},reset,sync:()=>{},
  unsaved:()=>edited||started||!!pending.command,pending:()=>!!restore()};
}
