# Financial swap settlement MVP

Scope: financial fixed/floating and floating/floating swaps. Futures margining
and physical settlement are subsequent increments. These contracts do not opt in
existing trades automatically or change pure instrument pricing.

## Accepted policies (D215)

Enrollment pins one exact currency revision and its `display_minor_units` value
as contractual precision under `currency_minor_units_to_even_v1`. This is an
explicit contractual adoption for enrolled swaps, independent of UI formatting.
Each gross side-payment occurrence aggregates exact applications, allocation,
membership ratio, direction and booked quantity before one ties-to-even rounding.
Corrections restate that total and subtract the previously determined rounded
total; intermediate prices, applications and unit cash are never money-rounded.

`swap_due_nominal_v1` carries due/overdue balances at nominal amount, with no
automatic interest or credit adjustment. Future determined balances use the
selected explicit discount policy. Ordinary market scenarios freeze determined
cash and eligible actual fixings. Existing pure pricing retains `side_payment_due`.

`swap_completed_period_trading_pnl_v1` realizes each completed contractual period
only after all its quantities and prices are established. A known fixed price
does not realize future delivery. Payment reduces outstanding balance without
recognizing earnings again. This is trading P&L, not statutory accounting.

`financial_swap_period_completion_v1` pins the completion rule and instant on
an occurrence. Date-only periods complete at midnight UTC on the exclusive end
date (`date_only_utc_v1`). Clocked MW schedules complete at their retained final
delivery interval end (`retained_quantity_interval_end_v1`). A matching gross
period realizes only when both memberships have completed and are final.

`swap_explicit_correction_due_v1` requires a valid explicit due instant for every
correction. It never inherits an earlier historical date. Original determinations
and cash history remain immutable; correction amounts have separate lineage.

## Ownership and sequence

A payment occurrence belongs to a trade and originating binding/membership and
contractual period. Current side IDs, payment dates, revisions, corrected prices
and calculation runs are evidence, not obligation identity. Before durable
determination, a current generated payment is only a candidate occurrence.
Durable acceptance must resolve retained continuity and reject ambiguous or
unsupported changes to previously determined contributions.

Deliver in order, with executable gates:

| Slice | Deliverable | Gate |
| --- | --- | --- |
| S1 | Pure final-payment calculation and occurrence candidate | Existing pre-due cash agrees exactly; quantity/direction once |
| S2 | Immutable determinations, continuity and correction adjustments | Authorized idempotency, certified replay, concurrency, no duplicate contributions |
| S3 | Cash, allocations, reversals and balances | Partial/full payment and conservation |
| S4 | Explicit settlement-aware trade/portfolio value | Exact occurrence coverage, continuous due value, unchanged future periods |
| S5 | Per-period recognition and daily economic bridge | Closing remaining minus opening remaining plus net cash reconciles |
| S6 | Existing trade inspection UI and shared operations | Evidence, determine/payment/correction/reconciliation; stale submissions refused |

Determination requires every indexed contractual fixing to be eligible and final
and every quantity to be established. Projected amounts remain valuation inputs.
Historical contexts retain exact pins and knowledge cutoff through every read.
Financial operations reuse current authorization and publication fencing; no
separate invoice or settlement approval workflow is required.

Reports retain gross sides and currency partitions and identify overlapping
measures: determined outstanding is part of remaining value; due cash is a subset
of outstanding; realized/unrealized transfers do not add economic profit. New
trades and contractual changes require separate bridge contributions. Forecast
transitions, discount carry, corrections and rounding stay visible.

Backend limits exclude invoices, bank dispatch, accounting integration, cross-trade
netting, FX settlement, overdue interest, write-offs, disputes, backdated rewrites
of settled periods, physical cost accounting, option lifecycle and fee engines.

## Implemented local capabilities

S1–S6 have executable managed, persistence, HTTP, delegated-operation and browser
consumers. This is a bounded local MVP, not production qualification.

