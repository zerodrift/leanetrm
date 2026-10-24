import {TabulatorFull} from './vendor/tabulator.js';
import {compareDecimalStrings} from './analytics-model.js';
import {matchesTableRow,columnValues,validNumericFilter} from './regular-table.js';
import {publicText} from './market-view.js';
export type GridLayout={order:string[];widths:Record<string,number>;sort:{key:string;direction:'asc'|'desc'}[]};
export const emptyGridLayout=():GridLayout=>({order:[],widths:{},sort:[]});
// cells: rich content drawn for a cell instead of its display text; sorting and filtering still use the row's exact strings.
// searchText: each source row's words that its rich cells draw, which the row search reads beside the row's exact strings.
// visual: columns that only draw, such as a sparkline, and are neither sorted nor filtered.
// group: rows under headers, in the order given: each source row's group key and the header drawn for each key.
// stretch: the last column fills the grid's remaining width.
type GridOptions={displayRows?:string[][];cellTitles?:string[][];visible?:boolean[];initial?:{query:string;columns:(string|null)[]};retainMissingFilters?:boolean;keys:string[];columnGroups?:Record<string,{key:string;title:string;measureTitle:string}>;layout:GridLayout;onLayout:(value:GridLayout)=>void;onReady?:()=>void;onRowOpen?:(index:number)=>void;
  cells?:(index:number,column:number)=>Node|null;searchText?:string[];visual?:number[];group?:{keys:string[];order:string[];header:(key:string,count:number)=>HTMLElement};rowHeight?:number;stretch?:boolean};
