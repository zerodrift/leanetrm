# Park and Loan contracts

The first commercial profile is `physical_gas_park_loan_v1`, entered through the
shared deal ticket under Physical Gas. It composes existing gas quantity
instruments and independently directed timed cash. Native ownership and cash
operations perform accepted advances and returns. Disposable local managed,
native, browser and retained-price-worker qualification has passed. Aggregate CI,
release and deployment qualification remain separate gates.

The separately selected [outstanding-balance elapsed-hour fee
profile](gas-park-loan-balance-fee.md) reuses the same principal contract with
its own retained fee law and independent cumulative cash determinations.

## Installed terms

The profile requires explicit service kind (park or loan), customer or provider
perspective, positive principal, title-transfer treatment, one exact point and
quantity basis, one exact nominal mark, dated commitments, equal partial returns,
and an independent fixed fee. The fee's magnitude and pay or receive direction
are separate inputs. The selected profile explicitly determines fee cash on
accepted advance (`accepted_advance_v1`); its due time cannot precede planned
advance, and preadvance payment is not an installed capability. A zero fee retains
the explicit direction and an explicitly not-applicable due time.

A customer borrowing gas, or a provider accepting parked gas with title transfer,
receives principal. The other two service and perspective combinations give
principal. This closed mapping belongs to the selected profile; neither cash
signs nor presentation labels determine title.

The installed advance window starts at the selected advance time and excludes
the return due time. It permits one full advance and partial returns strictly
after accepted advance. Late returns remain recordable, and passing return due
does not discharge principal. Custody-only arrangements, multiple advances,
other points or quantity bases and additional return rights require further
qualified profiles. Balance-time charges use the separate profile linked above.

## Independent owners

Booking future commitments creates no actual stock or operator claim. Before
advance, the agreement owns two ordinary nominal gas components with opposite
principal quantities at the exact same mark, plus a separate timed fee component.
The absolute fee is never normalized by principal quantity.

Accepted advance transfers the relevant contributions to native owners exactly
once. A receiving party gains stock and an independent return liability. A giving
party dispatches identified owned stock and gains a return claim in its explicitly
selected compatible operator account. The independently directed service fee
becomes its own native cash holding. The originating service components then
cease to contribute, preventing duplicate exposure.

Stock use does not repay a loan. Principal return does not pay a fee. Remaining
stock, claims, liabilities and unpaid cash survive the service's principal
completion. Whole-book valuation includes those independent owners under the
explicit native ownership policy. The trade's remaining scheduled-plan value
covers only its retained service contributions.

## Entry and operations

The managed description supplies editable terms, exact choices, time controls
and validation. Ownership and party fields remain explicit. Selected inventory
and return accounts must match the retained book, entity, point, quantity,
currency, market definition and mark pins; a return claim account also matches
the contract counterparty. Future import and agent authoring adapters must use
these same native business operations; dedicated authoring for those entry points is not qualified here.

The shared Lifecycle workspace offers reviewed manual advance, return and fee
payment actions through `gas_park_loan`. Each selection retains exact source
evidence, stock or reservation where applicable, native policy versions, service
version, context and request identity. Preview executes the native producer in a
rollback boundary. Execution checks the reviewed source and current authority
under the native publication lock. Exact accepted retries recover their original
outcome before attempting a new source resolution.

Retained terms and operating balances remain inspectable when a position-specific
valuation input is unavailable. Such a read carries an explicit unavailable
value; it does not replace the amount with zero. Operating times use the shared
local-time editor and retain exact UTC instants. The display zone changes
presentation, not contractual timing.

## Temporal behavior and qualification boundaries

Core relationships use exact-version foreign keys. Native evidence, accepted
financial effects, command outcomes and payment allocations retain their
independent identities and publication lineage. Reads carry business time and
knowledge cutoff; a later correction cannot replace a retained historical fact.

This first profile exposes corrected source evidence as requiring reconciliation.
Service-linked principal or fee-determination changes without a qualified
continuity adapter refuse rather than bypassing the service allocation ledger.
Independent bank-payment revisions retain the existing native cash contract.
Their historical summaries select the source-known revision before applying its
payment time, so a later-dated correction cannot revive an older payment. Broader
quantity and fee-determination reconciliation remains separate delivery scope.

Current-clock value and price scenarios use ordinary instrument repricing and
immutable shared factor scenarios, including retained queued price jobs. The
Risk workspace offers the explicit contractual-only or whole-book independent-gas
pricing policy. The latter requires current gas-read authority and is unavailable
for an individual-trade selection; changing it invalidates setup and current
report comparison without changing a retained request. Worker
reads retain the original exact book or selected-trade scope, input certificate
and current actor and worker authority. No independent-ownership pricing policy
is inferred for an unadvanced agreement; source authority remains required.
Generated close activation and economic horizon continuation through an unadvanced service require separate
qualification. An unsupported continuation must refuse, including when an
agreement is already known but becomes effective only after the opening instant.
A known future contract cannot disappear from economic risk because the opening
book has no current service position.

## Verification

Managed checks exercise all service and perspective directions, principal
conservation, the bounded advance window, partial and overdue returns, independent
fixed fees, reusable component identities and generic market scenarios.
Disposable native checks exercise booking without title, real preview rollback,
exact and semantic retries, historical cuts, current authority, source
corrections, direct-operation guards and independent surviving fee cash.

The real application browser qualifier passed 68 checks over booking, retained
inspection, typed lifecycle forms, actual movements, independent fee payment and
uncertain-response recovery. All four service/perspective combinations exercise
normal Risk controls after advance, with exact native owner memberships and
independent base/up/down values. Twenty native whole-book value and scenario
checkpoints cover all four service and perspective combinations. Nine actual
retained price jobs match ordinary and retained reports with independent values
and owner memberships; original actor revocation, natural worker-grant expiry
and unchanged source bytes are independently checked. A populated upgrade
preserves existing owners and cash and rejects nine predecessor-function drifts.

Independent adversarial findings in temporal heads, native policy changes,
worker admission and UI request handling were resolved and checked. The required
integrated runner and aggregate CI still gate the exact release commit. These
local qualifiers used a pinned .NET 10.0.402 SDK, stock PostgreSQL 16 and an owned
Chromium application instance; production was not used.
