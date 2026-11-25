# Physical gas swing with daily elections and period limits

The [quantity-rights design](2026-11-24-quantity-rights-with-limits-design.md)
defines the approved quantity contract, election history, deterministic plan and
storage comparison. This design adds an explicitly selected stochastic valuation.
The counted result for assessable undecided days is their joint, contingent value.
The best plan on forwards appears separately with its quantities and value. Both
use the same rights and the same scenario. A plan creates no election, dispatch,
cash payment or storage plan record.

The published baseline is D454 (748b7695). Tasks 1–8 of the
[implementation plan](../plans/2026-11-25-gas-swing.md) built and exercised a
narrower profile on that baseline. Its mandatory term budget, lot elections,
31-day limit and held-money rounding do not implement the contract below. The
remaining tasks cover implementation and qualification. Names for replacement
types and policy versions below are proposals.

## What the trade can state

A retained gas Instrument owns one physical `daily_quantity` right. It identifies
the driving physical slot and every ordinary cash membership taking a constant
positive share of that quantity. Each Side executes its own whole-term program.
The holder is `us` or `counterparty`. Exact retained operating dates, source
schedule and deadline instants identify consecutive whole gas days. Select
`lead_before_first_gas_day_v1` and a nonnegative integer lead in hours from
each gas day's start. Compare instants across DST changes.

| Term | Contract |
| --- | --- |
| Reference | Positive, constant contract quantity B per operating day on one independent physical source. No fuel link to or from it. |
| Daily range | Nonnegative minimum a and maximum b with a ≤ B ≤ b. An election can be any supported exact native quantity in this closed interval, including zero when admitted. |
| Limit period | Explicitly select `delivery_month` or `term`. Month uses the operating date in the retained clock. Each such period owns its own budget and count. |
| Lower and upper total | Independently optional nonnegative quantities. With either bound select exactly one basis. `per_period_v1` applies it as entered to each period. `per_day_v1` multiplies it by the number of retained days in that period, including elected and elapsed ones. An omitted endpoint is unbounded on that side. |
| Count | Optional nonnegative integer C per period with explicit rule `differs_from_base_v1`. Each elected or planned quantity different from B uses one day, in either direction. A zero cap is meaningful. The count resets at each selected period boundary and is never prorated. |
| Planning step | Positive s. Both (B − a)/s and (b − a)/s must be integers. Candidates are a + i·s for i from zero through (b − a)/s. The step determines numerical choices for future decisions. |
| Selection and pricing | Save explicit election/operational selection, plan rule `best_plan_on_forwards_v1`, one-factor model, numerical policy, day-count, marks convention and no discounting. |

Do not supply a basis for an absent pair of totals or a count rule without its
count. For a two-sided total the **stated** upper minus lower must be at least one
step under either basis, before multiplication. For example, per-day bounds
1,000/1,050 with step 100 refuse even for 30 days. Also check each applied
period for feasibility, including a short or partial month. Equal minimum
and maximum totals are outside this contract. Equality of daily endpoints is
valid if they equal B. With B=1,000, a=850, b=1,150 and s=75 the five planned
quantities include 1,000. An election of 1,025 and a total of 3,050 are
legitimate quantities. No absolute amount has to be a multiple of 75.

Retain daily and total quantities and the planning step as exact reduced ratios
to the source's held B, with exact slot/schedule references. Numerical grid
geometry is a retained valuation term of this definition. Include it and the
commercial terms in swing identity, and pin the numerical algorithm/model in
`SwingPricingBinding`. Omit swing serialization entirely on existing no-swing
identities. Record absolute election amounts in the same native unit and exact
input format as other gas quantities. The admitted wire precision is distinct
from the valuation step. No decimal division followed by a tolerance in
managed or SQL admission.

The one-factor model admits one positive unadjusted native point curve with a
unique dated daily or monthly outright lookup for each covered gas day.
Each following consideration or usage fee is fixed, linear per native unit in
the same currency and covers the complete term on the driving clock. Refuse
partial-day/period-total sources, additional quantity parts, missing followers,
two physical sources inside the right, overlapping rights, floating strikes,
forecast overrides, implicit conversion and ambiguous/adjusted projections.
Unrelated charges, sources, disjoint D448 rights and same-source D454 reported
balance cash-out can coexist under existing composition limits. Balances use
operator facts and keep both cash-out Sides outside the swing coverage.

