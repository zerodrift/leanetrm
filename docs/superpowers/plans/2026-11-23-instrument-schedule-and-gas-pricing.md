# The Instrument Schedule and Gas Pricing from Independent Sides Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every sided Instrument gives a schedule and derives its value from it; a gas trade is valued through that schedule on every route; and a gas payment can take a share, a date range or a part of each day's delivered quantity, priced on any named index.

**Architecture:** `SidedInstrument` gains `GenerateSchedule()` and its combining step is sealed to evaluate that schedule. The plain schedule reproduces today's sum, so only gas changes behavior. The saved-program Instrument base overrides the schedule: it works out each entry's day quantities once, hands them to each Side through the existing occurrence context, and each Side still computes its own rows. Gas pricing adds terms to the Instrument's entries and uses existing fixed and floating Sides.

**Tech Stack:** C# on .NET 10 (`src/OpenEtrm.MarketData`, `src/OpenEtrm.Api`), PostgreSQL migrations (`db/migrations`), TypeScript presentation (`web/src`), Python check tools (`tools/`), browser checks (`tests/browser`).

**Spec:** [docs/superpowers/specs/2026-11-23-instrument-schedule-and-gas-pricing-design.md](../specs/2026-11-23-instrument-schedule-and-gas-pricing-design.md)

## Global Constraints

- Futures contracts and exchange-traded futures options get no schedule method and are not touched.
- Only gas moves onto an overriding schedule. Crude, LNG, power, certificate, storage, transport, park and loan and swap valuation keep their numbers.
- Every existing check passes without a changed expected value, with one exception: checks that book the retired strict named daily index form move to `named_index_daily_v1`, and their assertions of the refusal `index_fixing_missing` are replaced by a forecast-labelled row.
- The identity of every Instrument that carries no quantity policy and no part is byte-identical before and after.
- A Side never reads another Side. The Instrument never runs a Side's saved program; each Side computes its own rows.
- Saved values, exactly: quantity policy `delivery_schedule_v1` (D445, unchanged); part kinds `band` and `shortfall`, with whole as the absent value; index payment conventions `point_publications_v1` (unchanged), `named_index_daily_v1` and `named_index_monthly_v1`. A new booking with `daily_published_index_v1` refuses with `gas_index_terms`.
- Limits: one to four physical Sides, at most seven payment Sides, at most eight Sides in all. Shared schedule staging is not changed.
- A part other than whole needs an independent physical source and a composition with no fuel link.
- A trade with a part is valued with held day quantities or refuses with `physical_lifecycle_quantity`.
- Refusal codes reuse the existing ones: `gas_composition_input` for the booking form, `physical_lifecycle_quantity` for day quantities, `gas_index_terms` and `gas_index_coverage` for index payments, `gas_billing_profile` in the database and `gas_billing_input` in the API for billing.
- All arithmetic is exact (`ExactQuantity`, `ExactMoney`, `ExactRatio`, `SignedRatio`). No rounding is introduced.
- Business rules stay in the managed backend. TypeScript presents backend results only.
- Synthetic data only. No customer, vendor or competing product names in code, fixtures, docs or commit messages.
- Write the failing check first in every task. Commit with `git commit -s`. Do not push.
- Database, HTTP and browser checks need a disposable PostgreSQL environment; run them through `tools/run_preview.py` and `tools/check_all.py` with `$PG_BIN`, `$DOTNET` and `$BROWSER` set, as the existing gas checks do.
- Work on a fresh branch cut from a base that contains D445 and migration 0390. Migration and decision numbers below are the next free ones at the time of writing; take the next free ones at build time and use them consistently.

## Review Focus

1. **A trade with a part and no day quantities.** It refuses. It never values at fraction 1. (Tasks 5, 6)
2. **A band level equal to the day's quantity, and a part that falls to zero after a payment was recorded.** The row is zero and the recorded payment stays as an overpaid balance. (Tasks 3, 5)
3. **A period-total trade whose daily contract quantity is a repeating fraction, with band levels.** Every quantity stays exact. (Task 3)
4. **A payment over part of the term when a record exists for a day outside its dates.** That day does not enter the payment. (Tasks 5, 10)
5. **A monthly named index whose price is published while its month is in delivery.** Days already ended become earned on it; days not ended stay unrealized; before publication every day is a labelled forecast. (Tasks 5, 10)

## Worked examples used by the checks

Both are synthetic, with no discounting. We receive 10,000 per gas day for three gas days D1, D2, D3. The entries are:

| Entry | Side | Direction | Part |
| --- | --- | --- | --- |
| 1 | Physical delivery, policy `delivery_schedule_v1` | Receive | none |
| 2 | Index payment, monthly index | Pay | band 0 to 8,000 |
| 3 | Index payment, daily index, spread 0.05 | Pay | band from 8,000, open |
| 4 | Index payment, daily index | Receive | shortfall below 10,000 |
| 5 | Index payment, monthly index | Pay | shortfall below 10,000 |

**Example A, no day ended.** Point value 3.10, daily index forecast 3.20, monthly index forecast 3.00. D1 has no record, D2 is scheduled 9,000, D3 is allocated provisional 7,000.

| | Entry 1 | Entry 2 | Entry 3 | Entry 4 | Entry 5 | Total |
| --- | --- | --- | --- | --- | --- | --- |
| D1 | 31,000 | −24,000 | −6,500 | 0 | 0 | 500 |
| D2 | 27,900 | −24,000 | −3,250 | 3,200 | −3,000 | 850 |
| D3 | 21,700 | −21,000 | 0 | 9,600 | −9,000 | 1,300 |
| Sum | 80,600 | −69,000 | −9,750 | 12,800 | −12,000 | 2,650 |

**Example B, D1 and D2 ended.** Final allocations D1 9,000 and D2 7,000. Published daily index D1 3.30 and D2 3.10. Published monthly index 3.00. D3 has not started, has no record, and uses the forecasts of example A. No payment is due.

