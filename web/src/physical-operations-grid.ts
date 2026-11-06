import type {PositionFact} from './position-facts.js';
import type {GasQuantityRow} from './gas-daily-quantities.js';

const products={
 physical_gas:{label:'Natural gas',tone:'gas',workflow:'Manual operations'},
 physical_crude:{label:'Crude oil',tone:'crude',workflow:'Delivery & cash'},
 physical_lng:{label:'LNG',tone:'lng',workflow:'Delivery view · operations TBD'}
} as const;
export function physicalProduct(type:PositionFact['instrumentType']){
 if(!(type in products))throw Error('The directory returned an unsupported physical instrument.');
 return products[type as keyof typeof products];
}
export const physicalTradeKey=(p:Pick<PositionFact,'tradeId'|'bindingId'>)=>p.tradeId+':'+p.bindingId;
export const gasDeliveryKey=(r:Pick<GasQuantityRow,'obligationId'|'day'>)=>r.obligationId+':'+r.day;
export function operationsTag(label:string,tone:string){const tag=document.createElement('span');tag.className='operations-tag';tag.dataset.tone=tone;tag.textContent=label;return tag;}

/** Display only: no PV, lifecycle inference or aggregation of independent sides. */
export function physicalTradeGrid(positions:readonly PositionFact[]){
 const rows=positions.map(p=>[p.tradeReference,physicalProduct(p.instrumentType).label,p.book,p.deliveryFrom??'',p.deliveryTo??'',p.marketLabel??'',p.bookedQuantity??'',p.bookedUnit??'',physicalProduct(p.instrumentType).workflow]);
 return{keys:['trade','commodity','book','from','to','market','quantity','unit','workflow'],headers:['Trade','Commodity','Book','Delivery from','Delivery to · exclusive','Pricing reference','Quantity','Unit','Workflow'],rows,
  displayRows:rows.map((r,i)=>r.map((v,c)=>c===6&&positions[i].bookedQuantity===null?'By side':v||'—')),rowKeys:positions.map(physicalTradeKey),numeric:[6]};
}
