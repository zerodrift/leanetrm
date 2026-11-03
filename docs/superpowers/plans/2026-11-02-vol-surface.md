# Volatility Surface Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fit, store bitemporally and show a volatility surface with rich/cheap for each listed futures-option product: a heat map, a rotatable 3D surface and per-expiry smiles.

**Architecture:**
- **Fit:** a project-owned, versioned vol model (`smile_quadratic_log_moneyness_v1`) in `OpenEtrm.MarketData.Options` fits one smile per expiry from the per-series volatilities the existing solver produces.
- **Fitting operation:** the API fits a product at an exact input frame and records an immutable surface revision through the existing trading publication and knowledge protocol. It is idempotent under D238.
- **Reading:** a read returns the latest revision known at the context's cutoff, and says whether its inputs have since been revised.
- **Page:** a new Markets page draws the stored result. It does no maths.

**Tech Stack:**
- C# on .NET 10.0.402 (`~/.openetrm-tools`), with the custom `Check(...)` executables in `tests/*.Checks`;
- PostgreSQL 16 migrations;
- Python HTTP/SQL checks under `tools/`, run by `tools/run_preview.py`;
- TypeScript in `web/`, with `echarts` 6.1.0 already a dependency;
- browser checks in `tests/browser/*.mjs`.

**Spec:** `docs/superpowers/specs/2026-11-02-vol-surface-design.md`

## Global Constraints

- **TypeScript is presentation only.** The page maps returned values to pixels, colours and percentage text. It never fits, interpolates, computes moneyness or computes rich/cheap.
- **No silent defaults.** Missing, ambiguous or conflicting input raises a `MarketDataException` or SQL error with a stable code. Codes this work adds:
  - `vol_model_unknown`, `vol_fit_insufficient_strikes` and `vol_fit_nonpositive`;
  - `vol_surface_nondeterministic` and `vol_surface_input`;
  - `vol_surface_product`, `vol_surface_no_settlements` and `vol_surface_context`.
- **Arithmetic** is `LatticeDecimal` throughout: 30 fractional digits, half-even, with the project's own `Ln` and `Sqrt`. No numerical-library dependency is added.
- **The vol model id and version are explicit request inputs**, echoed in every response and persisted on every revision. The solver policy id (`american_futures_option_crr_v1`) is persisted too.
- **Wire values are decimal fractions**, as the solver returns them: 0.012 is 1.2 volatility points. Exact decimals are serialized as exact text, as other trading reads do.
- **Idempotency (D238).**
  - A request id returns its first outcome.
  - Refitting a key whose latest revision pinned the same input observations stores nothing and returns that revision.
  - A recomputed result that differs from the stored one raises `vol_surface_nondeterministic`.
  - Different pinned inputs produce a new revision with its own knowledge time.
  - Reads order by knowledge (publication order), never by row order.
- **Bitemporal storage reuses the existing protocol.** Use `trading.publications`, `trading.knowledge_receipts`, `security.trading_visible(catalog_id,publication_order,effective_at)` and the `market_data.guard_knowledge_receipt()` immutability trigger, exactly as `db/migrations/0154_crude_receipt_cash.sql` does for crude operational events. Do not create a new knowledge clock or timestamp column.
- **Permissions.**
  - Reading needs `ReadMarketData` and `ReadTrades`, since stored revisions ride the trading visibility protocol.
  - Submitting a fit job needs `PublishMarketData` and `ReadTrades`, and only in the current workspace: a historical context is refused with `historical_read_only`. The worker rechecks the actor's current authority before fitting and at publication.
  - Agents get no fit operation in this work.
- **Commodity-neutral.** No code or copy names gas or crude except example data.
- **Public repository rules (AGENTS.md).**
  - Synthetic example values only.
  - No customer, company or competing-product names, and no host names or local configuration.
  - Commit with `git commit -s`, and end each commit message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Never push without the user's approval.
