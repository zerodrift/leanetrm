# Native curve definitions

Status: D318, owner-directed design (2026-11-04), extended by D358 (2026-11-06), D359 (2026-11-07) and D362 (2026-11-08).
Curves & prices → Curve definitions validates, previews, saves, publishes and
edits definitions locally, including interest-rate curves. Published
interest-rate definitions are admitted into market revisions and priced from
(D359), and published futures and forward definitions are admitted into market
revisions at a named close (D362). Examples are synthetic.

## Intent

A curve is authored from the chain the schedule page already builds:

```text
schedule (ordered dates) -> date mapping (symbolic positions) -> curve (its contract window)
```

A schedule decides which date comes next. A date mapping refers to those dates
symbolically: with context-month selection `oe.ng[1]` is the next occurrence
after the context date, so a mapping's contracts move forward as the context
date moves. A curve names one exact published mapping version and how many of
its contracts it carries. Its contracts therefore always roll; nobody extends a
curve by hand. Publishers and importers are responsible for supplying the marks
of every contract in the window at each close.

## What a curve definition holds

Every field is explicit; none has a default.

| Field | Meaning |
| --- | --- |
| Code, name | Stable identifier and display name |
| Commodity, location | Reference-data codes; the pair must be a declared commodity location |
| Quote | Quote specification: price currency, quantity unit and multiplier |
| Delivery profile | Delivery clock (time zone), calendar and interval length |
| Contract kind | `future` (with an exact futures product) or `forward` |
| Contract window | Exact published date-mapping version and contract count |
| Settles to | A reference settlement target |
| Price rule | `direct`: one mark source and role per contract |
| Missing price | What a missing contract price does |
| Interpolation | How prices between contracts are formed |

### Ownership and matching

The contract owns what it is: commodity, location, delivery period, expiry and,
for a future, its exchange series. The curve owns how it is priced: quote,
settlement, price rule, missing-price policy and interpolation. Where both name
the same term the values must be equal or the definition is refused:

- a futures product's commodity, location, quote currency, quote unit and quote
  multiplier must equal the curve's commodity, location and quote terms;
- each futures contract in the window must match exactly one series of the
  curve's futures product with the same delivery start and exclusive end, and
  last trade at that series' known last-trading instant;
- a placeholder futures product is not executable and is refused;
- an index target's commodity, location, currency, quote unit and multiplier must
  equal the curve's.

Matching never searches nearby dates, other products or other indices; missing
or ambiguous matches fail by name.

### Settlement by business case

| Case | Settlement target kind | Status |
| --- | --- | --- |
| Futures | `future_series`: the exchange's own settlement price | Supported |
| OTC physical or financial gas | `index`: one published price index | Supported |
| Index spread (basis) | `index_difference` | Coming soon |
| Power forwards settling to hourly physical prices (nodal LMP) | Hourly settlement source | Coming soon |
| Forwards settling to a futures final settlement | `future_series` on a forward | Coming soon |

A future must settle to `future_series` and a forward to `index`. Unsupported
cases are shown in the editor as disabled **Coming soon** choices and refused by
the server; they never fall back to a supported case.

### Policies

| Policy | Supported | Coming soon |
| --- | --- | --- |
| Price rule | `direct` (trader mark or daily settlement from one source) | Formula contracts |
| Missing price | `reject`: the read names the contract and fails | Carry forward, interpolate |
| Interpolation | `none`: only contract prices exist | Linear between contracts |

## Contract window resolution

The preview resolves the mapping version in an explicit retained context and
takes its first *count* contracts as positions 1..count. Each position keeps its
label, delivery start, exclusive delivery end, last-trading instant, futures
series (for futures) and the date receipts of its source schedule references.
Positions must be in strictly increasing, non-overlapping delivery order. A
count larger than the mapping's contracts is refused. The same definition
resolved at a later context date gives the later contracts.

The mapping's own date rules decide when the window steps forward; the curve
adds no roll rule. A preview names the close selected in the page header and
reads its reference terms and resolves its mapping in that close's one exact
context: current knowledge captured on the close's date, or the session's
selected historical context, whose effective date must be the close's
(`market_date` otherwise). Choosing another close in the header clears the
shown window; previewing again rolls it.

## Interest-rate curves (D358, D359)

An interest-rate curve names its currency, mark unit (percent or decimal per
year), day count, compounding, interpolation and extrapolation, a mark source and
an exact published version of a date-list mapping (D357). Each term is required
and none is defaulted; later conventions are shown as Coming soon and refused.
Every label of the list is one tenor, such as `1D → oe.days + 1bd` or
`4Y → oe.days[1461]`, so the curve's maturities roll with the context date
instead of being stored as dates. Contract terms (commodity, location, quote,
delivery profile, futures product, settlement target, price rule, mark role and
missing price) do not apply and are refused.

A preview resolves the list at the header's selected close. `t_0` is that close's
date, which must equal its valuation UTC date; otherwise the preview is refused
(`rate_context`). Each tenor shows its maturity and day count from `t_0`; a
maturity on or before `t_0` is refused by label (`rate_maturity`). The preview
shows no rates: a tenor's marks belong to the series its admission creates.
Bootstrapping from par or swap quotes is not part of this design.

