import {aiTabs} from './agent-settings.js';

// The sidebar's groups in order, each with the pages it lists. A page is named by the id of its entry (`nav-<page>`),
// a sidebar button, or for the hourly prices of Curves & prices the switch beside its contract prices; its group's
// label is the eyebrow above the page title. `opened` are a group's pages that another page opens instead of the sidebar:
// PPA bookings, from the Blotter's new trade panel and its PPA positions.
export type NavigationGroup={id:'overview'|'trading'|'markets'|'operations'|'settings'|'admin';label:string;pages:readonly string[];opened?:readonly string[]};
export const navigationGroups:readonly NavigationGroup[]=[
 {id:'overview',label:'Overview',pages:['dashboard']},
 {id:'trading',label:'Trading',pages:['futures','gas','crude'],opened:['ppa']},
 {id:'markets',label:'Markets',pages:['market','custom-curves','delivery','interval-series','interval-curves']},
 {id:'operations',label:'Operations',pages:['jobs']},
 {id:'settings',label:'Settings',pages:['configuration','schedules','power','gas-setup','reference','parties']},
 {id:'admin',label:'Admin',pages:['admin','ai']},
];

const unknownPage=()=>Error('This page could not load. Please refresh.');
export function pageGroup(page:string):string{
 const group=navigationGroups.find(g=>g.pages.includes(page)||g.opened?.includes(page));
 if(!group)throw unknownPage();
 return group.label;
}

// Whether an account's sidebar lists a page. The server still authorizes every read and write.
export function pageAvailable(page:string,permissions:string[]):boolean{
 const has=(...needed:string[])=>needed.every(permission=>permissions.includes(permission));
 switch(page){
  case 'futures':case 'ppa':case 'gas':case 'crude':return has('ReadMarketData','ReadTrades');
  case 'dashboard':case 'market':case 'custom-curves':case 'delivery':case 'interval-series':case 'interval-curves':
  case 'configuration':case 'schedules':case 'power':case 'gas-setup':case 'reference':return has('ReadMarketData');
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
