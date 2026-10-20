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

The standard and mixed previews use different disposable clusters. The browser
runner uses fresh temporary profiles and accepts only the launcher's verified
owned target. Normal completion removes the owned application, browser and database
resources. Unconfirmed teardown retains the private directory and fails the check.

Custom-curve qualification on 2026-10-19 passed the complete local command on
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
nothing. Even the table owner cannot store one, also from a session with
`standard_conforming_strings` off, where both rules still accept ordinary text. Add
model keeps a name with letters beyond ASCII exactly. Before 0059 applies, its body is
refused after the display-name rule's definition changes, after the terms rule's
privileges change and under repeatable read isolation, and it recognizes both rules
under another caller search path. Over stored display names that follow the rule,
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
loopback fake provider through the preview's provider endpoint override, and every proxy
variable names a trap:

- **Worker:** before serving, the API takes the agent-worker lock and starts a generation,
  and tests run under it. A request that gives up while the worker's connection is slow to
  answer leaves the worker usable.
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
    that test.
- **Expired context:** three cases let the request context expire while a test waits for
  its principal, its model row or its record write, and each names the recheck that
  refuses it.
- **Guard:** before 0062 applies, its body is refused after a pricing helper's definition
  changes and after a qualification helper's privileges change.

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
offered, or stays on the one used when the change is refused. After a lost response to a committed price, key replacement or provider
Enable, the page keeps the request through an identity switch and shows what was
saved; Retry resends it under the same identity and the audit still records one
change. The key never reaches session storage, local storage or the URL, its field
clears when the request settles, and Retry asks for it again. Measured against the
client width at 390 px, neither tab overflows the page. A revoked administrator keeps
no model, price, capability, terms or audit rows on screen, a viewer sees no AI
settings, and hosted mode hides them.

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
