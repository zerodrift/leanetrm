# Booked executable Side schedules

This checkpoint implements booking-time executable storage and an authorized
direct loader for D258. It does not switch the application's valuation adapters
to that loader. The source-resolution, instrument-mapping and booking-admission
checkpoints below are implemented; the general swap/PPA portfolio and worker
pricing cutover remains work in [Side schedule evaluation](side-plans.md).
Physical gas and crude already execute retained programs in their bounded
product workflows; see [gas](physical-gas-side.md) and [crude](physical-crude.md).

## Stored content

Booking generates the complete whole-term Side schedule: observations,
quantities, applications, amounts, discounts and output operations. The same
`booked_side_program_v1` contract supports dated swaps and native interval sides.
It retains ordered operands, reverse dependencies, source bindings, first
contribution consumers, exact grids, sibling ports and named roots. It contains
contractual constants and references, never market samples or scenario values.

`trading.side_schedule_programs` adds one immutable record per retained schedule:

| Column | Meaning |
| --- | --- |
| `catalog_id`, `schedule_id` | Exact foreign key to the retained schedule |
| `version` | Supported executable format and operation-address contract |
| `content` | Complete executable document as exact UTF-8 bytes in `bytea` |
| `content_hash` | SHA-256 of those bytes, enforced by a database constraint |

Existing typed schedule tables remain the relational FK and audit authority.
The artifact directly represents those same calculations. Native grids retain
interval selections and local labels as arrays; there is no additional SQL row
per multiplication. Storage uses stock PostgreSQL without extensions.

## Admission and retries

The managed booking path compiles each distinct schedule, checks publication
correspondence and stages its typed content. After typed readback,
`security.seal_side_schedule_program` admits the bytes and seals the complete
schedule in one transaction under the schedule row lock. A failed typed source,
shape or topology check rolls back the executable insert too.

The database independently compares constants, row metadata, operand ordering,
bindings, grids, roots, ports, reverse addresses and first-guard consumers. It
requires complete, codec-readable structure: supported shapes and horizons,
used grids, nonempty identities, root coverage and explicit consumers. Claimed
manifests and newly calculated byte digests cannot certify a changed price.

Raw JSON requires unique keys and integer numeric tokens before `jsonb`
conversion. Required, nullable and unknown fields remain distinct; amounts are
canonical exact strings. Duplicate/missing/unknown fields, decimal or exponent
representations of integer addresses and invalid UTF-8 refuse.

Identical retries retain the original artifact; different bytes conflict, even
after sealing. Sealed-schedule reuse also checks exact executable content. Final
trade binding and publication retain their existing short atomic transaction.

## Direct loading

`RetainedScheduleStore.LoadPrograms` reads authorized headers and bytes together
and calls `BookedSideProgram.Read`. It takes no Side parameters and calls no
generator, definition loader, lowering pass or executable compiler. It rechecks
membership and source authority after decoding before returning protected content.

`security.read_instrument_side_programs` checks current trading/source authority,
instrument membership, publication visibility, explicit trading-history boundaries
and worker claims. Unpublished, unavailable, unsupported or inconsistent programs
refuse. Callers still select instruments through their authorized trade-revision
read; this loader does not replace book scope or trade selection.

The loaded program executes against explicit input columns through the shared
evaluator. This checkpoint does not provide production source-selection adapters
or establish paid state. Payment facts belong to trade obligations.

## Installation and qualification

Migration `0129_booked_side_programs.sql` refuses installations containing any
retained schedules. A schema release requires explicit rebooking and preservation
of the installation's historical boundary. Reading never backfills artifacts or
changes sealed history. There is no compatibility reader or rebuild fallback.

Disposable synthetic checks cover exact bytes, changed financial constants,
malformed instructions/schema, idempotency and mandatory sealing. Direct-loader
checks disable every normalized-content loading function, verify stored bytes
and an independent exact fixed-side PV, and cover dated/shared sides, all native
families and authority refusals.

These checks qualify storage and direct loading. They do not establish the live
pricing cutover, production source eligibility, new settlement capabilities,
largest-workload budgets or production throughput. Existing valuation adapters
remain until the next integration passes independent numerical and end-to-end checks.

## Dynamic-source checkpoint

`ScheduleMarketSources` binds demanded program columns to the existing price,
quantity and discount resolvers. It receives no Side terms and creates no
replacement schedule graph. The caller supplies the resolved price-graph context,
exact base snapshot, native reference/read data and explicit knowledge and
pending-actuals policies. Context identity includes the manifest; scenarios must
retain that context and the base snapshot. Native indexed prices additionally
require the authorized instrument attribute, checked against the exact quantity
specification and supported price cadence.

