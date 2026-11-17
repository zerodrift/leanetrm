# Retained chosen Transport routes

Status: locally qualified retained operating consumer. Managed/native,
populated-upgrade and actual browser gates passed; deployment remains separate.

## Graph and source contracts

The first route has one immutable exact definition, reference bundle, gas
product, native quantity basis, book and legal-entity version. Its counterparties
remain on the separate source entitlements. The accepted definition operation
is its exact version; changing geometry or laws requires a distinct route and
explicit membership release/re-adoption. No general graph amendment is selected.

Each segment references an existing exact entitlement/path. Nodes explicitly
select external receipt, transit or external delivery roles. The graph is
connected, directed and acyclic. Local node and segment keys identify declared
rows; normalization never discovers a connection by matching location names.

The installed handoff policy joins only the identical exact reference,
pipeline, zone, point and product tuple. Cross-pipeline junctions need a qualified
transfer master and remain unsupported. A selected same-point planning handoff
does not establish hydraulic feasibility, operator authorization, nominations,
pooling, accepted performance or title.

Each segment has exactly one uniform source claim, including an explicit native
zero claim for an idle segment. The consumer adopts all current claims of each
selected capacity-plan stream exactly once. Repeated tranches of a fixed path,
missing claims, extra claims and duplicate mappings refuse. Quantities and stage
windows come from the immutable selected native claims; editable copies are not
accepted. Route adoption does not debit commercial capacity again.

## Conservation and numerical policy

The explicit installed route laws are:

- `gas_transport_chosen_uniform_route_v1`;
- `fuel_and_loss_each_fraction_of_gross_receipt_v1`;
- `one_explicit_uniform_receipt_and_delivery_per_segment_v1`;
- `fixed_nonnegative_elapsed_microseconds_v1`;
- `simultaneous_inflow_equals_outflow_no_storage_v1`;
- `same_exact_pipeline_point_handoff_v1`;
- `whole_capacity_plan_stream_exclusive_v1`.

Every segment selects exact fuel and loss fractions against gross receipt.
Delivery equals receipt times one minus those two fractions, and both delivery
boundaries equal their receipt boundaries shifted by the explicit lag. Every
transit node balances simultaneous incoming deliveries and outgoing receipts
over all exact interval boundaries. There is no node storage, inferred split,
netting, quantity clipping or rounding.

Fraction operands are canonical nonnegative integer strings, denominator
positive, with at most 4,096 bits per operand; normalization reduces them exactly.
Their sum cannot exceed one. Zero is explicit and canonicalizes to `0/1`. Lag is
an explicit nonnegative signed-64-bit microsecond integer string, and all shifted
instants must remain supported. Intermediate arithmetic remains exact; reduced
final results obey the existing 4,096-bit envelope.

Current capacity claims use plain native decimals of magnitude below `10^24`
and scale at most 18. Pure-core rational quantity support does not introduce
rational persisted claims. A required nonrepresentable delivery refuses rather
than selecting a rounding policy.

## Exclusive source membership and corrections

A capacity-plan stream's first immutable revision header is its stable source
identity. Typed foreign keys pin that first header and the selected current
header, and prove their entitlement/business-key chain. Claim-ID uniqueness
alone cannot conserve membership because a correction creates new claim IDs.

One current route-plan stream exclusively owns each selected logical capacity
stream. Current membership derives from latest route-plan heads, without a second
quantity ledger or mutable ownership cache. Per-source membership generations
include adoption, replacement, removal and future-effective touches. Unrelated
streams do not stale a selected membership review. Withheld foreign owner IDs
never imply availability; the source carries an explicit closed membership state.

The states are `available_v1`, `owned_by_this_route_v1` and `occupied_v1`.
An occupied source is never eligible for adoption. A known future own plan may
still appear in the route's retained membership evidence under that occupied
state; it cannot hide the release of a separate current own plan. Every own
membership pins the exact latest revision as well as its stable logical stream.

Source correction, release, capacity-version change or complete generation
change makes the current route assessment unavailable until explicit re-adoption.
Original inputs, accepted assessment and membership remain retained. A newly
generated claim ID cannot escape an existing logical source membership.

