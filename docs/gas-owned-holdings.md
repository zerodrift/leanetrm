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
remaining claim using a later variable gas mark. Determination does not
automatically apply a later source correction; the explicit reviewed correction
contract below retains its own native admission and complete prefix.

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
scope and cannot also select whole-book gas ownership. A two-date P&L run over
whole books selects no such policy and proves the absence at each date,
including a date that holds no trades (D452). The loader retains exact
native owner/binding identities, source bundles and surviving cash; it never
finds inventory through an originating trade. Future cash and currency-carried
claims consume the selected currency discount mapping and pinned rate inputs.
There is no constant discount fallback. The disposable whole-book check covers
a gas-only book with six native holdings and an independent zero-rate oracle.
Retained-worker authorization and event-aware projection have independent
qualifiers described in [economic projection](lifecycle-projection.md).

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
Within this quantity-only operator/loan adapter, a consumed transfer requires the
same exact mark definition, curve and contract on the obligation and stock. Equal
prices do not admit a different basis. Such a basis needs its own qualified full
continuation contract; an unused future choice does not trigger an early mark read.

The knowledge-only gas journal captures accepted future-effective facts without
advancing the opening clock. Scope checks detect these facts even when the
opening book has no inventory. An independent future opening stock or claim
requires its purchase, capital or transfer counterfact; missing coverage cannot
be treated as zero capital. The current quantity adapter refuses a horizon that
crosses an unqualified native physical transition or cashout. Facts strictly
after the horizon remain retained evidence. Qualified physical continuation
requires its complete native parent, performance and principal proofs. The
accepted-quantity cashout adapter requires complete source-known coverage of the
remaining claim; it does not combine that coverage with an uncommitted cashout
expectation or infer independent future capital.

Saved profile choices retain the selected source and current read authority,
including accounts whose accepted effective time is still ahead of opening.
Authorization deadlines are operation-local admission data and are excluded
from immutable source and profile evidence. New financial commands reject
source contexts that omit later mark or profile heads; original command receipts
and older business-fact acknowledgements retain their first outcomes.

Whole-book projection scope captures the original market, trade and selected gas
read deadlines before base-authority or source work can wait. Its native reader
and registered commit callback recheck that same minimum after all exact-source
checks return. A later grant cannot extend an admitted read. An explicitly empty
scope without a gas valuation policy does not acquire an unrelated gas deadline.
The public `tools/check_gas_projection_scope.py` qualifier exercises both wait
boundaries, exact source tuples, upgrade guards and actual historical,
Monte Carlo and cancellation workers using isolated synthetic records.

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
source reconciliation and typed operational forms have separate native, managed,
worker and browser qualifiers; schema activation remains a release decision.

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

The single-performance amendment requires all affected source allocations to
belong to that performance. The native batch consumer accepts an explicit
complete set of performances in one account, including withdrawal of a receipt
already consumed by a dispatch and replacement with identified, actually owned
stock. It advances all selected financial heads in one publication and submits
one complete principal continuation per changed obligation. Unchanged selected
members retain their exact financial revisions. Its deferred seal independently
checks the closed command, member closure, reported quantities and principal
versions. An older acknowledged snapshot never rewinds a mixed batch.

The same native producer supports managed batch review and execution. Its
classified source reader retains exact account and performance identities,
current financial heads and the earliest original read admission. Review shows
the complete source allocation and one principal prefix per changed obligation;
unchanged obligations need no financial continuation. The native command seals
every retained continuation step to the exact accepted principal run, including
causal predecessors and chronological order. An exact retry returns its first
outcome without reloading the source frame. The operational batch form retains
a separate qualification boundary. Loan quantity, fixed-fee and operator
amendments use their own qualified counterpart contracts below. Changes to
observed event times or advance-payment treatment remain outside these profiles.

The ordinary title and dispatch correction source reader retains exact selected
financial heads, applied and proposed evidence, captured source heads and linked
known resource facts. Its mandatory dependency-scope marker does not represent a
whole-book portfolio. The complete dependency families and combined encoded size
are admitted before result arrays are aggregated; an excessive scope is refused
without truncation.

An explicit manual correction can revise opening owned title and ordinary
delivery or loss quantities while retaining their native identities, event times
and source keys. Every affected allocation of each source is supplied; preserving
unused source quantity and withdrawing all allocations are distinct selected
contracts. Unused source capacity never becomes owned stock. Identified stock,
reservations and all accepted resource prefixes must remain valid. The existing
delivery/loss source-magnitude interpretation also applies to signed evidence.
Linked futures performances, operator settlements and loan terms require their
own complete counterpart corrections.

