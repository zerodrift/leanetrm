type Version={id:string;version:number};
// Presentation over the authorized library; commands retain their explicit base.
export function currentAuthoringVersion(selected:Version|undefined,versions:readonly Version[]):boolean{
 return !selected||versions.some(v=>v.id===selected.id&&v.version===selected.version)&&!versions.some(v=>v.id===selected.id&&v.version>selected.version);
}
