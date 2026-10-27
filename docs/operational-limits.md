# Operational limits

Status: proposal (P42), not implementation evidence. Nothing here is implemented.

On 2026-10-27 a hosted close that pins 1,066 prices wrote about 86 KB of pin JSON.
`assistant.pin_turn` refused anything above 64 KiB, so every page-assistant turn over
that close failed `turn_input` before its model was called. Raising the bound needed a
database hotfix and then a migration (D256), because the bound was a literal in a SQL
function. Many other operational limits are literals in SQL, C#, TypeScript and the
deploy files, and some are stated in several places that must agree. The hosted
sign-in's 30 minutes is both the cookie's lifetime and the session row's `expires_at`.
The 32 KiB body and 20-second read timeout are each in both the API and the gateway.
This record inventories every limit and proposes where each should live.

## Principle

Every limit has two parts, and they live in different places:

- A **ceiling**: the largest value the code is written and tested for. It stays a
  fixed, reviewed value in the code or the database function's checks, and changes
  only with a release (and a migration where the database enforces it).
- An **operating value**: the value an installation actually runs with, at or below
  its ceiling. It lives in explicit, versioned configuration owned by whoever is
  accountable for it.

Some limits are only ceilings: they are part of a versioned contract, protect parsing
or memory before authorization, or are facts of the domain. They stay fixed.

Rules for every configured value:

- **Explicit.** Each value is stated in full in its configuration. A configuration
  that lacks one, or states one outside its range, is refused with a structured error
  naming the field: the process does not start, or the save is refused. The code
  fills in no value.
- **Versioned.** A change is a new immutable revision with its own knowledge time and
  author (D238). The value in force at an instant is resolved by knowledge time, never
  by row order.
- **Recorded.** An operation that applies a value records which revision (or, for host
  configuration, which digest) it used, alongside what it already records, as D245 and
  D249 do with the agent version. A refusal names the bound and the value applied.
- **Carried, not defaulted.** A migration that introduces a configured value gives the
  first revision the value the code applied until then, says so in its decision, and
  marks existing rows as having run under it, as 0120 and 0121 did.
- **Stated once.** A value that the API, the database, the gateway and the page must
  agree on comes from one source. The page reads limits from the API, as the PPA and
  settlement previews already do with their `limits` objects. The gateway's
  configuration is written from the hosted configuration.

## Where each limit lives

| Home | Owner | Holds | Changed by | Recorded with |
| --- | --- | --- | --- | --- |
| Agent version | Agent administrator (`ManageAgents`) | What one agent's turns may do and see | Saving a new version in Settings › AI | The turn's agent version (already recorded) |
| Installation settings | Access administrator (`ManageAccess`) for sign-in; agent administrator (`ManageAgents`) for assistant capacity | Policy and data-volume limits shared by every user or agent of the installation | An idempotent, audited save in Settings; a new revision | The revision id on the row the operation writes |
| Hosted configuration | Operator | Process, transport and capacity limits that must be known at start and must match the gateway, database and host | Editing the configuration and restarting, through the release gate | The configuration digest logged at start and kept in the release record |
| Job terms or command settings | The job's owner, or the operator for a command | Per-run values already saved with each job or read from a command's configuration file | Saving the job, or editing the command's configuration | The job revision or run (already recorded) |
| Fixed | Release review | Ceilings, contract constants and guards | A reviewed release (and migration) | The code version, runtime set or operation version |

## Complete inventory

How it was made: every numeric literal in `src/` (C#), `web/src/` (TypeScript, not
tests) and `deploy/`, and every bound in the current definitions of all SQL functions
and table `CHECK` constraints of a database at migration 0122, were extracted and
reviewed one by one. Line numbers are at `master` `bd1a612` plus 0122. Literals that
are not limits are not listed: HTTP status codes, regular-expression quantifiers of
fixed formats, SQL parameter positions and column ordinals, bit arithmetic, `LIMIT 1`
lookups, chart and SVG geometry, example values and display formatting. Retained
`*_pre_*` copies of superseded SQL functions are not listed; they are no longer called.

