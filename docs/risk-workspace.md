# Risk results workspace

D289 delivers the first report-workspace increment over the existing shared
market-factor and instrument-PV calculation path. It adds no statistical engine,
financial policy, permission, storage table or migration.

Risk owns its book/portfolio selection and run editor. The header selects the
market close and financial context. Empty book and portfolio selections explicitly
mean all readable books; selecting both uses the same intersection as the Blotter.
An unavailable scope cannot silently become an all-books run. Missing market
selection prevents new calculations while retained reports remain independently
readable under current authority.

D292 adds **Portfolio → Book → Trades** selection. A portfolio groups books;
a book holds trades. The trade list follows the chosen parents and retained
context, grouped by book with trade reference and product labels. Choose
**All trades in these books** or **Choose individual trades** (up to 256).
The list pages at 100; selected trades remain visible across pages. An individual
selection includes every held instrument binding of each trade, including an
option's separate premium. Empty individual selection disables Run; it never
means all trades. Changing a parent clears the individual choices and requires
a new choice. Unreadable identities are shown unavailable, rather than omitted
or replaced with invented trade references. Where identity itself is unauthorized,
the directory withholds it and marks the whole-book scope unavailable; authorized
individual choices remain usable.

The editor is the same component used in the Blotter. It offers the existing
held-contract, parallel-curve and custom up/down shocks, with explicit sizes and
optional nested stresses. D295 adds named joint scenarios and selected Greeks
with explicit mixed pairs and half/nominal/double agreement tolerances. All sizes
and tolerances require an explicit choice. Parent stresses remain visible for
custom and joint modes. It retains the backend catalog's conventions, numerical
policy and complete captured context. The owning source is revalidated before
every submission. A market/context replacement invalidates the catalog.

## Reports and graphics

Risk opens a recent completed run matching the chosen scope and market close
when one is available. Candidate selection requires every unique part, matching
scope/context and completed state. The retained report's exact snapshot is checked
before automatic selection. An older or unrelated report is never substituted
as a current calculation. The directory is bounded; it is not a complete archive.
Matching includes the exact trade subset. A retained subset never becomes a
whole-book report; its caption states its captured selection independently of
the current filters. Multipart display and export also require matching subsets.

The shared report widget appears in Risk and in the Blotter's **Full risk report
and graphics** section. It presents backend delta, diagonal gamma and up/down
P&L as sensitivity ladders or heatmaps, with accessible exact report data and
factor-to-position/side-binding drill-through. Units, valuation measures,
currencies and market-factor/computed-output coordinates remain separate.
Independent shocks overlap and have no browser-computed portfolio total.

Graphics show at most 64 factors and 16 book series at once. Tables page at
100 rows. These are labeled display windows over the retained report, not changes
to its financial scope or coverage. Selecting a metric, native unit or window
does not run a new financial calculation. Plot coordinates are approximate;
tooltips and tables preserve backend exact and display strings.

Coverage appears beside the report: excluded positions and reasons, missing or
failed parts, running/cancellation states, and unavailable results. Missing
exclusion evidence or inconsistent context, snapshot, scenario or factor identity
refuses display. A missing scope prevents a complete-coverage claim. An absent
part never becomes a zero result. Work counters and worker configuration notes
live in expandable diagnostics.

D295 uses the same widget for named scenario loss/P&L ladders and book heatmaps,
and selected delta/gamma/cross-gamma graphics. Exact tables retain all three step
estimates, native sensitivity units, requested tolerances and backend agreement.
Selected scenario/pair detail shows the underlying trade/instrument bindings.
Each joint run is one complete report; missing sample or binding receipts refuse
display. Native measures and currencies remain separate.

## Retention and recovery

**Run now** is an interactive result for the current visit. **Queue as job** uses
the existing durable, idempotent operation and worker. Queuing does not provision
a worker. Every part's request identity is allocated and retained before the first
POST. Unknown submissions survive a reload in actor/workspace-scoped session
storage; only pending requests and job metadata are kept there, not report bodies.
Storage failures keep the in-memory request and show an explicit warning to keep
the page open and reconcile it. They do not claim successful reload recovery.

Recovery reauthorizes and rereads known jobs, reconciles unknown request IDs, and
can resume unsent parts through the same original run/ordinal identities. A retry
does not create a second run or replace an unconfirmed identity. Explicitly giving
up setup edits does not discard unconfirmed writes. Account/context generation
checks prevent delayed responses from populating a different view.

