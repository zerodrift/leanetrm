# Bounded monthly gross fixed-strike preview

D201 implements shared payment-lineage planning and one completed operating
month's gross fixed-strike preview for the explicitly opted-in fixed physical
contract accepted in [SP1–SP9](ppa-settlement-spec.md). Migration 0094 adds
administrative continuity records; the managed backend calculates cash from
[0093 certified meter-finality evidence](meter-finality.md).

This implements the authorized narrower power-only sequence. Global S1/S2 and
their shared gas dependencies remain open. There is no approval, persisted cash
proposal, acceptance, contribution claim, obligation, payment or settlement worker.
Existing Phase C valuation, overdue measures, risk jobs and meter availability
rules are unchanged. A preview contributes nothing to recognized cash or book
valuation, so it cannot double count a future accepted obligation.

## Operations and authority

The existing `ppa/catalog` response exposes `settlementPreview`: exact policies,
operation names, required permission, work/response limits and exclusions.
Both operations use authenticated current working sessions and an explicitly
selected available snapshot. Historical inputs use an explicit certified cutoff;
selecting historical evidence does not restore historical permissions.

- `POST /api/settlement/payment-lineages/plan`: `snapshotId`, `tradeId`,
  `expectedRevision`, exact `original` membership, complete `mappings` for the
  selected head and all six accepted `policies`. Each mapping names `bindingId`,
  `originRevision`, `instrumentId`, `sideId`, `role` and `ordinal`.
- `POST /api/settlement/ppa/preview`: `snapshotId`, `lineageId`,
  `expectedRevision`, `month` as the first operating date, and exact `knownAt`.
  The caller cannot submit a component subset or cash total. Unknown fields refuse.

Current `ReadTrades`, `ProposeSettlementDeterminations`, market `ReadMarketData`
and exact reference-catalog access are required. The new proposal permission has
**no default grants**. Existing catalog/workspace/delegation rules are reused;
this does not introduce a different per-book authorization model. The existing
transaction authorization fence and final recheck after complete serialization
preserve current authority. The API buffers the bounded response before commit
and before starting its HTTP body; a failed operation returns no partial total.
Runtime roles have no direct access to the new forced-RLS tables.

Planning persists an immutable explicit opt-in and exact successor assignments,
not cash. Natural original-membership uniqueness and exclusive successor keys are
serialized under the existing trading publication gate. Concurrent identical
plans return the same lineage; a conflicting assignment refuses atomically.
Replaying an unchanged plan adds no empty publication. Existing trading
commit certification governs visibility. A mapping's business anchor is its exact
originating binding revision, even when a retained prefix appears in a later
amended head; its knowledge is when the assignment became known. Fully replaced
original memberships remain retained. Administrative mapping does not backdate
cash acceptance.

## Complete occurrence and exact calculation

Canonical identity is `(catalog, trade origin, payment lineage, operating month)`.
The server reads one certified trade head and its whole binding partition. It
requires a contiguous original coverage partition, allowing only an explicitly
terminated suffix. Original and replacement segments retain their own MW and
strike schedules, with one compatible currency, direction, clock, native cadence,
monthly payment rule and due instant. Incompatible exact revision pins refuse the
whole operation. Every native UTC cell belongs to exactly one retained segment;
repeated fall-back hours are separate keys. The entire operating month must have
ended, including after early termination.

Each contribution key independently names catalog, trade, the original payment
membership origin and native UTC bounds. The key excludes lineage aliases,
preview time and later trade revisions. It is evidence for future ownership
claims; this implementation neither persists a cash occurrence nor claims keys.

For every declared asset-cell, the server selects the latest head visible at both
business-effective time and commit-certified knowledge. Only `final` qualifies.
Missing, suspended, withdrawn or incompatible evidence refuses the complete
occurrence without fallback. A later raw revision alone changes no selected
finality; a separately published correction can change a later-cutoff preview.
Explicit zero remains a real contribution with its declared capacity denominator.
Finality continuity remains the exact asset/source/stage/native-cell chain.
Payment continuity does not prove cross-source alias continuity; matching names,
codes or prices do not qualify renamed sources or infer replacement evidence.

