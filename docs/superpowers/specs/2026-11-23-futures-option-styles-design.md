# Futures option styles: European exercise, expiry on the underlying's last day, margined premium

Status: design dated 2026-11-23, approved by the owner on 2026-11-23. Piece 1,
European exercise, is implemented and recorded in
[verification](../../verification.md#european-futures-options-d446); pieces 2 and 3
are not. The decision is D446 in [decisions](../../decisions.md); its number
is provisional until the branch merges. Each piece has its own task plan in
[plans](../plans/).

## Intent

The application admits one kind of listed option on a future: American exercise into
the underlying future, expiring strictly before that future stops trading, with the
premium paid up front ([futures options](../../futures-options.md), D288). Listed
energy options also come in three other shapes, and a book that holds one cannot be
entered today:

1. **European exercise.** The option is exercised only at expiry, by the contract's
   own automatic rule, into the underlying future.
2. **Expiry on the underlying's last trading day.** The option and its cash-settled
   future stop on the same day, and the price that decides exercise is the price the
   future finally settles on.
3. **Margined premium.** No premium changes hands at the trade. The option is marked
   to its own settlement every day and the difference is paid as variation margin,
   as for a future.

This design admits each of the three. Each is separable: it has its own contract
terms, migration, checks and refusals, and can be built without the others. A product
may combine them.

The owner's purpose is a demonstration book for a gas and power desk that holds a
liquid, financially settled option. An option that exercises into a cash-settled
future is financially settled: its exercise ends in the future's cash settlement.

## What exists today

Read from the migrations and the managed backend on 2026-11-23, not run.

- **Product terms.** `ref_data.futures_option_products` states `exercise_style`
  (only `american`), `settlement_method` (only `exercise_into_future`),
  `premium_style` (`premium_paid_up_front` or `futures_style_margin`), whether
  contrary instructions are accepted, the strike interval, and four versioned rule
  rows: exercise, expiry, automatic exercise and exercise notice.
- **Series.** `ref_data.require_futures_option_series` refuses a series whose last
  trading date is on or after its underlying's, or whose expiry instant is at or
  after it (`option_expiry`).
- **Booking.** `security.book_futures_option` refuses a margined request
  (`option_unsupported`) and a margined product (`option_product_margined`). The
  trade revision row and `FuturesOptionInstrument` admit only premium paid up front.
  A booking needs `BookTrade` and no lifecycle setup.
- **Valuation.** `FuturesOptionInstrument` is a leaf instrument (D417). It prices on
  the American tree named by the valuation policy's numerical policy, calibrated so
  that the base value equals the option's daily settlement, and holds that implied
  volatility under futures shocks (`option_implied_volatility_held_v1`).
  `FuturesOptionTerms` rejects any style other than `american`.
- **Lifecycle.** Exercise, assignment, instructions and expiry are lifecycle actions
  (`lifecycle.option_proposal`, `lifecycle.execute_option_action`). Each needs a
  saved action policy and an option profile for the trade, and the profile names a
  margin policy on the underlying future, which the future booked by exercise adopts
  in the same transaction. Expiry reads the underlying's final `daily_settlement`
  observation on the option's last trading date. Of the option actions only expiry can be scheduled.
- **Futures margin.** A saved margin policy on a futures contract lists dated daily
  and final occurrences. Adoption binds a held future to it. Determinations record
  the settled basis and the variation; cash obligations and payments are separate
  records. A future's PV needs that margin state.
- **Who can act.** On a cash-settled future every step is held by an existing human
  role (see [Roles](#roles)). On a physically delivered future the margin policy
  needs a delivery profile, and no human role holds the permission that configures
  one. That is a separate matter and is not changed here.
- **Already admitted.** An American option that exercises into a cash-settled
  delivery-hour future and expires before the delivery month fits these rules as
  they are, with its dated lot, a daily early-exercise deadline and instructions on
  the expiry day (migration 0382). It needs none of the three pieces.

## Goals

- A product row states its exercise style, its expiry's relation to the underlying,
  its automatic exercise rule and its premium style. None is inferred from another
  term, from the commodity or from the exchange.
- A European series is booked, valued by a Black-76 formula under a named numerical
  policy, calibrated to its own settlement, repriced under the shared scenarios and
  expired by its contract's rule.
- A series may expire on its cash-settled underlying's last trading day when its
  product's expiry rule says so, and its exercise is decided by the price its rule
  names.
- A margined option is booked without premium cash, carries its own variation
  margin in the margin records futures use, and is valued at the variation not yet
  margined.
- Every existing product, series, booking, event, margin record and retained result
  keeps its meaning and its bytes. The American pricer is not edited.

## Non-goals

- Options that settle straight to cash with no future. No current product needs
  one. [What stays refused](#what-stays-refused) says how one would be expressed.
- Average-price options, options into a strip of several futures, calendar spread
  options and weekly options.
- An expiry on the last trading day of a physically delivered future.
- Initial margin, margin offsets between an option and its future, and clearing
  accounts.
- European or margined options in the lifecycle projection of D401 and in its cash
  and physical reference strategies. They are refused there by name until each is
  qualified on its own.
- New roles or permissions, and any change to who may act on a physically delivered
  future.
- Volatility as a risk factor. It stays as it is for American options.

## Anti-goals

- Pricing an option as a style it is not. A European series is never valued on the
  American tree, and the reverse.
- A default. A row that leaves out the relation, the rule's at-the-money outcomes,
  the premium style or an instruction cutoff it needs is refused, not completed.
- Folding margin into value. Determined margin, cash due, cash paid and the
  outstanding variation stay four separate facts.
- A second margin journal beside the futures one.
- A commodity or exchange switch anywhere in valuation, risk or lifecycle.

## Constraints from the repository

- Every instrument derives from `Instrument`; a listed futures option is one of the
  two leaf instruments (D417). No second option class is added.
- Exchange specifics are versioned reference rows with a source, never code.
- Missing or conflicting financial behaviour raises a structured error (D238 for
  writes; the valuation policy for reads).
- Pricers are black boxes with declared inputs. Risk reprices under shared base, up
  and down scenarios and buckets the differences.
- Futures keep variation-margin state apart from outstanding exposure.
- Arithmetic is the project's fixed-point decimal with its own `exp`, `ln`, `sqrt`
  and normal distribution. No financial library is added.
- Historical reads carry business time and a knowledge cutoff. A settlement
  published later never enters an earlier view (D375).
- Tests come first and are independent of the implementation's algorithm.

## Owner decisions (2026-11-23)

1. All three pieces are designed in full, each with its own task plan. The order is
   European exercise, then expiry on the underlying's last day, then margined
   premium.
2. European options are priced by a Black-76 formula named in the valuation policy
   beside the American tree. They are not priced on the tree.
3. An option that expires on its future's last day still books a linked future at
   the strike, as D288 decided. The future's own final margin then settles it.
4. A margined option's variation margin lives in the futures margin records, not in
   a separate journal.

Calls made by the designer and shown to the owner with the approaches:

- No new role. Existing permissions decide who acts ([Roles](#roles)).
- Options that settle straight to cash stay refused.
- The Options page and stored volatility surfaces take European products as the
  last tasks of piece 1, and margined products as the last task of piece 3.
- European and margined options are refused in the D401 lifecycle projection until
  separately qualified.
- Closing a margined option strikes a last margin that takes its mark to zero,
  because the linked future opens at the strike. It is a named policy term, to be
  confirmed against a clearing house's own description before a real product
  cites it.

Calls made after an independent read of this document, accepted by the owner with
the document:

- A margined option's journal always reads as if recorded in business-time order.
  A close recorded late restates the margin struck after it, instead of closing
  from the latest basis. This follows D238 and D375 and costs a restatement path
  the futures journal does not have for a changed lot count.
- An option that expires on its future's last day expires at the future's close,
  not earlier that day, and is judged by the final settlement only.
- The price that decides exercise is read from the margin policy's settlement
  source, so it is the price the linked future is paid on.
- A European product does not accept contrary instructions.
- The European model tolerates a settlement that implies a discount factor up to
  0.005 below the selected curve's.
- An adopted margined holding with margin pending is refused in valuation, as a
  future is; before adoption it is valued on its trade premium.

## Approaches

**European pricing.** One way was sensible: a closed-form model chosen by the
product's stated style. The alternative, the existing tree with early exercise
switched off, needs no formula but keeps the tree's price steps, so small bumps stay
meaningless (D395). The formula is smooth and declares no price step.

**Expiry on the last day.**

| | What happens at an in-the-money expiry | Trade-off |
| --- | --- | --- |
| A, chosen | A future at the strike is booked and adopts its margin policy; the future's final margin pays final price less strike | Two records. Follows the contract's words and D288, and reuses futures margin. Needs a narrow rule for a future booked at the instant its market closes |
| B | The expiry event itself records the cash | One record and no future. Restates "exercise into the future" as something else and adds a second cash path |

**Margined premium.**

| | Where the margin lives | Trade-off |
| --- | --- | --- |
| A, chosen | The futures margin policy, occurrence, run, determination and cash records | One journal, one review table, one schedule. Widens records that futures rely on, so existing futures results must stay identical |
| B | A separate set of option margin records | Futures margin is untouched. A second copy of the same machinery, against the rule on parallel frameworks |

Dropped early: a second leaf instrument for European options (D417 forbids it), and
treating a margined premium as one payment deferred to the close (it misstates the
daily cash).

## Terms on the versioned rows

One table for the three pieces. Every term is required where its column applies.

| Term | Row | Values | Piece |
| --- | --- | --- | --- |
| Exercise style | `futures_option_products.exercise_style` | `american`, `european` | 1 |
| Settlement method | `futures_option_products.settlement_method` | `exercise_into_future` only | none |
| Contrary instructions | `futures_option_products.contrary_instructions_allowed`, with the series' cutoff where accepted | as today; not accepted on a European product | 1 |
| Expiry relation | `futures_option_rules.underlying_relation` on the `expiry` rule | `before_last_trading`, `on_last_trading_day` | 2 |
| Automatic exercise evaluation | `futures_option_rules.evaluation` on the `automatic_exercise` rule, with its threshold and the outcome for a call and for a put exactly at the money | `settlement_moneyness_v1`, `final_settlement_moneyness_v1` | 2 |
| Close and expiry time | `trading_close_times` | as today | none |
| Premium style | `futures_option_products.premium_style` | `premium_paid_up_front`, `futures_style_margin` | 3 |
| Margin terms | a saved option margin policy on the option contract | see piece 3 | 3 |

Strike interval, premium tick, multiplier basis and the daily early-exercise deadline
are unchanged.

## Piece 1: European exercise

### Instrument

`FuturesOptionInstrument` stays the one listed-option instrument, keyed by its exact
series. `FuturesOptionTerms` admits `european` beside `american`. The economic
identity is unchanged: the series key. A European and an American series are
different series of different products, so no existing identity moves.

### Pricing contract

Model `european_futures_option_black76_v1`, per unit of quote:

```text
T  = years from the valuation instant to the expiry instant (act_365_fixed_instant_v1)
s  = volatility × √T
d1 = (ln(F ÷ K) + s² ÷ 2) ÷ s        d2 = d1 − s
call = D × (F × N(d1) − K × N(d2))
put  = D × (K × N(−d2) − F × N(−d1))
```

- **Declared inputs.** The underlying future's price `F` on its direct
  daily-settlement curve; the held volatility, annualized; the discount factor `D`
  to the expiry instant from the currency's payment-discount selection; the
  valuation instant. The strike `K`, right and expiry come from the series.
- **Arithmetic.** The project's 30-digit fixed-point decimal, half-even, with its
  own `ln`, `sqrt` and `normal_decimal_taylor_bisection_v1`. The unit price is
  rounded once, half-even, to 18 digits before the multiplier, as for the tree.
- **Declaration.** Dependencies: the underlying only. Time: between the calculation
  instant and expiry. One discount input at the expiry instant. The held volatility
  as a volatility input. No price step.
- **Refusals.** A nonpositive price, strike or volatility, a discount factor above
  one or not positive, and a valuation at or after expiry raise the codes the tree
  raises today (`option_input`, `option_expired`).

`FuturesOptionMarketBinding` chooses the model from the series' stated style. A
series whose style the selected numerical policy does not price raises `option_terms`.

### Calibration

The rule of D288 holds: the base value equals the option's daily settlement.

- The volatility is solved by bisection between 0.01 and 3, in at most 30 halvings,
  until the formula reproduces the settlement within half the series' clearing
  increment. The formula has no range limit, so the bracket's top is never lowered.
- The formula's value at zero volatility is the discounted intrinsic value,
  `D × max(F − K, 0)` for a call. A settlement is struck with the market's
  discounting, not the selected curve's, so a deep in-the-money settlement can lie
  below that value by more than an increment without being wrong. The numerical
  policy therefore states a discount tolerance `t` of 0.005. A settlement below
  `(D − t) × intrinsic` less one increment raises
  `option_settlement_below_intrinsic`. Above that bound it is calibrated or falls
  back.
- **Fallback.** A settlement at or below the formula's price at volatility 0.01 has
  no volatility. If the valuation policy declares
  `option_settlement_at_discounted_intrinsic_v1`, the base value is the settlement
  and each scenario value is `max(0, D × scenario intrinsic + base residual)`, where
  the residual is the settlement less the discounted base intrinsic and may be
  negative. Undeclared, it
  raises `option_calibration_fallback_undeclared`. The mode is shown and counted as
  the American fallback is.
- A settlement above the price at volatility 3 raises
  `option_settlement_above_bound`.

### Valuation policy

`FuturesOptionValuationPolicy` gains an optional member:

```json
"european": {
  "numericalPolicy": "european_futures_option_black76_v1",
  "calibrationFallback": "option_settlement_at_discounted_intrinsic_v1"
}
```

`calibrationFallback` is that name or `none`. The existing members keep their
meaning and govern American series only.

- A scope holding a European series under a policy without `european` raises
  `option_policy` and names the series.
- A policy with `european` and no European series is accepted; the member is unused.
- A serialized policy without the member is byte-identical to today's, so retained
  jobs replay unchanged.
- The Blotter, the risk catalog and the trading P&L state a new policy that
  carries the member. The earlier stated policy keeps its bytes for the checks and
  retained runs pinned to it. See [Highest-risk inputs](#highest-risk-inputs) for
  what that does to retained runs.

### Risk

Nothing new. The solved volatility is held while the underlying's factor is shocked;
base, up and down values come from the formula at the scenario price and the
scenario's discount. The calibration receipt records the same fields, and a retained
job recomputes it and refuses a different result (`risk_option_calibration`). The
option declares no price step, so the trading P&L bump is not sized from it and the
bump report's two-step rule does not apply to it. No code reads the commodity.

### Lifecycle

- **Before expiry.** `option.exercise` and `option.assignment` are refused with
  `option_exercise_style`: in the proposal, in the event primitive, and as the
  reason shown beside the action in Scheduling › Lifecycle.
- **Instructions.** A European product does not accept contrary instructions. A
  product row that states `european` and accepts them is refused by a check. At
  expiry a short holding then takes the automatic outcome on all its lots. A
  European product whose holders may instruct would need a way to record a
  partial assignment at expiry, and no current product requires one.
- **At expiry.** `option.expiry` is unchanged. It can be recorded once the expiry
  instant has passed. A filed instruction decides where one is accepted; otherwise
  the automatic rule does. Exercised lots book one linked future each at the strike;
  abandoned lots close.
- **What records itself.** Nothing does without configuration. A person saves the
  trade's action policy and option profile. Under a `manual` policy a person
  determines the expiry; under `review` the scheduled job stores a proposal for a
  person to execute; under `automatic` the scheduled job records the expiry and the
  linked future. "Automatic exercise" in a contract is the rule that decides; the
  action policy decides who records.

### Reads and the ticket

- The option ticket's facts gain **Exercise** ("European · at expiry only") and
  **Settlement** ("Exercises into the future at the strike"), from the backend
  catalog. The Blotter's option row and Details show the style.
- Options › Chain lists a European product from its definitions with no change.
- **Stored surfaces.** The volatility-surface tables accept
  `european_futures_option_black76_v1` for a European product and keep refusing it
  for an American one. A fit names the policy of its product's style; its stored
  value and sensitivities come from the formula. Strategy analysis prices each leg
  by its product's model; `option_expiry_exercise_value_v1` is unchanged.

### Migration and what is unaffected

- Replace `futures_option_products_exercise_style_check` with a check admitting the
  two styles, and add a check that a European product does not accept contrary
  instructions.
- Add the early-exercise refusal to `lifecycle.option_proposal_base` and
  `security.apply_futures_option_event`.
- Widen the surface tables' policy checks as above.

No row is rewritten. Every existing product is American, so every existing series,
booking, event, fit and retained result is untouched, and the pinned risk digest of
the American example must not change.

## Piece 2: Expiry on the underlying's last trading day

### Contract terms

- **Relation.** The product's `expiry` rule row states `underlying_relation`:
  `before_last_trading` or `on_last_trading_day`. It is required on an expiry rule
  and absent on every other rule.
- **Evaluation.** The `automatic_exercise` rule row names the price it judges by:
  - `settlement_moneyness_v1` (today's): the underlying's final `daily_settlement`
    observation on the option's last trading date;
  - `final_settlement_moneyness_v1` (new): the underlying's final
    `final_contract_settlement` observation.
  Both use the stated in-the-money threshold and the stated outcome for a call and
  for a put exactly at the money. A rule that leaves those out is unknown and
  refuses at use, as today.
- **Pairing.** The two go together in both directions. A product whose expiry
  rule states `on_last_trading_day` names `final_settlement_moneyness_v1`, and a
  product that names it states `on_last_trading_day`. Before the last day there is
  no final settlement to judge by, and no current product expires on the last day
  and is judged by that day's daily settlement. Any other pairing raises
  `option_exercise_rule` when a series is inserted.

### Series admission

`ref_data.require_futures_option_series` reads the relation:

| Relation | Admitted when | Otherwise |
| --- | --- | --- |
| `before_last_trading` | last trading date before the underlying's, and expiry instant before its last-trading instant | `option_expiry` |
| `on_last_trading_day` | same business date as the underlying's last-trading milestone, expiry instant equal to that instant, and the underlying product is cash settled | `option_expiry`; on a physical underlying `option_expiry_physical_same_day` |

An expiry after the underlying's last trading is refused under either relation. The
underlying's last-trading milestone must be known, as today.

The instants are equal for a reason. The model's time ends at the expiry instant,
and the price that decides exercise is fixed when the future stops trading. An
option that expired earlier that day would be valued as if nothing could move after
its expiry while its deciding price still moved. So the option product's expiry
close and the futures product's last-trading close are the same local time on the
same clock.

### Expiry

`option.expiry` for a rule evaluated by `final_settlement_moneyness_v1`:

- reads the one `final_contract_settlement` observation of the underlying contract
  that is final, uncorrected and known at the action's cutoff, from the settlement
  curve of the profile's margin policy. The price that decides exercise is then the
  price the linked future's final margin pays on. The profile's expiry curve must
  be that curve (`lifecycle_option_source` otherwise);
- with none, raises `option_expiry_settlement_missing` and writes nothing; with more
  than one, `option_expiry_settlement_ambiguous`;
- records the snapshot, the observation and the rule version, as today. A later
  expiry of the same holding on a different settlement raises
  `option_expiry_conflict`.

The event's business time is the series' expiry instant. The recording time is
later: an expiry cannot be determined at a knowledge cutoff earlier than the final
settlement's publication.

### The linked future

An exercised lot books one future at the strike, executed at the option's expiry
instant, in the option's book and parties, through the futures booking path. Two
rules make that possible on the last day:

- **Booked at the close.** A future booked by an option event may be executed at
  its last-trading instant. Every other futures booking keeps today's rule,
  strictly before it.
- **Margined from the strike.** The future adopts the profile's margin policy in the
  same transaction. That policy's window covers the execution instant and it lists
  the future's final occurrence. Where the final occurrence's instant is the
  execution instant, the policy's opening is `include_opening_instant`; a policy
  that would skip the final occurrence raises `lifecycle_option_margin_policy`.

The future's final margin is then the existing `futures.margin` action: final
settlement less strike on the held lots, due as the policy says. Nothing about that
action changes.

### What the desk sees

For a long in-the-money call of the synthetic gas example:

1. The expiry instant passes. The holding is listed as expired and unprocessed
   (`option_expired`), with no value, as today.
2. The future's final settlement is published. **Determine expiry** records
   "exercised" against it, closes the option's lots and books the future at the
   strike.
3. **Determine margin** on that future records final price less strike and the
   future's state becomes final.
4. Payment is recorded when it is made.

Under automatic action policies steps 2 and 3 run from their scheduled jobs.

### Migration and what is unaffected

- Add `underlying_relation` to `ref_data.futures_option_rules`. Every existing
  expiry rule row states `before_last_trading`. That is not a default: the trigger
  those rows' series were inserted under admitted nothing else. New rows must state
  it.
- Replace the series trigger function; add the evaluation and its read to the
  automatic outcome, the proposal and the event primitive; add the option-origin
  rule to futures booking admission.

Every existing series satisfies `before_last_trading` and keeps its identity. No
booking, event or margin record changes.

## Piece 3: Margined premium

### Contract terms

The product row states `futures_style_margin`. The margin terms are a saved,
versioned **option margin policy** on the exact option contract, as a futures margin
policy is on a futures contract:

- the option's own direct daily-settlement curve as the settlement source;
- an effective window and the dated daily occurrences, each with its due instant;
- the opening treatment and the cash rounding terms a futures policy has;
- `closingMark: zero_at_close_v1`, and one final occurrence at the series' expiry
  instant with its due instant. The final occurrence needs no settlement: the
  option's mark at its close is zero by the closing term.

A policy on the option contract of a product whose premium is paid up front raises
`option_margin_policy`.

### Booking

`options/book` with convention `futures_option_margined_v1`:

- the request states `premiumStyle: futures_style_margin`, and it must equal the
  product's (`option_booking_input` otherwise, in both directions);
- `premiumPaymentDate` is absent; supplying one raises `option_booking_input`;
- the trade holds one instrument, the option at signed lots. The premium is the
  trade price per unit of quote, checked against the premium quote's tick and
  range. It is the execution basis. No cash instrument and no cash membership are
  written;
- retries and conflicts behave as for a booking with premium paid up front (D238).

### Margin state

Kept in the futures margin records, widened to hold an option holding:

- **Adoption** binds the holding's option binding to an option margin policy from
  its execution. It is an explicit action, not part of booking.
- **Daily determination.** At each occurrence, the evidence is the option's own
  final `daily_settlement` observation at that instant from the policy's source.
  The variation is settlement less previous basis, times the multiplier, times the
  lots open at that instant. The first basis is the trade premium. Exact and
  rounded amounts, the adjustment, its due instant and the cash membership are
  recorded as for a future. With no such observation the determination raises
  `option_margin_settlement_missing`.
- **Closing determination.** An exercise, an assignment and an expiry (exercised or
  abandoned) each strike a determination for the lots they close, in the same
  transaction: zero less the basis at the event's business instant, times the
  multiplier, times those lots. Its evidence is the option event. It is due at the due instant of the
  policy's first occurrence, daily or final, at or after the event's business
  instant; a policy with none raises `option_margin_occurrence_missing`. Lots that
  stay open keep their basis and go on being margined.
- **Order.** A holding's journal reads as if every fact had been recorded in
  business-time order.
  - A close needs the holding's margin determined through every daily occurrence
    before its business instant. Otherwise it raises `option_margin_behind` and
    names the occurrence. A daily occurrence at the same instant as a close is
    determined first.
  - A close may be recorded after later occurrences were determined, as an
    assignment notice that arrives the next morning is. Those determinations are
    restated in the same transaction on the lots open at each occurrence as now
    known. Each difference is an adjustment with its own due instant, as when a
    corrected settlement restates a determination. A view at an earlier knowledge
    cutoff keeps the determinations it had (D238, D375).
- **Corrections** to a settlement restate determinations as the futures journal
  does today.
- **State read.** Settled basis, settled-through instant, open lots, open or closed
  status and the source context: the option counterpart of `FuturesMarginState`.

A holding that is not adopted cannot be closed: exercise, assignment and expiry
raise `option_margin_adoption_required`, because closing it would never collect the
premium.

Two properties follow and are checks:

- For a long holding closed in full, the daily variations and the closing
  determination sum to minus the trade premium times multiplier times lots, before
  rounding. The linked future's own margin carries the payoff.
- Before rounding, the cumulative margin a buyer has paid is at most the premium at
  every step.

### Valuation

`FuturesOptionValuationPolicy` gains an optional member:

```json
"marginedPremium": { "valuation": "margined_option_open_variation_v1" }
```

Under it, for a series whose product is margined:

- the model is the one for the series' style, at a unit discount. No discount input
  is declared: a margined option's price is not discounted. Calibration to the
  option's settlement is at a unit discount too;
- the value of a holding per option is the model value less its basis. The basis is
  held state of the binding; the instrument stays one shared unit per series;
- **not adopted:** the basis is the trade premium and the row says so. This is the
  option's counterpart of a future's execution P&L;
- **adopted:** the basis is the settled basis of the margin state at the
  valuation's knowledge. A holding with a daily occurrence at or before the
  valuation instant that has not been determined is refused with
  `option_margin_state`, as a future with pending margin is. Missing margin is not
  valued as if it had been struck;
- the result is the variation not yet margined, with no discounting to a margin
  due. Determined margin, cash due and cash paid are read from the margin and cash
  records, never from value.

A scope holding a margined series under a policy without the member raises
`option_policy`. The funded-margin valuation a future can select, which discounts
its open variation to the next due, is not offered for options.

Risk is the same repricing: the basis does not move with the market, so base, up
and down differences come from the model value alone.

### Lifecycle

- A new action kind `option.margin`, which may be automatic, configured and
  executed under the permissions `futures.margin` uses.
- Exercise, assignment, instructions and expiry are as for premium paid up front,
  plus the closing determination.
- The linked future is booked at the strike and adopts its futures margin policy,
  as today.

### Reads and the ticket

The Blotter shows one row for a margined option trade, with its value (the open
variation), its margin status (not adopted, margined through an instant, closed)
and the margin determined so far. There is no premium row. Scheduling › Lifecycle
lists the holding's margin occurrences in the table futures use.

The ticket states the premium style from the product instead of offering a choice:

```text
Contract
  Exchange             [Synthetic exchange ▾]
  Commodity            [Crude oil ▾]
  Underlying future    [Synthetic crude · Mar 2027 ▾]
  Call / put           [Call ▾]
  Strike               [70.00 ▾]
  Last trade           24 Feb 2027 17:00
  Expiry               24 Feb 2027 17:00 · before the future's last trading day
  Exercise             American · on any business day
  Settlement           Exercises into the future at the strike
Execution
  Buy / sell           [Buy ▾]
  Lots                 [10]
  Premium              [2.15]
  Premium style        Margined daily on this option's settlement
  Initial margin       [            ]  Coming soon
  Executing broker     [            ]  Coming soon
```

For a product paid up front the same place reads "Paid up front" and the **Premium
payment date** field follows it, as today. The request still states the style; the
backend still refuses a mismatch. **Initial margin**, **Executing broker**, **Custom
trade fields** and the More actions stay "Coming soon".

Stored surfaces and strategy analysis for a margined product use the unit discount
and record it on the fit.

### Migration and what is unaffected

- `trading.futures_option_trade_revisions`: admit both premium styles and both
  conventions; the premium cash instrument and quantity are null exactly when the
  premium is margined.
- `security.book_futures_option`: replace the two refusals with the rules above.
- Margin records: a policy implementation for option contracts with the closing
  term; an adoption that names an option trade's binding; a determination whose
  evidence is either a settlement observation or an option event; the
  `option.margin` action kind.

Every existing option booking is paid up front, because the row admitted nothing
else, and keeps its premium cash. Every existing futures margin policy, adoption,
run, determination, hash and first outcome is unchanged, and every existing futures
margin check passes without edits.

## Roles

No role or permission is added. On a cash-settled underlying:

| Step | Permission | Human roles holding it today |
| --- | --- | --- |
| Save a futures or option margin policy | `WriteMarketDefinitions` | Market data editor |
| Book an option | `BookTrade` | Trader |
| Save an action policy and an option profile; adopt a margin policy | `AmendTrade` | Trader, Trade supervisor, Gas delivery operations |
| Record exercise, assignment, an instruction or expiry | `BookTrade` | Trader |
| Determine margin on a future or an option | `AmendTrade` | Trader, Trade supervisor, Gas delivery operations |
| Run expiry or margin from a scheduled job | the action's permission and `RunJobs`, for the job's owner and for its service principal | Trader |

So a trader can do every trading step. A Trade supervisor can set up actions and
determine margin but cannot book or record an exercise or expiry, since each books
a future. No human settlement role exists; the settlement operator role is for
agents and covers swaps. Separating expiry from trading would need a role that
holds `BookTrade` for lifecycle bookings only, which is a decision of its own.

## What stays refused

| Case | Refusal |
| --- | --- |
| An exercise style other than American or European | check `futures_option_products_exercise_style_check` |
| Exercise or assignment of a European option before expiry | `option_exercise_style` |
| A European series under a policy that names no European model | `option_policy` |
| A numerical policy that does not price the series' style | `option_terms` |
| A settlement below the model's zero-volatility value by more than an increment | `option_settlement_below_intrinsic` |
| A European product that accepts contrary instructions | check on the product row |
| An expiry after the underlying's last trading, or against the product's relation, or on the last day at another instant than the future's close | `option_expiry` |
| An expiry on the last day of a physically delivered future | `option_expiry_physical_same_day` |
| A relation and an evaluation that are not paired | `option_exercise_rule` |
| An expiry curve that is not the margin policy's settlement curve, under the final-settlement evaluation | `lifecycle_option_source` |
| An automatic rule with unstated at-the-money outcomes, at expiry | `option_exercise_rule_unknown` |
| Expiry before the deciding settlement is known | `option_expiry_settlement_missing` |
| A linked future whose policy would skip its final occurrence | `lifecycle_option_margin_policy` |
| A booking whose premium style differs from the product's, or a payment date on a margined booking | `option_booking_input` |
| A margin policy or adoption for an option paid up front | `option_margin_policy` |
| Closing a margined holding that has not adopted a policy | `option_margin_adoption_required` |
| Closing a margined holding whose margin is behind the close | `option_margin_behind` |
| Valuing an adopted margined holding with margin pending | `option_margin_state` |
| A daily margin with no final settlement of the option at the occurrence | `option_margin_settlement_missing` |
| A margined series under a policy without the margined valuation | `option_policy` |
| A European option in the D401 projection or its reference strategies | `projection_option_style` |
| A margined option there | `projection_option_premium_style` |
| An option that settles straight to cash | check on `settlement_method` |

A cash-settled option would be a product row with `settlement_method` `cash` and a
rule naming its settlement price, and an expiry that records cash and no future. It
is not admitted until a product needs it.

## Synthetic examples

Two synthetic products carry the checks. Neither is an exchange's product, and
every price is synthetic.

| | Synthetic gas option | Synthetic crude option |
| --- | --- | --- |
| Underlying | A monthly cash-settled gas future, 5,000 MMBtu | A monthly cash-settled crude future, 1,000 barrels |
| Style | European; automatic only | American; instructions accepted to a stated cutoff on the expiry day |
| Expiry | On the future's last trading day at the future's close, 14:30 on the example clock | Three business days before the future's last trading day |
| Automatic exercise | `final_settlement_moneyness_v1`, threshold 0, at the money a call and a put both abandon | `settlement_moneyness_v1`, threshold 0, at the money a call exercises and a put abandons |
| Premium | Paid up front | Margined |
| Strike interval, premium tick | 0.10, 0.001 | 0.50, 0.01 |
| Pieces | 1 and 2 | 3 |

A European margined option product on the crude future carries the combination of
pieces 1 and 3.

## Tests that define done

Written first, each against an independent reference.

**Piece 1**

- The formula equals the independent 60-digit Black-76 values in
  `tests/fixtures/futures-option-oracle.json`, discounted and undiscounted, on
  every case, to at least 20 decimal places; the check pins the observed bound.
- Put-call parity `call − put = D × (F − K)` holds to the same bound.
- Calibration reproduces a settlement within half an increment; the fallback, the
  below-intrinsic and the above-bound cases each give their one outcome. A deep
  in-the-money settlement struck at a slightly higher rate than the selected
  curve's falls back with a negative residual; one struck beyond the tolerance is
  refused.
- A one-tick up and down revaluation gives a delta and a gamma within a stated
  bound of the oracle's sensitivities.
- The American example's pinned risk digest is unchanged.
- A European product inserts; a third style is refused by the check; early exercise
  is refused in the proposal and in the primitive; expiry processes.
- A book with a European series is refused under a policy without the member, and a
  retained policy without the member replays to the same bytes.

**Piece 2**

- The series trigger admits and refuses each relation against a date before, on and
  after the underlying's last day, with the instant before, equal and after, on a
  cash and on a physical underlying, and under each pairing of relation and
  evaluation.
- Every series of the existing example keeps its code, identity and count.
- With the final settlement known, an in-the-money, an out-of-the-money and an
  exactly-at-the-money holding each take the rule's outcome. Before it is known,
  and at a knowledge cutoff before its publication, expiry is refused and writes
  nothing.
- The linked future is booked at the strike at the close instant, adopts its policy
  and its final margin equals final settlement less strike times lot times lots, by
  hand arithmetic. The observation that decided exercise is the one that margin
  pays on.
- An ordinary futures booking at the last-trading instant is still refused.

**Piece 3**

- A margined booking writes no cash; a retry returns the first outcome; each
  mismatch is refused.
- A hand-computed daily sequence gives each variation, the closing determination
  and their sum of minus the premium; the buyer's cumulative margin never exceeds
  the premium.
- A partial exercise closes its lots at zero and leaves the rest margined.
- A close with margin behind its business instant is refused.
- An assignment recorded after the next occurrence was determined restates it: the
  cash at each due instant equals what in-order recording gives, and a view at the
  earlier knowledge cutoff shows the original determination.
- A corrected settlement restates the determinations after it.
- Value equals model value less basis; its deltas equal those of the same series
  valued at a unit discount with premium paid up front. An adopted holding with a
  pending occurrence is refused.
- Closing an unadopted holding is refused.
- Every existing futures margin check passes unedited, and the retained futures
  policy hashes are unchanged.

**All pieces.** An HTTP check per role in the table above, and a browser check of
the ticket and of the lifecycle list.

## Highest-risk inputs

1. **Widening the margin records.** Adoption is tied to futures trade rows, a
   determination to one settlement observation per occurrence, and a binding to
   one quantity. The journal restates for a changed source, not for a changed lot
   count. The option holding, its changing open lots, the closing evidence and the
   restatement must fit without changing a futures row, hash or guard. If they
   cannot, the builder returns to the owner before choosing a separate journal.
2. **The same-instant future.** A future booked at its market's close and finally
   margined at that instant uses an opening term that exists but has had no such
   consumer.
3. **Discounting.** A margined option's value and calibration use no
   discounting. Mixing a discounted settlement into it, or discounting a margined
   option, misprices every series of the product. For a European option paid up
   front, the discount tolerance of 0.005 is a first value: the builder confirms
   it against deep in-the-money examples struck at a stated rate difference, and
   records the evidence.
4. **The stated policy.** The Risk page opens a retained run as a scope's current
   report only when it was valued under the stated pricing policy (D396). Adding
   members to the stated policy means earlier runs stop being current and remain
   readable as retained runs. The builder confirms this from the comparison and the
   decision record says so.
5. **The expiry relation on sealed rows.** The column must reach existing sealed
   reference rows the way migration 0382 added its columns, with the migration
   identity check still passing.
6. **Terms a source may not state.** A product's premium style, its at-the-money
   outcomes and its instruction cutoff are required. A product whose source leaves
   one out cannot be listed until it is supplied with a source.

Numerical and temporal changes here need independent adversarial review before
publication.

## Left to the builder

- File, function and constraint names beyond those quoted, and the column shapes
  that widen the margin records within decision 4.
- How the numerical policy record expresses a model with no lattice.
- The exact bounds the checks pin, taken from evidence.
- The fixture codes of the synthetic products and their calendar.
- Whether the three pieces are three migrations or more; each piece's migration
  stands alone.
- The trading P&L's reading of a margined holding, under this invariant: its
  day-to-day P&L is the change in model value, and its margin cash settles that
  P&L and is not added to it.

## Remaining work after this design

- European and margined options in the D401 projection and its reference
  strategies.
- Options that settle straight to cash; average-price, strip, spread and weekly
  options.
- An expiry on a physically delivered future's last day, and one on the last day
  that is earlier than the future's close or judged by the daily settlement.
- A European product whose holders may file contrary instructions.
- The funded-margin valuation for a margined option.
- Initial margin and margin offsets.
- A role that separates recording expiry from trading.
- Who may act on a physically delivered underlying.
