import {setupTicket,definiteTicketRefusal,type TicketHandle,type TicketOptions} from './ticket-controller.js';
import {actorTransport,PendingCommand,restoreTradeRequest} from './pending-command.js';
import {setupTabs} from './side-panel.js';
import type {TicketDraft} from './ticket-types.js';

type Options=Pick<TicketOptions,'api'|'instant'|'zone'|'actor'>&{source:()=>{snapshotId:string};historical:()=>boolean;busy:()=>boolean;onBooked:(trade:string)=>Promise<void>};
type Receipt={operationId:string;agreements:string;purchaseTradeId:string;saleTradeId:string;purchaseReference:number;saleReference:number};
const path='tickets/gas-locations/book';
function element<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string){const node=document.createElement(tag);if(text)node.textContent=text;return node;}
function button(label:string,run:()=>void){const b=element('button',label);b.type='button';b.onclick=run;return b;}
function options(name:string,title:string,choices:readonly (readonly [string,string,string?])[]){
 const group=element('fieldset');group.className='location-choice';group.append(element('legend',title));const inputs:HTMLInputElement[]=[];
 const row=element('div');row.className='location-options';group.append(row);
 for(const [value,caption,detail] of choices){
  const label=element('label'),input=element('input');input.type='radio';input.name=name;input.value=value;input.required=true;
  const copy=element('span');copy.append(element('strong',caption));if(detail)copy.append(element('small',detail));
  label.append(input,copy);row.append(label);inputs.push(input);
 }
 return {group,value:()=>inputs.find(input=>input.checked)?.value};
}
function endpoint(sale:boolean){
 const leg=sale?'sale':'purchase',section=element('section');section.className='location-endpoint';section.dataset.leg=leg;
 const head=element('header'),mark=element('span',sale?'↗':'↙');mark.className='location-flow-mark';mark.setAttribute('aria-hidden','true');
 head.append(mark,element('h3',sale?'Sale':'Purchase'));
 const flow=element('p');flow.className='location-flows';flow.append(element('span',sale?'Deliver gas':'Receive gas'),element('span',sale?'Receive payment':'Pay for gas'));
 const price=options('gas-location-'+leg+'-price','Payment price',[['fixed','Fixed rate'],['indexed','Indexed']]);
 section.append(head,flow,price.group);return {section,value:price.value};
}

