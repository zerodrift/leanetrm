// Refusals that prove a first booking or amendment wrote nothing, so its retained request can be released. Every swap
// code is one, except an outcome the API could not confirm.
const knownRejections=new Set(['trade_operation_conflict','futures_revision_conflict','futures_amendment','futures_no_change','futures_trade','futures_conflict','futures_input','futures_lots','futures_execution','futures_convention','futures_book','futures_definition','futures_execution_policy','futures_limit','historical_read_only','access_denied','request_format','request_too_large','request_token','numeric_format','price_domain','quote_specification']);

export function knownTradeRejection(code:string|undefined):boolean{
  return !!code&&(code.startsWith('swap_')&&!code.endsWith('outcome_unknown')||knownRejections.has(code));
}

/** What the page says of a request whose outcome it does not know: which request it is, and what can be done about it. */
export const retainedText=(command:{label?:string})=>`${command.label??'A trade request'} · its outcome is not known. The identical request is retained: check its committed outcome or retry it before entering another booking.`;