- **TDD.** Each behaviour check is written first and fails for the right reason before the code exists.
- **Migration** `db/migrations/0186_vol_surfaces.sql` follows master's `0185_facility_read_plans.sql` (renumbered from 0172, 0180, 0184, then 0185, at the master merges). Renumber it again if master moves on before merge.
- **Fits are background jobs.** A fit runs as job kind `vol_surface_fit_v1` on the existing retained risk-job queue (`risk.jobs`, `src/OpenEtrm.Api/RiskWorker.cs`), under its lease, attempt, cancellation and security-generation rules. Add no new queue, service or cache. Fit runtime is measured and recorded (series, solves, elapsed); no budget is enforced.

## Review Focus

1. **Settlement revised after a fit.** A surface read at a cutoff after the revision must show the stored surface flagged as revised (`inputsRevised: true`), never a silent refit or the old surface presented as current. Task 5 adds `revised settlement flags the stored surface`.
2. **Two near-simultaneous fits of the same key with different request ids.** They must produce one revision, with the second a no-op returning it. Task 4 adds `concurrent identical fits record one revision`.
3. **A product whose underlying has no settlement for one expiry.** That expiry's series carry `option_underlying_missing` and the expiry is `vol_fit_insufficient_strikes`. The rest of the surface is fitted, with no request-level failure. Task 2 adds `expiry without underlying is a gap, others fit`.
4. **A cutoff before any surface was recorded, after one exists.** The read returns `notFitted`, never the later revision. Task 5 adds `earlier cutoff does not see later revision`.
5. **Authority removed between submit and run.** The job fails with the existing authority-changed outcome and records no revision. Task 5 adds `revoked fitter's job records nothing`.
6. **Strikes exactly at the forward.** The call is the fitted point, and k = 0 is in range. Task 2 adds `at-the-forward strike uses the call`.

---

### Task 1: The vol model contract and the quadratic smile fit

**Files:**
- Create: `src/OpenEtrm.MarketData/Options/VolModel.cs`
- Create: `tests/OpenEtrm.MarketData.Checks/VolSurfaceChecks.cs`
- Modify: `tests/OpenEtrm.MarketData.Checks/Program.cs`, adding `if(args is ["--vol-surface"]){VolSurfaceChecks.Run();return;}` next to the `--futures-options` line

**Interfaces:**
- Produces:
  - `public sealed record SmilePoint(LatticeDecimal LogMoneyness, LatticeDecimal Volatility)`
  - `public sealed record SmileParameters(LatticeDecimal A, LatticeDecimal B, LatticeDecimal C)`
  - `public abstract record SmileFit`, with:
    - `sealed record FittedSmile(SmileParameters Parameters, int Points, LatticeDecimal RmsResidual, LatticeDecimal MaxAbsResidual) : SmileFit`
    - `sealed record SmileGap(string Code) : SmileFit`, where Code is `vol_fit_insufficient_strikes` or `vol_fit_nonpositive`
  - `public interface IVolModel { string Id { get; } SmileFit Fit(ImmutableArray<SmilePoint> points); LatticeDecimal Evaluate(SmileParameters p, LatticeDecimal k); }`
  - `public static class VolModels { public const string QuadraticLogMoneyness = "smile_quadratic_log_moneyness_v1"; public static IVolModel Require(string id); }`. An unknown id throws `MarketDataException("vol_model_unknown", …)`.

- [ ] **Step 1: Write the failing checks** in `VolSurfaceChecks.Run()`, each a `Check(name, action)` like `FuturesOptionChecks`:
  - `quadratic smile recovers known parameters`: generate 7 points at k ∈ {−0.3,−0.2,−0.1,0,0.1,0.2,0.3} from a=0.45, b=−0.10, c=0.80. The fit returns those parameters to within 1e−25, with RMS and maximum residual of zero to within 1e−25.
  - `rich cheap equals a deliberate offset`: the same points with +0.02 added to k=0.1 only. The fit is least-squares, so assert that the residual at k=0.1 (market minus `Evaluate`) is the largest absolute residual and is positive. For an exact value, recompute by hand from the 3×3 normal equations in the test, with an independent `decimal` solve at 28 digits, and compare to within 1e−20.
  - `fewer than five strikes is a gap`: 4 points give `SmileGap("vol_fit_insufficient_strikes")`.
  - `one-sided strikes is a gap`: 5 points, all with k > 0, give `SmileGap("vol_fit_insufficient_strikes")`.
  - `nonpositive smile is a gap`: points from a=0.05, b=0, c=−2 at k ∈ ±{0.1,0.2,0.3} and 0. Their fitted smile reaches ≤ 0 within the range, giving `SmileGap("vol_fit_nonpositive")`.
  - `fit is deterministic`: two fits of the same points are equal records.
  - `unknown vol model is refused`: `VolModels.Require("smile_x_v9")` throws with code `vol_model_unknown`.
