# Scheduling by desk, for schedulers and operations

Status: step 1 implemented (D461). Step 2 not implemented.

Depends on [product scope and desks](2026-11-27-product-scope-and-desks-design.md),
which defines a desk and which desks an installation runs.

## Intent

The Scheduling group grew by accretion. D367 named it for two pages; three more
arrived with their features. A scheduler now finds delivery work in two places,
starts from a trade when the working unit is a gas day, and chooses among nine
Facilities sections that mix gas, crude and setup. Two entries are named
Facilities and Operating facilities and mean different things. Lifecycle lists
option exercise and swap payments beside gas title receipts.

Scheduling work differs by commodity, so the group is arranged by desk. A person
picks their desk and sees only its entries, in the order the work is done: see
the position, move it, record what happened, settle differences. A row seen in
one entry opens the matching work in the next.

Steps 1 and 2 below change where screens are listed, what they are called and how
they link. They change no calculation, permission, stored data or financial
behavior. Step 1 adds one scope parameter to an existing read, and step 2 adds
two identity fields to another.

## Who this is for

- **Gas scheduler.** Works by gas day and pipeline: position, nominations,
  confirmations, allocations, imbalances.
- **Crude operations.** Works by terminal and custody point: receipts,
  reservations, dispatches, invoices.
- **Trading operations.** Works by trade event: exercise, expiry, settlement
  determination, margin.

## What exists today

| Sidebar entry (page id) | Keyed by | Reads at |
| --- | --- | --- |
| Lifecycle (`lifecycle`) | One discovered action occurrence | Financial close |
| Physical operations (`physical-operations`) | One trade, then one Side and day | Financial close |
| Facilities (`facilities`), nine sections | Varies by section, below | Operating business date |
| Operating facilities (`physical-power`) | One power facility version | Financial close |
| REC inventory (`rec-inventory`) | One certificate account | Its own business date |

Facilities sections, in today's order: Trades and volumes (facility profile and
window), Pipeline outlook (gas days by pipeline, across trades), Facility
definitions (facility), Physical activity (facility), Quantities & allocations
(point, gas day, commodity and legal entity), Transport capacity (entitlement),
Transport route plans (route), Gas delivery (route), Crude delivery (trade).

Three facts shape the proposal:

1. Pipeline outlook is already the cross-trade, per-gas-day view a gas scheduler
   starts from. It is the second of nine sections, and Facilities opens on
   Quantities & allocations.
2. Every read in Physical operations is scoped to one selected trade, and its
   directory lists gas, crude and LNG trades together. The outlook's Booked
   Sides rows open the Blotter, not that Side's delivery work.
3. Operating facilities has no dispatch, outage or actuals workflow. It saves
   parameters, previews supplied-plan pricing and books operating-right trades.
   Lifecycle reads a financial close and most of its actions are trade events.

## Goals

1. A person sees one desk's scheduling entries at a time.
2. The Gas desk's first entry is the day's position across trades.
3. Each entry holds one kind of work and has at most four tabs.
4. A Side and day seen in the position view opens that Side and day's delivery
   work directly.
5. Pages that are not scheduling work are listed with the desk that uses them.

## Non-goals

- New business operations, permissions, tables or migrations.
- A cross-trade crude position. Each crude read is one trade today; a terminal
  view across trades needs a new read with its own contract.
- Marine cargoes, truck and rail tickets, voyages, time charter, laytime and
  demurrage. None has a backend.
- Power scheduling entries beyond REC inventory. What a power scheduler does
  here has not been surveyed; it gets its own design.
- Entering nominations or allocations for many Sides at once. Only the existing
  per-route nomination batch does that today.
- Changing which clock any page reads. See "Decisions for the owner".
- Splitting setup from operating work inside Capacity, Route plans or Facility
  definitions. Each is one consumer today.

## Anti-goals

- A page or section reachable today becomes unreachable, or reachable by an
  account that could not reach it before.
- A leave guard, unsaved-edit question or retained-request recovery is lost when
  a section becomes a sidebar entry or the desk changes.
- A desk is shown with entries that do nothing. A desk appears only when it is
  enabled and has an entry the account can open.
- A label implies capability that does not exist: completion states for gas
  stages, live transmission to a pipeline, or commodities a view does not read.
- The browser filters, totals or classifies business facts to make a view.

## Constraints

- TypeScript is presentation only. Every quantity and status stays backend text.
- Page ids stay as they are. The assistant's page admission
  (`assistant.is_prompt_page`), `webPages`, the `facilitySection` report mapping
  and page memory depend on them.
- Sidebar availability must equal today's section gates in
  `facilities-workspace.ts`, so no entry widens what an account sees. The server
  still authorizes every read and write.
