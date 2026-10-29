import {TabulatorFull,type GridGroup,type GridRow} from './vendor/tabulator.js';
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
// toolbarHost: where the filter toolbar (search, column filters, row count) is drawn, when not above the table.
// giveUp: columns, by index, that the grid gives up in this order, one at a time, only while the columns it shows do not fit its width, as a
// consumer that takes room from it asks; `fitColumns` changes them while the grid stands. A grid with room for all of them shows all.
type GridOptions={displayRows?:string[][];cellTitles?:string[][];visible?:boolean[];initial?:{query:string;columns:(string|null)[]};retainMissingFilters?:boolean;keys:string[];columnGroups?:Record<string,{key:string;title:string;measureTitle:string}>;layout:GridLayout;onLayout:(value:GridLayout)=>void;onReady?:()=>void;onRowOpen?:(index:number)=>void;onRowDoubleOpen?:(index:number)=>void;
  cells?:(index:number,column:number)=>Node|null;searchText?:string[];visual?:number[];group?:{keys:string[];order:string[];header:(key:string,count:number)=>HTMLElement};rowHeight?:number;stretch?:boolean;toolbarHost?:HTMLElement;giveUp?:readonly number[]};
export function renderDataGrid(host:HTMLElement,key:string,scope:string,headers:string[],rows:(string|HTMLElement)[][],numeric:number[],isBusy:()=>boolean,options:GridOptions){
  const el=<K extends keyof HTMLElementTagNameMap>(tag:K,value?:string)=>{const n=document.createElement(tag);if(value!==undefined)n.textContent=publicText(value);return n;};
  const values=rows.map(row=>row.map(cell=>publicText(typeof cell==='string'?cell:cell.textContent??'')));
  const state={query:options.initial?.query??'',columns:headers.map((_,i)=>options.initial?.columns[i]??null)};
  const toolbar=el('div');toolbar.className='table-filters';
  const search=el('input');search.type='search';search.placeholder='Filter rows…';search.className='table-search';search.value=state.query;search.maxLength=200;search.setAttribute('aria-label','Filter table rows');
  const toggle=el('button','Column filters');toggle.type='button';toggle.className='secondary column-filter-toggle';toggle.setAttribute('aria-expanded','false');
  const clear=el('button','Clear filters');clear.type='button';clear.className='text-button table-filter-clear';
  const count=el('span');count.className='table-row-count';count.setAttribute('aria-live','polite');toolbar.append(search,toggle,clear,count);
  // The open row, when the filters hide it, is said so with the way to show it: the position a panel is opened on by a booking or by the assistant is
  // not one the person pressed in the rows, which they could not have pressed had its row been hidden.
  const hiddenNote=el('p','The selected position is hidden by this table’s row search or column filters. ');hiddenNote.className='table-open-note';hiddenNote.setAttribute('role','status');hiddenNote.hidden=true;
  const showHidden=el('button','Clear filters');showHidden.type='button';showHidden.className='text-button';hiddenNote.append(showHidden);
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
  // A selection can remove its hidden-row notice between native clicks. Keep
  // the notice below the grid so that change cannot move a row under the pointer.
  const container=el('div');container.className='standard-data-grid';host.replaceChildren(...(options.toolbarHost?[]:[toolbar]),filterPanel,container,hiddenNote);options.toolbarHost?.replaceChildren(toolbar);
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
  const ordered=[...options.layout.order.filter(k=>options.keys.includes(k)),...options.keys.filter(k=>!options.layout.order.includes(k))];
  // The columns the grid gives up in this order while they do not fit it, and those it has given up now.
  let order:readonly number[]=options.giveUp??[],given=new Set<number>();
  const shown=(i:number)=>options.visible?.[i]!==false&&!given.has(i);
  const definitions:Record<string,unknown>[]=[],groups=new Map<string,{columns:Record<string,unknown>[]} >();
  for(const key of ordered){
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
    table.redraw(true);
  }
  // Shows every column, then gives up those of `order`, from its first, for as long as the table still overflows. A table that
  // fits shows them all, and one that fits only without them shows as many as it can.
  function fit(){
    if(!ready||!alive||!order.length&&!given.size)return;
    const left=new Set<number>();lay(left);
    for(const i of order){if(!overflows())break;left.add(i);lay(left);}
  }
  // The room a table has changes with its container's width, which is not the window's: it is fitted again when that changes.
  let width=0;
  const resizing=new ResizeObserver(()=>{if(!alive||container.clientWidth===width)return;width=container.clientWidth;sizeDetail();fit();});
  resizing.observe(container);
  // The row the consumer has open, which it names by source index: marked whenever its element is drawn.
  let opened:number|null=null;
  let detail:{index:number;body:HTMLElement}|undefined;
  function sizeDetail(){const holder=container.querySelector<HTMLElement>('.tabulator-tableholder');if(detail&&holder)detail.body.style.maxWidth=holder.clientWidth+'px';}
  const sourceIndex=(row:GridRow)=>Number(row.getData().__sourceIndex);
  function markOpen(row:GridRow){const target=row.getElement(),isOpen=sourceIndex(row)===opened;target.classList.toggle('open-row',isOpen);if(isOpen)target.setAttribute('aria-current','true');else target.removeAttribute('aria-current');
    if(detail?.index===sourceIndex(row)&&detail.body.parentElement!==target)target.append(detail.body);
    if(options.onRowOpen)target.setAttribute('aria-expanded',String(detail?.index===sourceIndex(row)));
  }
  const table=new TabulatorFull(container,{
    // Grouped rows start in the order of their groups, which Tabulator keeps as it meets them; a group the filters empty disappears.
    data:values.map((row,index)=>({...Object.fromEntries(row.map((cell,i)=>[field(i),cell])),__sourceIndex:String(index)}))
      .sort((a,b)=>options.group?options.group.order.indexOf(options.group.keys[Number(a.__sourceIndex)])-options.group.order.indexOf(options.group.keys[Number(b.__sourceIndex)]):0),
    rowFormatter:(row:any)=>{
      const target=row.getElement() as HTMLElement;
      if(options.onRowOpen){target.tabIndex=0;target.classList.add('openable-row');target.setAttribute('aria-label','Open row details');target.onkeydown=event=>{if(event.target===target&&['Enter',' '].includes(event.key)){event.preventDefault();openRow(row);}};}
      markOpen(row);
    },
    columns:definitions,
    layout:options.stretch?'fitDataStretch':'fitDataFill',height:Math.min(560,Math.max(140,rows.length*(options.rowHeight??35)+(options.group?.order.length??0)*36+(groups.size?82:46)))+'px',movableColumns:true,columnDefaults:{tooltip:false},placeholder:'No rows match this selection.',
    ...(options.group?{groupBy:(data:Record<string,string>)=>options.group!.keys[Number(data.__sourceIndex)],groupHeader:(key:string,count:number,_rows:unknown,group:GridGroup)=>{
      // A group header opens and closes its rows from the keyboard as well as the arrow, and says whether its rows show.
      const header=options.group!.header(key,count);header.tabIndex=0;header.setAttribute('role','button');header.setAttribute('aria-expanded',String(group.isVisible()));
      header.onkeydown=event=>{
        if(event.target!==header||!['Enter',' '].includes(event.key))return;
        event.preventDefault();group.toggle();
        // Toggling draws every group header again, so the focus follows to the new header of this group.
        const redrawn=table.getGroups().find(other=>other.getKey()===key)?.getElement();if(redrawn)redrawn.querySelector<HTMLElement>('[role=button]')?.focus();
      };
      return header;
    }}:{}),
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
  function openRow(row:GridRow){if(!current()||!options.onRowOpen)return;const index=sourceIndex(row);if(Number.isInteger(index)&&index>=0&&index<rows.length)options.onRowOpen(index);}
  function safeRowGesture(event:MouseEvent,targetRow:HTMLElement){
    const selection=window.getSelection(),target=event.target as Element;
    return current()&&event.button===0&&!event.ctrlKey&&!event.metaKey&&!event.shiftKey&&!event.altKey&&
      !target.closest('a,button,input,select,textarea,[contenteditable=true],.position-expansion')&&
      !(selection&&!selection.isCollapsed&&container.contains(selection.anchorNode))&&
      !(event.detail>0&&gesture&&(gesture.cancelled||gesture.row!==targetRow));
  }
  // Virtual redraws can replace a row element between the two clicks. The
  // immutable source index identifies the same position within this grid.
  let doubleCandidate:number|undefined;
  const indexAt=(element:HTMLElement)=>{const row=table.getRows().find(row=>row.getElement()===element);return row?sourceIndex(row):undefined;};
  // A second native mousedown selects a word before dblclick. Keep text
  // selection available for drags and existing selections; suppress only that
  // automatic selection for a second click on the same eligible row.
  const secondMouseDown=(event:MouseEvent)=>{
    const row=(event.target as Element).closest<HTMLElement>('.tabulator-row');
    if(options.onRowDoubleOpen&&event.detail===2&&row&&doubleCandidate!==undefined&&indexAt(row)===doubleCandidate&&safeRowGesture(event,row))event.preventDefault();
  };
  container.addEventListener('mousedown',secondMouseDown,true);
  table.on('rowClick',(event:MouseEvent,row:GridRow)=>{
    const target=row.getElement();
    if(!safeRowGesture(event,target)){doubleCandidate=undefined;return;}
    if(options.onRowDoubleOpen&&event.detail>1)return;
    doubleCandidate=options.onRowDoubleOpen&&event.detail===1?sourceIndex(row):undefined;
    openRow(row);
  });
  // Handle the eligible double click before the grid's cell handler, which
  // otherwise selects the whole cell even when mousedown was prevented.
  const doubleClick=(event:MouseEvent)=>{
    const target=(event.target as Element).closest<HTMLElement>('.tabulator-row'),candidate=doubleCandidate;doubleCandidate=undefined;
    if(!options.onRowDoubleOpen||!target||candidate===undefined||!safeRowGesture(event,target))return;
    const index=indexAt(target);if(index!==candidate)return;
    if(index===undefined||!Number.isInteger(index)||index<0||index>=rows.length)return;
    event.preventDefault();event.stopPropagation();options.onRowDoubleOpen(index);
  };
  container.addEventListener('dblclick',doubleClick,true);
  const searched=(cells:string[],index:number)=>options.searchText?[...cells,options.searchText[index]]:cells;
  function matches(row:Record<string,string>){return matchesTableRow(searched(headers.map((_,i)=>row[field(i)]),Number(row.__sourceIndex)),state.query,state.columns,options.visible,numeric);}
  const matchesSource=(index:number)=>matchesTableRow(searched(values[index],index),state.query,state.columns,options.visible,numeric);
  function update(){
    const shown=values.filter((_,index)=>matchesSource(index)).length;count.textContent=`${shown} of ${values.length} rows`;clear.hidden=!state.query&&!state.columns.some(v=>v!==null);
    hiddenNote.hidden=opened===null||values[opened]===undefined||matchesSource(opened);
  }
  function apply(){update();if(ready&&alive)table.setFilter(matches);}
  function layout():GridLayout{return {order:table.getColumns().map(c=>options.keys[Number(c.getField().slice(1))]),widths:Object.fromEntries(table.getColumns().map(c=>[options.keys[Number(c.getField().slice(1))],Math.max(40,Math.min(3000,Math.round(c.getWidth()||120)))])),sort:table.getSorters().map(s=>({key:options.keys[Number(s.field.slice(1))],direction:s.dir}))};}
  // fitDataFill refits every column without a definition width whenever the
  // grid is redrawn for a new size, so a user's resize becomes that width.
  table.on('columnResized',(column:any)=>{column.getDefinition().width=column.getWidth();});
  table.on('tableBuilt',()=>{if(!alive){table.destroy();return;}ready=true;markBuilt();fit();apply();options.onReady?.();});
  for(const event of ['columnMoved','columnResized','dataSorted'])table.on(event,()=>{if(ready&&current())options.onLayout(layout());});
  search.oninput=()=>{if(!current())return;state.query=search.value;apply();};
  toggle.onclick=()=>{if(!current())return;filterPanel.hidden=!filterPanel.hidden;toggle.setAttribute('aria-expanded',String(!filterPanel.hidden));};
  function clearFilters(){state.query='';state.columns=state.columns.map(()=>null);search.value='';selects.forEach(s=>{s.value='';if(s instanceof HTMLInputElement){s.setCustomValidity('');s.removeAttribute('aria-invalid');}});apply();}
  clear.onclick=()=>{if(!current())return;clearFilters();};
  // Showing the hidden row clears the filters, and brings the row into view with focus on it once the grid has drawn it.
  showHidden.onclick=()=>{
    if(!current())return;
    const index=opened;clearFilters();
    if(index!==null)whenDrawn(index,row=>{row.getElement().scrollIntoView({block:'nearest'});row.getElement().focus({preventScroll:true});});
  };
  update();
  const visibleIndices=()=>table.getColumns().map(c=>Number(c.getField().slice(1))).filter(i=>options.visible?.[i]!==false);
  // Once the grid has drawn, which a grid that has only just been made has not when it is asked, the row of a source index is scrolled to within the grid and then `use`d.
  function whenDrawn(index:number,use:(row:GridRow)=>void,stillCurrent=()=>true){
    if(!alive)return;
    void built.then(()=>new Promise<void>(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve())))).then(()=>{
      if(!stillCurrent())return;
      const row=alive&&ready?table.getRows().find(candidate=>sourceIndex(candidate)===index):undefined,held=focusedDetail();
      // scrollTo rebuilds virtual rows without the table's render events.
      return row?.scrollTo('nearest',false).catch(()=>undefined).then(()=>{if(alive&&stillCurrent()){use(row);restoreDetailFocus(held);}});
    });
  }
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
    /** Marks the row of a source index as the open one (null: none), and keeps it marked as the grid draws its rows again; says so when the filters hide it. */
    selectRow(index:number|null){opened=index;update();if(ready&&alive)for(const row of table.getRows())markOpen(row);},
    /** Retains the detail beneath its source row through sorting and virtual redraws. */
    detailRow(index:number|null,body:HTMLElement){
      const changed=index===null?detail!==undefined:detail?.index!==index||detail?.body!==body;
      if(index===null){detail?.body.remove();detail=undefined;}else if(changed){detail?.body.remove();detail={index,body};}
      sizeDetail();
      if(ready&&alive){
        for(const row of table.getRows()){markOpen(row);row.normalizeHeight();}
        // Removing an offscreen detail also changes the virtual renderer's
        // estimated row height and padding. A full public redraw rebuilds
        // those estimates; retain the person's horizontal and vertical place.
        if(changed&&index===null){const holder=container.querySelector<HTMLElement>('.tabulator-tableholder'),top=holder?.scrollTop??0,left=holder?.scrollLeft??0;table.redraw(true);if(holder){holder.scrollTop=top;holder.scrollLeft=left;}}
      }
    },
    resizeDetail(keepVisible=false){
      sizeDetail();if(!ready||!alive)return;
      const selected=detail,held=focusedDetail()??(keepVisible?selected?.body:undefined),focusAt=focusVersion;
      for(const row of table.getRows())if(sourceIndex(row)===selected?.index)row.normalizeHeight();
      // Returning from a tall editor can leave its source row outside the
      // virtual viewport. Restore only that still-current position, without
      // taking focus from a control the person has moved to in the meantime.
      if(keepVisible&&selected){
        const stillSelected=()=>{const active=document.activeElement;return detail===selected&&!selected.body.hidden&&focusVersion===focusAt&&
          (selected.body.contains(active)||active===null||active===document.body||active===document.documentElement);};
        whenDrawn(selected.index,()=>restoreDetailFocus(held),stillSelected);
      }
    },
    /**
     * Moves focus to the row of a source index, scrolling it into view first, once the grid has drawn again: a change of the
     * grid's width, such as a closing panel's, redraws its rows in the frame after it, which would take focus from a row focused sooner.
     */
    focusRow(index:number){whenDrawn(index,row=>row.getElement().focus({preventScroll:true}));},
    /** Scrolls the row of a source index into view, in the grid and in the page, as far as it takes to show it, once the grid has drawn again. */
    revealRow(index:number){whenDrawn(index,row=>row.getElement().scrollIntoView({block:'nearest'}));},
    destroy(){if(!alive)return;alive=false;resizing.disconnect();gesture=undefined;doubleCandidate=undefined;container.removeEventListener('mousedown',secondMouseDown,true);container.removeEventListener('dblclick',doubleClick,true);container.removeEventListener('pointerdown',pointerDown,true);container.removeEventListener('scroll',keepDetailFocusOnScroll,true);document.removeEventListener('pointermove',pointerMove,true);document.removeEventListener('pointercancel',cancelGesture,true);document.removeEventListener('scroll',cancelGesture,true);document.removeEventListener('focusin',focusChanged,true);if(ready)table.destroy();host.replaceChildren();toolbar.remove();},
  };
}
