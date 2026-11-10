import {publicText} from './market-view.js';

// A close a page offers beside the header's As of. Switching moves the whole app; a separate window keeps this one where it is and opens
// the same app at that close (D361). The close travels by its recorded close id, as a frame's id belongs to one session's listing.
export type CloseTarget={page:'market';curve:string}|{page:'vol-surfaces';product:string;definitionRevisionId?:string};
export type CloseLink=CloseTarget&{close:string};

export function closeLink(search:string):CloseLink|null{
 const query=new URLSearchParams(search),close=query.get('close');
 if(!close)return null;
 const curve=query.get('curve'),product=query.get('product');
 if(query.get('page')==='market'&&curve)return {close,page:'market',curve};
 if(query.get('page')==='vol-surfaces'&&product){
  const definitionRevisionId=query.get('definitionRevisionId');
  return {close,page:'vol-surfaces',product,...(definitionRevisionId===null?{}:{definitionRevisionId})};
 }
 return null;
}
export function closeLinkHref(link:CloseLink,path:string,search:string){
 const query=new URLSearchParams(search);for(const name of ['close','page','curve','product','definitionRevisionId'])query.delete(name);
 for(const [name,value] of Object.entries(link))if(value!==undefined)query.set(name,value);
 return `${path}?${query}`;
}

// Resolves true when the person chooses to switch the As of; opening a window or cancelling leaves this window as it is.
export function askCloseChoice(options:{close:string;current:string;windowUrl:string|null}):Promise<boolean>{
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text:string)=>{const n=document.createElement(tag);n.textContent=publicText(text);return n;};
 const dialog=document.createElement('dialog');dialog.id='close-choice-dialog';dialog.className='close-choice-dialog';dialog.setAttribute('aria-labelledby','close-choice-title');
 const title=el('h2',`Open ${options.close}?`),note=el('p',`Your As of is ${options.current}. Trades, risk and every other page read the As of.`);title.id='close-choice-title';note.id='close-choice-note';
 const actions=document.createElement('div');actions.className='close-choice-actions';
 const switching=el('button',`Switch As of to ${options.close}`),cancel=el('button','Cancel');switching.type=cancel.type='button';switching.id='close-choice-switch';cancel.id='close-choice-cancel';cancel.className='secondary';
 actions.append(switching);
 return new Promise(resolve=>{
  const done=(choice:boolean)=>{dialog.close();dialog.remove();resolve(choice);};
  if(options.windowUrl){
   const separate=el('a','Open in a separate window');separate.id='close-choice-window';separate.className='button-link secondary';separate.href=options.windowUrl;separate.target='_blank';separate.rel='noopener';
   separate.addEventListener('click',()=>setTimeout(()=>done(false)));actions.append(separate);
  }
  actions.append(cancel);dialog.append(title,note,actions);
  switching.onclick=()=>done(true);cancel.onclick=()=>done(false);
  dialog.addEventListener('cancel',event=>{event.preventDefault();done(false);});
  document.body.append(dialog);dialog.showModal();switching.focus();
 });
}