- S1 uses `SidePaymentCash` for the same exact schedule arithmetic as existing
  pricing, without discounting. Preview retains quantity, application/fixing,
  model, numerical, currency-rounding and certified context evidence.
- S2 persists trade-owned occurrences and immutable original/correction chains.
  Authorized commands fence trade head, settlement version, input preview and
  publication. Exact actor/payload retries return the prior result. Unchanged
  periods retain occurrence identity across amendments; determined contractual
  rewrites and changed completion sources are refused.
- S3 records signed cash with explicit gross allocations, partial/full payment
  and append-only reversals. Allocation and balance conservation are checked in
  the shared backend. Gross allocations cannot exceed their selected contributions or move across
  trades/currencies. Reversals restore the exact original allocation set.
- S4 supplies binding-specific lifecycle valuation to trade inspection, blotter,
  portfolio scenarios and retained-close calculation. Exact payment coverage
  excludes a determined original contribution once. Future periods stay valued;
  determinations and eligible actuals remain frozen under market shocks. Distinct
  trade histories sharing an instrument retain separate valuation scopes and
  discount inputs. Pure instrument pricing retains its existing due guard.
- S5 reports gross period recognition and exact remaining value, outstanding
  face/PV, due cash, net cash, realized/unrealized P&L, estimates, corrections and
  rounding. Two selected certified contexts reconcile by currency for an unchanged
  trade/book scope. The separately selected D216 policy adds new trades and
  supported future-period amendments. Missing or unsupported selected contracts
  refuse the whole bridge rather than producing partial totals.
- S6 extends trade inspection with occurrence/evidence tables, determination,
  explicit correction due entry, cash/reversal entry and opening/closing P&L.
  Backend previews bind market inputs, trade revision, settlement head and prior
  determination. Changed context or stale submission requires recalculation.
  Unknown write outcomes use existing operation-status recovery.

The host-facing `SwapSettlementOperations` adapter exposes the same preview,
inspect, determine, cash, net preview/record, status and reconciliation operations under current
principal/delegator/delegation checks. `swap_settlement_operator` is an opt-in,
agent-only role; the migration creates no agent, delegation or default grant.
The existing page assistant remains a reader. Agent arguments cannot replace the
host's authorized frame or promote historical reads into writes.

The economic bridge is:

> Economic P&L = closing remaining value − opening remaining value
> + change in cumulative contractual cash known to the two contexts.

Effective-dated cash in `(opening instant, closing instant]` is shown separately.
A cash entry recorded later for an earlier date appears as a prior-cash knowledge
adjustment. It must not be labeled cash received during the period. Realized and
unrealized movements reconcile to economic P&L. Contractual face movement and
discount-effect movement form another reconciliation; corrections and rounding
are overlapping explanations, not additional earnings columns. This is not a
full market-factor attribution engine.

## Enforced bounds and remaining work

Only fixed/floating and floating/floating financial swaps, including qualified
monthly/scheduled multi-month terms, are executable consumers. One exact currency
per instrument is retained; book results stay partitioned by currency. First
accepted determination enrolls the trade under the explicit policy versions.
Inspection alone creates no obligation or silent enrollment.

Single-contribution cash entry remains available. Multiple-target settlement
requires the explicit D216 contract below; cross-trade netting is unavailable. Effective-dated writes
cannot precede a later recorded settlement command or their determination.
Amendments preserve unchanged determined contributions; explicit source-changing
continuity mappings and settled-period rewrites are not supported.

The current rate implementation supports future cash dates in 1901–2199.
Discounted future cash later on the same UTC date needs a separately supported
intraday convention and is refused before determination. Explicitly undiscounted
future instants on that date use factor one. Due cash uses the nominal policy
without manufacturing a past-due discount factor.

The unchanged-contract bridge remains available without changing its numerical
contract. New trades and supported contractual amendments use the separately
selected closing-context bridge below. Invoices, dispatch, accounting integration, FX settlement,
interest, write-offs, disputes, configurable fees, option lifecycle, futures
margining and physical settlement remain outside this MVP.

## Local verification

