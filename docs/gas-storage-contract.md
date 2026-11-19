# Commercial gas storage contract (D431)

Status: the contract's entitlement and the feasibility of a supplied injection
and withdrawal plan are implemented and checked locally, with the plan's flows
valued as an existing resource right. Nothing is stored, booked or shown in the
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
title or cash. The plan is the desk's own schedule: nothing here chooses when to
inject or withdraw.

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
seven checks against figures worked by hand: inventory and in-kind fuel over a
three-day plan; limits taken from the opening band; capacity and terminal
bounds; withdrawal fuel; one direction per day, grid, opening and
implementation refusals; entitlement policies and band coverage; and the plan
valued as a resource right at −8,150, moving to −8,400 and −7,900 under a ±0.1
gas shock.

## Remaining work

1. Retained contract, plan and booking; the plan-to-quantity mapping in the
   managed API; blotter and detail presentation; an entry ticket. New trade
   still lists Storage as Coming soon.
2. A link from a contract to the facility it is held in, with allocation of the
   contract's flows to facility activity.
3. Value or return terms for inventory held at the end of the plan, and for
   opening inventory.
4. A plan proposed from the forward curve (the intrinsic optimum), rolling
   re-optimization and any option value above intrinsic. Each needs its own
   decision: the repository values supplied plans and contains no optimizer.
5. Interpolated or percentage ratchets, cash fuel, simultaneous nominations and
   overrun charges.