The source's quantity-policy selection determines the operating behavior.
Reuse its existing contract-quantity (no independent policy) and
`delivery_schedule_v1` choices. On this right, the first selects the elected
quantity on an elected day. The second applies the existing eligible
allocation/schedule precedence with the election as fallback. Both ignore
operational facts on unelected days. For an election of 1,100 and scheduled
900, budget use is 1,100 in either case. Selected ordinary quantity is 1,100
without a source policy and 900 under the delivery schedule policy. Without
an election the same schedule of 900 enters neither PV nor the budget.

Present a single explicit choice at booking. Retain source policy and right
choice version in identity. Where a retained right/API needs a selection tag,
map the first choice to `swing_elected_quantity_v1` and the second to
`swing_elected_delivery_quantity_v1` exactly. A wire form carrying both
source and derived tag must agree. Do not expose a second independent selector,
infer a policy from existing records or accept scheduled-only/other policy
versions. Preserve the published meaning of an absent source policy on trades
without a swing right.

No simultaneous monthly and term budget, previous-day count, direction-specific
count, ramp, refraction, changing base, carry-forward, cash penalty or automatic
election is specified. The contract contains neither a stochastic demand model
nor a transport or storage entitlement. D433 storage valuation continues to use
its recorded plan in force.

## Continuous feasibility and reservation

Work in exact ratios throughout, or equivalently in native quantities with the
same unit. In one period let E be the sum of elections in force, h their number
different from B and n the number of days without an election. The latter
includes any missed deadline for the purpose of recording facts. Without a
count let k=n. With a count, reject h>C and let k=min(n,C−h). The exact interval
of attainable totals is

    [E + n·B − k·(B−a), E + n·B + k·(b−B)].

Changing up to k of the identical remaining days continuously from their bases
fills the interval. Intersect it with the stated total bounds. A nonempty
intersection and valid individual elections are necessary and sufficient. With
no open days this tests the elected total itself. A cut, allocation, forecast,
planned schedule or observed delivery cannot change E, h or these bounds.

Use the same contract in managed booking, retained reads and native booking and
recording. A batch first replaces the selected facts for all its touched days,
then checks each touched period at the union of effective-time boundaries. The
writer can sweep per-day deltas in E, elected-day count and changed-day count,
grouped by period and effective instant, including later known revisions. Apply
simultaneous changes before testing. Withdrawals release a day and any count
slot. Individual intermediate mutations of one batch are never visible.
The original Monday/Tuesday correction example must refuse even though the
latest heads alone would admit it.

## Exact forward plan and shared storage search

For an assessable period with n planning days, reserve **all** visible elections
first, including future delivery dates. Let level z be the sum of the
chosen integer indices i. At terminal, the total is E + n·a + s·z. Intersect
the reachable levels with the inward bounds ceil((L−E−n·a)/s) and
floor((U−E−n·a)/s) for each supplied endpoint. Compute these signed floors
and ceilings exactly. The count state starts with C−h and a move consumes one
iff i differs from (B−a)/s. With no count, omit that dimension. With no total,
omit the accumulated level if it cannot affect admissible moves. Coupled days
may skip over fixed elections in date order.

The attainable integer totals with at most k changes contain every step from
the all-base level down to k daily decrements of (B−a)/s and up to k daily
increments of (b−B)/s. Thus a one-sided bound intersecting the continuous
range always includes an endpoint of the grid. A two-sided interval at least
one step wide either contains a grid point or meets a reachable grid endpoint.
This proof also applies after an off-grid election because E translates the
entire interval exactly. A failure to find a grid completion after successful
admission is a structured calculation failure. Never round the election,
increase the limit or inject a residual off-grid action.

