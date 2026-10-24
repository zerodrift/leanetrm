import {publicText} from './market-view.js';
import {compareDecimalStrings} from './analytics-model.js';

type Cell=string|HTMLElement;
type Filters={scope:string;query:string;columns:(string|null)[];expanded:boolean};
const filters=new Map<string,Filters>();
const clean=(value:string)=>publicText(value).toLocaleLowerCase('en').trim();
export function validNumericFilter(filter:string){return filter===''||/^\s*(?:<=|>=|!=|=|<|>)?\s*-?\d+(?:\.\d+)?\s*$/.test(filter);}
function matchesNumericFilter(value:string,filter:string){
  if(filter==='')return value==='';
  const parsed=/^\s*(<=|>=|!=|=|<|>)?\s*(-?\d+(?:\.\d+)?)\s*$/.exec(filter);
  if(!parsed||!value)return false;
  const compared=compareDecimalStrings(value,parsed[2]);
  switch(parsed[1]??'='){case '<':return compared<0;case '<=':return compared<=0;case '>':return compared>0;case '>=':return compared>=0;case '!=':return compared!==0;default:return compared===0;}
}
export function matchesTableRow(cells:string[],query:string,columns:(string|null)[],visible?:boolean[],numeric:number[]=[]){
  const shown=cells.map(publicText),values=shown.flatMap((value,i)=>visible?.[i]===false?[]:[clean(value)]),terms=clean(query).split(/\s+/).filter(Boolean);
  return terms.every(term=>values.some(value=>value.includes(term)))&&columns.every((term,i)=>term===null||(numeric.includes(i)?matchesNumericFilter(shown[i]??'',term):shown[i]===term));
}
export const columnValues=(values:string[])=>[...new Set(values.map(publicText))];
export function clearTableFilters(){filters.clear();}

