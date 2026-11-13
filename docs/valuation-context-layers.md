# Valuation context and scenario layers

Status: the initial managed valuation layers and P&L consumer are implemented
locally. Managed, browser and disposable PostgreSQL 18 checks cover the initial
report run/save/read/restart and both time modes. This is not a production
qualification or a general source-composition feature.

The executable slice adds a distinct calculation clock, declared time/discount/IV
inputs, atomic price/clock/IV children, common finite differences, shared unit and
Side reuse, and two P&L time modes. Separately dated holdings and market sources,
backward clocks, executable volatility surfaces, rate-shock axes, projected
lifecycle events and non-price worker requests remain planned. Those boundaries
are explicit below and in [trading P&L](trading-pnl.md).

## 1. Purpose and requirements

A valuation must be able to use an explicitly selected period's market inputs
while calculating at a different valuation instant, t₀. Numerical theta is one
consumer. Historical-market analysis, combined stresses and other sensitivities
must use the same capability.

Agreed requirements:

- Instruments and Sides own valuation. Books and portfolios aggregate the same
  `PV(context)` results. Risk creates scenarios and compares those results;
  it does not choose instrument formulas or assign exposure from dependencies.
- Scenarios are immutable, temporary layers over admitted inputs. They preserve
  source evidence, knowledge cutoffs, exact contracts and current authorization.
- Share dependency planning, affected-work evaluation, unit-value reuse and
  external quantity scaling across ordinary PV, risk and P&L.
- Offer both one-calendar-day model decay and opening-to-closing time roll.
  The selected initial UI choice is **one-calendar-day model decay**. Calendar,
  market-transport and lifecycle conventions must still be explicit.
- Full trading P&L reconciles actual value, cash and position changes. Paid cash
  leaves remaining PV; a due date does not establish payment. Scenario projections
  are not payment, fixing, exercise or settlement records.
- Run first; offer Save after presenting the result. Saving retains the exact
  backend result and its inputs without recalculation.
- UI and agent operations have the same contracts. No parallel legacy behavior,
  report-specific pricers, implicit fallback or untyped context property bag.

The initial executable policies are frozen absolute inputs with a forward clock,
retained dated conditional discounting and held calibrated annualized series IV.
Each request states the selected policy. Later transport conventions and event
projections below remain design requirements until independently qualified.

## 2. The design in one sentence

**Resolve facts once; derive immutable valuation layers; ask the existing
portfolio to value under each layer.**

There are two operations with different meanings:

| Operation | What it does | What it produces |
| --- | --- | --- |
| Select sources | Authorizes and resolves holdings, reference versions, observations, lifecycle facts and knowledge | A newly admitted base environment |
| Apply a scenario layer | Changes declared calculation inputs, including t₀, within that retained environment | An immutable child scenario |

Changing the historical workspace date or knowledge cutoff uses source selection.
Changing only t₀ uses a scenario layer. Both reach the same valuation boundary;
only source selection performs historical reads. No layer changes a database
session's effective time, advances knowledge or fetches a newer fixing.

```text
authorized source selection
  └─ retained holdings, reference versions, markets and lifecycle evidence
       └─ prepared base environment
            ├─ base PV
            ├─ clock child                 → the same portfolio PV
            ├─ price up / down children    → the same portfolio PV
            └─ selected combined children  → the same portfolio PV
                 └─ results, diagnostics and retained receipts
```

Sources from period A and t₀ in period B constitute a counterfactual valuation,
not a claim about what the portfolio was actually worth or knew at B. Using
observations later than t₀ is explicitly identified as hindsight. An ordinary
historical valuation retains its existing stricter admission rules.
Observation hindsight is separate from knowledge restatement: an earlier market
view using later-known corrections retains that explicit correction policy and
cutoff. It is not presented as information available contemporaneously at t₀.

## 3. Existing boundaries and the necessary changes

