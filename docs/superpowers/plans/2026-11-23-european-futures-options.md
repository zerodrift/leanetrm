# European Futures Options Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status:** Built locally on 2026-11-23 as migrations 0391 and 0392. The managed, Python, web, database and HTTP checks pass; the real-browser checks have not been run. See [verification](../../verification.md#european-futures-options-d446). Plan of 2026-11-23 for piece 1 of D446.

**Goal:** A listed futures option product may state European exercise; such a series is booked, valued by a Black-76 formula, calibrated to its settlement, repriced under shared scenarios and expired by its automatic rule, and is never valued on the American tree.

**Architecture:** The option stays the one leaf instrument keyed by its series. The series' stated style selects the model through a small dispatcher; the American tree's code and values do not change. The valuation policy gains an optional `european` member, and a new stated policy carries it. Two migrations admit the style in reference data and lifecycle, and in the stored volatility surfaces.

**Tech Stack:** C# on .NET 10 (`src/OpenEtrm.MarketData`, `src/OpenEtrm.Api`), PostgreSQL migrations (`db/migrations`), TypeScript presentation (`web/src`), Python check tools (`tools/`).

**Spec:** [docs/superpowers/specs/2026-11-23-futures-option-styles-design.md](../specs/2026-11-23-futures-option-styles-design.md), sections "Terms on the versioned rows", "Piece 1: European exercise", "What stays refused" and "Tests that define done".

## Global Constraints

- Model id `european_futures_option_black76_v1`; day count `act_365_fixed_instant_v1`; volatility bracket 0.01 to 3, never lowered; at most 30 halvings; calibration within half the series' clearing increment; discount tolerance 0.005.
- Fallback name `option_settlement_at_discounted_intrinsic_v1`, or `none`.
- Arithmetic is `LatticeDecimal` (30 digits, half-even) with the project's `Ln`, `Sqrt` and `NormalDistribution.Cdf`. No library is added. The unit price is rounded once, half-even, to 18 digits before the multiplier.
- The American tree's values are unchanged: `PinnedDigest`, `HostIndependentDigest` and `CentredPinnedDigest` pass with no edit to their expected values.
- A serialized `FuturesOptionValuationPolicy` without the new member is byte-identical to today's. `Release1` is not edited.
- No existing row is rewritten. Migrations use the header and the anchored patch pattern of `db/migrations/0382_dated_option_lots.sql`: the anchor must occur exactly once or the migration raises.
- Refusal codes are exactly those in the spec's "What stays refused" table.
- TypeScript presents backend values only.
- Synthetic data only. No customer, vendor or competing product names in code, fixtures, documents or commit messages.
- Write the failing check first in every task. Commit with `git commit -s`. Do not push and do not open a pull request without the owner's word.
- Migration numbers 0391 and 0392 and decision D446 are provisional: take the next free numbers when building.
- Database, HTTP and real-browser checks need a disposable PostgreSQL environment. They run in CI or on the owner's preview host, on the owner's word. Do not install or start PostgreSQL on the workstation.
- One combined independent adversarial review at the end (Task 10), covering numerical and temporal behaviour. No per-task review rounds.

## Review Focus

1. **Seconds from expiry.** A valuation 60 seconds before expiry, in and out of the money, returns the discounted intrinsic value or zero within 10⁻¹², with no numeric range error. (Task 1)
2. **A zero settlement.** An out-of-the-money series settling at 0 calibrates to the declared fallback with value 0 at every scenario, and is refused when the fallback is `none`. (Task 2)
3. **A mixed book.** American and European series on the same future under one policy are each valued by their own model, and each calibration receipt names its numerical policy. (Task 4)
4. **Replay.** A retained job with a European series recomputes its calibration and refuses a different result with `risk_option_calibration`. (Task 4)
5. **Expiry outside the rate curve.** A European series whose expiry lies outside the selected curve's pillars is listed with `discount_coverage` and no value, and the rest of the book is valued. (Task 6)

---

## File Structure

