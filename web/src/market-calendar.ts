import {displayDate,displayInstant,publicText,timeLabels} from './market-view.js';
import {frameCatalogNote,type FrameCatalog} from './frame-catalog.js';
import type {CloseBadge} from './latest-close.js';

export type MarketClose={id:string;at:string;name:string;businessDateUTC:string};
export type MarketDates={context:unknown;curve:string|null;timeZone:string;selectionPolicy:string;dates:{date:string;frames:MarketClose[]}[];unavailable:{date:string;reason:string}[];minDate:string|null;maxDate:string|null;frameCatalog?:FrameCatalog};

// Gregorian layout only. Available business dates and exact closes come from
// the authorized backend; this component never infers a publication calendar.
export function calendarMonth(month:string):(string|null)[]{
 const first=new Date(month+'-01T00:00:00Z');if(!/^\d{4}-\d{2}$/.test(month)||Number.isNaN(first.getTime()))throw Error('Choose a calendar month.');
 const year=first.getUTCFullYear(),m=first.getUTCMonth(),count=new Date(Date.UTC(year,m+1,0)).getUTCDate(),offset=first.getUTCDay();
 return Array.from({length:42},(_,i)=>i>=offset&&i<offset+count?`${month}-${String(i-offset+1).padStart(2,'0')}`:null);
}
export function shiftCalendarMonth(month:string,direction:number){const value=new Date(month+'-01T00:00:00Z');value.setUTCMonth(value.getUTCMonth()+direction);return value.toISOString().slice(0,7);}
export function closesOnDate(catalog:Pick<MarketDates,'dates'>,date:string){return catalog.dates.find(group=>group.date===date)?.frames??[];}
export function uniqueClose(catalog:Pick<MarketDates,'dates'>,date:string){const closes=closesOnDate(catalog,date);return closes.length===1?closes[0].id:undefined;}

/** Explain supplied availability refusals; never infer another close or relax pricing rules. */
export function unavailableClosesMessage(catalog:Pick<MarketDates,'dates'|'unavailable'>):string{
 if(catalog.dates.length)return '';
 const reasons=new Set(catalog.unavailable.map(day=>day.reason)),details:string[]=[];
 if(reasons.has('source_expired')||reasons.has('unsupported_expiry'))details.push('Required contracts have expired without a supported pricing transition.');
 if(reasons.has('missing_observation'))details.push('Required prices are missing from the retained closes.');
 if(reasons.has('market_data_unavailable'))details.push('The retained closes have no usable price points for this selection.');
 return ['No complete close is available for this selection.',...details,'Choose another curve or ask a market-data maintainer to complete its inputs and configuration.'].join(' ');
}

const badgeText:Record<CloseBadge,string>={latest:'Latest close',selected:'Selected close',newer:'Newer close available',choose:'Choose a close'};