| | Entry 1 | Entry 2 | Entry 3 | Entry 4 | Entry 5 |
| --- | --- | --- | --- | --- | --- |
| Accrued, D1 | 0 | −24,000 | −3,350 | 3,300 | −3,000 |
| Accrued, D2 | 0 | −21,000 | 0 | 9,300 | −9,000 |
| Unrealized, D3 | 31,000 | −24,000 | −6,500 | 0 | 0 |

Realized and unsettled is −47,750, unrealized is 500 and the total is −47,250.

---

## Stage 1: the contract

### Task 1: The Instrument schedule and the plain schedule on the sided base

**Files:**
- Create: `src/OpenEtrm.MarketData/Trading/InstrumentSchedule.cs`
- Modify: `src/OpenEtrm.MarketData/Trading/Instruments.cs` (`SidedInstrument`, `:151-214`)
- Create: `tests/OpenEtrm.MarketData.Checks/InstrumentScheduleChecks.cs`
- Modify: `tests/OpenEtrm.MarketData.Checks/Program.cs` (register `--instrument-schedule`)

**Interfaces:**
- Produces, in `InstrumentSchedule.cs`:
  - `public sealed record InstrumentScheduleEntry(int Slot,SideMembership Membership)` with `public SideSchedule Schedule=>Membership.Side.GenerateSchedule();`
  - `public readonly record struct InstrumentScheduleRow(int Slot,ScheduleRow Row);`
  - `public class InstrumentSchedule` with `public SidedInstrument Instrument { get; }`, `public ImmutableArray<InstrumentScheduleEntry> Entries { get; }`, `public IEnumerable<InstrumentScheduleRow> Rows { get; }`, `public virtual InstrumentValuation Evaluate(SideValuationContext context)` and a `protected internal` constructor taking the Instrument.
- Produces, on `SidedInstrument`: `public virtual InstrumentSchedule GenerateSchedule()`. It returns one cached instance per Instrument instance.
- Changes `SidedInstrument.PV(SideValuationContext)` to `GenerateSchedule().Evaluate(context)`. It is not sealed until Task 2.

- [x] **Step 1: Write the failing checks** in `InstrumentScheduleChecks.Run()`. Build one Instrument of every sided class, reusing the fixtures `InstrumentSideAlignmentChecks` and `SideModelOracleChecks` already build and adding a minimal fixture for any class they do not cover. For each, assert:
  - `GenerateSchedule().Entries` has one entry per membership, in order, with the same Side, coefficient and quantity ratio;
  - `GenerateSchedule().Rows.Count()` equals the sum of `side.GenerateSchedule().Rows.Count` over its Sides, and every row carries its entry's slot;
  - `ReferenceEquals(instrument.GenerateSchedule(),instrument.GenerateSchedule())`;
  - `instrument.GenerateSchedule().Evaluate(context).PresentValue` equals the sum of `side.PV(context).PresentValue.Scale(ratio).Scale(coefficient)` computed in the check;
  - two Instruments sharing one Side object, valued in one context, raise `context.EvaluatedSides` by one for that Side.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --instrument-schedule`
Expected: build failure naming `GenerateSchedule`.

- [x] **Step 3: Implement.** The plain `Evaluate` is the body `SidedInstrument.PV` has today: `RequireSideComposition()`, then `new InstrumentValuation(instrument,context,[..Sides.Select(m=>context.Evaluate(m.Side))])`. It must not call `Side.GenerateSchedule()`; only `Rows` and `InstrumentScheduleEntry.Schedule` do, on request. Cache the schedule in a `ConditionalWeakTable`, as `RetainedProgramSide` caches its schedule.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --instrument-schedule`, then `-- --instrument-sides`, `-- --independent-sides` and `-- --side-model-oracle`
Expected: each prints `PASS`.

- [x] **Step 5: Commit**

```bash
git add src/OpenEtrm.MarketData/Trading/InstrumentSchedule.cs src/OpenEtrm.MarketData/Trading/Instruments.cs tests/OpenEtrm.MarketData.Checks/InstrumentScheduleChecks.cs tests/OpenEtrm.MarketData.Checks/Program.cs
git commit -s -m "Give every sided Instrument a schedule and value it through that schedule"
```

### Task 2: Conditions, the sealed combining step, and inspection on the valuation

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/Instruments.cs`, `InstrumentSchedule.cs`, `PaymentSchedules.cs` (`InstrumentValuation`, `:546`)
- Modify, removing each `PV(SideValuationContext)` override: `Trading/FinancialSwaps.cs:323`, `MonthlySwaps.cs:30`, `ScheduledSwaps.cs:29`, `CrossCommodityScheduledSwap.cs:35`, `ForwardContract.cs:22`, `RecForwardInstrument.cs:34` and `:71`, `RetainedOperatingInstrument.cs:52`, `RetainedProgramInstrument.cs:83`, `Gas/GasStorageInstrument.cs:73`, `Gas/IndexedPhysicalGasPurchase.cs:54`
- Modify: `src/OpenEtrm.MarketData/Trading/SideScheduleSnapshot.cs:99-108`, `src/OpenEtrm.Api/BookedScheduleSources.cs:45`
- Test: `tests/OpenEtrm.MarketData.Checks/InstrumentScheduleChecks.cs`

**Interfaces:**
- Produces, on `SidedInstrument`:
  - `public sealed override InstrumentValuation PV(SideValuationContext context)=>GenerateSchedule().Evaluate(context);`
  - `protected internal virtual void RequireBeforeValuation(SideValuationContext context){}`
  - `protected internal virtual void RequireAfterValuation(InstrumentValuation value){}`
- `InstrumentSchedule.Evaluate` calls the first before combining and the second on the result.
- Produces: `internal class RetainedInstrumentSchedule<TMember>:InstrumentSchedule`, returned by `RetainedProgramInstrument<TMember>.GenerateSchedule()`. Its `Evaluate` holds the body `RetainedProgramInstrument.PV` has today, both branches, unchanged.
- Produces: `public ImmutableArray<SideScheduleSnapshot> InstrumentValuation.InspectSchedules(ImmutableArray<SideScheduleIdentity> schedules,ExactDecimal scale,Func<DateOnly,DateTimeOffset> boundary)`, replacing the extension class `InstrumentScheduleInspection`.

- [x] **Step 1: Write the failing checks.** For each class that loses an override, assert that the refusal it gives today is unchanged in code and message:
  - the five swap and forward classes with a discounting policy that does not match their valuation;
  - `RecForwardInstrument`, `RetainedRecForwardInstrument`, `RetainedOperatingInstrument` and `GasStorageInstrument` once delivery has started;
  - `IndexedPhysicalGasPurchase.PV(context)`;
  - `RetainedGasInstrument` with a held state whose held factors disagree, and with no held state after delivery started.

  Capture each expected code and message by running the check against the unchanged code first, and write them as literals.

- [x] **Step 2: Run to verify the checks pass on the unchanged code, then seal**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --instrument-schedule`
Expected: `PASS`. Then add `sealed`; the build fails naming each remaining override.

