// The tab dock shared by Curve builder and the Blotter: draws an arrangement of dock-model.ts as tab groups side by side or above one
// another. A tab moves by dragging its group's grip or tab onto a group's centre or edge, or from its menu (right-click, the
// context-menu key or Shift+F10); a divider resizes a split; a group's collapse button puts its shown tab away to a one-line bar. The
// arrangement is this viewer's presentation state: the caller restores it into `initial` and stores what `changed` hears.
// A narrow page shows every tab in one group and leaves the saved arrangement for when the page is wide again.
import {type Arrangement,type DockLayout,type DockRules,type DropEdge,type Group,type Split,collapse,groupsOf,moveArrangement,moveTab,resize,showTab,splitsOf,toggleCollapsed} from './dock-model.js';
import {menuKey,placeOf,setupMenu,type MenuItem} from './context-menu.js';
import {tabAfterKey} from './side-panel.js';

export type Tab={id:string;label:string;body:HTMLElement};
const edges:readonly DropEdge[]=['top','left','centre','right','bottom'];
/** The arrangement without the selected tabs and split ratios: when only those change, the groups are updated where they stand. */
const form=(l:DockLayout):unknown=>l.kind==='group'?l.tabs:[l.direction,l.children.map(form)];
const shape=(a:Arrangement)=>JSON.stringify([form(a.layout),[...a.collapsed].sort()]);

export type Dock={arrangement():Arrangement;set(a:Arrangement):void;reveal(tab:string):void;relayout():void;destroy():void};

/** `changed` hears each arrangement the viewer makes (never one set by the caller or a narrow page's); `shown` hears each tab whose
 *  body comes into view, also when the dock is drawn again and moves it, so that what it draws can measure itself there (a moved element
 *  loses its scroll offsets); `label` names the tab groups to assistive technology. */
