# Page assistant, phase 1: local-preview core

Status: design proposal awaiting approval. Nothing in this document is implemented.

The page assistant ships in three phases, each with its own design:

1. **Local-preview core** (this document): Settings › AI, model provider adapters,
   usage and cost, agent principals and access grants, context pinning, the fixed
   read tools plus Navigate and Show card, an in-process worker, a server-sent event
   stream, the assistant panel, application-drawn cards and conversation memory. It
   runs only under `--local-preview`.
2. **Agent notes** (later design): per-user agent notebooks and a common notebook,
   versioned notes with sources and a Notes view.
3. **Hosted baseline** (later design): the assistant under `--hosted-pilot`.

Phase 1 brings a bounded slice of [Milestone 5](plan.md) ahead of Milestones 1–4. It
does not satisfy Milestone 5's acceptance criteria. Requirements in
[architecture.md](architecture.md) §9–§12 and the agreed decisions in
[decisions.md](decisions.md) apply unless §14 records a change.

## 1. Intent and success

A user asks an assistant about the page they are on and is taken to where the
answer lives. A trader on the blotter asks which trades drive a book's execution
P&L as of the selected date. The assistant reads the same data the page shows,
under the trader's own authority and the context pinned when they asked, answers
with the values it read, opens the trade that matters and shows a chart of those
values. Administrators connect model providers, allow models, record token prices,
define agents and see what each model, user and agent cost.

Phase 1 succeeds when, in the local preview:

- a user on a supported page (§7.3) opens a docked panel, picks an agent, asks and
  receives a streamed answer grounded in server reads made under that user's
  authority and pinned context;
- the agent moves the user to another page, record or filter, says where it went and
  offers Back;
- answers can include cards that the application draws from values the server
  returned in that turn;
- every model call records normalized token usage and an exact cost, or an explicit
  unpriced or unknown state, from the price revision pinned when the call opened;
- administrators manage providers, write-only credentials, models, effective-dated
  prices and agents, and read usage by model, user and agent;
- with no provider enabled, no turn calls a model and the launcher stays hidden.
  Existing pages, reads and bookings behave as today.

Under `--hosted-pilot` phase 1 is inert (§11).

## 2. Goals, non-goals and anti-goals

### Goals

- Five model vendors (Anthropic, OpenAI, Google, xAI, Meta) through three protocol
  adapters (§6). A provider is enableable only after qualification (§6.4).
- Settings › AI with Usage, Models & prices, Providers and Agents tabs (§9).
- A docked, resizable panel that persists across page changes (§10).
- Server-sent events from server to browser (§8).
- Page questions that change no business state, validated navigation and
  application-drawn cards.
- Usage and cost reporting by model, user and agent, with CSV export.

### Non-goals

- Agent notes (phase 2) and any hosted operation (phase 3).
- Day-over-day or period P&L comparison and P&L explain (D77, O12): the next slice.
- Monthly workspace or user spending caps: a later slice. Phase 1 bounds cost per
  turn (§7.5) and by concurrency.
- Open sensitivity and other calculations.
- Agents that book, amend, approve, publish or otherwise change business state.
- Tools beyond the fixed set (§7.3), external tool servers or sub-agents.
- Agent-written code or any scripting sandbox (architecture §12).
- Embedded page views in the panel. Only the four card kinds in §10.3 exist.
- Tables, HTML, links or images in answer text.
- Paging of tool reads. A result over the tool-result bounds is refused (§7.3).
- Delivery-price reads by any tool.
- Running an unsaved agent definition, which has no versioned contract.
- Billing, attachments, images, voice, pop-out windows, steering or branching.
- Administrator pause, single-turn cancellation and run inspection (architecture §9
  Operations). Disable and Retire cancel running turns (§7.5).

### Anti-goals

Each of these is a failure even if everything else works:

- An agent reading or showing anything the asking user cannot currently read, or
  reading a context other than the one pinned at ask, except the current-only
  definition reads that §7.2 allows under a working-data pin.
- A credential in a log, audit row, SQL parameter, model context, browser storage,
  URL or any response to the browser.
- Business data sent anywhere except the fixed endpoint of an enabled provider.
- A cost of zero for an unpriced or unreported call, or a recorded cost changing
  after a price edit.
- Silently switching provider or model after an error.
- A number on a card that the server did not return.

## 3. Constraints from the existing system

Host and requests:

- The API runs in exactly one selected mode (`Program.cs`). `--local-preview`
  listens on loopback and requires the loopback peer, Host `127.0.0.1` and the port,
  plus a matching Origin when one is sent.
- The first middleware rejects bodies over 32 KB and cancels each request after 15
  seconds. Requests of one workspace session are serialized by a semaphore.
  In-memory workspace sessions end 30 minutes after creation and do not survive a
  restart.
- The local preview has two scopes, `/api` and `/examples/exchange/api` (when the
  exchange fixture is loaded). Each has its own manifest, connection pools,
  sessions, session cookie and anti-forgery scope. They share one PostgreSQL
  database.
- Request records reject unlisted JSON members (`JsonUnmappedMemberHandling.Disallow`).
- The CSP allows scripts and connections only from the application origin and
  forbids framing.

Context:

- The As of selection is an input frame held in the in-memory session: random ids,
  at most 128 per session. `POST /market/context` selects a historical context;
  `POST /market/current` or a profile switch clears it.
  - Trading reads send a frame id and a knowledge mode (current, or a retained
    context id). Current is accepted only while no historical context is selected.
  - Curve, history, party and power-hour reads send only a frame id, which must
    belong to the session's current market context (`snapshot_missing`).
  - `GET /workspace`, `/date-schedules` and `/curve-names` bind the session's
    market context.
  - Power-hour and TOU product reads refuse a historical context
    (`power_historical`).
- Every date-context capture (`security.capture_date_context`, current or with an
  explicit cutoff) first certifies committed collection batches under global
  advisory lock 730849102 (D134), held to commit. A catalog holds at most 4096
  retained contexts and none are reclaimed; at the limit a capture that would add one
  fails with `knowledge_limit`. A current-mode capture always adds one, because its
  cutoff is the capture instant.

Authorization:

- Page reads are handlers inside `MapPreview` that depend on the HTTP request, the
  workspace session and its frames. They cannot run from a background worker as
  they are (§12 step 2).
- `security.has_permission`, for an agent principal, requires the agent's own
  permission, an unexpired, unrevoked delegation listing it, the delegator's current
  permission and `CreateDelegations`, and enabled principals. It does not know which
  user asked. `security.issue_context`, executable by the authenticator login, issues
  agent contexts with a delegation. `security.trading_access` and
  `security.use_market_context` also require `ReadMarketData`.
- Any write to a security control table, delegations included, advances the
  generation on the single `security.epoch` row (`guard_control_change`).
  `security.use_context` holds that row `FOR SHARE` for the request transaction. A
  delivery-price collection attempt cannot finish if the generation changed. P18
  requires throughput qualification before broader use of this fence.
- `security.use_administration_context` (migration 0010) takes the epoch row
  `FOR UPDATE` before it checks `ManageIdentities` and `ManageAccess`, and any member
  reaches it through `POST /admin/update`.
- Users & access creates agent principals with any roles through
  `apply_administration` in the local preview and edits any member, agents included.
- Role templates: `ReadMarketData` is held by every role except access
  administrator. `ReadTrades`: trader and risk analyst. `ReadTradeParties`: trader,
  risk analyst and market data editor. `CreateDelegations`: trader, risk analyst,
  market data editor and access administrator.
- Hosted provisioning (`tools/provision_hosted.py`) grants the owner every row of
  `security.roles`.

Resources and numbers:

- Each scope opens a runtime and an authenticator pool of eight connections, with
  5-second command and connect timeouts. Every request issues a request context on
  the authenticator pool. `security.request_contexts` rows are never reclaimed.
- Money uses exact decimals with at most 18 fractional digits (P15) and exact-version
  currency references. Exact division that does not terminate within 18 digits fails
  with `precision_loss`. `AmountFormat` supports 0–18 places.
- The browser is presentation only (AGENTS.md).

Egress and release:

- The API makes no outbound calls today. Outbound HTTP exists only in the separate
  delivery-price collector (`--collect-power`, D133).
- In code and operator documents, "provider" already means a PJM or ERCOT
  delivery-price source (`PowerProviderSettings`, `provider_http`).
- Every hosted release bundle carries the full migration inventory, and a new
  migration stops automatic hosted updates until an operator qualifies a new
  baseline (D135).

## 4. Vocabulary

- **Model provider** ("provider" here and in Settings › AI): one of the five model
  vendors with its adapter, fixed endpoint, credential and enabled state. Distinct
  from delivery-price providers, which this design does not touch.
- **Model**: a vendor model identifier an administrator allowed, with immutable
  versions of its capabilities (§5.1).
- **Price revision**: an immutable, effective-dated set of per-million-token rates
  for one model in one currency.
- **Agent**: a named assistant definition with its own agent principal (D40). Each
  save creates an immutable **agent version**. A version is a **draft** until
  Publish makes it the agent's **published** version.
- **Access grant**: a revocable delegation from one user to one agent (§7.1).
- **Conversation**: one user's thread with one agent, made of **turns**. A turn is
  one question and the agent's complete response.
