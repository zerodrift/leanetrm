import {dataSections} from './data-sections.js';

// The Reference Data console's menu, in order. Tables come from data-sections.ts with their names; this names only the group each
// sits in and the setup pages listed with them. An entry a person may not open is left out, and with it any group left empty. The
// server still authorizes every read and write.
export type ConsoleEntry={id:string;name:string;page:string;table?:string;alsoPages?:readonly string[]};
export type ConsoleGroup={id:string;name:string;entries:readonly ConsoleEntry[]};
type PageEntry=Omit<ConsoleEntry,'table'>;
const page=(id:string,name:string):PageEntry=>({id,name,page:id});
const layout:readonly {id:string;name:string;before?:readonly PageEntry[];after?:readonly PageEntry[]}[]=[
 {id:'curves',name:'Curves & pricing',before:[page('curve-definitions','Curve definitions'),page('custom-curves','Curve builder'),page('configuration','Price setup')]},
 {id:'time',name:'Time',after:[page('schedules','Schedules & date mappings'),page('power','Power TOUs')]},
 {id:'products',name:'Products'},
 {id:'units',name:'Commodities & units'},
 {id:'physical',name:'Physical',before:[{id:'delivery-setup',name:'Delivery setup',page:'gas-setup',alsoPages:['crude-setup']}]},
 {id:'entities',name:'Entities',after:[page('parties','Entities & counterparties')]},
];

export function consoleMenu(o:{pageAvailable:(page:string)=>boolean;tables:readonly string[]}):readonly ConsoleGroup[]{
 const sections=dataSections([...o.tables]);
 return layout.map(group=>{
  const pages=(entries:readonly PageEntry[]=[])=>entries.filter(e=>o.pageAvailable(e.page));
  const tables=sections.find(s=>s.name===group.name)?.sections.map(s=>({id:s.value,name:s.name,page:'reference',table:s.value}))??[];
  return {id:group.id,name:group.name,entries:[...pages(group.before),...tables,...pages(group.after)]};
 }).filter(group=>group.entries.length);
}

export function consoleEntryFor(menu:readonly ConsoleGroup[],page:string,table?:string):ConsoleEntry|undefined{
 return menu.flatMap(g=>g.entries).find(e=>table===undefined?!e.table&&(e.page===page||e.alsoPages?.includes(page)):e.table===table&&e.page===page);
}
