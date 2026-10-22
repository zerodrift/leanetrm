# Monthly gross PPA settlement contract

Status: **ACCEPTED for the explicitly opted-in bounded fixed-strike contract**,
2026-10-22 (D200). SP1–SP9, including rounding, approval and original adjustment
due-date conventions, are accepted in this scope. The proposal wording below is
retained as the exact accepted specification; its “propose” labels record the
wording's origin, not an outstanding approval gate for this bounded contract.
Acceptance does not opt in existing bookings or select conventions for other
products. The [shared meter-finality prerequisite](meter-finality.md) and the bounded
[power lineage planner and gross preview](settlement-preview.md) are implemented. Cash acceptance, obligations, payments, workers and gas remain outside
the authorized implementation scope. Existing Phase C and v1 fixing rules remain
unchanged.

The specimen is one completed operating month of one fixed physical PPA's gross
strike-payment lineage. It uses the shared settlement occurrence, determination,
approval and adjustment contracts described in [the settlement plan](settlement-plan.md).
It creates no PPA-only ledger. The original specification includes no production settlement lifecycle. The
separately implemented evidence and preview operations are documented above. The [independent executable specification](../specs/settlement/README.md)
is a pure in-memory reference model with synthetic inputs, not an application module.

## Accepted policy wording

### SP1 — scope and explicit opt-in

> `ppa_monthly_gross_fixed_v1` admits exactly one explicitly mapped strike-payment
> lineage of one trade in one catalog, one currency, one contractual clock, native
> cadence 15 or 60 minutes, one operating month and one resolved monthly due instant.
> Delivery and every cash contribution must have ended by the selected valuation
> instant; initial determination waits until the operating month ends, even after
> early termination. The admitted month contains every interval of that trade's
> active coverage, not necessarily every calendar interval after termination.
> Select the settlement rule, evidence eligibility, rounding, approval and
> correction policies explicitly. Missing selections refuse. Existing bookings
> are not automatically opted in.

The bounded specimen is pay-fixed with metered asset energy and a positive
contract-MW binding; receive-fixed is the explicit opposite sign. Retained segments
may have different MW and fixed strike schedules already supported by Phase C.
Physical delivery value, floating sides, fees, certificates, capacity, FX, net
payment grouping, invoice/bank dispatch and payments are outside this task.
Source units, stages, capacity versions and reference/definition pins retain Phase C
rules. No forecast may substitute for final meter evidence in a settlement determination.

### SP2 — stable occurrence and contribution ownership

