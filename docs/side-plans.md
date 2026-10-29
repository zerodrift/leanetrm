# Side schedule evaluation

Status: design accepted for the local implementation trial (2026-10-28), amended
to retain executable schedules at booking (2026-10-29). This
record specifies the contract, not completion evidence. Current executable scope
and measurements are recorded in [whole-term side schedules](side-schedules.md).

A [payment-block experiment](side-block-experiment.md) separately tests retaining
vector calculations directly in stock PostgreSQL. D258 now accepts that direction
for the local trial. Replacing expanded native operation rows with retained
vector operations is in progress; the experiment does not qualify the application
cutover by itself.

D253 decides the side model: a Side is its parameters for the whole term,
`Side.GenerateSchedule()` builds its schedule from those parameters alone, rows
hold graph references rather than values, and `Side.PV` sums the side's PV
contributions (as amended by D258, whose logical rows and edges include
observations, applications, averages and amounts, with one contribution per
period or interval). D253 leaves open whether each row is itself a
graph node, or the side reads node values and sums, and asks for a bound and a
benchmark. D258 retains each booked side's complete schedule, rows and edges, in
PostgreSQL. The owner's 2026-10-29 amendment to D258 requires compilation at
booking, with the complete executable schedule retained for every Side family.
Booked valuation reads that program directly and binds its dynamic input columns
under one resolved context; it does not compile or rebuild the schedule on load.
Independent source reads run concurrently in bounded batches under the same
temporal and authorization contract. D257 decides the price node the rows read.
This supersedes the earlier compile-per-calculation proposal. Implementation and
qualification of direct execution remain work.

## Baseline before the trial

Before the trial, two schedule shapes existed; neither was the complete PV input on its own.
D253 replaces both with `ScheduleRow`; the behaviour below is the parity target.

- **Payment sides** (`SidePaymentSchedule`, `PaymentSchedules.cs`). The schedule is
  an object graph of `SidePaymentPlan`, `SchedulePriceTerm` and
  `SchedulePriceApplication`. `Dependencies` and `DependenciesAt` rebuild their
  lists on each call. The side holds its fixing schedule and passes one
  `IndexFixing` to each lookup, and `SideValuationContext.Resolve` decides between
  projection and fixing itself, repeating `RequireIndexProjection` and a subset of
  `IndexFixingPricing.Resolve`'s checks for every term in every scenario.
- **Interval sides** (`IntervalSideSchedule`, `PpaSides.cs`). The schedule holds only
  its side; its `Dependencies` throws. The rows actually valued are
  `PreparedSideRow`s held by `IntervalValuationInputs` under `(instrument, side)`.
  Period curves already decide between mark and published fixing inside the curve
  lookup (`IntervalPriceResolver`).
- **Row evaluation** (`IntervalValuationSession.Evaluate`, `Components`). Each row
  looks up its curve and period, selects a period contract with `Single`, finds
  its price in a dictionary keyed by `DateTimeOffset`, allocates its price
  components and builds a complete `ScheduleRowValue`, including rows that are
  not retained as detail.
- **Scenario reuse** (`BookValuation.cs`, `PV`). An instrument is either unchanged,
  and takes its base value, or affected, and re-evaluates every row of every
  side. `IntervalPriceResolver` reuses unchanged curve months, so those rows read
  reused prices, but every row is still evaluated, twice per factor.
- **Knowledge coordinates.** Interval inputs carry `KnownAt` and a
  `KnowledgeContext`, and every prepared curve must share the request's
  `ValuationAt` and `KnownAt`. Marks and fixings come from the `MarketSnapshot`'s
  pinned selections: a frame's cutoff is its valuation instant, and its market
  cutoff bounds observation times (D174). The snapshot carries no knowledge
  context, so nothing records that its selections and the interval inputs belong
  to one resolved context.

## Valuation measures preserved by the trial

| Row | Prices from | PV |
| --- | --- | --- |
| Value date after valuation (`InMeasure`) | The scenario | `amount × df` (Discounted) |
| Delivered certificate (`InMeasure`) | The scenario | `amount`, undiscounted (HoldingValue) |
| Value date at or before valuation, payment side | The base resolver | None; overdue known or estimated measure |
| Value date at or before valuation, delivery side | The base resolver | None; delivered-value measure |

