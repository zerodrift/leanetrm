# Page assistant, phase 1: local-preview core

Status: design proposal; implementation follows §12. Step 2 is complete for the §7.3
read tools: the market and reference page reads (`MarketReads`) and the schedule
library, TOU product and power hour catalog reads (`ScheduleReads`), extracted under a
page-view `PinnedContext` (a retained context, or working data that binds nothing), and
the trade party read (`PartyReads`) and the trading workspace, Blotter and trade detail
reads (`TradeReads`), which bind the dated cutoff of a `TradingView`: for working data a
context captured from current knowledge at the frame's date or, for trades, any context
captured earlier for that date, otherwise the retained context. The other page
operations stay page handlers because no tool reads them: session drafts (contract
shortcuts and draft date schedules), calculations (the power hour calculation and date
resolution), the date picker's projection of the library and the other trading reads
(catalogs, the futures-only Blotter and holdings, deal formats, drafts, previews and
risk), which run on the trade reads' shared path. From step 3, `ManageModelProviders`,
provider and write-only credential storage with key custody, model identities and
effective-dated price revisions are implemented, with the Providers and Models & prices
reads and Replace key, Add model, Rename, Add price and Withdraw price, on those two
Settings › AI tabs. Step 4 has begun with the provider-neutral boundary (§6.1) and the
Messages adapter with its usage mapping `messages_v1` (§6.2), which pass the Messages
conformance suite against a fake provider (§6.3), and with provider qualification: the
confirmed usage mapping each adapter applies, sourced and dated terms versions,
attestations bound to the current key and terms, and provider Enable and Disable
(§6.3), and model capability versions with model Enable and Disable (§5.1, §6.3), each
through the API and on the Settings › AI tabs: a provider's card records terms,
attests and enables it, and a model's card records capabilities under the efforts its
provider's applied mapping states and enables it. Model Enable is the first consumer of
a provider's enabled state; calls will consume both. Test, through the API, sends one
fixed synthetic prompt through the model's adapter and records the call, its outcome,
usage by dimension and exact cost (`token_cost_v1`) under the agent worker's generation
(§5.1, §5.3, §7.4, §9). A provider's card tests one of its models, retaining a test whose
outcome was not confirmed, and the Providers tab shows each vendor's endpoint, marked when
it is the check endpoint, and its last test, marked when it ran under a key or
terms no longer attested. From step 5, agent administration works through the API:
`ManageAgents` and `UseAgents`, the agent reader role held only by agent principals,
agents with immutable versions, and create, save, Publish and Retire (§5.4, §9), while
Users & access no longer creates or changes agents. Settings › AI offers each tab by its
own permission, and its Agents tab creates agents, saves drafts, publishes, marks an agent
whose published model version is no longer current, and retires. Users grant an agent
version its reads and revoke their grants through the API (§7.1): one current grant per
user and agent, covering later versions with the same model identity and permissions,
with a grant audit, and Retire revokes an agent's open grants. In the profile dialog a
person reads their grants in force when they ask and revokes them, and Users & access
shows those who administer a workspace's access every grant in force there and the
latest grant audit records, read-only. Turns are recorded in the database (§5.1, §7.2,
§7.4, §7.5): an ask in the person's own context with admission, the pinned-context
record and its reserve, a turn's one call with its checks, retries and input limit,
cancellation and its sources, recovery at a worker start and the owner's reads, all
checked in SQL. Through the API a person asks, stops and reads their
turns, and the in-process worker runs each turn its process admitted: one call without
tools, retried as §7.5 allows, streamed within the turn's elapsed limit, its text
published in batches the database rechecks, then its usage closed and its ending
recorded (§7.4–§7.6). Tool steps are recorded in the database (§7.1, §7.3, §7.5,
§7.6): the requests of a call that stopped for tools answered by steps in order before
the tool loop's next call, each read's sources joining the turn's source set, the
source-access check in the agent's authority before every call and publication, the
replay recheck on the owner's reads, earlier turns included only while their sources
pass, and the tool-step, retry and input limits across the loop, all checked in SQL.
The worker runs the tool loop with the six read tools and Navigate (§7.3): a version's
switched-on tools are offered, and each request is answered by a step whose read runs in
a request context issued to the turn's agent under its grant, on agent pools of its own,
at the turn's pin; Read current page dispatches the page report checked at ask (§8),
and results carry the page's values and display. Each result names the navigation
targets it issues by handles numbered across the turn, and Navigate opens a page the
assistant reads or an issued target once a request context issued to the agent opens
its page: the target, which keeps the turn's pinned context, is published as a
navigation event right after its step, and the owner's reads hide it as they hide
text (D190). Through the API a person streams their conversation's latest turn (§8):
its stored events after a sequence, read in their own request context with the replay
recheck, then each piece of answer text the worker publishes once its publication
recheck has committed, with every other event, steps included, read the same way,
through to the turn's terminal event, with resume, heartbeats, one stream per turn and
the refusals §8 lists; the runtime set is `turns_v10` (D193, D239, D245, D260, D270). `ReadModelUsage` reads usage
through the API and the Usage tab of Settings › AI (§5.3, §9): calls, tokens and the
exact cost of priced calls by model, user or agent over a half-open range of local dates
in a zone prefilled from Profile, a call falling on the day whose bounds hold its start,
per exact currency reference with provider tests as their own row and a daily chart, and
an audited export of that report as a formula-guarded CSV. From step 6 the panel asks
and answers in the local preview (§10.1, D191): a header launcher, shown to a person who
uses agents and may use one, opens a panel docked beside the narrowed page, resized by
pointer or keyboard, and a full-width sheet below 760 px. Its picker lists the usable
versions with provider, model and a Draft label and preselects the last explicit choice
only while it is listed; Looking at shows the page report and context an ask sends;
consent grants the version and sends the question again; answers stream with collapsible
step lines, a usage line and incomplete answers marked, drawn from text nodes. A live
target that keeps the page's context and leaves nothing unsaved moves the page, with a
notice and Back, and any other offers Go, which first selects its context, a retained
view again by its id. A tab keeps an unfinished turn's stream while the panel is closed
and lights the launcher when it ends, which History marks unread; Stop, Retry, Resume
after a newer stream and a reload offer once the session ends follow §8. Show card is
not implemented. Phase 3 runs all of this under `--hosted-pilot` too (§11, D192): the
signed-in account asks, its turns and streams end with its sign-in session, the
installation's `host.json` names its credential key files, and the API sends model
requests only to a qualified provider's fixed endpoint, a turn's only once it is
enabled.

The page assistant ships in three phases:

1. **Local-preview core** (this document): Settings › AI, model provider adapters,
   usage and cost, agent principals and access grants, context pinning, fixed read
   tools plus Navigate and Show card, an in-process worker, a server-sent event
   stream, the assistant panel, application-drawn cards and conversation memory.
2. **Agent notes** (later design): agent notebooks plus one common notebook that every
   agent can read and write, with versioned, sourced notes and a Notes view (§11).
3. **Hosted baseline** (§11): phase 1 under `--hosted-pilot`, as an installation
   someone else operates runs it.

Phase 1 brings a bounded slice of [Milestone 5](plan.md) forward. It does not satisfy
Milestone 5's acceptance criteria. [architecture.md](architecture.md) §9–§12 and the
agreed decisions in [decisions.md](decisions.md) apply unless §14 records a change.

## 1. Intent and success

A trader on the blotter asks which trades drive a book's execution P&L as of the
selected date. The assistant reads the same data the page shows, under the trader's
own authority and the context pinned at ask, answers with the values it read, opens
the trade that matters and shows a chart of those values. Administrators connect model
providers, allow models, record token prices, define agents and see what each model,
user and agent cost.

Phase 1 succeeds when, in the local preview:

- a user on a supported page (§7.3) opens a docked panel, picks an agent and receives
  a streamed answer grounded in server reads made under that user's authority and
  pinned context;
- the agent moves the user to another page, record or filter, says where and offers
  Back;
- answers can include cards drawn by the application from values the server returned
  in that turn;
- every model call records normalized token usage and an exact cost, or an explicit
  unpriced or unknown state, from the price revision pinned when the call opened;
- administrators manage providers, write-only credentials, models, effective-dated
  prices and agents, and read usage by model, user and agent;
- with no provider enabled no turn calls a model, the launcher is hidden, and existing
  pages, reads and bookings behave as today.

## 2. Scope

**Goals** are the success list above, with five vendors (Anthropic, OpenAI, Google,
xAI, Meta) through three protocol adapters (§6).

**Non-goals:** agent notes (phase 2) and any hosted operation (phase 3); day-over-day
or period P&L comparison and P&L explain (D77, O12), the next slice; monthly spending
caps, a later slice (phase 1 bounds cost per turn and by concurrency); sensitivities
and other calculations; agents that change business state; tools beyond the fixed set,
external tool servers and sub-agents; agent-written code (architecture §12); embedded
page views; HTML, links or images in answer text (tables, D247, are drawn); paging of tool reads;
delivery-price reads; running an unsaved agent definition; billing, attachments,
voice, pop-outs, steering and branching; administrator pause, single-turn cancellation
and run inspection (architecture §9 Operations).

**Anti-goals.** Each is a failure even if everything else works:

- an agent reading or showing anything the asking user cannot currently read, or
  reading a context other than the one pinned at ask (except §7.2's labeled
  current-only definition reads);
- a credential in a log, audit row, SQL parameter, model context, browser storage, URL
  or response;
- business data sent anywhere except the fixed endpoint of an enabled provider;
- a cost of zero for an unpriced or unreported call, or a recorded cost changing after
  a price edit;
- silently switching provider or model after an error;
- a number on a card that the server did not return.

## 3. Constraints from the existing system

Facts the body relies on and does not restate:

- **Host.** The API runs in one mode (`Program.cs`); `--local-preview` requires the
  loopback peer, Host `127.0.0.1` with the port and a matching Origin when sent. The
  first middleware rejects bodies over 32 KB and cancels requests after 15 seconds; a
  semaphore serializes each session's requests. In-memory sessions end 30 minutes
  after creation and do not survive a restart. Two scopes, `/api` and
  `/examples/exchange/api`, each have their own manifest, pools, sessions, cookie and
  anti-forgery scope over one database. Request records reject unlisted JSON members.
  The CSP allows scripts and connections only from the application origin.
- **Context.** The As of selection is an input frame in the session (at most 128).
  `POST /market/context` selects a historical context; `/market/current` or a profile
  switch clears it. Every date-context capture certifies collection batches under
  global advisory lock 730849102 (D134), held to commit; a catalog retains at most
  4096 contexts, never reclaimed (`knowledge_limit`), and each current-mode capture
  adds one.
- **Authorization.** Market and reference page reads, the schedule library, TOU
  product and power hour catalog reads, the trade party read and the trading
  workspace, Blotter and trade detail reads call `MarketReads` or `ScheduleReads` with a
  `PinnedContext`, or `PartyReads` or `TradeReads` with a `TradingView`; other page
  operations are still `MapPreview` handlers bound to the HTTP request, session and
  frames. `security.has_permission` for an agent principal requires the agent's
  permission, an unexpired, unrevoked delegation listing it, the delegator's current
  permission and `CreateDelegations`, and enabled principals; it does not know who
  asked. `security.trading_access` and `security.use_market_context` also require
  `ReadMarketData`. Control-table writes advance `security.epoch`
  (`guard_control_change`), which `security.use_context` holds `FOR SHARE` per request
  and which fences delivery-price collection (P18 requires throughput qualification
  before broader use). `ReadMarketData` is held by every role except access
  administrator, `ReadTrades` by trader and risk analyst, `ReadTradeParties` also by
  market data editor, and `CreateDelegations` by those three and access administrator.
- **Resources.** Each scope has runtime and authenticator pools of eight connections
  with 5-second timeouts. Every request issues a request context, and
  `security.request_contexts` rows are never reclaimed. Money is exact decimal with at
  most 18 fractional digits (P15). Apart from provider Test in the local preview (§9),
  the API makes no outbound calls; only the delivery-price collector does (D133), where
  "provider" means a PJM or ERCOT source.
  A new migration stops automatic hosted updates until an operator qualifies a
  baseline (D135).

