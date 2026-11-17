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

/**
 * The scope the Blotter's risk report asks for: the books and portfolios of the read on screen, narrowed to one trade when the person
 * asked for that trade's risk. No trade named means every trade of those books, which is stated by leaving the trades out. `held` says
 * whether the read on screen holds a trade.
 */
export function blotterRiskScope(context:Record<string,unknown>,view:{bookIds:readonly string[];portfolioIds:readonly string[]},trade:{tradeId:string;reference:string}|null,held:(tradeId:string)=>boolean){
 // A trade the read on screen no longer holds is refused by name: its books are never asked for in its place.
 if(trade&&!held(trade.tradeId))throw Error(`Trade ${trade.reference} is no longer among the positions on screen. Choose Run risk to set up risk for the books on screen.`);
 return {context:{...context},bookIds:[...view.bookIds],portfolioIds:[...view.portfolioIds],...(trade?{tradeIds:[trade.tradeId]}:{})};
}
/** Whether a run's scope names trades and this one is not among them: such a position is not in the run, which is not the same as unvalued. */
export const outsideRunScope=(scope:Pick<RiskScope,'tradeIds'>|undefined,tradeId:string):boolean=>scope?.tradeIds!=null&&!scope.tradeIds.includes(tradeId);
/** What a run that names trades says it covers; nothing for a run of whole books. `reference` names a trade the page can name. */
export function tradeScopeNote(scope:Pick<RiskScope,'tradeIds'>|undefined,reference:(tradeId:string)=>string|undefined):string|null{
 if(scope?.tradeIds==null)return null;
 const named=scope.tradeIds.length===1?reference(scope.tradeIds[0]):undefined;
 return `This run covers ${named?`trade ${named}`:scope.tradeIds.length===1?'one chosen trade':`${scope.tradeIds.length} chosen trades`} only; no other position is in it.`;
}
/** What the page says when what risk is set up for changes and that takes a run off the desk; nothing when the desk held none. */
export const scopeChangeNotice=(hadRun:boolean,trade:{reference:string}|null):string|null=>
 hadRun?`The risk run on the desk was cleared: risk is now set up for ${trade?`trade ${trade.reference} only`:'the books on screen'}.`:null;
