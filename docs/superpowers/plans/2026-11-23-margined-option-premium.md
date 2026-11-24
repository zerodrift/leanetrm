# Margined Option Premium Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status:** Not started. Plan of 2026-11-23 for piece 3 of D446.

**Goal:** An option whose product states a margined premium is booked with no premium cash, carries its own variation margin in the futures margin records, and is valued at the variation not yet margined.

**Architecture:** The futures margin tables are widened to hold an option holding: a policy on an option contract with a closing term, an adoption that names an option trade, and a determination whose evidence is a settlement or an option event. Options get their own SQL entry functions over those tables, so no futures function is redefined. Margin arithmetic stays in a managed planner, as for futures; the native functions check it. The option instrument values a margined series at a unit discount less a per-holding basis supplied as binding state.

**Tech Stack:** PostgreSQL migrations (`db/migrations`), C# on .NET 10 (`src/OpenEtrm.MarketData`, `src/OpenEtrm.Api`), TypeScript presentation (`web/src`), Python check tools (`tools/`).

**Spec:** [docs/superpowers/specs/2026-11-23-futures-option-styles-design.md](../specs/2026-11-23-futures-option-styles-design.md), sections "Owner decisions", "Piece 3: Margined premium", "Roles", "What stays refused", "Tests that define done" and "Highest-risk inputs".

This plan needs neither of the other two. Where it names "the stated policy" it means whichever policy the Blotter states when this plan is built.

## Global Constraints

- Booking convention `futures_option_margined_v1`; policy implementation `dated_option_margin_policy_v1`; closing term `zero_at_close_v1`; valuation `margined_option_open_variation_v1`; action kind `option.margin`.
- Daily variation: settlement less previous basis, times the multiplier, times the signed lots open at that instant. The first basis is the trade premium. A daily occurrence at the same instant as a close is determined first.
- Closing determination: zero less the basis at the event's business instant, times the multiplier, times the signed lots closed; due at the due instant of the policy's first occurrence, daily or final, at or after that instant.
- A holding's journal reads as if recorded in business-time order. A close needs margin determined through every daily occurrence before it (`option_margin_behind`). A close recorded late restates later determinations under a new run revision; an earlier knowledge view keeps what it had.
- A margined series is valued at a unit discount with no discount input. A holding's value is the model value less its basis: the trade premium before adoption, the settled basis after. An adopted holding with a pending occurrence is refused (`option_margin_state`).
- Every existing futures margin policy, adoption, run, determination, `terms_hash` and first outcome is unchanged, and every existing futures margin check passes without an edit. No futures SQL function is redefined. If Task 1 cannot meet this, stop and return to the owner before any other task (spec, highest-risk input 1).
- Every existing option booking keeps its premium cash. Permissions are those of the spec's Roles table; no role or permission is added.
- Margin arithmetic is planned in managed code and checked natively, as `db/migrations/0258_futures_margin_journal.sql:205-207` states for futures. Arithmetic is exact (`ExactMoney`, `ExactUnitPrice`); rounding is the policy's.
- Migrations use the header and anchored patch pattern of `db/migrations/0382_dated_option_lots.sql`. Writes are idempotent (D238).
- TypeScript presents backend values only. Synthetic data only.
- Write the failing check first in every task. Commit with `git commit -s`. Do not push and do not open a pull request without the owner's word.
- Migration numbers 0394 to 0396 are provisional: take the next free numbers when building.
- Database, HTTP and real-browser checks run in CI or on the owner's preview host, on the owner's word. Do not install or start PostgreSQL on the workstation.
- One combined independent adversarial review at the end (Task 13). No per-task review rounds.

## Review Focus

1. **A short holding.** Daily, closing and restated margin all carry the short's sign: a short receives when the settlement falls and receives its premium through the closing determination. (Tasks 2 and 8)
2. **Rounding.** With a rounding increment coarser than the exact variation, each determination records its rounding effect; the exact sum is minus the premium and the rounded sum differs from it by at most half an increment per determination. (Task 2)
3. **A basis of zero at the close.** An option that settled at 0 closes with a zero determination, which writes no cash membership and still closes the state. (Task 7)
4. **Booked and closed with no occurrence between.** The closing determination uses the trade premium as its basis and is due at the first occurrence at or after the close. (Task 7)
5. **A corrected settlement that was a closing basis.** The correction restates the daily determination and the closing determination that used it. (Task 8)

---

## File Structure

| File | Responsibility |
| --- | --- |
| `db/migrations/0394_option_margin_records.sql` (new) | Widens the shared margin tables; changes no function |
| `db/migrations/0395_margined_option_booking.sql` (new) | Trade revision terms and the booking function |
| `db/migrations/0396_option_margin.sql` (new) | Option margin entry functions, the closing step in option events, the `option.margin` kind |
| `src/OpenEtrm.MarketData/Trading/FuturesOptionMargin.cs` (new) | The planner, its records and the holding's margin state |
| `src/OpenEtrm.MarketData/Trading/FuturesOptions.cs` | The instrument values a margined series from binding state |
| `src/OpenEtrm.Api/FuturesOptionMarginStore.cs` (new) | Reads native policy and state |
| `src/OpenEtrm.Api/FuturesOptionMarginCoordinator.cs` (new) | Plans, previews and submits determinations |
| `src/OpenEtrm.Api/OptionMarginLifecycleActions.cs` (new) | Discovery, preview and execution of `option.margin` |
| `src/OpenEtrm.Api/LifecycleOptionMarginSettings.cs` (new) | Policy save and adoption |
| `tools/check_option_margin_records.py`, `tools/check_margined_option.py` (new) | Database and HTTP checks |
| `tests/OpenEtrm.MarketData.Checks/FuturesOptionMarginChecks.cs` (new) | Managed checks, flag `--option-margin` |

