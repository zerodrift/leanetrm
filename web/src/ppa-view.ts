import type {Catalog} from './ppa-contract.js';
import {timeLabels} from './market-view.js';
import {PendingCommand,type RequestCommand} from './pending-command.js';
export type Exact={numerator:string;denominator:string};
export const exactText=(value:Exact|null|undefined):string=>value==null?'Unavailable':value.denominator==='1'?value.numerator:`${value.numerator}/${value.denominator}`;
export type Measures={valueExcludingOverdue:Exact;completePv:Exact|null;unavailableReason:string|null;overdueKnownBalance:Exact;overdueEstimated:Exact;classes?:{kind:string;amount:Exact}[];coverages?:{kind:string;amount:Exact}[]};
export function measureRows(m:Measures):string[][]{return [
 ['Complete PV',m.completePv===null?`Unavailable · ${m.unavailableReason}`:exactText(m.completePv)],
 ['Value excl. overdue cash (undue PV + holdings)',exactText(m.valueExcludingOverdue)],
 ['Overdue balance (known)',exactText(m.overdueKnownBalance)],['Overdue (estimated)',exactText(m.overdueEstimated)]];}
export type Reference={revision?:{catalogId:string;revisionId:string};code:string};
export function quantityLabel(reference:Reference,catalog:Pick<Catalog,'reference'|'references'>):string{
 if(reference.revision?.catalogId!==catalog.reference.catalogId||reference.revision?.revisionId!==catalog.reference.revisionId)throw Error('The displayed quantity requires a different exact reference revision.');
 const spec=catalog.references.quantitySpecs.find(s=>s.code===reference.code),unit=catalog.references.units.find(u=>u.code===spec?.unit_code);
 if(!spec||!unit||!unit.symbol)throw Error('The exact quantity unit is unavailable in this catalog.');
 return `${unit.symbol} [${reference.code}]`;
}
export type InspectionRow={interval:{start:string;end:string};quantity:Exact;quantitySpecification?:Reference;currency?:{code:string};amount:Exact;presentValue?:Exact|null;kind:string;quantityState:string;economicClass:string;coverage:string;valueAt:string;priceComponents?:{price:Exact&{unit?:{currency:{code:string};quantity:Reference;multiplier:string}};state:string}[]};
export function inspectionRows(rows:InspectionRow[],zone:string,label:(reference:Reference)=>string=r=>r.code):string[][]{
 const labels=timeLabels(rows.flatMap(r=>[r.interval.start,r.interval.end]),zone);
 return rows.map((r,i)=>[labels[i*2],labels[i*2+1],r.kind,exactText(r.quantity)+(r.quantitySpecification?' '+label(r.quantitySpecification):''),r.quantityState,(r.priceComponents??[]).map(p=>`${exactText(p.price)}${p.price.unit?` ${p.price.unit.currency.code.toUpperCase()} / ${label(p.price.unit.quantity)} (quote multiplier ${p.price.unit.multiplier})`:''} · ${p.state}`).join('; '),exactText(r.amount)+(r.currency?' '+r.currency.code.toUpperCase():''),exactText(r.presentValue)+(r.currency?' '+r.currency.code.toUpperCase():''),r.economicClass,r.coverage,r.valueAt]);
}
export function refusalText(error:{code?:string;message?:string;scope?:unknown;selectedBundle?:unknown;affectedBindings?:unknown}):string{
 return [error.code,error.message,error.scope===undefined?'':`Requested scope: ${JSON.stringify(error.scope)}`,error.selectedBundle===undefined?'':`Selected revisions: ${JSON.stringify(error.selectedBundle)}`,error.affectedBindings===undefined?'':`Affected bindings: ${JSON.stringify(error.affectedBindings)}`].filter(Boolean).join('\n');
}
// A reviewed body is copied once. An uncertain write cannot be changed or silently replaced.
type CommandStorage=Pick<Storage,'getItem'|'setItem'|'removeItem'>;
export class ReviewedPpaCommand{
 private readonly pending=new PendingCommand(code=>!!code&&(code.startsWith('ppa_')||['access_denied','request_too_large','trade_party'].includes(code)));
 constructor(private readonly storage?:CommandStorage,private readonly key=''){
  try{const saved=JSON.parse(storage?.getItem(key)??'null');if(saved&&['ppa/book','ppa/amend','ppa/terminate'].includes(saved.path)&&typeof saved.operationId==='string'&&saved.body&&typeof saved.body==='object'&&!Array.isArray(saved.body))this.pending.command={...saved,uncertain:true};}catch{/* Browser drafts never grant authority. */}
 }
 private retain(){try{if(this.command)this.storage?.setItem(this.key,JSON.stringify(this.command));else this.storage?.removeItem(this.key);}catch{/* The exact request remains available in memory for this visit. */}}
 get command():RequestCommand|undefined{return this.pending.command;}
 review(path:string,body:Record<string,unknown>,id?:()=>string){return this.pending.begin(path,body,id);}
 invalidate(){if(!this.command?.uncertain)this.pending.clear();this.retain();}
 sending(){const prior=this.pending.sending();this.retain();return prior;}
 failed(code:string|undefined,prior:boolean){this.pending.failed(code,prior);this.retain();}
 complete(){this.pending.clear();this.retain();}
}

// Editing a shared form never silently replaces a retained side's independent source.
export function componentQuantity<T>(selected:T,retained:T|undefined,choice:string):T{
 if(choice==='selected')return structuredClone(selected);
 if(choice==='retained'&&retained!==undefined)return structuredClone(retained);
 throw Error('Choose the retained component quantity or the explicitly selected delivery quantity.');
}
