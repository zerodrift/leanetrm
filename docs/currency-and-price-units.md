# Exact currency and price units (D438)

Currency identity is the full reference catalogue, revision and code. Display
codes, names and symbols never establish equality. Money aggregates only within
one exact currency; no FX conversion or cross-revision denomination mapping is
implied.

## Ownership and integrity

- Plain monetary price indexes declare a required currency foreign key. Interest
  rates declare currency and their rate convention. Physical quantities and
  dimensionless factors do not acquire a currency.
- Derived prices inherit a complete `PriceUnit`: exact currency, exact quantity
  specification and quote multiplier. Explicit quantity conversion can change
  the denominator while preserving currency. Combining prices requires compatible
  units; independent output positions may retain different currencies.
- Migration 0380 adds a composite discount-default foreign key to the rate curve's
  exact source/currency tuple. Invalid populated configurations refuse the
  migration without inferred repairs or altered business facts.
- Migration 0381 validates native graph unit propagation at publication, including
  direct native calls and exact published parents. A bounded explicit traversal
  stack avoids database recursion. Managed publication also uses the existing
  mark-free topology under the selected snapshot/certified cutoff. Actual pricing
  continues to validate units under each execution context.

## Operations and presentation

`custom-curves/publish` requires `snapshotId` for the topology's business date.
Accepted-command recovery runs before ephemeral snapshot lookup, retaining the
original request outcome under current authority.

Option cash totals partition exact currencies; held lots partition exact series.
The Options chain selects holdings from its surface's exact reference revision.
Blotter price statistics partition price denomination independently of external
lot/capacity quantities. Money, quantities, prices and sensitivities retain their
own display kinds through the managed formatter.

UI prices show complete units such as `GBP/GJ`, including a non-unit multiplier.
Distinct currencies or quantity bases with colliding labels receive source/version
qualifiers. Schedule inspection, calculation steps, publications, P&L and operating
power/REC prices carry their actual units. CSV trade rows retain exact reference
fields; pivot cells retain their native group/reference identities; ladder rows
retain factor IDs and exact price units with an explicit book/measure/currency
column dictionary. Filtering or regrouping does not detach provenance from values.

## Payment rounding

New PPA payment lineages require `roundingTerms` with an exact `currency` reference,
positive decimal-text `increment`, `tieRule` (`to_even` or `away_from_zero`) and
`stage=payment_total`. The version is
`cash_explicit_increment_per_occurrence_v1`. Terms are immutable and participate in
idempotent comparison. Financial rounding occurs once after summing the complete
occurrence and preserves exact rational amounts, including totals outside bounded
working-decimal range. Display precision is independent.

Retained `cash_2dp_half_even_per_occurrence_v1` facts remain readable with their
original declared semantics; new lineages cannot select that policy. Retained terms
are read through the same authorized operation used by the UI and assistant.
Configured-currency cash preview needs its actual meter/index components; an
undiscounted calculation does not consume an interest-rate curve. Qualified USD
external feeds retain their explicit feed contracts. No new currency pricing,
PPA example or rounding convention is seeded by this change.

## Qualification and release

Qualification includes exact-key collisions, quote denominators versus external
capacity, multiplier partitions, signed increment rounding, large retained cash,
API serialization, direct native publication, mixed independent output currencies,
22 nested published parents without marks, populated mapping-upgrade refusal,
current authority, temporal correction replay and idempotent writes. Browser checks
cover managed display, exact held-series selection, explicit post-save knowledge
refresh, immutable controls and stale-response disposal. Executed local results
and their limits are in the [verification record](verification.md#exact-currency-and-price-units-d438);
local verification does not deploy the application.

Apply the additive migrations with the normal write-admission/drain boundary and
coordinate the API/UI release. Preserve existing exact references and financial
facts. No application reset or new generic context storage is required.
