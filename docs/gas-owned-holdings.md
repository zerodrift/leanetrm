# Independently owned gas holdings

Gas holdings are independent native economic owners. Completing or excluding a
source trade does not remove remaining stock, an operator claim, a commodity-loan
return obligation or its cash. Ordinary `Instrument` pricing consumes exact
retained mark, quantity and currency definitions; quantity belongs to the held
position rather than the reusable instrument definition.

An account selects an exact book, legal owner, quantity specification, location,
mark source and currency. Operator accounts additionally identify the exact
operator counterparty. Title and evidenced loss use explicit versioned policies.
Reference and mark relationships use exact native foreign keys.

| Native interest | Financial meaning | Discharge |
|---|---|---|
| Owned stock | Positive gas to which title was accepted | Identified, physically available stock is delivered, returned or lost under accepted evidence. Reservations consume availability, not ownership or PV. |
| Operator balance | A signed contractual quantity claim or debt; it is distinct from owned stock | Explicit quantity performance or final-price cashout under the saved terms. |
| Commodity loan | Accepted title to fungible gas plus a separate negative return position and explicit fee cash | Identified stock is returned under accepted evidence. Selling the borrowed stock leaves the return liability. |
| Physical-future delivery obligation | The remaining acquisition or dispatch obligation at the accepted final price | The qualified physical-futures adapter links title/dispatch, identified stock and consideration. Final margin alone does not transfer title. |

Operational observations are immutable source-key revisions. Unmatched or
corrected evidence can be recorded without manufacturing a financial holding.
Financial application requires the latest eligible evidence and exact native
links. Applying a source twice cannot create additional stock or claims. A
correction to already applied evidence requires reconciliation; the valuation
loader refuses to silently reinterpret its accepted quantities.

Owned quantities, reservations and consumption conserve each lot at every
effective event boundary. Owned quantity can remain positive before its
availability instant, while available quantity is zero. No operation creates
negative owned stock, infers procurement or extinguishes a return because its
due date has passed. An explicit title-transferring loan creates positive stock
and an equal negative return; the lender and fee terms are retained separately.

Operator quantity performance either creates a newly titled lot in an explicitly
selected compatible owned account or consumes an identified existing lot. The
operator interest is reduced by the same quantity. The native relationship
prevents the same receipt or dispatch from discharging two interests.

The cashout baseline saves an exact source, observation role, fixing instant,
cash due instant and `payment_total_half_even_2dp_v1` rounding contract. Only the
single final source head visible under the selected source context can determine
cashout. Partial determinations use differences of cumulative rounded totals.
Zero cashout can extinguish quantity without inventing a cash holding. Before a
past fixing has been accepted as cashout, the loader refuses to value the
remaining claim using a later variable gas mark. This baseline does not provide
an automatic correction or reconciliation executor.

Cashout and loan fee cash survive the quantity position. Actual payment amounts
and corrections are separate immutable allocations. The ordinary cash pricer
retains unpaid balances and exact due instants. A due date does not mean paid.
The quantity ledger does not attest missing purchase cash or capital transfers;
complete economic P&L must obtain the native source handoff and transfer facts.

`GasOwnedActions` uses the common lifecycle dispatcher with closed typed
selections. Preview runs the same native producer under a savepoint, validates
the deferred conservation constraints and rolls back every proposed effect.
Its hash pins normalized operands, explicit policy and the exact source
certificate, excluding generated effect identifiers. Execution rechecks the
current saved action binding and native account heads. Current authority and the
original admission deadline are checked after waits and immediately before
commit. A receipt is reconciled before resolving a historical source again.

Native economic facts use the common trading publication stream. Certified reads
retain both effective time and knowledge cutoff, including payment correction
history. Native rows are immutable and runtime access is through guarded
functions. Repeating a request returns its first outcome; repeating a business
fact acknowledges the existing effect without another financial publication.
Acknowledgements distinguish the acknowledged revision from the current head;
acknowledging an older fact does not reactivate it or rewind the current state.

Whole-book valuation selects `gas_owned_mark_to_market_v1` explicitly and loads
independent owners by the selected books. A book with owned gas cannot return a
complete valuation without that policy. A selected-trades request has its own
scope and cannot also select whole-book gas ownership. The loader retains exact
native owner/binding identities, source bundles and surviving cash; it never
finds inventory through an originating trade. Future cash and currency-carried
claims consume the selected currency discount mapping and pinned rate inputs.
There is no constant discount fallback. The disposable whole-book check covers
a gas-only book with six native holdings and an independent zero-rate oracle.
Retained-worker authorization and event-aware projection have separate gates.

The focused checks are `--gas-owned-holdings` in the managed domain checks and
`tools/check_gas_owned_holdings.py` against disposable PostgreSQL. The native
fixture also invokes `--gas-owned` API checks for the ordinary portfolio and
review/execute boundary. Projected title/performance and connected supply
allocation are a separate lifecycle-risk adapter; these actual operations alone
do not qualify a complete event-aware VaR result.

Surviving owned stock may select a new exact mark through an immutable
`mark_binding` revision. The binding preserves native ownership and quantity;
its selected market definition, contract and curve form a new unit instrument
identity. Exact historical knowledge retains the earlier mark. This operation
does not roll stored closes, delivery terms or source observations, and requires
the same exact quantity specification, currency and location.

Quantity projection profiles are selected by exact account, profile identity and
revision. Each account explicitly retains quantity obligations or performs their
full remaining quantity at the contractual due instant. Receipt assumptions name
the destination account, inventory mark and availability convention. Supplied
returns name owned lots and reservations or an explicitly linked incoming
receipt. Connected accounts retain their separate policies and original economic
owners; the combined component values each residual once. A projected return
consumes selected stock and reduces its liability without adding an internal
transfer gain. Received gas continues as inventory. Due dates alone never create
accepted title or actual performance.

