# Connected graph kernel benchmark

Measured on 2026-11-10 with `ConnectedGraphBenchmark` and report schema
`connected_graph_kernel_benchmark_v1`. Three fresh Release processes completed
the independent arithmetic checks at every size. The largest plan had
5,000,001 nodes and 5,000,000 edges. Its median base evaluation was 102.1425 ms;
a broad source shift required 18.1849 ms to construct the affected overlay and
119.2079 ms to evaluate it. A sparse output shift evaluated one node.

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
Session creation allocates only the top-level page table; value pages are
allocated as nodes are evaluated. Evaluation includes output
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
| Compile plan | 1.2874 | 11.9002 | 70.2042 |
| Create base session | 0.0226 | 0.0008 | 0.0007 |
| Evaluate base outputs | 2.0330 | 20.7155 | 102.1425 |
| Create sparse overlay | 0.0681 | 0.0060 | 0.0052 |
| Evaluate sparse output | 0.0251 | 0.0018 | 0.0016 |
| Create nested overlay | 0.0459 | 0.0017 | 0.0043 |
| Evaluate nested output | 0.0214 | 0.0027 | 0.0022 |
| Create broad overlay | 0.3727 | 3.4481 | 18.1849 |
| Evaluate broad outputs | 2.2686 | 23.1513 | 119.2079 |
| Create two context sessions | 0.0798 | 0.0010 | 0.0033 |
| Evaluate both contexts concurrently | 8.6745 | 22.4120 | 123.5262 |

At five million edges, the three base observations ranged from 101.6034 to
104.8323 ms; broad evaluation ranged from 117.3870 to 121.8036 ms. Three local
observations do not establish latency percentiles or service capacity.

| Additional shape / phase | Median ms | Median allocated bytes |
| --- | ---: | ---: |
| Chain: compile | 2.1591 | 4,904,376 |
| Chain: create base session | 0.2022 | 352 |
| Chain: evaluate base | 1.5710 | 2,955,536 |
| Chain: create broad overlay | 0.6395 | 70,720 |
| Chain: evaluate broad child | 1.4905 | 2,907,600 |
| Diamonds: compile | 1.3512 | 4,897,840 |
| Diamonds: create base session | 0.0915 | 352 |
| Diamonds: evaluate base | 1.8718 | 858,176 |
| Diamonds: create sparse overlay | 0.0497 | 3,128 |
| Diamonds: evaluate sparse child | 0.0268 | 2,160 |

## Allocations and memory

Allocated bytes are managed allocations during a phase across the entire
process, including parallel workers. They include temporary compilation arrays,
evaluation stacks and paged scenario storage. They are neither
retained memory nor peak resident memory.

| Shared-source phase | 100,000 edges: bytes | 1,000,000 edges: bytes | 5,000,000 edges: bytes |
| --- | ---: | ---: | ---: |
| Compile plan | 4,897,848 | 44,779,808 | 274,228,464 |
| Create base session | 384 | 496 | 984 |
| Evaluate base outputs | 858,176 | 8,566,128 | 42,821,680 |
| Create sparse overlay | 3,128 | 3,240 | 3,728 |
| Evaluate sparse output | 2,160 | 2,160 | 2,160 |
| Create nested overlay | 5,432 | 5,544 | 6,032 |
| Evaluate nested output | 4,232 | 4,232 | 4,232 |
| Create broad overlay | 562,720 | 4,544,840 | 35,280,272 |
| Evaluate broad outputs | 408,272 | 4,050,848 | 20,239,384 |
| Create two context sessions | 808 | 1,032 | 2,008 |
| Evaluate both contexts concurrently | 1,763,784 | 17,134,464 | 85,643,712 |

The following numbers are medians of each process's highest end-of-phase sample
for the workload. Managed samples follow a forced collection. They include all
retained sessions and reporting state, and do not capture transient peaks within
a phase. A later small workload's working set can include pages retained by the
runtime after the earlier large workload. One MiB is 1,048,576 bytes.

| Workload | Highest sampled retained managed MiB | Highest sampled working set MiB |
| --- | ---: | ---: |
| Shared source: 100,000 edges, six sessions | 8.487 | 71.188 |
| Shared source: 1,000,000 edges, six sessions | 79.823 | 133.344 |
| Shared source: 5,000,000 edges, six sessions | 394.660 | 402.453 |
| Chain: two sessions | 5.730 | 274.656 |
| Diamonds: two sessions | 3.811 | 274.125 |

The managed process API did not supply a lifetime peak on this platform, so the
JSON field is null. The external macOS `/usr/bin/time -l` measurement reported
maximum resident set sizes of 421,986,304, 423,936,000 and 422,264,832 bytes.
That is a process-lifetime range of 402.438–404.297 MiB, with a median of
402.703 MiB. These peaks cover the whole executable and every workload, not
one graph or one scenario.

Sparse child storage follows the 256-node blocks a scenario touches. Beyond
that it grows by one eight-byte table slot per 65,536 plan nodes: 3,128, 3,240
and 3,728 bytes at the three sizes here.
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
