import type {PivotCellFocus} from './assistant-pages.js';

// Copy only native group identities from the server projection, never a label or cell amount.
export function pivotCellFocus(group:Record<string,unknown>,dimensions:readonly string[],measure:string):PivotCellFocus{
  const fields=[...new Set([...dimensions.map(d=>d==='book'?'bookId':d==='portfolio'?'portfolioId':d==='contract'?'contractId':d),
    'groupId','instrumentType','quantitySpecification','currencyReference',...(dimensions.some(d=>d==='market'||d==='contract')?['definitionRevisionId']:[])])];
  const keys:Record<string,unknown>={};
  for(const field of fields){
    if(!Object.hasOwn(group,field))throw Error('This pivot cell has no exact server identity. Refresh it before asking.');
    keys[field]=structuredClone(field==='commodity'?(group.commodity as {code:string}).code:group[field]);
  }
  return {keys,measure};
}
