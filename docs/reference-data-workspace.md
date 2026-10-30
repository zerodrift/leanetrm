# Reference data workspace, specification corrections and assisted lookup

Status: Phase 1 implemented (D261); Phases 2 and 3 are proposed designs, not
implemented. Three separately plannable phases. Phase 1 is presentation plus
read operations. Phases 2 and 3 add a write path and an agent capability, and
require decision records, independent adversarial review and the checks named
below before publication.

Phase 1 deviates from this text in two ways. History is read only by people
holding `WriteReferenceData` or `SealReferenceData`, since author names are not
readable by the runtime role; readers see no History. Reading History with its
authors needed one read-only SQL function, `security.reference_display_history`
in migration 0130, so Phase 1 adds a schema head that needs the usual hosted
schema release window.

## Why

The Reference data page today is one "Show" dropdown of 24 categories, a search
limited to the chosen category, a raw table per category and a separate editor
card where the row is chosen again from a second dropdown. One exchange futures
product is spread across four categories (Futures products, Futures contracts,
Contract dates, Trading eligibility), so reviewing a single product such as a
NYMEX Henry Hub natural-gas future means knowing where each part is filed and
reading four unrelated tables. Only display labels are editable; economic terms
(contract size, tick, calendars, units) cannot be corrected at all, so a wrong
specification has no remedy in the application.

Goals:

- Find any reference item from one search and browse categories by group.
- See a futures product's complete specification, its monthly contracts, their
  dates and eligibility on one page.
- Edit in place with one Edit action; correct economic terms, not only labels.
- Move objects pinned to a wrong specification onto the corrected one under an
  explicit, reviewed operation that is not a trade amendment.
- Let the assistant look up specifications, read public exchange specification
  pages and propose corrections that a person reviews and saves.

Non-goals:

- No change to agreed trade terms. A correction never amends, re-confirms or
  notifies a counterparty.
- No silent recalculation. Repinned valuations become stale and are re-evaluated
  through the normal runs.
- No agent write authority over reference data. The assistant proposes; a person
  with the correction role saves.
- No physical table consolidation; storage keeps its exact-version keys
  ([reference-data.md](reference-data.md)).

Anti-goals (a failure even if it works):

- Overwriting a reference row, or any read resolving "latest" by row order. A
  correction is a new revision with its own knowledge time (D238).
- A historical (As of) view showing corrected values for a time before the
  correction was known.
- Web page text acting as an instruction, or a proposed value saved without the
  reviewer seeing its cited source.

## Phase 1 — Navigation, product page and in-place editing

### Navigation

The page gets two rows of tabs: five group tabs, then sub-tabs for the
categories in the selected group. A search box beside the group tabs always
searches every category the person may read. The proposed grouping (builder's
call to refine labels; every current category must appear exactly once or be
folded into the Futures product page):

| Group | Sub-tabs |
| --- | --- |
| Markets & contracts | Futures, Price indexes, Exchanges, Clearing houses, Interest rates, Data sources |
| Commodities & units | Commodities, Units, Unit conversions, Currencies, Measurement bases, Quantity definitions, Numeric input types |
| Locations | Locations, Location types |
| Calendars | Calendars, Calendar dates, Market clocks |
| Conventions | Price conventions, Settlement rules, Delivery patterns |

Futures contracts, Contract dates and Trading eligibility are not sub-tabs; they
appear on the Futures product page. The selected group, sub-tab, search and item
are part of the page address so a view can be linked and survives reload.

Search results are grouped by category, show the item name, category and a
one-line summary (for a futures product: exchange, location, lot size, quote
unit), and open the item's detail page. Search is a backend operation over the
selected context's resolved revisions, bounded and paged like the current
catalog read; the browser does not filter a downloaded catalog.

### Detail pages

Selecting a row on any sub-tab, or a search result, opens a detail page for that
item with a breadcrumb back to its list. Generic categories show their fields as
a labelled grid. The **Futures product page** is a single scrolling page:

1. Header: product name, definition state (placeholder/candidate/synthetic),
   permanent code, current version, the context's As-of time, **Edit** and
   **History**.
2. Venue: exchange, clearing house, settlement method.
3. Underlying: commodity, location, market clock.
4. Economics: contract size, lot unit and basis, quote currency and unit, quote
   multiplier, outright tick.
5. Calendars: trading, delivery, settlement.
6. Monthly contracts: one row per series with label, delivery period, first and
   last trading dates and other retained milestones, and trading eligibility.

Each referenced item (an exchange, a calendar, a location) links to its own
detail page. History lists the product's versions with knowledge time, author,
reason, the changed fields, and for an assisted correction its cited evidence.

The page is served by one backend read that returns the product, its series,
milestones and eligibility under the selected context, with presentation labels
and the exact revision identities. TypeScript renders it; it computes no dates,
eligibility or units.

### Editing

One **Edit** button turns every field the person may change into an input in
place. Fields the person's role cannot change stay as text; identity fields
(code) show as permanent. **Save** classifies the change:

- Display fields only (names, labels): the person confirms the listed changed
  fields, then one operation saves and applies them. It requires both
  `WriteReferenceData` and `SealReferenceData`; the confirmation is the explicit
  review that today's separate apply step provides. The display-overlay model
  (original source revision plus display revision) is retained. Display editing
  extends to the name and label fields of every category.
- Any economic field (Phase 2, futures only): requires the correction
  permission, a reason, and the impact review before it is saved.

Monthly-contract milestone dates are edited from the product page's Edit mode.
Historical (As-of) contexts are read-only; Edit is unavailable there with the
existing `historical_read_only` reason. Unsaved-edit tracking, retained
uncertain requests and request ids follow the existing editor's behaviour.

Phase 1 ships with Edit covering display fields only; economic fields stay read
only until Phase 2.

## Phase 2 — Specification corrections and repinning

A correction changes economic reference terms that were wrong. It is authorized
by role: a person holding the correction permission makes and saves a
correction alone; no second approver is required.

**Scope.** Corrections cover futures only: the product's economic terms, its
monthly contracts' delivery periods and milestones, and trading eligibility.
Other categories (unit-conversion ratios, calendars and calendar dates, price
conventions, interest-rate definitions, settlement rules) are later extensions,
each added with its own dependants, impact rules and review. A correction
carries every monthly contract forward under the corrected product revision with
the same series codes, so series identity is stable across revisions. Adding or
removing a monthly contract is a separate operation, not a correction.

**Recording.** A correction creates a new reference revision containing the
corrected rows, with its author, reason and, when it came from an assistant
proposal, a link to the stored proposal and its evidence. Prior revisions are
retained unchanged. Saving identical values is a no-op returning the existing
revision (D238); a retried save carries its request id and gets its first
outcome.

**Workspace boundary.** A correction applies only in the corrector's workspace.
Catalogs may be shared, and catalog namespaces are not an authorization
boundary, so the correction never repins or reports objects outside the
corrector's workspace and readable scope. Other workspaces keep the old
revision until someone there applies the correction under their own review.

**Impact review.** Before saving, the backend computes and returns every object
in the workspace pinned to the revision being corrected that depends on a
changed row. The person sees counts and a browsable list, then confirms. If the
pinned set changes between review and save, the save is refused with a
structured conflict and the review is recomputed. Each object kind is handled
as follows:

| Object | Effect |
| --- | --- |
| Booked trades/sides and futures holdings | Repinned |
| Curve definitions and curve memberships | Repinned |
| Stored valuations, risk results, variation-margin figures | Marked stale; recomputed by the normal runs |
| Market observations, settlement prices, quotes | Unchanged; never rewritten |
| Market scenarios | Unchanged; immutable |

**Repinning.** Confirming saves the correction and repins each affected object
to the corrected revision in one idempotent operation. A repin is a separate
record beside the object, linking the object, old revision, new revision and
the correction. It is never a new trade version and not a trade amendment:
agreed trade terms, trade versions, confirmations and counterparty events do not
change. Stale valuations are not deleted or recomputed in the save.

