# Rolling Rate Curves Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a published interest-rate curve definition the only kind of rate
curve. It is admitted into market definitions with one series per tenor, built at
each close from `t_0` with its own currency, unit and conventions, and used for
payment discounting. The literal-maturity rate path is removed.

**Architecture:**
- A small project-owned conventions engine in `OpenEtrm.Rates` turns marks into
  discount factors. It handles two day counts, three compoundings, two
  interpolations, three extrapolations, and the exact `t_0` anchor. It reuses
  `LatticeDecimal` for `exp`/`ln` and rounds once to 18 places.
- Admission writes one rate curve row pinned to the exact definition version, plus
  tenor rows with deterministic series identities, into a draft definition
  revision.
- At valuation, a resolver asks the date library for the pinned date list in the
  frame's exact context, turns labels into pillars, and gives `RatesCurve` a
  fully resolved definition. Every consumer (evaluate, swaps, PPAs, futures-option
  premiums) goes through it.

**Tech Stack:** C# (.NET 10.0.402), PostgreSQL 16 migrations, TypeScript web
client, Python preview integration checks, Node browser checks.

**Spec:** `docs/superpowers/specs/2026-11-07-rolling-rate-curves-design.md`

## Global Constraints

- Commands use `$DOTNET` (the SDK matching `global.json`), `$PG_BIN` (stock
  PostgreSQL 16 binaries) and `$BROWSER` (a Chromium-compatible executable).
- `AGENTS.md` applies:
  - TDD;
  - no silent defaults;
  - idempotent writes (D238);
  - historical context on every read;
  - exact-version foreign keys;
  - presentation-only TypeScript;
  - adversarial review for temporal and numerical work.
- **Offered terms**, each required, with no default. The stored codes in brackets
  are exact:
  - day count: ACT/365 Fixed (`actual_365_fixed`), ACT/360 (`actual_360`);
  - compounding: Simple (`simple`), Continuous (`continuous`), Annual (`annual`);
  - interpolation: Log-linear discount factor (`log_linear_discount`), Linear zero
    rate (`linear_zero`);
  - extrapolation: None (`none`), Flat zero rate (`flat_zero`), Flat forward
    (`flat_forward`);
  - mark unit: Percent per year (`percent_per_year`, range −10 to 100), Decimal
    per year (`decimal_per_year`, range −0.1 to 1).
- **Coming soon** (shown disabled, refused by the server):
  - `thirty_360`, `actual_actual_isda`;
  - `semi_annual`, `quarterly`;
  - `cubic_spline`, `monotone_convex`;
  - `basis_points`.
- **Arithmetic and policy:**
  - Factors are rounded once to 18 decimals, half-even.
  - `exp`/`ln` come from `LatticeDecimal` (30 digits).
  - One numerical policy, `rate_curve_lattice30_to_even_18_v1`, is recorded on
    every build.
- **Anchor:** DF(`t_0`) = 1 exactly, and it is the left bracket before the first
  pillar (owner-decided).
- Maximum span between adjacent brackets is `DiscountInterpolation.MaximumPillarDays`
  (3660); it does not apply to extrapolation.
- **Errors:**
  - `rate_maturity`: a pillar on or before `t_0`;
  - `rate_mark`: a missing mark, naming the label;
  - `rate_domain`: a mark outside its unit's range;
  - `discount_coverage`: after the last pillar with `none`;
  - `discount_interpolation`: a bracket too wide;
  - `unsupported_rate_convention`: an unknown or Coming-soon code;
  - `discount_curve`: the currency of the default ≠ the curve's currency.
- **No VaR or scenario shocks** on rate curves.
- Synthetic examples only. The hosted seed is private and never committed.
- **Sequencing:** Task 3 replaces `rate_curves`. Literal-rate fixtures stay broken
  until Task 8 converts them, so Tasks 3–7 verify with their named targeted
  checks, and only Task 8 runs the full suite.
- Do not push or publish. The migration needs a hosted schema window.

## Review Focus

