# Business scenarios and risk-engine acceptance contract

Status: current coverage inventory plus proposed next implementation. A planned
scenario below is not a passing test or implemented settlement/risk capability.
Examples are synthetic, with explicit quantities, units and conventions. Missing
settlement evidence is an error, never a last-price substitution or zero.

## What is executable now

| ID | Scenario and expected result | Executable evidence |
| --- | --- | --- |
| EXP01 | Immediately before the base future expires, dependent power is 29.1; exactly at expiry an explicitly supplied final future value of 3.2 gives 30.5 under the configured frozen-final rule | `ExpiryTransitionChecks.cs` |
| EXP02 | The same final evidence does not change a require-live rule: reject at expiry | `ExpiryTransitionChecks.cs` |
| EXP03 | Missing final, ordinary daily settlement labeled as final, preliminary final, premature final or observation beyond cutoff: reject the particular invalid input | `ExpiryTransitionChecks.cs` |
| EXP04 | The dependent remains priceable until its own later expiry; the boundary itself rejects live-root pricing. A stale live future value of 900 cannot replace the configured frozen final | `ExpiryTransitionChecks.cs` |
| ROLL01 | Prompt resolves to the old dated contract immediately before its recorded expiry and the next eligible dated contract at the boundary; earlier receipts remain unchanged | `ContractSequenceChecks.cs` |
| ROLL02 | Missing eligibility, coverage/horizon, explicit date role or listing data raises an error. Appending the far-end contract does not change the earlier prompt | `ContractSequenceChecks.cs` |
| ROLL03 | Published monthly schedule versions resolve start/end/last-trading references in one retained effective/known context; backdated corrections preserve old views | `SchedulePublicationChecks.cs`, `check_schedule_publication.py`, `check_temporal_preview.py` |
| MIX01 | Explicit daily/monthly/BALMO mappings span mid-month through following month-end. Daily and BALMO benchmark changes affect their own dependencies; missing daily marks cannot be filled by monthly/BALMO marks | `MixedResolutionChecks.cs`, `check_mixed_preview.py` |
| SEC01 | Viewer cannot administer; administrator has no implicit market read. Scoped suspension blocks historical replay and restoration permits original pins again | `check_admin_preview.py`, `check_temporal_storage.py` |
| SEC02 | Stale edits and payload reuse conflict; concurrent identical commands produce one account/audit; private-table access, cross-workspace changes, expired admission and agent role without delegation are denied | `check_admin_preview.py`, `check_temporal_storage.py` |
| SEC03 | Browser creates/edits/suspends/restores, filters accounts, denies last-admin removal, clears stale forms after actual two-session revocation, and retains/retries the identical request after a lost or malformed post-commit response | `tests/browser/administration.mjs` |
| PWR00 | Negative power outright/basis prices retain signed values and exact money; independently supplied outright prices have no inferred gas exposure | `PowerCurveChecks.cs` |
| UNIT01 | Price × explicit heat rate cancels the declared gas quantity to a power-price unit; compatible price × quantity returns money. Reject mismatched references, reversed ratios and mixed quote scales, including zero-valued operands | `TypedPriceChecks.cs` |
| UNIT02 | Preserve quote multipliers through arithmetic; validate final quote units/domain without imposing that domain on a scaled intermediate. Derived prices retain exact precision without clearing-increment rounding; unsupported precision and overflow reject | `TypedPriceChecks.cs` |
| FUT01 | Three independently executed holdings in two books share one exact futures contract with explicit pricing bindings; changed contract marks reprice every holding while preserving signed quantities and execution terms | `FuturesChecks.cs`, `check_futures_preview.py` |
| FUT02 | Tick, lot, exact unit/convention, live-interval and full pinned-input mismatches reject; a zero execution P&L retains its open quantity and Unprocessed settlement | `FuturesChecks.cs`, `check_futures_storage.py` |
| FUT03 | Backdated trades stay out of pre-commit contexts; exact effective instants exclude later same-day executions; missing certificates and direct runtime writes fail | `check_futures_preview.py`, `check_futures_storage.py` |
| FUT04 | Identical concurrent retries produce one booking; uncertain/denied retries retain identity, current revocation clears rows, and selecting a shared contract preserves its exact curve | `check_futures_preview.py`, `tests/browser/futures.mjs` |
| BLT01 | Explicit book/portfolio filters scope trade rows and totals; unknown saved identifiers, unsupported measures and incomplete pivot contracts reject | `check_futures_preview.py` |
| BLT02 | Pivot totals reconcile with trade arithmetic, keep zero-net groups, partition incompatible units/currencies and count distinct trades rather than bindings | `FuturesChecks.cs` |
| BLT03 | Selected columns, slicing and named views survive browser reload; snapshot export carries exact display values/context; common header dates and active profile update correctly | `tests/browser/futures.mjs`, `tests/browser/market-context.mjs` |
| BLT04 | The four weekday demo dates show 2/4/6/8 cumulative trade bindings across two books; repeat loading is idempotent, exact P&L matches independent oracles and old knowledge views exclude later bookings | `check_seeded_preview.py` |
| TIME01 | The common knowledge context applies to curves, rates and reference views; later corrections stay out of earlier receipts; date/context changes clear stale rows | `check_market_temporal.py`, `tests/browser/market-context.mjs` |
| RSK01 | A 0.10 gas shock through the explicit heat-rate 7 recipe changes a 10,000-unit power position by 7,000; independent all-in marks remain unchanged | `RiskChecks.cs` |
| RSK02 | Ten thousand eligible holdings share three unit calls for base/up/down; distinct books and execution bases stay independent; affected curves rebuild once | `RiskChecks.cs`, `FuturesChecks.cs`, `RiskBenchmark.cs` |
| RSK03 | Nonlinear and zero-delta examples retain gamma and actual scenario values; exact quotient, unit/domain and missing-input failures do not become rounded/zero risk | `RiskChecks.cs` |
| RSK04 | A risk analyst uses the blotter's retained context; current revocation blocks recalculation/export, edits clear results and Trader gains no scenario authority | `check_risk_preview.py`, `tests/browser/risk.mjs` |
| RSK05 | Nested signed quote shifts compose on the parent; siblings, observed facts and failed-parent recovery stay isolated. Nonlinear risk reprices the complete stressed baseline | `NestedScenarioChecks.cs`, `check_risk_preview.py`, `tests/browser/risk.mjs` |
| RSK06 | Durable futures jobs pin financial context and exact inputs, reconcile identical admission, fence stale workers and preserve cancellation/completion ordering under current authority | `check_risk_jobs.py`, `check_risk_jobs_db.py`, `tests/browser/risk.mjs` |
| SWP01 | Actual index fixings replace only their own projected observations, retain correction history and remain frozen under live quote shocks | `IndexFixingChecks.cs`, `check_index_fixings.py` |
| SWP02 | Managed multi-year monthly strips preserve explicit quantity allocation, per-payment discount inputs and shared-trade scenario values | `MonthlySwapChecks.cs` |
| CLOSE01 | Generated-close reports retain futures execution P&L and swap PV as separate measures while sharing commodity quote scenarios | `PilotCloseChecks.cs`, `check_pilot_close.py` |

