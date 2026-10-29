import type {Preview,Terms} from './ppa-contract.js';
import type {Holding} from './desk-model.js';
import {publicText} from './market-view.js';
import {exactText,inspectionRows,refusalText,type InspectionRow} from './ppa-view.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Row=InspectionRow&{discount?:{factor:string;source:string}|null};
const pageSize=48;
function node<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string){
 const el=document.createElement(tag);if(text!==undefined)el.textContent=publicText(text);return el;
}
// A titled table whose numeric columns align right; an empty host keeps no title.
function table(host:HTMLElement,title:string,headers:string[],rows:string[][],numeric:readonly number[]){
 const heading=node('h4',title),wrap=node('div'),grid=node('table'),head=node('thead'),body=node('tbody'),titles=node('tr');wrap.className='table-wrap';
 const cell=(tag:'th'|'td',text:string,i:number)=>{const el=node(tag,text);if(numeric.includes(i))el.className='numeric';return el;};
 headers.forEach((title,i)=>{const th=cell('th',title,i);th.scope='col';titles.append(th);});head.append(titles);
 for(const values of rows){const tr=node('tr');values.forEach((value,i)=>tr.append(cell('td',value,i)));body.append(tr);}
 grid.append(head,body);wrap.append(grid);host.replaceChildren(heading,wrap);
}

/** A read of one position's schedules: all amounts and sums are returned by the backend. */
export function mountPpaPositionSchedule(host:HTMLElement,api:Api,context:Record<string,unknown>,h:Holding,terms:Terms,knownAt:string,zone:string){
 const root=node('section'),title=node('h3','Side schedules'),note=node('p','Loading side values…'),totals=node('div'),monthly=node('div'),detail=node('div'),pager=node('div');root.className='side-panel-section position-schedules';
 note.className='schedule-note';note.setAttribute('role','status');for(const block of [totals,monthly,detail])block.className='schedule-block';pager.className='schedule-pager';
 const controls=node('div'),side=node('select'),month=node('select'),inspect=node('button','Show intervals'),previous=node('button','Previous intervals'),next=node('button','Next intervals');
 controls.className='schedule-fields';inspect.type=previous.type=next.type='button';
 const label=(text:string,select:HTMLSelectElement)=>{const label=node('label',text);label.append(select);return label;};
 controls.append(label('Side',side),label('Delivery month',month),inspect);controls.hidden=true;pager.hidden=true;
 pager.append(previous,next);root.append(title,note,totals,controls,monthly,detail,pager);host.append(root);
 let alive=true,generation=0,result:Preview|undefined,offset=0,reading=false,more=false;
 const request=(detailMonths:string[])=>({snapshotId:context.snapshotId,definitionRevisionId:terms.definitionRevisionId,referenceCatalogId:terms.referenceCatalogId,referenceRevisionId:terms.referenceRevisionId,
  scope:'trades',selectedIds:[h.tradeId],explicitSubset:true,terms:null,quantity:null,coverageFrom:null,coverageTo:null,knownAt,pendingActuals:h.pendingActuals,detailMonths});
 const binding=()=>result?.bindings.find(b=>b.bindingId===h.bindingId);
 function sync(){inspect.disabled=reading||!side.value||!month.value;previous.disabled=reading||offset===0;next.disabled=reading||!more;side.disabled=month.disabled=reading;}
 function selected(){
  generation++;detail.replaceChildren();pager.hidden=true;offset=0;more=false;
  const selected=binding()?.sides.find(s=>s.sideId===side.value),periods=selected?.measures.periods.filter(p=>p.month===month.value)??[];
  table(monthly,'Selected month',['Delivery month','Quantity','Value date','Amount','PV','Economic class','Price coverage'],periods.map(p=>[p.month,`${exactText(p.quantity)} [${p.quantitySpecification.code}]`,p.valueAt,exactText(p.amount),exactText(p.presentValue),p.economicClass,p.coverage]),[1,3,4]);sync();
 }
 async function page(start:number){
  if(!side.value||!month.value||reading)return;reading=true;sync();const at=++generation;note.textContent='Loading selected month…';
  try{
   const value=await api<Preview>('ppa/inspect',{preview:request([month.value]),bindingId:h.bindingId,sideId:side.value,offset:start,limit:pageSize});
   if(!alive||at!==generation)return;
   if(!value.detail)throw Error(refusalText(value as unknown as {code?:string;message?:string}));
   const d=value.detail;if(d.bindingId!==h.bindingId||d.sideId!==side.value)throw Error('The schedule response belongs to another position.');
   const values=inspectionRows(d.rows,zone);
   table(detail,'Intervals',['Delivery start','Delivery end','Kind','Quantity','Quantity state','Price','Amount','PV','Economic class','Coverage','Value date','Discount factor'],values.map((r,i)=>[...r,(d.rows[i] as Row).discount?.factor??'Unavailable']),[3,5,6,7,11]);
   offset=d.offset;more=d.hasMore;pager.hidden=false;
   note.textContent=`${d.total} rows in the selected side and month; showing ${d.total===0?0:offset+1}–${offset+d.rows.length}. Amounts include the membership coefficient and position quantity. Full side values are shown above.`;
  }catch(error){if(alive&&at===generation){detail.replaceChildren();note.textContent=publicText(error instanceof Error?error.message:'The schedule could not be read.');}}
  finally{if(alive){reading=false;sync();}}
 }
 side.onchange=month.onchange=selected;inspect.onclick=()=>void page(0);previous.onclick=()=>void page(Math.max(0,offset-pageSize));next.onclick=()=>void page(offset+pageSize);
 void(async()=>{
  try{
   const value=await api<Preview>('ppa/preview',request([]));if(!alive)return;
   if(!Array.isArray(value.bindings))throw Error(refusalText(value as unknown as {code?:string;message?:string}));result=value;
   const held=binding();if(!held||held.instrumentId!==h.contractId)throw Error('The PPA position changed. Refresh the Blotter.');
   table(totals,`Side totals · ${value.currency.code.toUpperCase()}`,['Side','Coefficient','Value excluding overdue cash','Complete PV'],held.sides.map(s=>[`${s.role.replaceAll('_',' ')} ${s.ordinal+1}`,s.coefficient,exactText(s.measures.valueExcludingOverdue),exactText(s.measures.completePv)]),[1,2,3]);
   side.replaceChildren(new Option('Choose a side…',''),...held.sides.map(s=>new Option(`${s.role.replaceAll('_',' ')} ${s.ordinal+1}`,s.sideId)));
   const months=[...new Set(held.sides.flatMap(s=>s.measures.periods.map(p=>p.month)))].sort();
   month.replaceChildren(new Option('Choose a month…',''),...months.map(m=>new Option(m.slice(0,7),m)));controls.hidden=false;
   note.textContent=`Side values in ${value.currency.code.toUpperCase()}, under this Blotter's close and knowledge cutoff. Choose one side and month for bounded interval detail.`;sync();
  }catch(error){if(alive)note.textContent=publicText(error instanceof Error?error.message:'The schedule could not be read.');}
 })();
 return()=>{alive=false;generation++;root.remove();};
}
