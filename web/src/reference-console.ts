import {consoleEntryFor,type ConsoleEntry,type ConsoleGroup} from './console-menu.js';

// Navigation owns no reads: the Reference Data console's menu. The existing page controller applies the leave, context and authority
// guards before calling show after a successful move, so a refused leave keeps the entry shown marked.
export function setupReferenceConsole(o:{host:HTMLElement;menu:()=>readonly ConsoleGroup[];open:(entry:ConsoleEntry)=>void;search:(text:string)=>void;busy:()=>boolean}){
 const part=<T extends HTMLElement>(id:string)=>o.host.querySelector<T>('#'+id)!;
 const groups=part('reference-console-groups'),form=part<HTMLFormElement>('reference-search-all'),text=part<HTMLInputElement>('reference-search-all-text');
 const toggle=part('reference-console-toggle'),body=part('reference-console-body');
 const buttons=new Map<string,HTMLButtonElement>(),sections=new Map<string,HTMLDetailsElement>();
 let drawn='',shown:ConsoleEntry|undefined,last:string|undefined,visible=false,refocus:string|undefined;
 const groupOf=(id:string)=>o.menu().find(g=>g.entries.some(e=>e.id===id));
 // A table entry has one id; the buttons of pages keep the sidebar's nav-<page> address.
 const domId=(entry:ConsoleEntry)=>entry.table?'nav-table-'+entry.table:'nav-'+entry.id;
 function draw(menu:readonly ConsoleGroup[]){
  buttons.clear();sections.clear();
  groups.replaceChildren(...menu.map(group=>{
   const section=document.createElement('details'),summary=document.createElement('summary'),list=document.createElement('ul');
   summary.textContent=group.name;section.className='reference-console-group';
   list.append(...group.entries.map(entry=>{
    const item=document.createElement('li'),button=document.createElement('button');
    button.type='button';button.id=domId(entry);button.textContent=entry.name;
    button.onclick=()=>{if(o.busy()||(shown?.id===entry.id&&!entry.table))return;refocus=entry.id;o.open(entry);};
    item.append(button);buttons.set(entry.id,button);return item;
   }));
   section.append(summary,list);sections.set(group.id,section);return section;
  }));
 }
 const openShownGroup=()=>{const current=shown&&groupOf(shown.id);for(const [id,section] of sections)section.open=id===current?.id;};
 // Typing narrows the entries; the groups with a match open so they can be seen, and an empty box returns to the group shown.
 function filter(){
  const query=text.value.trim().toLowerCase();
  for(const group of o.menu()){
   const section=sections.get(group.id)!;let matches=0;
   for(const entry of group.entries){const match=!query||entry.name.toLowerCase().includes(query);buttons.get(entry.id)!.parentElement!.hidden=!match;if(match)matches++;}
   section.hidden=!matches;if(query)section.open=matches>0;
  }
 }
 function sync(){
  const menu=o.menu(),key=JSON.stringify(menu.map(g=>[g.id,g.entries.map(e=>e.id)]));
  if(key!==drawn){drawn=key;draw(menu);openShownGroup();filter();}
  o.host.hidden=!visible;document.body.classList.toggle('in-console',visible);
  text.disabled=o.busy();
  for(const [id,button] of buttons){button.disabled=o.busy();if(shown?.id===id)button.setAttribute('aria-current','page');else button.removeAttribute('aria-current');}
  // Entries are disabled while a page is read, which takes focus from the one chosen; it is given back afterwards.
  if(refocus&&!o.busy()){buttons.get(refocus)?.focus({preventScroll:true});refocus=undefined;}
 }
 text.oninput=filter;
 form.onsubmit=event=>{event.preventDefault();if(!o.busy())o.search(text.value);};
 function fold(on:boolean){body.hidden=on;o.host.classList.toggle('folded',on);document.body.classList.toggle('console-folded',on);toggle.setAttribute('aria-expanded',String(!on));toggle.setAttribute('aria-label',`${on?'Expand':'Collapse'} the Reference Data menu`);toggle.querySelector('span')!.textContent=on?'›':'‹';}
 toggle.onclick=()=>fold(!body.hidden);
 return {sync,fold,
  // `reference` shows the console before its table is known.
  show(page:string,table?:string){
   const entry=consoleEntryFor(o.menu(),page,table);
   visible=!!entry||page==='reference';shown=entry;if(entry)last=entry.id;
   sync();openShownGroup();filter();
  },
  destination():ConsoleEntry|null{const entries=o.menu().flatMap(g=>g.entries);return entries.find(e=>e.id===last)??entries[0]??null;},
  reset(){shown=undefined;last=undefined;visible=false;text.value='';fold(false);sync();},
 };
}