- [ ] **Step 2: Run and see them fail.** Run `~/.openetrm-tools/dotnet run --project tests/OpenEtrm.MarketData.Checks -- --vol-surface`. Expected: a build failure (the types are not defined).
- [ ] **Step 3: Implement `VolModel.cs`.**
  - Ordinary least squares on [1, k, k²] solves the 3×3 normal equations by Gaussian elimination in `LatticeDecimal`, pivoting on the largest absolute value.
  - "Both sides of the forward" means at least one point with k < 0 and at least one with k ≥ 0. Count distinct k values.
  - Non-positivity is tested at every point's k and at the vertex −b/(2c) when it lies inside [min k, max k].
  - The RMS is √(Σr²/n) using `LatticeDecimal.Sqrt`.
- [ ] **Step 4: Run the checks.** The same command now gives `PASS` for all seven.
- [ ] **Step 5: Commit.** `git add src/OpenEtrm.MarketData/Options/VolModel.cs tests/OpenEtrm.MarketData.Checks && git commit -s -m "Fit a versioned quadratic volatility smile"`

### Task 2: Assemble a surface from per-series calibrations

**Files:**
- Create: `src/OpenEtrm.MarketData/Options/VolSurface.cs`
- Modify: `tests/OpenEtrm.MarketData.Checks/VolSurfaceChecks.cs`

**Interfaces:**
- Consumes: Task 1's `IVolModel`, `SmileFit`; the existing `OptionCalibration` (`SolvedVolatility`, `IntrinsicFallback`), `FuturesOptionTerms` and `OptionRight`.
- Produces:
  - `public sealed record SurfaceSeries(string Code, OptionRight Right, ExactDecimal Strike, DateTimeOffset ExpiryAt, string UnderlyingCode, ExactDecimal? Settlement, string Outcome, LatticeDecimal? MarketVolatility)`. Outcome is `solved`, `at_intrinsic` or an error code.
  - `public sealed record SurfaceExpiryInput(DateTimeOffset ExpiryAt, string UnderlyingCode, LatticeDecimal? Futures)`. Futures is null when the underlying settlement is missing.
  - `public sealed record SurfaceSeriesResult(SurfaceSeries Series, LatticeDecimal? LogMoneyness, LatticeDecimal? Fitted, LatticeDecimal? RichCheap, bool FitPoint)`
  - `public sealed record SurfaceGridPoint(ExactDecimal Strike, LatticeDecimal LogMoneyness, LatticeDecimal Fitted)`
  - `public sealed record SurfaceExpiry(DateTimeOffset ExpiryAt, string UnderlyingCode, LatticeDecimal? Futures, string Status, SmileParameters? Parameters, int Points, LatticeDecimal? RmsResidual, LatticeDecimal? MaxAbsResidual, ImmutableArray<SurfaceGridPoint> Grid)`. Status is `fitted`, `vol_fit_insufficient_strikes` or `vol_fit_nonpositive`.
  - `public sealed record VolSurfaceResult(string ModelId, ImmutableArray<ExactDecimal> StrikeAxis, ImmutableArray<SurfaceExpiry> Expiries, ImmutableArray<SurfaceSeriesResult> Series)`
  - `public static class VolSurfaces { public static VolSurfaceResult Build(IVolModel model, ImmutableArray<SurfaceExpiryInput> expiries, ImmutableArray<SurfaceSeries> series, ExactDecimal strikeInterval); }`

