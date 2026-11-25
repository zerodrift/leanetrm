# Curve Marks Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Publish and inspect curve volatility and correlation marks through polished grids and monthly strips, with exact analytics and durable receipts.

**Architecture:** A managed editor service sits over existing immutable mark facts and request receipts. Market-only reads and one atomic multi-target writer expose explicit context and revision contracts. TypeScript renders grids, charts and pending commands.

**Tech Stack:** C#/.NET 10, stock PostgreSQL, TypeScript and existing ECharts, Python preview checks and real Chromium browser checks.

**Spec:** [Curve marks editor design](../specs/2026-11-24-curve-marks-editor-design.md)

## Global Constraints

- The spec is authoritative; existing financial values and legacy mark command outcomes remain unchanged.
- Exact decimal fractions, no rounding, inference, withdrawal, interpolation, PSD validation or automatic reprice.
- TypeScript presents backend results. Only chart coordinates may use approximate numbers.
- One kind/source/effective instant per atomic batch, at most 240 unique cells; convention `curve_marks_log_return_held_v1`, policy `curve_mark_batch_v1`.
- ReadMarketData for editor reads; additionally ImportObservations and current working mode for review/publication; no ReadTrades dependency.
- Preserve certified knowledge and business time, actor-owned recovery and existing risk-worker lease rules.
- Synthetic public examples only. TDD and `git commit -s`. No pushes during implementation.
- Database/HTTP/browser qualification runs through tools/run_preview.py and tools/check_all.py with PG_BIN, DOTNET and BROWSER set; use disposable resources only.
- Migration and decision numbers are next free at integration. Keep private reports/logs out of tracked files.

## Review Focus

1. Later-effective write head beside an earlier displayed mark: disable correction without leaking heads in historical views (Tasks 2–3).
2. Lost reply after the session expires: reconstruct durable source coordinates and recover only the original actor's receipt (Tasks 2–5).
3. Original 0.50 acknowledged after a 0.60 correction: receipt must not imply that 0.60 was reverted (Tasks 2–5).
4. One conflicting cell in a many-curve batch: no partial facts or receipt; preserve draft (Tasks 2–5).
5. Full-matrix paste with unequal mirrors, zero or blanks: reject conflict atomically, preserve zero, never treat blank as withdrawal (Tasks 1, 4–5).

### Task 1: Exact managed editor contracts and draft behavior

**Files:** Create `src/OpenEtrm.Api/CurveMarkEditorContracts.cs`, `CurveMarkEditorDraft.cs`; create `tests/OpenEtrm.Api.Checks/CurveMarkEditorChecks.cs`; register `--curve-mark-editor` in checks Program and managed runner.

**Interfaces:** Public record contracts for context (ContextId, AsOf, BusinessDate, optional SnapshotId), view (kind, mode, ordered curve selection, inclusive FromMonth/ToMonth), cell key (FirstCurve, nullable SecondCurve, Month), change (key fields, ExpectedRevision, Value), and batch (Policy, OwnerId, OperationId, Kind, Source, Context, Reason, Changes). Use CatalogueSource for exact definition/reference identity. Expose pure `CurveMarkEditorDraft` operations for command normalization, view cells, draft validation/coverage/differences and addressed TSV/matrix paste. Define precise request/response records here for later tasks; adapters may extend them with context metadata without changing behavior.

- [ ] Write failing checks for 0.500000000000→0.5, exact 0.6−0.5=0.1, negative correlation, 12-place values, invalid range/precision, canonical pairs, duplicate keys, bounds, missing/zero distinctions, 20×12 grid and 240-month strip, rectangular TSV and full matrix mirrors/diagonals/blanks; preserve whole pre-paste draft on refusal.
- [ ] Run `$DOTNET run --project tests/OpenEtrm.Api.Checks -c Release -- --curve-mark-editor` and capture expected failure.
- [ ] Implement the managed contracts and pure behavior with structured errors and exact existing number types; manual clearing invalid, no formula parsing.
- [ ] Run the new checks and affected existing API checks; inspect diff and signed-off commit.

### Task 2: Atomic native batches and market-only history

**Files:** New next-free `db/migrations/*_curve_mark_editor.sql`, db README; `tools/check_curve_marks.py` and its preview runner/check_all registration.

**Interfaces:** Native setup/cells/history/status and atomic batch operations over existing tables; request policy and canonical contracts from Task 1. Existing `trading.record_curve_marks` delegates shared private cell logic without changing legacy normalization or receipts. New batches keep one aggregate receipt and durable source coordinates in versioned metadata.

