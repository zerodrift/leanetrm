export type ContractPriceStatusRequest={snapshotId:string;curveId:string;version:number;nodeId:string};
type PriceState={nodeId:string;curveCode:string;targetContractId:string;sourceContractId:string;deliveryFrom:string;deliveryTo:string;expiry:string;
 state:'projected'|'awaiting_publication'|'final'|'publication_schedule_missing';publicationId:string;publicationVersion:number;index:string;
 occurrenceId:string|null;expectedPublicationAt:string|null;publishedAt:string|null;releaseId:string|null;fixingPrice:string|null;selectedAsDependency:boolean;price?:string|null;pricingError?:string|null};
export type ContractPriceStatus={snapshotId:string;curveId:string;version:number;rows:PriceState[]};
const labels={projected:'Projected',awaiting_publication:'Awaiting publication',final:'Final published price',publication_schedule_missing:'Publication schedule missing'};
/** Present the server's saved-price states. No date comparison, fixing selection or fallback price runs in the browser. */
export function contractPriceStatusRows(response:ContractPriceStatus,request:ContractPriceStatusRequest,contracts:{id:string;label:string}[]):(string|null)[][]{
 if(!response||response.snapshotId!==request.snapshotId||response.curveId!==request.curveId||response.version!==request.version||!Array.isArray(response.rows)||
  response.rows.some(row=>!row||!Object.hasOwn(labels,row.state)||!row.nodeId||!row.targetContractId||!row.sourceContractId||!row.deliveryFrom||!row.deliveryTo||typeof row.selectedAsDependency!=='boolean'||
   (row.state==='final'?typeof row.fixingPrice!=='string'||!row.releaseId||!row.occurrenceId||!row.publishedAt:row.fixingPrice!==null)))
  throw Error('The saved contract price states are missing or inconsistent. Reload to inspect them.');
 return response.rows.filter(row=>row.nodeId===request.nodeId).map(row=>[
  contracts.find(contract=>contract.id===row.targetContractId)?.label??row.targetContractId,`${row.deliveryFrom} → ${row.deliveryTo}`,labels[row.state],row.fixingPrice,
  row.expectedPublicationAt??'No occurrence linked',row.publishedAt??'Not published',row.index,row.selectedAsDependency?'Selected for this valuation':row.state==='final'?'Not selected for this valuation':'No final price selected',row.price??null,row.pricingError??'—']);
}
