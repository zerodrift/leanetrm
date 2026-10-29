# Payment-block storage experiment

Status: backend-only local experiment. This tests a candidate physical layout for
D258 before a production migration, booking cutover or UI integration. The
existing retained-row format remains the application format during this test.
D258 subsequently accepts compact vector retention for the local application
trial; integration and release qualification remain separate from these results.

## Representation

A block describes one payment occurrence's calculation over an exact delivery
grid and explicit interval selection. It references quantity and price columns,
the price multiplier and adder, the quote multiplier, and a payment date. Sources
retain exact version keys. Market samples and resolved discount factors belong
to a selected calculation context, separately from the retained block.

The common energy calculation is:

```text
amount = quote multiplier × Σ selected q_MWh[i] × (multiplier × price[i] + adder)
PV = amount × discount(payment date)
```

There is no additional duration factor when `q` already represents MWh. A rate
or profile source first integrates its declared capacity and exact grid duration
into energy. A one-period swap uses the same calculation with one selected cell;
the interval represents the contractual delivery bucket, not an assumption that
its duration is an hour.

Instrument membership coefficients and external trade quantities remain outside
these side definitions. The workload contains two independent sides and compares
their separate PVs and unsigned aggregate; it does not conceal a pay coefficient
inside a reusable side.

Grouping follows payment occurrences, units and operation semantics. Separate
payment dates, currencies or discount rules remain separate. A physical storage
chunk can cover part of a payment, but its partial sum is not a new obligation.
An irregular selection must retain its exact interval boundaries. Calendar days
are not a universal minimum economic granularity.

## Five experimental tables

The disposable schema is intentionally smaller than the complete production
publication and authorization model:

| Table | Contents |
| --- | --- |
| `contexts` | The explicit valuation instant and resolved knowledge cutoff |
| `source_definitions` | Exact source identity/version, kind, unit and UTC grid, including explicit scalar broadcasting |
| `source_columns` | Context-selected samples in aligned integer numerator and denominator arrays |
| `payment_blocks` | Side and occurrence identity, dates, typed source foreign keys and calculation terms |
| `discount_factors` | Context-selected factors for the payment occurrences |

PostgreSQL arrays provide a concrete column layout without a columnar database
extension. Numerators and denominators are integer `numeric[]` values; division
is performed exactly by the managed evaluator, not rounded into a SQL decimal.
This trial does not choose between arrays and a future versioned binary codec.
It also does not establish the final number of production tables: publication,
trade-revision mappings, source bindings, ports and safe booking remain required.

The application's integration reuses its audited price and quantity source stores.
The experimental `source_columns` table stands in for already selected calculation
inputs; it is not a proposed second canonical market-data store. Retained schedule
grids hold contractual geometry; literals belong to closed operation parameters.
Bindings resolve numerical
columns from the existing sources under the selected context.

Writes retry as no-ops when the complete facts match. Conflicting facts are
refused, and stored blocks cannot be updated. The launcher creates its own
PostgreSQL cluster with a unique Unix socket and no TCP listener, verifies its
data directory, and stops and removes it afterwards. It cannot target an existing
development or production database.

## Financial invariants

- Normalization retains its declared denominator and scope. A partial selection
  cannot derive a replacement denominator from its selected cells. D195's asset
  normalization uses capacity in force for each interval; this may be a column,
  rather than one monthly denominator. The experimental literal divisor is an
  arithmetic test, not a replacement normalization policy.
- Shaping retains its original calibration window. Additive shaping includes its
  adjustment. A correction outside a payment selection can still change that
  selection through calibration.
- Price, quantity, shape and discount changes belong to immutable calculation
  inputs and scenarios. They do not update the retained payment blocks. A compiled
  coefficient is reusable only while all its dependencies and partitions agree.
- Forward-bucket reduction is a separately qualified affine optimization. For
  `p = A f + b`, the payment is `qᵀ A f + qᵀ b`. Shape or quantity shocks can
  invalidate these coefficients; nonlinear terms require their model evaluator.
  This experiment initially measures aligned columns, not that reduction.
- Exact rational quantities remain rational throughout the calculation. The
  existing decimal-input `IntervalData.Dot` is not evidence that arbitrary
  rational normalization can be converted into working decimals without loss.
- The experiment does not choose fixings, roll contracts, settle obligations or
  infer dependencies. Production eligibility and temporal rules remain required.

## Running the experiment