- [ ] Write failing disposable native checks for atomic multi-curve and multi-pair commands, one-publication/receipt counts, invalid/stale rollback, identical facts/older acknowledgement, exact legacy replay, source/authority denials, bounded history, temporal boundaries, owner-only status, absence versus denial and durable-coordinate recovery.
- [ ] Run the focused preview and record the missing-function failure.
- [ ] Implement with pinned predecessors and existing authority/commit conventions; working editor reads register certified market-only inputs and historical reads bind them, both filtering publication prefix AND known time. Leave valuation/risk readers untouched. History pages 50, selected value distinct from current head, no live head in historical mode.
- [ ] Run native checks plus existing gas exercise legacy checks; inspect diff and signed-off commit.

### Task 3: API setup, validation, review, publication and recovery

**Files:** `src/OpenEtrm.Api/CurveMarkEditorEndpoints.cs`, focused store/admission modules, minimal Program registration; extend API and Python checks.

**Interfaces:** POST `/curve-marks/editor/{setup,validate,review,record,status,history}`. Setup returns authorized paged choices/cells/analytics/limits/editability and exact context. Validate addresses draft/paste errors without facts. Review returns definitive changes/acknowledgements using rolled-back exact writer. Record and status return validated immutable receipts. Status never needs the old frame. No generic TradingRead or ReadTrades requirement.

- [ ] Write failing HTTP checks for a market publisher without trade access completing all operations, reader denial, invalid contexts, source revocation/expired authority, review without persistence, later head, record race, old acknowledgement, status after session renewal and context-qualified analytics.
- [ ] Run focused preview to observe missing routes.
- [ ] Implement using KnowledgeResolver/CalendarReadCut and ICommitAdmission patterns. Read bind differs from write source registration. Persist/reconstruct certified ReadInputCoordinates. Fresh current refresh keeps business/source coordinates; current authority is always checked.
- [ ] Run managed and focused native/HTTP checks including existing gas exercise behavior; inspect diff and signed-off commit.

### Task 4: Polished grids, strips, charts and history

**Files:** New feature view/model/style modules under web/src, minimal app/index/navigation hooks; new web tests.

**Interfaces:** Feature page lifecycle supports load/sync/leave/unsaved/pending/reset and exact market selection. Consume Task 3 responses with validated types. Reuse existing theme/chart/actor/context patterns; no new dependency. Draft and publication workflow includes required reason, backend validation and review, then exact record. Recovery hardening belongs to Task 5, but basic pending transport must be used from first publish.

- [ ] Write failing presentation/model tests for missing/zero/edit/error states, backend exact strings, selected cell and strip navigation, context fences, charts preserving gaps, fixed correlation scale and published/draft legends.
- [ ] Run focused web tests and capture failure.
- [ ] Build the approved dense workspace: sticky headers/context/draft bar, aligned values, accessible upper-triangle editing, mirrored lower triangle, inspector/drawer, term charts/heatmap/exact tooltips/backend differences/coverage/history. Explicit selection, scoped draft guards and keyboard editing. Use decimal strings for commands; charts only approximate coordinates.
- [ ] Run web build/tests, inspect responsive semantics and signed-off commit.

### Task 5: Paste, recovery and end-to-end browser behavior

**Files:** Feature web model/view tests, `tests/browser/curve-marks.mjs`, extend focused preview checks.

**Interfaces:** Actor-owned retained immutable PendingCommand with mark-specific restore validation; exact command ID/body retry, owner-only status. Paste returns backend-addressed candidates and is applied atomically after review. UI responses fenced by actor/source/context/generation.

- [ ] Write failing browser/checks for TSV/full-matrix paste, mirrored conflicts, blank/zero, paste bounds; publication conflict preserves draft; lost reply/reload/session expiry; owner/context switch and late replies; old fact acknowledgement; receipt retained on refresh failure; keyboard/narrow/theme layouts.
- [ ] Observe focused browser failures before closing gaps.
- [ ] Complete clipboard and durable recovery, block replacement while unknown, distinguish denial/absence/commit, fresh current editor refresh only. Verify an editor-published missing gas mark produces existing exercise values after explicit reprice; pinned historical/risk results remain unchanged.
- [ ] Run `npm test --prefix web` and focused disposable browser/HTTP checks; inspect screenshots privately at desktop/narrow widths including maximum grids, long labels, empty/error/recovery states; signed-off commit.

### Task 6: Full qualification, independent review and records

**Files:** Feature contract and decision/status/verification docs; spec status line.

- [ ] Run repository, frontend, managed, database, workspace, gas and trading groups with pinned tools, retaining actual counts and any failures.
- [ ] Obtain independent adversarial whole-branch review of authorization, time, atomicity, recovery, numerical behavior and visual implementation. Fix findings with red/green evidence and rerun affected checks.
- [ ] Update decision/architecture/security/implementation/plan/verification records accurately; mark spec built only when all required work passes. Integrate current master and resolve any shared runner/nav/migration-number conflicts with concurrent work.
- [ ] Inspect full staged diff, generated files and commit identity; signed-off records commit. Leave clean local branch ready for the authorized PR workflow; do not deploy or merge.
