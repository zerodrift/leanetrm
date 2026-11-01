# Events and lifecycle

Status: baseline approved for implementation and publication on 2026-10-16.
The bounded futures booking workflow retains explicit `Unprocessed` settlement
state and latest-head booking amendments with immutable versioned events. Futures
settlement transitions and a generic event executor remain unimplemented. Enrolled
financial swaps have a bounded [settlement and P&L lifecycle](swap-settlement.md);
physical crude has bounded [delivery and cash operations](physical-crude-operations.md).
The broader contracts below complement [the architecture](architecture.md) and
[delivery plan](plan.md).

## 1. Ownership

Instruments and sides define their supported lifecycle behavior. The booked trade
owns instrument quantities, lifecycle state and retained obligation occurrences.
Events, TraderId, TradeDate, counterparty, memo and legal entity also belong to
Trade. Instruments remain reusable unit definitions; each booked quantity uses
the existing unit-aware Quantity value type, retained externally to the instrument
by the trade for that binding.
Side schedules describe planned occurrences; accepted business events are retained
on the trade, with links to the affected instrument bindings and occurrences.

Two trades may reference the same instrument and side definitions. Amending,
exercising or terminating one trade must not mutate the other's definition or
state. Definition identity, booked occurrence identity and lifecycle state are
different concepts.

Suggested responsibilities:

| Object | Responsibility |
| --- | --- |
| Portfolio | Contains books and aggregates their authorized trade values |
| Book | Contains trades and defines the relevant business/access boundary |
| Trade | Booking identity, book binding, TraderId, TradeDate, counterparty, memo, legal entity, instrument-to-quantity bindings, applicable lifecycle state and retained business events |
| Instrument | Reusable unit economics, supported economic actions and coordination of its side behaviors |
| Side | Planned events, schedule/valuation behavior and applicable component rules |
| Lifecycle handler | Pure validation/planning of a supported state transition |
| Application service | Current authorization, approval, concurrency and atomic persistence |
| Scheduler | Discover/reconcile due work; request validated transitions |

Classes implement behavior through common contracts and may delegate to typed
handlers. A domain method does not directly write the database, send a payment,
publish a notification or authorize its caller.

## 2. Three distinct concepts

| Concept | Meaning | Example |
| --- | --- | --- |
| Planned event | A scheduled opportunity, observation or obligation | Exercise window, fixing time, delivery interval or payment due date |
| Command | A request to perform an action | Amend a quantity, record a fixing, exercise a right or terminate future delivery |
| Business event | An immutable accepted fact | Trade amended, fixing recorded, right exercised or payment confirmed |

A timer reaching a date does not prove exercise, lapse, delivery or payment.
Notifications are delivery mechanisms for already-committed facts, not another
authority for business state. A failed/rejected command does not become a
successful lifecycle event merely because it was requested.

An instrument's event plan can align side events without losing their source or
contingency. Side-level event identities and links survive the combined view.

## 3. Proposed behavior contracts

```text
Trade.Decide(command, lifecycleContext) -> ProposedTransition
Instrument.PlanLifecycleAction(command, occurrenceState, lifecycleContext)
Side.PlannedEvents(scheduleContext) -> PlannedEventSet
Side.PlanLifecycleAction(command, occurrenceState, lifecycleContext)
Apply(event, priorState) -> nextState
```

Exact method names are proposals. Required properties are:

- Supported actions and request/result schemas are discoverable by people and
  agents through the same business operation catalog.
- The context pins business state, effective date/instant, applicable definitions,
  observed source facts and rule versions. No hidden clock or market access.
- Planning returns typed proposed events, new bindings and validation diagnostics.
- Applying a committed event is deterministic. Replay does not ask a model to
  reinterpret the original request or use today's prices to rewrite the event.
- Unsupported transitions fail explicitly. A new subclass is not permission to
  bypass common authorization, version checks or persistence rules.

The proposed execution path is:

```text
Request
  -> authorize and inspect current version
  -> domain handler plans/validates transition
  -> approve exact proposal where policy requires
  -> revalidate current authority, source facts and expected versions
  -> atomically commit events, new state, request outcome and outbox
```

Preview/planning is not execution. A material change to the proposal, relevant
source facts or expected version invalidates its execution proof and any approval
whose meaning changed.

## 4. Amendments and corrections

An amendment is a retained transition, not an overwrite or deletion of the old
trade. Record the prior bindings, accepted changes, new bindings, effective
date/instant, actor, reason and application revision.

