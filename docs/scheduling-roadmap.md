# Scheduling workflows and contribution areas

Status: the desk navigation and the informational roadmap are implemented
(D461, D462). Every workflow marked **TBD** below is unimplemented. These are
scoping and contribution areas, with no committed delivery dates or selected
financial policies.

Each Scheduling page has a collapsed **Planned workflows** panel for its desk.
Opening it shows descriptions and contribution guidance. It does not open an
operating page, read business data or record a transaction. Available operating
entries still depend on the account's existing permissions.

## What an installer can use today

| Desk | Existing Scheduling entries and their scope | Missing workflows shown as TBD |
| --- | --- | --- |
| Gas | Gas day position; per-trade delivery and billing; nominations, route plans and capacity; shared facility quantities and explicit allocations to booked gas deliveries | Pipeline connections; storage statements |
| Power | REC inventory | Schedules and dispatch; transmission scheduling |
| Crude | Per-trade delivery, receipts, inventory disposition, dispatch and the supported cash lifecycle | Cargo parcels and liftings; pipeline movements; tank inventory and blending across trades; voyages and freight; time charters; laytime and demurrage; rail and truck movements |
| LNG | Booked cargo terms and retained delivery schedules can be inspected | Cargo actuals and reconciliation; terminal operations; voyages and freight; time charters; laytime and demurrage |

**TBD is a missing implementation, not an unconfigured or unlicensed feature.**
No installation setting enables it. The installation's choice of enabled desks
remains a separate proposed design. Operating facilities and Lifecycle remain
under Trading. The panel is a focused contribution map, not a complete product
capability or production-readiness declaration.

## Crude and shipping: separate scopes

A cargo parcel identifies a quantity of a grade being lifted or transported.
It may be part of a vessel's cargo, and an explicit allocation may connect it
to one or more trades. A pipeline batch identifies a movement in a pipeline.
A vessel, its voyage and the agreement to hire or use it have separate
identities. None is automatically the booked Trade or its economic Side.

Time charter concerns use of a vessel for an agreed period, with explicit hire
terms. Voyage charter concerns carriage for a voyage. An implementation must
retain the selected contract's obligations; it cannot assume one standard
allocation of bunkers, expenses, delays or off-hire. The
[IMO port-call guide](https://wwwcdn.imo.org/localresources/en/OurWork/PartnershipsProjects/Documents/GIA-just-in-time-hires.pdf)
describes the distinction. Crude pipelines can carry different grades in
batches; the [pipeline operations study published by PHMSA](https://www.phmsa.dot.gov/sites/phmsa.dot.gov/files/docs/Report%20to%20Congress%20on%20the%20Study%20of%20Diluted%20Bitumen%20-%20August%202014.pdf)
provides background. These references supply terminology, not executable
policies or redistributed reference data.

| TBD area | Records and questions to scope | A useful first contribution |
| --- | --- | --- |
| Cargo parcels & liftings | Cargo identity, grade, owner, loading window, splits/combinations, custody evidence and exact allocations to trades | One synthetic parcel serving two explicitly authorized trades, with corrected loading evidence and no duplicated quantity |
| Pipeline movements | Nominations, capacity, batch identity, origin/destination, linefill, receipts, deliveries and losses under a selected contract | One batch from receipt to delivery, with a stated loss policy and late measurement correction |
| Tank inventory & blending | Tank and owner positions across trades, transfers, grade/quality evidence, measurement basis and blends | One tank transfer with independent quantity and ownership reconciliation; blending needs its own quality and conversion contract |
| Voyages & freight | Vessel identity, port calls, loading/discharge windows, cargo allocations, voyage costs and freight agreements | One voyage with one parcel and an explicit freight contract |
| Time charters | Vessel delivery/redelivery, hire periods and rates, payments, off-hire evidence, bunker obligations and cost ownership | One hire period with a disputed off-hire interval; specify payment, correction, currency and valuation rules before calculation |
| Laytime & demurrage | Statements of facts, notice, allowed time, counting rules, exceptions, rates and reviewed claims | One load/discharge event with independently calculated time under a selected contract; no universal clock or rate |
| Rail & truck movements | Movement tickets, vehicle or consist references, custody quantities, loading/unloading evidence and carrier charges | One movement ticket with a corrected unloading quantity |

Shipping work is listed on both Crude and LNG. This does not make shipping
mandatory for either desk, or decide that their quantity and commercial
policies are interchangeable. A pipeline-only installation may contribute to
pipeline movements without implementing marine workflows.

## Other desk contributions

- **Gas pipeline connections — TBD:** external submissions and acknowledgements
  around the existing native nomination stages. Start with one synthetic
  operator protocol, including rejection, duplicate acknowledgement and retry.
- **Gas storage statements — TBD:** imported operator statements reconciled to
  explicit storage rights and recorded quantities. Storage contracts and facility
  ledgers already exist; this item concerns their external statement workflow.
- **Power schedules and dispatch — TBD:** select a market and specify the
  schedule, instruction and metered-delivery records before building submission
  or reconciliation. REC inventory does not implement energy scheduling.
- **Power transmission scheduling — TBD:** choose an explicit reservation,
  interchange and curtailment contract. This does not implement FTR trading.
- **LNG cargo actuals and reconciliation — TBD:** retain loading/discharge
  measurements, quality and losses with explicit mass, volume or energy bases.
  Booked cargo inspection does not supply those operational facts.
- **LNG terminal operations — TBD:** berth windows, terminal nominations,
  inventory and regasification need a selected terminal contract and qualified
  measurement/conversion policies.

## Contributing one increment

1. Choose one TBD area and describe its users, their authority and the smallest
   useful operation. Include a synthetic example with independently stated
   expected results. Documentation and domain examples are useful contributions.
2. Identify the existing native consumer and any missing operational scope.
   State units, ownership, effective and knowledge times, correction behavior
   and financial terms explicitly. Do not infer defaults from a commodity name.
3. Agree the bounded contract and acceptance checks before implementation.
   Write a meaningful failing check, then add only the behavior it requires.
   Test unauthorized access, retained history, duplicate requests and uncertain
   write recovery where applicable.
4. Reuse the [delivery workbench](delivery-workbench.md),
   [physical lifecycle extension guide](physical-lifecycle-extension-guide.md)
   and [facility quantity ledger](facility-quantity-ledger.md) where their
   contracts fit. Keep each booked physical membership's schedule independent;
   allocate a shared operational record explicitly. Quantity flows, transfer
   events and inventory balances remain distinct.
5. Put calculations and business rules in the managed backend. Financial
   contracts follow [Instrument and Side composition](instrument-contracts.md);
   a shipping cost is not a new browser calculation or an implicit crude-price
   adjustment. Add storage or configuration only when a concrete consumer needs
   it. UI and agents use the same authorized operations.
6. Follow [CONTRIBUTING](../CONTRIBUTING.md), obtain the required independent
   review, and record actual verification and limitations. Change a TBD label
   only when an authorized, tested operating workflow exists. A form, seed row
   or documentation draft alone is not an implemented capability.

The display descriptions live in `web/src/scheduling-roadmap.ts`. Keep this
guide and the display aligned when a scope changes. No new commodity framework,
table, permission, page identifier or assistant operation is created by adding
an informational roadmap item.
