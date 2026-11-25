# Curve Marks Editor

**Markets → Volatility & correlation** reads and publishes monthly market marks under
`curve_marks_log_return_held_v1`. Choose the exact definition and reference
source, business instant, curves and months. The context bar displays the
source, business instant, certified knowledge cutoff and working or historical
mode.

## Views and exact values

| View | Scope |
| --- | --- |
| Volatility grid | Up to 20 curves by 12 inclusive delivery months |
| Correlation matrix | Up to 20 curves for one delivery month |
| Monthly strip | One curve or pair across up to 240 inclusive delivery months |

Volatility is an annualized decimal fraction from zero to ten. Entering
`0.50` means 50% annualized volatility and is displayed as `0.5`.
Correlation is from −1 to +1. Both use exact decimal strings with at most
12 fractional places. Values are validated and normalized in managed code.
Supported delivery months are January 1901 through December 2199. Sources use
outright curves with a unit quote multiplier. A correlation key is an unordered
pair of distinct curves under one exact source and a common quote currency.

The upper triangle holds editable marks and the lower triangle mirrors them.
The diagonal says **Same curve; no mark**. The inspector shows the selected
curve or pair, its term structure and its revision history. On narrow screens
the inspector opens as a drawer and the grid scrolls within the page. Published
term lines are solid. Validated draft lines are dashed. Missing months remain
gaps. The correlation heatmap uses a fixed −1 to +1 scale. Tooltips preserve
exact values and revision information. Only chart coordinates are approximate.

The managed response counts marked and missing keys at the selected cut, and
changed and invalid draft cells. A recorded zero counts as marked. Valid
differences are exact draft value minus selected value. A draft for a missing
key is labeled **New mark**. **Reset cell** restores the read value. Clearing
an input leaves an invalid edit.

## Editing and clipboard

Tab and Shift-Tab move between editable cells. Enter commits the cell edit and
moves down. Escape cancels the edit. A single visible draft follows the selected
scope. Changing the scope, page or workspace context with an open draft offers
review, discard or continued editing. Publication requires an explicit button.

Paste plain-text TSV at an editable cell to address a rectangle of values. A
blank preserves the cell's existing value or draft. Ragged rows, formulas and
values outside the editable area reject the paste as a whole. One terminal
line ending is allowed. The full-matrix option takes exactly one row and column
per selected curve. A blank or exact one on the diagonal is ignored. If both
mirrors contain values, their exact normalized values must agree. The editor
shows the addressed keys and values before applying the paste to the draft.

## Review, publication and recovery

A command under `curve_mark_batch_v1` specifies one kind, one exact source,
one effective instant, a nonblank reason of at most 500 characters and 1–240
unique cells. Each cell supplies the expected write revision and a proposed
value. Review shows the selected value, proposed value, expected write
revision, projected outcome and effective instant. Editing the draft or reason
invalidates review. The server runs the same writer in a rolled-back savepoint
for review and checks the expected predecessors again on publication.

Publication commits a batch atomically. A changed batch appends one trading
publication. Every successful batch returns one immutable receipt. Retrying
the same operation with the same canonical command returns its original result.
A different command using that operation ID is refused. Submitting an
identical fact acknowledges its original revision, even when a correction
has since advanced the head. If a `0.5` mark was corrected to `0.6` at the
same effective instant, acknowledging the `0.5` reports its original revision
separately from the `0.6` head at publication. An acknowledgement-only batch
adds no mark publication. An invalid cell or stale predecessor for a changed fact
refuses the whole batch and preserves the draft.

Before sending a publication, the browser verifies it can restore the exact
command and captured read proof from actor-scoped session storage. If a reply is
lost, the pending panel can retry the identical command or check the original
operation. A confirmed receipt resolves the operation. A confirmed absence can
be dismissed or retried. Denial or unavailability leaves the operation pending
and blocks replacement. Recovery after session renewal reconstructs the stored,
server-certified source coordinates under current authority.

On success, the editor refreshes knowledge for the matching working scope and
the same business instant and source. The receipt remains visible if the refresh
fails. Valuation and risk inputs refresh explicitly. Historical views and
retained results keep their original cuts.

## API and temporal contract

The workspace sends JSON POST requests to `/api/curve-marks/editor/` with the
following operation suffixes. Read and write authority is scoped to the current
workspace and exact source. The editor uses market permission independently of
trade access. See the [security boundary](security.md#curve-mark-editor-boundary).

| Operation | Request and result |
| --- | --- |
| `setup` | Exact source and context, search and page, optional view. Returns owner, eligible choices, limits and optional layout and selected cell state. |
| `validate` | Same source, context and view, current edits and optional paste. Returns normalized draft, coverage and addressed changes. |
| `history` | Source, context, kind, key and page. Returns value, revision, predecessor, actor, reason, effective time, knowledge time and effective eligibility. |
| `review` | Exact batch. Returns selected and proposed values, expected revisions and projected change or acknowledgement. |
| `record` | Exact batch. Returns accepted facts and the head for each key at publication. |
| `status` | Original owner, operation and kind. Returns `committed` with its receipt, `absent`, or an authority or availability error. |

The source selects the exact definition revision, reference catalog and
reference revision. A request context carries its certified context ID,
business date, explicit instant and optional captured snapshot. A read
registers the working cut or binds the historical cut. A selected fact must be
inside the certified publication prefix and known at the cutoff, and must have
taken effect by the business instant. History also includes known revisions
whose effective instant lies later than the selected business instant. Catalog
and per-key history pages contain at most 50 entries.

A writable view supplies the known chain head separately from the selected
value. A later-effective head blocks a backdated correction. Historical and
read-only views omit the write head. The writer checks the actual predecessor
under the publication lock. Exact mark values are supplied independently.
Withdrawal, interpolation, covariance fitting and positive-semidefinite
admission are outside this contract. Listed-option surfaces and heat-rate-call
spread marks keep their separate authoring contracts.

[Decision D453](decisions.md#d453--curve-mark-publication-is-atomic-and-market-scoped) ·
[Verification](verification.md#curve-marks-editor-d453)
