# CI impact selection

D405 keeps the complete local gate and selects conservative groups for pull
requests. `tools/ci_impact.py` emits the selected matrix and full-gate indicator.
Repository checks always run. Every master push and manual dispatch remains full;
application publication additionally requires the complete same-commit gate and
existing publication enablement/environment controls.

| PR changed paths | Selected checks in addition to repository |
| --- | --- |
| Named root Markdown documents, Markdown under `docs/`, separate `site/`, independent settlement specs, root Python tests | None |
| `web/tests/` | Frontend |
| Rates/Shared managed test projects | Managed |
| Explicitly mapped browser scenarios | Every declared consumer, including frontend startup consumers |
| `web/src/` | Frontend, database and all sixteen integration groups |
| Managed runtime, API/MarketData check projects, shared fixtures, SQL, dependencies, build/tooling, workflow, deployment, unknown paths/helpers | Every group |

The browser map is deliberately bounded. Update `BROWSER_GROUPS` when a scenario
gains another consumer; new scenarios and shared helpers already require the full
gate. Managed runtime feature dependencies and runtime coverage-based test
selection are not implemented. Runtime TypeScript remains presentation only.

The impact job uses full local Git history. PR comparisons run from the common
ancestor to the checked-out merge result, including target changes since that
ancestor. Both PR parents must belong to that checkout. Missing revisions, empty
diffs and unexpected events select everything. NUL-delimited paths preserve
unusual filenames; disabled rename detection includes old and new paths. Selection
failure cannot pass the stable required aggregate check.

Four shared groups own repository, frontend, managed and database behavior checks.
Each integration group builds its own locked frontend assets, API checks,
MarketData checks and worker. No mutable application, database, market scenario
or previous result is shared as test evidence. Lightweight groups skip PostgreSQL
installation; other jobs preserve pinned verified package acquisition and caches.
The complete local gate retains every command in the same order.

Gas qualification runs in three independently prepared jobs: `gas-native` owns
the 24 native, populated-upgrade and retained-worker helpers; `gas-browser` owns
the nine application-browser invocations, including the separate provider-only
custody case; `gas` owns the seven preview workflows. This separates the queue
that reached the 90-minute job limit without removing checks or extending that
limit. The dynamic matrix and full-gate indicator include all three jobs; every
behavior command still runs exactly once across a complete matrix.

Local qualification covers disposable Git histories, union/unknown path selection,
merge-result and stale-checkout handling, CLI outputs, partition command coverage,
shared checks executing once, fresh-build prerequisites and aggregate failure states.
Workflow lint and deployment rendering are separate checks. Hosted qualification
must complete on the published commit before claiming the changed matrix passed.