| Owner | Executable responsibility | Extension boundary |
| --- | --- | --- |
| `KnowledgeStore`, `MarketContextStore`, `PinnedContext`, retained frames | Certified historical selection, exact sources and current authority | Keep as the source-admission boundary; compose separately selected sources there |
| [ResolvedCalculationContext](../src/OpenEtrm.MarketData/Calculation/TemporalBindings.cs), `MarketSnapshot` | Ordinary historical mode couples effective time and valuation time; market cutoff cannot exceed valuation time | Keep these source certificates truthful; do not relax them to impersonate a hypothetical clock |
| [ConnectedScenario](../src/OpenEtrm.MarketData/Calculation/ConnectedScenarios.cs) | One immutable ancestry for atomic price, clock and IV edits | Further input families need declared consumers and policies |
| [GraphSession](../src/OpenEtrm.Shared/Calculation/GraphSession.cs) | Shared dependency plan, sparse offsets, affected closure and memo reuse | Extend its existing child mechanism only where a clock changes an operator's bound inputs |
| [PreparedValuationEnvironment](../src/OpenEtrm.MarketData/Trading/PreparedValuationEnvironment.cs), `UnitValuationContext`, `PricingInputs` | Source evidence stays fixed; calculation time and typed inputs are separate, declaration-fenced values | Separately dated source admission remains planned |
| [UnitPricingContract](../src/OpenEtrm.MarketData/Risk/RiskEngine.cs), [BookValuation](../src/OpenEtrm.MarketData/Trading/BookValuation.cs) | Price, time, discount-provider and IV declarations fence affected unit and Side reuse | State transitions require their own explicit qualified capability |
| `PortfolioRiskCalculation`, finite differences | Base once, shared sibling scenarios, exact aggregation and stability diagnostics | Accept the same typed scenario plan across supported input families |
| Retained risk jobs and report artifacts | Exact source manifests, worker fences, result receipts and current authorization | Retain the environment and layers in the existing execution envelope |

Option PV reads `PricingInputs.ValuationAt`, declared scenario IV and discount
factors resolved for that same clock. Its calibration remains anchored to the
source snapshot. Both the unit and Side reuse fences include these input
families; equal commodity prices cannot justify reusing a clock-sensitive value.

`GraphSession.Advance` explicitly represents a newly admitted base input frame.
It must retain that meaning. It is not a shortcut for a clock scenario. Likewise,
changing a frame's `At` would change native holdings selection and recorded-close
attestation, not merely the model clock.

## 4. Coordinates and ownership

Use the existing exact source manifests, rather than a new all-purpose context
table. A prepared environment references their certified identities and contains
the calculation contract. The following coordinates are distinct:

| Coordinate | Owner and meaning | Effect of a clock layer |
| --- | --- | --- |
| Holdings effective time | Native selection of trade heads, memberships, quantities and recorded lifecycle state | Unchanged |
| Knowledge cutoff and resolved prefixes | What was available to the installation, including corrections and exact definition publication | Unchanged |
| Market source | Exact close/session or working snapshot, observation cutoff, source matrix, correction policy and frozen/live input uses | Unchanged |
| Calculation t₀ | Instant at which models evaluate remaining time and admitted state transitions | Changed explicitly |
| Contract dates and clocks | Absolute delivery, expiry, fixing and payment coordinates, with retained calendar/time-zone rules | Unchanged; relative calculations use the selected policy |

There is one calculation instant, not an independently mutable collection of
pricing, expiry, discount and settlement clocks. Contract-specific business dates
are derived from that instant using their own retained clock rules. The display
zone is not a financial input. A policy can freeze recorded state or project a
specified transition; it cannot covertly give each subsystem a different t₀.

### Source composition

An advanced selection may combine holdings at H with market data at A. Each read
uses its own certified context. The first consumer requires one explicit requested
knowledge ceiling for the run; retain each source's actual certified cutoff and
committed prefix. Retained closes keep their exact recorded pins and selection
policy. Different source certificates do not imply permission to mix arbitrary
knowledge histories: admission proves visibility and complete causal dependency
closure under the run's selected contract.

Every resolved cutoff must be at or below the requested knowledge ceiling;
uncertified coverage refuses admission. Each source's own certificate bounds all
facts and dependencies attributed to that selection. A later source certificate
cannot authorize a dependency inside an earlier source selection. Closure covers
observations, exact definitions/references, activation bindings and lifecycle
dependencies under their actual owner contexts. Cross-source bindings themselves
require certified visibility under the run ceiling, rather than name-based joins.

Exact booked terms remain exact. Market data selection does not select a newer
trade definition, calendar or currency mapping. An older market period must
provide compatible exact inputs for the held contract. A later-created contract
does not acquire an older history through a matching code or nearby tenor.
Cross-period mapping requires an explicitly supported exact binding; otherwise
admission reports the missing coverage.

The first executable slice can use the same source context for holdings and
markets while independently shifting t₀. Separately dated holdings/market sources
use the same admission contract and are a subsequent slice, not a new scenario
engine. Independently chosen knowledge ceilings for individual sources remain
outside the first consumer.

