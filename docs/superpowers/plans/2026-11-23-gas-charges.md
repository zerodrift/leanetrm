# Charges on Gas Trades Implementation Plan

Status: implemented locally under D449. The final whole-branch review is pending.

**Goal:** A gas trade can carry a lump sum and a capacity charge: payment entries that own their quantity, accrue on their own days and take recorded payments as any gas payment does.

**Architecture:** An owning entry's Side is the existing fixed payment Side, built with one row for a lump sum and one row per calendar month for a capacity charge. The entry's terms sit in the Instrument's identity. The shared lifecycle gains day states for an owning entry's own days and stops requiring that those days belong to a physical side. No new Side type is added.

**Tech Stack:** C# on .NET 10 (`src/OpenEtrm.MarketData`, `src/OpenEtrm.Api`), PostgreSQL migrations (`db/migrations`), TypeScript presentation (`web/src`), Python check tools (`tools/`), browser checks (`tests/browser`).

**Spec:** [docs/superpowers/specs/2026-11-23-charges-design.md](../specs/2026-11-23-charges-design.md)

The build followed the [Instrument schedule and gas pricing plan](2026-11-23-instrument-schedule-and-gas-pricing.md) and the [exercise rights design](../specs/2026-11-23-exercise-rights-design.md).

## Global Constraints

- No new Side type. No change to transport, storage, park and loan, custody or owned loans.
- Every existing check passes without a changed expected value. An Instrument with no owning entry keeps its identity, byte for byte.
- The shared payment policy compares UTC dates. The payment date is on or after the date of exact row completion (`PaymentDatePolicy.RequireCompletion`). The row becomes overdue on the UTC date after payment day, with all its days ended. Remittance is admitted once any of its days has ended, including before the UTC payment date starts.
- A following payment entry must still have every day it covers among the physical days.
- Saved values, exactly: entry role `charge`; bases `lump_sum` and `capacity`; capacity payment convention `next_month_day_utc_v1` with a day from 1 to 28 (`GasIndexedCash.Payment`).
- A lump sum's quantity is one. A capacity charge has one positive daily quantity and one positive raw rate for its whole term. Booking divides the stated price by the exact quote multiplier without rounding.
- An owning entry names no source and no stage, and takes its clock from a named physical side of the same trade. Both payment and delivery calendar bindings are explicit.
- Owning entries count toward the trade's seven payment Sides and eight Sides in all.
- Charge input refusals from the booking form use `gas_composition_input`. Clock-boundary, calendar and lifecycle refusals keep their established codes.
- All arithmetic is exact. No rounding is introduced.
- Business rules stay in the managed backend. TypeScript presents backend results only.
- Synthetic data only. No customer, vendor or competing product names in code, fixtures, docs or commit messages.
- Write the failing check first in every task. Commit with `git commit -s`. Do not push.
- Database, HTTP and browser checks need a disposable PostgreSQL environment; run them through `tools/run_preview.py` and `tools/check_all.py` with `$PG_BIN`, `$DOTNET` and `$BROWSER` set.
- Migration and decision numbers are the next free ones at build time.

## Review Focus

1. **A lump sum dated on the booking date, under a gas day clock that does not start at UTC midnight.** Its day is the latest one already ended when that date begins, so it is owed at booking and never due with an unended day. (Tasks 1, 3)
2. **A capacity row whose days lie outside the physical term.** Its day states come from its own days. (Task 3)
3. **A payment recorded partway through a capacity month.** It is accepted once one day has ended and shows as part paid against what has accrued. (Tasks 3, 5)
4. **A trade of twice the size with the same stated charge.** The stated charges retain the same amounts. (Tasks 2, 5)
5. **An ordinary payment entry.** Its numbers and identity are unchanged, and it is still refused when a day it covers is not a physical day. (Task 3)

## Reference values used by the checks

Synthetic, no discounting. We pay each charge. Gas days start at 15:00 UTC unless a check says otherwise.

