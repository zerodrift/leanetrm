# Trade actions and instrument bar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** New trade has one instrument list; a position's details show its trade actions as buttons, including backend-drafted increase and reduce; risk runs for one trade.

**Architecture:** The chooser in `web/src/new-trade.ts` loses its board state and is always the grouped bar. A new pure module supplies the action list to both the row menu and the position head. The backend's close-out draft code takes an optional adjustment (direction and quantity) and is exposed on a second route. The Blotter's risk scope gains an optional trade.

**Tech Stack:** TypeScript (presentation only, `node --test`, headless Chrome tests), .NET 10 minimal API, PostgreSQL-backed checks in CI.

**Spec:** `docs/superpowers/specs/2026-11-17-trade-actions-and-instrument-bar-design.md`

## Global Constraints

- TypeScript is presentation only: no term, sign, limit or eligibility is computed in the browser.
- Never a silent default: a missing or ambiguous draft input refuses the whole draft with a reason.
- One entry pane, one mode, one instance of each editor (D421).
- Actions not wired are drawn Coming soon (`ticket-soon`, disabled, `aria-disabled`) and send nothing.
- Button ids `new-trade-<product>` and the group id `new-trade-product` are kept.
- No new dependency, table, migration or permission.
- Synthetic examples only; no vendor or customer names.
- Local toolchain: `~/.openetrm-tools/node-24.22.0` and `~/.openetrm-tools/dotnet-10.0.402`. No database here: database-backed and live browser checks run in CI only.
- Commit with `-s` (DCO). Nothing is pushed without the owner's approval.

## Review Focus

- A quantity typed as `0`, `-5`, `1e3`, `1/3`, `  10 ` or empty: the backend refuses each with a reason shown in the details; no form opens.
- The row's revision changes between pressing Buy more and pressing Draft trade: the operation refuses as Close out does, naming both revisions.
- The details hold unsaved edits when an action button is pressed: the leave question is asked before anything is read or replaced.
- A one-trade risk run is on the desk and the person selects another row: its Risk tab and lens cell read "Not in this run", not "Not valued at this close".
- The Blotter is in its own window: the pop-out shows the same action buttons, and the main window's New trade bar still lists only what that window opens.

---

### Task 1: The instrument bar

**Files:**
- Modify: `web/src/new-trade.ts` (`setupNewTrade`), `web/src/app.css:1041-1063`
- Test: `web/tests/new-trade-chooser-browser.test.mjs`, `web/tests/new-trade-page-browser.test.mjs`

**Interfaces:**
- Produces: `#new-trade-product.instrument-bar` with no `data-state`; each `button.instrument` holds only `.instrument-name` (and `.instrument-mark` or `.ticket-soon-tag`) and carries its description in `title`.

- [ ] **Step 1: Failing tests.** In the chooser browser test: with no product chosen and with `swaps` chosen, `#new-trade-product` has the same child structure and the same computed `display`; no `.instrument-about` exists; `#new-trade-swaps.title` equals `productAbout('swaps')`; with nothing chosen `#new-trade-note` is hidden and the status reads `Choose an instrument type to enter a new trade.`; at 1280 px every group is on one line and the bar's height is under 60 px; at 360 px the bar wraps and `scrollWidth<=clientWidth`; on the page (`#futures-page[data-entry]`) the bar's computed `position` is `sticky`, in `.details-form` it is `static`.
- [ ] **Step 2: Run** `cd web && npm test 2>&1 | tail -30`. Expected: the new assertions fail.
- [ ] **Step 3: Implement.** Remove the `about` span from tiles and set `button.title`; drop `chooser.dataset.state`; rename the class to `instrument-bar` keeping `instrument-chooser` out. CSS: one layout (the former `[data-state=chosen]` rules, restyled as a segmented control per group: shared border, 32 px buttons, pressed state in accent, group label in the small-caps style); `#futures-page[data-entry] .new-trade-head{position:sticky;top:0;z-index:2;background:var(--surface)}` with a bottom border. Delete the board rules.
- [ ] **Step 4: Run** `npm test`. Expected: pass. Update assertions in existing tests that named the board (`data-state`, `.instrument-about`).
- [ ] **Step 5: Commit** `Show New trade's instruments as one bar`.