- [x] **Step 3: Implement.** Move each check-only override into `RequireBeforeValuation` or `RequireAfterValuation`. `IndexedPhysicalGasPurchase` throws its existing exception from `RequireBeforeValuation`. Move the saved-program body into `RetainedInstrumentSchedule.Evaluate`. Move `InspectSchedules` onto `InstrumentValuation` and delete the extension class.

- [x] **Step 4: Run to verify it passes**

Run: `python3 tools/check_all.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --browser "$BROWSER" --group managed`, then `--group trading`
Expected: both groups pass with no changed expected value.

- [x] **Step 5: Commit**

```bash
git add src tests
git commit -s -m "Seal the Instrument's combining step and state its checks as conditions"
```

---

## Stage 2: gas valued through its Instrument

### Task 3: The quantity part and one calculation of an entry's day quantities

**Files:**
- Create: `src/OpenEtrm.MarketData/Trading/EntryQuantities.cs`
- Modify: `src/OpenEtrm.MarketData/Trading/PhysicalDeliveryQuantities.cs` (`DeliveryQuantityFractions`, `:87-106`)
- Test: `tests/OpenEtrm.MarketData.Checks/DeliveryQuantityChecks.cs`

**Interfaces:**
- Produces:
  - `public enum QuantityPartKind{Band=1,Shortfall}`
  - `[EconomicDefinition("quantity_part_v1")] public sealed record QuantityPart([property:IdentityField]QuantityPartKind Kind,[property:IdentityField]ExactRatio Lower,[property:IdentityField]ExactRatio? Upper)` with `public static QuantityPart Band(ExactRatio lower,ExactRatio? upper)`, `public static QuantityPart Shortfall(ExactRatio below)` and `public SignedRatio Take(SignedRatio dayQuantity,SignedRatio baseQuantity)`. Levels are ratios of the base quantity. For a shortfall, `Lower` holds the level and `Upper` is null.
  - `public sealed record EntryDayQuantity(DateOnly Day,SignedRatio Contract,SignedRatio Selected,SignedRatio Taken,string Source,ImmutableArray<DeliveryQuantityEvidence> Evidence);`
  - `public static class EntryQuantities` with `public static ImmutableArray<EntryDayQuantity> For(IEnumerable<HeldDayQuantity> sourceDays,QuantityPart? part,SignedRatio baseQuantity,DateOnly from,DateOnly to)`.
- Changes `DeliveryQuantityFractions.For` to take `IEnumerable<EntryDayQuantity>` and sum `Taken` over `Contract`.

- [x] **Step 1: Write the failing checks** in `DeliveryQuantityChecks.Run()`, with base quantity 10,000:

```csharp
var band=QuantityPart.Band(Ratio(0,1),Ratio(4,5));var above=QuantityPart.Band(Ratio(4,5),null);var shortfall=QuantityPart.Shortfall(Ratio(1,1));
Equal(band.Take(Q(9000),Q(10000)),Q(8000));Equal(band.Take(Q(7000),Q(10000)),Q(7000));Equal(band.Take(Q(8000),Q(10000)),Q(8000));
Equal(above.Take(Q(9000),Q(10000)),Q(1000));Equal(above.Take(Q(8000),Q(10000)),Q(0));Equal(above.Take(Q(7000),Q(10000)),Q(0));
Equal(shortfall.Take(Q(9000),Q(10000)),Q(1000));Equal(shortfall.Take(Q(10000),Q(10000)),Q(0));Equal(shortfall.Take(Q(11000),Q(10000)),Q(0));
// Levels scale with the base quantity.
Equal(band.Take(Q(18000),Q(20000)),Q(16000));
// A repeating daily quantity stays exact: 100,000 over 30 days, band 0 to 3,000 per day.
Equal(QuantityPart.Band(Ratio(0,1),Ratio(3,100)).Take(new SignedRatio(10000,3),Q(100000)),Q(3000));
Refused("quantity_part",()=>QuantityPart.Band(Ratio(4,5),Ratio(4,5)));Refused("quantity_part",()=>QuantityPart.Shortfall(Ratio(-1,1)));
```

  Also assert that `EntryQuantities.For` with a null part returns `Taken==Selected` for every day, drops days outside `from` and `to`, refuses a missing day inside them with `physical_lifecycle_quantity`, and that `DeliveryQuantityFractions.For` over it equals every fraction the existing D445 checks expect.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-quantities`
Expected: build failure naming `QuantityPart`.

- [x] **Step 3: Implement.** `Take` for a band is `min(max(q−L·b,0),(U−L)·b)`, or `max(q−L·b,0)` when `Upper` is null; for a shortfall it is `max(L·b−q,0)`. The constructor refuses a negative level and a band whose lower level is not below its upper.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-quantities` and `-- --physical-lifecycle`
Expected: both print `PASS`.

- [x] **Step 5: Commit**

```bash
git add src/OpenEtrm.MarketData/Trading/EntryQuantities.cs src/OpenEtrm.MarketData/Trading/PhysicalDeliveryQuantities.cs tests/OpenEtrm.MarketData.Checks/DeliveryQuantityChecks.cs
git commit -s -m "Work out an entry's day quantities, with its part, in one place"
```

