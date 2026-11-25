# Imbalance cash-out on a gas trade: design

Status: built under D454 on 24 November 2026, from the owner-approved second
review. It builds on the implemented
[Instrument schedule and gas pricing design](2026-11-23-instrument-schedule-and-gas-pricing-design.md)
(D446–D447). See [local qualification](../../verification.md#imbalance-cash-out-d454).

## Intent

A pipeline imbalance is the gas an operator says we are owed, or owe, at a
point. On a cash-out pipeline it is settled in money each month.

The system records the operator's reported balance for a trade's delivery
today, but as operating evidence only. It has no value in the trade. This
design lets a gas trade state an imbalance cash-out under one selected
profile, `daily_index_cashout_v1`: each gas day's imbalance is priced at that
day's published index. It is two index payments, one received on days gas is
owed to us and one paid on days we owe. Their Side is the existing index
payment Side.

Other cash-out methods, such as a monthly average price or prices that step
with the size of the imbalance, are other profiles and are not designed here.

## Where this sits

The review of physical gas products grouped the gaps into six building blocks.
The fifth was listed as "a balance position derived from scheduled less
allocated quantity". This document replaces that entry. The balance is the one
the operator reports. Nothing is derived from scheduled and allocated
quantities.

## Goals

- A gas trade can state an imbalance cash-out on one of its physical sides.
- Each gas day's quantity comes from the operator's reported balance for that
  side's service accounts, under rules the trade states.
- A day whose quantity cannot be known is unassessed, and every total built
  from it says so.
- The same statements, under the same business time and knowledge cutoff, give
  the same value on every route.
- Delivery valuation and ordinary payment entries are unchanged, and no trade
  booked before this change values differently.

## Non-goals

- A balance that is rolled forward and made up in kind. It is gas owed, not a
  payment, and stays the operator balance that owned holdings already value.
- Deriving a balance from scheduled and allocated quantities.
- The cash of an imbalance transfer between accounts. It stays outside the
  portfolio, as today.
- Other cash-out profiles.
- An imbalance on a transport contract. No such link exists.
- Linking a statement to an owned operator balance. See
  [Known limits](#known-limits).
- A charge on a balance over time. Park and loan has its own.
- Forecasting an imbalance for a day that has not ended.
- Changing the rule that a recorded payment matches its entry's direction.

## Anti-goals

- No new Side type, no Side that reads another Side, and no signed quantity.
- No balance inferred from an allocation residual or from another trade.
- No unknown counted as zero, in a row or in any total.
- No daily quantity taken from a difference that spans more than one day.
- No older statement revived when its latest revision is withdrawn or
  ineligible.
- No guess at what a statement reports or how accounts net. The trade states
  both.
- No statement entering a view earlier than its knowledge time.

## Implementation baseline

File references are starting points; the builder confirms each.

| Piece | Behavior today | Where |
| --- | --- | --- |
| Service account | Created after a trade is booked, against one of its delivery obligations, and kept with it for good. A side may have several accounts. The holder is our legal entity or the counterparty | `trading.gas_service_accounts`, migration `0206:11`, `:29`, `:369` |
| Amendment | A trade with a service account, or one that has started, refuses amendment | migration `0209:94-96` |
| Statement | The operator's signed balance for one account and gas day, accepted only after the day ends, under the service terms covering that day, as revisions that are provisional, final or withdrawn. Positive means gas owed to the holder | migration `0206:53`, `:325`, `:379-393`, [delivery operations](../../gas-delivery-operations.md) |
| What a statement reports | No document says whether it is the day's own imbalance or a running total. Transfers are scoped to one account and day | migration `0206:106-112`, `:332-339` |
| Statement policy | Final statements only, or provisional and final. A missing, withdrawn or ineligible statement is unassessed, never zero | `GasDeliveryOperations.cs:47-61` |
| Current balance | The reported balance plus accepted transfers not yet included in it | `GasDeliveryOperations.cs:49-61` |
| Permission | Reading a statement needs operations permission. Trade visibility does not grant it | migration `0206:133-139` |
| Facts read in valuation | Delivery quantities are read under the financial knowledge cutoff by anyone who can read the trade and its market data, and by a risk worker inside its leased scope | migration `0390:54`, D445 |
| Held day quantities | Complete, nonnegative quantities of a physical side, with a positive contract quantity. A missing day throws | `PhysicalDeliveryQuantities.cs:33-56`, `:78-79` |
| Payment record | Belongs to one entry. Its amount and every allocation must have that entry's direction. It needs one ended gas day | migration `0216:88-126`, `:194-204`, `PhysicalLifecycle.cs:49-60` |
| Negative price | An index payment's row at a negative price has the opposite sign to its entry, and a payment of that sign is refused | migration `0216:194` |
| Row state | A row of zero reads as "no amount due" | `PhysicalLifecycle.cs:282` |
| Owned operator balance | A separate signed claim or debt, valued at a mark, not linked to any statement | [owned holdings](../../gas-owned-holdings.md), migration 0265 |
| Ticket | The Imbalance tab is a placeholder | `GasTicket.cs:77` |

## Approaches

1. **An imbalance cash-out on the gas trade, as two entries (chosen).** It
   keeps to the line of trade, Instrument and Side, and each entry has one
   fixed direction.
2. **Extending the owned operator balance.** Set aside by the owner. It keeps
   the position outside any trade.
3. **Valuing statement balances directly.** Rejected. It would be a third
   position for the same gas.

## Design

### Two entries with fixed directions

An imbalance cash-out is two payment entries on one physical side:

- **Owed to us:** received, on days the balance is positive.
- **Owed by us:** paid, on days the balance is negative.

Each has the existing index payment Side, with one row for each gas day of the
physical side's term, and its own spread. The trade states, once for the pair:

| Field | Meaning |
| --- | --- |
| Profile | `daily_index_cashout_v1` |
| Side | The physical side whose service accounts they follow |
| Index | A named index, published daily |
| Spreads | A fixed adder for each of the two entries. Either may be zero |
| Payment day | A day, 1 to 28, of the following month |
| Statement policy | Final statements only, or provisional and final |
| Statement basis | `daily_imbalance_v1` or `month_to_date_balance_v1` |
| Account netting | `net_accounts_v1` or `each_account_v1` |

These terms are part of the Instrument's definition and identity. A trade
without them keeps its identity.

### A distinct quantity source

These entries do not use the held day quantities of delivery. Those are
complete quantities of a physical side, and delivery valuation and ordinary
following payments keep using them unchanged.

An imbalance entry has its own input: for each gas day, a state and, when
assessed, an absolute nonnegative quantity. The states are:

| State | Meaning |
| --- | --- |
| `assessed` | The day's quantity is known. It may be zero, which is a real zero |
| `unassessed` | The day has ended and its quantity cannot be known |
| `not_applicable` | The day has not ended. No imbalance exists yet |

A row's amount is its quantity times its price. The quantity is absolute: it
does not grow with the trade's size.

### From statements to a day's figure

For each account and gas day, in this order:

1. **Resolve the account for that day.** Take the side's service accounts
   held by our legal entity, and the service terms covering that gas day, as
   they stood at the valuation's business time and knowledge cutoff. The set
   of accounts visible today is not used.
2. **Take the latest revision first.** Take the statement's latest revision
   that is effective by the business time and known at the cutoff. Then test
   it against the statement policy. A withdrawn or ineligible latest revision
   makes that account and day unassessed. An older revision is never used in
   its place.
3. **Take the current balance:** the reported balance plus accepted transfers
   not yet included in it.

A side with no account for a day, or any account unassessed for it, makes the
day unassessed.

### The statement basis

- **`daily_imbalance_v1`:** an account's balance for a day is that day's own
  imbalance.
- **`month_to_date_balance_v1`:** an account's balance is the running total
  for the calendar month. Its figure for a day is its balance less its balance
  on the immediately preceding calendar day. That needs the preceding day to
  be assessed. The only day that needs no predecessor is the first day of a
  calendar month.

  A day whose preceding day is missing is unassessed, because the difference
  would span more than one day and belongs to no single day's price. A side
  whose term starts after the first of a month is therefore unassessed for the
  rest of that month under this basis: its first statement may include earlier
  days.

  A corrected statement changes its own day's figure and the next day's.

  An accepted transfer in a month makes that month ambiguous under this basis.
  A trade with one refuses its imbalance value for that month, with its own
  reason.

### Account netting

Entries have different spreads, so the order of netting changes the value.

- **`net_accounts_v1`:** add the accounts' figures for the day, then split the
  total. A positive total goes to the entry owed to us, a negative one to the
  entry owed by us.
- **`each_account_v1`:** split each account's figure, then add. The entry owed
  to us takes the sum of the positive figures, and the entry owed by us the
  sum of the sizes of the negative ones.

With accounts of +100 and −100, an index of 3.00, and spreads of 0.10 owed to
us and 0.20 owed by us, the first gives 0 and the second gives −10.

### Value and cash

A day's amount is its quantity times that day's index plus the entry's spread.
Before the index is published it is a labelled forecast. Cash is earned when
the day is assessed and its index is published.

- **A zero price** gives a zero amount, a real zero.
- **A corrected price** changes the amount as it does for any index payment,
  and the row shows the difference as underpaid or overpaid.
- **A negative index plus spread** gives an amount of the opposite sign to its
  entry. It is valued with its true sign. Its payment cannot be recorded,
  because a payment must match its entry's direction. Every index payment has
  that limit today, and this design does not change it.

### Unassessed in totals and risk

An unassessed day has no amount. A total that includes one is a subtotal, and
it must say so. The entry, the trade, the portfolio and the risk result each
carry an incomplete-coverage marker with the count of unassessed days whenever
one is present. No screen or report shows such a total as complete.

### Revisions after payment

- **A revised quantity of the same sign** shows the row underpaid or overpaid.
- **A revision that changes the sign** drops the paid entry's row to a real
  zero with its payment overpaid, and gives the other entry the new amount.
- **A withdrawal** makes the day unassessed. Its recorded payment remains a
  fact. The row shows "unassessed, payment recorded". It is not shown as
  overpaid, and its accrual is unknown, not zero.

### Settlement

Each entry's month is due on the stated day of the following month. A payment
record belongs to one entry, so:

- **Gross settlement** is two payments, one on each entry.
- **Net settlement** is one recorded fact with one net amount. It produces a
  payment on each entry, in that entry's direction, whose sum is the net
  amount. Both carry the settlement's identity, so cash reporting sees one
  movement. In the worked example, one receipt of 1,190 clears +1,830 on one
  entry and −640 on the other.

### Reading statements in valuation

Statements are read in valuation by one read for a trade and binding, under
the financial business time and knowledge cutoff. The draft opens it to anyone
who can read the trade and its market data, and to a risk worker inside its
leased job scope, as D445 does for delivery quantities. Operating screens keep
their own permission. This widens who can see a value drawn from statements.
The owner confirmed it on 24 November 2026.

### Worked example

Synthetic, no discounting, both spreads zero, one account, the index
published. Statements are final.

| Day | Reported | Index | Daily basis: quantity, amount | Month-to-date basis: quantity, amount |
| --- | --- | --- | --- | --- |
| D1, first of a month | +300 | 3.00 | +300, +900 | +300, +900 |
| D2 | +300 | 3.10 | +300, +930 | 0, 0 |
| D3 | −200 | 3.20 | −200, −640 | −500, −1,600 |
| D4 | none | 3.30 | unassessed | unassessed |

Daily basis: +1,830 owed to us and −640 owed by us, a subtotal of +1,190 with
one day unassessed. Month-to-date basis: +900 and −1,600, a subtotal of −700
with one day unassessed.

If D2 had no statement, under the month-to-date basis D2 and D3 would both be
unassessed, and the subtotal would be +900 with three days unassessed. Under
the daily basis only D2 would be.

A trade of twice the contract size with the same statements gives the same
imbalance amounts.

### Booking, ticket and display

Composition booking accepts an imbalance cash-out and creates its two entries.
They count as two of the trade's seven payment Sides. The ticket's Add side
gains "Imbalance cash-out", one field per row, with every choice required and
nothing prefilled. The Imbalance tab shows, for each gas day, the reported
balance and its status, the day's state, its quantity, the index, the amount
and which entry carries it, and the count of unassessed days above them.

### Persistence and admission

The terms are stored with the trade's other entries. Database admission checks
that the side is a physical side of the trade, that the index has daily
coverage for the term, and that the pair is complete. It does not require a
service account. A net settlement is stored as one fact linked to its two
payments.

### Risk and reports

A day that is assessed and whose index is not yet published moves with that
index, so the entries carry price risk on those days only. A changed statement
with no price change lands in the P&L residual, as a quantity change does
under D445. Risk results carry the incomplete-coverage marker.

### Known limits

- **Double counting.** Nothing links a statement to an owned operator balance.
  If both are kept for the same gas, both are valued, and this design cannot
  detect it.
- **Negative prices.** Cash for a day at a negative price cannot be recorded.
- **Month-to-date after a mid-month start.** The first month is unassessed.
- **Retired service terms** can leave a day unassessed for good, because a
  late final statement is then refused.

### What another commodity reuses

The distinct quantity source with its three states, and incomplete coverage in
totals, are on the shared base. Another product supplies its own statements
when it has a consumer.

## Build order

Each stage leaves every check passing.

1. **The quantity source and its rules,** at the managed level: both bases,
   both netting rules, the three states, and incomplete coverage in totals.
2. **Statements in valuation:** the read, the store, booking and net
   settlement.
3. **The ticket and the Imbalance tab.**

## Verification

- **Unchanged numbers.** Every existing check passes without a changed
  expected value. A trade without an imbalance cash-out keeps its identity.
  Delivery valuation and ordinary payments give the same results beside a
  cash-out as without one.
- **Oracle.** Every cell of the worked example under both bases, the missing
  D2 case, the netting example, and the trade of twice the size.
- **Month-to-date.** A month end; a missing preceding day; a term starting
  mid-month; a corrected earlier day changing the next day's figure.
- **Revisions.** A withdrawn latest revision over an older final one; a newer
  provisional revision under the final-only policy; each case of
  [Revisions after payment](#revisions-after-payment).
- **Accounts.** No account yet; an account created or retired during the
  term; two accounts with one unassessed; a counterparty-held account.
- **Coverage.** The marker and its count on the entry, the trade, the
  portfolio and the risk result.
- **Prices.** A zero price; a corrected price; a negative price valued with
  its sign and refused for payment.
- **Settlement.** Gross, and one net amount clearing both entries.
- **Transfers.** Netted under the daily basis; refused for that month under
  month-to-date.
- **Time.** A statement known later than the cutoff, and one effective later
  than the business time.
- **Routes.** Preview, reads, the Blotter and risk agree.
- **Admission.** API and database agree on every accepted and refused booking.
- **Ticket.** Browser checks for the side, booking and the Imbalance tab.
- **Review.** One independent adversarial review of temporal behavior and of
  the authority to read statements in valuation.

## Decisions to record

- A new decision for the imbalance cash-out under `daily_index_cashout_v1`. It
  revises D339's statement that a statement balance has no portfolio value,
  for trades that state one, and records who may read a statement's value.

## Confirmed design calls

The owner-approved calls are recorded in D454.

1. Anyone who can read the trade sees its imbalance value. Confirmed by the
   owner on 24 November 2026.
2. The trade states what a statement reports and how accounts net. Neither is
   assumed.
3. Under the month-to-date basis a day needs its preceding day, and only the
   first day of a month needs none.
4. An unassessed day gives a subtotal that is marked incomplete everywhere.
   Valuation does not refuse.
5. A withdrawal after payment leaves the payment as a fact and the accrual
   unknown.
6. A net settlement is one fact that produces a payment on each entry.
7. A negative price is valued with its sign, and its cash cannot be recorded
   until the payment rule itself is changed.
8. Only cash-out is on the trade. A rolled balance stays the owned operator
   balance.
9. A day that has not ended has no imbalance. None is forecast.
10. A statement is an absolute quantity and does not scale with the trade.

## Left to the builder

- How the quantity source reaches the Side, and how the marker is carried
  through portfolio and risk results.
- The shape of the read, the settlement fact and their authority checks.
- Field names, labels, codes and migration numbering.
- The order of work inside each stage.