- **Risk worker valuing a discounted swap.** It must resolve the curve's date list
  under its own role: expect a value, not 42501. (Task 5)
- **Historical close resolution.** A historical view of an old close resolves
  pillars from that close's own `t_0` and its schedule versions, not today's.
  (Task 4)
- **Payment dates landing on a boundary.** A payment exactly on `t_0`, exactly on a
  pillar, and exactly on the last pillar each return the exact factor with no
  interpolation. (Task 4)
- **A relabelled tenor across definition versions.** It gets a new series; a kept
  label keeps its series. (Task 3)
- **Negative rates.** A −0.5% percent mark builds a discount factor above 1.
  (Task 1)

---

### Task 1: Rate conventions engine

**Files:**
- Create: `src/OpenEtrm.Rates/RateCurveMath.cs`
- Test: `tests/OpenEtrm.Rates.Checks/RateCurveMathChecks.cs` (register in `tests/OpenEtrm.Rates.Checks/Program.cs`)

**Interfaces:**
- Produces:
  - `public sealed record RateConventionSet(string DayCount, string Compounding, string Interpolation, string Extrapolation, string MarkUnit)`
    with `static RateConventionSet Parse(...)`, which throws
    `unsupported_rate_convention`.
  - `public static class RateCurveMath`:
    - `const string Policy = "rate_curve_lattice30_to_even_18_v1"`;
    - `ExactDecimal DecimalRate(ExactDecimal mark, string markUnit)`, which throws
      `rate_domain`;
    - `ExactDecimal Factor(ExactDecimal decimalRate, int days, RateConventionSet c)`;
    - `ExactDecimal Interpolate(RateNode left, RateNode right, int days, RateConventionSet c)`;
    - `ExactDecimal Extrapolate(RateNode beforeLast, RateNode last, int days, RateConventionSet c)`;
    - `static ImmutableArray<RateChoice> Choices`, with
      `public sealed record RateChoice(string Field, string Code, string Label, bool Offered)`.
  - `public sealed record RateNode(int Days, ExactDecimal DecimalRate, ExactDecimal Factor)`.
    The anchor is `new(0, 0, 1)`.

- [ ] **Step 1: Write the failing checks.** Use the independent 50-digit oracle
  values below (each within ±1e-18):
  - `"day count and compounding factors match the oracle"`, rate 0.0425 at 1461
    days:
    - simple/365F: `0.854615803368122831`
    - continuous/360: `0.841575821458754610`
    - annual/365F: `0.846537540280258786`
    - continuous/365F: `0.843566587370921225`
  - `"interpolation matches the oracle between pillars"`, continuous/365F, 1Y =
    4.30% at 365 days and 2Y = 4.10% at 730 days, queried at 500 days:
    - `log_linear_discount` → `0.944193025537330858`
    - `linear_zero` → `0.943753012601149260`
  - `"extrapolation after the last pillar"`, same pillars, at 900 days:
    - `flat_forward` → `0.904688689863042511`
    - `flat_zero` → `0.903846358531547477`
    - `none` → `Reject("discount_coverage")`
  - `"the t_0 anchor brackets the first pillar"`: continuous/365F, 1D = 3.90% at 3
    days; day 1 with log-linear → `0.999893156393116265`; day 0 → exactly `1`.
  - `"percent and decimal marks give identical factors"`: `DecimalRate(4.25,
    percent)` equals `DecimalRate(0.0425, decimal)`. Out of range is rejected with
    `rate_domain`: `101` percent, `1.01` decimal. A mark of −0.5 percent gives a
    factor above 1.
  - `"unsupported and coming-soon conventions are refused"`: `thirty_360`,
    `semi_annual`, `cubic_spline`, `basis_points` and `""` are each rejected with
    `unsupported_rate_convention`.
- [ ] **Step 2:** Run `$DOTNET run --project tests/OpenEtrm.Rates.Checks`.
  Expected: build fails, `RateCurveMath` not found.
