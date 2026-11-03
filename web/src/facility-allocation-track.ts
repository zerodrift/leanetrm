import {renderRegularTable} from './regular-table.js';
import type {FacilityAllocationTrack} from './ticket-types.js';

/** Read-only physical Side quantities; usable even when contractual PV is unavailable. */
export function facilityAllocationTrack(allocations:FacilityAllocationTrack[]){
 const track=document.createElement('details');track.className='ticket-facility-track';
 const summary=document.createElement('summary');summary.textContent='Facility allocation plan';
 const note=document.createElement('p');note.className='field-help';note.textContent='This trade’s physical Side shares of the facility schedule. Plans do not change billing quantities.';
 const table=document.createElement('div');table.className='table-wrap';track.append(summary,note,table);
 const scope=allocations.map(a=>a.obligationId).join(':');
 renderRegularTable(table,'physical-side-facility-'+scope,scope,['Physical Side','Delivery day','Planned quantity','State'],
  allocations.map(a=>[a.sideCode,a.day,a.quantity===null?'No current allocation':`${a.quantity} ${a.unit}`,a.state==='planned'?'Planned':'Removed from plan']),[],()=>false);
 return track;
}
