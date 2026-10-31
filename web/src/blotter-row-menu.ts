// The menu of a Blotter row (blotter-workbench.md, Row context menu): what a trader does with one position. Nothing books from it: Close
// out drafts the offsetting trade into the reviewed form, and Amend opens the amendment form, both through the Blotter's operations.
import type {BlotterOperation} from './blotter-operations.js';
import type {MenuItem} from './context-menu.js';

/** What Ask the assistant about this position puts in the assistant's composer, for the person to send or change. */
export const askAboutText=(reference:string)=>`Tell me about position ${reference}: what it holds, what it is worth and its risk.`;
/** An item's action: a Blotter operation, or one of the row's own, which the page carries out for the row the menu was opened on. */
export type RowMenuAction=BlotterOperation|{kind:'openWindow'|'copyId'|'ask'};
type Refusable='book'|'amend'|'ask';
/**
 * What the account may do with the row, each refusal with its reason in `why`; `stale`, when the row's values are not current, is why
 * nothing is offered at all.
 */
export type RowMenuCan=Record<Refusable,boolean>&{why?:Partial<Record<Refusable,string>>;stale?:string};

/**
 * The items of a row's menu, in the order a trader reads them. An action the account may not take is shown with why, never left out or
 * offered to fail. Close out is offered whatever the instrument: what the backend cannot draft it says in the form, not guessed here.
 */
export function rowMenuItems(row:{key:string;instrumentType:string;reference:string},can:RowMenuCan,run:(action:RowMenuAction)=>void):MenuItem[]{
  const reason=(what:Refusable)=>{const why=can.why?.[what];if(!why)throw Error(`The row menu was given no reason why ${what} is refused.`);return why;};
  const item=(label:string,action:RowMenuAction,allowed=true,what?:Refusable):MenuItem=>
    can.stale!==undefined?{label,disabled:can.stale}:allowed?{label,run:()=>run(action)}:{label,disabled:reason(what!)};
  return [
    item('Close out…',{kind:'draftCloseOut',key:row.key},can.book,'book'),
    item('Amend…',{kind:'amend',key:row.key},can.amend,'amend'),
    item('Open in its own window',{kind:'openWindow'}),
    item('Copy trade ID',{kind:'copyId'}),
    item('Ask the assistant about this position',{kind:'ask'},can.ask,'ask'),
  ];
}
