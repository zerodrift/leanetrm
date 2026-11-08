import {numberText} from './schedule-grid.js';
// How started-delivery gas is worded. The backend decides every state, amount and basis; this module only words what it reports.
export type GasMeasures={unrealized:string;realizedUnsettled:string;total:string;label:string;discounting:string;expectedCash:{dueAt:string;amount:string}[]};
export type MeasureLine={label:string;value:string};
// The P&L explain measures: days not yet ended are unrealized; delivered days fixed and not yet paid are realized but unsettled.
const measureTexts:Readonly<Record<string,string>>={Unrealized:'Unrealized (remaining delivery)',RealizedUnsettled:'Realized, unsettled (delivered, not paid)'};
export type LifecycleRow={paymentAt:string|null;paid:string|null;outstanding:string|null;paidAt?:string|null;accrued?:string|null;finalDetermination?:string|null;
 lifecycle:{delivery:string;due:string|null;cash:string|null;economicClass:string|null;coverage:string|null;fulfillment:string|null}};

export const words=(value:string|null|undefined)=>{if(!value)return '—';const text=value.replace(/_/g,' ').replace(/([a-z])([A-Z])/g,'$1 $2');return text[0]+text.slice(1).toLowerCase();};

/** The unrealized and realized unsettled measures and their labelled sum as separate figures, with the discounting basis both state;
 *  the expected cash follows, undiscounted and by due date, and is marked as not added to the sum. */
export function measureLines(m:GasMeasures,currency:string,instant:(at:string)=>string):MeasureLine[]{
 const amount=(value:string)=>`${numberText(value)} ${currency}`;
 return [{label:measureTexts.Unrealized,value:amount(m.unrealized)},{label:measureTexts.RealizedUnsettled,value:amount(m.realizedUnsettled)},{label:m.label,value:amount(m.total)},
  {label:'Discounting',value:m.discounting},{label:'Expected cash by due date · not added to the total',value:''},
  ...m.expectedCash.map(c=>({label:`Due ${instant(c.dueAt)}`,value:amount(c.amount)}))];
}

const basisTexts:Readonly<Record<string,string>>={contract_estimate_allocation_pending_v1:'estimated from contract; allocation pending'};
/** The trade overview's value measure: a started-delivery trade names its measure with its discounting basis, both or neither. */
export function valueMeasureText(label:string|undefined,discounting:string|undefined){
 if((label===undefined)!==(discounting===undefined))throw Error('A trade’s value measure and its discounting basis come together.');
 return label===undefined?'Contractual PV':`${label} · ${discounting}`;
}

export const quantityBasisText=(state:string|null)=>state===null?'—':basisTexts[state]??words(state);

const unpaid=new Set(['Remaining','AccruedUnpaid']),paidStates=new Set(['Paid','PartPaid']);

/** The lines under a row's delivery state: its settlement, what was paid and when, what is still due, and its measure. A row without the
 *  started-delivery states (another product, an older program) words only the fields it always had. */
export function lifecycleLines(row:LifecycleRow,instant:(at:string|null)=>string):string[]{
 const state=row.lifecycle,cash=state.cash,physical=cash==='NotApplicable';
 // A part-paid day shows both when the recorded payment was made and when the unpaid remainder is due.
 const paidOn=cash!==null&&paidStates.has(cash)&&row.paidAt?`Paid on ${instant(row.paidAt)}`:'';
 const due=cash!==null&&(unpaid.has(cash)||cash==='PartPaid')&&row.paymentAt!==null?`Due date: ${instant(row.paymentAt)}`:'';
 return [state.due&&`Payment: ${words(state.due)}`,cash&&(physical?'Settlement: not applicable (physical delivery)':`Cash: ${words(cash)}`),
  row.accrued!=null&&`Accrued: ${numberText(row.accrued)}`,row.paid!==null&&`Paid: ${numberText(row.paid)}`,row.outstanding!==null&&`Outstanding: ${numberText(row.outstanding)}`,paidOn,due,
  row.finalDetermination!=null&&`Final determination: ${numberText(row.finalDetermination)} · accrual unchanged`,
  state.fulfillment&&`Fulfillment: ${words(state.fulfillment)}`,state.economicClass&&words(state.economicClass),state.coverage&&(measureTexts[state.coverage]??words(state.coverage))].filter((v):v is string=>!!v);
}
