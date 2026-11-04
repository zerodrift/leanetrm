# Separate synthetic dataset

The hosted application can explicitly enable a separate synthetic dataset for
booking and risk. The runtime, supported pricing conventions and authorized
business operations are public application code. This dataset’s generators, seed profiles,
owner imports and their operational qualification scripts are private tools;
they are not shipped in this repository or required by its build and CI.

## Installation contract

Private host configuration opts in with
`"syntheticDatasetManifest":"synthetic-manifest.json"`. With the option absent,
the installation exposes only its existing datasets. The manifest must contain a
lowercase SHA-256 generation identity, independent market and reference catalogs,
exact revisions, nonempty observation pins and explicit curve memberships. Its
tenant, workspace, identity issuer and operating mode must match the primary
workspace. Supplemental physical/reference catalogs are not admitted through
this adapter.

The adapter supplies no seed data, credentials, import command or financial
fallback. The installed dataset must already satisfy native reference,
publication, knowledge-time and pricing contracts. Fictional prices and contract
terms are identified as synthetic in the workspace notice. Terms, units,
expiries, discount inputs and numerical policies remain explicit native inputs.

## Booking, risk and isolation

The selector opens `/app?example=synthetic` in hosted mode. Its API namespace,
page cookie, retained trade/report links, calculation cache and browser command
recovery identity retain the selected catalog. Authenticated connection pools
are shared; ordinary native booking and risk authority checks still apply.
Queued risk requires a dedicated worker login registered to that catalog.

Migration 0190 adds a distinct namespace for conversations, approvals and
personal preferences. Exact definition, owner, grant and constraint guards
refuse an unexpected prior schema. Assistant page operations use the selected
API namespace. Signing out revokes the authenticated login across both datasets;
a request token from another dataset cannot authorize that sign-out.

Futures and options catalogs use the selected frame's explicit contract
universe. A selected member missing a required mark still refuses pricing.
The synthetic page directory offers up to eight recent durable closes under its
selected context, while earlier stored closes remain accessible through an
explicit historical business context. The existing saved-selection capacity is
unchanged. Workspace and history reads bound observation anchors to the visible
frames and retain their same-fact correction history. Exact-pin history preserves
its selected revision; as-known history resolves revisions at the explicit cutoff.

Public checks cover manifest/configuration refusal, browser dataset and pending
command identity, gateway body limits, native namespace upgrade guards and
ordinary preview regressions. Private operational qualification additionally
covers installed data admission/retry, booking, portfolio risk, queued replay,
correction history, hosted sign-out and same-user assistant scope isolation.
Production activation requires an approved application release and separately
qualified private provisioning.
