// Plain words for the positions a close cannot value (D220). The backend decides which positions
// those are and why; a status it has not defined is refused rather than shown as a value.
export type ValuationReason='discount_coverage'|'discount_curve'|'option_settlement_missing'|'option_expired'|'option_underlying_missing'|'option_calibration_nonconvergent'|'option_settlement_below_intrinsic'|'option_settlement_above_bound'|'option_calibration_fallback_undeclared'|'source_expired'|'missing_observation'|'index_fixing_missing'|'delivery_mark_final_settlement'|'other_definition_versions'|'unpriced_delivery'|'needs_rebooking'|'gas_delivery_lifecycle'|'gas_market_context'|'gas_imbalance_transfer'|'exercise_marks'|'exercise_determination'|'exercise_conflict'|'exercise_model_scope'|'holder_choice_inputs'|'physical_gas_risk_unavailable'|'gas_custody_acceptance_window'|'gas_custody_reconciliation'|'gas_park_loan_reconciliation'|'gas_balance_fee_advance_window'|'crude_delivery_lifecycle'|'crude_lifecycle'|'crude_market_context'|'lng_delivery_lifecycle'|'lng_market_context'|
 'financial_state_unavailable'|'financial_state_future'|'rights_mark_required'|'rights_mark_future'|'rights_mark_corrected'|'expiry_transition_required'|'gas_transport_financial_unpriced'|'heat_rate_call_marks'|'heat_rate_call_exercise'|'spread_option_inputs'|'gas_storage_market_context'|'gas_storage_plan'|'gas_storage_started'|'gas_storage_ratchet'|'gas_storage_inventory'|'gas_storage_terminal'|'gas_storage_direction'|'market_inputs_missing';
