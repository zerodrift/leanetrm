# Connected graph kernel benchmark

Measured on 2026-10-21 with `ConnectedGraphBenchmark` and report schema
`connected_graph_kernel_benchmark_v1`. Three fresh Release processes completed
the independent arithmetic checks at every size. The largest plan had
5,000,001 nodes and 5,000,000 edges. Its median base evaluation was 106.8675 ms;
a broad source shift required 22.5180 ms to construct the affected overlay and
217.5109 ms to evaluate it. A sparse output shift evaluated one node.

These are checked `Int64` graph operations with **zero financial instruments or
unique instrument pricers**. They do not qualify the rich curve compiler, exact
rational pricing, complex derivatives, database loading, authorization, durable
workers, distributed execution or hosted throughput at this scale. See the
[connected calculation contract](connected-calculation.md) for the implemented
financial integration and its supported boundaries.

## Environment and protocol

The environment was macOS 26.6.2, Arm64, 14 reported logical processors,
.NET SDK 10.0.402 and .NET runtime 10.0.13. The executable was built in Release,
used workstation GC and ran with `DOTNET_TieredCompilation=0`; no
`DOTNET_ReadyToRun` override was set.

Each process performed one small warm-up, followed by one observation of every
phase in the same workload order. Tables report the median of the three process
observations, rather than three repetitions sharing the same value caches.
Disabling tiered compilation does not remove all first-use runtime and JIT costs.
The small sparse timings, especially at the first workload size, should not be
used to infer a latency bound or a size-dependent speedup.

Compilation includes edge enumeration, admission checks, sorting, cycle
validation and construction of forward/reverse compressed adjacency arrays.
Session creation allocates the base value storage. Evaluation includes output
traversal and checksum accumulation. Each requested output is checked against
an independent formula outside timing. Checksums alone are not the oracle.
Forced collections used for memory samples also run outside timed intervals.
The cumulative work counters include validation/isolation reads and later
descendant accesses to parents; they are not counters for individual timed
phases.

## Workloads and evaluated work

For the shared-source workload, let `p = edges / 2`. One source has value 11,
`p` separate leaves have values `i % 13 - 6`, and each of `p` output nodes adds
the shared source to its own leaf. Thus there are `2p + 1` nodes, `2p` edges and
`p` requested outputs. All prices and calculations here are synthetic integers.

| Edges | Nodes | Requested outputs | Base nodes evaluated | Sparse nodes evaluated | Broad nodes evaluated |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 100,000 | 100,001 | 50,000 | 100,001 | 1 | 50,001 |
| 1,000,000 | 1,000,001 | 500,000 | 1,000,001 | 1 | 500,001 |
| 5,000,000 | 5,000,001 | 2,500,000 | 5,000,001 | 1 | 2,500,001 |

Each size retains six sessions: the base, a sparse child adding 5 to one output,
its nested child adding 2 to that output's leaf, a broad child adding 2 to the
shared source, and two independent base contexts with source values 17 and 23.
The nested child evaluates two nodes and preserves the inherited output offset.
The broad child traverses `p` reverse edges and affects `p + 1` nodes. Each of
the two concurrent contexts evaluates all `2p + 1` nodes independently while
sharing the immutable plan. These contexts test value isolation, not historical
data resolution or authorization.

Additional shapes exercise depth and shared dependencies:

| Shape | Nodes | Edges | Outputs | Base nodes evaluated | Shifted nodes evaluated |
| --- | ---: | ---: | ---: | ---: | ---: |
| Deep chain | 100,000 | 99,999 | 1 | 100,000 | 100,000 |
| 25,000 disjoint diamonds | 100,000 | 100,000 | 25,000 | 100,000 | 4 |

The chain has one base and one broad child; the last value is independently
checked as chain length, then length plus 2. Each diamond has one source, two
branches and one output. Its independent output is 5; shifting one diamond's
source by 1 gives 7, evaluating its shared source only once. The diamond workload
has one base and one sparse child.

## Median phase times

All times below are milliseconds. Overlay creation and evaluation are separate:
discovering a broad affected closure is real work even before any output is read.

| Shared-source phase | 100,000 edges | 1,000,000 edges | 5,000,000 edges |
| --- | ---: | ---: | ---: |
| Compile plan | 1.7216 | 17.6162 | 93.0650 |
| Create base session | 0.0263 | 0.0319 | 0.1619 |
| Evaluate base outputs | 2.1494 | 20.8575 | 106.8675 |
| Create sparse overlay | 0.0692 | 0.0036 | 0.0049 |
| Evaluate sparse output | 0.0260 | 0.0011 | 0.0012 |
| Create nested overlay | 0.0848 | 0.0012 | 0.0012 |
| Evaluate nested output | 0.0390 | 0.0008 | 0.0008 |
| Create broad overlay | 0.4883 | 4.1578 | 22.5180 |
| Evaluate broad outputs | 3.9855 | 43.6268 | 217.5109 |
| Create two context sessions | 0.0660 | 0.0460 | 0.0714 |
| Evaluate both contexts concurrently | 8.8298 | 23.6930 | 120.8880 |

