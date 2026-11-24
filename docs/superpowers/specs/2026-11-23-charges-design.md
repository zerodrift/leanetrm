# Charges on gas trades: design

Status: built locally, D450. Migrations 0399 and 0400. The baseline below describes the
pre-change implementation. This build uses the
[Instrument schedule and gas pricing contract](2026-11-23-instrument-schedule-and-gas-pricing-design.md).
See the [gas contract](../../physical-gas-side.md#independent-gas-charges-d450)
and [local verification](../../verification.md#independent-gas-charges-d450).

## Intent

A charge is an ordinary fixed payment. What distinguishes one charge from
another is what it is charged on.

Before D450, gas payments used the quantity of a physical Side. The
owning entry lets a gas composition carry a lump sum or a capacity charge.
It uses the existing fixed payment Side. For either basis, the first gas day
must have a representable UTC start. Fresh booking admits a charge that has
already started. A future-only amendment requires every original and
proposed charge to start strictly after the amendment instant. A recorded
payment on an original charge prevents replacement even when the payment
has a later effective instant.

## Where this sits

The review of physical gas products grouped the gaps into six building blocks.
The fourth was listed as "a charge Side by basis". This document replaces that
entry, for gas trades.

A gas exercise premium is an independent lump sum on the same trade as the
right. A right naming a charge is refused in booking and in retained
admission.

Transport charges belong to a separate redesign. See [Transport](#transport).

## Goals

- A gas trade can carry a lump sum on a stated date, including a date before
  delivery starts.
- A gas trade can carry a capacity charge: a rate on a stated daily quantity,
  owed whether or not gas flows.
- Both accrue on their own days and take recorded payments as gas payments do
  today.
- No trade booked before this change values differently.

## Non-goals

- Any charge on transport.
- A charge on a balance over time. It needs the balance the fifth block
  defines.
- Consolidating the fees that storage, park and loan, park custody, financial
  transport and owned loans already carry.
- Booking a cash payment as a trade of its own.
- A rate taken from a tariff, and an index-priced charge.
- A daily quantity that changes within the term.
- Final billing of these entries, and discounting.

## Anti-goals

- No new Side type, and no Side that reads another Side.
- No relaxing of the shared rules on when a payment may be due or recorded.
- No silent default. Basis, amount or rate, quantity, days, clock and payment
  are saved explicitly.
- No changed number for any existing trade.

## Implementation baseline

These references identify the starting implementation.

| Piece | Pre-D450 behavior | Where |
| --- | --- | --- |
| Gas payment entry | Names a physical source and stage. Quantity follows that source under the selected share and part | [D447](../../decisions.md#d447--gas-pricing-is-a-composition-of-independent-payment-sides) |
| Fixed payment Side | The managed Side accepts several contiguous periods, each with its own quantity | `SwapSidePeriods.cs:40-66` |
| Admitted fixed payment | One row over its selected term within the physical source | migrations 0326 and 0391 |
| Payment date | The UTC payment date is on or after the UTC completion date | `PaymentDatePolicy.cs:14-18`, `SwapSidePeriods.cs:58`, migration `0326:69` |
| Due and paid | A row cannot be due while a day it covers has not ended, and no payment can be recorded before one of its days has ended | `PhysicalLifecycle.cs:54`, `:249`, migration `0216:197-204` |
| Lifecycle days | Day states come from the selected physical program and cover every day of a following payment row | Pre-D450 `PhysicalLifecycle` and `RetainedGasInstrument` |
| Accrual without a policy | A payment row accrues by its count of ended days, and requires equal physical quantities on the days it covers | `PhysicalLifecycle.cs:237-241` |
| Held quantities | Every payment must name a physical source | `PhysicalDeliveryQuantities.cs:25-42`, `:63-64` |
| In-term lifecycle | Admitted only on gas memberships | migration `0216:51-52` |
| Gas payment records | Keyed by trade, entry and row. The earned test reads the source's physical days | migration `0216:91-95`, `:189-204` |
| Clocks | Only fixed-offset gas day clocks are admitted. Physical sides of one trade may have different clocks | migration `0326:49`, [physical gas side](../../physical-gas-side.md) |
| Units | Every entry of a trade shares one quantity unit and one currency | `GasCompositionInstrument.cs:54-56` |
| Index payment dates | Paid on a stated day, 1 to 28, of the following month, with no holiday adjustment | `GasIndexedCash.cs:20`, `:49-51` |
| Charges elsewhere | Storage, park and loan, park custody, financial transport and owned loans each carry fees in their own way | migrations 0338, 0351, 0354, 0308, 0370 |
| Standalone cash | Payments can be recorded against a cash Instrument, but no route books one on its own | `Program.cs:2895` |

## Approaches

1. **A payment entry that owns its quantity (chosen).** Its Side is the
   existing fixed payment Side, and the Instrument states its quantity.
2. **A new charge Side type.** Not needed. Every charge is a fixed payment.
3. **A separate cash Instrument for each charge**, using separate booked
   objects like the existing fee profiles on other products.

## Design

### An entry that owns its quantity

A payment entry either follows a physical entry, as today, or owns its
quantity. An owning entry states:

| Field | Lump sum | Capacity charge |
| --- | --- | --- |
| Basis | `lump_sum` | `capacity` |
| Price | Its amount | A fixed rate per unit per day |
| Quantity | One | One stated daily quantity for the whole term |
| Days | One, derived from its payment date | Stated first gas day and exclusive end |
| Clock | The gas day clock of a named physical side of the trade | The same |
| Payment | A stated UTC instant | The stated day 1–28 of the following month at UTC midnight under `next_month_day_utc_v1` |
| Direction | Pay or receive | Pay or receive |
| Calendars | Explicit delivery and payment bindings | Explicit delivery and payment bindings |

It names no source and no stage. Its unit and currency are the trade's.
Quantities are saved as exact ratios of the trade's base quantity. The Trade
scales once, preserving the same stated charge across delivery sizes.

The raw amount or rate states contractual cash in trade currency. The
existing exact quote is carried with its unit and multiplier. Booking
divides the raw price by that multiplier exactly to obtain the Side's price
literal. The absolute stated price must be below 10^12 and the literal
must fit the supported 18-place decimal scale exactly. This contractual
price has no market quote range. A capacity rate and its daily quantity
are positive. A lump sum can be signed or zero.

Its terms are part of the Instrument's definition and identity. An Instrument
with no owning entry keeps its identity.

### Its Side and its rows

The Side is the existing fixed payment Side.

- **Lump sum:** one row. Its one day is the latest gas day, by its clock, that
  has ended when its payment date begins. The shared rules on payment dates
  keep a premium dated on the booking date accrued at booking. A later
  premium remains unrealized until its derived day ends.
- **Capacity charge:** one row for each calendar month its days fall in. A
  row's quantity is the stated daily quantity times its count of days. It is
  paid at UTC midnight on the stated day of the following month, with no
  holiday adjustment, as index payments are.

The capacity shape admits several fixed payment occurrences on one Side for
the charge role only. The payment date must be on or after the UTC date
containing the exact gas-day completion instant. A capacity row with
payment day 1 may complete after UTC midnight that day. The last day
accrues at that completion instant. The row is overdue on the next UTC date.

`GasChargeSides.LumpSum` takes the Side ID, typed amount, unit quantity,
clock, UTC payment instant, explicit Side calendar policy and exact
`QuoteSpecification`. `GasChargeSides.Capacity` takes the Side ID, typed
rate, unit quantity, clock, first gas day, exclusive end, payment day,
explicit Side calendar policy and exact `QuoteSpecification`.

### Accrual on its own days

An owning entry's rows accrue as their own days end, by the entry's clock. The
Instrument works out those day states from the entry's own days, beside the
physical days it already has. A row's days need not be days of any physical
side.

A capacity row accrues by its count of ended days, as a payment without a
quantity policy does today; its day quantities are equal by construction. A
lump sum has one day, so it is either still to come or owed in full.

Each Side still computes its own rows. Part payment, full payment and an
overpaid balance work as they do for any gas payment row. An effective
remittance needs at least one of the row's days to have ended, including
when the earned day ends before the payment date starts. The resolution of
the payment fact uses its effective instant and the selected knowledge
cutoff. The recorded payment sign follows direction times the nominal sign.
Zero nominal admits no payment.

### What changes in the shared lifecycle

Four places assume every payment follows a physical side. Each gains the
owning case:

- day states, which also come from an owning entry's own days;
- the rule that every payment day is a physical day, which applies only to
  entries that follow;
- held quantities, which need no source for an owning entry;
- the earned test of gas payment recording, which reads an owning entry's own
  days.

### Booking, ticket and display

Composition booking accepts owning entries through the optional
`chargeSides` array. They count toward seven payment Sides and eight total
Sides. Each `GasChargeRequest` states its code, basis, direction, price,
clock Side and explicit `calendarPolicy`, plus the exclusive fields of its
basis. A lump sum supplies `paymentAt`. Capacity supplies `dailyQuantity`,
`from`, `to` and `paymentDay`. With no charges, the ticket omits the array.

The standalone ticket's Add side offers **Charge** with the controls in the
[deal ticket contract](../../deal-ticket.md#gas-charge-side-d450). In the
joint schedule each month spans its actual contribution intervals while
each Side keeps its grain. A one-day capacity stub is monthly. The combined
view counts native contributions completed by their own interval ends
under `native_schedule_rows_v1`. Exact Side inspections report each row's
days, quantity, payment date, accrual, paid cash, outstanding cash and
unrealized cash.

### Worked example

Synthetic, no discounting. We pay each charge.

| Entry | At booking | After ten of thirty days | After the month | After payment |
| --- | --- | --- | --- | --- |
| Lump sum of 5,000, payment dated on the booking date | −5,000 owed | −5,000 owed | −5,000 owed | 0 |
| The same lump sum dated a week after booking | −5,000 to come | −5,000 owed | −5,000 owed | 0 |
| Capacity charge, 10,000 a day at 0.02, one thirty-day month | −6,000 to come | −2,000 owed, −4,000 to come | −6,000 owed | 0 |

A trade of twice the size with the same stated terms gives the same amounts.
The lump sum and daily quantity are independent of delivery size.

### Persistence and admission

Owning entries are stored with the trade's other entries, with no source and
with their basis, quantity, clock side, days and payment terms. Database
admission checks the derived lump-sum day, ordered whole gas days and
positive daily capacity for a capacity charge, explicit calendars and the
payment date relative to completion's UTC date. Invalid booking terms
refuse with `gas_composition_input`. Clock-boundary, calendar and lifecycle
errors retain their established codes. Admission of the several-row fixed
payment shape with in-term lifecycle is specific to charges. Final gas billing
refuses a charge occurrence with `gas_billing_profile`.

### Risk and reports

A charge has a fixed price and no price dependency, so it carries no price
risk. Its value changes only as days end and payments are recorded.

### Transport

Transport charges belong to transport's own redesign. That work needs
explicit policies for daily contracted capacity, gas-day clocks, linked
units, payment records, multiple fee memberships and authority to read
operating plans in valuation.

### What another commodity reuses

The own-day accrual uses the shared retained lifecycle. The gas composition
is its current owning-entry booking consumer. Each future commodity needs
its own supported booking contract.

## Build order

Each stage leaves every check passing.

1. **Owning entries and their accrual**, at the managed level: the term, the
   rows, the day states, accrual and payments.
2. **Booking and persistence:** the new admitted shape, the store and the
   payment recording's earned test.
3. **The ticket and the schedule display.**

## Verification

- **Unchanged numbers.** Every existing check passes without a changed
  expected value. An Instrument with no owning entry keeps its identity.
- **Oracle.** Every cell of the worked example, and the trade of twice the
  size.
- **Lump sum.** Dated on the booking date, a week later, and before delivery
  starts. A remittance just before the end of its derived earned day
  refuses. A remittance at that boundary can succeed even before the UTC
  payment date begins.
- **Capacity charge.** Before, during and after a month, including days
  outside the physical term. The 20 January–10 March 2027 inclusive term
  has portions of 12, 28 and 10 days. A 15:00 UTC clock has five ended
  February days at 2027-02-06T15:00:00Z and 24 at 2027-02-25T15:00:00Z.
- **Payments.** Part payment, full payment, overpayment, and a payment known
  later than the valuation's cutoff.
- **Refusals.** A capacity charge with a zero quantity or its days out of
  order, a payment day above 28, a clock side that is not a physical side of
  the trade, and an owning entry that names a source.
- **Routes.** Preview, reads, the Blotter and risk agree.
- **Admission.** Exercise the same valid and invalid booking cases through
  managed admission and the native writer.
- **Ticket.** Browser checks for the Charge side, booking and the schedule.
- **Review.** One independent adversarial review of temporal behavior: day
  states, due dates and payment visibility.

## Contract decisions

[D450](../../decisions.md#d450--gas-compositions-admit-fixed-payments-that-own-their-quantity)
records the following seven calls and links the exercise premium to D448.

1. A lump sum's day is the latest gas day that has ended when its payment
   date begins. No shared payment rule is relaxed.
2. A capacity charge has one row per calendar month and is paid on a stated
   day of the following month, with no holiday adjustment.
3. A capacity charge has one daily quantity for its whole term.
4. An owning entry takes its clock from a named physical side of the trade.
5. A lump sum and a daily quantity are stated amounts. They do not grow with
   the trade's delivery quantity.
6. Rates are fixed and stated on the trade. No tariff supplies one.
7. Existing fees on other products are left as they are.

The [implementation plan](../plans/2026-11-23-gas-charges.md) records the
interfaces and qualification sequence.