### Task 2: One action list for the row menu and the position head

**Files:**
- Create: `web/src/position-actions.ts`
- Modify: `web/src/blotter-row-menu.ts` (items come from the new module), `web/src/blotter-operations.ts` (new operation)
- Test: `web/tests/position-actions.test.mjs`, `web/tests/blotter-row-menu.test.mjs`, `web/tests/blotter-operations.test.mjs`

**Interfaces:**
- Produces:
  - `type AdjustDirection='increase'|'reduce'`
  - `BlotterOperation` gains `{kind:'draftAdjustment';key:string;direction:AdjustDirection;quantity:string}`; `readBlotterOperation` refuses any other direction and a `quantity` that is not a non-empty string of at most 64 characters (it does not judge the number).
  - `adjustLabels(row:{instrumentType:string;lots:string|null}):Record<AdjustDirection,string>`: `future` with lots not starting `-` → `Buy more…`/`Sell…`; `future` with lots starting `-` → `Sell more…`/`Buy back…`; otherwise `Increase…`/`Reduce…`.
  - `adjustUnit(row:{lots:string|null;unit:string}):string`: `lots` when `lots!==null`, else `row.unit`.
  - `type PositionAction={id:'closeOut'|'amend'|'increase'|'reduce'|'risk'|'terminate'|'novate';label:string;state:{kind:'enabled'}|{kind:'disabled';reason:string}|{kind:'soon'}}`
  - `positionActions(row,can:RowMenuCan&{risk:boolean;why?:{risk?:string}}):PositionAction[]` in the spec's order, `terminate` and `novate` always `soon`.
- `rowMenuItems` builds its Close out, Amend and the two adjustment items from `positionActions`; adjustment items in the menu select the position and open the head's quantity row (action `{kind:'adjust',direction}`).

- [ ] **Step 1: Failing tests** for each interface above, including: stale disables every real action with the stale reason and leaves `soon` as `soon`; a refused action without a reason throws (as the menu does today); labels for `'40'`, `'-15'`, `null`.
- [ ] **Step 2: Run** `npm test`; expected fail on missing module.
- [ ] **Step 3: Implement** the module and the operation reader.
- [ ] **Step 4: Run** `npm test`; expected pass.
- [ ] **Step 5: Commit** `Name a position's actions once for its menu and its details`.

### Task 3: The adjustment draft in the backend

**Files:**
- Modify: `src/OpenEtrm.Api/CloseOutDrafts.cs`, `src/OpenEtrm.Api/Program.cs:2285` (second route)
- Test: `tests/OpenEtrm.Api.Checks/CloseOutDraftChecks.cs`, `tools/check_close_out_drafts.py` (fixture positions only if a one-schedule swap is not already booked)

**Interfaces:**
- Produces: `POST /trading/position-adjustment-draft` taking `PositionAdjustmentDraftRequest(Context,TradeId,BindingId,Revision,InstrumentType,Direction,Quantity)`, all `JsonRequired`, unmapped members disallowed. Result: the close-out draft's object plus `adjustment:{direction,quantity}`.
- `internal sealed record PositionAdjustment(bool Increase,ExactDecimal Quantity)` with `static PositionAdjustment From(string direction,string quantity)`: direction exactly `increase` or `reduce`; quantity matches `^[0-9]{1,18}(\.[0-9]{1,12})?$` and is greater than zero; anything else `CloseOutTerms.Refused(...)`.
- `FuturesStore.DraftCloseOut` and `SwapStore.DraftCloseOut` take `PositionAdjustment? adjustment` (null is the close-out, unchanged).

