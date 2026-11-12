import {type RequestCommand} from './pending-command.js';

// Transport equality without dropping the microseconds through JavaScript Date.
export function utcMicrosecondText(value:string){
 const match=/^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(value);
 if(!match||match[2]?.length===7&&!match[2].endsWith('0'))throw Error('Invalid UTC instant');
 return match[1]+'.'+(match[2]??'').slice(0,6).padEnd(6,'0')+'Z';
}

// Presentation filtering only. The managed consumer independently validates every reference.
export function publicationIndicesFor<T extends {commodity:string;quote:string}>(publication:{indices:T[]},commodity:string,quote:string):T[]{
 return publication.indices.filter(index=>index.commodity===commodity&&index.quote===quote);
}
export function publicationVersion<T extends {key:{version:number}}>(definition:T):T{
 const next=structuredClone(definition);next.key.version++;return next;
}
export function restoreAuthoringRequest(value:unknown,path:string):RequestCommand|undefined{
 if(!value||typeof value!=='object'||Array.isArray(value))return;
 const saved=value as Record<string,unknown>;
 if(saved.path!==path||typeof saved.operationId!=='string'||!saved.operationId||!saved.body||typeof saved.body!=='object'||Array.isArray(saved.body))return;
 return {path,operationId:saved.operationId,body:structuredClone(saved.body as Record<string,unknown>),...(saved.uncertain===true?{uncertain:true}:{}),...(typeof saved.label==='string'?{label:saved.label}:{})};
}
export const authoringLabels:Record<string,string>={
 same_delivery_period_v1:'Match the same delivery period',explicit_contracts_v1:'Map individual contracts',
 exchange_final_v1:'Official futures settlement',published_index_v1:'Publication index',derived_inputs_v1:'Derived from inputs',
 published_release_v1:'Publication release · publisher timing',
 single_assessment_v1:'One final assessment per delivery period',require_final_v1:'Require the final price',
 forward_curve_no_basis_v1:'Use this forward curve before the fixing',
 gas_daily_explicit_v1:'Daily gas · explicit publisher calendar',gas_monthly_explicit_v1:'Monthly gas · explicit publisher calendar',
 crude_placeholder_v1:'Crude · placeholder',ngl_placeholder_v1:'NGL · placeholder',lng_placeholder_v1:'LNG · placeholder',power_placeholder_v1:'Power · placeholder',power_captured_hourly_v1:'Power · captured hourly LMPs'
};
export const authoringLabel=(value:string)=>authoringLabels[value]??value;
