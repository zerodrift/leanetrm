# CI dependency caches

GitHub verification and release builds cache NuGet's global package directory
and npm's download cache through the existing, commit-pinned setup actions.
NuGet's cache identity includes `global.json` and all committed source/test
`packages.lock.json` files. npm uses `web/package-lock.json`. Every job still runs
locked restores and `npm ci --ignore-scripts`; neither `node_modules` nor build
outputs are restored as dependencies.

## PostgreSQL admission

`tools/ci/postgresql.lock.json` pins stock PostgreSQL 18.7 for Ubuntu 24.04 amd64,
its server/client package revision, and each dependency's exact public URL,
size and SHA-256. The immutable binary bundle and matching source bundle have
their own SHA-256 pins. The reviewed lock in the checkout is the trust anchor;
a checksum downloaded beside an artifact is not sufficient authority.

CI caches only the admitted `.deb` files under a key containing the complete
lock hash, operating system and architecture. `tools/ci_dependencies.py` verifies
every file on both hits and misses before invoking the installer. It refuses
changed cached bytes, invalid bundle hashes, unlisted or duplicate archive
members, links, traversal and missing members. Downloaded files enter the cache
only after the complete acquisition passes verification. A partial cache can
acquire missing files, but an invalid existing file fails the job.

An optional repository variable, `POSTGRESQL_BUNDLE_URL`, selects the exact public
Gitea generic-package file URL. This URL contains no credentials. It must be
reachable from GitHub-hosted runners; a private LAN address is insufficient.
With the variable absent, CI explicitly downloads the individually pinned public
archive URLs. With it present, a bundle failure fails the job: it does not silently
switch to another source. A cache miss admits only the bundle of the checked-out
lock; the bundle of any other lock fails the job. HTTP is supported for an
operator's public package endpoint, with SHA-256 admission unchanged; HTTPS is
preferred. No CI consumer downloads a signing key or refreshes network
repository indexes.

Installation builds a temporary `file:` repository and archive directory from
only verified packages, with network sources, package downloads and removals
prohibited. The generated local index trusts exactly the admitted package bytes.
APT's canonical archive filenames are populated for offline `Pre-Depends` handling.
Only server/client installation is requested. Apt retains compatible installed
libraries, including older libc/development pairs and newer `libpq5`/`libpq-dev`
pairs, and selects missing or required dependency upgrades from the local bundle.
The server and client themselves must match the selected package revision; newer
installed versions refuse instead of forcing a downgrade. Unresolvable installed
companions also refuse. The runner's installed state and apt's dependency checks
remain part of qualification; the bundle does not freeze the entire runner image.

The workflow records PostgreSQL, ICU and time-zone versions as before. Every
database check still creates a disposable cluster; cached packages contain no
database state, credentials, application settings or installation configuration.

## Preparing and publishing a new bundle

