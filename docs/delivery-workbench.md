# Reusable delivery workbench

The workbench composes existing native operating consumers. Its first profiles
are chosen gas transport intervals and accepted crude receipt/dispatch events.
Commodity, movement method, measurement, service rights, custody, title and cash
retain independent contracts. A profile name does not activate another product.

## Behavior and ownership

`DeliveryBehavior<TInputs>` is the small pure extension contract in
`OpenEtrm.MarketData.Physical`. Resolved immutable inputs produce a
`DeliveryAssessment`: exact quantity specification, activities, measures,
constraints and declared capabilities. The assessment is a projection, never an
authorization token, native admission witness or writable quantity store.

`GasPipelineDeliveryBehavior` calls the installed route conservation kernel and
consumes its complete native capacity assessments. Measures count external
receipt/delivery boundaries once; serial and split/merge handling is not new
fulfillment. Fuel and loss remain explicit. Missing capacity is unassessed.

`CrudeReceiptDeliveryBehavior` calls the existing `CrudeInventoryCash` reconciler.
Accepted receipts and dispatches are discrete transfer events; reservations,
title, cash corrections and supported restrictions remain native. A transfer
has an exact instant and no invented duration. An interval has increasing exact
boundaries. Neither is an inventory balance.
Constraint residuals retain their declared profile meaning: gas reports signed
conservation/excess differences, while crude reports contractual quantity remaining.

The workbench does not change Side economic identity, contractual quantity,
pricing, contribution ownership or scenario dependencies. Each booked physical
membership keeps its own delivery occurrences. Independent source evidence is
retained once and linked to targets through exact relationships.

## Operating views

Facilities exposes Gas delivery workbench and Crude delivery workbench using the
shared described-operation shell, operation fields, inspection tables, activity
timeline, constraint presentation and retained-request recovery. Native adapters
provide every amount, policy, permitted action and reference choice. The browser
does not calculate fuel, capacity, allocation totals or cash.

Read context is the explicit operating business date and certified knowledge
cutoff. It is independent of a financial close. Changing account, profile or
context invalidates the old draft/review; an uncertain accepted command retains
its original identity and terms for status/retry. A historical view cannot write.

Gas transport reads expose current route assessment and immutable recorded
evidence. A stale capacity, source or membership is not a fresh successful
assessment. Crude reads expose their native retained receipts/dispatches and
reconciliation; their existing qualified lifecycle operations remain their
authoritative writers.

The page identities `delivery_workbench_gas` and `delivery_workbench_crude` are
explicitly unsupported assistant financial/source pages. Adding presentation
does not grant a new assistant tool or permission. Existing native authorized
business operations remain the shared entry boundary.

## Chosen-route fulfillment links

Migration 0358 adds relationships between exact chosen gas route revisions and
booked native delivery occurrences. It adds no physical flow, stock, title, cash
or second capacity reservation. The installed policy is
`exact_terminal_native_interval_v1`.

One allocation plan replaces the complete allocations for its identified route
source stream. Rows pin the route endpoint, booked obligation, trade revision,
instrument, native day, reference basis, direction and exact positive quantity.
Only external receipt/delivery nodes with exactly compatible native intervals,
points, products, legal entity and units are eligible. There is no inferred
proration, conversion, path, pro rata rule or allocation of a residual.
The first target adapter admits a single literal native-day quantity row;
multi-day schedule roots and timed delivery scheduling need separate qualification.

The source route already consumes its selected capacity claims. Relating that
source to trades does not debit commercial capacity again. Allocations conserve
each selected terminal source and each target's admissible quantity across the
applicable current plans. Ordinary request replay and semantic acknowledgement
return their first records; neither reinstates an obsolete head.
V1 refuses an assignment effective before a latest-known head in any stream
that has allocated the same target and native day. A future release cannot free
quantity for an earlier assignment. This is an explicit admission limit.

Allocation facts are immutable and published through the existing certified
journal. SQL foreign keys and native verification independently check the
typed source/target scope. Current authority applies to the whole shared source
and every target, including removed targets on replacement and original targets
on recovery. Source corrections and target amendments make assessment stale;
they never silently rewrite earlier accepted relationships.

The API prefixes are `delivery/workbench/gas-transport` and
`delivery/workbench/crude-receipt`, with directory, read, preview, record and
status operations. The described forms are the admitted closed native commands,
not a scripting or arbitrary `(type,id)` relationship interface.

## Manual nomination batches

