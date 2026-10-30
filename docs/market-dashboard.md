# Market dashboard

Status: phase 1 implemented locally, unpublished, awaiting the adversarial
review AGENTS.md requires for its temporal, asynchronous and authorization
changes. The decisions are D263 (layout), D264 (risk run definitions) and D265
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
  Switching saves the arrangement and keeps every widget. In Morning a widget
  can be moved to another section; any widget can be removed.
- **Add widget.** Latest levels takes 1 to 12 curves from the context's
  navigation (interest-rate curves are not offered, since the curve-shapes read
  does not state them). Price chart takes one curve and one of the contracts
  the close's curve shape states. Positions by month takes an existing risk run
  definition or saves a new one.
- **Latest levels.** One curve-shapes read for all configured curves: a tile per
  curve in configured order with the backend's front contract, price, unit and
  sparkline. A curve the read does not return, or does not price, is a refusal
  tile with the reason; the other tiles still show.
- **Price chart.** The contract's `/history` under the explicitly named
  `exact_snapshot_pins_v1` policy, drawn with the shared time chart. A history
  conflict or a contract absent at the close is a refusal.
- **Positions by month (D264, D265).** The delta ladder (D230) of the
  definition's latest completed run at the context. A tie of completed runs at
  one valuation time is shown as a tie, never a chosen run. A newer completed
  run beyond the selected close, and a newer run that did not complete, are
  stated. **Run now** queues the definition at the retained context; a retry
  after a lost reply resends the same request id. A new definition is built from
  the readable books, a preset (Shock every held contract or Parallel shift) and
  a shock size for each exact price unit the risk catalog states for what the
  preset shocks; no size is prefilled.
- **Freshness.** Each widget's footer states source, observation time and state
  from backend fields: Latest, Newer available (also for an earlier selected
  close) or Historical in a past context. Latest levels adds "Intraday not
  configured".
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

Run on 2026-10-30 against a newly owned local preview with stock PostgreSQL
16, .NET 10.0.402, Node 24.22.0 and headless Chrome on macOS arm64.

- HTTP checks in the standard `--check`: 8 dashboard-layout checks
  (`tools/check_dashboard_layout.py`) and 9 risk run definition checks.
- HTTP checks in `--check --risk-jobs-check`: the Run now checks (parts of at
  most sixteen shocks, retries, missing sizes, lost book access, catalog price
  units) and the latest-run checks (cutoff, newer, queued, cancelled part, tie,
  access).
- Web unit tests for the layout edits, freshness footer, Latest levels and
  Positions models.
- `tests/browser/dashboard.mjs`, 18 checks in the standard browser suites, in
  the risk suite's browser after its checks, as the Risk Analyst. It adds one widget of each kind, switches
  Board → Morning → Board, reloads and finds the saved layout, saves a new
  definition, presses Run now and reloads until the owned risk worker's run shows
  as a ladder, applies a past effective date and checks the read-only page, and
  checks that a configured curve the close does not return is a refusal tile
  while the other widgets render, then that the page fits 390 pixels. The
  standard fixture defines all its curves in one revision and returns every
  listed curve at each close, so the check removes one curve from the browser's
  copy of the curve-shapes reply for that close; the refusal itself is the page's.

`python3 tools/run_preview.py --check --browser-check` passed with 958 owned
browser checks, 131 of them in the risk and dashboard browser. Before this
change it stopped with a 403 from `/api/bootstrap`: the preview keeps at most 64
sessions for 30 minutes, the whole run takes under ten minutes, and the new
layout and definition HTTP checks opened seven sessions of their own. Those
checks now share two sessions, switching them between users, and the Dashboard
runs in the risk suite's browser. Counted in the API, the run then peaks at 63
of the 64 sessions, leaving one. At the same point of a run, this branch had
opened two more sessions than the commit before the dashboard work.

### Page benchmark

`tools/benchmark_pages.py` now seeds the Risk Analyst's dashboard and measures
opening it, as it measures the other pages. Workload: the benchmark's standard
synthetic fixture (12,420 observations, 92 contracts, 12 curves, 24 trades of
which 16 swaps, 10,189 reference rows) and a saved Board layout of three
widgets, one of each kind: Latest levels of the 10 non-rate curves the
navigation lists, a Price chart of `local_power`'s front contract, and Positions
by month of one definition (2 books, Shock every held contract, 2 price units,
65 held contract targets). That definition has 1 completed run of 5 parts,
made by Run now at the 2020-12-18 close and run by an owned risk worker before
measurement. The Dashboard is ready when its level tiles, chart canvas and
ladder rows are drawn with no widget refusal.

Five samples, 40 ms browser latency, 10 MiB/s down and 5 MiB/s up, on macOS
26.6.2 arm64, .NET 10.0.402, stock PostgreSQL 16.14 and Chrome 156.0.8078.74,
from this branch's working tree on 2026-10-30:

| Page | p50 | p95 | Maximum |
|---|---|---|---|
| Dashboard | 1,065 ms | 1,090 ms | 1,090 ms |

**Finding: the Dashboard's p95 exceeds the one-second page budget.** Every
other page stayed under it in the same run (the slowest, the Blotter, at a p95
of 545 ms; initial readiness 948 ms; curve pricing p95 172 ms). API resident
memory was 815,408 KiB at the reporting instant, not a peak. In one sample the
slowest requests were `trading/directory` (470 ms to headers), which Positions
reads on every draw to retain its context and list books before reading the
latest run, and `history` (454 ms), which the chart reads after its own
curve-shapes read. Positions' requests run in sequence: definitions, directory,
latest run, display amounts. The same open made four curve-shapes reads: Latest
levels, the chart and the Add widget control each read their own. No tuning was
done; the benchmark does not establish which change would bring the page under
budget. Single client and synthetic data only; no concurrency or production
claim.

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
- The Positions ladder does not refresh when a run it queued completes; the
  person presses the widget's Reload.
- Board placement and size are set when a widget is added; there is no drag or
  resize on the Board yet.
- Each layout save re-reads every widget, and the Positions widget reads the
  trading directory, the definitions and the latest run on each draw.
