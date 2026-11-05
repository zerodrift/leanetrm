# Native curve definitions

Status: D318, owner-directed design (2026-11-04). The first increment, a curve
editor that validates a definition and previews its rolling contract window, is
implemented locally. Saving, publishing and pricing from native definitions
remain work, listed at the end. Examples are synthetic.

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
- **Curves & prices → Define a curve**: the editor takes the place of the
  market board or a curve and previews at the header's selected close. Schedules
  and date mappings stay on Settings → Schedules & date mappings. It previews
  only; it does not save.

No table, migration or dependency is added. The preview is read-only.

## Remaining work

1. Durable drafts and immutable published curve-definition versions with
   request identities, generations and certified knowledge receipts, following
   the contract-date library (D167, D238).
2. Admitting a published definition into a draft market revision: materialize
   the window's contracts (extending D283's admission beyond absolute indexes)
   and create the curve, its members and its direct source, so closes and
   pricing consume it.
3. The Coming soon cases above, each with its own consumer and checks.
4. Not yet compared: the delivery profile's calendar and clock against a futures
   product's delivery calendar and market clock; the mark role and source
   against futures execution policies (a forward may currently name a daily
   settlement role).
5. The catalog and preview read every visible reference table; a targeted read
   of the terms they use would be smaller.
