# Bounded physical gas swing

Status: proposed design; no implementation or qualification is claimed. The
first profile and numerical policies below are design recommendations. Build
only after [PR #352](https://github.com/zerodrift/leanetrm/pull/352) is merged and
its resulting master is the implementation base. Re-read the merged imbalance
contract before editing shared files. Do not reserve migration or decision numbers.

## Intent

A gas Instrument can give one party the choice of daily delivery quantities
subject to a shared minimum and maximum over its term. Choosing more today
changes what can be chosen later. The saved Instrument owns that constraint;
the existing physical and payment Sides own their economics and schedules.

The first consumer is a physical purchase or sale against fixed per-unit
payments. It extends the membership-right approach of D448 without adding an
option Side, an option Instrument or a trade created by election. A mandatory
minimum can make the remaining contract value negative. Its value must not be
floored at zero or computed as a strip of independent calls.

The earlier [field contract](../../physical-gas-field-contract.md#swing-rights-and-elections)
describes a larger family. This increment qualifies volumetric swing, not every
SW01–SW08 variant in the [acceptance plan](../../physical-commodity-test-plan.md#park-and-loan-and-swing-rights).

## First executable profile

These are proposed product boundaries, not inferred defaults:

- One swing right per gas composition, over one independent physical membership
  and every ordinary payment/usage-fee membership following its delivery quantity.
  Name the covered entries explicitly and validate the complete set.
- One to 31 consecutive whole gas days. The term may cross a calendar month;
  there is one cumulative budget for the entire term, with no monthly reset.
- A constant positive reference daily quantity, constant daily minimum and
  maximum, and a positive contractual quantity increment. Each admissible
  quantity, including both total limits, is an integer number of increments.
  The increment is a commercial restriction, not a pricing discretization.
- Minimum and maximum total quantity, both required. A positive minimum is a
  physical obligation, not minimum billing or a cash penalty for non-take.
- Holder `us` or `counterparty`; settlement is physical by recorded election.
  Each deadline is the first instant of its retained gas day minus the explicit
  nonnegative lead hours, under `lead_before_first_gas_day_v1`.
- Fixed per-unit payment prices and constant positive shares, in the physical
  membership's native quantity and currency. Each covered payment spans the
  same delivery term; its existing row/payment-date policy remains authoritative.
  Fixed usage fees are allowed and enter the same per-unit choice economics.
- One positive, unadjusted point-price curve for the physical membership, with
  the explicit single-factor pricing binding below. Additional point valuation
  adjustments, multiple floating prices and implicit FX/unit conversions refuse.
- Explicit no discounting, as in the admitted gas composition profile.
- Explicit `swing_elected_delivery_quantity_v1` selection: an accepted election
  supplies the commercial fallback quantity; the existing scheduled/allocation
  selection rules supply operational quantities when present. No election is
  inferred from an operational record.

Independent charges, plain memberships and D448 rights on disjoint sources may
coexist within the existing eight-Side/seven-payment bounds. A premium is an
ordinary D450 lump sum outside the swing right. A #352 imbalance cash-out pair
may refer to the same physical source but remains outside the covered entries:
it consumes reported absolute balances, never a modeled swing quantity.

Not admitted: cash automatic swing, floating-index strikes, fuel links touching
the source, quantity bands/shortfalls on its following payments, partial-day or
period-total reference quantities, variable daily bounds, overlapping rights,
ramping, refraction, change counts, make-up/carry-forward, penalty settlement,
storage or transport entitlement, early discretionary decisions within a gas
day, negative modeled forwards, or continuous quantity elections.

In particular SW03's initial/previous-day change counts, up/down budgets and
resets stay unsupported. They must not be represented by inert ticket fields.

## Ownership, identity and scaling

The immutable `SwingRight` retains its ordinal, physical source slot, covered
slots, holder, quantity increment relative to the physical reference quantity,
daily/total limits in integer increments, quantity policy and exact retained
days/deadlines. It belongs to the gas Instrument's economic identity. Empty
swing terms are omitted from canonical serialization, preserving every existing
no-swing identity byte for byte. Base quantity is descriptive; it is never an
implicit election at a missed deadline.

The model, numerical grid and mark convention form a separate
`SwingPricingBinding`, included in `PricingBindingIdentity`. The booking request
must select them explicitly. This increment has one installed model and no
pricing-binding editor. Selected curve revisions, volatility revisions and the
resolved cutoff belong to calculation inputs, not to economic identity.

Elections belong to the exact trade and binding. They hold absolute quantities
in the retained native unit. Operational quantities and recorded cash also stay
absolute. None is multiplied when another trade has a different size.

For a new, unexercised right, multiplying its reference quantity and its
quantity increment by the same positive factor multiplies every feasible
covered schedule and its unrounded economic value by that factor. Daily/total
integer limits do not change. This theorem concerns the covered right only:
an unchanged independent premium/charge and absolute operational/cash facts do
not scale. The model's held-amount rounding does not preserve exact numerical
homogeneity, so the inherited normalization must not be sold as that guarantee.

For ordinary stochastic reference fixtures, independently recompute both sizes
and compare their covered-right values within 0.00000001 currency units plus
`(1 + scale) * (days * dailyMaximumLots + 0.5) * 10^-18`, the conservative
input/output rounding envelope for those fixtures. Test the small-amount failure
of exact homogeneity explicitly: one optional lot, forward 1, fixed payment 0.4,
volatility 0.1, one hour to deadline, and increments `10^-18` and `2*10^-18`
both produce `10^-18` after the specified rounding. Neither full composition
value nor an elected position acquires an arbitrary scalar-reuse permission.

The existing retained valuation path computes with the actual binding quantity
and facts before normalizing its result for the external Trade multiplication.
Keep that path and its binding-specific `BookValuation.ScopeKey`. Never reuse a
one-unit result across different election states or scale recorded elections.
An ordinary size change must create/reconcile a binding through an admitted
amendment contract; this increment does not enable running-gas amendments or
unchecked scaling of an existing elected position.

## Exact feasibility and elections

Let `q` be the held quantity increment, `l`/`u` the daily integer limits and
`L`/`U` the term limits. Admission requires `q > 0`, `0 <= l <= u`,
`0 <= L <= U`, and a nonempty feasible intersection with `[n*l,n*u]`.
The physical reference quantity is positive, on this grid and within daily bounds.
Redundant but consistent total bounds are permitted and remain as entered.

For a current view, let `c` be the sum of visible accepted election lots and `m`
the number of undecided days, including future days. There must be a completion:

```
max(L - c, m*l) <= min(U - c, m*u)
```

After proposing `k` lots for one undecided day, apply the same check with
`c + k` and `m - 1`. A locally valid election that makes the remaining minimum
unreachable refuses. Recorded future-day elections reserve their quantities as
soon as effective; they need not be a chronological prefix of delivery days.
For a correction or batch, replace the old selected quantities and validate the
complete candidate state atomically. Do not count an election twice on retry.

`SwingElection` identifies right, gas day, absolute quantity, exercising party,
submitted instant, effective instant, revision and evidence. Submission and
effectiveness are distinct from database publication. Require the correct
contractual holder and
`binding effective <= submitted <= effective <= retained deadline`.
This is a record of an accepted commercial election, not a command sent to a
counterparty or a pipeline. Late recording of evidenced timely acceptance is
allowed; it is not acceptance of a late exercise.

The native command records a nonempty bounded batch on one right. It requires
`AmendTrade`, current source/book authority, the expected trade revision, and a
complete expected-head list for the right's days, including null heads. Serialize
on the trade/right, recheck the head list and the whole budget after acquiring
the lock, and publish the fact revisions and receipt together. Two concurrent
commands for different days must not overconsume one cumulative allowance.

A changed fact is a revision with its predecessor; effective instants do not
move backwards within a day's revision chain. Withdrawal is a revision carrying
no quantity and explicit evidence. It makes the day undecided when effective.
Semantic retries return the original fact even if superseded, without reviving
it. Request retries return their first receipt. Neither rewrites prior cash.

Validate candidate feasibility at every affected effective-time boundary, not
only the latest heads. Otherwise a correction could make an intermediate
historical view impossible. A withdrawal may leave an elapsed undecided day;
record the correction and report the determination refusal on valuation.

### Temporal selection

Match the existing exercise-store rule: first restrict revisions to the resolved
knowledge cutoff, then to `effectiveAt <= valuationAt`, then choose the greatest
revision per day. A selected withdrawal stays withdrawn; it does not revive an
older quantity. A known future-effective revision does not displace the earlier
effective revision yet. Retain the next known effective transition for time reuse.

At or after an undecided day's deadline, complete valuation refuses with
`swing_determination`. There is no forecast election, base-volume fallback,
automatic minimum take or automatic abandonment. This includes missing elections
after the final day and a missing zero election on a day with a zero daily minimum.

Actual flow may violate commercial limits. Preserve valid nominations, cuts,
measurements and allocations in their existing ledgers. They do not consume or
restore swing allowance. Show commercial and operational quantities separately.

## Day quantities and lifecycle value

The shared held state gains fact-only swing elections and the next effective
transition. The gas adapter and Instrument schedule prepare immutable day inputs;
each Side still runs only its own retained program.

1. Resolve eligible elections and check the remaining feasible set and deadlines.
2. For an elected day, start from its elected commercial quantity. Apply the
   already installed operational-selection order and finality rules, replacing
   only their contractual fallback with the election. Preserve the original
   retained reference quantity as the scaling denominator and both provenance
   chains. A separate `elected` quantity label identifies the new fallback.
3. Undecided covered days have zero ordinary contributed quantity. Existing
   operational records on such a day cause `swing_conflict`; they are not elections.
4. Following covered payments receive the same selected quantity times their
   exact share. They accrue only on ended days and apply actual recorded payments
   with today's rules, including part paid, overpaid and zero corrected accrual.
5. Add one joint remaining-right value to unrealized value. It includes the
   remaining mandatory quantity as well as discretionary quantity. Never add a
   second independent option value for each day or double-count daily minima.

A separate counterfactual Side occurrence supplies the model's per-increment
physical and fixed-payment economics on undecided days. The ordinary Side
occurrence is unchanged. Side evaluation, row-to-day allocation and normalization
reuse D448/D447 behavior. The Instrument must not execute program instructions
itself or create peer-Side calls. Whole-term payments allocate by exact day
quantities; already elected days are excluded from model economics.

An early election for a future day moves that day's economics into ordinary
Side value immediately and reserves its quantity in the remaining budget.
Its payment remains unrealized until its own days end. Published marks and
operational cuts do not retroactively change the commercial reservation.

Charges, recorded cash and #352 reported balance cash-out keep their existing
consumers. A swing election creates none of these facts and never dispatches cash.

## Pricing contract

### Alternatives and selected first model

Independent daily calls cannot enforce a shared volume budget. Intrinsic
optimization is useful as a separately labeled feasible plan, but assumes the
current forward curve and is not the full uncertain value. A multi-factor,
mean-reverting or jump model needs additional explicitly governed inputs.

The proposed first model is `gas_swing_one_factor_lognormal_v1`: one common
Brownian information factor for the admitted point curve. Under its explicit
pricing measure, each delivery day's forward has constant log-return volatility
equal to that delivery month's supplied curve mark. The continuous reference is

```
F_d(t) = F_d(0) * exp(sigma_month(d) * W_t - sigma_month(d)^2 * t / 2).
```

Time is elapsed instant time from the calculation instant, using
`OptionNumericalPolicies.ActualOver365FixedInstant`. The holder observes the
factor at each deadline and maximizes expected signed receipts less payments
subject to the remaining feasible quantity set. Future shocks are not visible
when making an earlier decision. Prices after election are valued at the
conditional delivery forward; election is not early physical delivery or payment.

The one-factor assumption imposes common shocks across the covered delivery
tenors. It is an explicit model restriction, not a correlation inferred from
marks. The model consumes no correlation marks and assumes no mean reversion,
seasonal volatility interpolation, demand, transaction costs or jumps. All
monthly volatility inputs must exist, including an explicit zero when selected.
No adjacent-month fallback or volatility calibration is introduced. The existing
curve-mark editor is the authoring consumer.

### Finite numerical model

Use `swing_trinomial_6h_half_even_18_v1`, implemented with `LatticeDecimal`.
This is a finite approximation, not a claim that its result equals D448's
analytic single-exercise formula to the last digit.

Anchor six-hour time steps on the first undecided deadline. Include the exact
calculation instant, every later undecided deadline and the last deadline. The
first interval may be shorter than six hours. Dates are never rounded. Let
`h = sqrt(3 * 21600 / (365 * 86400))`. A step of `delta` seconds moves integer
factor node `j` to `j-1`, `j`, `j+1`, with probabilities

```
p_minus = p_plus = delta / 129600
p_zero = 1 - 2*p_plus
```

For each required monthly volatility `sigma`, define

```
m_sigma(step) = p_zero + p_plus*exp(sigma*h) + p_minus*exp(-sigma*h)
M_sigma(0) = 1
M_sigma(step+1) = M_sigma(step) * m_sigma(step)
F_d(step,j) = F_d(0) * exp(sigma_month(d)*h*j) / M_sigma_month(d)(step)
```

This finite-grid normalization preserves each forward's martingale expectation
under the same transitions, including unequal monthly volatilities and the
fractional first interval. Do not use an unnormalized sampled lognormal drift.

The contract state is cumulative elected lots, not an enum with one state per
future exercise date. At a decision node, for each feasible quantity `k`, add
the holder's per-increment net economics times `k` to the conditional continuation
at the updated volume state. Between deadlines, roll conditional expectations
only. The last deadline is a decision before the terminal feasibility check.
An infeasible state is explicitly absent, never a large negative sentinel or
zero. Ties choose the smaller current quantity, with dates processed chronologically.

Precisely, let `A_d` be the signed uncertain physical amount for one increment
and `C_d` the signed fixed-payment/fee sum, both from the holder's perspective.
The immediate amount is `k * (A_d * exp(sigma*h*j)/M_sigma(step) + C_d)`.
`A_d` may be negative for a holder delivering gas; the underlying physical
price must be positive. At terminal, value is zero only for a feasible total.
At a decision, maximize immediate amount plus successor expectation; a
non-decision step has no quantity choice. Already elected days contribute no
immediate model amount and reserve their lots before this recursion starts.

Inputs from exact Side amounts enter the model rounded half-even to 18 decimal
places per per-increment uncertain and certain amount, as D448 does at its
boundary. Retain `LatticeDecimal` through the complete backward induction;
round only the joint result with `ToExact()`. Apply the holder/position sign and
add ordinary exact lifecycle amounts afterwards. Never round a node to currency
minor units. No decimal conversion is introduced into contractual quantities.

With all required volatilities explicitly zero, use an exact rational intrinsic
solver over the identical feasible set. If only one future schedule is feasible,
its deterministic forward value is exact and needs no volatility; the pending
elections and deadline refusals still remain. Do not introduce an intrinsic
fallback when a genuinely stochastic calculation is unavailable.

Bound work before allocating: at most 31 delivery days, 33 quantities per day,
2,049 reachable residual-volume states, 1,600 time steps and 20,000,000 reachable
node/state/action evaluations. These proposed v1 limits must be exercised in
qualification. Overflow, unsupported magnitudes or excess work gives
`swing_numerical_scope`, with no silently coarser grid. Preserve cancellation
within each bounded block of work. Use rolling value layers, not the entire
policy tree, for ordinary valuation.

### Risk and numerical qualification

Declare the existing point-curve factors and each monthly volatility actually
used. Use `curve_marks_log_return_held_v1`; forward shocks hold volatility unless
the shared scenario explicitly shocks its volatility factor. Reprice the whole
constrained problem for every scenario. There is no trade-specific price bump,
synthetic correlation risk or exposure allocated from expected exercise volume.

Retain model/grid IDs, ordered day inputs, all mark revisions, bound quantity,
election revisions, market scenario, calculation instant and knowledge cutoff
in calculation identity. Time validity ends at the earliest pending deadline,
known election transition or existing delivery/payment/market boundary.

Finite-grid decision kinks are possible. Report risk as the existing finite
scenario PV differences with their actual bump and numerical policy, not as a
claim of smooth analytic gamma. Compare the production grid to independently
computed finer grids and one-day analytic values before admitting the profile.
Persist diagnostic resolution/error comparisons in verification, not a hidden
automatic resolution switch. The finite model must agree with an independent
80-digit decimal implementation to 0.00000001 currency units on the fixtures.
For a single optional 10,000-unit election 30 days away at volatility 0.50,
test `(forward,strike)` pairs `(3.20,3.00)`, `(3.20,3.50)` and `(3.00,3.00)`.
The six-hour grid must differ from the analytic lognormal call by at most 2
currency units; a separate 45-minute reference grid by at most 0.25. These are
qualification tolerances for these fixtures, not a universal error guarantee.
The six-hour 3.20/3.00 case is approximately 2,948.8581864; the analytic value
is approximately 2,947.4418966. Report the selected finite model honestly.

## Worked synthetic oracles

All amounts below are undiscounted. No fixture uses a real observation.

| Case | Inputs | Independent expected result |
| --- | --- | --- |
| Local and total limits | Three days; base 1,000, increment 100, daily 700–1,100, total 3,000–3,100 | `[1,100,1,000,1,000]` accepted; `[1,100,1,100,1,100]` refused; first election 700 refused because the remaining maximum cannot reach 3,000; 1,050 refused off-grid |
| Intrinsic choice | Three days; daily 700–1,100 in 100s, total 2,400–2,700; forwards 3.20, 2.80, 3.40; pay 3.00 | Quantities `[900,700,1,100]`, value 480 exactly |
| Mandatory loss | Same quantity terms; forwards 2.90, 2.80, 2.70; pay 3.00 | Quantities `[1,000,700,700]`, value -450 exactly; not zero |
| Shared optional budget | Three days; daily 0–10,000 in 1,000s, total 0–10,000; forwards 3.10, 2.95, 3.20; pay 3.00 | Intrinsic 2,000; an independent strip's 3,000 is wrong for this contract |
| Operational cut | Election 1,100; scheduled 900; fixed payment 3.00 and fixed fee 0.10 | Budget consumes 1,100; selected consideration 2,700 and fee 90 before signs; allocation correction does not restore rights |
| Early future election | D3 elected 1,100 before D1's deadline | D3 is ordinary future value; 1,100 is already reserved; D1/D2 optimize the reduced allowance |

An independent two-deadline induction oracle uses a supplied tiny martingale
tree, not the production grid: root price 3; first-step prices 4 and 2 with
probability 1/2; each next step multiplies by 4/3 or 2/3 with probability 1/2.
Pay 3; elect 0 or 1,000 each day, maximum total 1,000, minimum zero.
The correct joint value is `1,000 * 7/12`, rounded once at the output boundary.
After price 4 at the first date, waiting has conditional value `7/6` per unit,
greater than the immediate 1. Perfect hindsight gives `1,000 * 5/6` and is
incorrect; summing the two independent calls gives `1,000 * 13/12` and is also
incorrect. An independent enumerator must enumerate adapted decisions, not pick
the best exercise separately on each complete future path.

Also prove:

- With no shared restriction, the solver equals a sum of one-day choices on
  the same finite tree, not necessarily the analytic formula.
- Reversing all membership directions and exchanging holder labels negates
  the result exactly after rounding. Merely changing the holder on unchanged
  economics is a different contract and does not generally negate value.
- Volatility zero, forced quantity, zero elections, tiny quantities, rational
  membership shares, the last deadline and both sides of a month boundary.
- A future-effective correction and a later-known correction independently
  leave an earlier view unchanged; selected withdrawals never resurrect facts.
- A payment applied partway through a whole-term row remains paid after an
  election/quantity correction; surplus remains an overpaid balance.
- Two bindings sharing economic terms but different elections never share PV;
  proportionally scaled new rights satisfy the economic theorem and numerical
  comparison envelope separately, while an unchanged premium stays unchanged.
- Concurrent distinct-day elections cannot violate the term budget. Retries,
  permission expiry, stale expected heads and expired worker leases refuse or
  replay under the existing authority contracts.
- #352 statement amounts and D450 charges stay unchanged by a swing election;
  their unavailable-data markers are not swallowed by the joint valuation.

## Persistence and service boundary

Add narrow relational tables for swing terms, exact member links, retained days
and deadlines, the pinned pricing binding, election revisions and command
receipts. Use exact foreign keys to the saved Instrument/membership/schedule.
Do not store a second editable operational schedule or a generic JSON event log.

Extend `security.write_gas_composition` and `GasCompositionStore` together.
Native admission independently verifies the same term grid, ownership, clock,
scope and feasibility contracts as managed admission. Keep unknown-key refusal,
request replay and additive upgrade predecessor pins. No-swing requests omit
the optional key; old saved programs remain byte-identical.

Proposed routes: `POST /api/gas/swing/record` and `POST /api/gas/swing/read`.
Use existing owned-context input frames and current authorization. Native reads
support risk workers only inside their exact leased job/book/trade scope. Every
read carries business time and resolved knowledge through trade head, terms,
elections, quantities, prices, marks and payments. Capture/recheck all consumed
inputs under the established publication and commit fences.

Preview, single-trade read, schedule inspection, Blotter, P&L consumers and
queued risk must call the same retained Instrument path. Unsupported valuation
on one trade produces its own reason and the existing incomplete-total marker;
it must not price that trade at zero or prevent other eligible rows from valuing.

## Ticket, election action and inspection

Use the existing description-driven gas ticket. A `Swing right` section names
the physical source, covered fixed payments/fees, holder, quantity increment,
daily/term bounds, lead hours and explicit quantity/model/numerical selections.
No financial field is silently prefilled. Show reference quantity and resolved
deadline examples from the backend. Keep unsupported change-count/ramp controls
absent from this executable section. Errors point to their owning field.

The booked trade offers `Record swing election`. A day/range grid shows the
retained deadline, elected quantity, daily limits and the feasible remaining
total range. The user supplies quantity, exercising party, submission/effective
times and evidence. The server validates the whole draft; one submit creates
one atomic command. Pending-outcome recovery reuses its operation ID and exact
payload. A model proposal never populates or submits an election automatically.

The schedule separates `elected` days from `undecided` days, with pending rows
labeled `contingent`. Show commercial elected quantity beside selected operating
quantity, with separate evidence. An explicit zero is shown as `elected · 0`.
Display the joint remaining-right value once at right level, with total budget,
committed amount, remaining feasible range and next deadline. Do not fabricate
additive daily option values for a coupled contract.

A quant-details drawer shows model/grid IDs, current forward strip, monthly
volatility values and revisions, the one-factor assumption, exact intrinsic plan
and its separately labeled value, scenario bumps and work counts. The intrinsic
plan is a current-curve comparison, not a forecast or an accepted nomination.
Use aligned units, tabular numbers, restrained state colors, visible cutoff
labels and keyboard navigation. TypeScript formats backend results only.

## Refusals

| Code | Meaning |
| --- | --- |
| `gas_composition_input` | Booking fields/relationships violate the admitted profile; include field coordinates |
| `swing_right` | Invalid retained terms, grid, coverage or overlapping right |
| `swing_election` | Invalid party, quantity, timing, group or fact payload |
| `swing_election_conflict` | Stale predecessor/head set, revision or conflicting command |
| `swing_feasibility` | Daily/total limits leave no feasible completion |
| `swing_determination` | An elapsed deadline has no eligible recorded election |
| `swing_conflict` | Operational/payment state requires a commercial decision that is missing |
| `swing_model_scope` | Economics or price behavior outside the selected model |
| `exercise_marks` | Required explicit curve volatility missing/unusable; reuse existing mark refusal |
| `swing_numerical_scope` | Grid/work/magnitude outside the declared numerical envelope |

Authorization retains SQLSTATE `42501`; payment/quantity lifecycle failures keep
their existing codes. Never turn a denial into an empty election history.

## Qualification and sequence

The [implementation plan](../plans/2026-11-25-gas-swing.md) follows terms and
independent numerical checks, managed lifecycle, booking persistence, election
authority/history, every-route parity, ticket/browser, then full review. Start
none of its implementation tasks before #352 is merged. Integrate its cash-out
state, entry projections and writer shapes; do not replay their predecessors.

Use synthetic data and disposable PostgreSQL through `tools/run_preview.py`
and `tools/check_all.py`, with `PG_BIN`, `DOTNET` and `BROWSER` explicitly set.
Keep existing expected values unchanged. Obtain independent adversarial review
of model non-anticipation, volume feasibility, numerical policy, quantity scaling,
temporal selection, command concurrency and authority before publication.
Document actual workload (days, grid nodes, volume states, scenarios, evaluated
actions, memory, elapsed time and environment), not performance inferred from
trade count. Signed local commits only; publication requires authorization.

## Method references

The dynamic-programming formulation and the distinction between daily and
aggregate constraints are consistent with the original research in
[Jaillet, Ronn and Tompaidis, Valuation of Commodity-Based Swing Options](https://faculty.mccombs.utexas.edu/stathis.tompaidis/research/swing/swing_published.pdf)
and [Tashiro, Pricing Swing Options with Typical Constraints](https://orsj.org/wp-content/or-archives50/pdf/e_mag/Vol.54_02_03_086.pdf).
The finite-grid policy in this document is a project design choice, not their
model or calibration. No market dataset or implementation is imported.
