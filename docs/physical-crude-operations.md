# Physical crude delivery and cash

Status: locally implemented with the bounded qualification recorded below.
Production activation remains a separate release gate.

## Selected contract

The first operational profile, `physical_crude_receipt_cash_v1`, applies to an
existing fixed-price purchase: receive crude, pay positive fixed consideration,
and retain any independently signed nonnegative handling-fee membership. This
first profile requires the exact quote multiplier to equal one; other quote
multipliers refuse explicitly. It accepts an
externally certified standard-volume quantity at the booked 60°F basis, grade
and custody point. The operator supplies a business key, an effective UTC instant,
evidence and explicit confirmation of title transfer at acceptance. The profile
requires that acceptance instant to fall within the contractual delivery window.
It does not calculate observed-to-standard volume corrections or certify a
measurement from a typed quantity.

Each receipt explicitly selects one of two treatments:

- **Owned inventory:** the accepted quantity is owned by the booked legal entity
  at the retained custody point.
- **Onward delivery:** the accepted quantity is reserved against an exact booked
  sale revision with compatible owner, grade, quantity specification and custody
  point. It remains owned until a separate dispatch confirms outward title
  transfer. Dispatch cannot exceed the receipt or sale capacity.

Receipt, title confirmation, invoice issuance and payment are distinct recorded
facts. This profile supports coincident receipt/title acquisition only; selecting
it is an explicit operational agreement, not an interpretation of the trade's
physical direction. Separately timed title acquisition requires a further
contract. Inventory here is a quantity ledger, without inventory cost valuation,
tank capacity, transport scheduling or general-ledger postings.