### Source refresh and rebase

Selecting another close or a later knowledge cutoff creates a new base. An explicit
rebase may reapply a saved layer recipe only after re-admitting sources, exact
targets, model capabilities and coverage. It creates new scenario and run
identities. The earlier run can be linked as provenance; its prepared descendants
are not executable parents of the new base. Live corrections follow the existing
admitted-frame advance path, never mutate an in-flight calculation.

## 5. One scenario boundary, typed layers

`PreparedValuationEnvironment` replaces the former price-only prepared boundary
for all callers. `UnitValuationContext` remains the pricer-facing boundary, with
declaration-fenced typed input access. `SideValuationContext` receives those inputs
at construction; a reusable Side context cannot be rebound after evaluation.

The domain structure is:

```text
Prepared environment
  admitted source manifest + authorized portfolio projection
  versioned valuation/transport contract + complete dependency plan
  base valuation scenario

Valuation scenario
  base manifest identity + parent identity
  resolved typed layer + effective calculation instant
  immutable input views + scenario receipt

Resolved layer
  optional ClockAt(exact target instant, exact clock-roll policy)
  explicit typed input shifts with resolved targets, units and conventions
```

The absence of a clock edit inherits the parent's already explicit clock; an
absent input shift inherits that input. This is layer composition, not a missing
policy fallback. Every base has a complete admitted contract.

Input families supply typed requests, values, dependencies, target resolution and
validation. Implement the families with current consumers: commodity prices,
calculation time, dated discount inputs and calibrated option volatility. Reuse
their existing domain modules. Add further families only with an executable
consumer. Avoid `Dictionary<string, object>`, unrestricted callbacks, runtime
service lookup, or a generic plugin registry.

One valuation-scenario ancestry is authoritative. `ConnectedScenario` and any
rate/volatility evaluator are projections of it; they cannot have independently
editable parent stacks. A price-only child delegates to the existing curve shift
implementation. A clock-only child can share its entire price graph when no
price operator consumes time. The common receipt records both cases faithfully.

### Composition rules

1. Resolve a child's targets against its admitted parent, using exact keys and
   units. A clock edit names an absolute target, avoiding an ambiguous accumulated
   “one more day.” Named day-roll requests resolve to that target before execution.
2. Within one child, duplicate or overlapping edits of the same coordinate are
   refused. Existing parent-additive price shifts retain their meaning. Quote
   shocks and computed-output interventions stay distinct coordinates.
3. Apply the normalized edits, then evaluate derived inputs in dependency order.
   Clock-dependent discount/volatility transport uses the retained source anchor
   and the child's complete input state. Do not repeatedly roll a previously
   rounded scalar or silently recalibrate to an unchanged option settlement.
4. Preserve ordered ancestry. Parent-relative shifts and coordinate mappings need
   not commute. Only policies that establish equivalence may share numerical
   work; even equivalent effective states keep their own request lineage.
5. For a state-only frozen-input clock policy, A→B→C must give the same values as
   A→C with the same final edits. Their lineage differs. A path-dependent projection
   must declare its transition state and cannot assert that equivalence.
6. All FD samples are siblings of the selected parent. Success, cancellation or
   failure of one sample cannot change another, the parent or the workspace.

The initial shifts address retained source coordinates. A transformed-coordinate
shock needs its own explicit contract; it cannot be guessed from the layer order.
Parallel and bucket axes that overlap are not independent factors, and cannot
be double-counted as such in a derivative or attribution result.

## 6. Complete dependencies and reusable evaluation

The typed dependency set extends `UnitPricingContract`. Declarations
cover prices, time, discount providers, volatility/calibration, schedule inputs
and binding-owned lifecycle state when consumed. Source provenance and model,
measure, unit and numerical identities remain part of the valuation contract.

An instrument composes the declarations of its Sides and selected providers.
A discount provider declares the clock, dated curve and conventions it consumes;
an instrument need not duplicate its interpolation internals. Factor discovery
uses this same closure, projecting only supported shockable inputs. State facts
and frozen observations are dependencies, not automatically shockable factors.
Dependency presence establishes affected work, never an exposure amount.

All input reads, including the calculation clock, pass through the declaration
fence. A deliberately constant value can declare no inputs. An undeclared clock
read is an error, not permission to reuse a stale result. Audit direct snapshot
date reads in models, Side schedules, interval evaluators and lifecycle helpers;
each must explicitly use source evidence or the calculation clock.

