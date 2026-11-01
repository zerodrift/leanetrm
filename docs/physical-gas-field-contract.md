# Physical gas terms and field admission

Status: proposed contract and acceptance requirements, 30 October 2026. This
defines required behavior and ownership, not implemented product coverage.

Physical gas support includes delivery obligations, contractual cash, operational
quantities and the lifecycle linking them. Completeness is measured by executable
trade profiles. Capturing a field does not establish support for its behavior.

## Field admission rule

A field may enter an executable Side definition only when all of the following
exist in the same implementation slice:

1. A precise business meaning, type, unit and required/optional condition.
2. A named managed consumer that uses it in calculation, validation or an enabled
   workflow. Display or serialization alone is insufficient.
3. A behavior test in which changing the field changes the expected calculation,
   admissibility or workflow outcome. Identity-only tests are supplementary.
4. Exact-version persistence and replay when booking is enabled, including input
   eligibility under the selected effective and knowledge coordinates.
5. The same validation through authorized API, import and UI operations.

Unsupported economic terms refuse before booking. They cannot be accepted as
ignored JSON, optional placeholders, unexplained defaults or annotations.
Display-only metadata belongs outside executable Side terms and must be labeled
descriptive. A descriptive field cannot be used to claim a contractual capability.

## Proposed ownership

| Owner | Independent concepts | Consumer and required behavior |
| --- | --- | --- |
| Trade | Trade reference/date, book, internal and counterparty legal entities, trader/contact, agreement | Booking, access, amendment history and contractual party resolution. These facts are not shared unit-Side identity. |
| Trade workflow | Draft/booked/canceled state, expected revision, amendment coverage and permitted action dates | Authorized transitions, idempotency and conflict detection. A UI date filter cannot authorize an amendment. |
| Instrument membership | Component role, receive/deliver or pay/receive direction, external quantity basis | Signed composition, one-time scaling and stable obligation ownership. Avoid independent contradictory buy/sell and direction flags. |
| Instrument composition | Physical delivery and cash components, payment quantity scope | One payment can consume several delivery roles; separately priced points use distinct payment bindings. No redundant pricing-level switch. |
| PhysicalGasSide | Exact pipeline/zone/point references and admitted product | Commodity/network/zone/point compatibility, physical-value assignment and operational allocation eligibility. |
| PhysicalGasSide | Delivery-period geometry, gas-day clock and supported flow window | Exact UTC obligation coverage and operating labels. Partial-day flow is distinct from the gas-day boundary. |
| PhysicalGasSide | Contractual quantity profile and declared quantity point | Receipt versus delivery basis, daily entitlement, elapsed-hour rate or explicit period-total allocation. |
| PhysicalGasSide | Physical-quantity selection and completion treatment | Eligible forecasts/operational quantities for valuation; remaining delivery, retirement or supported inventory transfer. |
| PhysicalGasSide or explicit service component | Service obligation and supported quantity rights | Firm/interruptible effects, permitted elections, limits and deadlines. A service label without these consumers is descriptive only. |
| Payment side | Billing-quantity selection, fixed/indexed/formula terms and payment rules | Cash consideration, fixing eligibility, rounding and settlement. A physical market mark is not an invoice price. |
| Fee/service-charge side | Charge scope, quantity/event basis, rate, limits and payment rules | Reservation, usage and other separately owed amounts. No requirement that all fees follow flowing volume. |
| Reference data | Network, zones/groups, points, external meter/operator identifiers, measurement definitions | Eligible selection, operational identification and explicit conversion. Avoid duplicating derived point attributes as editable Side fields. |
| Operational records | Elections, nominations, confirmations, measurements, allocations and corrections | Retained fact revisions selected for a named purpose. Do not store them by overwriting the immutable Side or its schedule. |
| Calculation results | Prices, quantities, amounts, discount factors, PV, coverage and lineage | Read-only resolved schedule views. Calculated values are not contractual inputs or accepted payments. |
| Trade payment lifecycle | Occurrences, determinations, corrections, cash and reversals | Immutable cash history and outstanding balances per trade; shared instruments retain separate histories. |

## Quantity, delivery and operational coverage

