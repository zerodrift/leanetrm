// Presentation of the exact eligible tuples supplied by the managed catalog.
// This does not infer a valuation setup or a compatible unit conversion.
export const crudeReferenceFields=['commodity','grade','facility','custodyPoint','quantityCode','curve','clock'] as const;
export type CrudeReferenceField=typeof crudeReferenceFields[number];
export type CrudeSelection=Partial<Record<CrudeReferenceField,string>>;
export type CrudeReference=Record<CrudeReferenceField,string>&{code:string;definitionRevisionId:string;commodityName:string;gradeName:string;facilityName:string;custodyPointName:string;quantityName:string;curveName:string;clockName:string};
const labels={commodity:'commodityName',grade:'gradeName',facility:'facilityName',custodyPoint:'custodyPointName',quantityCode:'quantityName',curve:'curveName',clock:'clockName'} as const;
export function crudeChoices<T extends CrudeReference>(rows:readonly T[],selected:CrudeSelection){
 return Object.fromEntries(crudeReferenceFields.map(field=>{
  const eligible=rows.filter(row=>crudeReferenceFields.every(other=>other===field||!selected[other]||row[other]===selected[other]));
  const choices=[...new Map(eligible.map(row=>[row[field],{value:row[field],label:row[labels[field]]}])).values()];
  return[field,choices];
 })) as Record<CrudeReferenceField,{value:string;label:string}[]>;
}
export function crudeSelection<T extends CrudeReference>(rows:readonly T[],selected:CrudeSelection):T|undefined{
 if(crudeReferenceFields.some(field=>!selected[field]))return;
 const matches=rows.filter(row=>crudeReferenceFields.every(field=>row[field]===selected[field]));
 if(matches.length>1)throw Error('The selected references have ambiguous valuation bindings. Configure one exact binding.');
 return matches[0];
}
