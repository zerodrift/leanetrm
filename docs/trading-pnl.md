# Trading P&L and attribution

The Risk report compares two explicitly retained close contexts over one portfolio,
book and trade scope. It must use the common Instrument/Side PV and immutable
scenario contracts, independently of instrument family or pricing model.

The agreed value basis is remaining economic value. Paid amounts are excluded;
due but unpaid receivables and obligations remain. A due date alone is not a
payment. The report reconciles value movement with signed economic cash and
separately identified non-earning transfers. Currencies remain separate exact
references unless a translation policy is explicitly selected.

Cash projections retain each allocation's stable identity and resolved revision
under each knowledge cutoff, including known facts with a later effective date.
A settlement header allocated to multiple holdings supplies distinct native
allocation identities. No identity is generated afresh by a report read.
Recognition follows effective time; revision resolution follows knowledge time.
Repeated allocation, paid-time and amount facts return their original revision,
even after a correction. They append no publication and do not reinstate an old
head. A new value still requires the current expected revision. Exact command
retries return their first outcome under current authorization.
Cash corrections affecting the opening balance are separate from cash effective
in the comparison interval, which is open at the opening instant and closed at
the closing instant. Non-earning transfer movements are subtracted from value
and economic-cash movement rather than reported as profit.

A complete point explicitly attests remaining-value, cash and transfer coverage.
Missing accounting capability is a structured refusal, not an empty cash ledger.
Position absence means no holding only within that complete projection. Exact
trade, binding and book identities are retained; contractual amendments, exercise,
splits and transfers require their owned transition evidence. Contract codes,
prices and economic similarity cannot establish continuity.

## Attribution contract

Actual economic P&L and its explanation are separate results. The position bridge
must retain continuing holdings, new trades, closures, amendments and transfers.
Continuing exposure is repriced through the shared PV boundary. Declared factors
supply first-order, curvature and mixed effects using the existing finite-
difference engine and explicit bump, unit and convergence policies. A displayed
Greek is an approximation; its residual remains visible and reconciles to the
actual movement. Missing factor coverage must not appear as zero sensitivity.
Volatility, discount and time inputs need declared scenario capabilities rather
than model-specific report formulas. Time roll must preserve its selected fixing,
lifecycle and market-input policy.

A holding is explained only by the price inputs its own valuation declares; an
input it does not declare cannot move its value and adds no estimate to it. Mixed
gamma is requested for each pair of factors that one holding prices together. A
pair no holding prices together has an exactly zero mixed difference and is not
repriced. A report covers at most 16 price factors, and 16 such pairs in one
market partition; a larger scope is refused before pricing, with its counts. No
instrument the report supports today prices two factors, so the joint-pair
request is covered by the calculation checks alone.

An estimate is unavailable when its central differences do not converge, or when
one of its factors has no exact closing price. Such a factor keeps its opening
estimates and has no movement. A Greek effect none of whose estimates is
available has no amount in a currency's bridge, where it moves nothing. A partly
available effect keeps its amount with the count of unavailable estimates. Either
way the unexplained part stays in the visible residual.

## Price bump

The request selects the bump of its opening Greeks and the agreement their
estimates must reach. Nothing is filled in. Factor policy
`held_primitive_prices_selected_bump_central_v1` takes `factorBump`, every member
stated: 1 to 1,000 trading ticks; 2 or 4 price steps, or null for ticks alone; a
nonnegative absolute tolerance; and two relative tolerances from 0 to 1, one for
deltas and one for gammas, mixed gammas included. The former
`held_primitive_prices_one_tick_central_v1` is retired (D394): a request that
names it is refused.

A valuation may declare the price step of one of its price inputs: the smallest
move of that price its model resolves, inside which its value follows the
model's grid. A factor priced by a holding in scope that declares a step is bumped by
the selected number of steps of the largest such step, rounded up to whole
ticks. Every other factor, and every factor when no price steps are selected, is
bumped by the selected ticks. A tick bump smaller than two declared steps is
refused before pricing, naming the factor and the ticks two steps span: its half
step would lie inside one step, where a second difference reads the model's
grid, not its curvature.

An estimate is used when its half, nominal and double steps agree within the
absolute tolerance plus its Greek's relative tolerance times the nominal
estimate. The agreement applies to every factor of the report. A bump whose
double step leaves a quote's domain fails the run in the shared scenario
operation, and one that takes a price past what a valuation accepts fails it in
that valuation. For a [tree-priced option](futures-options.md) two price steps
reach a non-positive futures price only where volatility times the square root
of the years to expiry, its width, exceeds 2.2; four steps do above 1.18. That
failure names neither the factor nor the bump.