- **Answer**: a turn that completed. Usage counts only these as answers.
- **Source set**: the tool reads whose content reached the model in a turn,
  including reads of earlier turns sent as history (§7.6), recorded as exact
  tool-read ids.
- **Model call**: one request attempt to a provider, by a turn or a provider test.

The interface says "agent" and "assistant". Vendor names appear under the AGENTS.md
exception recorded in §14.

## 5. Data and administration

### 5.1 Tables

Tables follow existing conventions: workspace scope, row-level security,
exact-version foreign keys and audited operations with operation identities and
expected versions. Each has a consumer in this document.

| Table | Contents | Mutability |
| --- | --- | --- |
| Providers | Vendor (one of five). Versions: retention option, training-use terms, whether the account owner can view request or response content, each with source and date. State: enabled; account-terms attestation bound to the current credential | Identity plus immutable versions |
| Model-provider credentials | Provider, credential version, nonce, ciphertext and tag, last four characters, set by and at | Replace-only; never readable through the API |
| Models | Provider and vendor model identifier (immutable); display name (audited presentation field). Versions: maximum output tokens, temperature support and range, reasoning control, identifier-stability source, accepted served identifiers, tool-calling and streaming confirmation, each with source and date. State: enabled | Identity plus immutable versions |
| Price revisions | Model identity, exact currency reference, rates and optional tier (§5.3), `effective_from`/`effective_to`, append sequence, `recorded_at` | Immutable; corrections append |
| Agents and agent versions | Agent: name, agent principal, published version, active or retired. Version: picker description, instructions, exact model version, temperature, reasoning value, caching switch, tools, allowed roles, limits (§7.5), grant duration | Head moves forward; versions immutable |
| Model administration audit | Operation identity, actor, target, redacted request, before and after; usage-export entries | Append-only |
| Grant audit | Operation identity, delegation id, delegator, agent principal, agent version, provider and model identity, permissions, expiry, actor, outcome, time | Append-only |
| Conversations | Owner, agent, scope, read-through sequence (§10.2) | Immutable except read-through |
| Turns | Conversation, exact agent version, grant id, question, answer and incomplete marker, pinned-context record (§7.2), page report, source set, included earlier turns, runtime-set version and limits, state, outcome, worker generation, fencing-token digest, asking workspace session, profile and session end, timestamps | States move forward only |
| Turn events | Ordered stream events for replay | Append-only |
| Tool reads | Result, or the failure code of a read that ran; exact read-operation version; each catalog with the access function and permission checked; for a current-only read, the instant it read | Immutable |
| Cards | Validated specification, server-composed title, bound tool reads, issued navigation targets | Immutable |
| Model calls and usage | Turn or test (with testing administrator, test-token digest and parameters sent), attempt, start instant, exact model and provider versions, requested and served identifier, reported service tier, usage-mapping version, per-dimension counts and states (§6.3), cost method, exact price revision, cost, outcome | Immutable once closed |
| Worker generation | Current agent-worker generation for the database (§7.4) | Advanced at worker start |

Price revisions and the display name belong to the model identity, so renaming never
leaves a model unpriced or marks agents "Model changed". Agent versions reference an
exact model version; turns, the exact agent version.

Grants use the existing `security.agent_delegations` and
`security.delegation_permissions` tables, with the grant audit as history.

### 5.2 Credentials

- An administrator enters a key through a dedicated operation. The C# boundary
  removes it before any SQL parameter, log or audit write. The audit records a
  redaction marker and a keyed hash, so an identical retry is recognized.
- The browser holds the key only in the form field and the in-flight request. It
  never writes it to browser storage, a URL or the pending-command record, which
  holds only path, provider, expected version and operation identity. The field is
  cleared when the request settles. After an uncertain outcome the tab shows the
  current credential status, and Retry asks for the key again and reuses the
  operation identity. The server returns the recorded result when the keyed hash
  matches and a conflict when it differs.
- Two 32-byte secrets, each in its own private file: a key-encryption key (AES-GCM)
  and a retry-hash key (HMAC-SHA-256). `tools/run_preview.py` generates both once
  per disposable database and passes the same files to every API start for it.
- Each seal uses a fresh 96-bit nonce from `RandomNumberGenerator` and a 128-bit tag.
  The associated data is provider identity, vendor and credential version. A missing
  or wrong key file or a failed tag check yields `credential_unavailable`. Nothing
  is generated or defaulted at API start. Credentials do not depend on ASP.NET data
  protection, whose keys are ephemeral in the local preview.
- Rotating either secret is out of scope. Losing one means re-entering every key.
- The API returns only "set", the last four characters, who set it and when.
- Keys travel only in headers (`x-api-key`, `Authorization: Bearer` or
  `x-goog-api-key`). Provider clients follow no redirects, use a `SocketsHttpHandler`
  with `UseProxy=false` (ambient proxy variables are ignored), keep default
  certificate validation, and take the connect and idle timeouts and the response
  byte bound from the runtime set (§7.5).
- Each vendor endpoint is a sourced, dated HTTPS constant in its adapter. The local
  preview reads an optional endpoint override that must be an IP-literal loopback URL
  (`127.0.0.1` or `[::1]`); it exists only so runtime and browser checks can reach the
  fake provider, which lives under `tests/` and is not published.

### 5.3 Prices and cost

- Rates are exact decimals per million tokens with at most 12 fractional digits:
  uncached input, cache read, cache write and output. A rate may be "not offered",
  except cache read on protocols that cache automatically (§6.1).
- Each revision binds one currency row from a sealed reference revision scoped to
  the workspace. The security-definer price operation rechecks the actor's current
  `ReadMarketData` reference access to that catalog (D131) and otherwise fails with
  `currency_unavailable`. Currency choices and Usage labels come from the same scoped
  read (D124). An operator assigns model administrators a role with `ReadMarketData`.
- An optional long-prompt tier gives a token threshold and a complete second rate
  set. If uncached input + cache read + cache write is strictly greater than the
  threshold, the second set prices every dimension of the call, output included.
  Tiers that price only tokens above the threshold cannot be entered. If the pinned
  revision has a tier and any prompt dimension is unknown, the call's cost is unknown.
- Price revisions are operational configuration, not certified business knowledge
  (no D67 certification). Appends take the model's expected version. When a call
  opens (§7.1), the server pins the committed revision with the highest append
  sequence whose [`effective_from`, `effective_to`) contains the call's start
  instant, in the transaction that opens the call. A later append never reprices
  it; Usage flags calls priced by a since-superseded revision. `recorded_at` makes
  no knowledge claim.
- A model with no price in force cannot start a call (`model_unpriced`). Tokens in
  a "not offered" dimension leave the cost unstored (`cost_unpriced`). Usage that is
  `usage_unrecognized` or `usage_unknown` stores no cost, never a partial one.
- `token_cost_v1` computes cost once, on the server, and owns the tier rule.
  Reasoning is an informational subset of output and is never priced separately;
  reasoning greater than output is `usage_unrecognized`.
- Costs and sums are exact at 18 fractional digits, never rounded, and displayed
  with `token_cost_display_v1` (trailing zeros trimmed, grouped), so a nonzero cost
  never shows as zero. Cost per answer and share use `token_cost_report_v1` (18
  places and 2-place percentage, ties to even); the policy is recorded with each
  report.
- Totals group by exact currency reference (D72) and are never added across
  references. A label shows the display code, plus the reference revision when two
  totals share a code. CSV export always includes the exact reference.
- Regional, fast, priority and cache-storage surcharges are unsupported and never
  requested (§6.1).

### 5.4 Permissions, roles and agent principals

| Permission | Grants | Initial roles |
| --- | --- | --- |
| `ManageModelProviders` | Providers, credentials, models, prices, provider tests and their audit | Access administrator |
| `ManageAgents` | Agents, versions, Publish, Retire, agent principals and their audit | Access administrator |
| `ReadModelUsage` | Usage reports and audited CSV export | Access administrator |
| `UseAgents` | The panel, grants and asks | Trader, risk analyst, market data editor |

- `UseAgents` goes only to the roles that hold `CreateDelegations`, which every agent
  read requires. Viewers and market data approvers do not get the assistant.
- An agent version's allowed roles may contain only roles holding `UseAgents` and
  every permission its switched-on tools need (§7.3). A database check enforces this
  (`agent_role_lacks_permission`, naming role and permission). For example, market
  data editor cannot be allowed on a version with Read trades on.
- A new **agent reader** role holds exactly `ReadMarketData`, `ReadTrades` and
  `ReadTradeParties`. A database check allows it only on agent principals and allows
  agent principals no other role. The migration fails if an agent principal already
  holds another role; it never revokes silently.
- Creating an agent creates, in one `ManageAgents` security-definer operation, the
  agent principal, an `agent_credential` binding at the manifest issuer with the
  principal id as subject, its workspace membership (counted in the 256-member limit)
  and the agent reader grant. The principal has no login or password. The operation
  appends to `security.administration_audit`, so Users & access shows the creation.
- Retire disables the principal, revokes its open grants and cancels its running
  turns. It is permanent and keeps history.
