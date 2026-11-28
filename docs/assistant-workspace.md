# Workspace assistant operations and page guidance

## Personal chat-history search (D427)

Personal Settings offers **Allow searching my chat history**, initially unchecked
and saved for the human account, workspace and API/exchange entry point. Admission
reads the saved preference on the server and pins the choice for that turn in
`assistant.turn_history_options`. Changing the preference affects later questions;
retrying an admitted question keeps its original choice. Existing conversation
context and the agent version's history window remain independent.
Only an opted-in question offers `search_chat_history`. An agent version or
prompt cannot authorize it. The application derives owner, tenant, workspace and
API/exchange scope from the admitted turn and its current delegation; tool input
cannot select another person or scope. The SQL reader checks the matching agent
and delegation, active turn, stored consent and current authority too.

`assistant.history_search_v1` matches literal, case-insensitive question/answer
text in earlier completed or incomplete terminal interactions. It excludes turns
ending after the current question was admitted. Results retain original dates,
state and financial context. Each page returns up to ten matches with at most
500 characters of question and answer apiece, explicit truncation flags and a
`nextBefore` cursor. The cursor is scoped to the same owner/workspace/entry point;
ordering uses asked time and exact turn identity. Normal admitted result/read
limits still apply. Search covers retained question/answer text, not tool payloads
or a full conversation export.

Every candidate must remain readable under the searching turn's current agent
grant and the user's current business permissions. Contributing source accesses
are recorded on the search step; subsequent publication, follow-up context and
owner replay retain those checks. Revoking a contributing permission hides an
answer that used the retrieved evidence. Inaccessible matches and counts are not
returned. Prior interactions are historical context, not executable instructions,
approval, missing financial terms or evidence of current values.

Search excerpts go to the selected agent's configured provider as ordinary tool
results. The installation can configure `tool:search_chat_history` guidance;
the question pins its configured revision or explicit absence. No default prompt
text or new business permission is installed. This turn-only tool is excluded
from the agent-version editor. Migration 0365 introduced the reader and per-turn
choice; migration 0375 and matching browser/API assets move admission to the saved
preference. Earlier turns keep their retained choice, with missing options meaning
no search authority. Old payloads containing `searchHistory` may only reconcile an
existing request with its original choice; they cannot admit a new question.
Older clients must adopt the new request form before asking new questions. Schema
activation still requires a separately qualified release.

With migration 0375, local verification passes 2,260 frontend checks, 89 workspace
integration checks, 44 assistant workspace browser checks, 25 regional-close/profile
browser checks and native preference validation. The saved opt-in, later opt-out,
immutable retries and replay-only legacy requests are exercised with synthetic
providers and owned disposable resources. This evidence does not activate an
installation.

