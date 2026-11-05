# Page digests and visible walkthroughs

Status: approved for local implementation; per-slice qualification is recorded in
[the implementation contract](assistant-walkthroughs.md). Baseline `2fdcd927`: `turns_v12`, D311;
D312–D315 record the approved slices. Extend the [existing assistant](assistant-workspace.md)
and `TurnWorker` loop. Backend evidence/plans/values; typed browser presentation.
No generated pages/code or model-call prefetch.

## D312 — Server page digest (slice 4.1)

Add `read_page_digest` / `workspace.digest_v1`: read admitted `PageReport` at
`TurnPin`. Preserve mode, book/portfolio ids, grouping, shown/primary measures
alongside docked trade/revision; current reports lose the view when details open.
Looking at announces the digest. Fixed instructions require it first when enabled;
pinned page guidance cannot override them.

Compose deterministic lines through shared authorized page reads, using
`TradeReads`/`TradingBlotter`'s single valuation and summaries before workspace
transport serialization. Return the read envelope and issued handles.
Line order: view/frame/cutoff; instrument/status counts; shown-measure totals by
book and pivot grouping; three highest/lowest positions with reference, book,
instrument, terms and value; docked terms; pending/stale evidence. Preserve native
partitions, rank exact comparable values with trade/binding-id tie breaks, and
use backend `{value, display}` strings. Missing primary measures require a question.

Curves/custom curves/jobs/Risk reuse existing reads for identity, last build/run,
input count and state; elsewhere page/selection only. Label current-only reads;
preserve historical/underlying refusals and contributing source-access rechecks.
Text is at most 4 KiB UTF-8, within admitted 500-record/64-KiB bounds. Oversized
groups/selections refuse whole; omit nothing silently.

**Approval choice:** `BlotterFreshness` is an equality token, not status counts;
unsaved tickets, close-out drafts and uncertain browser requests have no server
records. Mark unavailable, never zero; exact counts need an agreed pointer-backed
consumer. Record the gap in `plan.md`; no draft store.

## D313 — Selection asks (slice 4.2)

Add selected trade ids and pivot-cell group identities independently of the docked
trade. Pivot identity includes native partition/measure, never amounts. Resolve
selected digest lines at the pin, including applicable bindings; invalid or
over-budget selections refuse whole. Add keyboard/pointer multi-selection to the
single-selection grid, **Ask about these positions**, pivot and docked asks.
Prefill the editable composer without sending. Looking at/send-time comparison
and strict pop-out reports carry identical pointers, never table cells.

## D314 — Visible walkthrough (slice 4.3)

Add `present_walkthrough` / `workspace.walkthrough_v1`: at most twelve `go`,
`select`, `sort`, `filter`, `open`, `scroll_to`, `fill`, `say`, `pause` steps.
Backend page catalogs validate the complete plan against the report, successful
reads and issued handles, including page transitions. Adapters resolve ids through
existing presentation handlers; no selectors/automatic saves. Unknown targets,
unread pages and commit controls refuse.

A fill carries field-specific evidence from the current person's exact ask span.
Backend parsing must verify an unambiguous field/value binding, not merely find
that number somewhere; reference choices also require authorized identities.
Ambiguity asks a question. Fill invalidates previews and changes unsaved state
only; numerical narration binds to returned displays. No Book/Save/Authorize step.

Store one immutable `walkthrough` event immediately after its tool step, with
identity `(turn, step)` and D190 publication/replay hiding. Eligible live asks may
start; replay/reload requires explicit Start/Resume and owner replay recheck,
never repeated fills automatically. Continue rechecks access/context. Show the
agent-labelled cursor/narration with keyboard controls and reduced motion.
Stop/Esc cancels pending steps; context/identity changes, edits or missing controls
halt with the failed ordinal. Fixed instructions use walkthroughs for screen
requests. Review waits for turn completion; consent/Authorize and ticket booking
remain human actions.

## D315 — Keep existing definitions only (slice 4.4)

Map source-bound reads exactly to `TableViews`' Blotter definition or
`RiskRunDefinitions`' supported preset/books/unit sizes. Classify newly exposed
saves in `AssistantOperations`; Keep prepares Review → Authorize with stable
turn/step identities and an approval receipt for legacy layout saves. Store
inputs, never result values or execution authority; reopening reads under the
person's context/current permissions; saving never runs risk. Require read
permission and missing terms. Prose tables lack source bindings and `show_card`
is unimplemented: offer no Keep there. Record this prerequisite/unmappable reads
in `plan.md`; no artifact store.

## Compatibility and verification

Require administrator-selected versions and explicit user grants; widen neither
existing versions nor grants. **Proposed rollout:** reserve `turns_v13` for the
complete contract; publish no intermediate v13 binary. Independently installed
slices require another runtime bump for later incompatible shapes. SQL rejects
older binaries. Migration consumers are tool/grant admission and stored events.

Write failing managed/SQL and presentation/browser checks first. Extend
`check_assistant_workspace.py`: exact digest lines/budget/refusals/retained values;
three ids without cells; invalid plans/targets and access-loss replay; each step
presentation-only, fill without submit, Stop, mandatory Authorize; Keep live parity
and denied/unmapped saves. Update assistant docs,
`frontend-wiring.md`, `decisions.md` and deferred work in `plan.md` per slice.
Run `check_standalone.py`, both preview workspace/runtime suites and web tests;
add browser checks for 4.2–4.4 with explicit PostgreSQL/.NET/browser binaries.
Run the complete [verification command](verification.md#required-tools) before
publication, using synthetic disposable resources only.

Independent adversarial review must try cross-owner/scope handles, revocation and
expiry during reads/publication, retained-pin substitution, injected fill evidence,
commit controls disguised as fields, duplicate/resumed events, stale callbacks
and older-runtime admission. Resolve findings before publication.
**O29:** outside browser-agent page tools remain unexposed until separate
agent identity/delegation is enforceable; the person's browser session is
insufficient. Implementation and qualification are recorded in the linked contract; source
publication and production activation remain separate release steps.
