# Delivery quantity as a valuation input: design

Status: implemented locally, 23 November 2026 (D444). Targeted managed, database,
API and browser verification passes; broader regression qualification is recorded
in [verification](../../verification.md). Publication and deployment are separate.
The selected quantity applies in the in-term lifecycle instead of the
schedule format (see Approaches). The quantity rules and initial scope below are
confirmed, including a zero-quantity correction retaining an overpaid balance.

## Intent

A physical trade is booked at a contract quantity. What is then scheduled, and
later allocated, often differs. Before this change, those quantities were
recorded beside the trade while value, accrued cash and risk stayed on contract
quantity. The saved policy lets them follow what is scheduled, and then what is
allocated.

The owner asked for this to be built on shared mechanisms so that another
commodity can adopt it without a second implementation.

## Where this sits

A review of physical gas products against current capability grouped the gaps
into six shared building blocks, ordered by how many products each serves:

1. Delivery quantity as a valuation input (this document).
2. Several weighted price terms on a payment Side.
3. A European option strip Side, generalizing the heat-rate call.
4. A charge Side by basis: fixed, per unit moved, per capacity period, per balance over time.
5. A balance position derived from scheduled less allocated quantity.
6. Constrained rights valued on the lattice.

Each is its own design. This document covers the first only.

## Goals

- A physical membership can state that its day quantity follows its booked
  delivery schedule. Value, accrued cash and risk then use the selected
  quantity of each gas day.
- Payment and fee Sides that take their quantity from that membership follow
  it, with no copy of the selection rule.
- Every day shows which quantity it used and where it came from.
- The same inputs under the same knowledge cutoff give the same result. A later
  record never enters an earlier view.
- The mechanism lives in the shared in-term lifecycle, which every valuation
  route already uses. Nothing in it names gas except the adapter that selects
  among gas quantity records.

## Non-goals

- Fuel-linked physical sides. A composition with a quantity link keeps the
  contract quantity in this slice.
- A changed delivery point. Rescheduling to another point needs its own decision.
- Pricing that is not proportional to quantity, such as volume tiers. It needs
  its own Side behavior and is a later design.
- Final billing of indexed cash. The daily cash determination stays fixed-price.
- Keep-whole and imbalance positions. They read the same quantities later and
  are separate designs.
- The two-location entry screen. It keeps the contract quantity until it gains
  the same field.
- Crude, LNG or power changes. They adopt the mechanism when they have a
  consumer.
- Discounting of gas compositions, booking a started delivery through the
  ticket, and automated closes containing gas. All unchanged.

## Anti-goals

- No operational record rewrites a contract term. The booked schedule keeps the
  contract quantity and is not changed in shape.
- No silent fallback. Using the contract quantity for a day with no record is a
  rule of the saved policy, and the day is labelled as such.
- No commodity branch in the shared evaluator, and no second pricing engine.
- No change to how any already booked trade is valued.

## Implementation baseline

The following describes behavior before D444; contract-policy trades retain it.

| Piece | Behavior | Where |
| --- | --- | --- |
| Gas day quantity | A literal written into the retained schedule at booking, normalized to one unit; membership ratios and trade size scale it once | `GasCompositionInstrument`, `PhysicalDeliverySchedule.ReadLiteralQuantities` |
| Payment quantity | A copy of its source side's stage quantity, fixed at booking. A fixed payment is one whole-term row; an indexed payment has one row per gas day | `GasCompositionInstrument` |
| Database admission | Requires an exact set of rows per gas schedule, each day quantity a literal equal to the contract total, and refuses a gas schedule that declares a port | `trading.require_gas_program_shape`, `trading.require_gas_index_cash_shape` |
| Recorded quantities | Trade-owned revisions per physical membership and gas day, of kind nominated, confirmed, scheduled or allocated, with provisional, final or withdrawn status | [daily quantities](../../physical-gas-daily-quantities.md) |
| Selection | `GasDailyQuantitySelection.Select` picks one quantity for a day under a named policy. Its read states that contractual value is unchanged | same |
| Held state | `PhysicalCashState` carries the trade, binding, held quantity and recorded payments into valuation, outside economic identity | `PhysicalLifecycle`, `GasCashPaymentStore` |
| In-term value | Every composition booked since the in-term lifecycle is valued through `PhysicalLifecycle` with its held state, from booking. Ended days leave the unrealized measure; their cash accrues on contract quantity, labelled as an estimate. A multi-day payment accrues by day count and refuses unless every covered day has the same contract quantity | `PhysicalLifecycle`, D360 |
| Two valuation routes | Blotter and single-trade reads run each schedule in the composition store; portfolio risk and P&L run them through the retained instrument. They meet in the lifecycle evaluator, which receives the held state on both | `GasCompositionStore.Value`, `GasPortfolio.Prepare`, `RetainedGasInstrument` |

## Approaches

