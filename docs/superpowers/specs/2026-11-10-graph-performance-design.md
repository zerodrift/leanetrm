# Graph and valuation performance: first round

Status: implemented 2026-11-10. Workloads and values are synthetic.

## Why

The connected calculation graph (`OpenEtrm.Shared.Calculation`) is not where
valuation time goes today. The existing 20-year hourly PPA benchmark
(`--ppa-benchmark-sample`) measured, before this change:

| Run | Time | Managed allocation | Graph nodes evaluated |
| --- | ---: | ---: | ---: |
| `hourly_20y_unit`, one valuation | 6.5 s | 4.1 GB | 14,161 |
| `hourly_20y_shared`, 961 scenarios | 15.6 s | 14.8 GB | 22,801 |

The kernel spends about 30 ns per node, so 14,161 nodes cost under a
millisecond. The largest single cause found in the 961-scenario run was that
`Instrument.EconomicIdentity` rebuilt the instrument's canonical text and
SHA-256 digest on every read, and one scenario read it about thirteen times for
the same immutable instrument.

The kernel itself had three avoidable costs that matter for plans with millions
of nodes: a list allocated on every read, hash-based storage for every scenario
overlay, and a comparison sort per node at compilation.

## Goals

1. An instrument derives its economic identity once per instance.
2. The graph kernel evaluates and creates scenarios with less work and memory,
   with identical results, counters and errors.
3. The kernel benchmark record states the new measurements.

## What does not change

- Identity digests, canonical text, manifest ids, numerical results, work
  counters, error codes and error messages.
- The public signatures of `DependencyPlan`, `GraphSession<T>`,
  `GraphInputs<T>`, `GraphWork` and `Instrument`.
- Locking in `GraphSession<T>`. Every read still passes the session gate.
- `DefinitionIdentity.For` itself, and identities of types other than
  `Instrument`.
- Runtime, garbage-collector and compilation-mode settings, dependencies,
  tables, configuration values and migrations.

## Economic identity

`Instrument.EconomicIdentity` is derived on first read and retained for that
instance.

- Every `[IdentityField]` property in `src` is get-only or init-only and has an
  immutable type, so an instance's identity cannot change after construction.
- Instruments are copied with `with`. A copy duplicates every instance field,
  so an identity held in an instance field would describe the original's terms.
  The retained identity is therefore keyed by object reference and lives
  outside the instance, in a `ConditionalWeakTable`. That table compares keys
  by reference and does not keep an instrument alive.

The check "an instrument derives its economic identity once and a copy derives
its own" in `TradingIdentityChecks` covers both properties.

## Graph kernel

1. **Adjacency without a comparison sort** (`DependencyPlan.Compile`). Edges
   are first grouped by dependency in declaration order. Walking dependencies
   in ascending order then fills each node's dependency list in ascending
   order, and walking nodes in ascending order refills each dependent list in
   ascending order. A repeated edge is two equal neighbours in a dependency
   list and is rejected with the existing `graph_edge` error.
2. **One evaluation stack per session** (`GraphSession<T>.Get`). The explicit
   stack is an array the session keeps between reads. It leaves the session
   for the duration of a read and returns when the read ends, unless it grew
   beyond 4,096 frames. A nested read on the same thread therefore finds no
   stack and takes its own. Growth is capped at `Array.MaxLength`.
3. **One storage layout for base frames and scenarios.** Values live in pages
   of 256 nodes, reached through chunks of 256 pages, both allocated on first
   use. A scenario's pages also carry its affected marks. The set of affected
   nodes and the dictionary of changed values are gone; `Advance` and `Rebind`
   keep their existing hash-based closure.
4. **The layer walk runs only where a layer shifts.** When a scenario is
   created, each of its recomputed nodes that any layer offsets is marked.
   Evaluation applies the per-layer offsets only to marked nodes, in the same
   layer order as before.

Invariants:

- Every node a scenario evaluates is in its affected set, so its page exists.
- A scenario's pages are never shared, and a base frame's pages carry no
  affected or shifted marks.
- `Inherits(node)` is true exactly when the session has a parent and the node is
  not in this scenario's affected set.
- A sparse scenario's storage does not grow with the plan's node count beyond
  one chunk-table slot (8 bytes) per 65,536 nodes.
- Locking, the `evaluating` guard, cancellation points on every loop, and the
  order in which values become visible are unchanged.
- A read never shares its evaluation stack with another read in progress on
  the same session, including a nested read on the same thread. A scenario
  that is reading an inherited value from its parent can be read again on the
  same thread when the parent's evaluator holds that scenario; the session
  gate admits the same thread again.

