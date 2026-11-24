# Listed options on futures

Status: local implementation, verified as recorded in
[verification](verification.md#listed-american-futures-options-d288) and, for European
exercise, in [verification](verification.md#european-futures-options-d446), which also
lists the checks that have not yet been run. This is not a production activation
record. The decisions are [D288 and D446](decisions.md).

The first release covers two NYMEX products in the
[exchange futures example](exchange-futures.md#nymex-cl-and-listed-options):

| Product | Underlying | Exercise | Settlement |
| --- | --- | --- | --- |
| Henry Hub Natural Gas option (ON) | NYMEX NG, 10,000 MMBtu | American | Into one underlying futures contract per option |
| Crude Oil option (LO) | NYMEX CL, 1,000 bbl | American | Into one underlying futures contract per option |

Product facts come from the exchange's public pages, retrieved on 2026-11-01 and
recorded with their source rows: the
[ON](https://www.cmegroup.com/markets/energy/natural-gas/natural-gas.contractSpecs.options.html)
and [LO](https://www.cmegroup.com/markets/energy/crude-oil/light-sweet-crude.contractSpecs.options.html)
specifications and calendars, NYMEX rulebook chapters
[300](https://www.cmegroup.com/content/dam/cmegroup/rulebook/NYMEX/3/300.pdf),
[310](https://www.cmegroup.com/content/dam/cmegroup/rulebook/NYMEX/3/310.pdf) and
[370](https://www.cmegroup.com/content/dam/cmegroup/rulebook/NYMEX/3/370.pdf), and the
[strike listing and exercise procedures table](https://www.cmegroup.com/trading/files/strike-price/strike-price-listing-and-exercise-procedures-table.xlsx)
dated 2026-08-24. Only facts are stored; every price is synthetic.

A product row states its exercise style, `american` or `european` (D446). Both example
products are American and the example lists no European product: the checks of
[European exercise](#european-exercise) use a synthetic product on a synthetic
cash-settled future.

## Instrument

A listed option is its own immutable instrument. It references its exact underlying
`ref_data.futures_series` key and states its right, strike, expiry instant, exercise
style (`american` or `european`), settlement method `exercise_into_future` and the
versioned rules of its product. The exact `ref_data.futures_option_series` key is the identity; each
strike and right is a different instrument. Series exist only as explicit catalog
rows: the exchange's strike listing ranges and intervals are not modeled, so no
series is generated from them, and the example catalog's products and series carry
definition state `candidate`, not a production activation. The quantity basis is one option contract, and one contract exercises into one
underlying futures contract.

An option is not a side of another instrument. Exercise or assignment books a
separate futures obligation at the strike. Open question O13, which concerns an
option that controls sibling obligations inside one instrument, therefore remains
open only for embedded options.

Exchange specifics are reference data, never code:

- **Automatic exercise.** `nymex_itm_settlement_v1` evaluates `settlement_moneyness_v1`
  against the underlying's daily settlement on the option's last trading day: in the
  money by any amount is exercised; exactly at the money, calls are exercised and
  puts abandoned.
- **Contrary instructions.** The product row states whether they are accepted. ON
  and LO do not accept them, so exercise and abandon instructions are refused with
  `option_contrary_instructions_not_accepted`. The instruction path exists for
  products whose row accepts them, and such a row must state a cutoff. A European
  product accepts none: a check on the product row refuses one that does.
- **Close and expiry time.** A versioned setting gives a product's close-of-trading
  time of day, clock and source. NYMEX NG, CL, ON and LO use 14:30 New York under the
  operator qualification `operator:nymex-close-1430-new-york-v1`, which is not an
  exchange-published instant. SQL and the managed backend derive expiry and
  last-trading instants from the date and this setting and reject a stored instant
  that differs (`close_time_mismatch`). A product with no setting raises
  `close_time_setting_missing`.
- **Exercise-notice cutoffs.** The rule's 4:15 p.m. and 4:30 p.m. times are stored as
  source facts. The source states no time zone, so no intraday cutoff is derived or
  enforced.

## Booking

`options/book` books one trade holding two instruments:

- the option, at signed lots, from an explicit `side` (`buy` or `sell`) and unsigned
  `lots`;
- the premium, as a unit cash instrument (one USD, paid on the booked payment date)
  held at the signed premium amount: −premium × multiplier × lots, so a long pays and
  a short receives. Equal cash on the same date is one shared instrument.

For a delivery-hour option, the series supplies the energy in one underlying
futures lot. A quoted premium of 1.25 USD/MWh gives 400 USD per 320 MWh lot
and 460 USD per 368 MWh lot. The ticket shows the dated quantity and one-lot
premium at settlement. A fixed-multiplier product retains its own conversion.
For an hourly-lot future on an exchange in a different time zone, the trading
calendar supplies the venue clock and the delivery calendar supplies the local
profile clock.
Where the listing supplies a separate last-trade instant, new bookings close
at that instant. Exercise and expiry use the sourced notice and expiry times.

The premium payment date is explicit and may not precede the execution date, the UTC
date of the trade's execution; an earlier date is refused with `option_booking_input`.
A premium dated on the execution date is due from the start of that UTC day.
Payment is a separate recorded fact; due but unpaid premium remains in PV.

Premium style is a booked term. Only premium paid up front is accepted. A request for
margined premium is refused with `option_unsupported`, and a product whose own
premium is margined with `option_product_margined`. Booking is idempotent under its
request identity (D238) and needs a human booker with booking authority. A retry
returns its first outcome only while the booker can still see the series, as an event
retry does; otherwise it is refused as an unseen series.

## Valuation

The series' stated exercise style selects its model, and a series is never valued as
a style it is not. A European series is valued by a formula, described under
[European exercise](#european-exercise). The rest of this section is the American
model.

An American option is valued on a binomial tree of its underlying futures price, with zero
drift, by a project-owned backward-induction core that chooses hold or exercise
at every node. All arithmetic is fixed-point decimal with 30 fractional digits
and half-even rounding, with project-owned `exp`, `ln` and `sqrt`, so results do
not depend on a platform math library.

A calculation selects the tree by its option numerical policy. There are two.
`american_futures_option_crr_v1` is the one the Blotter, the risk catalog, the
trading P&L and volatility surfaces state. `american_futures_option_leisen_reimer_v1`
is selected only by a request that names it (D396); see
[the strike-centred tree](#the-strike-centred-tree).

| Setting | `american_futures_option_crr_v1` | `american_futures_option_leisen_reimer_v1` |
| --- | --- | --- |
| Tree | Cox-Ross-Rubinstein, rebuilt around each futures price | Leisen-Reimer with the Peizer-Pratt inversion, centred on the strike |
| Steps | 400 | 401; the count must be odd |
| Day count | `act_365_fixed_instant_v1`: exact elapsed time over a 365-day year | the same |
| Volatility bracket | 0.01 to 3, lowered per series where the tree's range ends (recorded) | the same; the range's end is found by halving |
| Bisection iterations | at most 30 | at most 30 |
| Step stability tolerance | 0.005: 400 steps agree with 1,600 on the convergence cases | 0.01: 401 steps agree with 1,605 on the same cases, within 0.001 on all but one |
| Accuracy at a unit discount, on the oracle's cases | \|tree − Black-76\| ≤ 0.125 · F · σ · √T / Steps | \|tree − Black-76\| ≤ 0.025 · F · σ · √T / Steps² |
| Price step | F · (exp(2 · σ · √(T ÷ Steps)) − 1) | none at a unit discount; under discounting the level spacing F · (u ÷ d − 1) |
| Money conversion | the 30-digit unit price is rounded once, half-even, to 18 digits before the multiplier | the same |

A discount factor above one (a negative rate) is rejected with `option_input`.

The implementation reuses a discounted continuation only within its lattice node
and next state, resolving it on first use. It retains successor accumulation,
rounding and first-decision tie order. Each lattice owns its successor arrays;
zero-intrinsic decisions and exact zero/one arithmetic avoid repeated allocations.
No calibration is reused across snapshots or scenarios. The owned workload check
pins the complete calibration receipt and an 800 MB allocation ceiling for one
synthetic at-the-money gas call: 400 steps, 13 tree prices and no scenarios. The
independent numerical oracles, recorded decisions and risk digest remain checks
of the unchanged financial contract; this resource bound is not a hosted latency
qualification.

**Base value equals settlement.** From the base market only, the implied volatility
is solved by bisection so that the tree reproduces the option's daily settlement
within half the series' clearing increment. The option and its underlying are read
under the same observation role (`daily_settlement`) from the same snapshot. The
receipt records the volatility, residual, iterations, upper bound, discount, base
futures price and settlement.

**Scenarios.** Under `option_implied_volatility_held_v1` the solved volatility is held
while futures factors are shocked, so delta and gamma come from the shared
base/up/down revaluations rather than from recalibration. The volatility is
recomputed from the pinned base inputs on every run, never stored as an observation;
a retained risk job recomputes it and refuses a different result with
`risk_option_calibration`. The option's declared pricing dependency is its underlying
only. Its settlement curve is a calibration input read from the base market, not a
risk factor, so no scenario shocks it; this is intentional. A new settlement revision
changes the snapshot, and every run recalibrates from its pinned inputs.

**Price step.** The rebuilt tree is laid out around each scenario futures price,
so its value is piecewise linear in that price. It bends where a lattice level
crosses the strike and, under discounting, far less where an exercise decision
changes. The strike crossings lie one price step apart: the futures
price times exp(2 · volatility · √(years ÷ steps)) − 1, which is 63 to 165 trading
ticks for the example's at-the-money calls. A solved option declares its tree's
step on its underlying futures dependency, at its base futures price and held
volatility. An option valued at intrinsic has no lattice and declares none. A
second difference of the value is meaningful only across whole steps. The
[trading P&L bump](trading-pnl.md#price-bump) is sized from the step, and the
portfolio risk bump report refuses a shock of the underlying under two steps
(D395).

**Intrinsic fallback.** When a settlement lies at or below the tree's price at the
bracket's lowest volatility, and no more than one clearing increment below intrinsic,
no volatility reproduces it. If the valuation policy declares
`option_settlement_at_intrinsic_v1`, the base value is the settlement and each
scenario value is max(0, scenario intrinsic + base residual) × multiplier. The mode is
shown on the Blotter row and the risk position, and the report counts fallback
positions. A policy that does not declare it raises
`option_calibration_fallback_undeclared`. Settlements further below intrinsic, above
the model's bound or without convergence raise named errors.

**Discounting and premium cash.** The tree discounts with the currency's existing
payment-discount selection, the one swaps use. Recorded payment allocations reduce
the outstanding premium under the selected knowledge cutoff. Before its payment
date, that remainder is discounted; due but unpaid cash remains nominal. Fully
paid cash contributes no remaining value under
`futures_option_value_excluding_settled_cash`. The row distinguishes pending, due
unpaid, partially paid and paid states. This supersedes D288's date-as-payment
premise (D386); booking a premium date does not record a payment.

**Exclusions.** A position the close cannot value is listed with its reason and no
value: `option_settlement_missing`, `option_underlying_missing`, or `option_expired`
for a held series at or past expiry with no recorded expiry (visibly unprocessed). A
pending premium, or an option's expiry, dated outside the selected rate curve's
pillars is listed with `discount_coverage`; the curve is not extrapolated, and the
rest of the book is valued (D290). A currency without a discount default fails the
read with `discount_curve_missing`.

### The strike-centred tree

`american_futures_option_leisen_reimer_v1` (D396) places the tree so that the
strike lies between its two middle terminal nodes at every futures price. No
level then crosses the strike as the price moves. Its up probability is the
Peizer-Pratt inversion h at Black-76's d2, its up factor is h(d1) ÷ h(d2) and its
down factor is (1 − h(d1)) ÷ (1 − h(d2)), so the futures price stays a
martingale. The exercise decisions, the solver, the scenario policy and the
intrinsic fallback are the rebuilt tree's.

It is built and checked, and a request may select it in its pricing policy's
`futuresOptions.numericalPolicy`. Nothing states it: the Blotter, the risk
catalog, the trading P&L and volatility surfaces use the rebuilt tree, and the
stored volatility-surface tables accept only that policy. Whether to make it the
stated policy is undecided. What the checks pin:

- **Values.** The tree equals an independent 60-digit evaluation of the same
  lattice to 22 decimal places, one case within 10⁻¹³ of d2 = 0 included. At a
  unit discount it is within 0.025 · F · σ · √T / Steps² of Black-76 on the
  oracle's cases, at 101 steps as at 401; at the pinned step counts that bound
  is a two-thousandth of the rebuilt tree's. It is not a bound for every option:
  two deviations out of the money the constant is 0.026 where volatility times
  root time is one and 0.053 where it is two. Under discounting the error is
  first order: against an independent 20,000-step tree it is within
  0.00005 · F · σ · √T for 33 of the oracle's 34 options and within 0.0002 for
  the other, a put far in the money two years out at a discount of 0.9, where
  the rebuilt tree is closer. Hundreds of deviations from the money the tree is
  worth exactly intrinsic.
- **Range.** The tree's highest node mirrors the futures price about the strike,
  so its range depends on both prices. A strike far above the futures price can
  leave no volatility to solve for, where the rebuilt tree values the option;
  the refusal names both prices.
- **Undiscounted Greeks.** At a unit discount nothing is exercised early and the
  value is smooth in the futures price. A one-tick delta is within 0.001 points
  of Black-76 and a one-tick gamma within 0.005%, on 39 listed options and on 48
  drawn from a fixed sequence; the three delta estimates agree within 0.0001.
- **Discounted Greeks.** Under discounting the exercise boundary crosses the
  tree's levels as the price moves, and the value keeps an oscillation of about
  one level spacing. One tick lies inside it. On the 48 drawn options discounted
  at 5% a year, against an independent tree: 47 one-tick deltas agree within
  0.0001 and all are within 0.15 points, but nine one-tick gammas are over 2% off
  and seven over 5%, all seven in the money, and the P&L's 10% agreement would
  use one that is 23% off. So the tree declares its level spacing as its price
  step whenever it is discounted, and the same consumers size and refuse by it.
  At two such steps every one of those gammas is within 2% and every delta within
  0.4 points. Of the oracle's 31 discounted options with a delta to judge, 24
  have gamma estimates at two steps that agree within 10%, each within 4% of an
  independent tree; the seven that do not agree have a volatility times root
  time of 1.4 or more, and a single pair there reads up to 9% off.

Measured on 2026-11-13 and pinned by no check. One tree price takes 1.3 to 1.4
times the rebuilt tree's on the development machine: a node's price is a product
of two factors rather than a lookup. On the same 48 options at 5% the rebuilt
tree at two of its own steps reads every gamma within 2.3% and every delta within
0.34 points, and at one tick reads 46 of 48 gammas over 10% off and deltas up to
1.4 points off while its three delta estimates agree within 0.0001.

### European exercise

A product whose row states `european` is exercised only at expiry (D446). Its series
are valued by `european_futures_option_black76_v1`, the Black-76 formula per unit of
quote:

```text
T  = years from the valuation instant to the expiry instant (act_365_fixed_instant_v1)
s  = volatility × √T
d1 = (ln(F ÷ K) + s² ÷ 2) ÷ s        d2 = d1 − s
call = D × (F × N(d1) − K × N(d2))
put  = D × (K × N(−d2) − F × N(−d1))
```

`F` is the underlying future's price on its direct daily-settlement curve, `K` the
strike and `D` the discount factor to the expiry instant from the currency's
payment-discount selection. The arithmetic is the tree's 30-digit decimal with the
project's `ln`, `sqrt` and `normal_decimal_taylor_bisection_v1`, and the unit price is
rounded once, half-even, to 18 digits before the multiplier. From twelve deviations
out that normal distribution is exactly 0 or 1, so close to expiry the value is the
discounted intrinsic value exactly; the result is never below that value.

| Setting | `european_futures_option_black76_v1` |
| --- | --- |
| Lattice | none: a formula in closed form |
| Day count | `act_365_fixed_instant_v1` |
| Volatility bracket | 0.01 to 3. The formula has no range, so the top is never lowered |
| Bisection iterations | at most 30 |
| Discount tolerance | 0.005 |
| Price step | none: the value is smooth in the futures price |
| Accuracy | within 10⁻²⁷ of an independent 60-digit Black-76 evaluation on the oracle's 17 cases, discounted and undiscounted, and within 10⁻²⁶ on its two wide cases |

**Calibration.** The base value equals the option's daily settlement, as for an
American series, within half the series' clearing increment. The value at zero
volatility is discounted: `D × intrinsic`. A settlement is struck with the market's
discounting, not the selected curve's, so far in the money it can lie below that
value without being wrong. A settlement below `(D − 0.005) × intrinsic` less one
clearing increment raises `option_settlement_below_intrinsic`. The tolerance of 0.005
admits a market rate half a point above the curve's for a year, or two points for a
quarter, and refuses a full point for a year; it is a first value, not a measured one.
A settlement at or below the formula's price at a volatility of 0.01 has no
volatility. Under the declared `option_settlement_at_discounted_intrinsic_v1` the
base value is then the settlement, and each scenario value is
max(0, D × scenario intrinsic + base residual) × multiplier, where D is the scenario's
discount factor and the residual, the settlement less the discounted base intrinsic,
may be negative. Undeclared, it raises `option_calibration_fallback_undeclared`. A
settlement above the price at a volatility of 3 raises
`option_settlement_above_bound`.

**Policy.** The valuation policy names the model in a member of its own:

```json
"european": {
  "numericalPolicy": "european_futures_option_black76_v1",
  "calibrationFallback": "option_settlement_at_discounted_intrinsic_v1"
}
```

`calibrationFallback` is that name or `none`. The policy's other model members
govern American series only. The Blotter, the risk catalog, the trading P&L and
strategy analysis state the policy that carries the member. A scope holding a
European series under a policy without it is refused with `option_policy`, naming
the series. A serialized policy without the member keeps its bytes, so a retained job
replays unchanged. A run retained under the earlier stated policy stays readable and
is no longer a scope's current report (D396). A numerical policy that does not price
a series' style is refused with `option_terms`.

**Risk.** Nothing is new. The solved volatility is held while the underlying's
factor is shocked, and base, up and down values come from the formula at the scenario
price and discount. The option declares its underlying, one discount input at its
expiry instant, its held volatility and no price step, so the trading P&L bump is not
sized from it and the bump report's two-step rule does not apply to it. Its
event-free interval runs to expiry. At the oracle's volatility a one-tick revaluation
gives the oracle's delta within 10⁻⁶ and its gamma within 0.01%. A fallback is
pinned to its source instant, as the American fallback is.

**Where it is refused.** A European series is refused with `projection_option_style`
in the lifecycle projection (D401) and in its cash and physical reference
strategies, which are qualified for American exercise only.

## Lifecycle

Each event is one authorized operation with a request identity. A retry returns its
first outcome; the same fact recorded again under a new request returns the existing
record. Every write needs booking authority at current knowledge. An exercise,
assignment or instruction dated after the moment it is recorded is refused with
`option_event_future` (D290).

- **`options/exercise`, `options/assignment`.** Close lots of a long or a short
  holding on a business day of the underlying's trading calendar, from the trade's
  execution through the expiry date. Each lot books one future of the underlying at
  the strike, in the option's book, through the futures booking path and in the same
  transaction: a call exercise or put assignment books a long future, a put exercise
  or call assignment a short one.
- **`options/instruction`.** Refused for ON and LO, as above. Where a product accepts
  instructions, a new one needs open lots and a business time from the trade's
  execution through the series' cutoff.
- **`options/expiry`.** Recorded only once the recording time is at or after the
  expiry instant (`option_expiry_not_reached`). The automatic rule evaluates the one
  final daily settlement selected from the frame; the event records the snapshot and
  that settlement. With none it raises `option_expiry_settlement_missing` and writes
  nothing.
- **`options/holding`.** Reads a holding's open lots and events at the read's
  knowledge. `options/status` reads a booking operation's first outcome.
- **European series.** Exercise and assignment are refused with
  `option_exercise_style`, in the lifecycle proposal and in the event primitive,
  ahead of their notice and exercise-window tests. The lifecycle list and a holding's
  Details give that reason for the two actions whatever is configured. Expiry is
  unchanged: once the expiry instant has passed, the product's automatic rule decides
  on the underlying's final daily settlement of that day, and each exercised lot books
  one future at the strike.

See [the lifecycle contract](exchange-futures.md#option-lifecycle) for the full rules.

## Reads and entry points

- **Blotter.** An option trade has two rows: the option (right, strike, expiry,
  exercise style, underlying, booked and open lots, settlement, implied volatility, calibration
  mode, value and value per contract, lifecycle state, linked futures) and the
  premium (cash state, signed amount, payment date). Each row states which lifecycle
  actions are available and why not.
- **Portfolio risk.** Options and premiums join the shared base/up/down scenarios
  with futures, swaps and other holdings. Reports and retained jobs carry the
  scenario policy, calibration receipts, tree-price counts and fallback count.
  A bump report's shock of a solved option's underlying spans at least two of
  its price steps, or the request is refused naming the smallest shock that
  does (D395). A report that values options states its option valuation policy
  in its heading, with the European member where the policy carries one, and the
  Risk page opens a retained run as a scope's current report only when it was valued
  under the stated pricing policy (D396).
- **Options.** A Trading page for readers of market data and trades (D408). Its
  product directory groups by commodity and searches product names, codes,
  exchanges and underlying futures. **Chain** shows calls and puts by strike
  for the selected expiry and underlying. `POST /api/options/listings` reads
  the contract terms from the exact definition at the selected close. Changing
  the close keeps a live selection or advances to the first live expiry. A
  link to a specific series always resolves that exact series.
  Traders can inspect the underlying delivery and, when the market contract
  is available, open its price history or a ticket for the dated option or future.
  A stored surface adds settlements, implied volatility, delta, gamma, vega
  and theta. **Show all Greeks** expands the quote table. Live positions
  and strategy legs link to their series in the chain. Expired open lots stay
  visible in Positions until a lifecycle event closes them. A live booked option
  also links from its Blotter details to its dated series.
  See [volatility surfaces](vol-surfaces.md#option-analytics).
  Its **Positions** view lists booked option positions
  with open lots under their underlying future, from
  **`POST /api/options/positions`** `{context, model}`: each row's value and
  sensitivities are the stored series figures times the multiplier and the signed
  open lots, computed in the backend. Delta and gamma are quantities in the unit
  the underlying is quoted per, and each group states the selection its surface
  was fitted under with the day its thetas run to. Delta, gamma and vega are
  subtotalled within one underlying future, value and theta totalled per
  currency; a sum with a missing member is missing, and thetas that run to
  different days are not added (`theta_days_differ`). A product whose inputs
  cannot be read at the close is a row state, not a failed read. The read prices
  nothing. Its **Strategy** view combines legs on the expiry in view (D409):
  from a template or from the chain's selected series, each a buy or a sale in
  whole lots at an entry price, which starts as the settlement shown.
  **`POST /api/options/strategy/setup`** `{context, product, model, expiryAt}`
  offers, from the stored surface, each template's legs or the code of why it
  has none, a price range, the instants a dated curve may be drawn at and the
  policies an analysis names. **`POST /api/options/strategy/analyze`** names all
  of them and returns the payoff at expiry with its net premium, largest profit
  and loss (each bounded or not) and breakevens, solved exactly; one curve per
  instant from ordinary PV of the legs under price, clock and volatility
  layers; each leg's implied volatility and delta; and the strategy's value and
  sensitivities. The page draws these and computes none. Both reads book and
  store nothing. See [strategies](#strategies).
- **New trade.** The option ticket selects exchange, commodity, underlying future,
  call or put, then strike; each list comes from the backend catalog and resets
  those below it. Its Contract section states **Exercise** and **Settlement** from
  the catalog's `exerciseStyle` and `settlementMethod`; a code the page does not know
  is shown as stated. Once a series is chosen, the ticket links to its product's
  [volatility surface](vol-surfaces.md), as an option trade's Details do in the
  Blotter. Lifecycle actions in the Blotter and the position window are
  offered only on working data with booking authority.
- **Agents.** Agents have the read-only `futures_option_catalog` tool
  (`trades.futures_option_catalog_v2`). It takes a required option product code and
  expiry date and returns that product's listed series of that expiry; there is no
  default to every product or expiry, and an unknown product or unlisted expiry is
  refused `option_definition`. The `futures_option_series` reference table, searched
  by product, lists each series' expiry date as `last_trading_date`. Agents cannot
  book options or record lifecycle events: booking facts need a human booker.
  Migration `0166_futures_options.sql` adds the tool only when the agent tool list
  is exactly the expected one, and fails if any agent tool or the agent reader role
  holds BookTrade or AmendTrade. The option entry page (`blotter_new_option`) is a
  page the assistant does not read: it can be asked from there and its page
  guidance is configurable, but the workspace operations do not cover it.

## Strategies

A strategy is option legs on one expiry and one underlying future (D409). The
pure analysis is `OptionStrategies` in
[OptionStrategies.cs](../src/OpenEtrm.MarketData/Trading/OptionStrategies.cs);
the reads are [OptionStrategyRead.cs](../src/OpenEtrm.Api/OptionStrategyRead.cs).

**Request.** Every member is required; none is filled in.

| Member | Meaning |
| --- | --- |
| `context`, `product`, `expiryAt` | The trading context, the option product and the instant every leg expires at. |
| `legs[]` | `series` (a listed series code), `side` (`buy` or `sell`), `lots` (a whole number from 1 to 100,000), `entryPrice` (exact decimal text, not negative, per unit of quote). Each series is on one leg. |
| `prices` | `lower`, `upper` and `points`: evenly spaced futures prices, bounds included. |
| `instants[]` | The instants of the dated curves, increasing, from the valuation instant to before the legs' instruction cutoff while one is ahead, or else their expiry. None is an empty list. |
| `volatilityShiftPoints` | Added to every leg's held volatility for the dated curves, in volatility points (5 is 0.05), in hundredths of a point. |
| `valuation` | The option valuation policy, in full: the one the setup offers. |
| `sensitivities` | The `option_sensitivities_layers_v1` selection, as a fit states it, with a clock only at a working snapshot. |
| `expiryRule` | `option_expiry_exercise_value_v1`. |

**Response.**

- `legs[]`: the request's legs with `right`, `strike`, `multiplier`, `premium`
  (signed: positive is paid), `state` (`solved`, `at_intrinsic` or
  `unavailable`) with `reason`, `settlement`, `impliedVolatility`, and `value`
  and `delta` per unit of quote.
- `expiry`: `rule`; `netPremium` as an amount with `direction` (`debit`,
  `credit` or `none`); `maximumProfit` and `maximumLoss`, each a signed profit
  and loss with `bounded`, or no amount when it has no bound; `breakevens[]` as
  `lower` and `upper` (equal for one price, no `upper` for every price from
  `lower` upward); and `curve[]` of `price` and `profitAndLoss`.
- `dated`: `state` `valued` with `curves[]` (`at`, then `points[]` of `price`,
  `value` and `profitAndLoss`), or `refused` with `reason` and `message`.
- `strategy`: `value`, `delta`, `gamma`, `vega` and `theta`, each an amount or
  the code of why there is none, and `dayTo` for theta. Value, vega and theta
  are money in `currency`; delta and gamma are quantities in `quantityUnit`,
  the unit the underlying is quoted per.
- `request`, `policies` (valuation, sensitivities as stated with their clock
  and day, the expiry rule, the price, clock and volatility layer conventions
  and the reporting policy), `limits` and `work` (calibration and valuation
  tree prices and their sum, the sets the curves were valued in, elapsed
  time).

**Refusals.** `strategy_limit`, `strategy_legs`, `strategy_leg_side`,
`strategy_leg_lots`, `strategy_leg_entry`, `strategy_leg_series`,
`strategy_expiry`, `strategy_underlying`, `strategy_price_range`,
`strategy_curve_instant`, `strategy_volatility_shift`, `strategy_expiry_rule`,
`strategy_product`, `option_policy`, `option_sensitivity_policy`,
`option_expired`, and for a template `strategy_template_strike`,
`strategy_template_series` or `strategy_template_money`.

Each leg is priced by its product's model: a European leg by the formula, with no
price step and no tree price counted in `work`.

**What it is not.** The payoff at expiry is an exercise value under a stated
rule, not a model price, and implies no lifecycle event. The dated curves hold
each leg's calibrated volatility under the price and clock scenarios
(`option_implied_volatility_held_v1`) and shift it only by the stated amount.
The sensitivities are the option model's own; Risk reports keep theirs.

## Remaining work

- **Volatility.** Stored volatility surfaces under a versioned vol model, with
  rich/cheap and their graphics, are shipped for viewing as
  [volatility surfaces](vol-surfaces.md) (D304), and each fit stores its series'
  value, delta, gamma, vega and theta for the Options page (D408). Valuing
  options from the surface, interpolated values for unsettled series, volatility
  in Risk and scheduled fitting remain. Strategies on one expiry and their
  payoff are analysed and drawn (D409); strategies across expiries, futures
  legs, saved strategies and booking a strategy in one action remain. The shared valuation layer
  supports held calibrated-series IV and weighted-node shocks with explicit
  annualized-volatility units; the stored analytical surface is still not a pricing
  source. See [valuation layers](valuation-context-layers.md).
- **Tick-sized risk bumps.** Parametric VaR and the PCA hybrid bump futures by
  trading ticks. On the rebuilt tree a one-tick delta is the slope of one
  straight piece of the option's value, which a parametric report's agreement
  test cannot see because three bumps inside one piece always agree, and the
  gamma it shows beside its deltas is not the option's. The trading P&L sizes its bump from the
  declared price step and the bump report refuses a shock under two steps
  (D395); neither removes the cause. The
  [strike-centred tree](#the-strike-centred-tree) removes it where nothing is
  discounted and leaves a smaller form of it where an option in the money can be
  exercised early. It is selectable and is not the stated policy; stating it
  needs a decision, a schema release for the volatility-surface tables and new
  pinned values.
- **Margined premium.** Designed, not built: D446 and its
  [design](superpowers/specs/2026-11-23-futures-option-styles-design.md) keep a
  margined option's variation margin in the futures margin records. Booking still
  refuses a margined premium.
- **Other option types.** An expiry on the underlying's last trading day is
  designed, not built (the same decision). Cash-settled, weekly, calendar spread,
  crack spread and average-price options remain.
- **European exercise, not yet done.** Its real-browser checks have not been run
  (see [verification](verification.md#european-futures-options-d446)).
  European series are refused in the lifecycle projection and its reference
  strategies. The example lists no European product. A European product whose
  holders may file contrary instructions is not supported. No check values a
  European series whose expiry lies outside the rate curve's pillars; that exclusion
  is the code American series use.
- **Settlement ingestion.** Option and futures daily settlements are synthetic
  example rows; no ingestion exists.
- **Agent booking authority.** Agent booking or lifecycle writes need their own
  design and review.
- **Recording expiry.** Nothing records an expiry without configuration: a person
  saves the trade's action policy and option profile
  ([lifecycle actions](lifecycle-actions.md)). Under a `manual` policy a person
  determines the expiry in Scheduling › Lifecycle; under `review` the scheduled job
  stores a proposal for a person to execute; under `automatic` the scheduled job
  records the expiry and the linked future
  ([lifecycle delivery](lifecycle-delivery.md)). Until then a held series past expiry
  stays visibly unprocessed (`option_expired`).
- **Intraday notice cutoffs.** Not enforced until the source states a time zone; a
  same-day late notice is accepted.
- **Close operations.** Pilot close and close-book policies do not yet state an
  option valuation policy, so they refuse books holding options (`option_policy`).
- **Host identity.** Arithmetic uses no platform math. A check pins the SHA-256
  digest of a futures and option risk result; it passes on macOS, and its Linux run
  in continuous integration has not yet been observed for this change.
- **Realized P&L.** Exercised value moves into the linked future's execution P&L; no
  separate realized lifecycle P&L or paid-premium lifecycle record exists.
