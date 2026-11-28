# Final daily billing of index-priced gas payments: design

Status: built under D463 on 27 November 2026. The owner selected the three
policies under [Selected policies](#selected-policies); the rest is the
maintainer's design, corrected after an independent review. See
[the contract](../../gas-cash-billing.md#indexed-profile-d463) and
[local qualification](../../verification.md#indexed-gas-billing-d463).

## Intent

A gas trade can pay on a published index: each gas day's quantity is priced at
that day's index, or at the index of the month the day falls in, plus a stated
spread. Valuation already earns such a payment once the fixing is published.
Final billing does not. `gas/billing/read` lists every indexed payment as an
unavailable choice, and `security.record_gas_billing` refuses it with
`gas_billing_profile`.

This design adds one explicitly selected billing profile,
`gas_final_daily_indexed_cash_v1`. It determines one gas day's final cash for
one indexed payment from the final accepted quantity and the final published
fixing, and it lets a later corrected fixing be billed as a reviewed
correction.

## Where this sits

D338 added final daily cash determinations for fixed-price payments. D459
added the entry profile (share, daily band or shortfall, payment-specific
dates) and scoped daily processing, and left "indexed, monthly, multisource,
receipt/fuel and right-dependent billing, invoices, accounting and automatic
correction policy" as separate consumers. This is the first of those: indexed.
D447 defines the payment terms this profile bills. D360 states that a
valuation accrual is derived at read time and is not a bill.

## Selected policies

The owner selected these on 27 November 2026, and added point publications to
the first on 28 November.

1. **Index kinds.** Payments on a named daily index (`named_index_daily_v1`),
   on a named monthly index (`named_index_monthly_v1`) and on point
   publications (`point_publications_v1`). All three retain one published
   fixing for each gas day. Point publications were first left out on the
   maintainer's mistaken reading that they price from a different source;
   their payment rows retain the same fixing binding.
2. **Rounding.** One determination for each payment and gas day, rounded once
   to the currency's minor unit, ties to even
   (`currency_minor_units_to_even_v1`). The sum of a month's daily bills can
   differ from a once-rounded monthly total. A monthly invoice total is a
   different contract and is not inferred.
3. **Corrected fixings.** A corrected final fixing can be billed as a new
   linked determination with its own typed cause and explicit due instant.
   The original stays immutable. Nothing is restated automatically.

## Goals

- A completed gas day of an eligible indexed payment can be previewed,
  reviewed and determined through the existing billing operations.
- The amount is the taken final quantity, times the payment's share, times
  the final fixing plus the booked spread, times the quote multiplier and the
  booked direction, rounded once.
- The determination retains the exact fixing observation it used, by foreign
  key.
- A missing, preliminary or ambiguous fixing refuses. No forecast, mark or
  earlier fixing stands in for it.
- The same inputs under the same business time and knowledge cutoff give the
  same bill. A fixing published or corrected after the cutoff does not enter
  an earlier view.
- The database checks the amount independently from the retained terms, the
  final quantity and the fixing it selects itself.
- No fixed-price determination, and no trade's valuation, changes.

## Non-goals

- The retired `daily_published_index_v1` convention. It keeps refusing with
  `gas_billing_profile`.
- A monthly or cumulative bill, its rounding, and an invoice or credit note.
- Automatic billing. Scheduled daily processing keeps reporting indexed
  payments as terms it does not bill; an indexed day is billed by a reviewed
  operation only.
- Payments controlled by an exercise or swing right. They keep refusing with
  `gas_billing_rights`.
- Imbalance cash-out payments, receipt and fuel stages, and charges.
- Recording that cash was paid, or linking a recorded payment to a
  determination.
- Reconciling a bill with the valuation's accrued amount. See
  [Known limits](#known-limits).
- Business-day adjustment of the payment date. The booked
  `next_month_day_utc_v1` instant is used as booked.
- Correcting a booked spread or index. That is a trade amendment.

## Anti-goals

- No market close is required or accepted. Billing keeps its own processing
  clock (D459). The fixing is read from published observations under the
  operation's knowledge cutoff.
- No silent price. A day without exactly one eligible final fixing is not
  billable.
- No second arithmetic. The managed calculation reuses `ExactQuantity`,
  `UnitPrice`, `ExactMoney`, `AmountFormat`, `QuantityPart` and
  `CashRestatement`.
- No rewrite of a determination when a fixing is corrected.

## Eligible payments

A payment is billable under this profile when all of these hold on the latest
trade revision visible at the selected business time and knowledge cutoff:

- its role is `indexed_consideration` and its stage is `delivery`;
- its `trading.gas_index_cash_terms` row states `named_index_daily_v1`,
  `named_index_monthly_v1` or `point_publications_v1`;
- neither it nor its physical source is controlled by an exercise or swing
  right;
- the gas day lies inside the payment's own delivery dates and has ended at
  the selected business time.

Share, daily band or shortfall, and payment-specific dates are consumed as the
entry profile consumes them (D459). The fixed profiles keep refusing indexed
payments, and this profile refuses fixed payments: each payment has exactly
one applicable family of profiles.

## Quantity

Unchanged from D338 and D459. The caller selects
`final_allocated_billing_v1` or `final_facility_allocated_billing_v1`. The
final accepted quantity of the payment's source obligation for that gas day is
required. A quantity part is taken from it against the held trade basis.
Reconciliation with contract and scheduled quantities is reported as today
and does not change the amount.

## Fixing

Each retained payment row already names its fixing: the booking stored, per
gas day, the index, the observation interval, the series and the source
(`trading.side_schedule_bindings`, port `fixing`). For a daily index the
interval is the gas day's contract; for a monthly index it is the calendar
month containing the day, so every day of a month names the same fixing. A
payment on point publications states no index of its own: its location's dated
periods selected a daily or monthly index for each gas day at booking, and that
day's row retains the result.

The profile selects the fixing under one policy, `final_fixing_head_v1`:

1. Candidates are observations of that exact series and source with role
   `index_fixing`, observed no earlier than the fixing definition's
   `fixing_at` and no later than the selected business time, whose knowledge
   event lies within the operation's market prefix and knowledge cutoff.
2. A candidate that has a visible successor (`corrects_id`) under the same
   limits is not a head.
3. Exactly one head must remain, and it must be `final`. None, more than one,
   or a preliminary head refuses with `gas_billing_fixing`.

This is the selection the option-expiry lifecycle action already applies to a
settlement observation, on a different role and series. It does not read a
close or its pins.

The billed unit price is the head's value plus the booked spread, in the
payment's retained quote. Neither is rounded.

## Amount

For gas day *d* of payment *p*:

```text
taken   = part(final quantity of d, held basis)   or the final quantity
exact   = taken × share × (fixing + spread) × quote multiplier × direction
rounded = exact to the currency's minor units, ties to even
```

A correction contributes the new rounded total minus the previous rounded
total. A negative fixing, a negative price after the spread, and an explicit
zero quantity keep their signed arithmetic.

## Due instant

An original is due at the payment row's retained instant: the booked day of
the month after the gas day, at UTC midnight. A correction supplies an
explicit UTC due instant, as today.

## Corrections

A determination now has two inputs that can change: the final quantity source
and the fixing. The predecessor retains both by foreign key, so the database
can tell which changed.

| Quantity source | Fixing | Outcome |
| --- | --- | --- |
| same | same | Already determined; the first result is acknowledged |
| changed | same or changed | Correction with `late_actuals`, `quantity_correction` or `allocation_correction` |
| same | changed | Correction with the new cause `index_fixing_correction` |

The cause must match the row. `index_fixing_correction` is refused when the
quantity source changed, and a quantity cause is refused when only the fixing
changed. A correction is recorded when an input changed even if its cash
contribution is zero. Quantity policy, rounding policy and precision cannot
change inside a chain, as today. An indexed payment has only this profile, so
its chain cannot change profile either.

The writer records a new determination only on a fixing that nothing has
corrected, whatever the supplied context knows, as it requires the quantity
head nothing has revised. A context that does not yet know a correction selects
the superseded fixing and is refused.

## Operations

No operation is added.

- `gas/billing/read` marks eligible indexed claims as supported, offers this
  profile for them and the fixed profiles for fixed payments, lists
  `index_fixing_correction` among the causes, and states each indexed claim's
  index, convention and spread.
- `gas/billing/preview` additionally returns the selected fixing (observation
  identity, value, observed instant, the observation it corrects), the spread
  and the billed unit price. The review hash covers the fixing identity, so a
  fixing corrected between review and acceptance refuses the stale review.
- `gas/billing/determine` accepts the reviewed quantity source, the reviewed
  fixing (`fixingObservationId`) and the predecessor. Request retries and
  semantic retries by another authorized actor keep their existing behavior.
- Authority is unchanged: `AmendTrade` with trade and source read authority.
  Reading the fixing requires current market read authority.

The Billing & corrections panel shows the fixing, spread and unit price in the
review and offers the new cause. It performs no calculation.

## Persistence

One migration.

- `trading.gas_cash_determinations` gains a nullable `fixing_observation_id`
  with a foreign key to `market_data.observations`. It is present exactly when
  the profile is `gas_final_daily_indexed_cash_v1`.
- The table allows one determination for each quantity source of a payment
  day today (two unique constraints). An indexed day can be determined again
  on the same quantity source with a corrected fixing, so for indexed rows the
  uniqueness becomes one determination for each pair of quantity source and
  fixing. Fixed rows keep today's rule.
- The profile and cause constraints admit the new values.
- The determine request names the reviewed fixing, `fixingObservationId`,
  beside the reviewed quantity source. It is required for this profile and
  refused for the fixed profiles, whose requests keep their exact retained
  form. A repeated request is recognised by its quantity source and fixing
  together. The scheduled writer builds no such request, so it cannot bill an
  indexed payment.
- `security.record_gas_billing` gains the indexed branch: it requires the
  eligible terms, selects the fixing head itself under the operation's
  context, requires it to be the reviewed observation, and checks the supplied
  exact amount by cross-multiplication against the taken quantity, share,
  fixing plus spread, multiplier and direction before checking the rounding.
- `security.read_gas_billing_components` returns the index terms of indexed
  payments, and a reader returns the fixing head for one payment day under the
  same checks.

Existing determinations and commands are not rewritten.

## Worked examples

All synthetic. Currency precision two.

| Case | Inputs | Exact | Rounded | Contribution |
| --- | --- | ---: | ---: | ---: |
| Daily original, pay | 98 final, fixing 3.30, spread 0.05 | −328.30 | −328.30 | −328.30 |
| Its fixing corrected | 98 final, fixing 3.31, spread 0.05 | −329.28 | −329.28 | −0.98 |
| Then quantity corrected | 100 final, fixing 3.31, spread 0.05 | −336.00 | −336.00 | −6.72 |
| Tie | 33 final, fixing 3.105, spread 0 | −102.465 | −102.46 | −102.46 |
| Monthly index, share | 80 final, share 0.4, fixing 3.00, spread 0 | −96.00 | −96.00 | −96.00 |
| Band 30 to 70 of 100 | 80 final, fixing 3.00, spread −0.25 | −110.00 | −110.00 | −110.00 |
| Negative price, receive | 50 final, fixing −0.10, spread 0 | −5.00 | −5.00 | −5.00 |

## Verification

Tests are written first and are independent of the implementation's
arithmetic.

- Managed checks: the examples above with independently computed rationals;
  refusal of a projected or absent price, a unit or currency mismatch and an
  incomplete day.
- Database checks through the native operation: forged amounts, another day's
  fixing as the reviewed identity, a superseded fixing under a context that
  does not know its correction, each wrong cause for each row of the
  correction table, a zero-contribution correction on a twice-corrected
  fixing, stale review, retry and cross-actor semantic retry, and unchanged
  fixed-profile behavior. A preliminary head and two unrelated originals on
  one series are refused by the selector but are not exercised natively: the
  synthetic fixture has no way to publish either.
- Temporal checks: an earlier knowledge view keeps the original fixing and
  determination after a correction is published and billed.
- Browser check: review and determine an indexed day, then its fixing
  correction, in Billing & corrections.
- Scheduled processing: discovery over a book holding an indexed payment
  reports every day of it as terms it does not bill, and neither discovery nor
  the operating preview accepts this profile.
- Independent adversarial review of the temporal selection, the numerical
  oracle and the authority checks before publication.

## Known limits

- A bill and the valuation's accrued amount can differ. Valuation uses the
  membership's saved quantity policy and its own market context; billing uses
  the final accepted quantity and `final_fixing_head_v1`. This design does not
  reconcile them.
- A month's daily bills are not a monthly invoice.
- A fixing corrected after a day is billed changes nothing until someone
  reviews the correction. Nothing lists such days in this increment beyond the
  preview showing that the head differs from the billed fixing.
- A payment whose monthly index publishes after the month cannot be billed
  until then, although its gas days have ended.
- The review hash of every billing preview changes with this increment. A
  fixed-price review in flight across the upgrade refuses as stale and is
  previewed again.
