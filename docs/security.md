# Identity and authorization foundation

Status: current security persistence, seven role templates and PostgreSQL RLS are
implemented locally and tested in disposable resources. Credential verification,
production sign-in and credential lifecycle are not yet implemented. Workspace
administration and atomic durable access audit now exist in the owned preview. The runnable market workbench is explicitly loopback-only with simulated
test identities; see the [preview boundary](implementation.md#disposable-local-market-workbench).

## Principals and login kinds

Humans, services and agents have separate stable principal IDs. Identity bindings
map exact verified issuer/subject pairs to a principal and a matching login kind:
`HumanInteractive`, `ServiceCredential` or `AgentCredential`. An agent has its own
credential/login binding. IDs, display names and model-generated claims are not
credentials. Principal identity/kind cannot be edited in place.

The authentication adapter must verify signature/credentials, issuer, audience,
expiry and permitted login flow before supplying verified claims. Issuance,
rotation, session management and credential revocation integration remain work;
credential rotation should preserve principal identity. Never log raw credentials
or capability tokens. There are no seeded users, passwords, grants or universal
administrator accounts in the migrations. The owned disposable preview fixture
separately provisions six clearly labeled synthetic test identities and scoped
grants; these never establish a production login boundary.

## Common roles and scoped authority

[CommonRoles.cs](../src/OpenEtrm.Security/CommonRoles.cs) and
[0003_security.sql](../db/migrations/0003_security.sql) install these templates, and
[0066_agent_administration.sql](../db/migrations/0066_agent_administration.sql) adds the
agent reader:

| Role | Intended permissions |
| --- | --- |
| Viewer | Read scoped market data |
| Trader | Read, book/amend trades, use agents and delegate within authority |
| Risk Analyst | Read, run valuations/scenarios, use agents and delegate within authority |
| Market Data Editor | Read, import observations, write market/reference drafts, use agents and delegate within authority |
| Market Data Approver | Read, freeze definitions and approve/publish qualified changes |
| Access Administrator | Manage identities/access, model providers and agents, and create scoped delegations |
| Agent reader | Read market data, trades and trade parties; held only by agent principals |

A trigger on role grants keeps the agent reader role to agent principals and agent
principals to it, even for the database owner, while any grant can still be revoked;
migration 0066 fails, revoking nothing, if an agent principal already holds a current
role. An agent principal created through Users & access before 0066 therefore holds no
current role and has no authority; Users & access still lists it but no longer changes
it, and it is not an agent of agent administration. Users & access lists agents and the
agent reader role but creates and changes no agent and grants no one that role
(`agent_managed`); agents are created and retired through agent administration
([ai-assistant.md](ai-assistant.md) §5.4). Hosted provisioning grants its owner the
six human roles by name.

Migration 0011 adds explicit `ReadTrades` to Trader and Risk Analyst. The bounded
futures booking, holdings and actor-owned request-status operations are implemented;
Viewer and Access Administrator have no implicit trading read. Current scope is
workspace-wide, with no per-book exceptions. Historical inputs never restore old
permissions, and denied reads clear displayed holdings while preserving uncertain
request identities. Latest-head booking amendments require current AmendTrade
authority and expected-revision concurrency. Generic valuation and financial
publication remain future operations. Storage sealing is not financial approval
or publication. Maker/checker approval rules remain part of those future commands.

Role assignments bind exact tenant/workspace scopes and half-open UTC intervals.
Revoked grants, disabled principals and suspended workspace memberships confer no authority. Access administration
has no implicit market-data access. This first implementation has no wildcard,
role inheritance, group hierarchy or book-level exception rules. Installation
operators must provision explicit verified identities and memberships; application
administration uses separately authorized, current workspace commands.

A market catalog belongs to one explicit workspace. Reference catalogs have one
owner scope and explicitly configured read-only shares; recipient editors cannot
write shared drafts. Sharing currently covers the catalog's retained revisions;
it is not an activation, publication or data-rights grant. Unmapped catalogs deny
access. Reference/market relationships use exact-version foreign keys and every
reference/market table has RLS enabled and forced.

## Database request boundary

The migration creates three isolated PostgreSQL roles:

- `openetrm_runtime`: no login, table ownership, superuser or RLS-bypass authority.
  A provisioned application login inherits its narrow privileges.
- `openetrm_authenticator`: no login, with only the capability-issuance operation.
  Its separately provisioned trusted login is used after authentication.
- `openetrm_security_owner`: no login; owns private security state/routines and
  has narrowly granted market/reference access with RLS bypass for those routines.
  It is never granted to runtime or authenticator roles.

The trusted authenticator issues an opaque, short-lived request capability from
verified issuer/subject/login-kind claims and an explicit workspace/delegation.
The runtime binds it to its exact database login, backend PID and transaction ID.
The private mapping, rather than caller-set session variables, establishes identity.
Runtime cannot issue capabilities, read their table, assume privileged roles or
change security tables. Capabilities must remain internal to the trusted server.

A committed binding cannot be reused in another transaction. A rolled-back binding
can be retried while unexpired and must revalidate current authority. Tokens have
at most five minutes' lifetime. There is no automatic cleanup job yet; that belongs
to service integration. Pool reuse without a fresh binding confers no access.

Only read-committed runtime transactions are supported. Admission locks the current
security epoch in share mode; every control-plane mutation updates that epoch and
must serialize with active requests. Revocation either precedes admission or waits
for the admitted transaction to finish. This coarse fence favors a simple verified
boundary; it is not a scalability result. Use bounded request transactions when
integrating the service.

Capability expiry is checked using actual trusted time after admission lock waits.
Each permission evaluation captures actual current UTC time for grant/delegation
and context expiry. Historical market selectors cannot restore old authority.
Runtime has no direct state-column update permission. The permission-checked
market sealing routine still validates exact references and bundle completeness.

## Requests the API cannot read

The API reads a request body of at most 32 KiB. Binding reads the query values and the
`application/json` body, in UTF-8, that a route declares before its handler runs, and
refuses one it cannot read there, so nothing is written. No handler answers a bare 400
or 413, so every `/api` route names these refusals, in the local preview and in hosted
mode:

- `request_format` (400): a query value that does not parse, or a body that is
  malformed, including one holding an escaped lone surrogate, repeats a member, holds
  a value its member's type cannot hold, such as a whole number beyond a 32-bit
  member's range, or holds an unknown member where its request type refuses one, as
  every Settings › AI, grant and account request does.
- `request_too_large` (413): a body over the route's limit, 32 KiB, or 8 MiB for
  `/api/interval-series/revise`.

Behind the hosted gateway, `deploy/nginx.conf.template` keeps the same body limits and
answers a larger `/api` body with 413 and the same `request_too_large` code and
message. A gateway rendered from an earlier template refuses every body over 32 KiB
with nginx's own 413, which pages cannot read, so they keep such a request unconfirmed.

Each answer is a fixed code and message that echo nothing the request held, and
neither refusal is logged, so no part of a refused provider key reaches an answer or
the application log. A refusal that names its own code, such as `request_token`,
keeps it. Settings › AI releases a first attempt refused either way, since nothing was
written. For a form with entries it states the ranges they keep; for one without, a
refusal can only mean the page and the server disagree, and it asks for a refresh.
Users & access, parties, jobs, custom curves, trading and the contract-date and
reference metadata editors release such a first attempt too. They say when a request
was too large, and show their general message, which asks for a refresh, when the
server could not read it.

## Agents

An agent action requires all of: the agent's own current scoped grants, an explicit
unrevoked delegation with matching scope/time/permissions, and the enabled
non-agent delegator's current grants for both the action and `CreateDelegations`.
Missing delegation never falls back to a human/service identity. Chained delegation
and autonomous agent modes are unsupported in this first slice.

The managed authorization decision retains actual actor, effective delegator,
delegation ID, security revision, scope, permission and check time. It is evidence,
not a reusable bearer capability. Durable audit must retain both identities and
commit the operation/outcome alongside its economic context when commands exist.

### Scheduled and child agent authority

The required [scheduled job and agent contract](architecture.md#111-schedules-temporary-jobs-and-agent-execution)
extends the target architecture; it does not make chained delegation or unattended
agent execution supported by the current security slice. Schedule management,
job submission, agent launch, redelegation and business actions are distinct
capabilities with exact resource scopes. The authorized operations must validate
the executing principal and delegation rather than accepting identity claims from
task text. Scheduling for another identity requires explicit authority over that
identity and delegation; an owner field is not an impersonation capability.

All launched agents and child jobs remain within their originating authority,
delegation expiry, provider/tool restrictions and aggregate resource limits.
Admission, tool use and publication recheck current authority and serialize effects
with revocation. Authority revocation or grant/delegation expiry blocks new work
and publication; bounded cancellation tracks physical work still running. Job or
schedule expiry follows its explicitly selected admission/cancellation policy and
does not extend authority. Durable automation needs an
explicit grant that supports the intended lifetime; a short-lived session or
delegation cannot be silently renewed by a timer. Own-job control and management
of another principal's jobs are separately authorized. Job/result discovery also
requires current access and must not reveal another scope's runs or payloads.
Server-derived creation/delegation lineage covers agent-created schedules and
their future occurrences. Independent durable automation requires a separately
authorized operation and grant; caller-supplied parent/root fields cannot detach
work from inherited cancellation or aggregate limits.

### Container execution authority

The required [Swarm execution contract](architecture.md#112-docker-swarm-and-image-based-execution)
places Docker management access in a trusted execution controller. Image/profile
approval, build/push, workload launch, agent delegation and business operations
need distinct capabilities. An approved digest is necessary but does not grant
permission to run it in any scope. Reject caller-supplied raw Docker specs, host
mounts, privileged settings, arbitrary secrets and management-network attachments.
Workloads have no Docker socket, manager credentials or database login; their
short-lived business credentials are bound to a specific admitted run and current
execution fence. Authenticate bootstrap through a trusted runtime binding, never
caller-supplied task IDs/labels. Each physical replacement, including a Swarm
restart within the same attempt, acquires a new exclusive task fence and
invalidates its predecessor's authority atomically.
Revocation and accepted cancellation block new tool/effect/result admissions even
while container termination is pending or the manager is unreachable. A durable
exclusive controller lease/epoch governs management commands; late commands from
a former owner cannot authorize a task or supersede newer desired state. Reconcile
their exact owned resources. No production isolation or Swarm capability is
implied by this design.

### Infrastructure administration authority

The required [node maintenance contract](architecture.md#113-infrastructure-administration-and-node-maintenance)
provides a shared Jobs & Infrastructure view to authenticated members. Approved
shared status is distinct from protected node details and job inputs/logs/results.
Members can control their own jobs; explicit installation-wide job-management
authority lets administrators control all jobs. Derive ownership from trusted
creation/delegation evidence and reauthorize each query/action on the actual
resource. Cross-owner management does not imply permission to read business data
or private execution content; even the owner needs current content read authority.
Own-job controls cannot restart a shared worker or another run. Retry/restart
requires the run's current business authority, approvals and bounds. Backend enforcement cannot depend on
hidden buttons or caller-supplied owner/scope/parent IDs.

Detailed infrastructure inspection, availability/placement changes, shared
service restart and host maintenance have separate capabilities. Owning a job or
managing all jobs does not grant node/host control. A workspace administrator or
agent-launch grant is
not cluster administration. Shared-node actions require an explicit cluster-wide
mandate or authority covering every affected tenant/workload; infrastructure
rights do not grant business-data access. Govern placement labels as security
policy, since relabeling can move a workload across trust boundaries.

Typed node/host operations bind exact registered cluster, node, machine/provider
and service identities, current versions and applicable approval evidence.
Host credentials stay in a protected adapter; clients/agents cannot submit raw
shell commands, arbitrary endpoints or privileged container specifications.
Reauthorize each maintenance phase and serialize disruptive cluster operations
with fenced ownership. An accepted external command may continue after revocation
or cancellation: block further phases, reconcile it and retain uncertain status
until observed. Outage recovery requires an independent authenticated operator
path; the application cannot guarantee its own availability during maintenance.

## Remaining integration gates

1. Implement verified authentication and protected capability exchange on separate
   trusted authenticator/runtime connections; never expose arbitrary SQL or accept
   principal IDs, scopes, times or login-kind claims directly from clients/agents.
2. Load actual resource ownership and current grants into authorized operations;
   align database and managed authorization and commit business/audit writes.
3. Integrate initial-admin provisioning, verified identity linking, credential
   lifecycle, delegation authoring and any additional approval policy. The current
   preview admin commands manage six fixed roles; `ManageAccess` does not permit
   arbitrary SQL, role editing, credential issuance or cross-workspace elevation.
4. Add end-to-end credential lifecycle, request deadlines, job-phase reauthorization,
   capability cleanup and migration/operational role provisioning.

The managed evaluator still cannot authenticate caller-supplied state/time/scope;
those are trusted server inputs. The SQL fence protects database transactions,
not arbitrary external work. RLS is complementary to application business rules.

## Schedule authoring boundary

This section governs economic/date schedules, not automation jobs. These market
definition permissions do not authorize execution scheduling or agent launch.

The independent schedule editor uses existing WriteMarketDefinitions for draft
saves and SealMarketDefinitions for publication. Readers can inspect published
versions and workspace drafts but cannot mutate them. Candidate contract previews
remain private session artifacts. Published rows and stable reference identities
have storage guards; runtime lacks publication-state update privilege. The publish
operation rechecks current authority after admission and row-lock waits. Its
committed publication prefix is scoped to the market catalog and does not disclose
another scope's publication count. Historical schedule views still require current
read authority. This is a bounded schedule workflow, not whole-application temporal
or authentication-provider qualification.

## Historical contexts use current access

Schedule knowledge certificates and retained temporal contexts use forced RLS
under the same current catalog/workspace permissions as source facts. A context
identifier is a replay identity, not a credential or delegation. The context
operation rechecks read permission after locks and after its certification work.
Published schedule mutation still requires the existing scoped sealing permission;
reading history does not grant editing or publication. Raw runtime SQL cannot
modify certificate/context stores or call the private commit-time helper.

Owned database tests exercise revocation after a context is retained: HTTP replay
returns 403 and direct historical RLS returns no rows. Restoring current permission
allows the exact retained result again. Capability expiry during a blocked capture
also rejects it. These tests do not replace future external credential verification
or production credential/deployment qualification.

## Workspace administration and historical database views

Migrations 0009/0010 extend the current boundary. A market context binds once per
request and restricts both market and reference reads at the database. Historical
writes are denied by restrictive policies and the permission-checked definer
paths. The same exact cutoff covers separate certified schedule and market
prefixes. Receipt timestamps cannot make an uncommitted price historically visible.

Workspace membership suspension does not edit global principal status. Directory,
role changes and audit are scoped to the admitted tenant/workspace. Private tables
have forced RLS and no runtime table grants; only narrow definer operations are
callable. Administrative mutation requires both ManageIdentities and ManageAccess,
exclusive epoch admission before any shared admission, current-clock checks and an
expected target version. Agent delegation requirements still apply. Business read
rights are independent of administration. Member creation requires explicit kind
and roles; removal of all roles on another member is permitted and denies business
access. Roles have no inheritance or wildcard behavior.

The same administrative operation ID/payload/actor/scope returns its original
outcome. Another payload or actor conflicts. Account changes and append-only audit
commit together. Last-admin removal and self-removal of administrative rights are
rejected. Retrying an uncertain browser request uses its original identity; an
unknown response never becomes proof of rollback. UI caches clear on denied reload.

Authentication remains simulated and restricted to loopback. Preview registration
binds only the invalid synthetic issuer; it must not be exposed as real signup.
No migration installs users, credentials, grants, memberships or a default admin.
Upgrading an existing grant installation requires verified membership mappings;
the fresh-install migration rejects unqualified existing grants.

Named blotter layouts are current presentation state. Forced RLS restricts their
rows to the bound human principal, tenant and workspace; every API operation
requires current ReadTrades authority. No actor or workspace identifier is
accepted in the layout operation. The API validates the supported schema,
limits named views, serializes each account's writes and checks expected versions.
Saved settings cannot select financial contexts or confer business access. UI
layout metadata clears on identity changes even before a market date is selected.

Raw futures/swap business operations require current reference-catalog read
authority before evaluating new economic or calendar terms. Remembered exact IDs
do not preserve revoked access. Actor-owned accepted-command replay/status remain
current trade-control operations. Discount-default reads additionally retain the
selected historical reference/market-definition visibility.


## Implemented automation boundary

The [bounded jobs/controller implementation](automation.md) uses forced RLS on
all automation tables and narrow identity-derived operations. `RunJobs` admits
owner jobs; `ManageJobs` controls workspace jobs. A separate explicit installation
registration permits cross-scope job management. Container launch additionally
requires `LaunchWorkloads`, a current approved profile and any required delegation.
The worker is bound to a restricted actual database login and service principal,
with no direct business-data access, runtime/authenticator membership or RLS bypass.

Infrastructure control requires current `ManageInfrastructure` authority and an
unexpired installation operator registration. Ordinary administrators receive
neither implicitly. Agents can schedule only through their own current grants and
exact delegations; agent infrastructure control is rejected. UI visibility grants
no access to other owners' rows, payloads or host configuration.

Epoch admission, post-lock time/authority checks and monotonic claims fence stale
publication. External dispatch admission commits before Docker commands and is
serialized against revocation. An admitted command can finish later; cleanup and
unknown-state accounting remain separate from new execution authority. Shared
cluster admission serializes workload launch and maintenance; prepared unremoved
work prevents maintenance, including uncertain outcomes. The controller has
privileged mTLS manager access; image processes receive no credentials. This is
not full agent credential exchange or qualified hostile-code isolation.

## Page-assistant provider administration

Migration 0043 adds `ManageModelProviders`, held by Access Administrator, for the
page assistant's Settings › AI ([ai-assistant.md](ai-assistant.md) §5). Provider,
credential and audit tables force RLS with no runtime table grants; definer
operations recheck the permission and refuse delegated agent contexts, so an agent
never administers providers or keys. A provider key is write-only. The API
validates it, digests it with HMAC-SHA-256 under a private retry key, bound to the
tenant, workspace, request identity and vendor, and seals it with AES-GCM under a
private key-encryption key, bound to the provider identity, vendor and credential
version. Only nonce, ciphertext, tag and last four characters are stored. The audit
keeps a redaction marker and the digest, so an identical retry returns its original
outcome and any other reuse of the request identity in that workspace conflicts.
The preview runner creates both 32-byte key files, readable only by their owner,
once per database. A key file that is unset, missing, a symbolic link, of another
length or open to anyone else, and a credential that fails authentication, are
`credential_unavailable`. These routes do not exist under `--hosted-pilot` (P37).

Migration 0045 preserves the granted operation's signature, owner and privileges.
Credential replacement rechecks current authority after its principal and provider
row waits, before returning a replay, and after storage writes. Expiry during a
wait raises an authorization error and rolls back the operation, including its
audit row; the SQL boundary enforces this independently of the API's final check.

Migration 0051 adds models and price revisions under the same permission and table
rules, with five more definer operations that recheck it and refuse delegated agent
contexts. Like credential replacement, each model command rechecks current
authority after its principal and model row waits, before returning a replay, and
after its writes. Add price also requires the actor's current `ReadMarketData`
access to the currency's sealed reference revision (D131), checked before and again
after its writes, otherwise `currency_unavailable`, so a model administrator prices
models through an operator-assigned role that holds it (D152). Withdraw price names
no currency and needs no such access. Currency choices and labels are the reader's
own scoped reference read, and each choice names its catalog and revision.
Migration 0053 moves Add price's cache-read rule into a helper that the directory also
states. It first verifies the definitions it replaces and the authority helpers they
rely on, as 0051 does, keeps their owners and grants, and leaves the new helper
unexecutable by the runtime.

Migration 0058 adds provider qualification under the same permission and table
rules, with four more definer operations: Record terms, Attest, Enable and Disable.
Each refuses delegated agent contexts, so an agent never records terms, attests or
enables a provider, and each rechecks current authority after its principal and
provider row waits, before returning a replay, and after its writes. Only Enable
enables a provider, and only one whose adapter applies a confirmed usage mapping and
whose current key and terms versions an administrator has attested
(`model_provider_unqualified`). Replace key and new terms disable the provider in the
same transaction, so a changed key or changed terms never stay in use on an earlier
attestation. Mappings, terms versions and attestations are immutable, and the runtime
reads none of them directly. The migration verifies the definitions it replaces and
the authority and replay helpers it relies on, keeps their owners and grants, and
leaves its own helpers unexecutable by the runtime.

Migration 0059 gives model display names the character rule of terms text, so a
display name can no longer begin or end with a Unicode space or carry a bidirectional
embedding, override or isolate, which could reorder how it reads, or a line separator.
One helper lists those code points for both rules, and the runtime cannot execute it.
The migration verifies the two definitions it replaces and keeps their owners and
grants. It stops on any stored display name that breaks the rule and names the affected
models, rather than renaming them outside the audit.

Migration 0060 adds model capability versions with three more definer operations,
Edit, Enable and Disable, under the same rules: each refuses delegated agent contexts
and rechecks current authority after its principal and model row waits, before
returning a replay, and after its writes. Only Enable enables a model, and only one
whose current capability version was validated under the mapping its vendor's adapter
applies, on an enabled, qualified provider (`model_unqualified`,
`model_provider_unqualified`, `model_provider_disabled`). Versions and the mapping
efforts they draw on are immutable, the runtime reads neither directly, and the
migration's helpers are unexecutable by the runtime.

Migration 0061 has the Providers directory also state each applied mapping's
reasoning efforts. The directory keeps its definer, owner and grants, still rechecks
`ManageModelProviders` and refuses delegated agents, and the migration verifies the
definitions it relies on and replaces. Settings › AI offers only the efforts the
directory states, and the server validates every capability it is sent.

The Messages adapter (step 4) sends the key only in its `x-api-key` header, after
checking its format, so a malformed key never reaches a header error. It reads an
error body only to classify a 400 and never stores, logs or returns it. Streamed
reasoning text is discarded, and signed reasoning stays in the reply it returns for
replay within a turn. It reads at most the runtime set's response bytes and waits at
most its idle timeout for headers and each read. It takes an HTTP handler rather than
a client, which would buffer a whole response before the bound applied.

Provider Test (migration 0062, step 4) is the first route that calls a provider:
- **Handler:** requests go through one handler that ignores proxy settings in the
  environment, follows no redirect, keeps default certificate validation and adds no
  tracing header.
- **Check endpoint:** the local preview accepts one override, set only by the launcher's
  checks: an http or https origin whose host is a loopback IP literal, written exactly as
  it parses. Any other value stops the API from starting. Each request keeps its adapter's
  path there.
- **Opening a test:** happens in the administrator's request context and transaction.
  `open_test` refuses delegated agents and rechecks `ManageModelProviders` after its
  principal and row waits and after its writes. It pins the price in force and returns
  the attested key still sealed. The API decrypts the key and checks the request in that
  transaction, so a refusal records nothing, and nothing is sent until the record commits.
- **Closing a test:** the handler closes the record with a 32-byte token held only in
  memory, whose SHA-256 digest the record keeps.
  - `close_test` needs no request context, so a deadline, an abort or a permission lost
    meanwhile still records the usage.
  - The close runs whatever ended the stream. A close that fails transiently is retried
    until it lands. Any other failure is logged, and the test stays in progress until the
    next worker start closes it as interrupted.
  - A missing or wrong token is `agent_runtime_unavailable`.
  - The answer is read through the replay path under the administrator's current
    authority.
- **Worker:** one agent worker per database holds session-level advisory lock 730849160 on
  an unpooled connection, so a start that fails releases the lock it took.
  - The API consults the worker only after the permission check, and runs no command on
    its connection, which only holds the lock and, from step 5, listens. A worker that
    cannot start is logged; it refuses new tests, but a retry still returns a finished
    test's outcome.
  - A test is opened only under the current generation while that generation's worker
    still holds the lock, so another process's API refuses tests.
  - A worker start closes, once, every test an earlier generation left open. Migration
    0065 has it wait for in-flight opens before it stamps its start, so a test it closes
    never closes before it started.
- **Hosted:** none of this exists under `--hosted-pilot`.

Migration 0064 has the Providers directory also state each provider's latest test, as
`test_state` gives it, which carries no token digest, generation or sealed key, and
whether that test ran under the key and terms attested now. The directory keeps its
definer, owner and grants, still rechecks `ManageModelProviders` and refuses delegated
agents, and shows only tests of the caller's workspace. The providers route adds each
vendor's endpoint, the adapter's constant or, in the local preview under the check
override, where requests actually go, marked as a check endpoint. A test request the
page retains is kept per identity like the other commands, and a refusal that can come
after the test was recorded keeps it rather than inviting a second, billed test.

Agent administration (migration 0066, step 5) needs `ManageAgents`, held by the access
administrator role, and never runs under a delegated context:

- **Create** makes an enabled agent principal, an `agent_credential` binding at the
  manifest issuer with the principal id as subject, a workspace membership counted in
  the 256-member limit, which Users & access counts too, and the agent reader grant,
  recorded in both the Users & access and the agent audit. No login or password exists:
  the principal is issued request contexts by its agent credential alone, never as a
  person or a service. An agent's name is unique in its workspace, ignoring the case of
  ASCII letters only.
- **Repeats:** each command's request names its action, and a repeat answers only the
  identical request from the same administrator; a request identity used for any other
  administrative change, in Settings › AI or Users & access, is refused. The
  administrator's principal row serializes their commands, so an identical repeat sent
  while its original still runs waits for it and answers as it does.
- **Admission:** create and Retire write control tables. Each first runs every check
  under the shared admission, where refusals and repeats answer; only a command that
  will write repeats them under exclusive admission, which takes the epoch row before
  binding, so a refusal never blocks other requests.
- **Versions** are immutable: an exact model version that is current, enabled,
  qualified and priced on an enabled, qualified provider, one of its efforts, an output
  limit within its own, tools from the fixed set, limits and a grant duration. The API
  passes the runtime set's bounds on instructions and grant duration, which the database
  applies after a repeat's replay. Each allowed role must hold `UseAgents` and every
  permission the version's tools need, a rule the database applies to each allowed role
  row as it is written; its refusal names the role and the permission as fields, which
  the Agents tab states. Choosing a provider or editing instructions grants no
  application read.
- **Publish** publishes the newest version after the same model and provider checks;
  the published version only moves forward.
- **Retire** is permanent: it disables the principal and its membership, which keeps
  its member slot, and revokes its open grants, recording each against the
  administrator in the grant audit; history stays. Disabling the principal requests the
  cancel of the agent's turns.

Grants (migration 0068, step 5) delegate an agent version's reads to its agent
([ai-assistant.md](ai-assistant.md) §7.1). Each grant command runs in a person's own
request context, unexpired and undelegated, as an enabled member; agents and services
are refused:

- **Grant** needs current `UseAgents`, and `CreateDelegations`, which every delegated
  read requires of its delegator. The request names only the agent version; the database
  takes the delegator from the bound context and the agent principal, the permissions of
  the version's tools and the expiry, now plus the version's grant duration, from the
  stored version. That version must be the agent's published version, by an allowed role
  held now, or its newest version while still a draft, by a holder of `ManageAgents`
  (`agent_not_allowed`); the agent must be active (`agent_retired`); the user must hold
  every permission the version needs (`grant_permission_missing`, naming one); and the
  duration must be within the runtime set's bound, which the API passes
  (`grant_duration_exceeded`). Nothing is granted partially. A grant revokes the user's
  earlier one to that agent, expired or not: a unique index on unrevoked delegations
  keeps one current grant per user and agent, even for the owner.
- **Coverage:** a grant covers a version while it is unrevoked and unexpired, from the
  user to the version's agent, given for a version with the same model identity, and
  lists every permission the version needs. A newer capability version of that model
  asks nothing again, and a covering grant keeps its own expiry.
- **Revoke** names a grant, needs no `UseAgents` and succeeds only for its delegator;
  another user's grant is answered exactly like an unknown one (`grant_unknown`).
  Revoking a grant already revoked, replaced or ended by Retire answers when it ended
  and writes nothing.
- **Grant audit:** each grant, and each end of one (replaced, revoked or with Retire),
  appends an immutable record in the same transaction, naming the version, model
  identity and permissions the grant was given for. A grant is a delegation with such a
  record; the user's grants list only those.
- **Repeats and admission:** request identities are scoped by the audit a command
  writes. A repeat answers only the identical request from the same user, whose
  principal row serializes their commands, and grant, Revoke and Retire each refuse an
  identity another of them recorded in the grant audit. Grant and Revoke write control
  tables only under exclusive admission. Refusals and no-ops answer from the shared
  pass, which takes no exclusive admission and locks only the user's principal row, the
  agent or the grant. Each write advances the epoch, so it fences a delivery-price
  attempt in progress.

Turns (migrations 0069, 0071 and 0072, step 5) are recorded in the database; in the
local preview the API asks, stops and reads them and an in-process worker runs them
([ai-assistant.md](ai-assistant.md) §7.2, §7.4):

- **Ask** runs in a person's own request context, unexpired and undelegated, as an
  enabled member, with `UseAgents` and `CreateDelegations`. It names an agent version
  the person may use (`agent_not_allowed`) of an active agent (`agent_retired`), needs
  every permission the version needs (`grant_permission_missing`) and a covering grant
  (`grant_required`), and is refused while the version's model or provider would stop a
  call (`model_changed`, `model_disabled`, `model_unqualified`, `model_unpriced`,
  `model_provider_disabled` or `model_provider_unqualified`). A conversation is its
  owner's, in its scope, with one agent (`conversation_not_found`,
  `conversation_agent_mismatch`), checked before admission. A repeat answers only the
  same person's identical ask, whose principal row serializes their asks.
- **Admission** runs under the workspace's advisory lock, and the authority is checked
  after it and every other wait, before one active turn per person and the runtime set's
  number per workspace are counted.
- **The pinned-context record** is written once, by the asking person presenting the
  turn's token, rechecking their authority after each wait. A retained context is pinned
  only for a person who may read the catalog's market data, and a capture keeps the
  runtime set's reserve of retained contexts for page captures.
- **Worker writes** take no request context. Each presents the turn's token, whose
  SHA-256 digest the turn stores, under the worker generation that admitted it while
  that generation's worker still holds the agent-worker lock
  (`agent_runtime_unavailable` otherwise). Closing a call needs only the token, so
  accounting finishes in any state; a test's close refuses a turn's call.
- **Before the call** the database checks the owner's current authority without their
  request context (`UseAgents`, `CreateDelegations` and every permission the version
  needs held now, which needs their principal and membership enabled; an allowed role
  for a published version or `ManageAgents` for a draft; the grant unrevoked and
  unexpired; the agent's principal and membership enabled) and ends the turn
  `access_changed` when any part is lost, or when a source of the turn no longer passes
  in the agent's authority; then what stops a call on the model. A role loss cancels
  nothing and is caught there.
- **Sources:** a turn's source set is the access function, permission and catalog each
  of its reads ran under, even a read that then failed, and those of the turns it
  included. Worker writes have no request context, so they evaluate the set as a
  context issued to the turn's agent under its grant would answer it: the agent's
  principal and membership enabled with the permission, the grant current and listing
  it, its delegator holding it and `CreateDelegations`, and the catalog in the
  workspace's scope. A check proves this equal to the access functions in an issued
  agent context across the grant's revocation and term, a permission it lists, the
  delegator's permissions, membership and principal, the agent's role, a permission of
  it and its membership, and the catalogs' scopes; for a disabled agent, which no context
  is issued to, it answers nothing accessible. The reads themselves run in an agent
  context.
- **Publication recheck:** each answer append and tool step takes the epoch row `FOR
  SHARE` before the turn and then rechecks that authority and the turn's sources, so it
  waits for a revocation in progress and publishes nothing once it commits. The turn
  records the refusal: every later append is refused too, a cancel request no longer
  applies, and whatever the worker asks the turn fails `access_changed` unless recovery
  interrupts it. Usage, cost and the terminal event carry no business data and skip the
  recheck; a step's event carries only its number, tool and code.
- **Replay recheck:** the owner's reads, Stop's answer and an ask's answer show a turn's
  answer and text only while the owner, in their own request context, can read every
  source in its set, and hide them as `access_changed` otherwise; this needs no grant, so
  a revoked grant or a retired agent hides nothing. An ask whose record failed answers
  its turn's ending as the worker wrote it, which says nothing of the recheck: that turn
  never started, so it has neither answer nor text. An ask includes an earlier turn only
  when it passes, and when the covering grant lists every permission its sources used;
  each withheld turn is named by a step, and the included turns' sources must pass in the
  agent's authority before admission (`access_changed`).
- **Cancellation:** Stop is the owner's alone, in its scope; another person's turn is
  answered like an unknown one (`turn_not_found`). Provider and model Disable, Replace
  key, Record terms, Retire and grant revocation request the cancel of each turn they
  affect, as triggers, so every writer is covered; no text is published after it.
- **Records:** row triggers refuse, the owner's statements included, any update or
  delete of a turn event, a tool step or what its read ran under, a move of a turn's
  state or record backward, a change to an ended turn, a rewrite of answer text, and any
  change to a conversation but marking it read further; truncating these tables or
  disabling their triggers takes their owner. The runtime holds no privilege on the turn
  tables and executes none of the internal turn helpers.
- **Routes:** ask, Stop and the owner's reads run in the session's request context as
  its profile, one request at a time per session, in the route group's scope (`api`, or
  `exchange` for the exchange example), and every POST needs the page's origin and
  request token. The database knows the session by a random digest, never its cookie,
  with its profile and end. Hosted mode maps none of these routes.
- **Asking:** the API generates the turn's 32-byte token; admission stores only its
  SHA-256 digest, and the record write and every worker write present the token. Only
  the answer that admits a turn has the request write its record, in a short
  transaction of its own, and hand the token to the worker, which holds it only in
  memory; a repeat answers with its turn as it stands. However the record write fails,
  the turn fails in a scope of its own: with the database's refusal, `access_denied`
  for a refused permission, `request_cancelled` after an abort or the deadline, or
  `agent_runtime_unavailable` otherwise. Once that lands its ending is the answer;
  otherwise the error is, and the turn ends later: `ask_incomplete` at admission's
  deadline while it has no record, or interrupted.
- **The worker** holds the agent-worker lock on its unpooled connection, which then
  listens on `assistant_turn_cancel`; its writes use a pool of their own. However that
  connection ends, the generation ends with it: the process's turns stop their calls,
  the lock is released, and the next start interrupts the turns. Each call opens in one
  transaction with `open_turn_call`, the decrypted key and the adapter's checks, so
  nothing is sent unless the open commits. The key stays in the worker's memory and
  reaches only the request's header. A cancel notification, a profile switch, the
  session's end or a publication the database refuses or does not take stops the call
  in flight, and nothing more is published or sent; the call is closed with the usage
  it has, a call this program abandons closing interrupted at the next start, and the
  database decides the ending. A session's end is recorded before the turn ends, so the
  turn ends cancelled `session_ended`; a write the database does not take, that end's
  included, fails the turn `agent_runtime_unavailable`.
