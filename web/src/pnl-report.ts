import {node,table,type Cells} from './risk-report-elements.js';
import {amountText} from './amount-display.js';
import {compareDecimalStrings} from './analytics-model.js';
import {currencyKey} from './currency-labels.js';
import {bridgeChart} from './pnl-bridge.js';
import {displayDate} from './market-view.js';
import {amount,estimates,isZero,unavailable,type Contribution,type Currency,type Endpoint,type GreekEffect,type Position,type Report,type Total,type Values} from './pnl-model.js';

type Shown={instant:(at:string)=>string;currency:number;chooseCurrency:(index:number)=>void};
const currencyId=(c:Currency)=>currencyKey({code:c.code,...c.revision});
const section=(name:string,heading:string)=>{const part=node('section');part.className='pnl-section '+name;part.append(node('h3',heading));return part;};
const states:Record<string,string>={continuing:'Held',new:'New',closed:'Closed',cash_only:'Cash only'};
const reasons:Record<string,string>={finite_difference_unstable:'Estimate did not converge',closing_exact_price_unavailable:'No closing price'};
// Whose calendar clock one calendar day was counted on.
const clockOwners:Record<string,(name:string|null)=>string>={prior_close_convention:name=>`of the prior date’s close convention, ${name}`,request:()=>'chosen for the prior date, a working snapshot'};
// A factor's name, which wraps in its cell so that the figures beside it stay in view.
const factorName=(label:string)=>{const name=node('span',label);name.className='pnl-factor-name';return name;};
// A Greek effect as a table cell: its amount, that it is unavailable, or its amount with what is missing from it.
function greek(effect:GreekEffect,unavailable='Unavailable',missing=(count:number)=>` · ${estimates(count)} unavailable`):string|HTMLElement{
 if(!effect.amount)return unavailable;
 if(!effect.unavailableEstimates)return amount(effect.amount);
 const cell=node('span');cell.append(amount(effect.amount),missing(effect.unavailableEstimates));return cell;
}

/** Draws one currency of a report. Every amount, price and estimate is the backend's; the page counts, orders, names and lays them out. */
export function drawReport(host:HTMLElement,report:Report,shown:Shown){
 const total=report.totals[shown.currency]??report.totals[0],rows=report.positions.filter(p=>currencyId(p.values.economicPnl.currency)===currencyId(total.currency));
 host.append(hero(report,total,rows.length,shown),metrics(total.values),factorsSection(report,total),bridgeSection(report,total),timeSection(report,total,shown),positionsSection(report,rows));
}

// The period a report compares. A recorded close reads as its business day, since its convention names its time of day; any
// other date reads as its instant. Two closes of one convention name it once; otherwise each close names its own.
function comparedDates(report:Report,shown:Shown){
 const {opening:from,closing:to}=report,day=(end:Endpoint)=>end.close?displayDate(end.close.businessDate):shown.instant(end.at);
 if(from.close&&to.close&&from.close.session===to.close.session)return `${from.close.name} · ${day(from)} → ${day(to)}`;
 const named=(end:Endpoint)=>end.close?`${end.close.name} · ${day(end)}`:day(end);
 return `${named(from)} → ${named(to)}`;
}

function hero(report:Report,total:Total,rows:number,shown:Shown){
 const top=node('div'),result=node('div'),period=node('div');top.className='pnl-hero';period.className='pnl-period';
 result.append(node('p','Economic P&L'),amount(total.values.economicPnl),node('small',total.currency.code.toUpperCase()));
 period.append(node('span',report.scope.books.map(b=>b.name).join(' · ')+' · '+report.scope.tradeSelection),node('strong',comparedDates(report,shown)),
  node('span',rows===1?'1 holding or cash item':`${rows} holdings and cash items`),node('span',`Known as of ${shown.instant(report.opening.context.knownAt)}`));
 top.append(result,period);
 if(report.totals.length>1){
  const chooser=node('select');chooser.setAttribute('aria-label','Report currency');
  for(const [n,t] of report.totals.entries())chooser.add(new Option(t.currency.code.toUpperCase(),String(n),false,t===total));
  chooser.onchange=()=>shown.chooseCurrency(Number(chooser.value));top.append(chooser);
 }
 return top;
}

function metrics(values:Values){
 const cards=node('div');cards.className='pnl-metrics';
 for(const [label,money] of [['PV movement',values.valueMovement],['Period cash',values.cash],['Available Greek effects',values.explained],['Residual',values.residual]] as const){
  const card=node('div');card.append(node('span',label),money?amount(money):unavailable());cards.append(card);
 }
 return cards;
}