- [ ] **Step 1: Failing checks** in `CloseOutDraftChecks`, expected values taken from the fixture's bookings, not from the draft: long 40 lots, increase 10 → `lots=="10"`; long 40, reduce 10 → `"-10"`; short −15, increase 5 → `"-5"`; short −15, reduce 15 → `"15"`; short −15, reduce 20 → refused `close_out` naming 15 open lots; quantities `0`, `-5`, `1e3`, `1/3`, empty → refused; direction `sell` → refused; price and parties equal the close-out draft's for the same position; one-schedule swap increase keeps the coefficient's sign and states the quantity, reduce reverses it; scheduled swap → refused; `physical_gas` → refused by name on both routes; a stale revision → the close-out's revision refusal.
- [ ] **Step 2: Build** `~/.openetrm-tools/dotnet-10.0.402/dotnet build tests/OpenEtrm.Api.Checks`. Expected: compile errors for the missing request type.
- [ ] **Step 3: Implement.** Futures: `lots=adjustment is null?-held:(adjustment.Increase==held.IsPositive?q:-q)`, refusing a reduce above `|held|`. Swap: refuse when `scheduled is not null`; `Quantity=q`; coefficient kept or reversed; refuse a reduce above the swap's own quantity. `TradeReads.PositionAdjustmentDraft` shares one private method with `CloseOutDraft`; that method refuses an instrument type other than `future` and `financial_swap` by name before reading (heat rate and PPA keep their existing words).
- [ ] **Step 4: Build** the solution; expected clean. The checks themselves run in CI (`--close-out-drafts-check`).
- [ ] **Step 5: Commit** `Draft an increase or reduction of a position for review`.

### Task 4: Request, reader and forms for an adjustment draft

**Files:**
- Modify: `web/src/close-out-draft.ts`, `web/src/futures-booking.ts`, `web/src/swap-editor.ts`
- Test: `web/tests/close-out-draft.test.mjs`

**Interfaces:**
- Consumes: Task 3's route and result.
- Produces:
  - `adjustmentRequest(read,h,direction:AdjustDirection,quantity:string)` = `closeOutRequest(read,h)` plus `direction` and `quantity` as given.
  - `readCloseOutDraft(value,kind,adjustment?:{direction;quantity})`: when given, requires `value.adjustment` to equal it exactly.
  - `CloseOutShown` becomes `{reference:string;adjustment?:{direction:AdjustDirection;label:string}}`; `renderCloseOut` heads the form `<label without …> of <reference>` and, for a future, says `A new trade in the same contract, book and parties as <reference>. Nothing is booked until you press Book.`
  - `closeOutRefusal(error,reference,what='Close-out')` prefixes `<what> of <reference> was not drafted.`

- [ ] **Step 1: Failing tests** for each.
- [ ] **Step 2: Run** `npm test`; expected fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `npm test`; expected pass.
- [ ] **Step 5: Commit** `Read and show an adjustment draft as the close-out is`.

### Task 5: Action buttons in the position head

**Files:**
- Modify: `web/src/position-panel.ts` (`drawHead`, `sync`), `web/src/futures-view.ts` (`draftCloseOut` generalised, handlers, `rowAction`), `web/src/app.css`
- Test: `web/tests/position-panel-actions-browser.test.mjs` (new), `tests/browser/blotter-close-out.mjs` (CI)

**Interfaces:**
- Consumes: `positionActions`, `adjustLabels`, `adjustUnit`, `adjustmentRequest`, the `draftAdjustment` operation.
- Produces: in `.side-panel-actions`: `#side-panel-close-out`, `#side-panel-amend`, `#side-panel-increase`, `#side-panel-reduce`, `#side-panel-risk`, `#side-panel-open-curve`, `#side-panel-terminate`, `#side-panel-novate`; a quantity row `#side-panel-adjust` (`form`, hidden until asked) with `#side-panel-adjust-quantity` (`required`, `inputmode="decimal"`, labelled `<label without …>: quantity in <unit>`), `#side-panel-adjust-submit` (`Draft trade`) and `#side-panel-adjust-cancel`. `setupPositionPanel` options gain `actions:(h:Holding)=>PositionAction[]` and `run:(id:PositionAction['id'],input?:{quantity:string})=>void`.

