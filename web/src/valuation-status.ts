// Plain words for the positions a close cannot value (D213). The backend decides which positions
// those are and why; a status it has not defined is refused rather than shown as a value.
export type ValuationReason='source_expired'|'missing_observation';
const reasons:Readonly<Record<ValuationReason,string>>={
  source_expired:'Contract expired · awaiting settlement',
  missing_observation:'Missing price at this close',
};

export function valuationReason(code:ValuationReason):string{
  if(typeof code!=='string'||!Object.hasOwn(reasons,code))throw Error('The Blotter returned an unknown valuation status.');
  return reasons[code];
}

// The note a valuation total carries when the backend left positions out of it.
export function valuationNote(excluded:number):string|null{
  if(!Number.isInteger(excluded)||excluded<0)throw Error('The Blotter returned an invalid count of positions it cannot value.');
  return excluded===0?null:`Excludes ${excluded} position${excluded===1?'':'s'} that can't be valued at this close`;
}
