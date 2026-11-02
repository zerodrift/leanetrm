import {sideColour} from './side-colour.js';

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