The HTTP futures example has a fixed size of 40 units per lot and mark 3.00.
Book A holds +2 lots executed at 2.90 and +1 at 2.95; Book B holds −1 at 3.10.
Execution P&L is respectively 8, 2 and 4 currency units, with book totals 10 and 4.
All holdings retain one shared exact futures contract with explicit pricing bindings. This is a
price/holdings oracle, not a daily-settlement or futures-PV qualification.

The expiry price oracle is `(future + 0.1 + 0.2) × 7 + 4 + 2`: 29.1 at
future 3.0 and 30.5 at final 3.2. The multiplier and every dependency are explicit
fixture inputs, not an inferred energy conversion.

The contract-sequence kernel tests import supplied dated-contract rows. The editor
separately publishes independent date schedules. These checks do **not** establish
published schedule → activated futures/forward definitions → automatic business
lifecycle integration. Contract-date candidates are still candidates. Likewise,
rejecting an expired live OTC constituent is not picking up a realized index fixing.

Run the managed expiry, dependency, mixed-resolution and contract-sequence tests:

```sh
dotnet run --project tests/OpenEtrm.MarketData.Checks
```

The checked-in browser scenarios are invoked only by the disposable preview runner,
which supplies a matching private target/health identity and a fresh browser profile:

```sh
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin \
  --dotnet /path/to/dotnet --check --browser-check \
  --browser /path/to/chromium-compatible-browser
```

The runner owns and cleans its database/application/browser resources. Browser
checks accept no arbitrary application URL. Native database builds are unnecessary.
The [CI workflow](verification.md) invokes these scenarios. Its complete command
passed on the Ubuntu 24.04 hosted runner on 2026-10-17; verification records the
run and environment separately from application capability claims.