Use `LevelPlanSearch` in `OpenEtrm.Shared/Numerics` for the two actual
deterministic consumers. It accepts ordered layers, their reachable level and
optional count coordinates, feasible integer moves with successor coordinates,
exact integer rewards and feasible terminal coordinates. It returns the best
integer objective and ordered moves or an explicit infeasible/work result.
A caller converts exact signed rational day rewards to integer scores with a
common positive denominator and restores that scale exactly. Apply the caller's
budget before materializing states/backpointers and bound intermediate integer
storage. Keep just the two value layers and the chosen move for each reachable
state/day. A missing successor is infeasible. Zero is a valid reward.

On equal objectives compare the current move by distance to its rest move,
then by lower move. With optimal suffixes chosen by the same rule this is the
chronological lexicographic tie rule. Swing's rest is the base index. The
counterparty holder maximizes its own signed reward. Storage's rest is zero,
so a withdrawal wins an equal-size tie with injection. The storage adapter
retains its band-specific asymmetric injection/withdrawal moves, opening
inventory offset, exact fuel conversions, terminal window, 4,001-level check
and existing 20,000,000 day/move precheck (including unreachable levels as
today). It calls `GasStoragePlans.Evaluate` on the returned moves and retains
`GasStoragePlanProposals.Propose`, its public ID, refusal codes, plan and
objective. Storage receives no swing states, new model or price tree.

Swing supplies the same feasible moves and successor coordinates to the
deterministic search and to its price-node induction. The two computations use
different reward arithmetic and continuation rules. There is no universal
pricer or change to the listed-option lattice.

## Elections, assessment and retained valuation

Record on the exact visible trade/binding, right, operating day and holder with
absolute quantity or explicit withdrawal, evidence, submission and effective
instants. Require timely evidenced submission ≤ effective ≤ retained deadline.
Publication and booking can be later. A late-booked trade may record an
acceptance earlier than the binding's economic effective time. Reads still
require the binding to be visible at their selected business time and knowledge
cutoff. At a cutoff before recording the acceptance remains unknown. A later
withdrawal recorded with timely effectiveness can leave the elapsed day
unassessed. Preserve revision ordering, explicit zero, predecessor identity,
certified knowledge, highest eligible revision per day and next known transition.

| Selected day | Ordinary covered rows | Remaining right |
| --- | --- | --- |
| Elected | Use the accepted commercial amount, or the operating amount selected by its explicit policy. Show both chains of evidence. | Reserve commercial amount and compare it with B for the count. |
| Unelected, before deadline, assessable | Covered ordinary rows contribute no contingent amount. Display the exact intrinsic comparison quantity as planned. | Include in the holder's joint stochastic decision. |
| Unelected, at/after deadline | No selected amount or derived accrual. Show the missing election. | Unassessed. |
| Other unelected day in a period containing a missed day and any stated total or count | No selected quantity or modeled accrual. Show why the period cannot be planned. | Unassessed until eligible facts restore coverage. |

A missed day with daily bounds only affects itself. A missed term-budget day
affects every other unelected day in the term. A monthly reset isolates later
months. Elected days, including those later than a missed deadline, continue
to value. A nominated, scheduled or allocated quantity on an unelected day
remains an operational fact. It never triggers an election, blocks an eligible
plan, fills missing coverage or enters ordinary swing PV. Historic and current
views apply these rules independently.

Source the uncertain physical and certain fixed rewards from concrete Sides'
counterfactual unit-quantity occurrences; isolate by date and retained occurrence,
split whole-term linear cash rows only over their contributing days, and apply
signed memberships/holder perspective once. The ordinary occurrence still
values elected operating quantities and cash. An early future election values
as ordinary future delivery at once and accrues cash only under its row's policy.
Add the holder-signed joint value to unrealized once at the right. Do not add the
forward plan as ordinary PV or allocate stochastic value to individual entries.

Keep settled cash absolute. New payment on a covered cash occurrence requires
an elected day in that occurrence and the normal payment checks. Mixed
whole-term rows expose the assessed subtotal and missing coverage. A correction
cannot erase a receipt or its native cash-accounting flow. Assess P&L cash
from the payment lineage even on a swing-only trade without D454 entries. If all qualifying days are subsequently withdrawn,
retain the paid fact and its evidence, identify unassessed paid coverage when
applicable, and do not invent a known outstanding accrual. Keep part-paid,
zero-corrected and overpaid outcomes on known coverage. The action to amend a
trade with **any** recorded swing election (including one later withdrawn)
refuses under the existing serialized trade write.