Retained links use `#risk/job/<exact job ID>`, including the exchange selector in
links from a popped-out Blotter. They open that job and its listed run siblings
under their captured context. Missing siblings remain visible. A link grants no
authority and does not share another actor's private report. Header/scope
differences are labeled, and changing the embedded retained-job selection updates
the displayed address. Exports reread every part under current authority.

## Remaining work

The [joint-scenario and selected mixed-derivative operation](joint-scenario-risk.md)
is integrated through admission, retained workers and graphical reports (D291,
D295). Migration 0172 needs a separately qualified schema release.
The [aligned factor history panel](risk-factor-history.md) (D296) prepares the
shared selected scope's exact common changes, shows coverage and rejected
intervals, graphs one dated factor and exports a reauthorized manifest. All history
choices are explicit; insufficient history is unavailable. This read/export panel
does not add a durable report archive or statistical worker.
[Parametric VaR/ES](parametric-risk.md) (D299) now uses that history with explicit
native finite differences, sample covariance and drift/confidence/tail policies.
Risk shares Gaussian density, signed book/factor contributions, in-sample linearized
losses and correlation/covariance graphics, plus a paged archive of completed statistical
reports. Unavailable scope/history or unstable deltas retain diagnostics without
risk estimates. Accelerated hybrid execution, EVT and rolling
backtests remain subsequent increments. No production throughput is claimed.

## Guided parametric setup (D301)

The primary **Parametric VaR** action opens the shared selected scope with visible
editable starting settings: 99% confidence, six months, one business-day horizon,
zero drift and 60 minimum common changes. A sole business calendar is selected
automatically. **Scenarios and Greeks** opens the existing deterministic editor.

**Run VaR** prepares exact end-of-day price history automatically;
**Preview price coverage** is optional. Official daily closes are selected
initially. Permitted alternatives are ordered explicitly, remain on the pinned
source, and appear beside missing-official diagnostics in the colored factor grid.
Actual observation timestamps and changes between unlike price types remain
visible. The report offers correlation or covariance with exact native units.

Advanced controls expose drift, the minimum history and finite-difference
settings. All settings are sent and retained explicitly; API missing-field
validation, insufficient-coverage refusal and uncertain-command recovery remain
unchanged. See [the walkthrough and sampling contract](risk-factor-history.md#guided-var-setup-d301).

D305 adds [full-PV historical VaR/ES](historical-risk.md) through this same captured
scope/history/PV and retained-worker infrastructure. Guided setup selects the
method, automatically aligns history, and optionally compares the identical study
to sample-mean delta-normal. Historical reports graph empirical probabilities and
losses, expose book/trade quantile/tail allocation and drill through every sample's
factor moves and native PV bindings. The completed archive discovers statistical methods;
current authority is checked for report reads and export. Rates, time and current
option calibration remain frozen. Historical repricing does not imply accelerated hybrid execution, EVT or an operating
backtest workflow.

## Full-PV Monte Carlo (D307)

VaR setup also offers [Monte Carlo](monte-carlo-risk.md), sharing automatic EOD
history, explicit price-type priorities and the native report/archive workflow.
Visible path/seed controls and selected drift accompany full-PV loss distribution,
convergence, conditional sampling bounds, book/trade allocations, calibration
matrices and indexed path/holding drilldown. Scope-aware starting paths include
base, native result, option and PPA work. Reauthorized exports retain complete
shocks and exact PV evidence; the shared Blotter renderer keeps statistical
reports separate from factor up/down cells. Production schema rollout is separate.

## PCA / hybrid qualification (D308)

The optional [Monte Carlo analysis](pca-hybrid-risk.md) displays standardized
loadings, explained market variance, ideal residual/cross covariance and
reconstruction diagnostics. All native factor delta exposure remains, including
low-variance residuals. Actual quantized PC vectors define selected diagonal and
mixed curvature; every generated path still receives full PV. Comparison widgets
show native VaR/ES, path/tail error, whole-valuation fallback and measured reference
versus added fitting work. A failed shared fitting batch falls back every valuation
with its reason. This mode qualifies only the captured run and adds work; it does
not enable accelerated execution or certify future tails.
