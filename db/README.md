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

## Audited interval series

[0064_interval_series.sql](migrations/0064_interval_series.sql) stores one value per UTC
interval with immutable revisions ([ppa-design.md](../docs/ppa-design.md) §5, phase A). The
HTTP routes, screen, limits and 20-year measurements are in
[implementation.md](../docs/implementation.md#audited-interval-series) (D172).
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
`market_data.interval_series_knowledge_receipts`. All five tables are immutable, for the owner
too, and runtime reads them only in a current (non-historical) context, under current market
access and, for a series and its revisions and values, the series' exact reference version.

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
