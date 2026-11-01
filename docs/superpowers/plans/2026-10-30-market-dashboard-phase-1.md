# Market Dashboard Phase 1 Implementation Plan

Status: historical implementation plan. Phase 1 has merged; the checklist below
records the planned sequence, not current completion evidence. The actual schema
uses 0139 for layouts, 0140 for risk definitions and 0141 for certified run knowledge.
See [the current dashboard record](../../market-dashboard.md) for implemented
behavior, verification and remaining work. Published migrations are immutable;
subsequent schema changes require new migrations.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Dashboard page with a per-user layout (Board or Morning), freshness footers, Latest levels and Price chart widgets from existing reads, and saved risk run definitions feeding a Positions widget.

**Architecture:** Two new backend stores: an account-owned dashboard layout (following `presentation.table_views`) and immutable, user-owned risk run definitions linked to the risk runs queued from them. A "latest completed run" read resolves a definition's run under the selected effective time and knowledge cutoff. The browser presents layout, widgets and freshness from backend fields and existing reads only.

**Tech Stack:** PostgreSQL migrations (`db/migrations`), C# minimal API (`src/OpenEtrm.Api`), Python HTTP checks against a disposable preview (`tools/check_*.py` via `tools/run_preview.py`), TypeScript (`web/src`, `node --test web/tests`), browser checks (`tests/browser`).

**Spec:** `docs/superpowers/specs/2026-10-30-market-dashboard-design.md` (phase 1 of its delivery order).

## Global Constraints

- AGENTS.md applies. TypeScript is presentation only; no business rule, default or derived figure in the browser.
- No silent defaults: missing, ambiguous or conflicting input is a structured refusal (`code`, `message`), never a guessed value.
- Every write is idempotent (D238): a retried command carries its request id and gets its first outcome; reusing a request id for a different command is a conflict.
- Every business read carries the session's effective time and knowledge cutoff; access is checked under current permissions.
- Synthetic data only. No Blotter file changes: `futures-view.ts`, `data-grid.ts`, `table-workspace.ts`, `desk-*.ts`.
- Page budget: under one second from navigation to usable on the standard fixture (`tools/benchmark_pages.py`).
- The original plan reserved 0130/0131; implementation used 0139/0140 and a separate 0141 for run knowledge.
- Commits signed off (`git commit -s`), ending with the Co-Authored-By line. Nothing pushed without the user's approval.
- Adversarial review before publication (temporal, async execution, authorization).

## Review Focus

1. A saved widget names a curve, contract or definition that is not visible in the selected context, or that the user can no longer read: that widget shows a refusal and the rest of the page renders (Tasks 1, 6).
2. Two browser sessions edit the layout at once: the stale save is refused with a conflict, nothing is lost, and Reload shows the winner (Task 1).
3. A definition names a book the user has lost access to: Run now and the latest-run read both refuse (Tasks 3, 4).
4. A run with a failed or cancelled part is not a completed run: the widget keeps the previous completed run and says a newer run did not complete (Task 4).
5. Switching to a past context while a Run now is still queued: the widget shows the run known at that cutoff, with no live update, and never the in-flight one (Task 4).

---

### Task 1: Dashboard layout storage

**Files:**
- Create: `db/migrations/0139_dashboard_layouts.sql`
- Create: `src/OpenEtrm.Api/DashboardLayouts.cs`
- Modify: `src/OpenEtrm.Api/Program.cs` (map endpoints next to `/table-views/blotter`, line ~973)
- Create: `tools/check_dashboard_layout.py`
- Modify: `tools/run_preview.py` (call it after `check_table_views`, line ~927)
- Modify: `docs/decisions.md` (add D261: Dashboard page and account-owned layout)

**Interfaces:**
- Produces: `GET /api/dashboard/layout` → `{version: uuid|null, layout: Layout|null}`; `POST /api/dashboard/layout` with `SaveDashboardLayout(Guid RequestId, JsonElement Layout, Guid? ExpectedVersion)` → `{version, layout}`.
- `Layout` (version 1), validated in `DashboardLayouts.Validate(JsonElement)`:
  ```json
  {"version":1,"arrangement":"board"|"morning",
   "widgets":[{"id":"<uuid>","kind":"latest_levels"|"price_chart"|"positions",
     "config":{...},"board":{"x":0-11,"y":0-199,"w":1-12,"h":1-12},
     "morning":{"section":"weather_events"|"inventories"|"prices_positions"|"missed","order":0-63}}]}
  ```
  Configs: `latest_levels` `{"curves":[code,...]}` (1–12 distinct curve codes); `price_chart` `{"curve":code,"contractId":uuid}`; `positions` `{"definitionId":uuid}`. At most 32 widgets, distinct ids, at most 24,000 bytes. Unknown fields, kinds or sections are `dashboard_layout` (422).

