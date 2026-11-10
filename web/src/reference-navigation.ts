import {foldedIntoProduct} from './data-sections.js';
import {formatReferenceAddress} from './reference-address.js';
// Presentation only: the results of the cross-category search of reference data. The backend searches,
// resolves and authorizes; this lists what it returned and links each hit to its item's address.
export type ReferenceHit={table:string;code:string;label:string;productCode:string|null;summary:{label:string;value:string|null}[]};
type Section={readonly value:string;readonly name:string};
type Group={readonly name:string;readonly sections:readonly Section[]};
type Listed={label:string;detail:string;summary:string;href:string};
type Options={host:HTMLElement;groups:()=>readonly Group[]};

// The backend's one-line summary of a hit, such as a futures product's exchange, location, lot size and quote unit.
function summaryLine(summary:unknown){
  if(!Array.isArray(summary)||!summary.every(f=>typeof f?.label==='string'&&(f.value===null||typeof f.value==='string')))
    throw Error('A search result has no valid summary. Refresh and try again.');
  return (summary as ReferenceHit['summary']).map(f=>`${f.label}: ${f.value??'Not supplied'}`).join(' · ');
}
// Hits in the order of their categories, under each category's name. A futures contract belongs to its product, so it
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
  const results=o.host.querySelector<HTMLElement>('#reference-search-hits')!;
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
  function clear(){results.replaceChildren();results.hidden=true;}
  clear();
  return {showHits,clear};
}