`AssetVolumes` and `CapacityNormalization` reuse the accepted stage conversion,
capacity-version and unit rules. At each native interval, aggregate quantity is
the sum of the declared assets' exact eligible energy at the declared stage.
The denominator is the sum of the positive MW capacity bases in force for
those same declared assets and intervals. Stage conversion applies to energy;
it does not rescale that capacity denominator. Divide once to obtain energy per contract MW, multiply by that
segment's original MW, then by its original fixed strike and explicit sign.
Quarter-hour MWh is already energy and is not multiplied by interval hours again.
Every required asset remains in the denominator when its eligible energy is zero;
missing energy never becomes zero. No forecast supplies settlement evidence.

Exact rational quantities and signed money remain exact through all components.
The complete occurrence's aggregate cash rounds once to two decimals, ties to
even, using shared `IntegerRounding`. Response fields include exact/unrounded
cash, rounded cash, signed `rounded − exact` difference, original due terms and
`overdue_candidate` or `not_due_candidate`. These labels do not assert an accepted
outstanding balance, imply payment, accrue interest or supply settlement-aware PV.

## Evidence and limits

The response includes each contribution's originating key, selected binding and
revision, original MW, interval, strike, due date, exact quantity/cash and
normalization evidence. `proposalPins` retain the lineage/policies, one trade head,
selected bindings and mappings, original binding, complete exact terms, certified
context/cutoff, valuation snapshot, definition/reference versions, clock fingerprint
and every finality head with exact reading and verifier provenance. Decimal
financial evidence uses strings or rational numerator/denominator strings.

These are **nonbinding preview inputs**, not an approval token or authenticated
maker proposal. The lineage's `actor_id` is its historical assignment author,
not necessarily the preview caller. A future proposal/approval operation must
resolve its own current maker/represented human principal, bind the full content,
recheck current heads and authority, and implement accepted SP7 independently.
No caller-replayed identity or digest will establish authorization.

Bounds are one month, 32 retained bindings, four distinct assets, 3,100 native
contributions and 12,400 source cells. The existing request-body cap is 32 KiB and
operation deadline is 15 seconds. This new operation has a 16 MiB serialized
response cap; existing PPA previews retain their 8 MiB cap. Exact-boundary and
one-byte-over checks cover both. The API refuses an oversized complete result;
it does not omit evidence, paginate a cash total or increase the deadline.
These simultaneous limits are admission ceilings, not completion guarantees for
every Cartesian combination or retained-history size.

The unchanged knowledge system allows 4,096 retained contexts per catalog, and
current finality publication captures a distinct context per new cell publication.
Combining the 721-hour initial/correction fixtures with a 2,884-cell quarter-hour
month exceeded that cap before preview. Qualification therefore uses separate
owned databases; it does not delete history or raise the cap. In particular,
four-asset quarter-hour monthly finality cannot currently be populated through
this per-cell publication sequence within one catalog's context budget. Bulk
publication and broader retained-history capacity are not qualified here.

## Verification and reuse

The independent Python specification remains separate from production code:

```sh
python3 -m unittest discover -s specs/settlement -v
/path/to/dotnet run --project tests/OpenEtrm.MarketData.Checks -- --settlement-preview
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin \
  --dotnet /path/to/dotnet --no-build --ppa-check
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin \
  --dotnet /path/to/dotnet --no-build --settlement-quarter-check
```

Behavior checks preceded implementation. A targeted physical-scope regression
failed before the admission fix. Review also corrected mapping visibility to use
originating business time. Managed cases cover exact rational multi-asset and
capacity-version arithmetic, signed rounding ties, round-once versus per-cell
rounding, both DST transitions, quarter-hour energy, zero/missing, incompatible
contexts and retained amendment/termination coverage. HTTP checks independently
compare a complete certified month against the executable planner/preview model,
exercise current authority loss, concurrent identity creation, atomic incompatible
due/direction refusal, historical replay and no-partial-result failures.

The two correction examples remain distinct: changing **one** of 721 hourly
readings from 50 to 51 MWh restates −1,153,600 to −1,153,632, a −32 candidate
difference. Changing **all 721** readings restates it to −1,176,672, a −23,072
candidate difference. Neither preview creates an adjustment or recognized cash.

| Reused implementation | Boundary retained |
| --- | --- |
| Trading PPA terms, selected-head reads and publication certification | One exact binding partition; original quantities and terms; current authority |
| Meter-finality selector | Exact final/raw/calendar pins; two temporal coordinates; no withdrawal fallback |
| Power native clock grid and payment rules | Repeated UTC hours, original due dates and pinned clock identity |
| AssetVolumes, CapacityNormalization and exact quantity/money | Explicit stage/capacity versions, complete denominator, no financial floating point |
| Shared IntegerRounding | Two-decimal ties-to-even applied once after complete occurrence aggregation |
| Existing API request, transaction and response machinery | Strict requests, deadline/cancellation, current authority and complete bounded serialization |

