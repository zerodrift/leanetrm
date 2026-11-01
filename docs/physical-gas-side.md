# Physical gas booking and pre-delivery valuation

Physical gas is entered in the [deal ticket](deal-ticket.md) (D281). Its booking is
the independent-side composition below: one to four physical sides with payment and
fee sides. A purchase or sale at one point is the composition of one physical side.

The single-point template and its operations (`gas/preview`, `gas/instructions`,
`gas/book`, `gas/status`, `gas/directory` and `gas/editor`) are removed; no new
single-point trade can be booked, and migration 0156 revokes the runtime's call of
the retired booking entry, `security.book_physical_gas`. `gas/catalog` remains. Stored
single-point trades stay readable: the Blotter values them and the trade read and the
deal ticket open them read-only, all as the one-physical-side composition they are,
from their retained programs. Their v4 command supplied pipeline, zone, delivery location and product;
the backend resolved the valuation mapping. Delivery dates were independent inputs.
Reopening loads their stored executable programs and resolves source metadata from
authorized typed references. It never rebuilds an instrument or schedule for booked
execution. Both profiles value through the existing exact schedule interpreter and
market-source resolver; no second gas arithmetic engine is used.

The stored single-point profile admitted one forward month or a 1–31-day subset, a selected
fixed-offset gas clock and start minute, daily or equal-day period-total fixed
quantities, receive/deliver direction, fixed consideration, an optional fixed
per-unit fee with its own pay/receive direction, an explicit UTC payment instant and explicit no discounting. It
admits no fuel adjustment. The independent-side profile below admits explicit
fixed-quantity fuel links. Floating cash, operational fuel treatment and broader
captured clocks remain separately bounded capabilities.

Physical settlement, inventory and gas scenario risk remain unsupported. The common Blotter includes both gas profiles through the native retained-schedule pricer, with a distinct contractual PV measure. One immutable gas instrument is one position binding; physical-side quantities are never summed into a receipt-plus-delivery total. Missing gas market context or started delivery produces an explicit unavailable valuation, while retained terms and daily operational quantities remain readable.

New trade offers Future, Swap, PPA and Physical gas in the same panel. Physical gas opens the deal ticket, whose operations delegate to the composition's preview, booking, status and idempotent recovery operations; the other forms use their own. A confirmed booking refreshes the common Blotter and opens its exact position when uniquely identified. A booked composition's day quantities are recorded in its physical side's tab of the ticket. PPA lifecycle operations remain native and open in the same panel. Unknown booking outcomes block replacement requests and retain the actor-owned command across ticket remounts, including when supplementary browser storage fails.

Mixed interactive and queued risk reports retain the exact gas bindings as `physical_gas_risk_unavailable` exclusions while repricing supported financial/PPA holdings. A gas-only scope cannot return a successful zero risk result. Migration 0150 supplies the minimal temporal gas position inventory to leased risk workers without granting raw gas table access. Automated closes containing gas remain explicitly unsupported. Operational quantity revisions do not change contractual PV or create extra positions.

## Independent sides and fixed quantity links

The deal ticket's Add side offers physical, payment and fee sides. The explicit
profile is `fixed_gas_composition_v1`, using the existing v4 context convention and
`gas/composition/preview`, `/book`, `/status` and `/editor`, which the ticket
operations delegate to. A preview and a booking are held to the gas setup their
context resolves: a request naming another setup, or none where the context resolves
one, refuses with `ticket_input` on the `gas_setup` field; a retried booking still
receives its first outcome.

Admit one to four physical memberships and up to four fixed-price cash/fee
memberships. Receive/deliver belongs on each physical membership; pay/receive
belongs on each cash membership. There is no trade-wide direction. Each physical
side selects its own PZL/product and 1–31-day delivery profile within one forward
month. Independent sides can have different dates, quantity bases and clocks.
All memberships require the same exact native quantity specification and currency;
there is no implicit physical-unit or FX conversion.

