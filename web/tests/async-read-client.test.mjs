import test from 'node:test';
import assert from 'node:assert/strict';
import {asyncReadRequest,createAsyncReadClient} from '../dist/async-read-client.js';

const protocol='async_reads_v1',admissionWindowMs=190000,snapshotId='20000000-0000-4000-8000-000000000001';
const curveBody=()=>({snapshotId,curve:'synthetic_curve',includeWorkspace:false});
const flush=()=>new Promise(resolve=>setImmediate(resolve));
function deferred(){let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no;});return {promise,resolve,reject};}
class Source extends EventTarget{
  closed=false;
  close(){this.closed=true;}
  emit(type,data){this.dispatchEvent(new MessageEvent(type,{data:typeof data==='string'?data:JSON.stringify(data)}));}
}
function harness(options={}){
  const sources=[],requests=[],cancellations=[],cancellationSignals=[],states=[],channels=[];
  const {cancelReply,...clientOptions}=options;
  let identity={profile:'synthetic_viewer',context:null};
  const root=options.root??'/api/';
  const client=createAsyncReadClient({
    post:async(path,request,signal)=>{
      if(path===root+'async-reads/channels'){
        const channelId=`10000000-0000-4000-8000-${String(channels.length+1).padStart(12,'0')}`;
        channels.push({channelId,signal,request});return {protocol,channelId,...identity,admissionWindowMs};
      }
      if(path===root+'async-reads/cancel'){cancellations.push(request);cancellationSignals.push(signal);return cancelReply?cancelReply.promise:{...request,state:'cancellation_requested'};}
      assert.equal(path,root+'async-reads');
      const ack=deferred();requests.push({body:request,ack,signal});return ack.promise;
    },
    createEventSource:url=>{const source=new Source();source.url=url;sources.push(source);return source;},
    getIdentity:()=>identity,onState:state=>states.push(state),connectionTimeoutMs:1000,requestTimeoutMs:1000,cancellationTimeoutMs:1000,...clientOptions
  });
  const ready=async()=>{await flush();const source=sources.at(-1),channelId=channels.at(-1).channelId;source.emit('ready',{protocol,channelId});await flush();return source;};
  const result=(request,overrides={})=>({protocol,channelId:request.body.channelId,requestId:request.body.requestId,operation:request.body.operation,state:'completed',snapshotId:request.body.body.snapshotId??request.body.body.context?.snapshotId??null,result:{synthetic:request.body.requestId},...overrides});
  return {client,sources,requests,cancellations,cancellationSignals,states,channels,ready,result,identity:value=>{identity=value;}};
}

test('two independent reads submit before either acknowledgement, with queued reads dispatched as slots complete',async()=>{
  const h=harness();
  try{
    const reads=Array.from({length:5},()=>h.client.read('curve.evaluate.v1',curveBody()));
    const results=Promise.all(reads);results.catch(()=>{});
    await flush();assert.equal(h.sources.length,1);assert.equal(h.requests.length,0);
    const source=await h.ready();assert.equal(h.requests.length,2,'only two execute, neither waits for an HTTP acknowledgement');
    source.emit('response',h.result(h.requests[1]));await flush();assert.equal(h.requests.length,3);
    source.emit('response',h.result(h.requests[0]));await flush();assert.equal(h.requests.length,4);
    source.emit('response',h.result(h.requests[2]));await flush();assert.equal(h.requests.length,5);
    source.emit('response',h.result(h.requests[4]));source.emit('response',h.result(h.requests[3]));
    assert.equal((await results).length,5);assert.equal(h.sources.length,1);
  }finally{h.client.close();}
});

test('dispatchers own distinct stable channel identities across disconnect and reset without replay',async()=>{
  const left=harness(),right=harness(),reads=[];
  const read=h=>{const pending=h.client.read('curve.evaluate.v1',curveBody());pending.catch(()=>{});reads.push(pending);return pending;};
  try{
    const first=read(left),other=read(right),leftSource=await left.ready(),rightSource=await right.ready();
    const clientId=left.channels[0].request.clientId;
    assert.match(clientId,/^[\da-f]{8}-[\da-f]{4}-4[\da-f]{3}-[89ab][\da-f]{3}-[\da-f]{12}$/i);
    assert.notEqual(clientId,right.channels[0].request.clientId,'separate tabs must not replace each other’s stream');
    leftSource.dispatchEvent(new Event('error'));await assert.rejects(first,{code:'async_read_disconnected'});
    assert.equal(rightSource.closed,false);assert.equal(left.requests.length,1,'disconnect does not replay the read');
    const retry=read(left);await left.ready();assert.equal(left.channels[1].request.clientId,clientId);
    left.client.reset();await assert.rejects(retry,{code:'async_read_reset'});
    const afterReset=read(left),fresh=await left.ready();assert.equal(left.channels[2].request.clientId,clientId);
    assert.equal(left.requests.length,3,'only the three explicitly requested reads were submitted');
    fresh.emit('response',left.result(left.requests[2]));rightSource.emit('response',right.result(right.requests[0]));
    await Promise.all([afterReset,other]);
  }finally{left.client.close();right.client.close();await Promise.allSettled(reads);}
});

