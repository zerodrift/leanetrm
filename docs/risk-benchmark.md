# Shared scenario calculation benchmark

This original synthetic benchmark qualifies managed calculation, not database or
service throughput. Reproduce it with the pinned SDK:

```sh
dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --risk-benchmark /tmp/openetrm-risk-benchmark.json
```

Observation on 2026-10-19: .NET 10.0.13, macOS 26.6.2, Arm64, 14 logical
processors, Release, sequential single-threaded calculation on an unisolated local
host. One warm-up precedes five samples; each run has fresh valuation caches.
No cross-run price cache is measured. Values below include base evaluation.

All workloads have **50,000 holdings**. Factors are independently shocked with
explicit absolute up/down pairs; 433 scenarios means one base plus 216 pairs,
not 433 simultaneous-factor combinations.

| Workload | Unique unit instruments | Factors / scenarios | Median | Allocations | Unit valuations | Quantity applications |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Repeated, holding detail | 1 | 1 / 3 | 283.22 ms | 359.39 MiB | 3 | 150,000 |
| Shared fan-out, holding detail | 128 | 3 / 7 | 271.75 ms | 382.77 MiB | 388 | 151,564 |
| Distinct, holding detail | 5,000 | 1 / 3 | 618.26 ms | 452.04 MiB | 15,000 | 150,000 |
| 1-month basis, book totals | 9 | 9 / 19 | 0.97 ms | 1.65 MiB | 53 | 212 |
| 24-month basis, book totals | 216 | 216 / 433 | 150.85 ms | 242.98 MiB | 1,272 | 5,088 |
| Futures, book totals | 1 | 1 / 3 | 136.85 ms | 187.14 MiB | 3 | 12 |

Allocations are median current-thread total bytes allocated during a run, not
retained memory or peak resident memory. The generated private JSON records the
sample range, environment, work counters, preparation costs and reference timings.
Database reads, authorization, retained context resolution, serialization,
concurrent requests and browser work are excluded. Booking database bounds are
unchanged; this does not demonstrate a 50,000-booking hosted service.

The first three workloads have base-plus-basis DAGs with 3, 257 and 10,001 nodes,
and 2, 129 and 5,001 primitive inputs. They include portfolio preparation and
comparison of every holding/book against a separately prepared independent
full-repricing reference inside timing. Reference calculations use independent
synthetic snapshots, full curve builds and a direct formula per holding. The
repeated and shared-fan-out workloads use two books. Distinct means 5,000 unique
instruments, each held repeatedly; it does not mean 50,000 unique instruments.
Simple references can be as fast as the general path; fewer unit calls alone do
not establish a universal latency improvement.

The futures workload includes retained input/lifecycle validation, lot
normalization and book preparation. It uses one shared 40-unit fixed-total future
across four books; exactly three instrument valuations and twelve net quantity
applications cover base/up/down. Validation does not compute separate holding
P&L. An independent signed-lot/mark oracle validates results outside timing.

## Multilevel basis workload

Synthetic monthly OTC recipes use public market-location names; they are not a
claim that these exact inter-hub differentials are exchange-listed products or
that these observations are real prices. Every edge is an independent explicit
basis input in matching currency, quantity, delivery period and profile:

```text
Henry Hub
  + (Houston Ship Channel - Henry Hub)       -> Houston Ship Channel
  + (Waha - Houston Ship Channel)            -> Waha
  + (SoCal Border - Waha)                    -> SoCal Border
  + (SoCal Citygate - SoCal Border)          -> SoCal Citygate

ERCOT North RT peak
  + (Houston Hub RT peak - North RT peak)    -> Houston Hub RT peak
  + (Houston Zone RT peak - Houston Hub)     -> Houston Zone RT peak

Independent Waha all-in mark                -> its own factor only
```

There are nine held unit swaps, nine primitive quotes and fifteen dependency
nodes per maturity. All instruments are real fixed/floating `FinancialSwapInstrument`
instances with external physical quantities and explicit undiscounted payments;
pricing delegates to their side PV implementations. Gas uses MMBtu and power MWh.
A separate independent signed quote-incidence oracle checks every book PV,
up/down P&L, delta and zero gamma outside timing; smaller correctness checks also
verify every holding, upstream hedge cancellation and all-in independence.

The 24-month case holds 216 unique instruments **in all four books**, giving 864
book/instrument quantity groups. Its 50,000 quantities are aggregated once during
plan preparation. Of 50,000 holding references, 49,784 are additional references to
shared instruments. Of 93,528 possible unit/scenario valuations, 1,272 execute and
92,256 reuse unaffected base values. There are 5,088 net quantity applications,
1,704 curve-node evaluations and 154,176 curve-node reuses. These are computation
counts, not exposure allocations inferred from dependencies.

The 24-month plan preparation median is 28.90 ms,
with 45.74 MiB allocated. It is measured separately
from the 150.85 ms scenario run. The one-month case prepares 36
book/instrument groups in 32.59 ms. Position/model/schedule construction
is excluded; preparation here means compiling existing validated instrument and
holding objects into the book valuation projection. Adding phase medians is not
an end-to-end latency measurement.

The core report exposes actual unit calls, reused unit/scenario values, repeated
instrument references and quantity applications. Detailed reports intentionally
retain holding rows and their arithmetic; book-only mode avoids that matrix.
Exact quantities/cash remain unrounded until the selected output boundary.
Cancellation and independent adversarial review cover the common valuation path.
These results do not qualify options, rate/volatility shocks, futures variation
margin, settlement PV or production capacity.