Worked examples used throughout. **Example A** (managed checks, multiplier 1,000): long 10 lots, trade premium 2.15; settlements 2.40, 1.95, 2.10 on days 1 to 3; 4 lots exercised on day 3 before that day's occurrence; expiry closes the other 6.

| Step | Lots | Arithmetic | Amount |
| --- | --- | --- | --- |
| Day 1 | 10 | (2.40 − 2.15) × 1000 × 10 | 2,500.00 |
| Day 2 | 10 | (1.95 − 2.40) × 1000 × 10 | −4,500.00 |
| Close | 4 | (0 − 1.95) × 1000 × 4 | −7,800.00 |
| Day 3 | 6 | (2.10 − 1.95) × 1000 × 6 | 900.00 |
| Expiry close | 6 | (0 − 2.10) × 1000 × 6 | −12,600.00 |
| Sum | | −(2.15 × 1000 × 10) | −21,500.00 |

**Example B**: short 5 lots, trade premium 3.00; settlements 3.20 and 2.90. Day 1: (3.20 − 3.00) × 1000 × (−5) = −1,000.00. Day 2 on 5 lots: (2.90 − 3.20) × 1000 × (−5) = 1,500.00. An assignment of 2 lots dated day 2 before the occurrence is recorded afterwards: day 2 restates on 3 lots to 900.00 (adjustment −600.00) and the close is (0 − 3.20) × 1000 × (−2) = 6,400.00, due at day 2's due instant.

The database tool uses the same shapes on a 2,500 MMBtu underlying with prices a tenth of Example A's (premium 0.215; settlements 0.240, 0.195, 0.210): 625.00, −1,125.00, −1,950.00, 225.00, −3,150.00, sum −5,375.00.

---

### Task 1: Widen the shared margin records (gate)

**Files:**
- Create: `db/migrations/0394_option_margin_records.sql`
- Create: `tools/check_option_margin_records.py`
- Modify: `tools/check_all.py:120-121` (register beside `check_futures_margin_adoption`)

**Interfaces:**
- Produces:
  - `settlement_data.futures_margin_policies`: the `implementation` check also admits `dated_option_margin_policy_v1`; column `closing_mark text`; constraint `futures_margin_policies_closing_mark_check`: `(implementation='dated_option_margin_policy_v1')=(closing_mark IS NOT NULL) AND(closing_mark IS NULL OR closing_mark='zero_at_close_v1')`.
  - `settlement_data.futures_margin_adoptions`: `origin_kind text NOT NULL DEFAULT 'future'` with values `future`, `future_option`; stored generated columns `futures_origin_trade` and `option_origin_trade` (the trade id when the kind matches, else null). The two existing foreign keys (`0257:42-44`) are re-pointed from `trade_id` to `futures_origin_trade`. A new foreign key `(catalog_id, option_origin_trade, origin_revision)` references `trading.futures_option_trade_revisions(catalog_id, trade_id, revision)`. For an option origin `binding_id = instrument_id`.
  - `settlement_data.futures_margin_determinations`: `source_observation_id` nullable; `closing_event_id uuid` referencing `trading.futures_option_events(catalog_id,id)`; `lots ref_data.exact_number`; constraint `futures_margin_determinations_evidence_check`: `num_nonnulls(source_observation_id,closing_event_id)=1 AND(closing_event_id IS NULL OR lots IS NOT NULL)`. The unique constraint at `0258:30` becomes two partial unique indexes: `futures_margin_determination_occurrence` on `(catalog_id,trade_id,binding_id,run_revision,occurrence_id) WHERE closing_event_id IS NULL`, and `futures_margin_determination_close` on `(catalog_id,trade_id,binding_id,run_revision,closing_event_id) WHERE closing_event_id IS NOT NULL`.
  - Trigger functions behind `exact_delivery`, `qualified_physical_policy`, `exact_delivery_owner`, `qualified_physical_adoption` and `physical_final` return without action for a policy on a `future_option` contract or an option-origin adoption. They read `futures_series` with `INTO STRICT` today (`0272:267-270`, `0285:13-16`, `0273:200-204`).
  - New columns are trailing and nullable or defaulted, because existing code inserts positionally (`0262:168-169`, `tools/check_futures_margin_adoption.py:270-272`).

- [ ] **Step 1: Write the failing check tool**

`tools/check_option_margin_records.py`, run through `check_reference_editing.main` with its `before_migration` hook (`tools/check_reference_editing.py:201`), over the journal fixture of `tools/check_futures_margin_journal.py`:

- before the migration, capture `to_jsonb(row)` of every row of the seven margin tables; after it, each row's `to_jsonb(row)` less the new columns is identical, `origin_kind` reads `future`, and every policy's `terms_hash` recomputes to its stored value;
- as the security owner: an adoption with kind `future_option` naming an option trade inserts; the same naming a futures trade is refused by the new foreign key; a determination with both or neither evidence is refused by `futures_margin_determinations_evidence_check`; two occurrence determinations for one occurrence in one run are refused; an occurrence determination and a closing one for the same occurrence in one run insert; a policy with the option implementation and no closing mark is refused.

