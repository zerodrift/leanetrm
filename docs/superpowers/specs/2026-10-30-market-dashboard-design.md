# Market dashboard

Status: historical design. Phase 1 is implemented in mainline under D265–D267;
see the [current dashboard record](../../market-dashboard.md) for shipped scope,
verification and remaining work. Later phases below remain proposals.

## Why

A trader decides what to hedge or close from a view of market direction. Each
morning they form that view in a fixed order:

1. weather,
2. inventory levels,
3. their positions along the curve (the delta ladder),
4. anything they might have missed, or that the agent raises.

During the day, events change the view: a policy announcement, geopolitical
news, a hurricane forming, an ISO unplanned outage, a polar-vortex forecast.
Today only prices and the delta ladder exist in OpenETRM, on separate pages.
There is nowhere to see the rest, and nothing watches for it.

The market dashboard is one page, separate from the Blotter, that shows those
inputs at a glance as graphics, says exactly how current each one is, and lets
the agent watch for events and post what it finds.

## Goals

1. A new **Dashboard** page of widgets the user adds according to what they trade.
   It is commodity-neutral: gas and power today, crude and LNG as they arrive.
2. Two arrangements of the same widgets, chosen by the user:
   - **Board**: a free grid; the user places and sizes widgets.
   - **Morning**: ordered sections (Weather & events → Inventories → Prices &
     positions → Might have missed); the user picks which widgets sit in each.
   Switching arrangement never removes a widget.
3. Widgets are graphics wherever the data allows: charts, sparklines, bars
   against a range, not bare numbers.
4. Every widget states how current its content is, on screen, at all times.
5. External data (weather, inventories, news and events, outages, intraday
   prices) arrives through declared **provider connections** that an
   installation configures from a setup screen and then leaves running. The
   public build ships synthetic providers only.
6. Prices show the settlement close and the latest intraday price side by side,
   with the change between them.
7. The user can ask the agent, in plain words, to watch something. The agent
   keeps the watch as a record, checks it on a schedule, and posts alert and
   chart cards to the dashboard.

## Non-goals

- Real provider connectors (ISOs, pipeline bulletin boards, EIA reports,
  price-reporting agencies, weather services). They are planned separately on
  top of the provider connection this design defines.
- An agent web browser, scraping, VPN egress or searching under a person's own
  account. An installation may add a web-research provider under its own terms;
  it is not part of this design or the public repository.
- Alert delivery outside the dashboard (phone, email, chat).
- User-defined trigger rules ("tell me if X moves 5%").
- Acting on an alert. The agent never books, hedges, amends or closes anything
  as a result of a watch.
- Changes to the Blotter (`futures-view.ts`, `data-grid.ts`,
  `table-workspace.ts`, `desk-*.ts`), which is being redesigned separately.
- New pricing, curve or risk behavior.

## Anti-goals

The dashboard fails, even if it renders, if:

- **It is slow.** The page must be usable within the one-second page budget the
  markets pages hold on the standard fixture. One slow or failing provider never
  holds up the others.
- **The trader has to second-guess whether information is up to date.** A value
  shown without its observation time, or a stale value that looks fresh, is a
  defect. A widget whose data is late says so; it is never silently left showing
  the last value as if current.
- A missing reading is shown as zero, carried forward, interpolated or replaced
  by another source.
- A past context shows anything learned after its knowledge cutoff.
- An agent card states a number the backend did not supply, or the agent's
  judgment is presented as a fact without the readings it rests on.
- Alerts become clutter: the same event raised repeatedly as new cards.

## Constraints

- AGENTS.md throughout. TypeScript is presentation only: "vs normal", "vs
  five-year range", "change since close", age and lateness, ordering and
  section membership are computed or stated by the backend.
- No silent defaults. A widget without a declared series, a comparison without a
  declared baseline, a provider without a declared cadence: each is a
  structured, visible refusal, not a guess.
- Bitemporal context (architecture §7, [context.md](../../context.md)): every
  dashboard read carries the selected effective time and knowledge cutoff.
- Idempotent writes (D238): the same observation recorded twice is one row; a
  different value for the same business key is a new revision with its own
  knowledge time.
- Closes follow D162/D219: never substituted. Several closes at the latest
  instant ask the user rather than pick one.
