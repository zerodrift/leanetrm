import {attachReadReferences,readReferences} from './read-references.js';
const postOperations={
  evaluate:'curve.evaluate.v1',history:'curve.history.v1','market/curve-shapes':'curve.shapes.v1',
  'trading/directory':'trading.directory.v1','trading/freshness':'trading.freshness.v1','trading/positions':'trading.positions.v1',
  'trading/workspace':'trading.workspace.v1','trading/blotter':'trading.blotter.v1',
  'futures/editor':'futures.editor.v1','swaps/editor':'swaps.editor.v1','options/strategy/analyze':'options.strategy.analyze.v1',
  'portfolio/risk/catalog':'portfolio.risk.catalog.v1','portfolio/risk':'portfolio.risk.calculate.v1',
  'portfolio/risk/scope':'portfolio.risk.scope.v1','portfolio/risk/history':'portfolio.risk.history.v1'
} as const;
export type AsyncReadOperation=typeof postOperations[keyof typeof postOperations]|'portfolio.risk.jobs.list.v1'|'portfolio.risk.jobs.read.v1'|'portfolio.risk.archive.v1';
export type AsyncReadIdentity={profile:string;context:unknown};
export type AsyncReadConnectionState='idle'|'connecting'|'connected'|'disconnected'|'closed';
export type AsyncReadEventSource=Pick<EventSource,'addEventListener'|'removeEventListener'|'close'>;
export type AsyncReadClientOptions={
  post:(path:string,body:unknown,signal?:AbortSignal)=>Promise<unknown>;
  createEventSource:(url:string)=>AsyncReadEventSource;
  getIdentity:()=>AsyncReadIdentity;
  root?:string;
  onState?:(state:AsyncReadConnectionState)=>void;
  maxActive?:number;maxQueued?:number;
  connectionTimeoutMs?:number;requestTimeoutMs?:number;cancellationTimeoutMs?:number;
};
export type AsyncReadOptions={signal?:AbortSignal;onAccepted?:()=>void};
export class AsyncReadError extends Error{
  constructor(readonly code:string,message:string,readonly source:'client'|'server'='client'){super(message);this.name='AsyncReadError';}
}
const uuid=(value:unknown):value is string=>typeof value==='string'&&/^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i.test(value)&&value!=='00000000-0000-0000-0000-000000000000';
/** Only these named business reads use the response channel. Commands retain their existing HTTP contract. */
export function asyncReadRequest(path:string,body?:unknown):{operation:AsyncReadOperation;body:unknown}|undefined{
  if(body!==undefined){const operation=Object.hasOwn(postOperations,path)?postOperations[path as keyof typeof postOperations]:undefined;return operation?{operation,body}:undefined;}
  if(path==='portfolio/risk/jobs')return {operation:'portfolio.risk.jobs.list.v1',body:{}};
  const job=/^portfolio\/risk\/jobs\/([^/?]+)$/.exec(path);
  if(job&&uuid(job[1]))return {operation:'portfolio.risk.jobs.read.v1',body:{id:job[1]}};
  const archive=/^portfolio\/risk\/(parametric|monte-carlo|historical|statistical)\/archive\?(.+)$/.exec(path);
  if(!archive)return undefined;
  const query=new URLSearchParams(archive[2]);
  if([...query.keys()].some(key=>!['limit','before','id'].includes(key))||['limit','before','id'].some(key=>query.getAll(key).length>1))return undefined;
  const limit=query.get('limit');
  if(!limit||!/^\d+$/.test(limit)||Number(limit)<1||Number(limit)>100)throw new AsyncReadError('risk_archive_input','Choose an explicit report page size from 1 to 100.');
  return {operation:'portfolio.risk.archive.v1',body:{method:archive[1].replace('-','_'),limit:Number(limit),before:query.get('before')||null,id:query.get('id')||null}};
}
const protocol='async_reads_v1',maximumRequests=128,maximumRequestBytes=32768,maximumResponseCharacters=16*1024*1024+24576;
const operations=new Set<string>([...Object.values(postOperations),'portfolio.risk.jobs.list.v1','portfolio.risk.jobs.read.v1','portfolio.risk.archive.v1']);
const record=(value:unknown):value is Record<string,unknown>=>!!value&&typeof value==='object'&&!Array.isArray(value);
const text=(value:unknown):value is string=>typeof value==='string'&&value.length>0;
const protocolError=()=>new AsyncReadError('async_read_protocol','The asynchronous read response is inconsistent. Request the data again.');
const identityError=()=>new AsyncReadError('async_read_identity','The selected account or market context changed. Reload the selected view.');
function cancelled(){const error=new AsyncReadError('async_read_cancelled','The read was cancelled.');error.name='AbortError';return error;}
function canonical(value:unknown):string{
  if(Array.isArray(value))return '['+value.map(canonical).join(',')+']';
  if(record(value))return '{'+Object.keys(value).sort().map(key=>JSON.stringify(key)+':'+canonical(value[key])).join(',')+'}';
  const json=JSON.stringify(value);if(json===undefined)throw identityError();return json;
}
function identityKey(value:unknown):string{
  if(!record(value)||!text(value.profile)||!Object.hasOwn(value,'context')||!(value.context===null||record(value.context)))throw identityError();
  return canonical({profile:value.profile,context:value.context});
}
function snapshotFor(operation:AsyncReadOperation,body:Record<string,unknown>):string|null{
  if(operation==='portfolio.risk.jobs.list.v1'||operation==='portfolio.risk.jobs.read.v1'||operation==='portfolio.risk.archive.v1')return null;
  let context:unknown=body;
  if(operation==='portfolio.risk.history.v1')context=record(context)?context.scope:undefined;
  if(!operation.startsWith('curve.')&&!['trading.directory.v1','trading.freshness.v1'].includes(operation))context=record(context)?context.context:undefined;
  const snapshot=record(context)?context.snapshotId:undefined;
  if(!uuid(snapshot))throw new AsyncReadError('async_read_request','The selected read requires its explicit input snapshot.');
  return snapshot.toLowerCase();
}
type Pending={
  id:string;operation:AsyncReadOperation;body:Record<string,unknown>;snapshotId:string|null;reserved:boolean;sent:boolean;
  controller:AbortController;timer:ReturnType<typeof setTimeout>;signal?:AbortSignal;abort?:()=>void;onAccepted?:()=>void;
  resolve:(result:unknown)=>void;reject:(error:Error)=>void;
};
type Retired={operation:AsyncReadOperation;snapshotId:string|null;abandoned:boolean;timer?:ReturnType<typeof setTimeout>};
type Channel={
  id?:string;identity:string;ready:boolean;alive:boolean;count:number;renewAt:number;controller:AbortController;
  source?:AsyncReadEventSource;detach?:()=>void;timer?:ReturnType<typeof setTimeout>;
  pending:Map<string,Pending>;active:Set<string>;retired:Map<string,Retired>;
};

