import {renderRegularTable} from './regular-table.js';
import type {FacilityAllocationTrack} from './ticket-types.js';

/** Read-only physical Side quantities; usable even when contractual PV is unavailable. */
export function facilityAllocationTrack(allocations:FacilityAllocationTrack[]){
 const track=document.createElement('details');track.className='ticket-facility-track';
 const summary=document.createElement('summary');summary.textContent='Facility allocations and final inputs';
 const note=document.createElement('p');note.className='field-help';note.textContent='This trade’s physical Side shares of the facility schedule. Accepted final inputs retain their allocation source. Plans alone do not change final inputs.';
 const table=document.createElement('div');table.className='table-wrap';track.append(summary,note,table);
 const scope=allocations.map(a=>a.obligationId).join(':');
 renderRegularTable(table,'physical-side-facility-'+scope,scope,['Physical Side','Delivery day','Planned quantity','State','Accepted final input'],
  allocations.map(a=>[a.sideCode,a.day,a.quantity===null?'No current allocation':`${a.quantity} ${a.unit}`,a.state==='planned'?'Planned':'Removed from plan',a.finalState==='final'?`${a.finalQuantity} ${a.unit}`:'Not accepted']),[],()=>false);
 return track;
}