| File | Responsibility |
| --- | --- |
| `src/OpenEtrm.MarketData/Options/EuropeanFuturesOptionModel.cs` (new) | The Black-76 formula and its input checks |
| `src/OpenEtrm.MarketData/Options/FuturesOptionModels.cs` (new) | Chooses the model for a series' style; the only caller of either model from pricing code |
| `src/OpenEtrm.MarketData/Options/AmericanFuturesOptionModel.cs` | `FuturesOptionTerms` admits two styles; the policy registry gains the formula's policy; the tree refuses a European series |
| `src/OpenEtrm.MarketData/Options/ImpliedVolatility.cs` | Solves through the dispatcher; European floor, tolerance and fallback |
| `src/OpenEtrm.MarketData/Trading/FuturesOptions.cs` | Binding and instrument use the dispatcher; no price step for the formula |
| `src/OpenEtrm.Api/FuturesOptionPortfolio.cs` | `european` policy member, `Release2`, per-series policy choice |
| `db/migrations/0391_european_futures_options.sql` (new) | Style and instruction checks; early-exercise refusal |
| `db/migrations/0392_european_option_surfaces.sql` (new) | Surface tables and functions accept the formula's policy for a European product |
| `tools/check_european_option.py` (new) | Database and HTTP checks over a layered synthetic product |
| `tests/OpenEtrm.MarketData.Checks/EuropeanFuturesOptionChecks.cs` (new) | Managed checks, flag `--european-options` |

---

### Task 1: The Black-76 formula

**Files:**
- Create: `src/OpenEtrm.MarketData/Options/EuropeanFuturesOptionModel.cs`
- Modify: `src/OpenEtrm.MarketData/Options/AmericanFuturesOptionModel.cs:12-19` (terms), `:21-31` (`OptionLattice`), `:39-80` (policy record and registry), `:137` (`ValidateInputs`)
- Create: `tests/OpenEtrm.MarketData.Checks/EuropeanFuturesOptionChecks.cs`
- Modify: `tests/OpenEtrm.MarketData.Checks/Program.cs:101` (add the flag beside `--futures-options`, and the default pass), `tests/OpenEtrm.MarketData.Checks/FuturesOptionChecks.cs:603` (`RejectsUnsupportedTerms`)

**Interfaces:**
- Produces:
  - `FuturesOptionTerms.American = "american"`, `FuturesOptionTerms.European = "european"`; the constructor admits both and raises `option_terms` otherwise.
  - `OptionLattice.ClosedForm` (third enum value).
  - `OptionNumericalPolicy.DiscountTolerance` (`LatticeDecimal?`, init-only, null for the trees).
  - `OptionNumericalPolicies.Black76 = "european_futures_option_black76_v1"`; `Require(Black76)` returns `new(Black76, OptionLattice.ClosedForm, 0, ActualOver365FixedInstant, 0.01, 3, 0, 30) { DiscountTolerance = 0.005 }`.
  - `public static LatticeDecimal EuropeanFuturesOptionModel.UnitPrice(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal volatility, LatticeDecimal discountToExpiry, DateTimeOffset valuationAt, OptionNumericalPolicy policy)`
  - `public static LatticeDecimal EuropeanFuturesOptionModel.ZeroVolatilityValue(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal discountToExpiry)` (discount times intrinsic).
  - `internal static LatticeDecimal EuropeanFuturesOptionModel.ValidateInputs(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal discountToExpiry, DateTimeOffset valuationAt, OptionNumericalPolicy policy)` returning years.

- [ ] **Step 1: Write the failing checks**

In `EuropeanFuturesOptionChecks.cs`, a `public static void Run()` registering these with the `Check(label, method)` helper used in `FuturesOptionChecks.cs:1408`. Build terms and instants as `FuturesOptionChecks.Price` does (`:155-159`), with style `"european"`; load cases with the same reader as `Black76Cases()` (`:139-148`).

