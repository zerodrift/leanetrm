# First market-data implementation

Status: local synthetic preview. This is a bounded
foundation with a runnable local market-data and bounded futures trading preview. No existing application or production
resource is used by its checks.

A separate managed [connected calculation prototype](connected-calculation.md)
now exercises temporal graph selection, linked curve operators, nested input and
output shocks and ordinary instrument PV. It adds no browser business logic or
hosted routes. Its [large-graph benchmark](connected-graph-benchmark.md) measures
the compact integer execution kernel; saved custom-curve booking, worker
integration and large exact financial workloads remain work.

Earlier foundation checkpoint verification (2026-10-17): 60 frontend, 102 managed core,
24 mixed-resolution managed, 29 rate, 31 shared temporal and 20 schedule checks;
173 stock PostgreSQL schema checks; 198 standard and 24 mixed HTTP, 14 schedule-storage,
50 temporal/security storage, ten persisted-adapter and 49 owned rendered-browser
checks pass. Three process-cleanup checks also pass. The standalone dependency
check and local documentation links pass. Earlier counts below describe their
individual increments, rather than additional current suites. These results
qualify the synthetic local preview; they do not establish production readiness.

The small [shared utility library](../src/OpenEtrm.Shared/README.md) now provides
immutable date sets, explicit rolling, UTC interval grids/selections and bounded
canonical sharing. Its 31 checks and synthetic microbenchmark qualify these raw
in-memory operations only. Date-set operations remain coding utilities. The named contract-schedule resolver
described below is a domain consumer with exact contract identities. Monthly anchor generation and durable schedule authoring are now consumers. Project instructions
now require TDD, DRY, simple code and an explicit quality review before check-in.

## Durable jobs and shared infrastructure

The [automation implementation](automation.md) adds a separate .NET worker and
trusted controller over stock PostgreSQL. It includes owner/admin job controls,
explicit agent delegation checks, forced RLS, database-time claims and fences,
quota enforcement across workspaces, fair reconciliation and exact-request replay.
Jobs & Infrastructure is ordinary navigation; shared status grants no mutation
rights. Node availability requires both explicit identity permission and an
installation operator registration. The worker stack is separately configurable.

Synthetic Docker responses exercise approved digest/platform launch, strict
service/task identity, unknown outcomes and individual cleanup. Real Engine
execution and recovery remain unqualified. Full agent/tool execution, financial
job contexts and host management are still work. No runtime credentials or
installation-specific configuration are seeded or included in this repository.

## Role-tailored workbench and reference-driven choices

The browser shell uses one token-based stylesheet, a compact market-view chip
(current, historical or not-applied) and permission-driven navigation: readers
see Markets, Reference data and a read-only Schedules page (published versions
and date lookup); market-data editors and approvers also see Contract
definitions, schedule drafting/publication and the session-only roll shortcuts;
access administrators see Users & access. Visible controls follow the
permissions reported by `/api/access`; every operation is still authorized by
the backend.

Business names and choices come from stored data. Migration 0026 adds required
display names to curves, rate curves and roles; 0027 adds authored TOU products
(drafts, approver publication, immutable published versions bound to a sealed
clock rule, current-workspace only) edited on the Power TOUs page and seeded with
seven common ISO peak conventions; 0028 records the ISO `weekend_mask` meaning and binds
new schedule versions to an exact reference calendar (`date_schedule_v2`) under
the existing draft, publication and temporal rules, with a row-security check
that the calendar's reference catalog is readable. Power holidays come from the
settlement index calendar, product clocks from clock rules, rule choices (schedule
anchors, adjustments and selection, shortcut date roles, identity kinds, power
blocks and classifications, rate convention labels) from the validating backend, and an IANA clock is
refused unless its declared tzdb release equals the installed release. Economic
power choices (curve, period, product, hours, interval) start unselected.
Retained `date_schedule_v1` inline calendars remain readable and are never
reinterpreted.

Schedule calendars must come from a sealed reference revision (a database
trigger enforces this for every writer), and readers receive published versions
only. An IANA clock also requires an installed zone file and an unchanged tzdb
release across loading; cached zones are cleared first. Known limits: in a
historical view, a reference calendar is visible only while a market revision
that pins it is visible, so a schedule outside that coverage fails with
`calendar_reference` rather than resolving; and the disposable fixture declares
its host's installed tzdb release, so the release check protects stored rules
moved between hosts rather than the fixture itself.

Curve display names reach every screen through one served directory
(`GET /api/curve-names`, all bundles of the catalog), so trading views show stored
names too; an unknown or conflicting code is an error. Migration 0026 refuses a
database that already holds curves until a verified naming migration supplies
them. The exchange example seeds the same published ISO TOU products on its own
market clocks, and its power indexes settle on the NERC-holiday calendars of
those clocks.
## Amount display and unit conversion

Deal editors expose final money decimal places under collapsed Advanced settings;
trade, fixed and floating roles retain independent policies. Shared current presentation
is stored against the exact trade identity with optimistic revisions and current
trade read/amend authority. It is independent of financial history, economic keys
and contractual cash rounding. Old browser Profile precision is ignored.
Decimal futures price display follows tick precision. The managed amount types
provide formatted `ToString` overloads using resolved exact-reference labels.
Tables show units alongside quantities/prices; formatting preserves raw sorting,
filters, inputs and exact exports. Display ties use to-even; current initial
presentation choices are 3/4/3/6 decimal places for money/price/quantity/factors.
Only money places are exposed; noncash display trims padding zeros and futures
prices preserve tick places.

Reference data → Units / Unit conversions provides a typed quantity converter.
The consumer uses stored exact ratios, inverse paths and explicit final rounding,
rejecting incompatible commodity/basis/dimension/version or missing paths.
`UnitPrice.ConvertTo` preserves price-times-quantity cash using the inverse ratio.
`UnitPrice.Times` defers intermediate multiplication to the existing exact-money
aggregation path. Gas composition/heating-value, density and FX conversions are not enabled.
Power duration/allocation has a separate unsaved consumer below. An optional exact supplemental unit bundle preserves
existing market definitions while giving the converter its selected catalog.

## Legal entities and counterparties

**Settings → Entities & counterparties** provides create/edit/history controls
for the existing versioned models. Codes are permanent within their party type
and catalog. Edits append name/effective-time revisions with an expected head;
equal effective instants remain distinguishable by publication knowledge.
Trades keep exact party references. Operations retain actor, request and relational
result references for safe retry, including after subsequent edits. Delete,
deactivation, corporate hierarchies and broader attributes are not implemented.

The market-data-editor template has `ReadTradeParties`/`WriteTradeParties`;
trader/risk templates have party read access. These permissions do not grant trade
read/booking or access administration. Reads use the selected effective/knowledge
context; historical writes reject. Denied or context-stale rows are cleared, and
uncertain save identities are retained separately by account/workspace.

The power example accepts typed quantity inputs with explicit total MWh or flat
MW basis. Total energy requires a selected allocation; the supported profile uses
actual selected interval durations. `ExactQuantity` retains fractions until final
rounding. Settlement requires the exact index quantity specification and returns
exact cash evidence and typed Money. Each example has editable display precision.
Compatible quantity definitions must exist in its selected reference revision;
another revision is not an automatic substitute. Booked swap sides do not yet
persist this quantity rule.

## Current trading implementation

Trade booking now consumes separate immutable `LegalEntity` and `Counterparty`
models with exact catalog/ID/revision keys. Both party versions are required on
new futures/swap bookings and amendments. Migration 0029 adds immutable booking
facts for the original authenticated human user, server persistence time and
UTC booking date; clients cannot set them. Economic effective time is separate.
Shared Trade Terms render above product/side editors; the quantity belongs to
the instrument binding, with no editable trade-wide total. The bounded operation
still supports one instrument binding per trade.
Retained states, history, optional blotter columns and exports expose the selected
facts. Party-only corrections append a revision without changing the reusable
unit instrument. Migration 0018 preserves legacy rows with NULL facts, adds exact
foreign keys/RLS and V2 event schemas, and closes older runtime write entrypoints.
Initial synthetic party versions are installation fixtures; the bounded name/code
editors described above are implemented. A general reference-authoring
or party-onboarding workflow is not implemented.

`IdentityField` marks economic properties and `EconomicDefinition` pins their
canonicalization contract on each executable instrument/side type. Explicit
encodings include normalized decimals, exact references, conventions and ordered
memberships. Canonical payload equality accompanies SHA-256 lookup. An owned
identity map validates futures definitions; swap unit-value reuse keys by economic
identity, including its explicit discounting choice, under the selected full context. Database
economic uniqueness and the publication gate continue to deduplicate persisted
definitions atomically. No global result cache or new numerical dependency is
introduced. Booking lookup uses typed primary/candidate keys enforced by database
indexes; managed digests support operation-local reuse.

`FuturesContract` is a leaf instrument with normalized amount one. Its marked
SeriesKey is the existing exact `ref_data.futures_series` primary key; LotSize and
settlement method resolve from its retained product/milestones. `CurveModel`
provides primary-key and constituent lookup returning one contract instance per
model, available before booking. Explicit `FuturesMarketBinding` holds curve/member
and quote-admission policy outside contract identity. Changing only a recipe or
pricing policy does not create a different future. Final exact P&L applies quote
multiplier, LotSize and external lots once.

Migration 0019 retains old UUIDs/events and adds typed exact-series aliases and
per-revision pricing references with FKs, RLS and immutable publication coordinates.
New bookings use the canonical series-key alias and create no futures side/wrapper.
Missing revision pricing metadata fails closed. Futures holdings/risk expose
`pricingBindingId` and separate canonical instrument identity; old operation-status
payloads retain `sideId` as a compatibility name for original request replay.

Fixed-side identity includes its exact quote key, matching database candidate-key
lookup. Floating-side identity names its curve and contractual observation/averaging/
payment configuration; actual projection references have a separate binding identity.
Swap calculation reuse includes both identities and the retained published UUID.
Instrument interning rejects a conflicting binding rather than substituting it.
Swap definitions mark Discounting, and common/specialized PV reject conflicting
policies. Existing market series, observations, explicit derived dependencies and
immutable scenario risk already separate market inputs from contractual cashflows.
Standalone physical forward booking, option products and VaR/covariance remain
unimplemented; their agreed design is recorded in architecture/plan.

Trading opens a shared Blotter with individual futures trade bindings, managed
pivots, account-saved layouts, exact exports and shared charts. Eight synthetic
weekday bookings exercise cumulative holdings across the four standard frames.
Leaf futures contracts retain exact market terms; composed instruments retain nonempty immutable side memberships.
Pricing reports typed gross execution P&L and open quote sensitivity; it does not
implement futures PV, settlement cash, variation margin or fees.

The editor opens from trade rows. Amendments append immutable latest-head booking
states with expected revision, explicit reason/effective instant, current authority
and idempotent operation identity. Previous states remain read-only. Book moves
and amendments preserve stable trade references; reusable instruments have their
own references. UUIDs remain relationship keys. New table layouts show references;
existing saved layouts retain their selections.

The header distinguishes working knowledge, retained history, selected price date
and actual today in the user's time zone. Disabled booking has an explanation and
an explicit return to working inputs. Power TOUs and Reference data are under
Settings. Visible branding is LeanETRM. These presentation choices do not alter
financial contexts or authorization.

Migrations 0011 through 0015 add certified trading publications/contexts, futures
bookings, account layouts, immutable trade revisions and readable references.
Both trading backfills acquire the publication gate and lock their source tables.
Migration 0013 additionally requires stopped runtime write admission and drained
legacy calls: the gate cannot replace a function body already executing.
Disposable upgrade checks preserve original bookings and earlier certificates.
See [database storage](../db/README.md) and [verification](verification.md).

## Financial swap numerical core

The managed core now includes independent FixedSide/FloatingSide definitions and
a FinancialSwapInstrument with normalized currency and quantity bases. Delivery
and payment terms are independently owned by each side. Opposite membership coefficients carry pay/receive; positive typed trade
quantity scales the result once. Contractual fixed prices retain PriceUnit without
inheriting the observed quote's domain or tick. Floating projections must equal the
originating model's retained outright index, quote and exact dated contracts.

