import {formatReferenceAddress,type ReferenceAddress} from './reference-address.js';
// Presentation only: a reference item's own page, with a breadcrumb back to the list it belongs to, its identity and its fields
// as a labelled grid. A field naming another item links to that item's address; `editField` names the item's column it shows, where Edit
// may put an input in its place.
export type ItemField={label:string;value:string;linkTo?:{table:string;code:string};editField?:string};
export type ItemHeading={list:{name:string;address:ReferenceAddress};title:string;facts:readonly (readonly [string,string])[]};

export const node=<K extends keyof HTMLElementTagNameMap>(tag:K,content='',className=''):HTMLElementTagNameMap[K]=>{const n=document.createElement(tag);n.textContent=content;if(className)n.className=className;return n;};
function link(label:string,address:ReferenceAddress){const a=node('a',label);a.href=formatReferenceAddress(address);return a;}
export function pair(term:string,value:string|HTMLElement,editField?:string){
  const p=node('div'),definition=node('dd');definition.append(value);if(editField)definition.dataset.editField=editField;p.append(node('dt',term),definition);return p;
}
function pairs(className:string,entries:readonly (readonly [string,string|HTMLElement,string?])[]){
  const list=node('dl','',className);list.append(...entries.map(([term,value,editField])=>pair(term,value,editField)));return list;
}

export function itemHeading(h:ItemHeading){
  const crumb=node('nav','','breadcrumb');crumb.setAttribute('aria-label','Breadcrumb');
  crumb.append(link(h.list.name,h.list.address),node('span',' › '),node('span',h.title));
  crumb.lastElementChild!.setAttribute('aria-current','page');
  return [crumb,node('h2',h.title),pairs('item-facts',h.facts)];
}
export function itemGrid(fields:readonly ItemField[]){
  return pairs('item-grid',fields.map(f=>[f.label,f.linkTo?link(f.value,f.linkTo):f.value,f.editField] as const));
}
export function renderReferenceItem(host:HTMLElement,heading:ItemHeading,fields:readonly ItemField[]){
  host.replaceChildren(...itemHeading(heading),itemGrid(fields));host.hidden=false;
}
