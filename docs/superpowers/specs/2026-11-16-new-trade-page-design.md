# New trade page and grouped instrument chooser

Status: implemented by D421. Live browser checks run in CI.

## Intent

New trade lists ten instruments in one row and opens every ticket inside the
Blotter's Details pane, below the positions. Products added since the row was
designed made the row long, and a ticket with two or three sides has no room there:
its sides stack and the person scrolls to compare them.

New trade becomes its own page under Trading, above Blotter. Instruments are chosen
from named groups, and the ticket has the page's width, so the sides of a trade read
next to each other with the schedule under them.

This changes where entry happens and how it looks. It changes no booking operation,
permission, API, stored data, valuation or financial behavior.

## Goals

1. New trade is a sidebar entry under Trading, above Blotter, for every bookable
   instrument.
2. Instruments are grouped: Financial, Power, Gas & LNG, Oil & liquids, Structured.
3. On the page, a ticket's sides sit next to each other, with the trade's own fields
   above them and the schedule and preview below at full width.
4. There is one entry screen. The Blotter's New trade button opens the page.
5. Operating rights (thermal toll, battery, solar output), which are bookable today
   only from Operating facilities, are reachable from New trade.

## Non-goals

- Backend changes of any kind: operations, permissions, tables, migrations, page
  identifiers the backend knows.
- Booking for Storage. It stays Coming soon. Transport became bookable while this
  was designed (D416) and is listed as an instrument like any other.
- Moving operating-right entry off Operating facilities. It depends on a saved
  facility version and a supplied plan, and is its own project.
- Moving more products onto the description-driven ticket
  ([deal ticket](../../deal-ticket.md)). Each product keeps the editor it has.
- Amendments and close-outs. They start from a position and stay in the Blotter's
  Details pane.
- A dedicated read for the futures contract list. A new future still reads the
  Blotter's workspace to get its priced contracts, as it does today.

## Anti-goals

These would make the work a failure even if the page looked right.

- A retained request (a booking whose outcome is not known) is not shown on the
  page the person is on, or takes more than one step from there to check or retry.
- Visiting New trade drops the Blotter's read, its values or a risk run on the desk.
- An unsaved-edit question is lost: leaving the page, switching instrument, changing
  account or context all still ask before entries are dropped.
- A second copy of an editor exists, so that two forms of one product can hold
  different entries.
- The chooser selects an instrument for the person, or a ticket opens with a value
  the person did not choose and the backend did not propose.
- Any amount, date, schedule or eligibility is computed in the browser.
- The pop-out Blotter loses entry, hand-over of a retained request, or Bring back.
- An instrument is offered that cannot be opened where it is offered.

## Constraints

- TypeScript is presentation only (AGENTS.md). The page moves and restyles existing
  forms; it adds no rule.
- The Blotter view (`web/src/futures-view.ts`) owns state that entry shares with
  amendments, close-outs and risk: the single retained-request slot and its storage
  key, the booking catalog, the swap and futures editors (one instance each, used for
  new trades and for amendments or close-outs), and the product editors that
  `product-tickets.ts` moves between hosts. One mode is shown at a time. This design
  keeps that: it does not create a second controller.
- The backend and saved data know the assistant's page identifiers
  (`blotter_new_trade`, `blotter_new_future`, `blotter_new_swap` and the other
  `blotter_new_*`). They are kept.
- Tickets are vertical inside a side and inside a form without sides: one field per
  row, never a grid of fields. Two things sit across the page: the sides, next to
  each other, and the band of trade-level fields above them (the trade strip of the
  [deal ticket](../../deal-ticket.md)).
- The order a person tabs through a ticket is the order they see. Nothing is
  reordered by style alone.
- A field the backend does not use is drawn Coming soon and is never sent.
- No new dependency.

## Approaches

**A. One entry pane, shown on its own page (chosen).** New trade is a sidebar entry
and a page to the person: its own title, the whole area, none of the Blotter's
toolbar, positions or charts. Underneath it is a second presentation of the Blotter's
page, as Risk's sidebar entries are views of the one Risk page (D365, D392). The pane
that holds a new trade today, with its head, form host and unsaved-edit question,
fills the area in that presentation and sits in the Blotter's dock otherwise. The
Blotter view's existing entry functions drive it. Every editor, the retained-request
slot, the storage keys, the assistant's identifiers and the pop-out keep working
unchanged, because there is still one pane and one mode.

A section of its own, hidden from the Blotter's, was tried on paper and dropped: the
Blotter's reads stop when its section is hidden, a new future is opened by such a
read, and a booked trade is followed by one. One section shown two ways keeps every
read, guard and leave question the Blotter has.

**B. Lift entry into its own controller.** A new module owns the chooser, the
product forms and booking; the Blotter keeps positions, amendments and close-outs.
Cleaner on paper, but the editors and the retained-request slot are shared by entry
and amendment, so this means either two instances of each editor (the second-copy
anti-goal) or a broker between two controllers for every shared piece. It rewrites
how the pop-out hands a retained request to the main window and touches every live
browser check that books a trade. Nothing the person sees differs from A.

