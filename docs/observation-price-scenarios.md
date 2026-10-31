# Forward projection and settlement acceptance matrix

Status: bounded booking, valuation, risk and financial cash integration under
D261. Building-block checks and integrated consumers are identified separately
below. These do not establish complete commodity coverage or physical settlement.
All numerical examples are synthetic.

## Explicit selection contract

Commodity filters candidate sources. Admission also checks quantity and currency
units, location/product, delivery coverage, observation frequency, averaging and
weighting, calendar/time zone, publication timing, and exact curve/index versions.
Commodity or a monthly label alone never establishes compatibility.

Keep delivery interval, price observation/trade date and calculation window,
contract-selection/fixing anchor, publication/finality instant, knowledge cutoff,
and referenced dated contract separate. Last trading, expiry, final publication
and payment are distinct milestones. Select prompt at the explicit contractual
anchor, never at the later time a publication happens to arrive. A curve used to project
prices need not be the index defining actual settlement. That mapping must be
explicit in the versioned terms; crossing into delivery never creates it.

## Commodity and source combinations

Every row is an explicit configured case, not a convention inferred from a
commodity name. “Settlement” below means the price used in the payment formula;
it does not claim physical delivery or futures margin processing.

| ID | Commodity/product | Forward projection | Actual settlement price | Required behavior and rejection case |
| --- | --- | --- | --- | --- |
| GAS-M | Gas, monthly indexed | Selected monthly OTC curve | One published monthly index value | One externally published monthly fixing stays one observation during delivery; its publication need not be at month-end. Reject automatic conversion to daily rows or last-mark substitution while publication is missing. |
| GAS-A | Gas, monthly average of daily indices | Explicit whole-month or remaining-period projection with declared weighting | Contractual average of daily/flow-date prices | Preserve already fixed contributions and project only the remainder. Reject double-counting actuals by treating a whole-month mark as a balance-of-month mark. |
| GAS-D | Gas, daily indexed | Explicit daily curve or qualified monthly-to-daily mapping | Daily index publications | Preserve booked daily weights; only published observations fix. Reject an unqualified monthly mapping and gaps in required daily publications. |
| GAS-F | Gas, futures-referenced daily observations | Selected dated futures contracts | Those contracts’ official daily exchange settlements; final value for later samples only under an explicit carry rule | Resolve exact contract or prompt offset at each explicit fixing instant. Reject bare monthly futures selection without the required contract-selection terms, or unavailable final evidence. |
| GAS-X | Gas, daily physical index with futures projection | Futures-derived configured curve | Explicit separate daily index | Futures expiry does not freeze the daily settlement index. Reject implicit source switching or missing delivery mapping. |
| PWR-H | Power, interval indexed | Explicit hourly/subhourly or shaped forward curve | Matching interval index prices with explicit day-ahead/real-time market and location | Preserve UTC interval identities, DST, energy weights and negative prices; never substitute day-ahead for real-time. Reject missing intervals, ambiguous local hours and unqualified frequency conversion. |
| PWR-B | Power, monthly block | Selected monthly base/peak forward product | Explicit block index or weighted interval-index formula | Preserve selected TOU/calendar and averaging. Reject treating a peak product as all-hours or a monthly mark as an hourly shape. |
| PWR-Q | Power, shaped or metered volume | Explicit shaped forward prices and declared quantity forecasts | Selected interval index times contractual interval quantities | Keep price fixing separate from metering and quantity revisions; reject substituting a block-average price for a generation-weighted price. |
| PWR-F | Power, futures-referenced | Selected dated futures contracts | Selected contract’s official daily settlement, or final value under explicit post-expiry sampling terms | Use that contract’s recorded milestones even when trading overlaps delivery. Reject reuse of another product’s expiry convention. |
| OIL-A | Oil/products, period average | Selected forward curve with explicit observation mapping | Declared daily assessments averaged over the contractual window | Preserve location/grade, quote scale and weights. Reject implicit barrel/mass conversion, calendar substitution or missing assessments. |
| MET-A | Metals, prompt/average | Selected dated forward/futures or explicit average curve | Declared dated settlement or assessment average | Keep prompt date separate from averaging and delivery periods. Reject treating all prompts as monthly or inferring quality/unit conversions. |
| LNG-I | LNG, index-linked cargo or financial contract | Selected LNG/gas forward curve or explicit cross-index recipe | Declared LNG/gas assessment average, contractual coefficients and adjustments | Retain delivery location, energy basis, pricing window and lag. Reject substituting a gas hub index or inferring a mass-to-energy conversion. |
| LNG-O | LNG, oil-linked cargo | Explicit oil-forward formula with typed coefficients | Declared oil assessment average over the selected lagged window | Fix each formula component on its own schedule. Reject inferred oil linkage, lag, slope, constants or conversion factors. |
| FRT-I | Freight, index-settled forward | Selected route/product freight forward curve | Declared route index average over the contractual settlement period | Retain route, vessel/product definition, quote basis and index calendar. Reject substituting another route or treating currency/day, currency/tonne, currency/container and lump-sum quotes as interchangeable. |
| FRT-W | Freight, Worldscale-quoted voyage | Explicit route/year flat-rate and Worldscale inputs | Agreed Worldscale percentage applied to the selected flat rate, with declared adjustments | Reject treating Worldscale points as currency/tonne or silently selecting a flat-rate edition; fixed and variable differentials must follow their declared treatment. |
| FRT-V | Freight, physical voyage or charter pricing | Explicit projected rate and separately declared cost components | Agreed rate or declared index formula, applied to the contracted quantity/time basis | Keep rate fixing separate from quantity/time determination. Reject inferred voyage duration, cargo quantity, fuel exposure or additional charges. |
| CGO-P | Cargo, commodity purchase/sale | Selected commodity forward curve or exact pricing recipe | Fixed price or assessment formula with explicit cargo pricing window | Keep loading, discharge, delivery and pricing windows distinct. Reject selecting a pricing month from a shipment date without an explicit rule. |
| CGO-D | Cargo, delivered-price composition | Explicit commodity plus freight/other declared component curves, or independent all-in curve | Agreed all-in price or explicit component settlement formulas | Preserve each component’s units, dates and fixing state. Reject adding freight twice or inferring freight exposure from an all-in mark. |
| SYN-P | Any supported commodity, assembled curve | Published exact recipe and source versions | Explicit component fixing rules or separately declared settlement index | One fixed constituent leaves other constituents live. Reject freezing the entire recipe, inferring component settlement from the recipe name, or silently changing recipe versions. |
| FIXED | Any supported commodity, agreed fixed unit price | None for the unit price | Agreed price | Market-source changes cannot alter the agreed unit price; cash can still change with explicitly variable quantity, FX or charges. Reject contradictory floating-price terms. |