```csharp
Check("the Black-76 formula equals the independent oracle to twenty decimal places", () => {
    foreach (var c in Black76Cases()) {
        Within(European(c, OptionRight.Call, c.Discount), L(c.Call), L("0.00000000000000000001"), "call");
        Within(European(c, OptionRight.Put, c.Discount), L(c.Put), L("0.00000000000000000001"), "put");
        Within(European(c, OptionRight.Call, "1"), L(c.UndiscountedCall), L("0.00000000000000000001"), "undiscounted call");
        Within(European(c, OptionRight.Put, "1"), L(c.UndiscountedPut), L("0.00000000000000000001"), "undiscounted put");
    }
});
Check("put-call parity holds: call minus put is the discount times futures minus strike", ...);   // same bound
Check("the formula rises with volatility and never falls below the discounted intrinsic value", ...);
Check("sixty seconds from expiry the formula returns discounted intrinsic in the money and zero out of it", () => {
    // F=5, K=3, D=0.99, sigma=0.5, expiry = ValuationAt + 60 s
    Within(price(OptionRight.Call), L("1.98"), L("0.000000000001"), "in the money");
    Within(price(OptionRight.Put), L("0"), L("0.000000000001"), "out of the money");
});
Check("the formula refuses an American series, nonpositive inputs, a discount above one and an expired valuation", () => {
    Reject("option_terms", () => EuropeanFuturesOptionModel.UnitPrice(american, ...));
    Reject("option_input", /* futures 0; volatility 0; discount 0; discount 1.01 */);
    Reject("option_expired", /* valuationAt == expiry */);
    Reject("option_numerical_policy", /* the CRR policy passed to the formula */);
});
Check("the tree refuses a European series", () =>
    Reject("option_terms", () => AmericanFuturesOptionModel.UnitPrice(european, L("3"), L("0.5"), L("1"), ValuationAt, OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein), CancellationToken.None)));
Check("the wide oracle cases price without a range error", ...);   // black76_wide, undiscounted_call
```

Change `RejectsUnsupportedTerms` (`FuturesOptionChecks.cs:603`) to reject the style `"bermudan"` instead of `"european"`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -- --european-options`
Expected: build error, `EuropeanFuturesOptionModel` not defined.

- [ ] **Step 3: Implement the terms, the policy and the formula**

The formula is the spec's, with `T` from `AmericanFuturesOptionModel.YearFraction`. `LatticeDecimal` has no unary minus: write `N(−x)` as `One − N(x)`. `AmericanFuturesOptionModel.ValidateInputs` gains one line raising `option_terms` for a style other than American; nothing else in the tree changes.

- [ ] **Step 4: Run to verify they pass, and that the tree is unchanged**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -- --european-options` then `-- --futures-options`
Expected: every line `PASS`, including `PinnedDigest` and `HostIndependentDigest` with unedited expected values.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEtrm.MarketData/Options tests/OpenEtrm.MarketData.Checks
git commit -s -m "Price European futures options with a Black-76 formula"
```

### Task 2: Calibration through one dispatcher

**Files:**
- Create: `src/OpenEtrm.MarketData/Options/FuturesOptionModels.cs`
- Modify: `src/OpenEtrm.MarketData/Options/ImpliedVolatility.cs:39-93`
- Test: `tests/OpenEtrm.MarketData.Checks/EuropeanFuturesOptionChecks.cs`

**Interfaces:**
- Consumes: Task 1's model, terms constants and policy.
- Produces:
  - `public static void FuturesOptionModels.RequireStyle(FuturesOptionTerms terms, OptionNumericalPolicy policy)`: `option_terms` unless the policy prices the style (`ClosedForm` with European; a lattice with American).
  - `public static LatticeDecimal FuturesOptionModels.UnitPrice(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal volatility, LatticeDecimal discountToExpiry, DateTimeOffset valuationAt, OptionNumericalPolicy policy, CancellationToken ct)`
  - `public static LatticeDecimal? FuturesOptionModels.PriceStep(...)` with the parameters of `AmericanFuturesOptionModel.PriceStep`; null for the formula.
  - `public static LatticeDecimal FuturesOptionModels.Floor(FuturesOptionTerms terms, LatticeDecimal futures, LatticeDecimal discountToExpiry)`: intrinsic for American, discounted intrinsic for European.
  - `ImpliedVolatility.DiscountedFallbackMode = "option_settlement_at_discounted_intrinsic_v1"`; `ImpliedVolatility.FallbackModeFor(FuturesOptionTerms terms)`.
  - `ImpliedVolatility.Solve` keeps its signature. For a European series `IntrinsicFallback.Intrinsic` holds the discounted base intrinsic and `Residual` may be negative.

- [ ] **Step 1: Write the failing checks**

Each uses clearing increment 0.001 and the formula's policy. Hand values: `F = 5`, `K = 3`, `D = 0.99`, call; discounted intrinsic `1.98`; refusal bound `(0.99 − 0.005) × 2 − 0.001 = 1.969`.

```csharp
Check("European implied volatility reprices each oracle settlement within half an increment", ...);
    // settlement = oracle call rounded half-even to 0.001; SolvedVolatility; |Residual| <= 0.0005; UpperBound == 3