Migration 0359 retains one actor-bound command receipt and exact native
nomination memberships for a reviewed group. The selected profile is
`complete_allocation_nomination_batch_v1`.

A batch names one committed current allocation plan and covers all of its
obligation/native-day targets. Requested quantities equal those exact selected
allocations. Every entry explicitly selects its service-account version, external
request reference, submission time, channel, reason and evidence. Account choices
are restricted to the exact owned booked anchor. A new reference starts a new
request stream; corrections of existing references retain the native predecessor
contract.
The first batch selector requires one applicable allocation stream. Multiple
streams remain inspectable without choosing a source implicitly.

Preview returns canonical inputs, the original requested inputs and the complete
native reviews. Record rechecks the source and quantity heads, executes existing
manual nomination operations in one transaction and retains the group outcome
atomically. Failure of any child rolls back the whole command. Reusing a request
identity with different inputs refuses; retry/status reauthorize the original
scope and return its original outcome even after later heads have changed.
All child reviews and certified operating/financial contexts are prepared before
the first native publication. Commit reuses the existing native command with
those exact certificates; it does not recapture knowledge over uncommitted
members of its own batch.

This operation records evidence of requests submitted outside the application.
It sends nothing and changes nominated quantities only. Counterparty responses,
pipeline responses and explicit confirmation/schedule adoption remain separate.

The batch uses the existing native gas operation context and requires an
explicit selected financial input frame. The operating workbench itself requires
no price frame. An operating date is never converted into an invented close.
The batch profile checks source applicability on the selected allocation's
retained effective business date under current knowledge, using the installed
UTC end-of-day operating clock. Native nomination reviews keep the separately
selected financial frame and its exact intraday timestamp.
Removing that native dependency is a separate qualified extension.

## Adding a delivery consumer

1. Identify the real operation, its owner, authoritative source and exact booked
   targets. Choose interval flow, transfer events or a separately qualified stock
   consumer. A commodity or equipment label supplies none of these behaviors.
2. Write independent numerical and temporal examples before extracting behavior.
   Reuse an existing policy only when all its semantics agree.
3. Implement a typed immutable input and a small `DeliveryBehavior<TInputs>`
   consumer. Keep authorization, persistence and clocks outside pure behavior.
4. Add a native adapter using existing source relationships, capabilities,
   expected-head admission and command receipts. Add tables only when this
   operation needs relationships that existing native stores cannot represent.
5. Supply the common assessment and backend field/action descriptors. Reuse the
   common shell and controls; register a specialized widget only for a supported
   interaction. Unsupported actions remain unavailable with an explicit reason.
6. Qualify save/reopen, corrections, original-request replay, current authority,
   historical source selection and populated upgrades. Keep cash, title, custody,
   accepted performance and valuation gates separate.

Future pipeline products need their declared compatibility/quality and native
measurement policies. LNG cargo handling needs qualified loading/discharge,
mass/energy, loss and acceptance rules. Gasoline by truck needs actual loading
and unloading evidence, load/compartment identity, measurement conditions and
delivery acceptance. A loading ticket and unloading ticket are distinct facts;
their difference is not automatically an accepted loss or a billing quantity.
These are extension requirements, not executable profiles in this increment.
Blending and transformations require their own conservation consumers.

## Qualification boundary

Local qualification passed on the combined `29aa49f6` baseline with migrations
0358/0359 and the compiled delivery implementation:

- The full managed core suite reported 785 checks. Thirteen related API groups
  passed, including the two delivery groups and existing route, capacity,
  lifecycle and Side source/presentation contracts.
- All 164 repository checks and 2,180 frontend tests passed. The frontend suite
  had no skipped tests. Desktop and phone views were visually inspected.
- `tools/check_delivery_workbench.py` passed on stock PostgreSQL 18. It verifies
  populated upgrades against complete existing trading/settlement row digests,
  exact Side/program/obligation identities, guarded helper drift, temporal
  conservation, source corrections, private storage and closed inputs.
- `tools/check_delivery_workbench_http.py` passed against the frozen combined
  API/UI and final SQL in a disposable stock PostgreSQL 18 database. It books
  synthetic gas/crude trades, saves/reopens exact terminal assignments, records
  complete manual nomination batches, injects a second-child insert failure,
  verifies rollback, retries and lost-reply recovery, and checks retained reads,
  current authority, forged-frame refusal and exact native crude handoff.

Independent review, complete merge CI and deployment remain separate gates.
Additional delivery consumers and live operator transmission require their own
qualification.
