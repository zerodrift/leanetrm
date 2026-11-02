# American Futures Options Implementation Plan

Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Book, value at settlement, risk-manage and exercise listed American options on NYMEX NG and CL futures (ON, LO).

**Architecture:**
- **Numerics:** a project-owned fixed-point type gives deterministic `exp`, `ln` and `sqrt`.
- **Core:** a backward-induction core sits under a binomial American futures-option model.
- **Valuation:** the option's pricing binding solves implied volatility once, from the base market, and reprices under shared futures scenarios.
- **Lifecycle:** SQL operations record exercise, assignment, instructions and expiry idempotently and book linked futures through the existing futures booking path.

**Tech Stack:**
- C# on .NET 10.0.402, with the custom `Check(...)` executables in `tests/*.Checks`;
- PostgreSQL 16 migrations;
- Python HTTP/SQL checks under `tools/`, run by `tools/run_preview.py`;
- the TypeScript UI in `web/`.

**Spec:** `docs/superpowers/specs/2026-11-01-american-futures-options-design.md`

## Global Constraints

- TypeScript is presentation only; every pricing, exercise and lifecycle rule lives in the managed backend or SQL.
- No silent defaults. Missing, ambiguous or conflicting inputs raise a structured `MarketDataException`/SQL error with a stable code.
- Every write is idempotent (D238). A retried request id returns its first outcome, and the same fact recorded again is a no-op.
- Pricers implement `Instrument.Declare` and `Instrument.PV(UnitValuationContext)` with complete dependencies and a model version, and read market data only through `PricingInputs`.
- Values are per one option contract; holdings scale afterwards.
- Model id, numerical policy, day-count and scenario policy are persisted calculation inputs.
- Only `premium_paid_up_front` is bookable. `futures_style_margin` returns `unsupported`. Only `american` with `exercise_into_future` is bookable.
- Base PV equals settlement price × multiplier within the policy tolerance, or the series is in the declared `option_settlement_at_intrinsic_v1` mode, or it errors.
- Identical pinned inputs give byte-identical outputs on every host. No `double`, `Math.Exp`, `Math.Log` or `Math.Sqrt` in pricing code.
- The public catalog holds real public specification facts only. Prices are synthetic and labeled synthetic.
- No customer, company or competing-product names in any file, commit or PR.
- Commits use `git commit -s` and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Nothing is pushed without the owner's approval.
- The migration is `0166_futures_options.sql` and the decision row is `D288`, numbered after master's 0165 and D287.
- Tests are written first and are independent of the algorithm: closed forms, bounds, independent oracles, never the tree's own output.

## Highest-Risk Inputs

1. **Delta collapses to zero.** A futures-shocked scenario must not re-solve volatility. Pinned in Task 5 by the "held volatility" check.
2. **Deep in-the-money near expiry.** The fallback applies only under its stated conditions; otherwise the error is named. Pinned in Task 4.
3. **Expiry with missing settlement.** No state change, and the holding stays visibly unprocessed. Pinned in Task 8.
4. **Valuation at or after expiry.** Gives `option_expired`, never a negative year fraction. Pinned in Task 3.
5. **Short options.** Assignment direction and the premium cash sign are correct for a short call and a short put. Pinned in Tasks 7 and 8.

## File Structure

| Path | Responsibility |
| --- | --- |
| `src/OpenEtrm.MarketData/Options/LatticeDecimal.cs` | Fixed-point (30 fractional digits, BigInteger) arithmetic with half-even rounding and `Exp`, `Ln`, `Sqrt` |
| `src/OpenEtrm.MarketData/Options/BackwardInduction.cs` | Lattice/state/decision contracts and the backward solver |
| `src/OpenEtrm.MarketData/Options/AmericanFuturesOptionModel.cs` | Option terms, numerical policy, CRR futures lattice, unit value |
| `src/OpenEtrm.MarketData/Options/ImpliedVolatility.cs` | Bracketed solver and intrinsic-fallback classification |
| `src/OpenEtrm.MarketData/Trading/FuturesOptions.cs` | `FuturesOptionInstrument`, `FuturesOptionMarketBinding`, portfolio preparation |
| `tests/OpenEtrm.MarketData.Checks/FuturesOptionChecks.cs` | Unit checks for Tasks 1–5 (`--futures-options`) |
| `tests/fixtures/futures-option-oracle.json` | Independent oracle values generated once with Python `decimal`/`mpmath` at 50 digits; the generator script is committed beside it |
| `db/migrations/0166_futures_options.sql` | Option reference tables, booking, lifecycle and expiry functions, grants |
| `tools/exchange_fixture.py` | Adds NYMEX CL futures, ON/LO option products, series and synthetic settlements |
| `src/OpenEtrm.Api/FuturesOptionStore.cs` | Catalog, booking, lifecycle and expiry HTTP operations |
| `src/OpenEtrm.Api/Program.cs`, `AgentTools.cs` | Route and agent-tool registration |
| `src/OpenEtrm.Api/PortfolioValuation.cs`, `TradingBlotter.cs` | Mixed-book materialization and Blotter rows |
| `tools/check_futures_option_storage.py`, `tools/check_futures_option_lifecycle.py`, `tools/check_futures_option_portfolio.py` | SQL/HTTP checks |
| `web/src/futures-option-view.ts` (+ existing New trade and position window wiring) | Presentation |
| `docs/futures-options.md`, `docs/decisions.md`, `docs/architecture.md`, `docs/plan.md`, `docs/verification.md`, `docs/exchange-futures.md` | Records |