- [ ] **Step 1: Write the failing check** `tools/check_dashboard_layout.py::run_checks(url, query)`, in the style of `tools/check_table_views.py`:
  - `account starts without a dashboard`: `trader.call('dashboard/layout') == {'version': None, 'layout': None}`
  - `layout follows the account into another browser session`
  - `a retried save returns its first outcome`: same `requestId` + same body → identical response, one stored version
  - `a request id reused for a different layout is a conflict`: 409 `dashboard_request_conflict`
  - `a stale expected version is refused and loses nothing` (Review Focus 2): second session saves with the old version → 409 `dashboard_layout_conflict`; GET still returns the first session's layout
  - `unknown kinds, sections, fields and oversize layouts are refused`: each → 422 `dashboard_layout`
  - `switching arrangement keeps every widget`: save `board` with 3 widgets, then `morning` → same widget ids and configs
  - `an account without ReadMarketData cannot read or save`: 403

- [ ] **Step 2: Run it to verify it fails**
  Run: `python3 tools/run_preview.py --check`
  Expected: FAIL at `account starts without a dashboard` (404).

- [ ] **Step 3: Write migration `0139_dashboard_layouts.sql`**
  `presentation.dashboard_layouts(tenant_id, workspace_id, principal_id, version uuid, layout jsonb)` with one row per account (primary key on the three ids), and `presentation.dashboard_layout_requests(tenant_id, workspace_id, principal_id, request_id, body_hash bytea, response jsonb)` for idempotent replay. Row security and a `security.dashboard_identity()` helper exactly as `0012_account_table_views.sql`, but requiring `ReadMarketData` instead of `ReadTrades`.

- [ ] **Step 4: Implement `DashboardLayouts.Validate/Read/Save` in `DashboardLayouts.cs`**
  Follow `TableViews.cs`: an advisory lock per account, then replay by request id (same hash → stored response; different → `dashboard_request_conflict`), then the expected-version check, then upsert with a new version. Map `GET`/`POST /dashboard/layout` under `database.WithIdentity(Profile(session),"ReadMarketData",...)`.

- [ ] **Step 5: Run the checks**
  Run: `python3 tools/run_preview.py --check`
  Expected: every `PASS` line above, no failures.

- [ ] **Step 6: Add D261 to `docs/decisions.md`** (page, Board/Morning, one layout per account, layout is current-only presentation state).

- [ ] **Step 7: Commit** — `git commit -s -m "Store an account's dashboard layout"`

---

### Task 2: Risk run definition storage

**Files:**
- Create: `db/migrations/0140_risk_run_definitions.sql`
- Create: `src/OpenEtrm.Api/RiskRunDefinitions.cs`
- Modify: `src/OpenEtrm.Api/Program.cs` (next to `/portfolio/risk/jobs`, line ~1237)
- Create: `tools/check_risk_run_definitions.py`; Modify: `tools/run_preview.py`
- Modify: `docs/decisions.md` (add D262: saved risk run definitions)

**Interfaces:**
- Produces:
  - `POST /api/risk/definitions` with `SaveRiskRunDefinition(Guid RequestId, Guid? DefinitionId, int? ExpectedVersion, string Name, ImmutableArray<Guid> BookIds, string Preset, ImmutableArray<DefinitionSize> Sizes)` → `RiskRunDefinitionVersion`.
  - `DefinitionSize(PriceUnitReference Unit, string Size)`: `Unit` is the exact currency and unit references a `PriceUnit` holds; `Size` is a positive plain decimal.
  - `Preset` ∈ `held_contracts`, `held_curves`.
  - `GET /api/risk/definitions` → the caller's definitions, each at its latest version.
  - `RiskRunDefinitionVersion = {definitionId, version:int, name, bookIds, preset, sizes, knownFrom}`.
- Storage:
  - `risk.run_definitions(catalog_id, id, tenant_id, workspace_id, owner_id)`.
  - `risk.run_definition_versions(definition_id, version, name, preset, known_from)`, immutable.
  - `risk.run_definition_books(definition_id, version, book_id → trading.books)`.
  - `risk.run_definition_sizes(definition_id, version, currency/unit foreign keys, size numeric)`.
  - `risk.run_definition_requests` for replay.
  - Owner-only row security. Saving requires `RunScenario` and trading access to every book.

