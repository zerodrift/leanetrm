import {TabulatorFull,type GridGroup,type GridRow} from './vendor/tabulator.js';
import {compareDecimalStrings} from './analytics-model.js';
import {matchesTableRow,columnValues,validNumericFilter} from './regular-table.js';
import {publicText} from './market-view.js';
import {retainPlace,type Place} from './grid-place.js';
export type GridLayout={order:string[];widths:Record<string,number>;sort:{key:string;direction:'asc'|'desc'}[]};
export const emptyGridLayout=():GridLayout=>({order:[],widths:{},sort:[]});
/** Rows under headers, in the order given: each source row's group key and the header drawn for each key. */
export type GridGroupOption={keys:string[];order:string[];header:(key:string,count:number)=>HTMLElement};
type Cells=(string|HTMLElement)[][];
// cells: rich content drawn for a cell instead of its display text; sorting and filtering still use the row's exact strings.
// searchText: each source row's words that its rich cells draw, which the row search reads beside the row's exact strings.
// visual: columns that only draw, such as a sparkline, and are neither sorted nor filtered.
// group: rows under headers (GridGroupOption).
// rowKeys: each source row's own identity, such as its trade and binding, which the selection, the open row and the place in the rows follow
// through new data. A grid given none names its rows by their place in the rows it was given.
// stretch: the last column fills the grid's remaining width.
// toolbarHost: where the filter toolbar (search, column filters, row count) is drawn, when not above the table.
// giveUp: columns, by index, that the grid gives up in this order, one at a time, only while the columns it shows do not fit its width, as a
// consumer that takes room from it asks; `fitColumns` changes them while the grid stands. A grid with room for all of them shows all.
export type GridOptions={displayRows?:string[][];cellTitles?:string[][];visible?:boolean[];initial?:{query:string;columns:(string|null)[]};retainMissingFilters?:boolean;keys:string[];columnGroups?:Record<string,{key:string;title:string;measureTitle:string}>;layout:GridLayout;onLayout:(value:GridLayout)=>void;onReady?:()=>void;onRowOpen?:(index:number)=>void;onRowDoubleOpen?:(index:number)=>void;
  cells?:(index:number,column:number)=>Node|null;searchText?:string[];visual?:number[];group?:GridGroupOption;rowKeys?:readonly string[];rowHeight?:number;stretch?:boolean;toolbarHost?:HTMLElement;giveUp?:readonly number[]};
/**
 * What a grid is built for and cannot change while it stands: its key, scope and columns. A later render with the same identity changes the
 * standing grid's rows, grouping, column visibility, filters and layout in place, so the person keeps their place in it.
 */
