# In-term physical delivery: accrued cash, payments and remaining value

Status: design, 2026-11-07; built as recorded in D359, which supersedes this text where they differ (load role, undiscounted compositions, physical price lookup). It selects
the first consumer for the lifecycle valuation slice (L5) of the
[physical lifecycle design](../../physical-lifecycle-design.md) and records the
owner's decisions for it. A decision-record entry is added when it is built.

## Intent

Show, on the hosted pilot, an indexed physical gas trade that is already in its
delivery term:

- gas days that have ended carry fixed but unpaid cash, which stays in PV until a
  payment is recorded against it;
- earlier periods show as paid and leave PV, while remaining in the history;
- days still to come are valued from the single mixed-tenor Henry Hub location
  curve: daily HHD plus basis through November, monthly H plus basis from
  December (D330–D332);
- each Side's schedule shows those states row by row.

Today no physical product can do this. Gas, crude and LNG all refuse valuation
once delivery or loading has started (`gas_delivery_lifecycle`,
`crude_delivery_lifecycle`, `lng_delivery_lifecycle` in
`ScheduleProgramDemand`). The behaviour exists only as a managed example
([indexed physical gas](../../indexed-physical-gas-example.md),
`IndexedPhysicalGasPurchase` with `explicit_paid_cash_nominal_due_v1`) that no
booking, persistence, worker or hosted database admits.

**How in-term trades arrive.** Users will not book trades that are already in
their delivery term through the ticket. Such trades come in through an
administrator: a later novation or migration workflow, and for now a direct SQL
load of the pilot. The ticket's refusal to book a started delivery stays. What
changes is that a trade which *is* in term, however it was loaded, values and
inspects correctly instead of refusing.

**Shared, not gas-only.** The started-delivery step (whole period ended → cash
fixed and owed → recorded payment) is built once in the shared trading
lifecycle, with gas as its first consumer. Crude and LNG keep their explicit
refusals until they select their own performance evidence; the refusal names
the missing policy. No placeholder implementation, empty result or zero stands
in for them.

## The demo, as agreed

Synthetic throughout. Receive 100 MMBtu per gas day at Henry Hub, September 1 to
December 31, 2026. Pay the daily index plus 0.00 on day 10 of the following
month. The gas day runs 09:00 to 09:00 Central from the configured gas-day
clock, not midnight UTC. Valuation is at the Friday November 6 close, 21:00 UTC
(15:00 Central). Discount factors come from the pinned payment-date curve.

Synthetic publisher: one final daily fixing at 12:00 Central. Friday's
publication prices Saturday, Sunday and Monday with one value. These conventions
are explicitly synthetic and claim no publisher's methodology.

| Gas days | Delivery | Price | Cash | Due | Settlement | In which measure |
| --- | --- | --- | ---: | --- | --- | --- |
| Sep 1–30 | Ended | Fixed | −8,700 | Oct 10 | Paid 8,700 Oct 10 (recorded) | None; history only |
| Oct 1–31 | Ended | Fixed | −9,300 | Nov 10 | Accrued (whole-day), unpaid | Outstanding settlement |
| Nov 1–5 | Ended | Fixed | −1,555 | Dec 10 | Accrued (whole-day), unpaid | Outstanding settlement |
| Nov 6 | In progress | Fixed 3.18, published Thu Nov 5 | −318 | Dec 10 | Remaining | Remaining delivery MTM |
| Nov 7–9 | Not started | Fixed 3.22, one Fri Nov 6 value | −966 | Dec 10 | Remaining | Remaining delivery MTM |
| Nov 10–30 | Not started | Forecast HHD + basis | −6,809 | Dec 10 | Remaining | Remaining delivery MTM |
| Dec 1–31 | Not started | Forecast H + basis | −10,912 | Jan 10 | Remaining | Remaining delivery MTM |

With illustrative factors 0.99951 (Nov 10), 0.99582 (Dec 10) and 0.99202
(Jan 10):

| Measure | PV USD |
| --- | ---: |
| Remaining delivery MTM: physical leg +18,884.09, payment leg −18,884.09 | 0.00 |
| Outstanding settlement: −9,300 × 0.99951 − 1,555 × 0.99582 | −10,843.94 |
| Remaining contract value + outstanding settlement | −10,843.94 |
| Expected cash flows, undiscounted and never added: Nov 10 −9,300; Dec 10 −9,648; Jan 10 −10,912 | — |

