# PCA diagnostics, qualification and Hybrid Monte Carlo (D308–D311)

Monte Carlo setup can include **PCA diagnostics and hybrid comparison**. This
mode retains the complete full-PV reference and fits a black-box delta-gamma
candidate against the same captured portfolio and native shocks. Every generated
path receives full PV. This is qualification work with additional measured cost;
it does not reduce full-PV work or certify future seeds, portfolios or tails.

## Setup and report

The visible starting selection uses up to three curvature directions, lowered
when required to fit the selected scope's work budget, one native
trading tick, relative derivative agreement 0.0001, native absolute loss-error
allowance zero and relative loss-error allowance 0.001. All choices are explicit
request inputs. Users can select one to eight directions, bounded by the complete
held factor count. The path limit includes both reference and fitting work;
changing the selected directions can lower the visible allowed path count.
History keeps the existing EOD and ordered price-type controls.

**Allow full-PV fallback** starts unchecked. Without that selection, any failed
qualification refuses the run with a structured error; it does not substitute
full-PV values. The explicit choice is retained in the request, study identity,
queued replay and report. A fresh setup or account reset starts unchecked.

The report adds explained standardized market variance, factor loadings, ideal
PCA residual norms, native residual/cross-covariance matrices, raw eigenvalues,
reconstruction diagnostics, same-path full-PV/candidate/qualified-hybrid losses,
native portfolio and standalone holding VaR/ES comparisons, holding errors,
classification/fallback reasons and reference versus additional calculation counters. The Blotter prepares display
amounts only when its full report is opened, with per-batch account/read/run and
freshness fences; closing or replacing the view invalidates pending formatting. Explained market variance is
not a measure of portfolio risk coverage. A low-variance spread can carry large
risk. Every native factor remains in the candidate's linear term.

## PCA numerical contract

The optional request's `analysis` selects
`correlation_pca_hybrid_qualification_v1`, with:

- `cyclic_jacobi_binary64_residual_checks_v1`;
- `sample_sd_constant_unit_v1`;
- `complete_native_shocks_all_factor_delta_v1`;
- `full_reference_no_fallback_qualification_v1`, or the user-selected
  `full_reference_opt_in_full_pv_qualification_v1`;
- `pc_0_01_native_17_places_collinear_v1`.

The exact captured sample covariance remains authoritative. Nonconstant columns
are scaled by their sample standard deviation; constant columns use scale one.
The standardized covariance has diagonal one for nonconstant columns and zero
for constant columns. The bounded binary64 cyclic Jacobi solver starts with the
identity, visits pairs in canonical factor order and admits at most 100 sweeps.
Its rotation threshold is eight binary64 machine epsilons times the larger of
one and the dimensionless maximum absolute row-sum norm.

Eigenvalues sort descending with original column index breaking exact ties.
Each vector's largest absolute loading is positive, with canonical factor order
breaking loading ties. Repeated/nearby eigenvalues describe an invariant subspace;
individual economic labels and stability across changed calibrations are not
claimed. The selected count is retained even above the covariance rank or at
rank zero; directions use scales/loadings, never square roots of eigenvalues.
Deterministic mean drift remains in the applied vectors.

Covariance reconstruction and eigen-equation residuals must be at most
`1e-12 * max(1, norm)`; orthogonality and each full standardized native-shock
reconstruction must be at most `1e-12 * factorCount`. A raw negative eigenvalue
below the negative reconstruction tolerance refuses the optional analysis.
Tolerated roundoff-negative eigenvalues remain signed in the receipt; only their
explained-variance mass is zero. They never repair covariance or enter shock
generation. The complete R6 generator, native shifts and full-PV risk result are
unchanged with analysis enabled.

For the ideal loading projector P and residual projector R=I−P, the diagnostic
matrices retain Cov(residual)=S R C_scaled Rᵀ S and retained/residual cross
covariance S P C_scaled Rᵀ S. These describe the ideal PCA projection. They do
not claim that quantized fitting directions exactly equal eigenvectors.
Approximate coefficients retain their exact IEEE binary64 fractions as decimal
or rational text. No display rounding enters calculations.

