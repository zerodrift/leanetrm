# Local verification and continuous integration

Run verification from a clean checkout using public dependencies and the supplied
synthetic fixtures. No existing database or application is a verification target.

## Required tools

- The exact .NET SDK in `global.json`.
- The exact Node version in `.node-version`, including its bundled npm.
- Python 3 with assertions enabled; the hosted job uses Ubuntu's Python 3.12.
- Installed stock PostgreSQL 16+ binaries, including `initdb`, `pg_ctl` and `psql`.
- An installed Chromium-compatible browser. Keep its normal sandbox enabled.

Run as an ordinary user, not root. PostgreSQL's shared system service is not used.
The existing preview launcher allocates private Unix-socket-only clusters and
checks their data directory before applying migrations or loading fixtures.

```sh
python3 tools/check_all.py \
  --pg-bin /path/to/postgresql/bin \
  --dotnet /path/to/dotnet \
  --browser /path/to/browser
```

The command checks its orchestration, standalone dependency boundaries and owned
process cleanup; installs the locked frontend dependencies without install scripts;
builds and checks the frontend; restores every managed check project in locked mode;
and runs the managed, rate, temporal and schedule checks. It then runs the stock
database schema suite, standard HTTP/storage/adapter checks, owned browser scenarios
and mixed-period HTTP checks. A failing child command stops the run with a nonzero
status. Python optimization is rejected because it disables assertions used by the
existing verification scripts.

The standard and mixed previews use different disposable clusters. The browser
runner uses fresh temporary profiles and accepts only the launcher's verified
owned target. Normal completion removes the owned application, browser and database
resources. Unconfirmed teardown retains the private directory and fails the check.

## Hosted job

[The workflow](../.github/workflows/ci.yml) uses `ubuntu-24.04` hosted runners on
pushes, pull requests and manual dispatch. Checkout and tool-setup actions are
pinned to exact commits. The workflow has read-only repository access, does not
persist checkout credentials and requires no repository secrets. It runs the same
`check_all.py` command as local verification. It does not publish packages or
deploy the application.

The job uses the runner image's stock PostgreSQL 16 binaries and installed Chrome;
it does not start the image's shared PostgreSQL service. SDK, Node and application
dependency versions are pinned. The hosted image, PostgreSQL patch, browser,
Python, ICU and time-zone data can change. Their versions are recorded in the job
log, so this is a reproducible command and input set rather than a frozen OS image.

