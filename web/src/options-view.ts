import {publicText} from './market-view.js';
import {chainExpiries,chainRows,measureReason,type Cell,type ChainSide} from './options-chain.js';
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
type ProductPage={definitionRevisionId:string;rows:Record<string,string|number|null>[];hasMore:boolean};
// One expiry's strategy: the legs a person is assembling, what its setup offered, and its last analysis with whether the
// choice has changed since.
// `busy` is the number of the read it waits on, or 0.
type Strategy={choice:StrategyChoice;setup?:StrategySetup;analysis?:StrategyAnalysis;stale:boolean;busy:number;message:string};

const active=(job:Job)=>!!job&&['queued','running','cancellation_requested'].includes(job.state);
const messageOf=(error:unknown)=>error instanceof Error?error.message:String(error);
const codeOf=(error:unknown)=>(error as {code?:string}).code;
function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
 const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=publicText(text);return node;
}
const button=(className:string,text:string,id?:string)=>{const b=el('button',className,text);b.type='button';if(id)b.id=id;return b;};
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
 openSurface:(product:string,definitionRevisionId:string,series?:string)=>void;book:(series:string)=>void}){
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

 const productBar=el('nav','options-products');productBar.id='options-products';productBar.setAttribute('aria-label','Option products');
 const placeholder=el('div','vol-placeholder');placeholder.id='options-placeholder';
 placeholder.append(el('span','vol-placeholder-mark'),el('h3',undefined,'Choose an option product'),
  el('p',undefined,'Its chain is read from the volatility surface calibrated at this close.'));
 const missing=el('div','vol-missing');missing.id='options-missing';
 const expiryBar=el('nav','options-expiries');expiryBar.id='options-expiries';expiryBar.setAttribute('aria-label','Expiries');
 const facts=el('dl','options-facts');facts.id='options-facts';
 const tableHost=el('div','options-chain-host');
 const readout=el('aside','vol-card options-readout');readout.id='options-readout';readout.setAttribute('aria-live','polite');
 const basis=el('p','options-basis');basis.id='options-basis';
 // The expiry in view, which the chain and the strategy share.
 const expiryHead=el('div','options-expiry-head');expiryHead.append(expiryBar,facts);
 const chain=el('section','options-chain-view');chain.append(tableHost,readout,basis);
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

 let products:{code:string;name:string}[]=[],moreProducts=false,definition:string|undefined,product='',expiryAt='',selected='',
  shown:SurfaceRead|undefined,positions:OptionPositionsRead|undefined,held=new Map<string,string>(),view:'chain'|'positions'|'strategy'='chain',generation=0,strategyRead=0,
  // A read failed and none has succeeded since: the failure stays said whatever view is chosen.
  broken=false;
 const instant=(at:string)=>new Intl.DateTimeFormat('en',{month:'short',day:'numeric',year:'numeric',hour:'numeric',minute:'2-digit',timeZone:o.zone()}).format(new Date(at));
 const expiryLabel=(e:SurfaceExpiry)=>new Intl.DateTimeFormat('en',{month:'short',day:'numeric',year:'numeric',timeZone:o.zone()}).format(new Date(e.expiryAt));

 function showHere(){
  let here;try{here=o.frames.here();}catch{here=undefined;}
  const heading=hereHeading(here,o.frames.sessionName,o.zone());hereKind.textContent=heading.kind;hereTitle.textContent=heading.title;
 }
 function failed(cause:unknown){
  broken=true;root.dataset.state='error';error.hidden=false;
  error.textContent=publicText(`${messageOf(cause)}${codeOf(cause)&&!messageOf(cause).includes(codeOf(cause)!)?` (${codeOf(cause)})`:''}`);
  chain.hidden=true;missing.hidden=true;positionsHost.hidden=true;expiryHead.hidden=true;strategyHost.hidden=true;
 }
 function renderProducts(){
  productBar.replaceChildren(...products.map(entry=>{
   const chip=button('options-product','');chip.dataset.product=entry.code;chip.setAttribute('aria-pressed',String(entry.code===product));
   chip.append(el('span','options-product-name',entry.name),el('code','vol-card-code',entry.code));
   chip.onclick=()=>{if(product===entry.code)return;product=entry.code;expiryAt='';selected='';renderProducts();void readProduct();};
   return chip;
  }));
  if(!products.length)productBar.append(el('p','vol-empty-shelf','No option products in the reference data at this date.'));
  else if(moreProducts)productBar.append(el('span','options-more','More products are listed on Vol surfaces.'));
 }

 const cell=(figure:Cell,className='numeric')=>{
  const td=el('td',className,figure.text);if(figure.missing){td.title=measureReason(figure.missing);td.classList.add('options-missing-figure');}return td;
 };
 // One side's cells, call side mirrored so that each side's settlement sits beside the strike.
 function sideCells(side:ChainSide|null,right:'call'|'put'):HTMLTableCellElement[]{
  if(!side)return Array.from({length:7},()=>el('td','options-unlisted'));
  const heldCell=el('td','numeric options-held');if(side.heldLots!==null)heldCell.append(el('span','options-held-lots',side.heldLots));
  const cells=[el('td','numeric options-settle',side.settlement===null?'—':trimmed(side.settlement)),cell(side.impliedVolatility),cell(side.delta),cell(side.gamma),cell(side.vega),cell(side.theta),heldCell];
  for(const td of cells){
   td.dataset.series=side.code;td.dataset.right=right;td.classList.toggle('options-itm',side.inTheMoney===true);td.classList.toggle('options-selected',side.code===selected);
   td.onclick=()=>{selected=side.code;renderChain();};
  }
  return right==='call'?cells.reverse():cells;
 }
 function renderFacts(expiry:SurfaceExpiry){
  const fact=(term:string,value:string,id?:string)=>{const pair=el('div'),dd=el('dd',undefined,value);if(id)dd.id=id;pair.append(el('dt',undefined,term),dd);return pair;};
  facts.replaceChildren(fact('Underlying future',expiry.underlyingCode),fact('Settlement',expiry.futures===null?'—':trimmed(expiry.futures),'options-underlying-settlement'),
   fact('Expiry',instant(expiry.expiryAt)),fact('Days to expiry',expiry.daysToExpiry===null?'—':decimalText(expiry.daysToExpiry,{places:1})));
 }
 function renderReadout(series:SurfaceSeries|undefined){
  readout.replaceChildren(el('h3',undefined,'Selected series'));
  if(!series){readout.append(el('p','vol-readout-empty','Choose a call or a put in the chain to see its figures.'));return;}
  const analytics=series.analytics,list=el('dl');
  const row=(term:string,value:string)=>list.append(el('dt',undefined,term),el('dd',undefined,value));
  const stated=(measure:{value:string|null;missing:string|null}|undefined)=>measure?.value??(`— ${measureReason(measure?.missing??series.series.outcome)}`);
  row('Series',series.series.code);row('Settlement',series.series.settlement===null?'—':trimmed(series.series.settlement));
  row('Value',analytics?trimmed(analytics.value):`— ${measureReason(series.series.outcome)}`);
  row('Delta',stated(analytics?.delta));row('Gamma',stated(analytics?.gamma));row('Vega',stated(analytics?.vega));row('Theta',stated(analytics?.theta));
  if(analytics?.priceBump)row('Futures bump',trimmed(analytics.priceBump));
  const actions=el('div','vol-missing-actions'),book=button('','Book this series','options-book'),open=button('secondary','Open on Vol surfaces','options-open-surface');
  book.onclick=()=>o.book(series.series.code);open.onclick=()=>o.openSurface(product,definition!,series.series.code);
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
 // The expiry chips and the chosen expiry's facts; the expiry, or none when every one has passed.
 function renderExpiries():SurfaceExpiry|undefined{
  const expiries=chainExpiries(shown!.surface!);
  if(!expiries.some(e=>e.expiryAt===expiryAt))expiryAt=expiries[0]?.expiryAt??'';
  expiryBar.replaceChildren(...expiries.map(expiry=>{
   const chip=button('options-expiry',expiryLabel(expiry));chip.dataset.expiry=expiry.expiryAt;
   if(expiry.expiryAt===expiryAt)chip.setAttribute('aria-current','true');else chip.onclick=()=>{expiryAt=expiry.expiryAt;selected='';render();};
   return chip;
  }));
  const expiry=expiries.find(e=>e.expiryAt===expiryAt);
  if(expiry)renderFacts(expiry);else facts.replaceChildren();
  return expiry;
 }
 function renderChain(){
  const surface=shown!.surface!,expiry=renderExpiries();
  if(!expiry){tableHost.replaceChildren(el('p','vol-empty-shelf','Every listed expiry of this product has passed.'));renderReadout(undefined);basis.textContent='';return;}
  const exactHeld=new Map((positions?.held??[]).filter(h=>h.seriesReference.revision.catalogId===shown!.referenceRevision.catalogId&&h.seriesReference.revision.revisionId===shown!.referenceRevision.revisionId).map(h=>[h.series,h.openLots]));
  const rows=chainRows(surface,expiryAt,exactHeld),table=el('table','options-chain');table.id='options-chain';
  const head=el('thead'),groups=el('tr','options-chain-groups'),names=el('tr');
  const group=(text:string,span:number)=>{const th=el('th',undefined,text);th.colSpan=span;th.scope='colgroup';return th;};
  groups.append(group('Calls',7),group('',1),group('Puts',7));
  const measures=['Settle','IV','Delta','Gamma','Vega','Theta','Held'];
  for(const name of [...[...measures].reverse(),'Strike',...measures]){const th=el('th',name==='Strike'?'options-strike':'numeric',name);th.scope='col';names.append(th);}
  head.append(groups,names);
  const body=el('tbody');
  for(const row of rows){
   const tr=el('tr');tr.dataset.strike=row.strike;tr.classList.toggle('options-atm',row.atTheMoney);
   const strike=el('th','options-strike',trimmed(row.strike));strike.scope='row';if(row.atTheMoney)strike.title='At the money: the listed strike nearest the underlying settlement';
   tr.append(...sideCells(row.call,'call'),strike,...sideCells(row.put,'put'));body.append(tr);
  }
  table.append(head,body);tableHost.replaceChildren(table);
  renderReadout(surface.series.find(s=>s.series.code===selected));
  const stated=surface.sensitivities;
  basis.textContent=stated?`Per unit of quote. Delta per (${shown!.priceUnit}), gamma per (${shown!.priceUnit})², vega per volatility point, theta for one day${stated.dayTo?` to ${instant(stated.dayTo)}`:''}. Held: open lots across your books. Sensitivities are the option model’s own (${stated.policy}); Risk reports keep theirs.`:'';
 }
 // The booked positions by underlying future. A position whose product has no surface here shows its lots and where to calibrate.
 function renderPositions(){
  const table=positionTable(positions!),grid=el('table','options-position-table');grid.id='options-position-table';
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
    const series=el('td');series.append(el('code','vol-card-code',row.series));
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
  const key=product+'|'+expiryAt;let found=strategies.get(key);
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
   const name=el('td');name.append(el('span','options-leg-name',`${trimmed(leg.strike)} ${leg.right}`),' ',el('code','vol-card-code',leg.series));
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
  const read=shown;
  for(const [chip,name] of [[chainView,'chain'],[positionsView,'positions'],[strategyView,'strategy']] as const)chip.setAttribute('aria-pressed',String(view===name));
  if(broken)return;
  error.hidden=true;productBar.hidden=view==='positions';positionsHost.hidden=view!=='positions'||!positions;
  const fitted=view!=='positions'&&!!product&&read?.state==='fitted';
  expiryHead.hidden=!fitted;chain.hidden=!fitted||view!=='chain';strategyHost.hidden=!fitted||view!=='strategy';
  if(view==='positions'){
   placeholder.hidden=true;missing.hidden=true;root.dataset.state=positions?'positions':'loading';
   if(positions)renderPositions();return;
  }
  placeholder.hidden=!!product;
  if(!product||!read){missing.hidden=true;root.dataset.state=product?'loading':'idle';return;}
  root.dataset.state=read.state==='fitted'&&view==='strategy'?'strategy':read.state;missing.hidden=read.state==='fitted';
  if(read.state==='fitted'){if(view==='strategy')renderStrategy();else renderChain();return;}
  const name=products.find(p=>p.code===product)?.name??product,text=el('div','vol-missing-text');
  text.append(el('h4',undefined,active(read.job)?`${name} is being calibrated at this close`:`${name} is not calibrated at this close`),
   el('p',undefined,'A chain is read from the surface fitted to one close’s option settlements. It is not carried to another day or session.'));
  const choices=el('div','vol-missing-actions'),go=button('',active(read.job)?'Follow it on Vol surfaces →':'Calibrate on Vol surfaces →','options-calibrate');
  go.onclick=()=>o.openSurface(product,definition!);choices.append(go);
  missing.replaceChildren(el('span','vol-missing-mark'),text,choices);
 }

 // Each read is numbered: an answer to a read that a newer one replaced is dropped.
 async function readProduct(){
  const number=++generation;shown=undefined;broken=false;supersede();render();
  if(!product)return;
  try{
   const context=o.context();
   const offer=await o.api<import('./vol-surface-view.js').AxisOffer>('vol-surfaces/axes',{context,product,model:volModel,definitionRevisionId:definition});
   if(number!==generation)return;
   if(offer.definitionRevisionId!==definition||offer.snapshotId!==context.snapshotId||offer.model!==volModel)throw Error('The axis offer does not match the selected context.');
   const axis=offer.axes.find(a=>a.id===offer.initialAxis);
   if(!axis)throw Error('The surface declares no initial axis.');
   const surface=await o.api<SurfaceRead>('vol-surfaces/read',{context,product,model:volModel,definitionRevisionId:definition,
    axis:axis.id,points:axis.preset.points.map(p=>p.value)});
   if(number!==generation)return;
   if(surface.definitionRevisionId!==definition||surface.model!==volModel||surface.snapshotId!==context.snapshotId||(surface.state==='fitted')!==!!surface.surface)
    throw Error('The option chain read does not match the selected product, model and context. Refresh and try again.');
   shown=surface;render();
  }catch(cause){if(number===generation)failed(cause);}
 }
 async function refresh(){
  const number=++generation;broken=false;showHere();root.dataset.state='loading';error.hidden=true;
  // What a strategy was offered and how it analysed belong to the context that was read; its legs are the person's.
  supersede();for(const kept of strategies.values()){kept.setup=undefined;kept.analysis=undefined;kept.stale=false;kept.busy=0;kept.message='';}
  try{
   const context=o.context(),params=new URLSearchParams({table:'futures_option_products',snapshotId:context.snapshotId,offset:'0',search:''});
   const [page,held]=await Promise.all([o.api<ProductPage>('reference/catalog?'+params),o.api<OptionPositionsRead>('options/positions',{context,model:volModel})]);
   if(number!==generation)return;
   if(!page.definitionRevisionId)throw Error('The option directory changed. Refresh to read its published definition.');
   if(held.model!==volModel)throw Error('The option positions read does not match the selected model. Refresh and try again.');
   definition=page.definitionRevisionId;products=page.rows.map(row=>({code:String(row.code),name:String(row.name)}));moreProducts=page.hasMore;
   positions=held;holdings(held);
   renderProducts();
  }catch(cause){if(number===generation)failed(cause);return;}
  await readProduct();
 }
 function holdings(read:OptionPositionsRead){held=new Map(read.held.map(h=>[h.series,h.openLots]));}
 function reset(){generation++;supersede();strategies.clear();broken=false;products=[];definition=undefined;shown=undefined;positions=undefined;held=new Map();productBar.replaceChildren();render();}
 chainView.onclick=()=>{view='chain';render();};positionsView.onclick=()=>{view='positions';render();};
 // Opening the strategy asks again for a setup that could not be read.
 strategyView.onclick=()=>{view='strategy';if(product&&expiryAt&&!strategy().setup)strategy().message='';render();};
 return {
  refresh,
  /** Shows a product's chain, at an expiry and with a series selected where they are named. */
  async open(code:string,expiry?:string,series?:string){product=code;expiryAt=expiry??'';selected=series??'';await refresh();},
  leave(){generation++;supersede();},
  reset
 };
}