**Time.** A correction publishes a new resolved context that pins the corrected
revision and the repinned objects. Existing contexts keep pinning the old
revision, so historical views and reports computed under them are unchanged.
This uses the existing manifest/context pins; it does not depend on the
unbuilt knowledge-publication protocol (`known_from`/`known_to` intervals for
reference revisions, see [context.md](context.md)), and no read falls back to a
current or latest revision.

To be resolved in this phase's decision record (builder's design, owner
approval): whether a correction revision is catalog-wide or scoped to the
changed rows given the current catalog/revision key structure; the exact
permission name and which roles hold it; whether a correction whose impact
crosses a closed period is permitted, refused or needs an extra confirmation;
and how a correction to a row that also changed in an unrelated later revision
is merged or refused.

## Phase 3 — Assistant lookup and proposed corrections

The assistant uses the same authorized operations as the UI. It gets three
tools; none writes.

- **Look up reference data**: the Phase 1 cross-category search and detail read,
  at the conversation's pinned context.
- **Fetch a public web page**: an application-owned fetch tool, not a
  provider-specific web feature, so it works with any configured provider and no
  provider type enters domain contracts. Any public URL is allowed. The fetcher
  refuses private, loopback, link-local and internal addresses (including after
  redirects and DNS resolution), allows only HTTP(S), and bounds size, time and
  redirects. Returned page text is untrusted data: it cannot trigger tools,
  widen permissions or count as approval.
- **Propose a correction**: the assistant supplies the product, field changes,
  a reason and, per changed field, evidence (URL, retrieval time, the exact
  supporting passage and a content hash of the fetched page). The backend
  validates the proposal with the same rules as Save and returns the diff and the
  Phase 2 impact. The proposal is stored as an agent record (id, conversation
  turn, pinned context, changes, reason, evidence), idempotent on its turn and
  step, so Review survives a reload and a saved correction can link to it. It
  never writes reference data or repins anything.

**Review.** The proposal appears in the assistant panel with **Review**, which
opens the product page in Edit mode prefilled with the proposed values
(highlighted, with the current value and the cited passage beside each) and the
reason. The reviewer may change anything, then Save runs the normal correction
and impact confirmation under the reviewer's own permission. If the
specification changed after the proposal, Review revalidates against the current
revision and shows the difference rather than applying stale values. The saved
correction records that it originated from the proposal and retains the
evidence citations: the URL, retrieval time, hash and the short supporting
passage only, not a copy of the page.

Fetch and proposal calls count against the existing agent execution limits and
usage accounting.

## Verification

- Phase 1: behaviour checks for grouped navigation, cross-category search,
  linked addresses, the product page read under current and As-of contexts, and
  display-edit save; browser checks of the page at desktop and narrow widths.
- Phase 2: checks that a correction appends a revision and never overwrites,
  identical saves are no-ops, retries return the first outcome, historical reads
  are unchanged, the impact set is complete for each pinned object kind, repins
  are not trade amendments, valuations go stale, and conflicts refuse.
  Independent adversarial review (temporal behaviour and numerical correctness).
- Phase 3: checks that the fetcher refuses internal addresses and redirect
  escapes, injected page instructions cause no action, proposals never write,
  Review revalidates a stale proposal, and evidence is retained on save.
  Independent adversarial review (authorization and agent boundary).

## Calls made without owner input

These were settled during design review and should be checked by the owner:
the repin/stale/unchanged table in Phase 2; repins as separate records rather
than trade versions; one-step display save requiring both existing grants with
display editing extended to every category's labels; stored assistant
proposals; monthly contracts carried forward by code, with add/remove kept
separate from corrections.

## Left to the builder

Visual styling within the existing app styles, final group and tab labels,
search ranking, how the impact list is paged, the History presentation, and the
narrow-width layout of the two tab rows.
