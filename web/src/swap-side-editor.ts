// Each supported side has its own presentation template. Payment/observation
// choices, resolved dates, quantities and values are supplied by the backend.
export function mountSwapSideEditor(side:'fixed'|'floating'){
 const host=document.getElementById(`swap-${side}-editor`)!;
 const template=document.getElementById(`swap-${side}-template`) as HTMLTemplateElement;
 host.replaceChildren(template.content.cloneNode(true));
 if(side==='floating')mountIndexSearch(host,'swap-index');
}

export function mountFirstFloatingEditor(){
 const template=document.getElementById('swap-floating-template') as HTMLTemplateElement;
 const content=template.content.cloneNode(true) as DocumentFragment;
 const names=new Map<string,string>();content.querySelectorAll<HTMLElement>('[id]').forEach(el=>names.set(el.id,el.id.startsWith('swap-floating-')?el.id.replace('swap-floating-','swap-first-'):el.id.replace('swap-','swap-first-')));
 content.querySelectorAll<HTMLElement>('*').forEach(el=>{for(const attribute of ['id','for','aria-labelledby']){const old=el.getAttribute(attribute);if(old&&names.has(old))el.setAttribute(attribute,names.get(old)!);}});
 content.querySelector('h3')!.textContent='First floating side';
 const direction=content.querySelector('#swap-first-direction')!,select=document.createElement('select');select.id='swap-first-direction';select.required=true;select.append(new Option('Choose…',''),new Option('Pay floating','-1'),new Option('Receive floating','1'));direction.replaceWith(select);
 content.querySelector('label')!.setAttribute('for',select.id);
 const host=document.getElementById('swap-first-editor')!;host.replaceChildren(content);mountIndexSearch(host,'swap-first-index');
}

function mountIndexSearch(host:HTMLElement,id:string){
 const select=host.querySelector<HTMLSelectElement>(`#${id}`)!;
 const label=document.createElement('label'),search=document.createElement('input');
 search.id=`${id}-search`;search.type='search';search.placeholder='Filter indices and curves';search.autocomplete='off';
 label.htmlFor=search.id;label.textContent='Find index';select.previousElementSibling!.before(label,search);
}

// Keep independent controls in business order without inventing shared terms.
export function arrangeSwapSections(){
 for(const side of ['fixed','first','floating']){
  const root=document.querySelector(`#swap-${side}-editor .instrument-side`)!;
  for(const suffix of ['from','to'])document.getElementById(`swap-${side}-delivery`)!.append(document.getElementById(`swap-${side}-${suffix}`)!.parentElement!);
  const observations=document.getElementById(`swap-${side}-observations`)!;
  if(side!=='fixed')for(const suffix of ['reset-frequency','projection','averaging','fixing-mode','fixing'])observations.append(document.getElementById(`swap-${side==='first'?'first-':''}${suffix}`)!.parentElement!);
  observations.append(root.querySelector('.side-payment-rule')!);
 }
}
