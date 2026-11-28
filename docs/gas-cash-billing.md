# Final daily gas cash determinations

## Pricing-entry profiles (D447, D459)

`gas_final_daily_fixed_cash_v1` retains its original boundary: share one, no
quantity part and the complete source term. The explicitly selected
`gas_final_daily_entry_cash_v1` additionally consumes the booked share, daily
band or shortfall, and payment-specific delivery dates. It applies the existing
quantity-part behavior to the held native final quantity, scales thresholds by
the held basis, applies share and quote multiplier once, then rounds once.
Neither profile admits indexed, receipt/fuel, monthly cumulative or multisource
billing. Valuation accrual is not a final bill.

## Indexed profile (D463)

`gas_final_daily_indexed_cash_v1` bills one completed gas day of a delivery
payment priced on a named daily index, a named monthly index or point
publications. The amount is the taken
final quantity, times the retained share, times the final published fixing plus
the booked spread, times the quote multiplier and direction, rounded once. Share,
daily band or shortfall and payment dates behave as in the entry profile. An
original is due at the payment's retained next-month instant. A payment has one
family of profiles: the fixed profiles refuse an indexed payment, and this
profile refuses a fixed one. Payments governed by an exercise or swing right,
and the retired strict daily index convention, are not billable. A payment on
point publications bills from the index its location's dated periods selected
for that gas day at booking.

The fixing is selected under `final_fixing_head_v1`: of the published
observations of the payment day's retained series and source that are known at
the operation's knowledge cutoff and observed between the fixing instant and the
billing instant, the one with no known successor. It must be alone and final.
A missing, preliminary or ambiguous fixing refuses with `gas_billing_fixing`.
No market close or snapshot selection chooses the fixing, and no forecast or
mark replaces it. A monthly index gives every gas day of its month the same
fixing, so those days wait for its publication.

The preview shows the fixing, the spread and the unit price. Accepting names the
reviewed fixing, `fixingObservationId`, beside the reviewed quantity source. The
determination retains the observation by foreign key. The native writer selects
the head itself, requires it to be the reviewed observation, and checks the exact
amount before the rounding.

A later corrected fixing changes nothing until it is reviewed. The preview then
shows the new head and its contribution. A correction on the same quantity
source and a newer fixing takes the cause `index_fixing_correction`; a changed
quantity source takes one of the quantity causes, whether or not the fixing also
changed. The other cause refuses in each case. An earlier knowledge view keeps
the fixing and determinations it knew.

Scheduled daily processing does not bill an indexed payment, and the daily
billing action refuses this profile. A bill is not reconciled with the
valuation's accrued amount, which follows the membership's quantity policy and
its snapshot. A month's daily bills are not a monthly invoice.

Payments controlled by an exercise or swing right, including through their
physical source, refuse new ordinary fixed billing before and after election.
A disjoint right does not disable another supported payment. Accepted command
and source-fact acknowledgements remain available under current authority;
eligibility changes do not rewrite their outcomes.

D338 implements a bounded commercial consumer, `gas_final_daily_fixed_cash_v1`.
It consumes one completed native gas day and one fixed-price payment or fee
membership from a retained composed booking. It determines original/correcting
cash obligations; it does not issue invoices, record payments, transfer title,
post accounting journals or change contractual portfolio PV.

## Explicit execution contract

- The caller selects `final_allocated_billing_v1` or
  `final_facility_allocated_billing_v1`. Missing, provisional or withdrawn final
  evidence refuses. A source correction requires explicit manual final recording
  or facility acceptance; raw readings never replace accepted inputs implicitly.
- Coverage is exactly the cash component's source physical membership and one
  retained native operating day. Other days need not have final inputs. Different
  days/components are separate gross claims; this profile performs no implicit
  aggregation, netting, title recognition or financial-swap coverage substitution.
- The first profile supports the booked **delivery** stage only. Receipt/fuel
  components remain visible as unavailable choices. It does not derive actual
  fuel or transform final allocations using a contractual fuel percentage.
- Fixed prices, independently signed cash directions, exact quantity/currency
  revisions, quote multiplier and original payment date come from retained
  booking terms and exact reference-data FKs. No current setup or market mark
  reprices contractual consideration. Negative prices and explicit zero final
  quantities retain their signed arithmetic; neither means missing data.
- The selected `currency_minor_units_to_even_v1` policy rounds each native-day
  cash component at the exact currency revision's minor-unit precision. A
  correction contributes **new rounded total minus previous rounded total**.
  Monthly/cumulative invoice rounding is a different contract and is not inferred.
- An original uses its booked cash membership's due instant. Every new correction
  supplies a UTC due instant and an installed cause: `late_actuals`,
  `quantity_correction` or `allocation_correction`, plus review evidence. These
  typed causes describe commercial inputs, not accounting estimates/errors.
  This profile cannot correct booked prices/fees or change its selected quantity
  and rounding policies mid-chain. Such behavior needs a qualified successor
  contract with explicit continuity. There is no configurable cause master yet.