// Pure presentation over the already-authorized displayed values. This renderer
// is used only by ordinary tables; the connected worksheet has its own renderer.
export function renderRegularTable(host:HTMLElement,key:string,scope:string,headers:string[],rows:Cell[][],numeric:number[],isBusy:()=>boolean,options?:{displayRows?:string[][];visible?:boolean[];initial?:{query:string;columns:(string|null)[]};retainMissingFilters?:boolean;noColumnFilter?:number[];onRowActivate?:(index:number)=>void}){
  let saved=filters.get(key);
  if(!saved||saved.scope!==scope||saved.columns.length!==headers.length){saved={scope,query:options?.initial?.query??'',columns:options?.initial?.columns.slice()??headers.map(()=>null),expanded:false};filters.set(key,saved);}
  const state=saved;
  const current=()=>host.isConnected&&toolbar.isConnected&&toolbar.parentElement===host&&filters.get(key)===state&&!isBusy();
  const el=<K extends keyof HTMLElementTagNameMap>(tag:K,value?:string)=>{const n=document.createElement(tag);if(value!==undefined)n.textContent=publicText(value);return n;};
  const toolbar=el('div');toolbar.className='table-filters';
  const search=el('input');search.type='search';search.value=state.query;search.placeholder='Filter rows…';search.setAttribute('aria-label','Filter table rows');search.autocomplete='off';search.className='table-search';
  const columnToggle=el('button','Column filters');columnToggle.type='button';columnToggle.className='secondary column-filter-toggle';columnToggle.setAttribute('aria-expanded',String(state.expanded));
  const clear=el('button','Clear filters');clear.type='button';clear.className='text-button table-filter-clear';
  const count=el('span');count.className='table-row-count';count.setAttribute('aria-live','polite');
  toolbar.append(search,columnToggle,clear,count);
  const scroll=el('div');scroll.className='regular-table-scroll';const table=el('table'),thead=el('thead'),heading=el('tr'),columnRow=el('tr');columnRow.className='column-filter-row';columnRow.id='column-filters-'+key;columnToggle.setAttribute('aria-controls',columnRow.id);columnRow.hidden=!state.expanded;
  const inputs:HTMLSelectElement[]=[];
  headers.forEach((name,i)=>{const th=el('th',name);th.scope='col';if(numeric.includes(i))th.className='numeric';th.hidden=options?.visible?.[i]===false;heading.append(th);const filter=el('th');filter.hidden=th.hidden;
    // A column filter lists each distinct value; a caller opts out a column whose values are nearly all distinct.
    if(name&&!options?.noColumnFilter?.includes(i)){const input=el('select'),all=el('option','All values');all.value='';input.append(all);
      const choices=columnValues(rows.map(r=>typeof r[i]==='string'?r[i] as string:r[i]?.textContent??''));
      choices.forEach((value,index)=>{const option=el('option',value===''?'(Blank)':value);option.value=String(index);input.append(option);});
      if(state.columns[i]!==null&&!choices.includes(state.columns[i]!)){if(options?.retainMissingFilters){choices.push(state.columns[i]!);const option=el('option',state.columns[i]!+' (unavailable)');option.value=String(choices.length-1);input.append(option);}else state.columns[i]=null;}
      input.value=state.columns[i]===null?'':String(choices.indexOf(state.columns[i]!));input.setAttribute('aria-label','Filter '+name);
      input.onchange=()=>{if(!current())return;state.columns[i]=input.value===''?null:choices[input.selectedIndex-1];apply();};filter.append(input);inputs[i]=input;}columnRow.append(filter);});
  thead.append(heading,columnRow);table.append(thead);const tbody=el('tbody'),elements:HTMLTableRowElement[]=[],searchable:string[][]=[];
  rows.forEach(values=>{const tr=el('tr');values.forEach((value,i)=>{const td=el('td');if(typeof value==='string'){td.textContent=publicText(options?.displayRows?.[elements.length]?.[i]??value);if(options?.displayRows?.[elements.length]?.[i]!==undefined)td.title=value;}else td.append(value);if(numeric.includes(i))td.className='numeric';td.hidden=options?.visible?.[i]===false;tr.append(td);});tbody.append(tr);elements.push(tr);searchable.push(values.map((v,i)=>headers[i]?publicText(typeof v==='string'?v:v.textContent??''):''));});
  table.append(tbody);scroll.append(table);const empty=el('p');empty.className='table-filter-empty';empty.setAttribute('role','status');
  // For a caller that acts on one row at a time: a click, Enter or Space activates a row and marks it current.
  // The shown rows share one tab stop, which the Up and Down keys move.
  const activate=options?.onRowActivate;let stop:HTMLTableRowElement|undefined,chosen:HTMLTableRowElement|undefined;
  const moveStop=(row?:HTMLTableRowElement)=>{if(row===stop)return;stop?.removeAttribute('tabindex');stop=row;row?.setAttribute('tabindex','0');};
  if(activate){
    table.classList.add('activatable-rows');
    const choose=(row:HTMLTableRowElement)=>{if(!current())return;chosen?.removeAttribute('aria-current');chosen=row;row.setAttribute('aria-current','true');moveStop(row);activate(row.sectionRowIndex);};
    tbody.onclick=event=>{const row=(event.target as Element).closest('tr');if(row?.parentElement===tbody)choose(row);};
    tbody.onkeydown=event=>{
      const row=event.target as HTMLTableRowElement;
      if(row.parentElement!==tbody||!['Enter',' ','ArrowDown','ArrowUp'].includes(event.key))return;
      event.preventDefault();
      if(event.key==='Enter'||event.key===' '){choose(row);return;}
      let next:Element|null=row;
      do next=event.key==='ArrowDown'?next.nextElementSibling:next.previousElementSibling;while(next instanceof HTMLTableRowElement&&next.hidden);
      if(next instanceof HTMLTableRowElement){moveStop(next);next.focus();}
    };
  }
  function apply(){let visible=0;for(let i=0;i<rows.length;i++){const match=matchesTableRow(searchable[i],state.query,state.columns,options?.visible);elements[i].hidden=!match;if(match)visible++;}
    count.textContent=`${visible} of ${rows.length} rows`;clear.hidden=!state.query&&!state.columns.some(v=>v!==null);empty.hidden=visible>0;
    empty.textContent=rows.length?'No rows match your filters.':'No details to show.';
    if(activate&&(!stop||stop.hidden))moveStop(elements.find(row=>!row.hidden));
  }
  search.oninput=()=>{if(!current())return;state.query=search.value;apply();};
  columnToggle.onclick=()=>{if(!current())return;state.expanded=!state.expanded;columnToggle.setAttribute('aria-expanded',String(state.expanded));columnRow.hidden=!state.expanded;if(state.expanded)inputs.find(Boolean)?.focus();};
  clear.onclick=()=>{if(!current())return;state.query='';state.columns=state.columns.map(()=>null);search.value='';inputs.forEach(input=>{if(input)input.value='';});apply();search.focus();};
  for(const control of [search,columnToggle,clear,...inputs.filter(Boolean)])control.disabled=isBusy();
  host.replaceChildren(toolbar,scroll,empty);apply();
  return {state:()=>({query:state.query,columns:state.columns.slice()}),visibleRows:()=>{
    if(!current())throw Error('Refresh this table before exporting.');
    return searchable.filter(row=>matchesTableRow(row,state.query,state.columns,options?.visible)).map(row=>row.filter((_,i)=>options?.visible?.[i]!==false));
  }};
}
