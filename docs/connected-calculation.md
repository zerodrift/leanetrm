# Connected curve calculation

Status: one connected evaluator for ordinary curves, basis relationships and
saved recipes, with monthly booked projections and retained portfolio execution
(D182–183, D186–187). The custom-curve workbench edits exact weighted mappings and
exact-version published custom sources; the trade
editor selects compatible published outputs; portfolio risk exposes quote and
computed-output targets through the API and UI. The backend uses the database's
existing certified financial context. Contract/date selection, formula compilation,
pricing and scenario behavior remain entirely in the backend.

## What changes dynamically

A calculated curve retains its operators and exact input links. For example,
an underlying contract can feed a weighted basket, which feeds a mean and then
another curve. Shocking that contract reevaluates the affected downstream nodes.
Shocking the basket's computed output adds an offset at that node and propagates
it downstream while preserving its upstream links. Cached answers remain scoped
to the graph and scenario; they do not replace those links with source prices.

The graph is prepared for one pinned financial context. It does not subscribe to
database changes during evaluation. A new snapshot or date selection requires
new preparation; a price shock creates a child scenario over the existing graph.
Rolling contract and schedule selections stay pinned during price shocks.
Publishing a new recipe version does not rebind an existing booked projection.
Historical samples deliberately remain frozen when current quotes are shocked.

Published custom sources import their exact output panel through direct graph
edges. Parent declarations, manifests, calendars/schedules and genuine native
samples remain in the compiled closure, including shared ancestors. All versions
use the same resolved context; the saved relationship is an exact output-node FK
under migration 0082. The supported bound is 32 published-source edges per path
and 256 exact recipe versions. Native source history stays within one exact market
revision. This adds composition without changing resampling or panel-alignment
policies; see [custom recipes](custom-curves.md).

| Change | Calculation behavior |
| --- | --- |
| Primitive quote shock | Reevaluate that input and its affected consumers; reuse unaffected parent values |
| Computed-output shock | Apply an offset after that node's calculation; downstream consumers see it |
| Nested shock | Preserve inherited offsets and isolate the parent and sibling scenarios |
| New snapshot or financial dates | Prepare a new graph with the selected observations and references |
| Published recipe edit | Create a new version; existing exact booked references keep their version |

Automatic persisted activation of logical curve versions and continuous live-feed
updates are separate implementation work. The managed bitemporal resolver and
its tests do not imply those operations exist in the application.

## Calculation contract

`Calculation/ConnectedCurves.cs` prepares typed curves against one
`ResolvedCalculationContext`. The context retains effective time, knowledge
cutoff, valuation time, market cutoff and the identity of the already-resolved
input manifest. Ordinary historical valuation requires equal effective and
valuation instants. A context identifier is not an authorization capability.

Exact curve references retain their versions. A logical curve reference uses
`TemporalCurveBindings` to select one exact version under half-open effective
and knowledge intervals. Missing or overlapping assertions, unavailable
publication history and unsupported context modes fail. Every transitive
reference resolves under the same context. A date change prepares a new graph;
it cannot rewrite an existing session. The resolver consumes retained assertions
supplied by a trusted caller; persistence and certification of those logical
activation assertions remain work.

Supported calculated relationships are explicit uniform means, weighted means,
signed weighted baskets/spreads, rolling contract selections and resolved
schedule occurrence mappings. Weights are dimensionless and required; weighted
means require nonnegative weights with a positive sum. Inputs must share the
same exact currency, quantity and quote multiplier. No conversion, interpolation
or weighting policy is inferred. Rolling selections use the existing managed
contract selector and retain the dated contracts selected for that context.
Price shocks do not roll those contracts.

Live source points retain their exact input, observation, quote and contract
bindings. Frozen facts remain explicit. Derived nodes retain references to
their dependencies; they never turn a derived result into a detached source
price. Exact rational arithmetic preserves nonterminating averages through PV.
Existing typed source operations retain their explicit 18-decimal-place
representability and quote-domain checks. Connected means, weighted combinations
and instrument PV preserve exact rational values; working-decimal consumers
reject values they cannot represent. Display rounding is separate. This does not
silently relax the source model's numerical contract. SQL recipe execution is
removed; unsupported nodes and engine versions reject.

## Scenarios

