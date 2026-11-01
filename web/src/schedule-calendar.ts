import {displayDate} from './market-view.js';
export type CalendarEvent={date:string;label:string;kind:'start'|'end'|'expiry'|'date';detail?:string};
/** Civil month geometry only; no schedule dates or financial rules are generated here. */
export function monthCells(month:string):(string|null)[]{
 const first=new Date(month+'-01T00:00:00Z'),last=new Date(first);last.setUTCMonth(last.getUTCMonth()+1);last.setUTCDate(0);const days=last.getUTCDate();
 const offset=(first.getUTCDay()+6)%7;return Array.from({length:Math.ceil((offset+days)/7)*7},(_,i)=>i<offset||i>=offset+days?null:`${month}-${String(i-offset+1).padStart(2,'0')}`);
}
export function instantDate(instant:string,zone:string){
 const parts=new Intl.DateTimeFormat('en-US',{timeZone:zone,year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(new Date(instant));
 return ['year','month','day'].map(type=>parts.find(p=>p.type===type)!.value).join('-');
}
export function setupScheduleCalendar(host:HTMLElement){
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
 let events:CalendarEvent[]=[],month='',selected='',title='Date explorer',owner='';
 host.className='schedule-calendar card';host.setAttribute('data-own-controls','');
 function render(){
  host.replaceChildren();const heading=el('div');heading.className='schedule-calendar-heading';heading.append(el('span','CALENDAR'),el('h2',title));host.append(heading);
  if(!events.length){const empty=el('div');empty.className='calendar-empty';empty.append(el('span','▦'),el('h3','See the dates together'),el('p','View a saved schedule or preview a date mapping to see its dates here.'));host.append(empty);return;}
  const toolbar=el('div');toolbar.className='calendar-toolbar';
  const prev=el('button','‹'),next=el('button','›'),picker=el('input');picker.type='month';picker.value=month;picker.setAttribute('aria-label','Calendar month');
  const sorted=events.map(e=>e.date).sort(),first=sorted[0].slice(0,7),last=sorted.at(-1)!.slice(0,7);picker.min=first;picker.max=last;
  function move(delta:number){const d=new Date(month+'-01T00:00:00Z');d.setUTCMonth(d.getUTCMonth()+delta);month=d.toISOString().slice(0,7);render();}
  prev.type=next.type='button';prev.setAttribute('aria-label','Previous month');next.setAttribute('aria-label','Next month');prev.disabled=month<=first;next.disabled=month>=last;prev.onclick=()=>move(-1);next.onclick=()=>move(1);
  picker.onchange=()=>{if(picker.value&&picker.checkValidity()){month=picker.value;render();}};toolbar.append(prev,picker,next);host.append(toolbar);
  const grid=el('div');grid.className='calendar-grid';grid.setAttribute('role','group');grid.setAttribute('aria-label','Schedule dates for '+month);
  for(const weekday of ['Mon','Tue','Wed','Thu','Fri','Sat','Sun']){const n=el('span',weekday);n.className='calendar-weekday';grid.append(n);}
  for(const date of monthCells(month)){
   if(!date){const blank=el('span');blank.className='calendar-blank';grid.append(blank);continue;}
   const dayEvents=events.filter(e=>e.date===date),button=el('button');button.type='button';button.className='calendar-day';button.classList.toggle('has-events',dayEvents.length>0);button.setAttribute('aria-pressed',String(selected===date));
   button.setAttribute('aria-label',`${displayDate(date)} · ${dayEvents.length} events`);button.append(el('span',String(Number(date.slice(-2)))));
   const dots=el('span');dots.className='calendar-dots';for(const kind of [...new Set(dayEvents.map(e=>e.kind))]){const dot=el('i');dot.className='calendar-dot '+kind;dots.append(dot);}button.append(dots);button.onclick=()=>{selected=date;render();};grid.append(button);
  }
  host.append(grid);const legend=el('div');legend.className='calendar-legend';for(const [kind,label] of [['start','Delivery start'],['end','End · exclusive'],['expiry','Last trading'],['date','Schedule date']]){const item=el('span'),dot=el('i');dot.className='calendar-dot '+kind;item.append(dot,el('span',label));legend.append(item);}host.append(legend);
  const visible=selected&&selected.startsWith(month)?events.filter(e=>e.date===selected):events.filter(e=>e.date.startsWith(month));
  const agenda=el('div');agenda.className='calendar-agenda';agenda.setAttribute('aria-live','polite');agenda.append(el('h3',selected.startsWith(month)?displayDate(selected):'This month'));
  if(!visible.length)agenda.append(el('p','No scheduled events on these dates.'));
  for(const event of [...visible].sort((a,b)=>a.date.localeCompare(b.date))){const row=el('div');row.className='calendar-event '+event.kind;row.append(el('small',displayDate(event.date)),el('strong',event.label));if(event.detail)row.append(el('span',event.detail));agenda.append(row);}host.append(agenda);
 }
 render();return {show(name:string,values:CalendarEvent[],source='exchange'){owner=source;title=name;events=values;selected='';month=[...events].sort((a,b)=>a.date.localeCompare(b.date))[0]?.date.slice(0,7)??'';render();},clear(source?:string){if(source&&source!==owner)return;title='Date explorer';events=[];month=selected=owner='';render();},reset(){title='Date explorer';events=[];month=selected=owner='';render();}};
}
