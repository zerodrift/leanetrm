# Full-PV Monte Carlo VaR and expected shortfall (D307)

Monte Carlo draws correlated joint changes in the complete shared dated-factor
universe and reprices the captured current portfolio through ordinary instrument
PV. It uses the same immutable sibling scenarios, native holdings, source history,
current option calibration and retained-job fences as historical simulation.
There is no product allowlist or Greek-based pruning.

## Run it in Risk

Select portfolios, books or individual trades, open **VaR setup** and choose
**Monte Carlo VaR / expected shortfall**. Review confidence, history window,
calendar, simulation paths and reproducible seed, then **Run VaR** or
**Queue VaR job**. The shared starting history settings use six months, a minimum
of 60 common changes, end of day and official daily closes. A sole calendar
selects automatically; allowed alternative price types remain explicitly ordered
and flagged in the aligned grid. History is prepared or reused automatically.

The visible starting simulation settings use 99% confidence, zero drift, seed 1
and up to 4,096 paths. The selected scope's native result width, option rollback
nodes, PPA rows and complete factor count lower the starting count when required.
All policies and choices are submitted and retained explicitly; the calculation
API supplies no missing-field financial defaults. Advanced settings allow sample
mean drift. The horizon is the supported single adjacent-session market move;
there is no manual time entry or implied horizon scaling.

The shared Risk/Blotter report includes the empirical simulated-loss distribution,
a path loss chart with VaR/ES, signed book and trade allocations, convergence by
sample prefix, and a native-measure selector. Enter a path number or select a loss
point to inspect every factor move and holding's base PV, scenario PV and loss.
Correlation/covariance graphics use 32-by-32 display windows while calibration
retains every factor. The original aligned price/changes grid remains available.
Money cards follow the user's display precision and retain exact values on hover
and in reauthorized JSON exports. Completed reports appear in the common archive.

## Financial contract

`dated_factor_full_pv_monte_carlo_v1` selects
`joint_additive_gaussian_v1`. Calibration shares the exact sample means and
`equal_weight_sample_n_minus_one_v1` covariance with delta-normal risk, using the
same complete aligned raw absolute changes. Covariance is centered on the sample
mean regardless of the selected `zero_drift_v1` or `sample_mean_drift_v1` forecast
policy. There is no annualization, volatility decay, covariance repair, prompt
roll, proxy, resampling of separate factor columns, or lognormal transform.

`frozen_portfolio_one_adjacent_session_v1` and
`current_instrument_pv_frozen_nonfactor_inputs_v1` keep valuation time, quantities,
contract state, known fixings, rates and current option calibration fixed. Stored
fitted volatility surfaces remain view-only under D304. A path is a sibling of
the same current base, never a child of the preceding path. Additive Gaussian
moves can produce negative prices. A pricing-domain failure fails the entire
run; paths are never clipped, discarded, replaced or recalibrated.

