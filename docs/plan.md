# Delivery plan and acceptance criteria

Status: design baseline approved for implementation on 2026-10-16. Milestone 0 is complete; no
application implementation milestones below are complete.

The [verification workflow](verification.md) now defines a single local/CI command
covering the existing managed, frontend, database and browser suites. The complete
command passed on the Ubuntu 24.04 hosted runner on 2026-10-17; verification
records the exact run and environment. Production deployment remains unqualified.

The implemented trading workspace now has a shared Blotter with individual trade
rows, managed pivots, account-saved layouts, exact-data exports and shared charts.
Eight synthetic weekday futures bookings cover the four standard market frames.
Immutable futures contracts are shared directly by curve consumers and trades across books.
Typed execution P&L and deterministic base/up/down open sensitivity are implemented;
daily/final settlement, variation-margin transitions and generic futures PV remain
unimplemented. Quantities, quote scales, numerical policy and exact inputs stay in
the managed backend.


Temporary futures and swap editors expose Price and Risk before booking. Trade
parties and book membership are required only for booking. The backend converts
explicit typed quantities against the selected pricing reference revision; it
rejects unsupported or inexact decimal conversions rather than choosing a rounding
policy. Price returns a pinned historical context reused by Risk. Draft risk
requires `RunDealRisk`, distinct from book-wide scenario authority, and reprices
the same instrument under immutable shared one-trading-tick quote scenarios.
Only declared live quote lineage is shocked; all-in marks acquire no inferred
upstream exposure. Interest rates remain fixed. Results retain exact rational
base/up/down cash, delta and gamma, including nonterminating daily-average
sensitivities. The UI formats those results through backend amount formatting,
clears them when terms/context change, and never books a trade as a side effect.
Swap drafts report PV; futures drafts report execution P&L and open exposure.
Variation-margin processing and full futures settlement PV remain unimplemented.

Simple futures contracts are leaf instruments keyed by the exact futures-series
primary key, independent of the curve recipe. Explicit pricing bindings retain
selected curve/member and quote policy. Composed products use `SidedInstrument`
with nonempty immutable memberships; fixed/floating swaps remain its current
consumer. Fused exact aggregation preserves final arithmetic and trade-owned
quantities. Migration 0019 preserves historical UUID aliases and pricing bindings;
new future bookings create neither a side nor a wrapper definition.

Latest-head futures booking amendments append immutable versioned trade states.
They require explicit reason/effective time, current authority and expected-head
concurrency. Historical and superseded states remain read-only; arbitrary insertion
into earlier history, settlement and general lifecycle execution remain work.
Readable trade/instrument references survive amendments, book moves and definition
reuse. They appear in new table layouts, the editor, exports and risk input receipts;
existing saved layouts retain their choices. The header distinguishes working
knowledge, historical views, selected price date and actual today in the user's
zone. Power TOUs and Reference data are under Settings; visible branding is LeanETRM.

Trade owns events, TraderId, TradeDate, counterparty, memo and legal entity.
The backend assigns trader to the original authenticated human workspace member,
TradeDate to the UTC booking date and execution time to persistence time. These
facts are read-only and survive amendments. Memo and exact-version parties remain
editable trade terms. Shared presentation renders Trade Terms above the product
editor and its sides; quantities belong to instrument bindings. Instruments
remain unit definitions with separate unit-aware Quantity values held by Trade per
instrument binding. Reuse the existing project-owned Quantity, Money and UnitPrice
contracts backed by ExactDecimal; external quantities live outside the instrument
definition.

Versioned marked economic fields now define managed instrument/side identity.
Request-local loading and swap unit-value reuse consume the canonical keys;
atomic database uniqueness independently retains one definition across bookings.
Trade quantities, attribution and parties remain outside economic identity.
Futures SeriesKey is marked and matches the exact series primary-key columns;
LotSize is resolved from those immutable terms. Fixed-side quote references now
participate in keys, and floating sides separate contractual curve/observation
configuration from resolved pricing bindings. Swap discounting remains marked.
Lookup and uniqueness use typed primary/candidate keys; managed digests remain
calculation aids rather than database authority. Observable/factor identity is
separate from full contract equivalence. Options, standalone physical forward
booking, VaR/covariance and cross-revision forward-observable normalization remain
explicit later work.