- [ ] **Step 1: Write the failing checks.** The inputs are hand-built `SurfaceSeries`, with no tree:
  - `otm right is the fit point`: for strikes below F the put has `FitPoint=true` and the call false; above F, the reverse.
  - `at-the-forward strike uses the call`: a strike equal to F makes the call the fit point, with k = 0.
  - `both rights get rich cheap`: an ITM solved series has `RichCheap = Market − Fitted(at its k)`, and `FitPoint=false`.
  - `failed otm solve drops the strike, never the itm`: the OTM put at one strike has outcome `option_settlement_missing` and the ITM call solved. That strike contributes no fit point. With six strikes otherwise, the expiry still fits on five.
  - `at intrinsic and errors carry no rich cheap`: these have null `MarketVolatility`, `Fitted` and `RichCheap`, and keep their outcome.
  - `expiry without underlying is a gap, others fit`: one expiry with null Futures gets status `vol_fit_insufficient_strikes` and null k on its series, while the other expiry is `fitted`.
  - `grid spans fitted strikes at the listed interval`: the grid runs from the lowest to the highest fitted strike in steps of `strikeInterval`, with no point outside, and `Fitted` equals `model.Evaluate` at each grid k.
  - `strike axis is the union across expiries`: two expiries with offset strikes give an ascending, distinct axis.
- [ ] **Step 2: Run and see them fail.** Same command. Expected: a build failure.
- [ ] **Step 3: Implement `VolSurfaces.Build`.**
  - Compute k = `Ln(K/F)`.
  - Series are ordered by expiry, then strike, then right (call first). Expiries are ordered by instant.
  - The grid is built only for `fitted` expiries.
- [ ] **Step 4: Run the checks.** Expected: all Task 1 and Task 2 checks pass.
- [ ] **Step 5: Commit.** `git commit -s -m "Assemble a volatility surface with rich/cheap per series"`

### Task 3: Calibrate every listed series of a product at a frame

**Files:**
- Modify: `src/OpenEtrm.Api/FuturesOptionPortfolio.cs`, extracting the series calibration out of `Prepare`
- Create: `src/OpenEtrm.Api/VolSurfaceFit.cs`
- Test: `tests/OpenEtrm.Api.Checks/VolSurfaceFitChecks.cs`, registered in that project's `Program.cs` as `if(args is ["--vol-surface-fit"]){VolSurfaceFitChecks.Run();return;}`

**Interfaces:**
- Consumes: Task 2's `VolSurfaces.Build`, `SurfaceSeries` and `SurfaceExpiryInput`.
- Produces:
  - `internal static Task<FuturesOptionCalibrations> FuturesOptionPortfolio.Calibrate(NpgsqlConnection, Guid catalog, PreviewData, InputFrame, MarketSnapshot, IReadOnlyCollection<FuturesOptionInstrument>, FuturesOptionValuationPolicy, CancellationToken)`.
    - It returns the bindings, settlements, failures by instrument id and discounting.
    - It contains the logic now inline in `Prepare`: expiry, discount coverage, requests, missing curves and parallel ordered solve.
    - `Prepare` calls it for held series. Its behaviour is unchanged.
  - `internal sealed record VolSurfaceInputs(ImmutableArray<Guid> ObservationIds, VolSurfaceResult Result)`
  - `internal static Task<VolSurfaceInputs> VolSurfaceFit.Compute(NpgsqlConnection, PreviewDatabase, PreviewData, InputFrame, MarketSnapshot, string productCode, IVolModel, CancellationToken)`.
    - It calibrates every listed `ref_data.futures_option_series` of the product under `FuturesOptionValuationPolicy.Release1`.
    - It reads each expiry's underlying settlement from the same basis.
    - It returns the exact option and underlying observation ids it used, sorted.
    - An unknown product throws `vol_surface_product`. A product with no settled series throws `vol_surface_no_settlements`.

- [ ] **Step 1: Write the failing checks** against the exchange example manifest (the same source `FuturesOptionChecks` uses):
  - `surface fit reproduces each settlement's solved volatility`: every `solved` series' `MarketVolatility` equals the receipt volatility `FuturesOptionPricing.Prepare` gives that series alone.
  - `observation ids are exactly the pinned inputs`: the set equals the frame's option and underlying settlement observations for the product.
  - `unknown product is refused` and `product without settlements is refused`, with their codes.
  - `surface fit runtime`: prints the series count, solve count and elapsed milliseconds. It records them and enforces no budget.
