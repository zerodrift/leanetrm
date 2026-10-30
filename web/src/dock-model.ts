// The tab dock shared by Curve builder (markets-desk.md) and the Blotter (blotter-workbench.md): a page's views are tabs in groups,
// and a group can be split beside or above another to a depth the caller's rules set. This module is only the arrangement: which tabs
// sit in which group, which are collapsed, how groups split, and how a drop, a saved arrangement or a narrow screen changes it.
// Drawing and storage belong to the caller.

export type DockLayout=
  |{kind:'group';tabs:string[];active:string}
  |{kind:'split';direction:'row'|'column';ratio:number;children:[DockLayout,DockLayout]};
export type Group=Extract<DockLayout,{kind:'group'}>;
export type Split=Extract<DockLayout,{kind:'split'}>;
export type DropEdge='centre'|'left'|'right'|'top'|'bottom';

/** The layout and the tabs put away to a one-line bar: what a viewer arranges and a caller stores. */
export type Arrangement={layout:DockLayout;collapsed:string[]};
/** `fixed` tabs can never be collapsed; splits nest at most `maxDepth` levels; a divider keeps a split's ratio within `ratioRange`. */
export type DockRules={maxDepth:number;fixed:readonly string[];ratioRange:readonly [number,number]};

export const defaultLayout=(tabs:readonly string[],active:string):DockLayout=>({kind:'group',tabs:[...tabs],active});

/** The groups in depth-first order: the numbers a drop target uses. */
export function groupsOf(l:DockLayout):Group[]{
  return l.kind==='group'?[l]:l.children.flatMap(groupsOf);
}

/** The splits in depth-first order, each before its children: the numbers a divider uses. */
export function splitsOf(l:DockLayout):Split[]{
  return l.kind==='group'?[]:[l,...l.children.flatMap(splitsOf)];
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
export function moveTab(l:DockLayout,tab:string,target:{group:number;edge:DropEdge},rules:DockRules):DockLayout{
  const groups=groupsOf(l);
  const from=groups.find(g=>g.tabs.includes(tab));
  const to=groups[target.group];
  if(!from||!to)return l;
  const edge=target.edge!=='centre'&&depthOf(l,to)>=rules.maxDepth?'centre':target.edge;
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

/** Shows `tab` in the group that holds it. */
export function select(l:DockLayout,tab:string):DockLayout{
  if(!groupsOf(l).some(g=>g.tabs.includes(tab)))return l;
  return rebuild(l,g=>g.tabs.includes(tab)?{...g,active:tab}:g)!;
}

const clamp=(ratio:number,rules:DockRules)=>Math.min(rules.ratioRange[1],Math.max(rules.ratioRange[0],ratio));

/** Sets the share of the first child of split `index` (`splitsOf` numbering). */
export function resize(l:DockLayout,index:number,ratio:number,rules:DockRules):DockLayout{
  const target=splitsOf(l)[index];
  if(!target||!Number.isFinite(ratio))return l;
  const walk=(n:DockLayout):DockLayout=>n.kind==='group'?n:{...n,ratio:n===target?clamp(ratio,rules):n.ratio,children:[walk(n.children[0]),walk(n.children[1])]};
  return walk(l);
}

/** One group with every tab once, in the order the groups showed them, keeping the first group's active tab (a narrow screen). */
export function collapse(l:DockLayout):DockLayout{
  const groups=groupsOf(l);
  return {kind:'group',tabs:groups.flatMap(g=>g.tabs),active:groups[0].active};
}

function parse(saved:unknown,depth:number,rules:DockRules):DockLayout|null{
  if(typeof saved!=='object'||saved===null)return null;
  const s=saved as Record<string,unknown>;
  if(s.kind==='group'){
    const {tabs,active}=s;
    if(!Array.isArray(tabs)||!tabs.length||!tabs.every(t=>typeof t==='string')||typeof active!=='string'||!tabs.includes(active))return null;
    return {kind:'group',tabs:[...tabs] as string[],active};
  }
  if(s.kind!=='split'||depth>=rules.maxDepth)return null;
  const {direction,ratio,children}=s;
  if((direction!=='row'&&direction!=='column')||typeof ratio!=='number'||!Number.isFinite(ratio))return null;
  if(!Array.isArray(children)||children.length!==2)return null;
  const a=parse(children[0],depth+1,rules);
  const b=parse(children[1],depth+1,rules);
  return a&&b?{kind:'split',direction,ratio:clamp(ratio,rules),children:[a,b]}:null;
}

/** `a` with `tab` collapsed or expanded again; a fixed tab (the Blotter's positions grid) never collapses. */
export function toggleCollapsed(a:Arrangement,tab:string,rules:DockRules):Arrangement{
  if(!groupsOf(a.layout).some(g=>g.tabs.includes(tab)))throw new Error(`Unknown tab ${tab}.`);
  if(rules.fixed.includes(tab))throw new Error(`The ${tab} tab cannot be collapsed.`);
  return {layout:a.layout,collapsed:a.collapsed.includes(tab)?a.collapsed.filter(t=>t!==tab):[...a.collapsed,tab]};
}

/** Collapse belongs to what a group shows: `dropped` tabs (those a group stops showing, and a tab being shown) stay collapsed no longer. */
const without=(a:Arrangement,dropped:readonly string[]):string[]=>a.collapsed.filter(t=>!dropped.includes(t));

/** Shows `tab` in its group, expanded. The group's other tabs lose any collapse, so choosing one later shows it open. */
export function showTab(a:Arrangement,tab:string):Arrangement{
  const group=groupsOf(a.layout).find(g=>g.tabs.includes(tab));
  return group?{layout:select(a.layout,tab),collapsed:without(a,group.tabs)}:a;
}

/** `moveTab` on an arrangement: the moved tab arrives expanded, the group it leaves (which shows a neighbour now) and, for a centre drop,
 *  the group it joins (which shows the moved tab) keep no collapse. */
export function moveArrangement(a:Arrangement,tab:string,target:{group:number;edge:DropEdge},rules:DockRules):Arrangement{
  const layout=moveTab(a.layout,tab,target,rules);
  if(layout===a.layout)return a;
  const groups=groupsOf(a.layout),from=groups.find(g=>g.tabs.includes(tab))!,to=groups[target.group];
  return {layout,collapsed:without(a,[...from.tabs,...(target.edge==='centre'?to.tabs:[])])};
}

/** An arrangement saved by an earlier visit (untrusted storage) if it is well formed, holds exactly these tabs once each and collapses
 *  none of the fixed ones; else `fallback`. */
export function restoreArrangement(saved:unknown,tabs:readonly string[],rules:DockRules,fallback:Arrangement):Arrangement{
  if(typeof saved!=='object'||saved===null)return fallback;
  const {layout:raw,collapsed}=saved as Record<string,unknown>;
  const layout=parse(raw,0,rules);
  if(!layout||!Array.isArray(collapsed))return fallback;
  const held=groupsOf(layout).flatMap(g=>g.tabs);
  const same=held.length===tabs.length&&new Set(held).size===held.length&&held.every(t=>tabs.includes(t));
  const shelved=collapsed.every(t=>typeof t==='string'&&tabs.includes(t)&&!rules.fixed.includes(t))&&new Set(collapsed).size===collapsed.length;
  return same&&shelved?{layout,collapsed:collapsed as string[]}:fallback;
}
