# Rolling date lists and a curve editor that saves

Status: approved 2026-11-06 and implemented locally as D356 and D357; see
[curve definitions](../../curve-definitions.md) and
[schedules](../../schedules-and-contract-sequences.md). Examples are synthetic.

This is the first of four related projects. It covers projects 1 and 2:

1. **Date foundations**: a generated daily schedule and a generic date-list mapping.
2. **Curve editor that saves**: durable curve-definition drafts and published
   versions for contract curves and for a new interest-rate curve kind.
3. *(Later)* **Runtime resolution**: published definitions drive closes, the market
   board, valuation and risk.
4. *(Later)* **Cleanup and migration**: existing curves become published
   definitions; the literal-maturity rate path is removed.

Projects 3 and 4 get their own designs. They are described here only where this
design must leave room for them.

## Why

A rate curve today stores literal maturity dates
(`market_data.rate_curve_points.maturity`), and the rate engine refuses any curve
with a maturity on or before the valuation date
([ZeroRateCurve.cs](../../../src/OpenEtrm.Rates/ZeroRateCurve.cs)). To keep two
years of daily rate history usable, the hosted demo carries 522 curves, one per
business day (`usd_zero_20241104` … `usd_zero_20261103`), each with a next-day and a
four-year maturity. At any close only that day's curve builds; the market board
lists all 522 and selecting an old one shows "Rate maturity must follow the
selected valuation date."

Commodity curves have the same shape problem: their contracts are literal dated
rows in a market revision. D318 already designed the fix for those (schedule →
date mapping → curve, resolved against the context date) and built a read-only
preview, but nothing can be saved, and there is no way to express a rate maturity
such as "next business day" or "four years out" against the context date.

The owner's intent: dates are symbolic and resolved at runtime from the user's
context date. The as-of date picked in the page header is `t_0` and is the
valuation date. A user should be able to create and edit a curve's tenors or
contracts, and the date machinery should be usable by anything, not only rates.

## Goals

1. **Daily schedule rule.** A published schedule whose dates are every calendar day,
   or every open day of one pinned business calendar, generated in memory. It
   always uses `on_or_after` selection, so `oe.days[0]` is the first date on or
   after the context date and `oe.days[n]` is the nth after it.
2. **Date-list mapping.** A new date-mapping type on Settings → Schedules & date
   mappings whose rows are a label and one date expression, such as
   `1D → oe.days + 1bd` and `4Y → oe.days[1461]`. Generic: any consumer can name a
   published version. Interest-rate curves are its first consumer.
3. **Interest-rate curve kind in Define a curve.** Its terms are currency,
   interest-rate specification, mark source and an exact published date-list
   mapping version. Previewed at the header's close: each label's resolved
   maturity date. Rates and discount factors per label arrive with project 3.
4. **Curves can be saved and edited.** Drafts, immutable published versions and
   edits as new drafts, for both contract curves (D318) and interest-rate curves,
   following the contract-date library's request identities, generations and
   certified knowledge receipts (D167, D238).

## Non-goals

- Bootstrapped or par-instrument curves (deposits, futures, overnight-index swaps),
  a solver, or new interpolation. Rates stay marked spot zero rates under the
  existing simple ACT/365F convention. Listed in `docs/plan.md` as possible future
  work with no current consumer.
- A `cd` suffix. On a calendar-day schedule `oe.days[n]` already means `t_0 + n`
  calendar days; `±Nbd` stays the only offset.
- Month or year arithmetic (`+1M`, `+4Y`). Four years is written as a calendar-day
  index.
- Admitting published definitions into market revisions, or pricing from them
  (project 3). The board, closes, valuation and risk keep reading today's stored
  curves until then.
- Changing or removing existing curves, including the hosted demo's 522 rate
  curves (project 4).

## Anti-goals

- A date expression that executes code. Expressions stay data references
  ([DateReference.cs](../../../src/OpenEtrm.Shared/Temporal/DateReference.cs)).
