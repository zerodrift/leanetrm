# Exact pricing lookups and monthly swap valuation

Commodity builds index exact curve membership, target-to-source mappings and
selected observations after their existing definition/input validation. Lookup
does not change mark interpretation, choose another contract or select a newer
observation. Missing mappings and due actual fixings remain errors. Inputs and
indexes belong to immutable selected models/builds; they are not permission caches.

Discounting prepares the complete selected zero-rate curve once for the valuation,
retaining validation of every pillar. Payment dates use binary search, repeated
dates share their factor/evidence, and each side retrieves its own factors by exact
side identity. Each payment still verifies its currency, date and complete context.
There is no cross-request result cache or implicit discount-curve selection.

`ZeroRateLookup.AtPillar` retrieves a retained exact zero rate, discount factor and
observation identity. Missing exact pillars fail. It does not invent a zero-rate
interpolation convention. Payment discount factors retain the existing explicit
log-linear discount interpolation policy and require bracketing pillars; there
is no extrapolation. Exact integer bounds accelerate the root search while
preserving its final exact half-integer comparison, rounding policy and maximum
3,660-day pillar interval. Sparse interpolation still costs more than an indexed
exact-pillar lookup.

Amount formatting, increment rounding and rate-factor construction share
`OpenEtrm.Shared.Numerics.IntegerRounding`. Units, financial conventions, numeric
bounds and structured errors stay in their owning modules.

## Managed monthly strips

`MonthlySwapSide` composes existing fixed or floating monthly side definitions.
Their index sampling, actual-fixing selection, payment rules, calendars, price
lookups and quote scaling are reused. Supported strips contain one to 1,200
ordered, contiguous complete months within the existing supported date domain.
`MonthlyFinancialSwap` pairs fixed and floating legs with the same delivery and
quantity-allocation scope, opposite unit coefficients and explicit discounting.
Each period retains its own payment date, allocated cash, discount inputs and PV.

Quantity basis is an explicit, versioned economic term:

| Basis | Allocation of one external trade unit |
| --- | --- |
| Per month | One unit in every delivery month |
| Total, equal months | `1 / number of months` in each month |
| Total, calendar days | The month's actual calendar days divided by all delivery days |

Allocations remain rational, including nonterminating equal-month shares. Each
payment's reset cash reconciles to its allocated cash. Membership coefficients
apply to completed side PVs; the external trade quantity scales the instrument
once. No payment or per-unit rounding is introduced. These total-quantity choices
do not authorize a flow-rate, MW-to-MWh or hourly-profile conversion.

Old single-payment side identities and projection-only definitions retain their
meaning. Due payments require explicit lifecycle state and remain unsupported;
neither a matured payment nor missing actual becomes zero. The booking/editor and
database write contracts still support one month. This managed pricing capability
does not create persisted multi-year bookings, settlement or payment processing.

## Shared quote scenarios

Draft risk builds one base and streams its immutable up/down scenarios, preserving
unaffected built values. Its rate inputs are prepared once per request, on the
first pricing callback, and stay fixed under this commodity-quote shock contract.
Portfolio risk values a shared instrument once per scenario and applies every
trade binding's quantity outside it. Book totals reconcile with holding deltas.
Exact dated contracts, declared basis dependencies and frozen actuals remain
distinct; independent all-in marks acquire no inferred upstream exposure.

Checks cover long-dated monthly and daily prices, 10/30-year monthly strips under
all three quantity bases, individual payment DFs, multiple books and shared trade
bindings, offsets, frozen actuals, missing inputs and identity conflicts. Draft
quote risk retains its 128-factor limit. The interactive portfolio-risk screen
and its legacy API remain futures-only. Generated-close jobs use retained mixed
futures/swap holdings and persist exact reports with separate execution-P&L and
swap-PV measures (D169). That path does not create multi-year bookings or wire
swaps into the interactive risk screen.

## Focused measurements

