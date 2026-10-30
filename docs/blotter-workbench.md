# Blotter workbench

Status: proposed design, not implementation evidence. It builds on
[blotter-desk.md](blotter-desk.md) and replaces that document's desk layout
and position expansion. Decisions it introduces are listed under
[Decisions to record](#decisions-to-record) and are added to
[decisions.md](decisions.md) when implementation starts.

## Why

A trader opens the Blotter to scan current positions against their view of
market direction and to find what needs a hedge or a close-out. That is a
top-to-bottom working pass over a long list, so the one thing the page must
never do is lose the trader's place. Today it does, in four ways:

1. **First load shows two tables.** `load()` in `web/src/futures-view.ts`
   draws a provisional table of unvalued position facts (`renderFacts`: Value
   "Pending", "—" for missing market and delivery), then destroys it and draws
   the valued table when `trading/workspace` returns. Half-filled rows appear
   and then jump.
2. **Every control change rebuilds the grid.** `render()` calls
   `workspace.render`, which destroys the Tabulator grid and builds a new one
   (`web/src/table-workspace.ts`). Regrouping rows already read, choosing
   columns, or applying a view resets scroll, selection and focus.
3. **Opening a row shifts the view.** The position detail is inserted inside a
   virtual-rendered row (`detailRow` in `web/src/data-grid.ts`). Its height
   changes the renderer's estimates and the grid redraws around it; much of
   `data-grid.ts` exists to work around this.
4. **The page is a document, not a workspace.** Large tiles and charts sit
   above the table, the page scrolls as a whole, and the table has a fixed
   maximum height (560 px) however large the window is. A full-width
   "Close position view" button closes the detail.

The Blotter becomes a desktop-style workbench: panes that fill the window,
a grid that is built once and keeps its state, details that open beside the
list without moving it, and actions on a position from the position itself.

## Goals

1. The Blotter fills the window beside the navigation. The page never
   scrolls; only panes scroll. Resizing the window resizes the panes.
2. The positions grid is built once per Blotter visit and updated in place.
   Regrouping, sorting, filtering, column choice, a new valuation and a
   changed selection never lose the scroll position, the selected row or
   keyboard focus, unless the selected row no longer exists.
3. Position details open in a docked pane, not inside the grid. The grid's
   row heights never change because a detail opened or closed.
4. Charts are secondary: a collapsible pane, by default at the bottom.
5. Panes can be rearranged by dragging, resized and collapsed. The
   arrangement is part of a saved Blotter view.
6. The Blotter can pop out into its own window that stays tied to the main
   window.
7. First load shows progress instead of provisional rows.
8. A context menu on the selected row offers the actions a trader takes on a
   position, including a pre-filled close-out reviewed before booking.
9. Every Blotter action is one operation that the UI and the assistant both
   call (AGENTS.md: UI and agent entry points use the same operations).

## Non-goals

- Streaming valuation (positions arriving in batches). Designed separately;
  this design defines the client hook it fills (see [First load](#first-load)).
- The assistant driving the Blotter, suggesting hedges, drawing charts in the
  chat and running reports. A later project, built on goal 9.
- News and article retrieval for the assistant.
- The market dashboard (prices, latest levels, weather). A separate page.
- New pricing, valuation, risk mathematics or permissions.
- Other pages. Curve builder keeps its dock; this design generalizes the dock
  module it uses but does not change Curve builder's behavior.

## Anti-goals

The redesign fails, even if it renders, if any of these happen:

- A control change, a valuation arriving or a detail opening moves the rows
  the trader is looking at, or takes focus away from where they were.
- A row shows a partial value set, or a summary shows a total over a scope
  that is not completely valued, without a visible flag and reason.
- The browser computes a close-out or hedge quantity, side, price or any other
  trade term. Proposed trade terms come from the backend.
- Anything is booked without the trader reviewing and submitting the form.
- The pop-out and the main window show different contexts or views of the
  same Blotter without saying so.
- Existing safety behavior is lost: retained trade requests with retry and
  reconcile, unsaved-edit guards, optimistic view versions, live-context
  pinning, stale-response rejection and the assistant's page report
  (blotter-desk.md, Anti-goals).

## Constraints

- AGENTS.md applies in full: TypeScript is presentation only, no silent
  defaults, exact decimal strings are the values, synthetic data, TDD.
- The constraints of blotter-desk.md stay, including D162 close selection,
  D190/D191 page reporting and the page-performance target (architecture §8).
- Saved views: `BlotterView` in `web/src/blotter-layout.ts` (version 5) is
  saved to the account through `table-views/blotter`. Earlier versions are
  never migrated; they are listed by name and saved again (existing rule).
- Reuse before writing new code: the dock model and dock drawing
  (`curve-dock-model.ts`, `curve-dock.ts`), the context-menu module
  (`context-menu.ts`), the position panel (`position-panel.ts`) and the
  position pop-out (`position-window.ts`, `position.html`). Generalize them
  rather than copying; move shared code where both pages use it.
- No new dependency without a named consumer (AGENTS.md). Tabulator 6.6.1
  stays the grid unless the builder shows it cannot meet goal 2.
- Adversarial review is required for: the pop-out's tie to the main window
  (authorization and context pinning across windows), the close-out draft
  operation (numerical, trade terms), and the page-report change.

## Layout

The default arrangement, top to bottom:

| Pane | Contents | Default |
| --- | --- | --- |
| Toolbar | View, books and portfolios, row grouping, filter, columns, New trade, pop-out, As-of | Fixed, not a pane |
| Summary strip | One line: swap PV, futures execution P&L, positions, not valued, risk status, by currency | Open |
| Positions grid | The trade rows or pivot | Takes the remaining height |
| Details | The selected position | Collapsed until a row is opened |
| Charts | The desk charts (PV by market, position by delivery month) | Open, bottom |

- The summary strip replaces the large tiles. It shows the same backend
  figures with the same rules (separate currencies, swap PV and futures P&L
  never added, pending/unavailable flagged with reason). The tiles' per-book
  bars and the position mix by commodity and instrument type move into the
  Charts pane as charts of their own; the risk tile's largest move and shock
  count stay in the strip as the risk status, and **Run risk** moves to the
  toolbar. No backend read is dropped. (Design call.)
- The risk report (D221/D222) opens in the Details pane, as it opens in the
  side panel today; its results show in the grid under the Risk lens.
  (Design call.)
- The charts show the scope the grid shows (selected books, portfolios and
  filters), as the desk charts do today.
- At narrow widths (the existing `narrow` rule in `curve-dock.ts`) panes
  stack in one column and the saved arrangement is kept for when the window
  is wide again.

## Panes: arrange, resize, collapse

- Panes are the Summary strip, Positions grid, Details and Charts. Each has a
  header with its title, a drag grip and a collapse control (⌄ / ⌃).
- Dragging a pane header onto another pane's edge docks it on that side;
  onto its centre makes them tabs in one group. This is the Curve builder
  dock interaction, generalized: `curve-dock-model.ts` becomes a shared dock
  model used by both pages. The same moves are available from the pane
  header's menu (keyboard: context-menu key or Shift+F10).
- Dividers resize. A collapsed pane shows a one-line bar with its title and
  ⌃; its space goes to its neighbours.
- The Positions grid cannot be collapsed or closed.
- The arrangement (dock layout, split ratios, collapsed panes, active tabs)
  is saved in the named Blotter view. This bumps `BlotterView` to version 6;
  version 5 views follow the existing earlier-version rule. An unsaved
  arrangement persists for the browser session like other unsaved view
  edits. **Reset layout** restores the default arrangement.

## Positions grid

- One grid instance per Blotter visit. Data, grouping, sorting, filters,
  column visibility and order change through the grid's in-place API
  (for Tabulator: `replaceData`/`updateData`, `setGroupBy`, `setSort`,
  `setFilter`, column show/hide/move). Destroying the grid is reserved for
  leaving the Blotter, switching between trade rows and pivot, and an
  account or workspace change.
- Rows are identified by a stable key (trade and binding), not by source
  index, so the selection and scroll anchor survive new data. Revision is row
  data: an amendment or correction updates the row in place and the
  selection follows the trade, as D221 has the panel follow it. (Design
  call.)
- Place retention: on any update the grid keeps the selected row selected and
  keeps the first visible row at the same position in the viewport (scroll
  anchoring by row key). If the selected row is gone, the selection clears
  and a one-line notice says why.
- Group rows show the backend's subtotals and collapse and expand in place.
- Keyboard: ↑/↓ moves the selection; Enter opens details; the context-menu key
  or Shift+F10 opens the row menu; Escape collapses details and returns
  focus to the row.
- Selecting a row while details are open shows that position in details.
  Selection does not scroll the grid.
- Double-click keeps its current meaning (open the position in its own
  window).
- Lenses: switching Values and Risk changes columns and cell content in place
  and keeps place. The Ladder lens (D230) draws per-factor totals, not trade
  rows; like the trades/pivot switch it replaces the grid's rows, and
  returning to trade rows restores the scroll anchor and selection.
  (Design call.)

## Details pane

- Opens the selected position using the existing position panel
  (`position-panel.ts`) and its tabs: Curve, Terms, Risk, History. The Terms
  tab holds the sides table (pay/receive, fixed or floating basis, price,
  current price, quantity, value), which is what a trader reads first, so the
  pane opens on Terms. The last chosen tab is kept while moving between
  positions.
- This supersedes D253's amendment that a position's detail opens under its
  Blotter row (decided by the owner, 2026-10-30). Each side's evaluated
  schedule, which D253 placed under the row, shows in the Details pane with
  the sides table.
- Header: ◂ ▸ (previous and next position in grid order), the position's
  title, and ⌄ to collapse. "Close position view" is removed.
- New trade, amendment and close-out forms open in the details pane; the
  unsaved-edit guard applies before it shows another position or collapses.
- The inline row expansion (`position-expansion.ts`) and the grid's detail
  row support in `data-grid.ts` (`detailRow`, `resizeDetail`, the virtual
  buffer workarounds) are removed.

## First load

- The grid shows no rows until positions are valued. A thin progress bar
  under the toolbar and a status line show what is happening: "Valuing
  positions" while the read runs, and "Valuing positions · 74 of 120" once
  counts are available.
- The provisional facts table (`renderFacts`, `trading/positions` paging for
  display) is removed from the first-load path. Opening a position before the
  valuation finishes is not offered. (Builder's call: whether the facts read
  is still needed for the count; it must not draw rows.)
- Client hook for streaming: the grid accepts batches of complete, valued
  rows and appends them in backend order without moving the rows on screen.
  Until streaming exists, the whole valued set arrives as one batch. The
  summary strip shows totals only when the complete scope is valued;
  meanwhile it shows "Pending" with the reason, as today.
- A failed valuation keeps the toolbar usable and shows the reason and a
  Retry in the grid area. A refresh of an already loaded Blotter keeps the
  current rows on screen, marked stale, until the new values replace them in
  place.

## Row context menu

Right-click, the context-menu key or Shift+F10 on a row opens a menu for that
position (`context-menu.ts`). Items are shown only when the account may
perform them; otherwise they are shown disabled with the reason.

| Item | Effect |
| --- | --- |
| Close out… | Opens a new-trade form in details, pre-filled with the offsetting trade |
| Amend… | Opens the amendment form in details (existing) |
| Open in its own window | The existing position pop-out |
| Copy trade ID | Copies the trade reference |
| Ask the assistant about this position | Opens the assistant panel with this position as context |

Close-out:

- A new backend read operation returns draft trade terms for a full
  close-out of one position revision in the pinned context: same instrument
  and contract or delivery, opposite side, the position's open quantity, and
  as the price the position's current curve price (mark) in the pinned
  context (owner, 2026-10-30). The form labels the price with its source and
  close, for example "Curve price at 29 Oct 17:30 close", until the trader
  edits it. A position with no mark in that context gets a structured error,
  never another price.
- A partly settled swap closes its remaining periods only (owner,
  2026-10-30): the draft covers each unsettled period, opposite side, the
  period's open quantity and its current curve price; settled periods are
  left untouched. Which periods count as settled is the backend's settlement
  state in the pinned context and knowledge cutoff; a period whose state is
  ambiguous (for example fixed but not yet settled) is refused with the
  reason rather than included or dropped. A by-period or scheduled swap
  (D214) keeps its per-period quantities. The browser never derives these. Missing or ambiguous terms (for example a
  partly settled swap) return a structured error shown in the form, never a
  guessed draft.
- The draft opens in the existing booking or swap form for review. The trader
  may edit it; booking uses the existing booking operations, retained
  requests and idempotency. Nothing books from the menu.

## Pop-out Blotter

- A toolbar button opens the Blotter in its own browser window (a second
  page like `position.html`). The main window's Blotter shows "Blotter is in
  its own window" with **Bring back**, and resumes there when the pop-out
  closes.
- The pop-out follows the main window: account, workspace, market date or
  close, historical context and knowledge cutoff. A change in the main
  window is applied in the pop-out at once, whichever page the main window
  shows (the header's As-of selection, not a page's own read, is what it
  follows).
- While popped out, the Blotter and its view (books, portfolios, grouping,
  filters, columns, layout, saved view) live only in the pop-out; the main
  window shows the placeholder and no Blotter controls, so there is one
  editor of the view and no concurrent edits. Bring back hands the current
  view, including unsaved edits, to the main window. Narrow stacking is per
  window. (Design call.)
- The pop-out reads through the same API and authorization as the main
  window. It never receives data from the main window that it has not read
  itself under its own session; the main window only sends selections.
- When the main window closes, signs out or changes account, the pop-out
  shows why and stops showing values rather than leaving stale numbers.
- The assistant panel stays in the main window. Its page report (D190/D191)
  reports the pop-out's Blotter state, relayed, and "Ask the assistant about
  this position" in the pop-out focuses the main window's panel. A Navigate
  target to the Blotter brings the pop-out to the front. (Design call;
  part of the required page-report review.)
- Booking, amending and close-out work in the pop-out with the same
  operations and guards. A retained trade request is owned by one window at
  a time. (Builder's call: the cross-window channel, for example
  `BroadcastChannel` with an origin and session check.)

## Shared operations

Each Blotter action is a named operation with typed input, used by the UI
and later exposed to the assistant: select view, choose books/portfolios,
group rows, sort, filter, choose columns, select position, open/collapse
details, arrange/resize/collapse panes, reset layout, pop out/bring back,
draft close-out, open amendment. The assistant page report (D190/D191)
reports the selected position, open pane and view as today, plus whether the
Blotter is popped out. Exposing these operations as assistant tools is out of
scope here.

## Backend changes

1. Close-out draft read (above), with tests per instrument type: future,
   financial swap, heat rate, PPA (PPA may be refused as unsupported with a
   structured reason).
2. `BlotterView` version 6 validation on the server side, if the server
   validates saved layouts.
3. Nothing else; streaming valuation is its own design.

## Decisions to record

- The Blotter grid is built once and updated in place; destroying it is
  limited to the cases above.
- Position details are a docked pane; the inline row expansion is removed. Supersedes D253's
  "detail opens under its Blotter row" (owner, 2026-10-30).
- Pane arrangement is part of the saved Blotter view (version 6).
- The pop-out Blotter follows the main window's context and shares the view
  both ways; it reads under its own session.
- Close-out drafts are produced by the backend; the browser never derives
  trade terms.
- First load shows no unvalued rows.

## Verification

- Browser checks (the existing harness under `web/tests` plus a browser
  run): scroll position and selected row are unchanged after regrouping,
  sorting, filtering, column changes, a refreshed valuation, opening and
  collapsing details, and resizing the window.
- First load renders no row before values arrive and shows the progress bar.
- Panes: drag to each edge and centre, resize, collapse, save view, reload,
  reset layout; narrow-width stacking.
- Pop-out: context change in the main window reaches the pop-out; view change
  in either reaches both; main window closed or signed out stops the pop-out.
- Close-out draft: backend tests per instrument type and refusal reasons;
  UI test that the form opens pre-filled and nothing books without submit.
- Page performance stays within the architecture §8 target.

## Left to the builder

- Tabulator API choices and how scroll anchoring is implemented.
- Pane header visuals, icons and animation, within the shared visual system.
- The cross-window channel and its message format.
- Whether the facts read survives for the progress count.
- Default split ratios.

## Deferred

- **Hedge…** is not in the menu (owner, 2026-10-30). Close-out covers the full
  offset; hedging with a different instrument or quantity belongs with the
  assistant work that suggests hedges.