| Required concept | Consolidated representation | Acceptance obligation |
| --- | --- | --- |
| Physical point, network and zone | One exact delivery-point binding plus effective relationship records | F01: incompatible network/zone/specification refuses; selecting a point cannot independently change its other relationships. |
| Meter or external point identifier | Scoped, dated identifier on the measurement/delivery point | F02: changed external code preserves historical submissions; wrong operator identifier refuses. |
| Product and quality | Product specification plus eligible measurement attributes | F03: specification mismatch refuses; an admitted quality adjustment changes only its declared quantity or price stage. |
| Delivery dates and operating clock | Pinned local gas-day start and exact UTC coverage | G04; F04: start/end flow windows differ from whole-day boundaries; unsupported partial-day terms refuse. |
| Daily entitlement and hourly flow | Explicit quantity basis | G04: daily entitlement remains constant on short/long days; hourly flow integrates elapsed time. |
| Daily capacity or maximum quantity | Separate quantity-limit rule | F05: 1,001 against a daily maximum of 1,000 refuses under the selected scope; a limit never becomes an automatic scheduled delivery. |
| Monthly/term quantity | Period amount plus an explicit allocation profile | F06: equal-day and duration-based allocations have independent expected amounts; monthly entitlements retain month boundaries. Only equal-day period allocation exists in the current quantity foundation. |
| Quantity point | Explicit receipt or delivery anchor | F07: 980 delivery at 2% of gross requires 1,000 receipt; 980 delivery at 2% of net requires 999.60 receipt. Native quantity stage is not inferred from direction. |
| Fuel | Named formula, applicable rate/basis and eligible quantity source | G05; F08: cash-paid fuel does not also reduce physical quantity; missing required rate refuses. |
| Fuel rounding | Explicit stage, precision and tie/directional policy | F09: fractional fuel is rounded only at the selected boundary; gross/net/fuel remain consistent under the selected residual rule. Current foundation supports exact unrounded fuel only. |
| Daily quantity changes | Dated contractual/elected quantity records | F10: changing one admitted operating day changes only its contractual coverage and linked charges; aggregate period quantity reconciles. |
| Election rights | Owner, notification deadline, period, quantity range and exercised election | F11: late or out-of-range election refuses; minimum/maximum rights do not overwrite metered quantity. |
| Variable-volume rights | Explicit baseline, count rules, size bounds, period resets and conditions | F12: count up/down/both changes against the selected baseline and enforce remaining rights. Unsupported optionality refuses before booking. |
| Minimum take or payment | Physical obligation and separate billing-minimum rule | F13: billable minimum may exceed delivery without inventing commodity flow; resulting size-dependent payoff does not silently claim linear scaling. |
| Quantity purpose and eligibility | Separate valuation, nomination and billing selectors with temporal applicability | G09/G11; F14: a higher-priority observation outside the admitted cutoff cannot replace a selected input. |
| Quantity transformations | Explicit allocations, reductions and source-stage conversions | F15: receipt/delivery and allocation conservation; no automatic propagation to unrelated trades or hedges. |
| Physical unit and measurement conditions | Exact native quantity specification and versioned conversion inputs | F16: volume-to-energy requires eligible quality/reference-condition data; missing or incompatible conversion refuses. |
| Precision | Exact internal amounts plus explicit operational/export/settlement rounding | F17: display precision cannot change PV, nominated quantity or settlement cash. |
| Eligible points and paths | Supported endpoint/service eligibility and explicit route where consumed | C06; F18: point-group membership alone does not prove connectivity or a transport right. Separate transport/storage profiles require their own behavior. |
| Delivered versus outstanding | Explicit delivery evidence and completion policy | L03/L04/L07: forecast or elapsed time alone is not accepted delivery; inventory transfer cannot double-count delivery value. |

## Price, payment and charge coverage