| Entry | At booking | After ten of thirty days | After the month | After payment |
| --- | --- | --- | --- | --- |
| Lump sum of 5,000, payment dated on the booking date | −5,000 owed | −5,000 owed | −5,000 owed | 0 |
| The same lump sum dated a week after booking | −5,000 to come | −5,000 owed | −5,000 owed | 0 |
| Capacity charge, 10,000 a day at 0.02, one thirty-day month | −6,000 to come | −2,000 owed, −4,000 to come | −6,000 owed | 0 |

A capacity charge of 10,000 a day at 0.02 from 20 January 2027 to 10 March 2027 inclusive has three rows: 12 days and 2,400, 28 days and 5,600, 10 days and 2,000.

"Owed" is the realized and unsettled component. "To come" is the unrealized component.

---

## Stage 1: owning entries and their accrual

### Task 1: The rows of a lump sum and of a capacity charge

**Files:**
- Create: `src/OpenEtrm.MarketData/Gas/GasChargeSides.cs`
- Create: `tests/OpenEtrm.MarketData.Checks/GasChargeChecks.cs`
- Modify: `tests/OpenEtrm.MarketData.Checks/Program.cs` (register `--gas-charges`)

**Interfaces:**
- Produces:
  - `public enum ChargeBasis{LumpSum=1,Capacity}`
  - `public static class GasChargeSides` with:
    - `public static DateOnly LumpSumDay(GasDayClock clock,DateTimeOffset paymentAt)`
    - `public static FixedSide LumpSum(Guid id,UnitPrice amount,Quantity unitQuantity,GasDayClock clock,DateTimeOffset paymentAt,SideCalendarPolicy calendars,QuoteSpecification quote)`
    - `public static FixedSide Capacity(Guid id,UnitPrice rate,Quantity unitQuantity,GasDayClock clock,DateOnly from,DateOnly to,int paymentDay,SideCalendarPolicy calendars,QuoteSpecification quote)` with `to` exclusive.
- `LumpSumDay` is the latest operating day whose end, `clock.Boundary(day.AddDays(1))`, is at or before `PaymentDatePolicy.Start(PaymentDatePolicy.Date(paymentAt))`.
- `LumpSum` has one period: that day, allocation one, payment at `paymentAt`, completion at the day's end.
- `Capacity` has one period for each calendar month its operating days fall in: the month's first and last covered day, an allocation equal to its count of days, completion at the end of its last day, and payment at UTC midnight of `paymentDay` in the following month.

- [x] **Step 1: Write the failing checks** in `GasChargeChecks.Run()`:
  - with gas days starting at 15:00 UTC, a payment at any time on 10 January 2027 gives the day 8 January 2027; with gas days starting at 00:00 UTC it gives 9 January 2027;
  - the lump sum's one row covers that day, has quantity one, and passes `PaymentDatePolicy.RequireCompletion`;
  - the capacity charge from 20 January to 10 March 2027 inclusive has rows of 12, 28 and 10 days, paid on the stated day of February, March and April, each passing `RequireCompletion`;
  - a payment day of 0 or 29, an end not after the start, and a zero or negative rate on a capacity charge each refuse with `gas_composition_input`.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-charges`
Expected: build failure naming `GasChargeSides`.

- [x] **Step 3: Implement** with the dated `FixedSide` constructor at `src/OpenEtrm.MarketData/Trading/FinancialSwaps.cs:44` and `FixedSidePeriod` with its `DeliveryCompletionAt`, as `GasCompositionStore.cs:154` builds a fixed gas payment today.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-charges`
Expected: `PASS`.

- [x] **Step 5: Commit**

```bash
git add src/OpenEtrm.MarketData/Gas/GasChargeSides.cs tests/OpenEtrm.MarketData.Checks/GasChargeChecks.cs tests/OpenEtrm.MarketData.Checks/Program.cs
git commit -s -m "Build the rows of a lump sum and of a capacity charge from a fixed payment Side"
```

### Task 2: The owning term in the booking form and the saved Instrument