- [ ] **Step 2: Run and see them fail.** Run `~/.openetrm-tools/dotnet run --project tests/OpenEtrm.Api.Checks -- --vol-surface-fit`. Expected: a build failure.
- [ ] **Step 3: Extract `Calibrate` and implement `Compute`.** The existing `--futures-options` MarketData checks and the option portfolio checks must stay green unchanged.
- [ ] **Step 4: Run the new checks and the existing ones.** Run `--vol-surface`, `--futures-options` and `python3 tools/check_all.py` for the option rows. Expected: all pass. Record the runtime in the commit body.
- [ ] **Step 5: Commit.** `git commit -s -m "Calibrate every listed series of an option product for its surface"`

### Task 4: Bitemporal storage of surface revisions

**Files:**
- Create: `db/migrations/0186_vol_surfaces.sql`
- Create: `tools/check_vol_surface_storage.py`, run from `tools/run_preview.py` next to `check_futures_option_storage`
- Modify: `tests/test_migration_identity.py` only if it pins the migration list

**Interfaces:**
- Produces:
  - **Tables.**
    - `trading.vol_surface_revisions`:
      - catalog_id and id;
      - the exact option product key (FK to `ref_data.futures_option_products`);
      - business date `effective_on date`;
      - `model_id text`, `solver_policy text` and the frame id;
      - `publication_order` (FK `trading.publications`), `actor_id` and `request_id`;
      - `content jsonb`, the serialized `VolSurfaceResult`, and `content_sha256 text`;
      - unique (catalog_id, product key, effective_on, model_id, publication_order).
    - `trading.vol_surface_inputs`: (revision, observation id), with an FK to `market_data.observations`.
    - `trading.vol_surface_commands`: request id → revision, as `trading.crude_lifecycle_commands`.
  - **Immutability.** The immutable triggers and restrictive RLS `history_scope` policies are copied from 0154's loop, plus `security.reference_access(…,'ReadMarketData')`.
  - **Writing.** `security.record_vol_surface(catalog uuid, request uuid, product jsonb, effective_on date, model_id text, solver_policy text, frame uuid, inputs uuid[], content jsonb, content_sha256 text) RETURNS jsonb`, `SECURITY DEFINER`. It returns `{revision, publicationOrder, knownFrom, created}`.
    - It requires `PublishMarketData` and refuses outside the current write context.
    - A known request id returns the first outcome, and the same id with a different payload raises `vol_surface_input`.
    - It locks the key, then reads the latest visible revision.
    - If that revision's input set equals `inputs` and its digest equals `content_sha256`, it returns it with `created=false`. If the input set is equal but the digest differs, it raises `vol_surface_nondeterministic`.
    - Otherwise it publishes a new revision under a new publication order.
  - **Reading.** `security.read_vol_surface(catalog uuid, product jsonb, effective_on date, model_id text) RETURNS jsonb`. It returns the latest revision visible under the current context by `security.trading_visible`, ordered by publication_order. The revision includes its inputs, `knownFrom` and actor. With none, it returns null.
- [ ] **Step 1: Write the failing SQL checks** in `check_vol_surface_storage.py`, in the `run_checks(query, manifest)` style of `check_futures_option_storage.py`:
  - `revisions are immutable` (an UPDATE or DELETE is refused);
  - `a retried request returns its first outcome`;
  - `the same request with a different payload is refused`;
  - `identical inputs and content record nothing new`;
  - `identical inputs with different content are refused as nondeterministic`;
  - `new inputs record a new revision with a later publication order`;
  - `concurrent identical fits record one revision`: two connections with different request ids on the same key, giving one row;
  - `fitting requires PublishMarketData` and `reading requires ReadMarketData`;
  - `earlier cutoff does not see later revision`, using a captured context before the second revision.
- [ ] **Step 2: Run and see them fail.** Run `python3 tools/run_preview.py --exchange-check`. Expected: FAIL, because the functions do not exist.
- [ ] **Step 3: Write the migration.** Follow 0154's grants, policies and `SET search_path=pg_catalog` exactly. Add the function names to whatever ownership and identity lists the newer migrations extend (compare `0169_risk_trade_selection.sql`).
- [ ] **Step 4: Run the checks.** Same command. Expected: all `PASS`, with the existing storage checks still green.
- [ ] **Step 5: Commit.** `git commit -s -m "Store volatility surface revisions bitemporally"`