- Creation and Retire write control tables, so they use a dedicated exclusive
  admission. Every check first runs in an ordinary short read; only a command that
  will write opens the exclusive admission, which takes the epoch row `FOR UPDATE`
  before binding and repeats the checks.
- Users & access: `apply_administration` refuses to create an agent (`agent_managed`)
  and refuses any update targeting an agent principal or naming the agent reader role
  (`agent_managed`). `administration_directory` leaves that role out of assignable
  choices, still serves the agent kind for display, and agent rows have no Edit.
- Provider, credential, model, price and agent-version operations write no control
  table. They use ordinary admission and check their permission inside the operation.
- Managing providers decides where agent traffic goes: the vendor account behind a
  key receives questions, tool results and answers, and its owner may see what the
  vendor retains. Editing instructions changes what an agent reads within each grant.
  Both are audited. Neither grants any application read permission.
- Usage reports show users, agents, models, counts and cost, never conversation
  content. Users see only their own conversations and the cost of their own turns.

## 6. Provider adapters

### 6.1 Contract

- A provider-neutral boundary (D06) has one operation: stream a response for
  normalized messages, a system instruction, tool definitions and explicit
  parameters, with cancellation. It returns typed events: text delta, tool request,
  completion with stop reason, normalized usage and error category. Provider types
  and response ids stay inside the adapter. Adapters use the in-box .NET HTTP client;
  no vendor SDKs.
- Every parameter whose vendor default could change behavior or billing is sent
  explicitly, as an agent-version value or an adapter constant recorded with the
  mapping version:
  - **Temperature:** required where the model version supports it, validated
    against its range, otherwise recorded as "not sent".
  - **Reasoning:** each model version records its control (effort levels or budget
    range and field) or "not adjustable". An adjustable model needs an explicit
    agent-version value (`parameter_required`).
  - **Service tier:** the vendor's standard tier where the protocol has the field. A
    response reporting another tier makes usage `usage_unrecognized`.
  - **Tools:** tool choice "auto" and parallel tool calls as recorded constants.
  - **Caching:** the caching switch applies only on the Messages API (explicit
    five-minute breakpoints). Elsewhere caching is automatic; any retention field is
    sent at its standard value.
  - **Storage:** provider-side response storage is disabled wherever offered (for
    example `store: false`).
- A model is allowed only by an identifier its vendor documents as not repointed,
  with source and date. The served identifier is recorded; if it is neither the
  requested one nor an accepted served form, usage is kept and the turn fails with
  `model_mismatch`.
- Identifiers match `^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$`, optionally narrowed per
  adapter (`model_identifier_invalid`). The `generateContent` adapter builds its URL
  from its endpoint constant, the prefix `/v1beta/models/`, the identifier as one
  path segment and `:streamGenerateContent?alt=sse`, and sends nothing if the result's
  scheme, host, port or path prefix differ from its constants.
- Requests ask for no reasoning content. Reasoning content streamed anyway is
  discarded in the adapter and never emitted, stored, logged or shown.
- Error bodies are read only to classify them (§7.5) and to apply the mapping's
  not-billed statuses (§6.3). They are never stored, logged, streamed or returned.
- An opaque continuation value (signed thinking blocks, thought signatures,
  encrypted reasoning items) is echoed unchanged with that step's tool results to the
  same model in the same turn. It lives only in worker memory for the turn.

### 6.2 Protocols

| Providers | Protocol | Endpoint path |
| --- | --- | --- |
| Anthropic | Messages API | `POST /v1/messages`, SSE |
| OpenAI, xAI, Meta | Responses API | `POST /v1/responses`, SSE |
| Google | `generateContent` | `POST /v1beta/models/{model}:streamGenerateContent?alt=sse`, SSE |

### 6.3 Usage normalization

- Each provider (vendor plus protocol) has its own versioned usage mapping with
  source and date. Providers sharing a protocol share the parser, not the mapping.
- Dimensions: uncached input, cache read, cache write, output and reasoning. Each is
  declared reported (source field and the event carrying its final value), not
  billed (a structural zero with source and date, never inferred from absence) or
  included in another dimension.
- Only final values count: `message_delta` for Messages, the terminal response event
  for Responses, the last chunk's `usageMetadata` for `generateContent`.
- Each mapping states its output formula. For `generateContent`, output is
  `candidatesTokenCount` + `thoughtsTokenCount` and reasoning is
  `thoughtsTokenCount`.
- A normally ended stream with a missing usage block, a missing reported dimension,
  an unnamed nonzero field or an inconsistent total is `usage_unrecognized`.
- In a cancelled, failed or interrupted stream, each reported dimension without its
  final value is unknown and the call is `usage_unknown`. Unknown never becomes zero.
- Each mapping lists the error statuses that are not billed. Any other status may
  have been billed, which leaves the attempt's input unknown (§7.5).

### 6.4 Qualification

- A conformance suite runs every adapter against the local fake provider, which
  replays hand-written synthetic recordings: streamed text; tool requests; a two-step
  tool loop whose continuation value must be echoed byte for byte; usage with cache
  and reasoning; reasoning content and continuation data that must appear in no event,
  row or log; errors, rate limits and an overload before any event; malformed events;
  cancellation, transport failure and overload after streamed text; a response past
  the byte bound (`model_provider_response_too_large`); and error bodies echoing
  synthetic request text and a masked synthetic key that must appear nowhere. The fake
  asserts every request path. Checks never call real providers. The fake is
  injected through an `HttpMessageHandler`, as `PowerImportChecks` does.
- A provider is enableable only with: its usage mapping confirmed from published
  documentation; the vendor's published retention and training-use terms for the
  account type and whether the account owner can view content, each with source and
  date; and an administrator attestation, bound to the current credential, that the
  account is of that type, does not permit training on submitted content and has no
  data-sharing opt-in. Otherwise it shows `model_provider_unqualified`. Meta is shown
  but stays unqualified until its usage fields are confirmed from published
  documentation; phase 1 is accepted without it.
- A model is enableable only when tool calling and streaming are confirmed from
  published documentation and pass conformance (`model_unqualified` otherwise).
- Conformance against the fake is not real-provider qualification. O09 stays open
  until real task outcomes are recorded.

## 7. Agent runtime

### 7.1 Authority and grants

- **Who may use a version.** The published version: users who hold `UseAgents`, one
  of its allowed roles and every permission it needs. A draft: holders of
  `ManageAgents` who also hold `UseAgents` and every permission it needs, with
  normal grants and usage. Only the newest version can be a draft.
- **Grant.** The first time a user asks an agent without a covering grant, the ask
  fails with `grant_required` and the panel asks: "Allow Page guide, which sends your
  questions and the data it reads to <provider> (<model>) through the vendor account
  your administrators configured, whose owner may be able to see them, to read
  trades, trade parties and market data on your behalf until 2026-11-17?" The
  permissions are those of the version's switched-on read tools (§7.3).
- The grant request names only the agent version; any other field is rejected and
  writes nothing. A security-definer operation takes the delegator from the bound
  human context and the agent principal, permission set and expiry (now plus the
  version's duration) from the stored version. Failures: `agent_retired`,
  `agent_not_allowed` and `grant_permission_missing` (naming it). Grants are never
  partial.
- **Coverage.** A grant covers version V when it is unrevoked and unexpired, comes
  from the user to V's agent, was given for a version with V's provider and model
  identity, and lists every permission V needs. A new version that changes provider
  or model identity, or needs a permission the grant lacks, therefore asks again. A
  model-version change with the same identifier does not.
- A version's grant duration must not exceed the maximum in the runtime set
  (`grant_duration_exceeded` on save). A covering grant keeps its own expiry.
- A new grant revokes the user's earlier grants to that agent in the same
  transaction, with an audit record each, so a user has at most one current grant
  per agent. Running turns under a revoked grant are cancelled (§7.5).
- **Revoke** takes a delegation id and succeeds only for its delegator. Another
  user's id fails with the same error and shape as an unknown id, whatever its state.
  Revoke needs no `UseAgents`.
- Grant and revoke write control tables. They follow the creation pattern in §5.4:
  every check (including whether an identical grant exists, or whether an owned
  grant is already revoked or expired) runs first in an ordinary read; refused and
  no-op commands answer from it and never lock the epoch; only a writing command
  takes the exclusive admission and repeats the checks. The existing
  `guard_control_change` trigger stays, so a grant or revoke fences an in-flight
  delivery-price collection attempt like any role change.
- Each grant and revocation appends a grant audit record in the same transaction.
  The profile dialog lists the user's grants and history with Revoke. Holders of
  `ManageIdentities` and `ManageAccess` see every grant and revocation in Users &
  access.
- **Agent context.** Every tool read runs in a request context for the agent
  principal, issued through `security.issue_context` with login kind
  `agent_credential` and the turn's grant, recorded on the turn at ask. The grant
  never comes from browser input, model output or another turn. SQL then requires the
  agent's permission, the grant listing it and the delegator's current permission and
  `CreateDelegations`. Tying a turn to its owner's grant is the server's
  responsibility, under the same trusted-authenticator boundary that ties a page
  request to its session.
- **Current authority** means, evaluated in PostgreSQL: the user and membership are
  enabled; the user holds `UseAgents` and one of the version's allowed roles (for a
  draft, `ManageAgents`); the turn's grant is unrevoked and unexpired; the agent
  principal and membership are enabled.