Selected complete capacity version/generation values belong to the assertion's
source value. A new certified context with identical heads is the same fact;
explicit re-adoption after a selected generation changes is a new source value.
Expected route heads and membership generations are optimistic controls.

An explicitly selected route-membership release frees only its own memberships
under the exact current route-plan predecessor and current authority. It can
operate when capacity sources have become stale or released. It adds no flows
and does not amend capacity claims. Released state is unassessed, rather than a
fabricated successful zero-flow plan.

## Operating boundary

The endpoint prefix is `gas/transport/routes`, with directory, read, preview,
record and status operations. Reads carry the explicit operating context and
route ID. Writes carry context, owner, operation ID, effective time and a closed
canonical command. Status uses original owner/operation identity.

A definition supplies exact owner/reference/native-basis fields, all selected
laws, nodes and segments. A plan additionally pins `definitionId`, complete
`sources`, `sourcePlans` and segment-to-claim `flows`. Capacity version/generation
belongs to source value; expected route/predecessor/membership generations are
admission controls. A release supplies its exact definition, own predecessor and
explicit release policy, without new sources or flows.

The accepted definition operation identifies the immutable version. Plan and
release receipts separately return `routePlanId` for the stable logical stream
and `revisionId` for the accepted operation. A replay retains its first receipt;
a fresh acknowledgement of the same fact returns `changed:false`.

The first graph supports at most 128 nodes and 256 segments. Native discovery
returns every authorized route within its 256-route scope, or refuses a larger
scope explicitly. The UI applies no second truncation to that complete list.
Broader searched discovery remains a separate consumer; it cannot silently omit
source choices or claim complete scope.

New facts use the existing explicit UTC business-date assertion clock. Stage
windows remain independent. Latest-known future route/source heads cannot
resurrect an obsolete current view or free membership before its permitted time.

The route envelope's `hasFutureHeads` describes its own definition/plan heads.
Each member of `sourceCapacities` carries its separate complete source flag;
a future capacity version is explicitly unavailable. A false route flag alone
does not certify source freshness. Backend action metadata can therefore retain
an eligible own release while withholding adoption and current assessment.

`ReadMarketData`, `ReadTrades` and `ReadGasOperations` authorize the complete
route/source scope. `ConfigureGasServices` defines a route;
`RecordGasOperations` adopts or releases a plan. Authority is captured before
source or publication waits and its original bounds survive native deferred
and managed final admission. Status retains its entry-captured kind deadline.

Exact replay and semantic acknowledgement precede fresh source resolution.
They preserve the first outcome and never reinstall an old membership head.
Acknowledgement preview has no current assessment table, including exact
replays whose original outcome retains `changed:true`.

Native proof must independently reconstruct typed graph/source/flow membership,
all exact capacity and route intervals, actor/source/effective-time provenance
and conserved outcomes. One header per publication, source-known strictly earlier
predecessors and fresh-fact uniqueness apply. A feasibility Boolean grants no
admission. Existing capacity verification is reused through a private snapshot
consumer with independently qualified predecessor parity.

## Delivery gates and remaining products

The executable baseline composes receipt 100 into delivery 98 after fuel 2,
explicit split 60 plus 38, exact later merge 98 and terminal delivery 98. All
seven segments include a declared zero idle leg. Separate serial entitlement
scopes preserve their shared split/merge capacities without inventing inventory.

Qualification requires independent managed/native arithmetic, source correction
and exclusive-membership races, old-fact replay, publication/definer attacks,
original grant waits, populated upgrades and the actual shared application
workflow. These gates passed for the declared operating profile, including nine
actual managed reader/witness checkpoints and final deferred-source validation
after a later same-transaction capacity correction. Earlier capacity consumers
and populated title/custody/fee/storage facts passed their existing regressions.

The final local checkpoint passed 742 managed core checks, the full API schedule
suite, 1,997 frontend and 152 Python checks. The same frozen application passed
55 route, 64 capacity and 30 storage browser checks. Desktop, historical-evidence,
local-zone and phone views were reviewed. This evidence does not qualify the
separate financial, physical-performance or deployment capabilities below.

Nomination/confirmation/performed tracks, physical asset feasibility, operator
balances, title, cash, financial Transport booking, charges/value, unused
flexibility and financial risk continuation remain separate capabilities.