An independent physical side supplies a positive contractual daily quantity or
equal-day period total. A linked side names another physical side and supplies
an explicit no-fuel, gross-less-fraction or gross-divided-by-one-plus-fraction rule.
The link consumes its source's delivered quantity; its own receipt stage is that
source output, and its own delivery stage applies the selected rule. Fuel is
receipt minus delivery. Linked profiles must have matching operating intervals,
product, clock and quantity basis. Cycles, missing sources, duplicate codes and
physical fan-out refuse. Splits require a separately qualified allocation rule.
These links do not prove route continuity, service rights or custody/ownership
semantics for a transportation contract.

Cash and fees select a physical source and its receipt, delivery or positive fuel
stage, their own fixed per-unit price, payment instant and direction. An eligible
configured usage tariff can supply a delivery-based fee price. A tariff cannot
silently price a receipt/fuel stage or overwrite a manual price. Selecting the
fuel stage bills a separately declared charge on that reduction; it does not
change in-kind fuel into cash-paid fuel. Fixed nominal/capacity charges, floating
cash, actuals and settlement are not admitted by this profile.

The first independent physical amount defines the external holding quantity.
Every side retains a normalized one-unit executable schedule; exact positive
membership ratios preserve the native quantities separately from direction.
The side identifiers, quantity source/stage, fuel rule and reduced integer ratios
are stored in `trading.gas_component_terms`, with exact membership, source-slot,
assignment and tariff foreign keys. Final SQL admission independently resolves
the quantity relationships and validates every unit program before atomic
publication. It also retains the original party, source-visibility, effective
revision, idempotency and cross-family command-collision checks.

Fixed links are materialized at booking. Reopening directly loads the retained
unit programs, multiplies each quantity and PV by its stored membership ratio
and the holding amount once, and reports receipt/delivery/fuel reconciliation.
It does not regenerate a Side or recompute link ratios from current setup. Dated
meter attribution uses each booked physical point and setup version. Exported
quantities remain contractual, not readings or allocations. Linking operational
actuals or future elections requires a separately admitted input-port profile.

Synthetic period-total oracle, with no discounting: receipt 1,000 at mark 3.20,
delivery 980 at mark 3.10 after 2% gross-based fuel, and a payable fee of 0.10 on
delivery. Contributions are +3,200, −3,038 and −98; PV is 64, and fuel is 20.
Changing only the fee stage to receipt makes PV 62. With the alternate divisor
fuel rule, the same sources produce exact PV `3200/51`; no intermediate rounding
is introduced. The shared managed fuel rule also serves the existing gas
quantity foundation.

The ticket preserves exact reviewed commands across uncertain booking replies
and reload, invalidates review after any term/link change, and provides a durable
composed-trade directory with retained daily schedules, price/payment inspection
and independently signed contributions. Shared mixed-product reports still
refuse scopes containing gas; the new composition cannot disappear from totals.

## Admitted terms and their consumers

| Term | Executable use |
| --- | --- |
| Pipeline, zone, delivery location and product | Validate the versioned physical path and commodity applicability; resolve one exact valuation mapping; retain the full path in side identity and source metadata. Invalid combinations refuse. |
| Gas clock and delivery period | Generate exact operating-day UTC intervals, retain those grids and enforce the pre-delivery boundary. Captured clock rules participate in economic identity. |
| Quantity basis | Admit normalized daily or equal-operating-day period-total quantities; generate exact delivery quantities. Hourly-rate quantities remain supported by the quantity primitive but are not admitted by this Side profile. |
| Fuel convention and fraction | Apply the selected exact receipt-to-delivery formula once; use its net quantity for physical value and validate cash allocations against that quantity. |
| Valuation assignment identity and effective period | Require exactly one assignment per whole gas day, no duplicate keys, missing coverage, overlap or unused assignments. Retain and resolve the complete assignment definition. |
| Market contract, index, quote and optional fixing | Resolve prices using the existing period-price selector under the certified context, including publication eligibility and missing-fixing refusal. |
| Physical-value price adjustment | Add the compatible typed adjustment before multiplying by quantity. It changes physical valuation, not fixed contractual consideration. |
| Cash price, allocation and payment date | Existing FixedSide/FloatingSide schedules calculate and discount consideration and usage charges independently. |
| Membership direction | Reverse delivery and cash contributions once; external quantity scales the normalized result once. |

