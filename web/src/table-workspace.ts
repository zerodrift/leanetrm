import {renderDataGrid} from './data-grid.js';

type Choice=readonly [string,string];
// lead: the consumer's own content between the toolbar and the table, across the workspace's whole width, such as summary tiles.
// overview: more of it, in the column of the table and above its heading, such as charts. aside: a panel the consumer docks beside the overview and the table.
// rowGroups: how trade rows can be grouped; the Group by control, in the heading row above the table, also offers None, an empty value.
type WorkspaceOptions={
  key:string;rowMode:string;rowLabel:string;dimensions:readonly Choice[];measures:readonly Choice[];
  scopes:readonly Choice[];actions:readonly Choice[];pivotHelp:string;onApply:()=>void;lead?:HTMLElement;overview?:HTMLElement;aside?:HTMLElement;rowGroups?:readonly Choice[];
};

/** Shared presentation shell. Consumers supply authorized rows and server pivot operations. */
export function createTableWorkspace(host:HTMLElement,options:WorkspaceOptions){
  if(!/^[a-z][a-z0-9-]*$/.test(options.key))throw Error('Invalid table key.');
  const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string,id?:string)=>{
    const result=document.createElement(tag);if(text!==undefined)result.textContent=text;if(id)result.id=options.key+'-'+id;return result;
  };
  const button=(label:string,id:string)=>{const result=node('button',label,id);result.type='button';result.className='secondary';return result;};
  function icon(path:string){const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 24 24');svg.setAttribute('aria-hidden','true');const shape=document.createElementNS(svg.namespaceURI,'path');shape.setAttribute('d',path);svg.append(shape);return svg;}
  const icons:Record<string,string>={settings:'M5 3h12l3 3v15H4V3h1m3 0v6h8V3M8 21v-8h8v8',columns:'M3 4h18v16H3zM9 4v16m6-16v16',scope:'M4 5h16M7 12h10m-7 7h4',pivot:'M4 4h16v16H4zM4 10h16M10 4v16m0-6h10', 'chart-settings':'M4 3v17h17M8 16v-5m5 5V7m5 9V4'};
  const shell=node('section');shell.className='table-workspace';shell.setAttribute('aria-label',options.rowLabel);
  const toolbar=node('div');toolbar.className='table-workspace-toolbar';
  const saved=node('select',undefined,'saved');saved.setAttribute('aria-label','Saved table view');saved.append(new Option('Current view · not saved',''));toolbar.append(saved);
  const menus:HTMLDetailsElement[]=[];
  function menu(title:string,id:string){
    const details=node('details',undefined,id),summary=node('summary',title),panel=node('div');if(icons[id])summary.prepend(icon(icons[id]));panel.className='table-menu-panel';details.className='table-menu';details.append(summary,panel);toolbar.append(details);menus.push(details);
    details.addEventListener('toggle',()=>{if(details.open)for(const other of menus)if(other!==details)other.open=false;});return panel;
  }
  function labeled(parent:HTMLElement,label:string,control:HTMLElement){const wrap=node('div'),caption=node('label',label);if(control.id)caption.htmlFor=control.id;wrap.append(caption,control);parent.append(wrap);}
  const savePanel=menu('Save view','settings'),name=node('input',undefined,'name');name.maxLength=60;name.placeholder='Name this view';labeled(savePanel,'View name',name);
  const saveActions=node('div');saveActions.className='table-menu-actions';saveActions.append(button('Save view','save'),button('Delete view','delete'),button('Reload views','views-reload'));savePanel.append(saveActions);
  const note=node('p','Named views are saved to your account. Reload views restores the selected saved layout. Dates and market inputs are selected separately.','view-note');note.className='field-help';savePanel.append(note);
  const columnsPanel=menu('Columns','columns'),columnChoices=node('div',undefined,'column-choices');columnChoices.className='table-field-choices';columnsPanel.append(columnChoices);
  const columnNote=node('p','Filters on hidden columns stay active. Pivot units and currencies stay visible.');columnNote.className='field-help';columnsPanel.append(columnNote);
  const scopePanel=menu('Book / portfolio','scope');
  for(const [key,label] of options.scopes){const select=node('select',undefined,key);select.multiple=true;select.size=4;labeled(scopePanel,label+' · none selected means all',select);}
  const scopeApply=button('Apply filters','scope-apply');scopeApply.onclick=()=>{options.onApply();};scopePanel.append(scopeApply);
  const pivotPanel=menu('Pivot','pivot');const builderHeading=node('div');builderHeading.className='pivot-builder-heading';const introduction=node('div');introduction.append(node('strong','Build a pivot'),node('p','Arrange your rows, columns and values. Drag dimensions between axes or use the arrow controls.'));builderHeading.append(introduction);pivotPanel.append(builderHeading);
  const mode=node('select',undefined,'mode');mode.append(new Option(options.rowLabel,options.rowMode),new Option('Pivot table','pivot'));labeled(pivotPanel,'Display',mode);
  const fields=node('div',undefined,'pivot-fields');fields.className='pivot-builder';fields.hidden=true;
  function ticks(parent:HTMLElement,id:string,label:string,choices:readonly Choice[]){const fieldset=node('fieldset'),legend=node('legend',label),list=node('div',undefined,id);list.className='table-field-choices';fieldset.append(legend,list);parent.append(fieldset);fillChoices(list,choices,[],()=>{});}
  ticks(fields,'dimensions','Rows',options.dimensions);ticks(fields,'split-dimensions','Columns',options.dimensions);ticks(fields,'measures','Measures',options.measures);
  const swapAxes=button('Swap rows and columns','swap-axes');swapAxes.className='text-button';swapAxes.onclick=()=>{const selected=(id:string)=>Array.from(fields.querySelectorAll<HTMLInputElement>('#'+options.key+'-'+id+' input:checked'),n=>n.value);const rows=selected('dimensions'),columns=selected('split-dimensions');setPivot([...columns,...rows],selected('measures'),rows);};builderHeading.append(swapAxes);
  const rules=node('details');rules.className='pivot-calculation-help';rules.append(node('summary','Units and calculation rules'),node('p',options.pivotHelp));fields.append(rules);pivotPanel.append(fields);
  const apply=button('Apply view','apply');apply.onclick=()=>options.onApply();pivotPanel.append(apply);
  const chartPanel=menu('Charts','chart-settings'),show=node('input',undefined,'show-chart');show.type='checkbox';labeled(chartPanel,'Show visual analysis',show);
  const chartMeasure=node('select',undefined,'chart-measure');chartMeasure.append(...options.measures.map(([key,label])=>new Option(label,key)));labeled(chartPanel,'Measure',chartMeasure);
  const chartKind=node('select',undefined,'chart-kind');chartKind.append(new Option('Grouped bars','bar'),new Option('Line plot','line'),new Option('Heatmap','heatmap'));labeled(chartPanel,'Plot style',chartKind);
  const chartHelp=node('p','Charts follow the selected pivot axes. Trade rows show dated contracts by book and market. Units and currencies have separate panels; table row filters do not change the chart.');chartHelp.className='field-help';chartPanel.append(chartHelp);
  const chartApply=button('Apply chart','chart-apply');chartApply.onclick=()=>options.onApply();chartPanel.append(chartApply);
  const actions=node('div');actions.className='table-workspace-actions';for(const [id,label] of options.actions)actions.append(button(label,id));toolbar.append(actions);
  // One line above the rows: what they are, how they are grouped, and the grid's search and row count.
  const heading=node('div');heading.className='table-workspace-heading';
  const title=node('h3',options.rowLabel,'table-title');title.className='table-workspace-title';heading.append(title);
  if(options.rowGroups){
    const wrap=node('label',undefined,'row-group-control'),caption=node('span','Group by'),select=node('select',undefined,'row-group');wrap.className='table-group-by';
    select.append(...options.rowGroups.map(([key,label])=>new Option(label,key)),new Option('None',''));wrap.append(caption,select);heading.append(wrap);
  }
  const filters=node('div',undefined,'filters');filters.className='table-workspace-filters';heading.append(filters);
  const definition=node('div',undefined,'definition');definition.className='table-view-definition';definition.hidden=true;const content=node('div',undefined,'rows');content.className='table-wrap';
  const main=node('div');main.className='table-workspace-main';main.append(...(options.overview?[options.overview]:[]),heading,definition,content);
  const stage=node('div');stage.className='table-workspace-stage';stage.append(main,...(options.aside?[options.aside]:[]));
  shell.append(toolbar,...(options.lead?[options.lead]:[]),stage);host.replaceChildren(shell);
  shell.addEventListener('keydown',event=>{if(event.key==='Escape'){const open=menus.find(m=>m.open);if(open){open.open=false;open.querySelector('summary')?.focus();}}});
  function fillChoices(target:HTMLElement,choices:readonly Choice[],selected:string[],change:(values:string[])=>void,required:string[]=[]){
    const order=[...choices].sort(([a],[b])=>{const ai=selected.indexOf(a),bi=selected.indexOf(b);return (ai<0?choices.length:ai)-(bi<0?choices.length:bi);});
    const axis=target.id===options.key+'-dimensions'?'rows':target.id===options.key+'-split-dimensions'?'columns':undefined;
    function move(key:string,destination:HTMLElement,before?:HTMLElement){
      for(const list of [fields.querySelector('#'+options.key+'-dimensions')!,fields.querySelector('#'+options.key+'-split-dimensions')!]){
        const input=Array.from(list.querySelectorAll<HTMLInputElement>('input')).find(n=>n.value===key)!;input.checked=list===destination;
        if(list===destination){destination.insertBefore(input.parentElement!,before??null);input.focus();}
      }
    }
    target.replaceChildren(...order.map(([key,label])=>{
      const row=node('label'),input=node('input');input.type='checkbox';input.value=key;input.checked=selected.includes(key)||required.includes(key);input.disabled=required.includes(key);row.title=label;
      input.onchange=()=>{if(axis&&input.checked){const other=fields.querySelector('#'+options.key+(axis==='rows'?'-split-dimensions':'-dimensions'))!;const match=Array.from(other.querySelectorAll<HTMLInputElement>('input')).find(n=>n.value===key);if(match)match.checked=false;}change(Array.from(target.querySelectorAll<HTMLInputElement>('input:checked'),n=>n.value));};row.append(input,document.createTextNode(label));
      if(axis){
        row.draggable=true;row.dataset.field=key;row.ondragstart=event=>{event.dataTransfer?.setData('text/plain',key);row.classList.add('dragging');};row.ondragend=()=>row.classList.remove('dragging');
        const transfer=button(axis==='rows'?'→':'←','move-'+axis+'-'+key);transfer.className='pivot-move';transfer.setAttribute('aria-label','Move '+label+' to '+(axis==='rows'?'columns':'rows'));transfer.onclick=event=>{event.preventDefault();const destination=fields.querySelector('#'+options.key+(axis==='rows'?'-split-dimensions':'-dimensions')) as HTMLElement;move(key,destination);};row.append(transfer);
      }
      return row;
    }));
    if(axis){target.ondragover=event=>{event.preventDefault();};target.ondrop=event=>{event.preventDefault();const key=event.dataTransfer?.getData('text/plain');if(key&&options.dimensions.some(d=>d[0]===key)){const before=(event.target as HTMLElement).closest<HTMLElement>('label');move(key,target,before?.parentElement===target?before:undefined);}};}
  }
  function setPivot(dimensions:string[],measures:string[],splitBy:string[]=[]){fillChoices(fields.querySelector('#'+options.key+'-dimensions')!,options.dimensions,dimensions.filter(d=>!splitBy.includes(d)),()=>{});fillChoices(fields.querySelector('#'+options.key+'-split-dimensions')!,options.dimensions,splitBy,()=>{});fillChoices(fields.querySelector('#'+options.key+'-measures')!,options.measures,measures,()=>{});}
  let grid:ReturnType<typeof renderDataGrid>|undefined;
  return {
    clear(){grid?.destroy();grid=undefined;content.replaceChildren();definition.replaceChildren();definition.hidden=true;},
    describe(axes:{label:string;values:string[]}[]){definition.replaceChildren(...axes.map(axis=>{const group=node('div'),label=node('span',axis.label);group.append(label,...(axis.values.length?axis.values:['None']).map(value=>node('strong',value)));return group;}));definition.hidden=!axes.length;},
    busy(value:boolean){content.inert=value;},
    render(...[key,scope,headers,rows,numeric,isBusy,gridOptions]:Parameters<typeof renderDataGrid> extends [HTMLElement,...infer Rest]?Rest:never){grid?.destroy();grid=renderDataGrid(content,key,scope,headers,rows,numeric,isBusy,{...gridOptions,toolbarHost:filters});return grid;},
    columns(choices:readonly Choice[],selected:string[],change:(values:string[])=>void,required:string[]=[]){fillChoices(columnChoices,choices,selected,change,required);},
    pivot:setPivot,
    closeMenus(){for(const menu of menus)menu.open=false;},
  };
}
