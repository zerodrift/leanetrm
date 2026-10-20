# Standalone power preview

**Power TOUs** sits alongside the existing curves, rates, schedules and admin
screens. It uses this repository's managed API and isolated stock PostgreSQL.
Check the repository boundary with `python3 tools/check_standalone.py`.

Choose a market date in the header, then open **Power TOUs**. Select a power
curve, delivery period, product example, hours block and grid interval; none of
these economic choices is preselected. The two editable Monday–Sunday grids
separate non-holidays from holidays. Cells support 60, 30, 15 or 5 minutes.
HE/HB changes labels, never interval identity or economics.
Choose an exact quantity specification for **Flat MW** or **Total MWh**. Total
energy also requires an allocation; the supported choice is flat MW across the
selected intervals. Decimal display places belong to this unsaved example and
are editable for quantities, prices and money.

TOU products are authored on the same page: editors draft a product (code, name,
market clock, hour-beginning peak window, Saturday flag), approvers publish it,
and published versions never change. The calculation offers each product's latest
published version. Each binds an exact clock rule from a sealed reference
revision. TOU products are current definitions: under a historical market view
they are not readable and the page refuses rather than substituting them. The
preview starts with seven common ISO conventions (PJM, NYISO, ISO-NE: Monday–
Friday HE08–HE23 Eastern prevailing; ERCOT, SPP: HE07–HE22 Central prevailing;
CAISO/WECC: Monday–Saturday HE07–HE22 Pacific prevailing; MISO: HE07–HE22 fixed
EST). Verify them against the product specification before any settlement use. Holidays are the closed dates of the curve's
settlement index calendar inside the delivery period; the calendar must cover
the whole period, and clients cannot supply their own holiday list. An IANA
clock rule's declared tzdb release must equal the release of the installed
zoneinfo data (`+VERSION`, else the `tzdata.zi` header, under `$TZDIR` or the
system directory); otherwise the calculation is refused with `clock_tzdb`. The
disposable preview fixture declares the release installed on its host.

| Seeded product | Non-holiday on-peak | Clock |
|---|---|---|
| PJM, NYISO, ISO-NE | Monday–Friday HE08–23 | Eastern prevailing |
| ERCOT, SPP | Monday–Friday HE07–22 | Central prevailing |
| CAISO / WECC | Monday–Saturday HE07–22 | Pacific prevailing |
| MISO settlement / FTR | Monday–Friday HE07–22 | Fixed UTC−05 |

