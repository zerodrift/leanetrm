# Regional closes

Status: D334, implemented in the repository and verified by the checks under
[Verification](#verification). Hosted activation requires separate qualification.
The owner decided the points listed below; calls marked *design call* await owner review. It
refines D162 and D219 where they allow several selectable closes on one day.
Examples are synthetic.

## Why

A deployment can span regions. Desks in London mark their end of day at London
time; desks in New York mark theirs hours later. Before D334, every recorded close
was another selectable frame: several closes on a day stayed selectable, and the
business day was only the UTC date of the close instant. Two problems followed:

- With one global "latest close wins" rule, a New York close would silently
  replace London's sign-off, and London books would revalue at New York prices
  or lose curves New York does not mark.
- A close recorded at 00:30 UTC for a New York business day lands on the next
  UTC date.

## Decided by the owner

1. A close is a regional end of day, called a **close session** (for example
   London EOD, New York EOD). Each region's close is its own complete close.
2. A business day has at most one close in force per session. When a close is
   recorded again for the same session and business day, the latest recorded
   one takes over **the whole session**, not individual curves.
3. Earlier closes for that session and day remain visible as history but cannot
   be selected for new work.
4. A close keeps its exact UTC instant.
5. **Always latest**: every view, including a historical "known at" view, uses
   the close currently in force for a session and business day.
6. Recording, and so replacing, a close needs a grant for that session.
7. Existing closes are retired and deployments reseeded; none are mapped.
8. Each person's chosen session is remembered.
9. Only recorded closes are closes. The As-of list shows the close in force for
   the person's session and day; unrecorded snapshots are working data, offered
   separately and never listed as closes.
10. Commodities with several daily prices (for example a precious metal with
   morning and afternoon fixings and an exchange settlement) must remain
   supportable later.

## Goals

- Regions record, correct and sign off their own close without affecting any
  other region.
- Every read names the session and business day it uses; nothing chooses a
  session or close by default.
- One small rule decides which close is in force.
- Results that were calculated at a close remain reproducible after that close
  is replaced, and say that it was replaced.

## Non-goals

- Assembling one official close from several regions' contributions. A
  firm-wide close is just another session that covers every curve at one cutoff.
- Per-curve precedence or carrying a curve forward from an earlier close.
- Automatic regional assignment of users, books or desks from location or time
  zone.
- Changing how a close pins prices (D172 exact pins stay as they are).

## Anti-goals

These would make the change a failure even if it technically works:

- A close appearing in a view because of who recorded it or when, rather than
  the session the view named.
- A missing curve in the in-force close being filled from an older close or
  another session.
- A replaced close silently changing the numbers of a saved result.

## Concepts

### Close session

A versioned configuration item (exchange specifics are configuration, never
code). Each published version has:

| Field | Meaning |
| --- | --- |
| Code, name | Stable identifier, display name |
| Clock | An exact clock rule version (time zone) from reference data |
| Cutoff | Local time of day of the close on that clock, for example 17:30 |
| Business calendar | An exact calendar version; only its business days have closes |
| Curves | The exact curves the session's close must carry |
| Effective from / to | Business days this version governs |

Sessions are authored and published like other configuration (draft, publish,
request id, generation; D167, D238). A curve may belong to several sessions; for
example London may mark every curve at London time while New York marks only
its own.

### Close

A recorded close gains two explicit fields beyond what it stores today:

- `session` — the exact session version it closes;
- `business_date` — the session business day it closes.

Its instant `at` is the session's cutoff on that business day, converted to UTC
from the session clock. Registration supplies the session and business day; the
server derives and checks the instant, so `at` and `business_date` cannot
disagree.

### Close in force

For a (session code, business day), the close in force is the close with the
highest `publication_order` among **all** closes recorded for that pair, whoever
reads it. Every other close for that pair is **replaced**. There is no stored
"current" flag; the rule is evaluated on read, so recording a close is still a
single insert. (Calls made in this design, marked *design call* below, are open
to owner review.)

- *Design call:* the key spans session versions and definition-bundle
  revisions. A close under a newer bundle revision replaces one under an older
  revision for the same session and day. Republishing a session version does
  not change closes already recorded; the next close recorded for a day must
  meet the version then in effect.
- *Design call:* if a reader cannot see the close in force (its definition or
  reference revision is not visible to them), the read is refused
  (`close_not_visible`), naming that a newer close exists. An older close is
  never served in its place, so two people never see different closes in force.

## Behavior

### Recording a close

Recording keeps today's checks (current workspace, `PublishMarketData`, exact
existing frame, sealed definition, pins observed at or before the instant,
frozen-pin rules, request-id idempotency) and adds:

0. **Per-session authority (owner decision).** Recording, and so replacing, a
   close also needs a grant to record closes for that session. `PublishMarketData`
   alone records no close. A grant names the principal or role and the session
   code, and is checked again at the end of the write like other authority. A
   London grant cannot record or replace New York's close. Defining or
   publishing a session version is separate configuration authority.
   *Design call:* recording is the sign-off; there is no separate sign-off step.
   Generated pilot closes need the grant for the service principal that runs
   them.

