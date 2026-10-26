# Market data, contract histories and explicit risk behavior

Status: market-data requirements and acceptance criteria. Broader behavior remains
planned; these contracts alone do not establish implementation.
The bounded local implementation and remaining gates are described in
[implementation.md](implementation.md).

The first concrete entity/key/specification proposal and worked synthetic example
are in [market-model.md](market-model.md). Its implementation choices remain for
review; they do not weaken the agreed explicit-behavior rules here.

## Never assume; never silently default

**NEVER ASSUME user intent. NEVER SILENTLY GO TO DEFAULTS.**

Financial behavior must be explicit in the selected versioned definition, policy
and resolved context. Missing, ambiguous, unsupported or conflicting behavior
raises a structured exception/error; it must not produce a plausible successful
result. This applies equally to UI, imports, API and agent operations.

Require explicit mark interpretation, units/currency, source/revision selection,
dependencies, delivery mappings, sampling/roll rules, interpolation/extrapolation,
staleness/missing-data policy, settlement/fixing rules and numerical conventions.
An explicitly selected policy can define behavior; an absent choice cannot be
replaced by an undocumented default, inferred relationship, last-known price,
zero, alternate source or a different pricing mode. Resolve every applied choice
into the retained manifest. Labels, correlations and a language model's guess
are not declarations of economic intent.

## All-in inputs and dependency-based prices

These are distinct, explicitly selected pricing definitions:

```text
AllInLocationPrice = marked all-in factor
SpreadLocationPrice = declared base curve + marked spread factor
```

The all-in price node has no inferred dependency on a base curve. A base-curve
shock therefore does not change that node. It can respond to a shock of its own
all-in factor. Other dependencies explicitly consumed elsewhere by an instrument
or its pricer, such as discounting or FX, remain applicable.

The spread-based definition consumes its declared base and spread. A shock of
either input rebuilds the dependent price according to that declared recipe.
Never calculate an implied spread from an all-in mark to manufacture exposure.
Never infer dependencies from economic similarity, mark levels or correlation.

The definition and incoming mark type must agree. An all-in mark sent to a
spread-only input is rejected; receiving it must not silently remove recipe edges
or change a pricing mode. Supplying both interpretations without an explicit
supported selection rule is an error. A deliberate mode/dependency change creates
a retained definition version and does not reinterpret prior runs.

## Deterministic shocks and black-box PV

For each requested factor/scenario, apply the declared bump once to the immutable
market context, rebuild affected curve dependencies and invoke the affected
instrument/side PV implementations. Retain base, up and down PVs. Finite
differences use those actual results and the explicit bump convention.

The [implemented risk engine](risk-engine.md) also composes immutable child
scenarios: each signed absolute quote shift applies to its parent, and discarding
a child leaves its parent and siblings intact. Risk under stress evaluates the
stressed base and its up/down children. Immediate futures calculations and durable
jobs use the same retained financial context; queued work keeps its original
effective date, knowledge cutoff and exact observations when the UI date changes.
Rates, volatility and arbitrary pricing-parameter shocks remain unsupported.

**Identical pinned inputs, scenario, model versions and numerical policy must
produce identical outputs.** Ordinary shock repricing has no random outcome,
hidden sampling, wall clock, current-data lookup or execution-order-dependent
reduction. Solver/model modes must meet their declared reproducibility contract
before support is enabled.

Dependency edges identify potentially affected work. They do not assign an
exposure amount or replace PV calculations with risk allocated from spread size,
relative mark values, correlations or assumed hedge ratios. A declared dependency
can produce zero delta at a particular context; it still remains a dependency.
An explicit formula within a supported curve recipe is an economic transformation,
not permission to infer a risk-allocation model.

In the implemented shared valuation contract, instruments own unit values and
books apply each trade binding's external quantity. Reuse requires identical
economic inputs, model versions and compatible full context; market linearity
does not establish reuse eligibility.
Future analytical accelerators must preserve the authoritative repricing result
under their explicitly supported domain; they cannot invent a dependency or risk
attribution. Future VaR models may describe uncertain markets, while data selection
and execution still follow explicit reproducible contracts.

## Dated contracts, curves and historical alignment

Each dated market contract has its own retained identity, terms, units, expiration
and delivery period/profile. Observations attach to that identity and retain mark
kind, source, status and revision. A price is selected through context, not stored
as one mutable current-price attribute on the contract.

A curve snapshot is a cross-section of selected contracts/prices at one market
context. Its display can plot each contract's expiry against that price. Selecting
a constituent opens the history of that same dated identity. Its history axis is
observation/mark time, not contract expiry. Keep delivery periods and expiry
distinct in metadata even when a chart uses expiry as its axis.

