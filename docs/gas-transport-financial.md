# Transport rights and independent fixed-fee cash

## Selected contract

The first installed financial Transport profile is
`physical_gas_transport_direct_rights_v1`. A real held Trade owns one complete
rights package over an exact commercial capacity entitlement/version. The user
selects the profile, company perspective, valuation source, package basis,
currency, rights-value policy, fee law and expiry law explicitly. One financial
owner per stable entitlement is supported. Partition, assignment, resale and
ownership amendments require separate contracts.

The profile consumes a company-signed current present value of the **whole
remaining service-rights package**, excluding independent fees and owned gas:
`signed_current_whole_remaining_rights_pv_excluding_fees_and_owned_gas_v1`.
It supplies no automated Transport valuation model. Missing value is unpriced;
an explicitly entered zero is a valid value. Provider perspective does not
reverse a signed mark or choose the fee direction.

The selected source is a genuine direct outright whole-package quote, with
multiplier one, matching exact package/currency reference keys and a final
TraderMark at the evaluation instant. The package quantity is explicitly
declared under this contract and must have a dimensionless count basis; gas
energy or volume cannot stand in for it. Compatibility uses the declared
dimensions, not example codes or labels.

The first profile pins the exact selected valuation definition. Another
definition with equal reference labels is not a replacement. Source-definition
amendment is unsupported. A compatible direct curve/member/series must be live
at the selected valuation instant when booking, although its observation may
still be missing. Fresh mark adoption also requires the selected member to be
live: equality with its expiry is already expired. This uses the selected instant,
independently of today's date, and supplies no future marks or source amendment.

## Separate ownership and source identities

| Owner or source | Meaning |
| --- | --- |
| Commercial entitlement and capacity version | Exact operating contract and dated gross limits |
| Financial Trade and native instrument | Retained agreement, parties, book, immutable pricing/fee/expiry terms |
| Financial state revision | Explicitly adopted capacity version and remaining-rights identity |
| Market observation | Independently published signed package value |
| Mark adoption | Explicit application of that exact observation to one state and valuation instant |
| Ordinary cash membership | Independently owed fixed fee and complete recorded-payment history |
| Chosen capacity/route plans | Operating choices; no implied financial amendment or physical performance |
| Physical activity and gas title | Independent owners and operations |

Operating and valuation reference versions are independent exact foreign keys.
Booking does not clone an old entitlement into a new reference version. Both
selected bundles must remain accessible. Currency references also remain exact:
equal currency codes in different bundles do not authorize netting.

Mark adoption stores the actual definition/curve/contract/series/observation,
source and quote relationships. It does not create a second editable money
ledger or disguise the value as gas or dated cash. Market-data publication keeps
its ordinary permissions; AmendTrade may adopt a published fact but cannot
publish one through the financial operation.

The initial state is born with booking. An explicit `adopt_capacity_version`
amendment may select a compatible current version of the same entitlement.
It cannot predate that version or the preceding financial state. It preserves
the immutable fee and makes the old state's mark ineligible. Equal money
explicitly adopted for a new state is a new application, not an acknowledgement
of the previous state's mark.

Operational plan generations, route claims and membership releases are absent
from the financial-state identity. They do not silently change contractual
rights or renew a mark. Reconciliation must never manufacture a physical flow,
gas title or bank payment.

## Cash and expiry

The required first fee law is
`unconditional_one_off_fixed_amount_at_booking_v1`. Its absolute amount,
independent pay/receive direction and literal due instant are selected terms.
The obligation starts at booking and is independent of use or service expiry.
Nonzero fees create a genuine ordinary TimedCashPayment membership. Explicit
zero requires an explicit absent due instant and creates no cash owner.

Before due, unpaid cash consumes its captured currency discount mapping and
exact-instant discount convention. At due it follows the ordinary cash policy.
The rights input is already current PV and receives no second discount factor.
The installed rate convention requires its valuation UTC date to equal the
selected rate business date. A differing financial business date can value
zero-fee rights or due cash without rates; outstanding discounted fees retain
the rate convention's explicit refusal. No date or discount fallback is inferred.
Payments use the shared recorded-cash operation. Complete known allocation
revision chains resolve knowledge time before recognizing payment time. A known
correction moving a payment into the future does not resurrect an older effective
revision. Every observable knowledge boundary preserves the selected same-sign
fixed-obligation capacity; refund and fee-amendment laws are not supplied.

The required expiry law is
`fixed_window_capacity_rights_expire_at_through_v1`. The product declares the
fixed contractual end; the shared lifecycle coordinator dispatches the selected
saved manual/review policy. No automatic mode is installed for this action.

Live rights at or after Through refuse complete value until the explicit
supported transition is accepted. Expiry creates a sealed closed state at
Through. That state's remaining rights value is known zero without a mark.
Unpaid fee cash, gas ownership and operating records retain their independent
owners. An old accepted acknowledgement cannot reopen the rights.

## Valuation and risk boundaries

A whole position explicitly opts into `SupportsWholePosition` and continues to
declare `SupportsQuantityScaling=false`. Shared Trade and portfolio admission
accept only exactly positive one in its normalized package basis. Fractions,
multiples, sign reversal, delivery coverage, execution bases and non-PV
reinterpretations refuse. Repeating the same whole-contract valuation identity
in one complete scope also refuses. Ordinary scalable instruments are unchanged.

