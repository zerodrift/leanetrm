# Unit instruments and trade bindings

Trade owns an ordered collection of `TradeInstrument` values. Each binding has a
stable occurrence key, an immutable Instrument reference and an external signed
typed Quantity. Independent fills or amended obligations can reference the same
instrument without becoming the same occurrence. Position is a derived valuation
or reporting projection, not a separate booking aggregate.

The managed futures booking, draft, amendment and read adapters construct these
generic bindings. Futures, financial-swap and PPA portfolio preparation use
`ValuationPortfolio.FromTrades`; existing database rows, event keys and public
request/response shapes remain unchanged. There is no schema migration in this
increment. Storage readers retain authorization, historical visibility and
product-specific admission before generic calculation.

## Typed valuation contract

```csharp
UnitValuation Instrument.PV(UnitValuationContext context)
```

`UnitValuation` carries an exact rational money numerator, its exact quantity
denominator and the specific instrument/scenario context that produced it.
Currency remains part of the money type. Portfolio evaluation rejects a result
with another context, currency or quantity basis before scaling or aggregation.
`UnitMoney` remains the context-independent per-unit value used for execution
basis. Existing `Value(UnitValuationContext)` and rich side-context PV methods
are bridged by the common entry point; new leaf models can implement typed PV
without introducing sides or an unused discount provider.

`Declare(context)` supplies model version, complete market dependencies, quantity
basis and output currency. Declarations enable shared scenarios and unaffected
work reuse; they do not assign exposures. Risk calls PV under base/up/down
contexts and measures returned values. It does not select pricing formulas by
concrete instrument type. Binding validation, scoped workload estimates and
lifecycle dispatch belong to the instrument contracts.

`MarketFactors(context)` returns the canonical shared market factors resolved
from those declarations. The portfolio unions them by exact factor identity,
independently of instrument family: several sides and instruments referencing
the same dated contract contribute one factor to the run. One immutable shock
then reaches every consumer through normal PV evaluation. The dependency graph
retains price construction and exact source identity; curve labels, instrument
IDs and repeated holdings do not create extra market factors.

Only instruments declaring `SupportsQuantityScaling` can enter this unit-based
trade/portfolio path. This means linear scaling in booked size, not linearity in
prices. A nonlinear market model can still scale by contract count. A
quantity-dependent payoff needs an explicitly supported alternative before
admission; it cannot silently use unit scaling.

## Futures contracts and execution

`FuturesContract` remains a leaf Instrument identified by its exact immutable
dated series key. Curve members and trade bindings reference that same economic
definition. Lot normalization stays on the contract and pricing selection stays
in `FuturesMarketBinding`.

`FuturesExecution.Bind` converts an explicit signed integer lot count to the
contract's physical quantity once and returns a generic `TradeInstrument`.
`FuturesExecution` contains execution price/time, pricing binding and supported
settlement state; it stores no second booked quantity. Displayed/persisted lots
are derived from the external quantity using the exact contract lot size.

Execution P&L requires explicit execution facts on every binding. Each execution
basis is retained, including offsetting fills. Zero net quantity does not erase
the difference between purchase and sale prices. Execution-P&L/open-risk support
remains explicitly distinct from futures settlement PV. Present-value requests
still reject until retained margin and settlement state can support them; the
common method name does not change that capability boundary.

## Forward contracts

`ForwardContract` implements a bounded managed cash-settled indexed forward using
existing FixedSide and FloatingSide implementations. Both sides require compatible
price units and quantity basis, the same delivery period and the same payment
instant. The unit payoff is canonically indexed payment minus fixed payment.
Buy/sell direction is the external signed quantity, so opposing holdings reuse
the same instrument definition.

Identity includes complete side economics and explicit discounting. Different
quantities do not change identity; different strikes or payment terms do. An
authorized operation can use `InstrumentIdentityMap` to intern equivalent
complete definitions. Different strikes can still reference the same floating
Side object and share its calculation. Published identities remain governed by
the retained-definition map; hashing does not replace full-key checks.

This class has executable managed valuation/risk consumers. It does not add a new
persisted forward discriminator, standalone forward ticket, physical forward
settlement workflow or automatic reclassification of existing financial swaps.
Saved instruments keep their economic identity versions.

## Reuse and work accounting

Unique instrument/coverage/lifecycle scopes are valued once per affected scenario,
then external quantities are applied to trade or book results. Identical units
across books reuse calculations when their resolved policies and inputs agree.
Instrument identity alone does not establish calculation equivalence.

Ordinary sided instruments also share an operation/scenario-local side-context
cache. Its key includes the exact declared dependency set and resolved
discount-provider object. Shared Side objects evaluate once in that context.
Different providers, dependency fences or scenarios create different contexts.
The cache grants no market access and cannot cross runs or historical cutoffs.
Keys use hashed lookup rather than scanning prior scopes. Work reports include
these side calculations alongside interval-side work.

The trade projection uses dense scope indices and existing exact book aggregation.
It avoids recomputing economic identity for repeated references to the same
immutable Instrument instance. Full definition checks apply when different
objects claim the same ID. Calculation reuse does not merge trades, accounts or
events; accounting/clearing netting remains a separate operation.

## Verification

Managed checks cover a PV-only model without a product adapter, shared unit values
across books, repeated instrument occurrences in one trade, incorrect units,
stale results, undeclared size scaling and missing execution facts. Forward checks
use independent price/strike/quantity arithmetic, definition interning, shared
floating-side evaluation and isolation between discount providers. Existing
futures checks retain variable lots, precision, immutable terms, offsetting fills
and grouped-risk oracles. Results are recorded in [verification](verification.md).
