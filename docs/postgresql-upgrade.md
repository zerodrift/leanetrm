# PostgreSQL 18 to 19

D468 selects stock PostgreSQL 19.0 for new installations and verification.
The deployment image is pinned by digest in `deploy/compose.yaml`; CI's Ubuntu
packages and matching sources are pinned in `tools/ci/postgresql.lock.json`.
This document defines the separately admitted transition from an existing 18
database. An image replacement cannot upgrade its data directory.

## Historical continuity

Stock `pg_upgrade` changes the physical system identifier and does not preserve
native commit timestamps. Both were previously required directly by historical
read admission. Migration `0414_postgresql19_continuity.sql` makes the transition
explicit without changing financial facts, known times or retained job inputs.

For an existing catalog, `knowledge_clocks.system_identifier` and `timeline_id`
remain its original **source lineage**. The same values remain in risk jobs,
scheduled occurrences, assistant work and other retained owners. The lineage
is live only on its original physical endpoint or on the single successor named
by an immutable upgrade admission. Recovery mode, disabled commit tracking,
another system identifier or another timeline still refuse historical reads.
Catalogs created after the upgrade start with the new physical identity.

Keeping the original source tuple also keeps the deterministic context identity
consistent when an old owner and a fresh read select the same catalog, business
date and cutoff. An occurrence admitted before the upgrade can continue its
explicit child-read policy afterward. It receives no new permission or implicit
refresh of its old inputs. Ordinary authorized certification still decides
coverage, publication order and gaps; preparation does not advance those clocks.

## Private preparation and admission

The migration adds three private immutable stores: preparation attempts, exact
full-XID commit times belonging to an attempt, and one accepted destination.
Runtime and worker roles cannot prepare, admit or edit these records. The
installation owner must be a superuser, on the primary with commit tracking
enabled, using `READ COMMITTED` under an independently established writer fence.

`security.prepare_postgresql_upgrade(request_id, source_schema_digest,
expected_schema_digest)` runs on 18.
It retains each transaction in the reviewed publication/origin inventory from
native commit metadata or an existing exact immutable publication receipt.
Duplicate evidence must agree. Missing evidence refuses preparation; no caller
can provide replacement commit times. The inventory rejects unclassified XID
columns. The full 64-bit transaction identity is retained, including its epoch.

Preparation hashes every persistent user table's rows and every sequence's
`last_value` and `is_called`, with fixed formatting and bytewise ordering.
It excludes only the three upgrade-proof tables, whose selected commit inventory
has its own count and SHA-256. It also hashes non-system role attributes,
credentials, memberships and database authority/settings without returning that
material. Unsupported persistent relation kinds refuse.
Shared table locks protect the read; the operator fence must also prevent new
connections and independent sequence advances throughout preparation and shutdown.

The schema digests are **operator-supplied evidence**, not schema checks performed
inside SQL. Use the pinned PostgreSQL 19 `pg_dump --schema-only` against the
actual source, including ownership and privileges. Retain that dump and its
`tools/deploy_release.py:schema_fingerprint`. Restore those exact bytes with
`ON_ERROR_STOP` into an isolated, empty stock 19 database with explicitly matching
role names; dump that database for the expected 19 schema fingerprint. PostgreSQL
19 changes parsed-expression rendering, so raw 18 and 19 dump text can differ.
Let PostgreSQL canonicalize the source SQL; do not strip or rewrite constraints,
views or permissions to make a comparison pass. Measure the actual upgraded 19
schema and require exact equality with the canonical expected schema. The
normalizer removes only dump header versions and the random restriction key.
Supplying an expected digest in place of measuring the destination is not a
qualified admission. Real role authority is verified separately by the data
proof; the schema-only restore's role names are scaffolding for DDL validation.

`security.admit_postgresql_upgrade(request_id, data_digest, schema_digest)` runs
on 19 after a successful stock copy upgrade. It requires the selected source
proof, unchanged data and sequence state, matching measured schema, transferred
full-XID allocation/status evidence, and a different physical system identity.
It verifies the retained timestamp count and digest, then records the actual
destination identity and timeline. Missing, stale or altered proof refuses.
`checked_commit_time` can then use the admitted exact full-XID receipts; native
lookups continue to validate the full epoch before narrowing to PostgreSQL's
32-bit lookup. Neither route invents a time.

A request retry returns its original preparation or admission. If preparation
is abandoned and 18 resumes writing, retain that proof and use a new request ID
for a later preparation. The old proof cannot admit changed data. One accepted
successor is supported by this contract; another major upgrade or a failover
timeline needs a separately reviewed continuity policy.

## Operator sequence

1. Verify the exact source release, migration journal, database identity,
   dependency images, permissions and available storage. Qualify migration 0414
   and the full copy upgrade on isolated resources before touching production.
   Inventory the cluster's databases: the row proof covers the connected
   application database. Another database containing application data requires
   its own preservation proof or refusal before cutover; `pg_upgrade` affects
   the whole cluster.
2. Take and validate a recoverable backup. Fence public writes, stop collectors,
   schedulers and workers, and drain database clients. Preserve their prior
   enabled/disabled states. Keep the automatic release timer stopped.
3. Apply the compatibility migration on 18 and retain its checked journal entry.
   Measure the native and canonically restored schema with the pinned 19 client,
   prepare a request-keyed receipt,
   and shut down the source cleanly without allowing another writer.
4. Use stock `pg_upgrade` in **copy mode**, with the exact old/new binaries and
   separate data directories. Retain the old directory and backup. Complete
   required rebuild scripts before reading any affected relation; verify schema,
   rows, sequences, original owner inputs and the selected proof before admission.
5. Admit the measured destination, update the private physical deployment
   baseline to the actual new system identifier, and use the 19 Compose data
   directory. The catalog lineage identifiers remain unchanged. Analyze the
   upgraded database as required by PostgreSQL and qualify application startup,
   retained historical reads and each configured background consumer.
6. Restore the previously active consumers and ingress only after verification.
   Retain recovery artifacts privately. Once 19 accepts business writes,
   switching back to the old directory would discard those writes and is not
   an authorized automatic rollback.

The production baseline's physical identity remains an independent deployment
check. An application continuity receipt never substitutes for that gate,
release attestation, network fences, backups or installation authorization.

## Verification

`python3 tools/check_postgresql_upgrade.py` builds a disposable qualification
image from the exact stock 18.7 and 19.0 image digests. It uses one newly owned
container with no network or published ports, applies the migration journal,
loads synthetic data and performs a real copy upgrade. The recovery CI group
runs this regression. `--ssh-host` selects an explicit development Docker host;
the check still creates and removes only its own container and image.

The 2026-11-28 local regression passed with exact schema, authority, row,
sequence and commit-time preservation. It exercised a queued risk job's new
worker binding, scheduler recovery under a new fence, a close published by that
retained occurrence after the upgrade, historical close replay, a previously
uncertified listing, new writes and refused altered/incomplete proofs. Preparation
abort/resume and request retries also passed. These are synthetic correctness
checks, not a workload benchmark or a production recovery claim.

The ordinary verification matrix also runs against 19, including current
authorization, temporal, worker, numerical and browser checks. Successful fresh
schema checks alone do not qualify an existing installation's major upgrade.

PostgreSQL's [pg_upgrade documentation](https://www.postgresql.org/docs/19/pgupgrade.html)
defines the native upgrade and rebuild requirements. Its
[logical-replication upgrade notes](https://www.postgresql.org/docs/19/logical-replication-upgrade.html)
describe the loss of native commit timestamps during major upgrades.
