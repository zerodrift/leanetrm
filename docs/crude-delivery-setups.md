# Crude delivery setups

D293 adds an executable mapping from exact delivery masters to an existing
monthly index source. Its consumers are the delivery setup editor, native crude
trade preview/booking, retained trading views, and interactive/queued contractual
portfolio risk. This is a local implementation; production activation is separate.

A published setup retains:

- A logical code, immutable version, explicit preceding publication, and an
  inclusive/exclusive delivery-date interval.
- Exact delivery reference catalog/revision foreign keys for commodity, grade,
  facility, custody point, standard-volume specification, and UTC clock. The
  grade/facility/custody relationship must already exist as a typed master.
- The original pricing definition revision and curve. Its quote, currency,
  quantity specification, multiplier, monthly contracts, fixing definitions,
  series and selected observations remain in that original namespace.
- The explicit `same_standard_volume_at_60f_v1` policy. It admits matching
  commodity, measurement-basis and unit codes with verified volume dimensions,
  a grade at 60 Fahrenheit, and unchanged exact pricing-unit scales. The managed
  validator checks coherent connected unit graphs and preserves every pricing
  component ratio; additional delivery units may not reinterpret those ratios.
  No unit, density or temperature conversion is performed.

The source must be an explicitly admitted primary, swap or crude bundle with
exact dated input selections. Publishing a setup never copies a curve,
observation or market factor. Explicit supplemental observation selections and
frozen pins are preserved; certified latest-correction views use their declared
correction policy. Setup preview and contractual valuation use the same source
selection rule.

## Authoring and publication

Settings → Crude delivery setup offers Save, Validate and Publish. Each field is
selected explicitly. Validation checks typed references, standard-volume
compatibility, and exactly one qualified monthly forward/fixing per covered
month; it does not assert that market prices are available on every market date.
A published version can preview its physical delivery value at a chosen market
date before delivery starts. Consideration and fees are independent cash sides.

Authoring requires `WriteMarketDefinitions`; publication requires
`SealMarketDefinitions`. Both also require current read authority for the market
and both reference catalogs. Drafts are immutable. Equal normalized terms under
the same base reuse the draft; an already published identical fact is reused.
A draft can be resaved under a newer explicit base after a competing publication.
Returning to earlier terms from a later base creates a new revision; retrying the
original command continues to return its original outcome.
A stale base cannot publish. Publication availability is certified from the
transaction's checked commit time, never its creation timestamp or insertion
order. A retained context cannot see a later setup publication. Current-head resolution
chooses the latest known publication before checking authority; a hidden head
raises an error instead of making an older publication current.

Commands retain actor, request identity, canonical payload and first outcome.
Reusing a request with different terms refuses. Unknown save/publish outcomes
keep the exact request across reloads; a new request cannot replace them.
Authorization is checked after publication waits and again before returning.

The current ticket consumer supports multiple grades and curves within **one
current original pricing revision**. A second simultaneously current pricing
revision refuses publication with `crude_setup_market_scope`. Replacing a setup's
revision remains possible when the other current bindings are compatible with
that scope; retained trades can continue to use earlier admitted revisions.
A broader source-selection workflow remains planned.

## Booking, retention and risk

The crude ticket presents the authorized reference combinations. When several
published bindings share the same displayed tuple, an explicit Delivery setup
selection is required. The request retains `crudeSetupId`; a binding and setup
cannot be mixed. Final database admission checks the native physical and cash
graphs, exact setup, complete source terms, delivery coverage, and current
published head. A competing publication invalidates a new booking under a stale
head. An already admitted booking retry keeps its first outcome.

The delivery assignment and physical side use explicit v2 economic contracts.
Their delivery keys name the maintained masters; instrument quantities, cash,
output units and currency remain the original pricing references. The retained
program includes the selected assignment and equivalence contract. Existing v1
identities and program manifests omit the new fields and retain their contracts.
Opening or valuing a saved trade never follows the current setup or unit catalog.

Portfolio grouping stays on the original pricing definition. Physical and
financial holdings consume common market scenarios and original factors. A new
master revision does not create exposure or allocate it by inference. The real
queued worker reads the same retained programs under both current reference
permissions and its active lease.

## Qualified scope and remaining work

This increment supports future fixed total standard volume at 60 Fahrenheit,
zero tolerance, one monthly index contract per delivery, independent fixed
consideration/optional handling fee, and explicit undiscounted contractual PV.
The original quote multiplier is retained. Delivery beginning at or before the
valuation instant refuses this pre-delivery valuation profile.

Delivery-to-cash operations for the new separate-master bindings deliberately
refuse with `crude_mapped_lifecycle` before operational writes. Existing imported
v1 assignments retain their independently qualified receipts, inventory and cash
workflow. Extending those joins and contracts to maintained delivery masters is
remaining work; native booking is not evidence of that qualification.

Production activation additionally requires operator-supplied exact custody
handoff references and reviewed installation data. A public terminal name alone
is not a contractual meter, connection or custody point. Public qualification
fixtures remain project-authored synthetic data. No production custody identity
or financial observation is invented by this implementation.

Physical LNG, cargo logistics, tanker freight and Incoterms remain their next
workflow slice. Incoterms need an edition, rule and named contractual place saved
on a trade; they do not imply price assignment, title or freight costs. Broader
fee types and amounts remain deferred until a supported contractual consumer is
implemented.

## Verification

The managed `--physical-crude-side` checks cover separate reference namespaces,
retained v2 execution, unchanged v1 admission, incompatible quantities and indirect
unit-scale changes, shared physical/financial scenario oracles, and a quote with multiplier 0.001
where 1,000 volume units at a raw price of 2 give exactly 2 currency units.

`tools/run_preview.py --crude-setup-check` creates a stock disposable PostgreSQL
installation. It uses a separate delivery catalog/revision and an original source
declared only as a swap bundle. Checks cover semantic replay, normalized dates,
publication role separation, stale-base/rebase behavior, monthly coverage,
historical visibility, current authority for both references, unchanged original
observations, native booking/reopen, stale-head refusal, corrected-source parity,
selected-trade and whole-book queued-worker risk parity, and permission
revocation/recovery without following a hidden current head. Existing native crude and mixed-portfolio
checks also run. Add `--browser-check --browser /path/to/browser` for authoring,
post-commit coded-failure recovery across reloads, explicit ambiguous-binding
selection, ticket booking and read-only-role checks.