- **Source-access check** means: under the turn's agent context, the recorded access
  function and permission pass for every catalog in the turn's source set.
- **Turn-start and per-call checks.** At ask and before every model call: current
  authority and the source-access check; the agent is active; the model is enabled
  and qualified, the version's model version is still current and a price is in
  force; the provider is enabled and qualified; the credential decrypts. Failures:
  `access_changed`, `agent_retired`, `model_changed`, `model_disabled`,
  `model_unqualified`, `model_unpriced`, `model_provider_disabled`,
  `model_provider_unqualified` or `credential_unavailable`. A turn never follows a
  newer version.
- **Opening a model call.** The last step before each call opens its record through
  the turn's fencing-token operation (§7.4), in the transaction that runs the
  database checks and reads the ciphertext. It records the start instant and pins the
  price revision. No request byte is sent unless it commits. Closing adds counts and
  outcome only.
- **Publication recheck.** Before each content-bearing event (step, text, card,
  navigation target) is appended, current authority and the source-access check run
  in the append transaction, which holds the epoch row `FOR SHARE` as page requests
  do. The stream sends only committed events, so a completed revoke stops every later
  append; content already committed may be in flight, as with a page response.
  Failure ends the turn with `access_changed`. Terminal, usage and cost events carry
  no business data and skip the recheck.
- **Replay recheck.** Before stored turn content is sent again (conversation reads,
  stream replay, re-materialized cards), the server evaluates, in the asking user's
  own request context, the recorded access function for every catalog in the turn's
  source set. If any fails, the turn's answer, cards and targets are hidden and
  marked `access_changed`. Replay needs no grant and ignores agent, model and provider
  status, so a retired agent's turns still replay.

### 7.2 Pinned context

- The ask names the knowledge mode (current, or the exact retained context id) and,
  when one is selected, the As of frame id, in the form trading reads use (§3).
  - A named frame must belong to the session and its current market context
    (`snapshot_missing`).
  - Current is accepted only while no historical context is selected. A retained
    context is accepted only when it equals the selected historical context.
  - Anything else, or an unresolvable mode, fails with `context_required`. The ask
    never falls back.
- At ask the server writes the turn's immutable pinned-context record: knowledge
  mode, captured or retained context id, resolved knowledge cutoff and, when a frame
  was named, its complete pin set (frame id, At, observation ids, curve-contract
  selections). It is the turn's snapshot identity. The worker reads only from it.
- **Working data.** An ask passes the length, page-report, conversation, agent,
  grant and turn-start checks, then admission (§7.4), which creates the turn queued
  and takes its slots. The asking request then captures once, in the user's request
  context and its own short transaction, and writes the record with the fencing
  token. That write is accepted only while the turn is queued with no record. Both
  run under the request's cancellation, within the 15-second deadline.
  - A failed capture ends the turn failed with the error a page read would return.
    No other cutoff is tried.
  - An abort or the deadline cancels the capture; the turn fails `request_cancelled`.
  - A turn already cancel requested ends cancelled.

  Each terminal write presents the token, appends its terminal event and runs under a
  cancellation scope independent of the request. Tool reads use only the stored
  context and never capture.
- **Retained-context reserve.** Each admitted working-mode ask permanently uses one
  of the catalog's 4096 retained contexts. The assistant's capture goes through a
  wrapper that, under lock 730849102 and before inserting, refuses with
  `knowledge_budget_reserved` when fewer than the runtime set's reserve would remain.
  Page captures keep the reserve. Reclaiming contexts stays open under O07.
- Tools take no context, snapshot or workspace inputs. The exception is current-only
  definitions (TOU products and power hours): they run without a bound market context,
  only under a working-data pin, and return `power_historical` under a historical pin.
  Each records the instant it read, and every display of it (card title and footer)
  says current data read at that instant, never the pinned cutoff.
- A read that consumes a frame fails with `snapshot_missing` when the turn pinned
  none: curve prices and history, trade parties, trades and power hours.
- **Page report.** Stored with the turn at ask, never changed. Read current page
  reads only the stored report, so navigation during the turn, Navigate included,
  never changes what the turn reads. Its selection is checked when the tool runs: the
  page's extracted read runs under the pinned context and the agent's checks, and a
  selection it does not find is refused with `page_selection_unavailable`.
- **Session binding.** The turn records its workspace session, profile and session
  end. A profile switch or the session end cancels the turn (§7.5). The worker
  checks the binding before every read and publication.

### 7.3 Tools and page coverage

The tool set is fixed. Each tool is switched on per agent version.

| Tool | Behavior |
| --- | --- |
| Read current page | Dispatches the stored page report to that page's read tool |
| Read trades | Blotter rows, holdings with execution P&L, and trade detail |
| Read trade parties | Legal entities and counterparties |
| Read market data | Curves, contract definitions, dated contracts and observations |
| Read schedules | Published date schedules and calendars, TOU products and power hours |
| Read reference data | The reference-catalog read behind Reference data (today the `references` and `unitCatalog` parts of `GET /workspace`) |
| Navigate | Requests a move to a server-issued target |
| Show card | Creates an application-drawn card from this turn's reads (§10.3) |

| Page | Tool | Permissions | Context rule |
| --- | --- | --- | --- |
| Curves & prices | Read market data | `ReadMarketData` | Pinned |
| Contract definitions | Read market data | `ReadMarketData` | Pinned |
| Schedules | Read schedules | `ReadMarketData` | Pinned |
| Power TOUs | Read schedules | `ReadMarketData` | Current-only; power hours need the pinned frame |
| Reference data | Read reference data | `ReadMarketData` | Pinned |
| Entities & counterparties | Read trade parties | `ReadTradeParties`, `ReadMarketData` | Pinned |
| Blotter rows and holdings | Read trades | `ReadTrades`, `ReadMarketData` | Pinned |
| Blotter risk results | none | | Not supported: calculations are a non-goal |
| Delivery prices | none | | Not supported: no tool reads delivery-price data |
| Users & access, Settings › AI | none | | Not supported |

- Read current page has no permissions of its own. It dispatches to the tool the
  table names; if that tool is off for the version, it is refused with
  `tool_not_enabled`. Grants, consent and the allowed-role check list only the
  permissions of switched-on read tools. Trade and party tools include
  `ReadMarketData`, which `security.trading_access` and binding the pinned market
  context require.
- On an unsupported page, Read current page returns `page_not_supported`, the
  "Looking at" line says the assistant cannot read this page, and the agent never
  answers from another page's data.
- Read tools call session-independent operations extracted from the page handlers
  (§12 step 2), so pages and tools share one implementation and authorization path.
  Each tool's contract is the operation's contract (architecture §10): versioned
  identifier, input and output schema and permission, plus the runtime set's row and
  byte bounds. A result over a bound fails with `result_too_large` and is never
  truncated or paged; paging is deferred. The model may retry with narrower inputs
  the schema accepts (for example the blotter's book and portfolio filters). When
  the schema cannot narrow (a long party list: `PartyReadRequest` takes only a
  snapshot and kind), the agent cannot read it, the step line shows the code and the
  page still shows everything. Page results do not change.
- A tool step ends one of three ways:
  - a result;
  - a refusal returned to the model as the step result: `page_not_supported`,
    `snapshot_missing`, `power_historical`, `result_too_large`, `card_too_large`,
    `card_invalid`, `tool_arguments_invalid`, `tool_not_enabled` or
    `page_selection_unavailable`. It carries only the code and fixed server text,
    shows on the step line, and the turn continues;
  - any other failure ends the turn: authority, session and fencing failures with
    their own codes, and anything else (a command timeout, a database error) failed
    with `tool_failed`.

  A refusal counts as a tool step, so refusal loops end at `limit_tool_steps`.
- A read whose operation ran under its access check is recorded as a tool read even
  when it then fails (`result_too_large`, `page_selection_unavailable`), with the
  failure code and access rows and no result, and joins the source set. A refusal
  before the operation runs adds no source.
- Results carry exact values and server-formatted strings (D114). The preamble
  requires the agent to quote only returned values and derive none. Prose is model
  output; cards are the exact channel.
- **Navigation targets** are issued by the server; the model never composes one. A
  page target names a workspace page the user can currently open (checked under the
  turn's agent context against the delegator's current permissions) and keeps the
  pinned context. Record, filter and context targets come only from this turn's reads.
  Targets open read views only, never booking or amendment editors or prefilled
  fields.

### 7.4 Execution and ownership

- Turns run in an in-process worker, an interim path until the Milestone 4 durable-run
  mechanism. States follow architecture §11 (queued, running, cancel requested,
  completed, failed, cancelled) plus interrupted. Terminal transitions are atomic and
  exclusive.
- A turn completes only when its last model call ends normally with no pending tool
  request and every check and publication succeeded. Cancellation (Stop, Disable,
  Retire, revocation, session end) ends it cancelled. Recovery ends it interrupted.
  Every other ending is failed, including every limit, provider and check code, even
  when text was published; that text stays marked incomplete.
- **One worker per database.** A local-preview process takes a session-level
  advisory lock in an agent-worker lock class on a dedicated connection, held for its
  lifetime, and advances the worker generation. If it cannot take the lock, or loses
  that connection, its assistant and provider-test routes refuse with
  `agent_runtime_unavailable`. After taking it, and before serving agent work, it ends
  every non-terminal turn of an earlier generation as interrupted, marks their open
  calls `usage_unknown` and closes open provider-test records as interrupted, each
  exactly once. Admission and every turn write require the turn's generation to be
  current. Phase 3 replaces this with multi-process leases.
- **Admission** takes a transaction-scoped advisory lock keyed on the workspace, then
  counts: one active turn (queued, running or cancel requested) per user per
  workspace, and the runtime set's workspace limit (`turn_active`,
  `workspace_turn_limit`). It also ends, exactly once and as interrupted, any turn of
  the current generation still non-terminal past its elapsed-time limit plus the
  admission deadline, so a stuck turn cannot hold slots.
