/** Captured selection identity. Legacy absent tradeIds and explicit null both mean all trades. */
export type RiskScope={bookIds:readonly string[];portfolioIds:readonly string[];tradeIds?:readonly string[]|null};
const sameIds=(a:readonly string[],b:readonly string[])=>{const x=[...a].sort(),y=[...b].sort();return x.length===y.length&&x.every((id,i)=>id===y[i]);};
export function sameTradeSelection(a:readonly string[]|null|undefined,b:readonly string[]|null|undefined):boolean{
 if(a==null||b==null)return a==null&&b==null;
 return a.length>0&&b.length>0&&sameIds(a,b);
}
export function sameRiskScope(a:RiskScope,b:RiskScope):boolean{
 return sameIds(a.bookIds,b.bookIds)&&sameIds(a.portfolioIds,b.portfolioIds)&&sameTradeSelection(a.tradeIds,b.tradeIds);
}

// The scope bar's one line: an empty selection stated as what it means.
export function riskScopeSummary(scope:{portfolios:readonly string[];books:readonly string[];trades:number|null}):string{
 const named=(names:readonly string[],all:string,plural:string)=>names.length===0?all:names.length===1?names[0]:`${names.length} ${plural}`;
 const trades=scope.trades===null?'All trades in these books':scope.trades===0?'No trades chosen':scope.trades===1?'1 trade':`${scope.trades} trades`;
 return [named(scope.portfolios,'All portfolios','portfolios'),named(scope.books,'All readable books','books'),trades].join(' · ');
}