- [ ] **Step 1: Failing browser test** (headless, no backend): the eight buttons in order; a disabled action shows its reason in `title` and in the note line when focused; Terminate and Novate carry `ticket-soon` and never call `run`; pressing Increase shows the quantity row focused, Cancel hides it and returns focus, submit calls `run('increase',{quantity:'10'})` with the text as typed; pressing Reduce while the row is open for Increase relabels it; the buttons wrap at 320 px without overflow.
- [ ] **Step 2: Run** `npm test`; expected fail.
- [ ] **Step 3: Implement.** `draftCloseOut(key)` becomes `draftPositionTrade(key,adjustment?)`: same revision pin, booking control, refusal handling and form opening; only the request, the reader's expectation and `shown` differ. The pinned revision for an adjustment is the row's when the quantity row was opened. The head's primary look: Close out and the two adjustments `secondary`, Amend as today, the row separated from Coming soon by a divider.
- [ ] **Step 4: Run** `npm test`; expected pass. Extend `tests/browser/blotter-close-out.mjs` with: Buy more 10 on the long future opens the futures form with lots `10`; Sell 10 with `-10`; reduce 99 shows the refusal in the details and no form; unsaved edits ask first.
- [ ] **Step 5: Commit** `Show a position's trade actions as buttons`.

### Task 6: Risk for one trade

**Files:**
- Modify: `web/src/futures-view.ts` (risk scope, `openRiskReport`, `positionRisk`, run bar), `web/src/risk-view.ts` (scope line), `web/src/position-panel.ts` (Risk tab words)
- Test: `web/tests/blotter-risk-scope.test.mjs` (new), `tests/browser` risk check (CI)

**Interfaces:**
- Produces: `blotterRiskScope(read:{context:Record<string,unknown>},view:{bookIds:string[];portfolioIds:string[]},trade:{tradeId:string}|null)` in `web/src/risk-scope.ts`, returning `{context,bookIds,portfolioIds}` and `tradeIds:[tradeId]` only when a trade is given; `riskScopeNote(trade:{reference:string}|null):string|null` = `This run covers trade <reference> only.`; `PositionRisk` gains `{state:'outside';reference:string}` drawn as `Not in this run: it covers <reference> only.`

- [ ] **Step 1: Failing tests** for the two functions, and that a scope with a trade is not `sameRiskScope` as the same books without one.
- [ ] **Step 2: Run** `npm test`; expected fail.
- [ ] **Step 3: Implement.** The Blotter keeps `riskTrade:{tradeId,reference}|null`. The head's Risk sets it and opens the report; the toolbar's Run risk clears it. Changing it resets the report's captured catalog and scope before `risk.open` so the catalog is read for the new scope. The run bar's detail and `#risk-scope` show `riskScopeNote`. `positionRisk` returns `outside` for a row whose trade is not the run's when the run's scope names trades.
- [ ] **Step 4: Run** `npm test`; expected pass.
- [ ] **Step 5: Commit** `Run risk for the one trade the details show`.

### Task 7: Documents, decision and final review

**Files:**
- Modify: `docs/decisions.md` (next free number), `docs/blotter-workbench.md` (row menu and details head), `docs/frontend-wiring.md`, `docs/verification.md`, `docs/plan.md`, the spec's status line

- [ ] **Step 1:** Write the decision record: what changed, what it supersedes in D421, the four calls listed in the spec, what is left (scheduled swaps, other instruments, Terminate and Novate, a second risk run).
- [ ] **Step 2:** `cd web && npm test` and `dotnet build` once more; expected clean.
- [ ] **Step 3:** One independent review of the whole branch, on the draft's authority and numbers and the unsaved-edit and revision paths. Resolve findings.
- [ ] **Step 4: Commit** `Record trade actions, the adjustment draft and one-trade risk`.