The first matching row applies, in the order holding, discounted, overdue,
delivered: a delivered certificate with a later value date is a holding, and
delivered certificates take no payment key. Dated swap payment valuation admits
only undue rows; a due payment is refused before valuation. Interval payment
sides retain their explicit overdue measures. Rows that are delivered but not yet paid
remain `InMeasure`: they are discounted and take scenario prices, including
estimates under `EstimateUntilActual`. Price state (fixed, actual, published
fixing, projected, modeled, estimated) is per row and need not be contiguous in
time.

## Where time lives

Under D253 a row references nodes; it does not decide what they return. Whatever
depends on the valuation instant or a cutoff is therefore the node's, not the
row's:

- a price node returns a mark, a published fixing (D257), a collected actual, a
  frozen anchor or an estimate, with its state and lineage;
- a quantity node returns metered, contractual or forecast quantity with its state;
- a discount node returns the factor for its payment date.

The schedule stays a function of the side's parameters alone, as D253 requires,
and is valid on every valuation date while those parameters and the definition
and reference revisions are unchanged. Two things are computed per row from the
valuation instant, and together they form the partition:

- **Value-date boundary.** Rows whose value date is after the valuation instant are
  undue. Booking retains a separate value-date index; one search finds them.
  Payment order need not match delivery order.
- **Delivered boundary.** Rows whose interval has ended are delivered. One search on
  interval ends finds them. It helps determine holdings and `EconomicClass`, and
  whether a row reading a still-live period node is relabelled Estimated or refused
  (`actuals_required`), so one monthly node can serve delivered and live rows.

Quantity state comes from the resolved quantity node; elapsed delivery alone does
not make a forecast metered or contractual. Delivery and payment indexes stay
independent.

Rows outside the measure (overdue and delivered) read unshocked base values in
every scenario. Base and shocked input lanes belong to the same resolved
financial context, with their scenario identities recorded explicitly.

Evaluated state keeps price selection, delivery progress, contractual expiry and
payment state separate. A fixed price, elapsed delivery or expiry does not mean
paid. Settlement state is resolved for the trade binding and obligation
occurrence under the same effective time and knowledge cutoff; it is never
stored as mutable state on a reusable Side. Missing lifecycle support remains
explicit. This contract introduces no additional settlement commands.

## Booked executable schedule

Booking constructs and validates the complete executable schedule (D258),
including these columns and addresses. A draft constructs the same contract
transiently. Booked valuation loads and executes it directly:

| Column | Contents |
| --- | --- |
| Node tables | The distinct price, quantity and discount nodes the side's rows reference, each once, in a fixed order |
| Payment table | The distinct payment keys and contractual value dates |
| Contribution columns | Per contribution row: delivery window, grid index (D212), value-date ordinal, quantity node or fixed quantity, price-term slot |
| Price-term slots | Per index observation: price node ordinal, observation weight, multiplier and adder; or a fixed price |
| Node-to-row index | For each node, the contribution rows that read it |
| Row map | For each retained row, the execution terms it contributes to: one observation may feed several slots, and several applications and averages fuse into one coefficient |

Booking follows the retained rows' roles, roots and edges and records what the
PV roots read. No retained calculation is dropped, and bounded drill-down reads
the same operation and cell addresses. Optional reductions follow stored
instructions; valuation does not derive another execution plan from the rows.
Grouping is exact only for operations affine in their live price
nodes, with quantities independent of price: each operation version declares
whether it is, and the declaration is checked independently. Affine grouping is
an optional fast path. Other qualified operation versions use their declared
model evaluator; an unsupported operation is refused rather than forced into a
linear representation. This enables no option model by itself. The executable schedule
holds references and contract terms only, never node values.
The reusable program's identity includes the side's economic identity, exact
definition and reference revisions (including clock, TOU and shape definitions),
declared input-port shapes and execution version. The instrument's retained
composition binds those ports to its sibling sides at booking; it does not make
the reusable Side specific to that instrument. Value reuse additionally requires
the resolved port mappings, coverage and full context/scenario. Requested
coverage is an evaluation input, not a reason to rebuild the schedule.

Publication must prove that executable instructions, operands, bindings, grids,
ports and named roots agree with the typed relational definition. A content
digest establishes byte integrity, not this semantic correspondence. Publication
of both representations is atomic and idempotent. A missing or unsupported
executable schedule refuses under the existing reporting policy; it never invokes
a generator or falls back to the previous valuation path.

