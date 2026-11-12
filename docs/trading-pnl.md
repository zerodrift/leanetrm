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

Durable results and first-outcome receipts use separate authenticated protection
purposes and private installation keys. Saved read, exact-request retry and
same-fact retry work after restart without the memo. Current authority is always
checked. After a lost response, an authenticated serialized check can establish
that no durable outcome exists; only that definitive outcome releases the page's
uncertain save request when the unsaved result is no longer available.

## Implementation status

The managed accounting calculation, price-factor attribution and their focused
numerical checks are implemented in the working change. Currency cash uses
explicit recorded-payment state, including partial payments and knowledge-time
revisions; focused native operation and historical-read checks have passed.
Two-context report operations, exact-result saving and the Risk UI are implemented
locally. Disposable PostgreSQL 18 checks have passed native run/save/read/restart,
both time modes for a continuing option, new-and-paid holdings, idempotent retries,
tamper and cross-catalogue refusal, and exact reference-access revocation. The UI has currency
totals, a managed-value waterfall, position detail, explicit close/knowledge/time
selection and optional saved reports. Browser checks cover run-before-save,
exact retry after remount, definitive-unsaved recovery and late response fencing.
Captured reports retain the engine fingerprint, selected valuation policy, exact
endpoint holdings and discount inputs, unit declarations and Greek estimates,
units, convergence tolerances and sample identities.
The populated upgrade check books two native options before migration 0250 and
proves that their original terms and membership coordinates survive, with separate
owners of a shared premium unit and no inferred payment facts.
Both time modes consume the shared clock layer; unsupported portfolio members
produce an unavailable whole-scope time diagnostic, never an invented zero.

Recorded currency payments have an independent complete known ledger, including
facts whose owner is not held at an endpoint. Complete accounting capability is
required across the selected scope; unsupported futures variation margin,
exercise transfers and other lifecycle gaps refuse a full report. General
source composition, non-price worker requests, executable surfaces and complete
non-price attribution remain later scope. This is not a production qualification.
