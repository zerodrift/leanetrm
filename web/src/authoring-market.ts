export type AuthoringMarket={revisionId:string;name:string};
export type AuthoringMarketCatalog={marketRevisionId?:string;markets?:AuthoringMarket[]};
/** A source bundle is selected by the server or the person, never by curve code or array order. */
export function authoringMarket(catalog:AuthoringMarketCatalog,requested?:string){
 if(!requested&&catalog.marketRevisionId===undefined&&catalog.markets===undefined)return {revisionId:undefined,markets:[] as AuthoringMarket[]};
 if(typeof catalog.marketRevisionId!=='string'||!catalog.marketRevisionId||!Array.isArray(catalog.markets)||
  catalog.markets.some(m=>!m||typeof m.revisionId!=='string'||!m.revisionId||typeof m.name!=='string'||!m.name)||
  new Set(catalog.markets.map(m=>m.revisionId)).size!==catalog.markets.length||!catalog.markets.some(m=>m.revisionId===catalog.marketRevisionId)||requested!==undefined&&requested!==catalog.marketRevisionId)
  throw Error('The available source markets are missing or inconsistent. Reload before authoring.');
 return {revisionId:catalog.marketRevisionId,markets:catalog.markets};
}
export const authoringMarketField=(marketRevisionId:string|undefined)=>marketRevisionId?{marketRevisionId}:{};
/** Reused contract and curve codes do not acknowledge a write to another exact source bundle. */
export function acknowledgesAuthoringMarket(receipt:unknown,request:Record<string,unknown>){
 if(request.marketRevisionId===undefined||request.marketRevisionId===null)return true;
 return typeof request.marketRevisionId==='string'&&request.marketRevisionId.length>0&&receipt!==null&&typeof receipt==='object'&&
  (receipt as Record<string,unknown>).marketRevisionId===request.marketRevisionId;
}