The policies the page reads offer two price steps, one tick, 0.000001, 0.15 for
deltas and 0.10 for gammas. The offer is shown and sent; it is never applied to
a request that names none. Its reasons, for a tree-priced option:

- **Two steps.** The half, nominal and double steps land on one, two and four
  whole steps of the holding that declared the step. Four steps are the only
  other count accepted: an odd count puts the half step between two bends.
- **0.10 for gammas.** A factor has one bump. Another option on the same future
  has another volatility and so another step, and under that step its half
  step lands between its own bends. Of fourteen such options, under steps 1.2
  and 1.5 times their own, fewer than ten have a gamma within 5% and thirteen
  within 10%.
- **0.15 for deltas.** One bump serves both Greeks. Across four price steps a
  small delta changes by more than 5% of itself, so a 5% agreement withholds
  it. The double step errs about three times as far as the nominal, so 15%
  keeps a used delta within about 5%.

What the calculation checks measure for a tree-priced option under the offer.
No bound is claimed for every option.

- **Listed options.** On the oracle's options and five more at the money, each
  bumped by two of its own price steps, a gamma's estimates agree up to a width
  of 1.5 and not from 2, and a used gamma is within 7% of its reference:
  Black-76 at a unit discount, and an independent 2,000-step tree under
  discounting. At the money it reads high by about 0.4 times the price step as
  a share of the futures price, because the bumps are additive and the lattice
  is geometric. A used delta is within 5% of Black-76, low-delta options
  included.
- **Seeded sample.** Of 48 undiscounted options drawn from a fixed sequence,
  one week to eighteen months from expiry and within two standard deviations of
  the money, every gamma and every delta is used, the worst 3.0% and 3.5% from
  Black-76.
- **Largest errors found.** For an option with time value a used gamma was
  found up to about 8% from its reference: 7.8% high for a put 1.6 standard
  deviations out of the money at a width of 0.9, 8.2% low for a call beside its
  early-exercise boundary, and 8.1% high for a put 2.2 deviations out under
  another option's step 1.1 times its own. A used delta was found up to 5.6%
  high, for a five-delta call under a step 1.75 times its own.
- **Other holdings.** A holding's estimates depend on the other holdings in
  scope that price its factor, since the largest declared step sets its bump.
  Reports over parts of a scope need not add up to the report over the whole.
  Under another option's larger step an option exactly on a bend can lose its
  gamma and a low-delta option its delta: the checks pin one option on a bend
  under a step 1.5 times its own, and two low-delta options under a step twice
  their own.
- **Exercise.** Deep inside its exercise region an option's estimates are
  exactly zero and agree. Nearer the boundary they reach across it and mostly
  do not agree, and the option has no gamma. Close inside the boundary
  they can agree again, because every down leg lies outside it: the option is
  worth its intrinsic value, which has no curvature at that price, and is
  still given a gamma. The checks pin one such call.

The [valuation context and scenario layers](valuation-context-layers.md)
contract supplies the shared mechanism for time, discount and volatility inputs.
Both one-calendar-day model decay and opening-to-closing time roll are required,
with the former initially selected in the UI. A one-day diagnostic does not
silently stand for a multi-day P&L component; projected lifecycle/cash effects
remain distinct from actual cash and the full reconciliation target.

One calendar day runs from the opening frame to the same local time on the next
calendar date of a calendar clock (D398). Where the opening frame is a recorded
close, the clock is that of the calendar its exact close session version pins:
the request names no clock, its `clock` member null or absent, and one that
names a clock is refused with `pnl_time_policy`. Where it is a working snapshot,
which belongs to no close convention, the request names the clock as a full
clock rule; the policies offer two, and a request that names none is refused
with the same code. The roll to the closing instant takes no clock. The roll and
a clock the request names are decided before any endpoint is read. A
convention's calendar and clock rule are reference data, read under the opening
endpoint's own bound context, its knowledge cutoff and effective date, before
the closing endpoint is prepared; where they cannot be read, one calendar day is
refused with `pnl_time_clock`. Nothing else supplies a clock. Two clocks give
different instants only across a change of offset. New York changes its offset
early on a Sunday, so from a weekday close there the day is always 24 hours;
from 17:00 on Saturday 2026-10-31, a close only a calendar with Saturdays has,
it is 25 hours on that clock and 24 on UTC.

The initial
report attributes opening primitive-price delta, gamma and mixed gamma only.
Generic calibrated-IV/weighted-node sensitivities exist in the shared engine;
cross-close volatility, rate, fixing, model, state and other effects remain in the
visible residual until exact factor correspondence and ordering are qualified.

## Run and save