- [ ] **Step 1: Write the failing check** `tools/check_risk_run_definitions.py`:
  - `a definition is saved with exact books, preset and sizes`
  - `revising creates version 2 and keeps version 1 readable`
  - `a retried save returns its first outcome; a reused request id with other terms is a conflict` (409 `risk_definition_request_conflict`)
  - `a stale expected version is refused` (409 `risk_definition_conflict`)
  - `an unknown preset, empty books, duplicate unit or non-positive size is refused` (422 `risk_definition`)
  - `a book the account cannot trade-read is refused` (403)
  - `another account sees none of these definitions`

- [ ] **Step 2: Run to verify it fails** — `python3 tools/run_preview.py --check`; Expected: FAIL (404).

- [ ] **Step 3: Write migration `0140_risk_run_definitions.sql`** as specified above, with foreign keys only (no JSON relationships) and a trigger refusing UPDATE/DELETE on version rows.

- [ ] **Step 4: Implement `RiskRunDefinitions.Save/List`**; map the endpoints under `RunScenario`.

- [ ] **Step 5: Run the checks** — Expected: all `PASS`.

- [ ] **Step 6: Add D262 to `docs/decisions.md`**: saved definitions, the preset resolved in the backend, no default sizes.

- [ ] **Step 7: Commit** — `git commit -s -m "Save risk run definitions"`

---

### Task 3: Run now from a definition

**Files:**
- Modify: `src/OpenEtrm.Api/RiskRunDefinitions.cs`, `src/OpenEtrm.Api/Program.cs`
- Modify: `db/migrations/0140_risk_run_definitions.sql`: add `risk.definition_runs(run_id primary key, definition_id, version, catalog_id, context_id, created_at)` and `risk.definition_run_jobs(run_id, part, job_id → risk.jobs)`. This was planned before publication; subsequent changes to the published schema require a new migration.
- Modify: `tools/check_risk_run_definitions.py`

**Interfaces:**
- Consumes:
  - `PortfolioRiskPreview.Catalog`: its `heldContractTargets`, `heldCurveTargets` and `scenarioTargets`.
  - `RiskJobs.Submit` with `RiskJobRun(Id, Part, Parts)`.
  - `RiskJobRun.MaxParts`.
- Produces: `POST /api/risk/definitions/{id}/run` with `RunRiskDefinition(Guid RequestId, int Version, FuturesReadRequest Context)` → `{runId, definitionId, version, jobs:[{part, jobId}]}`.

- [ ] **Step 1: Add failing checks**:
  - `run now queues the held targets of the definition's books at the retained context`: the job requests' targets equal the catalog's `heldContractTargets`, each with the size for its price unit.
  - `a held price unit without a size is refused and queues nothing`: 422 `risk_definition_size`; the message names the unit; no job is created.
  - `parts hold at most sixteen bumps and share one run id`.
  - `a retried run returns its first outcome`: same `requestId` → same `runId` and jobs.
  - `a definition book the account can no longer read refuses the run` (Review Focus 3): revoke the book's access, then 403.
  - `a non-retained context is refused`: `risk_job_context`.

- [ ] **Step 2: Run to verify it fails** — Expected: FAIL (404).

- [ ] **Step 3: Implement `RiskRunDefinitions.Run`**. In one transaction:
  1. Replay the request id.
  2. Load the exact version and re-check book access.
  3. Build the catalog for its books at the context.
  4. Select the preset's held targets.
  5. Refuse if any target's price unit has no size.
  6. Split into parts of 16 in catalog order, and submit each through `RiskJobs.Submit` with one new run id.
  7. Insert the `definition_runs` rows.

- [ ] **Step 4: Run the checks** — Expected: all `PASS`.

- [ ] **Step 5: Commit** — `git commit -s -m "Queue a risk run from a saved definition"`

---

### Task 4: Latest completed run of a definition

**Files:**
- Modify: `src/OpenEtrm.Api/RiskRunDefinitions.cs`, `src/OpenEtrm.Api/Program.cs`, `tools/check_risk_run_definitions.py`

**Interfaces:**
- Produces: `POST /api/risk/definitions/{id}/latest` with `LatestRiskRun(FuturesReadRequest Context)` → one of:
  - `{state:"latest"|"newer_available", run:{runId, version, valuationAt, knownAt, parts:[<RiskJobs.Read result>...]}, newer:{runId, valuationAt}|null, incomplete:{runId, valuationAt, failedParts}|null}`
  - `{state:"none"}`
  - 409 `risk_run_tie` with `{runs:[{runId, valuationAt}]}`
- `parts` is in part order, in the shape `web/src/delta-ladder.ts` `deltaLadder()` already accepts.

