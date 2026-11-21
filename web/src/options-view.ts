import {deliveryLabel,publicText} from './market-view.js';
import {action as button,element as el,listedChain,optionProductPicker,readOptionProducts,type ListedSeries,type OptionListings} from './option-directory.js';
import {chainRows,measureReason,type Cell,type ChainSide} from './options-chain.js';
import {positionTable,type OptionPositionsRead} from './options-positions.js';
import {analysisLines,analysisRequest,choiceProblem,legFigures,offeredChoice,sensitivityTiles,strategyChart,strategyReason,templateLegs,
 type Side,type StrategyAnalysis,type StrategyChoice,type StrategyLeg,type StrategySetup} from './options-strategy.js';
import {clearStrategyChart,renderStrategyChart} from './options-strategy-chart.js';
import {decimalText,hereHeading,volModel,type FuturesReadRequest,type SurfaceExpiry,type SurfaceSeries,type VolSurfaceFrames,type VolSurfaceResult} from './vol-surface-view.js';

// Options (Trading): the option chain of a product's expiry at the selected close, from the product's stored volatility
// surface, the booked option positions with those series' figures scaled to the lots held, and a strategy of legs on one
// expiry with its payoff. Every settlement, volatility, sensitivity, sum, payoff and breakeven is the backend's; the page
// chooses what to show and formats it. A product with no surface at this close shows that, never another close's figures.
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
type Job={state:string}|null;
type SurfaceRead={referenceRevision:{catalogId:string;revisionId:string};priceUnit:string;definitionRevisionId:string;snapshotId:string;model:string;state:'fitted'|'notFitted';job:Job;surface:VolSurfaceResult|null};
// One expiry's strategy: the legs a person is assembling, what its setup offered, and its last analysis with whether the
// choice has changed since.
// `busy` is the number of the read it waits on, or 0.
type Strategy={choice:StrategyChoice;setup?:StrategySetup;analysis?:StrategyAnalysis;stale:boolean;busy:number;message:string};

const active=(job:Job)=>!!job&&['queued','running','cancellation_requested'].includes(job.state);
const messageOf=(error:unknown)=>error instanceof Error?error.message:String(error);
const codeOf=(error:unknown)=>(error as {code?:string}).code;
// Trailing zeros of an exact price, as a label shows it.
const trimmed=(value:string)=>value.includes('.')?value.replace(/0+$/,'').replace(/\.$/,''):value;

/**
 * The Options page. `refresh` reads the option products and booked option positions at the context and the chosen product's
 * stored surface; `open` shows a product's chain at an expiry and series, as a link from its surface does; `reset` clears
 * what an earlier context showed. `openSurface` opens the product's volatility surface, where a close is calibrated;
 * `book` opens the option ticket on a series. A strategy's legs are kept per product and expiry until the context changes;
 * what its setup offered and its analysis are dropped whenever the page is read again.
 */
