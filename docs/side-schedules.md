# Whole-term side schedules

Status: local implementation trial; full integration and storage qualification are
in progress. No hosted release is qualified by this record.

D253 uses one `Side` contract for sided instruments. `FixedSide` and
`FloatingSide` support dated swap periods and native PPA intervals. Physical
power, certificate and capacity delivery retain their own behavior families.
Futures remain leaf instruments. Monthly and scheduled financial swaps remain
distinct instrument types, with whole-term shared sides rather than child sides.

`GenerateSchedule()` is parameter-only. Interval sides own concrete delivery
geometry: the exact pinned clock, captured clock rules, cadence and optional
delivery mask. Binding a payment to delivery slots resolves that geometry when
the immutable side is constructed. Missing, cyclic or incompatible geometry is
refused. Valuation checks captured rules against the selected pinned reference
revision; it does not choose a current clock or infer cadence from a slot name.

The retained `ScheduleDefinition` contains scalar and vector pricing operations,
ordered named edges, typed exact-version bindings, exact interval grids, declared
quantity ports and named roots. Native operations act on payment-period columns;
there is no retained arithmetic record per interval. Observation applications,
averaging, normalization, amounts and discount steps remain inspectable through
the operation and its selected cell. Only contribution and measure roots are summed.

`ScheduleRow` is the contribution view over that complete structure. It is a lightweight structural record. It retains a side/payment key,
ordinal, delivery/observation window, payment date, quantity terms or source
reference, and fixed/index/location price references. A generated schedule has
no sampled market values. `ScheduleRowValue` is the common evaluated record for
swaps and PPAs. It carries resolved quantities, prices, amount, discount and PV,
with explicit lifecycle/economic classification where applicable. PPA overdue
cash and holdings keep their existing separate measures; unavailable complete PV
does not become zero.

PPA row references compile to the managed calculation graph. Price edges import
the retained connected scenario, preserving nested input shocks, output offsets,
historical aliases and frozen sources. Quantity edges retain source revisions,
capacity, adjustments, normalization and explicit linked slots. This does not
invent a price-to-generation relationship: a quantity changes only through its
declared underliers. Exact arithmetic and typed units remain unchanged.

The graph stores immutable monthly columns and aggregate roots. Interval cells
do not each allocate a graph node. Plans admit at most 2,000,000 nodes,
8,000,000 edges, 35,136 cells per column and 32,000,000 column cells in total;
exceeding a bound is a structured refusal. Cancellation passes through compilation
and evaluation. Work receipts count evaluated final row amounts, graph nodes and
reused reads separately. The coarse request admission budget remains an upper
bound, rather than being reported as measured work.

The Blotter reads values from the backend. PPA detail requests select one side,
one delivery month and pages of 48 intervals (the API bound remains 128).
Membership coefficients and position quantity are applied on the server.

Ordinary Blotter entry first reads retained booking facts in pages of at most
64 bindings, without loading pricing schedules or market observations. It shows
the booked positions while a separate backend request prices the complete
selected scope. Money totals remain pending until that complete response arrives;
neither an unpriced position nor an unfinished page becomes a zero value.
Further fact pages use the first page's retained context. Working-data pricing
captures current knowledge independently and replaces facts and values together
only after their loaded binding revisions and total count agree. Historical
pricing keeps the selected exact context. A changed scope requires refresh.
Leaving the view cancels its background request; generation checks also reject
late replies. Pricing failure retains the authorized facts with values unavailable.

The metadata directory enables entry and book selection independently of full-book
pricing. A position read selects one exact trade, binding, revision and retained
context; its values stay separate from complete-book totals. Terms and paged side
schedules can open while whole-book valuation is pending or unavailable. Retained
inspection is read-only. Current authorization and input visibility are checked
for every read, including cached schedule reuse. PPA reads isolate other trades
but preserve existing whole-trade sibling admission; they do not silently price
only one binding of an otherwise incompatible PPA trade. The futures product form
still requires its complete priced catalog.

## Stored definitions

The compact trial uses twenty tables for retained schedules and their publication
protocol. Six contain the pricing structure:

