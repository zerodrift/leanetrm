import {renderRegularTable} from './regular-table.js';
import {zonedInput} from './user-time.js';
import {JobRequest} from './job-requests.js';
import {setupPilotClose} from './pilot-close-view.js';
import {setupPilotCloseReport} from './pilot-close-report.js';

type Job={id:string;name:string;operation:string;operationName?:string;pilotReport?:{runId:string;state:string}|null;state:string;version:number;dueAt:string;expiresAt:string;intervalSeconds:number|null;timeZone:string;misfire:string;overlap:string;timeoutSeconds:number;maximumAttempts:number;profileId:string|null;profileVersion:number|null;profileName:string|null;canEdit:boolean;editReason:string|null;owner:string;mine:boolean;actions:string[];runs:{id?:string;state:string;startedAt:string;attempt:number;observation:string|null;errorCode:string|null}[]};
type Node={clusterId:string;id:string;version:number;name:string;role:string;state:string;availability:string;cpuNano:number;memoryBytes:number;observedAt:string;stale:boolean;actions:string[]};
type CollectionJob={id:string;generation:string;name:string;provider:string;operation:string;serviceName:string;state:string;enabled:boolean;scheduleEnabled:boolean;cron:string;scheduleZone:string;cadence:string;fromDay:number;throughDay:number;sourceCount:number;canEdit:boolean;editReason:string|null;lastAttempt:{state:string;startedAt:string;finishedAt:string|null;errorCode:string|null}|null};
type CollectionDirectory={page:number;total:number;jobs:CollectionJob[];settings:{minimumDayOffset:number;maximumDayOffset:number;maximumNameLength:number}};
type Directory={page:number;total:number;jobs:Job[];collectionJobs:CollectionDirectory;profiles:{id:string;version:number;name:string}[];canCreate:boolean;manageAll:boolean;observedAt:string;infrastructure:{state:string;observedAt:string|null;nodes:Node[];commands:{id:string;nodeId:string;availability:string;state:string;errorCode:string|null;createdAt:string;canResolve:boolean}[]}};
export function setupJobs(options:{api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;identity:()=>string;instant:(at:string)=>string;zone:()=>string}){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
  const dueInput=zonedInput(get<HTMLInputElement>('job-due'),options.zone,options.api),expiryInput=zonedInput(get<HTMLInputElement>('job-expires'),options.zone,options.api);
  const pending=new JobRequest();
  let directory:Directory|undefined;
  let selected:Job|undefined,selectedCollection:CollectionJob|undefined,generation=0;
  const pilotClose=setupPilotClose({...options,pending:()=>!!pending.command,mutate});
  const pilotReport=setupPilotCloseReport(options);
  const editorStatus=document.createElement('p');editorStatus.id='job-edit-status';editorStatus.className='field-help';editorStatus.setAttribute('role','status');get('job-editor-title').after(editorStatus);
  const key=()=>`openetrm.jobs.request.${options.identity()}`;
  function save(){try{if(pending.command)sessionStorage.setItem(key(),JSON.stringify(pending.command));else sessionStorage.removeItem(key());}catch{/* The in-memory request is retained. */}get('job-pending').hidden=!pending.command;}
  function closeEditor(){selected=undefined;get<HTMLFormElement>('job-create').reset();for(const option of get('job-operation').querySelectorAll('[data-retained]'))option.remove();dueInput.clear();expiryInput.clear();editorStatus.textContent='';get('job-editor-title').textContent='New job';get('job-submit').textContent='Create job';get('job-submit').hidden=false;get('job-cancel-edit').hidden=true;get('job-create').hidden=!directory?.canCreate;get<HTMLDetailsElement>('job-editor').open=false;get('job-editor').hidden=!directory?.canCreate;fields();sync();}
  function closeCollectionEditor(){selectedCollection=undefined;get<HTMLFormElement>('collection-job-form').reset();get<HTMLDetailsElement>('collection-job-editor').open=false;get('collection-job-editor').hidden=true;get('collection-job-status').textContent='';get('collection-job-cadence').textContent='';sync();}
  function clear(){directory=undefined;closeEditor();closeCollectionEditor();pilotReport.reset();for(const id of ['jobs-table','job-runs','jobs-nodes','jobs-node-commands','collection-jobs-table'])get(id).replaceChildren();for(const id of ['jobs-page-count','collection-jobs-page-count'])get(id).textContent='';get<HTMLButtonElement>('jobs-previous').disabled=true;get<HTMLButtonElement>('jobs-next').disabled=true;get('jobs-infrastructure').textContent='Status unavailable.';}
  function openEditor(job:Job){
    if(options.busy()||!directory?.jobs.includes(job))return;
    closeCollectionEditor();
    selected=job;get('job-create').hidden=false;get('job-editor').hidden=false;get<HTMLDetailsElement>('job-editor').open=true;
    get('job-editor-title').textContent=job.canEdit?'Edit job':'Job details';get('job-submit').textContent='Save job';get('job-submit').hidden=!job.canEdit;get('job-cancel-edit').hidden=false;
    get('job-cancel-edit').textContent=job.canEdit?'Cancel edit':'Close details';
    editorStatus.textContent=`${job.owner} · ${job.state}. ${job.canEdit?'Changes apply to future runs. Ownership, operation and approved image remain fixed.':job.editReason??'These retained terms are read-only.'}`;
    field('job-name').value=job.name;
    const operations=get<HTMLSelectElement>('job-operation');if(![...operations.options].some(o=>o.value===job.operation)){const retained=new Option(job.operationName??'Retained operation',job.operation);retained.disabled=true;retained.dataset.retained='true';operations.add(retained);}
    field('job-operation').value=job.operation;dueInput.set(job.dueAt);expiryInput.set(job.expiresAt);
    field('job-frequency').value=job.intervalSeconds===null?'once':'interval';field('job-interval').value=job.intervalSeconds===null?'':String(job.intervalSeconds);
    field('job-timeout').value=String(job.timeoutSeconds);field('job-attempts').value=String(job.maximumAttempts);
    const profile=job.profileId===null?'':`${job.profileId}:${job.profileVersion}`;
    const profiles=get<HTMLSelectElement>('job-profile');if(profile&&![...profiles.options].some(p=>p.value===profile))profiles.add(new Option(job.profileName??'Retained approved image',profile));profiles.value=profile;
    fields();sync();get('job-editor-title').scrollIntoView({block:'nearest'});if(job.canEdit)field('job-name').focus();
  }
  function openCollectionEditor(job:CollectionJob){
    if(options.busy()||!directory?.collectionJobs.jobs.includes(job))return;
    closeEditor();selectedCollection=job;get('collection-job-editor').hidden=false;get<HTMLDetailsElement>('collection-job-editor').open=true;
    get('collection-job-editor-title').textContent=job.canEdit?'Edit collection schedule':'Collection job details';
    field('collection-job-name').value=job.name;field('collection-job-schedule').value=String(job.scheduleEnabled);field('collection-job-from').value=String(job.fromDay);field('collection-job-through').value=String(job.throughDay);
    get('collection-job-cadence').textContent=`${job.provider} · ${job.cadence} · ${job.serviceName}`;
    get('collection-job-status').textContent=job.canEdit?'Changes affect future collections. The registered service, sources and daily cadence remain fixed.':job.editReason??'These collection settings are read-only.';
    get('collection-job-save').hidden=!job.canEdit;sync();get('collection-job-editor-title').scrollIntoView({block:'nearest'});if(job.canEdit)field('collection-job-name').focus();
  }
  async function send(){
    const command=pending.command,identity=options.identity();if(!command)return;
    const previouslyUncertain=pending.sending();save();
    try{
      const body=command.path==='jobs/pilot-close/create'?{...command.body,schedule:{...(command.body.schedule as Record<string,unknown>),requestId:command.operationId}}:{...command.body,requestId:command.operationId};
      const result=await options.api<Record<string,unknown>>(command.path,body);
      if(identity!==options.identity()||pending.command!==command)return;
      if(command.path==='jobs/collection/update'&&(!result||result.requestId!==command.operationId||result.id!==command.body.id||typeof result.generation!=='string'||result.generation===command.body.expectedGeneration||!['name','scheduleEnabled','fromDay','throughDay'].every(key=>result[key]===command.body[key])))
        throw Object.assign(Error('The collection update has an unconfirmed response. Retry the retained request.'),{code:'response_unknown'});
      if(['jobs/pilot-close/create','market/closes/register'].includes(command.path)&&(!result||result.requestId!==command.operationId||typeof result.id!=='string'||!result.id))
        throw Object.assign(Error('The pilot request has an unconfirmed response. Retry the retained request.'),{code:'response_unknown'});
    }
    catch(error){if(identity!==options.identity()||pending.command!==command)return;clear();pilotClose.reset();const code=(error as {code?:string}).code;pending.failed(code,previouslyUncertain);save();throw error;}
    pending.clear();save();await load();
  }
  async function mutate(path:string,body:Record<string,unknown>){pending.begin(path,body);save();await send();}
  function button(label:string,job:Job,action:string){const b=document.createElement('button');b.type='button';b.className='secondary';b.textContent=label;b.onclick=()=>void options.act(()=>mutate('jobs/control',{id:job.id,action,expectedVersion:job.version}));return b;}
  function render(){
    if(!directory)return;
    get('job-create').hidden=!selected&&!directory.canCreate;
    get('job-editor').hidden=!selected&&!directory.canCreate;
    get('jobs-page-count').textContent=`Showing ${directory.jobs.length} of ${directory.total} jobs — page ${directory.page+1}.`;
    get<HTMLButtonElement>('jobs-previous').disabled=directory.page===0;get<HTMLButtonElement>('jobs-next').disabled=(directory.page+1)*100>=directory.total;
    get('jobs-scope').textContent=directory.manageAll?'You can manage all jobs in your authorized scope.':'Your jobs and schedules.';
    get('jobs-infrastructure').textContent=directory.infrastructure.state==='not_configured'?'Swarm is not configured for this workspace.':directory.infrastructure.observedAt?`Latest node snapshot ${options.instant(directory.infrastructure.observedAt)}.`:'No node observation is available.';
    renderRegularTable(get('jobs-nodes'),'jobs-nodes',options.identity(),['Node','Role','Health','Availability','CPUs','Memory (MiB)','Observed','Actions'],directory.infrastructure.nodes.map(n=>{
      const actions=document.createElement('div');const labels:Record<string,string>={pause:'Pause scheduling',active:'Resume scheduling',drain:'Drain idle node'};
      for(const a of n.actions){if(!labels[a])continue;const b=document.createElement('button');b.type='button';b.className='secondary';b.textContent=labels[a];b.onclick=()=>void options.act(()=>mutate('infrastructure/availability',{clusterId:n.clusterId,nodeId:n.id,expectedVersion:n.version,availability:a}));actions.append(b);}
      return[n.name,n.role,n.stale?'Stale observation':n.state,n.availability,String(n.cpuNano/1e9),String(n.memoryBytes/1048576),options.instant(n.observedAt),actions];
    }),[4,5],options.busy);
    renderRegularTable(get('jobs-node-commands'),'jobs-node-commands',options.identity(),['Node','Requested availability','Outcome','Details','Requested','Reconciliation'],(directory.infrastructure.commands??[]).map(c=>{const node=directory!.infrastructure.nodes.find(n=>n.id===c.nodeId);const action=document.createElement('div');if(c.canResolve&&node&&!node.stale){const b=document.createElement('button');b.type='button';b.textContent='Keep observed state and reconcile';b.onclick=()=>void options.act(()=>mutate('infrastructure/resolve',{id:c.id,expectedVersion:node.version}));action.append(b);}return[node?.name??'Unavailable node',c.availability,c.state,c.errorCode??'',options.instant(c.createdAt),action];}),[],options.busy);
    const labels:Record<string,string>={worker_check:'Worker readiness check',swarm_job:'Approved container job'};
    renderRegularTable(get('jobs-table'),'jobs',options.identity(),['Job','Owner','Operation','State','Next due','Expires','Actions'],directory.jobs.map(j=>{
      const actions=document.createElement('div');
      const edit=document.createElement('button');edit.type='button';edit.className='secondary';edit.textContent=j.canEdit?'Edit schedule':'View details';edit.setAttribute('aria-label',`${edit.textContent} for ${j.name}`);edit.onclick=()=>openEditor(j);actions.append(edit);
      if(j.pilotReport?.state==='report_published'){const report=document.createElement('button');report.type='button';report.className='secondary';report.textContent='View close report';report.onclick=()=>void options.act(()=>pilotReport.load(j.pilotReport!.runId));actions.append(report);}
      for(const a of j.actions){const labels:Record<string,string>={pause:'Pause',resume:'Resume',cancel:'Cancel',run_again:'Run again'};if(labels[a])actions.append(button(labels[a],j,a));}
      if(!j.canEdit&&j.editReason){const reason=document.createElement('p');reason.className='field-help';reason.textContent=j.editReason;actions.append(reason);}
      const name=document.createElement('button');name.type='button';name.className='text-button';name.textContent=j.name;name.onclick=()=>openEditor(j);
      return[name,j.owner,j.operationName??labels[j.operation]??'Retained operation',j.state,options.instant(j.dueAt),options.instant(j.expiresAt),actions];
    }),[],options.busy);
    renderRegularTable(get('job-runs'),'job-runs',options.identity(),['Job','Started','Outcome','Attempt','Details'],directory.jobs.flatMap(j=>j.runs.map(r=>[j.name,options.instant(r.startedAt),r.state,String(r.attempt),r.observation==='unknown'?'Outcome unknown; intervention required':r.errorCode??r.observation??''])),[3],options.busy);
    const collection=directory.collectionJobs,settings=collection.settings;
    if(!settings||![settings.minimumDayOffset,settings.maximumDayOffset,settings.maximumNameLength].every(Number.isSafeInteger))throw Error('Collection settings are incomplete. Reload jobs before editing.');
    for(const id of ['collection-job-from','collection-job-through']){field(id).setAttribute('min',String(settings.minimumDayOffset));field(id).setAttribute('max',String(settings.maximumDayOffset));}
    get('collection-job-name-limit').textContent=`Maximum ${settings.maximumNameLength} characters.`;
    get('collection-jobs-page-count').textContent=`Showing ${collection.jobs.length} of ${collection.total} collection jobs — page ${collection.page+1}.`;
    renderRegularTable(get('collection-jobs-table'),'collection-jobs',options.identity(),['Job','Operation','State','Cadence','Delivery day offsets','Sources','Latest attempt','Actions'],collection.jobs.map(job=>{
      const action=document.createElement('div'),edit=document.createElement('button');edit.type='button';edit.className='secondary';edit.textContent=job.canEdit?'Edit schedule':'View details';edit.setAttribute('aria-label',`${job.canEdit?'Edit collection schedule':'View collection details'} for ${job.name}`);edit.onclick=()=>openCollectionEditor(job);action.append(edit);
      if(!job.canEdit&&job.editReason){const reason=document.createElement('p');reason.className='field-help';reason.textContent=job.editReason;action.append(reason);}
      const latest=job.lastAttempt;
      return[job.name,`${job.provider} delivery-price collection`,job.state,job.cadence,`${job.fromDay} through ${job.throughDay}`,String(job.sourceCount),latest?`${latest.state} · ${options.instant(latest.startedAt)}${latest.errorCode?' · '+latest.errorCode:''}`:'No attempts recorded',action];
    }),[5],options.busy);
    const profiles=get<HTMLSelectElement>('job-profile'),profile=selected?.profileId?`${selected.profileId}:${selected.profileVersion}`:profiles.value;
    profiles.replaceChildren(new Option('Choose an approved image…',''),...directory.profiles.map(p=>new Option(p.name,`${p.id}:${p.version}`)));
    if(selected?.profileId&&![...profiles.options].some(p=>p.value===profile))profiles.add(new Option(selected.profileName??'Retained approved image',profile));
    profiles.value=[...profiles.options].some(p=>p.value===profile)?profile:'';
  }
  async function load(page=0,collectionPage=0){const request=++generation,identity=options.identity();clear();const result=await options.api<Directory>(`jobs?page=${page}&collectionPage=${collectionPage}`);if(request!==generation||identity!==options.identity())return;directory=result;render();sync();}
  function fields(){field('job-attempts').setAttribute('max',field('job-operation').value==='swarm_job'?'1':'3');get('job-interval-field').hidden=field('job-frequency').value!=='interval';get('job-profile-field').hidden=field('job-operation').value!=='swarm_job';}
  field('job-frequency').onchange=fields;field('job-operation').onchange=fields;
  get('jobs-refresh').onclick=()=>void options.act(()=>load());
  get('jobs-previous').onclick=()=>void options.act(()=>load(Math.max(0,(directory?.page??0)-1)));
  get('jobs-next').onclick=()=>void options.act(()=>load((directory?.page??0)+1));
  get('collection-jobs-previous').onclick=()=>void options.act(()=>load(directory?.page??0,Math.max(0,(directory?.collectionJobs.page??0)-1)));
  get('collection-jobs-next').onclick=()=>void options.act(()=>load(directory?.page??0,(directory?.collectionJobs.page??0)+1));
  get('collection-job-close').onclick=()=>{if(!options.busy())closeCollectionEditor();};
  get<HTMLFormElement>('collection-job-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    if(pending.command)throw Error('Reconcile the outstanding job request first.');
    const job=selectedCollection;if(!job?.canEdit)throw Error('Choose an editable collection job.');
    const schedule=field('collection-job-schedule').value;if(!['true','false'].includes(schedule))throw Error('Choose the collection schedule state.');
    await mutate('jobs/collection/update',{id:job.id,expectedGeneration:job.generation,name:field('collection-job-name').value,scheduleEnabled:schedule==='true',fromDay:Number(field('collection-job-from').value),throughDay:Number(field('collection-job-through').value)});
  });};
  get('job-retry-request').onclick=()=>void options.act(send);
  get('job-discard-request').onclick=()=>{pending.clear();save();sync();};
  get('job-cancel-edit').onclick=()=>{if(!options.busy())closeEditor();};
  get<HTMLFormElement>('job-create').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    if(pending.command)throw Error('Reconcile the outstanding job request first.');
    if(selected&&!selected.canEdit)throw Error('This job is read-only. Refresh its current status.');
    const frequency=field('job-frequency').value;if(!['once','interval'].includes(frequency))throw Error('Choose a frequency.');
    const due=field('job-due').value,expires=field('job-expires').value;
    if(!due||!expires)throw Error('Supply due and expiry times in your display time zone.');
    const profile=directory?.profiles.find(p=>`${p.id}:${p.version}`===field('job-profile').value);
    const editing=selected,identity=options.identity(),request=generation;
    const target=editing?{id:editing.id,expectedVersion:editing.version}:{operation:field('job-operation').value,profileId:profile?.id??null,profileVersion:profile?.version??null};
    const terms={name:field('job-name').value,intervalSeconds:frequency==='once'?null:Number(field('job-interval').value),
      timeZone:editing?.timeZone??'UTC',misfire:editing?.misfire??'run_once',overlap:editing?.overlap??'forbid',
      timeoutSeconds:Number(field('job-timeout').value),maximumAttempts:Number(field('job-attempts').value)};
    const dueAt=await dueInput.instant(),expiresAt=await expiryInput.instant();
    if(identity!==options.identity()||request!==generation||selected!==editing)throw Error('The job selection changed. Refresh before saving.');
    await mutate(editing?'jobs/update':'jobs/create',{...terms,dueAt,expiresAt,...target});
  });};
  function sync(){pilotClose.sync();get<HTMLButtonElement>('jobs-previous').disabled=options.busy()||!directory||directory.page===0;get<HTMLButtonElement>('jobs-next').disabled=options.busy()||!directory||(directory.page+1)*100>=directory.total;get<HTMLButtonElement>('collection-jobs-previous').disabled=options.busy()||!directory||directory.collectionJobs.page===0;get<HTMLButtonElement>('collection-jobs-next').disabled=options.busy()||!directory||(directory.collectionJobs.page+1)*100>=directory.collectionJobs.total;for(const input of get<HTMLFormElement>('job-create').querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))input.disabled=options.busy()||(input.id!=='job-cancel-edit'&&(!!pending.command||!!selected&&!selected.canEdit))||(!!selected&&['job-operation','job-profile'].includes(input.id));for(const input of get<HTMLFormElement>('collection-job-form').querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))input.disabled=options.busy()||(input.id!=='collection-job-close'&&(!!pending.command||!selectedCollection?.canEdit));}
  // Opens a job's details, as a navigation target names it: only one this page lists, which the person can see.
  function openJob(id:string){
    const job=directory?.jobs.find(j=>j.id===id);
    if(!job)throw Error('That job is not listed on this page of Jobs & Infrastructure.');
    openEditor(job);
  }
  return{load,openJob,shown:()=>selected?.id??null,sync,unconfirmed:()=>!!pending.command,contextChanged(){pilotClose.reset();pilotReport.reset();},refresh(){dueInput.refresh();expiryInput.refresh();pilotClose.refresh();pilotReport.refresh();render();sync();},reset(){generation++;clear();pending.clear();pilotClose.reset();pilotReport.reset();try{const stored=sessionStorage.getItem(key());if(stored){const value=JSON.parse(stored);if(['jobs/create','jobs/update','jobs/control','jobs/collection/update','jobs/pilot-close/create','market/closes/register','infrastructure/availability','infrastructure/resolve'].includes(value.path)&&typeof value.operationId==='string'&&value.body&&typeof value.body==='object')pending.command={...value,uncertain:true};}}catch{/* Invalid browser state cannot grant authority. */}fields();get('job-pending').hidden=!pending.command;sync();}};
}
