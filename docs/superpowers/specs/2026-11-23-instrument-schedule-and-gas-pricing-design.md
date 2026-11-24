# The Instrument schedule and gas pricing from independent Sides: design

Status: built locally, D446 and D447. Migration 0391. Qualification is recorded in
[verification](../../verification.md); publication and deployment are separate.
The baseline and audit sections below describe the pre-change implementation.

## Intent

A trade is one or more Instruments, and an Instrument is one or more Sides. A
Side gives a schedule and derives its present value from it. An Instrument
does not: it adds up Side totals, and the schedule shown for a trade is
assembled elsewhere. Anything that spans Sides is therefore worked out beside
the Instrument, in more than one place.

This design does three things, in one build:

1. Gives every sided Instrument a schedule and derives its value from it.
2. Moves gas onto it, so a gas trade's value, accrued cash and displayed
   schedule come from one evaluation.
3. Adds gas pricing built from independent Sides: a payment can take a share,
   a date range or a part of each day's delivered quantity.

It builds on [delivery quantity as a valuation input](2026-11-23-delivery-quantity-input-design.md)
(D445).

## Where this sits

The review of physical gas products grouped the gaps into six building blocks.
The second was listed as "several weighted price terms on a payment Side". This
document replaces that entry. A price that is a sum of terms is a sum of
payments, so each term is its own existing fixed or floating Side.

The third block changes shape as a result. An option becomes an Instrument
whose payoff takes its Sides' rows as inputs, in place of an option Side. That
block is a separate design. It is the first user of the Instrument schedule
that is not a sum, and this design must not close it off.

## The line

```text
Trade -> [Instrument, ...] -> [Side, ...]
```

- A **Trade** holds one or more Instrument bindings. Each applies its external
  quantity once.
- An **Instrument** holds ordered entries, one per Side, and any term that
  spans them. It gives a schedule, and its value is derived from that schedule.
- A **Side** holds its own terms and gives a schedule and a value from them. It
  does not know its peers.
- **Futures contracts and exchange-traded futures options** are Instruments
  without Sides. They get no schedule method. D181 is unchanged.

Market data, scenarios and numerical policy are inputs. Fixings, recorded
quantities and payments are facts about one binding: they are supplied to
valuation and do not compute it. The API and browser lay out what an
Instrument returns.

## Goals

- Every sided Instrument has a schedule method, and its value cannot be
  combined any other way.
- A gas trade's value, accrued cash, risk and displayed schedule come from one
  evaluation by its Instrument, on every route.
- A payment entry can take a share of its source's quantity, cover part of its
  source's term, and take a part of each gas day's quantity.
- An index payment names its own index and whether it is published daily or
  monthly.
- No trade booked before this change values differently.
- A Side shared by several Instruments is still valued once per scenario.

## Non-goals

