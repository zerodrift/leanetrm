# Commercial gas storage contract (D431, D432)

Status: a storage contract can be entered from New trade, booked, given a plan,
valued and listed. Managed valuation and the plan search are checked locally;
persistence and the HTTP operations are checked against a disposable database
and preview. The ticket and Details view have not been looked at in a browser.
There is no facility link, discounting, measured flow or settlement yet; see
[Remaining work](#remaining-work). Nothing here is qualified for release.

## Contract and facility

A storage contract is a commercial right in someone's storage: a contracted
inventory range, daily injection and withdrawal limits that depend on how full
the contract is, fuel retained in kind and charges. It is separate from the
[physical facility](storage-facility-operations.md), whose own rules, activity
and capacity are not multiplied by the contracts held in it. A contract's
opening inventory, limits and plan are its own.

`GasStorageEntitlement` states:

| Term | Meaning |
| --- | --- |
| `Quantity` | The one quantity every amount is in |
| `MinimumInventory`, `MaximumInventory` | The contracted inventory range |
| `Ratchets` | Bands that cover the range exactly, each with a maximum daily injection and withdrawal |
| `InjectionFuel`, `WithdrawalFuel` | Fractions in [0, 1) retained in kind; zero is stated, not assumed |
| `RatchetBasis` | `gas_day_opening_inventory_band_v1` |
| `FlowPolicy` | `single_direction_per_gas_day_v1` |
| `FuelBasis` | `in_kind_fuel_at_storage_point_v1` |

A gas day's limits are those of the band its opening inventory is in, for the
whole day, even when the day's flow carries inventory into another band. Bands
are half-open from below; a full contract is in its last band. This is the
commercial daily-ratchet law. It differs from the facility's physical law, which
tests a continuous flow against every band it crosses.

Injection and withdrawal are measured at the storage point: gas delivered to the
contract and gas delivered from it. With injection fuel `f`, inventory rises by
`Injection × (1 − f)`. With withdrawal fuel `g`, a delivered withdrawal takes
`Withdrawal / (1 − g)` from inventory. Daily limits apply to the quantities at
the point.

## Supplied plan

`GasStoragePlans.Evaluate` (`gas_storage_supplied_plan_v1`) takes the
entitlement, the opening inventory at the first gas day's start, a terminal
inventory range and one row per gas day with its injection and withdrawal. Gas
days are supplied with their exact bounds and operating date; they are whole
(23, 24 or 25 hours), ordered and contiguous. The operation steps inventory
exactly, day by day, and refuses a plan that:

- flows both ways on one gas day (`gas_storage_direction`);
- exceeds the opening band's daily limit (`gas_storage_ratchet`);
- leaves the contracted inventory range (`gas_storage_inventory`);
- ends outside the stated terminal range (`gas_storage_terminal`).

It returns each day's band, opening and closing inventory and fuel, and the
totals. It performs no pricing, optimization or data selection and assigns no
title or cash.

## Proposed plan (D432)

`GasStoragePlanProposals.Propose` (`gas_storage_inventory_lattice_search_v1`)
finds the plan worth most under stated prices. Its inputs are the entitlement,
opening inventory and terminal range of a supplied plan, plus:

- for each gas day, what one unit delivered into the contract costs and what
  one unit delivered out of it earns. The caller states both, with whatever
  forward, charge and discounting it selected. The search resolves no price;
- an inventory step.

The search considers plans that move inventory in whole steps from the opening
inventory, inside the contracted range, and maximizes

```text
Σ Withdrawal × WithdrawalValue − Injection × InjectionCost
```

by exact backward induction over those inventory levels, under the same daily
ratchet, direction, fuel and terminal rules as a supplied plan. Arithmetic is
exact integers; nothing is rounded or iterated to a tolerance. Among plans of
equal value each day takes the smaller flow, then the lower inventory, so the
same inputs always give the same plan.

The result is the optimum of that lattice, not of every real plan: a finer step
can find more value, and a plan that would stop between two levels is not
considered. The step is an explicit input and is returned with the proposal.
With fuel in kind, the step must make the delivered quantities exact decimals
(a step of 990 with 1% injection fuel is 1,000 delivered in); otherwise the
search refuses with `gas_storage_step`. It refuses a problem beyond 4,001
levels or 20 million day-level-move combinations (`gas_storage_work`), and one
with no lattice plan that meets the terminal range (`gas_storage_infeasible`).

The proposed plan is passed through `GasStoragePlans.Evaluate` before it is
returned, so a proposal is always a plan the contract admits. It is a
proposal: valuing or booking it is the same step as for a plan the desk wrote.
This is intrinsic value against one set of prices. There is no rolling
re-optimization, price model or option value above intrinsic.

## Valuation

`GasStorageInstrument` is a `SidedInstrument`. `Compose` first admits the plan
through `GasStoragePlans.Evaluate`, so a plan the ratchets, capacity, fuel or
terminal range refuse never becomes an instrument. The plan's daily quantities
are then literal terms of five ordinary operating Sides, which own every
schedule, price and payment:

| Side | Sign | Quantity | Price |
| --- | --- | --- | --- |
| Floating | +1 | each gas day's withdrawal | that day's withdrawal curve contract |
| Floating | -1 | each gas day's injection | that day's injection curve contract |
| Fixed | -1 | each gas day's injection | injection charge |
| Fixed | -1 | each gas day's withdrawal | withdrawal charge |
| Fixed | -1 | contracted capacity, once per payment month | monthly capacity charge |

The instrument adds no pricing of its own. The value is the intrinsic value of
the plan in force against the forward curve, net of charges, and risk is the
ordinary base/up/down repricing of those Sides under shared scenarios. A
different plan is a different valued schedule and identity; the contract's
retained terms do not change. Inventory left at the end of the plan carries no
value, and opening inventory has no cost. Once the first gas day begins the
instrument refuses with `gas_storage_started`: planned flows have become
measured ones this profile does not own.

`OperatingRightInstrument` with the two storage output kinds remains available
for a plan supplied as typed quantities; the booked path uses the literal
composition so the standard engine values it with no separate quantity input.

## Booking and plans

Migration `0370_gas_storage_contracts.sql` retains a booked contract in
`trading.gas_storage_contracts` (inventory range, fuel, policies, opening and
terminal inventory, charges, term, gas-day clock and start minute, with
exact-version references to the definition, both curves, the quote, the clock
and the payment rule), `gas_storage_ratchets` (the bands in order),
`gas_storage_days` (one row per gas day with its exact bounds, payment instant
and the curve member each flow is priced at) and `gas_storage_trade_revisions`.
A held contract is one exclusive right: it has no quantity to scale it.

`security.book_gas_storage` takes the closed `gas_storage_trade_v1` command and
the expanded gas days. It requires booking authority, the selected policy
versions, a coherent range with opening and terminal inventory inside it, bands
that cover the range exactly, fuel fractions below one, nonnegative charges, two
outright curves under one quote in the contract's quantity, and gas days that
are the whole term in order, contiguous, ahead of the booking instant, paid
once per month and each on the one contract delivering it. A retried command
returns its first outcome; the same contract, book and parties under a new
command are the same fact.

The plan is revisable and separate from the contract. `trading.gas_storage_plans`
holds one revision chain per contract. `security.record_gas_storage_plan` takes
the closed `gas_storage_plan_v1` command: one row per gas day in order, one
direction a day, the expected head revision, and its source (entered by the
desk, or the lattice search with its step). The same flows at the same instant
are the fact already recorded; a stale expected revision or an earlier
effective instant refuses. Reads return the latest plan revision known and
effective under the selected knowledge context, or none. The database retains
exact, complete, one-way flows; admission under the ratchets is the managed
operation's, applied before a plan is recorded and again whenever it is valued.

## Managed operations

| Route | Authority | Behavior |
| --- | --- | --- |
| `POST /gas-storage/catalog` | `ReadTrades` | What the ticket chooses from: outright curves with quote, quantity and units, fixed-offset gas-day clocks, the supported payment rule and policy versions, and the booking directory. |
| `POST /gas-storage/book` | `BookTrade` | Validates the entitlement, quote, charges and payment rule, expands the gas days on the gas-day clock with their payment instants and delivering contracts, and calls the native booking. |
| `POST /gas-storage/plan` | `BookTrade` | Admits the plan through the contract's entitlement, then records it. A plan the ratchets refuse records nothing. |
| `POST /gas-storage/propose` | `ReadTrades` | Runs the lattice search at the selected snapshot's forwards: injection cost is the forward plus the injection charge, withdrawal value the forward less the withdrawal charge, undiscounted. Returns the plan's path and objective. It records nothing. |
| `POST /gas-storage/get` | `ReadTrades` | The contract as the blotter lists it, with its ratchets, gas days and, when a plan is in force, that plan's day-by-day inventory path; valued, or with the reason it is not. |
| `POST /gas-storage/status` | `ReadTrades` | The outcome of a booking or plan command. |

A booked contract is an ordinary position under instrument type `gas_storage`
and measure `gas_storage_plan_present_value`: it is in position facts, the
single-position read, the risk trade directory, portfolio valuation and the
blotter. On the blotter each contract is valued on its own; one with no plan in
force, a plan its entitlement no longer admits, or a started term is listed
with that reason and no value. A portfolio valuation or risk run that includes
such a contract refuses instead. The payment rule is the monthly calendar-day
unadjusted rule the operating Sides support. Cash is valued undiscounted, stated
in the valuation setup.

### Ticket and Details

New trade lists Gas storage under Structured. The ticket has one field per row
in stacked sections, and the ratchet bands as a small table: each row states
where its band ends and its two daily limits, so the bands cover the range with
no gap by construction and the last ends at capacity. The limits are drawn
against inventory as they are typed, with the opening and terminal inventory
marked. Nothing is preselected; zero fuel and zero charges are entered, not
assumed. The drawing decides nothing: the backend judges every amount.

A booked contract's Details draw the ratchets and, when a plan is in force, its
inventory through the term against capacity and the band edges with each day's
flow beneath. Propose plan runs the search for a stated inventory step and draws
the proposed path with its objective; Record as plan records it as the next
plan revision with a reason. A plan written by the desk is recorded through the
API. The ticket and both drawings report their own assistant page identity
(`blotter_new_gas_storage`, migration `0371_gas_storage_entry_page.sql`).

## Verification

`dotnet run --project tests/OpenEtrm.MarketData.Checks -- --gas-storage` runs
fourteen checks. Eight on supplied plans, against figures worked by hand: inventory and in-kind fuel over a
three-day plan; limits taken from the opening band; capacity and terminal
bounds; withdrawal fuel; one direction per day, grid, opening and
implementation refusals; entitlement policies and band coverage; and the plan
valued as a resource right at −8,150, moving to −8,400 and −7,900 under a ±0.1
gas shock; and the booked composition valued through the risk engine at the
same figures, refusing a plan its ratchets do not admit. Six on proposals: a hand-worked four-day plan worth 1,100 inside the
ratchets; no flow when the spread does not cover its costs; a terminal
inventory met at least cost and the tie-break; fuel in kind and the step it
needs; agreement of the objective with an exhaustive search of every lattice
plan on three cases of four or five days, each replayed as an admitted plan
worth its stated objective; and the refusals.

`python3 tools/check_gas_storage_contract_storage.py --pg-bin <PostgreSQL 18 bin>`
runs seven checks on the booking and plan storage: authority and closed tables,
malformed commands, policies, capacity, ratchet coverage and reference terms,
gas-day order and contract coverage, replay and the same-fact no-op, plan
coverage, revision order and replay, immutability, and reads of the contract
and the plan in force by knowledge time.

`python3 tools/run_preview.py --check --structured-power-check` includes three
HTTP checks: the catalog, booking through the managed gas days with replay and
refusals, and an unplanned contract listed as unpriced; a plan the ratchets
refuse recording nothing, and an admitted plan valued exactly at its flows and
charges from the snapshot forward with its inventory path returned; and the
proposed plan as the lattice optimum, refused for a step its fuel does not suit,
adopted as the plan in force and revaluing the ordinary position.

`web/tests/gas-storage-ticket.test.mjs` runs five checks on the ticket's state.
No automated check draws the charts.

## Remaining work

1. A look at the ticket, the Details drawings and the propose and record flow
   in a browser; a plan editor for a desk-written plan.
2. A link from a contract to the facility it is held in, with allocation of the
   contract's flows to facility activity.
3. Value or return terms for inventory held at the end of the plan, and a cost
   for opening inventory.
4. A retained discount binding, so payments are discounted, and the same
   discounting in the proposal's objective.
5. Measured flows, inventory actuals and settlement cash, so a started term is
   valued. Until then a contract stops being valued when its first gas day
   begins.
6. A risk run over a booked contract; rolling re-optimization and any option
   value above intrinsic, each with its own decision.
7. Interpolated or percentage ratchets, cash fuel, simultaneous nominations,
   overrun charges, written (sold) capacity and amendment.
