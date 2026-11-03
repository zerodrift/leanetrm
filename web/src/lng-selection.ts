// Table-backed presentation choices. Pricing and relationship validation remain
// native operations; unpriced masters are not filtered through market mappings.
export const lngPhysicalFields=['commodity','product','loadFacility','loadCustody','dischargeFacility','dischargeCustody','energyQuantityCode','clock'] as const;
export type LngPhysicalField=typeof lngPhysicalFields[number];
type Row={code:string;name?:string;symbol?:string;commodity_code?:string;unit_code?:string;dimension_code?:string;edition_code?:string;rule_code?:string};
export type LngReferences={referenceCatalogId:string;referenceRevisionId:string;commodities:Row[];products:Row[];custodyPoints:{stage:string;facility_code:string;custody_point_code:string}[];locations:Row[];quantities:Row[];units:Row[];clocks:Row[];conversions:Row[];incotermsEditions:Row[];incotermsRules:Row[]};
export function lngChoices(r:LngReferences){
 const commodities=new Set(r.products.map(p=>p.commodity_code)),quantities=r.quantities.filter(q=>commodities.has(q.commodity_code)),units=new Set(quantities.map(q=>q.unit_code));
 const option=(row:Row)=>({value:row.code,label:row.name??row.symbol??row.code});
 const points=(stage:string,key:'facility_code'|'custody_point_code')=>{const codes=new Set(r.custodyPoints.filter(p=>p.stage===stage).map(p=>p[key]));return r.locations.filter(l=>codes.has(l.code)).map(option);};
 const quantity=(row:Row)=>({value:row.code,label:`${row.code} · ${r.units.find(u=>u.code===row.unit_code)?.symbol??row.unit_code}`});
 return{commodity:r.commodities.filter(c=>commodities.has(c.code)).map(option),product:r.products.map(option),loadFacility:points('load','facility_code'),loadCustody:points('load','custody_point_code'),dischargeFacility:points('discharge','facility_code'),dischargeCustody:points('discharge','custody_point_code'),energyQuantityCode:quantities.filter(q=>q.dimension_code==='energy').map(quantity),clock:r.clocks.map(option),quantityCode:quantities.map(quantity),quantityUnit:r.units.filter(u=>units.has(u.code)).map(option),conversionCode:r.conversions.map(option),edition:r.incotermsEditions.map(option),rule:r.incotermsRules.map(row=>({value:row.code,label:`${row.rule_code??row.code} · ${r.incotermsEditions.find(e=>e.code===row.edition_code)?.name??row.edition_code}`})),namedPlace:r.locations.map(option)};
}
export function lngSelection(value:Partial<Record<LngPhysicalField,string>>):Record<LngPhysicalField,string>|undefined{
 if(lngPhysicalFields.some(k=>!value[k]))return;
 return Object.fromEntries(lngPhysicalFields.map(k=>[k,value[k]!])) as Record<LngPhysicalField,string>;
}