## 4. Vocabulary

- **Model provider** ("provider" in this document and in Settings › AI): one of the
  five vendors with its adapter, fixed endpoint, credential and enabled state.
  Delivery-price providers are untouched.
- **Model**: a vendor model identifier an administrator allowed, with immutable
  capability versions. **Price revision**: an immutable, effective-dated rate set for
  one model in one currency.
- **Agent**: a named assistant with its own agent principal (D40). Each save creates
  an immutable **agent version**, a **draft** until Publish makes it **published**.
- **Grant**: a revocable delegation from one user to one agent (§7.1).
- **Conversation**: one user's thread with one agent, made of **turns** (a question
  and the agent's complete response). An **answer** is a completed turn.
- **Source set**: the tool reads whose content reached the model in a turn, including
  reads behind earlier turns sent as history (§7.6), as exact tool-read ids.
- **Model call**: one request attempt to a provider, by a turn or a provider test.

The interface says "agent" and "assistant". Vendor names appear under the AGENTS.md
exception in §14.

## 5. Data and administration

### 5.1 Tables

Tables follow existing conventions: workspace scope, row-level security, exact-version
foreign keys, audited operations with operation identities and expected versions.

| Table | Contents | Mutability |
| --- | --- | --- |
| Providers | Vendor. Versions: the vendor's retention and training-use terms and whether the account owner can view content, each with source and date, for the account type they apply to. State: enabled; attestation bound to the current credential and terms versions | Identity plus immutable versions |
| Credentials | Provider, credential version, nonce, ciphertext, tag, last four characters, set by and at | Replace-only; never readable |
| Confirmed mappings | Vendor, usage-mapping version, confirmation date and sources, and the reasoning efforts its adapter sends, installed by a reviewed migration (§6.3); and, per vendor, the confirmed mapping its adapter applies in the release, which states its efforts | Confirmations and efforts immutable; a release repoints its adapters' mappings |
| Models | Provider, vendor identifier (immutable), display name (audited presentation field). Versions: output limit, reasoning control, identifier-stability source, accepted served identifiers, tool and streaming confirmation, each sourced and dated, and the usage mapping version they were validated under; temperature support and range join with the first mapping that sends temperature (the Messages mapping sends none). State: enabled | Identity plus immutable versions |
| Price revisions | Model identity, kind (rate set or withdrawal); for a rate set, exact currency reference, rates and optional tier; `effective_from`/`effective_to`, append sequence, `recorded_at` | Immutable; corrections and withdrawals append |
| Agents, agent versions | Agent: name, principal, published version, active or retired. Version: description, instructions, exact model version, temperature, reasoning, caching switch, tools, allowed roles, limits (§7.5), grant duration | Head moves forward; versions immutable |
| Model administration audit | Operation identity, actor, target, redacted request, before and after; usage exports | Append-only |
| Grant audit | Operation identity, delegation, delegator, agent principal and version, provider and model identity, permissions, expiry, actor, outcome, time | Append-only |
| Conversations | Owner, agent, scope, read-through sequence | Immutable except read-through |
| Turns | Conversation, agent version, grant, question, answer and incomplete marker, pinned-context record (§7.2), page report, source set, included earlier turns, runtime-set version, limits, state, outcome, worker generation, fencing-token digest, session binding, timestamps | States move forward only |
| Turn events | Ordered stream events | Append-only |
| Tool reads | Result or failure code; exact read-operation version; each catalog with the access function and permission checked; read instant for current-only reads | Immutable |
| Cards | Validated specification, server-composed title, bound tool reads, issued targets | Immutable |
| Model calls | Turn or test (test: administrator, token digest, parameters), attempt, start instant, exact model and provider versions, requested and served identifier, service tier, usage-mapping version, per-dimension counts and states, cost method, price revision, cost, outcome | Immutable once closed |
| Worker generation | Current agent-worker generation (§7.4) | Advanced at worker start |

Prices and the display name belong to the model identity, so a rename never leaves a
model unpriced. Each provider and model identity row also has its own version, which
advances with each audited change to that row (Replace key, Record terms, Attest,
Enable, Disable, a rename, a capability edit) and which
commands name as their expected version. It is not one of the immutable provider or
model versions that agents and calls pin. Grants use the existing
`security.agent_delegations` and `security.delegation_permissions`, with the grant
audit as history.

### 5.2 Credentials