The shared action handler previews the same native producer under rollback,
reviews the complete source allocation, and rejects stale reviewed inputs. One
publication seals the exact request, quantity revisions, source receipts and
reported outcome. A retry returns its first outcome before reloading its source;
an older identical fact acknowledges its retained revision without rewinding the
current financial state. Original authorization terms remain binding through
publication waits and commit, even if a successor grant becomes active. The
operational form reads exact source-known title and ordinary-use choices,
including retired titles, and loads each selected member's financial revision
and source evidence separately. Quantities, retraction and allocation contracts
require explicit choices. Source heads are selected before event-time filtering;
an unavailable newer head cannot revive an older document. The same closed
ordinary, physical and linked-batch contracts are described to assistants.
The owned full application browser qualifies observed-source configuration,
lazy member selection, reviewed correction and exact retry after an uncertain
acknowledgement. Loan quantity and fixed-fee amendments have their own qualified
contracts below, as do operator corrections. Changed event-time or advance
treatment remains outside these amendment contracts.

Fixed loan fees have immutable versioned terms rooted in the exact original
accepted borrow, including an explicitly zero fee without an invented cash row.
`gross_timed_fixed_fee_adjustments_v1` records a complete prior-fee reversal at
its original due instant and a complete revised fee at its selected due instant.
A due-only amendment therefore retains two dated gross legs even when their
amounts sum to zero. Original cash and every bank-allocation revision remain
unchanged. A revised zero requires an explicit null due time; an explicitly
overdue fee remains unpaid until an actual bank fact is recorded.

The shared manual action and local-time form retain exact loan/fee revisions,
selected adjustment law, source and policy. The review shows source/revised terms,
all gross cash, bank history and the proposed signed adjustments using backend
decimal text. It does not scale fees with quantity or infer netting or refunds.
The independent native seal validates the closed request and complete typed
membership, excludes unrelated financial and availability effects, and rechecks
the original deadline after outcome work. New financial changes require the
current source closure; an acknowledgement reconciles a retained fact without
rewinding the current terms. Exact retries precede source-frame resolution.

`tools/check_gas_fee_browser.py` qualifies native faults at immediate constraints
and actual commit, source/policy/time boundaries, ordinary cash PV, the real form
on desktop/mobile and lost-response retry. `tools/check_gas_fee_existing_origins.py`
seeds native loans before migration 0308, proves unchanged predecessor financial
and receipt rows, and runs the same amendment consumer after upgrade. Both run
in the gas CI group on disposable stock PostgreSQL. With an explicitly
selected discount factor of 0.95, fee −3 less recorded payment −0.5 has unpaid PV
−2.375; an explicit zero then leaves refund-receivable PV +0.475. No bank refund
is manufactured by either transition.

The read-only operator-correction source retains one exact claim's complete
known settlement prefix in original publication order, including future-effective
settlements. It exposes saved cashout terms, receipt and funding lots, known stock
uses and reservations, original cash and bank revisions, applied evidence and
captured correction heads. Its dependency scope is explicit and its encoded
families are bounded before aggregation. Native checks qualify cashout and signed
quantity counterparts, earlier knowledge prefixes and expiry during the final
source guard: a successor permission does not extend the original deadline.
The bounded native operator writer admits the explicit
`gas_operator_quantity_reconciliation_v1` contract for an account-scoped set of
claims and each claim's complete known settlement prefix. The immutable selected
`replay_original_settlement_publication_prefix_v1` law preserves original
settlement order and saved fixing coordinates. A corrected final observation at
those coordinates is an explicit input. Each step retains its previous prefix
revision, cumulative quantity, exact total, rounded total and cash contribution.
Only the difference from its prior contribution creates new signed cash, at the
explicitly selected adjustment due instant and original settlement effective
instant. Original settlements, cash and bank allocations remain immutable.