Financial gas and power swaps now have local booking/amendment operations, a
shared trade blotter and an editor with independently valued side tables. The
booking model supports one whole delivery month, monthly-index or complete daily
calendar observations, independently generated payment dates and explicit currency-default
or no discounting. Daily resets aggregate into one monthly floating payment;
retained exact lookups, cash allocations, DFs and PVs are inspectable. Four explicit
weekday dates have synthetic monthly/daily gas and power examples.
Each side now selects either a period-end day lag or a day of the following
month, with explicit day basis, short-month behavior, date adjustment and exact
calendar where consumed. Generated date-only rows retain the rule evidence.
New entry has commodity-filtered indices/units and separate reset-frequency and
averaging-method fields. Earlier manual-date revisions remain inspectable without
inferred replacement conventions; amendments must select the new rules explicitly.
The swap editor can resolve published schedule symbols against its exact retained
context, review the source version/occurrence and apply a literal delivery date.
Suggestions obey that same cutoff. Payment-policy choices come from the backend's
typed supported conventions; reference choices remain database-backed. The standard
local example uses USD from the currency reference table with synthetic prices.
Shared tables now guard row activation during selection/scrolling and use a single
scroll container for schedule inspection.
The managed core additionally validates calendar-based and quantity-weighted
rules; those need persisted authoring before booking controls are exposed.
Actual fixings, recurring payment strips, swap risk, physical products and PPAs
remain work. Current shock controls support futures only.
FixedSide and FloatingSide now independently generate and value their own
schedules through Side.PV. Each side currently has one payment; payment/delivery
dates and DFs can differ. Side results retain normalized unit quantities, lookup
terms/weights, prices/lineage, exact cash/PV and discount construction evidence.
Instrument.PV only sums membership-signed side PVs under one full context; trade
quantity scales afterward. Recurring periods and actual fixings remain work; the browser inspector
now exposes the actual valued payment and reset rows. Options retain explicit sibling references
and distinct base/final valuation modes; the exercise-control accounting choice
in O13 is still open and no option model is enabled.
The instrument editor uses vertical side fields, separated from trade-level
booking terms. Future multi-side editors align corresponding fields; delivery
start/end and currency may initialize only unset counterpart fields during entry,
without a copy button. Each value stays editable and later changes do not overwrite
the other side. Fixed/floating entry is implemented; persistent cross-side linking is not.
Fee implementation is deferred: individual-execution and grouped-position fees
need explicit calculation scope, periods, netting, tiers and allocation. Expected
charges, statement evidence, adjustments and payments remain distinct; missing fee
coverage is unknown. No unsupported fee controls or assumed rates are exposed.
Reconciliation reporting and published closes follow supported financial/lifecycle
contracts. Existing execution-P&L diagnostics remain gross and exclude fees.

Amount precision is now scoped to shared current deal presentation, with separate
trade/fixed/floating role policies and optimistic version checks. The Profile
precision fields are removed and old browser values are ignored. Futures prices
retain tick-derived decimal formatting; calculation precision and contractual
cash rounding remain explicit, separate policies. These controls persist on the
deal's role, not the reusable economic side identity.
Money/Quantity/UnitPrice
support formatted text with exact-reference labels. Reference data includes a
quantity-conversion consumer over common energy, power, volume and mass units;
exact ratios and final explicitly selected rounding stay in the managed backend.
Physical gas heating-value, density and power-duration conversions remain separate
future consumers. Supplemental unit catalogs preserve existing market/trade pins.

The standalone [power TOU preview](power-preview.md) now connects editable masks
to actual selected curve contracts and synthetic flat settlements. It qualifies
current-input calculations, explicit clock captures and fixed-MW integration;
durable TOU/product publication and historical clock selection remain work.
The next quantity entry contract supports explicit total MWh and flat MW choices.
It must retain side delivery/allocation rules and typed generated quantities,
preserve total energy through interval allocation, and reject missing conversion
context. The unsaved preview now provides total-MWh entry with explicit flat-rate
allocation and exact interval quantities alongside flat-MW integration. Independent
checks cover DST, TOU/holidays, signed quantities, conservation, incompatible
references, cash rounding and overflow. Booked-side rate integration remains work.
Legal-entity/counterparty editors now append immutable versions through separate
party permissions, with retained history, optimistic conflicts and safe retries.
Immutable booking facts now reference actual scoped human users and preserve
the original captured booker across account changes and financial amendments.
Server-owned UTC dates/timestamps and separate economic coordinates follow D129.

The initial reference-data schema and disposable database checks are implemented.
The first local market-data slice now also implements commodity curve building,
typed units/money, generic series histories, a dated zero-rate/discount-factor
example, additive market/reference storage,
common roles, persisted current authority and database RLS. See [implementation.md](implementation.md) and [security.md](security.md).
This does not complete any application/service milestone. Changes follow the
publication discipline below.
The intended eventual hosted domain is leanetrm.com.

This plan separates the first runnable foundation from later product breadth.
Each milestone produces a reviewable working result with tests and documented
limits. It is not a promise to deliver all future capabilities in one commit.

