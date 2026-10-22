# Local verification and continuous integration

Run verification from a clean checkout using public dependencies and the supplied
synthetic fixtures. No existing database or application is a verification target.

## Required tools

- The exact .NET SDK in `global.json`.
- The exact Node version in `.node-version`, including its bundled npm.
- Python 3.11+ with assertions enabled; the hosted job uses Ubuntu's Python 3.12.
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
and runs the managed, rate, temporal, schedule and automation checks. It builds the
separate .NET worker and tests its durable execution and controller lifecycle in a
new private PostgreSQL cluster, with synthetic Docker responses. It then runs the stock
database schema suite, standard HTTP/storage/adapter checks, owned browser scenarios
and mixed-period HTTP checks. A failing child command stops the run with a nonzero
status. Python optimization is rejected because it disables assertions used by the
existing verification scripts.

The retained-risk suite (`--risk-jobs-check --browser-check`) also allocates its
own cluster. It verifies nested scenario oracles, captured header context,
idempotent admission, separate worker processes, current-authority checks,
lease recovery, cancellation and browser history/retry behavior. The standalone
`tools/check_risk_jobs_db.py` exercises SQL authority and concurrent claims.
Neither check qualifies a real Swarm cluster or single-run distributed reduction.

The [integrated Live correction path](live-calculation.md) adds an owned gate:

```sh
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin \
  --dotnet /path/to/dotnet --live-calculation-check \
  --browser-check --browser /path/to/browser
```

It checks committed correction propagation through native/custom pricing,
booked PV and independent side schedules, quiet unrelated wakeups, exact older
contexts, shared projections, session admission and displayed-context pause.
Eight browser checks include failed-update visibility and Retry before Live.
Managed checks cover memo-page isolation, semantic slot reuse, nested scenarios,
more than 64 successive base frames and cancellation while evaluation owns a session.
Financial parity across immediate and worker executions compares exact inputs,
PV, sensitivities, graph manifests and instrument-pricer counts. Physical curve
work can differ with cache history; rereading a stored job result stays exact.

Every `tools/check_all.py` gate passed across owned local runs on 2026-10-22 with
macOS arm64, .NET 10.0.402, Node 24.22.0, Python 3.14.2, stock PostgreSQL 16.14
and Chrome 156.0.8078.47. Results include 420 managed core checks plus registered
connected/shared checks, 76 rate checks, 237 frontend checks, 46 Python checks,
406 standard browser checks, 52 custom-curve browser checks and 41 retained-risk
browser checks. Final focused Live, hosted and worker runs passed after review
corrections. Independent numerical, temporal/authorization and UI reviews found
no remaining blocker; disposable resources completed teardown. These synthetic
results do not qualify large financial throughput or a deployed cluster.

The saved managed projection integration has a separate disposable gate:

```sh
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin \
  --dotnet /path/to/dotnet --check --connected-portfolio-check
```

The D189 additions run in that same gate. `tools/check_curve_activation.py`
checks certified effective/knowledge selection, inactive gaps, unchanged exact
pins, replay/conflict behavior, concurrent expected revisions and half-open date
boundaries. Rollback-only SQL probes verify bound-catalog isolation and current
authority after receipt/head assembly, including permission expiry during normal
publication and exact retry. A separately sealed synthetic revision reuses a
native curve code with different prices to prove exact source-revision isolation.
Managed API input checks (`--curve-activation-inputs`, also registered with
`--schedules`) preserve explicit zero revisions, minimum dates and null daily
coordinates while rejecting omitted fields. The owned HTTP checks verify the
same refusals before any activation or booking is written.

`tools/check_daily_custom_projection.py` checks every reset's delivery and exact
price against a deliberately reversed output layout, complete weighted PV and
31 primitive sensitivities. It covers missing/duplicate delivery refusals,
binding reuse through booking/amendment, nested primitive/output stresses and
worker parity after successor activation. A partially fixed January period keeps
three actuals, 28 remaining forecasts, the original denominator and exact worker
results. A separate book consumes one historical ancestor as a full scalar panel
and through a daily child subset, exercising coalesced overlapping demands.
It also consumes a second version with a smaller historical set in the same
book, verifying independent sample-frame admission and worker reconstruction.
Managed API `--connected-samples` checks preserve already-covered receipt rows
while retaining otherwise missing frames; these also run with `--schedules`.
The curve browser gate adds eleven activation checks: publication after Live starts,
both exact-version controls, explicit versus effective intent, inactive/stale
display, pending-request recovery, draft preservation, account reset and access.

The D189 full synthetic runner passed locally on 2026-10-22 in the environment
listed above, including 63 curve browser checks. Final managed API and connected
integration reruns passed after required-input and independent historical-frame
retention corrections. The frontend checks and read-only Compose/Swarm render
checks passed. Independent numerical, temporal/authorization and UI reviews found
no remaining blocker. These results do not qualify financial throughput, an
existing hosted schema upgrade or a deployed cluster.

Its named consumers publish synthetic recipes, explicitly bind outputs to monthly
or daily indexed swaps, and compare scheduled PV, portfolio risk and retained worker results.
`tools/check_weighted_portfolio.py` adds multiple source ports with signed and
normalized weights, exact member relationships, picker eligibility and nested
computed-output risk with an independent numerical oracle.
`tools/check_custom_projection_db.py` checks published-version/output retention,
immutable economic bindings, worker read privileges and direct SQL refusals for
mismatched revisions and missing sources. `tools/check_recipe_weights_db.py`
checks the normalized member-weight relationships and rejected invalid mappings.
`tools/check_published_curve_sources.py` adds parent/child/grandchild panels,
exact formula/storage roundtrips, booked PV, primitive quote sensitivities,
nested ancestor-output stresses and retained worker equality after a historical
source correction. It also checks successor publication without rebinding and
certified historical versions/prices. `tools/check_published_curve_sources_db.py`
checks immutable output FKs, final publication completeness, transitive scope
revocation and the 32/33-edge boundary. The managed connected-recipe checks cover
shared ancestors, context mismatch, exact cycles and forged imported manifests
or output names. The custom-curve browser suite chooses, previews, saves/reloads
and formula-roundtrips the typed published source without frontend arithmetic.
`tools/check_connected_risk_db.py` checks the admitted graph/sample
foreign keys, temporal freezing, publication knowledge cutoff and rejection of
changed pins. These SQL helpers run inside that owned preview; they are not
commands for an existing database. Targeted MarketData.Checks switches are
`--connected-recipes`, `--custom-projection` and `--connected-portfolios`.
Rates.Checks includes exact rational connected scheduled-swap PV. This describes
the registered gates, without claiming a hosted deployment or throughput result.

The D186 cutover adds `tools/check_curve_scenarios.py` to the owned
`--risk-jobs-check` gate for quote/output coordinates, contract/point/parallel/
whole-delivery-bucket selection, nested stress and retained worker equality.
Browser gates exercise explicit weighted rows, formula roundtrips, the published
projection picker and scenario controls while retaining context/authority and
uncertain-request recovery checks. Migration 0080 refuses existing saved recipes;
0081 refuses all retained pre-cutover risk jobs, including completed ones, pending
an explicit operator archive/reset. Verify these cutovers only against
owned disposable resources; they are not instructions to reset an installation.

