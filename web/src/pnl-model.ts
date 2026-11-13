import {node} from './risk-report-elements.js';
import {amountText} from './amount-display.js';
import {compareDecimalStrings} from './analytics-model.js';

/** The trading P&L report as the backend states it. An amount is exact text; its display text is the backend's too. */
export type Currency={code:string;revision:{catalogId:string;revisionId:string}};
export type Money={amount:string;currency:Currency};
// A Greek effect none of whose estimates is available has no amount; unavailableEstimates counts the missing ones.
export type GreekEffect={amount:Money|null;unavailableEstimates:number};
export type Step=GreekEffect&{label:string;kind:'greek'|'residual'|'cash'|'adjustment';start:Money;end:Money};
// What opening Greeks explain has no amount when none of the estimates is available; unavailableEstimates counts the missing ones.
export type Values={openingValue:Money;closingValue:Money;valueMovement:Money;cash:Money;priorCash:Money;transfers:Money;economicPnl:Money;explained:Money|null;residual:Money;
 unavailableEstimates:number};
// A price factor of the opening holdings. It has no closing price, and so no change, when the closing endpoint states none.
export type Factor={axis:string;label:string;priceTick:string;opening:{price:string};closing:{price:string}|null;change:{price:string}|null;unavailableReason:string|null};
export type Contribution={formula:string;label:string;axes:string[];amount:Money|null;unavailableReason:string|null};
export type Position={position:{tradeId:string;bindingId:string;bookId:string};tradeReference:string|null;instrumentReference:string|null;state:string;values:Values;
 contributions:Contribution[]};
export type Total={currency:Currency;values:Values;bridge:Step[];factorEffects:{axis:string;delta:GreekEffect;gamma:GreekEffect}[];greekCoverage:string};
export type Report={id:string;version:string;scope:{books:{id:string;name:string}[];tradeSelection:string};
 opening:{at:string;context:{knownAt:string}};closing:{at:string;context:{knownAt:string}};
 time:{mode:string;status:string;totals:{currency:Currency;value:Money}[]|null;partitions:{status:string;detail?:string;clock:{at:string;calendar?:{zone:string}}}[]};
 policies:{coverage:string;timeModes:{id:string;label:string}[]};factors:Factor[];totals:Total[];positions:Position[]};
export const reportVersion='trading_pnl_report_v2';

export const isZero=(m:Money)=>compareDecimalStrings(m.amount,'0')===0;
export const sign=(m:Money)=>isZero(m)?'flat':m.amount.startsWith('-')?'loss':'gain';
export const moneyText=(m:Money)=>amountText(m.amount,'money');
/** An amount as its display text, in its sign's ink, with the exact value as its title. */
export function amount(m:Money){const text=node('span',moneyText(m));text.className='pnl-money '+sign(m);text.title=m.amount;return text;}
export const estimates=(count:number)=>`${count} ${count===1?'estimate':'estimates'}`;
/** What stands where an amount would, when there is none to show. */
export function unavailable(){const text=node('span','Unavailable');text.className='pnl-unavailable';return text;}