- Any default: a missing calendar, coverage, mapping version, specification or
  source is a structured error, never a substituted value.
- Silently rolling a calendar day onto a business day. Calendar days are calendar
  days.
- Silently dropping a maturity that resolves on or before `t_0`.
- Storing generated daily dates as rows.
- A parallel persistence or publication framework next to the existing schedule
  and contract-date libraries.

## Decided behaviour

### Daily schedule rule

- A new schedule engine version, `date_schedule_v4`, with definition
  `{kind: "daily", days: "calendar" | "business", firstDate, lastDate}`.
- `days: "business"` requires an exact bound calendar version and generates that
  calendar's open days. `days: "calendar"` generates every day; it may bind a
  calendar, which is then used only by `±Nbd` offsets in expressions. With no bound
  calendar, a `±Nbd` offset on it fails with `calendar_reference`.
- Selection is always `on_or_after`; other selections are refused for this rule.
- `firstDate` and `lastDate` are explicit and inclusive. Resolution beyond them
  fails with `date_index` (the existing out-of-schedule error). A business-day rule
  also fails where its calendar has no coverage. Maximum span: 100 years.
- Dates are generated in memory when a resolver first needs the schedule, never
  stored, and then selected by the existing `on_or_after` resolver. Identical
  inputs give identical dates. (Planning refinement: reusing the existing resolver
  instead of separate index arithmetic; 100 years is about 36,500 dates.)
- Published, versioned and immutable like every other schedule. Saving, publishing,
  effective intervals, knowledge views and receipts are unchanged.

### Date-list mapping

- A mapping type alongside contract mappings, on the same Settings page, with the
  same draft/publish/edit flow, request identities, generations, validation
  context and certified knowledge receipts.
- Each row: a label (1 to 80 characters, unique within the version) and one date
  field. The field is a literal date or a schedule reference with an occurrence
  and an optional business-day offset, stored exactly as contract-date fields are
  stored today (pinned schedule version, occurrence, business days).
- At most 120 rows.
- Resolving a published version in a context returns each row's label, resolved
  date and date receipt. Resolved dates must be strictly increasing in row order;
  otherwise the read fails with `date_list_order`, naming both rows.
- The mapping says nothing about what its dates mean. A consumer applies its own
  rules (a rate curve requires every date after `t_0`).

### Interest-rate curve kind

- Define a curve offers a third kind, **Interest rate curve**, alongside Exchange
  futures and OTC forward.
