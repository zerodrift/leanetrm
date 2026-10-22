# Publication-driven OTC fixing and settlement

Status: draft design for review, 2026-10-20. No settlement feature, migration or
production data activation is implemented by this document. Proposed choices are
recorded as P39, not as agreed financial conventions. The implementation inventory
below originated at source revision `557ffd8`; the current Phase C supplement
is [the next-task proposal](ppa-settlement-next.md). That proposal does not accept
P39 policies or authorize settlement implementation. The bounded
[monthly gross policy/specification](ppa-settlement-spec.md) now supplies exact
proposed wording and an independent executable reference model for review.
D200 accepts SP1–SP9, including rounding and approval, only for explicitly
opted-in bounded fixed-strike contracts. The [meter-finality prerequisite](meter-finality.md)
and [power payment planning/gross preview](settlement-preview.md) are separate
bounded implementations (D200–D201); no global settlement phase is complete.

D202 subsequently narrows the current MVP to settlement calculations, draft
break-even pricing and a graphical workbench. Approval, accepted-cash accounting,
payments, reversals and automated correction adjustments below remain deferred
future design. They are not prerequisites for pricing and do not introduce a
separate approval workflow. See [the current scope](ppa-break-even.md).

## 1. Outcome and scope

A daily power or gas swap must resolve each contracted reset against its eligible
published actual, preserve unresolved periods and their declared projections,
determine the resulting cash obligation, and retain that obligation until payment
is recorded. The same fixing is reusable across authorized trades. Delivery end,
last trading, publication, fixing acceptance, approval and payment are distinct.

First delivery targets existing one-month financial swaps with daily or monthly
resets and separately specified fixed/floating payment dates. Deliver one qualified
power index and one gas publication through the same fixing/settlement operations.
Their exact identities remain selection gates in section 12. Multi-year booking,
futures variation margin, physical delivery, invoices, taxes, fees, FX settlement,
bank dispatch and cross-trade netting are outside this first delivery.

Proposed bounded cash scope: retain separate side-payment obligations and support
manual confirmation of one approved obligation's full outstanding amount. Partial
payments, split allocations and netting require their own supported contracts. If
the first selected agreement requires those features, qualify them before activating
that agreement; never reinterpret it as gross settlement or full payment.

## 2. Reuse and implementation boundaries

| Existing component | Reuse | Remaining work |
| --- | --- | --- |
| `Trading/IndexFixings.cs`, `Trading/PaymentSchedules.cs` | Exact dated actuals, partial fixing, original averaging weights, frozen commodity risk | Publication-driven eligibility version and trade-owned accepted-fixing state |
| Migration 0050 | Exact schedule/index/series/source foreign keys and restrictive visibility | Bind qualified producer rules and distinguish expected polling from eligibility |
| `power_data.prices`, `PowerCollection`, `PowerImportProtocol` | Immutable native intervals, revisions, certified collection batches, scoped collection | Contractual finality/publication evidence and source-to-index publication |
| `PowerHours`, `TimeOfUse`, `PowerQuantitySchedule`, `PowerSettlement` | Clock/DST/coverage checks and exact quantity/cash arithmetic | Qualified contracted index aggregation and accepted obligations; the D171 preview already consumes collected native interval prices |
| Market observations/corrections and certified knowledge contexts | Exact input pins, immutable correction chains and historical reads | Typed evidence linking published fixings to raw source revisions |
| Trade amendment journal and book authorization | Current scope, immutable history, expected-version/idempotency patterns | Reset occurrences, determined obligations, approvals and payment evidence |
| .NET worker, automation jobs and claim fencing | Discovery, bounded execution, ownership and retry controls | Narrow fixing-publication and settlement-reconciliation operations |

Current indexed pricing already uses supplied final actuals and removes only their
live commodity factors. It does not ingest real publications or establish that
cash was paid. Legacy swap sides still raise `side_payment_due` for a due payment.
Phase C PPA sides instead separate known and estimated overdue balances from
`ValueExcludingOverdue`; they do not create accepted obligations or paid state.

