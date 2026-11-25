# Imbalance Cash-out on a Gas Trade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** A gas trade can state an imbalance cash-out on a physical side: two index payments, one received and one paid, whose quantity for each gas day comes from the balance the operator reports for that side's service accounts.

**Architecture:** Each of the two entries is an ordinary following payment entry with a fixed direction and the existing index payment Side. In place of its side's delivery quantity it follows the side's reported balance, taking the positive or the negative part. One pure function turns statements into day quantities under a stated basis. The Instrument's schedule supplies those quantities to each Side through the mechanism that already supplies delivery quantities. A day with no eligible statement is its own state.

**Tech Stack:** C# on .NET 10 (`src/OpenEtrm.MarketData`, `src/OpenEtrm.Api`), PostgreSQL migrations (`db/migrations`), TypeScript presentation (`web/src`), Python check tools (`tools/`), browser checks (`tests/browser`).

**Spec:** [docs/superpowers/specs/2026-11-24-imbalance-cashout-design.md](../specs/2026-11-24-imbalance-cashout-design.md)

**Depends on:** [the Instrument schedule and gas pricing plan](2026-11-23-instrument-schedule-and-gas-pricing.md), and must be built on its branch. Tasks 1 to 3 need that plan complete through its Task 8. Tasks 4 and 5 need it through its Task 10. Tasks 6 to 8 need it through its Task 11. The exercise rights and gas charges plans change several of the same files; build these plans one after another, not side by side. Where the built code uses another name than this plan, use the built name.

## Global Constraints

- No new Side type and no signed quantity. Each entry has one fixed direction and a nonnegative day quantity.
- Every existing check passes without a changed expected value. A trade without an imbalance cash-out keeps its identity, byte for byte.
- Nothing is derived from scheduled or allocated quantities, from an allocation residual, or from another trade.
- An unassessed day is never shown or counted as zero.
- Saved values, exactly: entry role `imbalance_cashout`; balance sides `owed_to_us` and `owed_by_us`; profile `daily_index_cashout_v1`; statement bases `daily_imbalance_v1` and `month_to_date_balance_v1`; account netting `net_accounts_v1` and `each_account_v1`; statement policies `final_statement_balance_v1` and `reported_statement_balance_v1` (the existing `GasImbalanceTrading` constants); day states `assessed`, `unassessed` and `not_applicable`; index convention `named_index_daily_v1`; payment convention `next_month_day_utc_v1` with a day from 1 to 28.
- The entry owed to us is received. The entry owed by us is paid.
- A statement balance is an absolute quantity. It does not scale with the trade's size.
- A day's current balance is `GasImbalanceTrading.Balance`: the reported balance plus accepted transfers not yet included in it.
- Refusals: `gas_composition_input` in the booking form; `gas_imbalance_transfer` for a month-to-date month that has an accepted transfer; a missing read authority is a refusal with SQLSTATE `42501`, never an empty result.
- A statement enters a valuation only when it is effective by the valuation's business time and known at its knowledge cutoff.
- All arithmetic is exact. No rounding is introduced.
- Business rules stay in the managed backend. TypeScript presents backend results only.
- Synthetic data only. No customer, vendor or competing product names in code, fixtures, docs or commit messages.
- Write the failing check first in every task. Commit with `git commit -s`. Do not push.
- Database, HTTP and browser checks need a disposable PostgreSQL environment; run them through `tools/run_preview.py` and `tools/check_all.py` with `$PG_BIN`, `$DOTNET` and `$BROWSER` set.
- Migration and decision numbers are the next free ones at build time.

## Reviewed implementation requirements

A second review of the design changed several rules after this plan was first written. These requirements amend the sketches and signatures in Tasks 1 to 8, and win where a task says otherwise.