**Files:**
- Modify: `src/OpenEtrm.MarketData/Gas/GasChargeSides.cs`, `GasCompositionInstrument.cs`, `RetainedGasInstrument.cs`
- Modify: `src/OpenEtrm.MarketData/Trading/RetainedProgramInstrument.cs` (`IRetainedProgramMember`), `DefinitionIdentity.cs`
- Test: `tests/OpenEtrm.MarketData.Checks/GasChargeChecks.cs`

**Interfaces:**
- Produces: `[EconomicDefinition("owned_quantity_v1")] public sealed record OwnedQuantity([property:IdentityField]ChargeBasis Basis,[property:IdentityField]GasDayClock Clock,[property:IdentityField]ExactRatio DailyQuantity);`
- `IRetainedProgramMember` gains `OwnedQuantity? ValuationOwned { get; }`. `RetainedGasMember` gains an identity field `Owned`, a trailing optional constructor parameter. Other member types return null. The identity writer skips it when null.
- `RetainedGasMember` refuses, with `gas_membership`, an owning member that also has a source slot, a quantity policy or a part.
- Produces: `public sealed record GasChargeComponent(string Code,FixedSide Side,ChargeBasis Basis,string ClockSide,ExactDecimal DailyQuantity,ExactDecimal Direction);`
- `GasCompositionInstrument` takes a trailing `ImmutableArray<GasChargeComponent> charges=default`. Each becomes a membership with role `charge`, its direction, and a quantity ratio of `DailyQuantity` over the base quantity. A lump sum's `DailyQuantity` is one.
- The booking form refuses, with `gas_composition_input`: a clock side that is not a physical side of the trade; a Side that is not what `GasChargeSides` builds for those terms and that side's clock; a currency or unit other than the trade's; a daily quantity that is not positive; a lump sum whose daily quantity is not one; more than seven payment and charge entries together; and more than eight Sides in all.

- [x] **Step 1: Write the failing checks.**
  - A trade of base quantity 10,000 with a lump sum has a membership ratio of 1 over 10,000 for it; a capacity charge of 10,000 a day has ratio one; one of 4,000 a day has ratio 2 over 5.
  - The same lump sum on a trade of base quantity 20,000 has ratio 1 over 20,000, so its amount is unchanged.
  - The identity of the existing composition fixture, and of the existing saved gas fixture, equal the literals captured before the change.
  - An owning entry changes the identity; changing only its daily quantity, or only its clock, changes it again.
  - Each refusal above.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-charges`
Expected: build failure naming `OwnedQuantity`.

- [x] **Step 3: Implement.**

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-charges`, then `-- --gas-composition-pricing`, `-- --gas-instrument-schedule` and `-- --compact-retained`
Expected: each prints `PASS`.

- [x] **Step 5: Commit**

```bash
git add src tests
git commit -s -m "Hold a payment entry's own quantity in the gas Instrument's identity"
```

### Task 3: Accrual on an owning entry's own days

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs` (`PrepareGeometry`, `:164-181`; the payment row loop, `:230-275`), `RetainedProgramInstrument.cs`, `PhysicalDeliveryQuantities.cs`
- Modify: `src/OpenEtrm.MarketData/Gas/RetainedGasInstrument.cs` (`PrepareSides`)
- Test: `tests/OpenEtrm.MarketData.Checks/GasChargeChecks.cs`

**Interfaces:**
- `PhysicalLifecycle.PrepareGeometry` gains a second argument: the owning entries, each as its slot, program and `OwnedQuantity`. The geometry gains each owning slot's own day states, worked out from the rows' days and the entry's clock with the same started and ended rule as a physical day.
- In the payment row loop, a row of an owning slot reads its day states from its own slot. The rule that every covered day is a physical day applies only to the other slots.
- An owning row accrues by its count of ended days over its count of days. It carries the contract quantity label.
- Held day quantities are not required for an owning entry, and an owning entry needs no source.
- A composition of physical and owning entries uses held cash lifecycle from booking. The physical programs preserve their existing start requirements.

- [x] **Step 1: Write the failing checks,** through `RetainedGasInstrument.EvaluateLifecycle`, with a physical side and its ordinary fixed payment beside each owning entry:
  - every cell of the reference table, for all three entries, with the ordinary entries' amounts unchanged by the owning entry's presence;
  - the three-row capacity charge, with all rows to come before 20 January, January owed in full and March to come on both February checks, and February accrued for five days at 2027-02-06T15:00:00Z and 24 days at 2027-02-25T15:00:00Z;
  - a capacity charge whose days end a month after the physical term ends still accrues day by day;
  - a recorded payment of 1,000 after ten days of the thirty-day month leaves 1,000 outstanding of the 2,000 accrued; a payment of 7,000 after the month leaves an overpaid balance of 1,000;
  - the derived lump-sum day refuses remittance one microsecond before its end and admits it at its end, before the UTC payment date begins;
  - a day-1 capacity row retains its last unrealized day up to the exact completion instant later on that UTC payment date;
  - a payment known later than the valuation's knowledge cutoff does not count;
  - an ordinary payment entry covering a day that is not a physical day still refuses.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-charges`
