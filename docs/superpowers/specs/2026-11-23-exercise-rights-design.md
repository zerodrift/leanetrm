# Exercise rights on an Instrument

Status: built locally, D448. Migrations 0392–0396. The baseline below describes
the pre-change inputs. This build uses the
[Instrument schedule and gas pricing contract](2026-11-23-instrument-schedule-and-gas-pricing-design.md).
The implemented [gas contract](../../physical-gas-side.md#exercise-rights-d448)
and [verification](../../verification.md#exercise-rights-on-a-gas-instrument-d448)
record the supported scope and local results. Integrated CI is a separate merge gate.

## Intent

An option over a physical trade is not a separate thing that is traded. It is
a right over some of an Instrument's own Sides. One party may choose each
period whether those Sides' obligations happen. It is an artifact of how the
Instrument is priced.

This design lets an Instrument state such a right. Until the choice is made,
the Instrument values each period by a model of the holder's choice. Once the
choice is recorded, the period is the plain sum of those Sides, or nothing. No
option Side is added, no option Instrument is added, and exercise books no new
trade.

## Where this sits

The review of physical gas products grouped the gaps into six building blocks.
The third was listed as "a European option strip Side". This document replaces
that entry. It is the first user of the Instrument schedule whose result is not
a sum.

Listed futures options are exchange-traded objects and stay Instruments in
their own right (D288). They are not changed.

## Goals

- A gas Instrument can state an exercise right over a group of its entries.
- One payoff rule covers a call, a put, a cap, a floor, a spread and the
  greater or lesser of two indexes. Which it is follows from the Sides'
  directions and from who holds the right.
- Exercise is recorded as a fact. A period is valued before it and after it.
- The profile covers daily and monthly physical options with either holder and
  either a fixed or index strike. Cash applications include caps and floors on
  index payments, greater or lesser of two indexes, and a keep-whole that pays
  only when positive.
- Small price shocks reprice the supported positive-time stochastic model.

## Non-goals

- Listed futures options.
- One exercise over several delivery months. It needs a volatility for the
  whole strip, which no mark holds.
- Moving the heat-rate call onto this mechanism. See
  [The heat-rate call](#the-heat-rate-call).
- Conditional premiums. An independent premium uses a D449 lump-sum charge.
- Firm transport, where scheduling gas is the exercise.
- Rights with limits across periods such as swing counts, storage or ratchets.
- Early exercise inside a period, and any path-dependent payoff.
- A group with more than one floating price curve on the same side.
- Deemed exercise of a physical right.
- Instruments valued with discounting. Gas compositions admit none today.
- Inferring a volatility or a correlation, and shocking a correlation.
- Negative prices.
- A marks editor, and changes to delivery operations.

## Anti-goals

- A right introduces no new Side payoff or peer-Side access.
- No new traded object, and no trade booked by an exercise.
- No inferred exercise of a physical right. A physical period past its
  exercise time with no recorded election refuses.
- No silent change of model, and no fallback mark.
- No changed number for any existing trade.

## Implementation baseline

The references identify the starting implementation.

| Piece | Starting behavior | Where |
| --- | --- | --- |
| Instrument schedule | Designed and being built. The saved-program base, shared by gas, crude, LNG, operating rights and certificate forwards, overrides the schedule, works out each entry's day quantities once, and gives each Side its occurrence. Each Side computes its own rows | The design named in the status line |
| Fixed gas payment | One row for its whole term, paid after the term ends. It accrues by its ended days' quantities, and recorded payments are held against the row | `GasCompositionInstrument.cs:88`, `PhysicalLifecycle.cs:239-262` |
| Physical gas row | Quantity times the location mark. Its price is labelled projected, then actual once its curve contract expires. It is never a published fixing | `ScheduleMarketPrices.cs:78`, `PeriodPriceSelector.cs:97-104` |
| Discounting | Gas compositions admit none | `GasCompositionStore.cs:126` |
| Heat-rate call | One Side in a one-Side Instrument, holding a strip of delivery days. It reads power and fuel prices from the market itself. A day at or past its exercise time refuses | `HeatRateCallOption.cs`, D431 |
| Model | One European call on long less short less strike, by Kirk's approximation. It needs a positive long price, a positive short price and a positive short plus strike. With a short volatility of zero it is the single-price formula. There is no put form | `Options/KirkSpreadCall.cs:21-27` |
| Numerics | The model runs in a fixed-scale decimal. It rounds the result once, half-even, and exact arithmetic follows | `HeatRateCallOption.cs:150-158` |
| Marks | Each ordered pair of curves and delivery month carries two trader volatility marks and a correlation. The store refuses a curve paired with itself and two curves of one commodity. There is no mark for a single curve | `trading.spread_marks`, migration `0368:23`, `:69` |
| Volatility in valuation | An Instrument declares volatility inputs. The context supplies each by ID, and scenarios may shift volatility | `VolatilityInput.cs`, `RiskEngine.cs:86` |
| Listed option exercise | Instructions and events are recorded facts | `FuturesOptionLifecycleProjection.cs`, D288 |
| Daily gas facts | Day quantities are revisions with a predecessor, a status, evidence, withdrawal and knowledge time | [daily quantities](../../physical-gas-daily-quantities.md) |
| Architecture | Section 5.4 proposes an option Side with inputs supplied by the Instrument, names a joint evaluation as the alternative, and leaves the choice open | `docs/architecture.md:2297` |

## Approaches

1. **An exercise right as a term of the Instrument over its own entries
   (chosen).** The right is evaluated in the Instrument's schedule. After
   exercise the same Sides carry delivery, quantities and payments, with no
   hand-off.
2. **A separate option Instrument holding leg Sides.** The additional
   Instrument would duplicate identity and need a physical hand-off on exercise.
3. **An option Side**, as the heat-rate call is today and as architecture
   section 5.4 formerly proposed. This would restate its legs or read its peers.
4. **Exercise books a new trade**, as listed options do. A daily option could
   then book thirty trades in a month.

## Design

### The exercise right

A gas Instrument may carry exercise rights with the following fields.

| Field | Meaning |
| --- | --- |
| Entries | One or more of the Instrument's entries. An entry belongs to at most one right |
| Period | Each gas day or each delivery month groups the covered entries |
| Holder | Us or the counterparty |
| Settlement | A recorded election decides physical obligations. Published prices decide cash automatic obligations. |
| Exercise times | Physical settlement saves a deadline at or before the first gas day, generated at booking by a named rule. Cash automatic uses the latest fixing time among the group's rows. |
| Model | `spread_call_kirk_lognormal_v1`, at actual over 365 |

A right is part of the Instrument's definition and identity. The identity
writer skips it when absent, so an Instrument without one keeps its identity.
Entries outside every right are summed as they are today.

A physical right must cover a physical entry. A cash automatic right covers
payment entries only, since only an index payment has a published price.

A group is one right in one period.

### A decision is a day quantity

The Instrument already works out each entry's quantity for each gas day. A
right adds the decision to that step.

- on an exercised day, an entry in the right has the quantity its own terms
  give it.
- on an abandoned or an undecided day, it has zero.

Each Side then computes its rows from those quantities, as it does today. A
whole-term fixed payment therefore accrues only on exercised days, and its
recorded payments apply to the original row.

An Instrument with a right is always valued with held day quantities, as one
with a quantity part is.

### The value of a group

| State of the group | What the Instrument counts |
| --- | --- |
| Exercised | Its rows, as ordinary rows |
| Abandoned | Nothing |
| Undecided, before its exercise time | The model's holder-choice value C is positive for our right and negative for the counterparty's right |
| Undecided, physical, at or after its exercise time | Refuses, until an election is recorded |
| Undecided, cash automatic, at or after its exercise time | The same value with no time left, on the forecast prices, labelled forecast |

A group's forward amounts are what its rows would be if only that group's days
carried quantity, each entry having what its own terms would give it if the
group were exercised. An undecided physical group uses contract quantities.
Price times quantity is linear in the day quantities, so row allocation is
exact. An exercised group's Forward sum and Counted show gross selected
economics. Ordinary Side rows retain paid and outstanding balances, and PV
reconciles from those entries plus undecided contributions.

### The model

The group is valued from the holder's side. A row is certain when its price is
fixed by the contract or is a published index fixing. A market mark is
uncertain for exercise pricing, whatever its schedule label.

- A is the sum of uncertain amounts the holder receives.
- B is the sum of uncertain amounts the holder pays.
- c is the net certain amount the holder receives, negative when it pays.

The holder's payoff is the greater of A − B + c and zero. All uncertain rows on
one side must use one price curve, and the two sides must use different
curves. Otherwise the group refuses as outside this model.

| Case | C |
| --- | --- |
| c ≤ 0, A > 0, B > 0 | The call with long A, short B and strike −c |
| c < 0, A > 0, B = 0 | The call with long A, short −c at zero volatility, and strike 0 |
| c = 0, A > 0, B = 0 | A |
| c ≤ 0, A = 0 | 0 |
| c > 0, A > 0, B > 0 | The call with long B, short A and strike c, plus A − B + c |
| c > 0, A = 0, B > 0 | The call with long B, short c at zero volatility, and strike 0, plus c − B |
| c > 0, B = 0 | A + c |

"The call" is the installed formula, unchanged. The fifth and sixth rows use
put-call parity. Each call has a positive long amount, short amount and
short-plus-strike amount.

Because a right is always valued from its holder's side, the two parties to
one contract compute the same C and count it with opposite signs.

The model runs on the group's amounts. Each of the three amounts enters it
rounded half-even to 18 decimal places, because a daily quantity can be a
repeating fraction and the model's decimal type cannot hold one. An installed
call result is rounded once, half-even, to the working scale. Parity terms are
added exactly. Model time uses actual over 365 fixed instant from the calculation
instant to the group's exercise time.

### Marks

- A volatility mark for each price curve and delivery month.
- A correlation mark for each pair of curves and delivery month.
- A group uses the marks of its own delivery month.

These are two new revisable stores, with the rules of the existing spread
marks. The existing store keys both volatilities to a pair and refuses two
curves of one commodity.
It stays as it is for the heat-rate call.

Marks are trader marks held through price scenarios. They are pricing bindings
and stay outside identity. A group whose mark is missing is unpriced and
refuses. Nothing is inferred. When a published price leaves a group with one
uncertain side, it needs only that side's volatility.

### Exercise facts

A physical right is decided by elections. An election names a group and is
`exercised` or `abandoned`, with evidence and an effective time. It follows
the daily gas fact contract for authorized commands and withdrawals. A retry
returns its first outcome. A change records a new revision with its own
knowledge time. One command can record elections for many groups of a trade.

A group is decided at a valuation when an election effective by then is known
at that valuation's knowledge cutoff. A later election never enters an earlier
view. The same facts serve a right the counterparty holds.

A cash automatic group needs no election. It is decided when every index price
on its covered rows is published, including a row whose taken quantity is zero.
The outcome is exercised when the holder's sum is above zero and abandoned
otherwise. At or after its last fixing instant, an available projection supplies
the zero-time forecast until publication, including after the payment is due.

An active scheduled or allocated delivery on a covered physical membership
requires an exercised day even when valuation uses contract quantity. A payment
on a covered cash row requires at least one exercised day in that original row.
Either conflict refuses valuation of that holding.

### Evaluation

The saved-program schedule applies the day's decision and then counts the
undecided group contribution.

1. In the day quantity step, the rule of
   [A decision is a day quantity](#a-decision-is-a-day-quantity).
2. After the Sides have computed their rows, it adds, for each undecided
   group, the value in the table above. That value joins the unrealized
   component.

The result shows each group's state, forward sum and value counted, plus model
inputs for an undecided group. An undecided group's
value is reported at the group and is not divided among its Sides.

### What each product is

| Product | Entries in the right | Period | Holder | Settlement |
| --- | --- | --- | --- | --- |
| Daily call to buy gas at a fixed price | Physical receive, fixed payment | Day | Us | Physical |
| Daily put to sell gas | Physical deliver, payment received | Day | Us | Physical |
| The same with an index strike | Physical, index payment | Day | Us | Physical |
| Monthly option | The same entries | Month | Us | Physical |
| An option we sold | The same entries | Day or month | Counterparty | Physical |
| Cap on an index price we pay | Receive index, pay fixed cap | Day | Us | Cash automatic |
| Floor on an index price we receive | Pay index, receive fixed floor | Day | Us | Cash automatic |
| We pay the greater of indexes A and B | Pay A, receive B, beside a plain payment of B | Day | Counterparty | Cash automatic |
| Keep-whole only when positive | The two shortfall payments | Day | The protected party | Cash automatic |

### Worked example

Synthetic, no discounting. We may buy 10,000 a day at a fixed 3.00. The right
covers the physical Side and the fixed payment, by day, and we hold it.

| Day | State | Forward sum | Counted |
| --- | --- | --- | --- |
| D1 | Exercised at point price 3.10 | +1,000 | +1,000 |
| D2 | Abandoned at point price 2.95 | −500 | 0 |
| D3 | Undecided 30 days before exercise with point forward 3.20 and volatility 0.50 | +2,000 | +2,947.44 |

The same D3 written to a counterparty counts −947.44 for us. They hold the
right and their C is 947.44. With an index strike
in place of the fixed price (index forward 3.00, volatility 0.40, correlation
0.90), D3 counts +2,163.32. The three values are to two decimal places and are
computed independently of the implementation.

### Booking, ticket and display

Gas composition booking accepts rights. The ticket gains an "Exercise right"
section with fields for the covered Sides, exercise period, holder, settlement
and exercise time rule.

A booked trade gains an action to record elections for a range of periods.

The schedule shows each group with its state, forward sum and counted value.

### Persistence and admission

The rights and their saved exercise times are stored with the composition's
entries and enter the Instrument's identity. Elections are stored as revisions
against a trade binding and group. The two mark stores are new.

Database admission independently checks existing, disjoint entries. A physical
right covers a physical entry, and a cash automatic right covers only payment
entries. Each physical exercise time is at or before the first gas day of its
group.

### Risk and reports

An Instrument with an undecided group declares its volatility inputs and that
its value depends on the calculation time. Risk reprices under base, up and
down price scenarios and under volatility shifts. The closed formula needs
no pricing lattice step. A correlation has no shock, as under D431.

### The heat-rate call

It is not moved in this build. Three things it needs have no home on the Sides
it would use. These are its heat-rate factor, a signed capacity for a sold
strip, and a published price to decide an elapsed day. D431 left that last point
open. It keeps its own Side, its own marks and its present limits.

The model table above gives the same value as the heat-rate call for the same
inputs. A check states that, so the two cannot drift.

### What another commodity reuses

The retained schedule uses the common Side evaluation contract. Gas supplies
the implemented admission, group geometry, market binding and election facts.
Another commodity needs its own qualified consumer and business policies.

## Build order

Each stage leaves every check passing.

1. **Rights and value before exercise.** The term, grouping, the day quantity
   rule, both holders, the model table, declared marks, and refusal of a
   physical group at its exercise time.
2. **Exercise.** Election facts, decided groups, cash automatic decisions and
   the conflict refusals.
3. **Gas.** Booking, persistence, the two mark stores, the ticket, the
   election action and the schedule display.

## Acceptance checks

- **Unchanged numbers.** Existing expected values and no-right identities
  are acceptance baselines.
- **Oracle.** The worked example's five values.
- **Model.** Every row of the model table against an independent
  calculation. Parity between the first and fifth rows. Agreement with the
  heat-rate call on its documented inputs. Bought and sold values of one
  contract equal and opposite to the last digit.
- **Groups.** Day and month grouping, overlapping membership refusal, and
  refusals for two curves on one uncertain leg or one curve on both legs.
- **States.** Each row of the state table, for both holders.
- **Day quantities.** A whole-term fixed payment accrues only on exercised
  days. A part and share apply on exercised days. An abandoned day with a
  delivery record refuses.
- **Elections.** Retry, revision, withdrawal, a later election not entering an
  earlier view, and many groups in one command.
- **Cash automatic.** All index prices must publish to decide. Exact zero
  abandons. An unpublished price leaves a labelled forecast.
- **Routes.** Preview, reads, the Blotter and risk agree.
- **Risk.** Small price differences scale with the shock. A volatility shift
  reprices. A missing mark refuses.
- **Admission.** API and database agree on every accepted and refused booking.
- **Ticket.** Browser checks for the section, booking, recording an election
  and the schedule.
- **Review.** One independent adversarial review of numerical behavior,
  temporal behavior and authorization of the election command.

## Decision record

[D448](../../decisions.md#d448--exercise-rights-are-terms-of-an-instrument)
settles architecture section 5.4. The Instrument schedule evaluates the right
over its own memberships. D288 and D431 stand.

## Design calls recorded in D448

1. One exercise over several delivery months is left out, because it needs a
   strip volatility.
2. The heat-rate call is not moved in this build.
3. D449 supports a premium as an independent lump-sum charge on the same
   trade. A right naming the charge refuses.
4. A market mark is uncertain until exercise, whatever its label. Only a
   contract-fixed price or a published index fixing is certain.
5. A physical period needs a recorded election. There is no deemed exercise,
   and an elapsed period without one refuses.
6. A cash automatic group with a covered price still unpublished at or after
   its latest fixing instant uses an available forecast at zero model time,
   including after the payment due date.
7. An exact zero is abandoned.
8. A decision is applied as a day quantity of zero or the entry's own.
9. One payoff rule, valued from the holder's side, with no list of option
   types.
10. One price curve on each side of a group, and different curves on the two
    sides.
11. Two new mark stores, per curve and per pair, by delivery month.
12. A covered physical day with a scheduled or allocated delivery requires
    exercise. A payment on an original covered cash row requires at least one
    exercised day in that row.
13. Amounts enter the model rounded half-even to 18 decimal places.

## Implementation choices from the design

- The shape of the right, the group and the election, and their storage.
- The named rules that generate physical exercise times.
- How a group's forward amounts are read from each Side's rows.
- Field names, labels, codes and migration numbering.
- The order of work inside each stage.
