// The menu of a Blotter row (blotter-workbench.md, Row context menu): what a trader does with one position. Nothing books from it: Close
// out and the adjustments draft a trade into the reviewed form, and Amend opens the amendment form, through the Blotter's operations.
import type {BlotterOperation} from './blotter-operations.js';
import type {MenuItem} from './context-menu.js';
import {positionActions,type AdjustDirection,type PositionActionId,type PositionCan} from './position-actions.js';

/** What Ask the assistant about this position puts in the assistant's composer, for the person to send or change. */
export const askAboutText=(reference:string)=>`Tell me about position ${reference}: what it holds, what it is worth and its risk.`;
export const askSelectionText='Tell me about these selected positions: what they hold, their value and risk.';
export const askPivotText='Explain this selected pivot cell and the positions behind it.';
export function selectionAskItem(allowed:boolean,run:()=>void):MenuItem{
  return allowed?{label:'Ask about these positions',run}:{label:'Ask about these positions',disabled:'Your role does not include the page assistant.'};
}
/** An item's action: a Blotter operation, or one of the row's own, which the page carries out for the row the menu was opened on. */
export type RowMenuAction=BlotterOperation|{kind:'openWindow'|'copyId'|'ask'|'risk'}|{kind:'adjust';direction:AdjustDirection};
/** What the account may do with the row: its position's actions (position-actions.ts) and asking the assistant about it. */
export type RowMenuCan=PositionCan&{ask:boolean;why?:{ask?:string}};

/**
 * The items of a row's menu, in the order a trader reads them: the position's supported actions, as its details offer them, then the
 * row's own. An action the account may not take is shown with why, never left out or offered to fail.
 */
export function rowMenuItems(row:{key:string;instrumentType:string;reference:string;lots:string|null},can:RowMenuCan,run:(action:RowMenuAction)=>void):MenuItem[]{
  const actionOf:Record<Exclude<PositionActionId,'terminate'|'novate'>,RowMenuAction>={closeOut:{kind:'draftCloseOut',key:row.key},amend:{kind:'amend',key:row.key},
    increase:{kind:'adjust',direction:'increase'},reduce:{kind:'adjust',direction:'reduce'},risk:{kind:'risk'}};
  const own=(label:string,action:RowMenuAction,refused?:string):MenuItem=>can.stale!==undefined?{label,disabled:can.stale}:refused!==undefined?{label,disabled:refused}:{label,run:()=>run(action)};
  const askRefused=()=>{if(can.ask)return undefined;const why=can.why?.ask;if(!why)throw Error('The row menu was given no reason why ask is refused.');return why;};
  return [
    ...positionActions(row,can).flatMap((action):MenuItem[]=>action.state.kind==='soon'?[]:[{label:action.id==='amend'?'Amend…':action.label,
      ...(action.state.kind==='disabled'?{disabled:action.state.reason}:{run:()=>run(actionOf[action.id as keyof typeof actionOf])})}]),
    own('Open in its own window',{kind:'openWindow'}),
    own('Copy trade ID',{kind:'copyId'}),
    own('Ask the assistant about this position',{kind:'ask'},askRefused()),
  ];
}