### Task 4: Entry terms on the saved gas Instrument, inside its identity

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/RetainedProgramInstrument.cs` (`IRetainedProgramMember`, `:8-15`)
- Modify: `src/OpenEtrm.MarketData/Gas/RetainedGasInstrument.cs` (`RetainedGasMember`, `:10-29`; `Declare`, `:61`)
- Modify: `src/OpenEtrm.MarketData/Trading/DefinitionIdentity.cs:68-78`
- Modify: the crude, LNG, operating and certificate member records, to return null for the new members
- Create: `tests/OpenEtrm.MarketData.Checks/GasInstrumentScheduleChecks.cs`; register `--gas-instrument-schedule` in `Program.cs`

**Interfaces:**
- `IRetainedProgramMember` gains `int? ValuationSourceSlot { get; }`, `string? ValuationQuantityPolicy { get; }` and `QuantityPart? ValuationPart { get; }`.
- `RetainedGasMember` gains identity fields `int? SourceSlot`, `string? QuantityPolicy` and `QuantityPart? Part`, as trailing optional constructor parameters defaulting to null.
- Rule: `SourceSlot` is populated only when the Instrument carries a policy or a part on some entry. Otherwise every member has all three null.
- The identity writer skips each of the three when it is null.
- `RetainedGasInstrument.Declare` names the policy from its members, no longer from the held state.

- [x] **Step 1: Write the failing checks.**
  - Build the existing D360 gas fixture (no policy, no part). Run it on the unchanged code, print `instrument.EconomicIdentity.Canonical`, and write that string as a literal. Assert it is unchanged.
  - Assert that giving the physical member `QuantityPolicy="delivery_schedule_v1"` changes the identity, that giving a payment member a part changes it again, and that two Instruments differing only in which physical entry a payment follows have different identities.
  - Assert the constructor refuses a policy on a payment member, a part on a physical member, a part with no `SourceSlot`, and any policy other than `delivery_schedule_v1`, each with `gas_membership`.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-instrument-schedule`
Expected: build failure naming `QuantityPolicy`.

- [x] **Step 3: Implement.**

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-instrument-schedule`, then `-- --compact-retained`, `-- --retained-schedules`, `-- --lng-cargo` and `-- --physical-crude-side`
Expected: each prints `PASS`.

- [x] **Step 5: Commit**

```bash
git add src tests
git commit -s -m "Hold a gas entry's quantity policy and part in its Instrument's identity"
```

### Task 5: The saved-program schedule works out day quantities and hands them to each Side

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/RetainedProgramInstrument.cs` (`RetainedInstrumentSchedule.Evaluate`, `PrepareSides`)
- Modify: `src/OpenEtrm.MarketData/Trading/RetainedProgramSide.cs` (`RetainedProgramContext`, `:10-18`; `Evaluate`, `:138-161`; `InspectSchedule`, `:172-189`)
- Modify: `src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs` (`PhysicalCashState`, `:13-60`; `EvaluateSide`, `:184`; the cash and physical row loops, `:210-275`)
- Modify: `src/OpenEtrm.MarketData/Trading/PhysicalDeliveryQuantities.cs`, `ScheduleFollowedDay.cs`
- Modify: `src/OpenEtrm.MarketData/Gas/RetainedGasInstrument.cs` (`PrepareSides`, `ValidateValuationState`), `Gas/GasHeldQuantities.cs`
- Test: `GasInstrumentScheduleChecks.cs`, `DeliveryQuantityChecks.cs`, `PhysicalLifecycleChecks.cs`, `PaymentDatePolicyChecks.cs`

**Interfaces:**
- `PhysicalCashState` carries facts only. Its quantity argument becomes `ImmutableArray<HeldDayQuantity> ownerDays`: days of physical slots only, each with contract and selected quantity, label and evidence. `QuantityPolicy`, `QuantityMemberships` and `Follows(int)` are removed.
- `RetainedProgramContext` gains `internal ImmutableArray<ImmutableArray<EntryDayQuantity>> EntryDays { get; init; }`, indexed by slot; a default inner array means the entry stands on its own contract quantity.
- `PhysicalLifecycle.EvaluateSide` gains a trailing `ImmutableArray<EntryDayQuantity> days` and uses it where it reads `state.Day(slot,day)` today.
- `RetainedInstrumentSchedule.Evaluate`, when any member has a policy or a part:
  1. requires a `PhysicalCashState`, or refuses with `physical_lifecycle_quantity`;
  2. requires complete days for every physical slot that has a policy or a following payment with a part, and for a slot with no policy requires every label to be `PhysicalLifecycle.ContractQuantity`;
  3. builds each entry's days with `EntryQuantities.For`: a physical entry from its own days with no part, a payment entry from its source's days with its part and its own dates, with the held trade quantity as the base;
  4. gives each Side its occurrence with those days, then applies sign and ratio and sums, as today.
- `GasHeldQuantities.Build` returns days of physical slots only, and builds contract-labelled days for a physical member that has no policy but has a following payment with a part. `GasFollowedMember.PaymentSlots` is removed.
- `ScheduleSelectedQuantity.Read` and `RetainedProgramValuation.InspectSchedule` read `EntryDayQuantity` and report `Taken` as the quantity used.

