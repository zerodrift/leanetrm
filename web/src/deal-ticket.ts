import type {ScheduleView,TicketChoice,TicketDescription,TicketField,TicketLink,TicketSummary,TradeActions} from './ticket-types.js';
import {addSide,removeSide,type FieldValue,type TicketSide,type TicketState} from './ticket-model.js';
import {numberText,resolvedUnit,scheduleGridData,scheduleTabs,sideKind,type ScheduleSide} from './schedule-grid.js';
import {emptyGridLayout,renderDataGrid,sideColour,type GridHandle} from './data-grid.js';
import {reasonOf} from './ticket-preview.js';

// Draws a deal ticket from its backend description, the model's state and the latest preview: the trade strip, one card per side whose
// common band rows line up across the cards, and the schedule panel. Every value, aggregate and refusal is the backend's or the model's;
// the view only lays them out and reports what the person does.
export type TicketStatus={kind:'current'|'updating'|'stale'|'refused';text:string};
export type TicketEvents={edit(side:string|null,field:string,value:string):void;accept(side:string|null,field:string):void;selectSide(code:string):void;tab(id:string):void;
 level(l:string):void;toggle(row:string,open:boolean):void;addSide(kind:string):void;removeSide(code:string):void;preview():void;book():void;
 enter(side:string,day:string,value:string):void;entryValue(field:string,value:string):void;record():void;cancelEntry():void;checkEntry():void;retryEntry():void};
// A day's entry in a side's tab, as the person fills in its record: the fields the record takes, their values and choices, whether a record
// awaits its outcome (then only Check and Retry are offered) or is being sent, and what the last attempt said.
export type TicketEntry={side:string;day:string;fields:TicketField[];values:Record<string,string>;choices:Record<string,TicketChoice[]>;pending:boolean;sending:boolean;text:string};
// fieldErrors and choices are keyed by the field id for a trade field and by "<side code>.<field id>" for a side field. Under the chosen
// level every schedule row is open but those in `closed`, the rows the person closed. quantityUnit is the unit the schedule's preview resolved,
// and instant the page's formatter for the instants the ticket shows. links are the schedule's quantity links, and entry the day entry open, if any.
export type TicketView={state:TicketState;mode:'new'|'booked';actions:TradeActions|null;schedule:ScheduleView|null;quantityUnit:string|null;instant:(at:string)=>string;previous:ScheduleView|null;summaries:TicketSummary[];
 links:TicketLink[];entry:TicketEntry|null;
 status:TicketStatus;fieldErrors:Record<string,string>;choices:Record<string,TicketChoice[]>;selectedSide:string|null;tab:string;level:string;closed:ReadonlySet<string>;on:TicketEvents};

// The common band's rows, in order: TicketBand.Rows in TicketDescriptions.cs.
const band=['direction','start','end','product','quantity','price','payment'];
const levelLabels:Readonly<Record<string,string>>={month:'Month',day:'Day',hour:'Hour'};

export function bandRows(d:TicketDescription,kinds:string[]):string[]{
 const used=new Set(kinds.flatMap(kind=>sideKind(d,kind).fields.flatMap(f=>f.band===null?[]:[f.band])));
 for(const row of used)if(!band.includes(row))throw Error(`Field band '${row}' is not a common band row.`);
 return band.filter(row=>used.has(row));
}

function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
 const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=text;return node;
}
const button=(className:string,text:string,click:()=>void)=>{const b=el('button',className,text);b.type='button';b.onclick=()=>click();return b;};
// What the model refuses, as the reason an action is unavailable; null when it is allowed.
const refusal=(attempt:()=>unknown)=>{try{attempt();return null;}catch(error){return reasonOf(error);}};
const sideName=(d:TicketDescription,side:TicketSide)=>`${sideKind(d,side.kind).pill} · ${side.code}`;
const soonTag=()=>el('span','ticket-soon-tag','Coming soon');
// A coming-soon side kind or tab: greyed, never pressed, with its hover text saying what it will do.
function soonButton(b:HTMLButtonElement,soon:string){
 b.classList.add('ticket-soon');b.disabled=true;b.setAttribute('aria-disabled','true');b.title=soon;b.onclick=null;b.append(soonTag());
 return b;
}