test('route descriptors cover explicit read operations, preserve bodies and never admit commands or arbitrary paths',()=>{
  const body=curveBody();assert.deepEqual(asyncReadRequest('evaluate',body),{operation:'curve.evaluate.v1',body});
  assert.deepEqual(asyncReadRequest('options/strategy/analyze',body),{operation:'options.strategy.analyze.v1',body});
  for(const path of ['options/strategy/setup','options/positions','options/book'])assert.equal(asyncReadRequest(path,body),undefined,path);
  assert.equal(asyncReadRequest('portfolio/risk/jobs',{}),undefined);
  assert.equal(asyncReadRequest('portfolio/risk/jobs/'+snapshotId+'/cancel',{}),undefined);
  for(const path of ['profile','market/context','futures/book','corrections','toString','constructor','/evaluate','evaluate?x=1','https://synthetic.invalid/evaluate'])assert.equal(asyncReadRequest(path,body),undefined,path);
  assert.deepEqual(asyncReadRequest('portfolio/risk/jobs'),{operation:'portfolio.risk.jobs.list.v1',body:{}});
  assert.deepEqual(asyncReadRequest('portfolio/risk/jobs/'+snapshotId),{operation:'portfolio.risk.jobs.read.v1',body:{id:snapshotId}});
  assert.deepEqual(asyncReadRequest('portfolio/risk/monte-carlo/archive?limit=25'),{operation:'portfolio.risk.archive.v1',body:{method:'monte_carlo',limit:25,before:null,id:null}});
  assert.deepEqual(asyncReadRequest('trading/freshness',{snapshotId}),{operation:'trading.freshness.v1',body:{snapshotId}});
  assert.equal(asyncReadRequest('portfolio/risk/statistical/archive?limit=25&limit=50'),undefined);
  assert.equal(asyncReadRequest('portfolio/risk/statistical/archive?limit=25&target=private'),undefined);
  // Reads no page requests keep their ordinary routes and are not response-channel operations.
  for(const path of ['trading/position','portfolio/risk/statistical/setup','portfolio/risk/parametric','portfolio/risk/parametric/summary',
    'portfolio/risk/historical','portfolio/risk/historical/summary','portfolio/risk/historical/sample','portfolio/risk/monte-carlo'])
    assert.equal(asyncReadRequest(path,{}),undefined,path);
});

test('HTTP acceptance alone never completes a read; SSE may precede acknowledgement or its failure',async()=>{
  const h=harness();
  try{
    let accepted=0,finished=false;
    const first=h.client.read('curve.evaluate.v1',curveBody(),{onAccepted:()=>accepted++});first.then(()=>{finished=true;});
    const source=await h.ready(),request=h.requests[0];
    request.ack.resolve({channelId:request.body.channelId,requestId:request.body.requestId,state:'accepted'});
    await flush();assert.equal(accepted,1);assert.equal(finished,false);source.emit('response',h.result(request));await first;
    const second=h.client.read('curve.evaluate.v1',curveBody(),{onAccepted:()=>accepted++});await flush();
    source.emit('response',h.result(h.requests[1]));await second;h.requests[1].ack.reject(new Error('Lost acceptance.'));
    await flush();assert.equal(accepted,1);assert.equal(source.closed,false);
  }finally{h.client.close();}
});

test('malformed, unknown and conflicting terminal identities terminate active and queued reads',async()=>{
  const corruptions=[()=>'{broken',r=>({...r,protocol:'unexpected'}),r=>({...r,channelId:snapshotId}),r=>({...r,requestId:snapshotId}),
    r=>({...r,operation:'write.trade'}),r=>({...r,snapshotId:null}),r=>({...r,state:'running'}),r=>({...r,result:null}),
    r=>({...r,error:{code:'contradiction',message:'Completed reads cannot also fail.'}}),r=>({...r,state:'failed',result:undefined,error:{code:'missing_message'}})];
  for(const corrupt of corruptions){
    const h=harness(),reads=Array.from({length:3},()=>h.client.read('curve.evaluate.v1',curveBody()));
    const rejected=Promise.all(reads.map(read=>assert.rejects(read,{code:'async_read_protocol'})));
    const source=await h.ready();source.emit('response',corrupt(h.result(h.requests[0])));await rejected;
    assert.equal(source.closed,true);assert.equal(h.requests.length,2);h.client.close();
  }
});

