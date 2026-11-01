import {crudeConvention} from './physical-crude-model.js';
export const crudeReceiptProfile='physical_crude_receipt_cash_v1';
export const crudeRoundingPolicy='crude_cumulative_receipt_cash_v1';
type Base=ReturnType<typeof crudeOperationBase>;
const required=(value:string,name:string)=>{if(!value.trim())throw Error(`Enter ${name}.`);return value;};
// Bind explicit form selections only; native operations own quantities, allocation and rounding.
export function crudeOperationBase(context:Record<string,unknown>,head:{tradeId:string;revision:number;version:number},fields:{profile:string;businessKey:string;effectiveAt:string;evidence:string}){
 if(fields.profile!==crudeReceiptProfile)throw Error('Choose the receipt and cash profile.');
 if(!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$/.test(fields.effectiveAt))throw Error('Enter an explicit UTC effective instant.');
 return{context:{...context,convention:crudeConvention},tradeId:head.tradeId,expectedRevision:head.revision,expectedVersion:head.version,operationId:crypto.randomUUID(),profile:fields.profile,businessKey:required(fields.businessKey,'a business key'),effectiveAt:fields.effectiveAt,evidence:required(fields.evidence,'evidence')};
}
export function crudeReceipt(base:Base,fields:{quantity:string;titleTransferred:boolean;disposition:string},sale?:{trade_id:string;revision:number}){
 if(!fields.titleTransferred)throw Error('Confirm title transferred at acceptance.');
 if(!['owned_inventory','onward_delivery'].includes(fields.disposition))throw Error('Choose a disposition.');
 if(fields.disposition==='onward_delivery'&&!sale)throw Error('Choose the exact booked sale revision.');
 return{...base,quantity:required(fields.quantity,'certified quantity'),titleTransferred:true,disposition:fields.disposition,saleTradeId:fields.disposition==='onward_delivery'?sale!.trade_id:null,saleRevision:fields.disposition==='onward_delivery'?sale!.revision:null};
}
export function crudeDispatch(base:Base,fields:{receiptId:string;quantity:string;titleTransferred:boolean}){
 if(!fields.titleTransferred)throw Error('Confirm title transferred on dispatch.');
 return{...base,receiptId:required(fields.receiptId,'a receipt'),quantity:required(fields.quantity,'dispatch quantity'),titleTransferred:true};
}
export function crudeInvoice(base:Base,fields:{receiptId:string;component:string;decimalPlaces:string;roundingPolicy:string}){
 if(!['consideration','handling_fee'].includes(fields.component))throw Error('Choose a cash component.');
 if(fields.roundingPolicy!==crudeRoundingPolicy||!/^\d+$/.test(fields.decimalPlaces))throw Error('Choose currency precision and rounding policy.');
 return{...base,receiptId:required(fields.receiptId,'a receipt'),component:fields.component,decimalPlaces:Number(fields.decimalPlaces),roundingPolicy:fields.roundingPolicy};
}
export function crudePayment(base:Base,fields:{invoiceId:string;amount:string;direction:string}){
 if(!['pay','receive'].includes(fields.direction))throw Error('Choose the payment direction.');
 return{...base,invoiceId:required(fields.invoiceId,'an invoice'),amount:required(fields.amount,'payment amount'),direction:fields.direction};
}

/** Acceptance is durable even when the separate contractual valuation is unavailable. */
export async function refreshAcceptedCrudeOperation(valuation:()=>Promise<void>,ledger:()=>Promise<void>):Promise<unknown>{
 let failure:unknown;try{await valuation();}catch(error){failure=error;}
 await ledger();return failure;
}
// A facts read establishes a real position mode; a mounted form alone has a hidden ancestor.
export async function openCrudeOperationPanel(selected:{tradeId:string;bindingId:string;revision:number},show:(mode:{kind:'position';tradeId:string;bindingId:string;revision:number})=>Promise<boolean>,open:()=>Promise<boolean>,current:()=>boolean,reveal:()=>void){
 if(!await show({kind:'position',...selected})||!current())return false;
 if(!await open()||!current())return false;
 reveal();return true;
}
export const crudeRecordCorrectionPolicy='crude_record_correction_v1';
/** Corrections identify an eligible native record; they do not create an opposite cash movement. */
export function crudeRecordCorrection(base:Base,kind:'payment'|'invoice',policy:string,target?:{id:string;canReverse?:boolean;canVoid?:boolean}){
 if(policy!==crudeRecordCorrectionPolicy)throw Error('Choose the explicit record correction policy.');
 if(!target||(kind==='payment'?target.canReverse:target.canVoid)!==true)throw Error('Choose a record eligible for correction.');
 return{...base,reversalPolicy:policy,...(kind==='payment'?{paymentId:target.id}:{invoiceId:target.id})};
}