The current rights instrument composes one concrete
`GasTransportDirectRightsSide` under D417. That Side owns its admitted mark,
exact horizon, accepted expiry and declared source fence; ordinary Side
aggregation applies a single positive-one membership. Its claim schedule has no
delivery, observation or payment calendar and refuses payment-row inspection.
It does not manufacture a cashflow from a current monetary estimate. The
published economic/pricing identities and absent retained-schedule identity are
unchanged; the fee remains a separate ordinary cash instrument.

Unit values run through the ordinary black-box PV contract and immutable shared
factor scenarios. The real captured primitive must equal its declared market
request; graph aliases cannot validate one point and value another. Financial
state and accepted expiry identity remain separate from retained agreement and
binding identity. The valuation instrument identity is derived from the accepted
financial state using the versioned `gas_transport_financial_rights_state_v1`
domain. Native binding and retained-instrument identity continue to identify the
agreement. An adopted capacity version or accepted expiry changes valuation
identity without changing the agreement or its independent fee instrument.

For a synthetic customer mark of 20 and pay fee of 12.50 under explicit zero-rate
inputs, remaining value is 7.50. A mark of 21 gives 8.50. An independently entered
provider mark of −20 and receive fee of 12.50 gives −7.50; a provider mark of −20
with a pay fee of 12.50 gives −32.50. The perspective itself supplies neither
sign. After accepted expiry, unpaid fee value is −12.50. An effective payment of
5 leaves −7.50; fully paid fee cash leaves zero. These are remaining values,
excluding wealth held in bank cash.

No gas exposure is inferred from a direct rights mark. Cash retains its declared
discount dependencies. Current-clock price scenarios do not establish complete
economic horizon continuation, cross-clock P&L or generated close support; those
consumers have explicit fences, including source-known agreements becoming
effective inside a requested horizon.

## Application and authorization

Entry uses the shared deal ticket. Financial source discovery is independent
of the gas catalogue role. Booking may be explicitly unpriced; publication and
mark adoption complete its valuation afterward. Retained inspection uses
attested saved choice labels, not new-entry operating discovery. An unavailable
future state has no substituted older editable draft or service horizon.

Capacity selection reads commercial terms at the exact financial instant and
certified business date. It does not invoke the operating screen's end-of-day
plan assessment. A latest global capacity version that is not known or not yet
effective supplies no eligible replacement; an older version is not substituted.
Operating plans remain outside this financial dependency.

Financial reads require current ReadTrades, ReadMarketData and access to both
exact reference versions. New booking additionally requires BookTrade and
ReadGasOperations; adopting operating capacity additionally requires
ReadGasOperations and AmendTrade. Mark adoption and expiry require AmendTrade.
No inventory or physical-facility permission is implied. Optional operating
discovery retains its originally captured absence/deadline.

Scope presence retains the selected books, trades and certified knowledge cutoff.
An empty Transport scope does not impose its valuation-time restriction on other
products. A scope containing a visible Transport agreement still refuses a
valuation instant later than its knowledge cutoff.

Original authority is captured before waiting source/owner reads and rechecked
through native and managed commit boundaries. Exact request recovery and
semantic old-fact acknowledgement precede new source resolution. A receipt may
retain retry coordinates while the accepted typed state/mark keeps its original
provenance; acknowledgement creates no new financial header. Readonly previews
roll back their provisional native writes and distinguish authentic accepted
outcomes from new effects. No dedicated financial ticket/mark assistant tooling
is qualified by the unsupported new-entry page identity.

## Qualification and remaining work

Local checks cover pure ordinary/connected numerical oracles, whole-position
admission, source aliases, provider signs, independent cash histories, complete
native source/receipt/deferred seals, real original-authority waits, populated
upgrade and actual managed reads/values against stock PostgreSQL. Existing
capacity/route witnesses remain unchanged on upgrade. Exact input currency
references and readonly publication heads are checked independently.

The same local artifact passes 32 actual managed source/command/value
checkpoints and 26 full-application browser checks with native postconditions.
The browser covers all five customer/provider and fee variants, mark adoption,
capacity amendment, risk, independent payments, retained receipt recovery and
expiry. The empty portfolio uses its selected exact market source and bound
context. Accepted account changes retain the correct identity even if subsequent
market-date loading refuses. Desktop and phone views were inspected.

The shared lifecycle qualifier captures a current context that already sees the
closed rights, reviews a new-request acknowledgement, and retains the original
state and event time without another financial header. Exact replay, provisional
rollback, strict source expiry, deferred native seals, populated upgrade and
original-authority lock waits pass. Cross-business-date checks preserve the
installed rate refusal above. Managed core passes 744 checks, frontend passes
2,020 and Python passes 155; the API build has zero warnings/errors.

Two actual retained PRICE jobs match their complete ordinary reports, including
independently signed customer/provider values and exact source, scope, native
binding and financial-state valuation identities. Current worker authority and
restricted producer access remain enforced. Later market corrections leave the
retained reports unchanged. These use the same API and SQL as the native and
browser consumers.

First-value market-data authoring still needs its ordinary publication consumer:
correcting an existing observation does not establish entry of a missing first
value. Integrated upstream qualification, staged publication and release are
separate gates.
Automated valuation, source/party/fee amendment, residual service rights,
assignment and complete economic continuation remain separate capabilities.
No production activation is implied.

New and saved Transport tickets report their identity for assistant questions.
They expose no qualified assistant trade read or navigation target; unavailable
reads refuse explicitly while separately selected background Blotter summaries
retain their ordinary scope and authority.