Each row is either **Move**, with its home, or **Fixed**, with its reason:
*contract* (part of a versioned operation, view, runtime set or calculation; a change
is a new version), *domain* (a fact of calendars, units or numbers), *guard* (bounds
memory or work on untrusted input), *storage* (a stored field's length) or
*presentation* (affects only how the page draws).

### Assistant

| Limit | Where | Value | Decision |
| --- | --- | --- | --- |
| Pins on one turn | `assistant.pin_turn` (`0122_pin_turn_bound.sql:37`) | 262,144 bytes | **Move:** installation, assistant (ceiling 1 MiB, proposed) |
| Retained contexts per catalog | `assistant.pin_turn` (`0122_pin_turn_bound.sql:56`); also `security.capture_schedule_context`, `security.save_trade_party` | 4,096 | Fixed: guard, the retained-context store's ceiling. One constant in three functions, to share in one helper |
| Retained contexts reserved for pages | `src/OpenEtrm.Api/RuntimeSet.cs:25` | 64 | **Move:** installation, assistant |
| Turns a workspace runs at once | `RuntimeSet.cs:21` | 4 | **Move:** installation, assistant |
| Longest agent grant | `RuntimeSet.cs:15` | 90 days | **Move:** installation, assistant |
| Tool result records and bytes | `RuntimeSet.cs:30` | 500, 64 KiB | **Move:** agent version (ceilings 5,000 and 1 MiB, proposed) |
| Agent pool per login | `RuntimeSet.cs:33` | 6 | **Move:** hosted (must exceed workspace turns) |
| Time per read ceiling | `RuntimeSet.cs:33`; `assistant.save_agent_version` | 30 s | Fixed: guard, bounds how long a read holds the epoch row (D245) |
| Agent version ranges | `assistant.save_agent_version` (`0121_agent_history_window.sql:73-76`); `assistant.agent_versions` checks | Tool steps 1–100, input tokens 1–10⁸, output tokens 1–10⁷, 1–3,600 s per turn, 0–10 retries, 0–1,000 history turns, description 500 | Fixed: ceilings of agent-version values |
| Provider connect, idle, response | `RuntimeSet.cs:7-8` | 5 s, 30 s, 4 MiB | **Move:** hosted (response size ceiling stays fixed) |
| Provider test | `RuntimeSet.cs:9`, `:11` | 256 output tokens, 10 s | Fixed: contract, derived from the 15-second request deadline. Recompute when the deadline moves |
| Question, agent instructions | `RuntimeSet.cs:19`, `:13`; `assistant.is_instructions` (`0066_agent_administration.sql:152`) | 4,000 and 6,000 characters (32,000 in SQL) | Fixed: contract, keeps each body under the 32 KiB request ceiling |
| Page report, tool input, navigation target | `ask_turn` (`0121_agent_history_window.sql:130`); `record_tool_step` (`0086_navigation_targets.sql:89`); `is_navigation_target`; `assistant.turns`, `tool_steps` checks; `AgentTools.cs:218` | 8 KiB each; target label 200 | Fixed: contract (`turns_v7`) |
| Streamed text | `RuntimeSet.cs:27-28`; `append_turn_text` (`0072_tool_steps.sql:513`) | 250 ms, 2,048 bytes | Fixed: contract (`turns_v7`) |
| Turn events | `RuntimeSet.cs:41-42`; `assistant.turn_events` check | 15 s heartbeat; 256 replayed, 64 buffered; 16 KiB per event | Heartbeat **Move:** hosted (must stay below the gateway's read timeout); the rest fixed: contract |
| Admission and worker timing | `RuntimeSet.cs:23`, `:35`, `:37`; `TurnStream.cs:127` | 30 s admission, 1 s backoff, 5 s write, 2 s retry | Fixed: contract, derived from the request deadline. Recompute in a new runtime set when it moves |
| Tool argument shapes | `AgentTools.cs:102-152`, `:555`, `:590`; `AgentTools.CustomCurves.cs:16-18`; `AgentTools.Jobs.cs:22` | 64 ids, 64 items, 160-character search, 200 reference rows, 6,144-character text and formulas, job pages of 25 up to page 10,000 | Fixed: contract, part of each tool's versioned schema |
| Read view sizes | `TradeReads.cs:79`; `MarketReads.cs:128`; `TradingBlotter.cs:20`; `PreviewDatabase.cs:421`, `:76` | 64 PPA trades, 64 shaped curves, 4 summaries, 200 reference rows, offset 100,000 | Fixed: contract, part of each view version |
| Directory pages | `agent_directory`, `model_directory`, `provider_directory`, `usage_directory`, `workspace_grants`, `security.administration_directory` | 100 | Fixed: contract |
| Model and price ranges | `assistant.edit_model`, `open_test`, `append_price`; `model_versions`, `model_calls` checks; `ModelCatalog.cs:148`; `ResponsesAdapter.cs:18` | 10⁷ tokens; prices to 999,999,999,999; at least 16 output tokens for one vendor | Fixed: contract and vendor protocol |
| Identifiers and names | `are_identifiers` (16), `is_display_name` (80), `is_source` (2,048), `is_terms_text` (1,000), `ModelBoundary.cs:18` (128), `ModelProviders.cs:102` (key 8–512), `assistant.credentials` (512 bytes) | as listed | Fixed: storage |
| Workspace members | `assistant.create_agent`, `security.apply_administration`; `ModelCatalog.cs:176` | 256 | Fixed: guard. Could become installation capacity if a workspace needs more |
| Provider qualification date | `assistant.is_checked_on` (`0058_provider_qualification.sql:60`) | 14 hours | Fixed: domain, the earliest time zone's lead over UTC |
| Usage figures | `assistant.usage_figures` | 1 microsecond | Fixed: domain |

### Sign-in and sessions

| Limit | Where | Value | Decision |
| --- | --- | --- | --- |
| Hosted sign-in lifetime | `HostedAuthentication.cs:57`; `security.finish_hosted_login` (`0032_hosted_local_accounts.sql:105`) | 30 minutes, not sliding, stated twice | **Move:** installation, sign-in. The cookie expires with the session row |
| Page session lifetime | `Program.cs:1338` | 30 minutes | **Move:** follows the sign-in lifetime |
| Lockout | `finish_hosted_login` (`0032_hosted_local_accounts.sql:100`) | 5 failures, then 10 minutes | **Move:** installation, sign-in |
| Password length | `HostedAccess.cs:25`, `:32` | 15–1,024 characters | Minimum **Move:** installation, sign-in; the 1,024 maximum stays fixed: guard |
| Invitation lifetime | `tools/provision_hosted.py:87` | 24 hours | **Move:** installation, sign-in |
| Sign-in attempts per address | `HostedAuthentication.cs:51` | 12 per minute | **Move:** hosted |
| Page sessions per process | `Program.cs:303` | 64 | **Move:** hosted (capacity) |
| Session frames, live frames | `PreviewSessionFrames.cs:12`; `Program.cs:865`, `:905`; `PreviewSessionLive.cs:28`; `MarketDates.cs:43`; `MarketCloses.cs:57` | 128 frames, 4 live | Fixed: guard, per-session memory |
| Request context lifetime | `security.issue_context` | at most 300 s | Fixed: guard |
| Profile and names | `PreviewDatabase.cs:174` (48), `HostedAccess.cs:13` (48), `claim_hosted_invitation` (80; token 2,048) | as listed | Fixed: storage |
| HSTS | `Program.cs:82` | one year | Fixed: security header |

### Requests, gateway and processes

| Limit | Where | Value | Decision |
| --- | --- | --- | --- |
| Request deadline | `Program.cs:88` | 15 s; 20 s for `/api/ppa` | **Move:** hosted |
| Gateway read timeout | `deploy/nginx.conf.template:25` | 20 s | **Move:** hosted, written from the same value (must exceed the longest deadline) |
| Page request timeout | `web/src/app.ts:120` | 20 s for everything | **Move:** served by the API from the hosted deadline; the page keeps none of its own |
| Request body | `Program.cs:35`, `:240`; `deploy/nginx.conf.template:16`, `:72`, `:79`, `:84` | 32 KiB; 8 MiB for interval-series revisions | **Move:** hosted, written to both; ceilings fixed |
| Database pools and timeouts | `HostedConfiguration.cs:49`; `PreviewDatabase.cs:133-168`; `Worker/Program.cs:22`, `:48` | 8, 6 and 2 connections; 5 s command and connect | **Move:** hosted |
| Database connections | `deploy/compose.yaml:15` | `max_connections=80` | **Move:** hosted; the start check proves it exceeds the sum of pools |
| Live contexts | `LiveContexts.cs:48`, `:74`, `:89` | 10 s heartbeat, 5 s write, 250 ms poll | **Move:** hosted |
| Calculation change poll | `CalculationChanges.cs:42` | 1 s | **Move:** hosted |
| Container resources | `deploy/compose.yaml:21-68`; `deploy/swarm-workers.yaml:31`, `:83`; `deploy/risk-workers.swarm.yaml:26-28`; `SwarmWorkloads.cs:145`, `:183` | memory, CPU, process and `/tmp` limits; restart delay | Already deploy configuration, with no check that they agree with the pools and workers. Add one to the release gate |
| Gateway, timers and services | `deploy/nginx.conf.template:38`, `:44`; `openetrm-collection.service:9-10`; `openetrm-collection.timer:5`; `openetrm-deployment.service:18`; `openetrm-deployment.timer:7`; `openetrm-worker-database-proxy.service:10`, `:24` | static cache 300 s; 2 h and 10 min start timeouts; 08:00 UTC collection; 600 s waits | Already deploy configuration. Keep there |
| Release tooling | `tools/deploy_release.py:75`, `:103`, `:222`, `:403`, `:420` | 15–180 s step timeouts | Fixed: operator tooling, outside the application |
| Swarm gateway | `SwarmGateway.cs:30`, `:95`, `:99`, `:115`; `SwarmConnection.cs:19`; `Worker/Program.cs:75` | 1,024 nodes, 4 MiB, 80-character names; 1–15 s timeout | Fixed: guard; the timeout is already configuration |
| Infrastructure freshness | `automation.infrastructure_directory`, `request_node_command`, `resolve_node_command` | 30 s | **Move:** hosted, with the worker poll it must exceed |
| Node observations | `automation.observe_nodes` | 1,024 | Fixed: guard |

### Jobs, workers and collection

| Limit | Where | Value | Decision |
| --- | --- | --- | --- |
| Job schedule, timeout, attempts | `Jobs.cs:46-82`; `automation.update_job`, `create_job`; `automation.jobs` checks; `PilotCloseJobs.cs:73-94` | 60 s to 1 year; 1–3,600 s; 1–3 attempts; first run within 1–366 days; 128 books and curves | Fixed: ceilings. The values are already job terms |
| Worker poll and lease | `Worker/Program.cs:20-21`; `automation.claim_next`, `claim_node_command`, `renew_close_risk_lease`; `RiskWorker.cs:16`, `:86`; `risk.claim`, `risk.heartbeat` | poll 1–60 s, lease 5–120 s (risk 15–300 s), timeout 1–3,600 s; claim 32 at a time | Fixed: ceilings. The values are already configuration |
| Job pages | `automation.job_list`, `owned_jobs`, `owned_job_history`, `power_data.job_directory`, `risk.list`, `infrastructure_directory` | 25, 50, 100; up to page 10,000 | Fixed: contract |
| Power collection | `PowerCollection.cs:21`, `:43`, `:48`; `PowerPriceClient.cs:29`, `:74-94`; `PowerImportProtocol.cs:23`; `power_data.finish_attempt`, `reserve_request`; `power_data.sources`, `request_gates` checks | timeout up to 2,700 s; 60 s HTTP; 16 MiB pages; 1–31 days; 100,000 rows; 32,000,000 bytes; 1-minute gate | HTTP timeout **Move:** command settings (beside `TimeoutSeconds`); the rest fixed: guard and ceilings of existing settings |
| Reference capture and import | `ReferenceCaptureCommand.cs:16`, `:40`, `:43`; `ReferenceImportCommand.cs:21-31`; `ReferenceImportProtocol.cs:75-163`; `CmeReferenceCapture.cs:29-51`, `:151`; `IceReferenceCapture.cs:61-73`; `security.import_reference_capture`; `ref_data` checks | 30 min and 5 min deadlines; 60 s HTTP; 30 s commands; 1–48 MiB files; 50,000 records; 1,024 documents; 16–256 MiB captures | Deadlines and HTTP timeout **Move:** command settings; the rest fixed: guard |
| Close and risk payloads | `automation.create_close_risk_job`, `publish_pilot_close`; `security.register_market_close`, `read_market_close_parts`; `risk.submit`, `retain_axes`, `retain_connected`, `retain_market`, `finish`, `guard_run`; `CloseRiskAdmission.cs:51-55`; `RiskJobs.cs:23`; `automation.close_risk_profiles`, `execution_profiles` checks | 2×10⁹ row-scenarios; 4,096 factors; 100,000 rows; 20,000 marks; 256 parts; 16 stresses; 32,000,000-byte reports | Fixed: contract ceilings; the values are job terms |
| Pilot mark moves | `PilotMarkGenerator.cs:15`; `automation.pilot_close_moves` check | 10,000 ticks | Fixed: contract |

### Trading, curves, PPAs and settlement

| Limit | Where | Value | Decision |
| --- | --- | --- | --- |
| Revisions per trade | `SwapStore.cs:437`; `SwapScheduled.cs:195`; `security.save_*`, `amend_futures*`, `book_futures*`; `trade_revisions` checks | 256 | Fixed: contract |
| Reasons, memos, names | `SwapStore.cs:439` and similar; `TradeAnnotations.cs:28`; `security.require_trade_annotation`, `save_ppa_booking`; many name checks | reason 500, memo 4,000 (PPA 2,000), names 80, codes 48 or 64 | Fixed: storage |
| PPA booking request | `security.save_ppa_booking` | 262,144 bytes | Fixed: guard |
| PPA sizes | `PpaCompiler.cs:14-37`, `:145`; `PpaInstrument.cs:58-86`; `PpaStore.cs:75`, `:169-211`; `security.retain_ppa_terms`; `ppa_price_steps` check; `IntervalValuationSession.cs:61` | 8 members, 100 assets, 256 bindings, 360 or 120 months, 2,000,000 rows, 8 MiB responses, 4,000,000 cells, pages of 64 or 128 | Fixed: contract (already served to the page as `limits`) |
| Settlement preview | `SettlementPreview.cs:28-58`; `MonthlyGrossPreview.cs:46`; `settlement_data` functions and checks | 16 MiB; 32 bindings, 4 assets, 3,100 cells; 256 trades | Fixed: contract (already served as `limits`) |
| Calendars and dates | `RateConventions.cs:22`; `PaymentRules.cs:13-58`; `MonthlySwaps.cs:13`; `FinancialSwaps.cs:84-108`; `DiscountInterpolation.cs:10`; `MonthlyDates.cs:17`; `DateRolling.cs:30`; `DateWorkspace.cs:52-65`; `PowerHours.cs:70-74`; `ContractSequence.cs:28`; `security.swap_*` functions | years 1901–2199; 36,600 calendar days; 1,200 or 120 months; 366 days; 3,660 calendar days; ±10,000 business days | Fixed: domain and contract |
| Interval data | `IntervalPrices.cs:15`; `IntervalSeries.cs:14`; `IntervalCurvesStore.cs:35`; `IntervalData.cs:12`; `PowerSettlement.cs:33`; `AssetVolumes.cs:59-71`; `market_data.open_interval_series_read`, `append_interval_series_revision` | 35,136 intervals; 200,000 per revision; 31-day reads; 110,000 periods | Fixed: contract |
| Exact numbers | `ExactDecimal.cs:18-71`; `ExactQuantity.cs:23`; `ExactRatio.cs:16`; `AmountFormat.cs:19-53`; `SignedRatio.cs:61`; `RecipeModel.cs:66`; numerator and denominator checks | 18 places, 4,096 bits, 10⁴², 1,234 digits | Fixed: domain (P15) |
| Curve recipes | `RecipeEngine.cs:31-264`; `RecipeFormula.cs:12-57`; `RecipePanels.cs:27`; `ConnectedRecipe.cs:65-296`; `CustomCurves.cs:113-334`; `CustomCurves.Connected.cs:104`; `CustomCurves.Published.cs:31`, `:58`; `CurveLookup.cs:12-29`; `security.publish_custom_curve_activation`; `custom_curve_*` checks | 50,000 work units, 64 nodes, 16,384-character formulas, depth 32, 120 positions, 366 dates, 16,384 contracts | Fixed: contract |
| Risk scenarios | `CurveScenarios.cs:26-56`; `DealRisk.cs:37`; `RiskEngine.cs:132`; `PortfolioRiskPreview.cs:89-91`; `GraphSession.cs:30`; `SwapDraft.cs:82`; `FuturesDraft.cs:39` | 16 bumps, 4,096 shifts, depth 64, 128 factors, 16 portfolios | Fixed: contract |
| Futures and books | `Futures.cs:191-249`; `BookValuation.cs:219`; `FuturesBlotter.cs:44`; `ClosePortfolioValuation.cs:60`; `PortfolioValuation.cs:160`; `security.prepare_futures_binding` | 100,000 lots, trades and positions; 128 books; 256 rows | Fixed: contract |
| Units and formats | `UnitConversion.cs:27`; `UnitOperations.cs:45-50`; `security.save_trade_amount_format`; `presentation.trade_amount_formats` check | 256 definitions, 1,024 conversions, 18 places | Fixed: contract |
| Saved layouts | `TableViews.cs:27-128`; `presentation.table_views` check | 512-character keys, 128 columns, 20 views, 24,000 bytes | Fixed: storage |
| Preview personas | `SchedulePreview.cs:98`; `DateWorkspace.cs:111`, `:153`; `ScheduleStore.cs:79-97`; `CustomCurvesStore.cs:108-264`; `ContractDateStore.cs:138-153`; `TouProductStore.cs:68-85` | 16 to 64 drafts or versions | Fixed: guard on the disposable preview's memory |
| Reference editing | `ReferenceEditors.cs:46-74`; `security.save_reference_display_edit` | 300 fields, 600 characters, 20 revisions shown | Fixed: storage and contract |

### Page code

The page has no business limits of its own. These literals are presentation, or copies
of server limits that the page should read from the API instead of restating:

| Limit | Where | Value | Decision |
| --- | --- | --- | --- |
| Request timeout | `app.ts:120` | 20 s | **Move:** served by the API (above) |
| Copies of server limits | `blotter-layout.ts:32-51` (`TableViews.cs`); `risk-view.ts:78` (`RiskJobs.MaxParts`); `risk-presets.ts:38`, `risk-view.ts:327` (16 stresses and bumps); `date-editor.ts:214` (120 contracts); `curve-blend.ts:31` (30 inputs); `analytics-model.ts:30` (128 columns); `app.ts:616` (200 rows); `trade-annotations.ts:36` (4,000); `ppa-editor.ts:37` (1,000); `ai-settings.ts:44`, `agent-settings.ts:110-113` (range messages) | as listed | **Move:** the API serves each operation's limits, as PPA and settlement previews already do; the page reads them and keeps no copy |
| Saved preferences | `preferences.ts:17-19` | 128 and 64 entries | Fixed: storage of a browser preference |
| Pacing and display | `app.ts:673` (250 ms); `risk-view.ts:448` (750 ms); `interval-refresh.ts:10` (Off, 5 s, 30 s); `assistant-text.ts:55` (2,000); `assistant-panel.ts:430-445`; `power-view.ts:67` (60 rows); `ppa-editor.ts:141-175` (48 rows); `amount-display.ts:35` (128 per batch); `time-chart.ts:210` (6 places); `chart-view.ts`; `desk-model.ts:83` | as listed | Fixed: presentation |

## Recording the value used

- **Agent version.** The turn records its agent version; nothing new.
- **Installation settings.** Each revision table has an id. `assistant.turns` gains
  `limits_revision`, set at ask and pin, and `security.hosted_sessions` gains
  `policy_revision`, set at sign-in. Each refusal names its bound and value, as
  `limit_tool_read` does. A row recorded before the migration carries the first
  revision only when that revision holds the value it actually ran under. Turns pinned
  before 0122 ran under 64 KiB, so the pins bound's first revision must not be
  backdated to them. Those rows keep `NULL`, which the decision defines as "before
  installation limits", checked by a constraint on their `asked_at`.
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

1. **0122** (D256, done separately): pins bound 262,144 bytes, matching the pilot hotfix.
2. **Assistant installation limits**: `assistant.installation_limit_revisions`
   (revision id, pins bytes, workspace turns, retained-context reserve, longest grant
   days, recorded at, by, operation id), typed columns with `CHECK`s at the ceilings,
   immutable and append-only. First revision carries 262,144 / 4 / 64 / 90.
   `assistant.save_installation_limits(operation, expected_revision, …)` under
   `ManageAgents`, idempotent by operation id (D238). `pin_turn` and `ask_turn` read the
   revision in force and record it on the turn. New runtime set `turns_v8` without the
   moved values.
3. **Sign-in policy**: `security.sign_in_policy_revisions` (lifetime minutes, failed
   attempts, lockout minutes, minimum password length, invitation hours) under
   `ManageAccess`; first revision carries 30 / 5 / 10 / 15 / 24. `finish_hosted_login`
   and invitation issue read it and record the revision. The cookie's expiry is set
   from the session's `expires_at`, so the lifetime is stated once; the page session
   ends with it.
4. **Tool result bounds on the agent version**: `agent_versions.tool_result_rows` and
   `tool_result_bytes`, saved and checked like `history_turns` (0121); versions saved
   earlier take 500 and 65,536.
5. **Hosted configuration** (no migration): a required `limits` object in
   `HostedConfiguration` with request deadlines, body sizes, pools, timeouts and
   `max_connections`, the sign-in rate, page sessions, heartbeats and polls,
   infrastructure freshness and provider transport, each range-checked against the
   others (pools below `max_connections`, heartbeats below the gateway timeout, the
   gateway timeout above the longest deadline). The API refuses to start without it.
   `tools/provision_hosted.py` writes it and renders the gateway's body sizes and read
   timeout from it; the release gate checks it. A configuration from before this has
   none and is refused, with no shim. The power-collection and reference commands gain
   their HTTP timeouts and deadlines in their own configuration files.
6. **Limits served to the page**: each operation the page validates against serves its
   limits with its other options, and the page's copies are removed.
7. **Settings UI**: one page per owner showing the revision in force, its history and
   an edit form, using the same operations as any other client.
8. **One retained-context ceiling**: the 4,096 budget is read from one helper by every
   function that captures a context.

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
5. Whether workspace members (256) should become installation capacity.
