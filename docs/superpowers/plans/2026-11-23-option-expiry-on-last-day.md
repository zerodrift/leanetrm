# Option Expiry on the Underlying's Last Trading Day Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status:** Not started. Plan of 2026-11-23 for piece 2 of D446.

**Goal:** A listed futures option may expire at the instant its cash-settled underlying stops trading, be judged by that future's final settlement, and exercise into a future at the strike that its own final margin then settles.

**Architecture:** The product's expiry rule row states its relation to the underlying and its automatic exercise rule names the price it judges by; the two new values are paired. The series trigger, the expiry proposal and the event primitive read those terms. A future booked by an option event may be executed at its last-trading instant, in SQL and in the managed execution record. Nothing in the futures margin action changes.

**Tech Stack:** PostgreSQL migrations (`db/migrations`), C# on .NET 10 (`src/OpenEtrm.MarketData`, `src/OpenEtrm.Api`), TypeScript presentation (`web/src`), Python check tools (`tools/`).

**Spec:** [docs/superpowers/specs/2026-11-23-futures-option-styles-design.md](../specs/2026-11-23-futures-option-styles-design.md), sections "Terms on the versioned rows", "Piece 2: Expiry on the underlying's last trading day", "What stays refused" and "Tests that define done".

This plan does not need the European plan. Its example product is American in style so that it stands alone; the rules here do not read the exercise style.

## Global Constraints

- Relation values `before_last_trading` and `on_last_trading_day`; evaluation values `settlement_moneyness_v1` and `final_settlement_moneyness_v1`.
- The two new values are paired in both directions. Any other pairing raises `option_exercise_rule` when a series is inserted.
- An `on_last_trading_day` series: same business date as the underlying's last-trading milestone, expiry instant equal to that instant, underlying product cash settled. Otherwise `option_expiry`, or `option_expiry_physical_same_day` on a physical underlying.
- Under the final evaluation the deciding observation is the underlying contract's `final_contract_settlement` at the final occurrence of the option profile's margin policy, from that policy's settlement curve source and quote; final, uncorrected, known at the action's cutoff, exactly one.
- A future may be executed at its last-trading instant only when an option event books it. Every other futures booking stays strictly before it.
- Every existing expiry rule row states `before_last_trading`. No other existing row changes. Every existing series keeps its code and identity.
- Migrations use the header and anchored patch pattern of `db/migrations/0382_dated_option_lots.sql`; the reference backfill uses the trigger pattern of `db/migrations/0361_commodity_icons.sql:9-17`.
- No commodity or exchange switch. TypeScript presents backend values only. Synthetic data only.
- Write the failing check first in every task. Commit with `git commit -s`. Do not push and do not open a pull request without the owner's word.
- Migration number 0393 is provisional: take the next free number when building.
- Database, HTTP and real-browser checks run in CI or on the owner's preview host, on the owner's word. Do not install or start PostgreSQL on the workstation.
- One combined independent adversarial review at the end (Task 8), covering temporal behaviour and authorization. No per-task review rounds.

## Review Focus

1. **A short holding.** A short call exercised at the last day books a short future at the strike, and its final margin has the opposite sign of the long's. (Task 5)
2. **A final settlement corrected after expiry was recorded.** The recorded expiry stands; a new expiry proposal for that holding reports `option_expiry_conflict`; the future's margin restates on the corrected price through the existing journal. (Task 5)
3. **A display copy of the reference revision.** Copying a revision that holds an `on_last_trading_day` series succeeds: rules are copied before series and carry the new column. (Task 3)
4. **The scheduled expiry runs before the final settlement is published.** It writes nothing, reports `option_expiry_settlement_missing`, and a later run succeeds. (Task 4)
5. **A clock change on the last day.** The equal-instant rule compares instants, not local times: a series whose close time matches the future's on a day the clock changes is admitted. (Task 3)

---

## File Structure

| File | Responsibility |
| --- | --- |
| `db/migrations/0393_option_expiry_on_last_day.sql` (new) | Relation column and backfill; series trigger; deciding price; booking a future at its close |
| `src/OpenEtrm.MarketData/Trading/FuturesOptionLifecycleProjection.cs:10-37` | The rule record admits the second evaluation and names its observation role |
| `src/OpenEtrm.MarketData/Model.cs:67-74` | The series specification carries the relation |
| `src/OpenEtrm.MarketData/CurveModel.cs:132` | The definition check reads the relation |
| `src/OpenEtrm.MarketData/Trading/Futures.cs:318` | An option-origin execution may sit at the last-trading instant |
| `src/OpenEtrm.Api/PreviewDatabase.cs:1070-1090`, `FuturesStore.cs:310-316`, `FuturesOptionKnownHistoryStore.cs:56` | Materialize the relation, the origin and the deciding role |
| `tools/check_option_last_day_expiry.py` (new) | Database and HTTP checks over a layered synthetic product |