## One resolved context

Node values come from the graph of one resolved context (context §2). The context
records one identity and each role's cutoff explicitly:

| Input | Effective time | Cutoff |
| --- | --- | --- |
| Marks | The frame's market close | The frame's pins; a frame's cutoff is its valuation instant (D174) |
| Frozen anchors (`FrozenFact` block marks) | Their contract expiry | The frame's pins, with certified knowledge by `known_at` for ended intervals and by the earlier of the valuation instant and `known_at` for live ones |
| Index fixings | Their publication instants | The market cutoff (D257) |
| Collected actuals | Their intervals | `known_at`, and for live intervals the valuation instant (D174) |
| Shapes | Their revisions | `known_at` |
| Quantities | Their delivery intervals | `known_at`, with meter deadlines at the valuation instant; corrections update delivered quantities, never live forecasts with meters (D195) |
| Discount factors | Not yet defined | Today only the factor's snapshot identity, source and positivity are checked |

- **The same rules decide eligibility.** The context supplies the coordinates; the
  existing per-role rules decide what is eligible. This proposal does not change
  D174's split between marks at the frame's pins and actuals and shapes at
  `known_at`.
- **The correction policy is named.** An exact pinned snapshot and a certified
  latest-correction policy at the cutoff are different selections; the context
  names the one used for each role.
- **Mismatches are refused.** A snapshot or node input that does not belong to the
  context is a structured error. The snapshot (or the frame that builds it) must
  first record the context it belongs to. Discount factors have no rates cutoff
  yet; the step that records the context either defines one or explicitly leaves
  discount inputs to their current snapshot-identity check.

Independent source requests use bounded parallel reads after the authorized
context-capture transaction has committed and disposed. Each read uses its own
connection, the same committed context and explicit source cutoffs, with current
authorization; none captures a fresh current view. The caller joins complete
immutable results and rechecks authority before publication. A failed or
cancelled batch returns no partial successful valuation. This describes the
required adapter boundary, not a completed database implementation.

## Evaluation

D253's floating price term is an index (curve, multiplier, adder) observed under
an observation rule. For a row with quantity `q` and observations `k` of weight
`w_k` (total `W`):

```text
amount = Σ_c q_c · (m_c · Σ_k w_k · price_k / W + adder_c)
```

summed over the row's price components `c`: one for a payment row, one per
location component for a physical delivery row. A fixed price term gives
`amount = q · fixed price`. For dated swap payments `m = 1`, the adder is today's
spread (D257) and `q` is unit quantity × allocated quantity; interval payments
retain their selected multiplier. The quote's unit multiplier
converts price × quantity to money for both terms and is distinct from `m`.

These are the retained rows' observation, application, average, amount and
discount operations, evaluated together. For each side, over undue rows paid at
`p` and delivered certificate holdings, the PV root is:

```text
PV = Σ_p df[p] · (C[p] + Σ_d price[d] · Q[p, d]) + H_c + Σ_d price[d] · Qh[d]
```

- `d` is a price node whose value under the context is live: projected, modeled or
  estimated. A node returning a fixed price, a published fixing, an actual or a
  frozen anchor is a constant and contributes to `C[p]` or `H_c`, never to `d`.
- `Q[p, d]` sums `q_c · m_c · w_k / W` over components and observations, paid at
  `p`, that read `d`;
  `C[p]` sums `q · adder` and the constant amounts of those rows. `Qh[d]` and
  `H_c` are the same sums for delivered certificate holdings, which are not
  discounted.
- Overdue and delivered rows contribute no PV; their amounts feed the measures.
- Membership coefficients apply at the instrument, after the side PV (D253), as
  quantity ratios do today.

This is an exact regrouping of exact rational arithmetic. Parity covers both of
today's forms: interval components (`quantity × (multiplier × price + adder)`)
and dated swap payment applications (`(price + spread) × w_a / W_p × unit multiplier ×
unit quantity × allocated quantity`). Which grouping is constant and which is
live is a per-context property of the nodes. Coefficients and accumulators are
computed values under stored reduction instructions; their values may change
with context or scenario, while operation addresses and topology remain fixed.
Non-affine operations retain interval evaluation. A shape or quantity shock must
invalidate any reduced coefficients that depend on it.

