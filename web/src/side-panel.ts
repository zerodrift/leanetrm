// The side panel (blotter-desk.md, Side panel): one place beside the Blotter's positions that holds a position, a new trade or a
// risk report, and beside a curve on Curves & prices that holds one of its contracts (markets-desk.md). This shell owns what every
// mode shares: showing and hiding, docked or overlay placement, focus, Escape, the tab strip and asking before unsaved edits are left.
// A mode draws its own content into the parts the shell hands out.

/**
 * What a panel holds: on the Blotter, one position (a trade's binding) at a revision, a new trade of a product or, until the person
 * has chosen one, of none, a risk report, or a shocked contract of the delta ladder (D230); on Curves & prices, one contract (or
 * maturity) of a curve; or nothing.
 */
export type PanelMode=
  |{kind:'position';tradeId:string;bindingId:string;revision:number}
  |{kind:'new';product:'futures'|'swaps'|'ppa'|'gas'|null}
  |{kind:'risk'}
  |{kind:'contract';factorId:string}
  |{kind:'curve-contract';curve:string;contract:string}
  |null;

/** The width of the main area from which the panel docks beside the positions; narrower, or beside the assistant, it overlays them. */
const dockedFrom=1180;
export function panelPlacement(mainWidth:number,assistantOpen:boolean):'docked'|'overlay'{
  return !assistantOpen&&mainWidth>=dockedFrom?'docked':'overlay';
}

export function sameMode(a:PanelMode,b:PanelMode):boolean{
  if(a===null||b===null)return a===b;
  if(a.kind!==b.kind)return false;
  if(a.kind==='position'&&b.kind==='position')return a.tradeId===b.tradeId&&a.bindingId===b.bindingId&&a.revision===b.revision;
  if(a.kind==='new'&&b.kind==='new')return a.product===b.product;
  if(a.kind==='contract'&&b.kind==='contract')return a.factorId===b.factorId;
  if(a.kind==='curve-contract'&&b.kind==='curve-contract')return a.curve===b.curve&&a.contract===b.contract;
  return true;
}

/** What a mode is called, for the person and for assistive technology: a new trade names its product once it has one. */
export function modeLabel(mode:NonNullable<PanelMode>):string{
  switch(mode.kind){
    case 'position':return 'Position';
    case 'new':return mode.product===null?'New trade':({futures:'New future',swaps:'New swap',ppa:'New PPA',gas:'New physical gas trade'} as const)[mode.product];
    case 'risk':return 'Risk report';
    case 'contract':return 'Contract';
    case 'curve-contract':return 'Contract';
  }
}

/** What new inputs on screen do to the risk report: whether its setup's choices are carried over, whether it is read again, and the note. */
export type RiskRefresh={carry:boolean;reopen:boolean;note:string|null};
const riskNotes={
  live:{cleared:'Risk results cleared · live corrections changed the inputs',
    retained:'Live corrections changed the inputs · the unconfirmed risk submission keeps the inputs it was captured at'},
  settlement:{cleared:'Risk results cleared · a settlement record changed the values they were run on',
    retained:'A settlement record changed the values · the unconfirmed risk submission keeps the inputs it was captured at'},
} as const;
/**
 * What new inputs on screen, from a live correction (D188) or a settlement record, do to the risk report (D221). A run on screen is
 * forgotten, as its results belong to the inputs that changed, with a note: one that says it was cleared, or, for an unconfirmed
 * submission the report shows again under the inputs it was captured at, one that says so. An open report is read again at the new
 * inputs, so that a run is always made at the inputs on screen, and a setup holding the person's edits has its choices carried over.
 */
export function riskRefreshPlan(state:{open:boolean;edited:boolean;run:boolean;retained:boolean},cause:keyof typeof riskNotes):RiskRefresh{
  return {carry:state.open&&state.edited,reopen:state.open,note:state.run?riskNotes[cause][state.retained?'retained':'cleared']:null};
}
/**
 * What a live correction (D188) does to the panel. A panel showing a position or a new trade is read again, unless it holds unsaved edits,
 * which a refresh never takes from the person; the risk report follows `riskRefreshPlan`, whose edits are the setup's.
 */
