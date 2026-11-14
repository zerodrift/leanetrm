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

Every handler retains the exact source snapshot and admits its event schedule for
the common horizon before history is read. A component prepared under another
source clock or input set cannot be reused. Potential physical branches require
their qualified delivery policies during this preparation, even when a particular
sample later abandons an option and never creates that physical future.

`CashLifecycleProjection` retains actual source-known payments as a separate
immutable collection. Those payments enter period cash at their effective
instants under both hypothetical disposition choices. A projected scheduled
payment reserves every already-known later actual allocation, so it cannot pay
the same balance twice. Opening overdue cash is never paid merely because its
due date passed. Projected discharge state has a separate type and cannot enter
an actual valuation operation. Opposite-direction refunds require their own
qualified contractual policy; the scheduled-payment profile does not invent one.

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

`LifecycleCurrencyDiscounting` prepares the complete native rate term structure
once and resolves event-created due keys without further source reads. It uses
the existing exact-instant interpolation and conditional ratio conventions at
the explicit horizon clock. Factor evidence retains the original source snapshot,
the new calculation instant, exact curve version, full tenor geometry and the
required native pillars. Equal rates do not erase required pillars. The source-only
discount contract remains separate; it does not silently acquire a clock roll.

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
of failed paths. Native source resolution and physical delivery adapters,
retained worker/HTTP/UI integration and browser qualification remain
separate delivery gates until their own executable checks pass.

The pure listed-option adapter retains the source calibration and open signed
remaining lots. At expiry it applies the exact versioned automatic exercise rule
or the captured contrary instruction, then replaces the option with the ordinary
linked future at strike. Later margin events use the same futures difference and
rounding calculation. Premium cash is a separate original holding. A physical
linked future requires qualified delivery admission before any path is sampled.
Instruction cutoffs and expiry are separate boundaries: the held option remains
priceable after its instruction cutoff and before expiry, at its held volatility.

The pure dated financial-swap adapter uses the existing Side payment cash,
averaging, reset applications and exact membership/quantity scaling. Path-local
fixings lock once; projected contractual cash is rounded under the selected
gross-payment convention. Separate hypothetical binding state is rejected by
actual valuation and native settlement. Source-known native determinations and
their complete correction chains remain actuals, including future-effective
determinations visible at the selected knowledge cutoff. Their exact amounts
apply at the recorded effective instant and take precedence over a same-instant
projected fixing. Knowledge-only payment history retains native composite
allocation identities, reconciles opening allocations and bounds every effective
allocation prefix by its exact accepted contribution. Future actual payments are
reserved against hypothetical discharge and recognized once at their own instant.

A fully prepared scope with no variable factors may use the separately labelled
deterministic economic result. Its single exact point mass includes signed time
and carry risk; the market-only diagnostic is zero. It creates no historical
dates, covariance calibration or random paths. This admission is never a fallback
for failed dependency preparation or missing prices/history.

The horizon identifies the native close-session primary key: catalog, code and
exact version. It uses no synthetic session UUID. Native certification of that
configuration and complete next-close calendar resolution remain source-reader
responsibilities.

The native horizon reader records immutable close-session publication links in
the existing certified trading publication stream. Existing published settings
receive their real migration commit time, with no historical backdating. Reads
bind the exact source context, verify the selected governing opening version and
follow only the source-known effective session schedule to the next close.
Every consumed calendar/clock/version is authorized and retained; unrelated
versions are excluded from evidence. Missing coverage, an unreadable consumed
version or an ambiguous/nonexistent cutoff refuses rather than rolling the date.
Managed source admission retains the original read deadline through commit.
Owned native checks cover old-cutoff refusal, migration receipts, calendar
holidays/weekends, a known future version change, an unreadable unused version,
source-context binding and successor-grant deadline refusal.

Qualified connected economic components can update one flat immutable member
map atomically, as required when delivery consumes an explicitly linked owned
stock position. `Compose` builds the sole valued residual/cash aggregates, and
preparation and every transition verify those aggregates against all members.
Members retain their original economic owners, opening PV, residual PV and cash;
results and empirical tail contributions remain separate for each owner. Nested
states are never priced again. Source-known payments and native cash evidence
cannot move between members during a hypothetical path. Current composition
admits one book, currency and source graph; cross-source funding requires its own
qualification. The managed independent oracle transfers 100 units of value
between two members and retains their -100/+100 P&L with zero portfolio loss.
