# Database login boundary profiles

API startup, the worker and private reference import use
`OpenEtrm.Api.DatabaseLoginBoundary`. This database infrastructure owner evaluates
one common isolation predicate with an explicit profile. PostgreSQL role names
identify installation roles; they are not substitutes for business reference IDs.

Every profile rejects superuser, RLS bypass, database/role creation privileges,
security-owner membership, metadata-reader owner membership, and membership in another known service role outside
its required set. Each also rejects direct or inherited ownership of application
relations, non-system schemas, or the connected database. System namespaces start
with the literal `pg_` prefix; an application namespace such as `pgx_operations`
is not excluded from the ownership check.

| Profile | Required role membership | Require INHERIT | Require commit tracking |
| --- | --- | --- | --- |
| Runtime | `openetrm_runtime` | No | Yes |
| Authenticator | `openetrm_authenticator` | No | Yes |
| Scheduler | `openetrm_scheduler` | No | No |
| ReferenceImporter | `openetrm_runtime`, `openetrm_reference_importer` | Yes | No |
| ImportAuthenticator | `openetrm_authenticator` | Yes | No |

The importer profiles retain their existing INHERIT requirement. The importer
and standalone scheduler do not independently require commit tracking; their
other execution prerequisites still apply. The runtime used by pilot close work
uses the same Runtime profile as the API and therefore requires commit tracking.
All five profiles also reject risk-worker and metadata-reader membership.
These profiles validate installation isolation, not permission to perform an
individual operation. Existing context binding, row-level security and command
permissions remain authoritative.

The consolidation intentionally strengthens prior startup checks: API runtime
and authenticator logins reject scheduler/collector membership; worker runtime
also rejects collector membership and requires commit tracking; the scheduler
rejects collector membership and ownership beyond its former four named schemas,
including schema and connected-database ownership. Importer profiles also reject risk-worker and metadata-reader membership.
Literal system-prefix matching no longer skips application names beginning with
`pg` followed by an arbitrary character.

`tools/check_database_login_boundaries.py --pg-bin PATH --dotnet PATH` creates
fresh local clusters with commit tracking on and off. It exercises positive
profiles, missing required membership, inherited conflicting memberships,
privilege flags, table/schema/database ownership and import inheritance. The
clusters are stopped and removed; existing databases are not used. Supply
`--no-build` only after building `tests/OpenEtrm.Api.Checks` from the same tree.