- **A distinct quantity source.** An imbalance entry's day input is its own type: a state and, when assessed, an absolute nonnegative quantity. It is not a held day quantity and has no contract quantity. Delivery valuation and ordinary following payments must give the same results beside a cash-out as without one.
- **Latest revision first.** For each account and day, take the latest revision effective by the business time and known at the cutoff, then test the statement policy. A withdrawn or ineligible latest revision makes that account and day unassessed. An older revision is never used in its place.
- **Accounts per gas day.** Resolve the side's accounts and the service terms covering each gas day as they stood at the business time and knowledge cutoff, not as they stand today.
- **Month-to-date needs the preceding day.** A day's figure is its balance less the balance of the immediately preceding calendar day, which must be assessed. Only the first day of a calendar month needs none. A missing preceding day makes the day unassessed. A term starting after the first of a month is unassessed for the rest of that month. A corrected statement changes its own day's figure and the next day's.
- **Account netting is a stated term.** `net_accounts_v1` adds the accounts' figures and then splits the total. `each_account_v1` splits each account's figure and then adds.
- **Incomplete coverage.** A total that includes an unassessed day is a subtotal. The entry, the trade, the portfolio and the risk result each carry a marker and the count of unassessed days.
- **Withdrawal after payment.** The day becomes unassessed, its recorded payment stays a fact, and the row's state is `unassessed_paid`. It is not overpaid and its accrual is not zero.
- **Net settlement.** One recorded fact with one net amount produces a payment on each entry, in that entry's direction, summing to the net amount and carrying the settlement's identity.
- **Prices.** A zero price is a real zero. A negative index plus spread is valued with its sign, and a payment against it is refused by the existing direction rule, which is not changed.
- **Read permission.** The owner confirmed on 24 November 2026 that anyone who can read a trade may see its imbalance value. The valuation read needs `ReadTrades` and `ReadMarketData`, as block one's read of delivery quantities does. Operating screens keep their own permission.

## Review Focus

1. **An ended day with no eligible statement.** It is unassessed on every route, with no amount, and is never "no amount due". (Tasks 1, 3, 5)
2. **A statement revised after its day was paid, changing the day's sign.** The paid entry falls to zero and shows the payment as overpaid; the other entry shows the new amount. Nothing refuses. (Task 3)
3. **A month-to-date balance across a month end, and after a missing day.** A new month starts from zero, and the day after a missing day is unassessed, because its difference spans two days. (Task 1)
4. **A statement known after the valuation's knowledge cutoff, or effective after its business time.** It does not enter. (Tasks 3, 4, 5)
5. **A reader who can read the trade but not gas operations, and a risk worker outside its lease.** The first sees the value; the second is refused. (Tasks 4, 5)

## Reference values used by the checks

Synthetic, no discounting, both spreads zero. One account's final statements and the published daily index:

| Day | Reported | Index | Daily basis: quantity, amount | Month-to-date basis: quantity, amount |
| --- | --- | --- | --- | --- |
| D1 | +300 | 3.00 | +300, +900 | +300, +900 |
| D2 | +300 | 3.10 | +300, +930 | 0, 0 |
| D3 | −200 | 3.20 | −200, −640 | −500, −1,600 |
| D4 | none | 3.30 | unassessed | unassessed |

Daily basis: owed to us +1,830, owed by us −640, total +1,190. Month-to-date basis: +900, −1,600, total −700.

Further cases:

- Month-to-date statements of +100 on 30 January, +150 on 31 January, +40 on 1 February and +10 on 2 February give quantities +100, +50, +40 and −30.
- Month-to-date statements of +100 on D1, the first of a month, and +250 on D4, with none between, give +100 on D1 and unassessed on D2, D3 and D4.
- Month-to-date statements starting on the tenth of a month give unassessed for every day of that month.
- Accounts of +100 and −100 on one day, index 3.00, spreads 0.10 owed to us and 0.20 owed by us: `net_accounts_v1` gives 0, and `each_account_v1` gives +310 and −320, a net of −10.
- A later final statement withdrawn, with an older final revision beneath it: unassessed.
- D1 paid at 900, then its statement withdrawn: `unassessed_paid`, with the 900 still recorded.
- One net receipt of 1,190 clears +1,830 on the entry owed to us and −640 on the entry owed by us.
- Two accounts reporting +300 and −100 on one day give +200 under `net_accounts_v1`.
- A reported +250 with an accepted transfer of −200 not yet included gives a current balance of +50.
- D1 paid at 900, then revised to +200: 600 accrued and 300 overpaid. Revised instead to −100: the entry owed to us shows 0 accrued and 900 overpaid, and the entry owed by us shows −300.