A history read names its revision policy (D230). `exact_snapshot_pins_v1` shows
each retained snapshot's exact pins, so several samples can share a business
instant. `as_known_at_cutoff_v1` shows, per instant, the latest revision recorded
at or before the read's knowledge cutoff: the selected context's instant, or
current knowledge for working data, which the response states. Identical
samples at one instant collapse (D229); different ones are refused naming the
instant. Each point carries its observation identities and certified `knownAt`.

Histories attach to stable market factor identities and can be assembled into an
aligned sample set: factors are columns and explicitly sampled intervals are
rows. Retain sampling/calendar/cutoff rules, source/quality information, missing
and stale observations, transformation/units and the selected knowledge context.
Covariance and autocorrelation are calculations over those identified histories
and explicit estimation windows, not permanent intrinsic fields of a contract.
VaR estimators and calibration policies remain later scope.

A rolling reference selects dated contracts using an explicit versioned rule.
Retain the selected underlying identity at every observation. Raw rolling prices,
adjusted analytical histories and actual dated-contract settlements are different
artifacts. Adjustment must not overwrite original observations or silently turn
a roll gap into a market shock. Roll and adjustment algorithms remain open.

## Curve ownership, basis and deep composition

Curve owns common constituent specifications; dated MarketContracts retain their
identities and dated terms. Commodity, yield and composite/algorithmic curves
share a curve abstraction with explicitly typed specifications. Internal factor,
point, version and build records need not become separate public business objects.

Basis curves carry explicit spread conventions, base references, units and delivery
mappings. Composite outright curves declare how base and basis values combine.
One reusable source-binding structure supports multiple dependency layers, shared
inputs and forward curves consuming futures curves. Bind exact versions and source
constituents, validate dimensions/profiles/coverage and reject cycles.

Pricing dependencies and settlement targets remain separate. A forward keeps its
own index/formula, fixing and payment terms when consuming a futures input. Its
source-expiry rule must explicitly freeze an eligible final observation, perform
a specified transition, or end eligibility. Never silently roll or substitute a
current source. Missing/unsupported inputs produce pending/error under the selected
policy. The five-layer example and proposed storage links are in
[market-model.md](market-model.md).

## Mixed daily/monthly constituents

Mixed daily/monthly constituents are an agreed requirement. A curve may contain
daily delivery contracts in an explicitly selected near period and monthly
contracts thereafter. Contract delivery intervals, not a single curve-wide
frequency, identify coverage. A versioned resolution/membership rule states which
period is daily and when that selection changes; the word "prompt" alone is not
a rule. No new daily/monthly curve classes or reference catalogs are required.

The basis definition identifies its actual benchmark. Daily locational basis
against a daily benchmark consumes that exact day; BALMO basis against a BALMO
benchmark consumes the same remaining period. A monthly future's frozen final
cannot silently replace either. A directly quoted BALMO contract and a strip of
daily contracts are distinct inputs; one BALMO mark does not determine a daily
shape. Different start dates retain different dated BALMO identities.

The [mixed daily/monthly gas acceptance case](examples/mixed-resolution-gas.md)
now executes in the managed checks with equal-period daily, monthly and BALMO
bindings. Eight saved market dates span mid-month through the following month-end.
The test includes a separately available expired monthly final and proves it does
not fill a missing BALMO benchmark. The forward portion now has an isolated
database/API/browser fixture with exact per-date selections and explicit history
gaps. That fixture is not a settlement/publication resolver. Separately supported
consumers now include custom-curve exact means, interval block shaping and
actual/projected index-fixing valuation; see [implementation](implementation.md).
Those named contracts do not enable arbitrary unequal-period mappings or infer
fixing/weighting rules for this mixed-resolution definition.

## Acceptance before market-data implementation is enabled

1. Show a curve cross-section and selected dated-contract history from the same
   explicit observation/context contract; retain revisions and stable identities.
2. Prove a base shock leaves an all-in node unchanged and rebuilds an explicitly
   base-plus-spread node. Separately prove sensitivity to the all-in node's own
   factor and to any other declared instrument inputs.
3. Reject missing interpretation, conflicting mark kinds, missing dependencies,
   unsupported/missing policies and attempted silent fallback.
4. Reprice affected instruments in base/up/down scenarios. Obtain sensitivities
   from those PVs without allocating risk from mark values or spread proportions.
5. Repeat identical pinned runs with different enumeration/worker scheduling and
   preserve the documented deterministic output/reduction behavior.
6. Keep historical sampling, rolling-series construction and future VaR policy
   explicit; reject unqualified combinations instead of filling gaps silently.
7. Validate common curve specifications against every constituent and retain exact
   reference links. Demonstrate deep mixed futures/forward dependencies with
   explicit delivery mappings, independent settlement terms and cycle rejection.
8. Replay source expiry with pinned final observations/rules; reject unavailable
   final facts or unconfigured transitions and preserve earlier captured builds.