Expected: FAIL; the lump sum refuses because its day is not a physical day.

- [x] **Step 3: Implement.** Change nothing about which days have ended for physical sides, when a price is earned, or how recorded payments apply.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-charges`, then `-- --physical-lifecycle`, `-- --payment-date-policy`, `-- --delivery-quantities` and `-- --gas-instrument-schedule`
Expected: each prints `PASS`.

- [x] **Step 5: Commit**

```bash
git add src tests
git commit -s -m "Accrue an owning payment entry on its own days"
```

---

## Stage 2: booking and persistence

### Task 4: The database and the store carry owning entries together

The API passes the request to the database writer, which independently validates admission. Both contracts change together.

**Files:**
- Create: `db/migrations/0397_gas_charge_entries.sql`; modify `db/README.md`
- Modify: `src/OpenEtrm.Api/GasCompositionStore.cs` (`GasCompositionRequest`, `Resolve`, `GasCompositionExecution.BuildInstrument`, `Load`)
- Create: `tools/check_gas_charges.py`
- Modify: `tools/run_preview.py` (flag `--gas-charges-check`), `tools/check_all.py` (the gas suite)
- Test: `tests/OpenEtrm.Api.Checks/GasTicketChecks.cs`, `tools/check_gas_charges.py`

**Interfaces:**
- Produces the record `GasChargeRequest(string Code,string Basis,string Direction,string Price,string? DailyQuantity,string? From,string? To,string? PaymentAt,int? PaymentDay,string ClockSide)` with the init property `SideCalendarPolicyInput CalendarPolicy`. A lump sum states `PaymentAt` and leaves `DailyQuantity`, `From`, `To` and `PaymentDay` null. A capacity charge states those four and leaves `PaymentAt` null.
- `GasCompositionRequest` gains `ImmutableArray<GasChargeRequest>? ChargeSides`, omitted when null on write.
- `Resolve` builds each Side with `GasChargeSides` and the named side's clock, and passes the components to the booking form. `GasCompositionExecution.BuildInstrument` gives each owning member its `Owned` term.
- `trading.gas_component_terms`: the role check admits `charge`; new columns `charge_basis text`, `charge_quantity_n numeric`, `charge_quantity_d numeric`, `clock_slot integer` referencing a physical entry of the same Instrument, and `payment_day integer` with `payment_convention text`. A `charge` row has no source and no stage; every other payment row keeps both.
- `security.write_gas_composition` accepts `chargeSides`, checks independently everything Task 2 refuses, derives a lump sum's day from its payment date and the clock, and raises `gas_composition_input` otherwise.
- The admitted fixed payment program gains the several-row shape, `payment/0/*` to `payment/n/*`, for `charge` rows only, with the in-term lifecycle those rows need.
- The earned test of gas payment recording reads an owning entry's own days and clock. For every other entry it is unchanged.

- [x] **Step 1: Write the failing checks.**
  - `GasTicketChecks.Run()`: a request round-trips `chargeSides` and omits the key when null; a lump sum with a daily quantity and a capacity charge with a payment instant each refuse on resolve.
  - `tools/check_gas_charges.py`, following `tools/check_gas_pricing.py`: a purchase with a lump sum and a three-month capacity charge books, stores one `charge` row each with its basis, quantity, clock slot and payment terms, stores three payment rows for the capacity charge, and replays to its first outcome; a clock side that is a payment, a ninth Side, a capacity charge with a payment day of 29, and a `charge` row with a source or stage are each refused with `gas_composition_input`; a composition without the key stores no `charge` row.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`
Expected: build failure naming `ChargeSides`.

- [x] **Step 3: Write the migration and the store change together.** Follow the house upgrade pattern of the preceding gas migration: one transaction, the advisory lock, and a predecessor pin for every function patched.

- [x] **Step 4: Run to verify it passes**

Run `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`. Run `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check` with each selection in its own disposable invocation: `--gas-charges-check`, `--gas-composition-check --gas-pricing-check`, and `--gas-in-term-check`.
Expected: each run ends with its checks passed.

- [x] **Step 5: Commit**

```bash
git add db src tests tools
git commit -s -m "Book and save a gas trade's lump sums and capacity charges"
```

### Task 5: Every route reports the reference values, and payments apply

**Files:**
- Modify: `tools/check_gas_charges.py`
- Modify, only where a check exposes a gap: `src/OpenEtrm.Api/GasCompositionStore.cs`, `GasBlotter.cs`, `GasPortfolio.cs`, `src/OpenEtrm.MarketData/Trading/ScheduleView.cs`

**Interfaces:**
- The schedule read reports each owning row with its exact days, quantity, accrued, paid, outstanding and unrealized amounts. The gas final billing profile refuses charge slots.

- [x] **Step 1: Write the failing checks.** For a purchase with the two lump sums and the thirty-day capacity charge of the reference table:
  - the preview, the single-trade read, the Blotter and a risk run each report every cell of the table at each of its three valuation times, through the administrator's in-term load where a time is in the term;
  - recording 1,000 against the capacity row after ten days shows it part paid on every route; recording it before a day has ended is refused;
  - a context captured before that payment still reports it unpaid;
  - the same trade booked at twice the size reports the same charge amounts;
  - a risk run shows no price sensitivity from either charge.

- [x] **Step 2: Run to verify failure**

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-charges-check`
Expected: FAIL on the first assertion a route does not yet satisfy; if every assertion passes, record that and continue.

- [x] **Step 3: Close each gap the checks expose.**

- [x] **Step 4: Run to verify it passes**

Run `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check` separately with `--gas-charges-check`, `--gas-in-term-check`, and `--gas-delivery-quantity-check`. Incompatible fixture selections refuse before creating resources.
Expected: all checks pass.

- [x] **Step 5: Commit**

```bash
git add src tools
git commit -s -m "Report gas charges identically in preview, reads, the Blotter and risk"
```

---

## Stage 3: the ticket and the display

### Task 6: The Charge side on the ticket

**Files:**
- Modify: `src/OpenEtrm.Api/GasTicket.cs`
- Test: `tests/OpenEtrm.Api.Checks/GasTicketChecks.cs`

**Interfaces:** a side kind `charge`, pill "Charge", one field per row:
- `charge_basis`, label "Charged on", required choice: `lump_sum` ("A lump sum") or `capacity` ("A daily capacity");
- `charge_direction`, label "Direction", required choice from the existing pay and receive choices;
- `charge_price`, required decimal, labelled "Amount" for a lump sum and "Rate per unit per day" for a capacity charge;
- `charge_daily_quantity`, label "Daily quantity", `charge_start`, label "From", `charge_end`, label "To · exclusive", and `charge_payment_day`, label "Payment day", from the existing payment day choices: shown and required only for `capacity`;
- `charge_payment_at`, label "Payment date": shown and required only for `lump_sum`;
- `charge_clock_side`, label "Gas day of", required choice from the trade's physical sides.
- Use the shared **Schedule calendars** control for the fixed delivery and payment policy bindings. Represent the basis-dependent price label with Side-local `labelByChoice`. A sole physical Side is visibly proposed for `charge_clock_side`.
- A refusal from booking identifies its Side and field. Calendar refusals appear on `calendar_policy` with their existing codes. A typed gas-day boundary refusal identifies `charge_clock_side`.

- [x] **Step 1: Write the failing checks** in `GasTicketChecks.Run()`: a lump sum draft and a capacity draft each map to the request and back; a draft with no charge maps to no `chargeSides` key; a payment day of 29 refuses on `charge_payment_day`; a capacity end not after its start refuses on `charge_end`; an eighth payment or charge side is refused; a trade with one physical side proposes it for the clock.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`
Expected: FAIL, unknown side kind `charge`.

- [x] **Step 3: Implement.**

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket` and `-- --ticket-description`
Expected: both pass.

- [x] **Step 5: Commit**

```bash
git add src/OpenEtrm.Api/GasTicket.cs tests/OpenEtrm.Api.Checks/GasTicketChecks.cs
git commit -s -m "Enter a lump sum or a capacity charge on the gas ticket"
```

### Task 7: Browser checks

**Files:**
- Create: `tests/browser/gas-charges.mjs`
- Modify: shared ticket and retained schedule presentation, the managed schedule view and the owned browser runner. Tests cover exact retained terms, month envelopes and native row-count progress.

- [x] **Step 1: Write the failing browser checks,** using the `gasTicket` driver in `tests/browser/gas-ticket.mjs`: Add side offers "Charge"; choosing "A lump sum" shows "Amount" and "Payment date" and hides the capacity fields; choosing "A daily capacity" shows the four capacity fields and hides "Payment date"; a trade with a lump sum and a capacity charge books; its schedule shows one lump sum row and one row per month, each with what has accrued and what is to come; a payment day of 29 shows the refusal on that field.

- [x] **Step 2: Run to verify failure, implement, then pass**

Build and run the full frontend suite with two Node test workers as shown
in [verification](../../verification.md#independent-gas-charges-d449). Then run
`python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-charges-check --browser-check --browser "$BROWSER"`.
The full web suite passed. The owned browser covered both bases, field refusals, booking, exact terms, accrued and unrealized cash, and completed schedule-row progress.

- [x] **Step 3: Commit**

```bash
git add web tests/browser
git commit -s -m "Check the Charge side of the gas ticket in the browser"
```

### Task 8: Full groups, records and review

**Files:**
- Modify the decision, gas and ticket contracts, delivery plan, implementation and verification records, and the lifecycle extension guide. Align this plan and the gas charge and exercise designs with the built interfaces.

- [x] **Step 1: Run the full groups**

Run `python3 tools/check_all.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --browser "$BROWSER" --group gas`, then `--group trading` and `--group managed` sequentially.
All passed with 18 gas, 20 trading and 81 managed workloads. See
[verification](../../verification.md#independent-gas-charges-d449).

- [x] **Step 2: Update the records.**
  - `docs/decisions.md`: one new decision under the next free number. It states the owning entry, its two bases, the lump sum's day rule, the capacity charge's rows and payment day, accrual on its own days, the new admitted shape, and the seven calls confirmed in the spec. It notes that transport's charges are left to transport's redesign.
  - `docs/physical-gas-side.md`: replace the statement that fixed nominal and capacity charges are outside the profile.
  - `docs/deal-ticket.md`: the Charge side and its refusals.
  - `docs/plan.md`, `docs/implementation.md`, `docs/verification.md`: status and the actual check counts from Step 1.
  - The spec records D449, exact quote and calendar inputs, UTC date and earned-day rules, and the owning schedule presentation.

- [ ] **Step 3: Obtain one independent adversarial review** of the temporal behavior: the lump sum's day, own-day states under a clock that does not start at UTC midnight, due dates, and payment visibility under a knowledge cutoff. Resolve findings and rerun Step 1.

- [x] **Step 4: Commit the local implementation and qualification records.**

```bash
git add docs
git commit -s -m "Record gas charges and their verification"
```
