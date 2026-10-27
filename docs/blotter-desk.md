# Blotter desk

Status: proposed design, not implementation evidence. Decisions it introduces
are listed under [Decisions to record](#decisions-to-record) and are added to
[decisions.md](decisions.md) when implementation starts.

## Why

Trade entry, curves and risk are separate destinations today. The application
opens on Curves & prices with no market date. The Blotter lists individual
trades with the deciding values in its rightmost columns, and its summary omits
swap present value. Risk is a form below the table that takes one scenario
target from a long list. New futures open an inline form below the table, new
swaps open a modal, and amending opens a modal that covers the Blotter. Nothing
links a position to its curve.

The Blotter becomes a trading desk: the first page a trader sees, showing
current positions at the newest close, a summary of the book, and every
follow-on action (inspect a curve, book, amend, run risk) beside the positions
instead of on another page.

## Goals

1. The Blotter is the default page for accounts that can read trades.
2. It opens on the newest available close (rules below).
3. A summary shows swap PV and futures execution P&L as separate totals per
   currency, split by book, with the position mix and the risk status.
4. Positions show direction and size, price against mark, value and the
   market's curve shape at a glance.
5. One side panel hosts the selected position (curve, terms, risk, history,
   amend), new trades (futures and swaps, with price before booking) and risk
   report setup.
6. Risk results come back inline in the desk.
7. The sidebar is regrouped with Trading first; page headings match their group.
8. One shared visual system (tokens, signed values, commodity identity, charts)
   applies to every page.

## Non-goals

- The public landing page. It is a separate follow-up that reuses this visual
  system.
- New pricing models, settlement or variation-margin processing, or new risk
  mathematics.
- Changes to roles, permissions or authorization.
- Redesigning other pages beyond the shared visual system and regrouping.
  PPA bookings stays its own page under Trading.

## Anti-goals

The redesign fails, even if it renders, if any of these happen:

- The browser computes totals, subtotals, deltas, position direction or
  commodity, or infers any of them from values or names.
- A different close, partial data or a missing value appears without a visible
  flag and reason.
- Present value and execution P&L, or different currencies, are added together.
- A position's changes under separate one-at-a-time shocks are summed into one
  number, or exposure is allocated across factors.
- Existing safety behavior is lost: retained trade requests with retry and
  reconcile, unsaved-edit guards, optimistic versions, live-context pinning,
  stale-response rejection and the assistant's page report.

## Constraints

- TypeScript is presentation only (AGENTS.md). Totals, pivots, risk results,
  valuation status, position direction and commodity come from the backend.
  Exact decimal strings remain the values; numbers are for pixels only.
- No silent defaults (AGENTS.md). Every default in this design is shown on
  screen, and fallback behavior is recorded as a decision.
- D162: unavailable dates are disabled, no alternative close is chosen, and a
  displayed day with several closes or correction versions needs an explicit
  choice.
- D160 and D188: Blotter reads keep their scoped contracts. Live corrections stay
  an explicit, read-only selection.
- D190 and D191: the assistant reports exactly what the page shows. D191 relies
  on trade dialogs being modal; this design changes that (D221 below).
- Portfolio risk accepts at most 16 bumps per request with holdings detail and
  4,096 with books detail. Each factor appears once, with an explicit positive
  size (`src/OpenEtrm.Api/PortfolioRiskPreview.cs`).
- Business pages target under one second from navigation to usable
  (architecture §8). The page benchmark measures it.
- Public repository rules: synthetic examples only; no customer or company names.
- TDD for every behavior. Adversarial review for the valuation-status change
  (temporal and numerical) and the assistant page-report change.

## Navigation

Sidebar groups, in order. Visibility follows today's permissions.

| Group | Pages |
| --- | --- |
| Trading | Blotter, PPA bookings |
| Markets | Curves & prices, Custom curves, Delivery prices, Interval series, Interval curves |
| Operations | Jobs & infrastructure |
| Settings | Contract definitions, Schedules, Power TOUs, Reference data, Entities & counterparties |
| Admin | Users & access, AI |

Each page's eyebrow is its group name. The Blotter opens first when the account
can read trades; otherwise Curves & prices. The preview's example-dataset switch
moves into the workspace card at the foot of the sidebar.

## Opening on the newest close

Decided by the owner (D219), with the latest instant of master's browsing
defaults (D217):

1. Pages that read As of start from the authorized availability calendar
   (`market/dates`) for the current scope. Its newest date (`maxDate`) holds its
   latest close.
2. If one close has the latest instant, it is selected and the As-of control
   shows a **Latest close** badge. If several closes share that instant, such as
   a close and its correction versions, the calendar opens on that date and
   asks. Nothing is selected silently (D162).
3. Choosing another close replaces the badge with **Selected close**. A reload,
   or a return to working data, starts from the newest close again.
   (Builder's call: whether a session remembers a chosen close, as long as the
   badge says which applies.)
4. Knowledge mode stays working data at current knowledge, as today.
5. If a newer close becomes available during a session, the control shows
   **Newer close available** with a one-click switch. It never switches by
   itself. Availability is checked again on navigation and when the window
   regains focus; nothing polls (designer's call).

## Positions the close cannot value

Decided by the owner (D220). Today one unvaluable position fails the whole
Blotter read. For example, at every close after an expired constituent the read
returns `422 source_expired` from `CurveModel.Connected.cs`. Instead:

- Trading workspace, Blotter and pivot reads return each position with a
  valuation status. A position that cannot be valued at the selected close for
  a position-specific reason is returned with that reason's structured code,
  instead of failing the read. Examples are an expired constituent without a
  supported transition (`source_expired`) or a missing observation
  (`missing_observation`).
- Such a position stays in the table with a **Not valued** chip and the reason
  in plain words (for example "Contract expired · awaiting settlement"). Its
  value cells are blank, never zero.
- Every valuation total excludes it and says so: PV and P&L tiles, group
  subtotals, pivots, charts and exports. For example: "Excludes 1 position that
  can't be valued at this close". Its booked quantity still counts in position
  counts and the long/short chart, with the flag, because quantity is a booked
  fact rather than a valuation (designer's call).
- Failures that are not about one position still fail the read: authorization,
  context, scope and catalog consistency.
- The backend owns which codes are position-specific. The list is reviewed
  adversarially with the change.
- A risk run excludes positions that are not valued at its base close and
  lists them in the run bar.

## Desk layout

```
┌ sidebar ┐┌ header: Blotter · profile ───────────────────────────────────────────────┐
│ Trading ││ toolbar: book scope · view · Risk report · Scenario · New trade          │
│ Markets ││ [run bar, after a risk run]                                              │
│ ...     ││ ┌ Swap PV ┐┌ Futures P&L ┐┌ Positions ┐┌ Risk ┐                         │
│         ││ ┌ chart: Swap PV by market ┐┌ chart: long/short by market ┐┌ side     ┐ │
│         ││ Positions · grouped by book  Group by [Book]  [search]  24 of 24 rows    │
│         ││ ┌ positions, grouped, Values | Risk lens ───────────────────┐│ panel    │ │
│         ││ floating: As of [Latest close ▾] · Context ▴ · Assistant     │          │ │
└─────────┘└───────────────────────────────────────────────────────────────┘└──────────┘
```

The As of control, its badge, the knowledge in use and live corrections float
with the shared Context panel at the foot of the page (D217), beside the
Assistant launcher; the header keeps the page title and the profile.

**Summary tiles**, one row per currency inside a tile, never added across
currencies:

1. Swap PV: total and a small bar per book.
2. Futures execution P&L: total and a small bar per book.
3. Open positions: count, with the mix by commodity and by instrument type.
4. Risk: **Not run at this close** with a run button. After a run it shows the
   largest move, the shock count and small bars per position.

Not-valued positions are excluded and noted as above. Tile totals and their
per-book splits come from money-totals pivots (`trading_blotter_money_totals_v1`,
D223) grouped by instrument type and by book, which add money of one currency
across quantity units. The position mix comes from the backend's commodity and
instrument-type pivot (`trading_blotter_totals_v1`). All of them are summaries of
the Blotter's own read.

**Charts** (shared chart component and theme):

- Swap PV by market: one card; per currency a captioned panel of diverging
  horizontal bars, with gains right and losses left, signed labels, and the
  commodity marked beside each market.
- Long and short by market: one card; per quantity unit a captioned panel of
  diverging bars in the commodity color. A market whose swaps have no single
  quantity (one of them is scheduled by period, D214), or no single direction, gets
  no bar, and the panel names each such market and why under its caption
  ("Not drawn: …"). After a risk run this chart becomes "Change if each contract
  moves up".
- Each card has one heading and a caption per currency or unit, bars about 20px
  tall, and the values of its bars as text beside the canvas.

**Positions table**, grouped by book by default. Group headers show backend
subtotals of all of their positions, and how many of those positions the row
filters leave showing ("4 of 6 positions"); they open and close from the
keyboard as well as the arrow. The heading row above the table carries the
heading, **Group by**, the search and the row count on one line.

| Column | Content |
| --- | --- |
| Trade | Instrument-type pill and trade reference |
| Market | Commodity dot, market name, delivery period |
| Position | Diverging size bar on a fixed track, signed quantity, and under it the unit with lots (futures) or pay/receive fixed from the trade's sides (swaps, backend field); **By period**, with no bar, for a scheduled swap (D214) |
| Price | Execution or fixed price, labeled with its price unit (for example "Executed · USD/MMBtu (IT)"); **By period** for a scheduled swap whose fixed periods differ in price |
| Mark | Curve price at the selected close, labeled with its price unit; **Several marks** when a valued swap has no single curve price, **By period** for a scheduled swap and **Settlement lifecycle** for a swap valued by its lifecycle (D215) |
| Value | PV or execution P&L with its measure tag; blank with a chip when not valued |
| Curve | Sparkline of the market's forward curve with the delivery period marked; gaps preserved |

The Position column shows the backend's signed exposure. A swap whose sides do
not reduce to one fixed and one floating side shows **Multi-side** in the table
and each side's direction in the panel (designer's call). Each note in place of
a figure says what it means in its tooltip and in the panel.

Clicking a row, or pressing Enter on it, opens it in the side panel. **Group
by** offers Book (default), Market, Instrument type and None (a flat list); it
sits in the heading row above the table, with the row search, the **Column
filters** toggle (its panel opens under that row) and the row count. Columns,
pivot, scope, views and export stay in the toolbar, and pivot views keep their
configurable analytics chart, restyled. Grouping by market refuses a market whose
futures, or whose swaps, are held in more than one quantity specification or
currency, because its header would state that measure twice. A market whose
futures and swaps differ in unit or currency is not refused: its header states
PV and P&L once each.

Saved views move to a new format that records the desk grouping. Following
project practice, there is no migration. Views saved in the earlier format show
as unavailable and must be saved again (designer's call).

Live corrections (D188) refresh the tiles, charts, sparklines and rows through
the existing refresh path, and clear a risk run with a note, as today.

## Side panel

Docked on the right beside the positions when the main area is wide enough. It
becomes an overlay drawer below that width and while the assistant panel is
open; the breakpoint is the builder's call. A swap's settlement and P&L view
needs more room than a docked panel has: off a phone it opens the panel wide,
over the desk, which keeps its layout beneath. Modes:

1. **Position.** Header with instrument type, trade reference, revision and
   book. Title with market, delivery, side and quantity. The value, plus mark
   minus price. Tabs:
   - **Curve**: the market's forward curve at the selected close, the delivery
     period marked, and the trade's price as a reference line. **Open curve**
     opens Curves & prices with that curve and contract selected. A scheduled
     swap whose periods project from several curves has no one curve: it has no
     curve shape and no Open curve, and says why.
   - **Terms**: today's editor content (`trade-editor`, `swap-editor`) as a read
     view. **Amend** makes it editable in place, with the amendment reason and
     effective time as today. A scheduled swap (D214) amends in its own ticket,
     and a swap's settlement and P&L view (D215), with D216's net settlement
     within the trade and its contractual P&L bridge, opens from Terms, each in
     the panel in place of the terms.
   - **Risk**: this position's results from the current run (base, up, down,
     delta, gamma, and which factor moved by how much), or a run for this
     position alone.
   - **History**: revisions and events, as today.
2. **New trade.** A Future | Swap switch, today's booking fields, the existing
   draft valuation shown as "Price before booking" with the market's curve, and
   **Book**.
3. **Risk report.** Presets: **Shock every held contract**, **Parallel shift**,
   and **Custom scenario** (today's scenario lab with nested stresses).
   **Run now**, **Queue as job**, and retained job history.

Switching modes or positions with unsaved edits asks first, reusing
`trackEdits` and `unsaved()`. Retained trade requests show retry and reconcile
in the panel, as today.

## Risk report inline

Decided by the owner:

- A **run bar** above the summary states what ran: preset, number of shocked
  factors, sizes and units, close, knowledge mode and time. It has **Edit
  shocks**, **Export** and **Clear**. A queued run shows its job state there.
- The positions table gets a **Values | Risk** lens. Risk shows each position's
  shock (size and unit), up change, down change (with small diverging bars) and
  delta with its unit. Gamma is in the panel.
- A position moved by more than one shocked factor shows **n shocks**, and the
  panel lists each factor's result. The table never sums across factors.
- Changing the close, context or book scope clears the run with a note, as the
  current generation checks do.
- **Shock every held contract** selects the catalog's `contract` scenario
  targets for held contracts. Sizes are entered per price unit (for example
  USD/MMBtu, USD/MWh); there are no built-in sizes. The last sizes used are
  prefilled visibly.
- More than 16 shocked factors exceed one holdings-detail request. The run is
  split into requests of at most 16 factors, all pinned to the same retained
  context. A queued run submits one job per part. The run bar shows the parts
  as one run. Each factor's result is independent, so no arithmetic combines
  parts (designer's call; revisit with measurements).
- The Risk lens applies to trade-row views. Pivot views show a note pointing
  to the panel and export for factor results (designer's call).
- Run is disabled with today's explanation when the account cannot execute
  risk. Accounts need the Risk Analyst role, alone or alongside Trader.
- After a run, a third lens, **Ladder**, shows the run's per-factor totals by
  book and measure in place of the trade rows (D230,
  [delta ladder](delta-ladder.md)).

## Curves connection

Row sparklines and the panel's curve chart use backend curve reads at the same
close and context. If one read per held market misses the page target, the
backend adds a batched read. Curves & prices shows **Positions on this curve**
with a link back to the Blotter filtered to that market, for accounts that can
read trades.

## Visual system

- Keep the existing palette (deep green sidebar, green accent) and refine
  spacing, radius and shadows. Tokens live in `web/src/app.css`.
- Commodity identity: gas `#eb6834`, power `#2a78d6`. This pair passes the
  palette validator (normal-vision ΔE 33.6, CVD ΔE 24.7 on white). Gas is the
  commodity codes `gas` and `test_gas`, power `electricity` and `test_power`:
  the explicit energy codes of the imported reference catalog and those of the
  synthetic one. Any other commodity takes the next palette color in code
  order, and marks always name the commodity too.
- Polarity: gains `#1f8a5b`, losses `#d6453d`. Their CVD separation (7.8) needs
  secondary encoding, so signs and bar direction are always shown.
- Tables use tabular figures; summary values are larger and semibold.
- Full charts use the shared ECharts component with one shared theme.
  Sparklines and small bars are inline SVG from a shared helper that preserves
  gaps and shows exact strings in tooltips.

## Backend changes

1. Per-position valuation status in trading workspace, Blotter and pivot reads;
   totals exclude not-valued positions and report them (D220).
2. A commodity dimension in the managed Blotter pivot, or an explicit commodity
   on each row.
3. Swap direction on Blotter rows, from the trade's sides.
4. A batched curve read for held markets, only if needed for the page target.
5. Risk runs exclude and list positions that are not valued at the base close.

## Decisions to record

| ID | Decision |
| --- | --- |
| D219 | The Blotter is the default trading page and opens on the newest close under the rules above |
| D220 | Trading reads report positions that cannot be valued at the selected close, with structured reasons; totals exclude them and say so |
| D221 | Trade entry and amendment move from modal dialogs into the Blotter's side panel. Refines D191: the page report names the panel's position and mode, and the risk lens state |
| D222 | Risk reports come back inline: run bar, Values/Risk lens, risk tile and panel detail. Shock sizes are explicit per price unit |

## Verification

- Web unit tests (`npm test --prefix web`): latest-close selection from a dates
  catalog (single close, several closes, newer close), lens rendering, panel
  modes, not-valued rows and total notes, and sparkline gaps.
- Backend checks for per-position valuation status, written failing first with
  an expired constituent.
- Browser checks in the owned preview: open to the newest close, select a
  position, amend, book with a draft price, run **Shock every held contract**,
  read the lens, clear the run.
- The page benchmark keeps the Blotter under one second on the standard fixture.
- Adversarial review for D220 and D221 before publication.

## Replaced, not kept

There are no compatibility paths. The modal trade editors, the inline futures
booking card, the risk card below the table and the flat Blotter layout are
replaced by the desk, the side panel and the lens. Their code is removed once
the desk covers their behavior.

## Left to the builder

- Module structure (a desk module beside or within `futures-view.ts`) and CSS
  organization.
- Breakpoints, motion and exact spacing.
- Whether a session remembers a chosen close.
- The reviewed list of position-specific failure codes.
- Whether to replace the 16-factor split with a larger backend limit, once
  measured.
