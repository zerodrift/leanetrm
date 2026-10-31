# Physical commodity delivery through executable acceptance cases

Status: proposed local implementation plan, 30 October 2026. No new product,
settlement policy, migration or publication is authorized by this document.
Baseline: `e527b35a8b056a160cab0c75be6a8d7495692669` on master.

Implementation branch subsequently fast-forwarded to `3f24e1d` before code
changes; that update changes market presentation, not the scanned Trading core.
The first [gas quantity increment](physical-gas-quantities.md) implements the
contractual quantity portions of G04/G05, with explicit clock and fuel terms.
The subsequent managed [pre-delivery profile](physical-gas-side.md) implements
G01/G02 and fixed-contract fuel/quantity composition with exact schedule execution.
It does not close P0/P1, introduce database booking or claim the gas lifecycle.

Introduce physical gas first, then crude, LNG cargo and freight through complete
trade workflows. Preserve Instrument → independent Sides → retained executable
Schedules. Extend the existing pricing and settlement contracts through qualified
consumers rather than creating a second commodity pricing or payment engine.
All examples below are synthetic proposed acceptance oracles, not implemented
tests or market conventions.

## Existing implementation and extension points

| Existing contract | Evidence | Implication |
| --- | --- | --- |
| Independent reusable sides and explicit quantity scaling | `Trading/Instruments.cs`, `IndependentSideChecks` | Retain signed memberships and trade-owned size; review the current blanket SidedInstrument scaling declaration before admitting size-dependent fees. |
| Complete retained calculations, typed bindings, grids, ports and roots | `Trading/ScheduleDefinition.cs`, `RetainedScheduleChecks` | Extend named supported families and operations; no separate commodity schedule database. |
| Booking-time executable artifact and direct loader | [Booked Side programs](booked-side-programs.md), `ScheduleProgramExecutionChecks`, `ScheduleMarketSourceChecks` | Production pricing cutover is still incomplete. Qualify sibling ports, partial coverage, source receipts and portfolio/worker execution before claiming the new path is integrated. |
| Structural rows and separate evaluated values | `Trading/ScheduleRow.cs`, `ScheduleRows.cs`, `ScheduleProgramValues.cs` | Do not replace the retained program with a mutable table of sampled prices, quantities or statuses. |
| Power delivery, certificates, capacity and linked payment quantities | `Trading/PpaSides.cs`, `PpaTerms.cs`, `PpaGraphQuantities.cs` | Reuse proven behavior, but PpaRole, PpaSideTerms, generated/delivered stages and asset-capacity normalization are not generic gas/cargo contracts. |
| Effective-period location valuation | `Power/LocationValuation.cs` | Reuse exact assignment and overlap/gap validation; add specification/context applicability only with a concrete consumer. |
| Cross-commodity cash swaps | `Trading/CrossCommodityScheduledSwap.cs`, `CrossCommoditySwapChecks` | Typed units and independent source risk already exist; this does not establish physical delivery. |
| Determinations, corrections, partial cash, reversals and remaining value | `Trading/SwapSettlement.cs`, `SettlementDetermination.cs`, [swap settlement](swap-settlement.md) | Lifecycle currently admits financial swap families. Reuse arithmetic/command invariants through a new explicit physical payment consumer; do not simply remove product guards. |
| Physical PPA settlement preview | [Settlement preview](settlement-preview.md), `SettlementPreviewChecks` | Meter finality and gross preview are reusable evidence; this path does not accept physical cash or issue invoices. |
| Standalone physical booking requirements | [Physical energy booking](physical-energy-booking-plan.md) | Operational facts, delivery completion, physical cash and inventory remain work. |

No physical gas/crude/LNG/freight side implementation or tariff engine was found
in the scanned Trading/API source. Financial gas examples and commodity labels
are not physical product support.

## Ownership and schedule contract

The [physical gas field contract](physical-gas-field-contract.md) records required
term ownership, field admission and additional behavior cases F01–F35. A field
cannot enter an executable Side as an unused placeholder. Quantity limits,
entitlements, measurement stages and selected operational quantities are distinct.

Proposed concrete sides are PhysicalGasSide, PhysicalCrudeSide,
PhysicalLNGCargoSide and PhysicalLNGFreightSide, composed by validated physical
instrument templates. Existing FixedSide/FloatingSide behavior supplies commodity
consideration where compatible. FeeSide supplies additional explicit charges.
Names and a minimal common physical base remain implementation choices.

