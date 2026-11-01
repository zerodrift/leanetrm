# Deal ticket

Status: implemented locally for physical gas (see [Current implementation](#current-implementation));
the other sided products are still to move: financial gas and power swaps
(including scheduled swaps), heat-rate swaps, PPAs, physical crude, and gas
transport, park and loan and storage. Its decisions are recorded as D278–D281 in
[decisions.md](decisions.md).

## Why

Every sided product has its own hand-built entry screen today: the swap editor,
the scheduled-swap editor, the physical gas editor and the PPA editor. Each
looks and behaves differently, the PPA entry has never been intuitive, and the
next products (physical crude, LNG cargoes, freight, battery, gas storage) would
each bring another. Traders cannot compare the sides of a trade at a glance, and
reference choices and looked-up values are filled differently on each screen.

The deal ticket is one way to enter and read every sided trade. The backend
describes each product; the browser draws any product from that description in
one layout. A new product is backend work plus its description, with no new
screen. The same ticket shows a new trade, a booked trade opened from the
Blotter, an amendment and a close-out, and it shows the trade's schedules as the
person edits.

The ticket also leads product work. A product's ticket shows every field the
product will have. Fields the backend does not use yet are drawn as
*coming soon* (below), so each product's remaining work is visible on screen.

## Goals

1. One ticket for every sided product: physical gas, financial gas and power
   swaps (including scheduled swaps), heat-rate swaps and PPAs move onto it in
   this project.
2. Sides read side by side. Fields that every side has line up across the
   sides; each side's own fields follow.
3. Drop-downs list exactly the eligible reference data for the field, filtered
   by the choices already made. Values the backend can look up (prices, tariff
   rates, payment dates, gas-day starts, asset nodes) are filled in and say
   where they came from.
4. The trade's schedules are visible while editing and update after each change.
   The joint instrument schedule is the main view; each side has its own.
5. A booked trade shows what is settled fact and what is still forward: every
   value says where it came from.
6. New products plug in through their description. Coming-soon fields make a
   product demonstrable before all of its fields are implemented, without
   pretending they are.
7. A table widget that renders every grid in the ticket beautifully, reusable by
   other pages.

## Non-goals

- New product backends (crude, LNG, freight, battery, storage, pipeline crude).
  Each is its own later project that starts from its ticket.
- Futures entry. A future has no sides and gets no artificial one (architecture,
  §Instruments). Its tables take the new grid look only.
- The Blotter's panes, docking, row menu and pop-out. The
  [Blotter workbench](blotter-workbench.md) owns them; this ticket is what its
  Details pane shows for a sided position.
- Changing what a product books, values or settles. Moving a product onto the
  ticket keeps its booking contract, preview, idempotency and refusals.

## Anti-goals

These would make the work a failure even if the screens looked right.

- **Business rules in the browser.** Totals, net cash, spark spreads, quantity
  derivations, TOU evaluation, schedules, payment dates and lookups come from
  the backend. The browser formats exact decimal strings and never computes an
  amount.
- **Silent defaults.** A looked-up value is a visible, labelled proposal the
  person can see and change. A missing lookup is an empty field with the
  backend's reason, never a guess. Nothing is filled from a previous trade,
  another side or a product-wide default unless the description declares that
  lookup.
- **Fields that pretend.** A field that is shown as a term is captured, used by
  a backend consumer and tested (the field admission rule in
  [physical gas terms](physical-gas-field-contract.md)). A field that is not
  yet used is drawn as coming soon and is never sent.
- **A second set of booking paths.** The ticket calls the same authorized
  operations as the API, imports and agents.
- **One-off product screens creeping back.** A product that does not fit the
  description is a gap in the description, solved there.

## Constraints

From [AGENTS.md](../AGENTS.md) and the architecture, restated where the ticket
touches them:

- TypeScript is presentation only. The product description, lookups, previews,
  schedules and summaries are backend operations.
- Missing, ambiguous or conflicting behavior raises a structured error, shown on
  the field or side it concerns, with its reason.
- Reference choices are exact-version references, not names.
- Historical context (business time and knowledge cutoff) applies to every
  description, drop-down, lookup and preview read. A booked trade opened as of
  an earlier date shows the choices and values of that context.
- Every write is idempotent (D238). Previews are reads; booking keeps its
  request id and first outcome.
- UI and agents use the same operations. An agent can read a product's
  description and fill a ticket through the same contract.
- No new dependency. The grid uses Tabulator 6.6.1, already in `web/package.json`.
- Synthetic data in examples, fixtures and checks.

## Layout

Decided with the owner. Mockups were reviewed in the brainstorming session; this
section describes them.

### Trade strip

Across the top: the product and the trade's state (Draft, Booked · forward,
Booked · in delivery), then trade-level fields: trade date, book, counterparty,
agreement and the other trade facts the product's description lists. In
delivery, the strip adds a progress bar (for example, "Day 20 of 31 · 61%
delivered") and the delivered and remaining figures the backend reports.

### Side cards

One card per side, side by side, in instrument order. A card has a coloured top
edge and a pill for its kind (Physical, Payment, Floating, Fee, Cargo, Freight,
Attributes), its name, then two bands:

1. **Common to all sides** (green band). The same rows in every card, in the
   same order, at the same height: Direction, Start, End, Product / TOU,
   Quantity, Price, Payment. Labels may differ per side ("Price per
   MMBtu", "Rate per MMBtu"); the row is the same kind of field. The cards share
   one row grid so these rows line up exactly. Each row holds the term in the
   shape the booking contract admits: Payment holds the payment rule
   ("Month end + 20 business days"), not a date; the dates it produces appear in
   the schedule.
2. **Side details** (grey band). The side's own fields, at their natural height:
   pipeline, zone and point; index and pricing window; heat rate and unit
   conversion; asset, node and meter; tariff; route and vessel class.

Where a side cannot take a common field, its slot holds read-only information
on a grey background instead of an empty hole, so the row still lines up:

- a physical side's Price slot shows the market price at its point, with the
  curve and close it came from;
- a physical side's Payment slot shows which cash sides pay for it;
- a derived quantity shows its rule ("Power MWh × 7.50 · from heat rate").

Where a card ends before its neighbours, the space holds a **side summary** from
the latest preview: billed quantity, amount, and contract against market. After
the cards, a dashed **Add side** card offers the side kinds the product admits.

Clicking a card selects it and selects that side's schedule tab.

### Schedule panel

Below the cards, one grid with tabs:

- **Instrument** (first, open by default): the joint schedule of the whole trade.
  Each side is a column group in its card's colour; a final **Trade** group
  holds net cash, payment date and product-specific trade measures (spark
  spread for a heat-rate swap). All values come from the backend.
- One tab per side with that side's full schedule.

A toolbar holds the row level, a row filter, a column picker, CSV export and the
preview status ("Up to date · previewed after your last change", "Updating…",
"Preview refused: <reason>"). A totals row is pinned at the bottom.

**Mixed granularity.** Sides may run on different periods (daily delivery,
monthly payment; hourly generation, monthly payment). The Instrument schedule is
a tree whose levels are the product's periods. Each side shows its values on the
rows of its own granularity and on every coarser row as the backend's
aggregate; on a finer row a coarser side's cells are blank. Net cash and other
trade measures sit on the rows the backend reports them for (payment periods)
and aggregate upward. (Design call.)

**Additional tabs.** Views the current editors offer beyond terms and
schedules (draft risk, PPA break-even, swap observation and fixing inspection,
technical lineage) become further tabs after the side tabs, shown only for the
products whose backend offers them. They keep their existing operations.
(Design call.)

**Per-period inputs.** Inputs entered per period rather than per side (a
scheduled swap's explicit period quantities, physical gas daily quantity
records, PPA profiles) are editable cells in that side's tab, drawn with the
same field states as card fields. The description marks which schedule columns
are inputs. (Design call.)

**Row shapes.** The description declares one of two:

- **Flow-shaped** (gas, pipeline crude, swaps, heat rates, PPAs, power): rows are
  delivery periods. The row level switches Month / Day / Hour, offering only
  the levels the product's granularity has, and a month opens in place to its
  days and a day to its hours. Fifteen hourly years stay usable because only
  opened groups are loaded.
- **Cargo-shaped** (crude cargoes, LNG, freight): one row per lift, with laycan,
  bill-of-lading date, quantity with tolerance, pricing window, price and
  amount. The row level switches Lift / Pricing day. A pricing window shows its
  days as small squares, filled once fixed, with the anchor day outlined.
  No product in this project is cargo-shaped, so the cargo shape is designed
  here and built with the first cargo product, which is its consumer (AGENTS.md:
  no addition without a current consumer). The description schema leaves room
  for it. (Design call.)

**Changes.** After an edit, cells whose values moved carry a small amber marker
until the next edit.

**Sources.** Every value carries a small tag naming where it came from, as the
backend reports it: Contract, Fwd, Fixed, Settled, Actual, Meter, Nominated,
Estimated, Nominal, "3 of 5 fixed". In delivery, a line marks the valuation
date, delivered rows are dimmed, and the totals split into delivered, remaining
and total. The ticket shows a tag only when the backend supplies it; it never
infers that a value is actual from its date.

### Footer

Preview and Book (or Save amendment, Book close-out), with the note that green
fields were filled from a lookup.

### Pane width

The ticket lays out by the width of its container, not the window:

- **Wide** (the New trade page, a wide Details pane, the pop-out): cards side by
  side over the grid, as above.
- **Narrow** (a narrow Details pane): cards stack. A strip of side chips at the
  top jumps between them and selects the side's tab. The grid keeps the
  Instrument tab and scrolls sideways.

## Field states

Every field in the description has exactly one state. The ticket draws each the
same way on every product.

| State | Drawn as | Sent with the trade |
| --- | --- | --- |
| Editable | Input or drop-down | Yes |
| Looked up | Green field with a chip naming the source ("Close 30 Oct", "Tariff T-2", "Calendar", "Asset") | Yes, as the value shown |
| Derived | Read-only value with its rule | No; the backend derives it |
| Information | Grey read-only slot | No; not a term |
| Coming soon | Greyed, hatched, dashed, with a *Coming soon* tag; hover explains what it will do | No; holds no value |

Rules:

- A lookup proposes the value in the shape the booking contract admits (a
  payment rule, an exact reference, a decimal string), never a derived form the
  contract does not take.
- Lookup chips are an entry aid. Booking stores the value, not where it was
  looked up, so a booked trade's fields show no chips; its schedule shows the
  value sources the backend reports. (Design call.)
- A looked-up value is a proposal made by a backend lookup the description
  declares for that field. Editing it removes the chip; the value is then the
  person's and is never re-filled. A later change upstream (a new point, a new
  close) offers the lookup again on that field instead of overwriting it.
- A lookup that cannot resolve leaves the field empty with the backend's reason.
- A drop-down lists the eligible exact-version references for the field under
  the ticket's context, filtered by the choices it depends on (a point by its
  zone, an index by its commodity). Changing a parent choice clears dependent
  choices that are no longer eligible and says so; it does not pick a
  replacement.
- A coming-soon field cannot be focused for entry, has no value, and is omitted
  from preview and booking requests. Its hover text comes from the description.
  When the backend admits the field, its description changes its state; no
  screen code changes.

## Product description

The backend serves, per product and version, a description the ticket draws.
Its exact schema is left to the builder (below); it must express:

- the product, its version and its row shape (flow or cargo);
- trade-level fields;
- the side kinds the product admits, with minimum, initial and maximum counts, and for
  each kind: its pill, colour role, name, common-band fields and detail fields;
- for each field: its business meaning, type, unit, required condition, state,
  the reference read that lists its choices and the choices it depends on, and
  any lookup that proposes its value;
- the schedule columns for the Instrument tab and each side tab, by column
  group, with units;
- the operations for preview, book, amend and close-out, which are the
  product's existing authorized operations.

The description carries no business rule. It names operations and fields; the
operations hold the rules. Its fields are the same fields the booking
contract admits, so the description and the contract cannot disagree: a field
in the description that the contract does not admit is a check failure, unless
it is coming soon.

Products whose sides follow the [D253 side model](decisions.md#d253--one-side-model-parameters-a-whole-term-schedule-rows-read-from-the-graph)
describe its parameters directly (delivery terms, quantity rule, price term,
observation rule, payment rule). Physical gas keeps its own side families
([physical gas](physical-gas-side.md)) and describes them the same way.

## Live preview

After an edit, once the edit settles, the ticket sends the whole draft to the
product's preview operation and redraws the side summaries and the schedule from
the result. Only the latest preview's result is drawn; an older response that
arrives late is discarded. A refused preview keeps the last good schedule on
screen marked stale, with the refusal's reason on the fields it names. A
preview never books and never writes.

## Modes

| Mode | Opened from | Editable |
| --- | --- | --- |
| New | New trade; the Blotter's New trade | All editable fields |
| Booked | A Blotter row (Details pane), its pop-out | None; the Amend action opens the amendment mode |
| Amend | Amend… in the row menu or the ticket | The fields the amendment operation admits at the valuation date; others show a lock with the reason |
| Close-out | Close out… in the row menu | The close-out draft's fields, pre-filled by the backend close-out draft |

A mode is offered only where the product has the operation. Today physical gas
has no amendment beyond its daily quantity records, and the Blotter workbench's
close-out draft refuses heat-rate swaps and PPAs; the ticket shows those actions
unavailable with the backend's reason rather than building new operations.
Product-specific actions the backend offers (PPA terminate) appear as their own
actions. (Design call.)

The unsaved-edit guard of the Blotter workbench applies before the ticket shows
another position or closes.

## Table widget

The grid is the existing Tabulator wrapper (`data-grid.ts`, as the Blotter
workbench leaves it) with a new theme and the features the schedule panel needs:

- grouped column headers coloured by side;
- tree rows (Month → Day → Hour; Lift → Pricing day) opening in place, loading
  children on demand;
- a pinned totals row;
- source tags, change markers and the valuation-date line as cell decorations;
- tabular figures, units in headers, right-aligned numbers, dimmed past rows,
  hover highlight;
- the toolbar above (row level, filter, columns, CSV, status).

Other pages may adopt the theme; moving them is not part of this project.

## Products in this project

| Product | Sides | Row shape | Notes |
| --- | --- | --- | --- |
| Physical gas | Physical delivery, fixed payment, fees; independent and linked physical sides | Flow (day) | Both today's templates; fuel links show as derived quantities |
| Financial gas swap | Fixed and floating | Flow (month, day) | Includes scheduled swaps |
| Financial power swap | Fixed and floating | Flow (month, day, hour) | Product / TOU in the common band |
| Heat-rate swap | Power leg and fuel leg, both floating | Flow (month, day, hour) | Fuel quantity derived from the heat rate factor |
| PPA | Physical generation, payment, attributes | Flow (month, day, hour) | Meter and estimate sources; certificate fields coming soon where not admitted |

Each move keeps the product's booking contract and removes its old editor
(no backwards compatibility: there is one entry screen per product, the ticket).

## Current implementation

Physical gas has a description (`physical_gas` v1) and returns a schedule view
from its preview and booked read. It is the one gas entry screen: New trade lists
one Physical gas entry, which opens the ticket, and every gas trade, composed or
single-point, opens in it read-only from the Blotter's Details pane, its pop-out
and the trade window. The composition editor, the single-point template and the
page's separate daily quantity panel are removed (D281); so are the single-point
operations (`gas/preview`, `gas/instructions`, `gas/book`, `gas/status`,
`gas/directory` and `gas/editor`). Stored single-point trades stay readable.

- Coming soon (D279) is declared by the description: a field, a side kind, a side
  column or a tab may carry `soon` hover text. Validation refuses one without it,
  a required soon field, a soon field naming choices or a lookup, a soon side kind
  that opens or admits a side or has fields, columns or a side limit, a soon column
  that takes entries or names a unit, and a soon trade column. A soon column is not
  a schedule view column. The ticket draws each greyed and hatched, disabled and
  `aria-disabled`, with a *Coming soon* tag and the text as its hover; none holds a
  value or is sent, and the booking refuses a soon side or a value for a soon field.
  Physical gas declares the owner's set: on the physical side, swing (daily minimum
  and maximum, swing range as a percentage of the base quantity, swing notice
  deadline), nomination (cycle, nominating party), heat content in Btu per cubic
  foot and quality specification; in the strip, the master agreement's force
  majeure and credit terms, read-only, which no agreement table stores yet; the
  existing floating side (D84, D93) as a soon Add side kind that gas booking does
  not accept yet; a soon Volume column beside the physical quantity; and a soon
  Imbalance tab (nominated against actual, the imbalance, its cash-out price and
  amount). New trade lists Transport, Park and loan and Storage as coming-soon
  instruments, which cannot be opened. They are presentation copy in New trade,
  since no instrument description exists for them.

- The schedule grid (`web/src/schedule-grid.ts`) turns a schedule view into the
  Instrument and side tabs: side-coloured header groups and a Trade group, the
  rows of the chosen level and coarser as a tree, source tags, change markers
  against the previous view, dimmed delivered rows and the view's own totals as
  a pinned row. Exact decimals are shown with digit grouping only; an `a/b`
  ratio is shown exact. Instants are shown in the person's chosen time zone,
  by the formatter the rest of the workbench uses. In delivery the pinned row holds the view's delivered,
  remaining and total lines, in that order.
- The ticket view (`web/src/deal-ticket.ts`) draws the trade strip (state,
  trade fields, delivered count, status and the model's notices), one card per
  side whose common band rows share one row grid, the Add side card, the
  schedule panel and the footer. A side is named by its kind's pill and code;
  no description declares a side name field yet. Below a container width of
  640px the cards stack and side chips select a side. Booked mode shows Amend
  and Close out disabled with the backend's reasons.

- Quantities and amounts carry the side's direction: delivered gas and paid
  cash are negative. Clients show them as reported and never re-sign them or
  take absolute values. Prices are not signed.
- A numeric cell is an exact decimal, or an exact `a/b` ratio when the value
  has no exact decimal at the working scale (a fuel-linked delivery, for
  example). `trade.payment_at` is an ISO-8601 UTC instant. A blank cell means
  the level has no value there; it is never zero.
- When a view cannot be built, the result keeps its valuation and sides, with
  `schedule: null`, `summaries: null` and `scheduleRefusal: {code, message}`;
  for example, physical sides on different gas-day clocks.
- `summaries[].againstMarket` is not defined yet and is always null, so no
  "vs market" line is drawn.
- Units are reference data. A description field or column names the unit it
  is in by kind, never by symbol: `quantity` is the trade's quantity unit. The
  preview and the booked read return `quantityUnit`, the symbol of the one
  quantity specification every side's gas path is quoted in. Sides quoted in
  different specifications refuse with `gas_composition_input`; a
  specification without a unit refuses with `ambiguous_reference`. The ticket
  shows the unit its latest preview resolved beside quantities and in the
  schedule headers, and none before a preview.
- The description reads its side caps from the composition contract
  (`GasCompositionInstrument`): one to four physical sides, and up to four
  cash and fee sides together.
- Current gas limits: no delivered/remaining split once delivery has started
  (a started delivery is refused), one forward month, and fixed-offset gas-day
  clocks.
- The preview and the booked read (`trade`) return `schedule`, `summaries`,
  `scheduleRefusal` and `quantityUnit` at the top level. The composition
  editor read (`gas/composition/editor`) nests them under `valuation`, beside
  `terms`.
- The ticket operations are `POST /api/tickets/description`, `choices`,
  `lookup`, `preview`, `book`, `status` and `trade`. Each runs the gas operation
  it delegates to, with that operation's permission and context handling; booking
  in a historical context refuses with `historical_read_only`.
- A choices or lookup request names a side by its code (`null` for a trade
  field) and a field by its description id. Catalog choices come from the gas
  catalog under the request's context and need their parent values: a zone
  needs its side's pipeline, a point its zone, a usage tariff a delivery-stage
  fee whose source side has a valued path and delivery term. A side answer
  refuses (`ticket_input`) when the draft's `gas_setup` is not the one the
  context resolves; `gas_setup` is empty where the context resolves none.
  Preview and booking are held to the same setup, naming the `gas_setup`
  field: a draft priced under a setup since superseded, or under none where
  one resolves, neither previews nor books, while a retried booking still
  receives its first outcome. A
  setup has no name, so its choice is labelled by its publication order
  (`Published setup <n>`).
- Party choices are `<id>@<revision>`. `market_price` is the single price the
  side's schedule shows at the read's frame, labelled `"<point> · close <date>"`;
  `eligible_tariff` (on `fee_tariff`) proposes the code of the one tariff
  eligible for a delivery-stage fee without a manual price; the booking resolves
  and pins the rate. No lookup fills a price field. A lookup that cannot resolve
  refuses with `ticket_lookup`.
- Party and book choices are trading records read under the request's bound
  context, so a historical context lists the revisions it knows.
- Booking takes working data only: a selected historical context, retained
  knowledge or a captured `contextId` refuses with `historical_read_only`, and
  another convention refuses.
- `trade` opens a booked gas trade by trade, binding and revision as a draft
  (refusing `ticket_input` when that draft does not map back to exactly the
  stored terms), with `schedule`, `summaries`, `scheduleRefusal`,
  `quantityUnit`, `links` and `quantityPolicy` at the top level. Amend and
  close-out are unavailable with a reason; `actions.record` says whether the
  person may record day quantities here, by the record operation's own rule: a
  composed trade, on working data, to a role the trading authority lets amend
  trades, else not allowed with that rule's reason. A composed trade opens as
  the draft that booked it.
- There is one gas ticket. A single-point trade opens as the composition of one
  physical side it is: `physical-1` with its retained path, term, direction,
  quantity and basis; `cash-1`, paid by the side that receives the gas; and,
  with a fee, `fee-1` with its own direction and its price or tariff. The
  single-point profile charges both on the side's contractual delivery
  quantity, so each takes the `delivery` stage, and pays them at its one
  payment instant. Every side is a whole membership of the booked quantity. The
  schedule is built by the composition's own view assembly from the trade's
  retained programs, so it equals the preview of that draft exactly. A fee
  direction without a fee, a fee with both a price and a tariff, or a direction
  other than receive or deliver refuses with `ticket_input`. Single-point trades
  record no daily quantities: `quantityPolicy` is null, `actions.record` is not
  allowed, and their day cells are not entries.
- A refusal of one draft value names it: the error body adds `side` (null for a
  trade field) and `field`. Missing, blank, unknown and non-editable values, a
  decimal field that is not a plain decimal, a quantity against the link rule
  and a source naming no physical side are named this way.
- The ticket controller (`web/src/ticket-controller.ts`) opens a new ticket with
  each side kind's initial count from the description: one physical and one
  payment side for gas. After an edit it reads
  the choices of the fields that depend on it and of the lists that refused;
  adding or removing a side, or editing a trade field, reads every list.
  Choice and lookup reads share a four-request concurrency limit per ticket.
  New reads replace queued reads for the same field; removed sides and closed
  tickets start no queued work. Responses must still belong to the current
  field request or lookup generation, including when edits return to earlier
  terms. Skipped reads are not backend refusals. A
  side is coded `<kind>-<n>` with the lowest number not yet used in the
  ticket, so a removed side's code is never given to a later side; removing a
  side clears every choice that named it, and that choice's dependents, each
  with a notice. Once
  edits settle (300 ms) it previews the whole draft and reads every lookup
  again; a lookup that fills a term previews the draft in turn. Only the latest
  preview is drawn. A refused preview keeps the last schedule, marked stale,
  with the reason on the field the refusal names; lookup and choice refusals
  show on their fields. A side added since the last preview is left out of the
  grid until a preview holds it. Book is taken only for the draft last previewed
  without refusal, and sends `tickets/book` as a retained request: an unknown
  outcome locks the ticket with Check and Retry, and survives a reload, where
  the Blotter offers to reconcile it.
- A booked trade's schedule shows each physical day's quantity under the
  operations projection (`operational_projection_v1`, returned as
  `quantityPolicy`): the day's latest allocated, scheduled, confirmed or
  nominated record, tagged with its kind, else the contract's. Amounts stay the
  contract's; a recorded quantity does not revalue the trade. The amount's
  sources are the evaluated contract's (`contract` with its price's), so on a
  recorded day the grid tags the quantity with the record's kind and the amount
  as Contract, and quantity times price need not equal the amount.
- A side column's `input` lists the modes in which its day cells are entries.
  Gas declares the physical `quantity` column an entry in booked mode: in a
  booked physical side's tab of a trade whose `actions.record` is allowed, each
  day quantity is an input. An input opens empty, the drawn quantity its
  placeholder, since the record takes a plain unsigned decimal. Changing one
  opens a record form for that day, whose kind, status and evidence the person
  chooses (none is chosen for them). Record reads `gas/quantities/read` for the
  trade revision and the day's previous record of that kind, and sends
  `gas/quantities/record` with its own operation id, kept until its outcome is
  known: an unknown outcome offers Check (`gas/quantities/status`) and Retry of
  the same command. The backend refuses what it does not admit, such as a
  negative quantity or a historical context. Once recorded the ticket reads the
  trade again and draws its schedule; the cells that moved are marked.
- The preview and the booked read return `links`: for each linked physical
  side its source, target, formula, fraction and the receipt, delivery and fuel
  totals as exact text. The Quantity links tab draws them.
- The controller draws the ticket again only when what it shows has changed, so
  an answer that changes nothing keeps the person's place in the schedule.
  Late labels for booked fields update in place, preserving a daily quantity
  input's value and focus. The read-only Quantity links grid still redraws when
  its formula labels change.
  The page's busy sweep, which disables and re-enables every control, leaves the
  ticket's controls as the ticket drew them (`data-own-controls`), so a read-only,
  refused or coming-soon control never becomes enabled.
- Not yet: tree rows load with their view rather than on demand; a quantity
  command awaiting its outcome is kept while the page is open, not across a
  reload; the billing selection and the revision history of day quantities are
  not drawn; the trade date
  lookup is declared but not served; a usage tariff list read earlier is not
  read again when its source side's path changes (the booking still checks it).

## Dependencies and order

1. The [Blotter workbench](blotter-workbench.md) merges first. The ticket builds
   on its grid wrapper and appears in its Details pane.
2. The shared trading workflow admits physical gas (pull request 152, "Unify
   physical gas and existing instruments in the shared trading workflow")
   before the physical gas move, so booked gas trades open from the Blotter
   like every other product. The gas move is planned against that workflow.
3. Products whose sides are moving to the D253 model describe the model that is
   implemented when they move. Where D253 is not yet qualified for a product,
   the description follows that product's current booking contract, and
   changes when D253 lands.
4. Before each product moves, the owner supplies its coming-soon field
   inventory: the fields the product needs beyond what today's screens
   capture, each with its business meaning and hover text, written in this
   project's own words. Physical gas starts from
   [physical gas terms](physical-gas-field-contract.md). A product with no
   inventory yet moves with only its supported fields.
5. Build order: the description contract and table widget; then physical gas
   (the richest current product); then swaps and heat rates; then PPAs.

## Verification

- Behaviour tests first for each piece (TDD): description validity, field
  states, dependent drop-down clearing, lookup chips and their removal on edit,
  coming-soon omission from requests, late preview responses discarded, and
  each product's booking unchanged through the ticket.
- A check that each description agrees with its booking contract.
- Browser checks for each product: enter, preview, book, reopen, and each mode
  the product offers; wide and narrow panes.
- Independent adversarial review before publication (temporal context on
  lookups and drop-downs, async preview ordering).

## Left to the builder

- The description's schema, transport and versioning.
- Colours for side kinds within the existing theme tokens.
- Debounce timing for live preview.
- Narrow and wide breakpoints of the container.
- How many rows of a level load at a time.

## Decisions to record

Recorded as D278 (deal ticket), D279 (field states), D280 (schedule panel) and
D281 (one gas ticket).

- **Deal ticket.** One description-driven ticket draws every sided product for
  new, booked, amend and close-out; product editors are removed as products
  move.
- **Field states.** Editable, looked up, derived, information and coming soon,
  with the rules above; coming-soon fields are never sent.
- **Schedule panel.** Joint Instrument schedule first, a tab per side, flow and
  cargo row shapes, backend-reported value sources.
