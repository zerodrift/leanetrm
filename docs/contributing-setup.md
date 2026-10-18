# Contribution policy setup

The Project uses Apache-2.0 and DCO sign-offs for all contribution commits.
Designated core contributors additionally sign the
[Core Contributor Agreement, version 1.2](../CLA.md).

## DCO checks

1. Install the [DCO GitHub App](https://github.com/apps/dco) for
   `zerodrift/leanetrm`. The app checks commit sign-off lines; it does not verify
   copyright ownership or employer authorization.
2. Verify on a test pull request that an unsigned commit fails and a properly
   signed-off commit passes. For an existing authored commit, a contributor can
   use `git commit --amend --no-edit -s` if they can make the DCO certification.
   Each included contribution commit needs appropriate sign-offs; do not certify
   another person's work on their behalf.
3. Require the exact status check produced by the DCO App in the default branch's
   ruleset or branch protection, selecting the expected app as its source when
   supported. Verify that a failing DCO check blocks merging. Installing the app
   alone does not establish required merge enforcement.

These repository documents do not install the app or configure branch rules.
The live integration and enforcement must be verified separately. CLA Assistant
is not needed for this policy.

## Core agreements

Designate individuals in writing before granting commit access or assigning
ongoing responsibility for a module. Have the agreement reviewed by counsel
before collecting signatures. Use a direct signing process that retains the
exact agreement version/text, signer's identity and GitHub account, signing date
and time, and evidence of assent.

Resolve any employer or client ownership before accepting affected work. Where
permission is needed, obtain it from an authorized representative of the rights
holder; the contributor's signature alone is not a substitute. This applies to
casual contributors as well as core contributors.

Keep executed agreements, employer permissions and signature records in
restricted maintainer storage outside the public repository, with backups.
The agreement covers the signer's contributions before and after signing, and
its grants survive role changes. Merely publishing the agreement does not bind
past contributors; each required signer must actually assent.

Casual contributions remain subject to Apache-2.0 obligations. Core agreements
do not remove license obligations for other contributors' work or third-party
dependencies. A new agreement version needs renewed assent to its changed terms;
retain the earlier version and its records.
