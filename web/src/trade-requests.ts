// Refusals that prove a first booking or amendment wrote nothing, so its retained request can be released. Every swap
// code is one, except an outcome the API could not confirm.
const knownRejections=new Set(['trade_operation_conflict','futures_revision_conflict','futures_amendment','futures_no_change','futures_trade','futures_conflict','futures_input','futures_lots','futures_execution','futures_convention','futures_book','futures_definition','futures_execution_policy','futures_limit','historical_read_only','access_denied','request_format','request_too_large','request_token','numeric_format','price_domain','quote_specification']);

export function knownTradeRejection(code:string|undefined):boolean{
  return !!code&&((code.startsWith('swap_')||code.startsWith('scheduled_')||code.startsWith('quantity_factor')||code==='quantity_mapping'||code==='quantity_conversion'||code==='cross_commodity_swap'||code.startsWith('settlement_'))&&!code.endsWith('outcome_unknown')||knownRejections.has(code));
}

// Refusals that say nothing of a retried original: of the caller rather than of the request (its access, its request token, or a
// historical view it reads), and of an operation identity the backend holds for another actor or payload, which may be the original's.
const unprovenOnRetry=new Set(['access_denied','request_token','historical_read_only','trade_operation_conflict','settlement_operation_conflict']);
// The requests whose backend answers a retry with the original's outcome before it refuses one: swap bookings, amendments and
// settlement commands. A futures booking or amendment is admitted (eligibility, the trading interval) before its earlier outcome is
// looked up, so a refused retry of one says nothing of the original.
const replaysFirst=(path:string)=>path.startsWith('swaps/');

/**
 * Whether a refusal releases the retained request it answers: a refusal for good means nothing was written, on a first send, and on a
 * retry where the backend replays the original's outcome before it refuses, unless the refusal says nothing of the original.
 */
export function releasesRetained(path:string,code:string|undefined,retrying:boolean):boolean{
  return knownTradeRejection(code)&&(!retrying||replaysFirst(path)&&!unprovenOnRetry.has(code!));
}

/** What the page says of a request whose outcome it does not know: which request it is, and what can be done about it. */
export const retainedText=(command:{label?:string})=>`${command.label??'A trade request'} · its outcome is not known. The identical request is retained: check its committed outcome or retry it before entering another booking.`;

const settlementPath=(path:string)=>path.startsWith('swaps/settlement/');

/** Whether the account may retry a retained request: an amendment or a settlement command needs AmendTrade, a booking BookTrade. */
export function retryAllowed(path:string,catalog:{canBook?:boolean;canAmendTrades?:boolean}|undefined):boolean{
  return !!(path.endsWith('/amend')||settlementPath(path)?catalog?.canAmendTrades:catalog?.canBook);
}
