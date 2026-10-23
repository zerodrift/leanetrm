// Selection and search over backend-qualified choices; no financial conventions
// or date coverage are inferred in the presentation layer.
export function linkedValue(values:readonly string[],current=''){return values.includes(current)?current:values.length===1?values[0]:'';}
export function quantityChoice(choices:readonly {specification:{code:string}}[],quoteQuantityCode:string,current=''){
 if(choices.some(c=>JSON.stringify(c.specification)===current))return current;
 const canonical=choices.filter(c=>c.specification.code===quoteQuantityCode);
 return canonical.length===1?JSON.stringify(canonical[0].specification):'';
}
export type DeliveryPeriod={resetFrequency:string;averagingMethod:string;deliveryFrom:string;deliveryTo:string};
export function deliveryChoices(periods:readonly DeliveryPeriod[],resetFrequency:string,averagingMethod:string,start=''){
 const available=periods.filter(p=>p.resetFrequency===resetFrequency&&p.averagingMethod===averagingMethod);
 return{starts:[...new Set(available.map(p=>p.deliveryFrom))],ends:[...new Set(available.filter(p=>p.deliveryFrom===start).map(p=>p.deliveryTo))]};
}
export function filterSwapIndices<T extends {commodity:string;index:string;curve:string;definitionRevisionId:string}>(rows:readonly T[],commodity:string,search='',revision?:string):T[]{
 const query=search.trim().toLowerCase().replaceAll('_',' ');
 return rows.filter(i=>i.commodity===commodity&&(!revision||i.definitionRevisionId===revision)&&`${i.index} ${i.curve}`.toLowerCase().replaceAll('_',' ').includes(query));
}
