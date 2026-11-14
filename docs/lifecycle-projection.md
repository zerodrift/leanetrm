# Economic lifecycle projection

D394's pure projection path retains the certified opening snapshot, its market
cutoff, knowledge cutoff and immutable connected graph. The calculation clock is
separate. Projection never appends observations, accepted settlements, payments,
trade revisions or native lifecycle events.

`ILifecycleProjection` declares its native economic owner, complete transition and
possible residual price dependencies, opening ordinary valuation portfolios and
exact events. An opening unpaid obligation remains an ordinary holding. New cash
contributions belong to the event that creates them; earlier contributions cannot
be rewritten or removed. Independent native holdings use typed accounting owners,
without creating artificial trades.

The source adapter supplies a retained selected-session version, calendar and
complete resolved close schedule. The engine chooses the first configured close
strictly after opening, shared by every market partition. An absent next close is
unavailable. Historical and Monte Carlo scenario rows share that one horizon.

Every policy field is explicit. Supported paths are `shock_then_hold_native_quotes_v1`
and `elapsed_linear_native_quotes_18_to_even_v1`. The latter scales each terminal
native primitive move by elapsed UTC microseconds and rounds that native quote
offset once to 18 fractional places, half-even. Each event and horizon scenario
branches from the immutable opening graph; moves are never accumulated through
successive interpolation steps. Connected derived curves rebuild from their
affected primitives. Captured rates and calibrated volatility remain fixed under
`captured_rates_and_volatility_held_v1`; this profile supplies no rate or volatility
shock axis.

An actual fixing requires its exact selected final frozen source evidence. Missing
opening fixings cannot become forecasts. Projected fixings have separate types and
freeze only inside their own path after their event. A future's projected final
settlement freezes its explicitly selected native primitive for subsequent
dependent curves; ordinary daily margin preserves continuing exposure. Conflicting
final instants refuse. Projected final prices never become actual
`FuturesSettlementFact` instances.

Mark and official settlement sources remain separate. A futures projection selects
its scenario-event market explicitly in addition to the adopted actual settlement
policy. Matching quotation units cannot infer that choice. Global event ordering
respects declared prerequisite identities, including at the same instant. A
scheduled time before opening does not prove that a prerequisite occurred; the
opening adapter must retain its completed event evidence.

Residual positions use ordinary `ValuationPortfolio`, Instrument/Side PV and the
same immutable connected scenarios. Projection-marked state cannot enter an actual
valuation environment. A future margin event transfers its rounded amount to a
separate cash contribution and resets its path-local basis. Final cash settlement
removes its future exposure while unpaid cash remains a cash instrument. Physical
final settlement requires a qualified delivery handoff rather than the cash-final
adapter.

Cash disposition selects projected payment at the contractual due instant or
continued outstanding cash. Future unpaid contributions use ordinary
`TimedCashPayment` PV. Period cash is either nominal or accumulated to the horizon
using the exact ratio of captured source discount factors. Currency curves require
an explicit captured term-structure capability, exact-instant timing and retained
identity; a scalar payment factor cannot define accumulation. One currency uses
one selected accumulation curve version across all market partitions. There is no
implicit currency conversion.

Absolute economic P&L is horizon residual PV plus horizon-valued period cash minus
opening PV. The no-shock result retains time and carry. Historical and Monte Carlo
risk reduce complete economic wealth observations with the existing exact signed
empirical quantile/tail reducer. A separately labelled market-only diagnostic uses
no-shock horizon wealth as its comparison value. It never replaces the economic
measure or removes deterministic drift from it. Gain-only tails may have negative
VaR or expected shortfall. A failing holding or path fails the complete run; rows
are never dropped or resampled. Event-aware parametric risk is unavailable until
separately qualified.

Preparation exposes the entire transition/residual factor union and connected
manifest before history capture. Retained history must cover those exact native
coordinates and the certified opening forecast. Monte Carlo uses the existing
counter-addressed generator under its explicitly selected economic valuation
profile. Complete owner/currency scope and all path indices enter reduction.

The managed checks independently cover derived-curve interpolation and fixing
freeze, futures margin cash plus residual conservation, final future freezing for
a dependent forward, nominal/accumulated and future unpaid cash, native owners,
signed empirical tails, deterministic carry, full Monte Carlo paths and refusal
of failed paths. Native source resolution, option/swap and physical delivery
adapters, retained worker/HTTP/UI integration and browser qualification remain
separate delivery gates until their own executable checks pass.
