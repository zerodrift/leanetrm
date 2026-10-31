# Shared domain mechanisms

Common mechanisms have a concrete owner and callers. Financial meaning stays
with the typed domain contract; authorization and persistence stay with the
operation that owns the database connection.

| Mechanism | Owner | Consumers and preserved boundary |
| --- | --- | --- |
| Signed exact fractions | `Shared/Numerics/SignedRatio` | Cash, quantity, price and schedule arithmetic; wrappers retain units, bounds and explicit rounding |
| Exact quoted price | `MarketData/ExactUnitPrice` | General price and payment calculations; a financial value type, independent of custom-curve authoring |
| Quantity reference coherence | `MarketData/QuantityDefinitions` | Both power quantity compilers; five exact keys must belong to the same revision |
| Retained manifest encoding | `MarketData/Trading/ScheduleManifestWriter` | Generation and authorized paged verification; one canonical format, unchanged hashes |
| Authorized book selection | `Api/BookScope` | Futures, swaps, PPA, positions and portfolio risk; exact visible IDs, explicit filter intersection |
| Prior settlement outcome | `Api/SettlementCommandReplay` | Determination, gross cash and net cash; the existing database function retains atomic retry and authority enforcement |
| Database login validation | `Api/DatabaseLoginBoundary` | API, worker and reference import; explicit service profiles and common restrictions |

New physical consumers should reuse these owners rather than copy existing
product handlers. Reusing mechanics does not enroll a physical obligation in a
swap lifecycle, infer a measurement conversion, or merge independent cash and
commodity obligations.

Book-directory reads use the caller's authorized database connection and catalog.
Selection validates explicit arrays and availability, preserves directory order,
and returns the intersection of book and portfolio filters. Callers retain their
own ordering and empty-result behavior. Futures and swap holdings now consistently
intersect the visible directory even when filters are empty. No selection is
cached across authority contexts.

The exact price type's namespace is now `OpenEtrm.MarketData`. Source consumers
must use that namespace. Public JSON properties and the inspected retained
identity encodings remain unchanged; normalized fraction equality continues to
support hash collections. Unbounded cash/ordinary-price intermediates and bounded
quantity/compound-price operations retain their existing policies.

Reference relationships use exact catalog/version keys. Textual codes that are
components of constrained composite keys remain valid identities; labels and
unqualified strings cannot replace them. Closed operation names and model
versions remain explicit implementation contracts. Migration 0149 adds the
missing snapshot, parent and series-contract relationships described in
[retained schedule integrity](retained-schedule-integrity.md).

## Verification

Managed checks cover shared rational arithmetic and invalid defaults, financial
bounds and rounding, public amount serialization/equality, quantity coherence,
manifest parity and paging, and book selection. The shared, market-data, rates
and API check suites, worker build, Python verification tests and frontend build
were exercised locally during this refactor.

Before release, run the ordinary disposable-database workflows as well:

```sh
python3 tools/check_database_login_boundaries.py --pg-bin "$PG_BIN" --dotnet "$DOTNET"
python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --scheduled-swap-check
python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --retained-schedule-check
python3 tools/run_preview.py --pg-bin "$PG_BIN" --dotnet "$DOTNET" --check --swap-risk-holdings-check
```

The login checks are also registered in `check_all.py`; the observation foreign
key checks run inside the scheduled-swap workflow. These database workflows were
not executed successfully in the refactor environment: PostgreSQL initialization
was denied its required shared-memory allocation. Managed checks do not replace
migration, database-role or persisted-workflow qualification. No migration was
applied to an existing database.