### Task 5: Fit jobs on the risk queue, and the read

**Files:**
- Create: `src/OpenEtrm.Api/VolSurfaceStore.cs`, holding job admission, worker execution and the read
- Modify:
  - `src/OpenEtrm.Api/RiskWorker.cs`: dispatch by retained request convention. `vol_surface_fit_v1` runs the fit; portfolio risk is unchanged.
  - `src/OpenEtrm.Api/Program.cs`: routes next to `/options/*`.
  - A migration, only if the risk queue's SQL (`risk.claim`, `risk.finish` and the job list or read functions) must learn the new convention. Fold it into `0180_vol_surfaces.sql` if Task 4 is not yet merged, otherwise make it `0181`.
- Create: `tools/check_vol_surface_api.py`, run from `tools/run_preview.py`

**Interfaces:**
- Consumes: Task 3's `VolSurfaceFit.Compute` and Task 4's SQL functions. `RiskJobs.Submit`, `RiskJobs.Read` and `RiskWorker` are the patterns to reuse.
- Produces:
  - `public sealed record VolSurfaceFitRequest(Guid RequestId, Guid SnapshotId, string Product, string Model)` and `public sealed record VolSurfaceReadRequest(FuturesReadRequest Context, string Product, string Model)`. Both have `[JsonUnmappedMemberHandling(Disallow)]`.
  - `POST /api/vol-surfaces/fit`, under `PublishMarketData` and `ReadTrades`. It pins the frame, product, business date, model and actor, and returns `{jobId, state}`.
    - The same request id returns the same job.
    - A queued or running job for the same key and pinned frame is returned instead of a new one.
  - **Worker execution.** The worker claims the job and rechecks authority. It computes, serializes the result canonically and hashes it. It calls `security.record_vol_surface` and finishes the job, with the job result holding `{revision, created, series, solves, elapsedMs}`. Recording and finishing happen in one transaction.
    - A fit error finishes the job `failed` with its code and records nothing.
  - `POST /api/vol-surfaces/read`, under `ReadMarketData` and `ReadTrades`. It resolves the context as option valuation does, then returns:
    - `context`: business date, cutoff and snapshot id;
    - `model` and `solverPolicy`;
    - `state`, which is `fitted` or `notFitted`;
    - `revision`: id, knownFrom, actor and `inputsRevised`;
    - `job`: the newest fit job for the key visible to the caller, with id, state and error code, or null;
    - `canFit`: whether the caller may submit;
    - `surface`: the `VolSurfaceResult`, with camel-case fields and exact decimal text.
  - `inputsRevised` is true when any pinned observation has a newer revision visible at the cutoff.
- [ ] **Step 1: Write the failing HTTP checks**, in the style of `check_futures_option_portfolio.py`, running the risk worker the way the existing risk-job checks do:
  - `not fitted before any fit`;
  - `fit job completes and read returns the stored surface`;
  - `same request id returns the same job` and `a second submit while queued returns the queued job`;
  - `refit is a no-op`: the job completes with the same revision and `created=false`;
  - `revised settlement flags the stored surface`: record a settlement revision for one series, read, and expect `inputsRevised: true` with the surface unchanged;
  - `refit after revision records a new revision`, after which the old cutoff still reads the old one;
  - `unknown model is refused at submit`, with `vol_model_unknown`;
  - `historical context cannot fit`, with `historical_read_only`;
  - `reader without PublishMarketData sees canFit false and cannot fit`;
  - `revoked fitter's job records nothing`: revoke `PublishMarketData` after submit and before the worker runs;
  - `a failing fit finishes failed with its code`: a product with no settlements at the frame gives `vol_surface_no_settlements`;
  - `portfolio risk jobs are unaffected`: the existing risk-job checks still pass.