export function setupDock(host:HTMLElement,o:{tabs:readonly Tab[];narrow:()=>boolean;rules:DockRules;initial:Arrangement;changed:(a:Arrangement)=>void;shown?:(id:string)=>void;label?:string}):Dock{
  const {rules}=o,label=new Map(o.tabs.map(t=>[t.id,t.label]));
  const flat=(a:Arrangement):Arrangement=>({layout:collapse(a.layout),collapsed:a.collapsed});
  let saved=o.initial,narrow=o.narrow(),shown=narrow?flat(saved):saved,dragged:string|undefined;
  const menu=setupMenu('dock-menu');
  // Each tab keeps its button and body for the page's life; a new arrangement moves them into new groups.
  const buttons=new Map(o.tabs.map(t=>[t.id,tabButton(t)]));
  const bodies=new Map(o.tabs.map(t=>[t.id,t.body]));
  // The tabs in view (those the page shows first it draws itself), and the drawn splits by their depth-first numbers (splitsOf).
  let visible=new Set(groupsOf(shown.layout).map(g=>g.active));
  let drawnSplits:{first:HTMLElement;second:HTMLElement;divider:HTMLElement}[]=[];

  const div=(className:string)=>{const node=document.createElement('div');node.className=className;return node;};

  function tabButton(t:Tab){
    const b=document.createElement('button');b.type='button';b.id=`${host.id}-${t.id}`;b.className='dock-tab';b.textContent=t.label;
    b.setAttribute('role','tab');b.setAttribute('aria-haspopup','menu');b.setAttribute('aria-expanded','false');b.draggable=true;
    if(!t.body.id)t.body.id=`${b.id}-panel`;
    b.setAttribute('aria-controls',t.body.id);t.body.setAttribute('role','tabpanel');t.body.setAttribute('aria-labelledby',b.id);
    b.onclick=()=>reveal(t.id);
    b.onkeydown=event=>{
      if(menuKey(event)){event.preventDefault();openMenu(t.id,placeOf(b));return;}
      const group=groupsOf(shown.layout).find(g=>g.tabs.includes(t.id))!,next=tabAfterKey(group.tabs,t.id,event.key);
      if(next===null)return;
      event.preventDefault();reveal(next);buttons.get(next)!.focus();
    };
    b.oncontextmenu=event=>{event.preventDefault();openMenu(t.id,{x:event.clientX,y:event.clientY});};
    dragSource(b,t.id);
    return b;
  }
  /** Makes `node` pick up `tab` for a drop on a group. */
  function dragSource(node:HTMLElement,tab:string){
    node.ondragstart=event=>{
      if(narrow){event.preventDefault();return;}
      dragged=tab;host.dataset.dragging='';
      if(event.dataTransfer){event.dataTransfer.effectAllowed='move';event.dataTransfer.setData('text/plain',label.get(tab)!);}
    };
    node.ondragend=endDrag;
  }
  function endDrag(){dragged=undefined;delete host.dataset.dragging;for(const zone of host.querySelectorAll('.dock-zone.over'))zone.classList.remove('over');}
  /** A place a dragged tab can be dropped: it shows while over it, and a drop moves the tab there. */
  function dropTarget(node:HTMLElement,group:number,edge:DropEdge){
    node.ondragover=event=>{if(dragged===undefined)return;event.preventDefault();if(event.dataTransfer)event.dataTransfer.dropEffect='move';node.classList.add('over');};
    node.ondragleave=()=>node.classList.remove('over');
    node.ondrop=event=>{if(dragged===undefined)return;event.preventDefault();const tab=dragged;endDrag();move(tab,{group,edge});};
  }

  function drawGroup(g:Group,index:number,depth:number){
    const group=div('dock-group'),panels=div('dock-panels'),zones=div('dock-zones'),away=shown.collapsed.includes(g.active);
    group.dataset.group=String(index);
    for(const id of g.tabs)panels.append(bodies.get(id)!);
    if(away){
      group.dataset.collapsed='';panels.hidden=true;
      const bar=div('dock-bar'),title=document.createElement('span');title.className='dock-title';title.textContent=label.get(g.active)!;
      bar.append(title,collapseButton(g.active,true));group.append(bar,panels);mark(g);return group;
    }
    const head=div('dock-head'),strip=div('dock-tabs'),grip=div('dock-grip');
    strip.setAttribute('role','tablist');strip.setAttribute('aria-label',`${o.label??'Views'}, group ${index+1}`);
    for(const id of g.tabs)strip.append(buttons.get(id)!);
    grip.draggable=true;grip.title='Drag to move';grip.textContent='⠿';grip.setAttribute('aria-hidden','true');dragSource(grip,g.active);
    head.append(strip,grip);
    if(!rules.fixed.includes(g.active))head.append(collapseButton(g.active,false));
    for(const edge of depth>=rules.maxDepth?['centre' as const]:edges){const zone=div('dock-zone');zone.dataset.edge=edge;dropTarget(zone,index,edge);zones.append(zone);}
    dropTarget(strip,index,'centre');
    group.append(head,panels,zones);mark(g);return group;
  }
  /** The button that puts a tab's pane away to a bar, or, on the bar, brings it back. */
  function collapseButton(tab:string,away:boolean){
    const b=document.createElement('button');b.type='button';b.className='dock-collapse';b.dataset.collapse=tab;b.textContent=away?'⌃':'⌄';
    b.setAttribute('aria-expanded',String(!away));b.setAttribute('aria-label',`${away?'Expand':'Collapse'} ${label.get(tab)}`);
    b.onclick=()=>toggle(tab);
    return b;
  }
  function drawSplit(l:Split,depth:number,count:{groups:number;splits:number}):HTMLElement{
    const node=div('dock-split'),at=count.splits++;node.dataset.direction=l.direction;
    const [first,second]=l.children.map(child=>draw(child,depth+1,count));
    const divider=div('dock-divider');divider.tabIndex=0;divider.setAttribute('role','separator');divider.setAttribute('aria-label','Resize the groups');
    divider.setAttribute('aria-orientation',l.direction==='row'?'vertical':'horizontal');divider.setAttribute('aria-valuemin',String(Math.round(rules.ratioRange[0]*100)));divider.setAttribute('aria-valuemax',String(Math.round(rules.ratioRange[1]*100)));
    node.append(first,divider,second);drawnSplits[at]={first,second,divider};
    // A side collapsed to bars keeps its own size (proportion), so a ratio would change nothing on screen: the divider does not move.
    if(l.children.some(barred)){divider.tabIndex=-1;divider.setAttribute('aria-disabled','true');return node;}
    const ratioAt=(event:PointerEvent)=>{const box=node.getBoundingClientRect();return l.direction==='row'?(event.clientX-box.left)/box.width:(event.clientY-box.top)/box.height;};
    divider.onpointerdown=event=>{
      if(event.button!==0)return;event.preventDefault();divider.setPointerCapture(event.pointerId);
      divider.onpointermove=moved=>apply({layout:resize(shown.layout,at,ratioAt(moved),rules),collapsed:shown.collapsed},false);
      const end=()=>{divider.onpointermove=divider.onpointerup=divider.onpointercancel=null;};
      divider.onpointerup=ended=>{end();apply({layout:resize(saved.layout,at,ratioAt(ended),rules),collapsed:saved.collapsed});};
      // A cancelled drag carries no position to keep: the arrangement returns to what it was.
      divider.onpointercancel=()=>{end();apply(saved,false);};
    };
    divider.onkeydown=event=>{
      const step=({ArrowLeft:-.05,ArrowUp:-.05,ArrowRight:.05,ArrowDown:.05} as Record<string,number>)[event.key];
      if(step===undefined)return;event.preventDefault();apply({layout:resize(saved.layout,at,ratioOf(at)+step,rules),collapsed:saved.collapsed});
    };
    return node;
  }
  function draw(l:DockLayout,depth:number,count:{groups:number;splits:number}):HTMLElement{
    return l.kind==='group'?drawGroup(l,count.groups++,depth):drawSplit(l,depth,count);
  }
  const ratioOf=(at:number)=>splitsOf(shown.layout)[at].ratio;
  /** Shows a group's selected tab. */
  function mark(g:Group){const away=shown.collapsed.includes(g.active);for(const id of g.tabs){const active=id===g.active,b=buttons.get(id)!;b.setAttribute('aria-selected',String(active));b.tabIndex=active?0:-1;bodies.get(id)!.hidden=!active||away;}}
  /** Whether a side shows only collapsed bars: a collapsed group, or a split of such sides. */
  const barred=(l:DockLayout):boolean=>l.kind==='group'?shown.collapsed.includes(l.active):l.children.every(barred);
  /** Sizes each split's two sides by its ratio; a side of collapsed bars keeps its own size and the other takes the rest. */
  function proportion(){
    splitsOf(shown.layout).forEach((split,at)=>{const drawn=drawnSplits[at],[a,b]=split.children.map(barred);
      drawn.first.style.flex=a?'0 0 auto':`${b?1:split.ratio} 1 0`;drawn.second.style.flex=b?'0 0 auto':`${a?1:1-split.ratio} 1 0`;
      drawn.divider.setAttribute('aria-valuenow',String(Math.round(split.ratio*100)));});
  }
  /** Tells `o.shown` of the tabs that have just come into view. */
  function announce(){
    const now=new Set(groupsOf(shown.layout).filter(g=>!shown.collapsed.includes(g.active)).map(g=>g.active)),fresh=[...now].filter(id=>!visible.has(id));
    visible=now;for(const id of fresh)o.shown?.(id);
  }
  /** Draws `shown` again, keeping focus on what had it. The first drawing announces nothing: the page draws what it shows first itself. */
  function render(again=true){
    const focused=host.contains(document.activeElement)?document.activeElement as HTMLElement:undefined;
    drawnSplits=[];menu.close();stopWaiting();
    host.replaceChildren(draw(shown.layout,0,{groups:0,splits:0}),menu.element);proportion();
    if(focused?.isConnected&&document.activeElement!==focused)focused.focus({preventScroll:true});
    // Every body was moved into the groups just drawn, so every one in view comes into view again.
    if(again)visible=new Set();
    announce();
  }
  /** Shows `next`, which is also kept (and `changed` told) as the arrangement unless the page is narrow or `keep` is false (a divider being dragged). */
  function apply(next:Arrangement,keep=true){
    const same=shape(next)===shape(shown),away=o.tabs.find(t=>t.id===focusedIn);
    shown=next;if(!narrow&&keep){saved=next;o.changed(next);}
    if(!same)render();else{groupsOf(shown.layout).forEach(mark);proportion();announce();}
    // Focus in a body that is put away, or lost from it (a control there disabled while it works), moves to the tab its group now
    // shows, so that it is not lost.
    const lost=document.activeElement===null||document.activeElement===document.body||away?.body.contains(document.activeElement);
    if(away&&away.body.hidden&&lost){const group=groupsOf(shown.layout).find(g=>g.tabs.includes(away.id));if(group)focusWhenEnabled(buttons.get(group.active)!);}
  }
  // The one tab waiting to take focus once the page enables its controls again; a later wait replaces it.
  let waiting:MutationObserver|undefined;
  function stopWaiting(){waiting?.disconnect();waiting=undefined;}
  /** Focuses a tab now, or, while the page has disabled its controls for work in progress, once they are enabled again, if focus is still lost. */
  function focusWhenEnabled(button:HTMLButtonElement){
    stopWaiting();
    if(!button.disabled){button.focus({preventScroll:true});return;}
    const watch=waiting=new MutationObserver(()=>{
      if(button.disabled)return;if(waiting===watch)stopWaiting();
      if(document.activeElement===null||document.activeElement===document.body)button.focus({preventScroll:true});
    });
    watch.observe(button,{attributes:true,attributeFilter:['disabled']});
  }
  function move(tab:string,target:{group:number;edge:DropEdge}){
    if(narrow)return;
    const next=moveArrangement(saved,tab,target,rules);if(next===saved)return;
    apply(next);buttons.get(tab)!.focus({preventScroll:true});
  }
  /** Puts away or brings back the pane showing `tab`; the button that did it, now drawn the other way, keeps focus. */
  function toggle(tab:string){
    apply(toggleCollapsed(narrow?shown:saved,tab,rules));
    host.querySelector<HTMLElement>(`[data-collapse="${tab}"]`)?.focus({preventScroll:true});
  }

  function openMenu(tab:string,at:{x:number;y:number}){
    const button=buttons.get(tab)!,back=()=>button.focus({preventScroll:true});
    // A narrow page shows one group, so its menu says why it offers no moves.
    if(narrow){menu.open(`${label.get(tab)} tab`,[{label:'Arrange groups on a wider page'}],at,back,button);return;}
    const groups=groupsOf(saved.layout),from=groups.findIndex(g=>g.tabs.includes(tab));
    const offer=(text:string,target:{group:number;edge:DropEdge}):MenuItem=>moveTab(saved.layout,tab,target,rules)===saved.layout?{label:text}:{label:text,run:()=>move(tab,target)};
    const others=groups.map((g,index)=>({g,index})).filter(({index})=>index!==from);
    const choose=()=>menu.open('Move to group',others.map(({g,index})=>offer(`Group ${index+1} · ${g.tabs.map(id=>label.get(id)).join(', ')}`,{group:index,edge:'centre'})),at,back,button);
    menu.open(`${label.get(tab)} tab`,[offer('Move to new group right',{group:from,edge:'right'}),offer('Move to new group below',{group:from,edge:'bottom'}),
      others.length?{label:'Move to group…',run:choose}:{label:'Move to group…'}],at,back,button);
  }

  /** Shows a tab, bringing back its pane if it was put away, in the group that holds it. */
  function reveal(id:string){
    const next=showTab(shown,id);
    if(JSON.stringify(next)!==JSON.stringify(shown))apply(next);
  }
  /** Replaces the arrangement with one the caller chose (a reset, a restore); `changed` is not told. */
  function set(next:Arrangement){saved=next;shown=narrow?flat(saved):saved;render();}
  /** Follows a change of the page's width: narrow shows one group, wide again the saved arrangement. */
  function relayout(){
    const now=o.narrow();if(now===narrow)return;
    narrow=now;shown=narrow?flat(saved):saved;render();
  }
  // The body that last held focus, kept when focus is lost from it rather than moved elsewhere.
  let focusedIn:string|undefined;
  const enter=(event:FocusEvent)=>{focusedIn=o.tabs.find(t=>t.body.contains(event.target as Node))?.id;};
  host.addEventListener('focusin',enter);
  const leave=(event:FocusEvent)=>{if(!host.contains(event.target as Node))focusedIn=undefined;};
  document.addEventListener('focusin',leave);
  host.classList.add('tab-dock');render(false);
  return {arrangement:()=>saved,set,reveal,relayout,destroy(){host.removeEventListener('focusin',enter);document.removeEventListener('focusin',leave);stopWaiting();menu.close();host.classList.remove('tab-dock');host.replaceChildren();}};
}
