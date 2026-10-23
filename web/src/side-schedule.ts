import type {IntervalWidgetData} from './interval-data-widget.js';
type Payment={deliveryFrom:string;deliveryTo:string;paymentDate:string;cashAmount:string;discountFactor:string;presentValue:string};

// Presentation of evaluated payment rows; no aggregation or financial arithmetic.
export function sideScheduleData(payments:readonly Payment[],scope:string,unit:string,
 format:(value:string,kind:'money'|'factor')=>string):IntervalWidgetData & {displayRows:string[][]}{
 const rows=payments.map(p=>[p.deliveryFrom,p.deliveryTo,p.paymentDate,p.cashAmount,p.discountFactor,p.presentValue]);
 return {scope,title:'Side payment schedule',caption:`${payments.length} payments · values per normalized unit`,
  headers:['Delivery start','Delivery end · exclusive','Payment date',`Cash · ${unit}`,'DF',`PV · ${unit}`],rows,numeric:[3,4,5],
  displayRows:payments.map(p=>[p.deliveryFrom,p.deliveryTo,p.paymentDate,format(p.cashAmount,'money'),format(p.discountFactor,'factor'),format(p.presentValue,'money')]),
  chart:{title:'Scheduled cash and present value',axis:'category',categoryTitle:'Payment / delivery period',unit,kind:'bar',
   series:([{name:'Cash',field:'cashAmount'},{name:'Present value',field:'presentValue'}] as const).map(s=>({name:s.name,
    points:payments.map((p,n)=>({x:String(n),label:`${p.paymentDate} · ${p.deliveryFrom} – ${p.deliveryTo}`,y:p[s.field]}))}))}};
}
