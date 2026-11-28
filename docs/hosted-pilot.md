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
output directory. To publish the supplied UTC, PJM and ERCOT clocks, also supply
`--clock-from` and exclusive `--clock-to` dates, the qualified `--dotnet` runtime
and `--api` assembly. The managed renderer verifies the named source release and
captures that explicit coverage before the reference revision is sealed. Without
that selection, the initial units-only revision contains no clocks. A collection
installation requires clock publication. An optional private collection contract supplies exact source
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

### Regional closes (D334, migration 0202)

Migration 0376 instead requires the complete
[runtime-context reset and reseed](ephemeral-context-cutover.md#operator-reset-and-reseed).
The older regional-close cleanup below is not a substitute for that cutover.

Migration 0202 refuses to run while any market close or pilot close profile
exists: existing closes are retired, not mapped. Before the release, stop the
deploy timer, clear the pilot's market closes and every record that refers to
them (pins, members, receipts, close-risk and pilot profiles, runs and reports),
apply the migration, then reseed: publish the deployment's close sessions, grant
each recorder their sessions, record the seeded closes through the new rules and
record each person's session choice.

## Page assistant

The page assistant runs under `--hosted-pilot` ([ai-assistant.md](ai-assistant.md) §11,
D192). Its release adds migration 0087, so it ships in an operator schema window (D135),
which also makes these installation changes:

1. **Credential key files.** As root on the host, run
   `python3 tools/provision_hosted.py --credential-keys <PRIVATE_ROOT>/application --owner 1654:1654`.
   It writes `credential.key` and `credential-retry.key`, 32 random bytes each, mode
   0600 and owned by the account the API runs as. Each appears only once complete, it
   refuses either that exists, and it shows no key. A new installation's provisioning
   output already holds both. Back them up with the installation's other private
   files: losing them means entering every vendor key again.
2. **Connection limit.** The API's pools hold at most 46 database connections, so
   `deploy/compose.yaml` allows the database 80. Install it, record its hash
   (`composeSha256`) and recreate the database container once.
3. **Egress.** Let the API container open outbound TCP 443 and DNS, and nothing else.
   With nftables, a forward chain on the bridge Docker created for the project's
   `web_ingress` network does it. The bridge is named `br-` and the first 12 characters
   of the network's id:

   ```nft
   table inet openetrm_egress {
     chain api_egress {
       type filter hook forward priority -200; policy accept;
       iifname "<bridge>" oifname != "<bridge>" ct state established,related counter accept
       iifname "<bridge>" oifname != "<bridge>" meta l4proto { tcp, udp } th dport 53 counter accept
       iifname "<bridge>" oifname != "<bridge>" tcp dport 443 counter accept
       iifname "<bridge>" oifname != "<bridge>" counter drop
     }
   }
   ```

   Persist it like the ingress fence. Record it in the deployment settings as
   `egressGuard`: its file and that file's hash, the bridge, and its rules as
   `nft list chain inet openetrm_egress api_egress` prints them, counters cleared. Every
   deployment then refuses to proceed if the file or a live rule changed, or if the
   network was recreated with another bridge.
   Confirm from a disposable container on that network that HTTPS to the vendor's
   endpoint connects while another port does not.
4. **Host configuration, last.** The previous image refuses a `host.json` naming the key
   files, so add `"credentialKeyFile": "/run/private/credential.key"` and
   `"credentialRetryKeyFile": "/run/private/credential-retry.key"` only after the
   previous API has started for the last time, just before the head deploys. Record the
   reviewed hash (`hostConfigurationSha256`).
   - The new image refuses to start without the two fields and names them.
   - Keep the previous `host.json` and its hash: rolling back to the previous image means
     restoring both first.

One serving API process holds the agent worker. A deploy's disposable candidate refuses
agent work while it does, and replacing the serving process interrupts its unfinished
turns at the next start. The gateway needs no change: it already passes event streams
through unbuffered, with a read timeout above the stream's heartbeat.

Before anyone else gets the launcher, the installation's owner qualifies a vendor live,
entering their own key in Administration › AI so that no one else handles it:
- records the vendor's terms and attests;
- adds the model with its capabilities and the price they enter, and runs Test;
- asks a scripted question from the panel that reads and navigates.

Anything the live API sends that the adapter refuses is fixed under a recorded check
first. Conversations are kept until an installation needs a retention policy (O11).

## Public homepage and workspace routing

The repository's default public page is the buildless content in [site](../site/README.md);
an installation may install its own page, stylesheet and icon in its place (D385).
Its three deployable files are separate from the authenticated application's
browser assets. The gateway template `deploy/nginx.conf.template` requires seven
explicit substitutions: `PUBLIC_HOST`, `CERTIFICATE_CHAIN`, `CERTIFICATE_KEY`,
`UPSTREAM_ADDRESS`, `UPSTREAM_PORT`, `LANDING_ROOT` and `LANDING_HEADERS`.
`LANDING_ROOT` names the installed public directory and
`LANDING_HEADERS` names the installed `site/headers.inc`. Render these inputs
through `tools/gateway_configuration.py` with the selected host limits. It also
writes the derived size/deadline placeholders and preserves nginx's own `$request_uri`, `$uri`,
`$request_filename`, `$document_root`, `$is_args`, `$args`, `$remote_addr` and `$openetrm_backend` variables. Keep rendered configuration,
certificate keys and installation addresses outside source control.

Only `/`, `/_landing/site.css`, `/_landing/favicon.svg` and the bounded images
folder `/_landing/images/` serve public files, with GET/HEAD, restrictive
public-content headers and no JavaScript (D382, D388). An image name is one
lowercase segment with a listed extension, and only a regular file is served:
folders answer 404 and symbolic links inside the folder 403. Other methods are
refused with 403 whatever the name. Images are operator content installed under
`LANDING_ROOT`; they are not in the repository. A request for an absent image
writes nothing to the gateway's log, and error answers carry the same
five-minute public cache period as successful ones, so install an image before
the page names it. Unknown paths under `/_landing/` return 404.

The gateway serves no films. A page shows a film as a poster image from the
images folder that links to the film on an outside video host; the page plays
nothing itself (D388). `/app` proxies to the application's existing
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
range and exact `clockCodes` in the selected reference revision. The generated
SQL calls the operator-only idempotent publisher; boundaries and fingerprints
come from that revision's published clock capture. Prepare the capture with
`--render-clock-captures` under its explicitly named source release and coverage
before sealing the reference. Apply reviewed SQL as operator. Missing coverage
or a mismatched retained day blocks collection; it never invents 24 hours.
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

## Blotter browser maintenance (D455)

The Blotter chart identity repair may be backported to an installed release
without adopting intervening schema changes. This is a bounded, operator-only
exception to D135 for that presentation repair, not a general maintenance branch
or an automatic updater capability. The repaired source must also be reviewed
and merged into the protected branch.

Before promotion, verify the original release attestation and its image digest,
and require successful full CI on the exact published backport commit. Build
clean and repaired browser assets with the same pinned Node runtime and locked
dependencies. The clean output must match the installed assets byte for byte;
the repaired delta may contain only the desk module, affected entry bundles and
their HTML cache hashes. Retain dependency notices unchanged.

Derive one immutable image from that verified base. Compare its complete
filesystem, file modes and container configuration with the base: only the
reviewed browser files and explicit provenance labels may differ. Record source,
base image, toolchain, asset and resulting image hashes in a private receipt.
The resulting image is operator-qualified and **not GitHub-attested**; preserve
the base attestation separately and do not manufacture an automated release run.
Independent adversarial review must cover the artifact and promotion procedure.

Qualification uses disposable resources. Do not attach a test candidate to the
production database: startup can alter runtime coordination state. Promotion
holds the installed operation lock, rechecks its schema, private configuration,
binding and ingress baselines, pauses new work and drains active operations.
Switch the API, risk worker and scheduled workers to the same immutable image,
then verify readiness before restoring admissions. Preserve the previous image
and configuration with explicit recovery instructions; interruption requires
operator reconciliation. No schema or business-data migration is authorized.

Keep the automatic updater stopped. Its receipts must distinguish this manual
update from attested releases, and a later compatible release requires explicit
baseline and serving-image reconciliation before the updater is restarted.

## Physical directory maintenance (D456)

The Physical Operations directory selection repair may be backported onto the
installed D455 image. Require its corresponding protected-branch merge, full CI
on the exact published backport, and independent review before promotion. This
is a separate, bounded backend exception; it does not widen D455's browser scope.

Verify the original release attestation, installed D455 filesystem proof and
operator receipt chain. With the pinned SDK and locked dependencies, reproduce
the original API assembly and PDB bytes, including their source and build-path
metadata. Build the corrected assembly with the exact backport source identity.
The only executable filesystem changes may be `OpenEtrm.Api.dll` and its matching
`OpenEtrm.Api.pdb` in both the API and worker directories. The worker references
the API project; leaving its assembly unchanged would split the deployed code.
Compare all other files and container configuration with the installed image,
including the preceding browser repair, dependency notices, dependency manifests
and runtime configuration. Only `org.opencontainers.image.revision`,
`io.openetrm.maintenance.base`, `io.openetrm.maintenance.contract` and
`io.openetrm.maintenance.provenance` image labels may change. Retain the build
commands, toolchain hashes and complete comparison in private operator evidence.

Qualify the corrected application with disposable synthetic resources. Retain
its separate operator provenance and apply the D455 operation lock, baseline,
ingress, drain, complete worker cohort and recovery requirements. No candidate
runs against the production database before the guarded promotion. The previous
image and manual receipt remain available; the automatic updater stays stopped
until a compatible attested release is explicitly reconciled.

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

The release envelope permits at most 512 regular files, including its manifest,
image, Compose file, license and migration inventory. An installed release reader
older than this limit refuses a release above 256 regular files until the matching
reader is installed in the operator window. Byte, path, type and hash checks apply
independently; the inventory limit does not authorize schema changes.

The current deployment contract permits application-image updates only. Exact
migration inventory, actual database schema fingerprint, database identity, host
configuration, interpolated private binding and live pre-DNAT fence must match the
operator's reviewed baseline. New migrations or changed infrastructure stop for
operator qualification; the updater never reruns installation seeds or changes
collector job switches. The collector and updater share an operation lock.
The schema fingerprint is the SHA-256 of the schema inside the database's
`pg_dump --schema-only` output: everything after the header and before the
closing `\unrestrict` line. The opening banner, the random `\restrict` key and
the two header lines that name the server and `pg_dump` versions are left out
(D465). A dump in any other form is refused. A `schemaSha256` recorded by an
earlier `deploy_release.py` matches nothing under this one. With the timer
stopped, record it again when installing this deployer. `DATABASE_CONTAINER` is
the value in `/etc/openetrm/compose.env`, and the last argument is the
directory that holds the deployer, which the invoking user must be able to
read:

```sh
sudo docker exec "$DATABASE_CONTAINER" sh -c 'IFS= read -r PGPASSWORD < /run/secrets/database-owner; export PGPASSWORD; exec pg_dump -h 127.0.0.1 -U openetrm_owner -d openetrm --schema-only' \
  | python3 -c 'import sys; sys.path.insert(0, sys.argv[1]); from deploy_release import schema_fingerprint; print(schema_fingerprint(sys.stdin.buffer.read()))' /opt/openetrm/operations
```

Until it is recorded, a poll quarantines the head it tries and `--recover`
refuses.
The connected-curve migrations 0080/0081 also require the
[cutover prerequisites](connected-calculation.md#adopting-the-schema-cutover);
their empty-record guards are not an automatic data conversion or reset.

A head whose release carries migrations missing from that baseline fails the check
on every attempt, so the operator quarantines it on the first poll that finds its
release and never retries it. To deploy such a head, stop `openetrm-deployment.timer`
before its release is published, then qualify and apply the schema upgrade and
promote the reviewed baseline while the timer is stopped. The operator also requires
current public readiness, so restart the timer only once the current application is
serving again; its next poll then admits that head. A head quarantined before its
schema upgrade can deploy only through a newer head with the same migrations.

### Retained gas risk schema release

Migration 0165 enrolls the fixed contractual gas profiles in the common portfolio
risk path. It adds read-only worker policies with the runtime history and current
authority predicates plus an active-lease fence, and makes the held-position
inventory refuse incomplete source metadata. It changes no booked economics or
financial records.

Qualify this migration against an isolated restored backup before the schema
window, checking the complete original row sets and the resulting schema
fingerprint. Keep the updater stopped while the release inventory differs from
the installed baseline. During cutover, fence public mutations, drain and stop
the API and background writers, and retain a verified final backup before applying
the exact qualified migration. Promote the migration inventory and schema
fingerprint together only after the installed result matches the rehearsal.

The API, retained-risk worker and scheduled calculation workers must use the
same attested release image. Validate the candidate before switching, verify
worker replacement and public readiness, then reopen writes and restore the
updater. Preserve the quarantine list; a head already quarantined for the missing
migration requires a newer verified release rather than an edited deployment
receipt. Existing retained jobs keep their admitted engine fingerprints and
must not be rewritten to match a new binary.

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
installation setting leaves the interrupted deployment unreconciled. Recording the
new image as active also requires that the API runs under the installed Compose
file's resource limits (D458); reconciling back to the previous image does not,
since that container may run under earlier ones.

Inspect the private state receipt and systemd journal to distinguish waiting for
CI, deferred collection, quarantine, retry backoff and operator recovery. Private
operator settings and schema/ingress baselines are installation artifacts, not
repository fixtures. Qualify the real attestation verifier and the full Docker /
stock PostgreSQL deployment path on disposable synthetic resources before enabling
the polling timer. A workflow file alone does not prove an end-to-end release.


## Optional retained-risk workers

The [risk-worker contract](risk-engine.md) and
[Swarm template](../deploy/risk-workers.swarm.yaml) add independently replicated
workers for retained interactive portfolio requests. Provision each dedicated login,
service scope, external secrets and private network explicitly after the schema
release. Workers require the API's exact managed engine fingerprint. The existing
Compose baseline, automation controller and generated-close adapters retain their
separate responsibilities. No live deployment or multi-node capacity qualification
is established by this template.


## Operating configuration transition (D302)

Migrations 0181–0183 require an operator-qualified schema window; automatic
updates must remain stopped at the migration/configuration gate. No existing
installation or private configuration is modified by these sources.

1. Preserve the exact current image, `host.json`, protected connection files,
   rendered gateway and their hashes. Qualify the target with isolated synthetic
   resources before selecting operating values. Review every field of
   `deploy/process-limits.example.json`; it is an executable synthetic baseline,
   not an instruction to overwrite an installation's policy.
2. State all selected process values in the required `limits` section of the
   target's private `host.json`. State `Timeout`, `Command Timeout` and
   `Maximum Pool Size` explicitly in each protected connection file. The API
   accepts connect ≤5 s, command ≤30 s and pool ≤8; worker files accept ≤5 s,
   ≤5 s and ≤2 respectively. Process values must stay within the qualified bounds.
3. Apply the reviewed schema and target binary together. Existing workspaces get
   explicit carried-forward operating baselines; fresh provisioning calls
   `security.initialize_operating_policies` before issuing its invitation.
   Missing policy refuses. A new API process fences/interrupts its predecessor's
   work through the existing worker-generation contract. D302 introduced
   `turns_v12` admission; subsequent reviewed migrations advance the required
   runtime to [the current workspace contract](assistant-workspace.md),
   `turns_v20`. Old request receipts remain replayable.
4. Render a private gateway file using the same target `host.json` and a private
   JSON file containing the seven operator placeholders:
   `python3 tools/gateway_configuration.py --host /private/host.json --values /private/gateway-values.json --output /private/nginx.candidate.conf`.
   Validate with the target nginx before installation. The renderer derives body
   bytes, refusal messages and a read timeout above the extended API deadline,
   with a fixed 20-second minimum for the existing stream heartbeat protocol.
   Review the installed configuration hash and deployment baseline explicitly.
5. Rollback is a paired image/configuration transition. Earlier binaries reject
   the new `limits` member, and the earlier invitation claim/reset and assistant
   admission contracts are intentionally closed by these migrations. Restore
   that image's exact host configuration before restarting it; do not claim full
   recovery from an authentication-page smoke test. Authenticated financial reads
   and required account workflows must be separately qualified for the selected
   prior image/schema, or keep the target stopped until a compatible repair.

`tools/check_release_docker.py` requires `--previous-process-limits absent|named`
in addition to the existing prior credential-key contract. It preserves the prior
host file and tests restoring that file with the prior image. That rollback smoke
checks authentication-page availability and anonymous denial only; it does not
qualify prior-image account claims/resets, assistant use or financial reads. The
attested prior-image Docker run remains a release prerequisite for this increment.

## Hosted capacity (D458)

D458 raises the API container's memory limit in `deploy/compose.yaml` above a
demo dataset's measured peak, and the ceilings of the request time limits. The
demo's whole load peaked at 2,231 MiB resident in the API, against its
3,072 MiB container. Its risk worker peaked at 1,003 MiB, measured with the
runtime's own settings on six processors. The hosted demo's risk worker runs
under a host-level unit outside this repository, with 1 GB of memory and one
processor, so that peak is at its limit; raising it is an operator step on the
host, and D458 sets no figure for it. The change is infrastructure: nothing takes
effect until the operator applies it, in this order. Each step names the check
its safety depends on and the way back.

Before starting:

- **Will the updater deploy this head?** A poll deploys a head only when its
  release's migration inventory and Compose hash equal the installed baseline
  (`migrations`, `composeSha256`), the installed Compose file and `host.json` match
  their recorded hashes and the database's schema fingerprint matches. Any other
  head fails a check and is quarantined on the first poll that finds its release.
  An installation whose schema is behind the release, such as one serving a D455
  or D456 maintenance image, goes through its schema window first, as for the
  [retained gas risk schema release](#retained-gas-risk-schema-release). These
  steps do not replace it.
- **Host memory.** While a candidate runs beside the serving API, the host needs
  memory for twice the API's limit plus the database's and the collector's, and
  the risk worker's unit as the operator sets it.
- **Command timeouts.** Compare `Command Timeout` in the API's runtime and
  authenticator connection files with the limits to be selected. Sign-out waits
  behind a slow request, and a shorter command timeout answers it with an error.

1. **Stop `openetrm-deployment.timer`** before the release carrying D458 is
   published, and keep it stopped until step 5. Then check that no poll is running
   (`systemctl is-active openetrm-deployment.service` shows `inactive`, not
   `activating`) and that the deployer's state receipt (`state.json`) shows a
   phase other than `switching`, `checking` or `blocked`. Recover such a switch
   with `--recover` before step 2: a poll refuses with "operator recovery
   required" while one is open, and once the new Compose file is installed,
   recovering that earlier target is held to its limits. A leftover `preparing`
   or `preflight` phase, from an interrupted candidate, needs nothing: `--recover`
   refuses it, and step 3's poll removes that candidate before anything else. Do
   not run a poll to clear it, since a poll before step 2 quarantines the
   published D458 head. Way back: start the timer again.
2. **Install the operator files from that release's commit:** `deploy/compose.yaml`
   (as `/etc/openetrm/release-compose.yaml`, the file the service units name),
   and `tools/deploy_release.py` together with `tools/release_bundle.py`, which it
   imports (in `/opt/openetrm/operations`). An older `release_bundle.py` can refuse
   a newer release, and an older deployer starts its candidate with 1 GiB whatever
   the Compose file says. Check: from a checkout of the same commit,
   `python3 tools/check_deployment.py --compose /etc/openetrm/release-compose.yaml`
   passes; it renders the installed file and requires the API's explicit memory,
   processor and process limits. Record the new
   `composeSha256`. The serving API is unchanged by this step. Way back: reinstall
   the previous three files and record the previous hash.
3. **Deploy the image with the timer still stopped:** run one poll with
   `sudo systemctl start openetrm-deployment.service`, leaving `host.json`
   unchanged; earlier images refuse an ordinary limit above 15 s or an extended
   limit above 20 s, so the limits wait for step 4. A journal ending
   `DEPLOYMENT_SUCCEEDED <commit>` means the candidate ran under the new Compose
   limits and promotion recreated the API under them, both checked by the
   deployer. Any other ending leaves the earlier image serving, or, after a
   switch, `blocked` for the recovery above.
   - A candidate the host records under other limits than the rendered ones is
     the host's doing, not the release's. The deployer removes it and backs off
     without quarantining the head, and the journal shows the differing limits,
     for example `DEPLOYMENT_FAILED HostResourceMismatch: the host records the
     candidate under other limits than the rendered API: PidsLimit None
     (rendered 128)` from a daemon that dropped the process limit. Check what the
     daemon applies: `docker create
     --network none` any local image with the rendered `--memory`, `--cpus` and
     `--pids-limit`, read `docker inspect --format '{{.HostConfig.Memory}}
     {{.HostConfig.NanoCpus}} {{.HostConfig.PidsLimit}}'`, then `docker rm` it. A
     poll before `retryAfter` in `state.json` prints only `DEPLOYMENT_BACKOFF`; the
     wait starts at 2 minutes and doubles up to 1 hour. Each retry downloads the
     release again (up to 768 MiB) and loads and saves its image, so while the
     timer runs, a mismatch that persists costs that once an hour until the host
     is fixed.
   - A switch blocked because the promoted API runs under other limits is resolved
     by recreating the API under the installed Compose file,
     `sudo docker compose --env-file /etc/openetrm/compose.env -f /etc/openetrm/release-compose.yaml up -d --no-deps --force-recreate api`,
     confirming that `docker inspect --format '{{.HostConfig.Memory}} {{.HostConfig.NanoCpus}} {{.HostConfig.PidsLimit}}' <API container>`
     shows the rendered limits, and only then running `--recover`, which refuses
     the new image while they differ. If the recreated API still runs under other
     limits, return to the previous image with the D302 paired rollback and run
     `--recover`: it records the previous image as active and quarantines the
     head.

   Check before step 4: the deployer's state receipt (`state.json`) names this
   commit as `activeCommit`, and
   `docker inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' <API container>`
   prints a commit that contains D458 (`git merge-base --is-ancestor <D458 commit> <printed commit>`).
   Way back: there is no automatic rollback. To return to the earlier image, use
   the D302 paired rollback (its `host.json` is still in place). To return to the
   earlier memory limit as well, reinstall the previous Compose file, record its
   hash and recreate the API with
   `sudo docker compose --env-file /etc/openetrm/compose.env -f /etc/openetrm/release-compose.yaml up -d --no-deps api`.
   Keep the timer stopped until the new Compose file is installed again: every
   later release carries it, so against the previous hash each would be
   quarantined.
4. **Select the longer limits, gateway first:**
   1. Write the target `host.json` with the new `limits` (at most 29 s ordinary and
      35 s extended). From it, render the gateway with `tools/gateway_configuration.py`,
      validate the result with the target nginx (`nginx -t`), install it and reload
      nginx. Its read timeout becomes the extended limit plus the response grace,
      40 s at 35 s and 5 s, which also covers the limits still running. The way back
      is the previous rendered file, reloaded.
   2. Only then install that `host.json`, record `hostConfigurationSha256` and
      restart the API (`docker restart <API container>`). A gateway still rendered
      from the earlier limits would cut requests longer than its read timeout (25 s
      at 20 s and 5 s) with a bare 504. Check: the container keeps running with a
      steady restart count (`docker inspect --format '{{.State.Running}} {{.RestartCount}}'`),
      its log has no "Supply limits" refusal, and the public site answers. Way back:
      restore the previous `host.json` and its hash and restart the API. The new
      gateway can stay, since its read timeout covers the earlier limits.
5. **Start `openetrm-deployment.timer` again** only once step 4's hash is
   recorded and the site serves.

Risk history reads, risk job submissions and a whole-firm Blotter read at a close
stay slow on the demo dataset on two processors; the longer limits only stop them
being cancelled at 15 s. Their speed is an open performance item.

## Database patch image (PostgreSQL 18.7)

`deploy/compose.yaml` pins the stock `postgres:18.7-bookworm` image by its index
digest in place of 18.6, the patch release CI verifies under the
[dependency lock](ci-dependencies.md). The change is infrastructure. A release
carrying it has a Compose hash that differs from an installed 18.6 baseline, so
the updater quarantines that head unless `openetrm-deployment.timer` is stopped
before the release is published and the Compose file and its `composeSha256` are
installed as in steps 1 and 2 of [Hosted capacity](#hosted-capacity-d458). The
updater switches only the API with `--no-deps`, and a database restart during a
switch blocks further automatic switches. A serving database therefore stays on
18.6 until its container is recreated from the installed file. Once that file
is installed, a `docker compose up` that names `api` without `--no-deps`, or
names no service, recreates the database on 18.7. Run none outside the window.

Moving between 18 patch releases needs no dump and restore. A disposable check
initialized the 18.6 image with this file's database settings, wrote synthetic
rows, stopped the server cleanly and started the 18.7 image on the same data
directory. The system identifier, data checksums, commit timestamp tracking and
UTC setting were unchanged, the rows read back identically, `pg_checksums`
reported no bad block, and both images report collation version 2.36. A second
check did the same after applying `db/bootstrap.sql` and all 411 migrations: the
18.7 server started without error and reported the same index inventory.

The 18.7 release notes ask for a `REINDEX` of btree indexes on `bpchar` columns
declared without a length, of `pg_trgm` GiST indexes, and of GIN indexes
suspected of earlier damage. The migrated schema has only btree indexes, no
`bpchar` column and no extension other than `plpgsql`, so its definition
requires none. They also name TOAST indexes damaged by an earlier `REINDEX
CONCURRENTLY`. That depends on what was run on the installation, not on the
schema; nothing in this repository runs it. The notes say incremental backups
taken before the update should not be trusted; an installation that uses them
takes a new full backup afterwards.

Whether the recorded `schemaSha256` survives the change depends on the
installed deployer. The schema dump's header names the server and `pg_dump`
versions, and on the migrated disposable database those two lines were the only
difference between the images besides the random key. A deployer from before
D465 hashes them. Once the database is on 18.7 its recorded fingerprint no
longer matches, and each head the updater tries fails its schema check and is
quarantined until the fingerprint is recorded again. The journal names only the
error class, so this reads the same as real schema drift. The D465 deployer
leaves the two lines out, so the fingerprint it records is the same under both
images.

The installation's own window is not qualified here. As for a schema window,
stop the API and background writers and retain a verified backup before
recreating the database container. Before restoring the updater, confirm the
server version, the unchanged system identifier and public readiness, and
compare the schema dump with the one taken before the change. Record
`schemaSha256` again unless the D465 deployer and a fingerprint it produced are
already installed. An installation using the optional worker database transport
also confirms that its `DATABASE_ADDRESS` still reaches the recreated container.