- [ ] **Step 1: Add failing checks** (all against a definition with several runs at different valuation times):
  - `the latest completed run at or before the effective time is chosen`
  - `a run known after the knowledge cutoff is not visible`: choose a cutoff between two runs' creation; the earlier run is returned.
  - `a run with a failed or cancelled part is not completed` (Review Focus 4): cancel a part of the newest run; the previous run is returned, with `incomplete` naming the newer run.
  - `a newer completed run beyond the selected effective time is stated`: with current knowledge and an earlier selected close, `state == 'newer_available'` and `newer` names the later run. With a past knowledge cutoff, runs known after it are never mentioned.
  - `two completed runs at the same valuation time are refused`: 409 `risk_run_tie` listing both.
  - `a queued run is never shown in a past context` (Review Focus 5).
  - `lost book access refuses the read` (Review Focus 3).

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement `RiskRunDefinitions.Latest`**
  - Select by `valuation_at <= effective_at` and `created_at` known by the cutoff, through the existing knowledge-store resolution. Never resolve by row order.
  - "Completed" means every part's job is `completed`.
  - Read each part with `RiskJobs.Read` so current authority is re-checked.

- [ ] **Step 4: Run the checks** — Expected: all `PASS`.

- [ ] **Step 5: Commit** — `git commit -s -m "Read a definition's latest completed risk run"`

---

### Task 5: Dashboard page, layout and freshness

**Files:**
- Create: `web/src/dashboard-layout.ts` (pure layout model), `web/src/freshness.ts` (footer model), `web/src/dashboard-view.ts` (page)
- Modify: `web/src/navigation-groups.ts`: add `{id:'overview',label:'Overview',pages:['dashboard']}` first; `pageAvailable('dashboard')` requires `ReadMarketData`. `firstPage` is unchanged.
- Modify: `web/src/index.html` (page section and `nav-dashboard`), `web/src/app.ts` (route), `web/src/app.css`
- Test: `web/tests/dashboard-layout.test.mjs`, `web/tests/freshness.test.mjs`, `web/tests/navigation-groups.test.mjs` (extend)

**Interfaces:**
- Produces:
  - `type Arrangement='board'|'morning'`
  - `type Section='weather_events'|'inventories'|'prices_positions'|'missed'`
  - `type DashboardWidget` and `type DashboardLayout`, matching Task 1's JSON exactly.
  - `homeSection(kind): Section`: latest_levels, price_chart and positions all → `prices_positions`.
  - `addWidget(layout, widget): DashboardLayout`, `removeWidget(layout, id)`, `moveOnBoard(layout, id, box)`, `moveToSection(layout, id, section, order)`, `arrange(layout, arrangement)`. None of these changes another widget's config.
  - `morningSections(layout): {section, label, widgets}[]`, in the fixed order with labels "Weather & events", "Inventories", "Prices & positions", "Might have missed".
  - `type Freshness={state:'latest'|'newer_available'|'historical'|'not_yet_available'|'failed';observedAt:string|null;source:string;reason?:string}`
  - `freshnessFooter(f, zone): string`, which never shows a time it was not given.

- [ ] **Step 1: Write failing tests**:
  - `arrange keeps every widget and its config` (board→morning→board)
  - `morning sections follow the fixed order and show empty sections`
  - `a widget moved to another section stays there after arranging`
  - `freshness footer states source, observation time and state; a missing time reads "No observation yet"`
  - `failed shows the backend reason`
  - `dashboard is listed for ReadMarketData accounts only`

- [ ] **Step 2: Run** `cd web && npm test` — Expected: FAIL (module not found).

- [ ] **Step 3: Implement the two models and the page shell**
  - Header: the existing context selector and a Board / Morning switch.
  - Board: a 12-column CSS grid placed from `board` boxes.
  - Morning: the four sections in order.
  - Saving goes through `/dashboard/layout` with a request id and the expected version; a 409 shows "This dashboard changed in another session" with **Reload**.
  - Each widget renders in its own error boundary, so a refusal in one widget never blanks the page.

- [ ] **Step 4: Run** `cd web && npm test` — Expected: PASS.

- [ ] **Step 5: Commit** — `git commit -s -m "Add the Dashboard page with Board and Morning layouts"`

---

### Task 6: Latest levels and Price chart widgets

**Files:**
- Create: `web/src/dashboard-levels.ts`, `web/src/dashboard-chart.ts`
- Modify: `web/src/dashboard-view.ts`
- Test: `web/tests/dashboard-levels.test.mjs`

**Interfaces:**
- Consumes:
  - `POST /market/curve-shapes` with the session's snapshot, as `market-board.ts` reads it.
  - `latest-close.ts` `closeBadge` and `latestInstant`, for freshness.
  - `chart-view.ts` / `time-chart.ts` with `POST /history` for the chart.
