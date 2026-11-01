// Client for the streamed Blotter valuation (NDJSON frames: start, rows*, complete | error).
// Parses and validates the frame contract only; it performs no financial arithmetic.

export type BlotterStreamRequest={context:{snapshotId:string;convention:string;knowledgeMode:'current'|'retained';contextId?:string};
  view:{mode:'trades'|'pivot'}&Record<string,unknown>;bookingCatalog?:'directory'|'priced_contracts';summaries:unknown[]};
export type BlotterStreamStart={context:{contextId:string;effectiveOn:string;knownAt:string};asOf:string;positions:number}&Record<string,unknown>;
export type BlotterStreamHandlers={start(s:BlotterStreamStart):void|Promise<void>;rows(rows:Record<string,unknown>[],valued:number,positions:number):void|Promise<void>;
  complete(c:{blotter:Record<string,unknown>;summaries:unknown[];catalog:unknown}):void|Promise<void>};

export class BlotterStreamError extends Error{
  constructor(readonly code:string,message:string){super(message);this.name='BlotterStreamError';}
}

type Frame={type?:unknown}&Record<string,unknown>;
const bad=(code:string,message:string)=>new BlotterStreamError(code,message);
const isCount=(v:unknown):v is number=>typeof v==='number'&&Number.isInteger(v)&&v>=0;
const isObject=(v:unknown):v is Record<string,unknown>=>typeof v==='object'&&v!==null&&!Array.isArray(v);

function parseFrame(line:string):Frame{
  let frame:unknown;
  try{frame=JSON.parse(line);}catch{throw bad('stream_order','A stream frame is not valid JSON.');}
  if(!isObject(frame))throw bad('stream_order','A stream frame is not an object.');
  return frame as Frame;
}

export async function readBlotterStream(body:ReadableStream<Uint8Array>,request:BlotterStreamRequest,handlers:BlotterStreamHandlers,signal:AbortSignal):Promise<void>{
  const reader=body.getReader(),decoder=new TextDecoder();
  const trades=request.view.mode==='trades';
  let positions:number|null=null,valued=0,received=0,terminal=false,buffer='';

  const accept=async(frame:Frame)=>{
    if(terminal)throw bad('stream_order','A frame follows the terminal frame.');
    // The server refuses before start with an HTTP error, so an error frame always follows start.
    if(frame.type==='error'){
      if(positions===null)throw bad('stream_order','The stream does not begin with a start frame.');
      if(typeof frame.code!=='string'||typeof frame.message!=='string')throw bad('stream_order','An error frame is malformed.');
      throw bad(frame.code,frame.message);
    }
    if(frame.type==='start'){
      if(positions!==null)throw bad('stream_order','The stream has more than one start frame.');
      const s=frame as unknown as BlotterStreamStart;
      if(!isCount(s.positions))throw bad('stream_count','The start frame has no position count.');
      if(typeof s.asOf!=='string')throw bad('stream_order','The start frame has no As of.');
      if(request.context.contextId!==undefined&&s.context?.contextId!==request.context.contextId)
        throw bad('stream_context','The stream context is not the requested context.');
      positions=s.positions;await handlers.start(s);return;
    }
    if(positions===null)throw bad('stream_order','The stream does not begin with a start frame.');
    if(frame.type==='rows'){
      const batch=frame.rows;
      if(!Array.isArray(batch)||!isCount(frame.valued)||!batch.every(isObject)||!trades&&batch.length>0)throw bad('stream_count','A rows frame is malformed.');
      if(frame.valued>positions||frame.valued<valued)throw bad('stream_count','The valued count is out of range.');
      received+=batch.length;
      if(trades&&received>positions)throw bad('stream_count','The stream carries more rows than positions.');
      valued=frame.valued;await handlers.rows(batch as Record<string,unknown>[],valued,positions);return;
    }
    if(frame.type==='complete'){
      if(frame.positions!==positions||frame.valued!==positions||valued!==positions)throw bad('stream_count','The final counts do not equal the positions.');
      if(trades&&received!==positions)throw bad('stream_count','The stream rows do not equal the positions.');
      if(!isObject(frame.blotter)||!Array.isArray(frame.summaries)||!isObject(frame.catalog))throw bad('stream_order','The complete frame is malformed.');
      terminal=true;await handlers.complete(frame as unknown as Parameters<BlotterStreamHandlers['complete']>[0]);return;
    }
    throw bad('stream_order','The stream has an unknown frame.');
  };

  // An abort settles at once, even while a read waits for a transport that ignores the signal.
  let stop=()=>{};
  const aborted=new Promise<ReadableStreamReadResult<Uint8Array>>(settle=>{stop=()=>settle({done:true,value:undefined});signal.addEventListener('abort',stop,{once:true});});
  try{
    for(;;){
      if(signal.aborted)return;
      const {done,value}=await Promise.race([reader.read(),aborted]);
      if(signal.aborted)return;
      buffer+=decoder.decode(value,{stream:!done});
      let newline:number;
      while((newline=buffer.indexOf('\n'))>=0){
        const line=buffer.slice(0,newline);buffer=buffer.slice(newline+1);
        if(line.trim()==='')continue;
        await Promise.race([accept(parseFrame(line)),aborted]);
        if(signal.aborted)return;
      }
      if(done)break;
    }
    if(buffer.trim()!=='')throw bad('stream_truncated','The stream ended inside a frame.');
    if(!terminal)throw bad('stream_truncated','The stream ended without a terminal frame.');
  }catch(e){
    if(signal.aborted)return;
    throw e;
  }finally{
    signal.removeEventListener('abort',stop);
    reader.cancel().catch(()=>undefined);
  }
}

export async function streamBlotter(transport:(path:string,body:unknown,signal:AbortSignal)=>Promise<Response>,request:BlotterStreamRequest,handlers:BlotterStreamHandlers,signal:AbortSignal):Promise<void>{
  const response=await transport('trading/workspace/stream',request,signal);
  if(signal.aborted)return;
  if(response.status!==200){
    const failure=await response.json().catch(()=>null) as {code?:unknown;message?:unknown}|null;
    throw bad(typeof failure?.code==='string'?failure.code:'stream_http',typeof failure?.message==='string'?failure.message:`The stream request failed with status ${response.status}.`);
  }
  if(!response.body)throw bad('stream_truncated','The stream response has no body.');
  await readBlotterStream(response.body,request,handlers,signal);
}