- [ ] **Step 3: Implement `RateCurveMath`.**
  - Year fraction = days/365 or days/360, as an exact ratio.
  - Simple uses exact integer arithmetic (as `RateConventions` does).
  - Continuous = `exp(−r·t)`; annual = `exp(−t·ln(1+r))`, using `LatticeDecimal`.
  - Log-linear between nodes reuses `DiscountInterpolation`'s exact geometric root
    on 18-place factors, after a policy check.
  - Linear zero interpolates the decimal rate in days, then applies `Factor`.
  - Flat forward:
    `DF_last·exp(−f·(d−d_last)/basis)` with
    `f = ln(DF_prev/DF_last)·basis/(d_last−d_prev)`.
  - Flat zero = `Factor(r_last, d)`.
  - Round once to 18 places, half-even.
- [ ] **Step 4:** Run the same command. Expected: all `PASS`, including the
  existing rate checks.
- [ ] **Step 5:** Commit:
  `git commit -s -m "Add rate curve conventions with independent oracles"`

### Task 2: Interest-rate definitions own their terms

**Files:**
- Create: `db/migrations/0215_rolling_rate_curves.sql` (definition columns; later
  tasks append)
- Modify: `src/OpenEtrm.Api/CurveDefinitions.cs`,
  `src/OpenEtrm.Api/CurveDefinitionStore.cs`, `src/OpenEtrm.Api/Program.cs`
  (choices)
- Test: `tests/OpenEtrm.Api.Checks/CurveDefinitionChecks.cs`,
  `tools/check_curve_definitions.py`

**Interfaces:**
- Consumes: `RateConventionSet.Parse`, `RateCurveMath.Choices`.
- Produces:
  - `CurveDefinitionDraft` loses `InterestRate` and gains `Currency`, `MarkUnit`,
    `DayCount`, `Compounding`, `Extrapolation` (all `string?`, defaulting to
    `null`). The existing `Interpolation` carries the rate interpolation for
    `interest_rate`.
  - `CurveRateTerms` becomes `(string Currency, RateConventionSet Conventions)`.
  - `CurveReferenceTerms.InterestRates` is replaced by a `Currencies` set.
  - `curve_definition_versions` gains `currency_code` (FK to `ref_data.currencies`
    by reference revision), `mark_unit`, `day_count`, `compounding` and
    `extrapolation`, and loses `interest_rate_code`.

- [ ] **Step 1: Write the failing checks.**
  - Managed check `"a rate definition names every term explicitly"`: a draft with
    any one term missing is rejected with `curve_definition` naming it; an unknown
    currency is rejected with `reference_choice`; `semi_annual` is rejected with
    `unsupported_rate_convention`; a contract kind supplying any rate term is
    rejected.
  - In `check_curve_definitions.py`:
    - saving and publishing a rate curve with USD, `percent_per_year`,
      `actual_365_fixed`, `continuous`, `log_linear_discount`, `flat_forward`
      round-trips every term;
    - the existing curve-definition catalog's `choices` lists the Coming-soon
      codes with `offered:false`.
- [ ] **Step 2:** Run `$DOTNET run --project tests/OpenEtrm.Api.Checks -- --schedules`.
  Expected: FAIL (unknown members).
- [ ] **Step 3: Implement.**
  - Validate the terms in `CurveDefinitions` (rate kind requires all of them;
    contract kinds refuse them).
  - Persist and read them in `CurveDefinitionStore`.
  - Migration 0215 section 1:
    - drop `interest_rate_code` and its foreign key;
    - add the columns with `CHECK` lists of offered codes;
    - rewrite the kind `CHECK` so `interest_rate` requires all five plus
      `interpolation`;
    - refuse to run if any `interest_rate` version exists.
- [ ] **Step 4:** Run the managed checks, then
  `LC_ALL=en_US.UTF-8 python3 tools/run_preview.py --pg-bin $PG_BIN --dotnet $DOTNET --contract-dates-check`.
  Expected: PASS.
- [ ] **Step 5:** Commit:
  `git commit -s -m "Interest-rate definitions own currency, unit and conventions"`

### Task 3: Admit a published rate definition