Pipeline, zone and point are existing typed location records linked by
`ref_data.pipeline_zones` and `ref_data.pipeline_delivery_points`. Commodity
applicability reuses `ref_data.commodity_locations`. These reference relationships
also appear in the reference browser. The Side holds their exact references,
not duplicated location attributes. Direct-meter attribution and preparation of unsubmitted contractual schedules are supported below. Meter measurements, allocations, a complete gas-product
catalog and transport eligibility remain unsupported. Callers supply qualified reference definitions and one
compatible native quote unit; volume-to-energy conversion remains unsupported.

## Physical location and valuation are separate

Pipelines and zones are network roles of the existing versioned location master.
The membership tables establish which zones belong to a pipeline and which
operating points belong to each zone. They contain no trade dates or prices. The separate gas delivery-convention
reference owns the clock and operating-day start for an admitted path/product;
changing a market curve cannot silently change its physical gas day.
They are reusable for pipeline-delivered commodities; ports, terminals and other
physical arrangements need their own qualified relationships when consumed.

A gas valuation mapping links an exact physical path and product to a market
curve and supported adjustment. The curve's market location can differ from the
physical delivery point. Several physical points can share one curve and have
different explicit adjustments. A missing mapping leaves the physical point
visible in reference selection but blocks pricing. The current profile resolves
one native currency from its explicitly selected mapping; multi-currency mapping
selection remains a separate extension.

The ticket presents physical-side fields, separate consideration and optional
usage-fee sections. Product and pipeline filter the admissible zones and points.
Changing them never changes delivery dates. No month is encoded into a physical
location option. The existing one-forward-month admission bound remains explicit;
an independent date range does not imply unsupported multi-month booking.

