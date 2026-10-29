# Side schedule evaluation

Status: design accepted for the local implementation trial (2026-10-28). This
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
PostgreSQL. This record proposes how a loaded schedule executes: compiled once
per calculation into columns, with node values read under one resolved context,
and only the work a scenario changes repeated. D257 decides the price node the
rows read. Neither changes D253's or D258's contract.

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
delivered certificates take no payment key. Payment sides have only undue rows; a
due payment is refused before valuation. Rows that are delivered but not yet paid
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
  undue. Compilation retains a separate value-date index; one search finds them.
  Payment order need not match delivery order.
- **Delivered boundary.** Rows whose interval has ended are delivered. One search on
  interval ends finds them. It helps determine holdings and `EconomicClass`, and
  whether a row reading a still-live period node is relabelled Estimated or refused
  (`actuals_required`), so one monthly node can serve delivered and live rows.

Quantity state comes from the resolved quantity node; elapsed delivery alone does
not make a forecast metered or contractual. Delivery and payment indexes stay
independent.

Rows outside the measure (overdue and delivered) read base-context node values in
every scenario, as today; a scenario evaluation therefore reads two contexts, both
recorded.

## Compiled schedule

Valuation loads a booked side's retained schedule (D258), or takes a draft's
transient one, and compiles it once per calculation into columns:

| Column | Contents |
| --- | --- |
| Node tables | The distinct price, quantity and discount nodes the side's rows reference, each once, in a fixed order |
| Payment table | The distinct payment keys and contractual value dates |
| Contribution columns | Per contribution row: delivery window, grid index (D212), value-date ordinal, quantity node or fixed quantity, price-term slot |
| Price-term slots | Per index observation: price node ordinal, observation weight, multiplier and adder; or a fixed price |
| Node-to-row index | For each node, the contribution rows that read it |
| Row map | For each retained row, the execution terms it contributes to: one observation may feed several slots, and several applications and averages fuse into one coefficient |

Compilation follows the retained rows' roles, roots and edges and groups what the
PV roots read. Grouping fuses rows into coefficients for execution only; no
retained row is dropped, and drill-down re-evaluates any retained row on request
from node values. Grouping is exact only for operations affine in their live price
nodes, with quantities independent of price: each operation version declares
whether it is, and the declaration is checked independently. Affine grouping is
an optional fast path. Other qualified operation versions use their declared
model evaluator; an unsupported operation is refused rather than forced into a
linear representation. This enables no option model by itself. The compiled schedule
holds references and contract terms only, never node values.
Its identity is the side's economic identity, its instrument and slot context, its
coverage, the definition and reference revisions (including clock, TOU and shape
definitions) and the evaluation implementation version.

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
live is a per-context property of the nodes, so the grouping is rebuilt with node
values; the compiled schedule is not.

Period summaries key on `SidePeriodKey` (class, coverage, lifecycle, quantity
state and value date); the modeled amount is a separate measure. The base
valuation groups by
`(p, d, SidePeriodKey, modeled)`; scenario valuations need PV only and use the
coarse grouping. The Blotter's evaluated schedule (D253) is produced from the same
compiled rows and node values on request, for example a month's rows at a time.

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

Within one calculation, a side's compiled schedule serves the base and every
scenario. Across calculations the retained schedule (D258) is reloaded, and the
graph already advances frames copy-on-write (`GraphSession.Advance`, or `Rebind`
when a frame's resolved dependencies differ, as when a fixing instant passes and a
node's dependency moves from its projection to its fixing). Reusing
compiled columns across calculations needs a
process-level cache, which today's design deliberately does not have ("No global
cache or implicit current-data read exists"). It is not proposed here; a later
proposal would define its authorization namespace, keying, retention bound and
concurrency, with adversarial review, after measurements show compilation matters.

## Identity and storage

- The retained schedule (D258) is the stored artifact. Compiled columns are
  derived per calculation and never stored; receipts add the evaluation
  implementation version and the retained schedule's identity.
- Equal compiled contents do not establish PV reuse across contexts
  ([schedules](schedules-and-contract-sequences.md#shared-immutable-data-composition-and-the-fast-path)).
- This record adds no table beyond D258's.

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
3. **Schedule independence.** One side's compiled schedule is identical across
   consecutive valuation dates and a fixing publication; only node values and the
   partition differ. A correction changes node values, and compiled quantity terms
   only where quantity preparation stays compiled (see open questions).
4. **One context.** A snapshot or node input from another context is refused.
5. **Node-level reuse.** A scenario reaching one node gives the same PV as full
   evaluation and recomputes only the terms that read it; the evaluated schedule
   changes only in those rows (D253's shock check).

A benchmark records workload, unique sides, rows, nodes and contracts, scenarios,
terms recomputed, allocations, retained memory and environment, separately for
schedule loading, input preparation, schedule compilation, base evaluation and
each scenario, as D253's row-node question requires.

## Order of work

The owner authorized a local implementation trial on 2026-10-28. Each behavior
starts with an independent failing check. Publication and hosted schema changes
remain separate actions. D253's side types and D258's retained schedules are separate
work; steps 3 to 5 apply to their rows.

1. Benchmark the current interval and payment paths, separating input
   preparation from evaluation, for base and up/down.
2. Record the resolved context on the market snapshot or its frame, and refuse
   valuation inputs that do not belong to it; define a rates cutoff for discount
   factors or explicitly keep their current check.
3. Compile a loaded or draft schedule into columns, with its row map, and
   evaluate it by the grouping above, behind the parity and lineage checks.
4. Node-level scenario reuse from the scenario graph's affected closure. Interval
   pricing (`IntervalPriceResolver`, `BlockShaping`) runs outside `GraphSession`
   today; its price nodes must become graph nodes first.
5. Produce the Blotter's evaluated schedule from the retained rows, the row map
   and node values.

## Open questions

- Should a corrected historical view (context §7) select mark corrections known by
  `known_at`, rather than the frame's pins? That would refine D174 and needs its
  own decision.
- Which parts of PPA quantity preparation (asset versions, normalization,
  shaping) become graph nodes and which stay compiled terms; step 1's measurements
  show whether it matters.
- How often grouping by payment and node reduces work on hourly-shaped books.
- Which lifecycle policy the unified `FixedSide` and `FloatingSide` carry for a due
  payment, and where it is declared: today swaps refuse one (`side_payment_due`)
  and PPA payment sides report it as overdue. Parity needs both.
