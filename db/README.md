# Reference-data storage foundation

[0019_contract_instruments.sql](migrations/0019_contract_instruments.sql) removes
new-booking futures side/wrapper allocation. The existing exact futures-series
primary key identifies the contract; compact UUID aliases are checked against the
full key. Typed immutable alias and per-revision pricing tables preserve legacy
IDs/events and exact selected curve/member references. Composite FKs require the
trade alias and selected constituent to resolve the same exact series. RLS follows
original publication/effective coordinates; missing pricing evidence fails closed.
Apply with runtime admission stopped and calls drained. This does not backfill
settlement state or implement futures PV.


[0020_swap_identity_keys.sql](migrations/0020_swap_identity_keys.sql) aligns the
floating-side candidate key with its curve/observation/payment definition. Resolved
monthly contract IDs remain binding evidence outside that key. Reuse validates
complete ordered reset bindings; ambiguity is rejected. Prior rows/UUIDs are not
rewritten, and conflicting retained keys stop migration. Drain runtime writes
before applying this migration as well.

[0018_trade_parties.sql](migrations/0018_trade_parties.sql) adds immutable scoped
legal-entity/counterparty versions and exact foreign keys on trade revisions.
New runtime booking/amendment entrypoints require both parties and an explicit
date-only trade date, retaining V2 booking events and full idempotency payloads.
Earlier entrypoints lose runtime execution permission. Existing V1 revisions and
their certificates retain NULL date/party facts without inferred backfills.
Apply this additive migration with runtime write admission stopped and admitted
calls drained; the publication gate does not replace draining old function bodies.

[0001_reference_data.sql](migrations/0001_reference_data.sql) is the first migration.
It requires stock PostgreSQL 16 or later and installs no extensions. This is a
storage foundation, not a complete application schema or a runnable service.

The original migration created 28 tables covering the following families.
Migrations 0002 and 0005 below add used specifications and retire unused catalogs;
the resulting reference schema has 31 tables. This historical list is not the
current active catalog inventory:

- Catalogs and immutable reference revisions.
- Currencies, dimensions, units, conversions, commodities and commodity units.
- Clock rules, calendars and dated business-day overrides.
- Location types, locations and commodity/location bindings.
- Exchanges, clearing houses and their supported relationships.
- Day-count and compounding conventions; FX, interest, heat-rate and price-index
  definitions. Dated observations and curve recipes belong to market data later.
- Futures products, dated series, milestone values, sources and product evidence.

Every revision child has a composite catalog/revision key and exact-revision
foreign keys. Definitions accept inserts only while their revision is draft;
updates/deletes are rejected. Corrections create another revision. A seal locks
out later inserts and waits for existing insertion transactions to finish.

The migration does not install real definitions or automatically load fixtures.
[The fixture](../tests/fixtures/reference-synthetic.sql) is entirely synthetic and
only for a disposable test database. There is no active/approved product state,
runtime application role, authorization service or knowledge-publication resolver.
Sealing freezes storage; it does not approve economics, data rights or use.

Reference numeric inputs reject unsupported range/scale rather than using a
rounding `numeric(p,s)` coercion. Calculation precision and runtime/API round trips
remain separate qualification gates. Semantic validation must check calendar
coverage, conversion consistency, milestone clocks/dates and evidence completeness.

## Disposable verification

Use preinstalled database binaries. The test runner never builds PostgreSQL,
accepts no existing database URL and starts no network listener. It creates its
own cluster/socket in a private temporary directory, checks that server's data
directory, applies the migration, loads synthetic fixtures and removes the cluster
after testing. Trust authentication applies only to that private test socket.
If startup or shutdown cannot be confirmed, the runner reconciles any server in
its newly owned directory and retains that directory rather than deleting it
under a potentially running process.

```sh
python3 tools/check_reference_schema.py --pg-bin /path/to/postgresql/bin
```

The runner uses only the Python standard library as test orchestration. This does
not change the approved C#/.NET application stack. It exercises real writes and
failure SQLSTATEs, including foreign keys, dimensional relationships, numeric/date
bounds, immutability and both orderings of insertion/freezing races under read
committed, repeatable read and serializable isolation. A transaction may receive a
serialization failure; no write is allowed to enter an already sealed revision.

The migration alone supplies no runtime access. The scoped runtime/RLS boundary
is added in 0003 below; verified activation, context publication and domain/trading
tables remain further work. Runtime roles must not own tables, disable triggers, change
DDL, truncate data or receive migration privileges. Database-owner control is an
administrative trust boundary, not an application permission system.

See [reference-data.md](../docs/reference-data.md) for business requirements and
remaining futures/rights gates.

## Market-data slice

[0002_market_data.sql](migrations/0002_market_data.sql) adds seven reference tables:
measurement bases, quantity specifications, quote specifications, uniform delivery
profiles, typed numeric value specifications, settlement target identities and
cash-rounding conventions. It also
permits the explicit per-delivery-day futures lot basis. Legacy commodity-unit
tables retain their original canonical-dimension contract.

Nine market tables cover catalogs, definition revisions, dated contracts, curves,
memberships, source inputs, explicit constituent mappings, generic typed series
and append-only observations. Curves own common references. All
relationships pin exact catalogs/revisions; unsupported mappings and cycles prevent
sealing. Futures must match their dated series, specified product economics and
known last-trading instant. Sealing also checks compatible reuse of constituents.

Market-definition writers increment a locked revision generation. This prevents a
concurrent stale-snapshot sealer from validating an incomplete view of the bundle.
Observation corrections require an existing compatible predecessor; source
event/revision keys are unique, and repeat writes raise conflicts. Idempotent
ingestion, qualified publication, selection queries and persistence adapters remain
application work. Stored observation values obey explicit domains and clearing
increments; no coercive rounding is applied.

```sh
python3 tools/check_reference_schema.py --pg-bin /path/to/postgresql/bin --market
```