- A missing context is refused with its reason, as `nativeDeliveryReason` does
  today. A link never chooses a close or a date for the user.

## Approaches

**A. Relabel and reorder only.** Rename the two Facilities entries and reorder
the nine sections. Cheapest, but delivery work stays split and the day view stays
buried.

**B. One list in workflow order.** Facilities' sections become sidebar entries
in one Scheduling list for every commodity. Better for gas, but crude and REC
entries sit among gas ones and the list grows with each commodity.

**C. Entries by desk (recommended).** The same views as B, grouped under a desk
switcher. Each desk is a short list in the same order of work. Entries are views
of the existing pages, by the mechanism Risk uses (`risk-views.ts`, D365, D392).
No new page ids, operations or migrations.

**D. A new day-first workbench.** One page per gas day with inline entry across
trades. It needs new cross-trade reads and multi-row write operations with their
own contracts and review. C does not block it, and the Gas desk's position view
is where it would later live.

## Sidebar

`navigationGroups` in `web/src/navigation-groups.ts` and the `nav-group-*`
markup in `web/src/index.html` change together.

**Trading** gains **Lifecycle** and **Operating facilities**. Both keep their
names and page ids. Their eyebrow becomes TRADING. The Blotter's option and swap
settlement panels already link to Lifecycle; their text that places it under
Scheduling changes to Trading.

**Scheduling** shows a desk switcher above its entries. The switcher lists each
enabled desk that has at least one entry the account can open, and remembers the
account's last choice. With one such desk, no switcher is drawn.

| Desk | Entry | Is today | Tabs |
| --- | --- | --- | --- |
| Gas | Gas day position | `facilities` · Pipeline outlook | Volumes, Booked Sides, Capacity, Balances (its existing views) |
| Gas | Trade deliveries | `physical-operations`, gas trades | Overview, Delivery, Billing & corrections |
| Gas | Nominations & transport | `facilities` · Gas delivery, Transport route plans, Transport capacity | Nominations, Route plans, Capacity |
| Gas | Storage & allocations | `facilities` · Quantities & allocations, Physical activity, Trades and volumes, Facility definitions | Allocations, Physical activity, Trades and volumes, Definitions |
| Crude | Trade deliveries | `physical-operations`, crude trades | Overview, Delivery & cash |
| Crude | Receipts & dispatches | `facilities` · Crude delivery | None |
| LNG | Cargoes | `physical-operations`, LNG trades | Overview, Delivery |
| Power | REC inventory | `rec-inventory` | Unchanged |

Notes on the table:

- **Gas day position** is named for what it reads.
- **Trade deliveries** replaces "Physical operations", which described the whole
  group. Each desk's entry lists that desk's trades.
- **Storage & allocations** replaces "Facilities" on the Gas desk. Definitions is
  the last tab because it is setup.
- The eyebrow above a page title names the group and the desk, for example
  SCHEDULING · GAS.
- Setup is not a desk. It stays in Reference Data → Locations & delivery, where
  Delivery setup already switches between gas and crude.

### Views of the existing pages

A navigation-only module in the idiom of `risk-views.ts` maps the entries onto
the `facilities` and `physical-operations` pages. An entry sets which sections
the workspace offers and which one opens. From another page, an entry opens its
page through the existing guards and takes effect once the page has moved; a
refused leave changes nothing. Changing desk is a navigation like any other and
goes through the same guards.

- The Facilities tab row and narrow-viewport picker list only the current
  entry's sections. The nine-item picker goes away.
- Moving between two entries of one page is a section change. It uses the
  existing `chooseSection` path with its pending-command and unsaved-edit
  checks.
- The page title is the entry's name. The assistant's page report is unchanged,
  because it is derived from the page and section, not the entry.
- Availability: Gas day position and Nominations & transport need
  `ReadMarketData`, `ReadTrades` and `ReadGasOperations`, the sections' gate
  today. Storage & allocations and Receipts & dispatches keep the page's current
  rule, and tabs keep their individual gates.
- Back-navigation still cannot restore Trades and volumes or the position
  view. The page report names both as facility quantities, and telling them
  apart reaches the backend page registry. That is left for later.
- The other views' tabs stay in the document, hidden, so an exact section can
  still be opened from elsewhere. The sidebar entry and the desk then follow
  the section shown.
- Each Trade deliveries entry lists its own desk's trades.
  `physical/operations/directory` accepts the desk whose trades to list, and the
  server applies it before paging. The browser does not hide rows. A request
  without a desk is unchanged.

## Links between entries (step 2)