The separation is consistent with the independent title, quantity evidence,
delivery documentation and billing provisions in these
[published crude sales terms](https://www.spr.doe.gov/reports/SSPs/PART625.html).
Those terms illustrate contract-specific choices; they are not imported as
universal rules or application defaults.

## Contractual cash allocation

An invoice selects one accepted receipt revision and one retained cash
membership: consideration or handling fee. The fixed contractual price, quote
multiplier, exact currency and payment date come from that booked membership.
Independent pay/receive memberships never net implicitly. Partial invoicing
allocates the original contractual obligation; it neither replaces booked
quantity with actual quantity nor waives the unreceived or uninvoiced remainder.

The selected numerical implementation is
`crude_cumulative_receipt_cash_v1`. Each component has a retained invoice ordinal
and predecessor. Its new invoice amount is the rounded cumulative exact amount
less the preceding cumulative rounded amount, using the exact currency's minor
units and the explicitly selected shared ties-to-even rounding policy. This
preserves the full contractual rounded amount when all quantities are invoiced.
The invoice retains exact allocation, rounded amount and rounding effect; later
facts cannot silently revise an issued invoice.

Payments are explicit signed cash records against one invoice, with their own
business key, effective instant and evidence. Partial payments leave an open
invoice balance; an invoice is not proof of payment. Overpayments, incompatible
currencies and conflicting payment direction refuse. Physical completion,
uninvoiced contractual cash and open invoice cash remain separate balances.

## Persistence and read context

Every operation retains an exact trade revision, authorized actor, certified
context, effective instant and knowledge publication. Core trade, receipt,
invoice and reference relationships use exact foreign keys. Reads resolve
facts under the selected business time and knowledge cutoff, with current
permissions. No historical query substitutes today's operational state.

A retried command returns its first outcome. Re-recording an identical business
fact with a new request identity returns the existing fact. A changed receipt
before downstream allocation creates a revision; referenced receipts cannot be
silently rewritten.

## Cash-record corrections

The explicitly selected `crude_record_correction_v1` policy supports full payment
record reversals and invoice record voids. These correct recorded facts; they do
not initiate a bank transaction, prove that money was returned, or create a
refund obligation. Each correction retains its exact target, evidence, actor,
effective time, accepted event version and certified knowledge publication.
Original invoice and payment rows remain immutable and visible in the audit view.

A payment reversal removes the entire selected payment record from the active
invoice balance. Partial reversal amounts and inferred balancing payments are
unsupported. An invoice can be voided only after every active payment against it
has been reversed, and only when it is the latest active invoice for its cash
component. Unwind later component invoices first. The restriction preserves
previously issued amounts under cumulative rounding.

Reissued invoices append a new immutable component ordinal and point to the
latest active predecessor. They never reuse a historical ordinal or revise a
prior invoice's amount. A changed payment or invoice under the same business key
can be recorded after its predecessor is reversed or voided; an exact
`corrects_id` retains that revision relationship. Replaying an identical original
fact returns that original record, even if it is now inactive, rather than
reactivating it. A second correction targeting an already reversed record refuses.

Once all invoice dependencies of a receipt are voided, the receipt may be
corrected if it has no dispatch and still meets the supported delivery-window
and effective-time rules. The existing monotonic operational timeline remains:
this increment does not introduce backdated receipt corrections, dispatch/title
reversals, actual bank refunds or inventory returns. Historical contexts before
the correction continue to show the original active balances.

Retained contractual pricing programs remain unchanged. This operational ledger
does not introduce post-delivery portfolio valuation, inventory price risk or
automated close accounting. The existing pre-delivery valuation boundary remains
explicit.

## Qualification gates

- Partial receipts preserve contractual remainder; neither treatment defaults.
- Owned and reserved quantities reconcile to receipts less actual dispatches.
- Exact-sale allocation cannot exceed sale capacity, including multiple purchases.
- Separate consideration and fee invoices retain independent signs and original
  due dates; fractional allocations reconcile under cumulative rounding.
- Partial payments, full payment and overdue balances have independent oracles.
- Replays and identical business facts create no duplicate economics; receipt
  corrections preserve history and refuse once dependent records exist.
- Historical effective time and knowledge cutoff exclude later facts; revoked
  authority and incompatible exact references fail closed.
- The native UI uses the same authorized operations and actor-bound recovery as
  other callers. It performs no quantity or financial calculations in TypeScript.


## Verification

The managed receipt/cash checks use independent quantity and cash oracles,
including a 400-barrel partial receipt against 1,000 contracted barrels,
independently signed consideration/fee components, partial payment, overdue cash,
exclusive delivery boundaries and half-cent cumulative allocations. Exact
original/invoiced/uninvoiced cash remains distinct from rounded payable cash.
The full managed core and API schedule suites pass; independent numerical,
authorization, temporal and UI recovery review found no unresolved material issue.

`python3 tools/run_preview.py --pg-bin <pg-bin> --dotnet <dotnet> --crude-lifecycle-check`
uses an owned stock PostgreSQL database. It qualifies 400 barrels of owned
inventory and 600 barrels reserved against an exact onward sale, dispatches of
250 and 350 barrels, consideration invoices of 28,000 and 42,000, independently
receivable handling fees of 40 and 60, and partial then full payment. The checks
exercise exact command replay, equivalent-value business-key replay, receipt
corrections, historical knowledge, cross-purchase sale capacity, source visibility
and revoked authority. Original retained contractual PV stays unchanged before
delivery; post-delivery contractual valuation continues to refuse explicitly.

The same runner with `--browser-check --browser <browser-executable>` passes
27 browser checks. Coverage includes opening operations beyond the first page of
authorized position facts under one exact retained context when contractual PV
is unavailable, explicit receipt choices, recovery after
a lost acceptance response and reload, invoice review invalidation on refresh,
partial payment, ledger readback and actual panel visibility after accepted
operations. The frontend suite passes 1,047 tests, including rejection of inconsistent
fact pages and preservation of exact intraday delivery boundaries.
The CI trading partition includes the complete crude lifecycle and browser checks.
All checks selected by `tools/check_all.py --group trading` pass with the pinned
managed and Node runtimes and disposable stock PostgreSQL. This includes the
existing swap, gas, mixed PPA/heat-rate, retained-program, factor-unit, index-fixing,
close-out and observation-schedule workflows.

The correction increment additionally qualifies full payment reversal, changed
business-key replacement with an exact correction link, ordered invoice voids,
reissue with a new ordinal, receipt correction after cash dependencies are inactive,
and unchanged balances in an older certified context. The native oracle preserves
70,000 of original consideration after receipt correction to 999 barrels: 27,930
is actively invoiced and 42,070 remains uninvoiced. Physical dispatch remains 600
barrels; cash-record corrections do not alter title or movement facts.

A separate half-cent native case invoices two 0.5-barrel receipts at 0.01 per
barrel. Its component amounts are 0 and 0.01; voiding and reissuing the second
retains the zero-valued predecessor and the 0.01 cumulative total. Raw PostgreSQL
rounding results must fit the exact-number storage contract without caller-side
normalization. Browser checks cover lost reversal-response recovery, restored
signed open balances, invoice void/reissue, visible retained audit rows and
rendered panel visibility. The historical business-match guard is tested against
a rolled-back counterfactual that removes only its temporal predicate.

These checks do not qualify separate title-acquisition timing, observed-volume
conversion, actual bank refunds, dispatch reversals, inventory costing, transport/cargo,
automated accounting closes or production activation. Operations are bounded to
10,000 accepted event revisions per purchase; no throughput claim follows from
this functional fixture.