| Table | Contents |
| --- | --- |
| `side_schedules` | The immutable schedule header, compiler version and content manifest |
| `side_schedule_rows` | Scalar or vector pricing operations, dates, typed outputs and closed versioned parameters |
| `side_schedule_edges` | Named links from a calculation step to its inputs |
| `side_schedule_bindings` | Exact references to contracts, curves, indices, quantity sources and other supported inputs |
| `side_schedule_roots` | Named outputs, including quantity, contractual cash and side PV |
| `side_schedule_grids` | Exact UTC axes and selected intervals, captured clock references and packed local date/time/occurrence metadata |

The remaining tables retain shared side identity, typed quantity ports and their
composition, private request chunks and seals, publication authority, and the
instrument-to-schedule mapping. They serve booking, safe retry, historical reads
and access control. This physical layout remains subject to storage qualification.
It is common to the supported commodity families; no separate schedule table is
created for gas, power or a cross-commodity instrument.

PostgreSQL remains stock and row-oriented. Arrays inside the grid records provide
compact column storage; the managed engine resolves numerical quantity and price
columns through the existing audited source stores. The side schedule contains
their references and operations, not sampled future prices or quantities. No
columnar extension or second canonical market-data store is introduced.

The only retained schema is `side_schedule_blocks_v2`, generated by
`whole_term_side_blocks_v2`. Native cell views are lazy and identify their physical
root plus cell ordinal. Per-cell obligation identities use their own coordinates
and semantic inputs, independently of a block's ordinal, partition or manifest.
Loading does not expand the superseded scalar-operation DAG. Exact retained month
geometry is indexed once, so quantity preparation does not scan the whole term
for every month.

For example, a floating payment retains a contract lookup, observation
applications, a weighted average, a quantity expression, amount, discount and PV.
The lookup identifies its market input rather than storing today's projected
price. Valuation resolves those references under the selected immutable context
and scenario. A shock changes the resolved input and its dependent values; it
does not update the retained rows. Contractual fixed prices remain literals.

Migrations 0123, 0124 and 0125 require a schema release and rebooking. They refuse a
database containing earlier swap/PPA economic definitions rather than reinterpret
stored trades. Whole-term swap headers own flattened period parameters; PPA
sides pin normalized schedule geometry with exact clock/calendar/TOU relationships.
Economic hash deduplication and idempotent operation replay remain mandatory.
One-period swaps use the same new side definitions with one explicit period.

The shared definitions are `FixedSide_v4` and `FloatingSide_v8`;
the PPA instrument is `ppa_instrument_v2`, and its index price term is
`ppa_index_price_v2` with explicit period settlement selection. PPA-only payment side classes and
monthly/scheduled child-side classes are removed. Existing authored PPAs and
swaps must be rebooked through the authorized API in an isolated release window.
A development trial must retain its previous database separately and rebook under
the new definitions; an application image alone cannot upgrade existing trades.
Production release and rebooking remain separate work.

The cutover removes legacy implementation paths. There are no compatibility
wrappers, dual-format loaders, old-definition fallbacks or legacy booking routes.
Stored definitions are explicitly rebooked under the new contract or retired;
missing contractual terms are never invented to make an old trade load. Private
one-time rebooking tools stay outside the repository. Historical migration files
remain the immutable schema record, and an operator's rollback backup does not
enable an old format in the application.

Booked schedules are loaded through the visible instrument/trade revision. A
missing artifact or unsupported schema/generator refuses direct valuation. Under
D252's report-only policy, the Blotter lists the affected whole trade as **Needs
rebooking**, prices healthy trades and leaves affected complete group totals
unavailable. Corrupt rows, unsupported operations within a supported definition,
missing typed bindings and manifest mismatches remain hard errors. It never
regenerates a booked schedule. Transient drafts use
the same generator and pricing contracts without storing a schedule.

Unpublished chunks are immutable and bound to the actor, command, expected head
and compiler versions. A seal checks complete typed content; a separate final
publication header controls business visibility. Final publication, instrument
bindings and the idempotent result commit together. Child rows inherit the
header's temporal policy. Large-request resume and storage limits require the
qualification below before a supported maximum is claimed.

