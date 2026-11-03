// Physical reference controls display database masters. Pricing resolution is a
// managed operation; this module neither selects a mapping nor handles curves.
import {clockLabel,type ClockRow} from './market-view.js';
export const crudeReferenceFields=['commodity','grade','facility','custodyPoint','quantityCode','clock'] as const;
export type CrudeReferenceField=typeof crudeReferenceFields[number];
export type CrudeSelection=Partial<Record<CrudeReferenceField,string>>;
export type CrudeReferenceRow={code:string;name?:string;commodity_code?:string;quantity_code?:string;unit_code?:string;dimension_code?:string;symbol?:string;location_code?:string;standard_temperature?:string|number;temperature_scale?:string};
export type CrudeReferenceData={catalogId:string;revisionId:string;commodities:CrudeReferenceRow[];grades:CrudeReferenceRow[];locations:CrudeReferenceRow[];deliveryPoints:{commodity_code:string;grade_code:string;facility_code:string;custody_point_code:string}[];quantities:CrudeReferenceRow[];units:CrudeReferenceRow[];clocks:CrudeReferenceRow[]};
// The clock list is the one the crude profile admits, which its catalog states apart from the reference masters.
export function crudeChoices(references:CrudeReferenceData,clocks:readonly ClockRow[]){
 const commodities=new Set(references.grades.map(g=>g.commodity_code));
 const facilities=new Set(references.deliveryPoints.map(r=>r.facility_code));
 const custodyPoints=new Set(references.deliveryPoints.map(r=>r.custody_point_code));
 const option=(r:CrudeReferenceRow)=>({value:r.code,label:r.name??r.code});
 return{commodity:references.commodities.filter(r=>commodities.has(r.code)).map(option),grade:references.grades.map(option),
  facility:references.locations.filter(r=>facilities.has(r.code)).map(option),custodyPoint:references.locations.filter(r=>custodyPoints.has(r.code)).map(option),
  quantityCode:references.quantities.filter(r=>commodities.has(r.commodity_code)&&r.dimension_code==='volume').map(r=>({value:r.code,label:references.units.find(u=>u.code===r.unit_code)?.symbol??r.unit_code??r.code})),
  clock:clocks.map(r=>({value:r.code,label:clockLabel(r)}))};
}
export function crudeSelection(selected:CrudeSelection):Record<CrudeReferenceField,string>|undefined{
 if(crudeReferenceFields.some(field=>!selected[field]))return;
 return Object.fromEntries(crudeReferenceFields.map(field=>[field,selected[field]!])) as Record<CrudeReferenceField,string>;
}
// The crude contract fixes its standard temperature in degrees Fahrenheit; any other scale is refused rather than shown as its code.
export function standardTemperature(value:string|number,scale:string){
 if(scale!=='fahrenheit')throw Error(`The crude grade names an unsupported temperature scale: ${scale}.`);
 return `${value} °F`;
}