| Required concept | Consolidated representation | Acceptance obligation |
| --- | --- | --- |
| Physical market valuation | Qualified point/specification valuation assignment with source and projection policy | G01/G02/G12: physical mark changes MTM without changing fixed contractual consideration; assignment gaps/overlaps refuse. |
| Contractual fixed/indexed price | Typed pricing choice with required parameters | G03: switching to indexed terms requires an explicit index/source/quote binding; inactive fixed fields cannot remain economic inputs. |
| Price reference | Publication, published market coordinate and quote identity | F19: publication identity is distinct from delivery location; similar names cannot substitute for exact references. |
| Projection selection | Exact forward/custom-output binding and installed projection rule | F20: changing the projection changes only unfixed exposure; missing due fixings do not fall back to expired forwards. |
| Observation mapping | Observation cadence/window, date offsets, calendars and day grouping | F21: weekends, holidays and multi-flow-day observations retain declared application weights and coverage. |
| Averaging | Explicit arithmetic, duration or quantity weighting and denominator policy | F22: unequal quantities produce independently specified weighted cash; zero denominator follows an explicit supported rule. |
| Index multiplier and price adjustment | Dimensionless multiplier plus typed price adder | F23: apply the selected order and units once; multiplier and adder affect contractual cash without changing the physical mark. |
| Price caps/floors, bands and tiers | Installed payoff operators with explicit application stage and grouping | F24: cap/floor on average differs from average of capped/floored inputs; tier thresholds declare marginal/all-in behavior. Unsupported combinations refuse. |
| Reference-contract selection | Explicit dated contract selection and supported roll rules | F25: observation shifts, contract offsets and roll cutoffs select exact contracts reproducibly; no inferred nearby contract. |
| Event-based pricing | Eligible event, anchor and before/after windows, inclusion and adjustment rules | C05; F26: revised event dates move only the declared observation/payment windows. Fixed-day pricing does not acquire event optionality. |
| Price and payment units | Exact quantity denominator and qualified conversion path | F27: quantity conversion and price conversion are reconciled so multiplication yields the intended currency once. |
| Payment currency and FX | Settlement currency and explicit fixed/observed FX rule where supported | F28: quote-to-payment currency conversion uses its own dates, source and rounding. Unsupported multicurrency terms refuse. |
| Payment dates | Frequency, due-date anchor/offset, business-day adjustment and payment calendar | F29: due dates survive persistence and amendment; permitted overrides retain explicit origin and history. |
| Discounting | Selected currency/rate policy and value dates | F30: a discount shock changes admitted future PV, not determined nominal cash; due balances use their selected lifecycle policy. |
| Agreement/template selection | Constructor over explicit resolved versioned terms | F31: template defaults become visible saved terms; changing a template or agreement does not mutate existing definitions. |
| Service/rate schedule | Provider/agreement, service level, route/point-group applicability and effective periods | G08; F32: contract rates and general rates have explicit precedence; ambiguity and missing required rates refuse. |
| Fee basis and amount | Fixed amount, native usage quantity, capacity or duration | G07/N02/N03: zero usage can coexist with a reservation charge; a whole-contract amount is not multiplied by throughput. |
| Fee scope | One point/role, selected receipt/delivery set, or explicit aggregate | F33: aggregate exactly the selected roles once; different billing quantities remain inspectable. |
| Fee bounds and price bands | Installed minimum/maximum or tier calculation | F34: fee limit applies at its declared period and stage; additional unsupported nonlinear charges refuse. |
| Fee counterparty and payer | Explicit cash-obligation party mapping | F35: a fee to a third party is not silently netted with commodity consideration. |
| Finality, acceptance and correction | Separate source eligibility, cash determination and payment state | G10/G11/L05–L10: exact pinned inputs, round-once restatement, stable occurrences and exactly-once cash effects. |

## Features that must not become inert side fields

UI layout, field visibility, action selectors, calculated date envelopes,
calculated PV, displayed meter codes, period totals and calculated final prices
are projections or presentation controls. They are not additional economic terms.
Business references, contacts, broker details and descriptive annotations belong
to trade/reference records, with their own workflows where applicable.

Price-trigger elections, service optionality, transport capacity, storage rights,
variable-volume risk models and event-relative pricing require installed
behaviors. Do not admit them through an unimplemented toggle, generic model name,
service label or free-form formula. Explicit unsupported terms must be visible
before booking, not discovered after a successful-looking valuation.