export function gridIdentity(key:string,scope:string,headers:readonly string[],numeric:readonly number[],options:GridOptions){
  return JSON.stringify([key,scope,headers,numeric,options.keys,options.visual??[],options.columnGroups??null,!!options.stretch]);
}
const frame=()=>new Promise<void>(resolve=>requestAnimationFrame(()=>resolve()));
export function renderDataGrid(host:HTMLElement,key:string,scope:string,headers:string[],rows:Cells,numeric:number[],isBusy:()=>boolean,options:GridOptions){
  const el=<K extends keyof HTMLElementTagNameMap>(tag:K,value?:string)=>{const n=document.createElement(tag);if(value!==undefined)n.textContent=publicText(value);return n;};
  const texts=(source:Cells)=>source.map(row=>row.map(cell=>publicText(typeof cell==='string'?cell:cell.textContent??'')));
  // Two rows with one key could not be told apart when the rows change, so the selection or the place could move to the wrong one.
  const keysOf=(source:Cells,given:readonly string[]|undefined)=>{
    const result=given?[...given]:source.map((_,index)=>String(index));
    if(result.length!==source.length||new Set(result).size!==result.length)throw Error('Each row of this table needs a key of its own.');
    return result;
  };
  let values=texts(rows),keys=keysOf(rows,options.rowKeys);
  const state={query:options.initial?.query??'',columns:headers.map((_,i)=>options.initial?.columns[i]??null)};
  const toolbar=el('div');toolbar.className='table-filters';
  const search=el('input');search.type='search';search.placeholder='Filter rows…';search.className='table-search';search.value=state.query;search.maxLength=200;search.setAttribute('aria-label','Filter table rows');
  const toggle=el('button','Column filters');toggle.type='button';toggle.className='secondary column-filter-toggle';toggle.setAttribute('aria-expanded','false');
  const clear=el('button','Clear filters');clear.type='button';clear.className='text-button table-filter-clear';
  const count=el('span');count.className='table-row-count';count.setAttribute('aria-live','polite');toolbar.append(search,toggle,clear,count);
  // The open row, when the filters hide it, is said so with the way to show it: the position a panel is opened on by a booking or by the assistant is
  // not one the person pressed in the rows, which they could not have pressed had its row been hidden. A selection an update leaves without
  // its row is cleared and said so here too.
  const hiddenNote=el('p');hiddenNote.className='table-open-note';hiddenNote.setAttribute('role','status');hiddenNote.hidden=true;
  const noteText=el('span'),showHidden=el('button','Clear filters');showHidden.type='button';showHidden.className='text-button';hiddenNote.append(noteText,showHidden);
  // Rows kept on screen while they no longer state the current read, with the reason.
  const staleNote=el('p');staleNote.className='table-stale-note';staleNote.setAttribute('role','status');staleNote.hidden=true;
  const filterPanel=el('div');filterPanel.className='grid-column-filters';filterPanel.hidden=true;filterPanel.id='column-filters-'+key;toggle.setAttribute('aria-controls',filterPanel.id);
  let selects:(HTMLSelectElement|HTMLInputElement)[]=[];
  // The column filters offer the values of the rows the grid holds, so they are drawn again whenever its rows or visible columns change.
  function drawFilters(){
    selects=[];
    filterPanel.replaceChildren();
    headers.forEach((header,i)=>{
      if(options.visual?.includes(i))return;
      if(numeric.includes(i)){
        const label=el('label',header+(options.visible?.[i]===false?' (hidden)':'')),input=el('input');input.type='text';input.maxLength=200;input.placeholder='e.g. >= 100';input.setAttribute('aria-label','Filter '+header);input.title='Use =, !=, <, <=, > or >= with an exact number.';input.value=state.columns[i]??'';
        const mark=()=>{input.setCustomValidity(validNumericFilter(input.value.trim())?'':'Enter a number with =, !=, <, <=, > or >=.');input.setAttribute('aria-invalid',String(!validNumericFilter(input.value.trim())));};
        if(input.value)mark();
        input.oninput=()=>{if(!current())return;state.columns[i]=input.value.trim()?input.value:null;mark();apply();};label.append(input);filterPanel.append(label);selects.push(input);return;
      }
      const label=el('label',header+(options.visible?.[i]===false?' (hidden)':'')),select=el('select');select.setAttribute('aria-label','Filter '+header);select.append(new Option('All values',''));
      const choices=columnValues(values.map(row=>row[i]));if(state.columns[i]!==null&&!choices.includes(state.columns[i]!))choices.push(state.columns[i]!);
      choices.forEach((value,index)=>select.append(new Option(value||'(Blank)',String(index))));select.value=state.columns[i]===null?'':String(choices.indexOf(state.columns[i]!));
      select.onchange=()=>{if(!current())return;state.columns[i]=select.value===''?null:choices[Number(select.value)];apply();};label.append(select);filterPanel.append(label);selects.push(select);
    });
  }
  drawFilters();
  // A selection can remove its hidden-row notice between native clicks. Keep
  // the notices below the grid so that change cannot move a row under the pointer.
  const container=el('div');container.className='standard-data-grid';host.replaceChildren(...(options.toolbarHost?[]:[toolbar]),filterPanel,container,staleNote,hiddenNote);options.toolbarHost?.replaceChildren(toolbar);
  let alive=true,ready=false,markBuilt=()=>{};
  // Resolves once the table is built, so work asked for earlier waits for it instead of being skipped.
  const built=new Promise<void>(resolve=>{markBuilt=resolve;});
  const current=()=>alive&&host.isConnected&&container.parentElement===host&&!isBusy();
  // A pointer gesture belongs to one row. Selection, dragging and scrolling
  // must not become an accidental edit when the pointer is released.
  let gesture:{row:Element|null;x:number;y:number;cancelled:boolean}|undefined;
  const pointerDown=(event:PointerEvent)=>{const selection=window.getSelection();gesture={row:(event.target as Element).closest('.tabulator-row'),x:event.clientX,y:event.clientY,cancelled:event.button!==0||!!(selection&&!selection.isCollapsed&&container.contains(selection.anchorNode))};};
  const pointerMove=(event:PointerEvent)=>{if(gesture&&Math.hypot(event.clientX-gesture.x,event.clientY-gesture.y)>5)gesture.cancelled=true;};
  const cancelGesture=()=>{if(gesture)gesture.cancelled=true;};
  container.addEventListener('pointerdown',pointerDown,true);
  document.addEventListener('pointermove',pointerMove,true);
  document.addEventListener('pointercancel',cancelGesture,true);
  document.addEventListener('scroll',cancelGesture,true);
  // Deferred row positioning must yield to a later focus move, including one inside the detail.
  let focusVersion=0;const focusChanged=()=>{focusVersion++;};
  document.addEventListener('focusin',focusChanged,true);
  const field=(i:number)=>'c'+i;
  const ordered=(layout:GridLayout)=>[...layout.order.filter(k=>options.keys.includes(k)),...options.keys.filter(k=>!layout.order.includes(k))];
  // The columns the grid gives up in this order while they do not fit it, and those it has given up now.
  let order:readonly number[]=options.giveUp??[],given=new Set<number>();
  const shown=(i:number)=>options.visible?.[i]!==false&&!given.has(i);
  const definitions:Record<string,unknown>[]=[],groups=new Map<string,{columns:Record<string,unknown>[]} >();
  for(const key of ordered(options.layout)){
    const i=options.keys.indexOf(key),group=options.columnGroups?.[key],title=publicText(group?.measureTitle??headers[i]);
    const definition={title,titleFormatter:()=>document.createTextNode(title),field:field(i),visible:shown(i),minWidth:100,width:options.layout.widths[key],resizable:true,headerSort:!options.visual?.includes(i),hozAlign:numeric.includes(i)?'right':'left',cssClass:numeric.includes(i)?'grid-number':'',formatter:(cell:any)=>{const index=Number(cell.getRow().getData().__sourceIndex),rich=options.cells?.(index,i);if(rich)return rich;const span=document.createElement('span');span.textContent=publicText(options.displayRows?.[index]?.[i]??cell.getValue());if(numeric.includes(i)&&String(cell.getValue()).startsWith('-'))span.className='grid-negative';if(options.cellTitles?.[index]?.[i]!==undefined)span.title=publicText(options.cellTitles[index][i]);else if(options.displayRows?.[index]?.[i]!==undefined)span.title=String(cell.getValue());return span;},headerWordWrap:true,
      sorter:numeric.includes(i)?(a:string,b:string)=>a===''?(b===''?0:-1):b===''?1:compareDecimalStrings(a,b):'string'};
    if(!group){definitions.push(definition);continue;}
    let parent=groups.get(group.key);if(!parent){const title=publicText(group.title);parent={columns:[]};groups.set(group.key,parent);definitions.push({...parent,title,titleFormatter:()=>document.createTextNode(title),headerHozAlign:'center',cssClass:'pivot-column-group'});}
    parent.columns.push(definition);
  }
  // Whether the columns the table shows are wider than the room it has: it would scroll sideways.
  const overflows=()=>{const holder=container.querySelector<HTMLElement>('.tabulator-tableholder');return holder!==null&&holder.scrollWidth>holder.clientWidth+1;};
  // Draws the table again with the columns of its view that are not given up, and measures against what it draws.
  function lay(left:ReadonlySet<number>){
    given=new Set(left);
    for(let i=0;i<headers.length;i++){const column=table.getColumn(field(i));if(column){if(shown(i))column.show();else column.hide();}}
    redraw();
  }
  // Shows every column, then gives up those of `order`, from its first, for as long as the table still overflows. A table that
  // fits shows them all, and one that fits only without them shows as many as it can.
  function fit(){
    if(!ready||!alive||!order.length&&!given.size)return;
    const left=new Set<number>();lay(left);
    for(const i of order){if(!overflows())break;left.add(i);lay(left);}
  }
  // The room a table has changes with its container's width, which is not the window's: it is fitted again when that changes, at the place
  // the person last left it. An open detail keeps its own place and focus as it reflows (resizeDetail), so its rows are only fitted.
  let width=0;
  const resizing=new ResizeObserver(()=>{if(!alive||container.clientWidth===width)return;width=container.clientWidth;sizeDetail();if(detail){fit();return;}const at=lastPlace;void change(fit,at&&(()=>at));});
  resizing.observe(container);
  // The row the consumer has open, and the row selected, by key: marked whenever their elements are drawn.
  let opened:string|null=null,selected:string|null=null,selectionLost=false,selectionChanged:((key:string|null)=>void)|undefined;
  let detail:{key:string;body:HTMLElement}|undefined;
  function sizeDetail(){const holder=container.querySelector<HTMLElement>('.tabulator-tableholder');if(detail&&holder)detail.body.style.maxWidth=holder.clientWidth+'px';}
  const sourceIndex=(row:GridRow)=>Number(row.getData().__sourceIndex);
  const keyOf=(row:GridRow)=>row.getData().__key;
  const keyAt=(index:number|null)=>index===null?null:keys[index]??null;
  function markRow(row:GridRow){
    const target=row.getElement(),key=keyOf(row),isOpen=key===opened;target.dataset.rowKey=key;
    target.classList.toggle('open-row',isOpen);if(isOpen)target.setAttribute('aria-current','true');else target.removeAttribute('aria-current');
    target.classList.toggle('selected-row',key===selected);target.setAttribute('aria-selected',String(key===selected));
    if(detail?.key===key&&detail.body.parentElement!==target)target.append(detail.body);
    if(options.onRowOpen)target.setAttribute('aria-expanded',String(detail?.key===key));
  }
  function markRows(){
    if(selected===null)delete container.dataset.selectedRow;else container.dataset.selectedRow=selected;
    if(ready&&alive)for(const row of table.getRows())markRow(row);
  }
  // Grouped rows start in the order of their groups, which Tabulator keeps as it meets them; a group the filters empty disappears.
  const groupOf=(data:Record<string,string>)=>options.group!.keys[Number(data.__sourceIndex)];
  const rowData=()=>values.map((row,index)=>({...Object.fromEntries(row.map((cell,i)=>[field(i),cell])),__sourceIndex:String(index),__key:keys[index]}))
    .sort((a,b)=>options.group?options.group.order.indexOf(groupOf(a))-options.group.order.indexOf(groupOf(b)):0);
  const height=()=>Math.min(560,Math.max(140,values.length*(options.rowHeight??35)+(options.group?.order.length??0)*36+(groups.size?82:46)))+'px';
  let drawnHeight=height();
  const table=new TabulatorFull(container,{
    index:'__key',
    data:rowData(),
    rowFormatter:(row:any)=>{
      const target=row.getElement() as HTMLElement;
      if(options.onRowOpen){target.tabIndex=0;target.classList.add('openable-row');target.setAttribute('aria-label','Open row details');target.onkeydown=event=>{if(event.target===target&&['Enter',' '].includes(event.key)){event.preventDefault();openRow(row);}};}
      markRow(row);
    },
    columns:definitions,
    layout:options.stretch?'fitDataStretch':'fitDataFill',height:drawnHeight,movableColumns:true,columnDefaults:{tooltip:false},placeholder:'No rows match this selection.',
    groupHeader:(key:string,count:number,_rows:unknown,group:GridGroup)=>{
      // A group header opens and closes its rows from the keyboard as well as the arrow, and says whether its rows show.
      const header=options.group!.header(key,count);header.tabIndex=0;header.setAttribute('role','button');header.setAttribute('aria-expanded',String(group.isVisible()));
      header.onkeydown=event=>{
        if(event.target!==header||!['Enter',' '].includes(event.key))return;
        event.preventDefault();group.toggle();
        // Toggling draws every group header again, so the focus follows to the new header of this group.
        const redrawn=table.getGroups().find(other=>other.getKey()===key)?.getElement();if(redrawn)redrawn.querySelector<HTMLElement>('[role=button]')?.focus();
      };
      return header;
    },
    ...(options.group?{groupBy:groupOf}:{}),
    initialSort:options.layout.sort.filter(s=>options.keys.includes(s.key)).map(s=>({column:field(options.keys.indexOf(s.key)),dir:s.direction})),
  });
  // A resize or sort detaches and reattaches the row, including its persistent
  // detail. Preserve its focused control without taking focus from another one.
  const focusedDetail=()=>{const active=document.activeElement;return active instanceof HTMLElement&&detail?.body.contains(active)?active:undefined;};
  function restoreDetailFocus(held:HTMLElement|undefined){
    const active=document.activeElement;
    if(held?.isConnected&&detail?.body.contains(held)&&!detail.body.hidden&&
      (active===null||active===document.body||active===document.documentElement))held.focus({preventScroll:true});
  }
  let detailFocus:HTMLElement|undefined;
  table.on('renderStarted',()=>{detailFocus=focusedDetail()??detailFocus;});
  table.on('renderComplete',()=>{const held=detailFocus;detailFocus=undefined;restoreDetailFocus(held);});
  // Focusing a prompt or a field can scroll the virtual viewport too. Capture
  // its focus before Tabulator handles that scroll and rebuilds the visible rows.
  const keepDetailFocusOnScroll=()=>{const held=focusedDetail();if(held)requestAnimationFrame(()=>{if(alive)restoreDetailFocus(held);});};
  container.addEventListener('scroll',keepDetailFocusOnScroll,true);

  // The person's place: every change of the rows, their grouping, sorting, filters or columns, and of the grid's width, keeps the first row
  // in view where it was and the selected row selected (grid-place). Changes run one at a time, each from the place the last one left.
  const holder=()=>container.querySelector<HTMLElement>('.tabulator-tableholder');
  function capture():Place{
    const view=holder();
    if(view&&ready&&alive){
      const top=view.getBoundingClientRect().top;
      for(const row of view.querySelectorAll<HTMLElement>('.tabulator-row[data-row-key]')){const box=row.getBoundingClientRect();if(box.height>0&&box.top>=top-0.5)return {anchor:row.dataset.rowKey!,offset:box.top-top,selected};}
    }
    return {anchor:null,offset:0,selected};
  }
  // The rows the filters let through, in order: the rows a place can be kept on.
  const shownKeys=()=>table.getData('active').filter(matches).map(row=>row.__key);
  // A later change or an explicit scroll supersedes a place still being restored; a later change starts from the place being restored.
  // Changes wait for the table to be built: Tabulator refuses data before then, and run one at a time; restoring a place does not hold them up.
  let placing=0,updating=false,lastPlace:Place|undefined,restoring:{version:number;place:Place}|undefined,watching=0,queue:Promise<void>=built;
  async function restore(place:Place){
    const version=++placing,view=holder(),row=place.anchor===null?false:table.getRow(place.anchor);
    restoring={version,place};
    try{await settle(version,place,view,row);}finally{if(restoring?.version===version)restoring=undefined;}
  }
  // Whether a row is drawn within the grid's viewport.
  function inView(row:GridRow,view:HTMLElement){const target=row.getElement();if(!target.isConnected)return false;const box=target.getBoundingClientRect(),port=view.getBoundingClientRect();return box.bottom>port.top&&box.top<port.bottom;}
  async function settle(version:number,place:Place,view:HTMLElement|null,row:GridRow|false){
    if(!view||!row)return;
    const away=()=>{const target=row.getElement();return target.isConnected?target.getBoundingClientRect().top-view.getBoundingClientRect().top-place.offset:undefined;};
    // A row already where it was is left alone: scrolling to it would draw the rows again for nothing.
    if(Math.abs(away()??Infinity)<1){lastPlace=capture();return;}
    // A person working in the open detail sees the rows where they are: an anchor still in view is near enough, and scrolling would draw
    // the rows again under them.
    if(focusedDetail()&&inView(row,view))return;
    // Scrolling rebuilds virtual rows without the table's render events, detaching the open detail and any focus in it. The focus is put
    // back only where it was: a key pressed meanwhile has moved it on, and that move stands.
    const held=focusedDetail(),at=focusVersion;
    try{await row.scrollTo('top',false);}catch{return;}finally{if(focusVersion===at)restoreDetailFocus(held);}
    // The virtual renderer estimates the heights of rows it has not drawn, so the row is measured where it lands and moved again until it holds.
    for(let tries=0;tries<4&&version===placing&&alive;tries++){
      const delta=away();if(delta===undefined)return;
      if(Math.abs(delta)<1)break;
      const focused=focusedDetail(),moved=focusVersion;view.scrollTop+=delta;await frame();if(focusVersion===moved)restoreDetailFocus(focused);
    }
    if(version===placing&&alive)lastPlace=capture();
  }
  // A full redraw rebuilds the virtual renderer's estimates and scrolls to the top: the horizontal place is kept, and the first row in view
  // where it was, as far as the rows drawn at the old scroll offset show it.
  function redraw(place=capture()){
    const view=holder(),top=view?.scrollTop??0,left=view?.scrollLeft??0,held=focusedDetail(),at=focusVersion;
    table.redraw(true);
    if(!view)return;
    view.scrollTop=top;view.scrollLeft=left;placeNow(place);
    // The rows are drawn again at the restored offset on its scroll event, which can detach and reattach the open detail.
    if(held&&view.scrollTop!==0)requestAnimationFrame(()=>requestAnimationFrame(()=>{if(alive&&focusVersion===at)restoreDetailFocus(held);}));
  }
  // Moves the anchor back to its offset at once, when its row is drawn: a later scroll the person or a caller makes must not be undone.
  function placeNow(place:Place){
    placing++;
    const view=holder(),row=place.anchor===null?false:table.getRow(place.anchor),target=row?row.getElement():undefined;
    if(view&&row&&focusedDetail()&&inView(row,view))return;
    const delta=view&&target?.isConnected?target.getBoundingClientRect().top-view.getBoundingClientRect().top-place.offset:0;
    // Even a fraction of a pixel would scroll, and a scroll draws the virtual rows again, detaching a detail being opened.
    if(Math.abs(delta)>=1)view!.scrollTop+=delta;
  }
  // The place a change starts from: the one still being restored, or where the rows are now.
  const aimedPlace=()=>restoring&&restoring.version===placing?restoring.place:capture();
  async function keepPlace(work:()=>void|Promise<void>,from?:()=>Place){
    // A grid destroyed while a change waited has nothing left to change.
    if(!alive)return;
    if(!ready){await work();return;}
    const before={...(from?.()??aimedPlace()),selected},previous=shownKeys();
    updating=true;try{await work();}finally{updating=false;}
    if(!alive)return;
    const kept=retainPlace(before,previous,shownKeys());
    if(kept.selectionLost){selected=null;selectionLost=true;markRows();report();selectionChanged?.(null);}
    void restore(kept.place);
  }
  // The place is taken when the change is asked for, unless earlier changes are still to run: what draws in between, such as the open
  // detail sizing itself, must not become the place kept.
  let waiting=0;
  function change(work:()=>void|Promise<void>,from?:()=>Place){
    const asked=from??(waiting===0&&ready&&alive?(place=>()=>place)(aimedPlace()):undefined);
    waiting++;
    const run=queue.then(()=>keepPlace(work,asked)).finally(()=>{waiting--;});queue=run.catch(()=>undefined);return run;
  }
  // Where the person scrolls to is the place a change of width keeps: by then the rows have already moved.
  const watchPlace=()=>{if(watching||!alive)return;watching=requestAnimationFrame(()=>{watching=0;if(alive&&ready&&!updating)lastPlace=capture();});};
  container.addEventListener('scroll',watchPlace,true);
  // A header sort is the grid's own: its place is taken before it sorts and restored once it has drawn.
  let sortPlace:Place|undefined;
  table.on('dataSorting',()=>{if(ready&&alive&&!updating&&!sortPlace)sortPlace=capture();});
  table.on('dataSorted',()=>{const place=sortPlace;sortPlace=undefined;if(place)requestAnimationFrame(()=>void change(()=>{},()=>place));});

  function openRow(row:GridRow){if(!current()||!options.onRowOpen)return;placing++;const index=sourceIndex(row);if(Number.isInteger(index)&&index>=0&&index<values.length){choose(keyOf(row));options.onRowOpen(index);}}
  // The person selects a row by opening it; a selection the consumer makes is not reported back to it.
  function choose(key:string){if(key===selected)return;selected=key;selectionLost=false;markRows();report();selectionChanged?.(key);}
  function safeRowGesture(event:MouseEvent,targetRow:HTMLElement){
    const selection=window.getSelection(),target=event.target as Element;
    return current()&&event.button===0&&!event.ctrlKey&&!event.metaKey&&!event.shiftKey&&!event.altKey&&
      !target.closest('a,button,input,select,textarea,[contenteditable=true],.position-expansion')&&
      !(selection&&!selection.isCollapsed&&container.contains(selection.anchorNode))&&
      !(event.detail>0&&gesture&&(gesture.cancelled||gesture.row!==targetRow));
  }
  // Virtual redraws can replace a row element between the two clicks. The
  // row key identifies the same position within this grid.
  let doubleCandidate:string|undefined;
  const keyAtElement=(element:HTMLElement)=>{const row=table.getRows().find(row=>row.getElement()===element);return row?keyOf(row):undefined;};
  // A second native mousedown selects a word before dblclick. Keep text
  // selection available for drags and existing selections; suppress only that
  // automatic selection for a second click on the same eligible row.
  const secondMouseDown=(event:MouseEvent)=>{
    const row=(event.target as Element).closest<HTMLElement>('.tabulator-row');
    if(options.onRowDoubleOpen&&event.detail===2&&row&&doubleCandidate!==undefined&&keyAtElement(row)===doubleCandidate&&safeRowGesture(event,row))event.preventDefault();
  };
  container.addEventListener('mousedown',secondMouseDown,true);
  table.on('rowClick',(event:MouseEvent,row:GridRow)=>{
    const target=row.getElement();
    if(!safeRowGesture(event,target)){doubleCandidate=undefined;return;}
    if(options.onRowDoubleOpen&&event.detail>1)return;
    doubleCandidate=options.onRowDoubleOpen&&event.detail===1?keyOf(row):undefined;
    openRow(row);
  });
  // Handle the eligible double click before the grid's cell handler, which
  // otherwise selects the whole cell even when mousedown was prevented.
  const doubleClick=(event:MouseEvent)=>{
    const target=(event.target as Element).closest<HTMLElement>('.tabulator-row'),candidate=doubleCandidate;doubleCandidate=undefined;
    if(!options.onRowDoubleOpen||!target||candidate===undefined||!safeRowGesture(event,target))return;
    if(keyAtElement(target)!==candidate)return;
    const index=keys.indexOf(candidate);if(index<0)return;
    event.preventDefault();event.stopPropagation();options.onRowDoubleOpen(index);
  };
  container.addEventListener('dblclick',doubleClick,true);
  const searched=(cells:string[],index:number)=>options.searchText?[...cells,options.searchText[index]]:cells;
  function matches(row:Record<string,string>){return matchesTableRow(searched(headers.map((_,i)=>row[field(i)]),Number(row.__sourceIndex)),state.query,state.columns,options.visible,numeric);}
  const matchesSource=(index:number)=>matchesTableRow(searched(values[index],index),state.query,state.columns,options.visible,numeric);
  function report(){
    const shown=values.filter((_,index)=>matchesSource(index)).length;count.textContent=`${shown} of ${values.length} rows`;clear.hidden=!state.query&&!state.columns.some(v=>v!==null);
    const at=opened===null?-1:keys.indexOf(opened),hidden=at>=0&&!matchesSource(at);
    noteText.textContent=hidden?(selectionLost?'The selected position is no longer shown: it is hidden by this table’s row search or column filters. ':'The selected position is hidden by this table’s row search or column filters. ')
      :selectionLost?'The selected row is no longer shown, so no row is selected.':'';
    showHidden.hidden=!hidden;hiddenNote.hidden=!hidden&&!selectionLost;
  }
  function apply(){report();if(ready&&alive)void change(()=>table.setFilter(matches));}
  function layout():GridLayout{return {order:table.getColumns().map(c=>options.keys[Number(c.getField().slice(1))]),widths:Object.fromEntries(table.getColumns().map(c=>[options.keys[Number(c.getField().slice(1))],Math.max(40,Math.min(3000,Math.round(c.getWidth()||120)))])),sort:table.getSorters().map(s=>({key:options.keys[Number(s.field.slice(1))],direction:s.dir}))};}
  // Brings the standing columns to a layout a later render asks for, such as a saved view's: their order, widths and sort.
  function applyLayout(next:GridLayout){
    const wanted=ordered(next);
    if(!options.columnGroups&&layout().order.join('\u0000')!==wanted.join('\u0000'))
      wanted.forEach((key,at)=>{const column=table.getColumn(field(options.keys.indexOf(key)));if(at>0&&column)column.move(field(options.keys.indexOf(wanted[at-1])),true);});
    for(const [key,width] of Object.entries(next.widths)){const column=options.keys.includes(key)?table.getColumn(field(options.keys.indexOf(key))):false;if(column&&Math.abs(column.getWidth()-width)>1)column.setWidth(width);}
    const sort=next.sort.filter(s=>options.keys.includes(s.key)).map(s=>({column:field(options.keys.indexOf(s.key)),dir:s.direction}));
    if(JSON.stringify(sort)!==JSON.stringify(table.getSorters().map(s=>({column:s.field,dir:s.dir}))))table.setSort(sort);
  }
  function fitHeight(){const next=height();if(next!==drawnHeight){drawnHeight=next;table.setHeight(next);}}
  async function replaceAll(){fitHeight();await table.replaceData(rowData());}
  // Replaces the rows under the grouping now asked for. A grouping given up is dropped before the rows, whose groups no longer exist; one
  // taken up is applied after them, once they are in its order.
  async function regroup(grouped:boolean){
    if(grouped&&!options.group)table.setGroupBy(false);
    await replaceAll();
    if(options.group&&!grouped)table.setGroupBy(groupOf);
  }
  // fitDataFill refits every column without a definition width whenever the
  // grid is redrawn for a new size, so a user's resize becomes that width.
  table.on('columnResized',(column:any)=>{column.getDefinition().width=column.getWidth();});
  table.on('tableBuilt',()=>{if(!alive){table.destroy();return;}ready=true;markBuilt();fit();apply();options.onReady?.();});
  for(const event of ['columnMoved','columnResized','dataSorted'])table.on(event,()=>{if(ready&&!updating&&current())options.onLayout(layout());});
  search.oninput=()=>{if(!current())return;state.query=search.value;apply();};
  toggle.onclick=()=>{if(!current())return;filterPanel.hidden=!filterPanel.hidden;toggle.setAttribute('aria-expanded',String(!filterPanel.hidden));};
  function clearFilters(){state.query='';state.columns=state.columns.map(()=>null);search.value='';selects.forEach(s=>{s.value='';if(s instanceof HTMLInputElement){s.setCustomValidity('');s.removeAttribute('aria-invalid');}});apply();}
  clear.onclick=()=>{if(!current())return;clearFilters();};
  // Showing the hidden row clears the filters, and brings the row into view with focus on it once the grid has drawn it.
  showHidden.onclick=()=>{
    if(!current())return;
    const key=opened;clearFilters();
    if(key!==null)whenDrawn(key,row=>{row.getElement().scrollIntoView({block:'nearest'});row.getElement().focus({preventScroll:true});});
  };
  report();
  const visibleIndices=()=>table.getColumns().map(c=>Number(c.getField().slice(1))).filter(i=>options.visible?.[i]!==false);
  // Once the grid has drawn, which a grid that has only just been made, or is being changed, has not when it is asked, the row of a key is
  // scrolled to within the grid and then `use`d. It supersedes a place still being restored.
  function whenDrawn(key:string,use:(row:GridRow)=>void,stillCurrent=()=>true){
    if(!alive)return;
    void built.then(()=>queue).then(()=>new Promise<void>(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve())))).then(()=>{
      if(!stillCurrent())return;
      const row=alive&&ready?table.getRow(key)||undefined:undefined,held=focusedDetail();
      if(row)placing++;
      // scrollTo rebuilds virtual rows without the table's render events.
      return row?.scrollTo('nearest',false).catch(()=>undefined).then(()=>{if(alive&&stillCurrent()){use(row);restoreDetailFocus(held);}});
    });
  }
  /** Marks the rows as no longer stating the current read, with the reason, or clears the mark (null). */
  function markStale(reason:string|null){container.classList.toggle('stale',reason!==null);staleNote.textContent=reason===null?'':publicText(reason);staleNote.hidden=reason===null;}
  return {
    ready:()=>ready&&alive,
    state:()=>({query:state.query,columns:state.columns.slice()}),layout,
    headers:()=>visibleIndices().map(i=>headers[i]),numeric:()=>visibleIndices().flatMap((original,i)=>numeric.includes(original)?[i]:[]),
    visibleRows:()=>{if(!current()||!ready)throw Error('Refresh this table before exporting.');return table.getData('display').filter(matches).map(row=>visibleIndices().map(i=>row[field(i)]));},
    /** The source rows the filters show, in display order. */
    shownRows:()=>{if(!current()||!ready)throw Error('Refresh this table before exporting.');return table.getData('display').filter(matches).map(row=>Number(row.__sourceIndex));},
    /**
     * Draws one column's rich cells again, for content that arrived after the grid; the rest of each row stays as it was.
     * It relies on Tabulator 6.6.1 running a cell's formatter again when the cell is set to the value it already has
     * (Cell.setValue); a Tabulator upgrade has to keep that behavior, or this has to change with it.
     */
    redrawCells(column:number){if(ready&&alive)for(const row of table.getRows()){const cell=row.getCell(field(column));if(cell)cell.setValue(row.getData()[field(column)]);}},
    /** Gives up these columns instead of those it was given before, in this order, while the table does not fit its width. */
    fitColumns(next:readonly number[]){
      if(next.length===order.length&&next.every((index,at)=>index===order[at]))return;
      order=next;fit();
    },
    /** Replaces every row with these, by key, keeping the person's place. */
    replaceRows(next:Cells,displayRows:string[][]|undefined,rowKeys:readonly string[]){
      const nextKeys=keysOf(next,rowKeys);
      return change(async()=>{values=texts(next);keys=nextKeys;options={...options,displayRows};drawFilters();report();await replaceAll();});
    },
    /** Adds these rows after the others; the rows on screen stay where they are. A grouped grid's rows are replaced, never appended to. */
    appendRows(more:Cells,displayRows:string[][]|undefined,rowKeys:readonly string[]){
      const added=keysOf(more,rowKeys);
      return change(async()=>{
        if(options.group)throw Error('Rows cannot be added to a grouped table; replace its rows.');
        if(!!options.displayRows!==!!displayRows)throw Error('Added rows must state display text exactly when the table does.');
        const nextKeys=[...keys,...added],from=values.length;
        if(new Set(nextKeys).size!==nextKeys.length)throw Error('Each row of this table needs a key of its own.');
        values=[...values,...texts(more)];keys=nextKeys;options={...options,displayRows:displayRows&&[...options.displayRows!,...displayRows]};
        drawFilters();report();fitHeight();
        await table.addData(texts(more).map((row,at)=>({...Object.fromEntries(row.map((cell,i)=>[field(i),cell])),__sourceIndex:String(from+at),__key:added[at]})),false);
      });
    },
    /** Groups the rows under these headers, or not at all, keeping the person's place. */
    setGrouping(group:GridGroupOption|undefined){
      void change(async()=>{const grouped=!!options.group;options={...options,group};await regroup(grouped);});
    },
    /** Shows the columns marked true and hides the others, keeping the person's place; filters on hidden columns stay. */
    setVisible(visible:boolean[]){void change(()=>{options={...options,visible};drawFilters();report();lay(given);fit();});},
    /**
     * Takes a later render's rows and options for the grid it already is (gridIdentity): its rows, grouping, visible columns, filters and
     * layout change in place, and only once the new rows are in do they stop being marked stale. An update that fails puts the rows it
     * replaced back and keeps them marked stale with the reason; it never rejects.
     */
    reconfigure(next:Cells,nextOptions:GridOptions){
      const nextKeys=keysOf(next,nextOptions.rowKeys);
      return change(async()=>{
        const before={values,keys,options,order,query:state.query,columns:state.columns.slice()},grouped=!!options.group;
        try{
          values=texts(next);keys=nextKeys;options=nextOptions;order=options.giveUp??[];
          if(options.initial){state.query=options.initial.query;state.columns=headers.map((_,i)=>options.initial!.columns[i]??null);search.value=state.query;}
          drawFilters();report();
          await regroup(grouped);
          lay(given);applyLayout(options.layout);fit();
          markStale(null);
        }catch(error){
          ({values,keys,options,order}=before);state.query=before.query;state.columns=before.columns;search.value=state.query;drawFilters();report();
          await regroup(!!nextOptions.group).catch(()=>undefined);
          markStale(`These rows could not be updated: ${error instanceof Error&&error.message?error.message:'the update failed'}`);
        }
      });
    },
    /** Selects the row of a key, or none, without reporting it back as the person's selection. */
    select(key:string|null){selected=key;selectionLost=false;markRows();report();},
    selected:()=>selected,
    /** Hears the selection the person makes, and its clearing when an update leaves it without its row. */
    onSelect(handler:(key:string|null)=>void){selectionChanged=handler;},
    markStale,
    /** Marks the row of a source index as the open one (null: none), and keeps it marked as the grid draws its rows again; says so when the filters hide it. It is also the selected row. */
    selectRow(index:number|null){opened=keyAt(index);if(opened!==null){selected=opened;selectionLost=false;}report();markRows();},
    /** Retains the detail beneath its source row through sorting, updates and virtual redraws. */
    detailRow(index:number|null,body:HTMLElement){
      const at=keyAt(index),changed=at===null?detail!==undefined:detail?.key!==at||detail?.body!==body,place=changed&&at===null?capture():undefined;
      if(at===null){detail?.body.remove();detail=undefined;table.options.renderVerticalBuffer=0;}else if(changed){detail?.body.remove();detail={key:at,body};}
      sizeDetail();
      if(ready&&alive){
        for(const row of table.getRows()){markRow(row);row.normalizeHeight();}
        // Removing an offscreen detail also changes the virtual renderer's
        // estimated row height and padding. A full public redraw rebuilds
        // those estimates; retain the person's horizontal place, and the first row in view where it was, also when the detail was above it.
        if(place)redraw(place);
      }
    },
    resizeDetail(keepVisible=false){
      sizeDetail();if(!ready||!alive)return;
      const shownDetail=detail,held=focusedDetail()??(keepVisible?shownDetail?.body:undefined),focusAt=focusVersion;
      const row=shownDetail?table.getRow(shownDetail.key):false;if(row)row.normalizeHeight();
      if(shownDetail?.body.isConnected){
        const height=shownDetail.body.offsetHeight,holder=container.querySelector<HTMLElement>('.tabulator-tableholder');
        // A native focus jump inside a tall detail must still land in that
        // row. Tabulator 6.6.1 resets its learned buffer on resize. Retain the
        // measured extent in its configured buffer, then reset it on collapse.
        const buffer=holder&&height>holder.clientHeight?height:0;
        if(table.options.renderVerticalBuffer!==buffer){table.options.renderVerticalBuffer=buffer;table.redraw(true);if(row)row.normalizeHeight();}
      }
      // Returning from a tall editor can leave its source row outside the
      // virtual viewport. Restore only that still-current position, without
      // taking focus from a control the person has moved to in the meantime.
      if(keepVisible&&shownDetail){
        const stillSelected=()=>{const active=document.activeElement;return detail===shownDetail&&!shownDetail.body.hidden&&focusVersion===focusAt&&
          (shownDetail.body.contains(active)||active===null||active===document.body||active===document.documentElement);};
        whenDrawn(shownDetail.key,()=>restoreDetailFocus(held),stillSelected);
      }
    },
    /**
     * Moves focus to the row of a source index, scrolling it into view first, once the grid has drawn again: a change of the
     * grid's width, such as a closing panel's, redraws its rows in the frame after it, which would take focus from a row focused sooner.
     */
    focusRow(index:number){const at=keyAt(index);if(at!==null)whenDrawn(at,row=>row.getElement().focus({preventScroll:true}));},
    /** Scrolls the row of a source index into view, in the grid and in the page, as far as it takes to show it, once the grid has drawn again. */
    revealRow(index:number){const at=keyAt(index);if(at!==null)whenDrawn(at,row=>row.getElement().scrollIntoView({block:'nearest'}));},
    destroy(){if(!alive)return;alive=false;placing++;if(watching)cancelAnimationFrame(watching);resizing.disconnect();gesture=undefined;doubleCandidate=undefined;container.removeEventListener('mousedown',secondMouseDown,true);container.removeEventListener('dblclick',doubleClick,true);container.removeEventListener('pointerdown',pointerDown,true);container.removeEventListener('scroll',keepDetailFocusOnScroll,true);container.removeEventListener('scroll',watchPlace,true);document.removeEventListener('pointermove',pointerMove,true);document.removeEventListener('pointercancel',cancelGesture,true);document.removeEventListener('scroll',cancelGesture,true);document.removeEventListener('focusin',focusChanged,true);if(ready)table.destroy();host.replaceChildren();toolbar.remove();},
  };
}
export type GridHandle=ReturnType<typeof renderDataGrid>;