The adapter resolves dated index prices, native energy prices, certificate and
monthly capacity prices, raw asset volumes, daily capacity versions and explicit
payment factors. It preserves base/scenario demand lanes and rejects missing or
ambiguous metadata. Fixed native prices need no unrelated price curves. Physical
assignments retain their exact quote multiplier and adjustment; capacity uses
individual operating days, including daylight-saving boundaries.

`BookedScheduleLookups.Dated` obtains dated metadata from an authorized exact
market/reference read. Fixing publication instants come from qualified
`index_fixing_observations`, not contract expiry or observations. Stored index,
quote, delivery, series, source and implementation bindings must agree. The
existing database direct-load check exercises this decoder while normalized
schedule-content reads are disabled.

The focused `--schedule-market-sources` check covers independent dated and native
values, hourly/quarter-hour quantities, mixed fixed/projected observations,
symmetric shocks, missing due fixings, conflicting publication metadata,
historical delivered values, changing daily capacity, source identity and context
refusals, explicit indexed attribute/cadence admission and cancellation.
Local verification on 2026-10-29 passed the full trading verification group
(managed, frontend, stock-database and owned browser suites), the focused
source checks and a final full managed rerun. Independent adversarial code
review has no remaining material finding within this checkpoint.

This is source-resolution qualification, not the application pricing cutover.
Sibling-quantity composition, partial amendment coverage, complete source receipts,
bounded concurrent database input reads, custom-output execution integration,
portfolio/worker parity and workload qualification remain required. The existing
application valuation adapters still run. The new adapter explicitly refuses a
sibling source without its composition adapter; it never regenerates that source.
No production deployment or schema installation is qualified by this checkpoint.

## Futures and OTC forward lookup boundaries

| Consumer | Selected price | Transition and exposure |
| --- | --- | --- |
| Futures holding | Exact futures series and explicit curve/member/quote binding | The current value is execution P&L and open-mark exposure. Valuation refuses at expiry; settlement PV and variation-margin processing remain unsupported. |
| OTC indexed forward or swap | Exact model-owned Forward constituent on an outright index curve, or the explicitly selected custom projection | At the qualified fixing publication instant, the selected final index fixing replaces the projection. A missing due fixing refuses. Fixed observations retain frozen lineage and no live quote risk. |
| Futures upstream of an OTC curve | Only the curve's declared dependency and exact constituent mapping | `RequireLive` refuses an expired source. Explicit `FreezeFinalFuture` consumes its final contract settlement, which stops live upstream exposure. This does not supply the OTC index's own fixing. |

An indexed lookup cannot substitute a futures contract for its forward contract.
An outright mark has no inferred basis or upstream exposure. Futures lot/quote
scaling and execution basis remain separate from discounted OTC cash flows.
The standalone managed forward contract has no persisted forward booking ticket;
existing booked OTC consumers are the supported sided swap contracts.

## Native contribution coverage

The stored executor accepts an explicit native delivery coverage as an evaluation
input. It demands sources only for included contribution cells, preserves the
original reduction windows, and records the coverage in the demand and result.
Excluded cash cells remain not demanded; excluded PV cells remain outside the
measure. They do not create overdue balances or require otherwise unused inputs.
A boundary splitting a retained contribution cell refuses, including a partial
monthly capacity contribution. Instrument admission remains responsible for the
full contractual envelope and native boundaries inside sparse TOU gaps.

Focused checks cover hourly and quarter-hour partial spans, exact partition totals
across a daylight-saving month, excluded overdue payments, empty side intersections,
capacity-boundary refusal and direct market-source resolution. This closes the
native contribution-selection prerequisite only. The instrument metadata,
connections and source-evidence checkpoints below build on it. Live portfolio
and worker integration, durable replay receipts, broader custom-output coverage
and end-to-end workload qualification remain required.

## Instrument-owned quantity connections

The managed execution layer can request named quantity outputs with explicit
per-cell base/scenario demands. This executes the stored quantity operations
without demanding the output member's prices, discount factors or PV. The demand
and evaluation retain those masks; output names alone are not a reuse identity.

`InstrumentScheduleExecution` accepts exact member identities and explicit
input-instruction/cell to output-name/cell mappings. It validates complete input
coverage, exact units, clocks and interval labels, normalization identity and
acyclic connections. It propagates demand backward over the admitted instrument
order and evaluates members forward, merging shared upstream requests. Receiving
Sides and `ScheduleMarketSources` get typed values only, never another Side or a
peer resolver. Quantity stage and state are retained; combining different stages
refuses, preserving the existing quantity contract.

