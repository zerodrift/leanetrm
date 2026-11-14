import type {ReferenceKey} from './swap-observations.js';

export const calendarKey=(reference:ReferenceKey)=>JSON.stringify([reference.revision.catalogId,reference.revision.revisionId,reference.code]);
export type SideCalendarChoice={value:string;label:string;reference?:ReferenceKey};
export const referenceCalendarChoices=(rows:{reference:ReferenceKey;code:string}[]):SideCalendarChoice[]=>rows.map(row=>({value:calendarKey(row.reference),label:row.code,reference:row.reference}));
export function savedCalendarCode(code:string|null|undefined,choices:SideCalendarChoice[]){
 if(!code)return null;
 const matches=choices.filter(c=>c.reference?.code===code);
 return matches.length===1?matches[0].value:'Unavailable or ambiguous calendar: '+code;
}

// Entry state only: the backend still generates every schedule from its explicit inputs.
export class SideCalendarSelection{
 private selected='';
 conflicts:string[]=[];
 restore(values:(string|null|undefined)[]){const distinct=[...new Set(values.filter((v):v is string=>!!v))];this.conflicts=distinct.length>1?distinct:[];this.selected=distinct.length===1?distinct[0]:'';}
 choose(value:string){this.selected=value;this.conflicts=[];}
 value(){return this.selected||undefined;}
 check(choices:string[]){
  if(this.conflicts.length)throw Error('This side has different saved calendars. Choose one holiday calendar for all its schedules.');
  if(this.selected&&!choices.includes(this.selected))throw Error('The selected side holiday calendar is unavailable in these market inputs. Choose an available calendar explicitly.');
 }
 read(choices:string[]){this.check(choices);if(!this.selected)throw Error('Choose a holiday calendar for this side.');return this.selected;}
}

export function sideCalendar(options:{id:string;choices:()=>SideCalendarChoice[];changed:()=>void}){
 const selection=new SideCalendarSelection(),element=document.createElement('div'),label=document.createElement('label'),control=document.createElement('select'),note=document.createElement('p');
 element.className='side-calendar';control.id=options.id;control.dataset.sideCalendar='';label.htmlFor=control.id;label.textContent='Holiday calendar';note.className='field-help';note.setAttribute('role','status');element.append(label,control,note);
 const choices=()=>options.choices(),values=()=>choices().map(c=>c.value);
 function sync(disabled=false){
  const rows=choices();control.replaceChildren(new Option('Choose…',''),...rows.map(c=>new Option(c.label,c.value)));
  const savedLabel=(value:string)=>{const offered=rows.find(c=>c.value===value);if(offered)return offered.label;try{const [catalog,revision,code]=JSON.parse(value);if(catalog&&revision&&code)return `${code} · reference version ${revision}`;}catch{}return value;};
  const selected=selection.value();if(selected&&!rows.some(c=>c.value===selected)){const retained=new Option('Unavailable · '+savedLabel(selected),selected);retained.disabled=true;control.append(retained);}
  control.value=selected??'';control.disabled=disabled;
  try{selection.check(values());note.textContent='Used by every holiday-aware schedule on this side. All-days and unadjusted rules keep their meaning.';control.setCustomValidity('');}
  catch(error){note.textContent=(error as Error).message+(selection.conflicts.length?' Saved calendars: '+selection.conflicts.map(savedLabel).join('; ')+'.':'');control.setCustomValidity(note.textContent);}
 }
 // Native selects emit input before change; commit before a parent form synchronizes its controls.
 const changed=()=>{if(control.value===(selection.value()??'')&&!selection.conflicts.length)return;selection.choose(control.value);sync();options.changed();};
 control.oninput=control.onchange=changed;
 return{element,sync,restore:(saved:(string|null|undefined)[])=>{selection.restore(saved);sync();},reset:()=>{selection.restore([]);sync();},
  read:()=>selection.read(values()),check:()=>selection.check(values()),
  reference:()=>choices().find(c=>c.value===selection.value())?.reference,
  code:()=>{const selected=selection.read(values());return choices().find(c=>c.value===selected)?.reference?.code??selected;}};
}
export type SideCalendar=ReturnType<typeof sideCalendar>;
