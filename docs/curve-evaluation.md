# Curve evaluation

`CurveBase<TRequest,TValue>` defines query validation, required inputs, coverage,
single evaluation and bounded batch evaluation. Curves consume resolved immutable
inputs. Authorization, exact-version selection and historical visibility remain
at the existing operation boundary.

`AssetCurve` evaluates typed prices over native curve members or published recipe
outputs. Exact contract identity, a date inside a delivery period, and a point
sample date are distinct requests. Contract queries preserve the contract key;
point queries introduce no market contract or delivery period. `AssetCurveValue`
retains exact rational price units, observation lineage, scenario and resolved
context. A prepared batch shares its connected plan across immutable scenarios
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
scenario coordinates, rate-valued custom recipes and interval/profile lookup are
separate work.

## Verification

Managed curve checks use independent endpoint/fraction and discount-factor
oracles, duplicate batch queries, gap/boundary rejection and connected shocks.
`tools/check_curve_lookup.py`, included in `--connected-portfolio-check`, checks
saved published-parent interpolation, formula roundtrip, policy publication
guards, per-reset exact booked PV, endpoint deltas, partly-fixed lookup demand
pruning through published parents and retained worker equality.
The custom-curve browser checks exercise blank policy choices and editing the
lookup fields and typed input connections through backend metadata.
