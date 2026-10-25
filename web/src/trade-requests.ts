import type {RequestCommand} from './pending-command.js';
// Refusals that prove a first booking or amendment wrote nothing, so its retained request can be released. Every swap
// code is one, except an outcome the API could not confirm.
const knownRejections=new Set(['trade_operation_conflict','futures_revision_conflict','futures_amendment','futures_no_change','futures_trade','futures_conflict','futures_input','futures_lots','futures_execution','futures_convention','futures_book','futures_definition','futures_execution_policy','futures_limit','historical_read_only','access_denied','request_format','request_too_large','request_token','numeric_format','price_domain','quote_specification']);

export function knownTradeRejection(code:string|undefined):boolean{
  return !!code&&((code.startsWith('swap_')||code.startsWith('scheduled_')||code.startsWith('settlement_'))&&!code.endsWith('outcome_unknown')||knownRejections.has(code));
}

/** What the page says of a request whose outcome it does not know: which request it is, and what can be done about it. */
export const retainedText=(command:{label?:string})=>`${command.label??'A trade request'} · its outcome is not known. The identical request is retained: check its committed outcome or retry it before entering another booking.`;

// The requests the Blotter sends whose outcome it may not know: bookings, amendments and settlement commands.
const sentPaths:readonly string[]=['futures/book','futures/amend','swaps/book','swaps/amend','swaps/settlement/determine','swaps/settlement/payment'];
const settlementPath=(path:string)=>path.startsWith('swaps/settlement/');

/** Whether a request kept by an earlier visit is one the Blotter sends, intact, so that it is retained again after a reload. */
export function restorable(saved:unknown):saved is RequestCommand{
  if(!saved||typeof saved!=='object')return false;
  const {path,operationId,body}=saved as Record<string,unknown>;
  return typeof path==='string'&&sentPaths.includes(path)&&typeof operationId==='string'&&!!body&&typeof body==='object';
}

/** Whether the account may retry a retained request: an amendment or a settlement command needs AmendTrade, a booking BookTrade. */
export function retryAllowed(path:string,catalog:{canBook?:boolean;canAmendTrades?:boolean}|undefined):boolean{
  return !!(path.endsWith('/amend')||settlementPath(path)?catalog?.canAmendTrades:catalog?.canBook);
}
