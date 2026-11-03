import {resolvedUtcInstant as instant} from './user-time.js';
import {amountText} from './amount-display.js';
import {displayExactInstant} from './market-view.js';
import type {RequestCommand} from './pending-command.js';
export const crudeConvention='physical_crude_fixed_volume_v1';
export type CrudeProfile={code:string;quantityBasis:string;tolerance:string;discounting:boolean;label?:string};
export type CrudeFields={deliveryFrom:string;deliveryTo:string;quantity:string;direction:string;fixedPrice:string;considerationDirection:string;paymentAt:string;feeMode:string;handlingFee:string;handlingFeeDirection:string};

// Presentation binding only: native operations validate the selected reference,
// delivery geometry, units, price convention and all numerical behavior.
export function crudeRequest(context:Record<string,unknown>,header:Record<string,unknown>,assignment:{code:string;definitionRevisionId:string;crudeSetupId?:string|null;physical?:unknown}|undefined,profile:CrudeProfile|undefined,fields:CrudeFields):Record<string,unknown>{
 if(!assignment)throw Error('Choose the exact physical delivery reference.');
 if(!profile)throw Error('Choose an explicit contract profile.');
 if(!['none','per_unit'].includes(fields.feeMode))throw Error('Choose whether a handling fee applies.');
 if(fields.feeMode==='per_unit'&&(!fields.handlingFee||!['pay','receive'].includes(fields.handlingFeeDirection)))throw Error('Enter the fee and its independent direction.');
 return{...context,...header,...(assignment.physical?{physical:assignment.physical}:{}),...(assignment.crudeSetupId?{crudeSetupId:assignment.crudeSetupId}:{}),convention:crudeConvention,knowledgeMode:'current',definitionRevisionId:assignment.definitionRevisionId,assignmentCode:assignment.code,
  profile:profile.code,quantityBasis:profile.quantityBasis,tolerance:profile.tolerance,discounting:profile.discounting,
  deliveryFrom:instant(fields.deliveryFrom),deliveryTo:instant(fields.deliveryTo),quantity:fields.quantity,direction:fields.direction,
  fixedPrice:fields.fixedPrice,considerationDirection:fields.considerationDirection,paymentAt:instant(fields.paymentAt),
  handlingFee:fields.feeMode==='per_unit'?fields.handlingFee:null,handlingFeeDirection:fields.feeMode==='per_unit'?fields.handlingFeeDirection:null};
}
export function restoreCrudeCommand(value:unknown):RequestCommand|undefined{
 if(!value||typeof value!=='object'||Array.isArray(value))return;
 const v=value as Record<string,unknown>;
 if(v.path!=='crude/book'||typeof v.operationId!=='string'||!v.operationId||!v.body||typeof v.body!=='object'||Array.isArray(v.body))return;
 return{path:'crude/book',operationId:v.operationId,body:structuredClone(v.body as Record<string,unknown>),uncertain:true};
}
// What a crude preview or booked valuation shows: its delivery, volume, pricing source and contract terms by name, each side's signed PV, and amounts at
// the display precision. A direction or side the contract does not name is refused rather than shown as its code.
export type CrudeValue={presentValue:string;currency:string;quantity:string;quantityUnit:string;deliveryFrom:string;deliveryTo:string;pricingSource:string;priceUnit:string;profileName:string;sides:{role:string;direction:string;presentValue:string}[]};
const physicalDirections:Record<string,string>={receive:'Receive crude',deliver:'Deliver crude'},cashDirections:Record<string,string>={pay:'Pay consideration',receive:'Receive consideration'},
 feeDirections:Record<string,string>={pay:'Pay fee',receive:'Receive fee'},sideRoles:Record<string,string>={delivery:'Delivery',consideration:'Consideration',handling_fee:'Handling fee'},
 sideDirections:Record<string,string>={receive:'Receive',deliver:'Deliver',pay:'Pay'};
const named=(names:Record<string,string>,value:unknown)=>{const name=names[String(value)];if(!name)throw Error(`The crude preview names an unknown term: ${String(value)}.`);return name;};
export function crudePreview(value:CrudeValue,terms:Record<string,unknown>|undefined,zone:string){
 const money=(v:string)=>amountText(v,'money');
 return{total:`Contractual present value: ${money(value.presentValue)} ${value.currency}`,
  terms:[['Delivery window',`${displayExactInstant(value.deliveryFrom,zone)} to ${displayExactInstant(value.deliveryTo,zone)} · exclusive`],['Contract volume',`${amountText(value.quantity,'quantity')} ${value.quantityUnit}`],
   ...(terms?[['Pricing source',value.pricingSource],['Physical direction',named(physicalDirections,terms.direction)],['Consideration direction',named(cashDirections,terms.considerationDirection)],
    ['Fixed price',`${String(terms.fixedPrice)} ${value.priceUnit}`],['Payment · '+zone,displayExactInstant(String(terms.paymentAt),zone)],
    ['Handling fee',terms.handlingFee===null?'None':`${String(terms.handlingFee)} ${value.priceUnit} · ${named(feeDirections,terms.handlingFeeDirection)}`],['Profile',value.profileName]]:[])] as [string,string][],
  columns:['Side','Direction',`Signed PV · ${value.currency}`],
  sides:value.sides.map(s=>[named(sideRoles,s.role),named(sideDirections,s.direction),money(s.presentValue)])};
}
// Delivery months follow the delivery clock while times are entered in the display zone; when the two differ, say so beside the dates.
export function crudeClockHint(clock:string|undefined,zone:string){
 return !clock||clock===zone?'':`Delivery months start at midnight on the ${clock} delivery clock. Enter times here in ${zone}.`;
}