- A key is entered through a dedicated operation. The C# boundary removes it before
  any SQL parameter, log or audit write; the audit keeps a redaction marker and a
  keyed hash of the workspace, request identity, vendor and key, so an identical retry
  is recognized and a different one is a conflict. Malformed input is
  `model_admin_input`, and a body that does not bind, such as one with an unknown or
  repeated field, is `request_format`, or `request_too_large` over 32 KiB
  ([security](security.md#requests-the-api-cannot-read)); reusing a request identity
  for another request or acting on a stale provider version is `model_admin_conflict`;
  an outcome the API cannot confirm is `model_admin_outcome_unknown`.
- The browser holds the key only in the form field and the in-flight request, never in
  storage, a URL or the pending-command record, and clears the field when the request
  settles. After an uncertain outcome the tab shows current credential status; Retry
  asks for the key again under the same operation identity.
- `tools/run_preview.py` generates two 32-byte secrets once per disposable database,
  each in its own private file: an AES-GCM key-encryption key and an HMAC-SHA-256
  retry-hash key, passed to every API start. Each seal uses a fresh 96-bit nonce from
  `RandomNumberGenerator` and a 128-bit tag; associated data is provider identity,
  vendor and credential version. A key file that is unset, missing, a symbolic link,
  not exactly 32 bytes or open to anyone but its owner, and a failed tag, are
  `credential_unavailable`. Nothing is generated at API start, and nothing depends on
  ASP.NET data protection. Rotation is out of scope; losing a file means re-entering
  every key. Under `--hosted-pilot` the installation's `host.json` names the two files,
  which `tools/provision_hosted.py` writes (§11).
- The API returns only "set", the last four characters, who set it and when.
- Keys travel only in headers. Provider clients follow no redirects, use a
  `SocketsHttpHandler` with `UseProxy=false`, keep default certificate validation, and
  take connect and idle timeouts and a response byte bound from the runtime set.
- Each vendor endpoint is a sourced, dated HTTPS constant in its adapter. The API
  accepts one optional override, `OPENETRM_PROVIDER_CHECK_ENDPOINT`, that must be an
  IP-literal loopback URL and that only owned checks set, so they can reach the loopback
  fake provider (`tools/fake_model_provider.py`), which serves only synthetic
  recordings.

### 5.3 Prices and cost

- Rates are exact decimals per million tokens, below 10^12 with at most 12 fractional
  digits, for uncached input, cache read, cache write and output. Uncached input and
  output are always offered; cache read and cache write may be "not offered", except
  cache read on protocols that cache automatically. The Models & prices directory
  states for each model whether cache read is required, by the rule pricing applies.
- Each rate revision binds a currency row of a sealed reference revision. The price
  operation rechecks the actor's current `ReadMarketData` access to that catalog
  (D131), otherwise `currency_unavailable`; choices and Usage labels use the same
  scoped read (D124). Revisions can repeat a currency, so each choice also names its
  catalog and revision.
- An optional long-prompt tier has a threshold, a token count from 1 to below 10^12,
  and a second rate set offering exactly the dimensions the first offers. When uncached
  input + cache read + cache write exceeds the threshold, the second set prices every
  dimension, output included. If the pinned revision has a tier and a prompt dimension
  is unknown, cost is unknown.
- Prices are operational configuration, not certified knowledge (no D67). When a call
  opens (§7.1), its transaction pins the committed revision with the highest append
  sequence whose [`effective_from`, `effective_to`) contains the call's start instant.
  Later appends never reprice it; Usage flags calls priced by a superseded revision.
- A withdrawal revision appends like a correction but carries only its interval, so it
  needs no `ReadMarketData`. A call that pins a withdrawal, or finds no revision, has
  no price in force and is refused (`model_unpriced`). Tokens in a "not offered"
  dimension store no cost (`cost_unpriced`). `usage_unrecognized` and `usage_unknown`
  store no cost, never a partial one.
- `token_cost_v1` computes cost once, in PostgreSQL, whose numeric keeps every cost
  exact at any magnitude, and owns the tier rule. Reasoning
  is an informational subset of output, never priced; reasoning above output is
  `usage_unrecognized`. Costs and sums are exact at 18 digits; `token_cost_display_v1`
  trims and groups, so nonzero never shows as zero; `token_cost_report_v1` gives cost
  per answer and share (18 places, 2-place percentage, ties to even) and is recorded
  with each report.
- Totals group by exact currency reference (D72), never added across references; a
  label adds the reference revision when two totals share a code.
- Regional, fast, priority and cache-storage surcharges are never requested.

### 5.4 Permissions, roles and agent principals

| Permission | Grants | Initial roles |
| --- | --- | --- |
| `ManageModelProviders` | Providers, credentials, models, prices, tests, their audit | Access administrator |
| `ManageAgents` | Agents, versions, Publish, Retire, agent principals, their audit | Access administrator |
| `ReadModelUsage` | Usage reports and audited export | Access administrator |
| `UseAgents` | The panel, grants and asks | Trader, risk analyst, market data editor |

- `UseAgents` goes only to roles holding `CreateDelegations`, which every agent read
  requires. Model administrators need an operator-assigned role with `ReadMarketData`
  to price models.
- A version's allowed roles may contain only roles holding `UseAgents` and every
  permission its switched-on tools need; a database check enforces it
  (`agent_role_lacks_permission`, naming role and permission).
- A new **agent reader** role holds exactly `ReadMarketData`, `ReadTrades` and
  `ReadTradeParties`, from migration 0119 `ReadJobs` (D241), and from migration 0128
  `DraftCustomCurves`, the agent's own authority to save a custom-curve draft it
  claimed for a step of its turn while its delegator holds `WriteMarketDefinitions`
  (D260); no other role holds it, and the role never holds `WriteMarketDefinitions`. A database check allows it only on agent principals and allows
  agent principals no other role. The migration fails, never revokes silently, if an
  agent principal already holds another role.
- Creating an agent is one `ManageAgents` security-definer operation: the principal,
  an `agent_credential` binding at the manifest issuer with the principal id as
  subject, its workspace membership (counted in the 256-member limit) and the agent
  reader grant. No login or password. It appends to `security.administration_audit`.
- Retire disables the principal, revokes its open grants and cancels its running
  turns. It is permanent and keeps history.
- **Control-table admission.** Creation, Retire, grant and revoke write control
  tables. Each first runs all checks in an ordinary short read; refusals and no-ops
  answer from it. Only a command that will write opens an exclusive admission, which
  takes the epoch row `FOR UPDATE` before binding, then repeats the checks. The
  existing `guard_control_change` trigger stays, so these writes fence a
  delivery-price attempt like any role change. Provider, credential, model, price and
  agent-version operations write no control table, use ordinary admission and check
  their permission inside.
- `apply_administration` refuses to create agents and any update targeting an agent
  principal or naming the agent reader role (`agent_managed`). The directory leaves
  that role out of assignable choices and shows agent rows without Edit.
- Choosing providers decides which vendor account receives questions, tool results and
  answers; editing instructions changes what an agent reads within each grant. Both
  are audited; neither grants an application read permission.
- Usage shows users, agents, models, counts and cost, never content. Users see only
  their own conversations and the cost of their own turns.

## 6. Provider adapters

The [implemented mappings and limits](provider-adapters.md) include D210: Messages
for Anthropic, Responses for OpenAI, xAI and Meta, and streamGenerateContent for
Google. Conformance and owned HTTP checks are synthetic; real-provider task
qualification remains open.


### 6.1 Contract

- A provider-neutral boundary (D06) streams a response for normalized messages, a
  system instruction, tool definitions and explicit parameters, with cancellation. It
  yields text deltas, then exactly one ending: a completion with its stop reason (end
  of turn, tool use, output limit, context window or refusal), normalized usage and
  the served identifier, with the tool requests and the reply to replay when it
  stopped for tools; or a failure with its category and usage. A request missing a
  required value is refused before anything is sent. Vendor types and response ids
  stay inside the adapter. Adapters use the in-box HTTP client and server-sent events
  reader, no vendor SDKs.

| Providers | Protocol | Endpoint path |
| --- | --- | --- |
| Anthropic | Messages API | `POST /v1/messages`, SSE |
| OpenAI, xAI, Meta | Responses API | `POST /v1/responses`, SSE |
| Google | `generateContent` | `POST /v1beta/models/{model}:streamGenerateContent?alt=sse` |

- Every parameter whose vendor default could change behavior or billing is sent
  explicitly, from the agent version or an adapter constant recorded with the mapping
  version: temperature where supported, validated against the model version's range;
  reasoning, which an adjustable model requires (`parameter_required`); the standard
  service tier, with any other reported tier making usage `usage_unrecognized`; tool
  choice "auto" and parallel tool calls; caching (the agent switch places explicit
  five-minute breakpoints on the Messages API; elsewhere caching is automatic at
  standard retention); and provider-side response storage disabled where offered.
  A parameter that some supported model refuses is withheld, and where the response
  reports its value, that value must be the standard one. The Messages mapping
  requests adaptive thinking with its text omitted, an explicit effort, standard-only
  capacity and global inference. It sends no sampling parameters (temperature, top_p,
  top_k): current models accept only their defaults, and earlier ones none at all with
  thinking. It sends no speed, which some models accept only with a beta header; fast
  mode is an opt-in, and a reported fast speed makes usage `usage_unrecognized`. A
  model that rejects one of the parameters sent cannot pass Test.
- A model capability version explicitly selects `fixed`, whose sourced identifier
  is documented as not repointed, or `alias`, approving the provider's alias policy
  (D213). Existing versions and legacy commands retain the fixed-only contract.
  Either policy accepts only the requested name and its explicit served list; an
  unlisted snapshot is not automatically approved. A returned alias does not prove
  which underlying weights ran. Calls retain the requested identity, capability
  and price versions and actual reported identifier. A
  served identifier that is neither requested nor accepted is checked at the first
  event that names it, so nothing it wrote is published. Its usage is
  `usage_unrecognized`, storing no cost, and the turn fails with `model_mismatch`.
- Identifiers match `^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$`, optionally narrowed per
  adapter (`model_identifier_invalid`). The `generateContent` adapter inserts the
  identifier as one path segment and sends nothing if the built URL's scheme, host,
  port or path prefix differ from its constants.
- Reasoning content is never requested; any streamed anyway is discarded in the
  adapter. Error bodies are read only to classify (§7.5) and apply not-billed
  statuses, never stored, logged, streamed or returned. An opaque continuation value
  (signed thinking blocks, thought signatures, encrypted reasoning items) is echoed
  unchanged to the same model within the turn and lives only in worker memory.

### 6.2 Usage normalization

- Each provider (vendor plus protocol) has a versioned usage mapping with source and
  date; providers sharing a protocol share the parser, not the mapping.
- Dimensions are uncached input, cache read, cache write, output and reasoning. Each
  is declared reported (source field and the event carrying its final value), not
  billed (a sourced structural zero, never inferred from absence) or included in
  another dimension. Each mapping states its output formula; for `generateContent`,
  output is `candidatesTokenCount` + `thoughtsTokenCount` and reasoning is
  `thoughtsTokenCount`.
- Only final values count. For Messages, input and cache counts are `message_start`'s,
  where the vendor documents them for streaming, as updated by any `message_delta`
  that reports them, and output and reasoning are the last `message_delta`'s; the
  counts are cumulative and may not fall. For Responses, the terminal event; for
  `generateContent`, the last chunk's `usageMetadata`.
- A normally ended stream with a missing usage block or priced dimension, a count
  that falls, a field the mapping does not name that holds anything but nulls and
  exact zeros, a reported service tier, region or speed other than the one requested,
  or an inconsistent total is `usage_unrecognized`. Reasoning is never priced, so a
  reasoning count the stream does not report is unknown and leaves usage recognized.
  In a cancelled, failed or interrupted stream, a reported dimension without its final
  value is unknown and the call is `usage_unknown`; unknown never becomes zero.
- A Messages refusal before any output is `usage_unrecognized`: the vendor bills it
  only in some refusal categories and says that list may change. Output is text or a
  tool request; a refusal after reasoning alone or an empty block is still before it.
  A refusal after output is billed as reported.
- Each mapping lists error statuses that are not billed. Any other status may have
  been billed, so the attempt's input is unknown. The Messages documentation names
  none, so no Messages error after sending is retried. A connection that was never
  established sent nothing, and its input is known zero.

### 6.3 Qualification

- A conformance suite runs every adapter against a local fake provider, injected
  through an `HttpMessageHandler` as `PowerImportChecks` does, replaying hand-written
  synthetic recordings: text; tool requests; a two-step loop whose continuation value
  must echo byte for byte; usage with cache and reasoning; reasoning and continuation
  data that must reach no event, row or log; errors, rate limits and overloads before
  and after streamed text; malformed events; cancellation; transport failure; a
  response past the byte bound (`model_provider_response_too_large`); and error bodies
  echoing request text and a masked synthetic key that must appear nowhere. The fake
  asserts every request path. No check calls a real provider.
- A provider is enableable only when the usage mapping its adapter applies is
  confirmed from published documentation, which a reviewed migration records with its
  date and sources and the exact mapping version the release applies; the vendor's
  published retention and training terms for the account type and whether the account
  owner can view content, each sourced and dated, which an administrator records as an
  immutable terms version; and an administrator attestation, bound to the current
  credential and terms versions, that the account is of that type, does not permit
  training on submitted content and has no data-sharing opt-in. Otherwise
  `model_provider_unqualified`. A terms field is 1 to 1000 characters without
  surrounding spaces, control characters, bidirectional controls or line separators,
  spelled out so the rule never depends on the database's locale; its source an `https`
  URL naming a host, without user information or a port, in at most 2048 visible ASCII
  characters; and its checked date one that has begun somewhere, so no administrator's
  today is refused (UTC+14). D210 confirms Meta's documented Responses usage along
  with OpenAI, xAI and Google; account terms and attestation remain required.
- A model is enableable only when tool calling and streaming are confirmed from
  published documentation and pass conformance (`model_unqualified`): its current
  capability version records both confirmations and was validated under the usage
  mapping its vendor's adapter applies, the adapter the conformance suite exercises.
  The mapping version states the reasoning efforts its adapter sends (`messages_v1`:
  low, medium, high, xhigh, max), and a version records a non-empty set of them. A
  release that changes an adapter's mapping leaves earlier versions unqualified until
  an administrator records a new one. A vendor whose adapter applies no confirmed
  mapping gets no capability version (`model_mapping_unconfirmed`).
- Conformance is not real-provider qualification; O09 stays open.

## 7. Agent runtime

### 7.1 Authority and grants

- **Current authority**, evaluated in PostgreSQL: user and membership enabled; the
  user holds `UseAgents` and every permission the version needs; at ask the version
  was the agent's published version and the user holds one of its allowed roles, or it
  was the agent's newest version, still a draft, and the user holds `ManageAgents` (a
  later Publish or save never fails a running turn); the turn's grant is unrevoked and
  unexpired; the agent principal and membership are enabled. The **source-access
  check**: under the agent context, each recorded access function and permission
  passes for every catalog in the source set. Worker writes run without a request
  context, so they evaluate it as a context issued to the turn's agent under its grant
  would answer it; a check proves them equal across the states the mirror reads (§14).
- **Consent.** Asking without a covering grant fails with `grant_required` and the
  panel asks: "Allow Page guide, which sends your questions and the data it reads to
  <provider> (<model>) through the vendor account your administrators configured,
  whose owner may be able to see them, to read trades, trade parties and market data
  on your behalf until 2026-11-17?", naming only the permissions of the version's
  switched-on read tools, which are what the grant lists.
- **Grant.** The request names only the agent version; any other field is rejected and
  writes nothing. A security-definer operation takes the delegator from the bound
  human context and the principal, permissions and expiry (now plus the version's
  duration, capped by the runtime set: `grant_duration_exceeded` on save) from the
  stored version, failing with `agent_retired`, `agent_not_allowed` or
  `grant_permission_missing`, never partially. It revokes the user's earlier grants to
  that agent in the same transaction: one current grant per user and agent.
- **Coverage.** A grant covers version V when unrevoked, unexpired, from the user to
  V's agent, given for a version with V's provider and model identity, and listing
  every permission V needs. A new model version under the same identifier does not ask
  again, and a covering grant keeps its own expiry.
- **Revoke** takes a delegation id, succeeds only for its delegator, needs no
  `UseAgents`, and answers another user's id exactly like an unknown one
  (`grant_unknown`). Revocation, replacement included, cancels the grant's running
  turns, except one whose publication was refused, which fails `access_changed`. Each
  grant and revocation appends a grant audit record in the same transaction; the profile
  dialog lists the user's grants with Revoke, and `ManageIdentities` with `ManageAccess`
  holders see all of them in Users & access, read-only: every grant in force in the
  workspace, by person and then agent, and the latest 100 grant audit records.
- **Grant and Revoke requests** carry an operation identity. A request without it, or
  naming no positive version or no grant, is `grant_input`; one under an identity that
  another grant, Revoke or Retire recorded is `grant_conflict`; an outcome the API
  cannot confirm is `grant_outcome_unknown`, and repeating the identical request
  answers as its original did.
- **Agent context.** Every tool read runs in a request context for the agent
  principal, issued through `security.issue_context` (login kind `agent_credential`)
  with the turn's grant, fixed at ask and never taken from browser input, model output
  or another turn. SQL then requires the agent's permission, the grant and the
  delegator's current permissions, which a read checks once its context is bound and
  again after it reads. The check after the read is defense in depth: the bind holds
  the epoch row `FOR SHARE` until the read's transaction ends, so no revocation commits
  during a read, and a grant or role grant whose term ends during it, the one change
  that check can see, also fails the step's publication recheck. Binding a turn to its
  owner's grant is the server's job, under the same trusted-authenticator boundary that
  binds a page request to its session.
- **Before the ask and every model call**: current authority and the source-access
  check; agent active; model enabled, qualified, priced, its version still current;
  provider enabled and qualified; credential decrypts. Failures: `access_changed`,
  `agent_retired`, `model_changed`, `model_disabled`, `model_unqualified`,
  `model_unpriced`, `model_provider_disabled`, `model_provider_unqualified`,
  `credential_unavailable`. A turn never follows a newer version.
- **Opening a call** is the last step, through the turn's fencing token (§7.4), in the
  transaction that runs those checks and reads the ciphertext: it records the start
  instant and pins the price. No request byte is sent unless it commits.
- **Publication recheck.** Each content-bearing event (step, text, card, target) is
  appended in a transaction holding the epoch row `FOR SHARE` that repeats both
  checks, failing the turn with `access_changed`. The stream sends only committed
  events, so a completed revoke stops every later append. Terminal, usage and cost
  events carry no business data and skip it.
- **Replay recheck.** Before stored content is sent again (conversation reads, stream
  replay, re-materialized cards), every catalog's recorded access function is
  evaluated in the asking user's own request context; any failure hides answer, cards
  and targets as `access_changed`. Replay needs no grant and ignores agent, model and
  provider state, so a retired agent's turns still replay.

### 7.2 Pinned context

- The ask names the knowledge mode (current, or the exact retained context id) and the
  As of frame id when one is selected, as trading reads do, except that it never names
  an earlier capture over working data: a frame of the session's current market
  context (`snapshot_missing`), current only while no historical context is selected,
  a retained context only when it is the selected one. Working data is captured at
  its frame's date, so an ask over it names a frame (`snapshot_missing`). Anything
  else is `context_required`; the ask never falls back.
- The turn's immutable **pinned-context record** (knowledge mode, captured or retained
  context id, resolved cutoff and, with a frame, its exact pin set: frame id, name, At,
  observation ids, the observations it froze, curve-contract selections, swap market
  selections and whether it resolves the latest correction) is its snapshot identity;
  the worker reads only it, and tool reads rebuild the page's frame from it.
