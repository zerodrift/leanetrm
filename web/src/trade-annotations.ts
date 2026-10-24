import {displayInstant,publicText} from './market-view.js';
export type Trader={id:string;revision:number;code:string;name:string};
export type PartyChoice=Trader;
export type PartyCatalog={legalEntities:PartyChoice[];counterparties:PartyChoice[]};
export type Booking={tradeDate:string|null;legalEntityId:string|null;legalEntityRevision:number|null;counterpartyId:string|null;counterpartyRevision:number|null;legalEntity?:string|null;counterparty?:string|null};
export type Annotation=Booking&{booking?:Booking;traderId:string|null;traderRevision:number|null;memo:string|null;trader?:string|null;traderCode?:string|null;executedAt?:string;bookingFacts?:{implementation:string;labelCapturedAt:string}};
type Editable=Pick<Annotation,'memo'|'legalEntityId'|'legalEntityRevision'|'counterpartyId'|'counterpartyRevision'>;

/** The booking facts of a trade revision as a read view lists them: who, when and with whom, as the editors' header fields hold them. */
export function bookingRows(state:Annotation,zone:string):(readonly [string,string])[]{
 const booking=state.booking??state;
 return [['Trader',state.trader??'None'],['Trade date',booking.tradeDate??'Not recorded'],['Legal entity',booking.legalEntity??'None'],['Counterparty',booking.counterparty??'None'],
  ['Executed',state.executedAt?displayInstant(state.executedAt,zone):'Not recorded'],['Memo',state.memo??'None']];
}

/** The booking facts a revision's history lists: its trader, memo, trade date and parties, each said when it is absent. */
export function historyBooking(state:Annotation):(readonly [string,string])[]{
 const booking=state.booking??state;
 return [['Trader ID',state.traderCode??'None'],['Memo',state.memo??'None'],['Trade date',booking.tradeDate??'Not recorded'],['Legal entity',booking.legalEntity??'None'],['Counterparty',booking.counterparty??'None']];
}

// Shared trade header. Identity and dates are returned by the server, never read
// from controls into a command. Each product editor owns its instrument quantity.
export function tradeAnnotations(prefix:string,zone:()=>string){
 const host=document.getElementById(prefix+'-trade-terms')!;
 const title=document.createElement('h3');title.textContent='Trade terms';
 function input(name:string,label:string,readonly=false){const wrap=document.createElement('div'),caption=document.createElement('label'),field=document.createElement('input');field.id=prefix+'-'+name;caption.htmlFor=field.id;caption.textContent=label;field.type='text';field.readOnly=readonly;wrap.append(caption,field);return{wrap,field};}
 function select(name:string,label:string){const wrap=document.createElement('div'),caption=document.createElement('label'),field=document.createElement('select');field.id=prefix+'-'+name;caption.htmlFor=field.id;caption.textContent=label;field.required=true;wrap.append(caption,field);return{wrap,field};}
 function row(...nodes:HTMLElement[]){const line=document.createElement('div');line.className='trade-annotation-fields';line.append(...nodes);return line;}
 const book=select('book','Book'),trader=input('trader','Trader',true),date=input('trade-date','Trade date',true),executed=input('executed','Execution time',true),entity=select('legal-entity','Legal entity'),party=select('counterparty','Counterparty');
 const provenance=document.createElement('p');provenance.className='field-help';provenance.hidden=true;
 const memoLabel=document.createElement('label'),memo=document.createElement('textarea');memo.id=prefix+'-memo';memoLabel.htmlFor=memo.id;memoLabel.textContent='Memo';memo.maxLength=4000;memo.rows=2;
 host.replaceChildren(title,row(book.wrap,entity.wrap,party.wrap),row(trader.wrap,date.wrap,executed.wrap),provenance,memoLabel,memo);
 let initialMemo:string|null=null,executedAt:string|undefined;
 const key=(id:string,revision:number)=>JSON.stringify([id,revision]);
 function refresh(){executed.wrap.querySelector('label')!.textContent='Execution time · '+zone();executed.field.value=executedAt?displayInstant(executedAt,zone()):'Assigned when booked';}
 return{
  populate(actor:Trader,catalog:PartyCatalog,state?:Partial<Annotation>){
   trader.field.value=publicText(state?.trader??actor.name);date.field.value=state?.booking?.tradeDate??state?.tradeDate??'Assigned when booked';executedAt=state?.executedAt;refresh();
   provenance.hidden=state?.bookingFacts?.implementation!=='retained_commit_utc_v1';provenance.textContent=provenance.hidden?'':'Booking time recovered from the retained commit. Trader name captured during the data upgrade.';
   initialMemo=state?.memo??null;memo.value=initialMemo??'';
   const booking=state?.booking??state;
   function choices(select:HTMLSelectElement,rows:PartyChoice[],id:string|null|undefined,revision:number|null|undefined,label:string|null|undefined){
    select.replaceChildren(new Option('Choose…',''),...rows.map(p=>new Option(publicText(`${p.name} · ${p.code}`),key(p.id,p.revision))));
    if(id&&revision){const value=key(id,revision);if(!Array.from(select.options).some(o=>o.value===value))select.append(new Option(publicText(`${label??'Retained party'} · version ${revision}`),value));select.value=value;}
   }
   choices(entity.field,catalog.legalEntities,booking?.legalEntityId,booking?.legalEntityRevision,booking?.legalEntity);
   choices(party.field,catalog.counterparties,booking?.counterpartyId,booking?.counterpartyRevision,booking?.counterparty);
  },
  read():Editable{const [legalEntityId,legalEntityRevision]=entity.field.value?JSON.parse(entity.field.value):[null,null];const [counterpartyId,counterpartyRevision]=party.field.value?JSON.parse(party.field.value):[null,null];return{legalEntityId,legalEntityRevision,counterpartyId,counterpartyRevision,memo:memo.value===(initialMemo??'')?initialMemo:memo.value};},
  refresh,
  reset(){entity.field.replaceChildren();party.field.replaceChildren();trader.field.value='';date.field.value='';executedAt=undefined;refresh();memo.value='';initialMemo=null;},
 };
}
