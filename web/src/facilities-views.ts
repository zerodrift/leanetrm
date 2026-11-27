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

// Navigation only: the sidebar's Facilities entries. An entry is marked while Facilities shows a section of its view, whichever
// route opened that section. From another page an entry opens Facilities through the existing guards and takes effect once the
// page has moved; a refused leave changes nothing.
export function setupFacilityViews(o:{view:()=>FacilityView;choose:(view:FacilityView)=>void;prepare:(view:FacilityView)=>void;open:()=>void;onPage:()=>boolean;busy:()=>boolean;available:(view:FacilityView)=>boolean}){
 const button=(view:FacilityView)=>document.getElementById('nav-'+view) as HTMLButtonElement;
 let wanted:FacilityView|undefined,shown=false;
 function sync(){for(const view of facilityViews){button(view).disabled=o.busy();button(view).classList.toggle('active',shown&&view===o.view());}}
 for(const view of facilityViews)button(view).onclick=()=>{
  if(o.busy()||!o.available(view))return;
  if(!o.onPage()){wanted=view;o.open();}
  else if(view!==o.view())o.choose(view);
 };
 return {sync,
  // Called as the app moves to a page, before that page reads.
  page(page:string){shown=page==='facilities';if(shown&&wanted)o.prepare(wanted);wanted=undefined;sync();},
  reset(){wanted=undefined;shown=false;sync();},
 };
}
