// Initial browsing only. Explicit selections are retained by their owning view.
export function latestFrame(frames:readonly {id:string;at:string}[]):string{
 const newest=frames.reduce<{id:string;at:string}|undefined>((latest,frame)=>!latest||Date.parse(frame.at)>Date.parse(latest.at)?frame:latest,undefined);
 return newest&&frames.filter(frame=>Date.parse(frame.at)===Date.parse(newest.at)).length===1?newest.id:'';
}
// A product may expose several exact bundles at one desk instant. Keep the
// selection empty so the person chooses, rather than inventing a tie-breaker.
export function frameAt(frames:readonly {id:string;at:string}[],at:string):string{
 const finer=(value:string)=>/[1-9]/.test((value.match(/\.(\d+)(?:Z|[+-]\d{2}:\d{2})$/)?.[1]??'').slice(3));
 if(finer(at))return '';
 const found=frames.filter(frame=>!finer(frame.at)&&Date.parse(frame.at)===Date.parse(at));
 return found.length===1?found[0].id:'';
}