**Files:**
- Create: `src/OpenEtrm.Api/RateCurveAdmission.cs`
- Modify: `db/migrations/0215_rolling_rate_curves.sql` (section 2),
  `src/OpenEtrm.Api/Program.cs` (route `POST /api/curve-definitions/admit`)
- Test: `tools/check_curve_definitions.py`

**Interfaces:**
- Produces:
  - `RateCurveAdmissionRequest(Guid RequestId, Guid RevisionId, Guid DefinitionId, int Version, string ValueSpecification)`.
  - `RateCurveAdmissionReceipt(Guid RequestId, string Code, ImmutableArray<(string Label, Guid SeriesId, bool Created)> Tenors)`.
  - `RateCurveAdmission.TenorSeries(Guid catalog, string code, string label) -> Guid`.
    This is a name-based UUID of `"rate_tenor:{code}:{label}"` in the catalog
    namespace (`AgentTools.NameUuid`).
  - **Table `market_data.rate_curves`** is replaced:
    - columns: `catalog_id, revision_id, code, reference_catalog_id, reference_revision_id, definition_id, definition_version, source_code`;
    - foreign key to `curve_definition_versions(id, version)`;
    - a trigger requires that version to be published and of kind
      `interest_rate`.
  - **New table `market_data.rate_curve_tenors`**:
    - columns: `catalog_id, revision_id, curve_code, ordinal, label, series_id`;
    - primary key `(catalog_id, revision_id, curve_code, ordinal)`;
    - unique `(…, curve_code, label)` and `(…, series_id)`;
    - foreign keys to `rate_curves` and `series`.
  - **Request ledger** `market_data.rate_curve_admission_requests`, following
    `contract_materialization` requests.

- [ ] **Step 1: Write the failing checks** in `check_curve_definitions.py`:
  - `"admitting a published rate definition creates one series per label"`;
  - `"a retried admission returns its first receipt; a changed payload under the same id is refused"`;
  - `"a draft or unpublished version cannot be admitted"`;
  - `"re-admitting into a later revision reuses each label's series"`;
  - `"a new version that renames a label gets a new series for it and keeps the others"`;
  - `"the value specification must be dimensionless with the unit's exact range"`
    (−10/100 for percent);
  - `"admission requires WriteMarketDefinitions"`: a reader gets 403.
- [ ] **Step 2:** Run
  `LC_ALL=en_US.UTF-8 python3 tools/run_preview.py --pg-bin $PG_BIN --dotnet $DOTNET --contract-dates-check`.
  Expected: FAIL (404 on `/admit`).
- [ ] **Step 3: Implement** `RateCurveAdmission.Admit(NpgsqlConnection, RateCurveAdmissionRequest, CancellationToken)`:
  - **Authority and context:** require `current_write_context()` and
    `WriteMarketDefinitions`.
  - **Order of work:** lock first, replay a recorded request id, then validate
    that the target revision is a draft.
  - **Rows:** insert the curve, tenor and series rows (series kind `zero_rate`,
    value specification as given, idempotent on conflict); then record the
    request.
- [ ] **Step 4:** Run the same command. Expected: PASS.
- [ ] **Step 5:** Commit:
  `git commit -s -m "Admit published rate definitions with stable tenor series"`

### Task 4: Build a rolling curve at a close

**Files:**
- Create: `src/OpenEtrm.Api/RateCurveResolver.cs`
- Modify:
  - `src/OpenEtrm.Rates/RatesCurve.cs`;
  - `src/OpenEtrm.Rates/ZeroRateCurve.cs` (becomes the pillar build for a resolved
    definition);
  - delete `src/OpenEtrm.Rates/ZeroRateLookup.cs`;
  - `src/OpenEtrm.Api/PreviewDatabase.cs` (remove `RateDefinition`/`BuildRates`
    over literal points);
  - `src/OpenEtrm.Api/MarketDates.cs`.
- Test: `tests/OpenEtrm.Rates.Checks/RatesCurveChecks.cs`,
  `tests/OpenEtrm.Rates.Checks/DiscountLookupChecks.cs`,
  `tools/check_curve_definitions.py`