Remaining MTM is zero in this table only because, in the illustration, the
physical marks equal the contractual prices and both legs share payment-date
factors. Neither is a rule. The existing physical measure discounts each gas
day's value at the exclusive end of that gas day (see
[physical gas side](../../physical-gas-side.md)), and the build keeps that
convention. Payment-leg cash discounts to its payment date. So with real
factors, remaining MTM is a small timing difference, not zero:

> remaining MTM = Σ over remaining days of quantity × physical mark × DF(gas-day end) − Σ quantity × payment price × DF(payment date)

Delivered quantity over ended gas days is 6,600 MMBtu. Its disposition
(inventory, resale or use) is outside these measures, and the screen says so.

**Oracle.** The repository fixture reproduces the amounts above exactly. It
seeds those fixings and curve marks itself, and its expected values are
recomputed by hand from the formula above with its own explicit discount
factors. The pilot seed uses the pilot's own curves at its close. Its check
recomputes the same formula independently from the marks it reads, rather than
matching these illustrative amounts.

## Goals

1. **Value started physical gas compositions.** For a booked gas composition
   with indexed or fixed-price consideration, valuation after delivery start
   succeeds under an explicitly selected, versioned lifecycle policy instead of
   refusing. Every new gas composition, booked through the ticket or loaded by an
   administrator, compiles that policy, so a trade booked forward keeps valuing
   once it enters its term. Existing trades are not backfilled and keep their
   refusing programs; the pilot is reseeded. For fixed-price consideration, an
   ended gas day accrues contract quantity × fixed price, with no fixing needed.
2. **Whole gas-day boundary.** A gas day moves from remaining to accrued only once
   its gas day has ended under the retained gas-day clock. A gas day in progress
   stays in remaining MTM. There is no intraday proration.
3. **Fixing state from publications.** A price is fixed when its published
   observation is visible at the valuation instant and knowledge cutoff. This
   follows the existing exact published-index resolver, with no date inference. A
   fixed price on an undelivered day locks the payment price only. The physical
   leg keeps its independent market mark.
4. **Accrual quantity is labelled.** Ended days accrue on the contract quantity,
   labelled "estimated from contract; allocation pending". The quantity basis is
   part of the selected policy and is visible on each row. A later policy version
   may select allocated quantities from Physical operations. This design leaves a
   typed slot for that and does not implement it.
5. **Paid comes only from recorded payments.** A payment record links to an exact
   accrued occurrence, carries a signed amount, an effective payment instant and a
   knowledge time, and may be partial. Passing the due date never pays anything.
   Unpaid cash past its due date stays at nominal under the existing explicit
   policy. Fully paid occurrences stay in history with zero outstanding.
6. **Separate measures.** The trade reports remaining delivery MTM and
   outstanding settlement separately, plus their labelled sum. Gas compositions
   select no discounting, so both are undiscounted and labelled so; discounting
   outstanding cash to each payment date is later work (D359). Expected cash
   flows by due date are shown undiscounted and are not added to either. Nothing
   is labelled trading P&L.
7. **Side schedules show the states.** The existing Side inspector shows, per
   row: delivery state (ended, in progress, not started); quantity and its basis;
   price state and source (fixed with its publication, or forecast with its curve
   source); cash amount and due date; settlement state (paid with the recorded
   date and amount, accrued unpaid, part paid, remaining); and its PV
   contribution and measure. This goes through `SideValuation.InspectSchedule`
   and the registered native reader, with no product branch in the shared reader.
8. **Risk unchanged in kind.** Retained up/down risk invokes the same Instrument
   PV under shared scenarios. Accrued amounts are frozen under price shocks, and
   only their discounting stays sensitive. Remaining days respond to their curve
   factors as today. No commodity-specific shock handling is added.
