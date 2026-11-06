# Page digests and presentation walkthroughs

D312–D314 are implemented in the bounded contract below; D315 is deferred. This extends the existing
[workspace assistant](assistant-workspace.md); it adds no independent agent loop.
The current runtime is `turns_v17` (D335); deployment requires matching schema,
API and browser assets. A runtime increment alone does not qualify a hosted release.
Migration 0194 is consumed by digest admission, tool consent and Looking at;
0195 adds walkthrough admission and the stored event. Neither adds a table.
Migration 0196 guards admission and walkthrough storage for workspace page handles;
older v13 binaries cannot admit new work. Migration 0198 extends the identity-only
report catalog and requires v15 at admission and stored-plan publication.
Existing versions and grants are unchanged. Migration 0203 guards v17 admission, plan storage and closed swap fields against reviewed function fingerprints.

## Page digest

An administrator enables `read_page_digest` on a new agent version. The user must
grant a version that includes the new tool; an older workspace grant does not
cover it. Existing versions and grants are not changed. Looking at announces
the digest only for an enabled chosen version. Fixed instructions require it
as the first read on page questions. Page prompts cannot override this rule.

The tool requires `mode: "overview"` for the numerical digest or `mode: "controls"` for screen assistance. Controls returns current authorized page/entry metadata and page handles without portfolio valuation, trade handles or pivot evidence. It retains the turn context and publication/source rechecks; date-only facility turns gain no entry navigation. Overview reads the admitted page report at the turn's
exact context/frame/cutoff, under workspace consent and the person's current
operation permissions. Projected reads use the same managed operations as pages,
on assistant pools with the existing read deadline and authorization fence.
Contributing permissions/catalogs join publication and replay source checks.

Blotter reports may carry a separate `view` containing mode, book/portfolio ids,
groupBy, shown measures and an explicitly selected primaryMeasure, or null.
The ordinary selection still names the docked trade/revision or the table view.
Duplicated table scope/grouping must agree. Primary measure follows an explicitly
selected numeric sort in trade rows, or the first selected pivot measure; without
one, the digest asks for a choice instead of assuming a financial measure.

One ordinary Blotter valuation supplies instrument counts, valued/unavailable
status, shared totals by book and pivot grouping, then the three largest and
smallest positions per comparable native partition. Exact values determine rank;
trade/binding ids break ties. Display strings use the managed page formatter.
Totals retain instrument, currency and quantity partitions. Returned navigation
handles identify server-read trades, not model-composed destinations. Docked
details are read at their requested revision independently of table scope.

Curves, saved custom curves and jobs reuse their typed readers; Risk uses the
selected classified operation or directory. Current operational records remain
labelled with their read instant and refuse historical substitution. Other pages
report page/selection only, without claiming access to values they cannot read.

Successful digest text is at most 4 KiB UTF-8 and its result must also fit the
turn's admitted row/byte bounds (baseline 500 records/64 KiB). Underlying failures
retain their reason. Oversized groups, sources or text refuse whole; no successful
partial report is created. This bounds transport, not calculation latency.

Unsaved tickets, close-out drafts and uncertain browser requests are not server
facts. Their counts remain explicitly unavailable. `BlotterFreshness` is an
opaque equality token, not a freshness classification. The digest describes its
own pinned server valuation and source unavailability; it does not infer what a
browser cache or unsent form contains. No draft store is added.

## Selection asks

Blotter reports carry a separate focus: at most 64 distinct trade ids or one
pivot cell's native group identities and measure. The shared backend grouping for selectable, quantity-partitioned pivots
issues an opaque group id including its native price partition; no cell amounts
or labels enter the ask. Existing money-summary shapes remain unchanged. Selected positions and pivot totals are resolved again
at the exact turn pin. Unavailable or ambiguous selections refuse whole.

Ctrl/Command-click, Shift-click and keyboard selection identify trade rows.
The context menu offers Ask about these positions; pivot cells and docked
positions also open an editable, unsent question. Looking at states the focus.
Pop-out reports use the same strict pointer contract. Async row-open completions
cannot replace a newer selection; a non-value pivot cell clears its focus.

## Presentation walkthroughs

An administrator must select `present_walkthrough` on a version and the person
must grant that version. An older workspace or digest grant cannot cover the
new tool. The tool takes `planJson`, an array of one to twelve steps; the entire
plan validates before publication. Fixed instructions require walkthroughs for
requests to show, find, explain or do something on screen when enabled.

A successful `read_workspace_page` with available authorized operations returns
its own `target` handle and fixed `targetLabel`. The target names that page with
no selection. Handles join the same turn-local sequence as digest and legacy
read targets, only after complete-result bounds and step publication succeed.
An empty or refused page read issues none.

