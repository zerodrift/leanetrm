import {PendingCommand} from './pending-command.js';
import {setupAddWidget} from './dashboard-add.js';
import {chartBody} from './dashboard-chart.js';
import {levelsBody,refusalNote,type DashboardMarket} from './dashboard-levels.js';
import {addWidget,newWidget,arrange,removeWidget,moveToSection,morningSections,endOfSection,type Arrangement,type DashboardLayout,type DashboardWidget,type Section,type WidgetKind} from './dashboard-layout.js';

type Stored={version:string|null;layout:DashboardLayout|null};
const kindNames:Record<WidgetKind,string>={latest_levels:'Latest levels',price_chart:'Price chart',positions:'Positions by month'};
const knownRejections=new Set(['dashboard_layout','dashboard_layout_conflict','dashboard_request_conflict','access_denied','request_format','request_too_large','request_token']);
// An account that has saved no dashboard starts from an empty Board.
const empty:DashboardLayout={version:1,arrangement:'board',widgets:[]};

function element<K extends keyof HTMLElementTagNameMap>(tag:K,className='',text=''):HTMLElementTagNameMap[K]{
  const node=document.createElement(tag);node.className=className;node.textContent=text;return node;
}

export function setupDashboard(options:{api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;historical:()=>boolean;market:()=>DashboardMarket}){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const pending=new PendingCommand(code=>!!code&&knownRejections.has(code));
  let version:string|null=null,layout:DashboardLayout|undefined,conflict=false,releases:(()=>void)[]=[];

  // The body of one widget. Each widget kind renders here, reading the market now on screen; a body that holds something to release says how.
  function renderWidget(widget:DashboardWidget):HTMLElement {
    switch(widget.kind){
      case 'latest_levels':return levelsBody(widget.config,options.market(),options.api);
      case 'price_chart':{const {body,dispose}=chartBody(widget.config,options.market(),options.api);releases.push(dispose);return body;}
      case 'positions':return element('p','muted',`${kindNames[widget.kind]} has no display yet.`);
    }
  }

  // A widget that throws shows its own refusal in place of its body; the other widgets and the page are unaffected.
  function widgetElement(widget:DashboardWidget):HTMLElement {
    const card=element('article','card dashboard-widget'),title=element('h3','',kindNames[widget.kind]),actions=element('div','dashboard-widget-actions');
    const remove=element('button','secondary dashboard-edit','Remove');remove.type='button';remove.setAttribute('aria-label',`Remove ${kindNames[widget.kind]}`);
    remove.onclick=()=>void options.act(()=>save(removeWidget(layout!,widget.id)));
    if(layout!.arrangement==='morning'){
      const section=element('select','dashboard-edit');section.setAttribute('aria-label',`${kindNames[widget.kind]} section`);
      for(const {section:value,label} of morningSections(layout!))section.add(new Option(label,value,false,value===widget.morning.section));
      section.onchange=()=>void options.act(()=>save(moveToSection(layout!,widget.id,section.value as Section,endOfSection(layout!,section.value as Section))));
      actions.append(section);
    }
    actions.append(remove);
    const header=element('div','card-heading');header.append(title,actions);card.append(header);
    try{card.append(renderWidget(widget));}
    catch(error){card.append(refusalNote(error));}
    return card;
  }
  function render(){
    const host=get('dashboard-widgets');
    for(const release of releases)release();
    releases=[];
    get('dashboard-conflict').hidden=!conflict;
    if(!layout){host.replaceChildren();return;}
    for(const [id,arrangement] of [['dashboard-board','board'],['dashboard-morning','morning']] as const)get(id).setAttribute('aria-pressed',String(layout.arrangement===arrangement));
    if(layout.widgets.length===0){host.replaceChildren(element('p','muted','No widgets have been added to this dashboard.'));return;}
    if(layout.arrangement==='board'){
      const grid=element('div','dashboard-board');
      for(const widget of layout.widgets){
        const card=widgetElement(widget),{x,y,w,h}=widget.board;
        card.style.gridColumn=`${x+1} / span ${w}`;card.style.gridRow=`${y+1} / span ${h}`;grid.append(card);
      }
      host.replaceChildren(grid);
    }else host.replaceChildren(...morningSections(layout).map(({label,widgets})=>{
      const section=element('section','dashboard-section');
      section.append(element('h3','',label),...(widgets.length?widgets.map(widgetElement):[element('p','muted','Nothing in this section.')]));
      return section;
    }));
    sync();
  }
  function sync(){
    const readOnly=options.historical();
    get('dashboard-scope').textContent=readOnly?'Read only in a historical view. Return to working data to change the dashboard.':'Your dashboard follows you between browsers.';
    for(const control of document.querySelectorAll<HTMLButtonElement|HTMLSelectElement>('#dashboard-page .dashboard-edit'))control.disabled=readOnly||options.busy();
    for(const id of ['dashboard-board','dashboard-morning'])get<HTMLButtonElement>(id).disabled=readOnly||options.busy()||!layout;
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
  async function load(){
    const saved=await options.api<Stored>('dashboard/layout');
    pending.clear();conflict=false;version=saved.version;layout=saved.layout??empty;adding.refresh();render();
  }
  const adding=setupAddWidget(get('dashboard-add'),{market:options.market,api:options.api,
    add:content=>options.act(()=>save(addWidget(layout!,newWidget(layout!,crypto.randomUUID(),content))))});
  const switchTo=(arrangement:Arrangement)=>()=>void options.act(()=>save(arrange(layout!,arrangement)));
  get('dashboard-board').onclick=switchTo('board');
  get('dashboard-morning').onclick=switchTo('morning');
  get('dashboard-reload').onclick=()=>void options.act(load);
  return {
    load,sync,
    // The live-correction refresh (D188): the widgets read again, and the add control offers the refreshed navigation. Only a live context calls it.
    async liveRefresh(){if(layout){adding.refresh();render();}},
    unconfirmed:()=>!!pending.command,
    reset(){for(const release of releases)release();releases=[];pending.clear();version=null;layout=undefined;conflict=false;get('dashboard-widgets').replaceChildren();get('dashboard-conflict').hidden=true;},
  };
}
