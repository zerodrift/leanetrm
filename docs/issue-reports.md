# Issue reports

A signed-in person can send a report about the software to the installation's
issue tracker and get back the issue's number and link (D463). This document
records what is implemented, its contracts and what remains.

"Ticket" already means the deal ticket in this application, so the feature is
named for what it creates: an issue report.

## Implemented

| Part | State |
| --- | --- |
| Workspace policy: `disabled` or `review_required`, with a daily limit per person | Implemented (migration 0412) |
| Retained requests with replay, conflict and a daily bound | Implemented |
| One admitted dispatch per attempt; uncertain outcomes; lookup, resend and abandon | Implemented |
| GitHub App adapter: scoped token, create issue, find issue by marker | Implemented |
| HTTP operations for people | Implemented |
| Assistant drafting a report for the person's review and authorization | Implemented |
| Browser screens: the policy, and Report an issue | Implemented |
| `automatic` mode | Not implemented; needs its own recorded decision |
| Hosted installation wiring for the tracker connection | Not implemented |

Local checks use a loopback synthetic tracker. No real tracker has been called
and no hosted installation is qualified.

## What it does not do

Creating an issue does not assign anyone, promise a fix, open a pull request,
merge code or deploy anything. Those remain the repository's ordinary workflow.

## Policy

An administrator holding `ManageIssueReporting` saves an explicit policy
revision for the workspace.

| Mode | Behavior |
| --- | --- |
| absent | Nothing can be submitted. No mode is assumed |
| `disabled` | Nothing can be submitted |
| `review_required` | A person submits text they have read. `dailyLimit`, from 1 to 50, bounds each person's requests in any 24 hours |

Revisions are immutable and numbered. A save names the version it expects.
Saving the facts already in force records the request and creates no revision.
The same request identity returns its first result; with different content it
conflicts.

`automatic` submission by an agent is not a mode today. It would be the first
effect an agent executes without a person's authorization, so it waits for a
decision that states which content it may send.

## Tracker connection

The connection is private installation configuration, not workspace data. The
API reads the file named by `OPENETRM_ISSUE_TRACKER_CONFIG`; without it the
operations answer `issue_tracker_unconfigured`. Every member is required:

| Member | Meaning |
| --- | --- |
| `repository` | The one `owner/name` reports go to |
| `appId` | The GitHub App's client ID or app ID |
| `installationId` | The app's installation that can reach the repository |
| `privateKeyFile` | Absolute path of the app's RSA private key in PEM form, readable by its owner only |
| `apiVersion` | The dated REST API version to request |
| `connectSeconds` | 1 to 10 |
| `responseSeconds` | 5 to 60; bounds one whole exchange, token included |
| `maximumResponseBytes` | 2,097,152 to 16,777,216; one page of a listing must fit |

The API does not start with a file that is incomplete, a key that is not a
private RSA key, or a key file others can read.

The app needs the Issues permission on that repository and nothing else. For
each exchange the adapter signs a short-lived assertion, obtains an installation
token limited to `issues: write` on the one repository, uses it and discards it.
Neither the key nor a token enters a response, a log, the browser or model
context. Nobody chooses a repository per request: the model, the person and the
workspace administrator cannot redirect a report.

`OPENETRM_ISSUE_TRACKER_CHECK_ENDPOINT` redirects the adapter to a loopback
origin for owned checks, under the same rule as the provider check endpoint. A
deployment whose serving API names it is refused.

## Operations

Each report is returned with its state, attempt, issue number and link, error
code and `decisions`: what its owner may still decide, as the server sees it.

All require a signed-in human acting for themselves. An agent's delegated
context is refused.

| Route | Permission | Result |
| --- | --- | --- |
| `GET /api/settings/issue-reports` | `ManageIssueReporting` | Configured repository, current policy and its last 20 revisions |
| `POST /api/settings/issue-reports` | `ManageIssueReporting` | Saves a policy revision |
| `GET /api/issue-reports` | `SubmitIssueReports` | Configured repository, current mode and the person's latest 25 reports |
| `POST /api/issue-reports` | `SubmitIssueReports` | Retains and sends a report |
| `POST /api/issue-reports/reconcile` | `SubmitIssueReports`, owner of the report | Looks for an unconfirmed report in the tracker |
| `POST /api/issue-reports/resolve` | owner of the report; `resend` also needs `SubmitIssueReports` | `resend` or `abandon` a report that is not settled |

The six human role templates hold `SubmitIssueReports`. Access administrator
holds `ManageIssueReporting`. The agent reader role holds neither.

A report has a `kind` (`bug`, `request` or `question`), a single-line `title` of
at most 120 characters and a `body` of at most 4,000. Each must contain a
visible character. Control characters and characters that reorder or break a
line invisibly are refused; a body may hold tabs and line breaks. The issue's title is the
report's title. Its body is `Kind: <kind>`, the report's body and a hidden
marker line, with nothing else added.