1. **The in-term lifecycle applies the selected quantity (chosen).** The held
   state carries each day's contract and selected quantity. The lifecycle
   evaluator scales each row. It is exact because every gas row is price times
   quantity. The booked schedule and its database admission are unchanged, and
   both valuation routes are covered at the one place they share.
2. **The schedule declares its quantity as an input.** Considered first, as the
   PPA does it. Set aside because gas admission checks schedules row for row
   and forbids ports, both valuation routes would need quantities passed
   between Sides, and nine readers assume literal day quantities. It remains
   the route for pricing that is not proportional to quantity.
3. **Amend the trade to the scheduled quantity.** Rejected. It loses the
   contract quantity that later blocks need, and an operational record must not
   rewrite contract terms.

## Design

### A saved quantity policy on the physical membership

A physical membership of a composition carries one quantity policy, chosen at
booking and immutable, stored with the composition's component terms beside
its quantity link:

- **Contract quantity.** Today's behavior, and the meaning of every trade
  booked before this term existed.
- **Follows delivery schedule.**

The Side definition and its schedule are the same under both. Recorded
quantities are never part of economic identity. Two trades with the same Side
definition have separate records, as they have separate payments. A retained
valuation names the quantity policy in its model version.

### Selecting a day's quantity

For each gas day of a membership that follows its schedule, the first of these
that exists and is not withdrawn is used:

1. Allocated, final.
2. Allocated, provisional.
3. Scheduled.
4. Contract quantity.

The same order applies before and after the gas day ends; a final allocation
can exist only once it has ended. A nomination or a confirmation does not move
value. A withdrawn record is not eligible, and an older superseded record of
the same kind is never brought back. Manual allocations and adopted facility
allocations both count as allocated; two records of the same rank that
disagree for one day refuse that trade's valuation. An explicit zero is a
quantity.

Records are resolved at the valuation's business time and knowledge cutoff.
Each selected day carries its rank, the record's revision and its evidence
reference. Equal-valued winners retain both sources' provenance. A withdrawal
suppresses earlier revisions of its own source stream; another source's current
eligible head remains available.

Scheduled records come from manual daily entry or from the delivery desk's
schedule adoption. Recorded quantities are delivery-stage only. A side without
a quantity link receives and delivers the same quantity, so a payment on its
receipt or delivery stage follows the same day, and it has no fuel stage.

This is a new named policy beside `operational_projection_v1` and the two
billing policies, in the same selection function. The existing policies keep
their meaning.

### The held state carries the selected quantities

The held binding's valuation state carries, for every gas day of each
membership that follows its schedule, the contract quantity, the selected
quantity, its rank and its revision, in absolute native units. A payment or fee
membership carries its source membership's days. Coverage is complete: a day
with no record is present with the contract quantity and the contract rank, so
the evaluator never fills a gap itself.

The state is built by the store that reads recorded payments, under the same
financial business instant and knowledge cutoff, for both valuation routes.
The saved membership terms identify the followed physical slots and their cash
sources independently of the returned records. Missing a whole followed slot,
its cash-source mapping or any day refuses, as do quantities that do not match
the booked schedule. An unauthorized or failed read never becomes fallback.

### Value and accrual

- **A gas day not yet ended** values its physical Side at the location mark
  times the selected quantity, and its cash at its price times the selected
  quantity. Both sit in the unrealized measure, as now.
- **An ended gas day** leaves the unrealized measure. Its cash accrues at its
  fixed or published price times the selected quantity. This replaces accrual
  on contract quantity for these trades.
- **A payment covering several days** is worth its contract amount times the
  sum of selected quantities over the sum of contract quantities. It accrues
  the share of its ended days. The equal-daily-quantity refusal does not apply
  to these memberships.
- Price rules are unchanged: a published fixing locks the payment price only,
  and accrued cash needs a fixed, actual or published price.
- Recorded payments are unchanged. When a correction lowers the accrued amount
  below what was paid, the row reads overpaid, as it does today.
  This includes an accrued amount of zero: the full payment remains visible as
  an overpaid balance. It creates no refund and does not change contract-policy
  trades or other settlement profiles.
- Each physical row's quantity label names the rank it used. A payment row is
  labelled as following the selected quantity.

### Worked example

Synthetic. A purchase of 1,000 per gas day for days 1 to 4 at a fixed 3.00, with
a location mark of 3.20 on every day and no discounting. The valuation instant
falls after day 2 has ended, with day 3 in progress.

| Day | Record | Selected | Source |
| --- | --- | ---: | --- |
| 1 | Allocated final 980 | 980 | Allocated, final |
| 2 | Scheduled 1,050 | 1,050 | Scheduled |
| 3 | Scheduled 900 | 900 | Scheduled |
| 4 | None | 1,000 | Contract |

| Measure | Contract quantity | Follows schedule |
| --- | ---: | ---: |
| Physical value, days 3 and 4 | 6,400 | 6,080 |
| Cash, days 3 and 4 | −6,000 | −5,700 |
| Unrealized | 400 | 380 |
| Realized unsettled, days 1 and 2 | −6,000 | −6,090 |
| Total | −5,600 | −5,710 |