The next settlement/roll/risk acceptance cases and current coverage are explicit in
[business scenarios](business-scenarios.md). They distinguish existing expiry/roll
primitives and the bounded shared scenario engine from unimplemented fixing
selection and variation margin; planned cases are not counted as passing tests.

Read [the architecture](architecture.md) and [decision record](decisions.md) for
the contracts these milestones must preserve.

The implemented bitemporal context now covers existing commodity/rate curves,
observation histories, immutable definition/reference bundles, schedules and
contract-date binding under current RLS. Scope, explicit revision selection and
remaining limits are recorded in
[implemented scope](implementation.md#shared-historical-market-view-and-administration).
Workspace identity/role administration and atomic access audit are implemented
in the loopback preview. Production credential integration, initial-admin bootstrap,
durable curve activation, broader trade lifecycle history and failover/restore
qualification remain work. Exact legacy input editing stays a separately labeled
mode and does not claim timestamp certification.

Current sequencing: the agreed curve structure is approved for a first bounded
implementation. Establish dated contract histories, units/money and explicit pricing
modes first, with the identity/authorization boundary implemented alongside them;
instrument behavior follows. The next power design starts with hourly curves,
with UTC intraday intervals, explicit market-clock
labels, separate day-ahead/real-time identities and contractual fixing rules.
Monthly/peak/off-peak aggregation and hourly price shaping are later qualified
operators, not automatic conversions. See [power cases](business-scenarios.md#power-curves-and-settlement-acceptance-cases).
The requirements in [market-data.md](market-data.md) distinguish the bounded
implementation from broader planned service behavior.
The first [market-model proposal](market-model.md) makes the required field/FK
links and synthetic direct/dependent examples concrete for review. That model
informed the local additive migration and managed core; broader shapes
remain under review.
Curve ownership, deep composition and the futures-to-forward boundary are now
agreed in that document. The implemented subset has explicit mapping/transition
and numerical contracts; broader policies and service integration remain work.

The bounded preview implements typed series/observations and deterministic curve
construction with exact revision pins and current scoped access. Credential
verification and production API integration remain work. Cross-catalog activation, customizable
policy languages, role hierarchies, book exceptions, VaR and broader curve operators
remain later work. Certified timestamp selection covers the existing market,
reference and schedule screens under the qualified primary's publication contract;
trade/lifecycle history and operational recovery remain unimplemented. Per-contract diagrams and
calculation tables expose actual evaluated dependency values under the selected
input frame; they neither calculate browser prices nor infer risk allocations.

Keep reference/setup scope justified by current consumers or executable baseline
cases; retain a [table inventory](reference-data.md#current-table-consumers-and-implementation-policy).
The interest-rate example consumes explicit Actual/365 Fixed and simple-compounding
identifiers through a small project-owned implementation. It uses dated samples with histories;
it does not implement bootstrapping, interpolation or discounted instrument PV.
FX conversion, automatic rate-payment schedules, broader rate conventions and
additional convention catalogs stay later scope until an implemented workflow
requires them. The supported units and financial conventions remain project-owned;
new breadth must preserve exact amount semantics and avoid silent defaults.

The [mixed-resolution gas acceptance case](examples/mixed-resolution-gas.md) now
runs 24 managed checks: daily near-period contracts and monthly tails, a separate
BALMO benchmark/basis, eight retained dates from mid-month through following
month-end, explicit mapping failures and replay. It corrects the earlier proposed
monthly-future base: only a spread defined against that reference may consume it.
The fixture is now persisted/adapted in an isolated `--example mixed-gas` preview,
with 24 HTTP checks over all eight frames and the original preview's 56 HTTP
and 10 adapter checks still passing. Qualified index fixings, source publication/knowledge eligibility,
weighted aggregation and broader mappings remain work. The mixed monthly tail
here uses forward marks; mixing futures and daily-index settlement conventions
needs explicit specification support. No new reference catalog is justified
merely by introducing another delivery frequency.

The next example is under review in
[separate source curves and rollover](examples/curve-composition-rollover.md):
daily-only/monthly-only sources, explicit splice selection, named BALMO windows,
and distinct tests for prompt rollover, new listing/eligibility and delivery
progression. Do not describe the existing demo's hard-coded per-date request
lists as an implemented roll engine. The month-end daily-window choice remains
open; no financial default has been selected.

The current-contract rule is now agreed: advance at recorded expiry through the
retained sequence; far-end listing remains independent. The first bounded backend
implementation is [the schedule-backed relative-contract resolver](schedules-and-contract-sequences.md)
over supplied futures rows, reusing series/milestone references. Tests qualify
explicit revision corrections, coverage, expiry boundaries and eligibility.
Certified historical schedule resolution and bounded monthly generation now run
in the preview; qualified futures-series generation and durable curve activation remain work.
Do not build a broad date scripting language or duplicate holiday/expiry catalogs.
The separate daily/BALMO month-end selection remains open.

Monthly anchors, explicit calendar/adjustment choices, indexed date expressions
and private curve contract date drafts now run in the browser preview. See
[implemented scope](implementation.md#independent-schedules-publication-and-contract-dates).
Schedule drafts and immutable publications now persist in PostgreSQL with certified
effective/known resolution. Qualified futures-series generation and activation of
priceable curves remain work.

Shared immutable schedule payloads are agreed in D58. The first
[shared utility](../src/OpenEtrm.Shared/README.md) passes 31 checks for date-set
composition, adjustment collisions, canonical sharing and explicit UTC interval
selections. Its microbenchmark separates payload construction from warm reuse.
The browser now configures named symbolic date references such as `#ng1` over
explicit contract schedules. Combine/compare/diff stay coding utilities. Kept
shortcuts are immutable private preview artifacts, not published reference data.
Current-input clock/TOU compilation and certified historical schedule/market
selection are implemented within the documented preview boundaries. Durable
clock/TOU publication remains work; broader performance budgets require application workloads.

Next schedule work extends the existing start/end/last-trading bindings to typed
custom fields and stable mappings across corrected definitions. Qualify
effective/known selection and retained outputs before durable curve integration;
reject cycles between source schedules, contract construction and relative
selectors. The private preview implements indexed monthly references and
`oe.ng + 1bd`; published schedule versions and explicit captured views are
implemented. Backdated replay for future trading/lifecycle domains remains work.
See [date-field bindings](schedules-and-contract-sequences.md#date-schedules-before-contract-construction).


## Milestone 0 — Record and review the design

Deliver:

- Domain vocabulary, side ownership and definition/version boundaries.
- Shared factor scenarios, black-box pricers and quantity-scaling contracts.
- Curve dependencies, units, precision and historical input semantics.
- Administration, delegation, provider boundaries and durable execution rules.
- Public-content instructions, proposed choices and open decisions.

Acceptance:

- Independent adversarial review covers numerical reuse, authorization, temporal
  semantics, execution failures and public release boundaries.
- Review findings are resolved or explicitly recorded as unresolved decisions.
- Documentation clearly distinguishes requirements, proposals and implementation.
- Links and the complete outgoing diff are checked before publication.

## Milestone 1 — Build and prove the deterministic core

Build an independent core library with no database, network or model-provider
dependency. Pin the supported runtime/toolchain and dependencies.

Deliver:

- Typed monetary amounts, prices, quantities, units and coefficients.
- A documented bounded numerical policy, canonical decimal input/output,
  conversion rules, overflow/error behavior and explicit rounding boundaries.
- Independent versioned side definitions, memberships, instruments, portfolios, books and trades.
- Common instrument/side behavior interfaces and abstract implementations. Each
  side generates its schedule and computes its PV contribution; ordinary
  instruments combine schedules and sum normalized signed side contributions.
- A minimal explicit dated schedule/normalized-shape contract; automatic calendar
  breadth is not required to prove the core.
- UTC instant storage with separate date/local-time types and versioned clock
  provenance. User display zones do not affect economic results. Automatic TOU
  and rate schedules require the gates in
  [time-and-schedules.md](time-and-schedules.md) before being enabled.
- Fixed/floating linear valuation against an explicit immutable market context.
- A futures instrument/side contract and a synthetic fixed-lot futures valuation
  convention, with explicit settled basis, realized/unsettled variation margin,
  remaining risk and supported closeout policy. See
  [reference-data.md](reference-data.md).
- Quantity-scaling declarations and structured errors for unsupported behavior.
- Versioned inspect/compose/preview/scenario request and response schemas, written
  for both human and agent clients from the beginning.
- Explicit business/knowledge context contracts for resolved historical inputs;
  fixture contexts can prove core behavior before the persistence resolver exists.

Initial pricing conventions:

- Explicit dated buckets and a declared total-quantity basis are the smallest
  supported shape. Rate/per-occurrence quantities are enabled only with their
  own normalization and time-integration tests.
- Start with single-currency cashflows and aggregation. Unsupported FX or
  cross-currency configurations return a structured error.
- Future observations use explicitly bound forward inputs; observations already
  fixed under the contract's schedule require the appropriate available realized
  fixing. A missing fixing never silently falls back to a forward quote.
- Undiscounted valuation is an explicit named mode. Discounted valuation requires
  pinned discount inputs for the payment dates; it cannot silently use one when
  those inputs are absent.
- Explicit versioned transformations normalize each side to the containing instrument
  quantity basis. Pay/receive labels do not add a sign beyond the signed
  membership coefficient and the trade's quantity for that instrument.
- Reject unsupported pricing/lifecycle conventions until their semantics and
  tests exist; a linear product label is not evidence of complete coverage.

Acceptance:

1. Construct a fixed/floating instrument from two independent sides.
2. Reuse one side across multiple instruments without sharing membership weights
   or the quantities held by trades.
3. Preserve correct units through price-times-quantity and heat-rate conversion.
4. Reject incompatible currencies/units, unsupported precision, overflow and
   missing inputs.
5. Prove positive, negative and zero quantity behavior and signed coefficients.
6. Prove normalized shapes share calculations only when the required inputs agree.
7. Prove settlement/aggregation rounding follows the declared contract.
8. Prove repeated evaluation of the same input manifest gives the same outputs.
9. Verify aggregation is identical across input enumeration, worker counts and
   execution scheduling under the documented reduction policy.
10. Test future forwards, realized fixings, missing fixings, explicit discount
    modes, incompatible quantity bases and unsupported currency configurations.
11. Verify schedule/PV polymorphism through the common interfaces, schedule reuse,
    composite delegation and stable sibling bindings. Reject missing peers and
    recursive instrument/side dependencies.
12. Hold an option and a separate premium instrument directly in one Trade's
    conceptual fixture; lifecycle contract tests preserve the premium's recorded
    history when the option changes. Distinct obligation histories do not collapse
    merely because a reusable definition is economically equivalent. This is a
    domain-contract test, not an option-pricing product release.
13. Reconcile synthetic futures execution-to-current P&L, settled variation,
    unsettled amounts and outstanding risk without double counting. A settlement
    can reset post-settlement value without removing market exposure. Reject
    unsupported physical-delivery, margin and settlement conventions explicitly.

Independent review focuses on numeric policy, identity and false quantity reuse.

## Milestone 2 — Shared scenarios and dependency propagation

Deliver:

- Dated primitive factor identities and explicit prompt-alias resolution.
- Typed, versioned curve recipes and a validated compiled dependency plan.
- Synthetic gas base/basis and power conversion/location examples.
- Base/up/down factor overlays over an immutable market snapshot.
- A black-box unit-pricer contract with declared dependencies.
- Repricing, delta/gamma and scenario P&L with explicit units and bump conventions.
- Within-run reuse and instrumentation of actual evaluated work.

Acceptance:

1. Apply an absolute base-gas shock once in each scenario.
2. Propagate through location gas, heat-rate-derived power and location power.
3. Reprice different dependent contracts against that same scenario; do not
   mutate or separately shock trades.
4. Verify the synthetic 0.10 USD/MMBtu, 7 MMBtu/MWh and 10,000 MWh case produces
   7,000 USD undiscounted scenario P&L under the stated fixed inputs.
5. Verify direct gas/basis and inherited power exposures use the right units.
6. Leave an unrelated factor's dependent computations unchanged and unexecuted
   where independence is proven.
7. Demonstrate a shared derived node is evaluated once per scenario.
8. Demonstrate eligible unit calculations are reused across quantities, while
   different shapes/terms have distinct valuation identities.
9. Compare reuse results with a slow independent full-portfolio repricing path.
10. Verify quote-input shocks and direct output-curve shocks are distinct requests.
11. Reject cycles, incomplete mappings and missing data; never return default zero.
12. Use a synthetic nonlinear black-box test pricer to prove scenario machinery
    does not assume market linearity. This test is not an option-product release.
13. Retain actual dated contracts so a prompt roll creates a new context and does
    not change a previously retained run.
14. Reject zero/invalid bumps, incompatible shock units, unsupported percentage
    conventions and insufficient numerical resolution with structured errors.
15. Distinguish explicit all-in inputs from base-plus-spread definitions. Reject
    incompatible mark kinds and missing choices; no inferred implied spread,
    dependency changes or silent fallback. Other declared pricer inputs remain.
16. Prove base/up/down repricing determinism and obtain sensitivities from PV
    outputs, without allocating risk from spread levels or price proportions.

Independent review focuses on shock propagation, units, finite-difference
cancellation, cache identity and missing dependencies.

## Milestone 3 — Persist operations with administration and history

Deliver:

- Reproducible stock-database setup and versioned additive migrations.
- Relational reference tables for currencies, units/dimensions/conversions,
  commodities, locations, exchanges/clearing houses, clocks/calendars, rate
  conventions and futures product/series definitions. Exact-version foreign keys
  constrain all core relationships; referenced data is retained.
- Public placeholders/synthetic fixtures, source evidence and a qualified
  installation-data import/approval path. No real data activation or distribution
  is implied by a schema validation pass or storage seal.
- Separate administrative/migration and unprivileged runtime connections.
- Principal lifecycle, agent/service identities and a scoped capability matrix.
- Explicit provisioning with no published default credentials.
- Versioned domain records, application revisions and immutable input manifests.
- Bitemporal facts/relationships and a qualified date-to-knowledge context resolver,
  including market observations, later imports/corrections and coverage limits.
- Consistent effective_from/effective_to and known_from/known_to columns, UTC
  knowledge timestamps, documented half-open boundaries and a reserved finite
  open-ended sentinel. Keep source observation/contract dates separate.
- Typed read, compose, preview, book, amend, calculate and request-status operations.
- Atomic idempotency records and append-only application audit.
- Current-authorization checks across historical reads, results and exports.
- Typed lifecycle commands/events with retained before/after bindings, validated
  transitions and state owned by the trade for its instrument bindings and obligation occurrences. See
  [Events and lifecycle](lifecycle.md).

Acceptance:

1. Provision a new isolated installation using only documented public artifacts.
2. Book a trade's instruments and quantities atomically through an authorized operation.
3. Resolve a duplicate request to its original outcome; reject a reused request
   identity with a different payload.
4. Recover after an uncertain response without creating another booking.
5. Reject stale expected versions and preserve earlier revisions after amendments.
6. Verify backdated changes do not rewrite an earlier input manifest or result.
7. Verify users/agents cannot cross workspace/book/portfolio scope or inspect
   hidden definitions through searches, errors, counts, caches or exports.
8. Complete a revoke and verify subsequent operations and cached/historical reads
   are denied; cover revoke/restore and pooled connection reuse.
9. Verify runtime database credentials cannot bypass the intended data boundary.
10. Verify administrative capability does not silently imply business-data access.
11. Verify approvals, where configured, bind the exact scope/payload/version and
    cannot be reused after their meaning changes. Atomically consume single-use
    approvals; test concurrent requests with different identities and an expired,
    disabled or no-longer-authorized approver.
12. Restart the service and retain bookings, audit, request outcomes and run inputs.
13. Test delegated scope intersection, expiry, revocation and disallowed
    redelegation, including attempts to inspect another principal's request status.
14. Amend one trade using a shared definition and verify all other trades and
    historical calculations retain their original terms/state.
15. Distinguish a booking correction from an economic early termination; retain
    existing deliveries, realized fixings and payable/receivable obligations.
16. Replay business events into the same state without sending external actions,
    repricing with current market data or reinterpreting events under new rules.
    Verify event/schema upgrades using retained handlers or tested migrations;
    unsupported reconstruction must fail explicitly.
17. Recompute remaining schedules and quantity-scaling eligibility after partial
    settlement/exercise/amendment. Preserve provenance while permitting reuse of
    equal normalized remaining economics; ordinary risk freezes actual decisions
    and realized fixings.
18. Reproduce historical and corrected-past views of a backdated amendment and a
    corrected quote. Retain historical portfolio/book/trade and curve/reference
    relationships, while permitting future-delivery trades already known then.
19. Qualify date-cutoff resolution under concurrent commits, lag, crashes, equal
    timestamps, clock regressions and missing retained coverage. Freeze successful
    context resolutions and reject unverifiable cutoffs without current fallback.
20. Validate one consistent cross-scope cut and complete historical dependency
    closure, including a trade referencing a concurrently published shared curve.
    Historical book membership cannot bypass current record classification.
21. Test exact shared boundaries, multiple same-day corrections, equal timestamp
    handling, UTC/zone conversion and sentinel round trips. Queries must neither
    double-count boundary versions nor treat the sentinel as a real business date.
22. Reject orphan and cross-catalog/revision reference writes at the database,
    incompatible unit/commodity dimensions, frozen-definition changes and seal/write
    races. Initial storage checks exist; runtime authorization, publication and
    activation integration remain incomplete.
23. Reject activation of incomplete placeholders, candidate series with an
    unqualified product, missing evidence/calendar coverage, conflicting dates
    and missing/expired use permissions. Keep hosted data rights separate from
    the public code license and installation-internal use.

Application revision pins implement the knowledge coordinate behind the required
historical date/time selector. Before accepting that selector, implement and test
the knowledge-time contract in [Historical context and temporal data](context.md).
Pre-commit row timestamps are not permanent commit/availability records.

Independent review covers authorization, temporal behavior, transaction races,
idempotency and migration/release boundaries.

## Milestone 4 — Durable runs and a usable browser slice

Amount presentation keeps final money decimal controls under collapsed Advanced
settings, defaulting to three places. Ordinary deal entry prioritizes contractual
terms; noncash amounts use compact backend formatting and futures tick places.

Deliver:

- Durable run states, worker ownership/fencing, bounded concurrency and restart
  recovery over pinned inputs.
- Progress, diagnostics, cooperative cancellation and request reconciliation.
- Browser flows for identity, scoped reference selection, side/instrument
  composition, trades, market recipes, risk results and administration.
- Calculation receipts with explainable dependency paths and input provenance.
- Market data navigation by commodity or by futures/forward market type, plain
  business labels and no visible internal identifiers.
- Distinct business reference and market setup views; internal relationship and
  revision tables stay behind the scenes. Typed locations support exact supplied
  GPS points; pipeline routes, region boundaries and topology are later scope.
- User profiles for time zone and extensible remembered presentation preferences;
  compact minute-level context labels preserve exact selected versions. Browser-local
  preview preferences do not complete authenticated account persistence.
- A historical environment selector that propagates one resolved context through
  navigation, tables, charts, schedules, risk and reports, with read-only defaults.
- Due-event reconciliation over retained occurrence IDs, version checks and an
  atomic outbox for committed lifecycle notifications/downstream work.

Acceptance:

1. Complete the first synthetic trading/risk workflow through the browser.
2. Show base/up/down values, risk units and the path from factor to instrument to trade.
3. Show where unit computations were reused and how quantity was applied.
4. Show errors and partial runs explicitly; do not present incomplete portfolio
   totals as successful complete risk.
5. Interrupt a worker and resume/reconcile without duplicate committed results.
6. Reject publication from a worker that has lost its execution fence.
7. Verify cancellation-requested and cancelled are distinct, truthful states.
   Race cancellation against successful publication in both orders and assert one
   terminal result; stale workers cannot overwrite the outcome.
8. Revoke access during queued/running work and verify it cannot publish or return
   results under revoked authority. Include revoke/restore and a revoke committing
   between an earlier permission check and the attempted publication commit.
9. Verify a schema/model upgrade does not rewrite old receipts or claim replay
   using a different engine. Report unsupported replay honestly.
10. Race a manual amendment against a due event. Reject/reconcile stale work,
    deduplicate repeated delivery and retain already-realized obligations.
11. Verify expiry does not mean payment or physical settlement is complete, and
    an unresolved fixing cannot become an automatic lapse or zero valuation.
12. Restart and redeliver outbox work without repeating business effects; rebuilding
    projections does not reissue notifications, payments or exercises.
13. Allow distinct actions and reconsideration after missing facts on one stable
    occurrence, while preventing another external effect under a new attempt key.
14. Revoke recipients/delegation or change destination/automation policy between
    outbox enqueue and dispatch; block unauthorized delivery and retain outstanding
    obligations for separately authorized service/operator processing.
15. Change context during pagination/navigation/running requests and discard stale
    responses. All business panels use the same context; queued calculations keep
    their pinned inputs, and historical views retain current authorization.
    Include A-to-B-to-A context switching and reject unsupported lifecycle-roll or
    hindsight coordinate combinations.

Independent review covers asynchronous behavior, result publication, current
authorization and retained-version boundaries.

## Milestone 5 — AI-operated application

Design the operation schemas from milestone 3 for agent use; this milestone adds
the model runtime and client adapters rather than a separate business-rule path.

Deliver:

- A discoverable versioned operation catalog with typed inputs/outputs, required
  authority, bounds, temporal context and idempotency behavior.
- API and MCP adapters invoking the same operations as the UI.
- A provider-neutral adapter and capability contract, with at least two adapters
  or an explicitly supported external-agent route plus adapter conformance tests.
- Scoped agent identities, delegation, allowed provider/model routing, protected
  credentials, resource budgets and auditable task progress.
- Exact-payload proposals/approvals and structured validation failures.

Acceptance:

1. An agent discovers permitted operations and available reference definitions.
2. It composes a linear instrument from reusable sides, previews the cashflows,
   resolves missing/ambiguous terms and proposes a booking.
3. It obtains approval only where policy requires it and books exactly once.
4. It shocks a shared factor, runs risk and explains trade-value changes from
   retained calculation evidence.
5. It answers why an unrelated trade did not move without inventing inputs.
6. A second provider/agent route completes the same deterministic business task;
   compare state/results, not wording.
7. Denied permissions, stale proposals, malformed tool arguments, provider errors
   and step/budget exhaustion produce controlled outcomes.
8. Untrusted text cannot grant authority, approve a mutation, access secrets or
   redirect data to a disallowed provider.
9. Disconnect after a booking response is lost and reconcile the committed
   request outcome before attempting another mutation.
10. The browser and direct API remain usable without a configured model provider.
11. Agent tools honor the selected historical context across discovery, reads,
    previews, calculations and exports. A missing/invalid context cannot silently
    fall back to current business data.

Independent review covers delegated authority, data routing, approvals, tool
discovery and provider-failure/retry boundaries.

## Milestone 6 — Performance qualification and public baseline

The baseline release combines milestones 1–5. Record actual capability and limits;
do not imply this is a complete ETRM or production-qualified deployment.

Publish a reproducible benchmark matrix with synthetic workloads:

| Axis | Required cases |
| --- | --- |
| Trades/instrument bindings and unique unit definitions | Many copies of a few contracts; many distinct contracts; mixed books |
| Dependency topology | Chains, fan-out, shared children and unaffected branches |
| Scenario breadth | Single-factor up/down, multiple factors and parallel runs |
| Shape | Single period, multiple periods and distinct hourly profiles |
| Cache state | Cold plan/data; within-run reuse; any separately validated warm cache |
| Operations | Calculation, aggregation, persisted receipt and authorized result retrieval |

Record environment, versions, latency distribution, memory, throughput, database
reads and actual curve/pricer execution counts. Agree measurable budgets after
collecting the first representative results. Measure core CPU work separately
from database, serialization and end-to-end latency.

Acceptance:

- Results match the independent full-repricing reference for the supported domain.
- More copies of an eligible unit contract do not cause more unit-pricer calls;
  trade quantity scaling/aggregation costs remain visible.
- A single-factor shock executes affected dependencies rather than every curve.
- Resource bounds hold under concurrent requests and cancellation.
- Required tests, independent reviews and public-content checks pass.
- A fresh checkout builds and runs with public dependencies and synthetic data.
- Operator instructions cover initialization, upgrade, backup/restore, credential
  lifecycle and recovery for the actual implemented feature set.

## Later scope

Extend only after the baseline contracts are demonstrated:

- Broader calendars, delivery shapes and schedule authoring.
- Options and exercise rules, volatility inputs and nonlinear model diagnostics.
- Option-side sibling dependencies, shared exercise groups and an explicit
  contribution/allocation convention that does not double-count the underlying.
- Physical/financial composite sides with typed gas/power delivery metadata and
  explicit activity-to-payment quantity links.
- Storage/dispatch optimization and capacity-dependent quantity behavior.
- Advanced fees, quantity tiers and any justified scripting sandbox.
- Multi-factor P&L explain, cross sensitivities and residual attribution.
- Calibration, simulation, validated analytic/automatic differentiation paths.
- Safe cross-run result reuse and incremental market updates.
- Live feed adapters and their entitlement/provenance controls.
- Settlement workflows, richer administration and deployment hardening.

An item in later scope must not be advertised as implemented because an interface
could accommodate it.

## Working and publication discipline

Work in small coherent commits with targeted tests and updated documentation.
The repository already has its own initial commit and a license change; do not
rewrite those commits or import private history to create a cosmetic single
commit. Implementation commits describe only this public project.

Before publishing, inspect all staged files and generated artifacts, dependency
sources/attribution, operational references, credentials, company/customer and
competing product names, commit metadata and the actual diff against the public
branch. Required technical identifiers and license notices stay accurate.

Keep draft changes local until the user explicitly approves publication. Writing
or reviewing an architecture proposal does not itself authorize pushing it.

Tests use newly allocated disposable resources and neutral synthetic fixtures.
No existing application, shared test workspace or production system is a test
target. Document what was run and what remains unverified.

Architecture notes are living contracts. Update them when implementation evidence
changes a proposal, and retain the compatibility consequences of the decision.

The bounded [exchange futures example](exchange-futures.md) extends executable
booking, amendment, execution P&L and open shocks to explicitly resolved physical,
variable-lot and quoted-basis contracts. Nineteen sourced product families and one
synthetic calendar-day control cover 113 dated definitions. Full settlement,
variation margin, physical delivery and production data activation remain exit
criteria before a broader futures lifecycle claim.

## Private hosted pilot extension

The private pilot uses invitation-only local credentials and a fresh stock database.
Provider settings, source selections and real observations are private installation
data. A bounded managed PJM/ERCOT delivery-price collector and daily UTC timer
retain job outcomes, exact units, full market-day coverage and certified correction
history. Schedules can remain paused independently of an explicit operator run.
This does not activate futures catalogs, implement fixing-based valuation or certify
production readiness. The automated-release extension adds exact-source CI and
attestation admission, bounded data-only packaging, isolated candidate checks and
interruption/retention controls. Automatic database upgrades and rollback of
financial state remain outside that contract. See [pilot operations](hosted-pilot.md) for release gates and limits.
