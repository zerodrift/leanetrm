# In-Term Physical Delivery Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Booked gas compositions (indexed or fixed-price consideration) keep valuing after delivery starts. Ended gas days carry accrued, unpaid cash in an outstanding-settlement measure until recorded payments clear it, and remaining days stay in remaining delivery MTM.

**Architecture:**
- Two new lifecycle values in compiled Side programs make the shared demand/interpreter layer stop refusing started deliveries:
  - physical: `whole_gas_day_delivery_v1`;
  - cash: `accrued_payment_v1`.
- A new managed binding state, `PhysicalCashState`, carries recorded payments.
- `RetainedGasInstrument` splits each evaluated contribution into remaining and accrued parts and returns both measures and their sum. Instrument/Side pricing and generic scenario risk are unchanged.
- One migration admits the new lifecycles for new gas writes. It adds payment tables with an idempotent recording function and an in-term load (built as a dedicated loader role; see D359).
- The API, inspector and UI project the measures and row states.

**Tech Stack:** C# (.NET 10, `src/OpenEtrm.MarketData`, `src/OpenEtrm.Api`), PostgreSQL migrations (`db/migrations`), Python preview checks (`tools/`), TypeScript presentation (`web/src`).

**Spec:** `docs/superpowers/specs/2026-11-07-in-term-physical-lifecycle-design.md`

## Global Constraints

- TypeScript is presentation only. Lifecycle classification, accrual, payment conservation and discounting live in the managed backend.
- No defaults: a missing policy, fixing, curve point, discount factor or payment state raises a structured `MarketDataException`, never zero or success.
- Idempotent writes (D238): the same payment fact returns the existing row; a retried request id returns its first outcome; a changed value for the same business key is a new revision with its own knowledge time.
- Core references use exact foreign keys. Lifecycle state stays outside economic identity and program digests of trades that do not select it.
- Existing trades keep `future_gas_delivery_v1`/`undue_payment_v1` and their refusals. There is no backfill.
- Crude and LNG keep `crude_delivery_lifecycle`/`lng_delivery_lifecycle`. Selecting the new lifecycle for them refuses with code `physical_lifecycle_unsupported`.
- The ticket keeps refusing to book a started delivery (`gas_delivery_lifecycle` in the booking SQL).
- Whole gas days: a cell is ended when its grid end instant ≤ valuation instant. There is no intraday proration.
- Quantity basis label for accruals: `contract_estimate_allocation_pending_v1`, displayed as "estimated from contract; allocation pending".
- Physical leg value discounts at gas-day end (existing). Cash discounts to its payment date. Due unpaid cash is nominal (no discount).
- Public repo rules (AGENTS.md): synthetic data only; no publisher/vendor names beyond permitted market names; the pilot seed script stays private.
- Commits use `git commit -s` and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Nothing is pushed without the owner's approval.

## Review Focus

1. **A gas day ending exactly at the valuation instant** counts as ended (`End ≤ ValuationAt`). Tested in Task 1.
2. **A payment recorded with a knowledge time after the context's cutoff** is invisible: the row stays accrued and unpaid. Tested in Task 3.
3. **A fixing corrected downward after a payment** shows `Overpaid` with a negative outstanding, not a refusal and not a silent clamp. Tested in Task 3.
4. **Cash past its due date and still unpaid** stays in outstanding settlement at nominal, with no discount factor read. Tested in Tasks 1 and 3.
5. **A portfolio holding an old-lifecycle trade and a new one** after delivery starts: the old one still refuses, as a position-specific refusal, while the new one values. The portfolio total refuses rather than omitting the old trade silently. Tested in Task 5.

---