---

### Task 1: Deterministic fixed-point numerics

**Files:**
- Create: `src/OpenEtrm.MarketData/Options/LatticeDecimal.cs`
- Create: `tests/OpenEtrm.MarketData.Checks/FuturesOptionChecks.cs`
- Create: `tests/fixtures/futures-option-oracle.json`, `tests/fixtures/futures_option_oracle.py`
- Modify: `tests/OpenEtrm.MarketData.Checks/Program.cs` (add `if(args is ["--futures-options"]){FuturesOptionChecks.Run();return;}`)
- Modify: `tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj` (copy the oracle fixture to output)

**Interfaces:**
- Produces: `public readonly record struct LatticeDecimal : IComparable<LatticeDecimal>`.
  - Arithmetic: `+ - * /`.
  - Functions: `Exp()`, `Ln()` (throws `lattice_domain` for ≤0), `Sqrt()` (throws `lattice_domain` for <0), `Pow(int)`, `Max`.
  - Conversion: `static LatticeDecimal From(ExactDecimal)`, `ExactDecimal ToExact()` (half-even at 18 digits).
  - Text: `static LatticeDecimal Parse(string)`, `string ToString()` (invariant).
  - Constant: `public const int FractionDigits = 30`.

- [ ] **Step 1: Write the oracle generator.** `tests/fixtures/futures_option_oracle.py` uses `mpmath` with `mp.dps=50` to write `exp`, `ln` and `sqrt` values for at least 40 inputs. The inputs span `1e-12` to `50`, plus negatives for `exp`, each rounded half-even to 30 digits. Run it once and commit the JSON.
- [ ] **Step 2: Write the failing check `LatticeFunctionsMatchOracle`.** For every oracle row, `LatticeDecimal.Parse(x).Exp().ToString()` equals the oracle string to ±1 unit in the last digit (same for `Ln`, `Sqrt`). Also check `Parse("0").Ln()` and `Parse("-1").Sqrt()` throw `MarketDataException` with code `lattice_domain`. Also check that `From(ExactDecimal.Parse("1.25")).ToExact()==ExactDecimal.Parse("1.25")`, and that rounding of `1/3` to exact is half-even at 18 digits.
- [ ] **Step 3: Run it to verify it fails.** Run: `~/.openetrm-tools/dotnet-10.0.402/dotnet run --project tests/OpenEtrm.MarketData.Checks -- --futures-options`. Expected: build error, because `LatticeDecimal` is not defined.
- [ ] **Step 4: Implement `LatticeDecimal`** on a `BigInteger` scaled by 10^30, using only integer operations:
  - `*` and `/` round half-even once per operation;
  - `Exp` reduces by `ln 2` and uses a Taylor series;
  - `Ln` uses Newton iteration on `Exp`;
  - `Sqrt` uses integer Newton.

  Every iteration count is bounded and documented. Magnitude limit `10^24`, error `numeric_range`.
- [ ] **Step 5: Run the checks and verify they pass.** Same command. Expected: `PASS` lines and exit 0.
- [ ] **Step 6: Commit** with `git commit -s -m "Add deterministic fixed-point numerics for option lattices"`.

### Task 2: Backward-induction core

**Files:**
- Create: `src/OpenEtrm.MarketData/Options/BackwardInduction.cs`
- Modify: `tests/OpenEtrm.MarketData.Checks/FuturesOptionChecks.cs`