test('a repeated completed outcome refuses the channel instead of changing another read',async()=>{
  const h=harness(),first=h.client.read('curve.evaluate.v1',curveBody()),source=await h.ready();
  source.emit('response',h.result(h.requests[0]));await first;
  const second=h.client.read('curve.evaluate.v1',curveBody()),rejected=assert.rejects(second,{code:'async_read_protocol'});await flush();
  source.emit('response',h.result(h.requests[0]));await rejected;assert.equal(source.closed,true);h.client.close();
});

test('server failures and cancellations release only their matching slots',async()=>{
  const h=harness(),first=h.client.read('curve.evaluate.v1',curveBody()),second=h.client.read('curve.evaluate.v1',curveBody()),third=h.client.read('curve.evaluate.v1',curveBody());
  const failed=assert.rejects(first,{code:'synthetic_refusal'}),cancelled=assert.rejects(second,{name:'AbortError',code:'request_cancelled'});
  const source=await h.ready();source.emit('response',h.result(h.requests[0],{state:'failed',result:null,error:{code:'synthetic_refusal',message:'Synthetic refusal.'}}));
  await failed;await flush();assert.equal(h.requests.length,3);
  source.emit('response',h.result(h.requests[1],{state:'cancelled',result:null,error:{code:'request_cancelled',message:'Cancelled.'}}));await cancelled;
  source.emit('response',h.result(h.requests[2]));await third;assert.equal(source.closed,false);h.client.close();
});

test('active abort rejects promptly but holds its server slot until the correlated terminal response',async()=>{
  const h=harness(),abort=new AbortController();
  const first=h.client.read('curve.evaluate.v1',curveBody(),{signal:abort.signal}),second=h.client.read('curve.evaluate.v1',curveBody()),third=h.client.read('curve.evaluate.v1',curveBody());
  const rejected=assert.rejects(first,{name:'AbortError'}),source=await h.ready();abort.abort();await rejected;await flush();
  assert.equal(h.requests.length,2,'cancelled work still occupies a server slot');assert.equal(h.cancellations.length,1);assert.equal(h.requests[0].signal.aborted,true);
  source.emit('response',h.result(h.requests[0]));await flush();assert.equal(h.requests.length,3,'late final result releases the slot without publishing it');
  source.emit('response',h.result(h.requests[2]));source.emit('response',h.result(h.requests[1]));await Promise.all([second,third]);h.client.close();
});

test('a matching server non-admission fence releases a cancelled submission without awaiting an impossible event',async()=>{
  const cancelReply=deferred(),h=harness({cancelReply,maxActive:1,cancellationTimeoutMs:25}),abort=new AbortController();
  const first=h.client.read('curve.evaluate.v1',curveBody(),{signal:abort.signal}),second=h.client.read('curve.evaluate.v1',curveBody());
  first.catch(()=>{});second.catch(()=>{});
  try{
    const source=await h.ready(),original=h.requests[0];abort.abort();await assert.rejects(first,{name:'AbortError'});await flush();
    assert.equal(h.requests.length,1,'local abort alone cannot release possible server work');
    cancelReply.resolve({...h.cancellations[0],state:'not_admitted'});await flush();
    assert.equal(h.requests.length,2,'the server fenced the original identity before admission, so queued work can execute');
    original.ack.reject(new DOMException('The original submission was aborted.','AbortError'));
    source.emit('response',h.result(h.requests[1]));await second;
    await new Promise(resolve=>setTimeout(resolve,40));assert.equal(source.closed,false,'the impossible-terminal timeout must be removed');
    assert.equal(h.requests.length,2,'the abandoned read is never replayed');
  }finally{h.client.close();}
});

test('a non-admission receipt with different request or channel identity cannot release an occupied slot',async()=>{
  for(const field of ['channelId','requestId']){
    const cancelReply=deferred(),h=harness({cancelReply,maxActive:1}),abort=new AbortController();
    const first=h.client.read('curve.evaluate.v1',curveBody(),{signal:abort.signal}),second=h.client.read('curve.evaluate.v1',curveBody());
    first.catch(()=>{});second.catch(()=>{});
    try{
      const source=await h.ready();abort.abort();await assert.rejects(first,{name:'AbortError'});await flush();
      const refused=assert.rejects(second,{code:'async_read_protocol'});
      cancelReply.resolve({...h.cancellations[0],[field]:snapshotId,state:'not_admitted'});await refused;
      assert.equal(source.closed,true);assert.equal(h.requests.length,1);
    }finally{h.client.close();}
  }
});