At five million edges, the three base observations ranged from 105.8890 to
113.1136 ms; broad evaluation ranged from 214.7221 to 223.7302 ms. Three local
observations do not establish latency percentiles or service capacity.

| Additional shape / phase | Median ms | Median allocated bytes |
| --- | ---: | ---: |
| Chain: compile | 2.2367 | 4,904,376 |
| Chain: create base session | 0.1949 | 812,856 |
| Chain: evaluate base | 1.4433 | 2,097,536 |
| Chain: create broad overlay | 1.0146 | 4,831,168 |
| Chain: evaluate broad child | 2.5868 | 10,549,808 |
| Diamonds: compile | 1.5940 | 4,897,840 |
| Diamonds: create base session | 0.0883 | 812,856 |
| Diamonds: evaluate base | 1.7225 | 2,200,000 |
| Diamonds: create sparse overlay | 0.0495 | 1,176 |
| Diamonds: evaluate sparse child | 0.0267 | 472 |

## Allocations and memory

Allocated bytes are managed allocations during a phase across the entire
process, including parallel workers. They include temporary compilation arrays,
evaluation stacks and broad-scenario hash/dictionary storage. They are neither
retained memory nor peak resident memory.

| Shared-source phase | 100,000 edges: bytes | 1,000,000 edges: bytes | 5,000,000 edges: bytes |
| --- | ---: | ---: | ---: |
| Compile plan | 4,897,848 | 44,779,184 | 274,228,464 |
| Create base session | 812,896 | 8,125,400 | 40,625,400 |
| Evaluate base outputs | 4,400,000 | 44,000,000 | 220,000,000 |
| Create sparse overlay | 1,008 | 1,008 | 1,008 |
| Evaluate sparse output | 224 | 224 | 224 |
| Create nested overlay | 1,016 | 1,016 | 1,016 |
| Evaluate nested output | 224 | 224 | 224 |
| Create broad overlay | 2,852,712 | 24,982,376 | 122,958,880 |
| Evaluate broad outputs | 8,471,984 | 80,376,104 | 376,454,544 |
| Create two context sessions | 1,625,832 | 16,250,840 | 81,250,840 |
| Evaluate both contexts concurrently | 8,854,168 | 88,002,272 | 440,002,208 |

The following numbers are medians of each process's highest end-of-phase sample
for the workload. Managed samples follow a forced collection. They include all
retained sessions and reporting state, and do not capture transient peaks within
a phase. A later small workload's working set can include pages retained by the
runtime after the earlier large workload. One MiB is 1,048,576 bytes.

| Workload | Highest sampled retained managed MiB | Highest sampled working set MiB |
| --- | ---: | ---: |
| Shared source: 100,000 edges, six sessions | 7.613 | 69.344 |
| Shared source: 1,000,000 edges, six sessions | 70.741 | 139.281 |
| Shared source: 5,000,000 edges, six sessions | 333.194 | 462.688 |
| Chain: two sessions | 9.460 | 165.625 |
| Diamonds: two sessions | 3.078 | 162.734 |

The managed process API did not supply a lifetime peak on this platform, so the
JSON field is null. The external macOS `/usr/bin/time -l` measurement reported
maximum resident set sizes of 485,408,768, 485,277,696 and 485,490,688 bytes.
That is a process-lifetime range of 462.797–463.000 MiB, with a median of
462.922 MiB. These peaks cover the whole executable and every workload, not
one graph or one scenario.

Sparse child storage does not scale with total node count in these measurements.
Broad shifts still allocate and evaluate in proportion to their affected
closure. Base value arrays are proportional to plan size for each independent
context. The rich financial compiler additionally retains typed definitions,
exact arithmetic inputs and manifest evidence; its memory and performance at
five million edges have **not** been qualified by this benchmark.

## Reproduce

Run from the repository root with the SDK selected by `global.json`. Keep JSON
reports and operating-system logs outside the repository. On macOS:

```sh
dotnet restore tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj --locked-mode
dotnet build tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj -c Release --no-restore

for sample in 1 2 3; do
  /usr/bin/time -l env DOTNET_TieredCompilation=0 \
    dotnet exec tests/OpenEtrm.MarketData.Checks/bin/Release/net10.0/OpenEtrm.MarketData.Checks.dll \
    --connected-graph-benchmark "/tmp/connected-graph-${sample}.json" \
    > "/tmp/connected-graph-${sample}.log" 2>&1
done
```

Each invocation performs its own warm-up and validates every workload before
writing its report. Compare phases by workload name and edge count, taking the
median of each metric across the three reports. The macOS maximum-resident-set
size above is in bytes; other operating-system tools may use different units.
Record the runtime, GC and compilation settings when comparing another machine.