**Interfaces:**
- Consumes: `LatticeDecimal`.
- Produces:
  - `public interface ILattice { int Steps{get;} int Nodes(int step); LatticeDecimal Underlying(int step,int node); (int Node,LatticeDecimal Probability)[] Next(int step,int node); LatticeDecimal Discount(int step); }`
  - `public interface IInductionContract<TState> where TState:struct,Enum { ImmutableArray<TState> States{get;} LatticeDecimal Terminal(LatticeDecimal underlying,TState state); ImmutableArray<InductionDecision<TState>> Decisions(int step,LatticeDecimal underlying,TState state); }`
  - `public sealed record InductionDecision<TState>(string Code,LatticeDecimal Immediate,TState Next);`
  - `public sealed record InductionResult<TState>(LatticeDecimal Root,ImmutableArray<ImmutableArray<ImmutableDictionary<TState,string>>> Chosen);`
  - `public static class BackwardInduction { public static InductionResult<TState> Solve<TState>(ILattice lattice,IInductionContract<TState> contract,CancellationToken ct); }`
- Ties between equal values pick the first decision in declared order; this is documented and checked.

- [ ] **Step 1: Write the failing check `TwoStepHandTree`.** A test-only lattice with 2 steps on 100: up 1.2, down 0.8, p=0.5 and a per-step discount of 0.9. Terminal values are 144, 96 and 64.
  - A European put (single decision `hold`), K=100: the step-1 nodes are 1.8 and 18, so the root is `8.91`.
  - An American put (`hold` or `exercise` in state `Alive`, `exercise` moves to `Exercised`, terminal value 0 when exercised), K=100: at the step-1 down node, exercising (20) beats holding (18), so the root is `9.81` and `exercise` is chosen there.
  - With a discount of 1 and K=100, the American put's down node ties at 20. The first declared decision, `hold`, is chosen.

  Write this hand arithmetic in the check's comments.
- [ ] **Step 2: Run it to verify it fails** with the `--futures-options` command. Expected: build error.
- [ ] **Step 3: Implement `BackwardInduction.Solve`** as a backward loop over steps. Each node's continuation is the discounted probability-weighted next values for the decision's next state; the best of the immediate value plus continuation wins. Check cancellation once per step.
- [ ] **Step 4: Run the checks and verify they pass.**
- [ ] **Step 5: Commit** with `git commit -s -m "Add a backward-induction core for lattice contracts"`.

### Task 3: American futures option model

**Files:**
- Create: `src/OpenEtrm.MarketData/Options/AmericanFuturesOptionModel.cs`
- Modify: `tests/OpenEtrm.MarketData.Checks/FuturesOptionChecks.cs`, `tests/fixtures/futures_option_oracle.py`, `tests/fixtures/futures-option-oracle.json`

**Interfaces:**
- Consumes: Tasks 1–2.
- Produces:
  - `public enum OptionRight{Call=1,Put}`
  - `public sealed record FuturesOptionTerms(OptionRight Right,ExactDecimal Strike,DateTimeOffset ExpiryAt,string ExerciseStyle,string SettlementMethod)`. The constructor rejects any style other than `american` and any method other than `exercise_into_future` with `option_terms`.
  - `public sealed record OptionNumericalPolicy(string Id,int Steps,string DayCount,LatticeDecimal VolatilityMin,LatticeDecimal VolatilityMax,LatticeDecimal PriceTolerance,int MaxIterations)`.
  - `public static class OptionNumericalPolicies { public static OptionNumericalPolicy Require(string id); }`. It holds exactly one entry, `american_futures_option_crr_v1`, with day count `act_365_fixed_instant_v1`. An unknown id raises `option_numerical_policy`.
  - `public static class AmericanFuturesOptionModel { public const string Id="american_futures_option_crr_v1"; public static LatticeDecimal YearFraction(DateTimeOffset valuationAt,DateTimeOffset expiryAt,OptionNumericalPolicy policy); public static LatticeDecimal UnitPrice(FuturesOptionTerms terms,LatticeDecimal futures,LatticeDecimal volatility,LatticeDecimal discountToExpiry,DateTimeOffset valuationAt,OptionNumericalPolicy policy,CancellationToken ct); }`. `UnitPrice` is per unit of quote, before the multiplier.
