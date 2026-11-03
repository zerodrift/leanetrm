/** Overlapping callers await the same complete read, including its identity checks. */
export function coalescedRead<T>(read:()=>Promise<T>){
 let current:Promise<T>|undefined,following:Promise<T>|undefined;
 const joined=()=>current??=read().finally(()=>{current=undefined;});
 // A demand made after an action cannot be satisfied by a read that started before it.
 // Concurrent demands share one following read, even when the older read failed.
 const afterCurrent=():Promise<T>=>current===undefined?joined():following??=current.catch(()=>{}).then(()=>{
  following=undefined;return joined();
 });
 return Object.assign(joined,{afterCurrent});
}