## Generic fitting and valuation

No product allowlist or market-linearity flag determines eligibility. The common
PV path evaluates shared immutable sibling scenarios, and trade quantities remain
in the ordinary valuation boundary. All native deltas use exact central
half/nominal/double trading-tick steps. Curvature uses diagonal and mixed central
differences along the selected PC directions at the same three step scales.

A nominal PC direction is 0.01 times its scale/loading vector, rounded once to
17 decimal places, ties to even. Its exact half and double are formed from that
vector. Nonzero component underflow, range failure or a standardized direction
error above 1e-10 makes fitting unavailable. Mixed corners combine their two
signed vectors by canonical native factor before applying the shared scenario;
exact cancellations are omitted. The actual directions, full native steps and
executed fitting PV matrix are retained.

Projection uses standardized weighted least squares in the actual quantized
direction basis, with an explicit inverse-Gram and reconstruction check at
1e-10. The candidate is today's base PV plus every native delta times the full
applied native move, plus one-half the actual-direction quadratic form. The
projection includes selected sample-mean drift. Residual delta is never dropped;
omitted residual/cross-residual curvature is an approximation requiring the
full-path comparison. The holding's maximum actual-projection residual linear
P&L is reported separately from the ideal PCA diagnostics.

Derivative agreement compares half/double with nominal using the explicit
relative allowance times the larger absolute estimate. Every candidate holding
loss and its standalone empirical VaR/ES are compared with full PV using native
absolute allowance plus relative allowance times the larger absolute amount. Zero losses receive no inferred floor.
Values and tolerances are exact rational numbers after the declared binary64
projection boundary. All fitting and predicted retained numbers must fit the
shared 4096-bit transport.

## Qualification and fallback

The no-fallback policy refuses a failed derivative, holding loss, standalone
VaR/ES, portfolio comparison, or fitting numerical/domain check with
`risk_pca_hybrid_qualification`. No successful hybrid report or substituted
result is returned. The complete full-PV reference is still calculated for
qualification, but it is not silently returned as a successful replacement.

Only when the user explicitly selects fallback does a failed derivative, holding
loss or standalone VaR/ES comparison fall back the complete shared
unit-valuation identity across all its bindings, measures and currencies, for
every path. Errors cannot disappear by netting opposing holdings first. The
remaining hybrid portfolio is then checked path by path and at its empirical
VaR and ES in each native partition. Failure falls back the entire portfolio.
Only full-reference quantile/ES tail membership defines the reported tail-error
diagnostic; full-path comparisons remain required.

Fitting currently executes as one shared portfolio batch. A numerical or
pricing-domain failure in any fitting scenario therefore refuses a no-fallback
run. With explicit opt-in, it causes full-PV fallback for every valuation in that portfolio; healthy candidates are not
separately qualified in that failed batch. Its report retains fallback identities
and reasons. Completed fitting counters are retained; an interrupted fitting
attempt without complete counters marks total work unavailable rather than
reporting invented savings. Exact-arithmetic exhaustion in optional candidate,
hybrid or standalone empirical reductions also refuses a no-fallback run; with
opt-in it falls back the optional comparison and retains the successful full-PV
reference. Missing holdings, changed scope/base, authority,
history and cancellation failures remain fatal. Any failure on a generated
full-reference path still refuses the complete Monte Carlo run.

## Retention, limits and remaining work