Native checks independently qualify `.75/2.25` quantities at price `1.005` as
`.75/2.27`, with adjustments `-.25/+.25`; fixing-only corrections for both signed
entitlements; versioned successors with zero cash difference; zero and negative
prices; exact historical prefixes; first outcomes and old-fact acknowledgements
without rewinding current heads. Typed members and exception-free deferred
checks reconstruct prefix geometry and the stored outcome. The preceding fee
consumer's closed publication seal also excludes the new operator family.
Physical corrections retain the exact original receipt or funding-use identity
and append its selected quantity/evidence revision with the settlement revision
in one publication. Native checks qualify claim/title `10→9` with zero claim
residual and stock `9`, then the unchanged known future loss leaves stock `7`.
A signed debt settlement `2→1.5` retains its funding lot and leaves stock `1.5`
and debt `−1.5`. Protected ordinary holdings and known-history reads resolve the
same financial heads while preserving original publication order and knowledge
prefixes. A fresh request acknowledging the old physical fact preserves the
newer pair. The selected cashout ordering law is always explicit; an all-return
selection supplies the required cashout object with an explicit null adjustment
due instant. Reversing the incoming settlement array changes neither the selected
native order nor its cash result, and the retained request preserves that array.

Ordinary delivery/loss corrections explicitly name each native use, quantity,
evidence, funding lot, reservation, retraction and allocation policy. Only
source-known uses effective by the selected clock in the connected inventory
accounts are admitted. Loan, operator-settlement and futures-linked use IDs stay
outside this ordinary family. Reducing title below its surviving use refuses;
selecting title `1` and loss `1` together leaves zero stock and zero claim residual.
A complete zero withdrawal retains all native identities and zero financial heads.
An allocation-policy-only accepted state advances its typed heads once, including
at zero value; later requests acknowledge its exact snapshot without rewinding a
newer policy.

Native fault checks qualify exact original cash actor, publication and effective
coordinates, source-context refusal and no-effect receipts with empty source/cash
members. Members cannot be appended to a retained command; each family also
revalidates the complete header and original authority at deferred validation.
Preexisting-data checks retain every original gas, publication and knowledge row
and enroll only exact native origins. Upgrade checks refuse all tested function
source, execution-shape, ownership and ACL changes and preserve public OIDs/ACLs.
Actual commit and immediate-constraint probes also qualify the exact current-read
publication, complete raw-settlement origin enrollment and the original native
authority after its last quantity proof. Applied observation conflicts identify
the selected financial head's publication and knowledge time; causal settlement
order continues to use the original publication.

Original operator performance commands acknowledge exact accepted financial
snapshots without another publication, allocation or cash membership. The native
revision and first command outcome remain unchanged; acknowledged and current
financial revisions and quantities are separate fields. `cashId` and
`effect.cashAmount` retain the original gross membership, while
`acknowledgedCashAmount` and `currentCashAmount` report accepted contributions.
`originalCashAmount` is null when no original cash membership exists. Native
checks qualify an original zero contribution followed by an adjustment of `1`,
with the original cash identity still null. Matching snapshots belonging to two
distinct native settlements refuse as ambiguous.

A new native performance consumes the accepted current quantity prefix. The
independent cashout example changes the entitlement to `3.25` and the settled
prefix to `.75+1.5`, whose cash total is `2.26`. A new quantity of `1` contributes
`3.27−2.26=1.01`; it does not reuse the original raw prefix. A later fixing
correction replays all three members and retains each original cash membership
and its birth-prefix revision. A new identified physical performance of `1.5`
after an accepted `1.5` use exhausts stock `3` and debt `3` without cash.
Continuation upgrade checks refuse 272 tested dependency, execution, permission
and view changes, while retaining all nine existing function identities and the
exact current-settlement view.

A separate complete physical book qualifies ordinary old/current PV and native
known-history revisions, the unchanged loss crossing the explicit horizon, and
actual historical/Monte Carlo workers with retained replay and cancellation.
The actual application qualifies ordinary-use and zero-state forms, exact
lost-reply retries, current authority loss and restoration, and independent native
balances. `tools/check_gas_operator_ordinary_value.py` also qualifies both corrected
books together through ordinary PV, known history and actual retained workers.
At the original source instant and current knowledge cutoff it retains exactly
five owners: stock `8`, funding/debt `+1.75/−1.75`, and the withdrawn book's
surviving funding/debt `+1.5/−1.5`. Zero claim, receipt and use heads retain their
native revisions and ancestry without returning as valued owners. The known
one-unit loss leaves stock `7` at the explicit later close, so total value moves
from `8P` to `7P`. Independent per-owner shocks and the half-open native history
qualify the signed tail `(P+0.77) × quotation multiplier`. Historical and 32-path
Monte Carlo workers reproduce the complete interactive reports; retained replay,
conditional gas authority and actual source-load cancellation leave all native
financial, cash, bank and receipt facts unchanged. These local proofs do not
authorize schema activation or establish an arbitrary product combination.