Holiday on-peak cells are empty. Off-peak is the exact complement. These are
editable examples, not exchange product certification. The fixed-clock MISO
example does not define every product associated with that market. The PJM
mask follows [Manual 15, section 12.4](https://www.pjm.com/-/media/DotCom/documents/manuals/m15-redline.pdf).
Qualify real product specifications independently before live settlement.

UTC boundaries retain operating date, local hour, offset and repeat occurrence.
Fall has 25 actual hourly intervals, with the repeated local hour twice, rather
than a fictitious HE25. Spring has 23; fixed EST retains 24. Missing/ambiguous
midnight boundaries and transitions cutting through a cell fail explicitly.

## Calculation contract

The preview consumes the actual selected native curve value and dated member
under current authorization. It requires an explicitly designated synthetic power
commodity, an outright price per MWh with multiplier one, and a compatible
single-index settlement target. Reference/definition pins and captured clock
rules accompany the result.

The example explicitly allocates that quote flat to selected intervals. It does
not construct a calibrated hourly curve or change the curve's stored profile.
Generated all-in prices have no invented LMP decomposition. The managed
settlement class separately accepts energy, congestion and loss components and
checks their exact sum against total LMP.

For flat MW, MWh is MW × actual elapsed hours. Average prices are duration weighted
and cash is the sum of interval MWh × price. Integer/rational intermediates
remain exact, including five-minute durations. The required
`duration_weighted_power_quantity_to_even_v2` policy rounds price reports to 18 digits
and final cash to two, ties to even, after aggregation. It also returns the exact
cash numerator/denominator. Signed quantities and negative prices retain signs.
Hour and energy reports round to 18 fractional digits under that versioned
policy; cash is calculated from exact intermediates, never these report strings.
Hourly diagnostic aggregation requires complete UTC hours and does not replace
a contractually named published hourly price.

Total MWh is conserved rather than multiplied by hours. Each interval retains an
`ExactQuantity` with its exact quantity specification and rational share of total
selected duration. A 1-MWh total over 23 hours therefore has 1/23 MWh in each hour,
without rounding those shares before settlement. Signed totals, exact price
denominators and final overflow checks use the same typed amount operations.
Display rows retain the exact decimal price and rational energy separately from
formatted text. The selected reference revision must contain its quantity
definitions; a supplemental unit catalog is not an implicit cross-version bridge.

Price type (Test, Official or a user label) and timing (End of day, Intraday or a
user label) are independent required classifications. A label never promotes
generated data into an official publication.

## Scope

This is an **unsaved synthetic calculation**. Durable mask publication, product
bindings, settlement imports, official revision eligibility and historical clock
publication remain work. Historical market-context mode rejects this calculation
rather than substituting installed current rules for historical authority.
Current-input mode captures the installed rules once for its calculation receipt.

The fixture uses actual MWh/MW reference units and retains its synthetic currency;
it does not convert user databases. No tables were added for this preview.
Variable volume, metered sub-hour quantity, shaping calibration and futures
variation margin and persistence of these quantity rules on booked sides are
outside this example. The domain, HTTP and browser
checks cover DST, holidays, partitioning, exact arithmetic, reference validation,
editable grids, current access and the original curve screens.

## Continuation checklist

The standalone slice above is implemented and tested. The remaining authoring
and business requirements are:

- Extend the grid editor with rectangular painting and spreadsheet copy/paste.
  Reject overflowing or malformed pastes instead of truncating or filling cells.
  Numeric cells must distinguish an explicit zero from an unknown blank.
- Reuse a suitable numeric grid for month-by-hour shaping weights. Editing and
  validation alone must not claim calibrated prices or applied quantity shaping.
- Provide standalone date-range/membership queries and browser examples for
  spring, autumn, fixed-clock and empty selections, independently of which
  monthly contracts happen to be in the curve fixture. Zero selected hours is a
  valid mask query; an average settlement over zero duration is undefined.
- Extend explicit clock capture and replay, holiday coverage validation and
  non-hour transition scenarios before claiming general clock support. The
  current compiler rejects transitions that cut through a chosen interval.
- Exercise varying generated sub-hour prices and LMP components in the browser,
  including gaps, duplicates, negative values and classification mismatches.
  The current UI generates flat total prices; component validation and hourly
  aggregation currently have managed checks rather than a component editor.
- Qualify explicit peak/off-peak constituent selection, locational combinations
  and forward/fixing boundaries. Keep exact mappings and units, fail on gaps or
  ambiguous overlaps, and count the deal's energy once.
- Add effective/known history to TOU product publication so historical views
  can use the products that were in force; today products are current-only.

These are pending items, not assertions of completed capability. Continue from
the existing standalone domain, API, reference links and checks; do not replace
the curve, schedule or administration foundation with a different application.

## Collected interval delivery value

`POST /api/power/settlement` is the bounded, unsaved actual-price consumer. It
connects one selected power forward and one operating day to the curve's explicit
`market_data.curve_physical_price_sources` association in the exact model revision.
The source is independent of the forward curve's mark recipe: directly quoted
and derived forwards can consume a separately designated delivery-price source.
No association is inferred from curve names, point names, matching prices or the
forward mark's source. Each curve revision permits one source, with exact catalog,
reference, commodity, location, currency and MWh compatibility. A new mapping
requires a draft model version; published mappings and sources remain immutable.

The operation uses current definitions and an explicit `knownAt` cutoff for price
history. It refuses historical-view mode because historical TOU publication is not
implemented. This is not an as-of reconstruction of every definition: the result
pins the selected current model/TOU versions and the separate certified history
context. It does not require a live forward mark or reprice an expired forward.

The request selects `snapshotId`, `curve`, `contractId`, `deliveryDate`, `knownAt`,
`product`, `productVersion`, native `minutes`, complete `nonHolidays`/`holidays`
TOU masks, `quantityBasis`, exact `quantitySpecification`, `quantities` and
`numericalPolicy`. `deliveryDate` must lie inside the selected forward's delivery
period. The exact published product clock must match the physical source clock;
no resampling, clock conversion or hourly/quarter-hour price substitution occurs.
The holiday list comes from the selected index's covered reference calendar.

Every quantity row supplies exact UTC `start`/`end`, decimal-string `value` and:

- `interval_power`: MW with a required explicit decimal-string `shape` multiplier
  for that interval. Energy is MW × shape × actual elapsed hours, calculated with
  rational intermediates. An explicit shape of one represents an unshaped rate.
- `interval_energy`: already-shaped MWh with absent/null `shape`. The amount is
  consumed directly; any additional shape, including one, is rejected.

Quantities and prices join by UTC boundaries, never local hour labels or row order.
The full native operating day must be present in collected history at `knownAt`
before its selected TOU intervals can be valued. Missing prices anywhere in that
day, duplicate/mismatched quantities, incompatible exact references/units, missing
or ambiguous associations and unavailable source authority refuse the calculation.
Zero quantities still require their prices. Negative prices and signed quantities
retain their signs; repeated DST intervals remain separate UTC observations.

The shared `PowerSettlement` arithmetic calculates each exact interval MWh × price
and rounds the aggregate cash once under the explicitly requested
`duration_weighted_power_quantity_to_even_v2` policy. Its duration-weighted average
price is diagnostic and is never multiplied by shaped volume to reconstruct cash.
The receipt retains exact energy/cash, per-row contribution, source and immutable
batch identity, provider revision, certified known time, model/reference pins,
product/clock/mask and `explicit_interval_power_quantity_v1` quantity behavior.

The operation reads one exact curve association and one bounded native price
window. Ordinary market pages do not load these associations. A source/UTC-start
index and both start-time bounds keep the history read inside that source's
selected day; the existing certified cutoff and revision-selection rules remain
in force. No per-interval database request or new price-history store is added.

For an independent synthetic oracle, take four consecutive 15-minute intervals,
USD/MWh prices and an explicit 4-MW input in each interval:

| Interval | Price | MW shape | Derived MWh | Separately supplied already-shaped MWh |
| --- | --- | --- | --- | --- |
| 1 | 10 | 1 | 1 | 1 |
| 2 | -20 | 2 | 2 | 2 |
| 3 | 30 | 0.5 | 0.5 | 3 |
| 4 | 60 | 1 | 1 | 4 |

The MW case gives 4.5 MWh and cash `10 - 40 + 15 + 60 = 45`. The already-shaped
MWh case gives 10 MWh and cash `10 - 40 + 90 + 240 = 300`. Neither result is the
duration-average price of 20 multiplied by its shaped volume. The independent
checks also exercise the complete source-backed operating-day path.

Existing collectors/readers support PJM hourly DA/RT total LMP and ERCOT hourly DA
or 15-minute RT SPP. ERCOT SPP is not relabeled as raw SCED LMP. This consumer reads
collected facts under the existing latest-collected-revision policy; completeness
is not evidence of publisher finality. Its `publicationStatus` is `collected`,
never automatically `Official` or `Final`. Source usage rights and finality-based
fixing publication remain separate qualification work in the
[settlement design](settlement-plan.md). The existing `/power/preview` retains its
explicitly synthetic flat-forward meaning; this operation does not accept, book,
pay or persist a settlement obligation. The existing browser form continues to
use the synthetic operation; the actual-price slice is an authorized HTTP consumer.

Independent synthetic checks cover varying hourly and quarter-hour prices,
MW shapes, already-shaped MWh, negative prices, 23/25-hour days, correction cutoffs,
source lineage and current authorization. Run the focused owned-database check:

```sh
python3 tools/run_preview.py --pg-bin /path/to/stock-postgresql/bin \
  --dotnet /path/to/dotnet --power-actual-check
```

All observations used by those checks are original synthetic values; no external
market dataset or service credential is included.
