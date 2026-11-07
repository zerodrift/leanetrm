# Rolling Date Lists and Curve Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a generated daily schedule, a generic date-list mapping, an interest-rate curve kind, and durable save/publish/edit of curve definitions in a Curve definitions view.

**Architecture:** The daily rule is a new schedule engine (`date_schedule_v4`) compiled in memory into the existing occurrence list, so the existing `on_or_after` resolver selects from it. Date lists are a second engine (`date_list_v1`) of the existing contract-date library, sharing its save, publish, request and receipt machinery. Curve definitions get their own store modelled on the contract-date library; a curve save can also save its mapping's rows as a draft, and a curve publish publishes that mapping draft first in the same transaction.

**Tech Stack:** C# (.NET 10.0.402), PostgreSQL 16 migrations, TypeScript web client, Python preview integration checks, Node browser checks.

**Spec:** `docs/superpowers/specs/2026-11-06-rolling-date-lists-and-curve-editor-design.md`

## Global Constraints

- `AGENTS.md` applies: TDD, no silent defaults, idempotent writes (D238), historical context on every read, exact-version foreign keys, presentation-only TypeScript.
- Date expressions stay data references; no executable expressions.
- Daily rule: `days` is `"calendar"` or `"business"`; selection is always `on_or_after`; explicit inclusive `firstDate`/`lastDate`; maximum span 100 years (36,525 days); dates never stored.
- Date list: label 1 to 80 characters, unique per version; at most 120 rows; resolved dates strictly increasing in row order, else `date_list_order`.
- Rate curve: required code, name, interest-rate specification, mark source, exact date-list mapping version; currency comes from the specification; any contract-only term is refused; a maturity on or before `t_0` fails with `rate_maturity` naming the label; no rates or discount factors shown in this project.
- Curve definitions: at most one draft per code; at most 64 versions per code; save needs `WriteMarketDefinitions`, publish `SealMarketDefinitions`, reads `ReadMarketData`; retries with the same request id and payload return the first outcome; stale generations are refused.
- Rendered curves expose no edit operation.
- Examples and fixtures are synthetic.
- Do not push or publish; migrations need a schema window on the hosted demo.

## Review Focus

- A business-day daily schedule resolved where its calendar has no coverage: expect `no_business_day`, never a date past coverage. (Task 1)
- A `+Nbd` offset on a calendar-day daily schedule without a bound calendar: expect `calendar_reference`. (Task 2)
- A curve save whose inline rows target a mapping draft someone else changed in Settings: expect `contract_date_conflict`, and neither draft written. (Task 6)
- A curve publish where the mapping draft fails validation at publish: neither the mapping nor the curve is published. (Task 6)
- A historical knowledge view earlier than a curve's publication: the version is invisible to that view. (Task 6)

---

### Task 1: Daily date generation

**Files:**
- Create: `src/OpenEtrm.Shared/Temporal/DailyDates.cs`
- Test: `tests/OpenEtrm.Shared.Checks/ScheduleChecks.cs`

**Interfaces:**
- Produces: `public enum DailyDays { Calendar = 1, Business }`; `DailyDates.Generate(DateOnly first, DateOnly last, DailyDays days, DateSet? businessDays) -> ImmutableArray<MonthlyOccurrence>` where each occurrence is `new(new(d.Year,d.Month,1), d, d)`. `MaximumDays = 36525`.

- [ ] **Step 1: Write the failing checks** in `ScheduleChecks.Run()` using the file's `Check`/`Eq`/`Reject` helpers and its existing `calendar` (2026-01-01 to 2026-05-01):
  - `"daily calendar dates include every day inclusively"`: `Generate(2026-01-01, 2026-01-10, Calendar, null)` has 10 dates, first 2026-01-01, last 2026-01-10, including the weekend 2026-01-03.
  - `"daily business dates follow the calendar's open days"`: `Generate(2026-01-01, 2026-01-10, Business, calendar)` equals `calendar`'s open days in that range.
  - `"a business daily rule needs a calendar covering its range"`: `Reject("no_business_day", ...)` for last 2026-06-30 with `calendar`; `Reject("daily_definition", ...)` for `Business` with `null`.
  - `"daily bounds are explicit and at most 100 years"`: `Reject("daily_definition", ...)` for last before first and for a span of 36,526 days; undefined `DailyDays` value rejected.