- The lattice is CRR on the futures price: `u=exp(σ√Δt)`, `d=1/u`, `p=(1−d)/(u−d)`, and a per-step discount of `discountToExpiry^(1/Steps)` computed as `exp(ln(DF)/Steps)`. States `Alive` and `Exercised`; decisions `hold` and `exercise`.
- `YearFraction` throws `option_expired` when `valuationAt >= expiryAt`.

- [ ] **Step 1: Extend the oracle** with Black-76 European call and put values, using `mpmath` `ncdf` at 50 digits, for 12 cases spanning moneyness 0.5–2, σ 0.1–1.5 and T 0.01–2.
- [ ] **Step 2: Write the failing checks.**
  - `ZeroRateCallEqualsBlack76`: DF=1, so the American call equals the Black-76 call within `policy.PriceTolerance × 100`. That loose bound covers discretisation error; the convergence check below tightens it.
  - `Bounds`:
    - value ≥ intrinsic;
    - value ≥ the Black-76 European value at DF<1 minus the same tolerance;
    - a call is at most the futures price;
    - a put is at most the strike.
  - `Monotonic`: value rises with σ and with T; calls rise and puts fall in the futures price.
  - `Convergence`: at the pinned step count, the value is within the tolerance of the 4×-steps value for 6 oracle cases.
  - `ExpiredValuation`: `valuationAt == expiryAt` throws `option_expired`.
  - `RejectsUnsupportedTerms`: `european` and `cash` both raise `option_terms`.
- [ ] **Step 3: Run them to verify they fail** with the build error.
- [ ] **Step 4: Implement the model.** Choose the pinned step count, `PriceTolerance`, bracket `[VolatilityMin, VolatilityMax]` and `MaxIterations` from convergence evidence. Record the evidence (cases, step counts, max error and runtime per price) in the commit message body.
- [ ] **Step 5: Run the checks and verify they pass**, and print the runtime of one `UnitPrice` call at the pinned steps.
- [ ] **Step 6: Commit** with `git commit -s -m "Price American options on futures with a CRR lattice"`.

### Task 4: Implied volatility and intrinsic fallback

**Files:**
- Create: `src/OpenEtrm.MarketData/Options/ImpliedVolatility.cs`
- Modify: `tests/OpenEtrm.MarketData.Checks/FuturesOptionChecks.cs`

**Interfaces:**
- Consumes: Task 3.
- Produces:
  - `public abstract record OptionCalibration`;
  - `public sealed record SolvedVolatility(LatticeDecimal Volatility,LatticeDecimal Residual,int Iterations):OptionCalibration`;
  - `public sealed record IntrinsicFallback(LatticeDecimal Intrinsic,LatticeDecimal Residual):OptionCalibration`, where `Residual = settlement − intrinsic`;
  - `public static class ImpliedVolatility { public const string FallbackMode="option_settlement_at_intrinsic_v1"; public static OptionCalibration Solve(FuturesOptionTerms terms,LatticeDecimal settlement,LatticeDecimal futures,LatticeDecimal discountToExpiry,LatticeDecimal clearingIncrement,DateTimeOffset valuationAt,OptionNumericalPolicy policy,bool fallbackDeclared,CancellationToken ct); }`.
- Error codes:
  - `option_settlement_below_intrinsic`: settlement < intrinsic − clearing increment;
  - `option_settlement_above_bound`: settlement > price at the upper volatility bound;
  - `option_calibration_nonconvergent`: no convergence within the iteration limit;
  - `option_calibration_fallback_undeclared`: the fallback condition holds but the mode isn't declared.
- Fallback condition: intrinsic − increment ≤ settlement ≤ price at `VolatilityMin`.
- The solver is bisection on σ in the bracket, which is deterministic and needs no derivatives.

- [ ] **Step 1: Write the failing checks.**
  - `RoundTrip`: for 8 synthetic settlements generated from oracle σ values, `Solve` returns `SolvedVolatility`, and repricing at it is within `PriceTolerance` of the settlement.
  - `FallbackAtIntrinsic`: a settlement equal to intrinsic for a deep in-the-money put with T=2 days, with `fallbackDeclared:true`, returns `IntrinsicFallback`. With `false` it raises `option_calibration_fallback_undeclared`.
  - `BelowIntrinsic`: intrinsic − 2 increments raises `option_settlement_below_intrinsic`.
  - `AboveBound`: above the call upper bound raises `option_settlement_above_bound`.
  - `OutOfMoneyZero`: an out-of-the-money option at settlement 0 with fallback declared returns `IntrinsicFallback` with intrinsic 0.