Carry distinct missing commercial days through entry, right, trade, book,
portfolio, risk and both dates of P&L. Extend D454's incomplete-subtotal path so
a missing source day is counted once across its followers, including a whole-term
cash occurrence. Retain the reason/owner of each missing assessment. At aggregate
levels preserve D454's distinct source-slot/day metric within a binding when
both assessments are missing on the same date, and show both reasons in the
details. Different trades retain independent counts. An all-missing period has
no assessed model or plan value; the sum of known contributions can be zero
only with its incomplete marker visible. Label the comparison model, planned-day
count and scope on any comparison subtotal/roll-up; the counted PV has its own
stochastic scope and coverage. If required market evidence or a numerical
calculation fails on assessable days, propagate its specific pricing refusal.
Missing election coverage cannot hide a failure on another assessable period.

## Stochastic model, size and numerical policy

Keep `gas_swing_one_factor_lognormal_v1`, `curve_marks_log_return_held_v1`
and `act_365_fixed_instant_v1`. The forward for delivery day d is a
martingale under the selected pricing measure with constant monthly log-return
volatility σₘ and one common Brownian factor. There is no estimated correlation,
mean reversion, calibration, interpolation, discounting or missing-mark
substitution. Assessable days with genuine quantity flexibility require the
explicit monthly volatility including zero. A missing forward required by a
Side refuses normally. A continuously forced schedule can be valued exactly on
forwards with no volatility dependency. Establish this from the commercial
feasible set, not the number of grid paths. One test can intersect each possible
current choice with exact future completion intervals for an unchanged day and
for a changed day consuming one count. Only singleton projections for every day
prove uniqueness. A coarse grid leaving one plan on a commercially flexible
period still requires its marks. A selected explicit zero-volatility strip uses
the exact forward solver. Never choose zero volatility on a missing mark.

Use a new numerical policy, proposed
`swing_trinomial_6h_unit_reference_v2`. The fixed six-hour time grid starts
at the exact calculation instant. Anchor regular ticks at the first future
retained deadline's phase. Insert every future retained deadline through the
last modeled decision into this same clock, including any DST splits and dates
with fixed elections. This makes the factor clock independent of a month's
budget and of skipped early decisions. With a calculation before a distant
first deadline, count the entire prefix. Use one factor from calculation time,
including across periods whose budgets reset. A later month cannot restart at
an unconditioned root price on its first day.

For Δ seconds in a transition let h=√(3·21600/(365·86400)),
p₋=p₊=Δ/129600 and p₀=1−2p₊. Node j moves to j−1,j,j+1.
For each consumed σ compute its prefix normalizer M₀=1 and
Mₜ₊₁=Mₜ·(p₋ exp(−σh)+p₀+p₊ exp(σh)).
The multiplier at (t,j) is exp(σhj)/Mₜ. Keep the same
`LatticeDecimal` operation order for reproducibility (30-place half-even
arithmetic). Compute or cache only budgeted arrays, and pin operation order
if caching. Reject positive-multiplier underflow, excess magnitude or overflow.

Normalize **before model rounding**. For each candidate q use exact Side
outputs to derive the holder's signed physical and fixed amounts A(q), C(q)
at one native unit of reference daily quantity, retaining q/B and membership
shares as exact ratios. Equivalently divide the actual held hypothetical
amounts by the positive held B exactly once. Round each of these two amounts
half-even to 18 places at this unit boundary. Immediate model reward is
rounded A(q) times the conditional multiplier plus rounded C(q). At each
reachable (time, price node, period level, remaining count), maximize this
reward plus the expectation of the successor value under the three branches.
Non-decision times only take the expectation. The last deadline still chooses
before the terminal constraint. The fixed dates of elections consume no model
reward. Absent moves/states are infeasible. Equal computed values use the
base-nearest, lower-second comparison. Choose at the **observed** price node,
before seeing its child. Deterministic unique schedules and explicit
zero-volatility valuation use exact rational Side amounts, including values
smaller than the numerical rounding unit.