`GasCashBilling` uses existing `ExactQuantity`, `UnitPrice`, `ExactMoney` and
`AmountFormat` operations. `CashRestatement` extracts the existing swap
rounded-total difference, now used by both swap and gas consumers. Crude's
cumulative invoice allocation and record-reversal behavior remain separate.
No additional formula language, commodity framework or numerical dependency is
introduced.

## Persistence, review and history

Migration 0205 adds immutable `trading.gas_cash_determinations` and actor-owned
`trading.gas_billing_commands`. A determination retains exact trade revision,
instrument/cash slot, obligation/day, manual revision or facility adopted-target
FK, predecessor/ordinal, profile and numerical policy, currency revision, exact
and rounded amounts, contribution, due date, cause/evidence, actor and certified
recording context/publication. Core links are relational, not JSON relationships.
The command payload is retained solely for exact request replay.

The native preview pins the source, booked terms, native day, numerical policy
and previous determination. Accepting re-resolves under the publication gate and
refuses a changed review. Database checks independently enforce source scope,
finality, coverage, retained cash arithmetic, currency precision and chain
continuity. Source facts are never consumed again as another delivery or cash
movement.

Request retries return their first result. A fresh request or another currently
authorized actor recording the same accepted occurrence returns its existing
fact and receives its own immutable receipt. Changed policy, payload, cause or
due terms cannot create a duplicate or reactivate an earlier source. A genuine
accepted quantity revision creates a new linked determination, including when
its cash delta is zero. A repeated source creates no zero correction.

Reads carry the shared As of and resolved knowledge cutoff through the existing
trading binding. Later corrections do not enter earlier views. Current authority
is required for reads and receipts; `AmendTrade` plus trade/source read authority
is required for determination and recovery, without `BookTrade`. Mutations and
semantic retry receipts recheck after storage; status rechecks after receipt and
source reads. Historical views are read only. Posted-history restatement and
accounting period state remain outside this commercial contract.

## Selected metadata reads (D355)

D459 selects the latest trade revision visible at the supplied effective time
and knowledge cutoff. The billing readers follow that binding's exact component,
obligation, schedule, quote and currency relationships. Their retained schedule
proof authorizes the booked definitions after the original catalogue period has
ended; no current definition or mark substitutes for them. Manual and accepted
facility heads, scheduled quantities and cash history use the same explicit cut.
Current read authority and the original deadline remain commit checks.

The owned `--physical-assistant-check` consumer runs the actual managed billing
read at one certified context before and after adding 512 unrelated synthetic
curves, then after `VACUUM ANALYZE`, under custom and generic plans. It compares
the complete native result and bounds work per relation so reductions elsewhere
cannot conceal repeated authorization work. The disposable fixture freezes automatic
table maintenance during each comparison, checks unchanged non-curve planner
statistics and restores original maintenance settings, including after refusal.
The certified context is captured once before measuring either planning mode;
all baselines and growth reads reuse it, and the three context-table populations
must remain unchanged. Capturing a second view during baseline setup would
otherwise enlarge an earlier measurement's RLS inputs. The analyzed layout
covers the full database's RLS dependencies before recording its baseline.
Stale small-table estimates can still cause one physical curve scan;
this is not a history-independent scan or
latency claim. Each measurement explicitly selects and verifies its own
five-second SQL command deadline, independently of the general preview budget.

## Native operations and UI

- `gas/billing/read`: eligible native-day/component choices, supported policies
  and contextual determination history.
- `gas/billing/preview`: exact original/restated amount, signed contribution,
  source, predecessor and review hash. Already determined inputs are identified.
- `gas/billing/determine`: accept the reviewed source and predecessor with a
  retained operation ID, cause/due terms and evidence.
- `gas/billing/status`: recover that actor's original operation outcome under
  current authority.
- `gas/billing/worklist`, `operating-preview`: bounded discovery and review using
  an explicit processing clock, independently of a market close.
- `gas/billing/enrollment-choices`, `enrollment-preview`, `enroll`: authorized
  book/policy choices, candidate inspection and versioned scheduled scope.

Open **Scheduling → Gas → Trade deliveries**, select the booked
instrument, then choose **Billing & corrections**.
The billing panel remains available after delivery starts even when contractual
PV is unavailable. Select the claim, final source, billing profile and rounding
contract; preview, review and accept. Corrections require their explicit cause
and local due time resolved by the shared managed time resolver. Editing inputs
invalidates review. Billing edits use the shared Keep/Discard guard for tabs,
positions, pages, profile and context changes. Unknown outcomes use the Operations workspace's
actor-owned retained command and recovery card for status or exact retry,
including widget remounts and unavailable browser storage during the same visit. The UI renders managed results and performs no cash calculations.

The assistant exposes these same native reads and approval-only commands in
Physical operations, reusing their canonical Blotter operation identities.
Migration 0205 and `turns_v18` introduced the billing admission boundary;
[the current workspace runtime](assistant-workspace.md) includes its later page
aliases. Retained earlier requests remain replayable; deployment and
provider/tool consent remain separate from local qualification.