- [ ] **Step 2: Run** `~/.openetrm-tools/dotnet-10.0.402/dotnet run --project tests/OpenEtrm.Shared.Checks/OpenEtrm.Shared.Checks.csproj`. Expected: build fails, `DailyDates` undefined.
- [ ] **Step 3: Implement `DailyDates.Generate`.** Business days: every `businessDays` date in `[first,last]`; fail `no_business_day` when `first` or `last` is outside the calendar's known coverage.
- [ ] **Step 4: Run Step 2's command.** Expected: all `PASS`.
- [ ] **Step 5: Commit** `git commit -s -m "Generate daily calendar and business dates"`

### Task 2: Daily schedule engine

**Files:**
- Create: `db/migrations/0212_daily_schedules_and_date_lists.sql` (schedule half; Task 3 adds the date-list half to this file)
- Modify: `src/OpenEtrm.Api/DateWorkspace.cs` (`MonthlyDraft`, `Compile`, `Choices`)
- Modify: `src/OpenEtrm.Api/PublishedSchedules.cs` (`Compile` engine switch, `Validate`)
- Modify: `src/OpenEtrm.Api/ScheduleStore.cs:71` (engine selection)
- Test: `tests/OpenEtrm.Api.Checks/DateWorkspaceChecks.cs`, `tests/OpenEtrm.Api.Checks/SchedulePublicationChecks.cs`, `tools/check_contract_dates.py`

**Interfaces:**
- Consumes: `DailyDates.Generate` (Task 1).
- Produces: `public sealed record DailyRule(string? Days,string? FirstDate,string? LastDate)`; `MonthlyDraft` gains trailing optional `DailyRule? Daily=null` (JSON ignored when null). Engine `"date_schedule_v4"` when `Daily` is set. A v4 schedule resolves through the unchanged `PublishedScheduleResolver`.

- [ ] **Step 1: Write failing managed checks** in `DateWorkspaceChecks`:
  - `"a calendar daily schedule resolves t0 plus n days on or after the context"`: a published v4 version (`days:"calendar"`, 2026-01-01..2030-12-31, `on_or_after`) resolved at context 2026-11-03: `oe.days` → 2026-11-03, `oe.days[1461]` → 2030-11-03 (2026-11-03 + 1461 days, across 29 February 2028).
  - `"a business-day offset on a calendar daily schedule uses its bound calendar"`: with a bound calendar where 2026-11-04 is open, `oe.days + 1bd` at 2026-11-03 → 2026-11-04; at Friday 2026-11-06 → Monday 2026-11-09.
  - `"a business-day offset without a bound calendar is refused"`: `Reject("calendar_reference", ...)`.
  - `"a business daily schedule starts at the first open day on or after the context"`: `days:"business"`, context Saturday 2026-11-07 → `oe.bdays` 2026-11-09.
  - `"daily schedules only use next-date selection"`: saving v4 with `context_month` or `absolute_index` → `Reject("schedule_definition", ...)`; a draft with both `Daily` and `Dates`, or `Daily` and monthly fields → `schedule_definition`.
  - `"resolving past a daily schedule's last date fails"`: `oe.days[2000]` near `lastDate` → `date_index`.
- [ ] **Step 2: Run** `~/.openetrm-tools/dotnet-10.0.402/dotnet run --project tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj`. Expected: compile failure on `DailyRule`.
- [ ] **Step 3: Implement.** `DateWorkspace.Compile` handles `Daily` (exclusive with `Dates` and monthly fields); `PublishedSchedules.Compile` maps `"date_schedule_v4"`; `Validate` requires `on_or_after` for v4; `ScheduleStore` picks v4 when `Daily` is set, binding a calendar only if supplied; `Choices` adds `days` choices `calendar` ("Every calendar day") and `business` ("Every business day of the calendar").
- [ ] **Step 4: Migration.** In `0212`, extend `schedule_engine_calendar` with `date_schedule_v4`: `definition->'daily'` is an object, `definition->'dates'` null, calendar columns all set when `daily->>'days'='business'`, all set or all null for `calendar`; add a check that `engine_version='date_schedule_v4'` implies `selection='on_or_after'`. Extend `engine_version` checks wherever 0007/0160 restrict them.
- [ ] **Step 5: Run Step 2's command.** Expected: all `PASS`.
- [ ] **Step 6: Integration check.** In `tools/check_contract_dates.py`, publish `oe.days` (calendar, bound to the fixture calendar) through `date-schedules/save` and `publish`, then assert `date-schedules/resolve` of `oe.days[1]` at two contexts one day apart differ by one day, and that a viewer cannot save (403).
- [ ] **Step 7: Run** `LC_ALL=en_US.UTF-8 python3 tools/run_preview.py --contract-dates-check --pg-bin /opt/homebrew/opt/postgresql@16/bin --dotnet ~/.openetrm-tools/dotnet-10.0.402/dotnet`. Expected: exits 0.
- [ ] **Step 8: Commit** `git commit -s -m "Publish generated daily schedules"`