The optional typed policy belongs to the existing Monte Carlo request and study
identity. Jobs retain it with the existing source/clock/engine/lease/attempt
fences; direct and worker operations regenerate the same numerical analysis.
Actual execution-cost counters can differ with warm versus fresh preparation
caches. Migrations 0191 and 0192 guard the complete prior private retention helper
definition, owner, ACL and search path. Migration 0192 admits only the two new
explicit qualification modes and adds no table or raw grant. The original
`full_reference_qualification_v1` is read-only legacy evidence: existing
completed reports retain their original policy and show a legacy-policy label. It
is refused for new submissions and fresh queued calculations; requests and reports
are never silently upgraded. Existing outcomes remain available through
authorized report reads; an old-policy API resubmission is refused. Old requests
with no analysis retain their original numerical contract
and study identity. Authorized report export retains the full reference, actual
fitting directions, fitting PVs, models and qualification results.

Admission counts full reference base/paths plus fitting base and
`6 * factorCount + 6 * componentCount²` sibling scenarios before scenario PV.
It applies the existing complete value, PPA-row and option-tree budgets.
Additional counters subtract shared parent-graph, materialization and cached volume
work already charged to the reference; new fitting PV and quantity applications
remain charged. Report size remains bounded at 30 MB. Calculation counters distinguish reference and
additional work; no performance benefit is claimed from these comparisons.
D311 adds the separately selected approximate mode below. Per-valuation isolation
of fitting domain failures, EWMA, EVT, broader shock types and operating rolling
backtests remain separate work.

Independent numerical checks cover analytic eigenpairs, singular/constant and
tied factors, unit rescaling, every native-vector reconstruction, low-variance
spread delta, exact quadratic mixed curvature, drift, nonsmooth derivatives,
opposing bindings, whole-portfolio error and exact collinear/mixed directions.
See [verification](verification.md) for the executing owned-database and browser
checks.


<a id="planned-accelerated-hybrid-mode"></a>
<a id="planned-hybrid-monte-carlo-valuation"></a>

## Hybrid Monte Carlo valuation (D311)

Status: implemented as a separately selected approximate method. Production
schema activation is a separate release operation. Full-PV Monte Carlo and the
every-path qualification mode above retain their original contracts.

### Run a calculation

1. In Risk, select a portfolio, book or individual trades and open VaR setup.
2. Choose **Hybrid Monte Carlo VaR / expected shortfall · approximate**. Review
   history, confidence, paths, random seed, retained PCA components and **Full-PV
   validation paths**. The visible starting panel has up to 64 paths, up to three
   components and up to 4096 simulation paths, bounded by the captured scope's
   complete work limits. Advanced settings expose the separate validation seed,
   finite-difference ticks and stability/loss tolerances. No time entry is needed;
   official EOD closes remain selected unless alternatives are explicitly allowed.
3. Leave **Allow full-PV fallback** unchecked to stop on failed checks. Select it
   only to permit complete portfolio repricing after a fitting/validation failure.
   The choice is retained in the request, study identity, worker and report.
4. Calculate immediately or queue. Aligned history is prepared/reused by the
   existing guided workflow. Scope or source changes cannot inherit model approval.
5. Read the approximate distribution separately from **Hybrid Monte Carlo
   validation**. The main path table contains model PV estimates on every path,
   including checked indices. Validation has its own actual full-PV drilldown,
   observed panel VaR/ES and error graph, PCA/residual matrices, applied valuation,
   reasons and work by phase. Retained Risk and Blotter reports and exports use
   the same evidence. Fresh method selection resets fallback off.

### Explicit execution contract

The complete method tuple is:

- `dated_factor_hybrid_monte_carlo_v1`;
- `pca_delta_gamma_frozen_nonfactor_inputs_v1`;
- `empirical_prefix_diagnostics_no_interval_v1`.

The other R6 generator/covariance/native-rounding policies are unchanged. The
required `analysis` reuses D308 fitting with either
`sampled_panel_no_fallback_qualification_v1` (default) or
`sampled_panel_opt_in_whole_portfolio_full_pv_v1` (explicit consent). Required
`hybrid` inputs select `pca_delta_gamma_sampled_pv_validation_v1`,
`sha256_ranked_indices_independent_of_loss_v1`,
`whole_portfolio_full_pv_v1`, an integer validation count from 2 through the
selected path count, and a canonical unsigned 64-bit decimal validation seed.
Mixed/missing policies refuse; the old every-path fitting modes cannot authorize
this execution. Visible defaults are sent explicitly, never inferred by the API.

