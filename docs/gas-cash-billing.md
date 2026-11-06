# Final daily gas cash determinations

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

## Native operations and UI

- `gas/billing/read`: eligible native-day/component choices, supported policies
  and contextual determination history.
- `gas/billing/preview`: exact original/restated amount, signed contribution,
  source, predecessor and review hash. Already determined inputs are identified.
- `gas/billing/determine`: accept the reviewed source and predecessor with a
  retained operation ID, cause/due terms and evidence.
- `gas/billing/status`: recover that actor's original operation outcome under
  current authority.

Open **Operations → Physical operations**, select Natural gas and the booked
instrument, then choose **Billing & corrections**.
The billing panel remains available after delivery starts even when contractual
PV is unavailable. Select the claim, final source, billing profile and rounding
contract; preview, review and accept. Corrections require their explicit cause
and local due time resolved by the shared managed time resolver. Editing inputs
invalidates review. Billing edits use the shared Keep/Discard guard for tabs,
positions, pages, profile and context changes. Unknown outcomes use the Operations workspace's
actor-owned retained command and recovery card for status or exact retry,
including widget remounts and unavailable browser storage during the same visit. The UI renders managed results and performs no cash calculations.

The assistant operation catalog exposes these same native reads and approval-only
commands on the Blotter. Migration 0205 and `turns_v18` gate new admission/tool
execution together. Retained earlier requests remain replayable; deployment and
provider/tool consent remain separate from local qualification.

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
