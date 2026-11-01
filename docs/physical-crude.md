# Physical crude: fixed contractual volume

Status: local implementation with the bounded qualification recorded below.
This is not a production activation record.

## Contract

The first contract is a future obligation to receive or deliver a fixed total
standard volume of one exact crude grade at one exact custody point. A separate
fixed-price consideration side and optional fixed per-unit handling side own
their cash amounts and payment dates. Each membership has an explicit direction.
The physical contribution is delivery market value, not a payment or a second
sale. An instrument contains normalized one-unit sides; the trade owns the
positive typed contractual quantity and scales the instrument once.

The selected version explicitly admits standard volume at 60°F, a fixed total
over one delivery window, zero contractual quantity tolerance, one compatible
monthly forward valuation assignment and no discounting. These are submitted
contract terms, not defaults inferred from a commodity name. Missing choices or
unsupported profiles refuse. Extending discounting, quantity tolerance or delivery
allocation requires an executable consumer and independent qualification.

Delivery boundaries are explicit UTC instants. The window must be wholly future
and covered by its exact monthly assignment. The physical contribution's value
date is the exclusive window end; consideration and handling each retain the
explicit payment instant entered on the ticket, shared in this first profile.
Starting delivery closes this pre-delivery valuation
profile. Passing a timestamp never proves a receipt, title transfer or payment.

## Exact references and units

Grade, commodity, facility, custody point, quantity basis, unit, quote, currency
and valuation assignment retain exact scoped reference versions. A city or a
pricing hub is not a custody point. A grade's identity selects its applicable
valuation assignment; another grade at the same point cannot borrow that mark.
An all-in curve has only its declared price dependencies. Equal price levels do
not imply a common benchmark exposure or a quality differential.

