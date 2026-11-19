# Heat-rate call option (D430)

Status: managed valuation and risk are implemented and locally checked. There is
no persistence, booking operation, ticket, exercise determination or settlement
yet; those are listed under [Remaining work](#remaining-work). Nothing here is
qualified for release.

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

## Marks

`HeatRateCallMarks` supply, for exactly each bucket the strip uses, an annualized
log-return volatility for the power forward, one for the fuel forward and their
correlation, under `spread_marks_log_return_held_v1`. They are a pricing binding:
they change `PricingBindingIdentity` and never the economic identity. A strip
without marks is unpriced; a missing, extra or duplicated bucket refuses. No
volatility or correlation is inferred from a price history, a listed option
surface or another product.

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

## Remaining work

1. Native persistence and an idempotent booking operation that retains the
   expanded exercises.
2. Published volatility and correlation marks with knowledge time, and their
   selection for a valuation.
3. Ticket, blotter, position and risk-workspace presentation.
4. Recorded daily exercise determinations from published index prices, and the
   resulting settlement cash.
5. A correlation sensitivity, once its shock contract is selected.
6. Intraday or hourly exercise, physical delivery, start costs, minimum run
   times and other unit constraints. The
   [supplied-plan thermal toll](structured-power-products.md) remains the
   contract for those.