Booking admits at most 1,500,000 pricing-plan entities: the sum of operations,
edges, bindings, roots, ports and grids across distinct reusable sides. Full-term
geometry and source preparation are admitted before compilation under separate
two-million output-cell and four-million preparation-cell bounds. A short booked
coverage cannot bypass admission of its complete retained term. Mixed native
energy and accredited-capacity terms retain the native preparation cost. The
backend generates bounded compact content once, counts its physical entities and
reports `side_schedule_limit` above that bound. Repeated memberships do not
multiply stored work. These are admission ceilings, not throughput guarantees.
The authoring catalog states this booking limit separately from evaluated-row,
detail-response and transient valuation limits. Long draft valuation benchmarks
do not qualify equally long retained bookings or a complete book's cold load.

Each side owns its normalization basis, policy and exact asset set. Cross-side
quantity input ports have explicit units and captured geometry; the instrument
binds them to a supplying schedule's quantity output set. Definition sharing
shares no trade settlement state. Calculation identities and receipts include
the retained content manifests separately from economic identity.

A snapshot records the explicitly resolved knowledge context separately from its
quote cutoff and exact pins. Interval preparation and the connected price graph
reject a different context or cutoff. Discount factors retain their selected rate
sources, conventions, pillar construction and interpolation as dependencies.
Commodity risk continues to hold rates fixed; internal rate scenario checks do
not enable a public rates-risk product.

The existing-close worker retains its explicit `valuation_instant_v1` source
policy: quantity, shape and actual-price reads stop at the close instant. The
certificate used to read a subsequently registered close can be later. Both
coordinates are retained: the snapshot names the resolved certificate and the
input receipt names the source cutoff and policy. Ordinary valuation requires
the resolved cutoff; the close policy requires the exact valuation instant,
never a source cutoff later than its certificate. Neither policy changes quote
pins or substitutes a different historical context.

## Qualification

The frozen baseline fixture contains 205 valuations and 138,830 evaluated rows
from source baseline `e6c62d1`. It compares exact instrument/side PV and row
windows, quantities, prices, amounts, factors and PV under base and shocks.
It covers swaps, forwards, heat rates, PPA fixed/indexed payments, capacity,
certificates, overdue cash, DST and mixed books. Routine checks read the fixture;
they never regenerate expected values from the new implementation.

Financial benchmark checks independently count calendar hours and selected
blocks, verify exact measured work, and compare selected shocks with fresh full
repricing. Performance measurements include preparation and valuation, with
fixture construction excluded. They qualify the named synthetic workloads only;
database/HTTP throughput and deployment capacity require separate measurement.

A superseded expanded-format storage baseline used PostgreSQL 16.14, SDK 10.0.402 and macOS
Arm64, with a 3 GiB resident-memory target. Its largest single instrument had two
quarter-hour volume sides over twelve months: 35,136 intervals per side,
456,771 logical rows, 562,176 edges, 140,544 bindings, 210,818 roots and one port.
It published in two bounded staging attempts taking 34.71 seconds in total; the
final transaction took 0.188 seconds. Cold typed loading took 3.503 seconds and
managed attachment 1.864 seconds, with sampled peak RSS of 1.449 GB. These are
single samples with zero scenarios, not a complete-book latency qualification.
The layout uses bounded typed reads and indexes selected for ordinal access;
partitioning has not been qualified by this trial.

The compact layout is separately exercised by the [payment-block
experiment](side-block-experiment.md) and full authorized HTTP checks. A fresh
20-year hourly, five-side booking now completes in 4.42 seconds; its exact base PV
80,803,200 returns in 10.78 seconds, including authorization, context resolution,
input reads, preparation, valuation and response handling. It evaluates 876,600
row amounts and 14,181 graph nodes. This single Debug API sample uses PostgreSQL
16.14 and SDK 10.0.402 on macOS arm64. Source preparation takes 6.25 seconds,
admission/definition reads 1.30 seconds and valuation/construction 3.22 seconds.
Sampled API RSS reaches 2.91 GB; process-wide allocation is 5.45 GB and is not
resident memory.

That booking adds 1,200 grids, 8,888 operations, 10,800 edges, 2,160 bindings and
3,605 roots to the six measured schedule-content tables: 26,658 rows including
schedule headers. The measured shared-table size increase is 14,213,120 bytes,
including TOAST and indexes; it is not an attributed per-instrument footprint. Source
series and publication/port protocol tables are excluded from that byte count.
The exact geometry validation checks complete matching columns directly, with
full cell validation for split or ambiguous grids; it does not relax eligibility.