## Next settlement/roll acceptance scenarios

These independently stated acceptance cases distinguish implemented pricing
foundations from remaining lifecycle operations. SET01 records existing fixing
valuation and the remaining exact two-day oracle; SET02 and ROLL04 still require
their business operations. Planned cases are not passing placeholders or skipped
tests counted as coverage.

### SET01 — Daily OTC fixings replace only their own unresolved periods

Choose an explicitly cash-settled two-day receive-index/pay-fixed instrument:
100 units per day, fixed price 3.00 currency/quantity-unit, no fees, discount factor
1, fixed contractual dates and an explicitly bound daily index/publication rule.
Day 1 has a final fixing of 2.80; day 2 remains an unexpired forward mark of 3.40.

| Context/action | Expected economic value | Expected behavior |
| --- | --- | --- |
| Day 1 fixed, day 2 floating | `100 × (2.80−3.00) + 100 × (3.40−3.00) = 20` | Retain both obligations; only the second period consumes a live market factor |
| Shock day 2 +0.10 | 30 | Scenario P&L +10; day 1 fixing unchanged |
| Shock only an unrelated live curve | 20 | No affected-pricer evaluation beyond a reusable baseline |
| Day 2 reaches its fixing boundary; required final unavailable | Explicit unresolved-fixing error | Expiry neither supplies a fixing nor extinguishes the obligation |
| Later correct day 1 to 2.90 | New knowledge view 30 | The earlier knowledge view still returns 20 |
| Both periods finally fixed | Sum from those exact final fixings | No live commodity-price exposure for the fixed amounts; payment and any declared discount/FX dependencies remain separate |

The source identity, observation period, publication lag/finality, price component,
weights and revision policy belong to the settlement definition. Index differences
must resolve both required components. Averaging/TOU/partial publication requires
its own explicit supported weights and coverage; it must not emerge from matching
contract labels. A corrected actual is a new fact/knowledge context, not a shock.

D165 and D172 now implement this mechanism for monthly and 31-day daily periods.
`check_index_fixings.py` and `IndexFixingChecks.cs` cover partly fixed periods,
missing and wrong actuals, frozen actuals under shocks and corrections. A
pre-correction snapshot, retained context or certified cutoff keeps the original
value. This exact two-day oracle is not yet a check. Fixing publication, accepted
trade fixings and cash obligations are designed in the
[settlement plan](settlement-plan.md) (P39).

### SET02 — Futures daily settlement preserves open exposure

Choose an explicitly cash-settled future, quantity 10,000 quote units, multiplier 1,
execution price 3.00, no fees/FX and no discounting. Define daily variation and final
settlement rules explicitly. Actual posting/payment status is separately retained.

| Event/valuation | Independently expected result |
| --- | --- |
| Daily settlement 3.10 | Variation attributable to execution-to-settlement move +1,000 |
| Immediately after daily reset at 3.10 | Remaining mark-to-settlement value 0; +0.10 live-price scenario still changes value by +1,000 |
| Current tradable value 3.15 | Unsettled move since last settlement +500; execution-to-current total +1,500 |
| Daily variation + residual reconciliation | +1,000 + 500 = +1,500; no double counting of settled amounts |
| Supported final settlement 3.20 becomes final | Execution-to-final total +2,000; preserve outstanding cash status independently |
| After finalization under that cash-settled definition | Ordinary live-quote shocks cannot alter the final amount |
| Required daily/final publication absent or provisional | Explicit missing/unresolved result; no final-from-daily substitution |

Last trading, final-price determination, publication, cash settlement and physical
obligations are different events. These numbers do not specify physical futures,
collateral/interest, multi-day final averaging or automatically remove risk at a
last-trading timestamp. Unsupported lifecycle conventions must fail explicitly.

### ROLL04 — Published schedules actually drive activated contract rolls

Create the independent schedule first; publish it; use its retained references to
define dated contract start/end/last-trading dates; explicitly activate a complete
eligible contract set. Before/at/after each boundary, check the selected dated ID,
prices and factor identity. Adding the far-end contract is a separate publication
with listing eligibility. Correcting the schedule creates a new version/context;
it does not rewrite booked dated instruments or captured runs.

A prompt alias advances its selection in a new context. A trade already bound to
a dated instrument keeps that instrument. Rolling the trade is an explicitly
requested economic operation, not a side effect of looking up the prompt again.
This integration remains unimplemented; ROLL01–03 cover its separate foundations.