### Task 1: Admit the started-delivery lifecycles in program demand and classification

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/BookedSideProgram.cs:150-156` (allowed lifecycle/measure pairs)
- Modify: `src/OpenEtrm.MarketData/Trading/ScheduleProgramDemand.cs:90-110`
- Modify: `src/OpenEtrm.MarketData/Trading/ScheduleProgramInterpreter.cs:189-211` (`Classify`)
- Test: `tests/OpenEtrm.MarketData.Checks/ScheduleProgramDemandChecks.cs`

**Interfaces:**
- Produces:
  - lifecycle string constants `ScheduleLifecycles.WholeGasDayDelivery = "whole_gas_day_delivery_v1"` (native gas grid rows, measure `value_excluding_overdue_v1`);
  - `ScheduleLifecycles.AccruedPayment = "accrued_payment_v1"` (dated cash rows, measure `present_value_v1`).
  Both live in a new `static class ScheduleLifecycles` in `ScheduleProgramDemand.cs`.

- [ ] **Step 1: Write the failing checks.** Add `StartedGasDelivery()` to `ScheduleProgramDemandChecks`, called from `Run()`. Build a 3-day physical gas program and a 3-row dated cash program by rewriting lifecycle parameters, as `PhysicalCrudeSideChecks.cs:48` does, and assert:
  - Physical rows with `whole_gas_day_delivery_v1` at a valuation instant equal to day 1's grid end: no `MarketDataException`. Day 1 has no demanded inputs, and days 2–3 demand their `price_lookup` and `discount` inputs exactly as under `future_gas_delivery_v1` before start.
  - The same program with `future_gas_delivery_v1` still throws `gas_delivery_lifecycle`.
  - Cash rows with `accrued_payment_v1` at a valuation instant ≥ the row's `ValueAt`: no `side_payment_due`; zero `discount` inputs; amount inputs on the `Base` lane only.
  - The same row with `undue_payment_v1` still throws `side_payment_due`.
  - A due `accrued_payment_v1` row whose price resolves as projected throws `side_due_leg_unresolved`.
  - A `BookedSideProgram` pairing `whole_gas_day_delivery_v1` with a crude or LNG family refuses at compile with `physical_lifecycle_unsupported`.

- [ ] **Step 2: Run, confirm failure**

  Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --schedule-program-demand`
  Expected: exception from `StartedGasDelivery` (lifecycle not admitted).

- [ ] **Step 3: Implement.**
  - In `BookedSideProgram` validation, admit both values with their measures, and admit `whole_gas_day_delivery_v1` only for `Document.Family=="gas"`.
  - In `ScheduleProgramDemand`, for `whole_gas_day_delivery_v1`, set `selected[cell]=false` for cells with `EndTicks(cell) <= context.ValuationAt.Ticks`. That reuses the coverage-selection path, so deselected cells read no inputs and contribute nothing.
  - For `accrued_payment_v1`, apply the existing due-row lane logic, with no discount when due.
  - In `Classify`, treat a due `accrued_payment_v1` row like `interval_payment_v1`: require fixed, actual or published prices, giving coverage `OverdueBalance`. Classification of not-due rows is unchanged.

- [ ] **Step 4: Run, confirm pass.** Same command → exits 0. Then run the full default suite, `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release`, and confirm it exits 0 with no changed results.

- [ ] **Step 5: Commit.** `git add` the four files, then `git commit -s -m "Admit whole gas-day delivery and accrued payment lifecycles"`.

