# Quantity rights with limits across days: design

Status: design approved by the owner on 24 November 2026, after a review.
Not implemented. It builds on the
[Instrument schedule and gas pricing](2026-11-23-instrument-schedule-and-gas-pricing-design.md)
and [exercise rights](2026-11-23-exercise-rights-design.md) designs, on the
incomplete marker of the
[imbalance cash-out](2026-11-24-imbalance-cashout-design.md) design, and on
D445. Names of new types, fields and codes are proposals.

## Intent

Some gas contracts let one party choose each day's quantity inside a range,
under limits that span days: a total for the month or the term, and a limit on
how many days may leave the base quantity. A swing contract is the usual
example. The exercise rights design covers a choice between all and nothing,
one period at a time, and set limits across periods aside.

This design adds them. The holder elects a quantity for each gas day. A day
not yet elected carries the quantity of the holder's best plan on the forward
prices, inside every limit. Each Side then values its own rows from those
quantities, and the Instrument sums them.

The value is intrinsic: what the best plan is worth if prices turn out as the
forwards say. It adds nothing for uncertainty, and every result that includes
it says so.

The search that finds the plan is shared. Storage's plan proposal moves onto
it and gives the results it gives today.

## Where this sits

The review of physical gas products grouped the gaps into six building blocks.
This is the sixth. The exercise rights design names it as a non-goal: "Rights
with limits across periods: swing counts, storage, ratchets".

The repository already states what a swing right must do, in the
[field contract](../../physical-gas-field-contract.md) and in planned cases
SW01 to SW08 of the
[test plan](../../physical-commodity-test-plan.md). Nothing is built. This
design builds a first part and says which parts it leaves.

## Goals

- A gas trade can state a right under which the holder elects each gas day's
  quantity inside a daily range.
- Limits across days: a least and a most total for each delivery month or for
  the term, and the most days whose quantity may differ from the base.
- An election is a recorded fact. One that breaks a limit is refused, in every
  view of time.
- Days not yet elected are valued on the holder's best plan on forward prices.
  The plan is found in valuation, and the same inputs give the same plan.
- Every total that includes planned days says the value is intrinsic.
- One search serves this right and storage's plan proposal.
- Value scales exactly with the trade's size.

## Non-goals

- Value above intrinsic: a price process, a daily volatility, or a correlation
  between forward months. None exists in the repository.
- Any change to how storage is valued. It stays on the plan in force.
- Recording a computed plan.
- Choosing among transport paths.
- One election for a whole month. Elections are by gas day.
- A count rule other than "differs from the base": comparing with the day
  before, separate allowances up and down, and a limit on the size of a change.
- A payment for missing a least total, and carrying quantity from one period
  to the next. Limits here are hard.
- A stated quantity that applies when no election arrives.
- A base quantity or a limit that changes inside the right's term.
- Totals for the month and for the term on one right, and a least total equal
  to the most total.
- A right over two physical entries, over an entry with a quantity part or a
  fuel link, or over a quantity stated as a period total.
- A cash-settled quantity right.
- Amending a trade that has recorded elections.
- Showing elections on the delivery workbench.
- Discounting. Gas compositions admit none today.
- Merging this search with the price lattice used by listed American options.

## Anti-goals

- No Side holds a limit, and no Side reads another Side.
- No base quantity and no zero stands in for a day that is not yet elected.
- No intrinsic value shown as the complete value of the right.
- No election inferred from a nomination, a schedule or a forecast.
- No plan recorded as a fact or as an election.
- No view of time in which recorded elections break a limit.
- No changed number for any existing trade or any storage proposal.

## Implementation baseline

File references are starting points; the builder confirms each. "Rights
branch" is the branch where the exercise rights design is being built.

