# Market dashboard

Status: phase 1 implemented locally and unpublished. The adversarial review
AGENTS.md requires was done once for the whole phase; its findings are fixed
here, and the changed temporal paths (the context the Positions widget reads in,
and tied runs) await a focused re-review. The decisions are D263 (layout), D264 (risk run definitions) and D265
(run receipts and knowledge) in [decisions.md](decisions.md). The design is
[the dashboard specification](superpowers/specs/2026-10-30-market-dashboard-design.md);
phases 2 to 4 and the other follow-ups are under [Remaining work](#remaining-work)
and in [plan.md](plan.md).

## What phase 1 provides

A **Dashboard** entry heads a new Overview navigation group for accounts with
`ReadMarketData`. The page reads at the session's market date and context, like
the other market pages, and adds a Board / Morning switch.

- **Layout (D263).** One layout per account, stored server-side by
  `GET/POST /api/dashboard/layout` (migration 0130), so it follows the account
  between browsers. Every save carries a request id and the expected version: a
  retry returns its first outcome, a reused id with another layout is
  `dashboard_request_conflict`, and a stale version is
  `dashboard_layout_conflict`, which the page shows with a Reload button. A
  layout names curves, contracts and definitions only; it holds no price, date
  or figure.
- **Arrangements.** Board is a twelve-column grid placed from each widget's
  stored box; Morning shows the four fixed sections (Weather & events,
  Inventories, Prices & positions, Might have missed), empty ones included.
  Switching saves the arrangement and keeps every widget. On the Board each
  widget's Arrange control moves it a column or row (left, up, down, right) and
  sizes it a step (narrower, wider, shorter, taller), within the twelve columns
  and the stored bounds; these are keyboard-operable buttons, keep the focus
  across the save, and there is no drag. In Morning a widget can be moved to
  another section; any widget can be removed.
- **Reads.** Opening the page makes one curve-shapes read for the curves of
  every Latest levels and Price chart widget. A layout edit (arranging, moving,
  adding or removing) keeps every unchanged widget as drawn and reads only a
  new one; a new close, context, live correction or Reload reads every widget
  again. The Add widget form reads a chart's contracts, or Positions'
  definitions and books, only while it is open.
- **Add widget.** Latest levels takes 1 to 12 curves from the context's
  navigation (interest-rate curves are not offered, since the curve-shapes read
  does not state them). Price chart takes one curve and one of the contracts
  the close's curve shape states. Positions by month takes an existing risk run
  definition or saves a new one.
- **Latest levels.** A tile per configured
  curve in configured order with the backend's front contract, price, unit and
  sparkline. A curve the read does not return, or does not price, is a refusal
  tile with the reason; the other tiles still show.
- **Price chart.** The contract's `/history` under the explicitly named
  `exact_snapshot_pins_v1` policy, drawn with the shared time chart. A history
  conflict or a contract absent at the close is a refusal.
- **Positions by month (D264, D265).** The delta ladder (D230) of the
  definition's latest completed run, read in the page's own context: current
  knowledge in working data, or the selected historical context. The read
  names the definition, so the widget makes one request per draw. A newer
  completed run beyond the selected close, and a newer run that did not
  complete, are stated, also when no run is shown. A run valued at an earlier
  close than the selected one says "Valued at an earlier close". Completed runs
  tied at one valuation time are listed, each with its version and the time it
  became known; the person picks one with Show this run, which is then labelled
  "Chosen run" until the next read, and nothing about the choice is stored.
  **Run now** captures the page's context, queues the definition there, follows
  its parts and reads the widget again once every part has finished; a retry
  after a lost reply resends the same request id. A new definition is built from
  the readable books, a preset (Shock every held contract or Parallel shift) and
  a shock size for each exact price unit the risk catalog states for what the
  preset shocks; no size is prefilled.
- **Freshness.** Each widget's footer states source, observation time and state
  from backend fields: Latest, Newer available (also for an earlier selected
  close), Valued at an earlier close or Chosen run (Positions) or Historical in
  a past context. Latest levels adds "Intraday not configured". When the page
  later finds a newer close (on focus or opening a page), the Latest levels and
  Price chart footers say Newer available without reading again.
- **Historical context.** In a past or unapplied context the page is read only:
  every edit control and Run now is unavailable. A context applied with no close
  chosen refuses each widget with "Choose a market date above."; choosing a close
  reads every widget at it. Changing the close or applying a context reads every
  widget again. In working data the page's live-correction refresh (D188)
  redraws the widgets; no browser check exercises that path.
- **Isolation.** Each widget renders inside its own error boundary; one widget's
  refusal never hides the others.

The browser holds no business rule: fronts, prices, freshness states, the
choice of run and the ladder's figures come from the backend. Bars in the ladder
are a visual scale of the backend's amounts.

## Verification

Run on 2026-10-30 against newly owned local previews with stock PostgreSQL
16.14, .NET 10.0.402, Node 24.22.0 and headless Chrome 156 on macOS 26.6.2
arm64.

- HTTP checks in the standard `--check`: 8 dashboard-layout checks
  (`tools/check_dashboard_layout.py`) and 9 risk run definition checks.
- HTTP checks in `--check --risk-jobs-check`: the Run now checks (parts of at
  most sixteen shocks, retries, missing sizes, lost book access, catalog price
  units) and the latest-run checks: the cutoff, a run valued at an earlier close
  (`earlier_close`), newer, queued, a cancelled part, a definition whose only run
  failed (`none` naming it), a tie listing each run's version and its known time
  equal to its latest receipt, a tied run chosen explicitly (`chosen`) and any
  other run id refused (`risk_run_choice`), and lost access.
- Managed check: a definition's shocks split in order into parts of at most a
  holdings request's shocks, refused as `risk_definition_targets` over the
  part limit.
- Web unit tests for the layout edits and Board steps, the curves of one shared
  read, the freshness footer, Latest levels (including a curve the navigation
  does not name) and the Positions model (page context, ties, chosen and
  earlier-close runs, a finished run).
- `tests/browser/dashboard.mjs`, 32 checks in the standard browser suites, in
  the risk suite's browser after its checks, as the Risk Analyst. It adds one
  widget of each kind; switches Board → Morning → Board without reading any
  curve again; reloads, finds the saved layout and sees one curve-shapes read for
  the page; presses Run now and waits, with no Reload, until the widget shows
  the owned risk worker's run as the latest; moves a widget down and narrows it
  with the Enter key on its Arrange buttons, keeps the focus there, redraws
  nothing and finds the box after a reload; saves through the API a layout
  naming `no_such_curve`, which the catalog lacks, and sees the backend's reason
  in that tile and the chart's refusal while every other widget renders; opens
  the page on the close before the newest (the calendar reply ends a date early,
  as in the market-dates check) and sees both close footers turn to Newer
  available when a focus finds the newest close, with no read; at that earlier
  close in working data sees the newer completed run stated, runs there and sees
  "Newer available", runs again and chooses one of the two tied runs, shown as
  "Chosen run"; selects the newest close again; applies a past effective date and
  checks the read-only page; and checks the page fits 390 pixels.

Driving the page found two defects, both fixed with a check: a configured curve
the navigation does not name left its Latest levels widget reading forever, and
re-stating the close footers threw while no close was chosen, which left the
page busy after a past context was applied.

The full verification (`tools/check_all.py`) passed apart from one run of the
live-calculation browser, which stopped at the Blotter side panel's "keyboard
focus on a button of the panel's head" and passed when run again; the remaining
steps then passed. The standard browser run stays within the preview's 64
sessions: the dashboard checks open none of their own.

### Page benchmark

`tools/benchmark_pages.py` seeds the Risk Analyst's dashboard and measures
opening it, as it measures the other pages. Workload: the benchmark's standard
synthetic fixture (12,420 observations, 92 contracts, 12 curves, 24 trades of
which 16 swaps, 10,189 reference rows) and a saved Board layout of three
widgets, one of each kind: Latest levels of the 10 non-rate curves the
navigation lists, a Price chart of `local_power`'s front contract, and Positions
by month of one definition (2 books, Shock every held contract, 2 price units,
65 held contract targets). That definition has 1 completed run of 5 parts,
made by Run now at the 2020-12-18 close and run by an owned risk worker before
measurement; no scenario is evaluated on open. The Dashboard is ready when its
level tiles, chart canvas and ladder rows are drawn with no widget refusal.

Five samples, 40 ms browser latency, 10 MiB/s down and 5 MiB/s up, on macOS
26.6.2 arm64, .NET 10.0.402, stock PostgreSQL 16.14 and Chrome 156.0.8078.74,
from this branch's working tree on 2026-10-30:

| Dashboard | p50 | p95 | Maximum |
|---|---|---|---|
| Before (each widget read on its own) | 1,065 ms | 1,090 ms | 1,090 ms |
| After | 754 ms | 847 ms | 847 ms |

Opening the page now makes, in sequence on the session: the layout, one
curve-shapes read for the ten configured curves (the chart's among them), the latest run (which names
the definition, about 100 to 130 ms to headers), the market calendar and the
chart's `history` (the slowest, 516 to 639 ms to headers). Before, it also read
the definitions, the trading directory (470 ms, to retain a context) and two
more curve-shapes reads, one of them the closed Add widget form's. Every other
page stayed under the budget in the same run (the Blotter at a p95 of 553 ms;
initial readiness 927 ms; curve pricing p95 168 ms). API resident memory was
796,480 KiB at the reporting instant, not a peak. Single client and synthetic
data only; no concurrency or production claim.

## Remaining work

- **Phase 2:** provider connections, observation and event storage, the Data
  providers setup screen and synthetic providers; the Weather vs normal, Weather
  events, Inventory, Outages and News widgets, with cadence-based lateness.
- **Phase 3:** intraday prices on Latest levels, from the live-price source
  shared with the Blotter work.
- **Phase 4:** agent alerts and pinned chart cards on the dashboard, then
  watches once scheduled agent tasks exist.
- Lateness of an expected close against its business calendar (planned with
  provider cadence in phase 2); closes show only Latest, Newer available or
  Historical.
- Running a saved definition from the Blotter, which keeps its own risk report.
- Dragging widgets on the Board; widgets may overlap after a move.
- Publication-lock contention from definition runs is unmeasured
  ([verification.md](verification.md)).