The bounded loan quantity amendment pairs the exact native received-stock and
return-obligation owners with a versioned loan principal. A reviewed correction
names the selected title and agreement revisions, both position revisions and
every changed dispatch or identified repayment use. It retains each use's
original clock and business reference. Complete stock, reservation and repayment
prefixes, including source-known future facts, must remain valid. Source
allocation remains explicit; fixed fee memberships and recorded bank payments
are retained independently rather than scaled with principal.

The native reader and writer and the default shared action handler are qualified
for same-instant loan principal corrections with separate repayment funding.
Original authorization bounds apply through waits and commit, and retained
receipts precede mutable source reads. Independent seals reject altered request,
member and outcome facts. Source-backed forms retain the loan, stock and debt
revision tuple and lazy-read each exact editable use or repayment. Quantities,
source allocation, retraction and fixed-fee treatment require explicit choices.
Read-only native eligibility excludes linked operator and futures uses.

A complete zero withdrawal removes retired loan and repayment quantities from
the active managed view. Their native identities, zero financial heads, evidence
and cash links remain available. Isolated checks qualify current and historical
source views, ordinary whole-book value and fee-only continuation with no
quantity-profile selection. The public synthetic loan fixture is shared with
the application qualifier. `tools/check_gas_loan_quantity_browser.py` qualifies
the real lazy source form, exact revision and allocation review, separate fixed
fee/bank history and lost-response retry through the native API. Nine browser
checks and independent stock/debt assertions cover desktop and mobile; narrow
reviews stack facts into readable full-width values. A separate native check
also qualifies positive-gross fully repaid loans using rate inputs alone, with
no commodity mark or quantity profile, through ordinary PV and economic cash
continuation. The normally configured agent path also qualifies reviewed loan
quantity and fixed-fee amendments, exact retained retries, and refusal after
agent-role or delegator-permission loss without financial mutation.
Operator and fee-term counterparts, mixed linked effects and changed clocks are
not covered by this bounded writer.

The shared gas-read boundary rechecks the finite original authorization deadline
after all delegated source and scope checks return. A successor grant cannot
extend that invocation. Its public qualifier exercises the ordinary quantity
choice reader at this final boundary and rejects changed predecessor source,
execution metadata or privileges. Historical, Monte Carlo and cancellation worker
checks remain in the separate projection-scope qualifier against the same native
boundary; no worker permission or source scope is broadened.

An original dispatch, loss or repayment command can acknowledge an already
accepted positive financial quantity snapshot. Matching retains every immutable
original field, the exact native event and, for repayment, its loan link. It
records no second resource use or publication. A new acknowledgement reports
its accepted financial revision separately from the current financial revision
and quantity; a historical snapshot cannot rewind the current head. Original
operation receipts retain their first outcomes. If the requested tuple matches
distinct native events, the command refuses the ambiguity instead of selecting
one. Zero retractions continue through the explicit correction contract.

The opt-in agent-only `gas_lifecycle_operator` role contains exactly
`ReadMarketData`, `ReadTrades`, `ReadGasOwnedHoldings` and
`RecordGasOwnedHoldings`. It creates no principal, assignment or delegation and
grants no configuration or job capability. Human and service administration
cannot assign it. Delegated operations still require the live agent, delegator,
workspace and source intersection and the same explicit review. Losing current
agent or delegator authority also refuses access to a retained write receipt.
Settings › AI saves the additional operating role explicitly with the agent
version, independently of allowed delegator roles. Publish activates None or
Gas and ends old delegations when that choice changes. A fresh user grant must
cover the version's exact gas and tool permissions. The reader role remains;
neither selecting Gas nor publishing it supplies the user's missing gas rights.

An owned title that funds an accepted futures dispatch can require one linked
correction: reducing the title first exceeds stock capacity, while replacing the
dispatch allocation first leaves its applied title source unreconciled. The
`ordinary_futures_quantity_bundle_v1` contract takes the complete ordinary and
physical selections, both exact accounts and both saved manual policy versions.
It preflights every selected member under the original knowledge prefix,
including unchanged physical members, before either product writes. Both native
product journals then share one publication. Typed membership, source allocation
and independently reconstructed child and outer outcomes seal the composition.
It changes neither accepted bank payments nor original fee memberships.

An existing linked fact returns its first outcome or acknowledges its retained
financial revision without another publication or a rewind of current state.
Historical acknowledgements retain a read publication even when no financial
publication exists. The independent seal validates closed nested request shapes
before counting or iterating members, including on acknowledgements. Original
authority deadlines apply through waits and final commit admission.

