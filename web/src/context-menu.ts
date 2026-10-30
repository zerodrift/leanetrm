// A context menu (role=menu) opened at a point, shared by the curve graph and the tab dock on Curve builder (markets-desk.md). Arrow keys,
// Home and End move between its items; Escape or Tab closes it and returns focus to what opened it; a press outside closes it.
import {publicText} from './market-view.js';

/** An item without `run` is shown, disabled, to say why nothing is offered. */
export type MenuItem={label:string;run?:()=>void};

export function setupMenu(className:string){
  const menu=document.createElement('div');menu.className=className;menu.setAttribute('role','menu');menu.hidden=true;
  let back:(()=>void)|undefined,expanded:Element|undefined;
  /** Opens the menu at `at`, closing any other first: `back` returns focus, and `trigger` is marked expanded while it is open. */
  function open(name:string,items:MenuItem[],at:{x:number;y:number},returnTo:()=>void,trigger?:Element){
    close();if(!items.length)return;back=returnTo;expanded=trigger;expanded?.setAttribute('aria-expanded','true');menu.setAttribute('aria-label',name);
    menu.replaceChildren(...items.map(item=>{
      const entry=document.createElement('button');entry.type='button';entry.textContent=publicText(item.label);entry.setAttribute('role','menuitem');entry.tabIndex=-1;
      if(item.run)entry.onclick=()=>{close(false);item.run!();};else entry.setAttribute('aria-disabled','true');return entry;}));
    menu.hidden=false;const box=menu.getBoundingClientRect();
    menu.style.left=Math.max(8,Math.min(at.x,innerWidth-box.width-8))+'px';menu.style.top=Math.max(8,Math.min(at.y,innerHeight-box.height-8))+'px';
    // Focus the first item offered, or the first item when none is, so that Escape always closes the menu from the keyboard.
    (menu.querySelector<HTMLElement>('[role=menuitem]:not([aria-disabled])')??menu.querySelector<HTMLElement>('[role=menuitem]'))?.focus();
  }
  function close(refocus=false){
    if(menu.hidden)return;menu.hidden=true;expanded?.setAttribute('aria-expanded','false');expanded=undefined;menu.replaceChildren();
    const to=back;back=undefined;if(refocus)to?.();
  }
  menu.onkeydown=event=>{
    const entries=[...menu.querySelectorAll<HTMLElement>('[role=menuitem]')],i=entries.indexOf(document.activeElement as HTMLElement);
    const move:Record<string,number>={ArrowDown:i+1,ArrowUp:i-1,Home:0,End:entries.length-1};
    if(event.key in move){event.preventDefault();entries[(move[event.key]+entries.length)%entries.length]?.focus();}
    else if(event.key==='Escape'||event.key==='Tab'){event.preventDefault();event.stopPropagation();close(true);}
  };
  document.addEventListener('pointerdown',event=>{if(!menu.hidden&&!menu.contains(event.target as Node))close();},true);
  return {element:menu,open,close};
}

/** Whether a key opens an object's context menu: the context-menu key or Shift+F10. */
export const menuKey=(event:KeyboardEvent)=>event.key==='ContextMenu'||event.shiftKey&&event.key==='F10';
/** Where a menu opened from the keyboard appears: under the start of the object it belongs to. */
export const placeOf=(element:Element)=>{const r=element.getBoundingClientRect();return {x:r.left+Math.min(r.width/2,24),y:r.bottom+4};};