- [ ] **Step 2: Verify it fails before the migration**

Run on the preview host, on the owner's word: `python3 tools/check_option_margin_records.py --pg-bin <pg bin> --dotnet <dotnet> --no-build`
Expected: FAIL, column `origin_kind` does not exist.

- [ ] **Step 3: Write the migration**

- [ ] **Step 4: Verify, and decide the gate**

Run on the preview host: the tool, then `python3 tools/check_all.py --pg-bin <pg bin> --dotnet <dotnet> --browser <browser> --group lifecycle`.
Expected: the tool passes, and `check_futures_margin_adoption`, `check_futures_margin_journal`, `check_futures_margin_sources`, `check_futures_margin_projection_sources`, `check_futures_physical_delivery` and `check_physical_margin_qualification` pass with no edit to them.
If any of them needs an edit to pass, stop here and return to the owner.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0394_option_margin_records.sql tools
git commit -s -m "Widen the margin records to hold an option holding, with futures rows unchanged"
```

### Task 2: The option margin planner

**Files:**
- Create: `src/OpenEtrm.MarketData/Trading/FuturesOptionMargin.cs`
- Modify: `src/OpenEtrm.MarketData/Trading/FuturesMarginTrajectory.cs:8-15`
- Create: `tests/OpenEtrm.MarketData.Checks/FuturesOptionMarginChecks.cs`; modify `tests/OpenEtrm.MarketData.Checks/Program.cs` (flag `--option-margin`, and the default pass)

**Interfaces:**
- Produces:
  - `FuturesMarginPolicy.OptionVersion = "dated_option_margin_policy_v1"`; `public string? ClosingMark { get; init; }`.
  - `public sealed record FuturesOptionMarginTerms(MeasureBinding Owner, Guid BookId, ExactDecimal SignedLots, ExactDecimal Multiplier, UnitPrice TradePremium, DateTimeOffset ExecutedAt, FuturesMarginPolicy Policy);`
  - `public sealed record FuturesOptionLotClose(Guid EventId, DateTimeOffset At, ExactDecimal Lots);`
  - `public sealed record FuturesOptionMarginEvidence(Guid OccurrenceId, ObservationKey Observation, ExactUnitPrice Price);`
  - `public enum FuturesOptionMarginStepKind { Daily = 1, Close }`
  - `public sealed record FuturesOptionMarginStep(FuturesOptionMarginStepKind Kind, Guid OccurrenceId, Guid? ClosingEventId, ObservationKey? Observation, ExactDecimal SignedLots, ExactUnitPrice PreviousBasis, ExactUnitPrice Mark, ExactMoney ExactVariation, Money Variation, ExactMoney RoundingEffect, DateTimeOffset At, DateTimeOffset DueAt);`
  - `public static ImmutableArray<FuturesOptionMarginStep> FuturesOptionMargin.Plan(FuturesOptionMarginTerms terms, ImmutableArray<FuturesOptionLotClose> closes, ImmutableArray<FuturesOptionMarginEvidence> evidence, DateTimeOffset through)`
  - `public sealed record FuturesOptionMarginChange(FuturesOptionMarginStep Step, Money? Accepted, Money Adjustment);`
  - `public static ImmutableArray<FuturesOptionMarginChange> FuturesOptionMargin.Changes(ImmutableArray<FuturesOptionMarginStep> accepted, ImmutableArray<FuturesOptionMarginStep> planned)`: the planned steps that differ from the accepted ones, each with its adjustment.
  - Refusals: `option_margin_policy` (wrong implementation or closing mark, or not exactly one final occurrence), `option_margin_settlement_missing` (a daily occurrence at or before `through` with no evidence), `option_margin_occurrence_missing` (a close with no occurrence at or after it), `option_lots_exceeded` (closes exceed the booked lots).

`Plan` rules the signature does not fix:
- steps are ordered by instant, a daily step before a close at the same instant;
- a daily step's lots are the booked lots less every close strictly before its instant;
- the final occurrence produces no step of its own; it supplies the due instant of closes at expiry;
- rounding is `FuturesMargin.Difference`'s (`FuturesMargin.cs:97-104`), with the series multiplier folded into the quantity.

- [ ] **Step 1: Write the failing checks**

```csharp
Check("a long holding's daily and closing margin follow Example A", () => {
    var steps = Plan(ExampleA);
    Equal(Amounts(steps), "2500.00,-4500.00,-7800.00,900.00,-12600.00");
    Equal(Kinds(steps), "Daily,Daily,Close,Daily,Close");
    Equal(Sum(steps), "-21500.00");
});
Check("the buyer's cumulative margin never exceeds the premium", ...);   // running sum >= -21500.00 at every step
Check("a daily occurrence at the same instant as a close is struck first, on the lots still open", ...);
Check("a late assignment restates the later determination and closes from the basis at its instant (Example B)", () => {
    var accepted = Plan(ExampleB with no closes);                       // -1000.00, 1500.00
    var changes = FuturesOptionMargin.Changes(accepted, Plan(ExampleB with the assignment));
    Equal(Describe(changes), "Daily:900.00:-600.00;Close:6400.00:6400.00");
    Equal(changes[1].Step.DueAt.ToString("O"), Day2Due.ToString("O"));
});
Check("a short holding's signs mirror the long's", ...);
Check("rounding effects are recorded and the exact sum is minus the premium", ...);   // rounding increment 1, premium 2.153
Check("a holding closed before any occurrence closes from its trade premium", ...);
Check("a zero basis closes with a zero determination", ...);
Check("the planner refuses a missing settlement, a close with no later occurrence, excess lots and a policy without one final occurrence", ...);
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -- --option-margin`
Expected: build error, `FuturesOptionMargin` not defined.

- [ ] **Step 3: Implement the planner**

- [ ] **Step 4: Run to verify they pass**

Run: `-- --option-margin`, then the default pass.
Expected: all `PASS`; the futures margin checks unedited.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEtrm.MarketData/Trading tests/OpenEtrm.MarketData.Checks
git commit -s -m "Plan a margined option's daily and closing variation margin"
```