These cases do not admit every combination. A monthly delivery product may use
daily observations; GAS-M specifically means a single externally published
monthly fixing. GAS-A is a different contract: a monthly average whose daily
components were part of its terms from the start. Its valuation may expand into
actual-plus-projected daily contributions without changing the booked contract.
A whole-month forward quote and a remaining-month quote are not interchangeable. A dated futures
contract can supply prices for daily observations only with explicit selection and
sampling semantics. Contract expiry does not imply completion of delivery, index
publication, payment or outstanding exposure.

LNG is a commodity/product family; freight is a transport service and can also
underlie a financial contract; cargo describes a shipment/delivery structure that
can apply to LNG, oil, metals or other commodities. These are separate admission
attributes, not mutually exclusive commodity dropdown entries. The examples
above propose explicit contracts and do not assert universal market conventions.
Cargo dates may be windows or event-relative anchors. An unresolved required event
must remain unresolved under an explicit policy or reject admission; it must not
silently become the valuation date or a guessed loading/discharge date. Event
resolution and amendments must retain versioned lineage without silently rewriting
already fixed observations.

## TDD order and independent oracles

1. **Compatibility:** table-driven backend admission/rejection tests for the rows
   above. Exercise the same contract through UI metadata and booking admission.
   Missing mandatory terms must produce structured errors, never inferred choices.
