# Facility quantities and physical Side delivery tracks

Status: implemented for local synthetic qualification (D303, migration 0184).
Operations › Facilities & delivery › Quantities & allocations and the authorized
API use the same database operations. Broader lifecycle responsibilities remain in the
[design](physical-lifecycle-design.md) and [extension guide](physical-lifecycle-extension-guide.md).
D321 adds explicit [final-input adoption and a gas basis demo](gas-basis-demo.md).
No deployment or production qualification is implied.

## Supported contract

The first consumer is a manually reported quantity for one retained native gas
day, allocated explicitly among compatible booked physical gas obligations. Its
facility boundary is the exact delivery point. A multi-point facility needs a
qualified reference relationship; a location name alone does not establish it.
Quantity specification, gross direction, legal entitlement and original day
geometry are pinned. Target capacities reuse retained quantity literals and
binding ratios, rather than another calendar or inferred conversions or losses.

A source is recorded once; allocations link it to each trade's own physical Side.
The trade ticket and unvalued trade inspector project only that Side's planned
delivery amounts from the same ledger, independently of PV availability. There is no second writable copy of its facility allocation schedule.
Plans do not overwrite existing gas daily inputs. A separate explicit D321
adoption supplies facility-only final billing inputs through exact allocation
links. Neither plan nor adoption establishes fulfillment, changes contractual
PV, creates invoices or posts cash.
Financial hedges create no physical flow. Other commodities require qualified
adapters and their own independent examples before activation.

## Using the UI

An access administrator can assign **Facility operations** and **Facility operating
limits** through the existing Users & Access role editor. Neither preset is
assigned automatically. Operations can discover retained booked deliveries,
create/open their facility day, record or correct its quantity, select trade/Side
targets with the shared searchable multi-select, check quantities, and save a full
replacement allocation plan. The separate limits role configures or withdraws a
native-period maximum. Effective time and evidence must be entered explicitly.

The compact shared header selects the business date and knowledge context.
Working reads use current knowledge; applying an As known on selection gives a
read-only historical view. This profile explicitly selects
`facility_business_date_end_utc_v1`: reads include records through the end of the
business date in UTC, independently of the native gas-day clock or price close.
A new record's effective instant must fall on that selected UTC business date.
Reported quantities require an ended native interval. Display/input times use
the person's configured time zone.

Trade inspection also requires an explicitly selected market close in the Blotter.
Without one, the allocation links explain that prerequisite; the facility does
not choose a financial close on the user's behalf.

Reported, allocated and unallocated quantities are calculated in the managed
backend with exact native arithmetic. A source of 100, allocations 60/25/10 and
maximum 90 shows allocated 95, unallocated 5 and an operating breach of 10.
Allocation does not cure a physical breach. A correction to 90 preserves the
95-unit plan, exposes -5 unallocated and requires reconciliation; a replacement
plan must conserve the corrected source. Missing source or a missing/withdrawn
latest limit is **unassessed**, rather than successful compliance. Reports above
a maximum remain recordable. This is an assessment, not a hard admission or
curtailment policy, and it does not dispatch external notifications.

Revision history shows up to 100 visible revisions of each kind; the directory
shows the latest 200 facility days and target discovery the first 1,000 compatible
native cells, with truncation identified. These are display bounds. SQL write
validation always checks the exact submitted targets independently.

Unconfirmed requests keep their original operation ID, payload and opaque account
binding. Check status or retry the original request; do not create another request
to recover it. Actor-scoped memory supports navigation when browser storage fails;
when storage works it also supports reload recovery. Account changes and permission
revocation cannot reinterpret an earlier actor's request. Failed/superseded reads
withhold old values, and forms protect unsaved edits on navigation and reload. Saving one form asks
before discarding unsaved drafts in another.

## Storage and operations

| Record | Responsibility |
| --- | --- |
| `facility_quantity_ledgers` | Exact-version facility/point, commodity, quantity specification, legal entity, direction, native interval and recording actor. |
| `facility_gas_delivery_scopes` | Exact gas hierarchy, retained obligation witness and stable operating-day identity. Reference/unit/clock changes cannot mint another capacity pool. Changed pins require an explicit future continuity operation. |
| `facility_quantity_revisions` | Immutable reported quantity/evidence revisions with predecessor, effective time, actor and certified publication lineage. |
| `facility_quantity_limits` | Immutable native-period maximum revisions under `native_interval_maximum_v1`; a null maximum explicitly withdraws the latest rule. |
| `facility_quantity_plans` | Complete replacement plans pinning the source revision, previous plan, effective time, actor and publication. |
| `facility_gas_targets` / `facility_gas_allocations` | Exact booked obligation, trade revision and instrument relationships. One native obligation cell belongs to one facility scope. |
| `facility_quantity_commands` | Actor-scoped request receipts and typed original authorization scope for retries. |