Six checks in `DependencyPlanChecks` cover these:

- adjacency is ascending for any declaration order and a repeated edge rejects
  wherever it appears;
- nested scenarios stay exact on both sides of every storage block boundary;
- a failed deep evaluation leaves nothing behind for the next read;
- repeated output reads do not allocate a stack per read;
- a broad scenario allocates by touched storage block, not per affected node;
- a nested read of a scenario during its own inherited read keeps both
  evaluations intact.

The two allocation checks hold in both build configurations. An unoptimized
build boxes each `long` result in the kernel's existing null check, 24 bytes
per evaluated node, with the previous kernel as well. Measured bytes:

| Check | Previous, Release | Previous, Debug | Now, Release | Now, Debug | Limit |
| --- | ---: | ---: | ---: | ---: | ---: |
| 19,999 output reads | 1,929,016 | 2,408,992 | about 170,000 | about 650,000 | 1,000,000 |
| 100,000-output scenario | 23,132,424 | 25,532,472 | about 1,930,000 | about 4,330,000 | 8,000,000 |

## Evidence

Measured 2026-11-10 on macOS 26.6.2, Arm64, 14 logical processors, .NET 10.0.13,
Release, on a machine shared with other work. Timings vary by about 10% between
runs; allocation figures are stable.

`hourly_20y_shared`, single samples. The reported `work` is identical in all
four runs, and `baseValues` is identical once the book, catalog and revision
identifiers the fixture creates anew in each process are set aside.

| Run | Milliseconds | Allocated bytes |
| --- | ---: | ---: |
| Before | 16,149 | 14,797,168,656 |
| Identity derived once | 12,529 | 11,602,960,584 |
| Kernel changes as well | 11,528 | 11,611,473,024 |

Kernel, `shared_source_fanout` with 5,000,000 edges: medians of three fresh
processes each with `DOTNET_TieredCompilation=0`.

| Phase | Before ms | After ms | Before bytes | After bytes |
| --- | ---: | ---: | ---: | ---: |
| Compile plan | 100.07 | 71.22 | 274,228,464 | 274,228,464 |
| Evaluate base outputs | 139.38 | 108.67 | 262,347,464 | 42,824,360 |
| Create sparse overlay | 0.01 | 0.01 | 1,088 | 3,728 |
| Evaluate sparse output | 0.00 | 0.00 | 224 | 2,160 |
| Create broad overlay | 23.57 | 19.62 | 122,958,960 | 35,280,272 |
| Evaluate broad outputs | 228.98 | 122.06 | 376,454,544 | 20,239,384 |
| Evaluate both contexts concurrently | 166.62 | 130.07 | 524,694,792 | 85,646,024 |

Sparse scenarios allocate a few kilobytes more than before, because a
scenario's first write to a block allocates a 256-slot page. The full record,
measured separately under its own protocol, is the
[connected graph kernel benchmark](../../connected-graph-benchmark.md).

This is a kernel measurement of checked integer operations. It does not qualify
financial compilation, exact pricing, database loading or hosted throughput.

## Later rounds (not part of this change)

These came out of the same profile. Their shares are sampling estimates, not
measured savings, and each needs its own design and review before any work.

| Candidate | Where | Share observed |
| --- | --- | --- |
| Per-scenario work that covers the whole instrument although one month changed: coverage validation, discount-factor sets, period-summary scaling, scenario market materialization | `IntervalValuationSession`, `IntervalValuationInputs`, `ValueMeasures`, `CurveModel.WithShifts` | most of the scenario phase; graph evaluation itself is about 7% |
| Allocation in exact arithmetic: each value is a heap object over two big integers, and every operation reduces by a greatest common divisor | `SignedRatio`, `ExactMoney`, `ExactQuantity`, `ExactUnitPrice` | 25–32% of samples in collection and allocation |
| Schedule-graph compilation: one serialized object and digest per node, grid digests, grouping | `ScheduleGraphPlan`, `PpaScheduleGraph.Compile` | about 36% of a single valuation |
| Preparation: distinct sets over records with deep generated hash codes | `IntervalValuationInputs` constructor | about 30% of a single valuation |

A read path that skips the session gate was measured as an upper bound by
removing the gate entirely: at most 7% on base evaluation and 25% on a broad
scenario. It would weaken the guard against undeclared dependency reads and is
not proposed.

Compilation mode and collector settings were also measured on the 961-scenario
run and are not part of this round. Disabling tiered compilation or dynamic
profile-guided optimization changed the time by less than run-to-run variation.
The server collector was about 10% faster in elapsed time while using twice the
memory and more processor time. A 256 MB first-generation budget was about 60%
slower.
