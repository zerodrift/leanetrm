import {sideColour} from './side-colour.js';

// These are presentation rows, not shared contractual terms. A missing term
// leaves space beside the other side; no value or control is copied.
const sideRows=['product','observations','direction','currency','start','end','quantity','price','payment','periods'] as const;
type SideRow=typeof sideRows[number];
export function prepareSideDeck(deck:HTMLElement){
 if(deck.parentElement?.classList.contains('side-comparison'))return;
 const frame=document.createElement('div');frame.className='side-comparison';
 deck.before(frame);frame.append(deck);
}
export function alignSideCard(card:HTMLElement,rows:Partial<Record<SideRow,HTMLElement[]>>){
 const remaining=[...card.children].filter(n=>!n.classList.contains('ticket-card-head'));
 for(const [index,key] of sideRows.entries()){
  const nodes=rows[key];if(!nodes?.length)continue;
  const row=document.createElement('div');row.className='entry-side-row';row.dataset.sideRow=key;
  row.style.setProperty('--side-row',String(index+2));row.append(...nodes);card.append(row);
 }
 const details=document.createElement('div');details.className='entry-side-details';
 for(const node of remaining)if(node.parentElement===card)details.append(node);
 card.append(details);card.classList.add('aligned-side-card');
}

// Shared presentation for gas and the native product editors. The caller owns
// permitted side kinds, quantity links, limits and booking operations.
export function sideCardHeader(title:string|HTMLElement,pill:string){
 const head=document.createElement('header');head.className='ticket-card-head';
 const name=typeof title==='string'?document.createElement('strong'):title;
 if(typeof title==='string')name.textContent=title;name.classList.add('ticket-side-name');
 const badge=document.createElement('span');badge.className='ticket-pill';badge.textContent=pill;
 head.append(name,badge);return head;
}
export function decorateSideCard(node:HTMLElement,title:HTMLElement,pill:string,colour:string){
 node.classList.add('ticket-card','entry-side-card');node.style.setProperty('--side-colour',sideColour(colour));
 node.prepend(sideCardHeader(title,pill));
}
export function sideAddButton(label:string,run:()=>void,disabled=false,reason?:string){
 const button=document.createElement('button');button.type='button';button.className='ticket-add-kind';button.textContent='+ '+label;
 button.disabled=disabled;if(reason)button.title=reason;button.onclick=()=>{if(!button.disabled)run();};return button;
}
