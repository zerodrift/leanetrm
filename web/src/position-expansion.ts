import {askLeave,askInPanel,sameMode,type PanelMode,type SidePanel} from './side-panel.js';

/** Positions use a persistent row expansion; other desk modes use the side panel. */
export function setupPositionExpansion(side:SidePanel,o:{unsaved:()=>boolean;busy:()=>boolean;changed:()=>void;closed:()=>void;resized:(keepVisible?:boolean)=>void}){
 const host=document.createElement('section');host.id='position-details';host.className='position-expansion';host.tabIndex=-1;host.hidden=true;host.setAttribute('aria-label','Position details');
 const close=document.createElement('button');close.type='button';close.className='secondary position-close';close.textContent='Close position view';
 const part=(name:string)=>{const el=document.createElement('div');el.id=`${host.id}-${name}`;return el;};
 const parts={head:part('head'),tabs:part('tabs'),body:part('body'),view:part('view'),form:part('form'),prompt:part('prompt')};
 parts.prompt.className='side-panel-prompt';parts.prompt.setAttribute('role','alertdialog');parts.prompt.setAttribute('aria-label','Unsaved changes');
 parts.form.hidden=parts.prompt.hidden=true;parts.body.append(parts.view,parts.form);host.append(close,parts.head,parts.tabs,parts.body,parts.prompt);
 let mode:Extract<PanelMode,{kind:'position'}>|null=null,question:AbortController|undefined;
 const abandonQuestion=()=>{question?.abort();question=undefined;};
 const current=():PanelMode=>mode??side.current();
 async function ask(asker:(abandoned:AbortSignal)=>Promise<boolean>){
  if(!mode)return side.ask(asker);if(question)return false;const asking=question=new AbortController();
  try{return await asker(asking.signal)&&!asking.signal.aborted;}finally{if(question===asking)question=undefined;}
 }
 // Focusing the new question can scroll to the bottom of a tall virtual row.
 // Its added height must be measured before that scroll, while it is attached.
 const questionLayout=()=>{if(mode)o.resized(false);};
 const leave=()=>!mode?side.leave():o.unsaved()?ask(askLeave(host,questionLayout)):Promise.resolve(!question);
 async function show(next:PanelMode){
  if(question)return false;
  if(sameMode(current(),next)){if(mode)host.focus({preventScroll:true});return true;}
  if(next?.kind==='position'){
   if(mode?!await leave():!await side.show(null))return false;
   mode=next;host.hidden=false;close.disabled=o.busy();o.changed();
   // The desk attaches the detail to its row after show resolves. Focus only
   // once that placement is painted, and only if this opening still owns it.
   requestAnimationFrame(()=>{if(mode===next&&host.isConnected&&!host.hidden)host.focus({preventScroll:true});});
   return true;
  }
  if(mode&&!await leave())return false;
  const leaving=mode!==null;
  mode=null;host.hidden=true;abandonQuestion();
  if(leaving)o.changed();
  return side.show(next);
 }
 function reset(){mode=null;abandonQuestion();host.hidden=true;side.reset();}
 const position={
  element:(name:'head'|'tabs'|'body'|'view'|'form')=>parts[name],
  form(on:boolean){
   const active=document.activeElement,changed=on===parts.form.hidden,closing=!on&&!parts.form.hidden;
   const keepVisible=closing&&mode!==null&&(!active||host.contains(active)||active===document.body||active===document.documentElement);
   parts.view.hidden=on;parts.form.hidden=!on;
   // A following prompt may focus beneath the newly opened form before the
   // ResizeObserver runs. Measure this structural change synchronously.
   if(changed&&mode!==null)o.resized(keepVisible);
  },
  reveal:()=>host.scrollIntoView({block:'nearest'}),
  focusForm(){
   const opening=mode,focus=()=>Array.from(parts.form.querySelectorAll<HTMLElement>('select:enabled,input:enabled:not([type=hidden]),textarea:enabled'))
    .find(control=>control.getClientRects().length>0)?.focus({preventScroll:true});
   focus();
   // Opening an editor may finish while its action still disables the fields.
   // Retry after painting, provided neither the position nor focus has moved.
   requestAnimationFrame(()=>{
    const active=document.activeElement;
    if(mode===opening&&!parts.form.hidden&&host.isConnected&&(!active||active===document.body||active===document.documentElement||active===host))focus();
   });
  },
 };
 const shell:SidePanel={show,reset,current,leave,ask,
  element:name=>mode?position.element(name):side.element(name),form:on=>mode?position.form(on):side.form(on),
  reveal:()=>mode?position.reveal():side.reveal(),focusForm:()=>mode?position.focusForm():side.focusForm(),place:()=>{close.disabled=o.busy();side.place();o.resized();}};
 const closePosition=()=>{void show(null).then(closed=>{if(closed)o.closed();});};
 close.onclick=closePosition;
 host.addEventListener('keydown',event=>{
  // Tabs, calendars and form fields own their keys; the surrounding grid must
  // not also handle Home, End or arrows as navigation between trade rows.
  event.stopPropagation();
  if(event.key==='Escape'&&!event.defaultPrevented){event.preventDefault();closePosition();}
 });
 // Detail reads and forms change height after the grid first places the row.
 let height=0;new ResizeObserver(()=>{const next=host.offsetHeight;if(next!==height){height=next;o.resized();}}).observe(host);
 return{host,shell,position,ask:(question:{text:string;keep:string;go:string})=>ask(askInPanel(mode?host:document.getElementById('side-panel')!,question,mode?questionLayout:undefined))};
}
