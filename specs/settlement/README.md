# Monthly gross settlement executable specification

Status: **ACCEPTED for the explicitly opted-in bounded fixed-strike contract**,
not a production settlement implementation. The exact
[SP1–SP9 policy wording](../../docs/ppa-settlement-spec.md) is authoritative for
review. Rounding, approval and adjustment due-date conventions are accepted in that
bounded scope; other contracts are not implicitly opted in. The reference model
and expected answers remain unchanged. The separately authorized
[meter-finality prerequisite](../../docs/meter-finality.md) does not implement cash.
The reference model imports no application code and performs no database, network,
API, worker or filesystem operations. It is not included in the production gate.

Run from the repository root with Python 3.11 or later and IANA time-zone data:

```sh
python3 -m unittest discover -s specs/settlement -v
```

The suite has **36 checks**, passing with Python 3.14.2 on 2026-10-22. Expected
cash values are literal rational amounts; arithmetic uses standard-library
`Fraction`, and calendar construction uses `zoneinfo`. No binary floating point
or production rounding/quantity implementation supplies the expected answers.

## Acceptance cases and scope

| Area | Executable cases |
| --- | --- |
| Whole-month numerical oracle | November 2031 New York has 721 hourly cells; 28,840 contract MWh at pay-fixed 40 gives −1,153,600. One hourly cell 50→51 whole-asset MWh gives −32; changing all 721 gives −23,072. |
| Calendar and quantities | Repeated fall hours are distinct UTC cells; spring day has 23 hours; 2,884 quarter-hours preserve energy without duration multiplication; sum asset MWh over sum same-asset capacity; missing versus explicit zero; positive denominator; retained capacity version changes. |
| Amendment occurrence | One selected trade head, original prefix and replacement suffix at original MW/strike; canonical monthly occurrence and contribution keys survive amendment; terminated suffix excluded; gaps, overlaps, unmapped sides and incompatible due/currency/direction/parties refuse. Complete-plan omission refuses. |
| Finality | Explicit publication required; unverified raw correction cannot replace final head; both temporal coordinates constrain publication visibility; suspension/withdrawal blocks fallback; duplicate readings, broken predecessor chains and invalid cells outside selected coverage refuse. |
| Cash conventions | Exact aggregation before one ties-to-even cent rounding; adjustments use differences of rounded cumulative totals; original accepted amount immutable; successive reversal correction; zero correction advances evidence; zero original owns contributions. |
| Authority and approval | Distinct controlling principals, alias rejection, explicit expiry and equality, 24-hour maximum, current maker/checker/executor grants, principal grants, book scope, active delegation and exact approved content. Failed transition consumes nothing. |
| Financial identity/recovery | Same actor/request exact replay survives newer heads/expired approval; changed content conflicts; unauthorized replay refuses; new command cannot duplicate financial action; occurrence alias cannot duplicate contributions; expected-revision conflict; publication/acceptance serial orders. |
| Valuation partition | Claimed contributions excluded exactly once; accepted rounded cash remains unchanged by unaccepted corrections; explicit discount; acceptance visible on both clocks; due equality is known overdue; opposing overdue cash netting to zero is still incomplete; zero acceptance has no payment right. |
| External payment evidence | Full original payment leaves later adjustment outstanding; effective and knowledge visibility; unknown, partial, excessive, wrong-sign or duplicate payment facts refuse. No payment operation is implemented. |

The lifecycle cases use two retained hourly cells of a trade terminated early in
a **completed** November month. The whole-month and quarter-hour numerical cases
are separate full-grid checks. This avoids misrepresenting a two-cell lifecycle
fixture as a long-workload or full-month persistence qualification.

## Trusted boundaries and remaining implementation work

`Family`, `Binding`, `PlannedOccurrence`, `Actor`, source facts and proposals are
immutable **trusted reference-model manifests**, not client DTOs. The future
operation must resolve exact versions, unique lineage/successor mappings, source
rights, principals and context using authorized reads. `plan` selects one trade
head and retains all required contribution keys; `preview` consumes that whole
manifest and checks completeness/context. Directly fabricating all manifest fields
is outside this model's input boundary, not a supported way to bypass validation.
Future production APIs must build these manifests internally.

Binding asset values are already converted to the explicitly selected stage and
carry the capacity version in force for their interval segment. The specification
checks the resulting normalization and changed-capacity segments; it does not
implement stage conversion, unit catalogs or database capacity-version lookup.
Existing Phase C implementations remain responsible for those operations.

Authority snapshots stand for already resolved current book/source/reference
access; the small actor model exercises principal separation and current scope.
Finality publication facts are already certified append-only evidence; this model
selects/validates them but implements no verifier operation. Proposals must be
built through the preview boundary; acceptance does not independently reprice
trusted immutable proposals. Exact structural equality stands for the complete
canonical payload, not an unspecified hash-only identity.

`PaymentEvidence` is externally certified synthetic S4 input, naming one accepted
nonzero obligation and its complete signed amount. It carries effective and
knowledge times and discharges only that visible named amount. It is not an
implementation of approval, allocation, reversal, banking or settlement payment
recording. Partial payments are intentionally unsupported in this specimen.

`value_partition` is a small known-cash oracle with an explicitly supplied discount
factor. It demonstrates ownership and accepted/overdue cash partitioning. It is
not a production valuation adapter, term-structure resolver or reimplementation
of Phase C estimated-overdue, physical-delivery or certificate-risk calculations;
those remain unchanged and their prior checks remain applicable.

Immutable state transitions specify atomic success/refusal and both serial race
orders. They **do not qualify** actual concurrent transactions, RLS, durable commit,
locks, distributed retries or workers. Those require future implementation tests.
Host IANA calendar data supplies the civil grid; product clock/version pinning
remains a future integration requirement. The source reuse map, gas prerequisites
and operation order are in the policy document. No global S0–S5 settlement phase
is completed by these checks.
