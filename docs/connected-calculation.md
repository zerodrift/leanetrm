# Connected calculation prototype

Status: managed backend prototype. The public portfolio API, retained risk jobs
and custom-curve editor continue to use their existing paths. This prototype
does not admit custom curves into booked instruments, migrate saved recipes or
certify database knowledge cutoffs. No frontend calculation code is added.

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
supplied by a trusted caller; SQL activation and certification integration remain
work.

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
Working-decimal consumers explicitly reject an unrepresentable rational value.
Registered SQL transforms require a separately qualified executor and are
rejected by this prototype.

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

An executable synthetic instrument proves underlying quote → computed mean →
weighted spread → common instrument PV. Existing stored swap schedule pricing
still requires its qualified market-model adapter; this prototype does not
replace its input checks. The existing snapshot bridge admits one retained
observation per exact input key. A historical average using multiple frozen
observations of the same series can be evaluated in the curve graph, but cannot
yet pass through this instrument bridge; it fails validation. Persisted book
bindings, portfolio aggregation through this path, API/worker receipts and
custom-editor integration remain work.

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

Run the registered managed checks through `tools/check_all.py`, or use the
MarketData.Checks switches `--dependency-plan`, `--temporal-bindings`,
`--connected-curves`, `--connected-scenarios` and `--exact-curve-prices`.
The Release benchmark switch is `--connected-graph-benchmark <output.json>`;
keep its generated report outside the repository. The workload records node and
edge counts, scenarios, evaluated work, allocation/retained memory, environment
and independent arithmetic verification. See the [benchmark record](connected-graph-benchmark.md).
