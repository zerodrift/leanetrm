import {renderRegularTable} from './regular-table.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Party={id:string;name:string;revision:number};
type Agreement={id:string;code:string;name:string;revision:number;agreement_type:string;products:string[];status:string;effective_at:string;valid_to:string|null;procedure:string;provisions?:string;filename:string|null;legal_entity_id:string;counterparty_id:string};
type Credit={id:string;product:string;legal_entity_id:string;counterparty_id:string;revision:number;effective_at:string;valid_to:string|null;status:string;reason?:string};
type Confirmation={revision:number;status:string;reference:string;evidence:string;effective_at:string};
// carried_from names the earlier revision whose commercial terms, and so confirmations, this revision carries unchanged.
type Trade={trade_id:string;revision:number;carried_from?:number|null;product:string;agreement_id:string;executed_at:string;confirmations:Confirmation[]};
type Directory={products:string[];entities:Party[];parties:Party[];agreements:Agreement[];clearances:Credit[];trades:Trade[];canEdit:boolean;canCredit:boolean;canDownload:boolean;canConfirm:boolean;working:boolean};
type Options={api:Api;act:(work:()=>Promise<void>)=>Promise<void>;source:()=>string;busy:()=>boolean};

function element<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string){
 const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;
}
function picker<T>(list:T[],value:(x:T)=>string,label:(x:T)=>string){
 const select=element('select');select.required=true;
 for(const row of list){const option=element('option',label(row));option.value=value(row);select.append(option);}
 return select;
}
const labelText=(s:string)=>s.replaceAll('_',' ');
function input(type='text',value=''){const node=element('input');node.type=type;node.value=value;node.required=true;return node;}
function labelled(label:string,field:HTMLElement){const wrapper=element('label',label);wrapper.append(field);return wrapper;}
function action(label:string,click:()=>void){const b=element('button',label);b.type='button';b.className='secondary';b.onclick=click;return b;}
function dataUrl(file:File):Promise<string>{
 return new Promise((resolve,reject)=>{const reader=new FileReader();reader.onerror=()=>reject(Error('The PDF could not be read.'));
  reader.onload=()=>resolve(String(reader.result).split(',')[1]);reader.readAsDataURL(file);});
}

