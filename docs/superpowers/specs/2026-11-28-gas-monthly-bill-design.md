# Monthly gas bill for one trade: design

Status: built under D464 on 28 November 2026. The owner selected the four
policies under [Selected policies](#selected-policies); the rest is the
maintainer's design, corrected after an independent review. See
[the contract](../../gas-monthly-bills.md) and
[local qualification](../../verification.md#monthly-gas-bills-d464).

## Intent

Final gas billing determines cash one payment and one gas day at a time
(D338, D459, D463). Nothing gathers those determinations into the document a
counterparty is sent for a delivery month, and nothing says which
determinations a document already covers.

This design adds that document for one trade: a monthly bill over the trade's
existing daily determinations, and adjustment notes for determinations made
after it. It creates no payable. Every amount on a document is the amount of a
determination that already exists.

## Where this sits

D463 rejected one once-rounded bill for each delivery month as "a different
rounding contract with its own rules for incomplete months" and left a monthly
bill as a separate consumer. The lifecycle design describes the planned
`Invoice` as "a commercial document over eligible existing obligations";
"issuance does not create the same payable a second time and never proves
payment". The accounting boundary states that a correction "records a separate
credit linked to that invoice; it does not replace the original". This design
follows all three.

Crude invoices partition one receipt's cash cumulatively and correct by void
and reissue. Swap net settlement discharges selected determinations. Neither
is a document over daily determinations, so neither is reused as a table; the
exact money types and the command, replay and visibility patterns are.

## Selected policies

The owner selected these on 28 November 2026.

1. **Amount.** A document's amounts are the sums of its daily determinations'
   rounded amounts. Nothing is rounded again.
2. **Scope.** One trade and one delivery month, gross. Payments we pay and
   payments we receive are totalled separately and never netted. The market
   convention of one invoice for each counterparty and month, and netting,
   are terms of the agreement with the counterparty. They follow as further
   scope policies that need a typed election on the agreement.
3. **Completeness.** A bill is issued only when every gas day of the month
   that its trade's billable payments cover has a determination.
4. **Corrections.** An issued bill is immutable. A determination made after it
   goes on a linked adjustment note.

## Goals

- A complete delivery month of one trade can be previewed, reviewed and
  issued as a bill through native operations.
- A month that is not complete refuses, and the preview lists each payment
  day that has no determination.
- Each determination is on at most one document. A document's totals are
  exactly the sums of its lines.
- After a bill, later determinations of that trade and month can be issued as
  an adjustment note linked to the bill.
- The same facts under the same business time and knowledge cutoff give the
  same document. An earlier knowledge view sees the documents and
  determinations it knew.
- The database assembles and checks each document itself.
- No determination, valuation or recorded payment changes.

## Non-goals

- A bill across trades, for a counterparty or an agreement, and any netted
  amount.
- Rounding a month once, or any amount that is not a determination's.
- A partial bill for an incomplete month.
- Voiding or reissuing a document.
- A rendered or exported document, a document number series, and sending
  anything to a counterparty. Export stays explicitly unsupported (L12 of the
  physical commodity test plan).
- Linking a recorded payment to a document, payment allocation, dunning and
  disputes.
- Charges, imbalance cash-out, receipt and fuel stages, and payments governed
  by an exercise or swing right. They have no determinations.
- Accounting entries and tax.
- A screen. This increment adds the native operations and their checks. The
  Billing & corrections panel followed as its own increment; see
  [the contract](../../gas-monthly-bills.md#screen).
- Assistant operations.

## Anti-goals

- No second payable. Issuing records which determinations a document covers
  and nothing else.
- No silent omission. A payment the bill cannot cover is named on every read
  of that bill's scope, with the reason.
- No inferred netting. One signed total across pay and receive payments is
  not stored or returned.
- No rewrite. Documents and their lines are immutable.

## Scope policy

`gas_trade_month_gross_v1` is selected explicitly on every preview and issue
and is retained on the document.

- The delivery month is the calendar month of the retained native gas day.
- The trade is read at its latest revision visible at the selected business
  time and knowledge cutoff.
- A **billable payment** is a delivery-stage `consideration`, `fee` or
  `indexed_consideration` that final billing can determine: not governed by an
  exercise or swing right, and, when indexed, on a named daily or monthly
  index or on point publications.
- Every other payment of the trade is **not covered**. Reads of the scope list
  each with its reason. A trade with no billable payment has no bill.
- The **expected days** of a billable payment are the retained gas days of its
  physical source that lie in the month and inside the payment's own dates.

## Documents

A document belongs to one trade and month and has an ordinal. Ordinal one is
the **bill**. Each later ordinal is an **adjustment note** that names its
predecessor.

A line is one determination and carries that determination's contribution:
its rounded amount for an original, and the new rounded total less the
previous one for a correction. A day corrected before the bill is issued has
several lines whose sum is its current amount.

| Document | May be issued when | Lines |
| --- | --- | --- |
| Bill | No document exists; no expected day lacks a determination; at least one determination exists | Every visible determination of the trade and month |
| Adjustment note | A bill exists; at least one visible determination of the trade and month is on no document | Those determinations |

Each document retains two totals in the trade's currency: the sum of its lines
on payments we pay, and the sum on payments we receive. A payment keeps its
booked direction: a negative price or a correcting credit changes the amount
inside its own total.

Every determination on a document must belong to the trade revision's own
instrument. A month that holds determinations of an earlier instrument
refuses with `gas_bill_revision`; billing an amended trade is separate work.

Each line keeps its determination's due instant. A document states no due
date of its own.

## Operations

- `gas/bills/read`: for one trade, each delivery month with its state
  (`incomplete`, `ready`, `issued`, `adjustment_ready`), its missing payment
  days, its payments not covered, and its documents with their lines.
- `gas/bills/preview`: for one trade, month and scope policy, the document
  that would be issued, its lines and totals, its predecessor and a review
  hash; or the reason none can be.
- `gas/bills/issue`: accepts the reviewed predecessor, review hash and content
  digest with a retained operation identity and evidence. The content digest
  names the document's determinations in line order, whatever session
  reviewed them.
- `gas/bills/status`: recovers the actor's own operation outcome.

Reads carry the shared business time and knowledge cutoff. Issuing requires
working data and `AmendTrade` with trade and market read authority, as
determining does. A retried request returns its first outcome. A fresh request
for a document that already exists with the same lines, by its content digest,
returns that document and receives its own receipt. A review of any other
lines conflicts. A determination recorded between review and acceptance
refuses the stale review. A scheduled run cannot issue a document.

A document takes effect at the business time of the view that issued it. A
working view whose business time precedes a month's document cannot issue for
that month; its reads say so with `gas_bill_later_document`.

## Persistence

One migration.

- `trading.gas_bills`: trade revision, month, ordinal and predecessor, scope
  and amount policies, currency revision, the two totals, the content digest,
  evidence, actor, publication and effective instant. One document for each
  trade, month and ordinal. A commit-time check requires each document to have
  lines, to follow its predecessor, and to state the digest and totals of
  exactly those lines.
- `trading.gas_bill_lines`: document, determination and amount. A
  determination is on at most one line. Foreign keys tie the line to its
  document's trade and month and to the determination's own trade, payment
  and day.
- `trading.gas_bill_commands`: actor-owned requests and outcomes.
- Readers and the issuing writer. The writer assembles the document's lines
  and totals itself under the operation's context and requires them to equal
  the reviewed ones.

## Worked example

Synthetic. One trade receives gas on 1 and 2 January and pays a fixed 3.00;
it also receives a fee of 0.10.

| Step | Fact | Result |
| --- | --- | --- |
| 1 | 1 January determined: payment −300.00, fee +10.00 | Month incomplete: 2 January missing for both payments |
| 2 | 1 January payment corrected to −294.00 (contribution +6.00) | Still incomplete |
| 3 | 2 January determined: payment −300.00, fee +10.00 | Ready |
| 4 | Bill issued | Five lines; pay total −594.00; receive total +20.00 |
| 5 | 2 January fee corrected to +9.80 (contribution −0.20) | Adjustment ready |
| 6 | Adjustment note issued | One line; pay total 0.00; receive total −0.20 |

## Verification

Tests are written first and are independent of the implementation.

- Managed checks: assembling the example above by hand-computed totals;
  missing days; nothing to issue; mixed currency refused.
- Database checks through the native operations: the example end to end; an
  incomplete month refused with its missing days; a determination recorded
  between review and acceptance; retry and a fresh request for the same
  document; a review of other lines; a forged total or digest on a direct
  call; a determination already on a document cannot be on another; an earlier
  knowledge view and an earlier business-time view; unchanged determinations
  and valuation. Not exercised natively: a month holding an earlier
  instrument's determinations, mixed currency, a term spanning several months,
  not-covered reasons other than rights, authority revoked during a wait, and
  the refusal of a scheduled run.
- Independent adversarial review of the completeness rule, the temporal
  selection, idempotency and authority before publication.

## Known limits

- A bill for one trade is not the conventional counterparty invoice.
- A trade with a charge, a cash-out or a right-dependent payment gets a bill
  that does not cover it, and says so.
- A document is a record here. Nothing renders, numbers or sends it.
- Recorded payments are not matched to documents.