Runtime has no direct table access. Fixed-search-path database functions check
current authorization, bound contexts and exact retained references:

- `record_facility_quantity_command` accepts closed `create`, `source`, `limit`
  and `allocate` commands. Allocation plans replace the full set of claims.
- `read_facility_quantity_ledger`, `facility_quantity_directory` and
  `facility_quantity_choices` supply authorized context-bound operations views.
- `facility_quantity_status` recovers the original actor's receipt.
- `read_trade_facility_allocations` supplies only the selected trade's own
  Side/day/quantity/unit/state. It exposes no pool total, residual, source/plan
  head, other counterparty or global update timestamp. Removing a claim returns
  explicit `no_current_allocation` with null quantity, not a zero billing input.

HTTP routes under `/api/facilities/quantities/` expose `directory`, `read`,
`preview`, `record` and `status`. Preview applies `FacilityTargetCapacity` and
`FacilityQuantityBalance` in `OpenEtrm.MarketData`, reusing project-owned
`Quantity`, `ExactQuantity` and `OpenEtrm.Shared` exact arithmetic. Presentation
uses shared tables, time inputs, edit tracking, request retention and selectors.
There is no duplicated commodity calculation in TypeScript or new general-purpose
lifecycle framework. Assistant page reading/writing for Facilities is explicitly
unsupported; no assistant-specific privileged operation is introduced.

## Authorization and integrity

`ReadMarketData`, `ReadTrades` and `ReadFacilityQuantities` authorize a full pool
within the current catalog scope. Recording requires `RecordFacilityQuantities`;
limits require `ConfigureFacilityLimits`. Writes require working data. Each target
trade and exact retained reference must remain independently readable.
Replacement plans authorize both removed and new targets. Receipts recheck their
original scope without depending on unrelated targets added later. Ordinary trade
readers can inspect their own allocation track without full facility permission.

Writes share the security epoch and trading publication fence, serialize ledger
heads and atomically commit facts and receipts. Identical facts at the same
business key/value/effective time return the original row, including equivalent
decimal spellings and reordered allocations. An actor/request ID retains its first
outcome; an old fact retry never reinstates that fact as the current head. Changed
values append revisions and reads select them by certified knowledge time.

Exact foreign keys and immutable triggers protect all relationships. Deferred
constraint triggers independently enforce authorized provenance, exact chains,
current pinned source with no forward knowledge reference, source conservation,
target scope/native geometry, exact
trade pins and target quantity bounds. A later transaction cannot add claims to
an existing plan. SQL sums and cross-products protect persisted integrity; managed
code owns the quantity/operating assessment. Source corrections retain old plans
and historical reads; no automatic netting, redistribution or loss allocation occurs.

## Verification and limits

Run with a pinned supported SDK, stock PostgreSQL and an installed Chromium browser:

```sh
python3.13 tools/run_preview.py --pg-bin /path/to/pg/bin --dotnet /path/to/dotnet \
  --facility-quantity-check --gas-daily-quantity-check --deal-ticket-check \
  --check --browser-check --browser /path/to/browser
```

Managed checks independently verify conservation, exact fractions, native-unit
refusals, operating breaches and missing inputs. SQL/HTTP checks use a 100-unit
source and three trades with 60/25/10 claims, source corrections, concurrent plan
replacements, historical cutoffs, withdrawn limits, actor-bound retries and
revocation. Deliberately incomplete definer writes test triggers independently
of command checks. Shifted clocks cannot create another logical-day pool. Own
trade projections remain identical when only another target's allocation changes.
Owned browser checks exercise the shared selector, corrections, limits, request
recovery, edit guards, context transitions and responsive layout alongside existing
gas/ticket workflows. See [verification](verification.md#facility-quantity-ledger-d303).

Migration 0184 adds objects without rewriting legacy rows. Hosted schema/API/role
qualification, broader facility mappings, external evidence ingestion, broader final
allocation profiles and notification dispatch remain separate work. No additional
billing, cash, netting, commodity or production capability follows from these plans.