function editor(f:TicketField,value:string,choices:TicketChoice[]|undefined,edit:(value:string)=>void){
 let control:HTMLInputElement|HTMLSelectElement;
 if(f.type==='choice'){
  const select=el('select','ticket-control'),options=[{value:'',label:'Choose…'},...choices??[]];
  // A value the current choices do not list (a booked or looked-up value) stays shown as it is, never silently emptied.
  if(value&&!options.some(c=>c.value===value))options.push({value,label:value});
  for(const c of options){const option=el('option',undefined,c.label);option.value=c.value;select.append(option);}
  control=select;
 }else{const input=el('input','ticket-control');input.type=f.type==='date'?'date':'text';if(f.type==='decimal')input.inputMode='decimal';control=input;}
 control.value=value;control.setAttribute('aria-label',f.label);control.onchange=()=>edit(control.value);
 return control;
}

function fieldRow(o:TicketView,f:TicketField,side:string|null,v:FieldValue|undefined){
 const key=side===null?f.id:`${side}.${f.id}`,value=v?.value??'',entry=o.mode==='new',unit=resolvedUnit(f.unit,o.quantityUnit);
 const row=el('div',`ticket-field ticket-${f.state}`);row.dataset.field=f.id;row.dataset.state=f.state;
 const label=el('span','ticket-label',f.label);label.title=f.meaning;
 const body=el('div','ticket-body');row.append(label,body);
 const chip=()=>{if(entry&&v?.looked){row.classList.add('ticket-looked');body.append(el('span','ticket-chip',v.looked.label));}};
 if(f.state==='soon'){
  // Coming soon: it cannot be entered, holds no value and is never sent; its hover text says what it will do.
  const control=el('input','ticket-control');control.disabled=true;control.setAttribute('aria-disabled','true');control.setAttribute('aria-label',f.label);control.title=f.soon??'';
  row.title=f.soon??'';body.append(control,soonTag());
 }else if(f.state==='editable'&&entry){
  chip();body.append(editor(f,value,o.choices[key],next=>o.on.edit(side,f.id,next)));
  if(unit)body.append(el('span','ticket-unit',unit));
  if(v?.offered)body.append(button('ticket-offer',`Use ${v.offered.value} (${v.offered.label})`,()=>o.on.accept(side,f.id)));
 }else{
  chip();
  const shown=f.type==='choice'?o.choices[key]?.find(c=>c.value===value)?.label??value:value;
  if(shown)body.append(el('span','ticket-value',unit?`${shown} ${unit}`:shown));
  if(f.state==='derived'&&f.rule)body.append(el('span','ticket-rule',f.rule));
 }
 if(o.fieldErrors[key]){const error=el('p','ticket-error',o.fieldErrors[key]);error.setAttribute('role','alert');row.append(error);}
 return row;
}

function summary(s:TicketSummary){
 const box=el('div','ticket-summary');box.append(el('span','ticket-summary-title','Side summary · from preview'));
 for(const [label,value] of [['Billed quantity',s.billedQuantity],['Amount',s.amount],['Against market',s.againstMarket]] as const)
  if(value!==null){const line=el('div','ticket-summary-line');line.append(el('span',undefined,label),el('span','ticket-number',numberText(value)));box.append(line);}
 return box;
}