export function renderDataGrid(host:HTMLElement,key:string,scope:string,headers:string[],rows:(string|HTMLElement)[][],numeric:number[],isBusy:()=>boolean,options:GridOptions){
  const el=<K extends keyof HTMLElementTagNameMap>(tag:K,value?:string)=>{const n=document.createElement(tag);if(value!==undefined)n.textContent=publicText(value);return n;};
  const values=rows.map(row=>row.map(cell=>publicText(typeof cell==='string'?cell:cell.textContent??'')));
  const state={query:options.initial?.query??'',columns:headers.map((_,i)=>options.initial?.columns[i]??null)};
  const toolbar=el('div');toolbar.className='table-filters';
  const search=el('input');search.type='search';search.placeholder='Filter rows…';search.className='table-search';search.value=state.query;search.maxLength=200;search.setAttribute('aria-label','Filter table rows');
  const toggle=el('button','Column filters');toggle.type='button';toggle.className='secondary column-filter-toggle';toggle.setAttribute('aria-expanded','false');
  const clear=el('button','Clear filters');clear.type='button';clear.className='text-button table-filter-clear';
  const count=el('span');count.className='table-row-count';count.setAttribute('aria-live','polite');toolbar.append(search,toggle,clear,count);
  const filterPanel=el('div');filterPanel.className='grid-column-filters';filterPanel.hidden=true;filterPanel.id='column-filters-'+key;toggle.setAttribute('aria-controls',filterPanel.id);
  const selects:(HTMLSelectElement|HTMLInputElement)[]=[];
  headers.forEach((header,i)=>{
    if(options.visual?.includes(i))return;
    if(numeric.includes(i)){
      const label=el('label',header+(options.visible?.[i]===false?' (hidden)':'')),input=el('input');input.type='text';input.maxLength=200;input.placeholder='e.g. >= 100';input.setAttribute('aria-label','Filter '+header);input.title='Use =, !=, <, <=, > or >= with an exact number.';input.value=state.columns[i]??'';
      input.oninput=()=>{if(!current())return;state.columns[i]=input.value.trim()?input.value:null;input.setCustomValidity(validNumericFilter(input.value.trim())?'':'Enter a number with =, !=, <, <=, > or >=.');input.setAttribute('aria-invalid',String(!validNumericFilter(input.value.trim())));apply();};label.append(input);filterPanel.append(label);selects.push(input);return;
    }
    const label=el('label',header+(options.visible?.[i]===false?' (hidden)':'')),select=el('select');select.setAttribute('aria-label','Filter '+header);select.append(new Option('All values',''));
    const choices=columnValues(values.map(row=>row[i]));if(state.columns[i]!==null&&!choices.includes(state.columns[i]!))choices.push(state.columns[i]!);
    choices.forEach((value,index)=>select.append(new Option(value||'(Blank)',String(index))));select.value=state.columns[i]===null?'':String(choices.indexOf(state.columns[i]!));
    select.onchange=()=>{if(!current())return;state.columns[i]=select.value===''?null:choices[Number(select.value)];apply();};label.append(select);filterPanel.append(label);selects.push(select);
  });
  const container=el('div');container.className='standard-data-grid';host.replaceChildren(toolbar,filterPanel,container);
  let alive=true,ready=false;
  const current=()=>alive&&host.isConnected&&container.parentElement===host&&!isBusy();
  // A pointer gesture belongs to one row. Selection, dragging and scrolling
  // must not become an accidental edit when the pointer is released.
  let gesture:{row:Element|null;x:number;y:number;cancelled:boolean}|undefined;
  const pointerDown=(event:PointerEvent)=>{gesture={row:(event.target as Element).closest('.tabulator-row'),x:event.clientX,y:event.clientY,cancelled:event.button!==0};};
  const pointerMove=(event:PointerEvent)=>{if(gesture&&Math.hypot(event.clientX-gesture.x,event.clientY-gesture.y)>5)gesture.cancelled=true;};
  const cancelGesture=()=>{if(gesture)gesture.cancelled=true;};
  container.addEventListener('pointerdown',pointerDown,true);
  document.addEventListener('pointermove',pointerMove,true);
  document.addEventListener('pointercancel',cancelGesture,true);
  document.addEventListener('scroll',cancelGesture,true);
  const field=(i:number)=>'c'+i;
  const ordered=[...options.layout.order.filter(k=>options.keys.includes(k)),...options.keys.filter(k=>!options.layout.order.includes(k))];
  const definitions:Record<string,unknown>[]=[],groups=new Map<string,{columns:Record<string,unknown>[]} >();
  for(const key of ordered){
    const i=options.keys.indexOf(key),group=options.columnGroups?.[key],title=publicText(group?.measureTitle??headers[i]);
    const definition={title,titleFormatter:()=>document.createTextNode(title),field:field(i),visible:options.visible?.[i]!==false,minWidth:100,width:options.layout.widths[key],resizable:true,headerSort:!options.visual?.includes(i),hozAlign:numeric.includes(i)?'right':'left',cssClass:numeric.includes(i)?'grid-number':'',formatter:(cell:any)=>{const index=Number(cell.getRow().getData().__sourceIndex),rich=options.cells?.(index,i);if(rich)return rich;const span=document.createElement('span');span.textContent=publicText(options.displayRows?.[index]?.[i]??cell.getValue());if(numeric.includes(i)&&String(cell.getValue()).startsWith('-'))span.className='grid-negative';if(options.cellTitles?.[index]?.[i]!==undefined)span.title=publicText(options.cellTitles[index][i]);else if(options.displayRows?.[index]?.[i]!==undefined)span.title=String(cell.getValue());return span;},headerWordWrap:true,
      sorter:numeric.includes(i)?(a:string,b:string)=>a===''?(b===''?0:-1):b===''?1:compareDecimalStrings(a,b):'string'};
    if(!group){definitions.push(definition);continue;}
    let parent=groups.get(group.key);if(!parent){const title=publicText(group.title);parent={columns:[]};groups.set(group.key,parent);definitions.push({...parent,title,titleFormatter:()=>document.createTextNode(title),headerHozAlign:'center',cssClass:'pivot-column-group'});}
    parent.columns.push(definition);
  }
  const table=new TabulatorFull(container,{
    // Grouped rows start in the order of their groups, which Tabulator keeps as it meets them; a group the filters empty disappears.
    data:values.map((row,index)=>({...Object.fromEntries(row.map((cell,i)=>[field(i),cell])),__sourceIndex:String(index)}))
      .sort((a,b)=>options.group?options.group.order.indexOf(options.group.keys[Number(a.__sourceIndex)])-options.group.order.indexOf(options.group.keys[Number(b.__sourceIndex)]):0),
    rowFormatter:options.onRowOpen?(row:any)=>{const target=row.getElement() as HTMLElement;target.tabIndex=0;target.classList.add('openable-row');target.setAttribute('aria-label','Open row details');target.onkeydown=event=>{if(event.target===target&&['Enter',' '].includes(event.key)){event.preventDefault();openRow(row);}};}:undefined,
    columns:definitions,
    layout:options.stretch?'fitDataStretch':'fitDataFill',height:Math.min(560,Math.max(140,rows.length*(options.rowHeight??35)+(options.group?.order.length??0)*36+(groups.size?82:46)))+'px',movableColumns:true,columnDefaults:{tooltip:false},placeholder:'No rows match this selection.',
    ...(options.group?{groupBy:(data:Record<string,string>)=>options.group!.keys[Number(data.__sourceIndex)],groupHeader:(key:string,count:number)=>options.group!.header(key,count)}:{}),
    initialSort:options.layout.sort.filter(s=>options.keys.includes(s.key)).map(s=>({column:field(options.keys.indexOf(s.key)),dir:s.direction})),
  });
  function openRow(row:any){if(!current()||!options.onRowOpen)return;const index=Number(row.getData().__sourceIndex);if(Number.isInteger(index)&&index>=0&&index<rows.length)options.onRowOpen(index);}
  table.on('rowClick',(event:MouseEvent,row:any)=>{
    const selection=window.getSelection(),target=event.target as Element;
    if(event.button!==0||event.ctrlKey||event.metaKey||event.shiftKey||event.altKey||target.closest('a,button,input,select,textarea,[contenteditable=true]')||
      selection&&!selection.isCollapsed&&container.contains(selection.anchorNode)||
      event.detail>0&&gesture&&(gesture.cancelled||gesture.row!==row.getElement()))return;
    openRow(row);
  });
  const searched=(cells:string[],index:number)=>options.searchText?[...cells,options.searchText[index]]:cells;
  function matches(row:Record<string,string>){return matchesTableRow(searched(headers.map((_,i)=>row[field(i)]),Number(row.__sourceIndex)),state.query,state.columns,options.visible,numeric);}
  function update(){const shown=values.filter((row,index)=>matchesTableRow(searched(row,index),state.query,state.columns,options.visible,numeric)).length;count.textContent=`${shown} of ${values.length} rows`;clear.hidden=!state.query&&!state.columns.some(v=>v!==null);}
  function apply(){update();if(ready&&alive)table.setFilter(matches);}
  function layout():GridLayout{return {order:table.getColumns().map(c=>options.keys[Number(c.getField().slice(1))]),widths:Object.fromEntries(table.getColumns().map(c=>[options.keys[Number(c.getField().slice(1))],Math.max(40,Math.min(3000,Math.round(c.getWidth()||120)))])),sort:table.getSorters().map(s=>({key:options.keys[Number(s.field.slice(1))],direction:s.dir}))};}
  // fitDataFill refits every column without a definition width whenever the
  // grid is redrawn for a new size, so a user's resize becomes that width.
  table.on('columnResized',(column:any)=>{column.getDefinition().width=column.getWidth();});
  table.on('tableBuilt',()=>{if(!alive){table.destroy();return;}ready=true;apply();options.onReady?.();});
  for(const event of ['columnMoved','columnResized','dataSorted'])table.on(event,()=>{if(ready&&current())options.onLayout(layout());});
  search.oninput=()=>{if(!current())return;state.query=search.value;apply();};
  toggle.onclick=()=>{if(!current())return;filterPanel.hidden=!filterPanel.hidden;toggle.setAttribute('aria-expanded',String(!filterPanel.hidden));};
  clear.onclick=()=>{if(!current())return;state.query='';state.columns=state.columns.map(()=>null);search.value='';selects.forEach(s=>{s.value='';if(s instanceof HTMLInputElement){s.setCustomValidity('');s.removeAttribute('aria-invalid');}});apply();};
  update();
  const visibleIndices=()=>table.getColumns().map(c=>Number(c.getField().slice(1))).filter(i=>options.visible?.[i]!==false);
  return {
    ready:()=>ready&&alive,
    state:()=>({query:state.query,columns:state.columns.slice()}),layout,
    headers:()=>visibleIndices().map(i=>headers[i]),numeric:()=>visibleIndices().flatMap((original,i)=>numeric.includes(original)?[i]:[]),
    visibleRows:()=>{if(!current()||!ready)throw Error('Refresh this table before exporting.');return table.getData('display').filter(matches).map(row=>visibleIndices().map(i=>row[field(i)]));},
    /** The source rows the filters show, in display order. */
    shownRows:()=>{if(!current()||!ready)throw Error('Refresh this table before exporting.');return table.getData('display').filter(matches).map(row=>Number(row.__sourceIndex));},
    /** Draws one column's rich cells again, for content that arrived after the grid; the rest of each row stays as it was. */
    redrawCells(column:number){if(ready&&alive)for(const row of table.getRows()){const cell=row.getCell(field(column));if(cell)cell.setValue(row.getData()[field(column)]);}},
    destroy(){if(!alive)return;alive=false;gesture=undefined;container.removeEventListener('pointerdown',pointerDown,true);document.removeEventListener('pointermove',pointerMove,true);document.removeEventListener('pointercancel',cancelGesture,true);document.removeEventListener('scroll',cancelGesture,true);if(ready)table.destroy();host.replaceChildren();},
  };
}
