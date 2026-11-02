// Physical reference controls display database masters. Pricing resolution is a
// managed operation; this module neither selects a mapping nor handles curves.
export const crudeReferenceFields=['commodity','grade','facility','custodyPoint','quantityCode','clock'] as const;
export type CrudeReferenceField=typeof crudeReferenceFields[number];
export type CrudeSelection=Partial<Record<CrudeReferenceField,string>>;
export type CrudeReferenceRow={code:string;name?:string;commodity_code?:string;quantity_code?:string;unit_code?:string;dimension_code?:string;symbol?:string;location_code?:string;standard_temperature?:string|number;temperature_scale?:string};
export type CrudeReferenceData={catalogId:string;revisionId:string;commodities:CrudeReferenceRow[];grades:CrudeReferenceRow[];locations:CrudeReferenceRow[];deliveryPoints:{commodity_code:string;grade_code:string;facility_code:string;custody_point_code:string}[];quantities:CrudeReferenceRow[];units:CrudeReferenceRow[];clocks:CrudeReferenceRow[]};
export function crudeChoices(references:CrudeReferenceData){
 const commodities=new Set(references.grades.map(g=>g.commodity_code));
 const facilities=new Set(references.deliveryPoints.map(r=>r.facility_code));
 const custodyPoints=new Set(references.deliveryPoints.map(r=>r.custody_point_code));
 const option=(r:CrudeReferenceRow)=>({value:r.code,label:r.name??r.code});
 return{commodity:references.commodities.filter(r=>commodities.has(r.code)).map(option),grade:references.grades.map(option),
  facility:references.locations.filter(r=>facilities.has(r.code)).map(option),custodyPoint:references.locations.filter(r=>custodyPoints.has(r.code)).map(option),
  quantityCode:references.quantities.filter(r=>commodities.has(r.commodity_code)&&r.dimension_code==='volume').map(r=>({value:r.code,label:`${r.code} · ${references.units.find(u=>u.code===r.unit_code)?.symbol??r.unit_code}`})),
  clock:references.clocks.map(option)};
}
export function crudeSelection(selected:CrudeSelection):Record<CrudeReferenceField,string>|undefined{
 if(crudeReferenceFields.some(field=>!selected[field]))return;
 return Object.fromEntries(crudeReferenceFields.map(field=>[field,selected[field]!])) as Record<CrudeReferenceField,string>;
}
