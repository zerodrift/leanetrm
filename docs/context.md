# Historical context and temporal data

Status: baseline approved for implementation and publication on 2026-10-16.
The broad baseline below remains a product contract. Shared contexts for existing
market observations, commodity/rate curves, exact definition/reference bundles,
schedules and contract-date candidates are qualified in owned previews; see
[implemented scope](implementation.md#shared-historical-market-view-and-administration).
Immutable synthetic futures booking history now shares the certified cutoff and
adds exact effective-instant selection. Latest-head futures and financial swap
amendments retain prior effective/knowledge views. General lifecycle transitions,
activation and production recovery remain unimplemented.

This is a baseline product requirement, not an optional reporting feature. Read
with [architecture](architecture.md), [lifecycle](lifecycle.md) and [plan](plan.md).

## 1. Two independent historical coordinates

The application must answer both:

- What was effective at a business date/instant?
- What did this installation know at a selected point in its retained history?

Use `effective_from` / `effective_to` for the effective interval and `known_from` /
`known_to` for the knowledge interval. Point queries use `effective_at` and
`known_at`: "what was effective on this date, as known on that date?"
These are different coordinates. Bitemporal behavior is implemented in the
application's retained data model over an unmodified database; a database fork
or a different database product is not required.

Use the same effective/known terminology for operations/risk users, quants,
developers and AI agents. The UI can say "Effective on" and "As known on" while
the API/schema keeps precise identifiers. Separate contract attributes use explicit
names such as `contract_effective_date`, `delivery_start` and `delivery_end`.
They are not aliases for the row's historical effective interval.

An illustrative amendment:

```text
May 1:  Book a trade with quantity 100.
May 10: Record an amendment to quantity 120, effective May 5.
```

| effective_at | known_at | Selected quantity |
| --- | --- | --- |
| May 7 | May 7 | 100: the amendment was not known yet |
| May 7 | May 11 | 120: the later amendment changes the corrected view of May 7 |
| May 3 | May 11 | 100: the amendment is not effective on May 3 |

The example assumes resolved instants and sufficient retained coverage. Production
date/time selection must specify the applicable clock and boundary convention.

An instrument's future delivery dates are not its existence/effective-time filter.
A trade booked in May for November delivery can exist in the May environment.
Keep trade existence, effective terms and future scheduled delivery distinct.

## 2. Requested context and resolved context

The user selects a requested business environment. The server resolves it once
into an immutable context token used throughout that environment.

Proposed request coordinates:

```text
RequestedContext
  scope
  effective_at
  known_at
  valuation_at       # explicit resolved instant
  market_as_of       # explicit resolved instant
  scenario           # explicit base selection or versioned scenario reference
```

An explicitly selected ordinary historical mode uses the selected instant for business
time, knowledge cutoff, valuation time and market cutoff. Advanced controls can
separate them, with explicit labels; changing one coordinate must not silently
change another.

This named mode is an explicit selection rule, not a missing-field fallback.
Expand it to complete visible coordinates and an explicit scenario selection
before evaluation; retain them in the resolved context. Without complete inputs
or an explicitly selected supported expansion rule, reject the request. Never
silently assume base scenario, current market, a valuation date or a cutoff.

The first historical valuation mode requires `effective_at = valuation_at` and
`market_as_of <= valuation_at`. A supported advanced mode may relax those constraints
only with explicit semantics. Selecting a pre-expiry trade state and valuing it
after expiry requires a defined lifecycle roll/projection and its necessary
fixing/exercise facts under the same knowledge cutoff; otherwise reject it.
`market_as_of > valuation_at` is hindsight, permitted only as an explicitly labeled
counterfactual analysis rather than ordinary historical valuation.

`market_as_of` limits market observation times; `known_at` limits what information was
available to this installation. These are not interchangeable. A forward price
observed in May may describe a delivery period in November.

The resolved context contains stable identifiers, including:

- Effective/valuation/market coordinates and the requested knowledge cutoff.
- Resolved committed revision/prefix pins for all participating data scopes.
- The market snapshot, actual contract aliases and curve/reference bindings.
- Applicable lifecycle state, rules, model and numerical policy versions.
- Scope, completeness/coverage status and context schema version.
- A context identifier/fingerprint carried by requests, responses and results.

Current permissions are validated separately; a historical token is not a
permanent access grant. The context is an immutable value, not an ambient mutable
clock or a long-lived database transaction.

Revision pins must form one causally consistent cut with complete dependency
closure across participating scopes. Individually valid prefixes fetched at
different times are insufficient. A trade referencing a shared curve or model
version requires that dependency to be included under the same knowledge
contract. A lazy reference discovered later must resolve within those pins or
fail; it cannot introduce a current dependency into the historical environment.

## 3. Business records and relationships

Retain logical facts/versions with half-open effective and knowledge intervals:

```text
effective_from <= effective_at AND effective_at < effective_to
known_from <= known_at AND known_at < known_to
```

Both axes use `_from` / `_to` consistently, with inclusive starts and exclusive
ends `[from, to)`. Document this in column comments and query contracts. Do not use
inclusive `BETWEEN` for temporal containment: it selects both versions at a shared
boundary. Validate `from < to` and reject incompatible overlapping history.

The knowledge columns are UTC timestamps (`timestamptz`), never calendar dates.
Retain supported subsecond precision for multiple corrections in one day. The
effective timeline also uses explicit instants; date-only contractual fields stay
separate dates and require an explicit clock/boundary when mapped to the timeline.
Database connections explicitly pin UTC timestamp transport, independently of
server defaults or process time-zone settings. API timestamps carry an offset
and normalize to UTC; display zones do not change
the selected instant.

Use one reserved finite end marker for open-ended timeline rows, consistently in
storage and API serialization. The proposed exact marker is
`9999-12-31T00:00:00Z`; each supported implementation must enforce this exact
timestamp bound. It is an exclusive
end, not a real business date or the last instant of an inclusive day. Reject real
timestamps/context selections at or beyond the reserved marker. Do not mix null,
database infinity and a finite sentinel within this convention. Lower bounds and
coverage remain explicit; an open end does not prove history existed indefinitely.

Knowledge endpoints are assigned by the trusted knowledge/publication protocol,
not by clients or imported source timestamps. Retained revision/commit identities
still provide ordering, concurrency and replay proof behind the timestamp query.
Equal timestamps and clock regressions require the qualified resolution rules
in section 5; precision alone cannot establish commit order or remove ambiguity.
Do not synthesize a claimed wall-clock availability time merely to make it unique.
Qualification must also define valid stored knowledge intervals under ties and
regressions. Revision ordering alone does not prevent zero-width or reversed
timestamp ranges. One candidate is complete publication batches whose final
projection is timestamp-addressable, with intermediate revisions retained for
journal/replay access; no representation is selected until its semantics pass
the resolver and storage tests together.

Retain replaced facts and the amendment/correction links. Serialize revision
allocation and validate that
no two incompatible states apply to the same logical record at the same pair of
coordinates. Range columns/indexes or equivalent indexed predicates and
transactional constraints are implementation tools, not substitutes for semantics.

Apply temporal selection to relationships as well as scalar fields:

- Portfolio-to-book membership and book-to-trade membership.
- Trade instrument bindings, quantities, terms and lifecycle state.
- Instrument/side definition references and any effective binding intervals.
- Price-index/location associations, curve recipes and calendar/convention binds.
- Relevant reference data and model-selection bindings.

Immutable instrument/side definitions can be retained once. The temporal fact is
which definition the trade or other relationship referenced under the context.
Do not resolve a historical trade through today's book membership, curve recipe
or calendar merely because the referenced object ID still exists.

Each instrument/quantity binding is held directly by Trade. Retain stable binding
and obligation keys inside Trade for history when necessary; this does not add
another quantity-bearing business class. Economic equivalence allows definition
reuse, not automatic netting of distinct lifecycle/settlement histories.

## 4. Market observations and corrections

Market data retains separate coordinates:

| Coordinate | Meaning |
| --- | --- |
| Factor/contract identity | The actual dated market input, including quote kind and units |
| Observation time | The market time the observation represents |
| Delivery period | The future or historical period being priced |
| Source publication metadata | What the source reports about publication/correction |
| Installation knowledge revision | When this installation retained the observation/correction |

Select observations under the context's market cutoff and knowledge revision,
then apply the explicit source-selection/staleness/correction policy. A quote
correction imported later cannot overwrite the original historical observation.
The policy and its dependencies are retained inputs.

For example, a May 6 observation imported on May 8 is not information this
installation knew on May 7 merely because its observation/source date is May 6.
A corrected May 6 observation received on May 10 is excluded from a May 7
knowledge context while remaining available in a corrected view using later
knowledge.

Imported archives cannot silently backdate this installation's knowledge. If
trusted source archives support a reconstruction of externally available history,
that is an explicitly labeled source-history mode with separate provenance.
It does not claim to reproduce what this installation actually knew.

Never fall back to newer data when a historical input is missing. Distinguish
known-empty results, a missing input, an unavailable historical archive and an
unauthorized result. Do not reveal private history through those distinctions to
an unauthorized caller.

## 5. Mapping a date to durable knowledge

Committed application revisions are the authoritative ordering of knowledge.
Users still need a date/time selector: resolving `known_at` into those revisions
is mandatory before the baseline historical environment is accepted.

A pre-commit row timestamp, transaction start time or client-supplied source date
does not prove information was committed/available at that instant. An ordinary
MVCC transaction snapshot is also not a durable archive after the transaction
ends. Do not use either as an undocumented historical-visibility shortcut.

Choose and document the knowledge-time contract before implementing its resolver:

- Application-publication time: when an explicit publication protocol made a
  complete retained knowledge prefix available to the application.
- Database commit time: the recorded commit coordinate of the source transaction,
  captured and durably retained through supported database mechanisms.

These are different promises. Selecting one does not justify labeling it as the
other. A publication implementation must prove availability and a stable complete
prefix, not merely insert a timestamp into the original write transaction.

The resolver must handle publication/capture lag, crashes, gaps, equal timestamps,
clock regressions, recovery/source lineage and retention boundaries. It returns
an immutable prefix/manifest only after its cutoff is certified complete. If it
cannot prove the requested cutoff, it reports pending/unavailable/ambiguous as
appropriate rather than including later facts or silently selecting current data.
Persist the successful resolution so a captured context never changes later.

A retained journal of commit metadata from standard logical replication is one
candidate if commit-time semantics are selected. It introduces capture privileges,
checkpoint/lag and recovery requirements and must be independently qualified.
An application-publication alternative likewise needs its own proof and tests.
No particular implementation is approved by this document.

The database's transient old-row/commit-timestamp storage must not be assumed to
be the permanent application history. Backups and restores must retain facts,
revision/publication metadata and source lineage together. Missing knowledge
metadata is a loss of historical coverage, not permission to invent timestamps.

## 6. Consistency across the environment

All business reads and calculations use the resolved context:

- Portfolio/book trees, trade blotters, searches, filters and counts.
- Trade details, instrument/side terms and composed schedules.
- Quotes, curves, fixings, volatility/FX/discount data where supported.
- PV, risk, scenario runs, explain, reports and exports.
- Agent tools, previews and retained workflows that read business data.

The same token flows from API operations to domain methods and every lazy/paged
read. Shared scenarios are overlays on this context's market snapshot; they do not
refresh inputs from current data. Jobs retain the resolved token when queued.

Pure schedule resolution may be shared when its relevant inputs agree, but
context-dependent lifecycle decisions and quantities remain pinned. Calculation
keys include the actual economic state and context dependencies. Cache hits must
not cross temporal coordinates accidentally or bypass current access checks.

Changing the UI context creates a new environment generation. Cancel/discard old
requests and accept responses only for both the active context ID and active
generation/request identity. Returning A to B to A does not let an old request
for A overwrite the new A environment. Clear or
partition caches, selections and pagination cursors; do not display mixed-context
rows or charts. Another browser tab can retain its own context.

`Current` is an explicit mode that resolves a fresh coherent snapshot on refresh.
An individual calculation still freezes its inputs. Show the selected times,
resolved snapshot/revision and any capture lag so a label does not imply data
newer than the actual certified prefix.

Historical business views are read-only by default. The application can calculate
and retain a new historical analysis without altering old business facts. A
backdated amendment is a separately authorized current command with an explicit
effective interval; it records new knowledge now rather than rewriting the past.

Account security, current permissions, credentials and provider secrets remain
current. Historical context never restores a disabled identity or an old grant.
Administrative audit can inspect retained policy history through a separately
authorized operation without using historical grants to authorize ordinary reads.

The proposed default evaluates record access against the current security
classification/control-plane policy, separately from historical business
membership. If a trade moves from book A to private book B, access to historical
book A alone does not authorize reading that trade's old state. Any policy
granting former-book access must be an explicit current grant/rule, not an
accidental consequence of a historical join. Current policy must also govern
searches, counts, aggregates and dependent definitions without leaking hidden data.

## 7. Valuation and replay modes

Keep these requests distinguishable:

| Mode | Meaning |
| --- | --- |
| Historical environment | Business state and market information under the selected two-time context |
| Corrected historical view | A past business date using a later explicitly selected knowledge cutoff |
| Exact saved replay | Reproduce a retained receipt with its captured inputs and implementation |
| Historical revaluation | Value historical inputs under a separately selected newer model/policy |
| Counterfactual scenario | Apply explicitly selected shocks or assumptions to a pinned environment |

Do not substitute current model behavior for a saved historical implementation
and call it exact replay. If the required implementation or source facts are
unavailable, return that limitation explicitly. Model upgrades and reference
corrections must not change the meaning of a retained receipt/context.

## 8. Date/time controls and coverage

The UI can present one simple historical date/time selection that sets the
ordinary coordinates together, with advanced controls for separate business,
knowledge, valuation and market times. Exact names/layout remain a UI decision.

Use an explicit account/display time zone and normalize instants to UTC. Reject
nonexistent local instants; require disambiguation of repeated instants. Date-only
contract terms stay dates. A selected date needs a visible time/boundary rule;
do not silently assume exchange close, a holiday calendar or end of day.

Display zones are separate from contract/market clocks and source-feed clocks.
Recurring TOU rules retain their local clock semantics; only resolved delivery
endpoints are UTC instants. Keep contractual operating dates and hour labels
visible in their own clock. See [time-and-schedules.md](time-and-schedules.md).

Display the start of provable retained coverage. Contexts before that coverage
are unavailable, unless a separate trusted reconstruction mode is selected.
Absence of data must not silently become proof that no trades or prices existed.

## 9. Qualification gates

Before offering the historical environment:

1. Reproduce the amendment table above and preserve every captured old result.
2. Verify later market imports/corrections never leak into an earlier context.
3. Verify future delivery contracts remain visible if they were already known.
4. Change portfolio/book membership, curve dependencies and calendars later;
   historical joins must retain the prior bindings.
5. Race writes, cutoff resolution, publication lag and crash/recovery; a context
   must never include a transaction that misses its stated knowledge contract.
6. Exercise equal timestamps, clock regressions, retained-coverage gaps and
   source recovery/lineage changes with fail-closed results.
7. Verify pagination, concurrent jobs, browser navigation and agent tools share
   one context. Switching contexts must discard late responses.
8. Revoke current access and deny historical/cached/exported results accordingly.
9. Distinguish historical revaluation from exact saved replay and verify upgrades.
10. Verify backup/restore retains temporal facts and their knowledge metadata.
11. Publish shared dependencies concurrently with referencing trades across scopes;
    verify consistent-cut/dependency closure, including lazy reads.
12. Move a trade from accessible book A to private book B and verify historical
    membership does not bypass the current record-access policy.
13. Switch A to B to A with delayed responses; require context and generation
    identity, not context identity alone.
14. Reject unsupported lifecycle roll/hindsight coordinate combinations and test
    every explicitly supported projection mode against its required facts.

This is a core storage/API/UI contract. Implementation choice O07 remains open;
that choice must be resolved and tested rather than removing the requirement.


## Retained risk execution

The risk UI uses this same header context. Its temporary scenario stack derives
immutable children with explicit parent-relative quote shifts. Popping a child
resumes its parent without restoring mutable data. Each durable risk occurrence
retains the resolved context, selected books, exact observation/definition pins,
scenario contract and engine fingerprint. A worker never follows a later UI
selection or an unresolved "current" date. Current execution authority is checked
separately, including revocation since admission. Recurring date-selection rules
and future valuation regimes require supported typed contracts before execution.

Frozen observation uses are retained alongside exact observation IDs. Workers read
those pins under the captured financial context and current authority; they never
resolve latest corrections again. See [risk scenarios and jobs](risk-engine.md).
