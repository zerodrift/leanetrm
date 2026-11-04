# PCA diagnostics and hybrid qualification (D308)

Monte Carlo setup can include **PCA diagnostics and hybrid comparison**. This
mode retains the complete full-PV reference and fits a black-box delta-gamma
candidate against the same captured portfolio and native shocks. Every generated
path receives full PV. This is qualification work with additional measured cost;
it does not accelerate the run or certify future seeds, portfolios or tails.

## Setup and report

The visible starting selection uses up to three curvature directions, lowered
when required to fit the selected scope's work budget, one native
trading tick, relative derivative agreement 0.0001, native absolute loss-error
allowance zero and relative loss-error allowance 0.001. All choices are explicit
request inputs. Users can select one to eight directions, bounded by the complete
held factor count. The path limit includes both reference and fitting work;
changing the selected directions can lower the visible allowed path count.
History keeps the existing EOD and ordered price-type controls.

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
- `full_reference_qualification_v1`;
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

A failed derivative, holding loss or standalone VaR/ES comparison falls back the complete shared
unit-valuation identity across all its bindings, measures and currencies, for
every path. Errors cannot disappear by netting opposing holdings first. The
remaining hybrid portfolio is then checked path by path and at its empirical
VaR and ES in each native partition. Failure falls back the entire portfolio.
Only full-reference quantile/ES tail membership defines the reported tail-error
diagnostic; full-path comparisons remain required.

Fitting currently executes as one shared portfolio batch. A numerical or
pricing-domain failure in any fitting scenario therefore causes explicit full-PV
fallback for every valuation in that portfolio; healthy candidates are not
separately qualified in that failed batch. Its report retains fallback identities
and reasons. Completed fitting counters are retained; an interrupted fitting
attempt without complete counters marks total work unavailable rather than
reporting invented savings. Exact-arithmetic exhaustion in optional candidate,
hybrid or standalone empirical reductions also falls back the optional comparison;
it does not discard a successful full-PV reference. Missing holdings, changed scope/base, authority,
history and cancellation failures remain fatal. Any failure on a generated
full-reference path still refuses the complete Monte Carlo run.

## Retention, limits and remaining work

The optional typed policy belongs to the existing Monte Carlo request and study
identity. Jobs retain it with the existing source/clock/engine/lease/attempt
fences; direct and worker operations regenerate the same numerical analysis.
Actual execution-cost counters can differ with warm versus fresh preparation
caches. Migration 0191 guards the complete prior private retention helper definition, owner, ACL
and search path, admits only the explicit complete policy and adds no table or
raw grant. Old requests with no analysis retain their original numerical contract
and study identity. Authorized report export retains the full reference, actual
fitting directions, fitting PVs, models and qualification results.

Admission counts full reference base/paths plus fitting base and
`6 * factorCount + 6 * componentCount²` sibling scenarios before scenario PV.
It applies the existing complete value, PPA-row and option-tree budgets.
Additional counters subtract shared parent-graph, materialization and cached volume
work already charged to the reference; new fitting PV and quantity applications
remain charged. Report size remains bounded at 30 MB. Calculation counters distinguish reference and
additional work; no performance or net acceleration claim follows from these
comparisons. True approximate acceleration, per-valuation isolation of fitting
domain failures, EWMA, EVT, broader shock types and operating rolling backtests
remain separate work.

Independent numerical checks cover analytic eigenpairs, singular/constant and
tied factors, unit rescaling, every native-vector reconstruction, low-variance
spread delta, exact quadratic mixed curvature, drift, nonsmooth derivatives,
opposing bindings, whole-portfolio error and exact collinear/mixed directions.
See [verification](verification.md) for the executing owned-database and browser
checks.