### Preparation and dynamic demand

Prepare the bounded requested scenario set before pricing. Resolve the union of
required targets, clock horizons and possible schedule branches from retained
terms and policies. Declarations include every branch that can execute, not only
the branch taken at base. Admission must identify missing fixings, rate pillars,
option coverage and unsupported event crossings before publishing a complete
portfolio result.

The prepared set permits no new database or network read during PV. A request
outside its declared horizon requires new preparation against the same retained
source selection, not an implicit current lookup. An expanded plan is not
compatible merely because its curve names match.

### Graph mechanics

Keep `GraphSession<T>` and `DependencyPlan` as the shared dependency/memo kernel.
Do not encode a date as `ExactUnitPrice` or replace the curve graph with an untyped
graph of every domain object.

The current price operators remain source-anchored, so clock and IV children share
the unchanged price graph. No graph-kernel extension is needed for this slice.
For a future price operator that genuinely consumes the clock, extend the kernel's existing
child construction to accept explicitly changed bound inputs and the child
evaluator, alongside offsets. Its owner supplies the complete changed-node set
from the typed dependency plan. The same reverse closure and copy-on-write memo
rules invalidate affected values, while unchanged nodes inherit parent results.
Parent offsets still participate when an affected node is rebuilt. Changing a
clock-bound input is a child operation; admitting different source facts remains
`Advance`. Keep that distinction in both API names and tests.

Rate and volatility providers reuse the same dependency identities and invalidation
rules at the prepared-environment boundary. They need not be forced into the
commodity graph's homogeneous value type. Generalize a kernel mechanism only when
the first concrete provider needs it; do not build a second scheduler/cache per
input family or a new framework in `OpenEtrm.Shared`.

A child evaluator may change only the operators/bound inputs covered by its
declared changed-node roots. All other node semantics remain identical, and
evaluators consume immutable inputs without ambient clocks or state. Apply each
applicable offset exactly once after its node is evaluated, including inherited
offsets on invalidated nodes. The kernel extension must not become an unrestricted
evaluator swap that evades invalidation.

### Unit and Side reuse

A reusable unit key comprises complete unit economics, coverage, selected measure,
model/numerical policy, pricing bindings, relevant schedule/lifecycle state and
the resolved identities of every consumed input. Booked quantity is applied
afterward under the existing declared scaling capability.
Binding-owned state includes its normalization and scaling contract: an absolute
recorded cash allocation is not interchangeable with a unit amount. Share only
where those state contracts prove equivalent unit economics; never infer this
from instrument identity and clock alone.

Use dependency equality within a compatible admitted environment. Whole-scenario
identity alone is too broad for unaffected-work reuse; equal price scalars alone
are too weak for clock-, volatility- or state-sensitive work. Zero delta is not
evidence of independence. Two trades sharing a contract but differing in paid
state are not the same valuation scope.
Coincidentally equal outputs do not establish equivalent resolved dependencies.

Reused numerical values must be returned in the child's valuation context with
explicit reuse evidence, never as an old context's result masquerading as a new
one. This applies to Side contexts, companion measures, schedule inspection and
calibration/interval caches as well as headline PV. Each output's dependency
contract covers everything it returns. Current authorization is rechecked even
on a cache hit; initial reuse stays operation-local.

## 7. What moves when t₀ moves

The initial `frozen_absolute_inputs_forward_clock_v1` policy keeps absolute delivery/expiry/payment coordinates and
retained source market levels. It recomputes quantities and time-dependent model
inputs only under the explicitly selected valuation/lifecycle contract.
Here quantities means contractual schedule projections, not booked position
quantities. The opening holdings and their external quantities stay frozen.

| Input | Initial behavior | Explicit refusal or later extension |
| --- | --- | --- |
| Dated commodity forwards and basis | Keep exact delivery-contract prices; rebuild dependent calculations under declared inputs | No automatic front-month relabelling or delivery-date translation |
| Reference data and booked schedules | Keep exact versions and contractual dates; reevaluate clock-dependent eligibility | No selection of today's calendar, curve definition or contract terms |
| Published fixings | Keep admitted actual/final facts with their original uses | A forward mark cannot become an actual because t₀ crossed its fixing time |
| Discounting | Derive target-date factors from a retained dated curve under the selected roll convention | A scalar DF alone cannot support an arbitrary clock roll |
| Option volatility | Hold calibrated annualized IV at the exact series/strike under an explicit policy | Intrinsic fallback is not an IV input; no fabricated zero vega |
| Recorded cash and operational facts | Keep their source evidence and recognition policy | Clock movement does not record payment, delivery, exercise or assignment |

