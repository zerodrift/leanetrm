import {renderDataGrid,type GridOptions} from './data-grid.js';
import {operationsTag} from './physical-operations-grid.js';
import {physicalDeliveryGrid,type PhysicalDeliveryView} from './physical-delivery.js';
export function renderPhysicalDeliveryGrid(host:HTMLElement,id:string,identity:string,view:PhysicalDeliveryView,zone:string,busy:()=>boolean,options:Omit<GridOptions,'keys'|'rowKeys'|'displayRows'|'searchText'|'cells'>){
 const model=physicalDeliveryGrid(view,zone);
 return renderDataGrid(host,id,identity,model.headers,model.rows,model.numeric,busy,{...options,keys:model.keys,rowKeys:model.rowKeys,displayRows:model.displayRows,searchText:model.searchText,
  cells:(row,column)=>{
   if(model.keys[column]==='status'){const status=view.rows[row].sourceStatus;return operationsTag(model.rows[row][column],['final','provisional','withdrawn'].includes(status)?status:'neutral');}
   if(column>=4&&column<4+view.tracks.length){const source=view.rows[row].heads.find(h=>h.kind===view.tracks[column-4].code);
    if(source?.status==='withdrawn')return operationsTag('Withdrawn','withdrawn');
    if(source){const value=document.createElement('span');value.className='operations-recorded-quantity';value.dataset.status=source.status;value.textContent=model.displayRows[row][column];value.title=`${source.status} · ${source.evidence}`;return value;}}
   return null;
  }});
}
export function renderDeliveryCapabilities(host:HTMLElement,view:PhysicalDeliveryView){
 const text=document.createElement('p');text.className='operations-mode';
 for(const c of view.capabilities){const item=document.createElement('span');item.textContent=c.label+(c.implemented?'':' · TBD');text.append(item);}
 host.append(text);
}