---

## Stage 1: the reported balance as a quantity source

### Task 1: Day quantities from statements

**Files:**
- Create: `src/OpenEtrm.MarketData/Gas/GasImbalanceCashout.cs`
- Create: `tests/OpenEtrm.MarketData.Checks/GasImbalanceCashoutChecks.cs`
- Modify: `tests/OpenEtrm.MarketData.Checks/Program.cs` (register `--gas-imbalance-cashout`)

**Interfaces:**
- Produces:
  - `public enum StatementBasis{DailyImbalance=1,MonthToDateBalance}`
  - `public sealed record AccountDayBalance(Guid AccountId,DateOnly Day,ExactQuantity Current,Guid StatementId,string Status,string Evidence);` `Current` is the output of `GasImbalanceTrading.Balance` for an eligible statement.
  - `public sealed record ImbalanceDay(DateOnly Day,string State,SignedRatio OwedToUs,SignedRatio OwedByUs,ImmutableArray<DeliveryQuantityEvidence> Evidence);`
  - `public static class GasImbalanceCashout` with `public static ImmutableArray<ImbalanceDay> Days(ImmutableArray<DateOnly> days,Func<DateOnly,bool> ended,Func<DateOnly,ImmutableArray<Guid>> accountsOn,ImmutableArray<AccountDayBalance> balances,StatementBasis basis,AccountNetting netting,ImmutableHashSet<DateOnly> monthsWithTransfers)`, with `public enum AccountNetting{NetAccounts=1,EachAccount}`. A month is named by its first day.
- Rules, in order, for each day:
  1. not ended: `not_applicable`, both quantities zero;
  2. ended, and there is no account or any account has no balance for the day: `unassessed`, both quantities zero;
  3. otherwise the day's total is the sum of its accounts' balances, and the state is `assessed`.
- Under `DailyImbalance` an account's figure for a day is its balance. Under `MonthToDateBalance` it is its balance less its balance on the immediately preceding calendar day; if that day is not assessed for the account the day is `unassessed`; the first day of a calendar month needs no predecessor.
- Under `NetAccounts`, `OwedToUs` is the sum of the accounts' figures when positive and `OwedByUs` its size when negative. Under `EachAccount`, `OwedToUs` is the sum of the positive figures and `OwedByUs` the sum of the sizes of the negative ones.
- Under `MonthToDateBalance`, an assessed day in a month listed in `monthsWithTransfers` refuses with `gas_imbalance_transfer`.
- It refuses, with `gas_imbalance_input`: a balance for an account not listed, two balances for one account and day, a balance for a day not listed, and balances in different units.

- [x] **Step 1: Write the failing checks** in `GasImbalanceCashoutChecks.Run()`: every row of the reference table under both bases; the month-end case; the gap case; the two-account case; one of two accounts missing a day gives `unassessed`; no accounts gives `unassessed` on every ended day; a day not ended is `not_applicable` even with a balance supplied for it; the month-to-date transfer refusal, and no refusal under the daily basis; each `gas_imbalance_input` refusal.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-imbalance-cashout`
Expected: build failure naming `GasImbalanceCashout`.

- [x] **Step 3: Implement.**

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-imbalance-cashout`
Expected: `PASS`.

- [x] **Step 5: Commit**

