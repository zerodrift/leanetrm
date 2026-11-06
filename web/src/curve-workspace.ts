const views=[['market','curve-price-contracts'],['custom-curves','nav-custom-curves'],['interval-curves','nav-interval-curves']] as const;
type CurvePage=typeof views[number][0];
export const isCurvePage=(page:string):page is CurvePage=>views.some(([name])=>name===page);

// Navigation owns no reads. The existing page controller applies the leave,
// context and authority guards before calling show after a successful move.
export function setupCurveWorkspace(o:{open:(page:CurvePage)=>void;available:(page:CurvePage)=>boolean;busy:()=>boolean;pendingContext?:()=>boolean}){
 const get=(id:string)=>document.getElementById(id)!;
 const root=get('curve-price-navigation'),entry=get('nav-market');
 let shown='',last:CurvePage='market';
 const current=()=>isCurvePage(shown)&&o.available(shown);
 const destination=()=>o.available(last)?last:'market';
 function open(page:CurvePage){if(!o.busy()&&o.available(page)&&(shown!==page||o.pendingContext?.()))o.open(page);}
 entry.onclick=()=>{if(!current()||o.pendingContext?.())open(destination());};
 for(const [page,id] of views)get(id).onclick=()=>open(page);
 function sync(){
  root.hidden=!current();entry.classList.toggle('active',current());
  for(const [page,id] of views){
   const button=get(id) as HTMLButtonElement;
   button.hidden=!o.available(page);button.disabled=o.busy()||!o.available(page);
   button.setAttribute('aria-pressed',String(shown===page));
  }
 }
 return{sync,show(page:string){shown=page;if(isCurvePage(page))last=page;sync();},reset(){shown='';last='market';sync();}};
}
