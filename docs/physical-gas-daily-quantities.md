# Daily gas delivery quantities

The daily quantity workflow records trade-owned operational evidence separately
from immutable contractual Side programs. It accepts composed gas bookings only;
stored single-point trades are not enrolled. No trade amendment, physical
completion, cash determination, payment, inventory or weighted inventory-cost
calculation follows from recording or selecting a quantity.

D321 adds a separate [facility-final input policy](gas-basis-demo.md#final-input-contract).
It reads adopted exact allocation links rather than copying facility amounts into
manual quantity revisions. Existing manual policies below remain unchanged.

## Ownership and persistence

Migration 0145 creates a delivery obligation for each physical membership when a
composed trade is booked. Existing composed bookings receive the same mapping on
migration. An obligation pins the originating trade revision, instrument and
membership slot through exact foreign keys. Future composition amendments now
create new revision-owned occurrences; they refuse trades with daily quantity,
service or facility relationships rather than infer continuity. Reads select the
retained selected revision's obligations. Two trades using reusable Sides have
different obligation identities and facts.

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
series/day. There are at most four physical memberships and 1200 retained
operating days per membership. History inspection returns the latest 100 visible revisions.

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
that wider lifecycle. A billing quantity preview is not a settlement determination. The separately
enrolled [D338 daily fixed cash consumer](gas-cash-billing.md) supplies that
bounded determination and its corrections without changing this read contract.

## API and user workflow

- `POST /api/gas/quantities/read`: exact context, trade ID and selection policy.
- `POST /api/gas/quantities/record`: current context, expected trade revision,
  operation, obligation, kind/status, day, quantity or withdrawal, evidence and
  expected predecessor.
- `POST /api/gas/quantities/history`: the latest 100 revisions under an exact
  retained grid context, available through the native history operation.
- `POST /api/gas/quantities/status`: original operation identity, actor-bound and
  checked against current trade/source authority.

Open **Operations → Physical operations → Delivery** for the booked gas
instrument and select its exact side/day row. **After flow** offers allocated
quantity entry with explicit status and evidence, followed by review and
acceptance. Nominations, confirmations and schedule adoption use the separate
[manual operations](gas-delivery-operations.md). The Blotter and separate trade
windows mount the [deal ticket](deal-ticket.md) read-only; their schedules retain
the operational projection alongside unchanged contractual amounts.

Operational reads remain available after delivery starts without bypassing the
pricing lifecycle guard. Financial calculations stay in the backend. The
workspace retains an uncertain command for status or exact retry across widget
remounts; available browser storage also preserves it across reloads. Entry
requires working data and current AmendTrade authority. Historical views and
read-only trade windows offer no quantity writes. Strict billing uses its
separate [daily cash workflow](gas-cash-billing.md); quantity history remains
available through the native history operation.

## Executable acceptance

Managed checks cover priority, explicit contract fallback, strict final billing,
zero, withdrawal, ambiguity and exactly-once native quantity normalization.
Owned database/API checks exercise retained day binding, unchanged original terms
and PV, immutable corrections, source pins, certified old-cutoff reads, series
visibility, restrictive row-security fences, pre-booking context refusal, actor
replay, fresh-fact no-op, backdated no-op refusal and concurrent predecessor races.
The original ticket checkpoint verified recorded-day schedule projection,
unchanged contractual amounts, role/context entry restrictions and lost-response
recovery in the then-editable physical-side tab. D340 moved entry and recovery
to Physical operations; the [workspace contract](physical-workspace.md#instrument-inspection-and-operational-work-d340)
records the current path. The dated qualification below describes that original
checkpoint. All examples and evidence are synthetic.

Interactive owned previews can include the synthetic January working frames with
`tools/run_preview.py --with-gas-quantity-dates` plus the usual installed-tool
arguments. This changes only the synthetic fixture, not supported market data.

Local qualification on 31 October 2026 passes 653 managed core checks, 1071 web
tests, the owned daily quantity, deal ticket and composition database assertions,
16 physical-gas and 28 deal ticket browser checks. This is a bounded local workflow, not a claim of
external nomination submission, physical settlement or production qualification.

The [quantity performance checkpoint](gas-quantity-performance.md) records the
authorized batch read, demand-loaded history and measured full-grid workload.