- Its terms: code, name, interest-rate specification (an exact
  `interest_rate_definitions` row; currency, day count, compounding and
  implementation come from it and must be ones the rate engine supports), mark
  source, and an exact published date-list mapping version. All are required; none
  is defaulted. The currency is shown from the specification, not entered
  separately (author's call: a second currency field could only disagree).
- Commodity, location, quote, delivery profile, futures product, settlement
  target, price rule, mark role, missing-price policy and interpolation do not
  apply. The editor hides them and the server refuses a rate definition that
  supplies any of them.
- Each mapping label is one rate tenor. Marks attach to the label, so "4Y" is one
  series observed at every close. This is where rates intentionally differ from
  contract curves, whose marks attach to dated contracts.
- The preview resolves the mapping at the header's close in that close's exact
  context, exactly as D318's contract preview does. `t_0` is that close's date,
  which is the mapping's context date and the rate engine's valuation UTC date; if
  the two ever differ the preview fails rather than choosing one. It shows, per
  label, the resolved date, its day count from `t_0` and its date receipt. A
  resolved date on or before `t_0` fails with `rate_maturity`, naming the label.
- Rates and discount factors are not shown in this project (author's call). A
  label's rate series is created when a published definition is admitted into a
  market revision (project 3); until then no observation is linked to a label, and
  the preview does not guess one from existing series.
- A published definition appears with the existing `interest_rate` curve kind, so
  the board's Rates tab needs no new kind.

### Saving and editing curves

- Applies to all three kinds. A definition has a stable code; versions are drafts or
  immutable published versions; editing a published version creates a new draft
  under the same code; at most one draft per code.
- Save requires `WriteMarketDefinitions`; publish requires
  `SealMarketDefinitions`; reads require `ReadMarketData`. UI and agents use the
  same operations.
- Save and publish carry a request id. A retry with the same id and payload returns
  the first outcome; a different payload under the same id is refused. Stale
  generations are refused.
- Saving validates the definition exactly as Preview does, at the header's close,
  and records that context as its validation context. Publishing records a
  certified knowledge receipt; earlier knowledge views cannot see later versions.
- A saved definition pins exact mapping versions. A newer mapping version does not
  rebind it; the user edits the curve to choose the newer version.

### Curve definition view

- Owner-directed: a curve is changed only through its definition, never in the
  rendered curve. Rendered curves (the market board, a curve's page, closes) may be
  generated at runtime and consumed downstream and in production, so they stay
  read-only and offer only a link to the curve's definition.
- **Curves & prices → Curve definitions** replaces today's "Define a curve" panel.
  It lists definitions (drafts to authors and publishers, published versions to
  readers) and opens one in an editor holding everything that defines the curve:
  its terms and its tenors or contracts.
- Tenors and contracts are edited in place in that editor: add, remove, relabel or
  change a row's date expression. The rows still belong to the curve's mapping, so
  saving the curve draft also saves those rows as a draft of its mapping, and the
  curve draft pins it. Publishing the curve publishes that mapping version first,
  in the same operation and with the same request identity; either both publish
  or neither does.
- Other curves pinned to earlier mapping versions are unaffected. The same mapping
  remains visible and editable in Settings → Schedules & date mappings for other
  consumers; if it changed there, the curve editor offers to move the curve's
  draft to the newer published version.
- Published versions never change. Editing a published curve starts a new draft;
  the published version keeps serving until the new one is published.

## Constraints

- `AGENTS.md` applies in full: TDD, no silent defaults, idempotent writes,
  historical context on every read, exact-version references, and adversarial
  review for temporal and numerical behaviour.
- Pricing code stays in the managed backend; the editor is presentation only.
- New tables or columns only where this design names their consumer.
- Migrations follow the hosted schema-window process; nothing here is deployed by
  the design.

## Left to the builder

- Storage shape for date-list mappings: extending the contract-date tables with a
  mapping kind or adding sibling tables, provided save, publish, request, receipt
  and guard behaviour is shared rather than duplicated.
- Storage for curve definitions, following the contract-date library's pattern.
- Exact API routes and payload field names.
- Editor layout, within the house rules: one field per row, stacked sections,
  disabled "Coming soon" for unhooked choices.
- Error codes beyond those named above, following existing naming.

## Verification

- Daily rule: calendar and business days; weekends and holidays; `[0]` on a
  closed day; offsets with and without a bound calendar; both coverage edges;
  identical output for identical inputs.
- Date list: order violations, duplicate labels, literal and referenced rows,
  resolution under historical knowledge views.
- Rate preview: dates after `t_0`, a date equal to `t_0`, the same definition
  rolling between two closes, unsupported specifications, and refusal of
  contract-only terms.
- Save and publish: retries, stale generations, permissions, knowledge cutoffs and
  pinned mapping versions; contract curves round-trip through save and publish; a
  curve publish that fails leaves its mapping draft unpublished; another curve on
  the earlier mapping version is unchanged.
- Rendered curve views expose no edit operation.
- Browser checks for the Settings mapping editor and Define a curve.
- Independent adversarial review before publication.

## Decision record

Implementation adds decisions for the daily rule and date-list mapping, and for
saved curve definitions with the interest-rate kind, and updates D318's remaining
work.
