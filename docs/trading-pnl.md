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

The [valuation context and scenario layers](valuation-context-layers.md)
contract supplies the shared mechanism for time, discount and volatility inputs.
Both one-calendar-day model decay and opening-to-closing time roll are required,
with the former initially selected in the UI. A one-day diagnostic does not
silently stand for a multi-day P&L component; projected lifecycle/cash effects
remain distinct from actual cash and the full reconciliation target. The initial
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
closing prices and their change. A factor with no exact closing price has
neither a closing price nor a change, and says why. Factors read one curve
after the other, each by delivery period. Each currency total carries its
reconciled values, its bridge, what each factor's delta and gamma explain by
themselves, and its Greek coverage. A bridge step names its kind: an opening
Greek, the residual, period cash or an adjustment. What opening Greeks explain
is an amount, or nothing when none of the estimates is available, for a
currency and for each holding. Holdings read by trade reference, then
instrument reference; ledger cash of a trade held at neither close has no
reference and comes last.

The page compares two closes of the selected Risk scope. Its pickers list the
closes the header lists, named as the header names them. Until the person
chooses, they show the header's close and what came before it: the close before
it of its own close convention, where it has one and such a close exists, and
otherwise the frame before it of any kind. Both are visible and can be changed
before Run, and the request names both closes explicitly. When the header names
no close, what the page offered is withdrawn; closes the person chose stay. The
knowledge cutoff reads as local time: untouched, it is the exact instant of the
Risk scope; edited, the backend resolves it. Whenever the Risk scope is read
again or changed, the result and the cutoff are cleared and the cutoff is
refilled from the scope.

A result opens with the economic P&L, the period and the scope. What moved lists
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
phone and desk widths. The full-application suite checks that the pickers list
the header's closes and follow a header close changed while the page is open.
Captured reports retain the engine fingerprint, selected valuation policy, exact
endpoint holdings and discount inputs, unit declarations and Greek estimates,
units, convergence tolerances and sample identities.
The populated upgrade check books two native options before migration 0250 and
proves that their original terms and membership coordinates survive, with separate
owners of a shared premium unit and no inferred payment facts.
Both time modes consume the shared clock layer; unsupported portfolio members
produce an unavailable whole-scope time diagnostic, never an invented zero.
Report format `trading_pnl_report_v2` carries the bridge and Greek coverage with
each currency total. The disposable check passes one saved report over ten option
trades on ten underlying futures months, with no cross-factor repricing, and
the refusal, with its count, of a book that also holds a future on each listed
family. Central
gamma at the one-tick bump converged for none of those ten tree-priced options:
the report shows that effect without an amount and leaves it in the residual. A
bump policy that makes their curvature explainable is undecided. A factor without
a closing price is covered by the calculation checks alone; the synthetic
datasets have no such close.

Recorded currency payments have an independent complete known ledger, including
facts whose owner is not held at an endpoint. Complete accounting capability is
required across the selected scope; unsupported futures variation margin,
exercise transfers and other lifecycle gaps refuse a full report. General
source composition, non-price worker requests, executable surfaces and complete
non-price attribution remain later scope. This is not a production qualification.
