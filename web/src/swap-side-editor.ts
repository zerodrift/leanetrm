// Each supported side has its own presentation template. Payment/observation
// choices, resolved dates, quantities and values are supplied by the backend.
export function mountSwapSideEditor(side:'fixed'|'floating'){
 const host=document.getElementById(`swap-${side}-editor`)!;
 const template=document.getElementById(`swap-${side}-template`) as HTMLTemplateElement;
 host.replaceChildren(template.content.cloneNode(true));
 if(side==='floating'){mountObservationControls(host,'swap');mountIndexSearch(host,'swap-index');}
}

export function mountObservationControls(host:HTMLElement,prefix:string){
  const id=(suffix:string)=>`${prefix}-observation-${suffix}`;
  const averaging=host.querySelector(`#${prefix}-averaging`)!.parentElement!;
  const calendar=document.createElement('div');calendar.id=id('calendar-field');calendar.hidden=true;
  const label=document.createElement('label');label.htmlFor=id('calendar');label.textContent='Observation calendar';
  const choices=document.createElement('select');choices.id=id('calendar');
  calendar.append(label,choices);
  const status=document.createElement('p');status.id=id('status');status.className='field-help';status.setAttribute('role','status');
  const weights=document.createElement('div');weights.id=id('quantities');weights.hidden=true;
  averaging.append(calendar,status,weights);
}

export function mountFirstFloatingEditor(){
 const template=document.getElementById('swap-floating-template') as HTMLTemplateElement;
 const content=template.content.cloneNode(true) as DocumentFragment;
 const names=new Map<string,string>();content.querySelectorAll<HTMLElement>('[id]').forEach(el=>names.set(el.id,el.id.startsWith('swap-floating-')?el.id.replace('swap-floating-','swap-first-'):el.id.replace('swap-','swap-first-')));
 content.querySelectorAll<HTMLElement>('*').forEach(el=>{for(const attribute of ['id','for','aria-labelledby']){const old=el.getAttribute(attribute);if(old&&names.has(old))el.setAttribute(attribute,names.get(old)!);}});
 content.querySelector('h3')!.textContent='First floating side';
 const direction=content.querySelector('#swap-first-direction')!,select=document.createElement('select');select.id='swap-first-direction';select.required=true;select.append(new Option('Choose…',''),new Option('Pay floating','-1'),new Option('Receive floating','1'));direction.replaceWith(select);
 select.parentElement!.querySelector('label')!.setAttribute('for',select.id);
 const host=document.getElementById('swap-first-editor')!;host.replaceChildren(content);mountObservationControls(host,'swap-first');mountIndexSearch(host,'swap-first-index');
}

function mountIndexSearch(host:HTMLElement,id:string){
 const select=host.querySelector<HTMLSelectElement>(`#${id}`)!,label=document.createElement('label'),search=document.createElement('input');
 search.id=id+'-search';search.type='search';search.placeholder='Filter indices and curves';search.autocomplete='off';
 label.htmlFor=search.id;label.textContent='Find index';select.previousElementSibling!.before(label,search);
}

// Present the independent terms in business order; no common economics are inferred.
export function arrangeSwapSections(){
 for(const side of ['fixed','first','floating']){
  const root=document.querySelector(`#swap-${side}-editor .instrument-side`)!;
  const delivery=document.getElementById(`swap-${side}-delivery`)!;
  const observations=document.getElementById(`swap-${side}-observations`)!;
  const prefix=side==='first'?'swap-first':'swap';
  if(side!=='fixed'){
   delivery.append(document.getElementById(prefix+'-commodity')!.parentElement!);
   const spread=document.getElementById(prefix+'-spread')!,label=root.querySelector(`label[for="${prefix}-spread"]`)!;
   const price=document.createElement('div');price.append(label,spread);root.append(price);
   delivery.append(document.getElementById(prefix+'-index')!.parentElement!);
   for(const suffix of ['reset-frequency','projection','averaging','fixing-convention'])observations.append(document.getElementById(prefix+'-'+suffix)!.parentElement!);
  }
  for(const suffix of ['from','to'])delivery.append(document.getElementById(`swap-${side}-${suffix}`)!.parentElement!);
  observations.append(root.querySelector('.side-payment-rule')!);
 }
}