### Task 3: Valuation of a margined holding

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/FuturesOptions.cs:14,37-38` (remove the premium-style refusal), `:57-69` (`Declare`), `:82-98` (`PV`), `:209-238` (`Prepare`)
- Modify: `src/OpenEtrm.MarketData/Trading/FuturesOptionMargin.cs` (state)
- Modify: `src/OpenEtrm.Api/FuturesOptionPortfolio.cs:15-37` (policy member), `:172-256` (state and unit discount)
- Test: `tests/OpenEtrm.MarketData.Checks/FuturesOptionMarginChecks.cs`, `tests/OpenEtrm.MarketData.Checks/FuturesOptionChecks.cs:1343`, `tests/OpenEtrm.Api.Checks/OptionPolicySelectionChecks.cs`

**Interfaces:**
- Produces:
  - `public enum FuturesOptionMarginStatus { NotAdopted = 1, Open, Closed }`
  - `public sealed record FuturesOptionMarginState(Guid TradeId, Guid BindingId, Guid? BookId, ReferenceKey Series, ExactDecimal OpenLots, ExactUnitPrice Basis, FuturesOptionMarginStatus Status, Guid? PendingOccurrence, DateTimeOffset KnownThrough) : IBindingValuationState`
  - `public sealed record FuturesOptionMarginedPolicy([property:JsonRequired] string Valuation)`; `FuturesOptionValuationPolicy.MarginedPremium` (optional, omitted when null); `Validate()` refuses any value but `margined_option_open_variation_v1` with `option_policy`.
  - `FuturesOptionInstrument` for a margined series: `Declare` lists no discount input; `PV` is the model value at a unit discount less `state.Basis`, times the multiplier; `Closed` values at zero with no dependency. No binding state, or a state with `PendingOccurrence`, raises `option_margin_state`. A series paid up front that is given such a state raises `option_margin_state`.
  - `FuturesOptionPricing.Prepare` calibrates a margined series at a unit discount (`ExplicitDiscounting` with `DiscountFactorSource.Undiscounted`, as `src/OpenEtrm.Api/GasPortfolio.cs:58`).
  - A scope holding a margined series under a policy without `MarginedPremium` raises `option_policy`, naming the series.

- [ ] **Step 1: Write the failing checks**

Base market: the option settles at 2.40 and calibrates to it; multiplier 1,000.

```csharp
Check("before adoption a margined holding is valued on its trade premium", () =>
    Equal(UnitPv(State(FuturesOptionMarginStatus.NotAdopted, basis: "2.15")), "250.00"));     // (2.40 − 2.15) × 1000
Check("after a determination its value is the variation not yet margined", () =>
    Equal(UnitPv(State(FuturesOptionMarginStatus.Open, basis: "2.40")), "0.00"));
Check("a margined option declares no discount input and calibrates at a unit discount", ...);
Check("its deltas equal those of the same series paid up front at a unit discount", ...);
Check("a pending occurrence, a missing state and a closed state give their one outcome", () => {
    Reject("option_margin_state", () => UnitPv(State(FuturesOptionMarginStatus.Open, "2.40") with { PendingOccurrence = occurrence }));
    Reject("option_margin_state", () => UnitPv(null));
    Equal(UnitPv(State(FuturesOptionMarginStatus.Closed, "0")), "0.00");
});
Check("a margined series under a policy without the margined valuation is refused and named", ...);   // option_policy
Check("a policy without the member serializes as before", ...);
```

Change `FuturesOptionChecks.cs:1343`: a margined series now constructs; the check asserts instead that valuing it with no state raises `option_margin_state`.

- [ ] **Step 2: Run to verify they fail**

Run: `-- --option-margin` and `dotnet run --project tests/OpenEtrm.Api.Checks -- --option-policy`
Expected: FAIL with `unsupported` from the instrument's constructor.

- [ ] **Step 3: Implement**

The stated policy gains the member through a new stated policy value, built from the current one with `MarginedPremium` set; the current one keeps its bytes.

- [ ] **Step 4: Run to verify they pass**

Run: `-- --option-margin`, `-- --futures-options`, the Api flags of the option policy checks.
Expected: all `PASS`; the pinned digests unedited.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -s -m "Value a margined option at its unsettled variation"
```

### Task 4: Booking without premium cash

**Files:**
- Create: `db/migrations/0395_margined_option_booking.sql`
- Create: `tools/check_margined_option.py`; modify `tools/check_all.py` (register), `tools/check_futures_option_lifecycle.py:100-102,139-143`
- Modify: `src/OpenEtrm.Api/FuturesOptionStore.cs:12-15,35,133-152`, `src/OpenEtrm.Api/Program.cs:308-309`
- Modify (readers that assume a premium): `src/OpenEtrm.Api/FuturesOptionPositions.cs:18,39-55,75-97,117-143,183`, `FuturesOptionPortfolio.cs:76-90,104-135,182-185`, `FuturesOptionBlotter.cs:20,29,38-40,59`, `PortfolioValuation.cs:537,620`, `TradePositionFacts.cs:126`, `OptionListings.cs:54`

