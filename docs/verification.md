# Local verification and continuous integration

## Transport capacity navigation after Scheduling views (D461)

The physical CI group failed because its Transport capacity browser scenario
opened Storage & allocations and assumed Capacity was the initial section.
The scenario now opens Nominations & transport and selects Capacity explicitly.
Its access assertion also checks that the Capacity workspace is visible and
the corresponding sidebar entry is active. Application behavior and permission
checks are unchanged.

After that failure, the corrected scenario passes all 65 browser checks and
its native database postconditions. The remaining Transport route scenario
passes all 55 browser checks and its native postconditions; the delivery
workbench HTTP and browser checks also pass. These local runs used the pinned
Node 24.22.0, Chrome for Testing 158, Python 3.13.18, .NET SDK 10.0.402 and
PostgreSQL 18.7 on arm64 macOS, with separate disposable synthetic databases.

## Scheduling roadmap and desk controls (D462, local)

Run on 27 November 2026 on arm64 macOS with the pinned Node 24.22.0,
Chrome for Testing 158, Python 3.13.18, .NET SDK 10.0.402 and PostgreSQL 18.7.
The application check used this tree's API and assets with an owned disposable
database and synthetic data.

- `npm run build --prefix web` passes.
- `node --test --test-concurrency=1` passes 108 focused checks, with no skips:
  navigation groups, Scheduling desks, Facilities views and workspace,
  Physical operations and grid, tab navigation, account refresh, assistant
  walkthroughs, theme, workspace layout, roadmap and desk layout. The new
  roadmap/layout files are `scheduling-roadmap-browser.test.mjs` and
  `scheduling-layout-browser.test.mjs` under `web/tests`.
- The checked-in `tests/browser/scheduling-roadmap.mjs` scenario passes all
  eleven checks against the running isolated preview. It covers all four
  desks, the limited trade-reader view, hiding the roadmap outside Scheduling,
  opening the roadmap without changing the operating page, and phone menu
  dismissal for both page changes and Facilities section changes. The scenario
  is also called by `physical-workspace.mjs`; the full enclosing suite was not
  run for this increment.
- Browser geometry checks pass at widths 1280, 1000 and 390. Visual inspection
  of the 1280 and 390 views and the collapsed desktop sidebar confirms that
  desk controls fit one row, Scheduling labels remain readable, and the
  roadmap cards stack within the phone viewport.

The layout and roadmap checks failed before implementation. Independent review
identified an assistant presentation target being displaced by the roadmap and
stale roadmap visibility while new access was pending. Failing regressions
preceded both fixes. Phone checks also reproduced a menu left open by desk
navigation. Review of that fix found event-order interactions with controls
becoming busy and a leave prompt receiving focus. A regression using the real
leave prompt verifies Keep editing focus and Escape cancellation. The menu now
closes during capture, before the destination handles the click; existing leave
and pending-command decisions remain in the destination. Final independent
review has no remaining findings in this increment.

This verifies presentation and navigation only. It does not implement or qualify
the workflows marked TBD. The complete frontend and `tools/check_all.py` suites,
new commodity operations and hosted deployment were not run for this increment.

## Scheduling desks (D461, local)

Run on 27 November 2026 on arm64 macOS with .NET SDK 10.0.402, PostgreSQL 18.7,
Python 3.13, Node 24.22.3 and Chrome for Testing 158 as the browser. Node is a
patch ahead of the pinned 24.22.0. The complete `tools/check_all.py` was not
run.

- `npm test` in `web`, with browser tests: 2,438 of 2,448 pass. The ten
  failures are the typed day quantity tests of `deal-ticket-browser.test.mjs`.
  They fail identically on the commit before this change with this browser and
  touch no changed code.
- `tools/run_preview.py --check --gas-operations-check --browser-check`: the
  native checks pass, including the directory by desk; 29 manual gas delivery
  and 20 physical gas browser checks pass. The gas scenario switches desks,
  checks each desk's entries, title and eyebrow, and reads the `desk` each
  directory request sends.
- `--crude-lifecycle-check --browser-check`: 50 physical crude browser checks
  pass, opening Trade deliveries on the Crude desk.
- `--structured-power-check --browser-check`: 16 structured-power and 19
  structured-trading browser checks pass. This run preceded the review fixes
  below and was not repeated after them. The frontend, gas and crude runs above
  are from after every review below.
- `tools/check_delivery_workbench_http.py` with the browser passes, including
  the crude delivery link's handoff to the exact trade.

An independent static review of the change examined access, leave guards,
state and the backend read. It found no account able to reach more or less than
before. It found, and this change fixes with a failing test first: a desk or
view chosen before a refused leave being applied by a later navigation; an
unconfirmed facility definition reopening on Physical activity; walkthrough
steps failing to find an entry of another desk; reload moving to another desk;
and the title of a hidden page being rewritten after access ends. The section
permission rules now have one definition used by the tabs and the sidebar.

A second review of the merged branch reproduced three defects in a browser,
each fixed here with a failing test first: an unconfirmed physical request did
not keep Trade deliveries on its desk; an account reading trades alone was sent
from Storage & allocations to the Crude desk; and a second desk chosen while a
leave question was open cost the first choice its desk. The second was also
confirmed in the running application as the preview's trader profile.

A third review found the pending-request hold too wide and too late, fixed the
same way: a desk other than the request's now opens on an entry of its own that
is not Trade deliveries, and an exact delivery link to another desk's trade is
refused with the reason before its page is left. The first was confirmed in the
running application with a synthetic retained gas request: from Power, Crude
opens on Receipts & dispatches and LNG is refused with the reason.

Not verified here: a person's check of the layout at phone width and with the
sidebar collapsed, where the switcher is not drawn; the remaining browser and
database suites; hosted deployment.

## Gas trade details and Blotter updates in the swing browser checks

On 26 and 27 November 2026 (UTC) the gas swing browser checks failed in four
hosted runs. Three timed out in a ten-second wait for the page to be idle, at
three different steps; the fourth timed out at one of those steps in the
60-second wait for the open trade's election days. Whenever a trade or an
election is recorded, the Blotter finds a new version, values its book again
and reads the open trade's details again. The page is busy throughout,
disables the controls it draws, and then draws the open trade's ticket again.
The checks went on as soon as an election's row was drawn.

The cause was measured on the swing preview's own fixture at its browser step:
41 physical gas trades and no two-location booking at the 18 December 2020
close, 42 trades and 44 revisions once the checks have booked theirs. A Debug
API and stock PostgreSQL 18.7 ran in a Linux container limited to four
processors; Chrome 158 drove the page from another machine with every request
and the page's busy state recorded; PostgreSQL logged statements, lock waits
and the executed plan of slow statements. Each read is one base valuation
without a scenario. Memory was not measured. These are measurements of one
synthetic fixture on one machine, not a throughput claim.

| Read on an otherwise idle host, three times each | Before | After |
| --- | ---: | ---: |
| One gas trade's details (`trading/gas`) | 2.33 s | 0.14–0.22 s |
| Blotter values for every book (`trading/workspace`) | 6.98–7.07 s | 4.92–5.29 s |

- The details read spent nearly all its time in one statement, which selected
  the trade's two-location booking by the trade of either confirmation. Its
  executed plan paired the 44 revisions with the 42 trade references and looked
  a revision up under row security for each pair: 1,848 index scans and 2.30 s,
  with no booking to find. The statement now starts from the selected trade's
  own revisions and finds a booking by either of its operations.
- One Blotter read ran about 1,290 statements. For each composed gas position
  it repeated the authorized position read it had already made for all of
  them; a composition now loads from the position its caller read, and the
  read runs about 820. The same repeated read is gone from the held portfolio,
  a Side schedule, the booked ticket and the preparation of a swing election.
- No statement waited for a lock. The cancelled query (SQLSTATE 57014) in the
  failed runs' diagnostics is a read the page abandoned when the checks moved
  on: 22 requests ended that way in a run that passed.

With the host busy with other work, the same container took 11 to 15 s for a
Blotter read before the change, twice cancelled at the 15-second request limit,
and 3 to 7 s for the details, so one update kept the page busy for 16 to 22 s.
The gaps between the same steps in the hosted runs put an update there at 10 to
20 s, where a step took about 2.5 s without one. An update starts within a
quarter of a second of the ticket's redraw, so whether the next click met it
was a matter of milliseconds: at that speed the previous checks also passed
once, in 169 s.

Holding the checks' next click until the page was busy made that order
certain. The previous checks then failed as in one of the hosted runs, with
the ten-second timeout while restoring the dock after the recovered range. The
Blotter asks for changes to a version only after its update to that version
has ended, so the changed checks read the version the server has after each
recorded election and wait until the page asks about it with its values
complete, then for the ticket to be drawn again. With the same hold armed they
passed all 28, against the previous API at that speed through seven updates of
13.8 to 16.7 s and against the changed API slowed likewise through seven of
9.3 to 12.1 s, and the hold found no update left to meet. Against the changed
API on the idle host they passed in 100 s, through seven updates of 5.2 to
5.7 s. The whole swing preview then passed 207 printed checkpoints, 28 of them
in the browser, and completed 23 leased jobs.

The Blotter still values gas positions one after another, so its read remains
the larger part of an update and grows with the book.

The gas operations preview now requires that the details of a trade in a
two-location booking read no more gas trade revisions once two unrelated trades
are booked: 7 before and after, where the previous statement read 60 and then
94.

## REC inventory and owned gas access (D460)

The checks were run first without migration 0411. The REC inventory storage
check stopped at its first holder assertion, `every human role that reads
trades reads REC inventory, and no other role does`. The owned
`--structured-power-check` run, with the preview's own grant of the REC
permissions to Trader removed, passed the 112 lines of its existing flow and
stopped when the Trader's REC directory read answered 403 `access_denied`. The
new `--inventory-access-check` run stopped when the demo operator's first owned
gas write was refused with `gas_owned_denied`. `--scheduled-swap-check`, with
the two reads added to the Trade supervisor's pinned list, failed that
assertion after 133 lines.

With the migration and before any check identity changed, seven checks failed.
Each asserts what a person or worker without the owned gas read is refused, and
Risk analyst and Trader now hold that read, as does every check worker, because
a worker is provisioned as a Risk analyst:

- the lifecycle economic worker check: the worker meant to lack the read held
  it;
- the cash lifecycle valuation check: `This exact cash consumer must be
  qualified without gas authority.`;
- the physical option value check: `synthetic_read_was_accepted`;
- the lifecycle risk scope and risk jobs checks: a job expected to be refused
  `risk_job_denied` was accepted;
- the gas principal carrying check: `Principal metadata bypassed native gas
  authority.`;
- the Park and Loan check: `A Risk reader without current gas authority was
  offered independent gas ownership.`

None passed unchanged. Each identity that must lack the read now holds one role
made in the check, a copy of what it held without the read. No assertion
changed.

With everything:

- The REC inventory storage check passes 40 cases. The holders of each of the
  six permissions are exactly the roles D460 names, and the two reads are held
  by exactly the human roles that read trades, plus the agent-only gas role for
  the owned gas read. Two reads granted by hand before the migration are kept.
  Applying the grants again changes no role and gives no person a role, and an
  installation without one of the named roles is told which. The demo operator
  sets up and records. Trader and Risk analyst read the certified inventory and
  the directory. Viewer, Market data editor, Market data approver and Access
  administrator read neither. Trader records and sets up nothing. Recording
  without the setup permission and setup without the record permission each
  stay refused.
- `--inventory-access-check` passes its fourteen lines. The demo operator sets
  up an owned gas account and binds four manual action rules, and the demo
  Trader books a park and loan agreement on it, in the standard preview with
  no other input and no grant. A Risk analyst is refused that booking with
  `gas_park_loan_denied` and a Viewer with `gas_owned_denied`. Each of thirteen
  roles is then run as one person against the REC directory, account read,
  command outcome, account setup, destination setup and evidence routes, and
  against the owned gas read, setup choices, evidence, account setup with a
  manual action rule, the positions of the book that holds the agreement and
  the risk setup. Each role is allowed exactly what D460 lists and refused the
  rest with 403: `access_denied` on the reads and the REC writes,
  `gas_owned_denied` on the owned gas writes. Contracts administration alone is
  offered REC setup and Facility operations alone REC recording. Gas service
  setup, holding no trade-party read, saves an owned gas account and its rule,
  and Gas delivery operations records and is refused setup. Only Risk analyst
  is offered the pricing policy that includes independent gas holdings. A
  trade reader whose role loses the owned gas read is refused the whole
  positions read of that book with 403 `gas_owned_denied`, and reads it again
  once the role holds the read.
- `--structured-power-check` passes the 123 lines of its HTTP part. In the REC
  HTTP check the operator sets up accounts and destinations and records
  evidence, the Trader reads what the operator recorded, and the Trader's own
  record and setup requests answer 403 `access_denied`.
- `--scheduled-swap-check` passes 151 lines and the preview's default owned
  check 1,587, where the demo operator holds exactly its five roles and no
  other existing member holds an operating, supervisory or bilateral role.
- Six of the seven checks above pass. The seventh, the cash lifecycle valuation
  check, passes its native part, including the line that it holds no gas
  authority, and stops where its browser must start.

Every command of nineteen check groups was then run, 422 in all: repository,
managed, database, workspace, reference, standard, exchange, assistant, risk,
trading, lifecycle, physical, gas, gas-native, gas-browser, portfolio,
settlement, ppa and recovery. 350 commands passed as written. The 53 preview
commands that end in a browser suite were run without that suite and passed.
The 19 helpers that need a browser were run as written; each passed its native
part and stopped where its browser must start. No command failed. The frontend
group was not run.

These runs used disposable Linux containers (Ubuntu 24.04.5, x86-64) with stock
PostgreSQL 18.7, Python 3.12.3, Node 24.22.0 and a Debug API build under SDK
10.0.402. The 203 repository Python unit checks and the repository group also
pass on arm64 macOS with Python 3.14.2.

An independent adversarial review of the whole change, made without the build
plan, found no way to make the migration grant more than D460's table, apply
in part, lose a grant made by hand or touch an agent-only role or a role that
reads no trades, and found no assertion removed or loosened. It found the
following, each resolved as stated.

- The storage check also granted Facility operations' record permission by hand
  before the migration, which hid the migration's own row: with that row
  removed from a copy of the migration, the check still passed all 40 cases.
  The hand grant was removed. The check then fails on that copy at its holder
  assertion and passes on the real migration.
- The helper that gives an identity a role without a permission copied from
  grants no longer in force and verified nothing afterwards. A probe gave a
  person with a current Risk analyst grant and a lapsed Trader grant
  `AmendTrade` and `BookTrade`. The helper now copies what the identity holds
  at that moment, refuses an identity that would then hold nothing and asserts
  the result. The probe passes.
- The structured trading browser scenario switched demo users without
  selecting its close again. It now selects it after switching back to the
  Trader. This was changed by reading. The scenario first ran in the CI run
  described below, and passed.
- D460 omitted two consequences. The assistant's own trade read is refused over
  a scope that holds a park and loan agreement. A custody agreement in a book
  refuses that book's Blotter and position reads to every role without
  `ReadGasOperations`, the booking Trader included; D460 now records this and
  leaves it undecided. A probe with the installed roles confirmed its native
  part: the Trader books the customer side and the provider side, is then
  refused that agreement and its book's custody directory with
  `gas_operations_denied`, and a holder of the gas roles reads both. The
  refusal of the Blotter and positions routes was read from the code.
- D460 overstated three things, now corrected: the access check covers thirteen
  roles and the routes named above, not every route; a person who holds
  Facility operations alone is offered no retirement beneficiary on the page,
  although a probe showed the native operation accepts that person's
  retirement; and delegating to the gas agent also needs a role that may use
  agents.

After these changes the lifecycle, gas-native, gas-browser, portfolio and
physical groups and the repository group were run again, with the same result.

All of this ran before the hosted capacity change (D458) reached master. With
that change brought in, the seven groups that hold every check this change
adds or edits were run again in containers of the same image, in the same way:
lifecycle, gas-native, gas-browser, portfolio, physical, standard and
settlement, 170 commands. 142 passed as written, the 10 preview commands that
end in a browser suite passed without it, and the 18 helpers that need a
browser passed their native part and stopped where their browser must start.
No command failed, and every command printed as many passed lines as before.
The repository group and the repository's Python unit checks, now 211, pass on
arm64 macOS.

CI then ran all twenty groups on that tree, the first run of any browser suite
on this change. Nineteen passed, among them the frontend group and the groups
that run the structured trading scenario this change edits and the cash
lifecycle valuation browser check. The gas group stopped in the swing browser
suite, which timed out while a booked schedule opened. Without this change,
master's own run on the same base stopped at the same line, and its run on the
billing change stopped in the same suite at another step. The five gas
commands after the swing suite did not run.

The daily gas billing change (D459) then reached master with migrations 0406
to 0410, so this change's migration became 0411 and its decision D460. With
master brought in again, the same seven groups and the gas group, which holds
the billing preview check, were run again in the same way, 190 commands. 151
passed as written, the 21 preview commands that end in a browser suite passed
without it, and the 18 helpers that need a browser passed their native part
and stopped where their browser must start. No command failed. Every command
printed as many passed lines as in its group's last run, except the billing
preview check, which the billing change extended from 97 lines to 114. The
repository group and the 211 Python unit checks pass on arm64 macOS.

Not run or not covered:

- No browser suite ran in these containers. Chrome's sandbox does not start in
  them, and it was not relaxed. CI ran them once, on the hosted capacity base,
  as described above. No browser suite has run with the billing change brought
  in, and the gas commands after the swing suite have never run with theirs.
- The frontend group, outside that CI run.
- After the billing change was brought in: the managed, database, workspace,
  reference, exchange, assistant, risk, trading, ppa and recovery groups. They
  ran before the hosted capacity change and in that CI run.
- The park custody routes and `/lifecycle/execute` have no route assertion of
  their own. Holder sets pin who can use custody, and recording is asserted on
  a route that passes the same native gate.
- No check runs the cash option consumer as an installed Trader, who now reads
  owned gas; the existing check qualifies it for a person without that read.

## Daily gas billing and processing (D459)

Initial qualification used synthetic contracts, this tree's API and disposable
stock PostgreSQL databases over Swing baseline `ea9e8eb4`. Tools were .NET SDK
10.0.402, Python 3.13.7, Node 24.22.0 and Chrome on macOS arm64. The initial
results and measurements below establish local capability, not production
throughput or deployment qualification.

The initial managed group passed 86 runner workloads. The complete frontend suite
passed 2,403 tests with no failures or skips; the Python suite passed 187 tests. Focused
native/API checks pass daily entry billing, explicit processing clocks, shared
`gas.bill_daily` actions and recurring scope processing. Fresh installation and
guarded migration checks cover 0406, 0407, 0408 and 0410, including changed native
definitions, owners, privileges and configuration. Migration 0409 adds the
bounded readers without replacing predecessor functions.

A populated `ea9e8eb4` billing fixture also passes the complete migration chain.
Its three determinations and eight command receipts retain every original column
after each migration. All seven receipts owned by the ordinary trader replay
through determine and status with their original outcomes; claims and history
remain unchanged and no duplicate financial or command rows are created.
A second populated baseline preserves eight jobs, eight profiles, eight runs,
ten creation receipts, 24 admissions and four manual/review/automatic results
after each migration. All ten creation requests and four retained result reports
replay unchanged under fresh ordinary trader authority, without additional rows.

Independent cash oracles cover share 0.4 on 80 at 2.25 = 72, band 30–70 = 90,
shortfall below 100 = 45, a payable correction from 72 to 54 = credit 18,
and half-even totals 1.005 → 1.00 and 1.015 → 1.02. Quantity thresholds,
held basis, shares and quote multipliers apply once. Tests preserve the old
profile, exact original receipts, concurrent correction identity and historical
views. Missing scheduled evidence differs from explicit zero; only a complete
reconciliation match permits an automatic original. Covered exercise and Swing
payments refuse ordinary billing, while disjoint payments remain eligible.

Native scheduler checks reject unrelated discovery writes, forged assessments,
cross-scope sources and financial publication without a terminal result.
Two claimers admit one run. Continuations and children retain the first cut;
later scans revisit late old-day evidence and expose corrections for review.
Tests cover paused/blocked owner quota, retained backpressure, versioned scope
updates, superseded child cancellation, recurring phase and distinct bill counts
after child replay. Native billing reads also retain exact booked terms after the
original setup period has ended.

The full native automation workload passes. Shared futures margin, scheduled
option expiry, swap determination and swap economic replay also pass with
unchanged financial deadlines. An independent
baseline/candidate comparison completes the five futures lifecycle cases in
11.30–22.37 seconds on the candidate, against 11.35–21.19 seconds on the baseline,
under their existing 30-second limit. Native/API Swing and facility-gas regressions
pass. A separate gas-basis browser workload times out at its initial ten-second
Blotter wait on both the committed baseline and candidate; that workload is not
claimed as passing here.

The selected billing metadata workload passes all four custom/generic-plan and
fresh/analyzed-growth comparisons under its independent five-second SQL command
budget. Adding 512 unrelated curves preserves the exact certified result and row
order. Fresh-growth metadata tuples are 4,650 → 5,162, entirely accounted for by
the extra curve rows. After analysis, totals are 10,175 → 10,163 for custom plans
and 9,835 → 9,823 for generic plans. These are measured native tuple counts for
that fixture, not general latency or throughput promises.

Independent adversarial review covered arithmetic, source selection, historical
cuts, current authority, async execution, recovery and migration boundaries.
Its findings were resolved with failing-then-passing regressions, including
timezone review invalidation, distinguishable policy choices and replay-safe
determination counts. No material review finding remains deferred.

The combined commercial billing browser workflow passes 27 checks, including
original/correction recovery, edit guards and eight Jobs checks: explicit policy selection,
visible old-day corrections, retained uncertain enrollment, exact retry, pause,
separate assessment/bill results, and desktop/narrow viewport containment.
The runner's subsequent general physical-gas entry workload times out at its
30-second Blotter pricing wait, so the aggregate browser command is not fully
passing. This remains a release check alongside the baseline gas-basis timeout
above. Financial deadlines were not extended; the billing browser allows context
refreshes to settle within their existing calculation budget.

Reconciliation checks preserve an explicitly supplied 25-hour interval. The
current gas booking profile itself requires fixed-offset delivery clocks;
daylight-saving booking and recurring-calendar behavior are not qualified by
that managed interval check.

With the pinned tools selected, focused integration is reproducible from the
repository root:

```bash
python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --gas-billing-check --browser-check --browser "$BROWSER"
python3 tools/check_automation.py --pg-bin "$PG_BIN" --dotnet "$DOTNET"
```

The measured discovery workload uses one small book and 120 distinct contracts
with 120 distinct instrument bindings, 31 gas days and two payments each.
The larger scope contains 7,290 quantity facts: 3,720 scheduled and 3,570 final.
It assesses 7,440 daily cash/reconciliation candidates with zero PV scenarios,
yielding 7,140 ready items and 300 waiting for final evidence. Each page retains
the first page's exact input cut and no occurrence appears twice.

| Workload | Candidates | Pages | Elapsed | Median page | Maximum page | Observed API RSS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 contract × 2 days × 2 payments | 4 | 1 | 0.749 s | 0.742 s | 0.742 s | 354.6 MiB |
| 120 contracts × 31 days × 2 payments | 7,440 | 930 | 760.867 s | 0.781 s | 2.995 s | 364.6 MiB |

This run used stock PostgreSQL 18.1 on an M4 Pro with 14 logical CPUs and
macOS 26.6.2. Fixture setup is excluded. The API and database were warm, and other
isolated qualification processes were running on the host. PostgreSQL's
asynchronously published counters observed 929 page-reader calls totaling
276.15 seconds, 1,019 input-reader calls totaling 164.63 seconds, and 1,019
component-reader calls totaling 36.40 seconds. Those counters can lag the final
request; their nested times are not a complete database wall-time measurement.
The largest response was 16,995 bytes. RSS was sampled from the API process;
the managed platform's worker peak-memory counter was unavailable.

Execution was measured separately: one discovery run and four original financial
children completed in 30.390 seconds, including five check-process startups.
Managed discovery took 8.836 seconds; individual financial processing took
4.569–4.686 seconds. A separate maximum-sized page assessed eight occurrences
and dispatched four children in 8.860 managed seconds under the existing
30-second deadline. That sample's children were cancelled without executing;
it does not establish execution throughput for all 7,440 candidates. An old-day
final revision from 100 to 98 was reassessed in 0.385 seconds and exposed the
expected −294 payment and reconciliation difference.

Run the synthetic workload with `tools/run_preview.py --gas-billing-benchmark`
and the same `--pg-bin` and `--dotnet` arguments. Its output identifies discovery,
actual execution, database counters, memory availability and environment
separately. Larger books, sustained worker throughput and production operating
capacity remain unqualified.

Integration with merged Swing baseline `6a09aeda` was then qualified using the
same pinned tools. Both managed check projects built without warnings. Nine
managed workloads passed, including 811 core checks, 238 gas-ticket checks,
billing operating/enrollment contracts and shared lifecycle/automation checks.
The full frontend suite passed 2,412 tests with zero skips; all 202 Python tests
passed. Fresh billing migration guards, native/API billing and recurring scope
checks, and all 27 commercial billing browser checks passed. The subsequent
general physical-gas workload still timed out at the same 30-second Blotter
pricing wait with SQL cancellation, so the aggregate browser gate remains open.
All five billing migrations are unchanged from the earlier reviewed packet.
Independent integration review confirmed that Swing's final commit checks retain
billing receipt/scan/child seals and discovery-worker write restrictions.

## Daily gas quantity rights (D457)

Synthetic checks use an isolated stock PostgreSQL 18.7 server, .NET SDK
10.0.402, Node 24.22.0, Python 3.14.2 and Chrome on arm64 macOS. The financial
references distinguish commercial feasibility from grid value. A January
off-grid election of 1,050 leaves an arithmetic allowance of 30,105 and a
feasible maximum of 30,100. In the separate three-day example,
electing 1,050 on the first day gives grid value 365 and continuous optimum
367.50. The independently enumerated two-decision tree gives the adapted
value 1,000 × 7/12. Separate
forced, zero-volatility and incomplete-period cases check exact amounts.
Distinct booked bindings consuming the same market inputs retain their own
elections, counts, paid cash and scoped worker reads.

Before the admission and reporting corrections, the normal full runner
completed 285 top-level commands across all 20 groups.
The combined run shares installs, restores and builds. These counts include
only commands executed by `check_all.py --group all`.

| Group | Commands | Group | Commands |
| --- | ---: | --- | ---: |
| repository | 5 | frontend | 2 |
| managed | 84 | database | 16 |
| workspace | 12 | reference | 14 |
| standard | 3 | exchange | 1 |
| assistant | 4 | risk | 5 |
| trading | 10 | lifecycle | 34 |
| physical | 24 | gas | 12 |
| gas-native | 24 | gas-browser | 9 |
| portfolio | 18 | settlement | 1 |
| ppa | 6 | recovery | 1 |

The frontend ran 2,405 tests with zero skips. The managed core reported 811
checks, the gas ticket 237, and the independent swing reference nine cases.
D433's 19 storage plan and valuation results match the predecessor. Seven
dedicated D454 checks covered both full row oracles, original identities and
ordinary entries. The separate CI Compose and Swarm configuration render
also passed.

The disposable swing preview passed 205 printed checkpoints, including 28
browser checks, and completed 23 leased jobs. Two bindings with identical
terms elected 775 and 1,025. The worker retained the respective election
revisions in every price lane and returned base/up/down PVs
390/391.1/388.9 and 450/450.9/449.1. The complete-head transport and
fresh/populated migration, rollback and cash-survival checks used owned
databases. The shared pricing and binary-exercise preview passed 19
exercise-right browser checks, two curve-mark checks and five pricing-entry
checks on the same assets. The D454 preview passed 103 printed checkpoints,
five browser checks and three leased jobs.

After the admission and reporting corrections and integration of D456, the
frontend group passed 2,409 tests with zero skips. The managed group completed
84 commands, including 811 core checks and 238 gas-ticket checks. All 12 gas
previews passed across a resumed run on the same compiled assets.
The swing preview passed 207 printed checkpoints, including 28 browser checks,
and completed 23 leased jobs. One prepared 1,200-day election command with
500 Unicode scalars per row used 3,811,573 compact UTF-8 bytes. A replay
padded to exactly 4,194,304 bytes succeeded. An oversized command returned
the field-specific 413 and preserved all heads. The P&L position showed two
missing assessments on the same opening source day, one at closing and
300 in settled cash. Each endpoint counted the shared source day once.

Separate cash/P&L and structured-power previews passed on those assets.
The latter verified the complete mixed-product Blotter, retained Physical
Operations pagination and independence from an unrelated storage reader.
Its browser checks covered 16 structured-power and 19 structured-trading cases.

The six-hour model has finite error. For the 30-day 10,000-unit call at forward
3.20, strike 3.00 and volatility 0.50, the grid gives 2,948.858186402, the
independent 45-minute grid 2,947.467404967 and the analytic reference
2,947.441896610. The three moneyness cases have six-hour and finer error gates
of 2 and 0.25 held currency units respectively. These gates cover the named
fixtures. Independent finite-grid recurrence and exact held-size scaling have
separate checks.

The seasonal and annual service measurements used one physical and one fixed
payment Side, one trade, positive volatility and concurrency one on a 14-CPU,
48 GiB host. Per term, two ordinary jobs independently shock the point inputs
and monthly volatilities. They repeat the pinned base and preparation. The
90-day term used three distinct contracts, 14 base/child comparisons and 16
evaluated unit calls. The 365-day monthly-reset term used 12 contracts, 50
comparisons and 52 calls. Its 48 isolated children fit across two 24-child
jobs under the normal 32-child limit. The annual commits took 49.992 and
50.841 seconds. The sampled annual API peak was 1,144,512 KiB, and the point
and volatility workers peaked at 878,432 and 869,328 KiB. An annual worker
acknowledged cancellation during retained portfolio preparation and released
its lease in 0.051 seconds. Exit and administrative verification took another
0.032 seconds. The numerical kernel has separate in-flight token checks.