The page has Run. After the result is presented it offers Save report. Save
preserves the exact backend-produced artifact, both endpoint contexts, scope,
policies, input identities and engine/result evidence. It does not recalculate
or accept result values supplied by the browser. The retained execution/report
infrastructure owns the artifact; saved reads reauthorize both endpoints and
underlying market, position and cash sources under current authority.

Run retains the completed JSON result and source receipts in a process-local
memo. Save carries a small authenticated handle rather than sending that result
back from the browser. Its eligibility window is 24 hours, subject to capacity
eviction and restart; saving never substitutes recalculation for a lost result.
The memo has a 32 MB serialized-payload budget, which is not a measurement of total
process memory. A report's captured result and receipts are bounded to 1.4 MB.
Run always returns its completed result. A result over that bound, or one the
memo cannot admit, is returned without a handle and with the reason, and the page
presents it as not savable. The returned result itself is bounded only by the
valuation admission limits. Each endpoint's discount evidence states a rate-curve
definition once. Each payment keeps its own factor and pillars and names that
curve and its endpoint market snapshot by identity.

Durable results and first-outcome receipts use separate authenticated protection
purposes and private installation keys. Saved read, exact-request retry and
same-fact retry work after restart without the memo. Current authority is always
checked. After a lost response, an authenticated serialized check can establish
that no durable outcome exists; only that definitive outcome releases the page's
uncertain save request when the unsaved result is no longer available.

## The report and the page

The report states every figure the page shows. An amount is its exact text, a
decimal or a fraction where no decimal is exact, with its currency reference.
The page sends the amounts it shows to the shared amount display and keeps the
exact value beside each; the evidence a report carries is not sent for display.
`factors` lists every opening price factor with its name, exact opening and
closing prices and their change, its bump and the largest price step its
holdings declare, if any. A factor with no exact closing price has
neither a closing price nor a change, and says why. Factors read one curve
after the other, each by delivery period. Each currency total carries its
reconciled values, its bridge, what each factor's delta and gamma explain by
themselves, and its Greek coverage. A bridge step names its kind: an opening
Greek, the residual, period cash or an adjustment. What opening Greeks explain
is an amount, or nothing when none of the estimates is available, for a
currency and for each holding. Holdings read by trade reference, then
instrument reference; ledger cash of a trade held at neither close has no
reference and comes last. `opening.close` and `closing.close` give a recorded
close's convention code, version and name with the business day it closes, and
are null for a working snapshot. For one calendar day `time.clock` gives the
clock rule, what it is (its IANA zone, or its fixed offset from UTC), its
source, `prior_close_convention` or `request`, and for a convention its code,
version, name and calendar; it is null for the roll. A report saved before D398
states neither and still opens.

The page compares a **Prior date** with a **Current date** for the selected
Risk scope (D398). Its pickers list the recorded closes of the header's close
convention, each named by its business day, and the working snapshots, each
named by its instant. The convention is stated once beside them; the header
chooses it, and with none chosen the page lists working snapshots alone and
says where to choose one. Until the person chooses, the pickers show the
header's date and what came before it: the recorded close before it, where it
is a recorded close and one exists, and otherwise the date before it of either
kind. Both are visible and can be changed before Run, and the request names
both explicitly as `opening` and `closing`. A date the person chose that the
convention no longer lists is left unchosen, and the page says so. The calendar
clock is asked only for one calendar day from a working snapshot, with nothing
chosen for the person, and a clock one person chose is not handed to another;
for a recorded close the page sends no clock. A closed Advanced
settings section holds the price bump: price steps or ticks alone, their counts
and the three agreement tolerances, shown as the backend offers them, bounded as
it states and sent as selected. Its title line states the selection while it is
closed. Changing a setting clears the result, a selection is dropped when
another person takes over, and a run pressed before the offer could be shown
shows it and stops. When the header names
no date, what the page offered is withdrawn; dates the person chose stay. The
knowledge cutoff reads as local time: untouched, it is the exact instant of the
Risk scope; edited, the backend resolves it. Whenever the Risk scope is read
again or changed, the result and the cutoff are cleared and the cutoff is
refilled from the scope.

A result opens with the economic P&L, the period and the scope. The period
reads a recorded close as its business day and any other date as its instant;
it names the convention once when both dates share it, and beside each date when
they differ. Under the time effect the page says which clock one calendar day
was counted on, by its rule's code and what it is, and whose it is. What moved
lists
each factor of the shown currency with its two prices, its change and its delta
and gamma effects. The bridge draws one column a step from zero to the result.
The residual is drawn hollow, a Greek effect with no amount has no bar and reads
Unavailable, a partly available one is marked partial, and an adjustment that is
exactly zero is listed in the reconciliation table and not drawn. Holdings name
their book and the factors their opening estimates are on; a holding new at the
closing close has none. They sort by any amount column, and a holding's
estimates open in a row of their own. Holdings whose value did not move and that
paid or earned nothing are counted, and listed on request. The page counts,
orders and words what it shows; it computes no amount.

