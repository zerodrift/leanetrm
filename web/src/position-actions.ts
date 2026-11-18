// What a trader does with one booked position (blotter-workbench.md, Row context menu and Details head), named once for the row's menu
// and the details' buttons. Nothing here decides a term: an adjustment's direction is relative to the position, and the backend drafts it.

/** An adjustment of a position, relative to it: more of the same side, or less. */
export type AdjustDirection='increase'|'reduce';
export const adjustDirections:readonly AdjustDirection[]=['increase','reduce'];

type Refusable='book'|'amend'|'risk';
/**
 * What the account may do with the position, each refusal with its reason in `why`; `stale`, when the row's values are not current, is
 * why nothing is offered at all.
 */
export type PositionCan=Record<Refusable,boolean>&{why?:Partial<Record<Refusable,string>>;stale?:string};
export type PositionActionId='closeOut'|'amend'|AdjustDirection|'risk'|'terminate'|'novate';
/** An action as it is offered: `soon` is a lifecycle event the backend does not support yet, which is drawn and never sent. */
export type PositionAction={id:PositionActionId;label:string;state:{kind:'enabled'}|{kind:'disabled';reason:string}|{kind:'soon'}};

/**
 * The names of the two adjustments, from the direction the position itself states: a future's signed lots say whether more of it is a
 * purchase or a sale. A position that states no lots is not guessed long or short, and its adjustments keep their neutral names.
 */
export function adjustLabels(row:{instrumentType:string;lots:string|null}):Record<AdjustDirection,string>{
  if(row.instrumentType!=='future'||row.lots===null)return {increase:'Increase…',reduce:'Reduce…'};
  return row.lots.startsWith('-')?{increase:'Sell more…',reduce:'Buy back…'}:{increase:'Buy more…',reduce:'Sell…'};
}
/** The unit an adjustment's quantity is asked in: lots where the position states lots, its own unit otherwise. */
export const adjustUnit=(row:{lots:string|null;unit:string}):string=>row.lots===null?row.unit:'lots';

/**
 * The position's actions in the order a trader reads them. An action the account may not take is listed with why, never left out or
 * offered to fail; the drafts are offered whatever the instrument, since what the backend cannot draft it says in the details.
 */
export function positionActions(row:{instrumentType:string;lots:string|null},can:PositionCan):PositionAction[]{
  const state=(what:Refusable):PositionAction['state']=>{
    if(can.stale!==undefined)return {kind:'disabled',reason:can.stale};
    if(can[what])return {kind:'enabled'};
    const reason=can.why?.[what];
    if(!reason)throw Error(`A position's actions were given no reason why ${what} is refused.`);
    return {kind:'disabled',reason};
  };
  const labels=adjustLabels(row);
  return [
    {id:'closeOut',label:'Close out…',state:state('book')},
    {id:'amend',label:'Amend',state:state('amend')},
    {id:'increase',label:labels.increase,state:state('book')},
    {id:'reduce',label:labels.reduce,state:state('book')},
    {id:'risk',label:'Risk…',state:state('risk')},
    {id:'terminate',label:'Terminate',state:{kind:'soon'}},
    {id:'novate',label:'Novate',state:{kind:'soon'}},
  ];
}