function card(o:TicketView,side:TicketSide,rows:string[]){
 const d=o.state.description,kind=sideKind(d,side.kind),selected=o.selectedSide===side.code;
 const node=el('article','ticket-card');node.dataset.side=side.code;node.dataset.kind=side.kind;node.classList.toggle('selected',selected);node.style.setProperty('--side-colour',sideColour(kind.colour));
 // A click on a card selects its side and that side's tab; a click on one of its controls is the control's.
 node.onclick=event=>{if(selected&&o.tab===side.code||(event.target as HTMLElement).closest('input,select,button'))return;o.on.selectSide(side.code);};
 const head=el('header','ticket-card-head');head.append(el('strong','ticket-side-name',sideName(d,side)),el('span','ticket-pill',kind.pill));
 if(o.mode==='new'){
  const reason=refusal(()=>removeSide(o.state,side.code)),remove=button('ticket-remove','Remove',()=>o.on.removeSide(side.code));
  remove.disabled=reason!==null;remove.title=reason??`Remove ${sideName(d,side)}`;head.append(remove);
 }
 node.append(head,el('div','ticket-band-title','Common to all sides'));
 for(const row of rows){
  const f=kind.fields.find(x=>x.band===row);
  // A kind without this term keeps its slot, so the row lines up with the other cards.
  const slot=f?fieldRow(o,f,side.code,side.values[f.id]):el('div','ticket-slot-empty');
  slot.dataset.band=row;node.append(slot);
 }
 const own=el('div','ticket-own');
 for(const f of kind.fields.filter(x=>x.band===null))own.append(fieldRow(o,f,side.code,side.values[f.id]));
 const reported=o.summaries.find(s=>s.side===side.code);if(reported)own.append(summary(reported));
 node.append(el('div','ticket-band-title ticket-band-own',`${kind.pill} details`),own);
 return node;
}

function addCard(o:TicketView){
 const node=el('div','ticket-add');node.append(el('span','ticket-add-title','Add side'));
 for(const kind of o.state.description.sides){
  const reason=refusal(()=>addSide(o.state,kind.kind)),offer=button('ticket-add-kind',kind.pill,()=>o.on.addSide(kind.kind));
  offer.disabled=reason!==null;if(reason)offer.title=reason;offer.style.setProperty('--side-colour',sideColour(kind.colour));
  node.append(kind.soon?soonButton(offer,kind.soon):offer);
 }
 return node;
}

function strip(o:TicketView){
 const d=o.state.description,view=o.schedule,node=el('header','ticket-strip');
 const title=el('div','ticket-title');
 title.append(el('strong','ticket-product',d.product),el('span','ticket-state',o.mode==='new'?'Draft':!view?'Booked':view.delivered?'Booked · in delivery':'Booked · forward'));
 const fields=el('div','ticket-trade-fields');for(const f of d.tradeFields)fields.append(fieldRow(o,f,null,o.state.trade[f.id]));
 node.append(title,fields);
 if(view?.delivered){
  const progress=el('div','ticket-progress'),bar=el('progress');bar.max=view.leaves;bar.value=view.deliveredLeaves;
  progress.append(bar,el('span','ticket-progress-text',`${view.deliveredLeaves} of ${view.leaves} delivered`),el('span','ticket-valuation',`Valued at ${o.instant(view.valuationAt)}`));
  node.append(progress);
 }
 const line=el('p','ticket-status',o.status.text);line.dataset.kind=o.status.kind;line.setAttribute('role','status');node.append(line);
 return node;
}

function schedulePanel(o:TicketView,sides:ScheduleSide[]){
 const d=o.state.description,panel=el('section','ticket-schedule'),tabs=el('div','ticket-tabs');tabs.setAttribute('role','tablist');
 for(const t of scheduleTabs(d,sides)){
  const tab=button('ticket-tab',t.label,()=>o.on.tab(t.id));tab.dataset.tab=t.id;tab.setAttribute('role','tab');tab.setAttribute('aria-selected',String(o.tab===t.id));
  tab.style.setProperty('--side-colour',sideColour(t.colour));tabs.append(t.soon?soonButton(tab,t.soon):tab);
 }
 const level=el('select','ticket-level');level.dataset.role='level';level.setAttribute('aria-label','Row level');
 for(const l of o.schedule?.levels??d.levels){
  const label=levelLabels[l];if(!label)throw Error(`The schedule has the unknown level '${l}'.`);
  const option=el('option',undefined,label);option.value=l;level.append(option);
 }
 level.value=o.level;level.onchange=()=>o.on.level(level.value);
 const bar=el('div','ticket-schedule-bar');bar.append(tabs,level);
 const content=el('div','ticket-grid');panel.append(bar,content);
 return {panel,content};
}