The implemented `unprocessed_futures_booking_amendment_v1` operation changes
book, exact dated contract, signed lots, execution price or execution instant.
It requires the current head, a reason and an explicit effective instant equal to
the selected working As of instant, no earlier than the previous head. The editor
previews both states using pinned prices and requires confirmation before submit.
Historical views and superseded revisions are read-only. Changing contract reuses
or creates an independent instrument binding; sibling trades retain their state.

`futures_booking_state_v1` events retain revision links, actor, operation identity,
reason, execution/effective coordinates and certified publication identity.
Historical reads select the newest visible revision before applying book filters.
Concurrent commands cannot fork the chain. Identical retry returns its accepted
original outcome even after a later amendment; outcome reconciliation is explicitly
current control state. Old and replacement states must both be supported live,
unprocessed futures in the same definition package and exact currency. This is a
booking correction workflow, not settlement, economic termination, arbitrary
backdated insertion or an external-effect dispatcher. No outbox consumer is claimed.

For example, changing a trade's fixed rate:

1. Resolve the current trade/version and exact referenced side terms.
2. Validate authority, permissible amendment scope and effective interval.
3. Create/reuse the new fixed-side definition and any required instrument version.
4. Rebind only this trade's affected instrument/interval, preserving shared definitions.
5. Commit the business event and new trade state atomically.
6. Recompute affected future obligations/risk under a new retained context.

Changing quantity follows its quantity and amendment rules; it does not require
inventing different side terms when the existing definition is still valid.

Keep three operations distinct:

- Correcting a booking/data error, with a retained explanation and correction link.
- Amending economic terms agreed for an effective interval.
- Economically terminating/unwinding obligations, including any termination
  payment and obligations that survive.

The word `cancel` must map to one explicit supported meaning. It must not silently
delete an audit trail, reverse a settled payment or erase delivered quantities.
Partial termination, novation and other richer actions require their own rules
before they are enabled.

Already-realized fixings, deliveries and settled cashflows remain facts. If a
correction changes their treatment, retain explicit correcting/reversing events
and their links rather than editing the original facts.

## 5. Expiry and outstanding obligations

Use separate state dimensions rather than a single status that conflates booking,
contract rights, physical delivery and settlement. The precise enums are an open
implementation decision.

Examples:

- Booking state: draft, booked or a supported booking correction.
- Rights/contract state: active, exercised, lapsed or terminated as applicable.
- Obligation state: planned, due, realized, settled or disputed as supported.

An option's exercise deadline, end of physical delivery and final payment date are
different events. Reaching expiry does not imply no remaining PV, receivable,
payable or delivery obligation. Paid/settled status requires retained evidence.

For an option, a supported rule evaluates exercise/lapse using its terms, deadline,
settlement convention and available required observations. Missing fixings or
ambiguous instructions produce a pending determination or structured failure;
they are not an automatic lapse or a zero result. Distinguish analytic scenario
exercise decisions from a legally/operationally committed actual exercise.

Ordinary market-risk scenarios preserve committed actual exercise decisions,
realized fixings and settlement facts. Recompute hypothetical exercise only for
rights still outstanding under the captured lifecycle state. Replacing a realized
fixing with a shocked forward or exercising an already-exercised right is not an
ordinary market shock. Any counterfactual historical analysis is a separate mode.

When exercise affects sibling sides, apply the occurrence-level transition to the
explicit controlled group. Do not globally activate/deactivate shared side
definitions. Exercise versus an amendment at the boundary must have a defined
ordering and a tested conflict rule before that option workflow is supported.

## 6. Recorded and effective coordinates

Each business event identifies its immutable event/schema version, trade and
applicable instrument binding/obligation occurrence, prior/new state versions, application
revision, effective date/instant, recording metadata, request/correlation identity,
actor and applicable approval/source references.

Effective time and when the application learned a fact are different. A backdated
amendment recorded today must not alter a previously captured view or valuation
manifest. Wall-clock metadata is not a permanent physical database commit record.

State reconstruction uses retained events and referenced immutable versions, with
explicit correction semantics. A retroactive event can require a new projection
for an effective interval, but it must not replay past external side effects or
pretend that already-completed actions never happened.

Lifecycle state and its revision are valuation inputs. An expired, exercised or
partially settled occurrence can value differently from another occurrence of
the same definition. An amendment, partial exercise or settlement recomputes the
remaining schedule, quantity mapping and quantity-scalability proof. Original
trade quantity and unchanged definition identity alone are insufficient.

