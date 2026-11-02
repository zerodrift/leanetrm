# Joint scenario repricing and finite differences

D291 supplies the managed R2 calculation boundary over the existing immutable
market context and common instrument PV. It accepts explicit named deterministic
scenario sets and selected mixed derivatives. D295 integrates HTTP admission,
durable worker runs and shared graphical reports in Risk and Blotter. Legacy
symmetric sensitivity requests preserve their original wire and job behavior.

## Independent joint scenarios

`RiskEngine.CalculateScenarios` takes a prepared, authorized portfolio projection,
a `ConnectedRiskScenarioSet`, the exact rational portfolio numerical policy and
book or holding detail. The set has an explicit identity, version and
`independent_joint_absolute_curve_scenarios_v1` convention. Every named scenario
supplies its label and signed absolute `CurveShift` definitions, with exact units,
targets and parent-additive convention. There is no inferred shock magnitude or
default stress catalog.

Each scenario is a temporary child of the same selected `ConnectedScenario`.
Multiple factors move together in that child. A selected parent stress makes all
results conditional on that stress. Samples never accumulate each other's shifts;
neither success, failure nor cancellation changes the parent. An exception needs
no market reset or undo operation.

The base PV is calculated once. Each child uses the same portfolio valuation path
as symmetric up/down risk: declared dependencies control affected work, eligible
unit values are shared, and the valuation path applies external quantities and
execution bases once. No instrument-family filter or Greek-based eligibility
shortcut is introduced. In particular, zero local delta does not remove a factor.
Computed-output interventions remain explicitly distinct from market-quote moves.

For each book/currency and optional binding, exact rational results retain:

```text
pnl(s)  = value(s) - base value
loss(s) = base value - value(s)
```

Groups retain their selected valuation measure; currencies and measures are not
combined. Named stresses have no assigned probabilities and are not VaR samples.
The operation returns a completed result only after every scenario succeeds and
the final cancellation check passes. Missing inputs or failed samples do not
produce a successful partial portfolio result.

The bounded set is processed one scenario at a time, retaining value/shift/work
receipts rather than executable child graphs or pricing sessions. Book detail
omits the positions-by-scenarios matrix. Current guardrails are 512 scenarios,
64 target shifts per scenario and 250,000 retained book/position value tuples
across the base and samples. These are allocation guardrails, not qualified
production workload or latency claims.

Receipts include snapshot and graph-manifest identities, the selected base's
complete scenario-layer ancestry, and each child's resolved exact points, units,
signed shifts and observation references. Work includes actual PV calls, quantity
applications and affected/evaluated/reused graph work. Receipts contain no graph
sessions. They identify the captured calculation lineage; a standalone persisted
replay also needs the authorized input manifest, retained definitions and pricing
bindings. The HTTP/retained integration below captures those inputs.

## Selected derivatives and step agreement

`RiskEngine.CalculateFiniteDifferences` consumes the same scenario operation.
The caller selects `exact_central_half_nominal_double_v1`, one to 16 named axes,
and an explicit list of up to 16 mixed pairs. Each axis supplies an existing
positive symmetric bump and separate delta/gamma tolerances. Each mixed pair
names two selected axes and its tolerance. Mixed axes must have disjoint resolved
coordinates; aliases and overlapping parallel/bucket targets cannot masquerade
as independent factors. Each unordered pair is selected at most once.

For each axis or pair, the engine calculates at half, nominal and double the
specified bumps. At any one step:

```text
delta_i    = (V(+hi) - V(-hi)) / (2 hi)
gamma_ii   = (V(+hi) - 2 V(0) + V(-hi)) / hi²
gamma_ij   = (V(+hi,+hj) - V(+hi,-hj) - V(-hi,+hj) + V(-hi,-hj)) / (4 hi hj)
```

Single axes cost six child evaluations; each selected mixed pair costs twelve.
There is one shared base. The engine does not request a dense Hessian. Both
factors' native price units, their ordering, requested bumps, formula versions,
all scenario receipts and the three exact estimates remain in the result. A
tiny step that cannot be halved exactly, a shifted quote outside its domain or
an unavailable corner fails explicitly without rounding or substitution.

