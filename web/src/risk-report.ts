export type RiskAmounts={base:string;up:string;down:string;upPnl:string;downPnl:string;delta:string;gamma:string};
export type RiskValues=RiskAmounts&{display:RiskAmounts;currency:string;currencyReference:unknown;deltaUnit:string;gammaUnit:string};
export type RiskTotal={bookId:string;book:string;measure:string;measureLabel:string;values:RiskValues};

export function riskTotalGroups(rows:RiskTotal[]){
  const groups=new Map<string,RiskTotal[]>();
  for(const row of rows){
    const key=JSON.stringify([row.measure,row.values.currencyReference,row.values.deltaUnit,row.values.gammaUnit]);
    const group=groups.get(key)??[];group.push(row);groups.set(key,group);
  }
  return [...groups.values()];
}