## Power curves and settlement acceptance cases

These cases include executable interval pricing, explicit block shaping,
actual-price resolution and rate-to-energy checks (D174). Booked interval sides,
settlement obligations and operator billing remain work. Each case states its
current evidence separately. Official market research informs the requirements;
public examples remain fictional and do not redistribute actual market definitions.

### One curve engine, explicit power specifications

Keep the shared curve/dependency abstraction. A power curve's common specification
must bind its commodity, location identity (node/hub/zone), market/index identity,
price component, quote units/currency, market clock, schedule/profile and settlement
rule through exact versioned references. Day-ahead and real-time are separate
identities. A published total LMP does not imply independently marked component
factors or a gas dependency. Composed energy/congestion/loss pricing requires
explicit component sources and recipe; avoid adding components to an already
all-in price. Published hub observations are not inferred from geographic proximity.

A constituent retains its dated start/end and selected profile. Monthly peak,
monthly off-peak and hourly forward curves can be separate curves with explicit
mappings. Date-only MarketContract bounds do not represent an hourly or subhourly
contract. The implemented interval-curve path retains exact UTC interval bounds
separately. Keep operating dates and market hour
labels as separate calendar metadata. User display time zones do not change them.

Delivery/quantity profiles, price-shaping rules and settlement aggregation weights
are distinct. A monthly block mark does not determine unique hourly prices.
Disaggregation requires an explicit qualified price shape/calibration rule;
aggregation requires its stated sampling/weighting policy. Do not silently flatten
monthly prices or reuse trade volume weights as the index's own settlement weights.
Cross-resolution mappings must account for the precise included intervals and
coverage. Realized index samples are interval observations; not every five-minute
sample needs to become a separately tradable forward contract.

The implemented interval-price path supports hourly and subhourly curves with
independent interval marks or explicit monthly peak/off-peak block shaping.
Separate day-ahead and real-time specifications preserve identity without an
inferred relationship. A monthly mark is disaggregated only under its selected
shape and calibration contract, preserving the exact block average.
No regional peak definition, holiday set, DST rule, feed resolution or averaging
method is supplied as a default. Initial scope need not include physical scheduling,
capacity/ancillary services, transmission rights, uplift or a complete market invoice.

### First hourly specification to review

| Level | Explicit fields/meaning |
| --- | --- |
| Common curve specification | Power commodity; exact node/hub/zone; day-ahead or real-time index; total price or explicit component; currency/MWh; market clock; source/quality policy; contract schedule and fixing/settlement definition |
| Dated hourly constituent | Stable identity; start/end UTC instants; retained market operating date/hour label; exact schedule/specification versions; independently supplied last-trading/fixing milestones where required |
| Observations | Exact constituent or index/interval identity; source event/revision; mark versus actual; provisional/final status; observed period distinct from publication and certified knowledge time |
| Pricing recipe | Direct hourly mark initially; later explicitly mapped hub + location spread or other qualified recipe. No automatic gas or component dependencies |
| Historical/scenario context | Exact effective/knowledge coordinates and retained inputs; ordinary shocks affect variable inputs, while resolved actuals remain fixed |

Start with a short synthetic horizon around an operating-day boundary plus a DST
fixture. Check exact period identity, timeline coverage, missing hours/duplicate
labels, source selection and signs before introducing block calibration or full
year volumes. The schedule is created/published before contract fields bind it.
Calendar dates stay calendar dates; UTC instants are required for intraday bounds.
The shape/profile selects intervals without redefining their price factor identity.

**Fixing is independent of interval end and last trading.** A day-ahead index can
already be known for an upcoming hour; a final real-time index may be known only
after the hour. Resolve the exact contractual index, observation period, permitted
status and retained knowledge context. Do not choose live versus fixed by testing
only `now >= end`. Trading/listing eligibility and cash settlement stay separate.
Interval-price resolution implements its explicit due rule and certified actual
history. Booked interval-side settlement remains separate work.

### PWR01 — A partially fixed hourly power contract

Choose two distinct one-hour UTC intervals, 10 MW in each, receive a specified
location/index price and pay 50 currency/MWh, discount factor 1, no fees. First
interval is finally fixed at 40; second remains a forward mark of 60.

- Each one-hour interval contains 10 MWh; total energy is 20 MWh.
- Economic value is `10 × (40−50) + 10 × (60−50) = 0`.
- A +1 currency/MWh shock to the second interval gives value +10. The first
  interval's realized fixing does not change. A common upstream shock must affect
  only the still-variable dependency paths.
