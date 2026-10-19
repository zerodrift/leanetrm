# Contributing to LeanETRM

Contributions are welcome, including small bug fixes and documentation updates.
The Project and ordinary contributions are licensed under the
[Apache License, Version 2.0](LICENSE).

## Sign off your contributions

Every contributor must certify the [Developer Certificate of Origin, version 1.1](https://developercertificate.org/)
(DCO) by adding a `Signed-off-by` line to each contribution commit. Git can add it
using your configured name and email:

```sh
git commit -s
```

The sign-off certifies that you have the right to submit the work under its
stated open source license. Read the DCO before signing. It is a certification,
not a cryptographic commit signature. Your sign-off name and email become part
of the public commit history.

For an existing commit that you authored and are entitled to sign off:

```sh
git commit --amend --no-edit -s
```

If a pull request contains several commits, each needs the appropriate sign-offs.
Coordinate with any collaborators before rewriting a shared branch; do not add
another person's certification on their behalf.

Casual contributors do not need to sign a separate CLA. Reporting a bug, asking
a question or joining a discussion does not require a DCO sign-off.

## Core contributors

The Maintainer designates core contributors in writing before granting commit
access or assigning ongoing responsibility for a module. Before beginning that
role, an individual must also sign the
[Core Contributor Agreement, version 1.2](CLA.md). It preserves contributor
copyright and grants additional rights, including commercial and proprietary
relicensing. DCO sign-offs remain required for core contributors.

Request the signing process directly from the Maintainer. Keep signed agreements
and employer permissions out of public issues, pull requests and repository files.

## Rights and third-party material

All contributors, including people submitting small fixes, must have permission
to contribute their work. If an employer or client owns relevant rights, resolve
that authorization before submitting. Do not submit confidential information or
trade secrets. A sign-off or individual agreement cannot grant rights you do not
hold.

Submit third-party material separately, identify its source, author and license,
and preserve applicable notices. The Maintainer reviews it under its existing
license; signing the core agreement does not change those terms.

## Development

Read the [project instructions](AGENTS.md), [architecture](docs/architecture.md)
and [delivery plan](docs/plan.md). Start new behavior and bug fixes with a
meaningful failing behavior check, and run the relevant
[verification checks](docs/verification.md).

Maintainers can follow the [DCO and agreement setup instructions](docs/contributing-setup.md)
to configure signing checks and retain core agreement records.