---

### Task 1: The rule and the series state the new terms

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/FuturesOptionLifecycleProjection.cs:10-37`, `src/OpenEtrm.MarketData/Model.cs:67-74`, `src/OpenEtrm.MarketData/CurveModel.cs:132`, `src/OpenEtrm.Api/PreviewDatabase.cs:1070-1090`
- Test: `tests/OpenEtrm.MarketData.Checks/OptionLifecycleProjectionChecks.cs`, `tests/OpenEtrm.MarketData.Checks/FuturesOptionChecks.cs`

**Interfaces:**
- Produces:
  - `FuturesOptionAutomaticRule.DailySettlementMoneyness = "settlement_moneyness_v1"`, `.FinalSettlementMoneyness = "final_settlement_moneyness_v1"`; `public ObservationRole DecidingRole` (`DailySettlement` or `FinalContractSettlement`). Any other evaluation still raises `option_exercise_rule`.
  - `FuturesOptionExpiryRelations.BeforeLastTrading = "before_last_trading"`, `.OnLastTradingDay = "on_last_trading_day"`.
  - `public required string ExpiryRelation { get; init; }` on `FuturesOptionSeriesSpecification`, materialized from the product's expiry rule row.
  - `CurveModel.RequireFuturesOptionDefinition`: before-relation needs expiry strictly before the underlying's last trading; on-last-day needs them equal; anything else `option_definition`.

- [ ] **Step 1: Write the failing checks**

```csharp
Check("a rule judged by the final settlement decides by the same arithmetic and names the final settlement role", () => {
    var rule = new FuturesOptionAutomaticRule(key, 1, "final_settlement_moneyness_v1", ExactDecimal.Parse("0"), "abandon", "abandon");
    Equal(rule.Decide(OptionRight.Call, ExactDecimal.Parse("3.10"), ExactDecimal.Parse("3.35")), "exercised");
    Equal(rule.Decide(OptionRight.Call, ExactDecimal.Parse("3.10"), ExactDecimal.Parse("3.10")), "abandoned");
    Equal(rule.Decide(OptionRight.Put, ExactDecimal.Parse("3.10"), ExactDecimal.Parse("3.35")), "abandoned");
    Equal(rule.DecidingRole.ToString(), ObservationRole.FinalContractSettlement.ToString());
});
Check("an unknown evaluation is still refused", () => Reject("option_exercise_rule", ...));
Check("a series on its underlying's last day is defined only at the same instant", () => {
    Accept(series with { ExpiryRelation = "on_last_trading_day" }, expiry: lastTrading);
    Reject("option_definition", /* on_last_trading_day, expiry one second before lastTrading */);
    Reject("option_definition", /* before_last_trading, expiry == lastTrading */);
    Reject("option_definition", /* either relation, expiry after lastTrading */);
});
Check("the lifecycle projection still refuses an option that does not expire before its underlying", () => Reject("projection_option_state", ...));
```

Every existing construction of `FuturesOptionSeriesSpecification` in the checks states `ExpiryRelation = "before_last_trading"`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -- --option-lifecycle-projection` and `-- --futures-options`
Expected: build error, `ExpiryRelation` not defined.

- [ ] **Step 3: Implement**

`PreviewDatabase.OptionSeries` reads `underlying_relation` from the product's expiry rule row (`expiry_rule_code`); a row without it raises `option_definition`.

- [ ] **Step 4: Run to verify they pass**

Run: both flags, then `-- --cash-option-model`, `-- --physical-option`, `-- --option-strategies`
Expected: all `PASS`.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEtrm.MarketData src/OpenEtrm.Api/PreviewDatabase.cs tests/OpenEtrm.MarketData.Checks
git commit -s -m "State an option's expiry relation and the price its automatic rule judges by"
```

### Task 2: A future booked by an option event may sit at its close

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/Futures.cs:300-330` (`FuturesExecution`), `src/OpenEtrm.Api/FuturesStore.cs:310-316` (`MaterializeHolding`), `src/OpenEtrm.Api/FuturesOptionKnownHistoryStore.cs:63`
- Test: `tests/OpenEtrm.MarketData.Checks/FuturesMarginChecks.cs`, `tests/OpenEtrm.MarketData.Checks/FuturesMarginTrajectoryChecks.cs`

