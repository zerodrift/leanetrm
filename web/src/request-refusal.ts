// A refusal the page has no wording of its own for, in the server's words.
type AffectedBinding={tradeId?:unknown;reason?:unknown};
type Refusal={code?:unknown;message?:unknown;affectedBindings?:unknown};

const generic='The app could not complete that request. Please refresh and try again.';
// Each reason as the end of a sentence that starts with the number of trades.
const reasons:Record<string,[one:string,many:string]>={
 exact_bundle_whole_operation_v1:['was booked under other reference or definition versions than the selected market data','were booked under other reference or definition versions than the selected market data'],
 missing_curve_view:['needs a curve the selected market data omits','need a curve the selected market data omits'],
};

function affected(bindings:unknown):string[]{
 if(!Array.isArray(bindings))return [];
 const byReason=new Map<string,Set<string>>();
 for(const b of bindings as AffectedBinding[])
  if(typeof b?.tradeId==='string'&&typeof b.reason==='string')byReason.set(b.reason,(byReason.get(b.reason)??new Set()).add(b.tradeId));
 return [...byReason].map(([reason,trades])=>{
  const [one,many]=reasons[reason]??[`was refused (${reason})`,`were refused (${reason})`];
  return `${trades.size} ${trades.size===1?'trade '+one:'trades '+many}: ${[...trades].join(', ')}.`;
 });
}

export function requestRefusal(error:Refusal):string{
 // A request the server could not read means the page and the server disagree, which a refresh resolves.
 const message=error.code!=='request_format'&&typeof error.message==='string'&&error.message.trim()?error.message.trim():generic;
 const trades=error.code==='pilot_close_inputs'?affected(error.affectedBindings):[];
 return trades.length?[message,...trades,'Choose books that leave out these trades to see the rest.'].join('\n'):message;
}