For each original zero-based path index, the selector hashes a 40-byte record:
ASCII `OpenETRM hybrid panel v1` starting at byte 0, zero padding, the validation
seed as unsigned little-endian 64-bit at byte 24, the index as little-endian
32-bit at byte 32, and trailing zero bytes. Rank by SHA256 bytes, break a tie by
original index, take the requested count and execute in original-index order.
The selector reads no losses, prices or shock values and uses no shock-stream
random draw. A changed count gives nested panels for a fixed seed/index range.
There is no adaptive expansion, replacement draw or implicit tolerance change.

All native factors/deltas and full native simulated shifts remain. PCA limits
curvature directions, not factor exposure. Ordinary PV supplies the complete
half/nominal/double fitting batch and the selected validation panel. The model
predicts all N main path values; checked paths are never patched with full PV.
Every generated shock, including unpriced ones, must pass the existing declared
quote min/max, coordinate and unit checks. Those checks do not establish that an
arbitrary black-box pricer is accurate or valid at every unpriced point.

<a id="acceptance-before-enabling-acceleration"></a>
<a id="acceptance-before-enabling-hybrid-monte-carlo"></a>

### Validation, failure and consent

Derivative stability, observed holding path errors, standalone panel VaR/ES and
native portfolio panel checks use the explicitly selected D308 tolerances.
Panel tail membership and metrics describe the **observed panel only**. A finite
panel cannot bound unpriced losses or certify full-distribution/tail accuracy.
Independent tests deliberately construct nonlinear losses missed by a panel.
The full-reference mode remains available when every-path evidence is required.

A failed model, holding, native portfolio or fitting numerical/domain check stops
a strict run with `risk_pca_hybrid_qualification`; no successful replacement
report is returned. With explicit opt-in, v1 reprices **every path and holding**
through full PV, including each shared binding and native partition. It reports
`full_pv`, `fallbackApplied`, reasons and completed work. There is no per-tail
patching or per-valuation partial execution in v1. Fallback pricing failure is
fatal. Invalid policies, generated quote-domain failures, changed/missing base
holdings/units/values, history, authority and cancellation remain fatal and never
invoke fallback.

Prefix convergence is descriptive. Hybrid results never inherit R6's conditional
binomial VaR interval, even after full-PV fallback: the applied method was selected
after observing sampled checks, so that interval would require separate coverage
qualification. ES and model/calibration uncertainty are not estimated.

### Custody and complete work

Migration 0193 guards the complete prior retention and report-summary functions,
including owner, ACL and search path. It admits the exact new policy tuple on the
existing job and exposes approximate/applied-method/fallback archive metadata.
There are no new tables, raw grants or instrument allowlists. Existing retained
source/clock/engine/lease/cancellation/current-authority fences are reused.
All completed fitting, panel and fallback passes must reproduce identical captured
base holding keys, units and PVs.

Before pricing, admission includes the N-row predicted output, fitting and panel
PV work, and all-N fallback work if consented. For d factors, k components and M
validation paths, fitting has `6*d + 6*k*k` sibling scenarios. Fitting and panel
each include a base; opted-in fallback adds N paths and another base. PPA row,
option-tree, result-value and 30 MB report bounds remain enforced. Actual counters
charge completed passes; an interrupted attempt marks total work unavailable.
Predicted values are counted separately from ordinary PV calls.

See [verification](verification.md#hybrid-monte-carlo-valuation-d311) for full-reference
oracles and measured workload results. Reducing PV calls alone is not a speedup
claim. Per-valuation fitting isolation, model reuse, EWMA, EVT and operating
backtests remain separate increments. The next delivery item is EVT tail risk,
then daily reporting/rolling backtests; see [the plan](plan.md#remaining-risk-delivery-sequence).
