# American options on exchange futures

Status: design dated 2026-11-01, implemented locally as recorded in D288 and
[futures-options.md](../../futures-options.md). Notes marked "Superseded by D288"
record where the implementation departs from this text.

## Intent

Book, value and risk-manage listed American options on exchange futures, starting
with two NYMEX products:

| Product | Underlying | Exercise | Settlement |
| --- | --- | --- | --- |
| Henry Hub Natural Gas option (ON) | NYMEX Henry Hub Natural Gas future (NG), 10,000 MMBtu | American | Exercises into one underlying futures contract per option |
| Crude Oil option (LO) | NYMEX Light Sweet Crude Oil future (CL), 1,000 barrels | American | Exercises into one underlying futures contract per option |

The exercise style and settlement method come from the exchange's public
specifications and must be confirmed against the current rulebook chapters when the
catalog rows are written. The rulebook pages found while drafting this design carry
older copyright dates; the builder records the source and retrieval date as the
existing exchange example does ([exchange-futures.md](../../exchange-futures.md)).

Two outcomes matter most to the owner:

1. **Values match the market.** An option's base value equals the exchange
   settlement price for that exact option contract.
2. **One backward-induction core.** American exercise is priced by working backwards
   through a price tree and choosing the best decision at every node. Later tolling,
   storage and transport valuation are expected to reuse that core with their own
   states and decisions.

## Goals

- A listed option is its own immutable instrument with an exact underlying futures
  series, right, strike, expiry, exercise style, settlement method, automatic
  exercise rule and premium style.
- Book long and short options with premium paid up front, as a premium cash
  instrument on the same trade.
- Value each option from its settlement price and give it futures delta and gamma
  under the existing shared base/up/down scenarios.
- Record early exercise, expiry, abandonment and assignment as explicit, idempotent
  lifecycle events. Exercise books a linked futures holding at the strike.
- Show options in the Blotter and the risk report beside futures, swaps and gas.
- Expose every operation to the UI and agents through the same authorized backend
  operations.

## Non-goals for the first release

- Volatility as a risk factor, volatility shocks and vega. Second release.
- Futures-style margined premium. Booking one fails with a structured
  `unsupported` error until futures variation margin exists. Second release.
- European options, cash-settled options (LN and similar), weeklies, calendar
  spread, crack spread and average-price options.
- Tolling, storage and transport. The core keeps a boundary for them; it builds only
  what the American futures option uses.
- Implied volatility surfaces, smile interpolation or extrapolation to unquoted
  strikes. Every option needs its own settlement price.
- Futures daily settlement, variation margin and delivery. Exercise creates a
  futures holding with execution P&L, exactly like a futures trade booked today.

## Anti-goals

- Inferring anything the contract or context does not state: premium style from the
  underlying being a future, exercise style from the product family, a volatility
  when the settlement price is missing, or an exercise decision when the final
  settlement price is missing.
- An option value that differs from its settlement price in the base scenario,
  beyond the stated calibration tolerance.
- Delta that comes out as zero because recalibration absorbs the futures shock (see
  "Implied volatility is its own factor").
- Results that differ between hosts. Workers on different operating systems must
  produce identical outputs from identical pinned inputs.

## Constraints from the repository

These are existing rules in [AGENTS.md](../../../AGENTS.md),
[architecture.md](../../architecture.md) and [decisions.md](../../decisions.md);
they are not new choices.

- Pricing, schedules, exercise evaluation and lifecycle rules live in the managed
  backend. TypeScript is presentation only.
- Pricers are black boxes that implement `Instrument.Declare` and
  `Instrument.PV(UnitValuationContext)`, with complete declared dependencies and a
  model version. Risk does not pick formulas by instrument type.
- Values are per unit (one option contract); the holding quantity scales afterwards,
  even though the payoff is nonlinear.
- An outright option mark has no inferred underlying exposure. Model-based valuation
  declares its underlying, volatility and rate inputs and reprices under shared
  scenarios.