**Interfaces:**
- Produces:
  - `trading.futures_option_trade_revisions`: `premium_instrument_id` and `premium_quantity` nullable; `premium_style` and `implementation` each admit two values; constraint `futures_option_trade_revisions_premium_terms_check`: the style is margined exactly when the implementation is `futures_option_margined_v1`, exactly when the premium instrument is null, exactly when the premium quantity is null.
  - `security.book_futures_option(...)`, same signature: with `implementation` `futures_option_margined_v1` it requires the margined style on the request and on the product and a null `premium_payment_date`, each mismatch raising `option_booking_input`; it writes no cash instrument and no cash membership. `option_unsupported` and `option_product_margined` are no longer raised. `security.futures_option_operation_status` returns a null `premiumInstrumentId` for such a trade.
  - `FuturesOptionStore.MarginedConvention = "futures_option_margined_v1"`; `FuturesOptionBookRequest.PremiumPaymentDate` is required for the paid-up-front convention and refused for the margined one.
  - A margined trade has one position fact and one Blotter row.

- [ ] **Step 1: Write the failing check tool**

`tools/check_margined_option.py`, layered as `tools/check_cash_option_value.py:36-80`: source and product `synthetic_margined_option`, cloned from `nymex_on` with `premium_style` `futures_style_margin`, underlying `ice_cfb`, multiplier 2500; close time, a call series at strike 3.10, its curve, contract and settlements 0.240, 0.195, 0.210. Booking assertions:

- a Trader books 10 lots at 0.215 under the margined convention; the trade has one instrument, no row in `trading.cash_payment_memberships`, and null premium columns;
- a retry returns the first outcome; a retry that differs raises `option_conflict`;
- a paid-up-front request on this product, a margined request on `nymex_on`, and a payment date on a margined request are each refused with `option_booking_input`;
- the Blotter shows one row for the trade and the positions read succeeds;
- an existing paid-up-front booking of the base example still has its premium row and cash membership.

Replace the two assertions in `tools/check_futures_option_lifecycle.py` (`:100-102`, `:139-143`) with the `option_booking_input` refusals above.

- [ ] **Step 2: Verify it fails**

Run the tool on the preview host.
Expected: FAIL with `option_booking_input` on the first booking (the convention is unknown).

- [ ] **Step 3: Write the migration and the API change**

Patch `book_futures_option` at its two refusals (`0166:518`, `0166:543`) and at the input test that requires a payment date (`0166:519-521`), with the anchored pattern.

- [ ] **Step 4: Verify**

Run on the preview host: the tool; `--group exchange`. Run locally: `dotnet run --project tests/OpenEtrm.Api.Checks -- --schedules`.
Expected: all `PASS`.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0395_margined_option_booking.sql src/OpenEtrm.Api tools
git commit -s -m "Book an option with a margined premium and no premium cash"
```

### Task 5: The option margin policy and adoption

**Files:**
- Create: `db/migrations/0396_option_margin.sql`
- Create: `src/OpenEtrm.Api/FuturesOptionMarginStore.cs`, `src/OpenEtrm.Api/LifecycleOptionMarginSettings.cs`
- Modify: `src/OpenEtrm.Api/LifecycleEndpoints.cs:85,92` (two routes), `src/OpenEtrm.Api/LifecycleOperations.cs:118-123,158-161` (option contracts in the policy list)
- Test: `tools/check_margined_option.py`

**Interfaces:**
- Produces:
  - `settlement_data.save_option_margin_policy(catalog uuid, body jsonb) RETURNS jsonb`: permission `WriteMarketDefinitions` with `ReadMarketData`; body as `save_futures_margin_policy` (`0257:228-231`) plus `closingMark`; the contract is a `future_option` contract of a margined product on its own direct daily-settlement curve; exactly one final occurrence, at the series' expiry instant; otherwise `option_margin_policy`. `terms_hash` uses the futures value function with `closingMark` added for this implementation only (the pattern of `0272:258-263`). The same fact saved again returns the existing row.
  - `settlement_data.adopt_option_margin(catalog uuid, body jsonb) RETURNS jsonb`: permission `AmendTrade` with `ReadTrades` and `ReadMarketData`; inserts an adoption with kind `future_option`, binding and instrument the trade's `option_instrument_id`; a holding paid up front, or a policy on another contract, raises `option_margin_policy`.
  - Routes `POST /lifecycle/options/margin/policy` and `POST /lifecycle/options/margin/adopt`.
  - `FuturesOptionMarginStore.ReadPolicy(...)` returning a `FuturesMarginPolicy` with `Implementation` `OptionVersion` and `ClosingMark` set.

- [ ] **Step 1: Add the failing assertions**

- a Market data editor saves the policy (three daily occurrences and the final one); a Trader is refused; saving it again returns the same revision;
- a policy with no final occurrence, with the final occurrence at another instant, on a futures contract, or on a paid-up-front product's option contract is refused with `option_margin_policy`;
- a Trader, a Trade supervisor and a Gas delivery operations account each adopt a holding; a Market data editor is refused;
- adopting a paid-up-front holding is refused with `option_margin_policy`;
- every futures margin policy hash is unchanged.

- [ ] **Step 2: Verify they fail**

Expected: FAIL, the route is not found.

- [ ] **Step 3: Implement the two functions, the store and the routes**

- [ ] **Step 4: Verify**

Run on the preview host: the tool; `--group lifecycle`.
Expected: all `PASS`.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0396_option_margin.sql src/OpenEtrm.Api tools
git commit -s -m "Save an option margin policy and adopt it for a holding"
```