“Use an old market” does not automatically mean “move its maturity labels to
today.” Transporting a relative-tenor curve or smile to different contracts is
a separate policy, with exact mappings and coverage. It is not needed for the
first frozen-input theta consumer.

### Discount roll

For the frozen dated discount-function policy, with source anchor a,
target t and payment T, the provider computes:

```text
D_t(T) = D_a(T) / D_a(t)
```

Both evaluations use the same retained curve, complete required pillars,
interpolation, day-count and compounding conventions. The denominator must be
valid and nonzero. Currency, reference date, payment-date boundary and numerical
policy remain explicit. This is a selected conditional discount roll, not an
assertion that every discount convention has this form.

Recompute option discount-to-expiry and Side payment factors through this same
provider; do not implement a theta-only DF formula in the option or report.
The shared ratio uses exact rational division and a single half-even rounding to
18 decimal places (`exact_rational_half_even_18_v1`). Its receipt retains the
source numerator, anchor denominator, observations and numerical convention.
An explicitly undiscounted contract remains explicitly undiscounted. Overdue
unpaid cash follows its existing contractual valuation policy rather than a
negative-maturity interpolation.

Retain enough source pillars for both t and T. The present payment-specific
factor cache is not proof of that coverage. Rolling before the source anchor or
beyond the covered domain is refused unless a separately selected convention
actually supports it. No flat-rate extrapolation or discount factor of one is
inferred. Rate shocks, when admitted, transform their declared source-coordinate
inputs before deriving these factors.

### Volatility, vega and skew

The first executable volatility input can be the existing calibrated per-series
IV, promoted to a declared typed pricing input. This reuses current calibration;
it does not require creating a new surface table. Under held-IV price/theta
policies, calibrate once at the source, retain its evidence and use that IV under
child futures/clock inputs. Re-solving to the unchanged option mark would erase
or mix the sensitivity being measured.

For vega, shift this declared volatility input and call the same portfolio PV.
For a supported smile, skew is a selected vector of changes across exact nodes,
with retained weights, normalization and coordinate definition. A strike/expiry
node set can support a stated tilt without pretending to be a continuous surface.
Parallel vega, node vega and skew are different axes; no automatic orthogonality
or additive exposure allocation is assumed.

Retain whether the quantity is annualized volatility or total variance; absolute
strike or moneyness coordinates; fixed-source or recalibrated inputs; interpolation,
extrapolation and domain policies. A move of 0.01 in annualized decimal volatility
is one volatility percentage point. Backend output retains both native derivative
units and any explicitly selected per-point display conversion.

The stored analytical surface is currently not an option-pricing source. A later
executable surface provider must bind it explicitly through the same input
contract before a “surface shock” can affect PV. Recalibration from shocked market
quotes is another explicit dependency path with its own cost and failure policy;
it must not silently replace held-IV risk. Neither path requires an option branch
in the risk engine.

## 8. Theta and event crossings

Let E₀ be the opening admitted environment, V its selected portfolio PV, and
L(t) the selected frozen-input clock layer. Both modes use ordinary scenario PV:

| Mode | Target | Output |
| --- | --- | --- |
| One-calendar-day model decay — initial UI selection | Same local wall time on the next calendar date in the selected retained clock | `V(L(t₁)) − V(E₀)`, money for that specific day |
| Opening-to-closing time roll | Exact closing valuation instant | Repriced opening exposure at that instant, with separately identified projected lifecycle/cash effects |

The first output is a finite monetary change, with later-minus-earlier sign. It
is not automatically an annualized derivative, a next-business-day roll or a
central difference. A calendar day can span 23 or 25 hours. Resolve ambiguous or
nonexistent local times by the explicit clock contract; reject an unspecified
choice. Retain source instant, target instant, calendar date and actual elapsed
time. The endpoint mode takes its target from the selected closing context but
does not import that context's prices, knowledge or facts.

The initial frozen-state clock policy supports forward rolls within its
declared domain. Backward rolls, especially across an already-recorded fixing,
payment or exercise, require an explicit historical-state projection and otherwise
refuse. The broader environment design permits such counterfactual contracts;
it does not make frozen later state coherent at an earlier clock by itself.

### Separate model decay from lifecycle effects

