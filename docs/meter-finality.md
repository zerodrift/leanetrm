# Shared meter-finality prerequisite

This implements only the shared meter evidence prerequisite of the accepted
[SP1–SP9 bounded contract](ppa-settlement-spec.md), through migration
`0096_meter_finality.sql`. Global S1 remains incomplete. Cash determination
acceptance, obligations, payments, workers and gas implementation are absent.
The existing PPA valuation path still uses its Phase C meter-availability rules;
it does not consume finality or advertise accepted settlement.

## Authorized database operations

`settlement_data.publish_meter_finality` accepts one exact source-stage native cell:
market catalog, sealed definition revision, volume source code, stage, UTC start
and end, exact reading revision, explicit `final`/`suspended`/`withdrawn` state,
verification-effective instant, expected predecessor UUID (null for the first
head), request UUID and a nonblank reason of at most 500 characters. It publishes
one immutable head and actor-scoped command receipt atomically. A transaction
must commit its raw reading before attempting finality publication; its knowledge
cannot be inferred from an uncommitted row.

The caller needs current `ReadMarketData` and `VerifySettlementMeters` in the
market catalog, plus current read access to the source's exact reference catalog.
The new permission has **no default role grants**. Source scope is the existing
market/reference catalog authorization boundary; no new asset-ACL model is
inferred. Agent calls retain the existing principal and delegation checks.
Publication is refused from historical request contexts. Runtime callers have no
direct table access and cannot call internal certification/validation helpers.
These are database operations available to authenticated runtime transactions;
no HTTP endpoint, UI, agent capability or worker is installed by this prerequisite.

The stable chain key is catalog, asset code, volume-source code, stage and exact
UTC cell bounds. Definition revision and reading revision are retained exact
foreign-key provenance, not ways to restart a chain. One serial successor must
name the current predecessor. A cloned definition using the same stable codes
cannot evade withdrawal. Code renames or aliases do not prove continuity; no
cross-code lineage inference or settlement eligibility for renamed sources is
implemented. Changing a source's asset, stage or native cell identity similarly
requires separate contract qualification before a settlement consumer may use it.

Every head pins the exact source definition and its metered series, exact raw cell
revision/endpoints, reference version, units and retained operating-day clock.
The raw relationship uses its existing primary key; an insertion trigger checks
endpoint equality against that immutable row. No index is added to the shared raw
series table.
Null or negative generation readings refuse; explicit zero is valid. Suspension
and withdrawal also name an exact valid reading for audit. New raw revisions,
including raw withdrawals, do not silently replace the separately published
finality head. Reinstatement requires an explicit new `final` head.

The existing immutable `power_data.operating_days` calendar supplies exact day
bounds and the clock fingerprint. Its managed renderer validates the pinned
clock/tzdb version. The operation refuses missing or ambiguous retained day
coverage and off-grid cells, rather than recomputing old dates with the database's
current time-zone rules. The exact source definition must cover the original
cell's operating date and have certified publication knowledge by the cutoff.
It need not remain effective at the later verification instant: corrections retain
original source terms. The effective instant must be finite, no earlier than cell
end, no later than server time, and nondecreasing along the chain.

## Certified selection and retained authority

`settlement_data.read_meter_finality` takes the same exact source/cell pins, a
certified market context UUID and an explicit business instant. The authenticated
request must already have that context bound through `security.use_market_context`;
the context's effective date must match the business instant's UTC date. Current
market/reference authority is checked independently of historical evidence.

Selection chooses the latest chain head visible at **both** verification-effective
time and commit-certified knowledge cutoff. Only then does it check the requested
exact definition/source pins and inspect state. Incompatible pins refuse instead
of restoring an older head. A `final` head returns its exact raw numeric value as
text, including zero. `suspended` and `withdrawn` return their authoritative state
and a null eligible value; neither falls back. No visible head is a structured
`meter_finality_missing` refusal. This read alone is not a cash determination.

The bounded response retains head/predecessor, raw reading revision, source,
reference, unit, operating date, clock fingerprint, both evidence knowledge times,
effective time, context and verifier evidence. Verifier evidence includes actor,
represented principal, delegation, tenant/workspace, the successful permission,
check instant and authorization epoch. Revoking a verifier later does not erase a
previously valid assertion; an explicit new suspended/withdrawn head changes it.
Current read authority still applies.

