# Unit instruments and trade bindings

## Instrument schedule (D446)

Every `SidedInstrument` exposes one cached `InstrumentSchedule` per instance.
Entries retain membership order, Side reference, coefficient and quantity ratio;
rows pair each Side row with its entry slot. Reading entries or evaluating the
plain schedule does not eagerly generate Side schedules. `PV(SideValuationContext)`
is sealed and delegates to `GenerateSchedule().Evaluate(context)`.
`RequireBeforeValuation` and `RequireAfterValuation` preserve product admission
and result checks. The plain evaluation preserves shared-Side context reuse.
Futures and exchange-traded futures options remain unchanged leaves (D181).

The retained-program schedule prepares occurrence inputs, then invokes each
concrete Side. It never executes a Side's program itself. Gas memberships may
carry an immutable quantity policy, source slot and exact quantity part in
Instrument identity. Absent terms are omitted from canonical identity, preserving
the exact earlier identity for an Instrument without policy or part. Physical
owner days remain trade facts outside identity. The schedule validates them,
computes each entry's own-date quantities once and supplies them through the
occurrence context; the Side owns row pricing, lifecycle and inspection.

Gas draft and stored compositions also retain each entry's physical owner in
the schedule execution identity, including entries without quantity terms.
Each payment uses only that owner's day quantities and clock. The prepared
context and reuse fence include this exact source map; legacy economic canonical
bytes remain unchanged. Direct managed callers with quantity terms can derive
the complete map from their declared membership source slots. With a right and
without a complete map, each physical entry uses its own native geometry and
connected cash uses its declared source slot. Pooled geometry is the compatibility
path for direct calls without a map, quantity terms or rights, and for unsourced
cash outside that explicit connection in a direct rights call. A covered payment
still needs an explicit source for its held day quantities.

## Exercise rights (D448)

An optional right is an immutable Instrument term over ordered, disjoint
memberships. Its period is `delivery_day` or `delivery_month`, its holder `us`
or `counterparty`, and its settlement `physical` or `cash_automatic`. Its
saved group dates, exercise instants, model and day count enter identity. Empty
rights leave the earlier economic canonical bytes intact. Exact mark revisions
belong to the pricing binding. Elections and delivery and payment facts belong
to the exact held trade binding.

The schedule prepares the entry's own held day quantity for exercised days and
zero for abandoned or undecided days. Each Side computes its ordinary rows and
keeps payment state. For an undecided group the schedule also values only that
group's hypothetical days through the same concrete Sides and contributes one
signed holder-choice amount. The Trade applies its external typed quantity once.
Ordinary signed entry contributions plus undecided contributions reconcile to
Instrument PV. The exercised group's Forward sum and Counted amounts show
gross selected rows. A recorded payment changes ordinary remaining PV without
changing those gross amounts. Abandoned groups count zero.

