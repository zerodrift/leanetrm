// The job a navigation target names, on whichever page of Jobs & Infrastructure's directory lists it. The directory
// lists every job its reader may see, 100 to a page, so its pages are read in order until one lists the job; a job no
// page lists is refused, never replaced by another.
export type DirectoryPage={page:number;total:number;jobs:{id:string}[]};
export async function findJob<D extends DirectoryPage>(id:string,read:(page:number)=>Promise<D>):Promise<{job:D['jobs'][number];directory:D}>{
  for(let page=0;;page++){
    const directory=await read(page),job=directory.jobs.find(j=>j.id===id);
    if(job)return {job,directory};
    if((page+1)*100>=directory.total)throw Error('That job is not listed on Jobs & Infrastructure for you.');
  }
}