### Task 2: Compile gas compositions with the started-delivery lifecycles

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/ScheduleDefinitionCompiler.cs:130-145`
- Modify: `src/OpenEtrm.Api/RetainedScheduleStore.cs:18-30` (`RetainedScheduleTerms.Generate`)
- Modify: `src/OpenEtrm.Api/GasCompositionStore.cs:128` (draft program compile)
- Test: `tests/OpenEtrm.MarketData.Checks/BookedSideProgramChecks.cs`

**Interfaces:**
- Consumes: `ScheduleLifecycles` (Task 1).
- Produces: `enum ScheduleLifecycleSelection { FutureDelivery, StartedPhysicalDelivery }` and `ScheduleDefinitionCompiler.Generate(Side side, CancellationToken ct = default, ScheduleLifecycleSelection lifecycle = ScheduleLifecycleSelection.FutureDelivery)`.
  - `StartedPhysicalDelivery` maps `PhysicalGasSide` to `whole_gas_day_delivery_v1` and dated `FixedSide`/`FloatingSide` cash to `accrued_payment_v1`.
  - For `PhysicalCrudeSide`/`PhysicalLngCargoSide` it throws `physical_lifecycle_unsupported`.

- [ ] **Step 1: Write the failing checks** in `BookedSideProgramChecks`:
  - (a) `Generate(gasSide)` emits `future_gas_delivery_v1` and its program digest equals the digest recorded before this change. Capture it in the test as a literal from a run on the base commit.
  - (b) `Generate(gasSide, lifecycle: StartedPhysicalDelivery)` emits `whole_gas_day_delivery_v1`, and the cash side emits `accrued_payment_v1`.
  - (c) The crude side with `StartedPhysicalDelivery` throws `physical_lifecycle_unsupported`.

- [ ] **Step 2: Run, confirm failure.** Run `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --booked-side-program`. Expected: compile error or exception.

- [ ] **Step 3: Implement** the parameter. `RetainedScheduleTerms.Generate` passes `StartedPhysicalDelivery` when `Instrument is GasCompositionInstrument`; all other instruments pass `FutureDelivery`. Do the same at `GasCompositionStore.cs:128` for draft previews.

- [ ] **Step 4: Run, confirm pass** (same command), plus the default MarketData suite.

- [ ] **Step 5: Commit**: `git commit -s -m "Compile new gas compositions with the started-delivery lifecycle"`.

### Task 3: Lifecycle valuation of retained gas compositions

**Files:**
- Create: `src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs`
- Modify: `src/OpenEtrm.MarketData/Gas/RetainedGasInstrument.cs`
- Modify: `src/OpenEtrm.MarketData/Trading/RetainedProgramInstrument.cs` (expose per-member `BookedSideEvaluation` to subclasses)
- Create: `tests/OpenEtrm.MarketData.Checks/PhysicalLifecycleChecks.cs`
- Modify: `tests/OpenEtrm.MarketData.Checks/Program.cs` (add `--physical-lifecycle`)

**Interfaces:**
- Consumes: Task 1 lifecycles, Task 2 compile option, `IBindingValuationState`, `CashAllocationBalance.Require` (`SidePaymentState.cs`).
- Produces (in `PhysicalLifecycle.cs`, namespace `OpenEtrm.MarketData.Trading`):
  - `public sealed record CashOccurrencePayment(Guid Id, int Slot, int Occurrence, DateTimeOffset PaidAt, DateTimeOffset KnownAt, ExactMoney Amount)`. `Occurrence` is the retained cash row ordinal (`payment/{n}`): a gas day for indexed cash, the single term row for fixed cash.
  - `public sealed class PhysicalCashState : IBindingValuationState`, with constructor `(Guid tradeId, Guid bindingId, Quantity quantity, ImmutableArray<CashOccurrencePayment> payments, string policy)`, where `policy` must equal `Policy = "physical_whole_day_accrual_v1"`. It rejects duplicate ids and empty ids.
  - `public enum DeliveryState { NotStarted = 1, InProgress, Ended }`
  - `public enum SettlementState { Remaining = 1, AccruedUnpaid, PartPaid, Paid, Overpaid }`
  - `public enum LifecycleMeasure { RemainingDeliveryMtm = 1, OutstandingSettlement }`
  - `public sealed record PhysicalLifecycleRow(int Slot, int Occurrence, DateOnly DeliveryFrom, DateOnly DeliveryTo, DeliveryState Delivery, SettlementState Settlement, ExactMoney Accrued, ExactMoney Paid, ExactMoney Outstanding, ExactMoney RemainingPv, ExactMoney OutstandingPv, string QuantityBasis)`
  - `public sealed record PhysicalLifecycleValue(ExactMoney RemainingDeliveryMtm, ExactMoney OutstandingSettlement, ExactMoney Total, ImmutableArray<PhysicalLifecycleRow> Rows)`
  - `RetainedGasInstrument.EvaluateLifecycle(UnitValuationContext context) : PhysicalLifecycleValue`. `Value(context)` returns `EvaluateLifecycle(context).Total` per unit when `context.BindingState is PhysicalCashState`. Without that state it keeps today's behaviour, so an old program still refuses.

Rules the implementation must follow:
- A cash occurrence's **ended fraction** is its count of ended gas days over its total gas days, using the physical member's grid ends. A day ends when `End ≤ ValuationAt`.
  - An indexed row is one day, so the fraction is 0 or 1.
  - A fixed row is accrued pro rata, which is exact because composition daily quantities are equal. Assert equal daily quantities and refuse with `physical_lifecycle_quantity` otherwise.
- The accrued amount is the ended fraction × the row amount. The accrued price must be fixed, actual or published; a projected price refuses with `side_due_leg_unresolved`.
- **Paid** is the sum of visible payments for (slot, occurrence): `PaidAt ≤ ValuationAt` and `KnownAt ≤ snapshot.Knowledge.KnownAt`.
  - Each payment must keep the accrued amount's direction (`CashAllocationBalance` sign rule).
  - Paying an occurrence with no ended day refuses with `payment_state`.
  - When paid exceeds accrued in magnitude, the state is `Overpaid` with a negative outstanding. Do not refuse.
- **Outstanding PV** is outstanding × the row discount factor when the row is not due (the factor is the contribution's PV/amount, both from the same evaluation), and the nominal outstanding when it is due.
- **Remaining PV**:
  - Cash rows: (1 − ended fraction) × the contribution PV.
  - Physical rows: the contribution PV of the cells that are not ended, which Task 1 already selects.
- `RemainingDeliveryMtm` is the signed sum of remaining PV over all members. `OutstandingSettlement` is the signed sum of outstanding PV. Use the member `Direction` and `Ratio` exactly as `RetainedProgramInstrument.Value` does.

- [ ] **Step 1: Write the failing oracle checks** in `PhysicalLifecycleChecks.Run()`. Build the spec's synthetic trade with programs compiled by Task 2:
  - receive 100 per day, Sep 1–Dec 31, 2026;
  - gas day 09:00–09:00 at a fixed −06:00 offset;
  - indexed pay, payment day 10;
  - one composition membership per calendar month;
  - fixings and marks exactly as in the spec table;
  - valuation at 2026-11-06T21:00Z, knowledge at load.
  Assertions with literal values:
  - `UndiscountedOracle`, all discount factors 1: `OutstandingSettlement == -10855.00`, `RemainingDeliveryMtm == 0`, `Total == -10855.00`. The Sep rows are `Paid`; Oct and Nov 1–5 rows are `AccruedUnpaid`; Nov 6 is `InProgress`/`Remaining`; Nov 7–9 are `NotStarted` with fixed price states; Nov 10–30 and Dec are `Remaining`. The physical rows' quantity basis is `contract_estimate_allocation_pending_v1` for ended days.
  - `DiscountedOutstanding`, with payment-date factors Nov 10 = 0.99951 and Dec 10 = 0.99582 supplied by an explicit `ISideDiscounting` fixture: `OutstandingSettlement == -10843.9431` exactly.
  - `PartialPayment`: add a payment of −4,000 on Oct 1–31 (31 day rows, allocations summing to −4,000). Outstanding settlement moves by +4,000 × 0.99951 = +3,998.04, and the affected rows read `PartPaid` or `Paid`.
  - `HiddenPayment` (Review Focus 2): the Sep payment with `KnownAt` after the cutoff leaves September `AccruedUnpaid` and due. Outstanding becomes −19,555.00 undiscounted.
  - `CorrectedFixing` (Review Focus 3): replacing the Oct 1 fixing by a value 0.10 lower after a full Oct 1 payment gives `Overpaid` with outstanding +10.00 on that row.
  - `DueUnpaid` (Review Focus 4): valuation at 2026-11-11T21:00Z with no October payment. October contributes −9,300.00 nominal, and no discount factor is read for its rows.
  - `FrozenUnderShock`: a +1.00 shock on the November daily source changes no accrued row. It changes `RemainingDeliveryMtm` only through Nov 10–30 (21 days × 100 on the physical leg and the cash leg), giving net 0 on the undiscounted fixture.
  - `FixedPrice`: a fixed 3.10 composition, Nov 1–30, at the same valuation instant, gives accrued −1,550.00 for 5 ended days, with the remaining 25 days in remaining MTM.
  - `UnsupportedCommodity`: a crude retained instrument given `PhysicalCashState` refuses with `physical_lifecycle_unsupported`.

- [ ] **Step 2: Run, confirm failure.** Run `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --physical-lifecycle`. Expected: compile error (types missing).

- [ ] **Step 3: Implement** `PhysicalLifecycle.cs` and `RetainedGasInstrument.EvaluateLifecycle` under the rules above. Add `--physical-lifecycle` to `Program.cs` and include `PhysicalLifecycleChecks.Run()` in the default run.

- [ ] **Step 4: Run, confirm pass.** Run `--physical-lifecycle`, then the default suite and `--side-payment-state` (unchanged behaviour).

- [ ] **Step 5: Commit**: `git commit -s -m "Value started gas compositions with accrued and remaining measures"`.

### Task 4: Database admission, payment records and in-term load

Note: the load is not owner-only as first planned; D359 records the dedicated loader role that was built.

**Files:**
- Create: `db/migrations/0215_in_term_gas_lifecycle.sql`
- Modify: `tests/test_migration_identity.py` only if it enumerates migrations explicitly
- Create: `tools/gas_in_term_fixture.py`, `tools/check_gas_in_term.py`
- Modify: `tools/run_preview.py` (add `--gas-in-term-check`, wired like `--gas-indexed-check` at lines 47, 150–158, 255, 805)
- Modify: `tools/check_all.py:93` (add a trading `preview_check` line)

**Interfaces:**
- Consumes: the program shapes from Task 2. The admission functions patched in place, following the `pg_get_functiondef` patch pattern of `0201_shared_curve_price_queries.sql:54-110`:
  - `trading.require_side_program_structure` and the `output_guard` lifecycle check, at `0201` line 242 for fixed cash;
  - `trading.require_gas_index_cash_shape`, at `0210` line 136.
- Produces:
  - **Admission:** new gas composition writes must carry `whole_gas_day_delivery_v1` (slot 0) and `accrued_payment_v1` (cash slots). Rows already stored are never re-validated.
  - **Payments:**
    - `trading.gas_cash_payments(catalog_id, id, trade_id, trade_revision, instrument_id, slot, business_key text, paid_at timestamptz, currency FK, amount numeric, operation_id, actor, publication/knowledge receipt columns as in 0205)`;
    - `trading.gas_cash_payment_allocations(catalog_id, payment_id, occurrence integer, amount numeric)`.
    These carry foreign keys to `trading.gas_trade_revisions` and `trading.gas_component_terms(catalog_id, instrument_id, slot)`. A database check enforces that allocations sum to the payment amount.
  - `security.record_gas_cash_payment(catalog uuid, body jsonb, as_of timestamptz) RETURNS jsonb`: idempotent by `operation_id` and by `(trade_id, slot, business_key, amount, paid_at)`. A changed amount for the same business key is a new revision with its own knowledge time. Granted to the runtime role; there is no HTTP route in this plan.
  - `security.load_gas_composition_in_term(catalog uuid, body jsonb, as_of timestamptz) RETURNS jsonb`: the `book_gas_composition` write path with the `valuation>=start_at` refusal removed. Granted to the owner role only, not runtime. Implement it by extracting the shared body into one internal function with an `allow_started boolean` argument; `book_gas_composition` passes `false`.

- [ ] **Step 1: Write the failing preview check.** `tools/check_gas_in_term.py: run_checks(url, query, manifest, run_worker)` asserts against an owned disposable stock PostgreSQL started by `run_preview.py`:
  - (a) `security.book_gas_composition` for a started term still raises `gas_delivery_lifecycle`.
  - (b) `security.load_gas_composition_in_term` as runtime raises a privilege error; as owner it loads the fixture trade (`tools/gas_in_term_fixture.py`, the spec's synthetic trade and fixings).
  - (c) `record_gas_cash_payment` called twice with the same body returns the same payment id and one row. With the same business key and a different amount, it creates a revision whose `known_from` is later than the original's.
  - (d) An allocation to an occurrence of another trade's instrument raises a foreign-key or ownership error.
  - (e) A pre-existing booked composition row keeps `future_gas_delivery_v1` after migration.

- [ ] **Step 2: Run, confirm failure.**

  Run: `python3 tools/run_preview.py --pg-bin <pg-bin> --dotnet <dotnet> --gas-in-term-check`
  Expected: failure on the missing function.

- [ ] **Step 3: Write `0215_in_term_gas_lifecycle.sql`** following 0205's table/receipt/grant style. Open it with a `-- Current consumer:` header line naming the in-term gas lifecycle (D359).

- [ ] **Step 4: Run, confirm pass**, same command.

- [ ] **Step 5: Commit**: `git commit -s -m "Record gas cash payments and admit in-term compositions"`.

### Task 5: API valuation, Blotter measures, Side inspection and admin load command

**Files:**
- Modify: `src/OpenEtrm.Api/GasCompositionStore.cs` (`Value` at line 123, `InspectSchedule` at 202–216, `Load`)
- Modify: `src/OpenEtrm.Api/GasBlotter.cs:35` and `src/OpenEtrm.Api/GasPositions.cs` (response fields)
- Create: `src/OpenEtrm.Api/GasCashPaymentStore.cs` (reads visible payments under the frame's context and knowledge cutoff → `PhysicalCashState`)
- Create: `src/OpenEtrm.Api/GasInTermLoad.cs` (owner-run command: prepares programs with `RetainedScheduleTerms.Generate`, then calls `security.load_gas_composition_in_term`)
- Modify: `src/OpenEtrm.Api/Program.cs` (dispatch of `admin-load-gas-in-term <request.json>` before the web host starts, mirroring any existing CLI dispatch in that file)
- Test: extend `tools/check_gas_in_term.py`

**Interfaces:**
- Consumes: `PhysicalCashState`, `PhysicalLifecycleValue` (Task 3); SQL functions (Task 4).
- Produces: the gas composition read response gains `measures: { remainingDeliveryMtm, outstandingSettlement, total, label: "Remaining contract value + outstanding settlement", expectedCash: [{ dueAt, amount }] }`. `expectedCash` is undiscounted, grouped by due date, and never summed into `total`. Inspector rows fill the existing fields:
  - `ScheduleInspectionLifecycle.Delivery` = `DeliveryState`;
  - `.Cash` = `SettlementState`;
  - `.Coverage` = `LifecycleMeasure`;
  - `ScheduleInspectionRow.Paid` and `.Outstanding`;
  - `QuantityState` = the basis label.

- [ ] **Step 1: Extend the failing preview check** (`check_gas_in_term.py`):
  - The HTTP gas position read at the 2026-11-06 21:00Z close returns the measures, with `total` equal to the independently recomputed formula from the spec over the fixture's marks and the discount factors read from the frame.
  - The Side inspector page for the cash slot shows September `Paid` (amount and date), October `AccruedUnpaid`, and Nov 7–9 with fixed price states and `Remaining`.
  - The retained risk worker reproduces `total`, and a +1 shock leaves the accrued rows unchanged.
  - A historical context whose knowledge cutoff is before the load reads no trade.
  - Review Focus 5: a portfolio of the fixture plus an old-lifecycle composition after its start reports a position-specific refusal for the old trade, and the portfolio total refuses.
  - For a fixed-price fixture day that also has a D338 final cash determination, the inspector row shows that determination's amount beside the contract-estimate accrual, in a new `ScheduleInspectionRow.FinalDetermination { get; init; }` string field. The accrual and the measures are unchanged by it.

- [ ] **Step 2: Run, confirm failure** with the Task 4 command.

- [ ] **Step 3: Implement.**
  - `GasCompositionStore.Value` attaches `PhysicalCashState`, read by `GasCashPaymentStore` under the frame, only when every member program carries the new lifecycles. Mixed or old programs keep today's path.
  - `InspectSchedule` maps `PhysicalLifecycleRow` into the inspection rows; there is no new reader.
  - `GasInTermLoad` connects with the owner connection the operator supplies, takes a request file, and writes nothing else.

- [ ] **Step 4: Run, confirm pass.** Then run `dotnet run --project tests/OpenEtrm.Api.Checks -c Release` and confirm it exits 0.

- [ ] **Step 5: Commit**: `git commit -s -m "Report remaining and outstanding gas measures and in-term Side states"`.

### Task 6: Present the measures and row states

**Files:**
- Modify: `web/src/gas-trade-view.ts`, `web/src/side-schedule-inspection.ts`
- Test: `web/tests/side-schedule-inspection.test.mjs` (extend the existing test file if one covers the inspector; otherwise create it), plus the browser step in `check_gas_in_term.py`

**Interfaces:**
- Consumes: the response fields from Task 5. No calculation in TypeScript: render only.

- [ ] **Step 1: Write failing tests:**
  - the trade view renders "Remaining delivery MTM", "Outstanding settlement" and "Remaining contract value + outstanding settlement" as separate figures, with expected cash below, labelled "not added";
  - the inspector renders the delivery, settlement and quantity-basis texts ("estimated from contract; allocation pending"), and "Due date" for unpaid rows versus "Paid on" only for recorded payments;
  - the browser check opens the fixture trade and reads those labels.

- [ ] **Step 2: Run, confirm failure.** Run `cd web && npm test`.

- [ ] **Step 3: Implement** the rendering in the two files, using existing amount formatting (`amount-display.ts`).

- [ ] **Step 4: Run, confirm pass.** Run `npm test`, then `python3 tools/run_preview.py --pg-bin <pg-bin> --dotnet <dotnet> --gas-in-term-check --browser-check --browser <browser>`.

- [ ] **Step 5: Commit**: `git commit -s -m "Show in-term gas measures and settlement states"`.

### Task 7: Decision record, documentation and qualification

**Files:**
- Modify: `docs/decisions.md` (append `## D359 — In-term physical delivery: accrued cash and remaining value`)
- Modify: `docs/physical-lifecycle-design.md` (L5 row: gas consumer implemented for the whole-day policy), `docs/indexed-physical-gas-example.md` (status), `docs/physical-gas-side.md` (lifecycle paragraph at lines 215–224), `docs/plan.md`

