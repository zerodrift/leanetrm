# Risk scenarios and durable runs

The risk operation uses the same resolved financial environment as the header
date selectors. Effective and knowledge coordinates select retained business
state; the selected market frame supplies explicit valuation/observation cutoffs
and exact observation pins. The existing supported date modes remain unchanged.
An actor's current authority accompanies that environment and is checked
separately. A historical context never restores a historical permission.

## Nested scenarios

The first composition convention is
`absolute_quote_input_parent_additive_rebuild_v1`. Each layer adds a signed,
nonzero amount in the selected live quote's exact price unit to its parent's
effective value. A later layer may select the same factor. For example, +0.20
followed by -0.05 produces a cumulative +0.15 shift. Both the original market
and the +0.20 parent remain independently usable.

`CurveModel.WithShifts(parent, shifts)` produces an immutable child. It validates
factor identity, units, domains, live-input eligibility and the explicit
convention before rebuilding affected nodes. The parent retains its snapshot,
observed facts and computation results. Scenario identities include the base
pins and ordered ancestry; a snapshot ID alone cannot identify a stressed value.
The managed core permits up to 64 layers; the first HTTP/UI consumer permits 16.

Risk under a stress uses that stressed scenario as the base of its symmetric
up/down evaluations. Reported shock P&L is relative to that base. Observed quote
values in input receipts remain the original observations. The receipt includes
the stress layers separately. Returning to a parent or clearing a stress does
not mutate market data or booked trades.

Current factor families are live commodity quotes. Frozen fixings, relative
shifts, direct output-curve shocks, rate/volatility shocks and arbitrary contract
attribute changes are unsupported. A future valuation regime must have a typed,
versioned contract and a consuming model. Date rolls require explicit lifecycle
semantics; they cannot be implemented as an arbitrary date property override.

## User and job operations

The futures risk API supports immediate calculations and durable runs:

| Operation | Purpose |
| --- | --- |
| `POST /api/futures/risk/catalog` | Discover factors under the selected books and financial context |
| `POST /api/futures/risk` | Calculate the explicit bumps around the selected stress |
| `POST /api/futures/risk/jobs` | Admit `{requestId, request}` with an exact retained context |
| `GET /api/futures/risk/jobs` | List recent owned job metadata for recovery after a reload |
| `GET /api/futures/risk/jobs/{id}` | Read the owned job and any completed result under current authority |
| `POST /api/futures/risk/jobs/{id}/cancel` | Request cancellation without publishing partial totals |

Submission retains the exact context, market revision, selected book scope,
observation pins, market frame, stress/bump conventions and managed engine
fingerprint. Empty interactive scope selectors resolve to explicit books before
queueing. An identical request identity and payload return the original run;
different inputs conflict. Changing the UI afterward does not change the job.
The browser discards results when its context or input generation changes.

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
login/principal/tenant/workspace/catalog tuple in `risk.workers`. The catalog and
its reference catalog must belong to that workspace. Use a separate login and registration
for each catalog; the worker cannot choose its scope from a job payload. The
settings secret names the connection-secret path `/run/secrets/risk-worker-connection`.

Each claim covers one complete run across its selected books. The managed
valuation path shares eligible unit prices across holdings and books, rebuilds
only affected dependencies, and aggregates exact quantities/cash. Scenario pairs
are streamed rather than retaining every market graph. The futures operation's
curve work counters include initial market construction and every stress layer;
prior preparation builds are reported separately from valuation scenario counts.

Additional workers increase concurrency across independent runs. Splitting a
single run into distributed scenario batches, distributed result reduction,
cross-run caching and recurring date-rule scheduling remain future work. Those
extensions need workload evidence and their own failure/replay qualification.
Workers claim only jobs matching the managed-assembly fingerprint captured at
admission. A differently versioned worker leaves that job queued. Drain or retain
compatible workers when changing the implementation; submitting a run does not
guarantee that a compatible worker is installed.

The worker rejects reports larger than 30 MB of UTF-8 JSON; the database also
checks its 32 MB retained JSON representation. An oversized result becomes an
explicit failed job, so lease recovery cannot repeatedly compute it forever.

## Scope and verification

The persisted portfolio consumer currently values unprocessed futures execution
P&L/open sensitivity. Full futures settlement PV and variation-margin transitions,
booked generic swap risk and executable options remain separate work. Managed
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