Supported core averaging rules are monthly index, every calendar day, equal
business-day observations, calendar-day weighting using the preceding/following
eligible observation, and explicitly supplied positive daily quantity weights.
Business-day rules require a covered exact calendar. Every required projection
must be present in date order; unsupported rules and gaps fail explicitly. The
bounded delivery interval is at most 366 days. Legacy projection-only sides reject
valuation at or after their declared first fixing. Indexed sides consume exact
pinned actual fixings from their qualified schedules (D165, D172); settlement
remains future work.

Each side applies its own quote multiplier and payment factors to exact rational
cash amounts. Exact side PVs survive signed instrument aggregation and later
trade quantity scaling. Repeating averages and final amounts remain exact
rationals. A consumer explicitly requesting working-decimal Money rejects an
unrepresentable amount; rational PV and risk outputs do not round to satisfy it.
The currency-default discount consumer builds rates from the same full snapshot,
retains exact mapping/curve and bracketing observation identities, and uses the
existing explicit simple Actual/365 Fixed rate policy. Log-linear interpolation of
stored 18-digit discount factors uses an exact integer geometric root and a single
nearest/ties-to-even at 18 digits output rounding. Pillar gaps are bounded to 3660 days;
extrapolation is rejected. discounting=false uses one without requiring a default
mapping or rate observations. Payment dates use the declared UTC rate date basis.

Each Side now declares GenerateSchedule and PV behavior. FixedSide and
FloatingSide independently generate their own payment plan, resolve their own
price inputs and evaluate their payment PVs through the shared linear payment
pricer. The instrument delegates to Side.PV, validates common currency/unit and
the same valuation-operation identity (including scenario and discount inputs),
and sums membership-signed exact side PVs. Cross-operation cached side results
are rejected even when snapshots match. Standalone snapshot/rate paths reject
conflicting contents under one observation identity.
The financial-swap trade-valuing operation applies positive typed trade quantity
once after that sum. The current instrument implementation is
`independent_side_financial_swap_v2`.

Each constituent FixedSide or FloatingSide has one payment. The managed
`MonthlySwapSide` composes these into a multi-payment strip with explicit rational
quantity allocations; `MonthlyFinancialSwap` supports one to 1,200 contiguous
complete months. Booking and the trade editor still persist one month.
Delivery/payment dates, calendars, lookup rules and quote multipliers belong to
each constituent side. SideValuationContext carries no
instrument, membership sign or trade quantity. A standalone fixed side can value
with only the selected snapshot and rate inputs; a floating side additionally
requires its declared model-owned market projections. Returned side schedules
are the actual evaluated rows. Shared payment dates do not merge the side rows.
The explicit scalar-factor compatibility operation rejects different payment
dates when discounting is enabled; the rates operation resolves each side's DF.

Rate-based rows retain exact currency/default mapping, curve definition and used
pillar maturities, annual rates, factors, observations and interpolation policy.
Side payments/side PVs use `independent_side_rational_pv_v1`; instrument aggregation
uses `signed_exact_side_pv_sum_v1`; final financial-swap trade scaling uses
`financial_swap_trade_scale_exact_rational_v1`. ExactMoney retains rational or
large side amounts through all those stages. Working-decimal Money conversion is
an explicit projection that applies its precision/range; exact trade PV and risk
remain rational. No rounded unit or gross-side PV is substituted.

FuturesContract explicitly rejects full PV without required variation-margin and
settlement state. Its existing execution-P&L/open-sensitivity operations remain
available through the common valuation contract. Sides implement schedule
generation; a universal instrument schedule interface remains proposed.
OptionSide is not implemented: explicit sibling references, base-versus-final
valuation modes and exercise-control double-counting constraints remain recorded
in the architecture; O13 does not yet select an adjustment or joint allocation.

These managed contracts now have persisted default resolution, authorized swap
booking/amendments, a shared blotter and a schedule-inspection editor, described
below. Draft swap risk, interactive booked-portfolio risk and generated-close
mixed-product reports are implemented. Interactive and generated-close consumers
share portfolio materialization and scenario calculation. Fee capture, calculation
and reconciliation remain work; current futures execution-P&L diagnostics are
gross and exclude fees.

The futures editor separates trade inputs from a vertical side panel populated
from actual selected contract metadata. The financial swap editor has aligned
fixed/floating columns and initializes only unset counterpart delivery/currency
fields. No copy button is present; later edits remain independent.

## Business scenario coverage and permanent browser regressions

[The scenario catalogue](business-scenarios.md) now maps current executable
business/edge cases separately from planned OTC fixing, futures variation-margin,
active schedule-roll integration and reusable scenario/risk behavior. Eight new
managed expiry-transition checks cover the early-expiring futures source through
its dependent's later expiry, exact transition, missing/wrong/provisional/premature
finals, observation cutoff and ignoring stale live inputs under an explicit frozen
final rule. They test the current price transition, not settlement cash accounting.

The 41 temporal and administrative browser regressions are maintained under
`tests/browser/`: 17 temporal and 24 administrative checks, invoked through the
owned preview runner's `--check --browser-check --browser` options. Target validation
requires the launcher's private marker, matching health run identity, loopback and
its owned database directory. Each suite uses a fresh browser profile. The runner
starts the browser runner in its own process group and awaits teardown of all live
members before removing profiles/database files, including Node timeout/interrupt.
Unconfirmed cleanup fails qualification and retains files. Three real-process
checks cover normal termination, SIGTERM-resistant descendants and a browser-like
child outliving its launcher. macOS refuses to signal a group whose remaining
members are already exiting (`EPERM`), as a large browser renderer can still be when
its launcher returns; two more checks simulate that refusal: such a group is awaited
until it is gone, and one that outlives the refusal still fails cleanup. Browser
regressions exercise lost post-commit and malformed gateway responses, exact
idempotent retry, actual two-session access revocation, stale-form clearing and
current permission boundaries.

The scenario-engine contract in that catalogue is proposed next work. It specifies
typed immutable hypothetical overlays (separate from observation corrections),
fixed facts, dated identities, declared dependency invalidation, shared black-box
PV, explicit quantity reuse and independent full-repricing/work-count checks.
Four additional power-curve regressions verify negative outright/basis prices,
independent all-in exposure and signed exact-money multiplication. Proposed power
curve specifications and PWR01–04 scenarios cover intervals, shapes, MW/MWh,
market clocks, partial fixings, components and explicit balancing semantics. No
hourly power lifecycle, reusable shocker, OTC fixing operation, margin ledger or
production authentication was added by these test/documentation changes.
The [CI workflow and shared verification command](verification.md) passed the
complete suite on the Ubuntu 24.04 hosted runner on 2026-10-17, including browser
sandbox and owned-resource teardown checks. Production deployment remains work.

## Shared historical market view and administration

The qualified historical context now covers the existing commodity curves,
interest-rate curves, market observation histories, exact immutable definition
and reference bundles, published schedules and contract-date resolution. This is
not a trade/lifecycle implementation: those business domains do not exist yet.

**View this context** applies the common effective date and exact UTC knowledge
cutoff. It explicitly selects the latest known correction of each already selected
source fact. It never chooses another source, fills missing dates, interpolates
or rolls a contract. The context fields are also used by the date configurator;
changing them clears displayed market results/reference rows. Navigation applies
pending coordinates before reading another business screen. Authoring a schedule
example alone is a private preview until the environment is explicitly applied.
**Return to current inputs** restores the separately labeled exact-input editing
mode. Its legacy pins are retained inputs, not wall-clock-certified snapshots.

Market dates are distinct from knowledge dates. A main price calculation requires
the selected input frame's UTC date to equal the effective context date; missing
prices do not silently use an earlier frame. History charts explicitly sample
earlier market dates with that same knowledge cutoff. Inputs after the effective
day are excluded. A future contract can still be present if its immutable
specification was effective and known in the selected environment.

Migration 0009 records sealed definition bundles and observation imports in a
separate ordered commit-event stream. It uses the existing publication/commit
gate and exact knowledge cutoff, but has its own prefix and immutable commit
certificates. Source `received_at` remains audit metadata, never knowledge proof.
Definition bundles have explicitly supplied half-open applicability periods;
reference closure follows their exact sealed version links. Point prices retain
their observation instants and explicit correction chains; no artificial
carry-forward validity interval is invented for a sampled price.

`use_market_context` binds one retained context to the authenticated database
transaction exactly once. Restrictive RLS hides later observations, unavailable
bundles and unrelated reference versions even from raw runtime SQL. Historical
INSERT/UPDATE operations are blocked by restrictive policies and permission
admission; definer publication/sealing operations cannot bypass that boundary.
Imports and sealing recheck current permission after waiting for their commit
gate. Certificate and context stores remain private-write, immutable and scoped.
A concurrent uncommitted import is excluded from a cutoff before its actual
commit, even if its receipt timestamp predates the cutoff. Restoring current
access permits exact old replay; old context IDs never restore revoked rights.

**Users & access** now provides workspace-scoped identity creation, explicit fixed
role assignment/replacement, display-name changes and access suspension/restoration.
Humans, services and agents retain distinct immutable kinds. An agent role alone
still cannot act without the existing delegation intersection. Administrators
have no implicit market-data read permission; their screen loads without fetching
the market workspace. Identity switching clears old market and administration
content, and a denied administration reload clears its tables, the agent grant cards
included, and edit forms. In the local preview, Users & access also shows every agent
grant in force in the workspace and the latest grant changes, read-only
(ai-assistant.md §7.1).
Security administration always operates on current authority, irrespective of the
historical business view.

Workspace membership controls are separate from global principal status: suspending
a member here does not disable that identity in another workspace. Administration
uses narrowly granted definer operations; runtime has no direct access to the
private membership/audit tables. Mutations take exclusive security-epoch admission
before shared admission and reject attempts to upgrade a bound request. Every
change checks the expected member version and atomically records actor, scope,
request identity and before/after state in an append-only audit. Concurrent exact
retries produce one account and one audit outcome; payload reuse conflicts. The
last current administrator is protected, and removing one's own administration
requires another administrator.

The browser retains uncertain commands in session storage scoped to preview run,
tenant/workspace and identity. Retry sends the original payload and request ID;
editing a pending payload cannot silently create another action. Only explicit
known rejections discard it; lost responses and unrecognized server/gateway
failures retain it. The directory shows the latest 100 committed audit entries;
older records remain in storage. Preview limits are 256 members and six fixed
role templates, not a generic policy language or role editor.

Authentication in this disposable preview is simulated. The synthetic fixture
provides selectable personas, including **Access Administrator**. New human preview
identities can be selected in the creating browser session. Registration does not
create a password, send an invitation or verify external credentials. The preview
issuer/login namespace is global to this disposable installation; production
identity linking, MFA, external agent credential custody and deployment hardening
remain separate work. The separate hosted mode implements operator-provisioned
invitations, password sign-in/reset and initial-owner setup; see
[private pilot operations](hosted-pilot.md).
No production identity, database or existing application was modified.

Verification for this increment: 60 frontend tests, 184 standard HTTP checks,
24 mixed-resolution HTTP checks, 14 schedule-storage checks, 50 temporal/security
storage checks and ten adapter checks pass. Source review includes independent
adversarial temporal, authorization and async review. The final owned preview passed 41 independent browser checks (17 temporal and 24
administrative), with zero uncaught JavaScript errors. These cover common context
navigation, rates, correction replay, real two-session revocation, lost post-commit
responses, malformed gateway responses, administration and idempotent retries.
Desktop and 390px mobile screenshots were inspected.
These counts qualify the synthetic preview, not production or performance.

## Bitemporal schedule contexts and current authorization

The first certified time-based context path now covers published schedules and
contract-date binding. It adds a shared half-open `BitemporalRange` and retained
server contexts with an effective business date and exact UTC `known_at`. The
browser's **Known on · UTC** control replaces publication-number authoring;
**Use current knowledge** explicitly captures a new context. Minute display keeps
the exact retained timestamp underneath. Editing either coordinate discards the
old context identity. Literal dates and every reference in a candidate use the
same resolved context. An unchanged candidate reuses that immutable context.