- [ ] **Step 1:** Write D359: the owner decisions and the designer's calls from the spec, the non-goals, and what remains (allocated basis, invoices, reconciliation, crude/LNG adapters, business-day payment adjustment).
- [ ] **Step 2:** Run `python3 tools/check_all.py --group trading` with the pinned tools and record the actual pass counts in the doc status lines.
- [ ] **Step 3:** Obtain an independent adversarial review of the branch, as AGENTS.md requires for temporal, numerical and authorization changes, covering:
  - knowledge-time visibility of payments;
  - loader-role load privilege (D359);
  - idempotency races;
  - old-program parity;
  - risk freezing.
  Resolve findings before any publication.
- [ ] **Step 4: Commit**: `git commit -s -m "Record D359 in-term physical delivery lifecycle"`.

### Task 8: Pilot release and seed (operational; each step needs the owner's approval in chat)

Not repository work: no files in this repo, and the scripts stay in `~/openetrm-private/`.

- [ ] **Step 1:** With the owner's approval to read the pilot, verify the spec's prerequisites: published HHD/H futures, OTC basis and combined curves; the PZL assignment; the 09:00 Central gas-day clock; fixings coverage; the discount curve at the Nov 6 close; the schema head.
- [ ] **Step 2:** Build the 0215 schema-window package from the latest one (memory: hosted-pilot schema release), rehearse it on the thorp twin, then run the window with approval.
- [ ] **Step 3:** Seed on the twin first, then the pilot:
  - synthetic fixings;
  - `admin-load-gas-in-term` with the spec's trade;
  - `security.record_gas_cash_payment` for September, effective Oct 10.
  Rerun the seed to prove it changes nothing. Check the Blotter and inspector at the latest close against an independent recompute.