export function setupMarketCalendar(host:HTMLElement,options:{zone:()=>string;busy:()=>boolean;enabled:()=>boolean;selected:()=>string;current:()=>{at:string}|undefined;select:(id:string)=>void;open:()=>void;newer:()=>void;workspace?:{enabled:()=>boolean;open:()=>void}}){
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=publicText(text);return n;};
 const open=el('button','Choose a date…');open.type='button';open.id='market-date-open';open.className='market-date-trigger secondary';open.setAttribute('aria-haspopup','dialog');open.setAttribute('aria-controls','market-date-dialog');
 // Says which close the control shows. Only a newer close arriving makes it a button, which selects that close.
 const flag=el('span');flag.id='market-date-badge';flag.className='close-badge';flag.setAttribute('role','status');flag.hidden=true;
 const control=el('div');control.className='market-date-row';control.append(open,flag);
 const status=el('span');status.id='market-date-status';status.className='market-date-status';status.setAttribute('role','status');
 const dialog=el('dialog');dialog.id='market-date-dialog';dialog.className='market-date-dialog';dialog.setAttribute('aria-labelledby','market-date-title');
 const heading=el('div');heading.className='dialog-heading';const title=el('h2','Choose a market close');title.id='market-date-title';const close=el('button','×');close.type='button';close.className='icon-button';close.id='market-date-close';close.setAttribute('aria-label','Close market calendar');heading.append(title,close);
 const scope=el('p');scope.id='market-date-scope';scope.className='field-help';
 const nav=el('div');nav.className='market-calendar-nav';const previous=el('button','‹'),next=el('button','›'),month=el('input');
 previous.type=next.type='button';previous.id='market-date-previous';next.id='market-date-next';previous.setAttribute('aria-label','Previous month');next.setAttribute('aria-label','Next month');month.type='month';month.id='market-date-month';month.setAttribute('aria-label','Calendar month');nav.append(previous,month,next);
 const grid=el('table');grid.id='market-date-grid';grid.className='market-calendar-grid';grid.setAttribute('role','grid');grid.setAttribute('aria-label','Available market dates');
 const legend=el('p','● Available close · unavailable dates are disabled');legend.className='field-help';
 const empty=el('p');empty.id='market-date-empty';empty.setAttribute('role','status');empty.hidden=true;
 const workspace=el('button','Browse all curves and closes');workspace.id='market-date-workspace';workspace.type='button';workspace.className='secondary';workspace.hidden=true;workspace.onclick=()=>{if(options.busy()||pending||!options.workspace?.enabled())return;chose=true;dialog.close();options.workspace.open();};
 const choices=el('section');choices.id='market-date-closes';choices.setAttribute('aria-label','Choose an exact close');
 dialog.append(heading,scope,empty,workspace,nav,grid,legend,choices);host.replaceChildren(control,status,dialog);
 let catalog:MarketDates|undefined,shownMonth='',chosenDate='',pending=false,focusOnOpen=false,message='Choose a market date.',shownBadge:CloseBadge|null=null;
 const allCloses=()=>catalog?.dates.flatMap(group=>group.frames)??[];
 function sync(){
  open.disabled=options.busy()||pending||!options.enabled();close.disabled=options.busy();
  for(const control of dialog.querySelectorAll<HTMLButtonElement|HTMLInputElement>('button,input'))control.disabled=options.busy()||pending||control.dataset.unavailable==='true';
  if(focusOnOpen&&dialog.open&&!options.busy()&&!pending){focusOnOpen=false;(grid.querySelector<HTMLButtonElement>('button[tabindex="0"]:not(:disabled)')??close).focus();}
 }
 function header(){
  const frames=allCloses(),index=frames.findIndex(frame=>frame.id===options.selected()),selected=index>=0?frames[index]:options.current();
  open.textContent=index>=0?timeLabels(frames.map(frame=>frame.at),options.zone())[index]:selected?displayInstant(selected.at,options.zone()):catalog&&!catalog.dates.length?'No available closes':'Choose a date…';
  open.title=`Market close · ${options.zone()}`;status.textContent=pending?'Checking available closes…':message;sync();
 }
 // A close chosen closes the dialog and hands what follows to the page, which may ask a question and focus it before the dialog's close
 // event comes: focus goes back to the trigger only when the dialog closes without a choice.
 let chose=false;
 function select(id:string){if(options.busy()||pending||!allCloses().some(frame=>frame.id===id))return;chose=true;dialog.close();options.select(id);}
 function renderChoices(){
  choices.replaceChildren();if(!catalog||!chosenDate)return;const frames=closesOnDate(catalog,chosenDate);
  if(frames.length<2)return;
  choices.append(el('h3',`${displayDate(chosenDate)} · choose a close`),el('p','This date has multiple saved closes or versions. Select one explicitly.'));
  const names=timeLabels(frames.map(frame=>frame.at),options.zone());
  frames.forEach((frame,i)=>{const button=el('button');button.type='button';button.className='market-close-choice secondary';button.dataset.frameId=frame.id;button.setAttribute('aria-pressed',String(frame.id===options.selected()));
   button.append(el('strong',names[i]),el('span',`${frame.name} · saved close ${i+1}`));button.onclick=()=>select(frame.id);choices.append(button);});
 }
 function chooseDate(date:string){
  if(!catalog||options.busy()||pending||!closesOnDate(catalog,date).length)return;
  chosenDate=date;const single=uniqueClose(catalog,date);if(single){select(single);return;}
  renderChoices();sync();choices.querySelector<HTMLButtonElement>('button')?.focus();
 }
 function render(){
  grid.replaceChildren();choices.replaceChildren();nav.hidden=!catalog||!shownMonth;legend.hidden=!catalog||!shownMonth;if(!catalog||!shownMonth){sync();return;}
  const shown=catalog;month.value=shownMonth;month.min=catalog.minDate?.slice(0,7)??'';month.max=catalog.maxDate?.slice(0,7)??'';
  previous.dataset.unavailable=String(!!month.min&&shownMonth<=month.min);next.dataset.unavailable=String(!!month.max&&shownMonth>=month.max);
  const head=el('thead'),row=el('tr');for(const day of ['Sun','Mon','Tue','Wed','Thu','Fri','Sat']){const cell=el('th',day);cell.scope='col';row.append(cell);}head.append(row);grid.append(head);
  const body=el('tbody'),cells=calendarMonth(shownMonth),selectedDate=catalog.dates.find(group=>group.frames.some(frame=>frame.id===options.selected()))?.date;
  const available=cells.filter((date):date is string=>!!date&&closesOnDate(shown,date).length>0);const focusDate=[selectedDate,chosenDate].find(date=>date&&date.startsWith(shownMonth))??available[0];
  for(let r=0;r<6;r++){const tr=el('tr');for(let c=0;c<7;c++){
   const date=cells[r*7+c],td=el('td');td.setAttribute('role','gridcell');if(date){const frames=closesOnDate(catalog,date),button=el('button',String(Number(date.slice(-2))));button.type='button';button.dataset.date=date;button.dataset.unavailable=String(!frames.length);button.tabIndex=date===focusDate?0:-1;button.setAttribute('aria-disabled',String(!frames.length));button.setAttribute('aria-label',`${displayDate(date)} · ${frames.length?frames.length+' available '+(frames.length===1?'close':'closes'):'No available close'}`);button.className=frames.length?'available':'';button.setAttribute('aria-pressed',String(date===selectedDate));td.setAttribute('aria-selected',String(date===selectedDate));
    if(frames.length){const dot=el('span','●');dot.className='market-date-dot';dot.setAttribute('aria-hidden','true');button.append(dot);}button.onclick=()=>chooseDate(date);button.onkeydown=event=>{
     const deltas:Record<string,number>={ArrowLeft:-1,ArrowRight:1,ArrowUp:-7,ArrowDown:7};let target:number|undefined;
     if(event.key in deltas){const step=deltas[event.key];let index=cells.indexOf(date)+step;while(index>=0&&index<cells.length){if(cells[index]&&closesOnDate(catalog!,cells[index]!).length){target=index;break;}index+=step;}}
     else if(event.key==='Home'||event.key==='End'){const indexes=Array.from({length:7},(_,i)=>r*7+i);if(event.key==='End')indexes.reverse();target=indexes.find(index=>!!cells[index]&&closesOnDate(catalog!,cells[index]!).length);}
     else if(event.key==='PageUp'||event.key==='PageDown'){event.preventDefault();navigate(event.key==='PageUp'?-1:1);grid.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus();return;}else return;
     event.preventDefault();if(target!==undefined){grid.querySelectorAll<HTMLButtonElement>('button').forEach(n=>n.tabIndex=-1);const to=grid.querySelector<HTMLButtonElement>(`button[data-date="${cells[target]}"]`);if(to){to.tabIndex=0;to.focus();}}
    };td.append(button);}tr.append(td);}body.append(tr);}grid.append(body);renderChoices();sync();
 }
 function navigate(direction:number){const value=shiftCalendarMonth(shownMonth,direction);if(catalog?.minDate&&value<catalog.minDate.slice(0,7)||catalog?.maxDate&&value>catalog.maxDate.slice(0,7))return;shownMonth=value;chosenDate='';render();}
 previous.onclick=()=>navigate(-1);next.onclick=()=>navigate(1);month.onchange=()=>{if(!/^\d{4}-\d{2}$/.test(month.value)||catalog?.minDate&&month.value<catalog.minDate.slice(0,7)||catalog?.maxDate&&month.value>catalog.maxDate.slice(0,7)){month.value=shownMonth;return;}shownMonth=month.value;chosenDate='';render();};
 open.onclick=()=>{if(!open.disabled)options.open();};close.onclick=()=>dialog.close();dialog.addEventListener('close',()=>{if(!chose)open.focus();chose=false;});
 function reset(note='Choose a market date.'){empty.hidden=true;workspace.hidden=true;catalog=undefined;shownMonth='';chosenDate='';pending=false;message=note;if(dialog.open)dialog.close();scope.textContent=note;grid.replaceChildren();choices.replaceChildren();header();}
 return{
  sync,
  update(nextCatalog:MarketDates){
   catalog=nextCatalog;pending=false;message=nextCatalog.dates.length?`${nextCatalog.timeZone} · available closes only`:'No complete close is available for this selection.';
   if(nextCatalog.dates.length&&options.selected()&&!allCloses().some(frame=>frame.id===options.selected()))message='The selected close is unavailable for this curve. Choose an available calendar date.';
   const selected=catalog.dates.find(group=>group.frames.some(frame=>frame.id===options.selected()));shownMonth=(selected?.date??catalog.minDate??catalog.dates[0]?.date??'').slice(0,7);chosenDate=selected?.date??'';
   empty.textContent=unavailableClosesMessage(nextCatalog);empty.hidden=nextCatalog.dates.length>0;workspace.hidden=nextCatalog.dates.length>0||nextCatalog.curve===null||!options.workspace?.enabled();
   scope.textContent=`${nextCatalog.timeZone}. ${nextCatalog.dates.length?'Select an available date, then an exact close when several exist. ':''}${frameCatalogNote(nextCatalog.frameCatalog)} Financial history and knowledge controls remain separate.`;header();if(dialog.open)render();
  },
  loading(){pending=true;message='Checking available closes…';header();},
  unavailable(note:string){reset(note);},
  // Opens on a given available date, asking for its close when it has several; otherwise on the selected close's month.
  show(date?:string){
   if(date&&catalog&&closesOnDate(catalog,date).length){shownMonth=date.slice(0,7);chosenDate=date;}
   render();if(!dialog.open)dialog.showModal();focusOnOpen=true;sync();
  },
  badge(next:CloseBadge|null){
   if(next===shownBadge)return;
   shownBadge=next;flag.hidden=next===null;
   if(next===null){flag.replaceChildren();delete flag.dataset.badge;return;}
   flag.dataset.badge=next;
   if(next!=='newer'){flag.textContent=badgeText[next];return;}
   const action=el('button',badgeText.newer);action.type='button';action.className='close-badge-action';action.disabled=options.busy();action.onclick=options.newer;flag.replaceChildren(action);
  },
  invalidate(note='Open the calendar to check available closes.'){reset(note);},
  selectedChanged(){header();if(dialog.open)render();},
  reset
 };
}
