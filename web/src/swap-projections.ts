/** Published projection choices come from the authorized backend catalog. */
export type CustomProjection=Readonly<{catalogId:string;curveId:string;version:number;outputNode:string;position:number|null;selection:string;implementation:string}>;
export type ProjectionChoice={label:string;reference:CustomProjection;samplings:{resetFrequency:string;averagingMethod:string}[]};
export function projectionReference(selected:string,choices:readonly ProjectionChoice[]):CustomProjection|null{
 if(!selected)throw Error('Choose the projected price source explicitly.');
 if(selected==='ordinary')return null;
 const choice=choices.find(choice=>JSON.stringify(choice.reference)===selected);
 if(!choice)throw Error('The selected published projection is unavailable in this context.');
 return structuredClone(choice.reference);
}
/** The name of the curve a projection key (`[definitionRevisionId, curve, index]`) selects, for a choice kept from earlier terms. */
export function projectionKeyLabel(key:string,curveName:(curve:string)=>string):string{
 let curve:unknown;
 try{curve=JSON.parse(key)?.[1];}catch{curve=undefined;}
 if(typeof curve!=='string'||!curve)throw Error('The kept projection has no curve to name.');
 return curveName(curve);
}
