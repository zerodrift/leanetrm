# Listed American options on futures

Status: local implementation, verified as recorded in
[verification](verification.md#listed-american-futures-options-d288). This is not a
production activation record. The decision is [D288](decisions.md).

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

## Instrument

A listed option is its own immutable instrument. It references its exact underlying
`ref_data.futures_series` key and states its right, strike, expiry instant, exercise
style `american`, settlement method `exercise_into_future` and the versioned rules of
its product. The exact `ref_data.futures_option_series` key is the identity; each
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
  products whose row accepts them, and such a row must state a cutoff.
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

The premium payment date is explicit and may not precede the execution date, the UTC
date of the trade's execution; an earlier date is refused with `option_booking_input`.
A premium dated on the execution date is settled cash from the start of that UTC day,
so it adds no value at or after the execution.

Premium style is a booked term. Only premium paid up front is accepted. A request for
margined premium is refused with `option_unsupported`, and a product whose own
premium is margined with `option_product_margined`. Booking is idempotent under its
request identity (D238) and needs a human booker with booking authority. A retry
returns its first outcome only while the booker can still see the series, as an event
retry does; otherwise it is refused as an unseen series.

## Valuation

The model is `american_futures_option_crr_v1`: a Cox-Ross-Rubinstein binomial tree on
the futures price with zero drift, valued by a project-owned backward-induction core
that chooses hold or exercise at every node. All arithmetic is fixed-point decimal
with 30 fractional digits and half-even rounding, with project-owned `exp`, `ln` and
`sqrt`, so results do not depend on a platform math library. The pinned numerical
policy is:

| Setting | Value |
| --- | --- |
| Steps | 400 |
| Day count | `act_365_fixed_instant_v1`: exact elapsed time over a 365-day year |
| Volatility bracket | 0.01 to 3, lowered per series where the tree's range ends (recorded) |
| Bisection iterations | at most 30 |
| Step stability tolerance | 0.005: the tree at 400 steps agrees with 1,600 steps on the convergence cases |
| Accuracy bound | at a unit discount, \|tree − Black-76\| ≤ 0.125 · F · σ · √T / Steps |
| Money conversion | the 30-digit unit price is rounded once, half-even, to 18 digits before the multiplier |

A discount factor above one (a negative rate) is rejected with `option_input`.

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
payment-discount selection, the one swaps use. Before its payment date the premium's
unit value is that discount factor. From the payment date the cash is settled: the
row shows its settled state, amount and date, and contributes no value under the
labeled measure `futures_option_value_excluding_settled_cash`.

**Exclusions.** A position the close cannot value is listed with its reason and no
value: `option_settlement_missing`, `option_underlying_missing`, or `option_expired`
for a held series at or past expiry with no recorded expiry (visibly unprocessed). A
pending premium, or an option's expiry, dated outside the selected rate curve's
pillars is listed with `discount_coverage`; the curve is not extrapolated, and the
rest of the book is valued (D290). A currency without a discount default fails the
read with `discount_curve_missing`.

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

See [the lifecycle contract](exchange-futures.md#option-lifecycle) for the full rules.

## Reads and entry points

- **Blotter.** An option trade has two rows: the option (right, strike, expiry,
  underlying, booked and open lots, settlement, implied volatility, calibration
  mode, value and value per contract, lifecycle state, linked futures) and the
  premium (cash state, signed amount, payment date). Each row states which lifecycle
  actions are available and why not.
- **Portfolio risk.** Options and premiums join the shared base/up/down scenarios
  with futures, swaps and other holdings. Reports and retained jobs carry the
  scenario policy, calibration receipts, tree-price counts and fallback count.
- **New trade.** The option ticket selects exchange, commodity, underlying future,
  call or put, then strike; each list comes from the backend catalog and resets
  those below it. Once a series is chosen, the ticket links to its product's
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

## Remaining work

- **Volatility.** Stored volatility surfaces under a versioned vol model, with
  rich/cheap and their graphics, are shipped for viewing as
  [volatility surfaces](vol-surfaces.md) (D304). Valuing options from the surface,
  interpolated values for unsettled series, vega, volatility shocks, scheduled
  fitting, strategies and payoff diagrams remain.
- **Margined premium.** Futures-style margined premium needs futures variation
  margin first.
- **Other option types.** European, cash-settled, weekly, calendar spread, crack
  spread and average-price options.
- **Settlement ingestion.** Option and futures daily settlements are synthetic
  example rows; no ingestion exists.
- **Agent booking authority.** Agent booking or lifecycle writes need their own
  design and review.
- **Recording expiry.** Expiry is recorded only through the `options/expiry` HTTP
  operation. No UI action or scheduled job records it yet, so a held series past
  expiry stays visibly unprocessed (`option_expired`) until someone calls it.
- **Intraday notice cutoffs.** Not enforced until the source states a time zone; a
  same-day late notice is accepted.
- **Close operations.** Pilot close and close-book policies do not yet state an
  option valuation policy, so they refuse books holding options (`option_policy`).
- **Host identity.** Arithmetic uses no platform math. A check pins the SHA-256
  digest of a futures and option risk result; it passes on macOS, and its Linux run
  in continuous integration has not yet been observed for this change.
- **Realized P&L.** Exercised value moves into the linked future's execution P&L; no
  separate realized lifecycle P&L or paid-premium lifecycle record exists.
