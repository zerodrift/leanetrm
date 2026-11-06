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
