# Workspace assistant operations and page guidance

D284 extends the assistant with discovery, reads, previews and human-approved
commands over the application's existing operations. Runtime `turns_v11`
requires migrations 0162–0164 and matching API/browser assets. This local
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
the selected frame; they never substitute current inputs.

**Settings → AI → Page prompts** configures guidance for every recognized page,
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

| Area | Operations |
| --- | --- |
| Markets/curves | Navigation, evaluation, history, overlays, corrections, composition, save, publication and activation |
| Reference/parties/schedules | Authorized catalogs, editors, conversion, date resolution, preview, save and publication |
| Trading | Catalogs, positions, draft pricing/risk, booking, amendments and supported swap settlement |
| Risk | Interactive calculations, retained job directory/reports, submission and cancellation |
| Interval/delivery data | Source/series catalogs, selected windows, corrections and supported curve/quantity previews |
| Jobs/infrastructure | Existing directory, job creation/update/control and typed node operations |
| Administration/AI | Account, provider/model/agent configuration, usage and page guidance |

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
operations accept explicit pinned contexts. Newer physical-product entry/setup
pages retain their existing read boundary; their prompts are configurable, but
those product-specific command families are not added by this catalog.

Existing turn/token/read limits and complete-result bounds of 500 records/64 KiB
apply. Oversized results refuse instead of becoming partial successful reports.
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