Raw reading knowledge and finality knowledge are separate PostgreSQL commit-time
receipts. Finality certification uses the existing shared knowledge gate and
monotone knowledge clock. Its wrapper is inserted **below** the existing-close
worker's admission-context shortcut, preserving its exact context reuse and
prohibition on fresh certification. A publication receipt returned before commit
contains identity/pins, not a fabricated own commit timestamp. Subsequent certified
selection resolves that timestamp.

## Concurrency and operation boundary

The existing authorization epoch share lock serializes control changes through
transaction completion. The shared publication gate serializes predecessor checks,
command replay and insertion. Authority is rechecked after lock waits and after
writes, including before returning an idempotent receipt. Exact same actor/request
and server-built complete payload returns the original receipt before new source
selection; changed content conflicts. A new request with a stale predecessor
refuses. No caller-supplied digest establishes equality.

A deferred constraint guard additionally rechecks authority for an ordinary
transaction's completion, and refuses a publication whose request expires after
the operation but before normal commit. PostgreSQL permits a caller to force
`SET CONSTRAINTS ... IMMEDIATE`; this guard is **not** an arbitrary-SQL physical
commit-time authorization hook. The supported operation boundary is current
authority after all operation waits under the epoch fence, with trusted transaction
completion. This task does not redesign the repository's runtime transaction
trust boundary or claim that PostgreSQL can reject every deliberately delayed
commit after a caller has forced deferred checks early.

No source selection or arithmetic is cached across requests. Publication and
selection accept one cell and return a bounded response; this is not a bulk
importer or monthly cash preview. Certification catch-up and chain-selection cost
depend on retained evidence history; no history-work cap or bulk throughput
qualification is claimed. Migration deployment requires the usual separately qualified schema
upgrade with runtime admission drained; no deployment was performed here.

## Verification

The owned-database checks are part of the existing `--ppa-check` stage:

```sh
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin \
  --dotnet /path/to/dotnet --no-build --ppa-check
python3 -m unittest discover -s specs/settlement -v
```

The suite checks real independent backends observed waiting at the publication
gate: competing first publications, identical requests, and competing successors.
It also checks predecessor identity, both temporal coordinates, old-cut correction
selection, suspension/withdrawal without fallback, zero versus null/negative,
current scopes, token/grant expiry during waits, normal deferred commit expiry,
immutable storage, same-code definition changes, retained clocks and internal
function privileges. The synthetic finality sources are isolated from Phase C HTTP
fixtures. Independent specification and Phase C regression evidence are recorded
with the completed qualification; no global settlement phase completion follows.


Qualification caught a compatibility regression in an initial migration draft:
an extra raw endpoint index displaced the existing interval-history range index
in legacy overlap probes. The 35,136-cell rewrite check exceeded its unchanged
30-second limit. Reusing the existing exact-reading primary key plus the endpoint
insertion check restored the original query plan; profiled rewrites completed in
about 0.27 seconds each (baseline, without profiling: 0.22–0.24 seconds). The legacy
query, timeout and authority rules were unchanged. These are diagnostic timings,
not a new finality throughput benchmark.


## Qualified results — 2026-10-22

The corrected tree passes **all 45 stages** of the complete synthetic gate in
**1,587.087 seconds**. The gate includes **40 meter-finality checks**, the existing
PPA storage/HTTP oracles, revision admission, browser/navigation, scenario reuse,
legacy interval series and worker recovery. All 659 executable-file hashes match
the corrected gate-start snapshot; 23 owned preview databases were removed.

Independent adversarial review is **GREEN**, with separate successful runs of
40 finality, 80 PPA storage, 32 PPA HTTP, 20 interval-series storage and 12
interval-series HTTP checks. The unchanged independent settlement specification
passes all 36 cases. Meaningful RED evidence preceded implementation and the
calendar, normal-commit authority and legacy-index compatibility fixes.

The real ten-year hourly risk replacement/lost-acknowledgement regression completed
in **349.093 seconds**, publishing one complete report with original admission
pins and deadline retained. Renewal failure, cancellation, authority loss and
overall-deadline controls publish no report. This preserves Phase C evidence; it
does not implement a settlement worker or qualify a new throughput target.

Implementation is complete only for this bounded shared meter-evidence prerequisite.
Global S1, cash determination acceptance, obligations, payments and gas remain
open. This historical qualification did not deploy the application.
