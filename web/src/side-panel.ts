// The Blotter desk's side panel (blotter-desk.md, Side panel): one place beside the positions that holds a position, a new
// trade or a risk report. This shell owns what every mode shares: showing and hiding, docked or overlay placement, focus,
// Escape, the tab strip and asking before unsaved edits are left. A mode draws its own content into the parts the shell hands out.

/** What the panel holds: one position at a revision, a new trade of a product, a risk report, or nothing. */
export type PanelMode=
  |{kind:'position';tradeId:string;revision:number}
  |{kind:'new';product:'futures'|'swaps'}
  |{kind:'risk'}
  |null;

/** The width of the main area from which the panel docks beside the positions; narrower, or beside the assistant, it overlays them. */
const dockedFrom=1180;
export function panelPlacement(mainWidth:number,assistantOpen:boolean):'docked'|'overlay'{
  return !assistantOpen&&mainWidth>=dockedFrom?'docked':'overlay';
}

export function sameMode(a:PanelMode,b:PanelMode):boolean{
  if(a===null||b===null)return a===b;
  if(a.kind!==b.kind)return false;
  if(a.kind==='position'&&b.kind==='position')return a.tradeId===b.tradeId&&a.revision===b.revision;
  if(a.kind==='new'&&b.kind==='new')return a.product===b.product;
  return true;
}

const modeLabels={position:'Position',new:'New trade',risk:'Risk report'} as const;

/** The tab an arrow, Home or End key moves to from `current`, wrapping at the ends; null for any other key. */
export function tabAfterKey(ids:readonly string[],current:string,key:string):string|null{
  const at=ids.indexOf(current);
  if(at<0)return null;
  switch(key){
    case 'ArrowRight':return ids[(at+1)%ids.length];
    case 'ArrowLeft':return ids[(at+ids.length-1)%ids.length];
    case 'Home':return ids[0];
    case 'End':return ids.at(-1)!;
    default:return null;
  }
}

/**
 * Fills a tab strip: buttons in a tablist, the selected one the strip's single tab stop, and the arrow, Home and End keys
 * moving between them and choosing the one they reach. `select` hears every choice the person makes; `mark` shows a tab as the
 * chosen one without that.
 */
export function setupTabs(list:HTMLElement,tabs:readonly (readonly [string,string])[],o:{label:string;panel:HTMLElement;selected:string;select:(id:string)=>void}){
  const ids=tabs.map(([id])=>id);
  const buttons=new Map(tabs.map(([id,label])=>{
    const button=document.createElement('button');button.type='button';button.id=`${list.id}-${id}`;button.className='side-panel-tab';button.textContent=label;
    button.setAttribute('role','tab');button.setAttribute('aria-controls',o.panel.id);button.onclick=()=>choose(id,false);return [id,button] as const;
  }));
  function mark(id:string){
    for(const [key,button] of buttons){button.setAttribute('aria-selected',String(key===id));button.tabIndex=key===id?0:-1;}
    o.panel.setAttribute('aria-labelledby',buttons.get(id)!.id);
  }
  function choose(id:string,focus:boolean){mark(id);if(focus)buttons.get(id)!.focus();o.select(id);}
  list.setAttribute('aria-label',o.label);
  list.onkeydown=event=>{
    const current=ids.find(id=>buttons.get(id)===event.target),next=current===undefined?null:tabAfterKey(ids,current,event.key);
    if(next===null)return;
    event.preventDefault();choose(next,true);
  };
  list.replaceChildren(...buttons.values());mark(o.selected);
  return {mark};
}

/**
 * The shell over the markup of `#side-panel`. `unsaved` says whether leaving the current mode would lose edits, and
 * `confirmLeave` asks the person; it hears `abandoned` aborted when the page resets the panel under its question.
 * `docked` says whether the page has room to dock the panel now, `closed` hears that the person closed it, and `changed` hears
 * every change of the panel's mode or placement, for a page that makes room for it.
 */
export function setupSidePanel(host:HTMLElement,o:{unsaved:()=>boolean;confirmLeave:(abandoned:AbortSignal)=>Promise<boolean>;docked:()=>boolean;closed?:()=>void;changed?:()=>void}){
  const part=(name:'close'|'head'|'tabs'|'body')=>host.querySelector<HTMLElement>('#side-panel-'+name)!;
  const element=(name:'head'|'tabs'|'body')=>part(name);
  let mode:PanelMode=null,question:AbortController|undefined;
  /** Applies docked or overlay placement now: the page calls it whenever the room it has changes. */
  function place(){host.dataset.placement=o.docked()?'docked':'overlay';o.changed?.();}
  function draw(){
    host.hidden=mode===null;host.dataset.mode=mode?.kind??'';
    if(mode){host.setAttribute('aria-label',modeLabels[mode.kind]);place();}else o.changed?.();
  }
  /**
   * Switches the panel to `next`, asking first when it holds unsaved edits: false means the person kept them and the panel is as
   * it was. Only one question is asked at a time; a request made while one is open is refused.
   */
  async function show(next:PanelMode):Promise<boolean>{
    if(question)return false;
    if(sameMode(next,mode)){if(mode)host.focus({preventScroll:true});return true;}
    if(mode!==null&&o.unsaved()){
      const asking=question=new AbortController();
      let leave=false;
      try{leave=await o.confirmLeave(asking.signal);}finally{if(question===asking)question=undefined;}
      if(asking.signal.aborted||!leave)return false;
    }
    mode=next;draw();
    if(mode)host.focus({preventScroll:true});
    return true;
  }
  /** Closes the panel without asking, for a page whose reads, and so whose positions, no longer stand. */
  function reset(){question?.abort();question=undefined;mode=null;draw();}
  async function close(){if(mode!==null&&await show(null))o.closed?.();}
  part('close').onclick=()=>void close();
  host.addEventListener('keydown',event=>{if(event.key==='Escape'&&!event.defaultPrevented){event.preventDefault();void close();}});
  return {show,reset,place,current:()=>mode,element};
}

export type SidePanel=ReturnType<typeof setupSidePanel>;

/** Asks inside the panel whether to discard what it holds unsaved. Escape keeps the edits, as the first button does. */
export function askLeave(host:HTMLElement){
  const slot=host.querySelector<HTMLElement>('#side-panel-prompt')!;
  return (abandoned:AbortSignal)=>new Promise<boolean>(resolve=>{
    const button=(label:string,leave:boolean)=>{const b=document.createElement('button');b.type='button';b.textContent=label;if(!leave)b.className='secondary';b.onclick=()=>finish(leave);return b;};
    function finish(leave:boolean){
      abandoned.removeEventListener('abort',abort);slot.onkeydown=null;slot.replaceChildren();slot.hidden=true;
      if(!abandoned.aborted)host.focus({preventScroll:true});resolve(leave&&!abandoned.aborted);
    }
    const abort=()=>finish(false);
    const text=document.createElement('p');text.textContent='This panel has unsaved changes. Leaving discards them.';
    const actions=document.createElement('div'),keep=button('Keep editing',false);actions.className='side-panel-prompt-actions';actions.append(keep,button('Discard changes',true));
    slot.onkeydown=event=>{if(event.key==='Escape'){event.preventDefault();finish(false);}};
    slot.replaceChildren(text,actions);slot.hidden=false;keep.focus();abandoned.addEventListener('abort',abort);
  });
}