`Stable` means both the half-step and double-step estimates satisfy:

```text
abs(estimate - nominal) <= absolute tolerance + relative tolerance * abs(nominal)
```

Tolerances must be explicitly nonnegative. The absolute tolerance has the
derivative's native units; the relative tolerance is dimensionless. Comparisons
use exact rational arithmetic, including zero or negative nominal estimates.
Agreement concerns the selected book aggregate or holding and the three chosen
steps. It does not establish differentiability, global linearity or model
accuracy. For example, the symmetric delta of `abs(x)` at zero agrees at zero
while its central gamma changes with the step; the gamma diagnostic is unstable.
Full-repricing scenario loss remains available independently of Greek stability.

## HTTP and retained runs (D295)

`POST /api/portfolio/risk` and its catalog/job operations accept optional typed
`joint` under `portfolio_instrument_repricing_v1`. Its discriminator is
`portfolio_joint_risk_v1`, with exactly one non-null `scenarios` or `derivatives`
contract and explicit empty `bumps`. Named sets supply identity/version, labels
and signed shifts. Derivative requests supply selected positive bump axes,
explicit delta/gamma tolerances and selected mixed pairs with their tolerances.
No default magnitudes, pairs, tolerances or probability model are introduced.

The HTTP bounds are 32 named scenarios, 16 shifts per scenario, 16 selected axes
and 16 mixed pairs. Admission checks aggregate base/sample tuple width against
250,000, PPA row-scenario work against the existing bound, and solved-option
nonterminal rollback work against a 100 million node bound. The catalog
exposes these limits. A positive FD bump must support exact half and double
steps at admission. These bounds do not establish production throughput.

All requested factors resolve before execution. Distinct targets in one named
sample, or a selected mixed pair, must have disjoint compiled nodes, including
aliases with different source keys. A parent stress remains a separate layer
and can reuse the same node. Each exact market partition receives common named
sample IDs; a partition without a sample's factors explicitly reuses the parent.
FD partitions calculate applicable axes and local mixed pairs. Across independent
market partitions a mixed derivative is exactly zero. Book totals aggregate only
matching measure, book and exact currency identities; half/nominal/double values
are added before aggregate agreement is assessed.

Migration 0172 extends existing retained axis validation with private invoker
helpers. The original typed request, exact unique axis/point union, selected scope,
pricing policy, observations, model receipts and connected input manifests remain
pinned. Worker reconstruction uses the same preparation and calculation operation.
Current authority, lease, cancellation and atomic publication fences remain in
force. A joint run is one complete job; legacy sensitivity multipart frames refuse.
A retry returns its first outcome.

Joint reports contain typed `joint` results and omit legacy `factors`; older
sensitivity readers fail closed. The shared Risk/Blotter widget verifies complete
sample/partition/binding coverage, displays backend loss/P&L or native derivatives,
and retains exact tables, three-step agreement and binding drill-through. Named
stresses remain deterministic cases without assigned probabilities.

## Verification and remaining work

The managed executable `--joint-scenario-risk` checks independent linear,
quadratic, cross-term, cubic and kink oracles; unlike factor units and unequal
bumps under a nonzero stressed parent; exact rational results; quantity/base
handling; repeated and unaffected unit reuse; replay identities and complete
pre-layered ancestry; invalid policy/unit/domain/overlap/capacity; and cancellation
or failure without parent mutation. The ancestry regression fails with the old
risk-wrapper-only walk. Existing connected and quote risk share the same internal
portfolio evaluation mechanism and work reducer.

Integration checks cover simultaneous moves under a nonzero parent, independent
siblings, multiple exact market partitions and explicit unaffected partitions;
selected local/cross-market derivatives, native aggregation, direct/worker parity,
selected product binding PV oracles, private SQL admission and report reload.
Aligned dated-factor history, statistical calibration, VaR/ES, Monte Carlo/PCA/hybrid, EVT and
backtesting remain subsequent work. No production deployment is implied by these
local checks. Migration 0172 requires separate installation qualification.