// An expired source is a future's own contract, awaiting settlement, but for a swap an input of the
// composite or custom curve it is projected from. A PPA booked under other reference or definition
// versions than the close is not valued at it (D246), nor is one whose delivery the close has no price for (D252).
const reasons:Readonly<Record<string,Readonly<Partial<Record<ValuationReason,string>>>>>={
  future:{source_expired:'Contract expired · awaiting settlement',missing_observation:'Missing price at this close',market_inputs_missing:'Booked market inputs are unavailable at this close'},
  financial_swap:{source_expired:'Past marking cutoff',missing_observation:'Missing price at this close',index_fixing_missing:'Missing published index fixing'},
  heat_rate:{source_expired:'Past marking cutoff',missing_observation:'Missing price at this close',index_fixing_missing:'Missing published index fixing'},
  physical_lng:{lng_delivery_lifecycle:'Loading has started; operational actuals valuation is not supported',source_expired:'Past marking cutoff',missing_observation:'Missing LNG price at this close',index_fixing_missing:'Missing published LNG fixing',lng_market_context:'LNG pricing inputs unavailable at this close'},
  physical_crude:{crude_delivery_lifecycle:'Delivery has started; custody and actuals valuation are not supported',crude_lifecycle:'Delivery has started; custody and actuals valuation are not supported',source_expired:'Past marking cutoff',missing_observation:'Missing crude price at this close',index_fixing_missing:'Missing published crude fixing',crude_market_context:'Crude pricing inputs unavailable at this close'},
  physical_gas:{gas_imbalance_transfer:'Month-to-date cash-out cannot include accepted transfers',exercise_conflict:'Recorded delivery or payment conflicts with the exercise decision',exercise_marks:'Required exercise marks are unavailable',exercise_determination:'An elapsed exercise group needs a recorded election',exercise_model_scope:'The exercise group is outside the selected model',holder_choice_inputs:'The exercise model inputs are outside its supported range',gas_delivery_lifecycle:'Delivery has started; operational valuation is not yet supported',source_expired:'Past marking cutoff',missing_observation:'Missing gas price at this close',index_fixing_missing:'Missing published gas fixing',gas_market_context:'Gas pricing inputs unavailable at this close',delivery_mark_final_settlement:'Missing final settlement of its expired delivery contract',physical_gas_risk_unavailable:'Gas scenario risk is not supported; contractual PV remains available'},
  physical_gas_park_loan:{source_expired:'Past principal marking cutoff',missing_observation:'Missing principal mark at this close',index_fixing_missing:'Missing published principal fixing',delivery_mark_final_settlement:'Missing final settlement of the principal marking contract',gas_park_loan_reconciliation:'Reconciliation required after a linked source correction',gas_balance_fee_advance_window:'Advance window closed without accepted performance; remaining plan value unavailable',discount_curve:'Independent fee discount inputs are unavailable at this close'},
  physical_gas_park_custody:{gas_custody_acceptance_window:'Acceptance window closed without accepted custody; service plan value unavailable',gas_custody_reconciliation:'Reconciliation required after a custody observation correction',discount_curve:'Independent fee discount inputs are unavailable at this close',discount_coverage:'Fee payment date outside the discount curve'},
  physical_gas_transport:{financial_state_unavailable:'Financial state unavailable in this view',financial_state_future:'Latest financial state is not yet effective in this view',rights_mark_required:'No current whole-rights mark has been adopted',rights_mark_future:'Latest whole-rights mark is not yet effective in this view',rights_mark_corrected:'The adopted whole-rights observation was corrected; explicit re-adoption is required',expiry_transition_required:'Capacity window ended; explicit rights expiry is required',gas_transport_financial_unpriced:'Whole-rights valuation inputs are unavailable at this close',discount_curve:'Independent fee discount inputs are unavailable at this close',discount_coverage:'Fee payment date outside the discount curve',missing_observation:'Missing adopted whole-rights observation at this close'},
  futures_option:{option_settlement_missing:'Missing option settlement at this close',option_expired:'Expired · awaiting its recorded expiry',missing_observation:'Missing underlying price at this close',
   source_expired:'Underlying contract expired',option_underlying_missing:'Missing underlying price at this close',option_calibration_nonconvergent:'Settlement could not be calibrated',
   option_settlement_below_intrinsic:'Settlement below intrinsic value',option_settlement_above_bound:'Settlement above its no-arbitrage bound',option_calibration_fallback_undeclared:'Settlement at intrinsic without a declared fallback',
   discount_coverage:'Payment date outside the discount curve'},
  heat_rate_call:{heat_rate_call_marks:'No volatility and correlation marks for every delivery month',heat_rate_call_exercise:'An exercise day has passed; exercise results are Coming soon',
   spread_option_inputs:'A forward price or mark is outside the option model'},
  gas_storage:{gas_storage_plan:'No injection and withdrawal plan in force',gas_storage_started:'The term has started; recorded flows are Coming soon',gas_storage_ratchet:'The plan exceeds a ratchet limit',
   gas_storage_inventory:'The plan leaves the contracted capacity',gas_storage_terminal:'The plan misses the terminal inventory',gas_storage_direction:'The plan flows both ways on a gas day',
   gas_storage_market_context:'The plan path needs the contract\'s own market definition at this close'},
  ppa:{other_definition_versions:'Booked market inputs are unavailable at this close',unpriced_delivery:'No price for part of its delivery at this close',needs_rebooking:'Needs rebooking'},
};

export function valuationReason(code:ValuationReason,instrumentType:string):string{
  if(typeof instrumentType!=='string'||!Object.hasOwn(reasons,instrumentType))throw Error('The Blotter returned an unknown instrument type.');
  const words=reasons[instrumentType];
  if(typeof code!=='string'||!Object.hasOwn(words,code))throw Error('The Blotter returned an unknown valuation status.');
  return words[code]!;
}

// The backend's count of positions a total leaves out; anything but a whole number of positions is refused.
export function excludedPositions(value:unknown):number{
  if(typeof value!=='number'||!Number.isInteger(value)||value<0)throw Error('The Blotter returned an invalid count of positions it cannot value.');
  return value;
}

// The note a valuation total carries when the backend left positions out of it.
export function valuationNote(excluded:number):string|null{
  const count=excludedPositions(excluded);
  return count===0?null:`Excludes ${count} position${count===1?'':'s'} that can't be valued at this close`;
}

// The measures that add only the positions a close values (D220); booked quantities and prices count every position.
const valuationMeasures:ReadonlySet<string>=new Set(['presentValue','executionPnl','markMin','markMax','markMean']);
export const valuationMeasure=(measure:string)=>valuationMeasures.has(measure);
