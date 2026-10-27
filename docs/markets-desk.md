# Markets desk

Status: implemented on the markets desk branch, with the follow-ups listed
under [Remaining work](#remaining-work). The decisions it introduces are D231 to
D235 in [decisions.md](decisions.md). Its capability is recorded in
[implementation.md](implementation.md) and verified by the browser and backend
checks named under Verification; the page benchmark covers all three pages.

## Why

The Blotter became a desk ([blotter-desk.md](blotter-desk.md)): summary tiles,
charts, grouped rows and a side panel beside them. The Markets pages still use
the older layout:

- **Curves & prices** opens on an empty "Select a curve" card. Its summary strip
  counts contracts and picks the front contract in the browser from the order
  of the evaluated points. A contract's price history opens as a card below the
  table.
- **Custom curves** stacks a long definition form, two accordions and a
  three-column workbench. Saving, publishing and the effective timeline sit in
  different places, and leaving the page with unsaved edits does not ask.
- **Delivery prices** hides every filter inside a Context popover, chooses the
  opening source and day in the browser, and shows a bare table with no chart.

This design gives the three pages the desk's structure and visual system, adds
a price-over-time chart with analytics overlays, and moves every derived figure
the pages show to the backend.

## Goals

1. Each page opens on something useful: a market board, the selected custom
   curve, or the newest delivery day. Every opening choice is stated on screen.
2. Summary tiles on each page, fed only by backend fields.
3. Prices over time are drawn with KLineChart (`klinecharts`), with analytics
   overlays the server computes and drawing tools the user controls.
4. Detail opens in the shared side panel instead of cards below tables or
   accordions.
5. One visual system with the Blotter: tokens, tiles, commodity identity,
   sparklines, chart theme.

## Non-goals

- New pricing, curve or collection behavior.
- Hourly / sub-hourly prices (the interval-curves switch) and Interval data.
  They keep their layout and gain only the shared tokens.
- **Positions on this curve** ([blotter-desk.md](blotter-desk.md#curves-connection)).
  It needs a trading read keyed by curve and stays in the plan.
- Assistant coverage of Delivery prices (D151 stands).
- Bollinger bands or any overlay that needs a square root. They wait for an
  explicit irrational-number policy.

## Anti-goals

The redesign fails, even if it renders, if any of these happen:

- The browser computes a count, front contract, statistic or overlay value, or
  chooses a default source, date or close.
- A missing price is drawn as zero, interpolated, or bridged by a line or
  overlay.
- An overlay value appears without its definition and window on screen.
- Existing safety behavior is lost: generation and scope guards, retained
  custom-curve requests with retry, optimistic versions, read-only historical
  contexts, live-correction refresh and the assistant page reports.

## Constraints

- AGENTS.md: TypeScript is presentation only; no silent defaults; a dependency
  needs a named consumer; TDD; adversarial review for numerical and temporal
  changes.
- D162 and D219: closes are never substituted. Delivery prices keeps its own
  cutoff and market clock and does not join the As of dock.
- D188: live corrections refresh Curves & prices and Custom curves through the
  existing refresh path, including any open panel.
- D190 and D191: assistant reports state exactly what the page shows.
- The page benchmark keeps each page under one second from navigation to usable
  on the standard fixture.

## Shared pieces

### Time chart (KLineChart)

`klinecharts` 10.0.3 (Apache-2.0, no runtime dependencies) draws prices over
time in a shared module, `time-chart.ts`. Consumers: a contract's price history
on Curves & prices, and interval prices on Delivery prices. Forward curves and
custom-curve previews are cross-sections by delivery period, not time, and stay
on the shared ECharts component, which preserves gaps between periods.

- **Data.** Each bar is one backend point. Its value is the exact decimal string,
  turned into a number only for pixels, as the existing charts do. A point
  without a price is passed as a gap. The chart draws no line across it.
  A priced point with no priced neighbour gets a dot, since a line joins only
  neighbours. Hovering shows the exact backend string for the instant, for the
  price and every overlay series, never a rounded number.
- **Theme.** Colors, fonts and grid come from the CSS tokens; a dark scheme follows when the app defines one (it defines none yet; see Remaining work).
- **Overlays.** The overlay menu lists the backend's overlay catalog. Choosing an
  overlay asks for its parameters (the window, with no prefilled value), sends
  them with the read, and draws the values the backend returns as a registered
  indicator whose `calc` only looks values up by instant. The legend names the
  definition and parameters, for example "Simple moving average · 5".
- **Drawing tools.** Horizontal price line, trend line, ray and text note, from
  the library. Drawings are presentation state for the open chart. They are not
  saved, and the toolbar says so.
- **Values as text.** The latest value, and every overlay's latest value, show
  beside the chart. The table beside the chart remains the accessible record.

### Server-computed overlays

A shared C# module (`OpenEtrm.Shared/Analytics`) computes overlays from an
ordered series of `(instant, exact value or missing)`. Definitions, versioned:

| Code | Parameters | Value at point *i* |
| --- | --- | --- |
| `simple_moving_average_v1` | window *n* (2–250) | mean of points *i−n+1…i* |
| `exponential_moving_average_v1` | window *n* (2–250) | α·x*i* + (1−α)·EMA*i−1*, α = 2/(n+1), where EMA*i−1* is the previously reported (rounded) value; seeded by the rounded simple mean of the first *n* points |
| `rolling_range_v1` | window *n* (2–250) | highest and lowest of points *i−n+1…i* |

- Arithmetic is exact signed rational (the module's private ratio, rounded with
  `IntegerRounding`), rounded half to even at 18
  decimal places under `exact_rational_half_even_18_v1`; the response names the
  policy and the definition version.
- A window that contains a missing point has no value, with reason
  `window_incomplete`. An EMA restarts its seed after a missing point.
- The input series holds at most 35,136 points (one year of half-hour
  intervals); more is an `overlay_input` refusal, as are unordered instants and
  values that are not plain ASCII decimals.
- The history read states the series it is charted from as `chart`: either
  `{points:[{at,value|null,unavailableReason}]}`, one point per instant, or
  `{conflict:{at,message}}`. Under `as_known_at_cutoff_v1` (D237) the points
  hold one sample per instant, resolved to the latest revision recorded at or
  before the read's knowledge cutoff. Under `exact_snapshot_pins_v1` several
  samples may share an instant: samples that agree on value and availability
  are one point, and samples that differ make the chart a conflict naming the
  first such instant. No frame is ever chosen and the returned points are
  unchanged either way; the browser draws only `chart`, and shows a conflict's
  message with the values table instead of a chart.
- Overlays run over the chart's points, so the chart and the overlays show the
  same series, aligned by instant. Over a conflicting series the read returns
  its points and the conflict with no `overlays` (status 200); the requests are
  still checked, so a bad parameter or input that is not the conflict remains
  an `overlay_parameter` or `overlay_input` refusal. The panel then says that
  overlays cannot be drawn over a series with different prices at one instant.
- The delivery period read (`power-imports/period`) instead gives overlays one
  point per expected interval of its operating days, each the interval's price
  as known at the read's cutoff. An unpublished interval is a missing point, so
  windows that touch it have no value (`window_incomplete`) rather than
  bridging the gap.
- Parameters are required. A missing, out-of-range or unknown parameter is a
  structured `overlay_parameter` refusal, never a default.
- `GET analytics/overlays` returns the catalog (code, label, parameters with
  bounds, value series names). Reads that accept overlays take
  `overlays:[{code,parameters}]` (at most 4) and return
  `overlays:[{code,parameters,policy,series:{name:[{at,value|null,reason}]}}]`.

### Side panel and tiles

Both pages reuse `side-panel.ts` (docked on wide screens, overlay drawer
otherwise or while the assistant is open, one question at a time, focus
restored) and the desk tile styles. The tile helpers become shared exports
instead of Blotter-private ones.

## Curves & prices

```
┌ library ┐┌ Gas futures · USD/MMBtu ─────────────────── [Price sources] [How it's priced] ┐
│ Gas     ││ ┌ Contracts ┐┌ Front contract ┐┌ Front price ┐┌ Settlement ┐                     │
│ ● Gas f…││ ┌ forward curve (ECharts) ─────────────────────────────┐┌ side panel: Jan 2021 ┐│
│ ● Gas fw││ │                                                      ││ History (KLineChart)  ││
│ Power   ││ └──────────────────────────────────────────────────────┘│ + overlays, drawing   ││
│ ● Hub p…││ contracts table (rows open the panel)                   │ How it's priced       ││
└─────────┘└─────────────────────────────────────────────────────────└───────────────────────┘
```

**Market board.** With no curve selected, the board replaces the welcome card:
one card per visible curve, grouped as the library groups them. Each card shows
the curve name, market type, price unit, front contract and price, contract
count and a sparkline, from one batched read at the selected close. Cards for
curves the close cannot price say why ("No remaining contracts"). Choosing a
card selects the curve, as the library does.

**Library.** Unchanged in behavior, with the commodity dot and the curve's
sparkline beside each name. `.curve-button` keeps its markup.

**Selected curve.**

- Tiles: Contracts, Front contract, Front price, Settlement (rates: Maturities,
  First maturity, First zero rate, Day count), from the evaluate response's new
  `summary`. The browser no longer counts points or reads `points[0]`.
- The forward curve chart and contracts table stay. Choosing a row, or pressing
  Enter on it, opens the contract in the side panel.
- **Price sources** and **How it's priced** stay as tabs, as today.

**Contract panel.** Header: contract, delivery period, price and unit. Tabs:

1. **History**: the contract's closes over time in the time chart, with
   overlays and drawing tools, and the values table under it. It replaces the
   history card.
2. **How it's priced**: the per-contract pricing graph for that contract.

A live correction refreshes the open panel with the page. Changing the close,
context or curve closes the panel.

## Custom curves

```
┌ Custom curves · NG prompt ● Draft v3 ─────── [Preview] [Save draft] [Publish] [⋯] ┐
│ library ┐┌ ops ┐┌ calculation graph (Visual | Formula) ────────┐┌ side panel ──────┐ │
│ NG prom…││ Src ││                                              ││ Selected         │ │
│ NG balm…││ Sel ││   cards and wires                            ││ operation        │ │
│ + New   ││ Avg ││                                              ││ · Details        │ │
│         ││     │└──────────────────────────────────────────────┘│ · Effective vers.│ │
│         ││     │┌ Points ┐┌ First period ┐┌ Last period ┐┌ Unit ┐│ · Combine curves │ │
│         ││     │ preview chart (ECharts) and table               └──────────────────┘ │
```

- **Header bar.** The curve's name, its state chip (Draft, Published, Unsaved
  changes) and version, and the actions on one line. Code, name and template
  move into the panel's **Details** mode.
- **Library.** Saved curves as a list with state chips, replacing the select.
  **New curve** starts from a blank graph or a template. The catalog read
  already returns the state and version shown.
- **Graph.** The workbench keeps its palette, board, formula mode and
  behavior. It becomes the main area, styled with tokens instead of literal
  colors.
- **Graph objects.** Each card shows its input ports and an output dot. A
  weighted contract mapping lists each explicit contract connection as its own
  row, labelled "Fixed contract · <catalog label>" with its weight exactly as
  typed, and an arrow runs from the source to that row. Arrows are focusable
  objects: right-click, the context-menu key or Shift+F10 opens Change
  source…, Change target port…, Edit weight (contract connections) and Remove
  arrow. A card's menu offers Add arrow from here…, Add contract connection…
  (mappings) and Remove operation. Add arrow, Change target port and dragging
  from an output dot enter connect mode: only the inputs whose catalog type
  accepts the output are offered, a click or Enter connects and Escape
  cancels. Every edit is an ordinary definition edit: it marks the curve
  unsaved and clears the preview; Formula then shows the edited graph (or none
  while the backend cannot format it). The backend validates every edit on
  preview or save. Read-only accounts get only View. A row whose source
  input was removed shows "Source disconnected" and opens the same menu from
  the row; changing a row's source clears its contract for an explicit new
  choice. Auto-layout and Fit use the cards' rendered heights, so cards never
  overlap and expanded sources stay inside the board.
- **Expandable sources.** A source card whose catalog entry lists contracts
  expands to show them as the catalog names them; picking one into a mapping
  (Connect to…, or dragging it onto the mapping) adds a fixed contract
  connection with no weight or output position chosen, focuses its weight, and
  Preview and Save wait until every weight is entered. Published custom-curve
  sources list no contracts in the catalog, so they do not expand. Expanded
  state is page state and is not saved.
- **Rolling selections.** A Select contracts card states its relative
  positions as its definition does. The preview does not yet state which
  contracts each selection resolved, so the card shows no resolved contracts.
- **Side panel modes:** Selected operation (today's inspector), Details,
  Effective versions (today's accordion) and Combine curves (today's
  accordion). Selecting a card opens Selected operation.
- **Preview.** Tiles from a new backend `summary` on the preview (point count,
  first and last delivery period, output unit, unavailable points), then the
  chart and table as today. Dependencies, observations and trace stay under the
  table.
- **Leaving with unsaved edits asks first**, through the same `leaving()` path
  and `beforeunload` guard as the Blotter, and only on moves that discard the
  edits: another page, a changed historical context or return to working data,
  an account switch or sign-out, another example, and, in the page, opening
  another library entry, New curve, a template, or Reload library on a selected
  curve. A change of close keeps the graph (D162), so it asks only when an
  effective-version timeline is unpublished, and Discard drops that draft alone.
  Saving preferences and following live keep the edits and never ask. Keeping
  returns focus to the control that asked. A request whose outcome is
  unconfirmed is not an unsaved edit: it is retained across a move, so it
  neither asks nor registers the unload guard. A move that fails after the
  edits were discarded reads the library again.
  Preview and Save also wait, naming what is missing, while a contract
  connection lacks its contract, output position or weight.
- The retained-request banner, optimistic versions, read-only explanations and
  assistant proposals are unchanged.

## Delivery prices

```
┌ Delivery prices ─────────────────────────────────────────────────────────────┐
│ Power ▾ PJM ▾ Day-ahead ▾ Hub A ▾ Source ▾  Nov 2 → Nov 2  Known at …  [Show] │
│ Newest delivery day · Synthetic PJM DA hourly                                 │
│ ┌ Intervals ┐┌ Low · High ┐┌ Average ┐┌ Collection ┐                           │
│ [Prices] [Collection history]                                                │
│ ┌ interval prices (KLineChart) + overlays, drawing tools ────────────────────┐ │
│ └──────────────────────────────────────────────────────────────────────────┘ │
│ interval table                                                               │
└──────────────────────────────────────────────────────────────────────────────┘
```

- **Filter bar.** The commodity, operator, market, location and source selects,
  the delivery date range and the knowledge cutoff move out of the Context
  popover into a bar at the top of the page. The cascade and its refusal of
  conflicting labels stay. A changed filter clears the choices below it and
  chooses none of them; the person's range is read when they choose Show
  prices.
- **Opening selection from the backend.** The catalog returns
  `opening:{sourceId,fromDate,throughDate,policy:'newest_available_delivery_day_v1'}`
  (the source with the newest available day, ties broken by source name then
  identity). The page opens on it and shows a **Newest delivery day** badge,
  which becomes **Selected range** once the user changes a filter, a date or
  the cutoff. The browser's `latestDeliverySource` is removed. A catalog
  without an opening shows that there are no delivery prices yet.
- **Tiles** from a new `summary` on the period read under the same rational
  policy as overlays: interval count with missing intervals, low and high with
  their instants, time-weighted average over the published intervals, and the
  latest knowledge instant used. A collection tile shows the jobs and the
  newest attempt's outcome from a new catalog `collection` summary.
- **Prices tab:** the time chart of interval prices with overlays, then the
  interval table. The period read states the series it is charted from as
  `chart:{points:[{at,value|null,unavailableReason}]}`, one point per expected
  interval of its operating days (the same points the overlays run over); an
  interval not published by the cutoff has no price and the reason
  `not_published_at_cutoff`. The browser draws only that series and refuses a
  read without it. **Collection history tab:** today's attempts table.
- The page keeps its own cutoff and generation guard, outside the As of dock.

## Backend changes

1. `market/curve-shapes`: each curve adds `contractCount` and
   `front:{contractId,deliveryFrom,deliveryTo,price}|null`. The board reads
   it in batches of 64.
2. `evaluate`: adds `summary`, the same fields, plus the rate-curve fields
   (maturity count, first maturity, first zero rate, day count).
3. The overlay module and catalog; `history` and `power-imports/period` accept
   overlays.
4. `power-imports`: adds `opening` and `collection`; `power-imports/period`
   adds `summary`.
5. `custom-curves/preview`: adds `summary`.
6. The preview launcher seeds synthetic delivery prices (two sources, a week of
   hourly and quarter-hourly prices) so the page can be seen and checked
   locally.

**Front contract** is defined once, in the backend: the first contract in
delivery order that the selected close prices. A curve with no such contract
has `front: null` and an availability reason.

## Decisions

D231 (KLineChart for time charts), D232 (server-computed overlays), D233
(backend curve summaries and the front contract), D234 (the delivery opening
selection and period statistics) and D235 (the unsaved-edit question) are
recorded in [decisions.md](decisions.md). D238 (idempotent writes) and D237
(history revision policy) govern the history reads the charts draw.

## Remaining work

Listed in [plan.md](plan.md): Positions on this curve, Bollinger bands,
per-selection resolved contracts in custom-curve previews, structured refusals
for an empty contract or null output position, a reported refusal of a saved
weighted draft previewed by id and version, rates curves on the market board and
the dark color scheme.

## Verification

- Backend numerical checks for each overlay definition, written failing first
  and independent of the implementation: hand-computed series, missing points,
  incomplete windows, parameter refusals, policy rounding.
- Backend checks for curve summaries (futures, forwards, rates, no remaining
  contracts), the delivery opening policy (ties) and the period summary
  (missing intervals, time weighting across a DST change).
- Web unit tests for the time-chart data mapping (gaps, overlay lookup by
  instant, no computation) and the new page models.
- Browser checks in the owned preview: the board, selecting a curve, the
  contract panel with an overlay, a custom curve with the leave question, and
  Delivery prices with seeded data, at 1440 and 390 pixels.
- The page benchmark keeps each page under one second.
- Adversarial review for the overlay module, the summaries and the delivery
  opening policy before publication.

## Replaced, not kept

The welcome card, the history card, the browser's count and front-contract
derivation, the custom-curve definition card and accordions, the delivery
Context popover and the browser's opening source choice are removed once their
replacements cover them. Tests are updated to the new structure, keeping the
ids that the behavior still needs.

## Left to the builder

- Breakpoints, spacing, motion and the exact overlay menu layout.
- Module structure for the page models and the panel modes.
- Batch scheduling for the market board within the page budget.