## The assistant

The assistant reaches issue reports through its closed operation catalog, under
the name `issue_reports`, from whatever page the person is on.

| Operation | Effect |
| --- | --- |
| `issue-reports_v1` | Reads the person's own reports and the current mode |
| `issue-reports_command_v1` | Drafts a report. It needs the person's approval |

The assistant cannot submit. It prepares the draft with `propose_page_action`,
and nothing reaches the tracker. The person opens **Review assistant proposal**,
which names the repository and says the report will be public there, reads the
exact kind, title and body, ticks the authorization and presses **Authorize
change**. The ordinary submission then runs under the person's own
sign-in, with every check above, and answers with the issue's number and link.
Authorizing the same draft again answers with that first outcome: the draft's
request identity is derived from its turn and step.

The draft carries no repository, and the assistant has no way to name one.
Looking a report up, resending it, giving it up and the workspace policy are not
in the catalog; they stay the person's own controls.

A draft does not outlive the policy. Authorized after reporting was disabled,
it is refused and nothing is sent.

Installations supply their own guidance for the assistant's tools. None is
installed for issue reports.

## Screens

**Report an issue** is a button in the page header for anyone holding
`SubmitIssueReports`, in an installation that has a tracker connection. It opens
a dialog with:

- the repository the report goes to, and that it is public;
- a kind with no preselected choice, a title and a description;
- a required confirmation that the person has read the text;
- the person's reports, each with what happened to it and, for a created issue,
  its link.

Each report offers the decisions the server lists for it: **Look for it in the
tracker**, **Send it again** and **Give it up** for an unconfirmed report, and
only the last for one that was never sent. When the workspace has not turned
reporting on, the dialog says so and offers no form. What the person has typed
stays in the form while they act on another report.

A request whose answer is lost keeps its identity in the browser session, so
**Retry original request** cannot create a second issue. It is released when
the server answers that it never retained the request.

**Issue reporting**, on Users & access, is the policy form for a holder of
`ManageIssueReporting`: off or on, the daily limit, the current version and
recent revisions.

## Content

The tracker is public. A report should describe the software problem with
public or synthetic examples. Nothing is attached: no conversation, log,
credential, customer identity or trading record.

Free text cannot be checked for private detail by a rule. In `review_required`
the control is that the person has read the exact text they submit.

## Delivery

A request moves through these states.

| State | Meaning | Settled |
| --- | --- | --- |
| `pending` | Retained, not sent | no |
| `dispatching` | One send is admitted and may be in flight | no |
| `submitted` | The tracker confirmed the issue; number and link retained | yes |
| `failed` | The tracker did not create an issue; `errorCode` says why | yes |
| `uncertain` | A send may or may not have created an issue | no |
| `abandoned` | The person gave it up | yes |

1. **Request.** The request identity is scoped to the workspace and the person.
   The same identity with the same kind, title and body returns the retained
   report in its current state, also after the installation changed its
   repository. Different content under the same identity is
   `issue_report_conflict`.
   A new request needs the policy to be on and the person to be under the daily
   limit. It retains the policy version and the repository it was made for.
2. **Admission.** In its own transaction, immediately before the tracker is
   called, the database rechecks the person's permission and the current policy
   and moves `pending` to `dispatching` as attempt 1. A second caller is
   admitted nothing.
3. **Send.** The adapter creates the issue under its own time bound. Neither the
   caller leaving nor the request's time limit interrupts it.
4. **Outcome.** The answer is recorded for that attempt. An outcome is a fact
   about a request already sent, so recording it checks neither the policy nor
   the issue report permission. It still needs the person's session, and on the
   assistant's path the authorization of the draft. If either was withdrawn
   during the exchange the outcome is not recorded, the report becomes
   uncertain, and the lookup finds its issue. A `submitted` outcome is accepted
   only with the retained repository's own page for that issue number.

The daily limit counts every request of the last 24 hours except one abandoned
before it was ever sent. Failed reports count: each one called the tracker.

A local transaction cannot commit an issue in the tracker, so three cases stay
uncertain: a response lost after the request left, a server error, and a process
that stopped between admission and outcome. A dispatch admitted more than two
minutes ago with no outcome is treated as uncertain; the adapter's whole
exchange is bounded to at most 60 seconds.

An uncertain report is never sent again by itself, and it never returns to
`pending`: `pending` always means never sent.

- **Reconcile** looks in the tracker for the report's marker. It is admitted at
  most once a minute for each report. Found: the report becomes `submitted`.
  Otherwise the answer says whether the listing was `absent` (read to its end
  without the marker) or `incomplete`. Absence alone does not prove that
  creation failed.
- **Resend** is the person's explicit decision. It is admitted in one step, as
  the next attempt, under the permission and policy current then; a refusal
  leaves the report uncertain. It is refused with `issue_report_wait` until two
  minutes after the previous attempt was admitted, when that exchange can no
  longer be completing. The new attempt looks for the marker first and creates
  only when the listing is complete without it.
