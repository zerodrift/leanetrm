# Financial swap quantities and recurring payments

The original `independent_side_financial_swap_v2` booking contract remains a
one-payment contract per side. Its external notional applies once to each side's
own delivery period. Price observation counts and payment dates do not repeat it.
Existing definitions, events and exact revisions retain that meaning.

`independent_scheduled_financial_swap_v1` adds independently authored monthly
sides. Each has one to 120 complete contiguous calendar months, one exact index
and quote basis within a floating side, and an explicit price and payment rule
for every period. The two sides may have different ranges, numbers of months,
quantities, observation conventions and payment dates. Fixed/floating and
floating/floating compositions use opposite membership directions. Cross-currency
and cross-commodity composition remain unsupported.

The managed flow remains Instrument → Side → SideSchedule → side PV. A scheduled
side composes qualified primitive monthly plans into its own ordered payment
records. Those records carry the normalized allocated quantity, observation
applications, resolved fixed/actual/projected prices, payment identity and discount
factor. Instrument PV sums signed side contributions; external trade quantity
scales that result once. Inspection consumes the same valued payment records.

## Quantity contracts

| Basis | Explicit input | Managed resolution |
| --- | --- | --- |
| `period_total` | Positive amount for every period and an exact quantity reference | Apply that amount once to that period |
| `calendar_day_rate` | Positive amount per calendar delivery day for each period | Multiply by the number of dates in that half-open delivery month, including weekends and holidays |
| `contract_total` | One positive total for that side's whole contract and a required allocation | Allocate by equal monthly periods or by calendar days; retain rational quantities exactly |
| `selected_interval_power` | Positive MW for every period, exact market clock, delivery product and any consumed holiday calendar | Integrate MW over selected actual hourly UTC intervals to MWh |

Compatible energy units use the existing exact unit converter. A source quantity
reference never selects an application basis. MW is accepted only by the power
basis; it is not an ordinary energy-unit conversion. Changing a unit or basis in
the ticket clears the amounts that would otherwise be reinterpreted.

Each reusable side has an independently normalized period vector whose shares sum
exactly to one. Instrument membership retains the second side's total divided by
the first side's total as a positive reduced rational ratio. The trade binding
holds the first side's canonical total. Fractions such as one third are not
rounded into decimal shares. Authored totals/rates/units/allocation choices belong
to the retained trade revision, independently of reusable normalized pricing.

## Price and payment independence

The versioned price application is `period_average_times_quantity_v1`: each
period's contractual observation average or fixed price multiplies that period's
resolved quantity. Quantity allocation does not choose observation weights. A
monthly published index and the qualified daily observation conventions retain
separate fixing boundaries, required sources and averaging applications. Each
constituent uses the existing fixing-resolution and connected-pricing operations.
Due missing fixings fail; ordinary risk freezes resolved actuals. Later contexts
may accept eligible final corrections, while original context pins replay.

Power entry currently supports all actual hours or an explicit local weekday
block with holiday exceptions. The clock reference and captured rule payload,
actual UTC intervals, operating dates and exact interval energies are retained.
Reopening uses retained quantities and intervals instead of today's zone rules.
The hourly compiler's existing 2000–2099 and clock-transition qualification bounds
apply. Empty or unsupported selections fail explicitly.

An unweighted daily average of prices 10 and 20 over deliveries of 23 and 25 hours
is 15. An expressly agreed average-price-times-energy contract for 1 MW pays 720.
An interval-price-times-energy contract would pay 730. This version explicitly
implements the former relationship; it does not infer the latter from MW, DST,
a mask or a curve. Independent explicit observation weights remain distinct from
power delivery quantities.

Monthly payment rules generate each period's date separately. The qualified
frequency is monthly; contractual aggregation into quarterly payments is unsupported.
Trade-owned settlement may explicitly discharge multiple eligible gross contributions
under `explicit_same_due_swap_net_v1`; this does not alter the side schedules. Settled/outstanding state is supplied by the separately enrolled
[swap settlement lifecycle](swap-settlement.md). Pure instrument valuation at or
after any payment date retains the explicit `side_payment_due` refusal.

Agreed fixed pricing requires every entered price, including supported zero and
negative prices. Par mode calculates one common fixed rate from the independently
resolved floating PV and the sum of the fixed periods' quantity-weighted discount
annuities. Only par mode accepts precision, with an explicit ties-to-even policy.
The resulting concrete rounded prices are reviewed and booked as agreed terms.

## Persistence, review and editing