- If the second period has ended but its required actual is not yet eligible,
  return an explicit unresolved fixing; never retain its expired forward silently.
- A day-ahead fixing published before the operating hour must be usable when its
  explicit settlement rule and knowledge context allow it. An hour ending does
  not supply a missing real-time final.
- A later published actual/correction appears in a new knowledge context only.
  Cash payment, discounting and FX dependencies remain separate from fixing status.

This uses the same fixed/variable input and scenario mechanism as gas, with power
interval/quantity definitions. It is a planned settlement/pricer test, not current PV.
Status (D174): verified at the interval-price level. The fixed and marked intervals
value at 0, a +1 mark shock gives +10 with the fixed interval unchanged, an ended
interval before its due actual is unresolved, a due missing actual is refused, a
day-ahead actual known before its hour is used, an actual published after the
valuation instant never prices an interval still live then, and a later correction
appears only at a later knowledge time. Sides, cash and payment remain phases C and D.

### PWR02 — Subhourly quantities, clocks and TOU

- A 12 MW quantity over an explicitly supplied five-minute interval is 1 MWh;
  over an explicitly supplied half-hour it is 6 MWh. A quantity already expressed
  as 6 MWh is not multiplied by duration a second time.
- For a synthetic DST-following all-hours clock, 10 MW across a specified 23-hour
  day gives 230 MWh and across a 25-hour day gives 250 MWh. The repeated local hour
  remains two different UTC intervals; the missing local hour is not manufactured.
- For an explicitly fixed-offset 24-hour day, the same rate gives 240 MWh. Changing
  the user's display zone changes none of these amounts or TOU membership.
- Peak/off-peak membership comes from the selected versioned calendar/profile.
  Mixed peak/off-peak or incomplete hourly coverage cannot silently form a full block.
- Exact UTC-grid duration/DST primitives are already tested in Shared. Status
  (D174): the rate-to-energy cases above are verified by managed checks (5- and
  30-minute intervals, 23-, 24- and 25-hour days and a fixed-offset day), and
  peak/off-peak blocks come from published TOU versions, with incomplete or
  overlapping coverage refused. Complete power-curve/settlement integration
  remains planned.

### PWR03 — Price identity, components and averaging

- A synthetic composed LMP with energy 40, congestion −6 and losses 1 is 35
  currency/MWh. If the definition consumes these components, a +1 energy-input
  shock changes the composed price by +1. An independently supplied all-in LMP
  stays independent of inferred component/gas shocks.
- Negative marks, components and permitted derived prices remain signed; there
  is no implicit zero floor. Current negative power/basis/money tests exercise
  that generic price behavior; complete component-index settlement is planned.
- Two equal-duration samples 40 and 60 have arithmetic mean 50 only under an
  explicitly selected equal-sample rule. Unequal durations/weights require their
  own declared aggregation; missing intervals, duplicates, wrong locations,
  wrong DA/RT market or inconsistent components raise errors.
- Preliminary, final and corrected prices keep their status and knowledge times.
  An explicitly supported preliminary billing/reconciliation rule is distinct
  from a final-only contract. The first final-only implementation rejects
  unavailable finals; it does not silently choose preliminary or alternate data.

### PWR04 — Financial forward settlement versus market balancing

An OTC forward settles against its exact contractual index/shape/aggregation rule.
A power future follows its separate product specification and margin/final-index
rules. Direct participation in a two-settlement market can instead involve a
scheduled day-ahead amount and a real-time deviation; it must not bill the whole
quantity again in both markets. These are different economic operations.

A synthetic example, excluding other invoice items: day-ahead scheduled energy
10 MWh at 40, actual energy 12 MWh and real-time price 60 for the matching stated
periods gives `10×40 + (12−10)×60 = 520` currency. Billing `10×40 + 12×60` would
incorrectly double count the scheduled portion. This is an independent acceptance
oracle for a future explicitly configured balancing workflow, not current billing.

## Shared scenario contract and remaining qualification

The bounded implementation now qualifies live quote-input overlays, affected-node
reuse, black-box unit execution, exact position differences, nested quote stresses
and durable futures jobs in RSK01–06. The [risk engine](risk-engine.md) records
their supported API, context and worker boundaries. Parameter/rate shocks,
relative or derived-output curve shifts, distributed partitions of one run and
full settlement PV remain work. Generated-close jobs separately retain mixed
futures-and-swaps reports.

