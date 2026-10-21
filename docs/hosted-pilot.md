# Private pilot operations

The hosted mode serves the same business operations as the isolated local preview,
with authenticated human subjects instead of selectable preview personas. It is a
bounded private pilot, not a production qualification or complete futures lifecycle.

## Release and initialization

Build the locked managed API and browser assets from the reviewed tree. Package
only published assemblies and built web assets. `deploy/Dockerfile` uses a pinned
public .NET runtime image; `deploy/compose.yaml` uses a pinned stock PostgreSQL
image with checksums, SCRAM credentials, UTC and commit timestamp tracking enabled.
The database and collector publish no host ports. The API publishes one listener
bound only to an explicitly supplied private gateway address. Its host ingress
filter admits only the configured gateway peer before Docker forwarding; the
collector uses a separate outbound network. No Swarm scheduling state is required.

Prepare a fresh installation with `tools/provision_hosted.py`, supplying the exact
HTTPS origin, installation UUID, qualified container tzdb release and a new private
output directory. An optional private collection contract supplies exact source
memberships, units, clocks, request rates and explicit job switches. No sources,
credentials or real prices are included in the public repository. The prepared
SQL refuses an existing tenant/catalog and creates no trades or observations.
Inspect the prepared seed before applying migrations and seed to the new database.
Keep the single-use owner invitation private; it expires after 24 hours. Its claimant
chooses their login, display name and password. A GET does not consume an invitation.

Provision distinct non-owner runtime/authenticator logins and a collector login
that belongs only to `openetrm_collector`. Connection strings, the installation
certificate for encrypting data-protection keys, provider credentials and manifests
live outside web assets and source control. Mount private files read-only; the API
may write only its retained state directory. Preserve installation identity and
protection keys across releases. User disablement and credential changes revoke
sessions; re-enabling an account does not restore its old cookies. Account creation
and password-reset invitations are initially operator-provisioned; hosted persona
switching and the preview account-create operation are disabled.

Run owned synthetic HTTP/browser checks and the full verification runner before
release. On the deployment host, run `python3 tools/check_deployment.py` to validate
the rendered Compose mounts and network boundaries without starting containers. Review the rendered gateway configuration, exact private upstream, image
identity, database role ownership and exact trusted proxy network. Test nginx syntax
before a graceful reload. The gateway overwrites forwarding headers; only the
explicit trusted proxy network may supply them. Proxy-network membership is not an
authenticated application identity. Route an exact hostname and maintain a valid
certificate plus a renewal reload hook. Do not expose the synthetic persona mode.

## Public homepage and workspace routing

The open-source homepage is the buildless content in [site](../site/README.md).
Its three deployable files are separate from the authenticated application's
browser assets. The gateway template `deploy/nginx.conf.template` requires seven
explicit substitutions: `PUBLIC_HOST`, `CERTIFICATE_CHAIN`, `CERTIFICATE_KEY`,
`UPSTREAM_ADDRESS`, `UPSTREAM_PORT`, `LANDING_ROOT` and `LANDING_HEADERS`.
`LANDING_ROOT` names the installed three-file public directory and
`LANDING_HEADERS` names the installed `site/headers.inc`. Substitute only these
placeholders; preserve nginx's own `$request_uri`, `$is_args`, `$args`,
`$remote_addr` and `$openetrm_backend` variables. Keep rendered configuration,
certificate keys and installation addresses outside source control.

Only `/`, `/_landing/site.css` and `/_landing/favicon.svg` serve public files,
with GET/HEAD, restrictive public-content headers and no JavaScript. Unknown
paths under `/_landing/` return 404. `/app` proxies to the application's existing
root, preserving the raw query; `/app/` redirects to `/app`. Successful login's
upstream `Location: /` becomes `/app` only on the exact `/auth/login` endpoint.
Failed login and other upstream redirects retain their original destinations.
All remaining application assets, authentication and API routes use the existing
upstream, including its account checks and overwritten trusted forwarding headers.
No application authentication or business operation moves into the public page.
API routes keep the API's request body limits, 8 MiB for `/api/interval-series/revise`
and 32 KiB otherwise, and the gateway answers a larger API body with 413 and the API's
`request_too_large` code and message, so a page can tell that nothing was written.
Every other route keeps the 32 KiB limit and nginx's own answer. The gateway buffers a
revise body of up to 8 MiB before the application authenticates its request, so
anonymous uploads of that size reach nginx's client body temporary files; allow for
them there.

