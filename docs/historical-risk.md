# Full-PV historical VaR and expected shortfall (D305)

Historical simulation applies every complete aligned factor-change vector to the
same captured current portfolio and calls ordinary instrument PV. Independent
shared factors are shocked once per scenario; instrument dependencies select
affected work. Instruments and independent Side bindings keep their existing
pricing contracts. The complete native holdings and factor union are retained;
there is no product allowlist or Greek-based factor pruning.

## Run it in Risk

1. Select portfolios, books or individual trades. Open **VaR setup** and choose
   **Historical simulation VaR / expected shortfall**.
2. Review confidence, history window and business calendar. The visible starting
   settings use 99%, six months and at least 60 complete changes. Sampling uses
   the selected calendar's end of day; there is no time-entry field. Official
   daily closes are initially required. Explicitly order any allowed alternative
   price types; the aligned grid identifies missing official closes and every
   fallback. A sole calendar selects automatically.
3. Click **Run VaR** or **Queue VaR job**. Matching history is prepared automatically
   and reused while its captured inputs remain the same. **Preview price coverage**
   is optional. Insufficient common history or excluded holdings produces an
   unavailable report with coverage evidence, never a partial successful estimate.
4. Read the empirical distribution, full-PV loss series with VaR/ES, and signed book
   allocations. Inspect book/trade allocation tables, then select a historical
   sample to see its business-session dates, factor moves and every retained
   trade/Side binding's base PV, scenario PV and loss. Each native currency and
   valuation measure is a separate partition. Risk cards use the selected money
   display precision and retain exact values in their tooltips and JSON.
   The grid retains source prices,
   price types, actual observation timestamps and captured knowledge evidence.
5. Optionally select **Include matching parametric comparison** before running.
   The same job computes a sample-mean delta-normal comparison using the exact
   same history, current quantities, pricing receipts, confidence and horizon.
   Its advanced FD settings remain explicit. Completed reports are discoverable
   alongside parametric reports in **Reports and runs**. JSON export reauthorizes
   and retains complete exact values and input evidence.

## Financial contract

`dated_factor_full_pv_historical_v1` uses
`equal_weight_raw_joint_changes_v1`: raw joint absolute changes from common
complete adjacent business sessions. Each row is an immutable sibling of the
same current base. The horizon is
`frozen_portfolio_one_adjacent_session_v1`; valuation time, quantities,
contractual state, known fixings, rates and current solved option calibration
(or the existing declared intrinsic-residual mode) stay fixed. No time passage,
contract roll, volatility recalibration, covariance scaling or implicit proxy is
introduced. Stored fitted volatility surfaces remain view-only under D304.
A historical sample outside a pricing domain fails the whole run; no sample is
dropped, clipped or replaced. All-zero rows and repeated vectors retain distinct
sample identities and their full equal probability mass.

Loss is base PV minus shocked PV. Books/market partitions are netted **before**
quantiles within each native measure/currency. For `n` observations and confidence
`c`, VaR is sorted loss number `ceil(n*c)`. ES averages the highest `n*(1-c)`
sample-equivalents, including fractional boundary mass. Ties at VaR share the
remaining boundary mass uniformly. Signed gain-only quantiles remain negative.
For losses `[0,10,20,30,40]` at 75%, VaR is 30 and ES is 38.

Book and trade allocations apply the portfolio's quantile/tail weights to each
component's losses. They reconcile exactly, can be negative, and are **not** Euler
or marginal risk. All VaR-tied rows share quantile attribution uniformly. The
report includes exact weights, tie counts and tail sample mass; a small tail count
is limited historical evidence, not an accuracy guarantee. Point masses are
shown explicitly. Histogram probabilities, loss amounts and allocation values
are backend results; browser approximation is confined to chart coordinates.

The numerical policy is `exact_empirical_probability_mass_v1`; quantile and tail
policies are `empirical_inverse_cdf_v1` and
`signed_upper_loss_fractional_mass_uniform_ties_v1`. Exact rational arithmetic
has a 16,384-bit intermediate bound. Confidence must be an explicit plain decimal
above 0.5 and at most 0.9999. No horizon square-root scaling is offered.

## Comparison and retained execution

The optional normal approximation requires explicit sample-mean drift and binds
to the same study identity. Its normal-only stability failures remain visible as
unavailable normal risk. After historical PV succeeds, comparison-only
`risk_parametric_numeric`, `price_domain`, `option_input` or `numeric_range`
failures preserve historical risk with an explicit unavailable-comparison receipt.
Coverage, source, authority, cancellation, lease and pricing-identity failures
remain fatal. Failed comparison attempts have no complete work receipt: the top
report explicitly labels completed historical work only; option counters still
record attempted tree prices. A failed comparison is never reported as zero work.

`POST /portfolio/risk/historical` and `/portfolio/risk/historical/jobs` use an
exclusive historical envelope. One complete holdings-detail job retains every
factor declaration, immutable clock receipt, exact history observation pins,
quantities and pricing inputs. Workers replay those inputs and recheck current
authority and the active attempt before publication. Existing evidence custody
and observation visibility are reused; migration 0189 adds no raw table grants.
Original request IDs return the original job. Statistical multipart composition
is refused. Existing named-scenario and sensitivity limits are unchanged.

Historical and combined paged archives are
`GET /portfolio/risk/historical/archive` and `/portfolio/risk/statistical/archive`;
the existing parametric archive keeps its original method filter. Readers,
archives and exports all require current report authority.

## Qualified bounds and remaining work

The complete historical matrix admits up to 128 factors, 2,048 changes, 4,096
native holding identities and 250,000 base/sample values, with a 30 MB complete
report limit. Existing PPA row-work and 100 million option rollback-node guards
apply. Optional comparison work is admitted with historical work before scenario
pricing; native FD representability is also checked before pricing. These are
refusal bounds, not throughput claims.

Independent checks cover empirical mass/ties, negative tails, offsets, native
partitions, nonlinear joint PV, zero/duplicate vectors, complete source holdings,
and direct/worker agreement. Owned stock-database qualification covers altered
clock receipts, missing observations, active leases, cancellation, current
access, immutable history, method separation and archive pagination. Browser
checks exercise guided setup, graphical reports, trade/sample drilldown,
reauthorized export and retained reports on desktop and phone. Captured business
dates, including New York end-of-day DST, supply labels rather than UTC dates.

Two bounded forecast/hypothetical-P&L fixtures exclude the following session from
calibration and apply that later move to frozen forecast holdings. They are an
initial no-look-ahead oracle, not an operating backtest service. Rolling report
storage, exceptions, coverage/independence tests and ES backtesting remain work.
Full Monte Carlo is next before PCA/hybrid approximation; EVT requires separate
tail qualification. Production activation requires a separately qualified schema
release.
