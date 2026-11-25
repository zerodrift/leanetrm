# Curve volatility and correlation workspace

Status: approved design; implementation and qualification pending.

## Contract

The Markets workspace edits the existing `curve_marks_log_return_held_v1`
facts. It has volatility and correlation desk grids, focused monthly strips,
exact change analytics, revision history and atomic publication. It does not
change listed-option surfaces, heat-rate-call spread marks or gas pricing.
There is no withdrawal, inferred mark, interpolation, covariance fitting or
positive-semidefinite admission rule. These are independently supplied pairs.

Volatility is entered as a decimal fraction (`0.50` means 50% annualized
volatility), from zero to ten. Correlation is from minus one to one. Both keep
at most twelve fractional places. Months retain the existing 1901–2199 range.
Missing is distinct from a recorded zero. All numerical validation,
normalization, differences and coverage calculations belong in managed code.
TypeScript presents exact strings; approximate numbers are only chart coordinates.

## Workspace

One Markets page, **Volatility & correlation**, uses existing theme, typography,
tables and charts. A compact context bar states exact source, business instant,
knowledge cutoff and current/historical state. Explicit curve and month choices
produce a volatility grid of at most 20 curves by 12 months, or a correlation
matrix of at most 20 curves for one month. A row/pair opens a vertical strip of
at most 240 months, one month per row and one editable value column. Each view
uses one exact definition/reference source. Correlation sources also share the
existing currency scope.

The correlation upper triangle is editable and the lower mirrors it. The
diagonal says **Same curve; no mark**, never generating a stored value. A right
inspector shows the selected term structure, exact values and revision history.
On narrow displays it becomes a drawer; only the grid scrolls horizontally.
Headers and draft actions remain visible. Values align by column. Published,
edited, invalid, missing and pending states differ in text/shape as well as colour.
Use restrained motion with reduced-motion support and no new UI dependency.

Published term-structure lines are solid and validated draft lines dashed;
missing months remain gaps. The correlation heatmap has a fixed -1 to +1 scale
and a visible legend. Tooltips retain exact values, curve/pair, month, revision
and convention. The backend reports exact draft-minus-selected differences;
a missing predecessor says **New mark**. Coverage counts marked, missing,
changed and invalid cells in the selected scope. History contains revision,
predecessor, actor, reason, value, effective time and knowledge time. Charts
never show an invalid or unvalidated draft as a valid mark.

Tab/Shift-Tab navigate cells, Enter commits the edit and moves down, Escape
cancels that cell edit. Keyboard shortcuts may open review, never publish.
There is one visible draft. Changes of scope, tab or context offer Review,
Discard or Keep editing. Clearing an input is an invalid draft, not withdrawal;
Reset cell restores its read value.

## Clipboard and review

Plain-text TSV contains values only. Rectangles start at the selected editable
cell; blanks leave the read value or existing draft unchanged. Reject ragged
rows, formulas and out-of-bounds destinations atomically. Ignore one terminal
empty line. Full-matrix paste requires exactly N by N cells; blank or exact one
on the diagonal is ignored, and supplied mirrors must be equal after exact
backend normalization. Show addressed changes before applying a paste. No CSV
files, header guessing, automatic expansion or fill.

A batch has one kind, exact source, effective instant and nonblank reason of
at most 500 characters. It holds at most 240 unique cells and uses policy
`curve_mark_batch_v1`. Reject oversize rather than split it. Review identifies
each key, selected value, proposed value, expected write revision and effective
instant. Any edit invalidates review. One invalid or stale cell rejects the
entire publication and preserves the draft.

## Storage, authority and time

Reuse `trading.volatility_marks`, `trading.correlation_marks` and
`trading.curve_mark_requests`. New atomic batches share the private cell writer
with legacy commands. Keep all legacy shapes, outcomes and replay behavior.
One batch uses one advisory transaction lock, one publication for changed
facts, and one aggregate receipt. An acknowledgement-only batch makes no new
mark publication. Same business key, value and effective instant acknowledges
the original fact even after a correction; it does not restore an older head.
Review and receipts explain that distinction explicitly.

New editor endpoints are separate from trade wrappers. Setup/validation/history/
status require ReadMarketData; review/publication also require ImportObservations
and working mode. No ReadTrades permission is needed. Capture authority deadlines
before waits and recheck sources and current authority at commit. Existing
valuation reads and risk-worker lease restrictions remain unchanged.

Use certified market-only read inputs. Both publication-prefix and known-time
predicates qualify facts; selected values additionally satisfy effective time.
History includes known revisions and flags their effective eligibility. Selected
value and current write head are separate. A later-effective head disables a
backdated correction with an explanation; historical views expose no newer head.
Catalog and per-cell history pages contain at most 50 entries.

Register original certified source coordinates for writes without binding a
historical read context. Store durable server-resolved ReadInputCoordinates in
new batch request metadata; never persist a generic context object. Receipt
recovery reconstructs these coordinates under current authority without an old
session frame. Review executes the writer under a rolled-back savepoint, leaving
no facts or receipt and exposing no temporary IDs. Publication rechecks predecessors.

## Recovery and publication effects

Reuse actor-scoped pending-command transport. Retain the exact command and
operation ID across lost replies and reload. Block replacement publication
until reconciliation. Committed, absent and denied/unavailable are distinct;
denial does not establish that an earlier attempt failed. Reads and replies
are fenced by actor, source, context and view generation.

After success, capture fresh current knowledge for the same business instant
and source, refreshing only the still-matching editor scope. Refresh failure
does not negate the receipt. Other valuations refresh explicitly. Historical
contexts and retained results stay pinned. An end-to-end gas exercise example
must become priced after editor publication and an explicit refresh, with its
existing numerical expectations unchanged.

## Delivery and verification

Use TDD, signed-off local commits, synthetic examples and disposable PostgreSQL.
Keep implementation separate from simultaneous gas imbalance changes. Assign
migration and decision numbers at integration. Independently review authority,
temporal selection, atomicity and recovery. Verify desktop/narrow layouts,
supported themes, maximum grids, long names, error and recovery states. Record
actual qualification, not planned results.
