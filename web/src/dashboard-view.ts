import {PendingCommand} from './pending-command.js';
import {readCurveShapes} from './curve-shapes.js';
import {setupAddWidget} from './dashboard-add.js';
import {setupPositions} from './dashboard-positions.js';
import {chartBody} from './dashboard-chart.js';
import {levelsBody,refusalNote,type DashboardMarket,type SharedShapes} from './dashboard-levels.js';
import {curvesPerRead} from './market-board.js';
import {addWidget,newWidget,arrange,removeWidget,moveToSection,moveOnBoard,nudged,configuredCurves,morningSections,endOfSection,
  type Arrangement,type DashboardLayout,type DashboardWidget,type Nudge,type Section,type WidgetKind} from './dashboard-layout.js';

type Stored={version:string|null;layout:DashboardLayout|null};
// A drawn widget body, kept by widget id while its content is unchanged: the close and context it was read at, what states its freshness
// again and what releases it.
type Drawn={content:string;readAt:string;body:HTMLElement;freshen?:(market:DashboardMarket)=>void;dispose?:()=>void};
const kindNames:Record<WidgetKind,string>={latest_levels:'Latest levels',price_chart:'Price chart',positions:'Positions by month'};
const knownRejections=new Set(['dashboard_layout','dashboard_layout_conflict','dashboard_request_conflict','access_denied','request_format','request_too_large','request_token']);
// An account that has saved no dashboard starts from an empty Board.
const empty:DashboardLayout={version:1,arrangement:'board',widgets:[]};
// The Board's move and size controls, in the order they are offered.
const nudges:readonly {nudge:Nudge;text:string;label:string}[]=[
  {nudge:'left',text:'←',label:'Move left'},{nudge:'up',text:'↑',label:'Move up'},{nudge:'down',text:'↓',label:'Move down'},{nudge:'right',text:'→',label:'Move right'},
  {nudge:'narrower',text:'Narrower',label:'Narrower'},{nudge:'wider',text:'Wider',label:'Wider'},{nudge:'shorter',text:'Shorter',label:'Shorter'},{nudge:'taller',text:'Taller',label:'Taller'}];

// The close and context a widget's figures are read at; its freshness may be stated again only while the page reads at the same.
const readAt=(market:DashboardMarket)=>JSON.stringify([market.snapshotId,market.navigation.context?.contextId??null,market.historical]);

function element<K extends keyof HTMLElementTagNameMap>(tag:K,className='',text=''):HTMLElementTagNameMap[K]{
  const node=document.createElement(tag);node.className=className;node.textContent=text;return node;
}