Run from this worktree, with the installed PostgreSQL and .NET paths:

```sh
python3 tools/experiment_side_blocks.py \
  --pg-bin /path/to/postgresql/bin --dotnet /path/to/dotnet \
  --months 12 --output /tmp/side-blocks-12-months.json
```

`--months 120` exercises a longer term. The managed check executable is built in
Release mode. Synthetic blocks are exported, stored and loaded from SQL, then
evaluated in the managed backend and compared with the existing schedule graph's
arithmetic operators. The comparison does not invoke the complete PPA compiler,
market-admission path or fixing selector.
Small independent fixtures check exact results and explicit refusals. No browser
or application server is involved.

Reports separate storage, load/JSON transfer, managed preparation and numerical
evaluation. They name intervals, blocks, distinct sides, scenarios, evaluated work,
allocations and memory scope. A PostgreSQL restart clears shared buffers but does
not flush the operating system's file cache; this must not be called a fully cold
disk benchmark.

The result qualifies only the measured synthetic storage and arithmetic paths.
Production booking, current authorization, historical visibility, lifecycle
measures, complete fixing selection, cancellation and concurrent service load
still require integration and independent review. A smaller SQL row count alone
does not prove that an application deadline will be met.

## Measured local result

One final sample per workload on macOS 26.6.2 arm64, 14 logical processors,
PostgreSQL 16.14, SDK 10.0.402/runtime 10.0.13, Release, workstation GC:

| Measurement | One year | Ten years |
| --- | ---: | ---: |
| Independent side definitions | 2 | 2 |
| Payment blocks | 24 | 240 |
| Exact source versions | 36 | 360 |
| Interval contributions per scenario | 70,080 | 701,184 |
| Scenarios | 7 | 7 |
| All five tables and indexes, bytes | 540,672 | 3,497,984 |
| Fixture storage, seconds | 0.203 | 3.795 |
| SQL load and JSON transfer, seconds | 0.066 | 0.449 |
| Managed decode and common-denominator preparation, milliseconds | 82.3 | 392.4 |
| Base payment-block arithmetic, milliseconds | 10.2 | 14.3 |
| Child-scenario arithmetic range, milliseconds | 0.53–2.31 | 3.86–6.95 |
| Base arithmetic allocations, bytes | 48,112 | 431,792 |
| Decode/preparation allocations, bytes | 21,402,072 | 233,474,784 |
| Whole evaluator process peak resident bytes | 194,461,696 | 1,115,947,008 |

Both workloads preserve every input exactly through SQL and match every payment,
side and aggregate PV against the existing graph operators under base, price
up/down, quantity up/down and discount-factor up/down scenarios. The independent
small fixtures add 24 hand-calculated scenario values, eight structured refusals,
explicit scalar broadcasting and a separate full-window shaping primitive with a
correction outside the payment selection. That primitive feeds supplied columns
to the kernel; it does not qualify retained source-construction integration.
Database checks cover identical and conflicting retries,
immutability, missing references, missing/null grid fields, zero denominators and
truncated columns. All passed.

These are single-process samples, not latency percentiles. The base arithmetic
sample includes first-use JIT effects. Source values repeat small rational patterns
and compress well; the storage result does not predict the compression or cost of
wide, less regular real inputs. The SQL measurement excludes application
authorization and production source preparation. The graph comparison freshly
compiles each scenario and constructs a quantity column per block, including
shared sources; it is not a matched benchmark of production scenario reuse.

Resident memory was measured by the operating system for the entire managed
evaluation process, including decoding, both kernels, all seven fresh graph
scenarios and JIT. It is not the compact kernel's peak memory. Runtime
`PeakWorkingSet64` was unavailable on this host and is reported as null rather than
zero. Allocation counters are cumulative allocations within each named phase,
not resident memory. These results justify implementing and measuring the compact
application path; they do not qualify that path before integration.

## D258 implementation boundary

An adopted layout must retain every logical calculation through versioned vector
operations and exact cell selections, instead of one physical SQL record per
arithmetic application. The experiment stores supplied quantity and price columns,
affine payment terms and selected discount factors; it does not yet retain the
production source-construction and selection pipeline in this form. Fine-grained
drill-down must reproduce each interval and intermediate from the same operation
and inputs. D258's amendment authorizes that integration, which requires a
measured storage cutover; the experiment does not
silently replace the current tables or introduce a dual-format compatibility
loader. Existing definitions would be rebooked or retired at that cutover.