- Futures and exchange-traded futures options.
- The option Instrument and any payoff that is not a sum.
- Moving crude, LNG, power, certificate, storage, transport, park and loan or
  swap valuation. They gain the plain schedule and are otherwise untouched.
  See [Recorded, not changed here](#recorded-not-changed-here).
- Triggers, one payment priced across several delivery points, monthly
  cumulative tiers, and imbalance positions.
- Final billing of an entry with a non-unit share, a part or a partial term.
- Compositions with a fuel link, stored single-point trades and the
  two-location entry screen. They keep today's behavior, as under D445.
- A columnar library dependency, and raising the limit of eight Sides.

## Anti-goals

- No Side holds several separable cash flows, and no Side reads another Side.
- No Instrument reprices a Side's cash flows or runs a Side's saved program
  itself. Each Side computes its own rows.
- No formula language. Parts are a closed, typed set.
- No silent default. Share, part, index and publication are saved explicitly.
- No changed number for any existing trade, and no changed identity for any
  Instrument that carries no quantity policy and no part.

## Implementation baseline

File references are starting points; the builder confirms each.

### Instruments and Sides

| Piece | Behavior today | Where |
| --- | --- | --- |
| Side | `GenerateSchedule()` and `PV(context)` are abstract. `ScheduleRow` is one row view across every Side schedule | `Trading/Instruments.cs:33`, `ScheduleRow.cs:16` |
| Instrument value | The sided base evaluates each Side through the context's cache and sums total × quantity ratio × coefficient. There is no schedule method | `Instruments.cs:208`, `PaymentSchedules.cs:546` |
| Overrides of that step | Five swap and forward classes check discounting after it. Four classes check that delivery has not started before it. One class refuses. The saved-program base replaces it when a held state is present | `FinancialSwaps.cs:323`, `GasStorageInstrument.cs:73`, `IndexedPhysicalGasPurchase.cs:54`, `RetainedProgramInstrument.cs:83` |
| Instrument inspection | An extension builds per-Side schedule snapshots from an Instrument valuation, applying ratio × coefficient. It has one caller | `SideScheduleSnapshot.cs:99`, `BookedScheduleSources.cs:45` |
| Exchange kinds | Derive from `Instrument` directly, not from the sided base | `Futures.cs:85`, `FuturesOptions.cs:12` |
| Identity | The identity writer writes every identity field, including a null one, unless a rule skips it by name | `DefinitionIdentity.cs:68-78` |

### Gas valuation

| Piece | Behavior today | Where |
| --- | --- | --- |
| Two forms | `GasCompositionInstrument` exists while booking and is never valued. A booked trade is valued as `RetainedGasInstrument`, on the saved-program base that crude, LNG, operating rights and certificate forwards share. Entry identity is direction, ratio and program digest | `GasCompositionStore.cs:157`, `RetainedGasInstrument.cs:10-32` |
| Lifecycle | Every composition booked since the in-term lifecycle is valued through it from booking | `GasCompositionStore.cs:108`, `GasPortfolio.cs:48` |
| Risk route | The saved Instrument gives each Side an occurrence: its slot, held factor, day geometry and the held state. Each Side computes its own accrued and paid rows, as D417 requires | `RetainedProgramInstrument.cs:83-104`, `RetainedProgramSide.cs:138-161` |
| Preview and read route | The composition store evaluates each Side program itself, applies ratio, quantity and direction, and sums. With a held state it replaces the total with the lifecycle's | `GasCompositionStore.cs:164-261` |
| Held state | Carries recorded payments, each followed entry's days (contract and selected quantity, label, evidence), and each entry's source and D445 policy. The policy is not in the Instrument's identity | `PhysicalDeliveryQuantities.cs`, `RetainedGasInstrument.cs:112` |
| When days are built | Only when an entry has the D445 policy. A source without it has no held days, and its payments are valued at fraction 1 | `GasDeliveryQuantityStore.cs:36`, `PhysicalLifecycle.cs:240-241` |
| Applying days | A payment row is scaled by selected over contract quantity for its days. A following payment must carry exactly its source's days | `PhysicalDeliveryQuantities.cs:44-50`, `:87` |
| Display | Value and display share only the final division. The sums over days are computed separately, in three places that assume a following payment's quantity equals its source's | `ScheduleFollowedDay.cs:29`, `ScheduleView.cs:155`, `RetainedProgramSide.cs:183`, `GasCompositionStore.cs:232` |

### Gas payments

| Piece | Behavior today | Where |
| --- | --- | --- |
| Payment entry | Names a physical source, a stage and a direction. Its ratio is the source's stage ratio. It must cover its source's whole term | `GasCompositionInstrument.cs:10`, `:91-97` |
| Bounds | One to four physical Sides and up to four payment Sides. Shared schedule staging admits eight Sides per trade for every product | `GasCompositionInstrument.cs:21`, migrations `0125:41`, `:282`, `0144:12` |
| Named index payment | One row per gas day. The index must equal the delivery point's own. After the fixing time a missing published price refuses valuation | `GasIndexedCash.cs:28`, `:35`, `:53`, `IndexFixings.cs:29-31` |
| Point publication payment | The point's setup selects an index, a curve and a daily or monthly publication by date range. The published price is used as soon as it is available. Only this implementation accepts a month's contract on a daily row | `GasPointIndexCash.cs:28-36`, `DeliveryIndexSide.cs:20-25`, `IndexFixings.cs:28` |
| Database admission | Checks the index payment's exact rows and terms, and ties the delivery-index implementation to point publications | `trading.require_gas_index_cash_shape`, migration 0229 |
| Billing | Fixed price × final allocated day quantity × direction. The database recomputes it, ignoring ratio and dates | `GasCashBilling.cs:29`, migration `0205:151` |

## Approaches

**Where a combined price lives**

1. **Independent Sides, combined by the Instrument (chosen).** Each term keeps
   its own schedule, fixing state and risk, in a shape admitted today.
2. **A payment Side holding several weighted terms.** Rejected. It bundles
   separable cash flows and duplicates composition.

**Where a term that spans Sides is evaluated**

1. **In the Instrument's schedule (chosen).** One evaluation gives value,
   accrual and display.
2. **In the held state and lifecycle, as D445 does today.** Smaller. Set aside
   by the owner: the term stays outside identity and display sums stay
   separate.
3. **Quantities passed between Sides as schedule inputs.** Rejected. Sides
   would depend on peers.

**How far the Instrument schedule reaches in this build**

1. **Every sided Instrument has it; only gas is moved onto it (chosen).**
   Other products keep their numbers by construction, because a plain
   schedule reproduces today's sum exactly.
2. **Also move every store and swap settlement.** Deferred. It adds no
   capability and touches products this work does not need.

## Design

### Part 1: the Instrument schedule

#### The contract

`SidedInstrument` gains `GenerateSchedule()`, returning an Instrument schedule.
The shared implementation on the base is the plain one: one entry per Side, in
order, holding

- the entry's slot, Side, coefficient and quantity ratio;
- the Side's own schedule, by reference.

Its rows are a view. Each is one Side row tagged with its entry. A row keeps
its Side's identity, kind, dates and payment links. Rows of different Sides are
listed together and never joined by date.

An Instrument that needs to see several Sides overrides `GenerateSchedule()`.

#### Value is derived from it

`SidedInstrument.PV(SideValuationContext)`, the step that combines Sides,
becomes a sealed call to `GenerateSchedule().Evaluate(context)`. For a plain
schedule, evaluation asks the context for each Side's valuation, which keeps
the cache, and sums total × quantity ratio × coefficient. That is today's
arithmetic, so no number changes.

Methods that prepare a context before that step, such as the saved-program
market inputs and the power interval session, are unchanged and then call it.
The swap settlement dispatch sits before that step and is also unchanged.

The existing Instrument inspection becomes a method of the evaluated schedule.

#### Conditions

An override that only checks something becomes a condition the Instrument
states and its schedule's evaluation applies:

- before combining: delivery has not started; or, for the one class that
  refuses today, an unconditional refusal;
- after combining: discounting matches the declared policy.

A condition can refuse. It cannot change a number.

#### What the option block will need

Not built here. An option Instrument will override `GenerateSchedule()` and
evaluate a payoff over its Sides' rows for each period. Two properties of this
design keep that open: rows keep their Side's period identity, and evaluation
belongs to the schedule, not to the base class.

### Part 2: gas valued through its Instrument

#### Entry terms live in the Instrument

Two terms that span Sides become terms of the saved gas Instrument's entries,
and part of its identity:

- the D445 quantity policy of a physical entry;
- the part of a payment entry, defined in Part 3.

The booking form carries the same two terms, since it resolves and checks
them.

The identity writer skips each when absent, by an explicit rule, so every
Instrument booked without them keeps its identity. An Instrument booked with
the D445 policy before this change gets a new identity and is booked again.

The held state keeps facts only: recorded payments, and day quantities with
their labels and evidence. The gas adapter still selects each day's quantity
from the records. The Instrument checks that the days it receives were
selected under its own entries' policy.

#### One evaluation

The saved-program base overrides `GenerateSchedule()`. Evaluating that
schedule:

1. Takes the binding's facts from the context.
2. Works out each entry's quantity for each gas day: the owner's selected
   quantity, and for a follower the part of it. This is one helper.
3. Gives each Side its occurrence, as today, now including those quantities.
4. Lets each Side compute its own rows: what has ended, what is earned, what
   is paid. The lifecycle rules of D360 and D445 are unchanged.
5. Applies each entry's sign and ratio and adds.

The result holds every row with the quantity it used, the entry totals and the
sum. Step 2 is the only place a day quantity is worked out.

With no held state and no entry term, the schedule is the plain one, as today.

#### One route

- **Preview.** The composition store builds the saved form from the draft's
  compiled programs and values that Instrument. Its own loop over Side
  programs goes.
- **Reads and risk.** Already value the saved Instrument.
- **Display.** The schedule view and the Side snapshots take quantities and
  amounts from the evaluated schedule. The three separate sums go.
- **Booking form.** `GasCompositionInstrument` still resolves a composition
  while booking. It refuses valuation when an entry carries a policy or a
  part, because its plain sum would be wrong.

### Part 3: gas pricing from independent Sides

#### Each price term is its own Side

| Case | Entries on the trade |
| --- | --- |
| Index × 1.05 | One index payment, share 1.05 |
| Average of two indexes | Two index payments, share ½ each |
| Index A less index B | Two index payments in opposite directions |
| Fixed and floating mix | One fixed payment and one index payment, each with its share |
| Price by date range | One payment per range, each with its own dates |
| Tiers by volume | One payment per tier, each on a band |
| Keep-whole on shortfall | Two index payments in opposite directions, both on the shortfall |
| Index less a cost adjustment | One index payment with its share, and a fixed fee |

Each term settles on its own row. The fixed part of a mix is earned while the
index part is still a forecast.

#### Share

A payment entry states an exact positive share. Its quantity ratio becomes
share × its source's stage ratio, fixed at booking. The ratio is already part
of the entry's identity and is applied once. Display shows the payment's own
quantity, which the single evaluation provides.

#### Part of the day's quantity

| Part | Quantity taken from a gas day of quantity q |
| --- | --- |
| Whole | q |
| Band from L to U | min(max(q − L, 0), U − L). U may be open, which gives max(q − L, 0) |
| Shortfall below B | max(B − q, 0) |

- **Levels are per gas day.** They are entered as quantities and saved as exact
  ratios of the trade's base quantity. A trade twice the size has levels twice
  as large.
- **q is the day quantity the source's policy selects.** With no policy that is
  the contract quantity.
- **Limits.** A part other than whole needs an independent physical source and
  a composition with no fuel link, as the D445 policy does.
- **Always with day quantities.** A trade with a part is valued with held day
  quantities whatever its source's policy. A day with no record carries the
  contract quantity, labelled as contract. Without them the Instrument refuses.
  Today such a trade would be valued at fraction 1, counting every tier on the
  whole quantity, with no error.

For a payment row covering gas days d:

- total fraction = Σ Part(q(d)) ÷ Σ contract(d);
- ended fraction = the same sum over ended days only ÷ Σ contract(d).

`contract(d)` is the source's contract quantity at the held scale, which is
positive. A part of zero is valid and gives a zero row.

Booking refuses a negative level, a band whose lower level is not below its
upper, and a part on a physical entry.

#### A payment over part of the term

A payment Side's own dates may cover part of its source's term. They must lie
within it and fall on gas day boundaries. An index payment has one row per
covered gas day. A fixed payment has one row over its dates, with its own
payment instant. Each row's quantity equals the source's contract quantity for
the days it covers.

#### Index choice

| Form | Index and publication come from |
| --- | --- |
| Point publications | The delivery point's setup, as today |
| Named index, daily | The payment. Each gas day uses that day's contract and fixing |
| Named index, monthly | The payment. Each gas day uses its month's contract and fixing |

Both named forms are built as point publications are built today, on the
delivery-index implementation. Only it accepts a month's contract on a daily
row.

All three forms share one fixing rule: a row uses the published price as soon
as it is available and a forecast until then. The row is labelled either way,
and cash is earned only on a published price.

Today's named daily form uses a stricter rule: after the fixing time a missing
published price refuses valuation. It is no longer offered for new bookings. A
trade already booked with it keeps its saved schedule and its rule.

For a named index, the published curve that carries its contracts is resolved
through the curve's settlement target. Exactly one must exist. Every covered
gas day needs exactly one contract and one fixing definition, or booking
refuses. A custom forecast curve stays an optional explicit choice.

A named index does not have to equal the delivery point's index. The ticket
proposes the point's own pricing visibly. The row layout admitted today is
unchanged; the admitted terms gain the two named forms.

#### Worked example

Synthetic, no discounting. We receive 10,000 per gas day. The monthly index is
3.00, the daily index 3.20 and the value at the point 3.10. Each row is the
value of one gas day that has not ended.

| Entry | Side | Direction | Part |
| --- | --- | --- | --- |
| 1 | Physical delivery, follows the delivery schedule | Receive | Not applicable |
| 2 | Index payment, monthly index | Pay | Band 0 to 8,000 |
| 3 | Index payment, daily index + 0.05 | Pay | Band from 8,000, open |
| 4 | Index payment, daily index | Receive | Shortfall below 10,000 |
| 5 | Index payment, monthly index | Pay | Shortfall below 10,000 |

| Day's quantity | Entry 1 | Entry 2 | Entry 3 | Entry 4 | Entry 5 | Day total |
| --- | --- | --- | --- | --- | --- | --- |
| 10,000, contract | +31,000 | −24,000 | −6,500 | 0 | 0 | +500 |
| 9,000, scheduled | +27,900 | −24,000 | −3,250 | +3,200 | −3,000 | +850 |
| 7,000, allocated provisional | +21,700 | −21,000 | 0 | +9,600 | −9,000 | +1,300 |

A separate case: an index payment at share 1.05 on 10,000 at 3.20 is 33,600.

Once a day has ended, its physical value leaves the measure and its cash
accrues only at a fixed or published price. A final allocation exists only for
an ended day. Accrued cash therefore has its own example, with its own
valuation time and published prices.

#### Booking, ticket and display

Composition booking accepts a share, a part and dates on each payment entry.
The ticket stays one field per row. A payment side gains:

- **Share of quantity.** Shown as 1 and saved explicitly.
- **Quantity part.** Whole, up to a daily level, above a daily level, between
  two daily levels, or shortfall below a daily level. Level fields appear with
  the choice.
- **From and To.** Editable within the source's term. They are derived today.
- **Pricing.** On an index payment: point publications, or a named index with
  daily or monthly publication.

A trade may hold up to seven payment Sides, within the unchanged limit of
eight Sides in all. The worked example uses five.

For every payment row the schedule shows the quantity used, the part that
produced it, and the source quantity with its label (contract, scheduled,
allocated). Side inspection explains the same figures.

#### Persistence and admission

`trading.gas_component_terms` gains the share and the part with its levels.
Their consumers are composition booking and the ticket. The quantity policy
stays in that table.

Database admission independently checks that each payment's ratio is its share
times its source's stage ratio, that its dates lie within its source's term,
and that a part meets its limits. The index payment check admits the two named
forms on the delivery-index implementation, with exact coverage for the
payment's own days. No row layout is added.

#### Billing

Daily fixed cash billing charges the whole final allocated quantity, and the
database recomputes the same amount. Both refuse an entry that carries a share
other than 1, a part other than whole, or dates shorter than its source's
term. A refusal in only one would leave the two agreeing on a wrong amount.

#### Risk and reports

Risk still reprices under base, up and down scenarios and buckets the
differences. A part depends on quantity, not price, so a scenario does not
change which part a row takes. A quantity change with no price change lands in
the P&L residual, as under D445.

### What another commodity reuses

Every sided Instrument has the plain schedule. Crude, LNG, operating rights
and certificate forwards are valued on the same saved-program base as gas, so
the entry terms, the day-quantity helper and the single evaluation are
available to them. Each adopts them when it has a consumer and supplies its
own recorded quantities.

## Build order

Each stage leaves every check passing and every existing number unchanged.

1. **The contract.** The schedule on the sided base, the sealed combining
   step, conditions in place of check-only overrides, and inspection on the
   evaluated schedule.
2. **Gas through its Instrument.** Entry terms in identity, the single
   evaluation, preview through the saved form, display from the evaluated
   schedule.
3. **Gas pricing.** Share, part, partial term, index choice, seven payment
   Sides, billing refusals and the ticket.

## Verification

- **Unchanged numbers.** Every existing managed, database, API and browser
  check passes without a changed expected value. The one exception is a check
  that books the retired strict named daily form: it moves to the named daily
  form, and its assertion of the strict refusal becomes a labelled forecast.
- **Unchanged identities.** The identity of every existing kind of Instrument
  without a quantity policy is byte-identical before and after.
- **Plain schedule.** For each sided Instrument class, the schedule's
  evaluation equals the sum of its Sides' totals × ratio × coefficient,
  computed independently in the check. Work counts do not rise.
- **Reuse.** A Side held by two Instruments is evaluated once per scenario
  context.
- **Routes.** Preview, blotter, single-trade read and risk give the same
  amounts for the D360 and D445 examples and for a trade with a part.
- **Oracle.** The worked example, computed independently of the
  implementation, for value and displayed quantities. A second example for
  accrued cash on ended days with published prices and a final allocation.
- **Parts.** A zero part, an open band, a shortfall, a partial term, a source
  with and without the policy, a share on display, and scaling with trade size.
- **Refusals.** Every booking refusal, a part without day quantities, days
  selected under another policy, the booking form, and billing in both the API
  and the database.
- **Index choice.** Daily and monthly named indexes, the point's own index, a
  missing contract, a missing fixing, and an index with no curve or several.
- **Knowledge.** A later quantity record does not enter an earlier view.
- **Admission.** API and database agree on every accepted and refused booking.
- **Ticket.** Browser checks for the new fields, review, booking and schedule.
- **Benchmark.** One portfolio valuation workload before and after, stating
  unique contracts, scenarios, evaluated work, memory and environment.
- **Review.** One independent adversarial review of numerical behavior,
  temporal behavior and scenario reuse before publication.

## Decisions to record

- A new decision for the Instrument schedule and the line. It replaces the
  architecture note that an Instrument-level schedule is not implemented. D181
  stays. Architecture section 5.4, which proposes an option Side, is left for
  the option block to settle in favour of an option Instrument.
- D445 is revised: the policy is an entry term in identity, and day quantities
  are worked out in the Instrument's schedule. D417's rule that each Side owns
  its accrued and paid rows stays.
- A new decision for share, part and partial-term payment entries.
- The rule that an index payment uses the delivery point's own index (D350,
  and the point publication rule of migration 0229) is replaced by explicit
  index choice under one fixing rule for new bookings.

