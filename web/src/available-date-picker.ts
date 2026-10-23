import {displayDate} from './market-view.js';

// Calendar layout is presentation only. Availability is
// supplied by the consuming operation; this component never invents valid dates.
export function calendarDays(month:string,available:readonly string[]){
 const start=new Date(month+'-01T00:00:00Z');
 if(!/^\d{4}-\d{2}$/.test(month)||!Number.isFinite(start.getTime()))throw Error('Choose a valid calendar month.');
 const dates=new Set(available),result:{date:string;day:number;weekday:number;available:boolean}[]=[];
 for(let day=1;day<=31;day++){
  const date=new Date(start);date.setUTCDate(day);if(date.getUTCMonth()!==start.getUTCMonth())break;
  const key=date.toISOString().slice(0,10);result.push({date:key,day,weekday:date.getUTCDay(),available:dates.has(key)});
 }
 return result;
}

export function createAvailableDatePicker(host:HTMLElement,options:{id:string;label:string;onChange:(date:string)=>void;busy?:()=>boolean}){
 const panel=document.createElement('details');panel.className='available-date-picker';
 const trigger=document.createElement('summary');trigger.id=options.id;trigger.setAttribute('aria-label',options.label);panel.append(trigger);
 const calendar=document.createElement('div');calendar.className='available-date-calendar';calendar.setAttribute('aria-label',options.label+' calendar');
 const heading=document.createElement('div');heading.className='available-date-heading';
 const button=(label:string)=>{const b=document.createElement('button');b.type='button';b.textContent=label;return b;};
 const previous=button('‹'),next=button('›'),title=document.createElement('strong');previous.setAttribute('aria-label','Previous month with data');next.setAttribute('aria-label','Next month with data');heading.append(previous,title,next);
 const grid=document.createElement('div');grid.className='available-date-grid';
 const note=document.createElement('p');note.className='field-help';note.textContent='Only dates with available data can be selected.';
 calendar.append(heading,grid,note);panel.append(calendar);host.replaceChildren(panel);
 let dates:string[]=[],selected='',month='',months:string[]=[];
 function render(){
  trigger.textContent=selected?displayDate(selected):dates.length?'Choose a date…':'No dates with data';
  const disabled=!!options.busy?.()||!dates.length;trigger.setAttribute('aria-disabled',String(disabled));if(disabled)panel.open=false;
  const index=months.indexOf(month);previous.disabled=disabled||index<=0;next.disabled=disabled||index>=months.length-1;
  title.textContent=month?new Intl.DateTimeFormat('en',{month:'long',year:'numeric',timeZone:'UTC'}).format(new Date(month+'-01T00:00:00Z')):'No available dates';
  grid.replaceChildren();for(const day of ['Su','Mo','Tu','We','Th','Fr','Sa']){const label=document.createElement('span');label.textContent=day;label.setAttribute('aria-hidden','true');grid.append(label);}
  if(!month)return;
  for(const day of calendarDays(month,dates)){
   const cell=button(String(day.day));cell.dataset.date=day.date;cell.disabled=disabled||!day.available;cell.setAttribute('aria-label',displayDate(day.date)+(day.available?'':' · no data'));cell.setAttribute('aria-pressed',String(day.date===selected));
   if(day.day===1)cell.style.gridColumnStart=String(day.weekday+1);
   cell.onclick=()=>{if(options.busy?.()||!dates.includes(day.date))return;selected=day.date;panel.open=false;render();trigger.focus();options.onChange(day.date);};grid.append(cell);
  }
 }
 trigger.addEventListener('click',event=>{if(options.busy?.()||!dates.length)event.preventDefault();});
 panel.addEventListener('keydown',event=>{if(event.key==='Escape'){panel.open=false;trigger.focus();}});
 previous.onclick=()=>{if(previous.disabled)return;month=months[months.indexOf(month)-1];render();};next.onclick=()=>{if(next.disabled)return;month=months[months.indexOf(month)+1];render();};
 return{set(available:readonly string[],value:string){dates=[...new Set(available)].sort();selected=dates.includes(value)?value:'';months=[...new Set(dates.map(d=>d.slice(0,7)))];month=selected?selected.slice(0,7):months.includes(month)?month:months[0]??'';render();},sync:render,value:()=>selected};
}