> Define one immutable payment-lineage assignment for the trade's original gross
> strike obligation and explicit successor mappings across amendments. Its identity
> binds catalog, trade origin, original payment membership, currency, contractual
> clock and monthly payment-rule version. An economic side-definition UUID or role
> ordinal reused by an amendment does not itself prove continuity. The assignment
> requires exact originating trade/binding/side references and cannot be inferred
> from equal prices or reused instruments.
>
> The occurrence's natural identity is `(catalog, trade origin, payment lineage,
> operating year-month)`. Binding IDs, trade revision, instrument/side definition
> IDs, source revisions, preview time, due-status, rounding amount, command ID and
> worker/run ID are **not** occurrence identity. Store those as exact provenance.
> One lineage has one canonical occurrence per month; caller-chosen alternative
> IDs cannot manufacture another occurrence.
>
> Read only the binding partition of one certified selected trade head. For each
> native interval, exactly one retained binding supplies original terms and MW.
> Repeated copies of a binding in older trade revisions are history, not additional
> contributions. Retained prefixes and replacement suffixes aggregate into this
> same monthly gross payment and round once together. A memo revision preserves
> identity. Termination removes only the unoriginated suffix. Interior gaps or
> overlaps refuse; a properly terminated suffix is explicitly outside scope.
>
> Require a unique lineage assignment per original payment membership and an
> exclusive successor mapping across all lineages. The planner retains an immutable complete required-coverage manifest; preview
> refuses omitted or extra contributions or a mismatched occurrence/trade context.
> Every originating contribution has a separate unique key `(catalog, trade origin, original payment membership
> origin, native UTC start, native UTC end)`, independent of a caller
> lineage/occurrence alias. Initial acceptance
> atomically claims all these keys, including zero-valued contributions. They
> cannot be accepted again under another occurrence, binding, revision or command.
> Adjustments reference the existing claims and never claim a second copy.

Clock, currency, monthly payment rule, resolved due instant, direction or party
changes within this month's lineage are unsupported by this first contract and
refuse the **whole occurrence**. No automatic split or cross-side netting occurs.
Future support would require explicit successor-lineage/supersession policy.
Changing MW/strike is supported only through retained Phase C coverage; no
backdated rewrite of already originated or accepted terms is proposed.

Lineage assignment is now implemented by shared migration 0094; accepted
contribution ownership remains a **missing shared settlement contract**. Migration 0089 preserves an old
`binding_id` while copying/truncating its coverage, and creates a new binding for
an amendment suffix. This is why neither summing revision history nor using a
binding UUID as the monthly obligation identity is correct. Exact lookup keys
and comparisons are authoritative; a digest only assists matching.

### SP3 — meter finality and knowledge

> `verified_meter_head_v1` requires an immutable, explicitly published finality
> assertion for each exact asset/source/stage/native-cell/reading-revision target.
> It retains the exact meter revision, captured units, verifier identity and
> authority evidence, verification-effective time and its own commit-certified
> knowledge time. Verification cannot precede interval end. Neither meter receipt,
> Phase C's `meter_due_v1` deadline, elapsed time nor `Metered` classification is
> evidence of contractual finality.
>
> The authoritative meter head is a serial, append-only finality-publication
> chain per `(asset, source, stage, native UTC start, native UTC end)`, excluding
> reading revision; each head pins its exact reading revision. Head states are: `final`, `suspended` or `withdrawn`, with an explicit predecessor.
> Select its latest published head visible at both business/valuation time and
> certified knowledge cutoff. Require that head to be `final` and its exact raw
> reading to be visible and valid. A suspended/withdrawn head refuses; never fall
> back to an older final. A newer raw reading without a finality publication is
> an unaccepted correction candidate, not a new authoritative head. It neither
> replaces a selected final nor silently invalidates a proposal at another layer.
>
> Missing, negative/incompatible generation energy, duplicate/conflicting heads,
> withdrawn or unqualified evidence make the entire determination unavailable.
> Explicit zero is valid and retains its asset capacity denominator. Verification
> learned after a cutoff cannot qualify the old cutoff. Raw correction and later
> verification have distinct timestamps and identities.

Finality publication is now a **bounded shared database evidence operation**
([implementation](meter-finality.md)), with explicit scoped meter-verification
authority and no default grants. It installs no HTTP endpoint or cash operation.
A verifier's authority is checked when the assertion is published and retained as
historical evidence. Later revocation does not erase a valid assertion; an explicit
suspension/withdrawal changes its head. Current source/reference/book access is
still required for each new cash action. Cash approval is a separate operation;
the proposed cash maker/checker rule does not implicitly grant meter verification.

New cash acceptance serializes finality-head publication and acceptance through
one shared head fence. After waits, compare the current **published finality heads**
with every proposed head and the selected trade head. A changed eligible head
requires a new preview and approval (`stale_proposal`), never silent repricing.
This fence is additional to the authorization epoch; an immutable old cutoff alone
cannot ensure freshness. Memo-only trade-head changes conservatively require a
new proposal but do not create a new occurrence. Acceptance-first preserves its
pins; publication-first invalidates the old proposal. These are required future
transaction outcomes, not SQL behavior implemented by this specification.

### SP4 — exact quantity and cash; proposed round-once convention

> For each interval `i`, let `E[a,i]` be each declared asset's whole-asset metered
> MWh after the explicitly selected stage conversion, and `C[a,i]` that same
> asset's positive MW capacity basis in force. Compute
> `q[i] = contractMW[binding(i)] × Σa E[a,i] / Σa C[a,i]`.
> Keep every asset contribution/version. Missing assets do not shrink the
> denominator; zero assets retain it. Apply contract MW once. An input MWh is
> already energy: do not multiply it by interval duration again. Use the original
> binding's strike for that interval and explicit direction `s ∈ {−1,+1}`.
> Exact gross cash is `X = Σi s × q[i] × strike[i]` across all retained segments.
>
> **Propose** `cash_2dp_half_even_per_occurrence_v1`: round `X` once to two currency
> decimals, with exact ties to even, after external MW and full occurrence
> aggregation. Preserve `X` as numerator/denominator and disclose `round(X) − X`
> as the rounding difference. Never round cells, assets or amendment segments
> first. No preview/display precision supplies this convention implicitly.

This two-decimal policy is for the selected synthetic currency only. A currency
requiring another convention is unsupported until separately selected/qualified.
No market delivery price is needed to determine fixed-strike cash. Physical
market value is separate delivery evidence and creates no cash obligation.

### SP5 — immutable atomic acceptance

> One initial acceptance atomically stores the exact complete determination,
> original economic/contribution manifest, finality and raw reading pins, both
> temporal coordinates, policies, signed exact/rounded cash, due terms and
> approval evidence; claims the contribution keys; creates the gross obligation
> (or explicit zero-cash determination); consumes the exact approval; and writes
> the financial-action and command receipts. Failure commits none of these.
> Acceptance/adjustment business-effective time is the trusted successful operation
> instant after waits, not a caller-selected earlier valuation/source time; its
> commit-certified knowledge time is separate and no earlier. Backdating refuses.
> The initial specimen has no separately accepted partial meter/reset state.
> Accepted pins and terms are immutable. Later source or trade revisions cannot
> rewrite them or create an automatic cash adjustment.

Accepted zero cash still transfers every contribution out of unaccepted valuation
and consumes approval. It creates no payment right; zero is not “missing”. A later
accepted correction may create a nonzero adjustment. Historical reads select
acceptance events through the same effective/knowledge context as valuation;
current permissions are never replaced by historical authority.

### SP6 — correction adjustments and their due terms

> `restate_rounded_occurrence_v1` restates the **whole accepted occurrence** using
> its immutable original terms, covered intervals, asset/capacity versions, stage
> conversions, contract MW, sign and rounding policy. Select complete eligible
> meter-finality heads under the new explicit context. Only meter evidence changes
> in this specimen; economic-term corrections require another supported operation.
> Let `Rnew` be the newly rounded whole-occurrence cash and `Rrecognized` the sum
> of original accepted cash plus every accepted adjustment, including zero events.
> Proposed adjustment `A = Rnew − Rrecognized`. Do not round a raw cash delta.
> Unaccepted proposals do not enter `Rrecognized`. Payment or reversal timing
> never changes this economic comparison.
>
> Each adjustment requires a new complete proposal, current-head checks, an exact
> expected accepted revision and fresh approval. A different finality head with
> unchanged rounded cash records a zero adjustment and new accepted pins. The
> same already accepted target pins cannot be accepted again under a new command.
> Correction proposals do not alter recognized cash before acceptance.
>
> **Propose** `original_due_for_adjustment_v1`: adjustment currency and contractual
> due instant equal the original occurrence's due terms. A late accepted adjustment
> is therefore immediately overdue; do not invent a new grace period, interest or
> default-interest amount. Original payments remain immutable, attached to their
> original obligations; they are never reapplied automatically to an adjustment.

The −32 example changes **exactly one native hourly interval** from 50 to 51
whole-asset MWh. In November 2031 New York, 721 intervals at 50 MWh, 100 MW asset
basis, 80 MW contract and pay-fixed 40 give 28,840 MWh and **−1,153,600**.
One interval correction adds 0.8 contract MWh: restated **−1,153,632**, adjustment
**−32**. Correcting **all 721 intervals** instead gives **−1,176,672** and
**−23,072**. These are distinct scenarios. If the original −1,153,600 was already
paid, that historical payment remains; only the −32 adjustment is outstanding.

### SP7 — proposed approval and current authority

> **Propose** `two_principals_exact_action_24h_v1`: one maker proposes an initial
> determination or adjustment; a different controlling human principal approves
> the exact action, full proposal, expected lifecycle revision and policy set.
> Approval has an explicit UTC expiry strictly after issue and at most 24 elapsed
> hours later. At equality it is expired. Changed terms, source heads or expected
> revision require a new proposal and approval. Approval is single-use and is
> consumed only by the atomic accepted action; a failed transaction consumes none.
>
> Resolve authenticated accounts, service registrations and delegation to their
> controlling/represented human principals. Different account names, service IDs
> or agents for the same principal do not satisfy separation. Unresolved, absent
> or ambiguous controlling principals refuse. Delegates require both their own
> scope and their principal's current authority. No self-declared role is trusted.
>
> At proposal, approval and acceptance require the corresponding scoped action
> permissions, current book/source/reference access and exact resource context.
> At commit, after every wait, recheck maker, checker and executor authority and
> active delegation, approval expiry, expected versions and any worker fence.
> Revocation before commit refuses without partial effects. Restoration does not
> make a stale proposal fresh. Job administration, valuation rights or a model
> response grant no settlement authority.

Suggested permission **names**, also proposed: `ReadSettlements`,
`ProposeSettlementDeterminations`, `ApproveSettlementDeterminations`,
`AcceptSettlementDeterminations`, `AcceptSettlementAdjustments` and
`VerifySettlementMeters`. Initial and adjustment approval use the same checker
rule but approvals bind distinct action kinds. No role grants are inferred.
A cash maker or checker may separately be a qualified meter verifier; no additional
separation beyond the explicitly proposed cash rule is assumed. Real licensing,
production roles and operator workflows remain qualification gates.

### SP8 — financial idempotency and unknown outcomes

> Namespace a command by catalog, authenticated actor and request ID; compare its
> complete submitted content. First recheck current read/control authority, then
> return an identical previously accepted command's original receipt **before**
> selecting new meter/trade heads or testing the old expected version/approval
> expiry. Changed content conflicts. Another actor cannot read a private command
> receipt by knowing its request ID. Ordinary scoped financial history is separate.
>
> Separately enforce canonical occurrence uniqueness, exclusive initial contribution
> ownership, one accepted successor per expected lifecycle revision and one
> accepted transition to an exact target finality manifest. A new command/worker ID
> for the same financial action refuses `already_accepted`; it cannot create cash
> twice or reuse consumed approval. A zero action follows the same rules.
> Unknown HTTP/worker acknowledgement reconciles the original command, never a
> newly minted financial identity. No success is inferred from transport state.

The shared future transaction must serialize publication and cash acceptance,
lock occurrence/claims/approval in a consistent order, check current authority
through commit, and commit events, partition ownership, approval consumption and
receipts together. The executable model examines both serial race orders and
rollback-as-no-state-change; it does **not** prove real database locks, durability,
RLS or distributed retry behavior. Those remain implementation acceptance gates.

### SP9 — valuation partition and outstanding cash

> Under one consistent valuation/effective/knowledge context, each originating
> payment contribution is either unaccepted or owned by its accepted occurrence,
> never both. Before acceptance, Phase C projected/delivered-unsettled rows retain
> their existing policies and exact amounts. Once acceptance is visible, exclude
> all claimed cells from unaccepted payment valuation and include the accepted
> obligation amounts instead. Physical delivery and certificate holdings retain
> their separate existing treatment. Do not use a settlement state from one
> cutoff with market/trading rows from another.
>
> Accepted cash uses immutable rounded determinations, never refreshed meter
> values. Corrections awaiting acceptance are separately labelled candidates and
> enter no recognized outstanding total. After adjustment acceptance, include its
> separate gross signed obligation exactly once. No payment is inferred from a
> due date, Realised classification or zero net balance.
>
> Before due, value each nonzero outstanding obligation under the explicitly
> selected existing discount convention and pinned factors (or explicit no-
> discounting). At or after due, preserve it as a known overdue signed balance,
> with no inferred discount/interest. Keep estimated overdue unaccepted amounts
> separate. `ValueExcludingOverdue` excludes overdue cash; `CompletePv` is
> unavailable if any nonzero gross outstanding obligation is overdue, even when
> opposing obligations sum to zero. A zero-cash accepted determination has no
> outstanding balance and no payment right. Preserve exact-to-rounded difference
> separately for reconciliation; never hide it as profit or another payment.

Payment evidence in the executable scenarios is **immutable external test input**
representing a future S4 consumer. This task implements no payment command,
allocation, reversal, netting or bank effect. Payment evidence retains effective and certified knowledge times and is selected
under the same context as obligations. Only the complete signed amount of a named
nonzero obligation is admitted in this specimen; unknown, duplicate, partial,
excessive or wrong-sign facts refuse. Payments may discharge only their named
obligation, once; a correction cannot copy them to a new obligation. A
complete settlement-aware valuation adapter is missing; Phase C code remains
unchanged. No accepted-obligation totals are advertised through current APIs.

## Operation and dependency sketch

| Shared operation and status | Required input and atomic outcome |
| --- | --- |
| Resolve payment lineage/plan occurrence (bounded power consumer implemented in 0094) | Explicit lineage mapping, exact selected trade head, month, native grid and retained coverage → canonical occurrence plus original component manifest; refuse incompatible terms/gaps/overlaps. No cash acceptance. |
| Publish meter-finality head (bounded S1 prerequisite implemented in 0093) | Exact raw cell revision, previous finality head, explicit final/suspend/withdraw assertion, verifier/context → certified append-only head. No cash. |
| Preview complete gross determination (bounded power consumer implemented) | Occurrence, exact trade/source heads, explicit policies and context → exact components, rounded proposal, due terms and full fingerprint; incomplete evidence produces no complete cash proposal. |
| Approve exact action (S3) | Maker proposal, resolved independent checker, expiry and expected version → single-use exact approval; no obligation yet. |
| Accept original/adjustment (S3) | Original request ID, exact approved proposal, expected accepted revision and source/trade heads → immutable event/obligation, claims, consumed approval and receipt atomically. |
| Read status/history (S3) | Original actor-bound command or separately scoped occurrence → original authorized receipt/history; no fresh pricing on replay. |
| Value settlement partition (S3) | Same certified trade/market/settlement context, claimed contributions, retained obligations, explicit discount factors → disjoint unaccepted/accepted/overdue measures. |
| Record/reverse full payment (S4, later) | Separately approved payment evidence, exact named obligation and outstanding state → shared payment lifecycle; **not implemented or newly authorized here**. |

Approval, acceptance, status/history of accepted cash, settlement-aware valuation
and payments in this sketch remain unimplemented. Global S1/S2 remain open. Dependency order:
bounded policy acceptance (complete) and source-use qualification (S0) →
shared finality evidence (S1 prerequisite) → occurrence planner/preview (S2) →
approval, atomic acceptance, corrections and valuation partition (S3) → shared
payments (S4). S5 automatic reconciliation requires its own profiles, discovery,
limits, current-authority/fencing and recovery qualification. No current risk job
profile is repurposed to settle. Migration 0093 is the meter-finality prerequisite; 0094 is immutable payment
continuity only. No cash-acceptance migration number is reserved.

## Code-aware reuse map

| Existing source | Reuse and required boundary |
| --- | --- |
| [PpaSides](../src/OpenEtrm.MarketData/Trading/PpaSides.cs), [PpaQuantityPreparation](../src/OpenEtrm.MarketData/Trading/PpaQuantityPreparation.cs), [AssetVolumes](../src/OpenEtrm.MarketData/Power/AssetVolumes.cs), CapacityNormalization | Exact retained strikes, stages and per-contract-MW quantities; apply the separate accepted finality predicate before admitting settlement inputs. Do not treat Phase C meter eligibility as finality. |
| [ScheduleRows](../src/OpenEtrm.MarketData/Trading/ScheduleRows.cs), [PaymentSchedules](../src/OpenEtrm.MarketData/Trading/PaymentSchedules.cs) | Original interval/slot/source/due-date evidence and existing value measures. Add shared accepted contribution ownership later; current enum has only projected and delivered-unsettled states. |
| [PowerSettlement](../src/OpenEtrm.MarketData/Power/PowerSettlement.cs), ExactQuantity/ExactUnitPrice/ExactMoney, AmountFormat | Exact quantity×price primitives and independent arithmetic comparison. Its preview precision is not the proposed payable rounding rule; its average price is not this fixed strike. |
| [PowerActualSettlement](../src/OpenEtrm.Api/PowerActualSettlement.cs), power_data collection/history | Native coverage and certified actual-price lineage patterns. Current response is unsaved/collected, not final or accepted. Fixed-strike cash needs no floating-price publication; physical market value remains separate. |
| [IndexFixings](../src/OpenEtrm.MarketData/Trading/IndexFixings.cs), migration 0050 | Exact dated observation/index/source references and frozen actuals. Preserve `IndexFixing_v1`/`published_index_observations_v1`; neither provides publication-driven gas or meter finality. |
| [0089](../db/migrations/0089_ppa_bookings.sql), PpaStore | Immutable economic terms, selected revision partition, original binding coverage, idempotent command/current-authority patterns. 0094 adds explicit lineage mapping; the shared planner derives canonical occurrence identity. Claims/approvals/obligations remain absent. |
| Knowledge certification and [0091](../db/migrations/0091_close_book_risk.sql)/[0092](../db/migrations/0092_close_risk_renewal.sql) | Temporal pins, authority, atomic receipt and worker recovery patterns. A report receipt is not a financial ledger; 0094 adds authorized lineage/preview operations and proposal permission, without default grants or a job profile. |

The independent Python specification imports none of these implementations. It
uses rational arithmetic and civil-time grids with literal expected values. It is
an independent preview oracle, not a replacement production financial engine.

## Shared gas dependencies and completion boundary

| Shared phase | Gas dependency still required; not implemented by this specimen |
| --- | --- |
| S0 | Select licensed publisher/feed, exact symbol/location, daily vs monthly/bidweek definition, currency/unit, trade vs flow calendar, weighting, finality/revision/correction and approval rules. No synthetic PPA choice selects these. |
| S1 | Typed raw publication/revision evidence, explicit weekend/holiday multi-flow-date mapping, month-end splits, separately certified finality/knowledge and scoped rights; distinguish bidweek from daily-average indexes. Preserve old v1 replay. No implicit carry-forward or reconstructed publisher index. |
| S2 | Shared occurrence/reset planning with gas's explicit flow targets and weights, eligible fixings and original quantities; reuse the same cash contract once compatible, not the PPA meter numerator. |
| S3 | Shared immutable determinations, approvals, correction adjustments, accepted contribution ownership and overdue valuation; add gas synthetic numerical and temporal/race cases. |
| S4 | Shared approved full-payment evidence and reversal/adjustment, original-action recovery and exactly-once financial effects. |
| S5 | Qualified publisher calendars, correction/backfill windows, discovery, paused rollout and actual worker authorization/fence/recovery tests. |

The fixed-strike specimen does not need floating market prices for its cash, but
that does not waive the PPA Phase D dependency on shared S0–S3 for power or the
shared S1 power **and gas** exit criteria. Meter-finality evidence itself is new
shared prerequisite work. This document closes no global settlement phase and
qualifies no source license, production deployment or payment operation. The
separate meter-finality prerequisite qualifies its own bounded SQL races only.
Further settlement implementation still requires explicit authorization.
