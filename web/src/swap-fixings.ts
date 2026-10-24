// Entry parsing only. The managed contract validates dates, exact observation
// coverage, source, eligibility and the selected fixing revision policy.
export type FixingBoundary={from:string;to:string;fixingAt:string};
export type FixingTerms={implementation:string;revisionPolicy:string;source:string;boundaries:FixingBoundary[]};
export function parseFixingBoundaries(text:string):FixingBoundary[]{
 const rows=text.trim().split(/\r?\n/).filter(row=>row.trim());
 if(!rows.length)throw Error('Enter an explicit fixing boundary for every observation.');
 return rows.map((row,n)=>{
  const fields=row.split(',').map(value=>value.trim());
  if(fields.length!==3||fields.some(value=>!value))throw Error(`Fixing row ${n+1} requires observation start, exclusive end and a UTC fixing instant.`);
  return{from:fields[0],to:fields[1],fixingAt:fields[2]};
 });
}
