# Markets desk

Status: proposed design, not implementation evidence. Decisions it introduces
are listed under [Decisions to record](#decisions-to-record) and are added to
[decisions.md](decisions.md) when implementation starts.

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
- **Theme.** Colors, fonts and grid come from the CSS tokens, in light and dark.
- **Overlays.** The overlay menu lists the backend's overlay catalog. Choosing an
  overlay asks for its parameters (the window, with no prefilled value), sends
  them with the read, and draws the values the backend returns as a registered
  indicator whose `calc` only looks values up by instant. The legend names the
  definition and parameters, for example "Simple moving average · 5 closes".
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
- A history read may hold several samples at one instant. Overlays take one
  sample per instant: identical samples count once, and different samples at one
  instant are an `overlay_input` refusal naming it; no frame is ever chosen. The
  returned points are unchanged, and the overlay series is aligned by instant.
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
- **Side panel modes:** Selected operation (today's inspector), Details,
  Effective versions (today's accordion) and Combine curves (today's
  accordion). Selecting a card opens Selected operation.
- **Preview.** Tiles from a new backend `summary` on the preview (point count,
  first and last delivery period, output unit, unavailable points), then the
  chart and table as today. Dependencies, observations and trace stay under the
  table.
- **Leaving with unsaved edits asks first**, through the same `leaving()` path
  and `beforeunload` guard as the Blotter. Today only the assistant waits. A
  historical-context change with unsaved edits also asks before discarding
  them.
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
  conflicting labels stay.
- **Opening selection from the backend.** The catalog returns
  `opening:{sourceId,fromDate,throughDate,policy:'newest_available_delivery_day_v1'}`
  (the source with the newest available day, ties broken by source name then
  identity). The page opens on it and shows a **Newest delivery day** badge,
  which becomes **Selected range** once the user changes a filter. The browser's
  `latestDeliverySource` is removed.
- **Tiles** from a new `summary` on the period read under the same rational
  policy as overlays: interval count with missing intervals, low and high with
  their instants, time-weighted average over the published intervals, and the
  latest knowledge instant used. A collection tile shows the jobs and the
  newest attempt's outcome from a new catalog `collection` summary.
- **Prices tab:** the time chart of interval prices with overlays, then the
  interval table. **Collection history tab:** today's attempts table.
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

## Decisions to record

| ID | Decision |
| --- | --- |
| D224 | Prices over time use KLineChart (`klinecharts` 10.0.3, Apache-2.0). Cross-sections by delivery period stay on ECharts |
| D225 | Chart overlays are server-computed from a versioned catalog under an exact rational policy, with required parameters and gaps for incomplete windows. Drawings are unsaved presentation state |
| D226 | Curve summaries (contract count, front contract and price, rate-curve fields) come from the backend; front is the first priced contract in delivery order |
| D227 | Delivery prices opens on a backend-chosen `newest_available_delivery_day_v1` selection, stated with a badge; its summary statistics are backend fields |
| D228 | Custom curves asks before discarding unsaved edits on navigation, unload or a context change |

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
