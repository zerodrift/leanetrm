# Jobs, workers and infrastructure

Status: bounded local implementation. Real Docker Swarm execution, recovery and
production deployment have not been qualified. This document describes the
implemented slice of the broader [execution contract](architecture.md#11-durable-execution-and-failure-semantics).

## Implemented operations

Jobs & Infrastructure is ordinary navigation for authenticated workspace members.
People see their own jobs; a current workspace `ManageJobs` grant permits managing
other jobs in that workspace. An explicitly registered `ManageAllJobs` installation
operator can manage jobs across scopes. Shared node projections contain approved
names, health, availability, capacity and observation times; they contain no host
addresses, credentials, raw Docker configuration, job inputs or logs.

The managed API exposes:

| Operation | Contract |
| --- | --- |
| `GET /api/jobs?page=0&collectionPage=0` | Authorized automation and collection jobs, independently paged at 100 rows, recent run metadata, profiles and shared infrastructure |
| `POST /api/jobs/create` | Exact request identity and explicit execution terms |
| `POST /api/jobs/update` | Amend an idle active/paused job's name, next due time, expiry, interval and run limits, with its expected version |
| `POST /api/jobs/control` | Pause, resume, cancel or run again, with expected job version |
| `POST /api/jobs/collection/update` | Amend an existing collection job's name, schedule switch and inclusive delivery-day offsets, with its exact generation |
| `POST /api/infrastructure/availability` | Pause/resume scheduling or drain an idle approved worker node, with exact node version |
| `POST /api/infrastructure/resolve` | Preserve observed availability and fence a previous uncertain conditional node update |

All writes use the existing authenticated identity transaction and request-token
boundary. Retain the original request identity and body after a lost response.
Exact retries return the retained result; changed payloads conflict. A known first-attempt
rejection can discard its pending request. Once a response is uncertain, later
authorization/policy rejections preserve that original identity until reconciled. The UI retains uncertain requests in
session storage, separately for each signed-in workspace/account.

New job entry starts collapsed. Click a job name or its explicit Edit/View details
action to open its saved terms. The backend supplies whether editing is
currently available and the read-only reason. Owners and current job managers use
the same update operation; neither the displayed role name nor browser controls
grant authority. Updates retain ownership, delegation, operation and the exact
approved execution-profile version. They preserve active/paused state and existing
runs, increment the job version and retain the request, response and audit action.
Stale versions and changed replays conflict. Current actor and owner execution
authority are checked again after lock waits, including before a saved response
is replayed. Running work and physical work awaiting cleanup block editing.
Terminal, blocked and expired jobs remain inspectable without amendment controls.
An update cannot reuse a due instant already retained for that job's occurrence.

Job scheduling is a current operational view, independent of the market As of
selection. Entry and editing display the user's time zone and use the managed
resolver for repeated or missing wall clocks; persisted instants and elapsed
recurrence remain UTC. Editing does not create a historical market-data workflow.

The supported schedule is explicitly UTC, once or a fixed interval of 60 through
31,536,000 seconds. Its selected policies are `run_once` for misfires and `forbid`
for overlap. Recurrences advance to the first future instant on their original
interval grid. Expiry, timeout and maximum attempts are required. The owner has
at most 64 outstanding jobs across workspaces, including paused/blocked jobs and
physical runs awaiting termination. Reactivation acquires the same capacity.

Two operations are executable:

- `worker_check` verifies that the separate .NET worker can claim and complete a
  durable job through its restricted database identity. It performs no business
  calculation. It permits one through three fenced attempts.
- `swarm_job` launches the approved image's own entry point using `bounded_exit_v1`.
  A zero-exit container task means this process operation completed; it asserts no
  financial result or committed business effect. Only one create is admitted per
  run. The client cannot supply commands, environment variables, credentials,
  mounts, ports or arbitrary Docker options.

## Delivery-price collection schedules

The same Jobs page lists existing `power_data.jobs` separately, with provider,
paused/scheduled/disabled state, retained cadence, delivery-day offsets, source
count and latest attempt. These rows run through the registered collection
service, rather than the automation scheduler or an arbitrary container job.
Viewing them requires current market-data access or `ManageJobs` in the exact
registered workspace/catalog scope. Approved metadata excludes collector login,
credentials, source configuration and provider responses.

A current scoped `ManageJobs` grant permits editing the name, schedule switch and
inclusive UTC calendar-day offsets. The supported offset range is -31 through -1;
the UI obtains these bounds from the backend. The registered service, source
bindings, operator-enabled gate and qualified `0 8 * * *` UTC cadence are fixed.
Pausing the schedule does not disable an explicit operator collection. A disabled
operator gate, ineligible service, unqualified sources or active collector worker
makes the settings read-only and supplies a reason. No job or schedule is enabled
by this migration or by opening its editor.

Updates use the existing collector execution fence, lock the exact job and
revalidate caller scope, job-management authority and registered service/source
eligibility after waits. A stale generation or changed replay conflicts. The
immutable command binds the exact job/catalog and retains before-terms plus a
control-only receipt. Replays require current authority; they do not return cached
source or service details. The browser validates the receipt, preserves an
uncertain request across reload or later denial and keeps reconciliation visible
outside collapsed editors. A confirmed write followed by a failed directory read
requires only a read refresh.

This operation collects delivery prices under the existing whole-batch,
exact-reference admission rules. Collection is idempotent (D238, migration
0118): an attempt must still cover every operating-day interval, but only
intervals whose price or provider revision differs from the latest published
revision enter a new batch. Repeating an identical collection succeeds and
publishes nothing, so recorded prices keep their original knowledge time and
period reads at any cutoff are unchanged. It does not create a forward curve, publish a
market close or perform valuation/risk. Operator provisioning and collection
worker installation remain separate reviewed steps.

## Page assistant reads

The page assistant reads Jobs & Infrastructure under `ReadJobs` (D239, D241,
[devops-assistant.md](devops-assistant.md)). Migration 0119 adds
`automation.owned_jobs(page)` and `automation.owned_job_history(job, before)`, which
return only jobs the reader owns, or for an agent's delegated context the jobs its
delegator owns, even when the reader manages other jobs. Another person's job is
`job_not_found`. They return each job's terms, state, version, editability and runs,
its audit actions and the named schedule terms of its command receipts, 25 jobs to a
page. A trigger keeps every job's owner, tenant and workspace fixed
(`job_owner_immutable`). They never return a request identity, service,
worker, fence or lease fields, and the database holds no container output for them
to read. They change nothing. The agent reader role holds `ReadJobs` and no
permission that creates, changes or cancels a job.

## Identity and database boundaries

Migrations 0035 and 0036 seed no workers, operators, profiles, credentials or jobs.
Every automation table has enabled and forced RLS. Runtime accounts cannot write
the tables directly. Narrow database operations derive actor, owner and scope
from a verified transaction context; caller-supplied ownership is rejected.

`RunJobs` is included in the six human role templates, not in the agent reader role
that only agent principals hold. `ManageJobs` is included
in the access-administrator template. `LaunchWorkloads` and `ManageInfrastructure`
require explicit grants. Infrastructure mutation additionally requires an
unexpired `automation.installation_operators` registration with capability
`ManageInfrastructure`; workspace administration alone is insufficient. Agent
infrastructure delegation is rejected in this slice.

An agent scheduling through a verified agent context needs its own enabled
membership and role grant, an exact current delegation permitting `RunJobs`, and
a delegator still entitled to both `RunJobs` and `CreateDelegations`. Image launch
also requires `LaunchWorkloads` on both identities and the delegation. Jobs retain
the agent owner and exact delegation. Disabling membership, revoking authority,
expiry or disabling a profile prevents later execution/publication.

The trusted worker login belongs only to `openetrm_scheduler`, without superuser,
RLS bypass, application/authenticator roles or ownership of application tables.
`automation.workers` binds its actual PostgreSQL `session_user` to one enabled
service principal, workspace and kind (`scheduler` or `controller`). That service
principal needs current `RunJobs` authority. It cannot choose another owner or
claim another workspace. The controller additionally needs the exact enabled
`automation.clusters` registration bound to its login.

The authorization epoch serializes admissions against explicit revocation.
Functions recheck current service authority after lock waits, including natural
grant expiry. Claims use database time, a lease, worker identity and monotonic
fence. Expired/superseded claims cannot publish results. Cleanup of previously
owned compute is separate from permission to start work.

## Image and maintenance contract

An immutable execution-profile version records its workspace, name, image digest,
architecture/variant, internal Swarm network, workload-node pool label, CPU,
memory and process bounds, and `bounded_exit_v1` protocol. Only its enabled flag
can change. Approval/provisioning is an installation operation; there is no public
profile-authoring endpoint yet.

The controller checks the registry descriptor is a single-platform manifest,
checks the manager's locally available exact digest and platform, and rejects
inherited image volumes. Index digests and unavailable qualification evidence
are rejected. Workload placement requires Linux, the approved architecture,
worker role and exact pool label. The network must be an internal Swarm network
labelled `openetrm.profile` with the exact profile ID.

Workloads use UID/GID 1654, read-only root filesystems, no additional privileges,
all capabilities dropped, a 16 MiB temporary filesystem, bounded local logging,
resource limits and no task restart policy. They expose no ports. They receive
no database, model-provider, application or Docker credentials. The supported
Engine protocol is API v1.47; actual Engine enforcement remains a qualification
requirement. Containers are not a qualified hostile-code isolation boundary.

The durable one-use launch admission commits before Docker submission. A lost
create response reconciles the deterministic owned service name; it never causes
another create. A known service ID cannot be replaced by a same-name clone.
Inspection rejects extra security/network authority outside the retained spec.
Task evidence must match the exact service/profile; successful completion also
requires an identified container and zero exit code. Owned terminal services are
removed individually, without pruning unrelated resources.

An uncertain admitted create can remain unresolved indefinitely. Missing service
and task history do not prove that partitioned compute stopped. Such work retains
its reservation, displays an unknown/intervention-required outcome, and blocks
maintenance. There is no qualified external stop-attestation recovery operation
yet. Fair polling prevents one uncertain run from starving other jobs or cleanup.
Swarm can reschedule physical tasks independently; this slice does not grant
those tasks business authority or establish exactly-once business effects.

Availability commands require a fresh node observation and exact Docker version.
Only ready, explicitly labelled workload workers can be changed. Managers and
unregistered infrastructure are not eligible. Drain additionally requires no
nonterminal tasks on the target node. It does not implement database failover.
Launch and maintenance admission share a cluster lock. Pending maintenance blocks
new launch admission; prepared, unremoved workloads block new maintenance.

An uncertain node update blocks subsequent cluster maintenance. An authorized
operator can preserve the currently observed availability using its current
version and a reserved maintenance-fence label. Only confirmation of the higher
version and matching label completes reconciliation, fencing delayed older
conditional updates. Original intent and resolution provenance remain recorded.
Committed external dispatch is the authorization boundary: an already admitted
bounded command can finish after revocation; later admissions are rejected.

## Running the separate services

Use the pinned SDK in `global.json`. For either worker kind:

```sh
dotnet restore src/OpenEtrm.Worker --locked-mode
dotnet build src/OpenEtrm.Worker --no-restore
dotnet src/OpenEtrm.Worker/bin/Debug/net10.0/OpenEtrm.Worker.dll
```

Supply protected files and explicit settings outside the repository:

| Setting | Required value |
| --- | --- |
| `OPENETRM_WORKER_CONNECTION_FILE` | Restricted PostgreSQL login connection string with one explicit host; network connections require `SSL Mode=VerifyFull` and the selected root certificate |
| `OPENETRM_WORKER_POLL_SECONDS` | Integer 1–60 |
| `OPENETRM_WORKER_LEASE_SECONDS` | Integer 5–120; controller additionally requires poll + six HTTP timeouts + 15 seconds |
| `OPENETRM_SWARM_CONFIG_FILE` | Controller only: protected JSON shown below |

```json
{
  "endpoint": "https://manager.synthetic.invalid:2376/",
  "clusterId": "syntheticcluster00000000001",
  "clientCertificateFile": "/run/secrets/swarm-client.pem",
  "clientKeyFile": "/run/secrets/swarm-key.pem",
  "rootCertificateFile": "/run/secrets/swarm-root.pem",
  "httpTimeoutSeconds": 5
}
```

This is a synthetic schema example, not deployable configuration. The controller
verifies cluster identity, the server hostname and its private certificate chain,
uses mutual TLS and refuses redirects. Its management certificate is privileged;
only this trusted process receives it. The scheduler has no Docker connection.

For an image build, publish the worker into a new external staging directory as
`worker/`, copy `deploy/worker.Dockerfile` there as `Dockerfile`, and copy `LICENSE`
and `LICENSES/`. Retain the version-matched Npgsql notice. Build using the reviewed
platform and record the resulting immutable image digest. No image is built,
published or deployed by the local verification tools.

The attested hosted application image also includes the published worker and its
version-matched provider notice. Its normal entry point remains the API; the
worker stack explicitly selects the worker entry point from that same image.
`deploy/swarm-workers.yaml` is the separate-service stack template. Every image,
secret/network name, replica count, poll and lease setting must be supplied
explicitly. Create separately scoped service logins, memberships/grants, worker
bindings and the controller cluster registration before starting it. Store only
the corresponding connection/certificate files in external Swarm secrets. Use
separate database and control networks, private PostgreSQL TLS and authenticated
manager access. Node labels select disjoint scheduler/controller placement; the
API's eligible nodes must exclude those control roles when separate placement is
required. The template creates no database, application, workload network or
host-maintenance access and does not migrate the existing hosted Compose release.

When a separately placed scheduler cannot join the database's local bridge,
`deploy/openetrm-worker-database-proxy.service` provides an optional installation
transport using the operating system's `socat` package. Its root-owned environment
file must supply an exact private listener address/port, permitted worker address
and database container address. Bind it only to an authenticated private tunnel;
never a public or wildcard address. It forwards opaque TCP bytes without TLS
termination or database credentials. The worker still requires PostgreSQL
`VerifyFull` TLS and its own restricted SCRAM login. Qualify the selected route,
certificate identity and source-address restriction before use. Keep container
identity/address checks in the private installation record; database replacement
requires explicit transport review. This transport publishes no database container
port and does not change Swarm node availability.

## Verification and remaining work

The owned PostgreSQL checks exercise real migrations, identities, RLS, delegated
agent scheduling, revocation, stale/expired claims, lock waits, cancellation,
idempotency, cross-workspace quota races, fair reconciliation and a separate .NET
worker. The controller lifecycle uses a synthetic Docker HTTP handler against the
real disposable database. Gateway checks cover lost create responses, immutable
identity, overprivileged specs, task evidence and image/platform qualification.
HTTP and owned-browser checks cover ordinary shared visibility and restricted
controls. Stack rendering checks use no daemon and establish configuration only.

Run these through `tools/check_all.py`; `tools/check_deployment.py` also validates
the Swarm template. No existing application/database/Swarm is used for testing.

Remaining work includes real disposable Swarm failure/recovery qualification,
host reboot and Docker Engine restart through an explicitly selected management
provider, full agent/provider execution and scoped tool-credential exchange,
child-agent ancestry/budget enforcement, broader recurrence/calendar authoring,
broader scheduled financial operations, external stop
attestations, workload artifacts/logs and a qualified Swarm release migration.
## Generated pilot closes

The explicitly selected `generated_pilot_close_v1` operation consumes a retained
exact input close, chosen books and root curves, a synthetic source, an explicit
seed and a clearing-tick movement bound for every native leaf. Its
`seeded_clearing_tick_move_v1` generator derives each movement from the seed,
due instant, exact series and anchor observation identity. It does not download
settlements or describe its observations as official exchange prices. Source
contracts, units, quote increments and dependency definitions remain unchanged.
Prices outside the selected quote domain fail; they are not clamped or retried.

Calendar-day UTC scheduling is an explicit simulation policy, not an exchange
holiday calendar. A job may run once or every elapsed UTC day, with no automatic
activation. Recurring occurrences retain the same immutable selected anchor; they
do not silently choose the most recent generated close. Root membership retains
only the exact anchored contracts still live
at the due instant; an empty root or missing book factor fails. The selected
`carry_exact_anchor_frozen_facts_v1` policy retains every exact anchored final
settlement and published index fixing as a frozen fact. Parameters retain their
separate exact-anchor policy. Publication rejects omitted, substituted or
preliminary frozen facts; it never generates or shocks actual index fixings.
No final settlement or missing observation is manufactured.

Before marks are written, the managed application adapter values every selected
book and requires the configured roots to cover all its declared factors. Both
supported futures and swaps participate; an unsupported product fails the whole
selection. Unit-level pricers declare factors and compute their own values. The
book adapter applies external quantities and reuses eligible canonical unit
valuations and immutable scenarios across independently labeled measure groups.
Shared sides may retain different per-instrument discount providers. Discount
inputs remain fixed during commodity-quote shocks. The selected
`exact_rational_portfolio_repricing_v1` policy stores exact rational money and
risk derivatives; it does not silently round an unrepresentable decimal result.
The legacy interactive futures operation retains its separate exact18 contract.
The portfolio risk API and retained risk queue use this common valuation adapter
with exact rational holding or book detail and explicit supplemental market pins.

The scheduler login issues a short-lived context for its registered service and
exact run fence. Its installed mapping has an exact market-catalog foreign key;
startup matches the selected manifest and runtime login before claiming work.
Claims and live contexts remain confined to that configured catalog.
A separate restricted runtime login performs managed reads and
fenced publication; it loads neither an authenticator nor the human owner's
credentials. The worker references the existing API adapter assembly without
starting an HTTP application. That application-layer coupling is intentional for
this consumer; it is not a separate general-purpose pricing service.

Pilot execution additionally requires both protected files selected by
`OPENETRM_PILOT_RUNTIME_CONNECTION_FILE` and `OPENETRM_PILOT_MANIFEST_FILE`.
The first contains the dedicated runtime connection; the second names the exact
market catalog and workspace. The installed `automation.pilot_runtime_logins`
mapping must match that catalog, runtime login and registered scheduler. Missing
or mismatched inputs reject startup before a financial claim. Both the service
and the job owner need the operation's current market/trade, valuation/scenario,
import/publication and job permissions. Migrations install no service account,
permission grant, profile or enabled schedule.

The first transaction retains its certified preflight context and commits native
marks. The second captures a certified calculation cutoff at that exact mark
commit, excluding later trade amendments, and retains the full report. Current
owner and service permissions, source scopes, deadlines and leases are required
at each phase. Publication uses a private operation-local permission window;
the service context cannot use generic observation imports or close publication.
A lease replacement reuses committed marks and reports under a new fence.
The generated-close worker does not renew its lease inside a calculation transaction.
Each phase must finish within its explicitly configured lease (at most 120
seconds) and overall deadline; a longer job timeout does not extend that lease.
No large-book latency guarantee is established by the small synthetic fixture.

The catalog distinguishes exact current-working configuration metadata from its
current certified book directory. It never chooses an anchor on the user's
behalf. Actual calculation always binds the selected close, due instant and
certified knowledge context. Durable closes are discoverable through the same
scoped market-frame navigation; readers retain explicit exact close identity.

## Generated-close report storage

Generated pilot marks and their portfolio report have separate commits. A close
can therefore exist while its calculation fails or its worker loses the lease.
The occurrence remains incomplete until its exact immutable report is published.
A replacement worker reuses committed marks and a committed report rather than
generating a different close. Successful completion requires the report as well
as fresh service and job-owner authority. After a finish operation waits on its
database update, its exact run fence, worker, lease, deadline and job expiry are
checked again; an expired finish rolls back the complete occurrence transition.

Report totals keep futures execution P&L and swap present value as separate
measures and retain currency groups. They are not a combined settlement PV or
variation-margin result. Quote exposure comes from shared deterministic
base/up/down repricing; discount inputs stay fixed during these commodity quote
scenarios. Results retain exact rational cash and derivatives. Display rounding
does not alter stored numerators or denominators.

Migration `0057_pilot_close_reports.sql` retains exact run, close, certified
calculation context, model and reference revisions. Typed child foreign keys
retain trade revisions and bindings, external quantities, canonical unit models,
market dependencies, native factor observations and quote units, and payment
discount mappings and observations. A retained futures database instrument alias
remains distinct from its canonical contract valuation identity. Policy and work
objects contain scalar settings and counters; financial relationships are not
stored as JSON links.

Readers need current market/trade, valuation and scenario permission and access
to the owning job. The selected historical view must see the report publication
and its exact sources. The calculation context is evidence and never replaces a
reader's selected cutoff. A retained historical view must select the close's UTC
business date; a current view can read the explicitly requested dated report.
Retained trade and discount-mapping effective times must not follow the close.
The fixed report reader obtains its rowsets in one
MVCC statement, with full authority checks before and after the database read
and a final managed check before returning the formatted envelope. Raw table
reads retain forced row-level security. This query shape is not evidence of a
50,000-position report latency guarantee.

The current publisher rejects reports above 20,000 unit models, 100,000 holdings,
4,096 factors, 256 book/currency/measure totals, 1,048,576 factor result rows or
40,000 payment discount records. Rational integer components are limited to
4,096 decimal digits. Unsupported or incomplete results fail rather than being
truncated. Schema upgrades require stopped write and worker admission and drained
calls; publishing an application image does not install this migration.


Interactive futures scenarios have a separate typed retained-run queue and trusted
worker, documented in [risk scenarios and jobs](risk-engine.md). It uses the same
financial context and current-authority principles, but does not execute arbitrary
container profiles. Its Swarm replicas process independent complete runs; the
existing scheduler and generated-close mixed-product adapter remain supported.

### Renewable existing-close completion

Migration `0095_close_risk_renewal.sql` applies only to `close_book_risk_v1`.
The explicitly configured 5–120 second worker lease is renewed every one third
of that duration through a separate scheduler connection. A renewal command is
bounded by the integral one-third lease duration (at least one second). Renewal
checks database time after ownership lock waits and refuses expired, cancelled,
replaced or unauthorized ownership. It cannot change the original overall
1–3,600 second job deadline, admission profile, input pins, attempt or fence.

Preparation retains a read-committed authorization fence and live per-read claim
checks, but does not hold job/run row locks. Request-context expiry for this
operation is bounded by the original deadline and job expiry; that token never
substitutes for the live lease check. Renewal does not update bound context rows.
After preparation commits, deterministic valuation uses immutable admitted inputs
outside any database transaction. Authority updates, cancellation and recovery
therefore do not wait for CPU valuation. A fresh context is bound for publication.
SQL statements retain a five-second bound and ownership lock waits a one-second
bound; these are statement bounds, not a total preparation time guarantee.

The publication wrapper waits on the existing report queue before locking the
job/run rows. The validated report and terminal success commit atomically. The
inner publisher and terminal helper are not callable by the runtime login.
Completion reconciliation is a read-only scheduler operation over the exact
run, login, worker and fence under current authority. Unknown commit outcomes
never cause a guessed failure or a second report. Success-first cancellation
returns the completed outcome without changing state; cancellation-first fences
publication. Authorization and command-key replay/conflict checks apply to both.
Cancellation callback failures are retained after the token is cancelled; they
cannot bypass joining renewal and the exact committed-outcome lookup. A callback
exception proves neither success nor failure of publication. Current authority
and caller cancellation still govern reconciliation; no receipt means no success.

Renewal failure cancels local calculation cooperatively and leaves durable
ownership to expire for ordinary recovery; it does not revive or repin the run.
Publication has a hard database deadline. Physical calculation stops at its next
cancellation check after failed renewal, so cessation can lag that deadline by
heartbeat cadence, the bounded renewal call and cooperative cancellation latency.
Generated-close, readiness and controller worker paths are unchanged.