Behavior checks exercise exact gross cash, final fixing eligibility, one-time
booked quantity scaling, ties-to-even rounded corrections, payment conservation,
clocked completion, mixed period finality, exact occurrence coverage, independent
trade histories, frozen scenarios and P&L reconciliation. Disposable unmodified
PostgreSQL checks exercise migrations, certified replay, idempotency, concurrent
publication, amendment refusal, cash reversals and delegated authority. Browser
checks exercise the actual trade inspection workflow, stale previews, payments,
corrections and reconciliation on desktop and narrow layouts.

The scheduled-swap prerequisite has managed, disposable HTTP and browser gates;
it is preserved as separate work. Passing these bounded checks does not qualify
all financial policies or deployments. No external/proprietary product material
or non-synthetic market dataset is used by these fixtures.

Verification run for the initial D215 increment:

| Gate | Observed result |
| --- | --- |
| Managed market-data suite | 591 core checks passed |
| Rate conventions | 84 checks passed |
| Shared contracts | Shared rounding and 34 temporal checks passed |
| API schedule and close-risk contracts | Passed, including incompatible model receipt refusal |
| Frontend | 327 tests passed |
| Scheduled swap / settlement disposable HTTP and delegated operations | Passed |
| Owned scheduled swap / settlement browser workflow | 15 checks passed; desktop and narrow screenshots inspected |
| Existing-close worker regression | Passed publication, independent PV/receipts, historical visibility and cancellation fencing |
| Verification-runner Python tests | 64 tests passed |

The initial D215 run did not execute the repository-wide `check_all.py` matrix.
The follow-up D216 verification below is separate. No deployment is qualified
by these checks.

## Explicit intra-trade net settlement (D216)

`explicit_same_due_swap_net_v1` requires two to 256 explicitly selected distinct
gross determination/adjustment contributions, one trade, identical exact legal
parties, currency revision, adopted precision, lifecycle policy versions and due
instant. The MVP uses exact due-instant equality; broader date compatibility and
cross-trade/currency netting are unsupported. Allocation amounts retain their
outstanding direction, cannot exceed a gross balance and sum exactly to the
entered signed cash amount. There is no inferred allocation priority.

A −300 payable and +350 receivable can clear through a +50 cash header and
allocations −300/+350. A partial −100/+120 allocation records +20 cash and leaves
−200/+230. A zero-net allocation records `zero_net_discharge_v1`, explicitly a
noncash discharge with evidence for both gross balances. It does not invent a
cash movement. Immutable gross determinations and correction chains remain
separate. Corrections may participate only when their explicit due instants and
other retained terms are eligible.

Migration 0111 extends the existing cash/allocation records without rewriting
financial rows. A payment header appears once; allocations are a separate array.
Singleton headers retain their determination/allocation fields for inspection
compatibility. Balance reads sum gross allocations, and cash/P&L sum headers once.
The same publication lock, expected trade/settlement versions, actor/payload
idempotency and current-authority checks protect single and net writes. Reversal
must be the exact opposite of the entire original allocation/policy set and can
occur once; an immutable reversal restores the original gross balances, including
zero-net discharge. History retains effective and recording knowledge separately.

HTTP `net-preview` and `net` and the host-facing `PreviewNet`/`RecordNet` operations
share these checks. Preview binds selected terms, balances, frame and versions;
record requires its reviewed hash. Reverse previews derive exact original lines.
The existing status operation recovers unknown outcomes using the retained
operation identity. The trade settlement UI supports selection, explicit amounts,
gross/net preview, record, history and reversal review. No new approval workflow
is introduced.

## Closing-context contractual P&L bridge (D216)

`reject_contract_changes_v1` retains the unchanged-scope bridge. Explicitly selecting
`closing_context_contract_changes_v1` adds the following independent calculations,
where economic value E is remaining contractual value plus cumulative contractual
cash known to the selected context:

| Contribution | Definition |
| --- | --- |
| New-trade economics | Closing E for a trade visible only in closing scope |
| Contractual amendment | Closing E of revised terms minus closing E of prior terms |
| Continuing economics | Closing E of prior terms minus opening actual E; unchanged trades use their closing actual E |