Loss is base PV minus simulated PV. Books and market partitions net **before**
quantiles within each exact currency reference and valuation measure. The shared
empirical reducer uses signed inverse-CDF VaR, fractional upper-tail mass ES and
uniform attribution across tied boundary losses, as specified in
[historical risk](historical-risk.md#financial-contract). Repeated/unchanged draws
keep distinct indices and full equal probability mass. Portfolio-tail allocations
reconcile exactly and can be negative; they are not Euler or marginal risk.

## Reproducible numerical generation

`exact_ldl_binary64_polar_v1` decomposes the exact rational covariance in captured
column order as `L D Lᵀ`. It checks exact reconstruction. A zero pivot requires an
exactly zero remaining residual column and contributes no variance; a negative
pivot or inconsistent zero pivot refuses. No tolerance, jitter or rank repair is
applied. Intermediate arithmetic is bounded at 16,384 bits; retained exact
coefficients must fit the shared 4,096-bit rational text contract.

The selected `sha256_counter_polar_log_series_v1` generator addresses each draw
by unsigned 64-bit seed, zero-based sample index, normal pair and rejection
attempt. Its message is the 20 ASCII bytes `OpenETRM MC polar v1`, followed by
little-endian seed (8 bytes), sample/pair/attempt (4 bytes each), and four zero
reserved bytes. The first two little-endian digest words supply 52-bit open-cell
midpoints. Marsaglia polar rejection accepts radius strictly between zero and
one and refuses after 128 attempts. A fixed range-reduced atanh log series and
IEEE binary64 square root avoid native log/trigonometric library differences.
Ordinary operations use fixed order without fused multiply-add. Path count and
batch boundaries do not enter a draw's address.

Exact coefficients cross a finite nonzero binary64 boundary, and native shocks
use `binary64_exact_to_native_18_places_to_even_v1`: the exact binary fraction is
rounded to 18 decimal places with ties to even. A nonzero shock rounded to zero,
nonfinite result or native magnitude overflow refuses. Generation and rounding
are approximate: exact calibration rank does not make quantized shock covariance
or all singular linear relationships exact. Actual submitted native shocks,
calibration and generation versions are retained for replay.

## Sampling diagnostics

Convergence shows quarter, half and full prefixes of the same indexed draws,
with duplicate prefix sizes removed. It is a diagnostic, not an accuracy promise.
`binomial_order_statistic_binary64_95_v1` supplies a pointwise 95% sampling interval
for VaR conditional on the fixed fitted model. It supplies no ES interval and
excludes calibration and model error.

For `B ~ Binomial(n,c)`, the lower rank `l` is the largest with `P(B < l)` at most
0.025, and the upper rank `u` is the smallest with `P(B >= u)` at most 0.025.
Rank zero or `n+1` represents an unbounded endpoint, never a substituted sample
extreme. The implementation uses normalized mode-centered binary64 probabilities,
direct tails and a conservative numerical allowance of 1e-12 at each cutoff.
Ties and point masses remain visible. The order-statistic construction follows
[NIST TN 2119, section 5.3](https://nvlpubs.nist.gov/nistpubs/TechnicalNotes/NIST.TN.2119.pdf).

## Retention, bounds and qualification

`POST /portfolio/risk/monte-carlo` and `/portfolio/risk/monte-carlo/jobs` accept
an exclusive complete-holdings envelope. The ordinary portfolio-risk envelope
also accepts `monteCarlo`, preserving its original scope selection separately
from canonical calibration holdings. One complete simulation is one job;
sensitivity multipart composition refuses. Workers regenerate from retained
source/clock receipts and the same engine binary fingerprint, recheck original
pricing/option/axis evidence and current authority, and publish only under the
active uncancelled attempt. Migration 0190 extends private existing custody and
archive functions without new tables or raw observation grants.

Bounds are 128 factors, 16,384 paths, two million generated shock values,
two million covariance sample-products, 4,096 native exposures, 250,000 retained
base/sample values and a 30 MB complete report. Source/generation receipts alone
must fit the report bound before scenario PV. Existing PPA row and 100 million
option rollback-node guards include the frozen base and every path. These are
refusal bounds, not throughput claims. Ordinary named-scenario and historical
limits remain unchanged.

Independent numerical checks compare Gaussian moments/linear risk, exact LDL,
SHA/polar rejection vectors, high-precision shocks and binomial ranks. Full-PV
checks cover nonlinear losses, singular/zero covariance, unlike units, actual
option-domain refusal, mixed native partitions and direct/worker agreement.
Stock-database checks cover immutable receipts, invalid policies/axes, method
archives, current authority, cancellation, lease custody and missing source pins.
Browser checks cover setup, graphics, drilldown, export, archive, replay and phone
layout. See [verification](verification.md) for the current qualification record.

PCA/hybrid acceleration, EWMA, alternative innovation distributions, operating
rolling backtests, ES uncertainty and Monte Carlo page-assistant summaries remain
separate increments. Production activation requires a separately qualified schema
release; application implementation does not deploy the schema.

## PCA and hybrid comparison (D308)

Setup now offers an explicit optional [PCA / hybrid qualification](pca-hybrid-risk.md).
It retains the exact same complete native shocks and full-PV reference, adds
loadings/residual graphics and fits a candidate using all native deltas plus
selected PC curvature. Complete path and native portfolio comparisons determine
per-run qualification. D309 refuses failed checks by default; the user must
select **Allow full-PV fallback** to permit substitution. Its work is additional
qualification, with no acceleration claim.
The selected policy, actual fitting vectors/PVs and classification results travel
with the original retained job; migrations 0191–0192 extend its strict private
admission and retain explicit fallback consent.
