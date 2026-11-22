/** A delivery month of a running swap that an amendment must keep as booked (D441), with the backend's reason. */
export type LockedPeriod={deliveryFrom:string;deliveryTo:string;reason:string};
/** A swap's value under its settlement lifecycle, as the backend states it; a running swap has no pure valuation. */
export type LifecycleValuation=Record<string,unknown>&{payments:{membershipOrdinal:number;deliveryFrom:string;deliveryTo:string;state:string;remainingValue:string}[]};

const reasons:Record<string,string>={settled:'Settled',delivered:'Delivered',in_progress:'In progress',fixing:'Fixing has started'};

/** Why the period of these delivery dates cannot change, or undefined when an amendment may change it. */
export function lockedNote(locked:readonly LockedPeriod[]|undefined,from:string,to:string){
 const period=locked?.find(p=>p.deliveryFrom===from&&p.deliveryTo===to);
 return period?`${reasons[period.reason]??'Started'} · cannot change`:undefined;
}

/** The first locked period that a proposed list of delivery periods would drop, which the contract cannot do. */
export const droppedLockedPeriod=(locked:readonly LockedPeriod[]|undefined,periods:readonly {deliveryFrom:string;deliveryTo:string}[])=>
 locked?.find(p=>!periods.some(kept=>kept.deliveryFrom===p.deliveryFrom&&kept.deliveryTo===p.deliveryTo));

/** The rows of a lifecycle valuation's delivery months, in the order the backend states them. */
export const lifecyclePeriodRows=(value:LifecycleValuation)=>
 value.payments.map(p=>[p.membershipOrdinal===0?'First':'Second',`${p.deliveryFrom} to ${p.deliveryTo}`,p.state,p.remainingValue]);