The knowledge-only gas journal captures accepted future-effective facts without
advancing the opening clock. Scope checks detect these facts even when the
opening book has no inventory. An independent future opening stock or claim
requires its purchase, capital or transfer counterfact; missing coverage cannot
be treated as zero capital. The current quantity adapter refuses a horizon that
crosses an unqualified native physical transition or cashout. Facts strictly
after the horizon remain retained evidence. Event-aware physical principal,
cashout and the remaining future-event adapters have separate qualification
gates before a complete economic portfolio result can be reported.

Saved profile choices retain the selected source and current read authority,
including accounts whose accepted effective time is still ahead of opening.
Authorization deadlines are operation-local admission data and are excluded
from immutable source and profile evidence. New financial commands reject
source contexts that omit later mark or profile heads; original command receipts
and older business-fact acknowledgements retain their first outcomes.

Physical-futures performance is a separate observed operation. Final margin
creates the signed ordinary delivery obligation under its exact enrolled
delivery profile. A long performance requires accepted positive title evidence
and creates a separately owned lot. A short performance consumes explicitly
selected compatible lots and reservations. Neither path infers procurement.
The ownership account's current execution-policy binding authorizes performance;
the profile's original enrollment policy remains retained financial evidence.

The managed principal planner sorts the complete source-known performance
prefix by performance instant and UTF-8 business reference. This includes
accepted performances that remain future-effective at a correction's selected
instant. It rounds cumulative signed consideration under the profile's saved
increment and tie convention, takes each performance's difference from the
previous cumulative total, and subtracts its earlier accepted contribution.
A backdated performance can therefore revise a later performance's contribution.
A changed final basis retains a complete new determination prefix even when
every cash adjustment is zero. Native constraints independently validate the
submitted operands, chronology, prior lineage and complete cash membership.

Principal adjustments have their own native cash identities under the delivery
obligation owner. They retain the contractual principal due instant and the
original performance effective instant. They remain independent of fee cash
and trade-owned payment memberships. Payment allocations and their corrections
are immutable; an allocation before the principal membership exists is refused.
The ordinary gas portfolio loads remaining delivery, surviving stock and
principal cash separately, including principal after quantity is fully performed.

Final margin and its reviewed principal continuation commit in the same native
publication. The existing margin command owns an immutable combined first-outcome
receipt, binding the complete submitted continuation. Retrying it needs no
recomputed source frame. Shared lifecycle receipts validate the exact underlying
margin request and combined outcome. Older accepted snapshots can be acknowledged
without rewinding current margin or principal. Existing cash-only margin receipts
retain their exact outcomes without a delivery receipt or history rewrite.

The physical checks are `--gas-delivery-principal`,
`tools/check_gas_futures_delivery.py` and
`tools/check_futures_delivery_cash_receipts.py`. Their disposable native fixtures
cover future-known and backdated principal prefixes, zero-cash rebasing,
independently valued delivery obligations, typed actual review/execution,
first outcomes, payment origin and source-knowledge boundaries. Observed quantity
or title corrections require an explicit reconciliation operation; changing source
evidence alone does not revise accepted stock. Event-aware physical projection,
source reconciliation and the operational UI retain their separate release gates.

The knowledge-only physical attachment reader retains the exact native future,
delivery owner, adopted profile, accepted final revisions, future-effective
performances, principal prefixes, supplied/received stock and payment revisions.
An explicit parent-binding selection returns its causal dependency facts rather
than every position in the same account. This source has a distinct mandatory
dependency-scope contract; it cannot replace a complete whole-book gas journal
or select sibling assets for valuation. It establishes no projected title.

Applied-source checks resolve operational evidence heads and operator cashout
observation heads at the captured knowledge cutoff before considering effect
times. Corrected sources do not rewrite accepted effects. Complete ordinary
valuation refuses unresolved effects effective by its selected instant;
economic projection refuses them when effective by its horizon, including a
fully consumed source lot or an extinguished claim with surviving cash.
Operational and configuration readers remain available for repair. An older
certified view retains its original accepted source. Reconciliation requires an
explicit financial action within its qualified scope.

The physical quantity amendment consumer retains the observed performance,
business reference, title instant and availability. It records immutable
financial quantity revisions linked to the exact original performance, stock
and identified consumption versions. Each accepted amendment recomputes the
complete chronological principal prefix, including later source-known
performances. Principal differences become separate signed cash memberships;
existing bank payments and historical views remain unchanged. An acknowledgement
of an older fact reports its acknowledged revision and the current revision
without reactivating that older state.

Raw source quantities are bounded allocation capacity. They do not establish
stock for an unused remainder. A correction explicitly selects either
`preserve_unallocated_source_quantity_v1`, which preserves source capacity minus
the complete allocated quantity, or `withdraw_all_source_allocations_v1`, which
requires an explicit zero-capacity source revision and closes every allocation
from that source. Stock replacement names all existing consumption revisions and
each replacement lot; it supplies no implicit procurement or negative stock.

The current amendment consumer handles one performance and requires all affected
source allocations to belong to that performance. A shared-source batch is
required for corrections spanning other performances or already consumed receipt
stock. General operator and loan amendments, and changes to observed event times
or advance-payment treatment, remain separate implementation gates.

`tools/check_gas_physical_known.py` qualifies the attachment scope and complete
native histories. `tools/check_gas_applied_sources.py` independently corrects a
fully consumed dispatch source and an extinguished cashout price, verifies the
old views and operational reads, and checks both valuation and horizon gates.
`tools/check_gas_quantity_reconciliation.py` qualifies native and managed
same-instant amendments, complete principal refunds, partial source allocation,
explicit stock replacement, immutable old views and first-outcome recovery.
