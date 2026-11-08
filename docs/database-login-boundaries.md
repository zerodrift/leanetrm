# Database login boundary profiles

API startup, the worker, private reference import and the administrator in-term gas load use
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
| GasInTermLoader | `openetrm_runtime`, `openetrm_gas_in_term_loader` | Yes | Yes |

The importer profiles retain their existing INHERIT requirement. The importer
and standalone scheduler do not independently require commit tracking; their
other execution prerequisites still apply. The runtime used by pilot close work
uses the same Runtime profile as the API and therefore requires commit tracking.
All six profiles also reject risk-worker and metadata-reader membership.
These profiles validate installation isolation, not permission to perform an
individual operation. Existing context binding, row-level security and command
permissions remain authoritative.

## Administrator in-term gas load

`OpenEtrm.Api --admin-load-gas-in-term CONFIG REQUEST` loads a gas composition
already in its delivery term (D359). It follows the reference-importer pattern
and has no HTTP route. The private configuration names the manifest, an
authenticator connection file and a loader connection file. These files and the
request must be private to their owner. The request is the ticket's composition
request, whose `snapshotId` names a recorded close, together with the
`administrator` subject who loads it. The authenticator issues that
administrator's context to the loader login, using the unchanged
`security.issue_context` and the manifest's issuer, as a human identity. The
command then runs the ticket's booking path: preparation, program staging, lock,
the load and publication. The trade and its revision record the administrator
as their actor. A retried request with the same operation id receives its first
outcome and adds no rows. An unknown or disabled administrator is refused with
`gas_in_term_identity`; one without the ticket booking's authority (`BookTrade`
with its reads) is refused with `gas_in_term_authority`. Neither refusal writes
anything.

`openetrm_gas_in_term_loader` is a NOLOGIN group role. Its only privilege is
EXECUTE on `security.load_gas_composition_in_term(uuid,jsonb,timestamptz)`.
`openetrm_runtime` does not hold that privilege. The loader login is a member of
both `openetrm_runtime` and the loader role, so it holds exactly runtime's
privileges plus the load. Like any runtime call, every read and write is bounded
by the bound context's authority. The Runtime profile rejects loader membership,
so the API's runtime login cannot load. A stricter split, in which the loader
holds no runtime privileges and runtime alone prepares and stages, remains later
hardening.

The consolidation intentionally strengthens prior startup checks: API runtime
and authenticator logins reject scheduler/collector membership; worker runtime
also rejects collector membership and requires commit tracking; the scheduler
rejects collector membership and ownership beyond its former four named schemas,
including schema and connected-database ownership. Importer profiles also reject risk-worker and metadata-reader membership.
Literal system-prefix matching no longer skips application names beginning with
`pg` followed by an arbitrary character.

`tools/check_database_login_boundaries.py --pg-bin PATH --dotnet PATH` creates
fresh local clusters with commit tracking on and off. It exercises positive
profiles (including GasInTermLoader), missing required membership, inherited conflicting memberships,
privilege flags, table/schema/database ownership and import inheritance. The
clusters are stopped and removed; existing databases are not used. Supply
`--no-build` only after building `tests/OpenEtrm.Api.Checks` from the same tree.