`ConnectedScenario.Base(plan)` creates one scenario. `WithShifts` creates a
child, leaving the parent and siblings available. Each shift specifies a signed
absolute amount, exact price unit and `absolute_curve_coordinate_parent_additive_v1`.

Two coordinates are supported:

- `MarketQuote` moves a live primitive quote and rebuilds affected consumers.
  The effective price must remain in its declared quote domain. A shared input
  receives one shift even if multiple curve points reference it. Frozen facts
  cannot receive this shift.
- `ComputedOutput` adds an offset after evaluating a calculated point. Its
  upstream dependencies remain connected and fixed unless independently
  shocked. All downstream consumers see the shifted output. An inherited
  output offset survives a later upstream shock.

Targets select all retained points of an exact curve, explicit point identities,
an exact futures/forward contract, or a half-open delivery bucket. The supported
delivery convention requires whole-point containment: a boundary splitting a
point raises an error. Parallel selection covers the curve points in the
prepared dependency closure; result receipts enumerate the resolved coordinates.
Different requests in one layer cannot overlap the same resolved node. Nested
layers can shift it again. Node ancestry is bounded to 64 layers.

Percentage shocks, interpolation changes, volatility surfaces and correlations
remain work. Volatility needs explicit absolute-volatility versus relative
conventions, surface coordinates and model rebuilding rules. Correlation needs
an explicit parameterization and matrix validity policy. Unsupported families
must fail rather than inherit commodity price semantics.

## Pricing boundary

`ConnectedValuation.Prepare(scenario, snapshot)` checks the shared input pins
once. `WithScenario(child)` reuses that validation only for the identical
prepared graph. `PV(instrument, bindings)` checks the instrument's existing
declaration, fences curve-price access to its exact bindings and calls
`Instrument.PV()`.
`SideValuationContext.ExactPrice()` resolves rational prices from the active
scenario on demand. The returned valuation must belong to that exact instrument
and context.

The shared provider connects underlying quote → computed mean/basket → common
instrument PV. Its stored monthly swap adapter preserves the original index and
actual-fixing checks while resolving the projected request through a distinct
exact custom-binding alias. Rational prices flow through the scheduled payment,
instrument and external holding quantity. Fully fixed swaps use their retained
actuals without a projected graph dependency.

A prepared valuation accepts the primary snapshot plus explicit source sample
snapshots. Each individual snapshot retains one observation per exact input;
different historical snapshots can retain different frozen observations of the
same series. Complete observations, use roles, sample cutoffs, recipe versions,
calendar/schedule evidence and graph manifests participate in reconstruction.
Historical sampling does not turn old marks into current risk factors.

Migration 0076 pins the published version/output descriptor to the floating side;
0077 separately retains a job's graph manifests, custom versions and dated sample
observations with exact foreign keys. Explicit booked version references remain
fixed. Effective/knowledge context selects retained trade relationships and
visible publications, while current authority gates every business read. The
managed logical-binding resolver is not a database activation table or a latest
version-by-name lookup.

The portfolio API, trade editor and durable worker use this materialization.
The HTTP/UI scenario catalog supplies exact quote/output coordinates and contract,
explicit-point, parallel and whole-delivery-bucket targets. The browser transports
those descriptors and explicit amounts; the backend resolves and validates their
members. Nested stress layers and symmetric risk bumps use the same graph.

Migration 0080 introduces `custom_curve_recipe_v2` and normalized weighted-contract
rows. Multiple source ports retain exact source/member foreign keys, output
positions and explicit signed-sum or normalized-mean weights. It refuses existing
saved recipes pending an explicit operator reset/rebooking; no historical recipe
is silently reinterpreted. Migration 0081 retains every portfolio market's graph
manifest and typed scenario axes, including their resolved points. Compiled recipe
coordinates are runtime graph identities anchored to that manifest, not invented
foreign keys to persisted curve definitions. Migration 0081 refuses any
retained risk jobs, including completed ones. An operator must explicitly archive
and reset them before adopting this baseline; the migration deletes nothing. The portfolio risk
routes are the sole portfolio-risk HTTP operations; no legacy futures-risk fallback remains.

The persisted custom projection supports monthly sampling within one source
catalog and revision. Database logical activation, daily custom mappings and
cross-revision histories remain work.