### Task 6: Daily determination, state and cash

**Files:**
- Modify: `db/migrations/0396_option_margin.sql`
- Create: `src/OpenEtrm.Api/FuturesOptionMarginCoordinator.cs`; modify `FuturesOptionMarginStore.cs`
- Modify: `src/OpenEtrm.Api/FuturesOptionPortfolio.cs:111-137,172-190` (supply each margined holding's state)
- Test: `tools/check_margined_option.py`

**Interfaces:**
- Consumes: Task 2's planner; Task 3's state.
- Produces:
  - `settlement_data.determine_option_margin(catalog uuid, body jsonb, steps jsonb, context jsonb) RETURNS jsonb`: permission `AmendTrade`. It checks natively that the steps are the eligible occurrences in order; that each step's evidence is the option contract's final `daily_settlement` observation at the occurrence instant from the policy curve's source and quote, uncorrected at the context cut; that its lots are the booked lots less the closes before it; that the basis chain starts at the trade premium; and the rounded total's increment and delta as `0258:343-345`. It writes one run, the changed determinations and their cash memberships as `0258:364-378`. No step for a due occurrence, or evidence that fails, raises `option_margin_settlement_missing`.
  - `settlement_data.read_option_margin_state(catalog uuid, body jsonb) RETURNS jsonb`: adoption, policy, occurrences, determination heads, open lots and obligations at the read's knowledge.
  - `FuturesOptionMarginCoordinator.Preview` and `.Determine`, the shapes of `FuturesMarginCoordinator.cs:53-140`.
  - `FuturesOptionMarginStore.ReadState(...)` returning `FuturesOptionMarginState`, with `PendingOccurrence` the earliest daily occurrence at or before the valuation instant that has no determination.

- [ ] **Step 1: Add the failing assertions**

- days 1 and 2 determine 625.00 and −1,125.00, each with a cash membership due at its occurrence's due instant;
- at the close where the option settles at 0.240, the Blotter value before adoption is (0.240 − 0.215) × 2500 × 10 = 625.00; once day 1 is determined it is 0.00, and it then moves with the model value less 0.240;
- the trading P&L of the holding over a day equals the change in its model value times 2500 times 10; its margin cash is not added to it;
- with day 1's settlement published and not determined, valuing the adopted holding is refused with `option_margin_state`, and the rest of the book is valued;
- determining with no settlement at the occurrence raises `option_margin_settlement_missing`;
- a retry returns the first outcome; the same snapshot under a new request reports `changed` 0;
- a corrected day 1 settlement restates day 1 and day 2 under a new run revision with adjustments, and a read at the earlier knowledge cutoff shows the earlier determinations.

- [ ] **Step 2: Verify they fail**

Expected: FAIL, function `determine_option_margin` does not exist.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Verify**

Run on the preview host: the tool; `--group lifecycle`.
Expected: all `PASS`; the futures journal tools unedited.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0396_option_margin.sql src/OpenEtrm.Api tools
git commit -s -m "Determine a margined option's daily variation margin and read its state"
```

### Task 7: The closing determination

**Files:**
- Modify: `db/migrations/0396_option_margin.sql`
- Modify: `src/OpenEtrm.Api/OptionLifecycleActions.cs:36-56` (plan and send the close)
- Test: `tools/check_margined_option.py`

**Interfaces:**
- Produces:
  - `lifecycle.option_proposal_base`, for a margined holding's exercise, assignment or expiry: `option_margin_adoption_required` when the holding has no adoption; `option_margin_behind`, naming the occurrence, when a daily occurrence before the event's business instant has no determination. Its details include `marginClose`, the steps the request carried, and they enter the proposal hash.
  - Request member `marginClose`: the steps `FuturesOptionMargin.Plan` gives for the holding with this close added, limited to the new and changed ones.
  - `settlement_data.apply_option_margin_close(catalog uuid, trade uuid, event uuid, steps jsonb, context uuid) RETURNS void`, not executable by the runtime role: checks each step natively (lots closed, basis at the event's instant, mark zero, due instant, increment) and writes the run, the closing determination with `closing_event_id` and `lots`, and its cash membership. A zero determination writes no cash membership.
  - `lifecycle.execute_option_action_pre_schedule_scope` calls it after `security.apply_futures_option_event` and before `lifecycle.enroll_option_future` (`0262:282-290`), in the same transaction.

- [ ] **Step 1: Add the failing assertions**

- exercising 4 lots on day 3 before its occurrence writes one event, one closing determination of −1,950.00 due at day 3's due instant, and the linked future at the strike with its adoption, in one publication;
- day 3 then determines 225.00 on 6 lots; expiry closes them with −3,150.00; the holding's determinations sum to −5,375.00 and its state is closed;
- exercising an unadopted holding is refused with `option_margin_adoption_required`; exercising with day 2 undetermined is refused with `option_margin_behind`; each writes nothing;
- a retried exercise returns its first outcome and writes no second determination;
- a holding booked and exercised before any occurrence closes from 0.215;
- a holding whose last settlement was 0 closes with a zero determination and no cash membership;
- a `marginClose` that the native check does not reproduce is refused with `lifecycle_stale_review`.

- [ ] **Step 2: Verify they fail**

Expected: FAIL, the exercise writes no determination.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Verify**

Run on the preview host: the tool; `--group lifecycle`; `--group exchange`.
Expected: all `PASS`; the existing option lifecycle tools unedited.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0396_option_margin.sql src/OpenEtrm.Api tools
git commit -s -m "Strike a margined option's closing margin with its exercise, assignment or expiry"
```

### Task 8: A close recorded late restates

**Files:**
- Modify: `db/migrations/0396_option_margin.sql`, `src/OpenEtrm.Api/OptionLifecycleActions.cs`
- Test: `tools/check_margined_option.py`, `tests/OpenEtrm.MarketData.Checks/FuturesOptionMarginChecks.cs`

**Interfaces:**
- Produces: `marginClose` may carry restated daily steps for occurrences at or after the event's business instant. `apply_option_margin_close` writes them in the same run as the closing determination, each with `previous_id`, the full new total, `adjustment` and an `adjustment_due` taken from the request's `correctionDue` under the futures rule (`0258:355-360`). A request that leaves a later determination unrestated is refused with `lifecycle_stale_review`.

- [ ] **Step 1: Add the failing assertions**

Example B on the 2,500 MMBtu underlying (premium 0.300; settlements 0.320, 0.290; short 5 lots):

- days 1 and 2 determine −250.00 and 375.00; an assignment of 2 lots dated day 2 before the occurrence is then recorded;
- day 2 restates to 225.00 with adjustment −150.00, and the close is 1,600.00, due at day 2's due instant;
- the cash at each due instant equals what recording the assignment first gives, shown by running both orders in two holdings;
- a read at the knowledge cutoff before the assignment shows day 2 at 375.00 and 5 lots open;
- a corrected day 1 settlement after the assignment restates day 1, day 2 and the close.

- [ ] **Step 2: Verify they fail**

Expected: FAIL with `lifecycle_stale_review`, or a day 2 determination left at 375.00.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Verify**

Run on the preview host: the tool; `check_futures_margin_journal` unedited.
Expected: all `PASS`.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0396_option_margin.sql src tools tests
git commit -s -m "Restate a margined option's later margin when a close is recorded late"
```

### Task 9: The `option.margin` action

**Files:**
- Modify: `db/migrations/0396_option_margin.sql`
- Create: `src/OpenEtrm.Api/OptionMarginLifecycleActions.cs`; modify `src/OpenEtrm.Api/LifecycleOperations.cs:41` (registry)
- Modify: `web/src/lifecycle-model.ts:14,27`, `web/src/lifecycle-settings.ts:17,78-101`
- Test: `tools/check_margined_option.py`, `web/tests/lifecycle-model.test.mjs`

**Interfaces:**
- Produces:
  - `lifecycle.action_kinds` row `('option.margin', true, 'AmendTrade', 'AmendTrade')`; the action list in `lifecycle.record_native_action` (`0263:197`), the check on `automation.lifecycle_profiles` (`0279:87-92`) and `automation.require_lifecycle_action_scope` (`0284:73-104`) admit it; `lifecycle.action_has_delivery` is false for it.
  - `OptionMarginLifecycleActions.Discover`, `.Preview`, `.Execute`, the shapes of `FuturesLifecycleActions.cs:33-82`, over option holdings and their policies.
  - Reason texts in `lifecycle-model.ts` for `option_margin_adoption_required`, `option_margin_behind`, `option_margin_settlement_missing`, `option_margin_state`.

- [ ] **Step 1: Add the failing assertions**

- `lifecycle/discover` lists the adopted holding's next occurrence under `option.margin`, and the review shows its rows in the margin table (occurrence, exact variation, rounded payment, cash adjustment, due, currency);
- under a manual policy a Trader, a Trade supervisor and a Gas delivery operations account each execute it; a Viewer and a Market data editor are refused;
- under an automatic policy the scheduled job determines the occurrence; under review it stores a proposal and writes nothing;
- the web model test: each new reason code has its own text.

- [ ] **Step 2: Verify they fail**

Expected: FAIL with `lifecycle_action_unsupported`.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Verify**

Run on the preview host: the tool; `--group lifecycle`. Run locally: `npm test --prefix web`.
Expected: all `PASS`.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0396_option_margin.sql src/OpenEtrm.Api web tools
git commit -s -m "Run a margined option's margin as a lifecycle action"
```

### Task 10: Blotter, Details and the ticket

**Files:**
- Modify: `src/OpenEtrm.Api/FuturesOptionPositions.cs`, `src/OpenEtrm.Api/FuturesOptionBlotter.cs`
- Modify: `web/src/futures-option-model.ts`, `web/src/futures-option-view.ts:60-65,138-139,211`, `web/src/desk-model.ts:38`
- Test: `web/tests/futures-option-model.test.mjs`, `web/tests/futures-option-details.test.mjs`, `tests/browser/futures-options.mjs:104-118`

**Interfaces:**
- Produces:
  - Option row members `marginStatus` (`not_adopted`, `open`, `closed`, or absent for a trade paid up front), `marginedThrough` and `marginDetermined`.
  - `export function premiumStyleText(style: string): string`: `Paid up front`, `Margined daily on this option's settlement`, or the code.
  - `export function optionBookingRequest(series, entry)`: `convention` and `premiumStyle` from the series; `premiumPaymentDate` only for a series paid up front.

- [ ] **Step 1: Write the failing web tests**

```js
test('the premium style is stated from the product', () => {
  assert.equal(premiumStyleText('futures_style_margin'), "Margined daily on this option's settlement");
  assert.equal(premiumStyleText('premium_paid_up_front'), 'Paid up front');
});
test('a margined booking sends the margined convention and no payment date', () => {
  const request = optionBookingRequest({ ...series, premiumStyle: 'futures_style_margin' }, entry);
  assert.equal(request.convention, 'futures_option_margined_v1');
  assert.equal(request.premiumStyle, 'futures_style_margin');
  assert.ok(!('premiumPaymentDate' in request));
});
test('a paid-up-front booking still sends its payment date', ...);
```

- [ ] **Step 2: Run to verify they fail**

Run: `npm test --prefix web`
Expected: FAIL, `premiumStyleText` is not exported.

- [ ] **Step 3: Implement**

The ticket shows **Premium style** as a fact, not a select. For a margined series the **Premium payment date** row is absent and a disabled **Initial margin** row carries the "Coming soon" tag, built as `futures-option-view.ts:34-37`. Details shows the margin status, the instant margined through and the margin determined in place of the "Premium paid up front" section. Update `tests/browser/futures-options.mjs:104-118`: the disabled option and its refusal text are gone.

- [ ] **Step 4: Run to verify they pass**

Run: `npm test --prefix web`; on the preview host the tool asserts the Blotter row's three members before adoption, after day 1 and after the close.
Expected: all `PASS`.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEtrm.Api web tests/browser tools/check_margined_option.py
git commit -s -m "Show a margined option's premium style and margin on the ticket and the Blotter"
```

### Task 11: Surfaces, strategies and the projection

**Files:**
- Modify: `src/OpenEtrm.Api/VolSurfaceFit.cs:29-58`, `src/OpenEtrm.Api/OptionStrategyRead.cs:216`
- Modify: `src/OpenEtrm.MarketData/Trading/FuturesOptionLifecycleProjection.cs:82-91`, `Trading/FuturesOptionCashValueBinding.cs:14`, `Trading/FuturesOptionPhysicalValueBinding.cs:18`, `src/OpenEtrm.Api/LifecyclePortfolioProjection.Options.cs:13-22`
- Test: `tests/OpenEtrm.Api.Checks/VolSurfaceFitChecks.cs`, `tests/OpenEtrm.MarketData.Checks/OptionLifecycleProjectionChecks.cs`

**Interfaces:**
- Produces: a fit and a strategy analysis for a margined product calibrate and price at a unit discount and record a discount of 1 on the fit; the projection and both reference-strategy bindings raise `projection_option_premium_style` for a margined series.

- [ ] **Step 1: Write the failing checks**

"a margined product's fit records a unit discount and reproduces its settlements"; "a margined series is refused with projection_option_premium_style" in the projection check class.

- [ ] **Step 2: Run to verify they fail**

Run: the Api flag that dispatches `VolSurfaceFitChecks`; `dotnet run --project tests/OpenEtrm.MarketData.Checks -- --option-lifecycle-projection`
Expected: FAIL, the fit discounts with the currency curve.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run to verify they pass**

Expected: both `PASS`.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -s -m "Fit margined option products at a unit discount and refuse them in the projection"
```

### Task 12: Documents and the verification record

**Files:**
- Modify: `docs/futures-options.md` (Booking, Valuation, Lifecycle, Reads, Remaining work), `docs/lifecycle-actions.md` (listed futures options, futures setup), `docs/architecture.md:2278-2296`, `docs/plan.md:2146-2150`, `docs/decisions.md` (D446: piece 3 implemented), `docs/verification.md` (new section "Margined option premium (D446)")

- [ ] **Step 1: State what is implemented and what is not**

Say that the closing term is a named policy term to be confirmed against a clearing house's own description before a real product cites it. The verification section lists each check run, where it ran and its result; a check that has not run is listed as not run.

- [ ] **Step 2: Check and commit**

Run: `python3 -m unittest discover -s tests -p 'test_*.py'`
Expected: `OK`.

```bash
git add docs
git commit -s -m "Document margined option premium and record its verification"
```

### Task 13: Combined review and full checks

- [ ] **Step 1: Run everything that needs no database**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks`, `dotnet run --project tests/OpenEtrm.Api.Checks -- --schedules`, `python3 -m unittest discover -s tests -p 'test_*.py'`, `npm test --prefix web`
Expected: all pass.

- [ ] **Step 2: Run the database groups on the owner's word**

Run on the preview host or in CI: `python3 tools/check_all.py --pg-bin <pg bin> --dotnet <dotnet> --browser <browser>` for groups `exchange`, `lifecycle`, `database`, `managed`, `frontend`, `repository`.
Expected: all pass; record the results in `docs/verification.md`.

- [ ] **Step 3: One independent adversarial review**

A fresh reviewer reads the whole branch diff against the spec, with this brief: that futures margin rows, hashes, guards and functions are unchanged; the order rule and the restatement at each knowledge cutoff; that the native checks reproduce every planned amount; signs for short holdings; the unit discount in value, calibration and fits; that no path collects or skips the premium twice; idempotent retries; each role's permissions; every refusal code. Resolve each finding, rerun the affected checks and record the outcome in `docs/verification.md`.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -s -m "Resolve the review of margined option premium"
```
