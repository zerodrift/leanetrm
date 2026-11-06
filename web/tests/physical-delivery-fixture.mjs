// Synthetic native responses for controller checks. Financial quantities and
// selections are supplied by each scenario, not calculated by the browser.
export function gasDeliveryFixture(daily){
 const reference=code=>({revision:{catalogId:'synthetic-reference',revisionId:'synthetic-version'},code});
 const text=q=>q.denominator==='1'?q.numerator:q.numerator+'/'+q.denominator;
 return{version:'physical_delivery_view_v1',tradeId:daily.tradeId??'trade',bindingId:'binding',revision:daily.expectedRevision,periodLabel:'Delivery day',selectionPolicy:'operational_projection_v1',
  tracks:[{code:'nominated',label:'Nominated'},{code:'confirmed',label:'Confirmed'},{code:'scheduled',label:'Scheduled'},{code:'allocated',label:'Allocated'}],attributes:[{code:'pipeline',label:'Pipeline'},{code:'zone',label:'Zone'}],capabilities:[{code:'gas_manual_operations_v1',label:'Manual scheduling & reconciliation',implemented:true}],
  rows:daily.rows.map((r,i)=>({key:r.obligationId+':'+r.day,sideCode:r.sideCode,period:r.day,start:r.start,end:r.end,direction:'Receive',location:reference(r.deliveryPoint),locationLabel:r.deliveryPoint,quantitySpecification:reference(r.quantitySpecification.code),unit:'Synthetic unit',contractual:text(r.contractual),selected:text(r.selected),selectedTrack:r.kind,sourceStatus:r.kind==='contractual'?'contractual':r.heads.find(h=>h.id===r.revisionId).status,heads:r.heads,attributes:{pipeline:r.pipeline,zone:r.zone},origin:{slot:i,sideId:'side-'+r.obligationId,scheduleId:'schedule-'+r.obligationId,programDigest:'synthetic-digest',root:'quantity',instruction:1,grid:0,cell:0,obligationId:r.obligationId}}))};
}
