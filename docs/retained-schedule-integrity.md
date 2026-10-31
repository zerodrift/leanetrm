# Retained schedule and observation integrity

Retained schedule generation and paged persistence verification use the same
`ScheduleManifestWriter`. It owns the canonical header and six typed content
sections: rows, edges, bindings, roots, ports and grids. Empty sections remain
encoded, page boundaries have no effect on the digest, and sections cannot move
backwards. The existing manifest format and hashes are unchanged. Persistence
verification still performs authorized page reads and exact row comparisons;
sharing serialization does not share or bypass authorization.

The writer belongs to the trading domain because the order and serialized types
are part of the retained schedule contract. Its single incremental hash stream
has no separate consumer that warrants a general shared framework. Numerical
values, reference keys and source bindings retain their existing exact encodings.

Migration 0149 pins the contract observation terms, explicit roll boundaries and
selected observations to the exact definition/reference tuple in
`market_data.revisions`. Each table now has a composite foreign key over
`catalog_id`, `definition_revision_id`, `reference_catalog_id` and
`reference_revision_id`. The columns are non-null, so a prompt selection's null
`selected_contract_id` cannot bypass definition existence through PostgreSQL's
nullable composite-key behavior. A separately valid reference revision cannot
be substituted for the one belonging to the selected definition revision.
Additional composite foreign keys bind each observation term to its owning side
period's complete snapshot and bind roll boundaries and selected observations to
their owning terms' snapshot. Independently valid bundles cannot be mixed across
these parent/child relationships. Fixed periods may retain a null definition
revision; contract observation terms are non-null and reference their own
floating period. A separate composite key binds each selected observation's
series to its selected contract; two independently valid series/contract IDs
cannot be combined into a false observation relationship. Source eligibility,
curve policy and quote compatibility remain validated by the booking operation;
this migration does not replace those financial rules with reference tables.

Existing protocol names and model versions remain explicit versioned contracts.
Reference codes that are already members of exact composite foreign keys remain
codes; replacing those keys with unqualified strings would weaken the contract.
The migration validates existing records and fails if any mismatch exists. It
does not rewrite historical snapshots or infer missing reference versions.
Validated foreign keys and supporting unique indexes scan/lock their existing
tables. The migration has explicit lock and statement timeouts; an oversized or
busy installation must schedule the migration appropriately. Failure rolls the
transaction back rather than publishing partially enforced relationships.

Managed retained-schedule checks cover an independent literal encoding, baseline
serialization parity, paged input, changed content, section ordering, completion
and cancellation. The scheduled-swap integration check exercises ordinary prompt
booking and the installed snapshot foreign keys. It tests direct invalid
mutations of a stored prompt term with rollback-only suspension of the immutable
trigger, so FK failures cannot be confused with append-only policy rejection.
The cases include nonexistent definitions, mismatched references and a different
valid bundle than the owning period. Separate transaction-scoped probe tables exercise
each installed FK independently, including wrong catalogs and child snapshot
mismatches; these are isolated constraint tests, not application write paths.
A stored observation also rejects substitution of another valid contract's
series. Direct parent-update checks accept any enforcing FK in the exact
snapshot chain because PostgreSQL does not promise constraint trigger order;
the isolated probes verify every named relationship independently.
Rollback restores all data and trigger state. These checks complement existing
schedule staging, publication and historical replay checks.
