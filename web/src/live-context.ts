import type {Context} from './date-editor.js';
import type {FrameSummary} from './market-navigation.js';

export type LiveSelection={id:string;generation:number;policy:'selected_facts_latest_committed_corrections_v1'};
export type LiveContextUpdate={live:LiveSelection;context:Context;frame:FrameSummary};
export type LiveStatus={state:'updating'|'live'|'stale';updatedAt?:Date};

// UI scheduling only: one in-flight render and one newest pending generation.
// The browser receives context summaries, never the backend dependency graph.
export class LiveUpdates<T extends {generation:number}> {
 private pending?:T;
 private running=false;
 private stopped=false;
 private connected=true;
 private applied=0;
 private newest=0;
 private failed=false;
 private updatedAt?:Date;
 private cancel?:()=>void;
 constructor(private readonly options:{apply:(value:T)=>Promise<void>;busy:()=>boolean;status:(value:LiveStatus)=>void;
  schedule:(work:()=>void)=>()=>void}){}
 offer(value:T,force=false){
  if(this.stopped||!Number.isSafeInteger(value.generation)||value.generation<1||value.generation<this.newest)return;
  if(value.generation===this.applied&&!this.failed&&!force){this.status();return;}
  this.newest=value.generation;this.pending=value;this.status();this.schedule();
 }
 disconnected(){if(this.stopped)return;this.connected=false;this.status();}
 reconnected(){if(this.stopped)return;this.connected=true;this.status();}
 stop(){this.stopped=true;this.pending=undefined;this.cancel?.();}
 private status(){if(!this.stopped)this.options.status({state:!this.connected||this.failed?'stale':this.running||this.pending?'updating':'live',updatedAt:this.updatedAt});}
 private schedule(){
  if(this.stopped||this.running||this.cancel)return;
  this.cancel=this.options.schedule(()=>{this.cancel=undefined;void this.run();});
 }
 private async run(){
  if(this.stopped||!this.pending)return;
  if(this.options.busy()){this.schedule();return;}
  const value=this.pending;this.pending=undefined;this.running=true;this.failed=false;this.status();
  try{await this.options.apply(value);if(!this.stopped){this.applied=value.generation;this.updatedAt=new Date();}}
  catch{this.failed=true;}
  finally{this.running=false;this.status();if(this.pending)this.schedule();}
 }
}

export function liveContextUpdate(value:unknown,id:string):LiveContextUpdate {
 const item=value as LiveContextUpdate;
 if(!item||item.live?.id!==id||item.live.policy!=='selected_facts_latest_committed_corrections_v1'||
  !Number.isSafeInteger(item.live.generation)||item.live.generation<1||!item.context?.contextId||!item.context.knownAt||
  !item.context.effectiveOn||!item.frame?.id||!item.frame.at)throw Error('The live context update is incomplete.');
 return item;
}
