import type {RequestCommand} from './pending-command.js';
export const crudeConvention='physical_crude_fixed_volume_v1';
export type CrudeProfile={code:string;quantityBasis:string;tolerance:string;discounting:boolean;label?:string};
export type CrudeFields={deliveryFrom:string;deliveryTo:string;quantity:string;direction:string;fixedPrice:string;considerationDirection:string;paymentAt:string;feeMode:string;handlingFee:string;handlingFeeDirection:string};
const utc=(value:string)=>{if(!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/.test(value))throw Error('Enter an explicit UTC instant.');return value+(value.length===16?':00Z':'Z');};
// Presentation binding only: native operations validate the selected reference,
// delivery geometry, units, price convention and all numerical behavior.
export function crudeRequest(context:Record<string,unknown>,header:Record<string,unknown>,assignment:{code:string;definitionRevisionId:string}|undefined,profile:CrudeProfile|undefined,fields:CrudeFields):Record<string,unknown>{
 if(!assignment)throw Error('Choose the exact physical delivery reference.');
 if(!profile)throw Error('Choose an explicit contract profile.');
 if(!['none','per_unit'].includes(fields.feeMode))throw Error('Choose whether a handling fee applies.');
 if(fields.feeMode==='per_unit'&&(!fields.handlingFee||!['pay','receive'].includes(fields.handlingFeeDirection)))throw Error('Enter the fee and its independent direction.');
 return{...context,...header,convention:crudeConvention,knowledgeMode:'current',definitionRevisionId:assignment.definitionRevisionId,assignmentCode:assignment.code,
  profile:profile.code,quantityBasis:profile.quantityBasis,tolerance:profile.tolerance,discounting:profile.discounting,
  deliveryFrom:utc(fields.deliveryFrom),deliveryTo:utc(fields.deliveryTo),quantity:fields.quantity,direction:fields.direction,
  fixedPrice:fields.fixedPrice,considerationDirection:fields.considerationDirection,paymentAt:utc(fields.paymentAt),
  handlingFee:fields.feeMode==='per_unit'?fields.handlingFee:null,handlingFeeDirection:fields.feeMode==='per_unit'?fields.handlingFeeDirection:null};
}
export function restoreCrudeCommand(value:unknown):RequestCommand|undefined{
 if(!value||typeof value!=='object'||Array.isArray(value))return;
 const v=value as Record<string,unknown>;
 if(v.path!=='crude/book'||typeof v.operationId!=='string'||!v.operationId||!v.body||typeof v.body!=='object'||Array.isArray(v.body))return;
 return{path:'crude/book',operationId:v.operationId,body:structuredClone(v.body as Record<string,unknown>),uncertain:true};
}