**Position to delivery.** A Booked Sides row gains an "Open delivery" action
beside its trade link. It opens the Gas desk's Trade deliveries on that trade's
Delivery tab with that Side and day selected.

- The outlook's `sources` rows carry trade id, revision, day and Side label, but
  not the binding or obligation identity that Trade deliveries selects by.
  `gas/pipeline-outlook` adds those two exact identifiers to each row. This is an
  additive change to an existing read; this link is its consumer.
- `PhysicalDeliveryTarget` gains an optional obligation and day.
  `physicalOperations.open` already exists for the crude workbench's buttons and
  goes through the leave guard.
- Trade deliveries reads at a financial close. When none is applied the action
  is refused with the existing reason. It does not pick one.

**Delivery to position.** A selected gas delivery row gains "View gas day
position", which opens the position view on that row's pipeline and day. The
view accepts a pipeline and window to open on; the user can change them as now.

**Stage names.** The four gas delivery stages are renamed so none repeats the
group's name and each says what is done there:

| Today | Proposed | Actions (unchanged) |
| --- | --- | --- |
| Before flow | Nominate | Service account & terms; Submitted nomination / retro request |
| Scheduling | Confirm | External acknowledgment; Apply accepted schedule |
| After flow | Allocate | Allocated delivery quantity; Operator balance statement |
| Reconciliation | Balance | New imbalance agreement; Apply accepted imbalance transfer |

They remain topics, not completion states (D341). The stage is reported to the
assistant by index, so this is one constant and its tests.

## Delivery order

Each step leaves the application working and is reviewable on its own.

1. Sidebar: move Lifecycle and Operating facilities to Trading; add the desk
   switcher and its entries as views of the existing pages, with the directory
   scoped by desk.
2. Links between entries and the stage names, including the outlook read's two
   added identifiers.

The switcher lists every desk with entries until the installation setting in the
scope design exists, then reads the enabled desks from it.

## Verification

- Failing behavior checks first, per `AGENTS.md`.
  `web/tests/navigation-groups.test.mjs` states the new groups and desks before
  they exist.
- A permissions table test: for each installed role, the set of reachable pages
  and Facilities sections is identical before and after.
- Browser checks: each entry opens its default tab; changing desk or leaving an
  entry with an unconfirmed command or unsaved edit asks as before; a desk with
  no openable entry is not listed; "Open delivery" lands on the exact Side and
  day and is refused without an applied close.
- `tests/browser/*` and `web/tests/*` that name `nav-*` ids, the SCHEDULING
  eyebrow, Lifecycle's place under Scheduling or the stage labels are updated
  without weakening what they assert.
- Backend checks: the outlook's added identifiers equal the identities
  `gas/operations/read` accepts for the same Side and day, in current and
  retained knowledge; the scoped directory returns exactly the desk's trades
  with unchanged pagination, and the unscoped request is unchanged.
- `tools/check_all.py` passes. One independent review covering authorization of
  reachable pages and both changed reads in historical views.

## Documentation

Add the next decision id to `docs/decisions.md` after the scope decision,
amending D367's page list. Update `docs/physical-workspace.md`,
`docs/delivery-workbench.md`, `docs/gas-facility-desk.md`,
`docs/operating-facilities.md`, `docs/structured-power-trading.md` and other
"Scheduling →" references. Record in `docs/plan.md` the items under "Decisions
for the owner" that are deferred.

## Decisions for the owner

These are not designed here. Each changes behavior or adds operations, so each
needs its own decision.

1. **Which clock Trade deliveries reads.** It reads a financial close, so a
   scheduler must apply one before recording a nomination. The Facilities views
   read an operating business date. Moving delivery work to a business date is a
   temporal change with its own review.
2. **Owned gas, park and loan.** Title receipts, dispatches, park and loan
   movements and custody are Lifecycle actions at a financial close. Whether the
   Gas desk lists them as an entry of its own, and at which clock.
3. **Crude terminal position.** A cross-trade view of receipts, reservations
   and dispatches by terminal, the crude counterpart of Gas day position.
4. **Setup out of Scheduling.** Whether entitlement, route and facility
   definitions move to Reference Data → Locations & delivery, which means
   separating the definition and operating halves of three consumers.
5. **Entry across many Sides.** Recording confirmations or allocations for a
   whole gas day in one reviewed operation (approach D).

## Choices made on the owner's behalf

All of these are proposed and can be changed: the entry and tab names; moving
Lifecycle and Operating facilities to Trading; a switcher over one long list;
keeping setup in Reference Data and not as a desk; the four stage names.

## Left to the builder

The view module's shape, the switcher's control and where its choice is
remembered, entry icons, each entry's default tab when its first tab is not
permitted, and the wording of interface strings whose location changes.
