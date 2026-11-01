// Existing whole-report fault checks inspect both transports as the same workspace.
// Stream-specific checks use the raw frames instead of this buffering probe.
export async function setupWorkspaceProbe(evaluate){
 await evaluate(`window.__workspaceProbe=${workspaceProbe.toString()}`);
}
function workspaceProbe(transport,inspect){
 return async(input,init)=>{
  const streamed=typeof input==='string'&&input.endsWith('/trading/workspace/stream');
  const path=streamed?input.slice(0,-7):input;
  const send=async(url,options)=>{
   const response=await transport(streamed&&url===path?input:url,options);
   if(!streamed||url!==path||!response.ok)return response;
   const frames=(await response.text()).trim().split('\n').map(line=>JSON.parse(line));
   const end=frames.at(-1);
   if(end.type!=='complete')return new Response(JSON.stringify(end),{status:503,headers:{'Content-Type':'application/json'}});
   return new Response(JSON.stringify({catalog:end.catalog,blotter:{...end.blotter,holdings:frames.flatMap(f=>f.type==='rows'?f.rows:[])},summaries:end.summaries}),{status:200,headers:{'Content-Type':'application/json'}});
  };
  const response=await inspect(path,init,send);
  if(!streamed||!response.ok)return response;
  const result=await response.json(),rows=result.blotter.holdings,total=rows.length;
  const frames=[{...result.catalog,type:'start',positions:total},{type:'rows',positions:total,valued:total,rows},
   {type:'complete',positions:total,valued:total,...result,blotter:{...result.blotter,holdings:undefined}}];
  return new Response(frames.map(f=>JSON.stringify(f)+'\n').join(''),{status:200,headers:{'Content-Type':'application/x-ndjson'}});
 };
}
