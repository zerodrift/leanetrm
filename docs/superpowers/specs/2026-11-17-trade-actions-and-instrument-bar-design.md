# Trade actions on a position, risk for one trade, and New trade without the board

Status: design. Nothing here is implemented until the decision record says so.

## Intent

Three things a trader asked for on 2026-11-17:

1. New trade shows a board of instruments and, once one is chosen, the same
   instruments again as a strip above the ticket. The list is shown twice and the
   first press only swaps one list for the other. New trade should show one list.
2. What a trader does with a booked trade (amend it, close it out, buy more, sell
   some) is behind a right-click on a Blotter row. It should be visible as buttons
   on the trade, with the events not yet supported shown as such.
3. Risk can be run for the books on screen but not for one trade.

## Goals

1. New trade has one instrument list, the grouped bar, whether or not an instrument
   is chosen. It reads as a designed control, not a row of loose buttons.
2. A position's details show its actions as buttons: Close out, Amend, increase,
   reduce and Risk, then Terminate and Novate as Coming soon.
3. Increase and reduce open a reviewed new-trade form the backend drafted, for a
   quantity the person states, as Close out does for the whole position.
4. Risk can be run for the one trade the details show.

## Non-goals

- Termination, novation, cancellation, partial exercise or any other new lifecycle
  event. They are drawn Coming soon and do nothing.
- Drafts for instruments Close out does not draft today (heat rate, PPA, options,
  physical products). Their buttons say the backend's reason.
- Increase or reduce of a scheduled swap. It holds a quantity per period; one stated
  quantity cannot be allocated among them without a rule nobody has chosen.
- A second risk run kept beside the desk's. The desk holds one run at a time.
- Staying on New trade after a booking, a common order of parts for every ticket, or
  any change to a product's editor.

## Anti-goals

- An amount, sign, direction, eligibility or quantity limit is decided in the browser.
- A draft opens part-filled, or with a term the backend did not state.
- A button is offered that fails when pressed without saying why, or is hidden
  where its refusal would have explained something.
- New trade chooses an instrument for the person.
- The unsaved-edit question, the retained-request block or the revision check of
  Close out is weakened for the new actions.
- A one-trade risk run is read as the desk's: rows outside it look valued at zero or
  "not valued at this close".

## Constraints

- TypeScript is presentation only. A label may follow a fact the backend states; no
  term of a trade is computed there.
- Never a silent default: the draft's quantity and direction are stated by the
  request; anything missing or ambiguous refuses the whole draft with a reason.
- UI and agents use the same authorized operations. The new draft is a backend read
  with the authority of the close-out draft; booking stays the booking operation.
- One entry pane, one mode, one instance of each editor (D421) is kept.
- Fields and actions not wired are drawn Coming soon and send nothing.
- This Mac has no database. Web unit and headless-browser tests and the .NET build
  run here; database-backed checks and live browser checks run in CI.

## Approaches

**New trade's list.** Three were weighed.

- *A. The bar is the only list (chosen by the owner).* The board is removed. The bar
  is drawn in both states; with nothing chosen the ticket area says to choose one.
- *B. The board is the only list.* Once chosen, the page shows the ticket and a
  Change instrument link back to the board. One list, but switching instrument is
  two presses and loses the at-a-glance bar.
- *C. Keep both (today).* Rejected: it is the complaint.

**Buy more and sell.** Three were weighed.

- *A. Backend draft relative to the position, quantity stated first (chosen).* The
  request says increase or reduce and a positive quantity. The backend returns the
  whole draft, as Close out does. Nothing is blank, so no form can be booked with a
  sign the person did not mean.
- *B. Backend draft with the quantity left blank.* The owner was first told this.
  It does not work for futures: the booking form holds signed lots, so a blank
  quantity carries no direction and Buy more and Sell would open the same form.
- *C. Coming soon.* Rejected by the owner, who asked for the backend.

**Risk for one trade.** The risk read already accepts trade identifiers inside the
authorized books (D292) and the risk report already carries them. The Blotter sends
them. A separate retained run for a position was considered and left out: it needs a
second run slot in the lens, the run bar, export and the queued-job history.

## New trade: the instrument bar

- `#new-trade-product` is always the grouped bar: each group a label and a segmented
  set of buttons, the chosen one pressed. The tiles, their board layout and the
  `data-state` switch between two layouts go.
- An instrument's one-line description is its button's `title`, and the line under
  the bar for the one chosen, as today.
- With nothing chosen the bar is followed by "Choose an instrument type to enter a
  new trade." No instrument is chosen for the person.
