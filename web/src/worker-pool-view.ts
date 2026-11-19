type WorkerTask={id:string;node:string;engine:string|null;seen:string|null;state:string};
export type WorkerPool={clusterId:string;maximum:number;desired:number;observed:number|null;observedAt:string|null;
  generation:number;diagnostic:string|null;canScale:boolean;tasks:WorkerTask[]};
type LogEntry={id:number;source:string;message:string;at:string};
type Options={instant:(at:string)=>string;busy:()=>boolean;act:(work:()=>Promise<void>)=>Promise<void>;
  api:<T>(path:string)=>Promise<T>;scale:(pool:WorkerPool,replicas:number)=>Promise<void>};

export function setupWorkerPool(options:Options){
  const panel=document.getElementById('jobs-worker-pool')!;
  const log=document.getElementById('jobs-infra-log')!;
  let generation=0,panelVersion=0,timer:ReturnType<typeof setTimeout>|undefined;
  function cancelPoll(){panelVersion++;clearTimeout(timer);}
  async function refresh(version:number){
    if(version!==panelVersion||document.getElementById('jobs-page')?.hidden)return;
    try{
      const value=await options.api<{pool:WorkerPool|null;canReadLogs:boolean}>('infrastructure/pool');
      if(version!==panelVersion)return;
      show(value.pool,value.canReadLogs);
    }catch{
      if(version!==panelVersion)return;
      clear();append(panel,'p','Worker status unavailable. Refresh Jobs to retry.');
    }
  }
  function clear(){cancelPoll();generation++;panel.replaceChildren();log.replaceChildren();}
  function append(parent:HTMLElement,tag:string,value:string){
    const element=document.createElement(tag);element.textContent=value;parent.append(element);return element;
  }
  function button(label:string,action:()=>Promise<void>){
    const element=document.createElement('button');element.type='button';element.className='secondary';element.textContent=label;
    element.onclick=()=>{if(!options.busy())void options.act(action);};panel.append(element);
  }
  async function logs(stream:'worker'|'node'){
    const version=++generation;log.replaceChildren();const entries=await options.api<LogEntry[]>('infrastructure/logs/'+stream);
    if(generation!==version)return;
    append(log,'h4',stream==='worker'?'Worker application log':'Swarm node events');
    if(!entries.length)append(log,'p','No entries recorded.');
    for(const item of entries)append(log,'p',options.instant(item.at)+' · '+item.source+' · '+item.message);
  }
  function show(pool:WorkerPool|null,canReadLogs:boolean){
    cancelPoll();
    if(!canReadLogs){generation++;log.replaceChildren();}
    panel.replaceChildren();
    if(pool){const version=panelVersion;timer=setTimeout(()=>void refresh(version),5000);}
    if(!pool)append(panel,'p','No managed risk worker pool is registered.');
    else{
      append(panel,'p',`Requested: ${pool.desired} · Swarm replicas: ${pool.observed??'unknown'} · Idle: ${pool.tasks.filter(t=>t.state==='idle').length} · Busy: ${pool.tasks.filter(t=>t.state==='busy').length}`);
      if(pool.observedAt)append(panel,'p','Last controller observation: '+options.instant(pool.observedAt));
      if(pool.diagnostic)append(panel,'p',pool.diagnostic.replaceAll('_',' '));
      for(const task of pool.tasks){
        append(panel,'p',`${task.node} · ${task.state} · ${task.seen?'Last heartbeat '+options.instant(task.seen):'Awaiting heartbeat'}`);
        if(task.engine)append(panel,'small','Engine '+task.engine);
      }
      if(pool.canScale){
        for(let count=1;count<=pool.maximum;count++)button('Run '+count+(count===1?' worker':' workers'),()=>options.scale(pool,count));
        button('Stop after current jobs',()=>options.scale(pool,0));
      }
    }
    if(canReadLogs){button('Worker app logs',()=>logs('worker'));button('Node events',()=>logs('node'));}
  }
  return {clear,show};
}