// What moved: each price factor these holdings are priced on, its two prices and what its opening delta and gamma explain.
function factorsSection(report:Report,total:Total){
 const part=section('pnl-factors','What moved'),named=new Map(report.factors.map(f=>[f.axis,f]));
 if(!total.factorEffects.length){part.append(node('p','No holding in this currency is priced on a market factor.'));return part;}
 part.append(table(['Factor','Opening','Closing','Change','Delta effect','Gamma effect'],total.factorEffects.map(effect=>{
  const factor=named.get(effect.axis);if(!factor)throw Error('The backend P&L result is incomplete.');
  const price=(value:string)=>amountText(value,'price',factor.priceTick);
  return [factorName(factor.label),price(factor.opening.price),factor.closing?price(factor.closing.price):reasons.closing_exact_price_unavailable,factor.change?price(factor.change.price):'—',
   greek(effect.delta),greek(effect.gamma)];
 })));
 return part;
}

function bridgeSection(report:Report,total:Total){
 const part=section('pnl-bridge','What drove the result');part.append(node('p',report.policies.coverage));
 if(total.greekCoverage==='partial'){
  // Of this currency's own factors, how many have no closing price.
  const closed=new Set(report.factors.filter(f=>f.closing).map(f=>f.axis)),unpriced=total.factorEffects.filter(effect=>!closed.has(effect.axis)).length;
  part.append(node('p',`Partial price-Greek coverage · ${unpriced} ${unpriced===1?'factor':'factors'} without a closing price · `+
   `${estimates(total.values.unavailableEstimates)} unavailable. Their effects remain in Residual.`));
 }
 try{
  const legend=node('p','Solid bars are opening price Greeks and cash. The hollow bar is the residual they do not explain. The last bar is the result.');legend.className='pnl-legend';
  part.append(bridgeChart(total.bridge,total.values.economicPnl),legend);
 }catch{part.append(node('p','The bridge cannot be plotted at this scale. Exact values remain available below.'));}
 const exact=node('details'),inResidual=(count:number)=>` · ${estimates(count)} unavailable, in Residual`;
 exact.append(node('summary','Exact reconciliation'),table(['Contribution','Amount'],[
  ...total.bridge.map((s):Cells=>[s.label,greek(s,`Unavailable · ${estimates(s.unavailableEstimates)}, in Residual`,inResidual)]),['Economic P&L',amount(total.values.economicPnl)]]));
 part.append(exact);return part;
}

function timeSection(report:Report,total:Total,shown:Shown){
 const mode=report.policies.timeModes.find(m=>m.id===report.time.mode);if(!mode)throw Error('The backend P&L result is incomplete.');
 const part=section('pnl-time',mode.label);
 if(report.time.status==='complete'){
  const value=report.time.totals?.find(t=>currencyId(t.currency)===currencyId(total.currency));
  part.append(value?amount(value.value):node('span','No opening exposure in this currency.'));
 }else part.append(node('p','Time effect unavailable for the complete opening scope.'));
 part.append(node('p','Frozen opening markets and knowledge. This diagnostic is separate from the period reconciliation.'));
 const counted=report.time.clock;
 if(counted){
  const owner=clockOwners[counted.source];if(!owner)throw Error('The backend P&L result is incomplete.');
  // A rule's code is a name: where it does not say what the clock is, what the clock is follows it.
  const clock=counted.rule.code===counted.zone?counted.zone:`${counted.rule.code} (${counted.zone})`;
  part.append(node('p',`One calendar day is counted on ${clock}, the calendar clock ${owner(counted.name)}.`));
 }
 for(const p of report.time.partitions)part.append(node('small',p.status==='complete'?`Value at ${shown.instant(p.clock.at)}${p.clock.calendar?' · '+p.clock.calendar.zone:''}`:p.detail??'This part of the scope has no time effect.'));
 return part;
}

type SortKey='openingValue'|'closingValue'|'cash'|'economicPnl'|'explained'|'residual';
const columns:[string,SortKey|null][]=[['Holding',null],['Book',null],['Priced on',null],['Status',null],['Opening PV','openingValue'],['Closing PV','closingValue'],['Cash','cash'],
 ['P&L','economicPnl'],['Price Greeks','explained'],['Residual','residual']];
const pageSize=100;