- External datasets need verified redistribution rights. The repository ships
  only synthetic observations, clearly labeled as synthetic.
- Model- or provider-specific types never enter domain contracts (agent and data
  providers alike).
- UI and agent use the same authorized operations.
- Adversarial review is required: this touches temporal behavior, async
  execution and authorization.

## The page

A top-level **Dashboard** navigation entry. Its header shows the context
selector the other pages use and the arrangement switch (Board / Morning).

- **Layout record (per user).** Which widgets the user has added, each widget's
  configuration, its Board position and size, and its Morning section and order.
  Stored server-side so it follows the user between browsers, written through an
  idempotent operation with an expected version. *My call:* one dashboard per
  user in this version; named or shared dashboards are later.
- **Adding a widget**: pick a kind, then configure it against what exists (a
  curve, a series from a configured provider, a location). The picker lists only
  series the backend reports as available under the current context.
- **Morning sections are fixed** in this version (the four above, in the
  trader's stated order). Each widget kind has a declared home section, and the
  user can move a widget to another section.

### Freshness on every widget

Each widget's footer shows, from backend fields only:

- the **observation time** of what it shows (for a forecast: issued time and the
  valid period),
- the **source** (provider connection name; "Synthetic" for synthetic data),
- a **state**: *current*, *late* (no new reading within the provider's declared
  cadence), *failed* (last fetch failed, with the reason), *not yet available*,
  or *historical* (a past context; no live updates).

Lateness is judged against a cadence each provider series **declares**. A
series without a declared cadence shows "cadence not declared"; it is never
assumed fresh.

Closes and risk runs are not provider series. Their widgets state *latest*,
*newer available* (a newer close or run exists than the one shown) or
*historical*, using the existing close-badge semantics (D219). Lateness of an
expected close against its business calendar is later work, planned with the
provider cadence in phase 2.

In the current context, widgets update live as new observations are committed
(using the existing live event stream). In a past context the page is
read-only and static.

## Widgets and the data each reads

| Widget | Reads | Graphic |
|---|---|---|
| **Latest levels** | For each chosen curve/contract: the settlement close (existing close catalog and curve shapes) and the latest intraday observation at or before the knowledge cutoff; the backend states the change and its unit. | Price, change arrow, intraday sparkline since the close. |
| **Price chart** | An existing curve or contract's price history (existing time-chart reads). | The existing KLineChart time chart. |
| **Weather vs normal** | Synthetic weather series per declared location: forecast or observed temperature (or another declared variable) and a declared normal series. Backend returns the difference per day. | Bars or lines per location over the forecast days. |
| **Weather events** | Event records of declared kinds (cold outbreak, tropical system) with location or region, issued time, valid period and a provider-stated severity. | List with timeline strip. |
| **Inventory** | An inventory series (gas storage region, crude hub, LNG storage) and a declared comparison range (e.g. five-year min/max/mean series). Backend returns level, change since the previous report and position against the range. | Level against a shaded range band, weekly bars of change. |
| **Outages** | Outage event records: market operator, zone or unit, capacity and unit, planned/unplanned, start and expected end. | Capacity-out bar by zone; list. |
| **News & events** | News items: headline, provider, published time, tags for commodity and region. Headlines only; no article bodies are stored. | List, newest first. |
| **Positions by month** | The delta ladder (the existing D230 read) of the latest completed run of the saved **risk run definition** the widget is configured with (see below). Two qualifying runs at the same latest valuation instant are a refusal that asks the user, as with closes. The run's valuation time is the widget's observation time. The widget never recomputes a run; it offers **Run now**, which queues one from the definition. | Ladder bars by delivery month. |
| **Agent alerts & charts** | Alerts and pinned chart cards for this user (below). | Alert list with evidence; the existing card renderers. |

Sources:

- Latest levels, Price chart and Positions read what OpenETRM already holds.
- Intraday prices are a new observation kind. They must come from the same
  live-price source the Blotter redesign introduces, not a second one; see
  Dependencies.
- Everything else comes from provider connections.

*My call:* no basemap or map tiles in this version. Map tiles are an external
dataset with their own rights; weather widgets are charts by location. A map
widget can follow once an installation supplies licensed tiles.

## Risk run definitions

Decided with the user after the first approval: a risk run today is a one-off job
that retains its exact shocked targets and sizes, not the preset that chose them,
so "the latest run of the same setup" cannot be identified from past runs. The
dashboard therefore adds saved definitions.

- A **risk run definition** belongs to a user and has immutable versions. A
  version holds: a name, the exact books (foreign keys), the preset (shock every
  held contract, or a parallel shift per held curve), and one shock size per
  price unit (exact currency and unit references). Revising creates a new
  version; old runs keep pointing at the version they used.
- **Run now** resolves the preset to targets in the backend, at a selected
  retained context, from the same held-target catalog the Blotter uses. A held
  price unit without a size in the definition is a structured refusal
  (`risk_definition_size`), never a default size. The backend splits the
  targets into parts as queued runs already are (D222) and records which run
  came from which definition version. A retried Run now with the same request
  identity returns its first outcome.
- **Latest completed run** of a definition, in a context, is the run whose every
  part completed, whose valuation time is at or before the effective time and
  which was known by the knowledge cutoff, with the greatest valuation time.
  A tie is a refusal that lists the tied runs.
- A newer completed run of the same definition than the one shown is stated on
  the widget ("newer run available"), as closes already do.
- The Blotter keeps its own risk report unchanged. Running a definition from
  the Blotter is later work for the Blotter redesign.

## Provider connections

A provider connection is a configured instance of a **provider kind**. Each kind
declares:

- the **series and event kinds** it can supply: variable, unit (from the unit
  catalog), location or region (typed locations from reference data), and for
  each series its expected cadence;
- the **settings** it needs (endpoint, account, region list), and which of them
  are secrets;
- its **fetch schedule**, run as a durable job by the existing scheduler.

Each fetch records **observations** (time series values) and **events**
(outages, weather events, news items). Every record carries:

- provider connection and series or event kind,
- observation time (and for forecasts, issued time plus valid period),
- knowledge time (the committed revision that recorded it),
- the provider's own identifier where it has one, as part of the business key.

Recording is idempotent per D238. A provider correction is a new revision; reads
resolve revisions by knowledge time.

### Setup screen

Settings gains a **Data providers** section where an administrator:

1. adds a connection by picking a provider kind,
2. fills in its declared settings (secrets entered through a dedicated operation,
   handled like model-provider keys in [ai-assistant.md §5.2](../../ai-assistant.md)),
3. runs a test fetch that reports what it found,
4. enables it; from then on it runs on its schedule,
5. sees each connection's last fetch, its outcome and late series.

*My call* on permissions:

- Configuring connections is its own grant.
- Reading a connection's data is a grant per connection, because each
  installation's redistribution rights differ by source. A widget on a
  connection the user cannot read shows a refusal, not an empty widget.
- The dashboard page needs no separate grant; each widget is checked under its
  own reads.
- The agent reads provider data through new read tools under the same
  per-connection grants as the user, and never with wider access.

### Synthetic providers shipped

The public build includes one synthetic provider kind per data type (weather,
weather events, inventory, outages, news, intraday prices). They generate
deterministic, clearly labeled synthetic observations for the synthetic
locations and curves in the preview, so every widget, freshness state (including
late and failed) and the agent flow can be exercised and tested.

## The agent on the dashboard

### Watches

- The user asks the agent, in the assistant panel, to watch something ("keep an
  eye on Gulf tropical systems for my LNG positions").
- The agent saves a **watch** record through an authorized operation:
  - the user's words,
  - the agent's structured reading of them: which series, event kinds, regions,
    curves or books it will check,
  - how often to check, stated by the user (the agent asks if it isn't; there
    is no default frequency),
  - who asked, when, and its state (active, paused, stopped).
- A watch runs under an agent grant. When that grant expires, or its provider
  or model changes, the watch **pauses** and shows why; the user resumes it
  under a new grant. Runs count against the existing agent cost and rate limits;
  a run refused by a limit is shown on the watch, not skipped silently.
- The user can list, edit, pause and stop watches on the dashboard. A watch
  belongs to the user who created it.
- Each active watch runs as a scheduled agent task under the user's current
  authority: no wider access than the user, re-checked every run.
- The watch keeps notes between runs (what it has already raised) in its own
  records, so the same event does not produce a new card every run.

### Charts and alerts

*My call:* the existing card contract ([ai-assistant.md §10.2](../../ai-assistant.md))
stays as it is (immutable, no agent-written text or numbers). The dashboard adds
one new record kind beside it, the **alert**, because the user asked for the
agent to raise messages and the card contract forbids agent prose.

- **Chart cards** are existing cards. The user puts one on the dashboard with an
  explicit **Add to dashboard** action in the assistant panel. A pinned card
  shows what it showed when created, with its context; it does not re-read.
- An **alert** is posted by a watch run (or by the agent in a conversation the
  user is having). It holds:
  - a short explanation, visibly marked as the agent's assessment,
  - evidence links: the exact observations, events and risk run it rests on,
    each shown by the app with source and time, never as numbers the agent
    wrote,
  - optionally existing chart cards it created in the same run.
- Alerts are retained. A later development of the same event (as the watch's
  notes identify it) is added to the existing alert as a new revision with its
  own knowledge time, instead of a new alert. The user sees the alert's history.
- The user can dismiss an alert; dismissal does not delete it.
- Alerts and pinned cards respect the context: a past context shows only alert
  revisions and pins known at its cutoff.

## Historical context

- The dashboard opens in the session's selected context, like other pages.
- In the current context: latest closes, latest intraday prices and newest
  observations as of now; live updates on.
- In a past context: closes, intraday prices, observations, events, risk runs
  and cards are resolved as known at the knowledge cutoff. The last intraday
  price known at that moment is shown, never a later one. No live updates; the
  page is read-only. Watches do not run against past contexts.
- *My call* on effective time: provider records are market facts, filtered by
  both coordinates. A widget shows observations whose observation time is at
  or before the effective time, forecasts and events issued at or before it,
  and closes for the effective date, all as known at the knowledge cutoff. The
  page is **historical** (read-only, static) when either coordinate is in the
  past.
- The layout record is a presentation preference, not business data. It is
  current-only and applies in any context.
- Access is always checked under current permissions.

## Dependencies

- **Live prices.** The Blotter redesign session is designing streaming prices.
  The intraday price observation kind and its live update path are shared with
  that work. *My call:* phase 3 starts only once that contract is agreed with
  the Blotter work, extending the existing market observation model rather than
  adding a parallel one. Phases 1, 2 and 4 do not wait for it; until then,
  Latest levels shows the close only and says intraday is not configured.
- **Scheduled agent tasks** (architecture §11.1) are planned but not built. Watches
  depend on them. Until they exist, the dashboard ships without watches; pinned
  chart cards and alerts the agent raises in a conversation still work.
- **Typed locations** with optional points already exist and identify weather,
  inventory and outage locations.

## Suggested delivery order

1. Page, layout record, Board/Morning arrangements, freshness footer; Latest
   levels (close only) and Price chart from existing reads; risk run
   definitions with Run now, and the Positions widget.
2. Provider connection model, observation and event storage, Data providers
   setup screen, synthetic providers; Weather, Inventory, Outages, News widgets.
3. Intraday prices on Latest levels (with the shared live-price source).
4. Agent cards on the dashboard; then watches once scheduled agent tasks exist.

## Left to the builder

- Table and operation names, migration layout and API shapes, consistent with
  existing conventions.
- Grid mechanics for Board (column count, snapping, drag handling).
- Which existing chart and tile components each widget reuses.
- The synthetic generators' shapes, provided they are deterministic and
  labeled synthetic.
- The exact page benchmark fixture extension, provided it includes several
  widgets of each kind and one late and one failed provider.

## Verification

- Behavior checks, written first, for: freshness states (current, late, failed,
  cadence not declared); idempotent observation recording and revision by
  knowledge time; past-context reads showing nothing learned after the cutoff,
  including intraday prices and cards; "vs normal" and "vs range" refusing
  without a declared baseline; layout switching preserving all widgets; watch
  authority re-checked per run; repeat events attaching to an existing card.
- Browser checks for both arrangements, every widget kind, and a past context.
- Page benchmark under one second with one provider slow and one failing.
- Adversarial review before publication (temporal, async, authorization).