Run the isolated gateway qualification explicitly with an installed stock nginx
that supports TLS and HTTP/2, plus OpenSSL:

```sh
python3 tools/check_public_routes.py --nginx /path/to/nginx
```

It starts only an owned temporary nginx process and a synthetic loopback
application, creates disposable certificates, and checks public routes, private
routes, queries, redirects, proxy headers and API body limits. Its requests use
HTTP/1.1, so it does not exercise HTTP/2. It does not connect to an existing
application or deployment. Standard unit discovery checks source content and
placeholder rendering without requiring nginx; passing those checks alone does not
qualify gateway routing. The owned check does not establish real hosted
authentication, account isolation, certificate renewal or production readiness.

Install the reviewed public files and header include read-only, review the exact
rendered template, and qualify nginx syntax plus real hosted authentication before
an explicitly authorized activation. Automatic application-image distribution
retains its existing strict release envelope and does not install or activate
the public site or gateway configuration. A gateway rendered from an earlier template
refuses every body over 32 KiB with nginx's own 413, so it needs this review and
activation before the revise route accepts larger bodies and pages can read the refusal.

## Delivery prices and scheduling

Supported feeds are PJM `da_hrl_lmps` / `rt_hrl_lmps` at 60 minutes and ERCOT
`np4-190-cd/dam_stlmnt_pnt_prices` at 60 minutes /
`np6-905-cd/spp_node_zone_hub` at 15 minutes. ERCOT RT values are settlement-point
prices, not raw SCED LMP; energy-weighted load-zone records are excluded explicitly.
Prices remain typed exact USD/MWh values, including zero and negative prices.
HTTP gzip, zlib-wrapped deflate and Brotli pages are decoded with separate 16 MB
encoded and decoded limits; unsupported stacked encodings are rejected.
Response schemas, pagination, node identity, native intervals, complete coverage
and repeated-hour flags are validated before publication. A failed source prevents
the entire job batch from succeeding. Secrets and response bodies are not logged.

The collector cannot author its market calendar. Prepare its exact-version UTC
operating-day boundaries using the managed API executable's
`--render-power-days REQUEST.json OUTPUT.sql` mode, with an explicit supported date
range, retained clock rules and the matching installed tzdb. Apply the reviewed SQL
as operator. Missing day coverage blocks collection; it never invents 24 hours.
A changed clock contract requires a new reference revision and calendar publication.

The managed `--collect-power scheduled` command takes private configuration through
`OPENETRM_COLLECTION_CONFIG`. The supplied systemd timer wakes it at 08:00 UTC;
per-job `schedule_enabled` remains authoritative. With both switches paused, the
timer performs no provider calls. `--collect-power now PJM` or `now ERCOT` is an
explicit operator run and does not enable a schedule. Trailing-day offsets and UTC
schedule semantics are retained in each job. The baseline attempts each scheduled
job at most once per UTC date; there is no automatic failure retry or missed-day
backfill. An operator may explicitly run again. A session advisory lock prevents
overlap; lost workers cannot publish through another connection. Interrupted
attempts are reconciled on the next admitted run. Current service/reference scopes,
job generation and authorization generation fence publication, including revoke
and restore. Provider request slots persist across worker invocations.

The Delivery prices screen exposes current source configuration, paused/scheduled
state, recent outcomes and explicit operating-date history queries. Interval times
use the human's display zone. History formatting preserves each exact observed
price instead of imposing a fixed decimal count. Delivery-day bounds use the
source's declared interval and exact reference clock; source units, currency,
commodity and location come from exact-version reference joins.
Each history read retains an exact knowledge cutoff
and certified context. Full historical workspace reference admission is not yet
supported for this separate workflow; a bound historical workspace context is
rejected. Imported delivery prices are not automatically forward marks or valuation
fixings. Future fixing consumers must explicitly bind their source and cutoff.