- [ ] **Step 2: Run them to verify they fail.**
- [ ] **Step 3: Implement `Solve`.**
- [ ] **Step 4: Run the checks and verify they pass.**
- [ ] **Step 5: Commit** with `git commit -s -m "Solve option implied volatility with a declared intrinsic fallback"`.

### Task 5: Option instrument, binding and scenario risk

**Files:**
- Create: `src/OpenEtrm.MarketData/Trading/FuturesOptions.cs`
- Modify: `src/OpenEtrm.MarketData/Model.cs` (`ContractKind.FutureOption`, `FuturesOptionSeriesSpecification`)
- Modify: `tests/OpenEtrm.MarketData.Checks/FuturesOptionChecks.cs`

**Interfaces:**
- Consumes: Tasks 3–4; `FuturesMarketBinding` (`src/OpenEtrm.MarketData/Trading/Futures.cs`), `UnitValuationContext.DiscountProvider` and `ISideDiscounting.Resolve` (`PaymentSchedules.cs`), `PricingInputs.ExactPrice`, `ValuationPortfolio.FromTrades`, and `RiskEngine.Calculate` (`Risk/RiskEngine.cs`, used as in `FuturesRisk.Calculate`).
- Produces:
  - `public sealed record FuturesOptionSeriesSpecification(ReferenceKey Key,ReferenceKey Product,ReferenceKey UnderlyingSeries,FuturesOptionTerms Terms,ExactDecimal Multiplier,ReferenceKey Currency,ExactDecimal ClearingIncrement,string AutomaticExerciseRule,DateTimeOffset InstructionCutoffAt,string PremiumStyle)`. Its identity is the exact series key.
  - `public sealed record FuturesOptionInstrument:Instrument`. `SupportsQuantityScaling=>true`; `Declare` returns model `AmericanFuturesOptionModel.Id` with dependencies `[underlying futures CurveRequest]`; `PV` prices against the scenario futures price.
  - `public sealed record FuturesOptionMarketBinding(Guid Id,FuturesOptionInstrument Instrument,CurveRequest Underlying,CurveRequest Settlement,string ObservationRole,OptionCalibration Calibration,LatticeDecimal BaseFutures,LatticeDecimal DiscountToExpiry,OptionNumericalPolicy Policy)`.
  - `public static class FuturesOptionPricing`:
    - `public const string ScenarioPolicy="option_implied_volatility_held_v1"`;
    - `public static FuturesOptionMarketBinding Prepare(FuturesOptionInstrument option,CurveBuild basis,MarketSnapshot snapshot,ISideDiscounting discounting,string observationRole,OptionNumericalPolicy policy,bool fallbackDeclared,CancellationToken ct)`;
    - `public static RiskRun Calculate(...)`, mirroring `FuturesRisk.Calculate`, with base/up/down futures bumps.
- `Prepare` reads the option's and the underlying's prices from the **base** build under the same observation role, using an `ExplicitDiscounting`-compatible `SidePaymentKey` at the expiry date. A role mismatch raises `option_observation_role`.
- Scenario PV:
  - solved mode: `UnitPrice` at the held σ and the scenario futures price, × multiplier;
  - fallback mode: `(intrinsic(scenario F) + Residual) × multiplier`.

- [ ] **Step 1: Write the failing checks.** Each uses a two-curve synthetic snapshot and `ExplicitDiscounting`.
  - `BasePvEqualsSettlement`: base unit PV equals settlement × multiplier within tolerance.
  - `HeldVolatilityDelta`: with up/down 1-tick futures bumps, delta is nonzero, positive for a long call and negative for a long put. The shocked PVs equal `UnitPrice` at the held σ, computed directly in the check.
  - `ShortPositionsFlipSign`: holdings of −1 flip delta.
  - `FallbackDelta`: delta for a fallback in-the-money call is one contract per lot (multiplier × bump / bump). It is zero for an out-of-the-money option at settlement 0.
  - `DeclaredDependencies`: `Declare` lists only the underlying futures request, so the settlement curve never appears among risk factors.
  - `ReplayIdentical`: two `Calculate` runs on identical pinned inputs produce identical serialized results.
  - `HostIndependentDigest`: the SHA-256 of the serialized results of a fixed 4-option case equals a digest pinned in the check. The check runs on macOS locally and on Linux in CI, so any host-dependent arithmetic fails one of them. Pin the digest from the first macOS run, then confirm the same digest on a Linux host before committing.
