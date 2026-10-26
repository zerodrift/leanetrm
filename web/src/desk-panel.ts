// The Blotter desk's use of its side panel: which position the panel shows, found again in each read the desk draws, and what
// the desk does when the person opens a row or closes the panel.
import type {Holding} from './desk-model.js';
import type {PositionInput,PositionTab} from './position-panel.js';
import {sameMode,type PanelMode,type SidePanel} from './side-panel.js';

type PositionKey=Pick<Holding,'tradeId'|'bindingId'|'revision'>;
export type PositionMode=Extract<PanelMode,{kind:'position'}>;

/** A position as the panel names it: a trade's binding at a revision. A trade can hold several bindings, each its own position. */
export const positionMode=(h:PositionKey):PositionMode=>({kind:'position',tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision)});

/** Where a read holds the position a mode names, or -1 when it does not hold it. */
export const positionIndex=(holdings:readonly PositionKey[],mode:PositionMode)=>holdings.findIndex(h=>sameMode(positionMode(h),mode));

type TradeKey=Pick<Holding,'tradeId'|'bindingId'|'instrumentType'>;
/** The rows of a read that hold a trade of a kind: those of its binding when one is named, and otherwise every binding it holds. */
const rowsOfTrade=(holdings:readonly TradeKey[],trade:{tradeId:string;instrumentType:string;bindingId?:string})=>
  holdings.flatMap((h,i)=>h.tradeId===trade.tradeId&&h.instrumentType===trade.instrumentType&&(trade.bindingId===undefined||h.bindingId===trade.bindingId)?[i]:[]);

/**
 * The row of a trade a person or the assistant names: of its binding when one is named, and otherwise of the only position the read
 * holds of it. A trade the read does not hold, or holds as several positions without a binding to tell them apart, is refused.
 */
export function tradeIndex(holdings:readonly TradeKey[],trade:{tradeId:string;instrumentType:string;bindingId?:string}):number{
  const found=rowsOfTrade(holdings,trade);
  if(!found.length)throw Error('That trade is not in this Blotter read. Choose the books it is in, then open it again.');
  if(found.length>1)throw Error('That trade holds several positions in this read, so the panel cannot tell which one is meant.');
  return found[0];
}

/**
 * A trade opens from the trade rows. A pivot's rows are totals, which hold no trade to open, so a target for one is refused there, in
 * words that name the view it needs: the person's view is never switched for them.
 */
export function requireTradeRows(mode:string){
  if(mode!=='trades')throw Error("That trade opens from the Blotter's Individual trades view. Set Display to Individual trades, apply it, and ask again.");
}

/** What a target for another revision than the Blotter holds says, and nothing when it names the one the Blotter holds. */
export function revisionNote(reference:string,target:number|null,held:number):string|null{
  if(target===null||target===held)return null;
  return target<held?`Revision ${target} of ${reference} has been amended since. The panel shows revision ${held}, its latest; the earlier revisions are in History.`
    :`Revision ${target} of ${reference} is newer than this Blotter read, which holds revision ${held}. Refresh the Blotter to read it.`;
}

/**
 * Where the panel follows a trade once an amendment of it is made. An amendment of the instrument gives the trade another binding, so
 * the trade is found by its id and kind: by the binding the amendment produced when its receipt names one, which a read that does not
 * hold it is never answered for with another position of the trade, and otherwise as the only position the read holds of it. Where it
 * is not found, or not told apart, the panel closes and says why; a move to another binding is said too.
 */
export function followedPosition(holdings:readonly TradeKey[],amended:Pick<Holding,'tradeId'|'bindingId'|'instrumentType'|'tradeReference'>,receipt?:{bindingId?:string}):{index:number;note:string|null}|{index:null;note:string}{
  const of=holdings.flatMap((h,i)=>h.tradeId===amended.tradeId&&h.instrumentType===amended.instrumentType?[i]:[]);
  const named=receipt?.bindingId!==undefined;
  const produced=named?of.find(i=>holdings[i].bindingId===receipt.bindingId):of.length===1?of[0]:undefined;
  if(produced!==undefined)return {index:produced,note:holdings[produced].bindingId===amended.bindingId?null:`${amended.tradeReference} now holds another instrument, so the panel follows the trade to the position the amendment produced.`};
  return {index:null,note:!of.length?`${amended.tradeReference} is not in this Blotter read after its amendment, so the panel was closed.`
    :named?`${amended.tradeReference} is not in this Blotter read at the binding the amendment produced, so the panel was closed.`
    :`${amended.tradeReference} holds several positions in this read, so the panel cannot tell which one the amendment produced.`};
}

