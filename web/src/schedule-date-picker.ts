import {displayDate,displayInstant,publicText} from './market-view.js';
import type {Context} from './date-editor.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Receipt={expression:string;date:string;source:{kind?:string;name?:string;symbol?:string;version?:number;occurrence?:number;context:Context}};
type Reference={symbol:string;name:string;version:number;selection:string};
type Target={input:HTMLInputElement;label:string;available?:()=>boolean;acceptsDate?:(date:string)=>boolean};

/** One-time authoring helper. The backend resolves all date semantics; applying
 * a reviewed receipt sets a literal date, not a live relationship on a trade. */
export function createScheduleDatePicker(host:HTMLElement,options:{id:string;api:Api;run:(work:()=>Promise<void>)=>Promise<void>;context:()=>Context|undefined;zone:()=>string;enabled:()=>boolean;busy:()=>boolean;targets:Target[];
  /** Hears that the person applied a date, which sets a value in a form for them. */
  edited?:()=>void}){
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=publicText(text);return n;};
 const panel=el('details');panel.className='schedule-date-picker';panel.id=options.id;panel.append(el('summary','Use a schedule date'));
 const fields=el('div');fields.className='schedule-date-fields';
 const target=el('select');target.id=options.id+'-target';target.append(new Option('Choose a side date…',''),...options.targets.map(t=>new Option(t.label,t.input.id)));
 const expression=el('input');expression.type='text';expression.maxLength=100;expression.id=options.id+'-expression';expression.placeholder='Choose a symbol or enter a date expression';expression.autocomplete='off';
 const list=el('datalist');list.id=options.id+'-symbols';expression.setAttribute('list',list.id);
 for(const [label,control] of [['Apply to',target],['Schedule reference',expression]] as const){const field=el('div'),caption=el('label',label);caption.htmlFor=control.id;field.append(caption,control);fields.append(field);}
 const resolve=el('button','Resolve date');resolve.type='button';resolve.className='secondary';resolve.id=options.id+'-resolve';fields.append(resolve);panel.append(fields,list);
 const context=el('p');context.className='field-help';
 const availability=el('p');availability.className='field-help';
 const note=el('p','Resolves in this editor’s business date and knowledge view. Review the result, then apply the exact date. Applying does not create a live schedule link.');note.className='field-help';
 const result=el('div');result.id=options.id+'-result';result.className='schedule-date-result';result.hidden=true;result.setAttribute('role','status');
 const description=el('span'),apply=el('button','Apply date');apply.type='button';apply.className='secondary';apply.id=options.id+'-apply';result.append(description,apply);panel.append(context,availability,note,result);host.replaceChildren(panel);
 let generation=0,receipt:Receipt|undefined,retainedContext='',retainedTarget='';
 const stamp=()=>JSON.stringify(options.context());
 const clear=()=>{generation++;receipt=undefined;retainedContext='';retainedTarget='';description.textContent='';result.hidden=true;};
 function sync(){const c=options.context();context.textContent=c?`Business date ${displayDate(c.effectiveOn)} · known ${c.knownAt?displayInstant(c.knownAt,options.zone()):'unavailable'} · ${options.zone()}`:'';for(const control of [target,expression,resolve,apply])control.disabled=!options.enabled()||options.busy();const prior=target.value;target.replaceChildren(new Option('Choose a side date…',''),...options.targets.filter(t=>t.available?.()!==false).map(t=>new Option(t.label,t.input.id)));target.value=prior;if(prior&&!target.value)clear();if(!options.enabled())clear();}
 panel.addEventListener('toggle',()=>{if(!panel.open){clear();return;}void options.run(async()=>{
  sync();if(!options.enabled())return;const started=generation,source=stamp();
  const library=await options.api<{references:Reference[]}>('date-schedules/catalog',{context:options.context()});if(started!==generation||source!==stamp()||!options.enabled())return;
  list.replaceChildren(...library.references.map(r=>new Option(`${r.name} · v${r.version} · ${r.symbol}[0]`,`${r.symbol}[0]`)));
  availability.textContent=library.references.length?'Choose a published symbol, optionally followed by an occurrence index and a business-day offset.':'No published symbols cover this view. Create and publish a date schedule in Schedules, or enter an exact date.';
 });});
 target.onchange=clear;expression.oninput=clear;
 resolve.onclick=()=>void options.run(async()=>{
  clear();if(!options.enabled())return;
  if(!target.value||!expression.value.trim()||options.targets.find(t=>t.input.id===target.value)?.available?.()===false)throw Error('Choose an available side date and enter a schedule reference first.');
  const c=options.context();if(!c?.contextId||!c.knownAt)throw Error('Reload the editor to obtain its exact date context.');
  const started=generation,source=stamp(),to=target.value,query=expression.value;
  const value=await options.api<Receipt>('date-schedules/resolve',{expression:query,context:c});
  if(started!==generation||source!==stamp()||to!==target.value||query!==expression.value||!options.enabled())return;
  if(options.targets.find(t=>t.input.id===to)?.acceptsDate?.(value.date)===false)throw Error('The resolved date has no available data for the selected side and delivery period.');
  receipt=value;retainedContext=source;retainedTarget=to;
  description.textContent=publicText(`${value.expression} → ${value.date}${value.source.version?` · ${value.source.name} · v${value.source.version} · occurrence ${value.source.occurrence}`:' · literal date'}`);result.hidden=false;
 });
 apply.onclick=()=>{
  if(options.busy()||!options.enabled()||!receipt||retainedContext!==stamp()||retainedTarget!==target.value){clear();return;}
  const chosen=options.targets.find(t=>t.input.id===retainedTarget);if(!chosen||chosen.available?.()===false||chosen.acceptsDate?.(receipt.date)===false){clear();return;}
  chosen.input.value=receipt.date;chosen.input.dispatchEvent(new Event('input',{bubbles:true}));chosen.input.dispatchEvent(new Event('change',{bubbles:true}));options.edited?.();clear();
 };
 return{sync,invalidate:clear,reset(){clear();panel.open=false;target.value='';expression.value='';list.replaceChildren();context.textContent='';availability.textContent='';sync();}};
}
