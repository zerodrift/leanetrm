import {dataSections} from './data-sections.js';

// The Reference Data menu, in order: the areas the sidebar lists, and the entries of each, which show as tabs across its pages. Tables
// come from data-sections.ts with their names; this names only the area each sits in and the setup pages listed with them. An entry
// may be one view of a page that has several (the first keeps the page's id), and may hold tables that are read from it and have no entry of their own. An entry a
// person may not open is left out, and with it any area left empty. The server still authorizes every read and write.
export type ConsoleEntry={id:string;name:string;page:string;table?:string;view?:string;alsoPages?:readonly string[];alsoTables?:readonly string[]};
export type ConsoleGroup={id:string;name:string;entries:readonly ConsoleEntry[]};
type PageEntry=Omit<ConsoleEntry,'table'|'alsoTables'>;
const page=(id:string,name:string):PageEntry=>({id,name,page:id});
const view=(id:string,name:string,of:string,shown:string):PageEntry=>({id,name,page:of,view:shown});
const layout:readonly {id:string;name:string;before?:readonly PageEntry[];after?:readonly PageEntry[]}[]=[
 {id:'curves',name:'Curves',before:[page('curve-definitions','Curve definitions'),page('custom-curves','Curve builder')]},
 {id:'pricing',name:'Pricing',before:[view('configuration','Contract pricing','configuration','contracts'),view('publications','Publications','configuration','publications')]},
 {id:'products',name:'Exchanges & conventions'},
 {id:'time',name:'Calendars',after:[view('schedules','Date schedules','schedules','exchange'),view('date-rules','Date rules','schedules','contracts'),page('power','Power TOUs')]},
 {id:'units',name:'Commodities & units'},
 {id:'physical',name:'Locations & delivery',after:[{id:'delivery-setup',name:'Delivery setup',page:'gas-setup',alsoPages:['crude-setup']}]},
 {id:'entities',name:'Legal entities',after:[page('parties','Entities & counterparties')]},
];
// The menu's areas by name, as the sidebar lists them.
export const consoleAreas:readonly {id:string;name:string}[]=layout.map(({id,name})=>({id,name}));

export function consoleMenu(o:{pageAvailable:(page:string)=>boolean;tables:readonly string[]}):readonly ConsoleGroup[]{
 const sections=dataSections([...o.tables]);
 return layout.map(group=>{
  const listed=sections.find(s=>s.name===group.name)?.sections??[];
  // An entry holds the tables that name it, where the account reads them.
  const pages=(entries:readonly PageEntry[]=[])=>entries.filter(e=>o.pageAvailable(e.page)).map(e=>{
   const held=listed.filter(s=>s.within===e.id).map(s=>s.value);return held.length?{...e,alsoTables:held}:e;
  });
  const tables=listed.filter(s=>!s.within).map(s=>({id:s.value,name:s.name,page:'reference',table:s.value}));
  return {id:group.id,name:group.name,entries:[...pages(group.before),...tables,...pages(group.after)]};
 }).filter(group=>group.entries.length);
}

// The name of the entry that is a view of a page, which titles the page while that view shows.
export function consoleViewName(page:string,view:string):string{
 const entry=layout.flatMap(g=>[...g.before??[],...g.after??[]]).find(e=>e.page===page&&e.view===view);
 if(!entry)throw Error('This page could not load. Please refresh.');
 return entry.name;
}

// The entry that shows a page: a table's own, or the one that holds it; the view of a page that has several; else the page's.
export function consoleEntryFor(menu:readonly ConsoleGroup[],page:string,table?:string,view?:string):ConsoleEntry|undefined{
 const entries=menu.flatMap(g=>g.entries);
 if(table!==undefined)return page==='reference'?entries.find(e=>e.table===table)??entries.find(e=>e.alsoTables?.includes(table)):undefined;
 return entries.find(e=>!e.table&&(e.page===page||e.alsoPages?.includes(page))&&e.view===view);
}