**Interfaces:**
- Produces:
  - `public enum FuturesExecutionOrigin { Market = 1, OptionEvent }`.
  - `FuturesExecution` takes the origin as an explicit constructor argument, with no default. `Market` keeps today's window `[EligibleFrom, Contract.Expiry)`. `OptionEvent` also admits `executedAt == Contract.Expiry`. Later instants raise `futures_execution` for both.
  - Retained futures rows are materialized with `OptionEvent` exactly when a row of `trading.futures_option_events` names the trade as its `futures_trade_id`.

- [ ] **Step 1: Write the failing checks**

Hand values: strike 3.10, final settlement 3.35, 4 lots of 2,500 MMBtu.

```csharp
Check("a market execution at the last-trading instant is refused and an option-origin one is admitted", () => {
    Reject("futures_execution", () => Execution(FuturesExecutionOrigin.Market, at: expiry));
    _ = Execution(FuturesExecutionOrigin.OptionEvent, at: expiry);
    Reject("futures_execution", () => Execution(FuturesExecutionOrigin.OptionEvent, at: expiry.AddSeconds(1)));
});
Check("a future opened at the strike at its close is settled by one final step", () => {
    // policy: opening IncludeOpeningInstant, one final occurrence at expiry; final_contract_settlement 3.35 observed at expiry
    var trajectory = Plan(execution: "3.10", lots: 4);
    Equal(trajectory.Steps.Length.ToString(), "1");
    Equal(trajectory.Steps[0].Variation.Amount.ToString(), "2500.00");      // (3.35 − 3.10) × 2500 × 4
    Equal(trajectory.Basis.Status.ToString(), FuturesMarginStatus.FinalMarginDetermined.ToString());
});
Check("the same future under after_opening_instant has no eligible occurrence and is refused", ...);   // futures_margin_schedule
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks` (the default pass runs the margin checks)
Expected: build error, `FuturesExecutionOrigin` not defined.

- [ ] **Step 3: Implement**

Every existing construction of `FuturesExecution` states `FuturesExecutionOrigin.Market`. If the second check fails after the build compiles, the planner refuses an inception at the final instant; make `FuturesContract.PlanMarginTrajectory` admit it under `IncludeOpeningInstant` and leave `FuturesMargin.Determine` as it is, since no live path calls it.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks`; `dotnet run --project tests/OpenEtrm.Api.Checks -- --futures-margin-adoption`
Expected: all `PASS`.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -s -m "Admit a future booked by an option event at its last-trading instant"
```

### Task 3: Reference rows and series admission

**Files:**
- Create: `db/migrations/0393_option_expiry_on_last_day.sql`
- Create: `tools/check_option_last_day_expiry.py`
- Modify: `tools/exchange_fixture.py:175` (the rule insert states the relation for expiry rules), `tools/check_all.py:131-132` (register the new tool)

**Interfaces:**
- Produces:
  - Column `ref_data.futures_option_rules.underlying_relation text`, with constraint `futures_option_rules_underlying_relation_check`: `(kind='expiry')=(underlying_relation IS NOT NULL) AND(underlying_relation IS NULL OR underlying_relation IN('before_last_trading','on_last_trading_day'))`.
  - `ref_data.require_futures_option_series()` replaced: reads the product's expiry and automatic rule rows; refusals `option_exercise_rule`, `option_expiry`, `option_expiry_physical_same_day` as in Global Constraints; its earlier refusals unchanged.

- [ ] **Step 1: Write the failing check tool**

`tools/check_option_last_day_expiry.py` follows `tools/check_cash_option_value.py:36-80`. Its layered bundle on the exchange fixture: source `synthetic_last_day_option`; `ice_cfb`'s curve flipped to `daily_settlement` as that file does; rules `synthetic_last_day_expiry_v1` (`on_last_trading_day`) and `synthetic_final_settlement_v1` (`final_settlement_moneyness_v1`, threshold 0, at the money both `abandon`); product `synthetic_last_day_option` cloned from `nymex_on` with those rules, underlying `ice_cfb`, multiplier 2500; an option-expiry close time equal to `ice_cfb`'s last-trading close on the same clock; a call and a put at strike 3.10 on one `ice_cfb` series, expiring on its last trading date. Storage assertions:

- the series inserts; every base example series keeps its code and count, and every base expiry rule row reads `before_last_trading`;
- a rule row of kind `expiry` without a relation, and a rule row of another kind with one, are refused by `futures_option_rules_underlying_relation_check`;
- the matrix: relation × (date before, on, after the underlying's last day) × (instant before, equal, after) × (cash, physical underlying) × (each evaluation), with the refusal named for every refused cell;
- a display copy of the revision (`security` reference display editing, as `tools/check_reference_reads.py` drives it) succeeds and the copied series reads the same relation;
- a series whose close time matches the future's on a day its clock changes is admitted.

- [ ] **Step 2: Verify the tool fails before the migration**

Run on the preview host, on the owner's word: `python3 tools/check_option_last_day_expiry.py --pg-bin <pg bin> --dotnet <dotnet> --no-build`
Expected: FAIL, column `underlying_relation` does not exist.

- [ ] **Step 3: Write this part of the migration**

Add the column; disable `guard_definition`, set `before_last_trading` where `kind='expiry'`, enable it; add the constraint; replace the trigger function. The function's on-last-day branch compares `NEW.expiry_at` with the milestone's `event_at` for equality and `NEW.last_trading_date` with its `business_date`.

- [ ] **Step 4: Verify**

Run locally: `python3 -m unittest discover -s tests -p 'test_*.py'`.
Run on the preview host: the tool; `python3 tools/check_all.py --pg-bin <pg bin> --dotnet <dotnet> --browser <browser> --group exchange`.
Expected: the tool's storage lines `PASS`; the group passes, including `tools/check_futures_option_storage.py:111-113` unedited.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0393_option_expiry_on_last_day.sql tools
git commit -s -m "Admit an option series that expires when its cash-settled future stops trading"
```

### Task 4: Expiry is judged by the price the rule names

**Files:**
- Modify: `db/migrations/0393_option_expiry_on_last_day.sql`
- Modify: `src/OpenEtrm.Api/FuturesOptionKnownHistoryStore.cs:56`, `src/OpenEtrm.Api/FuturesOptionStore.cs:208-209`
- Test: `tools/check_option_last_day_expiry.py`, `tests/OpenEtrm.Api.Checks` (`--lifecycle-option-expiry-read`)

**Interfaces:**
- Produces:
  - `security.futures_option_automatic_outcome(rule, option_right, strike, settlement)` accepts both evaluations; any other still raises `option_exercise_rule_unknown`.
  - `security.futures_option_deciding_role(rule ref_data.futures_option_rules) RETURNS text`: `daily_settlement` or `final_contract_settlement`; otherwise `option_exercise_rule_unknown`.
  - `lifecycle.option_proposal_base` and `security.apply_futures_option_event` read the deciding observation by that role. Under the final role they read it at the final occurrence of the profile's margin policy, from that policy's settlement curve; today's read is unchanged under the daily role.
  - `lifecycle.save_option_profile` and `lifecycle.require_option_reference`: under the final evaluation `expiry_curve` must equal the margin policy's `settlement_curve_code` (`lifecycle_option_source`).
  - `lifecycle.read_option_known_history_pre_risk` and `FuturesOptionKnownHistoryStore` accept an expiry event whose settlement has the role its recorded rule names.
  - The proposal's details gain `decidingRole`.

- [ ] **Step 1: Add the failing assertions**

Hand values: strike 3.10, a long call and a long put of 4 lots each; final settlement published after the close.

- before publication, and at a context cut before its publication though it now exists, the expiry proposal is refused with `option_expiry_settlement_missing` and nothing is written;
- the scheduled expiry job run before publication writes nothing and reports the same code; run again after publication it records the expiry;
- final settlement 3.35: the call is `exercised`, the put `abandoned`; 2.95: the reverse; 3.10: both `abandoned`;
- the event records the final settlement observation's id, and `decidingRole` reads `final_contract_settlement`;
- a profile whose expiry curve is not the margin policy's settlement curve is refused with `lifecycle_option_source`;
- a second final observation from the same source at that instant, neither correcting the other, gives `option_expiry_settlement_ambiguous`;
- the known-history read returns the recorded expiry.

- [ ] **Step 2: Verify they fail**

Run the tool on the preview host.
Expected: FAIL at the first expiry assertion with `option_exercise_rule_unknown`.

- [ ] **Step 3: Extend the migration**

`CREATE OR REPLACE` the outcome function and add the role function. Patch the others with the anchored pattern:

| Function | Anchor |
| --- | --- |
| `lifecycle.option_proposal_base(uuid,jsonb)` | `(catalog,r.definition_revision_id,future.id,'daily_settlement',underlying_curve.direct_source_code,underlying_curve.quote_code)` and `AND security.futures_option_market_date(future_product,o.observed_at)=s.last_trading_date AND o.observed_at<=at` |
| `security.apply_futures_option_event(...)` | `AND o.role='daily_settlement' AND o.publication_state='final' AND security.market_fact_visible(catalog,o.definition_revision_id,o.id)` and `AND security.futures_option_market_date(future_product,o.observed_at)=series_row.last_trading_date;` |
| `lifecycle.save_option_profile(uuid,jsonb)`, `lifecycle.require_option_reference` | `AND cc.rule='direct' AND cc.direct_role='daily_settlement' AND(cc.reference_catalog_id,cc.reference_revision_id,cc.quote_code)=(m.reference_catalog_id,m.reference_revision_id,m.quote_code)) THEN RAISE EXCEPTION 'lifecycle_option_source'` |
| `lifecycle.read_option_known_history_pre_risk` | its `daily_settlement` role test (`0262:457-458`) |

In both expiry reads the rule row is loaded before the observation, into its own variable, so the event's `rule_code` is still set only when the rule decided.

- [ ] **Step 4: Verify**

Run on the preview host: the tool; `--group lifecycle`. Run locally: `dotnet run --project tests/OpenEtrm.Api.Checks -- --lifecycle-option-expiry-read`.
Expected: all `PASS`; the existing option expiry tools pass unedited.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0393_option_expiry_on_last_day.sql src/OpenEtrm.Api tools tests
git commit -s -m "Judge a last-day option by its future's final settlement"
```