- **Working data.** After admission (§7.4) creates the turn queued, the asking request
  captures once in the user's context and its own short transaction and writes the
  record with the fencing token, within the request's deadline and cancellation. A
  failed capture fails the turn with the page read's error, and no other cutoff is
  tried; an abort or the deadline fails it `request_cancelled`, and any other failure
  `agent_runtime_unavailable`. Terminal writes use an independent cancellation scope.
  Tool reads never capture.
- **Reserve.** Each admitted working-data ask permanently uses one of the catalog's
  4096 retained contexts. The assistant captures through a wrapper that, under lock
  730849102, refuses with `knowledge_budget_reserved` when fewer than the runtime
  set's reserve would remain, keeping it for page captures. Reclaiming stays open
  (O07).
- Tools take no context, snapshot or workspace inputs. Each read binds the turn's
  pinned context exactly as its record names it, at its recorded cutoff: over working
  data the context the ask captured, as of the turn's frame, as trades re-read an
  earlier capture; under a historical view its retained context. Current-only
  definitions (TOU products, power hours) run only under a working-data pin
  (`power_historical` otherwise), bind no context and are labeled with their recorded
  read instant, never the pinned cutoff. Reads that consume a frame (curve prices and
  history, parties, trades, power hours) fail with `snapshot_missing` when the turn
  pinned none.
- **Page report.** Stored at ask and never changed, so navigation during the turn
  never changes what Read current page reads; its selection is checked when the tool
  runs (`page_selection_unavailable`).
- **Session binding.** The turn records its workspace session, profile and session
  end; a profile switch or session end cancels it, and the worker checks the binding
  before every read and publication.

### 7.3 Tools and page coverage

The tool set is fixed; each tool is switched on per agent version.

| Tool | Behavior |
| --- | --- |
| Read current page | Dispatches the stored page report to that page's read tool |
| Read trades | Blotter rows, holdings with execution P&L, trade detail |
| Read trade parties | Legal entities and counterparties |
| Read market data | Curves, contract definitions, dated contracts, observations, at the pinned frame |
| Read schedules | Published date schedules and calendars, TOU products, power hours |
| Read reference data | The `references` and `unitCatalog` read behind Reference data |
| Read custom curves | Reads pinned saved ingredients, operators and exact published versions |
| Read jobs | The person's own jobs, and one job's runs, audit actions and command receipts ([devops-assistant.md](devops-assistant.md), D239) |
| Prepare custom curve | Validates/prices an explicit formula and offers a reviewed draft; saves nothing |
| Navigate | Requests a move to a server-issued target |
| Show card | Creates an application-drawn card from this turn's reads (§10.2) |

| Page | Tool | Permissions | Context |
| --- | --- | --- | --- |
| Curves & prices, Contract definitions | Read market data | `ReadMarketData` | Pinned |
| Schedules | Read schedules | `ReadMarketData` | Pinned |
| Power TOUs | Read schedules | `ReadMarketData` | Current-only; power hours need the frame |
| Reference data | Read reference data | `ReadMarketData` | Pinned |
| Entities & counterparties | Read trade parties | `ReadTradeParties`, `ReadMarketData` | Pinned |
| Blotter rows and holdings | Read trades | `ReadTrades`, `ReadMarketData` | Pinned |
| Curve builder | Read custom curves | `ReadMarketData` | Pinned saved library; unsaved edits are not sent |
| Jobs & Infrastructure | Read jobs | `ReadJobs` | Current operational records; refused under a historical view |
| PPA bookings: the inspected PPA, or the listed book's PPA trades (D250) | Read trades | `ReadTrades`, `ReadMarketData` | Pinned, not the page's own market inputs and cutoff; not a Navigate page |
| The Blotter's Risk report and its trade rows under the Risk lens, a new trade entered in the Blotter's panel, Delivery prices, Interval series, Users & access, Settings › AI | none | | Not supported |

- Read current page has no permissions of its own; if the dispatched tool is off it is
  refused with `tool_not_enabled`. On an unsupported page it returns
  `page_not_supported`, and the agent never answers from another page's data.
- Read tools call session-independent operations extracted from the page handlers (§12
  step 2), so pages and tools share one implementation and authorization path. A
  tool's contract is the operation's (architecture §10): versioned identifier, input
  and output schema, permission, and the runtime set's row and byte bounds. An input
  names every member its schema states, those its view does not take as null, so a
  schema requires each member and needs no top-level composition, which a provider's
  tool schema need not accept; a member left out is `tool_arguments_invalid`. A result
  over a bound fails with `result_too_large`, never truncated. The model may retry
  with narrower inputs the schema accepts; where it cannot narrow (a long party list)
  the agent cannot read it and the page still shows everything.
- A tool step ends with a result; a **refusal** returned to the model as the step
  result (`page_not_supported`, `snapshot_missing`, `power_historical`,
  `result_too_large`, `card_too_large`, `card_invalid`, `tool_arguments_invalid`,
  `tool_not_enabled`, `page_selection_unavailable`, or the read's own refusal of its
  input, such as `missing_curve`, `missing_observation` or `knowledge_coverage`),
  carrying only the code and the fixed text the server keeps for it, or one fixed
  generic sentence for a code without one, after which the turn continues; or a
  turn-ending failure (authority, session and fencing codes, otherwise `tool_failed`).
  Refusals count as steps. Read current page's read refusing the page's selection
  itself is `page_selection_unavailable`: each operation declares which of its refusals
  mean that (`missing_curve`, `blotter_view`, `futures_trade`, `swap_trade`).
- **Operations** (from runtime set `turns_v2`): `market_data.curves_v1`, `curve_v1`,
  `observations_v1` and `contract_definitions_v1`; `trades.blotter_v1` and
  `trades.trade_v1`; `trade_parties.versions_v1`; `schedules.library_v1`,
  `tou_products_v1` and `power_hours_v1`; and `reference_data.table_v1`; from `turns_v4`
  each result also carries the handles of the targets it issues, and Navigate's
  operation is `navigation.target_v1`; `turns_v5` adds the curve catalog and
  preparation, `turns_v6` the job reads, `turns_v9` `custom_curves.save_draft_v1`,
  the one write (D260), and `turns_v10` the Blotter's layout on Read current page's
  result (D270). Contract definitions are read with the schedule
  library, under the one bind a request makes, so the page and the tool share that read.
  A result names its operation, the knowledge it was read at (the pinned context and
  cutoff, or for current-only definitions the read instant) and the frame it consumed,
  and returns the page read's own fields, without what only the page uses: booking
  catalogs, rights, workspace payloads, the pricing trace and the frame the result
  already names. A reference table returns one whole page of at most 200 rows, and more
  matching rows are `result_too_large`. Bounds are 500 records and 64 KiB.
- Steps answer a call's tool requests in order, numbered across the turn, and the tool
  loop's next call opens only once each request has one. A step records its tool, the
  operation and the instant it read, the request's input (an object of at most 8 KiB,
  absent only when its arguments were refused) and its code; its event carries only
  its number, tool and code. Results are stored with cards (step 7), their consumer.
- A refusal before the agent's context is bound adds none to the source set; once
  bound, a read's accesses join the source set even when it is then denied or fails.
- Read market data reads the pinned frame only; curve history is not read yet.
- Results carry exact values and server-formatted strings (D114): where the page
  shows formatted amounts (curve prices, Blotter rows and totals, trade detail), each
  amount is `{value, display}` under `tool_amount_display_v1`, the page's own rule
  applied on the server from the inputs the page starts it with: a field's name gives
  its kind; a price shows by its nearest tick, a mark by its row's mark tick, and other
  amounts by the nearest deal format, ties to even, thousands grouped and trailing
  zeros trimmed except for money. A trade shows as its editor shows it: a futures
  trade by the deal's trade format with its prices at its contract's settlement-price
  increment, where the catalog lists the contract; a swap's valuation by the deal's
  trade format and each side by its own. The deal's saved formats are read under the
  agent's context in the read's transaction; they are current shared presentation
  without history, as the page reads them, so a retained read shows its pinned values
  with the formats saved when it ran. Where the page starts from the viewer's own
  display precision (curve prices, the Blotter's book and pivot totals, a swap's
  terms), a preference outside the turn's pins, a result starts from the default (3
  places for money and quantities, 4 for prices, 6 for factors). One corpus of
  payloads, starting ticks and formats, and displays is asserted by both the managed
  and the page's checks. The preamble requires quoting only returned values and
  deriving none. Prose is model output; cards are the exact channel.
- **Navigation targets** are issued by the server; the model never composes one. A
  target is a page report plus the pinned context: one of the pages the assistant reads,
  a selection in that page's report form or none for the page as it opens, the turn's
  knowledge mode, the context its reads bind and its As of frame, whether it is the
  page, a record (a curve or a trade) or a filter, and a label the server composes from
  the page's name and what the selection shows. A page target names a page the user can
  currently open, checked under the agent context against the delegator's permissions,
  so only a page whose reads the turn's grant lists, and keeps the pinned context; it
  opens the page as it opens there, whatever the page's reads hold at that context: a
  page needing an As of frame asks for one, and current definitions show as they are
  when the target is followed. Record and filter targets come only from this turn's
  reads: each read issues its own selection as a target, a curve read its curve and a
  trade read the trade at the revision it read, and the curve directory and Blotter rows
  each curve or trade they list, under handles numbered across the turn (`n1`, `n2`, …)
  that the result carries. A handle names its target once the step that issued it is
  published, so a later request of the same call can name one. There is no separate
  context target: every target keeps the pinned context, so following one from another
  context changes it (§10.1). Targets open read views only. Navigate names `page` or
  `target`, the other null. A handle the server did not issue in this turn, or a page
  the delegator cannot open, is `tool_arguments_invalid`. A target's page is checked by
  the access functions and permissions its reads run under: one that opens joins the
  source set with those accesses, one that does not adds nothing to it, and a check
  stopped or failing once bound ends the turn as a read does (§7.5).

### 7.4 Execution

- Turns run in an in-process worker, an interim path until Milestone 4's durable runs.
  States follow architecture §11 (queued, running, cancel requested, completed,
  failed, cancelled) plus interrupted; terminal transitions are atomic and exclusive.
  A turn completes only when its last call ends normally with no pending tool request
  and every check and publication succeeded. Cancellation sources end it cancelled,
  recovery ends it interrupted, and everything else fails, even after published text,
  which stays marked incomplete.
- **One worker per database.** A preview process holds a session-level advisory lock
  (agent-worker class) on a dedicated connection, which then listens for cancel
  requests, and advances the worker generation; losing that connection ends the
  generation, stops the process's turns and releases the lock. While another session
  holds it, or the worker cannot start, assistant and provider-test routes refuse with
  `agent_runtime_unavailable`. Before serving, it ends every
  non-terminal turn of an earlier generation as interrupted, marks their open calls
  `usage_unknown` and closes open test records, each exactly once. Every turn write
  requires a current generation.
- **Admission** takes a transaction-scoped advisory lock on the workspace and enforces
  one active (queued, running or cancel requested) turn per user per workspace
  (`turn_active`) and the runtime set's workspace limit (`workspace_turn_limit`). It
  also interrupts, exactly once, any turn still non-terminal past its elapsed limit
  plus the admission deadline, marking its open calls `usage_unknown`. A queued turn
  still without a pinned-context record at the admission deadline fails with
  `ask_incomplete`. Every ending frees both slots.