Retain occurrence/trade IDs and state revisions in input manifests for provenance.
Computation reuse compares normalized valuation-relevant state, remaining
obligations and consumed facts; different provenance IDs alone need not prevent
reuse of economically identical inputs. Distinguish this semantic calculation
identity from the manifest that attributes results to authorized occurrences.
Preserve cashflow rounding boundaries and scope/access checks when doing so.

## 7. Persistence and concurrency

Use an append-only business-event journal and corresponding versioned trade
state, committed atomically. Trade state is a projection of accepted transitions,
not a separately editable competing history. Other modules need not all use event
sourcing merely because trades retain an event journal.

Serialize conflicting transitions or use expected-version checks with an
equivalently tested transaction protocol. Two concurrent amendments cannot both
claim the same prior version. An automated due action competes through the same
guard as a manual command. Retry/replan under current state and authority; never
reuse a stale approval after its meaning changes.

Persist the business event, state/binding changes, idempotency outcome and outbox
entries in one database transaction. Rollback publishes none of them. Lifecycle
publication also observes the authorization/revocation fence described in the
architecture.

Committed event replay rebuilds projections; it does not reauthorize an already
accepted historical event as if it were a new command, dispatch payments, send
notifications or recalculate event facts using current models. Access to replayed
state/results still uses current permissions.

Retain the event schema and compatible handler/projection versions needed for
reconstruction, or provide an explicit tested migration of event representation.
Do not silently run historical events through incompatible current handlers. If
the supported historical implementation/migration is unavailable, report that
reconstruction is unsupported rather than fabricate a state.

## 8. Due-event processing and downstream effects

Treat the scheduler as a reconciler. It discovers eligible planned events and
submits typed lifecycle commands with stable occurrence identities. It uses
bounded batches, ownership fencing and the same business guards as other callers.

Retain occurrence IDs across rebuilds and amendments that do not replace the
obligation. Explicitly supersede replaced occurrences and reconcile their queued
work. A new trade version alone must not create another payment or exercise
occurrence for the same obligation.

At execution, check the occurrence, current trade state, expected version,
required facts and current authority. A stale timer does not blindly apply an
old schedule after an amendment. Duplicate scheduler delivery resolves to the
retained business outcome; missing or disputed facts remain visible.

Distinguish stable obligation occurrence identity, action/phase identity,
command-attempt identity and external-effect identity. One occurrence may have
different due, observation, correction and settlement transitions. A blocked or
failed attempt remains recorded; changed source facts may authorize a new
validated attempt without suppressing a legitimate later action. An occurrence
ID alone is not an idempotency key for every action in its lifetime.

Conversely, a new attempt or source revision does not create another right to
make the same payment/exercise. Use stable business-effect identities and retain
links for explicit corrections/reversals. Reconcile an uncertain prior effect
before authorizing another effect; do not evade idempotency by inventing new keys.

Write outbox entries inside the transition transaction and dispatch only after
commit. Delivery may repeat; consumers deduplicate and reconcile before external
side effects.
There is no promise of exactly-once network delivery. Initial implementation can
use database-backed polling without a separate message-broker dependency.

New live processing and historical projection replay are separate execution
modes. External actions require explicit supported adapters, permissions,
idempotency and reconciliation. An event type existing in a schema does not mean
the application can execute a payment or perform physical delivery.

Dispatch rechecks current authority, recipient access, destination policy and
automation enablement. A committed event does not grant perpetual delivery
authority. User-delegated actions require the applicable delegation to remain
valid; system processing requires separately valid service authority and policy.
Offboarding the original actor does not erase a committed obligation. If dispatch
is blocked, retain the obligation and visible blocked-work state for an authorized
operator rather than silently sending it or dropping it.

## 9. Initial and later scope

The linear baseline should include booking, explicitly supported amendments,
booking correction, required fixing observations and due/outstanding-obligation
state for its supported cashflows. Exact enabled actions must be listed and
tested rather than inferred from a generic status editor.

Define the event framework and extension contracts now. Add actual option
exercise, richer physical lifecycles, termination, novation and settlement
execution only with their product rules and acceptance tests.

Required tests include duplicate/uncertain requests, conflicting amendments,
backdating, a due event racing an amendment, stable occurrence IDs across rebuilds,
permission revocation, surviving obligations after expiry, deterministic replay
and prevention of repeated external side effects.