2. **Gas monthly/daily:** book and reload one monthly observation versus three daily
   observations. For equal daily weights with actuals 10 and 20 and remaining
   projection 33, the price is 21. A monthly actual of 10 remains one observation
   and a price of 10. Intramonth valuation must not regenerate either schedule.
3. **Futures selection and sampling:** observations on opposite sides of an explicit
   roll boundary retain different concrete contract IDs after booking and reload.
   Later prompt rolls do not change either identity. D261's current half-open
   selection advances exactly at the selected roll boundary; test before/equal/
   after explicitly. It is not a universal exchange sampling convention. A
   requirement to sample the expiring contract at that instant needs an exact
   contract or a separately qualified selection rule. Sample daily settlements at
   the observation instant; require official final evidence for post-expiry samples.
   Earlier daily samples retain their daily value when the contract later expires.
   The selected anchor can precede publication: do not use the later publication
   to reselect prompt. Official settlement is not the last trade. Test non-trading
   days and the last-trading-to-final-publication gap with explicit policies;
   no new official daily settlement is fabricated after trading ceases. Carrying
   a final price into later observations is a contractual sampling rule, not a
   universal exchange rule. Retained corrections respect the knowledge cutoff.
4. **Different projection/settlement sources:** advance the futures expiry and
   index publication clocks independently for GAS-X. Only the declared observation
   publication fixes its payment input. Missing due actuals fail explicitly.
5. **Power:** for 1 MW across a synthetic 25-hour local day, energy is 25 MWh.
   Price the two repeated local hours at 100 and -20 and all others at zero:
   cash is 80, for both hourly and quarter-hour grids. Add spring DST, peak masks,
   holidays and independently calculated block weights before qualifying PWR-B.
6. **Synthetic:** final futures 3.2 plus live bases 0.1 and 0.2 gives 3.5.
   A 0.5 shock to the second basis gives 4; the final constituent and original
   scenario stay unchanged. Repeat through published recipe booking/reload and
   explicit separate-index settlement, not just an in-memory graph.
7. **Other commodities:** 2 barrels at 75 gives 150; 0.5 tonnes at 2000 gives
   1000. Cross-unit use fails. Add contractual averaging, prompt-date and
   grade/location admission tests before claiming OIL-A or MET-A integration.

8. **LNG:** an explicitly equal-weighted assessment pair of 10 and 14 gives 12;
   a declared additive adjustment of 1 gives 13 per energy unit. With 100 energy
   units, cash is 1300. Separately test an oil-linked formula with an explicitly
   typed coefficient and lagged window. Changing delivery month must not shift
   that window unless the contractual anchor rule requires it. Missing energy
   conversion or a required anchor event must fail, not create a guessed price.
9. **Freight:** a declared rate of 20 currency/tonne applied to 100 tonnes gives
   2000; a distinct charter rate of 1000 currency/day for an explicitly determined
   three days gives 3000. Reject cross-use of those quote bases. For an indexed
   rate, test partial fixing, route mismatch, publication delay and retained
   settlement-period weights independently of voyage dates. A synthetic WS150
   applied to an explicitly selected flat rate of 20 currency/tonne gives 30
   before any separately specified adjustments. Missing route/year rejects.
   For a financial freight position distinguish contract days/lots from voyage
   duration; daily variation margin must not be charged again at final settlement.
   Separately test a receive-floating/pay-fixed financial freight contract:
   index average 21 minus agreed rate 20, times a declared multiplier of 100,
   gives net contractual payoff 100 before discounting. This is not gross freight
   cash of 2100; reconcile any paid margin separately.