- [ ] **Step 2: Run them to verify they fail.**
- [ ] **Step 3: Implement** `FuturesOptions.cs` and the `Model.cs` additions.
- [ ] **Step 4: Run** `--futures-options` and the full MarketData checks (`dotnet run --project tests/OpenEtrm.MarketData.Checks`) and verify they pass.
- [ ] **Step 5: Commit** with `git commit -s -m "Value futures options at settlement with held implied volatility"`.

### Task 6: Reference data and catalog

**Files:**
- Create: `db/migrations/0166_futures_options.sql` (reference section only in this task)
- Modify: `tools/exchange_fixture.py`, `docs/exchange-futures.md`
- Create: `tools/check_futures_option_storage.py`
- Modify: `tools/run_preview.py`, `tools/check_all.py` (register the check)

**Interfaces:**
- Produces these tables, each keyed `(catalog_id,revision_id,code)` with exact-version foreign keys and row security matching `futures_products` and `futures_series`:
  - `ref_data.futures_option_products`: exchange, underlying futures product, multiplier, quote specification, `exercise_style`, `settlement_method`, `automatic_exercise_rule`, instruction-cutoff rule text;
  - `ref_data.futures_option_series`: product, underlying `futures_series` code, `right`, `strike`, `last_trading_date`, `expiry_at`, `instruction_cutoff_at`;
  - `ref_data.futures_option_rules`: rule code, version and public source reference.
- Settlement observations use the existing observation storage with role `daily_settlement`, keyed by option series.
- The migration begins `BEGIN; SELECT pg_advisory_xact_lock(730849102);`, like 0158.

- [ ] **Step 1: Write the failing check `check_futures_option_storage.py`.**
  - The catalog contains `nymex_cl` futures and ON/LO option products whose underlying is exactly `nymex_ng`/`nymex_cl`.
  - Strikes are multiples of the futures trading tick.
  - Every series' `expiry_at` precedes its underlying's last trading instant.
  - Inserting a series whose underlying belongs to another revision fails on the foreign key.
  - A `european` style is rejected by the check constraint.
  - Every price row is `synthetic`.
- [ ] **Step 2: Run it to verify it fails.** Run: `LC_ALL=en_US.UTF-8 python3 tools/run_preview.py --pg-bin /opt/homebrew/opt/postgresql@16/bin --dotnet ~/.openetrm-tools/dotnet-10.0.402/dotnet --check` with the new check registered. Expected: FAIL, relation missing.
- [ ] **Step 3: Implement the migration section and fixture rows.**
  - Before writing the rows, confirm the CL size, tick, settlement method and expiry dates, and the ON/LO exercise style, automatic exercise rule, instruction cutoff and expiry dates, against current public CME pages.
  - Record each source link and retrieval date in `ref_data.sources` as facts only.
  - Add explicit series for the existing November 2026–April 2027 maturities: three strikes per maturity around the synthetic mark, with calls and puts.
  - Generate synthetic `daily_settlement` values for each option inside the model bounds, using Task 3's model offline. Include one deep in-the-money near-expiry series priced at intrinsic, for the fallback.
- [ ] **Step 4: Run** the preview check plus `python3 -m pytest tests/test_migration_identity.py` and verify they pass.
- [ ] **Step 5: Commit** with `git commit -s -m "Add NYMEX CL futures and ON/LO option reference data"`.

### Task 7: Booking

**Files:**
- Modify: `db/migrations/0166_futures_options.sql` (booking section)
- Create: `src/OpenEtrm.Api/FuturesOptionStore.cs`
- Modify: `src/OpenEtrm.Api/Program.cs`, `src/OpenEtrm.Api/AgentTools.cs`
- Create: `tools/check_futures_option_lifecycle.py` (booking cases first)

**Interfaces:**
- Consumes: `security.book_futures` (`db/migrations/0011_futures_bookings.sql:159`) for authorization and request-id conventions; `FuturesStore.Binding` for the HTTP-to-binding pattern.
- Produces:
  - SQL `security.book_futures_option(catalog uuid,operation uuid,book uuid,revision uuid,series text,lots numeric,premium numeric,premium_payment_date date,premium_style text,executed_at timestamptz,…trade attribution as book_futures)` returning jsonb `{tradeId,optionInstrumentId,premiumInstrumentId}`. It requires the same permission as `book_futures`.
  - HTTP `options/catalog`, `options/book` and `options/status`, with request records shaped like `FuturesReadRequest`/`FuturesBookRequest` (JSON unmapped members disallowed).
  - Agent tools named `futures_option_catalog` and `futures_option_book`, calling the same store methods.