- [ ] **Step 2: Run and see them fail.** Run `python3 tools/run_preview.py --exchange-check`. Expected: FAIL with 404 on the new routes.
- [ ] **Step 3: Implement admission, worker dispatch and the read.**
- [ ] **Step 4: Run the checks.** Same command. Expected: all `PASS`, including the existing risk-job checks.
- [ ] **Step 5: Commit.** `git commit -s -m "Fit volatility surfaces as risk-queue jobs and read them"`

### Task 6: Example data with a synthetic smile

**Files:**
- Modify: `tools/exchange_fixture.py`, which holds `OPTION_PRODUCTS`, the strike loop around line 156 and `option_settlement`
- Modify: every check that pins option series counts, settlements or result digests. Find them with `grep -rn "nymex_on_\|nymex_lo_\|sha256" tools tests | grep -i option`.

**Interfaces:**
- Produces: seven strikes per expiry on the listed grid, at centre ± {0, 2, 4, 6} × interval except that the centre itself appears once. Each strike has a call and a put. Settlements come from a synthetic smile σ(k, T) = base + skew·k + curve·k² + term·T, plus a fixed per-series offset table that gives at least two rich and two cheap points per product.
  - The existing `AT_INTRINSIC`, `MISSING_SETTLEMENT` and missing-underlying cases are kept.
  - All values are labelled synthetic in comments.
- [ ] **Step 1: Write the failing check.** In `check_vol_surface_api.py` add `example surface shows rich and cheap`: after a fit, each product has every expiry `fitted` except the one with the missing underlying, and has at least one series with rich/cheap above +0.005 and one below −0.005.
- [ ] **Step 2: Run and see it fail.** Expected: FAIL, because three strikes give `vol_fit_insufficient_strikes`.
- [ ] **Step 3: Change the fixture, then re-pin every affected check deliberately.** Each re-pinned value gets a one-line reason in the commit body. Do not loosen any assertion.
- [ ] **Step 4: Run everything.** Run `python3 tools/check_all.py`. Expected: all green.
- [ ] **Step 5: Commit.** `git commit -s -m "Give the example options a synthetic smile across more strikes"`

### Task 7: The Vol surfaces page with the heat map and smiles

**Files:**
- Create: `web/src/vol-surface-view.ts`, holding the page, the heat map and the smiles
- Create: `web/src/vol-surface-link.ts`, holding the linked-highlight state, with subscribe and set for a series code
- Modify:
  - `web/src/navigation-groups.ts`: add `'vol-surfaces'` to Markets, gated by `ReadMarketData` and `ReadTrades`;
  - `web/src/index.html`: add `nav-vol-surfaces` and `vol-surfaces-page`;
  - `web/src/app.ts`: page switch, title "Vol surfaces", date context as on the `market` page;
  - `web/src/app.css`.
- Create: `tests/browser/vol-surfaces.mjs`, wired where `futures-options.mjs` is

**Interfaces:**
- Consumes: Task 5's read and fit responses.
- Produces: `export function setupVolSurfaces(o:{api:Api;context:()=>FuturesReadRequest;historical:()=>boolean}):{refresh():void;open(product:string):void}`. `open` is used by Task 9.

- [ ] **Step 1: Write the failing browser checks**:
  - the page is listed in Markets for a reader of market data and trades, and absent without `ReadMarketData` or `ReadTrades`;
  - a product picker and the read-only model label `smile_quadratic_log_moneyness_v1` are shown;
  - `notFitted` shows "Not fitted for this date" and a *Fit now* button only when `canFit`;
  - after *Fit now*, a queued or running job shows its state, and the page checks back until the job finishes and then shows the surface; a failed job shows its code next to the button;
  - after fitting, the heat map has one row per expiry and one column per strike axis value;
  - call and put halves are labelled, and a "not listed" cell is distinct from a gap cell, whose title gives the reason;
  - the gap expiry shows its reason in both views;
  - each smile header shows RMS and maximum residual;
  - values are shown as percentage points to two decimals, for example `1.20`;
  - `inputsRevised` shows "Inputs revised after this fit" and *Refit*;
  - hovering a heat-map cell highlights the same series' point in its smile and fills the shared readout;
  - the layout works at 375 px with no horizontal page scroll.
