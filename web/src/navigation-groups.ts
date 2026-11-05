import {aiTabs} from './agent-settings.js';

// The sidebar's groups in order, each with the pages it lists. A page is named by the id of its entry (`nav-<page>`),
// a sidebar button, or for the hourly prices of Curves & prices the switch beside its contract prices; its group's
// label is the eyebrow above the page title. `opened` are a group's pages that another page opens instead of the sidebar:
// Product bookings, from the Blotter's new trade panel and its positions.
export type NavigationGroup={id:'overview'|'trading'|'markets'|'operations'|'settings'|'admin';label:string;pages:readonly string[];opened?:readonly string[]};
export const navigationGroups:readonly NavigationGroup[]=[
 {id:'overview',label:'Overview',pages:['dashboard']},
 {id:'trading',label:'Trading',pages:['futures'],opened:['ppa','gas','crude']},
 {id:'markets',label:'Markets',pages:['market','custom-curves','delivery','interval-series','vol-surfaces','interval-curves']},
 {id:'operations',label:'Operations',pages:['physical','risk','jobs'],opened:['facilities','gas-setup','crude-setup']},
 {id:'settings',label:'Settings',pages:['schedules','power','reference','parties'],opened:['configuration']},
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
  case 'facilities':return has('ReadMarketData','ReadTrades','ReadFacilityQuantities');
  // Vol surfaces read option products, which trade readers see.
  case 'futures':case 'ppa':case 'gas':case 'crude':case 'vol-surfaces':return has('ReadMarketData','ReadTrades');
  case 'dashboard':case 'market':case 'custom-curves':case 'delivery':case 'interval-series':case 'interval-curves':
  case 'configuration':case 'schedules':case 'power':case 'physical':case 'gas-setup':case 'crude-setup':case 'reference':return has('ReadMarketData');
  case 'parties':return has('ReadMarketData','ReadTradeParties');
  case 'risk':return has('ReadMarketData','ReadTrades','RunScenario');
  case 'jobs':return true; // listed for every account
  case 'admin':return has('ManageAccess');
  case 'ai':return aiTabs(permissions).length>0;
  default:throw unknownPage();
 }
}

// Where an account opens: the Blotter for trade readers, else curves for market readers, else what it administers.
const firstPages=['futures','market','admin','ai'] as const;
export function firstPage(permissions:string[]):typeof firstPages[number]|null{
 return firstPages.find(page=>pageAvailable(page,permissions))??null;
}
