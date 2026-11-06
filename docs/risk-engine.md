# Risk scenarios and durable runs

D307 adds [full-PV Monte Carlo VaR/ES](monte-carlo-risk.md): explicit additive
Gaussian joint-factor generation, exact sample covariance/LDL, reproducible seeded
native shocks, ordinary frozen portfolio PV, empirical tails, convergence and
conditional sampling diagnostics. It shares retained source/worker custody and
Risk/Blotter widgets; named and historical scenario limits remain unchanged.

The API, UI and retained jobs use the same
[connected calculation engine](connected-calculation.md) for ordinary curves,
basis relationships and saved custom projections (D186). Scenarios explicitly
select primitive quote or computed-output coordinates, with contract, point,
parallel and whole-delivery-bucket targets. Integer graph-kernel capacity
measurements are recorded [separately](connected-graph-benchmark.md); they do not
qualify financial compilation, database loading or end-to-end throughput.

The risk operation uses the same resolved financial environment as the header
date selectors. Effective and knowledge coordinates select retained business
state; the selected market frame supplies explicit valuation/observation cutoffs
and exact observation pins. Under [D334](regional-closes.md#historical-views-always-latest),
interactive session-bound reads use the in-force close and its pins, including
prices recorded after a historical knowledge cutoff. Other business facts retain
that cutoff. Retained jobs keep their exact admitted inputs, but the worker does
not bind a close session: replay of close prices first recorded after the cutoff
remains a [documented limitation](regional-closes.md#remaining-work).
An actor's current authority accompanies that environment and is checked
separately. A historical context never restores a historical permission.

The explicit [Live corrections](live-calculation.md) selection advances the
shared header between runs. Each interactive risk calculation still freezes one
resolved frame for its complete base/up/down work; retained jobs never advance.
Instrument PVs and side schedules use the same bound context and normal pricing
operations. Numerical curve memoization does not cache permissions or reuse
instrument PVs across frames.

Custom effective-version selection (D189) resolves before ordinary graph pricing.
An admitted instrument/job keeps its exact recipe version even when a later
timeline activates a successor. Daily custom projections match unresolved resets
to exact delivery intervals; original actuals and full averaging weights remain
contractual. Shared ancestor demands coalesce before loading historical samples,
then only consumed forecasts compile. Primitive and computed-output scenarios use
the same remaining graph; an expired unused forecast cannot block an actual.

## Nested scenarios

The managed [joint scenario operation](joint-scenario-risk.md) (D291) prices
bounded named sets of sibling layers through the same PV path and computes
selected mixed derivatives with explicit three-step agreement checks. Its new
result contracts are admitted by the explicit D295 joint request described in
[joint risk](joint-scenario-risk.md#http-and-retained-runs-d295). Legacy symmetric
requests keep their original wire and multipart behavior.

The composition convention is
`absolute_curve_coordinate_parent_additive_v1`. Each layer adds a signed,
nonzero amount in the selected coordinate's exact price unit to its parent's
effective value. A later layer may select the same factor. For example, +0.20
followed by -0.05 produces a cumulative +0.15 shift. Both the original market
and the +0.20 parent remain independently usable.

`ConnectedScenario.WithShifts(shifts)` produces an immutable child. It validates
exact coordinate identity, units, domains, eligibility and the explicit convention.
A `market_quote` shift moves a live primitive; a `computed_output` shift offsets a
calculated value while preserving its upstream dependencies. Both propagate to
affected downstream consumers. The parent retains its snapshot, observed facts
and computation results. Scenario identities include the base
pins and ordered ancestry; a snapshot ID alone cannot identify a stressed value.
The managed core permits up to 64 layers; the first HTTP/UI consumer permits 16.

Risk under a stress uses that stressed scenario as the base of its symmetric
up/down evaluations. Reported shock P&L is relative to that base. Observed quote
values in input receipts remain the original observations. The receipt includes
the stress layers separately. Returning to a parent or clearing a stress does
not mutate market data or booked trades.

The backend catalog supplies exact scenario descriptors and units. Targets can
name an exact contract, an explicit set of points, all retained points of a
curve that satisfy the selected coordinate contract, or a delivery interval.
Bucket policy
`whole_point_delivery_contained_half_open_v1` requires whole-point containment and
rejects a boundary that splits a point. Results retain the target descriptor and
graph manifest; jobs additionally retain the exact resolved points for worker
reconstruction checks. Parallel/bucket selection does not invent unprepared maturities.
The UI filters offered descriptors, collects explicit amounts and bucket dates,
and displays backend results. It performs no selection or shock arithmetic.

Frozen observations cannot receive primitive quote shifts. Relative shifts,
rate/volatility/correlation shocks and arbitrary contract attribute changes are
unsupported. A future valuation regime needs a typed, versioned contract and a
consuming model. Date rolls need explicit lifecycle semantics; they cannot be
implemented as an arbitrary date property override.

## Instrument valuation

Risk uses the common instrument valuation contract (D95, D145, D148, D181).
`Instrument.Declare(context)` is required and identifies the model version,
complete dependencies, unit quantity and currency. Calculation calls
`Instrument.PV(UnitValuationContext)`, returning a typed `UnitValuation` with exact
money, quantity denominator and originating context. Risk does not select pricing
formulas by instrument type. The common entry point bridges existing `Value` and
rich side-context methods; portfolio validation checks context, currency and
quantity before applying booked size. See [unit instruments and trade bindings](instrument-contracts.md).
A PV model can return its
exact value without inventing a side discount provider. A side that needs payment
discounting still fails when that input is missing.

`Side.GenerateSchedule()` and `Side.PV()` are required. `SidedInstrument.PV()`
combines its independent side valuations and membership coefficients. A universal
`Instrument.GenerateSchedule()` remains a proposed interface; this risk operation
does not create synthetic schedules or settlement state to satisfy it. Futures
use their explicit execution-P&L implementation; full settlement PV is unsupported.

The authorized portfolio adapter materializes currently supported persisted
instruments once, before calculation. It groups them by exact market revision and
valuation measure, retaining their pricing bindings and discount inputs. The pure
calculation then uses the same generic scenario engine for every group. New
instrument pricing models implement the common contracts; supporting their booked
definitions still requires an authorized materialization path.

### Shared market factors

`Instrument.MarketFactors(context)` supplies the shared market inputs consumed by
the instrument's declared pricing dependencies. Portfolio risk takes their unique
union by exact input identity across instruments, sides, books and repeated trade
bindings. A shared dated contract is one factor regardless of how many product
families consume it. A prompt selector resolves to its dated contract under the
pinned context; a new context may resolve another contract without rebinding a
retained run. Curve labels and product types are not factor identities.

Each selected factor creates one common up/down scenario pair. Every dependent
instrument observes that same moved input through its ordinary PV operation;
unaffected work reuses its base value. Factor discovery establishes which inputs
can affect a value, while repricing measures the exposure. A zero local delta
does not remove a declared factor or its nonlinear consumers.

Supported contractual gas profiles enter this same path through retained
programs, including independent side directions and exact quantity ratios.
D350 adds future gas compositions with daily indexed consideration under the
ordinary floating-Side contract; physical and cash dependencies remain independent.
They report `gas_contractual_present_value`; gas-only scopes and mixed portfolios can
run immediate and retained risk without a gas product exclusion. Migration 0165
adds the read-only worker policies needed to load those programs under the same
history, current-authority and lease checks. Started delivery and missing inputs
still fail explicitly. Operational gas valuation and automated-close source
admission remain separate capabilities. Older retained reports keep their
original exclusion evidence and are never reinterpreted.

## User and job operations

The portfolio risk API supports immediate calculations and durable runs:

| Operation | Purpose |
| --- | --- |
| `POST /api/portfolio/risk/scope` | List authorized books and paged trade identities at the selected context, without pricing |
| `POST /api/portfolio/risk/catalog` | Discover factors under the selected books and financial context |
| `POST /api/portfolio/risk` | Calculate the explicit bumps around the selected stress |
| `POST /api/portfolio/risk/history` | Prepare complete aligned dated-factor history |
| `POST /api/portfolio/risk/parametric` | Calculate explicit one-session delta-normal VaR/ES |
| `POST /api/portfolio/risk/parametric/jobs` | Retain a complete statistical job |
| `GET /api/portfolio/risk/parametric/archive` | Page completed owned statistical reports |
| `POST /api/portfolio/risk/jobs` | Admit `{requestId, request}` with an exact retained context |
| `GET /api/portfolio/risk/jobs` | List recent owned portfolio job metadata for recovery after a reload |
| `GET /api/portfolio/risk/jobs/{id}` | Read the owned job and any completed result under current authority |
| `POST /api/portfolio/risk/jobs/{id}/cancel` | Request cancellation without publishing partial totals |

These are the sole portfolio risk HTTP operations. Draft-risk routes for unbooked
futures and swaps remain available. D186 removes the earlier `/api/futures/risk`
routes and fallback execution. Requests select
`portfolio_instrument_repricing_v1`, `exact_rational_portfolio_repricing_v1` and
the explicit pricing policy supplied by the catalog, covering curve, futures,
swap, rate, interpolation and bump conventions. Each bump or stress transports
an exact catalog `factor` descriptor, an explicit `size` and its convention;
series/quote identifiers alone no longer define a request.

The catalog also names the targets of what the selected books hold, which the Blotter's
risk report presets shock ([blotter-desk.md](blotter-desk.md), Risk report inline).
`heldContractTargets` has one whole-contract target for each distinct graph point that a
held instrument's declared dependencies are priced from, on the coordinate that publishes
that point: a primitive market quote, or a computed output such as a basis curve's.
`heldCurveTargets` has the whole-curve target of each curve those points are on, on the same
coordinate. Both keep the catalog's order and name only targets the catalog offers. They are
resolved from the declarations and graph bindings of the positions the run would value, so a
position the close cannot value contributes nothing of its own. A held point that is no whole
contract, such as a published custom curve's output, or that no coordinate publishes, such as
a frozen final settlement, is named by its label in `heldContractsWithoutTarget`, and a curve
that no whole-curve target covers in `heldCurvesWithoutTarget`; no broader or narrower target
stands in for either. Every `excludedPositions` entry names the book the position is held in
(`bookId`), in the catalog, the report and a job's retained frame and result, and a queued
run leaves out only positions held in the books its list names.

A holdings-detail request takes at most sixteen bumps. The Blotter's risk report makes a
larger preset run in parts of at most sixteen, each its own request, or its own queued job,
at the catalog's one retained context. Each factor's results are independent, so nothing
combines the parts, and a part that fails is reported as failed. The results and the download state
the preset, its size for each price unit and the held prices or curves it does not shock.

Each queued part of a run made in parts states its run (D222): `POST /api/portfolio/risk/jobs` takes
`{requestId, request, run:{id, part, parts}}`, and the job's retained frame keeps the run's identity, the
part and the part count, checked before the inputs are prepared (1 ≤ part ≤ parts ≤ 256, sixteen
shocks to a part and 4,096 in all). Every queued job's frame also keeps `scope`, the books and
portfolios its request was asked for, as chosen. The job store admits each part of a run once, and every
part at the run's one retained context and one close (frame), for its one scope, with its one part count,
serializing parts of one run sent at once; anything else is refused with `risk_job_run`, and a request
identity sent again with another run, or a frame without the `scope` this release writes, is
`risk_job_conflict`. Every job read, the recent-job list included, states its job's `run`, or `null` for a
job queued on its own, its `scope` and `shocks`, the number of its request's bumps, so the job history
shows a run's parts as one run after a reload too, and how many shocks it ran. The run's results are shown in the Blotter desk only against a read of the
market context, close and books they were run at; a report's `snapshotId` is the frame of the close its
request named.

The shared pricing policy's `bumpBasis` and `bumpTicks` select the generated-close
operation's automatically constructed bumps. Interactive requests supply each
coordinate bump size and `absolute_curve_coordinate_symmetric_v1` convention
explicitly; these govern their up/down evaluations and are not rescaled by
`bumpTicks`.

Both holding detail and book summaries retain rational base/up/down values,
shock P&L, delta and gamma. Exact wire values use decimal strings when
representable and fraction strings otherwise. Separate display values use twelve
decimal places and ties to even; display rounding never changes retained values.
Futures execution P&L and swap present value remain separate book/currency
measures. An unaffected market partition retains its base values with zero
sensitivities for the selected factor, including every requested holding row.

Submission retains the exact context, market revision, selected book scope,
observation pins and frozen-input uses, market frames, stress/bump conventions and managed engine
fingerprint. It also retains the positions the run leaves out because its base close
cannot value them (D220); the worker leaves out exactly those, each a held position of
the scope, and reports them in the result. Empty interactive scope selectors resolve
to explicit books before queueing. An identical request identity and payload return the original run;
different inputs conflict. Saved managed projections additionally retain their
published version/output descriptors and exact dated sample observations
(0076–0077). Migration 0081 retains a compiled graph manifest for every prepared
portfolio market and typed scenario axes with their resolved points. Persisted
contracts have exact foreign keys; compiled recipe coordinates are anchored to
the retained graph manifest rather than masquerading as persisted definitions.
The migration refuses any retained risk jobs, including completed ones, pending an
explicit operator archive/reset. It does not delete or rewrite retained history.
Those samples are separate from the primary
snapshot: multiple dates may retain different observations of the same series.
Historical samples are frozen; current quote factors remain explicit. Workers
read the captured IDs and reject a different reconstructed manifest. Changing
the UI afterward does not change the job.
Published-source chains retain every ancestor declaration in that graph manifest
and all native sample pins through the immutable exact-version relationships
(0082). A shock to a parent output propagates through its descendants; a shock to
the child output preserves the parent's inputs. Each market also explicitly
retains its source execution-eligibility convention. Workers use that convention
to reconstruct source selection, separately from the portfolio risk convention;
missing, unsupported or conflicting conventions reject.
The browser discards results when its context or input generation changes.

See the [cutover checklist](connected-calculation.md#adopting-the-schema-cutover)
before qualifying an installation baseline.

Supplemental swap market revisions carry explicit frames under the same retained
context and valuation instant. Migration 0074 retains their observation uses and
curve memberships through exact-version foreign keys. Workers reconstruct those
captured inputs instead of selecting fresh manifest defaults. The report's global
scenario identity includes all selected market parents and the ordered stress
stack; local partitions can have different numbers of affected layers.

The durable states are queued, running, completed, failed,
cancellation_requested and cancelled. Multiple worker processes claim independent
runs. Expired ownership can be reclaimed with a new lease token, while the input
manifest remains unchanged. A stale worker cannot publish. Cancellation of active
work is confirmed only after acknowledgment; lease expiry alone does not prove
that computation has stopped. The same worker may acknowledge its stopped,
cancelled attempt after its lease expires, but cannot publish a result then. A
crashed worker with an unconfirmed cancellation remains cancellation-requested;
this first version has no cluster process-death confirmation workflow. Results
and terminal state publish atomically.

Heartbeat and cancellation remain available while an input-loading transaction
holds its attempt-context foreign key to the job. Migration 0137 uses
`FOR NO KEY UPDATE` for attempt-control and cancellation locks: these serialize
job-state writers while allowing that foreign key's `KEY SHARE` lock. Claiming
still uses `FOR UPDATE SKIP LOCKED`; lease tokens, authority generations,
cancellation acknowledgment and publication checks are unchanged. The disposable
risk database check keeps a real `risk.bind` transaction open while independently
renewing and cancelling it, and verifies stale-token and cancellation-first
publication refusal. No lease, input-loading or calculation deadline is extended.

Migration 0138 permits risk workers to read retained swap quantity intervals
through the same authorized reader as interactive requests, with current attempt
fencing before and after the read. Settlement and contract-bridge bounds use
those exact intervals; private unguarded entry points are not granted to workers.
Risk holdings select the latest RLS-visible trade revision before applying book
scope and loading its terms. Historical trade reads retain all visible revisions;
a book transfer cannot resurrect the prior revision in its former book. A selected
revision missing required joined records refuses the read rather than silently
omitting that holding; the completeness check uses the same SQL statement.

Worker reads use a fresh transaction-local authority context for the retained
actor and delegation. Loading and publication transactions are short; CPU
calculation runs outside them. Current authority and retained authorization
generation govern execution and publication, including revoke/restore changes.
Neither a job identifier nor a cached result grants access. Calculation failures
remain failures and cannot become zero exposure or complete portfolio totals.

### Individual trade scope (D292)

Catalog and calculation requests accept optional `tradeIds`. Absent or null
preserves whole-book behavior and the legacy retained JSON shape. A nonempty
array selects at most 256 distinct, nonzero trade IDs within the selected
portfolio/book intersection and exact retained context. Empty, duplicate,
unknown or out-of-scope IDs refuse with `risk_trade_scope`; nothing widens to
all trades. Scope discovery accepts `{context, bookIds, portfolioIds, tradeIds,
offset, limit}` with pages of 1–100 rows. It returns parent books, trade choices,
selected choices even outside the page, availability, total and next offset.
Availability describes readable booking identity, not successful valuation.

Selection occurs before terms, required-source checks, market preparation and
PV, including unvalued-position retries. Every selected trade contributes all
its held bindings or explicit valuation exclusions; a missing trade cannot
silently disappear. Futures, swaps/heat-rate, PPAs, physical gas/crude/LNG and
futures options share this selection contract. Factors still come from the selected
instruments' declared shared dependencies, with no browser financial logic.

The request, retained frame scope and completed report capture the same trade
IDs. Worker replay verifies their agreement; existing multipart scope equality
prevents parts from mixing subsets. Legacy all-trade parts retain their original
shape and can continue without rewriting historical frames. Migration 0169 adds
selected complete-source reads and a minimal PPA identity projection fenced by
current authority, bound history and an active worker claim. It grants no direct
worker access to PPA booking rows. Existing installations require a separately
qualified schema release; local checks do not establish production deployment.

## Workers and horizontal execution

The same application binary provides:

```sh
dotnet OpenEtrm.Api.dll --risk-worker once
dotnet OpenEtrm.Api.dll --risk-worker serve
```

`OPENETRM_RISK_WORKER_CONFIG` names a private JSON file with explicit
`connectionFile`, `leaseSeconds`, `pollSeconds` and `timeoutSeconds`. The
connection file belongs to a registered, scoped risk-worker login. It must not
use runtime, authenticator, migration or database-owner credentials. Credentials
and installation configuration stay outside the public repository.

The optional [Swarm worker definition](../deploy/risk-workers.swarm.yaml) runs
replicas on nodes labeled `openetrm.risk=true`. It expects an existing private
database network, explicitly named external secrets, an image digest and a
chosen replica count. Swarm places/restarts processes; PostgreSQL coordinates
claims, leases and publication. It does not create financial contexts or aggregate
financial values. Installing this template does not qualify an existing cluster,
change database schemas or authorize deployment.

Provision the dedicated login as a member of `openetrm_risk_worker` only. The
database owner registers its enabled service principal, workspace membership,
current `ReadMarketData`, `ReadTrades` and `RunScenario` permissions, and the exact
login/principal/tenant/workspace/catalog tuple in `risk.workers`. The market catalog
must belong to that workspace, and its reference catalog must be currently readable
there through ownership or an explicit read-only share. Use a separate login and registration
for each catalog; the worker cannot choose its scope from a job payload. The
settings secret names the connection-secret path `/run/secrets/risk-worker-connection`.

Each claim covers one complete run across its selected books. The managed
valuation path shares eligible unit prices across holdings and books, rebuilds
only affected dependencies, and aggregates exact quantities/cash. Scenario pairs
are streamed rather than retaining every market graph. Graph work counts actual
node evaluation and reuse under the base and stress scenarios. Preparation reports
`SourceCompilations` and `DeclaredSourceNodes`; compiling source topology does not
evaluate prices. The existing futures materialization build remains included in
evaluated preparation work, separately from the global valuation scenario count.
Unit candidates use all unique values times that global count;
unit calls and curve work count actual local evaluations, including reuse across
unaffected partitions.

The existing [automation worker](automation.md) schedules typed operations and
controls bounded Swarm workloads. This financial worker is a separate, trusted
consumer with narrowly scoped database reads. It does not launch arbitrary
containers or grant database credentials to agent workloads. Generated pilot-close
jobs produce mixed futures/swaps reports (D169). Their existing primary-market
contract and the interactive operation now reuse the portfolio materialization and
calculation path; generated-close publication remains a separate typed operation.

Additional workers increase concurrency across independent runs. Splitting a
single run into distributed scenario batches, distributed result reduction,
cross-run PV/result caching and recurring scheduling of these interactive risk
requests remain future work. Qualified curve memoization is implemented in the
shared [live calculation path](live-calculation.md). These extensions need
workload evidence and their own failure/replay qualification.
Workers claim only jobs matching the managed-assembly fingerprint captured at
admission. A differently versioned worker leaves that job queued. Drain or retain
compatible workers when changing the implementation; submitting a run does not
guarantee that a compatible worker is installed.

The worker rejects reports larger than 30 MB of UTF-8 JSON; the database also
checks its 32 MB retained JSON representation. An oversized result becomes an
explicit failed job, so lease recovery cannot repeatedly compute it forever.

## Scope and verification

The interactive and retained consumers include futures, financial and heat-rate
swaps, qualified PPAs, fixed-volume crude holdings, gas compositions with fixed or
qualified indexed consideration, the bounded
[LNG cargo](physical-lng.md), and [listed American futures options](futures-options.md)
across exact primary and supplemental market revisions. They preserve the distinct native
valuation measures, including futures execution P&L/open sensitivity and swap PV
under fixed rate inputs. Options require the explicit tree, calibration and
held-volatility policies described in their contract. Full futures settlement PV,
variation-margin transitions and broader option products remain work.
The generic valuation contract does not imply that every
instrument type has an implemented pricer or persisted booking adapter. Managed
checks exercise common instrument PV, nonlinear scenarios and independent
repricing oracles; these do not establish production capacity.

The isolated verification entry point is:

```sh
python3 tools/run_preview.py --pg-bin /path/to/stock-postgres/bin \
  --dotnet /path/to/pinned-sdk/dotnet --risk-jobs-check \
  --browser-check --browser /path/to/chromium
```

This allocates a disposable database and synthetic inputs. The shared
`tools/check_all.py` workflow includes the risk suite. Verification results and
remaining deployment limits are recorded in [verification](verification.md).


Computed-curve combinations (D193) use the same connected plan in previews,
booked PV, portfolio risk and retained jobs. Their explicit panel weights do not
allocate PV exposure: base/up/down instrument PV determines sensitivities.
A primitive quote shock follows all declared paths; a computed-parent output
shock follows its weighted edges; a child-output offset changes that coordinate
with upstream inputs held constant. These axes answer different questions and
must not be summed as independent exposures. Synthetic integration checks book
a two-parent combination and verify independent parent/root deltas, nested
stresses, base isolation and exact retained-worker parity. Retained aligned
history, covariance diagnostics and full-PV historical/Monte Carlo risk are
implemented under the separate [statistical contracts](risk-factor-history.md).
Cross-run sample reuse, stochastic volatility/correlation shocks and financial
throughput qualification remain work.


## Shared automated-close pricing

Existing-close jobs use the same `PortfolioValuation` preparation and connected
risk evaluator as interactive portfolio pricing. The separate close valuation
engine is removed. The close adapter retains complete-book exact-bundle admission,
valuation-instant PPA knowledge, native factor pins, and the existing report
schema and publication fence. Generated-close and existing-close operation IDs
and permissions remain distinct.

The shared report conversion retains PPA input receipts and separate known and
estimated overdue balances. Work admission counts each unique instrument coverage
and only its affected native scenarios, using the same counter for native and
connected preparations.

The existing-close storage contract does not retain connected recipe/sample
manifests. Those sources remain explicitly unsupported for this operation even
though the shared engine can price them for other consumers. This consolidation
does not authorize source reselection on retries or widen retained job inputs.

Verification passed the 646 managed core checks, API and focused close checks,
existing-close worker integration, generated-close integration, and connected
portfolio regressions with synthetic inputs on disposable PostgreSQL databases.
Independent review covered scope authorization, retained inputs, numerical work
counting and publication fences.

Work reports preserve the same volume computations, unique side calculations,
resolved curve months, calibrated blocks and interval row counts through native
and connected execution. `rebuiltCurveNodes` and `reusedCurveNodes` count native
curve receipts; connected execution does not manufacture those receipts.
`curveGraphEvaluatedNodes` and `curveGraphReusedReads` retain the connected graph's
actual work, including base preparation, in portfolio and durable close reports.
These are separate from schedule graph node/read counters. The twelve-month
lease fixture requires at least 72 curve graph evaluations (24 base source
factors plus their 48 signed shocks); graph structure may add further work.

When unavailable prices require position-level qualification, one holdings read
reuses complete dated-source history only within that authorized operation and
exact definition, certified context and valuation instant. Individual observations
are still selected separately. Retained jobs bypass this reuse and load their
exact admitted pins; failed reads are not retained.

## Risk navigation

The [Risk results workspace](risk-workspace.md) owns run setup and opens existing
reports with the same editor and graphical report widget as the Blotter. Delta,
gamma and up/down P&L retain backend native units, separate measures/currencies,
exclusions and captured contexts. Its recent authorized job directory is bounded;
missing parts remain incomplete, and links grant no authority. Unknown multipart
submissions retain every original request identity before dispatch and reconcile
after reload. Worker and calculation diagnostics are expandable.
[Aligned history](risk-factor-history.md) and [delta-normal VaR/ES](parametric-risk.md)
now add statistical calibration, native distribution/contribution diagnostics and
a completed-report archive. [Hybrid Monte Carlo valuation](pca-hybrid-risk.md#hybrid-monte-carlo-valuation-d311)
is separately selected and approximate, with observed full-PV validation and
explicit opt-in fallback. EVT and rolling backtesting remain subsequent work.

Queueing writes to PostgreSQL. A separately configured managed risk worker claims
compatible work; submitting a request does not provision a worker. The queue
carries a second job kind, `vol_surface_fit_v1`, which fits and records a
[volatility surface](vol-surfaces.md#fit-jobs) (D304) under the same lease, attempt,
cancellation and security-generation rules; its actor needs `PublishMarketData`,
`ReadTrades` and `ReadMarketData` instead of `RunScenario`. `GET
/api/portfolio/risk/jobs` and the Risk page list portfolio risk jobs by convention,
so fit jobs do not appear there. The Swarm
stack in `deploy/risk-workers.swarm.yaml` places replicas on nodes explicitly
labeled for risk and uses the exact API image. The shared infrastructure view is
an approved node projection, not a risk-worker replica or queue-health monitor.
Real Swarm deployment and recovery remain unqualified here.

The page assistant has no general Python interpreter tool. A future code sandbox
needs a separately agreed execution and isolation contract; the existing managed
risk worker is not a general code execution service.

### Guided statistical runs (D301)

**Parametric VaR** uses the same portfolio/book/trade scope. Its visible starting
profile and end-of-day price-type controls are documented in the
[VaR walkthrough](risk-factor-history.md#guided-var-setup-d301). A Run click prepares
aligned history automatically and reuses only an exact matching prepared input.
Official closes are initially required; approved alternatives are explicit and
flagged per endpoint. Saved reports include the same bounded factor grid and
correlation/covariance display, independently of the current setup.

D305 adds [full-PV historical VaR/ES](historical-risk.md) through this same captured
scope/history/PV and retained-worker infrastructure. Guided setup selects the
method, automatically aligns history, and optionally compares the identical study
to sample-mean delta-normal. Historical reports graph empirical probabilities and
losses, expose book/trade quantile/tail allocation and drill through every sample's
factor moves and native PV bindings. The completed archive discovers statistical methods;
current authority is checked for report reads and export. Rates, time and current
option calibration remain frozen. Historical repricing does not imply hybrid
Monte Carlo valuation, EVT or an operating backtest workflow.

## PCA / hybrid qualification (D308)

The optional [Monte Carlo analysis](pca-hybrid-risk.md) displays standardized
loadings, explained market variance, ideal residual/cross covariance and
reconstruction diagnostics. All native factor delta exposure remains, including
low-variance residuals. Actual quantized PC vectors define selected diagonal and
mixed curvature; every generated path still receives full PV. Comparison widgets
show native VaR/ES, path/tail error, whole-valuation fallback and measured reference
versus added fitting work. D309 starts **Allow full-PV fallback** unchecked.
Failed qualification stops the run unless the user selects that option; an opted-in
failed shared fitting batch falls back every valuation with its reason. The
selected policy is retained through queue/replay and report export. This mode
qualifies only the captured run and adds work; it does not enable hybrid
Monte Carlo valuation or certify future tails.


## Hybrid Monte Carlo valuation (D311)

Choose the explicit approximate method in VaR setup; review simulation and
validation path counts, components and seeds. Fallback starts unchecked. Failed
checks stop the run unless complete full-PV fallback was explicitly permitted.
The main distribution contains model estimates; the separate validation section
shows observed full PV, sample-only errors and VaR/ES, coverage, PCA/residual
matrices and work. A consented fallback is labelled full PV with its reasons.
There is no inherited binomial interval or whole-tail accuracy guarantee.
The common archive, retained reopen, Blotter and export preserve method and
consent. See [the walkthrough and exact contract](pca-hybrid-risk.md#hybrid-monte-carlo-valuation-d311).