Period summaries key on `SidePeriodKey` (class, coverage, lifecycle, quantity
state and value date); the modeled amount is a separate measure. The base
valuation groups by
`(p, d, SidePeriodKey, modeled)`; scenario valuations need PV only and use the
coarse grouping. The Blotter's evaluated schedule (D253) is produced from the same
stored rows and node values on request, for example a month's rows at a time.

## Answer to D253's row-node question

Rows are not `GraphSession` nodes. Every logical row is retained relationally
(D258), but the calculation graph holds only the price, quantity and discount
nodes the rows reference; the side reads their values and evaluates its rows by
the grouping above. The graph contains distinct source nodes and column
operations. Primitive interval quotes may have individual source coordinates;
derived interval cells do not each require a graph node. Drill-down identifies a
retained operation and cell and evaluates it from the same node values,
reproducing each interval exactly.

A shock still changes exactly the rows that read the shocked nodes, as D253
requires: the node-to-row index names them.

## Scenarios

- **Node-level change.** A scenario's `GraphSession` tracks the affected closure
  of its shifts. The side recomputes every affected price, quantity, weight, normalization and
  discount term in that closure, including constant and holding contributions.
  It recomputes products jointly so simultaneous quantity, price and discount
  changes include their cross terms; the side's PV is the base PV plus the exact change in those terms.
  Node dependencies follow the resolved context (D257): a due fixing depends only
  on its fixing observation and a live node on its projection, so an expired mark
  is never requested and the closure matches today's `DependenciesAt`.
- **Price nodes are not shock coordinates.** They are selectors downstream of mark
  points; a shift that targets one is refused, so a settled fixing never moves.
- **Dependencies identify work.** Amounts come only from evaluating the affected
  terms; no per-node amount is reported.
- **Facts never take shocks.** Shocks apply to the immutable scenario graph, never
  to rows, sides or trades. A constant node has no live input for a shock to reach.
- **One evaluator.** The scenario engine still calls the common valuation
  contract. Reuse is internal to the side.

Rates scenarios and time-shift scenarios have no current consumer (current risk
holds rates fixed); they remain future work.

## Reuse within and across calculations

Within one calculation, a side's loaded executable schedule serves the base and
every scenario. Source selectors resolve the eligible input branches under the
selected context. Crossing a fixing instant changes eligible source values and
dependencies, not stored schedule instructions. Across calculations the same
published executable schedule is reloaded. This proposes no process-level value
cache; any later cache needs explicit authorization, identity, retention and
concurrency rules with independent review. Shared-side value reuse also includes
resolved sibling ports, requested coverage and the complete context/scenario.

## Identity and storage

- The retained schedule (D258) includes the executable artifact constructed at
  booking. Receipts record its exact identity and execution version. Market
  samples and evaluated values are not part of the reusable definition.