// Holdings and cash of one currency. A holding whose value did not move and that paid or earned nothing is counted and listed on
// request; a holding's estimates open in a row of their own beneath it. Ledger cash of a trade held at neither close has no reference.
function positionsSection(report:Report,all:Position[]){
 const part=section('pnl-positions','Positions & cash'),wrap=node('div'),grid=node('table'),header=node('thead'),head=node('tr'),body=node('tbody'),toggle=node('button');
 const paging=node('div'),previous=node('button','Previous rows'),next=node('button','Next rows'),range=node('span');
 const books=new Map(report.scope.books.map(b=>[b.id,b.name])),factors=new Map(report.factors.map(f=>[f.axis,f.label]));
 const still=(p:Position)=>[p.values.valueMovement,p.values.cash,p.values.economicPnl].every(isZero),quiet=all.filter(still).length;
 const name=(p:Position)=>p.tradeReference&&p.instrumentReference?`${p.tradeReference} · ${p.instrumentReference}`:'Ledger cash',identity=(p:Position)=>p.position.tradeId+'/'+p.position.bindingId;
 const pricedOn=(axes:string[],between:string)=>factorName(axes.map(axis=>factors.get(axis)??axis).join(between));
 let sort:{key:SortKey;descending:boolean}|undefined,quietShown=false,page=0;const open=new Set<string>();
 // A column that sorts has a button. The headings are built once, so the keyboard stays on a button when the rows are drawn again.
 const sorted=new Map<SortKey,HTMLElement>();
 for(const [label,key] of columns){
  const cell=node('th');head.append(cell);if(!key){cell.textContent=label;continue;}
  const control=node('button',label);control.type='button';control.className='pnl-sort';
  control.onclick=()=>{sort=sort?.key===key?{key,descending:!sort.descending}:{key,descending:true};page=0;draw();};
  cell.append(control);sorted.set(key,cell);
 }
 function detail(p:Position){
  const row=node('tr'),cell=node('td');row.className='pnl-detail';cell.colSpan=columns.length;
  cell.append(table(['Greek','Factor','Contribution'],p.contributions.map((c:Contribution):Cells=>[c.label,pricedOn(c.axes,' × '),c.amount?amount(c.amount):reasons[c.unavailableReason??'']??'Unavailable'])));
  row.append(cell);return row;
 }
 // What a holding's opening Greeks explain: nothing to show when none of its estimates is available, and a mark when some are missing.
 function explained(p:Position){
  if(!p.values.explained)return unavailable();
  const cell=node('span');cell.append(amount(p.values.explained));
  if(p.values.unavailableEstimates){
   const mark=node('span',' · partial');mark.className='pnl-partial';mark.title=`${estimates(p.values.unavailableEstimates)} unavailable, in Residual`;cell.append(mark);
  }
  return cell;
 }
 function draw(){
  const listed=all.filter(p=>quietShown||!still(p));
  if(sort){
   // A holding with no amount in the sorted column comes last either way.
   const {key,descending}=sort,figure=(p:Position)=>p.values[key]?.amount;
   listed.sort((a,b)=>{const x=figure(a),y=figure(b);return x===undefined||y===undefined?Number(x===undefined)-Number(y===undefined):compareDecimalStrings(x,y)*(descending?-1:1);});
  }
  page=Math.max(0,Math.min(page,Math.ceil(listed.length/pageSize)-1));
  for(const [key,cell] of sorted)cell.setAttribute('aria-sort',sort?.key===key?(sort.descending?'descending':'ascending'):'none');
  // The rows are drawn again; the keyboard returns to the holding it was on.
  const focused=body.contains(document.activeElement)?(document.activeElement as HTMLElement).dataset.holding:undefined;
  body.replaceChildren();
  for(const p of listed.slice(page*pageSize,(page+1)*pageSize)){
   const row=node('tr'),id=identity(p),priced=[...new Set(p.contributions.flatMap(c=>c.axes))];
   let holding:string|HTMLElement=name(p);
   if(p.contributions.length){
    const control=node('button',name(p));control.type='button';control.className='pnl-holding';control.dataset.holding=id;control.setAttribute('aria-expanded',String(open.has(id)));
    control.onclick=()=>{if(!open.delete(id))open.add(id);draw();};holding=control;
   }
   for(const value of [holding,books.get(p.position.bookId)??'—',priced.length?pricedOn(priced,', '):'—',states[p.state]??p.state,amount(p.values.openingValue),
    amount(p.values.closingValue),amount(p.values.cash),amount(p.values.economicPnl),explained(p),amount(p.values.residual)]){const cell=node('td');cell.append(value);row.append(cell);}
   body.append(row);if(open.has(id))body.append(detail(p));
  }
  if(focused)[...body.querySelectorAll<HTMLElement>('.pnl-holding')].find(control=>control.dataset.holding===focused)?.focus();
  toggle.hidden=!quiet;toggle.textContent=`${quietShown?'Hide':'Show'} ${quiet} ${quiet===1?'holding':'holdings'} with no P&L`;
  // A paging control that does not apply is hidden rather than disabled: the application re-enables every control when an action ends.
  paging.hidden=listed.length<=pageSize;previous.hidden=page===0;next.hidden=(page+1)*pageSize>=listed.length;
  range.textContent=`Rows ${page*pageSize+1}–${Math.min(listed.length,(page+1)*pageSize)} of ${listed.length}`;
 }
 toggle.type=previous.type=next.type='button';toggle.className='text-button';previous.className=next.className='secondary';
 toggle.onclick=()=>{quietShown=!quietShown;page=0;draw();};previous.onclick=()=>{page--;draw();};next.onclick=()=>{page++;draw();};
 wrap.className='table-wrap';paging.className='actions';paging.append(previous,range,next);header.append(head);grid.append(header,body);wrap.append(grid,paging);part.append(toggle,wrap);draw();
 return part;
}