Check("a settlement at discounted intrinsic falls back only when declared", () => {
    var fallback = (IntrinsicFallback)Solve(settlement: "1.98", fallbackDeclared: true);
    Equal(fallback.Intrinsic.ToString(), L("1.98").ToString()); Equal(fallback.Residual.ToString(), L("0").ToString());
    Reject("option_calibration_fallback_undeclared", () => Solve("1.98", fallbackDeclared: false));
});
Check("a deep in-the-money settlement struck at a slightly higher rate falls back with a negative residual", () =>
    Equal(((IntrinsicFallback)Solve("1.975", true)).Residual.ToString(), L("-0.005").ToString()));
Check("a settlement beyond the discount tolerance is refused", () =>
    Reject("option_settlement_below_intrinsic", () => Solve("1.96", true)));
Check("an out-of-the-money zero settlement falls back to zero, and is refused when no fallback is declared", ...);
Check("a settlement above the price at volatility three is refused", () => Reject("option_settlement_above_bound", ...));
Check("the American solver's outcomes are unchanged", ...);   // rerun three cases of ImpliedVolatilityRoundTrip through Solve and compare to their existing expected values
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -- --european-options`
Expected: FAIL at the first new check, `option_terms` from the tree.

- [ ] **Step 3: Implement the dispatcher and route `Solve` through it**

`Solve` takes its price function, its floor and its upper bound from `FuturesOptionModels`. The tree's range cap (`UpperBound`, `CentredUpperBound`) applies only to a lattice policy. The below-intrinsic test for a European series uses `(D − policy.DiscountTolerance) × intrinsic − increment`; for an American series it is today's.

- [ ] **Step 4: Run to verify they pass**

Run: `-- --european-options` and `-- --futures-options`
Expected: all `PASS`.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEtrm.MarketData/Options tests/OpenEtrm.MarketData.Checks
git commit -s -m "Calibrate European futures options to their settlement"
```

### Task 3: The instrument and its binding

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/FuturesOptions.cs:57-78` (`Declare`, `ResolvePriceSteps`, `PriceSteps`), `:155-167` (`UnitPrice`), `:175-191` (`Intrinsic`, `Receipt`), `:209-238` (`Prepare`)
- Test: `tests/OpenEtrm.MarketData.Checks/EuropeanFuturesOptionChecks.cs`

**Interfaces:**
- Consumes: `FuturesOptionModels` from Task 2.
- Produces: `FuturesOptionMarketBinding.UnitPrice` and `FuturesOptionPricing.Prepare` keep their signatures. A binding whose policy does not price its series' style raises `option_terms` in `Prepare`. A European binding records no tree price and its receipt's `Mode` is `solved` or `option_settlement_at_discounted_intrinsic_v1`.

- [ ] **Step 1: Write the failing checks**

Reuse the market helpers of `FuturesOptionChecks` (`BasePvEqualsSettlement`, `DeclaredDependencies`, `DeclaredPriceSteps` are the models to copy), with a European series.

```csharp
Check("a European option's base unit PV equals its settlement times the multiplier", ...);
Check("a European option declares its underlying, one discount input at expiry, its held volatility and no price step", ...);
Check("a European option's time dependency runs from the calculation instant to expiry", ...);
Check("one-tick revaluations give the oracle's delta within 0.000001 and gamma within 0.01 percent", () => {
    // black76_greeks: futures 3, strike 3, call 0.08, 588600 seconds, unit discount; bump 0.001 up and down
});
Check("a European fallback values each scenario at the discount times scenario intrinsic plus the base residual, floored at zero", ...);
Check("a European binding counts no tree price", () => Equal(binding.TreePrices.ToString(), "0"));
Check("preparing a European series with the tree's policy is refused", () => Reject("option_terms", ...));
```

- [ ] **Step 2: Run to verify they fail**

Run: `-- --european-options`
Expected: FAIL, the binding prices through `AmericanFuturesOptionModel` and raises `option_terms`.

- [ ] **Step 3: Route the binding through `FuturesOptionModels`**

`Declare` uses the series' expiry, not an instruction cutoff, as the end of a European series' time dependency. The fallback intrinsic for a European series is multiplied by the scenario's discount.

- [ ] **Step 4: Run to verify they pass**

Run: `-- --european-options`, `-- --futures-options`, `-- --option-sensitivities`, `-- --option-strategies`
Expected: all `PASS`; the pinned digests unedited.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEtrm.MarketData/Trading/FuturesOptions.cs tests/OpenEtrm.MarketData.Checks
git commit -s -m "Value a European series through its own model in the option binding"
```