FreightSide owns its contractual charge calculation. Fixed/indexed pricing and
voyage/quantity/duration charge bases are independent choices. It does not also
create an independently marked transport-service asset unless a separately
supported valuation model is selected. The first freight case values charges only.

| Owner | Captured data | How schedules consume it |
| --- | --- | --- |
| Instrument and Side definitions | Immutable delivery, quantity selection, price, fee, date and completion rules; stable membership roles | Booking compiles the complete executable program once. |
| Reference definitions | Locations, network/zone membership, custody points, specifications, clocks, valuation assignments, service agreements and rates | Exact-version typed bindings, including applicable delivery-time coverage. |
| Operational records | Nominations, confirmations, measurements, allocations, cargo/parcel events and corrections | Resolve under explicit effective/knowledge coordinates; retain exact selected revisions. |
| Resolved schedule | Selected quantities/prices, source states, calculation lineage and contribution measures | Read-only calculation output under a pinned context/scenario. |
| Trade payment lifecycle | Occurrences, accepted determinations, corrections, cash allocations and reversals | Applies once to covered contractual cash contributions; never mutates a shared Side. |
| Invoice/payment integrations | Future issued-document and external dispatch evidence | Separate consumers; recording cash is not evidence of bank dispatch or invoicing. |

Keep independent classifications for contribution kind (delivery/payment/holding),
quantity source and measurement stage, price state, physical completion,
determination state and cash balance. Existing enums cover only part of this.
Do not add every workflow state to ScheduleRow or treat `Realised` as proof of
accepted cash. A delivery row's value date is not a payable payment date.

Bindings name a role plus a quantity output, such as receipt energy, allocated
delivery energy, loaded mass, accepted discharge energy or chargeable duration.
Validate units, geometry and an acyclic dependency graph. Sibling quantity
outputs need not share the payment's aggregation cadence. Physical actuals are
never overwritten by a billing minimum, conversion or fee quantity.

## Baseline qualification cases

| ID | Case to prove before enabling new products | Expected behavior |
| --- | --- | --- |
| B01 | Book, load and execute existing fixed, floating and physical-power sides | Existing independent values and source pins survive; booked reads call no generator or replacement compiler. |
| B02 | Linked quantity port, amendment coverage and custom-output pricing | Direct execution equals independent expected cash; complete receipts retain every consumed source. A missing/cyclic port refuses. |
| B03 | Same side used by different instruments/trades with different quantity bindings | Compatible prices can be reused; quantity and lifecycle results cannot leak between bindings. |
| B04 | Repartition identical native contributions into different storage blocks | Business occurrence identity survives; storage manifest/ordinal is not its identity. |
| B05 | Booked portfolio, stored close and worker consume the same retained program | Same exact PV/risk and pins; cancellation or missing coverage yields no successful complete total. |
| B06 | Existing swaps, PPA valuation and enrolled swap settlement | Existing behavior remains qualified. Do not adopt a physical overdue or completion policy for financial contracts implicitly. |

## Park-and-loan and swing rights

