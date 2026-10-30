// The dashboard's layout as the server stores it (DashboardLayouts.cs validates it): which widgets, their configuration and where each sits on the
// Board and in the Morning sections. Pure edits only; references to curves and definitions live in `config`, never prices or dates.
export type Arrangement='board'|'morning';
export type Section='weather_events'|'inventories'|'prices_positions'|'missed';
export type Box={x:number;y:number;w:number;h:number};
type Placement={board:Box;morning:{section:Section;order:number}};
export type WidgetContent=
  {kind:'latest_levels';config:{curves:string[]}}|{kind:'price_chart';config:{curve:string;contractId:string}}|{kind:'positions';config:{definitionId:string}};
export type DashboardWidget={id:string}&Placement&WidgetContent;
export type WidgetKind=DashboardWidget['kind'];
export type DashboardLayout={version:1;arrangement:Arrangement;widgets:DashboardWidget[]};

// The Morning sections in the trader's order.
const sections:readonly {section:Section;label:string}[]=[
 {section:'weather_events',label:'Weather & events'},{section:'inventories',label:'Inventories'},
 {section:'prices_positions',label:'Prices & positions'},{section:'missed',label:'Might have missed'}];

// Where a new widget of a kind first appears in Morning; the person can move it.
const homeSections:Record<WidgetKind,Section>={latest_levels:'prices_positions',price_chart:'prices_positions',positions:'prices_positions'};
export const homeSection=(kind:WidgetKind):Section=>homeSections[kind];

// How large a new widget first is on the Board; the person can resize it.
const boardSizes:Record<WidgetKind,Pick<Box,'w'|'h'>>={latest_levels:{w:6,h:4},price_chart:{w:6,h:6},positions:{w:6,h:5}};

const absent=()=>Error('This widget is not on the dashboard. Reload the dashboard.');
function change(layout:DashboardLayout,id:string,edit:(widget:DashboardWidget)=>DashboardWidget):DashboardLayout{
 if(!layout.widgets.some(w=>w.id===id))throw absent();
 return {...layout,widgets:layout.widgets.map(w=>w.id===id?edit(w):w)};
}

// A widget of this content placed after everything on the Board (below the lowest widget, at the left edge) and at the end of its Morning section.
export function newWidget(layout:DashboardLayout,id:string,content:WidgetContent):DashboardWidget{
 const section=homeSection(content.kind);
 return {id,...content,board:{x:0,y:Math.max(0,...layout.widgets.map(w=>w.board.y+w.board.h)),...boardSizes[content.kind]},morning:{section,order:endOfSection(layout,section)}};
}
export function addWidget(layout:DashboardLayout,widget:DashboardWidget):DashboardLayout{
 if(layout.widgets.some(w=>w.id===widget.id))throw Error('This widget is already on the dashboard.');
 return {...layout,widgets:[...layout.widgets,widget]};
}
export function removeWidget(layout:DashboardLayout,id:string):DashboardLayout{
 if(!layout.widgets.some(w=>w.id===id))throw absent();
 return {...layout,widgets:layout.widgets.filter(w=>w.id!==id)};
}
export const moveOnBoard=(layout:DashboardLayout,id:string,board:Box)=>change(layout,id,w=>({...w,board}));
export const moveToSection=(layout:DashboardLayout,id:string,section:Section,order:number)=>change(layout,id,w=>({...w,morning:{section,order}}));
export const arrange=(layout:DashboardLayout,arrangement:Arrangement):DashboardLayout=>({...layout,arrangement});

export function morningSections(layout:DashboardLayout){
 return sections.map(({section,label})=>({section,label,widgets:layout.widgets.filter(w=>w.morning.section===section).sort((a,b)=>a.morning.order-b.morning.order)}));
}

// The order that puts a widget after every widget already in a section.
export const endOfSection=(layout:DashboardLayout,section:Section)=>Math.max(-1,...layout.widgets.filter(w=>w.morning.section===section).map(w=>w.morning.order))+1;