### Task 4: The valuation policy and the stated policy

**Files:**
- Modify: `src/OpenEtrm.Api/FuturesOptionPortfolio.cs:15-37` (policy), `:198-256` (`Calibrate`), `:266` (`Values`)
- Modify (stated policy, `Release1` to `Release2`): `src/OpenEtrm.Api/PortfolioRiskPreview.cs:37-45`, `RiskPricingPolicies.cs:16-17`, `TradingPnlReport.cs:240,323`, `OptionStrategyRead.cs:33,84,180,216`, `PortfolioRiskPreview.Economic.cs:44,65`
- Modify: `src/OpenEtrm.Api/LifecyclePortfolioProjection.Prepare.cs:102-111` (per-series policy)
- Test: `tests/OpenEtrm.Api.Checks/OptionPolicySelectionChecks.cs`

**Interfaces:**
- Consumes: Tasks 1 to 3.
- Produces:
  - `public sealed record FuturesOptionEuropeanPolicy([property:JsonRequired] string NumericalPolicy, [property:JsonRequired] string CalibrationFallback)`, unmapped members disallowed.
  - `FuturesOptionValuationPolicy.European` (`FuturesOptionEuropeanPolicy?`, `init`, omitted when null, the pattern of `Physical` at `:18-21`).
  - `FuturesOptionValuationPolicy.Release2 => Release1 with { European = new("european_futures_option_black76_v1", "option_settlement_at_discounted_intrinsic_v1") }`.
  - `internal OptionNumericalPolicy FuturesOptionValuationPolicy.NumericalFor(FuturesOptionSeriesSpecification series)` and `internal bool FallbackDeclaredFor(FuturesOptionSeriesSpecification series)`: `option_policy`, naming the series code, when the series is European and `European` is null.
  - `Validate()` also refuses, with `option_policy`, a `european.numericalPolicy` other than the formula's and a `european.calibrationFallback` other than the discounted name or `none`.

- [ ] **Step 1: Write the failing checks**

```csharp
Check("Release1 still serializes to four members", ...);            // as CashOptionPolicyChecks.cs:19
Check("the stated policy names both models", () => Equal(
    JsonSerializer.Serialize(FuturesOptionValuationPolicy.Release2, RiskJobs.Json),
    JsonSerializer.Serialize(FuturesOptionValuationPolicy.Release1, RiskJobs.Json)[..^1] +
    ",\"european\":{\"numericalPolicy\":\"european_futures_option_black76_v1\",\"calibrationFallback\":\"option_settlement_at_discounted_intrinsic_v1\"}}"));
Check("a European series under a policy without the member is refused and named", () => {
    var error = Refused("option_policy", () => FuturesOptionValuationPolicy.Release1.NumericalFor(europeanSeries));
    if (!error.Message.Contains(europeanSeries.Key.Code)) throw new Exception("The refusal must name the series.");
});
Check("the member refuses the tree's policy and an unknown fallback", ...);
Check("an American and a European series on one future are each calibrated by their own model", ...);   // receipts' NumericalPolicy differ
Check("a run retained under Release1 is not the current report under the stated policy and stays readable", ...);   // UnderStatedPricingPolicy
Check("a retained European calibration that recomputes differently is refused", ...);   // risk_option_calibration, the shape of the existing replay check
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --option-policy`
Expected: build error, `Release2` not defined.

- [ ] **Step 3: Implement the member, `Release2` and the per-series choice**

`Calibrate` passes `policy.NumericalFor(series)` and `policy.FallbackDeclaredFor(series)` to `FuturesOptionPricing.Prepare` for each series instead of one policy for all. Every site listed above that states a policy states `Release2`. Leave `CashOptionPolicyChecks.cs:19` and `PhysicalOptionPolicyChecks.cs:32` on `Release1`.

- [ ] **Step 4: Run to verify they pass**