Buy/sell appears on the physical-side section, not the trade header. The current
purchase/sale template still constructs one physical side and opposing consideration, with a separately directed optional usage fee;
it does not yet support arbitrary additional physical or financial memberships.
The [composition gate](physical-gas-field-contract.md#reference-identifiers-valuation-and-composition)
requires explicit per-member directions, quantity links and transport/path
semantics before expanding this booking contract. Meter observations, allocations and accepted nomination workflows remain unsupported.

Booked inspection shows the retained physical path and exports daily quantities
from the executed `schedule_quantity` roots. It never regenerates their schedule.
Display-only reference revisions must copy network memberships and include them
in the economic-equivalence proof, so a name edit cannot remove or change a path.

## Quantity and lifecycle boundaries

`PhysicalGasSide` requires a one-unit receipt profile. Its external quantity basis
is interpreted by the explicit daily or period-total convention. This prevents a
booked magnitude from being embedded and multiplied again accidentally.

`PhysicalGasInstrument` admits one dated cash period per consideration/usage
component. Each must cover the complete physical term in the same native unit
and currency, with an allocation exactly equal to the generated net delivery
quantity. This is a fixed contractual quantity relationship validated at
construction, not a dynamic operational quantity port. Future actuals must not
be implemented by silently changing these immutable cash allocations.

The admitted physical measure is future delivery market value, with each day's
value date at the exclusive end of its gas day. It is not a payable cash flow.
Its executable roots distinguish `physical_value` from `contractual_amount` and
its evaluated contribution kind is Delivery. Once any admitted delivery starts,
the profile refuses with `gas_delivery_lifecycle`. It cannot manufacture actual
delivery, retirement, inventory or overdue physical treatment.

The supplied discount operation is explicit. A value-date change can affect
discounted physical value independently of cash payment dates. Future due cash
continues to obey its existing payment-side policy; this profile does not create
a new physical settlement policy or enroll a trade in swap settlement.

## Execution and identity

`Side.GenerateSchedule()` is parameter-only. Draft schedules compile once on
first executable use and retain immutable typed instructions, sources, exact
grids and named outputs. Serialized executable bytes can be loaded and run
without regenerating Side parameters. Gas booking/publication remains a separate
integration gate, including independent database verification of the new binding
kinds and family.

`SideValuationContext.ForPhysicalGas` requires an explicit certified unshocked
base, a compatible scenario, discounting and qualified assignment definitions.
Source evaluation checks the same model, snapshot inputs and resolved context.
It verifies the complete lookup against its assignment, including fixing time;
another metadata array cannot reinterpret when a price becomes fixed.

Assignments form a separate pricing-binding identity. Instrument identity
retains contractual composition while instrument pricing identity aggregates
every ordered side pricing binding. The identity map refuses reuse of equal
contractual economics with different assignment bindings. No global cache stores
resolved prices, quantity states, authorization or trade lifecycle.

## Numerical and refusal checks

The synthetic one-day purchase, DF=1 and external quantity 1,000, gives:

| Component | Amount |
| --- | ---: |
| Delivery at 3.20 per native unit | +3,200 |
| Fixed consideration at 3.00 | −3,000 |
| Usage charge at 0.10 | −100 |
| Combined pre-delivery PV | +100 |

A +0.10 physical mark shock gives PV +200; a −0.10 shock gives zero. Reversing
direction gives −100 at the base mark. With 2% of gross retained as fuel, 980
units deliver and matched cash gives PV +98. Under the alternate net formula,
the per-unit physical value remains exactly 160/51 at a 3.20 mark. A two-day
period-total profile retains one-half quantity per day, rather than duplicating
the total.

The managed suite also covers independent adjustments, assignment/quantity
mismatches, gaps/duplicates, changed pricing identity, changed model/snapshot,
started delivery, cancellation, final fixing shock neutrality, missing due
fixings and modified fixing-time metadata. The existing fixed/floating lookup
compiler is shared with gas and retains existing family behavior.

## Persistence and current authority

`market_data.gas_valuation_assignments` retains the exact physical path, product,
valuation curve and price adjustment. `ref_data.gas_delivery_conventions` owns the
reference clock and gas-day start independently of that market mapping. Every
field is consumed during metadata resolution and independently revalidated by
final database admission. Typed gas instrument and membership records own the
contract dates, quantity basis, fixed cash prices and payment date. A trade
revision owns its book, quantity and direction. Common booking facts retain the
server-owned human identity and UTC booking date.

Final admission compares every allowed row, edge and output root with the
selected terms. It validates exact rational values by cross-multiplication,
complete native gas-day grids, exact header revisions, the forward month,
quote/index/fixing metadata and canonical assignment. Unknown economic fields
reject. Program bytes must separately match the normalized relational schedule.
Private staging is not a booked position. All sides publish in the trade's
transaction or none do. Retries preserve the original operation and payload;
changed terms or ownership conflict. Recovery receipts are actor-bound. The
browser retains an uncertain request across reloads and reconciles status before
clearing it.

The next gates are shared blotter/portfolio integration, operational quantity
ports and physical completion/settlement policies. The [field contract](physical-gas-field-contract.md)
and [delivery plan](physical-commodity-test-plan.md) retain unsupported terms as
requirements, not dormant Side fields.

## Shared physical pricing configuration

`market_data.gas_valuation_groups` gives a pipeline/product group one curve and
one explicit price adder in that curve's quote. Each point assignment names its
group and a further point adder. The total adder is generated in PostgreSQL;
a complete foreign key pins the group, pipeline, product, curve and group adder.
Duplicated linkage columns cannot diverge. There is no implicit nearest-zone
match or pipeline fallback. Explicit assignment allows several points in one
zone to share a group, points in different zones to share it, and another
pipeline to have a separate group over the same curve.

For the admitted additive profile:

```
physical mark = selected curve value + group price adder + point price adder
physical contribution = signed contractual delivery quantity * physical mark
```

Group and point adjustments change physical valuation only. They do not issue a
payment, change fixed consideration, reduce physical volume or select a tariff.
Percentage valuation adjustments remain unsupported in this profile. Physical
fuel remains a separate quantity rule; the existing managed fuel foundation does
not enable it in booking. Configuration must distinguish market-location basis
from separately owed service costs rather than entering one expense twice.

Settings → Gas delivery setup provides economic authoring for the admitted
profile. The general reference browser retains the imported master relationships;
a published setup's own point and meter relationships are inspected in the gas
setup editor and consumed by its pinned trades. This is separate from the
reference browser's display-name editor. No unrestricted row-edit endpoint is
introduced.

## Meter-attributed contractual schedules

`ref_data.gas_meter_assignments` maps an exact path/product to a pipeline-scoped
external identifier and native quantity specification over half-open operating
dates. The direct-meter profile rejects overlapping assignments to a point,
concurrent reuse of the external identifier within a pipeline, and a quantity
specification belonging to another commodity. Authors serialize under read
committed isolation; other isolation levels refuse before insertion. Grouped
meters and allocation weights are not admitted by this direct-meter profile.

Meter identity is available before flow. Evaluated daily schedule views join it
by operating day under the schedule's exact reference revision. Their quantities
are explicitly labeled `contractual`. Booked views load their quantities from
retained executable roots; meter attribution neither regenerates the schedule
nor replaces its contractual quantities.

Preparing an unsubmitted contractual meter schedule (`gas/instructions`) belonged
to the single-point template and was removed with it. Physical MTM remains
available at an unmetered commercial point. Actual readings and corrected
allocations will be separate observations with purpose-specific eligibility; they
cannot overwrite the immutable Side, meter reference history or stored contractual
schedule.

## Configured usage tariffs

`market_data.gas_usage_tariffs` pins a pipeline, product, exact quote, nonnegative
rate and half-open delivery validity period. The current installed charge basis
is contractual delivery quantity. Selecting a tariff resolves the usage-fee
Side's price; supplying both a tariff and a manual fee refuses. The entire trade
period must be covered without proration or rate fallback.

The v4 command explicitly supplies nullable `usageFee`, `usageTariffCode` and
`usageFeeDirection`. An active fee requires pay or receive; no fee requires a
null direction. Physical direction does not determine that fee direction.
Final database admission independently resolves the tariff, checks the executable
cash program against its rate and stores the exact tariff code and fee sign.
Book/reopen preserves that source and sign. Tariff changes require a new version;
existing bookings retain their pinned version.

This is a bounded per-unit charge consumer, not a complete transport tariff
engine. Reservation/capacity charges, seasonal splits, service priorities,
route-specific rates, tiers and actual-volume billing require additional admitted
profiles. A pipeline-level tariff does not itself prove a transportation right.

## Draft, validate and publish gas delivery setup

The local **Gas delivery setup** page under Settings edits a complete snapshot of
point relationships, shared valuation groups, meter assignments and per-unit
tariffs. Select existing master locations, products, clocks, quantities, quotes
and curves. Creating a new location-master identity is a separate reference-data
workflow. Master records and market observations are not cloned or rebound by
publishing a gas setup.

1. A market-definition editor starts from the current published setup or imported
   definitions, changes records and saves an immutable draft.
2. Select a delivery period of up to 31 operating days and validate the saved
   version. Preview shows each point's group, curve, index, quote, separate price
   adjustments and number of operating days without a meter assignment.
3. A market-definition approver opens that exact saved version, validates it and
   explicitly publishes. Final SQL admission revalidates the full snapshot.
4. New tickets consume the latest visible published setup for the exact base
   bundle. Each v4 booking explicitly pins nullable `gasSetupId`; null identifies
   the imported setup. Reopening retains the booked setup, physical clock,
   assignments, tariff source and meter lineage.

`market_data.gas_setup_versions` owns the immutable draft payload and published
version, with exact market-definition and reference-master foreign keys. Published
points and meters have typed setup-specific relationships; groups, assignments
and tariffs retain their exact setup foreign keys. Published relationships are
never resolved through a current location override. Point and zone membership
checks reject contradictory parents; group/curve/product compatibility, exact
rates, meter overlaps/identifier collisions and tariff validity are checked
before publication. An intentionally unpriced point or unmetered commercial
point can remain in the setup. Missing pricing blocks MTM; missing compatible
meter coverage blocks instruction preparation, not contractual valuation.

Each saved draft retains its base setup. Publishing a draft based on a superseded
head refuses rather than merging changes implicitly. Save/publish requests have
actor-bound idempotent outcomes; the browser retains the original command through
uncertain responses and reload. Unsaved edits have a leave prompt. Changing a
preview date or a definition invalidates its previous review.

Setup publication uses its own serialized, commit-certified knowledge stream
under the shared context-capture gate. A retained context excludes later setup
publications, including transactions that were uncommitted when it was captured.
Risk workers retain their existing lease and pinned-context gate. Setup authoring
is unavailable in historical mode. Publication order selects the current setup;
future-dated whole-setup activation is not part of this profile. Meter and tariff
validity remains explicitly dated by operating period.

For authored meters, exported attribution identifies `(market catalog, setup ID,
meter code)`; imported meters retain their original reference key. Reusing a
meter code in a successor setup cannot disguise a changed external identifier.
Operational measurements and allocations remain separate capabilities.

## Local verification

The complete managed runner passes 653 reported core checks, including gas
quantity, price-shock, fixing and retained-program cases. The focused stock
PostgreSQL check is `tools/run_preview.py --physical-gas-check` with the installed
`--pg-bin` and `--dotnet` paths. It books the one-physical-side purchase through the
composition operations and verifies the synthetic purchase oracle: physical value
+3200, fixed consideration −3000, usage fee −100, total PV +100. It also verifies
independent physical and fee directions, daily/period-total quantities, the
optional fee, booking/reopen parity, idempotent retry, historical exclusion and
read-only authority. The PZL checks also prove that the same location prices
January and February, changing the configured physical zone changes PV,
mismatched paths and unpriced points refuse, a configured tariff prices PV80 and
refuses outside its period or pipeline, and the delivered point may differ from the
market location. A non-midnight gas day is exercised through preview. The reference
checks verify shared group pricing, independent point/pipeline adders, meter
overlap and identifier collisions, wrong commodity units, inconsistent group
linkage, isolation refusal and display-clone identity preservation; additional
permissive policies cannot bypass current authority. Add `--browser-check --browser`
to exercise the deal ticket's gas entry: one gas entry in New trade, side-level
direction, points filtered by zone and only valued points offered, and a fee's
proposed tariff. Add `--gas-setup-check` to qualify economic authoring, configured
point/clock changes, historical setup visibility, tariff pricing and pre-publication
booked price/meter preservation. These are bounded local checks, not production or
complete gas lifecycle qualification.

Stored single-point trades are read, not booked. The mapping of their retained terms
to a one-physical-side draft is checked without a database (`--gas-ticket`), and
`--deal-ticket-check` writes one stored single-point booking through the owner
connection, check-only, from a booked composition's retained programs, then opens
it read-only through `tickets/trade` and compares its draft and schedule with a
ticket preview of those terms.

The multi-side integration runner is `tools/run_preview.py --gas-composition-check`
with the same explicit SDK/PostgreSQL paths. Add `--browser-check --browser` for
the complete ticket and uncertain-response recovery, and `--gas-setup-check` for
publication/rebinding regressions. Independent cases cover the PV64 and alternate
fuel oracles, receipt/delivery fee stages, independent later-month daily quantities,
invalid links, exact replay and direct retained-program reopening. Managed
scenario checks prove a +0.10 common-curve shock changes PV64 to66 because the
net physical quantity is20; contractual fixed fees remain unchanged.


The [daily delivery quantity workflow](physical-gas-daily-quantities.md) now records
separate trade-owned operational revisions and resolves native quantity inputs
from retained gas grids. It leaves this fixed contractual PV profile unchanged.

The integration checkpoint preserves dated-contract observation sources and the
mandatory published-fixing admission rules in the shared schedule runtime. Local
verification passed 651 managed core checks, 925 web checks and 44 physical gas
browser checks. The populated prior-schema upgrade check applies migrations
0142–0146 and proves existing reference, market, book and party rows remain
unchanged. That check does not establish replay of an existing financial trade
or its program history across this upgrade. The shared-workflow increment below
supersedes the earlier gas-bearing workspace refusal.

Reference catalog qualification includes 208 exact-version projection and label
checks. Path tables retain their composite primary-key ordering; gas product
fields resolve commodity labels while futures product fields retain their own
reference contract.

## Independent gas market selection

An installation may explicitly configure `gasMarket` with one sealed definition
revision distinct from the primary market and dated observation/contract selections. Gas setup authoring, physical
booking previews and retained schedule preparation use that revision and its exact
reference version. This does not replace the primary financial or power market,
redirect collection sources, or copy their operational series.

Without this configuration, gas uses the primary market as before. With it, a
missing or ambiguous dated selection refuses; primary price inputs are never a
fallback. The selected certified knowledge cutoff and valuation instant still
bind every read. A financial close does not certify the independent gas inputs.
Explicit gas pins remain exact; a view already carrying the latest-correction
policy resolves corrections only inside its certified cutoff. Daily quantity
records continue to read their original retained contractual programs.

Reopening a booked gas trade uses its visible retained definition header. Trades
booked on the primary market continue to use their original source after gas is
configured separately. Older independent gas revisions must retain their dated source selections in
`retainedGasMarkets`. Booked reads select that source by the authorized instrument
header, including its exact reference revision. Missing or ambiguous retained
sources refuse explicitly; changing configuration does not rebook a trade.


## Shared trading workflow qualification

The common workflow passed 932 web checks, managed API checks and disposable
PostgreSQL integration checks. The mixed fixture covers futures, financial swaps,
heat-rate swaps, PPAs and both gas booking profiles. Ordinary, streamed and exact
individual reads preserve retained position identity and native value measures.
A real leased worker reproduces mixed financial risk and exact gas exclusions;
a retained manifest missing those exclusions fails instead of repairing itself.

Browser qualification passed 18 shared-workflow checks at a 1440 px desktop,
45 gas booking/quantity/setup checks and 39 PPA booking/lifecycle/inspection
checks, including 390 px PPA layout. A lost accepted gas response with browser
storage unavailable survives editor detach/reopen and status reconciliation
without another booking. Closing a ticket clears its browser page-leave warning.
Nominations remain operational revisions of the same
trade, with contractual quantities unchanged. Exact PPA market frame selection
remains explicit when the desk instant cannot uniquely identify one bundle;
cancelled loads cannot reopen an old trade after an editor reset.

Run `tools/run_preview.py` with
`--unified-trading-check --browser-check`, `--gas-composition-check --browser-check`
and `--ppa-bookings-check --browser-check`, supplying the local PostgreSQL,
managed SDK and browser paths required by the preview tool. These checks do not
qualify gas scenario risk, operational PV or physical settlement.
