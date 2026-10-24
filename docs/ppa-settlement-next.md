# Original PPA settlement task proposal and current status

Current scope: D204 defers settlement approvals, accepted-cash accounting,
payments, reversals and automated correction adjustments. They are not MVP
pricing prerequisites. The [pricing/workbench record](ppa-break-even.md) describes
the current next consumer. This document retains the earlier settlement task and
reuse map; it does not select a new ledger task.

The [bounded contract and reuse/dependency map](ppa-settlement-spec.md) and
[independent executable specification](../specs/settlement/README.md) remain
available. SP1–SP9 are accepted for the explicitly opted-in bounded fixed-strike
contract (D202), including future rounding and approval conventions. The
[meter-finality prerequisite](meter-finality.md) and [payment continuity/gross
preview](settlement-preview.md) are implemented. Cash lifecycle, P39 outside
this bounded contract and global settlement phases remain open.

The original task, now complete, was **S0 policy wording and an independent executable
specification for one monthly gross fixed-strike payment**. Use one synthetic
fixed physical PPA, its retained binding coverage, one energy delivery and one
linked strike-payment side. Stop for policy acceptance before production domain
changes, migrations, APIs, approval operations or workers. This provides a
concrete first consumer for the missing shared settlement contracts.

The [PPA design](ppa-design.md#8-part-4-settlement) requires shared settlement
S0–S3 for power before PPA occurrences and determinations, then S4 for payments.
Those lifecycle phases are not implemented. The shared S1 exit criteria also
cover gas; a PPA-specific specification does not complete global S0/S1 or silently
remove that requirement. Any narrower implementation sequence needs explicit
acceptance. No parallel PPA-only obligation ledger is proposed.

## Existing code to reuse

| Source | Already available | Boundary |
| --- | --- | --- |
| [PowerSettlement](../src/OpenEtrm.MarketData/Power/PowerSettlement.cs) | Exact native-interval quantity × price aggregation, alignment/coverage/unit checks and exact rational amount evidence. | Its preview policy rounds price to 18 decimals and cash to two with ties-to-even. This is not an accepted PPA obligation-rounding contract. Duration-weighted average price is not automatically a contractual index. |
| [PowerActualSettlement](../src/OpenEtrm.Api/PowerActualSettlement.cs) | Authorized one-day collected actuals, full native-day coverage, explicit source mapping and batch/revision/cutoff lineage; interval MW or MWh quantities. | The response is unsaved and `collected`. It proves neither contractual finality nor accepted cash. Fixed-strike cash does not require physical market prices. |
| [IndexFixings](../src/OpenEtrm.MarketData/Trading/IndexFixings.cs), [PaymentSchedules](../src/OpenEtrm.MarketData/Trading/PaymentSchedules.cs), migration 0050 | Exact dated fixing relationships, partial fixing with original weights, retained actual lineage, frozen actual risk and explicit payment-date rules. | Preserve the existing v1 identity/behavior. Publication-driven eligibility and trade-owned acceptance are missing; a fixed strike needs no index reset. |
| [PpaSides](../src/OpenEtrm.MarketData/Trading/PpaSides.cs), [PpaQuantityPreparation](../src/OpenEtrm.MarketData/Trading/PpaQuantityPreparation.cs), [AssetVolumes](../src/OpenEtrm.MarketData/Power/AssetVolumes.cs) | Exact strike terms, delivery-linked quantities, eligible meter selection, source stages, capacity-version normalization and original coverage. | Phase C meter availability/deadlines are not contractual finality. External binding MW must still be applied exactly once. |
| [ScheduleRows](../src/OpenEtrm.MarketData/Trading/ScheduleRows.cs) | Native interval, side/slot, exact signed amount, payment date, lineage and separate overdue measures. | Only `RemainingProjected` and `DeliveredUnsettled` exist. Realised cash is a valuation classification, not payment evidence or a durable settlement occurrence. |
| ExactQuantity, ExactUnitPrice, ExactMoney and AmountFormat | Exact arithmetic and explicit rounding primitives. | Display precision does not select contractual rounding. |
| Migration 0092 and PpaStore | Immutable terms, retained binding history, current authority, optimistic concurrency and idempotent booking commands. | Financial settlement occurrence/action identities must be distinct from booking operations and job attempts. |
| Knowledge contexts, source revisions and migrations 0094–0095 | Certified knowledge, pinned retry inputs, current authority, fencing, renewable leases and atomic result patterns. | No settlement operation or permission is installed. Reuse these patterns only for a separately approved financial lifecycle. |

For this first fixed-strike consumer, direct reuse is the exact quantity and cash
types, PPA meter normalization, retained coverage and payment-date generation.
The power settlement kernel is an additional arithmetic comparison where its
explicit policy matches; wrapping its collected-value endpoint would not create
the missing cash lifecycle.

## Decisions the task must resolve

The original proposal required exact wording for the following choices. SP1–SP9
now supplies their accepted bounded contract; this section preserves task scope:

1. Stable gross payment occurrence identity, including how retained binding
   segments within one monthly payment period are represented. Physical delivery
   value must never be netted into the payable amount.
2. Exact meter evidence eligibility/finality, approval and certified knowledge.
   Missing, withdrawn, ambiguous, estimated or later-known required cells cannot
   support a complete accepted determination; explicit zero remains valid.
3. Sum each asset's compatible whole-asset MWh at the declared stage, divide by
   the same assets' summed MW capacity bases in force for that interval, and apply
   the originating binding MW once. Preserve source versions and stage/loss rules.
4. Sum signed exact quantity × retained strike over originating coverage. Propose
   a named two-decimal ties-to-even policy once per gross payment occurrence for
   this synthetic contract, retaining the exact unrounded value. This is a new
   proposal, not a consequence of existing display or preview rounding.
5. Keep signed unpaid known cash at and after due-date equality. Preserve Phase C
   overdue separation and unavailable complete PV unless a new due-cash policy is
   explicitly accepted. A due date is never evidence of payment.
6. Treat later corrections as candidates. Accepted pins remain immutable;
   propose an adjustment of restated rounded contractual cash less cumulative
   recognized rounded cash, with new approval and retained original payments.
7. Select explicit settlement/approval permissions, separation of duties, expected
   versions, proposal evidence fences and original-operation reconciliation.
   Distinguish financial idempotency from worker retry identity.
8. Define the exactly-once transfer from delivered-unsettled valuation into
   accepted outstanding cash without double counting. Payment and reversal remain
   shared S4 work, not part of the initial production consumer.

Accepted determinations, obligations, approval evidence, correction adjustments
and payment applications remain missing. Meter-finality evidence alone is now
implemented in 0096; the other shared lifecycle prerequisites remain open. The task must map these to shared
S0–S4 rather than treating control receipts as a financial ledger. Certificates,
capacity, floating payments, net grouping, FX, taxes, fees and bank dispatch stay
outside the proposed first contract.

## Independent acceptance specification

A proposed base fixture uses November 2031 on `America/New_York`: 721 hourly
intervals, a 100-MW asset capacity basis, 50 metered MWh per interval, an 80-MW
binding and a pay-fixed strike of 40 currency units/MWh. Each interval contributes
`50 / 100 × 80 = 40 MWh`; total contract energy is 28,840 MWh and exact signed
cash is **−1,153,600**. All cells require separately declared eligible evidence.
The accompanying physical delivery record creates no cash obligation.

The independently implemented specification covers the numerical cases and
serial contract outcomes below. Real concurrency, persistence and integration
remain future implementation gates:

- Both repeated DST hours once each, spring-forward and native quarter-hour
  variants; no duplicated duration scaling.
- Missing versus zero, withdrawals, later-known corrections, multi-asset
  normalization and a capacity-version boundary.
- Amended/terminated coverage at original quantities, strikes and payment dates,
  with every originating interval accounted for exactly once.
- A correction of **one native hourly interval** from 50 to 51 MWh: contract energy increases 0.8 MWh and
  restated cash becomes −1,153,632. The earlier accepted amount stays immutable;
  the proposed adjustment is −32 under the separately accepted correction rule.
- Round-once behavior: under the proposed ties-to-even policy, −1.005 becomes
  −1.00 and restated −1.015 becomes −1.02; the adjustment is −0.02, not a rounded
  raw delta. Later corrections compare with cumulative accepted amounts.
- Unpaid cash at due equality, corrections after payment, and no inference of
  payment from valuation classification or time passing.
- Concurrent identical commands, changed-content reuse, correction/acceptance
  races in both orders, stale versions, lost acknowledgement and authority loss;
  exact retained events with no partial effects or duplicate financial action.
- Original authorized receipt replay before new source selection; a replacement
  worker identity cannot create another financial effect.
- Unchanged v1 fixing, Phase C valuation, retained holding risk and overdue checks.

The deliverable is proposed wording, independent synthetic expected-value cases,
a source/type reuse map and a dependency/operation sketch. Stop for acceptance
before implementing any settlement lifecycle or exposing a settlement operation.