Hosted qualification passed on 2026-10-17 in
[run 39064855342](https://github.com/zerodrift/leanetrm/actions/runs/39064855342)
at commit `e7f9b4cafcd4855336cfbf8501d1122d5745339e`. The complete command passed
on Ubuntu 24.04 image `20261012.334.1`, .NET 10.0.402, Node 24.22.0, stock
PostgreSQL 16.15 and Chrome 155.0.8059.39 with its normal sandbox enabled. This
included 198 standard and 24 mixed HTTP checks, 50 browser checks and the existing
managed/frontend/schema/storage/adapter suites. Both disposable previews completed
teardown. This qualifies the verification command on that runner, not production
readiness.

Browser profiles remain under the preview owner's temporary root until its owned
process group has stopped. A regression checks that a live descendant can still
finish writing after the browser parent exits; parent exit alone does not permit
profile deletion.

Local qualification on 2026-10-17 passed the complete command from a fresh macOS
arm64 checkout with empty npm/NuGet caches, .NET 10.0.402, Node 24.22.0 and stock
PostgreSQL 16.14. This included 198 standard and 24 mixed HTTP checks, 49 browser
checks and the existing managed/schema suites. Workflow syntax and three runner
behavior checks also passed. The subsequent profile-lifetime correction passed
the standard preview and all 50 browser checks locally before the hosted rerun.

The subsequent typed-curve arithmetic change passed the complete local command
on the same macOS toolchain, including 117 managed core checks (15 typed-price
checks), 198 standard and 24 mixed HTTP checks and 50 browser checks. Both owned
previews completed teardown. The hosted result above identifies its earlier
commit and does not qualify these later changes on Ubuntu.

The subsequent bounded synthetic futures change passed the complete local command
on that macOS toolchain, including 131 managed core, 222 standard and 24 mixed HTTP,
36 futures-storage and 62 browser checks. Final shared-definition identity and
curve-selection refinements then passed 133 managed core checks (16 futures),
60 frontend checks and an API build with no warnings. The final owned standard
preview passed 222 HTTP, 41 futures-storage, 14 schedule-storage, 50 temporal/security
storage, ten adapter and 63 browser checks. This includes actual booking/capture
commit-gate races, expiry after a blocked booking, uncertain retry preservation,
denied-view clearing and exact curve/contract selection. Disposable resource
teardown completed. The earlier hosted run does not qualify these local changes.

The blotter checkpoint passed the complete local command on the same macOS
toolchain: 138 managed core, 63 frontend, 234 standard HTTP, 24 mixed HTTP,
41 futures-storage and 74 browser checks, with the existing rate/temporal/schedule,
schema, adapter and cleanup suites. Final refinements then passed 139 managed
core checks (including distinct-trade pivot counting), 63 frontend checks and a
warning-free API build. The final owned standard preview passed 234 HTTP,
41 futures-storage, 14 schedule-storage, 50 temporal/security storage, ten adapter
and 83 browser checks. These cover saved layouts and hidden-column filters,
explicit pivots, reauthorized snapshot download, shared header time controls,
fresh profile permissions and actual two-session permission removal/restoration
from a populated Power view. Owned resource teardown completed. Publication and
hosted qualification of these later changes remain separate.

Weekday demo population passed 13 additional owned-preview checks, including
2/4/6/8 cumulative bindings on 15–18 December 2020, independent per-book P&L
oracles, exact shared contract identity, repeat-load idempotency and exclusion
from a pre-booking knowledge receipt. The same standard run passed all 234 HTTP,
83 browser and existing storage/adapter checks and completed owned teardown. A
separate interactive launch confirmed that all four dates were populated before
the readiness URL was announced, then completed owned teardown. The frontend's
63 checks and verification-runner checks also passed. These are local results.

The account-layout table workspace passed 63 frontend checks, eight managed
layout-schema checks and a warning-free API build. A fresh disposable standard
preview passed 234 existing HTTP checks, nine account-layout checks, 14 schedule
storage checks, 50 temporal/security storage checks, 41 futures storage checks,
ten adapter checks, 13 seeded-position checks and 86 browser checks. Account
coverage includes separate browser sessions, distinct human identities, current
revocation, stale save/delete versions, clearing browser preferences, denied
saves and switching accounts before choosing a financial date. Desktop and
mobile table layouts were inspected visually. Independent review findings were
resolved, and owned teardown completed. These changes remain locally verified;
production sign-in, hosted execution and publication are separate work.

The UTC transport regression starts the API with a non-UTC database-session
environment while leaving the checker connections independent. Before the fix,
workspace startup failed on valid milestone instants rendered with a local
offset. Explicit UTC database connections restored the four market dates without
changing source dates or relaxing milestone validation. The resulting disposable
run passed 235 HTTP checks, nine account-layout checks, 86 browser checks and the
existing storage, adapter and seeded-position suites, then completed owned
teardown. The API build was warning-free.

The shared analytics workspace passed the complete local command on the same
macOS toolchain, including 139 managed core checks, 235 standard and 24 mixed
HTTP checks, nine account-layout checks and 101 owned browser checks, with the
existing schema, storage, adapter and cleanup suites. Final presentation
refinements passed 68 frontend checks and another complete owned standard
preview with all 101 browser checks. Both disposable database lifetimes completed
teardown. Desktop and mobile screenshots were inspected, including the cross-tab,
saved column order, curve chart and zero-based position bars.

Coverage includes exact decimal sorting, unit/currency partitioning, absent
cross-tab cells, account layout compatibility, native column resize/reorder,
concurrent layout revisions across financial refresh and date changes, chart
reads in the retained context, reauthorized image/CSV downloads, denied-result
clearing, untrusted headings and disposal during component initialization.
Dependency licenses and notices are included in the locally built assets.
Independent adversarial review findings were resolved. These changes remain
local; the earlier hosted result does not qualify them.

The shared quote-scenario checkpoint passed the complete local verification
command: 69 frontend, 159 managed core, 257 standard HTTP, 24 mixed HTTP and
115 owned browser checks, plus the existing schema, layout, storage and adapter
suites. Both disposable databases completed teardown. Final malformed-input and
cancellation refinements then passed 161 managed core checks and a warning-free
API build; the final owned standard run passed 258 HTTP and all 115 browser
checks and removed its disposable database.

Independent numerical checks cover the 7,000 synthetic gas-to-power scenario
oracle, nonlinear and zero-delta curvature, full per-position repricing, large
quantities, quote multipliers and exact position-scale division. API/browser
coverage includes unchanged market observations/holdings, retained contexts,
explicit factor/bump selection, current scenario revocation, reauthorized input
download, denied cache clearing and context/input edits. Desktop/mobile screens
were inspected. Independent adversarial findings were resolved.

The Release [risk benchmark](risk-benchmark.md) additionally compared every
position and exact book total against independent full curve builds and direct
per-position mark formulas across repeated, shared-fan-out and distinct workloads.
It records actual work, allocation type and environment, including the workload
where reuse bookkeeping costs more than the simple reference. These are local
bounded calculation results, not hosted, durable-run or production qualification.

The common instrument/futures-terms checkpoint passed the complete local command
with 71 frontend, 170 managed core, 263 standard HTTP, 24 mixed HTTP and 116 browser
checks, plus the existing schema, storage, layout and adapter suites. Final
precision-boundary and user-clock checks then passed 171 managed core checks and
a fresh owned standard preview with 263 HTTP and 118 browser checks. Disposable
resource teardown completed; the API build had no warnings or errors.

Independent checks cover nonempty immutable memberships, normalized signed
contributions, exact quote/lot scaling, negative prices crossing zero, distinct
execution/clearing increments and retained model/first-trading consistency.
Fractional-lot multiplication preserves representable final results without
rounding a per-lot intermediate. An explicit regression records the narrower
risk unit-precision boundary. Browser coverage verifies user-zone execution entry,
UTC transport and display independently of the browser's own time zone.
Independent numerical review findings were resolved. These results qualify the
supported execution-P&L/open-sensitivity slice, not settlement or full futures PV.


The latest-head futures amendment/editor checkpoint passed the complete local
verification command: 71 frontend checks, 171 managed core checks, 263 standard
HTTP checks, 29 amendment checks, 24 mixed-period HTTP checks and 137 owned browser
checks, alongside the existing rate, temporal, schema, storage, layout and adapter
suites. All owned disposable resources were removed. The API build completed
without warnings or errors.

A separate disposable upgrade database retained a booking and certified context
from before migration 0013, then verified unchanged original rows, publication
identity and historical visibility after backfill. Amendment checks cover stale
and concurrent commands, prior effective/knowledge views, filtering after revision
selection, capability expiry behind the publication fence, operation collision,
exact retry, contract replacement and exact risk event/revision pins. Browser
checks cover immutable history, delayed responses after cancellation, cleared
business content on denial, lost-response recovery, explicit date/mode status,
Settings navigation and desktop/mobile editor presentation. Independent adversarial
review findings were resolved. These checks qualify the bounded synthetic booking
correction workflow; settlement, arbitrary historical insertion and production
lifecycle execution remain outside the implemented boundary.


The readable-reference checkpoint passed 72 frontend checks, an API build with
no warnings/errors, the owned standard preview (263 HTTP, 33 amendment/reference,
47 futures-storage and 139 browser checks, plus existing adapter/layout/temporal
checks) and 24 mixed-period HTTP checks. Disposable resources were removed.
A retained-booking upgrade fixture exercises in-flight publishers at the admission
fence for both trading backfills, verifies complete revision/reference coverage,
and preserves earlier certified business history. The legacy-call drain boundary
for migration 0013 remains required; this does not qualify a generic online upgrade.
Browser checks cover visible references, saved-layout compatibility, exact row
activation and numerical revision sorting across multiple digits. Independent
adversarial review findings were resolved. No fee engine or fee reconciliation
workflow is implemented by this checkpoint.


## Financial swap numerical foundation

The local managed foundation adds fifteen financial-swap behavior checks. It
covers independent fixed/floating sides, typed quantity and quote scaling, both
pay/receive directions, six averaging rules, calendar coverage/boundaries, exact
model-owned projection/index identity, negative prices, positive factors above
one, large-leg cancellation and repeating intermediate averages. The forecast
model rejects valuation at/after its first fixing and caps delivery coverage at
366 days. The final managed core run passed 186 checks.

Rates now pass 40 checks, including independent high-precision numerical oracles
for log-linear factor interpolation, exact endpoints/small roots, missing/default
currency binding, no extrapolation, disabled-discount input boundaries and the
actual financial-swap valuation consumer. A combined commodity/rate snapshot
produces PV 9.71118981067694871; changing a selected rate under the same snapshot
identity recomputes it. Explicit undiscounted pricing consumes no rate inputs.
The interpolation policy preserves stored pillar factors, bounds pillar spans to
3660 days and rounds the final exact geometric root to nearest/ties-to-even at
18 fractional digits. These checks do not exercise a true halfway root.

The complete synthetic verification command passed with 184 core checks before
the final two additional managed regressions, all 40 rate checks, the existing
frontend/shared/schedule/schema suites, 263 standard HTTP, 47 futures storage,
33 amendment HTTP, 139 owned browser and 24 mixed-period HTTP checks. Both
disposable preview databases were removed. The final two regressions passed in
the 186-check managed run. API build has no warnings/errors; local documentation
links and whitespace checks pass. Independent adversarial review findings in
this bounded numerical change were resolved.

No financial-swap storage migration, authorized default-mapping resolution,
booking/editor/blotter/risk integration or swap browser controls are qualified by
this checkpoint. The running preview remains the futures application; these
changes were not published or deployed.


## Earlier net-payment checkpoint (superseded)

The managed swap now generates side payment plans, resolves their exact price
lookups and computes PV solely through the resolved payment schedule. Checks
inspect the actual row's dates, occurrence/currency key, source memberships,
quantity, fixed/index prices, weights, lineage, cash amount, DF, discount
construction inputs and PV. The existing independent discounted-PV oracle remains
9.71118981067694871. Changing a row factor changes its calculated PV; missing,
duplicate, extraneous, wrong-instrument/ordinal/time/currency and nonpositive
factors fail. One immutable contractual plan can resolve different market values
without changing an earlier cashflow result. Rational cash 2/3 with factor 0.6 gives
exact PV 0.4; it is not prematurely rounded to a working decimal.

The complete synthetic verification command passed with 187 managed core,
46 rate, 173 combined-schema, 263 standard HTTP, 47 futures-storage,
33 amendment HTTP, 139 browser and 24 mixed-period HTTP checks, plus the existing
frontend/shared/schedule/layout/adapter suites. Both disposable preview databases
were removed. Independent adversarial numerical review found no remaining blocker;
local documentation links, public-content scan and whitespace checks pass.

This earlier checkpoint exercised a shared net-payment calculation, not the
required independent Side.PV contract. It is superseded by the independent-side
checkpoint below.
Recurring periods, actual fixings, persisted swap/default mapping operations,
blotter/editor inspector and swap risk remain unimplemented. The retained preview
was not reloaded, and no changes were published or deployed.


## Independent side PV checkpoint

FixedSide and FloatingSide independently generate their own schedules and compute
per-unit PVs. Instrument.PV delegates to each side, validates the returned result
and sums membership-signed side PVs; typed trade quantity scales the instrument
result once afterward. No cross-side cash netting precedes side valuation.
Independent payment dates use independently resolved factors. Exact rational side
amounts survive aggregation and scaling before final Money conversion.

The full synthetic verification command passed with 196 managed core checks,
48 rate checks, 173 combined-schema checks, 263 standard HTTP, 47 futures-storage,
33 amendment HTTP, 139 browser and 24 mixed-period HTTP checks, plus existing
frontend/shared/schedule/layout/adapter suites. Both disposable preview databases
were removed. The final removal of the unused legacy common-factor execution
helper also passed the 196 core checks. Independent numerical/context review
found no remaining blocker in the implemented concrete sides.

Regressions reject cached side results from different discount operations or
base/up scenarios sharing the same snapshot. Returned results are bound to the
exact run-local SideValuationContext; cross-operation side caching is unsupported.
Standalone side and rate paths reject conflicting contents under one observation
identity. Tests also retain side-owned lookup/rate evidence, independent dates,
standalone fixed valuation, reversed memberships and delayed trade scaling.

This qualifies the managed bounded fixed/floating valuation path only. Recurring
payments, actual fixings, persistent swap/default-mapping operations, swap booking,
blotter/editor inspector and risk integration remain work. Options are not
implemented; the exercise-control accounting choice remains open in O13.


## Vertical instrument-side editor checkpoint

The existing futures editor separates book/quantity/execution inputs from a
vertical side panel populated from the selected contract catalog. Dates,
currency, unit/lot size, quote conventions and trading instants remain read-only
shared contract terms; selecting another supported contract invalidates amendment
preview. No swap or cross-side initialization controls are exposed, and there is
no copy button. Aligned grid rows support the side-column presentation contract.

The 72 frontend checks and standard owned preview passed, including 263 HTTP,
47 futures-storage, 33 amendment HTTP and 139 browser checks. Independent UI
review found no ownership/context-clearing blocker. Retained API compatibility
was checked read-only: all six selectable contracts provide every displayed field.
Desktop/mobile screenshots were inspected after the final layout changes;
vertical rows fit and the title/close control stays visible while scrolling.
The final 21 editor browser checks passed and their disposable database was removed.
The local static bundle uses the same preview address and retained database;
no database migration or API restart is required for this layout change.


## Financial swap booking and independent schedule checkpoint

The complete synthetic command passes with 196 managed core checks, 48 rate
checks, 72 frontend checks, the existing combined-schema/security/HTTP suites,
152 owned browser checks, 20 new swap HTTP checks and 26 direct swap storage
checks. Both the standard and mixed-period disposable databases are removed.
Sixteen synthetic monthly/daily gas/power examples seed idempotently over four
weekdays. Desktop/mobile editor screenshots were inspected.

The new checks reconcile 31 daily reset prices into one monthly floating payment,
independent side payment dates/DFs, normalized cash allocation, signed side PV and
one trade-quantity application. Stored rows must reproduce the managed schedules.
They reject missing/ambiguous/expired projections, unsupported conventions,
missing policies, incompatible quote changes and canonical no-op amendments.
Contractual fixed strikes remain independent of market-price bounds and ticks.

Storage checks cover all six new forced-RLS tables with real retained rows,
unbound/denied reads, direct-write denial, immutability, failed-write rollback,
historical write denial, later backdated default mapping isolation and capability
expiry while waiting for the publication gate. HTTP/browser checks cover operation
namespace collisions, retry identity, historical knowledge, latest-head book
moves, read-only old revisions, delayed response clearing, denied DOM clearing,
shared product partitions and unavailable measures remaining blank.

This checkpoint supersedes earlier statements that swap booking/default storage
and side inspection were pending. The booking model is one whole delivery month
with monthly-index or daily-calendar arithmetic projection and one payment per
side before the first fixing. Actual fixings, recurring payment strips, additional
persisted averaging rules, swap risk, fees, physical products and options remain
unimplemented. Independent review found no remaining source blocker in this
bounded path; local activation and publication remain distinct operations.


## Generated payment-date rules checkpoint

The full synthetic command passes with 205 core, 48 rates and 72 frontend checks,
154 owned browser checks, 20 swap HTTP checks, eight generated-schedule HTTP
checks and 31 direct swap-storage checks, plus the existing schema/security,
futures/history/layout/adapter and mixed-period suites. The final dedicated swap
browser run passes 16 checks, including payment-date invariance after changing
the user time zone to America/Chicago. Owned disposable resources are removed.

Independent calendar oracles cover both payment rules, strict business-day lags,
closures, following/preceding/modified-following, explicit short-month rejection
or clamp, leap years, output/coverage bounds and null rule rejection. Actual side
plans retain originating rule/calendar evidence. SQL admission rejects mismatched
generated dates and excessive calendar work under the publication fence; private
helpers cannot be called directly by runtime clients. Rule identity stays distinct
when generated dates coincide, and amendments retain the original version.

A disposable upgrade starts with genuine manual-instant bookings under 0015,
applies 0016, compares original column projections, checks exact retained instants
and null new rule FKs, reads the prior historical certificate and reconciles the
old accepted request through the replacement function. A separate owned API
qualification amends sixteen legacy examples using explicitly authored conventions,
checks unchanged PV/dates, preserved history and idempotent retries. No convention
is inferred from arbitrary historic dates.

## Schedule entry and shared table checkpoint

The full synthetic command passes with 205 core, 48 rates, 72 frontend and 171
owned browser checks, nine generated-schedule HTTP checks, 31 swap-storage checks,
and the existing schema/security, futures/history/layout/adapter and mixed-period
suites. Owned disposable resources are removed. Schedule adapter checks include
16 publication/catalog cases.

The symbol helper's suggestions and resolution carry the identical retained
editor certificate. A separate-session publication test proves older suggestions
and resolution do not gain later definitions; a fresh certificate does. Browser
checks cover literal date application, manual-edit invalidation, unavailable
symbols, historical read-only controls, delayed resolution after close and denied
access clearing helper metadata and shared rows.

Real pointer checks reproduce accidental opening during text selection before
the fix. Afterward selection and wheel scrolling leave the editor closed, a
deliberate click opens the displayed source row after virtual scrolling, Enter
still opens it, and horizontal scrolling keeps headers aligned. Schedule table
checks assert a single bounded scroll owner. Currency checks exercise the USD row
and linked keys in the rebuilt standard fixture rather than a browser label alias.


## Trade parties and marked economic identity checkpoint

On 2026-10-18, the managed core passed 216 checks, rates passed 48 checks and the
frontend passed 72 checks. The owned standard browser run passed all 176 checks.
The standard HTTP/database run also passed after the new party regressions were
added; all owned disposable resources were removed. The environment was macOS
Darwin 25.6 arm64, .NET SDK 10.0.402, stock PostgreSQL 18.1 and Chrome 155.0.8059.80.
No existing application or database was a test target.

The new behavior checks exercise normalized and versioned economic keys, marked
field changes, ordered memberships, exact reference versions, independent payment
rules, weighted observations, side UUID conflicts and preservation of published
instrument identities. A fixed canonical payload/hash vector protects the encoding.
API checks reject unavailable joined party labels rather than treating JSON null
as a valid name. Independent adversarial review covered identity reuse, temporal
party visibility, current authority, amendment ownership and migration boundaries;
its findings were resolved.

The disposable HTTP/storage checks book futures and swaps with explicit dates and
exact legal-entity/counterparty versions, amend only those facts while retaining
the unit instrument, preserve prior versions/names and reject missing or invalid
choices. Later-known and future-effective party versions cannot enter earlier
contexts. Foreign-catalog party IDs fail authorized selection and exact-version
foreign keys; party tables enforce RLS. Upgrade checks compare original V1 state
before/after 0018, retain old certified history and status reconciliation, leave
unavailable facts NULL and verify older runtime write entrypoints are closed.

These checks qualify the bounded synthetic booking flows. General party master
administration, wider lifecycle execution and production deployment remain work.

## Contract instruments and economic lookup keys checkpoint

On 2026-10-18, the managed core passed 222 checks, rates passed 48 checks and the
frontend passed 72 checks. The owned standard run passed 176 browser checks, 263
preview HTTP checks, 173 combined-schema checks, 51 futures-storage checks and 31
swap-storage checks, together with the amendment, temporal, schedule, party and
economic-key suites. The environment was macOS Darwin 25.6 arm64, .NET SDK
10.0.402, Node 24.22.0, stock PostgreSQL 18.1 and Chrome 155.0.8059.80.
The full verification command also passed all 24 mixed-period HTTP checks and
removed both owned disposable preview databases. No existing application or
database was a test target.

Independent behavior checks resolve one futures contract through both its exact
reference-series primary key and curve-member aliases before booking. A
market-only reader sees the same canonical identity as a later trade. Different
trade quantities reuse the contract; different pricing bindings remain separate
valuation work. Numerical oracles cover both bindings, quantity scaling and
shared-factor scenarios. A fixed UUID encoding vector and an occupied-UUID
collision check protect exact-key admission.

Storage checks inspect the actual primary/candidate-key columns, reject foreign
series references and conflicting retained projection bindings, and verify that
new futures bookings create no artificial side or wrapper definition. Missing
per-revision pricing references fail holdings, status and retry reconciliation.
Disposable upgrades preserve legacy UUIDs, events, publication coordinates,
certified history and exact pricing references through migrations 0019 and 0020.
Independent adversarial review found no remaining blocker in this supported path.

Futures retain explicit execution-P&L and outstanding-exposure behavior; full
variation-margin lifecycle/PV remains unfinished. Supported swaps retain
discounted/undiscounted choice in their instrument identity. Standalone physical
forward booking, executable options and VaR/covariance remain planned work.

## Amount formatting and common unit conversion checkpoint

On 2026-10-18, final managed checks passed 234 cases and frontend checks passed 75.
The full synthetic command passed the standard HTTP/database/upgrade suites, all
185 owned browser checks and 24 mixed-period HTTP checks. The final catalog-bound
ordering refinement was followed by another managed/API build and the focused
unit qualification, including ten additional owned browser checks. The environment
remained .NET 10.0.402, Node 24.22.0, stock PostgreSQL 18.1 and Chrome 155.0.8059.80;
owned test databases/browser processes were removed.

Independent oracles cover ten energy, power, volume and mass conversions and their
exact inverses, rational results with explicit final rounding, negative/zero
amounts, decimal ties, grouping beyond binary-number precision, inverse price
conversion with quote multipliers, commodity/basis/revision/dimension rejection,
missing/disconnected units, contradictory graphs, overflow and deterministic
precision-bound rejection under reordered inputs. Price-times-quantity regression
oracles verify representable final cash after tiny or overflowing intermediates.

The focused fixture publishes a separate exact unit reference bundle containing
a unit absent from the pricing bundle. API checks retain valid older market data
while rejecting the later unit catalog at an earlier knowledge cutoff; current
knowledge restores the selected catalog. Browser checks exercise tick precision,
editable Profile precision, visible units, retained raw grid values, conversion
input invalidation, explicit fractional rounding and supplemental labels on both
reference and quantity-definition screens. Null display entries fail with a
structured error. Independent numerical, temporal and release-boundary review
found no remaining blocker in this supported implementation.

The retained local preview was updated using an append-only unit reference bundle
and the same owned stock PostgreSQL 16.14 database. Original trading rows,
amendments and pricing references were verified unchanged. Private-port acceptance
covered all four market dates and both trade editors before reopening the URL;
final checks verified served assets, the exact configured 24-unit catalog and
2.5 MWh = 2,500 kWh. This remains a synthetic local preview, not production
qualification. Gas heating-value, density, FX and duration/shape conversions are
not implemented by the multiplicative unit converter.

## Party maintenance and typed power quantity checkpoint

On 2026-10-18, the complete synthetic verification command passed, including 243
managed core checks, 48 rate checks, 75 frontend checks, 194 standard browser
checks and 24 mixed-period HTTP checks. A separate owned fixture passed the party
maintenance HTTP checks and 20 focused party/power browser checks. Separating the
fixture preserves the preview's bounded session limit. The environment remained
.NET 10.0.402, Node 24.22.0, stock PostgreSQL 18.1 and Chrome 155.0.8059.80; owned
disposable databases and browser processes were removed.

Party checks create and append legal-entity/counterparty versions, preserve
certified past reads and exact retries after later edits, reject stale concurrent
updates, enforce effective dates and deny historical writes or revoked authority.
Browser checks cover creation, editing, history and read-only roles. Migration
0021 exposes bounded authorized operations rather than runtime table writes.

Independent amount/power oracles cover 23/24/25-hour delivery days, explicit
flat-MW integration, exact total-MWh conservation, nonterminating interval
allocations, TOU/holiday selection and multiple settlement resolutions. Negative,
zero and very large values, cancellation, explicit final rounding and overflow
are checked. Exact quantity specifications must agree throughout the schedule
and settlement index; matching a unit label alone cannot substitute a different
measurement basis or revision. JSON round trips retain decimal digits and exact
specification references. The UI uses backend-formatted amounts and local
quantity/price/money precision, including interval prices.

Independent adversarial review covered numerical behavior, current authority,
historical visibility, UI invalidation and the retained-preview activation guard;
identified findings were resolved. These checks qualify synthetic party
maintenance and the unsaved power example. Persisting these power quantity rules
on booked sides, linking traders to workspace users and moving remaining deal
precision controls out of Profile remain unfinished.

The retained local preview was then updated on the same owned stock PostgreSQL
16.14 database. Migration 0021 committed only after comparing every pre-existing
trading row; the immutable manifest and reference pins remained unchanged.
Read-only acceptance on a private port and the reopened URL covered both party
catalogs, typed one-MWh March delivery, all four market dates and both trade
editors. Served assets matched the frozen verified release. The retained pricing
bundle has an MWh quantity definition but no MW quantity definition, so it offers
total-energy entry only; no unrelated reference version is silently substituted.
Fresh disposable fixtures qualify both quantity bases. The activation fingerprint
uses explicit UTC rendering because database timestamp-expression text depends
on the session timezone. This remains a synthetic local preview.

## Exchange futures and deal precision checkpoint

On 2026-10-18, the complete synthetic verification command passed with 251 managed
core checks, 77 frontend checks, 196 standard browser checks, 20 focused
party/power browser checks, 12 focused unit/precision browser checks, nine exchange
browser checks and 24 mixed-period HTTP checks. The environment used .NET 10.0.402,
Node 24.22.0, stock PostgreSQL 18.1 and Chrome 155. The focused exchange HTTP/browser
suite also passed on stock PostgreSQL 16.14. All test databases and owned browser
processes were disposable.

The exchange fixture covers 113 dated contracts in 19 exchange product families
plus one explicitly synthetic calendar-day rate control. Independent oracles check
physical/cash execution terms, signed integer lots, 1MW contracts with dated
published MWh totals, calendar-day integration, negative basis quotes, exact P&L,
amendment/reuse and quote-specific ticks. Twenty factor scenarios are evaluated in
bounded batches; unrelated positions have zero shock P&L, including Henry Hub
shocks against directly quoted regional basis futures. Final-valid cash survives
an intermediate price difference outside working decimal magnitude.

Current shared deal precision is persisted separately for trade/fixed/floating
roles. Checks verify unchanged financial events, schedules and PV, historical
write denial, null-context rejection, expected-revision conflicts, scoped editing
and raw exact exports. Profile precision controls are removed; this supersedes
the earlier checkpoint's outstanding Profile item. Trader-to-workspace-user
linkage and booked power schedule quantity authoring remain unfinished.

Separate namespaces retain independent sessions, request tokens, input frames,
identity providers and administration scopes. Tests create identical logins in
both examples, sign in, revoke one independently and reject cross-workspace
updates. Raw authorized SQL rejects missing/incompatible canonical product
profiles and duplicated basis-index declarations. The retained-booking upgrade
checks preserve original financial rows while normalizing authoritative profiles.
Repeated authorized seed commands retain exactly nineteen exchange-family sample
bookings. All market observations and admission-start dates remain synthetic.

Independent adversarial review covered numerical semantics, exact reference
identity, version selection, authorization, asynchronous UI invalidation and the
local release boundary; identified findings were resolved. Futures scope remains
execution P&L and deterministic open shocks. Final settlement, variation-margin
transitions and physical delivery processing remain unimplemented.

The retained local preview was updated on its owned stock PostgreSQL16.14 database.
Migrations0022–0024 and the isolated exchange namespace committed atomically after
asserting every pre-existing baseline trading row unchanged. Main manifest bytes
and reference pins remained unchanged. Private-port and final-URL acceptance
covered all four original dates, both editors, parties, typed power quantities,
113 exchange contracts, nineteen new sample holdings and exact basis references.
Served assets and both manifest receipts matched the frozen verified release.

## Publication hardening checkpoint

A renewed publication review identified current-reference authorization and risk
intermediate-range gaps. Independent regressions reproduced both before their
fixes. Raw futures/swap booking and amendment now reject revoked reference scopes;
actor-owned exact replay/status remain usable under current trade authority.
Discount-default lookup also rejects a market definition outside the certified
historical view. Rejected writes retain no trading publication.

Risk unit cash and execution bases now remain ExactMoney until final position
scaling/conversion. Independent oracles cover opposite-sign near-limit prices,
quote multipliers and sub-decimal unit cash with a representable final result.
The focused stock PostgreSQL16.14 authority/identity checks and252 managed core
checks passed. The Release benchmark rechecked every position/book against its
independent reference; current work, allocations and timing are recorded in
[risk-benchmark.md](risk-benchmark.md). Its mixed performance is not a service
throughput claim.

## Quieter amount presentation checkpoint

On 2026-10-18, three-place final money display and compact noncash presentation
passed 80 frontend checks, the managed API adapter checks and the complete
standard disposable HTTP/database/browser command on stock PostgreSQL 16.14,
.NET 10.0.402, Node 24.22.0 and Chrome 155. All 199 owned browser checks passed;
the disposable database and browser were removed. Meaningful checks first failed
for the old two-place default and a shared cash/noncash formatting cache entry,
then passed after the changes.

The browser checks cover collapsed money-only Advanced settings below the trade
workflow, independent display saves without financial amendments, reopening saved
settings, compact quantity labels, futures tick places and the unsaved power
example's money-only control. Managed oracles verify exact large-value rounding,
compact quantity/factor strings and preserved tick places. Existing noncash
policies remain retained; the editor changes only the selected role's money
places. Calculation amounts, contractual settlement rounding and exact exports
are unchanged. Independent review found no implementation blocker.

The retained synthetic preview was then updated with a frozen verified API/web
release. Independently recreated pre/post migration schemas matched the retained
installation and migration 0025. Transactional comparisons preserved every
existing trading, presentation, reference and market row. Read-only acceptance on
a private port and the reopened URL verified both example namespaces, final-money
defaults, compact noncash strings, futures tick places and served asset hashes.
The existing database, both immutable manifests and all retained trades remain.


## Server booking facts and temporary deal valuation checkpoint

On 2026-10-18 the complete `tools/check_all.py` command passed on stock
PostgreSQL 16.14, .NET 10.0.402, Node 24.22.0 and Chrome 155. It includes
253 managed core checks, 80 frontend checks, database/security checks and 212
standard owned browser checks, followed by the isolated party/power, units,
exchange-futures and mixed-period suites. Disposable resources were removed.

Booking checks qualify authenticated original-human attribution, UTC booking date,
persistence timestamp, read-only API/SQL boundaries, exact retry without another
clock, independent amendment actor, renamed/disabled original membership,
historical exclusion and retained-data upgrades. Old missing trade dates remain
unchanged and can be derived from certified booking facts during amendments.
Migration-derived times and migration-captured names have explicit provenance.
Pending commands use the versioned server-owned booking contract namespace.

Independent draft oracles cover signed futures lots, base-plus-basis propagation,
all-in marks without inferred upstream factors, exact 31-day rational
sensitivities, therm-to-MMBtu conversion, nonzero quadratic gamma, pinned replay,
required policy inputs and distinct deal-risk authority. No draft Price/Risk
operation writes a trade or trading publication. Direct quoted basis futures
retain only their own observable exposure. Browser checks qualify unbooked
pricing without parties, backend kWh-to-MWh conversion through booking preparation,
selected-unit labels, invalidation on edits and delayed-response rejection after
closing the editor. Independent adversarial review found no remaining code blocker.

Swap drafts return PV. Futures drafts return execution P&L/open-price exposure;
variation-margin processing and full settlement PV remain unimplemented. Draft
risk shocks commodity quote factors by one trading tick with interest rates held
fixed; it does not implement a full multi-asset risk run or VaR.


## Combined workbench integration checkpoint

On 2026-10-18 the combined workbench passed the complete `tools/check_all.py`
command on stock PostgreSQL 16.14, .NET 10.0.402, Node 24.22.0 and Chrome 155.
The run includes 87 frontend checks, 253 managed core checks, database/security
checks and 218 standard owned browser checks, followed by the isolated
party/power, units, exchange-futures and mixed-period suites. All owned disposable
resources were removed.

The merged browser checks preserve published TOU product versions, reference
calendars, explicit economic choices, compact amount formatting, server-owned
booking facts and unbooked Price/Risk. The new raw-publication scope regression
first reproduced unauthorized publication after reference-scope revocation, then
passed with migration 0031: both TOU clocks and schedule calendars require current
catalog access before a new publication. Authorized publication and accepted
control replay remain available. Independent adversarial review found no remaining
merge or publication blocker.

Migrations 0026–0028 retain the stored-name/TOU/calendar sequence; booking facts
and draft-risk permission follow as 0029–0030. Legacy booking-upgrade checks retain
original rows and certified facts. The stored-name migration still refuses
populated unnamed catalogs, so these checks do not qualify an automatic upgrade
of such retained installations. Runtime deployment is separate from this source
integration.


## Common book valuation and factor discovery checkpoint

On 2026-10-19, 277 managed core checks passed, including an independent Release
run during adversarial review. New checks cover shared unit values across books,
exact summary netting beyond intermediate range, futures validation without
holding-P&L projection, restricted side inputs, invalid-context parity,
pre-cancelled base construction, typed instrument factor declarations and rejection
of omitted/extra/altered factor metadata. The multilevel basis oracle independently
checks actual swap PV, hedges, all-in independence and MWh/kWh cash/risk dimensions.
Existing schedule checks retain 23/24/25-hour and fractional interval coverage.

The owned stock-PostgreSQL preview integration suite passed with scoped relevant
factor catalogs, 17-factor streamed book summaries, detailed/summary reconciliation,
retained historical inputs, cancellation/authority boundaries and existing booking,
amendment, swap, schedule and storage checks. The managed API checks and 107 frontend
checks passed. API/web builds completed without errors. No existing database or
production runtime was used. The [Release benchmark](risk-benchmark.md) separately
records 50,000-position workload, unit/scenario counts, memory and measured phase
costs. Service capacity, durable runs and complete futures settlement PV remain
unqualified. Changes remain local pending publication approval.

## Nested scenarios and durable risk checkpoint

On 2026-10-21, 284 managed core checks, 107 frontend checks and 26 verification
runner/tool checks passed. The API built without warnings or errors; existing
schedule/date adapter checks also passed. New scenario checks independently
rebuild changed quote inputs, exercise nonlinear mixed-factor terms, preserve
parents/siblings under concurrent children and errors, and reconcile complete
market-preparation work counts.

The isolated `--risk-jobs-check --browser-check` suite passed on stock PostgreSQL
18.1, .NET SDK 10.0.402, Node 24.22.0 and Chrome 156. It reconciled interactive and
background reports exactly after changing the user's UI dates, completed distinct
runs in two independent worker processes, and checked idempotency, owner-only
results, current permission revocation and revoke/restore invalidation. Database
checks cover one-row concurrent claims, incompatible-engine exclusion, expired
lease recovery, stale publication rejection, cancellation/finish ordering,
expired matching cancellation acknowledgment, read-only worker privileges,
service-permission expiry during reads and oversized-result rejection.

All 28 risk browser checks passed, including nested parent restoration, retained
context and input receipts, current-authority exports, uncertain network/server
submission reconciliation, recent-job inspection after date changes, and delayed
status responses after cancellation or context reset. Desktop/mobile screenshots
were inspected; controls remained usable without page-wide overflow. Owned
database, API, worker and browser processes were removed after verification.

The optional Swarm worker definition passed `docker stack config` validation.
No Swarm deployment, multi-node capacity benchmark or production qualification
was performed. These results cover complete independent futures execution-P&L
runs; single-run distributed reduction, broader factor families, full futures
settlement PV and confirmation of crashed-worker termination remain outside
this implementation. Source changes remain local pending publication approval.


## Numeric blotter and financial swap composition

Verified locally on 2026-10-22 using the pinned .NET 10.0.402 SDK, stock
PostgreSQL 16.14, Node 24.7.0 and isolated owned browser/database resources:

- Frontend build and 115 checks passed, including exact numeric comparisons,
  saved price-pivot layouts, quote-multiplier partitions and backend formatting.
- 285 managed core checks and 51 rate checks passed. Independent oracles cover
  price extrema/means, decimal ties, oversized intermediate sums, par quotes with
  independent payment factors, floating spreads and reusable opposite memberships.
- API amount/layout/adapter checks passed.
- The standard HTTP/database preview checks passed, including legacy fixed-price
  admission, exact economic keys, retained histories, current reference authority,
  payment rules and all 31 swap-storage checks.
- The focused draft/browser command passed 110 browser checks plus the new
  composition HTTP checks. These cover numeric-only blotter cells, comparison
  filters and exports, explicit price statistics, par-price review, frozen draft
  risk, both financial swap types, spread amendments and canonical request replay.
- The units/precision browser suite passed its 14 checks with separate numeric
  cells and unit columns.

Reproduction uses `npm test --prefix web`, the MarketData/Rates/API check projects,
and `tools/run_preview.py --check`, `--draft-check --browser-check` and
`--unit-check --browser-check`, with the documented SDK, PostgreSQL and browser
arguments. Every preview allocates and removes its own synthetic database.

The broader browser run stopped at the existing Profile-dialog close/alert
assertion in `tests/browser/profile-dialog.mjs`; that whole browser run is not
recorded as passing. No publication, hosted upgrade or production qualification
was performed. Independent adversarial review remains a publication gate.

## Pivot workspace and linked plots

Verified locally on 2026-10-22 with the same pinned SDK, stock PostgreSQL and
Node versions above. `npm test --prefix web` passed 121 checks; the API build
completed without warnings and its layout/adapter checks passed. The focused
`--draft-check --browser-check` preview command passed 117 browser checks
(62 futures, 48 swaps and 7 grid interactions), plus draft/composition HTTP checks.

Coverage includes grouped pivot column headings, swapping row/column axes,
charts following the selected pivot grouping and retained context, bar/line/
heatmap switching without repricing, exact safe tooltips, unavailable cells,
price-unit partitions, explicit managed mean policies, version 3 account layouts,
legacy layout upgrades and reauthorized table/image exports. Desktop and
390-pixel mobile screenshots were inspected. The mobile plot and style controls
stay within the page; short plots hide redundant zoom controls and quantity
units appear above large axis labels.

All test databases and browser processes were disposed. This qualification does
not add hierarchical subtotals, cell drill-through, chart filtering by table row
filters or production deployment. Changes remain local and unpublished.

## Linked selection and availability calendars

The selector changes passed 137 frontend behavior checks, the API build, and the
isolated standard HTTP/storage/temporal/booking checks. Managed adapter checks
cover reset-specific swap periods, missing pinned observations, and preservation
of structural errors. Calendar checks include missing days, leap days, user-local
snapshot grouping, exact correction identities and repeated DST clocks.

The isolated collection checks qualify migration 0038's source/date availability:
empty sources, dates beyond the certified cutoff and sources without current
reference authority are excluded. Managed range checks preserve gaps and the
31-operating-day request bound. Availability reads recheck current authority after
certification and retain the cutoff for subsequent price reads.

Targeted browser checks passed all 53 swap scenarios and 13 unit/precision
scenarios, including paired calendar dates, invalid-input clearing, read-only
historical terms, independent side values and user-zone timestamp rendering.
Desktop and mobile screenshots were inspected locally. These checks use synthetic
data and disposable stock PostgreSQL resources; they do not qualify deployment.

The exchange fixture passed its HTTP checks and all nine browser checks over the
commodity/exchange/product/calendar selector, including physical and cash-settled
contracts, dated power lot sizes and independently quoted basis contracts.

The complete standard owned browser run passed 270 checks, including market
context, profile, administration, power, parties, futures, risk, amendments, swaps,
tables and units. The standard API/storage suite also passed independently.

A managed regression also verifies that availability dates remain ISO Gregorian
strings under an `ar-SA` server culture. The combined runner reuses its established
HTTP test session after identity-administration checks; explicit trader/risk
profile switches and ordinary authorization/CSRF enforcement remain in place.

The combined standard command subsequently completed successfully, including all
270 browser checks, the post-browser composition/authority checks and owned
resource cleanup. The draft-only runner branch also completed successfully.

### Financial swap ticket quantity and review

Local synthetic qualification on macOS arm64 used .NET SDK 10.0.402, stock
PostgreSQL 16 and an owned headless Chrome session. The API build completed with
zero warnings/errors. Frontend tests passed 139 checks; managed core tests passed
285 checks and rate tests passed 51 checks. The standard disposable API/storage
runner passed, including 20 swap HTTP checks, 10 schedule-booking checks and
31 swap-storage checks, plus legacy migration/replay and temporal/authority checks.

The draft/browser branch passed 135 checks: 66 financial swap, 62 futures and
7 table-interaction checks. The 66 swap checks also passed in a focused owned
session after the presentation refactor. Desktop 1440×1100 and narrow 390×844
renders were inspected for delivery entry, quantity/basis, pricing, payment rules
and trade review. Narrow review exposes signed cash, date, quantity, DF and PV
without horizontal scrolling for those fields.

Independent exact-fraction/decimal oracles verify signed cashflow-to-PV
reconciliation, independent delivery months/payment dates, one application of
notional despite 31 daily observations, supported negative agreed prices and
compatible unit conversions. Browser checks cover explicit par versus missing
agreed price, fixed dates outside floating projection coverage, booking/reopening,
amendments, historical read-only terms, stale selection-only changes, asynchronous
preview completion, display reload and incompatible-unit/quote edits. Existing
high-precision valuation oracles remain unchanged. Independent adversarial review
of the numerical projection, invalidation and historical boundaries found no
remaining actionable issues.

No booked MW integration, daily quantity rates, recurring settlement allocation,
physical gas/power booking or actual-fixing valuation is qualified by this work.
The quantity application response label describes the existing versioned swap
contract; booked quantities, events and financial identities are not migrated.

### Fixing-aware swap increment

The managed core passed 295 checks, including ten fixing checks with independent
mixed-period and fully fixed arithmetic, exact cutoff replay, zero-lag payment
preservation, missing/ineligible fixing failures and frozen actuals under shared
market scenarios. All 52 rate checks passed, including an independent actual-cash/payment-discount
oracle with no commodity projection inputs. Frontend checks passed
141 cases, including explicit fixing-entry parsing and missing-boundary failures.

The disposable `--fixing-check` branch qualifies source revision idempotency,
immutable correction lineage, certified historical cutoffs, exact retained decimal
prices/multipliers, valuation/risk receipt pins, partially fixed pricing and risk,
fully fixed commodity risk, booking/reopening/amendments, reused-side SQL fences,
legacy-versus-fixing-aware identity separation and current authority. An explicit
January 2 frame exercises expired actual observation contracts alongside thirty
live projected observations. At quantity 31, fixed price 3 and thirty projected
prices 3.02 through 3.31, actual prices 10, 16 and 19 independently produce trade
PVs 11.95, 17.95 and 20.95 without discounting. Earlier context and receipt replay
retain their earlier result. A fully fixed monthly price 10 gives PV 217 and no
live commodity factors under the selected rates-held-fixed risk policy.

Eleven fixing-aware browser checks pass over version/source/boundary restoration,
actual/projected review, source-edit invalidation, missing times, quantity
amendments, earlier read-only revisions, receipt visibility, metadata clearing
and fixed-side panels without inapplicable empty space.
Desktop and 390-pixel-wide renders are inspected locally. The standard disposable
API/storage suite and 135 existing browser checks also passed with the additive
fixing migrations. The public
verification runner includes the fixing branch; calculations remain synthetic
and use owned disposable stock PostgreSQL resources. No deployment or settlement
posting qualification follows from these checks.