function foot(o:TicketView){
 const node=el('footer','ticket-foot');
 if(o.mode==='new'){
  node.append(el('span','ticket-note','Green fields were filled from a lookup; edit one and the value is yours.'),button('ticket-preview','Preview',()=>o.on.preview()),button('ticket-book','Book',()=>o.on.book()));
  return node;
 }
 if(!o.actions)throw Error('A booked ticket needs its trade actions.');
 // The ticket has no amend or close-out mode yet, so these show what the backend says of them and are not offered.
 for(const [label,action,unbuilt] of [['Amend',o.actions.amend,'This ticket does not open amendments yet.'],['Close out',o.actions.closeOut,'This ticket does not open close-outs yet.']] as const){
  const b=el('button','ticket-action',label);b.type='button';b.disabled=true;b.title=action.allowed?unbuilt:action.reason;node.append(b);
 }
 return node;
}

// The record of a day's entry: its fields, then Record or Cancel, or, while a record awaits its outcome, Check and Retry of that record.
function entryForm(o:TicketView,e:TicketEntry,name:string){
 const node=el('section','ticket-record');node.setAttribute('aria-label',`Record ${name} · ${e.day}`);
 node.append(el('strong','ticket-record-title',`Record ${name} · ${e.day}`));
 const fields=el('div','ticket-record-fields');
 for(const f of e.fields){
  const row=el('label','ticket-field ticket-editable');row.dataset.field=f.id;row.append(el('span','ticket-label',f.label));
  const control=editor(f,e.values[f.id]??'',e.choices[f.id],value=>o.on.entryValue(f.id,value));control.disabled=e.pending||e.sending;row.append(control);
  const unit=resolvedUnit(f.unit,o.quantityUnit);if(unit)row.append(el('span','ticket-unit',unit));
  if(f.meaning)row.title=f.meaning;
  fields.append(row);
 }
 node.append(fields);
 if(e.text){const line=el('p',e.pending?'ticket-record-status':'ticket-error',e.text);line.setAttribute('role',e.pending?'status':'alert');node.append(line);}
 const actions:[string,string,()=>void][]=e.pending?[['ticket-record-check','Check record status',o.on.checkEntry],['ticket-record-retry','Retry the original record',o.on.retryEntry]]:
  [['ticket-record-send','Record',o.on.record],['ticket-record-cancel','Cancel',o.on.cancelEntry]];
 for(const [className,text,run] of actions){const b=button(className,text,run);b.disabled=e.sending;node.append(b);}
 return node;
}

// Each linked side's receipt, delivery and fuel over the term, exactly as the backend reports them.
function linksGrid(o:TicketView,content:HTMLElement):GridHandle|undefined{
 if(!o.links.length){content.append(el('p','muted','No side takes its quantity from a link.'));return;}
 const unit=o.quantityUnit,rule=(l:TicketLink)=>o.choices[`${l.target}.link_formula`]?.find(c=>c.value===l.formula)?.label??l.formula;
 const headers=['Source','Linked side','Rule','Fraction',...['Receipt','Delivery','Fuel'].map(label=>unit?`${label} (${unit})`:label)];
 const rows=o.links.map(l=>[l.source,l.target,l.formula,l.fraction,l.receipt,l.delivery,l.fuel]);
 return renderDataGrid(content,'deal-ticket-links',`${o.state.description.product}:links`,headers,rows,[3,4,5,6],()=>false,{keys:['source','target','rule','fraction','receipt','delivery','fuel'],
  displayRows:o.links.map((l,i)=>[l.source,l.target,rule(l),...rows[i].slice(3).map(numberText)]),rowKeys:o.links.map(l=>l.target),layout:emptyGridLayout(),onLayout:()=>{}});
}