test('a late non-admission receipt after reset cannot affect the new channel',async()=>{
  const cancelReply=deferred(),h=harness({cancelReply,maxActive:1}),abort=new AbortController();
  const first=h.client.read('curve.evaluate.v1',curveBody(),{signal:abort.signal});first.catch(()=>{});
  let next,last;
  try{
    await h.ready();abort.abort();await assert.rejects(first,{name:'AbortError'});await flush();h.client.reset();
    next=h.client.read('curve.evaluate.v1',curveBody());last=h.client.read('curve.evaluate.v1',curveBody());next.catch(()=>{});last.catch(()=>{});
    const source=await h.ready();cancelReply.resolve({...h.cancellations[0],state:'not_admitted'});await flush();
    assert.equal(source.closed,false);assert.equal(h.requests.length,2,'the new channel still has only its one active read');
    source.emit('response',h.result(h.requests[1]));await next;await flush();
    source.emit('response',h.result(h.requests[2]));await last;
  }finally{h.client.close();}
});

test('queued and immediately aborted reads never send a command',async()=>{
  const h=harness(),abort=new AbortController(),queuedAbort=new AbortController();abort.abort();
  await assert.rejects(h.client.read('curve.evaluate.v1',curveBody(),{signal:abort.signal}),{name:'AbortError'});assert.equal(h.channels.length,0);
  const first=h.client.read('curve.evaluate.v1',curveBody()),second=h.client.read('curve.evaluate.v1',curveBody()),queued=h.client.read('curve.evaluate.v1',curveBody(),{signal:queuedAbort.signal});
  const rejected=assert.rejects(queued,{name:'AbortError'}),source=await h.ready();queuedAbort.abort();await rejected;await flush();
  assert.equal(h.requests.length,2);assert.equal(h.cancellations.length,0);
  source.emit('response',h.result(h.requests[0]));source.emit('response',h.result(h.requests[1]));await Promise.all([first,second]);
  const beforeDispatch=new AbortController(),pending=h.client.read('curve.evaluate.v1',curveBody(),{signal:beforeDispatch.signal});
  const cancelled=assert.rejects(pending,{name:'AbortError'});beforeDispatch.abort();await cancelled;await flush();
  assert.equal(h.requests.length,2);assert.equal(h.cancellations.length,0);h.client.close();
});

test('cancellation drain has a bounded wait and cannot silently free an occupied server slot',async()=>{
  const h=harness({cancellationTimeoutMs:15}),abort=new AbortController();
  const first=h.client.read('curve.evaluate.v1',curveBody(),{signal:abort.signal}),second=h.client.read('curve.evaluate.v1',curveBody()),third=h.client.read('curve.evaluate.v1',curveBody());
  const rejected=assert.rejects(first,{name:'AbortError'}),ended=Promise.all([second,third].map(p=>assert.rejects(p,{code:'async_read_cancel_timeout'})));
  const source=await h.ready();abort.abort();await rejected;await ended;assert.equal(h.requests.length,2);assert.equal(source.closed,true);h.client.close();
});

test('connection and queued request waits are bounded',async()=>{
  const waiting=harness({connectionTimeoutMs:15});await assert.rejects(waiting.client.read('curve.evaluate.v1',curveBody()),{code:'async_read_connection_timeout'});
  assert.equal(waiting.requests.length,0);assert.equal(waiting.sources[0].closed,true);waiting.client.close();
  const h=harness({requestTimeoutMs:15});
  const reads=Array.from({length:3},()=>h.client.read('curve.evaluate.v1',curveBody()));const rejected=Promise.all(reads.map(p=>assert.rejects(p,{code:'async_read_timeout'})));
  await h.ready();await rejected;await flush();assert.equal(h.requests.length,2);assert.equal(h.cancellations.length,2);h.client.close();
});

test('disconnect rejects active and queued work; the next explicit read opens a new stream with no replay',async()=>{
  const h=harness(),reads=Array.from({length:4},()=>h.client.read('curve.evaluate.v1',curveBody()));
  const rejected=Promise.all(reads.map(p=>assert.rejects(p,{code:'async_read_disconnected'}))),old=await h.ready();old.dispatchEvent(new Event('error'));await rejected;
  assert.equal(old.closed,true);assert.equal(h.channels.length,1);assert.equal(h.requests.length,2);
  const next=h.client.read('curve.evaluate.v1',curveBody()),source=await h.ready();assert.equal(h.channels.length,2);assert.equal(h.requests.length,3);
  old.emit('response','malformed late event');source.emit('response',h.result(h.requests[2]));await next;h.client.close();
});