The narrower [collected interval delivery-value consumer](power-preview.md#collected-interval-delivery-value)
(D171) now connects an explicit power curve/source association, one operating day
and a price-history cutoff to the existing settlement kernel. It supports exact
interval MW shapes or already-shaped MWh and retains batch lineage. It consumes
collected facts under current pinned definitions; qualified final index publication,
trade fixing acceptance and the cash lifecycle below remain separate work.

## 3. Publication and valuation contract

### 3.1 Distinct coordinates

Every operation retains its explicit context and consumed evidence:

1. The delivery/flow interval and exact market clock/profile.
2. The producer/index rule version and contractual eligibility conditions.
3. For consumed actuals, vendor publication instant, revision identity and finality
   stage under the selected eligibility contract; missing evidence stays explicit.
4. Application receipt evidence and commit-certified knowledge context.
5. Valuation/business-effective time, selected trade state and market cutoff.

Expected publication/poll times are operational metadata. A late poll does not
change the business event's effective time, and an expected release time does not
prove that a price exists. Historical reads require the publication and its
application knowledge to be visible in the selected context; late ingestion must
not pretend an earlier close knew the value. Later verification/finality evidence
for an already collected row is append-only and receives its own commit-certified
knowledge time. A new verification assertion cannot retroactively qualify that
row for an earlier knowledge cutoff.

If a feed does not supply sufficient publication/finality evidence, report it as
unqualified. A documented alternative availability rule would be a separately
selected/versioned contract, not an inferred receipt-time substitution.

### 3.2 Preserve v1; introduce publication-driven eligibility explicitly

The current `published_index_observations_v1` uses `FixingAt` as an economic
boundary. Before it, the resolver projects; afterward it requires a final actual
observed at or after that boundary. An early eligible publication cannot change
that behavior through a data seed or clock edit.

Propose a new eligibility implementation and identity for publication-driven
resets. It binds the exact producer rule and finality/selection policy. Preserve
v1 constructors, stored definitions, canonical identities and replay. Existing
bookings move to the new terms only through an explicit supported amendment with
expected head/version and retained old/new terms. No background migration retags
old fixings or silently changes their valuation.

Migration 0050 currently requires the v1 implementation and a non-null `fixing_at`;
that schema cannot represent the new semantics unchanged. Introduce typed,
version-discriminated definitions: a v1 clock boundary or a v2 producer/eligibility
rule reference, with mutually exclusive required fields and exact foreign keys.
Retain existing v1 rows and canonical identity bytes. Never fill a v2 publication
time from a legacy clock or repurpose `FixingAt` as a polling estimate. Domain
construction/identity, SQL authoring/read/write operations, snapshot resolution and
dependency planning must change together. Qualify the migration on a populated,
disposable legacy fixture: old histories and retries remain identical, and ambiguous
mappings fail rather than receiving invented terms. The concrete table alteration
is part of S1 migration design, not a second actual-price store.

The new resolver operates on exact pinned candidates before market dependencies
are planned:

| Resolution | Condition | Valuation and commodity risk |
| --- | --- | --- |
| Actual | One eligible selected actual matches index, period, source, quote/units, stage, rule and both time cutoffs | Use its retained value/lineage; no live quote factor for that reset |
| Projected | The selected pending-publication policy explicitly permits projection at this instant, no due-required-evidence failure exists, and the exact projection is still live | Use declared projection and original weights |
| Unresolved | Required evidence is absent/incomplete/ineligible under the selected policy, selected evidence is invalid/conflicting, or a required projection has expired | Return structured diagnostics and no successful complete PV |

Invalid/conflicting selected evidence and lost authority take precedence over
projection. Missing or not-yet-eligible publication permits projection only under
the selected policy; excluded preliminary/unrelated candidates alone do not choose
the mode. Malformed, ambiguous, wrong-source or inaccessible required inputs never
become a successful forward fallback.

`AwaitingPublication`, `MissingRequiredActual`, `IncompleteCoverage`,
`UnknownFinality`, `AmbiguousRevision` and `UnsupportedConvention` are diagnostic
reasons. Lateness relative to expected polling time is an operational diagnostic,
not an alternative financial eligibility rule. Partial known contributions may be
shown with coverage, but cannot masquerade as the complete trade value.

An accepted trade fixing is a separate fact from a market actual being available.
Its selected observation/rule/context pins govern the determined cash. A later
vendor correction is visible as a correction candidate; it does not silently
replace accepted contractual amounts.

## 4. Source adapters and fixing publication

### 4.1 Common producer contract

Publish immutable, exact-version rules with typed relationships to the index,
quote/quantity/currency, source, dated reset targets, calendars/clock, aggregation,
rounding, finality, revision selection and correction behavior. Rule authoring and
approval are separate from running an importer. A worker cannot invent a rule.
Select a market-publication approval policy explicitly: it may permit bounded
publication under an approved producer rule, or require approval of the exact
batch/values. Rule approval does not replace required batch approval. Any required
batch approval binds the proposal digest and is consumed atomically with the
published observations, evidence and receipt, under current approver authority.

Normalize source facts, select an explicit certified cutoff, validate complete
coverage, calculate exact output, and publish `index_fixing` observations plus
typed evidence atomically. Reuse the existing observation store and correction
chains; do not create a second competing actual-price store.

Publication is once per qualified index/period/source revision, not once per trade.
Keep vendor IDs separate from normalized event IDs: one vendor publication may
produce several dated targets. An identical operation retry returns its original
receipt; conflicting reuse of a source revision or operation identity fails.

### 4.2 Power adapter

Each rule explicitly selects operator, node/hub/zone, DA/RT, total LMP/SPP or
declared components, native resolution, operating-day clock, all-hours or exact
published TOU/calendar, interval weights, coverage and index rounding. The current
collectors cover PJM hourly DA/RT totals and ERCOT hourly DA/15-minute RT SPP.
Other products/resolutions require their own qualified adapter.

Select exact immutable interval rows and retain their batch/source/start/end and
provider revision. Preserve zero/negative prices and repeated local-hour identity.
Reject missing, duplicate, overlapping, wrong-node or ineligible-stage intervals.
Do not fill gaps with forwards or infer finality from age. Legacy collected rows
without the required evidence stay ineligible.

The contracted daily index determines its own weights. A duration-weighted all-hours
index is not automatically a quantity-weighted index, a published hourly product,
or the realized cash for a shaped position. Reuse exact interval arithmetic, but
never publish `PowerSettlement.Summary.AveragePrice` merely because it is available.
Apply the selected index precision once; preserve exact calculation evidence.
Trade-shaped settlement remains a separate explicitly supported consumer.

### 4.3 Gas adapter

Bind publisher/feed, exact symbol/location, daily versus monthly method, price
component, unit/currency, publication stage, flow-day calendar and correction rule.
Ingest the licensed published index; do not reconstruct a publisher's index from
unlicensed or incomplete transaction data.

Retain publication date and explicit covered flow dates separately. Weekend and
holiday packages map each contracted flow day exactly once. Missing publications
do not imply carry-forward. A monthly bidweek index and a monthly average of daily
indices are distinct targets. Trade-date and flow-date symbols are distinct.

Publisher precision is retained. Contractual index and cash rounding remain
separate. Source usage/display/derived-data rights must be qualified for the
installation; a source metadata flag or market-data permission is not a license.

### 4.4 Complete publication boundary

Do provider HTTP and normalization outside database transactions. Preview returns
coverage, proposed source pins, exact outputs, rule versions and a digest under a
retained context. Publish rechecks the exact proposal and current authority in a
short transaction, then commits observations, provenance and receipt together.
Changed facts require a new preview/proposal, not a silent latest-price lookup.

A bounded batch either publishes all declared targets or publishes none. Cancellation,
lost authority or failed coverage cannot expose a half-published fixing batch.
Subsequent trade processing may proceed occurrence by occurrence and records each
outcome; it does not pretend all affected books form one atomic transaction.

## 5. Trade occurrences, obligations and valuation

### 5.1 Separate state axes

| Axis | Proposed states/meaning |
| --- | --- |
| Market resolution | Projected, Actual or Unresolved, computed from pinned facts/rules |
| Trade reset | Planned occurrence; accepted fixing determination; linked correction/replacement |
| Cash determination | Planned, incomplete, or determined obligation revision |
| Approval | Unapproved, approved, rejected, held/disputed, superseded; exact action/policy bound |
| Payment | Open or confirmed paid; reversal is a new linked event |
| Due status | Derived from contractual due terms and business time; never evidence of payment |

Use stable occurrence IDs for trade binding, side and reset/payment period. Retain
the generating rule/version and exact trade revision that supplied the terms.
An unrelated memo amendment does not create a new obligation. Material changes
explicitly supersede affected future occurrences and reconcile queued work.
Retain fixed/accepted/paid occurrences and their history; supported economic
corrections create explicit new events rather than erasing them.

An accepted fixing record pins the occurrence, actual observation, eligibility
rule, resolved trade state and certified context. A cash determination pins all
consumed reset versions, side membership signs, quantity, currency, payment terms,
grouping and rounding versions. Reuse unit pricing; apply external trade quantity
once, then contractual cash rounding at the declared obligation scope.

Do not infer netting from equal currency, counterparty or payment dates. The first
gross-side contract retains separate fixed and floating obligations, even when
their net economic PV is displayed. A required netting agreement is an explicit
activation gate and needs its own supported grouping/rounding contract.

### 5.2 Keep cash present after fixing and due dates

Before reset acceptance, provisional valuation may consume eligible actuals under
the selected market context. An accepted reset's exact pins govern subsequent
cash calculations even while its payment obligation remains provisional or
unaccepted. A source correction cannot replace those pins just because the whole
obligation has not been accepted. Accepted cash obligations use their retained
determinations; changing accepted reset/cash amounts requires an explicit linked
correction transition or adjustment.

The valuation planner partitions each contribution exactly once into:

- Remaining projected or unresolved contractual periods.
- Fixed but not yet represented in an accepted cash obligation.
- Accepted outstanding obligations, valued under their explicit cash policy.
- Confirmed paid cash, retained in movement/history reporting and excluded from
  outstanding PV.

Acceptance must move a contribution between buckets atomically, not add a second
cashflow. Preserve separate measures for outstanding PV, determined cash, paid
cash and pending adjustments. A paid trade's outstanding PV can be zero while
its realized cash/history remains nonzero.

For outstanding unpaid cash before due date, consume the selected currency
discount mapping and per-payment DF contract. At/after due date, retain and display
the signed outstanding amount. An explicit due/overdue valuation policy is required
before returning complete PV for that unpaid cash. Fully paid obligations have no
remaining cash to discount and require no overdue-PV treatment.
Face-value treatment, interest/late fees and settlement-date carry are decisions,
not implicit DF=1 or zero. Until a policy is supported/selected, return the known
balance with an unavailable-PV reason.

Lifecycle state and consumed fixing sets join model, numerical policy and market
pins in semantic calculation identity. Different occurrence IDs may share a unit
calculation only when their remaining economics and accepted inputs are identical.
Book/trade attribution and current access remain separate from calculation reuse.

### 5.3 Approval, payment evidence and corrections

Select a versioned approval policy explicitly; there is no automatic approval or
universal assumed separation-of-duties threshold. Approval binds exact action,
determination, amount/currency, source/rule pins, trade state and policy. Check the
approver's current authority again when consuming the approval. Changed relevant
terms, expired authority or superseded determinations invalidate execution.

`RecordPayment` references the approved obligation and full outstanding amount,
payment effective date and stable evidence identity. It records an externally
confirmed fact; it does not send money or trigger fresh market selection. Full
settlement is consumed once. Unsupported partial/over/split payments fail explicitly.
A reversal requires its own exact approved action, reason and original-payment
link. First scope permits a full reversal only. It restores only that payment's
amount to the obligation's outstanding balance exactly once; retain the original
payment and reversal events. Repayment needs a new action and fresh approval, not
reuse of the consumed original approval. A payment reversal changes recorded
payment state; it is distinct from a correction of the contractual economics.

Corrections append source facts. Unaccepted reset proposals may be regenerated
under a new context; accepted reset pins cannot be replaced through a provisional
obligation calculation. Accepted or paid amounts remain unchanged; a separately
approved adjustment links the original determination, correction and new
calculation. Payment evidence is not rewritten.

Propose one explicitly selected resettlement policy: fully restate contractual
cash using the retained quantity, grouping and rounding rules, then subtract the
cumulative already recognized contractual amount (original plus accepted
adjustments) at that scope. This is a difference of rounded amounts, not rounding
a raw price/cash delta. Pending unaccepted proposals may be superseded but do not
count as recognized cash. A further correction measures against the cumulative
recognized amount, avoiding duplicate adjustments. Payment timing does not change
that economic comparison. Other policies require separate support/qualification;
none is an implicit default. Replaying an accepted command never reselects today's
source revision or initiates another payment.

## 6. Proposed persistence, with named consumers

Names below are design contracts, not migration prescriptions. Implementation may
combine compatible headers while retaining exact typed relationships.

| Record family | Current consumer to implement | Required relationships |
| --- | --- | --- |
| Producer/eligibility rule versions and dated bindings | Fixing compiler and publisher | Exact index/quote/source references, schedule target, clock/calendar/TOU and selected policy versions; distinct v1/v2 semantics |
| Source publication evidence | Qualified power/gas importer | Existing immutable power price key `(batch_id,source_id,starts_at)` with stage/release evidence; gas publication/revision and explicit flow-date rows when gas importer ships |
| Fixing publication header and typed evidence members | Reproduction, correction and reconciliation | Emitted `market_data.observations(catalog_id,id)`, exact producer rule/dated target, certified source context; typed FKs to each consumed power or gas row |
| Trade reset/payment occurrences and append-only lifecycle events | Settlement planner and trade replay | Exact retained trade version/binding/side/period/rule; stable occurrence and linked supersession/correction IDs |
| Determination revisions and exact components | Preview/acceptance and outstanding cash valuation | Occurrence, accepted reset/source pins, quantity, currency, due terms and rounding/grouping policy |
| Approvals and transition receipts | Authorized commit/reconcile | Exact proposal/action digest, initiator/approver identity, current-authority evidence, expected version and immutable outcome |
| Payment evidence and linked reversal/adjustment events | Full-obligation payment confirmation | Approved determination, exact signed amount/currency, effective date and stable external/manual evidence identity |

Reuse market/trading knowledge certification and existing job receipts/fences.
Do not introduce arbitrary JSON relationships, a second generic event engine,
banking tables or an outbox with no current dispatch consumer. API payload JSON
does not replace typed reference relationships in persisted financial facts.

## 7. Authorized operations and durable execution

Proposed operation contracts:

| Operation | Exact input and result |
| --- | --- |
| Preview fixing publication | Rule/version, target range and source cut -> coverage, selected facts, output/evidence digest |
| Publish fixing batch | Operation ID, same exact proposal, expected source heads/digest and required exact batch approval -> immutable publication/observation receipt |
| Preview settlement | Trade/binding/occurrences, exact trade/market context and policies -> component amounts, unresolved reasons and proposed determination |
| Accept determination | Operation ID, expected lifecycle head, source context and exact proposal -> retained fixing/obligation events |
| Approve obligation action | Exact determination/action and policy -> single-use approval receipt |
| Record payment / reverse / accept correction adjustment | Exact approved action, expected outstanding state and evidence -> linked immutable event/receipt |
| Read operation status/history | Caller-bound operation/resource IDs -> original authorized control receipt or scoped history |

Mutations run in current control context while retaining explicit business-effective
coordinates. Do not turn the historical viewer into a mutation context. The same
operations serve the UI, agents and trusted workers; provider/model-specific types
do not enter financial contracts.

Source normalization and publication precede reconciliation of affected trades.
Both manual execution and the existing .NET worker use the same operations.
Collection, fixing publication and settlement reconciliation have separate typed
job profiles and permissions. Existing generated pilot closes continue to be
synthetic valuation work; they cannot manufacture official actuals or payments.

Use explicit publisher calendars, polling/retry limits, correction lookback,
backfill ranges and missing-data alert rules. The existing once-daily collector
does not establish publication-time or correction coverage. Pause new schedules
until their selected policies and installation-specific source rights are qualified.

At execution, recheck job owner/service/delegation, source/target scopes, book
access, current trade state and lease/fence. A stale job cannot execute an old
approval or overwrite an amendment. Stable business occurrence/action IDs survive
job retries; job-run IDs are not the financial idempotency identity.

## 8. RLS, identity, transactions and retries

Reuse `ImportObservations`, `ApproveMarketData` and `PublishMarketData` for their
existing distinct market stages. Propose separate scoped capabilities for reading
settlements, accepting determinations, approving cash actions, recording payment
evidence and accepting adjustments. Exact permission names and role grants are
an implementation decision; no role receives them merely by existing.

Market publication grants no book access. `RunJobs`, `RunValuation` and
infrastructure administration grant no settlement authority. Apply ENABLE/FORCE
RLS and restrictive current scope/temporal visibility to all new facts, evidence,
events, approvals and command receipts. Guard exact source/reference versions too.
Readers see only currently authorized books and entitled evidence; raw licensed
source access and derived settlement visibility are separately qualified.

Agents require their own current permission and matching current delegation from
an authorized principal. Services use registered claim-bound authority. Validate
approver identity/authority and any required separation-of-duties policy through
the trusted business operation, never a caller-supplied role or model answer.

For every mutation:

1. Resolve the authenticated actor/scope and recheck current authority.
2. Recognize an identical accepted operation under current access and return its
   original receipt before selecting fresh facts or applying current-head guards.
   Changed submitted terms under that ID conflict.
3. For a new action, lock its occurrence/lifecycle head and approval/outstanding
   state; verify expected versions and the exact approved proposal/source cut.
4. Under a shared publication/acceptance fence, after waits, certify and compare
   the currently selected eligible revision/head for each consumed target against
   the proposal's pinned expected heads, using its selected revision/supersession
   policy. Fixing publication checks normalized source heads; trade acceptance
   checks published fixing heads. Unapproved raw candidates do not silently
   invalidate a different layer. A changed eligible head returns `stale_proposal`
   and requires explicit new preview/approval; never reprice silently. A policy
   selecting a fixed publication vintage compares that selected vintage rather
   than inventing a latest-only requirement. The fence must serialize publication
   and dependent acceptance through commit; an old immutable cutoff or an
   authorization-epoch fence alone cannot provide this guarantee. This market-head
   freshness check applies only to newly admitted fixing/reset candidates and
   explicit correction proposals. Already accepted reset pins govern later
   obligation determination without refreshing those market heads. Payment and
   reversal validate accepted cash/approval/outstanding state, not current market
   heads; a later vendor correction alone cannot block an unchanged approved
   payment or force it to consume new prices.
5. Recheck grants, approver authority and worker fence after waits and through
   commit using the existing authorization-epoch protocol.
6. Commit the event, projection, consumed approval and operation receipt atomically.
   Rollback publishes none and consumes no payment right.

Under that shared fence, a correction that precedes acceptance and changes the
policy-selected head invalidates the new proposal. A correction after acceptance
becomes an explicit correction candidate/adjustment under the selected policy.
An identical accepted-operation replay returns its original receipt before this
new-action freshness check. Serialize competing payment/reversal commands so each
consumes or restores the approved amount only once.
Unknown HTTP/worker outcomes reconcile by the original operation status; they do
not justify a new payment identity. Replay rebuilds state without external effects.

## 9. Settlement workspace

Provide a regular book-scoped Settlements view. Read access does not imply write
authority. Expose available actions from the common operation catalog.

- Reset rows: delivery/flow date, expected versus actual publication, index/source,
  stage/revision, projected/actual/unresolved status, weight and source lineage.
- Payment rows: side/due date, exact determination and display amount, approval,
  outstanding/paid status, current version and correction/adjustment links.
- Filters: authorized books, payment window, index/source and actionable exceptions.
- Inspection: exact as-of/known context, selected calculation/rule versions and
  reproducible component calculations; clearly label provisional/incomplete values.
- Recovery: retain uncertain operation IDs, reconcile original outcomes, clear
  denied financial data and never re-submit an uncertain cash action under a new ID.

Use bounded server queries and incremental pagination. Initial pages load summaries
and exceptions; expand raw interval evidence only on an authorized request.

## 10. Phased delivery and exit gates

| Phase | Concrete deliverable | Exit gate |
| --- | --- | --- |
| S0: close the contract | Select first power/gas index, eligibility version, units/weights, finality/correction policy, payment grouping/rounding and approval policy | No unselected financial field; independent expected calculations and source-use qualification |
| S1: publish real fixings | Typed adapters, source evidence, versioned eligibility, preview/publish/status and pinned close integration | Power and gas synthetic end-to-end cases; old v1 replay unchanged; no automatic schedule activation |
| S2: settlement preview | Stable occurrence planning and deterministic exact reset/obligation components using existing pricing kernels | Indicative vs accepted states explicit; expired/missing inputs never yield a successful full value |
| S3: accept and value obligations | Trade events, determinations, approval/correction transitions and remaining-versus-outstanding valuation | No duplicate PV/cash, stable amendments, due-unpaid balance retained, overdue PV policy explicit |
| S4: record full payments | Approved manual payment evidence, reversal/adjustment and authorized recovery UI | Exactly one recorded business effect under races/retries; no bank-dispatch claim |
| S5: reconcile automatically | Bounded due/changed-publication discovery through existing worker and shared operations | Lease/revocation/cancellation/crash tests, explicit backfill/retry/correction windows and paused rollout |

Each phase includes schema/security, managed contracts, API/UI consumers, synthetic
tests and independent review before publication. Do not land unused schema early.
Source and financial correctness come before schedule enablement.

## 11. Independent acceptance and performance cases

Use original synthetic data in disposable stock PostgreSQL and owned browser/worker
fixtures. These are required checks to implement, not assertions of current coverage.

### Numerical oracle

Specify two daily receive-index/pay-fixed periods, 100 units **per day**, fixed
price 3, DF=1, no fees and explicit common payment dates. Gross side payments are
retained; the following net values are economic reporting, not permission to net.

| Inputs/action | Expected result |
| --- | --- |
| Day 1 final 2.8; day 2 forward 3.4 | Net PV 20; only day 2 consumes a live quote factor |
| Shock day 2 by +0.10 | Net PV 30; day 2 delta 100, day 1 commodity delta zero |
| Day 2 final 3.6 | Floating receivable 640, fixed payable 600; net determined value 40; no live commodity delta |
| Correct day 1 to 2.9 in a later context | New source-based calculation gives 650/600, net 50; earlier pins still give 640/600 |
| Original 640 receivable and 600 payable already paid | Preserve both payments; separately approve a +10 receivable adjustment, with no duplicate original cash |

### Reversal and rounding oracles

- A receivable obligation of 640 confirmed fully paid has outstanding balance 0.
  Its approved full payment reversal restores outstanding balance 640. Retrying
  that reversal leaves 640, not 1,280. Retain both events. Repayment requires fresh
  approval; the original approval cannot execute again. Partial reversal fails in
  the first scope.
- Under an explicitly selected two-decimal, ties-to-even policy, exact original
  cash 1.005 rounds to 1.00 and corrected cash 1.015 rounds to 1.02. The restated
  adjustment is 0.02, not the rounded raw difference of 0.01. If a later correction
  restates to 1.03 after that adjustment was accepted, the next adjustment is 0.01.
  Payment/reversal timing does not alter those recognized contractual amounts.

### Boundary, history and failure cases

- Projection expires at 00:00; no eligible actual at 07:00: unresolved, never the
  expired mark. An expected 09:00 publication is not proof of an actual.
- New publication-driven terms admit an eligible 08:30 final only if its source
  and certified knowledge are visible. Legacy v1 retains its original boundary.
- A 09:05 publication ingested after an earlier knowledge cut cannot repair that
  old result. Later verification of an old collected row is visible only from its
  own certified knowledge time. Missing/preliminary/wrong-source/unknown-stage
  facts fail explicitly when required by the selected policy; invalid selected
  evidence never falls back to projection.
- Power: 23/25-hour days, repeated hours, 15/60-minute coverage, negative/zero
  prices, TOU holidays, unequal weights, components and independent round-once
  oracles. A quantity-shaped calculation cannot silently become the common index.
- Gas: weekend/holiday multi-flow-date publication, month-end splits, distinct
  trade/flow symbols and monthly bidweek versus daily-average definitions.
- Corrections before/after reset acceptance, obligation acceptance, approval and
  payment; accepted reset pins still govern provisional obligations. Original
  receipts remain replayable and old contexts reproducible. Pending approval does
  not restore commodity exposure to an already resolved actual. Exercise both
  correction/acceptance orders under the publication fence and fixed-vintage as
  well as superseding-revision policies.
- Two trades sharing an instrument retain separate state/quantities and book
  rights. Amending one does not change the other's cash; paid/superseded history
  survives later amendments and book-scope changes.
- Unbound/cross-book reads, extra permissive policies, restricted evidence and
  unauthorized operation-status lookup cannot bypass restrictive RLS.
- Initiator/approver/service/agent delegation expiry or revoke/restore after waits;
  stale expected versions; correction/approval/payment races in both orders;
  cancellation/lease loss/rollback publish no partial financial action.
- Identical command retry after corrections/later payments returns its original
  receipt. Changed content conflicts. Unknown outcome reconciles without another
  payment; a new scheduler run does not create a new financial effect.

### Performance qualification

Read each source/range/cut once per bounded publication operation, map intervals to
targets in batches, and calculate each unique index/rule/period once. Reprice only
affected reset/instrument work; apply each trade's quantities outside shared unit
calculations. Reuse immutable inputs within the authorized operation only.

Record unique indexes, native intervals, targets, instrument/state variants,
holdings, scenario counts, actual evaluated/reused work, SQL/read/round-trip counts,
wall time, allocations, resident memory and the exact environment. Exercise late
corrections and concurrent users/workers too. The page target remains below one
second under a qualified load envelope; larger financial batches use durable jobs
and bounded progress reads. Existing pricing microbenchmarks do not qualify this
new ingestion/lifecycle path or every maximum-span discount interpolation.

## 12. Decisions required before activation

| Decision | Why it is a gate |
| --- | --- |
| First power operator/node/index, DA/RT, LMP/SPP, block/TOU and source rights | Identifies the actual settlement product and data adapter |
| Gas publisher/feed/symbol, daily/monthly definition, flow calendar and rights | Defines publication identity, units and covered contractual periods |
| Publication/finality/revision selection and missing-data treatment | Determines eligible actuals and whether early/late/preliminary facts are usable |
| Quantity, reset/index weighting, cash grouping and rounding stage | Determines settlement cash; equal-day, energy and time weights are not interchangeable |
| Approval/separation-of-duties and correction/resettlement policy | Determines which exact action may change accepted cash and who may authorize it |
| Due/overdue valuation and payment evidence requirements | Determines outstanding PV after due date and what establishes confirmed payment |
| Existing booking opt-in and historical implementation retention | Preserves old identities and prevents silent contract changes |
| Publisher timing, retries, backfill and correction monitoring windows | Establishes the operational service level without changing economic eligibility |

Implementation may proceed on deterministic synthetic contracts while these
installation-specific selections remain unset. Real activation requires explicit
selected policies and qualification. Open choices are not filled by library,
browser, provider or agent defaults.

## 13. Research and related contracts

- [Architecture](architecture.md), [lifecycle](lifecycle.md),
  [market model](market-model.md#8-settlement-references-and-realized-observations)
  and [business scenarios](business-scenarios.md#next-settlementroll-acceptance-scenarios).
- [PJM unverified-price feed definition](https://dataminer2.pjm.com/feed/rt_unverified_fivemin_lmps/definition):
  unverified operational prices are distinguished from verified values.
- [ERCOT market prices](https://www.ercot.com/mktinfo/prices): LMP/SPP products and
  price-correction stages are explicit.
- Natural-gas index publications: publisher-specific stages and trade/flow-day
  calendars need exact selection.

Research informs qualification; these links do not select an installation's
contract, grant data rights or establish a production implementation.


The narrower power-only D201 consumer is implemented as [shared payment-lineage
planning and monthly gross preview](settlement-preview.md), following the accepted
SP1–SP9 specification and 0093 meter-finality prerequisite. It does not complete
global S1/S2 or their gas dependencies. Approvals, accepted cash, financial claims,
obligations, payments and settlement workers remain unimplemented.
