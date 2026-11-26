type Deadlines={normalMilliseconds:number;extendedMilliseconds:number};

/** Only bootstrap and the example directory can precede the server's process metadata. The server bounds the
 * budgets it serves (ProcessLimits), so the page keeps no ceiling of its own. */
export function requestDeadlines(){
 let current:Deadlines|undefined;
 return {
  read(value:unknown){
   const v=value as Partial<Deadlines>|null;
   if(!v||!Number.isInteger(v.normalMilliseconds)||!Number.isInteger(v.extendedMilliseconds)
     ||v.normalMilliseconds!<1||v.extendedMilliseconds!<v.normalMilliseconds!)
    throw Error('The server request deadline metadata is missing or inconsistent. Refresh and try again.');
   current=v as Deadlines;
  },
  milliseconds(path:string){
   const route=path.split('?')[0];
   if(!current){
    if(route==='bootstrap'||route==='examples')return 20000;
    throw Error('Load the server request deadline metadata before reading workspace values.');
   }
   return route==='ppa'||route.startsWith('ppa/')||route==='trading/position'?current.extendedMilliseconds:current.normalMilliseconds;
  }
 };
}
