# Solar, generation tolling, batteries and RECs

Status: bounded local implementation and remaining delivery proposal,
2026-11-15. D412 implements operating-facility versions and supplied-plan
thermal, battery and solar previews, saved operating trades, common-scenario price
hedging and durable manual REC inventory/forwards. The broader product sequence
below remains future work. The power-plant scope here is **generation tolling**.

The historical source-inspection baseline in section 2 is commit `4bf8e146`.
The current slice is described separately below and in the
[operating-facility capability contract](operating-facilities.md) and
[saved trading and inventory contract](structured-power-trading.md). Physical-service and
price-publication qualification remain separate prerequisites where needed.

## Current implemented slice

Physical setup saves immutable thermal, battery and solar operating extensions of
existing reference locations, with exact foreign keys, effective windows,
expected-head checks, request replay and business-fact no-ops. Certified reads
preserve the knowledge cutoff and can select a known future-effective version.
Only consumed physical parameters are saved; guarantees, entitlement, state,
dispatch, prices and fees remain explicit preview inputs. Unsupported planned
fields appear disabled as **Coming soon**.

D417 implements explicit operating assembly through concrete Side composition,
with separate independent dates and prices, repeated quantity sources and native
saved admission. It preserves the qualified D412 request/identity contract below.
Fixed/floating pricing is shared economics; the concrete Side owns its
asset/delivery/payment behavior and retained schedule. The
[instrument contract](instrument-contracts.md) applies to previews, booked
programs and ordinary portfolio valuation alike. Qualification is bounded to
power, REC and their required common adapters.

The managed supplied-plan preview validates thermal commitment/fuel and battery
state paths, and resolves solar entitlement and delivery/payment quantities.
Ordinary independent delivery/payment Sides own the schedules and price typed outputs
from the same plan. Results expose exact gross rows and signed contributions
under the explicit right-holder, remaining-operating-value measure. Current
priced previews are undiscounted; fixed-profile solar exposes quantity residuals
until balancing-price sides are qualified.

The continuation saves future complete exclusive operating-right trades through
their retained Side programs, exposes ordinary Blotter/portfolio/Side reads and
compares explicit financial hedges under one immutable scenario set. Standalone
REC forwards retain independent physical/consideration schedules. The pure REC
range evaluator has a durable manual evidence consumer for exact lot ownership,
allocation/release, outgoing transfer and retirement, with certified corrections
and replay. These additions do not optimize, execute dispatch, submit registry
actions or establish a cash lifecycle. The sections below retain the wider
commercial design and future acceptance gates; the linked contracts delimit the
implemented slice.

## 1. Recommended model

Extend the existing Trade → Instrument → independent Side architecture. Keep a
facility's physical specification, a party's contractual rights, operating plans,
measured performance and financial hedges distinct. Solar already fits much of
the PPA model. Tolling and storage add constrained choices over time; RECs add
inventory and delivery evidence beyond the current modeled certificate holdings.

| Product | Trade economics | Physical operation needed | Typical hedges to represent separately |
| --- | --- | --- | --- |
| Solar physical PPA | Buy/sell generated energy, contractual payment and explicitly included environmental attributes | Forecast/meter generation, losses, curtailment and delivery | Shaped power sales, hub/node basis, firming purchases and REC sales |
| Solar virtual PPA | Fixed-for-floating cash on a declared volume; optional REC entitlement | Generation supplies the cash basis; no electricity delivery under this contract | Power/index/basis hedges; volume remains uncertain |
| Solar output-right toll | Right to specified available generation, against explicit access/availability and any variable payments | Output entitlement, availability, curtailment and permitted operating/marketing decisions | Shaped power sales, location basis, volume firming and explicit REC sales |
| Generation toll | Right to dispatch specified capacity, receive output and supply/pay for fuel, against fixed and variable charges | Commitment, dispatch, fuel use, outages and operating constraints | Sell power, buy fuel, explicit gas/power location basis; emissions where contracted |
| Battery toll or operating right | Right to charge/discharge a specified resource, against fees or an explicitly supported revenue allocation | State of charge, power/energy limits, losses, degradation and shared interconnection | Time-shaped power purchases/sales and location basis; separately qualified capacity or ancillary services |
| REC spot/forward | Delivery of an eligible certificate class/vintage against a payment | Issuance, ownership, allocation, transfer and retirement evidence | REC purchases/sales matching eligibility and vintage |