`tools/check_gas_physical_known.py` qualifies the attachment scope and complete
native histories. `tools/check_gas_applied_sources.py` independently corrects a
fully consumed dispatch source and an extinguished cashout price, verifies the
old views and operational reads, and checks both valuation and horizon gates.
`tools/check_gas_quantity_reconciliation.py` qualifies native and managed
same-instant amendments, complete principal refunds, partial source allocation,
explicit stock replacement, immutable old views and first-outcome recovery.
`tools/check_gas_quantity_reconciliation_batches.py` qualifies the native linked
batch, principal conservation, unchanged bank payments, immutable first outcomes
and malformed-command rollback through the independent deferred seal. It also
checks the actual default-registry managed preview, source allocation and
principal review, stale-hash refusal, atomic execution and evicted-frame retry.
`tools/check_gas_owned_reconciliation.py` checks the ordinary source primitive
against native observed title/dispatch facts and a synthetic oversized read
provider without changing financial rows or disabling their integrity seals.
`tools/check_gas_owned_reconciliation_writes.py` checks actual native and managed
ordinary corrections, shared-source allocation, signed evidence, immutable old
views, stale heads/reviews, unknown sources and first-outcome recovery. Its
rollback fault probes also exercise malformed stored receipts at both immediate
constraints and commit, and original authorization expiry with an active
successor grant.
`tools/check_gas_owned_quantity_choices.py` additionally qualifies the actual
main and lazy ordinary form sources and refuses an excessive UTF-8 response
envelope, including documents whose JSON escaping is larger than their binary
database representation. The browser checks retain exact row/source identity
and show acknowledged and current revisions separately without deriving money.
`tools/check_lifecycle_browser.py` runs this form through the real API and an
isolated native database, retaining the scheduler, physical final and title
checks while also verifying the ordinary correction's stable native owner,
financial revision and first outcome.
`tools/check_gas_lifecycle_agents.py` qualifies the actual delegated ordinary
review, approved execution and exact retry, denied read-only/revoked/foreign
contexts, role assignment boundaries and current authority loss. Its migration
probes independently reject changed definitions, function shape and privileges
of the existing agent-role and administration boundaries.
`tools/check_agent_operating_settings.py` adds the normal settings path: actual
browser save/Publish, None/Gas transitions, immutable grants, exact closed legacy
and explicit save contracts, and then approved native gas execution and retained
receipt authority checks by the normally configured agent. It also refuses
changed predecessor metadata and a pre-existing new-signature function that
could otherwise retain an unauthorized execution grant during upgrade.
`tools/check_gas_mixed_quantity_reconciliation.py` qualifies both sequential
refusals, the atomic title and dispatch replacement, unchanged signed principal
and bank history, stale reviews, evicted-source retries, exact knowledge-prefix
admission and malformed child/outer receipts at immediate constraints and commit.
Its upgrade probes pin both original product writers and their source and seal
helpers, including function metadata, ownership and privileges.
`tools/check_gas_mixed_quantity_browser.py` runs the linked action through the
real API and browser over an exact recorded close. It qualifies lazy native
member choices, explicit quantities and allocation terms, both source reviews,
desktop layout and exact first-outcome retry after a lost acknowledgement.
Independent native assertions then check one financial publication, both product
journals, the revised original/replacement stock split and unchanged principal
and bank history. Its close-convention setup has its own typed publication,
which is checked separately before the financial action. Both Lifecycle browser
qualifiers share one owned API/worker launcher and remove their disposable
processes and database after the run.

## Original title and principal acknowledgements

An original title or borrow command may acknowledge an accepted positive quantity
revision under its immutable original terms. It returns the existing native
identity and distinguishes acknowledged from current financial quantities. A
fully repaid loan still has its original gross principal; acknowledgement does
not recreate stock or debt. Later fixed-fee amendments do not replace the original
borrow's fee, cash or bank facts. An original zero fee retains an explicit null
due time without creating cash. Distinct matching native identities are ambiguous
and refused.

A fresh operation validates that its certified context exists and matches its
stated business date before matching an accepted fact. Valid historical knowledge
remains usable for acknowledgement. Exact retries return the first response
before source reads, under current authority and the original actor/payload
binding. A separately authorized actor uses a fresh operation for the same business
fact. `tools/check_gas_owned_title_acknowledgements.py` qualifies these cases and
the guarded same-identity native upgrade on disposable PostgreSQL. Its commit
probe observes live authority after the native producer returns, then requires
refusal after that same original grant expires, with no financial effect.
