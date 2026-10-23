import {aiTabs} from './agent-settings.js';

// The sidebar's groups in order, each with the pages it lists. A page is named by the id of its sidebar entry
// (`nav-<page>`); its group's label is the eyebrow above the page title.
export type NavigationGroup={id:'trading'|'markets'|'operations'|'settings'|'admin';label:string;pages:readonly string[]};
export const navigationGroups:readonly NavigationGroup[]=[
 {id:'trading',label:'Trading',pages:['futures','ppa']},
 {id:'markets',label:'Markets',pages:['market','custom-curves','delivery','interval-series','interval-curves']},
 {id:'operations',label:'Operations',pages:['jobs']},
 {id:'settings',label:'Settings',pages:['configuration','schedules','power','reference','parties']},
 {id:'admin',label:'Admin',pages:['admin','ai']},
];

const unknownPage=()=>Error('This page could not load. Please refresh.');
export function pageGroup(page:string):string{
 const group=navigationGroups.find(g=>g.pages.includes(page));
 if(!group)throw unknownPage();
 return group.label;
}

// Whether an account's sidebar lists a page. The server still authorizes every read and write.
export function pageAvailable(page:string,permissions:string[]):boolean{
 const has=(...needed:string[])=>needed.every(permission=>permissions.includes(permission));
 switch(page){
  case 'futures':case 'ppa':return has('ReadMarketData','ReadTrades');
  case 'market':case 'custom-curves':case 'delivery':case 'interval-series':case 'interval-curves':
  case 'configuration':case 'schedules':case 'power':case 'reference':return has('ReadMarketData');
  case 'parties':return has('ReadMarketData','ReadTradeParties');
  case 'jobs':return true; // listed for every account
  case 'admin':return has('ManageAccess','ManageIdentities');
  case 'ai':return aiTabs(permissions).length>0;
  default:throw unknownPage();
 }
}

// Where an account opens: the Blotter for trade readers, else curves for market readers, else what it administers.
const firstPages=['futures','market','admin','ai'] as const;
export function firstPage(permissions:string[]):typeof firstPages[number]|null{
 return firstPages.find(page=>pageAvailable(page,permissions))??null;
}