## Rate curves at a close (D359)

Admitting a published interest-rate definition into a draft definition revision
(`POST /api/curve-definitions/admit`, `WriteMarketDefinitions`, idempotent by
request id) writes the curve with its terms and one series per tenor label; a
label keeps its series across revisions and definition versions. At each close
the curve resolves its pinned date list from `t_0`, takes each tenor's mark, and
gives each payment date a discount factor: exactly 1 at `t_0`, interpolated from
`t_0` to the first pillar and between pillars, and extrapolated after the last
pillar only when the definition chooses to. A currency's discount default names
the curve. Curves & prices shows each tenor's maturity, mark and discount factor
in the curve's own unit, and links to the definition; nothing on the rendered
curve changes it. Rate curves are not risk factors.

## Contract curves in a revision (D362)

Admitting a published futures or forward definition into a draft definition revision
(`POST /api/curve-definitions/admit-contracts`, `WriteMarketDefinitions`, idempotent by
request id) names the close whose window it takes, at current knowledge on that close's
date. Each position becomes a contract and price series through the verified date-mapping
admission (D283); the curve records the exact definition version and its direct mark
source and role. Closes and pricing read it as any direct curve, from its members' marks.

A revision holds the window of the close named when it was admitted, so a later close's
window is admitted into a new draft revision; a future keeps its identity by exchange
series and a forward by curve code, delivery period and last-trading instant. Marks are
recorded against that identity in the revision that uses it. Date fields must be positions in saved date lists, absolute or
context-relative; generated schedules and business-day offsets are refused by expression.

## Saving, publishing and editing (D358)

A definition has a stable code and up to 64 versions: at most one draft, then
immutable published versions. Save requires `WriteMarketDefinitions`, publish
`SealMarketDefinitions`. Each command carries a request id; a retry with the same
payload returns its first outcome and a different payload is refused. Saving
validates exactly as Preview does, at current knowledge on the selected close's
date, and records that context; historical views cannot author. A version pins
its reference revision with exact foreign keys and its mapping by version and
generation. Publication is certified by its own knowledge receipt, so an earlier
knowledge view does not see it.

The editor holds everything that defines a curve. Its contracts or tenors are its
mapping's rows and are edited in place; saving the curve saves edited rows as the
mapping's draft and pins it. Publishing publishes that mapping draft first, in the
same transaction: if the curve's publication fails, neither is published, and a
mapping draft changed in Settings after the curve was saved makes the curve's
pinned generation stale. A newer published mapping version is offered, never
adopted silently. Rendered curves offer no way to change a definition.

## Implemented increment

- `GET /api/curve-definitions/catalog` (`ReadMarketData`): reference choices from
  the pinned reference revision, published date mappings and the policy choices,
  each marked supported or Coming soon.
- `POST /api/curve-definitions/preview` (`ReadMarketData`): takes a complete
  definition and the header's selected close (`snapshotId`), validates the
  definition against those terms and returns its resolved window, or a
  structured refusal (`curve_definition`, `curve_reference`,
  `curve_terms_mismatch`, `curve_unsupported`, `curve_contract_count`,
  `curve_contract_series`, `curve_contract_order`).
- **Curves & prices → Curve definitions**: lists saved definitions and edits one
  in place of the market board or a curve. Schedules and date mappings stay on
  Settings → Schedules & date mappings.
- A configuration action appears beside every listed native curve, on its market
  card and in its details. The navigation read (`curveConfigurations`) carries the
  exact admitted definition when available, whether it is editable and the reason
  when it is read-only. Readers and historical views can inspect that definition.
  Seeded native curves without an admitted definition show their exact retained
  terms and members; their immutable financial terms are not editable here. An
  authorized author can start a separate derived graph using that source, with
  its own identity and explicitly selected output operations. Saved synthetic
  curves have Edit graph or Inspect graph actions in the Curve builder library.
- D358: `GET /api/curve-definitions`, `POST /api/curve-definitions/save` and
  `POST /api/curve-definitions/publish`; a preview or save may carry the curve's
  edited mapping rows. Migration `0214_curve_definitions.sql` adds the versions,
  request ledger and knowledge receipts.
- D359: `POST /api/curve-definitions/admit`; migration `0215_rolling_rate_curves.sql`
  gives definitions their own rate terms, adds admitted rate curves and tenors,
  removes literal-maturity rate curves and the discount default's interpolation
  column.
- D362: `POST /api/curve-definitions/admit-contracts`; migration
  `0217_admitted_contract_curves.sql` pins a curve's definition version, adds the
  request ledger, checks context-relative date lists and checks admitted terms at sealing.

## Remaining work

1. Admitting contract windows from generated schedules or business-day offsets,
   and one revision serving closes across a roll.
2. The Coming soon cases above, each with its own consumer and checks.
3. Not yet compared: the delivery profile's calendar and clock against a futures
   product's delivery calendar and market clock; the mark role and source
   against futures execution policies (a forward may currently name a daily
   settlement role).
4. The catalog and preview read every visible reference table; a targeted read
   of the terms they use would be smaller.
