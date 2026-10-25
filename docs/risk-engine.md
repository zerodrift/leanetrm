# Risk scenarios and durable runs

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
and exact observation pins. The existing supported date modes remain unchanged.
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
complete dependencies, unit quantity and currency. `Instrument.Value(context)`
normally delegates to `Instrument.PV(context)`; the risk engine does not select
pricing formulas by instrument type. Its default PV path checks that the returned
value belongs to that exact instrument and scenario. A PV model can return its
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

## User and job operations

The portfolio risk API supports immediate calculations and durable runs:

| Operation | Purpose |
| --- | --- |
| `POST /api/portfolio/risk/catalog` | Discover factors under the selected books and financial context |
| `POST /api/portfolio/risk` | Calculate the explicit bumps around the selected stress |
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

Worker reads use a fresh transaction-local authority context for the retained
actor and delegation. Loading and publication transactions are short; CPU
calculation runs outside them. Current authority and retained authorization
generation govern execution and publication, including revoke/restore changes.
Neither a job identifier nor a cached result grants access. Calculation failures
remain failures and cannot become zero exposure or complete portfolio totals.

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

The interactive consumer covers the current preview's eight futures and sixteen
financial swaps across primary and supplemental market revisions. It values
unprocessed futures execution P&L/open sensitivity and swap PV under fixed rate
inputs. Full futures settlement PV, variation-margin transitions and executable
options remain work. The generic valuation contract does not imply that every
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
stresses, base isolation and exact retained-worker parity. Historical resampling,
volatility/correlation and financial throughput qualification remain work.
