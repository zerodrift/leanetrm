# Curve evaluation

`CurveBase<TQuery,TValue,TSelection>` provides one nonvirtual `Lookup(query)`
entry point. Each implementation must resolve and validate a typed query, declare
its selected inputs, and evaluate that same immutable selection. `LookupMany`
preserves order and duplicate requests through that entry point. The result
contains the typed value, exact curve key, selected policy and required inputs.
Price values carry units, scenario/context and observation lineage; discount
factors carry currency, snapshot and contributing rate pillars.

Concrete versioned policy records retain interpolation, boundaries and analytical
implementation. Price settings also retain axis and maximum span. Rate policy
retains construction, compounding, day count and date basis. There is no implicit
interpolation, extrapolation, alternate evaluator or missing-value fallback.
A stored recipe or analytical model implements evaluation behind this contract;
a fitter produces qualified curve state, rather than exposing a competing lookup.

Curves consume resolved immutable inputs. Authorization, exact-version selection
and historical visibility remain at the operation boundary. The current concrete
lookup families are prices and discount factors. The volatility fitter is a
separate producer; a selectable fitted-volatility lookup/provider is not admitted
by this change, and no expiry interpolation or volatility extrapolation is added.

`AssetCurve` evaluates typed prices over native curve members or published recipe
outputs. Exact contract identity, a date inside a delivery period, a whole
delivery interval and a point sample date are distinct requests. Contract queries
preserve the contract key;
point queries introduce no market contract or delivery period. `AssetCurveValue`
retains exact rational price units, observation lineage, scenario and resolved
context. A prepared batch binds an exact scenario with `WithScenario` and uses the same
`Lookup` method against that scenario. It shares its connected plan across immutable scenarios
and preserves duplicate requests and their order. The native factory requires a
complete input snapshot for its declared curve members; it does not fetch marks.

Lookup settings are explicit: method, sample axis, maximum bracket span, rejecting
left/right boundaries and implementation version. Methods are exact point,
containing delivery, previous point, next point and linear price on actual days.
The axis is delivery start, delivery end or an explicitly supplied sample date.
A delivery interval is half-open. Gaps and multiple owners reject. Point lookup
requires unique sample dates; historical rows must first be reduced explicitly.
Previous/next and linear methods require two bounding points within the selected
span. They never extend the end points. Monthly delivery prices retain bucket
semantics unless a point-date policy is explicitly selected.

`AssetDeliveryPeriod` selects one source interval covering the entire requested
half-open period under the explicit containing-delivery policy. A gap, overlap or
request spanning several source intervals refuses; the query does not average
across them. This supports mixed daily/monthly price grids without treating a
point sample as a delivery price.

Linear interpolation compiles integer day weights into the existing exact
connected weighted mean. Price evaluation, upstream shocks, computed-output
shocks and lineage use the same graph as ordinary curves. Intermediate prices
are rational; presentation rounding does not become a new observation.
`PricingInputs.ExactPrice` gives generic instrument pricers the same rational
price under their declared dependency fence.

## Saved custom curves

The custom-curve framework is the construction and publication interface for
these curves. The `lookup` operator consumes a typed panel and an explicit date
sequence. It uses the same lookup planner as `AssetCurve`; it introduces no
separate evaluator. Native sampled panels and exact published-version outputs
can feed it. Policies are persisted in the versioned node parameters and retained
in calculation manifests. Existing published definitions retain their policies,
engine and canonical evidence.

The workbench exposes each setting without inferred defaults. Its existing
preview, formula, save, publish, version and activation operations apply.
`delivery_range` declares one to 366 target days with an exclusive end date.
`daily_delivery_v1` explicitly turns each requested lookup into a daily delivery
price. The resulting real delivery intervals can be bound by the existing exact
daily custom-projection contract to indexed swaps, portfolio valuation and risk.
A price-free topology pass propagates demanded output intervals back through
lookup brackets and published ancestors before loading observations or checking
live eligibility. Expired points belonging only to actual resets are excluded;
a missing or expired endpoint still required by a projected reset rejects. Native source samples and lookup provenance remain bounded by the
existing 50,000-reference preparation limit.

Migration 0114 admits lookup nodes and validates policies and typed ports at
publication, including publication attempted outside the application. It extends
the existing immutable recipe tables and guards without changing old graphs.
Read, write, publication and worker permissions use the existing operations.

D332 / migration 0201 adds the persisted
`containing_delivery_interval_v1` binding and `curve_price_lookup` schedule
operation. Supported indexed swaps and the physical gas ticket retain the exact
published curve, requested interval and selected source evidence. Their normal
connected valuation and retained-worker paths consume those relationships; see
[the physical gas example](indexed-physical-gas-example.md). This future-delivery
consumer does not add indexed-purchase cash settlement or broader profile rules.

D350 adds independent daily indexed consideration to future gas compositions.
Physical and payment forecasts use separately selected curve bindings through
the same lookup/source machinery. Exact daily index contracts and fixing
definitions, an explicit spread, physical quantity allocations and next-month
UTC payment terms feed ordinary Instrument/Side PV and retained scenario risk.
Multi-month booking retains those native periods without inferred monthly
allocation of whole-term fixed cash. The selected measure remains contractual
net MTM; started-delivery indexed cash, paid/unpaid cash state and payment
execution are outside this profile.

## Rate curves

`RatesCurve` evaluates a typed `DiscountFactor` by maturity, retaining currency,
reference date, snapshot, numerical/interpolation policies and contributing
pillars. Payment discounting delegates to it. It reuses the existing direct zero
rate construction (`actual_365_fixed`, `simple`) and selected log-discount-factor
interpolation. Exact pillars bypass interpolation. Unbracketed dates reject,
including the reference date when it has no stored pillar; no factor of one is
inferred. The supported maximum bracket span remains 3,660 days.

Rate construction conventions remain on the exact rate definition. The explicit
interpolation policy remains on the existing versioned currency-discount mapping;
this increment does not migrate that persisted contract. Asset recipe operators
remain price-valued. Rate bootstrap instruments, additional conventions, rate
scenario coordinates, rate-valued custom recipes and rate interval/profile
evaluation remain separate work. Whole-delivery price lookup is supported as
described above.

## Verification

Managed curve checks use independent endpoint/fraction and discount-factor
oracles, duplicate batch queries, gap/boundary rejection and connected shocks.
`tools/check_curve_lookup.py`, included in `--connected-portfolio-check`, checks
saved published-parent interpolation, formula roundtrip, policy publication
guards, per-reset exact booked PV, endpoint deltas, partly-fixed lookup demand
pruning through published parents and retained worker equality.
The custom-curve browser checks exercise blank policy choices and editing the
lookup fields and typed input connections through backend metadata.
