const element=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;};
type Result={runId:string;sourceContextId:string;knownAt:string;mode:'manual'|'review'|'automatic';recordedAt:string;proposal:unknown;outcome:unknown};
type Report={job:{id:string;name:string;state:string};close:{id:string;name:string;at:string;businessDate:string};definition:{action:string;target:unknown;policyId:string;policyRevision:number;settings:unknown};results:Result[]};
export function setupLifecycleJobReport(host:HTMLElement,o:{api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;identity:()=>string;instant:(at:string)=>string;openLifecycle?:()=>void}){
 let generation=0,report:Report|undefined;
 function draw(){
  host.replaceChildren();host.hidden=!report;if(!report)return;
  host.append(element('h3',report.job.name),element('p',`Lifecycle task · ${report.job.state}`),element('p',`${report.close.name} · business date ${report.close.businessDate} · ${o.instant(report.close.at)}`));
  if(!report.results.length)host.append(element('p','No completed lifecycle occurrence is retained yet.'));
  const labels={manual:'Manual action required',review:'Review required',automatic:'Executed under the saved policy'};
  for(const result of report.results){
   const section=element('section');section.append(element('h4',labels[result.mode]),element('p',`Known as of ${o.instant(result.knownAt)} · recorded ${o.instant(result.recordedAt)}`));
   if(result.mode==='review')section.append(element('p','The task prepared a proposal without recording financial effects. Open Lifecycle, select this recorded close and review the occurrence against its current native state before recording it.'));
   const details=element('details');details.append(element('summary','Retained occurrence evidence'));const body=element('pre');body.textContent=JSON.stringify({runId:result.runId,sourceContextId:result.sourceContextId,policy:{id:report.definition.policyId,revision:report.definition.policyRevision},settings:report.definition.settings,proposal:result.proposal,outcome:result.outcome},null,2);details.append(body);section.append(details);host.append(section);
  }
  if(o.openLifecycle){const open=element('button','Open Lifecycle');open.type='button';open.className='secondary';open.disabled=o.busy();open.onclick=()=>{if(!o.busy())o.openLifecycle!();};host.append(open);}
 }
 return {async load(id:string){const version=++generation,actor=o.identity();const value=await o.api<Report>('jobs/lifecycle/'+encodeURIComponent(id));if(version!==generation||actor!==o.identity())return;
   if(value?.job?.id!==id||!value.close?.id||!value.close.at||!Array.isArray(value.results)||value.results.some(r=>!['manual','review','automatic'].includes(r.mode)||!r.runId||!r.sourceContextId||!r.knownAt))throw Error('The lifecycle task reply did not retain its exact source and occurrence. Refresh Jobs.');report=value;draw();},
  reset(){generation++;report=undefined;host.replaceChildren();host.hidden=true;},refresh:draw};
}