```bash
git add src/OpenEtrm.MarketData/Gas/GasImbalanceCashout.cs tests/OpenEtrm.MarketData.Checks/GasImbalanceCashoutChecks.cs tests/OpenEtrm.MarketData.Checks/Program.cs
git commit -s -m "Turn an operator's statements into a day's imbalance under a stated basis"
```

### Task 2: The entry's terms in the booking form and the saved Instrument

**Files:**
- Modify: `src/OpenEtrm.MarketData/Gas/GasImbalanceCashout.cs`, `GasCompositionInstrument.cs`, `RetainedGasInstrument.cs`
- Modify: `src/OpenEtrm.MarketData/Trading/RetainedProgramInstrument.cs` (`IRetainedProgramMember`), `DefinitionIdentity.cs`
- Test: `tests/OpenEtrm.MarketData.Checks/GasImbalanceCashoutChecks.cs`

**Interfaces:**
- Produces: `[EconomicDefinition("reported_balance_follow_v1")] public sealed record ReportedBalanceFollow([property:IdentityField]string Profile,[property:IdentityField]bool OwedToUs,[property:IdentityField]StatementBasis Basis,[property:IdentityField]AccountNetting Netting,[property:IdentityField]string StatementPolicy);`
- `IRetainedProgramMember` gains `ReportedBalanceFollow? ValuationBalance { get; }`. `RetainedGasMember` gains an identity field `Balance`, a trailing optional constructor parameter. Other member types return null. The identity writer skips it when null.
- `RetainedGasMember` refuses, with `gas_membership`, a balance-following member that has no source slot, or has a part or a quantity policy of its own, or whose direction does not match: received when `OwedToUs`, paid otherwise.
- `GasCashComponent` gains a trailing `ReportedBalanceFollow? Balance=null`. Such a component has role `imbalance_cashout`.
- The booking form refuses, with `gas_composition_input`:
  - a balance-following entry that is not an index payment on `FloatingSide.DeliveryIndexImplementation` with one row for each gas day of its source's whole term;
  - one with a share other than one, a part, or dates shorter than its source's term;
  - a source that is not an independent physical side, or a composition with a fuel link;
  - a physical side with one such entry and not its pair: exactly one owed to us and one owed by us, with the same profile, index, payment day, basis, netting and statement policy;
  - a statement policy other than the two installed values.

- [x] **Step 1: Write the failing checks.** A purchase with a complete pair is accepted and each entry's ratio is its source's; each refusal above; the identity of the existing composition fixture and of the existing saved gas fixture equal the literals captured before the change; adding the pair changes the identity, and changing only the basis changes it again.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-imbalance-cashout`
Expected: build failure naming `ReportedBalanceFollow`.

- [x] **Step 3: Implement.**

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-imbalance-cashout`, then `-- --gas-composition-pricing`, `-- --gas-instrument-schedule` and `-- --compact-retained`
Expected: each prints `PASS`.

- [x] **Step 5: Commit**

```bash
git add src tests
git commit -s -m "Hold an imbalance cash-out's two entries in the gas Instrument's identity"
```