A ten-year quarter-hour, five-side booking also succeeds in 4.48 seconds, storing
600 grids and 4,448 operations. Its Debug API preview still exceeds the unchanged
20-second deadline and samples 3.80 GB RSS. That valuation and a 3 GiB deployment
of that workload remain unqualified. A separately measured Release API prices
the same explicit trade to exact PV 40,408,960 in 15.55 seconds, including source
reads and response handling, but samples 4.23 GB RSS. That establishes its timing
on the uncapped local process, not compliance with a 3 GiB deployment limit.
Combining the two large benchmark trades in
one complete-book request exceeds the explicit evaluated-row budget and is
refused; a partial total is not returned. These stress fixtures are distinct from
ordinary functional/browser qualification and the actual development dataset.

Earlier managed-only, pre-compact-layout Release samples used SDK 10.0.402,
runtime 10.0.13, macOS 26.6.2,
Arm64 and 14 logical processors. Both workloads use one distinct five-side
instrument, exact synthetic prices and no returned interval detail. Each sample
runs in a fresh process, with no explicit warmup. Allocation is cumulative managed
allocation; working set is sampled after assertions and is **not** peak memory.
The runtime did not expose an OS peak for these samples. GB/MB below are decimal.

| Workload | Baseline elapsed | Graph elapsed | Baseline allocation | Graph allocation | Baseline working set | Graph working set |
|---|---:|---:|---:|---:|---:|---:|
| 20 years hourly, one binding/book, 720 unique contracts, one scenario (two samples each) | 4.94–5.77 s | 8.80–8.93 s | 4.69–4.72 GB | 5.92–5.93 GB | 675–753 MB | 1,095–1,098 MB |
| One year hourly, 100 bindings, five books, 36 unique contracts, 24 factors / 49 scenarios (one sample each) | 3.646 s | 1.376 s | 4.353 GB | 0.484 GB | 143 MB | 154 MB |

These samples do not measure the current HTTP or retained-storage path. The base
workload evaluates 876,600 row amounts in both implementations; its
graph evaluates 13,921 schedule nodes. The risk workload reduces evaluated row
amounts from 2,152,080 to 79,056, with 1,129 evaluated schedule nodes and 8,928
reused reads. Exact financial fingerprints match for the base value and all 845
risk base/up/down/PnL/delta/gamma values. These samples demonstrate scenario reuse
and a cold-construction regression. Reducing initial preparation/graph allocation
remains work; a different implementation language has not been benchmarked.

The retained-schedule recovery gate uses one twelve-month, quarter-hour volume
and fixed-payment instrument, one binding and one book: 35,136 intervals per side,
24 unique market contracts and 24 factors across 49 scenarios. Its independent
oracle verifies base PV 3,328,000 and every monthly peak/off-peak delta, gamma and
signed scenario value. Admission declares 70,272 rows and 3,443,328 conservative
row-scenarios; measured work is 210,816 amount computations, 805 evaluated schedule
nodes and 3,744 reused reads.

The local check used a Debug API, Release worker, SDK 10.0.402, PostgreSQL 16.14
and macOS 26.6.2 Arm64 with 14 logical processors. A replacement completed real
preparation and risk in 9.314 seconds with five observed renewals of its five-second
lease. Renewal failure, cancellation and authority loss during calculation left
no report, with database transactions already released; a five-second overall
deadline stopped cold preparation. Killing a worker after a preparation renewal,
waiting for natural lease expiry, changing the forecast after the pinned cutoff,
and dropping the replacement's COMMIT acknowledgement still produced one exact
report. Old ownership, repeated completion and later cancellation preserved that
single outcome. These checks qualify recovery for this workload; they do not
measure peak memory or deployment capacity. The older D201 ten-year, five-side
timing remains historical evidence and does not qualify booking that workload
under the current retained-plan entity bound.

PPA booking in New trade and retiring the PPA booking page remain separate work.
One-time seed and development-release tooling remains private; its execution is
qualified separately from the public synthetic checks.