This runs the reference suite plus market checks in the same newly owned disposable
cluster. It exercises the five-layer fixture, invalid key/type/mapping cases,
corrections and concurrent semantic sealing. The market migration alone grants no runtime access. The next migration supplies
the scoped security boundary described below.
See [implementation.md](../docs/implementation.md) for the managed core and limits.

## Current security and RLS

[0003_security.sql](migrations/0003_security.sql) persists scoped current authority
and six common role templates. It forces RLS on every market/reference table,
separates runtime/authenticator/definer privileges and binds trusted request
identity through private transaction capabilities. Reference sharing is explicitly
read-only; edits require the owning workspace and an applicable grant. No users,
credentials or automatic assignments are installed.

```sh
python3 tools/check_reference_schema.py --pg-bin /path/to/postgresql/bin --market --security
```

Checks use both role switching and a real unprivileged LOGIN on the private test
socket, plus revocation and expiry-after-lock-wait regressions. PostgreSQL role
creation/ownership is installation work and the disposable runner uses its own
new cluster administrator. Never grant runtime membership in authenticator or
security-owner roles. See [security.md](../docs/security.md) for the trust boundary
and required sign-in/API/audit integration.

## Location point coordinates

[0004_location_coordinates.sql](migrations/0004_location_coordinates.sql) adds two
nullable columns to the existing `ref_data.locations` table. It creates no tables
or extensions and does not change grants, RLS, exact type links or immutability.
The fixed convention is WGS 84 decimal degrees: latitude [-90,90], longitude
[-180,180]. Supply both or neither. Existing rows retain NULL coordinates; zero
is valid explicit data. The existing exact-number domain rejects unsupported
precision and nonfinite values without a rounding numeric typemod.