1. The request names a session code and business day. The server resolves the
   session version effective on that day; none, or a day that is not a session
   business day, is refused (`close_session`).
2. The selected frame's instant must equal the session cutoff on that day
   (`close_session_instant`).
3. The frame must carry exactly the session's curves: a missing curve is
   refused (`close_incomplete`, naming the curves); a curve outside the session
   is refused (`close_outside_session`). Every carried curve still builds from
   its pins.
4. Recording again for the same session and day is allowed and becomes the
   close in force. It must be complete in its own right; the editor starts a
   correction from the close in force, so unchanged curves are carried
   explicitly by the person, not by the server.
5. A retry with the same request id returns the first close (D238); it never
   creates a second replacement.

Generated pilot closes follow the same rules: a pilot close profile names its
session, and the generated close is recorded for the profile's session and the
due business day.

- *Design call:* a generated close is refused for a session and day that already
  has a close (`close_exists`); automation never replaces a person's close.
- *Design call:* a profile's anchor stays its explicit prior close. If that close
  has been replaced, generation is refused (`close_replaced`) until the profile
  names a new anchor.

### Reading and selecting

- The header's As-of control chooses a **session** and then a **business day**.
  The availability calendar (`market/dates`) is read for one named session and
  lists, per business day, only the close in force. Displayed days remain in the
  person's time zone (D162); the business day shown is the session's.
- Each day can open its history: every replaced close with its name, who
  recorded it, when, and its instant. History entries cannot be selected.
- An operation that names a replaced close for new work is refused
  (`close_replaced`, naming the close in force). This covers the header, risk
  submissions, custom-curve previews, curve-definition previews (D318) and
  assistant operations, which all use the same backend operations.
- D219's opening rule becomes per session: a page opens on the newest close in
  force of the person's remembered session, and **Newer close available**
  appears when a later close for that session takes over. A replacement of the
  close being viewed also shows that message.
- **The chosen session is remembered per person (owner decision).** Choosing a
  session saves it as that person's preference; a reload, a return to working
  data or signing in again opens on the newest close in force of that session,
  and an account switch loads the other person's own preference. The header
  always names the session in use. Nothing chooses a session for a person who
  has not chosen one: their pages show the session choice and no prices. If the
  remembered session is no longer published or visible to them, the page says
  so and shows the session choice; it never moves to another session.
- **Opening another session's close names that session (owner decision).** When
  an assistant answer's Go, or any other move, targets a close of a session other
  than the chosen one, the page refuses and names the session the close belongs
  to. The person chooses that session; nothing changes their remembered choice
  as a side effect.

### Working snapshots (owner decision)

A snapshot that has not been recorded as a close — the live frame, or a
snapshot created by an authorized correction or explicit revision selection
(D172) — is **working data**. It is offered in a separate working-snapshot
choice, labelled as such, and never appears in the close list or the
availability calendar. Recording a working snapshot for a session and business
day is how a close is recorded or replaced. Built-in preview and demo frames are
not working snapshots: the seed records them as closes in synthetic sessions, so
every demo page opens on a session close.

### Historical views (always latest)

Under a historical context the close in force is still decided by the latest
recorded close, not by the context's knowledge cutoff, and its pinned prices are
read even if they were published after that cutoff. Everything else (trades,
reference data, other market facts) keeps the context's cutoff. This is a
deliberate, recorded exception to the AGENTS.md rule that historical context
carries the knowledge cutoff through every read, and AGENTS.md is amended to
name it. The response states the close used and whether it was recorded after
the context's cutoff.

- *Design call:* the exception lifts only the knowledge-time checks on the
  close's receipt and pins. The close's definition and reference revisions must
  still be visible under the context, and its pins must still fall in revision
  periods covering the business day.
- *Design call:* a historical context's effective date is the session business
  day. Matching a close to a context uses `business_date`, never the UTC date of
  the instant, so a New York close after midnight UTC still matches.

### Saved results

Risk jobs, close reports, bookings and other saved results keep the exact close
they used. Pilot close report reads expose `replaced: true` and the close now in
force when their original close has since been replaced. Portfolio risk job views
do not yet expose replacement status. Rerunning is an explicit action at the close
in force; nothing reruns automatically.

- *Design call:* work admitted before its close was replaced (for example a
  queued close-risk job) is not new work and retains its exact close. Only
  admission checks `close_replaced`. Replacement disclosure and the historical
  worker limitations below remain separate implementation work.

### Several prices on one day

Different prices for the same instrument on one day (fixings, an exchange
settlement, a trader mark) are separate observations with their own source and
role inside one close, provided the session cutoff is after all of them. A
market whose day ends at a different time from others is its own session.

## Existing data