- [x] **Step 1: Write the failing checks** in `GasInstrumentScheduleChecks.Run()`, building the Sides in the managed layer (delivery-index floating Sides accept a month's contract on a daily row):
  - Example A through `RetainedGasInstrument.EvaluateLifecycle`: every cell of its table, the five entry sums and the total 2,650.
  - Example B: every accrued cell, the unrealized row, realized and unsettled −47,750, unrealized 500, total −47,250.
  - Example B with the monthly index not yet published and forecast at 3.00: entries 2 and 5 are labelled forecast on D1 and D2 and accrue nothing; realized and unsettled is 9,250, unrealized is −56,500 and the total is still −47,250.
  - A one-day index payment whose member ratio carries share 1.05, on 10,000 at 3.20, values at 33,600 and its schedule row shows quantity 10,500.
  - The same Instrument with its physical member's policy removed and no records: every day is example A's D1 row.
  - The Instrument valued with no held state refuses with `physical_lifecycle_quantity`. So does a state missing D2, and a state whose D2 label is scheduled when the member has no policy.
  - A payment covering D2 and D3 only, with a record on D1: D1 does not enter it.
  - Entry 3 with a recorded payment of 3,250 for D2, then D2 re-recorded as 8,000: its row is zero and it is overpaid by 3,250.
  - A trade at twice the size, with the same level ratios and doubled records, gives exactly twice every amount.

  Move the three existing callers of `PhysicalLifecycle.Evaluate(members,state,…)` in `PhysicalLifecycleChecks`, `PaymentDatePolicyChecks` and `DeliveryQuantityChecks` onto `RetainedGasInstrument.EvaluateLifecycle`, without changing an expected value.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-instrument-schedule`
Expected: build failure; `PhysicalCashState` has no constructor that takes owner days.

- [x] **Step 3: Implement.** Keep every lifecycle rule as it is: which days have ended, when a price is earned, how recorded payments apply. Only the source of a row's quantities changes.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-instrument-schedule`, then `-- --delivery-quantities`, `-- --physical-lifecycle`, `-- --payment-date-policy` and `-- --physical-lifecycle-projection`
Expected: each prints `PASS`.

- [x] **Step 5: Commit**

```bash
git add src tests
git commit -s -m "Work out gas day quantities in the Instrument's schedule and hand them to each Side"
```

### Task 6: One route: preview, reads and display go through the Instrument

**Files:**
- Modify: `src/OpenEtrm.Api/GasCompositionStore.cs` (`Execution.Instrument`, `:86`; core `Value`, `:164-261`; inspection, `:300-324`)
- Modify: `src/OpenEtrm.Api/GasDeliveryQuantityStore.cs:33-53`, `GasCashPaymentStore.cs`, `GasPortfolio.cs:44-48`, `GasBlotter.cs:39`
- Modify: `src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs` (remove the member-list `Evaluate`, `:159`), `ScheduleView.cs:132-160`
- Test: `tools/check_gas_delivery_quantity.py`, `tools/check_gas_in_term.py`

**Interfaces:**
- `GasCompositionExecution.Instrument(...)` builds each `RetainedGasMember` with its `SourceSlot` and `QuantityPolicy` from the composition rows, following the population rule of Task 4. `Part` follows in Task 9.
- Core `Value` builds the saved-form `RetainedGasInstrument` for a draft from its compiled programs, exactly as a booked trade's is built, and values it. The per-entry amounts, the lifecycle rows and the displayed quantities all come from that one valuation.
- `GasDeliveryQuantityStore.Build` returns owner days whenever a member has a policy.
- Acceptance by search: `grep -n 'sources.Evaluate\|PhysicalLifecycle.Evaluate(' src/OpenEtrm.Api/GasCompositionStore.cs` returns nothing, and `PhysicalLifecycle` has no public method that takes a list of members.

- [x] **Step 1: Pin the behavior.** In `tools/check_gas_delivery_quantity.py`, for the existing four-day follows-schedule trade, assert that the preview before booking, the single-trade read, the Blotter row and the risk run report the same total and the same per-entry amounts, to the last digit. This passes on the unchanged code and must keep passing.

- [x] **Step 2: Run the acceptance search to list what must go**

Run: `grep -n 'sources.Evaluate\|PhysicalLifecycle.Evaluate(' src/OpenEtrm.Api/GasCompositionStore.cs`
Expected: it lists the store's own program loop and its lifecycle call.

- [x] **Step 3: Implement.** Inspecting one Side reads that entry from the whole Instrument's valuation; it no longer values a one-member list.

- [x] **Step 4: Run to verify it passes**

Run: the search of Step 2, then `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`, then `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-composition-check --gas-in-term-check --gas-delivery-quantity-check --gas-daily-quantity-check --gas-indexed-check --gas-billing-check --gas-amendments-check --browser-check --browser "$BROWSER"`
Expected: the search returns nothing, and every check passes with no changed expected value.

- [x] **Step 5: Commit**

```bash
git add src tests tools
git commit -s -m "Value gas previews and reads through the saved Instrument, as risk does"
```

---

## Stage 3: gas pricing from independent Sides

### Task 7: Share, part and partial term in the booking form

**Files:**
- Modify: `src/OpenEtrm.MarketData/Gas/GasCompositionInstrument.cs`
- Create: `tests/OpenEtrm.MarketData.Checks/GasCompositionPricingChecks.cs`; register `--gas-composition-pricing`

**Interfaces:**
- `GasPhysicalComponent` gains a trailing `string? QuantityPolicy=null`.
- `GasCashComponent` gains trailing `ExactDecimal? Share=null` and `QuantityPart? Part=null`. A null share is refused; callers state it.
- `GasCompositionRole` gains identity fields `ExactDecimal? Share`, `QuantityPart? Part` and `string? QuantityPolicy`, skipped by the identity writer when null. An existing role is built with a null share, so its identity is unchanged; a share is written only when it is not 1 or the composition carries a policy or a part.
- Constants: `MaxCashSides=7`, new `MaxSides=8`.
- A payment's quantity ratio is `Share × Stage(...)`.
- A payment's rows must lie within its source's term, start and end on the source's gas days, and each row's quantity must equal the source's delivery quantity for the days it covers.
- `GasCompositionInstrument` overrides `RequireBeforeValuation` to refuse with `gas_composition_input` when a component carries a policy or a part. The booking form is not a valuation route for such a trade.

- [x] **Step 1: Write the failing checks.**
  - The booking form with a policy, and with a part, refuses `PV`; without either it values as today.
  - Share 1.05 on a 10,000 source gives a payment ratio of 1.05 times the source's stage ratio; share ½ twice gives two halves.
  - A payment over the last two of three days is accepted; one that starts before its source or off a gas day boundary is refused.
  - Four physical and four payment Sides are accepted; one physical and seven payments are accepted; one physical and eight payments, and two physical and seven payments, are refused.
  - A part on a composition with a fuel link, a part on a linked source, a zero or negative share, and a missing share are refused.
  - Every refusal is `gas_composition_input`.
  - The identity of the existing composition fixture is unchanged, by the literal captured before the change.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-composition-pricing`
Expected: build failure naming `Share`.

- [x] **Step 3: Implement.**

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-composition-pricing`, then `-- --physical-gas-side` and `-- --instrument-sides`
Expected: each prints `PASS`.

- [x] **Step 5: Commit**

```bash
git add src/OpenEtrm.MarketData/Gas/GasCompositionInstrument.cs src/OpenEtrm.MarketData/Trading/DefinitionIdentity.cs tests
git commit -s -m "Let a gas payment take a share, a part and part of its source's term"
```

### Task 8: Named index payments, daily and monthly

**Files:**
- Create: `src/OpenEtrm.Api/GasNamedIndexCash.cs`
- Modify: `src/OpenEtrm.Api/GasIndexedCash.cs:21-58`, `GasPointIndexCash.cs`
- Test: `tests/OpenEtrm.Api.Checks/GasTicketChecks.cs`

**Interfaces:**
- `GasIndexedCashRequest.ObservationConvention` accepts `point_publications_v1`, `named_index_daily_v1` and `named_index_monthly_v1`. `daily_published_index_v1` is refused with `gas_index_terms`.
- Produces: `internal static FloatingSide GasNamedIndexCash.Resolve(PreviewData data,PhysicalGasSide physical,GasCashRequest cash,DateOnly from,DateOnly to)`.
  - It is built as `GasPointIndexCash.Resolve` is built, on `FloatingSide.DeliveryIndexImplementation`, with one period per gas day from `from` to `to`.
  - The index is `terms.Index`. Its curve is the one row of `data.Definitions["curves"]` whose settlement target is of kind `index` with that index code, for the physical product's commodity and the assignment's quote. None or more than one refuses with `gas_index_coverage`.
  - Daily: the contract whose delivery is exactly the gas day. Monthly: the contract that starts on day 1 and spans the month containing the gas day.
  - Exactly one contract and one fixing per gas day, or `gas_index_coverage`.
- `GasPointIndexCash.Resolve` gains the same `from` and `to` and builds periods only for those days.
- Share the per-day period construction between the two; do not copy it.

- [x] **Step 1: Write the failing checks** in `GasTicketChecks.Run()`, with the file's existing reference data builders:
  - a named daily index other than the point's resolves to one period per gas day on the delivery-index implementation, each with that day's contract;
  - a named monthly index resolves every gas day of a month to the same monthly contract and fixing;
  - a point with publication periods accepts a named index;
  - `daily_published_index_v1` refuses with `gas_index_terms`;
  - an index with no curve, an index with two curves, a day with no contract and a day with no fixing each refuse with `gas_index_coverage`;
  - `from` and `to` inside the source's term give periods for those days only.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`
Expected: build failure naming `GasNamedIndexCash`.

- [x] **Step 3: Implement.**

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket` and `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --delivery-index`
Expected: both pass.

- [x] **Step 5: Commit**

```bash
git add src/OpenEtrm.Api/GasNamedIndexCash.cs src/OpenEtrm.Api/GasIndexedCash.cs src/OpenEtrm.Api/GasPointIndexCash.cs tests/OpenEtrm.Api.Checks/GasTicketChecks.cs
git commit -s -m "Price a gas index payment on any named index, published daily or monthly"
```

### Task 9: The database and the store carry the terms together

The API passes its request body to the database writer, which refuses unknown keys. The two therefore change in one task.

**Files:**
- Create: `db/migrations/0391_gas_pricing_entries.sql`
- Modify: `db/README.md` (the "Recent schema groups" list)
- Modify: `src/OpenEtrm.Api/GasCompositionStore.cs` (`GasCashRequest`, `:26`; `GasCompositionRow`, `:35`; `Execution.Instrument`, `:86`; `Resolve`, `:117-160`; `Load`)
- Modify: `src/OpenEtrm.Api/GasDeliveryQuantityStore.cs:33-53`, `GasCashBillingStore.cs`, `src/OpenEtrm.MarketData/Gas/GasCashBilling.cs`
- Create: `tools/check_gas_pricing.py`
- Modify: `tools/run_preview.py` (flag `--gas-pricing-check` beside `--gas-delivery-quantity-check`, with its implication and dispatch), `tools/check_all.py` (add the flag to the gas suite)
- Modify: `tools/check_gas_indexed.py`, `tools/check_gas_index_upgrade.py` (book `named_index_daily_v1`), and every composition body in the Python checks and fixtures (state `"share":"1"`)
- Test: `tests/OpenEtrm.Api.Checks/GasTicketChecks.cs`, `tools/check_gas_pricing.py`

**Interfaces:**
- Produces: `public sealed record GasQuantityPartRequest(string Kind,string Lower,string? Upper)`. `Kind` is `band` or `shortfall`. Levels are daily quantities in the trade's unit, as decimal text.
- `GasCashRequest` gains `string Share` (required) and, each omitted when null on write, `GasQuantityPartRequest? Part`, `string? DeliveryFrom`, `string? DeliveryTo`. Absent dates mean the source's term.
- `GasCompositionRow` gains trailing `ExactDecimal? Share=null` and `QuantityPart? Part=null`.
- `Resolve` converts each level to a ratio of the composition's base quantity, builds a fixed payment's Side over the payment's own dates, calls `GasNamedIndexCash.Resolve` or `GasPointIndexCash.Resolve` with those dates, and passes share and part to the booking form.
- `Execution.Instrument` gives each `RetainedGasMember` its `Part`. `GasDeliveryQuantityStore.Build` returns owner days whenever a member has a policy or a payment member has a part.
- Columns on `trading.gas_component_terms`: `share_n numeric`, `share_d numeric`, `part_kind text` (null, `'band'` or `'shortfall'`), `part_lower_n numeric`, `part_lower_d numeric`, `part_upper_n numeric`, `part_upper_d numeric`. Existing payment rows are set to share 1 over 1. Checks: a share only on a payment row, positive and reduced; a part only on a payment row; a shortfall has no upper level; a band's lower level is below its upper.
- `security.write_gas_composition` accepts the four payment keys `share`, `part`, `deliveryFrom` and `deliveryTo`. It admits one to four physical and up to seven payment entries with at most eight in all. It stores the share and each level as reduced ratios, the level over the composition's base quantity, computed independently. It checks independently that the payment's ratio is its share times its source's stage ratio, that its dates lie within its source's term, and that a part has an independent source and no fuel link in the composition. Violations raise `gas_composition_input`.
- `trading.gas_index_cash_terms.observation_convention` admits `named_index_daily_v1` and `named_index_monthly_v1`, each with an index code.
- `trading.require_gas_index_cash_shape` admits the two named forms on the delivery-index implementation: the curve is the one whose settlement target is that index, the days are the payment's own, the contract is the day's or the containing month's, and each day has one fixing. It keeps one row layout per gas day. New `daily_published_index_v1` terms raise `gas_index_terms`.
- `security.record_gas_billing` raises `gas_billing_profile`, and the API billing path raises `gas_billing_input`, for an entry with a share other than 1, a part, or dates shorter than its source's term.
- Produces: `tools/check_gas_pricing.py` with `run_checks(url, query, manifest, run_worker)`.

- [x] **Step 1: Write the failing checks.**
  - In `GasTicketChecks.Run()`: a `GasCashRequest` round-trips `share`, `part`, `deliveryFrom` and `deliveryTo`, and omits the three optional keys when null.
  - In `tools/check_gas_pricing.py`, following `tools/check_gas_delivery_quantity.py`:
    - the five-entry trade of the worked examples books, stores each share and each part as its reduced ratio, and replays to its first outcome;
    - a body with eight payments, a part beside a fuel link, a payment starting before its source, and a band with equal levels are each refused with `gas_composition_input`;
    - a named monthly index books; `daily_published_index_v1` is refused with `gas_index_terms`; an index with no curve is refused with `gas_index_coverage`;
    - a composition body without `share` on a payment is refused, and the same body with `"share":"1"` stores 1 over 1;
    - recording a bill for entry 2 is refused, and for a plain fixed payment on the same trade shape is accepted.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`
Expected: build failure naming `Share`.

- [x] **Step 3: Write the migration and the store change.** For the migration follow the house upgrade pattern of `db/migrations/0390_gas_delivery_quantity_valuation.sql:1-30`: one transaction, `SET LOCAL search_path=pg_catalog`, the advisory lock, and a predecessor pin (definition hash, owner, ACL, config) for every function patched. Compute each hash in the disposable database from the definition the preceding migrations leave. Patch by text replacement, asserting each replaced text occurs exactly once. Add the schema group line to `db/README.md`.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`, then `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-pricing-check`, then `--gas-composition-check`, `--gas-indexed-check`, `--gas-billing-check`, `--gas-in-term-check` and `--gas-delivery-quantity-check`
Expected: each run ends with its checks passed.

- [x] **Step 5: Commit**

```bash
git add db src tests tools
git commit -s -m "Save a gas payment's share, part and dates, admit named indexes and refuse their billing"
```

### Task 10: Every route reports the worked examples

**Files:**
- Modify: `tools/check_gas_pricing.py`
- Modify, only where a check below exposes a gap: `src/OpenEtrm.Api/GasCompositionStore.cs`, `GasBlotter.cs`, `GasPortfolio.cs`, `src/OpenEtrm.MarketData/Trading/ScheduleView.cs`

**Interfaces:**
- Consumes: everything through Task 9. Produces no new interface.
- The schedule read reports, on every payment row, the quantity used, the part kind (`band_v1`, `shortfall_v1`, or none), the source quantity and its label.

- [x] **Step 1: Write the failing checks** in `tools/check_gas_pricing.py`:
  - Example A through the preview, the single-trade read, the Blotter and a risk run: each reports every entry sum and the total 2,650.
  - Example B through the administrator's in-term load, with records and published prices: each route reports realized and unsettled −47,750, unrealized 500 and total −47,250.
  - The schedule read reports, for entry 3 on D2, quantity 1,000, part `band_v1`, source quantity 9,000 and label `scheduled`.
  - A payment with share 1.05 on 10,000 at 3.20 values at 33,600 and its schedule row shows quantity 10,500.
  - A payment covering D2 and D3 only ignores a record on D1.
  - A context captured before the D2 record still reports D2 on contract quantity.
  - A risk run on the booked trade before any record succeeds on contract-labelled days. The same trade with its physical side on contract quantity gives example A's D1 row on every day, on every route.

- [x] **Step 2: Run to verify failure**

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-pricing-check`
Expected: FAIL on the first assertion that a route does not yet satisfy; if every assertion passes, record that and continue.

- [x] **Step 3: Close each gap the checks expose.**

- [x] **Step 4: Run to verify it passes**

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-pricing-check --gas-in-term-check --gas-delivery-quantity-check`
Expected: all checks pass.

- [x] **Step 5: Commit**

```bash
git add src tools
git commit -s -m "Report a gas trade's parts identically in preview, reads, the Blotter and risk"
```

### Task 11: The ticket fields

**Files:**
- Modify: `src/OpenEtrm.Api/GasTicket.cs` (`CashKind`, `:96-110`; `IndexedCashKind`, `:112-121`; `Request`, `Draft`, `ChoiceSet`)
- Test: `tests/OpenEtrm.Api.Checks/GasTicketChecks.cs`

**Interfaces:** for each payment kind `cash`, `fee` and `floating`, with `{kind}` as the prefix:
- `{kind}_share`, label "Share of quantity", decimal, required, starting value `1`.
- `{kind}_part`, label "Quantity part", required choice from `quantity_parts`: `whole` ("Whole quantity"), `up_to` ("Up to a daily level"), `above` ("Above a daily level"), `between` ("Between two daily levels"), `shortfall` ("Shortfall below a daily level"). Starting value `whole`.
- `{kind}_part_lower` and `{kind}_part_upper`, labels "Lower daily level" and "Upper daily level", decimal, each shown and required only for the choices that use it: `up_to` uses upper, `above` uses lower, `between` uses both, `shortfall` uses lower.
- `{kind}_start` and `{kind}_end` become editable dates that start at the source's term.
- `floating_pricing`, label "Pricing", required choice from `index_pricing`: `point_publications` ("Point publications"), `named_daily` ("Named index, daily"), `named_monthly` ("Named index, monthly"). `floating_index` is required for the two named choices and hidden otherwise. The ticket proposes, visibly and never silently: `point_publications` when the source's delivery point has publication periods; otherwise `named_daily` with the point's assigned index in `floating_index`.
- Mapping: `up_to` is a band from 0 to upper; `above` is a band from lower with no upper; `between` is a band; `shortfall` is a shortfall; `whole` is a null part.
- A refusal from booking is raised on the field that caused it.

- [x] **Step 1: Write the failing checks** in `GasTicketChecks.Run()`:
  - each of the five part choices maps to its request part and back;
  - a draft opened from terms stored before these fields existed shows share `1`, part `whole` and the source's dates;
  - a part on a draft with a linked side refuses on `{kind}_part`; an upper level not above the lower refuses on `{kind}_part_upper`; dates outside the source's refuse on `{kind}_start` or `{kind}_end`;
  - the three pricing choices map to the three conventions, and a draft stored with `daily_published_index_v1` reopens read-only with its stored terms;
  - the pricing proposal is `point_publications` for a point with publication periods and `named_daily` with the point's index otherwise;
  - a draft may add a seventh payment and is refused an eighth, and with two physical sides is refused a seventh.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`
Expected: FAIL, unknown field `cash_share`.

- [x] **Step 3: Implement.** The ticket stays one field per row.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket` and `-- --ticket-description`
Expected: both pass.

- [x] **Step 5: Commit**

```bash
git add src/OpenEtrm.Api/GasTicket.cs tests/OpenEtrm.Api.Checks/GasTicketChecks.cs
git commit -s -m "Enter a gas payment's share, part, dates and index pricing on the ticket"
```

### Task 12: Presentation and browser checks

**Files:**
- Modify: `web/src/lifecycle-text.ts`, `web/tests/lifecycle-text.test.mjs`
- Create: `tests/browser/gas-pricing.mjs`
- Modify: `tests/browser/gas-indexed.mjs` (book the named daily form)

**Interfaces:**
- Consumes: the part labels the backend reports on a schedule row: `band_v1` and `shortfall_v1`.

- [x] **Step 1: Write the failing web test**

```js
assert.equal(quantityBasisText('band_v1'),'part of the day quantity');
assert.equal(quantityBasisText('shortfall_v1'),'shortfall of the day quantity');
```

- [x] **Step 2: Write the failing browser checks** in `tests/browser/gas-pricing.mjs`, using the `gasTicket` driver in `tests/browser/gas-ticket.mjs`:
  - a payment side shows "Share of quantity" as 1 and "Quantity part" as "Whole quantity";
  - choosing "Between two daily levels" shows both level fields, and choosing "Whole quantity" hides them;
  - the five-entry trade books, and its schedule shows 1,000 for entry 3 on D2 with the source quantity 9,000 labelled scheduled;
  - a part beside a linked side shows the refusal on the part field;
  - an index payment offers the three pricing choices and books on a named monthly index.

- [x] **Step 3: Run to verify failure, implement, then pass**

Run: `npm test --prefix web`, then `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-pricing-check --gas-indexed-check --browser-check --browser "$BROWSER"`
Expected: all web tests pass; the five pricing browser checks and the indexed browser checks pass.

- [x] **Step 4: Commit**

```bash
git add web tests/browser
git commit -s -m "Show a gas payment's part and check the pricing ticket in the browser"
```

### Task 13: Full groups, benchmark, records and review

**Files:**
- Modify: `docs/decisions.md`, `docs/architecture.md`, `docs/instrument-contracts.md`, `docs/physical-gas-side.md`, `docs/physical-gas-daily-quantities.md`, `docs/gas-cash-billing.md`, `docs/deal-ticket.md`, `docs/plan.md`, `docs/implementation.md`, `docs/verification.md`
- Modify: the spec's status line

- [ ] **Step 1: Run the full groups**

Run: `python3 tools/check_all.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --browser "$BROWSER" --group gas`, then `--group trading` and `--group managed`
Expected: every group passes.

- [x] **Step 2: Run the benchmark.** Value one fixed synthetic portfolio on the base of this branch and on its tip. Record the workload, unique contracts, scenarios, evaluated Side work, memory and environment for both. The evaluated Side work of plain Instruments must be equal.

- [ ] **Step 3: Update the records.**
  - `docs/decisions.md`: two new decisions under the next free numbers. The first states the Instrument schedule, the sealed combining step, conditions, the line, and that D181 stands. The second states share, part and partial-term entries, the three index payment forms and their one fixing rule, the retired strict form, the billing refusal, and the twelve calls confirmed in the spec. Note in D445 that its policy is now an entry term in identity.
  - `docs/architecture.md`: replace the note that an Instrument-level `GenerateSchedule()` is not implemented.
  - `docs/instrument-contracts.md`: the schedule contract and the conditions.
  - The gas and ticket documents: the new terms, fields, refusals and limits.
  - `docs/plan.md`, `docs/implementation.md`, `docs/verification.md`: status, the actual check counts from Step 1 and the benchmark from Step 2.
  - The spec's status line: built, with the decision numbers.

- [ ] **Step 4: Obtain one independent adversarial review** of the numerical behavior (exact parts, both worked examples, the paid-cash cases), the temporal behavior (knowledge cutoff of quantity records on every route) and scenario reuse (shared Sides, unchanged work for plain Instruments). Resolve findings and rerun Step 1.

- [ ] **Step 5: Commit**

```bash
git add docs
git commit -s -m "Record the Instrument schedule, gas pricing entries and their verification"
```