Real-data usage and redistribution rights remain installation responsibilities;
observations and private feed mappings must never enter public fixtures or exports.
Operational backup/restore, account invitation administration, wider monitoring,
provider outages and longer-run recovery still require operator qualification.

## Automated release distribution

The publisher job runs after successful CI for a push to the protected `master`
branch, only when `RELEASE_PUBLICATION_ENABLED` is explicitly enabled. The
`hosted-release` GitHub environment restricts publishing to that branch. It builds
the API/browser/worker image with pinned public dependencies and preserves the project,
Npgsql and browser dependency notices. A data-only GitHub Release contains the
Docker save archive, exact migration hashes and Compose evidence. The complete
bundle has a GitHub build-provenance attestation; private configuration, credentials,
account keys, installation SQL and observations never enter it.

An installed root-owned operator polls public GitHub endpoints every five minutes.
It needs no GitHub token, inbound webhook or Actions runner on the application host.
It admits only the current branch head with a successful push CI attempt, and
verifies the attestation's repository, workflow, source ref, exact commit and run
attempt using a separately installed, digest-pinned GitHub CLI. Source head is
checked again immediately before promotion. The installed operator and Compose
are never replaced by a downloaded release.

The current deployment contract permits application-image updates only. Exact
migration inventory, actual database schema fingerprint, database identity, host
configuration, interpolated private binding and live pre-DNAT fence must match the
operator's reviewed baseline. New migrations or changed infrastructure stop for
operator qualification; the updater never reruns installation seeds or changes
collector job switches. The collector and updater share an operation lock.

A head whose release carries migrations missing from that baseline fails the check
on every attempt, so the operator quarantines it on the first poll that finds its
release and never retries it. To deploy such a head, stop `openetrm-deployment.timer`
before its release is published, then qualify and apply the schema upgrade and
promote the reviewed baseline while the timer is stopped. The operator also requires
current public readiness, so restart the timer only once the current application is
serving again; its next poll then admits that head. A head quarantined before its
schema upgrade can deploy only through a newer head with the same migrations.

Before promotion, a disposable candidate uses the same private configuration and
database with a separate explicit loopback proxy admission. Anonymous sign-in and
protected-API checks perform no booking or account claim. A failed candidate leaves
the serving image unchanged and is quarantined. Temporary download/infrastructure
failures retry with bounded backoff. Only recorded, verified release images are
eligible for scoped cleanup: active and previous images remain, in-use images are
preserved, and blocked retention prevents further image accumulation. No broad
Docker prune is used.

Host egress policy must allow Docker's root-owned loopback proxy to reach TCP 8080
on the installation's ingress bridge. Scope any required host-only allowance to
that exact bridge, destination subnet, owner and port. Retire temporary allowances
for disposable qualification networks with those owned resources.

Environment changes and deployment phases are written atomically. Interruption
or failure around promotion blocks further automatic switches. No automatic
application or database rollback is attempted after financial writes could have
occurred. After diagnosing the actual serving image and health, an operator can
explicitly reconcile it with:

```sh
sudo python3 /opt/openetrm/operations/deploy_release.py \
  --config /etc/openetrm/deployment-operator.json --recover
```

Recovery reuses promotion's serving-container and environment checks before
recording success. A changed user, gateway binding, network, private mount or
installation setting leaves the interrupted deployment unreconciled.

Inspect the private state receipt and systemd journal to distinguish waiting for
CI, deferred collection, quarantine, retry backoff and operator recovery. Private
operator settings and schema/ingress baselines are installation artifacts, not
repository fixtures. Qualify the real attestation verifier and the full Docker /
stock PostgreSQL deployment path on disposable synthetic resources before enabling
the polling timer. A workflow file alone does not prove an end-to-end release.