## Adopted calls

The implementation contract adopts these calls (D446–D447).

1. **Fixing rule.** New named index payments use the published price as soon
   as it is available, as point publications do. Today's stricter named daily
   rule is no longer offered for new bookings. This changes financial behavior
   for one existing kind of booking.
2. **Reach.** Every sided Instrument gets the schedule and the sealed
   combining step. Only gas is moved onto an overriding schedule.
3. **Where gas carries its terms.** On the saved-program base that booked
   physical products share, not on a new Instrument class.
4. The D445 policy moves into the saved Instrument's identity.
5. A trade with a part is always valued with day quantities and refuses
   without them.
6. A part needs an independent source and no fuel link.
7. Levels are daily, entered as quantities and saved as ratios of the base
   quantity.
8. A share is folded into the entry's quantity ratio, and a date range is the
   payment Side's own dates.
9. The limit of eight Sides in all is unchanged.
10. A named index resolves its curve through the settlement target; none or
    several refuses. The alternative is to name the curve in the request.
11. Billing refuses these entries for now.
12. The record below is kept in this document.

## Recorded, not changed here

A read-only audit on 23 November 2026 compared the code with the line. These
points depart from it. None is changed here.

- The crude, LNG, structured power and certificate forward stores add up Sides
  themselves, with signs from the request or the trade row.