export function renderTicket(host:HTMLElement,o:TicketView):{destroy():void}{
 const d=o.state.description,sides=o.state.sides,rows=bandRows(d,[...new Set(sides.map(s=>s.kind))]);
 const named:ScheduleSide[]=sides.map(s=>({kind:s.kind,code:s.code,name:sideName(d,s)}));
 // The ticket keeps its controls' states itself: a page's busy sweep leaves them as drawn.
 const root=el('div','deal-ticket');root.dataset.mode=o.mode;root.dataset.ownControls='';
 const notices=el('ul','ticket-notices');for(const text of o.state.notices){const n=el('li','ticket-notice',text);n.setAttribute('role','status');notices.append(n);}
 // Narrow containers stack the cards; these chips then jump between the sides.
 const chips=el('nav','ticket-chips');chips.setAttribute('aria-label','Sides');
 for(const s of named){
  const chip=button('ticket-side-chip',s.name,()=>o.on.selectSide(s.code));chip.setAttribute('aria-pressed',String(o.selectedSide===s.code));
  chip.style.setProperty('--side-colour',sideColour(sideKind(d,s.kind).colour));chips.append(chip);
 }
 const deck=el('div','ticket-deck');deck.style.setProperty('--band-rows',String(rows.length));deck.style.setProperty('--cards',String(sides.length));
 for(const side of sides)deck.append(card(o,side,rows));
 if(o.mode==='new')deck.append(addCard(o));
 const {panel,content}=schedulePanel(o,named);
 root.append(strip(o),...o.state.notices.length?[notices]:[],chips,deck,panel,foot(o));
 host.replaceChildren(root);
 let grid:GridHandle|undefined;
 const shows=o.tab==='instrument'||sides.some(s=>s.code===o.tab);
 // A side added since the last preview has no values in its schedule yet; the grid draws the sides it holds.
 const previewed=named.filter(s=>o.schedule&&Object.keys(o.schedule.totals).some(key=>key.startsWith(`${s.code}.`)));
 const unpreviewed=named.find(s=>s.code===o.tab&&!previewed.includes(s));
 if(o.tab==='links')grid=linksGrid(o,content);
 else if(!shows)content.append(el('p','muted',`${d.tabs.find(t=>t.id===o.tab)?.label??o.tab} is not drawn in this ticket yet.`));
 else if(!o.schedule)content.append(el('p','muted','No schedule yet. Preview the trade to draw it.'));
 else if(unpreviewed)content.append(el('p','muted',`${unpreviewed.name} is not in the last preview yet.`));
 else{
  if(o.quantityUnit===null)throw Error('A drawn schedule needs the quantity unit its preview resolved.');
  const g=scheduleGridData(o.schedule,d,previewed,o.tab,o.level,{quantityUnit:o.quantityUnit,instant:o.instant},o.previous??undefined);
  // In a side's tab, the day cells of a column the description makes an entry in this mode are inputs, when a booked trade records them;
  // a change opens that day's record.
  const side=sides.find(s=>s.code===o.tab),entered=side?sideKind(d,side.kind).columns.map(c=>c.input.includes(o.mode)&&(o.mode==='new'||o.actions!.record.allowed)):[];
  const cells=(r:number,c:number)=>{
   if(!side||c===0||!entered[c-1]||g.levels[r]!=='day')return null;
   // The entry opens empty: the drawn quantity is grouped and signed, and the record takes the backend's plain unsigned decimal.
   const input=el('input','ticket-cell-input');input.placeholder=g.displayRows[r][c];input.inputMode='decimal';input.setAttribute('aria-label',`${g.headers[c]} · ${g.keys[r]}`);
   input.disabled=!!o.entry?.pending||!!o.entry?.sending;input.onchange=()=>o.on.enter(side.code,g.keys[r],input.value);
   return input;
  };
  grid=renderDataGrid(content,'deal-ticket-schedule',`${d.product}:${o.tab}`,g.headers,g.rows,g.numeric,()=>false,{keys:g.columns,displayRows:g.displayRows,rowKeys:g.keys,
   layout:emptyGridLayout(),onLayout:()=>{},headerGroups:g.headerGroups,totals:g.totals,decorate:(r,c)=>g.decorations[r][c],cells,soon:g.soon,
   // The chosen level shows its rows open under their parents, except the rows the person closed; the rows they open or close are reported.
   tree:{parents:g.parents,open:new Set(g.parents.filter((p):p is string=>p!==null&&!o.closed.has(p))),onToggle:(row,open)=>o.on.toggle(row,open)}});
  if(o.status.kind==='stale')grid.markStale(o.status.text);
  const open=o.entry&&o.entry.side===o.tab?o.entry:null;
  if(open)panel.append(entryForm(o,open,named.find(s=>s.code===open.side)!.name));
 }
 return {destroy(){grid?.destroy();host.replaceChildren();}};
}