9. **Historical correctness.** Payments and accruals are selected by business
   time and knowledge cutoff. A historical view sees only payments and fixings
   known at its cutoff (D334's latest-close exception applies to closes only).
10. **Pilot seed by SQL.** An administrator loads the demo trade, its synthetic
    fixings and the September payment directly into the pilot database. Every row
    passes the same database constraints, foreign keys and visibility rules as an
    application write. The seed is idempotent: rerunning it changes nothing. The
    seed script is installation data and stays out of the repository. The same
    synthetic example lives in the repository as a check fixture.

## Non-goals

- Booking in-term trades through the ticket, and the novation workflow itself.
- Invoices, disputes, credit notes, true-ups and allocation corrections.
- A user interface for recording payments. The seed writes payment rows. A
  recording operation can follow with its own authorized command.
- Scheduling cycles, cuts, fuel, imbalances, flow orders and transport charges.
- Intraday delivery, and allocated or metered quantities as the accrual basis.
- Physical recognition: inventory, resale attribution or consumption value of
  delivered gas.
- Crude and LNG started-delivery valuation. They keep explicit refusals.
- Netting across trades, counterparties or currencies.

## Anti-goals

The build fails, even if the demo screen looks right, if it:

- treats index plus zero as zero MTM by rule, or lets a fixed payment price stand
  in for the physical leg's market mark;
- infers fixed, delivered or paid from a date;
- presents contract quantity as delivered or final quantity without its label;
- labels an amount as discounted when it is not, or adds the cash forecast to PV;
- reports delivered gas's departure from MTM, or a payment, as a trading loss;
- implements "paid ⇒ trade value zero" (prepaid gas is still owed delivery);
- defaults any missing policy, fixing, quantity basis or discount factor to a
  successful result instead of a structured refusal;
- changes existing economic identities, program digests or results of trades
  that do not select the new policy;
- adds crude or LNG placeholder classes, tables or interfaces with no consumer.

## Constraints

From `AGENTS.md` and the existing decisions:

- **Valuation boundary.** Instruments and Sides price themselves from the
  supplied scenario context. Lifecycle state is operation-owned binding state,
  like `CashflowBindingValuation`/`PaymentSideState` and `SwapBindingValuation`,
  never part of reusable economic identity.
- **Idempotent writes (D238).** The same fact recorded again returns the
  existing row. A different value for the same business key is a new revision
  with its own knowledge time. Retries return the first outcome.
- **Exact relations.** Payment allocation rows reference the exact trade
  revision, binding, membership and occurrence through foreign keys, never by
  name or JSON.
- **Explicit policies.** The lifecycle policy, quantity basis, due-cash policy
  and numerical/rounding policy are named versions selected per binding. Missing
  or conflicting selections refuse.
- **Schema release.** New tables or functions ship through a pilot schema window
  (see `docs/hosted-pilot.md`). The pilot data may be reseeded freely.
- **TDD and review.** Tests first, with literal oracles independent of the
  algorithm. This touches temporal behaviour, numerical correctness and
  authorization, so it needs an independent adversarial review before
  publication.

## Existing code to build on

| Piece | Use |
| --- | --- |
| `PaymentSideState`, `FinalSidePayment`, `SideCashAllocation`, `CashPaymentState` (`Trading/SidePaymentState.cs`) | Frozen final cash per occurrence, linked allocations, conservation, knowledge-time selection and payment states. Today these are managed only. |
| `IndexedPhysicalGasPurchase` (`Gas/IndexedPhysicalGasPurchase.cs`) | Managed oracle for the cash measure (`UnpaidCashPresentValue`). It deliberately excludes physical value. The booked composition needs both measures. |
| `SwapSettlementInputs.Evaluate` (`Trading/SwapLifecycle.cs`) | Working pattern: determined contributions replace their forecast period, outstanding cash discounted to its due date, due balance at nominal, whole-period coverage. Reuse the mechanism, not its swap-only admission. |
| `ScheduleProgramDemand` guard `future_gas_delivery_v1` | The refusal point. The new policy is a distinct lifecycle the guard admits. The existing value keeps refusing. |
| `ScheduleProgramInterpreter.Classify` | Already classifies delivered versus undelivered cells (`DeliveredUnsettled`, `RemainingProjected`) and quantity state (contractual, metered, estimated). |
| `GasIndexedCash.Resolve`, published-fixing resolver | Exact daily index contract and fixing per gas day. Missing fixings refuse. |
| `trading.gas_cash_determinations` (D338, migration 0205) | Prior art for immutable per-gas-day determinations with corrections. It is scoped to fixed prices and final allocated quantities. Decide whether to extend it or add an indexed/contract-estimate sibling, and record the reason. |
| Side inspection (D352/D353) | The rendering and paging path for the new row states. |

## Decisions already made (owner)

- In-term trades are loaded by an administrator, by SQL for now. The ticket keeps
  refusing started deliveries.
- New ticket bookings get the started-delivery policy too, for both indexed and
  fixed-price consideration. There is no backfill of existing trades.
- The started-delivery step is shared. Gas is the first consumer; crude and LNG
  refuse explicitly.
- Whole gas-day convention, with the boundary from the configured gas-day clock.
- Fixed status comes from publication; a fixed undelivered day keeps its market
  mark.
- Accrual on contract quantity, labelled as an estimate with allocation pending.
- Paid only from recorded payment rows; partial payments allowed.
- Separate measures as in the table above; the cash forecast is not added.
- Outstanding cash discounted to its payment date with the pinned payment-date
  curve.
- Synthetic publisher conventions as stated, labelled synthetic.

## Calls made while writing (to confirm)

These were settled by the designer, not the owner, after the builder check:

- **Accruals are derived at read time.** An ended gas day with a visible
  published fixing accrues contract quantity × (fixing + spread) when the
  valuation runs. No stored accrual row, worker or daily command is needed. A
  later fixing correction is a new revision seen under its knowledge time.
  Freezing an accrual into an immutable determination belongs with invoicing,
  later.
- **The payment occurrence is the gas day.** `GasIndexedCash` already generates
  one cash period per gas day. A recorded payment is one cash record with
  per-gas-day allocations, each linked by foreign key to the trade revision,
  binding, cash membership and retained occurrence. Each allocation must keep its
  direction. Allocating to a day that has not ended refuses: there is no
  prepayment in this policy. There is no recording-time cap: accruals are derived
  at read time, so the database cannot know a day's accrued amount when a payment
  is recorded. Cash paid beyond the accrued amount, whether over-allocated or left
  above a fixing revision that lowered the accrual, reads as an explicit overpaid
  state, visible rather than refused. A recording-time cap belongs with a payment
  recording operation, later.
- **Physical marks after publication.** At the November 6 close, the mixed
  location curve must still carry a point for the Nov 6 gas day and one
  weekend interval covering Nov 7–9 (the containing-interval lookup selects
  it). The physical leg reads that point. It never falls back to the payment
  fixing; a missing point refuses.
- **Knowledge time of seeded facts is the load time.** The pilot seed is
  recorded with real knowledge time. The September payment carries effective
  time October 10 and is known from the load. Historical views before the load
  show nothing, which is correct. The demo uses the working view at the latest
  close. Backdated knowledge is not allowed.
- **Headline and risk measure.** The Blotter headline and retained risk use the
  labelled sum (remaining MTM + outstanding settlement), with both parts shown.
  Before delivery starts, outstanding settlement is zero and the sum equals
  today's contractual PV.
- **D338 final determinations stay separate.** A fixed-price day may also have a
  D338 final cash determination from final allocated quantities. This policy
  still accrues on the contract estimate. The inspector shows the determination
  beside it, and does not silently swap one for the other. Using final
  allocations as the accrual basis is the later policy version from Goal 4.
- **Cash and P&L reconciliation is out of scope.** The L5 gate's reconciliation
  report (closing minus opening remaining cash value plus recorded cash) is a
  later deliverable.
- **January 10, 2027 is a Sunday.** The existing payment convention
  (`next_month_day_utc_v1`) does not adjust for business days. The demo keeps
  the unadjusted date and shows it as the stated term. A business-day-adjusting
  convention is separate work.

## Left to the builder

- Storage shape for payments and their allocations: extend D338's tables or add
  a sibling. Use relational rows with exact foreign keys, immutable receipts,
  and no parallel event framework.
- The lifecycle policy's name and where it is selected: per held binding or per
  booked membership, outside economic identity.
- How the two measures appear in the position and Blotter responses, keeping
  the existing contractual PV contract available under its own name.
- How a trade spanning four calendar months is represented, within the existing
  31-day physical membership limit. One composition membership per month is
  acceptable if the inspector reads as one trade.
- The pilot release steps and seed script, kept private.

## Prerequisites to verify on the pilot

Not yet checked; read access to the pilot was not available while this was
written.

- The HHD and H futures curves, their OTC basis curves and the combined mixed
  location curve exist as published versions, and the PZL valuation assignment
  selects the combined curve.
- The gas setup's gas-day clock is 09:00 Central.
- Daily index contracts and published-fixing definitions cover September 1 to
  December 31, 2026, including the weekend convention above.
- A payment-date discount curve is pinned at the November 6 close.
- The pilot's schema head, so the release window can be planned.