### Task 3: The schedule supplies reported balances, and a day can be unassessed

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/PhysicalLifecycle.cs` (`PhysicalCashState`; the payment row loop; `SettlementState`), `EntryQuantities.cs`, `RetainedProgramInstrument.cs` (`RetainedInstrumentSchedule.Evaluate`), `ScheduleFollowedDay.cs`
- Modify: `src/OpenEtrm.MarketData/Gas/RetainedGasInstrument.cs`
- Test: `tests/OpenEtrm.MarketData.Checks/GasImbalanceCashoutChecks.cs`

**Interfaces:**
- Produces: `public sealed record SlotBalances(int Slot,ImmutableArray<Guid> Accounts,ImmutableArray<AccountDayBalance> Balances,ImmutableHashSet<DateOnly> MonthsWithTransfers);`
- `PhysicalCashState` gains a trailing `ImmutableArray<SlotBalances> balances=default` and `public ImmutableArray<SlotBalances> Balances`. It is keyed by physical slot. A state built with no entry for a slot means that slot has no account.
- `RetainedInstrumentSchedule.Evaluate`, for each balance-following entry: takes its source slot's balances, calls `GasImbalanceCashout.Days` with the source's gas days and their ended states, and builds the entry's day quantities taking `OwedToUs` or `OwedByUs` as the day's absolute quantity. A trade with a balance-following entry requires held day quantities, as one with a part does.
- The entry's days reach its Side as `ImbalanceDay` values, not as `EntryDayQuantity`. A row's amount is its absolute quantity times its price.
- `SettlementState` gains `Unassessed` and `UnassessedPaid`. A row whose day is `unassessed` has no accrued amount and no outstanding amount, and is not counted in the realized or the unrealized component. With a recorded payment its state is `UnassessedPaid` and the payment is reported beside it.
- A row whose day is `not_applicable` counts nothing.
- `PhysicalLifecycleValue` and `InstrumentValuation` gain `public int UnassessedDays { get; }`. It is the count of unassessed days across the Instrument's entries, and zero for every Instrument without a cash-out.
- Everything else in the lifecycle is unchanged: an assessed row is earned when its day has ended and its index is published, a forecast index leaves it projected, and recorded payments apply as today.

- [x] **Step 1: Write the failing checks,** through `RetainedGasInstrument.EvaluateLifecycle`, on a four-day purchase with its ordinary payment and the pair, valued after D4 has ended with the index published on all four days:
  - both bases of the reference table: each entry's total, the D4 rows in state `Unassessed`, and the ordinary entries unchanged by the pair's presence;
  - valued before D3 ends: D3 and D4 are `not_applicable` and count nothing;
  - `UnassessedDays` is 1 on the lifecycle value and on the Instrument valuation in the reference case, and 0 for the same trade without the cash-out;
  - the ordinary payment and the physical side give the same amounts with the cash-out present as without it;
  - D1 paid at 900 and then withdrawn is `UnassessedPaid` with 900 recorded, no accrued amount and no overpaid balance;
  - a day whose index plus spread is negative is valued with its sign;
  - with D3's index not yet published, the entry owed by us is projected at the forecast and is unrealized, not accrued;
  - the trade at twice the size gives the same imbalance amounts;
  - both revision cases of the reference list;
  - a state with no balances for the slot gives `Unassessed` on every ended day;
  - a state with no held day quantities refuses with `physical_lifecycle_quantity`;
  - the month-to-date transfer refusal reaches the caller as `gas_imbalance_transfer`.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-imbalance-cashout`
Expected: build failure naming `SlotBalances`.

