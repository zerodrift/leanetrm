/** Presentation transport only. Targets, units and containment policies are
 * offered by the backend; membership resolution and shock arithmetic stay there. */
export type ScenarioTarget={id:string;label:string;coordinate:string;kind:string;unit:string;priceUnit:unknown;factor:{coordinate:string;target:Record<string,unknown>}};
export type ScenarioShift={factor:ScenarioTarget['factor'];size:string;convention:string};
export function scenarioShift(target:ScenarioTarget|undefined,size:string,convention:string,bucket?:{from:string;to:string}):ScenarioShift{
 if(!target)throw Error('Choose an available scenario target.');
 const factor=structuredClone(target.factor);
 if(target.kind==='delivery_bucket'){
  if(!bucket?.from||!bucket.to)throw Error('Enter the delivery start and exclusive end for this bucket.');
  factor.target={...factor.target,from:bucket.from,to:bucket.to};
 }
 return {factor,size,convention};
}

const rejectedRiskAdmissions=new Set(['request_format','request_too_large','request_token','access_denied','risk_context','risk_job_context','risk_job_conflict','risk_factor','risk_bump','risk_stress','scenario_shift','scenario_coordinate','scenario_target','scenario_bucket','unit_mismatch','risk_position','risk_convention','risk_policy','risk_detail','numeric_format','numeric_range','futures_context','portfolio_context','portfolio_policy','swap_risk_model','blotter_view']);
export function knownRiskRejection(code:string|undefined):boolean{return !!code&&rejectedRiskAdmissions.has(code);}
