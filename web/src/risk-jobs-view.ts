import type {RiskJob} from './risk-view.js';

type Job=RiskJob;
/** Current authorized job metadata; results are fetched explicitly from their retained context. */
export function setupRiskJobs(options:{api:<T>(path:string)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;instant:(at:string)=>string;openReport?:(id:string)=>Promise<void>}){
 const get=(id:string)=>document.getElementById(id)!;
 let generation=0,recent:Job[]=[];
 type Archive={items:(Job&{confidence:string;historyId:string;calibrationAvailable:boolean})[];next:{createdAt:string;id:string}|null};
 const cell=(tag:string,text:string)=>{const node=document.createElement(tag);node.textContent=text;return node;};
 function reset(){generation++;recent=[];get('risk-jobs-list').replaceChildren();get('risk-jobs-report').replaceChildren();}
 async function report(id:string){
  if(!options.openReport)return;
  await options.openReport(id);
 }
 async function archive(version:number){
  const root=get('risk-jobs-report'),panel=document.createElement('details');panel.id='risk-parametric-archive';panel.append(cell('summary','Completed VaR / ES archive'));
  const note=cell('p','Completed statistical reports under current authority, newest first. Each report retains its original scope and calibration.');
  const table=document.createElement('table'),head=document.createElement('tr');for(const title of ['Method','Market as of','Confidence','Coverage','History','Report'])head.append(cell('th',title));
  const thead=document.createElement('thead');thead.append(head);table.append(thead);const body=document.createElement('tbody');table.append(body);
  const wrap=cell('div','');wrap.className='table-wrap';wrap.append(table);const more=cell('button','Load more reports') as HTMLButtonElement;more.type='button';more.className='secondary';let cursor:Archive['next']=null;
  async function page(){
   more.disabled=true;try{
    const loaded=await options.api<Archive>('portfolio/risk/statistical/archive?limit=25'+(cursor?'&before='+encodeURIComponent(cursor.createdAt)+'&id='+encodeURIComponent(cursor.id):''));if(version!==generation)return;
    if(!Array.isArray(loaded.items)||loaded.items.some(j=>j.state!=='completed'||!['parametric','historical','monte_carlo'].includes(j.kind??'')))throw Error('The statistical archive is inconsistent. Refresh Reports and runs.');
    for(const job of loaded.items){const row=document.createElement('tr');for(const value of [job.kind==='monte_carlo'?'Monte Carlo':job.kind==='historical'?'Historical simulation':'Parametric',job.asOf?options.instant(job.asOf):'Unavailable',job.confidence,job.calibrationAvailable?'Available':'Unavailable',job.historyId])row.append(cell('td',value));
     const action=cell('td',''),button=cell('button','Open VaR / ES report') as HTMLButtonElement;button.type='button';button.dataset.riskJob=job.id;button.onclick=()=>void options.act(()=>report(job.id));action.append(button);row.append(action);body.append(row);}
    cursor=loaded.next;more.hidden=!cursor;note.textContent=body.rows.length?'Completed statistical reports under current authority. Each report retains its own scope and calibration.':'No completed statistical reports are visible to this account.';
   }catch(error){if(version===generation){body.replaceChildren();note.textContent='The statistical archive could not be read. Refresh Reports and runs.';more.hidden=true;}throw error;}finally{more.disabled=false;}
  }
  more.onclick=()=>void options.act(page);panel.append(note,wrap,more);root.replaceChildren(panel);await page();
 }
 async function load(){
  reset();const version=generation;get('risk-jobs-list').textContent='Loading risk jobs…';
  try{
   const jobs=await options.api<Job[]>('portfolio/risk/jobs');if(version!==generation)return;
   recent=jobs;const root=get('risk-jobs-list');root.replaceChildren();
   if(!jobs.length)root.textContent='No recent risk jobs are visible to this account.';
   const table=document.createElement('table'),head=document.createElement('tr');
   for(const title of ['Run','Kind','State','Market as of','Finished','Report'])head.append(cell('th',title));
   // A run of several parts is numbered by its place in this list, so its parts read together; no key is shown.
   const runs=[...new Set(jobs.flatMap(j=>j.run?[j.run.id]:[]))];
   const thead=document.createElement('thead');thead.append(head);table.append(thead);const body=document.createElement('tbody');
   for(const job of jobs){
    const row=document.createElement('tr');for(const text of [job.run?`Run ${runs.indexOf(job.run.id)+1} · part ${job.run.part} of ${job.run.parts}`:'Single run',job.kind==='monte_carlo'?'Monte Carlo VaR / ES':job.kind==='historical'?'Historical VaR / ES':job.kind==='parametric'?'Parametric VaR / ES':'Sensitivities',job.state,job.asOf?options.instant(job.asOf):'Not recorded',job.finishedAt?options.instant(job.finishedAt):'—'])row.append(cell('td',text));
    const action=cell('td',''),button=cell('button','View details / report') as HTMLButtonElement;button.type='button';button.className='secondary';button.dataset.riskJob=job.id;button.onclick=()=>void options.act(()=>report(job.id));action.append(button);row.append(action);body.append(row);
   }
   table.append(body);const wrap=cell('div','');wrap.className='table-wrap';wrap.append(table);root.append(wrap);await archive(version);
  }catch(error){if(version===generation)get('risk-jobs-list').textContent='Risk jobs could not be loaded. Refresh to try again.';throw error;}
 }
 get('risk-jobs-refresh').onclick=()=>void options.act(load);
 return {load,reset,recent:()=>recent};
}
