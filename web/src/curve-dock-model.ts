// The tab dock on Custom curves (custom-curves-workspace design, Workspace layout): the page's views are tabs in groups, and a group can
// be split beside or above another to a depth of two. This module is only the arrangement: which tabs sit in which group, how groups
// split, and how a drop, a saved layout or a narrow screen changes it. Drawing and storage belong to the caller.

export type DockLayout=
  |{kind:'group';tabs:string[];active:string}
  |{kind:'split';direction:'row'|'column';ratio:number;children:[DockLayout,DockLayout]};
type Group=Extract<DockLayout,{kind:'group'}>;
export type DropEdge='centre'|'left'|'right'|'top'|'bottom';

const maxDepth=2;
const ratioRange=[0.2,0.8] as const;

export const defaultLayout=(tabs:readonly string[]):DockLayout=>({kind:'group',tabs:[...tabs],active:'outputs'});

/** The groups in depth-first order: the numbers a drop target uses. */
function groupsOf(l:DockLayout):Group[]{
  return l.kind==='group'?[l]:l.children.flatMap(groupsOf);
}

function depthOf(l:DockLayout,group:Group,depth=0):number{
  if(l===group)return depth;
  if(l.kind==='group')return -1;
  return Math.max(...l.children.map(c=>depthOf(c,group,depth+1)));
}

/** Rebuilds the tree with `change` applied to each node (children first); a group `change` returns null for is dropped. */
function rebuild(l:DockLayout,change:(g:Group)=>DockLayout|null):DockLayout|null{
  if(l.kind==='group')return change(l);
  const [a,b]=l.children.map(c=>rebuild(c,change));
  return a&&b?{...l,children:[a,b]}:a??b;
}

/** Moves a tab onto a group: its centre joins that group, an edge splits the group with the tab alone on that side. */
export function moveTab(l:DockLayout,tab:string,target:{group:number;edge:DropEdge}):DockLayout{
  const groups=groupsOf(l);
  const from=groups.find(g=>g.tabs.includes(tab));
  const to=groups[target.group];
  if(!from||!to)return l;
  const edge=target.edge!=='centre'&&depthOf(l,to)>=maxDepth?'centre':target.edge;
  if(from===to&&(edge==='centre'||from.tabs.length===1))return l;
  const left=from.tabs.filter(t=>t!==tab);
  const at=from.tabs.indexOf(tab);
  const rest:Group={kind:'group',tabs:left,active:from.active===tab?left[at]??left[at-1]??'':from.active};
  const alone:Group={kind:'group',tabs:[tab],active:tab};
  const moved=rebuild(l,g=>{
    const now=g===from?rest:g;
    if(g!==to)return now.tabs.length?now:null;
    if(edge==='centre')return {...now,tabs:[...now.tabs,tab],active:tab};
    const first=edge==='left'||edge==='top';
    return {kind:'split',direction:edge==='left'||edge==='right'?'row':'column',ratio:0.5,children:first?[alone,now]:[now,alone]};
  });
  return moved??l;
}

/** One group with every tab once, in the order the groups showed them, keeping the first group's active tab (a narrow screen). */
export function collapse(l:DockLayout):DockLayout{
  const groups=groupsOf(l);
  return {kind:'group',tabs:groups.flatMap(g=>g.tabs),active:groups[0].active};
}

function parse(saved:unknown,depth:number):DockLayout|null{
  if(typeof saved!=='object'||saved===null)return null;
  const s=saved as Record<string,unknown>;
  if(s.kind==='group'){
    const {tabs,active}=s;
    if(!Array.isArray(tabs)||!tabs.length||!tabs.every(t=>typeof t==='string')||typeof active!=='string'||!tabs.includes(active))return null;
    return {kind:'group',tabs:[...tabs] as string[],active};
  }
  if(s.kind!=='split'||depth>=maxDepth)return null;
  const {direction,ratio,children}=s;
  if((direction!=='row'&&direction!=='column')||typeof ratio!=='number'||!Number.isFinite(ratio))return null;
  if(!Array.isArray(children)||children.length!==2)return null;
  const a=parse(children[0],depth+1);
  const b=parse(children[1],depth+1);
  return a&&b?{kind:'split',direction,ratio:Math.min(ratioRange[1],Math.max(ratioRange[0],ratio)),children:[a,b]}:null;
}

/** A layout saved by an earlier visit (untrusted storage) if it is well formed and holds exactly these tabs once each; else the default. */
export function restore(saved:unknown,tabs:readonly string[]):DockLayout{
  const layout=parse(saved,0);
  if(!layout)return defaultLayout(tabs);
  const held=groupsOf(layout).flatMap(g=>g.tabs);
  const same=held.length===tabs.length&&new Set(held).size===held.length&&held.every(t=>tabs.includes(t));
  return same?layout:defaultLayout(tabs);
}