- The worker tracks the queued turns its process admitted. One still without a
  pinned-context record at the admission deadline ends failed with `ask_incomplete`;
  one moved to cancel requested before starting ends cancelled. Each ending frees
  both slots.
- Agent work uses its own runtime and authenticator data sources per scope on the
  existing logins, never the UI pools. Workspace concurrency stays below the agent
  pool size. Each tool read fits one 5-second command in one request context.
- No transaction, request context or epoch lock is held across a model call, a
  backoff or a stream write. Text deltas are coalesced into batches, each with one
  publication recheck.
- Conversation, turn, event, tool-read, card, target, model-call and usage writes are
  narrow security-definer operations. Each worker write presents the turn's fencing
  token, held only in the admitting process; the database stores its digest. The
  pinned-context write is accepted only from queued with no record and the start only
  from queued. Content writes and call opens are accepted only while running. Call
  closing, usage, cost and terminal events are accepted in any non-terminal state and
  need no request context, so accounting finishes after revocation or session end.
  From cancel requested the only terminal states are cancelled or interrupted.
- One narrow operation without the token moves a queued or running turn to cancel
  requested. Stop (owner's context), Disable, Retire, revocation and the
  profile-switch handler use it.

### 7.5 Limits and failure

- Each agent version declares, and each turn records: maximum tool steps; maximum
  total input tokens per turn; maximum output tokens per call (not above the model's,
  `model_limit_exceeded`); maximum elapsed time; retry count; and the caching switch.
  Exceeding one ends the turn with `limit_tool_steps`, `limit_input_tokens`,
  `limit_elapsed` or `limit_output_tokens`. A tool request cut off by a limit is not
  run.
- The input limit is checked before each attempt against the uncached input, cache
  read and cache write that earlier attempts reported. No estimate is made, so the
  crossing attempt completes and is billed. If an earlier attempt's input is unknown
  or unrecognized, the turn ends with `limit_input_unverifiable`. An attempt's input
  is known zero only if its transport failed before any byte was sent, or it got,
  before any stream event, an error status the mapping declares not billed.
- **Runtime set.** Versioned, shipped with the application, recorded on each turn,
  never host configuration: workspace concurrency; agent pool sizes; retry backoff;
  replay window; stream heartbeat interval (below 20 seconds); text coalescing
  interval; card data size; event size; maximum question and instruction length;
  tool-result row and byte bounds; maximum grant duration; provider test output limit
  and deadline; admission deadline (above the 15-second request deadline);
  retained-context reserve; provider connect and idle timeouts; provider response byte
  bound; stream subscriber buffer.
- `question_too_long` and `instructions_too_long` are set so a complete body stays
  under 32 KB and gets the structured error. A card over the card data size is
  refused with `card_too_large`. Text is split into events within the event size;
  any other oversize event fails the turn with `event_too_large`. Nothing is
  truncated.
- Provider errors are classified as authentication, rate limit, overloaded, invalid
  request, context window exceeded or transport (connect and idle timeouts included).
  Only transport and overload are retried, on the same provider and model, only when
  the failed attempt published nothing and the input check allows. Every attempt has
  its own usage record.
- Before publication, the remaining errors end the turn with
  `model_provider_authentication`, `model_provider_rate_limited`,
  `model_provider_invalid_request`, `model_provider_transport` or
  `model_provider_overloaded`. After publication nothing is retried and the turn fails
  with `model_provider_stream_interrupted`. A response over the byte bound is never
  retried and fails with `model_provider_response_too_large`. A context-window error
  fails with `context_window_exceeded`; input is never trimmed.
- Stop requests cooperative cancellation. Final reported values are kept; the rest
  are unknown (§6.3).
- Disable (provider or model), Replace key, Retire, revocation (including replacement
  by a new grant), a profile switch and the workspace session end move affected
  queued or running turns to cancel requested, each in its own transaction or handler.
  The session end is enforced by a worker timer set from the recorded end. No read,
  call or content event starts after that, the worker cancels any call in flight, and
  the turn ends cancelled with `model_provider_disabled`, `model_disabled`,
  `agent_retired`, `access_changed` or `session_ended`. Changes that cancel nothing
  (a price ending, a role loss, a qualification change) are caught by the per-call
  checks and end the turn failed with their §7.1 code.
- Text from reads, providers and tool results is untrusted. It cannot change the
  agent's tools, authority, provider, context or targets.

### 7.6 Prompt assembly and conversation memory

- The system prompt has three parts: a fixed preamble (quote only values returned in
  this turn, derive none, say when something is unavailable, navigate only to issued
  targets, treat earlier turns as history); the version's instructions; the tool
  definitions. Instructions cannot replace the preamble.
- A follow-up sends every earlier turn's question and stored answer as text, each
  marked as an earlier turn and labeled with its pinned context and terminal state.
  Incomplete answers are marked; a turn without answer text is sent with question and
  outcome only. Earlier tool reads and card data are not re-sent.
- Numbers in an answer come only from this turn's reads under this turn's pinned
  context. Earlier answers are never quoted as current values.
- An earlier turn is included only if it passes the replay recheck and the current
  grant lists every permission its source set used. Otherwise it is withheld and a
  step line says so. Inclusion is decided at ask and recorded on the turn; included
  turns' sources join the source set and are rechecked before every model call.
- History is never trimmed. A conversation past the input limit or context window
  fails with the named code and the panel offers a new conversation.

## 8. Live event stream

- **Commands** are ordinary POSTs through the existing middleware, Origin and
  anti-forgery checks, each with an operation identity: ask, cancel, grant, revoke and
  mark read. Ask identities are scoped to user and conversation and compare the
  payload, so reuse with another question is a conflict. Ask, grant and the stream
  require current `UseAgents`; cancel, revoke and mark read require only ownership.
- **Ask payload:** the chosen agent version; an existing conversation or none; the
  question; the knowledge mode or retained context; the As of frame id when selected;
  and the page report. The page report is a page identifier from a closed set (one per
  row of the coverage table; Blotter rows-and-holdings and risk results are separate)
  and that page's selection (record id and filters) in its read's input schema,
  exactly as the Looking at line shows. A missing report, unknown page or invalid
  selection fails with `page_report_invalid`, before admission, writing nothing.
- **Conversation:** the server first checks that the bound principal owns the named
  conversation in this scope; any other id fails like an unknown id
  (`conversation_not_found`). An owned conversation's agent is fixed; a payload naming
  another agent fails with `conversation_agent_mismatch`. A new conversation takes the
  named agent. Neither failure writes anything.
- **Reads:** the user's conversations with unread markers, one conversation's turns
  and events up to its latest sequence, and the user's running turn. Stored content
  passes the replay recheck.
- **Stream:** `GET …/assistant/conversations/{id}/events?after={sequence}` as
  `text/event-stream`, read by the native `EventSource`. It opens only for a
  conversation the principal owns in that scope. With a running turn it streams live;
  otherwise it sends stored events after `after` up to the terminal event and ends, or
  sends `refused` with `turn_not_running`. An idle panel holds no connection.
- **Resume.** Event ids are per-conversation sequences. Automatic reconnects resume
  with `Last-Event-ID`; a new stream passes `after` (the latest rendered sequence, or
  0). When `after` is beyond the latest sequence, or the next event is older than the
  replay window, the stream sends `refused` with `replay_unavailable`; the panel
  re-reads and opens one new stream. The server never silently restarts from the
  current position.
- **Events:** accepted, step started or finished, text delta, card, navigation target,
  usage and cost; terminal completed, failed, cancelled and interrupted; and stream
  events `replaced` and `refused`. The server ends a 200 stream only after a final
  event (terminal, `replaced` or `refused`). The panel calls `close()` on each, so the
  browser reconnects automatically only after a network drop or a slow-subscriber
  cut.
- **Wire format:** each event, live or replayed, is `id:` with the decimal sequence,
  `event:` with a fixed name, and one `data:` line holding a single-line JSON object
  from the API serializer, then a blank line. Model, read and user text appears only
  inside JSON strings, where CR and LF are escaped. The panel listens only for the
  fixed names and validates each payload against its schema; a failure closes the
  stream and re-reads the conversation once.
- **One stream per running turn.** A new stream for the user's running turn sends the
  older one `replaced` and ends it; the older tab shows the turn continuing elsewhere
  with Resume. A stream for a conversation without a running turn replaces nothing.
- The stream route is exempt from the 15-second deadline and the session semaphore,
  matched by path in the first middleware. If no terminal event is sent by the turn's
  elapsed-time limit, it sends `refused` with `stream_expired`.
- **Admission.** The existing loopback host checks and the GET Origin check apply.
  `EventSource` cannot send the anti-forgery header, so the stream is authorized by
  the scope's SameSite=Strict, HttpOnly session cookie and session lookup, and
  requires `Sec-Fetch-Site: same-origin` and `Sec-Fetch-Mode: cors`. Cookies are not
  port-scoped, so the Sec-Fetch check is what refuses a page on another loopback
  port. It returns no CORS headers. Its only effects are replacing the requester's
  older stream and nothing else.
- Admission failures are non-2xx responses, which stop `EventSource`; the panel
  re-reads once and does not reopen. Later refusals are one `refused` event with the
  code. A missing or expired workspace session gives `session_ended` on assistant
  routes, never `access_denied`; the panel offers reload with no retry loop. Page
  requests keep `access_denied`.
- On open the server flushes headers and an initial comment, then writes a comment
  heartbeat at the stream heartbeat interval while idle. Before each write it checks
  that the workspace session exists and its profile is unchanged; otherwise it sends
  `refused` with `session_ended`.
- An open stream holds no database connection while waiting. At open it subscribes in
  memory, then replays committed events after `after` in one short read with the
  replay recheck, dropping duplicates by sequence. Live events come from the worker in
  memory after commit, already rechecked in their append transaction. Each subscriber
  buffer is bounded; a stream that falls behind is ended without a final event, the one
  deliberate case, so the browser resumes through `Last-Event-ID`.
- Each scope maps its own stream and commands inside `MapPreview`. No new server or
  browser dependency is required.

## 9. Settings › AI

Each tab appears according to §5.4. Lists use the existing table component. Writes
use the existing pending-command pattern and structured codes, except Replace key
(§5.2). Providers, Models & prices and Agents end with a Recent changes table: the
latest 100 model administration audit entries for that tab, under that tab's
permission. Credential entries show only "key replaced".

- **Usage** (`ReadModelUsage`):
  - A date range and a By model / By user / By agent switch. Each request carries a
    reporting time zone, prefilled from the viewer's Profile zone and changeable; a
    missing or unknown zone is refused with `report_zone_invalid`. The range is a
    half-open [from, to) of local dates (D25). The server converts both ends in that
    zone, assigns each call to a day by its start instant, and returns the zone and
    UTC bounds it used. Days of 23 or 25 hours occur.
  - Per-currency totals of cost and tokens; answers (completed turns) and cost per
    answer; the cost of failed, cancelled and interrupted turns on a separate line.
  - A daily cost chart by model and a table of answers, turns by terminal state,
    per-dimension tokens, cost and share (a row's cost over the same-currency total).
  - Unpriced, unknown, unrecognized and superseded-price amounts shown separately.
  - Provider tests as their own row in every view, so views reconcile.
  - CSV export is audited as a usage-export entry (actor, view, range, zone) and
    recent exports are listed. It carries exact costs, sums, counts, per-dimension
    tokens, zone and range; ratios only as `token_cost_report_v1` strings. The
    browser builds it with the formula-guarded CSV writer (today `blotterCsv` in
    `web/src/blotter-layout.ts`), moved into a shared export module.
- **Models & prices** (`ManageModelProviders`):
  - Models per provider with status, rates, any tier, the revision in force and any
    scheduled revision.
  - Add model, Edit model, Enable or Disable, Add price. Every field is entered;
    nothing is inferred from the identifier. Add and Edit create a model version,
    except display-name edits. A model starts disabled; Enable needs an enabled,
    qualified provider and a qualified model. No models or prices are shipped.
  - Models without a price show "No price" and are not offered for agents.
  - Disable cancels running turns that use the model.
- **Providers** (`ManageModelProviders`):
  - The five vendors with protocol, endpoint in use (an override is marked as a check
    endpoint), qualification, terms, account-owner visibility, attestation, credential
    status, last test and enabled state.
  - Replace key, Attest account terms, Test, Enable or Disable. Replace key clears the
    attestation and disables the provider in the same transaction, with Disable's
    effects; only Enable, with a current attestation, resumes calls. The tab states
    that enabling sends users' questions and visible data to the vendor account whose
    key is set, that its owner may see them, and that the vendor account's own
    spending limit is the only cumulative cap.
  - **Test** sends one fixed synthetic prompt, with no workspace data and no tools, to
    a model of that provider the administrator picks. The model needs a price in
    force and may be disabled; the provider may be disabled but must be qualified. The
    administrator enters temperature and reasoning, validated as for an agent version
    (`parameter_required`). Maximum output is the test output limit
    (`model_limit_exceeded`). Other parameters follow §6.1; tool parameters and cache
    breakpoints are recorded as not applicable. One attempt, no retry.
  - The test record opens in the administrator's context under
    `ManageModelProviders` and pins its price revision before any byte is sent. It
    returns a per-test token held only by the handler; counts and close present the
    token and need no request context. Test succeeds only for a well-formed response
    that ends normally or at the output limit with recognized usage, and shows outcome,
    model, latency, counts and cost or `cost_unpriced`.
  - The test deadline keeps open, call and close within the 15-second request
    deadline. At the deadline or an abort the handler cancels the call and closes the
    record under an independent cancellation scope, failing `limit_elapsed` or
    `request_cancelled`. A retry with the same identity never sends a second request:
    it returns the closed outcome, or `test_in_progress`.
- **Agents** (`ManageAgents`):
  - A list and a form: name, picker description, instructions, model, temperature
    where supported, reasoning where adjustable, the §7.5 limits, grant duration, tool
    switches and allowed roles. The form offers only enabled, qualified, priced models
    on enabled, qualified providers. A save names the exact model version shown and
    fails with `model_changed`, `model_disabled`, `model_unqualified`,
    `model_unpriced`, `model_provider_disabled` or `model_provider_unqualified`.
  - Each save creates a draft version. Publish (with the agent's expected version)
    makes the newest draft the published version. Running an unsaved definition is
    refused. An agent whose pinned model version is no longer current shows "Model
    changed" and cannot answer until a new version is saved and published.
  - Retire is permanent (§5.4).

## 10. Assistant panel and cards

### 10.1 Layout

- The panel docks right and the page narrows. Its left edge resizes it by pointer or
  keyboard; the width is a browser-local preference (D45). Below 760 px it becomes a
  full-width sheet.
- A header launcher opens and closes it. The launcher is hidden without `UseAgents`
  or when no agent is usable.
- The panel keeps its conversation across pages and has its own busy state: page
  loading never disables the composer, and agent work never disables the page.
- Closing the panel or reloading does not cancel a turn. The stream stays open while
  the turn runs. After a reload the panel reads the user's running turn and resumes
  with `after`. When a turn finishes while the panel is closed, the launcher shows an
  indicator and History marks the conversation unread until it is opened (mark read
  advances the conversation's read-through sequence).

### 10.2 Contents

- **Header:** agent picker with provider, model, version and a Draft label where it
  applies; History; new conversation; close.
- **Agent choice:** the picker lists only usable agents. The user's last explicit
  choice is a browser-local preference (D45), preselected only while still listed;
  otherwise the picker reads "Choose an agent" and the composer is disabled. A
  conversation is locked to its agent; choosing another starts a new conversation.
- **Looking at:** the current page and selection and the context the next question
  will pin, or that the page is not supported. The ask sends exactly this (§8).
- **Conversation:** questions; collapsible step lines; streamed answers with
  incomplete ones marked; cards; navigation notices with Back; and a per-answer usage
  line (per-dimension tokens and cost, one amount per currency).
- **Consent:** on `grant_required` the panel shows the prompt in §7.1.
- **Composer:** Enter sends, Shift+Enter adds a line, Send becomes Stop during a turn,
  and a failed send keeps the draft with Retry.
- **Formatting:** answers render only paragraphs, lists, emphasis and code, built from
  text nodes. Tables, HTML, links and images are not rendered and URLs are not linked,
  because rendered links let injected text carry data out. Tabular and charted values
  appear only as cards.
- **Navigation:** moves the user and shows "Took you to …" with Back, which restores
  page, record, filters and context. A target that changes context, or a page with
  unsaved editor changes, shows a button instead of moving.

### 10.3 Cards

- The agent submits a typed specification: a kind (table, bar chart, line chart or
  value tiles) and bindings from card fields to fields of this turn's reads. It holds
  no numbers and no free text. Any other field or kind is `card_invalid`.
- The server composes the title from the bound reads' display names and pinned
  context (or a current-only read's instant). Labels, values and display strings come
  only from read schemas and stored reads.
- The browser draws cards with the existing table and chart components (D80), with
  sort, hover and series toggles. On creation the server issues, in the same
  transaction and under the same recheck, the navigation target for each bound row or
  point that has one, and stores them with the card; they count toward the card data
  size. A click uses the stored target and sends no command. A re-materialized card
  shows targets only after the replay recheck. No target is issued after the turn
  ends.
- The footer names the reads and their pinned context, or a current-only read's
  instant. No agent-written code runs anywhere.

## 11. Hosted mode in phase 1, and later phases

Under `--hosted-pilot` phase 1 is inert:

- its routes (assistant, stream, Settings › AI) are not mapped and no worker starts;
- the launcher and Settings › AI are hidden, because the API reports the assistant as
  unavailable in that mode;
- no model-provider HTTP client is registered and no agent pool or worker connection
  is opened;
- `tools/check_hosted_http.py` asserts all three.

Its migrations still ship in every release bundle, so the first hosted release after
phase 1 stops automatic updates until an operator qualifies a new baseline under
D135, even though the feature is off. SQL changes apply in every mode: the new
permissions appear in role listings, Users & access refuses agent principals and the
agent reader role, and `tools/provision_hosted.py` grants the owner an explicit list
of human role codes instead of every row of `security.roles`, because the agent
reader role cannot be held by a human. Phase 1 adds no `host.json` field and no
Compose change.

**Phase 2 (agent notes)** adds notebooks, note permissions on the agent reader role
and the `UseAgents` roles, Search notes and Save note, provenance and a Notes view.
Phase 1 keeps the source set as exact tool-read ids with their access rows, so note
provenance can copy it, and keeps the consent text and tool set extensible.

**Phase 3 (hosted baseline)** adds agent contexts bound to the asking account session,
credential key files in hosted configuration, API egress, gateway stream settings,
multi-process worker leases, and four hosted gates the owner has not decided:
retention (O11), approval to enable a provider, per-catalog data scope (O27, O22)
and API egress control. Phase 1 keeps these open: turns record their session binding
as a typed field, the worker generation is the seed of a lease fence, endpoints are
code constants with no host setting, credentials do not depend on data protection,
and the heartbeat interval already fits the gateway's 20-second read timeout.

## 12. Delivery order

Each step leaves the application working with no provider configured and keeps the
hosted inert check green.

1. Record the decisions in §14: in decisions.md first remove the blank lines that
   split the agreed-decisions table (after D77, D96 and D99, and the two before
   D132), then append. Amend AGENTS.md and update plan.md.
2. Extract session-independent typed read operations from the page handlers. They take
   an issued request context and a pinned-context record, never `HttpContext`.
   Existing pages switch to them with every existing check green.
3. Provider, credential, model, price and audit tables; `ManageModelProviders`;
   credential custody in `run_preview.py`; model and price operations; Models &
   prices and Providers tabs with Replace key. Every provider is unqualified.
4. The fake provider under `tests/`; adapters with sourced usage mappings;
   conformance; usage normalization and cost; model-call and usage tables for tests;
   qualification and attestation; Test, Enable and Disable. Confirm Meta's usage fields
   with source and date, or Meta stays unqualified.
5. Agents, access and turns: agent, version, grant audit, conversation, turn, event,
   tool-read and worker-generation tables; `ManageAgents`, `UseAgents`,
   `ReadModelUsage` and the agent reader role; agent creation, Publish and Retire;
   the Users & access and provisioning changes; grants; context pinning with the
   reserve; read tools and Navigate; the worker and agent pools; the stream; turn
   usage; the Agents and Usage tabs; and the changed existing checks (§13).
6. The panel with consent, the profile dialog's grants with Revoke, Users & access
   grant history, client navigation history and the background indicator.
7. Cards and Show card.
8. Architecture updates and a cross-document consistency pass.

Each step updates in the same publication every document its change makes
inaccurate: db/README.md for migrations; security.md for permissions, roles and
agent principals; reference-data.md for price revisions as a consumer of currencies;
and hosted-pilot.md for the inert hosted state, the provisioning change and the
qualification the migrations require.

## 13. Verification

- **C# checks:** cost (tier at, below and above the threshold with cache tokens
  counted, unknown prompt dimension with and without a tier, effective time,
  append-order supersession, a revision appended during an open call leaving its
  price unchanged, unpriced and unknown, totals by exact reference including two
  revisions of one code, display and report formats); usage mappings (structural
  zeros, final values, not-billed statuses, cancelled streams); input-limit
  enforcement (crossing attempt, unknown input blocking a retry, pre-send failure,
  not-billed overload allowing one, a reset after send blocking one); prompt assembly
  and history inclusion; credential redaction before SQL; Usage days across a
  daylight-saving change and a missing zone refused.
- **Adapter conformance** against the fake provider (§6.4).
- **HTTP checks:**
  - idempotent retries, payload conflicts and stale versions;
  - denials and `agent_role_lacks_permission`, `grant_permission_missing`,
    `grant_duration_exceeded`, `agent_not_allowed`, `agent_retired`, `agent_managed`;
  - a grant request with any field besides the agent version is rejected; stored
    grants match the version's principal, permissions and expiry; revoke of another
    user's id looks like an unknown id and writes nothing; audit is atomic;
  - grant coverage: a new version with the same provider, model identity and
    permissions asks nothing; one changing the model identity, or adding a
    permission, gets `grant_required`;
  - drafts: usable only by `ManageAgents` holders with `UseAgents` and the reads;
    hidden from other users until Publish; an unsaved definition cannot run;
  - credentials absent from audit, responses, logs, errors, events and the fake's
    request body; Replace key leaves the provider disabled and Attest alone does not
    re-enable it; model identifiers with `/`, `..`, `:`, `?`, `#` or `%`, starting
    `https:` or over 128 characters fail with `model_identifier_invalid`;
  - agent save after a provider Disable, a model Disable and a model version change
    fails with the matching code, and a save naming the current version clears
    "Model changed";
  - asks naming another user's conversation (`conversation_not_found`) or another
    agent (`conversation_agent_mismatch`), and asks with a missing or invalid page
    report (`page_report_invalid`), write nothing and reach no provider;
  - provider test: idempotency, refusals, parameters received, a stall past the test
    deadline returning `limit_elapsed` with unknown dimensions and no second request
    on retry, an abort still closing the record, losing the permission mid-test still
    recording usage, a count write without the token rejected;
  - a read over a bound fails with `result_too_large`, the fake receives only the code
    and the page still returns the full result;
  - stream admission: no cookie, a different Origin or missing Sec-Fetch headers get
    403 and no CORS headers; `replaced`, `turn_not_running`, `replay_unavailable`,
    `stream_expired`; no reconnect after a final event; `session_ended` from assistant
    routes for a missing session;
  - with an empty manifest (`frames: []`): an ask on Reference data succeeds; on
    Entities & counterparties the party step is refused with `snapshot_missing`, the
    fake receives that refusal and the turn completes.
- **Runtime checks** (against the fake provider; restarts use one start, readiness and
  stop helper shared with `run_preview.py`):
  - a turn reads only under its owner's grant and is cancelled with `access_changed`
    when that grant is revoked or replaced, while other users' grants remain;
  - losing the last `UseAgents` role stops new turns and fails a running one, and
    revoke still works;
  - provider disabled, model disabled, agent retired, grant revoked, key replaced,
    price ended and model version changed between two calls and during a backoff: no
    further call reaches the fake, and the turn ends with the matching state and code;
  - a revoke during a model call completes without waiting and no content event
    commits after it; removing a catalog scope or source permission mid-turn ends the
    turn with `access_changed` and stops further calls;
  - a profile switch and the session end during a turn and during a silent call each
    cancel the turn and close its stream with `session_ended`, with usage recorded;
  - context pinning when the selection changes mid-turn; navigating mid-turn (Navigate
    included) leaves Read current page on the asked page; a missing selection returns
    `page_selection_unavailable` and the turn continues;
  - queued turns: an abort right after admission, a capture held on lock 730849102
    past the request deadline, and a fault in the record write each end the turn and a
    new ask is admitted within the admission deadline; Stop on a queued turn ends it
    cancelled; a record write after cancel requested is rejected;
  - retained contexts: refused asks add no context; a failed capture frees the slot;
    at the reserve an ask fails with `knowledge_budget_reserved` while a current-mode
    `/date-schedules/context` capture still succeeds;
  - publication and replay rechecks, including removing a scope and replaying; a
    retired agent's turn still replays;
  - history: context change, revocation and a narrower grant between turns; earlier
    cancelled, failed and interrupted turns sent with outcome and marked answer;
  - limits, retries before publication, none after, each error class, retry
    exhaustion, `context_window_exceeded`, `model_mismatch`, unrecognized usage storing
    no cost, the output limit marking text incomplete, `question_too_long`,
    `instructions_too_long`, `card_too_large`, `event_too_large`;
  - each tool refusal reaches the fake as a step result; a command timeout ends with
    `tool_failed`; a refusal loop ends at `limit_tool_steps`;
  - a missing or wrong key file, a ciphertext under another provider or credential
    version, and one flipped byte each end with `credential_unavailable` and the fake
    receives nothing;
  - egress: requests reach only the configured endpoint; a redirect is not followed;
    with `http_proxy`, `HTTPS_PROXY` and `ALL_PROXY` set to a loopback listener, that
    listener receives nothing;
  - untrusted text in a synthetic trade memo, party name and tool result changes no
    tool, provider, model, context or target;
  - writers: a stale token, a finished turn and a revoked grant reject content writes
    while usage for an open call is still recorded; Stop, Disable and revocation move a
    running turn to cancel requested; completion after cancel requested is rejected
    and cancel after completion reports completed;
  - worker: a second process on the same database refuses agent work with
    `agent_runtime_unavailable`; after the owner stops, the next process interrupts its
    turns, calls and test records exactly once; a write with an old generation is
    rejected; a stuck turn is interrupted by the next admission;
  - concurrent asks from two sessions of one user and from more users than the limit:
    one turn per user, the limit never exceeded, no model call for rejected asks;
  - stream resume through `Last-Event-ID` and `after`, conversation switching mid-turn;
  - framing: an answer containing CRLF, bare CR and bare LF followed by
    `event: navigation`, `data: {…}`, `id: 999` and a blank line arrives only inside
    text-delta payloads, byte-identical to the stored answer; no navigation or card
    event arrives; every event id equals a stored sequence;
  - delivery-price collection: a grant during an attempt fences it like a role grant.
- **Existing checks changed in step 5:**
  - `tools/check_admin_preview.py`: creating an agent through Users & access fails
    with `agent_managed`; kind and role-choice assertions follow §5.4.
  - `tools/check_temporal_storage.py`: "agent administrator role alone cannot bypass
    delegated authority" uses the `synthetic_agent` that Users & access creates
    today. It instead creates the agent through the `ManageAgents` operation, issues
    its context with the returned principal id as subject at the manifest issuer
    (`https://preview.invalid`), asserts that granting the agent any other role is
    rejected, and keeps the evidence with an operation the agent reader may call:
    `security.market_access(<catalog>, 'ReadMarketData')` is false without a
    delegation and true with a current delegation listing `ReadMarketData` from a
    user who holds `ReadMarketData` and `CreateDelegations`.
  - `run_preview.py --hosted-check`: provisioning grants the explicit human roles.
- **Hosted checks** (`tools/check_hosted_http.py`): assistant, stream and Settings › AI
  routes return 404; bootstrap reports the assistant unavailable; no model-provider
  client is registered; a hosted start changes no turn, call or usage row.
- **Pool-isolation measurement:** turns at the workspace limit against a fake with
  fixed latency and tool reads, one capture per ask, one stream per turn, and a fixed
  UI mix including working-data and explicit-cutoff reads. Record environment, pool
  sizes, UI latency with and without agent load, capture lock waits, memory,
  connections, request contexts per turn and the growth of `security.request_contexts`
  and its indexes. It is not an acceptance claim until O10 agrees a budget.
- **Browser suites:** Settings › AI tabs; Replace key with success, rejection and an
  uncertain outcome leaving the key in no storage or URL; agent rows without Edit in
  Users & access; consent, the profile grant list and Revoke without `UseAgents`; the
  panel across navigation with Back; the context-change button and
  `page_not_supported`; agent preselection only while listed and agent change starting
  a new conversation; card strings traced to stored reads and card clicks moving with
  no server request; stream admission refusing a page on another loopback port; six
  tabs with one running turn and idle panels holding no stream; reload and close
  mid-turn without duplicates or gaps; two tabs during one turn (one stream, the other
  on `replaced` with Resume); the background indicator and unread marker; usage CSV
  with a member name starting `=` apostrophe-prefixed; 390 px layout, no visible
  identifiers and no uncaught errors.
- **Independent adversarial check** before each step is published (AGENTS.md):
  authorization and temporal context (steps 2–7, including the provider test token
  and the `ManageModelProviders` checks on Test and Enable in step 4, and Back and the
  context-change button in step 6); numerical correctness (cost, tiers, revisions and
  normalization in step 4; turn usage, totals and ratios in step 5; card values in
  step 7); async execution (the test deadline, its independent close and same-identity
  retries in step 4; worker states, fencing, generation recovery, cancellation,
  retries and stream replay in step 5; panel resume in step 6); credential custody and
  data routing (steps 3 and 4); and the release boundary for every step that adds a
  migration (D135). A final check covers the complete phase.

## 14. Decisions to record

Product-owner decisions become D rows. Design calls become P rows until the owner
approves them. Entries are numbered when recorded.

**AGENTS.md exception** (owner): model-provider vendor names (Anthropic, OpenAI,
Google, xAI, Meta) and their public API identifiers may appear in source,
documentation, fixtures and commits; real prices are not shipped.

**Owner decisions (D rows):**

- three phases (local-preview core, agent notes, hosted baseline); phase 1 runs only
  under `--local-preview` and is inert under `--hosted-pilot`;
- a docked panel; page questions plus navigation; tools beyond the fixed set later;
- Settings › AI with Usage, Models & prices, Providers and Agents, and the Agents form
  as mocked, with saved drafts and Publish replacing its "Test in pop-up"; running an
  unsaved definition is refused;
- application-drawn cards of four kinds (table, bar, line, value tiles); no embedded
  page views; no agent-written code;
- server-sent events, not SignalR;
- day-over-day and period P&L comparison is the next slice; monthly spending caps a
  later slice; phase 1 has per-turn limits and full usage and cost reporting (a
  deviation from architecture §9 Providers budgets and Milestone 5 resource budgets);
- conversation memory as in §7.6;
- only `ManageAgents` holders create agents; users pick from agents allowed for their
  role; trader, risk analyst and market data editor get `UseAgents`;
- no tool reads delivery-price data;
- a new version that changes provider or model asks for a new grant; a maximum grant
  duration in the runtime set applies to every version;
- model administrators get `ReadMarketData` through an operator-assigned role;
  otherwise price editing returns `currency_unavailable`;
- replay needs only the user's own current access to the turn's sources;
- the last explicit agent choice is a browser-local preference; a conversation is
  locked to one agent;
- Meta ships visible and not enableable until its usage fields are confirmed;
- answers are completed turns; other turns' cost is a separate line;
- Usage days use a viewer-picked zone prefilled from Profile, over half-open ranges;
- a background completion indicator and unread History marker.

**Open-item updates:**

- O04, partial: agent principals created only through `ManageAgents`; standing,
  revocable delegations with explicit duration. Redelegation, MFA and federation stay
  open.
- O09, partial: three protocol adapters and five vendors are a design call; O09 stays
  open until real task outcomes are recorded.
- O07: add "reclaim the retained contexts that assistant asks consume".
- O10: phase 1 performance acceptance depends on it.

**Design calls (P rows):**

- credential custody: sealed ciphertexts under fresh random nonces, two key files
  generated by the preview runner, write-only API, redaction before SQL, a Replace key
  form that never persists the key, Replace key clearing attestation and disabling;
- provider calls only to fixed vendor endpoints, a loopback-only preview override, the
  identifier grammar, error bodies never kept, ambient proxies ignored;
- provider enablement requirements (sourced mapping, published terms, account-owner
  visibility, attestation bound to the credential) and model enablement requirements;
- explicit model parameters, disabled response storage, non-moving identifiers and
  caching per protocol;
- the four permissions and initial roles; the agent reader role restricted to agent
  principals (amends D42, seven role templates); the Users & access and provisioning
  changes;
- grants derived from the agent version, coverage by provider, model identity and
  permissions ("model" read as model identity), one current grant per user and agent,
  pre-checked exclusive admission, and the existing epoch-advancing trigger kept;
- drafts usable by `ManageAgents` holders; only the newest version can be a draft;
- the pinned-context record, working-data capture after admission and the
  retained-context reserve; current-only reads labeled with their read instant;
- the page coverage table, Read current page dispatch and the stored page report;
- tool-result bounds without paging; refusals returned as step results; the
  terminal-state rule;
- server-issued navigation targets; card specifications without agent text, with
  targets stored at creation; the context-change button; answers without tables,
  links or images;
- effective-dated prices pinned at call open; `token_cost_v1`,
  `token_cost_display_v1`, `token_cost_report_v1`; totals by exact reference; usage
  days by call start instant; administrator-entered models with no seeded catalog,
  synthetic identifiers in checks and fixtures, display name as an identity field;
- the per-turn limit set and runtime set;
- the in-process worker: one worker per database by advisory lock and generation,
  fencing tokens, serialized admission, the stuck-turn backstop, the interrupted
  state; cancellation sources;
- the publication, replay and source-access checks; history never trimmed;
- the stream: final events, one stream per running turn, in-memory delivery, the
  deadline and semaphore exemption, cookie plus Sec-Fetch admission, the single-line
  JSON wire format, `session_ended` on assistant routes;
- the ask's conversation ownership and fixed agent;
- the provider test deadline, token and recovery;
- the read-handler extraction as the first engineering step;
- deferral of architecture §9 Operations items.

**plan.md:** this Milestone 5 slice is brought forward in three phases; day-over-day
and period P&L comparison is the next slice and monthly spending caps a later one.

## 15. Left to the builder

- Table and column names, migration split per step and SQL function boundaries.
- The order of handler extraction, provided pages and tools share each operation.
- The values in the runtime set, within the constraints stated.
- The formatting renderer and the navigation history structure.

## 16. Who decided what

§14 is the single record.

- **The product owner decided:** the AGENTS.md exception and every entry under §14
  "Owner decisions".
- **Design calls open to correction:** every entry under §14 "Design calls", and the
  O04 and O09 partial resolutions apart from `ManageAgents`-only creation.
- **Not yet decided by the product owner:** the four hosted gates, which belong to
  phase 3.
