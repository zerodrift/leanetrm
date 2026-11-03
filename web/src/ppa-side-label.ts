import {friendly} from './market-view.js';
// Side names describe their terms; direction is a separate selection.
const names:Record<string,string>={energy_delivery:'Power',certificate_delivery:'REC',capacity_delivery:'Capacity',strike_payment:'Fixed price',indexed_payment:'Index price',fixed_charge:'Fee',certificate_payment:'REC price',capacity_payment:'Capacity price'};
export const ppaSideLabel=(role:string)=>names[role]??friendly(role);
// A side as the ticket and its tables name it, numbered from one.
export const ppaSideName=(role:string,ordinal:number)=>`${ppaSideLabel(role)} ${ordinal+1}`;
// The break-even measures, quote rounding and pending-actuals policies the catalog offers, by name; a code the ticket does not know is refused, never shown.
const measures:Record<string,string>={ValueExcludingOverdue:'Value excluding overdue cash',CompletePv:'Complete PV'},roundings:Record<string,string>={half_even_v1:'Round half to even'};
const named=(names:Record<string,string>,kind:string)=>(code:string)=>{const name=names[code];if(!name)throw Error(`The PPA catalog offers an unknown ${kind}: ${code}.`);return name;};
const pendingActuals:Record<string,string>={require_actuals_v1:'Require delivered actuals',estimate_until_actual_v1:'Estimate until actuals arrive'};
export const ppaMeasureLabel=named(measures,'measure'),ppaRoundingLabel=named(roundings,'rounding'),ppaPendingActualsLabel=named(pendingActuals,'pending-actuals policy');
// Quantity specifications offered by their unit's symbol.
type Row={[key:string]:unknown};
export function quantityChoices(rows:readonly Row[],units:readonly Row[]){
 return rows.map(r=>{const unit=units.find(u=>u.code===r.unit_code);if(typeof unit?.symbol!=='string')throw Error(`The PPA catalog lists no unit for ${String(r.code)}.`);return{value:String(r.code),label:unit.symbol};});
}
