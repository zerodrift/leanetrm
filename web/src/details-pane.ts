// The Blotter's Details pane (blotter-workbench.md, Details pane): one place below the positions that holds a position, a new trade, a
// risk report or a shocked contract of the delta ladder. Its header steps to the previous and next position in the grid's order, names
// what it shows and collapses it. A mode draws its own content into the parts the shell hands out, as it did in the side panel.
import {askInPanel,askLeave,modeLabel,sameMode,type PanelMode,type SidePanel} from './side-panel.js';

/**
 * The pane over `host`, the dock's Details body. `unsaved` says whether leaving the mode shown would lose edits, which the person is asked
 * about first, before another mode is shown or the details collapse. `step` hears ◂ and ▸, `collapsed` that the person collapsed the
 * details (⌄ or Escape), `expand` that a mode wants the pane in view, and `changed` every change of the mode.
 */
export function setupDetailsPane(host:HTMLElement,o:{unsaved:()=>boolean;busy:()=>boolean;step:(direction:-1|1)=>void;collapsed:()=>void;expand:()=>void;changed:()=>void}){
  const node=<K extends keyof HTMLElementTagNameMap>(tag:K,id:string,className=''):HTMLElementTagNameMap[K]=>{const n=document.createElement(tag);if(id)n.id=id;if(className)n.className=className;return n;};
  const pane=node('section','position-details','details-pane');pane.tabIndex=-1;pane.hidden=true;
  const button=(id:string,text:string,label:string)=>{const b=node('button',id,'icon-button details-control');b.type='button';b.textContent=text;b.setAttribute('aria-label',label);return b;};
  const previous=button('details-previous','◂','Previous position'),next=button('details-next','▸','Next position'),collapse=button('details-collapse','⌄','Collapse details');
  const maximize=button('details-maximize','⛶','Maximize details');
  const heading=node('h2','details-title','details-title'),bar=node('div','','details-bar');bar.append(previous,next,heading,maximize,collapse);
  const part=(name:string,className='')=>node('div',`${pane.id}-${name}`,className);
  const parts={prompt:part('prompt','side-panel-prompt'),head:part('head'),tabs:part('tabs'),body:part('body'),view:part('view'),form:part('form','details-form')};
  parts.prompt.setAttribute('role','alertdialog');parts.prompt.setAttribute('aria-label','Unsaved changes');parts.prompt.hidden=parts.form.hidden=true;
  parts.body.append(parts.view,parts.form);pane.append(bar,parts.prompt,parts.head,parts.tabs,parts.body);
  const empty=node('p','details-empty','field-help');empty.textContent='Select a position and press Enter, or open one, to see it here.';
  host.replaceChildren(empty,pane);
  let mode:PanelMode=null,question:AbortController|undefined;
  function size(expanded:boolean){
    pane.dataset.maximized=String(expanded);maximize.setAttribute('aria-pressed',String(expanded));
    const label=expanded?'Restore details':'Maximize details';maximize.setAttribute('aria-label',label);maximize.title=label;
    maximize.textContent=expanded?'Restore':'⛶';
  }
  size(false);
  const abandonQuestion=()=>{question?.abort();question=undefined;};
  /** One question at a time; a request made while one is open is refused. A reset or a collapse answers the open one no. */
  async function ask(asker:(abandoned:AbortSignal)=>Promise<boolean>):Promise<boolean>{
    if(question)return false;
    const asking=question=new AbortController();
    try{return await asker(asking.signal)&&!asking.signal.aborted;}finally{if(question===asking)question=undefined;}
  }
  /** Asks whether to leave the unsaved edits the pane holds, when it holds any: false means the person kept them. */
  async function leave():Promise<boolean>{
    if(question)return false;
    return mode===null||!o.unsaved()||ask(askLeave(pane));
  }
  function form(on:boolean){parts.view.hidden=on;parts.form.hidden=!on;}
  function place(){const steps=mode?.kind!=='position'||o.busy();previous.disabled=next.disabled=steps;collapse.disabled=o.busy();}
  function draw(){
    if(mode===null)size(false);
    form(false);empty.hidden=mode!==null;pane.hidden=mode===null;pane.dataset.mode=mode?.kind??'';
    pane.setAttribute('aria-label',mode===null?'Details':mode.kind==='position'?'Position details':modeLabel(mode));
    heading.textContent=mode===null?'':modeLabel(mode);place();
    if(mode)o.expand();
    o.changed();
  }
  /**
   * Shows `next`, asking first when the pane holds unsaved edits: false means the person kept them and the pane is as it was. A position
   * leaves focus where the person has it, on the grid's row; another mode takes it, as its form or report is what the person opened.
   */
  async function show(next:PanelMode):Promise<boolean>{
    if(question)return false;
    if(sameMode(next,mode)){if(mode)o.expand();return true;}
    if(!await leave())return false;
    mode=next;draw();
    if(mode&&mode.kind!=='position')pane.focus({preventScroll:true});
    return true;
  }
  /** Closes the details without asking, for a page whose reads, and so whose positions, no longer stand. */
  function reset(){abandonQuestion();if(mode===null)return;mode=null;draw();}
  /** The person collapses the details: a question still open is answered no, the edits are asked about, and the pane closes. */
  async function close(){
    abandonQuestion();
    if(!await leave())return;
    if(mode!==null){mode=null;draw();}
    o.collapsed();
  }
  function focusForm(){
    const opening=mode,controls=()=>Array.from((parts.form.querySelector('form')??parts.form).querySelectorAll<HTMLElement>('select:enabled,input:enabled:not([type=hidden]),textarea:enabled')).find(control=>control.getClientRects().length>0);
    const revealActions=()=>parts.form.querySelector<HTMLElement>('.trade-editor-actions')?.scrollIntoView({block:'start'});
    controls()?.focus({preventScroll:true});
    revealActions();
    // Opening an editor may finish while its action still disables the fields: once painted, focus goes in if neither the mode nor focus moved.
    requestAnimationFrame(()=>{
      const active=document.activeElement;
      if(sameMode(mode,opening)&&!parts.form.hidden&&(!active||active===document.body||active===document.documentElement||active===pane)){controls()?.focus({preventScroll:true});revealActions();}
    });
  }
  previous.onclick=()=>{if(!previous.disabled)o.step(-1);};
  next.onclick=()=>{if(!next.disabled)o.step(1);};
  collapse.onclick=()=>void close();
  maximize.onclick=()=>{size(pane.dataset.maximized!=='true');};
  pane.addEventListener('keydown',event=>{if(event.key==='Escape'&&!event.defaultPrevented){event.preventDefault();void close();}});
  const shell:SidePanel={show,leave,ask,form,reset,place,current:()=>mode,element:name=>parts[name],reveal:o.expand,focusForm};
  return {shell,show,
    /** Collapses the details as ⌄ and Escape do, asking first about edits they hold. */
    collapse:close,
    /** Names what the header shows, such as a position's trade and market. */
    title(text:string){heading.textContent=text;},
    /** Asks a question of the pane's own, as the side panel asks one, after any unsaved-edit question. */
    ask:(asked:{text:string;keep:string;go:string})=>ask(askInPanel(pane,asked))};
}
export type DetailsPane=ReturnType<typeof setupDetailsPane>;