Checks cover generated quantities at hourly and quarter-hour cadence, compatible
multiple inputs, changed instrument connections without changing the receiving
program, partial coverage, shared upstream members, wrong context/scenario,
missing inputs, geometry mismatch, normalization mismatch, mixed stages and cycles.

The instrument mappings now have the booking-time storage and direct-loader
checkpoint below, together with typed valuation metadata and in-memory source
evidence. Live portfolio/worker integration remains work. In particular, the legacy
`SlotQuantity` authoring and persisted side-slot metadata still encode source-role
semantics. The new executor never infers connections from those labels; moving
that authoring/persistence relationship onto the instrument is required before
the new boundary is complete throughout the application. No production cutover
or largest-workload qualification is claimed.

## Retained instrument cell mappings

Migration `0147_instrument_schedule_cells.sql` extends the existing
instrument-owned `side_schedule_request_inputs` links with immutable exact cell
addresses. Sealing a booking resolves each linked quantity output to its input
instructions once, retaining zero-based input/output cell arrays with exact
schedule, row and root foreign keys. Split and sparse output blocks must cover
every input cell exactly once with matching units, clock, interval and local
labels. No prices or quantities are stored in these mappings.

`instrument_schedule_cell_seals` records the supported format and mapping count,
including zero for independent sides. Mappings and the marker seal atomically
with the existing port contract. Identical retries retain the first mappings;
reference/source and booking authority are rechecked after both initial creation
and retries. The old sealing entry point is not available to runtime/worker
callers. Publication requires the marker and mapped rows cannot be changed.

`RetainedScheduleStore.LoadInstrumentPrograms` loads the side program bytes and
saved cell addresses. It performs no geometry joins, schedule generation, term
reconstruction or graph compilation. The database enforces exact published
membership, certified knowledge, trading-history boundaries and current source
permissions. The managed loader rechecks all member/source authority after
reading and decoding both results.

The owned disposable PostgreSQL checks exercise exact sub-selection addresses,
split/sparse output partitions, retries, zero-input markers, immutable mutations,
missing-marker publication, source withdrawal, historical cutoffs and unclaimed
workers. Direct loading succeeds with all normalized schedule-content RPCs
disabled. This qualifies the persistence boundary, not the live valuation cutover.
`LoadPpaInstrumentPrograms` now projects the retained PPA currency, quantity
basis, signed memberships, delivery window, normalization and discount policy
from existing typed tables. It assembles the managed instrument executor without
constructing Sides or loading terms/calendars. Normalization must match both the
instrument and every member. The projection uses the same historical and current
source-authority checks, including a final managed recheck after assembly.
`LoadSwapInstrumentPrograms` also loads dated and whole-term swap members,
including exact signed coefficients, native quantity ratios, quote units,
discount policy and typed contractual factors. It reads no payment rules,
calendars or curve terms. Retained period dates and exact allocations are checked
against the sealed program quantity literals. Factor swaps preserve different native
units; the instrument owns their sizing. An exact pinned unit catalog verifies
that the retained ratio agrees with the selected factor and conversion version.
Both staged-program and actual member/factor reference authority are checked
before and after metadata projection, and again after managed decoding.

Independent checks cover a 3/2 scheduled ratio and 0.075 therm/kWh mapping to
15/2 MMBtu/MWh, including refusal of an unconverted ratio. Each scheduled side must retain contiguous monthly periods with allocations
summing exactly to one. Factor instruments additionally require identical delivery
periods and allocations; ordinary swaps keep their independent layouts. Payment
and observation rules are never compared between sides. The direct metadata
loader alone does not establish readiness for live pricing.
The live portfolio/worker integration remains work.
PPA staging and saving now use one exact booking-command envelope. Publication
compares its retained hash with the accepted operation's immutable request, even
on retry. The older publication entry point is internal only. Before committing
an API booking, the managed backend compiles the persisted actual members and
compares their ordered IDs, economic identities and pricing identities with the
sealed publication identities. A mismatch rolls back the booking transaction.
This verification runs at booking only and does not generate another schedule.