| Piece | Behavior today | Where |
| --- | --- | --- |
| Exercise right | Largely built on the rights branch. One party chooses all or nothing for each period. An exercised day's entries have their own quantity; an abandoned or undecided day's have zero | The exercise rights design |
| Elections | One database command records elections for many periods of a trade, under the publication gate. An effective time must be at or before its period's deadline and may be later than the recording. A reader takes each period's latest revision effective by its business time, among those known at its cutoff. A withdrawal is a revision | Rights branch, migration `0394:66`, `:109`, `:116`, `:172` |
| Refusals on a right | A physical period past its deadline with no election refuses. A delivery record on a day that is not exercised refuses. A payment on a covered row with no exercised day refuses | Rights branch, `ExerciseRights.cs:65`, `:96`, `:110` |
| Forward amounts | The exercise valuation derives a period's forward amounts from its Sides' evaluated rows | Rights branch, `RetainedExerciseValuation.cs` |
| Undecided rule | Designed with transport: the right states how an undecided day is valued, by the spread model or as nothing. Both add an amount at the group | The transport design |
| Delivery quantities | Under the delivery schedule policy a day's quantity is its allocated final, allocated provisional or scheduled record, else the contract quantity. Nominations and confirmations do not count. A recorded quantity is absolute and is normalized by trade size times membership ratio | `GasDailyQuantitySelection.cs:42-57`, [daily quantities](../../physical-gas-daily-quantities.md) |
| Held days | A held day has a positive contract quantity, a selected quantity that may be zero, and one of four labels. Any label but contract needs evidence, and a day labelled contract must equal its contract quantity | `PhysicalDeliveryQuantities.cs:33-41` |
| Quantity basis | A physical side's quantity is stated per operating day, or as a period total split equally over its days | `GasCompositionStore.cs:132`, `GasQuantitySchedule.cs:138-143` |
| Unassessed days | Designed: a day with no known amount has none, and every total that includes one is marked incomplete with its count | The imbalance cash-out design |
| Scenarios | A trade is repriced in a scenario only when one of its declared inputs changed. Otherwise its base value is reused | `BookValuation.cs:510-528` |
| Valuation time | A gas trade is pinned to its valuation time. A moved clock refuses, and P&L reports its time effect as unavailable | `RetainedGasInstrument.cs:66` |
| Trade size | A gas Instrument scales with its trade's quantity. Each contract quantity is scaled once | `Instruments.cs:153`, `RetainedGasInstrument.cs:72-74` |
| Amendments | A gas amendment creates a new instrument. It refuses a trade with daily quantity records rather than infer continuity | [daily quantities](../../physical-gas-daily-quantities.md) |
| Storage plan search | Exact backward induction over inventory in whole steps from the opening inventory. Daily limits depend on the inventory band. Each day tries no flow first, then each size as a withdrawal before an injection, and keeps a candidate only when it is strictly better. It refuses a step its fuel does not suit, more than 4,001 levels, work above 20,000,000 and a problem with no plan | `GasStoragePlanProposals.cs:21-22`, `:47`, `:57`, `:74`, `:91-98`, D433 |
| Storage checks | The exhaustive check compares the objective only. The tie check covers flow against no flow only. One work refusal is checked, far past the bound | `GasStoragePlanChecks.cs:220-258` |
| Storage valuation | Values the plan in force as five operating Sides. A proposal records nothing | `GasStorageInstrument.cs`, D432 |
| Price lattice | Used only by the listed American option. Its state is a price node and a contract state from a closed list, in fixed-scale decimals. It cannot hold a running total or a count | `Options/BackwardInduction.cs:22-29` |
| Swing fields | Four fields on the physical side's ticket list, shown as "Coming soon": daily minimum, daily maximum, swing range and notice deadline | `GasTicket.cs:57-60` |
| Written contract | Elections are trade-owned events, separate from nomination, confirmation and allocation, and are validated against the event history at the selected effective and knowledge time. An intrinsic plan and a model with uncertainty are different valuation scopes. A missing option value cannot become zero or a fixed base quantity. A count-limited right states what counts as a change. Count and total limits need their own proof of scaling | `physical-gas-field-contract.md:154-187` |

## Approaches

1. **The plan's quantities are the day quantities of days not yet elected
   (chosen).** Each Side computes ordinary rows from them and the Instrument
   sums them. The schedule shows every planned day, and value is derived from
   the schedule.
2. **A value added at the group**, as the spread model adds one, with zero
   quantity on undecided days. Rejected: the plan's value is a sum of rows, and
   hiding them would leave the schedule without the days the value comes from.
3. **A recorded plan**, as storage has. Rejected by the owner, who chose a plan
   computed in valuation. A recorded plan would also sit beside elections and
   look like one.