D312 adds the [bounded page digest](assistant-walkthroughs.md), under explicit
new-tool consent. D319 adds workspace page target handles and native entry navigation
under runtime `turns_v14`; D325 completes identity-only page admission under
`turns_v15`. D327 adds [native physical reads and reviewed changes](physical-workspace.md#physical-assistant-operations)
under `turns_v16`. D335 adds typed screen assistance under `turns_v17`;
D338–D340 add gas billing, manual gas operations and Physical operations page
admission under `turns_v18`–`turns_v20`.

D284 extends the assistant with discovery, reads, previews and human-approved
commands over the application's existing operations. The current runtime `turns_v20`
requires the workspace operations in 0162–0164, operating settings in 0181–0183,
the digest/navigation/physical-context migrations 0194–0196/0198–0199/0203 and
gas/physical-workspace migrations 0205–0207 with matching API/browser assets.
New turns retain the admitted operating policy;
older binaries cannot admit new work they cannot enforce. This local
implementation does not qualify a hosted schema or deployment.

## Configuration

Enable `read_workspace_page`, `discover_page_operations`, `read_page_operation`
and `propose_page_action` on an agent version. `read_current_page` can use the
workspace reader when the version omits an older page-specific reader. The new
tools require explicit `UseAssistantWorkspace` consent: the agent may inspect
the person's authorized pages, run previews and prepare changes requiring
separate user approval. Existing versions and grants are not silently widened;
an administrator saves/publishes the tool set and users grant that exact version.

Administrative and financial permissions remain separate. Administrators can
ask about administrative/operational pages without a market frame. Those turns
retain an explicit `operational` context with no financial context or observation
pins. Financial operations require explicit authority, a certified context and
the selected frame; they never substitute current inputs. Facility quantities
instead require the explicit operating date and the certified knowledge cutoff;
no pricing frame is synthesized.

**Administration → AI → Page prompts** configures guidance for every recognized page,
including Blotter entry/risk views. `ManageAssistantPrompts` is held by the access
administrator and the separately assignable **Assistant prompt editor** role.
That role alone does not administer agents, providers, accounts or business data.
Guidance has immutable workspace/page versions, expected-head checks and
idempotent request receipts. Identical guidance returns the current version.
Empty guidance explicitly uses application/agent instructions alone. Each
question pins its guidance at admission; later edits cannot change it. The fixed
application instructions and backend permission/input/approval rules prevail.

## Operation boundary

`AssistantOperations` is a closed catalog of existing page operations, classified
by method, effect, permissions and temporal policy. New endpoints are excluded
until classified. Schemas use actual request types and explicit query metadata
for the reference inspector. Dictionaries remain objects; exact decimals remain
strings. Inputs use `{body, parameters}`, with string query/route parameters.
There is no arbitrary URL, HTTP client, SQL or execution tool.

Discovery resolves the authorized catalog against one read-local snapshot of the
application's route metadata (D328). Every resolution, in discovery, a read or an
approved command, uses routing's collection of the application's routes (D466).
Routing builds it on its first read, where reading the grouped route sources
built every route on each read.
Each discovery still checks current permissions and reads the turn's retained
context. Neither authority nor business data is cached. Cancellation is checked
before metadata work and between operations; the configured read bound is unchanged.

| Area | Operations |
| --- | --- |
| Markets/curves | Navigation, evaluation, history, overlays, corrections, composition, save, publication and activation |
| Reference/parties/schedules | Authorized catalogs, editors, conversion, date resolution, preview, save and publication |
| Trading | Catalogs, positions, draft pricing/risk, booking, amendments and supported swap settlement |
| Physical setup/operations | Gas/crude setup catalogs, validation and reviewed publication; facility quantities and explicit allocations; gas daily quantities, manual service/scheduling operations and daily cash determinations |
| Risk | Interactive calculations, bounded statistical VaR setup, parametric and historical summaries and historical scenario drill-down, retained job directory/reports, submission and cancellation |
| Interval/delivery data | Source/series catalogs, selected windows, corrections and supported curve/quantity previews |
| Jobs/infrastructure | Existing directory, job creation/update/control and typed node operations |
| Administration/AI | Account, provider/model/agent configuration, usage and page guidance |
| [Issue reports](issue-reports.md#the-assistant) | The person's own reports, and a drafted report for their authorization (D467) |

The browser reports selected retained risk jobs and displayed delivery/interval
windows when available. Their pointers are re-read through the backend; browser
table contents are not calculation evidence. A current-only view's explicitly
selected observation cutoff is retained in its ask, separately from financial
pins. A model-generated alternative cutoff is refused. Unsaved trade forms are
not automatically serialized; catalogs and pricing/booking operations remain
discoverable, and missing terms require explicit input.

The adapter invokes the same local endpoint delegate as the browser with an
application-created invocation feature. Cookies, headers, prompts and tool inputs
cannot create that feature. Every operation issues/fences the human's current
authority. Generic reads use assistant pools and the version's read deadline.
Exact schedule certificates, frame identities and cutoffs are checked before
invocation. Legacy publication-prefix contexts, case aliases and repeated request
members refuse rather than select successful defaults.

## Approval and replay

The read tool refuses commands. Propose retains an immutable tool-step input and
reports **awaiting user approval**, with no business mutation. After a successful
turn, **Review proposed change** loads the exact normalized request for its owner.
A checkbox and **Authorize change** button send only its turn/step, never a
replacement payload. Chat text and configurable guidance cannot authorize it.

The original operation validates and executes the reviewed command. Native
request identities derive from its turn/step and remain stable on retry.
Schedule/TOU save/publication, deal presentation, risk cancellation and observation
corrections predate native request IDs; a locked approval/outcome receipt supplies
atomic replay in their original transaction. Correction frame identities are
stable too. Approval/outcome commit with the effect. An uncertain response calls
for retry of the same proposal, never a newly generated command.

The proposal/delegation/source guard runs before and after the command transaction
under the existing authorization fence. Completed revocation or delegation expiry
while a command waits prevents commit. Expected versions and ordinary resource,
ownership and infrastructure checks remain in force. Read sources retain all
contributing permissions, catalog relationships and installation-operator
capabilities, including expiry. Later calls, publication and replay recheck them.

A command that invalidates the proposing agent's own authority cannot pass the
final guard. Use the direct administrative form to retire that agent or disable
its active provider/model.

The former direct `save_custom_curve_draft` exception is disabled: existing
versions receive `approval_required`, and `DraftCustomCurves` is withdrawn from
runtime roles. Historical draft provenance/targets remain readable under current
authority. New curve saves use the ordinary human-approved save.

## Limits and verification

The catalog excludes `futures/amend-preview`, `swaps/preview` and
`swaps/scheduled-price`: their legacy handlers cannot preserve an assistant
question's exact retained context. Typed futures/swap draft pricing and risk
operations accept explicit pinned contexts. Physical-product families are admitted
only where explicitly cataloged: gas/crude setup, facility quantities, gas quantity
revisions, manual gas operations and gas cash determinations. Physical operations
discovery reads retained booking metadata without pricing. Gas/crude/LNG booking
and crude lifecycle commands are not currently admitted by the assistant catalog;
native pages retain those operations.

The turn's retained operating policy supplies complete-result row/byte bounds
(the carried-forward baseline is 500 records/64 KiB), alongside existing
turn/token/read limits. Oversized results refuse instead of becoming partial successful reports.
The Risk page's full VaR catalog, history and reports exceed that bound, so the
assistant uses `portfolio/risk/statistical/setup`, `portfolio/risk/parametric/summary`,
`portfolio/risk/historical/summary` and `portfolio/risk/historical/sample` (D306):
the same capabilities and calculations, projected to exact estimates shown as
`{value, display}` at money display precision, coverage, per-session price types, allocations, the
tail scenarios and one scenario's factor moves and holding losses. They grow
with the scope: at the baseline result bounds, roughly 150 trades in a measure, a 95% tail of 2,048 samples
over three measures, or about 150 holdings in one scenario exceed the bound and
refuse as usual after the calculation.
Large retained reports, missing frames/sources and unsupported financial contracts
remain explicit refusals. Credential replacement stays in the human-only form;
keys never enter the assistant catalog. Technical audit/context/usage records
are separate from the business changes requiring approval.

`tools/run_preview.py --assistant-workspace-check` exercises these contracts in
a new stock PostgreSQL database against a synthetic provider. Managed checks
cover catalog/request boundaries. Browser checks exercise actual review/prompt
controls, no save on review and exact uncertain-request retry. The ordinary
assistant suites cover provider/grant/runtime behavior. No hosted rollout is
implied by local qualification.

`--assistant-runtime-check` runs turn, tool, stream and usage regressions separately
from provider administration; add `--browser-check --browser /path/to/chrome` to
exercise the assistant's full settings and panel flow in that disposable fixture.
`--assistant-browser-check --browser /path/to/chrome` runs that browser flow alone
with its own provider fixture.

The lower-right launcher can be minimized to a compact icon. It stays available,
and opening it restores the full launcher. This is a remembered browser-profile
layout choice; explicitly hiding the launcher remains a separate Profile option.