The isolated hourly suite passes 34 cases; independent quarter-hour qualification
passes five cases, including its shared admission checks. The unchanged executable
specification passes 36 cases and the managed preview suite passes 31 assertions.
Independent adversarial review is green with no open blocking finding. It separately
reran the specification, managed suite, response limits, finality/legacy PPA checks
and the fresh quarter-hour HTTP workload.

Measured complete HTTP helper calls include certified-cutoff acquisition, reads,
preparation, calculation, serialization and client JSON decoding. One synthetic
trade/binding/asset and one occurrence were evaluated with no risk scenarios:

| Workload | Elapsed seconds | Decoded/reencoded JSON bytes |
| --- | ---: | ---: |
| November 2020, 721 hourly cells | 0.296844 | 3,666,907 |
| November 2020, 2,884 quarter-hour cells | 1.360170 | 14,643,897 |

These local diagnostic measurements used macOS 26.6.2 arm64, 48 GiB RAM, stock
PostgreSQL 16.14 and .NET SDK 10.0.402. Payload figures are not exact wire byte
counts. They qualify these workloads only; no maximum-size throughput promise
follows.

## Complete regression qualification — 2026-10-22

All **45 stages** of the complete synthetic gate pass in **1,604.209 seconds**,
including the new hourly preview checks, all 40 finality checks, prior PPA oracles,
R3 admission, reuse, enlarged-fixture navigation and 30 booking/inspection browser
checks. All 682 snapshotted non-document files remain unchanged through the gate;
23 gate-owned preview databases were removed. The separate quarter-hour run also
removed its owned database.

The unchanged real ten-year hourly worker recovery case completed in
**337.612 seconds**, with exactly one complete report after replacement and lost
commit acknowledgement. Original pins/deadline, independent base 40,408,960,
721 scenarios, 316,057,560 evaluated rows and 120 quantity preparations were
preserved. Renewal failure, cancellation, authority loss and overall-deadline
controls produced no report. This preserves Phase C recovery evidence; it adds no
settlement worker.

Implementation and independent review are complete within this bounded scope.
The context/response/history limits above remain. No approval, cash acceptance,
obligation, payment, worker or gas implementation was added. Global S1/S2 remain
open. This historical qualification did not deploy the application.


## Indexed physical payment extension

Migration 0095 adds the explicit `ppa_monthly_gross_indexed_v1` opt-in. The same
lineage, complete-coverage, temporal, final-meter, normalization and rounding
contracts apply. The payment side's bound index supplies native prices; the
physical location's valuation curve is never substituted. Each cell computes
`contractual quantity × (index multiplier × native index + adder) × direction`.
Only then is the complete occurrence summed exactly and rounded once. Original
amended segment quantities, index terms and payment dates remain retained.

Native hourly and quarter-hour collected observations use the existing exact
source, clock, revision and knowledge-cutoff rules. Available collected prices
are labelled `Actual`, `publication: collected`, `contractuallyFinal: false`;
this does not certify them as contractually final. Missing/withdrawn/incompatible
index evidence refuses without partial cash. Explicit zero and eligible negative
prices remain valid. No interpolation or physical-curve fallback is introduced.

Each component carries exact index/payment-price fractions and batch/revision/
known-at evidence. Its interval is the component's native interval. Curve code
joins to `proposalPins.priceSources`, which retains the exact curve/unit/source
and contexts once, and to the pinned segment terms. This lossless projection
avoids repeating unit and interval metadata in every quarter-hour row while
preserving the unchanged 16 MiB cap.

Independent checks cover unequal quantity/price weighting, both native cadences,
DST, multiplier/adder, missing versus zero, negative prices, amended/terminated
coverage and later-known single-cell price corrections with old-cutoff replay.
These price corrections are versioned source inputs, distinct from contractual
amendments and from the one-cell versus all-cell meter correction examples above.
The integrated checkpoint's full gate includes separate hourly and quarter-hour
owned fixtures and an interruption regression. See the
[tester guide](experimental-ppa-testing.md) for current commands and limits.
