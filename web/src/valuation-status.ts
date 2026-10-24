// Plain words for the positions a close cannot value (D214). The backend decides which positions
// those are and why; a status it has not defined is refused rather than shown as a value.
export type ValuationReason='source_expired'|'missing_observation';
// An expired source is a future's own contract, awaiting settlement, but for a swap an input of the
// composite or custom curve it is projected from.
const reasons:Readonly<Record<string,Readonly<Record<ValuationReason,string>>>>={
  future:{source_expired:'Contract expired · awaiting settlement',missing_observation:'Missing price at this close'},
  financial_swap:{source_expired:'Past marking cutoff',missing_observation:'Missing price at this close'},
};

export function valuationReason(code:ValuationReason,instrumentType:string):string{
  if(typeof instrumentType!=='string'||!Object.hasOwn(reasons,instrumentType))throw Error('The Blotter returned an unknown instrument type.');
  const words=reasons[instrumentType];
  if(typeof code!=='string'||!Object.hasOwn(words,code))throw Error('The Blotter returned an unknown valuation status.');
  return words[code];
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

// The measures that add only the positions a close values (D214); booked quantities and prices count every position.
const valuationMeasures:ReadonlySet<string>=new Set(['presentValue','executionPnl','markMin','markMax','markMean']);
export const valuationMeasure=(measure:string)=>valuationMeasures.has(measure);
