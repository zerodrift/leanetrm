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
fingerprint. Empty interactive scope selectors resolve to explicit books before
queueing. An identical request identity and payload return the original run;
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
The browser discards results when its context or input generation changes.

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
cross-run caching and recurring scheduling of these interactive risk requests remain future work. Those
extensions need workload evidence and their own failure/replay qualification.
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