test('close and reset reject all pending work and old channel messages cannot revive it',async()=>{
  for(const action of ['close','reset']){
    const h=harness(),reads=Array.from({length:3},()=>h.client.read('curve.evaluate.v1',curveBody()));
    const rejected=Promise.all(reads.map(p=>assert.rejects(p,{code:action==='close'?'async_read_closed':'async_read_reset'}))),old=await h.ready();
    h.client[action]();await rejected;assert.equal(old.closed,true);
    if(action==='close'){await assert.rejects(h.client.read('curve.evaluate.v1',curveBody()),{code:'async_read_closed'});h.client.reset();}
    const next=h.client.read('curve.evaluate.v1',curveBody()),source=await h.ready();old.emit('response',h.result(h.requests[0]));
    source.emit('response',h.result(h.requests[2]));await next;h.client.close();
  }
});

test('captured request bodies cannot change during connection setup, and scoped roots remain scoped',async()=>{
  const h=harness({root:'/examples/exchange/api/'}),body=curveBody(),pending=h.client.read('curve.evaluate.v1',body);body.curve='changed_after_request';
  const source=await h.ready();assert.equal(h.requests[0].body.body.curve,'synthetic_curve');assert.ok(source.url.startsWith('/examples/exchange/api/async-reads/events?'));
  source.emit('response',h.result(h.requests[0]));await pending;h.client.close();
});

test('explicit snapshot correlation follows each supported financial request shape, with null only for current job reads',async()=>{
  const cases=[['trading.directory.v1',{snapshotId}],['trading.freshness.v1',{snapshotId}],['futures.editor.v1',{context:{snapshotId},tradeId:snapshotId}],
    ['portfolio.risk.catalog.v1',{context:{snapshotId}}],['portfolio.risk.history.v1',{scope:{context:{snapshotId}}}],
    ['portfolio.risk.jobs.list.v1',{}],['portfolio.risk.jobs.read.v1',{id:snapshotId}],['portfolio.risk.archive.v1',{method:'statistical',limit:25,before:null,id:null}]];
  const h=harness();let source;
  for(const [operation,body] of cases){
    const pending=h.client.read(operation,body);if(!source)source=await h.ready();else await flush();
    const request=h.requests.at(-1),isCurrent=operation.includes('.jobs.')||operation.endsWith('.archive.v1');
    source.emit('response',h.result(request,{snapshotId:isCurrent?null:snapshotId}));await pending;
  }
  for(const [operation] of cases.filter(([op])=>!op.includes('.jobs.')&&!op.endsWith('.archive.v1')))await assert.rejects(h.client.read(operation,{}),{code:'async_read_request'});
  for(const operation of ['trading.position.v1','portfolio.risk.statistical.setup.v1','portfolio.risk.parametric.calculate.v1','portfolio.risk.parametric.summary.v1',
    'portfolio.risk.historical.calculate.v1','portfolio.risk.historical.summary.v1','portfolio.risk.historical.sample.v1','portfolio.risk.monte_carlo.calculate.v1'])
    await assert.rejects(h.client.read(operation,{context:{snapshotId},history:{scope:{context:{snapshotId}}},request:{history:{scope:{context:{snapshotId}}}}}),{code:'async_read_request'},operation);
  await assert.rejects(h.client.read('write.trade',{}),{code:'async_read_request'});h.client.close();
});

test('selected account/context must match actual channel creation and remain unchanged through delivery',async()=>{
  for(const created of [{profile:'different',context:null},{profile:'synthetic_viewer'},{profile:'synthetic_viewer',context:{contextId:snapshotId}}]){
    const h=harness({post:async()=>({protocol,channelId:snapshotId,...created})});
    await assert.rejects(h.client.read('curve.evaluate.v1',curveBody()),{code:'async_read_identity'});assert.equal(h.sources.length,0);h.client.close();
  }
  const h=harness(),reads=Array.from({length:3},()=>h.client.read('curve.evaluate.v1',curveBody()));
  const rejected=Promise.all(reads.map(p=>assert.rejects(p,{code:'async_read_identity'}))),source=await h.ready();
  h.identity({profile:'synthetic_viewer',context:{contextId:snapshotId}});source.emit('response',h.result(h.requests[0]));await rejected;
  assert.equal(source.closed,true);assert.equal(h.requests.length,2);h.client.close();
});