### Task 3: Date-list mappings

**Files:**
- Modify: `db/migrations/0212_daily_schedules_and_date_lists.sql` (date-list half)
- Modify: `src/OpenEtrm.Api/ContractDateStore.cs`, `src/OpenEtrm.Api/PublishedSchedules.cs` (request records), `src/OpenEtrm.Api/Program.cs` (one route)
- Test: `tests/OpenEtrm.Api.Checks/SchedulePublicationChecks.cs`, `tools/check_contract_dates.py`

**Interfaces:**
- Produces:
  - `public sealed record DateListRowDraft(string? Label,string? Date)`.
  - `ContractDateSaveRequest` and `PublishedContractRequest` gain `DateListRowDraft[]? Dates=null`; exactly one of `Contracts`/`Dates` is non-null, else `contract_dates`.
  - `public sealed record DateListEntry(int Position,string Label,DateOnly Date,DateReceipt Reference)`; `public sealed record BoundDateList(string Code,string Name,ScheduleContext Context,ImmutableArray<DateListEntry> Dates,Guid? DefinitionId=null,int? Version=null)`.
  - `ContractDateStore.CompileDateList(PublishedContractRequest body,IEnumerable<ScheduleVersion> versions,ScheduleContext context,ImmutableArray<ContractDateBinding>? pinned=null) -> BoundDateList`.
  - `ContractDateStore.ResolveDateList(NpgsqlConnection,ContractDateResolveRequest,ScheduleContext? selectedContext,CancellationToken) -> Task<BoundDateList>`; the existing `Resolve` refuses a `date_list_v1` version with `contract_dates`, and `ResolveDateList` refuses `contract_dates_v1`.
  - Route `POST /api/date-schedules/date-lists/resolve` (`ReadMarketData`), same shape as `/date-schedules/contracts/resolve`.
  - Bindings use role `"date"`.

- [ ] **Step 1: Write failing managed checks** in `SchedulePublicationChecks` against `CompileDateList` with in-memory schedule versions:
  - `"a date list resolves each label against the context"`: rows `1D: oe.days + 1bd`, `4Y: oe.days[1461]` at 2026-11-03 → positions 1 and 2, dates 2026-11-04 and 2030-11-03, receipts naming `oe.days`.
  - `"date-list dates must strictly increase"`: rows `A: oe.days[2]`, `B: oe.days[1]` → `Reject("date_list_order", ...)` with both labels in the message.
  - `"date-list labels are unique and at most 120 rows"`: duplicate label and 121 rows → `contract_dates`.
  - `"a literal date row is retained as a literal"`: row `Fixed: 2027-01-15` → receipt kind literal.
  - `"a request names contracts or dates, not both"` → `contract_dates`.
- [ ] **Step 2: Run** the Api checks command from Task 2. Expected: compile failure.
- [ ] **Step 3: Implement** `CompileDateList`, the engine branch in `Save` (engine `date_list_v1`, one `"date"` field per row, null cutoff) and in `Publish`/`Read`, and `ResolveDateList`.
- [ ] **Step 4: Migration** (date-list half of 0212): `contract_date_versions.engine_version IN ('contract_dates_v1','date_list_v1')`; `contract_date_contracts.cutoff_utc` nullable; `contract_date_fields.role` adds `'date'`; a deferred constraint trigger requiring, per version, `contract_dates_v1` rows to have a cutoff and exactly the three contract roles, and `date_list_v1` rows to have no cutoff and exactly one `date` role; replace `security.publish_contract_dates` so its completeness test follows the engine. Read the latest definitions of these functions before replacing them.
- [ ] **Step 5: Run Step 2's command.** Expected: all `PASS`.
- [ ] **Step 6: Integration check** in `tools/check_contract_dates.py`: save and publish a date list over `oe.days`; a retry of the save with the same request id returns the same receipt and no new version; resolve at two closes and assert the dates roll; resolving it through `/date-schedules/contracts/resolve` fails with `contract_dates`; a knowledge context captured before its publication cannot see it.
- [ ] **Step 7: Run** Task 2 Step 7's command. Expected: exits 0.
- [ ] **Step 8: Commit** `git commit -s -m "Add date-list mappings to the date library"`

