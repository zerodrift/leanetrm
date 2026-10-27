# Delta ladder on the desk

Status: implemented in the local preview (D230). Presentation only: the ladder
shows values the risk report already returns. Totals across books, a choice of
curve level and quantity-equivalent units remain work (see
[Remaining work](#remaining-work)).

## Why

A risk run shocks every held contract (or curve, or custom target), and the
server returns, for each shocked factor, its result totalled by book and by
valuation measure (`factors[].totals`). Before the ladder, the desk showed only
results per position: the Risk lens, each position's Risk tab, the change chart
and the risk tile. A trader could not answer *where along the curve am I exposed,
and in which book?* without opening positions one by one or reading the JSON
download.

## What a run returns (measured)

A "Shock every held contract" run at the Dec 18, 2020 close of the standard
preview, ±0.05 per price unit:

- 68 factors in 5 parts: 66 contract quotes (32 gas, 34 power) and 2 computed
  outputs (gas forwards, hub power forwards).
- Each factor has 4 totals: Book A futures execution P&L, Book B futures
  execution P&L, Book B swap PV, Book B PPA value excluding overdue cash, with
  explicit zeros.
- Each total carries base/up/down values, up/down P&L, delta and gamma, the
  currency and the delta unit (`USD / (1 USD / MMBtu (IT))`).
- Contract quotes carry structured delivery dates and curves. Computed outputs
  carry curves but no delivery dates.

Two findings shape the design:

1. **Rows are separate shocks, not additive buckets.** Gas futures Jan 2021 (an
   input quote) and gas forwards Jan 2021 (a computed output built from it) both
   show a Book B swap delta of 31,753,799.37: one exposure seen at two levels of
   the curve graph. Adding rows would double count.
2. **Cross-curve exposure is visible.** Most of that delta is the four hub power
   swaps, whose curve is built from gas futures. The gas futures row's Contract
   panel lists them (6,950,518.61 each) beside the four gas swaps (992,931.23
   each) and the heat-rate swaps (−7,000 each).

## Behaviour

### Where it lives

After a run whose results are shown on the desk, the positions lens becomes
**Values | Risk | Ladder**. Ladder replaces the trade rows with the ladder grid
and shows a **Delta | Up P&L | Down P&L** choice beside the lens. The heading
reads "Delta ladder · grouped by curve" and the trade rows' Group by control is
hidden. Search and column filters work on the ladder; the trade rows' filters,
saved layout and selection are left untouched. Clearing or replacing the run
returns the desk to Values.

### Rows

- One row per shocked factor, grouped by the factor's curve (named from the
  workspace's curve names; a curve with no name refuses the ladder). Groups
  follow the order of their first factor in the run; a group header says how
  many shocks it holds, each on its own, and carries no subtotal.
- A contract quote is named by its delivery period ("Jan 2021", "Jan 7, 2021").
  A factor without delivery dates is named by the server's label, never a parsed
  one.
- Coordinate reads **Quote** or **Computed point**; Shock reads "±0.05 1 USD /
  MMBtu (IT)".

### Columns and cells

- One column per (book, measure, currency) the run returns, in the order first
  returned, titled "Book B · Present value · USD". Measures, books and
  currencies are never combined.
- A cell shows the chosen value of that total through the backend amount
  formatter, in the desk's precision (delta as the lens shows deltas, P&L as
  money). Its tooltip gives the exact amount and unit and the up and down P&L.
- A zero is shown, muted. A total the run does not return for a factor reads
  "Not reported", never zero. A non-zero gamma marks the cell **nonlinear**.

### Contract panel

A ladder row opens the side panel's **Contract** mode: the factor's curve,
period, coordinate and shock, and the positions its shock moves under the chosen
value, from the same report, largest magnitude first (compared exactly), each in
its position's own display format. Each opens the position. The panel says how
many held positions the shock does not move. If the run on screen no longer
holds the factor, the panel closes and says so.

### Export

With the Ladder lens on, the Blotter's **Export** writes
`leanetrm-delta-ladder.csv`: the read's metadata, the run bar's title and
detail, then one row per factor the filters show with its curve, contract,
coordinate, shock, price unit, the value shown and each column's exact amount
("Not reported" where the run returns none).

### What it never does

- No totals across rows, books, measures or currencies, and no group subtotals
  ([blotter-desk.md](blotter-desk.md#anti-goals)).
- No unit conversion or browser rounding.
- No inferred rows or zeros: parts without results are named by the run bar, and
  positions a close cannot value (D220) are noted as before.

## Remaining work

- **One curve level.** "Shock every held contract" shocks quotes and computed
  points together. A ladder at one level (quotes only, or computed points only)
  needs the report setup to offer that choice.
- **Totals across selected books.** Needs a server-computed, qualified total per
  factor and measure.
- **Quantity-equivalent units** ("MMBtu equivalent"). Needs the server to state
  the conversion; delta is discounted, so it is not a plain quantity.
- Ladders for pivot views, which have no lens.

## Verification

- `web/tests/delta-ladder.test.mjs`: rows, grouping and labels; columns per
  (book, measure, currency) with explicit zeros; "Not reported" for missing
  totals; nonlinear marks; refusal of duplicate factors, duplicate totals and
  unnamed curves; the export; the Contract panel's positions.
- `web/tests/desk-panel.test.mjs`: the Contract mode's identity and label.
- `tests/browser/risk.mjs`: after a held-contract run, the Ladder lens has a row
  per factor and a column per (book, measure, currency); the first factor's
  cells are the server's deltas and, with Up P&L, its up P&L; its row opens the
  Contract panel listing each position the shock moves; the Risk lens returns
  the trade rows.

## Decision

D230 in [decisions.md](decisions.md).