At a monthly boundary all admissible terminal states transition to the same
initial state of the next period at the same factor node, with that month's
elections and count already reserved. Process monthly problems on consecutive
layers; no product of monthly histories. Skip unassessed/settled commercial
decisions while propagating their intervening factor time. Periods proved
commercially forced may be stripped at inception at their exact unconditional
forward values because their rewards are additive, unconditionally martingales
and cannot affect choices in another period. Apply the same exact strip to a
wholly zero-volatility independent period once its required marks are present.
No such split within a coupled period on the basis of a zero monthly mark alone.

Round the joint normalized stochastic root once with `ToExact()`, then scale
as an **exact rational** by held B and restore the holder/position signs in the
existing Trade normalization path. Do not round the scaled money back to 18
places before recording/reporting exact PV. Exact deterministic and ordinary
amounts keep their own lifecycle contracts. The same normalized inputs and
proportionate elections give identical normalized decisions, tree, rounded root
and plan. Multiplication by actual B then gives exact size homogeneity of
the covered value, including for 10⁻¹⁸ and 2·10⁻¹⁸. Check rational output
through all APIs, storage and display boundaries. This is a proof for two
independently evaluated bindings with proportional commercial and operational
quantities and cash (or none). A fixed premium and an unchanged absolute
payment or allocation keep their own amounts. No cross-binding scalar reuse.

Risk applies shared factor scenarios and reprices the whole retained Instrument
under each affected set of prices and monthly volatilities. No-shock reuse
requires identical eligible inputs. Retain methodology, grid and limits,
actual mark and lookup revisions, retained day/election and operational/cash
facts, binding, scenario, calculation time and resolved cutoff in the normal
calculation identity. Invalidate prepared inputs at the earliest pending
deadline, known effective transition or earlier existing boundary. Report finite
scenario differences and bumps; a changed optimal plan can change the exposure.
Pinned gas time still limits time-effect attribution.

## Resource admission and qualification targets

`GasQuantityTerms.MaximumOperatingDays` and existing Side schedule/program,
precision and request-shape contracts remain upstream limits. Within them
there is no separate 31-day term or 33-choice product limit. Before building
large arrays, booking checks continuous feasibility and preflights both planning
and positive-volatility pricing from the explicit booking calculation instant.
Missing marks do not prevent structural booking and never select a cheaper
zero-volatility estimate. Use the saved clock, terms and future deadlines only. Pass the authoritative
booking calculation instant from the owned writer/context to term admission;
never trust a client-computed work count or wall-clock time.
For late bookings, bound the future graph even if an elapsed period currently
has missing elections. Retain the estimate's clock and policy for inspection.
At valuation preflight again from the actual calculation instant, with exact
reservations and assessed periods. A valid election writes successfully even
when its valuation would exceed numerical scope.

For example, with r=(b−a)/s, after d pending choices in a period the coarse
level bound is 1+d·r with no count. With a count, let K=min(d, remaining
change allowance). For exactly h changes the cumulative deviation from base
ranges over at most 1+h·r integers. Sum that bound over h=0..K and, for each
h, intersect the integer range with exact remaining terminal reachability to
tighten it. With only a count, use K+1 coordinates. With only daily bounds
one coordinate suffices. The transition bound is at most (r+1) times the
pre-decision states, or just the base move when changes are exhausted. At
monthly boundaries restart at one coordinate; the factor width continues
growing. Use the smaller sum of bounded feasible action ranges where terminal
limits restrict fan-out. Do not use candidate-array length as a price-work
estimate.

The booking estimators in SQL and managed code use the same closed-form or
bounded streaming integer counts. Count six-hour ticks and inserted deadline
splits from the selected instant using exact ticks before enumerating layers.
At time t there are 2t+1 factor nodes. Charge the documented costs below for
these nodes, including three expectation branches for every successor state
at nonterminal times plus the actual bounded decision visits. Use the same
byte-cost table for both estimates, with conservatively sized numeric limbs,
array overhead, and exact backpointers. Reject a product exceeding the budget
by checked comparison before forming a machine-sized index. Implement this
estimator as a versioned resource policy with paired SQL/managed fixtures
using identical clocks, inputs, charges and refusal coordinates. Booking needs
no model payoff values or forecast of future elections.