Migration 0106 retains normalized side-period memberships through exact primitive
side foreign keys, rational allocations and an explicit scheduled instrument
version. Separate immutable trade authoring rows retain basis, original amounts,
unit references, allocation and delivery rules. Power clock evidence is retained
once per side revision, with per-period duration/energy and exact UTC interval
rows. Trade-owned rows use the owning revision's effective instant; all new read
paths retain current reference authority and historical publication cutoffs.

Working previews and booking share certified read preparation and managed pricing.
Writes retain current authorization, expected-head checks, version checks,
publication fencing and operation identity. Exact retries return the retained
outcome before recalculation. Existing single-period trades remain on their old
contract; no implicit conversion to a scheduled contract is performed.

Scheduled amendments currently stop once any retained fixing or payment is due.
Partially fixed valuation/replay is supported, but amending already fixed or
processed obligations needs a separate lifecycle operation. Valuation never
rewrites posted invoices or settlement records.

The primary review shows resolved period quantities, direction, contractual
prices and actual/projected states, signed trade cash, dates, discount factors and
trade PV. Delivery-hour evidence and retained UTC intervals remain inspectable.
Normalized calculations and technical lineage are expandable. Dependent edits and
context changes invalidate preparation and confirmation, including async replies.
The blotter exposes no misleading single quantity or mark for a scheduled
instrument; a fixed price is shown only when it is common to every fixed period.
Its date range is an envelope of the independently valued side periods, and
multiple projection markets are identified together rather than attributed to the
first month alone.

## Synthetic numerical examples and verification

The executable checks use original synthetic inputs:

- Pay 100 and 200 units at 3; receive 150 at 3.1 and 100 at 3.155: cash/PV without
  discounting is −119.5. Shifting the receive periods to prices 3.155 and 3.21
  gives −105.75, without changing the pay side.
- Rates of 10 units per calendar day resolve to 310 and 280 for January/February
  2021; a 590-unit total explicitly allocated by calendar days gives those same
  quantities. January/February 2044 checks retain 31/29 days.
- A 100-unit total allocated over three months retains `100/3` in each month.
  Converting synthetic energy units with an explicit factor of four preserves PV.
- A London October/November 2021 all-hours contract has 745/720 selected hours.
  Pay 2 MW at 40 and receive 1 MW at 45/50: signed cash is −59,600, −57,600,
  33,525 and 36,000, totaling −47,675. Reopening retains those quantities/PV.
- Independent rate checks reconcile cash × each dated discount factor to trade
  PV and solve par using unequal notionals and payment annuities.

Relevant checks are `--scheduled-swaps` in the market-data checks, the rate checks,
and the owned `tools/run_preview.py --scheduled-swap-check` qualification. The
latter includes API booking, exact revisions, amendments, retries, mixed fixing
states, unit conversion, risk and optional desktop/narrow browser checks. These
are bounded executable examples, not production or throughput qualification.

Arbitrary interval-varying MW profiles, interval-price cash settlement, zero-weight
observation conventions, stubs, other payment frequencies, bank dispatch and physical delivery booking remain
unsupported. The enrolled swap lifecycle supports manual cash recording;
standalone physical gas/power remains later scope.

Standalone physical gas/power booking is planned separately in the
[physical booking plan](physical-energy-booking-plan.md).

## Qualified scheduled authoring (D216)

The scheduled ticket uses the existing managed observation capability catalog and
shared observation controls. Published monthly index, equal calendar-day,
equal scheduled-observation, previous/next represented-day and explicit
quantity-weighted conventions are available where the exact index and selected
period are qualified. Calendar choices, generated mappings and typed observation
quantities remain exact, retained terms. Every floating period can independently
select a compatible exact published custom projection. Unsupported retained terms
stay locked rather than being simplified.

A common-term edit explicitly selects a source, target periods and quantity,
price/spread, payment and/or observation/projection groups. Review names affected
periods before Apply. Unselected values and delivery dates remain independent.
Weights match exact observation intervals and quantity references: a different
month retains its own explicitly entered weights, and unmatched intervals require
entry. Weights never copy by ordinal or derive from delivery quantities. A changed
context or dependent edit invalidates preparation, including pending bulk targets
and asynchronous compatibility/valuation replies. Single-payment contracts remain
on their existing contract; the monthly ticket is linked from the same entry.

Stubs, irregular periods, nonmonthly contractual payment aggregation, arbitrary
daily/hourly delivery profiles and interval-price-times-energy settlement remain
unsupported. Power uses the explicit period-average-price times integrated-energy
contract described above.