/** Bounded independent reads over one ephemeral stream; disconnected work is never replayed. */
export function createAsyncReadClient(options:AsyncReadClientOptions){
  const root=options.root??'/api/',maxActive=options.maxActive??2,maxQueued=options.maxQueued??32;
  const connectionTimeoutMs=options.connectionTimeoutMs??10000,requestTimeoutMs=options.requestTimeoutMs??75000,cancellationTimeoutMs=options.cancellationTimeoutMs??10000;
  if(!/^\/(?!\/)[^?#]*\/$/.test(root)||!Number.isInteger(maxActive)||maxActive<1||maxActive>2||!Number.isInteger(maxQueued)||maxQueued<0||maxQueued>32||
    [connectionTimeoutMs,requestTimeoutMs,cancellationTimeoutMs].some(value=>!Number.isFinite(value)||value<=0))throw new Error('Invalid asynchronous read client limits.');
  const clientId=crypto.randomUUID();
  let channel:Channel|undefined,closed=false,state:AsyncReadConnectionState='idle';
  const cancellations=new Map<AbortController,ReturnType<typeof setTimeout>>();
  function observe(callback:(()=>void)|undefined){try{callback?.();}catch{}}
  function setState(next:AsyncReadConnectionState){if(state!==next){state=next;observe(()=>options.onState?.(next));}}
  function cancelTransports(){for(const [controller,timer] of cancellations){clearTimeout(timer);controller.abort();}cancellations.clear();}
  function active(current:Channel){return current.alive&&channel===current;}
  function currentIdentity(){try{return identityKey(options.getIdentity());}catch{throw identityError();}}
  function requireIdentity(current:Channel){if(current.identity!==currentIdentity())throw identityError();}
  function settle(current:Channel,pending:Pending,result:unknown,error:Error|undefined,abandoned=false,waitForServer=false){
    if(current.pending.get(pending.id)!==pending)return;
    current.pending.delete(pending.id);clearTimeout(pending.timer);pending.signal?.removeEventListener('abort',pending.abort!);pending.controller.abort();
    const retired:Retired={operation:pending.operation,snapshotId:pending.snapshotId,abandoned};
    // Queued cancellations were never visible to the server and must not accumulate unbounded identity history.
    if(pending.sent)current.retired.set(pending.id,retired);
    if(waitForServer)retired.timer=setTimeout(()=>stop(current,new AsyncReadError('async_read_cancel_timeout','The cancelled read did not stop in time. Request the view again.'),'disconnected'),cancellationTimeoutMs);
    else current.active.delete(pending.id);
    if(error)pending.reject(error);else pending.resolve(result);
  }
  function stop(current:Channel,error:Error,next:AsyncReadConnectionState){
    if(!active(current))return;
    current.alive=false;channel=undefined;clearTimeout(current.timer);current.controller.abort();current.detach?.();current.source?.close();
    for(const retired of current.retired.values())clearTimeout(retired.timer);
    for(const pending of [...current.pending.values()])settle(current,pending,undefined,error,true);
    current.active.clear();cancelTransports();setState(next);
  }
  function cancelServer(current:Channel,pending:Pending){
    if(!pending.sent||!current.id)return;
    const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),connectionTimeoutMs);cancellations.set(controller,timer);
    void Promise.resolve().then(()=>options.post(root+'async-reads/cancel',{channelId:current.id,requestId:pending.id},controller.signal))
      .then(receipt=>{
        if(!active(current)||controller.signal.aborted||!record(receipt)||receipt.state!=='not_admitted')return;
        if(receipt.channelId!==current.id||receipt.requestId!==pending.id){stop(current,protocolError(),'disconnected');return;}
        const retired=current.retired.get(pending.id);
        if(!retired?.abandoned||!current.active.has(pending.id))return;
        // The server has fenced this identity against future admission. No SSE
        // terminal can exist, and this receipt cannot release another read.
        clearTimeout(retired.timer);retired.timer=undefined;current.active.delete(pending.id);pump(current);
      })
      .catch(()=>{}).finally(()=>{clearTimeout(timer);cancellations.delete(controller);});
  }
  function abandon(current:Channel,pending:Pending,error:Error){
    if(!active(current)||!current.pending.has(pending.id))return;
    // A sent cancellation keeps its slot until a terminal response or explicit
    // non-admission fence, even though the caller stops waiting now.
    settle(current,pending,undefined,error,true,pending.sent);cancelServer(current,pending);pump(current);
  }
  function makeChannel(identity:string,pending=new Map<string,Pending>()):Channel{
    const current:Channel={identity,ready:false,alive:true,count:0,renewAt:0,controller:new AbortController(),pending,active:new Set(),retired:new Map()};
    channel=current;return current;
  }
  function renew(current:Channel){
    if(!active(current))return;
    const pending=current.pending;current.pending=new Map();current.alive=false;clearTimeout(current.timer);current.controller.abort();current.detach?.();current.source?.close();
    for(const retired of current.retired.values())clearTimeout(retired.timer);
    cancelTransports();const next=makeChannel(current.identity,pending);setState('connecting');void connect(next);
  }
  function pump(current:Channel){
    if(!active(current)||!current.ready)return;
    try{requireIdentity(current);}catch(error){stop(current,error as Error,'disconnected');return;}
    if(current.count>=maximumRequests||performance.now()>=current.renewAt){
      if(!current.active.size&&current.pending.size)renew(current);
      return;
    }
    for(const pending of current.pending.values()){
      if(current.active.size>=maxActive||current.count>=maximumRequests)break;
      if(pending.reserved)continue;
      pending.reserved=true;current.active.add(pending.id);current.count++;
      void Promise.resolve().then(()=>{
        if(!active(current)||!current.pending.has(pending.id))return;
        requireIdentity(current);
        const request={channelId:current.id,requestId:pending.id,operation:pending.operation,body:pending.body};
        if(new TextEncoder().encode(JSON.stringify(request)).length>maximumRequestBytes)throw new AsyncReadError('async_read_request','The read exceeds the supported request size.');
        pending.sent=true;return options.post(root+'async-reads',request,pending.controller.signal);
      }).then(ack=>{
        if(!active(current)||!current.pending.has(pending.id))return;
        if(!record(ack)||ack.channelId!==current.id||ack.requestId!==pending.id||ack.state!=='accepted'){stop(current,protocolError(),'disconnected');return;}
        observe(pending.onAccepted);
      }).catch(error=>{
        if(!active(current)||!current.pending.has(pending.id))return;
        if(error instanceof AsyncReadError&&error.code==='async_read_identity'){stop(current,error,'disconnected');return;}
        // Only an explicit pre-admission HTTP refusal releases a submitted slot. A failure code or 5xx can follow admission.
        const code=record(error)?error.code:undefined,status=record(error)?error.status:undefined;
        if(!pending.sent||typeof status==='number'&&[400,403,409,413,422,429].includes(status)){
          settle(current,pending,undefined,error instanceof Error?error:new AsyncReadError(text(code)?code:'async_read_refused','The read was refused.'),true);pump(current);
        }else stop(current,error instanceof Error?error:new AsyncReadError('async_read_submit_failed','The read could not be submitted.'),'disconnected');
      });
    }
  }
  function response(current:Channel,event:Event){
    if(!active(current))return;
    try{
      requireIdentity(current);
      const raw=(event as MessageEvent).data;
      if(typeof raw!=='string'||raw.length>maximumResponseCharacters)throw protocolError();
      const value:unknown=JSON.parse(raw);
      if(!current.ready||!record(value)||value.protocol!==protocol||value.channelId!==current.id||!uuid(value.requestId)||
        !['completed','failed','cancelled'].includes(String(value.state)))throw protocolError();
      const pending=current.pending.get(value.requestId),retired=current.retired.get(value.requestId),expected=pending??retired;
      if(!expected||value.operation!==expected.operation||value.snapshotId!==expected.snapshotId||pending&&!pending.sent)throw protocolError();
      if(retired?.abandoned){clearTimeout(retired.timer);retired.timer=undefined;current.active.delete(value.requestId);pump(current);return;}
      if(!pending)throw protocolError();
      const result=value.result,error=value.error;
      if(value.state==='completed'){
        if(error!=null||result===null||typeof result!=='object')throw protocolError();
        settle(current,pending,attachReadReferences(result,value.readReferences),undefined);
      }else{
        if(result!=null||(error!=null&&(!record(error)||!text(error.code)||!text(error.message)))||(value.state==='failed'&&error==null))throw protocolError();
        const failure=record(error)?new AsyncReadError(error.code as string,error.message as string,value.state==='failed'?'server':'client'):cancelled();
        if(value.state==='cancelled')failure.name='AbortError';settle(current,pending,undefined,failure);
      }
      pump(current);
    }catch(error){stop(current,error instanceof AsyncReadError?error:protocolError(),'disconnected');}
  }
  async function connect(current:Channel){
    current.timer=setTimeout(()=>stop(current,new AsyncReadError('async_read_connection_timeout','The response stream did not become ready in time.'),'disconnected'),connectionTimeoutMs);
    try{
      requireIdentity(current);
      // The server starts its lifetime during the open request. Starting the
      // browser clock before sending keeps its admission window conservative.
      const opening=performance.now();
      const created=await options.post(root+'async-reads/channels',{clientId},current.controller.signal);
      if(!active(current))return;
      requireIdentity(current);
      if(!record(created)||created.protocol!==protocol||!uuid(created.channelId))throw protocolError();
      if(identityKey(created)!==current.identity)throw identityError();
      if(typeof created.admissionWindowMs!=='number'||!Number.isSafeInteger(created.admissionWindowMs)||
        created.admissionWindowMs<=connectionTimeoutMs)throw protocolError();
      current.renewAt=opening+created.admissionWindowMs;current.id=created.channelId;
      const source=options.createEventSource(root+'async-reads/events?channelId='+encodeURIComponent(current.id));current.source=source;
      const ready=(event:Event)=>{
        if(!active(current))return;
        try{
          requireIdentity(current);const raw=(event as MessageEvent).data;
          if(typeof raw!=='string'||raw.length>2048)throw protocolError();
          const value:unknown=JSON.parse(raw);
          if(current.ready||!record(value)||value.protocol!==protocol||value.channelId!==current.id)throw protocolError();
          current.ready=true;clearTimeout(current.timer);setState('connected');pump(current);
        }catch(error){stop(current,error instanceof AsyncReadError?error:protocolError(),'disconnected');}
      };
      const completed=(event:Event)=>response(current,event);
      const disconnected=()=>stop(current,new AsyncReadError('async_read_disconnected','The response stream disconnected. Request the data again.'),'disconnected');
      source.addEventListener('ready',ready);source.addEventListener('response',completed);source.addEventListener('error',disconnected);
      current.detach=()=>{source.removeEventListener('ready',ready);source.removeEventListener('response',completed);source.removeEventListener('error',disconnected);};
    }catch(error){stop(current,error instanceof Error?error:new AsyncReadError('async_read_connection_failed','The response stream could not be opened.'),'disconnected');}
  }
  function read<T>(operation:AsyncReadOperation,body:unknown,requestOptions:AsyncReadOptions={}):Promise<T>{
    if(closed)return Promise.reject(new AsyncReadError('async_read_closed','The read client is closed.'));
    if(requestOptions.signal?.aborted)return Promise.reject(cancelled());
    let captured:Record<string,unknown>,snapshotId:string|null,identity:string;
    try{
      if(!operations.has(operation)||!record(body))throw new AsyncReadError('async_read_request','Choose a supported read operation with explicit inputs.');
      const json=JSON.stringify(body);
      if(new TextEncoder().encode(json).length>maximumRequestBytes)throw new AsyncReadError('async_read_request','The read exceeds the supported request size.');
      captured=attachReadReferences(JSON.parse(json),readReferences(body));snapshotId=snapshotFor(operation,captured);identity=currentIdentity();
    }catch(error){return Promise.reject(error);}
    if(channel&&channel.identity!==identity)stop(channel,identityError(),'disconnected');
    if(channel){
      const draining=[...channel.active].filter(id=>!channel!.pending.has(id)).length;
      if(channel.pending.size+draining>=maxActive+maxQueued)return Promise.reject(new AsyncReadError('async_read_limit','Too many reads are waiting. Cancel a read or wait for a result.'));
    }
    const created=!channel,current=channel??makeChannel(identity),id=crypto.randomUUID();
    return new Promise<T>((resolve,reject)=>{
      const pending:Pending={id,operation,body:captured,snapshotId,reserved:false,sent:false,controller:new AbortController(),resolve:result=>resolve(result as T),reject,signal:requestOptions.signal,onAccepted:requestOptions.onAccepted,
        timer:setTimeout(()=>{if(channel?.pending.get(id)===pending)abandon(channel,pending,new AsyncReadError('async_read_timeout','The read did not complete in time. Request the data again.'));},requestTimeoutMs)};
      pending.abort=()=>{if(channel?.pending.get(id)===pending)abandon(channel,pending,cancelled());};
      current.pending.set(id,pending);pending.signal?.addEventListener('abort',pending.abort,{once:true});
      if(created){setState('connecting');if(active(current))void connect(current);}else pump(current);
    });
  }
  function dispose(next:AsyncReadConnectionState,code:string,message:string){
    if(channel)stop(channel,new AsyncReadError(code,message),next);else setState(next);
    cancelTransports();
  }
  return {read,close(){closed=true;dispose('closed','async_read_closed','The read client was closed.');},reset(){closed=false;dispose('idle','async_read_reset','The selected view changed. Request the data again.');}};
}
