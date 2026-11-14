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
