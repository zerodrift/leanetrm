import {linkedValue} from './choice-selection.js';

// Field meanings come from the native setup catalogs. Keep the accessible label
// short and associate the longer definition as a description, below the control.
export function describeDeliveryField(control:HTMLInputElement|HTMLSelectElement,description:string|undefined){
 const label=control.closest('label');if(!label)return;
 let field=label.parentElement!;
 if(!field.classList.contains('delivery-setup-field')){
  field=document.createElement('div');field.className='delivery-setup-field';label.replaceWith(field);field.append(label);
 }
 const id=control.id+'-help';let help=field.querySelector<HTMLElement>('.field-help');
 if(!help){help=document.createElement('p');help.className='field-help';field.append(help);}
 help.id=id;help.textContent=description??'Field definition unavailable. Reload this setup.';control.setAttribute('aria-describedby',id);
}

// Call only when starting a new entry or opening a fresh browsing selector.
// Existing values and later explicit clears belong to the person editing them.
export function selectOnlyDeliveryChoice(control:HTMLSelectElement){
 if(control.value)return false;
 const value=linkedValue([...control.options].filter(option=>option.value&&!option.disabled).map(option=>option.value));
 if(!value)return false;
 control.value=value;return true;
}
