// Each supported side has its own presentation template. Payment/observation
// choices, resolved dates, quantities and values are supplied by the backend.
export function mountSwapSideEditor(side:'fixed'|'floating'){
 const host=document.getElementById(`swap-${side}-editor`)!;
 const template=document.getElementById(`swap-${side}-template`) as HTMLTemplateElement;
 host.replaceChildren(template.content.cloneNode(true));
}

export function mountFirstFloatingEditor(){
 const template=document.getElementById('swap-floating-template') as HTMLTemplateElement;
 const content=template.content.cloneNode(true) as DocumentFragment;
 const names=new Map<string,string>();content.querySelectorAll<HTMLElement>('[id]').forEach(el=>names.set(el.id,el.id.startsWith('swap-floating-')?el.id.replace('swap-floating-','swap-first-'):el.id.replace('swap-','swap-first-')));
 content.querySelectorAll<HTMLElement>('*').forEach(el=>{for(const attribute of ['id','for','aria-labelledby']){const old=el.getAttribute(attribute);if(old&&names.has(old))el.setAttribute(attribute,names.get(old)!);}});
 content.querySelector('h3')!.textContent='First floating side';
 const direction=content.querySelector('#swap-first-direction')!,select=document.createElement('select');select.id='swap-first-direction';select.required=true;select.append(new Option('Choose…',''),new Option('Pay floating','-1'),new Option('Receive floating','1'));direction.replaceWith(select);
 content.querySelector('label')!.setAttribute('for',select.id);
 document.getElementById('swap-first-editor')!.replaceChildren(content);
}
