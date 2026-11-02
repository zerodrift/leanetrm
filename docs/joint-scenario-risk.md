# Joint scenario repricing and finite differences

D291 supplies the managed R2 calculation boundary over the existing immutable
market context and common instrument PV. It accepts explicit named deterministic
scenario sets and selected mixed derivatives. HTTP admission, durable joint-run
storage, workers and Risk widgets for these new results remain integration work.
The existing sensitivity reports and their retained-job contract are unchanged.

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
bindings. This increment does not add that storage/admission contract.

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

## Verification and remaining work

The managed executable `--joint-scenario-risk` checks independent linear,
quadratic, cross-term, cubic and kink oracles; unlike factor units and unequal
bumps under a nonzero stressed parent; exact rational results; quantity/base
handling; repeated and unaffected unit reuse; replay identities and complete
pre-layered ancestry; invalid policy/unit/domain/overlap/capacity; and cancellation
or failure without parent mutation. The ancestry regression fails with the old
risk-wrapper-only walk. Existing connected and quote risk share the same internal
portfolio evaluation mechanism and work reducer.

Next integration work is versioned joint-run admission/retention and graphical
named-scenario/mixed-sensitivity reports under current authority. Aligned dated
factor history, statistical calibration, VaR/ES, Monte Carlo/PCA/hybrid, EVT and
backtesting remain subsequent work. No migration or production deployment is
included in this calculation increment.
