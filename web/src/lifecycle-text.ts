import {numberText} from './schedule-grid.js';
// How started-delivery gas is worded. The backend decides every state, amount and basis; this module only words what it reports.
export type GasMeasures={remainingDeliveryMtm:string;outstandingSettlement:string;total:string;label:string;discounting:string;expectedCash:{dueAt:string;amount:string}[]};
export type MeasureLine={label:string;value:string};
export type LifecycleRow={paymentAt:string|null;paid:string|null;outstanding:string|null;paidAt?:string|null;accrued?:string|null;finalDetermination?:string|null;
 lifecycle:{delivery:string;due:string|null;cash:string|null;economicClass:string|null;coverage:string|null;fulfillment:string|null}};

export const words=(value:string|null|undefined)=>{if(!value)return '—';const text=value.replace(/_/g,' ').replace(/([a-z])([A-Z])/g,'$1 $2');return text[0]+text.slice(1).toLowerCase();};

/** The remaining delivery MTM, the outstanding settlement and their labelled sum as separate figures, with the discounting basis both state;
 *  the expected cash follows, undiscounted and by due date, and is marked as not added to the sum. */
export function measureLines(m:GasMeasures,currency:string,instant:(at:string)=>string):MeasureLine[]{
 const amount=(value:string)=>`${numberText(value)} ${currency}`;
 return [{label:'Remaining delivery MTM',value:amount(m.remainingDeliveryMtm)},{label:'Outstanding settlement',value:amount(m.outstandingSettlement)},{label:m.label,value:amount(m.total)},
  {label:'Discounting',value:m.discounting},{label:'Expected cash by due date · not added to the total',value:''},
  ...m.expectedCash.map(c=>({label:`Due ${instant(c.dueAt)}`,value:amount(c.amount)}))];
}

const basisTexts:Readonly<Record<string,string>>={contract_estimate_allocation_pending_v1:'estimated from contract; allocation pending'};
export const quantityBasisText=(state:string|null)=>state===null?'—':basisTexts[state]??words(state);

const coverageTexts:Readonly<Record<string,string>>={RemainingDeliveryMtm:'Remaining delivery MTM',OutstandingSettlement:'Outstanding settlement'};
const deliveryStates=new Set(['NotStarted','InProgress','Ended']);
const unpaid=new Set(['Remaining','AccruedUnpaid']),paidStates=new Set(['Paid','PartPaid']);

/** The lines under a row's delivery state: its settlement, what was paid and when, what is still due, and its measure. A row without the
 *  started-delivery states (another product, an older program) words only the fields it always had. */
export function lifecycleLines(row:LifecycleRow,instant:(at:string|null)=>string):string[]{
 const state=row.lifecycle,started=deliveryStates.has(state.delivery);
 const dated=state.cash!==null&&row.paymentAt!==null&&unpaid.has(state.cash)?`Due date: ${instant(row.paymentAt)}`:
  state.cash!==null&&paidStates.has(state.cash)&&row.paidAt?`Paid on ${instant(row.paidAt)}`:'';
 return [state.due&&`Payment: ${words(state.due)}`,state.cash&&`Cash: ${words(state.cash)}`,started&&state.cash===null&&'Settlement: not applicable (physical delivery)',
  row.accrued!=null&&`Accrued: ${numberText(row.accrued)}`,row.paid!==null&&`Paid: ${numberText(row.paid)}`,row.outstanding!==null&&`Outstanding: ${numberText(row.outstanding)}`,dated,
  row.finalDetermination!=null&&`Final determination: ${numberText(row.finalDetermination)} · accrual unchanged`,
  state.fulfillment&&`Fulfillment: ${words(state.fulfillment)}`,state.economicClass&&words(state.economicClass),state.coverage&&(coverageTexts[state.coverage]??words(state.coverage))].filter((v):v is string=>!!v);
}
