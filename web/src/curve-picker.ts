// The popover on Curve builder's title, holding the saved curves. Enter or Down opens it, Escape closes it and returns focus to the
// title, focus stays inside while it is open, and a click elsewhere closes it.
export function setupCurvePicker(o:{title:HTMLElement;panel:HTMLElement}){
 const focusable=()=>[...o.panel.querySelectorAll<HTMLElement>('button:not(:disabled),input:not(:disabled),select:not(:disabled),a[href]')].filter(n=>!n.closest('[hidden]'));
 o.title.setAttribute('aria-haspopup','dialog');o.title.setAttribute('aria-expanded','false');
 function show(on:boolean){o.panel.hidden=!on;o.title.setAttribute('aria-expanded',String(on));}
 function open(){show(true);focusable()[0]?.focus();}
 // Focus inside the panel goes back to the title; focus the person has moved elsewhere stays there.
 function close(){if(o.panel.hidden)return;const inside=o.panel.contains(document.activeElement);show(false);if(inside)o.title.focus();}
 o.title.onclick=()=>{if(o.panel.hidden)open();else close();};
 o.title.onkeydown=event=>{if(event.key==='ArrowDown'&&o.panel.hidden){event.preventDefault();open();}};
 o.panel.onkeydown=event=>{
  if(event.key==='Escape'){event.preventDefault();close();o.title.focus();return;}
  if(event.key!=='Tab')return;
  const items=focusable(),first=items[0],last=items.at(-1);
  if(!first||!last)return;
  if(event.shiftKey&&document.activeElement===first){event.preventDefault();last.focus();}
  else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first.focus();}
 };
 document.addEventListener('pointerdown',event=>{if(!o.panel.hidden&&!o.panel.contains(event.target as Node)&&!o.title.contains(event.target as Node))close();});
 return {close};
}
