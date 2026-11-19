# Heat-rate call option (D431)

Status: a heat-rate call option can be entered from New trade, and booked,
marked, valued and listed through the API; it appears on the blotter with a
read-only detail view. Managed valuation is checked locally; persistence and the
HTTP operations are checked against a disposable database and preview. There is
no marks editor, premium or capacity-payment cash, exercise determination or
settlement yet; see
[Remaining work](#remaining-work). Nothing here is qualified for release.

## Contract

A generation toll written as an option: the holder may, each delivery day,
receive power at the market price against paying for fuel at a contractual heat
rate plus a strike per unit of energy. It is cash settled. For one delivery day:

```text
Payoff = Energy × max(PowerPrice − HeatRate × FuelPrice − Strike, 0)
```

`HeatRateCallInstrument` is a `SidedInstrument` with one `HeatRateCallSide`
membership at coefficient 1. The external trade quantity is capacity; a positive
quantity owns the option and a negative quantity has written it. Premiums and
capacity payments are separate cash bindings on the trade, as for other
structured contracts. The option Side contains no fee.

`HeatRateCallTerms` are the immutable economic definition:

| Term | Meaning |
| --- | --- |
| `Capacity` | Quantity reference of the external trade quantity |
| `PowerQuote`, `FuelQuote` | Outright quotes in one currency with a multiplier of one |
| `HeatRate` | `QuantityFactor` from the fuel quantity to the power quantity |
| `Strike` | Nonnegative price in the power price unit |
| `ExerciseStyle` | `daily_automatic_cash_exercise_v1` |
| `Model` | `spread_call_kirk_lognormal_v1` |
| `DayCount` | `act_365_fixed_instant_v1` |
| `Exercises` | Ordered delivery days |

Each `HeatRateCallExercise` states its delivery day, exercise instant, payment
instant, energy per unit of capacity, the exact power and fuel curve requests it
is valued from, and its mark bucket. Nothing is generated from a calendar or a
block name inside the Side: booking expands the period once and retains the rows.

## Exercise schedule

`HeatRateCallSchedule.Expand` turns a delivery period into those rows from
explicit inputs only:

- the delivery clock, interval length and hours block (`DeliveryHours`, with its
  holidays), which give each operating day's energy per MW; a day with no hour
  of the block has no exercise, and a 23- or 25-hour day keeps its actual energy;
- `HeatRateCallExerciseRule` (`business_day_lead_local_cutoff_v1`): the holder
  schedules a day at a local cutoff a stated number of business days before it,
  at least one, on a stated calendar. Days after a non-business day share one
  cutoff;
- the monthly `PaymentRule`, applied to each delivery month;
- each curve's dated contracts. A day needs exactly one contract delivering it
  on each curve; none or several refuses.

Each delivery month is one mark bucket with a stable identity. A period with no
deliverable hour, an uncovered calendar day or a payment before its exercise
refuses with `heat_rate_call_schedule`. Cutoffs, leads, blocks and calendars are
configuration supplied to the operation; none is built in.

The strike is nonnegative so that the fuel cost plus strike stays positive under
every price shock. Negative power prices are outside the lognormal model and
refuse.

## Booking

Migration `0367_heat_rate_call_bookings.sql` retains a booked option in
`trading.heat_rate_call_instruments` (terms, with exact-version references to
the market definition, both curves and quotes, the clock, the hours block, both
calendars and the payment rule), `trading.heat_rate_call_exercises` (one row per
operating day, each bound to the curve member it is valued from) and
`trading.heat_rate_call_trade_revisions` (book, parties and the signed capacity).
Rows are immutable and closed to the runtime role.

`security.book_heat_rate_call(catalog, body, prepared, valuation)` takes the
closed `heat_rate_call_trade_v1` command and the expanded exercises. It requires
booking authority and:

- selected `exerciseStyle`, `model`, `dayCount` and exercise rule versions; no
  alternative is substituted;
- two outright curves of different commodities in one currency with a
  multiplier of one, and a capacity quantity of the power commodity;
- exercises in strictly increasing delivery order inside the period, each
  exercised after the booking instant and no later than its delivery day, with
  one payment instant per delivery month and positive energy;
- for each day, the contract that is the only member of its curve delivering
  that day.

A retried command returns its first outcome; the same command identity with
different content refuses. The same terms, book and parties under a new command
identity are the same fact and return the existing trade unchanged, so a second
identical option is not created. A different quantity is a different trade.
The database does not recompute the hours block: energy per day is the managed
schedule's output and is retained as booked.

`security.read_heat_rate_call_positions` returns the retained terms and
exercises for trades visible under the selected knowledge context, to the
runtime and the read-only risk worker.

## Managed operations

| Route | Authority | Behavior |
| --- | --- | --- |
| `POST /heat-rate-calls/catalog` | `ReadTrades` | What the entry ticket chooses from under the selected working snapshot: outright curves with their commodity, currency and units, capacity quantities, clocks, calendars, published block products, payment conventions, the supported exercise, model, day-count and exercise-rule versions, and the booking directory. It states whether this account may book. |
| `POST /heat-rate-calls/book` | `BookTrade` | Resolves the clock, hours block, calendars, payment rule and both curves under the selected working snapshot, expands the exercises and calls the native booking. A replayed command returns its first outcome. |
| `POST /heat-rate-calls/status` | `ReadTrades` | The outcome of a booking command. |
| `POST /heat-rate-calls/get` | `ReadTrades` | One booked option as the blotter lists it, with its exercises; valued, or with the reason it is not. |
| `POST /spread-marks/record` | `ImportObservations` | Records marks effective at the selected working snapshot's instant. |
| `POST /spread-marks/read` | `ReadTrades` | The marks in force under the selected trading context. An account that may record marks but not read trades cannot read them back; see Remaining work. |

Booking and marking refuse a historical view. A booked option is an ordinary
position: it is in position facts, the single-position read, the risk trade
directory, portfolio valuation and the blotter, under instrument type
`heat_rate_call` and measure `heat_rate_call_present_value`. On the blotter each
option is valued on its own; one without marks for every delivery month, or
with an elapsed exercise day or a forward at or below zero, is listed with
that reason and no value. A
portfolio valuation or risk run that includes such an option refuses instead.

### Entry ticket

New trade lists the option under Structured. The ticket has one field per row
in stacked sections (contract, delivery, exercise, valuation, payment, trade)
and offers only what the catalog returned. Nothing is preselected, including
the single supported exercise style, model, day count and exercise rule: each
is chosen and sent as stated. The page narrows lists from the catalog's own
facts: a fuel curve is of another commodity in the power curve's currency, a
capacity unit is of the power curve's commodity, and a block product is on the
chosen clock. Buy or sell gives the sign of the capacity. Every other number is
sent as entered for the booking to judge, and a refusal is shown in the
backend's words. A request whose outcome is unknown is retained per person and
can be checked or retried as sent, never edited. Premium, capacity payment and
discounting are drawn as Coming soon and send nothing. The ticket does not
preview the expanded exercise days before booking; the booked option's detail
view lists them. While the ticket is open the assistant is told a heat-rate
call option is being entered (page identity `blotter_new_heat_rate_call`,
migration `0369_heat_rate_call_entry_page.sql`) and reads none of it.

Booked options are valued undiscounted: no discount binding is retained with
the option yet, and the valuation setup states that explicitly.

## Marks

`HeatRateCallMarks` supply, for exactly each bucket the strip uses, an annualized
log-return volatility for the power forward, one for the fuel forward and their
correlation, under `spread_marks_log_return_held_v1`. They are a pricing binding:
they change `PricingBindingIdentity` and never the economic identity. A strip
without marks is unpriced; a missing, extra or duplicated bucket refuses. No
volatility or correlation is inferred from a price history, a listed option
surface or another product.

Migration `0368_spread_option_marks.sql` stores the marks as market data shared
by every option on a curve pair: `trading.spread_marks` holds one revision chain
per market definition, long curve, short curve and delivery month.
`security.record_spread_marks` requires the observation-import authority that
trader price marks use, the selected convention, two outright curves of
different commodities in one currency, a volatility from 0 to 10 and a
correlation from -1 to 1. A new value requires the expected head revision and
an effective instant at or after the head's. From migration 0372, submitting
the same values at the same effective instant under a new request returns the
original mark revision, even after a correction.
Each month in the request follows this rule independently. A retried request
returns its first outcome. `security.read_spread_marks` returns, for the months
asked, the latest revision known and effective under the selected knowledge
context, and omits a month that has none. Valuation reads them this way; both
volatility inputs take their identity from the stored mark revision, so every
option on the pair and month shares them.

The two volatilities are declared as ordinary `VolatilityInput`s, so existing
volatility shifts and sensitivity axes reach them by input identity. Correlation
is read from the binding and is held through every scenario. There is no
correlation shock.

## Valuation

Each day ahead of the calculation clock is one European call on the spread,
valued by `KirkSpreadCall` with exact 30-digit decimal arithmetic:

```text
A      = HeatRate × Fuel + Strike
s      = FuelVolatility × HeatRate × Fuel / A
σ²     = (PowerVolatility − ρ·s)² + s²·(1 − ρ²)
d1     = (ln(Power / A) + σ²T / 2) / (σ√T),   d2 = d1 − σ√T
Premium = Power × N(d1) − A × N(d2)
```

`T` is the exact time from the calculation clock to the day's exercise instant
over a 365-day year. The forward premium is rounded once, half-even, to 18
digits, multiplied by the day's energy and discounted with the declared factor
for that day's payment. At a zero strike the formula is the exact exchange
option; with a certain fuel price it is Black-76 at the combined strike.

The Side resolves both forwards from the supplied scenario and declares both
curve requests, both volatilities per bucket and every payment's discount input.
Its time dependence runs from the calculation clock to the first exercise.
Risk is the ordinary base/up/down repricing; nothing allocates exposure between
power and fuel outside it. The model is smooth in both prices and declares no
price step.

A delivery day at or past its exercise instant is a determined cash amount.
This profile does not own that determination, so a strip with an elapsed day
refuses with `heat_rate_call_exercise` rather than valuing the day at zero or at
a forward price.

## Verification

`dotnet run --project tests/OpenEtrm.MarketData.Checks -- --spread-options`
runs 21 checks:

- The model against `tests/fixtures/spread_option_oracle.py`: ten synthetic
  cases agree with an independent 60-digit evaluation within 1e-24, and stay
  within 0.1% of the long price of a conditional numerical integral that does
  not use the approximation (largest observed distance 0.062%, at two years and
  zero correlation). Certain-fuel, zero-variance, monotonic and refusal cases.
- The instrument through the shared risk engine: base value, power and fuel
  shocks, a written strip, a volatility shock by input identity, a later clock,
  the declared inputs, economic versus pricing identity, and refusals for an
  elapsed day, incomplete marks and invalid terms.
- The schedule: a weekday peak block after a holiday, a period across two
  months, an all-hours block across a weekend and a 23-hour day, and refusals.

The default managed suite passes unchanged at 785 checks.

`python3 tools/check_heat_rate_call_storage.py --pg-bin <PostgreSQL 18 bin>`
applies the whole migration chain to an owned database and runs seven checks on
the booking: authority and closed tables, malformed commands, unselected
policies and unknown references, exercise ordering and contract coverage,
request replay, the same-fact no-op, immutability, and position reads at and
before the booking instant.

`python3 tools/check_spread_mark_storage.py --pg-bin <PostgreSQL 18 bin>` runs
five checks covering publication authority, bounded values, request replay,
revision order, immutability, historical reads and mixed batches containing a
superseded mark.

`web/tests/heat-rate-call-ticket.test.mjs` runs nine checks on the ticket's
state: nothing offered ahead of its parent choice, the narrowing of fuel curves,
capacity units and block products, a choice cleared rather than replaced when
it is no longer offered, the exact booking terms a complete entry becomes for
both payment rules and both directions, and each missing value asked for by
name.

`python3 tools/run_preview.py --check --structured-power-check` includes three
HTTP checks: the catalog offering exactly the terms the booking then states and
refusing an account without trade access; booking through the managed schedule with replay, the same-fact
no-op and refusals; an unmarked option listed as unpriced; marking authority,
replay and a stale refusal; a marked option valued; a higher power volatility
worth more; a written option valued at the exact negative; and both options as
ordinary positions. These checks assert the direction and symmetry of values.
The model's numbers are checked against the oracle in the managed suite, not
recomputed over HTTP. The ticket and the booked option's blotter detail were
looked at once by hand in a browser against the synthetic preview; no automated
browser check covers them. A risk run over a booked option has not been
exercised.

## Remaining work

1. A marks editor: marking exists only as an API route today. A read of marks
   and of a mark request's outcome for an account without trade access. A
   preview of the expanded exercise days on the ticket before booking.
2. Premium and capacity-payment cash bindings at booking.
3. A retained discount binding, so payments are discounted.
4. Recorded daily exercise determinations from published index prices, and the
   resulting settlement cash. Until then an option stops being valued when its
   first exercise instant passes.
5. A risk run and browser verification over booked options; a correlation
   sensitivity, once its shock contract is selected.
6. Amendment and close-out.
7. Intraday or hourly exercise, physical delivery, start costs, minimum run
   times and other unit constraints. The
   [supplied-plan thermal toll](structured-power-products.md) remains the
   contract for those.