### Task 4: Settings editors for daily schedules and date lists

**Files:**
- Modify: `web/src/date-editor.ts` (schedule rule choice and mapping type choice), `web/src/schedule-workspace.ts` only if tab copy changes, `web/src/app.css`
- Test: `tests/browser/contract-dates.mjs`

**Interfaces:**
- Consumes: `DateWorkspace.Choices.days`, the save/publish routes, `DailyRule`, `DateListRowDraft`, `/date-schedules/date-lists/resolve`.

- [ ] **Step 1: Write failing browser checks** in `contract-dates.mjs`:
  - `"a daily schedule is saved and published from Settings"`: choose rule **Daily**, days **Every calendar day**, first/last dates, publish; the schedule list shows it with selection "Next date".
  - `"daily schedules offer only next-date selection"`: the selection control has one enabled option, `on_or_after`.
  - `"a date-list mapping is authored with a label and one date per row"`: choose mapping type **Date list**; the row editor has Label and Date columns only; add `1D`=`oe.days + 1bd` and `4Y`=`oe.days[1461]`; preview shows two resolved dates at the selected context; save and publish.
  - `"a date-list order error names both rows"`: swapped rows show the `date_list_order` message.
- [ ] **Step 2: Run** `LC_ALL=en_US.UTF-8 python3 tools/run_preview.py --contract-dates-check --browser-check --browser "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --pg-bin /opt/homebrew/opt/postgresql@16/bin --dotnet ~/.openetrm-tools/dotnet-10.0.402/dotnet`. Expected: browser check fails on the missing Daily rule.
- [ ] **Step 3: Implement** the editors. One field per row, stacked sections; error copy for `date_list_order` added beside the existing `contract_dates` copy in `web/src/shortcut-view.ts`.
- [ ] **Step 4: Run** `npm run build --prefix web && npm test --prefix web`, then Step 2's command. Expected: both pass.
- [ ] **Step 5: Commit** `git commit -s -m "Author daily schedules and date lists in Settings"`

### Task 5: Interest-rate curve kind in the curve preview

**Files:**
- Modify: `src/OpenEtrm.Api/CurveDefinitions.cs`, `src/OpenEtrm.Api/Program.cs:885-896`
- Test: `tests/OpenEtrm.Api.Checks/CurveDefinitionChecks.cs`

**Interfaces:**
- Consumes: `BoundDateList`, `ContractDateStore.ResolveDateList` (Task 3).
- Produces:
  - Choice `new("contractKind","interest_rate","Interest rate curve",true)`.
  - `CurveDefinitionDraft` gains trailing `string? InterestRate=null`. For `interest_rate`, `Contracts` names the date-list mapping (`MappingId`, `MappingVersion`) and `Count` must equal the list's length; commodity, location, quote, profile, futures product, settlement target, price rule, mark role, missing price and interpolation must be null.
  - `public sealed record CurveRateTerms(string Currency,string DayCount,string Compounding,string Implementation)`; `CurveReferenceTerms` gains `ImmutableDictionary<string,CurveRateTerms> InterestRates` from `interest_rate_definitions`.
  - `public sealed record RateMaturityPreview(int Position,string Label,DateOnly Maturity,int Days,DateReceipt Reference)`; `public sealed record RateCurveDefinitionPreview(string Code,string Name,string InterestRate,CurveRateTerms Terms,string MarkSource,string Mapping,ScheduleContext Context,DateOnly ValuationDate,ImmutableArray<RateMaturityPreview> Maturities)`.
  - `CurveDefinitions.PreviewRates(CurveDefinitionDraft draft,CurveReferenceTerms terms,BoundDateList mapping,DateOnly valuationDate) -> RateCurveDefinitionPreview`.
  - The preview route dispatches on `ContractKind`; for rates it passes the selected close's valuation UTC date and fails `rate_context` if it differs from the context's effective date.