10. **Cargo composition:** explicitly compatible commodity and freight components
    of 75 and 5 currency/tonne give 80 and cash 8000 for 100 tonnes. An independent
    all-in mark of 80 has no inferred freight dependency. Fix the commodity
    component and shock unfixed freight by 1: the composed price becomes 81,
    while the independent all-in mark remains 80. Test loading-relative and
    discharge-relative windows, unresolved events and explicit amendments; never
    infer delivery terms or additional charges from a cargo label.

11. **Monthly average versus remaining month:** with three equal-weight pricing
    dates, one actual at 10 and an explicit remaining-period forecast of 16 give
    a whole-period price of 14. A whole-period mark already equal to 14 must not
    be inserted as the remaining forecast (which would produce 38/3). Qualify
    explicit decomposition/shaping before using monthly marks in daily pricing.
12. **Delivery clocks and price weights:** test gas-day boundaries and DST as well
    as power; reject an assumed UTC-midnight gas day. Keep a benchmark's declared
    publication/flow-date weights distinct from the physical quantity profile.
    A fixed price with measured quantity is a fixed unit price, not fixed cash.

For each integration slice, first run a failing behavior check against the
production entry point, implement the smallest shared backend change, then rerun
that check and relevant regressions. Compile errors or unsupported fixture data
are not evidence of a missing financial behavior. Keep each slice green before
moving to the next; do not add a parallel commodity-specific pricing engine.

Repeat admitted cases immediately before/at/after fixing, roll, expiry and
publication boundaries. Test delayed and corrected publications with retained
knowledge cutoffs, missing evidence, immutable scenario shocks, booking retries,
and load/reprice without schedule regeneration. UI, imports and agent entry
points must receive the same admission result.

## Existing building-block evidence

`ObservationCurveScenarioChecks.cs` covers the monthly/daily gas distinction,
partially fixed assembled graph, repeated-hour power arithmetic and incompatible
commodity units. Its native hourly case also exercises the production resolver
through 24 projected, 12 actual/12 projected and 24 actual prices: independent
means are 30, 20 and 15, and forward dependencies fall from 24 to 12 to zero.
Production power settlement gives cash 360 versus 300 for flat versus shaped
24 MWh deliveries under the same actual prices; this is a one-day numerical
qualification, not a monthly booking-to-payment integration test. `ObservationContractChecks.cs`, `ExpiryTransitionChecks.cs`,
`IndexFixingChecks.cs`, `PowerQuantityChecks.cs` and `ConnectedRecipeChecks.cs`
provide selector, final-evidence, fixing, delivery-weight and recipe regressions.
These are baseline checks of existing behavior, not newly red-to-green integration
claims. Persistence, common observation-source admission and the combined booked
pricing cases above remain to be qualified. LNG, freight and cargo rows are
planned acceptance cases; the baseline command below does not test those cases
or establish voyage, inventory, title-transfer or physical-delivery support.

Run the focused baseline with:

```sh
dotnet run --project tests/OpenEtrm.MarketData.Checks -- --observation-curve-scenarios
```

## Market evidence and qualification boundaries

Public specifications support the distinctions below; they do not activate real
products or supply redistributable price data. Production admission must pin the
applicable specification version, calendars and authoritative contract dates.

- **Settlement versus last trade:** CME distinguishes calculated daily settlement
  from the last traded price. A final daily publication is also not necessarily a
  contract's final settlement. [CME settlement guide](https://www.cmegroup.com/trading/about-settlements.html).
