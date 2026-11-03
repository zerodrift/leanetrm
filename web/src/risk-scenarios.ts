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

const rejectedRiskAdmissions=new Set(['risk_historical_input','risk_statistical_history','risk_historical_scope','risk_historical_policy','risk_historical_samples','risk_historical_exposures','risk_historical_comparison','risk_historical_capacity','request_format','request_too_large','request_token','access_denied','risk_context','risk_job_context','risk_job_conflict','risk_factor','risk_bump','risk_stress','scenario_shift','scenario_coordinate','scenario_target','scenario_bucket','unit_mismatch','risk_position','risk_convention','risk_policy','risk_detail','numeric_format','numeric_range','futures_context','portfolio_context','portfolio_policy','swap_risk_model','blotter_view','risk_joint_contract','risk_joint_input','risk_scenario_capacity','risk_size','risk_sensitivity','risk_axis_input','risk_job_run','risk_parametric_policy','risk_parametric_scope','risk_parametric_delta','risk_parametric_capacity','risk_parametric_input','risk_history_capture','risk_capture_capacity','risk_history_manifest','risk_history_policy','risk_history_calendar','risk_history_clock','risk_history_capacity','risk_history_window']);
export function knownRiskRejection(code:string|undefined):boolean{return !!code&&rejectedRiskAdmissions.has(code);}