The producer runs in a pinned, stock Ubuntu container and takes an explicit
PostgreSQL Debian package revision. It verifies the checked-in PGDG public key
against primary fingerprint `B97B0AFCAA1A47F044F244A07FCC7D46ACCC4CF8`.
The key is the unmodified [PGDG signing key](https://www.postgresql.org/media/keys/ACCC4CF8.asc),
SHA-256 `0144068502a1eddd2a0280ede10ef607d1ec592ce819940991203941564e8e76`.
Ubuntu repositories use the stock image's Ubuntu archive keyring. Signed-index
refresh errors fail the build. No private package source is needed.

The producer requests the server, the client and `libpq5` at the one selected
revision. Since PostgreSQL 19, PGDG's `main` component publishes that release's
`libpq5` and no longer receives new PostgreSQL 18 builds of the library; the
build matching the current 18 server revision is published in component `18`.
`tools/ci/postgresql.sources` therefore enables `main 18`. The selected revision
must be one the current signed index offers for all three packages.

Run from the repository root, writing generated artifacts outside the checkout:

```sh
docker build --platform linux/amd64 \
  --build-arg POSTGRES_PACKAGE_VERSION=18.7-1.pgdg24.04+5 \
  -f tools/ci/postgresql.Dockerfile -t openetrm-ci-bundle:review .
docker create --name openetrm-ci-bundle-export openetrm-ci-bundle:review
docker cp openetrm-ci-bundle-export:/out /tmp/openetrm-ci-bundle
docker rm openetrm-ci-bundle-export
```

The producer resolves dependencies against the pristine base image's installed
state, so installing build tools cannot conceal required runtime packages. Apt
authenticates the binary and source files against signed repository metadata.
The output contains the binary archive, corresponding source archive, proposed
lock, source-file hashes and signed repository release receipts. Debian source
descriptions, original source archives and distribution patches are retained.
All original package copyright and license notices remain inside unmodified
packages and sources; those dependencies retain their respective licenses.

Review the proposed lock and source identities, exercise cold and warm installs
including a runner with newer compatible libraries, then copy only the reviewed
lock into `tools/ci/postgresql.lock.json`. Rebuilding later can select newer
transitive dependencies, so the output must be reviewed as a new bundle; it must
not overwrite an existing identity. Refreshing a key, base image or package pin
is a separately reviewed source change.

Publish **both** binary and corresponding-source bundles, the lock, source-file
manifest, key and signed archive receipts under one immutable
[Gitea generic-package version](https://docs.gitea.com/usage/packages/generic/).
Use a version incorporating the reviewed lock hash. A repeated upload should
compare existing bytes and succeed only for an exact match, never delete and
replace the existing version. Keep publication credentials out of the repository
and workflow. Verify an unauthenticated download against the reviewed lock before
setting `POSTGRESQL_BUNDLE_URL`; forks require the same public read access.

## Superseded pins

The pinned public URLs are not permanent. The Ubuntu and PGDG pools remove a
package file once no current index lists it, so a superseded pin can start
returning 404 while the lock is unchanged. On 2026-11-27 six of the 46 URLs of
the PostgreSQL 18.6 lock had gone: four `krb5` libraries and `tzdata` from
Ubuntu, and `libpq5` from PGDG.

A superseded pin does not change admitted bytes. A cache restored under the
lock's hash and a published bundle keep working. Only an acquisition that needs
the public URLs fails, at the first missing file, and nothing enters the cache.
There is no fallback to a neighbouring revision, another mirror or an archive
service: those bytes are not in the reviewed lock. GitHub evicts caches, so a
lock with dead URLs and no reachable bundle eventually stops every consumer.

Nothing checks the pinned URLs on a schedule. A cold public acquisition is the
check:

```sh
python3 tools/ci_dependencies.py --verify-only --cache "$(mktemp -d)"
```

Repair is a full refresh, never an edit of the failing entries. Run the producer
for a revision the PGDG index currently offers and treat its output as a new
bundle under the procedure above. The producer re-resolves every dependency, so
a refresh also moves pins that still download and can add or remove packages;
review the proposed lock as a whole. The 18.7 refresh moved `systemd` although
its 18.6 pin still downloaded. It added `libsystemd0`, which the newer `systemd`
requires at its own revision, and `libudev1`, which no package requires at that
revision: apt upgrades installed packages built from the same source together.

`POSTGRESQL_BUNDLE_URL` is one value for every branch, while the lock belongs to
a commit. Where the variable is set, publish the new bundle and point the
variable at it, or clear it, before the lock change is checked. A changed lock
misses every cache keyed on its hash and the cache qualification always requires
a miss, so both workflows would otherwise offer the previous bundle to the new
lock. Branches still on the previous lock then depend on their existing caches
until they take the new lock.

## Qualification

`python3 -m unittest discover -s tests -p test_ci_dependencies.py` checks cold
bundle/public acquisition, fully offline hits, partial caches, source failures,
tampering, archive boundaries, installed-version selection and workflow wiring.

The `Dependency cache qualification` workflow uses a fresh run-specific GitHub
cache key. It requires a miss, performs locked dependency acquisition, saves the
caches, deletes their local copies, restores them from GitHub and requires a hit.
It then restores NuGet with every package source removed, runs offline npm,
prohibits PostgreSQL downloads, installs PostgreSQL from the restored packages and
starts an isolated stock database with commit timestamps enabled. It runs when
dependency infrastructure changes in a pull request and can be dispatched manually.

This qualification does not attest application correctness or authorize a hosted
release. The ordinary verification matrix and release admission remain required.

The PostgreSQL 18.7 lock was produced on 2026-11-27 from the pinned base image
for revision `18.7-1.pgdg24.04+5`. An uncached second run reproduced the lock
and both bundles byte for byte. Each of its 48 entries matches the path, size
and SHA-256 in package indexes authenticated by the retained signed release
receipts. Cold public acquisition, a warm hit with downloads prohibited and
bundle acquisition from a local endpoint passed and admitted identical bytes.
Installation and an isolated database start passed in two disposable
containers, each with `python3`, `sudo` and `curl` added: cold in the pinned
base image, and offline from the warm cache in a fully upgraded Ubuntu 24.04
image holding PGDG `libpq5` and `libpq-dev` 19.0, which apt retained. Those are
local results. The hosted `Dependency cache qualification` workflow then passed
for this lock on 2026-11-27 in
[run 46068514108](https://github.com/zerodrift/leanetrm/actions/runs/46068514108)
at commit `d49ebddbb152a5297faa6921f12f57d98fb4053b`. The bundles for this lock
are not published.
