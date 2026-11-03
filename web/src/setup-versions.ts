// Saved delivery setup versions as the editors list them: a published setup by its publication order, a draft by the setup it revises.
// Two drafts that would read alike are numbered in list order. Keys stay the values the editors select by; they are never shown.
export type SetupVersionRow={id:string;state:string;base_setup_id:string|null;publication_order:number|null};
export function setupVersionLabels(rows:readonly SetupVersionRow[]):Map<string,string>{
 const order=new Map(rows.filter(r=>r.state==='published').map(r=>[r.id,r.publication_order]));
 const named=rows.map(r=>{
  if(r.state==='published')return `Published setup ${r.publication_order}`;
  if(r.state!=='draft')throw Error(`A saved setup has a state the editor does not know: ${r.state}.`);
  if(r.base_setup_id===null)return 'New draft';
  return order.has(r.base_setup_id)?`Draft from published setup ${order.get(r.base_setup_id)}`:'Draft from an earlier setup';
 });
 const counts=new Map<string,number>(),seen=new Map<string,number>();
 for(const n of named)counts.set(n,(counts.get(n)??0)+1);
 return new Map(rows.map((r,i)=>{const n=named[i];if(counts.get(n)===1)return [r.id,n];const k=(seen.get(n)??0)+1;seen.set(n,k);return [r.id,`${n} · ${k}`];}));
}
