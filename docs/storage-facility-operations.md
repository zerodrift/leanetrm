# Storage facility operations

Status: locally implemented operating profile. Managed behavior, native admission,
source correction, concurrency and populated upgrade checks pass on unmodified
PostgreSQL 16. The integrated shared UI browser checks pass. This profile does
not install the commercial Storage or Transport instruments. Release qualification
and schema activation remain separate steps.

## Physical asset and commercial contract

A physical facility has its own stable identity and immutable rule versions.
Its business key is the exact reference catalog and location code; a fresh
request for an identical definition and effective instant acknowledges the
existing fact rather than creating another asset or rule version.
Exact relationships identify its existing location, pipeline ports, quantity basis
and operating clock. Physical inventory bounds and injection/withdrawal ratchets
belong to that version. The [commercial gas storage contract](gas-storage-contract.md)
separately owns its entitlement, ratchets, plan and charges. Allocating its flows
to a facility is still open. Adding a contract cannot increase physical capacity.

The first consumer is storage physical operation under an explicitly complete
facility scope. It does not install the full commercial Storage instrument or a
Transport agreement. Opening physical inventory is dated evidence with revisions,
not a default copied to each contract. A physical observation or movement does not
create an owned lot, commodity liability, contractual fulfillment or cash.

## Installed assessment profile

`storage_total_physical_step_ratchets_v1` selects total physical inventory,
absolute inventory bands and exact native quantity per elapsed hour. The explicit
policies prohibit simultaneous positive injection and withdrawal and apply no
implicit loss or conversion. Other ratchet interpolation, inventory bases,
conversion and loss laws need separately qualified consumers.

Bands cover the full declared inventory range without a gap or overlap. Each
movement supplies an exact port, direction, quantity and interval, with constant
gross flow over that interval. Overlapping flows are added at their shared event
boundaries. Every band traversed for positive inventory extent must support the
gross rate. An endpoint arriving at a boundary has not yet flowed in the next
band; a subsequent movement is assessed in its own direction from that boundary.
Inventory and rates remain rational, without rounded crossing timestamps.

Chosen plans explicitly state that the listed activities cover the window and
that gaps are idle. Actuals require independent evidence that reporting covers
the complete window; a missing report is not zero flow. Missing opening evidence
or incomplete actual coverage leaves the total inventory assessment unassessed
with no closing level. A known opening-level breach remains visible even when
subsequent reporting is incomplete. Complete measured evidence outside a physical limit is
retained with a breach. Prospective plan admission requires a feasible assessment.
Twenty-three and twenty-five elapsed hours remain their actual durations.

The rule's effective instant is its applicability start. A selected rule must
already apply at the window's start; a known superseding rule inside that window
requires an explicit supported transition or separate assessment windows.
Observed openings and completed measured intervals cannot extend beyond the
command's effective instant or the certified knowledge cutoff. Chosen plans may
be prospective. A measured closing level describes its exact reporting window,
not an inferred current inventory. The UI displays that window and its pinned
rule revision separately from the current definition's ratchets.

## Activity, allocation and history

Planned and measured activity have independent identities and corrections. Further
nomination, confirmation and scheduling tracks must remain distinct when their
consumers are installed. Physical activity references the asset and pipeline
ports once. Contract allocations link the same physical source through exact
relationships; allocation edits neither create another physical flow nor transfer
title. Existing own-trade views retain only that trade's authorized share.

Definitions, opening evidence, activities, coverage and accepted plan witnesses
retain exact versions and knowledge lineage. Later corrections require an explicit
reassessment rather than silently changing an accepted plan. Historical reads
must select the known revision before applying its business-time geometry.
Native admission must serialize the shared facility state, recheck current
permission and input heads, and independently validate the complete numerical
witness. A caller-supplied success flag is insufficient.

Reported-source selection searches exact compatible revisions by pipeline,
point, operating date or identity, with bounded results and an explicit request
to refine a truncated search. Linking a source retains its existing quantity and
allocation ledger; it does not create a second writable physical quantity.
The initial facility directory displays at most 200 assets and states when more
exist. Broader facility discovery needs a qualified searched or paged authoring
path; the reported-flow search does not search the asset directory.

## Transaction admission

The supported writer is the authorized managed business operation. It captures
the original permission deadline, validates the native result and rechecks commit
admission immediately before committing. Native structural guards validate
typed facts, immutable membership, exact source lineage and the numerical witness;
deadline guards remain deferred through the producer's structural flush.

Stock PostgreSQL permits a caller with arbitrary session control to execute
`SET CONSTRAINTS ALL IMMEDIATE` and flush deferred triggers before commit. Native
deadline triggers therefore do not provide an unflushable commit boundary against
such a caller. The managed commit check is mandatory; direct SQL that changes
constraint timing is outside this transaction protocol. This follows PostgreSQL's
[constraint timing contract](https://www.postgresql.org/docs/16/sql-set-constraints.html).

## Qualification

Managed checks cover aggregate gross rates, intermediate bounds, ratchet crossings,
exact partial intervals, conservation and retained historical rule selection.
Native checks cover complete versus partial reporting, correction history,
concurrent plans, stale rules, revoked authority, fact retries, forged receipts,
sealed membership, original outcomes, knowledge horizons and source search.
A populated upgrade retains an existing reported quantity of 100 with 60 allocated;
source correction to 90 preserves the same ledger and its allocation.

The integrated browser run passes 30 checks covering authoring, fresh-request
fact acknowledgement, dated opening evidence, prospective review, retained
measured breaches, source search, request recovery, local-time display, historical
read-only views and existing allocation links on desktop and narrow screens.
Native postconditions retain the original source quantity of 100 and allocation
of 60, with no new title or cash. Assistant page/catalog parity also passes.
The required verification runner includes native, populated-upgrade and
real-browser checks in the physical partition. No deployment is authorized by
this document.
