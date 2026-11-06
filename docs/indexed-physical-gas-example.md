# Indexed physical gas: forwards, daily fixings and unpaid cash

Status: bounded managed calculation and synthetic worked example, locally
qualified under D329 and extended by D331–D332. The payment-aware purchase profile
is not admitted by the application ticket, booking/payment APIs, retained worker
or hosted database. D332 separately qualifies forecast-curve selection on the
existing future-delivery physical gas profile; its market-value and fixed-cash
contracts retain their existing meaning.

## Henry Hub source curves

D330 selects [ICE Henry Swing, HHD](https://www.ice.com/products/6590229/Henry-Swing-Future)
as the daily source and [ICE Henry LD1 Fixed Price, H](https://www.ice.com/products/6590258/Henry-LD1-Fixed-Price-Future)
as the monthly source. HHD references daily Gas Daily Henry Hub prices; H
references monthly NYMEX Henry Hub settlement. They are not interchangeable
settlement indices. Each product has a fixed 2,500 MMBtu lot and quotes USD/MMBtu;
the quoted minimum increments are 0.0001 for HHD and 0.001 for H.

The local source-data example contains 26 daily intervals from November 5 through
November 30, 2026, and 36 monthly intervals from December 2026 through November
2029. Three synthetic closes on November 2–4 keep the same dated contract
identities while their observations change. Native futures pricing and retained
up/down risk verify signed lot scaling and source independence. All prices,
series calendars and expiry cutoffs are explicitly synthetic; a weekday-only
calendar does not represent ICE holiday sessions, actual listings or delivery
packages. The source setup adds no application code, schema or public loader.

The local preview now has separate daily and monthly OTC price-projection
definitions and their combined curve, linked through exact published versions.
Each OTC curve adds an independent, explicitly quoted basis curve to its futures
unit prices. The generic signed-sum recipe declares both dependencies; lot size
is not applied to unit prices. No plain pass-through OTC curve is retained.
The mixed output keeps 26 daily intervals through November and 36 monthly
intervals from December, with an explicit December 1 cutover. These are forecast
price relationships, not a conversion of one settlement index into another.

The managed cash example below consumes the same hierarchy, bounded to its own
delivery term. Its existing daily lookup selects the containing interval for
each forecast day; a December monthly quote remains one source factor across
those lookups. Neither setup establishes an automatic roll, a continuous rolled
history or full physical futures margining.

## Physical ticket example

The existing future-delivery ticket can select the exact published combined curve
on each physical side. No additional daily projection curve is needed. The stored
`curve_price_lookup` operation retains the containing-interval policy and each
gas day's query separately from its native index/fixing observation period.
Preview, booked inspection and retained risk resolve the same typed sources.

A disposable local Henry Hub example receives 100 MMBtu per day on November 5–30
and December 1–31, 2026. Both physical sides select that one combined curve; two
fixed consideration sides pay 3.10 USD/MMBtu. At the synthetic November 4 close,
physical value is 19,362 USD, fixed consideration is −17,670 USD and net PV is
1,692 USD. The 57 delivery days consume 26 HHD points plus one H monthly point
and 27 independent basis points. Synthetic basis assessments are about
0.020 USD/MMBtu in November–March and 0.005 in warmer months, with small tenor
variations; these are invented marks rather than an asserted market premium.
There are no paid/delivered cash rows in this application example. Its independent
native index/fixing metadata is synthetic and does not assert a vendor's daily
publication or monthly aggregation methodology.

The native integration fixture also checks same-economics trades using different
selected forecast sources, independent daily/monthly scenario deltas, raw source
and query mismatch refusals, current authority and interactive/queued worker
parity. This extends curve admission rather than the indexed cash lifecycle below.
Operational loaders remain private; no production load or deployment is implied.

## Valuation boundary

D350 additionally admits future-delivery indexed consideration through the
ordinary gas composition ticket: one PhysicalGasSide and an independent
FloatingSide with exact daily index inputs, explicit additional spread and
monthly UTC payment terms. That application profile reports contractual MTM,
including the physical asset and signed payment. It does not persist the
started-delivery cash state described below; its equal zero-adder forecasts
offset before fixing. See [the deal ticket](deal-ticket.md#indexed-consideration-and-refreshed-entry-fields).

`IndexedPhysicalGasPurchase` is a normal `SidedInstrument` with an independent
`PhysicalGasSide` and `FloatingSide`. It implements the common unit `PV` contract
for the explicitly selected `UnpaidCashPresentValue` measure. Its cash Side owns
fixing/forward resolution, exact scheduled amounts, unpaid balances and payment
discounting; the instrument applies its pay membership. This measure excludes
the commodity's noncash market value and makes no fulfillment, inventory or
physical P&L assertion.

The risk engine is unchanged. It invokes `Instrument.MarketFactors` and
`Instrument.PV` under ordinary shared scenarios. Native factor/tenor results
come from those returned values. Neither this instrument nor its fixture adds
an exposure formula, a commodity shock rule or tenor allocation outside that
repricing.

`IBindingValuationState` supplies only the held trade, binding and quantity.
The instrument validates its supported state. `CashflowBindingValuation` and
`PaymentSideState` retain cash facts outside reusable economic identity; separate
binding states form separate calculation scopes and Side-context cache keys.
Existing `SwapBindingValuation` uses that common boundary while retaining its
financial-swap policies and decimal cash admission.

## Price and quantity terms

The bounded purchase uses one existing native physical quantity schedule and
one indexed consideration row for every gas day. Delivery geometry, exact native
quantity, daily fixing and explicit payment instant must agree. Cash currency
and quantity specification must match the physical Side. Prices and quantity
scaling use the existing exact Side arithmetic.

The example's forward projection uses ordinary saved curve recipes. Separate
daily HHD-plus-basis and monthly H-plus-basis graphs feed a whole-delivery splice. Its
explicit `containing_delivery` lookup maps each requested daily interval to its
selected source period. November forecasts retain each individual daily price;
December days use the selected December monthly price. A different qualified
shaping rule belongs in the selected price curve. There is no commodity-wide shaping default.
Identical prices do not establish factor identity: the normal graph retains the
actual source identities and dependency relationships supplied by the curve.

A due daily fixing uses the existing exact published-index resolver. Missing
required final observations refuse; forward prices cannot replace them. Actual
observations retain their original source lineage and are frozen under live
quote shocks. A monthly bidweek index and an average of daily index prices are
different price contracts; no cross-index projection or basis is inferred.

## Cash state

`explicit_paid_cash_nominal_due_v1` is selected explicitly. A determination is
created from the cash Side's final prices through its native price resolver,
with the exact owned membership, economic/pricing terms and resolved knowledge
cutoff. Its immutable `SidePaymentCash` evidence freezes the final amount and
source lineage. A later corrected market observation does not rewrite it.

Supplied cash allocations link to an exact determination and retain signed
amount, effective payment time and knowledge time. Each allocation and their
sum must preserve the determined direction and cannot overpay it. Normalizing
recorded cash to the Side's unit basis preserves exact rational amounts.

Fixed unpaid cash contributes to PV until allocated payment removes it. Future
unpaid cash receives its explicit discount factor. Due unpaid cash remains at
nominal amount under the selected policy; a scheduled payment date never creates
a paid fact. A fully paid row remains in history with zero outstanding PV and
needs no discount factor. A zero final amount is `NoAmountDue`; a zero forecast
remains `Projected`.

Cash facts are selected under both business and knowledge time. A visible
allocation requires its visible preceding determination. This pure managed
consumer does not certify authorization or database commit visibility and
performs no writes. Multiple original determinations for one occurrence are
refused; durable corrections, invoices, commercial cash-rounding admission,
reversals and payment recording remain separate work.

These dated cash rows use `ValueCoverage.UnpaidCashPresentValue` and explicit
`CashBalance` fields. They do not use the interval/PPA excludes-overdue
classification or its `ValueMeasureSet` summary.

## Executable example

The wholly synthetic purchase receives 100 MMBtu per gas day from October 1
through December 31, 2026. Its captured UTC clock starts at midnight; invented
final publication occurs at noon on each delivery day. Those conventions
do not describe an exchange or publisher's real methodology. The calculation
is pinned to November 4 at 16:00 UTC and knowledge through November 5 at 16:00 UTC.
It explicitly selects no discounting.

| Days | Price USD/MMBtu | Payment evidence | Unpaid cash PV USD |
| --- | ---: | --- | ---: |
| October: 31 | 3.00 final | Paid November 3, learned November 5 | 0 |
| November 1–4: 4 | 3.10 final | Unpaid; due December 10 | -1,240 |
| November 5–30: 26 | 3.20–3.26 futures + 0.020–0.024 basis | HHD and independent daily basis; due December 10 | -8,450 |
| December: 31 | 3.50 futures + 0.020 basis | H and independent monthly basis; due January 10 | -10,912 |

Full-period consideration is -29,902; recorded paid cash is -9,300; unpaid cash
PV is -20,602. A +1 USD/MMBtu shock to one November daily source gives -20,702;
the same shock to either December monthly source gives -23,702, through ordinary full repricing. There are 54 native futures/basis factors. Final cash remains
unchanged. This is one captured curve/snapshot example, not an automatically
rolling BALMO feed or a qualified historical curve-publication workflow.

Run from this repository with its selected .NET SDK:

```sh
dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --side-payment-state
dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-forward-settlement-example
dotnet run --project tests/OpenEtrm.MarketData.Checks -c Release -- --gas-forward-settlement-example-json
```

The JSON command exports the same native schedule and risk results with their
context, snapshot and scenario identities. Generated reports and operational
seed/load scripts remain outside the repository. It does not book a trade in an
existing application or write prices/cash to a database.

Qualification includes partial/full and invalid signed payments, overdue cash,
prepayment and zero rows, rational balances, missing knowledge, knowledge replay,
frozen determinations and execution-basis refusal. The physical fixture qualifies
92 daily rows, native projected up/down PV and two same-definition holdings with
different payment histories. Broader regression results are separate from any
future durable operation or deployment qualification.

Local verification on 2026-11-05: all 661 managed core checks, 86 rate checks, 104 ticket checks
and the API default check entrypoint passed. API and worker Release builds completed without warnings.
The existing scheduled-swap HTTP, agent settlement and retained-risk regression
passed against an owned disposable stock PostgreSQL database, which was removed
after the run. Independent adversarial review closed the bounded managed scope;
these results do not establish new durable or hosted admission.

D331's connected-portfolio regression also passed on an owned stock PostgreSQL
database, which was removed afterward. It exercises saved splice roundtrips,
direct lookup/weighted consumers, malformed SQL publication refusals, both
single-branch consumers and partly fixed retained-worker parity. Migration
checks refuse helper body, ownership, ACL, configuration and node-constraint
drift and work under a different caller search path. The local curve-builder
preview and generic splice graph were verified in the browser. These qualify
the shared curve mechanism; the physical cash profile remains a managed example.
