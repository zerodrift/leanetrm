# Operational limits

Status: proposal (P42), not implementation evidence. Nothing here is implemented.

On 2026-10-27 a hosted close that pins 1,066 prices wrote about 86 KB of pin JSON.
`assistant.pin_turn` refused anything above 64 KiB, so every page-assistant turn over
that close failed `turn_input` before its model was called, and raising the bound
needed a database hotfix and then a migration (D251). The bound was a literal in a
SQL function. Many other operational limits are literals in SQL, C# and TypeScript,
and some are stated twice: the hosted sign-in's 30 minutes is both the cookie's
lifetime and the session row's `expires_at`. This record inventories them and proposes
where each should live.

## Principle

Every limit has two parts, and they live in different places:

- A **ceiling**: the largest value the code is written and tested for. It stays a
  fixed, reviewed value in the code or the database function's checks, and changes
  only with a release (and a migration where the database enforces it).
- An **operating value**: the value an installation actually runs with, at or below
  its ceiling. It lives in explicit, versioned configuration owned by whoever is
  accountable for it.

Some limits are only ceilings: they are part of a versioned contract, protect parsing
or memory before authorization, or are physical facts of the domain. They stay fixed
(see [Fixed safety bounds](#fixed-safety-bounds)).

Rules for every configured value:

- **Explicit.** Each value is stated in full in its configuration. A configuration
  that lacks one, or states one outside its range, is refused with a structured error
  naming the field: the process does not start, or the save is refused. No value is
  filled in by the code.
- **Versioned.** A change is a new immutable revision with its own knowledge time and
  author (D238). The value in force at an instant is resolved by knowledge time, never
  by row order.
- **Recorded.** An operation that applies a value records which revision (or, for host
  configuration, which digest) it used, alongside what it already records, as D245 and
  D249 do with the agent version. A refusal names the bound and the value applied.
- **Carried, not defaulted.** A migration that introduces a configured value gives the
  first revision the value the code applied until then, says so in its decision, and
  marks existing rows as having run under it, as 0120 and 0121 did.

## Where each limit lives

| Home | Owner | Holds | Changed by | Recorded with |
| --- | --- | --- | --- | --- |
| Agent version | Agent administrator (`ManageAgents`) | What one agent's turns may do and see | Saving a new version in Settings › AI | The turn's agent version (already recorded) |
| Installation settings | Access administrator (`ManageAccess`) for sign-in; agent administrator (`ManageAgents`) for assistant capacity | Policy and data-volume limits shared by every user or agent of the installation | An idempotent, audited save in Settings; a new revision | The revision id on the row the operation writes |
| Hosted configuration | Operator | Process and transport limits that must be known at start and must match the gateway, database and host | Editing the configuration file and restarting, through the release gate | The configuration digest logged at start and kept in the release record |
| Fixed | Release review | Ceilings, contract constants and pre-authorization parsing guards | A reviewed release (and migration) | The code version, runtime set or operation version |

## Inventory

Line numbers are at `master` `bd1a612`. "Current" is the value in force today.

### Move to installation settings

| Limit | Where | Current | Bounds | Owner |
| --- | --- | --- | --- | --- |
| Pins on one turn | `db/migrations/0122_pin_turn_bound.sql` (`assistant.pin_turn`); was `0072_tool_steps.sql:366` | 262,144 bytes (65,536 before 0122) | The JSON of an As of frame's pins stored on a turn; grows with the prices a close pins | Agent administrator |
| Hosted sign-in lifetime | `src/OpenEtrm.Api/HostedAuthentication.cs:57` and `db/migrations/0032_hosted_local_accounts.sql:105` | 30 minutes, not sliding, stated twice | How long a hosted sign-in lasts before the person signs in again | Access administrator |
| Page session lifetime | `src/OpenEtrm.Api/Program.cs:1338` (`PreviewSession.Lifetime`) | 30 minutes | A page session; hosted, also capped by the sign-in | Access administrator (follows the sign-in) |
| Sign-in lockout | `db/migrations/0032_hosted_local_accounts.sql:100` (`security.finish_hosted_login`) | 5 failed attempts, then 10 minutes | Password guessing against one account | Access administrator |
| Turns a workspace runs at once | `src/OpenEtrm.Api/RuntimeSet.cs:21` | 4 | Concurrent page-assistant turns per workspace | Agent administrator |
| Longest agent grant | `src/OpenEtrm.Api/RuntimeSet.cs:15` | 90 days | The grant duration an agent version may ask for | Agent administrator |
| Retained contexts reserved for pages | `src/OpenEtrm.Api/RuntimeSet.cs:25`, with the catalog budget in `assistant.pin_turn` (`4096-reserve`, `0122_pin_turn_bound.sql:56`) | 64 of 4,096 | Contexts a turn's capture must leave for page captures | Agent administrator (the 4,096 budget stays a ceiling) |

### Move to the agent version

| Limit | Where | Current | Bounds | Owner |
| --- | --- | --- | --- | --- |
| Tool result records and bytes | `src/OpenEtrm.Api/RuntimeSet.cs:30` | 500 records, 64 KiB | One tool result a model reads; a larger one is refused `result_too_large`, never truncated | Agent administrator |

Tool steps, input and output tokens, time per turn, time per read, retries and the
history window are already agent-version limits (0066, 0120, 0121). Their ranges in
`assistant.save_agent_version` (`0121_agent_history_window.sql`) stay fixed ceilings.

### Move to hosted configuration

| Limit | Where | Current | Bounds | Owner |
| --- | --- | --- | --- | --- |
| Request deadline | `src/OpenEtrm.Api/Program.cs:88` | 15 seconds; 20 for `/api/ppa` | How long one API request may run | Operator |
| Page request timeout | `web/src/app.ts:120` (`transport`, `AbortSignal.timeout(20000)`) | 20 seconds for every request | How long the page waits for any response | Operator, served by the API (see below) |
| Request body | `src/OpenEtrm.Api/Program.cs:35`, `:240` | 32 KiB; 8 MiB for interval-series revisions | Bytes read before a request is parsed | Operator, under fixed ceilings |
| Sign-in attempts per address | `src/OpenEtrm.Api/HostedAuthentication.cs:51` | 12 per minute | Sign-in requests from one client address | Operator |
| Database pools and timeouts | `src/OpenEtrm.Api/HostedConfiguration.cs:49`; `src/OpenEtrm.Api/PreviewDatabase.cs:133-168`; `src/OpenEtrm.Worker/Program.cs:22`, `:48` | Pools of 8 (6 per agent login, 2 per worker); 5-second command and connect timeouts; 30 for agent reads | Connections and command time against the database | Operator |
| Event-stream heartbeat | `src/OpenEtrm.Api/RuntimeSet.cs:41`; `src/OpenEtrm.Api/LiveContexts.cs:48` | 15 seconds (turns), 10 (live contexts) | Silence before a comment, which must stay below the gateway's read timeout | Operator |
| Model provider transport | `src/OpenEtrm.Api/RuntimeSet.cs:7-8` | 5-second connect; 30-second idle; 4 MiB response | One provider call's transport | Operator |

The risk worker, power collection, reference capture and Swarm settings are already
hosted configuration with required, range-checked fields (`RiskWorkerConfiguration`,
`PowerCollectionConfiguration`, `CmeReferenceSettings`, `SwarmSettings`). They are the
pattern the request limits should follow.

### Fixed safety bounds

These stay in code or SQL. Each is a ceiling, part of a versioned contract, or a guard
that must hold before a request is authorized.

| Limit | Where | Value | Why fixed |
| --- | --- | --- | --- |
| Time per read ceiling | `src/OpenEtrm.Api/RuntimeSet.cs:33`; `save_agent_version` | 30 seconds | Bounds how long a read holds the security epoch row (D245) |
| Agent version ranges | `assistant.save_agent_version` (`0121_agent_history_window.sql`) | Tool steps 1–100, tokens, 1–3,600 s, retries 0–10, history 0–1,000 | Ceilings for agent-version values |
| Question, page report, tool input, text chunk | `RuntimeSet.cs:19`, `:28`; `ask_turn` (`0121_agent_history_window.sql:130`); `record_tool_step` (`0086_navigation_targets.sql:89`); `append_turn_text` (`0072_tool_steps.sql:513`) | 4,000 characters; 8 KiB; 8 KiB; 2,048 bytes | Keep each body under the 32 KiB request ceiling; part of runtime set `turns_v7` |
| Agent instructions | `RuntimeSet.cs:13`; `assistant.is_instructions` (`0066_agent_administration.sql:152`) | 6,000 characters (32,000 in SQL) | Same body ceiling |
| Stream replay and buffers | `RuntimeSet.cs:42` | 256 events, 64 per subscriber | Stream protocol of `turns_v7` |
| Admission and worker timing | `RuntimeSet.cs:23`, `:27`, `:35`, `:37`; `TurnStream.cs:127` | 30 s admission, 250 ms coalescing, 1 s backoff, 5 s write, 2 s retry | Derived from the request deadline and worker fencing; recomputed with it in a new runtime set |
| Read view sizes | `src/OpenEtrm.Api/TradeReads.cs:79`; `src/OpenEtrm.Api/AgentTools.cs:555`; directory `LIMIT 100`, job pages of 25 | 64 trades; 64 items; 100; 25 | Part of each versioned view (`trades.ppa_book_v1`, `jobs.list_v1`); a change is a new view version |
| Catalog retained contexts | `assistant.pin_turn` (`0122_pin_turn_bound.sql:56`) | 4,096 | Ceiling of the retained-context store |
| Calculation and calendar limits | `IntervalPrices.cs:15` (35,136), `MonthlySwaps.cs:13` (1,200), `PaymentRules.cs:13` (36,600), `FinancialSwaps.cs:108` (366), `DiscountInterpolation.cs:10` (3,660), `PpaCompiler.cs:14-16`, `:145`, `MonthlyGrossPreview.cs:46`, `IntervalSeries.cs:14` (200,000), `Overlays.cs:27`, `GraphSession.cs:30` | as listed | Part of calculation contracts and their numerical tests; changes need a new contract version |
| Exact arithmetic | `src/OpenEtrm.Shared/Numerics/SignedRatio.cs:61` | 18 digits | Numerical policy (P15) |
| Import and provider response sizes | `ReferenceImportProtocol.cs:83`, `IceReferenceCapture.cs:72-73`, `CmeReferenceCapture.cs:31`, `PowerPriceClient.cs:74-94`, `ReferenceImportCommand.cs:22`, `:26`, `ReferenceCaptureCommand.cs:43`, `SettlementPreview.cs:28` | 1–48 MiB; 16 MiB pages; 256 MiB captures | Memory guards on untrusted input |
| Close and risk payload caps | `automation.publish_pilot_close`, `security.register_market_close` (20,000), `risk.submit`/`risk.retain_*` (4,096 factors, 100,000 rows), `CloseRiskAdmission.cs:51`, `RiskJobs.cs:23` | as listed | Ceilings of the close and risk contracts; their operating values are already job terms |
| Text lengths | display names 80, memos 4,000/2,000, PPA numbers 64, reference display 48 | as listed | Storage and display contracts |
| Credential custody | `CredentialCustody.cs:60` | 32/12/16 bytes | AES-GCM parameters |
| Page pacing | `web/src/app.ts:673` (250 ms), `web/src/risk-view.ts:448` (750 ms), `web/src/interval-refresh.ts:10` (Off, 5 s, 30 s), `web/src/assistant-text.ts:55`, `web/src/blotter-layout.ts:51` | as listed | Presentation only: redraw pacing, a person's refresh choice and saved-layout validation. No business rule depends on them |

## Recording the value used

- **Agent version.** The turn records its agent version; nothing new.
- **Installation settings.** Each revision table has an id. `assistant.turns` gains
  `limits_revision`, set at ask and pin, and `security.hosted_sessions` gains
  `policy_revision`, set at sign-in. Each refusal names its bound and value, as
  `limit_tool_read` does. A turn or session recorded before the migration carries the
  migration's first revision only when that revision holds the value it actually ran
  under; turns pinned before 0122 ran under 64 KiB, so the pins bound's first revision
  must not be backdated to them. Those rows keep `NULL`, which the decision defines as
  "before installation limits", checked by a constraint on their `asked_at`.
- **Hosted configuration.** At start the API logs the configuration's SHA-256 and the
  limits it applies, once, and refuses to start without them. The release record for
  each hosted deploy keeps the same digest, so the values in force at any instant are
  known from the deploy history. No new table: there is no consumer for per-request
  rows yet.
- **The page.** The page stops owning a request timeout. The API serves the request
  deadline it applies (one per route class) in the `access` response the page already
  reads at start, and the page waits that long plus a fixed transport margin. The page
  refuses to send requests until it has those values, rather than falling back to 20
  seconds. Today the page waits 20 seconds for routes the API stops at 15.

## Migrations and changes

1. **0122** (D251, done separately): pins bound 262,144 bytes, matching the pilot hotfix.
2. **Assistant installation limits**: `assistant.installation_limit_revisions`
   (revision id, pins bytes, workspace turns, retained-context reserve, longest grant
   days, recorded at, by, operation id), typed columns with `CHECK`s at the ceilings,
   immutable and append-only. First revision carries 262,144 / 4 / 64 / 90.
   `assistant.save_installation_limits(operation, expected_revision, …)` under
   `ManageAgents`, idempotent by operation id (D238). `pin_turn` and `ask_turn` read the
   revision in force and record it on the turn. New runtime set `turns_v8` without the
   moved values.
3. **Sign-in policy**: `security.sign_in_policy_revisions` (lifetime minutes, failed
   attempts, lockout minutes) under `ManageAccess`; first revision carries 30 / 5 / 10.
   `finish_hosted_login` reads it and records the revision on the session. The cookie's
   expiry is set from the session's `expires_at`, so the lifetime is stated once; the
   page session ends with it.
4. **Tool result bounds on the agent version**: `agent_versions.tool_result_rows` and
   `tool_result_bytes`, saved and checked like `history_turns` (0121); versions saved
   earlier take 500 and 65,536. Ceilings 5,000 records and 1 MiB, pending measurement.
5. **Hosted configuration** (no migration): a required `limits` object in
   `HostedConfiguration` with request deadlines, body sizes, pools and timeouts, the
   sign-in rate, heartbeats and provider transport, each range-checked; the API refuses
   to start without it. `tools/provision_hosted.py` writes it and the release gate
   checks it. A configuration from before this has none and is refused, with no shim.
6. **Settings UI**: one page per owner showing the revision in force, its history and
   an edit form, using the same operations as any other client.

Each step is its own PR with its own failing checks first, and each migration that
replaces a function pins the definition it replaces. On the pilot, 0122 accepts the
hotfixed `pin_turn`, so the next schema window's "schema as expected" step should
expect 0122's hash (`7b49e5dc…`) for that function once 0122 is applied, and either
hash before.

## Open choices

1. Whether the hosted sign-in should slide with activity or keep a fixed lifetime
   (today: fixed 30 minutes). This proposal keeps it fixed and configurable.
2. Whether the pins bound belongs to the installation (proposed: it grows with the
   installation's closes, not with any one agent) or to each agent version.
3. The ceilings for the moved values, which need measurement before they are set:
   pins (proposed 1 MiB), tool results (5,000 records, 1 MiB), sign-in lifetime
   (5 minutes to 12 hours).
4. Whether the sign-in rate limit is an operator value (proposed) or part of the
   access administrator's sign-in policy.
