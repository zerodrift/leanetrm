// The curve shapes of markets (blotter-desk.md, Curves connection): the one backend read that shapes them, and what a draft's Price
// before booking draws its markets' curves from. The desk reads the curves of the positions it holds; a draft is for a market that
// is held or not, and is drawn from the desk's read when it has the curve and from one read of its own when it has not.
import type {CurveShape} from './desk-model.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
const unavailable=(why:string)=>`Curve shape unavailable · ${why}`;
/** What a market of another definition revision than the close's curves is given in place of a shape: the close has no curve of its own. */
export const otherRevision=unavailable('this market is defined in another revision than the curves of this close');

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

/** A market a draft is for, with the delivery period the draft marks on its curve, and the definition revision it was chosen from when the draft knows it. */
export type DraftMarket={curve:string;deliveryFrom:string;deliveryTo:string;definitionRevisionId?:string};

/**
 * The shape of each market a draft is for. The desk's own read of its curves is used when it holds the curve, and is waited for while
 * it is under way, so that a curve it is about to bring is never read again; any other market, and a curve the desk holds only as the
 * reason it has none, is read once and kept until `reset`, which the page calls when the desk reads again. A reason is not kept: it may
 * have passed by the next draft.
 */
export function draftShapes(o:{
  /** The desk's curves, once its read of them is over; none when it made no read. */
  desk:()=>Promise<ReadonlyMap<string,CurveShape|string>|undefined>;
  /** The definition revision of the curves of the close. */
  revision:()=>string;
  /** Reads the shape of one market, or says why it has none. */
  read:(curve:string)=>Promise<CurveShape|string>;
}){
  let kept=new Map<string,Promise<CurveShape|string>>();
  return {
    async shape(market:Pick<DraftMarket,'curve'|'definitionRevisionId'>):Promise<CurveShape|string>{
      if(market.definitionRevisionId!==undefined&&market.definitionRevisionId!==o.revision())return otherRevision;
      const held=(await o.desk())?.get(market.curve);
      if(typeof held==='object')return held;
      const mine=kept;let read=mine.get(market.curve);
      if(read===undefined){
        const own=read=o.read(market.curve);mine.set(market.curve,own);
        const forget=()=>{if(mine.get(market.curve)===own)mine.delete(market.curve);};
        own.then(result=>{if(typeof result==='string')forget();},forget);
      }
      return read;
    },
    reset(){kept=new Map();},
  };
}