One generic calendar does not replace delivery, observation and payment calendars.
One generic quantity type does not replace limit period, entitlement basis,
measurement stage and source state. One arbitrary date cutoff does not replace
the selected effective/knowledge context or purpose-specific eligibility.

## Park-and-loan and swing rights

Status: required gas profiles, not implemented or admitted by the fixed-quantity
booking command. Neither is a pipeline label or an unused optional Side field.

### Park-and-loan obligations

Compose physical advance and return memberships with explicit directions, point
bindings, delivery/return windows and separately directed fee memberships. From
the customer's perspective, a park normally delivers gas initially and receives
it back later; a loan reverses those directions. The recorded memberships, not a
trade-wide sign or product name, determine the obligations.

The contract specifies the return quantity rule, native measurement basis,
eligible return points, deadlines, tolerances, permitted elections and any
explicit cash-resolution right. Equal principal return is one profile, not an
assumed rule for every agreement. Loss, fuel or quantity premiums require their
own declared conversions and consumers. Pipeline reference data can qualify
locations and service rates; the contract owns the return obligation.

Trade-owned immutable events record eligible advances, credited returns,
corrections and explicitly accepted resolutions. Their ledger derives outstanding
principal in the declared common basis. A nomination, forecast or elapsed due
date alone cannot advance or extinguish principal. Retry is a no-op; corrections
preserve the original event and derive an adjustment. Reused Side definitions
must not share outstanding balances between trades.

Fee rules distinguish quantity moved, quantity outstanding over time, reserved
capacity and fixed amounts. They name their quantity state, time boundaries,
units and rounding. Current per-unit delivery tariffs do not implement an
outstanding-balance charge. The remaining return obligation and accrued/unpaid
fees stay visible after the initial physical movement retires. Storage stock,
custody, commodity ownership, gas owed and service cash remain distinct so that
the same gas is not valued twice.

### Swing rights and elections

A quantity-rights definition binds to named physical memberships and explicitly
linked payment quantities. It specifies the entitled party, effective periods,
reference quantity, minimum/base/maximum quantities, upward/downward allowances,
per-period and cumulative limits, notice clock/deadline, amendment rules and
expiry/reset rules. Quantity limits, capacity limits, minimum billing and actual
flow are independent concepts.

Count-limited rights also specify what counts as a change, the comparison
baseline, separate up/down/combined allowances, permitted change sizes and any
installed conditions. Comparing with the initial period election and comparing
with the preceding day's election are distinct policies. Changing a forecast
cannot exercise a right. Free-text conditions are descriptive, not executable
optional terms.

Trade-owned election events identify the exercising party, affected operating
days, requested quantities, submission/effective times and accepted outcome.
Validation resolves the eligible event history and remaining rights under the
selected effective/knowledge context. Accepted elections supply schedule quantity
inputs and the explicitly linked cash/fee inputs. They do not overwrite the
booked rights definition, meter readings or prior accepted quantities. Contract
election, operator nomination, confirmation and physical allocation have separate
states; an accepted commercial election is not proof of accepted pipeline flow.

Before exercise, the future quantity schedule is contingent. Valuing one chosen
schedule gives that schedule's value, not complete value of the remaining right.
Admit a declared rights-pricing model, its decision information and required
market inputs before reporting complete MTM. An intrinsic optimization and a
model including uncertainty have different valuation scopes. Missing option
valuation cannot silently become zero or a fixed base quantity. Count and
aggregate constraints also require independent quantity-scaling qualification;
the current blanket linear composition assumption is insufficient.

