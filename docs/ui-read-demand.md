# UI read demand

D310 separates navigation metadata from executable market reads. Opening a page
may read its exact definition/reference bundle and the offered close directory.
These are explicit directories: closes retain their existing 128-choice capacity;
the definition bundle is scoped to an exact revision, not a universal row limit.

Prices and optional visualizations follow a narrower demand:

| Consumer | Read demand |
| --- | --- |
| Startup and newer-close check | Latest eligible local date, including every tied close on that date |
| Market calendar | Explicitly requested availability; no close substitution |
| Contract history | Requested dated contract's declared input series, or every pillar needed by the selected rate curve |
| Custom curves and observation schedules | Declared source series, source, role and sample/final-settlement windows |
| Market board and curve library | Visible cards and rows; batches of at most 64 curves |
| Dashboard | Visible widgets; shared, serial batches of requested curve shapes |
| Blotter curve cells | Visible cells or the explicitly opened position |
| AI settings | Selected permitted tab; Models and Providers share their required directories |
| Volatility product picker | First metadata page, explicit search, More or an exact linked product |
| Swap settlement opening-date picker | Frame summaries from navigation |
| Pilot close configuration | Definition/reference metadata, books and close headers |

Full custom-calendar availability coalesces the actual source materializer's
historical sample windows at one certified cutoff. Exact current samples verify
every original frame pin, then hydrate the declared source series and preserve
only their original selected and frozen pins for native sampling. The outer
calculation frame and its fingerprint remain unchanged. If the aggregate exceeds
16,384 distinct windows, availability uses bounded candidate reads instead of
truncating the calendar. Latest-date requests qualify candidates as needed.
Limited reads finish whole local-date groups, including backward-midnight
transitions, before advancing to an older date.
Immutable reference calendars are materialized once inside the authorized
operation; every candidate still binds and checks its exact context and reference
authority. A later operation reads its own calendar. Source lookups index exact
contract/quote and series keys while retaining duplicates for cardinality checks.
Close directories retain typed IDs and memberships rather than a JSON document
per pin.

Contract history first verifies that every original selected frame pin is visible
under the same SQL statement. It then materializes only the demanded series and
all their same-fact revisions. PostgreSQL materializes visible versions for the
demanded series before joining them to the offered business facts; client row
metrics measure hydration, not physical database reads. Missing selected pins and duplicate identities
remain errors. Frozen markers remain attached to their exact pins. Dependency
traversal describes declared inputs; it does not assign exposure, select a newer
observation or introduce an expiry fallback. The ordinary managed pricer still
performs selection, expiry and numerical checks.

A multi-close history uses one SQL visibility snapshot and reuses immutable model
metadata only within its authorized operation. Observation and correction indices
are replaced per close. A cancelled or partially consumed reader is disposed
before another command uses the connection. Historical context and final native
permission checks remain mandatory.

History uses the session's already offered immutable close directory. A direct
caller discovers it on first use. New closes enter through a directory/calendar
refresh; opening a chart does not silently change its choices. Profile, context
and live-selection transitions invalidate the offered directory. Corrections
continue to follow the requested revision policy.

Delayed visual reads belong to their account, directory, selected close and
context. Replaced or hidden content releases its observers, and obsolete replies
cannot dispatch dependent history reads or publish into the current view. A new
load can retry a failed shape; one generation does not retry itself indefinitely.
A failed Blotter refresh preserves completed shapes and ends unresolved cells'
waits with a reason. Models and Providers share one directory attempt per page
load; switching tabs retains a failure until an explicit refresh or page reload.
Shared `visibleReads` / `whenVisible` helpers own subscriptions and disposal;
`batchReads` owns keyed coalescing, serial bounded requests, cached results and
cancellation. Market, Dashboard and Blotter callers supply their context guard
and rendering. The curve-shape request limit has one shared definition. These
helpers replace the screens' separate queue loops and the unused whole-board
batch planner.

Complete portfolio valuation, totals and risk retain their full selected scope.
Trading reads load the exact selected observation pins; revision inspection and
contract history use separate explicit revision reads. Booking catalogs still price the contracts explicitly selected by the frame when
the corresponding booking form opens. These calculations are not viewport
subsets. The position-facts response remains paged after scoped fact assembly;
this increment does not claim database paging for every portfolio product.

Verification passed 1,316 web tests, 657 managed core checks, 40 API data checks
and preparation reuse, the 1,223-check standard owned-browser suite, 89 exchange /
options / volatility browser checks, 198 custom-curve browser checks and 47
calendar browser checks. Native HTTP checks cover hidden original pins, correction
visibility, partial-reader cancellation, disjoint windows, per-operation calendar
reuse and a qualifying candidate after the aggregate-window limit.

One isolated development-overlay run used 1,219,392 synthetic observations, 522
stored business-day closes and 36 monthly futures points per date for 29 markets,
plus gas/oil futures options and rates. The offered directory retained 127 frames.
Linux containers ran .NET 10 and stock PostgreSQL 16; the API had 2 CPUs / 1 GiB,
and the isolated database had 2 CPUs / 2 GiB. Sequential responses measured:

| Read | Workload | Elapsed |
| --- | --- | --- |
| Latest date | Newest eligible local-date group, including ties | 2.97 s |
| Forward curve | 36 selected gas contracts | 1.52 s |
| Contract history | One dated contract, all 127 offered frames | 2.38 s |
| Full calendar | All offered exact frames | 8.90 s |
| Custom prompt calendar | One live gas contract per frame | 8.08 s |
| Historical custom calendar | 12 gas contracts × 20 prior business dates per frame | 9.61 s |
| Blotter | 66 trades, 74 holdings, 66 priced contracts | 11.15 s |
| Portfolio risk | 67 unique valuations, 60 native market contracts, 5 scenarios | 11.38 s |

Risk reported 79 unit-pricer calls among 335 candidates; the actual queued worker
matched the interactive result. Prior-date futures and options were also read.
Peak API container memory across this sequence was 803,811,328 bytes, including
repeated directory refreshes. Request and native SQL deadlines remained unchanged.
These are measurements of one captured workload, not a general capacity claim or
an attested-release deployment qualification.
