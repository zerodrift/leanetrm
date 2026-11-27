// Facilities is one page of nine sections. The sidebar lists it as four views, each holding the sections of one kind of work, in the
// order their tabs show. A view keeps the page's permissions: its sidebar rule in navigation-groups.ts copies its sections' gates.
export type FacilitySection='trades'|'outlook'|'quantities'|'definitions'|'activity'|'transport'|'route'|'gas-delivery'|'crude-delivery';
export const facilityViews=['facilities-position','facilities-transport','facilities','facilities-crude'] as const;
export type FacilityView=typeof facilityViews[number];
export const facilityViewSections:Record<FacilityView,readonly FacilitySection[]>={
 'facilities-position':['outlook'],
 'facilities-transport':['gas-delivery','route','transport'],
 facilities:['quantities','activity','trades','definitions'],
 'facilities-crude':['crude-delivery']};
export const facilityViewNames:Record<FacilityView,string>={'facilities-position':'Gas day position','facilities-transport':'Nominations & transport',facilities:'Storage & allocations','facilities-crude':'Receipts & dispatches'};
export const facilitySectionNames:Record<FacilitySection,string>={outlook:'Gas day position','gas-delivery':'Nominations',route:'Route plans',transport:'Capacity',
 quantities:'Allocations',activity:'Physical activity',trades:'Trades and volumes',definitions:'Definitions','crude-delivery':'Receipts & dispatches'};
export const facilityViewOf=(section:FacilitySection):FacilityView=>facilityViews.find(view=>facilityViewSections[view].includes(section))!;
export const isFacilityView=(entry:string):entry is FacilityView=>(facilityViews as readonly string[]).includes(entry);
// The permissions that open a section, for the workspace's tabs and the sidebar's entries alike. The server still authorizes every
// read and write.
export function facilitySectionAvailable(section:FacilitySection,has:(...rights:string[])=>boolean):boolean{
 switch(section){
  case 'trades':case 'crude-delivery':return has('ReadMarketData','ReadTrades');
  case 'outlook':case 'gas-delivery':case 'transport':case 'route':return has('ReadMarketData','ReadTrades','ReadGasOperations');
  case 'quantities':case 'definitions':case 'activity':return has('ReadMarketData','ReadTrades','ReadFacilityQuantities');
 }
}
export const facilityViewAvailable=(view:FacilityView,has:(...rights:string[])=>boolean):boolean=>facilityViewSections[view].some(section=>facilitySectionAvailable(section,has));

// Navigation only: the sidebar's Facilities entries. An entry is marked while Facilities shows a section of its view, whichever
// route opened that section. From another page an entry opens Facilities through the existing guards and takes effect once the
// page has moved: `open` calls `arrive` as its navigation moves the page, and only then is the view chosen, so a question still
// open for another entry cannot take it. `open` and `choose` settle when their navigation has finished or been refused; a refused
// leave changes nothing. Arriving without a chosen entry shows the view `fallback` names, if any.
export function setupFacilityViews(o:{view:()=>FacilityView;choose:(view:FacilityView)=>Promise<void>|void;prepare:(view:FacilityView)=>void;open:(arrive:()=>void)=>Promise<void>|void;onPage:()=>boolean;busy:()=>boolean;available:(view:FacilityView)=>boolean;fallback:()=>FacilityView|undefined}){
 const button=(view:FacilityView)=>document.getElementById('nav-'+view) as HTMLButtonElement;
 let wanted:FacilityView|undefined,shown=false;
 function sync(){for(const view of facilityViews){button(view).disabled=o.busy();button(view).classList.toggle('active',shown&&view===o.view());}}
 // `arrive` is a caller's own step at the same moment, such as the desk that lists the entry.
 async function open(view:FacilityView,arrive?:()=>void){
  if(o.busy()||!o.available(view))return;
  if(o.onPage()){if(view!==o.view())await o.choose(view);return;}
  try{await o.open(()=>{wanted=view;arrive?.();});}finally{if(wanted===view)wanted=undefined;}
 }
 for(const view of facilityViews)button(view).onclick=()=>void open(view);
 return {sync,open,
  // Called as the app moves to a page, before that page reads.
  page(page:string){shown=page==='facilities';if(shown){const view=wanted??o.fallback();if(view)o.prepare(view);}wanted=undefined;sync();},
  reset(){wanted=undefined;shown=false;sync();},
 };
}