`tools/check_curve_cutover.py` runs before migrations 0080/0081 in the owned
`--connected-portfolio-check` gate. It inserts retained draft/published v1 recipe
storage fixtures and queued/completed job storage fixtures, requires the actual
migration's exact refusal message, and compares retained rows, table/constraint
inventory and relevant function definitions/privileges after refusal. Each case
rolls back completely before normal empty-baseline migration proceeds. These
fixtures qualify installation refusal, not execution of legacy pricing or jobs.
The [operator checklist](connected-calculation.md#adopting-the-schema-cutover)
describes global inventory and the separate migration transaction boundaries.

The D186 cutover passed every `tools/check_all.py` gate across owned local runs
on 2026-10-21 with macOS arm64, .NET 10.0.402, Node 24.22.0, Python 3.14.2,
stock PostgreSQL 16.14 and Chrome 156.0.8078.47. Checks include 427 managed core
checks plus the registered connected suites, 76 rate checks, 234 frontend checks,
46 Python checks, 406 standard browser checks, 48 custom-curve browser checks,
22 calendar browser checks and 41 retained-risk browser checks. Weighted booked
PV and nested output risk reconcile with independent numerical oracles and worker
results; later historical corrections cannot change admitted job inputs. Calendar
availability reads history once per request under one certified knowledge cutoff,
while each candidate retains its exact effective context and current-authority
checks. The owned SQL regression counts that single history read and verifies the
working header remains unchanged. Deployment configuration rendering and independent
numerical, temporal, authorization and worker reviews passed. All owned resources
completed teardown. No financial throughput or cluster capacity qualification is
claimed.

The complete CI workflow also passed on Ubuntu 24.04 for source
`e4300ac50024e6cc318bd02e2889bef26efe8f2f` on 2026-10-22:
[pull-request verification](https://github.com/zerodrift/leanetrm/actions/runs/39719081352)
and [push verification](https://github.com/zerodrift/leanetrm/actions/runs/39719078560).
These runs qualify the merged D186 cutover; they do not qualify a deployed
installation or the later closeout checks described above.

The closeout checks passed locally on 2026-10-22 in the owned connected-portfolio
gate, including all four pre-migration refusal cases and the weighted booked
PV/retained-worker oracles. Narrowing 0080's guard to drafts or 0081's guard to
queued jobs caused the corresponding published/completed regression to fail;
the checked-in migration files were unchanged. All disposable databases were
removed. The 46 Python orchestration checks and standalone dependency check
also passed. Independent review found no release-boundary blocker. This adds
installation-refusal coverage and operational documentation, not a new pricing
model, historical activation service or performance qualification.

The preceding D183 saved-projection phase passed locally on 2026-10-21 using macOS arm64,
the pinned .NET/Node versions and disposable stock PostgreSQL 16. Verification
covered the complete managed core suite (427 core checks plus registered connected
checks), 76 rate checks, API schedule checks, 221 frontend checks and 46 Python
checks. Focused integration passed for connected draft/book/portfolio/worker
pricing, ordinary draft pricing, stored mixed swaps, retained risk jobs, and
custom curves with 43 browser checks. All 60 swap-editor browser checks passed,
including preservation of a real published projection through Price, Risk,
preview, amendment, reopen and ordinary-new reset. All 36 risk browser checks
also passed with that saved custom trade, including queued worker results. The
connected test appends a historical
price correction after queue admission: a new preview changes, while the retained
interactive and worker results preserve their exact earlier values and manifests.
An explicit quantity amendment retains the custom projection and uses the corrected
price while earlier retained risk remains unchanged.
Completed factor receipts retain no executable child scenario caches; independent
work-count and nested-domain checks pass. Independent numerical, temporal,
authorization and worker reviews found no remaining blocker. These are local
synthetic results, not a hosted deployment or large financial throughput claim.

The earlier [connected calculation prototype](connected-calculation.md) was checked on
2026-10-21 with 427 managed core checks, the registered exact-price and connected
curve checks, 75 rate checks, 34 shared temporal checks, API schedule checks,
209 frontend checks and 46 Python checks. Focused disposable PostgreSQL 16
regression runs also passed for mixed-book valuation, saved custom curves
(43 browser checks) and retained risk jobs (36 browser checks). Their application,
browser and database resources completed teardown. The new managed checks exercise
deep and shared graphs, concurrent sibling scenarios, sparse allocation, cancellation, exact
rational means, rolling and schedule connections, input/output shocks, source
identity conflicts and historical topology changes through ordinary instrument
PV. Independent numerical and temporal review findings were resolved before
publication. The [separate benchmark](connected-graph-benchmark.md) records three
Release runs through five million integer graph edges and the measured scope.
These checks do not qualify the financial compiler at that size or connect it
to hosted portfolio jobs.

Nested-risk qualification on 2026-10-21 passed 398 managed core checks,
206 frontend checks, 19 preview-data checks, 46 Python checks, 382 standard
browser checks and the isolated retained-risk SQL, HTTP and 32 browser checks.
The risk suite includes independent nested repricing, frozen-input transport,
two worker processes, expired-lease recovery, stale publication refusal,
permission expiry during result assembly, cancellation and uncertain-submission
reconciliation. The worker Swarm definition also rendered successfully; no cluster
deployment or throughput benchmark was performed.

The instrument-contract follow-up adds a PV-only nonlinear model and exact
rational holding detail. Its 401 managed core checks, 209 frontend checks and
36 risk browser checks pass. The owned mixed-book suite reconciles all eight
futures and sixteen monthly/daily swaps, including a daily quote's independent
quantity/31 sensitivity, exact holding-to-book sums and unchanged unrelated
holdings. Immediate and queued results match their financial receipts and work
counts after a UI date change. Eleven SQL check groups cover supplemental
exact-version pins, later corrections, frozen input uses, scoped worker reads,
lease/cancellation fences and convention-filtered history. Separate response
assembly probes expire ReadMarketData and RunScenario after live admission;
both refuse publication. These checks use synthetic inputs and disposable stock
PostgreSQL 16.14 resources; they do not qualify new instrument implementations
or cluster capacity. All `check_all.py` phases passed across owned runs for this
follow-up on macOS 26.6.2 arm64 with .NET 10.0.402, Node 24.22.0, Python 3.14.2,
PostgreSQL 16.14 and Chrome 156.0.8078.47. The standard run passed 386 browser
checks; the mixed-risk browser sequence runs after the editor checks have booked
both product families. Deployment configuration rendering passed, and all owned
application, browser and database resources were removed.

For the earlier nested-risk qualification, every `check_all.py` phase passed
across owned runs on macOS 26.6.2 arm64 with
.NET 10.0.402, Node 24.22.0, Python 3.14.2 and Chrome 156.0.8078.47. Initial
database phases, the standard preview, assistant and interval-series suites used
PostgreSQL 18.1 with debug/assertion checks; interval curves and the subsequent
execution, hosted, worker and mixed-period suites used PostgreSQL 16.14. The
interval-curve navigation read failed with `NpgsqlException` on that PostgreSQL
18.1 debug build, so that combination remains unqualified. The same complete
interval-curve suite passed on 16.14. All owned application, browser and database
resources were stopped and removed. Log checks accept either parameter-label
capitalization while retaining credential-leak assertions; interval visibility
checks prove presence/absence over the full catalog without counting every row.

The standard and mixed previews use different disposable clusters. The browser
runner uses fresh temporary profiles and accepts only the launcher's verified
owned target. Normal completion removes the owned application, browser and database
resources. Unconfirmed teardown retains the private directory and fails the check.

The earlier D161 custom-curve qualification on 2026-10-19 passed the complete local command on
macOS arm64 with .NET 10.0.402, Node 24.22.0, stock PostgreSQL 16.14 and Chrome
155.0.8059.80. The 132 frontend checks and managed suites include independent
rolling/locked rational means, inclusive daily-forward BALMO, single/parallel
factor shocks, frozen history, unit/multiplier preservation, typed formula
roundtrips, strict gaps and registered SQL output/count validation. The new
owned HTTP checks cover exact source/schedule references, current scope
revocation, immutable published relationships, certified knowledge cutoffs,
historical corrections and explicit frozen final-settlement inputs. The 25
custom-curve browser checks cover visual edits, raw per-tenor history charts,
formula/SQL parity, uncertain-save retry and permissions; their narrow-screen
bound [measured against a viewport that grew with the
page](#phone-width-overflow-checks). The standard preview's 268 browser checks
and the existing isolated suites also passed. Disposable test resources
completed teardown. All 31 Python checks and the additional history-panel
preview startup with eight futures and sixteen swap examples also passed. This
establishes the bounded local workspace; it does not qualify production
throughput, bookable custom indices or historical VaR.

The subsequent editor and market-calendar changes passed the complete command
on the same toolchain, including 136 frontend checks, 33 custom-curve browser
checks and 22 calendar browser checks. Scoped HTTP checks qualify full curve
dependencies and recipe history windows, exact snapshot/version choices,
user-zone dates, certified historical contexts and current reference authority.
Browser checks cover disabled unavailable days, explicit multiple-close choice,
graph-preserving snapshot changes, stale-response fencing, permission-denial
cleanup and keyboard navigation; their narrow-screen bounds [measured against
a viewport that grew with the page](#phone-width-overflow-checks). Independent
review found no remaining blocker. All owned disposable resources completed
teardown.

The final combined check with migration 0042 also passed after integrating the
reference-read authorization changes. It includes all 279 standard browser
checks, current-authority checks for API and raw mutation-response replay,
independent receipt foreign-key rejection checks and the complete editor/calendar
suites. Restoring reference access permits the original accepted retry without
creating a second publication.

Qualification after integrating the market-navigation and trading-read extraction
also passed the complete command: 138 frontend checks, 298 standard browser
checks, 34 custom-curve browser checks and 22 calendar browser checks. The
fresh-navigation regression verifies custom authoring and preview without pricing
workspace hydration. Exact frame/context selection and stale-response guards
remain intact. All owned disposable resources completed teardown.

The guarded market-read policy change was also qualified on stock PostgreSQL
16.14. Actual runtime reads retain one fresh permission check per mandatory
visibility check, including candidates excluded by their historical cutoff.
Same-context rows remain identical. Checks cover current scope and role revocation,
disabled principals, expiry between reads within one statement, additional
permissive policies, unexpected migration prerequisites and transactional rollback.

A separate Linux x64 comparison isolated these eleven policy expressions on the
schema-0041 application at `2cc918f67035fd671f3b2340b917704fe35c87c0`.
Five balanced pairs each used fresh .NET 10.0.13 Release API processes against one
warm stock PostgreSQL 16.14 database on Linux 6.8.0-88 with four logical CPUs.
Each API and database was limited to two CPUs and 1 GiB of memory; HTTP used
loopback without compression. The synthetic workload
contained 491 contracts, 14,016 source observations and 11,786 reference rows.
Each request priced 405 distinct futures using 439 exact observation pins, with
no booked positions or scenarios. Each variant had five cold and 25 warm samples.
All 60 complete response bodies were identical. Median paired time to response
headers decreased by 43.8 ms cold and 48.0 ms warm; four of five cold pairs and
all warm pairs improved. Median API RSS after the cold request was 139,632 KiB
before and 139,820 KiB after the policy change.
Median PostgreSQL CPU decreased while API CPU stayed similar, and neither container
throttled during the measured requests. This isolates the policy cost; it does
not qualify subsequent migrations, hosted transport or a page-load time budget.

The isolated page-assistant run (`--assistant-check`) first checks requests the API
cannot read (`tools/check_request_refusals.py`). Replace key, Edit model and Save
agent each refuse an expected version or tool-step limit beyond its 32-bit range,
broken JSON, an unknown member, a repeated member and an escaped lone surrogate with
400 `request_format`, and a body over 32 KiB with 413 `request_too_large`. A query
value that does not parse is `request_format` too. Each answer holds only its code and
a message, which is the same for every answer with that code and holds none of the
text sent, and the application log stays byte for byte unchanged, so no character of
the refused key reaches it. Each route's readable body, by contrast, reaches its
handler, which refuses it. The hosted checks send the same variants of an account
update, whose readable body its handler refuses, and the same query value through the
TLS proxy and get the same answers. The provider Test checks expect `request_format`
for an unknown member. In the party editor's browser checks, a name past the 32 KiB
limit is answered 413 `request_too_large`, and in the reference editor's, a symbol
past it; in the contract-date library's, a save sent with a stale request token is
answered 400 `request_token`. None of these pages keeps the request, and no party is
saved.

With its browser scenarios the run opens 32 of the 64 sessions a preview holds: its
modules reuse the administrator's, a second of the administrator's and the viewer's
sessions, and each opens only those its own personas, turns and races need.

Direct runtime SQL checks also hold principal, provider, credential-storage and
audit locks until a synthetic role grant or request context has expired. New key
replacements and accepted-operation retries then fail with SQLSTATE 42501, with
all provider, credential and audit rows unchanged. Ordinary creation, replacement
and replay continue through the provider administration integration suite.

The model and price commands (migration 0051) get the same treatment. Eight direct
SQL cases let the administrator's grants or request context expire while a command
waits at the last point its recheck must follow: its principal row, its model row,
the replay lookup, or its audit write, the last one. Each check names the recheck
that must refuse it, from the error's PL/pgSQL context: the replay lookup's, the
model lock's or the final one.
A market-data grant expiring during an Add price write leaves it
`currency_unavailable`. Every model, price, provider and audit row stays unchanged.
Before 0051 applies, its body runs in a rolled-back transaction, once after
`provider_directory`'s definition changes and once after `actor()`'s privileges
change; both times it refuses the helper. Under a caller search path that prints
those definitions differently, it still recognizes its helpers and applies. Migration
0053 gets the same checks, after Add price's helper definition changes and after the
currency read's privileges change.

Provider qualification (migration 0058) has its own checks after the model checks.
They confirm that the release installs one confirmed usage mapping, the Messages
mapping with its sources, which its adapter applies and which a vendor without a
provider row already lists, and that qualification follows the applied mapping, not
any mapping confirmed for the vendor. Writing nothing, each terms field is refused
when empty, longer than 1000 characters, surrounded by ASCII or Unicode spaces, or
containing a control, bidirectional control or line separator character; each source
when it is not https, names no well-formed host, has a port, user information, a
space or a non-ASCII character, or exceeds 2048 characters; and each date when it is
not a calendar date written yyyy-MM-dd or has not begun anywhere. They record terms,
attest and enable the Anthropic provider, then show that Replace key and new terms
each disable it and leave it unattested until it is attested and enabled again, that
Disable keeps the attestation so Enable alone resumes it, and that a provider with a
key, terms and an attestation but no confirmed mapping stays
`model_provider_unqualified`. A stale version for each command, a request identity
reused for other terms or another command, a second attestation of the same key and
terms, and enabling an enabled or disabling a disabled provider are conflicts;
identical and overlapping retries of each command return the original outcome and
write once. The database itself refuses each command to a viewer and to a delegated
agent. Six direct SQL cases let the request context expire while Record terms, Attest
or Enable waits for its provider row or its audit write, and each names the recheck
that refuses it; every row stays unchanged. Before 0058 applies, its body is refused
after Replace key's definition changes and after the provider state helper's
privileges change.

Display names (migration 0059) have their own model checks. Add model and Rename
refuse a display name that begins with a no-break space, ends with an ideographic space
or contains a right-to-left override, a line separator or a C1 next line, writing
nothing. The table's check constraint refuses one even from its owner, also from a
session with `standard_conforming_strings` off, where both rules still accept ordinary
text. Add model keeps a name with letters beyond ASCII exactly. Before 0059 applies,
its body is refused after the display-name rule's definition changes, after the terms
rule's privileges change and under repeatable read isolation, and it recognizes both
rules under another caller search path. Over stored display names that follow the rule,
including one of exactly 80 characters beyond ASCII, it applies and leaves those names
and both rules' owners, grants and settings unchanged. Over stored names that break the
rule in each of those ways but the C1 control, it stops, naming each model with its
vendor and identifier and echoing no stored name. Without the migration the refusals
fail, except for the C1 control: on PostgreSQL 16.14 (macOS arm64), `[[:cntrl:]]`
matches exactly U+0001–U+001F and U+007F–U+009F under C, libc `en_US.UTF-8` and ICU
`en-US` collations alike, so 0051's rule already refused it. Under those three
collations the new rule gave the same result for every code point alone, embedded,
leading and trailing.

Model capability versions (migration 0060) are checked next. Writing nothing, a
version is refused when its output limit is outside 1 to 10,000,000 or missing, its
reasoning efforts are empty, repeated or not among those its mapping states, its
accepted served identifiers are invalid (`model_identifier_invalid`), repeated, more
than 16 or include the model's own, a list is not one-dimensional from 1, or a fact's
source is not an https URL naming a host or its date is not a begun calendar date
written yyyy-MM-dd; a vendor whose adapter applies no confirmed mapping gets no version
(`model_mapping_unconfirmed`). The checks record versions at the smallest and largest
output limits and with 16 served identifiers; refuse a model without a version
(`model_unqualified`) before considering its provider; enable the model only once its
provider is enabled (`model_provider_disabled`) and qualified
(`model_provider_unqualified`); record a second version while it stays enabled; and
disable it while its provider is unqualified. In a release, rolled back with its check,
that repoints the Messages adapter to a second confirmed mapping, Enable refuses the
model, Disable still turns it off, efforts the new mapping does not state are refused,
and a version recorded under it qualifies the model again. Stale versions, reused
request identities, and enabling an enabled or disabling a disabled model are
conflicts; identical and overlapping retries return the original outcome and write
once. The database itself refuses each command to a viewer and to a delegated agent,
and another workspace can neither see nor change the model. Owner-level constraints
hold each fact's form, including checked dates begun when the version was recorded,
efforts the version's mapping states, served identifiers other than the model's own,
the version's own provider and vendor, a stated-efforts mapping for every adapter, and
the forms of stated efforts and mapping sources; versions and stated efforts are
immutable. Four direct SQL cases let the request context expire
while Edit or Enable waits for its model row or its audit write, and each names the
recheck that refuses it. Before 0060 applies, its body is refused after the model
state helper's definition changes, after the provider qualification helper's
privileges change and over a confirmed mapping whose sources repeat.

The Providers directory states the reasoning efforts of the mapping each adapter
applies, before and during that rolled-back repoint (migration 0061). Before 0061
applies, its body is refused after the directory's definition changes and after the
provider state helper's privileges change.

Provider tests (migration 0062) are checked next, first over HTTP. The API reaches a
loopback fake provider through the provider check endpoint, and every proxy
variable names a trap:

- **Worker:** before serving, the API takes the agent-worker lock and starts a generation,
  and tests run under it. No request waits for the worker's connection: with its backend
  stopped, a test still runs under the generation, and the worker keeps its lock.
- **The request:** a test sends exactly one request to the Messages path, with the key
  only in its `x-api-key` header. It carries the fixed synthetic prompt without workspace
  data or tools, within the test output limit and at the chosen effort.
- **Outcomes:**
  - success, at an end of turn, at the output limit and when served by an accepted
    identifier, priced exactly;
  - a refusal before output;
  - a mismatch, and a malformed served identifier, which is not kept;
  - unrecognized usage;
  - a request for a tool the test never offered, and a stop at the context window, each
    billed as reported;
  - 401, 429, 500 and 529, each with its classification;
  - a too-long and an invalid request;
  - a malformed stream, and one past the byte bound;
  - a redirect, which is not followed.

  Every failure after sending asserts its usage: unknown, or unrecognized for a mismatch.
- **Efforts:** each reasoning effort the applied mapping states passes the adapter's own
  effort check and is sent. If the database also stated an effort the adapter does not
  send, the test would be refused before anything is recorded or sent; the check states
  one for itself, then removes it.
- **Refusals:** route refusals send nothing: NUL in the effort, an empty model, and a key
  the preview's key file cannot open among them. The last also records nothing.
- **Deadline:** a stalled provider closes the test at its deadline with unknown usage. A
  retry meanwhile is `test_in_progress`, and afterwards returns the outcome without a
  second request. A second test in the same session waits for the first, so the request
  deadline cuts it short; that is recorded as its elapsed limit, not an abort.
- **Abort:** an aborted request closes the test as cancelled.
- **Late close:** a close the database cannot take at once is retried until it lands, so
  the outcome keeps the usage the handler read. A close that fails other than
  transiently is logged, and the test stays in progress.
- **Lost permission:** an administrator who loses the permission mid-test still has the
  usage recorded, and is refused the answer.
- **Takeover:** a session that takes the agent-worker lock over closes the running test
  as interrupted, and the test whose close failed too.
  - While it holds the lock, tests are refused, yet a retry of a finished test returns
    its outcome and a viewer is refused for want of permission.
  - Once the lock is free, a worker start that fails after taking it is logged, refuses
    the test and leaves no lock behind, while a retry of a finished test still returns its
    outcome.
  - While a start would succeed, a viewer's refused request takes no lock and starts no
    generation.
  - The API acquires the lock again for the next test.
- **Directory:** the Providers tab lists each shipped adapter's endpoint, here the check
  endpoint marked as one, and each provider's last test; vendors without an adapter have
  neither.
- **Egress:** no request reaches a proxy or redirect target, and the key's last four
  characters appear in no answer, request body or log line.

Then in SQL:

- **Cost:** `token_cost_v1` is exact over revisions built for the check:
  - a prompt at the tier threshold keeps the first rate set;
  - cache tokens can carry a prompt over the threshold, and then every dimension takes the tier's rates;
  - tokens in a dimension that is not offered leave the call unpriced, and none there cost nothing, with and without a tier;
  - an unknown count yields no partial cost;
  - the smallest and largest costs keep all 18 places, at a magnitude beyond C# `decimal`.
- **Worker:** a session without the agent-worker lock cannot start a generation, nor can
  another session while it is held, and the generation advances one at a time, even for
  the owner.
- **Opening:**
  - a test needs every input, and a new test the current generation, whose worker still
    holds the lock;
  - refusals come in their documented order: `model_unqualified`,
    `model_provider_unqualified`, `parameter_required`, `model_unpriced`;
  - a model edited twice is tested under its second version's efforts and output limit;
  - a test opening while its key is replaced waits for the replacement, then refuses the
    new, unattested key;
  - an unpriced model covers no revision, a withdrawal, one not yet in force and one ended;
  - the database refuses a viewer, a delegated agent, a session without a request context
    and another workspace;
  - refusals record nothing;
  - an open record pins the current capability version, the attested key and terms, the
    revision in force at its start, the cost method, the generation and the token digest,
    and returns the key sealed.
- **Retries and closing:**
  - a retry is `test_in_progress` while the test is open, and a conflict for another
    model, effort or administrator;
  - a close with another test's token, a changed token, none, or for an unknown call is
    refused;
  - the token closes a test once, and a later close changes nothing;
  - a retry then returns the outcome, with or without a worker generation;
  - a revision appended while a test was open does not change its cost;
  - accepted served identifiers are priced, unknown, unrecognized and mismatched usage
    store no cost, and a test that reached no vendor costs exactly zero.
- **Owner-level constraints:** these hold every count with its source, recognized usage,
  successes, and mismatches, which name neither a malformed nor an accepted identifier.
  They also hold an outcome's exact cost and a call's effort, output limit and rate-set
  pin. Calls and outcomes are immutable.
- **Lost permission:** a test whose administrator loses the permission still records its
  usage.
- **Recovery:**
  - once its worker's connection ends, a generation opens no more tests, even after
    another session takes the lock without starting one;
  - the next worker closes each test left open as interrupted, once: a further worker and
    a late close change nothing;
  - a worker that starts while a test is opening waits for the open to commit, then closes
    that test, never before it started (migration 0065).
- **Expired context:** three cases let the request context expire while a test waits for
  its principal, its model row or its record write, and each names the recheck that
  refuses it.
- **Last test:** the Providers directory (migration 0064) shows each provider's latest
  test, open and then closed, none for a provider never tested, and none of this
  workspace's to another. It states whether that test ran under the key and terms attested
  now, which stops being true once new terms are attested.
- **Guards:** before 0062 applies, its body is refused after a pricing helper's definition
  changes and after a qualification helper's privileges change. Before 0064 applies, its
  body is refused after the attestation or test state helper or the definition it
  replaces changes, and after the provider state helper's privileges change. Before 0065
  applies, its body is refused after the worker start's definition or the lock helper's
  privileges change. The directory and the worker start keep the owner and grants those
  guards verified.

Agent administration (`tools/check_agent_administration.py`, in the same run, through the
API and in SQL):

- **Roles:** only the access administrator manages agents; the trader, risk analyst and
  market data editor use agents, and each may delegate; the agent reader holds exactly
  its three reads. A trigger on role grants refuses, even from the owner, an unrevoked
  grant of the agent reader role to a human or of any other role to an agent; an
  earlier grant breaking that rule can still be revoked, and a revoked one stays
  revoked.
- **Create:**
  - a name follows the display-name rule and is unique in the workspace whatever the
    case of its ASCII letters, and a create names nothing else;
  - a created agent is an enabled agent principal bound by its own id at the manifest
    issuer, a member under its name and an agent reader, recorded once in each audit,
    and issued request contexts by its agent credential alone, never as a person or a
    service;
  - a repeat writes nothing, and a request identity used elsewhere, a Users & access
    change the suite makes included, is refused;
  - the 256-member limit counts agents, in agent administration and in Users & access.
- **Admission:** while another request is admitted, a refused or repeated create answers
  at once and a create that writes waits for exclusive admission; outside it, a create
  that passes every check is refused with `admission_required`.
- **Save:**
  - an identical save sent while its original still runs, held at the agent's row, waits
    for it and answers as it does;
  - every field is named, and nothing else;
  - text, limits, the grant's duration and the effort follow their rules, and
    instructions beyond 6,000 characters are too long, even beyond the 32,000-character
    storage ceiling. The database applies the bounds the API passes after a repeat's
    replay, so a repeat answers as before under lower ones, while a new save is refused
    beyond them or without them;
  - tools and roles come from their sets, each named once;
  - each allowed role holds `UseAgents` and the tools' permissions, and a refusal names
    the role and the permission;
  - a save names a known agent and the current version of a known, enabled, qualified,
    priced model on an enabled, qualified provider. `model_unqualified` and
    `model_provider_unqualified` come from a repointed mapping and directly recorded
    terms, both rolled back. A refused save writes nothing.
- **Publish:** it publishes the newest version, under the current agent version, after
  rechecking the model, and a Retire under its request identity is refused, as is a
  Publish under a Retire's. Triggers hold for every insert, update and delete, the
  owner's included: the published version only moves forward, a version is never
  updated or deleted, a version names a model of its agent's workspace with an effort
  and an output limit its model version allows, and an agent starts at version 1 with
  an agent principal, unpublished and active. A model edit after publication marks the
  published version.
- **Directory:** it offers only usable models, the roles that use agents and the tools
  with their permissions. It lists each agent once with its changes newest first, and
  another workspace's directory lists none of these agents, models or changes.
- **Retire:** it disables the principal and its membership, so an agent issued request
  contexts until then is issued none. A repeat writes nothing, a retired agent cannot change, and the Users & access
  audit records the loss of access. A refused Retire answers while another request is
  admitted.
- **Authority:** a viewer never administers agents, and a delegated agent runs none of
  the five operations.
- **Guard:** before 0066 applies, it refuses to start while an agent principal holds a
  current role, revoking nothing, and refuses a change to each pinned helper.

Grants (`tools/check_agent_grants.py`, in the same run, through the API and in SQL):

- **Offers:** a user holding an allowed role is offered the published version with its
  model, the permissions its tools need and its grant duration; only the newest draft is
  offered, and only to a user who also manages agents; a user without an allowed role is
  offered nothing, and one without `UseAgents` is refused.
- **Grant:**
  - it names a request identity and a positive agent version and nothing else;
  - it is refused, writing nothing and leaving the epoch alone, without `UseAgents`, for
    a version the user may not use (a draft without agent management, an older draft, an
    unknown version or agent), for a missing permission, named in the refusal, and
    beyond the runtime set's duration bound;
  - the database itself refuses a grant, or the offers, without `UseAgents` or
    `CreateDelegations`, and every grant operation in the context of an agent, a service
    or a person acting under a delegation;
  - it is a delegation from the user to the agent principal for exactly the version's
    permissions, until now plus its duration, recorded once in the grant audit with its
    version, model and permissions, and it advances the epoch;
  - the offer names it, the user's grants list it and another user's never do;
  - a repeat answers as before and writes nothing, and a request identity used for
    another grant, or by another user, is refused;
  - a new grant replaces the earlier one to that agent, and no other, recorded with it,
    even when the earlier one has expired, and a unique index refuses, even from the
    owner, a second unrevoked delegation from one user to one agent;
  - held past the API's command timeout it reports `grant_outcome_unknown`, and
    repeating it grants exactly once.
- **Coverage:** a grant covers a newer version with the same model identity and
  permissions, fewer permissions or a newer capability version of that model, keeping
  its expiry, and not one naming another model identity or needing another permission.
  An expired grant covers nothing, reads nothing and is not listed, yet stays unrevoked.
- **Delegated reads:** an agent created through `ManageAgents` reads market data with
  the user's current grant, and not without a delegation, under a replaced grant or
  after Revoke.
- **Admission and context:** while another request is admitted, a refused or repeated
  grant answers at once and a grant that writes waits for exclusive admission; outside
  it, a grant or Revoke passing every check is refused with `admission_required`; and a
  grant during a delivery-price attempt fences it. An expired request context lists and
  revokes nothing, even under exclusive admission; a grant whose roles, or a Revoke
  whose context, expire while it waits is refused at its last recheck; and a Revoke
  never waits for a request that only refers to its grant. A person in another workspace
  is offered, granted and revokes none of this workspace's agents and grants.
- **Revoke:** another user's grant is answered exactly like an unknown one; a Revoke
  names a request identity and a grant and nothing else; it revokes the delegation,
  recorded once, and advances the epoch, after which the grant reads nothing, is not
  listed and covers nothing; a repeat, or a second Revoke, answers the revocation and
  writes nothing, and revoking a grant its replacement or Retire ended answers when it
  ended; request identities do not cross between grant and Revoke; a user who no longer
  uses agents still lists and revokes their grants; and one whose membership is disabled
  lists and revokes nothing.
- **Retire** revokes the agent's open grants and no other, recording each against the
  administrator; a retired agent is neither offered nor granted; and a Retire under a
  grant's or a Revoke's request identity is refused, writing nothing, so that grant
  still repeats as it did.
- **Audit and guard:** row triggers refuse any update or delete of the grant audit,
  even from the owner. Before 0068 applies, it refuses to start while any delegation
  is unrevoked, even an expired one, revoking nothing, and refuses a change to each
  pinned helper.
- **A workspace's grants:**
  - the agents a user may use, a user's grants and a workspace's name each vendor as the
    Agents directory does;
  - the workspace lists a grant in force and its granted record with exactly their
    person, agent, version, model, permissions and times, and reading them writes
    nothing;
  - a replacement's two records share their instant in one order every read, a Revoke
    is recorded once by its delegator however often it repeats, Retire's ends are
    recorded against the administrator, and an ended grant is no longer in force;
  - a grant whose person is suspended is still in force there; the history is the
    latest 100 records, newest first, while a grant older than all of them stays
    listed in force; and the grants in force are exactly those their people list,
    ordered by person and then agent;
  - only an undelegated holder of `ManageIdentities` with `ManageAccess` reads them: the
    API refuses a trader and a viewer, and the database a person lacking either, an
    agent delegated both, an administrator acting under a delegation and an expired
    request context, and refuses at its recheck a read whose roles expire while it
    waits; an administrator of another workspace sees none of them;
  - before 0079 applies, it refuses a change to the helper it pins.

Turns (`tools/check_agent_turns.py`, in the same run, in SQL, playing the asking request
and the worker with a session of its own holding the agent-worker lock):

- **Ask:**
  - it names a request identity, a scope, a positive agent version, a question within
    the runtime set's bound (`question_too_long`), a page report object of at most 8
    KiB, the knowledge to pin, a session ending after now and before 9999-12-31 00:00
    UTC, and a runtime set, and nothing is written otherwise;
  - it is refused, writing nothing, without `UseAgents`, for a version the person may
    not use (an unknown agent or version, a draft without agent management), for a
    missing permission, without a covering grant, for a conversation that is unknown,
    another person's, in another scope or with another agent, before admission and
    without waiting for it, and under any but the worker's current generation;
  - an admitted ask creates the conversation and the queued turn under the covering
    grant with its accepted event and is answered as admitted; a repeat answers with its
    turn, not as admitted, and writes nothing, any other ask under its identity
    conflicts, and an identical ask sent while its original still runs waits for it and
    answers with its turn, not as admitted;
  - a draft turn is admitted to a person who manages agents.
- **Admission:** a person with an active turn is refused another, and an ask past the
  workspace's limit is refused; admission fails a turn still unpinned at the deadline
  with `ask_incomplete`, freeing its slot and refusing its record, ends cancelled one
  whose cancel was requested before its record, and interrupts one past its elapsed
  limit and the deadline, and those endings stand when the ask is then refused.
- **Pinned context:** only the asking person presenting the turn's token writes the
  record, once, before the turn can start; working data is captured once with its
  cutoff, refused while fewer than the reserve of the catalog's retained contexts would
  remain, counting a capture still in progress once it commits; a retained context is
  pinned as named with an As of frame's exact pins (its name, the observations it
  froze among its observations, its swap market selections and whether it resolves the
  latest correction among them), capturing nothing, and is refused when unknown, when its
  pins do not match the frame, miss or mistype any of them, name no instant the database
  reads (none, or one whose offset is out of range) or an id that is not a canonical
  uuid, and to a person who may not read the catalog's market data; its patterns, like a
  step's, read the same whatever the session's `standard_conforming_strings`; a record
  write or start that finds the cancel requested ends the turn cancelled.
- **Worker writes:** each, a step's included, presents the turn's token under the
  current generation, and a write the turn's state does not accept is refused with
  `turn_state_conflict`; the start answers the version's instructions, model and limits,
  every earlier turn the ask included with its answer, context and ending, and the
  catalog, pins, grant and agent principal its reads run under; a call pins the model's
  capability version, the attested key and terms, the price in force and its
  conversation's latest event, one call at a time; answer text comes only from the call
  in flight, in chunks of 1 to 2,048 bytes, which always fit their event; a provider
  test's close refuses a turn's call; a turn ends only once its call is closed and
  completes only after its last call ended the turn, keeping its answer and closing with
  its usage, summed per dimension over the counts each call reported with unrecognized
  usage counted as unknown, and its cost per currency, unpriced while any call has none,
  before its terminal event; a call closes once, a second close recording nothing, and
  an ended turn takes no further write.
- **Tool steps:** a step answers a request of a closed call, and until each request has
  one no call opens and the turn does not complete; a step names one of the version's
  tools and a code if any, a read a versioned operation, an instant from its call's
  close until now and each market, reference, trades or parties access it ran under in a
  catalog of that kind, a refusal before any read neither, and the request's input is an
  object of at most 8 KiB, absent only when its arguments were refused, and nothing is
  written otherwise; steps answer the requests in order, numbered across the turn, and
  their events carry the step's number, tool and any code, never its input or a value;
  the tool loop's next call opens once each request has a step, and the turn completes
  once its last call ended the turn. A close names how a completed call stopped, and a
  call that did not complete names neither; after the output limit no call or step
  follows and the turn does not complete; once a turn's cancel is requested a step
  records and publishes nothing. A call's requests that would take the turn past its
  version's tool steps, counting the steps it already took, record none, and the turn
  ends `limit_tool_steps`. The tool loop's next call is retried after an overload while
  the turn's failed calls are within the version's retries, and a turn whose last call
  failed does not complete though an earlier one did; after a call whose usage was not
  recognized the next call ends the turn `limit_input_unverifiable`, and once the input
  its calls reported is past the limit `limit_input_tokens`.
- **Navigation targets:** only a Navigate step without a code carries a target, and it
  names Navigate's operation, the instant its page was checked from its call's close and
  the accesses it was checked under, as a read names its own; a read, a refusal or a
  Navigate result without a target or Navigate's operation is refused `turn_input`, as
  is a target that is not exactly a page, selection, context, kind and label, on a page
  the assistant does not read, with a kind other than page, record or filter, a record
  off Curves & prices and the Blotter, or a selection that does not fit its kind or
  exceeds 8 KiB, with a context other than the turn's
  pinned mode, context and frame, or with a label that is not 1 to 200 characters of
  plain text, and nothing is written. The step is published as its step event and then
  a navigation event carrying its number and the target, the answer naming the latter's
  sequence; a refusal, such as an unissued handle keeping its input, publishes its step
  alone; a source the grant no longer covers refuses the publication of both. The
  owner's reads show each target, and once the replay recheck hides the turn's answer
  the conversation and the stream's reads show its hidden marker instead, leaving its
  steps as they are.
- **Sources:** a read joins the source set with what it ran under even when it then
  failed, and a refusal before any read adds nothing; a source the turn's grant no
  longer covers refuses a step's and text's publication and ends the turn
  `access_changed` at its next call; a step after its owner lost their role publishes
  nothing and records the refusal, though its read joins the source set, a later step
  records nothing, and the turn fails `access_changed`; a step or a call waiting for a
  revocation of its owner's role in progress publishes or opens nothing once it commits.
  Without a request context the source-access check answers each market, reference,
  trades and parties access as a context issued to the turn's agent answers it:
  currently, and without the grant, its term, a permission it lists, the delegator's
  permission, `CreateDelegations`, membership or principal, the agent's role, a
  permission of it or its membership, or the catalog's scope; and for a disabled agent,
  which no context is issued to, nothing.
- **Earlier turns:** a follow-up includes an earlier turn whose sources its owner can
  read and its grant covers, and those sources join its own; under a grant that no
  longer lists a permission its sources needed, an earlier turn is withheld, as is a
  turn that included it, each named by a step after the accepted event, and the new turn
  still counts them; an ask whose included turns used a source the agent can no longer
  read is refused `access_changed`. A turn whose source its owner can no longer read,
  and every turn including one, is answered by Stop, a repeated ask and the
  conversation and unfinished-turn reads with its answer and text hidden as
  `access_changed`, and an ask withholds them; with its grant revoked a turn still reads
  in full. The worker's own answers say nothing of the recheck.
- **Retries:** a transport failure or overload is retried as the next attempt up to the
  version's retries; no other failure is retried, nor one whose input is unknown or that
  published text, nor a call after a completed response (`retry_not_allowed`), and a
  turn failing after text keeps it, marked incomplete; a retry opens while the input its
  earlier attempts reported is within the version's limit, and once that input exceeds
  it the turn ends `limit_input_tokens`; the call after the model's capability version
  changed or its price was withdrawn ends the turn `model_changed` or `model_unpriced`,
  and an ask is refused so, as it is while the model's mapping or the provider's current
  key is unqualified.
- **Current authority:** the call after the owner lost any part of their authority on
  its own (their `UseAgents`, `CreateDelegations` or a permission the version needs, an
  allowed role with every permission kept, their principal or membership, the term of
  the grant, the agent's membership, or for a draft `ManageAgents`) opens nothing and
  ends the turn `access_changed`, though nothing requested its cancel; a later save
  never fails a draft turn, nor a later Publish a published one. An append waiting for a
  role revocation in progress publishes nothing once it commits and answers refused
  `access_changed`, and the turn records it: with the role granted again a later append
  is refused too and a Stop requests nothing, and the turn fails `access_changed`
  whatever the worker asks; after a refused first append, the next open fails the turn
  `access_changed` though the call closed retryable over the input limit; one waiting
  for a grant revocation, which cancels its turn, answers the cancel requested; either
  way the worker closes the call with the usage it has and the turn keeps the text
  published before, marked incomplete. A turn whose call completed completes after its
  owner lost their role, its terminal event skipping the publication recheck.
- **Cancellation:** Stop is its owner's alone, in its scope, and requests the cancel
  once; no text is published after it, and once the worker closed the call its next
  write ends the turn cancelled without a code, counting what the call reported and no
  more; the asking session's end cancels it with `session_ended`; provider Disable, a
  key replacing the provider's, model Disable, Retire, Revoke and a replacing grant each
  request the cancel of a turn they affect with their code, and no other turn, the next
  call opening nothing, and while the cause stands an ask is refused for it; a call
  opening while its model is switched off waits for the switch and then ends the turn
  cancelled; the worker is told of each cancel request once, by a notification naming
  the turn.
- **Lapses and recovery:** a repeat waiting for its original, an ask waiting for
  admission or for its agent and a record write waiting for its turn or to capture are
  refused at their own recheck once their authority lapses, writing nothing; a worker
  start waits for a write in flight, then interrupts every turn an earlier generation
  left queued, running or cancel requested, once, closing its open call and keeping its
  text marked incomplete; while no session holds the agent-worker lock no ask or worker
  write is accepted.
- **Reads:** a person's conversations are theirs in the scope, latest activity first,
  with their agent and latest turn; one conversation answers its turns in order and its
  events after a sequence; a person marks their conversation read through a sequence it
  holds, never backward, and it is unread while later events exist; each scope keeps its
  own unfinished turn; another person reads and marks none of them, and a read naming no
  known scope is refused.
- **Stream read:** one turn of a person's conversation, the latest unless named, as the
  conversation read shows it: its state, start, incompleteness and hidden marker, the
  instant its elapsed limit passes, the conversation's latest sequence and the turn's
  events after a sequence, those the conversation read answers, writing nothing. A
  sequence beyond the latest, before the event preceding the turn's first, or with more
  of the turn's events after it than the window is refused `replay_unavailable`, and
  one after an ended turn's last `turn_not_running`; a named turn reads alone though a
  later one began, and a turn ending without completing after text is incomplete. The
  read is its owner's alone, in their scope (`conversation_not_found`), names a turn
  only of its conversation and refuses malformed input (`turn_input`), and needs
  `UseAgents` and a request context; the runtime executes it but not the helper that
  shows an event. Once a source of a turn is unreadable it hides the text as the
  conversation read does and leaves the other events, steps included, as they are, and
  with the grant revoked it shows the text again. The conversation read answers byte for
  byte as 0072's did, with an answer hidden and without.
- **Records and guard:** triggers and constraints refuse, even from the owner, any
  update or delete of an event, a step or what its read ran under; deleting a
  conversation or a turn; moving a conversation's read mark back or changing anything
  else of it; and rewriting a running turn's answer, clearing a refused publication or
  completing its turn, moving a turn backward or changing one that ended. A step without
  input is a refusal of its arguments and answers its own turn's call. The Providers
  directory's latest test is never a later turn call. Before 0069 applies, it refuses a
  change to each pinned helper or to the privileges of one; before 0071 applies, it
  refuses a change to the ask it replaces or a helper the ask relies on, or to the
  privileges of one; before 0072 applies, it refuses a change to each definition it
  replaces or helper it relies on, or to the privileges of one; before 0078 applies, it
  refuses a change to the definition it replaces or a helper the stream's read relies
  on, or to the privileges of one, the risk worker's on the access functions included;
  before 0086 applies, it refuses a change to each definition it replaces or helper a
  Navigate step relies on, or to the privileges of one.

Turns over HTTP (`tools/check_agent_turn_http.py`, in the same run after the SQL turn
checks, through the API against the loopback fake provider):

- **Asking:** an ask is admitted, pinned over working data at its frame and run by the
  worker to completion, keeping the streamed answer and closing its call with the usage
  the stream reported before the terminal event. Its one request carries the key only
  in its header, and sends the fixed preamble with the version's instructions, the
  question, the version's output limit and effort, and, for a version without tools,
  none; this check's agents have none, and the tool checks below give theirs. The turn
  records the frame's exact pins. A repeat answers with its turn as it stands and sends
  nothing. A follow-up sends the earlier turn as history, labeled with its ending and
  pinned context, before the question. An answer streamed in many deltas at once is
  coalesced and published whole and in order, in pieces of at most 2,048 bytes.
- **Knowledge:** working data is asked only at a frame of the session
  (`snapshot_missing` without one or for another). While a historical view is
  selected, its retained context is asked with a frame of it, pinned as named with the
  frame's pins and capturing nothing, and working data is refused, as is any retained
  context the session has not selected (`context_required`).
- **Refusals:** an ask without a covering grant (`grant_required`) or while a turn is
  active (`turn_active`) is refused, as are a question too long (`question_too_long`)
  or missing (`turn_input`), a repeat whose request differs (`turn_conflict`), a
  conversation with another agent (`conversation_agent_mismatch`) and Stop for an
  unknown turn (`turn_not_found`), each with its status and sending nothing.
- **Endings:**
  - Stop cancels the call in flight, which closes `cancelled` with unknown usage, and
    the turn ends cancelled, keeping the text published before, marked incomplete;
  - mid-stream, a profile switch ends the session's turn `session_ended`, provider
    Disable `model_provider_disabled`, and text after a role revocation is refused, so
    the turn fails `access_changed`;
  - a turn past its elapsed limit fails `limit_elapsed`, its call closed so, keeping
    its text;
  - an append that waits past the worker write's bound for the epoch row, while the
    call still reads, stops the call, which closes `cancelled`, and the turn fails
    `agent_runtime_unavailable`, keeping the text published before;
  - a response stopped at the output limit fails `limit_output_tokens`, a refusal
    `model_refused`, and an overload after sending, whose input is unknown,
    `model_provider_overloaded` without a retry.
- **One worker:** a worker that loses its connection releases the agent-worker lock and
  stops and closes the call in flight, which closes `cancelled`, and the next worker
  interrupts its turn; while another session holds the lock, an ask is refused
  `agent_runtime_unavailable`, and once it is free the next ask starts a generation and
  completes.
- **Reads and the key:** a person lists their conversations, reads their unfinished
  turn and marks a conversation read, and no other person sees them; no log line or
  request body carries the key, and no request goes through a proxy.

Tools over HTTP (`tools/check_agent_tool_http.py`, in the same run after the turn
checks, in the administrator and trader sessions they hand over, so it opens no session
of its own; once the Usage checks have run too, the run asserts that the sessions the
process has opened, in every suite before, leave at least ten of the 64 a scope keeps
for the browser and later suites):

- **The loop:** a turn whose first two calls stop for tools completes once the third
  ends it. Each request offers the version's switched-on tools in the fixed order,
  Navigate after the reads, with automatic choice and closed schemas that require every
  member they state; each request is answered by a step, numbered across the turn,
  before the next call opens; the next call replays the reply unchanged and then the
  results in order; and the text is kept exactly as streamed. A step records its tool,
  operation, the request's input, the instant it read, after its call's close and before
  the next call, and every access its read ran under, and its event carries only its
  number and tool; each read names a target handle for its own selection and one for
  each curve or trade it lists, numbered on across the turn and its calls; the turn
  records `turns_v4`.
- **Identity:** each read runs in one request context of its own, issued to the agent's
  principal under the turn's grant on the `openetrm-agent-reads` backends; page views
  and trades bind the context the ask captured, even after the session selects a
  historical view while the turn runs, current-only definitions none, and no read
  captures a context.
- **Values:** each read returns its page's values without page-only material: the
  curve's prices, equal to the page's `evaluate` with the displays the page's display
  operation returns; the curve directory and the curve's input observations; contract
  definitions; a reference table's rows; the schedule library; TOU products and power
  hours, labeled with the instant they were read and power hours with the frame; the
  counterparties; and the Blotter's rows, a pivot's totals and a booked futures
  trade's and swap's revisions, with the swap's valuation, at the turn's context. Read
  current page reads the page stored at ask. A party name addressed to the model
  reaches it only inside a tool result.
- **Displays:** every amount a read displays, in Blotter rows, pivot and book totals
  and both trades, is the display the page's display operation returns for the page's
  own inputs: a row's ticks and deal formats, the default for totals and a swap's
  terms, a futures trade's prices at its contract's settlement-price increment, and a
  swap's valuation and each side by the money formats the trader saved for the deal.
- **Navigate:** a request names a page the assistant reads or a handle a read returned
  in this turn: a Blotter row's trade, the Blotter itself and a curve from the curve
  directory are each published as a navigation event right after its step, with the
  page, the selection (the trade at its row's revision), the turn's pinned mode, context
  and frame, the kind and the label, and the model receives the target's page, kind and
  label; each step records Navigate's operation, the instant its page was checked and
  that page's reads' accesses, each check in a request context of its own issued to the
  agent under the turn's grant on the agent pools. An unissued handle, a handle that
  only a party name the model read names, and a page outside the schema are refused
  `tool_arguments_invalid` before any check, the first two keeping their input, and the
  turn continues; under a grant that lists only market data, the Blotter is refused so,
  adding nothing to the source set, while Curves & prices is a target. A follow-up in
  the same conversation naming a handle its earlier turn issued, before it reads, is
  refused so too, keeping its input. A historical view without a frame publishes a page
  target keeping its retained context with no frame. A page check held where it reads
  the reference catalog's scope stops at once on Stop, ending the turn cancelled with no
  step, and past the 5-second `ToolRead` fails the turn `tool_failed` with a step naming
  Navigate's operation, the check's instant and its accesses.
- **Go and Back:** the panel selects a target's retained context again by its id, which
  yields exactly that context, which the session then shows and an ask pins, without
  capturing another; a request without the correction policy, naming an unknown context
  or with any other member is refused, leaving working data selected. Hosted mode does
  not map the route.
- **Refusals:** a read refusing the page's selection is `page_selection_unavailable`,
  arguments outside the schema `tool_arguments_invalid` and a read's own refusal its
  code (`missing_curve`), each a step the model receives as an error with only its code
  and fixed text, and the turn continues; a page whose tool is off is
  `tool_not_enabled`, reading nothing, and a version offers only its switched-on tools.
  A historical view without a frame refuses frame-consuming reads `snapshot_missing`,
  current-only definitions `power_historical` and an unsupported page
  `page_not_supported`, each before reading, and reads the curve directory at its
  retained context.
- **Endings:** a call whose requests would pass the tool steps has none run: no read, no
  request context and no step, and the turn fails `limit_tool_steps` without another
  call. Stop during the call after a step ends the turn cancelled, keeping its step and
  text. After the delegator's role is revoked while a call is held before its tool stop,
  the read is denied, its step recorded with the accesses it was denied but not
  published, and the turn fails `access_changed` without another call, its text then
  hidden from the owner's reads.
- **Reads in flight:** a read held where it reads, once its context is bound: Stop ends
  the turn cancelled and the session's end `session_ended`, each stopping the read
  well within its own bound and writing no step or other call; a read whose call
  stopped for it shortly before the turn's deadline is stopped there, failing the turn
  `limit_elapsed`, and one past the 5-second `ToolRead` fails it `tool_failed`, each
  with a step naming that bound with the read's instant and accesses; and a worker that
  loses its generation stops the read at once and writes no step for a second, the
  next start interrupts the turn, and once the lock is free the next ask completes.
- **Page reports and the key:** ask refuses a page report outside the closed set, or
  with a selection its page does not read, `page_report_invalid`, writing and sending
  nothing; no log line or request body carries the key, and no request goes through a
  proxy.

The managed tool checks rebuild the page's exact frame from a turn's record, for working
data and a retained context, and refuse a record without its frame, of another catalog,
naming another frame or in an unknown mode. They check the offered tools and their
permissions; that each schema requires every member it states, with no top-level
composition; that each tool accepts every member, its view's own as stated and the
others null, and refuses every other input, a member left out included, keeping none,
and that the largest accepted input fits a step's 8 KiB; the closed set of page
reports, ask's refusals and Read current page's dispatch; how a read's refusal,
denial, stop or failure is named and the fixed texts, and that a request
for a tool the version has not switched on is `tool_not_enabled` before any read; the
500-record and 64 KiB bounds, never truncating; each result's envelope and projection,
a trade's shown from its contract's increment and the deal's saved formats; and, over
the corpus in `tests/fixtures/amount-display-cases.json` that the page's checks also
assert, with the tick and format an editor's walk starts from, that results show
amounts as the page does, each amount once, by the last walk that reaches it. They
check that each read issues its own selection as a target and then each curve or trade
it lists as a record target, a trade at the revision read, under handles numbered from
the turn's next, each a page report its page reads, labeled with the page and what it
shows; that a page target names one of the seven pages the assistant reads, checked by
the accesses and permissions that page's reads all run under; that a target's event
keeps the turn's pinned mode, context and frame, and Navigate answers with the
target's page, kind and label; that Navigate's schema offers exactly those pages and
the handle form, and its parser takes exactly one of them; that a turn's handles name
targets only once their step is published, numbered on without a gap or a repeat, so a
handle the turn did not issue resolves to none; and that a result past the row or byte
bound issues no target, its rows counted before any target is issued, so the turn's
next read issues from the same handle. A tool stop continues the loop with the reply and
the results in order, and a request for a tool the request did not offer is
unreadable, keeping its final usage.

The live stream over HTTP (`tools/check_turn_stream_http.py`, in the same run after the
turn checks over HTTP, through the API against the loopback fake provider). It opens no
session: it streams in the administrator and trader sessions those checks hand it, acts
as other people by switching the administrator's profile, and sends the rest without a
cookie.

- **Replay:** a finished turn's stream opens with a comment, replays its stored events
  after the sequence through its terminal event, which says whether the turn is
  incomplete, and ends; `Last-Event-ID` overrides `after`; a sequence beyond the
  latest, or before a follow-up's first event, is refused `replay_unavailable` and one
  at a finished turn's end `turn_not_running`, each by an event without an id.
- **Admission:** a stream without `Sec-Fetch-Site: same-origin` and `Sec-Fetch-Mode:
  cors` (none, same-site, a typed address, no-cors, a navigation) is refused
  `access_denied`, without a session or with an unknown one `session_ended`, and with
  a foreign Origin 403, none with CORS headers or a new cookie; a sequence or
  `Last-Event-ID` that does not parse is `request_format` and a negative one
  `turn_input`; another person's conversation is `conversation_not_found` and a person
  without `UseAgents` `access_denied`. Without a session every assistant route answers
  `session_ended`, a command carrying the page's request token included, and pages
  `access_denied`.
- **Live:** a stream of a stalled turn holds no database transaction while it waits and
  sends each piece of text once it commits, in order without a gap or repeat, through
  the terminal event, its text the stored answer; a step reaches a stream already live
  once it commits, while the call after it is still open; a navigation target reaches
  the stream as a `navigation` event right after its step, as the conversation read
  shows it; an answer holding CR, LF, CRLF, `event:`, `data:` and `id:` lines and a
  blank line arrives only inside text events, never as a `navigation` event, byte for
  byte the stored answer; a stream dropped while its turn runs resumes
  through `Last-Event-ID`, and one opened once the turn has ended through `after`, each
  with exactly the events that followed.
- **Endings:** a newer stream of an unfinished turn, in the same session or another of
  its owner's, sends the older `replaced`; after Stop the stream sends the usage and the
  cancelled ending, incomplete; a profile switch ends the session's streams
  `session_ended` at once, not at their next heartbeat, while a turn asked in another
  session completes; choosing the profile a session already has ends its streams too and
  cancels its turn `session_ended`; a switch while a stream is admitted, its first read
  held, refuses that stream `session_ended`, replacing none, while the turn's stream
  goes on to its ending; a stream whose owner loses `UseAgents` is refused
  `access_denied` once the turn failed `access_changed`; a lost generation refuses its
  streams `agent_runtime_unavailable`, a stream is refused with a 503 while another
  session holds the lock, and once it is free a stream starts the next generation and
  replays to the interrupted ending; an idle stream pings 13 to 20 seconds apart, and
  one whose turn stays unfinished past its elapsed limit and the admission deadline, its
  row held, is refused `stream_expired`.
- **Sessions:** the cookies of both sessions are as they were handed, no response is
  left scripted and no request goes through a proxy.

The managed checks split answer text into appends of at most 2,048 bytes, never inside
a character and filling each to the bound where a character ends there, and send each
earlier turn's ending, pinned context and answer, marked when incomplete or noted as
missing, before the question. A table of call endings shows how each ends its turn:
completed, at the output limit, at the deadline whatever was published, interrupted
after text unless too large, otherwise with its failure's code, and retried only after
a transport failure or overload whose input is known and that published nothing, while
retries remain. A read that returns once its turn is to stop is no step, whatever it
returned, and one its bound stopped fails the turn `limit_elapsed` at the deadline,
else `tool_failed`.

The stream's managed checks write each frame: one data line of escaped JSON under a fixed
name, no id on `refused` or `replaced`, and a pushed piece of text byte for byte as the
event read back, hidden as the replay recheck hides it. They match the stream's path
alone, parse resume points, and drive one stream's delivery with a manual clock: from
where it resumes, pushed text in order once, a gap, a step and the run's end read,
heartbeats and the re-read before the turn starts, the session's end and profile,
expiry, a newer stream, a cut, shutdown, refused and failed reads, and a hidden answer.
The subscriptions replace a turn's older stream unless the newer one stopped first, end
the streams a session opened under the profile it switches from, and cut a full buffer
without the writer waiting.

Usage (`tools/check_model_usage.py`, in the same run, through the API and in SQL, over
calls and turns it writes as the database owner at chosen instants of 2019, since a call
records its own start):

- **Access:** only the access administrator role reads usage; a viewer and a trader
  neither read nor export it; an identity holding `ReadModelUsage` alone reads and
  exports it and reads no model, provider or agent directory; an agent under a delegation
  listing `ReadModelUsage` neither reads nor exports, and the runtime executes none of the
  internal helpers; a reader in another workspace sees none of the calls.
- **Inputs:** an unknown, differently cased, offset-only, blank, padded, NUL-bearing or
  absent zone is `report_zone_invalid`, as are `localtime`, `posixrules`, `Factory` and
  `posix/` and `right/` names, whether or not the database lists them; equal, reversed,
  367-day, malformed, impossible, timed and absent dates are `report_range_invalid`; a
  grouping other than model, user or agent, an export without a request identity and an
  unknown member are refused; none of these records anything, and a range of exactly 366
  days has 366 days.
- **Days:** in New York the spring-forward day lasts 23 hours and the fall-back day 25,
  holding both 1:30s; in Santiago the day whose midnight is skipped starts at 01:00 and
  lasts 23 hours; in the Azores, whose fall back repeats midnight, 26 October 2025
  starts at its first midnight, 00:00Z, lasts 25 hours and holds a call at 00:30Z,
  while the 25th lasts 24; `CET` is read as the zone, so 2 July 2025 starts at 22:00Z on
  the 1st and holds a call at 22:30Z; the 30 December 2011 that Apia skipped is a day of
  no length; in Goose Bay, whose fall back from 00:01 repeated both midnight and the
  instant before it, 7 November 2010 starts at the later midnight, 04:00Z, and the 6th
  lasts 25 hours, as the documented rule gives; calls at 23:59:59.999999 and at midnight
  fall on their own local dates, as do provider tests on the range's edges, which fall
  inside or outside it.
- **Figures:** a report names its format, `usage_report_v1`. By model, user and agent,
  each member and provider tests count calls by usage state and those without a cost,
  known tokens per dimension beside the calls that left each unknown, the cost of
  priced calls and its share, turns by terminal state, answers with an exact cost per
  answer and the other turns with their cost of priced calls apart, and each day's cost
  of priced calls, all equal to amounts computed from the synthetic calls; the three
  views have the same totals; two references sharing a code stay apart; a member whose
  answer lacks a cost has no cost per answer, and one with no priced call no cost,
  never zero; a revision appended later changes no cost and flags only the priced calls
  that start while it is in force; no question, answer or page report appears.
- **Methods:** `token_cost_report_v1` rounds ties to even at 18 places and at a 2-place
  percentage and has no ratio without a positive whole; `token_cost_display_v1` groups and
  trims without rounding, so 10^-18 shows in full.
- **Labels:** a reader without market data sees no currency label, a reader of market
  data sees each from its reference row with its catalog and revision, and each vendor is
  named.
- **Export:** an export records the report it returns with its request against the
  reader, naming no provider, model, agent or key; the tab lists it first; a repeat
  answers identically and records nothing; an export's request identity reused for
  another grouping, by another reader or for an agent change is refused, as is an export
  under the identity of a Users & access change, and none records anything; two
  identical exports sent at once by one reader record one export, and both answer with
  its report; the tab lists only the latest 100 exports; row triggers refuse any update
  or delete of an export record, even from the owner, and check constraints refuse one
  naming a provider, model, agent or key. Before 0075 applies, it refuses a change to
  the definition or privileges of the helper it pins.

Usage at volume is a measurement with sanity assertions and is not part of
`tools/check_all.py`:

```sh
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin --dotnet /path/to/dotnet \
  --usage-benchmark
```

In an owned standard preview it qualifies a provider and makes three priced models, with
a published agent on each, through Settings › AI. As the database owner it then writes
one workspace's year in New York, 100,000 calls between 2025-01-01T05:00Z and
2026-01-02T05:00Z: 50 users each ask one of the agents 980 questions with two calls
each (49,000 turns), and 2,000 provider tests run beside them. One model prices no cache
write, so its 30,496 recognized calls are unpriced; 2,646 calls have unknown usage; each
user's last turn is still running, its second call open (50). The access administrator
reads the 366 days grouped by user, 51 rows in one currency, three times in SQL
(`EXPLAIN ANALYZE` execution time) and three times over HTTP, then exports them once.
Every answer must count the declared calls and rows, and every read must finish within
the API's 5-second command timeout, or the run fails naming its step.

Three runs on 2026-10-21 (PostgreSQL 16.15, .NET 10.0.402 Debug build, Linux x86_64 with
4 logical processors and 16.9 GB; client, API and database on one machine) wrote the
workload in 17.0 to 18.0 s. The report took 0.90 to 1.20 s in SQL and 0.89 to 1.25 s
over HTTP, and the export 0.99 to 1.09 s. The report is about 118,700 bytes of JSON; its
export record holds 129,242 bytes as text and stores about 14,650 compressed. The call,
outcome and turn tables hold 100,000, 99,950 and 49,000 rows in 108 to 110 MB, and the
API's resident set grew by 18 to 19 MB across the reads, to at most 150 MB. A report's
work grows with the calls in its range; these runs time one reader on one machine and
qualify no larger volume, concurrent use or Release build.

The Users & access checks expect `agent_managed` for an agent create, the agent reader
role, and an agent update with and without roles, which leaves the agent unchanged. Its
browser scenario creates its own agent and finds it listed as an agent reader without
Edit. The temporal storage checks create an agent through `ManageAgents` and show it
reads market data only under a current delegation. The reference-policy agent holds the
agent reader role. The hosted checks find the provisioned owner with exactly the six
human roles.

Under `--hosted-pilot` (D192) the hosted check runs the assistant against the loopback
fake provider (`tools/check_hosted_assistant.py`), through a proxy that passes event
streams through as the gateway does:
- the hosted start takes the worker lock and starts the next generation, calling no
  provider;
- Replace key without the installation's key file is `credential_unavailable` and stores
  nothing;
- Settings › AI qualifies a provider, prices and enables a model and publishes an agent,
  which a person grants and asks;
- the ask's read runs on the hosted agent pools in the agent's own context, its requests
  reach the check endpoint alone, and the turn binds the sign-in session it was asked
  under, ending with it;
- signing out ends that session's stream with `session_ended` at once, while a held
  generation row keeps the worker from ending the turn, then the turn ends cancelled
  `session_ended`, while another account's turn runs on to its answer;
- a password reset and disabling the member in Users & access each cancel the turn of
  the session they revoke `session_ended`;
- an application session opened under an earlier sign-in is refused `session_ended` on
  assistant routes, and the next bootstrap opens one bound to the current sign-in.

The hosted browser scenario finds the person's grants in the profile dialog and offers a
provider administrator Settings › AI and the workspace's grants. In SQL,
`check_agent_turns.py` refuses a hosted ask `turn_input` whose binding names another
sign-in session or ends after it, and finds that signing out, a password reset and
disabling the account or its membership each request the cancel of the session's turn
`session_ended`, with its notification, while another account's turn runs on, as does
the account's own turn asked under another of its sign-in sessions; before 0087 applies,
it refuses a change to each definition it replaces or helper it relies on, or to their
privileges. The managed checks:
- refuse a host configuration without either key file, naming both, and one with a
  relative key file, one among the served assets or one file as both keys;
- bind an application session to its sign-in session's digest and end;
- refuse a stream `session_ended` once its read finds the sign-in session revoked;
- end a started live market stream quietly in that case;
- end a turn whose record write found its sign-in session ended as that end does, by
  recording it first.

`tests/test_provision_hosted.py` checks that provisioning writes both key files, 32
bytes each and owner-only, never replaces one, and leaves none behind when writing
fails. `tests/test_release_deployment.py` checks that a deployment verifies the egress
fence: an unchanged persisted file, the recorded rules live, and the ingress network's
own bridge, refusing a network recreated with another. It also refuses a serving API
that names a provider check endpoint. `tools/check_release_docker.py` starts the
previous image with the host configuration it accepts (`--previous-credential-keys`)
and adds the key files' fields before the deployment, as an operator does. `tools/check_public_routes.py` checks that the gateway passes an event
stream's first frame through before the application ends the response; a copy of the
template with buffering on fails it.

The Messages conformance suite also checks that the adapter's `Validate` refuses
everything `Stream` refuses before sending, and that the provider endpoint override is
only a loopback IP-literal origin.

Settings › AI has its own browser scenario, which runs after those HTTP checks in the
same isolated preview (`--assistant-check --browser-check`). From the page's own
session it creates an administrator without market-data access and a pricing
administrator, then enters every choice explicitly: a blank rate, tier choice or end
is refused before any request, a tier offers exactly the base set's rates, and cache
read cannot be left unpriced where the directory requires it. Repeated currencies are
told apart by catalog and revision. A provider's card records terms only with every
source and attests only once its statement is confirmed; the statement names the key
version, the key's last four characters and the terms version it binds, a new key
clears a confirmation given for the old one, and an attested pair offers no second
attestation and says why. The card enables and disables the qualified provider. A model's card
offers the reasoning efforts its directory states, which a stubbed directory changes,
or explains that no mapping is confirmed; Enable refuses a model without capabilities
and keeps no request, capabilities name the other served identifiers or that the vendor
reports only the model's own, and the recorded version shows each fact with its source
and date before Enable and Disable, after which focus moves to the control now
offered, or stays on the one used when the change is refused. A display name past the
32 KiB request limit, and one holding a lone surrogate, which the server cannot read,
are refused with the ranges every entry keeps; the page keeps no request and the name
is unchanged. After a lost response to a committed price, key replacement or provider
Enable, the page keeps the request through an identity switch and shows what was
saved; Retry resends it under the same identity and the audit still records one
change. The provider's card and table name the check endpoint, and a vendor without an
adapter has none. The test offers only the provider's models with capabilities and the
chosen model's efforts, names its reasoning effort before it is sent and sends only its
identity, model and effort. The card's last test, which a later test by another
administrator would replace, then shows its outcome, usage by dimension and exact cost.
After a lost response to a test at another effort, the page keeps the request while the
card shows the test the server recorded, and Retry reads that same test. A test the
server refuses, here for an effort a stubbed directory offers, keeps no request. A test
still in progress, and a refusal the server can send after a test ran, keep the request
for its own identity only: the page offers it after an identity switch, before any card
opens, and after a failed directory read, and Discard forgets it without sending it. The
last test is unmarked while its key and terms are attested; a new key marks it as run
under an earlier key or terms, and the card says to attest before testing again. The
launcher then confirms that the fake provider was reached only by these two tests, the
Usage scenario's one and once for each answer the panel's scenario scripts. The key never reaches
session storage, local storage or the URL, its field clears when the request settles,
and Retry asks for it again. Measured against the client width at 390 px, neither tab overflows the page. A revoked administrator keeps
no model, price, capability, terms, test or audit rows on screen, a viewer sees no AI
settings, and hosted mode hides them.

An Agents scenario follows in the same run. Through an independent session it qualifies
and enables the provider and adds a priced, enabled model, so it relies on no earlier
state; the launcher adds a role holding each of Settings › AI's three permissions alone.
A trader is offered no AI settings. An identity holding only `ManageAgents` lands on
Settings › AI, is offered the Agents tab alone and reads only the agents directory; one
holding only `ManageModelProviders` is offered Models & prices and Providers alone; an
access administrator is offered all four and opens on Models & prices. The tab says
what a save, Publish and Retire do. A refused create keeps no request; Create lists the agent with
nothing saved or published, sending only its identity and name, and opens its card. The
card offers exactly the directory's models, each at its current capability version, the
chosen model version's efforts, chosen anew, and its output limit, the roles that use
agents and every tool, and states the bounds the API passes. A refresh keeps what the
card has entered and chosen, and so does the next refresh after one that failed. A save
without a caching choice is refused before any request, and an allowed role lacking a
permission the tools need is refused by name and keeps nothing. Tool steps of
99999999999, which the server cannot read, and instructions past the 32 KiB request
limit are refused with the ranges the entries keep; the page keeps no request and
nothing is saved. A save creates a draft
and sends every field with the model version the directory listed. A chosen role that a
stubbed directory no longer lists as using agents is named rather than offered, after a
refresh as when its agent opens, and is offered still chosen once listed again; Publish
publishes the draft. After a capability edit the agent is marked "model changed" until a
version on the current model version is published, and the form, refreshed or reopened,
leaves unchosen a model chosen at the earlier capability version and says why. A lost
save response keeps the exact request for its own identity only; a refusal after that
uncertain attempt keeps it, and Retry reads the stored version, recorded once. A save
confirmed before its reload failed keeps no request, and the next read fills the form
from the version it made without calling that version newer. A form filled before
another administrator saved a newer version says so, and its save is refused before any
request until it starts again from the newest version; Publish keeps what the form holds
unsaved. A Retire whose answer never came is kept until Discard, which sends nothing. A
Retire confirmation never carries over to another agent's card; Retire needs a
confirmation that it is permanent, after which the agent offers no change. At 390 px the
tab and card fit the page, and no internal identifier shows. A change refused on its
first attempt because access ended keeps no request, and the administrator keeps no
agent, version, audit or form data, whether the next change or the next read is refused.

A grants scenario follows in the Agents scenario's browser and with its pricing session,
so the run opens no session for it. The page creates a trader and publishes an agent for
them. Profile reads the trader's grants only once Agent access is asked for, reading
nothing else, and lists each with its agent and version, model, what it reads and its
term, and a Revoke named for the agent. After a lost answer to a committed Revoke the
page keeps the request for that identity only, shows the grants in force and moves focus
to Retry, which resends the identical request and keeps nothing. A trader demoted to
viewer, who no longer uses agents, still lists and revokes a grant, sending only its
request identity and grant; a read refused while the membership is suspended shows no
grant and keeps no request, and restored the person lists the grant again. Users &
access then lists a grant in force by person, offering no Revoke, and the agent's changes
newest first, the retried Revoke recorded once. Agent access and both cards fit 390 px
and show no internal identifier. In the standard run Users & access shows both cards,
and a refused reload or retry clears them with the accounts; in hosted mode Agent access
and the cards are hidden and no assistant route is read.

A Usage scenario follows. Through an independent session it prices a model and
tests it once, so the report holds a priced provider test of this run in a currency
reference it names; since the preview's other checks also record calls, the page is
compared with the API's own answers, and the SQL checks own the figures. An identity
holding only `ReadModelUsage` lands on Settings › AI, is offered the Usage tab alone and
reads only its directory; the tab states the 366-day limit the API passes and chooses no
grouping or day; an access administrator is offered Usage first among four tabs. The
reporting zone is prefilled from Profile and stays as chosen when Profile changes. A
report without a grouping or a first day is refused before any request; a reversed range
and a zone the database does not name, added to the list for the check, are refused by
the server, and a report a stubbed answer gives an unknown format is refused by name and
not shown. A report sends exactly its zone, range and grouping and states them with its
UTC bounds; it shows the API's currency references in order, each with its rows,
provider tests last with the API's calls, cost of priced calls and calls without a cost,
a summary and a daily chart, and this run's test priced. A reader of market data sees
the currency named from its reference row, and one without sees only its code. Export
sends only its identity, zone, range and grouping, saves the CSV of the report it
recorded under that identity, whose generation instant the audit lists first, with the
export ID, report format, zone, range, grouping, methods and the exact currency
reference of the provider tests row, and keeps no request; a member a stubbed answer
names like a formula shows as text and exports apostrophe-prefixed. An export a stubbed
answer gives an unknown format is refused by name before its request is released, so no
CSV is saved and Retry, still offered, answers with the report that export recorded. A
lost export response keeps the exact request for its own identity only and says what it
would export while the audit shows the export recorded; Retry answers with the recorded
report, listed once. An export whose answer never came is kept until Discard, which
sends nothing. At 390 px the tab and its report fit the page, and no internal identifier
shows. An export refused because access ended keeps no request, and the reader keeps no
usage, form or audit data, as does an administrator whose next read is refused.

The panel's scenario ends the run. Through an independent session it qualifies the
provider, prices and enables a model and publishes two agents a trader may use, and it
scripts every answer through the fake provider's loopback control listener, which can
hold an answer after its first block until released. A viewer, and an editor whose
agents read lists none, see no launcher. The panel opens docked beside the narrowed page
on Choose an agent with Send disabled, names each usable version with its provider and
model, and widens by keyboard, remembering the width; Looking at names the page, what it
shows and its context, working data being askable only at an As of date. Asking without
a grant shows the §7.1 consent with the date the grant would end; Allow grants that
version alone and sends the same question under a new request identity, with exactly the
page report and knowledge Looking at showed. A live page target moves the page with a
notice and Back; step lines are collapsed and name each tool in plain words; the answer
draws emphasis, a list and code from text nodes, a link and markup staying literal; the
usage line gives tokens by dimension and the turn's exact cost; Back restores the page,
what it showed and its As of date. A target pinned to an As of date the page no longer
shows waits for Go and says why, and Go selects that date and opens the curve. A live
target that would leave unsaved booking edits waits and offers Go anyway, which moves,
with Back, and Back from a page with unsaved edits says so first too, offering Back
anyway. On Delivery prices Looking at says the page is not read, and Read current page
is refused `page_not_supported` in plain words. During a turn Send becomes Stop with the
agent and conversation fixed, and Stop marks the answer incomplete. A send lost before it
left keeps the draft with Retry and Discard, also after a reload, and Retry sends the same
identity once; while an ask is in flight History offers no conversation to open. After a
reload mid-turn the panel reopens on its conversation, resumes after exactly the last
event it read and shows the stored answer exactly. Closed mid-turn it keeps its stream;
the answer finishing lights the launcher without a read mark, another tab holding no
stream finds it unread in History, and opening the panel marks it read. Opening the turn
in a second tab replaces the first tab's stream, which offers Resume; resumed, it follows
the turn again, and a target it then receives waits for Go. With six tabs open on one
unfinished turn only the asking tab holds a stream. A target pinned to a historical view
waits in working data; Go selects that view again by its id, at its As of date, and Back
returns to working data. After a reload every target of the conversation waits for Go;
a Go whose page read then fails keeps Go and Back, and Back restores what the page
showed. Another agent starts a new conversation; after a reload the last explicit choice
is preselected, and once its version is no longer listed nothing is. At 390 px the panel
is a full-width sheet that fits the page and shows no internal identifier, and no tab
raises an uncaught error.

The panel's own modules have unit checks (`web/tests/assistant-*.test.mjs`): the stream
accepts only its fixed event names with their closed payloads, and a conversation read
the same events; a turn's view joins text in order, drops a repeated sequence and keeps a
hidden answer hidden; page reports, Looking at, when a target changes context or moves,
Back's places, step, ending and usage lines, the consent sentence with its date across a
daylight-saving change, the picker's labels and preselection, a kept ask read back only in
its exact form, a stream's order with its gaps, and the Markdown subset, whose links,
images and markup stay text and whose lines over 2,000 characters stay text whole.

The Messages adapter has a conformance suite
(`dotnet run --project tests/OpenEtrm.Api.Checks -- --model-providers`, also part of
the managed checks). A fake provider, injected as an `HttpMessageHandler`, asserts
that every request reaches only the documented endpoint with only the version and key
headers, and replays hand-written synthetic recordings:
- text, and two tool requests, one assembled from partial JSON split inside an escape
  and one whose deltas carry nothing;
- a two-step loop whose signature and redacted reasoning echo byte for byte, with a
  block's unnamed field kept, while the cache breakpoint moves to the last tool result;
- reasoning text streamed despite omission, which reaches no event and is never echoed;
- usage:
  - final usage with cache and reasoning, read from `message_start` where the caching
    guide places it and cumulatively across two `message_delta` events;
  - unrecognized when a priced count is reported by no event, falls or is malformed,
    when the last `message_delta` has no usage, when a modifier differs from the one
    requested, or when an unnamed field is not exactly zero;
  - the streaming guide's basic example, which reports no cache counts, is
    unrecognized rather than zero;
- stop reasons: a tool request cut off by the output limit, refusals before output (with
  no content, after reasoning only and after an empty block) and after text;
- errors:
  - the documented error statuses, every documented streamed error type and an unknown
    one;
  - a 400 body that is malformed, invalid UTF-8, holds a lone surrogate or stalls,
    which keeps its classification;
- malformed streams: invalid JSON or UTF-8, escaped lone surrogates, repeated names,
  misplaced or mismatched events, a block after `message_delta`, unknown blocks and
  deltas, a block or tool starting with content, a thinking block without its
  signature, an encoded stream and a success that is not an event stream;
- the byte bound at exactly its value and one byte past it;
- connections never established, lost mid-stream or idle;
- cancellation before sending, between events, during a pending read and during the
  header wait;
- a served model neither requested nor accepted;
- requests refused before sending: missing reasoning, malformed identifiers and keys,
  unset lists, an empty system instruction or text, no tool results, and a reply
  replayed to another model.

Message and request identifiers, reasoning, signatures, error text and the key reach
no event, and nothing is written to the console. No check calls a real provider.

## Hosted job

[The workflow](../.github/workflows/ci.yml) uses `ubuntu-24.04` hosted runners on
pushes, pull requests and manual dispatch. Checkout and tool-setup actions are
pinned to exact commits. The checks job has read-only repository access, does not
persist checkout credentials and requires no repository secrets. It runs the same
`check_all.py` command as local verification, with a 45-minute job deadline for
the complete suite; individual command and owned-resource deadlines remain in
force. The previous 30-minute limit interrupted the final mixed-period suite
after the preceding checks passed. It does not publish packages or
deploy the application. A separate, gated `publish` job runs only after successful
checks on a push to `master` when release publication is explicitly enabled. It
uses the hosted-release environment and scoped write/attestation permissions to
publish the reviewed release. Pull requests do not trigger that publisher.

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
profile deletion. macOS refuses to signal a group whose remaining members are
already exiting; such a group is awaited until it is gone rather than reported as
stuck, and one that outlives the refusal still fails cleanup.

Debugger startup now qualifies the port, HTTP endpoint and page target within
one shared 60-second readiness budget. Read-only probes tolerate transient
startup unavailability; attachment requires the exact owned loopback port.
Fifteen harness checks cover delayed readiness, deadline cancellation, process
exit, rejected foreign targets and confirmed TERM/KILL cleanup. Cleanup failures
retain the original error. The standard 298 browser checks, descendant-profile
check and isolated hosted HTTPS/browser suite passed with this owner. Disposable
resources completed teardown.

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

The retained local preview was updated on its owned stock PostgreSQL 16.14 database.
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
The focused stock PostgreSQL 16.14 authority/identity checks and252 managed core
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


The automation checkpoint on 2026-10-18 passed the full local command on macOS
26.6.2 arm64, .NET 10.0.402, Node 24.22.0, PostgreSQL 16.14 and Chrome
155.0.8059.80. Final targeted checks passed after the request-retry and worker
configuration refinements: 110 frontend checks, nine owned jobs/infrastructure
browser checks, actual worker/controller lifecycle and RLS checks in a fresh
private database, cross-workspace quota races, service-authority expiry during
lock waits, gateway response-body deadlines and exact service/task/image identity.
Worker and existing deployment templates pass render-only validation. Owned
previews and databases completed teardown. This qualifies the bounded local
[automation contract](automation.md), without qualifying real Swarm execution,
production deployment, full agent execution or host reboot/Engine restart.

## Common book valuation and factor discovery checkpoint

On 2026-10-19, 276 managed core checks passed, including an independent Release
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
amendment, swap, schedule and storage checks. The managed API checks and 111 frontend
checks passed. API/web builds completed without errors. No existing database or
production runtime was used. The [Release benchmark](risk-benchmark.md) separately
records 50,000-position workload, unit/scenario counts, memory and measured phase
costs. Service capacity, durable runs and complete futures settlement PV remain
unqualified. The isolated PR candidate preserves the current public reference-data and
scheduler contracts.

## Standard execution policies and source facts

On 2026-10-19, `tools/check_all.py` passed on stock PostgreSQL 16.14, the pinned
.NET/Node toolchains and Chrome. It ran 277 managed core checks, 116 frontend checks,
255 standard browser checks, 25 focused party/power checks, 14 unit/precision
checks, eight exchange checks, eleven job checks and 24 mixed-period HTTP checks,
plus schema, authorization, temporal, automation and hosted-authentication suites.
All database/browser resources were owned and disposable.

The standard futures execution-policy fixture preserves a date-only source fact,
requires its qualified next-local-date offset/tzdb proof and books at the explicit
bound using the declared price source/role. Raw authorized operations reject
missing policies, conflicting sources/roles, early bounds, date-proof bypass,
wrong fixed offsets, stale declared tzdb, repeated/skipped Havana midnights,
conflicting policies under another pricing convention, revoked scopes and
invisible history.
The public booking regression proves authorization precedes reading conflicting
reference economics. Independent Chicago spring/fall oracles retain 05:00/06:00 UTC
boundaries, with no fabricated source event. Eligibility remains outside contract
identity and external lot scaling remains unchanged. An owned 0039-to-0040
upgrade preserves original admissions and native milestone facts, converts the
exact retained source/role/bound, and rejects ambiguous price-source mappings.

The revised exchange example has 19 real product definitions and 72 source-confirmed
monthly maturities. Only 61 have qualified cutoff instants and executable market
rows; eleven year-end cutoffs stay unknown. Daily product terms are retained but
no daily maturities are inferred from listing cycles. Generated prices use the
separate synthetic source and `trader_mark` role. Invented product/venue controls
remain outside the exchange catalog. Ordinary-session time qualification and
bounded calendar coverage are described in [the example](exchange-futures.md).
This supersedes the earlier 113-contract example, which included an invented unit
control and inferred daily rows.

Job entry resolves the selected user clock through the managed resolver; a Tokyo
entry yields the correct UTC request, and switching accounts refreshes its zone
label. Stored instants and elapsed-interval recurrence remain UTC. Literal
first-trading/booking/delivery dates are not shifted or labeled as user-zone
instants. Explicit contractual clock inputs retain their authored convention.

Independent adversarial code review found no remaining concrete blocker after
these regressions were resolved. It did not independently re-fetch exchange
source records. Migration 0040 and existing-installation reconciliation still need
a qualified release; these local checks did not update a hosted database or
establish full futures lifecycle support. Contract-sequence shortcuts retain
the current eligibility-based selection semantics under the selected generic
policy; the date-only fixture reconciles their bound with the trading catalog,
and missing qualification rejects the sequence.

## Reference-read authority and startup projection

The focused 2026-10-19 checks first reproduced the duplicate-policy shape and
startup access round trip, then passed with migration 0041 and the optional
bootstrap access projection. Twenty-nine actual-role database checks cover
current and historical reference visibility, token expiry under custom and
generic plans, agent/delegator grant and delegation expiry, scope revocation,
pooled connections, permissive-policy composition and locking/update reads.
Catalog-only access and the separate imported-quantity restriction remain.

The startup checks cover legacy response compatibility, fresh projected
permissions, malformed or mismatched identity metadata, revoked business access
and later identity refresh. An owned HTTPS preview also verifies signed-in and
administration-only startup, session isolation, logout and revocation. All
resources are disposable and synthetic. These checks establish authorization
contracts; hosted latency and retained-installation migration qualification are
separate release checks.

## Consumer-scoped navigation and empty directories

Focused checks first reproduced the missing navigation operation and the empty
directory's unnecessary dependency on economic contract reads. The distinct
navigation contract now matches authorized full-workspace names,
classifications, frame summaries and exact working/retained revision/context
identities. A hidden member rejects partial classification; reference revocation
and administration-only access cannot produce a successful market directory.
Owned browser checks cover initial readiness without background full hydration,
first pricing/reference use, no-date selection, delayed hydration after a changed
selection, preference refresh and reset before navigation completes.
The optional first-pricing projection preserves default evaluation responses and
compares complete catalog metadata under working and retained contexts. Delayed
or inconsistent metadata/source responses cannot publish a partial pricing view.

Empty-directory checks compare complete legacy trades/pivot reports and booking
metadata under the same retained context. They reject missing selected pins,
withdrawn reference/trading authority and invalid views/books. Real synthetic
futures-only and swaps-only holdings retain complete valuation reads; failed
report joins cannot establish emptiness. These checks use disposable resources
and synthetic material. They establish scoped read contracts; larger-workload,
cold-process and hosted latency qualification remain separate measurements.

## Plan-independent page reads

Definition, reference and unit-catalog reads had no `ORDER BY`. Without
statistics PostgreSQL read them through index scans, often on unique keys other
than the primary key; after autovacuum's first `ANALYZE`, about a minute into a
disposable cluster, it switched to heap scans. A page recorded before that pass
and its later direct read then returned the same rows in another order. The
direct-read check now repeats every direct read with index scans disabled and
with sequential and bitmap scans disabled. Against the unordered reads it failed:
six definition, thirteen reference and six unit-catalog tables differed only in
row order. An explicit `ANALYZE` between the recorded pages and the direct reads
also reproduced the original failure.

Each read now orders its rows by the table's primary key within its revision;
both passes and the `ANALYZE` reproduction match their pages. The recorded
reference page links units, commodities and measurement bases, so it also fails
when a linked read is unordered. The power catalog, which inherited contract
order, now lists each curve's contracts by delivery date. Its check over the
exchange example's name-based contract identities failed first. The complete
command passed on 2026-10-19 on macOS 26.6.2 arm64 with .NET 10.0.402, Node
24.22.0, stock PostgreSQL 16.14 and Chrome for Testing 153.0.8010.12, including
330 standard and 8 exchange browser checks. Disposable resources completed
teardown. Swap-catalog daily months still follow contract-identity order, and
the power-import lists, automation directories and tied discount defaults depend
on incidental order or ties; they need separate checks.

A failed direct-read comparison now names the fields that differ and a bounded
excerpt of its first difference, instead of reporting only that the direct read
differs from the page. With the definition reads unordered again, the first pass
reports `definitions` and the first differing `curve_inputs` row; a database-free
check covers the report. The complete command passed on 2026-10-20 on Ubuntu
24.04.5 x86_64 with .NET 10.0.402, Node 24.22.0, stock PostgreSQL 16.15, Python
3.13.16 and Chromium 141.0.7390.37 with its normal sandbox, including 162 frontend
tests, 623 standard and 24 mixed HTTP checks and 360 standard-preview browser checks.

## Explicit index fixings for swaps

Local 2026-10-19 checks cover exact monthly publication schedules and partially
fixed daily gas/power periods. Independent arithmetic verifies actual/projected
PV, unchanged averaging weights and quote risk only for unresolved observations.
An actual present in storage but absent from the selected snapshot fails rather
than being fetched implicitly. Manual boundary overrides reject. Identical booking
retries retain their original outcome after an actual correction; changed terms
conflict and current write authority remains required.

The owned 0049-to-0050 upgrade preserves existing financial, reference and market
rows, certified history, definition identities and exact legacy retries. It adds
no guessed fixing rules to sealed bundles. Runtime checks cover forced RLS,
additional permissive policies, independently revoked market/reference access,
unbound sessions and authority expiry during a blocked write. Rejected writes
leave no trade or publication.

After integrating migrations 0046–0049, the complete local verification command
passed, including 298 managed market-data checks, 148 frontend checks, 353
standard browser checks and 37 swap storage checks. The Release API build passed
without warnings. Verification used
.NET SDK 10.0.402, Node 24.22.0, PostgreSQL 16.14 and Chrome 155 on macOS arm64;
all database/browser resources were owned, disposable and synthetic. Independent
adversarial review found no remaining concrete blocker. These checks do not
qualify live publisher data, broader peak/holiday observation authoring,
settlement/payment lifecycle operations or a hosted deployment.

### Acceptance oracle, wrong actuals and exact snapshot pins

Local 2026-10-20 checks extend `--index-fixing-check` with check-owned synthetic
frames. A one-month monthly-index swap on 10,000 MMBtu (IT) receives a published
3.20 USD/MMBtu February index and pays 3.00. It values at 2,000 USD undiscounted
before fees, with unit gross sides of 3.20 and 3.00 and lineage to the pinned
actual. On its payment date it rejects with `side_payment_due`. The discount
context now checks every side for a due payment before any discount lookup.
Before this, the API path reported `discount_context` even for undiscounted swaps,
and another side's missing discount curve could hide a due payment. Managed rate
checks cover valuation, discount creation and direct construction.

For a due January actual, five inputs cannot price or book: another source, a
trader mark, a preliminary value, no actual, or only February's actual. The
database writer rejects the same pin sets independently and admits the exact
January actual. The February projection needs a live future that expires on
January 28, before the index publishes on January 31. Between the two, price and
booking reject with `source_expired`, and at publication the pinned actual prices.

Exact snapshot pins (D172) are checked in both the main and the separate daily
swap bundle:

- A correction or explicit revision selection prices only through its own new
  snapshot, and that snapshot's frame JSON names its separate-bundle selection.
- The original snapshot, a pre-correction retained context and a certified
  context before the correction keep their original actuals. A certified context
  after the correction resolves the latest correction in both bundles, repeatably
  and read-only.
- Identical booking retries return their original results, including a retry
  through the corrected snapshot. The trade booked before the corrections values
  at 31, 32 and 33 USD at the original, corrected and selected snapshots,
  identically through the scoped and full trading reads. A new booking from the
  corrected snapshot stores the corrected actual and revalues at 32 and 31 USD at
  the corrected and original snapshots.
- Corrections need current import authority and refuse observations the snapshot
  does not pin. A close cannot be registered from a snapshot with separate-bundle
  selections.

Temporary mutations confirmed that the checks fail if the resolver ignores the
actual's source, if daily-bundle loading follows corrections again, or if a
correction no longer has to be pinned by its snapshot.

The supplemental pin-read check in `check_trading_pin_reads.py` previously required
the same snapshot to pick up a later daily-bundle correction. It now asserts D172:
the same snapshot keeps the held daily swap's PV, and a certified context after the
correction resolves it identically through the scoped and full trading reads.

Independent adversarial review found no critical defects. Its findings were
resolved before this record: decision and documentation wording, retry and
booking coverage, negative authorization checks, an explicit certified-context
marker, serialized selections and the cross-side due-payment order. A follow-up
review confirmed the resolutions.

After merging master, including D171's collected-interval delivery value, the
complete `tools/check_all.py` command passed on 2026-10-20 in all 39 stages,
including 343 managed core checks, 75 rate checks, 162 frontend tests, 360
standard browser checks, 41 swap storage checks and 623 preview HTTP checks. Verification used .NET SDK 10.0.402, Node 24.22.0, PostgreSQL 16.14 and
Chrome 155 on macOS arm64; all database and browser resources were owned,
disposable and synthetic. These checks do not qualify live publisher data,
settlement or payment lifecycle operations, or a hosted deployment.

## Unambiguous swap discount defaults

Focused checks on 2026-10-19 first reproduced the plan-dependent tie in the
currency discount-default lookup. One publication accepted two mappings for the
same definition revision, currency and instant, an upgrade over a retained tie
succeeded silently, the lookup returned one of the tied mappings, and an edited
lookup body could be replaced without review. With migration 0052 the tied write
fails on `currency_discount_default_key`, a later publication still replaces a
mapping at the same instant and a certified earlier context keeps its original
mapping. With the key removed inside a rolled-back transaction, the lookup raises
`swap_discount_ambiguous`, and a later single mapping resolves the tie. Over
retained synthetic fixtures the upgrade preserves every mapping and the lookup's
owner, grants and security settings; it stops on a retained tie or an unexpected
lookup definition.

The complete synthetic verification command passed with stock PostgreSQL 16.14,
.NET 10.0.402, Node 24.22.0 and Chrome for Testing 153 on macOS 26.6.2 arm64.
Independent adversarial review found no blocker. It also exercised the guard in
bound historical contexts against a committed tie in a separate disposable
cluster. All resources were disposable and synthetic. Hosted installations still
need operator baseline qualification before applying this migration (D135).

## Phone-width overflow checks

Ten owned browser checks assert that a page has no page-wide horizontal overflow
on a 390 × 844 phone with mobile emulation. Nine of them compared
`document.documentElement.scrollWidth` with `window.innerWidth`, which mobile
emulation widened to the content width when content overflowed the
`width=device-width` layout viewport. With 900 px of content at 390 px both read
900, and on a Settings › AI price form whose rate table overflowed both read
571, while the root element's `clientWidth` stayed 390, so the width comparison
passed on those overflowing pages. The Settings › AI checks have since compared
with the client width, and that rate table now scrolls within its form. All ten
checks now share one expression, `fitsWidth`, that compares `scrollWidth` with
the root element's `clientWidth`, the layout viewport; the calendar check also
bounds its dialog by that width.

Corrected, the eight checks outside Settings › AI passed on the existing pages.
Disposable runs then widened `main` to 900 px before each of the ten checks. All
ten failed while the previous `innerWidth` comparison passed on the same page,
and each passed once the widening was undone. That temporary code was removed.
The complete command then passed on 2026-10-20 on macOS 26.6.2 arm64 with .NET
10.0.402, Node 24.22.0, stock PostgreSQL 16.14 and Chrome 155.0.8059.85,
including 162 frontend checks and 360 standard, 57 AI-settings, 43 custom-curve
and 22 calendar browser checks. Disposable resources completed teardown.

These checks establish that each checked page state keeps its scrollable content
within a 390 px layout viewport in Chrome, to within one pixel. Fixed-position
content such as an open dialog does not count toward that width; only the
calendar check bounds its dialog separately. Content inside a scroll container,
such as the Settings › AI rate table, or clipped by an ancestor is not page
overflow. Other widths, page states and browsers remain unchecked; screenshots
remain the evidence of how a narrow layout looks.

## Browser checks after navigation

CDP `Page.reload` returns before the reloaded document commits, and
`Page.navigate` can return while the new document is still being parsed. A wait
after either must hold only for the new document, once the elements its next
step uses are parsed, and a check of an injected startup failure must establish
that the injection caused it. Three checks fell short, although none failed in
ordinary runs:

- The Blotter check that clears browser preferences reloads the page and waited
  for the Trader label on an idle page, which the page before the reload already
  showed. It now also waits for a `performance.timeOrigin` different from the
  one read before `Page.reload`.
- Hosted sign-in waited only for the `#signin` form, after loading `/auth` and
  after each redirect back to it, and then filled the form's login and password
  fields. It now waits for both fields. The administration sign-in wait compares
  `#admin-page`'s `hidden` with `false`, so a missing element no longer counts
  as shown; that wait could not pass early before, because the page is not idle
  until parsing completes.
- The check that a missing stored-name directory fails initial hydration
  accepted any startup failure. Its injected script now records that it removed
  `curveNames` from a successful `/api/workspace/navigation` response, and the
  check requires that record.

Disposable runs on 2026-10-20 injected one fault at a time through the CDP Fetch
domain into the checks before and after the change. With the reload's document
answered 204, so that no new document committed, the previous wait passed on
the earlier page within 6 ms of `Page.reload` and all 361 standard browser
checks passed; the new wait timed out. With that document held for 1.5 s
instead, `Page.reload` was acknowledged within 2 ms and Chrome answered the
wait's first evaluation only after the commit, so both waits passed; the
previous wait depended on that ordering, which the protocol does not document.
A parser-blocking script held for 1.5 s right after the sign-in form's token
field stopped the previous hosted check with `TypeError: Cannot set properties
of null (setting 'value')`; the new waits held at all three sign-ins and the
hosted check passed. Answering `/api/examples` with 503, or
`/api/workspace/navigation` with 500, during the missing-directory startup let
the previous check pass; the new check failed. The fault injector wrapped the
checks' CDP connection outside the repository.

With 14 CPU-bound processes competing on 14 cores, five standard previews passed
their 361 browser checks, taking 396–453 s each instead of about 160 s, and ten
hosted previews passed their 14 browser checks. The complete command then passed
on 2026-10-20 on macOS 26.6.2 arm64 with .NET 10.0.402, Node 24.22.0, stock
PostgreSQL 16.14 and Chrome 155.0.8059.85, including 170 frontend checks and 361
standard and 14 hosted browser checks. Disposable resources completed teardown.

These checks establish that the changed waits accept only the page state their
next steps use, and that unrelated startup failures fail the missing-directory
check, under the injected timings in Chrome 155. Each fault held one parser
position or answered one request; other positions, delays and browsers remain
unchecked, and waits in other checks were not fault-tested.

## Audited interval series

Phase A of the PPA design (D173) is checked in five layers, all on synthetic data in
owned disposable resources:

```sh
dotnet run --project tests/OpenEtrm.MarketData.Checks
npm test --prefix web
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin --dotnet /path/to/dotnet \
  --check --interval-series-check --browser-check --browser /path/to/browser
```

The managed checks expand segments onto clock grids, including repeated and skipped
hours and a half-hour clock change, and refuse misaligned, overlapping and
out-of-range starts. The frontend checks cover the CSV layout, which refusals release
an outstanding request, and reuse of instant-label formatters. In the owned preview,
20 storage checks exercise the definer operations directly (specifications of each
kind's dimension, viewer and reference-less refusal, correction and withdrawal, one
context per cutoff, change log, inclusive cutoff, stale head, overlaps at both
boundaries and on withdrawn-only intervals, replay, one bad row, validation under a
held knowledge gate, reason and window bounds, window ends inside stored or withdrawn
intervals and in gaps, commit-derived knowledge time, no direct writes, no rows in a
historical session), 12 HTTP checks exercise the routes as anonymous, viewer and
editor sessions (refusals, retries, fall-back day, read windows that cut a stored
interval, historical-session refusal, body limits, change log, reads and a refused
revision on a clock pinned to another tzdb release) and 38 browser checks drive the
screen: choosing an interval by click and by keyboard, Correct and Withdraw from the
action bar, the largest window and the next actions on it, each required to finish
within 5 s. `tools/check_all.py` runs all five layers.

The commands above passed on 2026-10-21 on macOS 26.6.2 arm64 with .NET
10.0.402, Node 24.22.0, stock PostgreSQL 16.14 and Chrome 156.0.8078.47: 354
managed market-data checks, 176 frontend checks, and the 20 storage, 12 HTTP and 38
browser checks above, the preview command in three consecutive runs. The owned
database and browser completed teardown in each.

The 20-year workloads are a measurement, not a pass/fail suite, and are not part of
`tools/check_all.py`:

```sh
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin --dotnet /path/to/dotnet \
  --interval-series-benchmark
```

It prints one JSON report among the launcher's log lines and exits nonzero if any step
is refused, times out or returns a wrong interval count. Its workloads, environment and results are in
[the implementation record](implementation.md#twenty-year-qualification). They
establish one editor session's timings on one machine and do not qualify concurrent
use, a hosted network or the API's Release build.

## Interval prices, curves and block shaping

Phase B of the PPA design (D174) is checked in five layers, all on synthetic data in
owned disposable resources:

```sh
dotnet run --project tests/OpenEtrm.MarketData.Checks
npm test --prefix web
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin --dotnet /path/to/dotnet \
  --interval-curves-check --browser-check --browser /path/to/browser
```

35 managed checks cover interval contracts (fall-back labels, misaligned and
overlapping intervals, pinned dates and expiry, no mixed constituents, mapping only
identical intervals, windows with gaps), blocks (304 November peak hours with the
Thursday holiday off-peak; peak and off-peak cover all 721 November and 743 March
hours) and shaping: oracles 1–4, unequal durations giving 48 and 58, every block's
exact mean equal to its mark, partition and coverage refusals, a cut window equal to
the whole month, and per-interval shape revisions. Under a +1 block-mark shock, one
prepared window recalibrates one block and reuses three; a shape or mark correction
recalibrates only its block. They also cover PWR01 at the interval-price level (value
0, +10 under the shock with the fixed interval unchanged, unresolved before the due
time, refused once due, a day-ahead actual, no actual published after the valuation
instant for a live interval, a correction only at a later cutoff), whole-hour and
whole-day aggregates and the uncovered PWR02 quantities. Deliberate faults in reuse
matching, partition overlap and the knowledge cutoff each failed their checks. The
frontend adds 4 presentation tests: labels for the repeated hour, lineage text
without internal identifiers, aggregates and the dense chart. In the owned preview,
23 storage checks cover the sealed bundle, read authority and a historical context,
immutability, and a refused draft copy for each seal rule a synthetic bundle can
violate; two rules have no such copy (see the
[implementation record](implementation.md#interval-prices-curves-and-block-shaping)). 37 HTTP checks
cover exact prices and lineage for marked, shaped and resolved reads on a frame that
values at 16:00 UTC on 15 December 2020: block means equal to their marks, a weekday
holiday's peak-window hours in the off-peak block, a knowledge time that keeps its
shape, mixed shape revisions, a withdrawn and restored interval, a curve listed as
unavailable, and actuals published through the collection worker, where the
delivered day resolves to its actuals, a due actual not yet collected is refused,
an ended hour before its due time is unresolved, live hours take their marks even
after their actuals arrive, and a correction appears only at a later cutoff. They
also cover aggregates, delivered, uncovered, window, cutoff, historical, missing-member
and anonymous refusals. Reintroducing the dropped holidays or the hindsight actual
each failed its HTTP check. 21 browser checks drive the screen: an unavailable curve
with its reason, shaped November with the repeated hour's offsets, lineage on choosing
a row, the delivered-interval message, actual, unresolved and projected states, day
and hour aggregates, the largest synthetic window (1,465 hours), 390 px width and the
historical refusal. The market read authority check pins sixteen read policies, and
`--power-actual-check` still passes over the shared collector and alignment.
Both interval screens compare Known at with the observed catalog cutoff in the
profile zone using the control's numeric wall time. Chrome may omit zero seconds
from `datetime-local` text. A deterministic browser regression covers whole-minute
and nonzero-second displays, exact fractional-cutoff retention, and rejection of
an empty value, a wrong zone or missing nonzero seconds. Both owned interval
suites pass with these assertions (21 curve and 38 series browser checks).
`tools/check_all.py` runs all five layers.