A physical decision requires a visible recorded election. A covered physical
day with a scheduled or allocated delivery must have an exercised election. A payment on an original covered cash row requires at
least one exercised day on that row. A known future change to the applicable
election bounds prepared-context reuse. Cash groups require publication of
every covered index, including a row with zero taken quantity. The available projection continues to price an undecided cash group
after its last fixing instant and payment due date. Gas is the supported
consumer. See
[gas admission, faults and scope](physical-gas-side.md#exercise-rights-d448).

## Mandatory composition boundary (D417)

Every financial instrument derives from `Instrument`. The only products allowed
to have no Sides are `FuturesContract` and exchange-traded
`FuturesOptionInstrument`. Every other instrument composes a nonempty ordered
collection of concrete subclasses of abstract `Side`. A single cash amount,
physical claim or retained program does not create another exception.

Each Side owns its immutable whole-term contractual parameters, declared inputs,
schedule behavior and `PV` implementation. An unbooked Side generates its own
schedule. A booked Side executes its exact retained schedule without regenerating
contractual geometry from current terms. Retained-program execution is an
implementation of a concrete Side; an instrument must not bypass Side by valuing
raw programs directly. Instrument PV combines membership-signed Side results.

Choose a concrete Side for its delivery, cash, fee or asset-specific behavior.
Fixed versus floating describes pricing economics. Reuse proven fixed/floating
price and payment mechanisms inside the appropriate Side implementation rather
than copying them, adding product switches to the common evaluator or treating
all physical behavior as a price-mode variant. A new asset label alone does not
justify another class; a supported behavioral contract does.

Membership identity includes the exact Side reference, occurrence/order,
coefficient and any explicitly supported normalization. Two Sides may consume the
same quantity source while retaining different prices, delivery/payment dates
and schedules. They remain separate contributions. A Side contains neither peer
Sides nor an Instrument whose PV it calls. Shared operating feasibility produces
typed quantity/state outputs consumed by those Sides; it adds no separate PV.

D417 applies this boundary to cash, holdings, rights and retained physical and
operating contracts. Retained Sides own exact saved schedule execution and
inspection, with ordinary signed aggregation and context-bound reuse. The
power/REC assembly and universal Side implementations pass bounded combined local
qualification, including native upgrades, retained workers and actual desktop/phone
flows. The [qualification record](verification.md#instrument-and-side-alignment-qualification-d417)
keeps earlier artifact checkpoints separate.
Existing booking versions and retained identities remain readable under their
original financial and authorization contracts. The rule alone establishes no
new lifecycle, quantity-scaling or financial-model capability.

## Trade ownership

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
are bridged by the common entry point.

D417 requires every non-exchange instrument to own explicit Side memberships.
Only `FuturesContract` and `FuturesOptionInstrument` remain standalone exchange
contracts. A new cash, inventory, service-rights or retained-program model puts
its own schedule and PV behavior on a genuine Side; its Instrument delegates
and composes the contributions. A Side must not call back into Instrument PV.
The managed contract rejects non-exchange held models without complete Side
memberships before declaration or PV, including direct risk-position input.
Adapter and UI integration pass bounded local qualification; the complete CI
matrix remains required before merge.

The unit-to-Side context carries the declared market inputs, exact calculation
clock, measure and held state. It introduces no dummy discount provider. Cash
retains its actual payment row; current commodity and rights values retain
noncash holding rows without invented delivery or payment dates. Paid state,
title, custody and operational performance remain independently owned facts.

`Declare(context)` supplies model version, complete market dependencies, quantity
basis and output currency. Declarations enable shared scenarios and unaffected
work reuse; they do not assign exposures. Risk calls PV under base/up/down
contexts and measures returned values. It does not select pricing formulas by
concrete instrument type. Binding validation, scoped workload estimates and
lifecycle dispatch belong to the instrument contracts.

A declaration may also state the price step of one of its price dependencies:
the smallest move of that price the valuation's model resolves. Inside one step
the value follows the model's grid, so a second difference of it is meaningful
only across whole steps. It is a property of the model, not an input: it
identifies no repricing and assigns no exposure. A step names one directly quoted
live price input. It has two consumers, each reading the steps its scope
declares and refusing a scope in which a holding declares one on any other
dependency: the [trading P&L bump](trading-pnl.md#price-bump) is sized from
them, and the [portfolio risk bump report](risk-workspace.md) refuses a shock
under two of them (D395). The
[listed American futures option](futures-options.md) declares its lattice's
step; a European one, priced by a formula, declares none (D449).

`MarketFactors(context)` returns the canonical shared market factors resolved
from those declarations. The portfolio unions them by exact factor identity,
independently of instrument family: several sides and instruments referencing
the same dated contract contribute one factor to the run. One immutable shock
then reaches every consumer through normal PV evaluation. The dependency graph
retains price construction and exact source identity; curve labels, instrument
IDs and repeated holdings do not create extra market factors.

`SupportsQuantityScaling` permits linear scaling in booked size, independently
of market linearity. A nonlinear market model can still scale by contract count.
`SupportsFixedUnitBinding` instead admits the exact declared complete unit and
refuses duplicate ownership within its portfolio. The separately selected
`SupportsWholePosition` contract admits one normalized positive package for PV
only, with no coverage, execution basis, fractional size, sign reversal or
duplicate whole-position identity across valuation groups. These capabilities
are distinct; a model cannot silently claim quantity scalability to gain entry.

## Retained program Sides

Gas, crude, LNG, operating-right and certificate-forward projections retain their
published v1 ordered `Members`, program bytes and digests as their canonical
financial definitions. Their genuine `RetainedProgramSide` memberships are a
deterministic execution view of those same programs. They do not select a new
schedule generator, compile replacement programs or invent calendar bindings.
The pure calendar decoder retains the exact saved policy, including nullable
synthetic publication evidence. Native API hydration still requires the original
applicable calendar publication proofs.

A correspondence proof validates every membership occurrence's native Side ID,
program, coefficient, quantity ratio, unit and captured calendar before identity,
interning, declaration or PV. Derived Sides are excluded from canonical encoding
only for this internal retained-composition contract; other Sided Instruments
retain their explicit Side definitions in economic identity. Reordered or altered
copies refuse. The proof and schedule wrapper are cached per immutable object;
each record copy receives its own proof. Inspection rows are allocated only when
requested, over the exact stored program addresses.

Each retained Side executes its own program using admitted immutable source
values. The gas Instrument supplies typed connected day geometry and its held
quantity normalization; each Side owns its accrued and paid cash rows. The
Instrument applies explicit signs and ratios and aggregates the resulting
contributions and named measures. Source authority, paid state and valuation
inputs are never cached as economic-definition proof. Runtime model versions
identify this Side execution without silently converting stored v1 definitions.

The ordered occurrence slot belongs to composition, rather than the reusable
Side definition. Connected cash execution receives an explicit occurrence context
with its exact owner, program, geometry, held normalization and binding state.
Reusing a Side must not reuse another occurrence's paid state or value cache;
missing or conflicting occurrence evidence refuses before valuation.

Retained gas source receipts encode timed-cash and commodity-holding definitions
through explicit versioned projections. They preserve native instrument IDs,
exact reference versions, cash timing or holding mark/carrying terms, complete
canonical economic definitions and the selected execution-model version. The
enclosing source retains ownership, quantities, paid state, chronology and native
receipts. Authorized native reads materialize the executable instruments; source
receipts have no instrument-reconstruction operation.

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
Different providers, dependency fences, binding states, operating inputs or
scenarios create different contexts. Retained-program execution additionally
scopes admitted source values and connected physical-day geometry; these inputs
cannot share Side values with another held lifecycle state.
The cache grants no market access and cannot cross runs or historical cutoffs.
Keys use hashed lookup rather than scanning prior scopes. Work reports include
these side calculations alongside interval-side work.

The trade projection uses dense scope indices and existing exact book aggregation.
It avoids recomputing economic identity for repeated references to the same
immutable Instrument instance. Full definition checks apply when different
objects claim the same ID. Calculation reuse does not merge trades, accounts or
events; accounting/clearing netting remains a separate operation.

## Verification

Composition qualification must exercise concrete Side inheritance, nonempty and
independently scheduled memberships, exact retained execution, signed membership
and trade quantity application, shared quantity sources with different economic
terms, and deterministic base/up/down portfolio PV. API and native admission
must agree on the same closed composition, exact references and supported bounds.
Malformed or missing members, duplicate member keys, conflicting definitions
under one Side ID and cross-context inputs refuse. Valid reuse of a Side or
quantity source remains explicit. Neither templates nor product slots may
silently supply a direction, price or payment convention.

Managed checks cover a Side-owned PV model without a product adapter, shared unit values
across books, repeated instrument occurrences in one trade, incorrect units,
stale results, undeclared size scaling and missing execution facts. Forward checks
use independent price/strike/quantity arithmetic, definition interning, shared
floating-side evaluation and isolation between discount providers. Existing
futures checks retain variable lots, precision, immutable terms, offsetting fills
and grouped-risk oracles. Results are recorded in [verification](verification.md).
