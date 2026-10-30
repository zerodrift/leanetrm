import {type FuturesProductRead,productHeader,productSections,milestoneColumns,monthlyRows} from './futures-product-model.js';
import {type ItemHeading,itemHeading,itemGrid,node} from './reference-detail.js';
// Presentation only: one futures product on a single page, its terms in sections and its monthly contracts in one table.
// The backend's read supplies every value; the page computes no dates, eligibility or units.

function contracts(read:FuturesProductRead,instant:(value:string)=>string){
  const columns=milestoneColumns(read),rows=monthlyRows(read,instant);
  const headers=['Contract','Delivery from','Delivery to (excluded)',...columns.map(c=>c.label),'Trading eligibility'];
  const table=node('table'),head=node('tr'),body=node('tbody');
  head.append(...headers.map(h=>{const th=node('th',h);th.scope='col';return th;}));
  body.append(...rows.map(r=>{
    const tr=node('tr'),label=node('td',r.label);
    // Edit may put an input in place of the contract's label, starting from the read's own value.
    Object.assign(label.dataset,{editTable:'futures_series',editCode:r.code,editField:'series_label',editValue:r.label});
    tr.append(label,...[r.deliveryFrom,r.deliveryTo,...columns.map(c=>r.milestones[c.key]),r.eligibility].map(v=>node('td',v)));return tr;}));
  const thead=node('thead');thead.append(head);table.append(thead,body);
  // The table scrolls within its own frame, never the page.
  const frame=node('div','','product-contracts');frame.tabIndex=0;frame.setAttribute('role','region');frame.setAttribute('aria-label','Monthly contracts');frame.append(table);
  return frame;
}
function section(title:string,...content:HTMLElement[]){const s=node('section','','item-section');s.append(node('h3',title),...content);return s;}

// `facts` follow the product's status and code in its header.
export function renderFuturesProduct(host:HTMLElement,read:FuturesProductRead,o:{list:ItemHeading['list'];facts:ItemHeading['facts'];instant:(value:string)=>string}){
  const header=productHeader(read);
  const [crumb,title,facts]=itemHeading({list:o.list,title:header.name,facts:[['Status',header.state],['Code',header.code],...o.facts]});
  title.dataset.editField='name';
  const parts=[crumb,title,facts,
    ...productSections(read).map(s=>section(s.title,itemGrid(s.fields))),section('Monthly contracts',contracts(read,o.instant))];
  host.replaceChildren(...parts);host.hidden=false;
}