### Task 5: The linked future is booked at the close and finally margined

**Files:**
- Modify: `db/migrations/0393_option_expiry_on_last_day.sql`
- Modify: the query behind `FuturesStore.MaterializeHolding` (`src/OpenEtrm.Api/FuturesStore.cs:310-316`) to return the origin
- Test: `tools/check_option_last_day_expiry.py`

**Interfaces:**
- Consumes: Task 2's `FuturesExecutionOrigin`.
- Produces:
  - `security.prepare_futures_binding(..., ordinal integer, option_origin boolean)` and `security.record_futures_trade(..., ordinal integer, option_origin boolean)`: a trailing explicit argument; the earlier signatures are dropped and every caller states it. With `option_origin`, `executed` and `valuation` may equal the last-trading instant; without it, today's refusal.
  - `security.apply_futures_option_event` passes `true`; `book_futures_with_parties` and `amend_futures_with_parties` pass `false`.
  - `lifecycle.enroll_option_future` raises `lifecycle_option_margin_policy` when the policy's final occurrence is at the execution instant and its opening is not `include_opening_instant`.

- [ ] **Step 1: Add the failing assertions**

Margin policy as `tools/lifecycle_option_client.py:44-47` builds it: opening `include_opening_instant`, one final occurrence at the future's expiry. Hand values as Task 2.

- the exercised call books a future of +4 lots at 3.10, executed at the last-trading instant, in the option's book and parties, adopted in the same transaction;
- `futures.margin` on that future determines one final step of 2,500.00 and the future's state is final; the observation it pays on is the observation the expiry recorded;
- a short call of 4 lots books a future of −4 lots and its final margin is −2,500.00;
- the same walk with a policy opening `after_opening_instant` is refused at execution with `lifecycle_option_margin_policy`, and nothing is written;
- an ordinary futures booking at the last-trading instant is still refused with `futures_execution` (`tools/check_futures_storage.py:75` and `tools/check_futures_preview.py:51` pass unedited);
- the Blotter and the positions read of the book succeed with the linked future present;
- each role of the spec's Roles table gets the outcome the table states for saving the margin policy, saving the profile, recording the expiry and determining the future's margin;
- after the final settlement is corrected, a new expiry proposal for the holding reports `option_expiry_conflict`, the recorded expiry is unchanged, and `futures.margin` restates the future's final step on the corrected price.

