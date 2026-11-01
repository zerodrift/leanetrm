// Mirrors of the deal ticket routes' JSON (System.Text.Json web defaults, nulls written).
// Numeric schedule values are exact decimal strings or exact "a/b" ratios, never numbers.
export type TicketField={id:string;label:string;meaning:string;type:string;unit:string|null;state:string;required:boolean;band:string|null;choices:string|null;dependsOn:string[];lookup:string|null;soon:string|null;rule:string|null};
export type TicketColumn={id:string;label:string;unit:string|null;measure:string;input:boolean};
export type TicketSideKind={kind:string;pill:string;colour:string;min:number;max:number;fields:TicketField[];columns:TicketColumn[]};
// At most `max` sides of the named kinds together.
export type TicketSideLimit={kinds:string[];max:number};
export type TicketTab={id:string;label:string};
export type TicketDescription={product:string;version:number;rowShape:string;tradeFields:TicketField[];sides:TicketSideKind[];sideLimits:TicketSideLimit[];tradeColumns:TicketColumn[];levels:string[];tabs:TicketTab[];modes:string[]};
export type TicketSideDraft={kind:string;code:string;values:Record<string,string>};
export type TicketDraft={product:string;version:number;trade:Record<string,string>;sides:TicketSideDraft[]};
export type TicketChoice={value:string;label:string};
export type TicketLookup={value:string;source:{kind:string;label:string}};
// A null value means the level has no value there, never zero; trade.payment_at holds an ISO-8601 UTC instant.
export type ScheduleViewCell={value:string|null;sources:string[]};
export type ScheduleViewRow={key:string;parent:string|null;level:string;start:string;end:string;operatingDate:string|null;cells:Record<string,ScheduleViewCell>;delivered:boolean};
export type ScheduleView={levels:string[];rows:ScheduleViewRow[];totals:Record<string,ScheduleViewCell>;delivered:Record<string,ScheduleViewCell>|null;remaining:Record<string,ScheduleViewCell>|null;deliveredLeaves:number;leaves:number;valuationAt:string};
export type ScheduleRefusal={code:string;message:string};
export type TicketSummary={side:string;billedQuantity:string|null;amount:string|null;againstMarket:string|null};
export type TradeAction={allowed:boolean;reason:string};
export type TradeActions={amend:TradeAction;closeOut:TradeAction};
// A preview answers its schedule, or why the schedule could not be built beside a valuation that succeeded; a booked trade read adds its draft
// and the actions it offers; a booking answers its receipt.
export type TicketPreview={schedule:ScheduleView|null;summaries:TicketSummary[]|null;scheduleRefusal:ScheduleRefusal|null};
export type TicketTrade=TicketPreview&{draft:TicketDraft;actions:TradeActions};
export type TicketReceipt={operationId:string;tradeId:string;instrumentId:string;revision:number};
