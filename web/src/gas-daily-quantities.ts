import type {TicketField} from './ticket-types.js';
type Quantity={numerator:string;denominator:string};
type Head={id:string;kind:string;status:string;quantity:string|null;evidence:string;revision:number};
export type GasQuantityRow={quantitySpecification:{code:string};obligationId:string;sideCode:string;day:string;start:string;end:string;contractual:Quantity;selected:Quantity;normalizedInput:Quantity;kind:string;revisionId:string|null;heads:Head[]};
export type GasQuantityView={context:{contextId:string};tradeId:string;expectedRevision:number;selectionPolicy:string;canWrite:boolean;rows:GasQuantityRow[];history:{id:string;side_code:string;day:string;kind:string;status:string;quantity:string|null;previous_id:string|null;evidence:string}[]};
export type GasQuantityReceipt={operationId:string;tradeId:string;obligationId:string;revisionId:string;revision:number;unchanged:boolean};
// The kinds and statuses a daily quantity record takes (gas/quantities/record), as the person chooses them.
const quantityKinds=[{value:'nominated',label:'Nominated'},{value:'confirmed',label:'Confirmed'},{value:'scheduled',label:'Accepted schedule'},{value:'allocated',label:'Allocated delivery'}];
const quantityStatuses=[{value:'provisional',label:'Provisional'},{value:'final',label:'Final · allocated delivery only'},{value:'withdrawn',label:'Withdraw previous record'}];
// The record's fields as a ticket draws them for a day entry, with their choices.
const recordField=(id:string,label:string,meaning:string,type:string,choices:string|null=null,unit:string|null=null):TicketField=>
 ({id,label,meaning,type,unit,state:'editable',required:true,band:null,choices,dependsOn:[],lookup:null,soon:null,rule:null});
export const quantityRecordFields=[
 recordField('quantity','Quantity','The day\'s native quantity, a nonnegative total for the gas day; the side\'s direction signs it in the schedule.','decimal',null,'quantity'),
 recordField('kind','Quantity kind','What the quantity records.','choice','kinds'),
 recordField('status','Status','Whether the record is provisional, final or withdraws the previous record of its kind.','choice','statuses'),
 recordField('evidence','Source evidence','Where the quantity comes from, or why it is corrected.','text')];
export const quantityRecordChoices={kind:quantityKinds,status:quantityStatuses};
export type GasQuantityEntry={kind:string;status:string;quantity:string;evidence:string};
/** The record of one day's quantity, against the trade revision and the day's previous record of that kind the read returned; a withdrawal
 *  carries no quantity. The command's operation id is added by whoever sends it. */
export function quantityRecord(context:unknown,view:GasQuantityView,row:GasQuantityRow,entry:GasQuantityEntry){
 return {context,tradeId:view.tradeId,expectedRevision:view.expectedRevision,obligationId:row.obligationId,day:row.day,kind:entry.kind,status:entry.status,
  quantity:entry.status==='withdrawn'?null:entry.quantity,evidence:entry.evidence,expectedPreviousId:row.heads.find(h=>h.kind===entry.kind)?.id??null};
}
/** Whether a receipt is the outcome of this record command. */
export const recordedBy=(command:Record<string,unknown>,r:GasQuantityReceipt)=>r.operationId===command.operationId&&r.tradeId===command.tradeId&&r.obligationId===command.obligationId&&typeof r.revisionId==='string';
