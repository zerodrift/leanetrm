# Rolling rate curves from published definitions

Status: draft for approval, 2026-11-07. Projects 3 and 4 of
[rolling date lists and a curve editor](2026-11-06-rolling-date-lists-and-curve-editor-design.md)
(D357, D358), narrowed to interest-rate curves. Examples are synthetic.

## Why

D358 lets an author define, save and publish an interest-rate curve whose tenors
are labels of a date list that resolves from the context date `t_0`. Nothing uses
those definitions yet. Every consumer still reads literal-maturity rate curves
(`market_data.rate_curves` plus `rate_curve_points`). Their dated pillars stop
working after one day. The hosted demo therefore carries one curve per business
day (522 `usd_zero_YYYYMMDD` curves) and one USD discount default per day pointing
at that day's curve. Its stored zero rates are all 0, so every discount factor has
been 1.

A yield curve is not a contract curve. Its benchmark tenors are inputs, called
pillars. Consumers ask it for a discount factor on an arbitrary date, which comes
from interpolating between the pillars on either side. The pillars must roll with
`t_0`, while a tenor ("4Y") stays the same benchmark from close to close.

## Goals

1. **A published interest-rate definition is the curve.**
   - Admitting a published definition version into a market definition revision
     creates one stable price series per tenor label.
   - At a close, each label resolves to a pillar date from that close's `t_0`, and
     that close's mark for the label's series is its zero rate.
2. **Interpolation is part of the definition.**
   - An interest-rate definition names its interpolation policy explicitly.
   - The one supported policy is today's log-discount policy
     (`log_discount_geometric_root_to_even_18_v1`), with its existing maximum
     pillar span.
   - No default: a definition without a policy is refused.
3. **Payment discounting is the purpose (owner-directed).**
   - The curve exists to look up discount factors on payment dates, through the
     currency discount default.
   - The market board and the curve page show it only for inspection: each label's
     resolved maturity, zero rate and discount factor at the selected close.
   - The curve page offers a link to the curve's definition and no edit control.

