# Reusable schedules and relative contract selection

Status: local implementation of shared temporal utilities, a bounded futures
sequence resolver and a [named-shortcut browser preview](implementation.md#imported-contract-date-lookups).
Monthly anchors and date-bound candidate curve contracts are now implemented in the
[preview](implementation.md#independent-schedules-publication-and-contract-dates).
Certified effective/knowledge contexts now cover the existing business screens.
Broader generation and published schedule → activated contract → lifecycle roll
integration remain work; see [scenario coverage](business-scenarios.md).
No deployment, publication or automatic reference-data import is authorized by
this document. Examples are synthetic.

## Observation contract selection

D261 fixes the selection anchor at each observation's explicit UTC fixing instant.
The managed `ObservationContractSelector` supports an exact dated contract or a
zero-based prompt offset. Prompt zero means the first contract whose selected roll
boundary is strictly after the fixing instant; at the boundary selection advances.
The caller explicitly selects exchange-expiry rolls or supplies one ordered roll
instant per pinned sequence contract. Explicit rolls must be after eligibility and
no later than expiry. They never replace exchange milestones. Calendar-derived
roll instants require separately resolved exact calendar terms; no calendar or
exchange convention is inferred here.

Resolution returns actual exact-version contract IDs, observation coordinates,
contract delivery periods, expiry and eligibility separately. An exact contract
can be identified after expiry without asserting that its expired mark is usable.
Prompt resolution requires complete sequence coverage and never skips an ineligible
contract. The legacy date shortcut uses the same selection kernel with its existing
explicit expiry-ordinal semantics.

`POST /api/schedules/observation-contracts` exposes this read-only resolution under
the existing authorized schedule operation. It does not save a trade, infer a
fixing price or admit these terms to current swap pricing. The first booking
integration must retain the resolved dated contracts in D258's rows and preserve
authoring terms for review. Pricing must consume the resolved rows, not rerun the
offset against the valuation date. Futures observations use daily exchange
settlement before expiry and the official final contract settlement after expiry once available; missing final prices fail
rather than freezing the last live mark. A different settlement index requires an
explicitly configured curve. Daily-source loading, normalized booked-side admission
and ticket controls remain work.

## Current authoring implementation

Independent date schedules now have saved/renameable drafts and immutable published
versions. Stable canonical references use `oe.name`. Users select either absolute
occurrence indexes or context-month semantics. Date fields accept a literal or
reference, and contract start/end date definitions consume published schedules
without choosing an existing curve. Publication views freeze committed schedule
prefixes; exact UTC knowledge cutoffs now use retained engine commit certificates. See
[implemented scope](implementation.md#independent-schedules-publication-and-contract-dates)
for exact storage, permissions, engine pins and remaining limits. Earlier design
examples below describe broader potential operations, not an implemented grammar.

The example loader gets its explicit terms from the managed backend. The ordinary
owned standard preview publishes `oe.sample_gas_months` through the same schedule
save and publish operations. It binds the exact synthetic reference calendar and
is visible to separate authorized sessions. The monthly gas contract example
requires an explicitly chosen published schedule and retains its date references.
The daily power example supplies two fixed literal delivery periods and UTC
cutoffs. Loading an example is a preview action. An authorized user can explicitly
save the reviewed rules as a shared draft and separately publish that version;
this does not publish a price curve, exchange instrument or daily generator.

## Shared contract-date library

Contract-date definitions are durable workspace data rather than session-only
candidates (D167). Save and publish use the existing `WriteMarketDefinitions` and
`SealMarketDefinitions` permissions. Readers see published versions; authors and
publishers can also inspect current drafts. Published versions and their child
rows cannot change. Editing one creates a new draft under the same stable code.
Optimistic generations reject stale saves. Save and publish commands have stable
request identities; retries return only the accepted version and state, followed
by a fresh authorized library read.

Saving resolves the form in an explicit certified context and pins the actual
schedule versions used for each field. Storage retains exact schedule foreign
keys, occurrence rules, business-day offsets and literal dates separately from
display expressions. The definition's validation context also has an exact
foreign key. No existing browser-session candidates are imported automatically.
Delivery end must follow delivery start. Last trading is a separately supplied
UTC instant and may occur before, at or after the exclusive delivery end; the
library does not infer a trading cutoff from delivery boundaries. A sequence
still requires nonoverlapping ordered delivery periods and increasing explicit
last-trading instants.

Resolving an exact published version requires an explicitly selected context.
The managed resolver applies its retained source versions, their effective
coverage and calendars. A newer publication under the same `oe.name` does not
rebind a saved definition. Earlier knowledge cutoffs cannot see later definition
publications; current access remains required for every source. A definition's
saved validation context is displayed independently of the user's resolve context.

This library has no implicit expiry or exchange policy. Unadjusted civil monthly
boundaries can be named as delivery dates when an explicitly chosen calendar has
the required coverage. They do not establish exchange last trading. Real contract
milestones must come from sourced facts; the illustrative three-business-day and
UTC-cutoff examples remain explicitly synthetic. The library does not generate
daily schedules, activate contracts or modify literal dates on booked sides.

Migration `0054_contract_date_library.sql` requires a separately qualified schema
upgrade; an application-image-only update cannot install it. Focused verification
uses `tools/run_preview.py --contract-dates-check --browser-check` with explicit
PostgreSQL, SDK and browser paths. All fixtures and observations are synthetic.

## Agreed behavior

For a configured sequence of dated contracts, the current contract advances to
the next contract when the current one's recorded expiry is reached. Adding a
new contract at the far end is a separate definition/listing event. A relative
reference such as `gas_front_1` resolves against that sequence for a selected
historical context; it is not a mutable identity for the dated contract itself.

Concrete schedule rows may be imported, entered by an authorized user or agent,
or generated by deterministic code. A reusable schedule definition can take
parameters or reference a retained existing schedule. UI, code and agents must
use the same validation, resolution and authorization contracts.

## Named date references

A schedule can have an explicitly assigned symbolic shortcut such as `oe.ng1`.
The shortcut binds a named, pinned schedule, positive relative position and a
selected date role. Symbol spelling carries no implicit behavior: `oe.ng1` is
position 1 only when its definition says so. Names and content reuse remain
separate; combine, compare and difference are coding capabilities, not a required
user-facing calendar workflow.

For the first supported futures source, a shortcut resolves to the selected dated
contract plus delivery start, exclusive delivery end or explicitly supplied
last-trading business date. A last-trading instant is not converted to a business
date by guessing its clock. The same managed resolver is available for future
instrument/side consumers and the preview API. Those consumers should pin the
definition and retain the result receipt rather than saving only a display string.
Dashboard and risk integrations will use the same meaning; they are future work.

Keep dated-contract histories distinct from a rolling shortcut's analytical
history. A future covariance calculation must explicitly choose dated factors or
a rolled analytical series, aligned observation inputs and a roll/return policy.
A price jump caused by changing contracts must not silently become a return on
one unchanged asset. A date shortcut creates neither prices nor a covariance
model and does not automatically rebook trades.

## Calendar, schedule and generator

### Date schedules before contract construction

Agreed direction: create reusable date schedules, then bind dated-contract fields
to their dates. Support standard fields and explicitly named custom date fields.
Keep the rule/reference and the concrete value plus source versions used to
construct a contract revision. Later schedule corrections create new retained
terms; they do not rewrite an earlier contract revision or calculation receipt.

Proposed editor: a small field-binding table, not separate logic for every field:

| Contract field | Schedule field/expression | Value type |
| --- | --- | --- |
| Start | `oe.gas_dates[row].start` | Operating/calendar date |
| End | `oe.gas_dates[row].end` | Operating/calendar date, exclusive bound |
| Last trading date | `oe.gas_dates[row].last_trading_date` | Operating/calendar date |
| Last trading instant | `oe.gas_dates[row].last_trading_at` | Explicit UTC instant |
| A supported custom date field | An explicit date reference or supported date operation | Declared operating/calendar date or UTC instant |

A date-only value need not be an open business day. For example, delivery may
start on a weekend. Apply an openness/adjustment requirement only where the
selected field rule specifies it.

`row` means a stable source occurrence key, not an accidental array index or a
join on equal dates. Fields in separate schedules need an explicit occurrence
mapping; missing/multiple matches fail rather than shifting another row into
place. Custom fields have declared types and names; they do not acquire standard
expiry, payment or settlement behavior merely from their spelling. Required core
terms still have their existing type, interval and milestone validations.

The dependency direction is date schedules -> dated contract terms -> contract
sequence/relative selectors -> consuming curves, sides and other schedules.
A date-valued `oe.ng1` can provide a date to a downstream consumer. A selector
derived from a contract cannot define that same contract's own source dates.
Validate the complete reference path and reject cycles. Existing catalog series
and milestone identities remain authoritative; compiled schedule data references
them rather than creating a second independently editable expiry catalog.
An imported milestone is a retained authoritative input to the schedule. A
generator instead proposes dates that may create a new qualified milestone
revision through the selected validation/activation operation. The same field
cannot be independently authoritative and editable in both places.

### Small typed operations and business-day offsets

Use small project-owned data types and operations with familiar business terms,
including schedule, calendar and roll convention. Each type or operation requires
a concrete consumer; terminology alone does not justify another class or table.

Proposed foundation: one small typed immutable `Sequence<T>` collection contract,
with native operators over it. Contract references, date-only occurrences and UTC
intervals retain their distinct types and meanings. Existing compact `DateSet`,
`UtcGrid` and `ContractSequence` implementations can expose compatible views;
there is no requirement to copy their buffers into a universal representation or
replace domain identities with array positions.

| Operation family | Behavior |
| --- | --- |
| Select | Index, take a range, find a boundary or project a declared field |
| Generate | Produce bounded unadjusted date anchors from explicit parameters |
| Move | Advance by calendar days or count open dates in an explicitly selected day set |
| Adjust | Move a closed anchor using a supported explicit adjustment mode |
| Compare | Compare compatible scalar dates or explicitly test collection membership |

Boundary search requires a declared ordering. Ordered selection preserves
occurrence identity, order and duplicates; set operations retain set semantics.

These are code operations, not separate user-managed business entities. A
calendar's open dates are a typed set view with retained weekend/closure rules,
coverage and clock/date basis. Its selected adjustment is an explicit policy
value. Named definitions and shortcuts bind these inputs to an operation and
retain source/context versions; they do not each require a new kind of manager,
configuration table or interpreter. The existing `DateRoll` values and shared
immutable buffers remain useful implementation pieces.

In the proposed operation layer, a date-valued reference used by calendar-dependent
operations binds a named calendar definition. Direct date-only reads do not need
business-day settings. That definition may include a selected adjustment mode.
Thus `oe.ng1 + 1bd` calls the same native movement operation as application code,
using the reference's explicit calendar binding. It is not an ambient default.
The calendar reference accompanies the resolved date without duplicating its raw
date payload. Changing calendars requires an explicit new/overridden binding.

The editor authors a bounded typed operation and shows its resolved output.
Concise expressions and structured controls are two ways to author that same
operation. Retain the origin/reference and dependencies rather than replacing
them with a formatted date. Missing names/calendars, incompatible types and
cycles fail before evaluation. The operation executes over pinned immutable
inputs, with no parsing, database access or ambient clock in the calculation loop.
This is a proposed next layer, not an implemented generic sequence API.

Generate recurring dates from their unadjusted anchor, then apply any selected
adjustment. Do not let an adjusted month-end date accidentally move later anchors.
Month/year movement requires explicit anchor/end-of-month behavior before support.
Hour-ending convenience functions produce interval selections under an operating
date, clock, labeling and missing/repeated-hour policy; they do not create a
universal catalog of fixed UTC hours.

Agreed capability: schedules share native indexing, selection and date operations,
including a concise business-day expression such as `oe.ng1 + 1bd`. The operator
evaluates a date-valued reference using an explicitly bound, versioned calendar.
The schedule field's date rule can retain that calendar, clock/date basis and
adjustment convention, so consumers need not repeat them in every expression.
Missing or conflicting bindings fail; no browser/server clock or financial
calendar is substituted. Calendar openness and date adjustment remain distinct
operations even when the editor presents their settings together.

### Roll conventions and business-day conventions

Use `RollConvention` for the unadjusted anchor pattern of a generated schedule.
A compact immutable tagged value can express a day of month or calendar month-end
without creating a subclass/table for every day. Weekday-based anchors and other
patterns can be added when a concrete consumer needs them. A schedule separately
states its frequency and generation bounds; a supplied date sequence remains a
valid alternative to generation, not an inferred fallback.

Use `BusinessDayConvention` for adjusting a closed date against the selected
calendar. Public definitions distinguish these adjustment rules:

| Convention | Adjustment of a closed date |
| --- | --- |
| Unadjusted | Keep the supplied date, even if closed |
| Following | Search forward for an open date |
| Preceding | Search backward for an open date |
| Modified following | Try forward; if the result changes month, use the backward result |
| Modified preceding | Try backward; if the result changes month, use the forward result |

Already-open dates remain unchanged under these adjustments. The existing
`DateRoll` implementation supports all five listed modes, with explicit
known-coverage validation even for Unadjusted. Specialized modes remain future
work until a consumer and independent checks justify them.

A sample monthly payment schedule could choose month-end anchors and preceding
adjustment; each month's anchor is calculated independently from its rule.
Delivery boundaries are separate fields: a first-of-month boundary with
unadjusted behavior remains first-of-month even on a closed day.

Before supporting generated schedules, explicitly choose short-month handling,
generation direction, irregular first/final period (stub) policy and start/end
adjustment rules. Retain unadjusted anchors and adjusted occurrences separately.
Do not infer end-of-month intent from an input date or allow an adjusted February
date to change March's original anchor. Calendar definitions may bundle a chosen
business-day convention; this is an explicit versioned binding, not one universal
rule for all delivery/trading/payment fields. Contract expiry-based advancement
is a separate relative-contract selection rule.

The current bounded monthly generator and positive/negative business-day
movement are implemented independently. Zero-offset expressions are rejected;
omitting an offset preserves the resolved date. Bitemporal publication remains
future work.

### Arithmetic contract and remaining qualification

- `+1bd` chooses the first open business date strictly after the base date;
  `+Nbd` counts N such dates. A closed starting day is not first rolled and then
  offset again: Saturday plus one business day is Monday when Monday is open.
- Negative business-day offsets count open dates strictly before the base date.
  Zero identity remains a proposal; the preview rejects `0bd` instead of choosing
  a financial policy. Omit the offset to return the unchanged resolved date.
- Calendar-day offsets and adjustment use distinct operators. A business-day
  offset already lands on an open date; do not apply another hidden roll step.
- Known closed dates are skipped. Every traversed date, including closures, must
  lie in known coverage; an unknown gap raises an error rather than being
  traversed as if it were open.
- Date and instant types do not implicitly coerce. Applying a business-date
  operation to an instant requires an explicit supported clock/date mapping.

The expression text is an authoring representation. Compile it into a bounded
typed operation with resolved references, rather than evaluating arbitrary code.
Source definitions/bindings must be selected under effective and knowledge time;
resolved outputs retain the same context and exact dependency versions. The
general schedule-operation layer and bitemporal definition storage remain
future work. The current preview implements monthly indexed references,
business-day movement and start/end/last-trading field bindings for private
candidate definitions. It does not implement custom date fields or activation.

First TDD cases: same-row multi-field binding, duplicate rolled dates without
occurrence loss, custom-field type rejection, reference cycles, Saturday/holiday
`+1bd`, negative/zero offsets, coverage gaps and replay before/after a backdated
schedule correction. Hour-ending built-ins additionally require a date, explicit
market clock/labeling convention and missing/repeated-hour policy before UTC
interval resolution; they are not universal fixed UTC hours.

Keep these meanings distinct without introducing a new table for each use:

| Concept | Responsibility |
| --- | --- |
| Business-day calendar | Working days, closures and clock/date basis used by a rule |
| Retained schedule | Concrete dated occurrences, their roles, ordering, source and exact definition versions |
| Optional schedule definition/generator | A named deterministic rule and explicit parameters producing concrete occurrences |

The existing futures-series and milestone relationships already retain dated
contract identities, delivery periods, first/last trading and related evidence.
Use those for the first contract-sequence consumer rather than copying expiry
dates into an unrelated holiday table. Broader schedule persistence should be
added only when another executable consumer needs it.

A bounded generator can eventually support recurrence, explicit date bounds,
anchor dates, supported day/business-day offsets, adjustment convention,
calendar/clock versions and output event roles. Missing parameters, ambiguous
local instants or unsupported rules fail; no default holiday treatment or market
cutoff is inferred. The exact parameter schema and operator names remain proposed.
No free-form scripting language is required for the first calendar-backed alias.

Generating delivery, observation and payment dates does not itself define
settlement averaging, price shaping, day count, monetary rounding or quantities.
Those remain their respective explicit financial contracts.

## Shared immutable data, composition and the fast path

Agreed: separate names/definitions share one immutable underlying date payload
when its complete typed contents are identical. This is canonical data reuse,
not independent mutable copies. Definitions retain their roles, source evidence,
authority, clock/calendar versions and historical selection. Equal dates alone
do not make contracts, calendars or payment obligations economically equivalent.

Keep a small typed kernel, not one untyped list with implicit conversions:

| Representation | Meaning and composition |
| --- | --- |
| Operating-date set | Distinct date-only members, for example holidays; bounded union/intersection/difference |
| Ordered occurrences | Dated events with stable occurrence-to-date mappings; equal dates do not merge obligations |
| Delivery intervals and selection masks | Half-open resolved UTC intervals, with TOU membership over an exact interval grid |

These are implementation representations, not a mandate for three new public
business classes or tables. A schedule definition combines the supported typed
operations and references already discussed. The result can share a compact
payload even when two definitions reached it through different rules.

For a joint calendar requiring both markets open, combine business-day sets by
intersection, including each calendar's weekends and explicit open/closed
overrides. Holiday union is sufficient only where the other date-basis/weekend
semantics match. A joint rule allowing either market open is different and must
select union explicitly. Combining incompatible market-date bases/clocks requires
an explicit mapping or fails.

Holiday/set composition, recurrence, adjustment/roll convention, and local-time
resolution are distinct operations. Preserve their selected order. Union of
date sets can remove duplicates; adjustment of ordered event occurrences cannot
silently deduplicate two obligations moved onto the same date. Any coalescing or
aggregation must be a separate selected operation with retained source links.
Unknown calendar coverage is not an open day; set complement is bounded to an
explicit known universe, with missing requested coverage rejected.

Daily, hourly and finer delivery resolutions are parameters of the rules/grid,
not separate engines or automatic precision conversions. A daily operating date
cannot be expanded by assuming 24 hours. TOU definitions select local windows and
calendar predicates under explicit clocks and DST policies; compilation resolves
actual UTC intervals before producing an interval-selection mask. A repeated
local hour retains distinct UTC intervals; a skipped hour is not fabricated.
If a boundary cuts an interval, split it under a supported explicit rule or
reject it. Never round the boundary to a convenient grid. See
[time-and-schedules.md](time-and-schedules.md).

The first utility uses sorted immutable integer arrays for date sets, contiguous
UTC boundary arrays and compact interval-selection bitsets. Its tests and bounded
microbenchmarks are recorded in the shared utility README. Broader representation
choices remain proposals; no new
compression library or dependency is justified yet. Grid masks share only when
their complete grid identity, boundaries and interpretation agree. Coverage,
coordinate type, precision, ordering and multiplicity are explicit, not encoded
only by the chosen storage format. Identical raw date buffers can be shared while
their definition/provenance and occurrence mappings remain distinct.

Persistent canonicalization will need a versioned typed content key and full
equality validation, not a hash alone. The implemented in-memory pool uses
process-local hashes and full equality, not durable content IDs.
Interning must be concurrency-safe and atomically publish a
complete immutable value. Reuse stays within the authorized sharing namespace;
recognizing equal content grants no access to another definition. Definition
changes retain old values and create/reuse a new payload; they cannot mutate it
in place. Calendar coverage or timezone-rule changes invalidate affected compiled
identities even where a retained raw buffer can still be shared.

Compile/import/validate once, then reuse in memory across calculations. Avoid
database reads, date-string parsing, rule interpretation, new per-date objects
and repeated timezone conversion inside the pricing loop. Keep calculation reuse
keys separate from payload-deduplication keys: model, roles, rule versions,
context, references and provenance needed for replay remain part of calculation
identity. Equal resulting dates do not by themselves authorize reuse of PV or
risk results. Cache policy must bound retention and avoid holding every horizon
forever; longer/extended schedules can reuse immutable chunks if measured useful.

Performance qualification must separate cold import/compilation, interning hits,
calendar composition, lookup/relative-contract resolution, rolling and repeated
TOU selection. Measure allocations, unique/shared payload counts, peak/retained
memory and elapsed distributions for sparse holidays and dense daily/hourly grids,
including multiple horizons and concurrent callers. Verify outputs against an
independent simple implementation, including DST, collisions, coverage gaps,
corrections and cache misses. This design is not benchmark evidence; choose and
report performance budgets before claiming that the implementation is fast.

## Retained rows are the calculation inputs

Import/manual/generator provenance stays attached to every version. A generated
proposal does not establish an authoritative exchange date. Conflicts between
sources, generated dates and explicit exceptions must be surfaced, then resolved
under a selected versioned policy. No automatic precedence is implied.

Retain the schedule/definition revision, event role, dated contract or source
occurrence, business/operating date where applicable, UTC instant where applicable,
clock/calendar pins and evidence. For generated rows also retain generator
version, parameters and generated output identity. References to existing
schedules are pinned to exact versions, with cycles rejected.

An approved explicit schedule is also a legitimate input without a generator.
Do not attempt to reverse-engineer a rule from imported rows. A referenced
materialized schedule and a regenerated schedule are different selected input
modes; missing rows do not silently trigger generation or a current-data lookup.

For historical resolution, use the schedule version effective for the context
and available under its knowledge cutoff. A later correction creates a retained
version rather than rewriting previous receipts. An existing schedule reference
in a side definition must remain pinned according to that side's explicit
context/version contract; a future template edit does not change booked terms.

## Minimal contract-sequence resolution

The implemented bounded futures resolver accepts an explicit pinned contract-sequence
revision, coverage and valuation instant, plus positive
ordinal `n`. Its caller must qualify the historical knowledge selection; there is
no automatic latest/history lookup. Each entry supplies the exact contract ID, sequence ordering,
reference to the selected roll milestone and its UTC instant, and the required
listing/first-trading eligibility. The definition pins the milestone's exact
role: last trading, expiry, final settlement, publication and delivery can be
different events. A missing selected event fails; no other role substitutes.

For the synthetic futures example, explicitly select the retained `last_trading`
milestone as its expiry-based roll boundary. The current bounded futures adapter
also binds its `MarketContract.Expiry` field to that authoritative milestone.
That is an explicit supported mapping, not a general equivalence for all products.

The alias rule selected here advances **at the selected expiry boundary**. A context immediately before
the recorded boundary still selects the old contract; at that instant it selects
the next unexpired entry. The `n`th relative reference follows the retained order,
not lexical labels, delivery-month guessing or a fresh sort by prices. A separately
requested liquidity-based or early-roll alias would require a different explicit
rule; it cannot change this reference's meaning.

Every selected entry must be eligible under its listing/definition status and
the context's exact data pins. Missing coverage, an unknown required milestone,
duplicate/ambiguous ordering, an unexpected hole, or an ineligible expected next
contract raises a structured error. Do not silently skip to a farther entry.
If the next contract's price is missing, report that separately from successful
identity resolution; a missing price never authorizes selecting another contract.
The complete required live prefix through ordinal `n` must be qualified.

For example, let the February contract's selected last-trading milestone occur
January 28 at 19:30 UTC, and let March already be listed and live. At 19:29 the first relative contract resolves
to February; at 19:30 it resolves to March. The March contract is not created by
that change. If April first becomes eligible the next day, that is an independent
extension of the available horizon. These times are synthetic fixture choices.

Retain the resolved contract IDs, sequence revision, selection-rule version,
context and input observations in every calculation. Market histories stay on
the dated contract IDs. Relative-label history is a separate analytical series
whose roll convention is retained; an unadjusted roll gap is not a return on one
unchanged asset. The alias never rolls, closes or rebooks a Trade automatically.

## Agent maintenance

An agent may collect permitted source data and propose structured schedule rows
with exact product/series references, source evidence and explicit unknowns.
It may generate candidate dates only through the selected deterministic rule.
The model's prose or inferred date is not authoritative schedule evidence.

The same scoped import, validation and activation permissions used by people
apply to an agent, with its own actor identity and any delegation retained.
Approval follows the configured operation/policy and role separation; this
document adds no unconditional human-confirmation requirement. Failed validation
or conflicting dates cannot be activated as a successful partial default.

Keep real source data and redistribution entitlements separate from public
synthetic fixtures. Only permitted authoritative data can be activated for a real
market; placeholder and unverified terms remain explicitly non-executable.

## Qualification sequence

The current managed/HTTP checks cover bounded futures resolution, corrected
explicit revisions, independent far-end extensions and private symbolic reuse.
Source ingestion/activation and historical knowledge selection below remain work.


1. Start with supplied synthetic contract-sequence rows. Prove first/second
   relative-contract resolution immediately before, at and after the selected
   expiry boundary. Reject a missing selected milestone even when settlement,
   publication or delivery events are present. Include
   multiple expiries when querying a later context. Validate full required prefix
   coverage and eligibility; reject gaps rather than silently skipping entries.
2. Prove that later far-end listing, definition registration and price arrival
   are separate. Identity resolution can succeed while its price is unavailable.
3. Correct an expiry or listing date in a later retained version. Demonstrate
   old-context knowledge selection and replay without changing old receipts.
4. Check human/agent import and activation scopes, conflicting evidence, duplicate
   rows, incomplete coverage and unauthorized writes before enabling maintenance.
5. Add a small named generator only for the same concrete use case or another
   current schedule consumer. Compare its materialized output with independent
   expected dates, including supported boundary/holiday cases. Verify identical
   pinned inputs/generator versions produce identical output, and reject unknown
   parameters/conventions. Broader syntax and calendar breadth remain later work.

This clarification settles the expiry-based monthly contract selector. It does
not choose the separate daily/BALMO window boundary at month-end; that remains
open in [the composition proposal](examples/curve-composition-rollover.md).

## Saved schedules and date mappings (D283)

A schedule is an ordered series of dates, independent of instruments. The editor
accepts explicit date lists and bounded monthly generation. Add/import merges
unique dates in order; repeating an import records no new fact. Corrections use
a new immutable version. A list needs no business calendar unless its expressions
use business-day offsets. A declared calendar must remain visible and exact.

Date mappings name symbolic fields such as `oe.delivery_months[0]` and
`oe.gas_expiries[0]`. They do not hold tick sizes, lot sizes or other product
specifications. Those belong to Futures reference data. The two primary workspace
tabs are Schedules and Date mappings, with one shared calendar preview.

The `on_or_after` selection makes index zero the first date on or after the
explicit context date. Absolute indexing and generated anchor-month selection
remain explicit alternatives. Resolution retains the exact publication and
historical context. No hourly behavior is added.

One request-scoped resolver indexes authorized versions, compiles each used
schedule once and caches parsed expressions and receipts. It does not cache
permissions across requests. Curve schedule selection shares the ordered-date
selection operation. Business-day movement uses the existing temporal library.

The local demo compiler starts with declared date lists and resolves mappings
through the same managed resolver before rendering instrument dates. The live
seed publishes those mappings through the ordinary commands and verifies their
receipts against the compilation. Editable native curve definitions and the full
instrument-authoring cutover remain separate work.

### Verified contract admission

`POST /api/date-schedules/contracts/materialize` requires current
`WriteMarketDefinitions` authority. The request names a target revision, exact
published mapping version, retained source context and explicit contract IDs,
mapping ordinals, labels, kinds and futures-series references. The initial
contract accepts only absolute occurrences of published explicit date lists,
without business-day offsets. The UTC cutoff comes from each mapping row.

The backend resolves the sources in the retained context, then restores the
current-write binding before construction. Every requested label must match its
declared occurrence. Existing contracts must match the target reference revision,
kind, futures series, delivery interval and expiry exactly. Identical terms are
reused, including in sealed revisions. Missing contracts require a draft target;
futures must also match their exact reference-series dates and known last-trading
instant. Changed retained terms fail rather than being revised in place.

The immutable admission retains exact contract, mapping-version/occurrence and
source-context foreign keys. It means that those terms were verified now, not
that an earlier contract was originally constructed by this mapping. Its own
transaction receives a certified knowledge receipt, independent of the source
mapping's or target revision's publication time. Certification orders pending
facts by actual commit time, since XID assignment order can differ from commit
order. Current source access is required for reads and command replay.

`POST /api/date-schedules/contracts/admissions` reads these proofs under an exact
retained context and current target/source authority. Earlier cutoffs cannot see
later verification. Command retries retain their request identity and first
outcome, including whether a contract was created; another request for the same
contract/mapping occurrence returns the existing admission. All construction and
request records commit atomically.

Migration `0161_contract_date_admissions.sql` requires a qualified schema release.
Owned synthetic checks cover alignment, concurrent reuse, reversed XID/commit
ordering, historical visibility, raw storage guards, source revocation, first
outcomes after sealing and rollback on invalid references. This does not qualify
a production cutover or native-curve authoring UI. Installation-specific seed
plans, scripts, source data and operational records remain outside the repository.

Mapping-library visibility checks each distinct exact schedule reference once per
outer visibility evaluation. This avoids repeating the same source check for
every field while preserving current authority, pinned versions and all field
occurrences. It introduces no cross-request permission cache.
