# Standalone physical energy booking plan

The [D303 object-oriented lifecycle design](physical-lifecycle-design.md) extends
the reuse direction across gas, crude, LNG, NGLs and power. Booked physical
memberships have DeliverySchedules over retained geometry, explicit operational
tracks and fulfillment/cash relationships. The [implemented gas facility increment](facility-quantity-ledger.md)
provides manual quantities, explicit plans, operating assessments and Side/UI views;
broader lifecycle classes remain proposals. Shared facility quantity ledgers expose
schedule/constraint views and explicit cross-trade allocations for qualified
multi-counterparty operations; financial hedges do not themselves move product.
Independent resource/movement schedules or shared evidence remain optional
supported scopes. This plan retains the physical gas/power requirements;
neither document implements the
proposed extension or selects missing financial conventions.

This plan specifies broader standalone gas/power delivery obligations and
operational settlement. Bounded [physical gas booking](physical-gas-side.md),
[contractual quantities](physical-gas-quantities.md) and
[daily quantity revisions](physical-gas-daily-quantities.md),
[manual gas operations](gas-delivery-operations.md) and bounded
[final daily cash determinations](gas-cash-billing.md) are implemented. Gas
invoicing/payment execution, broader physical completion and standalone physical
power booking remain work. [PPA booking](demo-instruments.md), bounded
[physical crude booking/operations](physical-crude.md) and
[LNG cargo booking/pre-delivery valuation](physical-lng.md) have separate contracts.
These physical workflows are not prerequisites for financial gas swaps or
cash-settled power swaps whose MW is integrated into an agreed energy payment basis.

The common design retains Instrument → Side → SideSchedule. A delivery Side owns
whole-term physical parameters and its delivery plan; a cash Side declares its own
payment/input terms. Instrument owns typed cross-membership connections. Trade-owned
lifecycle allocations link qualified evidence to determinations, documents and
cash records. Reusing delivered energy as a cash basis must not create a second
physical delivery or count cash twice. Operational state and accepted settlement
remain distinct from valuation context.

The first physical power contract should specify delivery point, obligation
supplier/receiver, exact unit, UTC interval grid, local operating clock, delivery
product, holiday calendar and supported loss rules. Reuse the interval, mask,
quantity-conversion and measurement primitives already consumed by power/PPA
calculations. Nomination, meter revisions, delivered/undelivered amounts and
operational exceptions require explicit retained records and authorized operations.
A forecast or current PV is not evidence of completed delivery.

The broader physical gas contract must specify delivery location, exact gas-day
boundary and pinned clock, total-period quantity versus a declared daily rate,
nomination/confirmation, measurement basis and any consumed quality/conversion
inputs. Energy and standard-volume quantities need an explicit versioned
conversion relationship with units and effective/knowledge coordinates. Neither a
unit label nor a price curve supplies calorific value, shrinkage, balancing or
transport terms. Storage/transport optimization is a later consumer.

Both products need an explicit lifecycle for obligation acceptance, measurement
finality, cash determination, invoice/settlement acceptance and outstanding versus
paid balances. Accepted cash retains exact contract, observation and measurement
revisions. Later eligible fixing/meter corrections may change a new valuation;
changing a posted record requires an independently authorized correction operation
with expected revision, original acceptance lineage and idempotent submission.
No valuation read silently rewrites posted operational or accounting state.

Implementation should begin with one named synthetic executable contract per
product, then retain the complete entry/preview/book/reopen/amend/replay path.
Add tables only with that current workflow. Reuse common primitives in their
owning managed modules; share dependency-free utilities in `OpenEtrm.Shared`.
Do not copy the financial ticket and imply that operational fields are supported.

Exit checks should independently prove 23/25-hour delivery days, repeated local
hours, explicit gas-day counting, energy/volume conversions, measurement gaps,
partial delivery, independent price/payment schedules and exactly-once cash
application. Historical replay must preserve accepted source revisions. Adversarial
checks must cover current authorization, conflicting amendments, duplicate or
uncertain submissions, cutoff changes and explicit posted-record corrections.
The existing bounded PPA and settlement-preview work provides reusable components,
not evidence that these standalone workflows are complete.