- Equal executable contents do not establish PV reuse across contexts
  ([schedules](schedules-and-contract-sequences.md#shared-immutable-data-composition-and-the-fast-path)).
- This record adds no table beyond D258's.

The first executable-format slice has a booking-time compiler, a closed byte
format and an in-place reader. Checks cover dated whole-term payments and native
physical, linked payment, certificate and capacity schedules. Publication
admission compares the complete program to the supplied typed definition;
changing a contractual price cannot be certified merely by replacing its byte
digest or claiming the same source manifest.

This is format qualification only. The compiler and publication check require an
already admitted financial definition. They do not establish source eligibility,
current authority or database publication. Policy identifiers are retained
exactly; their supported source adapters and interpreter must refuse unknown
financial semantics. Database seal enforcement, authorized loading and live
valuation through this format remain separate integration work.

Input selection now propagates per-cell base/scenario eligibility through the
stored operand addresses. Separate payment rows retain their own value dates and
selection, even when they share a lookup. Broadcasts union source uses;
reductions require their complete retained input windows. Holding and overdue
amounts do not request unused discount factors. These are source requests under
one context, not new schedule rows or a replacement execution graph. Source
resolution and trade-owned settlement integration remain subsequent work;
selecting the scenario lane does not make a published fixing shockable.

The evaluator foundation executes supplied admitted columns through those stored
addresses. Checks cover independent whole-term payments, rational daily
averages, hourly and quarter-hour DST grids, profile durations, volume
normalization, indexed multipliers/adders, capacity reductions and certificate
holdings. Separate contribution rows retain base overdue amounts and selected
scenario PV. Per-cell presence distinguishes a resolved zero, an input not
requested in that lane, and PV explicitly excluded by a lifecycle guard. Only
the last may be omitted from the declared measure. Equal actual scenario
identities use one canonical input with unioned demands. Eligibility masks also
suppress unused intermediate arithmetic; they do not change stored operations.

Arithmetic reuses the shared signed-ratio primitive, preserving unbounded cash
intermediates and the existing per-operation quantity and price limits. Discount
inputs retain their positive exact-working-decimal contract. Mixed component
price states remain separate flags, including modeled-price evidence. Row
classification is for the gross unit schedule; it does not establish that a
particular trade has or has not paid. Accepted cash, allocations and payment
state must come from that binding's obligation records under the selected
context. Source eligibility, lineage, authorized SQL loading and application
cutover are not qualified by these supplied-column checks. No end-to-end speed
or production-throughput claim follows from this evaluator foundation.

## Checks

Behavior checks precede each implementation step and stay independent of the
evaluation's algorithm:

1. **Parity.** For the synthetic PPAs and swaps, PV, value measures and period
   summaries equal today's for base, up and down under each supported bump,
   including delivered-unpaid rows, estimates, certificate holdings, capacity
   sides, period and interval curves, and shaped curves. Receipts equal today's
   apart from the added implementation version and schedule identity. Values
   are evaluated from loaded schedules and from the transient draft schedule,
   and agree. This is D253's first qualification check, applied to this
   evaluation.
2. **Lineage.** Every retained row maps to the execution terms it contributes to,
   and every row re-evaluated on request reconciles to its roots.
3. **Schedule independence.** One side's executable schedule is identical across
   consecutive valuation dates and a fixing publication; only node values and the
   input-use masks differ. A correction changes resolved quantity, price or
   discount values, not stored instructions. A booked read invokes no schedule
   generator, lowering pass or replacement execution-graph compiler.
4. **One context.** A snapshot or node input from another context is refused.
5. **Node-level reuse.** A scenario reaching one node gives the same PV as full
   evaluation and recomputes only the terms that read it; the evaluated schedule
   changes only in those rows (D253's shock check).

A benchmark records workload, unique sides, rows, nodes and contracts, scenarios,
terms recomputed, allocations, retained memory and environment, separately for
booking-time compilation, schedule loading, input preparation, base evaluation
and each scenario, as D253's row-node question requires.

## Order of work

The owner authorized a local implementation trial on 2026-10-28. Each behavior
starts with an independent failing check. Publication and hosted schema changes
remain separate actions. The 2026-10-29 amendment applies to every Side family.
Each integration step is a separate reviewable commit:

1. Benchmark the current interval and payment paths, separating input
   preparation from evaluation, for base and up/down.
2. Record the resolved context on the market snapshot or its frame, and refuse
   valuation inputs that do not belong to it; define a rates cutoff for discount
   factors or explicitly keep their current check.
3. Persist the complete executable schedule at booking, prove correspondence to
   the typed definition, and load it without generators or read-time compilation.
4. Resolve explicit dynamic inputs concurrently under one committed context,
   preserving authorization, lineage, units and grids. Evaluate stored scalar and
   vector instructions behind the exact parity and lineage checks.
5. Reuse unaffected scenario values using declared dependencies and the retained
   reverse addresses; compare with independent full repricing.
6. Produce the Blotter's evaluated schedule and context-based row states from
   retained addresses and resolved values.

## Open questions

- Should a corrected historical view (context §7) select mark corrections known by
  `known_at`, rather than the frame's pins? That would refine D174 and needs its
  own decision.
- Which source algorithms require graph nodes for their supported scenarios.
  Normalization and shaping remain explicit operations with declared dependencies
  regardless of the source adapter; a product label does not select an evaluator.
- How often grouping by payment and node reduces work on hourly-shaped books.
- Which lifecycle policy the unified `FixedSide` and `FloatingSide` carry for a due
  payment, and where it is declared: today swaps refuse one (`side_payment_due`)
  and PPA payment sides report it as overdue. Parity needs both.
