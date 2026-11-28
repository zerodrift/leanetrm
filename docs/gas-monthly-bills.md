# Monthly gas bills

D464 adds a monthly document over one gas trade's
[final daily cash determinations](gas-cash-billing.md): a bill for a complete
delivery month, and adjustment notes for determinations made after it. A
document is a record of which determinations it covers. It creates no payable,
rounds nothing, proves no payment, and is not rendered, numbered or sent.

## Scope policy

`gas_trade_month_gross_v1` is selected explicitly on every preview and issue
and retained on the document.

- One trade, read at its latest revision visible at the selected business time
  and knowledge cutoff, and one delivery month: the calendar month of the
  retained native gas day.
- A **billable payment** is a delivery-stage consideration, fee or indexed
  consideration that final billing can determine. A charge, an imbalance
  cash-out, a receipt or fuel stage, a payment governed by an exercise or swing
  right and the retired strict daily index convention are **not covered**.
  Every read lists them with the reason (`payment_kind`, `stage`, `rights`,
  `index_convention`). A trade with no billable payment day in a month has no
  bill for it.
- The **expected days** of a billable payment are the retained gas days of its
  physical source inside the month and the payment's own dates.
- Payments we pay and payments we receive are totalled separately. No amount
  across the two is stored or returned.

The conventional invoice for each counterparty and month, and netting, are
terms of the agreement with the counterparty. They need further scope policies
and a typed election on the agreement, and are not built.

## Documents

Ordinal one of a trade and month is the **bill**. Each later ordinal is an
**adjustment note** that names its predecessor. Documents and lines are
immutable.

| Document | May be issued when | Lines |
| --- | --- | --- |
| Bill | No document exists and every expected payment day has a determination | Every determination of the trade and month |
| Adjustment note | A bill exists and a determination of the trade and month is on no document | Those determinations |

A line is one determination and carries its contribution: the rounded amount
of an original, or the new rounded total less the previous one of a
correction. A day corrected before its bill has several lines whose sum is its
current amount. A determination is on at most one document. Each line keeps its
determination's due instant; a document states no due date of its own.

A payment keeps its booked direction. A negative price or a correcting credit
changes the amount inside that payment's own total.

A month that holds determinations of an earlier instrument of the trade
refuses with `gas_bill_revision`. Billing an amended trade is separate work.

## Operations

- `gas/bills/read`: one trade's payments, covered or not, and each delivery
  month with its state, its missing payment days and its documents with their
  lines. States are `incomplete`, `ready`, `issued`, `adjustment_ready` and
  `refused` with its code.
- `gas/bills/preview`: for one trade, month and scope policy, the document that
  would be issued, its lines, totals and predecessor and a review hash; or the
  state and missing days when none can be.
- `gas/bills/issue`: accepts the reviewed predecessor, review hash and content
  digest with a retained operation identity and evidence. The content digest
  names the document's determinations in line order.
- `gas/bills/status`: recovers the actor's own operation outcome.

Reads carry the shared business time and knowledge cutoff. A determination
counts as documented only by a document visible in the same view, so an earlier
view sees the month as it was. Issuing requires working data and `AmendTrade`
with trade and market read authority, as determining does.

A retried request returns its first outcome. A fresh request that reviewed the
very lines of the document already following its predecessor returns that
document and receives its own receipt, when its trade revision, scope policy
and evidence are also the same. A review of any other lines, or other terms,
refuses with `gas_bill_conflict`. A determination recorded between review and
acceptance refuses the stale review with `gas_bill_stale_preview`. A scheduled
run cannot issue a document.

A document takes effect at the business time of the view that issued it. A
working view whose business time precedes one of a month's documents cannot
issue for that month: its reads show the month as `refused` with
`gas_bill_later_document`.

The native writer assembles the document itself: the determinations of the
trade and month that its context knows and no document carries. It requires
those lines and their two totals to equal the reviewed ones, and refuses an
incomplete month, an empty document and a mixed currency. At commit the
database also requires every document to have lines, to follow its predecessor
and to state the digest and totals of exactly those lines.

## Screen

Open **Scheduling → Gas → Trade deliveries**, select the booked instrument and
choose **Billing & corrections**. **Monthly bills** follows the daily
determinations it gathers.

- Each payment of the trade is listed as covered or not covered, with the
  reason.
- Each delivery month is offered with its state. An incomplete month states
  how many payment days lack a determination; reviewing it names them and
  offers nothing to issue. An issued month, and a month the read refuses for
  this view, cannot be selected.
- Select the month, the bill scope and the review evidence, then review. The
  review states the document's kind, its two totals and every line. Editing an
  input, asking for another review or refreshing withdraws the review.
- Issuing sends exactly the reviewed request through the workspace's retained
  request card, so an unknown outcome can be checked or retried.
- Issued documents and their lines are listed below with their sequence, the
  document's retained ordinal within its trade and month. It is not a document
  number. A read-only or historical view shows them and offers no review.
- Recording a determination or issuing a document reads both panels of the tab
  again. The inputs of the panel that did not send the request are kept.

The screen draws what the reads return and computes no amount. A state,
document kind or scope policy it does not know is shown as the read named it.
`gas/bills/read` also returns `canWrite`: the context is working data and the
account may amend trades. It is not a promise that a document can be issued;
the writer decides.

Limits of the screen:

- A working view whose business time precedes the trade's latest revision can
  review a document that the writer then refuses with `gas_bill_conflict`.
  The read does not say so beforehand.
- A line is labelled with the payment the current trade revision has at the
  line's cash slot. After an amendment that changes the trade's payments, a
  line of an earlier document can carry the current payment's label.
- A retained request whose original never committed, and whose month another
  account has since changed, can only be checked or retried. It cannot be
  discarded from the screen. Daily determinations behave the same.

## Persistence

Migration 0413 adds `trading.gas_bills`, `trading.gas_bill_lines` and
actor-owned `trading.gas_bill_commands`. Foreign keys tie a line to its
document's trade and month and to the determination's own trade, payment and
day. The runtime has no direct access to these tables; it reads and writes
through the functions above.

## Qualification and remaining work

See [verification](verification.md#monthly-gas-bills-d464). There is no
assistant operation. Counterparty-level bills,
netting, a rendered or exported document, document numbering, matching
recorded payments to documents, voiding, partial bills and accounting remain
separate consumers.