- The premium cash instrument is a fixed cash payment of `premium × multiplier × |lots|`. It pays for a long and receives for a short, on `premium_payment_date`, booked explicitly.

- [ ] **Step 1: Write the failing HTTP checks.**
  - A long ON call books one option instrument and one premium instrument with the right signs.
  - A short LO put books premium received.
  - Retrying the same `operationId` returns the first outcome and adds no rows.
  - `premiumStyle:"futures_style_margin"` returns `unsupported`.
  - A missing premium payment date returns `option_booking_input`.
  - A user without booking permission is refused.
  - The agent tool books identically.
- [ ] **Step 2: Run them to verify they fail** with the preview command.
- [ ] **Step 3: Implement** the SQL function, store, routes and agent tools.
- [ ] **Step 4: Run them and verify they pass.**
- [ ] **Step 5: Commit** with `git commit -s -m "Book futures options with premium paid up front"`.

### Task 8: Exercise, assignment, instructions and expiry

**Files:**
- Modify: `db/migrations/0166_futures_options.sql` (lifecycle section), `src/OpenEtrm.Api/FuturesOptionStore.cs`, `Program.cs`, `AgentTools.cs`, `tools/check_futures_option_lifecycle.py`

**Interfaces:**
- Produces:
  - SQL table `trading.futures_option_events(catalog_id,trade_id,request_id UNIQUE,kind CHECK(kind IN('exercise','assignment','exercise_instruction','abandon_instruction','expiry')),lots,business_at,knowledge_at,futures_trade_id NULL,…)`.
  - SQL `security.record_futures_option_event(catalog uuid,request uuid,trade uuid,kind text,lots numeric,business_at timestamptz)`. Exercise and assignment book the linked futures in the same transaction through the internal path used by `book_futures`:
    - execution price = strike;
    - long future for a call exercise or a put assignment, short future for a put exercise or a call assignment;
    - book, counterparty and clearing account copied from the option trade, trader = the caller;
    - the futures operation id derived deterministically from the event's request id.
  - SQL `security.process_futures_option_expiry(catalog uuid,request uuid,series text,snapshot uuid)`, run once per open holding:
    - a filed instruction wins;
    - otherwise the versioned automatic rule applies against the underlying's `daily_settlement` on the expiry date;
    - a missing settlement raises `option_expiry_settlement_missing` and writes nothing.
  - HTTP `options/exercise`, `options/assignment`, `options/instruction` and `options/expiry`, with matching agent tools.
  - *Superseded by D288:* the expiry function is `security.process_futures_option_expiry(catalog uuid,request uuid,trade uuid,snapshot uuid,observations uuid[])`, run per holding with the frame's selected observations, and no agent tool records lifecycle events.
- Open lots are never negative: exercising or assigning more than the open lots raises `option_lots_exceeded`. Instructions after `instruction_cutoff_at` raise `option_instruction_closed`.

- [ ] **Step 1: Write the failing checks.**
  - Partial early exercise of 3 of 5 lots books 3 long NG futures at the strike and leaves 2 open.
  - Retrying the same request id returns the same futures trade.
  - Assignment of a short call books a short future.
  - An abandon instruction on an in-the-money option means expiry books nothing; an exercise instruction on an out-of-the-money option means expiry books futures.
  - With no instruction, expiry follows the automatic rule.
  - Expiry with the settlement removed raises `option_expiry_settlement_missing`, and the event count is unchanged.
  - Over-exercise and late instructions raise their codes.
  - A historical read before the event shows the pre-event state.
- [ ] **Step 2: Run them to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them and verify they pass.**
- [ ] **Step 5: Commit** with `git commit -s -m "Record futures option exercise, assignment and expiry"`.

### Task 9: Blotter, portfolio risk and the mixed portfolio check

**Files:**
- Modify: `src/OpenEtrm.Api/PortfolioValuation.cs`, `src/OpenEtrm.Api/TradingBlotter.cs`, `src/OpenEtrm.Api/PortfolioRiskPreview.cs`, `src/OpenEtrm.Api/RiskWorker.cs` (if retained jobs need the new binding)
- Create: `tools/check_futures_option_portfolio.py`, modeled on `tools/check_crude_portfolio.py`

