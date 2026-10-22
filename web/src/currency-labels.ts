// A currency as the reader's own scoped read of reference data names it (D124): its exact reference and its labels.
export type CurrencyReference={catalogId:string;revisionId:string;code:string};
export type Currency=CurrencyReference&{displayCode:string;name:string;catalogCode:string;revisionKey:string};

export const currencyKey=(c:CurrencyReference)=>JSON.stringify([c.catalogId,c.revisionId,c.code]);
// A label names its catalog, revision and code only when two currencies would otherwise read the same.
export function currencyLabels(choices:Currency[]){
  const plain=(c:Currency)=>`${c.displayCode} · ${c.name}`,counts=new Map<string,number>();
  for(const c of choices)counts.set(plain(c),(counts.get(plain(c))??0)+1);
  return new Map(choices.map(c=>[currencyKey(c),counts.get(plain(c))!>1?`${plain(c)} (${c.catalogCode} ${c.revisionKey} ${c.code})`:plain(c)]));
}