4. **The literal-maturity rate path is removed.**
   - Removed: `rate_curve_points`, the code that builds pillars from stored dates,
     and every curve, default and fixture that uses them.
   - No compatibility shim (owner's standing preference).
5. **The hosted demo has one USD curve with plausible history.** This is done by
   a private seed script and is not part of the repository (owner-directed).

## Non-goals

- **Bootstrapping.** Constructing the zero curve from market instruments
  (overnight fixings, futures, OIS swaps) is not in scope; tenors are marked
  directly as spot zero rates. A bootstrapped curve would take its instrument
  definitions from the same configuration screen; that is future work in
  `docs/plan.md`.
- **Interest-rate risk factors.** Rate curves stay out of VaR and scenario risk,
  as they are today: risk does not shock them. The tenors are arbitrary
  configuration, not market instruments, so their history is not a risk-factor
  history (owner's rationale). If rates enter risk later, the factors will be the
  market instruments.
- Contract-curve admission (D318 futures and forward definitions). This design
  covers rate curves only.
- Interpolation policies other than the existing log-discount policy.

## Anti-goals

- Any default: a missing definition, policy, mapping version, tenor series, or
  mark at a close is a structured error naming the missing item. Never skip a
  tenor or substitute another curve.
- Extrapolation before `t_0` or beyond the last pillar.
- A tenor's mark attached to a date. Marks belong to the tenor's series; the date
  is resolved at each close.
- Rebinding a curve to a newer definition or mapping version without a new
  admission.

## Decided behaviour

### Definition

- D358's interest-rate definition gains `interpolation`. It is required, and its
  only allowed value is the log-discount policy above.
- The editor shows it as a required select. Draft, save, publish and edit
  semantics are unchanged.
- Already-published D358 versions have no policy and cannot be admitted. The
  author edits them and publishes a new version. There are none in production, so
  no migration of versions is needed.

### Admission

- **Operation.** Admitting an exact published interest-rate definition version
  into a draft market definition revision is an authorized write. It requires
  `WriteMarketDefinitions`, is idempotent by request id (D238), and follows
  contract materialization.
- **Rows it creates.** The revision gets one rate curve row pinned to the exact
  definition version, and one tenor row per label: label, ordinal and a newly
  created series. Publishing the revision uses the existing revision publication.
- **Tenor series.**
  - A tenor series belongs to its curve within the revision.
  - Re-admitting the same definition version into a later revision of the same
    catalog reuses the same series identities, so a tenor's mark history
    continues.
  - A new definition version that keeps a label keeps that label's series; a new
    label gets a new series; a removed label's series is no longer read.
  - Labels are matched exactly, by text.
- The admitted curve's code is the definition's code. One curve per code per
  revision, as today.

### Building at a close

- For a close at `t_0`, the curve resolves its pinned date-list version in that
  close's exact context (as the D358 preview does). Each label becomes a pillar
  dated on its resolved date.
- The pillar's zero rate is the close's pinned observation for that label's
  series. The rest is today's exact simple ACT/365F arithmetic.
- Refusals:
  - **`rate_maturity`:** a resolved pillar falls on or before `t_0`.
  - **`date_list_order`:** dates are out of order.
  - **`rate_mark`:** a label has no observation at the close; the error names the
    label.
  - Any failure to resolve the date list.
- **Anchor at `t_0`.** The discount factor at `t_0` is exactly 1 by definition. It
  is used as the left bracket for dates between `t_0` and the first pillar, so a
  payment the day after a Friday close still discounts against a 1D pillar dated
  Monday. This is exact, not extrapolation, and is recorded in the build's lineage
  as the anchor.
  *(Author's call; it changes today's behaviour, which refuses those dates.)*
- Dates after the last pillar are refused (`discount_coverage`).
- A bracket wider than the maximum pillar span is refused
  (`discount_interpolation`), as today.

### Consumers

- **Market board and curve page.**
  - One card and one page per admitted rate curve, for inspection.
  - The page lists each label with its resolved maturity, days, zero rate and
    discount factor, and plots the pillars.
  - No interpolated line, per-date lookup tool or per-label history view
    (no consumer).
  - Historical views resolve against the selected close's own `t_0`, and use the
    D334 close rule.
- **Payment discounting.**
  - The currency discount default references an admitted rate curve (exact
    revision and code) with an explicit interpolation policy, which must equal the
    curve's.
  - One default per currency now serves every close, because the curve rolls.
    Per-day defaults are no longer needed.

### Removal

- **Schema.** `rate_curve_points` and literal-maturity rate curve rows are
  dropped. `rate_curves` keeps only definition-admitted curves.
- **Code.** Readers, fixtures and checks that construct literal pillars are
  rewritten against admitted definitions or deleted.
- **Hosted pilot.** The pilot's literal curves and per-day defaults
  (`usd_zero_*`, `demo_discount_curve`) and their observations are removed in the
  same schema window. The seed below replaces them.
- Following the hosted schema-release process: the migration refuses to run while
  literal rate curves exist that the window has not removed.

### Hosted demo seed (private, not in the repository)

- **Date list** "USD rate tenors" over a daily calendar-day schedule, with labels:
  - 1D = `oe.days + 1bd` on a business calendar;
  - 1W, 1M, 3M, 6M = calendar-day indexes 7, 30, 91, 182;
  - 1Y, 2Y, 3Y, 5Y, 7Y, 10Y, 20Y, 30Y = indexes 365, 730, 1095, 1826, 2557, 3652,
    7305, 10957.

  These are calendar-day approximations of the tenors, labelled as such. Every
  adjacent gap is within the maximum pillar span.
- One published interest-rate definition, "Synthetic USD zero rates", admitted and
  published into the demo's market definitions. The USD discount default points
  at it.
- **Synthetic marks** for every tenor at every demo close (about 522 closes):
  - an upward-then-flat shape around 3.9–4.4%;
  - small deterministic day-to-day moves;
  - recorded under a synthetic demo source and labelled synthetic.
- Rehearse on the twin, then apply on the pilot in the schema window.
  **Consequence:** demo PVs change because discounting becomes real. Accepted by
  the owner.

## Constraints

- `AGENTS.md` in full:
  - TDD;
  - exact-version foreign keys;
  - historical context on every read;
  - idempotent writes;
  - no silent defaults;
  - numerical results reproducible from pinned inputs;
  - independent adversarial review (temporal, numerical, schema release).
- Pricing and interpolation stay in the managed backend; the UI only presents.
- The migration follows the hosted schema-window process (deploy timer stopped,
  migration head published in order).

## Left to the builder

- Table and column names for admitted rate curves and tenor rows, provided:
  - the curve pins the exact definition version;
  - each tenor row pins its series;
  - rows follow the definition revision's foreign keys.
- How series identity is reused across revisions, provided it is deterministic
  and exact (for example, derived from catalog, curve code and label).
- API route and payload names for admission and for the discount default.
- Error codes beyond those named, following existing naming.

## Verification

- **Build:** pillars roll between two closes with the same marks; a label without
  a mark; a resolved date on `t_0`; order violations; historical views and a later
  correction.
- **Interpolation:** between `t_0` and the first pillar (the anchor); between
  pillars; beyond the last pillar refused; brackets wider than the maximum span
  refused. Independent numerical oracle values.
- **Admission:** retries; a different payload under the same request id;
  unpublished or policy-less versions refused; series reuse across revisions and
  versions; permissions.
- **Discounting:** a swap discounted from the definition-driven curve matches an
  independent calculation. The default's policy must equal the curve's.
- **Removal:** no reader of `rate_curve_points` remains; the migration refuses to
  run with literal rate curves present.
- Browser checks for the curve page (pillars, discount factors, definition link,
  no edit control) and for the policy field in the editor.
- Twin rehearsal of the private seed before the pilot window.

## Decision record

Add D359 for definition-driven rate curves, the `t_0` anchor and the removal of
literal-maturity rate curves. Update D358 and D318's remaining work.