Knowledge means certified engine-recorded commit time on the qualified primary.
This is not a claim about client acknowledgements or replica visibility. The owned
preview enables stock PostgreSQL commit timestamp tracking at startup. A narrow
routine records the publisher's full transaction identity, validates its epoch
against a fresh snapshot horizon before reducing it to the engine identifier,
and persists the verified commit time. Publication and certification share their
transaction fence through commit. A request that starts before another publisher
can correctly include or exclude that publisher according to its eventual commit
time, rather than the request's transaction number or a pre-commit row timestamp.

Each catalog retains a coverage origin, certified watermark and committed prefix.
A new commit cannot enter a previously certified time interval. Missing retained
commit metadata, future/precoverage cutoffs, unsupported tracking, timeline changes
or clock regression raise explicit errors. There is no current-data fallback.
Engine microsecond precision is explicit; finer API inputs are rejected. Equal
commit times form one boundary group, and empty temporal rectangles are excluded.

Read-only temporal facts expose `effective_from/to` and `known_from/to` by splitting
business intervals at retained correction boundaries. A February-only correction
leaves January and March unchanged. Context reads use the certified prefix in
that projection so later publications cannot alter an earlier receipt's bounds
or provenance. Original published versions and certificates remain immutable.
Existing explicit `knownThrough` API requests remain logical-prefix replay; they
do not claim timestamp certification and are not used by the normal context UI.

RLS and current permission checks apply to schedule history, commit certificates,
context receipts and temporal fact queries. Readers can capture/replay contexts;
editors can write drafts; approvers can publish. New context identifiers confer no
authority. Runtime cannot write or alter certificates or mint its own proof rows.
Permission is checked after admission waits and again after certification work.
A retained context fails after current read access is revoked and resumes exact
replay only after current access is restored. Scope checks also protect direct
SQL, not only browser routes.

Migration [0008](../db/migrations/0008_temporal_knowledge.sql) adds three bookkeeping
stores consumed by this workflow; it adds no generic business-table framework.
It requires tracking enabled before publications and rejects installations with
pre-existing unqualified publications instead of inventing a backfill. The preview
uses fresh owned databases. Failover/restore migration and tracking-history repair
require separate evidence; system/timeline changes fail fresh certification.
Previously retained snapshots remain immutable replay inputs under current access.

This schedule-specific foundation is now consumed by the shared market view
described above. Durable curve activation, trade/lifecycle history, arbitrary
cross-catalog activation and operational recovery remain outside this preview.

Verification: 31 shared temporal checks, 20 schedule adapter/resolution checks,
57 frontend checks, 162 HTTP checks, 14 schedule-storage checks, 25 temporal/storage
security checks and ten persisted-adapter checks passed. Independent source review
passed. Final isolated-browser testing passed 29 checks, including exact replay
across two cutoffs within one displayed minute, partial corrections, candidate
reload, explicit coverage errors, role controls and 390px layout. Desktop/mobile
screenshots were inspected. These results are not throughput or production claims.

## Independent schedules, publication and contract dates

**Settings → Schedules** creates date schedules independently of curves. Canonical
references use `oe.name`; configurable namespace aliases remain future authoring
convenience. A schedule generates monthly anchors with explicit first/last months,
day-of-month or month-end intent, short-month handling, business-day adjustment
and its own known calendar coverage, closed weekdays and closure dates. Bounds are
120 months and 3660 calendar days. No exchange calendar or financial rule is inferred.

The author explicitly chooses fixed occurrences or context-month selection. With
context-month selection, `oe.ng` or `oe.ng[0]` resolves the occurrence for the
selected context month; `oe.ng[1]` selects the next occurrence. With fixed selection,
an explicit index selects from the first stored occurrence. Missing context months
fail, rather than choosing a nearby month. `+Nbd` and `-Nbd` use the selected
version's calendar strictly beyond the base. Zero offsets remain unsupported.
A literal `2021-01-01` can be used in place of a reference in the same date field.

Schedules save to PostgreSQL with a stable identity and immutable reference name.
Draft display names and rules can be edited. Saves check the opened version,
state and generation; a refreshed list cannot silently advance a stale form's
concurrency token. Publishing requires the approver's current scoped permission.
A published version cannot be updated or deleted, including through privileged
ordinary DML; editing it saves a new draft version. One draft per schedule and
exact parent/version foreign keys are enforced by storage. Known compiler/
resolver implementation `date_schedule_v1` is retained; an unsupported engine
version fails instead of running the recipe through arbitrary current rules.
Changing those rules requires a new supported engine version.

The shared **Context date** and **Known on · UTC** coordinates are explicit.
Resolution first filters published versions by the effective interval and captured
publication prefix, then uses the newest eligible publication in that prefix.
A new publication does not change an earlier selected view. Publication and prefix
capture hold a common transaction fence through commit, with scoped ordinals;
permission is checked again after lock waits. The displayed publication timestamp
is audit metadata. Timestamp-based certification now exists for the schedule path described above;
broader market/reference temporal context remains unfinished.

**Settings → Market setup → Curve & contract definitions** uses already published
schedules in `start`, `end` and last-trading fields. `date` is used for a single
resolved date. No existing curve specification is required. The example explicitly
binds consecutive months and a last-trading offset of three business days before
start at 15:30 UTC. Users can replace a reference with a literal date. The same
context resolves every field, and receipts retain source identity, version,
implementation, calendar and context. Changing context clears stale output.