Hold opening contractual holdings and source-known state. Within a region where
the selected model remains admissible, recompute remaining life and derived
discount/vol inputs at the new clock. Do not delete a holding simply because its
expiry or payment date was crossed.

At a fixing, expiry, exercise, delivery or payment transition, the instrument/
Side lifecycle provider must declare one of the following, with exact evidence:

- The existing known facts suffice and its selected transition contract supplies
  the correct remaining value and any separately identified event effects.
- A selected deterministic projection supplies hypothetical continuation and
  projected cash/positions, explicitly distinct from recorded facts.
- The crossing is unsupported or requires unavailable facts; return a structured
  unavailable result for that requested calculation.

The initial model-decay policy refuses crossings that require a state
transition; it does not silently shorten the step or substitute a one-sided
estimate. A supported already-due unpaid amount can remain unpaid at both clocks.
Projected payment on a due date is a separate assumption, never the paid-state
default. Actual exercise choices, assignment and unobserved fixings cannot be
invented by a roll policy.

For a qualified projected lifecycle, retain remaining PV, projected signed cash,
projected position transfers and their timing/valuation basis separately. A
combined projected wealth measure needs an explicit common valuation/accumulation
convention for interim cash; adding nominal past cash to a differently dated PV
is not an unqualified theta definition. Reuse native lifecycle composition and
accounting identities, not a report-owned event simulator.

True futures PV and variation-margin transitions require their own supported
lifecycle capability. Execution P&L cannot fill that gap. An unavailable member
prevents a complete book theta; any supported subset is explicitly partial and
is never labelled the complete book.

### P&L reconciliation

Actual endpoint PV and actual cash remain the full trading P&L target. Time,
price, volatility, fixing, position, model/reference and knowledge effects form
an explicitly ordered explanation, with the residual retained.

One-day theta is a useful diagnostic even for a Friday-to-Monday report. It must
not be silently multiplied by three or inserted as the entire period time effect.
The UI shows this selected diagnostic beside the period reconciliation.
Selecting endpoint time roll supplies a period-aligned time explanation when its
coverage is complete. Until then, unexplained time remains in the residual.
Any scaled-theta approximation would require a separate selected attribution
policy. Mode, interval and coverage stay visible beside the number.

Do not add projected cash to actual economic cash or count a lifecycle movement
both in the time bridge and again in the cash/position bridge. Each bridge leg
retains its input/output state and measure; the chosen attribution order owns it
once. This spec does not approve a hidden market/time/volatility ordering for a
complete P&L waterfall.

## 9. Shared risk mechanics

Factor discovery asks the prepared portfolio for its declared shockable input
closure. Canonical exact factor identity deduplicates aliases across instruments
and market partitions. No list of supported instrument subclasses appears in
risk or the P&L report.

The existing finite-difference sampler supplies sibling scenarios. For a numeric
axis with explicit bump h, base/up/down give both central delta and gamma:

```text
delta = (V(+) − V(−)) / (2h)
gamma = (V(+) − 2V(0) + V(−)) / h²
```

Gamma does not need an extra option-only evaluation. Existing half/nominal/double
checks evaluate numerical agreement; they do not prove differentiability. Mixed
price/volatility or other supported derivatives use the same selected-corner
sampler, with dimensions validated by the typed axis contract. Avoid a dense
Hessian unless explicitly requested and admitted. A clock difference uses the
same scenario evaluator but its own temporal sampling contract; calendar days
are not forced into price-unit bumps or meaningless half-calendar-day checks.

Step sizes, domains, units, normalization, tolerances and model numerical policy
are retained inputs. Invalid negative volatility, incomplete interpolation or a
failed sample refuses the requested derivative. Do not clamp, switch models,
substitute an intrinsic value or publish a successful zero. Full-repricing stress
results and stable local Greeks remain distinct artifacts.

Measure, exact currency and compatible scenario/axis semantics control
aggregation. An absent factor in one admitted partition is explicitly unaffected;
a missing capability or missing input is unavailable. A common source factor is
shocked once even when many portfolios consume it. Shared units are priced once
per affected scenario and externally scaled for their holdings.

`SensitivityInput` and `ScenarioFactorUnit` extend the price-specific contracts
at the common finite-difference boundary. A typed axis retains its input family, exact coordinates and
dimensions; first and mixed derivatives retain the corresponding denominator
units. Use the existing shared unit mechanisms. Do not represent volatility or
time as a fake commodity price, or create separate delta/vega report algorithms
to avoid changing the common derivative contract.

## 10. Execution, receipts and UI