export function setupDashboard(options:{api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;historical:()=>boolean;market:()=>DashboardMarket}){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const pending=new PendingCommand(code=>!!code&&knownRejections.has(code));
  const positions=setupPositions(options.api);
  let version:string|null=null,layout:DashboardLayout|undefined,conflict=false;
  // Bodies are drawn once for a market and kept across layout edits; a new close, context or reload draws them again.
  let drawn=new Map<string,Drawn>();
  // The Board controls open for arranging, and the one to focus again once the saved layout is drawn.
  const arranging=new Set<string>();
  let refocus:{id:string;nudge:Nudge}|undefined;

  function forget(){for(const {dispose} of drawn.values())dispose?.();drawn=new Map();}

  // One curve-shapes read, in reads of at most the read's limit, for every Latest levels and Price chart curve not yet drawn.
  function sharedShapes(widgets:readonly DashboardWidget[],market:DashboardMarket):SharedShapes{
    const curves=configuredCurves(widgets),reads=[];
    for(let i=0;i<curves.length;i+=curvesPerRead)
      reads.push(readCurveShapes(options.api,{snapshotId:market.snapshotId,context:market.navigation.context??undefined},curves.slice(i,i+curvesPerRead)));
    return Promise.all(reads).then(maps=>new Map(maps.flatMap(map=>[...map])));
  }

  // The body of one widget, reading the market now on screen; shapes come from the render's shared read.
  function draw(widget:DashboardWidget,market:DashboardMarket,shapes:()=>SharedShapes):Omit<Drawn,'content'|'readAt'> {
    switch(widget.kind){
      case 'latest_levels':return levelsBody(widget.config,market,shapes());
      case 'price_chart':return chartBody(widget.config,market,shapes(),options.api);
      case 'positions':return positions.body(widget.config,market);
    }
  }

  function boardControls(widget:DashboardWidget){
    const name=kindNames[widget.kind],controls=element('details','dashboard-arrange');
    controls.open=arranging.has(widget.id);
    controls.ontoggle=()=>{if(controls.open)arranging.add(widget.id);else arranging.delete(widget.id);};
    const group=element('div','dashboard-arrange-buttons');group.setAttribute('role','group');group.setAttribute('aria-label',`Arrange ${name}`);
    for(const {nudge,text,label} of nudges){
      const button=element('button','secondary dashboard-edit',text),box=nudged(widget.board,nudge);
      button.type='button';button.dataset.widget=widget.id;button.dataset.nudge=nudge;button.setAttribute('aria-label',`${label}: ${name}`);
      if(box)button.onclick=()=>{refocus={id:widget.id,nudge};void options.act(()=>save(moveOnBoard(layout!,widget.id,box)));};
      else button.dataset.bound='true';
      group.append(button);
    }
    controls.append(element('summary','','Arrange'),group);
    return controls;
  }

  // A widget that throws shows its own refusal in place of its body; the other widgets and the page are unaffected.
  function widgetElement(widget:DashboardWidget,body:HTMLElement):HTMLElement {
    const card=element('article','card dashboard-widget'),title=element('h3','',kindNames[widget.kind]),actions=element('div','dashboard-widget-actions');
    const remove=element('button','secondary dashboard-edit','Remove');remove.type='button';remove.setAttribute('aria-label',`Remove ${kindNames[widget.kind]}`);
    remove.onclick=()=>void options.act(()=>save(removeWidget(layout!,widget.id)));
    if(layout!.arrangement==='morning'){
      const section=element('select','dashboard-edit');section.setAttribute('aria-label',`${kindNames[widget.kind]} section`);
      for(const {section:value,label} of morningSections(layout!))section.add(new Option(label,value,false,value===widget.morning.section));
      section.onchange=()=>void options.act(()=>save(moveToSection(layout!,widget.id,section.value as Section,endOfSection(layout!,section.value as Section))));
      actions.append(section);
    }else actions.append(boardControls(widget));
    actions.append(remove);
    const header=element('div','card-heading');header.append(title,actions);card.append(header,body);
    return card;
  }
  function render(){
    const host=get('dashboard-widgets');
    get('dashboard-conflict').hidden=!conflict;
    if(!layout){forget();host.replaceChildren();return;}
    for(const [id,arrangement] of [['dashboard-board','board'],['dashboard-morning','morning']] as const)get(id).setAttribute('aria-pressed',String(layout.arrangement===arrangement));
    // A widget no longer on the layout, or whose content changed, is released; one unchanged keeps its drawn body.
    const contents=new Map(layout.widgets.map(w=>[w.id,JSON.stringify([w.kind,w.config])]));
    for(const [id,{content,dispose}] of drawn)if(contents.get(id)!==content){dispose?.();drawn.delete(id);}
    // The market is read only when a widget is drawn; one that cannot be read refuses each widget drawn.
    const fresh=layout.widgets.filter(w=>!drawn.has(w.id));
    let market:DashboardMarket|undefined,shapes:SharedShapes|undefined;
    const bodies=new Map(layout.widgets.map(widget=>{
      const kept=drawn.get(widget.id);
      if(kept)return [widget.id,kept.body];
      try{
        const now=market??=options.market(),made=draw(widget,now,()=>shapes??=sharedShapes(fresh,now));
        drawn.set(widget.id,{content:contents.get(widget.id)!,readAt:readAt(now),...made});
        return [widget.id,made.body];
      }catch(error){return [widget.id,refusalNote(error)];}
    }));
    if(layout.widgets.length===0){host.replaceChildren(element('p','muted','No widgets have been added to this dashboard.'));return;}
    if(layout.arrangement==='board'){
      const grid=element('div','dashboard-board');
      for(const widget of layout.widgets){
        const card=widgetElement(widget,bodies.get(widget.id)!),{x,y,w,h}=widget.board;
        card.style.gridColumn=`${x+1} / span ${w}`;card.style.gridRow=`${y+1} / span ${h}`;grid.append(card);
      }
      host.replaceChildren(grid);
    }else host.replaceChildren(...morningSections(layout).map(({label,widgets})=>{
      const section=element('section','dashboard-section');
      section.append(element('h3','',label),...(widgets.length?widgets.map(w=>widgetElement(w,bodies.get(w.id)!)):[element('p','muted','Nothing in this section.')]));
      return section;
    }));
    sync();
  }
  function sync(){
    const readOnly=options.historical();
    get('dashboard-scope').textContent=readOnly?'Read only in a historical view. Return to working data to change the dashboard.':'Your dashboard follows you between browsers.';
    for(const control of document.querySelectorAll<HTMLButtonElement|HTMLSelectElement>('#dashboard-page .dashboard-edit'))control.disabled=readOnly||options.busy()||control.dataset.bound==='true';
    for(const id of ['dashboard-board','dashboard-morning'])get<HTMLButtonElement>(id).disabled=readOnly||options.busy()||!layout;
    // A Board control keeps the keyboard focus across the save it made, once the controls can be used again.
    const target=refocus&&document.querySelector<HTMLButtonElement>(`#dashboard-widgets button[data-widget="${refocus.id}"][data-nudge="${refocus.nudge}"]`);
    if(target&&!target.disabled){target.focus();refocus=undefined;}
  }
  async function send(){
    const command=pending.command!,previouslyUncertain=pending.sending();
    try{
      const saved=await options.api<{version:string;layout:DashboardLayout}>(command.path,{...command.body,requestId:command.operationId});
      ({version,layout}=saved);pending.clear();conflict=false;
    }catch(error){
      pending.failed((error as {code?:string}).code,previouslyUncertain);
      if((error as {code?:string}).code!=='dashboard_layout_conflict')throw error;
      conflict=true;
    }
    render();
  }
  async function save(next:DashboardLayout){pending.begin('dashboard/layout',{layout:next,expectedVersion:version});await send();}
  // The widgets drawn before are released and cleared before the layout is read, so a read that fails leaves none of their figures on the page.
  async function load(){
    forget();get('dashboard-widgets').replaceChildren();
    const saved=await options.api<Stored>('dashboard/layout');
    pending.clear();conflict=false;version=saved.version;layout=saved.layout??empty;adding.refresh();render();
  }
  const adding=setupAddWidget(get('dashboard-add'),{market:options.market,api:options.api,positions:positions.configurator(options.market),
    add:content=>options.act(()=>save(addWidget(layout!,newWidget(layout!,crypto.randomUUID(),content))))});
  const switchTo=(arrangement:Arrangement)=>()=>void options.act(()=>save(arrange(layout!,arrangement)));
  get('dashboard-board').onclick=switchTo('board');
  get('dashboard-morning').onclick=switchTo('morning');
  get('dashboard-reload').onclick=()=>void options.act(load);
  return {
    load,sync,
    // The live-correction refresh (D188): the widgets read again, and the add control offers the refreshed navigation. Only a live context calls it.
    async liveRefresh(){if(layout){forget();adding.refresh();render();}},
    // The close's badge changed after the widgets were drawn: each close footer read at the close and context now on screen states its
    // freshness again, with no read. A widget read at another close or context, or with no close to read at, refuses and is drawn again by
    // the next render.
    closeChanged(){
      let market:DashboardMarket;
      try{market=options.market();}
      catch(error){for(const {body} of drawn.values())body.replaceChildren(refusalNote(error));forget();return;}
      const now=readAt(market);
      for(const [id,{readAt:at,body,freshen,dispose}] of drawn){
        if(at!==now){body.replaceChildren(refusalNote(Error('This widget was read at another close or context. Reload the dashboard.')));dispose?.();drawn.delete(id);continue;}
        try{freshen?.(market);}catch(error){body.replaceChildren(refusalNote(error));}
      }
    },
    leave(){forget();get('dashboard-widgets').replaceChildren();},
    unconfirmed:()=>!!pending.command,
    reset(){forget();positions.reset();pending.clear();arranging.clear();refocus=undefined;version=null;layout=undefined;conflict=false;get('dashboard-widgets').replaceChildren();get('dashboard-conflict').hidden=true;},
  };
}
