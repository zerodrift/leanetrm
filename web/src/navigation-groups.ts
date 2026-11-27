import {aiTabs} from './agent-settings.js';

// The sidebar's groups in order, each with the pages it lists. A page is named by the id of its entry (`nav-<page>`),
// a sidebar button or a view inside its workspace; the group's
// label is the eyebrow above the page title. New trade is a presentation of the Blotter's page (D421), as Risk's entries are views of
// Risk, and Facilities' entries are views of Facilities. `opened` are a group's pages that another page opens instead of the sidebar:
// Product bookings, from the Blotter's new trade panel and its positions; the setup pages, from the Reference Data console's menu.
// Reference Data's entry is the list of its console's areas (console-menu.ts).
export type NavigationGroup={id:'overview'|'trading'|'markets'|'risk'|'scheduling'|'reference'|'administration';label:string;pages:readonly string[];opened?:readonly string[]};
export const navigationGroups:readonly NavigationGroup[]=[
 {id:'overview',label:'Overview',pages:['dashboard']},
 {id:'trading',label:'Trading',pages:['new-trade','futures','options','lifecycle','physical-power'],opened:['ppa','gas','crude']},
 {id:'markets',label:'Markets',pages:['market','delivery','interval-series','curve-marks','vol-surfaces','interval-curves']},
 {id:'risk',label:'Risk',pages:['risk','risk-pnl','risk-scenarios','risk-var','risk-runs']},
 {id:'scheduling',label:'Scheduling',pages:['facilities-position','physical-operations','facilities-transport','facilities','facilities-crude','rec-inventory']},
 {id:'reference',label:'Reference Data',pages:['reference'],opened:['curve-definitions','custom-curves','configuration','schedules','power','parties','gas-setup','crude-setup']},
 {id:'administration',label:'Administration',pages:['jobs','admin','ai']},
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
  case 'rec-inventory':return has('ReadMarketData','ReadTrades');
  case 'physical-power':return has('ReadMarketData','ReadPhysical');
  // Views of Facilities (facilities-views.ts). Each rule is the gate of its sections in facilities-workspace.ts.
  case 'facilities-position':case 'facilities-transport':return has('ReadMarketData','ReadTrades','ReadGasOperations');
  case 'facilities-crude':return has('ReadMarketData','ReadTrades');
  case 'facilities':return has('ReadMarketData','ReadTrades');
  // Options and Vol surfaces read option products, which trade readers see.
  case 'lifecycle':case 'physical-operations':case 'new-trade':case 'futures':case 'options':case 'ppa':case 'gas':case 'crude':case 'vol-surfaces':return has('ReadMarketData','ReadTrades');
  case 'dashboard':case 'market':case 'curve-marks':case 'custom-curves':case 'curve-definitions':case 'delivery':case 'interval-series':case 'interval-curves':
  case 'configuration':case 'schedules':case 'power':case 'gas-setup':case 'crude-setup':case 'reference':return has('ReadMarketData');
  case 'parties':return has('ReadMarketData','ReadTradeParties');
  case 'risk':case 'risk-pnl':case 'risk-scenarios':case 'risk-var':case 'risk-runs':return has('ReadMarketData','ReadTrades','RunScenario');
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
