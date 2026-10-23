# Model break-even PPA pricing

The bounded draft operation `POST /api/ppa/break-even` solves one explicitly
selected flat strike-payment membership of a fixed physical or virtual
fixed-for-floating PPA. It uses the existing instrument, side and schedule engine.
It does not book or amend a trade and needs no persisted proposed/executed state.

The request supplies the existing exact terms, delivery coverage, contract MW,
market snapshot, knowledge cutoff, pending-actuals policy, discounting choice,
valuation measure, selected membership, quote precision and rounding policy.
Two immutable request-local instrument variants retain all other inputs and
resolve the strike at zero and one. Exact rational arithmetic gives
`strike = -value(0) / (value(1) - value(0))`. Discount factors are resolved once
and frozen across both evaluations. No iterative tolerance or commercial risk
premium enters the result.

The response reports the exact root and units, sensitivity at the requested MW,
selected measure, original draft value, explicitly rounded half-even quote and
its exact residual. Source prices support 18 decimals and magnitude below 10^24;
enterability at source precision is separate from exactness at the selected
quote precision. Unavailable coverage, unsupported terms, incompatible revisions,
missing explicit inputs and zero sensitivity refuse the entire result.

`ValueExcludingOverdue` retains the accepted undue-PV plus holdings measure,
excluding overdue cash and delivered physical value. `CompletePv` refuses when
any overdue cash is present. Exclusions remain visible. The receipt retains the
request, exact definitions, every basis membership identity, market observations,
source revisions, temporal contexts, discount inputs and factors, model and
numerical versions. Basis side IDs are request-local and do not redefine stored
sides.

Current scoped ReadTrades, market and reference authority is rechecked through
the existing PPA operation. The request shares the existing two-million evaluated
row budget (both bases counted), 8 MiB response limit and 20-second deadline.
These are refusal ceilings, not completion promises for every workload.

## Solver/API evidence before graphical work

The first slice passes 34 managed independent numerical assertions and 30 HTTP
checks. Cases include physical and virtual contracts, reversed direction,
distinct payment-date discount factors, fractional and negative roots, signed
half-even quotes, ordinary-preview residual comparison, zero sensitivity,
missing inputs/coverage, later-known corrections, old-cutoff replay, withdrawals,
current authority loss and unchanged trade-event counts. An independent reviewer
reran these checks in a fresh disposable PostgreSQL database, along with the
existing 40 finality, 32 PPA and 34 settlement-preview checks: all passed.
Two review findings were fixed and rechecked: the advertised deadline and complete
membership-to-discount-factor mapping in the receipt. No blocking finding remains.
Full-gate and graphical qualification are separate from this first-slice evidence.

## MVP boundary

Settlement calculations, draft pricing and the graphical workbench comprise the
current MVP. Settlement approvals, accepted-cash accounting, payments, reversals
and automated correction adjustments are deferred. Previously specified SP1–SP9
lifecycle conventions remain retained future contracts, not a new approval
workflow or prerequisite for pricing. Trade amendments change contractual terms;
market and meter corrections remain versioned source data. Global settlement
phase completion is not claimed.

## Graphical workbench

The existing PPA editor now provides **Price an unbooked draft** and an explicit
model-pricing action. The draft path requires neither booking parties nor
BookTrade. Booking and amendment still use their existing reviewed operations;
pricing never supplies a trade ID or calls those writes. Existing composition,
quantity, payment and component controls continue to author the same backend
terms contract. One explicitly selected strike, measure, quote precision and
rounding policy must be chosen. Unsupported compositions remain available to
ordinary valuation but cannot invoke this solver.

The same response includes server-owned graphical values derived exactly from
its two basis schedule summaries at the entered strike. Groups retain side,
original operating month, quantity state, economic class, coverage and payment
or value date. The browser does not net legs or aggregate financial values.

- Monthly energy volumes are displayed separately for each side. Non-energy
  quantities, including optional certificates, retain their own units.
- Market and strike profiles show effective quantity-weighted values:
  signed gross amount divided by quantity and membership coefficient. They
  include declared adders and multipliers, and use currency per quantity unit
  with multiplier one. Zero quantity leaves the implied price unavailable.
- Gross contractual receipts/payments and gross physical energy value have
  separate charts. Exact PV, holdings and overdue classifications remain in
  drill-down and the input/exclusion receipt.
- The selected contract value versus strike uses exact server points at the
  root, root minus/plus one quote unit, and entered strike. The model root is
  marked explicitly. The exact zero point is distinct from a rounded quote's
  potentially nonzero residual.

Forecast, metered, contractual and estimated quantities retain explicit labels.
Economic classifications do not claim price-source finality. Native preview
inspection retains actual, projected, estimated and modeled price evidence.
These model profiles are not settlement acceptance or payment evidence.

Terms, knowledge, market context or pending-actuals changes clear graphical
results and receipts. Each asynchronous pricing request retains a generation;
late successes and errors cannot restore evidence after invalidation. Exact
fractions remain in tables and point descriptions; approximate browser numbers
only position SVG coordinates. Nonfinite plot coordinates leave gaps without
converting missing values to zero. The summary table wraps at phone width.

Additional independent numerical assertions cover the monthly 57,680 MWh
physical example, gross physical value 2,422,560, strike payments -2,307,200,
weighted market/strike prices 42/40, the zero-valued root and its exact slope.
An optional-certificate example retains its own quantity units and changes the
contract root to 44 while holding certificate terms fixed. The managed pricing
suite has 43 passing assertions. Frontend tests cover exact value retention,
zero-versus-missing plot data, oversized rational coordinates and finite extreme
spans. Review found and resolved plotting-range edge cases before qualification.

The enlarged-fixture browser suite passes 39 checks, including physical/virtual
pricing, exact mobile root/quote display, term invalidation, a deliberately delayed
successful response after context change, read-only draft access without booking
parties, existing amendment/termination behavior and no uncaught browser errors.
Independent final source and visual review has no open blocking finding. All 32
pricing HTTP checks, 40 finality checks and 34 hourly settlement-preview checks pass.
The separate quarter-hour preview passes 5 checks for 2,884 native intervals.
The independent settlement specification retains 36 passing cases.

All 45 gate stages are qualified on the unchanged source tree. The runner finished
in 1,117.645 seconds; an interrupted recovery stage was explicitly excluded from
that result and rerun in a fresh isolated database. The complete recovery rerun
passed in 605.863 seconds. Its real replacement risk calculation ran for 338.615
seconds, retained original admission inputs/deadline, survived lost commit
acknowledgement and published exactly one complete report. The independent
base value was 40,408,960, with 721 scenarios, 316,057,560 evaluated rows and
120 quantity preparations. Positive completion evidence, not process exit alone,
was required for that stage.

All 690 snapshotted non-document files remained unchanged during qualification.
The gate removed its 23 owned preview databases; separate recovery and quarter-hour
runs also removed theirs. The environment was macOS 26.6.2 arm64, stock
PostgreSQL 16.14, .NET 10.0.402 and Node 24.22.0 with the installed Chromium browser.
No new migration or settlement lifecycle operation was added. This is historical qualification evidence; current integrated testing is recorded
in the experimental checkpoint PR.

Remaining scope: one selected flat strike only; exact admitted fixed physical and
virtual fixed-for-floating compositions; existing source coverage, revision and
work limits. Monthly weighted profiles do not replace native price evidence.
This qualification does not promise a pricing runtime for every admitted term,
expand settlement coverage, or introduce persisted proposed/executed trade states.
