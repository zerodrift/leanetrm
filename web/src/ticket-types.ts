import type {SideCalendarPolicy,CalendarPurposes,SideCalendarCatalogRow} from './side-calendar.js';
import type {CatalogueSource} from './market-navigation.js';
import type {ExerciseState,GasMeasures} from './lifecycle-text.js';
import type {LifecycleTarget} from './lifecycle-model.js';
import type {InstrumentSideGroup} from './instrument-sides.js';
import type {TicketFieldGroup} from './ticket-field-groups.js';
// Mirrors of the deal ticket routes' JSON (System.Text.Json web defaults, nulls written).
// Numeric schedule values are exact decimal strings or exact "a/b" ratios, never numbers.
// A field's or column's unit names the unit its values are in, resolved per trade: 'quantity' is the trade's quantity unit, which each preview and
// booked read returns as quantityUnit. The description never holds a unit symbol.
export type TicketField={id:string;label:string;meaning:string;type:string;unit:string|null;state:string;required:boolean;visibleWhen?:{field:string;values:string[]};labelByChoice?:{field:string;labels:Readonly<Record<string,string>>};band:string|null;choices:string|null;dependsOn:string[];refreshOn?:string[];lookup:string|null;soon:string|null;rule:string|null;allowNotApplicable?:boolean;refreshDescription?:boolean};
// input lists the modes in which a side column's day cells are entries, recorded by the product's own operation.
// soon, on a field, side kind, column or tab, is the hover text of an item drawn as coming soon: it holds no value, is never sent and reads nothing.
export type TicketColumn={id:string;label:string;unit:string|null;measure:string;input:string[];soon:string|null};
// A new ticket opens with `initial` sides of the kind; none of a coming-soon kind is ever added.
export type TicketSideKind={kind:string;pill:string;entryCount?:number;colour:string;min:number;initial:number;max:number;fields:TicketField[];columns:TicketColumn[];soon:string|null;calendarPurposes?:CalendarPurposes|null};
// At most `max` sides of the named kinds together.
export type TicketSideLimit={kinds:string[];max:number};
export type TicketTab={id:string;label:string;soon:string|null};
export type TicketTermSection={id:'exerciseRights';label:string;fields:TicketField[]};
export type ExerciseRightDraft={right_sides?:string[];right_period?:string;right_holder?:string;right_settlement?:string;right_lead_hours?:string};
export type TicketDescription={product:string;version:number;rowShape:string;tradeFields:TicketField[];sides:TicketSideKind[];sideLimits:TicketSideLimit[];tradeColumns:TicketColumn[];levels:string[];tabs:TicketTab[];modes:string[];sideCalendars?:SideCalendarCatalogRow[];selection?:Record<string,string>;sideGroupsRequired?:boolean;fieldGroups?:TicketFieldGroup[]|null;repeatableTerms?:TicketTermSection[]};
export type TicketSideDraft={kind:string;code:string;values:Record<string,string>;calendarPolicy?:SideCalendarPolicy};
export type TicketDraft={product:string;version:number;trade:Record<string,string>;sides:TicketSideDraft[];exerciseRights?:ExerciseRightDraft[]};
export type TicketChoice={value:string;label:string};
export type TicketLookup={value:string;source:{kind:string;label:string}};
// A null value means the level has no value there, never zero; trade.payment_at holds an ISO-8601 UTC instant.
export type DeliveryQuantityEvidence={source:string;revisionId:string;reference:string};
export type ExercisePeriod={right:number;from:string;to:string;status:'exercised'|'abandoned'|'contingent'};
export type ExerciseGroup={right:number;from:string;to:string;state:ExerciseState;forwardSum:string;counted:string;
 inputs:{receives:string;pays:string;certain:string;receivesVolatility:string|null;paysVolatility:string|null;correlation:string|null;years:string}|null};
