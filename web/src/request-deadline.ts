type Deadlines={normalMilliseconds:number;extendedMilliseconds:number};

/** How long bootstrap and the example directory wait, since they precede the server's process metadata: the longest
 * ordinary budget the API can serve, its 29-second ceiling plus the 5-second response grace. This is the page's one
 * fixed request figure; HostedConfigurationChecks holds it to the API's ceilings. */
export const startupMilliseconds=34000;

/** The server bounds the budgets it serves (ProcessLimits), so the page keeps no ceiling on them. */
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
    if(route==='bootstrap'||route==='examples')return startupMilliseconds;
    throw Error('Load the server request deadline metadata before reading workspace values.');
   }
   return route==='ppa'||route.startsWith('ppa/')||route==='trading/position'?current.extendedMilliseconds:current.normalMilliseconds;
  }
 };
}
