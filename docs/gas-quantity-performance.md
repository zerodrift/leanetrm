# Daily gas quantity read performance

The quantity grid is a quantity-input consumer. It does not load market prices,
value a portfolio, determine settlement or calculate inventory costs. The current
fixed-contract PV is unchanged.

## Changes

The endpoint uses the shared exact trading-view binding without loading unused
market data. One bounded authorized batch resolves the heads for all obligation
and gas-day keys. The API groups those heads once, reads the retained programs and
performs exact selection/normalization. It never generates replacement schedules.
History is a separate demand-loaded read pinned to the grid's captured context.
The measured checkpoint used a standalone quantity grid with a history disclosure;
the current [deal ticket](deal-ticket.md) records daily quantities but does not
yet draw revision history or the strict billing selection. Their API operations
remain available. No selected-quantity result cache or permission cache was introduced.

Migration 0146 adds indexes for per-trade command counts and per-series history.
The batch adapter requires an exact bound request/catalog/context/as-of, checks
current trade, market, reference and setup authority before and after its read,
and applies the same publication high-water mark, certified receipt knowledge
and business-effective conditions to series and revisions. It scopes every row
to the selected trade and visible originating instrument. Ordinary forced row
security remains unchanged. The owner-function batch is not an unbound read path.

## Local measured workload

Qualification on 30 October 2026 uses a 14-logical-CPU arm64 workstation,
macOS 26.6.2, .NET SDK 10.0.402 and stock PostgreSQL 16.14. Each case samples
eight authorized loopback HTTP reads after synthetic booking/preparation and
revision writes. Warm median/max use the seven samples after the first. These
are not cold-process/cache measurements or production throughput claims.

| Work | Small grid | Full admitted daily grid |
| --- | ---: | ---: |
| Trades in measured request | 1 | 1 |
| Physical sides / unique retained programs | 1 / 1 | 4 / 4 |
| Gas days per side / selected cells | 1 / 1 | 31 / 124 |
| Quantity series / visible heads | 4 / 4 | 16 / 496 |
| Revision facts | 120 | 1,488 |
| Revisions per series/day | 30 | 3 |
| First HTTP sample after preparation | 40.79 ms | 134.17 ms |
| Warm HTTP median / max | 39.60 / 40.99 ms | 114.51 / 118.46 ms |
| Separate latest-100 history HTTP read | 20.81 ms | 22.26 ms |
| Observed API process RSS | 458,352 KiB | 571,040 KiB |

RSS describes the whole API process after fixture and benchmark operations,
not per-trade incremental memory or peak heap. There are zero price scenarios;
the evaluated work is one selected quantity per cell. Retained programs are
reused, but their protected bytes/identities and authority are still read and
checked. The full-grid response is approximately 188 KB including evidence and
all current heads; history is not in that response.

The diagnostic pre-batch implementation took about 4.4 seconds for a four-side,
31-day request with 496 heads and 1,488 revisions, almost all in repeated source
and row-policy proofs. That diagnostic used three unique programs; the final
case deliberately uses four. History exceeded the HTTP deadline in the diagnostic.
The final batch moves head resolution to approximately 11 ms without dropping
its authority or certified temporal conditions. This is not an exact paired
production benchmark or a claim about arbitrary trade counts.

The benchmark compares complete batch heads with independent ordinary runtime
row-security queries. Its small-grid 120-record case also compares the exact
latest-100 history array. The full-grid history is checked for bounded ordered
scope; historical/correction/withdrawal and same-program other-trade cases have
separate direct/API equivalence checks. Slow oracle queries are excluded from
HTTP timings. Original RLS proof comparison took about 1.93 seconds for the
small case and 10.09 seconds for the full head set.

Reproduce with `tools/run_preview.py --gas-quantity-benchmark` and the usual
installed `--pg-bin` and `--dotnet` arguments. The runner owns a fresh database;
all synthetic revisions use the normal authorized write gate. Optional
`measurePerformance` adds stage/count metadata to quantity-read responses.
`includeHistory` controls presentation work only and defaults to true for older
callers. Neither field changes financial selection or authority.

## Next valuation boundary

Operational valuation needs an explicit enrolled input profile. Existing literal
contractual quantities must not become runtime overrides. The next profile will
publish quantity-input programs once, reuse the common retained interpreter and
capture one immutable selected-quantity snapshot across price scenarios.

The first qualified profile should be future-only, preserving the started-gas
refusal. Linked stages must explicitly derive through the pinned fuel rule or
select independently eligible point evidence, with conflicts refused. Cash
programs need declared daily quantity inputs and whole-period reductions; scaling
an old scalar cash PV is insufficient. Completed delivery, outstanding value,
settlement and cost allocation require their own lifecycle policies and consumers.