- [x] **Step 3: Implement.**

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-imbalance-cashout`, then `-- --physical-lifecycle`, `-- --payment-date-policy`, `-- --delivery-quantities` and `-- --gas-instrument-schedule`
Expected: each prints `PASS`.

- [x] **Step 5: Commit**

```bash
git add src tests
git commit -s -m "Follow a side's reported balance, and show a day without a statement as unassessed"
```

---

## Stage 2: statements in valuation

### Task 4: The database and the store carry the cash-out and read its statements

The API passes its request body to the database writer, which refuses unknown keys. The two change in one task.

**Files:**
- Create: `db/migrations/0396_gas_imbalance_cashout.sql`; modify `db/README.md`
- Modify: `src/OpenEtrm.Api/GasCompositionStore.cs` (`GasCompositionRequest`, `Resolve`, `Execution.Instrument`, `Load`), `GasCashPaymentStore.cs`, `GasPortfolio.cs`, `GasBlotter.cs`
- Create: `src/OpenEtrm.Api/GasBalanceStore.cs`
- Create: `tools/check_gas_imbalance.py`
- Modify: `tools/run_preview.py` (flag `--gas-imbalance-check`), `tools/check_all.py` (the gas suite)
- Test: `tests/OpenEtrm.Api.Checks/GasTicketChecks.cs`, `tools/check_gas_imbalance.py`

**Interfaces:**
- Produces: `public sealed record GasImbalanceCashoutRequest(string Code,string Profile,string Side,string Index,string SpreadOwedToUs,string SpreadOwedByUs,int PaymentDay,string StatementPolicy,string StatementBasis,string AccountNetting);`
- `GasCompositionRequest` gains `ImmutableArray<GasImbalanceCashoutRequest>? ImbalanceCashouts`, omitted when null on write.
- `Resolve` builds the two index payment Sides with `GasNamedIndexCash.Resolve` on the daily convention, with entry codes `Code + ".to_us"` and `Code + ".by_us"`, and passes them to the booking form. `Execution.Instrument` gives each member its `Balance` term.
- `trading.gas_component_terms`: the role check admits `imbalance_cashout`; new columns `balance_side text` (`'owed_to_us'` or `'owed_by_us'`), `cashout_profile text`, `statement_basis text`, `account_netting text` and `statement_policy text`, set only on such a row. A table `trading.gas_cash_settlements` holds a net settlement and links its two payments.
- `security.write_gas_composition` accepts `imbalanceCashouts`, creates the pair, and checks independently everything Task 2 refuses. It does not require a service account.
- Produces: `security.read_gas_valuation_balances(catalog uuid,trade uuid,instrument uuid) RETURNS jsonb`, `SECURITY DEFINER`, granted to `openetrm_runtime` and `openetrm_risk_worker`, with the authority checks of `security.read_gas_valuation_quantities` in migration 0390: the runtime needs `ReadTrades` and `ReadMarketData` and a visible trade; the worker needs its active lease and exact job scope. It returns, for each physical slot of the Instrument:
  - the service accounts of that slot's delivery obligation whose holder is the legal entity;
  - for each gas day, the accounts and service terms that covered it at the business time and cutoff;
  - for each account and day, the latest revision effective by the business time and known at the cutoff, whatever its status, with its reported balance, included movement and evidence;
  - the accepted movements of each account and day known at the cutoff.
- Produces, in `GasBalanceStore`: `internal static async Task<ImmutableArray<SlotBalances>> Read(NpgsqlConnection connection,Guid catalog,GasCompositionExecution execution,DateTimeOffset at,CancellationToken ct)`. It applies each entry's statement policy, computes each current balance with `GasImbalanceTrading.Balance`, and lists the months with an accepted movement. It returns empty when the trade has no balance-following entry.
- Every route that builds the held state passes these balances to it.

- [x] **Step 1: Write the failing checks.**
  - `GasTicketChecks.Run()`: a request round-trips `imbalanceCashouts` and omits the key when null.
  - `tools/check_gas_imbalance.py`, following `tools/check_gas_pricing.py` and recording statements as `tools/check_gas_operations.py` does:
    - a purchase with an imbalance cash-out books before any service account exists, stores the pair with its basis and policy, and replays to its first outcome;
    - a cash-out on a payment, on a linked side, with one spread missing, or with an unknown basis is refused with `gas_composition_input`;
    - the read returns no accounts before one is created; after an account and four statements it returns them with status and evidence; a withdrawn statement is not returned as eligible; a counterparty-held account is not returned;
    - a context captured before the D3 statement does not return it;
    - a caller with `ReadTrades` and without gas operations permission can call the read; one without `ReadTrades` is refused with SQLSTATE `42501`; the risk worker reads inside its lease and is refused outside it.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`
Expected: build failure naming `ImbalanceCashouts`.

