# PCA diagnostics and hybrid qualification (D308, D309)

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
Hybrid Monte Carlo valuation, per-valuation isolation of fitting
domain failures, EWMA, EVT, broader shock types and operating rolling backtests
remain separate work.

Independent numerical checks cover analytic eigenpairs, singular/constant and
tied factors, unit rescaling, every native-vector reconstruction, low-variance
spread delta, exact quadratic mixed curvature, drift, nonsmooth derivatives,
opposing bindings, whole-portfolio error and exact collinear/mixed directions.
See [verification](verification.md) for the executing owned-database and browser
checks.


<a id="planned-accelerated-hybrid-mode"></a>

## Planned hybrid Monte Carlo valuation

Status: next implementation phase; hybrid Monte Carlo valuation is not enabled
by D308/D309, and no performance benefit has been demonstrated. The current
qualification mode still prices every generated path through full PV. The following requirements describe the
next phase and do not add an executable API policy or establish numerical
eligibility.

### Selection and financial contract

Hybrid describes the valuation method. The same joint market-factor simulation
feeds a fitted delta-gamma PV response; ordinary full PV supplies fitting,
validation and any explicitly permitted full repricing. The shared shock model
and residual factor exposure remain part of the calculation. Computational cost
is measured separately from the method's accuracy and validation requirements.

The user must explicitly select approximate hybrid Monte Carlo valuation,
separate from full-PV Monte Carlo and the current every-path qualification mode. Selecting
hybrid valuation does not authorize fallback. **Allow full-PV fallback** remains a
separate choice, initially off, retained with the calculation and honored by the
UI, API and worker. Failed model, holding, portfolio or fitting checks stop a
no-fallback run with a visible reason. Only explicit fallback consent permits
whole-valuation or whole-portfolio replacement; isolated tail losses must never
be patched into an otherwise approximate distribution.

Reuse the complete shared market-factor identities, native joint shocks, frozen
ordinary PV operation, native deltas and selected PCA curvature. Keep residual
factor moves and their linear exposure. New instrument types enter through their
ordinary PV, declared dependencies, retained materializer and supported
shock/history inputs; there is no product allowlist or assumption that a zero
local Greek proves a globally linear pricer. Eligibility belongs to the captured
valuation and model checks, including all shared bindings.

The new versioned calculation contract must state its approximation, fitting and
validation policies, tolerances, scenario counts and work limits. Validation
panel selection/size, sample independence and deterministic random-stream rules
must be chosen and documented before implementation, then independently
qualified before enabling the mode; this roadmap does not silently choose them. Missing policy must refuse. Changing
scope, market inputs, model, history or seed cannot inherit an earlier passing
result as an accuracy guarantee. Any later model-reuse contract needs its own
qualification.

### Execution, evidence and widgets

Use the fitted model for eligible simulation paths and ordinary full PV for the
explicit validation work and any user-authorized fallback. The report must label
the result as approximate and identify which values were approximated or fully
repriced, which validation paths were priced, and all failures or substitutions.
Keep candidate and reference diagnostics separate. A finite validation panel
cannot prove correctness on unpriced paths or certify their tail VaR/ES; observed
panel errors must not be presented as whole-distribution bounds. Full-reference
benchmark comparisons remain a separate accuracy check.

Risk and Blotter should expose the selected mode and fallback consent, PCA and
residual diagnostics, observed holding/portfolio validation errors, validation
coverage, approximation/fallback classifications, and actual fitting, validation
and simulation work. Retained reports and exports must preserve this evidence
and the selected policy. Direct and queued calculations must agree under the
same source, clock, engine, authority, lease and cancellation fences.

<a id="acceptance-before-enabling-acceleration"></a>

### Acceptance before enabling hybrid Monte Carlo

- Preserve the full native generator, residual exposure and shared factor/scenario
  behavior; test nonlinear residuals, low-variance spreads, kinks and opposing
  bindings with independent pricing fixtures.
- Compare approximate path losses and native portfolio/holding VaR/ES against
  complete full-PV reference runs on qualified workloads, including tail failures
  missed by a small panel. Do not treat one passing seed as general eligibility.
- Prove no-fallback refusal and explicitly consented fallback across immediate,
  queued and retained operations. Reject stale models and incomplete evidence.
- Demonstrate repeatable direct/worker execution and complete work accounting.
  Benchmark the same captured workloads, history, paths and seeds, recording
  unique factors/valuations, fitting and validation PV calls, evaluated work,
  elapsed time, memory and execution environment. Include setup/fallback cost;
  claim speedup only where the complete measurement demonstrates it.
- Verify the approximate/validation labels and consent controls in Risk, Blotter,
  report reopen and export. Schema/release qualification remains separate.

After this phase, the planned sequence is EVT tail estimation and then operating
daily reporting/rolling backtesting. EWMA is a separately selected calibration
enhancement; neither hybrid Monte Carlo valuation nor EVT implicitly selects it.
See [the delivery sequence](plan.md#remaining-risk-delivery-sequence).
