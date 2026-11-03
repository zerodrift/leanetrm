import {resolvedUtcInstant} from './user-time.js';
import type {RequestCommand} from './pending-command.js';
export const lngConvention='physical_lng_cargo_v1';
export type LngProfile={code:string;quantityBasis:string;tolerance:string;discounting:boolean;label?:string};
export type LngFields={loadFrom:string;loadTo:string;dischargeFrom:string;dischargeTo:string;paymentAt:string;quantity:string;quantityCode:string;quantityUnit:string;conversionMode:string;conversionCode:string;direction:string;considerationDirection:string;fixedPrice:string};
export function lngRequest(context:Record<string,unknown>,header:Record<string,unknown>,physical:Record<string,unknown>,incoterms:{edition:string;rule:string;namedPlace:string},profile:LngProfile|undefined,fields:LngFields):Record<string,unknown>{
 if(!profile)throw Error('Choose an explicit LNG contract profile.');
 if(!['direct','factor'].includes(fields.conversionMode)||fields.conversionMode==='factor'&&!fields.conversionCode)throw Error('Choose the quantity conversion terms explicitly.');
 if(!incoterms.edition||!incoterms.rule||!incoterms.namedPlace)throw Error('Choose each Incoterms reference.');
 const {conversionMode,conversionCode,...terms}=fields;
 return{...context,...header,...terms,physical,incoterms,convention:lngConvention,knowledgeMode:'current',profile:profile.code,quantityBasis:profile.quantityBasis,tolerance:profile.tolerance,discounting:profile.discounting,conversionCode:conversionMode==='factor'?conversionCode:null,
  ...Object.fromEntries(['loadFrom','loadTo','dischargeFrom','dischargeTo','paymentAt'].map(k=>[k,resolvedUtcInstant(fields[k as keyof LngFields])]))};
}
export function restoreLngCommand(value:unknown):RequestCommand|undefined{
 if(!value||typeof value!=='object'||Array.isArray(value))return;
 const v=value as Record<string,unknown>;
 if(v.path!=='lng/book'||typeof v.operationId!=='string'||!v.operationId||!v.body||typeof v.body!=='object'||Array.isArray(v.body))return;
 return{path:'lng/book',operationId:v.operationId,body:structuredClone(v.body as Record<string,unknown>),uncertain:true};
}