Save keeps the exact result and redraws only its own bar. A save the server
refuses outright leaves nothing outstanding. A save whose outcome is unknown can
be retried with its original request, or discarded. After a discard or a refusal
the saved reports are read again, and a result they hold reads as saved under
its name. A confirmed save stays confirmed when that list cannot be read. The
list reads the 100 most recently saved reports, lists those the person may still
read with their save time, closes, books and trade selection, and says when
older ones exist. It reauthorizes each report it reads; its cost at a hundred
reports is unmeasured and it has no paging.

## Implementation status

The managed accounting calculation, price-factor attribution and their focused
numerical checks are implemented in the working change. Currency cash uses
explicit recorded-payment state, including partial payments and knowledge-time
revisions; focused native operation and historical-read checks have passed.
Two-context report operations, exact-result saving and the Risk UI are implemented
locally. Disposable PostgreSQL 18 checks have passed native run/save/read/restart,
both time modes for a continuing option, new-and-paid holdings, idempotent retries,
tamper and cross-catalogue refusal, and exact reference-access revocation. The UI has currency
totals, the factor table, the bridge, sortable holdings with their estimates,
explicit close/knowledge/time selection and optional saved reports. Browser
checks cover those views with unavailable, partial and adjustment cases, two
currencies, an unavailable time effect, paging, keyboard focus, run-before-save,
exact retry after remount, a refused, an already-saved and a discarded save,
definitive-unsaved recovery, late run, save and saved-report responses, and
phone and desk widths. The full-application suite checks that the page names the
header's close convention, that its pickers list exactly the header's dates and
follow a header date changed while the page is open, and that the calendar
clock is asked only for a working snapshot as the prior date.
Captured reports retain the engine fingerprint, selected valuation policy, exact
endpoint holdings and discount inputs, unit declarations and Greek estimates,
units, convergence tolerances and sample identities.
The populated upgrade check books two native options before migration 0250 and
proves that their original terms and membership coordinates survive, with separate
owners of a shared premium unit and no inferred payment facts.
Both time modes consume the shared clock layer; unsupported portfolio members
produce an unavailable whole-scope time diagnostic, never an invented zero.
The disposable check counts one calendar day from a working snapshot on the
New York clock its request names, and from a recorded close of the same instant
and inputs on the clock of that close's convention, which keeps Chicago time,
with no clock named: the instant and the amounts are the same, and the report
states Chicago. It also passes the refusal of a missing clock for the working
snapshot and of a named clock for the recorded close (D398). Its session is
inserted directly; no check saves one through the session operation, or reads a
convention whose calendar clock cannot be read.
Report format `trading_pnl_report_v2` carries the bridge and Greek coverage with
each currency total. The disposable check passes one saved report over three
option trades on futures of two curves, with no cross-factor repricing, and the
refusal, with its count, of a run over futures on two delivery months of each
listed family. Under the offered bump every option's delta and gamma in that
report is available, and each gamma is within 2% of the Black-76 gamma at its
held volatility. Under a selection of four price steps and a 2% gamma agreement
the same three options have no gamma amount: each double step, eight price
steps out, reads about 4% below its nominal. The check also passes the refusal
of a missing, incomplete or out-of-bounds selection, of the retired one-tick
policy and of a tick bump smaller than two declared price steps. The
calculation checks pin what the [price bump](#price-bump) section states for
tree-priced options: the listed options at two of their own price steps, the
seeded sample, the largest errors found, the effect of another option's step
and a call close inside its exercise boundary.
A factor without a closing price is covered by the calculation checks alone;
the synthetic datasets have no such close.

A run is one request and is bound by the process request limit. It values each
held option nine times: once at each close, under six price samples and under
one clock sample. A selected bump moves the six price samples and adds none. On
2026-11-13 a report over ten options held on ten underlying futures, 62 opening
scenarios and 90 unit valuations besides the closing values, took about nine
seconds on an Apple-silicon development machine. On the hosted CI runner it did
not finish within the 15-second request limit and was cancelled, so the check
holds three options, a run of about four seconds on the development machine.
Memory was not measured. Wider option scopes need a faster valuation or a run
outside the request limit; both are later work.

Recorded currency payments have an independent complete known ledger, including
facts whose owner is not held at an endpoint. Complete accounting capability is
required across the selected scope; unsupported futures variation margin,
exercise transfers and other lifecycle gaps refuse a full report. General
source composition, non-price worker requests, executable surfaces and complete
non-price attribution remain later scope. This is not a production qualification.