The initial P&L consumer is a bounded synchronous authorized operation; its price
and clock samples use one prepared portfolio and the ordinary scenario reducer.
Both endpoints and all samples pass one aggregate work admission before PV.
There is no theta service or scenario database. The existing price-only worker
requests remain price-only. Extending their retained envelope to non-price layers
requires its own worker equality and recovery qualification; it is not claimed
by a synchronous result or by this design.

An environment/result identity includes source certificates and exact pins,
holdings/coverage/state, selected valuation and transport policies, ordered layer
definitions and resolved targets, calculation instant/clock rules, models and
numerical versions. Retain source-anchor identity separately from child scenario
identity. Changed input contents under the same identity are a conflict. Numerical
reuse never changes the identity of the requested result.

Readers, workers, saves and exports reauthorize all contributing sources and book
scope under current permissions. A retained historical context is not a grant.
Read the saved result as recorded; recomputation is a new run. Use the existing
request-id/first-outcome and same-fact idempotency contracts for durable writes.
No browser-supplied PV or artifact reconstruction is accepted on Save.

### Presentation contract

Keep historical context controls for choosing the source environment. The planned general
valuation/risk workspace adds a compact scenario control with separately labelled
**Market data**, **Positions as of**, **Known as of** and **Value at**. Ordinary
use can show their shared selection once; differing coordinates must be visible.
Advanced details expose exact close/session, corrected-versus-recorded selection,
clock convention and layer ancestry without replacing them with a misleading
single “As of” date.

The scenario editor supports the same admitted layer contract as agent requests.
It offers available factors and policies from the backend, displays the resolved
target before Run, and explains missing coverage. The one-day theta choice is
preselected as requested, but does not fill missing discount, volatility,
calendar or event policies. These come from the selected explicit valuation
contract or need selection.

Applying a layer changes the valuation view, not the global historical selection
or booking state. Each tab/run has its own immutable context and request generation.
Cancel/discard late responses on source, scope, layer or identity changes,
including A→B→A. Results visibly retain their source and calculation coordinates.
A scenario result cannot be passed off as an official close or actual settlement.

Risk presents base and scenario values, units, coverage, derivative stability and
work receipts. P&L retains its Run → results → optional Save flow, separate
currency totals, reconciliation and position drill-down. Controls and graphics
only present managed results; no financial date, Greek or cash math in TypeScript.

## 11. Examples and independent acceptance checks

All numerical examples here are controlled test inputs. They are not imported
market observations.

| Case | Required result |
| --- | --- |
| Source close Friday; t₀ Saturday | Same retained prices, terms, knowledge and holdings; eligible models have one calendar day's changed time inputs |
| Source close Friday; endpoint t₀ Monday | Same opening inputs; a period time effect, never three times Friday's one-day theta by default |
| Historical markets with current holdings | Separate certified source selections, exact compatible contracts; absent old history refuses |
| Markets observed after t₀ | Explicit counterfactual/hindsight mode; ordinary historical admission still refuses |
| Same source, one future cash amount | Changed discount inputs invalidate PV even when commodity dependencies are empty |
| Several holdings of one option and paid-state variants | Shared eligible option unit work; distinct cash-state units and traceable quantity application |
| Price and IV shocks together | One common child environment, with both declared changes visible to the option PV |
| A new fixed-income or custom instrument | Complete declarations and supported policies suffice; no risk-engine registration by instrument type |

Tests must be written to fail before each implementation step. Use independent
oracles and isolated disposable PostgreSQL 18 for native integration.

1. **Source immutability and knowledge.** Hash all source pins before/after layers;
   prove no source read occurs during PV. Publish a later correction and fixing:
   retained child results stay unchanged, explicit new admission sees only what
   its cutoff allows. No fabricated later source snapshot or close identity.
2. **Independent coordinates.** Vary holdings time, source market and calculation
   time separately. Verify exact native bound contexts, retained definitions,
   scope, causal closure and read authorization. Matching names cannot join
   incompatible definitions. Refuse unavailable retained history.
3. **Clock and discounts.** A future receivable of 100 with retained
   `D_a(T)=0.95` and `D_a(t)=0.99` has rolled PV `100×0.95/0.99` under the selected
   ratio policy, not 95. Verify both input lineage and the existing numerical
   policy, no unintended scalar reuse, and explicit out-of-domain refusal.