export function setupBilateralDesk(o:Options){
 const root=document.getElementById('bilateral-desk')!,editor=element('div'),summary=element('div'),message=element('p');
 let generation=0,dirty=false,retained:{key:string;operationId:string}|undefined;
 root.append(summary,editor,message);message.setAttribute('role','status');
 const reset=()=>{generation++;dirty=false;retained=undefined;summary.replaceChildren();editor.replaceChildren();message.textContent='';};
 async function load(){
  const request=++generation,snapshotId=o.source();
  const d=await o.api<Directory>('desk/directory',{snapshotId});if(request!==generation||o.source()!==snapshotId)return;
  dirty=false;summary.replaceChildren();editor.replaceChildren();
  const entityName=(id:string)=>d.entities.find(x=>x.id===id)?.name??id,partyName=(id:string)=>d.parties.find(x=>x.id===id)?.name??id;
  const table=(title:string,headers:string[],rows:(string|HTMLElement)[][])=>{
   summary.append(element('h3',title));const node=element('div');summary.append(node);
   renderRegularTable(node,'desk-'+title.replaceAll(' ','-'),snapshotId,headers,rows,[],o.busy);
  };
  async function download(a:Agreement){
   const file=await o.api<{filename:string;pdf:string}>('desk/document',{snapshotId,id:a.id});
   if(request!==generation)return;
   const bytes=Uint8Array.from(atob(file.pdf),c=>c.charCodeAt(0));const url=URL.createObjectURL(new Blob([bytes],{type:'application/pdf'}));
   const link=element('a');link.href=url;link.download=file.filename;link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
  }
  table('Legal agreements',['Agreement','Entity / counterparty','Products','Revision','Status','Effective UTC','Expires','Document',''],d.agreements.map(a=>[
   a.name,entityName(a.legal_entity_id)+' / '+partyName(a.counterparty_id),a.products.map(labelText).join(', '),String(a.revision),a.status,a.effective_at.slice(0,10),a.valid_to??'Open',
   d.canDownload&&a.filename?action('Download PDF',()=>void o.act(()=>download(a))):a.filename?'Filed':'—',
   d.working&&d.canEdit?action('Revise',()=>form('agreement',a)):'']));
  table('Credit decisions',['Entity','Counterparty','Product','Revision','Status','Effective UTC','Expires','Reason'],d.clearances.map(c=>[
   entityName(c.legal_entity_id),partyName(c.counterparty_id),labelText(c.product),String(c.revision),c.status,c.effective_at.slice(0,10),c.valid_to??'Open',c.reason??'—']));
  table('Commercial confirmations',['Trade / revision','Commercial terms','Product','Agreement','Status','Confirmation reference','Recorded UTC',''],d.trades.flatMap(t=>
   (t.confirmations.length?t.confirmations:[undefined]).map(c=>[t.trade_id+' / '+t.revision,
   t.carried_from==null?'Set by this revision':'Carried from revision '+t.carried_from,labelText(t.product),d.agreements.find(a=>a.id===t.agreement_id)?.name??'Filed',
   c?.status??'Awaiting confirmation',c?.reference??'—',c?.effective_at??'—',d.working&&d.canConfirm?action('Record',()=>form('confirmation',undefined,t)):''])));
  if(!d.working)return;
  if(d.canEdit)summary.append(action('New agreement',()=>form('agreement')));
  if(d.canCredit)summary.append(action('Record credit decision',()=>form('credit')));
  function form(kind:'agreement'|'credit'|'confirmation',previous?:Agreement,trade?:Trade){
   const f=element('form');f.className='card';f.append(element('h3',kind==='agreement'?'Agreement revision':kind==='credit'?'Counterparty credit decision':'Commercial confirmation'));
   const fields=element('div');fields.className='schedule-fields';f.append(fields);
   const add=<T extends HTMLElement>(label:string,node:T)=>{fields.append(labelled(label,node));return node;};
   const entities=picker(d.entities,x=>x.id,x=>x.name),parties=picker(d.parties,x=>x.id,x=>x.name);
   if(kind!=='confirmation'){add('Legal entity',entities);add('Counterparty',parties);}
   if(previous){entities.value=previous.legal_entity_id;parties.value=previous.counterparty_id;entities.disabled=parties.disabled=true;}
   const product=picker(d.products,x=>x,labelText);if(kind!=='confirmation')add(kind==='agreement'?'Covered products':'Product',product);
   if(kind==='agreement'){product.multiple=true;if(previous)for(const option of product.options)option.selected=previous.products.includes(option.value);}
   const choices=kind==='agreement'?['draft','executed','terminated']:kind==='credit'?['approved','hold','expired']:['sent','matched','disputed'];
   const state=add('Status',picker(choices,x=>x,x=>x));if(previous)state.value=previous.status;
   const date=add('Effective time (UTC)',input('datetime-local',new Date().toISOString().slice(0,19)));date.step='1';
   const until=input('date',previous?.valid_to??'');until.required=false;if(kind!=='confirmation')add('Expires on (exclusive)',until);
   const code=input('text',previous?.code),name=input('text',previous?.name),type=input('text',previous?.agreement_type);
   const procedure=picker(['written','oral','signed_contract'],x=>x,labelText),notes=element('textarea'),pdf=input('file');pdf.accept='application/pdf';pdf.required=false;
   if(kind==='agreement'){add('Agreement code',code);code.disabled=!!previous;add('Name',name);add('Agreement type',type);add('Confirmation procedure',procedure);
    procedure.value=previous?.procedure??'written';add('Special provisions',notes);notes.value=previous?.provisions??'';add('Executed PDF (up to 8 MiB)',pdf);}
   const confirmed=trade&&(trade.carried_from??trade.revision);
   const reference=input();if(kind==='confirmation'){f.prepend(element('p',trade!.trade_id+' / revision '+confirmed));add('Counterparty reference',reference);}
   if(kind!=='agreement'){notes.required=true;add(kind==='credit'?'Decision reason':'Confirmation evidence',notes);}
   const submit=element('button','Save revision');submit.type='submit';f.append(submit,action('Cancel',()=>{dirty=false;editor.replaceChildren();}));
   editor.replaceChildren(f);f.scrollIntoView({block:'nearest'});f.oninput=()=>{dirty=true;};
   f.onsubmit=e=>{e.preventDefault();void o.act(async()=>{
    const record:Record<string,unknown>={kind,status:state.value,effectiveAt:new Date(date.value+'Z').toISOString()};
    if(kind!=='confirmation')Object.assign(record,{legalEntityId:entities.value,legalEntityRevision:d.entities.find(x=>x.id===entities.value)!.revision,
     counterpartyId:parties.value,counterpartyRevision:d.parties.find(x=>x.id===parties.value)!.revision,validTo:until.value||null});
    if(kind==='agreement'){
     const file=pdf.files?.[0];if(file&&file.size>8388608)throw Error('Choose a PDF of at most 8 MiB.');
     if(!file&&state.value==='executed')throw Error('Attach the signed PDF for this executed revision.');
     Object.assign(record,{code:code.value,name:name.value,agreementType:type.value,procedure:procedure.value,provisions:notes.value,
      products:Array.from(product.selectedOptions,x=>x.value),filename:file?.name??null,pdf:file?await dataUrl(file):null,
      expectedRevision:Math.max(0,...d.agreements.filter(x=>x.code===code.value).map(x=>x.revision))});
    }else if(kind==='credit')Object.assign(record,{product:product.value,reason:notes.value,expectedRevision:Math.max(0,...d.clearances.filter(x=>
     x.product===product.value&&x.legal_entity_id===entities.value&&x.counterparty_id===parties.value).map(x=>x.revision))});
    else Object.assign(record,{tradeId:trade!.trade_id,tradeRevision:confirmed,reference:reference.value,evidence:notes.value,expectedRevision:Math.max(0,...trade!.confirmations.map(x=>x.revision))});
    const key=JSON.stringify(record);if(retained?.key!==key)retained={key,operationId:crypto.randomUUID()};
    const result=await o.api<{revision:number}>('desk/'+kind,{operationId:retained.operationId,record});
    if(request!==generation)return;retained=undefined;await load();message.textContent='Saved revision '+result.revision+'.';
   });};
  }
 }
 return {load,reset,edited:()=>dirty};
}