The managed catalog admits `go`, `select`, `sort`, `filter`, `open`, `scroll_to`,
`fill`, `say` and `pause`. Every named page must have a successful read in this
turn. Go resolves only a handle this turn issued; trade selections resolve issued
trade identities. A pivot selection requires the exact focus a successful digest
resolved on the server; a browser pointer or directory read is insufficient.
Narration uses short server templates, which introduce no calculated values.
No step names a selector, URL, code, Book, Save, job control or Authorize.

Blotter adapters use native presentation operations for row selection, column
sort/search/filter, details/charts, known widgets and unsaved futures/swap tickets.
Opening the ticket leaves book, contract, lots and price unchosen.
A `go` to an issued New trade, New futures trade or New swap page handle opens
the native product chooser or ticket. Ordinary `navigate` uses the same route.
The native catalog must allow booking; missing contexts and retained requests
refuse without changing to working data. Walkthroughs stop at unsaved edits;
ordinary Go/Back retains its explicit leave confirmation. Confirmed navigation
opens a fresh ticket without carrying discarded financial terms.
Stop and stale source/generation checks prevent a late catalog from mounting
a swap ticket or replacing a newer form. No price, preview or booking is sent
by opening it. D335 adds bounded explicit swap fills described below; the person chooses remaining
terms and uses the ordinary booking controls.

Entry-page discovery includes closed aliases of the existing native catalog and
booking operations, preserving canonical IDs, permissions, context and approval.
Amendment and settlement commands are excluded from new-ticket discovery. Other pages
support issued navigation, the page widget, narration/pause and Review of a
proposal on that page. An unavailable grid column, pane or widget stops with the
failed ordinal. There is no general editor-field adapter or model-written UI.
Live market navigation must be paused by the person before a walkthrough moves
pages, because the ordinary navigation path can otherwise record a live pause.

Futures filling supports `ticket.lots`, `ticket.executionPrice`, `ticket.memo`.
A futures ask uses this deliberately narrow literal form:

```text
Fill unsaved futures ticket
Set ticket.lots = "2"
Set ticket.executionPrice = "3.4"
Set ticket.memo = "Synthetic review"
```

Each field occurs once; a value must equal its stated JSON string. Numeric text
is at most 64 characters; memo is a plain line of at most 200 characters. Surrounding
prose, quoted/reported instructions, translation requests, duplicate assignments,
missing terms and guessed identities require clarification. The supported whole-ask grammars do not infer missing terms. Typed setters update unsaved state and invalidate
previews without dispatching input/change/blur/key/submit events. The person
chooses remaining terms and books through the ordinary form.

Migration 0195 publishes one immutable `walkthrough` event immediately after its
tool step in the same transaction, with `(turn, step)` identity and a backend
agent label. The existing publication/source fence and D190 owner-read hiding
cover the entire plan. Plans fit a 16 KiB managed budget and ordinary admitted
result bounds; storage also refuses oversized/unknown event vocabulary. Runtime
`turns_v14` covers workspace page target results and the existing tool/report/event
contract. Migration 0196 refuses v13 admission and stored walkthrough writes. Migration 0198
extends identity-only page admission and requires v15 at both boundaries;
previous runtime histories and grants remain unchanged.

Eligible live asks in this tab can start a plan. History, reload, gaps and duplicate
events never start one automatically. Start/Resume rechecks the owner's stored
event; each step and Continue recheck access and context. A labelled second cursor,
narration bubble, keyboard Continue, Stop/Esc and reduced-motion support accompany
native controls. Curves phone sheets retain access to the typed walkthrough
controls while other background content remains inert. Stop also suppresses later automatic plans/navigation from the same
ask; later plans/navigation require explicit Start/Resume/Go. Stop invalidates pending steps and native async form/detail
callbacks. Owner/context changes and competing input stop the run.

Progress is scoped by owner, conversation, turn and sequence. Resume requires the
captured page report and local form generation; any competing input or another
plan invalidates it. Form values never enter the report. Review waits for the
proposal turn to complete; opening it leaves consent unchecked and Authorize
disabled. Only the person's separate authorization or ordinary form submission
can commit a change.

## Keep prerequisite (D315)

No current answer card/table has an exact saved-definition mapping. The answer
renderer consumes prose blocks, without a binding to a successful read's operation
and inputs; `show_card` has no implemented artifact consumer. Existing saved
Blotter views and risk run definitions alone do not establish one. Keep is therefore
not offered or stored, including for accounts without read permission. The next
increment needs source-bound read artifacts and exact, permission-checked mapping
before a Keep proposal can enter Review → Authorize. Unmappable views remain
unkeepable. No generic artifact store, values store or new save endpoint is added.

Outside browser-agent page tools remain unexposed (O29). A browser session is
the person's authority, not an independent agent principal/delegation.

## Verification status

