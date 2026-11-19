# Commercial gas storage contract (D431, D432)

Status: the contract's entitlement, the feasibility of a supplied injection and
withdrawal plan and a plan proposed from stated daily prices are implemented and
checked locally, with a plan's flows valued as an existing resource right.
Nothing is stored, booked or shown in the
application yet; see [Remaining work](#remaining-work). Nothing here is
qualified for release.

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

The plan's flows are typed quantities for the existing
`OperatingRightInstrument`, through two added output kinds, `StorageInjection`
and `StorageWithdrawal`. The contract's economics are ordinary operating Sides
that each own their schedule and price:

- a floating Side that buys each day's injection at its stated curve contract;
- a floating Side that sells each day's withdrawal at its stated curve contract;
- fixed-price Sides for the injection and withdrawal commodity charges;
- a fixed-price literal Side for the demand charge.

The value is therefore the intrinsic value of the stated plan against the
forward curve, net of charges. Risk is the ordinary base/up/down repricing of
those Sides under shared scenarios; a gas price shock moves value by the plan's
net sold quantity at the shocked contracts. Inventory left at the end of the
plan carries no value here.

## Verification

`dotnet run --project tests/OpenEtrm.MarketData.Checks -- --gas-storage` runs
thirteen checks. Seven on supplied plans, against figures worked by hand: inventory and in-kind fuel over a
three-day plan; limits taken from the opening band; capacity and terminal
bounds; withdrawal fuel; one direction per day, grid, opening and
implementation refusals; entitlement policies and band coverage; and the plan
valued as a resource right at −8,150, moving to −8,400 and −7,900 under a ±0.1
gas shock. Six on proposals: a hand-worked four-day plan worth 1,100 inside the
ratchets; no flow when the spread does not cover its costs; a terminal
inventory met at least cost and the tie-break; fuel in kind and the step it
needs; agreement of the objective with an exhaustive search of every lattice
plan on three cases of four or five days, each replayed as an admitted plan
worth its stated objective; and the refusals.

## Remaining work

1. Retained contract, plan and booking; the plan-to-quantity mapping in the
   managed API; blotter and detail presentation; an entry ticket. New trade
   still lists Storage as Coming soon.
2. A link from a contract to the facility it is held in, with allocation of the
   contract's flows to facility activity.
3. Value or return terms for inventory held at the end of the plan, and for
   opening inventory.
4. A managed operation that resolves the proposal's daily prices from the
   selected scenario's forwards and the contract's charges, and offers the
   proposal for adoption as the plan.
5. Rolling re-optimization and any option value above intrinsic. Each needs
   its own decision.
6. Interpolated or percentage ratchets, cash fuel, simultaneous nominations and
   overrun charges.
