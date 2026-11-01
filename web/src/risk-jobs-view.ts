import type {RiskReport} from './risk-report.js';

type Job={id:string;state:string;asOf?:string;finishedAt?:string;run?:{id:string;part:number;parts:number}|null;result?:RiskReport;error?:{message:string}};
/** Current authorized job metadata; results are fetched explicitly from their retained context. */
export function setupRiskJobs(options:{api:<T>(path:string)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;instant:(at:string)=>string}){
 const get=(id:string)=>document.getElementById(id)!;
 let generation=0;
 const cell=(tag:string,text:string)=>{const node=document.createElement(tag);node.textContent=text;return node;};
 function reset(){generation++;get('risk-jobs-list').replaceChildren();get('risk-jobs-report').replaceChildren();}
 async function report(id:string){
  const version=++generation;get('risk-jobs-report').replaceChildren();
  const job=await options.api<Job>(`portfolio/risk/jobs/${encodeURIComponent(id)}`);if(version!==generation)return;
  const root=get('risk-jobs-report');root.append(cell('h3',`Job ${job.id} · ${job.state}`));
  if(!job.result){root.append(cell('p',job.error?.message??'No retained report is available for this job.'));return;}
  const result=job.result;
  root.append(cell('p',`Retained context ${result.context.contextId} · snapshot ${result.snapshotId}`));
  if(!Array.isArray(result.excludedPositions)){root.append(cell('p','This report does not record excluded positions and cannot be displayed.'));return;}
  root.append(cell('p',`${result.excludedPositions.length} excluded positions${result.excludedPositions.length?': '+result.excludedPositions.map(p=>p.tradeReference).join(', '):'.'}`));
  for(const factor of result.factors){
   root.append(cell('h4',`${factor.factor.label} · bump ${factor.bump} ${factor.factor.unit}`));
   const table=document.createElement('table'),head=document.createElement('tr');
   for(const title of ['Book','Measure','Currency','Base PV','Up P&L','Down P&L','Delta','Delta unit','Gamma','Gamma unit'])head.append(cell('th',title));
   const thead=document.createElement('thead');thead.append(head);table.append(thead);const body=document.createElement('tbody');
   for(const total of factor.totals){const row=document.createElement('tr'),v=total.values;for(const text of [total.book,total.measureLabel,v.currency,v.display.base,v.display.upPnl,v.display.downPnl,v.display.delta,v.deltaUnit,v.display.gamma,v.gammaUnit])row.append(cell('td',text));body.append(row);}
   table.append(body);const wrap=cell('div','');wrap.className='table-wrap';wrap.append(table);root.append(wrap);
  }
  const download=cell('button','Download retained report') as HTMLButtonElement;download.type='button';download.className='secondary';
  download.onclick=()=>{const url=URL.createObjectURL(new Blob([JSON.stringify(job,null,2)],{type:'application/json'}));const link=document.createElement('a');link.href=url;link.download=`risk-${job.id}.json`;link.click();URL.revokeObjectURL(url);};root.append(download);
 }
 async function load(){
  reset();const version=generation;get('risk-jobs-list').textContent='Loading risk jobs…';
  try{
   const jobs=await options.api<Job[]>('portfolio/risk/jobs');if(version!==generation)return;
   const root=get('risk-jobs-list');root.replaceChildren();
   if(!jobs.length){root.textContent='No retained risk jobs are visible to this account.';return;}
   const table=document.createElement('table'),head=document.createElement('tr');
   for(const title of ['Run','Job','State','Market as of','Finished','Report'])head.append(cell('th',title));
   const thead=document.createElement('thead');thead.append(head);table.append(thead);const body=document.createElement('tbody');
   for(const job of jobs){
    const row=document.createElement('tr');for(const text of [job.run?`${job.run.id} · part ${job.run.part}/${job.run.parts}`:'Single job',job.id,job.state,job.asOf?options.instant(job.asOf):'Not recorded',job.finishedAt?options.instant(job.finishedAt):'—'])row.append(cell('td',text));
    const action=cell('td',''),button=cell('button','View details / report') as HTMLButtonElement;button.type='button';button.className='secondary';button.onclick=()=>void options.act(()=>report(job.id));action.append(button);row.append(action);body.append(row);
   }
   table.append(body);root.append(table);
  }catch(error){if(version===generation)get('risk-jobs-list').textContent='Risk jobs could not be loaded. Refresh to try again.';throw error;}
 }
 get('risk-jobs-refresh').onclick=()=>void options.act(load);
 return {load,reset};
}
