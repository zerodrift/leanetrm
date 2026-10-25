// Initial browsing only. Explicit selections are retained by their owning view.
export function latestFrame(frames:readonly {id:string;at:string}[]):string{
 const newest=frames.reduce<{id:string;at:string}|undefined>((latest,frame)=>!latest||Date.parse(frame.at)>Date.parse(latest.at)?frame:latest,undefined);
 return newest&&frames.filter(frame=>Date.parse(frame.at)===Date.parse(newest.at)).length===1?newest.id:'';
}