test('a newly selected identity rejects previous work before admitting its own explicit reads',async()=>{
  const h=harness(),old=h.client.read('curve.evaluate.v1',curveBody()),rejected=assert.rejects(old,{code:'async_read_identity'});await h.ready();
  h.identity({profile:'synthetic_other',context:null});const next=h.client.read('curve.evaluate.v1',curveBody()),source=await h.ready();await rejected;
  source.emit('response',h.result(h.requests[1]));await next;assert.equal(h.channels.length,2);h.client.close();
});

test('queue capacity is finite and released after completion',async()=>{
  const h=harness({maxQueued:1}),reads=Array.from({length:3},()=>h.client.read('curve.evaluate.v1',curveBody()));
  await assert.rejects(h.client.read('curve.evaluate.v1',curveBody()),{code:'async_read_limit'});const source=await h.ready();
  source.emit('response',h.result(h.requests[0]));await reads[0];const next=h.client.read('curve.evaluate.v1',curveBody());await flush();
  source.emit('response',h.result(h.requests[1]));await flush();source.emit('response',h.result(h.requests[2]));source.emit('response',h.result(h.requests[3]));
  await Promise.all([...reads,next]);h.client.close();
});

test('channel identity history renews after 128 submissions without replaying any read',async()=>{
  const h=harness();let source;
  for(let index=0;index<127;index++){
    const pending=h.client.read('curve.evaluate.v1',curveBody());if(!source)source=await h.ready();else await flush();
    source.emit('response',h.result(h.requests[index]));await pending;
  }
  const last=h.client.read('curve.evaluate.v1',curveBody()),next=h.client.read('curve.evaluate.v1',curveBody());await flush();
  assert.equal(h.requests.length,128);source.emit('response',h.result(h.requests[127]));await last;
  const replacement=await h.ready();assert.equal(h.requests.length,129);assert.equal(h.sources.length,2);assert.equal(source.closed,true);
  replacement.emit('response',h.result(h.requests[128]));await next;h.client.close();
});

test('at the admission boundary a queued read waits for accepted work before using a new channel',async t=>{
  let now=1000;t.mock.method(performance,'now',()=>now);
  const h=harness(),received=[];
  const read=()=>{
    const result=h.client.read('curve.evaluate.v1',curveBody());
    result.catch(()=>{});return result;
  };
  try{
    const first=read(),source=await h.ready();
    now=1000+admissionWindowMs-1;
    const second=read();await flush();assert.equal(h.requests.length,2);
    const accept=request=>request.ack.resolve({channelId:request.body.channelId,requestId:request.body.requestId,state:'accepted'});
    h.requests.forEach(accept);await flush();
    now++;
    const queued=read(),abort=new AbortController();
    const abandoned=h.client.read('curve.evaluate.v1',curveBody(),{signal:abort.signal});
    const cancelled=assert.rejects(abandoned,{name:'AbortError'});abort.abort();await cancelled;
    assert.equal(h.cancellations.length,0,'an unsent read requires no server cancellation');
    source.emit('response',h.result(h.requests[0]));received.push(await first);await flush();
    assert.equal(h.requests.length,2,'an aged channel must drain without submitting the queued read');
    assert.equal(h.channels.length,1);assert.equal(source.closed,false);
    source.emit('response',h.result(h.requests[1]));received.push(await second);
    const fresh=await h.ready();assert.equal(h.channels.length,2);assert.equal(source.closed,true);
    assert.equal(h.requests.length,3);assert.equal(h.requests[2].body.channelId,h.channels[1].channelId);
    fresh.emit('response',h.result(h.requests[2]));received.push(await queued);
    assert.equal(new Set(received.map(result=>result.synthetic)).size,3);
    assert.equal(h.channels[0].request.clientId,h.channels[1].request.clientId);
  }finally{h.client.close();}
});

test('an explicit read renews an aged idle channel even before its disconnect event arrives',async t=>{
  let now=1000;t.mock.method(performance,'now',()=>now);
  const h=harness();
  try{
    const first=h.client.read('curve.evaluate.v1',curveBody());
    await flush();now=1500;
    const source=await h.ready();source.emit('response',h.result(h.requests[0]));await first;
    now=1000+admissionWindowMs;
    const next=h.client.read('curve.evaluate.v1',curveBody());next.catch(()=>{});
    const fresh=await h.ready();assert.equal(h.channels.length,2);assert.equal(source.closed,true);
    assert.equal(h.requests.length,2);assert.equal(h.requests[1].body.channelId,h.channels[1].channelId);
    fresh.emit('response',h.result(h.requests[1]));await next;
  }finally{h.client.close();}
});

