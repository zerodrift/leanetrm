# Power purchase agreements, interval series and shaped interval curves

Status: draft design for review, 2026-10-20. Phase A (section 5, audited interval
series) is implemented and recorded as D172; its contract, 20-year measurements and
remaining limits, including the owner references deferred to phases B and C, are in
[the implementation record](implementation.md#audited-interval-series). Phases B to D
are design only. Proposed choices are recorded as P40, not as agreed financial
conventions. The implementation inventory below was checked against source revision
`4a0a6a7`, before phase A.

## 1. Outcome and scope

A desk must book a power purchase agreement (PPA), see its unrealised value for
future delivery, settle delivered periods against metered production and actual
prices, and report realised and unrealised P&L. Every interval value that a trader
or operator can amend (price shapes, volume forecasts, metered production) keeps
a complete change history.

The setup is market-neutral. Jurisdiction-specific products, clocks, holiday
calendars and price sources are later reference data, not code paths. Checks use
synthetic data on clocks the system already has.

Delivery order:

1. **Interval series:** versioned, audited values per interval on any market clock.
2. **Interval curves:** forward prices per hour or shorter, either marked directly
   or shaped from block marks.
3. **PPA booking and valuation:** a PPA instrument composed of reusable sides.
4. **PPA settlement:** metered quantities and actual prices become cash
   obligations through the [settlement design](settlement-plan.md).

Each step has its own consumer and exit gate (section 10).

## 2. Agreed decisions

These were decided by the product owner and are not open to the builder:

- Generic first. No jurisdiction tailoring in this delivery.
- Version 1 supports **linear terms only**. Collars, reference-price floors and
  caps, negative-price provisions, volume caps and indexed prices with limits
  are rejected with structured errors until an option model exists.
- The modular instrument design is preserved: book → trade → {instrument: quantity,
  ...}. The PPA is a `PpaInstrument`, a `SidedInstrument` with an open set of
  side memberships; a subclass of an existing instrument may be the starting point.
  Its sides are independent reusable definitions, so PPAs with similar side
  configurations share them.
- Terminology: a **side** is a component of an instrument. A **leg** is a row of
  a side's schedule. The schedule rows determine the side's PV.
- **Physical sides value the commodity received or delivered; financial sides
  price the contractual payment.** Their prices come from different places:

  | Side | Represents | How its price is selected |
  | --- | --- | --- |
  | Physical | Commodity received or delivered at a physical location | Location → its assigned *location valuation curve*, with explicit location adjustments. Never set directly on the side |
  | Financial | Contractual cash paid or received for that commodity | Agreed fixed price, or a floating binding to a selected publication/source, index location and pricing convention |

  The two curves can coincide but serve different purposes. A financial side
  inside a physical instrument is the consideration for that commodity. A
  separately booked swap is a different instrument with its own quantity.
- `PowerPhysicalSide` is the power physical side. It carries the physical
  details: asset, meter, delivery location, market clock, interval grid and volume.
- An instrument has **no fixed number of sides**. Several physical sides can
  share one payment side or each have their own; physical-only instruments are
  allowed. Additional sides can be added.
- **Physical value and payment stay gross.** They remain separate schedule rows
  evaluated under the same pinned scenario. Same-curve offsets come from the
  arithmetic, never from a shortcut to zero. A resolved price is reused only when
  its complete selection rules match.
- A **virtual PPA has no physical side.** No commodity changes hands between the
  parties. It is a floating side receiving the contract index and a fixed side
  paying the strike; its quantity comes from the generation asset's volume source.
- RECs and capacity are supported in version 1. Delivered RECs are held
  certificates marked to their vintage curve and stay unrealised; delivered
  capacity is realised once its period's published price exists.
- Delivered intervals still awaiting a meter reading or actual price follow one
  of two explicit valuation policies, fail or estimate; there is no default.
- A virtual PPA settles as one net payment per period through an explicit net
  payment grouping of its own sides, rounded once at the net amount.
- Interval curves use **block marks + a stored shape + calibration**. Shapes may be
  additive or multiplicative and may be trader-entered, imported or later derived
  from history; all produce the same stored series.
- Settlement builds on the collected-interval settlement arithmetic (D171) and
  the lifecycle in the [settlement design](settlement-plan.md) (P39).

## 3. Goals, non-goals and anti-goals

**Goals**

- Book physical and virtual linear PPAs, including RECs and capacity, from
  reusable sides.
- Unrealised value from interval curves and volume forecasts; realised value from
  metered quantities and actual prices; the split visible per schedule row.
- Interval series with who/when/old/new history and as-of reconstruction.
- Interval forward curves built from block marks and stored shapes, with exact
  calibration and declared dependencies, usable by risk scenarios.
- Cash settlement of PPA payment sides through the shared settlement lifecycle.

**Non-goals for this delivery**

- Physical scheduling, nominations, delivery confirmation or imbalance billing.
  A physical PPA's delivered quantity comes from the meter.
- Option-like terms listed in section 2; probabilistic volumes (P50/P90).
- REC registry, eligibility, sale and retirement tracking (vintage is in scope);
  capacity accreditation models and performance obligations.
- Invoices, taxes, fees, netting across trades or instruments, and credit/collateral
  (as in the settlement design). Net payment grouping within one PPA is in scope.
- Deriving off-peak from base and peak marks, or splitting quarters/years into
  months. Version 1 shapes require a non-overlapping partition (section 6.3).
- Jurisdiction-specific reference data and price sources.

**Anti-goals** (failure even if the feature works)

- Silently flattening or disaggregating a block mark into intervals.
- Choosing a valuation curve from a location's name, type or nearest hub.
- Netting physical value against payment, or counting a quantity twice.
- Treating a past unmetered interval as metered, or an expired forward as an actual.
- Default shapes, adjustments, escalation bases, rounding or discounting.
- Pricing, shaping or schedule logic in TypeScript.
- PPA-specific pricing code inside the instrument: it only sums signed sides.

## 4. Reuse and implementation boundaries

| Existing component | Reuse | Remaining work |
| --- | --- | --- |
| `Side`, `SidedInstrument`, `SideMembership` (`Trading/Instruments.cs`) | Side-owned schedule and PV; instrument sums signed side PVs; external trade quantity | `PpaInstrument`; open memberships with stable roles; sibling quantity references (architecture §3.4) |
| `FixedSide`, `FloatingSide`, `PaymentSchedules.cs`, `PaymentRules.cs` | Payment rules, discounting, price states `Fixed/Projected/Actual`, lineage | One row per delivery interval; quantity from a referenced source; strike escalation and steps |
| Monthly strips (D170) | Multi-period composition, exact allocations | Interval granularity and per-row quantities |
| `PowerHours`, `TimeOfUse`, `PowerIntervalQuantities`, `PowerSettlement` | UTC/DST interval compilation; exact MW/MWh and cash arithmetic | Lift the 366-day and 110,000-interval caps by bounded chunking, with benchmarks |
| `market_data.curve_physical_price_sources` (0063) | Curve → collected actual-price source | Location valuation curve assignment; non-USD validation when needed |
| `power_data.prices`, batches, `read_prices(cutoff)` (0033) | Immutable revisions and as-of reads for collected prices | Same pattern for user-maintained series, with actor and reason |
| `market_data.observations` corrections (0002/0009) | Append-only corrections, certified knowledge time | Corrections record no actor today (`PreviewDatabase.Correct`) |
| TOU products (0027), calendars, clocks | Peak/off-peak membership | Block definitions for shaping |
| Custom curve recipes (0042) | Versioned graph recipes over curves | Later producer of history-derived shapes |
| Settlement design (P39) phases S0–S5 | Occurrences, determinations, approval, payment | PPA interval-quantity occurrences |

## 5. Part 1: interval series

An **interval series** holds one value per UTC interval for a declared clock and
resolution (5, 15, 30 or 60 minutes). It has three kinds in this delivery, each
with a named consumer:

| Kind | Unit | Consumer |
| --- | --- | --- |
| Price shape | currency/MWh offsets, or dimensionless factors | Interval curve shaping (section 6) |
| Volume forecast | MW output per MW of capacity basis (dimensionless) | PPA unrealised quantities (section 7.3) |
| Metered volume | MWh per interval | PPA realised quantities and settlement |

Requirements:

- **Identity:** a series definition has kind, exact quantity/quote specification,
  clock, resolution and an exact owner reference (an asset for volumes, a curve
  for shapes). Exact-version foreign keys, no free-text owners.
- **Revisions are immutable.** Each write appends a revision that records actor,
  reason, request identity and commit-certified knowledge time. Corrections never
  overwrite. A read at a knowledge cutoff returns the values known then.
- **Change log:** per interval, the old value, new value, actor, reason and time;
  per revision, a summary of changed intervals. Available through the same
  authorized operation for UI, API and agents.
- **Coverage is explicit.** Missing intervals are absent, never zero or
  interpolated. Consumers fail on gaps they need.
- **Boundaries:** values join by exact UTC start/end, never local hour labels.
  Repeated DST hours are distinct intervals; missing hours do not exist.
- **Entry and import** (file and API) go through one validated operation:
  distinct, non-overlapping UTC boundaries on the declared grid, exact decimal
  values, unit match.
- **Authorization:** series inherit read/write scope from their owner; current
  access is rechecked on every historical read.

Left to the builder: whether to generalise the `power_data` batch pattern or the
observation correction chain, and whether revisions store full snapshots or
deltas. Both must give exact as-of reads; qualify with a 20-year hourly series
(175,320 intervals) and its quarter-hour equivalent.

## 6. Part 2: interval curves and shaping

### 6.1 Interval constituents

Curve constituents today are date-only (`market_data.contracts.delivery_from/to`).
Add constituents with exact UTC start/end compiled from an explicit market clock,
keeping operating date and hour label as metadata, as specified in
[the first hourly specification](business-scenarios.md#first-hourly-specification-to-review).
Directly supplied interval marks are the first producer (PWR01, PWR02). The
interval curve keeps its explicit actual-price source (0063) for delivered intervals.

### 6.2 Blocks

A **block** is a delivery period crossed with a published TOU membership, for
example a month's peak hours. Its interval set is compiled from the exact TOU
product version, holiday calendar and clock, including 23/25-hour days. A block
mark is an ordinary curve observation on a block constituent.

### 6.3 Shaping operator

A versioned curve operator with explicit inputs:

- block marks forming a **complete, non-overlapping partition** of the requested
  horizon (each interval belongs to exactly one marked block);
- one shape series revision on the same clock and resolution;
- the method: `additive` or `multiplicative`;
- the calibration policy, initially duration-weighted equality to the block mark.

For block *b* with mark *M*, intervals *i* with duration *dᵢ*:

- Additive offsets *oᵢ*: `Pᵢ = M + oᵢ − Σ(dⱼ·oⱼ) / Σdⱼ`
- Multiplicative factors *fᵢ*: `Pᵢ = M · fᵢ · Σdⱼ / Σ(dⱼ·fⱼ)`; a non-positive
  `Σ(dⱼ·fⱼ)` is an error.

So `Σ(dᵢ·Pᵢ) / Σdᵢ = M` exactly. Arithmetic stays rational until the selected
output policy. Overlapping or missing block marks, missing shape intervals, a
grid mismatch or an unknown method raise structured errors.

Each interval price retains its block mark observation, shape revision, method,
operator version and policy. The interval price declares its block mark as a
dependency; a block-mark shock rebuilds the shaped curve. The shape is a pinned
input and is not shocked in version 1.

Left to the builder: storage of shaped outputs versus recomputation from pinned
inputs. Later producers (a month × day-type × hour factor table, factors derived
from historical actuals through a custom-curve recipe) write the same shape
series kind and need no change to the operator.

## 7. Part 3: PPA instrument and sides

### 7.1 Reference data

- **Generation asset:** effective-dated versions with exact location, capacity
  basis (MW, the denominator for per-unit volumes), meter identity, meter stage
  (`generated` or `delivered`) and accredited capacity (MW, for capacity sides).
  Add only attributes with a consumer here.
- **Location valuation curve:** an effective-dated assignment from a location to
  the curve that values commodity there, plus explicit location adjustments
  (initially an additive price adjustment). The curve's actual-price source values
  delivered intervals. A location with no assignment in force is an error.
- **Volume source:** a reusable definition binding an asset, a forecast series,
  the asset's metered series and explicit adjustments (section 7.3).

### 7.2 Instrument and quantity unit

`PpaInstrument : SidedInstrument`. Memberships carry a signed coefficient and a
stable role (`physical`, `consideration`, `adder`, `environmental`, `capacity`,
or a further supported role). The **unit is 1 MW of contract capacity**; the trade
quantity is contract MW and scales the completed unit PV once.

Validation: every quantity reference resolves to a sibling side or a volume
source in the same instrument; references are acyclic; each quantity is consumed
once per payment purpose; units, currencies and clocks are compatible; terms
outside section 2 have no representation and are rejected.

**Identity and fungibility** follow [architecture §3.1](architecture.md#31-definition-identity-and-versions)
and [§3.4](architecture.md#34-explicit-sibling-side-references):

- A side's identity is its canonical economic terms only. Labels, booked
  quantities, parties, booking facts and lifecycle state are excluded.
- A payment side references its quantity through a **stable sibling slot or
  role**, not a particular sibling's UUID. "Strike 40, 2.5% anniversary
  escalation, monthly payment rule, quantity from the `physical` slot" is one side
  definition shared by every PPA with those terms, on any asset.
- `PowerPhysicalSide` identity includes the exact volume source version, location,
  clock and grid, quantity stage and value-date rule. PPAs on the same asset with
  the same physical terms share it.
- `PpaInstrument` identity is its ordered memberships, signed coefficients, roles,
  resolved slot bindings and payment grouping (section 8). Trades with identical
  economics share one instrument and differ only in their external MW quantity.
- Sibling context is part of calculation identity: the same side definition in a
  different instrument is resolved and valued in that instrument's context, and
  shares a unit calculation only when all resolved inputs match.
- Uniqueness is enforced atomically with typed candidate keys, as for existing
  sides. A content hash is a lookup aid, never an identity or a permission.

### 7.3 Volume

For interval *i* and one unit:

- **Forecast** (future intervals): `forecast cf × duration hours × availability ×
  (1 − curtailment) × degradation`, at the forecast's stage.
- **Metered** (delivered intervals): `meter MWh ÷ capacity basis MW`, at the
  meter's stage. Availability, curtailment and degradation are already reflected
  and are not applied again.
- **Stage conversion:** `delivered = generated × (1 − loss)`. Each consumer
  declares the stage it uses.
- **Degradation:** `(1 − rate)^t`, where *t* follows an explicit basis:
  `anniversary_step` (whole years since the base date) or `continuous_act365`.

Each row records its quantity state: `Forecast`, `Metered`, `Contractual` (for
example accredited capacity MW-days from the asset version) or `Estimated`.

**Pending actuals.** Meter readings lag delivery by days and actual prices by
about a day. Every valuation request selects one of two policies; there is no
default:

- `require_actuals_v1`: a delivered interval without an eligible metered quantity
  or actual price raises a structured error.
- `estimate_until_actual_v1`: such an interval uses its forecast quantity and/or
  projected price; the row is labelled `Estimated` and reported separately from
  realised value. It never counts as realised.

The selected policy is part of the valuation context and its receipt.

### 7.4 Sides

| Side | Price | Quantity | Rows |
| --- | --- | --- | --- |
| `PowerPhysicalSide` | Location valuation curve + adjustment; actual source when delivered | Volume source | Delivery value (non-cash) |
| `FloatingSide` (extended) | Contractual publication/source, index location, DA/RT, per-interval convention | Sibling physical side or volume source | Cash payment |
| `FixedSide` (extended) | Strike with optional escalation (`rate`, base date, basis as above) **or** dated steps; or a flat adder | Sibling physical side or volume source | Cash payment |
| Environmental attribute physical side | REC location valuation curve | Certificates = MWh at the declared stage × certificates per MWh | Certificate delivery value (non-cash) |
| Capacity attribute physical side | Capacity location valuation curve per MW-day | Accredited MW × delivery days | Capacity delivery value (non-cash) |

RECs and capacity follow the same rule as energy: the attribute received or
delivered is a physical side; if the contract prices it separately, a fixed or
floating payment side references its quantity. In a bundled PPA the strike pays
for both, so no separate REC payment side exists.

- **RECs** carry a vintage (the generation month). Delivered certificates are
  `Held`: valued at their vintage's curve contract and reported as unrealised,
  never realised. RECs keep trading after generation, so no actual price exists;
  a later feature records sale or retirement.
- **Capacity** rows are per capacity period. A delivered period becomes realised
  once that period's published price exists as an actual observation; until then
  it is projected from the capacity curve.

Every schedule row (leg) carries: side and membership identity, UTC interval,
per-unit quantity with state and lineage, price with state (`Fixed`, `Projected`,
`Actual`) and lineage, amount, value or payment date from an explicit rule,
discount factor and PV. Physical sides also require an explicit value-date rule;
none is defaulted. Side PV is the sum of its rows.

### 7.5 Compositions

| Form | Memberships |
| --- | --- |
| Physical, fixed price | `PowerPhysicalSide` (+receive) + strike `FixedSide` (−pay), optional adder `FixedSide` |
| Physical, index-priced | `PowerPhysicalSide` (+) + `FloatingSide` paying the contract index (−) |
| Virtual | `FloatingSide` receiving the contract index (+) + strike `FixedSide` (−), quantity from the volume source |
| With RECs / capacity | Add attribute physical sides, plus payment sides only where separately priced |

Two PPAs on the same asset share the volume source and physical side even when
they settle at different indices. Different strikes produce different fixed sides
and share the rest.

### 7.6 Valuation, risk and P&L

`Book.PV` → trade → `PpaInstrument.PV` → signed side PVs, exactly as today.
Declared dependencies: block marks behind interval curves, location valuation
curves, contract index curves, attribute curves and discount curves. Volume
series, shapes and actuals are pinned inputs.

Rows classify the value:

| Category | Rows |
| --- | --- |
| Realised | Delivered; quantity `Metered` or `Contractual`; every price `Fixed` or `Actual` |
| Estimated | Delivered intervals valued under `estimate_until_actual_v1` |
| Held | Delivered REC certificates, marked to their vintage curve (unrealised) |
| Unrealised | All other rows |

Realised P&L is cumulative from inception: a delivered row keeps its amount in
history for the life of the deal. Cash sides report realised cash; physical sides
report realised delivery value, kept separate.

PPA valuation adopts the partition in [settlement design §5.2](settlement-plan.md#52-keep-cash-present-after-fixing-and-due-dates).
Each cash row is counted exactly once as remaining projected, delivered but not
yet in an accepted obligation, accepted outstanding, or paid. Paid cash leaves
outstanding PV but stays in realised history. Unpaid cash at or after its due
date shows its known balance with an unavailable-PV reason until an explicit
due/overdue policy is selected. For PPA sides this partition replaces the current
`side_payment_due` refusal; existing swap behaviour is unchanged. Before
settlement exists (phase C), delivered rows sit in the second bucket.

Delivered prices in phase C come from each curve's collected actual-price source
(0063) under D171's latest-collected-revision rule, for physical and floating
sides alike. A virtual PPA's floating side reads hourly actuals through that
association, not through per-hour fixing observations. Rows show the
`collected` publication status; settlement determinations later require the
publication eligibility of settlement phase S1. The 0063 validation is
generalised beyond its current currency and clock limits.

The location adjustment applies to projected and actual prices alike. Each
delivery interval uses the location assignment effective for that interval, as
known at the knowledge cutoff, so re-pointing a location does not restate
delivered history. An assignment version carries one adjustment value; basis that
varies by month or TOU block belongs in the curve recipe (hub + basis).

Historical-view mode: shaped curves and PPA valuation refuse it explicitly in
version 1, as D171 does, because TOU products are not yet historically published.
Interval series reads are as-of from phase A. Day-over-day comparison uses
persisted close results. Historical TOU publication is a later prerequisite.

Performance: a 20-year hourly PPA with five sides has about 876,000 rows per unit
instrument. Unit-instrument reuse values each instrument once per scenario.
Single-instrument preview and valuation are interactive; book risk runs as a
durable job, as generated closes do. Re-evaluating only the rows behind a bumped
block is an optimisation to add only if the benchmark requires it. Qualify with
the benchmark rules in AGENTS.md before claiming capacity.

## 8. Part 4: settlement

- Prerequisite: settlement design phases S0–S3 for power, delivered under that
  design rather than this one. Phase D adds PPA occurrences and determinations
  on that machinery and on S4 payments.
- Cash obligations come only from payment sides. Physical sides produce delivery
  records with realised value, never cash.
- **Net payment grouping.** A `PpaInstrument` may declare an explicit grouping
  of its own payment sides that share a payment period, currency and payment
  date, such as the floating, strike and adder sides of a virtual PPA. The group
  settles as one net obligation per period, rounded once at the exact net amount
  under the selected cash-rounding policy. The grouping is part of instrument
  identity, not side identity, so sides stay reusable. Side rows remain gross and
  inspectable. Without a declared grouping, each side settles gross. Netting across
  trades or instruments is not supported.
- A determination pins the metered series revision and the actual price
  observations it consumed. A later meter correction creates an explicit
  resettlement under the settlement design's correction policy.
- Occurrences are per payment period and side; their components retain every
  interval row.

## 9. Synthetic acceptance oracles

All values are synthetic; discount factor 1 unless stated.

1. **Additive shape:** mark 50; four one-hour intervals; offsets −10, 0, 10, 20 →
   prices 35, 45, 55, 65; average 50.
2. **Multiplicative shape:** mark 50; factors 0.8, 1.0, 1.2, 1.4 → prices 400/11,
   500/11, 600/11, 700/11; average 50.
3. **Zero-factor block:** all factors 0 → structured error.
4. **DST block:** a 25-hour day with equal offsets prices every interval at the mark;
   the repeated hour is two rows.
5. **Physical fixed price:** receive 100 MWh, location valuation 42, strike 40 →
   physical +4,200, payment −4,000, combined +200; payment obligation 4,000.
6. **Same curve:** receive 100 MWh valued at 32, pay index 32 → +3,200 and −3,200,
   both rows kept; at index + 1 → −3,300 payment, combined −100.
7. **Virtual PPA:** 100 MWh, hub 30, strike 40 → buyer value −1,000; no physical rows.
8. **Metered per-unit volume:** meter 50 MWh, capacity basis 200 MW, trade 80 MW →
   20 MWh.
9. **Forecast volume:** cf 0.25 over one hour, availability 0.98, curtailment 0,
   no degradation, delivered stage with loss 0.02, trade 80 MW → 80 × 0.25 × 0.98
   × 0.98 = 19.208 MWh.
10. **Realised/unrealised split:** interval 1 metered 10 MWh at actual 40; interval
    2 forecast 10 MWh at projected 60; strike 50 → realised −100, unrealised +100.
11. **Escalation:** strike 40, 2.5% `anniversary_step`, third contract year →
    42.025.
12. **RECs:** 100 MWh generated, 1 certificate per MWh, vintage curve 5 → +500
    `Held` (unrealised); no cash unless a REC payment side exists.
13. **Capacity:** 50 MW accredited, 30 days, published period price 4 per MW-day
    → 6,000 realised; without the published price, projected from the curve.
14. **Net payment rounding:** floating exact 1,000.015 and strike exact −1,500.005
    in one group, 2 decimals, ties to even → one obligation of −499.99. Rounding
    each side first would give 1,000.02 − 1,500.00 = −499.98.
15. **Pending actuals:** a delivered interval with forecast 10 MWh and no meter
    reading fails under `require_actuals_v1`; under `estimate_until_actual_v1` it
    values 10 MWh as `Estimated`, outside realised.
16. **Fungibility:** two trades on different assets with the same strike terms
    reference one strike side definition; two trades with identical economics at
    80 MW and 20 MW share one instrument, and the unit valuation runs once per
    scenario.
17. **Failures:** a location without a valuation curve, an unpartitioned block, an
    option-like term and a quantity consumed twice each raise their structured
    error.

## 10. Phased delivery and exit gates

| Phase | Deliverable | Exit gate |
| --- | --- | --- |
| A: interval series | Series definitions, immutable revisions with actor/reason, as-of reads, change log, import, UI view | DST and correction cases; as-of equality across revisions; authorization and replay checks; 20-year benchmark |
| B: interval curves | Interval constituents, direct marks, blocks, shaping operator, curve UI | Oracles 1–4; PWR01/PWR02; block-mark shocks rebuild exactly once per scenario |
| C: PPA booking and valuation | Assets, location valuation curves, volume sources, extended sides, `PpaInstrument`, booking API and editor, blotter and risk | Oracles 5–13 and 15–17; identity reuse across trades; unit PV reuse; benchmark |
| D: PPA settlement | PPA occurrences, determinations and net payment grouping once settlement phases S0–S3 are complete, with S4 payments | Oracle 14; meter corrections resettle explicitly; no cash from physical sides; exactly one recorded effect under retries |

Each phase includes schema and RLS, managed contracts, API and UI consumers,
synthetic tests written first, and independent adversarial review of numerical,
temporal, authorization and scenario-reuse behaviour. Land no schema without its
phase's consumer.

## 11. Design calls

Recorded as P40 and open to review; none is an agreed financial convention until
accepted:

- Unit is 1 MW of contract capacity; metered per-unit volume divides by the
  asset's capacity basis.
- RECs and capacity are attribute physical sides, with payment sides only when
  separately priced.
- Availability, curtailment and degradation apply to forecasts only.
- Version 1 shapes require a non-overlapping block partition; shapes are not
  shocked.
- Physical sides require an explicit value-date rule.
- Delivered prices in phase C come from collected actual-price sources; finality
  eligibility applies at settlement.
- Location assignments resolve per delivery interval; one adjustment per
  assignment version.
- Shaped curves and PPA valuation refuse historical-view mode in version 1.
- Single-instrument valuation is interactive; book risk runs as a durable job.
- Settlement waits for settlement design phases S0–S3.

## 12. Related contracts

- [Architecture](architecture.md) §3.2–§3.5 and §5; open decision O14 in the
  [decision record](decisions.md).
- [Power cases PWR01–PWR04](business-scenarios.md#power-curves-and-settlement-acceptance-cases).
- [Collected interval delivery value](power-preview.md#collected-interval-delivery-value) (D171).
- [Settlement design](settlement-plan.md) (P39).
- [Time, delivery calendars and TOU](time-and-schedules.md).