These are synthetic, warmed, single-thread managed measurements on macOS 26.6.2
Arm64, .NET 10.0.13, with 14 logical processors. They exclude database, authorization,
transport and hosted-page latency. Financial outputs/evidence match independent
checks. Rate and risk comparisons disable tiered compilation. Curve builds use
three timed samples, exact-pillar rate preparation seven, sparse rate preparation
three, and draft risk five. The 100,000-query row is one aggregate pass after
warm-up. These establish bounded observations, not a concurrent-throughput or
service-latency qualification.

| Workload | Before | After |
| --- | ---: | ---: |
| 7,305 dated contracts; 3 curves; 14,610 inputs; 21,915 evaluated nodes; one scenario | 219.77 ms | 57.34 ms |
| 128 payment dates; 128 rate pillars; 640 snapshot inputs; one valuation | 84.26 ms / 114.47 MB allocated | 0.73 ms / 0.98 MB |
| 360 monthly payment dates; 31 rate pillars; 329 interpolations; one valuation | 675.59 ms / 42.94 MB allocated | 91.07 ms / 10.23 MB |
| 100,000 exact-pillar queries; 360 stored pillars | 42.78 ms / 8.80 MB | 1.46 ms / zero query allocation |
| Draft basket: 120 consumed contracts; 1,080 catalog contracts/observations; 120 factors; 241 scenarios | 222.62 ms | 211.61 ms |

The curve indexes trade memory for lookup speed: total construction plus three
build allocations were 197.91 MB before and 199.97 MB after; the three builds alone
fell from 119.36 MB to 110.94 MB. Those are allocated bytes, not retained or peak
memory. The draft basket's distinct observed built values fell from 1,320 to 360;
241 pricing callbacks and the exact financial results were unchanged.

The sparse-rate case uses explicitly selected 4% simple annual rates, a short
initial pillar and annual pillars thereafter, with gaps of at most 366 days.
Every monthly factor matches independent 90- and 150-digit decimal logarithm/
exponential oracles using the retained endpoint factors and ties-to-even at 18
digits. A zero-rate control isolates lookup/preparation from nontrivial root
work. The comparison changes only the exact root search: a weighted arithmetic
upper bound and integer Newton refinement near the root, with binary fallback
for distant bounds. Date coverage, interpolation policy and final rounding are
unchanged. This measurement covers DF preparation, not a complete swap valuation.
Annual gaps do not qualify every supported 3,660-day interval or large factor
ratio; those remain more expensive workloads.

The executable monthly-strip benchmark is separate from those before/after
comparisons. It prices one instrument against a prebuilt commodity market, with
one explicit rate pillar per payment date. It also measures one contract's shock
across 10,000 bindings in two books: three scenarios, three unit-pricer calls.
Model, fixture and portfolio construction are excluded; risk includes market
building and all holding results. Dense pillars do not measure sparse-interval
interpolation. The report includes samples, allocated bytes, actual evaluated work
and point-in-time managed/resident memory; peak memory is not measured.

Five timed samples after one warm-up on the final implementation gave:

| Term | Commodity contracts / rate pillars | One-instrument price | One-factor risk across 10,000 bindings | Allocated bytes, price / risk |
| --- | ---: | ---: | ---: | ---: |
| 10 years | 120 / 120 | 1.61 ms | 119.29 ms | 2.21 MB / 96.48 MB |
| 30 years | 360 / 360 | 6.26 ms | 127.39 ms | 6.42 MB / 107.08 MB |

These are medians. The fixtures contain 240/720 selected inputs and 240/720 side
payments respectively. The three risk scenarios rebuild 122/362 curve nodes and
reuse 238/718; base builds evaluate 120/360 nodes. Managed-heap/resident point
measurements were 54.80/107.99 MB and 76.36/136.18 MB respectively.

```sh
dotnet run --project tests/OpenEtrm.Rates.Checks -c Release -- --monthly-swap-benchmark
dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --commodity-lookup-benchmark
```
