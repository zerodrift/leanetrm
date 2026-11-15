import {consoleEntryFor,type ConsoleEntry,type ConsoleGroup} from './console-menu.js';

// Navigation owns no reads: the sidebar's entry for each area of Reference Data, and across the top of the area's pages its entries
// and the search of reference data. There is one list of areas, the sidebar's. The existing page controller applies the leave,
// context and authority guards before calling show after a successful move, so a refused leave keeps the entry shown marked.
export function setupReferenceConsole(o:{host:HTMLElement;menu:()=>readonly ConsoleGroup[];open:(entry:ConsoleEntry)=>void;areas?:HTMLElement;search:(text:string)=>void;busy:()=>boolean;pendingContext?:()=>boolean}){
 const part=<T extends HTMLElement>(id:string)=>o.host.querySelector<T>('#'+id)!;
 const groups=part('reference-console-groups'),form=part<HTMLFormElement>('reference-search-all'),text=part<HTMLInputElement>('reference-search-all-text');
 const buttons=new Map<string,HTMLButtonElement>(),lists=new Map<string,HTMLUListElement>();
 const areas=[...o.areas?.querySelectorAll<HTMLButtonElement>('[data-area]')??[]],lastIn=new Map<string,string>();
 // The entry shown, and whether what shows is its own page rather than a table read from it.
 let drawn='',shown:ConsoleEntry|undefined,own=false,visible=false,refocus:string|undefined,wanted:string|undefined;
 // The page last shown for an entry that has several, such as Delivery setup's commodities, so the entry reopens it.
 const lastPages=new Map<string,string>();
 const reopened=(entry:ConsoleEntry)=>{const page=lastPages.get(entry.id);return page?{...entry,page}:entry;};
 const groupOf=(id:string)=>o.menu().find(g=>g.entries.some(e=>e.id===id));
 // A table entry has one id; the buttons of pages keep the sidebar's nav-<page> address.
 const domId=(entry:ConsoleEntry)=>entry.table?'nav-table-'+entry.table:'nav-'+entry.id;
 function draw(menu:readonly ConsoleGroup[]){
  buttons.clear();lists.clear();
  groups.replaceChildren(...menu.map(group=>{
   const list=document.createElement('ul');list.className='reference-console-entries';list.setAttribute('aria-label',group.name);
   list.append(...group.entries.map(entry=>{
    const item=document.createElement('li'),button=document.createElement('button');
    button.type='button';button.id=domId(entry);button.textContent=entry.name;
    button.onclick=()=>{if(o.busy()||(own&&shown?.id===entry.id&&!entry.table&&!o.pendingContext?.()))return;refocus=entry.id;o.open(reopened(entry));};
    item.append(button);buttons.set(entry.id,button);return item;
   }));
   lists.set(group.id,list);return list;
  }));
 }
 function sync(){
  const menu=o.menu(),key=JSON.stringify(menu.map(g=>[g.id,g.entries.map(e=>e.id)]));
  if(key!==drawn){drawn=key;draw(menu);}
  o.host.hidden=!visible;text.disabled=o.busy();
  // Only the area shown lists its entries, and one entry is nothing to choose between.
  const current=shown&&groupOf(shown.id);
  for(const [id,list] of lists)list.hidden=id!==current?.id||current.entries.length<2;
  // Until this account's tables are read, an area that holds only tables is still listed.
  const tablesRead=menu.some(g=>g.entries.some(e=>e.table));
  if(wanted&&visible&&!shown&&!o.busy()){const group=menu.find(g=>g.id===wanted);if(group||tablesRead)wanted=undefined;if(group)o.open(group.entries[0]);}
  for(const area of areas){const group=menu.find(g=>g.id===area.dataset.area);area.hidden=tablesRead?!group:!menu.length;area.disabled=o.busy();area.classList.toggle('active',visible&&!!group&&group.id===current?.id);}
  for(const [id,button] of buttons){button.disabled=o.busy();if(shown?.id===id)button.setAttribute('aria-current','page');else button.removeAttribute('aria-current');}
  // Entries are disabled while a page is read, which takes focus from the one chosen; it is given back once that page shows. Focus
  // the person moves elsewhere, as when they answer a leave question, cancels it.
  const active=document.activeElement;if(refocus&&active&&active!==document.body&&!o.host.contains(active))refocus=undefined;
  if(refocus&&!o.busy()&&shown?.id===refocus){const target=buttons.get(refocus);target?.focus({preventScroll:true});if(document.activeElement===target)refocus=undefined;}
 }
 // An area returns to the entry last shown in it, else opens its first.
 for(const area of areas)area.onclick=()=>{
  const group=o.menu().find(g=>g.id===area.dataset.area);
  if(o.busy()||(group&&visible&&group.entries.some(e=>e.id===shown?.id)&&!o.pendingContext?.()))return;
  // An area of tables not yet read opens the console, which reads them; its first entry opens once they are.
  if(!group){wanted=area.dataset.area;o.open({id:'reference',name:'Reference Data',page:'reference'});return;}
  o.open(reopened(group.entries.find(e=>e.id===lastIn.get(group.id))??group.entries[0]));
 };
 form.onsubmit=event=>{event.preventDefault();if(!o.busy())o.search(text.value);};
 return {sync,
  // `reference` shows the console before its table is known; a page of several views names the one it shows.
  show(page:string,table?:string,view?:string){
   const entry=consoleEntryFor(o.menu(),page,table,view);
   visible=!!entry||page==='reference';shown=entry;own=!!entry&&(table===undefined||entry.table===table);
   if(entry&&own)lastPages.set(entry.id,page);if(!visible||entry)wanted=undefined;const group=entry&&groupOf(entry.id);if(group)lastIn.set(group.id,entry.id);
   sync();
  },
  reset(){shown=undefined;own=false;wanted=undefined;lastIn.clear();visible=false;text.value='';sync();},
 };
}