## Reconciliation and scheduled processing (D459)

Reconciliation compares contractual, recorded scheduled and selected final
quantities for the same owned obligation, native interval, unit and delivery
stage. It retains exact differences and source identities. Missing scheduled
evidence is unavailable, including when the final quantity is explicitly zero.
A variance is an observation; it neither modifies the final quantity nor changes
the payment formula. Reviewed billing can use a valid final quantity with a
variance. Automatic originals require all three quantities to match exactly.

The shared `gas.bill_daily` action uses the same managed calculation and native
writer as interactive billing. Its source names the exact trade revision,
payment, gas day, policy versions, currency precision and processing clock.
`gas_processing_instant_utc_v1` requires an explicit UTC instant on the selected
processing business date. The target gas day may be older. A market close is
neither required nor accepted as a substitute. Source-only jobs never choose a
correction's cause or cash due instant.
The current booking profile admits fixed-offset delivery clocks. Discovery uses
their retained native boundaries; this increment does not qualify daylight-saving
delivery clocks or a general business-calendar scheduler.

**Jobs → Daily gas processing** requires explicit books, a fixed delivery window,
saved action policy and mode, source/rounding policies, readiness delay and
execution limits. Preview is required before Run once or Enroll recurring.
Changing a selection, identity, context or display timezone invalidates review.
An uncertain enrollment retains the original request ID and exact body for retry.

`gas_billing_scope_reassessment_v1` visits the complete selected scope each cycle;
it is not an incremental source-change cursor or a rolling date window. Each
cycle revisits waiting days, late old-day inputs, withdrawals and visible trade
heads. The scope is 1–64 books and 1–366 gas dates, with 1–8 occurrences per page
and an explicit dispatch limit no larger than the page. A readiness delay of
0–86,400 seconds is measured from each retained native day end. These bounds are
admission limits, not a production throughput guarantee.

The first admitted cut and processing clock belong to the complete scan and its
children. Continuations and retries reconstruct fresh runtime contexts from those
coordinates. A later cycle captures a new cut. Cursor advancement, verified
assessments, child admission and the parent's terminal result commit together.
The discovery worker cannot write financial/quantity facts or create unrelated
jobs. Each admitted child uses the existing lifecycle worker and commits its
native receipt and terminal result atomically under its own original deadline.

Jobs retain their ordinary owner/delegator/service intersection and owner quota
of 64 active, paused, blocked or running jobs. A full queue retains the cursor
and retries discovery after 60 seconds; ordinary continuations wait one second.
A once-only scope completes after the last page. Recurrence follows the original
cycle due-time phase, rather than accumulating continuation delays. Pause stops
new dispatch. Already admitted children retain their own controls and expiry.
A versioned scope update refuses while affected work is running and cancels
superseded pending children before starting reassessment.

Reports distinguish assessed states, queued children, review proposals and
distinct native bills. Replaying a child acknowledges its original bill without
increasing the bill count. A finished scan is a partial assessment: waiting data,
unsupported terms, refused sources, reconciliation differences and corrections
remain visible. A proposal is reopened through Billing & corrections for a fresh
review; it is not a recorded determination.

## Qualification and remaining work

Synthetic checks cover a 100-unit delivery at 3: original payable 300, corrected
98-unit payable 294, and a separate 6-unit cash credit. They also cover
independently signed fees, zero quantity, negative prices, half-cent rounding,
missing final inputs, exact native coverage, stale reviews, immutable originals,
historical cutoffs, independent concurrent sessions, cross-actor semantic
retries, amend-only authority and permission expiry during observed database
waits. Direct runtime calls also reject relative dates, inferred timezones,
sub-microsecond coercion and omitted/extra request fields. The accepted facility
path qualifies 60 to 55 units at the same rate:
original payable 180, restated payable 165, correcting credit 15. Withdrawing
acceptance blocks new billing previews while retained determinations remain.

Run managed core checks, API `--schedules`, web tests, and owned preview
`--gas-billing-check --browser-check` plus `--gas-basis-demo-check`. The former
checks the reviewed UI operation and existing gas editor; the latter qualifies
adopted-source FKs and withdrawal/history. Assistant runtime/workspace checks
qualify the new catalog boundary separately. Local qualification includes 658
managed core checks, API schedule checks, 1,368 web checks, 85 Python checks, 14
gas billing browser checks, 20 existing gas browser checks and 32 physical
assistant scenarios. The integrated runtime suite separately checks v20 admission and older-version
refusal. These are local synthetic results,
not accounting compliance or production activation.

Next consumers need their own qualified inputs/coverage and business oracles:
commercial invoice/credit issuance and payment allocation, wider fee/price
corrections, operational receipt/fuel billing and other commodity adapters.
[Accounting policies and standards references](accounting-policy-boundary.md)
remain a separate design; approved commercial amounts can subsequently feed its
narrow journal-preview consumer.
