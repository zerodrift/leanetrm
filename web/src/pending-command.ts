/** A request the page sends, kept exactly as it was first sent. Its `label` says what it is, for the person, and is never sent. */
export type RequestCommand={operationId:string;path:string;body:Record<string,unknown>;uncertain?:boolean;label?:string};

// Once sent, a command keeps its identity and economics through uncertain replies.
export class PendingCommand {
  command:RequestCommand|undefined;
  constructor(private readonly knownRejection:(code:string|undefined)=>boolean=()=>false){}
  begin(path:string,body:Record<string,unknown>,newId:()=>string=()=>crypto.randomUUID(),label?:string):RequestCommand{
    if(this.command){
      if(this.command.path!==path||JSON.stringify(this.command.body)!==JSON.stringify(body))throw Error('Review the outstanding request before changing its details. You can retry the original request or discard it after checking recent changes.');
      return this.command;
    }
    return this.command={operationId:newId(),path,body:structuredClone(body),...(label===undefined?{}:{label})};
  }
  clear(){this.command=undefined;}
  sending(){
    const previouslyUncertain=this.command?.uncertain===true;
    if(this.command)this.command.uncertain=true;
    return previouslyUncertain;
  }
  failed(code:string|undefined,previouslyUncertain=this.command?.uncertain===true){
    if(!this.command)return;
    // A denied retry says nothing about the preceding uncertain transaction.
    if(this.knownRejection(code)&&!previouslyUncertain)this.clear();
    else this.command.uncertain=true;
  }
}

// A local receipt is untrusted transport state, never write authority. Preserve
// the exact operation/body for status recovery or an authorized idempotent retry,
// and the label that names it for the person.
export function restoreTradeRequest(value:unknown):RequestCommand|undefined{
  if(!value||typeof value!=='object'||Array.isArray(value))return;
  const saved=value as Record<string,unknown>;
  if(typeof saved.path!=='string'||!['futures/book','futures/amend','swaps/book','swaps/amend','swaps/settlement/determine','swaps/settlement/payment','swaps/settlement/net','tickets/book'].includes(saved.path)||
    typeof saved.operationId!=='string'||!saved.operationId||!saved.body||typeof saved.body!=='object'||Array.isArray(saved.body))return;
  return{path:saved.path,operationId:saved.operationId,body:structuredClone(saved.body as Record<string,unknown>),...(saved.uncertain===true?{uncertain:true}:{}),...(typeof saved.label==='string'?{label:saved.label}:{})};
}

/** Actor-scoped transport retention survives editor remounts even when browser storage fails. */
export function actorTransport<T>(key:()=>string,storage:()=>Pick<Storage,'getItem'|'setItem'|'removeItem'>){
 const memory=new Map<string,T|null>();
 return{
  write(value:T|undefined){
   const actor=key();memory.set(actor,value===undefined?null:structuredClone(value));
   try{if(value===undefined)storage().removeItem(actor);else storage().setItem(actor,JSON.stringify(value));}catch{/* The exact actor-owned command remains in memory. */}
  },
  read():T|null{
   const actor=key();
   if(memory.has(actor))return structuredClone(memory.get(actor)!);
   try{const value=JSON.parse(storage().getItem(actor)??'null') as T|null;memory.set(actor,value);return structuredClone(value);}catch{return null;}
  },
 };
}