for(const acceptedPeer of [false,true]){
  test('a reservation that ages before dispatch moves only the unsent read'+(acceptedPeer?' after its accepted peer drains':''),async t=>{
    let now=1000;t.mock.method(performance,'now',()=>now);
    const h=harness();
    try{
      const first=h.client.read('curve.evaluate.v1',curveBody());first.catch(()=>{});
      const source=await h.ready(),request=h.requests[0];
      if(acceptedPeer){
        request.ack.resolve({channelId:request.body.channelId,requestId:request.body.requestId,state:'accepted'});
        await flush();
      }else{
        source.emit('response',h.result(request));await first;
      }
      now=1000+admissionWindowMs-1;
      const next=h.client.read('curve.evaluate.v1',curveBody());next.catch(()=>{});
      now++;await flush();
      assert.equal(h.requests.length,1,'the deferred POST must not send on the aged channel');
      if(acceptedPeer){
        assert.equal(h.channels.length,1);assert.equal(source.closed,false);
        source.emit('response',h.result(request));await first;
      }
      const fresh=await h.ready();
      assert.equal(h.channels.length,2);assert.equal(source.closed,true);
      assert.equal(h.requests.length,2);assert.equal(h.requests[1].body.channelId,h.channels[1].channelId);
      fresh.emit('response',h.result(h.requests[1]));await next;
    }finally{h.client.close();}
  });
}

test('a positive admission window is valid with a longer maximum connection timeout',async()=>{
  const h=harness({connectionTimeoutMs:admissionWindowMs+1});
  try{
    const pending=h.client.read('curve.evaluate.v1',curveBody());pending.catch(()=>{});
    await flush();assert.equal(h.sources.length,1,'an immediately opened channel has its advertised time available');
    const source=await h.ready();
    source.emit('response',h.result(h.requests[0]));await pending;
  }finally{h.client.close();}
});

test('an opening that has consumed the full admission window fails without retrying',async t=>{
  let now=1000;t.mock.method(performance,'now',()=>now);
  const h=harness();
  try{
    const pending=h.client.read('curve.evaluate.v1',curveBody());pending.catch(()=>{});
    await flush();now+=admissionWindowMs;
    const source=await h.ready();
    assert.equal(h.channels.length,1,'an expired opening must not loop through channel renewal');
    assert.equal(h.requests.length,0);assert.equal(source.closed,true);
    await assert.rejects(pending,{code:'async_read_connection_timeout'});
  }finally{h.client.close();}
});

test('a channel opening requires an explicit usable admission window',async()=>{
  for(const window of [undefined,null,'230000',-1,0,1000.5,Number.MAX_SAFE_INTEGER+1]){
    const h=harness({post:async()=>({protocol,channelId:snapshotId,profile:'synthetic_viewer',context:null,admissionWindowMs:window})});
    await assert.rejects(h.client.read('curve.evaluate.v1',curveBody()),{code:'async_read_protocol'});
    assert.equal(h.sources.length,0);h.client.close();
  }
});

test('structured HTTP refusal frees its slot, while lost acceptance terminates without automatic replay',async()=>{
  const h=harness(),first=h.client.read('curve.evaluate.v1',curveBody()),second=h.client.read('curve.evaluate.v1',curveBody()),third=h.client.read('curve.evaluate.v1',curveBody());
  const refused=assert.rejects(first,{code:'async_busy'}),source=await h.ready();h.requests[0].ack.reject(Object.assign(Error('Synthetic admission refusal.'),{code:'async_busy',status:429}));
  await refused;await flush();assert.equal(h.requests.length,3);source.emit('response',h.result(h.requests[1]));source.emit('response',h.result(h.requests[2]));await Promise.all([second,third]);h.client.close();
  const lost=harness(),reads=Array.from({length:3},()=>lost.client.read('curve.evaluate.v1',curveBody()));
  const rejected=Promise.all(reads.map(p=>assert.rejects(p,{code:'response_unknown'}))),stream=await lost.ready();
  lost.requests[0].ack.reject(Object.assign(Error('Lost acceptance.'),{code:'response_unknown'}));await rejected;
  assert.equal(lost.requests.length,2);assert.equal(lost.channels.length,1);assert.equal(stream.closed,true);lost.client.close();
});