- [ ] **Step 1: Write failing checks** in `CurveDefinitionChecks` with a synthetic `test_interest` specification (`usd`, `actual_365_fixed`, `simple`, the rate engine's implementation id) and a `BoundDateList` of `1D`→2026-11-04, `4Y`→2030-11-03 at 2026-11-03:
  - `"a rate curve previews each label's maturity and day count"`: positions 1, 2; `Days` 1 and 1461; `Terms.Currency=="usd"`.
  - `"a rate maturity on the valuation date is refused by label"`: list with `0D`→2026-11-03 → `Reject("rate_maturity", ...)`, message contains `0D`.
  - `"a rate curve refuses contract-only terms"`: setting `Commodity`, `Quote`, `SettlementTarget` or `Interpolation` → `curve_definition`.
  - `"a rate curve needs a supported specification and a known source"`: unknown code → `curve_reference`; a `compounding:"continuous"` specification → `unsupported_rate_convention`.
  - `"a rate curve carries every row of its date list"`: `Count` 1 for a two-row list → `curve_contract_count`.
  - `"a contract curve refuses an interest-rate specification"`: forward draft with `InterestRate` set → `curve_definition`.
- [ ] **Step 2: Run** the Api checks command. Expected: compile failure.
- [ ] **Step 3: Implement** `PreviewRates`, the kind branch in `Validate`, the terms read, and the route dispatch.
- [ ] **Step 4: Run Step 2's command.** Expected: all `PASS`.
- [ ] **Step 5: Commit** `git commit -s -m "Preview interest-rate curve definitions from date lists"`

### Task 6: Durable curve definitions

**Files:**
- Create: `db/migrations/0213_curve_definitions.sql`, `src/OpenEtrm.Api/CurveDefinitionStore.cs`
- Modify: `src/OpenEtrm.Api/Program.cs` (routes), `src/OpenEtrm.Api/ContractDateStore.cs` (expose `Save`/`Publish` for use inside an open transaction if not already)
- Test: `tools/check_curve_definitions.py`, `tests/test_migration_identity.py` expectations if it enumerates migrations

**Interfaces:**
- Consumes: `CurveDefinitions.Preview`, `PreviewRates`, `ContractDateStore.Save/Publish/Resolve/ResolveDateList`.
- Produces:
  - Tables `market_data.curve_definitions(catalog_id,id,code)`, `market_data.curve_definition_versions` (version, generation, state, name, kind, `reference_catalog_id`/`reference_revision_id`, term columns per kind with kind-shape checks, `mapping_definition_id`/`mapping_version` FK to `contract_date_versions`, `contract_count`, `validation_context_id`, publication columns as in `contract_date_versions`), `market_data.curve_definition_requests`, `market_data.curve_definition_knowledge_receipts`. Reference codes use composite FKs into the pinned reference revision as `market_data.curves` and `market_data.rate_curves` do. Guards, RLS and grants mirror 0054; `security.publish_curve_definition(catalog,definition,version,generation)` and `security.curve_definition_visible(...)`.
  - `public sealed record CurveMappingEdit(Guid? Id,int? Version,int? Generation,string? ExpectedState,string Code,string Name,ContractFieldDraft[]? Contracts,DateListRowDraft[]? Dates)`.
  - `public sealed record CurveDefinitionSaveRequest(Guid RequestId,CurveDefinitionDraft Definition,Guid? SnapshotId,CurveMappingEdit? Mapping=null,Guid? Id=null,int? Version=null,int? Generation=null,string? ExpectedState=null)`.
  - `public sealed record CurveDefinitionPublishRequest(Guid RequestId,Guid Id,int Version,int Generation)`.
  - `public sealed record CurveDefinitionVersion(Guid Id,int Version,int Generation,string State,CurveDefinitionDraft Definition,int? PublicationOrder,DateTimeOffset? PublishedAt)`.
  - `CurveDefinitionStore.Read/Save/Publish`; routes `GET /api/curve-definitions` (`ReadMarketData`; drafts only to principals with write or seal), `POST /api/curve-definitions/save` (`WriteMarketDefinitions`), `POST /api/curve-definitions/publish` (`SealMarketDefinitions`).
  - Save with `Mapping`: saves the mapping draft through `ContractDateStore.Save` with request id `UUIDv5(curve request id, "mapping")`, then pins the curve draft to that draft version, in one transaction. Publish: if the pinned mapping version is a draft, publishes it with request id `UUIDv5(curve request id, "mapping")` first; any failure rolls back both.

- [ ] **Step 1: Write failing integration checks** in `tools/check_curve_definitions.py`:
  - `"an editor saves a rate curve draft and an approver publishes it"`: save `usd_zero` over the published date list; publish; `GET /curve-definitions` shows version 1 published.
  - `"a retried save returns the first receipt"`: same request id and payload → identical receipt, one version; same id with a changed payload → refusal.
  - `"a stale generation is refused"`.
  - `"saving inline rows creates a mapping draft the curve pins"`: save with `Mapping` adding a `10Y` row; the mapping now has a draft and the curve draft pins it; the published curve version 1 still pins the earlier mapping version.
  - `"publishing the curve publishes its mapping draft first"`: after publish both are published; a second curve pinned to the earlier mapping version is unchanged.
  - `"a failed curve publish leaves the mapping unpublished"`: make the curve invalid at publish (`0D` row) → refusal; the mapping draft is still a draft.
  - `"a concurrent Settings edit makes the inline save conflict"`: change the mapping draft through `/date-schedules/contracts/save`, then save the curve with the old generation → `contract_date_conflict`; neither draft changed.
  - `"a viewer cannot save or publish, and sees no drafts"`.
  - `"a knowledge view captured before publication cannot see the version"`.
  - `"a contract curve round-trips through save and publish"` using the existing D318 fixture mapping.
- [ ] **Step 2: Run** Task 2 Step 7's command. Expected: fails on the missing routes.
- [ ] **Step 3: Write the migration** and **implement `CurveDefinitionStore`** and routes.
- [ ] **Step 4: Run Step 2's command and the Api checks command.** Expected: both pass.
- [ ] **Step 5: Commit** `git commit -s -m "Save, publish and edit curve definitions"`

### Task 7: Curve definitions view

**Files:**
- Modify: `web/src/curve-definition-editor.ts`, `web/src/index.html:228` (button label "Curve definitions"), `web/src/app.css`
- Test: `tests/browser/curve-definitions.mjs`

**Interfaces:**
- Consumes: Task 5 preview, Task 6 routes, `/date-schedules/date-lists/resolve`.

- [ ] **Step 1: Write failing browser checks** in `curve-definitions.mjs` (keep the existing D318 checks passing):
  - `"Curve definitions lists saved definitions and opens one"`.
  - `"choosing Interest rate curve shows only rate terms"`: commodity, location, quote, profile, product, target, rule, role, missing price and interpolation fields are hidden; specification, source and date list shown; currency shown read-only from the specification.
  - `"tenors are edited in place and previewed at the header close"`: add a `10Y` row, preview shows three maturities; change the header close and preview again; the dates roll by the business-day difference.
  - `"save then publish publishes the curve and its tenors"`: as approver; the list shows the new version published.
  - `"a newer published mapping version is offered to the curve's draft"`: publish a new version of the date list from Settings; opening the curve shows an offer to switch, and accepting it saves a draft pinned to the newer version.
  - `"rendered curves have no edit control"`: the market board and a curve's page contain no input, button or link that saves a definition. (The design's "View definition" link from a rendered curve needs a curve-to-definition association, which project 3's admission creates; record it there.)
  - `"the form stays inside the market content"` at phone width.
- [ ] **Step 2: Run** Task 4 Step 2's command. Expected: fails on the missing list.
- [ ] **Step 3: Implement** the view: list, editor with kind switching, editable rows table with Label and date expression(s), Preview, Save, Publish; no client-side pricing or date arithmetic.
- [ ] **Step 4: Run** `npm run build --prefix web && npm test --prefix web`, then Step 2's command. Expected: pass.
- [ ] **Step 5: Commit** `git commit -s -m "Add the Curve definitions view"`

### Task 8: Documentation, decisions and full verification

**Files:**
- Modify: `docs/curve-definitions.md`, `docs/schedules-and-contract-sequences.md`, `docs/implementation.md`, `docs/market-data.md` (rate maturities), `docs/decisions.md` (new D356 daily schedules and date lists; D357 saved curve definitions and the interest-rate kind; D318's remaining work), `docs/plan.md` (bootstrapped curves as unfunded future work; projects 3 and 4)
- Modify: `docs/superpowers/specs/2026-11-06-rolling-date-lists-and-curve-editor-design.md` status line

- [ ] **Step 1: Update the documents** to state what is implemented and what remains (admission, pricing from definitions, demo cleanup).
- [ ] **Step 2: Run** `LC_ALL=en_US.UTF-8 python3 tools/check_all.py` with the toolchain paths it requires. Expected: all checks pass; report any failure verbatim.
- [ ] **Step 3: Independent adversarial review** of the whole branch, covering temporal behaviour, knowledge views, idempotency and the atomic mapping-plus-curve publish. Resolve findings.
- [ ] **Step 4: Inspect** `git diff master...` for secrets, local paths and non-synthetic data.
- [ ] **Step 5: Commit** `git commit -s -m "Document rolling date lists and saved curve definitions"`
