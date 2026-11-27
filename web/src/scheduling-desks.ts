import {schedulingDesks,pageAvailable,type Desk,type DeskId} from './navigation-groups.js';

type DeskStorage=Pick<Storage,'getItem'|'setItem'>;
const key=(owner:string)=>'openetrm:desk:v1:'+owner;
const lists=(desk:DeskId,entry:string)=>schedulingDesks.find(d=>d.id===desk)!.pages.includes(entry);
/** The desks an account can open: each lists at least one entry its permissions allow. */
export const availableDesks=(permissions:string[]):Desk[]=>schedulingDesks.filter(desk=>desk.pages.some(page=>pageAvailable(page,permissions)));
/** The desk an entry is shown under: the current desk when it lists the entry, else the first available desk that does. */
export function deskFor(entry:string,current:DeskId|null,permissions:string[]):DeskId|null{
 const desks=availableDesks(permissions).filter(desk=>desk.pages.includes(entry));
 return desks.find(desk=>desk.id===current)?.id??desks[0]?.id??null;
}

// Navigation only: which desk's entries the Scheduling group lists. Choosing a desk opens its first entry through that entry's own
// guards and takes effect once the page has moved; a refused leave changes nothing. Only the desk's identifier is remembered,
// per tab, workspace and account.
export function setupSchedulingDesks(o:{permissions:()=>string[];owner:()=>string;storage:()=>DeskStorage;open:(entry:string)=>void;busy:()=>boolean}){
 const root=document.getElementById('scheduling-desks')!,entries=[...new Set(schedulingDesks.flatMap(desk=>desk.pages))];
 let desk:DeskId|null=null,wanted:DeskId|undefined;
 function draw(){
  const desks=availableDesks(o.permissions());
  root.hidden=desks.length<2;
  root.replaceChildren(...desks.map(d=>{
   const button=document.createElement('button');button.type='button';button.id='scheduling-desk-'+d.id;button.className='nav-desk';button.textContent=d.label;
   button.setAttribute('aria-pressed',String(d.id===desk));button.disabled=o.busy();
   button.onclick=()=>{if(o.busy()||d.id===desk)return;const first=d.pages.find(page=>pageAvailable(page,o.permissions()));if(first===undefined)return;wanted=d.id;o.open(first);};
   return button;
  }));
  for(const entry of entries)(document.getElementById('nav-'+entry) as HTMLElement).hidden=!pageAvailable(entry,o.permissions())||desk===null||!lists(desk,entry);
 }
 function set(next:DeskId|null){
  if(next===null||next===desk)return;
  desk=next;try{o.storage().setItem(key(o.owner()),next);}catch{/* Navigation also works when browser storage is unavailable. */}
  draw();
 }
 return {
  current:()=>desk,
  label:()=>schedulingDesks.find(d=>d.id===desk)?.label??null,
  // Called when permissions are known: the remembered desk if the account can still open it, else the first it can.
  start(){
   const desks=availableDesks(o.permissions());let saved:string|null=null;try{saved=o.storage().getItem(key(o.owner()));}catch{/* As above. */}
   desk=desks.find(d=>d.id===saved)?.id??desks[0]?.id??null;wanted=undefined;draw();
  },
  // Called as the app moves to a page, with the Scheduling entry it shows. A desk chosen in the switcher, or wanted for an exact
  // target, takes effect here when it lists the entry.
  page(entry:string){const chosen=wanted;wanted=undefined;if(!entries.includes(entry))return;set(chosen!==undefined&&lists(chosen,entry)?chosen:deskFor(entry,desk,o.permissions()));},
  // Called when the entry shown may have changed without a page move, such as a Facilities section opened from elsewhere.
  follow(entry:string){if(entries.includes(entry))set(deskFor(entry,desk,o.permissions()));},
  // The desk an exact target belongs to, used by the next page move.
  want(next:DeskId){wanted=next;},
  sync(){for(const button of root.querySelectorAll('button'))button.disabled=o.busy();},
 };
}