4. **A model with uncertainty.** Not chosen by the owner for this build. It
   needs a daily volatility and a correlation between months, which no mark
   holds.
5. **A swing Side or a swing Instrument.** Ruled out by the object model: a
   right is a term of the Instrument over its own entries.

## Design

### The right

An exercise right gains a choice. `all_or_nothing` is the right as designed.
`daily_quantity` is new, and a right with it states:

| Field | Meaning |
| --- | --- |
| Driving entry | The physical entry whose daily quantity the holder elects. Its contract quantity is the base quantity |
| Daily minimum and maximum | The range an elected quantity must lie in. The minimum may be zero |
| Limit period | `delivery_month` or `term`: the span each total and the count apply to |
| Minimum and maximum total | Optional. The least and the most quantity over one limit period |
| Total basis | `per_period_v1`: the stated total applies to each limit period. `per_day_v1`: the stated quantity times the gas days of the period |
| Changed days | Optional. The most days in one limit period that may be changed |
| Changed day rule | `differs_from_base_v1`: a day is changed when its quantity differs from the base. Stated whenever changed days are |
| Plan step | The quantity step of the plan search |
| Undecided rule | `best_plan_on_forwards_v1` |

The right's period is the gas day, its settlement is physical, and its
exercise times are one deadline per gas day, generated and saved as the
exercise rights design states. Holder, entries and identity are unchanged.

What the right may cover:

- exactly one physical entry, which is its driving entry;
- that entry's quantity is stated per operating day, so the base is the same
  on every day. A period total is refused;
- that entry has no fuel link, and carries no quantity policy or the delivery
  schedule policy. The scheduled-only policy of the transport design is
  refused;
- every entry that takes its quantity from the driving entry is in the right,
  and takes that quantity times a fixed ratio. An entry with a quantity part
  is refused.

The base less the minimum, and the maximum less the minimum, are each a whole
number of steps. The minimum is at or below the base, and the base at or below
the maximum. Totals may be any quantities. When both are stated, the most
total exceeds the least by at least one step.

### A plan is a day quantity

An entry's contract quantity stays what it is today: the base, which is
positive and is the denominator of every day's fraction. The right decides the
day's selected quantity.

| The day | The driving entry's selected quantity |
| --- | --- |
| Elected | The delivery record its policy selects; with none, the elected quantity |
| Not elected, before its deadline | The planned quantity |
| Not elected, at or after its deadline | None. The day is unassessed |

A delivery record on a day that is not elected is kept and is not counted,
before the deadline and after it. Before the deadline the holder can still
change the day. After it the election is missing, and a record does not stand
in for one.

The other entries follow by their ratios. Each Side computes its rows, and the
Instrument sums them. Nothing is added at the group.

A selected quantity carries a label. Two are new beside the four held labels:
`elected`, with the election as its evidence, and `planned`, with no evidence
and the rule's name.

For a `daily_quantity` right these replace three rules the exercise rights
design sets for an all-or-nothing right: zero quantity on an undecided day,
the refusal of a period past its deadline, and the refusal of a delivery
record on a day that is not exercised. That design's fourth rule is kept, with
elected in place of exercised: a covered payment row needs an elected day
before it can carry a recorded payment.

The two existing undecided rules add an amount at the group. This one puts
quantity in rows.

### The limits

All limits are counted on elected and planned quantities, never on scheduled
or delivered ones. An operator's cut does not use up a right.

A gas day belongs to the delivery month of its operating date. For one limit
period:

- the total is the sum of its days' elected and planned quantities;
- a day is changed when its elected or planned quantity differs from the base.

With `per_day_v1`, a period's limit is the stated quantity times its gas days,
so a short month and a part month are limited in proportion. With
`per_period_v1` the stated total applies to every period as written. The count
is not adjusted for a part month.

**Whether a plan exists** needs no search. Take a period with elected total E,
n days not elected, and k of them free to change: n with no count, else the
changed days left, at most n. A plan exists exactly when the changed days used
do not exceed the count, and the range from E + n × base − k × (base −
minimum) to E + n × base + k × (maximum − base) meets the range from the least
total to the most. Because the totals are at least a step apart, a plan on the
grid exists whenever one exists at all.

Booking refuses a right when any limit period has no plan, or when its search
would exceed the bound stated below.