export function setupGasLocationTicket(o:Options){
 const root=document.getElementById('gas-locations-page')!;root.dataset.ownControls='';
 const stored=actorTransport<ReturnType<PendingCommand['begin']>>(()=>'openetrm.gas.locations.'+o.actor(),()=>sessionStorage);
 const restore=()=>{const cmd=restoreTradeRequest(stored.read());return cmd?.path===path?cmd:undefined;};
 const context=()=>({...o.source(),convention:'physical_gas_fixed_quantity_v4',knowledgeMode:'current'});
 let editors:TicketHandle[]=[],generation=0,started=false,sending=false,edited=false,owner='',contextId='';
 let refresh=()=>{};
 const pending=new PendingCommand(definiteTicketRefusal);
 const reset=()=>{generation++;refresh=()=>{};for(const e of editors)e.destroy();editors=[];started=edited=sending=false;pending.clear();root.replaceChildren();};
 function begin(){
  reset();owner=o.actor();contextId=o.source().snapshotId;
  const g=generation,alive=()=>g===generation&&owner===o.actor();
  const saved=restore();if(saved)pending.command={...saved,uncertain:true};
  if(!saved&&o.historical())throw Error('Return to working data to enter a gas trade.');
  const fields=element('fieldset'),structure=element('div'),route=element('div'),initialActions=element('div');
  fields.className='location-setup';const heading=element('h2','Trade structure');heading.id='gas-location-structure';fields.setAttribute('aria-labelledby',heading.id);
  const agreement=options('gas-location-agreements','Confirmations',[
   ['one_agreement','One bilateral agreement','Four Sides on one trade'],
   ['separate_confirmations','Separate confirmations','One trade per location']]);
  structure.className='location-structure';structure.append(heading,agreement.group);
  const purchase=endpoint(false),sale=endpoint(true);route.className='location-route';route.append(purchase.section,sale.section);
  const start=button('Assemble Sides',()=>void assemble());start.id='gas-location-assemble';start.disabled=true;
  initialActions.className='location-initial-actions';initialActions.append(start);fields.append(structure,route,initialActions);

  const work=element('section'),workHead=element('header'),workTitle=element('h2'),navigation=element('div'),panels=element('div');
  work.className='location-work';work.hidden=true;workHead.className='location-work-head';navigation.id='gas-location-legs';navigation.className='gas-view-tabs';
  panels.id='gas-location-editors';workHead.append(workTitle,navigation);work.append(workHead,panels);
  const actions=element('div'),status=element('p'),receipt=element('div');actions.className='location-booking-actions';receipt.className='location-receipt';status.setAttribute('role','status');status.className='location-message';
  const bookButton=button('Book',()=>void book()),check=button('Check booking status',()=>void submit(true)),retry=button('Retry original booking',()=>void submit(false));
  check.className=retry.className='secondary';actions.append(bookButton,check,retry);root.append(fields,work,status,actions,receipt);
  function sync(){
   const busy=sending||o.busy();
   start.disabled=busy||started||!agreement.value()||!purchase.value()||!sale.value();
   fields.disabled=busy||started||!!pending.command;
   work.inert=busy||!!pending.command;
   bookButton.hidden=!started||!!pending.command;bookButton.disabled=busy||!editors.length||!editors.every(e=>e.reviewed?.());
   check.hidden=retry.hidden=!pending.command;check.disabled=retry.disabled=busy;actions.hidden=bookButton.hidden&&check.hidden;
  }
  refresh=sync;
  fields.onchange=()=>{edited=true;sync();};
  async function assemble(){
   if(start.disabled)return;
   const agreements=agreement.value(),purchasePrice=purchase.value(),salePrice=sale.value();
   sending=true;sync();
   try{
    const drafts=await o.api<TicketDraft[]>('tickets/gas-locations/start',{context:context(),agreements,purchasePrice,salePrice});
    if(!alive())return;
    const bilateral=agreements==='one_agreement';
    if(drafts.length!==(bilateral?1:2))throw Error('The returned Sides do not match the selected agreement structure.');
    started=true;fields.hidden=true;work.hidden=false;
    workTitle.textContent=bilateral?'Bilateral agreement':'Separate confirmations';
    const price=(value:string|undefined)=>value==='fixed'?'fixed rate':'index + spread';
    const sideLabels={purchase:'Receive gas',purchase_payment:'Pay · '+price(purchasePrice),sale:'Deliver gas',sale_payment:'Receive · '+price(salePrice)};
    drafts.forEach((draft,i)=>{
     const host=element('section');if(!bilateral)host.dataset.confirmation=i===0?'purchase':'sale';panels.append(host);
     editors.push(setupTicket(host,{...o,context,product:'physical_gas',initialDraft:draft,groupReview:sync,sideLabels,booked:()=>{}}));
    });
    panels.dataset.leg='purchase';
    setupTabs(navigation,[['purchase','Purchase'],['sale','Sale']],{label:'Location Sides',panel:panels,selected:'purchase',select:id=>{panels.dataset.leg=id;}});
    bookButton.textContent=bilateral?'Book bilateral trade':'Book both confirmations';
    status.textContent=bilateral?'Preview the four Sides before booking.':'Preview the purchase and sale before booking both confirmations.';
   }catch(error){if(alive())status.textContent=String(error);}
   finally{if(alive()){sending=false;sync();if(started)navigation.querySelector('button')!.focus({preventScroll:true});}}
  }
  async function book(){
   if(sending||o.busy()||o.historical()||o.source().snapshotId!==contextId){status.textContent='Reopen entry in the selected working context.';return;}
   const tickets=editors.map(e=>e.reviewed?.());if(tickets.some(t=>!t))return;
   pending.begin(path,{context:context(),agreements:agreement.value(),purchasePrice:purchase.value(),salePrice:sale.value(),tickets});stored.write(pending.command);await submit(false);
  }
  async function submit(check:boolean){
   const cmd=pending.command;if(!cmd||sending||o.busy()||!alive())return;
   sending=true;const uncertain=check?true:pending.sending();stored.write(pending.command);sync();
   try{
    const r=await o.api<Receipt|{found:false}>(check?'tickets/gas-locations/status':path,check?{requestId:cmd.operationId}:{...cmd.body,operationId:cmd.operationId});
    if(!alive())return;
    if('found' in r){status.textContent='No completed booking was found. The original request is available to retry.';return;}
    if(r.operationId!==cmd.operationId||r.agreements!==cmd.body.agreements||!r.purchaseTradeId||!r.saleTradeId||
       (r.purchaseTradeId===r.saleTradeId)!==(r.agreements==='one_agreement'))throw Error('The receipt does not identify the original confirmations.');
    pending.clear();stored.write(undefined);for(const e of editors)e.destroy();editors=[];started=edited=false;panels.replaceChildren();fields.hidden=work.hidden=true;
    status.textContent=r.agreements==='one_agreement'?'Bilateral trade booked.':'Both confirmations booked.';
    const open=(label:string,id:string)=>button(label,()=>void o.onBooked(id));
    receipt.append(open((r.purchaseTradeId===r.saleTradeId?'Trade ':'Purchase ')+r.purchaseReference,r.purchaseTradeId));
    if(r.saleTradeId!==r.purchaseTradeId)receipt.append(open('Sale '+r.saleReference,r.saleTradeId));
   }catch(error){if(alive()){pending.failed((error as {code?:string}).code,uncertain);stored.write(pending.command);status.textContent=String(error);}}
   finally{if(alive()){sending=false;sync();}}
  }
  if(saved){status.textContent='The original booking is awaiting confirmation.';fields.hidden=true;}sync();
 }
 return {root,begin:async()=>begin(),inspect:async()=>{throw Error('Open each booked gas trade from its Blotter reference.');},reset,sync:()=>refresh(),
  unsaved:()=>edited||started||!!pending.command,pending:()=>!!restore()};
}