type BookingConfirmation='purchase'|'sale'|'bilateral';
type AgreementRefusal={code?:string;details?:{confirmation?:BookingConfirmation;agreementSelection?:{operationId:string;choices:Party[]}}};

export async function submitWithBookingAgreements<T>(submit:()=>Promise<T>,record:(operationId:string,agreementId:string)=>Promise<unknown>,
 select:typeof selectBookingAgreement,checkScope:()=>void,maxChoices:number):Promise<T>{
 const chosen=new Set<string>();
 for(;;){
  checkScope();
  try{return await submit();}
  catch(error){
   const refusal=error as AgreementRefusal,selection=refusal.details?.agreementSelection;
   if(refusal.code!=='desk_agreement_ambiguous'||!selection||chosen.has(selection.operationId)||chosen.size>=maxChoices)throw error;
   checkScope();
   const agreement=await select(selection.choices,refusal.details?.confirmation);
   checkScope();
   await record(selection.operationId,agreement);
   chosen.add(selection.operationId);
  }
 }
}

export async function selectBookingAgreement(choices:Party[],confirmation?:BookingConfirmation):Promise<string>{
 const dialog=element('dialog'),form=element('form');form.method='dialog';const select=picker(choices,a=>a.id,a=>a.name+' · revision '+a.revision);
 const title={purchase:'Purchase confirmation',sale:'Sale confirmation',bilateral:'Bilateral confirmation'};
 const heading=element('h2',confirmation?title[confirmation]:'Booking agreement');heading.id='booking-agreement-title';
 dialog.setAttribute('aria-labelledby',heading.id);select.prepend(new Option('Choose an agreement','',true,true));
 const ok=element('button','Use agreement');ok.value='accept';ok.disabled=true;select.onchange=()=>{ok.disabled=!select.value;};
 const cancel=element('button','Cancel');cancel.value='cancel';cancel.formNoValidate=true;
 form.append(heading,labelled('Applicable executed agreement',select),ok,cancel);
 dialog.append(form);document.body.append(dialog);dialog.showModal();
 return new Promise((resolve,reject)=>{dialog.onclose=()=>{dialog.remove();if(dialog.returnValue==='accept')resolve(select.value);else reject(Object.assign(Error('Booking cancelled.'),{code:'desk_selection_cancelled'}));};});
}