These are required gas capability gates, not enabled features of the current
fixed-quantity ticket. Reuse independent physical/cash memberships and retained
programs. Add explicit obligation links, trade-owned balances and election facts;
do not introduce nominal product fields without their execution consumers. The
[field contract](physical-gas-field-contract.md#park-and-loan-and-swing-rights)
defines ownership and valuation boundaries.

| ID | Action / synthetic input | Independent expected result |
| --- | --- | --- |
| PL01 | Customer parks 1,000 native energy units and later receives 400 credited return units under an equal-principal rule | Initial delivery and return receipt have independent schedules. Outstanding return is 600; a separate customer loan has reversed physical directions. |
| PL02 | A 400-unit return is nominated but not credited under the selected completion policy | Outstanding principal stays 1,000. Forecast, nomination and credited return remain distinct facts. |
| PL03 | Credit the 400-unit return, retry the identical command, then correct that credited return to 350 | Retry leaves outstanding at 600; the correction makes it 650 with original event and correction lineage retained. |
| PL04 | Two whole gas days begin with outstanding balances 1,000 and 600 respectively; declared fee is 0.002 currency units per outstanding unit per gas day | Nominal fee is 3.20. A fee on units moved or reserved capacity uses a different named basis; missing balance is not zero. |
| PL05 | Original advance is complete, 600 is still owed, and its return deadline passes | Completion cannot erase the return claim/obligation. Overdue state, accepted repayment and any explicitly permitted cash resolution remain separate. No automatic physical repayment or cashout. |
| PL06 | Return uses another location, measurement basis or non-unit return ratio | Resolve the explicit point binding and qualified conversion/rule. Missing conversion or ineligible return point refuses; gas owed and ordinary inventory cannot double-count ownership value. |
| PL07 | Two trades share one immutable instrument and have advances of 1,000 and 700; only the first credits 400 | Outstanding balances are 600 and 700, independently authorized and historically reproducible. |
| SW01 | Daily base 1,000, minimum 700, maximum 1,100, with an explicit eligible election window | A 1,100 election passes daily bounds; 1,101 refuses without changing accepted quantities or consuming rights. |
| SW02 | Three days have a cumulative maximum of 3,100 under the same daily bounds | Election profile [1,100, 1,100, 1,100] refuses despite individually valid days; [1,100, 1,000, 1,000] meets the quantity bounds. Other contract conditions still apply. |
| SW03 | Qualified counted-change policy allows one upward change; accept it, then retry, then request a second qualifying upward change | Retry consumes no additional right; the second qualifying change refuses. Independently test initial-period and preceding-day baselines, up/down/combined budgets, period resets and change-size limits. |
| SW04 | Exercise comes from the wrong party, after its explicit gas-clock deadline, or outside its effective period | Refuse under the selected contract and knowledge context; existing accepted election and operator nomination remain intact. |
| SW05 | Accept a new commercial election; a later operator cut changes confirmed flow and a meter correction changes allocated flow | Preserve all states separately. Only declared selectors change valuation/billing quantity; operational changes do not themselves consume a commercial swing right. |
| SW06 | Linked cash price is 3 per unit and usage fee is 0.10 per unit of the selected elected quantity; change an eligible election from 1,000 to 1,100 | Nominal commodity consideration changes from 3,000 to 3,300 and fee from 100 to 110 with their independent signs. Unlinked sides remain unchanged. |
| SW07 | Rights remain unexercised, but the selected context lacks the required rights model or market inputs | Complete MTM is unavailable with a structured reason. Fixed-base marking or a zero option contribution cannot masquerade as a complete valuation. |
| SW08 | Book/reopen rights, compare historical elections before/after a correction, and evaluate different trade sizes | Retain complete executable terms and source lineage; historical knowledge excludes later facts. Qualify scaling against the actual count/aggregate constraints rather than multiplying one-unit option value without proof. |

These are planned acceptance oracles, not reported passing checks. The existing
gas command must continue rejecting unsupported rights and park/loan terms.

## Gas purchase and linked fees

First profile: one delivery point, one currency, explicit gas clock, fixed energy
quantity per operating day, fixed or admitted indexed consideration, and optional
linear usage fee. No storage, swing optimization or cross-border balancing.

| ID | Action / synthetic input | Independent expected result |
| --- | --- | --- |
| G01 | Book a future purchase: 1,000 MMBtu total, location mark 3.20 USD/MMBtu, fixed consideration 3, usage fee 0.10, all DF=1 | Delivery +3,200; commodity cash −3,000; fee −100; complete pre-delivery PV +100. Only −3,100 is contractual cash. |
| G02 | Shock only the physical location mark by +0.10 | PV +200; fixed cash unchanged. The delivery mark is not an invoice price. |
| G03 | Independently shock an indexed payment reference by +0.10 for 1,000 MMBtu | Payment contribution decreases by 100; unrelated physical mark unchanged. No forced cancellation of differently bound sources. |
| G04 | Gas operating days spanning spring/fall clock changes | One explicitly contracted 1,000 MMBtu/day entitlement remains 1,000 on both days. A separately selected hourly-rate rule integrates actual elapsed hours; no implicit interchange. |
| G05 | Receipt 1,000, fuel 2%, net=gross×(1−fuel) | Delivery 980. Under a separately selected net=gross/(1+fuel) rule, delivery is exactly 50,000/51. Reject an unqualified bare percentage. |
| G06 | Measure 1,000; allocate 980 to this trade; select allocated energy for billing | At 3 plus 0.10 fee, commodity −2,940 and fee −98. Measurement remains 1,000; allocation remains 980. No automatic actualization of a separate financial hedge. |
| G07 | Reservation charge 50 USD/day for two days; zero throughput | Reservation −100; usage fee zero. Missing measurement is not zero throughput. |
| G08 | Usage tariff 0.10 superseded by explicit contract rate 0.08 for 980 eligible units | Selected charge 78.40. A different contract uses 98. Missing/ambiguous rates refuse; changing current reference setup cannot alter earlier pinned calculations. |
| G09 | Nominate 1,000; confirm 990; record measured/allocated 980 | Preserve all four quantities and their revisions. Each named valuation/billing selector uses only its declared state and fallback. |
| G10 | Correct allocated billing quantity from 980 to 990 after determination | Restated commodity −2,970 and fee −99; adjustments −30 and −1. Original determinations/payments survive; retry adds no second adjustment. |
| G11 | Eligible volume known but required payment fixing missing or preliminary | Valuation follows its selected pending policy; final determination refuses. Price finality and physical finality remain independent. |
| G12 | Repoint location assignment, amend future delivery point, or change tariff season | New selected coverage changes only where explicitly applicable. Old knowledge/booking revisions reproduce their original assignments. |

## Physical lifecycle and settlement

This is a required gas delivery slice, reused by subsequent commodities. Select
explicit physical completion, pending actuals, recognition, due/overdue,
rounding and correction policies before admitting the corresponding commands.
Existing swap policies are candidates for reuse, not defaults.

| ID | Workflow | Expected behavior |
| --- | --- | --- |
| L01 | Draft → preview → book → reopen | Shared API/UI rules; exact references and terms survive persistence. Preview creates no operational or cash facts. |
| L02 | Nominate/confirm/import/allocate through authorized commands | Same fact is a no-op; changed value creates a revision. Conflicting expected heads refuse atomically. |
| L03 | Partial delivery | Remaining obligation uses only undelivered scope. Selected delivered quantities remain inspectable; no delivery is inferred from a forecast or the calendar alone. |
| L04 | Complete delivery with selected consumed/no-inventory treatment | Retire delivery market value once. For G06, unpaid cash is −3,038 under DF=1 even when its physical delivery component has retired. Report that scope explicitly; it is not total lifetime trading profit. |
| L05 | Determine commodity and fee payments, pay 1,000 of the 3,038 payable | Signed remaining outstanding is −2,038; determined cash is not also valued as projected cash. Gross allocation and rounding remain explicit. |
| L06 | Reverse that payment; repeat the identical command | Outstanding returns to −3,038 once. Unknown outcomes reconcile through original operation identity. |
| L07 | Receive and hold commodity rather than consume it | Either implement a separately valued inventory transfer with conserved ownership/quantity and no duplicate delivery value, or refuse this profile. Do not claim inventory from delivery-row classification. |
| L08 | Correct after partial/full payment or after a future amendment | New correction contribution refers to original occurrence and source pins; it is not a rewrite of paid cash or an unrelated new obligation. |
| L09 | New measurement/fixing arrives between preview and acceptance | Selected-head policy and publication fence enforce a stale-proposal conflict where applicable. Accepted pins are not silently refreshed during payment. |
| L10 | Grant revoked while waiting, cross-book access, worker lease loss, or rollback | No unauthorized evidence or partial committed operation. Current authority applies to retries and historical reads. |
| L11 | Schedule viewer, quantity entry, settlement inspection and cash entry | UI exposes independent states, selected basis, source revisions and missing inputs; no frontend financial calculations. |
| L12 | Invoice export requested | Explicitly unsupported until issued-document lineage, correction and export behavior is implemented. Cash-recording success must not claim invoice issuance. |

## Crude delivery and measurements

First profiles: simple pipeline and title-transfer delivery, with explicit grade,
quantity basis and location mark. Use the gas lifecycle where its declared rules
apply; do not copy gas clocks, fuel or normalization assumptions.

| ID | Action / synthetic input | Independent expected result |
| --- | --- | --- |
| C01 | Future 1,000 bbl purchase; physical 80 USD/bbl; fixed payment 78; handling 0.50; DF=1 | Delivery +80,000; consideration −78,000; fee −500; PV +1,500. |
| C02 | Same location, two grades and incompatible assignment candidates | Exact grade binding selects its declared mark/adjustment; ambiguous or missing assignment refuses. A quality adjustment is not automatically a payable fee. |
| C03 | 800 t at declared density 0.8 t/m³; explicit synthetic conversion 6.25 bbl/m³ | 1,000 m³ then 6,250 bbl. Persist density, conditions, rule and revision; no conversion inferred from unit labels. These factors are fixture terms, not standard conversion constants. |
| C04 | Contract 1,000 bbl with ±2% tolerance | 980 and 1,020 are eligible boundary deliveries; 979 is an exception under the chosen acceptance rule. Tolerance is not an instruction to clamp actual quantity. |
| C05 | Delivery/title event plus offset drives observation/payment timing | Forecast event prices indicative future rows; eligible actual event recomputes the mapped dates under the selected rule. Missing required event prevents final determination. |
| C06 | Pipeline receipt/withdrawal, cycle and direction | Validate applicable endpoints and cycle. Selecting a zone alone cannot fabricate a valid transport path. |
| C07 | Quality or quantity correction after settlement | Reproduce old accepted inputs, then calculate explicit new contractual adjustment; measurement revision does not amend the product specification. |

## LNG cargo and configurable freight

Use a PhysicalLNGInstrument containing cargo, cargo consideration, optional
freight and fees. For initial workflow qualification, use a purchased cargo
immediately delivered onward under explicit allocation: no unimplemented tank
inventory, regasification or voyage optimization may be inferred.

Synthetic pre-delivery example: 1,000 t loaded, 980 t expected discharge, explicit
50 MMBtu/t conversion at discharge, cargo payment 10 USD/MMBtu, delivery mark 11,
voyage freight 20,000 USD, handling 0.10 USD/MMBtu and DF=1.

| ID | Action | Independent expected result |
| --- | --- | --- |
| N01 | Preview the complete composed purchase | 49,000 MMBtu; delivery +539,000; cargo cash −490,000; freight −20,000; handling −4,900; PV +24,100. The delivered mark is defined before these separately booked freight/handling charges. |
| N02 | Freight basis uses loaded mass at fixed 20 USD/t | Freight −20,000 from 1,000 loaded tonnes, not −19,600 from discharge mass. Cargo/handling still use discharge energy. |
| N03 | Freight mode fixed voyage versus daily fixed 2,500 for 8 chargeable days | Each yields −20,000 under its own explicit terms. Nine chargeable days changes daily freight to −22,500; fixed voyage stays −20,000. |
| N04 | Indexed daily freight: 8 days and eligible daily rate 2,500 | −20,000. A +100 live rate shock changes charge by −800; final observations are frozen. Missing due observations refuse final determination. |
| N05 | Partial load/discharge and cargo split between two trades | Explicit allocations conserve the source parcel quantities; no full-cargo duplication. Individually supported unit definitions do not imply arbitrary scaling of the whole shipment. |
| N06 | Actual discharge 982 t replaces previously determined 980 t | Billable energy 49,100; cargo cash −491,000; handling −4,910; unchanged voyage freight −20,000. Total payable restatement −515,910; adjustment −1,010. |
| N07 | Loss, boil-off or fuel observation | Preserve source stages and apply each admitted rule once. Loaded/discharged difference alone cannot establish how much is boil-off, fuel or measurement variance. |
| N08 | Contract-count voyage fee shared with a variable-size cargo obligation | Explicitly declare normalization/allocation. Reject multiplying a whole-voyage lump sum by cargo tonnes or applying it twice across allocations. |
| N09 | Cargo window, custody/title event, route and freight dates differ | Retain distinct fields and event dependencies. Changing discharge timing affects only declared charges/pricing/date rules. |
| N10 | Demurrage, heel, gas-up/cool-down, quality rejection, terminal inventory or regasification requested | Enable only through separately specified operators and fixtures. Reject unsupported terms before booking; a descriptive field cannot silently imply execution. |

## Exchange compatibility

After product terms are executable, add a versioned FpML Confirmation profile for
supported gas and crude trades. Map terms, reference schemes, units, parties,
periods, limits/tolerances and pricing conventions field by field. Validate XML
and semantic round trips, with explicit refusal of unsupported economic terms.
Retaining raw XML is audit evidence, not implementation of a payoff.

LNG cargo and physical freight require an explicit mapping/extension assessment;
do not represent physical carriage as a cash-settled freight derivative merely
to obtain an XML document. External compatibility is a separate acceptance gate
from numerical or operational correctness. Pin the exact schema package before
implementing these adapters.

## Delivery slices and test ownership

Each slice begins with the meaningful failing case, implements the smallest
complete consumer, and finishes with persistence, API and UI behavior where
applicable. Do not land unused catalogs or placeholder product types.

| Slice | Cases / deliverable | Gate and likely code ownership |
| --- | --- | --- |
| P0 | B01–B06: qualify current retained-program integration | Existing schedule program, market-source and booked API checks; coordinate with current execution work rather than implement a parallel path. |
| P1 | G01–G05, G12: physical gas terms, clock and market valuation | Managed gas-side checks; retained compiler/source-family adapters and exact assignment references. Booking round trip and scenario parity are required. |
| P2 | G06–G09, L01–L03: versioned operational quantities and simple linked fees | Managed quantity/fee checks plus stock-database import/allocation, typed-port, authorized API and ticket/detail checks. Add only the fee/rate records consumed here. |
| P3 | G10–G11, L04–L12: physical cash and corrections | Extend existing settlement consumer contracts, occurrence ownership, cash allocations and inspection. No inventory or invoice claim without its own implementation. This closes the first complete gas workflow. |
| P3a | PL01–PL07: park-and-loan | Independently directed advance/return schedules, explicit return rules, trade-owned outstanding principal, balance-based fees, accepted repayment/correction and overdue treatment. Shared reference definitions do not share balances. |
| P3b | SW01–SW08: swing rights | Contractual bounds and counted-change rules, party/deadline validation, retained election history and quantity-source links, remaining-rights MTM under a declared model, and independent scaling qualification. Quantity validation alone does not qualify option value. |
| P4 | C01–C07: crude terms, quality/conversions and event dates | Reuse P1–P3; independent crude schedule/settlement cases plus API/browser round trip and source-correction evidence. |
| P5 | N01–N10: LNG allocation and configurable freight | Event/cargo facts, freight terms and quantity/duration ports; explicit inventory boundary and scaling admission. |
| P6 | Exchange compatibility profiles | Pinned schema validation, semantic round trips and unsupported-term checks; operational cases stay independently mandatory. |

Suggested new managed suites: PhysicalGasChecks, PhysicalQuantityChecks,
FeeScheduleChecks, PhysicalSettlementChecks, PhysicalCrudeChecks and
LNGCargoFreightChecks. Names are proposed; use the existing check runner and
fixture style. Reuse current managed settlement checks for shared arithmetic,
then add new domain checks for actual selection and physical completion.

Database/API acceptance must exercise exact-version foreign keys, historical
selection, RLS/current authority, idempotent retries, conflicting amendments,
correction/payment races and rollback. Owned browser tests prove entry, inspection,
actual capture and recovery through those same operations. Use isolated resources.

Benchmark at the first admitted daily gas and cargo workloads: unique sides,
intervals/events, quantity bindings, tariff lookups, scenarios, DB reads, evaluated
and reused work, elapsed time and memory. A cargo is not padded into hourly rows
unless a selected charge genuinely needs that geometry. No maximum is claimed
from passing the numerical cases.

## Scan verification

The initial `dotnet run` used the machine default 9.0 SDK and refused the pinned
10.0.402 requirement. Verification uses the installed SDK matching `global.json`; no SDK pin was changed.
The following existing managed groups passed in this clean worktree, using
`dotnet run --project tests/OpenEtrm.MarketData.Checks -- <group>` for the first
build and `--no-build` for subsequent runs:

- `--schedule-program-execution`
- `--schedule-market-sources`
- `--retained-schedules`
- `--schedule-program-demand`
- `--cross-commodity-swaps`
- `--swap-settlement`
- `--ppa-sides`

These checks establish a focused existing baseline, not completion of B01–B06 or
the production execution cutover. The 47 acceptance cases remain the delivery
target; the separate gas quantity increment now covers the contractual portions
of G04/G05. Database, browser, worker and production qualification are outside
this source-scan verification.


The [gas booking increment](physical-gas-side.md) now connects the bounded
fixed-contractual profile to typed persistence, shared retained programs and the
Physical gas ticket. Its focused database/API and browser checks are separate
from the original source scan above. G01 has a preview/book/reopen PV100 oracle;
operational capture, amendments, settlement and complete portfolio integration
remain subsequent gates.