- [x] **Step 3: Write the migration and the store change together.** Follow the house upgrade pattern of the preceding gas migration: one transaction, the advisory lock, and a predecessor pin for every function patched.

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`, then `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-imbalance-check --gas-composition-check --gas-pricing-check --gas-operations-check`
Expected: each run ends with its checks passed.

- [x] **Step 5: Commit**

```bash
git add db src tests tools
git commit -s -m "Book an imbalance cash-out and read its statements in valuation"
```

### Task 5: Every route reports the reference values

**Files:**
- Modify: `tools/check_gas_imbalance.py`
- Modify, only where a check exposes a gap: `src/OpenEtrm.Api/GasCompositionStore.cs`, `GasBlotter.cs`, `GasPortfolio.cs`, `src/OpenEtrm.MarketData/Trading/ScheduleView.cs`

**Interfaces:**
- The schedule read reports, for each gas day of an imbalance cash-out: the reported balance and its status, the day's state, the day's quantity, the index and whether it is published, the amount, and which of the two entries carries it.

- [x] **Step 1: Write the failing checks.** Through the administrator's in-term load, with four statements and published prices:
  - the single-trade read, the Blotter and a risk run each report the reference totals under each basis, and D4 as `unassessed` with no amount;
  - recording D4's statement as −200 under the daily basis adds −660 on the entry owed by us on every route; a context captured before it still reports D4 unassessed;
  - a payment of 900 against D1, then a revision of D1 to −100, shows 900 overpaid on one entry and −300 on the other;
  - a month-to-date trade with an accepted transfer shows its own `gas_imbalance_transfer` reason on its Blotter row while other trades still value;
  - a reader without gas operations permission sees the same values;
  - every route reports the subtotal with its incomplete marker and the count of unassessed days, and the risk result carries the same marker;
  - one net settlement of 1,190 records a payment on each entry, +1,830 and −640, under one settlement identity, and a second net settlement for the same month is refused;
  - a payment against a day at a negative price is refused, as it is for any index payment;
  - a risk run with D3's index unpublished shows price sensitivity on D3 only.

- [x] **Step 2: Run to verify failure**

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-imbalance-check`
Expected: FAIL on the first assertion a route does not yet satisfy; if every assertion passes, record that and continue.

- [x] **Step 3: Close each gap the checks expose.**

- [x] **Step 4: Run to verify it passes**

Run: `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-imbalance-check --gas-in-term-check --gas-delivery-quantity-check`
Expected: all checks pass.

- [x] **Step 5: Commit**

```bash
git add src tools
git commit -s -m "Report an imbalance cash-out identically in reads, the Blotter and risk"
```

---

## Stage 3: the ticket and the Imbalance tab

### Task 6: The Imbalance cash-out side on the ticket

**Files:**
- Modify: `src/OpenEtrm.Api/GasTicket.cs`
- Test: `tests/OpenEtrm.Api.Checks/GasTicketChecks.cs`

**Interfaces:** a side kind `imbalance`, pill "Imbalance cash-out", one field per row, every choice required and nothing prefilled:
- `imbalance_source`, label "Physical side", a choice from the trade's physical sides;
- `imbalance_index`, label "Cash-out index", a choice from the pricing indices;
- `imbalance_spread_to_us`, label "Spread when gas is owed to us", decimal;
- `imbalance_spread_by_us`, label "Spread when we owe gas", decimal;
- `imbalance_payment_day`, label "Payment day", from the existing payment day choices;
- `imbalance_statement_policy`, label "Statements used": `final_statement_balance_v1` ("Final only") or `reported_statement_balance_v1` ("Provisional and final");
- `imbalance_statement_basis`, label "A statement reports": `daily_imbalance_v1` ("That day's imbalance") or `month_to_date_balance_v1` ("The month-to-date balance");
- `imbalance_account_netting`, label "Several accounts": `net_accounts_v1` ("Net them, then settle") or `each_account_v1` ("Settle each on its own").
- One such side counts as two of the trade's seven payment Sides.
- A refusal from booking is raised on the field that caused it.