export function liveRefreshPlan(state:{panel:PanelMode;unsaved:boolean;riskRun:boolean;riskRetained:boolean}):{reloadPanel:boolean;risk:RiskRefresh}{
  const open=state.panel?.kind==='risk';
  return {reloadPanel:state.panel!==null&&!open&&!state.unsaved,
    risk:riskRefreshPlan({open,edited:open&&state.unsaved,run:state.riskRun,retained:state.riskRetained},'live')};
}

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
 * chosen one without that. The strip is a tablist, and the panel it controls a tabpanel, for as long as the strip holds tabs and
 * no longer: `clearTabs` takes both roles away, so that a mode with no tabs has neither.
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
  list.setAttribute('role','tablist');list.setAttribute('aria-label',o.label);o.panel.setAttribute('role','tabpanel');o.panel.removeAttribute('aria-label');
  list.onkeydown=event=>{
    const current=ids.find(id=>buttons.get(id)===event.target),next=current===undefined?null:tabAfterKey(ids,current,event.key);
    if(next===null)return;
    event.preventDefault();choose(next,true);
  };
  list.replaceChildren(...buttons.values());mark(o.selected);
  return {mark};
}

/** Takes the tabs out of a tab strip, and with them the roles of the strip and of the panel they controlled. */
export function clearTabs(list:HTMLElement,panel:HTMLElement){
  list.replaceChildren();list.onkeydown=null;
  for(const name of ['role','aria-label'])list.removeAttribute(name);
  for(const name of ['role','aria-labelledby','aria-label'])panel.removeAttribute(name);
}

/**
 * Takes the page behind a panel that covers it out of reach: every node beside the panel, and beside each of its ancestors, becomes
 * inert, so focus cannot leave the panel for what it hides. A dialog is left alone, being modal by itself, and so is anything that
 * was inert already. Returns what gives the page back.
 */
export function isolate(host:HTMLElement):()=>void{
  const made:HTMLElement[]=[];
  for(let node:HTMLElement=host;node.parentElement;node=node.parentElement){
    for(const sibling of Array.from(node.parentElement.children) as HTMLElement[]){
      if(sibling===node||sibling.localName==='dialog'||sibling.inert)continue;
      sibling.inert=true;made.push(sibling);
    }
  }
  return ()=>{for(const node of made)node.inert=false;};
}

/**
 * The shell over a panel's markup, whose parts are named by its own id (`#side-panel-head` for `#side-panel`): a head, a tab strip and a tab panel, which holds a `view` that a mode draws its tab
 * into and a `form` that takes its place while the mode edits something. `unsaved` says whether leaving the current mode would
 * lose edits, and `confirmLeave` asks the person; it hears `abandoned` aborted when the page resets the panel under its question.
 * `docked` says whether the page has room to dock the panel now, and `sheet` whether the open panel covers the whole page, which
 * is then out of reach until the panel is closed or stops covering it. `wide` says whether what the panel shows needs more room than
 * a docked panel has: the panel then lies over the page at a wider width, and the page beneath keeps its layout. `closed` hears
 * that the person closed the panel, and `changed` hears every change of its mode or placement, for a page that makes room for it.
 */