- **Gas clocks:** ICE TTF monthly futures stop trading before delivery; its daily
  products use gas days beginning at 06:00, with 23/24/25-hour lengths and explicit
  weekend/holiday strips. Thus neither a monthly label nor a date-only interval
  supplies a complete delivery rule. [Monthly specification](https://www.ice.com/products/27996665/Dutch-TTF-Gas-Futures/),
  [daily specification](https://www.ice.com/products/45436633/Dutch-TTF-Natural-Gas-).
- **Different gas fixing dates:** ICE Henry Basis combines a monthly Inside FERC
  index published in the contract period with the corresponding NYMEX contract's
  last-trading-day settlement. One constituent can therefore be known while the
  other remains unknown. [ICE Henry Basis specification](https://www.ice.com/products/6590136/Henry-Basis-Future/specs).
- **Power milestones:** EEX's published change notice explicitly distinguishes
  last trading, expiry, final delivery and clearing settlement, including cases
  where final pricing information arrives later. Treat this as evidence for
  distinct milestones, not a current universal power expiry formula.
  [EEX notice](https://www.eex.com/fileadmin/Global/News/EEX/EEX_Customer_Information/2024/20241218_EEX_Customer_information_Change_of_expiry_dates_for_some_financially_settled_power_contracts.pdf).
- **LNG pricing window:** ICE JKM uses publication days from the 16th two months
  before the contract month through the 15th of the preceding month, adjusted
  under its stated calendar rules. A December label does not imply December price
  observations. This financial contract does not prescribe every physical LNG
  sale's formula. [ICE JKM specification](https://www.ice.com/products/6753280/JKM-LNG-PLATTS-Future).
- **LNG quantities:** custody-transfer energy requires its own measurement and
  calculation terms; it is not a universal conversion from cargo tonnes.
  [GIIGNL custody-transfer handbook](https://www.giignl.org/resources/custody-transfer-handbook-6th-edition-2021).
- **Freight averages and margin:** Baltic describes monthly spot-index average
  settlement separately from daily forward-curve marking and variation margin.
  This supports partially fixed averaging and a separate margin ledger, not
  automatic equality between a freight derivative and a physical voyage invoice.
  [Baltic settlement guide](https://www.balticexchange.com/en/FFAS/How-to-trade-an-FFA/Part-four-Settlement.html).
- **Worldscale:** route flat rates and their annual revisions are explicit inputs;
  some differentials are not multiplied by the quoted percentage.
  [Worldscale methodology help](https://www.worldscale.co.uk/help).
- **Metals prompt dates:** LME supports daily, weekly and monthly prompts, with
  substitute dates and last trading separately specified. Generic monthly prompt
  logic is insufficient. [LME prompt structure](https://www.lme.com/en/Sustainability-and-Physical-Markets/Physical-market-benefits/Prompt-date-structure).
- **Cargo terms:** shipping cost responsibilities and delivery/risk points are
  separate; a named delivery term does not supply the commodity pricing window
  or justify a commodity-plus-freight decomposition of an all-in mark.
  [ICC delivery/risk explanation](https://academy.iccwbo.org/incoterms/article/place-of-delivery-risk-transfer-global-trade-contracts/).

No generic futures-versus-OTC rule determines whether a contract means the next
month, when it stops trading, or when its price fixes. Neither an exchange daily
settlement curve nor a physical forward curve is automatically interchangeable
with the underlying settlement-index series. Source roles and mappings remain
explicit even when names, units and delivery labels match.

## Power-specific acceptance refinements

A monthly power mark may already combine observed spot prices with projections
for the remaining intervals. EEX documents this construction for front-month and
front-week daily settlement prices. Consuming that whole-period mark as if it
priced only the remaining intervals double-counts the elapsed contribution.
[EEX settlement procedure, version 5.33](https://www.eex.com/fileadmin/EEX/Downloads/Trading/Specifications/Settlement_Price_Procedure/20260319_Settlement_Pricing_Procedures_v_5.33_EN.pdf).

A selected benchmark determines its averaging independently of the physical
volume profile. CME's PJM Western Hub peak calendar-month real-time contract
specifies an arithmetic average, a weekday/holiday definition and hour-ending
labels. Those terms must not be inferred from the word “peak” or from another
market's block. [CME chapter 176](https://www.cmegroup.com/rulebook/NYMEX/1a/176.pdf).

Add these tests before qualifying PWR-B and PWR-Q:

- Distinguish day-ahead, real-time and the declared location and price series even
  when interval labels and units match. A finer source needs a qualified
  aggregation rule; identical timestamps alone do not establish compatibility.
- For two one-hour intervals priced at 20 and 100, a flat 1 MW delivery gives
  cash 120 on 2 MWh and average 60. A shaped delivery of 2 MWh then 0 MWh gives
  cash 40 and a realized volume-weighted price of 20. Neither is a substitute
  for the other. All numbers here are synthetic.
- An observed day-ahead price may be known before delivery while actual metered
  quantity remains unknown. Price-fixed must not mean final payment amount.
- Retain provisional/verified price and meter revisions with separate cutoffs and
  the contract's finality rule. Include DST, negative prices, holidays, missing
  intervals, and zero-energy cases without inventing a weighted average.
- Do not change a monthly futures contract's identity into hourly contracts.
  For an explicitly interval-settled side, retain its interval schedule from
  booking; projections can be shaped from qualified monthly marks. For a side
  sampling monthly futures settlements, keep those source roles instead of
  silently substituting spot observations.

PJM exposes separate day-ahead and real-time market data, including distinct
interval series. The chosen financial contract determines which published series
and aggregation it consumes; raw dispatch frequency is not sufficient.
[PJM energy market data](https://www.pjm.com/en/markets-and-operations/energy).

## Implemented compatibility and retained-source checkpoint

The existing index-based admission path now uses the same qualified fixing
resolver for authoring discovery and submitted observation plans. A nonexistent
actual series, mismatched observation period or undeclared source cannot become
an offered compatible sampling. Every submitted observation still requires its
own fixing definition. Target and explicit override selections retain independent
projection and settlement references.

`SideObservationCompatibilityChecks` exercises monthly/daily target and override
cases through production preview and admission, plus commodity, currency, unit,
multiplier, cadence and fixing-metadata refusals. The missing-series case failed
before the resolver change and passed afterward.

`BookedScheduleLookups` now checks retained curve membership, quote ownership,
output units and mandatory override fixing bindings before returning decoded
sources. `BookedScheduleLookupChecks` round-trips executable bytes and verifies
two exact contract references, a separate settlement index and independent
publication instants. Missing override fixing and mismatched curve quote were
accepted before this change and are now refused; a nonmember contract also
refuses. Loading supplies no Side terms or prompt-selection clock.

Verification passed focused managed/API checks, the API build with zero warnings
or errors, and owned disposable PostgreSQL/HTTP observation and retained-program
suites. The latter exercises booking replay, exact weights, atomic rejection,
stored bytes, direct loading with normalized-content reads disabled, and current
source-authority checks. Independent cross-review found no remaining material
issue within this checkpoint. No schema or UI change is included here.

This closes validation gaps in existing dated Forward consumers. It does not
implement futures daily observations, separate observation/contract delivery
coordinates in booked source contracts, generic historical sampling, the common
booked valuation cutover, or commodity-specific LNG/freight/cargo workflows.
Those remain explicit integration tasks; this checkpoint must not be presented
as completion of the entire matrix.

## Booking integration: daily observations of dated contracts

The first additional executable consumer is a monthly OTC floating payment that
averages daily observations of explicitly selected monthly futures contracts.
The observation schedule and averaging stay independent of futures delivery.
An actual futures holding and its variation margin remain a different consumer.

The row storage separates side delivery and observation intervals; exact market
bindings identify the selected contract and its own delivery period. The existing
index-based Forward source remains unchanged. A distinct versioned
`contract_observation_price` source represents dated-contract observations within
the same floating-side schedule. Daily exchange settlements remain distinct from
published index fixings.

The new source variant must retain observation bounds, selection anchor, exact
curve/dated contract/quote, sample coordinates, official daily/final source roles,
and explicit post-expiry and publication-eligibility terms. The booking operation
resolves selectors once. Both existing retained-side valuation and direct program
loading must consume the concrete rows; neither receives a prompt selector.
Update the closed managed instruction schema, SQL binding/sealing checks and byte
correspondence together. Existing market-observation storage is sufficient; no
new price table is justified by this consumer.

For repeated samples of one series, use separate exact snapshots and merge their
connected exports. The shared engine already supports historical frozen inputs
and scenario-sensitive current inputs. Its one-selection-per-series/use invariant
must not be weakened to fit several observation dates into one snapshot.

Market observation timestamps and knowledge time already have separate roles:
`observed_at` is the market observation coordinate; certified commit visibility
controls when that fact becomes available. `received_at` is audit metadata.
A later-known fact can therefore retain an earlier market timestamp without
moving the sample to its receipt date. Keep the existing snapshot cutoff guards.
If a provider supplies a distinct publication timestamp, source admission must
retain its explicit meaning rather than reinterpret it as the fixing anchor.

The managed combined-source regression now passes for an earlier daily settlement of 10, a
later explicitly carried terminal settlement of 40 and a future projection of
30: equal weights give 80/3. A +3 shock to the future projection gives 83/3;
the two historical samples and the original scenario remain unchanged. It also
rejects missing final evidence, absent final-carry policy and daily/final role
confusion through the production graph compiler. Separate
before/after certified-knowledge tests must establish delayed publication and
correction eligibility; an in-memory sample test alone cannot establish them.

### Bounded source consumer

The first API consumer accepts complete monthly payment periods with explicitly
supplied daily observation intervals and fixing instants, arithmetic averaging,
and a direct futures daily-settlement curve. Exact-contract and prompt-offset
authoring both materialize concrete contract references. Normalized authoring
terms retain the selection for audit; reconstructed pricing consumes the stored
references. Observation evidence is separate from reusable side identity.

The source history policy is explicitly `visible_corrections_at_cutoff_v1`.
Historical actuals use terminal visible corrections at the exact fixing instant;
future observations use the selected current frame. A correction that is not final
does not revive an earlier final observation. An actual at valuation time is fixed,
while a future observation of the same series remains scenario-sensitive.
Post-expiry carry requires both the selected source policy and the curve's explicit
final-settlement rule. Missing final evidence refuses; earlier daily observations
keep their own prices.

Migrations 0131–0132 add the closed executable source operation and exact-version
authoring relationships. They use existing price observations. SQL sealing and
managed loading check the same source, role, series, quote and expiry contracts.

The executable checks cover stored-program base/shock/base values of
80/3, 83/3 and 80/3, and a synthetic month whose prompt-selected daily values sum
to 806. An exact-contract variant carries its final settlement to produce 848.
Changing one historical value by 10 produces 816 under later knowledge and
preserves 806 under the earlier retained cutoff. Partly fixed booking reuses the
same instrument identity. API and SQL both reject an ineligible prompt prefix
and superseded historical evidence. The first checkpoint excluded these positions
from risk; the retained-source extension below admits supported positions.

Verification passed the managed baseline, API checks, scheduled-swap HTTP/storage
suite and retained-program suite on owned disposable PostgreSQL databases.
Independent reviews of temporal sampling, numerical execution, storage and risk
boundaries were completed with the reported findings resolved.

This consumer does not expose arbitrary observation windows, exchange-business-day
averaging, holiday carry rules, synthetic source recipes, or alternate settlement
indices through the new source variant. Existing index/custom-projection paths
remain separate. Interactive and queued risk use retained dated-source evidence;
financial settlement uses the existing gross determination and cash lifecycle.
Automated-close risk uses shared connected pricing but still refuses these source
positions: its retained job contract stores native factors, not observation sample
manifests. That persistence boundary requires separate qualification.
The general booked-program execution cutover and the remaining
commodity matrix are still work; this is not an actual futures-position or
variation-margin implementation.


### Ticket authoring and published synthetic projections

The scheduled ticket separates published-index observations from dated futures
settlements. Commodity and exact market revision filter backend-qualified source
choices. The dated-source controls require an exact contract or explicit prompt
offset, roll rule, expiry policy, correction policy and official settlement role.
A new schedule also requires an exact fixing clock, local time and explicit
rejection of ambiguous or nonexistent local times. The backend generates the
calendar-day observations and resolves contracts; TypeScript transports the
returned terms. Editing the inputs invalidates the preview. Loading retained
observations does not infer a generator clock or rerun prompt selection.

Clock generation reuses the pinned fixed-offset/IANA clock implementation. A
complete month includes every calendar day, including weekends; this is not an
exchange-business-day averaging convention. Source preview also enforces the
existing side invariant that fixing must not occur after payment.

Published synthetic projections continue through the indexed source path. Once
all observations are actual, the ticket qualifies the exact published recipe's
output shape, quote and mapping without demanding unused forecast prices. The
backend still checks authorized published versions and actual fixing inputs;
missing or incompatible bindings never become ordinary curve projections.
Synthetic lifecycle checks use an independently calculated forecast cash of 9.5,
retain that version after a successor is published, and subsequently use an
actual index of 3.5 for cash of 50. Wrong units, output positions and unavailable
versions are refused even after all observations have fixed.

Ticket qualification includes browser preview, booking and reopening, explicit
source selection, stale-response rejection and narrow-screen receipt inspection.
The existing indexed observation, bulk-edit and power-quantity ticket checks also
pass. Managed, API and disposable-database checks cover the source and synthetic
projection contracts; independent review findings were resolved.


### Retained risk and financial cash

Migration 0133 extends the existing risk sample tables with the explicit consumer
kind and a flag for an observation fixing exactly at valuation time. Historical
samples, current fixed observations and current live projections remain distinct,
even when they refer to the same dated contract. SQL admission checks samples
against authorized booked schedule bindings and the job's book scope. Replay
loads the retained observation IDs and verifies the complete graph manifest; it
does not reselect historical facts after a correction.
Existing normalized recipe samples migrate explicitly as recipe/non-current-fixed
uses. New admissions require the extended manifest, so migration and API rollout
must be coordinated. The existing exact engine-version rule still applies to
queued jobs; this migration does not enable a new binary to execute old-engine
jobs. Completed results remain readable.

The synthetic daily month has 13 remaining February-contract samples and four
March-contract samples at January 15 08:00 UTC. With total quantity 31 and an
arithmetic 31-day average, native-price deltas are exactly 13 and 4. At the explicit
16:00 fixing, the February exposure falls to 12; that newly actual observation
remains fixed under shocks. A later preliminary correction cannot replace a
queued run's admitted final evidence. A current run must instead report the
missing final input until a final correction is available.

Financial determination requires every contractual fixing to be due and every
price to be final. Knowing an expired contract's terminal settlement early does
not advance a later observation's fixing instant. The settlement contract hash
retains exact source bindings; corrected observations change input evidence,
not the payment identity. Existing policies retain gross occurrences, currency
rounding, explicit correction due dates, idempotent commands and cash allocation.
This is financial swap settlement, not futures variation-margin processing.

Only undetermined payment occurrences demand source prices. A posted determination
remains cash when a later correction leaves the source temporarily without a final
price; a new determination still refuses that missing input. Portfolio demand is
combined per trade binding, so a paid trade cannot suppress an unsettled trade's
prices even when they share an instrument. Fully fixed or determined positions
have zero native-price sensitivity. Due published-index observations with missing
actuals are reported as unavailable positions before risk calculation.

The equality-boundary check uses the same official settlement record for today's
fixed observation and future live observations. Both uses survive normalized SQL
storage and queued replay with their different shock behavior. Negative checks
cover wrong contract, series, observation instant and book scope, as well as
revoked reference access.

Verification of this extension passed all 646 managed core checks, API checks,
the full scheduled-swap HTTP/storage and upgrade suite, and the connected
portfolio suite including weighted, nested and partly fixed synthetic curves.
Database checks used owned disposable PostgreSQL databases. Independent review
covered temporal replay, numerical freezing, settlement identity and authorization.