Build sparse layers from reachable coordinates, discarding every state without
a feasible suffix. Derive feasible action ranges from remaining daily/count and
terminal bounds before iterating. Count generation itself, including attempted
and filtered moves. Limit growth incrementally in checked wide arithmetic
before each allocation/expansion. Do not enumerate the raw Cartesian product
in order to decide it was too large. Keep two price/value layers, one set of
per-day plan backpointers and budgeted payoff/normalizer workspace. Stream
decision-layer payoffs or preflight their entire retained size. Account for
both successor expectation and choice work at decision nodes.

Proposed qualification ceilings per right/scenario are 100,000,000 counted
operations and 256 MiB of modeled live workspace. Count topology generation,
exact plan states and candidate visits, price payoff nodes, normalizer updates,
probability branches per successor state, and each priced candidate. Count
full factor layers from inception, including skipped dates. Charge state,
edge, backpointer, transition and live price arrays, indexes and bounded
arbitrary-precision limbs/temporary buffers. For the swing numerical policy,
propose a 4,096-bit ceiling per exact intermediate integer (including reward
denominators, common-denominator scores, values and arithmetic scratch).
Precheck operand bit lengths and checked quotient/product bounds before LCM,
multiplication or score addition. A near-bound exact result may use bounded
temporary space included in the estimate; no unbounded LCM/product is formed
to discover its length. Charge at least 512 payload bytes per live 4,096-bit
integer plus measured object/array/index overhead and scratch multiplicity,
or a certified smaller operand-width bound. For lattice values use the
existing 30-digit fixed precision and magnitude below 10²⁴, including the
implementation's wider transcendental and product temporaries. Measure these
charges against the actual runtime. Apply swing numerical gates to model/search
work only. Commercial quantity comparisons and the existing D433 adapter keep
their own exact admission contracts. Advancing counters/estimates must
refuse before crossing either ceiling. Report maximum active states, decision
fan-out, factor steps/nodes and peak live bytes separately. Test each counter,
a large but sparse term, rejected dense cases and a distant first deadline.
Retain structured `swing_numerical_scope` with the measured dimension and
limit; a financial infeasibility has its own code. Never change step, number
of factors or time resolution to fit. Freeze the actual measured limits with
the version after independent review. Do not ship if the following gates fail.

| Synthetic qualification workload | Required observation |
| --- | --- |
| Seasonal term | 90 days, B=1,000, daily 900–1,100, step 100, at most two changed days, term cap 90,100. Test a 1,050 future-day election as well as the empty history. |
| Annual monthly resets | 365 days from 2041-01-01, same range/step, at most two changed days each month, per-day maximum 1,005 and no lower total. Use actual month lengths. A first-month missed election leaves later months assessable. |
| Prices and marked optionality | Fixed 3.00, monthly forwards alternating 2.95/3.05, annualized volatilities alternating 0.20/0.25, first deadline six hours from calculation. Run base, point ±0.01 and vol ±0.01 including changed months. Already elected days alone or a zero-volatility shortcut cannot pass. |
| Budget boundaries | Prove below/at/over admissions for each frozen ceiling with integer estimator fixtures and at least one actually priced near-limit case. Compare estimated to instrumented counts and high-water memory. Check over-budget failure before large allocation. |
| End-to-end | Book and read both long terms via the ordinary API/native writer and a real worker. Characterize kernel cold/warm, full retained valuation and job runtime separately. Target ≤120 s per kernel call at each fixture and ≤512 MiB peak process RSS on the qualification host. Capture peak RSS during execution, not only before/after. |
| Cancellation | Check cancellation at entry, inside graph construction, exact search, payoffs and priced continuation, at bounded blocks (at most 1,024 candidate/branch visits between polls). On observed in-flight work require cancellation within one second on the measured host. Release buffers and worker state. |

The UTC 365-day fixture gives 1,457 six-hour steps and at most 2,915 price
nodes in a layer. With at most two changes, each layer needs at most
1 + 3 + 5 = 9 commercial coordinates before tighter pruning. The analogous
90-day fixture has 357 steps and at most 715 price nodes. Qualify a DST clock
separately with its actual inserted times. These arithmetic counts size a
test; they do not measure elapsed time or memory.

