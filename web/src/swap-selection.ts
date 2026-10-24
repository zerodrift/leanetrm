// Presentation filters over the authorized backend catalog. They neither infer
// economic compatibility nor manufacture a default when several choices remain.
export function linkedValue(values:readonly string[],current=''):string{
 return values.includes(current)?current:values.length===1?values[0]:'';
}
type AvailableIndex={commodity:string;index:string;curve:string;definitionRevisionId:string;contracts:unknown[];samplings:unknown[];quantityChoices:unknown[]};
export function availableSwapIndices<T extends AvailableIndex>(rows:readonly T[],commodity:string,search='',revision?:string):T[]{
 const query=search.trim().toLocaleLowerCase().replaceAll('_',' ');
 return rows.filter(i=>i.commodity===commodity&&i.contracts.length>0&&i.samplings.length>0&&i.quantityChoices.length>0&&(!revision||i.definitionRevisionId===revision)&&`${i.index} ${i.curve}`.toLocaleLowerCase().replaceAll('_',' ').includes(query));
}
export function quantityChoice(choices:readonly {specification:{code:string}}[],quoteQuantityCode:string,current=''):string{
 if(choices.some(c=>JSON.stringify(c.specification)===current))return current;
 if(current)return '';
 const canonical=choices.filter(c=>c.specification.code===quoteQuantityCode);
 return canonical.length===1?JSON.stringify(canonical[0].specification):'';
}
export type DeliveryPeriod={resetFrequency:string;deliveryFrom:string;deliveryTo:string};
export function deliveryChoices(periods:readonly DeliveryPeriod[],resetFrequency:string,start=''){
 const available=periods.filter(p=>p.resetFrequency===resetFrequency);
 return{starts:[...new Set(available.map(p=>p.deliveryFrom))],ends:[...new Set(available.filter(p=>p.deliveryFrom===start).map(p=>p.deliveryTo))]};
}