No backwards compatibility is kept for the old free-form close list
(owner's standing rule). Existing closes are **retired, not mapped** (owner
decision):

- The migration requires an empty close history: it refuses to run while any
  close, or any record that references one (close pins and members, risk jobs,
  close-risk profiles, pilot close profiles and reports), exists. It never
  deletes them itself.
- Deployments with closes clear that market data and reseed it before the
  schema release, as an operator step outside the migration. Every reseeded
  close is recorded through the new rules: a published session, its cutoff
  instant, its complete curve list and a session grant.
- The migration creates no session by itself. Sessions, their grants and the
  seeded closes are declared in synthetic seed data for fixtures and demos.
- The `session` and `business_date` columns are required from the start; there
  is no nullable legacy state and no legacy-close exception in the code.

## Implemented interfaces

- Migration `0202_regional_closes.sql`: `market_data.close_sessions`,
  `close_session_versions` (each pins one definition revision and one reference
  calendar, whose clock is the session clock) and `close_session_curves`;
  `security.close_session_grants` (per principal); `presentation.close_session_choices`;
  required `session_code`, `session_version` and `business_date` on
  `market_data.market_closes`; and the SQL functions that decide the cutoff
  instant, the close in force and recording authority. The migration refuses to
  run while any close or pilot profile exists.
- `GET /api/close-sessions/catalog`, `POST /api/close-sessions/save` (draft,
  `WriteMarketDefinitions`), `POST /api/close-sessions/publish`
  (`SealMarketDefinitions`).
- `GET /api/close-sessions/grants/list`, `POST /api/close-sessions/grants` and
  `POST /api/close-sessions/grants/revoke` (`ManageAccess`, exclusive
  administration admission).
- `GET`/`POST /api/close-sessions/choice`: the person's remembered session.
- `POST /api/market/closes/register` takes `session` and `businessDate`;
  `POST /api/market/dates` requires `session` and returns one close per business
  day with a `replaced` count; `POST /api/market/closes/history` lists a day's
  closes, newest first.
- Workspace and navigation frames carry `close` (session, version, business
  day, `replacedBy`, and under a historical view `recordedAfterCutoff`);
  a frame without `close` is a working snapshot.
- Pilot close definitions name `session`; pilot and close-risk reports carry
  `close.session`, `close.businessDate`, `close.replaced` and `close.inForce`.
- The header has a **Close session** choice, offers unrecorded snapshots as
  "Working snapshot ·" entries, and lists a day's replaced closes under
  **Earlier closes for this day**.
- Previews record every built-in frame as a close of a synthetic UTC session per
  cutoff time (`tools/seed_session_closes.py`), with a new session version
  wherever the frame's curves change, and record each synthetic member's choice
  of the session with the most closes.

Implementation calls, recorded for review:

- The session clock is the clock of the session's calendar, so the two cannot
  disagree.
- The remembered session is its own record, not a key in the general
  preferences document, so a fixture or a release can seed it per person.
- Grants are per principal; role grants remain later work.
- A generated close joins its profile's session, which must cover exactly the
  profile's curves; its anchor may belong to another session.
- An identity that cannot discover closes is refused (403) when it names one.
- A session's cutoff must fall on its business day's UTC date (`close_session_instant`
  otherwise), because every valuation reads a close on that date.
- A later published version supersedes the versions it overlaps from its own start;
  a version starting no later than a published one is refused.
- A request binds the session it reads (`security.use_close_session`); "always
  latest" releases only that session's pinned prices, in both the set and the
  per-row visibility checks, and only when the close in force is itself visible.
- Delegated requests record no close: a close is a person's sign-off.
- Replaced closes are not offered as pilot anchors and are refused at admission.

## Remaining work

- Sessions whose cutoffs fall after UTC midnight (late western closes) are refused;
  supporting them means valuing, booking and risk at the business day across UTC
  midnight.
- Generated pilot closes follow the pilot schedule's UTC instants, so only a UTC
  every-day session fits them.
- The risk worker binds no close session, so a historical risk job at a close
  recorded after its cutoff keeps that cutoff for the close's later prices.

- Role-based session grants and a screen for administering grants.
- Choosing a session's business day for an anchor on the pilot page uses the
  UTC date of the frame's instant.
- Portfolio risk job views do not yet report whether their close was replaced.

## Verification

- `--regional-closes-check`: sessions, superseding versions and grants; London,
  New York and a refused UTC−18h session; refused days,
  instants, curves and grants; idempotent and concurrent recording; the close in
  force, history and `close_replaced`; historical views reading a close recorded
  after their cutoff.
- `--pilot-close-check`: generated closes in their profile's session, refusing
  `close_exists` and a replaced anchor; reports that keep their numbers and say
  `replaced`.
- The standard HTTP suite, rewritten for seeded session closes.
- Web unit tests for the session offer; the `--market-dates` browser suite for
  the header.
- Independent adversarial review is required for changes to temporal behavior and
  authorization before publication.