Run: `-- --option-policy`, `-- --schedules`, `-- --cash-option-value-policy`, `-- --physical-option-policy`, `-- --option-strategy-request`, `-- --historical-risk`
Expected: all `PASS`. Where an existing check compared against the stated policy by value, its expected literal moves to `Release2`; record each such edit in the commit message.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEtrm.Api tests/OpenEtrm.Api.Checks
git commit -s -m "Select a European model in the option valuation policy and state it"
```

### Task 5: Refuse European options where they are not qualified

**Files:**
- Modify: `src/OpenEtrm.MarketData/Trading/FuturesOptionLifecycleProjection.cs:82-91`, `Trading/FuturesOptionCashValueBinding.cs:14`, `Trading/FuturesOptionPhysicalValueBinding.cs:18`, `Options/CashFuturesOptionModel.cs:33`, `Options/PhysicalFuturesOptionModel.cs:45`
- Test: `tests/OpenEtrm.MarketData.Checks/OptionLifecycleProjectionChecks.cs`, `CashOptionModelChecks.cs`, `PhysicalOptionChecks.cs`

**Interfaces:**
- Produces: each entry point raises `MarketDataException("projection_option_style", ...)` for a European series before any price is computed.

- [ ] **Step 1: Write the failing checks**

One check in each of the three classes, named "a European series is refused with projection_option_style", constructing the class's existing American case with the style changed.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks -- --option-lifecycle-projection`, `-- --cash-option-model`, `-- --physical-option`
Expected: FAIL with `option_terms` from the tree instead of `projection_option_style`.

- [ ] **Step 3: Add the five refusals**

- [ ] **Step 4: Run to verify they pass**

Expected: all three flags `PASS`.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEtrm.MarketData tests/OpenEtrm.MarketData.Checks
git commit -s -m "Refuse European options in the lifecycle projection and reference strategies"
```

### Task 6: Reference data and lifecycle admit the style

**Files:**
- Create: `db/migrations/0391_european_futures_options.sql`
- Create: `tools/check_european_option.py`
- Modify: `tools/check_all.py:131-132` (register beside `check_lifecycle_option_expiries`), `tools/check_futures_option_storage.py:89-92`
- Modify: `src/OpenEtrm.Api/FuturesOptionStore.cs:190-214` (`EventRefusal`)

**Interfaces:**
- Produces:
  - Constraint `futures_option_products_exercise_style_check`: `exercise_style IN('american','european')`.
  - Constraint `futures_option_products_european_instructions_check`: `exercise_style<>'european' OR NOT contrary_instructions_allowed`.
  - `lifecycle.option_proposal_base(uuid,jsonb)` and `security.apply_futures_option_event(uuid,uuid,uuid,text,numeric,timestamptz,uuid,uuid[],jsonb)` raise `option_exercise_style` (`P0001`) for an exercise or assignment of a European series.
  - `EventRefusal` message for `option_exercise_style`: "A European option is exercised only at expiry."

- [ ] **Step 1: Write the failing check tool**

`tools/check_european_option.py` follows `tools/check_cash_option_value.py:36-80`: a labelled synthetic bundle layered on the exchange fixture, run through `check_reference_editing.main`. The bundle adds source `synthetic_european_option`; product `synthetic_european_option`, cloned from `nymex_on` with `exercise_style` `european`, underlying `ice_cfb` and multiplier 2500; its option-expiry close time; one call and one put series expiring before the underlying; their curve, contracts and synthetic settlements. Assertions, each a `PASS` line:

- the European product and its series insert, and the series appears in `options/catalog` with `exerciseStyle` `european`;
- a product with style `bermudan` is refused by `futures_option_products_exercise_style_check` (23514);
- a European product accepting contrary instructions is refused by `futures_option_products_european_instructions_check` (23514);
- a Trader books 3 lots; the Blotter value equals settlement times 2500 times 3;
- `lifecycle/preview` of `option.exercise` is refused with `option_exercise_style`; a direct call of the event primitive as the security owner is refused with the same code;
- after the expiry instant, `option.expiry` records the automatic outcome and an exercised holding books its linked future at the strike;
- a series whose expiry lies outside the rate curve's pillars is listed with `discount_coverage` and no value, and the other series is valued;
- each role of the spec's Roles table gets the outcome the table states for booking, saving the profile and recording the expiry of the synthetic holding.

In `tools/check_futures_option_storage.py:91`, change the rejected style from `european` to `bermudan`.

- [ ] **Step 2: Verify the tool fails before the migration**

Run on the preview host, on the owner's word: `python3 tools/check_european_option.py --pg-bin <pg bin> --dotnet <dotnet> --no-build`
Expected: FAIL at the first assertion with `futures_option_products_exercise_style_check`.

- [ ] **Step 3: Write the migration**

Header as `0382:1-4`. Replace the style check and add the instructions check. Patch the two functions with the anchored pattern of `0382:109-126`:

| Function | Anchor (must occur once) | Inserted after the anchor |
| --- | --- | --- |
| `lifecycle.option_proposal_base(uuid,jsonb)` | `IF kind IN('exercise','assignment') THEN` | `IF product.exercise_style='european' THEN RAISE EXCEPTION 'option_exercise_style';END IF;` |
| `security.apply_futures_option_event(...)` | `IF event_at<r.execution_at OR security.futures_option_market_date` | the same refusal with `USING ERRCODE='P0001'`, inserted before the anchor |

- [ ] **Step 4: Verify**

Run locally: `python3 -m unittest discover -s tests -p 'test_migration_identity.py'` (numbering has no gap).
Run on the preview host: the tool above, then `python3 tools/check_all.py --pg-bin <pg bin> --dotnet <dotnet> --browser <browser> --group exchange` and `--group lifecycle`.
Expected: the tool's lines all `PASS`; both groups pass with no other edit.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0391_european_futures_options.sql tools src/OpenEtrm.Api/FuturesOptionStore.cs
git commit -s -m "Admit European exercise in option reference data and refuse early exercise"
```