**Interfaces:**
- Consumes: `FuturesOptionPricing.Prepare`/`Calculate`, the existing payment-discount selection used by swaps, and the existing excluded-positions reporting.
- Produces:
  - Blotter rows with `right`, `strike`, `expiryAt`, `underlyingSeries`, `lots`, `settlement`, `impliedVolatility`, `calibrationMode` (`solved` or `option_settlement_at_intrinsic_v1`), `presentValue`, `lifecycleState`, `openLots`, and `linkedFuturesTradeIds`.
  - The risk report adds the per-position calibration mode and a count of fallback positions. Exclusions use the structured codes from Task 4.

- [ ] **Step 1: Write the failing check `check_futures_option_portfolio.py`.** One book holds a CL future, a long ON call, a short LO put, the deep in-the-money fallback series and its premiums. It verifies:
  - each option PV equals settlement × multiplier × lots;
  - premium PVs come from the selected discounting;
  - delta signs under the futures factors;
  - the fallback position's mode and delta;
  - removing one option settlement excludes only that option, with `option_settlement_missing`;
  - a queued risk job matches the immediate report exactly;
  - exercised lots link to their futures in the Blotter.
- [ ] **Step 2: Run it to verify it fails.**
- [ ] **Step 3: Implement** materialization, Blotter rows and risk reporting.
- [ ] **Step 4: Run it, `--unified-trading-check` and the full preview `--check`, and verify they pass.** Record the workload for one full risk run: option count, unique series, scenarios, evaluated tree prices, wall time, peak memory and host.
- [ ] **Step 5: Commit** with `git commit -s -m "Include futures options in the Blotter and portfolio risk"`.

### Task 10: User interface

**Files:**
- Create: `web/src/futures-option-view.ts`
- Modify: the New trade product list and position window (`web/src/app.ts`, `web/src/position-window.ts`), and `web/src/app.css` if needed
- Create: `tests/browser/futures-options.mjs`; modify `tests/browser/run.mjs`

**Interfaces:**
- Consumes: the HTTP operations from Tasks 7–8 and the Blotter rows from Task 9. The browser computes nothing; it displays backend values and errors.

- [ ] **Step 1: Write the failing browser check.**
  - Book an ON call from New trade, then see it in the Blotter with its settlement and PV.
  - Exercise 1 lot from the position window, then see the linked future.
  - File an abandon instruction.
  - See the fallback position labeled with its mode.
  - Submitting margined premium shows the backend's `unsupported` message.
- [ ] **Step 2: Run it to verify it fails.** Run: `npm test --prefix web` and the preview browser checks with the pinned Node.
- [ ] **Step 3: Implement the view and wiring.**
- [ ] **Step 4: Run the checks and verify they pass.**
- [ ] **Step 5: Commit** with `git commit -s -m "Add futures options to New trade and the position window"`.

### Task 11: Records

**Files:**
- Create: `docs/futures-options.md`
- Modify: `docs/decisions.md`, `docs/architecture.md` (section 5.4 and "Observable instruments and option underlyings"), `docs/plan.md`, `docs/verification.md`, `docs/exchange-futures.md`

- [ ] **Step 1: Add the D288 row.**
  - Listed futures options are standalone instruments with an exact underlying series. Exercise creates a separate futures obligation; O13 stays open for embedded options.
  - Owner decisions 1–8 from the spec, dated 2026-11-01.
  - Model `american_futures_option_crr_v1`, scenario policy `option_implied_volatility_held_v1` and fallback `option_settlement_at_intrinsic_v1`.
- [ ] **Step 2: Write `docs/futures-options.md`** with actual capability and remaining work: vega, margined premium, European and cash-settled options, and settlement ingestion. Update the plan and architecture sentences that say no option product exists, and record the verified results and benchmark from Task 9 in `verification.md`.
- [ ] **Step 3: Check the repository rules.** Run `python3 tools/check_all.py` with the pinned toolchain. Also run a grep for forbidden names over the diff; it must find nothing.
- [ ] **Step 4: Commit** with `git commit -s -m "Record the futures options decision and verified capability"`.

### Independent adversarial review

After Task 11, the whole change receives independent adversarial review covering numerical correctness, temporal behavior, authorization of the lifecycle operations and scenario reuse, as AGENTS.md requires for those areas. Its findings are resolved before publication.
