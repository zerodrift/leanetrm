# Exact observation reads

D336 addresses native swap entry timing out while reading an already selected
frame. Its exact-id query still invokes the observation policy's complete
visibility set, enumerating unrelated history. The same path serves human and
assistant reads; increasing timeouts would retain that unnecessary work.

Keep one server visibility algorithm. The existing bulk visibility function
delegates to an internal core with no requested scope. The exact reader supplies
an explicit catalog, revision and pin array; its candidate rows are bounded by
those ids before knowledge-receipt and close joins. The newest close for the
bound session and day is selected before eligibility filters. Its visibility
checks the entire close once, including pins outside the requested array.

The definer point reader preserves ordinary SELECT privilege, the complete
SELECT-policy contract, current market authority, per-row authority deadlines,
and the risk worker's live claim. Unknown policy/RLS changes refuse. Final checks
refuse expiry or lost authority instead of returning a partial successful read.
Duplicates, empty arrays and native row order retain their existing meaning;
mixed-null inputs cannot select bulk data. No endpoint, tool/event shape, agent
grant, business operation or numerical policy changes. No table is added.

Qualification covers native managed reads with broad enumeration forbidden,
ordered equivalence to actual RLS in working and retained contexts, per-session
post-cutoff close pins, newest-close precedence, complete-close validity,
permission/policy/role changes, expiry and unchanged bulk/correction reads.
Independent adversarial review is required for these authorization and temporal
boundaries. Public fixtures and results are synthetic; production observations
and GUI evidence remain outside the repository.

## Selected fact history and calendar frames (D354)

The point reader's final physical fetch also stays parameterized by each exact
authorized identity. A guarded `LATERAL` lookup prevents the opaque visibility
set's row estimate from choosing a scan of unrelated observation history. The
same ordering, row clock and final authority/policy/worker checks remain in place.

The context-alias consumer also needs corrections of the selected facts. Migration
0211 adds `security.read_observation_facts`: private, indexed candidate lookup
follows the requested catalog, revision and five-part fact key. One materialized
call to the existing exact reader authorizes all candidates. Only facts with an
original requested anchor in that same authorized set can return their siblings.
An invisible raw anchor never grants access to another visible revision. No
second visibility algorithm or historical-permission lookup is introduced.

Migration 0223 also keeps each requested anchor lookup parameterized in the
history wrapper and both exact-input branches of the visibility core. A large
close must not make those joins switch to scans of unrelated history. The
2,605-anchor regression checks tuple work under custom and generic plans;
adding another 2,605 unrelated facts leaves the measured work at 7,855 in both
cases. This qualifies bounded work for that synthetic request, not a hosted
latency guarantee. Installation refuses changed helper definitions, owners,
ACLs or configurations; the visibility predicates and publication checks stay
in the retained functions.

Migration 0225 also drives close-pin, knowledge-event and knowledge-receipt matching
from each admitted candidate through their existing exact indexes, and deduplicates
requested IDs once for the visible anchor gate. It retains the complete-close check
and the single authorized sibling set. The owned batch regression compares complete ordered rows to ordinary RLS
for 32,768 distinct pins, corrections and duplicate requests under custom and
generic plans, within a five-second statement deadline. This is a synthetic
correctness and scaling check, not a hosted latency qualification.
The publication-pricing calendar check exercises the same exact-reader path under
a certified historical context, including the final publication series.

The existing reader checks its complete policy/SELECT contract, current
authority, retained context, newest close and worker claim before publication.
The history wrapper also retains its original authority deadline; a later grant
cannot extend an in-flight read. Candidate construction and final-authorized
anchors share one data statement. Nested volatile functions are not claimed to
freeze their own snapshots; concurrent-close and correction checks qualify the
boundary explicitly.

Calendar frame reads use the exact reader for each requested frame. Optional
series selection filters the returned display samples after complete-pin
visibility is counted, so a hidden pin outside the displayed series still makes
the frame incomplete. Frames remain ordered, and the unfiltered path continues
to deliver one frame payload at a time. Bulk correction-history and window
consumers retain their separate reads; neither a general latency claim nor a
financial calculation change follows from this increment.

Each exact-frame batch captures one native authority deadline before its data
statement. A subquery barrier evaluates the frame payload before comparing the
database clock with that original deadline; an expired payload is refused before
it can be yielded. A pre-existing later-starting grant can authorize a fresh
batch, but cannot extend this one. This does not claim that nested volatile
functions share one latest-close snapshot.