The Release kernel has a 512 MiB high-water RSS gate. These ordinary Debug
fixtures have 2 GiB sampled API and 1 GiB sampled worker gates. The original
512 MiB service target failed, including in a separate annual API control
without swing. Diagnostic 384 and 320 MiB heap limits also missed that API
target. The runtime uses its ordinary GC configuration. The modeled 256 MiB
workspace, 4,096-bit exact integer limit, 100 million operations, 15-second
HTTP budget, 60-second claimed job and 30-second renewable lease still apply.
Cold 1,200-day HTTP booking with one or two members and smaller-host capacity
remain unqualified. The full 1,200-head election HTTP transport was exercised
after authenticated native publication. See the
[complete workload measurements](superpowers/specs/2026-11-25-gas-swing-design.md#measured-marked-workloads)
and [commercial scope](physical-gas-side.md#daily-quantity-rights-d457).

## Imbalance cash-out (D454)

Local qualification uses synthetic trades, the checkout's own API and leased
worker, and isolated stock PostgreSQL 18.7 databases on macOS arm64. Toolchain:
.NET SDK 10.0.402, Python 3.13.7, Node 24.22.0 and Chrome. No production resources
are used. The full gas, trading and managed groups passed sequentially at
implementation commit `d8b1dc7f`, after the independent adversarial review and
its fixes. With the pinned tools selected, run from the repository root:

```bash
for group in gas trading managed; do
  python3 tools/check_all.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --browser "$BROWSER" --group "$group" || exit
done
```

| Group | Runner `CHECK` workloads | Printed `PASS` checkpoints | Owned browser checks | Completed workers | Databases removed |
| --- | ---: | ---: | ---: | ---: | ---: |
| Gas | 19 | 1,323 | 213 | 30 | 11 |
| Trading | 20 | 1,588 | 489 | 20 | 10 |
| Managed | 81 | 2,905 | — | — | — |

Runner workloads include restores and builds; printed checkpoints include
repeated setup and migration checks. Gas and trading each qualified an expected
`risk_graph_manifest` refusal for an altered retained manifest. Managed reported
806 core checks and 231 gas ticket checks. The dedicated cash-out workload
reported 103 PASS checkpoints, five browser checks and three completed workers.

Focused qualification passes both exact cash-out oracles: daily +1,190 and
month-to-date −700, each with one unassessed day. Managed cases cover immediate
predecessors, month boundaries, mid-month starts, account netting, exact units,
absolute size, negative and zero prices, paid corrections and paid withdrawal.
Native/API checks cover complete-pair admission, replay, financial readers
without gas-operations access, SQLSTATE 42501 outside a worker lease, and pinned
knowledge on ordinary and queued routes. Net settlement checks conserve +1,830
and −640 into one +1,190 receipt, preserve its identity on retries and refuse a
different second monthly settlement.

The full frontend suite passes 2,364 tests with zero skips, using sequential
files to avoid concurrent owned-browser startup contention on this host. Run
from the repository root after the web build:

```bash
node --test --test-concurrency=1 web/tests/*.test.mjs
```

The dedicated browser workload passes five checks: empty required cash-out
fields, booking, absent amounts on unknown days, signed daily statement values,
and exact native Side inspection for both payments. Gas ticket checks pass
231 cases. Retained overview and Blotter subtotal checks also pass. These are
local results, not evidence of deployment or production qualification.

Focused regressions retain paid cash when a fixing crosses zero, reject direct
worker settlement access, and hide a settlement before its paid instant. P&L
reports the quantity-only −300 residual with no cash, with a 900 receipt, and
after that receipt is revised to 600. It retains separate endpoint coverage;
an incomplete zero subtotal stays visible. Joint scenarios and derivatives
carry distinct coverage for an incomplete cash-out book and a complete book.
The independent review covered numerical behavior, temporal selection, paid
corrections, valuation authority and scenario reuse. Its four findings were
resolved with failing-then-passing regressions before the full qualification
above. No review findings remain deferred. The review was read-only; the
verification runs were performed separately by the implementer.

After merging master `b836ef3c` at commit `0207ce82`, the same local environment
passed `tools/check_all.py --group repository` (five workloads, 186 Python
unit checks, 36 settlement specification checks and 40 browser-harness checks).
Separate disposable `tools/run_preview.py` runs passed `--check
--gas-imbalance-check --browser-check` (103 PASS checkpoints, five browser
checks and three completed workers) and `--check --cash-pnl-check` (90 PASS
checkpoints). Both integration databases were removed. These reruns cover the
merged temporary-view source admission, whole-book owned-gas absence checks,
cash-out accounting and browser workflow. A separate read-only integration
review found no issues in their combined authorization and coverage behavior.

The settlement CI follow-up raises the disposable preview's runtime and risk
worker SQL command budgets from five to 30 seconds. Retained scheduled-swap
reads exceeded the five-second command budget on shared CI runners. Connection
and authenticator limits remain five seconds; worker calculation deadlines,
lease checks and cancellation still apply independently. This is a test and
local-preview budget adjustment, not a query optimization or a change to hosted
connection settings. The complete settlement group passes locally with nine
runner workloads, 252 PASS checkpoints, 101 owned browser checks and three
completed workers; its disposable database was removed. The checks cover both
dated-observation worker replay and scheduled-swap Blotter pricing. Independent
review found no blocking issue in the deadline change.

Billing metadata qualification retains its own five-second SQL command budget.
Its dedicated measurement process sets that budget before creating the database
client and verifies it on the opened connection. Exact result comparisons,
retained input coordinates and metadata-work bounds remain unchanged when the
general preview uses a longer command budget. At billing fix `467685b0`, the
full assistant group passed
12 runner workloads, 1,666 PASS checkpoints and 350 owned browser checks; all
three preview databases were removed. Repository checks passed again, and an
independent review found no blocking issue in the measurement's process scope
or preserved deadline.

Integration with master `cbab62a9` reserves D453 and migration 0401 for the
curve-mark editor, and D454 and migration 0402 for cash-out. The cash-out
migration's contents are unchanged. Repository checks passed on the combined
tree, as did three focused database/API/browser workloads: cash-out (103 PASS
checkpoints, five browser checks and three workers), curve marks (125 PASS
checkpoints and 30 browser checks), and physical assistant (153 PASS checkpoints
and ten browser checks). The last includes all four five-second billing
measurements. Each preview removed its database. Independent integration review
found no issues in migration ordering, test dispatch or retained deadlines.

## Curve Marks Editor (D453)

On 2026-11-24 the repository, frontend, managed and database groups passed at
`0b26568d`, which integrated master `b836ef3c`. After the futures execution
fixture correction at `3a2eabaf`, the full workspace rerun, gas, trading and
focused editor checks passed at that revision. The workstation ran macOS arm64
with stock PostgreSQL 18.7, .NET SDK 10.0.402, Node 24.22.0, Python 3.14.2
and Chrome 158. The runner used this tree's API, synthetic data and disposable
databases.

Run the groups with the pinned binaries from the repository root.

```sh
for group in repository frontend managed database workspace gas trading; do
  python3 tools/check_all.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --browser "$BROWSER" --group "$group" || exit
done
python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" \
  --curve-marks-check --browser-check --browser "$BROWSER" --no-build
```

The repository group passed 186 Python unit checks, 36 independent settlement
specification checks, 6 owned-process cleanup checks and 40 browser-readiness
checks. The standalone dependency check passed.

The frontend group ran the locked install, build and all 2,380 tests with
zero failures, cancellations or skips. Test files ran serially with the
existing deadlines. This includes first-publication storage failures, exact
retry after an uncertain outcome and review timestamp layout.

| Group | Runner workloads | Printed PASS checkpoints | Reported owned browser checks | Disposable previews removed |
| --- | ---: | ---: | ---: | ---: |
| Managed | 82 | 2,858 | — | — |
| Database | 24 | 434 | — | — |
| Workspace | 20 | 1,686 | 276 | 12 |
| Gas | 18 | 1,223 | 210 | 10 |
| Trading | 20 | 1,588 | 489 | 10 |

Workloads include dependency restores and builds. Printed checkpoints include
setup and migration checks. The managed group reported 805 core checks and
passed the editor's exact command, draft, addressed clipboard and receipt
checks. The database group reported 173 combined-schema checks.

The first workspace run stopped at the repeated-midnight futures execution
fixture. PostgreSQL 18.7 includes tzdata 2026f, which dropped the predicted
November 2026 fallback in Havana. The fixture now uses the observed November
2025 transition and checks that both fixed UTC instants map to the same local
midnight before requiring the two policy refusals. The focused execution
preview passed 90 checkpoints, including both refusals and the existing
financial expectations. [PostgreSQL 18.7 release notes](https://www.postgresql.org/docs/18/release-18-7.html)

The gas group completed 27 retained risk jobs. In the editor exercise case, a
missing January volatility left the call unpriced. The browser published
`0.5`, and an explicit financial refresh returned the existing four-place
acceptance value `2947.4419`. The earlier cut still showed missing volatility
and the retained risk result stayed pinned.

The focused web suite passed all 32 tests with zero failures, cancellations or
skips. The fresh focused preview passed 123 printed checkpoints, including
29 real-browser assertions and authenticated HTTP and native checks. Migration
0401 refused predecessor drift and preserved an overlapping legacy command's
original receipt. The editor checks exercised market-only admission, both
time coordinates, atomic rollback, stale predecessors, owner-scoped recovery,
receipt replay and source revocation. The preview removed its disposable
database.

Visual inspection covered the 20 × 12 volatility grid, 20-curve correlation
matrix, scrolled sticky headings, long synthetic labels, review evidence,
paste refusals, pending recovery and the 390-pixel inspector drawer. The
browser checks covered reduced motion, exact 12-place values, viewport
bounds and keyboard navigation in the supported theme.

On 2026-11-25 the full frontend group passed all 2,380 tests with the fixes
at `cb1b803a`. The focused managed editor checks passed. The focused preview
passed 125 checkpoints, including 30 browser assertions. A native batch
published in a non-UTC session returned the same receipt through owner-scoped
HTTP status and retry. Refused command representations left request, fact
and publication counts unchanged. Visual
inspection confirmed distinct column codes on the populated, scrolled matrix.
The gas exercise preview passed 128 checkpoints, including 21 browser checks,
and completed four retained risk jobs. Explicit repricing returned `2947.4419`
and the earlier missing mark and retained risk result stayed pinned.

Independent whole-branch review led to four corrections. The native batch
writer now requires canonical commands and serializes timestamps in UTC for
owner-scoped receipt recovery. Visible curve codes distinguish scrolled matrix
columns, and the documentation records the actual keyboard traversal and tested
revisions. An independent review of the fixes confirmed all four findings were
resolved. See the [feature contract](curve-marks-editor.md).

## Whole-book P&L dates without trades (D452)

Checked on synthetic data on 2026-11-24, in the same Linux x86_64 containers as
D451 below (stock PostgreSQL 18.7, .NET SDK 10.0.402, Python 3.12.3, Debug API
build), each run on a disposable database built from the whole migration
sequence and a fresh build. A workstation (macOS arm64) ran the 185 repository
Python unit checks and the standalone dependency check, which pass.

The new case in `tools/check_recorded_cash.py` records independently owned gas
in a book of its own through the native gas commands, with title received on
15 October: the book holds no gas at the 13th and 14th and some at the 15th
and 16th. With the code before D452, `tools/run_preview.py --check
--cash-pnl-check` passed 85 checks and stopped at the first run over that book,
from the 13th to the 16th, which names no trades and whose dates hold none:
422 `pnl_position`, a scope with neither holdings nor cash.

| Run, with the change | Result |
| --- | --- |
| `--check --cash-pnl-check` | passed, 90 PASS lines |
| the same with named-trade runs also making the proof | fails: the run that names its trade is refused with `gas_owned_policy_required` |
| `--check --async-workbench-check` | passed, 139 PASS lines |
| `--check --exchange-check`, which also runs the P&L checks | passed, 430 PASS lines |
| `--check`, the default owned check | passed, 1,586 PASS lines |

Rerun on 2026-11-24 after merging master at `2ae16e0a`, which took D450 for
independent gas charges; this entry and D451 below were renumbered then. On the
merged branch, in the same kind of container: `--check --cash-pnl-check` 90
PASS lines, `--check --async-workbench-check` 139, `--check --exchange-check`
430, the default owned check 1,586, and `tools/check_all.py --group managed`
passed its 81 workloads. The 186 repository Python unit checks, the settlement
specification and the standalone dependency check pass on the workstation.

What the new case establishes, for runs over the gas book that name no trades:

- From the 13th to the 16th, with no trade at either date, the run is refused
  at the 16th with `gas_owned_policy_required`, and the refusal says that a P&L
  report does not value such gas and to name the trades instead.
- A trade booked at the 16th in that book, with its premium cash recorded, is
  known to runs between earlier dates. From the 14th to the 15th the run is
  refused at the 15th, a date that holds the gas and no trade.
- From the 13th to the 14th, before the gas is held, each date proves its
  absence and the report lists that trade's cash alone.
- From the 14th to the 16th the run is refused at the 16th, which holds the
  trade, after the 14th has made the proof without gas or trades.
- A run from the 15th to the 16th that names the trade reports it as new and
  makes no claim about the book's gas.
- Over a book with no gas, a run whose prior date holds no trade reports that
  date empty, the trade as new, and is offered for saving.

Before D452, an exploratory run on an interactive preview over books that held
gas at both dates, neither holding a trade, returned a report listing only a
later trade's cash.

D451's other remaining limits were explored with temporary instrumentation that
recorded, after each date's view was restored, which admissions registered in
it would be refused. It is not in the tree.

- Presence proofs. In P&L runs over the exchange example (options, a whole
  book and a named trade) they were refused right after the restore
  (`gas_park_loan_scope`, `gas_transport_financial_context`), because the
  restore also removes the date's read coordinates. Once those coordinates are
  resolved again, as the final reauthorization resolves them outside any view,
  every proof passed: each date was on the UTC date of its instant. A close
  inserted directly with a business day before its UTC date, which the
  recording operation refuses, made both runs from it fail with 403
  `access_denied` while the date's close was read, before any proof was
  registered. The same runs from a close on its UTC date completed.
- Park and Loan source read. The Park and Loan native fixture
  (`tools/check_gas_park_loan.py`, four agreements across five checkpoints
  each) was extended with a P&L run over each agreement's book, whole and by
  the agreement's trade. All 40 runs were refused before commit:
  `pnl_accounting_coverage` while the agreement had value left,
  `gas_owned_policy_required` over the whole book once it held gas, and
  `risk_trade_scope` for a named agreement with nothing left to value. The
  agreement's source read was registered in 44 of their date views. Tried right
  after each restore it was refused (`gas_owned_context`), and nothing binds a
  date's context again before commit.

Not established here: no real-browser suite ran, for the reason given under
D451. The custody and Transport source reads were read in the code, not run.
The fence functions that make them unreachable have native checks of their own
(`tools/check_gas_park_custody.py`, `tools/gas_transport_financial_fences.py`);
no P&L run over books that hold such agreements ran here. The economic
projection's own source admission was not changed or run.

## Whole-book P&L date sources (D451)

Checked on synthetic data on 2026-11-24. A Linux x86_64 container (Ubuntu
24.04.5, stock PostgreSQL 18.7, .NET SDK 10.0.402, Python 3.12.3, Debug API
build) ran the database and HTTP checks, each on a disposable database built
from the whole migration sequence. A workstation (macOS arm64) ran the 185
repository Python unit checks and the standalone dependency check, which pass.

Before the change, with only the new whole-book case added,
`tools/run_preview.py --check --cash-pnl-check` passed the runs that name their
trades and stopped at the run that names none: 403 `access_denied`, SQLSTATE
42501.

| Run, in the container with the change | Result |
| --- | --- |
| `--check --cash-pnl-check` | passed, 89 PASS lines |
| the same with the view's requirement of its admissions removed | fails in the native check: an unadmitted source left its view unrequired |
| the same with a view also taking the admissions registered before it | fails in the native check: a view required an admission that was not its own |
| `--check --async-workbench-check` | passed, 139 PASS lines |
| `--check --exchange-check` | passed, 429 PASS lines |
| `--check`, the default owned check | passed, 1,586 PASS lines |
| `tools/check_all.py --group managed` | passed, 81 workloads |

What the new cases establish:

- A book of its own holds one option at both dates. The run that names no
  trades reports the same totals and positions as the run that names that
  trade, states its scope as all trades in the selected books, and is offered
  for saving.
- In one request, each of a saved report's two dates admits its own exact
  source in a temporary view, and the request commits.
- A source that was never admitted is refused with `trading_source_denied` when
  its view ends. That view leaves its ordinary admission pending and none that
  reads its state.
- What a restored source keeps for commit refuses an expired original deadline
  with `futures_margin_denied`.
- A source admitted before a view, and admitted again inside it, is still
  admitted after the restore and is required in full at commit.
- An admission that reads a view's state is required once before each restore
  and its retained part once at commit. One registered before the view, and an
  ordinary one registered inside it, are required at commit only, in full.

Not established here: no real-browser suite ran, because the browser's sandbox
does not start under the container's default security profile, which was left
as it was. The Risk page's own whole-book run was therefore not exercised in a
browser. The other check groups were not run for this change. No check lets an
original deadline lapse between a date's read and the commit of a real run.
Whole books that hold independently owned gas, and the limits D451 lists as
remaining, are covered by the next entry above
([D452](#whole-book-pl-dates-without-trades-d452)).

## Independent gas charges (D450)

Local verification on 2026-11-24 at `e8580a79` uses synthetic trades with
stock PostgreSQL 18.7 and the checkout's own API and worker. The pinned
tools are .NET SDK 10.0.402, Python 3.14.2, Node 24.22.0 and Chrome on macOS
arm64. The gas, trading and managed groups run sequentially through
`tools/check_all.py`. Integration fixtures provision and remove their own
databases. With the pinned tools selected, run the groups from the
repository root.

```bash
for group in gas trading managed; do
  python3 tools/check_all.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --browser "$BROWSER" --group "$group" || exit
done
```

All three groups passed. The first trading run stopped at the swap draft's
curve-read-count assertion and removed its disposable database. The full
trading rerun at the same commit with the same command and timeouts passed
both executions of that assertion. The cause of the initial failure is
unknown.

| Group | Runner `CHECK` workloads | Printed `PASS` checkpoints | Owned browser checks | Completed workers | Databases removed |
| --- | ---: | ---: | ---: | ---: | ---: |
| Gas | 18 | 1,220 | 208 | 27 | 10 |
| Trading | 20 | 1,588 | 489 | 20 | 10 |
| Managed | 81 | 2,809 | — | — | — |

Runner workloads include locked restores and builds. Printed checkpoints
include repeated setup and migration checks. Browser counts come from the
owned runner summaries. Each integration group also qualified one worker
refusal for an altered retained manifest (`risk_graph_manifest`). Managed
reported 804 core checks and 211 gas ticket checks.

The managed charge checks pin the no-charge identities and unchanged
following payment values. They exercise exact row shapes and charge
quantities, a second physical clock, distinct resolved day states across two
clocks, a term outside physical delivery, partial and overpaid cash, and
earlier knowledge. The lump sum refuses remittance one microsecond before
its derived earned day ends and admits it at that boundary, before its
payment date begins. Capacity payable on day 1 uses the exact completion
instant later the same day.

The gas charge integration workload reported 118 PASS checkpoints, 15 owned
browser checks and three completed retained workers. It compares independent
entry values and position components in previews, exact trades, the Blotter
and shared scenarios. The held schedule's reference example checks a
capacity charge of 10,000 units per day at 0.02. After ten days it has −2,000 accrued
and −4,000 unrealized. A −1,000 remittance leaves −1,000 outstanding then.
An earlier knowledge view of the same close excludes that remittance.
Changing delivery size leaves the charge amounts fixed. The workload also
covers retained workers, a multiplier-two typed price, remittance sign and
zero-amount refusals, request replay, invalid native shapes, an
exercise premium with unchanged group value and final billing refusals.

Migration 0400 extends migration 0399's native charge admission to refuse
the unrepresentable first UTC instant of a local 0001-01-01 day under a
midnight UTC+14 clock. Native lump-sum and capacity checks stage their
matching programs with the exact calendar and physical clock. Each accepts
the adjacent representable day.

Future replacement checks cover a fully paid upfront premium, original and
proposed capacity starting at the amendment instant, and a known payment
effective after the amendment. The ticket disables amendment for ineligible
holdings. Preview, booking preparation and the locked writer also reject a
started candidate. The checks compare the original revision, binding,
payment and allocations before and after refusal. A wholly future unpaid
replacement succeeds and its completed command replays after a payment on
the new binding.

The browser exercises explicit calendars, cleared basis-dependent price,
payment day 1–28, booking and exact retained charge Terms. It checks both
basis-specific labels, blank daily capacity cells, and the accrued,
outstanding and unrealized amounts through the exact Side inspectors. Saved
charge Terms show the stated currency, rate unit and capacity unit. The
inspector identifies the amount to come as unrealized present value.
The combined schedule includes independent monthly intervals and preserves
the monthly grain of a one-day capacity stub. Its progress uses the number
of native rows completed at their own ends. The ordinary projection still
uses its leaf-based progress, with no native-row basis marker.

The complete frontend suite passed all 2,353 tests with zero skips. It ran
the frontend build and the following two-worker test invocation from `web`.
The denomination test uses a multiplier-two quote, an exact 18-decimal-place
amount and a fractional capacity. A separate presentation test supplies
−3,925.75 unrealized PV against −4,000 nominal future cash.

```bash
npm run build
node --test --test-concurrency=2 tests/*.test.mjs
```

Independent review confirmed the corrected UTC boundary, amendment and payment
visibility checks, retained denomination and unrealized present value display.
The reviewed correction passed the focused preview, three full groups and full
frontend suite above. Optimizing the bounded repeated calendar reads remains
deferred. Integrated CI remains a separate gate.

Before publication, merge `75ca8b7e` incorporated the European-option changes
from master and assigned the unchanged charge migrations 0399 and 0400 and
the charge decision D450. Independent integration review found no lost
implementation, check registration or migration predecessor conflict.
On that combined tree, all 186 repository tests and 81 managed workloads
passed; the managed run printed 2,857 PASS checkpoints. The charge preview
passed 118 checkpoints, all 15 owned browser checks and three retained risk
jobs, then removed its disposable database. This run also qualified both
renumbered migration predecessor guards. The complete frontend build and
two-worker suite passed all 2,356 tests with zero failures or skips.

CI exposed a populated operating-upgrade fixture that reconstructed migration
0356's calendar predecessor from the current helper, retaining migration 0399's
later charge branch. The fixture now captures the actual four predecessors
before 0356, verifies legacy quantities, programs and retries under both the
predecessor and candidate, and atomically restores all current helper definitions.
This preserves later facility-booking and charge-calendar extensions. Both the
original guard failure and a current-helper preservation regression were observed
before their corrections. Independent review passed. The final structured-power
preview passed 156 checkpoints and all 35 owned browser checks, removed its
disposable database, and the full repository Python suite passed all 186 tests.

## European futures options (D449)

Qualified on synthetic data in two places on 2026-11-24. A workstation (macOS arm64,
.NET SDK 10.0.402, Node 24.22.0) ran the checks that need no database. A Linux
x86_64 container (Ubuntu 24.04.5, PostgreSQL 18.7, .NET SDK 10.0.402, Python 3.12.3)
ran the database and HTTP checks, each on a disposable database built from the whole
migration sequence. No real-browser suite has run against a preview: the browser's
sandbox does not start under the container's default security profile, which was
left as it was. The capability is described in
[futures options](futures-options.md#european-exercise).

Every row below ran on the branch with master merged in. Master then held the
Instrument schedule, gas pricing entries and exercise rights (D446 to D448) and
their migrations 0391 to 0396; this increment's migrations follow them as 0397 and
0398.

| Run | Where | Result |
| --- | --- | --- |
| `tools/check_all.py --group managed` | workstation | passed, 81 workloads; the rows below marked "in the group" are among them |
| MarketData checks, default | workstation, in the group | 804 managed core checks passed |
| MarketData checks `--european-options` | workstation, container | 31 PASS |
| MarketData checks `--futures-options` | workstation in the group, container | 70 PASS; the pinned risk digests pass with their expected values unedited on both |
| MarketData checks `--option-sensitivities`, `--option-strategies`, `--vol-surface` | workstation, container | 8, 25 and 22 PASS |
| MarketData checks `--option-lifecycle-projection`, `--cash-option-model`, `--physical-option`, `--cash-option-binding`, `--physical-option-binding` | workstation | pass, each with its European refusal |
| MarketData checks `--spread-options`, `--exercise-rights`, `--calendar-policy`, `--payment-date-policy`, `--hybrid-monte-carlo-risk` | workstation, in the group | pass |
| API checks `--schedules` | workstation in the group, container | 565 PASS |
| API checks `--option-policy`, `--european-options` | workstation, container | 8 and 4 PASS |
| API checks `--cash-option-value-policy`, `--physical-option-policy`, `--option-strategy-request`, `--risk-pricing-policy` | workstation | pass |
| API checks `--historical-risk`, `--monte-carlo-risk`, `--context-resolution`, `--read-references`, `--power-hedge`, `--async-reads`, `--assistant-prompts`, `--published-clock`, `--calendar-authoring`, `--gas-ticket`, `--position-adjustment` | workstation, in the group | pass |
| API checks `--lifecycle-actions`, `--economic-risk-contract`, `--lifecycle-schedule-contract`, `--gas-owned-setup`, `--shared-native-markets`, `--derivative-methods`, `--gas-accepted-order`, `--lifecycle-residual-currencies`, `--gas-operator-contract` | workstation, in the group | pass |
| Rates and Shared checks | workstation, in the group | 88 and 51 passed |
| `python3 -m unittest discover -s tests -p 'test_*.py'` | workstation | 185 OK, with migrations 0397 and 0398 in the numbering check |
| `npm test --prefix web` | workstation | 2,354 pass |
| Migrations 0397 and 0398 applied to a database | container | applied at the end of the whole sequence in every run below |
| `tools/check_european_option.py` and its managed read `--european-option-read` | container | 7 checks pass, alone and as a step of the `lifecycle` group |
| The same tool on a throwaway copy whose migration 0397 leaves the option event primitive unpatched | container | fails as intended: the primitive records the exercise of a European option |
| `tools/check_all.py --group database` | container | passed, 434 PASS lines |
| `tools/run_preview.py --check --exchange-check` without `--browser-check` | container | passed, 427 PASS lines, with the three new assertions of `tools/check_futures_option_storage.py` and the five of `tools/check_vol_surface_storage.py` |
| `tools/check_all.py --group lifecycle` | container | its 38 steps that need no browser passed, 1,634 PASS lines; the first of its four browser steps stopped when its browser did not start, and the other three did not start |
| `tests/browser/futures-options.mjs`, the browser part of the `exchange` group and the four browser steps of the `lifecycle` group | nowhere | not run |
| The other groups of `tools/check_all.py`, as groups | nowhere | not run; the `repository` group's Python suite and the `frontend` group's `npm test` are the workstation rows above |
| The risk worker's replay of a retained job that holds a European series | nowhere | no check exists |

What the checks that ran establish:

- **The formula.** `EuropeanFuturesOptionChecks` compares the Black-76 formula with the
  independent 60-digit values of `tests/fixtures/futures-option-oracle.json`: within
  10⁻²⁷ on its 17 cases, discounted and undiscounted, calls and puts (largest observed
  difference 1.5 × 10⁻²⁸), and within 10⁻²⁶ on its two wide cases (1.2 × 10⁻²⁷, at
  a strike of 3,276). Put-call parity holds to the same bound. The arithmetic is
  integer, and the same checks pass with the same expected values on Linux x86_64.
  Sixty seconds, and one clock tick, from expiry the value is the discounted intrinsic
  value or zero with no range error. The value rises with volatility and is never
  negative; without its floor a call far out of the money read −4 × 10⁻³⁰.
- **Calibration.** A settlement rounded from each of eight oracle values is repriced
  within half an increment at a volatility within 0.005 of the oracle's, under a
  bracket whose top stays 3 where the tree's range would lower it to 1.72. Hand cases
  with F = 5, K = 3 and D = 0.99: 1.98 and 1.975 fall back with residuals 0 and
  −0.005, 1.969 is the least admitted and 1.968999 is refused. A market rate of 4.5%
  against a 4% curve for a year falls back and 5% is refused; for a quarter 6% falls
  back and 6.5% is refused. An out-of-the-money zero settlement falls back to zero
  and is refused when the fallback is not declared.
- **Scenarios.** Through the shared base, up and down valuation a European holding's
  base value is its settlement within half an increment, its fallback values a shock
  of 1.25 at 22,483.75 up and zero down by hand arithmetic, and a zero settlement is
  worth nothing in every scenario. At the oracle's volatility a one-tick revaluation
  gives the oracle's delta within 10⁻⁶ and gamma within 0.01%; at the calibrated
  volatility the risk run and the sensitivity layers give delta within 0.001 and
  gamma and vega within 1%.
- **No series on the wrong model.** The tree refuses a European series and the
  formula's policy; the formula refuses an American series and a tree policy; the
  dispatcher, the solver and the binding refuse a policy that does not price the
  style with `option_terms`. The five projection and reference-strategy entry points
  refuse a European series with `projection_option_style`.
- **Unchanged American values.** The 70 `--futures-options` checks, the sensitivity
  and strategy checks and four solver outcomes re-read through the shared solver
  pass with no expected value edited. `Release1` serializes to its earlier bytes.
- **Policy.** The stated policy is `Release1` plus the `european` member, byte for
  byte. A European series under `Release1` is refused with `option_policy` and named.
  On one synthetic future an American and a European call under one policy are
  calibrated by the tree and the formula, and each receipt names its numerical
  policy. A retained calibration that recomputes differently, or under the other
  model, is refused with `risk_option_calibration` by the comparison the worker
  calls.
- **Presentation.** The web tests cover the ticket's Exercise and Settlement facts,
  the lifecycle reason and the Risk heading's European member.
- **Installed and end to end.** On a database built from the whole migration
  sequence, `tools/check_european_option.py` lists a synthetic European product, a
  call and a put at one strike, on a synthetic cash-settled future that also has an
  American call. The product is stored with no contrary instructions; a trader books
  it and a settings editor and a principal holding only Trade supervisor cannot.
  Exercise and assignment before expiry are refused in the proposal with
  `option_exercise_style` and write nothing. With the proposal's refusal set aside
  inside one rolled-back transaction, the option event primitive refuses the same
  exercise itself; on a copy whose primitive is left unpatched that check fails.
  After the expiry instant the automatic rule exercises the call into its future at
  the strike under its margin policy and abandons the put. The managed read shows
  the catalog's style and settlement, the two styles on one future each calibrated
  by their own model, the European holding worth its settlement, the lifecycle list
  refusing its exercise by style, a fit of the product naming the formula's policy,
  and a policy without the European member refusing the book.
- **Stored rows.** The product table refuses a style other than `american` or
  `european` and a European product that accepts contrary instructions. A surface
  revision is recorded only under the policy of its product's style, in both
  directions, and the table admits the two policy ids and no other.

Not covered by any check: a European series whose expiry lies outside the rate
curve's pillars (the `discount_coverage` exclusion is the code American series use,
and no fixture lists a future beyond the curve); a fit of a European product through
HTTP and the worker, end to end; the risk worker's replay of a retained job that
holds a European series; and the Gas delivery operations role. Not run: every
real-browser check of the ticket, the Details panel and the lifecycle list.

One independent adversarial review read the whole change on 2026-11-23 and ran the
managed option checks. It found that migration 0398 spliced an unparenthesized CASE
into two PL/pgSQL IF conditions, which would not have compiled; the CASE is now
parenthesized, `tests/test_migration_spliced_conditions.py` checks it, and the
migration has since applied. It found that the lifecycle projection refused a
European holding only after calibrating it and reading its profile; the projection
now refuses it where it names its options, with a check. It found that the option
event primitive's refusal was read from its installed definition and never executed;
the tool now executes it, as described above. The review's minor findings are not
acted on. It found no path that prices a series on the wrong model and no change to
American values. Merging master afterwards changed this increment's migration and
decision numbers and none of its behaviour; the merge was not reviewed separately.

## Exercise rights on a gas Instrument (D448)

Local verification on 2026-11-24 uses the application tree at `f777479d`,
Python 3.13, stock PostgreSQL 18.7, .NET SDK 10.0.402, Node 24.22.0 and Chrome
on macOS arm64. The integration groups build and run this checkout's assets
sequentially with isolated disposable synthetic databases. The three full
`tools/check_all.py` groups exited 0.

| Group | Runner `CHECK` workloads | Printed `PASS` checkpoints | Owned browser checks | Completed workers | Databases removed |
| --- | ---: | ---: | ---: | ---: | ---: |
| Gas | 17 | 1,102 | 193 | 24 | 9 |
| Trading | 20 | 1,588 | 489 | 20 | 10 |
| Managed | 81 | 2,734 | — | — | — |

Runner workloads include locked restores and builds. Printed checkpoints
include repeated setup and migration checks and are counted separately from
the runner's browser and managed summaries. Gas and trading each ran the
altered-manifest worker case that must terminate with `risk_graph_manifest`.
The completed-worker column counts successful jobs. The managed core reported
803 checks. The exercise-right selection emitted 36 PASS checkpoints, the gas
ticket reported 177 checks, and the listed-option and spread/heat-rate
selections emitted 70 and 21 PASS checkpoints.

The managed checks exercise every holder-choice case, both holders, put-call
parity, zero-time value, exact rounding and model refusals. They pin legacy
no-right identity and trade scaling and check daily and monthly grouping,
including staggered gas-day boundaries, an exact repeating quantity, and the
allocation of whole-term cash across groups. They also check retained fixing
and election deadlines, delivery conflicts on contract-quantity holdings,
publication on zero-volume rows and a still-live cash forecast after payment
falls due. Moving the supplied fixing together with the group deadline refuses
when the booked Side retains the earlier fixing. A three-day monthly group
uses the monthly observation on each day and a later daily fixing on one day.
Pricing-binding tests distinguish correlation revisions and preserve pair
symmetry. Settlement and reuse tests cover paid cash and known election changes. Independent held-value oracles include
2,947.4419 for the daily fixed-price purchase, −947.4419 when the counterparty
holds the right and 2,163.3174 for the indexed strike. The worked three-day result preserves the
exact elected and abandoned amounts alongside the modeled contribution.

The gas integration checks cover native and managed admission, exact physical
and cash exercise instants, both temporal coordinates, immutable revisions and
withdrawals, atomic and idempotent batches, current authority and retained
worker scope. Direct rights-table reads and the terms projection use the
selected holding and retained cutoff, including for a shared binding. A
booked-ticket read refuses to publish a pending head after its AmendTrade
grant expires. In the two-holding Blotter, the ordinary holding keeps its
price and the conflicting holding keeps its authorized correction action.
An exclusion at a retained cutoff survives later withdrawal of the delivery fact. Mark receipt replay
checks the effective instant for both volatility and correlation, including a
receipt for an identical historical fact. Interactive and retained risk reconcile
price, volatility and combined scenarios and preserve gross group economics when
ordinary Side cash is paid. Owned browser checks exercise the standalone
ticket, absent marks, schedule provenance, the main-window action, read-only
trade windows and exact retry after losing the response. The combined exercise
and pricing workload emitted 138 PASS checkpoints, completed 12 retained worker
runs and reported 24 owned browser checks, five for pricing and 19 for exercise.

The frontend build passed. The full frontend suite ran serially from `web`
with `node --test --test-concurrency=1 tests/*.test.mjs`. All 2,344 tests
passed with zero skips. They include a priced multi-clock ticket with an
unavailable combined schedule, an editable refusal that preserves the last grid
and its units, an initially unpriced amendment with a visible quantity unit,
a successful reread after a stale predecessor, and exact recovery when session
storage is unavailable. The table counts the owned integration browsers
separately. Integrated CI is a separate merge gate.

## Instrument schedule and independent gas pricing (D446–D447)

Local qualification on 2026-11-23 uses synthetic inputs, disposable stock
PostgreSQL 18.7, .NET SDK 10.0.402, Node 24.22.0 and Chrome on macOS arm64.
Each integration group uses a frozen checkout's own API/worker assets and owned database. No production
resources are used and no publication or deployment is implied.

The managed schedule oracle covers all 27 concrete sided Instrument families,
ordered memberships and rows, schedule instance reuse, exact independent Side
sums and shared-Side context reuse. Legacy refusal codes/messages and canonical
identity literals are pinned. Exact quantity checks include boundaries, explicit
zero, a repeating daily quantity, incomplete/mismatched held state, partial-term
exclusion, source-policy labels and a part reduced to zero after paid cash.

The three-day synthetic pricing oracle independently checks every entry in
Example A (total 2,650) and Example B (accrued unsettled −47,750, unrealized 500,
total −47,250). With the monthly price unpublished, Example B instead has accrued
9,250 and unrealized −56,500, retaining the total. Share 1.05 prices 10,500 units
at 3.20 for 33,600. Doubled holdings/records double each amount exactly.

The pricing HTTP/native suite checks reduced storage, request replay, independent
native refusals, monthly/daily index qualification, exact partial period totals
and billing boundaries. Preview, trade, Blotter and queued risk agree by entry;
earlier-cutoff reads retain prior quantities. Held-preview mismatched terms and
current-authority failures refuse. Migration checks pin predecessor definition,
owner, grants and configuration, test an independent caller search path and
preserve populated predecessor requests.

Review regression checks reject missing modern retry fields and a fixing on a
different same-date contract. Independent physical sources with overlapping
terms and different gas clocks accrue only their own payments; both contract and
delivery-schedule policies pass. Changed source maps refuse prepared-context and
definition reuse while preserving legacy economic canonical bytes. The HTTP
suite also verifies the partial-payment case through reads and queued risk.

The full managed group passes 80 runner checks, including 803 managed core
checks. The full gas group passes 17 runner checks and 173 browser checks;
the full trading group passes 20 runner checks and 489 browser checks. All three
groups passed again on application commit `26324396` after the review fixes.
The ticket suite passes 169 checks and the shared description suite 67. All
2,328 frontend tests pass with Node test concurrency explicitly bounded at two;
unbounded attempts hit browser/process-startup timeouts, with no assertions or
timeouts relaxed. Five pricing browser checks cover conditional levels, fuel-link
refusal, named monthly booking and signed taken/source quantities. Nine indexed
browser checks retain the original 59-day amounts and inspection paging.

### Fixed synthetic benchmark

Base `aab2c3e6` and application tip `26324396` ran the same
`--risk-benchmark` workload in Release on .NET 10.0.13, macOS 26.6.2 arm64,
14 logical processors. Every workload has 50,000 bindings. Each gets one warm-up
and five sequential samples with fresh within-run caches. Construction is
excluded except the portfolio/holding preparation stated by the benchmark;
the first three timings include per-position reconciliation against an
independent full-repricing reference. The basis and futures expected-value
checks run outside their timed sections.

Side work and unit calls below are equal at base and tip. Time and allocation
columns are base / tip. Allocations are current-thread bytes, including timed
validation; peak/resident/native memory is not measured.

| Workload | Unique held contracts | Scenarios | Side evaluations, both | Unit calls, both | Median ms, base / tip | Allocated bytes, base / tip |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| repeated | 1 | 3 | 3 | 3 | 457.90 / 468.61 | 502,509,232 / 559,709,776 |
| shared fanout | 128 | 7 | 388 | 388 | 820.82 / 845.53 | 884,424,448 / 1,024,876,880 |
| distinct | 5,000 | 3 | 15,000 | 15,000 | 800.26 / 904.39 | 946,337,784 / 1,005,019,504 |
| multilevel basis books (9) | 9 | 19 | 106 | 53 | 3.08 / 3.98 | 4,481,016 / 4,488,856 |
| multilevel basis books (216) | 216 | 433 | 2,544 | 1,272 | 257.63 / 349.71 | 1,021,487,104 / 1,023,918,672 |
| futures books | 1 | 3 | 0 | 3 | 153.01 / 172.47 | 228,780,792 / 228,782,080 |

Exact output parity and unchanged evaluated Side work pass. Timing was measured
on a shared development host, with isolated integration groups also running;
these medians do not establish a throughput improvement or regression. This is
managed calculation work, without database, serialization, authorization or
service throughput. Shared fan-out uses three factors; the basis cases use nine
and 216; the other workloads use one.

## Selected gas delivery quantities (D445)

Local qualification on 2026-11-23 uses .NET SDK 10.0.402, stock PostgreSQL 18.7,
Node 24.22.0 and Chrome on macOS arm64, with isolated disposable databases and
synthetic observations. The managed verification group, 127 gas ticket checks,
2,326 frontend tests and the repository verification group pass. All gas preview
workloads pass, including indexed cash, amendments, facility/daily operations,
final billing and the administrator's in-term load. The trading verification group
also passes, including shared booking, retained schedule reuse, cross-commodity
risk, index fixings, close-out, observation schedules and regional startup.

`DeliveryQuantityChecks` independently checks the four-day worked example:
unrealized 380, accrued unpaid −6,090 and total −5,710; a +0.10 location shock
raises unrealized to 570 without changing accrued cash. It checks exact fractions,
unequal daily contract quantities, mixed policies, complete membership/day
coverage, source copying, retained-instrument parity and trade-specific reuse.
Selection checks cover every rank, zero, withdrawals, equal evidence and conflicts.

`tools/check_gas_delivery_quantity.py` passes migration predecessor-drift checks,
native persistence, earlier-cutoff reads, Blotter/ticket/portfolio parity, queued
risk replay after a correction, and the ordinary worker's exact catalogue,
binding, book, trade, time and lease scope. It exercises manual and adopted
facility allocation conflicts, agreeing sources, independent withdrawals and
known records that are not yet effective. A real recorded payment remains fully
overpaid after ended-day quantities correct to zero. Configured usage fees follow
the selected source. Completed and interrupted legacy ticket commands retain their
outcomes; changed requests, another actor's retained command and fresh tickets
without the policy refuse. Malformed retry drafts retain structured refusals.

All five dedicated browser checks pass: the visible Delivery schedule proposal,
the linked-composition refusal, per-day source labels, evidence references and
the overpaid cash balance with its exact selected quantity ratio. Independent
adversarial review covered numerical, temporal, authorization and retained-command
behavior; its findings are resolved. Migration 0390 and these changes have not
been published or applied to production by this qualification.

## Running-trade amendments (D439–D441)

Qualification on 2026-11-21 used .NET SDK 10.0.402, stock PostgreSQL 18.7 and
disposable synthetic databases, with database-backed checks on Linux and the
managed and web suites on macOS arm64. The 799 managed core checks, the API
project's default checks including the started-month boundaries, the `--ppa-sides`
and `--settlement-preview` suites, 180 Python checks and all 2,293 web checks
passed.

The native desk check covers the commercial-change rule over independent revision
rows, including restated scheduled terms, and confirmations read and recorded
through the revision whose terms are carried. Through HTTP, the scheduled-swap
check refuses a quantity amendment under a credit hold while a memo correction is
admitted under the earlier agreement and credit decision, and admits a memo
correction as commercial when another agreement is chosen. The PPA booking check
appends an extension at the retained end, moves the end later from an earlier
boundary with exact independent values, and refuses an earlier end, a gap and an
empty termination. The settlement check previews a delivered month with unchanged
exact cash and lineage after the running trade is extended.

For a running scheduled swap (D441) the scheduled-swap check settles January,
then at mid-January restates the terms its read returns, reprices February and adds
March. Preview, ticket price, saved lifecycle value and Blotter PV equal an
independent decimal oracle over stated quantities, agreed prices and one
separately priced March mark; January's determination, settlement version and
period identity are unchanged, and the bridge reports the change as its amendment
component with no continuing movement. It refuses a changed price or quantity of
the settled month by name, a shorter term, a changed direction, discounting or
price settlement, a contract total re-spread over a longer term, and a month that
has already started added to a swap that has not. Run settings carry for an
unchanged month and not for a repriced one, January's fixed side settles under the
carried settings, and a second amendment restates a later month. The native proof
admits an unchanged started month and refuses a repriced month, a changed quantity
and a shorter term. The lifecycle swap, scheduling and action checks, the factor
swap, stored swap risk, close-out draft, gas-amendment, PPA and structured-power
checks passed unchanged. A browser test of the ticket covers locked controls, kept
months on extension and the exact request.

For D442 the same check settles January on a scheduled and a composed swap, then
amends each at the December booking As of: both are refused with
`swap_amendment_backdated`, the scheduled preview and trade read state the instant
of the latest activity, and the composed save, which has no managed preview, is
refused by the owned write alone. With the permission granted to the trader role
the scheduled amendment is admitted, a change to the settled month is still refused
by the settlement guard, and after the grant is removed the next one is refused
again. The check was first run without the rule and failed with an accepted
preview.

Not run: the owned browser suites against a live preview and the complete standard
check. Not covered for D442: a payment or net command as the latest activity, and
the role assigned through Users & access. Not covered by a check: the owned write's refusal
of a changed started month when the managed operation is bypassed, beyond its
native proof function; started swaps with a fixing-only month, floating/floating
sides, observation schedules or custom projections; the scope refusals for factor,
power-hour and exchange-settled swaps; carried settings on a composed swap. An
added PPA month is planned in the managed planner check only; no HTTP preview covers
one, because the fixture publishes final meters for one month.

## Exact currency and price units (D438)

Local qualification on 2026-11-20 used .NET SDK 10.0.402, stock PostgreSQL
18.7 and disposable synthetic databases on macOS arm64. The 799 managed core
checks, 88 rate checks, API `--schedules` suite and 178 Python checks passed.

The native/HTTP regional gate verified exact discount-default foreign keys,
valid populated upgrades and atomic refusal of conflicting mappings. Derived
publication rejected incompatible currencies through both API and native entry
points, retained independent output currencies, and traversed 22 nested published
parents without prices. Accepted publication retried successfully from a fresh
session; changing its snapshot under the same request ID refused. GBP booked
Side inspection retained its full quantity and price units.

The PPA gate passed 32 break-even HTTP checks. Its quarter-hour settlement gate
passed 17 indexed and eight fixed-preview checks over 2,884 native DST cells,
including later-known corrections, original-cutoff replay, amendment and
termination. The fixed response contained 14,752,814 decoded JSON bytes and
the indexed response contained 16,711,466, within the existing response cap.
Response-byte and work limits remain independent refusal boundaries; these
fixtures qualify no production latency or concurrent capacity. Managed
fixed/indexed checks also covered exact USD, GBP, EUR and CAD inputs with explicit
rounding terms.

The owned exchange gate passed. It verified price multipliers independently of
option lot sizes, absence of a price denomination on premium cash, and exact
currency and held-series references. Option values, premium cash, scenario risk, stored
surface reads and strategy analyses retained their independent numerical checks.
Later settlement/rate corrections retained the original surface at its old
knowledge cutoff.

The web suite ran 2,273 checks with no skips: 2,272 passed in the full run;
one failed before loading its page because the owned Chrome debugger did not
start. That check passed on its isolated rerun. Currency-specific browser
checks covered retained rounding controls, post-save knowledge refresh,
stale-response disposal, managed amount display and exact series selection.
Export checks retained filtered/regrouped factor and currency/price identities.
Independent domain, storage and UI adversarial review found no remaining blocker.

Requalification with facility migrations 0377–0379 present passed the API/seed/web
builds, 799 managed core checks, API `--schedules`, 178 Python checks plus the
combined-preset product regression, 133 focused web checks and the fresh native
regional gate. Currency migrations are numbered 0380–0381. Interactive combined
gas/regional presets retain agreement coverage for both products; integration
review found no remaining blocker.

Additional requalification passed 180 Python checks, the populated recorded-cash
upgrade and the regional, publication-authoring, captured-power and flat-power
integration gates. Temporary source helpers leave no runtime grants on their
temporary schemas while deferred commit guards retain the caller's identity.
Native projection checks use complete source-pinned recipes and reject unpublished
outputs at booking. Native publication validates structural policies before
deriving units, preserving the structured refusals for malformed splice graphs.

Actual-browser requalification passed 92 publication/contract authoring checks,
10 captured-power checks, 18 shared-trading checks, 30 option ticket/position
checks and 33 Options page checks. Strategy cash displays match the actual managed
formatter responses; captured product means and publication receipts retain their
complete price units without requiring insignificant trailing decimal zeros.

The complete owned risk gate passed 172 browser checks across scenario setup,
retained workspace recovery and cross-close results. The three suites share a
fifteen-minute aggregate watchdog and retain their individual action deadlines.

Integration with the grouped structured entry passed all 2,285 web checks, the
focused preview regression and 35 native/API-backed power and structured-booking
browser checks. Operating values retain their emphasis and every native price row
retains its full managed price unit.

These results qualify local behavior. Full CI, coordinated migration/API/UI
release and hosted activation remain separate gates; no hosted database was
changed by these checks. See the [contract](currency-and-price-units.md).

## Calendar and browser As of context lifetimes (D434)

Six consecutive requests for the ten most recent qualifying dates of the
synthetic rolling-average curve created 60 financial contexts at schema 0366.
The same requests created zero after migration 0373. The standard fixture for
this comparison had 175 distinct contracts and 44 offered frames. A built-in
curve price and its date strip each created zero contexts in both versions.
These counts measure database growth. Browser speed and production capacity
need separate measurements.

On the schema that includes heat-rate call options and gas storage, historical
workspace reads returned the booked positions, exercises, plan revisions and
spread marks visible at each tested cutoff. These reads added zero rows to the
four financial context tables. The isolated native tests also covered the
cutoff before booking and the mark before correction.
Historical lifecycle discovery returned the requested close and cutoff with
zero new context rows. Customer and provider gas-custody histories remained
readable with the action forms disabled. The provider-only fixture also
exercised this read without owned-inventory or bank-payment authority.
Preparing and previewing an ordinary trader mark left all four financial
context tables unchanged. Publication retained the displayed cutoff and
domain prefixes. A fresh sign-in could recover the published observation
after a later correction.

The volatility surface browser check applied a historical cutoff in the same
tab and returned the original fit revision at its selected close. A stored
fit missing required projection inputs reported that an explicit refit was
needed. Recorded zero coefficients and expired terms remained readable.

The calendar suite runs with `tools/run_preview.py --market-dates-check`.
It checks a real correction against independently resolved earlier and later
knowledge cutoffs, rejects inconsistent publication coordinates and verifies
that releasing a child restores the parent. An attempted commit of a transient
binding fails at the database boundary. Invalid calendar requests leave the
certified watermark alone. A successful working request still certifies the
shared publication watermark once.

With 4,096 stored contexts, two independent viewer sessions returned the same
ten-date strip as the sparse fixture. A repeat request returned it again.
The market, catalogue and trading context tables also had no new rows. Closing
the HTTP connection while PostgreSQL waited inside the temporary binding
released the waiter. Replacing a selected close during a second blocked read
returned `close_replaced`. The next read succeeded. All 67 calendar and close
session browser checks passed, including offering a newer close while the
current selection stays visible until the user accepts it.

Browser reload and browser Back both reopened the historical cutoff and
returned the same contract IDs and exact prices after the close was selected.
Restoring an expired view returned `409 workspace_view_expired`. The other
tab continued to price its own selection.

The related `--async-workbench-check` suite passed 50 HTTP/SSE checks including
cancellation before admission, independent browser tabs, profile changes and
authority expiring before disclosure. The `--risk-jobs-check` suite replayed
existing job inputs with identical financial values and recovered expired
worker ownership. The connected and joint scenario checks exercised parent
isolation and exact values under sibling shocks.

These suites ran in owned synthetic previews on macOS arm64 with stock
PostgreSQL 18.7, .NET 10.0.402, Node 24.22.0 and Chrome 157. The standalone
hourly interval reads retain their existing context contract.

The browser As of check opens two tabs sharing one sign-in and prices different
synthetic closes. It applies different cutoffs, restores the first close with
Back and releases a delayed date-strip response. The restored contracts and
prices match, and the other tab keeps its selection. The PostgreSQL integration
check counts all four financial context tables across initialization, Apply,
Back and return to Working data. The count increase is zero. A refused date
mapping save also adds zero. A successful save adds one related row in each
table, and retrying from another tab returns the original receipt. The two-tab
checks also restore a historical close after returning to Working data. The
combined calendar, close-session and As of suite passed 71 browser checks.

In the parametric Risk qualifier the selected source had zero stored receipts
before the job. The worker reproduced its history, deltas and financial
report after submission. A failed save rolled back both the source and the
clock receipt. A changed clock, foreign transaction or expired authority could
not consume the issuer's attestation. The historical assistant turn also
completed from its saved source. These are functional checks on synthetic
data. No browser latency comparison was made in this change.

On the earlier schema, a browser check traced the shared header with one existing context
in the synthetic catalog. Bootstrap and viewer profile selection left the count
at one. Opening the workspace increased it to two as the browser requested
current knowledge from `date-schedules/context`. Expanding As of details kept
the count at two. Choosing **Use current knowledge** increased it to three
before Apply. Applying that exact cutoff through `market/context` reused the
existing row. Closing the browser left the count at three.

## Heat-rate calls and commercial gas storage

The [heat-rate call checks](heat-rate-call-option.md#verification) cover daily
exercise schedules, valuation from power and fuel forwards, booking, spread marks
and marked or unpriced positions. The [gas storage checks](gas-storage-contract.md#verification)
cover ratchet and inventory feasibility, proposal optimality on the stated
lattice, booking, plan adoption and valuation. Both product guides identify
the ticket interactions checked by hand and the remaining browser and risk work.

Migration 0372 has native and HTTP regression checks for re-recording a fact
after a correction. A fresh request for the original fact must return the
original revision, leave the correcting revision in force and add no duplicate
fact. The spread-mark suite also submits a mixed-month batch and checks that
a stale distinct value rolls back the whole batch. See the native checks for
[spread marks](../tools/check_spread_mark_storage.py) and
[storage plans](../tools/check_gas_storage_contract_storage.py) and their HTTP
counterparts for [marks](../tools/check_heat_rate_call_http.py) and
[plans](../tools/check_gas_storage_http.py).

## Asynchronous read qualification (D404)

Owned disposable previews passed 32 asynchronous HTTP checks and 11 workbench
browser checks, including actual held SSE replies, mixed database sessions and
navigation while curve/trading/Risk reads wait. Hosted HTTPS checks passed with
real sign-in cookies, logout and session expiry during blocked work. Additional
owned browser suites passed 170 Risk, 295 assistant, 101 scheduled-swap and 35
Dashboard assertions. Live correction checks passed all 40 original assertions
with an additional forced replay while its generation was running.

These are local synthetic correctness results on stock PostgreSQL 18.1,
.NET 10.0.402, Node 24.7 and Chrome on macOS arm64. They do not establish
production capacity or tail latency. Required CI and release gates still apply.
The [read transport qualification command](async-reads.md#qualification) is part
of the workspace partition. Dated records below preserve earlier results.

## Ordinary reads outside the session gate (D419)

Measured on 16 November 2026 with `--page-read-benchmark` on one Linux machine
(x86_64, 2 logical processors, 7.8 GiB, PostgreSQL 18.7, .NET 10.0.402; client,
the launcher's Debug API build and the database together, loopback HTTP). Workload:
the standard synthetic example at its 2020-12-18 frame, read at one retained
context by the risk profile: 24 positions in 24 trades over
5 unique contracts (1 future, 4 financial swaps); the Risk read is one
factor bump, 3 scenarios, 5 unique valuations and 11 unit pricer calls. Each
figure is the median of 7 samples after one warm-up. The API's sampled peak
resident memory was 348 MiB before and 337 MiB after.

The wait is `GET access` issued 50 ms after an ordinary `portfolio/risk` read
starts, from the same session and, as the control, from another session.

| Wait behind a 608 ms read | Before, ms (max) | After, ms (max) |
| --- | ---: | ---: |
| Same session | 565.9 (591.4) | 7.6 (21.7) |
| Another session | 8.0 (19.5) | 7.5 (10.5) |

| Read | HTTP before, ms | HTTP after, ms | Channel before, ms | Channel after, ms | HTTP bytes | Channel bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `evaluate` | 75.6 | 83.2 | 100.0 | 83.9 | 259,604 | 271,402 |
| `history` | 75.5 | 91.1 | 82.0 | 91.2 | 2,215 | 2,532 |
| `trading/directory` | 19.5 | 33.2 | 34.6 | 30.8 | 1,418 | 1,689 |
| `trading/workspace` | 444.9 | 470.8 | 452.5 | 478.9 | 6,738 | 53,582 |
| `trading/blotter` | 429.8 | 432.3 | 456.7 | 440.4 | 51,525 | 52,114 |
| `portfolio/risk/catalog` | 593.6 | 639.1 | 627.0 | 622.1 | 381,809 | 383,339 |
| `portfolio/risk` | 601.2 | 620.5 | 633.0 | 617.5 | 443,168 | 446,277 |

An ordinary request no longer waits for another read of its session. An ordinary
covered read now authorizes again on its sealed evidence before it answers and
costs about what the channel read costs: 2 to 46 ms more than before in
this run. A second run of the same build differed from this one by up to 35 ms
on a single read, so the smaller differences are within run-to-run variation.
Bytes are the body as sent with `Accept-Encoding: gzip`; only `trading/workspace`
is compressed over HTTP, and no channel result is. This is one small synthetic
workload on one two-processor machine. It does not establish capacity, tail
latency or behaviour under several users.

Correctness on the same machine: the asynchronous workbench check passed 34
checks, five of them new. An ordinary request proceeds beside a waiting read; a
third read waits for a slot and completes; a profile switch refuses a waiting
read; Risk authority expiring during a read prevents its answer; and authority
that ends after a read's own transaction, while its answer waits for the session
gate, refuses it. The first failed before the change, and the last failed
against a build with the publication authorization removed. The live-calculation
check passed, including ordinary history after a live update. Locally,
`--async-reads` passed its three groups, `--assistant-workspace` its 21, and the
web suite 1,955 tests. Browser suites and the complete gate were not run for
this change outside CI.

## Working reads and the publication gate (D420)

The exchange browser suite failed intermittently on slower CI runners at the
step that opens the Blotter's option ticket from the Options page: the page
showed that the operation could not be completed, and the server logged a
database command timeout inside the knowledge capture of a trading read. The
same step passed on a runner where it took about five seconds.

The asynchronous workbench check now holds one session's read of working data
while it values and starts a second session's read of working data, once behind
a Risk catalog read and once behind a futures ticket read. It requires both
reads to reach their own work with nothing waiting on the publication gate.
Before the change the second read waited on the gate and never reached its own
work. With the capture split but the trade head still read in the valuing
transaction, the ticket case still failed that way. With the head read before
the capture, both cases pass with the other 32 checks. Merged with D419's
ordinary-read checks, the same check passed all 36 on the same machine.

The check then holds a P&L run while it reads its two dates and starts a second
session's read of working data. Before the change that read waited on the gate
behind the run. With the run's capture in a transaction of its own, it reaches
its own work with nothing waiting on the gate. Two more checks cover what the
split moved. A run refused for its request, by an unsupported policy or a
repeated book, stores no context: each case failed while its refusal came after
the separate capture and passes with it ahead of the capture.
A malformed run from a profile without the right is denied before its shape is
refused: this failed while the shape was refused outside the authorized
transaction. All 39 checks passed on the same machine. The standard example's
swaps are not booked for a complete report, so the held run reads its dates and
ends in a refusal of its own: for its market revision or for its accounting
coverage, by which swap the Blotter lists first. Complete reports are qualified
by the cash and P&L check on the exchange example, which passed on the final
code on the same machine, including saving and re-reading a report.

In owned disposable previews on one two-processor Linux machine with PostgreSQL
18.7 and .NET 10.0.402, the live-calculation check and the latest-risk-run check
passed on the final code, and the complete exchange check passed on the capture
split before the head was moved. Locally, `--trading-read-selection`,
`--async-reads` and `--assistant-workspace` passed.

This shows that a working trading read, with or without a head trade, no longer
holds the gate while it values, and that a P&L run no longer holds it once its
dates are captured. It does not measure throughput or memory with several
valuations at once, and the readers D420 lists as unchanged still hold the gate
for their transaction. Browser suites and the complete gate were not
run for this change outside CI.

## Market-date read batching (D426)

Measured on 17 November 2026 with `--market-dates-reads-check` in an owned
disposable preview. The control's application sources match `cb582382` exactly;
its native readers precede migrations 0363–0364. Both runs use the same fixture
and table schema; unused migration-0362 batch helpers also exist in the control.
`--market-dates-benchmark-only` retains business-answer assertions but skips the
new work/scaling and disclosure checks. Control and changed runs were sequential
on one Linux x86_64 machine (kernel 5.15, glibc 2.39), with stock PostgreSQL 18.7,
.NET runtime 10.0.13, SDK 10.0.402 and Node 24.22.0. The container is limited to
two CPUs and 3 GiB. The launcher's Debug API, HTTP client and database run together
over loopback. The hosted demo's 330 ms plain-request round trip is not emulated
or added to these results.

These timings precede the commodity-icon schema integration described below;
they have not been remeasured on that combined schema.

The synthetic session has 96 recorded closes: two qualify `bridge_basis` and 94
omit it from their recorded roots. They span one definition revision, 95 unique
contracts, 6,978 recorded pins and 281 distinct observation IDs, with 72–105 pins
per close. The curve request performs two matching frame qualifications and
rejects 94 candidates by exact root/rate choice. Workspace qualification visits
the same 96 matrices and their selected commodity/rate curves. There are no Risk
scenarios or trade PV evaluations in this workload. Last-closes stops when the
listed curves have their newest qualifying close; it does not exhaust the
calendar merely to count unavailable dates.

Each timing is the median of three requests. Each full-curve size has one warm-up
first; other reads follow the 96-close curve samples. PostgreSQL duration logging
is enabled identically in both runs. Counts include every top-level runtime-role
statement in the request: discovery, identity transactions and final publication
reauthorization. Parse/bind messages and statements inside native functions are
not counted. Host scheduling and JIT/GC cause run variation; this is not a tail
latency or capacity qualification.

| Request | Closes | Before ms | After ms | Before statements | After statements |
| --- | ---: | ---: | ---: | ---: | ---: |
| One curve, full calendar | 24 | 508.7 | 160.4 | 388 | 101 |
| One curve, full calendar | 48 | 668.4 | 202.6 | 628 | 101 |
| One curve, full calendar | 96 | 986.9 | 292.7 | 1,108 | 101 |
| One curve, `maximumDates:1` | 96 | 316.9 | 190.9 | 158 | 101 |
| Whole workspace, full calendar | 96 | 1,147.1 | 447.5 | 1,119 | 112 |
| Last-closes | 96 | 227.5 | 178.6 | 106 | 112 |
| Navigation | 96 | 153.8 | 57.6 | 18 | 17 |

All five reads improve in this run. The full curve removes 1,007 statements at
96 closes and is about 3.4 times faster; workspace is about 2.6 times faster.
Last-closes improves more modestly and includes fresh publication authorization.
Warm discovery reuses immutable native inputs after fresh RLS headers instead
of rematerializing their complete payload. Execution still reattests every
calendar candidate it consumes. Native matrix admissions now check the one exact
source four times rather than 384 times. Two hidden-day reads previously executed
their STABLE function four times and performed 768 source checks; scalar array
projection and the all-visible proof reduce those counts to two and four.
Actual hidden or unsupported cases retain the authoritative legacy query.

For the full curve at 96 closes, median summed database duration is 625.0 ms
before and 248.5 ms after; sampled API process CPU is 330/70 ms. Workspace is
681.8/299.0 ms database duration and 490/270 ms API CPU. CPU uses Linux clock ticks
with 10 ms resolution and includes all API threads; it is not additive with
database duration or wall time. Native header/head lookup and dated catalogue
work still grow with the facts and dates checked: constant statement count is
not constant native CPU. The API high-water resident set at the measurement
point is 304.0 MiB before and 301.4 MiB after, including preceding qualification
requests. This shared-pin fixture does not qualify memory with many distinct
sources/pins; request-local unions and proof sets retain their actual rows.

Checks were written and watched failing before the relevant changes. The
unchanged full-curve counts are 388/628/1,108 for 24/48/96 closes. A stronger
limited workload selects the commodity on every close but uses distinct invalid
future pin sets on older candidates: the first batched implementation still used
127/151/199 statements. The final implementation uses 105/105/105, with two
available dates and every other date independently unavailable. Missing pins
remain local; an unvisited denial or prefetched fact cannot poison a complete
limited answer. Warm matrix call counts changed from 2/2/2/1 to 1/1/1/0 for full,
latest, last-closes and navigation. Native work counters independently enforce
the source-check and hidden-function bounds above.

Business projections captured from unchanged code match for one curve,
workspace, rate curve, retained context, replaced close, hidden day and denied
reader. Random identities, capture instants and frame-catalog capture metadata
are excluded; the original suite separately checks response contracts. Independent
evaluation supplies mixed-source metadata refusal oracles. Unrequested rate-owner
commodity terms are not materialized, and supplemental metadata errors retain
their date-local reason. Native catalogue errors retain the calendar's legacy
whole-read `500` and last-closes structured refusal.

Direct scalar/set admission comparisons cover current and historical contexts,
eligible subsets, mixed sources and unchanged bound contexts. Scalar body drift
uses the original predicate; an always-true scalar cannot remove independent
missing-ID refusal. Runtime cannot execute the private helper. Hidden projection
checks cover empty sessions, 129 days, hidden heads with visible predecessors,
the unchanged public 128-ID limit, exact date formatting and legacy NULL output.
Initial-deadline checks cover catalogue, matrix/pin fast and fallback paths, and
both hidden-day paths with a live prepublished successor grant. Migration guards
refuse body, owner, configuration, ACL, strictness and parallel-safety drift and
work under an independent caller search path.

Held-read checks show an ordinary same-session request proceeds within one
second; authority ending during the read refuses disclosure and replacement
returns `close_replaced`. Fresh native header RLS also refuses loss of authority
to an unconsumed supplemental part. Independent adversarial review findings were
resolved; the reviewer did not independently execute DB/browser checks. Local
`--preview-data` passes 45 checks plus preparation checks; `--async-reads` passes
three groups. Runner/impact tests pass 45/17 and the standalone boundary passes.

After integration with `origin/master` at `9d6fe377`, the final native-reader
code passes regional-close HTTP/native/managed checks, context recovery with
14 browser assertions, async-workbench with 39 HTTP and 11 browser assertions,
and the original calendar suite with managed demand checks and 65 browser
assertions. The new upstream position-adjustment check passes all 40 assertions.
The integration changes no market-date runtime code or native functions.

Newest-close refresh qualification separately holds an older workspace response,
sends two focus events while it is active, then releases it. The original page
fails because it sends no follow-up; the coalescing fix sends exactly one, offers
the newer close and preserves the selected close. The calendar/regional browser
suite passes all 65 assertions in a disposable macOS PostgreSQL 18.7 preview;
the 17 newest-close unit checks also pass. The full standard preview passes,
including all 1,277 browser assertions and its native/HTTP checks. A separate
Linux disposable run passes the same full standard preview with the Dashboard's
explicit calendar-completion wait. Independent review of the pending-refresh
and scope/disposal fences found no blocking issue. No timeout was increased.

Integration with `origin/master` at `e76bce75` preserves its commodity icons
(D425, migration 0361) and assigns calendar batching D426 and migrations
0362–0364. All three calendar SQL bodies are byte-identical to the previously
qualified versions; the market-date implementation is unchanged. Focused local
qualification passes the API build, 45 managed preview checks, 38 navigation,
board and newest-close frontend checks, 45 runner and 17 CI-selection checks,
and the 65 calendar/regional browser assertions on the combined schema.
The owned read check also passes migration guards, answer equivalence, native
admission and original-grant deadline cases before its measurement stage stops
at Linux-only `/proc` instrumentation on macOS. It does not supply new timings
or a complete scaling/gate qualification for this integration. Independent
integration review found no blocker. The complete local matrix was not repeated.

The complete CI matrix, hosted deployment and curve-click timing, broad
concurrency/memory capacity, page request sizing and evaluation payload reduction
are not qualified by these measurements.

## Nested custom-curve previews

Local comparison on 2026-11-18 against master at `6fbda875`. Each build served
its own disposable, equally seeded database on loopback. The synthetic market
contained 3,031 contracts, including four added products with 750 contracts each,
32 daily closes and 20,576 added observations. Each custom curve returned 76
delivery periods. Both processes used Release builds on the same Apple M4 Pro
with 48 GiB RAM, macOS 26.6.2, .NET SDK 10.0.402 and PostgreSQL 18.7.

The table measures the authenticated preview HTTP request through receipt and
JSON decoding. Each workload had one warm-up and three timed requests per build
with alternating execution order. Times are medians, with the observed range in
parentheses, in milliseconds. Both builds returned the same displayed rows and the
same dated contract IDs and exact input prices for all six workloads.

| Workload | Input rows | Master ms | Changed ms |
| --- | ---: | ---: | ---: |
| Direct source, context date | 76 | 230 (227–230) | 174 (172–175) |
| Native base plus basis, context date | 76 | 433 (428–441) | 175 (172–176) |
| Direct source, 30-date mean | 2,280 | 1,332 (1,298–1,366) | 333 (324–340) |
| Native base plus basis, 30-date mean | 2,280 | 7,560 (7,550–7,601) | 459 (457–470) |
| Four levels of published recipes | 304 | 1,405 (1,404–1,444) | 299 (298–314) |
| Two published 30-date means combined | 4,560 | 2,512 (2,470–2,550) | 523 (511–524) |

The repository's smaller 12-contract, 20-date rolling average issued 20
historical observation statements per request on master and one on the changed
build in all three repetitions. Its median fell from 223 to 84 ms. The owned
`--custom-curves-check` enables statement logging and checks that count. It also
selects a 240-reference preview budget, admits the original recipe, and refuses
a weighted panel that consumes the samples twice.

An independent in-process comparison evaluated 76 outputs in a base scenario
and a single primitive shock. Timings below cover preparation, compilation,
base valuation and extraction of its inputs. Two process launches per build
supplied four warmed measurements per workload with .NET tiered compilation
disabled and the application numeric memo enabled. Memory is managed allocation
per operation. The four custom workloads used 380, 684, 1,292 and 304 distinct
primitive contracts respectively. The native control builds the native curve and its lineage.
The runs compared full plan and scenario IDs, exact base and shocked prices and
fingerprints of dated input prices, observation IDs and factors for custom graphs.

| Workload | Evaluated graph nodes | Master ms | Changed ms | Master → changed allocation |
| --- | ---: | ---: | ---: | ---: |
| Four-level chain | 1,368 | 182 | 34 | 128 → 30 MiB |
| Eight-level chain, 3,650 calendar dates | 2,584 | 2,482 | 156 | 925 → 97 MiB |
| Sixteen-level chain | 5,016 | 1,528 | 159 | 1,054 → 142 MiB |
| Four-wide shared graph, seven levels | 5,016 | 3,424 | 157 | 2,349 → 140 MiB |
| Native four-level control | 684 evaluations | 17.6 | 17.6 | 15 → 15 MiB |

All measured fingerprints matched. A separate deterministic run of the same
recipe, contract-observation and date-lookup fixtures on both builds produced
39 identical full plan hashes, including date-point plans. The managed suite and
owned HTTP suites also exercised exact final pins, missing and corrected inputs,
effective and known times, publication authority and retained worker replay.
The managed core (785 checks), custom preview, contract-observation and connected
portfolio/worker suites passed again after bringing the change onto master
`7f202f1e`. An empty historical demand also has a regression check that
requires the original no-SQL path.

With the repository SDK and PostgreSQL 18 utilities on the path, run the
repository checks with these commands.

```sh
dotnet run -c Release --project tests/OpenEtrm.MarketData.Checks -- --connected-recipes
dotnet run -c Release --project tests/OpenEtrm.Api.Checks -- --contract-observation-reads
pg_bin="$(pg_config --bindir)"
dotnet_bin="$(command -v dotnet)"
python3 tools/run_preview.py --dotnet "$dotnet_bin" --pg-bin "$pg_bin" --custom-curves-check
python3 tools/run_preview.py --dotnet "$dotnet_bin" --pg-bin "$pg_bin" --contract-observation-curve-check
python3 tools/run_preview.py --dotnet "$dotnet_bin" --pg-bin "$pg_bin" --connected-portfolio-check
```

These timings describe warmed synthetic server previews on a shared machine.
Browser rendering, cold load, concurrent-user p95, peak process memory,
hourly interval curves and production latency were outside this measurement.

## Current runner

For the current complete command and prerequisites, start with
[Required tools](#required-tools). `tools/check_all.py` runs all suites by
default. CI adds four shared groups (`repository`, `frontend`, `managed`,
`database`) to sixteen integration partitions. Pull requests select
the union of declared path consumers; master pushes and manual dispatch retain
the complete gate. See [impact selection](ci-impact.md) for rules and limits.

Shared behavior checks run once across a full matrix. Every integration job
retains locked preparation in its own checkout and owned disposable resources.
The reference, exchange, assistant, risk, trading, lifecycle, physical, gas,
portfolio, settlement, PPA, recovery, workspace and standard partitions retain
their existing consumers. Runner-contract checks require all full-gate commands,
each behavior command once, and each preview queue bounded to twelve suites.
Jobs keep their 90-minute limit; the existing aggregate check rejects failed,
cancelled or skipped selection/jobs. Lightweight groups do not install PostgreSQL.

Gas qualification is split into `gas-native` (24 native, populated-upgrade and
retained-worker helpers), `gas-browser` (nine browser invocations, including the
provider-only custody case) and `gas` (seven preview workflows). The previous
single job reached its 90-minute limit while the amendment workflow was still
passing, after the earlier native and browser consumers had completed. See the
[timed-out job](https://github.com/zerodrift/leanetrm/actions/runs/44097805646/job/134089647005).
The complete local command list and order are unchanged; each new job retains
the ordinary locked preparation and existing timeout. Orchestration checks cover
complete coverage, exactly-once execution and rejection of failed or cancelled
groups. Local qualification on 2026-11-17 passes all 166 Python checks and
compares all 258 complete-gate commands and their order with the prior plan.
Hosted qualification of the split remains pending.

The integrated runner requires shared, isolated application and partition-specific
native/application checks. The D404 local checkpoint passed 1,866 frontend checks and 122 Python
checks. The earlier calendar/lifecycle checkpoint passed 737 managed core checks,
88 rate checks, 1,806 frontend checks and 121 Python checks; those counts describe
that increment. Calendar migrations 0318–0337 follow
the lifecycle schema without changing any published predecessor migration.
Migration 0321 checks the exact retained schedule views, including invoker
semantics, ownership, ACLs, column grants and rewrite rules. Its parity qualifier
preserves economic values, period order, evidence and required-child refusal
while comparing the explicitly changed calendar authoring shape.

Migration 0336 resolves a calendar visibility cutoff once and checks each distinct
retained publication once per invocation. Native checks independently count that
work, refuse authority that expires during a proof-relation lock wait, and reject
helper definition or privilege drift. The proof retains its exact publication
transaction, sealed reference states and original knowledge boundary.
Series-filtered exact observation frames stream in order while checking every
original pin, including pins outside the requested series. Native checks cover
partial and empty payloads, unavailable pins, later-frame faults and authority
expiry. Selected-trade P&L checks refuse a hidden latest head and remain unchanged
when an unrelated instrument is hidden.
Migration 0337 selects the exact scheduled trade revision before Side aggregation.
Its work check requires two Side calendar reads for one two-Side trade and none
for an absent revision. Native parity retains amounts, dates, evidence, ordering,
required-child refusal and caller settings. Both runtime and worker checks retain
the invoker view's table and all five consumed-column permissions.

The fixed-loan-fee consumer qualifies migration 0308 through native, managed and
real browser paths on stock PostgreSQL 18.1 with synthetic data. The gas
partition runs `tools/check_gas_fee_browser.py` with the selected SDK/browser and
`tools/check_gas_fee_existing_origins.py` for the separately seeded predecessor
schema. Thirteen deliberate producer faults include amount/date/currency/reference
and outcome corruption, missing canonical fields, and unrelated resource effects;
they refuse at immediate constraints and actual commit. Permission expiry during
publication waits, final command seals and final source-helper work also refuses.
Exact cash/bank membership oracles produce PV −2.375 after a fee amendment and
+0.475 after an explicit zero, using a selected 0.95 discount factor. Fifteen
actual API/Chrome checks cover local-time entry, desktop/mobile review, retained
first outcomes after a lost response and reopening with fresh explicit choices.
The upgrade preserves every predecessor gas financial and command-receipt row.

The side-observation preview checks dependency-specific read authority: revoking
the scheduled-swap projection leaves ordinary swap terms, history and PV intact;
revoking either observation relation still denies a swap that retains that
observation schedule. Migration 0297 keeps the conditional invoker-function
boundary around scheduled terms while hydrating each selected period by its exact
keys. Its join planning setting is confined to the schedule helper. The native
retained-swap qualifier checks predecessor parity, caller-setting restoration,
required-child refusal and worker expiry separately.

D401's local native futures increment has focused owned PostgreSQL consumers:
`tools/check_futures_margin_sources.py`, `tools/check_futures_margin_adoption.py`
and `tools/check_futures_margin_journal.py`. Each takes `--pg-bin`; its managed
child uses the explicitly selected `DOTNET` executable and already-built API check
project. The lifecycle CI partition runs all three. The journal consumer qualifies
actual booking/adoption, causal corrections, distinct surviving cash, partial
payment, terminal PV without an open mark, explicit future-cash discounting,
review/source changes, original admission deadlines and immutable run membership.
The same consumer now checks the explicit native-margin scoped portfolio,
ordinary connected PV, exact cash provenance and retained accounting coverage.
A wrapped-review regression fails without transaction-owned admission and passes
with it; successor grants cannot extend its original read deadline.
`tools/check_trading_business_date.py` qualifies explicit business-date reads and
immutable command source admission, including read-scope restoration, source
conflicts, forged deadlines and successor grants. Migration 0258 refuses drift in
the exact native source helpers it updates. These checks preserve the existing
recorded-close calendar restrictions rather than qualifying broader close sessions.
It also inserts separate privileged synthetic contexts with inconsistent prefixes
to prove refusal, without modifying immutable records or the database software.
These are native/managed checks. The [remaining lifecycle gates](lifecycle-delivery.md)
include whole-portfolio/worker, physical delivery and HTTP/agent/browser workflows.

D354 adds the owned `--context-read-check --browser-check` consumer for the
grouped header, context explanations, failed-Apply recovery and retained knowledge
across pages. The `--regional-closes-check` consumer also runs managed native
point/frame/fact reads, predecessor/dependency guards and adversarial SQL. It
checks strict-subset frame completeness, actual two-frame grant expiry and fresh
successor reads, hidden anchors, full-close validity and concurrent replacement
close/correction publication. Broad observation enumeration is deliberately made
unavailable during the native context/calendar and managed selected-read checks.

Focused combined-source qualification passed 1,437 frontend checks and 14 context
recovery browser checks. Header checks cover visible grouping, native help
disclosures and phone layout. The cursor tracking check waits for its observed target
position instead of assuming the first animation frame completes within 60 ms.
Market-board checks reveal off-screen cards before requiring their optional
shapes, retaining exact curve coverage, unique reads and the 64-curve batch bound.

In the local synthetic workload of one requested fact and a 40-pin selected
close, adding 4,096 unrelated observations left fact work at 44/44 observation
tuple reads and point work at 42/42 under both custom and generic plans. These
are scoped work counters, not a latency or general history benchmark. The
combined release still requires the complete command below, positive worker
consumers and separate restore/upgrade qualification before production activation.

D355 adds the actual managed billing-read work check to
`--physical-assistant-check`. Its custom/generic plan matrix holds each certified
context across 512 unrelated curves and compares matching fresh/analyzed
maintenance states. It requires exact native result/order and per-relation work
bounds, allowing at most one physical scan of the added curve rows. The owned
fixture holds automatic maintenance, checks unchanged non-curve planner inputs
and restores exact table settings in finally; the analyzed baseline covers all
RLS dependency statistics. The old reader still fails this isolated work check.
The ticket's held-choice checks also require Save to remain disabled until its matching preview
arrives. Native amendment and physical gas browser checks passed 5 and 20 scenarios
respectively; these focused results do not replace the complete gate.

D344's managed `--assistant-workspace` checks consume the advertised fill
encodings to build plans for both supported ask forms. They preserve the exact
capability keys, a 4 KiB metadata bound and 2 KiB guide bound, and reject human
labels as plan values and unstated prerequisites. Run the existing owned
`--assistant-workspace-check --browser-check` and separate
`--assistant-runtime-check` consumers. Guidance-only changes do not establish
real-model GUI success from a scripted fixture.

D341 local UI qualification passed 1,379 web checks, including stage/day dirty
rollback, explicit historical action gates, failed-refresh identity clearing and
compact phone headers. The fresh stock PostgreSQL demo passed native manual gas
and billing scenarios; owned browser checks passed 10 scheduling/recovery and
15 cash-correction scenarios, plus six desktop/phone presentation checks.
Independent adversarial review verified the repaired context/refresh guards and
reran the focused controller, presenter and billing checks. No native financial
contract, schema, authorization policy or agent operation changed in this layout.

D340 moves physical work into **Scheduling → Physical operations**. The Blotter
keeps pricing, retained instrument overviews and read-only Side schedules. The
operations directory reads exact booked metadata without PV, refuses missing held
source metadata and follows the header context. Gas and crude reuse their native
workflows; gas billing has its own tab. Gas/crude/LNG overview presenters share a
physical outline with commodity-specific saved conventions. LNG operational
actuals and other unsupported commodity workflows remain explicitly TBD.
Migration 0207 / assistant `turns_v20` add page admission and native discovery;
financial contracts, permissions and persistence remain their existing owners.

D340 local qualification passed 1,376 web checks, 85 Python tooling checks and
managed API schedule/page checks. A fresh stock PostgreSQL fixture passed 36
physical assistant scenarios, including captured-context metadata-only discovery
and approved native service configuration from the new page. Independent review
checked native receipt identity, actor/context fencing and source completeness;
malformed or mismatched success/status responses retain their original command.
Owned native gas/billing scenarios verify immutable corrections and current
authority after observed storage waits. The relocated crude browser suite passed
47 receipt, dispatch, cash, correction, conservation and recovery checks. Gas
operations, cash billing and read-only ticket inspection passed another 10, 15
and 23 owned browser checks, including a separate trade window.

D339 adds [manual gas delivery operations](gas-delivery-operations.md): versioned
service accounts, nomination/retro requests, separate external acknowledgments,
reviewed schedule adoption, signed operator statements and paired imbalance
agreements. The dedicated gas workflow groups before flow, scheduling,
after flow and reconciliation. It reuses native daily quantities, facility
allocations, gas billing and the Blotter's retained-command transport. Migration
0206 and assistant `turns_v19` require separate release qualification. Live
connectivity, portfolio imbalance positions and accounting posting remain outside
this bounded consumer; new permissions require explicit grants.

D339 local qualification uses stock disposable PostgreSQL and synthetic contracts.
The native scenario verifies service setup, before/intra/retro classification,
100 requested / 95 confirmed / 90 scheduled adoption, stale heads, canonical
semantic retries, signed statements, explicit movement coverage, paired concurrent
acceptance, -600 owned cash and a preserved -50 corrected deficit. Raw ingress
refuses nonfinite/fractional/unbounded exact-cash components and historical write
bodies. Real observed transfer/alias/known-status/unknown-status storage waits
refuse expired authority without leaking facts or receipts. A ReadTrades-only
reader cannot read private operations, even with an added permissive policy;
public quantity projections retain generic provenance.

Eight real browser checks cover the four stages, sole account selection, trusted
Keep editing, native retro review/invalidation and lost committed response recovery
through the shared retained card with one submitted fact. The desk screenshot was
visually inspected. Thirty-five physical assistant scenarios include exact pinned
manual preview, proposal without writes and separately authorized native setup
with replay. The tightened native/agent scenarios, 1,372 web checks, 85 Python
checks, managed core/API checks and v18 turn/tool/stream/usage regressions pass.
These are bounded local results; production pipeline connectivity, portfolio
imbalance positions and accounting posting are not qualified by these checks.

D338 adds [final daily gas cash determinations](gas-cash-billing.md): an explicit
completed-day/fixed-delivery consumer with original and correcting contributions,
exact accepted source links and reviewed native/agent operations. It reuses swap
cash-restatement arithmetic while preserving crude's cumulative invoice contract.
Migration 0205 and assistant `turns_v18` require separate release qualification;
invoices, payments, accounting journals and contractual PV changes are excluded.

D336's owned `--check --regional-closes-check` fixture includes the actual
managed exact-pin reader, archived 0202 RLS row/order equivalence, working and
retained contexts, separate sessions, a broad-enumeration refusal probe and one
complete-close visibility decision for 40 pins under custom/generic plans. It
rejects changed SELECT policies/roles/RLS flags, revoked table SELECT, a risk
role without a live claim, invalid unrequested close pins, fallback to an older
close and expiry while results buffer. After 4096 unrelated later facts, bulk
observation tuple work equals the archived reader (420/420 in both plans), and
exact reads still do not invoke broad enumeration. The 0204 guards reject helper
and complete policy drift. The standard HTTP fixture also forbids broad
enumeration while the actual single-bundle futures catalog reads its exact pins.
The regional-close fixture runs in the complete gate's workspace
partition. Standard browser, assistant workspace/runtime and risk/close worker
suites qualify their native consumers. These are synthetic disposable checks;
production GUI observations are not public fixtures or performance claims.

D335's owned workspace checks cover controls-only digests without record/pivot
evidence, native swap entry without portfolio valuation, explicit unsaved swap
fields and human-only Preview/Book/Authorize. Browser checks cover aim before
mutation, missing/covered controls, late overlays, Stop during aim/read, nested
inline controls in native sheets/modals and confirmed navigation. The final
control remains briefly visible; panel close stops the run. The 0203 upgrade
checks refuse changed bodies, owners, ACLs and configuration for all three
replaced assistant helpers. Run the workspace and runtime preview flags
separately; use the complete command below for release qualification.


D328's managed workspace regression uses real grouped ASP.NET routes and bounds
route construction independently of elapsed time: one metadata snapshot and one
build per registered operation. It fails on the prior repeated enumeration. It
also checks fresh permissions/endpoints and cancellation before metadata work.
The owned workspace HTTP fixture discovers the full authorized catalog while
retaining command approval effects; its browser follows full discovery with
native New swap navigation. Use the existing `--assistant-workspace-check
--browser-check` command below. These checks use synthetic disposable resources.

D325–D326 add actual frontend-report / prompt-API / SQL catalog parity, empty
identity-only asks and immutable prompt pins for Dashboard, crude setup and Vol
surfaces. Managed checks reject browser values and Blotter-only fields on these
pages. Web browser checks cover cursor tracking, reduced motion, minimize/close,
late controls, replaced highlights and native modal Review controls without
consent or authorization. The workspace browser also checks the native swap field
anchor. These are synthetic owned checks, not production business tests.

D319 regression qualification uses the owned `--assistant-workspace-check --browser-check`
suite. It covers New swap operation discovery, page handles and shared numbering,
empty/bounded reads, native navigation, walkthrough go/pause, booking refusal and
Stop during an outstanding catalog read. `--assistant-runtime-check` separately
checks v15 admission and v14 refusal; run it separately from the workspace flag.

D320 adds injected initial-control-refresh failures to the owned assistant and
`--live-calculation-check --browser-check` suites. They check main-window recovery,
live Retry and pop-out recovery, plus native pointer Stop during a held catalog
read. No production reproduction is implied by these synthetic regressions.

The owned futures/swap browser fixtures normalize the virtual positions viewport
through `positions-scroll.mjs` before checking its first rows or group headers.
They allow 500 ms for pending retained-row placement, then make at most five
native scroll attempts before requiring the viewport at the top. The exact quantity/display and two-subtotal
assertions remain unchanged; this is fixture positioning, not a financial rule.
The pop-out Keep/Discard check captures its displayed context after the native
ticket-opening read and immediately before the proposed date change.
PPA and Dashboard reloads require a new document before checking controls or
request counters. The Dashboard read-count case leaves a restored Dashboard
before measuring its next explicit opening.

D312–D315 page-digest, selection and walkthrough checks run with
`python3 tools/run_preview.py --pg-bin /path/to/postgres/bin --dotnet /path/to/dotnet --assistant-workspace-check --browser-check --browser /path/to/chrome`.
The managed `--assistant-workspace` checks run first; the owned HTTP fixture then
qualifies consent, exact retained reads, plan publication/refusal and source hiding.
The owned browser exercises administrator tool choices, selected ids without cells,
the visible plan, unsaved fills, Stop and later-plan suppression. Web checks cover
closed events, pending-step cancellation, competing input, deferred Review and the
separate Authorize click. `--assistant-runtime-check` qualifies the ordinary turn
loop and rejects legacy runtime admission. The complete command below includes
this owned browser gate in the assistant partition. Optional
`OPENETRM_ASSISTANT_BROWSER_EVIDENCE` copies synthetic screenshots outside the tree.
No complete release verification or publication is implied by targeted local runs.

The workspace check for a delegation that ends during a held command no longer
arms a three-second term and sleeps. An approval spends about three seconds in
the API before its command transaction begins: two route-table enumerations of
1.3–1.8 s each over 518 endpoints on an idle four-core container, against about
70 ms of database work. The armed term could therefore end before the command's
first guard, and the check stopped on a denial it had not staged; the unchanged
check stopped that way on its first idle local run. The fixture now keeps the
term until the command is observed waiting on the held membership row inside
`security.apply_administration`, then moves `granted_to` to the instant of that
write. The held command owns the epoch row, so that one statement runs with
ordinary triggers skipped; this is fixture staging in place of the clock, not a
supported control change. `--assistant-workspace-check` then passes its 90
integration checks, and passes again with twelve CPU-bound processes started
around the scenario, which stretched it to 12.4 s. It fails, with the command
committed, against a scratch build whose final guard is removed and against
`assistant.turn_authorized` comparing the term with `now()`. Those runs used
PostgreSQL 18.7 without the browser suite. With six CPU-bound processes for the
whole run the suite stopped earlier, at the 20-second turn deadline of the Risk
page read. Other suites still arm a two- or three-second term before a held
operation (`Contexts.expiring` and the asynchronous read, market date and
storage authority checks); they were not changed.

The native volatility-fit admission regression runs with
`python3 tools/check_vol_surface_admission_expiry.py --pg-bin /path/to/postgres/bin`.
It uses an owned stock PostgreSQL cluster and synthetic fixtures. Both request
replay and queued-key reuse wait on observed advisory locks until the bound token
expires naturally, then refuse without recording a reuse alias. Valid creation,
replay and reuse retain the same job. The regression failed against migration
0187 and passes with 0188; it is included in the synthetic verification runner.

## PCA and hybrid qualification (D308)

`--pca-hybrid-risk` in the managed checks exercises independent analytic
2×2 eigenpairs, zero/singular/tied covariance, native-unit rescaling, complete
covariance and every-shock reconstruction through 128 factors, large low-variance
spread exposure,
closed-form exact quadratic diagonal/mixed coefficients, genuinely nonzero mean
drift, nonsmooth derivatives, opposing shared bindings, whole-portfolio offset
errors and exact collinear/mixed native directions. An independent standalone
inverse-CDF/fractional-tail oracle and coprime-denominator arithmetic-limit case
qualify the optional reduction boundary. A tiny native tick has an
explicit fitting-only refusal without consent, or an unavailable-analysis
full-PV receipt when the user opted in.

The existing owned `--monte-carlo-risk-check` suite also exercises optional PCA
policy/direct-worker parity, byte-identical reference generation/risk/matrix with
analysis selected, complete added work admission, native SQL policy mutations and
migration 0191–0192 body/owner/ACL/search-path guards. Consent checks cover
default refusal of holding and portfolio error, fitting-failure refusal, opt-in
substitution, separate study identities and direct/worker consent parity. Legacy
new work refuses; immutable legacy reports remain readable with their policy label.
Frontend readers require complete
loading/projection/fitting/model/classification evidence and enforce the selected
numerical residual bounds. Cash and PCA diagnostics use the shared backend amount
formatter; exact receipts remain unchanged. The browser suite adds
visible unchecked fallback selection, explicit opt-in, comparison/residual
graphics, measured work, retained
queue/export/reopen and shared Blotter/phone checks. Fifteen Monte Carlo/PCA,
fourteen historical and sixteen parametric owned browser checks pass. Held-display
close and cached-navigation tests prove stale formatting cannot revive a report or
block its current replacement. All runs use disposable
resources and neutral test inputs. This is qualification coverage, not measured
production throughput or proof about unpriced future paths.

## Hybrid Monte Carlo valuation (D311)

`--hybrid-monte-carlo-risk` adds seven managed numerical groups: unchanged native
R6 shock goldens, independent Python SHA256 panel-index goldens and boundary seeds,
exact quadratic full-N PV comparisons, strict mode separation and interval
suppression, omitted residual-curvature refusal/opt-in, deliberately unpriced
black-box tail spikes missed by a successful panel, and declared quote-domain
admission without PV or parent mutation. Existing eight R6 and nine D308 groups
remain green. API checks cover complete method/analysis/panel tuples, exact
captured base keys/units/values and independently computed output/fitting/panel/
worst-case fallback capacities. Frontend mutants reject policy, evidence,
classification, consent, interval and completed-check/fallback contradictions.

The owned PostgreSQL 16 `--with-exchange --monte-carlo-risk-check` suite compares
all main values and native partitions with a separate full-PV futures run, all-panel
controls, exact economic direct/worker replay, immutable source/clock evidence,
current authority, leases, cancellation, missing-source refusal and unchanged
observation RLS. Migration 0193 rejects altered predecessor function bodies,
owners, ACLs and search paths, and SQL admission rejects incomplete, unknown or
mixed execution/fitting policies. The nonlinear listed-option case uses an
explicit zero-error policy to exercise immediate/worker strict refusal and
consented complete fallback against a separate all-path full-PV run. Failed jobs
have no successful result. Archive metadata retains the applied method.

The 25-check owned browser suite exercises explicit method/panel selection, initially unchecked
fallback, model versus observed-full-PV graphics/drilldown, honest work and
coverage, absence of an inherited interval, exact export, queued/archive/reopen,
rejected contradictory fallback evidence and phone layout. The existing
full-PV/PCA and Blotter stale-formatting checks remain in that suite. A standalone
real-browser widget check also prevents the shared header from calling model
values full PV. All 1,317 frontend checks pass locally. These checks
qualify the bounded versioned contract; sampled acceptance does not prove
unpriced-path or full-tail accuracy. Production activation is separate.

### Local workload measurement — 4 November 2026

One held futures trade, one unique valuation/market contract/factor, one native
partition, 1024 identical seeded native paths, one retained component and 64
validation paths. Every approximate value and portfolio tail matches a separately
priced full-PV reference on this linear workload. Full PV executed 1025 scenario
and unit-PV calls; hybrid executed 78 (13 fitting including base and 65 validation
including base), with 1024 predicted holding values counted separately. There
was no fallback. Both include complete history/preparation and output work.

On macOS 26.6.2 arm64, PostgreSQL 16, .NET SDK 10.0.402 and Python 3.14.2, three
alternating warm HTTP requests after warm-up had median total roundtrip times
0.216 s full PV and 0.211 s hybrid. Separate fresh worker runs, including process
startup, claim, reconstruction, calculation and persistence, took 1.102 s and
1.209 s. Process RSS sampled every 50 ms peaked at 222960 KiB and 203376 KiB
respectively. One process per method is not a memory comparison; sampling can
miss the true peak and does not measure the API heap. This cheap-pricer workload
shows fewer PV calls with similar total time. It establishes no general speed,
memory or production-throughput improvement. The owned runner emits complete
work/environment/timing/memory evidence; generated artifacts stay private.

## Monte Carlo full-PV risk (D307)

[The Monte Carlo contract](monte-carlo-risk.md) is exercised by eight managed
numerical/scenario groups and two API contract/setup groups. Independent Python
oracles check fixed SHA/polar rejection vectors, exact hand-computed LDL,
168 high-precision Gaussian shock pairs, seven exact native golden vectors and
224 binomial count/confidence cases, including 18-place cutoff neighbors. Structural reader checks refuse deleted source
change vectors, impossible covariance rank and ambiguous numeric seeds. Checks
include signed Gaussian linear risk,
singular/interior-zero/zero-rank covariance, unlike native units, nonzero native
shock underflow, prefix/batch reproducibility, nonlinear sibling PV and an actual
positive-forward option-tree domain failure without partial results or parent
mutation. Existing full managed core and historical/parametric numerical checks
remain green locally.

Owned stock PostgreSQL and real worker checks compare every retained shock,
scenario loss, empirical tail/allocation, prefix diagnostic and study identity
between immediate and queued execution. Independent Fraction reducers cover
selected and mixed native partitions; an ordinary joint scenario reproduces the
MC native mapping. Qualification includes strict policy/seed/axis admission,
method-separated/common archives, current access, immutable clock/source custody,
active leases, cancellation, unchanged observation RLS and missing-source refusal.
Migration 0190's body/ACL/owner/search-path guards refuse altered baselines.

The 12 owned browser checks exercise guided EOD official-close preparation,
visible scope-aware paths/seed, full-PV widgets, path drilldown, source reuse on
reseed, rejected incomplete simulation evidence, reauthorized exact export,
definite first-attempt queue refusal, queue/archive/reopen, phone width and the
actual Blotter reader. Captures are
visually inspected on desktop and phone. A chart regression failed before adding
explicit simulation/loss category labels and bounded approximate tick labels;
financial report values and tooltips stay exact. The frontend suite has 1,303
passing checks and the Python runner has 85. These are local qualification
results, not production throughput or deployment evidence. Generated qualification
artifacts, operational logs and credentials remain
outside the repository.

## Historical full-PV risk (D305)

The [historical contract](historical-risk.md) has independent exact empirical
quantile/fractional-tail and tie-allocation checks, signed/offset/point-mass cases,
complete exposure/sample refusal and 513 nonlinear sibling-PV samples. The API
checks cover exclusive method envelopes, same-study comparison, narrow
comparison-only failure isolation, cancellation and an actual option-tree domain
case with frozen time/volatility. The new groups run through the normal managed
verification runner.

Owned integration runs use stock PostgreSQL, synthetic observations and the real
fenced worker. Independent Fraction oracles check all 249 scenario PV losses,
empirical risk and mixed native partitions; an independently requested joint
scenario agrees with the historical mapping. Two initial forecast/HPL fixtures
exclude next-session moves from calibration and retain forecast holdings.
Qualification includes immutable raw source pins, missing-evidence refusal,
clock-receipt tampering, current grants and active-lease/cancellation fences,
method/combined archive pagination and unchanged original request replay.
Migration 0189 refuses changed privileged helper bodies, owners, ACLs or search
paths atomically. No operating backtest service or production throughput is claimed.

Frontend checks refuse incomplete PV matrices, omitted/substituted comparison
exposures, wrong capture/method and multipart composition. New York EOD/DST dates
come from captured business sessions. Definite SQL refusals release only a first
rejected command; a previously uncertain command keeps its identity. Graphical
consumers carry backend loss/probability/allocation values without calculating
risk in TypeScript. Desktop/phone browser qualification covers guided automatic
history, native samples/trades, export, comparison, retained archives and reports.
The full frontend suite has 1,297 checks and the Python runner suite has 85 checks
at this increment. The existing parametric numerical/custody/16-browser suite
remains a regression gate.

Run with explicit local tool paths:

```sh
python3 tools/run_preview.py --pg-bin /path/to/postgres/bin --dotnet /path/to/dotnet --historical-risk-check --browser-check --browser /path/to/chrome
```

The standard CI group, which includes existing exchange and volatility-fit
qualification, has a 60-minute job budget. Two runs of the original 45-minute
budget were cancelled while existing exchange/fit qualification was progressing
without an assertion failure. Other verification groups retain 45 minutes. All group commands, assertions, aggregate success requirements
and release dependencies remain unchanged; this is CI capacity, not a pricing or
production-runtime limit.

## Option strategy viewer (D409)

Runs on 16 November 2026 at the branch head, after merging master with the
option analytics (D408) and the operating-facility work (D412) in it, with
synthetic data, on the local machine (macOS 26.6
arm64 with 14 logical processors, .NET 10.0.13, Node 24.22.0), from Debug builds. It
has no PostgreSQL, so the database-backed exchange check ran in a disposable
PostgreSQL 18 cluster in a container on a separate two-CPU Linux host, from a
Debug build, on the tree before the operating-facility work was merged in.
Neither machine runs the browser suites under `tests/browser`. The pull
request's CI ran them on an earlier tree of this branch, before master's D407
was merged in: its exchange group passed, with the Options suite's 32 checks,
the Strategy step among them.

| Run | Where | Result |
| --- | --- | --- |
| MarketData checks `--option-strategies` | local | exit 0, 24 PASS |
| MarketData checks `--option-sensitivities`, `--vol-surface`, `--futures-options` | local | exit 0; 8, 19 and 68 PASS |
| Api checks `--schedules` (with the option request, position and strategy request checks) | local | exit 0, 436 PASS |
| Api checks `--async-read-contracts`, `--assistant-workspace` | local | exit 0; 1 and 21 PASS |
| `npm test --prefix web` | local | 1,935 pass |
| `python3 -m unittest tests.test_ci_impact tests.test_check_all tests.test_migration_identity tests.test_ci_dependencies tests.test_public_routes` | local | 69 OK |
| `run_preview.py --check --exchange-check` | container | exit 0, 368 PASS |

What the local checks cover:

- **Payoff at expiry.** Thirteen cases worked by hand at a multiplier of
  10,000: a long call, a long straddle, a short put (its loss bounded by a
  futures price of zero), a call spread, an iron condor, a butterfly, a risk
  reversal for no premium (a breakeven range), a ratio spread (an unbounded
  loss), a breakeven that is not a terminating decimal, a payoff that is zero
  from a price upward, one that only touches zero at a strike, one that is
  zero at a futures price of zero, and a put bought for nothing. Each states
  the net premium, both extremes with their bounds and the breakevens; the
  curve is checked at price points, at a strike between them and with a strike
  below them all. The independent review also compared 200,000 random leg sets
  with a separate rational calculation and found no difference; that
  comparison is not a check in the repository.
- **Dated curves.** Bought calls and sold puts of the option fixture, at three
  futures prices, two instants and a volatility shift, against direct tree
  prices of each leg at that price, instant and volatility; the unshifted
  curve at the valuation instant against the legs' base values; a refusal when
  a leg is valued at intrinsic or did not calibrate, and `scenario_volatility`
  when the largest shift a request may state leaves no volatility; an instant
  at the legs' instruction cutoff, too many prices and a missing portfolio
  each failing as an error and not as a refusal; and two runs from the same
  inputs agreeing.
- **Requests.** Without a database: a complete request, 30 incomplete or
  unoffered ones each refused with its code, a request at every limit accepted
  and one beyond each refused, the asynchronous channel admitting the same
  typed request and nothing less, the setup's offer drawn from a stored
  surface alone (templates, range, days on a clock up to before the cutoff,
  a clock the host cannot count on, policies that an analysis accepts back),
  and a strategy's figures as its legs' scaled and added.
- **Web.** The view model and chart options as unit tests, and the Strategy
  view drawn in a headless browser over stubbed reads: the setup's offers, a
  template analysed with the offered policies sent back, a leg from the chain,
  a stale analysis, a refusal that keeps the legs, refused dated curves, a
  replaced analysis cancelled and its answer dropped, the keyboard's focus
  kept when the legs are redrawn, an answer leaving a number being typed
  alone, and another product chosen while an analysis is awaited.

Measured here through the scenario layers, for the limits a request states
(`MarketData checks --option-strategy-workload <points> <curves>`, two legs on
two unique series of the option fixture, a volatility shift on every sample,
one thread):

| Build | Points | Curves | Scenarios | Tree prices | Elapsed | Per tree price | Allocated |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Release | 21 | 2 | 42 | 88 | 3.2 s | 36 ms | 3.9 GB |
| Release | 41 | 3 | 123 | 252 | 9.1 s | 36 ms | 11.1 GB |
| Debug | 21 | 2 | 42 | 88 | 5.3 s | 60 ms | 3.9 GB |

A tree price is one 400-step tree in 30-digit decimal arithmetic and allocates
about 44 MB as it runs. A request at every limit (4 legs, 41 points, 3 curves)
is about 500 tree prices for its curves (492 scenario prices and one base
price per leg for each set) and about 68 for its sensitivities, beyond
calibration: of the order of 20 s of one thread in Release here, spread over
the curve sets and the sensitivities, which run side by side. That is why the
analysis is an asynchronous read and why the limits are no higher. These are
observations on one machine, not a performance qualification; CI's runners
have been about half as fast per thread.

The exchange run is of
the tree before master's operating-facility migrations 0342 and 0343 were
merged in; they follow the option analytics migration 0341, and this change
adds no migration. It had master's migrations 0339 and 0340 applied before
0341; an earlier run, before those were merged in, also passed (exit 0, 363
PASS).

The exchange run covers, against the exchange example with its surface fitted
at a working snapshot: the setup offering the seven templates at the series'
settlements, a price range, the days before the cutoff on the surface's clock
and the policies; a straddle analysed over HTTP with its exact premium, largest
loss, unbounded profit and two breakevens; each leg's volatility equal to the
stored surface's and its delta within 0.02 of it; the strategy's value as its
legs' scaled and added; the dated curve at the valuation instant never below
the payoff at expiry; a volatility shift raising that curve and leaving the
payoff alone; eleven requests beyond a limit or outside the offer each refused
with its code; a leg valued at intrinsic said on its row with the dated curves
refused and the payoff still stated; and no surface revision stored.

Measured in that run for one analysis (2 legs, 11 points, 1 curve): 22
calibration and 64 valuation tree prices in 4.2 s on the two-CPU host (4.5 s
in the earlier run).

The first exchange run stopped at the first analysis, exit 1 with 296 PASS: the
read resolved the product's market source without the view's own market data,
which a working snapshot refuses `market_inputs`. No local check could see
that. The script's own first draft also compared a count with `len()`, which
the independent review found by reading before any run.

Not run, because the host's container cannot start the browser; it runs in the
pull request's CI:

- The Strategy step of `tests/browser/options.mjs`, which runs the analysis in
  the application over the asynchronous read channel. Every expression of the
  suite compiles, and the Strategy checks as the suite composes them hold
  against the view drawn over stubbed reads; neither is a run against the
  application.

Not covered: no check compares the analysis over the asynchronous channel with
the same analysis over ordinary HTTP value for value, as the workbench
qualification does for its reads; the browser step is to exercise the channel
and the exchange check exercised the ordinary route. No check runs the setup or the analysis at
a recorded close or a retained context over HTTP (the example's frames are
working snapshots; the close's clock rule is covered without a database), none
asserts the refusal of a reader without Read trades (the endpoints are wired
as the positions read is), and none times an analysis in the application.

## Option analytics and the Options page (D408)

Runs on 15 November 2026 at the branch head, with synthetic data. The local
machine has no PostgreSQL, so database-backed checks ran in a disposable
PostgreSQL 18 cluster in a container on a separate two-CPU Linux host, from a
Debug build. The browser suites under `tests/browser` have not been run for
this change; they run in the pull request's CI.

| Run | Where | Result |
| --- | --- | --- |
| MarketData checks `--option-sensitivities`, `--vol-surface` | local | 8 and 19 PASS |
| Api checks `--schedules` (with the option request and position checks) | local | exit 0, 417 PASS |
| `npm test --prefix web` | local | 1,887 pass |
| `python3 -m unittest tests.test_ci_impact tests.test_check_all tests.test_migration_identity tests.test_ci_dependencies` | local | 60 OK |
| `run_preview.py --check --exchange-check` | container | exit 0, 342 PASS |
| `tools/check_risk_fit_authority.py` | container | exit 0, 29 PASS |
| `tools/check_vol_surface_admission_expiry.py` | container | exit 0, 3 PASS |

The exchange run applies migration 0341 and covers: a fit that states no
supported selection or the wrong clock is refused at submit; a fit at a working
snapshot and one at a recorded close each complete with analytics and state
their clock and day; every calibrated series has each measure or the code of
why not; a series at intrinsic has its settlement and no sensitivities; stored
signs and call/put agreement; an identical refit records nothing while another
selection records a new revision that only a later cutoff reads; and the
positions read with its units and stated selection. Two full local web runs
each had one unrelated browser test fail because its local browser did not
start within 60 s; each passed when re-run, and the third full run passed.

Measured on that host for one product of the example (71 listed series, 70
solved), in the risk worker: the fit itself 27.2 s; the analytics 47.8 s for
1,190 tree prices, about 17 per solved series, with the five expiries valued in
parallel (76.2 s before they were). No measure was missing. These are
observations on one small host, not a performance qualification. A fit job now
takes about 2.8 times as long there as before this change.

Not covered: no check isolates the recording of the discount-curve points the
one-day roll reads, and no check re-runs one fit job after an abandoned
attempt; a refit of the same inputs producing identical content is the
evidence of determinism.

## Volatility surfaces (D304)

D356 (surfaces at their close and the surface library), local runs on 6 November
2026 against the branch head after the independent review's fixes, each in its own
disposable PostgreSQL cluster with synthetic data:

| Run | Result |
| --- | --- |
| `npm test --prefix web` | 1,437 pass |
| `python3 -m unittest tests.test_migration_identity tests.test_release_deployment` | 33 OK |
| `run_preview.py --exchange-check --browser-check` | exit 0, 385 PASS |
| `run_preview.py --regional-closes-check` | exit 0, 72 PASS |
| `run_preview.py --market-dates-check --browser-check` | exit 0, 98 PASS |

The exchange run records one frame as the close of two synthetic sessions: a fit at
one session's close is not the other's nor the working snapshot's at the same
instant; another sign-in and a historical view (under its own alias id) read the
same surface; a close that replaces it starts without one, still reads the replaced
close's surface as history and refuses a calibration there with `close_replaced`; a
recording at a close refuses an input the close does not pin; a refit at an earlier
instant of the day records nothing new; and the library marks the context's own
surface and lists it first. The browser checks cover the product cards, search and
paging, the not-calibrated panel for readers and calibrators, opening the newest
calibrated close and a chip through the header, calibrating from the panel through a
lost response and retry, and the existing heat map, 3D, smile, phone-width and
historical-view checks.

Local runs on 2 November 2026 against the branch head after the final review's fixes
(a revision's inputs now include its valuation instant and the discount-curve points
the calibration read), each in its own disposable PostgreSQL cluster with synthetic
data. The capability is described in
[volatility surfaces](vol-surfaces.md).

| Run | Result |
| --- | --- |
| MarketData checks `--vol-surface` | 16 PASS |
| MarketData checks `--futures-options` | 33 PASS |
| `npm test --prefix web` | 1,237 pass |
| `python3 -m unittest tests.test_migration_identity tests.test_release_deployment` | 33 OK |
| `run_preview.py --exchange-check --browser-check` | exit 0, 330 PASS, including 30 futures-option and 46 Vol surfaces browser checks |
| `run_preview.py --risk-jobs-check` | exit 0, 186 PASS |
| `run_preview.py --connected-portfolio-check` | exit 0, 41 PASS |

The managed checks are independent of the fit's algorithm: a smile generated from
known parameters is recovered, rich/cheap equals an offset deliberately added to one
series, and too few strikes, a nonpositive smile, a missing underlying and the grid's
ends each give their stated result. The exchange preview run fits both example
products in the managed calculation (`--vol-surface-fit`), where the pinned
observations must equal every observation the series touch when each is calibrated
alone, discount-curve points included; stores and reads revisions under the trading
publication protocol, where the same observations at another valuation instant
record a new revision; and runs fit jobs over HTTP through a risk worker: a retried
request identity returns its job, a refit of the same inputs records nothing, a
revised settlement or discount-curve point flags the stored surface and a refit
records a new revision while the old cutoff still reads the old one, an expired series
is listed as `option_expired` and a product with no live series fails with that code,
a historical context cannot fit and the submit operation itself refuses a context not
captured from current knowledge, and a fenced attempt records nothing. The browser checks cover the
page's views, linking, fit states, hidden-page reads and historical view, and the
Volatility surface links from the option ticket and an option trade's Details, each
opening the page on the option's product at the context the option was read at;
leaving a ticket with typed entries asks first whether to discard them.

Measured fit runtime with 71 (ON) and 70 (LO) series per product, 70 solved each,
in a Debug build: the `--vol-surface-fit` check took 27.4 and 28.0 s (ON) and 29.6
and 31.2 s (LO) in two earlier runs, and 24.7 to 25.2 s (ON) and 25.9 to 26.8 s (LO)
in that task's four runs, and 36.1 s (ON) and 27.3 s (LO) in the final fix run, whose
risk-worker fit of ON took 16.4 s. Earlier end-to-end fit jobs in the preview worker took
18.3 to 22.6 s, with 0.14 to 0.21 s of queue wait. The fit inside the risk worker
took 15.9 to 20.9 s: 15.9 to 16.8 s for ON in the previous task's runs and 20.9 s in the
latest earlier run. The first build, with about 30 series per product, took
11.7 s per product, which missed the original 5 s synchronous budget.

Environment: macOS 26.6.2 arm64, PostgreSQL 16, .NET 10.0.402 Debug build, Node
24.22.0, Chrome, all components on one machine. These are local workload
observations, not a performance qualification. The vol-surface migrations need a
separately qualified schema release; nothing was deployed.

## Parametric risk and retained statistical reports

D299 was qualified locally on the integrated master using disposable stock
PostgreSQL clusters and owned browser profiles. The full managed suite includes
11 new numerical groups, with independent exact sample moments, normal tails,
zero/singular variance, native partitions and rolling-prefix knowledge oracles.
API checks include complete three-pass PPA/option/value admission. The integrated
frontend passed 1,221 checks; the Python runner passed 83 checks.

The dedicated parametric suite independently reconstructs native one-factor and
mixed-factor covariance/variance, then compares actual retained-worker results.
The one-factor fixture uses 249 common changes; the mixed held universe uses
three changes and retains its singular covariance diagnostics. Direct SQL checks
cover trusted capture provenance, altered/missing/cross-context receipts,
immutable source FKs, runtime/worker privilege denial, bounded orphan reuse,
active bound leases, expiry/cancellation/current authority and missing-evidence
failure. Ordinary and statistical jobs expose the same exact observation-ID set
under existing bound RLS. Three archive pages preserve ownership and ordering;
viewer calculation/report/archive access refuses.

Eleven owned browser checks passed, including desktop/phone graphics, explicit
unselected inputs, reauthorized export, wrong-policy response refusal at immediate
and known queued consumers, unavailable diagnostics, lost queue response and
reload recovery, archive discovery and parent-scope invalidation. Screenshots were
inspected. Existing history checks and ten browser checks passed; the existing
Risk/worker suite and 159 browser checks also passed before the final navigation
rebase. The integrated parametric rerun passed after that rebase. Independent
adversarial review resolved receipt provenance, numerical determinism, FD
representability/work bounds and complete consumer coverage/policy findings.

The 30 MB report limit refused the entire held universe over the long window;
the smaller explicit mixed window qualifies the native multi-factor calculation.
No production throughput or deployment is claimed. Migration 0175 requires the
normal qualified schema release.

## Separate physical references and derived crude pricing

D297 was qualified locally on 2 November 2026 using owned disposable stock
PostgreSQL clusters and isolated browser profiles. The API build had zero
warnings; API schedule checks and all 1,211 frontend checks passed.

`tools/run_preview.py --crude-setup-check --browser-check` passed native booking,
retained reopen and worker risk oracles, missing/ambiguous/partial-overlap mapping
refusals, source-pin bypass refusal at final SQL admission, exact reference
namespace checks, and completed-command replay after conflicting publication.
Observed storage waits followed by natural independent `ReadMarketData` expiry
refused both raw crude/gas HTTP responses and the direct SQL resolver. Reapplying
0173 over populated observations, bookings and retained programs preserved every
captured business row and the completed native retry outcome. Thirteen setup and
ticket browser checks passed, including explicit singleton selections, version
reselection and stale asynchronous response rejection. A separate native gas
browser check opened reference entry with no dated gas price frames.

`--gas-setup-check --deal-ticket-check --browser-check` passed, including an
unpriced reference path with no imported convention and its later explicit
maintained pricing setup. `--ppa-bookings-check --browser-check` passed 55 browser
checks and its exact API/worker oracles; valid unpriced power locations remain
available and report missing valuation coverage. The existing crude lifecycle
suite and its 47 browser checks also passed. These results establish the bounded
local capability; production activation requires the qualified schema release.

## Joint risk admission and reports

The D295 local qualification on 2 November 2026 passed 656 managed core checks,
19 focused joint-risk groups, API schedule/contract checks, 1,199 frontend checks,
the independent worker build with zero warnings, deployment configuration and
32 release-boundary checks. The integrated baseline includes maintained crude
setup migrations 0170–0171; joint admission is migration 0172.

Owned stock PostgreSQL checks exercise simultaneous signed moves under a nonzero
parent, common sample IDs across multiple exact market partitions, explicit
unchanged partitions, local and cross-market derivatives, exact native aggregate
agreement, immediate/worker financial parity and idempotent retries. Direct SQL
checks reject incomplete/forged retained axis unions, ambiguous typed requests,
missing/negative tolerances, unrepresentable half steps and joint multipart frames.
Private helpers grant no new runtime/worker authority.

Selected-trade named scenarios and nominal Greek estimates reproduce independent
legacy up/down PV results for futures, swaps/heat-rate, PPAs, gas, crude and
options, including the option's separate premium. Composed capacity PPA and
separately maintained crude source bindings retain their selected identity.
The complete risk/job suite passes 159 owned browser checks. New browser checks
exercise explicit editors, loss/Greek graphics, retained named-report reload,
queued derivative parity, binding detail and phone layout. Tables preserve exact
backend amounts beside display values; positive losses shade red without changing
plotted signs. Missing coverage refuses a complete report.

Independent adversarial review identified overlapping targets whose distinct
source keys resolve to the same compiled node. Admission now rejects those named
samples and mixed pairs before pricing or queueing; the adapter regression fails
with the previous key-only comparison. Review covers numerical aggregation,
retained inputs/current authority, worker replay, report coverage and exports.
No statistical estimator or production throughput is established by this release.
Migration 0172 requires separate installation qualification; no production deploy
was performed.

## Individual trade risk scope

The D292 local qualification on 2 November 2026 passed the focused API trade-scope
and schedule checks, 1,192 frontend checks, deployment configuration and 32 release
boundary checks. Disposable stock PostgreSQL runs passed unified trading, physical
crude, exchange futures/options, composed PPA bookings and the complete risk/job
suite with 151 browser checks, integrated with the compact shared date header.

Selected futures, swaps, heat-rate, PPAs, gas, crude and option trades reproduce
their whole-book binding base values and immediate/retained-worker financial
results. An option selection explicitly includes both option and premium bindings.
Checks cover empty/duplicate/unknown/out-of-book refusals, off-page selected IDs,
unreadable identity handling, legacy whole-book multipart continuation, reload
recovery and exact subset matching in reports. Runtime PPA identities withhold
unreadable reference sources without blocking an unrelated selected trade. Revoked
reference authority, stale lease tokens and cancellation fence bound worker reads.

Selected-only PPA replay also qualifies retention of the native price shape's
knowledge contexts independently of other trades' quantity inputs. Pricing values
remain in the shared managed PV operation. Independent adversarial review covered
authorization, temporal selection, replay, report scope and the migration boundary.
Migration 0169 needs a separately qualified schema release; these checks used only
owned synthetic resources and did not deploy to production.

## Joint scenario calculation boundary

The D291 qualification on 2 November 2026 passed 18 focused managed groups,
the full managed suite (656 aggregate checks), the worker build with zero warnings,
1,188 frontend checks and the owned stock PostgreSQL risk/retained-worker suite
with 146 browser checks. It used the integrated explicit-side and option-event baseline.
Synthetic linear, quadratic and cross-term oracles verify exact loss, unlike
units, unequal bumps, external quantities, unit reuse and mixed derivatives.
Cubic/kink cases and negative/zero nominal estimates qualify the explicit
three-step agreement policy. Failure and cancellation preserve the parent.

Independent adversarial review identified incomplete ancestry when preparation
starts from an already layered context. Walking actual scenario parents fixes
the receipt/work count; the regression fails with the old wrapper-only walk.
Review cleared the resolved implementation. This qualifies the managed numerical
boundary and existing report regressions, not new joint-run HTTP/worker retention,
graphical widgets, a statistical estimator or production capacity. No schema or
production deployment changed.

## Risk results workspace

The D289 local qualification on 2 November 2026 passed all 1,172 frontend
checks. The stock PostgreSQL risk suite passed scenario, scope/current-authority,
durable-job, cancellation, worker/replay and retained-definition checks, plus
143 full Risk browser checks. A final focused workspace run passed 22 browser
checks on the integrated futures-options baseline.

The workspace checks cover exact shared report values, delta/gamma units,
heatmaps and tabular drill-through, direct retained links, display-zone changes,
automatic matching-report selection, explicit book scope, account-change
questions, same-window address ownership and phone width. Deferred POST replies
exercise reload during a later multipart ordinal: completed metadata-only parts
are reauthorized and reloaded, all remaining original request IDs resume once,
and missing siblings remain incomplete with complete export unavailable. A
separate cached-catalog check refuses a changed owning context before valuation.

Independent adversarial review covered context/account fencing, current-authority
reads, uncertain submission persistence and pop-out workspace identity. These
checks qualify deterministic presentation and request recovery; no VaR estimator,
calibration pipeline or production throughput is claimed. This change adds no
schema migration.

## Shared market factors and retained gas portfolio risk

The D287 local checks on 1 November 2026 cover canonical factor sharing across
distinct instruments and repeated holdings, with one base/up/down scenario set
and independent book delta oracles. Managed physical-gas, trade-contract and
connected-portfolio checks pass, as do the managed API schedule/admission checks.

Owned PostgreSQL previews pass the mixed crude/futures/gas and unified trading
workflows. They verify exact contractual PV and sensitivities, shared upstream
factor propagation, unaffected holdings, nested stress isolation and retained
worker parity. Gas-only scope is admitted. Hidden held instruments and hidden
physical or fee memberships refuse rather than publishing incomplete values;
the latter checks also cover the ordinary inspector. A worker without a live
claim reads no gas trade rows, and an altered graph manifest fails reconstruction.
The omitted-member regression failed before its fix and passed afterward.

Migration 0165 preserves runtime history/current-authority predicates and adds a
worker lease fence. Independent adversarial review found no remaining blocker
after the member/program completeness fix. These are synthetic correctness and
authority checks, not a throughput benchmark or an installed schema upgrade.

## Generic trade bindings and typed PV

The D218 change passed the trading verification group on 25 October 2026,
including shared preparation, frontend checks, stock database schema/upgrade
checks, swap authoring and settlement, mixed portfolios, pilot-close workers and
connected published-curve/retained-worker parity. The final tree passed 625
managed core checks, the API schedule/admission checks, and the independent
worker build. The trading run also passed all 84 rate checks.

After routing futures draft risk through the typed-PV path, final isolated runs
passed `--draft-check --browser-check`, `--ppa-bookings-check` and
`--close-risk-check`. These cover public draft results, PPA booking/replay,
coverage and overdue measures, worker replacement, exact discount receipts,
opposing sensitivities and cancellation/publication fences. All database runs
allocated and removed their own synthetic PostgreSQL clusters.

The new managed checks verify repeated instrument bindings and shared book
valuation, typed output/context rejection, explicit size-scaling admission,
missing execution facts, canonical signed forward quantities, equivalent
definition interning, strike-specific economics and shared-side isolation.
Existing futures execution and settlement-PV refusal checks remain in force.
See [unit instruments and trade bindings](instrument-contracts.md) for the exact
implemented contracts and remaining booking/lifecycle limits. No hosted schema
or deployment was changed.

The final synthetic Debug benchmark used 50,000 futures bindings, four books,
one dated contract and one primitive factor over three base/up/down scenarios.
It performed three unit-pricer calls, three curve-node evaluations and twelve
book quantity applications. On macOS 26.6.2 arm64 with 14 logical processors,
.NET 10.0.13 and SDK 10.0.402, five fresh-cache samples after warm-up had a
258.68 ms median and 217,074,824 median thread-allocated bytes. These are total
allocations, not retained or peak memory. Independent lot/price arithmetic checks
the result. The timed operation includes holdings validation and portfolio
preparation; it excludes database, network, serialization and model/fixture
construction. The work counts establish reuse, not production throughput or a
universal speedup over full repricing.

## Retained swap editor and repeated sign-in regression checks

An isolated pre-fixing-convention database books a synthetic legacy swap, then
applies every subsequent migration. Its current editor returns only the saved
exact index/curve/revision with empty new-pricing sampling choices; its 10 USD
PV and retained event remain unchanged. Ordinary entry does not offer that
unqualified index. The swap browser suite verifies retained disabled selections
survive observation-choice refresh and that valued sides remain visible.

Hosted HTTPS checks verify a repeated sign-in redirects to the application while
preserving the current identity, session count and cookies, even when the form
names another account. Missing antiforgery tokens and foreign origins remain
refused. These checks use disposable synthetic databases.

Run verification from a clean checkout using public dependencies and the supplied
synthetic fixtures. No existing database or application is a verification target.

## Required tools

- The exact .NET SDK in `global.json`.
- The exact Node version in `.node-version`, including its bundled npm.
- Python 3.12+ with assertions enabled; the preview launcher uses Python 3.12
  f-string syntax. The hosted job uses Ubuntu's Python 3.12.
- Installed stock PostgreSQL 18 binaries, including `initdb`, `pg_ctl` and `psql`.
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
16.14. Under D282 a runtime read makes one fresh visibility and authority
decision per statement, and each row compares the clock with that authority's
end; a 30-row scan computes each set once. Same-context rows remain identical.
Checks cover current scope and role revocation, disabled principals, expiry
between reads within one statement and within one scan, each statement set
accepting exactly what its per-row function accepts over every row for current,
bound, trading-bound and write-profile requests, additional permissive policies,
unexpected migration prerequisites and transactional rollback.

On a 2026-11-01 physical copy of the hosted pilot database (stock PostgreSQL
16.14 on Linux, schema 0157), reads under one bound request with current knowledge
returned identical rows and bytes before and after migration 0158. Three runs each:
3,444 side schedule rows 2.09-2.15 s before and 18-20 ms after; 20,975 observations
1.21-1.30 s and 114-120 ms; 3,670 calendar days 530-590 ms and 7-13 ms; 1,239
futures milestones 170-200 ms and 4-6 ms; 1,083 contracts about 57 ms and 4-5 ms.
Two gas trade revisions, whose rule keeps a per-row check, took 8-10 ms before and
22-38 ms after. These are row-security read times, not Blotter request times.

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
  only in its `x-api-key` header. It carries the configured synthetic prompt without workspace
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

- **Asking:** a text-only ask is admitted without acquiring market inputs and run by the
  worker to completion, keeping the streamed answer and closing its call with the usage
  the stream reported before the terminal event. Its one request carries the key only
  in its header, and sends the pinned installation and agent instructions, the
  question, the version's output limit and effort, and, for a version without tools,
  none; this check's agents have none, and the tool checks below give theirs. The turn
  retains its selected frame identity but no pins, financial context or capture time. A repeat answers with its turn as it stands and sends
  nothing. A follow-up sends the earlier turn as history, labeled with its ending and
  context state (including no acquired context), before the question. An answer streamed in many deltas at once is
  coalesced and published whole and in order, in pieces of at most 2,048 bytes.
- **Knowledge:** a supplied frame must belong to the session (`snapshot_missing`
  for another); a conceptual question needs no frame. While a historical view is
  selected, its retained context is asked with a frame of it, retained as a selection
  without copying inputs, and working data is refused, as is any retained
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
  step, and past its agent's time per read fails the turn `limit_tool_read` with a step naming
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
  `limit_elapsed`, and one past its agent's time per read fails it `limit_tool_read`, each
  with a step naming that bound with the read's instant and accesses and one warning in
  the log naming the bound, the tool and the operation, never the input; with the API
  process paused while a read waits, the database stops the read and frees the epoch row
  within twice its time per read, and the resumed turn fails `limit_tool_read`; a read held for
  seven seconds within a 15-second time per read completes and the turn answers; and a worker that
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
shows; that a page target names one of the eight pages the assistant reads, checked by
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
a published agent on each, through Administration › AI. As the database owner it then writes
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
- Administration › AI qualifies a provider, prices and enables a model and publishes an agent,
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
provider administrator Administration › AI and the workspace's grants. In SQL,
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
previous image with the host configuration it accepts (`--previous-credential-keys` and `--previous-process-limits`)
and adds the key files' fields before the deployment, as an operator does. `tools/check_public_routes.py` checks that the gateway passes an event
stream's first frame through before the application ends the response; a copy of the
template with buffering on fails it.

The Messages conformance suite also checks that the adapter's `Validate` refuses
everything `Stream` refuses before sending, and that the provider endpoint override is
only a loopback IP-literal origin.

Administration › AI has its own browser scenario, which runs after those HTTP checks in the
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
state; the launcher adds a role holding each of Administration › AI's three permissions alone.
A trader is offered no AI settings. An identity holding only `ManageAgents` lands on
Administration › AI, is offered the Agents tab alone and reads only the agents directory; one
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
holding only `ReadModelUsage` lands on Administration › AI, is offered the Usage tab alone and
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
anyway. On Delivery prices and PPA bookings Looking at says the page is not read, an ask
from either is admitted with the page and no selection, and Read current page is refused
`page_not_supported` in plain words. During a turn Send becomes Stop with the
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

## Numeric blotter and composed swap qualification (2026-10-22)

The focused suites passed on stock PostgreSQL 16.14, .NET SDK 10.0.402,
Node 24.7.0 and Chromium-compatible Chrome, using disposable synthetic resources:

- 285 frontend checks, 422 managed core checks, 80 rate checks, the API schedule
  checks, 54 Python runner checks and 173 combined-schema checks.
- Draft/composition HTTP checks and 72 swap browser checks cover independently
  selected floating sides/spreads, blank versus explicit-zero strikes, reviewed
  par quotes, frozen risk terms, booking retries and amendments. Direct SQL checks
  reject either side's transitive wrong-revision projection and revoked source
  access on retry.
- Published fixing checks retain each slot's original actuals across corrected
  retries; fresh operations retain the corrected pins. Connected custom-curve
  and daily-projection checks retain exact PV, risk and worker results.
- The actual generated-close worker publishes a six-position mixed report with a
  floating/floating trade, including two undiscounted payment inputs. Recovery
  and current report-reader authority checks pass.

Integration with migrations 0090–0100 uses migration 0101 and decisions D206–D207.
The combined baseline passed 301 frontend checks, 533 managed core checks,
81 rate checks, 34 shared temporal checks, API checks and 61 Python checks.
Focused runs passed 72 swap-entry, 39 PPA and 11 generated-close browser checks.
An existing-close regression first rejected a valid floating/floating trade under
the fixed-side-only scope reader, then passed with exact floating-side references:
two independent spreads produce PV 1 and zero opposing quote sensitivity, with
both membership receipts retained. Existing PPA discount receipts, current
authority, cancellation, retained inputs and generated-close recovery also pass.
Composed booking rejects an operation ID already retained by a PPA amendment.

Pivot cells retain exact quantity/currency references and price multipliers;
price extrema remain exact and arithmetic means use the explicit 18-place
rounding policy. Charts approximate coordinates only, preserving exact safe
strings in tooltips and data exports. The pivot supports row/column grouping and
linked bar, line and heatmap plots. Subtotals, cross-axis grand totals and cell
drill-through remain unimplemented. Charts precede local grid row filters.
Migration 0101 requires operator review and a schema window; these local checks
do not qualify a hosted deployment or production financial throughput.

## Hosted job

[The workflow](../.github/workflows/ci.yml) uses `ubuntu-24.04` hosted runners on
pushes to `master`, pull requests and manual dispatch. A branch push without a pull
request runs no CI; its pull request run verifies the same head. Checkout and tool-setup actions are
pinned to exact commits. The checks job has read-only repository access, does not
persist checkout credentials and requires no repository secrets. The full local
`check_all.py` command remains available. [Impact selection](ci-impact.md)
chooses from the four shared groups and fourteen integration groups. Shared
behavior suites run once; each selected integration group performs its own locked
build and owns its disposable application, browser and database. Each group keeps
its 90-minute deadline and existing per-command/resource deadlines. The complete
local gate retains all 185 commands in the same order.

The stable `Stock database and browser checks` aggregate requires successful
impact selection and every selected job. Failed, cancelled and skipped jobs
cannot qualify. A separate publisher requires the complete gate for the same
master push, explicit publication enablement and the hosted-release environment.
All master pushes remain full because the updater admits a release only for the
exact current branch head; a documentation push must not strand earlier runtime
changes. Pull requests never publish an application release.

The job installs stock PostgreSQL 18.7 from the reviewed package lock and uses
the runner's Chrome. [Dependency caches](ci-dependencies.md) cover NuGet, npm and
verified PostgreSQL packages, including Ubuntu runtime dependencies. The producer
verifies signed archives once; consumer jobs verify pinned hashes and perform
offline package installation without downloading signing keys. A configured
public Gitea bundle URL selects bundle acquisition; otherwise the pinned public
package URLs are used explicitly. Cache hits never skip byte verification or
locked restores. The checks start their own disposable clusters. The job records
the runner image, PostgreSQL patch, browser, Python, ICU and time-zone versions.

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
blotter/editor inspector and risk integration remained work at this checkpoint. Options
were not implemented then; listed American futures options followed on 1 November 2026
(D288), and the exercise-control accounting choice for embedded options remains open in O13.


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
persisted averaging rules, swap risk, fees, physical products and options remained
unimplemented at this checkpoint (listed American futures options followed on 1 November 2026, D288). Independent review found no remaining source blocker in this
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
forward booking, executable options and VaR/covariance remained planned work at this
checkpoint; listed American futures options followed on 1 November 2026 (D288).

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
900, and on an Administration › AI price form whose rate table overflowed both read
571, while the root element's `clientWidth` stayed 390, so the width comparison
passed on those overflowing pages. The Administration › AI checks have since compared
with the client width, and that rate table now scrolls within its form. All ten
checks now share one expression, `fitsWidth`, that compares `scrollWidth` with
the root element's `clientWidth`, the layout viewport; the calendar check also
bounds its dialog by that width.

Corrected, the eight checks outside Administration › AI passed on the existing pages.
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
such as the Administration › AI rate table, or clipped by an ancestor is not page
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

## Existing-close PPA book risk

The full gate includes the owned `--close-risk-check` preview. It applies migration
0094 after a rollback-only pre-upgrade history probe, then uses the actual runtime
roles and worker against synthetic retained closes. The probe compares every
original job, run, audit and report column, including factors, discount inputs and
exact rational amounts. Historical operation names and identities remain intact.

The integration oracle checks a 25-hour DST delivery, retry input pinning after
later forecast/meter/actual corrections, held-certificate value and sensitivity,
separate known and estimated overdue balances, and enabled payment discount
receipts against an independent rational calculation. It checks whole-book
revision and unsupported-position refusals, an actual over-limit long-term book,
idempotent admission, missing policies, current authority, cancellation/replacement
fences, old and undeclared worker claim gates, and zero market writes by report
execution. Existing generated-close and PPA HTTP/browser suites remain in the gate.

`PpaBenchmark.cs --ppa-benchmark` runs the five named cases in fresh child
processes, with three samples per case. Small independent acceptance cases compare
calendar arithmetic and selected base/up/down full repricings; exact work counters
are asserted for the full-size samples. The measured workloads, process protocol,
allocation/residency distinction and original nonrenewing worker limitation are
recorded in [the implementation record](implementation.md#existing-close-ppa-book-risk).
The benchmark measures managed preparation and valuation, not complete HTTP or
durable-job latency.

The complete local gate passed on 2026-10-22, including all prior PPA economic,
R3, reuse and browser checks. Independent adversarial probes additionally verify
missing/later retained-cutoff refusal without certification writes, current
owner/service revocation, hidden-series restrictions under bound human and worker
contexts, helper-role isolation, cancellation and natural lease expiry during a
blocked read. All owned disposable resources were removed. This is local
synthetic qualification. The managed benchmarks alone do not establish durable
completion; D201 has separate actual-worker evidence below.


### Renewable existing-close worker verification

`tools/run_preview.py --close-risk-lease-check --pg-bin <stock-PG-bin> --dotnet
<pinned-dotnet> --no-build` uses this repository and a fresh disposable database.
It admits actual twelve-month quarter-hour PPA risk, runs isolated scheduler/runtime worker
logins, and covers failed renewal, cancellation, current authority loss, overall
deadline expiry, real worker death and natural replacement. A PostgreSQL wire
proxy drops a committed report's acknowledgement; it first independently confirms
one durable report and terminal success. The test checks unchanged admission
pins/deadline after correction, exact complete HTTP results, stale-context refusal,
terminal outcome replay/conflict/authority, and original legacy claim gates.
Independent count assertions can also be applied directly to the captured complete
worker receipt, without repeating valuation.

The deadline case suspends only its owned worker after observing its durable claim
and confirms that publication has not begun. Native renewal requests
cap at the original deadline before expiry and refuse after PostgreSQL's clock
passes it; the resumed worker must publish no report. This scheduling-stall fault
does not rely on cold preparation taking longer than five seconds or alter clocks.
The process is always resumed during cleanup.

The replacement must publish after its original lease would have expired, with
observed native renewals. If calculation reaches its transaction-free phase
before that boundary, a brief owned-process scheduling stall crosses it while
the renewed lease remains live. The receipt records this stall separately from
elapsed completion time; no computation-speed claim includes it. The native
publisher, calculation inputs, clocks and lease duration remain unchanged.

The historical ten-year hourly, five-side workload with a fifteen-second lease
measured 326.922 seconds for completion and 329.441 seconds for replacement and
lost-ack recovery, both over 120 seconds. These are not measurements of the current
twelve-month fixture. Exact numerical/work and
renewal/lock measurements and remaining limits are recorded in the
[implementation qualification](implementation.md#renewable-existing-close-completion-qualification).
The new stage is included in `tools/check_all.py`; this bounded change ran that
stage and affected suites, not the entire expanded 45-stage gate. Independent
adversarial probes also exercised expiry after a held job-row wait, wrong worker
and fence, invalid lease bounds, paused renewal, original deadline clamping,
current owner/service revocation and renewal/cancellation during a bound read
transaction. All tests use synthetic data and remove their owned resources.


### Final Phase C complete gate

On 2026-10-22 the complete expanded 45-stage `tools/check_all.py` command passed
on the corrected tree in 1,532.375 seconds, using stock PostgreSQL 16.14, SDK
10.0.402, Node 24.22.0 and Chrome on macOS Arm64. All executable/test/migration and
configuration hashes matched the starting tree. This supersedes the earlier
bounded D201 affected-suite-only qualification; it includes every legacy and PPA
stage, including actual renewable completion and recovery.

Final qualification reproduced and fixed a slot ordinal admission/storage mismatch,
missing unsupported-feature catalog metadata and a cancellation callback exception
that could bypass exact committed-outcome reconciliation. Failing checks precede
all three fixes. Twelve deterministic callback-fault/outcome combinations plus
unexpected-error cleanup preserve authority refusal, caller cancellation and
incomplete outcomes. The real replacement/lost-ack workload then completed in
332.465 seconds with one report/success audit, exact base 40,408,960 and independently
verified 721 scenarios/316,057,560 evaluated rows. All four no-report failure controls
passed. Existing economic oracles, R3, reuse, browser/mobile/navigation and generated
worker checks remain green. Independent whole-branch review has no open material
finding. All owned fixtures were removed. See [Phase C qualification](ppa-phase-c.md)
for measurements and limits; this is local synthetic, not production qualification.


The PPA reference/quantity stage also executes `tools/check_meter_finality.py`
against its isolated stock PostgreSQL fixture. Independent runtime backends are
observed waiting on the publication gate before testing first-head, identical
request and conflicting-successor outcomes. Temporal, predecessor, exact-zero,
withdrawal, scope/expiry, immutable-calendar and commit-receipt checks accompany
the unchanged PPA HTTP and independent settlement specification checks. These
qualify the bounded evidence prerequisite, not global S1 or a cash lifecycle.
See [the operation boundaries](meter-finality.md), including the PostgreSQL
constraint-timing limitation and history-dependent certification work.


The bounded [monthly gross preview](settlement-preview.md) adds managed cases to
the existing MarketData suite and HTTP/DB cases to `--ppa-check`. Its separate
`--settlement-quarter-check` qualification uses a fresh owned database to preserve
the unchanged per-catalog knowledge-context budget. The independent executable
settlement specification remains a separate `unittest discover -s specs/settlement`
run. These checks create no accepted cash or settlement worker.


PPA model pricing and graphical qualification is recorded in
[ppa-break-even.md](ppa-break-even.md). The core suite includes independent exact
root, discount, signed rounding, zero-sensitivity and profile cases. The existing
PPA HTTP harness also checks draft pricing through current scoped authority, and
the existing PPA browser harness checks graphics, read-only draft access and
stale-response rejection in the enlarged fixture. The independent settlement
specification and prior Phase C gates remain separate preserved checks.

## Additional model-provider mappings (D210)

Local qualification on 2026-10-23 used .NET 10.0.402, unmodified PostgreSQL
18.1 and Python 3.14.2 on macOS. The following checks passed:

- Managed API checks with `--model-providers`: the existing Messages matrix and
  OpenAI, Google, xAI and Meta synthetic request/stream/tool-loop/usage checks,
  including malformed payloads, final-usage retention, model mismatch, cancellation,
  unknown billing fields and output limits that release no tool work.
- Managed API checks with `--schedules`, including the shared worker and transport
  consumers, plus the standalone dependency boundary check.
- `tools/run_preview.py --assistant-check` on a fresh owned database: provider,
  capability, test, agent, delegation, tool execution, streaming and usage checks,
  including the new four-vendor HTTP Test checks.
- `tools/run_preview.py --provider-adapters-check` on the final source and another
  fresh owned database: all four new vendors route through key custody and admitted
  mappings, record exact synthetic costs, replay Test without another request and
  close mismatched-model calls durably. A loopback fake and proxy trap receive all
  requests; the owned resources are removed after each run.

Independent adversarial review has no remaining material finding. This records
local synthetic evidence, not real-provider/account availability, browser rendering
or hosted schema/application deployment qualification. No real token prices or
credentials ship in migration 0104. See [the adapter contract](provider-adapters.md).

## Financial swap authoring, net settlement and contractual bridge (D216)

The D216 local increment passed all required matrix gates across owned runs,
including 621 managed core checks, 84 rate checks, 328 frontend checks, 173
combined-schema checks and the focused 62-check swap browser workflow. The
standard browser gate required a fresh-browser retry after Chrome closed its
automation connection; the retry and every subsequent gate passed. Earlier
preparation and managed/schema results were retained from the preceding full run.
See [settlement capability and verification](swap-settlement.md#d216-local-verification)
for supported combinations, independent numerical oracles and enforced limits.
This is synthetic local verification, not deployment qualification.

Scheduled booking/amendment submissions use a bounded 35-second browser wait for
the write and populated holdings refresh. Settlement determination, payment, net
recording/reversal, reconciliation and operation-status recovery use explicit
60-second workflow waits. Settlement writes also refresh workspace/chart and
settlement inspection; the transport bounds each request at 20 seconds. Ordinary
page readiness retains its ten-second budget. Waiting never repeats a command.

The combined scheduled-swap browser command runs four owned suites (authoring,
schedules, settlement and netting) under a ten-minute process deadline. Individual
readiness deadlines, booking/refresh errors and financial assertions still fail
the command. The independent 86.5 cash oracle remains mandatory. Nineteen helper
checks cover delayed click and form completion with exactly one submission,
ordinary timeout refusal, finite overrides and invalid budgets refused before an
action. The form check independently simulates a 46-second write/refresh sequence.

## Common PPA and heat-rate portfolios (D229)

Local disposable PostgreSQL runs pass the mixed-instrument acceptance gate:
37 synthetic bindings across four product types, a PPA-only directory, exact
historical visibility, blotter/report base-value agreement and identical queued
worker factor results. Concurrent-job context custody and revoked worker service
authority checks pass. The existing PPA booking gate and scheduled-swap gate pass,
including the latter's settlement, payment, netting and delegated agent checks.
The factor-swap gate verifies alternate-unit preview, booking, reopening and
retained factor contracts without double scaling. Managed checks cover
independent gas/power shocks and the PPA connected/frozen-sample scenario bridge.
All 662 frontend checks pass. Independent adversarial review has no remaining
material finding. See [the implementation scope](demo-instruments.md).

The full managed core run passes 637 checks. Six mixed-instrument browser checks
pass, including PPA retained terms and reversed commodity ordering in heat-rate
entry. Desktop and 390-pixel screenshots were inspected. Standard interactive
startup also seeds all 37 bindings successfully; its owned database was removed
on shutdown. A frontend regression prevents a fabricated single PPA price unit
per contract MW; side prices remain identified separately.

## Market dashboard phase 1 (D265 to D267)

`tools/check_all.py` passed on 2026-10-30 on macOS 26.6.2 arm64 with .NET
10.0.402, Node 24.22.0, stock PostgreSQL 16.14 and Chrome 156, except that one
live-calculation browser run stopped at the Blotter side panel's "keyboard focus
on a button of the panel's head" check, which passed when run again; the steps
after it then passed. The standard browser run includes 32 Dashboard checks in
the risk suite's browser: widgets added, arranged and reloaded with no extra
curve read, Run now followed until the widget reads the finished run, a Board
widget moved and sized from the keyboard, a curve the catalog lacks refused by
the backend in its own tile, close footers turned to Newer available when a
newer close is found, a newer completed run stated at an earlier close in
working data, tied runs listed and one chosen, and a past context read only.
The HTTP checks cover the latest-run states (`none` with an incomplete run,
`earlier_close`, `newer_available`, a tie with versions and receipt known times,
`chosen`, `risk_run_choice`), and a managed check covers the part limit. The
checks are listed in [market-dashboard.md](market-dashboard.md#verification).

The page benchmark measured the Dashboard at p50 754 ms and p95 847 ms, under
the one-second budget (1,065 and 1,090 ms before one shared curve read, a
Positions read in the page's own context and drawn widgets kept across layout
edits); workload and requests are in
[market-dashboard.md](market-dashboard.md#page-benchmark).

**Not measured:** a definition's run creation and each part's terminal state
commit under the knowledge publication lock (730849102), which every working
context capture also takes. Worker finish latency for definition parts under
concurrent context captures has not been measured; it must be measured before a
hosted release.

## Streamed Blotter valuation (2026-10-30)

The [decision D268](decisions.md) streams the Blotter valuation. Local disposable
PostgreSQL runs on synthetic data verify the contract. Checks added:

- `tools/check_blotter_stream.py`, called from the mixed-instrument and the
  valuation-status gates: frame order (`start`, `rows`, one terminal), the start
  count equal to `trading/positions` total, rows and totals equal to
  `trading/workspace` for the same context, a retained context named in `start`,
  one frame per swap trade (`swap_units`) and per PPA trade (`ppa_units`), valued
  rows before unvalued ones, and D220/D252 unvalued rows unchanged. In the
  mixed-instrument gate, a lock on the interval-series values that only the PPA
  valuation reads holds the run after `start` and every earlier position, so the
  mid-run checks are deterministic: `gate_released` (`trading/positions` answers on
  the same session while the stream waits), `profile_switch_mid_run_is_error_frame`
  (the next frame is `access_denied` and no rows follow, which also makes
  `error_frame_shape` read a real error frame) and `abort_releases_connection` (after a client abort, the run's backend leaves its
  lock wait while the lock is still held, so the server stopped the run; the check
  does not distinguish a pooled connection from a closed one, and the API logs no
  error).
- `tools/check_blotter_stream.py` `run_hosted_checks`, in the hosted gate: a
  sign-in session that expires while the stream read waits before `start` is
  refused with HTTP 403 `access_denied` by the stream's authority check (before
  the fix the stream started and only its terminal frame failed). Sign-out cannot
  be raced mid-run: it revokes under the security epoch and so waits for the run's
  read transaction, then fails the final recheck.
- `tests/OpenEtrm.Api.Checks` `BlotterStreamChecks`: the writer checks, plus a
  deadline that has passed when `complete` is due (no bytes written, the error frame
  is the one terminal frame), a deadline firing while `complete` is written (written
  whole), and PPA bindings that are not grouped by trade (refused as a defect,
  never reordered).
- `tools/benchmark_blotter_stream.py` (`run_preview.py --blotter-stream-benchmark`):
  `incremental` and the performance rule below.
- `web/tests/blotter-stream.test.mjs`: 16 tests of the client reader and
  `streamBlotter` (frame order, count and context rules, truncation, error frame
  shape and an error frame before `start`, `start.asOf`, row entries, empty pivot
  rows, the `complete` members, non-200 bodies, abort, including abort while a read
  waits on a silent transport). The frontend suite passes 851 checks.

Benchmark, one machine over loopback (macOS 26.6.2 arm64, 14 logical CPUs, 48 GiB,
PostgreSQL 16.14, .NET 10.0.402 Debug build), 1 warm-up and 5 alternating samples
at a retained 2020-12-18 context, 1 scenario, rerun after the review fixes:

| | standard (33 positions, 1 book) | long PPA (34 positions, 2 books) |
|---|---|---|
| `trading/workspace` p50 | 1463 ms | 12023 ms |
| stream first rows p50 | 686 ms | 670 ms |
| stream complete p50 | 1625 ms | 12068 ms |
| API RSS sampled peak | 1.40 GB | 4.75 GB |

The standard workload has 4 swap, 1 futures, 2 heat-rate and 4 PPA unique contracts
(1 futures contract, 24 swap trades, 12 swap valuations, 5 PPA bindings evaluated).
The long workload adds one twenty-year hourly five-side PPA (175,320 intervals,
876,600 row evaluations) and completes inside the unchanged 15-second deadline.

Finding: in the long-PPA scope about 68% of the time (7.3 to 7.9 s) is the shared
`PpaPortfolio.Prepare`; the long binding's own valuation is about 3.3 s. Every PPA
frame waits for `Prepare`, so streaming improves time to first rows (futures and
swaps arrive at about 0.7 s) and frees short PPA trades from waiting on the long
one, but not the long trade's own time. Faster long schedules are separate work.

Before the review fixes the streamed standard run completed in 1588 ms against a
limit of 1419 ms x 1.1 + 50 ms = 1611 ms (12% p50 overhead, 24 ms margin). The fixes
build `complete` without serializing and reparsing the rows and without copying its
members. The rerun completes in 1625 ms against 1463 ms x 1.1 + 50 ms = 1659 ms: an
11% p50 overhead, passing by 34 ms. The workspace p50 itself moved by 44 ms between
the runs, so the margin is within machine noise; the rule was not loosened. The
remaining overhead was not profiled; the final recheck's second read transaction and
the per-frame authority checks are work `trading/workspace` does not do.

Not qualified: no pilot-scale figure (the 25-trade pilot book was not measured), a
Debug build on one machine with the client, API and database co-located, no cluster
or concurrent-user load, and RSS reaches about 4.7 GB with the twenty-year PPA
(the workspace path behaves the same).

Full gate run (`tools/check_all.py`, 2026-10-30, at the reviewed head): every
synthetic verification suite passed. That includes the 851 frontend checks, the 20
HTTP stream checks and the standard-fixture owned-browser run (940 checks). Two
earlier runs had each stopped on a keyboard-focus check in the side-panel browser
checks (the focused element was `BODY`), at different checks. The rerun with the
browser in focus passed them; those checks touch no file of this change.

Known and unchanged: a profile switch from A to B and back to A
between two frames passes the per-frame session check, and the stream completes
under the selection it captured; the profile and its database rights are A's
again, as in the progressive workspace.

Remaining work: no HTTP check reaches the deadline path (the 15-second request
deadline expiring after `start`, sent as a `request_cancelled` error frame); it
needs fault injection of the deadline. The writer's handling of a deadline at
`complete` is covered by the managed checks. No hosted check races a sign-in
session ending between two rows frames; the hosted check covers the same
authority check before `start`.

Independent review (2026-10-30): an adversarial review of the whole branch found
no critical issue and three important ones: the per-frame hosted session check,
authorizing `start`, and the mid-run authority and abort checks in the regular
gate. They were fixed in one wave with eight minor findings, and a scoped
re-review found every finding addressed. Residual, non-blocking: the lock-waiter
probe in the mixed gate matches any waiting backend, and the client module does
not type-check the rights booleans in `start`.

## Blotter read preparation (2026-11-01, local)

The Blotter reports valuation progress but displays positions and totals only
after the complete report passes context and count validation. Backend changes
restrict directory valuation reads to exact primary pins and required trading
milestones, limit swap revisions to selected heads and position metadata to exact
curve keys, compile
only required base-price graph nodes, share ordinary market and retained-side
preparation within an authorized operation, reuse scheduled-swap unit valuations,
and reduce repeated PPA normalization, cadence/dependency and fixing-series work. Financial arithmetic, historical selection and per-position
settlement lifecycle remain unchanged.

Behavior and work checks:

- A base-price preparation with one required contract and 405 selected quote
  points prepares one source node, compared with 405 before, with an identical
  independently calculated price. Scenario discovery still prepares all 405;
  exact retained replay, empty projected demand and required-input refusal pass.
  Within one read, two distinct consumers of two identical dependencies compile
  once instead of twice and allocate about 3.5 MB instead of 6.0 MB. Changed
  data, connection, context, pins or dependency sets cannot reuse preparation;
  each consumer still admits its due fixings and observes cancellation.
- Capacity normalization over 1,000 native intervals and two assets allocates
  about 0.587 MB with prepared immutable inputs, compared with 5.12 MB before.
  Exact asset-version boundaries, gaps, out-of-order intervals, source states,
  duplicate contributions and context mismatches remain independently checked.
- Eight attachments of a retained two-side PPA allocate about 1.12 MB with
  shared side adapters, compared with 2.75 MB independently. Two distinct
  instruments sharing a physical side retain their independent exact PVs;
  changed payment sides and content manifests do not share an adapter. Static
  price-reference discovery reads block templates instead of expanding each
  hourly obligation: the monthly check falls from 41,048 bytes to at most 4,096.
- Blotter swap reads select visible heads before book scope and term hydration.
  Guards prove obsolete and out-of-book terms are not read; retained history
  and editor history remain complete, and hidden required children refuse.
  Ordered projection contracts and weights now share one read: 256 selected
  reset rows cause 256 visibility checks instead of 512. Position metadata
  resolves only each exact curve, reducing its synthetic visibility checks
  from 48 to four while preserving missing-required-curve refusal.
- PPA price cadence admission inspects 1,465 distinct hourly intervals once,
  compared with 1,074,842 inspections before the change, for two months and two
  instruments/four sides. A new preparation owns fresh work; matching starts with
  different ends still refuse. Dependency resolution falls from 1,465 calls to
  two exact curve/month/settlement selections, preserving the expected contracts,
  first-seen order and an independently narrowed one-hour scope.
- A 31-period fixing lookup with 2,048 unrelated valid series allocates 680,384
  bytes, compared with 12,752,608 before. Parsed GUID lookup preserves duplicate
  cardinality. The allocation bound includes its fixed setup cost.
- Scheduled swap integration first failed the duplicate valuation-work check,
  then passed: twice the held quantity gives exactly twice the PV with no extra
  unit valuation. Historical reads exclude later positions; changed leg
  allocations/relative quantity create a distinct instrument and another unit
  valuation without changing the original clone's PV.
- The full PPA sides/numerical, preparation, connected-scenario and normalization
  reuse checks pass, together with side observation compatibility and retained
  booked-schedule lookup checks. Focused migration checks preserve projection,
  financial rows, policies and permissions, reject an unexpected prior view,
  and combine ordered projection arrays while retaining hidden-required-quote
  refusal. Function settings remain scoped to the read; owner, ACL, other
  configuration and refused-upgrade rollback are independently checked.
- Progress-only verification passed 325 focused browser checks and 1,109 frontend
  tests. Denied, truncated and inconsistent-context streams display no partial
  rows; leaving the page cancels the read and late frames cannot restore rows.

An earlier isolated before/after comparison used clean commit `8a15d6e0` and
the initial optimization set, before the quote join replacement was withdrawn
and before integrating D282 statement visibility. Each run used a disposable unmodified PostgreSQL database, one warm-up
and three alternating whole-workspace/stream samples, a retained 2020-12-18
context and one base scenario. Every stream sample's complete rows equalled its
whole-workspace result.

The standard workload has 33 positions in one book: 4 financial-swap, 1 futures,
2 heat-rate and 4 PPA unique contracts. Work is 1 evaluated futures contract,
24 swap trades, 5 PPA bindings and one supplemental market load; swap unit
valuations fall from 12 to 6. The annual workload adds one hourly five-side PPA
from 2031-11-01 through 2032-11-01 in a second book: 8,784 native intervals,
43,920 side-row evaluations, 34 positions and 6 PPA bindings.

| Measurement | Clean baseline | Combined changes |
|---|---:|---:|
| Standard workspace p50 | 1,411 ms | 889 ms |
| Standard stream complete p50 | 1,662 ms | 1,052 ms |
| One-year workspace p50 | 1,918 ms | 1,403 ms |
| One-year stream complete p50 | 2,107 ms | 1,587 ms |
| Standard sampled API peak RSS | 1.444 GB | 0.802 GB |
| One-year sampled API peak RSS | 1.446 GB | 1.182 GB |

Environment: macOS 26.6.2 arm64, 14 logical processors, 48 GiB, PostgreSQL 16.14,
.NET 10.0.402 Debug build, all components on one machine with loopback HTTP
and a Unix-socket database.
RSS was sampled every 100 ms, not a guaranteed high-water mark. An unrelated
idle local preview remained present. The combined changes reduce measured
local latency by about 37% for the standard workload and 25–27% for the annual
workload. Sampled peak RSS falls by about 44% and 18%, respectively; these are
workload-specific local observations, not production latency or general memory
qualification.
Both runs failed the unchanged standard-stream `workspace p50 × 1.1 + 50 ms`
gate: baseline by about 59 ms and changed run by about 24 ms. The existing
relative stream-overhead target remains unmet despite lower absolute latency;
this is not a passing performance qualification.

A separate earlier diagnostic pass, before dependency pruning and shared
preparation, traced ten standard workspace reads at each optimization stage.
Sampled allocation fell from approximately 6.59 GB to 3.09 GB (about 53% less).
The respective 25-second traces contained 14 and 7 generation-2 collections
and 4.0 and 2.9 seconds of GC suspension. Large
attributed stacks included connected market preparation for scheduled-swap
settlement detail and repeated fixing-reference lookup; a three-year hourly PPA
also exposed per-row dependency enumeration. Tracing and concurrent correctness
checks perturb timing. These are diagnostic allocation observations, not
production latency or resident-memory measurements.

The final default HTTP suite passes, including complete historical reads,
position metadata authority, scoped swap heads and the migration work guards.
The final combined scheduled-swap HTTP suite passes exact cash/PV, all six
observation conventions, custom and dated sources, retained amendments,
settlement/netting, risk, delegated authority and upgrade refusals. Independent
adversarial review found no blockers in the SQL scope changes, normalization,
retained-side reuse, connected purpose selection or request-local market reuse.
Native PPA execution still admits and expands retained whole-term programs;
direct execution of their compact vector programs remains work.

Worker-role qualification rejected the proposed quote join replacement: under
one exact lease/context it increased authorization work while returning the
same 29 rows. The prior quote join is retained. Ordered projection sharing
alone reduces the measured worker read from approximately 2.6 to 1.5 seconds
relative to that rejected replacement, with identical results. All live service,
lease, permission and temporal checks and the five-second command bound remain.

## Listed American futures options (D288)

Local runs on 2026-11-01 against the branch head after the master merge (migrations
0161 to 0165) and the final fixes, each in its own disposable PostgreSQL cluster with
synthetic data. The capability is described in [futures options](futures-options.md).

| Run | Result |
| --- | --- |
| MarketData checks `--futures-options` | 33 PASS |
| MarketData checks, default | 655 managed core checks passed |
| API checks, default and `--schedules` | pass |
| `npm test --prefix web` | 1,165 pass |
| `python3 -m unittest discover -s tests -p 'test_*.py'` | 81 OK |
| `run_preview.py --check --exchange-check --browser-check` | exit 0, 202 PASS, including 24 futures-option browser checks |
| `run_preview.py --check --browser-check` (standard) | exit 0, 2,564 PASS, 1,108 owned browser checks |
| `run_preview.py --check --assistant-check --browser-check` | exit 0, 1,104 PASS, 288 owned browser checks |

The pinned risk digest of `--futures-options` passed on macOS arm64; its Linux run is
to be confirmed by CI.

The managed checks test the model against independent references, not against its
own algorithm. A standard-library `decimal` oracle at 60 digits supplies `exp`,
`ln`, `sqrt` and Black-76 values for 17 cases, including high-corner cases with
F·σ·√T up to 400. At a unit discount every call and put lies within
0.125·F·σ·√T/400 of Black-76; the largest observed ratio is 0.0996, CRR's leading
at-the-money error term. Further checks cover bounds (at least intrinsic and the
European value, calls at most the futures price, puts at most the strike),
monotonicity, step stability against 1,600 steps, a two-step hand tree that
exercises at an interior node, calibration round trips to half the clearing
increment, the fallback for each right and direction, the zero floor on shocked
fallback values, named errors outside the bounds, held-volatility delta signs,
replay and a pinned risk digest. Temporary mutations confirmed that the hand-tree
and accuracy checks fail when early exercise is offered only at the root or when
volatility is inflated by 5%.

The exchange preview check books one book holding a CL future, a long ON call (3
lots, 1 exercised), a short LO put (2 lots, 1 assigned), a deep in-the-money ON call
in fallback mode and their premiums. It verifies that each option's value equals
settlement × multiplier × open lots within half an increment per lot; pending
premium values against an independent simple ACT/365F discount; settled premium
cash; linked futures from exercise and assignment; delta and gamma signs; queued-job
replay equal to the immediate report; and the exclusions for a missing settlement,
a missing underlying and an unprocessed expiry. The browser checks book through
the option ticket, read the Blotter, retry idempotently and show backend refusals.

Workload, measured on the branch before the master merge (the merge added retained gas
to the shared risk path; these timings were not re-measured after it), one full risk
run of the example's first book: 6 option positions across 5
unique series (4 solved, 1 intrinsic fallback), with futures, premiums and seeded
futures in the same run; 3 bumps and 7 scenarios (base plus up and down per bump);
50 calibration tree prices, 8 scenario tree prices and 30 unit-pricer calls. The
immediate report took 3.22 s, the Blotter read 2.83 s and the queued worker 4.66 s
from process start to exit, with a sampled worker peak of 286 MiB (sampled every 50
ms, not a guaranteed high-water mark). A later run of the same workload gave 3.11 s,
2.71 s, 4.49 s and 292 MiB. One 400-step price takes about 71 ms and allocates about
101 MB in a Release build (113 ms in Debug); one calibration takes 0.65 to 1.0 s per
series in Release, and the run calibrates distinct series in parallel.

Environment: macOS 26.6.2 arm64, Apple M4 Pro, 14 logical processors, PostgreSQL 16,
.NET 10.0.402 Debug build for the preview runs, Node 24.22.0, Chrome, all components
on one machine. These are local workload observations, not a performance
qualification. The digest check has not yet been observed on Linux for this change; CI is to confirm it.

## D302 shared mechanisms and operating configuration

Qualification on 2026-11-02 used isolated disposable stock PostgreSQL 16 and
synthetic inputs. The complete increment was exercised on base `f026c3ec`;
after integration with `5a6cd3e9`, the targeted checks below were rerun. The managed core's 656 checks pass with the retained gas/crude extraction;
its explicit canonical identity and rational PV oracles also passed against the
original concrete implementations before extraction. Shared utilities, API
contracts and the worker build pass. Custom reference-unit symbols survive catalog
presentation, and the reference-editing/integrity suite passes on the full schema.

On the `f026c3ec` baseline, the frontend passed 1,225 checks and Python tooling
passed 84. The complete standard preview passed its 1,213 browser checks and
subsequent backend checks. Hosted HTTPS/browser checks passed, including
capacity-one logout/re-login and SQL-verified revocation reclamation. Assistant
admission/tool/stream/usage checks and 288 browser checks passed; a turn admitted before a policy change retains
its old pin/reserve/output budgets, a new turn uses the new policy, and grant/save
replays keep their original outcome after a cap reduction. Managed output checks
consume actual SQL-admitted limits and refuse oversized complete results.

Owned settings/upgrade checks pass current authority, ownership, exact replay,
no-op revisions, optimistic conflicts, complete bounded input, access-only
administration without market authority, exact session/lockout/invitation durations
and duplicate failed-login handling. Altered privileged helper bodies/ACLs refuse;
completed prior turns, versions, grants and session identity/expiry remain unchanged
without invented policy lineage. Gas and crude HTTP/worker checks reproduce exact
retained mixed-portfolio valuation and shock results. The public gateway passes
38 live owned route checks, and custom process-budget rendering checks cover body
sizes, refusals, longer deadlines and the fixed stream heartbeat margin.

After integrating `5a6cd3e9` (D299 parametric risk), the combined tree passed 1,232 frontend
checks, 84 Python checks, managed API contracts, and owned settings/upgrade SQL.
The dedicated parametric SQL/HTTP/worker suite and its 11 browser checks passed.
These targeted reruns do not represent a full combined-tree CI run.

The subsequent integration onto `587fc183` preserves physical LNG (0176–0179,
D300) and guided VaR (0180, D301); operating settings now occupy 0181–0183 and
D302. A delayed-preference browser regression reproduces the pop-out account
switch failure on the earlier implementation. Verification demands now retain a
fresh following read, and delayed preferences are applied only after rechecking
the same tied identity. The private import fixture explicitly selects connection
budgets; six refusal checks verify that omitting any budget from either login
records no import receipt. The owned import/upgrade suite, 1,242 frontend checks
and 85 Python checks pass on this integration. The fixed assistant browser suite
passes all 288 checks, and the guided parametric SQL/worker suite and its 16
browser checks pass.

The exchange preview explicitly provisions its separate workspace policies before
agent admission. Its scoped-policy check and full exchange/options HTTP,
lifecycle, retained risk and 35 browser checks pass; missing configuration still
refuses at runtime.

Independent adversarial source review found no remaining material defect after
these fixes. This is local implementation qualification. An attested Docker run
with the selected prior image, paired configuration rollback and authenticated
service-recovery checks remains outstanding; the current rollback smoke covers
public authentication-page availability and anonymous denial only. See the
[operator transition](hosted-pilot.md#operating-configuration-transition-d302).

## Facility quantity ledger (D303)

The [facility contract](facility-quantity-ledger.md) has independent managed
checks for exact source conservation, fractional target quotas, native-unit
compatibility, source corrections, preserved measured operating breaches and
missing/withdrawn inputs. The full managed core suite passes 656 checks; managed
API/preview adapter checks pass. The Python verification runner passes 85 checks,
and the presentation suite passes 1,244 checks. These are local synthetic results.

The owned stock-PostgreSQL SQL/HTTP workflow books three real synthetic physical
gas trades and allocates a shared source. It qualifies immutable/no-op facts,
actor-scoped command replay, cross-account owner refusal, exact references,
retained cutoffs, concurrent replacement conflicts, operating-limit withdrawal
and correction reconciliation. Incomplete privileged writers separately test
constraint-trigger conservation, target quotas, native days, stale source
pins and forward source-publication refusal. A clock shift cannot create another pool. Own-trade allocation projections
show no source/plan fingerprints or other-party data and remain identical when
only another target changes.

The dedicated browser workflow passes 20 facility checks; the accompanying gas
and deal-ticket workflows pass 16 and 28 browser checks. Coverage includes
responsive facility views, loaded editable
controls, shared multi-select removal, unload/navigation guards, cross-form draft
protection, corrections, limit removal, lost-response status recovery and
navigation with unavailable browser storage. Historical Apply withholds/reloads
current values, explicit market-close selection enables read-only trade inspection,
own allocation tracks stay readable when contractual PV is unavailable,
and account changes clear old values. Administration checks verify the current
role catalog, initially unassigned facility roles, explicit assignment through the
normal operation and separation of recording versus limit configuration. Existing gas booking, setup, composition,
daily quantities and deal-ticket checks run alongside the facility workflow.

Use `tools/run_preview.py --facility-quantity-check --gas-daily-quantity-check
--gas-composition-check --gas-setup-check --deal-ticket-check --check
--browser-check --browser /path/to/browser`, with the explicit supported SDK and
stock PostgreSQL flags described above. The trading CI group includes the facility
workflow. No production or migration rollout, broader commodity qualification,
final billing adoption, notification dispatch or throughput benchmark is claimed.

## Facility read plans (2026-11-03, local)

Hosted runners timed out the Facilities page's first directory load after the
preview's 5-second command limit. The command was
`security.facility_quantity_directory`. Its per-ledger visibility checks
(`trading_visible`, `reference_version_visible`) sat on the ledger row of a view
that joined labels from five reference tables. Autovacuum vacuumed
`ref_data.locations` (row count, no statistics) and analyzed `ref_data.units` and
`ref_data.quantity_specifications`. After that the planner joined the reference
tables first: 106,272 combinations, probing the ledger 13,284 times and running
both checks on each probe. `security.facility_quantity_choices`, which the same
request runs next, degraded the same way: `facility_gas_target` resolved the
trade's latest visible revision for each of the schedule's 31 rows before it
matched the requested day. Migration 0185 reads the labels by exact key and
resolves the revision once per target. Results are unchanged.

Workload: the owned preview of the trading CI facility step (`--facility-quantity-check
--gas-daily-quantity-check --gas-composition-check --gas-setup-check --browser-check`),
measured just before the facility browser workflow starts. The catalog then holds one
facility ledger, 14 gas delivery obligations, 94 schedule grids and 222 target choices,
on one reference revision. Each read ran as the trader in a captured 2021-01-15 working
context, using `EXPLAIN (ANALYZE, BUFFERS, TIMING OFF)`: five consecutive calls in one
transaction, with the median reported. Before and after ran against the same database,
replacing only the 0185 objects. Environment: Apple M4 Pro, 14 cores, 48 GB, macOS,
stock PostgreSQL 16.14, all pages in shared buffers. Pages are deterministic for a
plan. Time is not. Other local workloads were running during the choices run (load
average 13 to 20), so its times are higher than the directory run's.

| Read, statistics left by autovacuum | Before | After |
| --- | --- | --- |
| `facility_quantity_directory`, one ledger | 2,958 ms, 1,769,720 pages | 1.1 ms, 2,182 pages |
| `facility_quantity_choices`, 222 targets | 3,637 ms, 1,189,144 pages | 838 ms, 165,799 pages |

Before autovacuum reached those tables, the directory read took 15.5 ms and the
choices read 420 ms, so the plans in use depended on maintenance timing. With 0185
the directory returned byte-identical JSON. The catalog-wide and ledger-scoped
choices also returned byte-identical JSON (matching MD5).

The facility ledger check repeats exactly that maintenance and bounds both reads
by pages read, which unlike time does not depend on the machine. With 3 synthetic
trades the reads take 1,650,158 and 185,346 pages before 0185 and 2,160 and
48,916 after (bounds 20,000 and 100,000). The trading CI facility step passes locally,
including 20 facility and 16 physical gas browser checks. The `Command Timeout` is unchanged.

The choices read still proves the trade's authority once for every candidate
(one call per obligation-day). Its cost grows linearly with the catalog's
single-day gas targets, up to its 1,001-result limit. No hosted-runner timing is
claimed here.

### Set-based catalog-wide choices (migration 0221, 2026-11-08, local)

Migration 0221 replaces the per-candidate work in the catalog-wide branch of
`facility_quantity_choices`. `resolve_facility_gas_targets` resolves all requested
obligation-days in one statement, deciding each trade's latest visible revision and
each schedule's publication once. The read then proves each trade's authority once.
`facility_gas_target` delegates to the same resolver after its unchanged authority
check, so a target has one definition. The ledger-scoped branch keeps its
per-candidate checks, which its one gas day bounds.

Workload: the owned preview of the trading CI facility step above, with statement
logging (`log_min_duration_statement=0`), before and after 0221 on the same commit.
Environment: Apple M4 Pro, 14 cores, 48 GB, macOS, stock PostgreSQL 16.14. Other
local workloads were running (load average about 5 before, 10 after).

| Read, median of the calls after the fixtures load | Before | After |
| --- | --- | --- |
| `facility_quantity_choices`, catalog-wide | 1,544 ms | 282 ms |
| `facility_quantity_choices`, one ledger | 41 ms | 77 ms |

The ledger-scoped read is slower because each candidate's `facility_gas_target`
now plans the larger set-based statement. Its pages stayed close in the scaled
comparison below (44,078 before, 49,972 after).

Scaled comparison: the same preview after the facility check, with 40 more 31-gas-day
trades (45 obligations, 45 trades, 1,275 candidate obligation-days, results capped at
1,001), both definitions installed side by side and read as the trader in the same
context. The catalog-wide read went from 3,059 ms and 935,410 pages to 547 ms and
48,051 pages. Independent review found joins between two materialized stages that
were quadratic in the requested obligation-days, which pages do not show. A nested
loop cannot implement a full join, so each of those joins is written as one inside
its own stage. At the catalog's 256-gas-trade booking limit (about 7,900 candidate
obligation-days, results capped at 1,001), the catalog-wide read went from 15.5 s to
1.04 s and returned byte-identical JSON, as did the ledger read. The resolver still
processes every candidate before the 1,001-result limit applies, so its cost follows
the catalog's obligation-days.

The scaled figures above predate the second full-join stage. An independent
synthetic fixture at the same limit (256 trades of 31 days, with stubbed authority
helpers) then showed the generic plan on unanalyzed tables, reached after five calls
in one backend, falling from about 21 s to under 2 s. No plan across 34 combinations
of scale, statistics and forced custom or generic planning nested two stages. With
analyzed statistics, the read's candidate statement can also change to a generic plan
after five calls: on that fixture it read about 1.58 million pages against 228,000
for the old definition, while taking less time. The check file pins the answers but
cannot detect a return of the quadratic plans, because they give the same answers.

Do not set `enable_nestloop=off` on this resolver. A plan that must still nest adds
the planner's disable cost (about 10^10), which exceeds `jit_above_cost`. The first
version did this, and hosted CI timed out on a three-trade fixture at the 5-second
limit, while a local server without JIT support read it in 20 ms. JIT was inferred
from the plan cost; the runner's server log was not available.

Answers and refusals were compared in that database: 360 catalog, ledger and unknown-ledger
reads and 732 single-target resolutions across trader and risk profiles, dates, instants
and rolled-back faults (duplicate quantity roots, a late publication, a late trade,
a missing trade knowledge receipt, no facility read permission, a missing counterparty
label) returned identical results or errors. Two further faults were refused by table
constraints before reaching the reads. The facility check bounds the
catalog-wide read at 15,000 pages (49,921 before 0221) and checks ordering, quantities,
an unknown ledger and one trade dropping out alone. No hosted-runner timing is claimed.

## Synthetic gas basis and final facility inputs (D321)

See [the recording guide](gas-basis-demo.md#local-verification) for the owned
scenario, executable command and local results. The trading partition runs
`--gas-basis-demo-check --browser-check` in addition to the existing facility,
daily gas, composition and setup regressions. New-schema hosted qualification
remains separate.

## Integrated physical delivery qualification (D321–D324)

Qualification after integrating the current curve-definition and assistant
navigation changes passed 657 managed core checks, 1,358 frontend checks, the
managed delivery-starting-values checks, 85 Python tests and 32 release-admission
tests. Deployment configuration rendering passed. Owned stock-PostgreSQL
workflows passed the gas basis/history/final-adoption scenario with four browser
checks, the facility/gas regression with 20 facility and 20 gas browser checks,
and the combined workspace/crude workflow with 22 browser checks.

Migration 0197 is qualified alongside the preceding assistant migration 0196.
Independent adversarial review found no outstanding material issue. Public
source excludes local configuration, generated assets, logs and private review
artifacts. Hosted schema/API activation remains a separate qualification.

## Physical operations grids (D342, local)

The frontend build and all 1,384 web checks pass. Focused checks cover exact
binding selection after sorting, global/column filters, all native side/day
rows, exact rational sorting, zero versus absent/withdrawn quantities, dirty
form retention, denied-read cleanup and repeated arrow-key selection. Service
entry stays on the highlighted obligation; transfer choices retain authorized
counterparties while restricting the owned leg to that obligation.

Owned stock-PostgreSQL scenarios pass 10 gas scheduling/recovery, 15 gas billing
and 47 crude lifecycle browser checks using the new trade/delivery grid helpers.
These retain the existing nomination acknowledgments, source adoption, correction,
lost-response recovery, inventory, dispatch, invoice and payment assertions.
Desktop and 390-pixel visual inspection confirms the grids and selected delivery
workflow fit their containers; wider tables scroll internally. Independent
adversarial review has no outstanding material finding.

This qualifies presentation and selection over the existing manual operations.
It adds no live operator interface, new financial policy, throughput claim or
hosted deployment qualification.

## Common physical delivery view (D343, local)

The frontend build, all 1,388 web checks and 659 managed core checks pass.
Independent quantity/geometry assertions qualify the shared retained-output
projection for gas, crude and LNG. Grid checks cover custom track names,
15-minute intervals, exact fractions, microsecond bounds, binding/revision
refusal and cargo TBD capabilities alongside the existing edit/recovery checks.
The intraday specimen qualifies presentation only, not power operations.

Owned stock-PostgreSQL qualification passes 10 gas operations, 47 crude lifecycle
and 14 LNG browser checks. Native read checks preserve whole-window quantities,
exact schedule/source/reference scope, retained context and stale-selection
refusal. All three adapters refuse with an authorization error when independently
expiring ReadMarketData authority lapses during an observed post-program
unit-label query wait while ReadTrades remains granted. That source recheck
precedes classification of labels filtered out by row security.

Desktop and 390-pixel gas grid inspection passes. Independent adversarial review
has no outstanding material findings. D343 adds no schema migration or financial
write policy; existing operational and release qualifications remain applicable.

## Physical operations release integration

The integrated schema uses migrations 0205–0207 after the existing curve,
regional-close and assistant-presentation migrations. Assistant admission advances
from the existing v17 boundary through v18/v19 to v20; retained replay and the
previous presentation vocabulary remain intact. Decision records D337–D343
describe this physical increment independently of the preceding records.

Qualification passes the API build, 663 managed core checks, API schedule and
assistant contracts, 1,398 web checks and 86 Python tooling/release checks.
Fresh stock-PostgreSQL workflows pass 10 manual gas operations, 19 gas billing,
47 crude lifecycle and 14 LNG browser checks, alongside the existing gas/setup
regressions. The assistant runtime passes turn, HTTP tool, stream and usage
regressions with v20 admission and older-version refusal.

Close-session changes use the existing context leave guard before changing the
saved session or selected frame. Real browser checks preserve dirty billing and
the exact header on Keep, apply the session on explicit Discard, and retain one
unchanged uncertain determination request across a session switch. The trading
CI partition now includes both manual gas and billing recovery workflows.
Independent adversarial review has no outstanding material finding. These are
local qualification results; hosted rollout remains separate.

## Contract-date set visibility (D363, local)

`tools/check_contract_dates.py` captures the API's real library statement for a
viewer, editor and approver and counts function calls in its transaction: no
per-row version or schedule check, and at most one set per secured relation. Over
every version in the library, the set equals what the per-row function admits for
each profile: current, bound to a current context, before a publication's knowledge
cut, and outside a schedule's window. It also covers a login with no bound request,
a request with no read authority, a draft over an unpublished schedule, a schedule
or calendar known only after the cut, a revoked calendar scope, and a second readable
catalog against a bound context. The authority window is checked for read-only,
historical and authoring authority that ends before read authority. A raw draft
write decides the set once for its several fields. The check is run locally against
a disposable database; independent adversarial review findings are resolved.


## Physical power curves and booked close comparisons (D376–D381)

`--power-booked-roll-check` runs owned stock PostgreSQL/HTTP qualification for
product forwards, published daily/monthly splices, location bindings, selected
TOU demand, saved daily additive shapes, immutable hourly MW profiles, retained
booking parity, source compatibility and captured correction replay. It includes
real 23/25-hour grids and the timed DA knowledge-eligibility boundary.

`--power-context-check` gives identical curve codes different marks in two
sources. The power graph must consume its recorded non-primary source; a close
omitting that source refuses. Managed close admission retains the head known at
each explicit cutoff, rejects an after-cutoff close even with old observations,
and requires the replacement once that replacement is visible.

Native `--power-flat`, `--power-atc`, `--power-product-averages`,
`--power-interval-quantities` and `--ppa-sides` checks assert independent interval,
quantity, PV and scenario oracles. `--close-risk` includes the zero-row power
graph worker fence. The standard frontend suite covers saved publication grids,
price-inspection controls, graph types and retained hourly profile presentation.
See [the capability contract](power-curve-composition.md) and
[the booked numerical examples](power-booked-roll.md) for exact limits. No
production finality, accounting or performance qualification is implied.


## One dark theme (D383, local)

The standard frontend suite holds the theme. The stylesheets state no colour
outside the token block and read no custom property that is neither defined
there nor set by a view. Text, mark, focus-ring, volatility and curve-kind
tokens meet the D383 ratios on every surface they are used on. The shared
chart's colours equal the tokens and the palette, each palette colour is 3:1
or better on every chart surface, and heatmap cell labels keep 4:1 or better
along each ramp. The sign-in page states only theme colours.

Browser checks draw real grids (rows, an open row, groups and their totals, a
pinned totals row, pages, a coming-soon column and the empty state), native
controls with a dialog, and the parametric, historical and Monte Carlo VaR
report views from synthetic reports with every collapsed section opened. An
audit of each drawn page finds no opaque ground above a relative luminance of
0.2 on an element 32px or more each way and no text, a form control's value
included, under 4.5:1 on its ground; a dialog's ground is the card surface.
The audit does not judge dimmed or disabled content, text over a background
image, generated content or a state that needs the pointer. The grid check
fails against the grid library's own stylesheet alone.

Local checks on 2026-11-12, each preview against its own disposable database:
`npm test --prefix web` (1583 tests); the standard preview with its HTTP and
browser checks (market navigation, the profile dialog, administration, power
TOUs, parties, futures, the trade editor, swaps, risk and dashboard, tables
and the Blotter, units and notices); the hosted sign-in preview with its
browser checks; the owned browser tests and the verification runner. The full
`tools/check_all.py` was not run.

In a local preview the market board, a business-date page, the Blotter as a
read-only account and the header were looked at with the same audit. This
branch's charts, VaR report views and 3D volatility surface were looked at on
synthetic data drawn from the built bundle. No release was deployed, and no
other page or state was looked at in a running application built from this
branch.

## Date-only As of with its details beneath (D384, local)

A unit check holds the As of label: a recorded close is named by the business
day the backend states, also where its instant falls on another day in the
display zone, and a working snapshot by its instant. A markup check holds As
of details after the Market close and Business date inside their own group.

The browser checks of the header ran in the standard preview (its market
navigation checks include the header's), the market close calendar preview
(calendar and regional close session checks) and the context read preview
(context recovery checks). They require that the As of is a
date without a time, that the collapsed line sits beneath it and names exactly
the close convention in use with the knowledge controls hidden, that opening
it shows the convention, the selected close's instant and the knowledge
cutoff, and that an absent default stays collapsed, selects nothing and asks
for a convention.

The header was also looked at in a local preview at desktop and phone width,
collapsed and expanded, and on a page read by business date, where the line
reads As of details beneath the Business date. A working snapshot and a
historical view were not looked at in the running app.

## Contract observation curve terms (D391, local)

The managed core suite holds the operator's numerical oracle on synthetic crude
futures E–J (expiry 19:30 UTC), a nine-day business calendar and 12:00 UTC
sampling. For the 2027-01 target, a January business-day average plus 2:1
weighted December roll spreads of F against G and H, it asserts exact rationals:
69.96 on 2026-12-01, where F, G and H each +1 give 70.96, 69.96 − 1/15 and
69.96 + 1/15; the same price on 2026-12-22 with F frozen, where G and H +1 give
70.56 and 70.36; 70.02 on 2027-01-20 with the context day frozen, so G is no
live input and H +1 gives 70.42; 70.04 on 2027-01-19, where G +1 gives 70.24
(one live day in five) and H +1 gives 70.44; 20983/300 on 2026-12-17 inside the
trade month, where F, G and H +1 add 1/4, 13/30 and 19/60; 70.00 on 2027-01-25
with no live future;
69.90 when sampling at 20:00 moves the expiry day to the next contract; 68.46
with a −1.50 differential; and a two-target case at 10724/155 and 4807/70. It
also asserts the windows, declaration identity, formula text and each refusal,
with the term number (from 1, as the editor labels rows) and member each term refusal names, including a `null`
position and a group whose weights cannot be summed, and the `recipe_input`
refusal of a context frame without the context day's contract, with only an
earlier mark of it, or offering a frozen observation from a past frame, and the
refusals, each with its own message, of a composite (`Add`) source and of a source
carrying contract price terms on an observation-day context. Planning a consumer at
2027-01-20 refuses with `recipe_input` given only the context instant; given
every window day's sampling instant it plans the sixteen expected native inputs,
the context day's G among them as a frozen input, and the curve prices 70.02
under that demand. The API check `--contract-observation-reads` admits 366 past
observation days and refuses more, counting a day once; all of 2024 with
2025-01-01 as a frozen context day is admitted, because the context day reads
the context frame.

```sh
dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --contract-observation-recipes
dotnet run --project tests/OpenEtrm.Api.Checks -c Release -- --contract-observation-reads
```

Every owned preview qualifies migration 0252 in its migration loop
(`tools/check_contract_observation_upgrade.py`): the hash pins refuse a changed
panel guard body, owner, configuration, ACL and node-kind constraint. The
populated qualification runs once, under `--contract-observation-curve-check`,
on the base preview fixture loaded in one transaction on the prior schema (the
crude fixture is applied after the migration loop): with a published
`weighted_panels` combination seeded there, the version reads back unchanged
after 0252 and both panel guards still refuse a source input. The contract
dates mode's `window_frames` fixture is the one that cannot load in one
transaction: it seals a pinned contract curve whose date admissions that mode
inserts only by rewriting the load.

The owned stock PostgreSQL preview loads the synthetic crude fixture, then runs
the storage check (table rules, security parity, publication refusals,
immutability and the unchanged panel guard), the HTTP curve check and, with
`--browser-check`, the Curve builder suite:

```sh
python3 tools/run_preview.py --pg-bin /path/to/postgres/bin --dotnet /path/to/dotnet \
  --contract-observation-curve-check --browser-check --browser /path/to/chrome
```

The published curve prices 68.46 on 2026-12-01 and on 2026-12-22, 68.52 on
2027-01-20 and 68.50 on 2027-01-25, each with the expected live inputs; on
2027-01-20 only H and the differential are live. A consumer reading it through
`published_source` prices the same values, with the same live inputs on
2027-01-20, and
`market/dates` for either definition lists the four priced closes. Before the
2027-01-19 settlement is recorded the finished average refuses with
`recipe_input`, and the knowledge view captured then still refuses afterwards.
A correction to 69.55 gives 68.51, also through the consumer, while the view
before it reproduces 68.50. A deliberately incomplete fifth close on 2027-01-22
omits H, the contract that observation day selects, and the preview refuses with
`recipe_input` naming the day. A term whose position is `null` refuses with
`recipe_definition` naming the term. The check inserts the late settlement and
its correction by SQL: the correction endpoint stamps receipt at the current
time, and the observations table requires `observed_at <= received_at`, so a
correction of an observation dated after now cannot be recorded through it. The
browser suite passed 13 checks: catalog-driven settings and terms, preview at
68.46, save as `observation_formula`, unchanged reload, and the refusals of a
cleared weight and of a new term with no position shown beside the operation.
`tools/check_all.py` includes this command.

Review fixes (local, 2026-11-13). The same command also publishes the
`published_source` consumer and an ordinary differential curve, then runs the
API check `--contract-observation-projections` against the owned preview
database: under the trading read of the 2026-12-01 working frame, projection
choices offer the ordinary curve and neither curve that reads contract
observation terms, and projection admission accepts the ordinary curve and
refuses both with `custom_projection`. Before the fix both were offered and
accepted. Forged references to either curve are refused with the same code and
message by `swaps/preview`, `swaps/book`, `swaps/price` and
`swaps/scheduled-price`; before the fix those routes answered other refusals
(`swap_input`, `trade_party`) and never qualified the projection. The browser
suite now passes 15 checks: it also types `2e` into Less position and Position
of a spread term and checks that each preview sends the typed text, is refused
(`400 request_format`), shows no price and shows the general error, and its
reload check reads back every setting and the source connection. Term refusals
number terms from 1. Results on the final code: the preview above exited 0 with
96 checks and 15 browser checks; `--connected-portfolio-check` (94),
`--draft-check --browser-check` (267; 189 browser), `--gas-indexed-check
--browser-check` (80; 9 browser), `--gas-composition-check --gas-setup-check`
(85), `--custom-curves-check --browser-check` (308; 236 browser) and
`--scheduled-swap-check` (113) exited 0; the managed core suite passed 675; the
API checks passed (98); `npm test --prefix web` passed 1634 tests.

Review fix round (local, 2026-11-13). A `position` or `lessPosition` present as
text, a fraction, a whole number beyond the 32-bit range, a boolean or an object is refused while the body is read as
`recipe_definition`, `An observation term position must be a whole number from 1 to 120.`:
the managed core check binds each case and keeps `null`, a whole number and an
absent member (a JSON error); the HTTP check gets `422` with that message from
preview and save for `"2e"`, `1.5` and `3000000000` and `400 request_format` for an absent
`position`; the browser suite shows that message beside the operation for `2e`
in Less position and in Position, with no price. Before the change all four
HTTP answers were `400 request_format`. The API check also publishes a second
version of the ordinary curve that reads the formula curve: its first version
stays offered and accepted but is no longer marked as the version in force
(before the fix it was). `swaps/observation-options` now refuses a forged
reference with `custom_projection` (before, it answered 200 with no options).
Results on the final code: `--contract-observation-curve-check --browser-check`
97 checks and 15 browser checks; `--gas-amendments-check --browser-check` (102),
`--gas-in-term-check` (87), `--index-fixing-check` (90),
`--close-out-drafts-check --browser-check` (123) and `--side-observation-check
--browser-check` (305) exited 0; the managed core suite passed 675, the API
checks 98 and `npm test --prefix web` 1634 tests.

Local results on 2026-11-13, on this change merged with the current master: the
managed core suite exited 0 with 675 managed core checks passed; the API checks
passed (98); `npm test --prefix web` passed 1633 tests; the release-deployment,
migration-identity, runner, public-site and public-route unit tests (65) and the
standalone boundary check passed; and the owned previews
`--contract-observation-curve-check --browser-check` (87 checks and 13 browser
checks), `--custom-curves-check` and `--connected-portfolio-check` passed. On
the final code before that merge, these owned previews also passed:
`--custom-curves-check --browser-check`, `--price-authoring-check --browser-check`,
`--market-dates-check --browser-check`, `--pilot-close-check --browser-check`,
`--regional-closes-check`, `--contract-dates-check` and `--example mixed-gas`.
A complete `tools/check_all.py` run was not performed locally on the final code;
continuous integration runs the complete suite.
These are synthetic local checks. They do not qualify a Side, trade or cash
consumer, explicit roll boundaries, or a production activation.

## Park and Loan local qualification (D406)

The bounded [commercial profile](gas-park-loan.md) is checked through:

- `tools/check_gas_park_loan.py`: native lifecycle, all four directions, source and
  authority fences, canonical retries, and 20 independent whole-book value and
  price-scenario checkpoints when the pinned SDK is supplied through `DOTNET`.
- `tools/check_gas_park_loan_upgrade.py`: populated predecessor retention and nine
  function-contract drift refusals.
- `tools/check_gas_park_loan_worker.py`: nine actual retained price jobs, complete
  ordinary/report parity, native owner identities, exact source and selector
  fences, original actor revocation and natural worker gas-grant expiry.
- `tools/check_gas_park_loan_browser.py`: 68 checks in the real owned application,
  including uncertain booking and advance responses, principal and fee operations,
  retained terms, explicit fee applicability and normal Risk controls after advance
  for all four service/perspective combinations.

These are required once in the gas CI partition. Local runs passed with stock
PostgreSQL 16, the pinned .NET 10.0.402 SDK and Chromium. An existing futures
historical/Monte Carlo worker consumer also passed, protecting unrelated worker
admission. Independent adversarial review found and verified fixes for policy
changes after preview, knowledge-first payment heads, worker source authority and
UI time-resolution races. Aggregate CI and exact-release checks remain mandatory;
local qualification does not activate a production schema or qualify service
horizon continuation.

## Outstanding-balance fee qualification (D410)

The [balance-time fee contract](gas-park-loan-balance-fee.md) has four required
consumers in the gas partition:

- `tools/check_gas_park_loan_balance_fee.py` verifies native booking, principal
  and fee operations, exact rational/cumulative rounding, explicit refunds,
  authority, immutable operation seals and 26 independent whole-book price and
  scenario checkpoints with the selected SDK supplied through `DOTNET`.
- `tools/check_gas_balance_fee_upgrade.py` applies the additive migration over
  populated fixed-fee agreements, preserves exact prior facts and outcomes,
  and refuses altered protected producer, probe and privilege contracts.
- `tools/check_gas_balance_fee_worker.py` executes nine retained PRICE jobs,
  including the required unavailable-plan refusal, ordinary/report parity,
  independent paid/refund cash and exact source and authority checks. Economic
  continuation refuses zero-rate, rounded-zero and principal-complete cash-only
  scopes; excluded scopes remain unaffected.
- `tools/check_gas_balance_fee_browser.py` operates shared profile entry,
  reopening, lifecycle and normal whole-book Risk in an owned application with
  synthetic data and disposable PostgreSQL. It checks independent service,
  principal and cash owners, monetary review fields, lost-response recovery,
  local-time display and phone layout.

The integrated local managed core passed 739 checks, the frontend passed 1,943
checks with none skipped, and Python passed 142 checks. API and worker builds
passed with zero warnings or errors; the full API schedule checks and native,
populated-upgrade and queued-worker consumers passed on stock PostgreSQL 16
with the pinned .NET SDK. The combined artifact passed 185 actual fee browser
checks and the 72-check fixed-fee browser regression, including native
postconditions. Review screenshots show the monetary facts and record control
on desktop and phone. All owned resources were removed. The complete CI matrix
still gates the exact commit. These checks do not qualify complete economic
continuation, quantity scaling, correction adapters or production activation.

After integrating the current master and facility foundation, migration 0350
retains the original fee SQL unchanged. Local qualification passes 740 managed
core, the full API schedule suite, 1,966 frontend and 144 Python checks. Native
qualification retains all 26 independent whole-book checkpoints; populated
upgrade, nine retained PRICE jobs, 185 balance-fee browser checks and 72 fixed-fee
browser regression checks pass on the same application artifact. Independent
review verifies both parents' source and verification registrations are retained.
Aggregate CI and production activation remain separate gates.


## Retained-title parking qualification (D411)

The [custody contract](gas-park-custody.md) adds five required commands in the gas
partition: native/source/value qualification, populated predecessor upgrade,
queued PRICE workers, normal application browser and restricted-provider browser.
Twelve managed boundary commands run once in the managed partition. The runner
tests prove those placements and selected-tool propagation.

The native/managed matrix passes 18 actual service/source checkpoints
(13 whole-book valuations and five explicit empty-financial refusals), including both fee signs, exact retained customer stock, provider
custody without commodity inputs, and paid/due cash without unused rates. A
zero-fee provider retains physical custody and service value zero; the empty
financial portfolio refuses explicitly, while independent stock in the same book
is valued normally. Payment heads are resolved by knowledge before business-time
application, including a later known bank revision effective after evaluation.

Independent adversarial checks cover exact typed parent/member receipts,
exclusive original/remainder reservation ownership, generic release/spending
bypasses, all accepted stock boundaries, future-effective source changes,
quantity corrections, current-source reconciliation and first-outcome recovery.
Raw keys canonicalize ASCII padding and refuse control characters at both public
RPC and typed persistence boundaries; free multiline evidence remains supported.
Opaque non-ASCII-space keys and supplementary characters also pass native-to-managed
rereads, with matching 128-scalar key and 2,000-scalar free-evidence bounds.
Populated upgrade preserves fixed-fee, balance-time and ordinary premium cash
facts, aliases, historical cuts, original outcomes and later supported operations,
and refuses protected producer/worker-boundary drift.

Actual source-history lock waits separately qualify missing original owned
authority, expired original owned authority and expired original gas-operations
authority despite valid successor grants. Provider setup succeeds without owned
authority. Explicit customer discovery obeys its original captured access.
The exact outer lifecycle source instant also passed an HTTP precision check:
valid input returns a review, while a nonzero sub-microsecond fraction refuses
instead of being truncated into the same source identity.

Three actual custody PRICE jobs match their ordinary complete reports and retain
source, scope, actor/delegator/current-worker authority and original deadlines.
The existing fixed-fee and balance-time worker regressions pass nine jobs each.
Economic horizon, generated-close and cross-clock P&L consumers refuse the
unqualified custody continuation for zero, future-effective, completed and
cash-only states; excluded scopes remain unaffected.

The corrected combined runtime passes 111 normal and 48 restricted-provider
full-application browser checks. They cover shared profile entry, booking and
reopening, raw/superseded/unapplied observations, refused mismatched performance,
partial/final returns, independent bank payments, uncertain-response recovery,
completed-custody inspection, user-zone times and phone layout. Desktop, phone
and maximized accepted/raw-history screenshots were inspected independently.
Existing fixed-fee and balance-time application regressions pass 72 and 185 checks.

Managed core passes 740 checks, the full API schedule checks pass, and API builds
have zero warnings/errors. The frontend passes 1,970 full checks followed by 19
focused final presentation checks; Python passes 144 checks. These results use
synthetic data, stock PostgreSQL 16 and the pinned .NET SDK in disposable
resources. Aggregate CI gates the exact published commit; release and production
activation remain separate.

After integrating the current master, facilities and balance-time fees,
migration 0351 retains the custody SQL unchanged. The integrated artifact passes
741 managed core checks, the full API schedule suite, 1,993 frontend and 146
Python checks. Native qualification retains all 18 source/value checkpoints;
populated upgrade, three retained PRICE jobs, four original-authority wait
controls, 111 normal and 48 restricted-provider browser checks pass. Independent
merge review verifies both parents' source and verification registrations are
retained. All checks use disposable synthetic resources; aggregate CI and
production activation remain separate gates.

## Requested assistant context and private prompt configuration (D407)

Local verification on 2026-11-16 uses disposable PostgreSQL databases, synthetic
prompt text and loopback model providers. The managed `--assistant-prompts`
checks pass: prompt composition contains only configured text; absent tool
guidance stays empty; all five provider mappings omit absent optional system
instructions. An oversized synthetic close retains separate selected-frame and
exact-close identities without copying its input matrix.

`--assistant-workspace-check --browser-check` passes 65 integration and 43
browser checks. Coverage includes questions and administrative reads without
financial acquisition, exact requested financial reads, recorded-close proposals,
historical aliases, explicit human authorization, and application/page/tool
configuration. An in-flight configuration edit cannot change the prompt or add
guidance absent at admission. Both migrations refuse helper definition or privilege
drift; populated upgrade checks preserve existing prompt rows exactly and add no
default guidance.

The initial checkpoint passed the provider and administration modules of
`--assistant-check`: provider, model catalog, qualification, model version, configured connectivity test,
agent administration and grant checks. `--assistant-runtime-check --browser-check`
exited successfully with 108 native turn, 22 HTTP turn, 65 HTTP tool, 20 stream,
44 usage and 295 owned browser checks. Permission refusal
before the first financial read retains no context or business sources;
source-based replay checks still protect earlier financial evidence, including
inherited conversation sources.

The initial `--physical-assistant-check --browser-check` passed 51 business
scenarios and nine browser checks. Facility quantities use corrections available at the first
requested read; retained reads preserve that certificate after subsequent
corrections. Gas operations retain their selected day and native authorization.
After integrating D405 and D406, the complete frontend suite passes 1,891 tests
with none skipped. The verification runner's 29 contract tests pass and require
the managed prompt checks, workspace browser checks and migration checks. Independent adversarial review found no
remaining findings. These results establish local synthetic behavior; hosted
prompt configuration, publication and deployment remain separate operations.

The combined upgrade applies the published Park and Loan migration before the
installation-prompt and requested-context migrations. Their exact predecessor
checks, helper drift refusals and existing prompt preservation all pass. The
populated Park and Loan upgrade preserves its original native owners, cash and
retry outcomes. Selected Park and Loan questions are admitted with the full
Blotter report; an explicit request for the unqualified trade reader returns
`page_not_supported` without financial pins or context. Managed and frontend
regressions first reproduced the rejected question and incorrect capability label.

The owned `--hosted-check --browser-check` also passes. Proposal review verifies
that admission has no acquired context and that the later persisted tool context,
navigation target and reopened draft use the same exact certificate. Hosted
sign-out, expiry, cross-account refusal and browser checks remain enforced.

The repository verification group also passes. The complete 190-command runner
was not executed locally; aggregate CI and release checks remain required.


## Commercial Transport capacity integration (D415)

The integrated capacity artifact passes 742 managed core checks, the full API
schedule suite, 2,009 frontend and 148 Python checks. Native source, concurrency,
receipt and authority checks, populated upgrade, 64 Transport and 30 existing
Storage browser checks pass against the same API, SQL and served web artifacts.
The protected page helper recognizes the approved Options identifier; all
original metadata guards and definition/privilege drift refusals remain enforced.
Independent merge review verifies both parents' source and verification
registrations are retained. Disposable synthetic resources were cleaned. These
results do not qualify physical performance, financial Transport, complete
economic continuation or production activation.


## Retained Transport route integration (D413)

The integrated route artifact passes 743 managed core checks, the full API
schedule suite, 2,020 frontend and 154 Python checks. Nine actual managed route
source/witness checkpoints, native membership/source/authority/concurrency
controls and populated upgrade pass. The same API, SQL and served web artifacts
pass 55 route, 64 capacity and 30 existing Storage browser checks, including
native postconditions and cleanup. Only the exact protected page-helper
fingerprint changes for the approved Options identifier; capacity verifier
extraction, owner/ACL/settings checks and drift refusals remain enforced.
Independent merge review preserves both parents' source and verification
registrations. These disposable synthetic checks do not qualify financial
Transport, physical performance, complete economic continuation or production
activation.


## Direct Transport rights and independent cash (D416)

The local financial Transport artifact passes 744 managed core checks, the full
API schedule suite, 2,020 frontend and 155 Python checks, with zero API build
warnings/errors. Thirty-two actual managed source/command/value checkpoints,
shared lifecycle execution/recovery, populated upgrade and two retained PRICE
jobs pass on the same API and SQL. The 26 full-application browser checks also
verify native postconditions, exact empty-portfolio source/context binding,
accepted actor identity, both signed perspectives, capacity amendment, payment
recovery, independent cash and expiry. Desktop and phone views were inspected.

Adversarial controls cover strict selected-instant source expiry, deferred typed
source/receipt seals, original permission capture across scope/publication waits,
current-closed semantic expiry acknowledgement and retained report stability
after market correction. Disposable synthetic resources were cleaned. First-value
market publication, integration with the current upstream stack and release
remain separate gates; no production activation or complete economic horizon
continuation is implied.


## Integrated financial Transport qualification (D416)

The earlier combined master and service-stack artifact passes 745 managed core
checks, the full API schedule suite, 2,044 frontend and 157 Python checks, with
zero API build warnings/errors. Native qualification passes 32 actual managed
source/command/value checkpoints, shared current-closed expiry acknowledgement
and original-authority/source/deferred controls. Populated upgrade preserves
operating routes and supports mixed-reference financial booking/adoption. Two
retained PRICE jobs and 26 actual application browser checks pass on the same
API and SQL, including independent fee cash, exact source/actor identity, expiry
and native postconditions. The retained-window regression preserves business
date independently of the UTC valuation date.

Independent source and merge review preserves both parents' contracts and
verification registrations. The only native change beyond migration 0354
renumbering is the exact protected prompt-helper fingerprint for the approved
Options identifier; all original metadata/ACL guards and drift refusals remain.
Disposable synthetic resources were cleaned. First-value market publication,
aggregate CI and release remain separate gates; no production activation or
complete economic continuation is implied.


## Side alignment integration baseline (D417)

The integration baseline adds the subsequently merged operating-facility and
certificate workflows to financial Transport. The API build and default
checks pass before the Side refactor. Financial migration 0354 passes
its populated upgrade, preserving the predecessor route witnesses and raw facts,
then executing genuine mixed-reference booking and market-value adoption.

Its protected prompt-helper fingerprint changes only for the five approved
operating-facility/certificate page identifiers; independently removing those
identifiers reproduces the earlier helper fingerprint. All original helper
metadata, owner and privilege guards remain, and altered-helper refusals pass.
This baseline does not qualify the subsequent instrument/Side and UI changes:
complete managed, native, price-scenario and actual browser gates remain open.


## Ordinary point market-value publication (D418)

The local point publisher passes native source/receipt/concurrency/authority
controls, populated upgrade and 21 actual HTTP/browser checks on the same API,
SQL and served web artifacts. Preview rollback, first 20, correction 21, a genuine
new 20, equal-predecessor acknowledgement and lost-response recovery preserve
immutable observation lineage. Eight independent native identity vectors retain
exact UTF-8 and microsecond semantics. Missing optional session frames do not
prevent business publication or readonly status recovery.

Selected currency, quantity-unit and multiplier labels come from the target's
immutable quote foreign keys in entry, review and status. Explicit edited-input
application retains independent rate/reference/frozen uses and does not select
itself. Native postconditions prove no automatic financial adoption or trading
publication; a separate Transport adoption and ordinary valuation produce exact
7.50 from a signed 20 rights value and independent −12.50 fee.

Real scope/publication waits refuse expired or absent original authority despite
active successor grants and unchanged security epoch. A late same-transaction
source correction refuses final admission; unrelated points and authentic old
acknowledgements remain valid. Populated upgrade retains existing facts and
valued sources and preserves an independently owned helper with a similar name.
Desktop and phone quote disclosure were inspected. Checks used stock PostgreSQL
16.14, .NET 10.0.402, Node 24.22.0 and an owned Chrome preview on macOS arm64;
all disposable resources were cleaned. Integrated-stack, CI and release gates
remain separate, with no production activation or performance claim.

The combined operating-facility, certificate and financial Transport baseline
also passes the API default/schedule suites, 779 managed core, 2,108 frontend and
160 Python checks. The publisher native, populated-upgrade and 21 actual browser
checks pass on that same combined API, SQL and web artifact. Migration 0355 is
byte-identical to the locally qualified publisher migration; only its filename
and the two qualifier hooks change. All predecessor migrations remain unchanged.
Upgrade checks preserve unrelated helper metadata and still refuse protected
helper drift. This baseline contains no subsequent Side implementation, and it
does not qualify that later increment or authorize production activation.


## Instrument and Side alignment qualification (D417)

The managed inventory covers 27 concrete production Instrument types and the two
exact exchange exceptions. Independent checks cover Side-owned cash, holding,
rights, physical-exchange and retained-program PV; source/scenario/held-state
fences; exact whole-position and fixed-unit admission; and separate fee ownership.
Five retained-family compatibility oracles preserve v1 ordered member bytes,
program digests, captured calendar policies and native occurrence IDs. Altered or
reordered copies refuse before identity reuse.

Native gas source evidence retains exact definitions through
`timed_cash_source_definition_v1` and `gas_holding_source_definition_v1`. Complete
enclosing ownership, chronology, quantity and paid-state evidence remains
independent of the executable model graph. Backend entry, review and saved-term
membership contracts cover eleven product/profile families. Draft bindings remain
explicitly absent; saved slots, Side IDs, coefficients and ratios remain exact.
Unpriced structural Sides and zero-fee financial absence are distinct states.
Selected operating reads retain their certified source, original authority and
touched-program visibility through publication.

The combined managed checkpoint passes 785 core checks, 88 rates, API default
(98), schedules (491), gas-ticket (118) and focused Side/structured/reuse groups.
API and Worker builds have zero warnings/errors. The full frontend checkpoint
passes 2,164 checks with zero skipped; 163 repository Python checks pass. Later
SQL and qualification-helper changes through the scoped-presence checkpoint
preserve every compiled source and all 389 built output files byte for byte.
That checkpoint uses the completed combined outputs without a rebuild. The later
physical-obligation admission checkpoint below uses rebuilt API/checks/Worker
outputs and retains its separate artifact identity.

Earlier reviewed application checkpoints include standard/futures (327), dedicated
swaps (189), assistant workspace (43) and point publication (21). An earlier
universal-Side packet passed fixed-fee Park/Loan (90), balance-fee Park/Loan (214),
custody (126), financial Transport (43) and structured operating/REC (31) actual
browser assertions. These remain evidence for their reviewed packets; they are
not represented as one later all-product application run.

The combined local scoped-presence checkpoint, including migrations 0355 point
publication, 0356 operating assembly and 0357 scoped financial presence, passes:

- 64 native operating PASS records, including 39 operating assertions and exact
  predecessor definition/owner/ACL/settings refusal. Migration 0356 retains its
  original bytes after renumbering.
- Populated operating upgrade preservation of 30 complete native history,
  identity, program and relationship sets. Original battery/thermal/solar values
  remain exactly `610`/`760`/`500`; original commands reopen and replay unchanged.
- Five independent Sides at `550.7` and a sixth repeated-source Side at `532.6`,
  with exact retained member identities, independent payment rules, delivery
  rows, source-cut/hidden-book controls, both original ReadPhysical timers and
  native semantic-receipt expiry refusal.
- 16 operating and 19 saved-trading/REC actual browser assertions, including
  visible genuine Side cards and terms on phones.
- 116 physical actual browser assertions: gas/deal-ticket 46, crude 50 and LNG
  20. Strong phone visibility, hit-test and width checks pass after waiting for
  the responsive layout to settle before scrolling. The assertion and timeout
  contracts are unchanged.
- 81 financial Transport native PASS records. Scoped-presence qualification adds
  seven control groups, 27 predecessor drift mutations, a search-path control,
  exact excluded/pre-birth scopes, strict live/closed rights, source-known future
  births and a real original-authority wait with an active successor grant.
  Populated upgrade retains native table digests, original booking/mark outcomes,
  all helper metadata and the independent scope/projection helper bodies.
- In-term gas and paid-state/PRICE replay, operator physical and ordinary-use/zero
  native PV with real historical and 32-path Monte Carlo workers, and the real
  financial Transport PRICE worker. Exact source/scope/actor authority,
  cancellation/ownership, complete ordinary-report equality and unchanged native
  financial publications remain enforced.

The subsequent physical-obligation admission checkpoint accepts independently
reconstructed genuine Sides only under the same native Instrument ID, complete
economic/pricing/schedule definitions and ordered Side types, IDs and bindings.
It rejects changed native IDs, contractual cash timing and accepted finals. Both
quantity mapping and source composition share this exact comparison; owner,
account, quantity, source and historical admission remain unchanged. Focused
regressions and actual physical-known, active projection and independent-market
projection consumers pass, including their real retained economic workers.
Immutable-source assertions use the production retained-evidence serializer and
its complete cash/holding definition contracts. API/checks and Worker builds have
zero warnings/errors, with byte-identical embedded API assemblies across all
three output trees. The earlier 151 browser assertions retain their original
scoped-presence packet identity.

Both combined qualification checkpoints verify unchanged source and artifact
hashes before and after execution and remove their owned disposable resources.
Independent adversarial reviews cover numerical, temporal, authorization, occurrence-state, scenario-reuse
and migration/release boundaries. The complete CI matrix remains a merge gate.

Local fixtures use synthetic owned resources, stock PostgreSQL 16.14, .NET
10.0.402, Node 24.22.0, Python 3.13 and Chrome on macOS arm64. Production activation,
performance/capacity qualification and commercial Storage valuation remain
separate work.

## Trade actions, adjustment drafts and one-trade risk (D424)

Local results on 2026-11-17, on this Mac without a database: `npm test --prefix
web` passed 2238 tests, including the instrument bar's layout at four widths, the
position's action buttons and quantity row in a headless browser, the action list,
the operation reader, the draft readers and the risk scope. The API and its checks
build with no warnings, and `--position-adjustment` passed 40 database-free checks
of the adjustment's direction, sign, limit and quantity syntax.
`tests/test_check_all.py` passed 45 tests.

Not run here: `--close-out-drafts-check --browser-check`, which holds the
adjustment draft's database checks (long and short futures in each direction, the
close-out's price and parties repeated, swaps kept and reversed, each refusal) and
the live browser checks of the buttons and the New trade bar. They run in CI.


## JSON read compression (D428)

Local Release checks on 2026-11-18 pass exact response/identity/context parity,
missing/null/empty values, success/failure/cancellation framing and the explicit
read compression allowlist. An actual Kestrel client verifies gzip and identity
negotiation, reads a flushed ready event while the handler is held before
completion, and confirms bootstrap tokens stay uncompressed. The existing
`--async-reads` CI selector includes these checks and passes its admission,
concurrency, normalized retries, cancellation, visibility, profile/sign-in and
publication-gate regressions. API/checks and Worker Release builds pass.

A bounded comparison of matching projection prototypes measured complete
click-to-render interactions through TLS/nginx, including decoding, adaptation,
validation, managed amount formatting, charts/tables and two animation frames.
Two complete runs restart the owned API/database/proxy and rotate mode order;
there are six primary observations per mode/profile. Partial runs are excluded.
The table reports pooled p50 / descriptive p95 and median complete-interaction
TLS payload bytes, including associated HTTP reads and framing.

| Added RTT | JSON raw result + gzip | Direct Protobuf, uncompressed | JSON / binary downstream |
|---|---:|---:|---:|
| 0 ms | 1,471 / 1,836 ms | 1,716 / 2,108 ms | 7.61 / 10.87 MiB |
| 330 ms | 1,986 / 2,107 ms | 2,231 / 2,404 ms | 7.62 / 10.92 MiB |

The primary curve has 72 displayed contracts and 608 observations, with source
metadata and first-use workspace hydration. The isolated data copy contains 90
curve rows, 11,930 contracts and 529,794 observations. Secondary workloads include
4/72-point curve switches, four-sample history, and scenario Risk over 13 trades,
16 positions, six instruments, three market contracts and three base/up/down
scenarios; the equivalence probe records nine unique valuations and eleven unit
pricer calls. Database-command counts and business requests for curve evaluation,
history and Risk calculation match across modes within each run/profile. Optional background
curve-board shape reads vary with scheduling; aggregate database work is not
identical. Newly captured Risk context handles/timestamps differ, but
resolve to the same business date and publication prefixes. Full projection
checks preserve financial text, metadata, observations and context.

Server: Linux x64, two virtual CPUs, 8.32 GB host RAM, .NET/ASP.NET Core 10.0.13,
PostgreSQL 18.6, 3 GiB API limit. Browser: Chrome 156.0.8078.79, Node 24.22.0,
3 GiB container limit. Builds use SDK 10.0.402. Client/server share the host;
it is not an exclusive-host or concurrent-user capacity test. The relay adds
165 ms per direction, calibrates health requests, and imposes no bandwidth cap
or packet loss. Every recorded JSON event stream uses HTTP gzip; binary streams
use neither HTTP nor message compression. Nginx buffering remains disabled.

These candidates share a separate managed projection cleanup; this table isolates
encoding choices and is not a before/after latency claim for this patch. Neither
complete run shows the required primary improvement against its strongest JSON
candidate. A three-run adoption gate remains unqualified. Small-sample p95s are
order statistics. Memory snapshots are not per-mode peaks; the counting writer's
maximum scratch is 16 KiB, and browser SDK decode is included in readiness but
not isolated as a comparable standalone CPU stage. The large history projection
has API parity; its unavailable-input UI states are outside the covered history
render case. These limits support retaining JSON, not a universal claim about
binary encoding or a production service-level guarantee.

## Curve hydration and read memory (D429)

Release checks on 2026-11-18 reproduce the old behavior before each memory fix:
serializing an oversized lazy result walks all 8,192 rows before rejecting it;
an oversized JSON object allocates 268,397,056 bytes before the stream limit
runs; materializing a 71,095,510-byte synthetic pin batch exhausts a 256 MiB
managed heap. The corrected writer stops the lazy result at row 2,049. Oversized
JSON object/element checks allocate 41,975,840 / 67,100,520 bytes respectively;
a definitely oversized string refuses before its encoded copy is allocated.
These are serialization allocation checks, excluding construction of their inputs.

The complete async-read checks pass exact logical UTF-8 limits at 16 MiB minus
one byte, exactly 16 MiB and one byte over, including escaped non-ASCII text.
They cover exact financial text, null/empty fields, the existing 64-level result
plus its envelope, serialization cancellation, correlated failures, partial-write cancellation,
one total event deadline, gzip/identity negotiation and ready-event flushing.
Writing a precomputed 4 MiB result adds 896 allocated bytes in its measured check;
the counting sink is preallocated and excluded. This is a publication-stage
allocation result, not the memory cost of evaluating that result.

An opt-in backend HTTP case uses actual managed native-curve metadata, 20,000
contracts, 20,000 observations, one selected contract and three sequential base
price reads. It serves a complete 17,026,511-byte catalog separately; its gzip
body is 3,928,369 bytes. Each price result preserves its complete precomputed
business projection and carries no catalog. This Release process includes both
the Kestrel server and its HTTP validation client. Its managed heap limit is
768 MiB and measured peak resident size is 759,054,336 bytes (724 MiB).

A separate input-decoding case reads 525,123 original synthetic observations
from a 252,887,126-byte UTF-8 batch and builds the native observation indexes.
It preserves every row, exact decimal text and denied identity. Under the same
768 MiB managed heap limit, measured peak resident size is 798,736,384 bytes
(762 MiB). This case includes batch decoding/indexing, not an API server,
database process or simultaneous curve evaluation. The complete batch remains
resident; larger or concurrent batches are outside this measured envelope.

Both cases run on macOS 26.6.2 arm64, 14 logical CPUs, 48 GiB host RAM,
.NET/ASP.NET Core 10.0.13 and SDK 10.0.402, using Release builds. The RSS source
is the operating system's `time -l`, rather than the runtime's unavailable
peak-working-set counter on this platform. The managed heap constraint is not
a Linux container/cgroup qualification. HTTP bytes above are compressed body
bytes over loopback, excluding headers, TLS and proxy framing. These cases do
not measure production latency, bandwidth, browser rendering or concurrent-user
capacity.

The owned actual-browser regression passes 13 checks, including a real power
curve, first hydration with over 16 MiB of injected unselected catalog metadata,
rapid curve switches, a catalog response delivered after supersession, pending
Risk reads and exact startup readiness. The metadata injection occurs after the
small fixture's HTTP response; it qualifies browser behavior rather than server
memory. The separate backend-scale case above covers server serialization.

The page projection comparison in the owned asynchronous HTTP fixture covers
four frames, 30 catalog contracts and six points of a nested power curve at one
exact close. The compact price result retains the same points, trace, selected
source and correction eligibility. It carries 31 observation rows versus 40
in the full-frame response and 51,718 versus 57,747 bytes as compact JSON
re-encoded by the test client. Frame summaries reduce the separate catalog
from 201,998 to 185,542 bytes by the same measure. Ordinary HTTP and SSE return
identical compact projections. Further owned checks cover exact rate tenors,
empty eligible delivery, a non-primary source revision and unknown projection
refusal. The payload counts exclude the response envelope and compression, and
both variants used the full market calculation input load. The owned calendar
run passed 67 browser checks. It confirms that the close strip starts after its
selected prices draw and that editing the context cancels a pending strip
response in the tested flow.

Native PostgreSQL calendar checks preserve the unchanged answer hashes, source
matrices, reached/unvisited denial precedence, original authority, current
publication permission, close replacement and profile authorization. The async
suite separately verifies profile/sign-in fences. Statement counts remain 101
for 24, 48 and 96 candidates. The macOS run adapts `ps` sampling only for the
check harness's Linux `/proc` counters; business assertions are unchanged. CPU
samples have 0.01-second resolution and calendar peak RSS is unavailable in that
sampler; these samples establish no performance claim. API/checks and Worker
Release builds pass.

The optional managed HTTP case is reproducible with:

```sh
dotnet build tests/OpenEtrm.Api.Checks -c Release
DOTNET_GCHeapHardLimit=30000000 dotnet \
  tests/OpenEtrm.Api.Checks/bin/Release/net10.0/OpenEtrm.Api.Checks.dll \
  --large-catalog-http
```

`DOTNET_GCHeapHardLimit` is hexadecimal here: `30000000` selects 768 MiB.
The file-input check accepts `--observation-pin-payload FILE EXPECTED_ROWS` for
an explicitly generated complete synthetic batch; generated payloads and local
operational evidence stay outside the repository. No schema migration or new
dependency is required. The first curve interaction adds an ordinary catalog
HTTP/database read; its added round-trip cost and production interaction latency
remain to be measured after an approved release.

## Curve definition dependency reads (D430)

The owned synthetic native preview compared full and dependency-scoped reads
with the same compact JSON projection, gzip configuration and browser page.
The only request difference was `definitionScope`. The target nested base
plus basis curve had 76 output points, 152 participating contracts, 152
consumed observations per close and 228 trace nodes. Four synthetic closes
were present. Scaling unrelated contracts and series raised the total native
definition from 3,031 to 12,031 contracts. Every measured HTTP result matched
its full-definition counterpart exactly.

| Contracts in definition | HTTP p50, full → scoped | HTTP sample max | SQL statements per read, median | Warm chart p50, full → scoped | Warm chart sample max |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 3,031 | 102.7 → 70.3 ms | 117.9 → 82.6 ms | 98 → 47 | 131.5 → 101.2 ms | 143.9 → 111.8 ms |
| 12,031 | 191.7 → 170.8 ms | 222.9 → 188.3 ms | 98 → 47 | 233.2 → 103.4 ms | 253.8 → 107.8 ms |

Runs used macOS 26.6.2 arm64, 14 logical CPUs, 48 GiB RAM, PostgreSQL
18.7, .NET/ASP.NET Core 10.0.13, SDK 10.0.402 Release builds and headless
Chrome 157.0.0.0. Each scale used nine alternating paired HTTP reads and
eight alternating paired browser clicks on loopback. Chrome consumed the real
SSE stream and changed only the outgoing definition selector. Browser timing starts
with the click from an already hydrated six-point curve and ends after
the selected 76-point table is ready and two animation frames have elapsed.
The HTTP statement count comes from native preview runtime logs. Median
summed statement duration was 66.0 → 50.9 ms and 109.2 → 153.5 ms at the
two scales. A separate 12,031-contract HTTP run also had exact parity and
98 → 47 statements, with round-trip medians of 186.5 → 71.2 ms and summed
statement medians of 107.2 → 55.4 ms. The difference between the large-scale
HTTP runs remains unexplained. These sample maxima describe run variation
within each run. Process peak memory, cold hydration, calibrated proxy latency,
concurrent users and a 96-close dataset remain open. The dependency-scoped
reader applies to price evaluation.

The native check passed 50 HTTP/SSE assertions, including identical compact
power and rate responses under working and certified contexts. A scoped
curve blocked on the publication gate refused disclosure after its authority
expired. Separate checks passed an exact non-primary recorded source and an
expired curve with no eligible output. The 13 owned browser checks passed,
including selection supersession and late catalog delivery. All 2,244
frontend tests passed.

## Operating facility and right access (D444)

The checks were run first without migration 0389. The facility storage check and
the native operating-trade check no longer grant themselves `ReadPhysical` or
`ManagePhysicalSetup`; both stopped at the Market data editor's first facility
save with `42501: power_facility_denied`. The owned
`--structured-power-check` run, at that point still under the preview's own
grants to Trader and Market data editor, passed the 95 lines of its existing
flow and stopped when a Risk analyst was refused the facility catalog (403).

With the migration, and with the preview's own grants of the two capabilities
removed:

- The facility storage check passes 50 cases. Applying the grant again changes
  nothing, and an installation without one of the named roles is told which and
  receives no part of it. Setup authority is installed on
  Market data editor alone, and the roles that read operating facilities are
  exactly the human roles that read trades plus Market data editor. Trader and
  Risk analyst read exact versions and the directory and cannot save; Viewer,
  Market data approver and Access administrator read nothing.
- The native operating-trade check passes. The editor saves each facility and
  the trader reaches admission. Risk analyst, Market data editor, Viewer,
  Market data approver and Access administrator are refused with
  `operating_booking_denied`, and a booker whose role loses `ReadPhysical` is
  refused with `operating_facility_unavailable`. A hand grant made before the
  migration is kept.
- `--structured-power-check` passes 118 lines. Each of thirteen roles is run as
  one person against the facility catalog, list, exact read, save, save
  recovery and supplied-plan preview, and the operating-right catalog,
  directory, exact read, valuation, review, booking, booking recovery, hedge
  report and position read. Each role is allowed exactly what D444 lists and
  refused the rest with 403. The confirmations desk lists the operating right,
  and a trade reader whose role loses `ReadPhysical` is refused the whole
  position read of the book that holds it. The preview gives Trader and Market
  data editor `RunScenario` for its own pages, so the two scenario routes are
  asserted through the eleven roles it leaves as installed.

These runs used a disposable Linux container with stock PostgreSQL 18.7, Python
3.12 and a Debug API build under SDK 10.0.402. With the migration the preview's
default owned check, where the trade-reading roles now hold `ReadPhysical` as
well, passes 1,584 HTTP and native lines there, and `--scheduled-swap-check`,
which states the Trade supervisor's permissions exactly, passes 149 lines. The
182 repository Python unit checks pass. In a running preview a Trader's New
trade page lists Operating right, and choosing it opens Operating facilities
with the catalog loaded and setup disabled. The browser suites and the other
check groups were not run for this change outside CI.

## Schema fingerprint without dump versions (D463)

The behavior checks were written first and failed against the earlier function.
Two synthetic dumps that differ only in their `\restrict` key and header version
lines had different fingerprints; a `\restrict` line inside a function body
could change without changing the fingerprint; and an empty dump, text around
the dump, a missing header line or a different closing key were all accepted.
With the change `python3 -m unittest tests.test_release_deployment` passes 41
checks. They also require that a changed definition, and a version line or a
pair of them inside a definition, still change the fingerprint.

The function was run on two real schema dumps of one disposable database
holding `db/bootstrap.sql` and 411 migrations, taken under the pinned
`postgres:18.6-bookworm` and `postgres:18.7-bookworm` images. The earlier
function gave two fingerprints. This one gives one, changes when one character
of a definition changes, and refuses a truncated dump. The recording command in
the [hosted guide](hosted-pilot.md#automated-release-distribution) was run
against that database in a disposable 18.7 container and printed the same
fingerprint; with no database to dump it failed instead of printing one.

`tools/check_release_docker.py` was then run with this deployer against the
attested release of master head `32921f1ab48e4a622179e62ebef745158d836d1f`,
with the release of `da0b3c2250c064b0eb7ef8a393b6ece58e75f8b7` as the previous
image and both previous-image options `named`. It recorded the fingerprint from
the release's stock 18.7 database after the release's migrations, and the
deployment's own baseline check accepted it. The check passed: attestation,
candidate preflight, promotion, unchanged database and data, the repeat run and
the paired rollback. The only local adaptation was a wrapper that passed the
sandbox's network proxy settings to `gh`.

Not run: a database on another major release, and any installation's own
re-recording.

## Worker RSS sampling at start

The risk group failed once on a pull request that did not touch it, in
[run 46086789647](https://github.com/zerodrift/leanetrm/actions/runs/46086789647/job/140798306846),
with `Invalid worker RSS sample ... reading '0 R'`. `measure_worker` in
`tools/run_preview.py` takes its first `ps` sample as soon as it has started the
worker and refused zero RSS from any process that was not exiting. A started
process has no resident pages until it first runs. Reading `/proc/<pid>/stat`
straight after starting a child gave state `R` with zero RSS in 52 of 300 starts
on four processors and in 291 of 300 pinned to one, and state `D` with zero RSS
once.

The sampler now accepts zero RSS in state `R` or `D` until the worker's first
positive sample. After that sample such a reading is still refused as a lost
one, zero RSS in any other live state is refused at any time, and a worker that
exits without a positive sample still has no measurement. The behavior checks
were written first and failed; `python3 -m unittest
tests.test_check_gas_swing_numerics` passes 13 checks. On one processor shared
with six busy loops, 60 real short workers gave the earlier sampler the same
`'0 R'` refusal once and the changed sampler 60 measurements.

Only Linux was observed. The rule adds no state for another system.
