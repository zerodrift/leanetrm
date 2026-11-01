# Delivery plan and acceptance criteria

Status: design baseline approved for implementation on 2026-10-16. Milestone 0 is
complete. The broad product milestones below remain incomplete; the bounded
capabilities described here have their own implementation and verification records.

Gas may use an explicitly configured independent market/reference selection;
this keeps physical location setup separate from the primary power and financial
inputs without rebasing their source identities or feeds.

D269 adds the local [physical gas booking slice](physical-gas-side.md): fixed
contractual delivery, independent consideration and optional usage fee, shared
retained executable schedules, and an authorized preview/book/reopen ticket.
Shared curve groups, point price adjustments, dated direct-meter schedule attribution
and configured per-unit tariffs are wired into that bounded profile. The
Gas delivery setup editor saves immutable drafts, previews resolution/coverage and
publishes versioned economic settings; saved trades retain their setup pins. Usage fees
have independent pay/receive directions. The fixed-quantity composition ticket now adds independently directed physical
and cash/fee sides, exact fuel links and retained per-member quantity ratios. The [daily quantity workflow](physical-gas-daily-quantities.md) adds trade-owned daily revisions, retained-grid reconciliation and explicit quantity-input selection. The [performance checkpoint](gas-quantity-performance.md) adds bounded batch selection and demand-loaded history. The common Blotter now includes gas positions, exact retained terms and contractual PV alongside futures, swaps and PPAs. New trade uses one product panel and each product’s existing authorized booking operations. Daily gas quantities open from the position and remain separate operational revisions. Mixed risk reports list exact gas exclusions while valuing supported positions; gas scenario risk, physical completion, operational PV and settlement remain work.
The first gas composition gate now supports fixed contractual memberships and
quantity links. Transport eligibility, allocation splits, operational inputs and
ordered paths require their own behavior;
see the [field contract](physical-gas-field-contract.md#reference-identifiers-valuation-and-composition).
Park-and-loan and swing rights are required gas profiles beyond this fixed-volume
slice. Their [acceptance gates](physical-commodity-test-plan.md#park-and-loan-and-swing-rights)
cover linked advance/return obligations, trade-owned outstanding balances and
fees, quantity elections and remaining-rights valuation. Neither capability is
enabled by the current ticket or supplied by a pipeline reference-data flag.

Deferred TODO: delivery-relative monthly price-contract offsets on the explicit
price-reference binding. January delivery with offset +1 selects the February
pricing contract while preserving January delivery geometry and quantities.
Booking must retain the exact selected contract and qualified fixing treatment;
missing or ambiguous matches refuse. No executable offset field is admitted yet.
Valuation-relative rolling selection remains a separate convention. Operational
quantity valuation takes priority over this deferred feature.

D253 unifies dated and interval payments on whole-term FixedSide/FloatingSide,
with parameter-only schedules and one structural/evaluated row contract. PPA
references compile to bounded monthly graph columns for affected-only scenario
reuse. [Side schedules](side-schedules.md) records the exact oracle, bounds and
rebooking boundary. D258 retains every logical pricing row, edge, typed binding and named root in
PostgreSQL; loading and atomic staging/publication are under local qualification.
[Evaluation](side-plans.md) defines the accepted trial contract. Migrations
0123–0127 require a schema release; a private
from-zero pilot seed and PPA entry in New trade remain separate work.

An archive-and-rebook cutover retains a certified trading-history boundary and
the display-reference high-water marks. Earlier trading contexts refuse
explicitly; market and reference history are unaffected. Qualification includes
exact boundary retries, current-authority checks and historical incomplete-prefix
refusals before a release can use this boundary.

The D252 report amendment lists missing or unsupported retained PPA schedules as
**Needs rebooking**, retains healthy position values and leaves affected complete
totals unavailable. Admission removes the former per-trade full-preparation retry;
direct valuation/risk, corruption and current-authority failures remain strict.

The compact D258 trial retains native calculations as typed vector operations
over exact grids in stock PostgreSQL, rather than expanded cell-operation rows.
The [standalone experiment](side-block-experiment.md) qualifies bounded arithmetic
and storage workloads. Application parity, complete HTTP workload measurements
and the development cutover remain in progress; no columnar extension is used.

The 2026-10-29 D258 amendment requires booking to retain the complete executable
schedule for all Side families. The next common execution path loads it directly,
resolves dynamic inputs concurrently under one committed context and immutable
scenario, and exposes context-based row states. No booked read regenerates a
schedule or compiles a replacement execution graph. This remains implementation
work, delivered in small reviewable commits; the existing qualification records
do not establish completion of this amendment. See [the execution contract and
checks](side-plans.md).

The [executable storage checkpoint](booked-side-programs.md) connects booking to
an immutable program artifact and adds an authorized direct loader. Migration
0129 requires explicit rebooking. Production input adapters and switching booked
pricing to that loader remain separate integration steps; this checkpoint alone
does not remove the existing read-time valuation preparation.
The next managed source checkpoint binds stored columns to the existing dated,
native quantity/price and discount resolvers under an exact graph context. Its
API metadata decoder resolves qualified fixing publication times without Side
terms. Native contribution coverage and managed instrument-owned quantity connections
now have focused execution checks. Booking-time cell mappings and their authorized
direct loader are implemented under migration 0147, with explicit rebooking
required. PPA booking verifies sealed identities against persisted members before
commit. PPA and swap execution metadata load directly from typed headers,
including exact factor/native-unit conversion and period-allocation checks.
Migration 0148 applies booking-time publication identity checks to swaps.
In-memory source receipts and bounded dated custom-output scenarios have managed
checks. Durable receipts, production input concurrency, broader custom-output
integration and the booked portfolio/worker cutover remain work; these checks do
not establish readiness for that deployment.

D261 adds a bounded daily-observation consumer of exact dated futures contracts
to scheduled swaps. Booking materializes selection results; later sampling retains
the selected contract and observation instant. The
[scenario matrix](observation-price-scenarios.md#bounded-source-consumer) records
the explicit policies, backend-connected ticket authoring, retained interactive
and queued risk, and financial cash settlement. Automated-close source evidence and
broader source combinations remain work. This consumer does not complete the
general booked-program cutover.

D229 integrates PPA bindings into the common blotter and interactive/queued
portfolio risk, with an explicit pending-meter policy and distinct value measure.
Heat-rate examples use the existing typed factor contract and independent
floating-side schedules. See [demo instruments](demo-instruments.md) for the
implemented contracts, synthetic preview and bounded verification.

D224 adds typed asset and rate curve evaluators and an explicit saved lookup
operator in the existing custom-curve graph. Exact prices, interpolation and
scenario dependencies share the connected evaluator; payment discounting uses
`RatesCurve`. See [curve evaluation](curve-evaluation.md) for policies, consumers
and bounded scope.

D218 implements the common managed Trade/instrument-binding model and typed
instrument PV entry point. Existing futures, swap and PPA portfolio adapters use
the shared projection. The bounded managed forward contract reuses existing sides;
new persisted forward booking and futures settlement PV remain separate work.
See [unit instruments and trade bindings](instrument-contracts.md).

The interval-data foundation (D212) shares exact UTC grids and masks between TOUs,
schedules and compact value columns. Shaping and PPA quantity preparation retain
exact arithmetic, revision lineage and explicit missing-data behavior. Hourly
pricing is a Curves & prices view; the reusable interval-data widget separates
presentation and optional refresh from financial-context selection. See
[interval data](interval-data.md) for implemented consumers and measured limits.

The local custom-curve workspace implements typed visual graphs, formula
roundtrips, exact multi-date sampling, explicit date/schedule references and
separate save/publish operations (D161). Prompt, inclusive daily-forward BALMO
and per-tenor historical-average templates have executable synthetic consumers.
The D186 cutover uses one connected evaluator for ordinary curves, basis and
custom recipes. `weighted_contracts` connects multiple named source ports with
exact persisted member weights and explicit `signed_sum_v1` or
`normalized_weighted_mean_v1` aggregation. The visual/formula workbench edits this
contract; formulas compile only in the backend. SQL panel execution is removed.
[Custom curves](custom-curves.md) records the supported policies and limits.
Graph cards distinguish backend output types from financial curve kinds, with
separate dated-contract icons on exact member rows (D259).
Published recipes supply explicit monthly indexed swap projections through
booking, portfolio and retained-risk operations. The editor offers compatible
published outputs from the authorized backend catalog and preserves the selected
exact version/output through amendments. Daily indexed projections can map each
unfixed reset to one computed output with the same exact delivery interval.
D262 adds raw component history for relative selections and exact weighted
contract mappings, with explicit sampling windows and retained roll jumps.
Cross-revision rolling histories, arbitrary output replay, adjusted roll series
and historical VaR/covariance remain work.

Published custom curves also act as reusable sources in the same visual/formula
workbench (D187). Explicit version/output FKs import the complete parent panel
under the same resolved context, keeping graph edges and transitive native sample
pins through booked PV and retained risk. Shared ancestors prepare once per
operation; cycles, more than 32 published-source edges per path or more than 256
exact versions reject. D193 adds signed/normalized weighted combinations of these panels under explicit
exact-delivery alignment. A guided composer builds the same graph as the advanced
visual/formula editor. Agent catalog/preparation tools use shared backend reads and
hand an unpriced proposal to the ordinary editor only on explicit Review.
D260 adds one agent write, a custom-curve draft saved through the ordinary Save
under the person's authoring permission, within a new curve or the assistant's own
draft in the same conversation; migration 0128 is its database groundwork, and the
tool ships in runtime set `turns_v9` (after `turns_v8`, reserved for P42), recording
its successful step in the draft's own transaction.
Historical resampling, interpolation and conversion remain work.

The managed [connected calculation engine](connected-calculation.md) (D182–183, D186)
adds compact dependency plans, nested sparse scenarios, bitemporal logical-to-exact
bindings, rational means/baskets, rolling selections and resolved schedule
connections. Parallel, explicit point, whole-delivery bucket and dated-contract
targets distinguish primitive quote shocks from computed-output offsets. A
synthetic consumer prices through the common instrument PV contract. The
[kernel benchmark](connected-graph-benchmark.md) exercises up to five million
connections; rich financial compilation, database context loading and production
throughput are not qualified by that integer workload. The saved managed recipe
adapter now retains its exact graph, calendars/schedules and multi-date source
pins through monthly swap PV, shared portfolio risk and the retained worker.
Historical samples are frozen; current inputs retain explicit live quote exposure.
The HTTP/UI risk catalog offers primitive quote and computed-output coordinates,
contract/point/parallel/whole-delivery-bucket targets and nested stresses. Portfolio
risk uses one HTTP operation family; legacy futures-risk routes are removed.
Migration 0080 admits only `custom_curve_recipe_v2` and refuses existing saved
recipes pending explicit reset/rebooking. Migration 0081 retains graph manifests
and typed scenario axes, and refuses any retained old jobs, including completed
ones, pending an explicit operator archive/reset before installation. These are
schema cutovers, not silent conversions of retained history.
Interactive preparation reads visible source-revision history and compiles source
topology without evaluation. `SourceCompilations` and `DeclaredSourceNodes` report
that work; the futures materialization build is still separately counted.
Historical query pushdown, broader
live-feed selection policies, financial workload qualification, volatility and correlation
scenarios remain work. Graph propagation within a pinned scenario is implemented. Explicit
[live corrections](live-calculation.md) advance successive certified header
contexts through committed notifications, with existing backend screen reads and
bounded numerical memoization. An existing scenario/job remains pinned; live
clock advancement and observation/contract rolling policies remain work. See the
[cutover prerequisites](connected-calculation.md#adopting-the-schema-cutover).
All calculation behavior stays in the backend.

Persisted custom-curve activation (D189) selects a complete effective timeline at
the certified knowledge cutoff, then an exact published output for the effective
date. Authoring requires current publishing authority and expected-revision
concurrency; gaps are inactive. The workbench supports timeline replacement and
effective preview through the existing evaluator. Exact booked/job references
remain independent of activation. Native-curve activation, resampling and
interpolation of published panels, and histories crossing market revisions remain
separate work.

The header's As of calendar qualifies available exact snapshots through the
managed backend (D162), including all inputs of a custom graph's history window.
It disables unavailable dates, preserves the user's display zone and requires an
explicit choice among multiple closes or saved versions. Changing only the
snapshot preserves the active custom graph and clears its old calculations.
Built-in availability reads all offered snapshots' exact pins without loading
unrelated observation history. Custom graphs retain the broader historical
read needed by their selected history and correction policies.

The [verification workflow](verification.md) now defines a single local/CI command
covering the existing managed, frontend, database and browser suites. The complete
command passed for the D186 cutover on the Ubuntu 24.04 hosted runner on
2026-10-22; verification records the exact source and runs. Production deployment
remains unqualified.

The public landing source and hosted gateway route contract are available
separately from the authenticated workspace (D163). An owned nginx check exercises
static routes, native application routing and a synthetic sign-in upstream.
This is gateway routing qualification, not verification of a real installation's
identity service or authorization. Landing/gateway activation remains an explicit
operator step outside automatic application-image updates.

The implemented trading workspace now has a shared Blotter with individual trade
rows, managed pivots, account-saved layouts, exact-data exports and shared charts.
Eight synthetic weekday futures bookings cover the four standard market frames.
Immutable futures contracts are shared directly by curve consumers and trades across books.
Typed execution P&L and deterministic base/up/down open sensitivity are implemented;
daily/final settlement, variation-margin transitions and generic futures PV remain
unimplemented. Quantities, quote scales, numerical policy and exact inputs stay in
the managed backend.

Futures execution admits explicitly qualified candidate terms under D158. Actual
first-trading timestamps can remain unknown when only a source date exists; an
exact-version policy supplies a separate supported boundary and selected price
source/role. Missing/conflicting policies, wrong sources/roles and inconsistent
or ambiguous clock boundaries reject. This does not enable official settlement,
variation margin or delivery. Public migrations 0038/0039 remain immutable
history; migration 0040 converts uniquely qualified retained admissions to generic
policies without changing native source facts. Their original source-prefix/hash
restrictions apply only to that legacy conversion. Existing installations require
a separately qualified schema release.

The shared managed valuation path is implemented (D145–148): risk creates
immutable scenarios and compares returned book/holding values. Instruments own
unit pricing and report factors through `MarketFactors(context)`. Books apply
external quantities and execution bases; summary reports compile exact net
quantities/costs once per book/unit. No optional quantity-scaling flag remains.
Market factors are exact dated references returned by instruments, checked against
complete pinned declarations and deduplicated by books. Generated-close automatic
bumps follow the selected holdings, including hedged upstream dependencies. The
interactive catalog also exposes eligible unheld live quotes in each prepared
market, compiled as lazy graph leaves; these yield explicit zero exposure for
unaffected holdings. All-in marks never acquire inferred upstream dependencies.

Nested immutable quote/output stresses and retained interactive portfolio jobs
are implemented locally (D178–183, D186). The same resolved header context supplies user and
worker calculations; child scenarios preserve parent values. Independent workers
share a fenced PostgreSQL queue, and an optional Swarm artifact describes their
placement. Single-run sharding, distributed reduction and real-cluster failure
qualification remain work. See [risk engine](risk-engine.md).

The 50,000-position Release [benchmark](risk-benchmark.md) covers repeated and
5,000-distinct unit values, actual futures execution-P&L, and actual swap PV through
four gas basis edges and two power basis edges with 216 independently shocked
monthly factors. Independent tests cover exact offsets, nonlinear zero-delta
values, quantity/price conversion invariance, dependency access fences,
conflicting bindings and cancellation. Streaming book summaries retain one factor
pair at a time and expose unit calls, reuse and quantity applications.

This qualifies the managed calculation path, not production throughput. The
interactive portfolio API and generated pilot-close worker share the retained
futures-and-swaps adapter (D169, D181), with separate futures execution-P&L and
swap-PV measures. The interactive operation covers the current mixed books and
their explicitly pinned supplemental market revisions. Its exact rational holding
and book outputs preserve nonterminating amounts without rounding and keep rate
inputs fixed under commodity price scenarios. Typed source arithmetic retains its
explicit 18-decimal-place representability checks; connected rational graph/PV
arithmetic does not remove those source contracts. Working-decimal output still
rejects unrepresentable results. Booking database bounds, large historical reads,
serialization/concurrent service load, broader instrument implementations,
rate/volatility shocks and complete futures settlement PV need separate
implementation/qualification. The earlier benchmark does not measure the new
worker, publication or report-read path.


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

New swap entry no longer accepts a manual first-fixing boundary (D165). An exact
sealed index schedule supplies dated publication instants and actual-series/source
bindings. The valuation path combines final, exactly pinned actual fixings with
remaining projections under unchanged observation weights. Due missing fixings
are structured errors. Actuals retain frozen lineage and cannot become live quote
risk. Legacy projection-only definitions and idempotent retries remain retained;
new raw/manual boundary writes are closed. Every bundle, including the separate
daily swap bundle, prices its snapshot's exact pins; a corrected actual needs a
new snapshot or a certified cutoff (D172). Public fixtures are synthetic. Real
publisher schedules, peak-product observations, licensed actual data ingestion and
fixing/payment lifecycle operations require separate qualification.

Managed multi-year monthly pricing uses whole-term fixed/floating sides with
explicit monthly periods, preserving exact per-payment lookups and DFs. Per-month quantity, an
equal-month allocation of total quantity and a calendar-day-weighted allocation
are explicit versioned choices; rational allocation precedes external trade
scaling. Independent 10/30-year price, discount and shared-trade shock checks
qualify this managed consumer (D170). Retained recurring booking has its own
quantity-schedule and storage bounds.
Interactive and generated-close operations retain mixed-product reports through
the common portfolio path (D169, D181). See [pricing lookups](pricing-lookups.md).

Financial gas and power swaps now have local booking/amendment operations, a
shared trade blotter and an editor with independently valued side tables. The
booking model supports one whole delivery month, a single monthly index or the
five existing daily observation/weighting modes, independently generated payment dates and explicit currency-default
or no discounting. Daily resets aggregate into one monthly floating payment;
retained exact lookups, cash allocations, DFs and PVs are inspectable. Four explicit
weekday dates have synthetic monthly/daily gas and power examples.
Each side now selects either a period-end day lag or a day of the following
month, with explicit day basis, short-month behavior, date adjustment and exact
calendar where consumed. Generated date-only rows retain the rule evidence.
New entry has commodity-filtered indices/units and separate reset-frequency and
averaging-method fields. Earlier manual-date revisions remain inspectable without
inferred replacement conventions; amendments must select the new rules explicitly.
If a selected exact quantity reference becomes unavailable, entry clears the
numeric quantity before initializing another working unit. It never reinterprets
the retained number in that new unit.
The swap editor can resolve published schedule symbols against its exact retained
context, review the source version/occurrence and apply a literal delivery date.
Suggestions obey that same cutoff. Payment-policy choices come from the backend's
typed supported conventions; reference choices remain database-backed. The standard
local example uses USD from the currency reference table with synthetic prices.
Shared tables now guard row activation during selection/scrolling and use a single
scroll container for schedule inspection.
Calendar-based rules retain an exact observation calendar and any required
outside-month guards. Quantity weighting retains a positive typed quantity for
each daily observation. Shared compatibility and schedule generation serve
authoring and submitted terms; original target applications are the rows valued
and inspected. Migration 0102 supplies normalized immutable authoring and shared
atomic booking. D253 replaces that earlier one-payment side shape with
whole-term parameters, including the single-period case.
Independent pricing windows, explicit zero weights, delivery blocks/events and
contract-relative or paired observations require separately qualified versions.
Fixing lifecycle events remain work. D214 adds explicitly versioned recurring
monthly financial-swap payments and quantity schedules; see
[quantity contracts](financial-swap-quantities.md) for qualified bounds.
Bounded physical and virtual PPA capabilities are documented below.
Draft swap quote risk supports unresolved
observations; final index fixings remain frozen inputs.
FixedSide and FloatingSide now independently generate and value their own
schedules through Side.PV. Each side owns all its periods directly;
payment/delivery dates and DFs can differ by period. Side results retain normalized unit
quantities, lookup terms/weights, prices/lineage, exact cash/PV and discount
construction evidence.
Instrument.PV only sums membership-signed side PVs under one full context; trade
quantity scales afterward. D214 retains explicit recurring periods through a new
scheduled contract. Fixing/payment lifecycle events remain work; the browser inspector now exposes the actual valued payment and reset rows. Options retain explicit sibling references
and distinct base/final valuation modes; the exercise-control accounting choice
in O13 is still open and no option model is enabled.
The D210 entry refinement places visual instrument cards between trade details
and side forms. A selected versioned template populates empty side terms;
commodity and market selections remain explicit. Each floating side owns its
commodity filter, and New Trade offers the existing PPA workflow. Cross-commodity
swap valuation remains outside this refinement.
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
The bounded collected-interval HTTP consumer now connects explicit curve/source
associations, one operating day and a certified price cutoff to the same typed
settlement arithmetic (D171). It supports interval MW with explicit shape and
already-shaped interval MWh, preserving source batches and UTC/DST identity.
Published-final fixing admission, booked cash obligations and payment remain work;
see [the consumer contract](power-preview.md#collected-interval-delivery-value) and
[the broader settlement design](settlement-plan.md). The browser's original flat
synthetic example retains its meaning.
The proposed [PPA design](ppa-design.md) (P40) orders the next power work as
audited interval series, interval curves with explicit block shaping, PPA booking
and valuation from reusable physical and financial sides, then PPA settlement on
the settlement design's lifecycle. The first two are implemented: audited interval
series (D173: store, authorized HTTP routes, screen and 20-year benchmark in
[the implementation record](implementation.md#audited-interval-series)) and interval
curves (D174: marked and block-shaped interval prices, actual/projected resolution and
explicit aggregation, with reads and a screen, in
[the implementation record](implementation.md#interval-prices-curves-and-block-shaping)).
Neither has a historical session mode. The locally implemented phase C slice 1 (D194)
adds exact rational quantity-times-price, shared prepared interval-price resolution,
explicit live balance blocks, opt-in frozen anchors, shaped collected actuals and the
qualified period-fixing consumer. Slice 2 (D195) adds sealed asset/source/location
definitions, exact forecast and meter quantities, explicit meter deadlines,
multi-asset normalization, daily accreditation, contract profiles and anniversary
compounding, with authorized quantity preview routes. D196 subsequently settles
R1/R2/C7 for the managed slice 3 consumer. D197 accepts R3’s exact-pin and
whole-operation incompatibility policy; slice 4 implements durable bookings and
the bounded base-valuation API.
Checkpoint evidence is separate from product scope:
D196 accepts native delivery coverage, whole-month capacity pricing and explicit
outstanding-value measures for slice 3. Managed side schedules, composition,
valuation and coverage-aware risk reuse passed Checkpoint B: independent exact
oracle comparison, measured full-term/segmented reuse and adversarial review.
Slice 4 adds exact typed booking storage, atomic lifecycle operations, R3 complete
scope admission and HTTP base valuation. Request-owned immutable validated series
indexes remain the only performance optimization; secondary optimizations remain
proposals. Slice 5 (D199) adds guided PPA booking, amendment/termination, a blotter
and bounded read-only inspection through the capability catalog and authorized API.
Physical and virtual booking, retained payments/holdings, overdue measures, repeated
DST hours, revision refusals, duplicate recovery, mobile layout and enlarged-fixture
navigation passed browser checks and independent review. Slice 6 (D200) adds a
read-only existing-close book-risk operation, typed persisted receipts and worker
operation gates. The full synthetic gate, independent adversarial review and all
15 managed benchmark samples passed. D201 subsequently qualified renewable
completion and recovery for an actual ten-year hourly risk workload beyond 120
seconds, preserving admission pins and the overall deadline. Other large books
have not been qualified end to end; legacy generated-close leases are unchanged.
Optional generated-close cutover and settlement remain later work. Qualification and measured HTTP limits are recorded in implementation.md;
service availability is not production or concurrent-load qualification.
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

Release migration order retains reference-intake storage as 0034, followed by
durable jobs (0035) and Swarm infrastructure (0036). Pinned migration identities
and independent executor authority are verified together (D141). The trusted
private managed reference capture/import commands replay retained source bytes
into the existing inactive reference storage. External source documents and
credentials remain outside the repository. Import does not activate trading,
certify historical publication or replace exact execution-policy admission.

The [bounded automation slice](automation.md) now implements durable UTC once/fixed-
interval jobs, a separate .NET Worker Service, forced RLS/current identity checks,
a shared Jobs & Infrastructure screen and a typed Swarm controller protocol.
Owned PostgreSQL, synthetic Docker HTTP and browser checks establish the local
contracts. Real Swarm failure qualification, full agent/provider/tool execution,
child budgets, financial jobs, host reboot/Engine restart and release migration
remain outstanding; the full automation milestone is not complete.

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
in the preview; qualified futures-series generation and durable native-curve
activation remain work. D189 supplies separate custom effective-version timelines.
Do not build a broad date scripting language or duplicate holiday/expiry catalogs.
The separate daily/BALMO month-end selection remains open.

Monthly anchors, explicit calendar/adjustment choices, indexed date expressions
and the shared versioned contract-date library now run in the browser preview. See
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



Blotter delivery includes numeric values with separate unit labels, exact
comparison filters, backend price statistics, configurable row/column pivots
and linked bar, line and heatmap plots (D206). Subtotals, cross-axis grand totals
and cell drill-through remain later work. Swap entry now covers fixed/floating
and floating/floating financial composition with explicit spreads and reviewed
par-rate entry (D207). Qualification includes selected actual fixings, published
custom projections, booking replay and generated/existing-close report compatibility.

The next observation-price integration starts with the
[commodity and forward/settlement acceptance matrix](observation-price-scenarios.md).
Qualify explicit source combinations before adding booking/pricing behavior;
existing component tests do not establish end-to-end support.

## Milestone 0 — Record and review the design

Deliver:

- Domain vocabulary, side ownership and definition/version boundaries.
- Shared factor scenarios, black-box pricers and mandatory unit-level valuation.
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
- Unit instruments, external trade quantities and structured errors for models
  that cannot satisfy this contract.
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
8. Demonstrate shared unit calculations are reused across external quantities, while
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
17. Recompute remaining schedules and unit-valuation context after partial
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
- Shared authorized operations and job views for recurring schedules and durable
  temporary jobs, with explicit timing, expiry, missed-run, overlap and retry
  policies. Retain definition/occurrence/attempt identities and execution context;
  job management is separate from permission to perform the scheduled work.
- A separately deployable .NET Worker Service for scheduling in this repository,
  sharing managed timing/admission/authorization code. Use stock PostgreSQL durable
  state and a restricted service identity, explicit polling/lease/recovery settings
  and fenced claims. Support separate Swarm node placement and API restarts without
  introducing a scheduler database extension or Docker access in the scheduler.
- Docker Swarm deployment and a trusted execution controller for bounded,
  digest-pinned container jobs under the [execution contract](architecture.md#112-docker-swarm-and-image-based-execution).
  Keep business scheduling separate from Swarm placement and task state; retain
  owned resource bindings, restart/recovery policy and explicit cancellation.
- A shared Jobs & Infrastructure view in ordinary navigation for all authenticated
  members, with approved shared status and own-job controls. Administrators with
  installation-wide job-management authority can control all jobs; protected
  inputs, logs, results and business data retain their own access checks.
  Per-resource permitted actions drive UI controls and backend operations enforce
  current ownership/authority. Shared authorized node/service
  operations under the [maintenance contract](architecture.md#113-infrastructure-administration-and-node-maintenance):
  inspect, pause/drain/activate, govern placement and restart owned services.
  Host reboot/Engine restart require a configured typed host adapter; retain
  durable maintenance progress, current authority, quorum/capacity/stateful gates
  and an independent operator recovery path.

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
16. Create, pause, amend and cancel recurring and temporary jobs through identical
    UI/API operations. Restart before dispatch and verify retained due work follows
    the selected missed-run/expiry policy; duplicate delivery admits one logical
    run. Expiry retains audit evidence and prevents new work without claiming that
    an already running worker has stopped.
17. Reject unauthorized job creation, cross-scope discovery and execution-identity
    substitution. Revoke the creator's automation delegation or execution grant
    after scheduling and deny subsequent admission/publication under that grant.
18. Verify each occurrence resolves its declared input-selection policy once,
    retains exact business/effective and knowledge coordinates, and retries the
    pinned calculation rather than silently selecting fresh inputs.
19. In an isolated disposable Swarm, launch a synthetic finite container job from
    an approved profile. Reject missing/revoked images, changed contracts, raw
    Docker specs and disallowed mounts/networks/secrets before Docker submission.
    Verify task credentials cannot reach Docker management or the database.
20. Lose a create response, restart the controller and interrupt a worker/node.
    Reconcile owned resources without duplicate logical effects. Include an old
    task still running after replacement: reject its tools/results by fencing,
    account for overlap and preserve pinned inputs/images and aggregate limits.
    Cover automatic restart within one logical attempt and forged bootstrap task
    IDs; reserve uncertain tasks' budgets before admitting any replacement.
    Race concurrent controllers through lease transfer and a delayed create that
    completes after cancellation; deny late task credentials and reconcile only
    obsolete owned resources without reverting the new owner's desired state.
21. Cancel while the manager is unreachable; retain cancellation-requested and
    usage accounting until all owned tasks terminate, while atomically denying
    new tool/effect/result admissions. Preserve effects committed before accepted
    cancellation. Cleanup cannot remove
    another run's resources. Qualify the selected Engine/API, finite-job restart
    behavior, storage bounds and result receipts; exit zero alone is not success.
    Verify explicit replica/concurrency bounds, reject global-job mode for finite
    runs and account for physical automatic restarts before credential admission;
    require controller-only replacement when overlap bounds cannot be enforced.
22. Rehearse Swarm deployment/recovery with isolated synthetic data, explicit
    database volume ownership/backups, secret/ingress boundaries and release
    admission. Verify migration from any standalone deployment before promotion;
    container rescheduling alone cannot pass database recovery acceptance.
23. Run the scheduler on a separate eligible Swarm node with a restricted database
    role. Restart the API while due work continues through shared managed operations.
    Overlap scheduler replicas during upgrade, lose a claim/renewal or database
    connection, and recover missed wake-ups: admit each occurrence once, reject
    stale-owner dispatch and preserve declared missed-run policy/current authority.
    Skew worker clocks and delay old renewals; shared database time controls due
    admission/expiry and stale renewal cannot restore ownership. When separate
    placement is selected, loss of eligible nodes cannot fall back to the API node.
    Verify scheduler credentials cannot perform Docker management operations.
24. Exercise node inspection, pause/drain/activate and owned service restart in a
    disposable Swarm through UI/API operations. Every authenticated member can
    open the shared view, inspect approved status and control their own jobs.
    Deny cross-owner actions and forged owner IDs; allow an administrator's
    explicit manage-all-jobs capability to control any job without granting its
    private content or extra business authority. Test owner/delegation changes,
    revoked authority, direct API calls and aggregate/detail/log access for leaks.
    Owners cannot read content after its read grant is revoked or restart shared
    services used by other jobs through an own-job action.
    Own-job retry/restart still applies approvals, lineage, expiry and budgets.
    Deny workspace/agent grants that
    lack infrastructure authority, cross-cluster targets, protected-label changes
    and shared-node disruption without the required cluster mandate. Restart
    preserves the approved image, business authority, retained job state and limits.
25. Use a synthetic host adapter to test reboot/Engine restart identity binding,
    authorization/approval changes, delayed/duplicated/lost responses and controller
    lease loss. Reconcile accepted commands before retry or restoration; report
    unsupported adapters and uncertain outcomes honestly. No arbitrary endpoint,
    shell command or agent-held host credential is admitted.
26. Race maintenance requests against the same cluster quorum/capacity margin;
    block unsafe steps and stale-owner follow-ups. Reject stale/unknown health,
    insufficient eligible capacity, unhandled standalone processes, node-local
    state and unqualified database/control-plane disruption. Confirm drain and
    replacement/stop state before the host action. Test return-to-service policy
    and independent recovery when the application/control path is unavailable.
    Race ordinary scheduler dispatch, controller launch/replacement/reconciliation
    and placement edits against maintenance: honor its shared durable admission
    gates, preserve reserved capacity and do not recreate intentionally stopped
    workloads before authorized resumption.

Independent review covers asynchronous behavior, result publication, current
authorization and retained-version boundaries.

## Milestone 5 — AI-operated application

The bounded model-administration extension D213 adds explicit fixed/alias policy
on immutable capability versions. Alias support keeps exact served-identifier
checks and existing pricing, authorization and credential gates. It requires
migration 0105 and matching application/UI assets; local synthetic checks do not
qualify a hosted release or real-account availability.

D211 adds the other four [provider adapters](provider-adapters.md), retaining
per-vendor usage mappings, explicit continuation handling and the existing
administration/worker gates. Real-provider task outcomes and hosted rollout
remain unqualified; the broader acceptance criteria below remain open.


Design the operation schemas from milestone 3 for agent use; this milestone adds
the model runtime and client adapters rather than a separate business-rule path.

The page assistant brings a bounded slice of this milestone forward in three
phases (D149): a [local-preview core](ai-assistant.md), agent notes and a hosted
baseline, which runs the core under `--hosted-pilot` (D192). The slice does not meet
the acceptance criteria below. Day-over-day and
period P&L comparison is the next slice and monthly spending caps a later one
(D153); until caps exist, the slice bounds cost per turn instead of the resource
budgets below.

Deliver:

- A discoverable versioned operation catalog with typed inputs/outputs, required
  authority, bounds, temporal context and idempotency behavior.
- API and MCP adapters invoking the same operations as the UI.
- A provider-neutral adapter and capability contract, with at least two adapters
  or an explicitly supported external-agent route plus adapter conformance tests.
- Scoped agent identities, delegation, allowed provider/model routing, protected
  credentials, resource budgets and auditable task progress.
- Exact-payload proposals/approvals and structured validation failures.
- Scheduler-launched agent tasks and agent-authored recurring schedules and
  temporary jobs under the [shared execution contract](architecture.md#111-schedules-temporary-jobs-and-agent-execution).
  Bounded child-agent launches require explicit redelegation, current authority
  and aggregate limits; the scheduler's service rights cannot flow into agents.
- Agent runs and permitted child workloads launch from approved Swarm execution
  profiles. The managed runtime keeps provider types outside domain contracts;
  image approval and launch do not confer tool or business authority.

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
12. An authorized agent creates a recurring job and a temporary job through the
    same operations as a person; both remain inspectable and controllable through
    the UI. Schedule creation alone cannot authorize any scheduled business effect.
13. A due job launches an agent under its declared principal/delegation, assignment,
    tools, provider policy, context and limits. Retain creator/scheduler/delegator
    and parent/child lineage; reconcile uncertain launch outcomes without duplicate
    logical runs. No launch is admitted when authority or required limits are absent.
14. Deny a child agent or job that expands authority, outlives delegation, escapes
    parent cancellation, or bypasses depth, fan-out, cost, rate or concurrency
    limits. Test revocation during queued/running child work and prevent publication
    with a stale authorization or execution fence.
    Include an agent-created recurring schedule's future occurrences: parent
    cancellation and aggregate limits cannot be bypassed by selecting a new root
    or waiting to enqueue children later. Verify independently surviving automation
    has a separate authorized durable grant and retains its creation lineage.
15. Require normal business approval policy for agent-generated effects. A schedule
    approval or task prompt cannot approve an arbitrary future payload. Verify
    standing automation policy, where supported, has explicit scope and limits.
16. Launch a scheduled agent in Swarm, then have it request an authorized child
    agent/container job and temporary schedule through normal operations. Deny
    unauthorized images, credential substitution and attempts to reach Docker or
    reset lineage/budgets. Test revocation, credential renewal and replacement
    tasks across the full parent/child workflow using synthetic operations.

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

Business-page readiness has a less-than-one-second target (D142), including the
authorized data a person can use. The owned `tools/benchmark_pages.py` workload
measures startup, all navigation views and curve pricing with explicit network
latency, retained observation counts, contracts and booked positions. It can
enforce that budget separately from ordinary correctness checks. Broader
concurrency and larger reference/contract workloads still require qualification.
Startup requests identity metadata without the unused frame projection; the
default full bootstrap remains available. An explicit initial access projection
can supply fresh navigation permissions for the verified account in that same
response. The browser consumes it once after checking the account identity;
later access refreshes and all business operations reauthorize independently.
The initial directory uses a separate typed market-navigation operation rather
than reading the full pricing workspace. It includes explicit curve
classifications, frame summaries and exact revision/context identities. Full
workspace hydration precedes its actual pricing/reference consumers and must
match the unchanged identity, generation and selected view. Qualify first-use
pricing and reference inspection separately so deferred work is not hidden by
the startup measurement. For an explicitly requested Blotter directory, a global
absence of visible futures and swap revisions can avoid unused valuation reads;
selected pins, bundle/reference visibility, view/books and current authority
remain mandatory. Nonempty authorized revision sets use the full existing path.
These narrow contracts defer unused economic validation without treating absent
economic inputs as successful calculations.
First pricing may explicitly request complete catalog metadata from the same
authorized evaluation read, with unchanged default evaluation behavior and
separate selected-source versus catalog-observation projections. Verify one
context bind, exact metadata/source validation and first-use chart readiness.
Priced trading workspace reads explicitly limit milestone metadata to the required
first/last-trading facts and primary observations to the selected snapshot pins.
Checks compare complete retained catalog/report responses, reject missing required
facts and prove unused metadata/corrections are not read. Generic/full metadata
reads retain their existing scope. Supplemental swap markets resolve corrections
only in a certified context's own frames (D172). End-to-end cold-entry
performance requires separate qualification.
Reference reads avoid a duplicate permissive scope check only where the mandatory
restrictive visibility policy already checks the same current authority per row.
Exact policy/helper guards and expiry, scope, historical and policy-composition
checks qualify migration 0041 separately from an application-image update.
After TypeScript checks, the pinned
build-only esbuild dependency bundles the presentation entry to avoid repeated
module discovery requests. Chart and table vendors retain their separate modules
and license notices. Blotter navigation
requests its explicit booking directory; priced futures entry loads only when
selected, together with a refreshed report under the same context (D143).

The five-sample bounded workload with 10,190 reference rows, 12,420 observations,
105 series, 92 contract definitions, 12 curves and 24 trades (16 swaps) passed that
budget for all ten navigation views. It retains 160 unique initial observation
pins; the priced curve evaluates 54 constituents with zero risk scenarios. With
40 ms browser latency, 10 MiB/s download and 5 MiB/s upload, initial readiness was
679 ms, curve pricing p95 was 138 ms and the slowest page p95 was 390 ms. Reference
inspection also verified consecutive 200-row pages and search beyond the first
page in 4,008 locations and 2,007 futures series. This run used stock PostgreSQL
16.14, .NET 10.0.402 and Chrome 155 on macOS 26.6.2 arm64; API resident memory was
513,504 KiB at the reporting instant, not a peak measurement. This bounded catalog
and single client do not establish larger-catalog or concurrency limits. Reports
retain source, migrations and frozen API/web artifact identities outside the
repository.

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

## Fixed-volume physical crude (D271)

Implement and qualify the bounded [physical crude workflow](physical-crude.md):
exact grade and custody-point references, fixed contractual standard volume,
independent physical/consideration/handling contributions, retained booking and
read-only inspection, common positions/blotter and interactive/queued portfolio
risk. Use failing behavior checks first, then isolated managed,
database, API and browser qualification. Existing booked programs must survive
the additive migration, and unsupported portfolio scopes must refuse rather
than drop crude positions. Independent numerical, authorization and temporal
review is required before publication.

The bounded [delivery-to-cash slice](physical-crude-operations.md) now records
certified partial receipts, explicit title transfer, user-selected onward-delivery
or owned-inventory treatment, component invoice allocation and partial payments.
Independent quantity, cash, historical and recovery checks cover both treatments;
neither is a default. Full payment-record reversal and ordered invoice void/reissue
now preserve immutable history and original cash obligations. Actual bank refunds,
dispatch reversals, backdated physical corrections, raw custody measurement,
inventory costing, cargo and broader physical completion remain further contracts.
The qualified local increment allocates owned receipt lots to exact sales, records
separate dispatches and reconciles dispatch-backed sales invoices and collections.
Purchase obligations remain unchanged; no automatic lot selection, costing or
P&L is introduced. Managed, API, native and browser checks demonstrate the complete workflow; a
populated additive-upgrade check preserves prior operational and cash history.
Automated crude closes, amendments and cancellation remain further work.
Production activation remains a separate release gate.

## Later scope

The [financial swap settlement sequence](swap-settlement.md) is now authorized
through S1–S6 (D215), with bounded local implementation and executable gates
recorded there. Futures margining and physical settlement remain later scope.
D216 adds bounded closing-context new-trade/future-amendment reconciliation and
explicit same-trade gross/net allocations. Broader transformations and cross-trade
netting remain unsupported; capability and verification records are in the
settlement document.

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
- Delta ladder extensions (D230): one curve level per run, server totals across
  selected books, and server-stated quantity-equivalent units.
- Calibration, simulation, validated analytic/automatic differentiation paths.
- Safe cross-run result reuse and incremental market updates.
- Markets desk follow-ups ([markets-desk.md](markets-desk.md)):
  - Positions on this curve, which needs a trading read keyed by curve.
  - The contract history chart lost the image export the old history card had.
  - Bollinger bands, which need an explicit irrational-number policy.
  - Per-selection-node resolved contracts in custom-curve previews, so a
    Select contracts card can say which contract "Position N" resolved to at
    the close.
  - Custom-curve preview and save return 400 `request_format` for an empty
    contract id or a null output position instead of a structured refusal.
  - A saved, unedited weighted draft previewed by id and version was reported
    refused as `custom_curve_definition`; unconfirmed and pre-existing, to be
    reproduced before any fix.
  - Rates curves on the market board.
  - A dark color scheme; the charts already follow the tokens.
- Market dashboard follow-ups ([market-dashboard.md](market-dashboard.md)):
  - Phase 2: provider connections, observation and event storage, the provider
    setup screen, synthetic providers and the Weather, Inventory, Outages and
    News widgets, with lateness against each series' declared cadence.
  - Phase 3: intraday prices on Latest levels, from the live-price source shared
    with the Blotter work.
  - Phase 4: agent alerts and pinned chart cards, then watches once scheduled
    agent tasks exist.
  - Lateness of an expected close against its business calendar.
  - Running a saved risk run definition from the Blotter.
  - Dragging widgets on the Board; the page moves and sizes them one step at a
    time.
  - Measuring publication-lock contention from definition runs before a hosted
    release (verification.md).
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
variable-lot and quoted-basis contracts. Nineteen sourced product families cover
72 source-confirmed monthly maturities across twelve families, 61 executable.
Daily definitions remain non-executable until their listings are confirmed. Invented unit controls
stay in numerical tests. Full settlement,
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

The standard execution-policy extension (D158) separates real exchange definitions
from generated price observations and installation seeding. Qualification covers
explicit execution eligibility, date-only source preservation, source/role matching,
current reference authority and historical visibility. Reconcile any privately
installed schema additions with the reviewed migration chain before deploying;
private seeding does not justify a separate application build or changed hashes.
Full futures lifecycle and official market-data activation remain work.

Job entry resolves user-zone wall clocks through the managed resolver (D159); UTC
storage and elapsed-interval recurrence remain explicit. Contractual clocks and
date-only facts are separate from timestamp presentation.

Workspace usability adds editable future job schedules and limits under current
authority, with retained request replay and physical-run cleanup guards. Reference
display authoring (D164) saves immutable versions and separately applies reviewed
labels to current presentation after an economic-identity proof. Financial-term
editing, replacing curve reference pins and transporting prices into new economic
versions remain unimplemented. The reference menu retains only categories with
documented current consumers. Delivery-price navigation consumes authorized source
metadata in commodity/operator/market/location order and keeps inclusive market
delivery dates distinct from user-zone interval timestamps. Synthetic schedule
examples use the existing monthly publication operation; literal daily contract
examples can be explicitly saved and published as shared date-rule definitions;
they do not become published exchange contracts. Exact source versions and a
certified validation context remain pinned, with resolution under a separately
selected context (D167). Contract activation and booked-side integration remain
outside this library.


Existing-close risk now has separately qualified renewable completion (D201):
actual ten-year hourly risk exceeded 120 seconds, with original admission pins and
overall deadline retained through worker death/replacement and lost commit
acknowledgement. Complete report and terminal success publish atomically. This
bounded fix does not qualify every admitted workload, eliminate the preparation
transaction's authorization fence, or change generated-close workers. See the
implementation record for measured limits. Settlement, generated-close cutover
and unrelated performance work remain separately scoped.


The [Phase C qualification record](ppa-phase-c.md) is the current capability and
limit inventory. Its [next settlement task proposal](ppa-settlement-next.md)
identifies the missing shared lifecycle prerequisites; it authorizes no settlement
implementation or generated-close cutover.

Final Phase C qualification is complete locally: the corrected tree passes all 45
stages, including real renewable worker replacement/lost-acknowledgement recovery,
and independent whole-branch review has no open material finding. This closes
Phase C within its recorded limits. The subsequently authorized bounded
[settlement policy/specification task](ppa-settlement-spec.md) is complete for
review, with an independent executable specification. SP1–SP9 were subsequently accepted for explicitly opted-in bounded fixed-strike
contracts (D202). The [shared meter-finality prerequisite](meter-finality.md) is implemented;
cash determination acceptance, obligations, payments, workers and
gas are outside its scope. Shared power/gas phase dependencies remain open.


D203 implements the separately authorized narrower power-only sequence:
[immutable payment continuity, complete occurrence planning and monthly gross
preview](settlement-preview.md). Global S1/S2 are not closed. Approval, immutable
cash acceptance, contribution claims, correction obligations, disjoint valuation
and payment execution still require separately authorized work.


D204 resets the current PPA MVP to settlement calculations, exact draft break-even
pricing and an extension of the existing graphical editor. The
[pricing and graphical workbench record](ppa-break-even.md) records its implemented scope
and independent evidence. Approvals, accepted-cash accounting, payments, reversals
and automated correction adjustments are deferred, not pricing prerequisites.
Trade amendments remain contractual changes; market/meter corrections remain
versioned inputs. Earlier lifecycle specifications are retained for future work;
they do not authorize a separate settlement approval workflow or claim global
settlement-phase completion.


Temporal browsing usability (D217) selects the latest unambiguous offered market
instant initially and resolves effective-date changes to explicit same-day
knowledge through the managed time-zone resolver. Shared floating Context panels
retain native cutoff contracts. Interval data opens the latest trailing day;
delivery prices open the latest available source/day, with explicit empty states.
The Assistant launcher floats at the lower right and its visibility is a browser
profile preference. Historical-session support is unchanged.

D227 adds selectable factor units and a versioned cross-commodity scheduled swap.
The [factor contract record](factor-swaps.md) describes supported monthly quantity
bases, independent price/payment rules, native source risk, gross settlement and
exact reference-version retention. Compatible new units consume published
conversion data; new financial semantics require a versioned implementation.
Physical delivery, cross-currency factors and interval-price × energy payoffs
remain outside this contract.

The Blotter desk's newest close (D219) takes the same latest instant: one
mechanism, the workspace's availability calendar, chooses the close when working
data opens or is returned to and states it with a **Latest close** badge; several
closes at that instant still ask (D162), and a historical view selects none by
itself. The As of control and its badge float with the Context panel, which names
the knowledge in use and holds live corrections.


D228 makes saved-value availability and amendment changes explicit in the swap
tickets. The backend compares the booked revision with the validated proposal;
single-period and scheduled tickets show the required amendment and exact changed
terms. Existing preview cashflows remain the valuation review. Publishing economic
index changes, selecting newer versions for existing contracts, bulk impact
analysis and a separate forecast-configuration migration remain future work.


Side observation entry now connects commodity-scoped index choices and observation
conventions in both directions for single-period and scheduled tickets. Period
qualification shares required source/fixing checks with delivery-date availability;
missing due actuals cannot remain offered as a usable selected plan. Entered
incompatible terms stay visible without an automatic replacement, and required
calendar controls remain available for repair. This is an extension of the D216
compatibility controls, not contract-relative sampling: durable side selectors,
flexible roll rules and observation-date/contract-delivery separation remain work.

Operational limits (P42, proposed): move each operating value out of code into
explicit, versioned configuration, keeping its ceiling fixed, and state each value
once. See [operational limits](operational-limits.md) for the complete inventory. In
order, each its own PR with failing checks first and none started before the owner
accepts P42:

1. Assistant installation limits: a revisioned table for the pins bound, workspace
   turns, retained-context reserve and longest grant, saved under `ManageAgents`,
   recorded on each turn; runtime set `turns_v8`.
2. Sign-in policy: a revisioned table for sign-in lifetime, lockout, minimum password
   length and invitation lifetime under `ManageAccess`, recorded on each hosted
   session; the cookie expires with the session row, so the lifetime is stated once.
3. Tool result records and bytes on the agent version, as 0121 added the history
   window.
4. A required `limits` section in the hosted configuration for request deadlines, body
   sizes, pools, timeouts and `max_connections`, the sign-in rate, page sessions,
   heartbeats, polls and provider transport, checked against each other; the gateway's
   body sizes and read timeout are written from it, and the API serves its request
   deadlines to the page, which no longer has its own timeout. The collection and
   reference commands take their HTTP timeouts and deadlines from their own settings.
5. Each operation the page validates against serves its limits; the page's copies of
   server limits are removed.
6. One shared ceiling for the 4,096 retained contexts per catalog.
7. Settings pages for the installation limits and sign-in policy.

The pilot's next schema window applies 0122 (D256) over its hotfixed `pin_turn`.


D261 resolves exact-contract and prompt-offset authoring terms at each observation
fixing instant into concrete dated contract receipts. The scheduled ticket uses
the managed generator and booking retains the resulting source relationships.
Daily official settlements and explicit final carry feed connected valuation,
interactive and queued risk, and financial cash determination. Retained risk jobs
replay admitted observation evidence; determined cash does not demand obsolete
source prices. Migration 0133 distinguishes fixed observations from live samples,
even when both use the same observation record. The bounded consumer still
requires full-month calendar-day arithmetic observations. Automated-close source evidence,
broader source/calendar combinations and the general booked-program
execution cutover remain separate work; see the scenario matrix.


### Executable contract cutover

D264 authorizes removing superseded executable contracts rather than preserving
compatibility readers. Current inputs must state current terms explicitly; obsolete
records require explicit replacement/rebooking instead of silent conversion.

The executable cutover removes the older fixed-total futures pricing convention,
synthetic execution qualification without policy rows, model edits without an
identifier policy, cash-only discount providers, and projection-only floating-side
authoring. Migrations 0134–0136 retire the obsolete SQL writer and enforce the
current execution-policy and dated-source admission contracts. Each dated indexed
lookup requires its fixing binding; contract observations retain their individual
fixing instants without the obsolete side-wide boundary. Existing incompatible
records refuse rather than acquiring inferred replacement terms. This includes
previous dated-contract programs carrying a side-wide fixing timestamp: their
individual observation terms remain auditable, but executable schedules require
explicit rebooking under the current format.

Verification covers 648 managed core checks, 85 rate checks, API checks, 838 web
checks and 171 swap browser checks. Disposable-database checks exercise current
futures policies, explicit model commands, indexed fixing corrections, scheduled
synthetic and dated-contract booking/pricing/settlement/risk replay, and coherent
malformed schedule programs. Independent review covered source
admission, retained pricing, lifecycle behavior and amendment boundaries.

Remaining compatibility should be identified by actual callers and persisted
contracts, not by searching for version suffixes alone.

Generated-close and existing-close jobs retain distinct operation identities and
publication permissions while sharing pricing. Historical migrations and upgrade
checks remain necessary to reproduce supported database upgrades. Neither
migration history nor distinct current operations are duplicate pricing engines.


D268 adds a streamed Blotter valuation (`POST /api/trading/workspace/stream`: start count, complete rows per trade as they finish, totals once at the end) with a client reader in `web/src/blotter-stream.ts`. Blotter UI adoption (progress bar, grid built once, removal of `progressive`) is separate work.
