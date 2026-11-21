import type {FacilityProfile} from './facility-profile.js';
import {publicText} from './market-view.js';

type Kind='select'|'text'|'decimal'|'whole'|'date'|'time';
type Unit='quantity'|'price'|null;
type Area='profile'|'input'|'contract'|'output'|'wide';
export type EntryGroup<F extends string>={title:string;area?:Area;fields:readonly (readonly [F,string,Kind,Unit?])[]};
export type FacilityActivity={available:()=>boolean;open:(profileId:string)=>void};

function node<K extends keyof HTMLElementTagNameMap>(tag:K,style?:string,text?:string){
 const element=document.createElement(tag);
 if(style)element.className=style;
 if(text!==undefined)element.textContent=publicText(text);
 return element;
}

export function entryFacts(rows:readonly (readonly [string,string])[]){
 const list=node('dl','entry-facts');
 for(const [label,value] of rows){
  const row=node('div');
  row.append(node('dt',undefined,label),node('dd',undefined,value));
  list.append(row);
 }
 return list;
}

export function facilityCurveChoice(code:string,curves:readonly {code:string;name:string}[]){
 const name=curves.find(curve=>curve.code===code)?.name;
 return {value:code,label:name&&name!==code?name+' · '+code:code};
}

export function facilityActivityButton(access:FacilityActivity|undefined,selected:()=>string|undefined){
 const button=node('button','secondary','View trades and volumes');button.type='button';
 const sync=()=>{button.hidden=!selected()||!access?.available();};
 button.onclick=()=>{const id=selected();if(id&&access?.available())access.open(id);};
 sync();return {button,sync};
}

export function operatingPlanValue(amount:string,currency:string){
 const value=node('p','operating-plan-value');
 value.append(node('span',undefined,'Remaining operating value:'),' ',node('strong',undefined,amount+' '+currency));
 return value;
}

// Only presentation is shared. Each ticket retains its own catalogue, draft, authorized booking and recovery.
export function structuredFields<F extends string>(fieldset:HTMLFieldSetElement,prefix:string,groups:readonly EntryGroup<F>[],activity?:FacilityActivity){
 const controls={} as Record<F,HTMLInputElement|HTMLSelectElement>;
 const captions={} as Record<F,{node:HTMLLabelElement;text:string;unit:Unit}>;
 const lines={} as Record<F,HTMLElement>;
 const sections=new Map<string,HTMLElement>();
 const flow=node('div','entry-flow'),profileDetail=node('p','entry-profile-detail');
 let selected:FacilityProfile|undefined;
 const report=facilityActivityButton(activity,()=>selected?.id);
 const points={input:node('div','entry-point'),output:node('div','entry-point')};
 profileDetail.hidden=true;
 for(const group of groups){
  const box=node('section','entry-section'),area=group.area;
  if(area)box.dataset.area=area;
  if(area!=='profile')box.append(node('h3',undefined,group.title));
  const isStage=area==='input'||area==='contract'||area==='output';
  if(isStage){
   if(!flow.parentNode)fieldset.append(flow);
   flow.append(box);
  }else fieldset.append(box);
  if(area==='input'||area==='output')box.append(points[area]);
  for(const [id,label,kind,unit] of group.fields){
   const control=kind==='select'?node('select'):node('input'),line=node('div','option-row'),caption=node('label',undefined,label);
   control.id=prefix+'-'+id;caption.htmlFor=control.id;
   if(control instanceof HTMLInputElement){
    if(kind==='date'||kind==='time')control.type=kind;
    if(kind==='decimal'||kind==='whole')control.inputMode=kind==='whole'?'numeric':'decimal';
   }
   line.append(caption,control);box.append(line);controls[id]=control;lines[id]=line;
   captions[id]={node:caption,text:label,unit:unit??null};
  }
  if(area==='profile')box.append(profileDetail,report.button);
  sections.set(group.title,box);
 }
 function setProfile(profile:FacilityProfile|undefined,locations?:{input:string;output:string},priceUnits?:{input?:string;output?:string}){
  selected=profile;report.sync();
  profileDetail.textContent=profile?publicText(profile.code+' · Facility version '+profile.version):'';
  profileDetail.hidden=!profile;
  for(const end of ['input','output'] as const){
   const host=points[end];host.replaceChildren();host.hidden=!profile||!locations;
   if(!profile||!locations)continue;
   const rows:[string,string][]=[['Pricing point',locations[end]]];
   if(priceUnits){
    const basis=end==='input'?profile.inputAdjustment:profile.outputAdjustment;
    rows.push(['Location adjustment',[basis,priceUnits[end]].filter(Boolean).join(' ')]);
   }
   host.append(entryFacts(rows));
  }
 }
 return {controls,captions,lines,sections,setProfile,syncActivity:report.sync};
}