### The best plan

For each limit period, a valuation finds the plan for its days that are not
elected and not past their deadline, on the prices of the scenario being
valued.

- **What a quantity is worth.** A day's unit amount is what the right's rows
  would sum to if the driving entry carried one unit on that day and nothing
  on any other, on the scenario's prices and ignoring delivery records. Rows
  are linear in day quantities and every entry follows by a fixed ratio, so a
  quantity is worth its size times the unit amount. The rights branch already
  derives a period's forward amounts from its Sides' evaluated rows, and the
  same route gives the unit amounts. The Sides are evaluated once for the
  unit amounts and again with the final quantities, so a trade with such a
  right is evaluated twice in each valuation.
- **Whose plan.** The plan is the best for the holder. When the counterparty
  holds the right, that is the plan worst for us.
- **What it starts from.** The period's elected total and its count of changed
  days.
- **What it must meet.** The daily range, the totals and the changed-day
  count.
- **Ties.** Among equally good plans, the first day takes the quantity nearest
  the base, the lower one when two are equally near; then the second day, and
  so on.

Planned quantities are the daily minimum plus whole steps. The plan is the
best of the plans on that grid. When an election or a total is off the grid,
it can fall short of the best plan the contract allows, by less than one
step's value on one day of the period.

A limit period with a total or a count and an unassessed day has no plan: its
running total is not known. Its days not yet elected are unassessed too. A
right with daily limits only has no running total, so its other days are
planned as usual.

A day with no forward price refuses as its rows do today. If a period should
have a plan and the search finds none, the valuation refuses with a structured
error.

**What the value is.** It is the holder's intrinsic value, which is a lower
bound of what the right is worth to the holder. When we hold the right our
value is understated. When the counterparty holds it our value is overstated.
The result states the model,
`best_plan_on_forwards_v1`, and the scope, `intrinsic`. The name fixes the
search, its tie rule and its grid. A change to any of them is a new name.

### The shared search

One search, proposed as `LevelPlanSearch` in the shared numerics, finds the
best sequence of daily moves of one level.

| Input | Storage | Quantity right |
| --- | --- | --- |
| Level | Inventory, in whole steps from the opening inventory | Steps taken above the daily minimum, summed over the planned days |
| Moves allowed from a level | Up to the band's injection limit, down to its withdrawal limit, inside capacity | From none to the daily maximum less the minimum, inside the most total |
| Value of a move, for each day | Two integers per step: the cost going up, the value going down | One integer per step going up |
| Rest move | No flow | The base less the minimum |
| Final level | Inside the terminal range | Inside the least and the most total, rounded inward to the grid |
| Changed days | Not kept | The changed days left, as a second part of the state |

The search is exact integer arithmetic. A caller whose values are fractions
puts them on a common denominator first. The tie rule is the one above, stated
on moves: the first day takes the move nearest its rest move, the lower one
when two are equally near. With a rest move of no flow, that is storage's rule
today.

It refuses a problem beyond the caller's number of levels or amount of work.
Work is days times the moves from every level, times one more than the changed
days when a count is kept. It never truncates. Storage keeps its 4,001 levels
and 20,000,000. The right states its own two bounds, set from a measured run.

`GasStoragePlanProposals.Propose` keeps its signature, its implementation
name, its checks of the contract and the step, its refusal codes and its
results. Before the code moves, three checks are added, because today's checks
do not pin them: a withdrawal against an injection of equal size and equal
value, and the level bound and the work bound each just under and just over.

### Elections

An election is the exercise rights design's fact with a quantity in place of
an outcome. It names the trade, the gas day, the party that made it and the
quantity, with evidence and an effective time. It keeps that design's rules:
an authorized command, a retry that returns its first outcome, a change as a
new revision with its own knowledge time, and withdrawal. One command can
record a range of days.

Recording refuses an election:

- made by a party that does not hold the right;
- with an effective time after its day's deadline;
- for a day outside the right;
- outside the daily range;
- that breaks a limit, as follows.

