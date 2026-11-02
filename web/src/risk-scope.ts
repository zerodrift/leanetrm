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
