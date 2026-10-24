# Reference data and futures

Status: approved baseline requirements with an initial schema implementation and
173 combined reference/market/security/coordinate/rate checks passing. No booking, valuation, activation or historical publication service is
implemented by the schema alone.
The local [market-data slice](implementation.md) adds measurement-basis, quantity,
quote, profile, settlement-target and cash-rounding references. Broader product
and state-dependent conversion support remains gated below. The bounded
[synthetic futures workflow](implementation.md#synthetic-futures-booking-and-holdings)
now consumes fixed-total cash product terms, exact curve membership and retained
trading milestones; daily/final settlement and margin processing remain work.

## Business references, setup and internal records

Business reference data identifies shared facts and business objects: units,
currencies, commodities, typed locations, venues, calendars, product/contract terms
and index/rate identities. **Market setup** defines how the installation constructs
and interprets prices: quote/quantity conventions, delivery patterns, settlement
rules and rounding. Catalog/revision records and relationship tables support both;
they are internal storage, not extra user-maintained reference categories.

The preview exposes curated Reference data and Market setup views. It hides joins
and version machinery from the category lists. This changes presentation, not
access rights. Pricing configuration remains versioned, explicitly selected and
subject to current permissions; labeling it setup never permits a silent default.
Existing exact-version foreign keys stay intact in their current storage schema.
Physical table consolidation and retirement of overlapping legacy definitions
require a separate migration rather than a cosmetic schema rename.

## Location types and geography

One Location model uses the existing exact-version `location_types` relationship.
Types can include pipeline, node, hub, region, terminal, delivery point and physical
site. Types describe the business meaning, not the geometry. There is no separate
business class/table for every location type, and a type is never inferred from
its name or available coordinates.

[0004_location_coordinates.sql](../db/migrations/0004_location_coordinates.sql)
adds optional point latitude/longitude to existing location versions. Both must be
supplied together in WGS 84 decimal degrees, with latitude in [-90,90] and longitude
in [-180,180]. The fixed coordinate convention follows the
[WGS 84 decimal-degree interchange convention](https://www.rfc-editor.org/rfc/rfc7946#section-4).
Numeric inputs retain the exact supported decimal representation; over-precision,
nonfinite values and out-of-range values are rejected without rounding.

Missing coordinates stay NULL; zero is a valid supplied coordinate, not a missing
value. Existing rows receive no inferred or backfilled point. A physical site can
retain a supplied GPS point, but a pipeline needs route geometry and a region a
boundary for their full geographic extents. A hub may represent a set of locations.
Routes, polygons, spatial queries and location memberships/topology are not yet
implemented. A supplied point does not establish any of them. In particular,
geographic proximity does not establish pipeline connectivity or a pricing/risk
dependency. Those relationships must be explicit when implemented.

Coordinate corrections create a new reference revision; retained definitions and
calculations keep their original version. Optional points allow unknown geography
and non-point/virtual locations; map-dependent operations must reject unavailable
geometry rather than invent it. No map-dependent operation exists in this slice.

## Required foundation

Trading additionally consumes scoped immutable `LegalEntity` and `Counterparty`
versions. A trade revision binds both through catalog/ID/revision foreign keys;
they are not properties of an instrument or inferred from a book/trader. Their
effective date and certified publication visibility govern selection. The local
preview supplies synthetic party versions for booking and amendment and bounded
authorized editors for appending legal-entity/counterparty versions. General
reference-catalog authoring remains outside that workflow.

Reference data is a core application module, not a collection of optional seed
files or arbitrary JSON attributes. The current schema provides these families:

| Family | Definitions and relationships |
| --- | --- |
| Currencies | Currency identity and display minor units; contractual rounding is separate |
| Units | Dimensions, units, commodity-compatible units and explicit multiplicative conversions |
| Commodities | Commodity identity, quantity dimension and permitted units |
| Locations | Typed locations and explicit commodity/location relationships |
| Venues | Exchanges and clearing houses, separately identified |
| Time | Versioned market clocks, calendars and dated business-day overrides |
| Rates and indexes | Interest-rate specifications consumed by dated zero rates; commodity-price index definitions used by settlement targets |
| Futures | Product terms, dated contract series, milestone states and supporting evidence |
| Provenance | Source identity, retrieval metadata, evidence and distribution review status |

Business relationships use foreign keys, including the catalog and exact reference
revision. Codes and names are labels/keys; a string naming an absent currency,
unit, location, commodity, exchange or product is not a valid relationship.
Imports, agents and UI writes face the same database constraints. Referenced rows
cannot be cascade-deleted or silently replaced. Structured extension payloads
cannot replace these core relationships with an entity/attribute/value model.

Dimensional consistency is enforced where it can be expressed relationally:
commodity units match the commodity dimension; conversion endpoints share a
dimension; product lot and quote units belong to their commodity. Constraints
also reject invalid numeric ranges, duplicate identities and incompatible null
combinations. Economic qualification still validates conversion consistency,
calendar coverage, source disagreements and model-supported behavior.

The initial commodity table declares one canonical quantity dimension per
commodity. Volume-versus-energy measurement bases for the same commodity require
a later explicit measurement-basis model and state-dependent conversion inputs;
they must not be modeled as an unconditional unit conversion. This baseline
restriction must be resolved before those mixed-basis products are enabled.

## Current table consumers and implementation policy

Add a table or dependency only with a named consumer or an executable baseline
case. A catalog import or a screen showing unused rows does not justify one.
After migrations 0001–0028, there are **34 reference tables**, including internal
version and relationship tables. Their bounded uses are:

| Tables in `ref_data` | Current consumer or executable baseline case |
| --- | --- |
| `catalogs`, `revisions` | Every exact reference key, draft/seal checks and scoped catalog ownership/sharing |
| `currencies` | Quote and interest currency links; managed Money rejects cross-currency addition |
| `dimensions`, `units`, `commodities`, `commodity_units` | Futures lot/quote validation and compatible commodity-unit foreign keys |
| `unit_conversions` | Capture an explicit multiplicative conversion; write checks reject mismatched endpoint dimensions. Quantity/price conversion consumer validates exact ratios, versions, commodities and bases |
| `measurement_bases`, `quantity_specifications` | Typed quantity/price multiplication and explicit ratio orientation in scaling curves |
| `quote_specifications`, `value_specifications` | All-in/spread type checks, quote multipliers, numeric-series domains and price-clearing grids |
| `clock_rules`, `calendars`, `calendar_days` | Futures milestone clock/calendar links; published date schedules (engine `date_schedule_v2`) bind an exact calendar version for business-day adjustment and rolling; the power preview takes holidays from the settlement index calendar, and authored TOU products bind exact clock rules. `weekend_mask` bit 0 is Monday through bit 6 Sunday |
| `location_types`, `locations`, `commodity_locations` | Commodity curve location compatibility, futures location links, explicit pipeline/node/hub/region types and supplied GPS points |
| `exchanges`, `clearing_houses`, `exchange_clearing_houses` | Futures product links and rejection of unsupported exchange/clearing relationships |
| `interest_rate_definitions` | The sample zero-rate curve explicitly consumes currency, day count, compounding and implementation identifiers to calculate discount factors |
| `price_index_definitions`, `settlement_targets` | Forward curves retain their own exact index settlement target independently of upstream futures. Fixing/payment execution is not implemented |
| `sources`, `product_evidence` | Exact observation source checks; futures evidence/provenance capture and source/product linkage constraints. Data-rights qualification remains separate |
| `futures_products`, `futures_series`, `futures_milestones` | Product economics, dated delivery and authoritative last-trading validation in the five-layer example |
| `delivery_profiles` | Explicit equal-profile/equal-period constituent mappings; unsupported shaping fails |
| `cash_rounding_conventions` | Money rounding with explicit currency, increment, tie rule and stage, exercised with positive/negative ties |

[0005](../db/migrations/0005_retire_unused_definitions.sql) retires the unused FX,
heat-rate, standalone day-count and standalone compounding catalogs. It refuses
populated legacy tables or external dependent objects; it never discards retained
data. The original published migration stays unchanged. Selected day count,
compounding and implementation identifiers now live directly on the used
interest-rate specification, with no default and no imported convention catalog.

The rate module implements Actual/365 Fixed and simple compounding directly,
with exact integer-ratio intermediates. The [implementation notes](implementation.md#interest-rate-example-and-numerical-policy)
record the explicit division/rounding policy and supported bounds. Units, Money
and Quantity also remain project-owned, with a deliberately small supported
catalog. A new convention needs an actual consumer and independent tests; no
broad financial library or imported master catalog is required.

Standard unit conversions and market economics remain distinct. A fixed energy
conversion can use a reviewed exact ratio, whereas gas-volume-to-energy conversion
requires explicit measurement conditions/heating value and power-to-energy
conversion requires duration/shape. Missing inputs never select an automatic
conversion. Database foreign keys, market unit variants, contractual measurement
bases and provenance remain mandatory.

## Common units and explicit conversions

The synthetic preview's quantity converter consumes project-authored unit and
conversion rows: Btu (IT), MMBtu (IT), therm (IT), Dth (IT), J/MJ/GJ, kWh/MWh,
kW/MW/GW, cubic metres, litres, cubic feet, Mcf/MMcf, US gallons, petroleum barrels,
kilograms, metric tonnes, pounds, short tons and troy ounces. Mcf and MMcf are
volume multiples, without inferred gas standard conditions or heating value.
The generic schema fixture retains its unrelated synthetic units; new standard
preview gas definitions explicitly select MMBtu (IT).

Ratios use exact defining constants, not typical fuel-content factors. For example,
1 MWh = 3.6 GJ, 1 petroleum barrel = 42 US gallons and 1 short ton = 2,000 pounds.
The exact IT Btu constant is 1,055.05585262 J; therm (US) is a different convention.
Definitions were checked against [NIST SP 811 conversion tables](https://www.nist.gov/pml/special-publication-811/nist-guide-si-appendix-b-conversion-factors/nist-guide-si-appendix-b8)
and [its exact Btu footnote](https://www.nist.gov/pml/special-publication-811/nist-guide-si-footnotes).
No external observations or proprietary master catalog are imported.

`consistent_exact_unit_ratios_v1` validates the supplied immutable conversion graph
and applies inverse/chained ratios without intermediate decimal rounding. Typed
quantities require the same exact commodity and measurement basis; matching a
physical dimension alone is insufficient. Price conversion applies the inverse
ratio while preserving the quote multiplier. Nonterminating results require an
explicit final precision/tie choice; display precision alone does not round stored
values. Reference data exposes a converter with exact or explicitly rounded mode.
A separately selected unit bundle can add definitions without changing the
reference versions of existing trades or curves; unavailable historical bundles
are not replaced with current rows.

## Reference revisions and history

The first storage contract is an immutable reference-revision bundle inside a
catalog. Every row and foreign key carries the same catalog/revision. Creating a
new revision retains the old one; it does not update rows used by existing runs.
This favors straightforward, coherent reference snapshots over independent
latest-row selection. Reference volume and snapshot-copy costs must be measured
before introducing finer-grained storage/reuse.

Draft revisions accept inserts. Definitions are append-only; corrections use a
new revision. Sealing stops further inserts and retains a complete bundle. A seal
is a storage freeze, not approval, activation, entitlement or historical knowledge
publication. Reference revision IDs will be included in resolved context manifests.

The schema's creation/retrieval timestamps are audit/source metadata. They are
not `known_from` and do not implement the unresolved knowledge-publication
protocol in [context.md](context.md). Business-effective selection and retained
publication intervals must be implemented before historical date selectors or
approved definitions can be exposed. No current/latest fallback is permitted.

Catalog namespaces are not an implemented authorization boundary. Workspace
ownership, current access, approval evidence, licensing grants and secure import,
export and activation operations remain application/persistence milestones.

## Futures are baseline products

The initial trading slice must include futures as well as fixed/floating linear
contracts. Keep three levels distinct:

1. A product definition specifies venue, commodity/location, quote currency and
   unit, quote multiplier, lot unit/basis, tick conventions and settlement method.
2. A dated series identifies an actual delivery period under one exact product
   revision. A prompt alias resolves to this dated identity for every run.
3. A Trade owns the signed contract quantity, execution/basis and lifecycle facts.
   The reusable product/series never acquires a trade's settlement state.

An immutable Instrument delegates economics and schedule behavior to its sides.
Future lifecycle settlement events update the Trade's outstanding/realized state.
The scenario mechanism still shocks dated market factors and reprices affected
instruments; it does not mutate contracts or Trade quantities.

Capture product terms with explicit units and quote scaling. One contract is not
one physical unit. A price quoted in a currency subunit needs its quote multiplier;
a per-hour lot is not a fixed total MWh lot. Tick values depend on those terms and
the dated delivery shape. Multi-tier/strategy tick rules require explicit future
support; one outright tick does not claim complete venue rule coverage.

Capture first trading, last trading, notice dates, delivery dates and final
settlement separately. Product introduction, series listing and a daily session
opening are different events. A delivery month alone does not determine every
deadline. Store native business dates and applicable clock rules, plus resolved
UTC instants when known. A date-only deadline is not an invented midnight instant.

Every milestone explicitly distinguishes unknown, known and not applicable.
Unknown first-notice data is not the same as a cash-settled contract having no
notice obligation. Date rules, holiday calendars and official series-specific
overrides are retained independently of calculated dates. Missing dates cannot
be replaced with plausible defaults or zeros.

## Accuracy and maintenance

### Implemented source capture boundary

The managed API executable also provides an operator command for private source
acquisition. It accepts no application/database credentials and does not mutate a
database. All paths are absolute; the credential file and output directory require
owner-only Unix permissions. Existing output files are never overwritten.

```sh
dotnet OpenEtrm.Api.dll --capture-reference cme-products CONFIG OUTPUT
dotnet OpenEtrm.Api.dll --capture-reference cme-contracts CONFIG SELECTION OUTPUT
dotnet OpenEtrm.Api.dll --capture-reference ice-files MANIFEST OUTPUT
```

`CONFIG` is private JSON with explicit `clientId`, `clientSecret`, `pageSize`,
`maximumRequests`, `minimumRequestSpacingMilliseconds`, `maximumResponseBytes`
and `maximumCaptureBytes`. Token and reference origins are fixed to official CME
HTTPS endpoints. Redirects are disabled. Requests use client-credentials OAuth;
tokens and provider error bodies are excluded from packages and diagnostics.
Pagination must retain consistent totals and return every exact expected page;
changed scope, duplicate identities, missing pages and resource limits reject the
whole capture. Retries require a new explicit invocation; there is no silent
partial-success or provider fallback.

`SELECTION` is an array of exact `{ "productId": "...", "exchange": "..." }`
source identities. Product names and trading symbols are not identifiers. The
selected product must report `securityType=FUT`; each instrument must link back
to that exact product on the selected clearing exchange. Discovery output also
contains non-futures and test products; it is not a tradable-product list.

The ICE manifest supplies `maximumDocumentBytes`, `maximumCaptureBytes` and
`files`. Each file requires `productId`, `exchange` (the supported IFED namespace),
`kind` (`expiry_csv` or `specification_pdf`), absolute `file`, exact official
download `url`, `retrievedAtUtc` and matching `sha256`. Local file evidence is
explicitly labelled `operator_reported_download`: its URL and retrieval time are
operator assertions, rather than an HTTP response observed by this process.
PDF content is retained as evidence without automatic financial interpretation.

The supported ICE CSV contract has `CONTRACT SYMBOL,FTD,LTD,FND,LND,FDD,LDD,FSD`
and optional `MWHS`. Gas exports may append one unnamed empty field to data rows;
nonempty unnamed values reject the report. The known Excel text wrapper around a
contract symbol is decoded as text, without evaluating formulas. Native dates
remain dates, blank cells remain unknown, and an empty report does not establish
absence of listings. Published MWh values use the existing exact source-capture
bound (at most 18 fractional digits, magnitude below 1e24); they acquire no
approved unit-reference binding and enter no calculation at intake. No hours,
delivery periods, UTC cutoffs, ticks or missing maturities are generated.

`exchange_reference_capture_v1` retains raw source bytes, hashes, source record
identities and capture start/end times. CME documents identify observed HTTP
responses; ICE documents identify operator-supplied files. All packages explicitly
say `state=unqualified`, `tradingEnabled=false`, `pointInTimeSnapshot=false`.
An enumeration can span provider updates, even when totals remain stable.
Hashes prove retained byte identity, not exchange authenticity, economic approval,
listing completeness, data-use permission or certified historical knowledge.

Source identities are intake keys, not replacements for the existing exact-version
futures product/series primary keys. Qualification must explicitly map them to
retained relational references before UI, imports or agents can book or price.
No production reference import/activation operation is implemented by this command.

Run the synthetic protocol/filesystem checks with the API checks project's
`--reference-capture` argument. The normal `--schedules` verification path runs
them as well. Real source documents and private connection files stay outside
the public repository.

### Qualification and publication

Accuracy is a controlled process, not a permanent claim about a static seed file:

1. Import an entitled source or an authorized manual entry into a new draft.
2. Retain source identity, exact source version/evidence and retrieval metadata.
3. Check dimensions, lot/quote conversion, tick economics and calendar coverage.
4. Compare calculated series dates with authoritative published series dates and
   dated rule changes; record conflicts rather than silently choosing one.
5. Require current scoped approval of exact definition content and usage rights.
6. Freeze and publish a qualified revision through the context protocol; retain
   prior versions, exceptions and verification evidence.
7. Monitor rule/holiday/listing changes and requalify affected future series.

A source being authoritative does not grant redistribution rights. A schema
validation pass, source status label or storage seal does not establish either
economic accuracy or permission to use/distribute that data.

## Public placeholders and installation data

Ship generic schemas, empty placeholders and clearly synthetic fixtures in the
public project. Do not ship real venue catalogs, copied rulebook prose, source
documents, settlement histories or privately licensed reference datasets.
Real definitions belong in installation-specific data loaded under appropriate
permissions. The project's code license does not license third-party data.

Placeholders have an explicit state and missing values; they are never executable
contract definitions. The initial schema intentionally has no active/approved
state. Candidate and synthetic labels are not authorization to book or price.
Activation must validate the complete product, series, clocks/calendars and every
required reference under current authority and applicable data-use permission.

Renaming or omitting the source's name does not establish reuse rights. A hosted
installation also needs its own use/display/distribution review; keeping data out
of source control alone does not resolve hosted-data rights. Source metadata is
not an enforcement mechanism. No redistribution operation is implemented yet.

## Futures valuation and settlement gate

Do not implement a futures holding as an ordinary uncollateralized forward held
unchanged from execution until expiry. Preserve execution economics, last-settled
basis, realized variation margin, unsettled variation and remaining exposure.
The sum must reconcile without counting the same price movement twice.

After settlement, remaining exposure can be nonzero even when the contract's
post-settlement value is zero. Ordinary risk freezes actual historical settlement
facts and shocks only still-variable inputs. Initial model policy must define
which amounts PV includes, cash timing, discounting and any price-alignment or
collateral assumptions. Initial margin is distinct from variation margin.

First enable a documented synthetic fixed-lot convention. Before a real product
is supported, qualify its daily/final settlement, closeout and any delivery/notice
rules. Physically deliverable products require an explicit supported path or an
enforced pre-delivery closeout policy; cash-settled tests do not prove physical
delivery support.

## Verification and remaining work

The migration tests must use a freshly created disposable database and verify
constraints with actual writes: orphan references, cross-revision/catalog links,
dimension mismatches, duplicate keys, invalid numbers, placeholder missingness,
referenced deletions and writes after sealing. Test freeze/write races too.

These storage checks do not qualify the economic model or the temporal resolver.
Further acceptance covers holiday exceptions, date-only versus instant deadlines,
unit/tick economics, pending verification, conflicting sources, expired permissions,
history replay and futures P&L/settlement reconciliation.


## Private inactive source import

`--import-reference CONFIG CAPTURE` validates retained source bytes through the
managed parser before connecting with a dedicated trusted operator login.
Configuration explicitly supplies separate authenticator/operator connection
files, service identity, tenant, workspace, catalog, stable operation UUID,
product/location mappings and any exact MWh quantity reference. Files are private
installation inputs; neither credentials nor real catalogs belong in this tree.
The application runtime cannot execute this import function. The operator login
cannot issue identities, own database objects or bypass row security. Scoped
capabilities still identify the service actor and require current reference read,
write and seal authority before and after the operation.

`imported_product_keys` and `imported_series_keys` retain native composite primary
keys, with unique foreign-key mappings to canonical product and series primary
keys in the same revision. Exact native-key lookups therefore retrieve the same
canonical definition without symbol searches or duplicate per-trade objects.
Source documents retain bytes, hashes, acquisition method and the original
retrieval timestamp. PostgreSQL retrieval instants explicitly floor submicrosecond
precision; the original timestamp remains evidence. Source-native calendar dates
remain dates. Unknown delivery periods and cutoff clocks stay unknown.
Published MWh amounts use exact `Quantity` values pinned to the explicitly selected
sealed electricity/energy/MWh specification. No MW/hour schedule is inferred.

The import freezes a new revision atomically and records the server actor/time.
All imported products and series remain non-executable placeholders. A repeated
operation with identical input returns its receipt; changed input under the same
operation or a duplicate payload under a different operation rejects. Existing
financial data, market revisions, marks and trade state are not changed. Historical
reads apply the existing exact revision visibility policy to aliases and evidence,
including visibility of their quantity references.

An empty daily expiry download retains an incomplete product with no series.
It does not establish an empty exchange listing and does not authorize invented
maturities. Exchange enumeration is evidence, not certification of current trading
eligibility, data entitlements or executable economics. No automatic recurring
reference schedule or real futures activation is implemented by this command.