Coordinates belong to retained reference versions; corrections create new
versions. A point is not an entire pipeline route, region polygon or hub-membership
set. Location types already support those distinct business meanings; broader
geometry and topology remain unimplemented. See
[location semantics](../docs/reference-data.md#location-types-and-geography).

Apply numbered migrations in order in a new installation. The preview applies
0001 through 0010 before its current synthetic fixtures. The schema runner separately
exercises upgrading already retained data with 0004 after the selected base suites,
then checks coordinate bounds, null pairing, finite values, precision, exact type
FKs and retained-version guards. The combined suite, including retirement and rates below, now passes 173 checks.

User-facing Reference data and Market setup are logical categories, not a reason
to move/duplicate their versioned SQL tables or relax their foreign keys.

## Retire unused catalogs without deleting retained data

[0005_retire_unused_definitions.sql](migrations/0005_retire_unused_definitions.sql)
removes unused `fx_rate_definitions`, `heat_rate_definitions`,
`day_count_conventions` and `compounding_conventions`. It also replaces the empty
interest definition's two convention FKs with explicit supported day-count,
compounding and implementation identifiers. These have no defaults and are
consumed by the rate example. The original published migration is unchanged.

Run retirement at READ COMMITTED. It locks all five affected tables, checks
unfiltered emptiness with `row_security=off` and uses no CASCADE. Populated data,
inadequate unfiltered access, unsupported transaction isolation or an external
dependent object aborts the whole transaction. Reconcile an installation's old
rows/dependencies explicitly before upgrading; never truncate them to force this
migration through. It is intended for the current empty installation catalogs,
not an automatic conversion of retained production definitions.

The disposable schema runner tests blocked populated rows, FORCE RLS filtering,
repeatable-read/serializable rejection, external dependencies and rollback before
applying this migration to the empty original schema. Only then does it load the
current reference fixture. That deliberately isolated migration test precedes
later market/security suites; the full preview applies numbered migrations in
normal order to a new empty cluster.

## Dated interest-rate curves

[0006_rate_curves.sql](migrations/0006_rate_curves.sql) adds two market tables,
`rate_curves` and `rate_curve_points`, bringing market storage to eleven tables.
The shared generic series/observations store annual decimal zero rates. Exact
maturity/specification/source FKs replace fictional commodity contracts. The
additional seal validator rejects empty curves, invalid sample types, conflicting
maturity reuse and unordered dates. The existing revision-generation lock and
forced scoped RLS also apply to both new tables.

The schema runner's `--market --security` path checks rate sealing, type and FK
constraints, immutable retained definitions, current permissions and cross-scope
isolation. The preview adds the executable project-owned discount-factor example.
See [the current reference-table inventory](../docs/reference-data.md#current-table-consumers-and-implementation-policy)
and [rate numerical policy](../docs/implementation.md#interest-rate-example-and-numerical-policy).

## Independent date schedules

[0007_date_schedules.sql](migrations/0007_date_schedules.sql) adds stable schedule
identities and version rows for the schedule editor. These are market configuration,
not imported reference catalogs. Drafts are generation-checked and editable;
published versions and reference identities are immutable. Parent/version FKs,
finite effective intervals and scoped RLS are enforced. Publishing is a narrowly
granted operation with post-lock permission validation and a captured committed
publication prefix; audit timestamps do not claim wall-clock knowledge resolution.
The preview checks both API behavior and direct runtime/owner storage boundaries.

## Certified schedule knowledge

[0008_temporal_knowledge.sql](migrations/0008_temporal_knowledge.sql) records full
publication transaction identities, immutable commit certificates and retained
contexts. `date_schedule_facts_at` exposes half-open effective/known rectangles
inside a captured prefix, preserving partial corrections and exact old receipts.
RLS remains forced on all three new stores and security-invoker projections.

The installation requires `track_commit_timestamp=on` before publishing any facts.
The owned preview enables it at server startup; no extension or native build is
used. Existing publications without verified transaction evidence are rejected by
this additive migration. They require a separately qualified migration, not an
inferred timestamp backfill. Fresh certification checks primary/system/timeline
identity, supported transaction epoch, retained engine metadata and clock ordering.
A failed proof produces an explicit error. Certified receipts do not depend on
future retention of their original engine timestamp entry.

The engine configuration and retention limitations are documented in the official
[commit timestamp functions](https://www.postgresql.org/docs/16/functions-info.html#FUNCTIONS-COMMIT-TIMESTAMP)
and [tracking setting](https://www.postgresql.org/docs/16/runtime-config-replication.html#GUC-TRACK-COMMIT-TIMESTAMP).
The contract is engine-recorded commit time, not the later instant a particular
client or replica observed the record. Failover/restore/clock-repair procedures
remain unqualified; no production database is changed by preview testing.

## Shared market knowledge and workspace access administration

[0009_market_knowledge.sql](migrations/0009_market_knowledge.sql) extends the same
commit gate/cutoff to sealed market bundles and imported observations. Definition
applicability periods are explicit before sealing. Ordered event assertions and
immutable certificates retain exact full transaction links; market contexts have
their own prefix alongside the schedule prefix. Exact reference versions are
reachable only through certified effective bundles when a view binds. Restrictive
SELECT/INSERT/UPDATE policies prevent future reads and historical writes; current
permission checks also protect definer sealing/publication. Install before sealed
bundles/imports; existing data without verified transaction evidence is rejected.
The observation's received_at is never used as commit proof.

[0010_workspace_administration.sql](migrations/0010_workspace_administration.sql)
adds private scoped memberships and append-only administrative audit. No account,
credential, membership or admin grant is seeded. Runtime can call narrow authorized
routines only. Administrative admission takes the exclusive epoch lock first;
shared-to-exclusive upgrades are rejected. Member versions and exact operation
identities serialize edits and reconcile concurrent retries. Suspension is scoped,
last-admin/self-removal are guarded, and current rights govern historical access.
The fresh-install migration rejects existing grants requiring verified membership
mappings; production upgrades/bootstrap require a separate qualified procedure.

The preview creates all migrations before its explicitly synthetic fixture.
Its four demo personas and invalid issuer are test-only. Registering additional
preview identities issues no real credentials and sends no messages. See
[implemented scope](../docs/implementation.md#shared-historical-market-view-and-administration).


## Local trading storage additions

[0011_futures_bookings.sql](migrations/0011_futures_bookings.sql) adds immutable
synthetic futures sides/instruments, portfolios/books, trades/bindings and a
commit-certified trading publication prefix joined to the shared historical
context. Scoped forced RLS and private authorized booking/status operations
protect current and retained reads. Futures settlement is explicitly unprocessed.

[0012_account_table_views.sql](migrations/0012_account_table_views.sql) stores
bounded optimistic-versioned account presentation layouts. Layouts carry no
financial context, permissions or calculated values.

[0013_trade_amendments.sql](migrations/0013_trade_amendments.sql) retains original
bookings as revision 1 and appends latest-head amendments with immutable event
identities, prior revision, explicit effective instant and reason. Original root
rows remain unchanged. Exact retries return their original accepted event;
conflicting ownership/payloads and stale heads are rejected. Historical state is
read-only. This is booking-state amendment, not settlement/lifecycle processing.

[0014_trading_references.sql](migrations/0014_trading_references.sql) adds exact-FK
trade and instrument reference numbers, backfills originals and allocates new
numbers atomically under the publication gate. UUIDs remain relationship keys.
References survive amendments and reusable instrument sharing.

Migrations 0013/0014 acquire the global publication fence and source-table locks
before backfill. Apply 0013 only with runtime write admission stopped and legacy
calls drained; an already-running old function body cannot be replaced by that
fence. Qualify upgrades on disposable retained-booking fixtures, preserve original
publication/context coordinates and take an owned backup before a retained local
upgrade. These checks do not qualify a generic online production upgrade.

[0015_financial_swaps.sql](migrations/0015_financial_swaps.sql) adds immutable typed
fixed/floating sides, ordered exact reset references, required instrument
memberships, swap trade revisions and exact currency-default curve mappings.
Runtime access is SELECT-only under forced RLS and the existing certified trading
context. Authorized booking/amendment functions serialize through the publication
fence, append a publication atomically, validate complete daily/monthly coverage
and retain original events for idempotent retries. Futures and swaps share operation
identity collision checks and readable TR/IN references. Apply with runtime write
admission stopped and calls drained; this is an additive local migration.

Default mappings append under a new trading publication; do not insert new rows
under an already committed publication. Supplemental synthetic daily observations
use a separately sealed market revision and explicit manifest pins. Preserve
existing immutable revisions, observations and retained context certificates.


[0016_swap_payment_rules.sql](migrations/0016_swap_payment_rules.sql) adds typed,
immutable payment conventions and exact optional rule FKs on fixed/floating sides.
It preserves existing side columns and manual payment instants; newly generated
UTC boundaries are compatibility storage for date-only rule results, not authored
payment times. New side identity includes the complete payment rule. Private
calendar/date helpers independently validate backend-generated dates and bounded
coverage. Runtime writes use the new scheduled-swap function; legacy accepted
commands retain exact retry reconciliation without admitting new manual-date
bookings. All rule rows share their side's atomic trading publication and RLS.

Stop/drain runtime calls before this upgrade. Preservation checks compare the
original column projection, explicitly accounting for the newly added nullable
FK columns, and preserve original publications, contexts and command payloads.
No payment rule is inferred or backfilled onto an existing side.

[0021_party_editors.sql](migrations/0021_party_editors.sql) adds authorized,
append-only legal-entity and counterparty maintenance. Party versions retain
their stable identity/code, explicit effective instant and certified publication.
Typed operation receipts provide exact retries; expected revisions reject stale
updates. Reads honor the selected certified context, while writes require current
scoped authority and a current working context. Runtime roles have no direct
party-table write access. Stop and drain runtime calls before applying this
additive migration; preserve existing party versions and exact trade references.

[0022_standard_futures_lots.sql](migrations/0022_standard_futures_lots.sql) retains
exact product/profile and optional index relationships, dated typed lot factors,
and exact index-minus-benchmark settlement references for quoted basis futures.
The v2 execution-P&L convention admits explicitly resolved variable lots and
physical settlement terms without implementing delivery or variation margin.
Existing unambiguous product profiles are normalized without changing trade rows.

[0023_deal_amount_formats.sql](migrations/0023_deal_amount_formats.sql) stores shared
current deal display precision, with separate trade/fixed/floating policies,
optimistic revisions and scoped business operations. Presentation does not amend
financial history. [0024_preview_identity_scope.sql](migrations/0024_preview_identity_scope.sql)
retains verified identity-provider scope when administrators create local preview
accounts. Stop/drain runtime calls before applying these migrations.

[0025_trading_reference_authority.sql](migrations/0025_trading_reference_authority.sql)
rechecks current reference-catalog access before privileged futures/swap economic
reads and calendar evaluation. Exact accepted-command replay retains its control
semantics. Discount-default resolution also checks the selected reference and
market-definition visibility in historical contexts. Existing owners and ACLs
remain unchanged; stop/drain runtime calls before upgrading.

## Display names, authored TOU products and schedule calendars

[0026_display_names.sql](migrations/0026_display_names.sql) stores a display name
on every commodity and rate curve and on each role template. A curve code keeps
one name across all revisions of its catalog. Existing curve rows have no
defined name, so the migration refuses a database that already holds curves;
such an installation needs a verified naming migration first. Stop/drain runtime
calls before upgrading.

[0027_tou_products.sql](migrations/0027_tou_products.sql) replaces fixed TOU
choices with authored products: editors save drafts and approvers publish
immutable versions bound to an exact sealed clock rule. Readers see published
versions only, and power previews pin the product version they price.

[0028_schedule_calendars.sql](migrations/0028_schedule_calendars.sql) binds each
date-schedule version to an exact sealed holiday calendar from a reference
catalog the writer can access. The weekend mask uses ISO 8601 bits (bit 0 is
Monday). Draft schedule versions are hidden from readers.


[0029_server_booking_facts.sql](migrations/0029_server_booking_facts.sql) retains
immutable human booker, UTC booking date and persistence time. APIs and raw
business writes reject caller-supplied booking facts; exact retries precede
stamping. Existing economic coordinates remain unchanged. These additions follow
the stored-name, TOU-product and reference-calendar migrations 0026–0028.
[0030_deal_risk.sql](migrations/0030_deal_risk.sql) grants the distinct draft-deal
risk capability to traders and risk analysts.
[0031_definition_publication_scope.sql](migrations/0031_definition_publication_scope.sql)
requires current access to the exact clock/calendar catalog before a new TOU or
reference-calendar schedule publication. Accepted retries retain their original
control ordinal and create no new publication. Drain runtime writes for upgrades.
The display-name migration still rejects populated unnamed catalogs; this
sequence does not claim an automatic upgrade of those retained installations.

## Page-assistant model providers

[0043_model_providers.sql](migrations/0043_model_providers.sql) grants
`ManageModelProviders` to access administrators and adds the `assistant` schema
for the page assistant's providers ([ai-assistant.md](../docs/ai-assistant.md)
§5): one provider per workspace and vendor, insert-only credentials and an
append-only administration audit. No provider, credential or key material is
installed. The API seals each key itself, so the key never reaches a statement,
parameter or log: the database holds only nonce, ciphertext, tag and last four
characters, and the audit a redaction marker with a keyed retry digest. The runtime
has no table access; three definer operations recheck `ManageModelProviders` and
refuse delegated agent contexts. Providers stay unqualified until their adapters
ship. As a new migration it stops automatic hosted updates until the
operator qualifies a new baseline (D135).

## Page-assistant models and prices

[0051_model_catalog.sql](migrations/0051_model_catalog.sql) adds the page
assistant's models and price revisions ([ai-assistant.md](../docs/ai-assistant.md)
§5.1, §5.3). A model is a provider's vendor identifier, immutable and unique per
provider, with an audited display name; adding a vendor's first model creates its
provider without a credential. Capability versions come with qualification (§12
step 4). A price revision is immutable and effective-dated: a rate set per million
tokens in an exact currency row of a sealed reference revision, with an optional
long-prompt tier, or a withdrawal that prices nothing. Revisions append, and the
highest sequence whose half-open interval contains an instant governs it. The audit
gains the model it concerns, tied to that model's provider, and the Providers
directory lists provider changes only. Like credential replacement (0045), each
command rechecks current authority after its principal and model row waits, before
returning a replay and after its writes, and the migration first verifies the exact
authority helpers it relies on and the definition it replaces. No model or
price is installed. As a new migration it stops automatic hosted updates until the
operator qualifies a new baseline (D135).

## Unambiguous swap discount defaults

[0052_unambiguous_discount_defaults.sql](migrations/0052_unambiguous_discount_defaults.sql)
adds `currency_discount_default_key`: one currency discount default per exact
catalog, definition revision, reference catalog/revision, currency, effective
instant and publication. A later publication may still replace a mapping at the
same instant, consistent with D99. Retained tied mappings stop the migration; it
never merges or deletes them, so such an installation needs an explicitly reviewed
reconciliation before upgrading. `security.swap_discount_default` keeps its owner
and grants and raises `swap_discount_ambiguous` rather than choosing between tied
latest mappings (D166). The migration takes the security epoch and trading
publication fences and refuses an unexpected lookup definition. As a new migration
it stops automatic hosted updates until the operator qualifies a new baseline (D135).

## Page-assistant cache-read rule

[0053_model_cache_read_rule.sql](migrations/0053_model_cache_read_rule.sql) adds
`assistant.cache_read_required`: a rate set must price cache reads on every protocol
except the Messages API, which caches only on request
([ai-assistant.md](../docs/ai-assistant.md) §5.3, §6.1). Add price applies the helper
in place of its inline rule, and the Models & prices directory states it for each
model, so Settings › AI never restates the rule. The replaced definitions keep 0051's
checks, owners and grants, and the migration first verifies them and the authority
helpers they rely on. As a new migration it stops automatic hosted updates until the
operator qualifies a new baseline (D135).

## Page-assistant provider qualification

[0058_provider_qualification.sql](migrations/0058_provider_qualification.sql) adds
provider qualification ([ai-assistant.md](../docs/ai-assistant.md) §5.1, §6.3): usage
mapping versions confirmed from published documentation, installed with their date
and sources, and for each vendor the confirmed version its adapter applies (one, the
Messages mapping `messages_v1`); vendor terms that administrators record as immutable,
sourced and dated versions; and attestations bound to a credential and terms version.
A provider gains an enabled state, off until Enable, which needs its adapter's
confirmed mapping and an attestation of its current key and terms. Replace key and new terms each leave the provider unattested and disable it
in the same transaction. Four definer operations, Record terms, Attest, Enable and
Disable, advance the provider's version and, like credential replacement (0045),
recheck current authority after their principal and provider row waits, before
returning a replay and after their writes. No terms or attestation is installed. The
migration first verifies the authority and replay helpers it relies on and the
definitions it replaces. As a new migration it stops automatic hosted updates until
the operator qualifies a new baseline (D135).

## Page-assistant display-name rule

[0059_model_display_name_rule.sql](migrations/0059_model_display_name_rule.sql) gives a
model's display name the character rule of vendor terms text
([ai-assistant.md](../docs/ai-assistant.md) §6.3): 1 to 80 characters with no space at
either end and no C0 or C1 control, line or paragraph separator, bidirectional
embedding, override or isolate, or zero-width no-break space. 0051 trimmed only ASCII
spaces and refused only `[[:cntrl:]]`, the C0 and C1 controls, so a name could begin
with a no-break space or carry a right-to-left override or a line separator.
`assistant.is_plain_text` now lists those code points once, rather than naming a
locale's character class, and reads its escapes the same way whatever the session's
`standard_conforming_strings`. `assistant.is_display_name` and `assistant.is_terms_text`
each apply it with their own length, so terms text keeps its meaning. The replaced
definitions keep their signatures, owners and grants, the runtime cannot execute the new
helper, and the migration first verifies the definitions it replaces. A replaced
function does not recheck the rows its constraint admitted, so the migration reads every
stored display name, with row security off and under read committed isolation, which it
requires. One that breaks the rule stops it with `model_display_name`, naming each
affected model with its vendor and identifier. The migration never renames a model: a
rename is an administrator's audited change. Such an installation renames those models
through Models & prices, then applies the migration again. As a new migration it stops
automatic hosted updates until the operator qualifies a new baseline (D135).

## Page-assistant model capability versions

[0060_model_versions.sql](migrations/0060_model_versions.sql) adds model capability
versions ([ai-assistant.md](../docs/ai-assistant.md) §5.1, §6.3, §9). A version is
immutable and records, each with its source and checked date, the model's output
limit, the reasoning efforts it accepts, its identifier's stability, the identifiers
its vendor may report as served and its tool and streaming confirmations, together
with the usage mapping version it was validated under. Foreign keys tie a version to
its model's own provider and that provider's vendor, and to a mapping whose efforts
are stated: `assistant.mapping_efforts` records, immutably, the reasoning efforts a
confirmed mapping's adapter sends (`messages_v1`: low, medium, high, xhigh, max), and
an adapter applies only a mapping whose efforts are stated. A trigger holds, for the
owner too, that a version's efforts are ones its mapping states and that its served
identifiers exclude its model's own. Every list a version or confirmed mapping holds,
sources included, is one-dimensional, numbered from 1 and distinct, and every fact
was checked on a date that had begun when its version was recorded. A model gains an
enabled state, off until Enable, which needs a current version validated under the
mapping its vendor's adapter applies, on an enabled, qualified provider. Three definer
operations, Edit, Enable and Disable, advance the model's version and, like the model
commands of 0051, recheck current authority after their principal and model row waits,
before returning a replay and after their writes. The audit's model actions gain the
three. No version is installed. The migration first verifies the helpers it relies on
and the definitions it replaces. As a new migration it stops automatic hosted updates
until the operator qualifies a new baseline (D135).

## Page-assistant directory efforts

[0061_directory_efforts.sql](migrations/0061_directory_efforts.sql) has the Providers
directory state, with the usage mapping each vendor's adapter applies, the reasoning
efforts that mapping's adapter sends, so Settings › AI offers exactly those when an
administrator records a model's capabilities ([ai-assistant.md](../docs/ai-assistant.md)
§9). Nothing else changes, and the replaced directory keeps its owner and grants. The
migration first verifies the helpers it relies on and the definition it replaces. As a
new migration it stops automatic hosted updates until the operator qualifies a new
baseline (D135).

## Page-assistant provider tests

[0062_provider_tests.sql](migrations/0062_provider_tests.sql) adds model-call records,
opened for now only by provider Test ([ai-assistant.md](../docs/ai-assistant.md) §5.1,
§5.3, §7.4, §9). `assistant.model_calls` is immutable. Each call names, by foreign key:
- its model's exact capability version;
- the attested key and terms of the model's provider;
- the price revision in force at its start, a rate set;
- the administrator and request identity;
- the agent-worker generation.

A call also records:
- the digest of the per-test token that closes it;
- its reasoning effort and output limit, which the version must allow;
- its cost method, `token_cost_v1`.

`assistant.model_call_outcomes` holds at most one immutable outcome per call. Each records:
- when it was recorded, which a retried close can put well after the stream ended;
- the outcome code and the served identifier;
- each usage dimension's count with its source: reported, not billed, or unknown, which has no count;
- the usage status;
- the exact cost.

Recognized usage knows every priced dimension and reports no more reasoning than output. Only
recognized usage is priced, by `token_cost_v1` under the pinned revision, and tokens in a
dimension it does not offer leave the call unpriced. Two triggers hold, for the owner too:
- a call's effort, output limit and rate-set pin;
- an outcome's cost, and a served identifier its call neither requested nor accepts
  appearing only with a mismatch.

`assistant.worker_generation` holds the database's agent-worker generation, which advances
one at a time and names the backend that started it.

Three definer operations make up the protocol:
- **`start_worker`**: needs the session-level advisory lock 730849160. It advances the
  generation and closes once, as interrupted with unknown usage, every call an earlier
  generation left open.
- **`open_test`**: runs in the administrator's context.
  - A retry of a test returns its outcome, with or without a worker generation, or
    `test_in_progress` while it runs.
  - A new test needs the current generation, whose worker still holds the lock, and
    refuses in this order: `model_unqualified`, `model_provider_unqualified`,
    `parameter_required`, `model_unpriced`.
  - It returns the attested key, still sealed.
  - Like the 0051 commands, it rechecks current authority after its principal and row
    waits and after its writes.
- **`close_test`**: needs the test's own token, not a request context. A later close
  changes nothing.

Nothing is seeded but the generation row, at 0. The migration first verifies the helpers
it relies on. As a new migration it stops automatic hosted updates until the operator
qualifies a new baseline (D135).

## Page-assistant provider directory tests

[0064_directory_tests.sql](migrations/0064_directory_tests.sql) has the Providers
directory state each provider's latest test, by its start, as `test_state` gives it, open
or closed, and whether it ran under the key and terms attested now, so the Providers tab
shows its last test and when new ones have outdated it
([ai-assistant.md](../docs/ai-assistant.md) §9). Nothing else changes; the replaced
definition keeps its owner and grants. The migration first verifies the helpers it relies
on and the definition it replaces. As a new migration it stops automatic hosted updates
until the operator qualifies a new baseline (D135).

## Page-assistant worker start time

[0065_worker_start_time.sql](migrations/0065_worker_start_time.sql) has
`assistant.start_worker` lock the generation row before it stamps its start. In 0062 the
stamp could be taken before the start waited for an in-flight test open, so a test the
start then closed as interrupted could read as closed before it started. Nothing else
changes; the replaced definition keeps its owner and grants. The migration first verifies
the lock helper it relies on and the definition it replaces. As a new migration it stops
automatic hosted updates until the operator qualifies a new baseline (D135).

## Page-assistant agent administration

[0066_agent_administration.sql](migrations/0066_agent_administration.sql) adds
`ManageAgents` (access administrator), `UseAgents` (trader, risk analyst and market data
editor, each of which may delegate) and the agent reader role, which holds
`ReadMarketData`, `ReadTrades` and `ReadTradeParties`
([ai-assistant.md](../docs/ai-assistant.md) §5.4). A trigger on role grants keeps that
role to agent principals and agent principals to it, even for the owner, while any
grant can still be revoked; the migration first fails, revoking nothing, if an agent
principal holds a current role.
`apply_administration` refuses to create agents, to change an agent principal and to
grant the agent reader role (`agent_managed`), and its directory marks that role as not
assignable; otherwise both are as before.

Agents and their immutable versions and allowed roles are new tables. An agent keeps
its name, unique in its workspace ignoring the case of ASCII letters whatever the
database's locale, and its principal; each audited change advances its version, its
published version only moves forward and Retire is final. A version pins an exact model version and
states an effort that version records, an output limit within its own, tools from the
fixed set, its limits and a grant duration in days. Each allowed role row must name a
role holding `UseAgents` and every permission the version's tools need
(`agent_role_lacks_permission`). The model administration audit gains an agent column.
Each command's request names its action, and the administrator's principal row
serializes their commands, so an identical repeat waits for its original and answers as
it does. Save takes the runtime set's bounds on instructions and grant duration as
parameters and applies them after a repeat's replay. Create and Retire write control tables, so outside exclusive admission they refuse with
`admission_required` once every check has passed; `use_agent_admission` binds that
admission. The migration first verifies the helpers it relies on and the two
definitions it replaces. As a new migration it stops automatic hosted updates until the
operator qualifies a new baseline (D135).

## Audited interval series

[0067_interval_series.sql](migrations/0067_interval_series.sql) stores one value per UTC
interval with immutable revisions ([ppa-design.md](../docs/ppa-design.md) §5, phase A). The
HTTP routes, screen, limits and 20-year measurements are in
[implementation.md](../docs/implementation.md#audited-interval-series) (D173).
`market_data.interval_series` defines a series by its kind and one exact specification in a
sealed reference revision:
- `additive_price_shape`: a quote specification;
- `multiplicative_price_shape`: a dimensionless value specification;
- `capacity_factor_forecast`: a dimensionless value specification (MW per MW of capacity);
- `metered_energy`: a quantity specification of the energy dimension.

A series also pins its clock and a resolution of 5, 15, 30 or 60 minutes. A definition whose
specification or clock does not meet these rules is refused with `interval_series_definition`
(SQLSTATE 23514). Owner bindings, a curve for shapes and an asset for volumes, belong to later
phases.

Each revision in `market_data.interval_series_revisions` is a delta of at most 200,000
intervals and records its actor, reason and request identity. Its rows in
`market_data.interval_series_values` set the intervals it names, keyed by exact UTC start; a
null value withdraws one. Every revision is certified on the shared knowledge gate:
`security.capture_date_context` records its commit time as its `known_from` in
`market_data.interval_series_knowledge_receipts`. Row triggers refuse any update or delete of
the five tables, the owner's included, and runtime reads them only in a current
(non-historical) context, under current market access and, for a series and its revisions and
values, the series' exact reference version.

Four definer operations make up the contract. Each needs a current, non-historical context and
refuses without current authority with `interval_series_denied` (SQLSTATE 42501):
- **`define_interval_series`** needs `WriteMarketDefinitions` and refuses a malformed or used
  code with `interval_series_code`.
- **`append_interval_series_revision`** needs `ImportObservations`. A retried request returns
  its original receipt, and a changed one is refused with `interval_series_conflict`. It refuses
  a stale expected revision with `interval_series_revision_conflict` and, writing nothing, any
  interval of another duration, overlapping another in the revision or a stored interval at a
  different start (a withdrawn one included), or holding a non-canonical or out-of-bounds value
  with `interval_series_values`. A stored interval changes only at its exact start. Checks of
  the request alone run before the shared knowledge gate lock; the replay, head and
  stored-overlap checks and the writes run under it. It rechecks current authority after its
  writes.
- **`read_interval_series`** returns each interval's value in the latest revision known at an
  explicit cutoff, a revision known exactly at the cutoff included, with that head revision.
- **`interval_series_changes`** returns each change with its previous value, actor, reason and
  knowledge time, and a summary per revision, at most 100,000 rows.

Both reads return the whole intervals inside a window no longer than 35,136 intervals, at a
cutoff no later than now, and refuse anything else with `interval_series_window`. They refuse
a From or To strictly inside a stored interval of the series, of any revision and withdrawn or
not, with `interval_series_grid`, so no interval is dropped at either end; an end in a gap is
accepted, and reads never load the pinned clock. Each read
retains one temporal context per cutoff, dated by the cutoff's UTC day, and rechecks reference
access after certifying it. As a new migration it stops automatic hosted
updates until the operator qualifies a new baseline (D135).

## Page-assistant grants

[0068_agent_grants.sql](migrations/0068_agent_grants.sql) lets a person grant an agent
version its reads ([ai-assistant.md](../docs/ai-assistant.md) §7.1). A grant is a
delegation from the person to the agent principal, in the existing delegation tables,
for the permissions the version's tools need and until now plus its grant duration, all
taken from the stored version; the request names only the version. A unique index on
unrevoked delegations keeps one current grant per person and agent, so a grant revokes
the person's earlier one to that agent. The new grant audit, immutable, records each
grant with the version, model identity and permissions it was given for, and each end of
one: replaced, revoked or with Retire. `retire_agent` now also revokes the agent's open
grants and refuses a request identity a grant or Revoke used; otherwise it is as before,
and the replaced definition keeps its owner and grants. Grant and Revoke write control
tables, so outside exclusive admission they refuse with `admission_required` once every
check has passed; `use_grant_admission` binds that admission. The migration first
verifies the helpers it relies on and the definition it replaces. Since a grant is a
delegation with a grant record, it fails, revoking nothing, while any delegation is
unrevoked, even an expired one. As a new migration it stops automatic hosted updates
until the operator qualifies a new baseline (D135).

## Page-assistant turns

[0069_agent_turns.sql](migrations/0069_agent_turns.sql) records turns
([ai-assistant.md](../docs/ai-assistant.md) §5.1, §7.4): conversations, each one
person's in one scope with one agent; turns, each a question to one exact agent version
under the grant that covered it, with its page report, its pinned-context record, the
earlier turns it includes, its runtime set, fencing-token digest, worker generation and
session binding; and each conversation's turn events, appended once. A trigger holds,
for the owner too, that a turn's state and record only move forward. A model call is now
a test's or a turn's: a turn's call names its turn, its attempt and its conversation's
latest event when it opened instead of a request identity, administrator and token
digest, and its outcome may be `cancelled`. `close_test` now closes only a test's call;
otherwise it is as before and keeps its owner and grants. The Providers directory's
latest test leaves turn calls out, with an index for that lookup.

`ask_turn` runs in the person's own request context and refuses a conversation that is
not theirs in the scope, or has another agent, before admission. Admission, under the
workspace's advisory lock, first ends what is stuck: a turn still without its record at
the deadline fails `ask_incomplete`, or ends cancelled once its cancel was requested,
and a turn past its elapsed limit plus the deadline is interrupted. The person's
authority is then checked, after every wait, as for every call and with a covering
grant, before a second active turn (`turn_active`) or one past the workspace's limit
(`workspace_turn_limit`) is refused; the endings stand either way. `pin_turn` writes the
pinned-context record once: over working data it captures once under lock 730849102,
refusing while fewer than the runtime set's reserve of the catalog's 4,096 retained
contexts would remain (`knowledge_budget_reserved`); a retained context is pinned as
named, for a person who may read the catalog's market data.

The worker's operations take no request context. Each but the close presents the turn's
token under the current worker generation; the close needs only the token, so accounting
finishes in any state. A stale token or generation is `agent_runtime_unavailable`, a
write the turn's state does not accept `turn_state_conflict`. A turn makes one call,
retried only after a transport failure or overload whose input is known and that
published nothing, within the version's retries (`retry_not_allowed` otherwise); a retry
after the attempts' reported input exceeded the version's limit ends the turn
`limit_input_tokens`. Opening a call locks the model and provider first, then rechecks
the owner's authority and what stops a call, which end the turn. An answer append takes
the epoch row before the turn and rechecks the owner's authority, so once a revocation
commits it publishes nothing, records the refusal and answers refused `access_changed`,
as every later append does, and the turn can then only fail `access_changed` or be
interrupted; once the turn's cancel is requested it publishes nothing either. The worker
then closes the call with the usage it has and ends the turn, which needs every call
closed; a turn completes only after its call completed, and its terminal event, like its
usage, skips the publication recheck. A turn's usage sums what its calls reported per
dimension, counting unrecognized usage as unknown. Stop, the session's end and triggers
on provider Disable, Replace key and Record terms, model Disable, Retire and grant
revocation request a turn's cancel, unless its publication was refused, and notify the
worker on `assistant_turn_cancel`. `start_worker` now also interrupts every turn an
earlier generation left unfinished, and `provider_directory` leaves turn calls out; both
keep their owner and grants. The migration first verifies the helpers it relies on and
the definitions it replaces. As a new migration it stops automatic hosted updates until
the operator qualifies a new baseline (D135).

## Interval curves

[0070_interval_curves.sql](migrations/0070_interval_curves.sql) lets a curve's constituents be
exact delivery intervals ([ppa-design.md](../docs/ppa-design.md) §6, phase B).
`market_data.contract_intervals` extends one contract with its UTC interval and the labels its
clock gives it: operating date, hour beginning, minute and occurrence (2 only for the second
pass through a repeated local hour). An interval lasts 5, 15, 30 or 60 minutes. Rows are
immutable definition rows, guarded like the contracts they extend, and runtime reads them only
where the exact definition revision is visible, under the same read policies as the other
market definition tables. Directly marked interval prices are ordinary observations of the
interval contracts' series.

`market_data.curve_blocks` makes a curve a block curve: its date-only constituents crossed with
all hours, or with the peak or off-peak hours of one TOU product version, on the clock, holiday
calendar and grid of the curve's delivery profile. `market_data.shaped_curves` defines a shaped
curve by its method (`additive` or `multiplicative`), its calibration policy
(`duration_weighted_block_mean_v1`), its operator version (`block_shape_v1`) and the phase A shape
series its block marks are calibrated onto; `market_data.shaped_curve_blocks` names its blocks.
Block marks are ordinary observations of the block curves. `market_data.curve_actual_rules`
gives a curve with a collected-price source ([0063](migrations/0063_power_curve_settlement_sources.sql))
the instant each interval's actual is due: its start or end plus an offset of up to seven days,
under `interval_actual_due_v1`.

Sealing a definition revision refuses with `interval_curve_definition` (SQLSTATE 23514), naming
the broken rule in the detail:
- an interval contract that is not a forward, does not deliver from its operating date to the
  next day or does not expire at its interval end; a curve that mixes interval and date-only
  constituents; or an interval whose duration differs from its curve's delivery profile;
- a shaped curve code that is also a curve code;
- a block curve that is not a direct curve of date-only constituents on a 5, 15, 30 or 60 minute
  profile;
- a shaped curve without exactly one all-hours block, or the peak and off-peak blocks of one TOU
  product version;
- shaped-curve blocks that differ in quote, commodity, location or delivery profile;
- a peak or off-peak block whose TOU version is not published on its profile's clock;
- a shape series that is not an additive shape in the blocks' quote (additive) or a multiplicative
  shape (multiplicative) on their reference version, clock and grid;
- a due rule on a curve without interval constituents, or whose source differs from the curve's
  delivery profile in clock or interval length.

Every rule that reads a curve's reference rows, TOU versions, shape series or sources requires a
matching visible row, so a row the sealer cannot see refuses the seal. The checks are their own
trigger and leave `validate_revision` unchanged. All five tables are immutable definition rows under the read
policies of the other market definition tables. As a new migration it stops automatic hosted
updates until the operator qualifies a new baseline (D135).

## Page-assistant ask answers

[0071_admitted_asks.sql](migrations/0071_admitted_asks.sql) has `ask_turn` say when its
answer admitted the turn ([ai-assistant.md](../docs/ai-assistant.md) §7.4, §8). Only the
process that asked holds the turn's token, so only the answer that admits a turn may
have that process pin the turn and hand it to the worker; a repeat answers with its turn
as it stands, as before. In 0069 the two answers were alike. Nothing else changes; the
replaced definition keeps its owner and grants. The migration first verifies the helpers
the ask relies on and the definition it replaces. As a new migration it stops automatic
hosted updates until the operator qualifies a new baseline (D135).

## Page-assistant tool steps

[0072_tool_steps.sql](migrations/0072_tool_steps.sql) records a turn's tool steps
([ai-assistant.md](../docs/ai-assistant.md) §7.1–§7.3, §7.5, §7.6, §8).
`assistant.tool_steps` keeps each step that answers a request of a call that stopped for
tools, in order and numbered across the turn: its tool, a read's operation and the instant
it read, the request's input and the code of a refusal or failure.
`assistant.tool_read_access` keeps the access function, permission and catalog each read
ran under. Both are immutable, owned by the security owner with row security forced, and
written only by `record_tool_step`. It takes the epoch row FOR SHARE, refuses a call's
requests that would take the turn past the version's tool steps (`limit_tool_steps`),
rechecks the owner's authority and every source in the agent's authority, recording a
refused publication as an append does, and publishes a step event carrying only the step's
number, tool and code; once the turn's cancel is requested it records nothing. A step
names its own turn's call, through a key on `model_calls(id, turn_id)`, and a step without
input is a refusal of its arguments.

A call's close records how a completed call stopped (`stop`, with `tool_requests` for a
request for tools), so a turn completes only after its last call ended the turn;
`close_turn_call` is recreated with these two more arguments and granted to the runtime
again. The tool loop's next call opens once each request has its step; the version's
retries bound the failed calls of the whole turn, and an earlier call's input not known
ends the turn `limit_input_unverifiable`. A turn's source set is its reads and those of
the turns it included. Before every call and publication it passes as a context issued to
the turn's agent under its grant would answer it, evaluated without one. The owner's reads
hide the answer and text of a turn whose sources the owner can no longer read
(`access_changed`), and each turn they answer says whether that hid its answer (`hidden`);
the worker's answers do not. An ask withholds such a turn, and one whose sources the
covering grant does not list, naming each by a step, and is refused `access_changed` while
the turns it includes use a source the agent can no longer read. The pinned-context record
keeps an As of frame's exact pins, as canonical uuids with an instant the database reads,
and the worker's start answers what tool reads need.

The definitions it replaces in place keep their owners and grants. A turn an earlier
release left unfinished, whose closed call names no stop, is interrupted at the next
worker start, as every earlier generation's turn is. The migration first verifies the
helpers it relies on and the definitions it replaces. As a new migration it stops
automatic hosted updates until the operator qualifies a new baseline (D135).

## Retained interactive risk jobs

[0073_risk_jobs.sql](migrations/0073_risk_jobs.sql) adds complete futures-risk
requests, exact context/book/observation relationships, worker registrations and
atomic lease/publication operations. Current actor, delegation and service scopes
remain authoritative through loading and publication, including revoke/restore.
Frozen input uses are explicit retained pins. A dedicated `openetrm_risk_worker`
role can read only the required financial tables under forced RLS; it cannot
issue runtime capabilities, book trades or launch containers.

The consumer and installation prerequisites are documented in
[risk scenarios and jobs](../docs/risk-engine.md). Existing automation migrations
0035/0036 and generated-close reports remain separate consumers. Apply ordered
migrations only through the reviewed installation process; the worker stack never
migrates an existing database. The disposable check is
`python3 tools/check_risk_jobs_db.py --pg-bin /path/to/postgresql/bin`.