- [ ] **Step 2: Verify they fail**

Run the tool on the preview host.
Expected: FAIL at the first assertion with `futures_execution`.

- [ ] **Step 3: Extend the migration and the read**

Patch `prepare_futures_binding` at the anchor `executed>=ends OR executed>valuation OR valuation<starts OR valuation>=ends`. Patch `enroll_option_future` at `OR r.execution_at<p.from_at OR r.execution_at>=p.until_at`.

- [ ] **Step 4: Verify**

Run on the preview host: the tool; `--group lifecycle`; `--group exchange`.
Expected: all `PASS`.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0393_option_expiry_on_last_day.sql src/OpenEtrm.Api tools
git commit -s -m "Book an exercised last-day option's future at the close and settle it by its final margin"
```

### Task 6: The catalog and the ticket state the relation

**Files:**
- Modify: `src/OpenEtrm.Api/FuturesOptionStore.cs:104-131` (`Describe`)
- Modify: `web/src/futures-option-model.ts:10-13`, `web/src/futures-option-view.ts:58-59,85-87,205`
- Test: `web/tests/futures-option-model.test.mjs`, `web/tests/futures-option-lifecycle-browser.test.mjs:26-46`

**Interfaces:**
- Produces: catalog series member `expiryRelation`; `export function expiryRelationText(relation: string): string` in `futures-option-model.ts`.

- [ ] **Step 1: Write the failing web tests**

```js
test('the expiry fact states its relation to the future', () => {
  assert.equal(expiryRelationText('on_last_trading_day'), "on the future's last trading day");
  assert.equal(expiryRelationText('before_last_trading'), "before the future's last trading day");
  assert.equal(expiryRelationText('something_else'), 'something_else');
});
```

Extend the Expiry-fact test in `futures-option-lifecycle-browser.test.mjs` to expect the instant followed by ` · ` and the relation text.

- [ ] **Step 2: Run to verify they fail**

Run: `npm test --prefix web`
Expected: FAIL, `expiryRelationText` is not exported.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run to verify they pass**

Run: `npm test --prefix web`; on the preview host the tool asserts `options/catalog` returns `expiryRelation` for the synthetic series and for a base series.
Expected: all `PASS`.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEtrm.Api/FuturesOptionStore.cs web tools/check_option_last_day_expiry.py
git commit -s -m "Show when an option expires relative to its future"
```

### Task 7: Documents and the verification record

**Files:**
- Modify: `docs/futures-options.md` (Instrument, Lifecycle, Remaining work), `docs/exchange-futures.md:97-160` (rules, option lifecycle; name the lifecycle entry points now in use), `docs/lifecycle-actions.md` (listed futures options), `docs/decisions.md` (D446: piece 2 implemented), `docs/verification.md` (new section "Option expiry on the underlying's last trading day (D446)")

- [ ] **Step 1: State what is implemented and what is not**

The verification section lists each check run, where it ran and its result; a check that has not run is listed as not run.

- [ ] **Step 2: Check and commit**

Run: `python3 -m unittest discover -s tests -p 'test_*.py'`
Expected: `OK`.

```bash
git add docs
git commit -s -m "Document option expiry on the underlying's last trading day"
```

### Task 8: Combined review and full checks

- [ ] **Step 1: Run everything that needs no database**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks`, `dotnet run --project tests/OpenEtrm.Api.Checks -- --schedules`, `python3 -m unittest discover -s tests -p 'test_*.py'`, `npm test --prefix web`
Expected: all pass.

- [ ] **Step 2: Run the database groups on the owner's word**

Run on the preview host or in CI: `python3 tools/check_all.py --pg-bin <pg bin> --dotnet <dotnet> --browser <browser>` for groups `exchange`, `lifecycle`, `database`, `managed`, `frontend`, `repository`.
Expected: all pass; record the results in `docs/verification.md`.

- [ ] **Step 3: One independent adversarial review**

A fresh reviewer reads the whole branch diff against the spec, with this brief: the knowledge cutoff at expiry; that the deciding observation is the one final margin pays on; that only an option event can book a future at its close, in SQL and in managed code; the backfill of sealed reference rows; display copies; every refusal code; that no existing series, booking or margin record changes. Resolve each finding, rerun the affected checks and record the outcome in `docs/verification.md`.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -s -m "Resolve the review of option expiry on the underlying's last trading day"
```
