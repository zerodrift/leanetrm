// One view's requested keys share bounded, serial reads. The owner supplies its
// current-context guard and disposes the reader when that view changes.
export function batchReads<K,V>(o:{maximum:number;current:()=>boolean;read:(keys:readonly K[])=>Promise<ReadonlyMap<K,V>>;
  received?:(values:ReadonlyMap<K,V>,keys:readonly K[])=>void;active?:(reading:boolean)=>void}){
  if(!Number.isSafeInteger(o.maximum)||o.maximum<1)throw Error('Supply a positive batch size.');
  type Entry={promise:Promise<ReadonlyMap<K,V>>;resolve:(values:ReadonlyMap<K,V>)=>void;reject:(error:unknown)=>void;sent:boolean};
  const entries=new Map<K,Entry>();let stopped=false,busy=false,timer:ReturnType<typeof setTimeout>|undefined;
  let idle=Promise.resolve(),finish=()=>{};
  const complete=()=>{if(busy){busy=false;o.active?.(false);finish();}};
  function cancel(){
    if(stopped)return;stopped=true;clearTimeout(timer);
    const empty=new Map<K,V>();for(const entry of entries.values())entry.resolve(empty);entries.clear();complete();
  }
  async function flush(){
    timer=undefined;
    try{
      while(!stopped){
        if(!o.current()){cancel();return;}
        const batch=[...entries].filter(([,entry])=>!entry.sent).slice(0,o.maximum);
        if(!batch.length)return;
        for(const [,entry] of batch)entry.sent=true;
        const keys=batch.map(([key])=>key);
        try{
          const values=await o.read(keys);
          if(stopped||!o.current()){cancel();return;}
          o.received?.(values,keys);for(const [,entry] of batch)entry.resolve(values);
        }catch(error){if(stopped||!o.current()){cancel();return;}for(const [,entry] of batch)entry.reject(error);}
      }
    }finally{complete();}
  }
  return {
    has:(key:K)=>entries.has(key),busy:()=>busy,idle:()=>idle,cancel,
    async get(keys:readonly K[]):Promise<ReadonlyMap<K,V>>{
      if(stopped||!o.current()){cancel();return new Map();}
      const wanted=[...new Set(keys)],reads=wanted.map(key=>{
        let entry=entries.get(key);
        if(!entry){
          let resolve!:Entry['resolve'],reject!:Entry['reject'];
          const promise=new Promise<ReadonlyMap<K,V>>((yes,no)=>{resolve=yes;reject=no;});
          entry={promise,resolve,reject,sent:false};entries.set(key,entry);
          if(!busy){busy=true;idle=new Promise(done=>{finish=done;});o.active?.(true);timer=setTimeout(()=>void flush(),0);}
        }
        return entry.promise.then(values=>values.has(key)?[[key,values.get(key)!] as const]:[]);
      });
      const values=await Promise.all(reads);return stopped||!o.current()?new Map():new Map(values.flat());
    }
  };
}