The shared contract-date library now retains reviewed date rules as durable
workspace drafts and immutable published versions (D167), replacing the earlier
session-only candidate list. Units, market specification qualification, dependency
mappings and marks remain separate from schedule creation. Explicit save and
publish operations do not activate or price contracts. Each field has a literal
date or an exact published schedule-version foreign key; occurrence indexes,
business-day offsets, the validation-context foreign key and UTC cutoff are
retained. Readers can resolve a selected published version in an explicit context
without rebinding its sources to newer schedule publications. Drafts require
current authoring access, publication has its separate permission, and historical
visibility uses certified commit receipts. Migration 0054 and its qualification
requirements are described in [the schedule contract](schedules-and-contract-sequences.md#shared-contract-date-library).
The owned preview database is removed when its launcher stops.

Migration [0007](../db/migrations/0007_date_schedules.sql) adds two market setup
tables, justified by this authoring consumer. It adds no reference catalog or
external dependency. RLS uses existing scoped read, definition-writing and sealing
permissions. Runtime cannot update publication state or canonical reference names.
API operations are under `/api/date-schedules`: list/capture, save, publish,
resolve and contract preview/save/publish/exact-version resolve. API and UI share
the same operations. Contract save/publish responses are control-only receipts;
their request identities retain a SHA-256 command hash and exact version foreign
key, followed by an independently authorized library read.

Earlier schedule-slice verification: 27 shared temporal checks, 11 contract-sequence checks, 20 schedule
adapter/resolution checks, 57 frontend checks, 140 HTTP checks, 13 adversarial
schedule-storage checks and ten persisted-adapter checks. Storage checks include
published/identity immutability, stale generations, finite dates, scope isolation,
publication idempotency and capability expiry during a blocked publication.
Independent source review passed; independent browser lifecycle checks cover
save/rename/version publication, context/view changes, literals, contract bindings,
separate-browser persistence, private candidates, filters and mobile layout. The
final browser run passed 52 checks, including two-editor stale-form reload and
concurrent-publication saves returning conflicts without overwriting or forking.

## Imported contract date lookups

The collapsed **Inspect imported contract dates** tool creates a private lookup over an explicitly supplied
contract schedule. Calendar combine/compare/difference remain library operations;
they are coding capabilities rather than the primary editing workflow. No new
tables or dependencies were added.

The form specifies a name, symbol such as `oe.ng1`, source schedule, positive
contract position, date role and test context. The symbol's spelling does not
infer its product, ordinal, date role or roll convention. The synthetic example
explicitly configures position 1 and delivery start. Supported returned dates
are delivery start, exclusive delivery end and supplied last-trading business
date. The source table shows dated contracts and their first/last trading terms.
**Before expiry** and **At expiry** test the selected recorded boundary. Exact
server instants survive minute-only display, including subminute boundaries.

The reusable managed `ContractSequence` and `ScheduleShortcut` live in
[src/OpenEtrm.MarketData](../src/OpenEtrm.MarketData/ContractSequence.cs). Resolution
uses a binary search for the first unexpired member, then validates eligibility
through the requested position. It advances exactly at expiry, never skips an
ineligible expected contract and never consults prices. A receipt retains the
symbol, exact sequence/reference revisions, rule version, position, date role,
valuation instant, dated ContractKey and date. Shared named definitions reuse a
compiled immutable sequence without merging dated contracts or histories.
Future sides/instruments can consume this same managed contract; they are not
implemented consumers yet.

The first adapter supports futures whose expiry is explicitly bound to the
retained last-trading milestone. It reads existing exact contract/series/milestone
links under scoped authorization. The standard synthetic fixture now supplies
first-trading milestones for all six futures plus explicit UTC resolution
coverage. Missing or date-only required trading instants fail; no midnight,
current-data or eligibility default is inferred. The mixed-forward fixture has
no configured sequence rather than inventing its listing/roll rules.

The preview API exposes `GET /api/schedules` and `POST` operations under
`/api/schedules/preview`, `/keep` and `/resolve`. The resolve request accepts a
kept symbol and a new explicit UTC instant. Kept definitions are private artifacts
of one browser session and demo persona, limited to 32 symbols, with no overwrite.
An identical keep is idempotent; different terms under an existing symbol return
a conflict. Current market/reference authorization is checked on every operation,
including re-resolution; stored artifacts grant no additional access. These are
not published reference definitions, durable administration or a historical
knowledge-to-revision resolver. Existing publication/permission gates still apply.

UI edits hide old results, pending operations disable controls, persona switches
clear the editor and private list, and timezone preference changes refresh only
display labels. Returned business dates never change with the profile timezone.
No automatic trade rollover, general recurrence/date-language engine, generic payment
schedule editor, VaR, covariance or rolling price-history builder is claimed.

Historical verification for the earlier rolling-shortcut slice: 11 managed sequence checks, five targeted adapter checks, 55
frontend checks, and 79 HTTP plus ten persisted-adapter checks in a fresh owned
standard preview. Independent adversarial review covered ordering, temporal
boundaries, eligibility, precision and current authorization. An independent
web tester passed 36 checks in an isolated headless browser, covering exact
subminute expiry requests, saved-symbol reuse, conflicts, profile isolation,
timezone refresh, stale-result clearing, existing curves and 390px mobile layout.
Desktop/mobile screenshots were inspected. These are synthetic preview results,
not production or cross-browser qualification. Targeted commands:

```sh
dotnet run --project tests/OpenEtrm.MarketData.Checks -- --contract-sequences
dotnet run --project tests/OpenEtrm.Api.Checks -- --schedules
```

## Implemented scope

The managed library in [src/OpenEtrm.MarketData](../src/OpenEtrm.MarketData)
constructs immutable commodity curves from explicitly selected observations.
Supported operations are direct prices/spreads, base plus spread, and typed scaling
plus spread. Every constituent mapping is explicit. This first mapping
subset requires equal delivery periods and the same delivery-profile reference.
Unsupported shaping, interpolation, FX and automatic unit conversion are rejected
through the type/mapping contracts rather than inferred.

The [synthetic acceptance case](examples/mixed-resolution-gas.md) now qualifies
managed mixed daily/monthly views and separately quoted BALMO contracts through
equal-period benchmark/basis bindings. It adds 24 checks and an optional 87-row
synthetic CSV. The test includes an expired monthly future final to prove it is
not a fallback for missing BALMO marks. Monthly tail inputs are forward marks.
The fixture is now persisted/adapted through an explicit `--example mixed-gas`
disposable preview. Eight saved dates pin their per-curve contract selections;
month-end BALMO returns an explicit empty delivery window. Commodity histories
evaluate the exact requested dated member, including saved marks outside the
displayed window, and retain unavailable/expired samples as labeled chart gaps.
Corrections retain their source frame's selections. New preview instances serve
their own frozen web assets. Unequal-period
monthly-base projection, BALMO shaping, aggregation and OTC fixing rules remain
unsupported. Existing explicit final-future freezing does not qualify those rules.

The synthetic example has five levels: futures, base-location gas forwards,
regional gas forwards, hub power forwards and local power forwards. Shared
constituents evaluate once per build, and each result retains exact observation
lineage. All-in curves have only their declared direct inputs. Instrument pricers,
shared position risk and scenario APIs now consume this model. Full futures
settlement and variation-margin processing remain work.

Curves carry the common quote, location, delivery profile and settlement target.
Dated constituents retain delivery/expiry and exact futures-series links. Reusing
a contract within a definition bundle requires compatible quantity, currency,
location and profile specifications. Resolved futures metadata includes quote,
lot size/basis and dated terms. The database checks product economics and the
known last-trading milestone; the managed model requires the exact resolved
futures specification supplied by its caller.

The SQL and managed core fixtures test their respective boundaries. The preview
now includes an adapter from the exact persisted sealed bundle into the managed
model. It verifies the bundle's market and reference pins, uses invariant ISO-date
parsing and loads reference relationships through scoped RLS reads. The managed
core itself receives resolved inputs; it does not query a reference registry or
establish entitlement/approval. Cross-revision identity compatibility and qualified
ingestion remain gates before exposing general history APIs.

## Units and monetary precision

[0002_market_data.sql](../db/migrations/0002_market_data.sql) adds commodity
measurement bases, quantity specifications, quote specifications, uniform delivery
profile definitions, generic numeric value specifications, settlement target links
and cash-rounding conventions.
Energy and volume specifications can coexist for one commodity; their conversion
is not inferred. Legacy products/rate definitions still use the initial canonical
commodity-unit links. Mixed-basis product/rate extensions remain further work.

`Money` carries an exact currency reference. `Quantity` carries an exact
commodity/basis/unit specification. `UnitPrice` carries its quote specification,
including currency, denominator quantity and quote multiplier. Price × compatible
quantity returns money and applies the explicit quote multiplier. Arithmetic
rejects incompatible references; display minor units never select cash rounding.

The first supported numerical policy is
`exact_18_fractional_abs_lt_1e24_v1`: plain decimal inputs with at most 18 fractional
digits, magnitude strictly below 10^24, exact addition/subtraction/multiplication,
and checked bounds at every intermediate operation. Integer arithmetic implements
the policy; no binary floating-point or silent decimal multiplication rounding.
An operation needing additional precision raises `precision_loss`. This is a
qualified narrow arithmetic mode for this slice, not every future pricer's policy.

Cash rounding is a separate call with exact convention, currency, increment,
tie rule and requested stage (`cashflow` or `payment_total`). Supported ties are
to-even and away-from-zero, including negative values. Trading ticks and clearing
increments are separate metadata. Actual daily/final settlement observations must
meet their clearing increment; derived curve values are never rounded to it.

## Interest-rate example and numerical policy

The synthetic **Interest rates** curve has three dated zero-rate samples and four
saved market dates. Each maturity has its own generic numeric series and retained
observation history; it is not a fictional commodity contract. Inputs are annual
decimal rates (`0.025` means `2.5%`). The screen displays percentages and discount
factors; corrections retain old selections and affect only the corrected sample.

[0006_rate_curves.sql](../db/migrations/0006_rate_curves.sql) adds `rate_curves`
and `rate_curve_points` with exact interest/source/series links, real maturity
dates, increasing ordering, immutable sealing and forced RLS. Shared series cannot
have conflicting maturities. Inputs must be non-price, dimensionless numeric
series; missing samples never become zero or an interpolated value.

The selected specification explicitly stores `actual_365_fixed`, `simple` and
`openetrm_rates_v1`. The curve explicitly chooses `valuation_utc_date`: its
reference date is the UTC date of the selected valuation instant. Display zones
never change it. Only exact maturity pillars are supported, without interpolation,
extrapolation, holiday adjustment, bootstrapping or cashflow PV. Additional
conventions need a concrete consumer and separately qualified behavior.

[RateConventions.cs](../src/OpenEtrm.Rates/RateConventions.cs) is an original,
framework-only implementation. Actual days are the half-open DateOnly difference,
including leap days; Actual/365 Fixed always divides by 365. Simple discounting is
`1 / (1 + annualRate * actualDays / 365)`. Integer-ratio intermediates are exact,
with no floating-point conversion, global date state or external financial library.

The numerical policy is `simple_actual365fixed_rational_to_even_18_v1`.
Discount factors divide once to 18 fractional digits using nearest/ties-to-even;
trailing zeros are omitted. Presentation independently rounds the original rational
to six digits using the same tie rule, avoiding double rounding. This explicitly
selected rate-output policy does not change Money/Quantity arithmetic or choose
cash rounding. Supported annual rates are [-0.1,1], dates 1901–2199 and maturities
strictly after the reference date. Nonpositive denominators are rejected.

Twenty-nine rate checks cover known factors, negative/zero rates, actual leap days,
18-digit and six-digit even ties, avoiding double rounding, the zero-denominator
boundary, large positive factors near the pole, unsupported conventions and exact
sources/pins/cutoffs. Independent numerical review covered the original formula
and its date, domain and rounding boundaries. Future conventions require equally
specific test vectors and new explicit identifiers where behavior changes.

## Immutable inputs and source expiry

Definition bundles accept inserts while draft and become immutable when sealed.
Sealing validates completeness, types, cycle freedom, mappings and futures terms.
Each definition write locks and increments the bundle generation: a concurrent
sealer with a stale repeatable-read/serializable snapshot must retry. Sealing
establishes neither economic approval, data rights nor knowledge publication.

Price and numeric observations share the same storage. A `series` row identifies
an exact dated contract/quote or a reusable numeric input with a value specification.
The first non-price types are quantity ratio and dimensionless number. More value
kinds require their own supported typed specifications. No fictitious contract is
needed for a numeric series, and there is no `fixed_heat_rates` table.

`curve_inputs` binds a multiplier to either an exact numeric series/source/role or
an explicit typed constant. The first multiplier binding applies to the curve's
constituents as declared; delivery-dependent multiplier mappings remain future
scope. A missing series observation raises an error, never a constant fallback.
Ratio orientation is numerator=input quantity and denominator=output quantity.
Only the named direct/add/scale-add rules execute; no general scripting is enabled.

Observations append separately against sealed definitions. Source event/revision
identities are unique. Corrections retain and must reference an already existing,
compatible predecessor. Updates, deletions, forward-reference corrections and
correction forks are rejected. Duplicate source-revision writes are conflicts at
the SQL boundary; an idempotent ingestion operation is not yet implemented.

The managed snapshot takes exact observation selections under the explicitly named
`ExactPinnedFinalObservationsNoAgeFiltering` policy. This narrow policy intentionally
applies no age filtering to supplied pins; it does not select or carry forward
values. Production source priority, sampling and staleness policies are unimplemented.
Snapshots pin one coherent variable value per series, with separately
named frozen inputs. No implicit latest lookup occurs.

Source-expiry policies supported here are `require_live` and
`freeze_final_future`. The latter requires an explicitly selected final-contract
settlement from the declared source at/after the resolved future expiry. It never
substitutes a daily settlement or rolls. Missing final inputs raise an error.
Forward settlement targets and payments remain independent of upstream futures.

Both SQL and managed instants use the range from year 1 up to, exclusively,
`9999-12-31T00:00:00Z`; delivery dates also exclude that reserved date. Managed
contexts require UTC instants. Receipt timestamps are audit metadata, not
`known_from`. The separate schedule path now certifies engine commit-time contexts; these market
observation frames have not yet adopted that resolver.

## Pinned histories

`ObservationArchive.ReadPinnedHistory` reads a half-open observation-time interval
for one exact series from an explicit set of observation IDs. Raw corrections are
retained separately. The result never selects a newer correction, carries a price
forward or fills a missing sample. Those pins are supplied explicit inputs, not
certification of a wall-clock `known_at`. Histories are ready to identify sample
columns; synchronized sampling/covariance/VaR estimators remain later work.

## Security boundary

[security.md](security.md) documents seven common role templates, persisted identities,
scoped grants/delegations, protected catalog ownership/sharing and enforced RLS.
Database requests bind trusted authenticated identity to an exact login/transaction,
with a revocation fence and actual-current expiry checks. An agent has a separate
principal/login kind and intersected delegated authority. The managed evaluator
also operates over supplied current security state.

The local preview includes workspace administration screens and atomic access
audit, alongside its restricted market/reference/schedule APIs and PostgreSQL
adapter. Production authentication, general trading APIs, business approval
commands and broader operation audit remain prerequisites for an exposed service.
The storage routines do not implement sign-in or a trading application.

## Verification

Use the SDK pinned by [global.json](../global.json). The MarketData and Rates libraries and their check projects use the framework
only. The API pins Npgsql for PostgreSQL access:

```sh
dotnet run --project tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj
dotnet run --project tests/OpenEtrm.MarketData.Checks -- --mixed-resolution
dotnet run --project tests/OpenEtrm.Rates.Checks/OpenEtrm.Rates.Checks.csproj
python3 tools/check_reference_schema.py --pg-bin /path/to/preinstalled/postgresql/bin --market --security
```

The managed checks exercise numerical/type failures, five-level building, dependency
reuse and independent all-in values, frozen source expiry, immutable correction
contexts, concurrent deterministic builds and current scoped/agent authorization.
The database suite uses a new private Unix-socket-only cluster and tests real
constraints, correction writes and both write/seal orderings at all three supported
isolation levels. No native database build is performed. Passing checks establish
these bounded contracts, not a production application or performance benchmark.

The current local verification result is 90 managed core checks, 29 rate checks and 173 combined
database checks. Independent adversarial reviews covered the series/unit/history
boundary and SQL authorization/RLS. These numbers describe synthetic regression
checks only. No deployment, commit or push has been performed.

## Disposable local market workbench

The TypeScript client in `web/` and ASP.NET host in `src/OpenEtrm.Api/` are an
explicit **local synthetic preview**, not production authentication. Install the
SDK pinned in `global.json`, Node with npm, and stock PostgreSQL 16+ binaries.

The browser build typechecks with TypeScript and uses the pinned build-only
esbuild 0.28.2 dependency to bundle the application entry. This reduces module
requests during startup. The existing chart and table vendor modules retain
their separate files and license notices; the deployed application needs no
Node runtime or bundler.

Startup explicitly requests compact bootstrap metadata and an access projection.
For a selected, verified identity, the server reads its current permissions in
the same response; the browser validates the identity and consumes that projection
once for navigation. Anonymous local preview still selects its synthetic identity
explicitly and reads access afresh. Profile refreshes, identity changes and every
business operation independently check current authority. The default bootstrap
response retains its existing shape.

`GET /api/workspace/navigation` is a separate typed response for the initial
directory and date picker. Its managed read obtains each curve's explicit market
type and, for commodity curves, its commodity and label; interest-rate curves
have `category: null`. It also obtains all authorized curve display names, exact
bundle/reference/context identities and frame summaries. It does not construct
the pricing model or load observation values or the unit-conversion catalog.
The browser retains navigation separately from the full workspace and hydrates
the latter only for consumers of its definitions, references or source pins.
Hydration must match the same account, selection, context and revision pins;
late responses cannot restore an earlier view. Existing full workspace endpoints
retain their response contracts. Every definition and reference read orders its
rows by the table's primary key within the selected revision, so identical pins
give identical arrays whatever plan the database chooses.

First dated-curve pricing explicitly requests complete workspace metadata with
the evaluation. Both projections share one bound authorized model read; catalog
observations remain empty while the price response retains its selected source
facts. The browser validates both before accepting either and renders the exact
unit/header before the chart. Evaluation without this explicit request preserves
its existing response and avoids metadata-only reads. No-date curve details and
reference inspection continue to use the full catalog endpoint.

An explicit Blotter directory first checks whether either raw futures or swap
revision table contains a visible row under its already bound trading context.
Any such row retains the full valuation path. An empty set uses the same report
projection after validating the view/books, exact sealed bundle and reference
visibility, and completeness of selected observation pins. Directory metadata
and final authority checks remain fresh. Narrow navigation and empty reports
defer validation of economic material they do not consume; priced catalogs
and nonempty reports continue to require complete supported financial inputs.

Migration 0041 removes a duplicate permissive access check on 35 reference tables
whose restrictive visibility policy already checks the same authority on every
invocation. Catalog-only policies and the separate imported-quantity restriction
remain. Exact helper and policy guards reject schema drift before changing these
policies; retained deployments require a separately qualified schema baseline.

Run:

```sh
python3 tools/run_preview.py \
  --pg-bin /path/to/installed/postgresql/bin \
  --dotnet /path/to/installed/dotnet
```

Open the printed `http://127.0.0.1:PORT` URL. The screen opens as the read-only
Viewer demo user. **Market data → Curves & prices** lets users browse by commodity
or market type, with Futures, Forwards and Rates filters. Choose a curve and the **Market date**
dropdown at the top, or click **Explore sample power prices** to explicitly open
local power forwards for 18 Dec 2020. Six delivery months use four saved dates.
Select **Price history** beside a contract to follow its prices over time, distinct
from the curve's delivery-month axis.

**Profile** holds the user's time zone and preferred browsing layout. A versioned
browser-local preference record also remembers commodity/type filters and the
selected detail tab, sidebar/curve-list collapse and individual menu-group states
per demo persona. The Market filter is saved as a commodity code, or as none for
**All markets**. Older profile records keep their other settings and acquire the
new presentation layout defaults, but not their former Market choice: they show
**All markets**. No market-date IDs, input policies, credentials
or authority are persisted as preferences. The preview does not implement account
profiles synchronized across devices; that follows verified sign-in. Unavailable
browser storage is reported, not presented as a successful save.

The navigation sidebar can collapse to an icon rail on desktop or hide its menus
on mobile. Its Market data and Settings groups, the curve list and each commodity
or market-type group expand independently. Returning from the icon rail restores
the saved group state. These controls are keyboard-accessible and remember their
state per demo persona in the browser; they do not change permissions or inputs.
The curve list orders its groups, and the curves within each group, by display
name with the browser's locale collation; for example, **Local power · all-in
prices** precedes **Local power forwards**. Grouped by market type, each group
is one market type. Grouped by commodity, each group is one commodity, and
interest-rate curves, which have no commodity, form the **Interest rates** group.
Groups never merge by name. Interest-rate curves appear under **All markets** and
the **Rates** type filter. The **Market** filter lists **All markets** first,
then each commodity in display-name order. **All markets** is the absence of a
commodity filter rather than a code, so any commodity, whatever its code, can be
chosen on its own. The order depends only on names;
equal names keep the order of the server's directory response.

Every regular table provides **Filter rows**, **Column filters**, a matching-row
count and **Clear filters**. Case-insensitive search matches words across displayed
cells; text in hidden columns is not searched. Column dropdowns list their distinct
available values and match selected whole-cell values exactly, and a column filter
stays active while its column is hidden. Column selections combine with that search. Matching uses displayed strings,
including exact decimals, without parsing or rounding financial values. Filters
only hide table rows. Charts, calculations, selected market inputs and connected
worksheet tables remain complete.

Table filters are transient and scoped to the profile, exact market frame/curve
and relevant contract/view, or reference category/revision. They survive harmless
presentation redraws, reset when those data scopes change and clear on profile
load. Stale detached controls cannot edit a newly rendered table, and controls are
disabled during pending reads/writes. Filter text is not a persisted profile field.

Dates and times display to the minute in the selected profile time zone. Exact
instants and selected version IDs still drive requests. Repeated local clock times
show an offset when necessary; same-minute price versions have distinct readable
labels. Delivery dates retain their contractual calendar identity. User-visible
text, tables, date choices and errors omit GUIDs. Reference data resolves linked
names and units instead of displaying key columns. API keys remain exact underneath.

**Price sources** shows the prices and conversion factors actually used by the
selected curve. **Profile → Preview access** allows an explicit switch to Market
Data Editor. Use **View changes** on the January gas-future price and save `3.1`
instead of `3`: January local power builds from `29.1` to `29.8`. The separately
observed all-in alternative stays `29.1`. This is a data correction and new input
selection. Temporary [risk scenarios](risk-engine.md) use separate immutable
overlays without publishing corrections. Earlier selections retain
their inputs. **View this version** supports explicit reconciliation; the application
never switches to a newer revision automatically. If a write's acknowledgment is
lost, reload and inspect **Price changes** before resubmitting. Changing display
preferences preserves an unfinished edit; changing the financial context clears it.
Switching demo users clears the selected curve before applying their preferences.

The catalog browser separates business **Reference data** from **Market setup**
(pricing, delivery, settlement and rounding configuration). Internal relationships
and version records stay outside the category lists. This is presentation
organization over the same authorized exact-version data, not a new permission.
Location types reuse the existing taxonomy. The synthetic preview includes pipeline,
node, hub, region, terminal, delivery point and physical-site examples, plus a
supplied WGS 84 point on a synthetic facility. Optional paired latitude/longitude
columns are added by migration 0004, with no new table or GIS extension. No route,
region boundary, topology or geographic risk inference is implemented.

The reference inspector includes units, currencies, commodities, locations,
venues, rate/index definitions, futures and quote/quantity/value specifications.
Every financial numeric API value is a string; browser numbers are used only for
chart coordinates. Calculation occurs in the exact managed engine. The server
exposes the existing supported rules; there is no browser financial engine.

The launcher creates a new private directory and Unix-socket-only PostgreSQL
cluster, verifies its exact data directory before loading anything and seeds only
original synthetic data. It accepts no existing database URL. The app uses separate
restricted authenticator/runtime logins and current database grants, rather than
the bootstrap owner. Runtime transactions issue/bind a capability and check the
operation under RLS. Test identities are chosen from a finite server-owned list.
The preview adds these identities only to its owned fixture; the migrations seed
no users or grants.

HTTP listens only on IPv4 loopback with exact Host/Origin checks, request deadlines,
anti-forgery protection and random short-lived HttpOnly SameSite cookies. The
preview is not a general multiuser login service: any local browser can select a
test persona. Input selections are session-local and corrections are shared inside
the disposable synthetic workspace. Sessions serialize operations; the UI locks
context controls while a request is pending and never retries mutations.

Ctrl+C stops only the owned child process and cluster and removes their directory.
Uncertain database startup/cleanup retains its private directory instead of deleting
potentially live files. Build output stays in ignored directories. `--no-build`
reuses existing managed/client output. No native builds, container images, existing
applications, production resources or external DNS are involved.

### Verification

```sh
# Includes build, 56 HTTP checks, ten adapter checks and owned-resource cleanup.
python3 tools/run_preview.py \
  --pg-bin /path/to/installed/postgresql/bin \
  --dotnet /path/to/installed/dotnet --check
```

Run `npm test --prefix web` for the 51 browser-independent presentation/preference
checks, including DST folds, exact date-choice identities, unit links, market-type
filters, hidden IDs, preference isolation/schema validation, storage failure,
reference/setup separation and coordinate labels.
These exercise the view model and preference storage, not a rendered browser.

The HTTP checks cover permissions, CSRF/Host/Origin boundaries, exact numeric
transport, ten commodity curves and one rate curve, dependency propagation versus independent all-in marks,
precision/grid rejection, old-input replay, correction conflicts, current roles,
session isolation and recovery without a mutation response. Rate checks cover
percentage display, exact rational discount factors, dated sample history, corrected
rate replay and independence from commodity prices. Adapter tests reject
mismatched bundle pins and run the real persisted fixture under non-Gregorian
server cultures. The separate core, rate and database suites contain 90, 29 and 173
checks respectively. Independent security and domain reviews cover the adapter,
async behavior, numerical/temporal boundaries and resource lifecycle. Nineteen worksheet checks in an isolated headless browser with a new temporary profile also
exercise single-contract diagrams, connected curve tables, common and exceptional
row mappings, shared-input tracing, single/all-contract toggles, month/date changes,
source revisions, independent all-in marks,
rate calculations, time-zone preferences, mobile scrolling and malformed-trace
error clearing. Desktop/mobile screenshots are inspected separately from the
functional checks. Another 27 rendered-browser checks cover layout/menu persistence,
profile isolation and reload, global/column/empty/clear filters across all six
regular table types, stale controls, context reset, pending-read disabling and
responsive navigation. The worksheet regression checks also pass with ordinary
table filters present. These remain preview QA, not production qualification.

### Per-contract pricing explanation

For curves with evaluated dependencies and for rates, **Price sources** and
**How it’s priced** open on one contract/maturity with linked selectors. A supplied
curve with no evaluated dependencies shows all contract prices and their source
observations immediately in a plain table, without single-price boxes or redundant
contract controls. **Show all contracts** displays a spreadsheet-style worksheet with
one table per curve, dated contract/price rows and connections between tables; the source table combines and deduplicates their observation
lineage. **Show selected contract** returns to the retained selection. This shared
presentation mode applies to rates too, and never changes the selected market
inputs or triggers a valuation request. A source revision outside the displayed
contracts is hidden when returning to a single-contract view. Commodity diagrams follow actual evaluator evidence: each node retains
its curve, dated contract, quote, exact value, observation lineage and variable or
frozen-final input use. Edges identify the actual base/spread evaluations; scaling
steps retain the exact multiplier and its observation, or an explicit constant.
Shared computations appear once per contract breakdown. Independent all-in prices
have a single supplied-price node and no inferred upstream relationship.

In the all-contract worksheet, a common connection replaces individual wires only
when its complete source/target row mapping is one-to-one, delivery starts/ends
match exactly, and the target rule, factor value/identity/specification, input
source/role and variable/frozen use agree. Every underlying row pair remains in
the view model. Differing or shared-source mappings get explicit row connections;
matching month labels or similar prices never establish a relationship.

Tables display exact strings in selectable cells, row numbers, column letters and
units. Scaling tables show their supplied factor. Non-monthly rows show exact
half-open delivery intervals; duplicate period labels receive deterministic
presentation ordinals rather than GUIDs. Selecting a row traces its ancestors and
its descendants separately, without expanding through a shared source to sibling
outputs and their unrelated inputs. The worksheet scrolls horizontally within its
panel; wider screens use the available width. Rates show separate annual-rate and
actual-day input tables connected to their exact discount-factor maturities.

The calculation table displays server-produced values and explicit equations; the
browser performs no financial arithmetic. The source table lists only observations
used by the selected contract, with supplied source names and units. Missing or
inconsistent links/lineage fail visibly and clear the diagram. Changing the market
date, curve or identity clears prior evidence before new work; corrections and
old-input replay retain the matching calculation trace. Rate views show annual
rate, actual days/365 and the original simple-discounting expression.

The diagram is a rendering of evaluated relationships, not a graph database or an
allocation of exposure. It scrolls inside its panel on narrow screens; the exact
calculation/source tables remain accessible alongside it. Structural view checks
cover dependency closure, shared nodes, frozen labels, stale root evidence,
missing/duplicate links and cycles. HTTP checks reconcile every returned trace
node using independent decimal arithmetic and verify all-in independence.

### Hosted preview gates

The intended hosted domain remains leanetrm.com. This host refuses to start unless
`--local-preview` is supplied and is not suitable for a public deployment. Before
hosting: integrate verified sign-in and session revocation, remove persona-based
capability issuance, commit durable operation audit, configure a private test
workspace/database, complete end-to-end browser checks and review the deployment,
TLS/proxy configuration, migrations, backups and rollback. Prepare a reviewable
isolated release before approval to expose it. Existing server applications and
DNS remain untouched. There is no deployment or production qualification in this
local preview.

## Mixed-period browser verification

The separate `--example mixed-gas` instance passes 24 HTTP checks over eight
saved market dates and seven commodity curves plus the basic interest curve.
The standard dataset still passes 56 HTTP checks and 10 adapter checks. The web
suite passes 51 checks, including exact daily/monthly/BALMO labels and explicit
history availability. Fifteen checks in a new isolated browser profile cover
dated history gaps, actual column-filter choices, single-contract source boxes,
connected all-contract tables, distinct BALMO inputs, month-end emptiness,
February rollover, hidden internal IDs and mobile page containment. These checks
use owned disposable resources; the user preview is a separate retained instance.
Desktop and mobile views are also inspected visually. No public deployment or
publication is implied.


## Financial swap booking and schedule inspection

The local application now books and amends monthly financial gas/power swaps
through the same authorized operations used by its editor. Each side owns delivery
and payment terms, generates its actual valued schedule and computes its own PV.
The instrument applies membership signs to completed side PVs; positive trade
quantity scales the sum once. Fixed contractual strikes do not inherit observed
market-price bounds or tick sizes. Preview validates current head, effective time,
reason and compatible exact quote/unit references before confirmation.

Booking exposes monthly-index and daily-calendar arithmetic averaging. The latter
requires complete, unambiguous, live dated daily contracts: a monthly mark cannot
fill a gap. Thirty-one January reset rows can feed one monthly payment. Reset cash
allocations remain rational and sum exactly to the side payment; the final trade
amount must fit the declared exact working format. Each side inspector retains
prices, weights, lookup/observation lineage, cash, payment time, discount mapping,
curve/pillars, DF and PV. User-local clocks affect display/entry; contractual
intervals and the explicit rate-date basis retain their meaning.

Currency defaults and supplemental immutable daily-market bundles respect the
common effective/knowledge context. A later backdated mapping does not alter a
certified earlier view. Migration 0052 rejects a second default for the same
definition revision, exact currency reference, instant and publication, and the
lookup raises `swap_discount_ambiguous` instead of choosing between tied latest
mappings (D166). The shared backend blotter displays projected swap PV and
futures execution P&L as separate nullable measures, with typed product partitions
in pivots. Saved account layouts retain their choices. New trade offers Futures
or Financial swap; both have backed booking and latest-head amendment operations.
The swap editor uses aligned vertical columns and initializes only unset peer
delivery/currency fields; subsequent edits remain independent.

Owned standard previews include sixteen explicit monthly/daily gas/power examples
across the same four weekdays, alongside the original eight futures. The examples
share Book B, while Book A stays futures-only. Portfolio risk now accepts either
book or their combined holdings under the common instrument valuation operation
(D181). Seeding uses
stable authorized commands and retries without duplicates. No live feed, actual
fixing ingestion, recurring payment strips, physical delivery, PPA or
fee/reconciliation workflow is enabled by this slice. Broader core averaging
strategies remain hidden until calendar/weight capture and persistence exist.


## Generated payment dates and commodity selection

New swap entry selects commodity from exact reference data and filters the
compatible floating indices, curves and working units. The editor labels its
selector Floating Index. Reset frequency and averaging method are distinct:
monthly index/no averaging or daily calendar/arithmetic mean are backed choices.
Payment frequency currently supports monthly payments over one delivery month.

Each side owns an explicit payment rule: days after its last delivery day, or a
specified day of the following month. Calendar/business-day lag, short-month
reject/clamp and unadjusted/following/preceding/modified-following adjustments are
implemented with covered exact reference calendars. Lag precedes adjustment.
Missing/inapplicable parameters, inconsistent dates, unknown conventions and
unsupported coverage fail explicitly. Managed generation reuses the shared date
rolling kernel; database admission independently validates the generated result.

Actual valued plans retain their rule/calendar evidence and generated
unadjusted/final dates. Payment dates display as dates and never shift with user
time zone. The existing selected UTC rate-date convention still determines DF
lookup and due-date eligibility. Legacy manual-instant revisions retain their
original contents; no rule is inferred. Latest-head amendments choose explicit
rules and append new immutable definitions/events. New entry has no editable
payment date/time field. New rule identities remain distinct even if their
currently generated dates happen to coincide.

The swap editor's schedule-date helper calls the common published resolver for
delivery start/end entry. Its symbol catalog and date resolution share the exact
editor context; a later publication does not change either. Reviewed results show
the actual date, source version and occurrence. Apply writes a literal date to
the selected side field and invalidates valuation; the trade has no moving symbol
binding. Read-only revisions cannot apply results, and form/context changes,
denial and close discard pending or resolved helper state.

Payment-rule option lists are returned by the backend from the same typed
conventions used to parse inputs. User-maintained commodities, indices, calendars,
units and currencies still come from selected reference rows. The standard local
sample dataset links quotes, rates and trades to `usd` / `USD` / `US Dollar` in
`ref_data.currencies`; the UI does not substitute a currency label.

Shared grids suppress row activation after pointer dragging, text selection or
scrolling. Deliberate pointer and keyboard activation preserve source-row identity
through virtual scrolling. A column width a person sets survives later redraws at
another grid width, such as when an opened menu adds a page scrollbar, and a saved
view stores it. Schedule tables have a single bounded scrolling area; shared table
text uses consistent spacing and tabular numeric alignment without changing exact
values or exports.

The separately selectable [exchange futures example](exchange-futures.md) uses
public product facts and synthetic observations. V2 lot normalization supports
physical/cash terms, fixed totals, explicit calendar-day rates and published
power-hour totals. Basis quotes retain exact regional-index and benchmark-series
settlement references while remaining independent factors for current mark risk.
Deal display precision is shared current presentation outside financial history;
Profile has no amount-precision controls. Daily/final settlement and variation
margin remain unimplemented.


Temporary futures and swap editors expose Price and Risk before booking. Trade
parties and book membership are required only for booking. The backend converts
explicit typed quantities against the selected pricing reference revision; it
rejects unsupported or inexact decimal conversions rather than choosing a rounding
policy. Price returns a pinned historical context reused by Risk. Draft risk
requires `RunDealRisk`, distinct from book-wide scenario authority, and reprices
the same instrument under immutable shared one-trading-tick quote scenarios.
Only declared live quote lineage is shocked; all-in marks acquire no inferred
upstream exposure. Interest rates remain fixed. Results retain exact rational
base/up/down cash, delta and gamma, including nonterminating daily-average
sensitivities. The UI formats those results through backend amount formatting,
clears them when terms/context change, and never books a trade as a side effect.
Swap drafts report PV; futures drafts report execution P&L and open exposure.
Variation-margin processing and full futures settlement PV remain unimplemented.

## Shared quote-input scenarios and open futures sensitivity

The managed risk path now uses a `ValuationPortfolio` projection of trade-owned
instrument quantities. Common `Instrument.Value` delegates to instrument PV;
futures explicitly select execution P&L. The book owns holding arithmetic and
exact net quantity/cost compilation for book-only reports. Repeated instruments
share one unit value per affected scenario, including across books. The risk
engine compares returned values without inspecting pricing methods.

`Instrument.MarketFactors(context)` returns immutable exact quote-factor metadata.
Books ask each unique instrument once, validate its complete pinned factors and
union those references. Curve consumers use a common context resolver for explicit
transitive recipes; no upstream factor is inferred for an independently marked
all-in curve. API discovery respects selected books/portfolios. Bumps stay explicit;
manual available-factor scenarios remain possible. Book summaries support up to
4096 factors; holding-detail requests retain the 16-factor response bound.

Synthetic gas and ERCOT curve checks exercise real unit swap PV through four and
two basis edges, respectively, across multiple maturities. The independent oracle
uses explicit signed quote incidence, including a Waha/HSC hedge and an independent
Waha all-in control. Unit checks cover MWh/kWh price/quantity invariance, derivative
dimensions and wrong-basis rejection; existing power checks cover 23/24/25-hour
energy, TOU and exact fractional interval allocation. These checks do not qualify
exchange data or establish physical-energy equivalence between gas and power.

The [benchmark](risk-benchmark.md) reports 50,000-position calculation work and
memory. It excludes production reads/transport and does not lift booking database
bounds or implement generic booked-swap/rate/volatility risk. Full futures PV and
variation margin remain unsupported. Core policy and futures convention have new
v2 identifiers; older calculation artifacts retain their original meaning.

## Generated pilot closes and mixed-book reports

The explicitly configured generated-close operation uses the existing job
directory, schedules, leases and replacement fences (D169). It retains an exact
anchor, selected books and root curves, synthetic source, seed, native clearing
tick bounds and versioned calculation policies. The worker preflights whole
selected books before writing marks. Its dedicated service runtime cannot import
observations outside the validated publication operation.

The retained portfolio adapter loads futures execution-P&L holdings and swap-PV
holdings, preserves their separate measures and shares immutable quote scenarios.
External quantities scale shared unit valuations. Exact rational summaries retain
base/up/down cash and derivatives without implicit rounding; rate inputs stay
fixed during commodity quote shocks. The earlier decimal output policy is
unchanged. Supplemental swap model bundles are rejected by this operation.

Generated marks and immutable reports have separate commits. A replacement reuses
published inputs and reports; a close without its report remains an incomplete
occurrence. Saved report relationships use exact versioned foreign keys for trades,
bindings, market factors, units and discount inputs. Reading a report requires
current authority and respects the explicitly selected historical view. Stored
closes are discoverable in later sessions with their original variable and frozen
pins. These capabilities do not implement settlement, variation margin, complete
daily economic P&L or historical VaR, and the managed benchmark does not qualify
their database or end-to-end throughput. Hosted activation and installation data
remain separate release operations.

## Audited interval series

Phase A of the [PPA design](ppa-design.md) (§5, §10; proposal P40) is implemented as
an audited store of values per UTC interval (D173). Shaped interval curves
([phase B](#interval-prices-curves-and-block-shaping)) read additive and
multiplicative shape series; PPA valuation (phase C) is not built, so no quantity or
settlement is derived from a stored value.

**Definition.** A series pins a kind, one exact specification in a sealed reference
revision, a clock and a resolution of 5, 15, 30 or 60 minutes
([migration 0067](../db/migrations/0067_interval_series.sql); the contract is
described in [db/README.md](../db/README.md#audited-interval-series)). The kinds are
`additive_price_shape` (a quote specification), `multiplicative_price_shape` (a
dimensionless value specification), `capacity_factor_forecast` (a dimensionless
value specification: output MW per MW of capacity) and `metered_energy` (a quantity
specification of the energy dimension). Codes are unique within the catalog. A
series has no owner binding: the curve for shapes and the asset for volumes arrive
with their consumers in phases B and C.

**Revisions.** Each revision is an immutable delta that stores only the intervals it
sets or withdraws, where a `null` value withdraws. It records its actor, a reason of
1 to 500 characters (padding counts, not blank), its request identity and a
knowledge time certified at commit on the shared knowledge gate. A read at an
explicit cutoff takes each interval from the latest revision known then; a
withdrawn interval has no row. A write names the revision it builds on and fails
with `interval_series_revision_conflict` if another was saved first. Retrying a
request identity returns its original receipt; the same identity with a different
command fails with `interval_series_conflict`. A revision holds at most 200,000
intervals; larger imports are several revisions.

**Grid and values.** `IntervalSeriesGrid.Expand`
(`src/OpenEtrm.MarketData/Power/IntervalSeries.cs`) expands each imported segment
onto the cell grid of the series' pinned clock, using the `PowerHours.IsCell` rule
that `PowerHours.Compile` also uses. A start that is not exactly one clock cell, a
start outside 2000-01-01Z to 2100-01-01Z, or an overlap is a structured error;
nothing is aligned or shifted. Repeated DST hours are separate intervals. Storage
rechecks that every interval has the series' duration and refuses any interval that
overlaps a stored one at a different start, withdrawn or not, so a stored interval
changes only at its exact start. Values are canonical exact decimals of at most 18
fractional digits, within the series' value specification bounds where it has one.
The checks of the request alone (lengths, durations, order, decimals and bounds) run
before the shared knowledge gate lock; the replay, head and stored-overlap checks and
the writes run under it. The stored-overlap check probes two index ranges per new
interval, one on each side of its start, so it never visits the revisions stored at
that start.

**Reads.** A values read returns up to 35,136 intervals (366 days of quarter-hour
intervals) and the change log up to 100,000 rows, at a cutoff no later than now. A
revision whose knowledge time equals the cutoff is included. Storage bounds the
window's duration to 35,136 intervals and returns the whole intervals inside it. It
refuses with `interval_series_grid` a From or To strictly inside a stored interval of
any revision, a withdrawn one or one saved after the cutoff included, so no interval
is silently dropped at either end; a From or To in a gap with no stored interval is
accepted. Reads never load the pinned clock, so they stay available after a tzdata
update, and a whole local day across a half-hour clock change is a valid window.
The change log gives each change's previous value, new value, actor, reason and
knowledge time, and a summary per revision. The values read visits every stored
revision of each interval in its window, so its cost grows with the number of
revisions per interval (measured below).

**HTTP and permissions.** `src/OpenEtrm.Api/IntervalSeriesStore.cs` and `Program.cs`
serve `GET /api/interval-series` (the catalog, choices and a fresh `knownAt` from
the database's clock, the clock reads compare their cutoff with),
`POST /api/interval-series/define`, `revise`, `values` and `changes`. Reads need
`ReadMarketData`, definitions `WriteMarketDefinitions` and revisions
`ImportObservations`, and each also needs read access to the reference catalog the
series pins; a read rechecks that access after certifying its cutoff. Every route
refuses a historical session with `interval_series_historical`, and a restrictive
row-level policy hides all five tables from a runtime session in a historical view:
operations work on current data, and history is read by sending an explicit
`knownAt` rather than by switching the session. Each read's retained temporal context
belongs to its cutoff alone, dated by the cutoff's UTC day, since series are not
effective-dated. Instants are UTC with `Z` or `+00:00` and 0 or 1 to 6 fractional
digits; reads render `+00:00`. Every request member is required: a missing, unknown or
unreadable member is refused `400` with `request_format`, and nothing is defaulted.
Only `revise` accepts bodies up to 8 MiB; every other route keeps 32 KiB. A body over
its route's limit, with or without a declared length, is refused `413` with
`request_too_large`, whose message names that limit. A save whose outcome is unknown answers `503` with
`interval_series_outcome_unknown`; repeating the same request identity recovers the
receipt.

**Screen.** Market data › Interval series (`web/src/interval-series-view.ts`)
defines series and shows values and the change log at a chosen Known at. An import
reads a CSV file with the header `start,value`, UTC starts ending in `Z` and a value
on every row; an import never withdraws an interval. An editor chooses one interval
by clicking its row of the values, or by moving to it with the Up and Down keys and
pressing Enter or Space (the rows share one tab stop). An action bar shows its start
and value and offers Correct and Withdraw, each requiring a reason. Choosing another
interval closes a change begun on the last; every read of the values (Show, Reload
or after a save) and a change of series or time zone clears the choice. The values
have no row buttons, and the per-interval columns of the values and change-log
tables (Start, End, the values and Known from) have no column filter, which would
list one choice per interval; the search box still covers them. Any structured
refusal from these routes releases the form, since nothing was saved; only an
unknown outcome (`interval_series_outcome_unknown`, a reply lost or timed out in
transport, or an answer without a code) keeps the request outstanding for an exact
retry. Presentation only: grid, values and authority are checked by the backend. In
Chrome 156 on the benchmark machine below, in three runs of the browser check, the
largest window, 366 days of quarter-hour values with its 35,136-row change log, took
2.73 to 2.74 s from Show until both tables were painted, about 0.37 s of it before
the change log arrived. With it shown, choosing its last interval and starting a
Correct took 0.20 s, and opening Profile, which disables and enables every control
on the page, 0.65 to 0.67 s. The renderer's JavaScript heap held 43 to 45 MiB and
the page about 923,000 DOM nodes.

**Verification.**

```sh
dotnet run --project tests/OpenEtrm.MarketData.Checks -- --interval-series
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin --dotnet /path/to/dotnet \
  --interval-series-check --browser-check --browser /path/to/browser
```

The managed checks cover the grid on synthetic clocks, including repeated and skipped
hours, half-hour offsets and a half-hour clock change.
`tools/check_interval_series_storage.py` runs 20 storage checks,
`tools/check_interval_series_http.py` 12 HTTP checks and
`tests/browser/interval-series.mjs` 38 browser checks. `tools/check_all.py` runs all
of them: the managed checks with the other managed suites, and the preview command
above, browser checks included.

### Twenty-year qualification

```sh
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin --dotnet /path/to/dotnet \
  --interval-series-benchmark
```

`tools/benchmark_interval_series.py` runs in the launcher's newly created database.
The launcher prints its JSON report among its own log lines, as the block that starts
with a line holding only `{` and ends with one holding only `}`, and exits nonzero if
a step fails. An editor session drives the same authorized routes as the screen. Its
import bodies (about 1.2 MB each) exceed the 32 KiB limit of the other routes and stay
under the 8 MiB limit of `revise`. Each workload defines one series
(`capacity_factor_forecast`, value specification `test_zero_rate`, clock
`synthetic_eastern`), so there is one unique contract per workload and no
scenarios: no valuation or shock is evaluated. The evaluated work is the intervals
stored, read and logged below. The workloads `hourly_20y` and `quarter_hour_20y`
each:

1. import 2027-01-01T05:00Z to 2047-01-01T05:00Z with values cycling `0.1` to
   `0.9`: `hourly_20y` as one revision of 175,320 intervals, `quarter_hour_20y` as
   four five-year revisions of 175,296, 175,392, 175,296 and 175,296 intervals
   (701,280 in all);
2. read one 366-day window from 2036-07-01T05:00Z at a cutoff after the import
   (8,784 and 35,136 intervals, the second the largest window allowed);
3. append a correction of 10,000 intervals from the start of that window;
4. re-read the window at the earlier cutoff and require the same rows;
5. read the change log for 31 days from the same start.

The workload `hourly_rewrites` imports the same hourly span ten times, each a full
rewrite of 175,320 intervals that shifts every value along the cycle, so each interval
ends with ten stored revisions (1,753,200 stored intervals); it then reads the same
366-day window, every row from the tenth rewrite. The workloads run in this order in
one API process.

The benchmark stops with the failing workload and step on any refusal, timeout or
wrong count. It requires the declared interval counts to match the date spans, each
receipt to match its revision, every read to return exactly the window's interval
count with the expected values and revisions, and the change log to hold each
interval's import and correction rows. `requestBytes` and `reviseSeconds` list the
import requests, one per revision, as the client sent and timed them.
`maxReviseSeconds` is the slowest revision request of the workload, the correction
included, to be read against `requestTimeoutSeconds`: this client and the browser
transport (`web/src/app.ts`) both abandon a request after 20 s, so a slower request
fails the run instead of being reported. `revisionsPerInterval` is the number of
stored revisions of every interval the rewrite workload's read visits. Read times
include the client's decoding of the response. `tableBytes` and `indexBytes` are
the growth, over the whole workload, of the five interval series tables.
`apiResidentBytesAfter` is the resident memory of the launcher-owned API process
sampled with `ps` after the workload, not a peak; since the workloads share one
process, each figure follows the workloads before it. `cpu` is the architecture and
logical processor count.

Report (the API, migration, screen and benchmark are those of revision `9280f2a`; the
commit that records this report changes only documentation):

```json
{
  "environment": {
    "postgres": "16.14",
    "dotnet": "10.0.402",
    "cpu": "arm64, 14 logical processors",
    "memoryBytes": 51539607552,
    "os": "macOS-26.6.2-arm64-arm-64bit-Mach-O",
    "topology": "One machine: client, API (the launcher's Debug build) and PostgreSQL; loopback HTTP and a Unix-socket database."
  },
  "workloads": [
    {
      "name": "hourly_20y",
      "clock": "synthetic_eastern",
      "intervalMinutes": 60,
      "intervals": 175320,
      "revisionIntervals": [175320],
      "requestBytes": [1227470],
      "reviseSeconds": [1.2],
      "read366DaysIntervals": 8784,
      "read366DaysSeconds": 0.044,
      "correction10000Seconds": 0.073,
      "changes31DaysRows": 1488,
      "changes31DaysSeconds": 0.022,
      "maxReviseSeconds": 1.2,
      "requestTimeoutSeconds": 20,
      "tableBytes": 17309696,
      "indexBytes": 26107904,
      "apiResidentBytesAfter": 299761664
    },
    {
      "name": "quarter_hour_20y",
      "clock": "synthetic_eastern",
      "intervalMinutes": 15,
      "intervals": 701280,
      "revisionIntervals": [175296, 175392, 175296, 175296],
      "requestBytes": [1227308, 1227980, 1227308, 1227308],
      "reviseSeconds": [1.267, 1.337, 1.122, 1.145],
      "read366DaysIntervals": 35136,
      "read366DaysSeconds": 0.103,
      "correction10000Seconds": 0.066,
      "changes31DaysRows": 5952,
      "changes31DaysSeconds": 0.049,
      "maxReviseSeconds": 1.337,
      "requestTimeoutSeconds": 20,
      "tableBytes": 66232320,
      "indexBytes": 97943552,
      "apiResidentBytesAfter": 584269824
    },
    {
      "name": "hourly_rewrites",
      "clock": "synthetic_eastern",
      "intervalMinutes": 60,
      "intervals": 1753200,
      "revisionIntervals": [175320, 175320, 175320, 175320, 175320, 175320, 175320, 175320, 175320, 175320],
      "requestBytes": [1227475, 1227475, 1227475, 1227475, 1227475, 1227475, 1227475, 1227475, 1227475, 1227475],
      "reviseSeconds": [1.294, 1.366, 1.301, 1.501, 1.352, 1.347, 1.362, 1.612, 1.588, 1.503],
      "revisionsPerInterval": 10,
      "read366DaysIntervals": 8784,
      "read366DaysSeconds": 0.052,
      "maxReviseSeconds": 1.612,
      "requestTimeoutSeconds": 20,
      "tableBytes": 163250176,
      "indexBytes": 388669440,
      "apiResidentBytesAfter": 970932224
    }
  ]
}
```

(The printed report lists the arrays one element per line; the values are unchanged.)

Three runs of revision `9280f2a` on the same machine, the one above included, gave
import revisions of 1.20 to 1.23 s (hourly) and 1.12 to 1.55 s (quarter-hour, 4.9 to
5.6 s for all four), reads of 0.044 to 0.045 s and 0.103 to 0.108 s, corrections of
0.066 to 0.076 s and change logs of 0.022 to 0.025 s and 0.049 to 0.051 s. The ten
rewrites took 1.29 to 1.69 s each, the first 1.29 to 1.31 s and the tenth 1.50 to
1.67 s, and the 366-day read after them 0.052 to 0.056 s, against 0.044 to 0.045 s
for the same window with one revision per interval. The slowest revision request in
any run took 1.69 s, against the 20 s timeout. Table growth was identical in every
run. Quarter-hour index growth was 97,943,552 bytes in one run and 175,013,888 bytes
in two; rewrite index growth was 388,669,440 bytes in one and 384,425,984 bytes in
two. Series identifiers are random. In four runs of an earlier version that recorded
them, the larger quarter-hour growth occurred exactly when the quarter-hour series'
identifier sorted before the hourly series', so that its keys entered the middle of
the indexes instead of appending after the last key. The API process held 287 to
300 MB after the hourly workload, 584 to 643 MB after the quarter-hour workload, which
follows it in the same process, and 963 to 983 MB after the rewrites.

What this qualifies: one editor session importing and reading a 20-year hourly and
quarter-hour series through the authorized HTTP path, and rewriting the hourly series
in full ten times, on one machine with a Debug build of the API and loopback HTTP.
Stored revision depth is one revision per interval in `hourly_20y` and
`quarter_hour_20y`, two for their 10,000 corrected intervals, and ten in
`hourly_rewrites`; deeper revision histories are not measured. The benchmark generates no other load, but the host was
not otherwise isolated. It does not qualify concurrent clients, a hosted network, the
API's Release build, the memory of the database process or the API's peak memory.
The browser's cost to render the largest window is measured by the browser check
(Screen, above), not by this benchmark. The interval series tables were not
explicitly analyzed before the reads, so the reads used whatever planner statistics
existed at the time.

### Not done and limits

- No consumer: interval curves, PPA valuation and settlement (phases B to D) do not
  read series, and no pricer declares a dependency on one.
- No owner bindings. A series is not tied to a curve or an asset, so access follows
  the market catalog and the pinned reference version only.
- No historical session mode. Series pages and routes refuse a session in a
  historical view, and storage shows such a session no series rows; history is
  available only through an explicit `knownAt`.
- No agent tool entry point yet. The operations exist only as the authorized HTTP
  operations that the screen also uses.
- A write checks stored overlaps and inserts while holding the transaction lock of
  the shared knowledge gate, which every temporal read and certified write also
  takes, so those operations wait for it; only the checks of the request alone run
  before it. Revisions of 175,296 to 175,392 intervals, including full rewrites over
  up to nine stored revisions per interval, took 1.12 to 1.69 s end to end in the
  measurements above (one session, Debug build, loopback), which bounds that wait for
  revisions of that size; a 200,000-interval revision was not measured. The
  200,000-interval cap exists because an earlier trial of a single 701,280-interval
  append exceeded the 5 s command timeout while holding that lock.
- A revision of 200,000 long decimals can exceed the 8 MiB body limit and is then
  refused `413` with `request_too_large`: that happens above roughly 38 characters
  per value, while the benchmark's values have 3.
- A revision expands onto the pinned clock, and an IANA-zone clock is used only
  while the installed tzdb release equals the one its clock rule declares (D127).
  After a tzdata update, every revision of an existing series on an IANA-zone clock
  is refused with `clock_tzdb`, and the series cannot be re-pinned to a successor
  reference revision: open decision O27. Reads never load the clock and stay
  available; the HTTP check reads values and the change log of a series whose clock
  declares another release, and only its revision is refused. Fixed-offset clocks are
  unaffected.
- Each values or change-log read at a new cutoff stores one retained temporal
  context, and a catalog holds at most 4,096 of them, never reclaimed
  (`knowledge_limit`). This existing platform limit is open decision O07; every
  such interval series read counts against it, and the benchmark stores five.
- Reads are bounded and not paged: windows over 35,136 intervals or change logs over
  100,000 rows are refused, not truncated. A values read's cost grows with the
  revisions stored per interval, which nothing bounds.
- The screen renders a whole window at once and is not paged. While the largest
  window was shown in the three runs above, the browser's renderer process peaked at
  2,042 to 2,050 MiB resident (sampled with `ps` during the check, not by it), against
  a JavaScript heap of about 45 MiB.

## Interval prices, curves and block shaping

Phase B of the [PPA design](ppa-design.md) (§6, §9 oracles 1–4, §10; proposal P40) is
implemented as reusable interval-price behaviour in `src/OpenEtrm.MarketData/Power`,
authorized reads and a Market data screen (D174). Nothing books, values or settles a
trade from it: sides, booking, valuation and settlement are phases C and D. The
storage contract is in [db/README.md](../db/README.md#interval-curves)
([migration 0070](../db/migrations/0070_interval_curves.sql)).

**Interval contracts and marked curves.** `market_data.contract_intervals` gives a
forward its exact UTC interval and the labels its clock gives it: operating date,
hour beginning, minute and occurrence (2 only for the second pass through a repeated
local hour). An interval contract delivers from its operating date to the next day
and expires at its interval end, and a curve's constituents are all interval or all
date-only. `IntervalContracts.Validate` requires every constituent to be one distinct
cell of `PowerHours.Compile`'s grid for its curve's delivery profile, labelled as
that compiler labels it. A directly marked interval price is an ordinary observation
of the contract's series, built by `CurveModel` like any mark; because the contract
expires at its interval end, the mark of an ended interval is never built.

**Blocks and shaping.** `curve_blocks` makes a direct, date-only curve a block curve:
all hours, or the peak or off-peak hours of one published TOU product version
(`IntervalBlocks`; holidays are off-peak). A shaped curve names one all-hours block
or the peak and off-peak blocks of one TOU version, a method (`additive` or
`multiplicative`) and one phase A shape series. Its blocks share one `DeliveryGrid`:
the clock, interval length and holiday calendar of their delivery profile.
`BlockShaping.Prepare` selects every block constituent the window touches, compiles
its whole delivery period, requires the blocks to cover every interval in the window
exactly once (`interval_block_partition`) and every block interval to have a shape
value with its exact boundaries (`interval_shape_coverage`; a withdrawn shape
interval is a gap). `BlockShaping.Evaluate` then calibrates each block exactly under
`duration_weighted_block_mean_v1` and `block_shape_v1`: additive
`Pᵢ = M + oᵢ − Σ(dⱼ·oⱼ)/Σdⱼ`, multiplicative `Pᵢ = M·fᵢ·Σdⱼ/Σ(dⱼ·fⱼ)`, so each block's
duration-weighted mean equals its mark, and a non-positive weighted factor mean is
`interval_shape`. Prepared inputs are evaluated per market scenario: with a base
build of the same definition, a block whose mark, mark lineage and shape values and
revisions are unchanged is reused, so a block-mark shock recalibrates only its block.
Each price keeps its block mark's observations and the revision that last set its
shape interval at the cutoff. Prices stay exact fractions; the API adds a six-place,
ties-to-even display string (`half_even_6dp_display_v1`).

**Actual and projected resolution.** `curve_actual_rules` gives a curve that has a
collected-price source ([0063](../db/migrations/0063_power_curve_settlement_sources.sql))
the instant each interval's actual is due: its start or end plus an offset of up to
seven days (`interval_actual_due_v1`). `IntervalPricing.Resolve` is bitemporal: an
interval that has ended at the valuation instant takes an actual known by the
cutoff, while one still live then takes only an actual already known at the
valuation instant (a day-ahead fixing), so no later publication prices it in
hindsight. Otherwise an interval whose actual is due is refused
(`interval_actual_missing`), an ended one is `Unresolved` with no price, and a live
one takes its pinned mark. Actuals come from the existing collected-price reader at
the cutoff and must be in the curve's exact price unit; one known after the cutoff
is refused. `IntervalPricing.Aggregate` returns duration-weighted means
(`duration_weighted_price_mean_v1`) per whole local hour, the repeated hour twice, or
per whole operating day; a window that cuts one is refused (`interval_aggregate`).
Each keeps its native intervals and has no mean while one is unresolved. Cash with varying quantities stays Σ(MWh × price) in
`PowerSettlement`.

**Reads.** `POST /api/interval-curves/catalog` lists the marked and shaped curves of
the selected frame, with the database clock as `knownAt`; `POST
/api/interval-curves/values` returns the points of a window of at most 35,136
intervals whose ends are grid boundaries of the curve's clock and whose every
interval is one of the curve's (`interval_curve_coverage`). Marks come from the
frame's pins. Like a close, a frame selects only the interval contracts live at its
valuation instant; an ended interval comes from the curve definition and has no mark,
so a plain marked read refuses a window that includes one (`interval_delivered`) and
`resolve` prices it from its actual. A shaped curve's shape and a resolved curve's actuals are read as known
at an explicit `knownAt`: a frame's cutoff is its valuation instant, earlier than
any shape revision or collection certified since, so the knowledge time is a request
input that a plain marked read refuses (`interval_curve_cutoff`). `resolve` uses the
frame's valuation instant, and `aggregate` (`hour` or `day`) requires it. Both routes
need ReadMarketData, authenticate before reading the snapshot and refuse a historical
session (`interval_curve_historical`), because TOU products and shape series are
current definitions. Every request member is required, `null` where it does not
apply, and unknown members are refused (`request_format`), as for interval series.
The catalog lists a curve whose definition cannot be resolved, such as a block
period its holiday calendar does not cover, as `unavailable` with its reason; it
neither drops it nor fails the other curves. A shaped or resolved read also returns
the knowledge contexts its shape or actual reads used. Points carry their labelled `interval`, an exact `price` with its
display string, and their lineage: observations, block and shape revision, or state,
due time and the actual's provider revision and knowledge time.

**Screen.** Market data › Interval curves reads the market date chosen in the header.
It offers a curve, a From and To in the profile's time zone, Known at (pre-filled with
the catalog's database time and needed only for a shaped or resolved read), Resolve
for curves with a due rule, and an hour or day aggregate. A chart (the shared chart,
without point symbols and sampled for display) and a table show each native
interval, the repeated hour with its UTC offset. Choosing a row or an aggregate states
where its price came from, without internal identifiers. An unavailable curve is
listed with its reason and cannot be read.

### Not done and limits

- Shaped curves do not resolve against actuals: a shaped curve has no curve row to
  bind a collected-price source to. A uniform interval-price consumer for sides, an
  exact price × quantity step for schedule rows and a per-scenario price cache shared
  by sides belong to phase C.
- Scenario reuse has no production risk consumer yet; managed checks exercise it.
- There is no historical session mode. Knowledge times are explicit per request,
  not pinned in a frame.
- A live interval priced by an actual known at the valuation instant (a day-ahead
  fixing) is evidenced by managed checks only: the collection worker certifies
  actuals at the database clock, after every synthetic frame.
- A block constituent must stay live through its delivery period: once a block
  contract expires its mark is no longer built, so a delivering (prompt) month cannot
  be shaped from its block. Shaping against a settled block mark belongs with the
  phase C consumers.
- Two seal rules are not exercised against storage: blocks of one shaped curve that
  differ in quote, commodity, location or profile, and a due rule whose source differs
  in clock or interval length. The synthetic reference has one location, and no
  supported source can be bound to the eastern hourly curve with another grid.
- Block holidays come from the delivery profile's calendar, which must cover each
  block period (`power_calendar`).
- The screen was measured on the synthetic shaped curve's widest span, 1,465 hourly
  intervals, not a multi-year window.



## Nested stress and background risk

The portfolio risk operation accepts up to sixteen signed absolute quote stresses
in parent order. Immutable managed scenarios retain the original snapshot and
derive a distinct identity for each child; removing a child leaves its parent
usable. Up/down risk is measured around that stressed base. Work receipts include
market preparation, shared unit evaluations and book quantity applications.

The header's resolved effective/knowledge context and exact market frame feed
both immediate calculations and queued jobs. Migrations 0073 and 0074 retain authorized
request identities, exact context/book/observation/curve-membership references,
financial choices and the engine fingerprint. The UI supports queueing, truthful
cancellation status, uncertain-submit reconciliation, recent owned jobs and
retained downloads. Inspecting an older job displays its captured dates.

Dedicated read-only workers claim one complete run at a time. They renew leases,
load retained inputs under fresh scoped authority, calculate outside database
transactions and publish behind current authorization and attempt-token checks.
Multiple processes distribute independent jobs; book aggregation stays within
each shared calculation. The optional Swarm worker definition is configuration,
not evidence of a deployed or capacity-qualified cluster. Single-run sharding,
cross-run caching and process-death confirmation for an unacknowledged
cancellation remain unsupported. See [risk engine](risk-engine.md) for contracts,
worker provisioning, limits and the isolated verification entry point.


The `/api/portfolio/risk` operation and its catalog/jobs routes use the common
portfolio materialization and calculation path also consumed by D169 generated
closes. The current eight futures and sixteen swaps can be selected together.
Exact market partitions preserve supplemental definition revisions, observation
uses and curve memberships through migration 0074's foreign keys; current
reference visibility and actor/service authority apply throughout. The earlier
futures routes retain their original numerical and valuation contracts. Recent
portfolio job history selects the portfolio operation convention.

This enforces the existing D95/D145/D148 boundary (D181): instruments implement
`PV()` and a required complete `Declare()`, while the generic risk engine compares
returned values under shared scenarios. `Side.GenerateSchedule()` and `Side.PV()`
are implemented abstract contracts; universal instrument schedule generation
remains proposed. Futures retain their explicit execution-P&L measure. A PV-only
model needs no artificial discount provider; actual side discount inputs remain
mandatory when the side consumes them.

The explicit rational policy supports holding detail as well as book summaries,
including nonterminating PV and sensitivities. The backend returns exact decimal
or fraction strings alongside display values rounded to twelve decimal places,
ties to even. Futures execution P&L and swap PV stay separate measures, and rates
remain fixed under commodity shocks. Unaffected market partitions retain every
requested holding with zero sensitivity. The common engine's existing decimal
policy continues to reject unrepresentable output instead of changing its meaning.
