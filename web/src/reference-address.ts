import {referenceGroups} from './data-sections.js';
// The reference page address: #reference/<table>[/<code>][?q=<search>]. Table names come only from the fixed section list.
export type ReferenceAddress={table:string;code?:string;search?:string};
const tables=new Set<string>(referenceGroups.flatMap(g=>g.sections.map(s=>s.value)));
export function formatReferenceAddress(a:ReferenceAddress){return `#reference/${a.table}${a.code===undefined?'':'/'+encodeURIComponent(a.code)}${a.search===undefined?'':'?q='+encodeURIComponent(a.search)}`;}
export function parseReferenceAddress(hash:string):ReferenceAddress|undefined{
  const match=/^#reference\/([^/?]+)(?:\/([^/?]+))?(?:\?q=(.+))?$/.exec(hash);
  if(!match||!tables.has(match[1]))return undefined;
  try{
    const address:ReferenceAddress={table:match[1]};
    if(match[2]!==undefined)address.code=decodeURIComponent(match[2]);
    if(match[3]!==undefined)address.search=decodeURIComponent(match[3]);
    return address;
  }catch{return undefined;}
}
