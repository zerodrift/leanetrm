// The curve shapes of markets (blotter-desk.md, Curves connection): the one backend read that shapes them, and what a draft's Price
// before booking draws its markets' curves from. The desk reads the curves of the positions it holds; a draft is for a market that
// is held or not, and is drawn from the desk's read when it has the curve and from one read of its own when it has not.
import type {CurveShape} from './desk-model.js';
export const curvesPerRead=64;

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
const unavailable=(why:string)=>`Curve shape unavailable · ${why}`;
export type CurveSelection=Readonly<{curve:string;definitionRevisionId:string}>;
export const curveSelectionKey=(market:CurveSelection)=>JSON.stringify([market.definitionRevisionId,market.curve]);
const missingDefinition=unavailable('the exact market definition is missing');

/**
 * Reads the shapes of some curves at a close, in the market context the page reads in: the selected historical one, or none for working
 * data, which the read states itself. A curve the read does not return has no entry; a read that fails, or was made in another context
 * than the page's, leaves each curve with the reason, and never fails.
 */
export async function readCurveShapes(api:Api,source:{snapshotId:string;context?:{contextId?:string}},curves:readonly string[]):Promise<Map<string,CurveShape|string>>{
  const found=new Map<string,CurveShape|string>();
  try{
    const read=await api<{curves:CurveShape[];context?:{contextId?:string}}>('market/curve-shapes',{snapshotId:source.snapshotId,curves});
    if(read.context?.contextId!==source.context?.contextId)for(const curve of curves)found.set(curve,unavailable('the curves were read in another market context'));
    else for(const shape of read.curves)if(curves.includes(shape.curve))found.set(shape.curve,shape);
  }catch(error){
    // A role that cannot read market data is told so, rather than that the read failed.
    const why=(error as {code?:string}).code==='access_denied'?'this account cannot read market data':'the curve read failed';
    for(const curve of curves)found.set(curve,unavailable(why));
  }
  return found;
}

/** Inspect exact retained inputs without claiming signed session-root coverage. */
export async function readInputCurveShapes(api:Api,source:{snapshotId:string;context?:{contextId?:string}},markets:readonly CurveSelection[]):Promise<Map<string,CurveShape|string>>{
 const found=new Map<string,CurveShape|string>();
 try{
  if(markets.some(m=>!m.definitionRevisionId||!m.curve))throw Error('Missing exact input coordinates');
  const read=await api<{curves:CurveShape[];context?:{contextId?:string}}>('market/input-curve-shapes',{snapshotId:source.snapshotId,curves:markets.map(({curve,definitionRevisionId})=>({curve,definitionRevisionId}))});
  for(const market of markets){
   const key=curveSelectionKey(market);
   if(read.context?.contextId!==source.context?.contextId){found.set(key,unavailable('the curves were read in another market context'));continue;}
   const matches=read.curves.filter(shape=>shape.curve===market.curve&&shape.definitionRevisionId===market.definitionRevisionId);
   if(matches.length===1)found.set(key,matches[0]);
   else found.set(key,unavailable('the read did not return this exact curve definition'));
  }
 }catch(error){
  const why=(error as {code?:string}).code==='access_denied'?'this account cannot read market data':'the curve read failed';
  for(const market of markets)found.set(curveSelectionKey(market),unavailable(why));
 }
 return found;
}

/** A draft's exact market and the delivery period it marks on the curve. */
export type DraftMarket=CurveSelection&{deliveryFrom:string;deliveryTo:string};

/** Reuse only the same source-qualified desk curve; otherwise inspect that exact input selection. */
export function draftShapes(o:{
 desk:()=>Promise<ReadonlyMap<string,CurveShape|string>|undefined>;
 read:(market:CurveSelection)=>Promise<CurveShape|string>;
}){
 let kept=new Map<string,Promise<CurveShape|string>>();
 const readFailed=unavailable('the curve read failed');
 const held=async()=>{try{return await o.desk();}catch{return undefined;}};
 return {
  async shape(market:CurveSelection):Promise<CurveShape|string>{
   if(!market.definitionRevisionId)return missingDefinition;
   const key=curveSelectionKey(market),desk=await held(),fromDesk=desk?.get(key);
   if(typeof fromDesk==='object'&&fromDesk.definitionRevisionId===market.definitionRevisionId)return fromDesk;
   const mine=kept;let read=mine.get(key);
   if(read===undefined){
    const own=read=(async()=>o.read(market))().catch(()=>readFailed);mine.set(key,own);
    own.then(result=>{if(typeof result==='string'&&mine.get(key)===own)mine.delete(key);});
   }
   return read;
  },
  reset(){kept=new Map();},
 };
}