End-to-end gates require the normal HTTP deadline, owned input-frame and
authority expirations, worker lease and execution budgets to hold through the
actual result/commit. A kernel under 120 seconds can still fail those gates.
Optimize the implementation before considering a separately reviewed scoped
execution-budget change. Never increase global timeouts for this profile.

These are test targets, not measurements or guaranteed throughput. Record exact
fixture versions, host/build, concurrency, unique contracts, scenarios, actual
evaluated and reused work, peak live/RSS/allocated bytes and elapsed time.
For stochastic long fixtures check an independent narrow-state recurrence or
independent finite-tree value on the same pinned grid. Also check that the
annual root agrees with the sum of independent monthly optima valued from
the same inception on the same factor clock, within a derived finite-arithmetic
rounding envelope. No independent monthly recentering at day one.

## Independent financial oracles

Use all [worked examples](2026-11-24-quantity-rights-with-limits-design.md#worked-example)
as exact forward-plan acceptance checks. Base 1,000, daily 700–1,100, step 100,
buy at 3.00, forwards 3.20, 3.05, 3.10 unless specified. Enumerate admissible
small schedules directly and compare ordered quantities **and** value.

| Case | Daily quantities | Exact value to us |
| --- | --- | ---: |
| Daily range alone | 1,100 / 1,100 / 1,100 | 385 |
| Maximum 3,100 | 1,100 / 900 / 1,100 | 375 |
| Same maximum and one changed day | 1,100 / 1,000 / 1,000 | 370 |
| Base schedule comparison | 1,000 / 1,000 / 1,000 | 350 |
| Maximum 3,100, seller holds | 700 / 700 / 700 | 245 |
| Sell at 3.00, buyer holds | 1,100 / 900 / 1,100 | −375 |
| Elect 1,100 first | 1,100 / 900 / 1,100 | 220 elected + 155 planned |
| Elect 700 first | 700 / 1,100 / 1,100 | 140 elected + 165 planned |
| Elect 1,050 first | 1,050 / 900 / 1,100 | 365 (continuous optimum 367.50) |
| Cap 3,150 | 1,100 / 900 / 1,100 | 375 (continuous optimum 377.50) |
| Equal forwards 3.10, cap 3,100 | 1,000 / 1,000 / 1,100 | 310 |
| All forwards 3.00 | 1,000 / 1,000 / 1,000 | 0 |
| Forwards 2.80 / 2.95 / 2.90, lower bound 2,500 | 700 / 1,100 / 700 | −265 |
| Up 0.01, cap 3,100 | 1,100 / 900 / 1,100 | 406 |
| Second forward down 0.10, same cap | 1,100 / 700 / 1,100 | 295 |
| Tenfold size, same ratios | 11,000 / 9,000 / 11,000 | 3,750 |

Retain the other exact bounded-profile values 480 and −450, shared optional
budget 2,000 and the original operational-cut/early-election examples. In the
two-decision adapted tree, start at 3, move to 4 or 2 equiprobably, then
multiply by 4/3 or 2/3 equiprobably. Pay 3 with one 1,000-unit exercise
available over both dates. The adapted optimum is 1,000·7/12, foresight
1,000·5/6 and independent calls 1,000·13/12. Compare at the versioned
normalized output precision. Add a tiny tree with count, monthly reset, an
off-grid reservation and unequal daily rewards. Enumerate nonanticipating
policies by observable node/history, including ties and mandatory negative PV.

Use an independent 80-digit recurrence and normalized rounding oracle. Compare
the production finite grid on the existing ordinary-sized fixtures to 10⁻⁸ in
held currency. For one optional 10,000-unit choice, 30 days, volatility 0.50
and (forward, strike) of (3.20,3.00), (3.20,3.50), (3.00,3.00), retain the
2 currency-unit six-hour analytic error gate and 0.25 gate for an independent
45-minute comparison. These tolerances are fixture-specific. The fixed-grid
3.20/3.00 value is about 2,948.858 against analytic 2,947.442. Test size
homogeneity by exact equality independently of approximation tolerances.

## Persistence, service and presentation

Upgrade the published 0402 schema directly. Revise the unpublished 0403 terms
and 0404 election migrations in place; retain 0405's exact-source lookup and
legacy wrapper behavior, regenerating its writer predecessor pin against the
revised 0403 on stock PostgreSQL. All three ship as one reviewed chain from
0402. There is no released integer-lot swing schema to migrate. Old local
preview databases are disposable. Never apply this rewrite to a populated
deployment of an unpublished migration. No migration through 0402 changes.

0403 stores selected limit period, optional bounds/basis, count/rule, exact
ratios to the reference, planning and pricing policy versions on the existing
immutable right/binding. Preserve exact member, schedule and retained deadline
foreign keys. 0404 stores absolute quantity or withdrawal on the exact binding
and removes the derived integer lot from admission, history and receipts.
A PostgreSQL comparison must use exact integer cross-products or native exact
common-unit addition/multiplication and signed integer div/mod. Rounded NUMERIC
division cannot establish ratio equality or inward endpoints. Preserve
`trading.gas_swing_terms`, `trading.save_gas_swing`,
`security.write_gas_composition`, owned frame/read/write/replay functions,
immutable receipts, runtime and worker scope. Guard every replaced predecessor
body, ACL, owner, search path and execution attributes against the actual
preceding migration, including any newly touched cash or amendment routine.
Run drifted-body and drifted-privilege rollback tests and a populated 0402
upgrade with plain gas, binary rights, cash-out, payments and replay. Keep
0404→0405's exact lookup ambiguity refusal scoped to its offending trade.

The record operation takes a bounded nonempty batch, exact expected trade
revision and the complete distinct expected-head set for the **whole right**
including nulls. Remove the 31-item limit on both heads and actions. Preflight
a versioned payload budget allowing the entire head set for the upstream
1,200-day geometry plus at least one maximum-evidence action. Cover 365-day
commands with distinct day quantities and complete heads, including a change
outside the draft range invalidating its predecessor. Applying a timer, paging
the display or splitting a transport payload must never split an authorized
atomic batch. Return a field error for a batch exceeding the documented request
budget. New rows, semantic duplicates, request retries and publication from
concurrent days obey the same serialized operation and authority-at-commit
checks as the current swing writer. Recheck input frames, trade/book visibility,
permission and the original authority deadline after waits and before commit or
replay. Risk workers use the leased generation and exact scoped read only.

Keep `/api/gas/swing/read`, `/prepare` and `/record` on the same
backend operation. Prepare retaining the opened read context and operation ID
supports replay after a lost response. Native record, HTTP and ticket must
agree on range, continuous feasibility, holder, timing and field coordinates.
The ticket exposes one field per term, optional endpoints/count, selected
basis/rule, plan step, deadline and pricing policies with no financial defaults.
Remove duplicate or placeholder swing range, minimum/maximum and notice fields.
The schedule shows for each period the applied limits/basis/count rule,
elected and separately planned totals, changes used/planned, feasible
remaining interval, coverage and next future deadline. Elected zero, withdrawal,
unassessed and an assessable contingent day remain visually distinct. Show
elected and selected operating quantities and their evidence side by side.

Show the assessed joint value once, independent charges/balances, and the
separately labeled intrinsic schedule and value (including count of planned
days). Include model/numerical versions, per-unit inputs, numerical bounds,
elapsed steps/evaluated work, monthly forward/mark provenance and the selected
cutoff. Never prefill a commercial election from a plan. TypeScript only
renders backend fields and statuses. Shared browser recovery, accessibility
and historical/working authority remain part of qualification.

Keep invalid terms, elections, stale heads, infeasible rights,
unsupported economics, missing marks and exceeded numerical scope distinct
(`swing_right`, `swing_election`, `swing_election_conflict`,
`swing_feasibility`, `swing_model_scope`, `exercise_marks`,
`swing_numerical_scope`). Keep authorization and current lifecycle refusal
codes. Missing determinations are reported as day coverage. Any missing
required model input on an assessable scope remains its own valuation refusal.
