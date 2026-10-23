import {navigationGroups} from './navigation-groups.js';
// market is the Market filter's commodity code, or null for All markets.
export type Preferences={version:1;timeZone:string;browseBy:'commodity'|'marketType';market:string|null;marketType:'all'|'futures'|'forwards'|'rates';tab:'prices'|'inputs'|'dependencies';sidebarCollapsed:boolean;libraryCollapsed:boolean;collapsedMenus:string[];collapsedCurveGroups:string[]};
export interface PreferenceStorage {getItem(key:string):string|null;setItem(key:string,value:string):void;}
// Collapsed groups are saved by navigation group id. The earlier three-group sidebar saved other ids; they name no current
// group, so a saved profile loses them instead of failing to load.
const groupIds:string[]=navigationGroups.map(group=>group.id),retiredMenus=['market-menu','settings-menu','trading-menu'];
export function validTimeZone(zone:unknown):zone is string {if(typeof zone!=='string')return false;try{new Intl.DateTimeFormat('en',{timeZone:zone});return true;}catch{return false;}}
// The time zones offered for a choice, UTC and the current choice first.
export function zoneChoices(current:string){return [...new Set(['UTC',current,...Intl.supportedValuesOf('timeZone')])].map(zone=>({value:zone,name:zone.replaceAll('_',' ')}));}
export function newPreferences(zone:string):Preferences {
  if(!validTimeZone(zone))throw Error('Choose a valid time zone.');
  return {version:1,timeZone:zone,browseBy:'commodity',market:null,marketType:'all',tab:'prices',sidebarCollapsed:false,libraryCollapsed:false,collapsedMenus:[],collapsedCurveGroups:[]};
}
export function parsePreferences(raw:string):Preferences {
  const v=JSON.parse(raw);
  if(v?.version!==1||!validTimeZone(v.timeZone)||!['commodity','marketType'].includes(v.browseBy)||v.market!=null&&(typeof v.market!=='string'||!v.market||v.market.length>128)||!['all','futures','forwards','rates'].includes(v.marketType)||!['prices','inputs','dependencies'].includes(v.tab))throw Error('Saved profile settings could not be read. Please save your preferences again.');
  for(const name of ['sidebarCollapsed','libraryCollapsed'])if(v[name]!==undefined&&typeof v[name]!=='boolean')throw Error('Saved layout settings could not be read.');
  for(const name of ['collapsedMenus','collapsedCurveGroups'])if(v[name]!==undefined&&(!Array.isArray(v[name])||v[name].length>64||v[name].some((x:unknown)=>typeof x!=='string'||x.length>128)||new Set(v[name]).size!==v[name].length))throw Error('Saved menu settings could not be read.');
  const menus:string[]=(v.collapsedMenus??[]).filter((x:string)=>!retiredMenus.includes(x));
  if(menus.some(x=>!groupIds.includes(x)))throw Error('Saved menu settings could not be read.');
  return {version:1,timeZone:v.timeZone,browseBy:v.browseBy,market:v.market??null,marketType:v.marketType,tab:v.tab,sidebarCollapsed:v.sidebarCollapsed??false,libraryCollapsed:v.libraryCollapsed??false,collapsedMenus:menus,collapsedCurveGroups:v.collapsedCurveGroups??[]};
}
const key=(profile:string)=>`openetrm.preferences.v1.${profile}`;
export function loadPreferences(storage:PreferenceStorage,profile:string,zone:string):Preferences {const saved=storage.getItem(key(profile));return saved===null?newPreferences(zone):parsePreferences(saved);}
export function savePreferences(storage:PreferenceStorage,profile:string,prefs:Preferences){storage.setItem(key(profile),JSON.stringify(parsePreferences(JSON.stringify(prefs))));}