- A swap in its settlement period takes its value from the settlement state,
  which recomputes cash without calling the Sides.
- Power contract Sides declare an input that names a sibling entry's quantity.
- Seven value Sides compute in `PV` and their schedule calls back into it.
- Park and loan, park custody and financial transport are saved as agreements
  and become several Instruments at valuation.
- Storage quantities come from a separately revisioned plan.
- One indexed gas purchase Instrument values only its payment Side, and only
  checks construct the state it needs. One forward class has no booking path.

## Left to the builder

- The shape of the Instrument schedule, its row view and its conditions.
- How an occurrence carries day quantities to a Side.
- How day quantities are built for a source with no policy.
- Field and column names, migration numbering, refusal codes and the structure
  of the admission functions.
- How the ticket lays out the level fields and the pricing choice.
- The order of work inside each stage.

## Implemented interfaces

The retained gas schedule also carries an explicit physical owner map for every
entry, sourced from existing component relationships even when no policy or
part is present. The map is in retained schedule identity and prepared-context
admission, preserving legacy economic canonical bytes. Lifecycle geometry is
per owner; unrelated physical quantities and gas clocks cannot affect a payment.

The managed quantity-part levels use nonnegative validated `SignedRatio`, because
`ExactRatio` represents strictly positive ratios and cannot express a band from
zero. Stored levels remain exact reduced numerator/denominator pairs. Saved
policy and part are immutable Instrument-entry identity fields; physical owner
days are facts supplied by held state. Null identity fields preserve predecessor
canonical bytes.

A saved trade's selected quantities can be previewed by explicit `previewTradeId`
and `previewContextId` under current authority and matching saved terms. An
unbooked draft has no operational owner. Held preview cannot book or amend.
Generic portfolio/risk reports carry per-entry evidence from the signed Side
contributions already evaluated; it is a separate partition from lifecycle
measures and adds no Side calls. Payable schedule quantities remain signed.

The ticket description supplies shared conditional visibility for part levels
and named index choice. An owned predecessor command read preserves completed
retry bytes after the ticket gains explicit share and date fields; fresh input
still meets current native admission. Forecast choices cover the payment's own
dates, and an unavailable assigned index does not propose a different index.
