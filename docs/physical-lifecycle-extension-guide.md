# Extending physical lifecycle behavior

Developer guide for the D303 object-oriented reuse direction and proposed
[physical lifecycle design](physical-lifecycle-design.md). This guide implements
no APIs and does not approve the proposed financial policies. Read the current
source before treating a design name as an existing class.

The implemented [common delivery view](physical-delivery-view.md) (D343) supplies
the interval/track grid contract and retained literal-output projection for gas,
crude and LNG. Reuse it for presentation when extending a native adapter; it does
not implement the broader lifecycle objects or operational policies proposed here.

The [implemented facility contract](facility-quantity-ledger.md) uses relational tables,
write functions and constraint triggers. Reuse those boundaries; proposed domain
object names do not justify a parallel persistence or event framework.

## Start with the contract and current consumers

1. Read `AGENTS.md`, architecture sections 3, 5 and 7, the lifecycle design, and
   the current product's implemented contract. Identify what is agreed versus
   proposed, the actual caller and an executable baseline. Do not select a missing
   quantity, sign, finality, rounding, correction, due-date or valuation convention.
2. Follow an existing operation from its UI/agent boundary through authorization,
   historical source selection, domain calculation, persistence and response.
   Inspect crude and swap mechanisms before writing another cash/invoice ledger.
3. Draw the participating objects and their invariants. Keep booked quantity on
   the Trade binding, unit economics on Instrument/Side and peer connections on
   Instrument. A booked physical membership has a DeliverySchedule over retained
   geometry with distinct operational quantity tracks. Direct entries can own their
   evidence; shared/imported records retain their source identity/authority and
   explicit allocations. An independent movement schedule is needed only for a
   selected cross-trade/resource consumer. Documents and cash keep their own objects.
   A new commodity label is not sufficient reason for a subclass.
   Treat each booked physical Side and its operational schedule as independently
   owned. Share behavior/policy classes and primitives; do not require shared Side
   instances. Preserve economic identity separately from operational occurrence identity.
   For the multi-counterparty facility case, use a qualified facility quantity
   ledger with schedule/constraint views and versioned allocations to the booked
   schedules. These are views of one scope, not duplicated writable stores;
   retain exact links to existing source records. Common facility
   reference does not pool units, products, direction, entitlements or authority.
   Facility-backed target quantities are projections of those allocations, not
   another mutable copy. Preserve counterparty isolation and residual privacy.
   Shared-pool allocation previews/writes require complete capacity-scope authority
   or a separately qualified independent entitlement; test that quantity probes
   and global stale-head/error details cannot disclose hidden allocations.
   Financial hedges enter risk through their instruments, not as physical flows.
   Only explicit physical obligations/movements affect quantity reconciliation.

## Choose the smallest behavioral extension

The implemented [Side inspection contract](side-schedules.md#implementer-contract-d353)
owns common rendering and paging. Populate native evaluated schedules/evidence via
`SideValuation.InspectSchedule` and register the normal authorized storage source.
Do not add a product branch to the shared reader or write another schedule grid.
Pricing and lifecycle eligibility remain the instrument/Side implementer's contract.

4. Use an existing policy when its complete semantics agree. Otherwise introduce
   one narrow typed/versioned capability with a named current consumer and an
   independent numerical or behavioral oracle. Prefer composition over combinations
   encoded by subclass names. Do not build all proposed interfaces in advance.
5. Keep whole-term physical, consideration and fee Sides independent. Source and
   target links, quantities/bases, member order and coefficients are explicit.
   Never hide peer Side references in a Side or replace the common PV entry point
   with a switch on commodity or concrete model type.
   Keep the retained delivery grid and operational tracks distinct: never regenerate
   an independent calendar or change economic terms to record a booked nomination.
   Each consumer selects its explicit quantity-input policy and stage rights.
   For facility constraints, select exact operating-rule versions, native basis,
   stage and warn/block operation. Preserve valid observed breaches as facts;
   do not clamp readings or treat allocation as a new movement. Missing required
   inputs are unassessed, not all-clear. Never infer physical netting/curtailment.
6. Put financial domain objects and policies in the owning managed domain module;
   only proven domain-neutral mechanisms go in `OpenEtrm.Shared`. Domain code
   receives immutable resolved inputs, not an unrestricted connection, provider
   SDK, browser state, hidden clock or mutable global context. Keep transport and
   authorization adapters separate from the financial implementation.

## Implement and qualify the whole operation

7. Write a meaningful failing consumer check first. For extraction, establish old
   behavior with independent golden/oracle checks first and retain both baselines.
   Never assert only that two callers of the same new algorithm agree.
8. Add exact-version references/typed relationships only for the selected consumer.
   Database uniqueness/FKs and operation validation must enforce the same occurrence,
   contribution, evidence, scope and currency rules. No free-text masters, arbitrary
   JSON relationships or unvalidated `(type, id)` targets.
9. Preserve current authority and certified historical context through every join
   and calculation. Commands retain request ID/canonical payload and fact business
   key, serialize expected heads, recheck after waits and publish facts/claims/
   receipt atomically. Unknown outcomes retain the original command; retries do not
   select fresh inputs. Use the existing worker lease/generation fences for jobs.
   Shared consumptive sources need their own stable capacity/claim fence across
   bindings and measurement revisions. A source correction cannot erase already
   posted cash: new acceptance qualifies current evidence, retained facts keep
   their exact original pins and require explicit correction operations.
   Enforced plans also fence operating-rule/availability heads and retain their
   assessment pins. Scoped exceptions/notifications obey the same pool authority;
   qualify stable issue identity, monotone expected-head publication/delivery
   fences over the complete applicable input-head set, idempotent transitions and
   current access at read/delivery. Allocation-only and availability-only changes
   must invalidate an older current-state assessment. Delayed
   breach/resolution events cannot regress a newer issue state; already dispatched
   stale events must be explicitly historical rather than claim current status.
10. Exercise every state axis independently: measured versus accepted, fulfilled
    versus title/inventory, determined versus documented, due versus paid. Prove
    partial quantity/cash conservation, revisions, reversals and historical reads.
    An invoice is not new money, and `CashPayment_v1` is not a generic unpaid balance.
    Negative-price profiles allocate against the resulting signed obligation,
    not the coefficient alone; existing crude rejects those prices explicitly.
11. Add the explicit lifecycle valuation scope only when qualified. Prove complete,
    disjoint contribution coverage, remaining/open cash continuity through due dates,
    independent side directions, native currency partitions and current/history/
    scenario/worker parity. Do not silently change existing contractual PV.
    Whole-period swap coverage cannot settle a partial gas/power tranche. Require
    retained disjoint native-cell/quantity coverage or refuse it; a correction
    reuses the original claim. Keep a cash-only bridge separate from any physical
    P&L measure requiring an explicit noncash delivery/title recognition term.
12. Obtain independent adversarial review for authorization, temporal behavior,
    numerical results, async/retry execution and release boundaries. Resolve findings
    and qualify any migration against populated retained baselines before publication.

## Update the capability record

For each supported profile, document its actual evidence/quantity/finality inputs,
title/inventory and invoice capabilities, rounding/correction/due policies,
currency/scope bounds, valuation measure, exact implementation/reference versions,
current authorized operations and independent evidence. Record unsupported cases
and separately qualified rollout requirements.

Update the product document, architecture/decision record, implementation status,
plan and verification links together. Change the README only when a capability
has an implemented qualified consumer. Never advertise LNG, NGLs, standalone power,
PPA accepted cash, general inventory accounting or bank integration because a
shared interface could accommodate them. Keep drafts local until publication is
explicitly authorized.