/**
 * Where the panel shows the trade a booking made, once the Blotter has been read again: the position of the trade the receipt names, of
 * its binding when the receipt names one. A trade the read does not show, because the books it is in are not among those selected, or
 * that it holds as several positions without a binding to tell them apart, is not guessed at: the person is told why nothing opened.
 */
export function bookedPosition(holdings:readonly TradeKey[],receipt:{tradeId?:string;bindingId?:string},instrumentType:string):{index:number;note:null}|{index:null;note:string}{
  if(receipt.tradeId===undefined)return {index:null,note:'The booking was accepted, but its receipt does not name the trade, so the panel cannot show it. Find it in the Blotter.'};
  const found=rowsOfTrade(holdings,{tradeId:receipt.tradeId,instrumentType,...(receipt.bindingId===undefined?{}:{bindingId:receipt.bindingId})});
  if(!found.length)return {index:null,note:'The new trade is booked, but this Blotter read does not show it. The books or portfolios selected may leave out its book.'};
  if(found.length>1)return {index:null,note:'The new trade is booked and holds several positions in this read, so the panel cannot tell which one to show.'};
  return {index:found[0],note:null};
}

/**
 * The panel's position follows the read the desk has drawn. `shell` and `position` are the panel and the view it draws a position
 * with; `holdings` are the positions of the read on screen in the order of the desk's rows, and `input` what the desk drew for
 * one, or nothing when it draws no positions. `grid` marks the open row, brings it into view and takes focus back to it.
 */
export function setupDeskPanel(o:{
  shell:Pick<SidePanel,'show'|'reset'|'current'>;
  position:{show(at:PositionInput,tab?:PositionTab):void;update(at:PositionInput):void;clear():void};
  holdings:()=>readonly Holding[]|undefined;
  input:(index:number)=>PositionInput|undefined;
  grid:()=>{selectRow(index:number|null):void;focusRow(index:number):void;revealRow(index:number):void}|undefined;
  notice:(message:string)=>void;
}){
  // The row the panel was opened from, which focus returns to when the person closes the panel.
  let openedAt:number|undefined;
  function drop(){o.position.clear();openedAt=undefined;o.grid()?.selectRow(null);}
  // Draws the position the panel is on from the read on screen now, or closes the panel, saying so, when that read does not hold it.
  function draw(opening:boolean,tab?:PositionTab){
    const mode=o.shell.current();
    if(mode?.kind!=='position')return;
    const index=positionIndex(o.holdings()??[],mode),at=index<0?undefined:o.input(index);
    if(!at){o.shell.reset();drop();o.notice('The position the panel showed is not in this read, so the panel was closed.');return;}
    openedAt=index;
    if(opening)o.position.show(at,tab);else o.position.update(at);
    o.grid()?.selectRow(index);
    // A position opened by a booking or by the assistant can be a row out of sight: it is shown, as a row the person pressed already is.
    if(opening)o.grid()?.revealRow(index);
  }
  return {
    /** Opens the position of a row, on a tab when one is named. The row that is already open is only given focus: its tab and its reads stay. */
    async open(index:number,tab?:PositionTab){
      const h=o.holdings()?.[index];
      if(!h||!o.input(index))return;
      const mode=positionMode(h),shown=sameMode(o.shell.current(),mode);
      // The person may be asked first, and a newer read may land while they decide: the position is looked up when they have.
      if(await o.shell.show(mode)&&!shown)draw(true,tab);
    },
    /** The workspace row of the position the panel shows now in the read on screen, or nothing. */
    shown(){
      const mode=o.shell.current(),held=o.holdings();
      if(mode?.kind!=='position'||!held)return undefined;
      const index=positionIndex(held,mode);
      return index<0?undefined:held[index];
    },
    /** The desk has drawn its read again, or its curves have arrived. */
    sync:()=>draw(false),
    /** The person closed the panel. */
    closed(){const row=openedAt;drop();if(row!==undefined)o.grid()?.focusRow(row);},
    /** The panel moved to another mode: the position and its row are forgotten, and focus stays where the person put it. */
    left:drop,
    /** The reads the panel was opened from no longer stand. */
    reset(){o.shell.reset();drop();},
  };
}
