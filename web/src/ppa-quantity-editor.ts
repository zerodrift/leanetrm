import {ppaSideLabel} from './ppa-side-label.js';
import type {Catalog,MemberTemplate,Quantity,Slot} from './ppa-contract.js';
import {friendly} from './market-view.js';
// The server publishes admitted quantity modes and link roles. This editor only
// selects those exact references; it neither calculates nor converts quantities.
export function ppaQuantityEditor(host:HTMLElement,catalog:Catalog,template:MemberTemplate,prefix:string,delivery:()=>{clock:string;cadence:string},links:()=>Slot[],initial?:Quantity){
 const root=document.createElement('fieldset');root.className='ppa-side-quantity';root.innerHTML=`<legend>Side quantity</legend><div class="schedule-fields">
 <label>Quantity source<select data-q="kind"></select></label>
 <label data-mode="volume">Volume sources<select data-q="sources" multiple size="3"></select></label>
 <label data-mode="volume">Quantity stage<select data-q="stage"></select></label>
 <label data-mode="profile">Capacity specification<select data-q="capacity"></select></label>
 <label data-mode="profile">Energy specification<select data-q="energy"></select></label>
 <label data-mode="profile">Hours<select data-q="membership"></select></label>
 <label data-mode="profile">Holiday calendar<select data-q="calendar"></select></label>
 <label data-mode="profile" data-tou>Published TOU version<select data-q="tou"></select></label>
 <label data-mode="accredited">Accredited assets<select data-q="assets" multiple size="3"></select></label>
 <label data-mode="slots">Quantity source sides<select data-q="slot" multiple size="3"></select></label></div>`;
 host.querySelector('.ppa-link')!.after(root);
 const field=(key:string)=>root.querySelector<HTMLSelectElement>(`[data-q="${key}"]`)!;
 root.querySelectorAll<HTMLSelectElement>('select').forEach(n=>n.id=prefix+'-quantity-'+n.dataset.q);
 function offer(key:string,rows:{value:string;label:string}[]){const n=field(key),selected=[...n.selectedOptions].map(o=>o.value);n.replaceChildren(...(n.multiple?[]:[new Option('Choose…','')]),...rows.map(r=>new Option(r.label,r.value)));for(const o of n.options)o.selected=selected.includes(o.value);}
 const rows=(values:Record<string,unknown>[])=>values.map(r=>({value:String(r.code),label:String(r.name??friendly(String(r.code)))}));
 const enums=(key:string,values:string[])=>offer(key,values.map(value=>({value,label:friendly(value)})));
 enums('kind',template.quantityKinds);enums('stage',catalog.authoringPolicies.quantityStages);enums('membership',catalog.authoringPolicies.hourMemberships);
 offer('capacity',rows(catalog.references.quantityGroups.contract));offer('energy',rows(catalog.references.quantityGroups.energy));offer('calendar',rows(catalog.references.calendars));
 let retainedSlots=initial?.slots.map(s=>JSON.stringify(s));
 function refreshChoices(){const d=delivery();
  offer('sources',rows(catalog.definitionsMetadata.volumeSources.filter(r=>r.clock_code===d.clock&&String(r.interval_minutes)===d.cadence)));
  offer('assets',rows(catalog.definitionsMetadata.generationAssets.filter(r=>r.clock_code===d.clock)));
  offer('slot',links().filter(s=>template.linkRoles.includes(s.role)).map(s=>({value:JSON.stringify(s),label:ppaSideLabel(s.role)+' '+s.ordinal})));
  if(retainedSlots?.every(s=>[...field('slot').options].some(o=>o.value===s))){for(const o of field('slot').options)o.selected=retainedSlots.includes(o.value);retainedSlots=undefined;}
  offer('tou',catalog.touVersions.filter(r=>(r.clock as unknown as {code:string})?.code===d.clock).map(r=>({value:JSON.stringify([r.id,r.version]),label:`${r.code} · version ${r.version}`})));
 }
 function sync(busy=false){refreshChoices();const mode=field('kind').value;for(const n of root.querySelectorAll<HTMLElement>('[data-mode]')){n.hidden=n.dataset.mode!==mode||n.hasAttribute('data-tou')&&field('membership').value==='all';for(const f of n.querySelectorAll<HTMLSelectElement>('select'))f.disabled=busy||n.hidden;}field('kind').disabled=busy;}
 function value(key:string){const v=field(key).value;if(!v)throw Error('Complete '+ppaSideLabel(template.role)+': '+friendly(key)+'.');return v;}
 const selected=(key:string)=>{const chosen=[...field(key).selectedOptions].map(o=>o.value),retained=key==='slot'?initial?.slots.map(s=>JSON.stringify(s)):key==='sources'?initial?.sources:key==='assets'?initial?.assets:undefined;return retained&&retained.length===chosen.length&&retained.every(s=>chosen.includes(s))?[...retained]:chosen;};
 function read():Quantity{const kind=value('kind'),q:Quantity={kind,sources:[],stage:null,capacity:null,energy:null,hours:null,slots:[],assets:[]};
  if(kind==='volume'){q.sources=selected('sources');if(!q.sources.length)throw Error('Select volume sources for this side.');q.stage=value('stage');}
  else if(kind==='profile'){const membership=value('membership'),tou=membership==='all'?null:JSON.parse(value('tou'));q.capacity=value('capacity');q.energy=value('energy');q.hours={membership,calendar:value('calendar'),touProduct:tou?.[0]??null,touVersion:tou?.[1]??null};}
  else if(kind==='accredited'){q.assets=selected('assets');if(!q.assets.length)throw Error('Select accredited assets for this side.');}
  else if(kind==='slots'){q.slots=selected('slot').map(s=>JSON.parse(s));if(!q.slots.length)throw Error('Select the delivery sides supplying this quantity.');}
  else throw Error('Choose an admitted quantity source.');return q;
 }
 refreshChoices();if(initial){const values={kind:initial.kind,stage:initial.stage,capacity:initial.capacity,energy:initial.energy,membership:initial.hours?.membership,calendar:initial.hours?.calendar,tou:initial.hours?.touProduct?JSON.stringify([initial.hours.touProduct,initial.hours.touVersion]):''};for(const [key,v]of Object.entries(values))field(key).value=v??'';for(const key of ['sources','assets'] as const)for(const o of field(key).options)o.selected=initial[key].includes(o.value);}
 sync();return{read,sync,linkedSlots:()=>field('kind').value==='slots'?selected('slot').map(s=>JSON.parse(s) as Slot):[]};
}