test('malformed acceptance and readiness fail closed, and late channel creation cannot revive timeout',async()=>{
  const h=harness(),pending=h.client.read('curve.evaluate.v1',curveBody()),rejected=assert.rejects(pending,{code:'async_read_protocol'});
  const source=await h.ready();h.requests[0].ack.resolve({state:'accepted'});await rejected;assert.equal(source.closed,true);h.client.close();
  const ready=harness(),waiting=ready.client.read('curve.evaluate.v1',curveBody()),failed=assert.rejects(waiting,{code:'async_read_protocol'});
  await flush();ready.sources[0].emit('ready',{channelId:ready.channels[0].channelId});await failed;ready.client.close();
  const opening=deferred(),late=harness({post:()=>opening.promise,connectionTimeoutMs:15});
  await assert.rejects(late.client.read('curve.evaluate.v1',curveBody()),{code:'async_read_connection_timeout'});late.client.close();
  opening.resolve({protocol,channelId:snapshotId,profile:'synthetic_viewer',context:null,admissionWindowMs});await flush();assert.equal(late.sources.length,0);
});

test('wire requests and complete response events have explicit size limits',async()=>{
  const h=harness();await assert.rejects(h.client.read('curve.evaluate.v1',{...curveBody(),padding:'x'.repeat(32768)}),{code:'async_read_request'});assert.equal(h.channels.length,0);
  const pending=h.client.read('curve.evaluate.v1',curveBody()),source=await h.ready(),reply=h.result(h.requests[0]);
  reply.result={padding:'x'.repeat(16*1024*1024-20)};source.emit('response',reply);await pending;
  const oversized=h.client.read('curve.evaluate.v1',curveBody()),rejected=assert.rejects(oversized,{code:'async_read_protocol'});await flush();
  source.emit('response','x'.repeat(16*1024*1024+8193));await rejected;h.client.close();
});

test('a structured failure after possible admission cannot free a slot or run queued work',async()=>{
  for(const status of [undefined,500,503]){
    const h=harness(),reads=Array.from({length:3},()=>h.client.read('curve.evaluate.v1',curveBody()));
    const ended=Promise.all(reads.map(p=>assert.rejects(p,{code:'request_cancelled'}))),source=await h.ready();
    h.requests[0].ack.reject(Object.assign(Error('Synthetic response failure after possible admission.'),{code:'request_cancelled',status}));
    await ended;await flush();assert.equal(h.requests.length,2);assert.equal(source.closed,true);assert.equal(h.channels.length,1);
    const next=h.client.read('curve.evaluate.v1',curveBody()),fresh=await h.ready();
    fresh.emit('response',h.result(h.requests[2]));await next;assert.equal(h.channels.length,2);h.client.close();
  }
});

test('archive route translation preserves explicit paging and does not coerce invalid limits into valid inputs',()=>{
  const path='portfolio/risk/statistical/archive?';
  for(const query of ['limit=1e1','limit=0x10','limit=','before=2026-01-01','limit=101','limit=1.5'])
    assert.throws(()=>asyncReadRequest(path+query),{code:'risk_archive_input'},query);
  assert.deepEqual(asyncReadRequest(path+'limit=25&before=2026-01-01T00%3A00%3A00Z&id='+snapshotId),{
    operation:'portfolio.risk.archive.v1',body:{method:'statistical',limit:25,before:'2026-01-01T00:00:00Z',id:snapshotId}
  });
});

test('a never-submitted cancelled request cannot acquire a terminal server outcome',async t=>{
  const generate=crypto.randomUUID.bind(crypto),ids=[];t.mock.method(crypto,'randomUUID',()=>{const id=generate();ids.push(id);return id;});
  const h=harness(),abort=new AbortController(),first=h.client.read('curve.evaluate.v1',curveBody()),second=h.client.read('curve.evaluate.v1',curveBody());
  const queued=h.client.read('curve.evaluate.v1',curveBody(),{signal:abort.signal}),cancelled=assert.rejects(queued,{name:'AbortError'});
  const rejected=Promise.all([first,second].map(p=>assert.rejects(p,{code:'async_read_protocol'}))),source=await h.ready();
  abort.abort();await cancelled;
  source.emit('response',h.result(h.requests[0],{requestId:ids.at(-1)}));await rejected;assert.equal(h.requests.length,2);h.client.close();
});

test('a terminal channel closes outstanding cancellation HTTP requests as well as its stream',async()=>{
  const reply=deferred(),h=harness({cancelReply:reply}),abort=new AbortController();
  const pending=h.client.read('curve.evaluate.v1',curveBody(),{signal:abort.signal}),rejected=assert.rejects(pending,{name:'AbortError'}),source=await h.ready();
  abort.abort();await rejected;await flush();assert.equal(h.cancellationSignals.length,1);assert.equal(h.cancellationSignals[0].aborted,false);
  source.emit('response',h.result(h.requests[0]));source.dispatchEvent(new Event('error'));
  assert.equal(h.cancellationSignals[0].aborted,true);reply.resolve({});h.client.close();
});