Their signed sum equals closing actual E minus opening actual E. A trade absent
at opening contributes its complete closing economics, including supported later
amendments; the separate amendment component applies to trades present at opening.
New-trade additions include bookings learned after opening knowledge, including earlier
effective bookings; their cash timing remains separately reported. Every
intervening revision must be visible and retain book, exact parties, instrument
family, currency and quantity basis. Changed/added/removed delivery periods must
start strictly after the amendment effective instant and remain undetermined at
that instant. Date-only start uses UTC midnight; clocked power uses retained first
delivery interval start. Started/completed undetermined rewrites also refuse.
Book transfers, missing scope, unknown continuity, incomplete inputs and unsupported
transformations refuse the entire comparison. Legacy amendment admission is not
broadened by this reporting capability.

Prior exact terms are reread with closing authority and valued in the closing
certified market, independently resolving primary/supplemental definitions. Only
exactly unchanged cash-contract and completion hashes carry immutable determined
contributions into this counterfactual. Cash assigned to those retained
contributions is reconstructed from gross allocation lines, so a net header
spanning retained and changed economics cannot double-count or misassign cash.
This is a report-only counterfactual; it creates or changes no determination.

Realized transfer plus unrealized movement is an alternative reconciliation to
the same economic P&L. Remaining-value movement plus period-effective cash plus
prior-cash knowledge adjustment is another. Contractual face plus discount-effect
movement is another; discount effect describes PV versus face changes, not a
market-factor attribution claim. Corrections, rounding, fixing transitions and
period state transitions overlap these explanations and are not additive earnings
columns. Netting, reallocation, payment and reversal exchange balance and cash
without recognizing profit again. Reporting UI explicitly selects the policy and
can capture a certified opening snapshot before the trade was booked.

## D216 local verification

The focused disposable swap gate passes managed allocation and contractual-change
oracles, all six qualified observation conventions through preview/booking/reopen/
amendment, exact custom projection retention, immutable replay, concurrent net
consumption, zero-net discharge/reversal, and authorized delegated operations.
Its owned browser run passes 62 checks, including independent quantity/spread
bulk entry, selected price/payment/observation edits, two-month explicit weights
with an independent 86.5 cash oracle, delayed bulk/weight responses, net allocation
review/record/reversal, and opening-before-booking P&L. Desktop and narrow
screenshots are inspected. The frontend run passes 328 checks.

Independent behavior examples include −300/+350 discharged by +50, an explicit
−100/+120 partial allocation, +10 newly booked economics, −30 future-period
amendment and −6 supplemental-to-primary-market amendment. Completed but
undetermined rewrites and new-trade exact-basis transformations refuse. Existing
unchanged-contract fixing, determination, correction, cash-knowledge and reversal
oracles remain in the same gate. Independent review findings on async restoration,
complete revision qualification, input bounds and conflict responses are resolved.

The browser also drops a successful net-record reply, reloads the page, verifies
that the original operation and body are retained, then recovers through status
without another cash movement. Determine/payment/net requests share the validated
reload path and retain current authorization on retry. Net entry participates in
unsaved-work detection and clears on trade changes and confirmed submission.

All repository `check_all.py` gates passed across owned local runs. The initial
full run passed shared preparation, 621 managed core checks (plus the registered
connected/shared suites), 84 rate checks, 328 frontend checks, 64 verification
runner checks, 36 independent settlement specifications, API adapters and 173
combined-schema checks. A Chrome automation connection closed during the standard
browser gate; a fresh owned browser passed that gate, and every subsequent matrix
gate passed. This includes the 62-check swap workflow, existing-close worker
replacement/lost-acknowledgement recovery, connected portfolio and generated-close
checks, physical-product compatibility, hosted/test infrastructure, durable risk
jobs and the final mixed-period fixture. The interrupted invocation is not
represented as an uninterrupted successful run. All databases and browser profiles
were disposable and synthetic; no application was deployed.