export function setupSidePanel(host:HTMLElement,o:{unsaved:()=>boolean;confirmLeave:(abandoned:AbortSignal)=>Promise<boolean>;docked:()=>boolean;sheet?:()=>boolean;wide?:()=>boolean;closed?:()=>void;changed?:()=>void}){
  const part=(name:'close'|'head'|'tabs'|'body'|'view'|'form')=>host.querySelector<HTMLElement>(`#${host.id}-${name}`)!;
  const element=(name:'head'|'tabs'|'body'|'view'|'form')=>part(name);
  let mode:PanelMode=null,question:AbortController|undefined,release:(()=>void)|undefined;
  /** The page behind a panel that covers it is out of reach while the panel is open, and only then. */
  function contain(){
    const covers=mode!==null&&(o.sheet?.()??false);
    if(covers&&!release)release=isolate(host);
    else if(!covers&&release){release();release=undefined;}
  }
  /** Applies docked or overlay placement, and the wide width, now: the page calls it whenever the room it has, or what it shows, changes. */
  function place(){host.dataset.placement=o.docked()?'docked':'overlay';if(mode!==null&&o.wide?.())host.dataset.wide='';else delete host.dataset.wide;contain();o.changed?.();}
  /** Shows the form in the tab panel in place of its view, or the view again. Every mode starts on its view. */
  function form(on:boolean){part('view').hidden=on;part('form').hidden=!on;}
  /**
   * Scrolls the page down to the panel when it starts below the window's top, for a form that needs the room: a docked panel keeps in
   * view from there as the page scrolls. A page already scrolled past the panel's start, and an overlay, stay where they are.
   */
  function reveal(){const top=host.getBoundingClientRect().top;if(top>0)window.scrollBy(0,top);}
  /** Moves focus to the first control of the form that can take it: one that is enabled and shown. */
  function focusForm(){
    const controls=part('form').querySelectorAll<HTMLElement>('select:enabled,input:enabled:not([type=hidden]),textarea:enabled');
    Array.from(controls).find(control=>control.getClientRects().length>0)?.focus({preventScroll:true});
  }
  function draw(){
    form(false);host.hidden=mode===null;host.dataset.mode=mode?.kind??'';
    if(mode){host.setAttribute('aria-label',modeLabel(mode));place();}else{delete host.dataset.wide;contain();o.changed?.();}
  }
  /**
   * Asks the person a question in the panel with `asker`: true only for an answer to go on that arrives before the page resets the
   * panel. Only one question is asked at a time; a request made while one is open is refused.
   */
  async function ask(asker:(abandoned:AbortSignal)=>Promise<boolean>):Promise<boolean>{
    if(question)return false;
    const asking=question=new AbortController();
    let going=false;
    try{going=await asker(asking.signal);}finally{if(question===asking)question=undefined;}
    return going&&!asking.signal.aborted;
  }
  /** Asks whether to leave the unsaved edits the panel holds, when it holds any: false means the person kept them. It is a question, not a move. */
  async function leave():Promise<boolean>{
    if(question)return false;
    if(mode===null||!o.unsaved())return true;
    return ask(o.confirmLeave);
  }
  /** Switches the panel to `next`, asking first when it holds unsaved edits: false means the person kept them and the panel is as it was. */
  async function show(next:PanelMode):Promise<boolean>{
    if(question)return false;
    if(sameMode(next,mode)){if(mode)host.focus({preventScroll:true});return true;}
    if(!await leave())return false;
    mode=next;draw();
    if(mode)host.focus({preventScroll:true});
    return true;
  }
  /** Closes the panel without asking, for a page whose reads, and so whose positions, no longer stand. */
  function reset(){question?.abort();question=undefined;mode=null;draw();}
  async function close(){if(mode!==null&&await show(null))o.closed?.();}
  part('close').onclick=()=>void close();
  host.addEventListener('keydown',event=>{if(event.key==='Escape'&&!event.defaultPrevented){event.preventDefault();void close();}});
  return {show,leave,ask,form,reveal,focusForm,reset,place,current:()=>mode,element};
}

export type SidePanel=ReturnType<typeof setupSidePanel>;

/**
 * Asks a question inside the panel: what it asks, the answer that keeps things as they are, which has focus, and the one that goes on.
 * Escape keeps, as the first button does.
 */
export function askInPanel(host:HTMLElement,question:{text:string;keep:string;go:string},beforeFocus?:()=>void){
  const slot=host.querySelector<HTMLElement>(`#${host.id}-prompt`)!;
  return (abandoned:AbortSignal)=>new Promise<boolean>(resolve=>{
    const button=(label:string,going:boolean)=>{const b=document.createElement('button');b.type='button';b.textContent=label;if(!going)b.className='secondary';b.onclick=()=>finish(going);return b;};
    function finish(going:boolean){
      abandoned.removeEventListener('abort',abort);slot.onkeydown=null;slot.replaceChildren();slot.hidden=true;
      if(!abandoned.aborted)host.focus({preventScroll:true});resolve(going&&!abandoned.aborted);
    }
    const abort=()=>finish(false);
    const text=document.createElement('p');text.textContent=question.text;
    const actions=document.createElement('div'),keep=button(question.keep,false);actions.className='side-panel-prompt-actions';actions.append(keep,button(question.go,true));
    slot.onkeydown=event=>{if(event.key==='Escape'){event.preventDefault();finish(false);}};
    slot.replaceChildren(text,actions);slot.hidden=false;beforeFocus?.();keep.focus();abandoned.addEventListener('abort',abort);
  });
}
/** Asks inside the panel whether to discard what it holds unsaved. */
export const askLeave=(host:HTMLElement,beforeFocus?:()=>void)=>askInPanel(host,{text:'This panel has unsaved changes. Leaving discards them.',keep:'Keep editing',go:'Discard changes'},beforeFocus);