export function setupOptions(o:{api:Api;context:()=>FuturesReadRequest;zone:()=>string;frames:Pick<VolSurfaceFrames,'here'|'sessionName'>;
 openSurface:(product:string,definitionRevisionId:string,series?:string)=>void;book:(series:string,definitionRevisionId:string)=>void;
 openContract:(curve:string,contractId:string,definitionRevisionId:string)=>void;
 bookFuture?:(curve:string,contractId:string,definitionRevisionId:string)=>void}){
 const root=document.getElementById('options-page')!;root.dataset.ownControls='';

 // The close in view, with the page's views beside it.
 const hero=el('header','options-hero'),hereKind=el('span','options-eyebrow'),hereTitle=el('h2','options-here');hereTitle.id='options-here';
 const heroText=el('div');heroText.append(hereKind,hereTitle);
 const views=el('div','options-views');views.setAttribute('role','group');views.setAttribute('aria-label','Views');
 const chainView=button('options-view','Chain','options-view-chain'),positionsView=button('options-view','Positions','options-view-positions');
 const strategyView=button('options-view','Strategy','options-view-strategy');
 views.append(chainView,positionsView,strategyView);
 hero.append(heroText,views);
 const error=el('p','vol-error');error.id='options-error';error.setAttribute('role','alert');error.hidden=true;

 const picker=optionProductPicker(code=>{
  if(!positions||product===code&&!broken)return;
  product=code;expiryAt='';underlyingCode='';selected='';picker.select(code);void readProduct();
 },()=>void open(''));
 const productBar=picker.root;
 const placeholder=el('div','vol-placeholder');placeholder.id='options-placeholder';
 placeholder.append(el('span','vol-placeholder-mark'),el('h3',undefined,'Choose a product to view its option chain'));
 const missing=el('div','vol-missing');missing.id='options-missing';
 const expiryBar=el('nav','options-expiries');expiryBar.id='options-expiries';expiryBar.setAttribute('aria-label','Expiries');
 const facts=el('dl','options-facts');facts.id='options-facts';
 const tableHost=el('div','options-chain-host');
 const readout=el('aside','vol-card options-readout');readout.id='options-readout';readout.setAttribute('aria-live','polite');
 const basis=el('p','options-basis');basis.id='options-basis';
 // The expiry in view, which the chain and the strategy share.
 const expiryHead=el('div','options-expiry-head'),productHeading=el('h3','options-selected-product');
 expiryHead.append(productHeading,expiryBar,facts);
 const chain=el('section','options-chain-view'),chainHeader=el('div','options-chain-heading');
 const allGreeks=el('input');allGreeks.type='checkbox';allGreeks.id='options-all-greeks';
 const greekControl=el('label');greekControl.append(allGreeks,'Show all Greeks');chainHeader.append(el('h3',undefined,'Option chain'),greekControl);
 allGreeks.onchange=()=>renderChain();chain.append(chainHeader,tableHost,readout,basis);
 const positionsHost=el('section','options-positions');positionsHost.id='options-positions';
 const strategyHost=el('section','options-strategy');strategyHost.id='options-strategy';
 const templateBar=el('nav','options-templates');templateBar.id='options-strategy-templates';templateBar.setAttribute('aria-label','Strategy templates');
 const legHost=el('div','options-chain-host options-strategy-leg-host'),controls=el('div','options-strategy-controls');controls.id='options-strategy-controls';
 const strategyMessage=el('p','options-strategy-message');strategyMessage.id='options-strategy-message';strategyMessage.setAttribute('role','status');
 const chartHost=el('div','options-strategy-chart');chartHost.id='options-strategy-chart';
 const analysisCard=el('aside','vol-card options-strategy-analysis');analysisCard.id='options-strategy-analysis';
 const tiles=el('div','options-strategy-tiles');tiles.id='options-strategy-tiles';
 const side=el('div','options-strategy-side');side.append(analysisCard,tiles);
 const results=el('div','options-strategy-results');results.append(chartHost,side);
 const strategyBasis=el('p','options-basis');strategyBasis.id='options-strategy-basis';
 strategyHost.append(templateBar,legHost,controls,strategyMessage,results,strategyBasis);
 root.replaceChildren(hero,error,productBar,placeholder,missing,expiryHead,chain,positionsHost,strategyHost);

 let productNames=new Map<string,string>();
 let definition:string|undefined,publishedDefinition:string|undefined,pinnedDefinition:string|undefined,product='',expiryAt='',underlyingCode='',selected='',
  shown:SurfaceRead|undefined,listings:OptionListings|undefined,positions:OptionPositionsRead|undefined,
  view:'chain'|'positions'|'strategy'='chain',generation=0,strategyRead=0,broken=false,surfaceIssue='';
 let readAbort:AbortController|undefined;
 const instant=(at:string)=>new Intl.DateTimeFormat('en',{month:'short',day:'numeric',year:'numeric',hour:'numeric',minute:'2-digit',timeZone:o.zone()}).format(new Date(at));
 const expiryLabel=(at:string)=>new Intl.DateTimeFormat('en',{month:'short',day:'numeric',year:'numeric',timeZone:o.zone()}).format(new Date(at));
 function nextRead(){readAbort?.abort();readAbort=new AbortController();return {number:++generation,signal:readAbort.signal};}
 function showHere(){
  let here;try{here=o.frames.here();}catch{here=undefined;}
  const heading=hereHeading(here,o.frames.sessionName,o.zone());hereKind.textContent=heading.kind;hereTitle.textContent=heading.title;
 }
 function failed(cause:unknown){
  broken=true;root.dataset.state='error';error.hidden=false;
  error.textContent=publicText(`${messageOf(cause)}${codeOf(cause)&&!messageOf(cause).includes(codeOf(cause)!)?` (${codeOf(cause)})`:''}`);
  chain.hidden=true;missing.hidden=true;positionsHost.hidden=true;expiryHead.hidden=true;strategyHost.hidden=true;
 }
 const cell=(figure:Cell,className='numeric')=>{
  const td=el('td',className,figure.text);if(figure.missing){td.title=measureReason(figure.missing);td.classList.add('options-missing-figure');}return td;
 };
 // One side's cells, call side mirrored so that each side's settlement sits beside the strike.
 function sideCells(side:ChainSide|null,right:'call'|'put'):HTMLTableCellElement[]{
  if(!side)return Array.from({length:7},(_,i)=>{const td=el('td','options-unlisted');td.hidden=!allGreeks.checked&&(right==='call'?[1,2,3]:[3,4,5]).includes(i);return td;});
  const heldCell=el('td','numeric options-held');if(side.heldLots!==null)heldCell.append(el('span','options-held-lots',side.heldLots));
  const cells=[el('td','numeric options-settle',side.settlement===null?'—':trimmed(side.settlement)),cell(side.impliedVolatility),cell(side.delta),cell(side.gamma),cell(side.vega),cell(side.theta),heldCell];
  for(const index of [3,4,5])cells[index].hidden=!allGreeks.checked;
  const pick=button('options-quote',cells[0].textContent??'—');pick.dataset.series=side.code;
  pick.setAttribute('aria-label',`Select ${side.code} ${right}`);pick.setAttribute('aria-pressed',String(side.code===selected));
  cells[0].replaceChildren(pick);
  for(const td of cells){
   td.dataset.series=side.code;td.dataset.right=right;td.classList.toggle('options-itm',side.inTheMoney===true);td.classList.toggle('options-selected',side.code===selected);
   td.onclick=()=>selectSeries(side.code);
  }
  return right==='call'?cells.reverse():cells;
 }
 function selectSeries(code:string){
  selected=code;renderChain();
  tableHost.querySelector<HTMLElement>(`button[data-series="${CSS.escape(code)}"]`)?.focus({preventScroll:true});
  if(readout.getBoundingClientRect().bottom<=tableHost.getBoundingClientRect().top)readout.scrollIntoView({block:'nearest'});
 }
 function renderFacts(expiry:SurfaceExpiry|undefined){
  const u=listings?.underlying;facts.replaceChildren();if(!u)return;
  const fact=(term:string,value:string|HTMLElement,id?:string)=>{
   const pair=el('div'),dd=el('dd');if(id)dd.id=id;dd.append(value);pair.append(el('dt',undefined,term),dd);facts.append(pair);
  };
  const links=el('span','options-underlying-links'),contractId=u.contractId;
  if(!contractId||u.curves.length!==1)links.append(el('strong',undefined,u.underlyingCode));
  if(contractId)for(const curve of u.curves){
   const link=button('options-contract-link',u.curves.length===1?u.underlyingCode:curve);
   if(u.curves.length===1)link.id='options-underlying-open';
   link.title=`View ${u.name} · ${u.underlyingCode} on ${curve}`;
   link.onclick=()=>o.openContract(curve,contractId,definition!);links.append(link);
  }
  fact('Underlying future',links);fact('Contract',u.name);fact('Delivery',deliveryLabel(u.deliveryFrom,u.deliveryTo));
  if(u.settlementMethod)fact('Settlement method',u.settlementMethod==='physical'?'Physical delivery':u.settlementMethod==='cash'?'Cash settlement':u.settlementMethod);
  if(expiry)fact('Settlement',expiry.futures===null?'—':trimmed(expiry.futures),'options-underlying-settlement');
  fact('Expiry',instant(u.expiryAt));
  if(expiry)fact('Days to expiry',expiry.daysToExpiry===null?'—':decimalText(expiry.daysToExpiry,{places:1}));
  if(contractId&&o.bookFuture&&listings?.canBook&&u.openForBooking&&u.curves.length===1){
   const book=button('secondary','Book underlying future');book.onclick=()=>o.bookFuture!(u.curves[0],contractId,definition!);
   fact('Trade',book);book.classList.add('options-future-book');
  }
 }
 function bookButton(series:ListedSeries){
  const book=button('','Book this series','options-book');
  book.disabled=!series.openForBooking||!listings?.canBook;
  if(book.disabled)book.title=listings?.underlying?.contractId===null?'This definition has no tradable contract for the underlying future.':
   !series.openForBooking?'This series is unavailable for booking at this close.':'Booking requires working data and permission to book trades.';
  book.onclick=()=>o.book(series.code,definition!);return book;
 }
 function renderReadout(series:SurfaceSeries|undefined,listed:ListedSeries|undefined){
  readout.dataset.empty=String(!listed);
  readout.replaceChildren(el('h3',undefined,listed?`${trimmed(listed.strike)} ${listed.right} · ${listed.code}`:'Selected series'));
  if(!listed){readout.append(el('p','vol-readout-empty','Choose a call or put to inspect its terms.'));return;}
  if(!series){
   const details=el('p','options-position-note',`${listings!.underlying!.name} · expires ${instant(expiryAt)}${listed.lastTradingAt?' · last trade '+instant(listed.lastTradingAt):''}`);
   readout.append(details,bookButton(listed));return;
  }
  const analytics=series.analytics,list=el('dl');
  const row=(term:string,value:string)=>list.append(el('dt',undefined,term),el('dd',undefined,value));
  const stated=(measure:{value:string|null;missing:string|null}|undefined)=>measure?.value??(`— ${measureReason(measure?.missing??series.series.outcome)}`);
  row('Series',series.series.code);row('Settlement',series.series.settlement===null?'—':trimmed(series.series.settlement));
  row('Value',analytics?trimmed(analytics.value):`— ${measureReason(series.series.outcome)}`);
  row('Delta',stated(analytics?.delta));row('Gamma',stated(analytics?.gamma));row('Vega',stated(analytics?.vega));row('Theta',stated(analytics?.theta));
  if(analytics?.priceBump)row('Futures bump',trimmed(analytics.priceBump));
  const actions=el('div','vol-missing-actions'),book=bookButton(listed),open=button('secondary','Open on Vol surfaces','options-open-surface');
  open.onclick=()=>o.openSurface(product,definition!,series.series.code);
  // A leg takes the settlement shown as its entry price, for the person to change in the strategy.
  const leg=(side:Side)=>{
   const add=button('secondary',side==='buy'?'Buy in strategy':'Sell in strategy','options-strategy-'+side),legs=strategy().choice.legs;
   add.disabled=legs.some(l=>l.series===series.series.code);if(add.disabled)add.title='This series is already a leg of the strategy';
   add.onclick=()=>{changed(s=>s.choice.legs.push({series:series.series.code,right:series.series.right,strike:series.series.strike,side,lots:'1',
    entryPrice:series.series.settlement===null?'':trimmed(series.series.settlement)}));renderChain();};
   return add;
  };
  actions.append(book,open,leg('buy'),leg('sell'));readout.append(list,actions);
 }
 function fittedExpiry(){
  return shown?.surface?.expiries.find(e=>Date.parse(e.expiryAt)===Date.parse(expiryAt)&&e.underlyingCode===underlyingCode);
 }
 function renderExpiries(){
  const expiries=listings?.expiries??[],expiry=fittedExpiry();productHeading.textContent=publicText(productNames.get(product)??product);
  expiryBar.replaceChildren(...expiries.map(entry=>{
   const sameDate=expiries.some(e=>e!==entry&&e.expiryAt===entry.expiryAt);
   const chip=button('options-expiry',expiryLabel(entry.expiryAt)+(sameDate?' · '+entry.underlyingCode:''));
   chip.dataset.expiry=entry.expiryAt;chip.title=instant(entry.expiryAt)+' · '+entry.underlyingCode;
   if(entry.underlyingCode===underlyingCode&&Date.parse(entry.expiryAt)===Date.parse(expiryAt))chip.setAttribute('aria-current','true');
   else chip.onclick=()=>{expiryAt=entry.expiryAt;underlyingCode=entry.underlyingCode;selected='';void readProduct({reuseSurface:true});};
   return chip;
  }));
  renderFacts(expiry);return expiry;
 }
 function renderChain(){
  const surface=shown?.surface,expiry=renderExpiries(),listed=listings?.series.find(s=>s.code===selected);
  greekControl.hidden=!surface||!expiry;
  if(!listings?.underlying){tableHost.replaceChildren(el('p','vol-empty-shelf','No live option expiries for this product at this close.'));renderReadout(undefined,undefined);basis.textContent='';return;}
  if(!surface||!expiry){
   tableHost.replaceChildren(listedChain(listings.series,selected,selectSeries));renderReadout(undefined,listed);basis.textContent='';return;
  }
  const exactHeld=new Map((positions?.held??[]).filter(h=>h.seriesReference.revision.catalogId===shown!.referenceRevision.catalogId&&h.seriesReference.revision.revisionId===shown!.referenceRevision.revisionId).map(h=>[h.series,h.openLots]));
  const rows=chainRows(surface,expiry.expiryAt,exactHeld,underlyingCode),table=el('table','options-chain');table.id='options-chain';
  const head=el('thead'),groups=el('tr','options-chain-groups'),names=el('tr');
  const group=(text:string,span:number)=>{const th=el('th',undefined,text);th.colSpan=span;th.scope='colgroup';return th;};
  const columns=allGreeks.checked?7:4;groups.append(group('Calls',columns),group('',1),group('Puts',columns));
  const measures=['Settle','IV','Delta','Gamma','Vega','Theta','Held'];
  for(const name of [...[...measures].reverse(),'Strike',...measures]){const th=el('th',name==='Strike'?'options-strike':'numeric',name);th.scope='col';th.hidden=!allGreeks.checked&&['Gamma','Vega','Theta'].includes(name);names.append(th);}
  head.append(groups,names);
  const body=el('tbody');
  for(const row of rows){
   const tr=el('tr');tr.dataset.strike=row.strike;tr.classList.toggle('options-atm',row.atTheMoney);
   const strike=el('th','options-strike',trimmed(row.strike));strike.scope='row';if(row.atTheMoney)strike.title='At the money: the listed strike nearest the underlying settlement';
   tr.append(...sideCells(row.call,'call'),strike,...sideCells(row.put,'put'));body.append(tr);
  }
  table.append(head,body);tableHost.replaceChildren(table);
  renderReadout(surface.series.find(s=>s.series.code===selected),listed);
  const stated=surface.sensitivities;
  basis.textContent=stated?`Per unit of quote. Delta per (${shown!.priceUnit}), gamma per (${shown!.priceUnit})², vega per volatility point, theta for one day${stated.dayTo?` to ${instant(stated.dayTo)}`:''}. Held: open lots across your books. Sensitivities are the option model’s own (${stated.policy}); Risk reports keep theirs.`:'';
 }
 // The booked positions by underlying future. A position whose product has no surface here shows its lots and where to calibrate.
 function renderPositions(){
  const at=Date.parse(o.frames.here()?.at??''),table=positionTable(positions!),grid=el('table','options-position-table');grid.id='options-position-table';
  const head=el('tr');
  for(const [name,numeric] of [['Trade',false],['Book',false],['Series',false],['Open lots',true],['Value',true],['Delta',true],['Gamma',true],['Vega',true],['Theta',true]] as const){
   const th=el('th',numeric?'numeric':undefined,name);th.scope='col';head.append(th);}
  const top=el('thead');top.append(head);grid.append(top);
  const figure=(cell:Cell,unit:string)=>{const td=el('td','numeric',cell.text);td.title=cell.missing?measureReason(cell.missing):unit;if(cell.missing)td.classList.add('options-missing-figure');return td;};
  for(const group of table.groups){
   const body=el('tbody');body.dataset.underlying=group.underlyingSeries;
   const title=el('tr','options-position-group'),name=el('th');name.colSpan=9;name.scope='colgroup';
   name.append(el('span','options-position-future',group.underlyingSeries),el('span','options-position-note',`${group.productName} · expires ${instant(group.expiryAt)} · money in ${group.currency}, delta and gamma in ${group.quantityUnit}${group.sensitivities?.dayTo?` · theta for the day to ${instant(group.sensitivities.dayTo)}`:''}`));
   title.append(name);body.append(title);
   const figures=(cells:{value:Cell;delta:Cell;gamma:Cell;vega:Cell;theta:Cell})=>[figure(cells.value,group.currency),figure(cells.delta,group.quantityUnit),
    figure(cells.gamma,`${group.quantityUnit} per (${group.priceUnit})`),figure(cells.vega,`${group.currency} per volatility point`),figure(cells.theta,`${group.currency} for one day`)];
   for(const row of group.rows){
    const tr=el('tr');tr.dataset.series=row.series;tr.dataset.state=row.state;
    const series=el('td');
    if(Date.parse(group.expiryAt)>at){
     const contract=button('options-leg-contract',row.series);contract.title='View this series in its chain';
     contract.onclick=()=>void open(group.product,group.expiryAt,row.series,group.definitionRevisionId);series.append(contract);
    }else series.textContent=publicText(row.series);
    if(row.state==='notCalibrated'){const go=button('secondary options-position-calibrate','Calibrate →');go.onclick=()=>o.openSurface(group.product,group.definitionRevisionId);series.append(' ',go);}
    else if(row.state==='unavailable'&&row.reason)series.append(' ',el('span','options-position-note',measureReason(row.reason)));
    tr.append(el('td',undefined,row.tradeReference),el('td',undefined,row.book),series,el('td','numeric',row.openLots),...figures(row));body.append(tr);
   }
   const subtotal=el('tr','options-position-subtotal'),label=el('th',undefined,`Subtotal · ${group.underlyingSeries}`);label.colSpan=4;label.scope='row';
   subtotal.append(label,...figures(group.subtotal));body.append(subtotal);grid.append(body);
  }
  const foot=el('tfoot');
  for(const total of table.totals){
   const tr=el('tr','options-position-total'),label=el('th',undefined,`Total · ${total.currency}`);label.colSpan=4;label.scope='row';
   const blank=()=>{const td=el('td','numeric','');td.title='Sensitivities to different futures are not added together';return td;};
   tr.append(label,figure(total.value,total.currency),blank(),blank(),blank(),figure(total.theta,`${total.currency} for one day`));foot.append(tr);
  }
  grid.append(foot);
  const host=el('div','options-chain-host');host.append(grid);
  positionsHost.replaceChildren(...(table.groups.length?[host,el('p','options-basis','Each figure is the series’ stored figure times its multiplier and the open lots held. Delta, gamma and vega are added within one underlying future only; value and theta are totalled per currency. A sum with a missing member is not shown.')]
   :[el('p','vol-empty-shelf','No option positions with open lots in the books you can read.')]));
 }
 // Strategy: legs on the expiry in view, kept per product and expiry, with the payoff the backend states for them.
 const strategies=new Map<string,Strategy>();
 function strategy():Strategy{
  const key=JSON.stringify([definition,product,expiryAt,underlyingCode]);let found=strategies.get(key);
  if(!found){found={choice:{legs:[],lower:'',upper:'',points:0,instants:[],volatilityShiftPoints:'0'},stale:false,busy:0,message:''};strategies.set(key,found);}
  return found;
 }
 const waiting=(s:Strategy)=>s.busy!==0&&s.busy===strategyRead;
 // A strategy read that another replaces, or that nobody waits for any more, is cancelled and its answer dropped.
 let strategyAbort:AbortController|undefined;
 function supersede(){strategyRead++;strategyAbort?.abort();strategyAbort=undefined;}
 function begin(s:Strategy){supersede();s.busy=strategyRead;const control=strategyAbort=new AbortController();return {number:strategyRead,signal:control.signal};}
 // A read that has answered has nothing left to cancel.
 function ended(s:Strategy,number:number,signal:AbortSignal){if(s.busy===number)s.busy=0;if(strategyAbort?.signal===signal)strategyAbort=undefined;}
 // A change to the choice leaves the last analysis on show, marked as no longer the choice's, and cancels any answer still awaited.
 function changed(edit:(s:Strategy)=>void){const s=strategy();edit(s);if(s.analysis)s.stale=true;s.message='';if(s.setup){supersede();s.busy=0;}}
 // The strategy's controls are rebuilt when its legs, curves or templates change. Whichever had the keyboard's focus keeps
 // it by its name; one that is gone hands it to the Analyse button, or to the first template when there is nothing to analyse.
 function keepingFocus(draw:()=>void){
  const held=document.activeElement instanceof HTMLElement&&strategyHost.contains(document.activeElement)?document.activeElement.dataset.focus:undefined;
  draw();
  if(held===undefined||strategyHost.contains(document.activeElement))return;
  const again=[...strategyHost.querySelectorAll<HTMLElement>('[data-focus]')].find(node=>node.dataset.focus===held&&!(node as HTMLButtonElement).disabled);
  (again??(analyseButton.disabled?templateBar.querySelector<HTMLElement>('button'):analyseButton))?.focus();
 }
 const said=(cause:unknown)=>`${messageOf(cause)}${codeOf(cause)&&!messageOf(cause).includes(codeOf(cause)!)?` (${codeOf(cause)})`:''}`;

 const field=(label:string,control:HTMLElement)=>{const wrap=el('label','options-strategy-field');wrap.append(el('span',undefined,label),control);return wrap;};
 const input=(id:string,mode:'decimal'|'numeric')=>{const node=el('input');node.id=id;node.type='text';node.inputMode=mode;node.autocomplete='off';return node;};
 const lower=input('options-strategy-lower','decimal'),upper=input('options-strategy-upper','decimal'),shift=input('options-strategy-shift','decimal');
 const points=el('select');points.id='options-strategy-points';
 const curves=el('div','options-strategy-curves');curves.id='options-strategy-curves';
 const addCurve=el('select');addCurve.id='options-strategy-add-curve';addCurve.setAttribute('aria-label','Add a dated curve');
 const curveField=el('div','options-strategy-field');curveField.append(el('span',undefined,'Dated curves'),curves,addCurve);
 const analyseButton=button('','Analyse','options-strategy-analyse'),clearButton=button('secondary','Clear legs','options-strategy-clear');
 const actions=el('div','options-strategy-actions');actions.append(analyseButton,clearButton);
 controls.append(field('Lowest futures price',lower),field('Highest futures price',upper),field('Price points',points),curveField,field('Volatility shift (points)',shift),actions);
 lower.onchange=()=>{changed(s=>s.choice.lower=lower.value.trim());renderStatus();};upper.onchange=()=>{changed(s=>s.choice.upper=upper.value.trim());renderStatus();};
 shift.onchange=()=>{changed(s=>s.choice.volatilityShiftPoints=shift.value.trim());renderStatus();};
 points.onchange=()=>{changed(s=>s.choice.points=Number(points.value));renderStatus();};
 addCurve.dataset.focus='add-curve';analyseButton.dataset.focus='analyse';clearButton.dataset.focus='clear';
 addCurve.onchange=()=>{const at=addCurve.value;if(at){changed(s=>s.choice.instants.push(at));renderStrategy();}};
 analyseButton.onclick=()=>void analyse();clearButton.onclick=()=>{changed(s=>s.choice.legs=[]);renderStrategy();};

 // What the strategy's state is, said beside its controls; nothing is redrawn that a person may be typing in.
 function renderStatus(){
  const s=strategy(),busy=waiting(s);
  strategyHost.dataset.state=busy?'busy':s.message?'refused':s.analysis?s.stale?'stale':'analysed':'empty';
  strategyMessage.textContent=publicText(busy?(s.setup?'Analysing…':'Reading what a strategy on this expiry may be built from…'):
   s.message||(s.analysis&&s.stale?'The legs or the scenario changed. Analyse again to update the payoff.':''));
  analyseButton.disabled=busy||!s.choice.legs.length;clearButton.disabled=!s.choice.legs.length;
 }
 async function readSetup(){
  const s=strategy(),{number,signal}=begin(s);renderStatus();
  try{
   const setup=await o.api<StrategySetup>('options/strategy/setup',{context:o.context(),product,model:volModel,expiryAt,definitionRevisionId:definition},signal);
   if(number!==strategyRead)return;
   if(setup.product!==product||setup.model!==volModel||Date.parse(setup.expiryAt)!==Date.parse(expiryAt))
    throw Error('The strategy setup read does not match the selected product, model and expiry. Refresh and try again.');
   s.setup=setup;if(setup.offered)s.choice={...offeredChoice(setup.offered),legs:s.choice.legs};
  }catch(cause){if(number===strategyRead)s.message=said(cause);}
  finally{ended(s,number,signal);if(number===strategyRead&&onShow(s))renderStrategy();}
 }
 // The strategy is the one drawn: its product's surface is in view and the Strategy view is chosen.
 const onShow=(s:Strategy)=>view==='strategy'&&!broken&&shown?.state==='fitted'&&s===strategy();
 async function analyse(){
  const s=strategy();if(!s.setup?.offered)return;
  const problem=choiceProblem(s.choice,s.setup.limits);
  if(problem){s.message=problem;renderStatus();return;}
  const {number,signal}=begin(s);s.message='';renderStatus();
  try{
   const body=analysisRequest(o.context(),s.setup,s.choice,definition);
   const answer=await o.api<StrategyAnalysis>('options/strategy/analyze',body,signal);
   if(number!==strategyRead)return;
   if(answer.product!==body.product||Date.parse(answer.expiryAt)!==Date.parse(body.expiryAt)||answer.legs.length!==body.legs.length||answer.legs.some((leg,n)=>leg.series!==body.legs[n].series)||
    answer.request.prices.points!==body.prices.points||answer.request.instants.length!==body.instants.length)
    throw Error('The strategy analysis does not match the legs and scenario that were sent. Analyse again.');
   s.analysis=answer;s.stale=false;
  }catch(cause){if(number===strategyRead)s.message=said(cause);}
  // An answer redraws what it states and leaves the legs and controls a person may be typing in as they are.
  finally{ended(s,number,signal);if(number===strategyRead&&onShow(s)){renderFigures(s);renderResults(s);renderStatus();}}
 }
 // Each leg's analysed volatility and delta, beside the leg it was analysed for.
 function renderFigures(s:Strategy){
  const figures=s.analysis?legFigures(s.analysis):new Map<string,{impliedVolatility:Cell;delta:Cell}>();
  for(const td of legHost.querySelectorAll<HTMLTableCellElement>('td.options-leg-figure')){
   const stated=figures.get(td.parentElement!.dataset.series!)?.[td.dataset.figure as 'impliedVolatility'|'delta'];
   td.textContent=stated?.text??'—';td.title=stated?.missing?strategyReason(stated.missing):'';td.classList.toggle('options-missing-figure',!!stated?.missing);
  }
 }
 function renderLegs(s:Strategy){
  const table=el('table','options-strategy-legs'),head=el('tr'),top=el('thead'),body=el('tbody');table.id='options-strategy-legs';
  for(const [name,numeric] of [['Side',false],['Lots',true],['Series',false],['Entry price',true],['IV',true],['Delta',true],['',false]] as const){
   const th=el('th',numeric?'numeric':undefined,name);th.scope='col';head.append(th);}
  top.append(head);
  s.choice.legs.forEach((leg:StrategyLeg,n)=>{
   const tr=el('tr');tr.dataset.series=leg.series;tr.dataset.side=leg.side;
   const flip=button('options-leg-side',leg.side==='buy'?'Buy':'Sell');flip.dataset.side=leg.side;flip.title='Change between buy and sell';flip.dataset.focus=`side:${leg.series}`;
   flip.onclick=()=>{changed(()=>leg.side=leg.side==='buy'?'sell':'buy');renderStrategy();};
   const lots=input('options-leg-lots-'+n,'numeric'),entry=input('options-leg-entry-'+n,'decimal');
   lots.dataset.focus=`lots:${leg.series}`;entry.dataset.focus=`entry:${leg.series}`;
   lots.className='options-leg-lots';lots.value=leg.lots;lots.setAttribute('aria-label',`Lots of ${leg.series}`);lots.onchange=()=>{changed(()=>leg.lots=lots.value.trim());renderStatus();};
   entry.className='options-leg-entry';entry.value=leg.entryPrice;entry.setAttribute('aria-label',`Entry price of ${leg.series}`);entry.onchange=()=>{changed(()=>leg.entryPrice=entry.value.trim());renderStatus();};
   const name=el('td'),contract=button('options-leg-contract',leg.series);contract.title='View this leg in the chain';
   contract.onclick=()=>{view='chain';selected=leg.series;render();tableHost.querySelector<HTMLElement>(`button[data-series="${CSS.escape(leg.series)}"]`)?.focus();};
   name.append(el('span','options-leg-name',`${trimmed(leg.strike)} ${leg.right}`),' ',contract);
   const remove=button('secondary options-leg-remove','Remove');remove.dataset.focus=`remove:${leg.series}`;remove.setAttribute('aria-label',`Remove ${leg.series}`);remove.onclick=()=>{changed(c=>c.choice.legs.splice(n,1));renderStrategy();};
   const figure=(measure:string)=>{const td=el('td','numeric options-leg-figure');td.dataset.figure=measure;return td;};
   const cells=[el('td'),el('td','numeric'),name,el('td','numeric'),figure('impliedVolatility'),figure('delta'),el('td')];
   cells[0].append(flip);cells[1].append(lots);cells[3].append(entry);cells[6].append(remove);tr.append(...cells);body.append(tr);
  });
  if(!s.choice.legs.length){const tr=el('tr'),td=el('td','options-strategy-empty','No legs yet. Choose a template, or add a call or a put from the chain.');td.colSpan=7;tr.append(td);body.append(tr);}
  table.append(top,body);legHost.replaceChildren(table);renderFigures(s);
 }
 function renderResults(s:Strategy){
  const analysis=s.analysis;
  if(!analysis){
   clearStrategyChart(chartHost);chartHost.append(el('p','vol-readout-empty','Choose a template or add legs, then analyse to see the payoff.'));
   analysisCard.replaceChildren(el('h3',undefined,'At expiry'),el('p','vol-readout-empty','No analysis yet.'));tiles.replaceChildren();strategyBasis.textContent='';return;
  }
  const curveLabel=(at:string)=>Date.parse(at)===Date.parse(analysis.valuationAt)?`Valuation · ${instant(at)}`:instant(at);
  renderStrategyChart(chartHost,strategyChart(analysis,curveLabel));
  const list=el('dl');
  for(const line of analysisLines(analysis)){
   const dd=el('dd',line.tone==='none'?undefined:'options-tone-'+line.tone,line.value);if(line.note)dd.title=line.note;list.append(el('dt',undefined,line.term),dd);
  }
  analysisCard.replaceChildren(el('h3',undefined,'At expiry'),list);
  if(analysis.dated.state==='refused'){
   const refused=el('p','options-strategy-refused',`No dated curve: ${analysis.dated.reason?strategyReason(analysis.dated.reason):'the valuation refused it'}.`);
   refused.id='options-strategy-refused';if(analysis.dated.message)refused.title=analysis.dated.message;analysisCard.append(refused);
  }
  tiles.replaceChildren(...sensitivityTiles(analysis).map(tile=>{
   const node=el('div','options-tile');node.dataset.measure=tile.term.toLowerCase();if(tile.missing){node.title=tile.missing;node.classList.add('options-missing-figure');}
   node.append(el('span','options-tile-term',tile.term),el('strong','options-tile-value',tile.value),el('span','options-tile-unit',tile.unit));return node;
  }));
  const clock=s.setup?.clock;
  strategyBasis.textContent=publicText(`At expiry an option in the money is exercised and worth its intrinsic value (${analysis.expiry.rule}); the payoff is stated over futures prices from zero upward. `+
   `Dated curves and sensitivities value the legs with the option model at each futures price, instant and volatility shift${analysis.strategy.dayTo?`; theta is for the day to ${instant(analysis.strategy.dayTo)}`:''}`+
   `${clock?`, with days counted on the ${clock.zone} clock`:''}. Sensitivities are the option model’s own (${analysis.policies.sensitivities.policy}); Risk reports keep theirs. `+
   `This analysis priced ${analysis.work.treePrices} trees. It books and stores nothing.`);
 }
 function renderStrategy(){keepingFocus(drawStrategy);}
 function drawStrategy(){
  const expiry=renderExpiries(),s=strategy(),offer=s.setup?.offered;
  for(const node of [templateBar,legHost,controls,results,strategyBasis])node.hidden=!expiry||!offer;
  if(!expiry){strategyHost.dataset.state='empty';strategyMessage.textContent='Every listed expiry of this product has passed.';return;}
  if(!s.setup){if(!waiting(s)&&!s.message)void readSetup();else renderStatus();return;}
  if(!offer){strategyHost.dataset.state='empty';strategyMessage.textContent='This expiry has no surface at this close, so no strategy is offered.';return;}
  templateBar.replaceChildren(el('span','options-templates-label','Templates'),...(s.setup.templates??[]).map(template=>{
   const chip=button('options-template',template.name);chip.dataset.template=template.id;chip.dataset.focus=`template:${template.id}`;
   // A template the expiry cannot place stays reachable, so that choosing it says why.
   if(template.refusal){
    const why=template.refusal.message;chip.setAttribute('aria-disabled','true');chip.title=why;
    chip.onclick=()=>{s.message=`${template.name} is not offered at this expiry. ${why}`;renderStatus();};
   }
   else chip.onclick=()=>{changed(c=>c.choice.legs=templateLegs(template));renderStrategy();void analyse();};
   return chip;
  }));
  renderLegs(s);
  lower.value=s.choice.lower;upper.value=s.choice.upper;shift.value=s.choice.volatilityShiftPoints;
  points.replaceChildren(...offer.pointCounts.map(count=>new Option(String(count),String(count))));points.value=String(s.choice.points);
  curves.replaceChildren(...s.choice.instants.map(at=>{
   const chip=button('options-strategy-curve',`${Date.parse(at)===Date.parse(offer.instants[0]??'')?'Valuation · ':''}${instant(at)} ✕`);chip.dataset.instant=at;chip.dataset.focus=`curve:${at}`;
   chip.setAttribute('aria-label',`Remove the curve at ${instant(at)}`);chip.onclick=()=>{changed(c=>c.choice.instants=c.choice.instants.filter(i=>i!==at));renderStrategy();};
   return chip;
  }));
  if(!s.choice.instants.length)curves.append(el('span','options-position-note','None: the payoff at expiry only'));
  addCurve.replaceChildren(new Option('Add a date…',''),...offer.instants.filter(at=>!s.choice.instants.includes(at)).map(at=>new Option(instant(at),at)));
  // An offer that could not list every date before the legs' cutoff says so where the dates are chosen.
  if(!offer.instantsComplete){const more=new Option('Later dates are not listed','');more.disabled=true;addCurve.append(more);}
  addCurve.disabled=s.choice.instants.length>=s.setup.limits.curves;addCurve.title=addCurve.disabled?`At most ${s.setup.limits.curves} dated curves`:'';
  renderResults(s);renderStatus();
 }

 function render(){
  for(const [chip,name] of [[chainView,'chain'],[positionsView,'positions'],[strategyView,'strategy']] as const)chip.setAttribute('aria-pressed',String(view===name));
  if(broken)return;
  error.hidden=true;productBar.hidden=view==='positions';positionsHost.hidden=view!=='positions'||!positions;
  const ready=!!listings&&view!=='positions',fitted=!!fittedExpiry();
  expiryHead.hidden=!ready||!listings?.expiries.length;chain.hidden=!ready||view!=='chain';strategyHost.hidden=!ready||!fitted||view!=='strategy';
  missing.hidden=true;placeholder.hidden=!!product||view==='positions';
  if(view==='positions'){root.dataset.state=positions?'positions':'loading';if(positions)renderPositions();return;}
  if(!ready){root.dataset.state=product?'loading':'idle';return;}
  root.dataset.state=fitted&&view==='strategy'?'strategy':shown?.state??'listed';
  if(view==='chain')renderChain();else if(fitted)renderStrategy();else renderExpiries();
  if(fitted||!listings?.expiries.length)return;
  missing.hidden=false;
  const text=el('div','vol-missing-text'),calibrating=active(shown?.job??null);
  text.append(el('h4',undefined,surfaceIssue?'Analytics unavailable':calibrating?'Calibration in progress':'No calibrated analytics at this close'),
   el('p',undefined,surfaceIssue||'Listed contracts are available. A fitted surface adds volatility, sensitivities and strategy analysis.'));
  const go=button('secondary',calibrating?'Follow it on Vol surfaces →':surfaceIssue?'Open Vol surfaces →':'Calibrate on Vol surfaces →','options-calibrate');
  go.onclick=()=>o.openSurface(product,definition!);missing.replaceChildren(text,go);
 }

 async function readSurface(context:FuturesReadRequest,code:string,revision:string,signal:AbortSignal){
  const body={context,product:code,model:volModel,definitionRevisionId:revision};
  const offer=await o.api<import('./vol-surface-view.js').AxisOffer>('vol-surfaces/axes',body,signal);
  if(offer.definitionRevisionId!==revision||offer.snapshotId!==context.snapshotId||offer.model!==volModel)throw Error('The axis offer does not match the selected context.');
  const axis=offer.axes.find(a=>a.id===offer.initialAxis);
  if(!axis)throw Error('The surface declares no initial axis.');
  const surface=await o.api<SurfaceRead>('vol-surfaces/read',{...body,axis:axis.id,points:axis.preset.points.map(p=>p.value)},signal);
  if(surface.definitionRevisionId!==revision||surface.model!==volModel||surface.snapshotId!==context.snapshotId||(surface.state==='fitted')!==!!surface.surface)
   throw Error('The option chain read does not match the selected product, model and context.');
  return surface;
 }
 async function readProduct({reuseSurface=false,restoreSelection=false}={}){
  const {number,signal}=nextRead(),code=product,revision=definition;
  if(!reuseSurface){shown=undefined;surfaceIssue='';}
  listings=undefined;broken=false;supersede();render();
  if(!code||!revision)return;
  try{
   const context=o.context(),scope={context,product:code,definitionRevisionId:revision};
   let selection={expiryAt:expiryAt||null,underlyingCode:underlyingCode||null,series:selected||null};
   const readListings=()=>o.api<OptionListings>('options/listings',{...scope,...selection},signal);
   const [terms,analytics]=await Promise.all([
    readListings().catch(cause=>{
     // A remembered series may have expired at the new close. An explicit link must resolve exactly.
     if(!restoreSelection||!selection.expiryAt||codeOf(cause)!=='option_definition')throw cause;
     signal.throwIfAborted();selection={expiryAt:null,underlyingCode:null,series:null};return readListings();
    }),
    reuseSurface?Promise.resolve({surface:shown,issue:surfaceIssue}):readSurface(context,code,revision,signal)
     .then(surface=>({surface,issue:''}),cause=>({surface:undefined,issue:said(cause)}))
   ]);
   if(number!==generation)return;
   if(terms.snapshotId!==context.snapshotId||terms.definitionRevisionId!==revision||terms.product!==code||
      selection.expiryAt&&Date.parse(terms.underlying?.expiryAt??'')!==Date.parse(selection.expiryAt)||
      selection.underlyingCode&&terms.underlying?.underlyingCode!==selection.underlyingCode||
      selection.series&&!terms.series.some(s=>s.code===selection.series))
    throw Error('The option listing does not match the selected contract and close.');
   listings=terms;shown=analytics.surface;surfaceIssue=analytics.issue;selected=selection.series??'';expiryAt=terms.underlying?.expiryAt??'';underlyingCode=terms.underlying?.underlyingCode??'';render();
  }catch(cause){if(number===generation)failed(cause);}
 }
 async function refresh(exactLink=false){
  const {number,signal}=nextRead();broken=false;listings=undefined;shown=undefined;positions=undefined;picker.loading();showHere();render();root.dataset.state='loading';
  supersede();for(const kept of strategies.values()){kept.setup=undefined;kept.analysis=undefined;kept.stale=false;kept.busy=0;kept.message='';}
  try{
   const context=o.context();
   const [directory,held]=await Promise.all([readOptionProducts(o.api,context.snapshotId,signal,pinnedDefinition),o.api<OptionPositionsRead>('options/positions',{context,model:volModel},signal)]);
   if(number!==generation)return;
   if(held.model!==volModel)throw Error('The option positions read does not match the selected model. Refresh and try again.');
   if(!exactLink&&definition!==directory.definition){expiryAt='';underlyingCode='';selected='';}
   definition=directory.definition;if(!pinnedDefinition)publishedDefinition=definition;
   positions=held;productNames=new Map(directory.products.map(p=>[p.code,p.name]));
   if(!exactLink&&!productNames.has(product))product='';
   picker.set(directory.products,product,!!pinnedDefinition&&pinnedDefinition!==publishedDefinition);
  }catch(cause){if(number===generation){picker.clear(!!pinnedDefinition);productBar.hidden=!pinnedDefinition&&view==='positions';failed(cause);}return;}
  await readProduct({restoreSelection:!exactLink});
 }
 function reset(){
  productNames.clear();nextRead();supersede();strategies.clear();broken=false;product='';expiryAt='';underlyingCode='';selected='';definition=publishedDefinition=pinnedDefinition=undefined;
  shown=undefined;listings=undefined;positions=undefined;picker.clear();render();
 }
 chainView.onclick=()=>{view='chain';render();};positionsView.onclick=()=>{view='positions';render();};
 // Opening the strategy asks again for a setup that could not be read.
 strategyView.onclick=()=>{view='strategy';if(product&&expiryAt&&!strategy().setup)strategy().message='';render();};
 async function open(code:string,expiry?:string,series?:string,revision?:string){
  view='chain';picker.clearFilters();product=code;pinnedDefinition=revision;expiryAt=expiry??'';underlyingCode='';selected=series??'';await refresh(true);
 }
 return {refresh,open,
  leave(){readAbort?.abort();generation++;supersede();},
  reset
 };
}