- The bar scrolls with the page, as the chooser did: a ticket's Preview and Book
  already stay at the top of the page while it scrolls, and two bars held there
  would stack. In a narrow container (the pop-out's Details pane, a phone) the bar
  wraps, a group at a time.
- `data-state` still says whether an instrument is chosen; the layout does not
  depend on it.
- Operating right keeps its ↗ and goes to Operating facilities; Storage keeps Coming
  soon. Button ids, groups, order, what each window offers, the leave question on
  switching and the retained-request block are unchanged.

## Position actions

The head of a position's details (`web/src/position-panel.ts`, `drawHead`) has one
action row, in this order:

| Button | Does |
| --- | --- |
| Close out… | The `draftCloseOut` operation, as the row menu's item |
| Amend | As today, including Close editor while editing |
| Buy more… / Sell… | The new `draftAdjustment` operation, after a quantity is stated |
| Risk | Opens the risk report scoped to this trade |
| Open curve | As today |
| Terminate, Novate | Coming soon: disabled, dashed, never sent |

- Each real button follows the rule of the row menu: an action the account may not
  take, or that the row's state rules out (stale values, booking unavailable, no
  amend right), is shown disabled with its reason, never hidden and never offered to
  fail. The reasons are the ones the row menu gives; one function supplies both.
- The pair of adjustment buttons is named from the position's own stated direction:
  a future with positive lots reads **Buy more…** and **Sell…**, one with negative
  lots **Sell more…** and **Buy back…**, anything else **Increase…** and
  **Reduce…**. The name is a label only. What is sent is `increase` or `reduce`.
- Pressing one shows a single row under the buttons: the quantity, in the position's
  stated unit (lots for a future), with Draft trade and Cancel. Draft trade sends
  the operation. The field is required and is sent as typed; the backend validates.
- The row menu gains the same two items, so the menu and the buttons stay one list.

## The adjustment draft (backend)

`POST /trading/position-adjustment-draft`, beside `/trading/close-out-draft` and
built on the same code, with the same authority (`ReadTrades` in the trading view).

Request, every member required, unknown members refused: `context`, `tradeId`,
`bindingId`, `revision`, `instrumentType` as for the close-out draft, plus
`direction` (`increase` or `reduce`) and `quantity` (an exact positive decimal).

Result: the close-out draft's shape (`kind`, `terms`, `price`, and for a swap
`periods` and `settledPeriodsExcluded`) plus `adjustment:{direction,quantity}`
echoing what was drafted. The forms open it as they open a close-out.

- **Future.** Same contract, book, parties and implementation. Lots are the stated
  quantity with the position's sign for `increase` and the opposite sign for
  `reduce`. The price is the Blotter's mark at the selected close, labelled as the
  curve price, as for Close out.
- **Swap with one schedule.** Same terms with the stated quantity as its quantity,
  its coefficient kept for `increase` and reversed for `reduce`, the fixed price set
  to the floating side's curve price as for Close out.
- **Refused, with a reason, code `close_out`** so the details show it in place of a
  form: a quantity that is not an exact positive decimal; a `reduce` larger than the
  open quantity (that would reverse the position: close it out and book the new
  side); a scheduled swap; every case the close-out draft refuses (contract not
  trading at the close, no mark, settled or ambiguous periods, revision not current,
  missing parties); and an instrument neither draft supports.
- Lot increments, limits and everything else the booking operation checks stay with
  booking. The draft books nothing.

Calls made here, for the owner to correct:

- The quantity is asked before the draft and not left blank in the form (see
  Approaches).
- The drafted price is the curve price at the close, as for Close out, labelled so
  and replaced by the person's own price on edit.
- A reduce equal to the open quantity is allowed; it is the close-out.
- An unsupported instrument is refused by name by both drafts. Today the close-out
  draft answers "position unavailable" in a banner for, say, a physical gas trade;
  with a visible button that is not good enough.

## Risk for one trade

- **Risk** in the position's actions opens the risk report in the Details pane with
  the Blotter's scope narrowed to this trade (`tradeIds:[tradeId]`). The report's
  scope line names the trade. Its setup, Run now, Queue as job and history are the
  report's own.
- The desk holds one run. A one-trade run replaces the desk's run, and says so: the
  run bar and the scope line name the trade, and a row outside the run reads "Not in
  this run" in the lens and in its Risk tab, never as unvalued.
- The toolbar's **Run risk** returns the scope to the books on screen.
- A pricing policy that needs whole books refuses a one-trade scope; the report
  shows the backend's reason.
- Needs `RunScenario`, as the toolbar's button; without it the button is disabled
  with the same words.

## What does not change

- Booking, amendment, the close-out draft's results, permissions, stored data and
  valuation.
- The Details pane's tabs, the pop-out's hand-over, the assistant's page identifiers.

## Verification

- TDD. Web unit tests first: the bar's single state, action items and their reasons,
  adjustment labels, the request and draft readers, the risk scope. Headless-browser
  tests for the bar's layout wide and narrow.
- Backend: the .NET build here; `CloseOutDraftChecks` gains adjustment cases (long
  and short future, each direction; reduce beyond open; malformed quantity; swap
  kept and reversed; scheduled swap refused; unsupported instrument) and runs in CI.
- Live browser checks for the buttons extend `tests/browser/blotter-close-out.mjs`
  and run in CI.
- One independent review at the end, on the draft's authority and numbers and on the
  unsaved-edit and revision paths.

## Documents and decision

A decision record takes the next free number when the work is ready to merge. It
supersedes D421's sentence on the chooser's two states and extends the row menu of
the [Blotter workbench](../../blotter-workbench.md).