- Premium-style versus daily-margin settlement is contractual and is never inferred.
- Model versions, numerical policy, units and snapshot identity are calculation
  inputs. Missing inputs, non-convergence and invalid contracts are structured
  failures; an unavailable result is not zero exposure.
- Every write is idempotent (D238). Retried commands carry a request id and receive
  their first outcome.
- Historical reads carry business time and a resolved knowledge cutoff.
- Public catalogs use real public specification facts and synthetic prices only.
- Tests are written first and are independent of the implementation's algorithm.

## Owner decisions (2026-11-01)

The owner decided these on 2026-11-01:

1. American options on NYMEX futures come first: ON and LO.
2. Exercise closes all or part of the option and books a new futures holding at the
   strike, linked to the option. It is not folded into the option trade.
3. Premium paid up front and futures-style margined premium are both needed.
   Premium paid up front ships first; margined premium is second.
4. The base value matches the exchange settlement price.
5. The pricer is a tree with backward induction, built so tolling, storage and
   transport can reuse it later.
6. The exchange's automatic exercise rule at expiry is a versioned contract term.
   The holder can override it by filing abandon (or exercise) before the cutoff.
   *Superseded by D288 for ON and LO:* the exchange accepts no contrary
   instructions for them, so the override applies only to products whose row
   allows instructions.
7. Volatility shocks and vega come second.
8. When no volatility reproduces a settlement price that sits at intrinsic value,
   the option is valued in an explicit, versioned intrinsic fallback mode rather
   than excluded (see "Intrinsic fallback").

## Design

### Instrument and catalog

Add CL to the exchange example as a NYMEX physical future beside NG, from its public
specification, with synthetic marks. Add option products and option series:

- **Option product:** exchange, underlying futures product, contract multiplier (one
  underlying futures contract per option), quote unit and currency, premium tick and
  clearing increment, exercise style `american`, settlement method
  `exercise_into_future`, and the versioned automatic exercise rule and exercise
  instruction cutoff.
- **Option series:** the product, the exact underlying `ref_data.futures_series` key,
  right (`call` or `put`), strike, last trading date and expiry instant. The exact
  series primary key is the instrument identity, as it is for futures. Different
  strikes are different instruments.
- Strikes and expiries come from explicit catalog rows. Listing-cycle rules alone do
  not create series, matching the futures example.
