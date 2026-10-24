// The Blotter desk's use of its side panel: which position the panel shows, found again in each read the desk draws, and what
// the desk does when the person opens a row or closes the panel.
import type {Holding} from './desk-model.js';
import type {PositionInput} from './position-panel.js';
import {sameMode,type PanelMode,type SidePanel} from './side-panel.js';

type PositionKey=Pick<Holding,'tradeId'|'bindingId'|'revision'>;
export type PositionMode=Extract<PanelMode,{kind:'position'}>;

/** A position as the panel names it: a trade's binding at a revision. A trade can hold several bindings, each its own position. */
export const positionMode=(h:PositionKey):PositionMode=>({kind:'position',tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision)});

/** Where a read holds the position a mode names, or -1 when it does not hold it. */
export const positionIndex=(holdings:readonly PositionKey[],mode:PositionMode)=>holdings.findIndex(h=>sameMode(positionMode(h),mode));

/**
 * The panel's position follows the read the desk has drawn. `shell` and `position` are the panel and the view it draws a position
 * with; `holdings` are the positions of the read on screen in the order of the desk's rows, and `input` what the desk drew for
 * one, or nothing when it draws no positions. `grid` marks the open row and takes focus back to it.
 */
export function setupDeskPanel(o:{
  shell:Pick<SidePanel,'show'|'reset'|'current'>;
  position:{show(at:PositionInput):void;update(at:PositionInput):void;clear():void};
  holdings:()=>readonly Holding[]|undefined;
  input:(index:number)=>PositionInput|undefined;
  grid:()=>{selectRow(index:number|null):void;focusRow(index:number):void}|undefined;
  notice:(message:string)=>void;
}){
  // The row the panel was opened from, which focus returns to when the person closes the panel.
  let openedAt:number|undefined;
  function drop(){o.position.clear();openedAt=undefined;o.grid()?.selectRow(null);}
  // Draws the position the panel is on from the read on screen now, or closes the panel, saying so, when that read does not hold it.
  function draw(opening:boolean){
    const mode=o.shell.current();
    if(mode?.kind!=='position')return;
    const index=positionIndex(o.holdings()??[],mode),at=index<0?undefined:o.input(index);
    if(!at){o.shell.reset();drop();o.notice('The position the panel showed is not in this read, so the panel was closed.');return;}
    openedAt=index;
    if(opening)o.position.show(at);else o.position.update(at);
    o.grid()?.selectRow(index);
  }
  return {
    /** Opens the position of a row. The row that is already open is only given focus: its tab and its reads stay. */
    async open(index:number){
      const h=o.holdings()?.[index];
      if(!h||!o.input(index))return;
      const mode=positionMode(h),shown=sameMode(o.shell.current(),mode);
      // The person may be asked first, and a newer read may land while they decide: the position is looked up when they have.
      if(await o.shell.show(mode)&&!shown)draw(true);
    },
    /** The desk has drawn its read again, or its curves have arrived. */
    sync:()=>draw(false),
    /** The person closed the panel. */
    closed(){const row=openedAt;drop();if(row!==undefined)o.grid()?.focusRow(row);},
    /** The reads the panel was opened from no longer stand. */
    reset(){o.shell.reset();drop();},
  };
}