- Agent work uses its own runtime and authenticator pools per scope on the existing
  logins, never the UI pools; workspace concurrency stays below their size, and each
  tool read runs in one request context and is bounded by its agent version's time per
  read and its turn's deadline (D245). The database holds each read to its time per
  read too, per statement and per wait between them, so a read the API can no longer
  cancel still ends within twice that; the agent pools' command timeout is the most time
  per read a version may set, 30 seconds. No transaction, request context or epoch lock is held across a
  model call, backoff or stream write. Worker writes use a pool of their own on the
  runtime login. Text deltas are coalesced for at most the runtime set's interval and
  published in pieces of at most 2,048 bytes, each with its publication recheck. A
  worker write the database does not take within the runtime set's bound stops the
  call in flight, which is still closed, and fails the turn `agent_runtime_unavailable`
  unless the database ended it otherwise; ending writes are retried while they fail
  transiently.
- **Fencing.** Every worker write is a narrow security-definer operation presenting
  the turn's fencing token, held only in the admitting process (the database stores
  its digest). The record write is accepted only from queued without a record, the
  start only from queued, content writes and call opens only while running. Call
  close, usage, cost and terminal events are accepted in any non-terminal state
  without a request context, so accounting finishes after revocation or session end.
  From cancel requested the only endings are cancelled or interrupted; a record write or
  start that finds cancel requested ends the turn cancelled instead. A refused
  publication is recorded, and the turn then ends only failed `access_changed` or
  interrupted. A write with a stale token or generation is rejected with
  `agent_runtime_unavailable`, a write the turn's state does not accept (such as an end
  while a call is open) with `turn_state_conflict`, and a call the retry rules do not
  allow with `retry_not_allowed`.