export type ScheduleViewCell={unassessedDays?:number;incomplete?:boolean;value:string|null;sources:string[];quantityPart?:string;sourceQuantity?:string;quantityEvidence?:DeliveryQuantityEvidence[];exercisePeriods?:ExercisePeriod[]};
export type ScheduleViewRow={key:string;parent:string|null;level:string;start:string;end:string;operatingDate:string|null;cells:Record<string,ScheduleViewCell>;delivered:boolean};
export type ScheduleView={unassessedDays?:number;incomplete?:boolean;levels:string[];rows:ScheduleViewRow[];totals:Record<string,ScheduleViewCell>;delivered:Record<string,ScheduleViewCell>|null;remaining:Record<string,ScheduleViewCell>|null;deliveredLeaves:number;leaves:number;valuationAt:string;progressBasis?:'native_schedule_rows_v1';groups?:ExerciseGroup[]};
export type ScheduleRefusal={code:string;message:string};
export type TicketSummary={side:string;billedQuantity:string|null;amount:string|null;againstMarket:string|null};
export type TradeAction={allowed:boolean;reason:string};
// record says whether this reader may record a booked side's day quantities here, by the record operation's own rule, and why not.
export type ExerciseAction={allowed:boolean;reason:string|null;label:string;preparePath:string;recordPath:string;
 tradeId:string;instrumentId:string;expectedRevision:number;readContextId:string|null;outcomes:TicketChoice[];
 periods:{right:number;from:string;to:string;exerciseAt:string;previousId:string|null;previousOutcome:Extract<ExerciseState,'exercised'|'abandoned'>|null;previousEffectiveAt:string|null}[]};
export type TradeActions={amend:TradeAction;closeOut:TradeAction;record:TradeAction;recordExercise?:ExerciseAction};
// A preview answers its schedule, or why the schedule could not be built beside a valuation that succeeded; a booked trade read adds its draft
// and the actions it offers; a booking answers its receipt.
// A link is a physical side whose quantity follows its source's: their receipt, delivery and fuel totals over the term, as exact text.
export type TicketLink={source:string;target:string;formula:string;fraction:string;receipt:string;delivery:string;fuel:string};
export type TicketPreview={scheduleEntries?:TicketScheduleEntry[];imbalanceRows?:ImbalanceReportRow[];unassessedDays?:number;incomplete?:boolean;source:CatalogueSource;schedule:ScheduleView|null;summaries:TicketSummary[]|null;scheduleRefusal:ScheduleRefusal|null;quantityUnit:string;currency?:string;links:TicketLink[];groups?:ExerciseGroup[]|null;sideGroups?:InstrumentSideGroup[];sideGroupsReason?:string|null};
// quantityPolicy is the daily quantity selection the booked schedule shows, or null for a trade that records no daily quantities.
export type FacilityAllocationTrack={obligationId:string;sideCode:string;day:string;quantity:string|null;unit:string;state:'planned'|'no_current_allocation';finalQuantity?:string|null;finalState?:string};
export type TicketTrade=TicketPreview&{measures?:GasMeasures|null;draft:TicketDraft;retainedChoices?:Record<string,TicketChoice[]>;lifecycleTarget?:LifecycleTarget;definitionRevisionId:string;snapshotId:string;context:{contextId:string;effectiveOn:string;knownAt:string};sideSlots:Record<string,number>;actions:TradeActions;quantityPolicy:string|null;facilityAllocations:FacilityAllocationTrack[]};
export type TicketReceipt={operationId:string;tradeId:string;instrumentId:string;revision:number;unchanged?:boolean};

export type TicketScheduleEntry={code:string;owner:string;kind:string;name:string};
export type ImbalanceReportRow={entry:string;slot:number;balanceSide:'owed_to_us'|'owed_by_us';day:string;state:string;reason:string|null;reportedBalance:string|null;
 statements:{accountId:string;serviceVersionId:string;statementId:string|null;status:string;reportedBalance:string|null;evidence:string}[];
 quantity:string|null;index:string;price:string|null;published:boolean;amount:string|null;cashState:string;accrued:string|null;paid:string;outstanding:string|null;paymentAt:string|null};
