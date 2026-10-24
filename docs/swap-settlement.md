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
- S3 records signed cash, one explicit allocation per entry, partial/full payment
  and append-only reversals. Allocation and balance conservation are checked in
  the shared backend. Cash cannot exceed its selected contribution or move across
  trades/currencies. Reversals restore the original allocation.
- S4 supplies binding-specific lifecycle valuation to trade inspection, blotter,
  portfolio scenarios and retained-close calculation. Exact payment coverage
  excludes a determined original contribution once. Future periods stay valued;
  determinations and eligible actuals remain frozen under market shocks. Distinct
  trade histories sharing an instrument retain separate valuation scopes and
  discount inputs. Pure instrument pricing retains its existing due guard.
- S5 reports gross period recognition and exact remaining value, outstanding
  face/PV, due cash, net cash, realized/unrealized P&L, estimates, corrections and
  rounding. Two selected certified contexts reconcile by currency for an unchanged
  trade/book scope. Changed/new/missing contracts refuse the whole bridge instead
  of producing partial totals or unexplained contractual earnings.
- S6 extends trade inspection with occurrence/evidence tables, determination,
  explicit correction due entry, cash/reversal entry and opening/closing P&L.
  Backend previews bind market inputs, trade revision, settlement head and prior
  determination. Changed context or stale submission requires recalculation.
  Unknown write outcomes use existing operation-status recovery.

The host-facing `SwapSettlementOperations` adapter exposes the same preview,
inspect, determine, cash, status and reconciliation operations under current
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

Cash entry currently allocates its entire amount to one determination or
adjustment contribution. Multiple entries support partial payments; multi-target
cash allocation and cross-trade netting are unavailable. Effective-dated writes
cannot precede a later recorded settlement command or their determination.
Amendments preserve unchanged determined contributions; explicit source-changing
continuity mappings and settled-period rewrites are not supported.

The current rate implementation supports future cash dates in 1901–2199.
Discounted future cash later on the same UTC date needs a separately supported
intraday convention and is refused before determination. Explicitly undiscounted
future instants on that date use factor one. Due cash uses the nominal policy
without manufacturing a past-due discount factor.

The unchanged-contract bridge is implemented. Separate bridge contributions for
new trades and contractual amendments remain an extension; those comparisons
currently refuse. Invoices, dispatch, accounting integration, FX settlement,
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

Verification run for this local increment:

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

The repository-wide `check_all.py` matrix was not run. These results describe the
listed gates, not all products or production readiness. No deployment is qualified
by these checks.