**Interfaces:**
- Consumes: `RateCurveMath`; `ContractDateStore.ResolveDateList`;
  `SelectedClose.Pin` (for the frame's exact `ScheduleContext`).
- Produces:
  - `public sealed record RateTenor(string Label, SeriesKey Series, DateOnly Maturity)`.
  - `public sealed record RateCurveDefinition(CurveKey Key, ReferenceKey Currency, ReferenceKey Source, RateConventionSet Conventions, Guid DefinitionId, int DefinitionVersion, ImmutableArray<RateTenor> Tenors)`.
    This replaces `ZeroRateDefinition`.
  - `ZeroRateValue` gains `string Label` and `string Mark`. Its rate is in the
    mark unit, and `RatePercent` is removed.
  - `ZeroRateBuild` gains `RateConventionSet Conventions` and
    `string MarkUnitLabel`. `NumericalPolicy` is `RateCurveMath.Policy`.
  - `RatesCurve(RateCurveDefinition, MarketSnapshot)` has no separate policy
    argument.
  - `RateCurveResolver.Resolve(NpgsqlConnection, PreviewData, InputFrame, string code, CancellationToken) -> Task<RateCurveDefinition>`.

- [ ] **Step 1: Write the failing checks.**
  - Managed (`RatesCurveChecks`), with a hand-built `RateCurveDefinition`:
    - `"pillars use the definition's conventions"`;
    - `"a payment between t_0 and the first pillar uses the anchor"`;
    - `"payments exactly on t_0, a pillar and the last pillar are exact"`;
    - `"a missing tenor mark names the label"` (`rate_mark`);
    - `"a pillar on t_0 is refused"` (`rate_maturity`).
  - Integration (`check_curve_definitions.py`), on an admitted curve with marks at
    two closes:
    - `"the same tenor rolls to a later maturity at a later close"`;
    - `"evaluate returns labels, maturities, marks in the declared unit, discount factors and backend unit labels"`;
    - `"a historical view resolves pillars from that close's own t_0"`.
- [ ] **Step 2:** Run
  `$DOTNET run --project tests/OpenEtrm.Rates.Checks`, then the preview command
  from Task 3. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - The resolver:
    - reads the curve and tenor rows from the frame's definition revision;
    - resolves the pinned definition's date list with the frame's
      `SelectedClose` context;
    - matches labels to tenor rows in order (any mismatch raises
      `rate_definition`);
    - returns the definition.
  - `MarketDates` evaluates rate curves through the resolver.
- [ ] **Step 4:** Run both commands. Expected: PASS.
- [ ] **Step 5:** Commit:
  `git commit -s -m "Build rolling rate curves at each close from their definitions"`

### Task 5: Discount payments with the rolling curve

**Files:**
- Modify:
  - `db/migrations/0215_rolling_rate_curves.sql` (section 3: drop
    `currency_discount_defaults.interpolation_policy`; replace
    `security.swap_discount_default` and `security.ppa_discount_default` so they no
    longer return it; grant the risk worker the date-list reads the resolver
    needs);
  - `src/OpenEtrm.Rates/PaymentDiscount.cs`;
  - `src/OpenEtrm.Api/SwapStore.cs`;
  - `src/OpenEtrm.Api/PpaReads.cs`;
  - `src/OpenEtrm.Api/FuturesOptionPortfolio.cs`.
- Test:
  - `tests/OpenEtrm.Rates.Checks/MonthlySwapRateChecks.cs`;
  - `tests/OpenEtrm.Rates.Checks/DiscountLookupChecks.cs`;
  - `tools/swap_fixture.py`, `tools/check_swaps_storage.py`;
  - risk-policy lists in `tests/OpenEtrm.Api.Checks/{CurveRiskAxisChecks,HistoricalRiskContractChecks,PpaPortfolioChecks,SwapRiskHoldingChecks}.cs`.

**Interfaces:**
- Produces:
  - `CurrencyDiscountCurve(Guid Id, ReferenceKey Currency, RateCurveDefinition Definition)`.
    Construction raises `discount_curve` when the currencies differ.
  - `PaymentDiscount.InterpolationPolicy` is replaced by
    `RateConventionSet Conventions`.

- [ ] **Step 1: Write the failing checks.**
  - Managed: `"a swap payment discounts from the rolling curve to the oracle"`. Use
    the Task 1 pillars; a payment at 500 days must give
    `0.944193025537330858` × the cash flow.
  - Managed: `"a default whose currency differs from the curve's is refused"`.
  - Integration: in the swap fixture's priced trade, its discounted PV equals the
    undiscounted PV times the independent factor.
  - Integration: `"the risk worker values a discounted swap"` (run under
    `openetrm_risk_worker`).
- [ ] **Step 2:** Run the managed checks, then
  `LC_ALL=en_US.UTF-8 python3 tools/run_preview.py --pg-bin $PG_BIN --dotnet $DOTNET --swap-risk-holdings-check`.
  Expected: FAIL.
- [ ] **Step 3: Implement.** `CurrencyDefault` resolves the curve with
  `RateCurveResolver`. Update the pinned policy lists in the four risk checks
  from `ZeroRateCurve.Policy` and `DiscountInterpolation.Policy` to
  `RateCurveMath.Policy`.
- [ ] **Step 4:** Run the commands from Step 2. Expected: PASS.
- [ ] **Step 5:** Commit:
  `git commit -s -m "Discount payments with definition-driven rate curves"`

### Task 6: Show rolling curves for inspection

**Files:**
- Modify: `src/OpenEtrm.Api/MarketNavigation.cs`, `src/OpenEtrm.Api/MarketReads.cs`,
  `web/src/app.ts`, `web/src/market-view.ts`
- Test: `tests/browser/curve-definitions.mjs`

**Interfaces:**
- Consumes: Task 4's evaluate payload: `points[].label`, `points[].mark`,
  `markUnitLabel`, `points[].discountFactor`, `definition {id, version}`.

- [ ] **Step 1: Write the failing browser checks:**
  - `"a rate curve page lists label, maturity, mark and discount factor"`;
  - `"the unit label comes from the server"`: a decimal-unit curve shows
    "per year", not "% per year";
  - `"the page links to the curve's definition and has no edit control"`.
- [ ] **Step 2:** Run
  `LC_ALL=en_US.UTF-8 python3 tools/run_preview.py --pg-bin $PG_BIN --dotnet $DOTNET --contract-dates-check --browser-check --browser "$BROWSER"`.
  Expected: FAIL.
- [ ] **Step 3: Implement.**
  - The board and navigation list admitted rate curves by definition name.
  - Remove the hard-coded `% per year` in `app.ts:498`; render
    `markUnitLabel`.
  - The table shows a Label column, and the chart plots pillars by maturity.
- [ ] **Step 4:** Run the same command. Expected: PASS.
- [ ] **Step 5:** Commit: `git commit -s -m "Show rolling rate curves by tenor"`

### Task 7: Editor terms

**Files:**
- Modify: `web/src/curve-definition-editor.ts`, `web/src/index.html`
- Test: `tests/browser/curve-definitions.mjs`

- [ ] **Step 1: Write the failing browser checks:**
  - `"a rate curve shows required currency, mark unit, day count, compounding, interpolation and extrapolation selects"`;
  - `"coming-soon conventions are disabled"`;
  - `"the interest-rate specification select is gone"`.
- [ ] **Step 2:** Run the Task 6 command. Expected: FAIL.
- [ ] **Step 3: Implement** from the catalog's choices. Selects start with no
  selection (no default), one field per row.
- [ ] **Step 4:** Run it. Expected: PASS.
- [ ] **Step 5:** Commit: `git commit -s -m "Edit rate curve terms in Curve definitions"`

### Task 8: Remove the literal-maturity rate path

**Files:**
- Modify: `db/migrations/0215_rolling_rate_curves.sql` (section 4):
  - refuse to run while any `rate_curve_points` row exists;
  - drop `market_data.rate_curve_points` and `market_data.validate_rate_revision`'s
    point checks;
  - the old `rate_curves` columns were already replaced in Task 3.
- Create: `tools/rate_curve_fixture.py`, a shared helper that emits:
  - a published date list;
  - an admitted rate definition;
  - tenor marks per frame;
  - a discount default.
- Modify: every literal-rate fixture and check:
  - `tools/{preview_fixture,swap_fixture,factor_swap_fixture,exchange_fixture,mixed_preview_fixture,reference_demo_fixture}.py`;
  - `tools/{check_preview,check_rate_schema,check_market_dates,check_market_navigation,check_market_read_authority,check_meter_finality,check_ppa_storage,check_vol_surface_api,check_close_risk_upgrade,check_discount_default_upgrade,demo_date_plan}.py`;
  - `tests/OpenEtrm.Api.Checks/{BookedScheduleLookupChecks,PreviewDataChecks}.cs`;
  - `tests/OpenEtrm.MarketData.Checks/ScheduleGraphChecks.cs`;
  - `docs/examples/market-model-synthetic.json`.
  Each one is converted to the helper, or deleted where it only tested literal
  points.
- Test: `git grep -n rate_curve_points -- src tools tests web docs/examples` has
  no hits outside migrations 0006–0214.

- [ ] **Step 1: Write the failing checks.**
  - `check_rate_schema.py`: `"the migration refuses while literal rate points exist"`;
  - `check_rate_schema.py`: `"rate_curve_points no longer exists after it"`.
- [ ] **Step 2:** Run
  `LC_ALL=en_US.UTF-8 python3 tools/check_all.py --pg-bin $PG_BIN --dotnet $DOTNET --browser "$BROWSER"`.
  Expected: FAIL in the rate-schema check.
- [ ] **Step 3: Implement** the migration section, the shared fixture helper and
  the conversions.
- [ ] **Step 4:** Run the same command. Expected:
  `All synthetic verification suites passed.`
- [ ] **Step 5:** Commit: `git commit -s -m "Remove literal-maturity rate curves"`

### Task 9: Decision record and docs

**Files:**
- Modify:
  - `docs/decisions.md`: add D359 (definition-driven rate curves, the `t_0`
    anchor, owned conventions, removal of the literal path, no rate VaR); update
    the D358 and D318 rows;
  - `docs/curve-definitions.md`;
  - `docs/market-data.md`;
  - `docs/plan.md`: bootstrapped curves and the remaining Coming-soon
    conventions are unfunded future work;
  - the spec's status line.

- [ ] **Step 1:** Write the docs; state only verified behaviour.
- [ ] **Step 2:** Run `git grep -nE "rate_curve_points|interest_rate_code" -- docs`.
  Expected: hits only in historical decisions.
- [ ] **Step 3:** Commit:
  `git commit -s -m "Document definition-driven rate curves (D359)"`

### Task 10: Private demo seed (outside the repository)

**Files:** a private seed script kept outside the repository (never committed).

- [ ] **Step 1: Write the script.** It must be idempotent:
  - delete `usd_zero_*`, `demo_discount_curve`, their per-day defaults and their
    observations;
  - publish the "USD rate tenors" date list (labels and indexes as in the spec);
  - publish "Synthetic USD zero rates" with USD, `percent_per_year`,
    `actual_365_fixed`, `continuous`, `log_linear_discount`, `flat_forward`;
  - admit it into the demo's definition revision;
  - write synthetic percent marks per tenor at every demo close, upward then flat
    at 3.9–4.4%, with deterministic small moves;
  - pin them into each frame (as `frame-rate-pins.py` does);
  - set one USD discount default.
- [ ] **Step 2:** Rehearse on the twin
  (`ONE_DATASET_TARGET=twin`) after deploying this branch's migration there.
  Expected: one USD rate curve renders at several closes, and a discounted swap PV
  is below its undiscounted PV.
- [ ] **Step 3: Stop.** Applying to the pilot needs the owner's go-ahead and the
  hosted schema window (memory: hosted pilot schema releases).
