// The tab dock on Curve builder (custom-curves-workspace design, Workspace layout): draws a layout of curve-dock-model.ts as tab groups
// side by side or above one another. A tab moves by dragging it onto a group's centre or edge, or from its menu (right-click, the
// context-menu key or Shift+F10); a divider resizes a split. The arrangement is this viewer's presentation state in this browser only.
// A narrow page shows every tab in one group and leaves the saved arrangement for when the page is wide again.
import {type DockLayout,type DropEdge,type Group,type Split,collapse,defaultLayout,groupsOf,moveTab,resize,restore,select,splitsOf} from './curve-dock-model.js';
import {menuKey,placeOf,setupMenu,type MenuItem} from './context-menu.js';
import {tabAfterKey} from './side-panel.js';

type Tab={id:string;label:string;body:HTMLElement};
const edges:readonly DropEdge[]=['top','left','centre','right','bottom'];
/** Splits nest two levels deep (curve-dock-model.ts); a group that deep takes a tab only at its centre. */
const deepest=2;
/** The arrangement without the selected tabs and split ratios: when only those change, the groups are updated where they stand. */
const shape=(l:DockLayout):unknown=>l.kind==='group'?l.tabs:[l.direction,l.children.map(shape)];

/** `shown` hears each tab whose body comes into view, so that what it draws can measure itself there. */
export function setupDock(host:HTMLElement,o:{tabs:readonly Tab[];narrow:()=>boolean;storageKey:string;shown?:(id:string)=>void}){
  const ids=o.tabs.map(t=>t.id),label=new Map(o.tabs.map(t=>[t.id,t.label]));
  let saved=restore(read(),ids),narrow=o.narrow(),shown=narrow?collapse(saved):saved,dragged:string|undefined;
  const menu=setupMenu('dock-menu');
  // Each tab keeps its button and body for the page's life; a new arrangement moves them into new groups.
  const buttons=new Map(o.tabs.map(t=>[t.id,tabButton(t)]));
  const bodies=new Map(o.tabs.map(t=>[t.id,t.body]));
  // The tabs in view (those the page shows first it draws itself), and the drawn splits by their depth-first numbers (splitsOf).
  let visible=new Set(groupsOf(shown).map(g=>g.active));
  let drawnSplits:{first:HTMLElement;second:HTMLElement;divider:HTMLElement}[]=[];

  function read():unknown{try{const raw=localStorage.getItem(o.storageKey);return raw===null?undefined:JSON.parse(raw);}catch{return undefined;}}
  function store(){try{localStorage.setItem(o.storageKey,JSON.stringify(saved));}catch{/* The arrangement lasts this visit. */}}
  const div=(className:string)=>{const node=document.createElement('div');node.className=className;return node;};

  function tabButton(t:Tab){
    const b=document.createElement('button');b.type='button';b.id=`${host.id}-${t.id}`;b.className='dock-tab';b.textContent=t.label;
    b.setAttribute('role','tab');b.setAttribute('aria-haspopup','menu');b.setAttribute('aria-expanded','false');b.draggable=true;
    if(!t.body.id)t.body.id=`${b.id}-panel`;
    b.setAttribute('aria-controls',t.body.id);t.body.setAttribute('role','tabpanel');t.body.setAttribute('aria-labelledby',b.id);
    b.onclick=()=>show(t.id);
    b.onkeydown=event=>{
      if(menuKey(event)){event.preventDefault();openMenu(t.id,placeOf(b));return;}
      const group=groupsOf(shown).find(g=>g.tabs.includes(t.id))!,next=tabAfterKey(group.tabs,t.id,event.key);
      if(next===null)return;
      event.preventDefault();show(next);buttons.get(next)!.focus();
    };
    b.oncontextmenu=event=>{event.preventDefault();openMenu(t.id,{x:event.clientX,y:event.clientY});};
    b.ondragstart=event=>{
      if(narrow){event.preventDefault();return;}
      dragged=t.id;host.dataset.dragging='';
      if(event.dataTransfer){event.dataTransfer.effectAllowed='move';event.dataTransfer.setData('text/plain',t.label);}
    };
    b.ondragend=endDrag;
    return b;
  }
  function endDrag(){dragged=undefined;delete host.dataset.dragging;for(const zone of host.querySelectorAll('.dock-zone.over'))zone.classList.remove('over');}
  /** A place a dragged tab can be dropped: it shows while over it, and a drop moves the tab there. */
  function dropTarget(node:HTMLElement,group:number,edge:DropEdge){
    node.ondragover=event=>{if(dragged===undefined)return;event.preventDefault();if(event.dataTransfer)event.dataTransfer.dropEffect='move';node.classList.add('over');};
    node.ondragleave=()=>node.classList.remove('over');
    node.ondrop=event=>{if(dragged===undefined)return;event.preventDefault();const tab=dragged;endDrag();move(tab,{group,edge});};
  }

  function drawGroup(g:Group,index:number,depth:number){
    const group=div('dock-group'),strip=div('dock-tabs'),panels=div('dock-panels'),zones=div('dock-zones');
    group.dataset.group=String(index);strip.setAttribute('role','tablist');strip.setAttribute('aria-label',`Curve views, group ${index+1}`);
    for(const id of g.tabs){strip.append(buttons.get(id)!);panels.append(bodies.get(id)!);}
    for(const edge of depth>=deepest?['centre' as const]:edges){const zone=div('dock-zone');zone.dataset.edge=edge;dropTarget(zone,index,edge);zones.append(zone);}
    dropTarget(strip,index,'centre');
    group.append(strip,panels,zones);mark(g);return group;
  }
  function drawSplit(l:Split,depth:number,count:{groups:number;splits:number}):HTMLElement{
    const node=div('dock-split'),at=count.splits++;node.dataset.direction=l.direction;
    const [first,second]=l.children.map(child=>draw(child,depth+1,count));
    const divider=div('dock-divider');divider.tabIndex=0;divider.setAttribute('role','separator');divider.setAttribute('aria-label','Resize the groups');
    divider.setAttribute('aria-orientation',l.direction==='row'?'vertical':'horizontal');divider.setAttribute('aria-valuemin','20');divider.setAttribute('aria-valuemax','80');
    const ratioAt=(event:PointerEvent)=>{const box=node.getBoundingClientRect();return l.direction==='row'?(event.clientX-box.left)/box.width:(event.clientY-box.top)/box.height;};
    divider.onpointerdown=event=>{
      if(event.button!==0)return;event.preventDefault();divider.setPointerCapture(event.pointerId);
      divider.onpointermove=moved=>apply(resize(shown,at,ratioAt(moved)),false);
      divider.onpointerup=divider.onpointercancel=ended=>{divider.onpointermove=divider.onpointerup=divider.onpointercancel=null;apply(resize(saved,at,ratioAt(ended)));};
    };
    divider.onkeydown=event=>{
      const step=({ArrowLeft:-.05,ArrowUp:-.05,ArrowRight:.05,ArrowDown:.05} as Record<string,number>)[event.key];
      if(step===undefined)return;event.preventDefault();apply(resize(saved,at,ratioOf(at)+step));
    };
    node.append(first,divider,second);drawnSplits[at]={first,second,divider};return node;
  }
  function draw(l:DockLayout,depth:number,count:{groups:number;splits:number}):HTMLElement{
    return l.kind==='group'?drawGroup(l,count.groups++,depth):drawSplit(l,depth,count);
  }
  const ratioOf=(at:number)=>splitsOf(shown)[at].ratio;
  /** Shows a group's selected tab. */
  function mark(g:Group){for(const id of g.tabs){const active=id===g.active,b=buttons.get(id)!;b.setAttribute('aria-selected',String(active));b.tabIndex=active?0:-1;bodies.get(id)!.hidden=!active;}}
  /** Sizes each split's two sides by its ratio. */
  function proportion(){
    splitsOf(shown).forEach((split,at)=>{const drawn=drawnSplits[at];
      drawn.first.style.flex=`${split.ratio} 1 0`;drawn.second.style.flex=`${1-split.ratio} 1 0`;drawn.divider.setAttribute('aria-valuenow',String(Math.round(split.ratio*100)));});
  }
  /** Tells `o.shown` of the tabs that have just come into view. */
  function announce(){
    const now=new Set(groupsOf(shown).map(g=>g.active)),fresh=[...now].filter(id=>!visible.has(id));
    visible=now;for(const id of fresh)o.shown?.(id);
  }
  /** Draws `shown` again, keeping focus on what had it. */
  function render(){
    const focused=host.contains(document.activeElement)?document.activeElement as HTMLElement:undefined;
    drawnSplits=[];menu.close();
    host.replaceChildren(draw(shown,0,{groups:0,splits:0}),menu.element);proportion();
    if(focused?.isConnected&&document.activeElement!==focused)focused.focus({preventScroll:true});
    announce();
  }
  /** Shows `next`, which is also kept as the arrangement unless the page is narrow or `keep` is false (a divider being dragged). */
  function apply(next:DockLayout,keep=true){
    const same=JSON.stringify(shape(next))===JSON.stringify(shape(shown));
    shown=next;if(!narrow&&keep){saved=next;store();}
    if(!same){render();return;}
    groupsOf(shown).forEach(mark);proportion();announce();
  }
  function move(tab:string,target:{group:number;edge:DropEdge}){
    if(narrow)return;
    const next=moveTab(saved,tab,target);if(next===saved)return;
    apply(next);buttons.get(tab)!.focus({preventScroll:true});
  }

  function openMenu(tab:string,at:{x:number;y:number}){
    if(narrow)return;
    const groups=groupsOf(saved),from=groups.findIndex(g=>g.tabs.includes(tab)),button=buttons.get(tab)!,back=()=>button.focus({preventScroll:true});
    const offer=(text:string,target:{group:number;edge:DropEdge}):MenuItem=>moveTab(saved,tab,target)===saved?{label:text}:{label:text,run:()=>move(tab,target)};
    const others=groups.map((g,index)=>({g,index})).filter(({index})=>index!==from);
    const choose=()=>menu.open('Move to group',others.map(({g,index})=>offer(`Group ${index+1} · ${g.tabs.map(id=>label.get(id)).join(', ')}`,{group:index,edge:'centre'})),at,back,button);
    menu.open(`${label.get(tab)} tab`,[offer('Move to new group right',{group:from,edge:'right'}),offer('Move to new group below',{group:from,edge:'bottom'}),
      others.length?{label:'Move to group…',run:choose}:{label:'Move to group…'}],at,back,button);
  }

  /** Shows a tab in the group that holds it. */
  function show(id:string){
    const group=groupsOf(shown).find(g=>g.tabs.includes(id));
    if(group&&group.active!==id)apply(select(shown,id));
  }
  /** Returns to one group of every tab on Outputs, and forgets the saved arrangement. */
  function reset(){
    saved=defaultLayout(ids);shown=narrow?collapse(saved):saved;
    try{localStorage.removeItem(o.storageKey);}catch{/* Nothing was kept. */}
    render();
  }
  /** Follows a change of the page's width: narrow shows one group, wide again the saved arrangement. */
  function relayout(){
    const now=o.narrow();if(now===narrow)return;
    narrow=now;shown=narrow?collapse(saved):saved;render();
  }
  host.classList.add('tab-dock');render();
  return {show,reset,relayout};
}
