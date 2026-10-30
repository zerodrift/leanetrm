# Booked executable Side schedules

This checkpoint implements booking-time executable storage and an authorized
direct loader for D258. It does not switch the application's valuation adapters
to that loader. Dynamic input adapters and the common booked pricing cutover
remain subsequent work in [Side schedule evaluation](side-plans.md).

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
