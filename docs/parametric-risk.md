# Parametric portfolio risk

D299 adds one-session delta-normal VaR and expected shortfall to Risk. The engine
consumes the complete [aligned dated-factor history](risk-factor-history.md),
ordinary portfolio PV and shared market-factor scenarios. Run now and retained
jobs use the same managed calculation. New product types need their ordinary
retained materializer and PV/factor declarations; statistical risk has no product
allowlist or nonzero-Greek filter.

## Explicit statistical and numerical contracts

The caller selects `dated_factor_delta_normal_v1` and
`exact_moments_binary64_normal_v1`, confidence as a plain decimal greater than
0.5 and at most 0.9999, and either `zero_drift_v1` or `sample_mean_drift_v1`.
The API supplies no missing-field financial defaults. Risk offers the visible,
editable D301 starting profile described in the
[VaR walkthrough](risk-factor-history.md#guided-var-setup-d301); every selected input
is sent and retained explicitly. The covariance estimator is
`equal_weight_sample_n_minus_one_v1`, centered about the sample mean under
`sample_mean_centered_v1`, regardless of forecast drift selection. Changes are
native absolute changes in fixed dated factors, on common complete rows. There
is no annualization, horizon scaling, decay weighting, proxy or covariance repair.

For common changes x, sample mean m, sample covariance S and net native deltas d,
loss has mean a = −d·m (or explicitly zero) and variance v = dᵀSd. With s = √v,
confidence c, normal quantile z and normal density φ:

- VaR = a + z·s.
- ES = a + φ(z)·s/(1−c).
- Each Euler component uses its own loss mean and covariance with the net loss.
  Signed book and factor components reconcile to the partition's risk, subject
  to the retained binary64 residual. Hedging components may be negative.

These are standard normal loss formulas; see the original
[RiskMetrics methodology](https://www.msci.com/research-and-insights/paper/1996-riskmetrics-technical-document)
and the author's [expected-shortfall derivation](https://personal.ntu.edu.sg/nprivault/MH8331/expected_shortfall.pdf).
VaR/ES are signed upper-loss-tail values, with no zero floor. A zero-variance
partition is a point mass at its selected loss mean, with no continuous density.
Singular sample covariance remains admissible; exact rank, constant columns and
a dimensionless correlation condition estimate are diagnostics, not repair rules.

Means, covariance, delta aggregation, loss variance, covariance cross terms and
in-sample linearized losses use exact rational arithmetic. The normal step uses
bounded original positive series, fixed 64-step bisection and ordinary binary64
operations. Density uses range reduction and four squarings, without native
platform exponential functions. Confidence that collapses to 0.5 in binary64
refuses. Nonzero exact values that cannot cross the finite binary64 boundary
refuse rather than silently underflowing. Approximate values retain G17 digits
expanded to plain decimal. The report shows VaR, ES, deviation and mean through
the shared amount display at the user's money precision (D114), with the exact
value on hover; exact moments and reconciliation residuals stay in a collapsed
receipt. Exact intermediate numerator/denominator size is bounded at 16,384 bits.

Deltas are measured by ordinary full PV at native half, nominal and double
central-difference steps, under
`native_tick_central_half_nominal_double_v1`. The caller chooses 1–1,000 trading
ticks and absolute native/relative delta agreement tolerances. Every factor,
book, valuation measure and currency retains all three estimates, nominal gamma,
unit and agreement result. Agreement is a local diagnostic, not proof of global
linearity. Unstable deltas, excluded positions or insufficient history make risk
unavailable; partial successful VaR/ES totals are never presented.

The horizon is `frozen_portfolio_one_adjacent_session_v1`: current holdings,
quantities, contractual state, rates, actual fixings and other inputs stay fixed
while dated market factors move. Gamma, time passage and full-repricing tails
are outside delta-normal risk. In-sample linearized loss charts are calibration
diagnostics, not historical simulation or out-of-sample backtests.

Native valuation measures and exact currency references define independent
partitions. There is no cross-currency conversion or netting across unlike PV
measures. Shared factors net across included books before variance is calculated.

## Retention, authority and archive

`POST /api/portfolio/risk/parametric` accepts the canonical captured history
request, statistical policy and FD policy. The dedicated
`POST /api/portfolio/risk/parametric/jobs` wraps that request with an idempotent
request ID. The ordinary portfolio risk envelope can also carry this typed
`parametric` request, `detail: books`, empty stresses and the full native factor
union. One complete statistical run is one job; sensitivity multipart frames
refuse. Results omit legacy `factors`, preventing older sensitivity readers from
misinterpreting a statistical report.

Migration 0175 retains exact historical observation-version FKs independently of
current-PV pins. It includes rejected endpoints, current anchors and correction
ancestry. The clock capture receipt is minted only by the API's separate trusted
authenticator connection from authorized server-resolved calendar/clock inputs.
Runtime and worker roles cannot mint, read raw, update or delete receipts. Job
admission and worker reads require the exact receipt/context/calendar/clock
payload and real FK. Failed identical submissions reuse receipts; unreferenced
captures are bounded at 128 per catalog and 16 per context.

The worker reads typed source evidence through a private lease-fenced operation,
reconstructs the expected history identity and uses the captured close instants
without resolving current time-zone rules. It checks actual calendar dates,
market/schedule contexts, full source keys, current authority and active lease.
Original PV/graph/axis/option pins, engine-version identity, cancellation and
publication fences remain in force. History retention does not change ordinary
bound observation RLS visibility. Captured identities never grant access.

`GET /api/portfolio/risk/parametric/archive` requires an explicit page size
1–100. Its paired `(createdAt,id)` cursor orders completed owned statistical
jobs newest first. Each page and report read rechecks current scope authority.
The archive is independent of the recent 50-job recovery directory and of whether
the current header can price a new run. It is a report archive, not a scheduler.

## Presentation and bounded qualification

Risk's guided setup offers history window, confidence, business calendar and
ordered permitted price types, with numerical controls under Advanced. Run VaR
automatically prepares the exact aligned history when needed; preview is optional.
The
shared report widget displays native VaR/ES/deviation cards, Gaussian density,
signed book/factor contributions, in-sample linearized losses with the model VaR
reference, and a switchable correlation/covariance heatmap. The aligned-factor grid
shows
selected prices or common changes, official-close coverage, permitted fallback
types and actual observation timestamps. Both views page their display without
reducing the calibration universe; covariance cells identify both native units.
Large matrices use explicit 32-by-32 display
windows; every factor remains in calibration. Exact component/covariance and FD
receipts accompany the graphics. Switching VaR/ES changes the displayed backend
components. TypeScript performs no financial calculation.

Queued execution uses the existing retained-command state machine: original
identity persists before dispatch, uncertain outcomes reconcile using the same
request, and reload reauthorizes retained results. Exports reauthorize and require
the captured statistical request, history identity, context and selected trades.
The completed-report archive offers links back to the same retained widget.

Admission bounds are 128 live factors, 4,096 native exposures and 2,000,000
sample/factor/factor products, within R3's existing history bounds. All three FD
passes count toward PPA row, option-tree and retained-value work limits before
scenario valuation. Complete reports are bounded at 30 MB. Work receipts include
the complete three-pass operation and separate per-pass diagnostics. These are
allocation/refusal guards, not production throughput claims.

Independent checks cover exact one/two-factor moments and tails, perfect offsets,
singular/zero variance, signed negative quantiles, hedging components, rational
centering, tiny amounts, native partitions, unstable or incomplete coverage,
forecast prefixes without later moves/corrections, numerical/domain bounds and
cancellation. Owned HTTP/SQL/browser qualification covers direct/worker replay,
receipt provenance, active-attempt custody, archive pagination/current authority,
explicit setup, report graphics, reauthorized export and uncertain-write recovery.

[Full-PV historical simulation](historical-risk.md) now supplies the separate
empirical quantile/tail-mass estimator and an optional same-study sample-mean
normal comparison. Its losses are distinct from this method's in-sample linearized
diagnostics. [Full Monte Carlo](monte-carlo-risk.md) shares the exact common-row calibration
and supplies a full-PV Gaussian simulation reference. An operating rolling-backtest
workflow and EVT remain subsequent increments. [PCA/Hybrid Monte Carlo](pca-hybrid-risk.md)
is separately implemented with explicit approximation and fallback contracts. Production activation requires
a separately qualified schema release.
