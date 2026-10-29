import type {ReferenceKey} from './swap-observations.js';

export type ProjectionPolicyReceipt={instrumentId:string;sideId:string;curve:ReferenceKey;
 settlement:{mode:1|2;curveTargetIndex:ReferenceKey;selectedIndex:ReferenceKey;projectionPolicy:'forward_curve_no_basis_v1'|null};label:string};
/** Risk policy evidence is supplied by the calculation, never reconstructed from marks or exposures. */
export function projectionPolicyLabels(value:unknown):string[]{
 const invalid=()=>Error('Risk projection policies are missing or invalid. Run risk again.');
 const object=(item:unknown):item is Record<string,unknown>=>!!item&&typeof item==='object'&&!Array.isArray(item);
 const text=(item:unknown):item is string=>typeof item==='string'&&item.trim().length>0;
 const key=(item:unknown):item is ReferenceKey=>object(item)&&text(item.code)&&object(item.revision)&&text(item.revision.catalogId)&&text(item.revision.revisionId);
 const same=(a:ReferenceKey,b:ReferenceKey)=>a.code===b.code&&a.revision.catalogId===b.revision.catalogId&&a.revision.revisionId===b.revision.revisionId;
 if(!Array.isArray(value))throw invalid();
 const labels=new Set<string>();
 for(const item of value){
  if(!object(item)||!text(item.instrumentId)||!text(item.sideId)||!key(item.curve)||!text(item.label)||!object(item.settlement))throw invalid();
  const selection=item.settlement;if(!key(selection.curveTargetIndex)||!key(selection.selectedIndex))throw invalid();
  if(selection.mode===1){if(selection.projectionPolicy!==null||!same(selection.curveTargetIndex,selection.selectedIndex))throw invalid();}
  else if(selection.mode===2){
   if(selection.projectionPolicy!=='forward_curve_no_basis_v1'||same(selection.curveTargetIndex,selection.selectedIndex)||
    selection.curveTargetIndex.revision.catalogId!==selection.selectedIndex.revision.catalogId||selection.curveTargetIndex.revision.revisionId!==selection.selectedIndex.revision.revisionId)throw invalid();
   labels.add(item.label);
  }else throw invalid();
 }
 return [...labels];
}