Standard volume and observed volume are different measurement bases. Geometric
barrel-to-cubic-metre conversion alone does not correct temperature, pressure,
density or water content. The U.S. petroleum barrel is 42 U.S. gallons, giving
the exact geometric ratio 0.158987294928 m³ per barrel. Reference data must supply
the qualified unit relationship; financial calculations must not hardcode it
from a display label. See [NIST Handbook 44, Appendix C](https://www.nist.gov/system/files/documents/2025/12/30/appc-26-HB44-20251222.pdf).

The public examples use an explicitly synthetic grade and custody facility.
They do not assert exchange deliverability. An exchange contract has its own
complete quality, delivery and quantity-election requirements; selecting a
60°F volume basis does not implement those requirements. See the separately
scoped [NYMEX crude contract specification](https://www.cmegroup.com/content/dam/cmegroup/rulebook/NYMEX/2/200.pdf).

## Execution, booking and history

Gas and crude use one API market-selection implementation with explicit
product policies. Gas preserves its admitted primary-market compatibility;
crude requires an independently declared market. The common manifest shape
retains separate product selections and exact dated price pins.

The domain-owned `CrudeDeliveryWindow` supplies UTC validation, exclusive date
coverage and monthly-forward eligibility to both API selection and Side
construction. Existing shared numerical primitives, reference-aware unit
conversion and the common schedule compiler/executor remain authoritative.
Product rules and database orchestration stay outside the dependency-free
`OpenEtrm.Shared` utilities. SQL admission independently verifies persisted
economics and references rather than trusting generated client programs.

Draft generation produces the same shared typed executable schedules used by
the other Side families. Booking retains the complete normalized program,
bindings, geometry, relationships and roots. Booked inspection executes that
retained content; it cannot regenerate a replacement from current authoring
terms. Physical value and cash contributions retain distinct output meanings.

Native UI and other clients use the same authorized catalog, preview, booking,
status, directory and inspection operations. The database admits exact reference
and program relationships atomically. Server-owned booking facts, exact party
versions, certified knowledge visibility and current authority apply throughout.
Repeated commands return the original outcome; changed bodies under an existing
request identity refuse. Recovery preserves the original request.

The common New Trade ticket, positions, blotter and retained trade details admit
crude holdings. Portfolio PV and interactive/queued scenario risk execute the
same retained programs under exact market partitions. Shared cash-side identities
retain independent membership signs. Physical dependencies use shared immutable
base/up/down scenarios; quantity scales the normalized result once. Historical
inventory completeness and current reference visibility are checked before a
result can be published. Unsupported gas scenario positions retain explicit
exclusions; automated closes containing crude refuse with `crude_close_scope`.

## Independent acceptance gates

| Case | Required result |
| --- | --- |
| Receive 1,000 bbl, mark 80, pay consideration 78, pay handling 0.50, explicit DF 1 | Delivery +80,000; consideration −78,000; handling −500; PV +1,500 USD. |
| Shock only the physical mark by +1 USD/bbl | PV rises by exactly 1,000 USD; fixed consideration and handling do not change. |
| Deliver instead of receive, with independently reversed cash direction | Physical and cash signs follow their memberships exactly once. |
| Two different grades at one point | Only the exact selected assignment can price the obligation. Missing or conflicting mappings refuse. |
| Same geometric units, different measurement bases | Observed volume or a different reference temperature cannot silently become standard volume at 60°F. |
| Quantity 1,000 rather than 1 | Trade scaling multiplies every normalized contribution once; it does not multiply by delivery hours or days. |
| At or after delivery start; at or after payment due | Structured lifecycle refusal, with no manufactured actual quantity, inventory or discharged cash. |
| Book, inspect, retry and alter the retained request | Inspection preserves terms and exact results; retry creates no second trade; conflicting reuse refuses. |
| Tampered schedule/reference and revoked reader/writer | Final admission or current authorization refuses, including historical and recovery paths. |
| Existing populated database upgraded | Existing bookings and sealed programs remain unchanged; no rebuild or automatic rebooking. |

## Later capabilities

The bounded [delivery and cash workflow](physical-crude-operations.md) now
records certified partial receipts with explicit title acquisition, selectable
owned inventory or onward delivery, separate dispatch, receipt-based invoice
allocation and partial payments. It preserves original contractual obligations
and does not extend the pre-delivery valuation profile.

Raw custody measurement, quality acceptance, density and temperature correction,
quantity elections, separately timed title acquisition, inventory costing,
transport rights, cargo allocation, dispatch reversals and actual bank refunds
remain further contracts. Full payment-record reversals and ordered invoice
void/reissue are covered by the bounded operations workflow. Amendment and cancellation also remain unsupported. A forecast
window or pinned grade does not prove that an operational event occurred.
The broader sequence remains in the [physical commodity plan](physical-commodity-test-plan.md).

## Verification

Managed checks cover the independent 1,500 USD oracle, symmetric price shocks,
grade-specific source selection, total-volume scaling, exact geometric unit
conversion, retained replay and lifecycle refusal. The focused check is
`dotnet run --project tests/OpenEtrm.MarketData.Checks -- --physical-crude-side`;
the complete managed suite and
frontend suite also pass.

An isolated stock PostgreSQL 16 upgrade from migration 0150 through 0152 preserved
three booked gas trades, nine memberships and seven sealed executable programs.
All prior column values across 33 trading and schedule tables were identical,
including 545 schedule rows and 434 bindings. The prior application reread the
same terms, physical details and PV after migration. An altered predecessor
function also caused the upgrade to refuse atomically.

`python3 tools/run_preview.py --pg-bin <pg-bin> --dotnet <dotnet> --physical-crude-check` passes against an
owned disposable database. It covers native preview/book/reopen parity, retry
and conflicting reuse, independent cash directions, intraday fixed-total volume,
pre-booking historical invisibility, current grant revocation and actor-bound
recovery. Seven independent physical/cash graph mutations are rejected by final
admission. Exact grade-version foreign keys, command correspondence, unknown
terms, sub-microsecond timestamp refusal and restrictive read policies are also
checked. A display-only reference revision retains the grade and custody-point
relationships. Mixed crude/futures/gas checks verify common positions and
blotter, crude PV of 10,100 USD for the independently signed native fixture,
±1 USD/bbl shocks producing ±1,000 USD, independent futures shocks, immutable
+2 USD/bbl stress and exact interactive/queued-worker parity. Gas risk exclusions
remain explicit. Hidden exact references refuse complete scope; an unbound worker
cannot gain visibility from permissive row policies. A timed permission test
proves that independently expiring market access during response preparation
refuses publication after the business read; the test records that its barrier
was reached to distinguish an early rejection.

The same disposable runner with `--browser-check --browser <browser-executable>`
passes eleven native browser checks covering the common New Trade ticket,
explicit choices, preview, booking, lost-response recovery after reload, retained
position inspection and viewport fit. These checks do not establish production activation or physical operational
completion.

After consolidating market selection and delivery-window validation, the managed
core suite, API schedule suite and both native gas/crude booking suites pass.
Additional checks cover mixed-product manifest JSON round trips, preservation of
each product's source policy, leap-year and ordinary February contracts,
exclusive month boundaries, intraday microsecond precision and supported horizon
limits. Independent review found no source-selection or authorization drift.

The portfolio increment passes 651 managed core checks and 1,036 frontend
checks. Managed coverage includes retained crude replay, shared cash-side cancellation and coexistence with prebuilt PPA markets.
It retains shared source bindings for additional native consumers without
discarding existing PPA values.