- Option settlement prices are observations on the option series' own quote
  specification, using the existing `daily_settlement` and `trader_mark` roles. The
  example's prices are synthetic, generated so they are consistent with an American
  price (above intrinsic, below the model's upper bound).
- **Which observation is used (my call).** The option's valuation curve declares its
  observation role explicitly, as futures curves declare `direct_role` today. Release
  1 supports `daily_settlement`, which is what "matches the market" means. A
  `trader_mark` is a different, explicitly selected mode, not a stand-in for a
  settlement. Calibration uses the option's and its underlying's observations under
  the same role from the same snapshot; a mismatch is a structured error.
- **Expiry observation (my call).** The automatic exercise rule compares the strike
  with the underlying future's `daily_settlement` on the option's expiry date. The
  option expires before its future, so `final_contract_settlement` does not apply.
  Release 1 does not add settlement ingestion: expiry is tested with synthetic
  settlement rows, and the example's frames gain synthetic `daily_settlement` rows
  labeled as synthetic.

### Resolving O13 for listed options

O13 concerns an option that controls sibling obligations inside one instrument. A
listed futures option is not that. It is a standalone instrument whose underlying is
another listed instrument, referenced exactly, and exercise creates a new, separate
futures obligation. This is the "additional obligation" meaning in architecture
section 5.4, with no sibling inside the option instrument. The new decision row
records this and keeps O13 open for embedded options, where it still applies.

### Trade shape

One trade holds:

- the option instrument, at a signed lot quantity (positive long, negative short);
- a premium cash instrument for premium × multiplier × lots, in the opposite
  direction, with an explicit payment date term. Both payment date and direction are
  booked, never derived from a clearing convention.
  *Superseded by D288:* the premium is a shared unit cash instrument (one currency
  unit on its payment date) held at the signed premium amount; after its payment
  date it is settled cash under a labeled measure.

Premium style is a booked option term. Only `premium_paid_up_front` is accepted in
the first release.

### The backward-induction core

A small, project-owned core in the managed backend. Its contract:

- **Lattice:** time steps, nodes per step, transition probabilities and one-step
  discount factors.
- **States:** the finite operational states a contract can be in.
- **Decisions:** for each node and state, the allowed decisions, each with an
  immediate cash value and a next state.
- **Result:** the value at the root and, for each node and state, the chosen
  decision. The exercise boundary comes from these chosen decisions.

The first and only lattice is a recombining binomial tree on one futures price
with zero drift, because a future costs nothing to carry. The first and only
contract is the American option, with states `alive` and `exercised` and decisions
`hold` and `exercise`. Intrinsic value uses the right and strike. Each step
discounts with the declared rate input.

The core lives in the market-data calculation library beside the existing pricers.
It moves into `OpenEtrm.Shared` only when a second consumer, such as storage, uses
it. Multi-factor and mean-reverting lattices are out of scope until then.

### Numerical policy

Everything valued today is exact rational arithmetic. The tree is the first
calculation that needs `exp`, `sqrt` and an iterative solver, so it needs its own
policy, pinned and versioned:

- **Steps:** a fixed number of tree steps from valuation instant to expiry instant,
  as part of the numerical policy. It is never chosen implicitly by the pricer.
- **Time:** year fraction between the valuation instant and the option's expiry
  instant under an explicitly named day-count convention, persisted with the model
  version.
- **Arithmetic:** it must be identical on every host. My call: fixed-precision
  decimal arithmetic with project-owned `exp`, `ln` and `sqrt`, so the result does
  not depend on a platform math library. The builder may propose binary floating
  point only with project-owned, correctly rounded transcendental functions and a
  cross-platform test that proves identical output.
- **Rounding:** the tree result converts to the existing exact money type at a
  declared precision before it leaves the pricer.

### Implied volatility is its own factor

- From the **base** snapshot only, solve for the volatility at which the tree,
  under the pinned numerical policy, reproduces the option's settlement price.
  Inputs: the option settlement price, the underlying futures settlement price, the
  discount input and the contract terms. The solver uses a pinned bracket, a
  tolerance and an iteration limit.
- The solved volatility becomes a separate primitive factor in the shared scenario
  for that option series. Up and down futures shocks change the futures factor only.
  The option then reprices at the unchanged volatility. If the solver re-ran inside a
  futures-shocked scenario, the option would always return to its own mark and
  delta would be zero.
- The scenario policy records this as an explicit, versioned rule (for example
  `option_implied_volatility_held_v1`).
- **Recomputed, not stored (my call).** The solved volatility is a derived input
  rebuilt from the pinned base inputs on every run. It is not a market observation
  and gets no table, revisions or knowledge-time resolution of its own. The result
  receipt records it, so retained jobs replay by recomputing from the same pinned
  inputs and comparing. The risk catalog does not list it as a shockable factor
  until volatility shocks arrive in release 2.
- Structured errors, never a guessed volatility:
  - missing option or futures settlement price;
  - settlement price below the American lower bound (intrinsic) or above the
    model's upper bound;
  - no root within the bracket, or no convergence within the iteration limit;
  - a root the solver cannot pin down within tolerance because value barely moves
    with volatility (deep in the money). The error names the option series.

  The one exception is the declared intrinsic fallback below.
- The base PV is the tree value at the solved volatility. It must equal the
  settlement price within the stated tolerance, and the result receipt records the
  solved volatility, the residual and the iteration count.

### Intrinsic fallback

Decided by the owner. A deep in-the-money option near expiry, or one whose
settlement price rounds to intrinsic, has no volatility that reproduces its price.
Dropping it would leave real exposure out of the book, so the valuation policy
names a second mode, `option_settlement_at_intrinsic_v1`.

- **When it applies.** Only when the settlement price is at or below the lowest
  value the tree produces inside the solver's pinned volatility bracket, and no more
  than one clearing increment below intrinsic. Both bounds use the pinned numerical
  policy. Every other calibration failure stays a structured error:
  - a settlement price more than one clearing increment below intrinsic;
  - one above the model's upper bound;
  - non-convergence for any other reason;
  - a missing settlement price.
- **Base PV.** Settlement price × multiplier, so the base still matches the market.
- **Scenarios.** The option is treated as if exercised. Under each futures shock its
  value is intrinsic at the shocked futures price plus the base residual (settlement
  price minus base intrinsic). Delta is one underlying contract per lot, signed by
  right and position, when the option is in the money at base, and zero when it is
  out of the money. Gamma comes from the same base/up/down values, so it is nonzero
  only when a shock crosses the strike.
- **It is never silent.** The result receipt and the Blotter show the mode for each
  affected option, and the risk report counts them. The mode is part of the
  selected valuation policy; a policy that does not name it turns these cases into
  structured errors.

### Discounting

Premium paid up front means the option's value is discounted. The tree's one-step
discount factors come from an explicitly selected rate input in the valuation
context, through the existing rates library. A missing rate input is a structured
error, not a zero rate.

My call: the option uses the context's existing payment-discount selection for its
currency, the same one swaps use, with no new context field. The option PV is per
unit at the valuation instant, and the premium cash instrument keeps its own payment
date and discounting. "PV equals settlement" compares the option's unit PV with
settlement price × multiplier and excludes the premium. Calibration and valuation
use the same rate, so the round trip holds whatever rate is selected; the rate
changes the solved volatility and the early-exercise boundary, not the base PV.

### Lifecycle events

Each event is an idempotent command with a request id (D238). It is recorded at a
business time with a knowledge time, and it reduces the option holding's open lots.

| Event | Who | Effect |
| --- | --- | --- |
| Early exercise | Holder of a long position, any business day before the cutoff | Exercises some or all lots. Books a futures holding in the same book at execution price = strike: long future for a call, short for a put. Linked to the option event |
| Assignment | Recorded for a short position from the clearing notice | Assigns lots. Books the opposite futures holding at the strike. Linked to the option event |
| Exercise instruction | Holder, before the expiry cutoff | Exercise at expiry even if the automatic rule would not |
| Abandon instruction | Holder, before the expiry cutoff | Do not exercise at expiry even if the automatic rule would |
| Expiry | Expiry processing for each open holding | Applies the instruction if one was filed, otherwise the versioned automatic rule, against the underlying's final settlement price for the expiry day. Exercised lots book futures; the rest expire worthless |

- A futures holding is booked through the existing authorized futures booking
  operation, so the UI, agents and lifecycle code share one path.
- Expiry with no final settlement price raises a structured error and leaves the
  holding open and visibly unprocessed. It never assumes in or out of the money.
- The premium cash instrument is unchanged by exercise or expiry.
- **The futures trade from exercise or assignment (my call).**
  - Book, counterparty and clearing account come from the option trade.
  - The trader is the person filing the exercise or recording the assignment.
  - Execution time is the exercise or assignment business time.
  - It is booked in the same transaction as the option event, under the same
    authority: whoever may book trades in that book may exercise. Agents need the
    same permission through their delegation.
  - The futures trade's idempotency key is derived from the option event's request
    id, so a retry cannot book twice.
- **Value on closed lots (my call).** Exercised lots leave the option's PV. Their
  value moves into the linked futures holding's execution P&L, which equals
  (settlement − strike) × multiplier for a call. Release 1 adds no separate realized
  lifecycle P&L; the Blotter shows the link so the transfer can be followed.
- The "suggested exercise" from the tree (exercising now is worth more than
  holding) is display only. It never files an instruction.

### Reads, Blotter and risk

- The Blotter shows option holdings with right, strike, expiry, underlying, lots,
  settlement price, solved volatility, PV and lifecycle state. Exercised lots link to
  the futures holdings they created.
- Portfolio risk includes options with delta and gamma against their underlying
  futures factor, under the same bump convention as futures. An option without a
  computable value is listed in the exclusions with its structured reason, as gas is
  today.
- Retained risk jobs replay to identical results.

### Agent and UI entry points

Booking, exercise, instructions, assignment and expiry each get one authorized
backend operation. The ticket and agent tools call the same operations. No business
rules in TypeScript.

*Superseded by D288:* agents get a read-only option catalog tool only. Booking and
lifecycle facts need a human booker, as for futures.

## Tests that define done

These are behavior checks, independent of the algorithm:

- **No early exercise premium for calls at zero rates.** With a zero rate input,
  an American call on a future equals the closed-form European Black-76 value
  within the stated tolerance, computed by an independent check.
- **Bounds.** The value is at least intrinsic and at least the European value.
  Calls are at most the futures price; puts at most the discounted strike.
  *Superseded by D288:* an American put on a future is bounded by the strike, not
  the discounted strike, because early exercise can pay up to the strike at once.
- **Monotonicity.** Value rises with volatility and time to expiry. Calls rise and
  puts fall with the futures price.
- **Convergence.** As the step count grows under fixed inputs, values settle; the
  pinned step count is within the stated tolerance of a high-step reference.
- **Calibration round trip.** For a synthetic settlement price inside the bounds,
  the solved volatility reprices to the settlement price within tolerance. Prices
  outside the bounds return the named structured errors.
- **Intrinsic fallback.**
  - A settlement price at intrinsic is valued at the settlement price, with delta of
    one contract per lot and the right sign for a long call, short call, long put and
    short put.
  - An out-of-the-money option at a zero settlement price has zero delta.
  - A price more than one clearing increment below intrinsic still errors.
  - A policy that does not name the mode errors on the same inputs.
- **Delta is not zero.** Under a futures up/down shock the option reprices at the
  held volatility, and delta has the right sign for calls, puts, longs and shorts.
- **Host independence.** The same pinned inputs give byte-identical results on Linux
  and macOS workers.
- **Lifecycle.**
  - Partial early exercise books the matching futures lots at the strike and leaves
    the rest open.
  - Retrying the same request id returns the first outcome.
  - Expiry follows a filed abandon or exercise instruction over the automatic rule.
  - Expiry with a missing final settlement price errors and changes nothing.
- **Unsupported paths.** Booking futures-style margined premium returns
  `unsupported`. A European or cash-settled series is not bookable.
- **Mixed portfolio.** A portfolio check, modeled on the existing crude portfolio
  check, holds a future, an ON option and an LO option, verifies PV equals
  settlement prices, delta signs and queued-job replay parity.

## Highest-risk inputs

The inputs most likely to break this for someone using it, most likely first:

1. **Delta collapses to zero** because the solver re-runs inside futures-shocked
   scenarios. Covered by the "Delta is not zero" test.
2. **Deep in-the-money options near expiry**, where value barely moves with
   volatility. The solver must not return a large arbitrary volatility. The option
   goes to the intrinsic fallback only under its stated conditions, and is shown as
   doing so; otherwise it errors with a clear reason.
3. **Expiry day with stale or missing settlement prices.** Expiry must not
   proceed, and the Blotter must show the option as unprocessed.
4. **Valuation on or after the expiry instant**, or with less than one step of time
   left. The pricer must return intrinsic only where the contract says the option is
   still exercisable, otherwise a lifecycle error, never a negative year fraction.
5. **Short options.** Assignment direction and the sign of the premium cash must be
   correct; tested with a short call and a short put.

## Left to the builder

- Exact table and column names, following existing `ref_data` and booking
  conventions.
- The specific step count, solver bracket, tolerance and iteration limit, with
  convergence evidence.
- The day-count convention, chosen from those the rates library supports and named
  in the policy.
- Blotter column order and the ticket layout for options, following the existing
  deal ticket.
- Any binomial parameterisation, provided it meets the tests above.