- [x] **Step 1: Write the failing checks** in `GasTicketChecks.Run()`: a draft maps to the request and back; a draft with no such side maps to no `imbalanceCashouts` key; a draft with six other payment sides is refused the cash-out; a linked source refuses on `imbalance_source`; no field carries a starting value.

- [x] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket`
Expected: FAIL, unknown side kind `imbalance`.

- [x] **Step 3: Implement.**

- [x] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --gas-ticket` and `-- --ticket-description`
Expected: both pass.

- [x] **Step 5: Commit**

```bash
git add src/OpenEtrm.Api/GasTicket.cs tests/OpenEtrm.Api.Checks/GasTicketChecks.cs
git commit -s -m "Enter an imbalance cash-out on the gas ticket"
```

### Task 7: The Imbalance tab and browser checks

**Files:**
- Modify: `src/OpenEtrm.Api/GasTicket.cs:77` (the Imbalance tab's placeholder), and the web file that renders a ticket's schedule tabs, with its test
- Create: `tests/browser/gas-imbalance.mjs`

**Interfaces:**
- The Imbalance tab is backed by the rows of Task 5. The backend supplies every value, label and state; the browser lays them out.
- Web text for the day states: `assessed` is blank, `unassessed` is "no statement yet", `not_applicable` is "day not ended". A subtotal with unassessed days reads "incomplete: N days without a statement".

- [x] **Step 1: Write the failing web test** for the three state texts, beside the file that holds the schedule's other state texts.

- [x] **Step 2: Write the failing browser checks** in `tests/browser/gas-imbalance.mjs`, using the `gasTicket` driver: Add side offers "Imbalance cash-out" with every field empty; a trade with one books; before any statement the Imbalance tab shows every ended day as "no statement yet" and no amount; after statements are recorded it shows each day's balance, quantity, index and amount, with a day owed to us and a day owed by us on their own entries.

- [x] **Step 3: Run to verify failure, implement, then pass**

Run: `npm test --prefix web`, then `python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --gas-imbalance-check --browser-check --browser "$BROWSER"`
Expected: all web tests pass and the imbalance browser checks pass.

- [x] **Step 4: Commit**

```bash
git add src web tests/browser
git commit -s -m "Show a trade's imbalance day by day on its Imbalance tab"
```

### Task 8: Full groups, records and review

**Files:**
- Modify: `docs/decisions.md`, `docs/gas-delivery-operations.md`, `docs/physical-gas-side.md`, `docs/deal-ticket.md`, `docs/security.md`, `docs/plan.md`, `docs/implementation.md`, `docs/verification.md`, and the spec's status line

- [x] **Step 1: Run the full groups**

Run: `python3 tools/check_all.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --browser "$BROWSER" --group gas`, then `--group trading` and `--group managed`
Expected: every group passes.

- [x] **Step 2: Update the records.**
  - `docs/decisions.md`: one new decision under the next free number. It states the two entries, the two statement bases, the day quantity rules, the unassessed state, who may read a statement's value in valuation, and the ten calls confirmed in the spec. It revises D339's statement that a statement balance has no portfolio value, for trades that state a cash-out.
  - `docs/gas-delivery-operations.md` and `docs/security.md`: the valuation read and its authority.
  - `docs/physical-gas-side.md` and `docs/deal-ticket.md`: the side, its fields, the Imbalance tab and the refusals.
  - `docs/plan.md`, `docs/implementation.md`, `docs/verification.md`: status and the actual check counts from Step 1.
  - The spec's status line: built, with the decision number.

- [x] **Step 3: Obtain one independent adversarial review** of the temporal behavior (statement effective time and knowledge cutoff on every route, revisions after payment) and of the authority of the valuation read (trade readers, operations permission, the risk worker's lease). Resolve findings and rerun Step 1.

- [x] **Step 4: Commit**

```bash
git add docs
git commit -s -m "Record the imbalance cash-out and its verification"
```
