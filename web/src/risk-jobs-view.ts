import type {RiskJob} from './risk-view.js';

type Job=RiskJob;
/** Current authorized job metadata; results are fetched explicitly from their retained context. */
export function setupRiskJobs(options:{api:<T>(path:string)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;instant:(at:string)=>string;openReport?:(id:string)=>Promise<void>}){
 const get=(id:string)=>document.getElementById(id)!;
 let generation=0,recent:Job[]=[];
 const cell=(tag:string,text:string)=>{const node=document.createElement(tag);node.textContent=text;return node;};
 function reset(){generation++;recent=[];get('risk-jobs-list').replaceChildren();get('risk-jobs-report').replaceChildren();}
 async function report(id:string){
  if(!options.openReport)return;
  await options.openReport(id);
 }
 async function load(){
  reset();const version=generation;get('risk-jobs-list').textContent='Loading risk jobs…';
  try{
   const jobs=await options.api<Job[]>('portfolio/risk/jobs');if(version!==generation)return;
   recent=jobs;const root=get('risk-jobs-list');root.replaceChildren();
   if(!jobs.length){root.textContent='No retained risk jobs are visible to this account.';return;}
   const table=document.createElement('table'),head=document.createElement('tr');
   for(const title of ['Run','Job','State','Market as of','Finished','Report'])head.append(cell('th',title));
   const thead=document.createElement('thead');thead.append(head);table.append(thead);const body=document.createElement('tbody');
   for(const job of jobs){
    const row=document.createElement('tr');for(const text of [job.run?`${job.run.id} · part ${job.run.part}/${job.run.parts}`:'Single job',job.id,job.state,job.asOf?options.instant(job.asOf):'Not recorded',job.finishedAt?options.instant(job.finishedAt):'—'])row.append(cell('td',text));
    const action=cell('td',''),button=cell('button','View details / report') as HTMLButtonElement;button.type='button';button.className='secondary';button.dataset.riskJob=job.id;button.onclick=()=>void options.act(()=>report(job.id));action.append(button);row.append(action);body.append(row);
   }
   table.append(body);root.append(table);
  }catch(error){if(version===generation)get('risk-jobs-list').textContent='Risk jobs could not be loaded. Refresh to try again.';throw error;}
 }
 get('risk-jobs-refresh').onclick=()=>void options.act(load);
 return {load,reset,recent:()=>recent};
}