- [ ] **Step 2: Run and see them fail.** Run `python3 tools/run_preview.py --exchange-check --browser-check --browser <chrome>` (as in `tools/check_all.py`). Expected: FAIL.
- [ ] **Step 3: Implement the page.**
  - The heat map is an echarts `heatmap` series. The diverging colour scale is centred on 0 and uses the app's theme tokens.
  - The smiles are echarts small multiples showing the fitted grid line, market scatter shaped by right, and rich/cheap bars.
  - *Fit now* and *Refit* submit a job with a fresh request id. An uncertain response is retried with the same id, following the existing pending-command pattern (`pending-command.ts`). While the read reports a queued or running job, the page re-reads on a short fixed interval, at most every 2 seconds, and stops when the job finishes or the page closes.
- [ ] **Step 4: Run the browser checks.** Same command. Expected: `PASS`.
- [ ] **Step 5: Commit.** `git commit -s -m "Show volatility surfaces with a rich/cheap heat map and smiles"`

### Task 8: The rotatable 3D surface

**Files:**
- Create: `web/src/vol-surface-3d.ts`
- Modify: `web/src/vol-surface-view.ts` and `tests/browser/vol-surfaces.mjs`

**Interfaces:**
- Consumes: `VolSurfaceResult` from the read, and Task 7's link state.
- Produces: `export function drawSurface3d(canvas:HTMLCanvasElement,surface:VolSurfaceResult,link:SurfaceLink):{reset():void;dispose():void}`

- [ ] **Step 1: Write the failing browser checks**:
  - the canvas renders non-blank pixels in light and dark themes;
  - dragging changes the rendered image and *Reset view* restores the original bytes;
  - hovering a stalk highlights the series in the heat map;
  - the legend labels the straight panels between expiries as a visual join;
  - a gap expiry draws no mesh row.
- [ ] **Step 2: Run and see them fail.** Same browser command. Expected: FAIL.
- [ ] **Step 3: Implement the view.**
  - Use a project-owned projection, painter's-order quads, and shading by volatility level.
  - Market points sit on stalks, coloured rich or cheap.
  - Respect `devicePixelRatio`.
  - Add no library.
- [ ] **Step 4: Run the checks.** Expected: `PASS`.
- [ ] **Step 5: Commit.** `git commit -s -m "Draw the fitted volatility surface in 3D"`

### Task 9: Links from options, and the documentation

**Files:**
- Modify: `web/src/futures-option-view.ts`, which holds both the option ticket's product line and the option trade view (there is no separate option product view), and `tests/browser/futures-options.mjs`
- Create: `docs/vol-surfaces.md`
- Modify:
  - `docs/decisions.md`: add D291 (the next free number), covering the vol-model contract, the first model, bitemporal storage under the trading publication protocol, view-only scope and the fit permission;
  - `docs/futures-options.md` "Remaining work": surfaces and graphics move to shipped, while vega, vol shocks, surface valuation and scheduled fitting remain;
  - `docs/verification.md`: record what was run and the fit runtime.

- [ ] **Step 1: Write the failing browser check**: an option trade, and the option ticket once a series is chosen, each show "Volatility surface", which opens the page on that product at the current context.
- [ ] **Step 2: Run and see it fail.** Same browser command. Expected: FAIL.
- [ ] **Step 3: Implement the links** via `setupVolSurfaces(...).open(product)`, and write the docs. The docs state only what is implemented and verified.
- [ ] **Step 4: Run the full suite.** Run `python3 tools/check_all.py`. Expected: all green.
- [ ] **Step 5: Commit.** `git commit -s -m "Link options to their volatility surface and document it"`

### After the plan: the final review and the pilot

- **Final review.** One combined adversarial review of the whole branch, as the user prefers. It covers temporal behaviour, authorization and numerical correctness, as AGENTS.md requires. Resolve its findings before asking to publish.
- **Pull request and the pilot.** Open the pull request only with the user's approval. After merge and the vol-surface migrations' schema window, the pilot example-data load runs as installation work outside the repository:
  - it adds the strikes and settlement revisions idempotently;
  - it submits a fit job for each loaded date through `/api/vol-surfaces/fit` and checks each completes;
  - it is rehearsed on a disposable copy first;
  - each phase needs the user's approval in chat.
