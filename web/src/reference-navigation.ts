import {foldedIntoProduct} from './data-sections.js';
import {formatReferenceAddress} from './reference-address.js';
import {setupTabs,clearTabs} from './side-panel.js';
// Presentation only: the reference page's group tabs, category tabs and the cross-category search box. The backend searches,
// resolves and authorizes; this lists what it returned and links each hit to its item's address.
export type ReferenceHit={table:string;code:string;label:string;productCode:string|null;summary:{label:string;value:string|null}[]};
type Section={readonly value:string;readonly name:string};
type Group={readonly name:string;readonly sections:readonly Section[]};
type Listed={label:string;detail:string;summary:string;href:string};
type Options={host:HTMLElement;groups:()=>readonly Group[];onSelect:(table:string)=>Promise<void>;onSearch:(q:string)=>Promise<void>};

// The backend's one-line summary of a hit, such as a futures product's exchange, location, lot size and quote unit.
function summaryLine(summary:unknown){
  if(!Array.isArray(summary)||!summary.every(f=>typeof f?.label==='string'&&(f.value===null||typeof f.value==='string')))
    throw Error('A search result has no valid summary. Refresh and try again.');
  return (summary as ReferenceHit['summary']).map(f=>`${f.label}: ${f.value??'Not supplied'}`).join(' · ');
}
// Hits in the order of their categories' tabs, under each category's name. A futures contract belongs to its product, so it
// is listed and linked under Futures.
export function groupReferenceHits(hits:readonly ReferenceHit[],groups:readonly Group[]){
  const sections=groups.flatMap(g=>g.sections),listed=new Map<string,Listed[]>();
  for(const hit of hits){
    const folded=(foldedIntoProduct as readonly string[]).includes(hit.table);
    if(folded&&!hit.productCode)throw Error('A futures contract in the search results names no product. Refresh and try again.');
    const table=folded?'futures_products':hit.table;
    if(!sections.some(s=>s.value===table))throw Error('The search returned a category that is not available here. Refresh and try again.');
    const entry={label:hit.label,detail:folded?`Contract ${hit.code}`:hit.code,summary:summaryLine(hit.summary),href:formatReferenceAddress({table,code:folded?hit.productCode!:hit.code})};
    listed.set(table,[...listed.get(table)??[],entry]);
  }
  return sections.filter(s=>listed.has(s.value)).map(s=>({name:s.name,hits:listed.get(s.value)!}));
}

export function setupReferenceNavigation(o:Options){
  const part=(id:string)=>o.host.querySelector<HTMLElement>('#'+id)!;
  const groupTabs=part('reference-groups'),groupPanel=part('reference-group-panel'),sectionTabs=part('reference-sections'),panel=part('reference-panel');
  const form=part('reference-search-all') as HTMLFormElement,text=part('reference-search-all-text') as HTMLInputElement,results=part('reference-search-hits');
  let shown='',listed=false;
  // Another group opens on its first category; the chosen group keeps the category shown. `list` says whether the category's
  // list is shown, or one of its items, whose tab then returns to the list.
  function select(table:string,list:boolean){
    const groups=o.groups(),index=groups.findIndex(g=>g.sections.some(s=>s.value===table));
    if(index<0)throw Error('That reference category is not available here.');
    shown=table;listed=list;
    setupTabs(groupTabs,groups.map((g,i)=>[String(i),g.name] as const),{label:'Reference groups',panel:groupPanel,selected:String(index),
      select:id=>{const group=groups[Number(id)];if(!group.sections.some(s=>s.value===shown))choose(group.sections[0].value);}});
    setupTabs(sectionTabs,groups[index].sections.map(s=>[s.value,s.name] as const),{label:groups[index].name,panel,selected:table,select:choose});
  }
  // Tabs are disabled while the category is read, which takes focus from the one a key moved to; it is given back afterwards.
  function choose(table:string){
    if(table===shown&&listed)return;
    const strip=[groupTabs,sectionTabs].find(list=>list.contains(document.activeElement));
    void o.onSelect(table).then(()=>strip?.querySelector<HTMLElement>('[aria-selected=true]')?.focus());
  }
  function showHits(hits:readonly ReferenceHit[],truncated:boolean){
    const grouped=groupReferenceHits(hits,o.groups());
    const node=<K extends keyof HTMLElementTagNameMap>(tag:K,content=''):HTMLElementTagNameMap[K]=>{const n=document.createElement(tag);n.textContent=content;return n;};
    results.replaceChildren(...grouped.length?grouped.map(group=>{
      const list=node('ul');
      list.append(...group.hits.map(hit=>{const item=node('li'),link=node('a',hit.label);link.href=hit.href;item.append(link,node('span',hit.detail));if(hit.summary)item.append(node('p',hit.summary));return item;}));
      const section=node('section');section.append(node('h3',group.name),list);return section;
    }):[node('p','No reference data matches this search.')]);
    if(truncated)results.append(node('p','Only the first matches are listed. Refine your search to see others.'));
    results.hidden=false;
  }
  function clear(){shown='';listed=false;clearTabs(groupTabs,groupPanel);clearTabs(sectionTabs,panel);text.value='';results.replaceChildren();results.hidden=true;}
  // A new search first takes away the results of the one before.
  form.onsubmit=event=>{event.preventDefault();results.replaceChildren();results.hidden=true;void o.onSearch(text.value);};
  clear();
  return {select,showHits,clear};
}
