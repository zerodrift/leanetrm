# Daily gas delivery quantities

The daily quantity workflow records trade-owned operational evidence separately
from immutable contractual Side programs. It accepts composed gas bookings only;
the older single-point template is not enrolled. No trade amendment, physical
completion, cash determination, payment, inventory or weighted inventory-cost
calculation follows from recording or selecting a quantity.

## Ownership and persistence

Migration 0145 creates a delivery obligation for each physical membership when a
composed trade is booked. Existing composed bookings receive the same mapping on
migration. An obligation pins the originating trade revision, instrument and
membership slot through exact foreign keys. The current booking profile admits
revision one only; future amendments require a qualified continuity mapping.
Two trades using reusable Sides have different obligation identities and facts.

A quantity series identifies an obligation, quantity kind, manual source profile
and delivery stage. Supported kinds are nominated, confirmed, scheduled and
allocated. Series creation has its own certified publication/effective pin.
Receipt-stage and fuel-stage observations and cross-trade movement allocations
are not admitted by this profile.

Each daily revision stores an exact nonnegative native interval total, status,
evidence, causal predecessor, operation/actor, certified recording context,
publication and effective instant. The native unit and UTC bounds come from the
retained gas program; neither is an independently editable override. Values have
up to 18 fractional digits and magnitude below 10^24. A final status is admitted
only for allocated quantities after the retained gas day ends. Provisional
allocations remain estimates, not accepted delivery or cash evidence.
Withdrawals have no quantity and explicitly supersede an existing revision.
Explicit zero is a quantity and never means missing or withdrawn.

Writes serialize on the shared trading publication gate and compare the exact
previous revision. Identical actor/request retries return the original outcome.
A fresh operation with the same current business fact is a no-op, provided its
recording instant does not precede that head. Changed data creates a new revision.
The profile admits at most 10,000 commands/facts per trade and 1,000 revisions per
series/day. There are at most four physical memberships and 31 retained days per
membership. History inspection returns the latest 100 visible revisions.

Runtime roles cannot mutate tables directly. Forced row security and restrictive
policies require current trade/source authority and certified temporal visibility.
Write functions require current working authority and AmendTrade, even on replay.
The recording context must include the trade and retained program publications;
an older same-date context cannot be attached to a later booking.

## Side schedule input resolution

`GasDailyQuantitySelection.ReadContract` loads the published gas program's named
quantity roots, exact literal quantities and retained daily grids. It does not
construct a Side, regenerate geometry or read a newer delivery setup. Each
contract quantity scales once by the positive trade size and membership ratio.

`GasDailyQuantitySelection.Select` consumes visible heads under an explicit policy:

- `operational_projection_v1`: choose allocated, scheduled, confirmed, nominated,
  then contractual, in that fixed order. Withdrawn heads are ineligible; an older
  superseded value of the same kind is never resurrected.
- `final_allocated_billing_v1`: every requested gas day must have ended and have a
  final allocated head. Missing or provisional evidence refuses the complete read.
  There is no contractual, nomination or scheduled fallback.

This policy selects one replacement quantity for a day; it does not add successive
operational stages. Equal-kind ambiguity refuses. The selected revision and source
evidence remain attached to each result. The typed native input includes exact
absolute quantity and normalization by trade size times membership ratio. For
absolute allocation 980 and scale 1,000, normalized input is exactly 49/50;
applying the existing scale once recovers 980, not 980,000.

The read consumer exposes those selected quantity inputs alongside contractual
quantities. It does not substitute them into the existing fixed contractual
program's PV, physical completion or payment roots. Dynamic linked fuel/cash
ports and settlement consumers need their own qualified execution profile. The
read response states `contractual_pv_unchanged`; no source field silently enables
that wider lifecycle. A billing quantity preview is not a settlement determination.

## API and user workflow

- `POST /api/gas/quantities/read`: exact context, trade ID and selection policy.
- `POST /api/gas/quantities/record`: current context, expected trade revision,
  operation, obligation, kind/status, day, quantity or withdrawal, evidence and
  expected predecessor.
- `POST /api/gas/quantities/history`: the latest 100 revisions under an exact
  retained grid context; loaded only when the history disclosure opens.
- `POST /api/gas/quantities/status`: original operation identity, actor-bound and
  checked against current trade/source authority.

The Physical gas page has a Daily delivery quantities action beside each composed
booking. It opens independently of pre-delivery valuation, so started deliveries
can be inspected without bypassing the pricing lifecycle guard. The grid shows
retained contract, nominated, confirmed, scheduled, allocated and selected
quantities and source kind. Users explicitly select quantity kind/status and
supply source evidence before recording. The revision history includes predecessor
and source evidence. Financial calculations stay in the backend.

An uncertain command retains its exact original contents in actor-scoped session
storage. Reload restores status/retry actions and prevents replacement commands.
A failed strict billing preview clears prior quantity results. Context/identity
changes clear displayed evidence; historical views cannot write.

## Executable acceptance

Managed checks cover priority, explicit contract fallback, strict final billing,
zero, withdrawal, ambiguity and exactly-once native quantity normalization.
Owned database/API checks exercise retained day binding, unchanged original terms
and PV, immutable corrections, source pins, certified old-cutoff reads, series
visibility, restrictive row-security fences, pre-booking context refusal, actor
replay, fresh-fact no-op, backdated no-op refusal and concurrent predecessor races.
Owned browser checks cover the grid, recording, unchanged contract/PV, unknown
write outcome, reload/status recovery, missing billing evidence and viewport fit.
All examples and evidence are synthetic.

Interactive owned previews can include the synthetic January working frames with
`tools/run_preview.py --with-gas-quantity-dates` plus the usual installed-tool
arguments. This changes only the synthetic fixture, not supported market data.

Local qualification on 30 October 2026 passes 649 managed core checks, 835 web
tests, the owned daily quantity/database assertions and 44 physical-gas browser
checks. Independent numerical, temporal, authorization and UI recovery review has
no remaining material findings. This is a bounded local workflow, not a claim of
external nomination submission, physical settlement or production qualification.

The [quantity performance checkpoint](gas-quantity-performance.md) records the
authorized batch read, demand-loaded history and measured full-grid workload.