- One narrow operation without the token moves a queued or running turn to cancel
  requested, unless its publication was refused. Stop (owner's context), Disable,
  Retire, revocation and the profile-switch handler use it.

### 7.5 Limits and failure

- Each agent version declares, and each turn records: maximum tool steps, total input
  tokens per turn, output tokens per call (not above the model's:
  `model_limit_exceeded`), elapsed time from the ask, time per tool read (1 to 30
  seconds, not above the elapsed time), retry count, history window (0 to 1,000 earlier
  turns, §7.6) and the caching switch. Exceeding one ends the turn with `limit_tool_steps`, `limit_input_tokens`,
  `limit_output_tokens` or `limit_elapsed`; a tool request cut off by a limit is not
  run, and a call whose tool requests would take the turn past its steps has none of
  them run. A read in flight at its version's time per read ends the turn
  `limit_tool_read`, and one in flight at the turn's deadline `limit_elapsed`, each
  recording its step with the instant and accesses it read under and logged as a warning
  naming that bound, its tool and its operation, never its input; a read that failed in
  another way ends it `tool_failed` (D245).
- The input limit is checked before each attempt against input that earlier attempts
  reported; nothing is estimated, so the crossing attempt completes and is billed. An
  earlier unknown or unrecognized input ends the turn with `limit_input_unverifiable`
  before the next tool-loop call. The panel identifies this as a provider usage
  verification failure, not a conversation-length limit or a request to start over.
  Input is known zero only for a transport failure
  before any byte was sent or a not-billed error status before any stream event.
- **Runtime set**: versioned, shipped with the application, recorded on each turn,
  never host configuration. Workspace concurrency; agent pool sizes; retry backoff;
  replay window (256 of the streamed turn's events); heartbeat interval (15 seconds,
  under 20); text coalescing interval; card data and event sizes; question and
  instruction lengths (set so a body stays under 32 KB: `question_too_long`,
  `instructions_too_long`); a worker write's bound and retry interval; tool-result row
  and byte bounds; maximum grant duration; test output limit and deadline (below 15
  seconds); admission deadline (above 15 seconds); retained-context reserve; provider
  timeouts and response byte bound; subscriber buffer (64 signals). Oversize cards are
  `card_too_large`; text is split into events; any other oversize event is
  `event_too_large`. Nothing is truncated.
- Provider errors classify as authentication, rate limit, overloaded, invalid request,
  context window exceeded or transport. For Messages, 401, 402 and 403 are
  authentication; 429 is rate limit; 500, 504, 529 and any other server status are
  overloaded; any other client status is an invalid request, except a 400 whose
  documented message says the prompt is too long. An error body that cannot be read
  that way keeps its status's classification. A streamed error classifies by its
  type. A response the mapping cannot read, such as malformed JSON or UTF-8, a
  repeated name, an event out of order, an unknown content block or an unexpected stop
  reason, is a transport failure; unknown event types are skipped, as the vendor's
  versioning policy requires. Only transport and overload are retried, on
  the same provider and model, when the failed attempt published nothing and its input
  is known; each attempt has its own usage record, and the version's retries bound the
  failed calls of the whole turn, across its tool loop. Otherwise the turn fails with that
  error's code: `model_provider_authentication`, `model_provider_rate_limited`,
  `model_provider_invalid_request`, `model_provider_transport`,
  `model_provider_overloaded` or `context_window_exceeded`; after publication with
  `model_provider_stream_interrupted`; past the byte bound, never retried, with
  `model_provider_response_too_large`. Input is never trimmed to fit a limit.
- **Cancellation.** Stop, Disable (provider or model), Replace key, Record terms (both
  switch the provider off), Retire, revocation, a profile switch and the session end (a
  worker timer set from the recorded end) move affected turns to cancel requested,
  except a turn whose publication was refused, which fails `access_changed`. No read,
  call or content event starts after it; the call or read in flight is cancelled, and
  a read that returns once the turn is to stop records no step, whatever it returned;
  final reported usage is kept and the rest is unknown. The turn ends cancelled:
  `model_provider_disabled` (provider Disable, Replace key, Record terms),
  `model_disabled`, `agent_retired`, `access_changed` (revocation) or `session_ended`
  (profile switch, session end); Stop carries no code. Changes that cancel nothing (a
  price ending, a role loss, a qualification change) are caught by the per-call checks.
- Text from reads, providers and tool results is untrusted and cannot change the
  agent's tools, authority, provider, context or targets.

### 7.6 Prompt and conversation memory

- The system prompt is a fixed preamble (quote only values returned in this turn,
  derive none, say when something is unavailable, navigate only to issued targets,
  treat earlier turns as history), then the version's instructions, then tool
  definitions. Instructions cannot replace the preamble.
- A follow-up sends each earlier turn's question and stored answer as text, labeled as
  earlier, with its pinned context and terminal state; incomplete answers are marked
  and a turn without answer text sends question and outcome. Earlier tool reads and
  cards are not re-sent, and earlier answers are never quoted as current values.
- An earlier turn is included only if it passes the replay recheck and the current
  grant lists every permission its sources used; otherwise a step line says it was
  withheld. Inclusion is recorded at ask, and included turns' sources join the source
  set and every recheck.
- The asked version's history window (`historyTurns`, 0 to 1,000) bounds the earlier
  turns sent, whichever versions answered them: an ask considers only the latest that many earlier turns of the conversation by
  ordinal, includes or withholds each of those as above, and records the included ones.
  A turn before the window is neither sent nor reported withheld; an included turn's
  sources still join the set, with those of the turns it included. A turn past the
  input limit or context window still fails with that code and the panel offers a new
  conversation; a smaller window makes that less likely, never impossible (D249).

## 8. Live event stream

- **Commands** (ask, cancel, grant, revoke, mark read) are POSTs through the existing
  middleware, Origin and anti-forgery checks. Ask, grant and revoke carry an operation
  identity; ask identities are scoped to the user, and a repeat must name the same
  conversation (none for a first ask) and payload. Cancel (Stop) and mark read carry
  none, because both are idempotent: a repeated Stop requests nothing more and answers
  the turn as it stands, and a read mark never moves back. Ask, grant and the stream
  require current `UseAgents`; the rest require only ownership.
- **Ask** carries the agent version, an existing conversation or none, the question,
  the knowledge mode or retained context, the frame id when selected, and the page
  report, exactly `{"page","selection"}` as Looking at shows, which on a page the Blotter
  reports (`blotter` and the `blotter_*` pages) may also carry exactly
  `"layout":{"pane":{"details","charts"},"poppedOut"}`, each a flag: whether its details
  and charts panes were open, each the shown tab of its group in the panes as drawn and
  not put away, and whether the Blotter was in a window of its own (D270). Looking at
  shows the layout as it shows the selection, so a pane opened or closed since it was
  shown refuses the send as a page change, as any other change does; Read current
  page's result on `blotter` names it, and no other result does.
  The page is one page identifier per
  navigable view from a closed set, `curves`, `contract_definitions`, `schedules`,
  `power_tous`, `reference_data`, `parties`, `blotter`, `custom_curves`, `jobs` and
  `ppa`, which the assistant reads (Navigate opens all but `ppa`, D250), and `blotter_risk`, `blotter_risk_lens` (the Blotter's trade rows under
  the Risk lens, D222), `blotter_new_trade`, `blotter_new_future`,
  `blotter_new_swap` (a new trade being entered in the Blotter's panel, of its product,
  or of none while none is chosen), `delivery_prices`, `interval_series`,
  `interval_curves`, `users_access` and `settings_ai`, which it does not.
  A read page's selection is its read tool's input for a view the page shows; any other
  page's is empty. Before admission and writing nothing it refuses an invalid report
  (`page_report_invalid`, checked by the parser Read current page dispatches with), a
  conversation the principal does not own in this scope, answered like an unknown id
  (`conversation_not_found`), and an agent other than the conversation's
  (`conversation_agent_mismatch`). The report is checked before the operation identity,
  so a repeat of an ask admitted under `turns_v1` with a report outside the closed set
  is refused `page_report_invalid`: the one repeat that the closed set's introduction
  does not answer as its original did.
- **Reads** (conversations with unread markers, one conversation's turns and events,
  the unfinished turn) pass the replay recheck.
- **Stream:** `GET …/assistant/conversations/{id}/events?after={sequence}`, read by
  native `EventSource`, for an owned conversation. It serves the conversation's latest
  turn only. With that turn non-terminal it streams live; otherwise it replays the
  turn's stored events after `after` to its terminal event, or sends `refused` with
  `turn_not_running` when none follows `after`. Idle panels hold no connection.
- **Events:** accepted, step, text delta, card, navigation target (`navigation`, right
  after its step's event, carrying the step's number and the target), usage and cost;
  terminal completed, failed, cancelled, interrupted; and `replaced` and `refused`. A
  stored event's data is its turn's id, `hidden` (null, or the code that hides the
  turn's answer, `access_changed`) and its payload as the owner's reads show it; a
  terminal event's also says whether the turn's answer is `incomplete`, as those reads
  state it, so the panel derives neither. `replaced` carries an empty object and
  `refused` a code with its fixed message, neither with an id. A 200 stream ends only
  after a terminal, `replaced` or `refused` event, on which the panel calls `close()`,
  so the browser reconnects only after a network drop or a cut (below).
- **Resume.** Event ids are per-conversation sequences. Reconnects send
  `Last-Event-ID`, which overrides `after`; a new stream passes `after`. A value that is
  not a decimal sequence is refused `request_format`, a negative one `turn_input`. A
  sequence beyond the conversation's latest, before the event preceding the turn's
  first, or leaving more of the turn's events than the replay window (256) gets
  `refused` with `replay_unavailable`, and the panel re-reads and opens one new stream;
  the server never silently restarts.
- **Wire format:** `id:` with the sequence, `event:` with a fixed name, one `data:`
  line of single-line JSON from the API serializer, a blank line; the comments `: open`
  and `: ping` are one line each. Model, read and user text appears only inside JSON
  strings, with CR, LF, U+2028 and U+2029 escaped, and a piece of text the worker
  pushed writes byte for byte as the same event read back. The panel accepts only the
  fixed names and validates each payload; a failure closes the stream and re-reads
  once.
- **One stream per unfinished turn**, because browsers cap connections per origin: a
  new one, from any of the owner's sessions, sends the older `replaced`, whose tab
  offers Resume. A stream stopped before it begins replaces none.
- The route is matched in the first middleware, exempt from the 15-second deadline and
  the session semaphore, and sends `refused` with `stream_expired` if the turn's
  elapsed limit plus the admission deadline passes without a terminal event; the
  worker's own `limit_elapsed` ending comes first.
- **Admission.** The loopback and GET Origin checks apply: a same-origin `EventSource`
  GET sends no Origin, so a request without one passes and a foreign one is refused.
  `EventSource` cannot send the anti-forgery header, so the stream relies on the
  scope's SameSite=Strict, HttpOnly session cookie plus `Sec-Fetch-Site: same-origin`
  and `Sec-Fetch-Mode: cors` (`access_denied` otherwise); cookies are not port-scoped,
  so Sec-Fetch is what refuses a page on another loopback port. A first read refuses a
  person without `UseAgents` (`access_denied`) and answers another person's
  conversation like an unknown one (`conversation_not_found`). For a non-terminal turn
  the stream then subscribes, takes the worker's generation as an ask does, starting
  one if it can (`agent_runtime_unavailable`, 503, while another process holds it),
  reads again what committed before it subscribed, and replaces the older stream; a
  switch away from its profile before then refuses it `session_ended`, replacing none.
  No CORS headers; the only side effects are that replacement and the worker's start.
  Failures are non-2xx, which stops `EventSource`, and the panel re-reads once. A
  missing or expired session is `session_ended` on assistant routes (pages keep
  `access_denied`), with a reload offer and no retry loop.
- **Delivery.** The server flushes headers and a comment on open and heartbeats every
  15 seconds while idle, checking before each write that the session is unexpired with
  an unchanged profile (else `refused`, `session_ended`); a profile switch ends at once
  the streams its session opened under the profile it left. A waiting stream holds no
  database connection. The worker pushes each piece of answer text in memory once its
  publication recheck has committed, and the stream sends the piece that follows the
  last event it sent. Every other event, a gap and the turn's ending come from a short
  read in the owner's own request context with the replay recheck: when a published
  event is not the next piece of text, when the worker's run of the turn ends, and at
  each heartbeat until the turn starts, so an ending its worker never wrote still
  arrives. Duplicates are dropped by sequence. Once a read hides the turn's answer,
  every later text is hidden too.
  - A run that ends with its turn unfinished, as a lost generation's does, and the
    server's shutdown send `refused` with `agent_runtime_unavailable`; a read refused
    for lost `UseAgents` sends `refused` with `access_denied`.
  - A subscriber overflowing its bounded buffer (64 signals) is cut without a final
    event, and so is a stream whose read fails otherwise; both resume through
    `Last-Event-ID`.
- Each scope maps its own stream and commands inside `MapPreview`; no new dependency.

## 9. Settings › AI

Tabs appear by permission (§5.4), use the existing table component and pending-command
pattern (except Replace key, §5.2), and end with that tab's latest 100 audit entries;
credential entries read only "key replaced".

- **Usage** (`ReadModelUsage`): a half-open range of at most 366 local dates (D25,
  `report_range_invalid`) in a reporting zone prefilled from Profile
  (`report_zone_invalid`; never `localtime`, `posixrules`, `Factory` or a `posix/` or
  `right/` name, host or tzcode artifacts a database may list), by model, user or agent.
  Each day is the half-open interval from its start to the next day's start, and a call
  falls on the day that holds its start instant. A day starts at the earlier of its
  midnight and the instant after the previous date's last microsecond, each read in the
  zone as PostgreSQL resolves local times: a skipped time takes the offset before the
  transition, a repeated one the offset after it. This is the first instant the zone's
  clocks show the date, except where a transition's skipped or repeated time strictly
  contains midnight in the tzdata release the database uses; release 2026c has such
  dates only before 2011. Where the clock moves back across midnight, no single interval
  holds a date's instants, so such a day ends where the next one starts. The response
  names its format, `usage_report_v1`, which the tab refuses unless it reads it; a later
  format keeps a reader for every earlier one, since an export replays the report it
  stored. The response states the zone, the UTC bounds and each day's bounds. It shows
  per currency the cost of priced calls beside the number of calls without a cost, and
  tokens; answers and cost per answer, with the other turns and their cost of priced
  calls on their own line; a daily chart of the cost of priced calls; turns by terminal
  state, tokens by dimension and the share of priced cost (a row's cost of priced calls
  over its same-currency total's); unpriced, unknown, unrecognized and superseded-price
  amounts apart; and provider tests as their own row so views reconcile. CSV export is
  audited and listed; it names its export and the report's format, carries exact costs,
  sums, counts, tokens, the exact currency reference, zone and range, ratios only as
  `token_cost_report_v1` strings, and uses the formula-guarded writer the Blotter export
  uses, `guardedCsv` in the shared export module `web/src/csv-export.ts`.
- **Models & prices** (`ManageModelProviders`): models per provider with status,
  rates, tier and the revision in force or scheduled; Add, Edit, Enable, Disable, Add
  price, Withdraw price. Every field is entered and none inferred: a request names each
  field, null where it means "not offered", no tier or open-ended. No models or prices
  ship. Edits other than the display name create a model version: an output limit of
  1 to 10,000,000 tokens, the reasoning efforts the model accepts, up to 16 accepted
  served identifiers other than its own, and a source and checked date for each fact,
  including the identifier's stability and the tool and streaming confirmations. An
  edit leaves the model enabled or disabled; a running turn stops at its next call
  (`model_changed`). Adding a vendor's first model creates its provider at version 1
  without a credential; this tab's audit records it as that Add model. Models start
  disabled; Enable needs a qualified model on an enabled, qualified provider
  (`model_unqualified`, `model_provider_unqualified`, `model_provider_disabled`).
  Unpriced models show "No price".
- **Providers** (`ManageModelProviders`): each vendor's protocol, endpoint (an
  override marked as a check endpoint), qualification, terms, owner visibility,
  attestation, credential status, last test and state; Replace key, Record terms,
  Attest, Test, Enable, Disable. Replace key and Record terms each clear the
  attestation and disable the provider in one transaction; only Enable with a current
  attestation resumes calls, never Attest alone. The tab states that enabling sends
  users' questions and visible data to the vendor account, that its owner may see them,
  and that its spending limit is the only cumulative cap.
- **Test** sends one fixed synthetic prompt without workspace data or tools to a
  priced model the administrator picks, on a qualified provider; either may be
  disabled. Temperature and reasoning are validated as for a version; output is capped
  at the test limit; there is one attempt. The record opens in the administrator's
  context under `ManageModelProviders` and pins its price before any byte is sent; a
  per-test token held by the handler closes it without a request context, under an
  independent scope at the test deadline or an abort (`limit_elapsed`,
  `request_cancelled`). A same-identity retry returns the outcome or
  `test_in_progress`, never a second request. A test succeeds only for a well-formed
  response ending normally or at the output limit with recognized usage. Otherwise its
  outcome says why: `model_refused`, `usage_unrecognized`, `model_mismatch`,
  `context_window_exceeded`, the provider error's code (§7.5), `limit_elapsed` (the test's
  deadline or its request's), `request_cancelled` (the client left), or `interrupted`
  when a later worker closed it (§7.4) or the handler failed before the stream's final
  event.
- **Agents** (`ManageAgents`): name, description, instructions, model, temperature and
  reasoning where applicable, §7.5 limits, grant duration, tools and allowed roles.
  Only enabled, qualified, priced models on enabled, qualified providers are offered;
  a save names the exact model version shown and fails with the §7.1 model and
  provider codes. Each save is a draft; Publish, with the expected version, publishes
  the newest. An agent whose model version is no longer current shows "Model changed"
  until a new version is published. Retire is permanent.

## 10. Assistant panel and cards

### 10.1 Layout and contents

- The panel docks right and the page narrows; its left edge resizes it by pointer or
  keyboard, the width a browser-local preference (D45). Below 760 px it is a
  full-width sheet. A header launcher toggles it and is hidden without `UseAgents` or
  a usable agent.
- The panel keeps its conversation across pages and has its own busy state. Closing or
  reloading does not cancel a turn; after a reload the panel reads the unfinished turn
  and resumes with `after`. A turn finishing while the panel is closed shows a
  launcher indicator and an unread History marker until opened.
- **Header:** agent picker with provider, model, version and a Draft label; History;
  new conversation; close. The picker lists usable agents and preselects the last
  explicit choice (D45) only while it is listed, otherwise "Choose an agent" with the
  composer disabled. A conversation is locked to its agent; another choice starts a
  new conversation.
- **Looking at:** the page, selection, the Blotter's layout where it reports one (D270)
  and context the next question will pin, or that the page is not supported. The ask
  sends exactly this.
- **Conversation:** questions; collapsible step lines; streamed answers, incomplete
  ones marked; cards; "Took you to …" notices with Back, which restores page, record,
  filters and context; and a usage line per answer (tokens by dimension, cost per
  currency). A target that changes context, or one leaving unsaved editor changes,
  shows a button instead of moving.
- **Composer:** Enter sends, Shift+Enter adds a line, Send becomes Stop during a turn,
  and a failed send keeps the draft with Retry. On `grant_required` the consent of
  §7.1 appears.
- **Formatting:** paragraphs, lists, pipe tables, emphasis and code, built from text
  nodes (D247). A table needs a header row, a separator row and rows of the header's
  width; anything else stays text. No HTML, links or images, and URLs are not linked,
  because rendered links let injected text carry data out. Charted values appear only
  as cards.

### 10.2 Cards

- The agent submits a typed specification: a kind (table, bar chart, line chart, value
  tiles) and bindings from card fields to fields of this turn's reads. It holds no
  numbers and no free text; anything else is `card_invalid`.
- The server composes the title from the bound reads' display names and pinned context
  (or a current-only read's instant). Labels, values and display strings come only
  from read schemas and stored reads. The footer names the reads and their context.
- The browser draws cards with the existing table and chart components (D80), with
  sort, hover and series toggles. At creation, in the same transaction and recheck,
  the server issues and stores the navigation target of each bound row or point that
  has one, within the card data size. A click uses the stored target and sends no
  command; a re-materialized card shows targets only after the replay recheck. No
  target is issued after the turn ends.

## 11. Hosted mode and later phases

Under `--hosted-pilot` the assistant runs as phase 3 below describes. The SQL applies
in every mode: the assistant's permissions appear in role listings, Users & access
refuses agent principals and the agent reader role, and `tools/provision_hosted.py`
grants the owner an explicit list of human role codes instead of every role, because
the agent reader role cannot be held by a human.

**Phase 2 (agent notes)** adds Search notes and Save note tools, notebooks, note
permissions, provenance and a Notes view. Notes live in an agent's own notebook or in
one **common notebook** per workspace that every agent can search and write. Sharing
must not widen authority, so a note is governed like an earlier turn (§7.6): it keeps
its turn's source set and pinned-context record; reading it adds its sources to the
reading turn's source set, so later notes inherit them; it reaches a model only when
it passes the replay and source-access checks and the turn's grant lists every
permission those sources used (Search notes otherwise omits it); it reaches a panel or
the Notes view only for a user who passes the replay recheck on all of them; and it
always appears labeled with its context, never as current values. Text a user typed is
not a source, so a note reaches anyone but its author only after the author confirms
its exact text in the panel. Whether an agent notebook is also kept per user is
decided with that design. Phase 1 already stores what this needs: source sets as exact
tool-read ids with access rows, and the pinned-context record.

**Phase 3 (hosted baseline)**, decided by the owner on 2026-10-22 (D192), runs phase 1
under `--hosted-pilot` as an installation someone else operates runs it. Migration
0087 is a schema change, so its release waits for operator baseline qualification
(D135), and the installation's operator adds the key files, `host.json` fields,
connection limit and firewall rule below in the same window.

- **Routes and worker.** Hosted mode maps the assistant, stream and Settings › AI
  routes and the panel's selection of a retained context by its id, and runs the
  worker, provider client and agent pools as the local preview does, on the
  installation's own runtime and authenticator logins. The signed-in account takes the
  place of a preview profile: the launcher, Settings › AI and the grants in Users &
  access appear by permission, as in the preview.
- **Sessions.** An application session belongs to the sign-in session it was opened
  under: it names itself to the database by that session's digest and ends when that
  session does, at most 30 minutes after sign-in, and one opened under an earlier
  sign-in is never used again. Turns and streams bind it (§7.2, §8), and the database
  refuses a hosted ask (`turn_input`) whose binding names another sign-in session or
  ends after the one its request context was issued for. Revoking a sign-in session,
  which signing out, a password reset and disabling the account or its membership all
  do, cancels its turns `session_ended` in the same transaction. Signing out also ends
  its streams at once with `session_ended`, and a stream that reads after its session
  was revoked elsewhere is refused `session_ended`. The session's end stops its turns
  and streams as in the preview.
- **Administration.** Providers, keys, models, prices, agents and usage are
  administered in Settings › AI by holders of their permissions, each change audited,
  as in the preview. Enabling a provider is the access administrator's decision there,
  after recording the vendor's terms and attesting; there is no installation-level
  list of vendors.
- **Credential keys.** `host.json` names the two key files of §5.2 in
  `credentialKeyFile` and `credentialRetryKeyFile`: both required and named when
  missing, two different files, absolute and outside the served assets, like the
  installation's other private files. Earlier images refuse the two fields, so an
  operator adds them last, after the previous API's last start.
  `tools/provision_hosted.py` writes them with a new installation's other private
  files, and its `--credential-keys` mode writes just the two into an existing
  installation's private directory for the account the API runs as, refusing either
  that exists; no person handles their bytes. The custody checks are the preview's
  (§5.2). They are backed up with the installation's other private files; losing them
  means entering every vendor key again.
- **Retention (O11).** Conversations are kept, as in the preview. A retention policy
  waits until an installation needs one.
- **Data scope (O22).** An agent reads its workspace's primary catalogs through the
  tools its version switches on and its grant lists, which the consent names.
  Explicit curve dependencies can also name reference calendars in other catalogs
  (D193). Those reads require current workspace scope and `ReadMarketData`, and
  their catalogs join the turn's source-access rechecks. There is no per-agent
  catalog selector; O28 records that optional policy.
- **Egress.** The API sends model requests only to a provider's fixed endpoint: a
  turn's calls once the provider is enabled, and Test's one synthetic prompt once it is
  attested (§9). Owned checks instead set the IP-literal loopback endpoint in
  `OPENETRM_PROVIDER_CHECK_ENDPOINT`, in either mode (§5.2), which a deployment refuses
  in a serving API. The installation's host firewall lets the API container open
  outbound TCP 443 and DNS only, and every deployment verifies that fence, refusing a
  changed rule or a network recreated with another bridge
  ([hosted-pilot.md](hosted-pilot.md)). A proxy filtering by host name waits for an
  installation that requires one.
- **Worker.** One API process per installation holds the worker generation (§7.4). A
  deploy's disposable candidate refuses agent work `agent_runtime_unavailable` while the
  serving process holds the lock. When none does, as on the first deploy, it takes the
  lock and starts a generation, but its checks never ask.
  Replacing the serving process leaves its unfinished turns to the next start, which
  interrupts them. Leases for several serving processes stay a later item.
- **Connections.** The API's pools hold at most 46 database connections: 8 each for
  page requests on the runtime and authenticator logins and for sign-in, 6 for each
  agent pool (§7.4), 8 for worker writes, the worker's lock and the calculation
  listener. `deploy/compose.yaml` allows the database 80.
- **Gateway.** Unchanged: it already sets `proxy_buffering off` and a 20-second read
  timeout, which the stream's 15-second heartbeat fits. `tools/check_public_routes.py`
  checks that a stream passes through it unbuffered.
- **Qualification (O09).** Before anyone else gets the launcher, an installation's
  owner enters their own vendor key in Settings › AI, records the vendor's terms and
  attests, adds a model with its capabilities and the price they enter, runs Test, and
  asks a scripted question from the panel that reads and navigates. Anything the live
  API sends that the adapter refuses is fixed under a recorded check. O09 then closes
  for that vendor only.
- **Spending.** D153 is unchanged: per-turn limits and full cost reporting, with the
  vendor account's spending limit, which the Providers tab states, the only cumulative
  cap.

## 12. Delivery order

Each step leaves the application working with no provider configured, keeps the hosted
checks green, and updates every document it makes inaccurate (db/README.md,
security.md, reference-data.md, hosted-pilot.md).

1. Record §14 in decisions.md (first removing the blank lines that split the
   agreed-decisions table after D77, D96 and D99 and before D132), AGENTS.md and
   plan.md.
2. Extract session-independent typed read operations from the page handlers, taking an
   issued request context and a pinned-context record, never `HttpContext`. Pages
   switch to them with every existing check green.
3. Provider, credential, model, price and audit tables; `ManageModelProviders`; key
   custody in `run_preview.py`; Models & prices and Providers with Replace key. Every
   provider unqualified.
4. The fake provider; adapters and mappings; model capability versions; conformance;
   usage and cost; model-call records for tests; qualification and attestation; Test,
   Enable, Disable. D210 adds the remaining sourced adapter mappings; account and
   model qualification remain separate.
5. Agents, grants and turns: the remaining tables; `ManageAgents`, `UseAgents`,
   `ReadModelUsage` and the agent reader role; creation, Publish, Retire; the Users &
   access and provisioning changes; context pinning and reserve; read tools and
   Navigate; worker and pools; the stream; Agents and Usage tabs; changed checks. Once
   turns record model calls, the Providers directory's last test, which migration 0064
   takes as a provider's latest call, leaves them out, with an index for that lookup.
6. The panel with consent; grants and Revoke in the profile dialog; grant history in
   Users & access; navigation history; the background indicator.
7. Cards and Show card.
8. Architecture updates and a cross-document consistency pass.

## 13. Verification

Every structured error code in this document has a check that produces it where it is
specified, and every rule stated as "never" has a check that attempts it. Checks use
synthetic data and the fake provider. The cases that coverage does not imply:

- **Numbers** (`token_cost_v1` and Usage's days, sums and report methods in SQL, the rest in C#): tier thresholds with cache tokens; unknown prompt dimensions with
  and without a tier; a revision appended during an open call; totals over two
  revisions of one currency code; input-limit accounting for the crossing attempt,
  pre-send failures, not-billed overloads and a reset after send; Usage days across a
  daylight-saving change.
- **Authority:** revoking or replacing one user's grant cancels only that user's
  turns; a revoke during a model call returns without waiting and nothing but call
  close, usage, cost and the terminal event commits after it; losing the last
  `UseAgents` role fails a running turn while Revoke still works; stored grants match
  the version, a version with the same provider, model identity and permissions asks
  nothing, and drafts stay hidden until Publish; each of provider disable, model
  disable, Retire, Replace key, price ending, model version change and scope removal,
  between calls and during a backoff, stops further calls with its code; a retired
  agent's turns still replay; history across a context change and a narrower grant; a
  grant during a delivery-price attempt fences it; `check_temporal_storage.py` proves
  delegated authority with `security.market_access(<catalog>, 'ReadMarketData')`,
  false without a delegation and true with one, for an agent created through
  `ManageAgents`.
- **Context:** selection and navigation changes mid-turn leave reads on the pinned
  context and asked page; an abort after admission, a capture held on lock 730849102
  past the deadline and a failed record write each free the slot; a profile switch and
  session end, during a turn and during a silent call, cancel it with `session_ended`
  and still record usage; refused asks add no retained context, and the reserve
  refuses an ask while a page capture succeeds; with an empty manifest a party step is
  refused and the turn still completes.
- **Custody and egress:** credentials appear in no SQL parameter, audit row, response,
  log, event or fake request body; a wrong key file, a ciphertext moved to another
  provider or version and one flipped byte send nothing; requests reach only the
  configured endpoint, follow no redirect, and ignore a loopback listener set as
  `http_proxy`, `HTTPS_PROXY` and `ALL_PROXY`; injected text in a synthetic trade
  memo, party name and tool result changes no tool, provider, context or target.
- **Execution:** stale tokens, finished turns and revoked grants reject content writes
  while usage still closes; a second process refuses agent work and the next one
  interrupts turns, calls and tests exactly once; concurrent asks from one user's two
  sessions and from more users than the limit; Stop on a queued turn and a cancel
  during capture end it cancelled; completion after cancel requested and
  old-generation writes are rejected, and cancel after completion reports completed;
  the next admission interrupts a stuck turn and closes its calls; a stalled provider
  test closes with unknown usage and never sends a second request on retry, and one
  losing its permission mid-test still records usage while a token-less write is
  rejected; a test with each reasoning effort its mapping states passes the adapter's
  own effort check against the fake provider.
- **Stream:** resume through `Last-Event-ID` and `after`; an answer containing CR, LF
  and CRLF followed by `event: navigation`, `data: {…}`, `id: 999` and a blank line
  arrives only inside text deltas, byte-identical to the stored answer; admission
  without the cookie or the Sec-Fetch headers, or with a foreign Origin, gets 403 and
  no CORS headers.
- **Browser:** Replace key's uncertain outcome leaves the key in no storage or URL;
  card strings trace to stored reads and clicks send no request; a page on another
  loopback port is refused; the context-change button and `page_not_supported`;
  preselection only while listed, and another agent starting a new conversation; six
  tabs with one unfinished turn and idle panels holding no stream; reload and close
  mid-turn without duplicates or gaps; a second tab on one turn getting `replaced`
  with Resume; the background indicator and unread marker; Revoke without `UseAgents`;
  a CSV member name starting `=` is apostrophe-prefixed; the 390 px layout shows no
  identifiers and no uncaught errors.
- **Changed and hosted checks:** `check_admin_preview.py` expects `agent_managed`;
  `run_preview.py --hosted-check` expects explicit human roles. Under `--hosted-pilot`,
  `check_hosted_http.py` takes the worker generation at start; administers a provider,
  model, price and agent in Settings › AI against the loopback fake provider; grants;
  asks and streams through the hosted proxy; and checks that signing out, a password
  reset and disabling the member each cancel a running turn `session_ended`, that
  signing out ends its stream `session_ended`, that a turn asked under another sign-in
  session runs on, that an application session never outlives its sign-in session or
  serves a later one, and that a missing key file is `credential_unavailable`. The database check
  refuses a hosted ask bound to another sign-in session or past its end.
- **Pool isolation:** turns at the workspace limit against a fixed-latency fake, one
  capture per ask and one stream per turn, with a fixed UI mix of working-data and
  explicit-cutoff reads; record environment, pool sizes, UI latency with and without
  agent load, capture lock waits, memory, connections, request contexts per turn and
  `security.request_contexts` and index growth. Not an acceptance claim until O10
  agrees a budget.
- **Independent adversarial review** before each step is published, covering
  authorization and temporal context (steps 2–7), numerical correctness (4, 5, 7),
  async execution (4–6), credential custody and data routing (3–4) and the release
  boundary of every migration (D135), with a final review of the complete phase.

## 14. Decisions

Owner decisions become D rows when recorded. Every other rule in this document is a
design call, recorded as P rows by section and open to correction until the owner
approves it. [decisions.md](decisions.md) records this section as D149–D156, D177,
D184, D185, D190–D193 and P24–P38, with the open-item updates below.

**AGENTS.md exception:** model-provider vendor names (Anthropic, OpenAI, Google, xAI,
Meta) and their public API identifiers may appear in source, documentation, fixtures
and commits; real prices are not shipped.

**Owner decisions:** the three phases; a docked panel with
page questions and navigation, more tools later; Settings › AI's four tabs and the
Agents form as mocked, with saved drafts and Publish replacing its "Test in pop-up"
and unsaved definitions refused; four card kinds, no embedded views and no
agent-written code; server-sent events, not SignalR; P&L comparison next and monthly
caps later, with per-turn limits and full cost reporting meanwhile (a deviation from
architecture §9 provider budgets and Milestone 5 resource budgets); conversation
memory (§7.6); agents created only by `ManageAgents` holders and picked by role, with
`UseAgents` for trader, risk analyst and market data editor; no delivery-price reads;
a new grant when provider or model changes, under a maximum duration; `ReadMarketData`
for model administrators through an operator-assigned role; replay on the user's own
current access; a browser-local agent choice and one agent per conversation; Meta
visible but not enableable until confirmed; answers as completed turns, other turns'
cost on its own line; Usage days in a viewer-picked zone prefilled from Profile, over
half-open ranges; the background indicator and unread History marker; and a phase 2
common notebook alongside agent notebooks.

**Tool decisions** (D177): the record keeps an As of frame's exact pins, from which tool
reads rebuild the page's frame (§7.2); a read's own refusal of its input returns to the
model as a refused step with its code and the page's fixed text; Interval series
and Jobs & Infrastructure are not supported pages (D239 later adds Jobs & Infrastructure); D193 adds Custom curves below; Read market data
reads the pinned frame only, curve history later; a step records its operation, input,
code, read instant and accesses, and results wait for cards (§7.3); worker writes
evaluate the source-access check as a context issued to the turn's agent would, which a
check proves equal (§7.1); Navigate follows the read tools and refuses a handle it did
not issue, or a page the delegator cannot open, as `tool_arguments_invalid`; and a call
whose tool requests would take the turn past its steps has none of them run (§7.5).

**Read tool decisions** (D184, decided on the owner's delegation): working-data reads
bind the context the ask captured, never capturing or taking lock 730849102, and
current-only definitions bind none (§7.2); contract definitions are read with the
schedule library and exposed as `market_data.contract_definitions_v1`; page reports name
one id per navigable view and are refused at ask, in the API, before admission (§8);
refusal texts come from a closed server table, with one generic sentence, and each
operation declares the refusals that make a page's selection unavailable; results show
amounts by `tool_amount_display_v1`, from the inputs the page starts its display with: a
trade's from its contract's increment and the deal's saved formats, and the viewer's own
precision as the default; Navigate is not offered until it is built, and a request for a
tool not offered is unreadable; reads after a call whose usage is unrecognized or past
the input limit run, and the next open ends the turn, the worker checking only the step
limit before reading; a denied read's step publication decides whether the turn
continues; each read is bounded by its version's time per read and its turn's deadline
(D245, which replaces the runtime set's `ToolRead`) and runs on agent pools named
`openetrm-agent-reads`, and one that returns once its turn is to stop is no step.

**Stream decisions** (D185): a stream serves its conversation's latest turn, with a
replay window of 256 of that turn's events; answer text arrives pushed once its
publication recheck commits and every other event, steps included, by a read with the
replay recheck, so text committed before an access loss may still reach a stream
admitted before it, once (security.md states the guarantee); every event says whether
the answer is hidden, and the terminal event whether it is incomplete; `stream_expired`
follows the elapsed limit plus the admission deadline; admission accepts a GET without
Origin and refuses a foreign one; any of the owner's sessions may stream, replacing the
older stream, with no per-session cap, and a stream may start the worker for an
unfinished turn; a lost generation or shutdown ends a stream `agent_runtime_unavailable`
and any failed read but a refused one cuts it; `session_ended` answers a missing session
on every assistant route; the runtime set's version changes; the conversation read
shares the stream's event helper; and Stop and mark read stay idempotent without an
operation identity (§8).

**Navigate decisions** (D190, decided by the owner): a target is a page report plus the
pinned context, labeled by the server; every read issues its own selection, a curve read
its curve and a trade read the trade at the revision it read, and the curve directory and
Blotter rows each curve or trade they list, as handles numbered across the turn within
the result bounds, so the runtime set changes; the worker holds a turn's issued targets
in memory and stores only the target a Navigate step issues, in its event; Navigate's
input names `page` or `target`, the other null; page targets are the eight pages the
assistant reads, each checked by its reads' accesses and permissions under the turn's
grant, a page that does not open adding nothing to the source set; there is no
separate context target; and a `navigation` event follows its step under the
publication recheck and is hidden as text is (§7.3, §8).

**Panel decisions** (D191, decided by the owner): Looking at reports the curve directory,
a curve or its observations by Curves & prices' curve and tab, power hours on Power TOUs
once a frame is chosen and TOU products before, and Blotter rows without their grouping,
totals with it, the trade open in the side panel at its revision, which is the one its form
shows while it is open (D221: the panel is not modal, so the assistant is asked while it is
open; a target for a trade is refused in a pivot), a new trade being entered there by its
product (`blotter_new_future`, `blotter_new_swap`, or `blotter_new_trade` before one is
chosen), which it does not read, the panel's open Risk report as `blotter_risk`, and, with no position
open in the panel, the trade rows under the Risk lens as `blotter_risk_lens` (D222), which it does not read;
only a navigation event arriving live on the stream the tab's own ask opened moves the
page, while the panel is open on an idle page without a dialog, keeping the page's
knowledge and frame and leaving nothing unsaved, and any other target waits for Go, which
selects its context first, a
retained view again by its id and never a new capture; Back restores page, report and
context from the tab's memory, also after a move that failed once it had changed the page,
and both first say so on a page with unsaved edits or an unconfirmed request; a tab keeps its
unfinished turn's stream with the panel closed, re-reading after a gap in its sequences,
its ending lighting the launcher and staying unread in History until the open panel shows
it, and nothing polls; an unconfirmed ask stays, through reloads, until Retry or Discard;
consent dates the grant's end in the person's zone and Allow resends under a new request
identity; the usage line shows exact recorded costs by currency code, `unknown` and
`unpriced`; answers use a Markdown subset drawn from text nodes, and step lines name tools
and reasons in plain words (§8, §10.1).

**Hosted decisions** (D192, decided by the owner, superseding P37): phase 1 runs under
`--hosted-pilot`; an application session belongs to its sign-in session, and revoking or
ending that session cancels its turns and ends its streams `session_ended`; providers
are enabled in Settings › AI alone, with no installation list of vendors;
`host.json` names the credential key files, which provisioning generates and no person
handles; conversations are kept (O11); agents read their workspace's one market and one
reference catalog (O22, O28); the API reaches only a qualified provider's endpoint, a
turn's calls only an enabled one's, and
the host firewall allows outbound TCP 443 and DNS; one serving process holds the worker;
the database allows 80 connections; the gateway is unchanged; and the owner qualifies
a vendor live before anyone else uses it (O09) (§11).

The agent reader role amends D42 (seven role templates).

**Open items:** O04 partly resolved (agent principals only through `ManageAgents`;
standing, revocable delegations with explicit duration; redelegation, MFA and
federation stay open). O09 stays open until real task outcomes are recorded. O07 adds
reclaiming the retained contexts assistant asks consume. O10 gates phase 1 performance
acceptance. O11 keeps conversations until an installation needs a retention policy, and
O28 records per-catalog agent data scope.

**plan.md:** this Milestone 5 slice is brought forward in three phases, with P&L
comparison next and spending caps later.

**Left to the builder:** table, column and function names; migration split per step;
the order of handler extraction, provided pages and tools share each operation;
runtime-set values within the stated constraints; the formatting renderer and the
navigation history structure.


**Curve preparation (D193).** Runtime set `turns_v5` adds
`custom_curves.catalog_v1` (`read_custom_curves`) and
`custom_curves.prepare_v1` (`prepare_custom_curve`), both requiring delegated
`ReadMarketData`. Catalog/preparation call the same managed reads as the editor,
within the existing 500-record/64 KiB result and 8 KiB argument bounds. Preparation
requires code, name, explicit `custom_curve_recipe_v2` engine and formula; no
financial choice is inferred. It returns the validated definition and pinned
preview without creating a business draft. All transitive market/reference
accesses join the turn's replay checks, including calendars of exact older
schedule versions. Dependency evidence identifies work, not PV exposure.

Navigate may follow a preparation's issued handle to a `proposal` target on
Curve builder. Its selection contains only the turn ID and successful step
ordinal; SQL validates both against the same turn's preparation record. It never
auto-opens. **Review proposed curve** protects unsaved edits, then the owner
operation revalidates the original engine, inputs/context and current source
access. It returns only an unpriced definition and origin dates to the ordinary
editor, keeping the selected header. Preview then calculates under that header;
Save and Publish require the person's existing permissions. Revoking the agent's
delegation alone does not revoke the current owner's ordinary source access.
The same owner operation runs in preview and hosted modes, under the current
human's sign-in session. A fresh authorized sign-in may review an earlier
completed proposal; a stale application session cannot.
Unknown/other-owner/non-preparation steps reject. Reopening no longer readable
sources rejects. Preparation itself writes nothing, and no agent-run portfolio risk
or conversation artifact table is introduced. Curve builder's Looking at describes
the saved library and ingredients; unsaved graph/formula edits stay in the editor.
Its page report is `{curve, unsaved}`: the saved curve it has open, or null, and
whether it holds unsaved edits.

**Curve drafts (D260).** D260 supersedes D193's "no agent write" for one tool:
`custom_curves.save_draft_v1` (`save_custom_curve_draft`), in runtime set
`turns_v9`, saves a draft through the person's own Save operation. Migration 0128
lists it with `ReadMarketData` and `WriteMarketDefinitions`, records who saved each
draft generation (a person, or the assistant with its turn and step), limits the
assistant to a new curve or a draft whose latest generation it saved in the same
conversation (`security.custom_curve_agent_writable`), and adds a `draft` target:
exactly the turn ID and a successful save step of that turn. The assistant never
publishes, changes activations or overwrites a person's generation, and refuses
under a historical or As-of pin (`historical_read_only`). The agent writes under
its own `DraftCustomCurves` (§5.4) with its delegator's current
`WriteMarketDefinitions`, only the curve version it claimed with its turn's token
for a step of its running turn; each claim saves exactly one new generation,
recorded as that successful step's, and every agent write is rechecked at commit.

The tool takes the preparation's code, name, engine and formula, with `curveId`
null for a new curve or the curve id an earlier save returned. Its description
tells the model to prepare and report key values first, to save only when the
person asked for a curve to be set up or saved, and that it never publishes. The
worker runs it in this order:

1. A historical pin refuses `historical_read_only`, and a page report naming that
   curve with `unsaved:true` refuses `custom_curve_open_unsaved`, before any claim.
2. The curve's exact sources, calendars and schedules are resolved at the turn's
   pinned context, as preparation reads them.
3. One transaction on one connection, under the agent's context holding only
   `ReadMarketData`: the claim (`custom_curve_agent_scope`,
   `custom_curve_catalog_limit` for a new curve once the catalog holds 64,
   counting curves the agent cannot see, `custom_curve_agent_claimed`,
   `agent_runtime_unavailable`), the store's save
   with `CurveDraftSaver.Assistant`, its saver row and `record_tool_step` for the
   successful step. References that differ from step 2 refuse
   `custom_curve_conflict`; a raw 42501 at a write or a `delegation_denied` at
   commit refuses `delegation_denied`; a code held by a draft the agent cannot
   see (23505) refuses `custom_curve_conflict`, naming nothing of it.
4. Any refusal rolls the whole transaction back, and the worker records the
   refused step as any other. A record the database does not take (a cancel
   requested, a publication refused) is such a refusal.

The request identity is UUIDv5(turn, step), and a new curve's identity is
name-based on the same key. The worker never retries a save step; a re-run of the
same key returns its first outcome from the store's receipt before writing while
the draft is still the assistant's, since the claim precedes the replay (D238). A second save in a turn is a new step and a
new generation. A save identical to the current generation writes nothing and
returns `unchanged_draft`; it has no saver row of its own, so its target is Curve
builder rather than a `draft` target. A saved draft's result carries the curve
(id, code, name, kind, version, generation, `savedBy`) and its `draft` target
`{turnId, step}`, labelled "Saved curve draft: <name>". The panel shows it as
**Open saved draft**, never moves the page by itself, and opens it through
`assistant/curve-draft`, which returns the curve and version that step saved as
the person's own context can read it, behind the page's ordinary leave prompt.
The turn's token reaches only the claim and the step record.