**The limit check.** An effective time may be later than the recording, and a
reader takes the revisions effective by its own time. So the elections in
force differ from one instant to another, and each must leave a plan. For a
new revision effective at time t, the check takes t and every later effective
time of a known election in the same limit period. At each, the elections in
force then, with the new one, must leave the period a plan by the rule in
[The limits](#the-limits). A day with no election in force counts as not
elected, whether or not its deadline has passed.

The check runs inside the database command, under the publication gate, where
elections are recorded today. A command's elections are checked together, as
the state they leave, and two commands cannot each pass and together break a
total.

Every view is then safe. A view at an earlier knowledge cutoff sees a state
that some check passed, and so does a view at any business time.

A refused election changes nothing. A recorded quantity is absolute and is
normalized as D445 normalizes a delivery quantity. An election need not be on
the plan's grid.

A revision is admitted whenever its effective time is at or before its day's
deadline, however late it is recorded, and passes the same check. A withdrawal
needs no check: freeing a day never removes a plan. A withdrawal after the
deadline leaves the day unassessed.

A trade booked after some deadlines have passed shows those days unassessed
until their elections are recorded.

Amending a trade that has recorded elections of a `daily_quantity` right is
refused, as it is for a trade with daily quantity records.

### Risk and time

Each scenario finds its own plan on its own prices. This is the repricing the
valuation boundary requires: the Instrument resolves prices from the scenario
it is given, and nothing is carried over from the base.

- A trade is repriced only in scenarios that change one of its inputs, so the
  search runs once for the base and once for each such scenario.
- Where a shock leaves the plan unchanged, the difference is the planned
  quantity times the shock. Where it changes the plan, the difference includes
  that change.
- No new input is declared. The right has no volatility.
- The trade stays pinned to its valuation time, as gas trades are. Each
  valuation sees the deadlines that have passed at its own time.

### Trade size

Every quantity of the right is held as an exact ratio to the driving entry's
contract daily quantity: the range, the totals and the step. The trade's size
then applies once, as it does to every gas quantity. A count needs no scaling.

The plan is the same at every trade size, and its value is in proportion. A
check values one contract at two sizes, with elections in proportion, and
requires both.

### What the result shows

For each limit period: its limits, its elected total, its planned total, its
changed days and its count of unassessed days.

For each day: elected, planned or unassessed, its quantity and its label.

A total that includes planned days is marked as including intrinsic value,
with the model and the count of planned days. Unassessed days carry the
incomplete marker of the imbalance cash-out design. Both markers travel
together, on the entry, the trade, the portfolio and the risk result.

### Worked example

Synthetic, no discounting. We buy gas at a fixed 3.00 and hold the right. Base
1,000 a day, daily range 700 to 1,100, step 100. Three days, with point
forwards of 3.20, 3.05 and 3.10, so one unit is worth +0.20, +0.05 and +0.10
to us.

| Limits | Plan | Value |
| --- | --- | --- |
| None beyond the daily range | 1,100, 1,100, 1,100 | +385 |
| Most total 3,100 | 1,100, 900, 1,100 | +375 |
| Most total 3,100 and one changed day | 1,100, 1,000, 1,000 | +370 |
| For comparison, the base quantity every day | 1,000, 1,000, 1,000 | +350 |

The remaining cases use a most total of 3,100 and no count.

- **The seller holds the right:** the plan is 700, 700, 700, and +245 for us.
- **We sell at 3.00 and the buyer holds the right:** the plan is 1,100, 900,
  1,100, and −375 for us.
- **Day 1 elected at 1,100:** the other days plan 900 and 1,100, worth +155.
  With day 1 at +220 the trade is +375.
- **Day 1 elected at 700:** the other days plan 1,100 and 1,100, worth +165.
  The trade is +305.
- **Day 1 elected at 1,050, off the grid:** the other days plan 900 and 1,100,
  and the trade is +365. The contract would allow 950 and 1,100 for +367.50.
- **A most total of 3,150, off the grid:** the plan is still 1,100, 900,
  1,100 for +375. The contract would allow 1,100, 950, 1,100 for +377.50.
- **Equal forwards of 3.10 on all three days:** the plan is 1,000, 1,000,
  1,100, by the tie rule, worth +310.
- **A price equal to the fixed price on all three days:** the plan is the base
  on every day, worth 0.
- **Forwards of 2.80, 2.95 and 2.90 with a least total of 2,500:** the plan is
  700, 1,100, 700, worth −265.
- **A scenario with every forward up 0.01:** the plan is unchanged and the
  value is +406, which is +375 plus 3,100 times 0.01.
- **A scenario with day 2's forward down 0.10:** the plan becomes 1,100, 700,
  1,100 and the value is +295.
- **Ten times the trade size:** the plan is 11,000, 9,000, 11,000 and the
  value is +3,750.

An election case. Three elections of 1,100 against a most total of 3,100
refuse on the third. The check must also catch this sequence: day 1 is elected
at 1,100, effective Monday 09:00; it is then revised to 700, effective Tuesday
08:00; days 2 and 3 are then elected at 1,100 each, effective Monday 11:00.
The latest revisions total 2,900, but the elections in force on Monday
afternoon total 3,300. The elections for days 2 and 3 are refused.

The values are computed by listing every plan, independently of the
implementation.

### Booking, ticket and display

Gas composition booking accepts the new choice and its fields. The ticket's
"Exercise right" section gains, one field per row: the choice, the daily
minimum, the daily maximum, the limit period, the minimum total, the maximum
total, the total basis, the changed days, the changed day rule and the plan
step. Quantities are entered at the trade's size, and the booking operation
converts them.

The four "Coming soon" fields leave the physical side's list. Daily minimum,
daily maximum and notice deadline, which is the exercise time rule, are wired
in the right's section. Swing range is removed, since the minimum and the
maximum state it.

A booked trade's action to record elections takes a quantity for each day.

The schedule shows what [the result shows](#what-the-result-shows).

### Persistence and admission

The new fields are stored with the right and enter the Instrument's identity.
The identity writer skips them when the choice is all or nothing, so a right
as designed keeps its identity. The election fact gains a quantity and the
party that made it.

Database admission checks each right independently: the choice and its rule
agree; a `daily_quantity` right is by gas day and physical; everything under
[The right](#the-right) about what it may cover; its range, steps and totals;
and that every limit period has a plan.

### Known limits

- **Intrinsic only.** The right is worth more than this value to its holder.
  A right we sold is carried at more than it is worth to us.
- **Forwards are monthly.** Days of one month share a forward, so inside a
  month the plan is decided by the tie rule. Value from daily prices appears
  only as days are elected.
- **P&L between two dates.** Each date finds its own plan. The part of a
  change in value that comes from a changed plan is not attributed to a price
  factor. It is at or above zero when we hold the right, and at or below when
  the counterparty does. A month's exposure can also move between runs when
  two months' forwards cross.
- **Time.** The trade is pinned, so a projection to a later clock refuses and
  P&L's time effect is unavailable, as for gas trades today.
- **A missed election.** One day past its deadline with no election leaves the
  rest of its limit period unassessed until the election is recorded. Under a
  term limit that is the rest of the contract.
- **Hard limits.** A contract that lets the holder miss a least total against
  a payment cannot be stated.
- **The delivery desk.** The delivery workbench and the quantities read still
  show the base as the contract quantity. Elections appear on the trade.
- **Work.** A long term with a fine step or a large count can exceed the bound
  and is refused at booking. The remedy is a larger step.

### What another commodity reuses

The choice, the limits, the plan as day quantities, elections with a quantity
and the search are on the shared base. Each commodity adds booking and its
election command when it has a consumer.

## Build order

This build needs the exercise rights build and the incomplete marker of the
imbalance cash-out build. It does not need transport. Both change the same
right, so they are built one after the other, in either order, and the second
adds its undecided rule beside the first's. Each stage leaves every check
passing.

1. **The search:** the three added storage checks, `LevelPlanSearch`, and
   storage's proposal moved onto it.
2. **The right and its plan:** the choice and its fields, the day quantity
   rule, the two labels, the best plan in each scenario, the intrinsic marker,
   and scaling.
3. **Elections:** the quantity and the party on the fact, the limit check in
   the database command, unassessed days, and the amendment refusal.
4. **Booking, the ticket and the schedule.**
5. **Documents:** the field contract and the test plan state what is built and
   what is left.

## Verification

- **Unchanged numbers.** Every existing check passes without a changed
  expected value, including every storage proposal check.
- **Oracle.** Every row and case of the worked example.
- **The search.** Agreement of the plan, not only the objective, with a
  listing of every plan on small problems, with and without a count; the tie
  rule; a problem with no plan; both bounds, just under and just over.
- **Limits.** Daily range, each total, each basis, a short month, a part
  month, the count, a count that resets by month, totals a step apart, and the
  rule for whether a plan exists against a listing.
- **What a right may cover.** Each refusal: two physical entries, a period
  total, a fuel link, the scheduled-only policy, a follower outside the right,
  a quantity part.
- **Elections.** Planned cases SW01, SW02, SW04, SW05 and SW06; the count part
  of SW03 for the base comparison; a retry; a revision recorded late; a
  withdrawal before and after the deadline; an election off the grid; the
  effective-time sequence of the worked example; two commands recorded
  together.
- **Knowledge.** Planned case SW08: an election seen before and after its
  knowledge time and before and after a correction. A later election never
  enters an earlier view.
- **Unassessed.** A missed day with daily limits only; a missed day under a
  total; a delivery record on a missed day; a trade booked after deadlines;
  the marker and its count on every total.
- **Scope.** Planned case SW07: the intrinsic marker on the entry, the trade,
  the portfolio and the risk result; a missing forward price refuses.
- **Risk.** A scenario that leaves the plan unchanged and one that changes it;
  a scenario that changes none of the trade's inputs reuses its base value;
  identical inputs give an identical plan.
- **Size.** One contract at two trade sizes, with elections in proportion: the
  same plan, values in proportion.
- **Holder.** The same right held by us and by the counterparty, on a purchase
  and on a sale.
- **Amendment.** Refused once an election is recorded.
- **Routes.** Preview, reads, the Blotter and risk agree.
- **Ticket.** Browser checks for the fields, booking, an election and the
  schedule.
- **Work.** A measured run of the largest admitted problem, with its days,
  levels, count, time and memory.
- **Review.** One independent adversarial review of numerical behavior and of
  temporal behavior of elections around the deadline.

## Decisions to record

- A new decision for quantity rights with limits. It extends the exercise
  rights decision with a choice, limits, a third undecided rule, a quantity on
  the election, two day labels and the intrinsic marker.
- A note on D433: storage's proposal uses the shared search, with unchanged
  results.

## Calls made in this draft

These are the author's, for the owner to confirm or change.

1. The plan's quantities are the day quantities of days not yet elected. The
   value is a sum of ordinary rows, and nothing is added at the group.
2. Each scenario finds its own plan. This reverses what the author first
   proposed, which was one plan found on the base and held through scenarios.
   Holding gives a difference that is exactly quantity times shock, but it
   makes a scenario's value depend on base prices, which the valuation
   boundary rules out, and it needs two mechanisms the code does not have.
3. A day past its deadline with no election is unassessed, and under a total
   or a count so is the rest of its limit period. The alternative is a stated
   quantity that applies when no election arrives.
4. Limits are hard. An election that breaks one is refused.
5. One count rule, named on the right: days that differ from the base, up or
   down together, reset each limit period.
6. One limit period on a right, month or term, with the total stated for the
   period or per gas day.
7. The plan step is a term of valuation, not of the contract. An election may
   be any quantity in the range. Whether a plan exists is judged on the
   contract, and totals a step apart make sure the grid has one too. The plan
   can fall short of the contract's best by less than one step on one day.
8. A delivery record on a day that is not elected is kept and not counted. A
   covered payment row needs an elected day.
9. Every total that includes planned days carries an intrinsic marker, up to
   the portfolio and the risk result.
10. The limit check covers every effective time, and runs in the database
    command.
11. Storage's proposal moves onto the shared search. Storage valuation is
    unchanged.
12. The right's quantities are held as ratios to the contract daily quantity,
    so value scales exactly.
13. A right covers one physical entry with a quantity per day, no fuel link
    and every follower inside it.
14. An election states who made it.
15. A trade with recorded elections cannot be amended.
16. The swing range ticket field is removed.

## Left to the builder

- How the two evaluations of the Sides are arranged inside one valuation.
- The search's interface, and the right's level and work bounds, set from the
  measured run.
- How the two labels and the intrinsic marker are carried.
- The shape of the election command for a range of days.
- Field names, labels, codes and migration numbering.
- The order of work inside each stage.