**C. Maximize the Blotter's Details pane.** No new page; New trade fills the dock.
Cheapest, but it is not a menu entry, the page is still titled Blotter, and the
Blotter's toolbar and pane chrome surround the ticket.

A is chosen: it gives the page and the room without duplicating state, and leaves B
possible later if entry and amendment are ever separated in the backend contracts.

## Sidebar and page

- `navigationGroups` (`web/src/navigation-groups.ts`) lists Trading as
  `['new-trade','futures','options']`. `nav-new-trade` sits above `nav-futures` in
  `#nav-group-trading`; its label is **New trade**.
- `pageAvailable('new-trade')` is the Blotter's rule: `ReadMarketData` and
  `ReadTrades`. An account that can read trades but not book sees the page with the
  backend's reason in place of the chooser, as the Blotter's button says it today.
- `firstPage` is unchanged: accounts still open on the Blotter.
- The page title is **New trade**, and once an instrument is chosen, its name
  ("New swap", from `modeLabel`). The eyebrow is Trading.
- The page uses the full width beside the sidebar and a docked Assistant, as the
  Blotter does, not the 1680 px cap of ordinary pages.
- The header shows the As of and context controls the Blotter shows; a new trade is
  booked under the same selection.
- The app's own page stays the Blotter's while New trade is shown, so its leave
  guards, unsaved-edit checks, reads, live refresh and context controls apply
  unchanged. The Blotter view says which presentation it shows; the sidebar marks
  New trade or Blotter from that, and the title follows it. The assistant is told
  through the Blotter view's existing report, so it sees the same `blotter_new_*`
  identifiers. Their display name in `assistant-pages.ts` becomes "New trade".

## Opening and leaving

| From | Result |
| --- | --- |
| Sidebar **New trade** | The page, on the chooser, or on the ticket it already holds |
| Blotter toolbar **New trade** | The same |
| Options page, book a series | The page with the futures option ticket for that series |
| Assistant or walkthrough opening a ticket | The page with that ticket |
| `showPage('ppa' \| 'gas' \| 'crude')` | The page with that ticket |

- Entering the page keeps what the Blotter has read: its positions, values and any
  risk run stay as they are behind the page and are there on return. When the
  Blotter has a read, the page uses its booking catalog; when it has none, the page
  reads the booking directory alone, with no valuation. `loadEntry` as it stands
  clears the Blotter's read and is replaced.
- Choosing Future reads the Blotter's workspace for its priced contracts, as it does
  today inside the Blotter, with the same effect on what the desk shows.
- The page holds only new trades. A close-out draft or an amendment opened from a
  Blotter row opens in the Blotter's Details pane as today, including when its form
  is the same editor.
- Leaving the page with entries asks first (the existing leave question). Entries
  the person keeps stay on the page; entries they give up are dropped, as
  `leavePage` does today for the Blotter.
- While the Blotter is in its own window, the page shows the Blotter's existing
  "in its own window" notice with **Bring back**; entry in the main window is not
  offered, as today.
- The pop-out Blotter keeps its toolbar New trade and opens tickets in its own
  Details pane, since it has no sidebar. Its chooser lists only the instruments it
  can open (Future and Swap); today it lists all ten and eight fail when chosen.
- Booking is unavailable in a historical or live view, with an unapplied context, or
  without the permission. The page then shows the reason (`newTradeControl`) and no
  chooser, so no field opens where booking is impossible.

## Instrument chooser

One ordered definition in `web/src/new-trade.ts` replaces `newTradeProducts` and
`comingSoon`; the retained-request labels and notices read their names from it.

| Group | Entries |
| --- | --- |
| Financial | Future, Futures option, Swap |
| Power | PPA, REC forward |
| Gas & LNG | Physical gas, Park and loan, Physical LNG |
| Oil & liquids | Physical crude |
| Structured | Operating right ↗, Transport, Storage (Coming soon) |

- A group with no entry the window can open or show is not drawn. No empty heading
  is added for future products; refined products, NGLs and biofuels join Oil &
  liquids, rates and FX get a group of their own, and environmental credits get one
  that REC forward moves to, each when its first instrument is bookable.
- No instrument is selected for the person.
- **Nothing chosen:** the chooser is the page. Each group is a labelled block of
  instrument tiles; a tile has the instrument's name and the one-line description
  `new-trade.ts` already holds (`about`).
- **An instrument chosen:** the chooser becomes one compact strip above the ticket,
  group labels with small buttons, the chosen one pressed. Every instrument stays
  one click away; switching asks first when the ticket holds entries.
- Operating right is a link, marked ↗, described as "Thermal toll, battery or solar
  output right · entered on Operating facilities". Choosing it goes to that page
  through the ordinary page change and its leave question, and does nothing else
  there: no facility, kind or draft is chosen for the person. It is drawn only for
  an account that can open Operating facilities. It is one entry, not three, because
  the kind belongs to the facility the person picks on that page.
