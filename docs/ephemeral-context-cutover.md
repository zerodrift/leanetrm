# Runtime context cutover

Status: implemented with focused local qualification. Full CI and the hosted
reset/reseed remain release gates.

A financial context is a runtime value. It is constructed for an authorized
operation from an explicit business date or instant, an explicit knowledge policy,
certified fact history, exact selected inputs and numerical/model policy. It is
never a separately saved business object.

Remove the temporal, market, catalogue and trading context stores, their foreign
keys, saved-context lookup and retention operations, lifetime quotas and
compatibility readers. Keep the immutable publication and commit-receipt journals:
those certify when business facts became known and are not context stores.
Operation bindings exist only within their authorized transaction and must be
released before it commits. Browser references carry bounded, authenticated input
coordinates; they grant no authority and do not provide a database lookup key.

Each durable consumer owns the inputs needed to reproduce its work. Validation
facts own their validation date and knowledge cutoff. Results own their original
selected sources and calculation policies. A job occurrence resolves its declared
input-selection policy at first admission and owns that outcome. Every execution
attempt constructs a fresh runtime context from that occurrence's inputs. A retry
does not select a newer cutoff or different inputs. A later recurring occurrence
resolves its policy independently. Current authority and execution fences apply
to every attempt and publication.

Statistical clock evidence uses independent one-use issuer tokens scoped to the
exact runtime backend, transaction and evidence. Repeated admissions in one
transaction receive distinct tokens; identical evidence reuses its business
receipt. Consuming one token cannot block issuance of the next.

The cutover requires a fresh database and explicit reseeding. Installation over
populated saved-context state refuses rather than converting opaque identifiers,
inventing missing cutoffs or retaining an alternative storage form. Existing
private state can be backed up for operator recovery; it is not imported as live
executable state. This release uses the fresh-installation procedure below.

## Operator reset and reseed

Use the reviewed release's migration manifest and API/worker images together.
Stop `openetrm-deployment.timer` before publishing that release: a migration
baseline mismatch quarantines the candidate commit, even if the database is reset
afterward. Keep the previous operator baseline in the recovery set.
Automatic deployment intentionally refuses a changed migration baseline; this is
an operator schema window, not an automatic in-place upgrade.

1. Record the serving image digests, deployment/initialization state and hashes of
   the installed Compose and private configuration. Stop the deployment and
   collection timers, API and financial workers. Stop PostgreSQL cleanly. Archive
   the complete existing PostgreSQL directory and private application state and
   configuration together, readable only by the operator. Retain the original
   image and protection/credential keys with that recovery set. A logical dump
   alone cannot restore the original commit timestamps and database lineage.
2. Start the pinned stock PostgreSQL image with a new empty data directory. Keep
   the original directory intact and offline. Require checksums, UTC and
   `track_commit_timestamp=on`. Apply `db/bootstrap.sql` and the exact ordered
   migration files listed in the reviewed release, with `ON_ERROR_STOP=1`.
3. Run `tools/provision_hosted.py` into a new private directory, supplying the
   exact HTTPS origin, a new installation UUID and the qualified tzdb release.
   Supply explicit clock coverage and the matching managed renderer when clocks
   or collection are selected. Supply a reviewed private collection contract only
   when collection is intended. Inspect and apply its `seed.sql`; use its new
   `manifest.json` and `installation.json`. The seed refuses an existing tenant or
   catalog and creates no trades or observations. Populate the intended catalogs
   and synthetic testing examples through their current native business operations.
4. Provision the separate authenticator, runtime, collector and worker logins
   required by the selected installation. Store their connection files outside
   the repository. Install the new credential keys from the provisioning output
   and a new installation protection-key set; point `host.json` at the new
   manifest, connection files and keys. Preserve the reviewed gateway, process
   limits, TLS and network fences. Reprovision the owner using the new private
   single-use invitation, then explicitly recreate other memberships, roles,
   agent/provider settings and schedules. Re-enter provider credentials through
   Administration. Do not copy old turns, jobs, saved reports, context rows,
   authorization tokens or serialized financial selections into the new database.
5. Check the new database's system identifier and migration hashes, absence of
   all four retired stores and lookup APIs, and empty financial fields on every
   committed request row. Qualify the candidate with the isolated hosted and
   runtime-context checks before switching the serving API and matching workers.
   Promote the initialization manifest's ordered migration inventory and database
   system identifier together with the operator settings' `schemaSha256` and
   `hostConfigurationSha256` used by `tools/deploy_release.py`. Start the API,
   confirm authenticated startup and
   intended seed contents, then restore the explicitly selected workers and
   timers. A temporary limit-removal change to the old database is superseded by
   this installation; it is not replayed into the new one.

Rollback restores the archived PostgreSQL directory, matching private state,
configuration and exact earlier images as one set while all application writers
are stopped. It does not logically restore old facts into the new lineage or run
an older API against the new schema. Keep the failed candidate offline for
operator diagnosis and restore the deployment baseline before enabling timers.

Qualification must prove absence of the retired tables, foreign keys and active
lookup APIs; repeated reads without durable context growth; exact historical and
parent-ceiling behavior; current authorization after waits; unchanged job results
after worker replacement; original inputs on retry; and new input selection for a
later occurrence. Pre-bind worker permission qualification must use job-owned
inputs and cannot treat an absent operation binding as an empty portfolio.

## Local qualification

The disposable checks use stock PostgreSQL 18.6 with commit tracking enabled,
the pinned .NET 10 SDK and synthetic inputs. They cover:

- A fresh migration chain, atomic refusal of populated predecessor bindings and
  changed consumer definitions/privileges, and absence of all four stores,
  retired read views and the ID-only clock-capture operation.
- 4,105 distinct reads across 33 bounded transactions, released financial request
  fields, exact historical replay, parent ceilings, clock lineage, altered proofs
  and cross-tab refusal. Six real-browser checks cover startup, refresh, details
  and document reload.
- Separate PPA risk workers, original-request replay after tab reset, replacement
  workers and exact existing-close publication. Generated closes retain their
  first admission through a killed worker; later recurring occurrences select a
  newer cutoff independently.
- Date mapping and curve construction, draft revalidation, immutable publication,
  fresh-tab materialization replay and current source revocation. Eighty assistant
  integration checks include nine independent report reads in one turn. Nine
  internal rate-tenor loads consume no browser proof slots.
- Statistical risk with independent numerical oracles, exact worker replay,
  missing-source refusal and current authorization. Clock custody uses the real
  separate-connection issuer protocol, including rollback, changed evidence,
  wrong transaction and expiry refusals, and repeated captures in one saving
  transaction with distinct one-use tokens and one business receipt.

The frontend suite passes 2,264 checks and the verification-runner suite passes
176 checks. This evidence qualifies the exercised synthetic paths; it does not
activate a hosted installation or replace the full CI matrix.
