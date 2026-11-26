# Saved operating rights, price hedges and REC inventory

D412 extends [operating facilities](operating-facilities.md) with saved trades,
ordinary retained Side valuation, a price hedge report and manually evidenced
certificate inventory. The implementation has separate native admission and
managed calculation contracts. A saved facility alone creates no trade.

## Explicit Side assembly (D417)

Status: the operating assembly and universal gas, cash and service Side
implementation pass bounded combined local qualification. The earlier power/REC
checkpoint below retains its own artifact identity; the final combined results
are recorded in [verification](verification.md#instrument-and-side-alignment-qualification-d417).

The optional preview `composition` selects `operating_side_composition_v2` while
legacy `valuation` is null. It explicitly states perspective, measure, currency,
complete-right quantity and discounting policy, then one to eight ordered
Sides. Each Side has a stable key, name, quantity source, coefficient,
`payment`/`delivery` kind, price and its own payment rule or explicit null for a
delivery. The legacy valuation request remains a separate unchanged version.
No price, direction or payment convention is inferred from resource kind, role
name or slot. Several Sides may price the same source with different terms.
The bounded complete-right profile admits coefficients `1` and `-1` and requires
at least one Side linked to a plan output. `horizon_right` supplies one literal
complete-right quantity, consumed once in the first native interval, for an
independently priced payment Side; delivery kind is refused for this source.
Its monthly payment uses the first operating month's end and the Side's explicit
payment rule. It is not a periodic capacity fee. A composition containing only
those payments is not admitted.

| Plan | Admitted quantity sources |
| --- | --- |
| Battery | `charging_energy`, `discharging_energy`, `grid_throughput` |
| Thermal toll | `energy_output`, `guaranteed_fuel`, `consumption_fuel` |
| Solar | `energy_output`, `payment_energy` |
| Whole-horizon payment | `horizon_right`, exactly once per selected Side |

Guaranteed and modeled consumption use distinct typed ports even when they have
the same native unit. Several Sides consuming one output do not create additional
physical production, fuel use or stored energy. The initial price profile remains
explicitly undiscounted, with fixed or selected-contract flat prices and monthly,
unadjusted calendar-day payment rules; delivery Sides use native interval ends.

Every member is a concrete subclass of abstract `Side`, owns its complete
immutable schedule and implements Side PV. Shared fixed/floating mechanics may
be reused; the instrument combines only signed Side contributions. A retained
program is executed by its concrete Side. Common feasibility outputs supply
typed quantities to all members without invoking a Side or Instrument PV.

The GUI prepares this same explicit contract and invalidates review after
composition or context edits. Native saved-v2 admission validates each original
member against its sealed quantity, price, sign, roots and independent value date.
Ordinary reopen, Side inspection and portfolio valuation execute concrete
`RetainedProgramSide` members. Feasibility-only reconstruction clears both
financial request versions and cannot reprice or regenerate those saved Sides.
Existing v1 commands, identities and first outcomes remain unchanged.

The owned HTTP workflow books and reopens five independent Sides, including two
same-source fees with the same display name and different payment dates, at an
independently expected value of `550.7`. Native checks preserve distinct thermal
guaranteed and consumption sources, reject malformed compositions and match
managed label whitespace/length rules. A delayed native receipt insertion that
expires the caller's authority refuses and rolls back, including semantic no-ops.
These are bounded qualification results, not production capacity evidence.

Independent Side dates do not enable started delivery, shared-resource pooling,
optimization, invoices or payment processing. The legacy profile and its
existing numerical oracles remain documented below. Populated installation
upgrade and release qualification remain explicit activation gates.

### D417 qualification

Independent operating-assembly verification on 2026-11-16 covered power, REC and
the required common adapters before the universal-Side integration. These counts
describe that verified workload, not the later merged artifact, broader products
or production capacity:

| Check | Verified result |
| --- | --- |
| Managed domain | 775 core checks pass, including concrete retained Sides, independent signed assembly, exact legacy identities, concurrent first access and context/scenario/discount isolation. |
| Managed API and builds | Default API suite emits 98 PASS records; four structured-power groups and one hedge group pass. Core, API and worker builds have zero warnings/errors. |
| Frontend | 1,961 checks pass, including actual Chrome component checks for independent Side entry, repeated sources, presets, allowed kinds and review invalidation. |
| Native storage | The complete 349-migration chain and native workflows pass on stock PostgreSQL 16.14 and 18.1, including malformed composition, exact source quantities, label parity, replay and current authority. |
| Saved trading HTTP | Five independent Sides reopen at `550.7`; an explicit sixth repeated-source membership gives `532.6`. All six native Side pages and calculation details pass. Legacy/v2 booking, expiry refusal, REC forwards and inventory remain green. |
| Populated operating upgrade | On PostgreSQL 16.14 and 18.1, exact predecessor helpers reproduce saved battery, thermal toll and solar quantities and admit their retained programs. Committed migration-body application preserves 30 history/program/relationship sets; original API commands reopen, reprice and replay unchanged. |
| Live API-backed browser | 27 checks pass: 15 facility/plan checks and 12 saved trading/REC checks. The run removes its owned disposable resources. |
| Repository | 142 Python checks pass. |

The subsequent combined Side packet also passes 16 operating and 19 saved
trading/REC actual browser checks, exact five/six-Side values of `550.7`/`532.6`,
30-set populated legacy preservation, both original ReadPhysical timers and native
semantic-receipt expiry refusal. Its compiled outputs are unchanged by the later
scoped-presence SQL correction. These results do not authorize deployment, schema
activation or operations on an existing database. Installation-specific populated
upgrade and release checks remain separate gates; no optimizer, pooled resource
operation or new physical/cash lifecycle is qualified by Side assembly.

### Populated operating upgrade compatibility

The owned API workflow books legacy battery, thermal toll and solar agreements
before exercising migration 0356 over their populated state. It restores the
tracked predecessor functions and constraints, verifies all four function
contracts against the migration's complete metadata guards, and uses the
predecessor quantity kernel and schedule admission to
validate the original plans and retained records. Original request outcomes must
also replay under the predecessor contract.

The migration body is committed before the subsequent API reads. Complete-row
digests cover 30 native history, identity, request, program and relationship sets,
including executable bytes and command hashes. The original trades reopen and
reprice at their independent values of 610, 760 and 500, with unchanged first
outcomes. Separate rolled-back probes detect altered helper metadata and a
corrupted synthetic fuel quantity without changing retained history.

This checks populated function/constraint and retained-record compatibility in a
disposable fixture. It does not exercise deployment bookkeeping, backup/restore
or an installation's migration runner, and it changes no migration journal.

## Operating-right booking

`supplied_operating_right_trade_v1` books one complete specified thermal toll,
battery operating right or solar output right. The command explicitly selects
the book, exact legal parties, immutable facility version, opening state,
supplied interval plan, contractual guarantees/entitlement, prices, payment rule,
economic perspective and fees. The server records its UTC booking date under
`server_booking_utc_v1`; this profile does not admit an invented execution date.

The external binding is exactly one complete right. Quantity scaling is disabled:
doubling a right would otherwise duplicate startup budgets, output limits or
stored energy. Serialized native admission refuses overlapping rights on the
same facility/window. Shared rights and pooled solar/storage constraints require
a separate allocation contract. Owned facilities do not acquire a synthetic
external counterparty merely to appear as a trade; internal entitlement admission
remains future work.

Each independent Side retains its own structural schedule and declared output
bindings from the same feasible plan. The instrument sums signed Side values;
the shared operating calculation supplies quantities without adding another PV.
Stored programs include exact reference and price dependencies. Fixed prices
have no curve exposure. `selected_contract_flat_v1` reads only the explicitly
selected native quote/curve/contract, which must cover every operating date and
have compatible exact quantity/currency references. There is no inferred price
curve, fixing source or spread dependency.

The saved profile supports future, fixed-zero-offset UTC intervals, at most 1,024
intervals, explicit undiscounted remaining operating value and the right-holder
perspective. Broader facility previews can inspect supported native clocks;
that does not widen saved admission. A saved valuation refuses at or after its
delivery start rather than projecting unrecorded actuals. Thermal fuel valuation
explicitly chooses guaranteed or modeled consumption. Solar priced output rights
use as-produced entitlement; firm-profile residual quantities remain a preview
until balancing price Sides are supported. Opening storage supplies usable
future energy without an inferred historical cost basis. Access fees use the
explicit once-per-horizon basis.

`/physical/power-trades` exposes catalog, preview, book, status, directory, get,
valuation and hedges operations. New booking prepares and seals owned programs,
then rechecks current authority and selected sources at final admission. Ordinary
Blotter, position facts, Side inspection and portfolio valuation consume the same
saved identities. There is no amendment, dispatch execution, metered performance,
fuel nomination, invoice or payment operation for this profile.

## Financial hedge comparison

`operating_right_price_hedge_v1` requires one saved operating trade, explicit
financial hedge trade IDs (futures, swaps, heat-rate swaps, listed futures options or heat-rate calls), selected books, a certified context,
nonempty shared factor bumps and explicit numerical/pricing/aggregation policies.
Listed option hedges use the portfolio model’s minimum shock size for delta and gamma.
All selected positions must price; excluded holdings cannot produce a successful
net report. Ordinary futures retain their native execution/variation-margin
measure rather than becoming forwards.

The common portfolio engine evaluates base/up/down PV under immutable shared
market scenarios. `shared_price_changes_v1` reports operating, hedge and net
up/down P&L, delta and gamma by exact factor and currency reference. Base values
remain separate by measure and currency. No FX conversion, exposure allocation,
cross-measure base sum, hedge ratio or hedge-accounting designation is inferred.
A supplied-plan price shock holds the plan fixed; it does not measure weather,
outage, redispatch or unexercised flexibility. Financial hedges create no physical
delivery, fuel requirement, stored energy or certificates.

## Standalone REC forwards

`rec_forward_v1` books an explicit whole certificate count, product/program/class
and generation vintage, exact parties and one future UTC delivery day. The
versioned assignment and physical mark contract must cover the complete delivery
window. Physical receipt/delivery and consideration have opposite directions
and independent retained Sides. Consideration explicitly uses a fixed price or
selected flat contract; monthly calendar-day, unadjusted payment rules and
undiscounted valuation are required. Payment cannot precede delivery completion.

The physical Side values the specified certificate asset; consideration values
the agreed payment. Both carry the whole count through a single external unit
binding. `/rec/trades` provides catalog, preview, book, status, get and valuation;
ordinary Blotter, Side inspection and portfolio risk use the same native records.
Booking creates an exact delivery obligation, not issued inventory. Neither a
solar forecast nor a battery discharge creates certificate ownership.

This forward profile supports future contractual value only. Started delivery,
trade amendments, inventory cost accounting, recorded cash settlement and
automatic purchase fulfillment remain separate work. An incoming inventory
receipt records manual evidence without automatically matching a purchase
forward. Outgoing delivery allocations and transfers use the exact booked
delivery capacity described below.

## Evidence-backed inventory

Migration 0344 adds the current manual inventory consumer, not an external
registry client. Accounts bind exact books, legal entities and registry account
references. Destinations bind exact counterparty versions and registry accounts.
Registry, product, class, program, eligibility assignment, evidence source and
retirement purpose references are typed and versioned. Public examples are
explicitly synthetic. Migration 0344 grants its capabilities to no role;
migration 0406 grants them to installed roles (D459).

Serial bounds are integer strings describing half-open ranges `[from,to)`.
Receipt evidence establishes ownership; allocation reserves an eligible range
against an exact native outgoing delivery. Release names the exact allocation
fact/revision. Transfer consumes that reserved range against the designated
counterparty destination within the booked delivery window. Retirement consumes
an available range with an explicit beneficiary, purpose and evidence. Quantity,
registry/program/class/vintage eligibility and aggregate delivery capacity are
checked before publication. Automatic lot selection is unavailable.

Natural registry lot identity cannot be duplicated by submitting another local
UUID or reference revision. Overlapping ownership, reserved retirement,
over-transfer and conflicting concurrent writes refuse atomically. Native locks
cover both account generations and shared delivery capacity. Corrections append
a new knowledge revision of the same business fact; replay resolves the whole
state and refuses a correction that invalidates dependent evidence. Stable
business order breaks equal-effective-time ties without inventing timestamps or
sorting random UUIDs (`latest_knowledge_revision_effective_then_business_order_v2`).

`rec_business_date_end_utc_v1` requires an explicit business date and current or
certified retained knowledge. Effective time and knowledge time remain distinct.
Historical reads preserve original evidence while using current authorization.
The `/rec` directory, read, account, destination, record and status operations
use authorized native storage and publication. Certified reads replay the retained
facts through the managed evaluator; selected history shows the latest eligible
revision of each business fact at that cutoff. Inventory values are counts, not
MTM or historical cost.

## Authority, history and recovery

All new tables use forced RLS and bounded authorized functions. Booking requires
current trade authority; operating reads also require physical-read authority.
Inventory read, evidence recording and account/destination configuration have
separate `ReadRecInventory`, `RecordRecInventory` and `ConfigureRecInventory`
capabilities. As installed (D459), every human role that reads trades holds the
read: Trader, Risk analyst, Facility operations, Facility operating limits, Gas
delivery operations, Gas service setup, Contracts administration, Counterparty
credit, Trade confirmations and Trade supervisor. Facility operations records
evidence. Contracts administration sets up accounts and transfer destinations,
because that form lists legal entities and counterparties only to a holder of
`ReadTradeParties`, which the facility roles do not hold. Trader records and
sets up nothing. Book and exact party scopes remain enforced. Worker reads retain
their lease/custody restrictions. Waiting or calculation cannot extend authority;
sources and permissions are checked again before returning/publishing.

Every write carries an actor-owned request identity. An exact retry returns its
first outcome before fresh market/facility validation. Reusing the identity with
different content refuses. A new request for the same business fact returns the
original identity without another fact/publication. Changed supported inventory
facts append revisions under an expected account generation. Unknown outcomes
retain the exact command and expose status/retry recovery. Historical views
cannot create facts or reinterpret present command receipts as historical data.

## User interfaces and qualification

Scheduling → Operating facilities contains consumed physical fields, supplied
plans, reviewed trade booking, saved-trade selection/repricing and the price
hedge report. Scheduling → REC inventory contains account/destination setup,
exact serial evidence, per-product balances/history and standalone forward
booking. New Trade → REC forward reuses that editor. Saved trades open in the
ordinary Blotter and independent Side inspector. Inventory has its own explicit
working business date without replacing the selected financial market close;
retained views remain read-only. Unsupported planned facility controls display
disabled **Coming soon** states and persist no unused parameters.

The assistant accepts identity-only context on these new pages and saved-product
inspectors. It does not read their financial forms, submit their commands or
reinterpret them as an existing swap product. An explicitly reported background
Blotter view keeps its existing authorized numerical overview under the selected
frame and knowledge context. That overview reads ordinary server positions;
unsupported saved-product details and unsaved form terms remain unread.

Managed checks use independent numerical expectations and malformed-program
refusals. Disposable PostgreSQL checks cover reference admission, helper drift,
publication/replay, overlap/conservation, corrections and current authorization.
The owned API/browser suite exercises native saved trades, shared hedge scenarios,
REC delivery/inventory and desktop/phone forms, using only its own disposable
database. Run the combined workflow from this repository:

```sh
python3 tools/run_preview.py --check --structured-power-check \
  --pg-bin /path/to/postgresql/bin --dotnet /path/to/dotnet \
  --browser-check --browser /path/to/chromium
```

Local qualification on 2026-11-16 used the pinned .NET 10.0.402 SDK and Node
24.7.0, stock PostgreSQL 16.14 and 18.1, and an owned Chrome profile. These results
record the D412 legacy profile; they do not qualify the D417 assembly increment:

| Check | Verified result |
| --- | --- |
| Managed core and targeted API checks | 773 core checks; retained operating/REC programs, independent Side selection and exact hedge-result checks pass. |
| Facility persistence | 44 native cases, including capped storage, concurrent retries, stale heads, certified reads and authority expiry after waits. |
| Retained program upgrade | Sixteen predecessor helpers reject altered execution/security metadata; supplied-plan source grammar admits only the explicit flat-contract profile. |
| Operating native admission | 30 behavior groups pass, including exact fuel/storage/solar quantities and closed parameter types. |
| REC inventory | 27 native behavior groups pass, including certified corrections, original-fact reassertion and concurrent first-outcome replay. |
| REC forward native admission | Authority, raw-table denial, exact assignment coverage, JSON shape, payment timing and sealed-program requirements pass. |
| Owned API workflow | Thermal 760, solar 500 and fixed battery 610 reopen at their independent expected PVs. The selected outright battery returns 34.495 with price delta 8.1. REC evidence conserves 100 issued as 60 transferred plus 40 retired; cross-account delivery-capacity admission and release pass. |
| User interface | 1,942 frontend checks and 25 live API-backed browser checks pass; financial desktop/phone screenshots were inspected. |
| Repository checks | 142 Python checks and the standalone dependency boundary pass. |

Optimization, stochastic flexibility, external registry submission, automatic
issuance, shared-resource dispatch and broader settlement remain separate
qualification gates. The implementation and local checks do not establish
production capacity or authorize schema/application activation.