The [acceptance plan](physical-commodity-test-plan.md#park-and-loan-and-swing-rights)
provides independent balance, fee, exercise and lifecycle cases. Schema and UI
fields enter only when their corresponding consumers and replay are qualified.

## Current implementation and next gate

### Reference identifiers, valuation and composition

The physical delivery point is not a pricing index or a meter identifier. A
qualified path/product valuation mapping selects a curve; that curve identifies
the price reference. A pipeline does not supply an implicit fallback index.
The mapping is stored as explicit group/point bindings in market definitions and displayed by the ticket and the versioned Gas delivery setup editor.
Draft, validation and publication are admitted for the current bounded profile;
broader economic terms still require their own consumers and checks.

Gas measurement identifiers belong in reference data, qualified by operator,
identifier scheme and effective period. A point may represent an individual
measurement site or a group; group membership and allocation rules need explicit
relationships. Meter readings and their corrections remain operational records.
Merely displaying an external identifier does not qualify nomination, measurement
or allocation support. F02 and F14 must pass before admitting those workflows.

An instrument must support a list of independently directed physical memberships
and separately directed financial memberships. Buy/receive and sell/deliver are
choices on a physical membership, never a trade-wide switch. Pay/receive belongs
on each cash membership. A purchase/sale template may construct an explicitly
linked opposing payment; that template is not the general composition contract.
The independent-side API now admits one to four fixed contractual physical
memberships and up to four fixed-price cash/fee memberships. Each has an explicit
direction and exact quantity normalization. Cash selects a named physical
receipt/delivery/fuel stage. The single-point template, which derived opposing
consideration, is removed; its stored trades read as one-physical-side compositions. Operational quantity inputs and transport rights remain separate
gates.

Two physical points can describe a location exchange. Transport additionally
requires explicit service/ownership semantics, supported receipt/delivery
eligibility and declared quantity relationships; two endpoint selectors prove
neither a connected route nor a transport entitlement. A path is an ordered set
of qualified segments with explicit connections, not the display order of
instrument sides. Operational movement links must not create an additional
commodity ownership value or duplicate consideration.

The fixed-quantity composition profile qualifies cases1–2 below through retained
booking/reopening and explicit membership ratios. The remaining transport and
operational gates still require these cases before broader booking:

1. Two physical memberships retain independent locations, dates, quantities and
   directions; editing one changes only it and its explicitly linked consumers.
2. Cash and fee memberships have their own directions and named quantity-stage
   bindings. A receiving physical membership does not force every charge to pay.
3. A supported two-point transport profile reconciles receipt, delivery and fuel;
   insufficient capacity, an ineligible endpoint or a missing service binding
   refuses. A location exchange without transport rights is labeled separately.
4. A multi-segment path validates ordered connectivity and allocation at shared
   endpoints. Disconnected segments, double-counted volumes and unsupported
   cycles refuse; visual side order cannot change the route.
5. Book/reopen and idempotent recovery preserve the complete member list and
   explicit links. Runtime execution loads retained programs for every member;
   it cannot apply the current first-slot-versus-other-slots sign shortcut.

The quantity foundation qualifies contractual gas-day geometry, receipt-based
quantity profiles, two exact unrounded fuel formulas and their economic identity.
The managed [pre-delivery profile](physical-gas-side.md) adds exact point/product
market assignments, noncash delivery value and separately calculated linear cash.
The bounded fixed-price profile now has typed database booking, a ticket and
retained-program reopening. Shared valuation groups, independently adjusted points, dated direct-meter attribution,
unsubmitted contractual meter schedules and configured per-unit tariffs now have
managed/reference/API consumers. Maximum-daily limits, delivery-anchored input,
elections, actual selection, partial-day flow and physical settlement remain
unsupported.
These remain explicit coverage gaps; none is an accepted dormant Side field.

Any expansion beyond the admitted profile must pass G01–G05 and the
applicable F01/F03/F04/F16 checks through retained schedule execution. Support only
the exact admitted combinations. Introduce additional terms with their listed
consumer and regression case, then qualify booking and lifecycle through L01–L12.

Maintain a capability matrix by executable profile: fixed-quantity purchase/sale,
indexed purchase/sale, linked usage/reservation charges, and later optional or
transport-linked profiles. Every listed term must be implemented, derived,
descriptive outside the Side, inapplicable to that profile, or explicitly refused.
A profile cannot claim full gas support from a field list alone.


The current physical-reference increment reuses the location master and commodity
applicability. Typed pipeline/zone/point memberships are reference data; the
trade's operating dates and quantities are separate terms. A qualified mapping
resolves the valuation curve and adjustment. Physical delivery and market
locations may differ. The ticket displays these fields directly and never
encodes a delivery month into a location choice.