1. **Capture once.** Pin effective/knowledge context, definitions, actual/fixing
   state, numerical policy and model versions. Resolve every prompt alias to a
   dated factor once. Capture requested outputs and declared pricer dependencies,
   including potentially exercised branches, not only currently nonzero exposure.
2. **Use typed hypothetical overlays.** A scenario names the base snapshot and
   an immutable map from exact dated primitive factor identity to a typed shock.
   Nested children compose signed absolute live quote shifts on their parent;
   returning to a parent discards only its child. The finite-difference bump is
   an explicit positive symmetric absolute live price-input shift;
   rate/parameter shifts remain future scope. In every case,
   currency, quote multiplier, quantity basis and rate representation remain
   explicit. Unsupported relative/cross-unit shocks fail. Source observations
   and IDs remain unchanged, with original lineage plus separate scenario evidence.
3. **Keep observations and shocks distinct.** Ingested settlements obey their
   exchange/contract increments. A hypothetical finite-difference shift can be
   off that ingestion grid when its declared numerical policy permits it. Never
   fabricate a corrected settlement observation to carry a scenario. Validate
   hypothetical domain/precision independently; never round, clamp or default.
4. **Freeze realized facts.** Overrides affect variable inputs only. If a series
   has both a stale variable input and a consumed frozen-final input, that frozen
   use is unchanged. Direct attempts to shock a realized fixing are rejected.
   An otherwise valid variable factor with no requested affected outputs returns
   unchanged values and zero incremental evaluated work, not invented exposure.
5. **Propagate through a compiled dependency plan.** No graph database is needed.
   Index declared input → consumer relationships. Evaluate a baseline once, mark
   affected nodes, reuse unaffected baseline values, and evaluate each affected
   node once per scenario. Treat pricers as black boxes consuming the same shared
   scenario view; dependencies schedule work, while actual PV outputs determine risk.
6. **Make curve shifts unambiguous.** A single-contract input shock names one
   dated primitive. A parallel shift of a directly quoted curve expands its
   explicit selected dated inputs once. Shared inputs are deduplicated and
   conflicting shifts reject. A derived-output override is a different operation;
   the first primitive-input implementation must reject unsupported output shifts,
   not bump every upstream base/spread or silently reinterpret the request.
7. **Reuse only equivalent calculations.** Cache unit-pricer work by complete
   economic inputs, date shape, calendars, payment/settlement state, model and
   context/scenario identity. Instruments own unit valuations and books apply
   external trade quantities under the shared contract. Market nonlinearity does
   not prevent eligible unit reuse. Identical names or a common underlying are insufficient.
8. **Retain authoritative results and work counts.** Save base/up/down results,
   exact factor bindings, bump units/magnitude, finite-difference convention,
   numerical policy, eligible reuse and actual node/pricer execution counts.
   Reconcile against a simple independent full-repricing path. Measure performance;
   trade count alone does not establish efficiency.

For absolute symmetric bumps `h`, central delta is `(PVup−PVdown)/(2h)` and central
gamma is `(PVup−2PVbase+PVdown)/h²`, with explicit dimensions. Choose division and
rounding policy before implementation; insufficient precision must not become
zero risk. Mixed/cross gamma and relative bump conventions remain separate scope.

### RISK01 — One factor, several dependent consumers

Use the existing synthetic recipe `(gas + 0.1 + 0.2) × 7 + 4 + 2` with gas 3.00.
Base derived price is 29.10; +0.10 gas gives 29.80; −0.10 gives 28.40. An independent
all-in price stays 29.10. The reusable scenario engine must reproduce these existing
price oracles without editing any source observation.

For an explicitly linear 10,000-unit instrument, fixed price 29.10 and discount
factor 1: base PV 0, up PV +7,000, down PV −7,000, gas delta +70,000 in the declared
price/quantity units, central gamma 0. Eligible repeated quantities share unit
pricing. Different dates/payment state must not collide. This instrument/risk
workflow is covered by RSK01 and the shared unit-valuation checks; the curve-price
oracle alone would not prove its PV behavior.

Existing checks also cover frozen-final inputs, partially fixed OTC valuation,
signed and offset quantities, nonlinear gamma, invalid bump/precision errors,
nested/concurrent scenario isolation, current authority and measured work counts.
Remaining acceptance work includes the settlement reset in SET02, parameter and
zero-rate shocks, broader parallel-curve commands and single-run distributed
aggregation. Each new consumer must reconcile to independent repricing before
its results are represented as complete portfolio risk.
