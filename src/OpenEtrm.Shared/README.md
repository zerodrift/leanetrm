# Shared utilities

Small, dependency-free .NET utilities used by executable temporal and numerical acceptance
cases. The named contract-sequence resolver stays in the market domain because
it carries exact contract/reference identities. Keep business definitions, storage and authorization in their owning
modules. Add common behavior here when it has a concrete consumer; do not turn
this folder into a general framework.

## Exact integer rounding

`IntegerRounding.RoundQuotient` rounds a signed integer fraction once, with a
positive denominator and an explicitly selected nearest/ties-to-even or
nearest/ties-away-from-zero rule. Amount formatting, increment rounding and rate
factor construction share this operation. Their callers retain responsibility
for units, scale, financial conventions, supported bounds and structured errors.
The utility selects no precision or financial policy and has no floating-point
intermediate. Checks compare signed fractions with an independent decimal oracle
and cover 300-digit half ties and unsupported inputs.

## Temporal payloads

| Type | Contract |
| --- | --- |
| `DateSet` | Immutable sorted unique dates within explicit half-open `[From, To)` coverage; membership, union, intersection and difference |
| `DateRolling` | Five explicit business-day adjustments and strict positive/negative movement; occurrence adjustment preserves order and duplicates |
| `MonthlyDates` | Bounded monthly anchors with explicit short-month policy and retained original/adjusted dates |
| `BitemporalRange` | Half-open effective dates and UTC knowledge intervals with a finite reserved open end |
| `DateReference` | Bounded names, occurrence indexes and signed business-day offsets; no scripting |
| `UtcGrid` | Immutable strictly increasing UTC interval boundaries; explicit uniform-grid size limit or supplied nonuniform boundaries |
| `UtcSelection` | Immutable slot bitmask bound to the exact grid, with exact selected duration in ticks |
| `ImmutablePool<T>` | Bounded, explicitly scoped, concurrency-safe canonicalization using full equality, including hash-collision checks |

Separate named definitions can share a canonical payload. A pool is owned by its
caller; it is neither global nor an authorization boundary. Sharing dates does
not merge obligations or qualify reuse of valuations. Pool values must have
immutable equality/hash behavior. Process-local hashes are not persistent IDs.

Date sets require matching coverage for composition. Queries outside coverage
fail rather than treating an unknown day as open. Rolling fails if a required
candidate is unavailable. The five supported adjustments, including Unadjusted,
validate the base against known coverage. Business-day Advance counts strictly
beyond the base using indexed open dates; zero movement is rejected. In particular, modified following requires a known
following candidate before deciding whether to move to the preceding month-end.

These are raw payloads: the caller must validate market clocks, date bases,
calendar versions, provenance and permissions. UTC grids do not infer local
hours or resolve DST. Tests supply explicit 23/25-hour grids and a synthetic
clock. A timezone/TOU compiler remains future work. The application now owns
a separate durable schedule draft/publication store.
The contract resolver is a separate domain consumer. `MonthlyDates` now generates
up to 120 explicit monthly anchors with short-month policy and adjustment, keeping
source months and unadjusted dates. `DateReference` parses bounded indexed names
and business-day offsets; domain reference lookup remains the caller's job. Set operations
are reusable coding capabilities; see the separate [named-shortcut implementation](../../docs/implementation.md#imported-contract-date-lookups).

## Checks and measurements

Run with the SDK pinned by the repository:

```sh
dotnet run --project tests/OpenEtrm.Shared.Checks -c Release
dotnet run --project tests/OpenEtrm.Shared.Checks -c Release --no-build -- --benchmark /tmp/temporal-benchmark.json
```

The 31 behavior checks include exhaustive small-set comparison against an
independent oracle, joint calendars, roll collisions, immutable ownership,
concurrent interning, deliberate hash collisions, mask word boundaries and
subhourly/DST durations. Behavior was written and observed failing before the
implementation.

An initial Release measurement on .NET 10.0.13, Arm64, Unix 26.6.2 with 14 logical
processors used seven warmed, single-thread samples per operation. The synthetic
workload has 3,653 covered dates, 2,609 business dates, 8,760 hourly intervals and
3,132 selected intervals. It has zero contracts, scenarios, database reads or
pricing evaluations.

| Operation | Median | Allocated bytes/operation |
| --- | ---: | ---: |
| Date membership | 24.9 ns | 0 |
| Hourly mask membership | 0.9 ns | 0 |
| Intern same reference | 10.1 ns | 0 |
| Intern equal date payload | 216.5 ns | 0 |
| Following-date adjustment | 50.1 ns | 0 |
| Union of two 120-date sets | 1.20 μs | 2,024 |
| Construct ten-year business-date payload | 48.7 μs | 134,176 |
| Construct one-year hourly grid | 26.0 μs | 70,144 |
| Construct one-year hourly mask | 19.8 μs | 1,168 |

Construction measurements create fresh payloads after runtime warm-up; the
`cold_*` labels do not mean process startup or cold JIT. Checksums validate each
measured batch. JSON output includes sample minimum/maximum and environment;
allocation measures use the current thread. Very small timings include harness
effects and are host-specific observations, not service latency budgets.

Ten thousand references shared one date payload. Its raw date array uses 10,436
bytes; the hourly grid and mask use 70,088 and 1,096 bytes. These sizes exclude
object headers, references, names and pool overhead. The first run reported
791,400 managed bytes after GC for the whole benchmark process; this is not
incremental retained payload memory. The platform did not expose process peak
working set, so the report records that field as unavailable. No portfolio,
database, concurrent-throughput or production performance claim follows from
these measurements.