- Produces:
  - `levelsTiles(shapes, configCurves): LevelTile[]`, where `LevelTile = {curve, name, front, price, unit, sparkline} | {curve, refusal}`. The tiles are in config order; a configured curve the read does not return becomes a refusal tile (Review Focus 1).
  - `levelsFreshness(badge, closeAt): Freshness`.

- [ ] **Step 1: Write failing tests**:
  - `tiles follow configured order with backend front, price and unit unchanged`
  - `a configured curve missing from the read becomes a refusal tile, not a gap`
  - `a curve the close cannot price shows the backend's reason`
  - `freshness is latest, newer_available or historical from the close badge, with the close instant`
  - `intraday reads "Intraday not configured"`

- [ ] **Step 2: Run** `cd web && npm test` — Expected: FAIL.

- [ ] **Step 3: Implement the widgets.**
  - Latest levels: one curve-shapes read for all configured curves, at most `curvesPerRead`.
  - Price chart: the existing time chart for the configured curve and contract.
  - Config pickers list only curves and contracts that the navigation read returns for the context.
  - In the current context, both widgets re-read through the existing live-correction refresh (D188), as Curves & prices does. A past context subscribes to nothing.

- [ ] **Step 4: Run** `cd web && npm test` — Expected: PASS.

- [ ] **Step 5: Commit** — `git commit -s -m "Show latest levels and a price chart on the dashboard"`

---

### Task 7: Positions widget

**Files:**
- Create: `web/src/dashboard-positions.ts`
- Modify: `web/src/dashboard-view.ts`
- Test: `web/tests/dashboard-positions.test.mjs`

**Interfaces:**
- Consumes: Tasks 2–4 endpoints; `delta-ladder.ts` `deltaLadder(parts, curveName)`.
- Produces: `positionsView(latest): PositionsView`, where `PositionsView = {ladder, freshness, newer, incomplete} | {none:true} | {tie:runs} | {refusal}`.

- [ ] **Step 1: Write failing tests**:
  - `a latest run draws the ladder from its parts unchanged`
  - `newer_available and an incomplete newer run are both stated`
  - `a tie lists the tied runs and draws nothing`
  - `none reads "No completed run yet" with Run now offered`
  - `historical context hides Run now`

- [ ] **Step 2: Run** `cd web && npm test` — Expected: FAIL.

- [ ] **Step 3: Implement the widget.**
  - Configuring creates or picks a definition: name, books, preset, and one size per price unit the catalog names. Sizes are never prefilled.
  - Run now posts with a fresh request id and keeps it for retry after an unknown outcome.
  - Ladder bars come from `deltaLadder` values.

- [ ] **Step 4: Run** `cd web && npm test` — Expected: PASS.

- [ ] **Step 5: Commit** — `git commit -s -m "Show positions by month from a saved risk definition"`

---

### Task 8: Browser check, benchmark and capability record

**Files:**
- Create: `tests/browser/dashboard.mjs`; Modify: `tests/browser/run.mjs`
- Modify: `tools/benchmark_pages.py` (add the dashboard with a seeded layout of all three widget kinds)
- Create: `docs/market-dashboard.md` (what is implemented, what remains: phases 2–4, close lateness, Blotter use of definitions)
- Modify: `docs/implementation.md`, `docs/verification.md`, `docs/plan.md` (Later scope: dashboard phases 2–4)

- [ ] **Step 1: Write the browser check**:
  1. Open Dashboard.
  2. Add one widget of each kind.
  3. Switch Board → Morning → Board and assert the same widgets.
  4. Reload and assert the layout persists.
  5. Create a definition, Run now, and wait for the ladder.
  6. Select a past context and assert the read-only state with no Run now.
  7. Configure a curve, then pick a context where it is absent, and assert a refusal tile while the other widgets render.

- [ ] **Step 2: Run** `python3 tools/run_preview.py --check --browser-check --browser <chrome>` — Expected: dashboard checks PASS.

- [ ] **Step 3: Run** `python3 tools/benchmark_pages.py` — Expected: dashboard p95 under 1000 ms. Record the workload, widget counts, definitions, runs and environment in `docs/market-dashboard.md`.

- [ ] **Step 4: Write the capability docs**, recording only what was verified.

- [ ] **Step 5: Run the full suite** `python3 tools/check_all.py` — Expected: no failures.

- [ ] **Step 6: Commit** — `git commit -s -m "Verify and document dashboard phase 1"`. Then request adversarial review (Tasks 1–4: temporal, async, authorization) before any publication.