Managed checks assert exact synthetic digest lines, ordered native extrema,
scope/grouping validation, server display precision and whole-result byte refusal.
Workspace checks cover new-tool consent and retained values after correction;
the ordinary runtime suite covers admission/publication/replay. Looking at and
pop-out contracts have presentation checks. Local slice 4.1 checks pass: the workspace suite has 33 integration checks, the
ordinary runtime suite passes, and all 1,319 web checks pass. Independent review
found and corrected scope disagreement, invalid lots grouping, price-statistic
precision, native-unit labels and native docked term shapes. These are bounded
local checks, not a performance qualification.
Slice 4.2 additionally passes 33 workspace integration checks, five owned
application-browser checks, all 1,322 web checks and standalone checks. Its
independent review corrected stale async selection, invalid-cell focus and
native price-partition ambiguity; row-background pivot menus cannot reuse focus.
No production rollout or publication is approved by local implementation.

The completed local v13 check run passes 39 workspace integration checks, twelve
owned application-browser checks, all 1,336 web checks, thirteen orchestration
checks, standalone boundaries and the ordinary assistant runtime suite.
The browser verifies administrator tool choices, exact selected ids, every
presentation kind, visible cursor/narration, unsaved fields, Stop and suppression
of a later plan from the same ask. Presentation checks additionally cover
in-flight cancellation, competing input, deferred Review and the separate human
Authorize click. Independent read-only adversarial review found no remaining
blocking issue after fixes to fill intent, resume scope/form generations, pivot
read provenance, deferred Review and later-event suppression. Review did not
independently rerun the suites. The complete release command in
[verification.md](verification.md#required-tools) remains required before any
release publication; it now includes the dedicated workspace browser gate.
Targeted checks alone do not qualify production activation.


D320 keeps walkthrough Continue/Stop under the runner's own control while a native
page read is busy. Initial control-refresh errors in main actions, live updates
and pop-out actions release busy ownership through their normal error paths;
they never retry a command. Owned browser regressions qualify recovery from that
failure path without attributing a production input lock to a specific exception.


## Identity-only page admission and guide controls (D325–D326)

Dashboard, Crude delivery setup and Vol surfaces report their page identity and an
empty selection. Their asks now pass both the managed and SQL closed catalogs,
pin page guidance and use the normal As of / retained-context requirements.
Facilities also regains SQL prompt admission for its existing empty report.
The digest returns identity, context and empty selection only. These pages have
no assistant reader, navigation target or newly exposed operation. Gas delivery
setup also has its missing Looking at label. Browser widget contents never enter
these reports. Catalog parity is checked from native PageState reports through
the server prompt list and SQL whitelist.

D335 removes the floating guide and its Minimize/Close controls. Continue, Stop
and status are inline in the assistant panel. The cursor tracks the actual native
control through layout and scroll changes, hides while offscreen or occluded,
and stops if a tracked control disappears unexpectedly. Intended transitions
retire the old control before awaiting native reads. Navigation to a native
ticket points at a visible enabled field rather than the page container. Glow
and motion respect reduced motion. Stop, panel close, replacement and teardown
release the target outline and tracking callbacks.

Native modal Review dialogs host the guide's presentation controls in their top
layer, keeping them reachable while the surrounding page stays inert. The guide
never checks consent, closes Review, submits a form or authorizes a proposal.


## Native cursor and entry assistance (D335)

The floating narration box has been removed. Status, Stop and Continue are inline
in the assistant panel; only that dedicated controls root moves into a native
modal. Closing the panel stops and suppresses later automatic presentation for
that ask. The cursor is a pointer-events-free manual popover in the top layer,
so dialog transforms do not change its viewport coordinates. It aims before a
typed native action, releases its old anchor during navigation and briefly
retains the final target. Reduced motion removes animation. No generic DOM click,
selector, key press or submit is accepted.

New swap opens through the ordinary authorized trading directory and swap catalog
without a full Blotter valuation. Controls mode already reads the entry metadata
needed for a walkthrough go; it needs no additional broad operation discovery.
Financial explanations still use overview and its ordinary pinned reads. Model
round trips and native catalog reads still take time; no model prefetch is added.

Swap fields are `swap.structure`, `swap.quantity`, `swap.fixedPrice`,
`swap.fixedDirection`, `swap.priceMode` and `swap.memo`. Only agreed pricing is
admitted. Example complete ask:

```text
Fill the swap ticket with structure fixed for floating, price mode agreed, quantity 1000 and fixed price 3.4
```

The equivalent literal form starts `Fill unsaved swap ticket` and uses unique
`Set swap.FIELD = "value"` lines. A fixed-price plan must first fill the explicitly
stated fixed-for-floating structure and agreed mode. The person chooses all
other missing terms and performs Preview/Book themselves; consent and Authorize
retain their separate human actions. Unsupported references, dates, par policies
or ambiguous asks require clarification. Native setters affect unsaved state
only, invalidate prior preview and contribute a local revision to Resume checks.


D336 keeps publication separate from execution: returning a walkthrough plan
confirms only that it was retained. Fixed instructions describe intended steps,
without claiming that native reads or controls have completed. The browser's
inline status reports completion or the first failed step. Native swap catalog
reads share the [bounded exact observation reader](exact-observation-reads.md);
no tool/event shape or runtime version changes.