- **Abandon** settles a `pending` or `uncertain` report without sending.

Both decisions name the attempt they follow. Repeating one after it took effect
changes nothing and answers with the report as it now is.

### The lookup

The marker is published in the issue, so anyone can copy it once the issue
exists. The report's issue is therefore the oldest marked issue created since
five minutes before the request. A later copy is newer, and an older issue
edited to carry the marker was created too early.

The adapter lists the repository's issues and pull requests by creation time,
newest first, twenty to a page. The listing is complete at the first one created
before that moment, or at a short page. After fifteen pages without reaching
either it is `incomplete`, and so is any page that cannot be read exactly or
exceeds the byte bound. A marked issue counts only when the listing was
complete.

### Reading the tracker's answer

On a first attempt:

| Answer to the create request | Outcome |
| --- | --- |
| 201 with a positive number and the configured repository's own `https://github.com/<repository>/issues/<number>` link | `submitted` |
| 201 that cannot be read as that, any other 2xx, any 5xx, no answer in time, a response lost after sending, anything unexpected | `uncertain`, `outcome_unknown` |
| 429 | `failed`, `tracker_rate_limited` |
| Any other 3xx or 4xx | `failed`, `tracker_refused` |
| The connection was never made | `failed`, `tracker_unavailable` |
| The token was refused | `failed`, `tracker_authentication`; no create request is sent |
| The report was retained for another repository than the one now configured | `failed`, `tracker_repository_changed`; nothing is sent |

A later attempt follows one that may have created the issue. It can settle as
`failed` only after a complete listing without the marker and then a create
request the tracker provably did not carry out. If it cannot obtain a token,
cannot read the listing, or the configured repository changed, the report stays
`uncertain`.

Revoking a permission or disabling the policy stops reports that were not yet
admitted. It cannot retract a request already sent or remove an issue already
created.

## Known limits

- The person's session is held for the length of the exchange, so their other
  requests wait while a report is being sent or looked up.
- The assistant's draft is written by a model that may have read the
  installation's data in the same conversation. The control is the person's
  review of the exact text; nothing checks it for business detail.
- The screens have unit tests for what they show and offer, and were tried by
  hand in a preview, including at a width of 390 px. They have no browser-suite
  scenario yet.
- The assistant's review cannot be the target of a walkthrough step.
- Only the latest attempt's admission time and code are retained for a report.
  There is no per-attempt history.
- A repository in which more than 300 issues and pull requests were created
  after a report cannot complete that report's lookup, and neither can one where
  a page of twenty exceeds the byte bound. Anyone who can post to a public
  tracker can cause either. The report stays uncertain and can be abandoned.
- A resend waits two minutes, but the tracker itself may complete or list an
  earlier request later than that. The lookup before a resend narrows this
  window; it does not close it.
- A tracker that answers 403 or 429 because of its own rate limits settles a
  first attempt as `failed`, which counts toward the person's daily limit.
- GitHub.com only; other hosts are not supported.

## Verification

Run from the repository root with stock PostgreSQL 18 and the pinned .NET SDK.

| Check | Covers |
| --- | --- |
| `tools/check_issue_reports.py --pg-bin …` | Policy revisions, authority, content rules, request replay and conflict, single admission, outcomes and their link rule, stale dispatch, the wait before a resend, bounded lookups, abandon, daily bound, recheck at dispatch, immutable revisions, no direct table or helper access |
| `dotnet run --project tests/OpenEtrm.Api.Checks -- --issue-tracker` | Key and settings admission, the adapter's requests, signed assertion, token scope, exact issue text, every answer classification on first and later attempts, and the lookup's ordering, bounds and refusals |
| `tools/run_preview.py … --issue-report-check` | The six routes against a loopback synthetic tracker, including a response lost after the issue was created and a resend that cannot reach the tracker |
| `tools/run_preview.py … --check --assistant-check` | The schema's forced row security and its list of operations the runtime may execute; an agent's delegated context refused for submission and policy |
| `tools/run_preview.py … --check --assistant-workspace-check` | Discovery of the two operations, the assistant refused as submitter, the draft sending nothing, Review showing the exact text, one issue on authorization, replay, another person refused, and a draft refused after the policy was disabled |
| `web/tests/issue-reports.test.mjs` | The policy as sent, the decisions each report state offers, what each state says, and when the form is offered |

`tools/run_preview.py … --with-issue-tracker` starts an interactive preview
connected to the synthetic tracker, to try the screens by hand.

All use synthetic data and disposable resources. Results are recorded in
[verification](verification.md#issue-reports-d463).

Not covered by a check: an installation with no tracker connection, concurrent
requests, and the 24-hour roll-off of the daily limit. The synthetic tracker
does not filter a listing by time; the adapter check asserts the exact query
instead.
