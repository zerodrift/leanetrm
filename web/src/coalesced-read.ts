/** Overlapping callers await the same complete read, including its identity checks. */
export function coalescedRead<T>(read:()=>Promise<T>):()=>Promise<T>{
 let current:Promise<T>|undefined;
 return ()=>current??=read().finally(()=>{current=undefined;});
}