These are proposed compositions and workflows, not four new price-curve types.
A technology label selects no payoff, market dependency or operating policy.

The [industry contract and workflow conventions](power-contract-conventions.md)
distinguish physical/financial and produced/shaped PPAs, thermal conversion tolls,
storage service rights and financial reference-payoff contracts. They add the
agreement/performance terms, operational record roles, separate product rights
and settlement/correction gates needed by the delivery sequence below. These
are proposed consumers, not an extension of current executable capability.

## 2. Historical source-inspection baseline

This table records the inspected `4bf8e146` baseline before the current slice.
It remains a historical comparison point, not the current capability inventory.

| Foundation | Evidence in this baseline | Boundary relevant to this proposal |
| --- | --- | --- |
| Common unit instruments and trade bindings | [Instrument contract](instrument-contracts.md), [Trade](../src/OpenEtrm.MarketData/Trading/Trades.cs) and [Instrument](../src/OpenEtrm.MarketData/Trading/Instruments.cs) | Product-specific persistence/admission still needs implementation; common interfaces do not enable arbitrary trades |
| Physical/virtual PPA, basis cash and capacity compositions | [PPA capability record](ppa-phase-c.md), [catalog](../src/OpenEtrm.Api/PpaUiCatalog.cs), [validator](../src/OpenEtrm.MarketData/Trading/PpaInstrument.cs) | Five admitted compositions; energy, capacity and arbitrary fee combinations cannot simply be mixed into a toll |
| Generation forecast and meter quantities | [Asset volumes](../src/OpenEtrm.MarketData/Power/AssetVolumes.cs), [quantity preparation](../src/OpenEtrm.MarketData/Trading/PpaQuantityPreparation.cs) | Nonnegative generation, explicit stages/adjustments/deadlines and capacity normalization; no battery state or thermal dispatch |
| Certificate and capacity valuation | [PPA sides](../src/OpenEtrm.MarketData/Trading/PpaSides.cs), [PPA design](ppa-design.md#74-sides) | Certificate conversion, vintage marks and modeled holdings exist; registry ownership, standalone REC booking, transfer and retirement do not |
| Contractual heat-rate swap | [Factor swaps](factor-swaps.md), [mixed consumers](demo-instruments.md) | Monthly quantities and period-average payments; no interval price × dispatch payoff, dispatch right, fuel nomination or startup state |
| Native interval prices, schedules and risk | [Interval data](interval-data.md), [connected calculation](connected-calculation.md), [retained schedules](booked-side-programs.md) | Reuse exact grids/scenarios and stored instructions; the general direct booked-program cutover remains incomplete |
| Physical and cash lifecycle boundaries | [Physical booking plan](physical-energy-booking-plan.md), [PPA settlement preview](settlement-preview.md), [swap settlement](swap-settlement.md) | Financial swap settlement and bounded PPA previews do not establish tolling, storage, REC or general physical settlement |

There was no generation-tolling or battery valuation consumer in that baseline.
The current generation source cannot represent charging by entering a negative
generation forecast: that bypasses both its range contract and storage physics.
The current capacity agreement values accredited MW-day entitlement; it supplies
neither dispatch control nor a general toll capacity-payment rule.

## 3. Contract, facility and operation

The proposed relationships are:

```text
Trade ── instrument binding + external quantity ── Instrument
                                                    │
                                      explicit independent side memberships
                                                    │
                           exact operating-right and facility-version references
                                                    │
                 declared operating plan + initial state + constraints
                                                    │
                    gross quantities, payments and valuation evidence

Forecasts / meters / operator confirmations ── retained input evidence
Separate hedge trades ── same market scenarios ── residual exposure report
```

### Every side owns its whole-term schedule

Solar, thermal tolls and battery tolls remain sided instruments under D253/D258.
Each independent Side owns its parameters, `GenerateSchedule()` and `PV(context)`.
The instrument retains memberships and combines their signed contributions; the
trade applies external quantity once. A toll is not a Side containing other Sides.

At booking, each Side generates and retains its complete structural schedule:
delivery/availability windows, quantity-source bindings, observation rules,
payment occurrences and calculation instructions. Physical output might have
15-minute rows, fuel a separately declared gas-day mapping, fixed fees monthly
rows and startup fees declared candidate-event rows whose quantities resolve
from the common operating plan. Sides keep those independent schedules and
explicit occurrence links; a common payment date does not merge obligations.

Evaluation loads those retained instructions. Forecasts, meters, declared plans,
state and scenario-dependent dispatch supply the dynamic values through typed
bindings. Each Side values its own contribution roots; detail, settlement and
risk inspect the same rows. Changing a market scenario changes evaluated values,
not the retained contractual schedule. The saved supplied-plan profile retains
and executes those programs end to end; broader operating choices and settlement
remain separate consumers.

Where operating choices couple the sides, one declared managed calculation
produces the consistent quantity/state outputs for the complete right and
horizon. Output energy, fuel use, starts, charge/discharge and fee bases refer
to those same outputs through exact typed ports. A fuel Side cannot select a
different dispatch from its power Side; battery charging and discharging cannot
each optimize against an independent inventory. The shared operation consumes
explicit inputs without calling the instrument's total PV recursively. Its
state/feasibility rows are intermediate evidence, not additional cash or PV.

For the proposed deterministic slices, `Side.PV` evaluates those quantities with
its own contractual prices, dates and discount inputs. Future stochastic or
exercise-controlled valuation needs an explicit allocation of the jointly
valued right to side contributions under the existing option boundary. Do not
add a full toll option value to unconditional fuel/output side values.

### A toll family can cover solar and storage

Use **toll** as a commercial composition: pay for a specified resource right,
with explicit output entitlement and operating responsibilities. Reuse ordinary
payment and delivery Side behavior where it fits; resource-specific production
and state rules remain versioned managed implementations. A shared composition
does not require a universal resource algorithm. Concrete Side subclasses own
the supported resource-specific behavior and reuse common pricing/schedule
mechanisms; instrument subclasses must not encode every combination of Sides.
Keep industry product names in entry: an ordinary solar agreement is offered as
a PPA; use a solar output-right/toll classification only where its actual rights
support it. Financial reference payoffs and physical operating control remain
independent choices.

| Proposed form | Resource/input basis | Side-owned schedules, where the contract creates those obligations |
| --- | --- | --- |
| Thermal conversion toll | Supplied fuel and a constrained generating right | Fuel provision/use, output entitlement, fixed availability payment and variable/start payments |
| Solar output-right toll | Available generation from specified assets and an explicit entitlement | Generated/delivered output, fixed resource-access or availability payment, any variable payment and optional REC delivery/payment |
| Battery storage toll | Supplied charging energy and a constrained storage right | Charge receipts/custody, discharge deliveries, fixed access payment and explicit throughput/degradation payments |
| Solar plus storage toll | Solar generation and one shared storage/interconnection plan | The applicable solar and battery obligations, linked to one feasible plan and explicit environmental entitlement |

A solar toll can pay a fixed amount for an agreed period's available output and
specified curtailment/marketing rights. Available sunlight and output remain
uncertain; access to solar capacity does not guarantee arbitrary hourly MW.
There is no fabricated fuel-supply obligation. The contract states who bears
weather, outage, curtailment and replacement-energy risk. An as-generated PPA
paying per MWh remains another valid composition and need not acquire operating
control merely because the same facility could support a toll arrangement.

For a battery toll, stored energy may remain owned by the toller throughout.
Receipt into custody does not itself create a sale to the operator. Record only
the contract's actual title, custody, delivery and payment contributions; market
purchases/sales held in separate trades retain their own obligations. The right,
operating movements and external energy trades must reconcile without counting
the same commodity value twice.

- **Facility specification:** reuse exact location, clock, unit and asset
  references. Add typed thermal or battery operating specifications only when a
  calculation consumes them. A facility is not itself a legal trade.
- **Contractual right:** instrument terms state eligible resource, term,
  entitlement, control/dispatch holder, economic perspective, fuel/charging
  responsibility, delivery points, fees and all consumed rules. Ownership of a
  plant does not imply a particular toll or market sale.
- **Operating inputs:** forecasts, availability, dispatch plans, initial commitment
  or state of charge and actual measurements have effective/knowledge versions.
  Updating a forecast does not amend legal terms. Exercising a right records a
  binding-owned occurrence rather than mutating a reusable instrument.
- **Hedges:** retain ordinary trade bindings. An explicit analysis link identifies
  the physical exposure, hedge scope and purpose; it neither changes their pricing
  dependencies nor establishes hedge-accounting qualification.

For a third-party toll/PPA/REC agreement, the existing trade owns its legal
parties and lifecycle. For an owned facility, first specify an explicitly typed
internal operating entitlement and its reporting scope. The proposed projection
still uses common instrument valuation, but must not invent an external
counterparty or book a second sale merely to place the asset in a book. Durable
internal entitlement admission is an open implementation decision; existing
external-trade booking is not evidence that it is supported.

### Unit valuation and shared physical limits

Linear per-MW normalization is suitable only where the defined economics scale
that way. Startup costs, minimum loads and storage limits make arbitrary scaling
of an optimized facility unsafe.

Use one **complete specified operating right** as the unit for a constrained
toll or battery instrument. The unit definition contains its MW/MWh limits and
fixed/start costs; the trade holds an external quantity of one. Multiple units
require independently deliverable identical rights. A fractional entitlement
requires its own explicit allocation/control terms, not an assumed fractional
dispatch of the whole plant. This retains mandatory unit-level valuation without
passing booked size into the pricer.

Two rights using one physical resource cannot each independently spend its full
capacity, startup budget or state of charge. Initially admit one exclusive right
per resource/window, with serialized overlap checks. Shared rights and solar plus
storage require one joint feasible operating plan and an explicit allocation of
its value/costs to the rights. Until qualified, refuse that composition. Exact
resource, right, state, coverage and allocation versions enter calculation reuse;
same nameplate MW or same Side ID is insufficient.

## 4. Solar: extend the PPA consumer

Start with current physical and virtual compositions using explicitly selected
asset forecast/meter sources. Retain generated versus delivered energy, capacity
basis, loss stage, availability, degradation, curtailment and meter deadlines.
Do not apply an adjustment twice when already reflected in a forecast or meter.
The supplied-plan output-right preview consumes an explicit exact entitlement
fraction. Durable percentage-of-output PPA booking remains separate work; do not
reinterpret an external contract-MW quantity as a percentage.

For a virtual PPA viewed by a buyer receiving floating and paying strike, the
simple undiscounted cash basis is `sum(E_i × (Index_i − Strike_i))`. For a
physical purchase, receipt of energy at its valuation location and the payment
are independent contributions. Specify the contractual index separately from
the physical valuation point, and specify REC entitlement even when its price
is bundled into the energy strike. No second REC payment is then implied.

Further clauses need separate versions and behavior checks: deemed generation,
economic versus instructed curtailment, negative-price suspension, floors/caps,
minimum takes, volume firming and make-up energy. Current linear adjustments do
not implement these contingent rights. A fixed-price sale of a firm hourly block
and a pay-as-produced PPA have different quantity risk.

The asset-and-hedge report should expose generation-weighted capture price,
shaped and block hedge quantities, hub/node basis and residual interval MWh.
When generation is zero, capture price is unavailable, not zero. Price risk on a
fixed generation forecast is a conditional result; solar volume/weather and
price-volume dependence require explicitly selected stresses or later models.

## 5. Generation tolling

A physical toll should compose explicit fuel delivery/use, energy entitlement,
capacity/availability payment and variable operating payments. Financial payments
link to the exact operating quantities or occurrences that cause them. A cash
toll with an agreed dispatch/payoff formula is a distinct contract. An ordinary
heat-rate swap remains a useful hedge of a fixed generation plan; it does not
grant dispatch rights or acquire toll optionality.

The first thermal specification needs net/gross output basis, fuel measurement
and heating-value basis, heat-rate representation, no-load and startup fuel,
variable cost, output limits, ramps, minimum up/down times, start limits,
availability/outages, initial commitment and elapsed on/off time. Each term is
explicit, including zero or an admitted no-constraint choice. Also retain dispatch
notice rules, fuel/power operating-clock mapping, delivery obligations, capacity
fee basis, performance guarantees/penalties and payment schedules. Unsupported
guarantees or penalties refuse rather than disappearing from value.

For an explicitly simplified net-output fuel model:

```text
Energy_i       = NetOutputMW_i × ElapsedHours_i
Fuel_i         = Energy_i × IncrementalHeatRate_i
                 + OnlineHours_i × NoLoadFuelRate_i
                 + Starts_i × StartupFuel_i
OperatingValue = sum(Energy_i × PowerPrice_i − Fuel_i × FuelPrice_i
                     − Energy_i × VariableCost_i − Starts_i × StartCharge_i)
                 − CapacityFees
```

Heat rate is fuel energy per electrical energy. If the selected total heat-rate
model already includes no-load fuel, use that model's explicit decomposition;
never add it twice. Contract-guaranteed fuel owed and actual fuel consumed can
differ and require distinct quantities. Value carbon/other emissions only where
the contract/model explicitly consumes a typed quantity and price.

`OperatingValue` is a marked economic-margin illustration. Actual market sales,
fuel purchases and toll payments remain distinct booked cash/physical obligations;
fuel opportunity value cannot be subtracted again as an additional cash purchase.
Dispatch choices must respect the entire horizon, not independently select every
positive hourly spread. Select an explicit end-horizon commitment/continuation
policy: starts near the boundary must retain minimum-run, shutdown and remaining
fuel/cost obligations beyond that boundary, or be disallowed by the admitted
terminal constraint. A truncated horizon cannot make those obligations free.
Fuel nomination, transport and imbalance are separate rights/workflows, even if
physical-gas work supplies reusable inputs later.

Start with supplied feasible commitment/dispatch and deterministic margin. A
subsequent intrinsic optimizer re-dispatches under each shared market scenario.
Neither this intrinsic value nor `max(today's spread, 0)` establishes the full
pre-exercise option value of a toll. Stochastic valuation would additionally need
approved power/fuel dynamics, volatility/dependence, information timing and
exercise rules. Fixed-plan and redispatched risk must be separately named.

## 6. Batteries and battery tolls

Distinguish a battery service/toll from a pre-agreed charge/discharge energy
schedule and from a financial spread contract. Retain who controls dispatch, pays
for charging, owns stored energy, receives revenues and bears losses/degradation.
A fixed service fee, revenue share and minimum-revenue guarantee have different
payoffs; only explicitly implemented forms may be admitted.

Required inputs include charge/discharge MW limits, usable MWh limits, initial
stored MWh and timestamp, charging/discharging efficiency with AC/DC boundary,
self-discharge/auxiliary load, availability, cycle/throughput constraints,
degradation cost/capacity policy, grid connection and charging-source permission.
Specify terminal state (fixed target, constraint or declared residual value),
dispatch horizon and settlement rules. The first executable version can require
explicit zero self-discharge and auxiliary load and fixed usable capacity;
nonzero losses or changing capacity remain refused until their equations exist.

With grid-side charge/discharge energies `C_i,D_i ≥ 0`, the simplified transition
is `StoredMWh_(i+1) = StoredMWh_i + eta_charge × C_i − D_i / eta_discharge`.
Power bounds integrate the actual interval duration. Check state bounds after
every transition, retain state across days and contract segments, and prohibit
simultaneous charge/discharge under the proposed first contract. Efficiency
alone does not prevent simultaneous cycling at negative prices. Round-trip
efficiency alone cannot select its two component efficiencies implicitly.

Energy-arbitrage contribution is `sum(D_i × SellPrice_i − C_i × BuyPrice_i)`
less explicitly defined fees/cycling costs, plus the selected terminal value.
For remaining value, opening stored energy is a physical state whose future
sale/use contributes value; do not subtract a scenario-valued opening mark and
erase its price exposure, or add a separate inventory mark on top of those same
future proceeds. Operating P&L is a different measure: reconcile remaining value
and realized cash to an explicitly retained opening value/cost basis. That basis
does not re-mark inside the same commodity scenario. A same-initial-state
opportunity-cost comparison is also a separately named diagnostic, not total
remaining value. Capacity, ancillary-service and energy revenues require
compatible joint reservations and performance rules; they cannot each claim the
resource's full headroom or stored energy.

Solar plus storage also needs one interconnection balance: generation to grid,
generation to battery, grid charging, discharge, losses and curtailment. Preserve
charging-origin evidence where environmental eligibility consumes it. Discharging
stored renewable energy does not itself create a second generation REC.

## 7. RECs: product identity, inventory and delivery

The initial proposal concerns U.S. RECs; other certificate systems need their
own admitted contracts. Treat a REC as a typed certificate quantity. The selected
eligibility/version defines registry/tracking system, technology/resource criteria, geography,
generation vintage/window, program qualification, permitted substitution and
delivery deadline. Project commissioning vintage and generation vintage are
distinct. Neither price nor a shared MWh label establishes fungibility.

Extend the current generation-linked certificate consumer in two separate steps:

1. A standalone REC forward has delivery specification, fixed/indexed payment,
   explicit quantity and dates. It can project economic exposure before the
   actual certificate lots exist. A fixed forward viewed by its buyer has a
   simple comparable-mark value of `Q × (eligible delivery mark − strike)` under
   its explicit discount/payment policy. Cash settlement needs its own qualified
   publication rule; a physical REC forward must not silently become cash-settled.
2. Issuance/receipt creates inventory only with retained evidence. Allocation
   reserves specific eligible lots/ranges; confirmed transfer reduces the
   transferor's inventory and fulfills the linked delivery once. Retirement
   consumes inventory with purpose/beneficiary evidence and is distinct from a
   sale or transfer. Registry rejection, cancellation and corrections require
   explicit transitions rather than overwritten history.

Use exact registry/certificate-range identity and serialized availability checks
to prevent duplicate issuance, overlapping allocation, double transfer and
sale-after-retirement. Forecast entitlement, modeled PPA holding, issued owned
inventory, committed delivery and retired certificates are separate states.
Reconcile forecast/model quantities to issuance through explicit lineage; never
add both as independent owned inventory. Fractional entitlements and registry
whole-certificate rounding need an explicit carry/allocation policy.

Retain the existing `none_recorded_v1` PPA holding interpretation for its current
contract. Adopting a disposition-aware contract is an explicit versioned change;
do not retrospectively treat old modeled holdings as registry-confirmed assets.
A confirmed transfer changes ownership and fulfills its linked physical delivery;
retirement changes inventory for its own distinct purpose. A fixed cash
determination replaces only that payment's projection, not undelivered physical
obligations or inventory still owned. Those states may coexist until delivery.
For a synthetic undiscounted example, an owned lot marked at 500 and a matching
seller forward with delivery contribution −500 and projected receipt +400 give
net economic value 400. Determining cash replaces that +400 projection with the
determined receipt; it leaves the −500 delivery obligation and +500 owned lot.
On confirmed delivery, consume the lot and physical obligation together, leaving
the outstanding cash under its own state.
Cash settlement extinguishes a delivery obligation only where the selected
contract explicitly provides it. Gross inventory, delivery and cash components
must reconcile without duplicated value.

## 8. Valuation, risk and lifecycle contracts

The managed backend owns dispatch validation, state transitions, quantity links,
prices, cash and risk. The browser and agents use the same authorized operations.
All market changes are immutable shared scenarios. Pricers declare complete
power, fuel, basis, REC and other consumed dependencies; direct all-in marks
acquire no inferred fuel or upstream exposure.

Every result declares one valuation mode:

- **Supplied operating plan:** hold the admitted feasible dispatch fixed for
  price shocks. This values that plan and its hedge, not unexercised flexibility.
- **Deterministic intrinsic:** optimize the admitted forward scenario subject to
  constraints, horizon, terminal policy and information assumptions. Reoptimize
  in base/up/down; retain decisions and nonsmooth scenario P&L.
- **Stochastic option value:** deferred until the selected probabilistic model,
  calibration and information/exercise policy are independently qualified.

An intrinsic optimizer using future interval prices is a stated foresight
assumption. Historical replay cannot give a past operator later-known prices,
outages, meters or dispatch choices. Already committed dispatch/purchase decisions
and observed physical state quantities stay fixed in scenarios; only remaining
rights can be optimized. Their unfixed prices and remaining contractual values
still reprice under their declared dependencies: freezing a decision does not
freeze its market value. An illustrative two-settlement market rule is
`Q_DA × P_DA + (Q_actual − Q_DA) × P_RT`, but only a selected, qualified market
contract may apply it. A PPA's contractual payment index remains independent.

Pin business/effective time and certified knowledge through every relationship,
source and calculation. Retain exact terms, state/plan, price inputs, model and
numerical versions, horizon, solver settings/tie-breaks and terminal treatment.
No solver dependency is proposed yet. A new dependency needs a named executable
case, public license review and deterministic small-case qualification. Missing
inputs, infeasibility, unsupported clauses and unqualified solver outcomes are
structured errors, never zero value or an automatic simpler model.

Reports should show gross physical/contractual contributions, asset/right value,
hedge value and residual exposure under a common measure, currency and context.
Do not sum today's futures execution P&L and PPA value-excluding-overdue as though
both were complete remaining PV. Price scenarios alone do not measure all
weather, outage, volume or operational risk. Hedge ratios come from shocked
values for the selected operating policy, not from dependency membership.

New writes require both command replay and business-fact idempotency (D238).
Equal effective facts return their retained identity even under a new request;
different values append revisions with new certified knowledge. Concurrent state,
right and inventory allocations require expected versions and atomic checks.
Current authority applies at historical reads, replay and publication. Proposed
operating actions stop at retained plans/confirmations; dispatch to an external
operator, fuel nomination and registry transfers need separately qualified
external-effect protocols.

For entry, extend the existing New Trade flow with capability-backed solar,
generation-toll, battery-toll and standalone-REC choices only as each consumer
becomes executable. Keep Trade Terms, explicit unit/right size and independent
side terms. A linked Operations view should show the referenced facility/right,
plan, measured state, feasible limits and exceptions. Price/Risk previews retain
the selected context and show gross contributions before booking; a hedge
comparison selects existing trades without creating or resizing a hedge.
Unsupported forms remain unavailable with a backend explanation. The current
toll/storage profile has separate preview and durable book/reopen flows.
Amendments, evolving operational state and broader settlement remain later work.

## 9. Delivery sequence and acceptance examples

Keep each step independently reviewable with a meaningful failing behavior check
before implementation. Reuse existing consumers before adding schema or controls.

| Step | Concrete deliverable | Gate |
| --- | --- | --- |
| 1. Solar and hedge analysis | One existing generation-linked PPA plus separately booked shaped power/basis hedges; explicit common-context residual report | Confirm composition/quantity fit and current scheduled-program boundary; disclose volume and measure limits |
| 2. Solar output-right toll | One synthetic available-output right with independent access-payment and delivery schedules | Explicit entitlement, control/risk allocation and fee basis; unsupported percentage or contingent clauses refuse |
| 3. Supplied-plan generation toll | One exclusive synthetic thermal right, explicit dispatch/fuel/fee terms and managed feasibility/margin preview | Resolve unit/right, perspective, fee and commitment policies; independently enumerate small feasible schedules |
| 4. Supplied-plan battery toll | One exclusive synthetic battery right with a complete state path and energy/fee preview | Energy conservation, boundary efficiencies, duration limits and terminal state; no implicit initial inventory |
| 5. Standalone REC forward and inventory | One synthetic eligible class/vintage, delivery/payment contract and evidence-backed lot allocation | Ownership/overlap checks, transfer/retirement transitions and replacement of outstanding value; no real registry adapter implied |
| 6. Intrinsic dispatch | Generation and storage optimization behind the same instrument valuation contract | Independent exhaustive small cases, deterministic tie policy, full scenario repricing and state/quantity reuse checks |
| 7. Broader structures | Shared rights, solar plus storage, ancillary services, contingent clauses and stochastic models | Separate supported consumers, joint feasibility, numerical evidence and explicit contract versions |

The bounded supplied-plan portions of steps 2–5 are implemented in
[operating facilities](operating-facilities.md) and
[saved trading/inventory](structured-power-trading.md). Broader PPA hedge analysis,
state continuity, optional rights and settlement remain separate acceptance
examples. This table establishes no production qualification. All numbers are
synthetic. Scalar cash examples
explicitly use USD, discount factor one and zero other charges; they establish
arithmetic, not a market convention.

| Case | Independent expected behavior |
| --- | --- |
| Solar cash basis | Interval MWh `[0,2,3]`, indexed USD/MWh `[20,40,60]`, strike 45: receive-floating/pay-fixed cash is 35; capture price is 52. Missing generation is not an entered zero. |
| Capture versus block hedge | MWh `[1,3]` at prices `[20,80]` have capture price 65 despite unweighted mean 50. A flat block does not remove the shape/volume exposure. |
| Fixed-plan toll and hedges | Produce 5 MWh at 60, consume 35 MMBtu at 3, zero additional startup/no-load fuel, variable cost 2/MWh, one start charge 10, total capacity fee 20: margin 155. A +1 power shock changes value +5; a +0.10 gas shock changes it −3.50. Matching short-power and long-gas financial hedges offset those conditional changes. |
| Commitment constraints | Two positive-spread hours separated by a loss hour can require staying on or another startup. Exhaustive feasible paths, including initial state and minimum up/down time, determine value; independent hourly maxima are not the oracle. |
| Storage conservation | Start/end 0 MWh, charge 10 grid MWh with efficiency 0.9 and discharge efficiency 0.9: deliver 8.1 MWh. Prices 20 then 100 give margin 610 before separately selected costs. Insufficient power/duration or energy capacity refuses the path. |
| Storage boundary | Opening stored energy retains price exposure in remaining value; the retained opening basis belongs to separate P&L. Terminal constraints cannot relax implicitly. Missing initial state, simultaneous cycling, invalid efficiencies and a discontinuous amendment state refuse. |
| REC inventory | Receive 100 confirmed eligible certificates, allocate/transfer 60, retire the remaining 40: available balance is 0. A forecast entitlement adds no issued inventory; a different vintage or program cannot fill the delivery implicitly. |
| Bundled rights | Adding optional REC delivery to a bundled solar contract creates no extra payment. Battery discharge and a second booking cannot claim the same generation certificates. |
| Side schedule ownership | Independently dated access fees, output deliveries and fuel/charging rows reconcile from their own Side roots to instrument value. Coupled quantities reference one operating-plan output set; scenario evaluation changes values without replacing retained instructions. |
| Time and correction | 23/25-hour days, repeated local hours, fuel-day mappings and later meter/outage corrections preserve native UTC identity and the selected knowledge cutoff. |
| Reuse and concurrency | Equal rights/state/coverage reuse one unit calculation; distinct initial state does not. Concurrent overlapping resource rights or certificate allocations cannot both succeed. |
| Lifecycle and recovery | Duplicate commands and repeated facts have one effect. Lost acknowledgement, partial delivery and corrections retain lineage; completed delivery/inventory and fixed cash cannot retain duplicate live value. |
| Authority and completeness | Revocation blocks historical reads and result publication. An unsupported selected holding makes complete totals unavailable; no portfolio omission or borrowed authority is permitted. |

Before admitting a broader form, select its toll perspective and fuel/payment
arrangement, operating constraints, battery boundary/terminal policy and REC
program/delivery rules. These remain explicit product-contract choices. The
current supplied-plan slice records its supported choices in the capability
contract; full optionality is later work.

Independent adversarial review is required before publishing changes to numerical,
temporal, authorization, scenario-reuse or operational boundaries. Runtime,
database, browser and recovery qualification belongs to each implemented slice;
this proposal supplies no production-readiness or performance claim.

## 10. Public source basis

The cited material supplies terminology and physical/accounting distinctions.
The proposed OpenETRM contracts and sequence above are design inferences, not
rules imported from these sources. Consulted 2026-11-15; no external dataset or
contract text is copied into this repository.

- [DOE PPA overview](https://betterbuildingssolutioncenter.energy.gov/resources/power-purchase-agreements-ppa-overview): distinguishes physical and virtual structures and discusses environmental attributes.
- [EIA glossary: tolling arrangement](https://www.eia.gov/tools/glossary/?id=t): input conversion and processing-fee arrangement; specific dispatch and fuel obligations still require contract terms.
- [SAM front-of-meter battery dispatch documentation](https://samrepo.nlr.gov/help/battery_dispatch_fom.html): separates supplied/manual dispatch from automated dispatch, state limits and charging-source choices. Its software defaults are not proposed OpenETRM policies.
- [EPA REC description](https://www.epa.gov/green-power-markets/renewable-energy-certificates-recs): distinguishes certificate attributes, generation vintage, tracking identity and eligibility; a REC represents renewable-generation attributes rather than electrical storage energy.
- [EPA energy-attribute tracking systems](https://www.epa.gov/green-power-markets/energy-attribute-tracking-systems): describes certificate issuance, ownership tracking and transfers; application forecasts alone do not establish registry evidence.