- Transport is a bookable instrument (D416) and sits under Structured with the other
  capacity and rights contracts, not under Gas & LNG. Storage keeps the Coming soon
  treatment and hover text it has.
- Button ids `new-trade-<product>` and the group's `new-trade-product` id are kept.
- In a narrow container (the pop-out's Details pane, a phone) the strip wraps.

## Ticket layout on the page

The page gives the ticket a wide container. The rules that force a ticket vertical
in the Details pane (`.details-form` in `web/src/app.css`) apply only there.

What the page gives every ticket:

- **Title row**: the instrument's name and its one-line description.
- **Trade fields**: trade date, book, counterparty and the product's other
  trade-level fields, several across in one band.
- **Sides**: one card per side, next to each other, in instrument order, each with
  its coloured top edge and kind pill. Inside a card, one field per row. Rows every
  side has (direction, start, end, quantity, price, payment) line up across the
  cards, as `side-card.ts` already aligns them. The add-side control follows the
  last card. More sides than fit scroll sideways without squeezing the cards.
- **Schedule and preview**: full width.
- **Actions**: Preview and Book stay in view while the ticket scrolls.

Each editor keeps the order of its own parts. The editors are the same instances
the Details pane shows for amendments and close-outs, and they do not agree today:
the swap editor has its actions at the top and its trade terms after the sides, and
the crude editor puts its trade fields after its sides. This project does not make
them agree. Where one part must move for the page to read well, it moves in the
markup, for both hosts, and its checks move with it; a common order for every
product comes with the description-driven ticket.

- Future, Futures option and REC forward have no side cards and get no artificial
  one. Their form keeps one field per row at a readable width, with the contract's
  or certificate's read-only facts, mark and price preview beside it instead of
  below, where the editor already has those as a separate part.
- Below about 600 px of container width the sides stack, as they do today.
- Colours, type and spacing come from the existing tokens and ticket classes. The
  page adds layout, not a second visual language.

## After booking

- **Booked:** the person lands on the Blotter with the new position open in the
  Details pane, as happens today (`followBooked`, `bookedExternal`): they see its
  reference, terms and value. The toolbar's New trade is one click back to the page,
  which opens on the chooser with nothing carried over.
  Staying on the page for the next trade was considered. Booking receipts other than
  REC forward's carry no trade reference, so the page could only say "Booked"
  without naming the trade unless it read the Blotter anyway. It is deferred until
  the receipts carry the reference, which is backend work.
- **Refused:** the form stays with the backend's reason, as today.
- **Outcome not known:** the retained-request cards are shown above the ticket and
  the chooser is held back until the request is settled, as `newTradeControl`
  requires today. They are the same cards the Blotter shows, visible on both pages.
  A future's or swap's card has Check status and Retry in place. A product ticket's
  card has its Reconcile button, which opens that ticket on the New trade page,
  where the ticket's own Check status and Retry are; from the Blotter that is one
  step, through the ordinary page change.

## What does not change

- Each product's editor, preview, booking request, request id and reconciliation.
- The single retained-request slot, its storage keys and the pop-out's hand-over.
- The Details pane for positions, risk, shocked contracts, amendments and
  close-outs.
- The assistant's page identifiers and walkthrough steps. A walkthrough's pointer
  for opening a new future names the sidebar entry or the Blotter button, whichever
  page is shown.

## Documents and decision

A decision record takes the next free number when the work is ready to merge. It
supersedes the lines of [Blotter workbench](../../blotter-workbench.md) and
[deal ticket](../../deal-ticket.md) that place new-trade forms in the Details pane.
`docs/frontend-wiring.md`, the navigation table of the 2026-11-09 design and
`docs/plan.md` are updated with the result.

## Verification

- TDD for behavior: the chooser's groups and what each window offers, page
  availability, where each route into New trade lands, what the page shows after
  each booking outcome, and the leave question. Unit tests in `web/tests` first.
- `web/tests/*-browser.test.mjs` cover the wide layout with headless Chrome and no
  backend: sides next to each other at 1280, 1440 and 1920 px, stacked under 600 px,
  aligned rows, and that the Details pane still stacks.
- The live browser checks (`tests/browser`) open New trade through one helper,
  `tests/browser/new-trade.mjs`; it changes to open the page. Checks that use the
  Blotter's selectors directly are updated with it. They run against the synthetic
  preview on the development host and in CI, not on a laptop.
- Each product's ticket is looked at on the running preview before the work is
  called done: Future, Futures option, Swap (ordinary, scheduled, heat rate), PPA,
  REC forward, Physical gas, Park and loan, Physical LNG, Physical crude, Transport.
- One independent review at the end, covering the unsaved-edit and retained-request
  paths in particular.

## Left to the builder

- How the Blotter's section hides its own parts in the New trade presentation,
  provided there is one pane and one mode.
- Per-product spacing and column widths in the wide layout, within the rules above.
- Where the retained-request card's element lives so that both pages show it.