A mark shock of +0.10 moves the unrealized measure from 380 to 570 and leaves
accrued cash unchanged. If a final allocation of 1,020 is later recorded for day
2, accrued cash becomes −6,000; a view at the earlier knowledge cutoff still
reads −6,090.

### Booking and the ticket

- The physical side gains a required field for the quantity policy with the two
  choices above. The backend proposes "Delivery schedule" as a visible,
  editable starting value.
- A composition with any quantity link refuses the follows-schedule policy
  before booking, with the reason on that field.
- Fee sides, including a fee priced by a configured usage tariff, follow their
  source's selected quantity when the source follows its schedule.
- The schedule view shows each day's selected quantity and its source for the
  whole term, and its amounts use the selected quantity.
- The quantities read no longer states that contractual value is unchanged for
  these trades; it names the policy that drives value.
- The administrator's in-term load accepts the same policy.
- The existing amendment rule stands: a composition with recorded quantities is
  not replaced whole.

### Persistence and admission

- No new store for quantities. The recorded revisions are the source.
- The policy is one typed column of the component terms. The booking function
  accepts it as an optional key of a physical side, admits only the
  follows-schedule value, and refuses it on a composition with a quantity link.
  Schedule admission is unchanged.
- One read returns the scheduled and allocated records a valuation needs, for
  the application and for the risk worker under its existing lease and pinned
  context. Ordinary composed-gas risk uses the ordinary retained-risk authority,
  with exact job, trade and book scope and current source authority. Facility
  adoption is resolved at the financial business instant, without substituting
  the delivery desk's end-of-day clock.
- Booking, recording and retries keep their existing idempotent contracts.

### Risk and reports

- Risk stays base, up and down repricing of the same Sides under shared
  scenarios. The selected quantities are calculation inputs pinned by the run's
  knowledge cutoff.
- Unit results of such a trade are not shared with another trade, as with
  recorded payments today.
- A value change caused only by a new quantity record falls in the visible
  residual of P&L explain. A separate attribution line is later work.
- A conflicting winning quantity refuses the trade on the Blotter and refuses
  a complete risk request containing that trade; it introduces no new partial
  risk exclusion policy.

### What another commodity reuses

Shared: the held-state day quantities, the lifecycle evaluator's scaling and
accrual, and the row labels. Gas-specific: the ranking over gas record kinds
and the store that reads them. A crude or LNG adapter would supply its own
ranking over its own evidence when it adopts the in-term lifecycle. None is
built here.

## Verification

Checks are written first and are independent of the implementation's algorithm.

- **Managed:** the worked example above, including the shock and the later
  correction; each rank; withdrawal; same-rank disagreement; explicit zero; a
  multi-day payment with unequal daily quantities; incomplete or mismatched
  state; and unchanged results for trades on contract quantity.
- **Database and API:** the new column and key, the linked-side refusal,
  booking, reopening and replay; reads at an earlier cutoff; worker access; and
  parity of the Blotter, the single-trade read and a queued risk run.
- **Browser:** the policy field, the linked-side refusal and the schedule view's
  sources.
- **Independent adversarial review** of the temporal, numerical and
  authorization behavior before publication.

## Confirmed policy decisions

1. A nomination or confirmation alone does not move value; only a scheduled or
   allocated record does.
2. A provisional allocation outranks a scheduled quantity, including before the
   gas day ends.
3. Manual allocations and adopted facility allocations both count as allocated.
   Two that disagree for one day refuse that trade's valuation.
4. Fee sides follow the selected quantity, which changes a usage tariff's basis
   from contractual to selected quantity for these trades.
5. The ticket proposes "Delivery schedule" as its starting value.
6. Quantity-linked compositions are refused the policy in this slice.
7. Already booked trades are not converted. Only disposable local demos are
   re-created.
8. Quantity-driven value changes sit in the P&L explain residual for now.
9. The two-location entry screen keeps the contract quantity in this slice.
10. A correction to zero after payment retains the payment and shows its full
    overpaid balance for the new policy.

## Implementation records

Migration `0389_gas_delivery_quantity_valuation.sql` adds the membership term,
native valuation read and retained-command compatibility gate. The shared
`PhysicalDeliveryQuantities` state validates coverage and source mappings;
`GasHeldQuantities` selects gas evidence. Both valuation routes consume that
state, and the ticket and common Side inspector display its selected quantities.

[D444](../../decisions.md#d444--selected-delivery-quantities-in-the-physical-lifecycle)
supersedes D360's accrual basis for these trades. [Physical gas](../../physical-gas-side.md), [daily
quantities](../../physical-gas-daily-quantities.md), the [deal
ticket](../../deal-ticket.md), [plan](../../plan.md),
[implementation status](../../implementation.md) and
[verification record](../../verification.md) describe the built scope.