### Task 7: Catalog, lifecycle reasons and the ticket

**Files:**
- Modify: `src/OpenEtrm.Api/FuturesOptionStore.cs:101-129` (`Describe`), `src/OpenEtrm.Api/OptionLifecycleActions.cs:58-86`, `src/OpenEtrm.Api/FuturesOptionPositions.cs:60-70,148-150`
- Modify: `web/src/futures-option-model.ts:10-13`, `web/src/futures-option-view.ts:58,67-69,98`, `web/src/lifecycle-model.ts:27`
- Test: `web/tests/futures-option-model.test.mjs`, `web/tests/lifecycle-model.test.mjs`, `tests/browser/futures-options.mjs:83-89`

**Interfaces:**
- Produces:
  - Catalog series member `settlementMethod` (the series' stated value).
  - Discovery reason `option_exercise_style` on `option.exercise` and `option.assignment` for a European holding; the same reason in the Details actions.
  - The Blotter's option row and Details carry `exerciseStyle`.
  - `export function optionTermFacts(series: OptionSeries): { exercise: string; settlement: string }` in `futures-option-model.ts`.

- [ ] **Step 1: Write the failing web tests**

```js
test('term facts state the exercise style and settlement from the catalog', () => {
  assert.deepEqual(optionTermFacts({ ...series, exerciseStyle: 'european', settlementMethod: 'exercise_into_future' }),
    { exercise: 'European · at expiry only', settlement: 'Exercises into the future at the strike' });
  assert.deepEqual(optionTermFacts({ ...series, exerciseStyle: 'american', settlementMethod: 'exercise_into_future' }),
    { exercise: 'American · on any business day', settlement: 'Exercises into the future at the strike' });
});
test('an unknown style or settlement is shown as its code, never as another style', ...);
test('the European exercise reason has its own text', () =>
  assert.equal(lifecycleReason('option_exercise_style'), 'A European option is exercised only at expiry'));
```

- [ ] **Step 2: Run to verify they fail**

Run: `npm test --prefix web`
Expected: FAIL, `optionTermFacts` is not exported.

- [ ] **Step 3: Implement**

The ticket's Contract section shows **Exercise** and **Settlement** after **Expiry**, from `optionTermFacts`; the "Exercise style" line leaves the backend section. `OptionLifecycleActions.Discover` selects `p.exercise_style` and gives the reason. Update `tests/browser/futures-options.mjs:83-89` to the new fact labels for the American example.

- [ ] **Step 4: Run to verify they pass**

Run: `npm test --prefix web`; add to `tools/check_european_option.py` an assertion that `lifecycle/discover` lists `option.exercise` for the European holding as inactive with reason `option_exercise_style`.
Expected: web tests `PASS`; the tool assertion passes on the preview host.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEtrm.Api web tests/browser tools/check_european_option.py
git commit -s -m "Show an option's exercise style and settlement, and why a European option cannot be exercised early"
```

### Task 8: Stored surfaces and strategies

**Files:**
- Create: `db/migrations/0392_european_option_surfaces.sql`
- Modify: `src/OpenEtrm.Api/VolSurfaceStore.cs:47,62,100,138,236`, `src/OpenEtrm.Api/VolSurfaceFit.cs:29-58`, `src/OpenEtrm.MarketData/Options/SurfaceAxes.cs:34`
- Test: `tests/OpenEtrm.Api.Checks/VolSurfaceFitChecks.cs:88,112`, `tools/check_vol_surface_storage.py:11`, `tools/check_vol_surface_api.py:14,113`, `web/tests/options-view-browser.test.mjs:36`

**Interfaces:**
- Produces:
  - `internal static string VolSurfaceStore.SolverPolicy(string exerciseStyle)`: the formula's policy for European, the rebuilt tree's for American; replaces the `SolverPolicy` property.
  - The solver-policy check on `trading.vol_surface_revisions`, and the pins in `trading.record_vol_surface_revision(...)` and `risk.submit_vol_surface_fit(...)`, accept exactly the policy of the product's style: `CASE p.exercise_style WHEN 'european' THEN 'european_futures_option_black76_v1' ELSE 'american_futures_option_crr_v1' END`.

- [ ] **Step 1: Write the failing checks**

- `VolSurfaceFitChecks`: "a European product's fit names the formula's policy and stores the formula's value and sensitivities"; "a fit naming the other style's policy is refused".
- `check_vol_surface_storage.py`: a revision of the synthetic European product with the formula's policy inserts; with the tree's policy it is refused; an American product with the formula's policy is refused.
- `tests/OpenEtrm.MarketData.Checks/OptionStrategyChecks.cs` (`--option-strategies`): "a strategy of European legs is analysed by the formula and its legs declare no price step".

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project tests/OpenEtrm.Api.Checks -- --vol-surface-fit` (use the flag `VolSurfaceFitChecks` is dispatched by in that project's `Program.cs`)
Expected: FAIL, the fit names the tree's policy.

- [ ] **Step 3: Implement**

The migration finds the table check by its definition (`pg_get_constraintdef` containing `american_futures_option_crr_v1`, exactly one or raise) and replaces it with a check admitting both ids; the per-product rule lives in the two functions, patched with the anchored pattern. `SurfaceAxes.TryDeclare` accepts the policy of the surface's product style.

- [ ] **Step 4: Run to verify they pass**

Run locally: the Api check flag, `npm test --prefix web`. On the preview host: `--group exchange`.
Expected: all `PASS`; the American surface pins unedited.

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0392_european_option_surfaces.sql src tests tools web
git commit -s -m "Fit and store volatility surfaces for European option products"
```

### Task 9: Documents and the verification record

**Files:**
- Modify: `docs/futures-options.md` (title, product table, Instrument, Valuation, Lifecycle, Reads, Remaining work), `docs/exchange-futures.md:78-122`, `docs/architecture.md:2278-2296`, `docs/plan.md:1173,2146-2150`, `docs/vol-surfaces.md` (policy statements), `docs/decisions.md` (D446: piece 1 implemented), `docs/verification.md` (new section "European futures options (D446)")

- [ ] **Step 1: State what is implemented and what is not**

Each document says European exercise is implemented and names the model, the policy member and the refusals. The verification section lists each check run, where it ran and its result; a check that has not run is listed as not run. `docs/futures-options.md` also corrects its statement that no scheduled job records expiry.

- [ ] **Step 2: Check the documents**

Run: `python3 -m unittest discover -s tests -p 'test_*.py'`
Expected: `OK`.

- [ ] **Step 3: Commit**

```bash
git add docs
git commit -s -m "Document European futures options and record their verification"
```

### Task 10: Combined review and full checks

- [ ] **Step 1: Run everything that needs no database**

Run: `dotnet run --project tests/OpenEtrm.MarketData.Checks`, `dotnet run --project tests/OpenEtrm.Api.Checks -- --schedules`, `python3 -m unittest discover -s tests -p 'test_*.py'`, `npm test --prefix web`
Expected: all pass.

- [ ] **Step 2: Run the database groups on the owner's word**

Run on the preview host or in CI: `python3 tools/check_all.py --pg-bin <pg bin> --dotnet <dotnet> --browser <browser>` for groups `exchange`, `lifecycle`, `database`, `managed`, `frontend`, `repository`.
Expected: all pass; record the results in `docs/verification.md`.

- [ ] **Step 3: One independent adversarial review**

A fresh reviewer reads the whole branch diff against the spec, with this brief: the formula and its bounds; the calibration floor, tolerance and fallback; that no path prices a European series on the tree; that American values, serialized policies and retained results are unchanged; the knowledge cutoff at expiry; every refusal code. Resolve each finding, rerun the affected checks and record the review's outcome in `docs/verification.md`.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -s -m "Resolve the review of European futures options"
```