4. **Typed invalidation.** A deliberately independent test pricer consumes
   price, t₀ and volatility with known polynomial outputs. Change each input
   alone and jointly; assert PV, derivatives, actual evaluated/reused units and
   unchanged unrelated graph work. Undeclared reads fail. A constant pricer reuses.
5. **Graph inheritance.** Under a stressed parent, change a clock-bound operator;
   its ancestors' applicable offsets still affect rebuilt values. Reverse closure
   includes every dependent, siblings remain independent, cancellation does not
   poison parent caches, and repeated state-only rolls match the direct target.
6. **Options and volatility.** Test actual supported model repricing at changed
   time/DF with retained IV, against an independent numerical fixture. Confirm no
   source recalibration under held-IV delta/theta; test vega units, node tilts,
   surface-coordinate overlap and intrinsic-fallback refusal.
7. **Calendars and events.** Weekends, DST gaps/overlaps, exact fixing/expiry/
   payment boundaries, backwards rolls and expired coverage. Distinguish due
   unpaid, actual paid and projected payment. Unsupported crossings never become
   zero PV, assumed exercise or a fabricated actual fixing.
8. **P&L ownership.** Reconcile actual PV plus cash/position changes independently
   of the explanation. Prove one-day theta is not mislabelled as multiday P&L;
   projected cash is not actual cash; bridge effects occur once and residuals
   reconcile. Missing capability cannot disappear from a portfolio total.
9. **Runtime reuse.** Many holdings, few unique unit/state scopes, mixed inputs.
   Quantity changes scale values without additional unit PVs. Paid-state, model,
   clock and coverage changes invalidate exactly their affected scopes, including
   schedule detail and companion measures. No cross-authority cache leakage.
10. **UI, jobs and saved results.** Direct/worker equality, exact envelope replay,
    source/layer/profile changes during requests, delayed A→B→A responses, parallel
    tabs, cancellation, authority revocation, save retry and exact saved output
    without pricing. All source and calculation coordinates survive reload.

Benchmark only after correctness: state environment, holdings, unique contracts
and lifecycle scopes, source partitions, scenarios, affected/evaluated nodes,
calibrations, actual PV calls, reuse, quantity applications, memory and cold/warm
time. Trade count alone is not a performance claim.

## 12. Delivery sequence and qualification boundary

The initial slice has passed the managed core and rate checks, the web suite,
and isolated native currency-cash/P&L integration. The latter covers continuing
options with both clock targets, new-and-paid positions, exact-result saving,
process restart, idempotent retry, lost-unsaved recovery, forged native records,
cross-catalogue tickets and current reference-access revocation. These are
bounded correctness results, not general lifecycle or performance qualification.
Saved evidence includes the engine fingerprint, selected valuation policy, exact
endpoint holdings and discount inputs, unit declarations and Greek estimates,
units, tolerances and sample identities.

1. **Common input contract.** Introduce the environment/scenario identity and
   typed dependencies at the existing prepared valuation boundary. Migrate all
   current pricers and Side/provider reads; base and price-only behavior must stay
   numerically identical. Incomplete consumers refuse new capabilities explicitly.
2. **Frozen-market clock layer.** Implement the shared clock input, dated discount
   roll, held per-series IV and bounded event admission. Qualify both requested
   theta targets through existing portfolio scenario evaluation. Preserve source
   certificates and ordinary historical semantics.
3. **Other sensitivities and source composition.** Expose calibrated-IV axes and
   supported rate axes through existing finite differences. Add explicit separately
   dated holdings/market admission. Executable surface/relative-tenor transport
   requires its own concrete pricing consumer and tests.
4. **Retained execution and presentation.** Extend the existing jobs/receipts,
   shared UI/agent operations, context display and exact-result saving. Integrate
   qualified time effects with the P&L bridge and visibly report coverage.
5. **Independent qualification.** Review temporal resolution, numerical policy,
   dependency completeness, reuse, authorization and async boundaries. Deploy the
   complete report to the isolated development environment only after its native
   and browser checks pass.

These are dependency-ordered implementation slices, not permission to claim a
complete P&L report after the first one. No tables, compatibility adapter or
default policy are justified solely by this design. Version the changed retained
execution contract coherently with its worker; do not execute queued old manifests
under new semantics. Saved completed results retain their original evidence.

The initial policies above are explicit executable choices, not fallbacks.
Rate-shock policies, executable surface transport, projected lifecycle/cash
conventions, separately dated source composition and a complete non-price P&L
attribution ordering remain later work. The architecture can support further
qualified choices at the same shared input boundary.
