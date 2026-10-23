# Interval data and calculations

D212 keeps interval values independent of TOUs and instruments. Stored series retain
kind, exact specification, clock, cadence and immutable correction history. The
existing PostgreSQL schema and authorized reads remain authoritative; there is no
additional database or native numerical dependency.

## Shared calculation foundation

`UtcGrid` supplies exact, half-open UTC intervals. A regular grid retains origin,
step and count instead of one timestamp per row. Sparse grids retain gaps; they
can change UTC phase across a clock transition. Nonuniform grids retain both
boundaries. Equality, exact lookup and overlap depend on boundaries, not local
hour labels. Touching intervals do not overlap.

`UtcSelection` is an immutable bitmask bound to an exact grid, with intersection,
union, overlap and allocation-free selected-slot enumeration. `TimeOfUse.Select`
uses the existing market-clock and holiday rules to produce that selection.
`PowerHours` consumes the selection when generating labeled schedule rows. TOUs
never create a numerical observation or fill missing data.

`IntervalData` stores exact values and revision numbers in 4,096-row column chunks.
Values fitting the supported compact representation use Int128; wider values
retain BigInteger without truncation. Builders transfer ownership on completion
and reject subsequent mutation. Temporary start columns are released after the
shared axis is built. An absent row is missing, while a stored zero remains zero.

Supported kernels are a selected sum, an exact dot product, elementwise products
into caller-owned memory, and column cross-products (`XᵀY`). The latter's current
executable baseline prices multiple synthetic meter profiles against multiple
price scenarios. Every participating column and mask must name identical UTC
intervals. There is no implicit join, resampling, duration multiplication or
normalization. Elementwise product numerators have common denominator 10^36;
dot/matrix entries return reduced exact fractions. Exact common input divisors
reduce intermediate magnitudes without changing values. Checked bounds and wide
integer accumulation preserve overflow cases rather than using floating point.

These are arithmetic kernels, not new financial operations. Consumers still
validate exact units, specifications, context and selected policy. A price shape
uses an explicit additive or multiplicative calibration policy. A meter quantity
is not normalized as a weight. The authorized operation remains the boundary for
knowledge selection and permissions.

## Current consumers

- Block shaping references compact rows through schedule-generated selections.
  Whole-block factor sums are prepared once and reused across market scenarios;
  scenario-dependent prices are still recalibrated. Sparse block selections do
  not allocate a full multi-year bitmask for every month.
- Native PPA price reads append bounded database windows directly to the builder,
  retaining only knowledge metadata rather than all source JSON documents.
- Meter and forecast reads use `IntervalQuantityData`: the same value table plus
  per-row indexes into distinct knowledge/context metadata. Revision alone is
  not assumed to identify a read context. Quantity preparation validates every
  supplied row and retains exact cutoff and revision-selection behavior.
- Legacy in-memory quantity fixtures can supply revision rows. A quantity series
  must supply either those rows or compact data, never conflicting representations.

Preparation, schedule labels and detailed price/PPA output still have their own
allocations. This is not a claim that the full application streams with constant
memory, nor that every financial calculation is a matrix multiplication.

## User interface

Interval data is the reusable input table. Hourly/sub-hourly pricing is a view
inside Curves & prices. Existing route identities remain valid for saved links
and page-assistant navigation.

Both interval screens and the swap Side payment schedule use `mountIntervalDataWidget`
for their plot and table. Side schedules retain exact numeric filter values separately
from formatted cells and plot backend cash/PV per payment without aggregation;
payments sharing a date remain distinct. Reset and lineage inspections remain available. Values and
financial arithmetic come from the backend. Missing intervals break plotted
lines; zero values remain visible. Plot lookup is linear in input points, and
same-scope updates retain the chart instance and zoom. Table filters, scroll and
focused filter controls are retained on same-scope updates.

Dashboard hosts may provide an authorized loader. Automatic refresh starts off,
with explicit five- or thirty-second choices. Reads never overlap, hidden or
removed views stop polling, and invalidation/disposal suppress stale responses.
A failed refresh retains the previous display with a visible error. Hosts must
call `clear()` when account, financial context or selection changes and `dispose()`
when abandoning a widget. The widget does not choose a new market snapshot or
advance a knowledge cutoff. The existing editor/pricing screens retain their
explicit read controls; no dashboard or automatic financial-context advancement
is introduced.

Hosts should request bounded display windows. The table currently renders the
requested window; it is not a virtualized view of millions of rows. Million-point
measurements below qualify backend kernels, not browser rendering.

## Reproducible numerical measurements

Run the synthetic benchmark with an output path outside the repository:

```sh
DOTNET_TieredCompilation=0 dotnet run -c Release \
  --project tests/OpenEtrm.MarketData.Checks -- \
  --interval-data-benchmark /tmp/interval-data-benchmark.json
```

The measured run used .NET 10.0.13, macOS 26.6.2, Arm64, 14 logical processors,
Release, with tiered compilation disabled. The table cases report medians of
three samples and compare the former row-object layout with compact columns.
Each contains one unique series, one selected sum, twenty years of intervals,
and an explicit daily 08:00–20:00 UTC selection. Half the points participate.
Numeric input objects are shared in the baseline, so it does not charge repeated
parsing to the old representation. Exact results must agree before reporting.

| Cadence | Stored points | Row-object build allocation | Compact build allocation | Row-object sum | Compact sum |
| --- | ---: | ---: | ---: | ---: | ---: |
| 60 minutes | 175,320 | 14,025,664 B | 4,938,488 B | 2.11 ms | 0.26 ms |
| 15 minutes | 701,280 | 56,102,496 B | 19,752,872 B | 8.88 ms | 1.15 ms |
| 5 minutes | 2,103,840 | 168,307,288 B | 59,032,280 B | 26.99 ms | 3.42 ms |

Each compact sum allocated 80 bytes; the five-minute baseline sum allocated
42,076,656 bytes. Build allocation includes temporary columns, so it is not
retained-memory or peak-process usage. Runtime retained-memory deltas were
inconsistent with allocation counters and are intentionally not reported.

The warmed matrix case uses 32 independent meter columns at 12 MWh and 16 price
scenario columns at 50 price units, each with 2,976 quarter-hour intervals. A
weekday 08:00–20:00 UTC TOU selects 1,008 rows. Its 516,096 exact products produced
512 results in 17.19 ms with 49,312 bytes allocated. The independent oracle is
600 times the selected interval count per result. This case excludes input
construction, database I/O, unit validation, output formatting and rendering.
These are local workload observations, not performance guarantees.