## Adopting the schema cutover

Use ordered migrations only in a separately reviewed installation baseline.
Stop new admissions and drain active API/worker operations before changing that
baseline. Inventory saved custom-curve versions and all retained risk jobs,
including terminal jobs. Migration 0080 requires no saved recipe versions;
0081 requires no retained risk jobs. Completing or cancelling a job does not
remove it and does not satisfy the second prerequisite.

Use the approved migration operator's read access to the complete tables;
runtime row-level security can hide records relevant to these global guards.
The inventory queries are read-only:

```sql
SELECT state, count(*) FROM market_data.custom_curve_versions GROUP BY state;
SELECT state, count(*) FROM risk.jobs GROUP BY state;
```

Preserve the old installation's financial history and results in its reviewed
archive/backup process, including the matching application/worker artifacts and
private configuration needed for retained reads. A fresh baseline with explicitly
recreated recipes and rebooked trades is the supported cutover approach; no
in-place conversion or general-purpose archive/reset command is supplied. Do not disable immutability
guards or delete foreign-key dependents to force the migrations through. If a
precondition fails, retain the prior baseline and resolve it explicitly.

The two migrations commit separately. An 0081 refusal does not undo an already
committed 0080. Inventory both prerequisites before applying either migration;
recovery of a partially applied baseline requires the reviewed backup/restore
procedure, not an application-image rollback.

Qualify the candidate with the disposable connected-portfolio and retained-risk
gates below, then verify its exact published output bindings, book totals and
scenario targets against reviewed inputs. API and worker artifacts must match:
jobs retain the admission engine fingerprint, and incompatible workers leave
them queued. Application-image updates and the Swarm worker definition do not
apply schema migrations. See [risk workers](risk-engine.md#workers-and-horizontal-execution)
and [the verification record](verification.md).

## Execution and qualification

`DependencyPlan` stores integer-indexed forward/reverse adjacency arrays and a
topological order. It rejects invalid edges and cycles without recursive graph
traversal. `GraphSession` evaluates declared dependencies on demand, caches
completed results and tracks only the affected closure for a child scenario.
Base storage is shared; child offsets and changed values are sparse. Every
potential branch input must be declared even when the base calculation does not
use it. Exceptions and cancellation never publish an incomplete node result.

The graph kernel is separate from the rich curve-definition compiler. Large
integer graph benchmarks do not qualify million-point financial compilation,
exact rational pricing, database loading, hosted throughput or distributed risk.
Broad shocks still perform work proportional to their affected closure.
Completed risk factors retain immutable scenario receipts and work counts.
Results do not retain execution caches after each up/down pair has priced all
valuation groups.
Interactive preparation reads visible source-revision history before bounded
sampling. Source topology compilation does not evaluate prices.
`ConnectedPreparationWork.SourceCompilations` and `DeclaredSourceNodes` count that
preparation separately from `GraphWork`; the portfolio's existing futures
materialization build remains counted as evaluated preparation work. Workers reload
admitted sample IDs. Historical query pushdown and end-to-end large financial
workload qualification remain work. Worker replicas
process separate whole runs; this integration does not distribute one graph or
reduce one run across machines.

Run the registered managed checks through `tools/check_all.py`, or use the
MarketData.Checks switches `--dependency-plan`, `--curve-engine-cutover`,
`--temporal-bindings`, `--connected-curves`, `--connected-scenarios`,
`--exact-curve-prices`,
`--connected-recipes`, `--custom-projection` and `--connected-portfolios`.
`tools/run_preview.py --connected-portfolio-check` exercises the saved projection,
weighted multi-source recipes, published-source chains, booking, shared portfolio and durable worker path
in a disposable database. Before applying 0080/0081, that gate also verifies
refusal over retained draft/published v1 recipes and queued/completed jobs, with
unchanged rows/schema and rolled-back fixtures. `--risk-jobs-check` includes HTTP
coordinate and target selection checks; `--custom-curves-check --browser-check`
exercises the workbench.
The Release benchmark switch is `--connected-graph-benchmark <output.json>`;
keep its generated report outside the repository. The workload records node and
edge counts, scenarios, evaluated work, allocation/retained memory, environment
and independent arithmetic verification. See the [benchmark record](connected-graph-benchmark.md).