The booking identity reader requires the owning operation's write authority,
exact accepted command and request-to-published-member mappings. It rechecks all
source authorities after projection. It does not use or loosen historical
valuation visibility for an uncommitted booking. The managed compiler remains
the semantic trust boundary: SQL proves command and artifact binding, not that
arbitrary caller-supplied instructions correctly implement financial terms.
Migration `0148_swap_program_admission.sql` extends that boundary to dated,
whole-term and factor swaps. Staging and saving share typed SQL command
normalizers; publication compares the accepted request after removing only the
server-generated `sideFixings` evidence. The API recompiles persisted members
at booking and compares exact identities before commit. Shared term projections
run as the caller for normal reads, preserving RLS, explicit trade/revision order
and the committed knowledge-receipt inner join. A separate operation-authorized
reader supplies uncommitted terms only to the owning current write operation.
Both readers use the same financial projection; no second reconstruction is
maintained. Normal holdings reads use one security-invoker batch function over the shared
view, restricted to exact revision keys. The selected set is projected once in a
materialized query. Dependent exact-key lookups select each revision and its
instrument before rendering terms, avoiding repeated catalog-wide protected
scans and rendering of superseded or unrelated revisions.
The committed receipt join remains in the same statement. JIT is disabled only
inside that bounded read, including its nested side-term projections; PostgreSQL
restores the caller’s setting on return or failure. Header readers declare their
schema-bounded cardinality (one exact header, at most eight instrument slots),
so their lateral join is not planned as a million-row result.
Gas booking and composition retain their existing publication path and graph
admission. Publication checks their exact accepted command from the trade record,
and schedules without linked quantity ports retain an empty cell-map seal.
Migration 0148 requires an explicit rebooking schema window.

Qualification includes an injected booking identity mismatch: the API refuses it,
no trade revision commits, and the same operation succeeds after restoring the
correct identity, with identical subsequent retries. Focused managed checks
refuse same-family price changes, changed pricing bindings and misplaced or
missing members. Disposable database checks cover command mismatch, wrong actor
or instrument, revoked read/write/reference access, historical contexts and
worker exclusion for the booking-only reader. Swap qualification repeats identity
fault injection, rollback and identical retries for dated, whole-term and factor
bookings. Normal history still hides revisions without committed receipts and
revisions beyond the selected knowledge boundary. The owned scheduled-swap and
side-observation suites also pass after sharing the term projection, including
amendments, settlement/cash continuity, six observation conventions, custom
projection reopening and retained worker checks. These regressions qualify the
existing paths and booking boundary, not the new live execution cutover.

The migration refuses existing sealed requests and requires an explicit rebooking
schema window. It does not silently backfill connections, rewrite history or
supply a valuation fallback. No hosted installation has been performed.

The managed source adapter now attaches in-memory input evidence to each
`BookedSideEvaluation`, keyed by instruction, cell and scenario. Evidence retains
selected volume revisions and knowledge, asset capacity versions, dated price
lineage and settlement selection, native price resolution and assignment
adjustments, and discount factors with their rate evidence. The program digest
is cached when compiled or read, rather than serialized again during valuation.
Only demanded cells receive evidence. Input ports record their received values;
the instrument retains provider mappings and each evaluated member's receipt.
Pure interpreter execution has no source receipt.

Focused checks cover projected and actual source evidence, scenario identity,
coverage masks, and quantity-only instrument providers. This is not a detached
or durable audit format: context and requested outputs remain on the containing
evaluation, and native evidence retains the prepared curve in memory. Durable
source manifests and live portfolio/risk cutover remain unqualified. Connected
custom projections use the existing shared graph binding, including exact output
shocks and lineage. Admission requires an unshocked graph base and the same
retained plan and pricing bindings for its scenarios. Focused checks refuse a
shocked base, another plan and an ordinary-curve fallback. Contract-observation
receipts preserve each selected sample and its actual/projected state.

## Schema release requirements

Migrations 0147 and 0148 follow the physical gas migrations on master.
Both refuse retained sealed booking requests; they do not convert or delete
history. Deployment requires an explicit archive, restore and rebooking plan
under the existing historical cutover contract, with booking writers stopped
through both migrations and activation of the matching API. Old application
booking commands are incompatible with the new publication contract, so an
image-only rollback is not a qualified recovery plan.

The release reader admits migration inventories plus four envelope files
within a 256-file cap, and refuses the 257th file before writing its body.
The installed operator is not updated by an application release: qualify and install the matching
`release_bundle.py` during the operator window. Stop the deployment timer before
publishing the schema release to avoid quarantining its head; promote the exact
migration inventory and database fingerprint only after the schema and application
checks pass. Restart polling only after the serving application is ready. The
[hosted deployment contract](hosted-pilot.md) still forbids automatic schema changes.

This checkpoint enables booking publication checks and supplies retained
execution adapters. It does not switch Blotter or the risk worker to direct
program execution, qualify long-term PPA throughput, or establish a deployment
window for an existing installation.
