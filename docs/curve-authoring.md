# Curve, contract and price-publication authoring

Status: implemented locally (D363), with authenticated HTTP, managed, stock
PostgreSQL and browser qualification. No production deployment is implied.

## Authoring and dependencies

The canvas presents eligible curves, exact dated contracts and published synthetic
outputs as objects. Selectable connections expose operations, weights, matching
rules and scope. The advanced graph and formula remain views of the same managed
recipe. TypeScript presents authoring inputs and backend receipts; it does not
resolve financial dates, aggregate prices or apply shocks.

Whole-curve relationships declare their matching rule and resolved membership.
Exceptions explicitly replace mappings or add contributions; overlapping,
incomplete and unsupported mappings refuse. Native contracts, synthetic output
points and interval series retain distinct supported operations.

Published synthetic outputs remain exact graph references when reused. Publishing
changes creates a new version without rewriting existing consumers. Prompt shocks
resolve exact dated contracts under the selected context and reprice active
dependencies. Frozen actuals remain unchanged. Weights and offsetting paths, not
reachability alone, determine the numerical response.

## Contract dates and settlement

Contract price terms explicitly select a date mapping and settlement source.
Same-delivery-period matching is a named choice; custom mappings retain exact
source/target relationships. Connection mappings and settlement observation
periods are separate. The backend returns resolved dates and identities for review.

Settlement selects the exact exchange contract's official settlement, an exact
publication index with its observation/aggregation convention, or derivation from
selected inputs without inventing a published benchmark for the synthetic.
Commodity filters eligible indices but does not identify them: location, product,
delivery timing, statistic, quote and publisher symbol remain explicit. Monthly
bidweek and an average of daily assessments are different terms.

Last trading, delivery, expected release, actual publication, receipt/knowledge
and payment are distinct. Expected time never manufactures a fixing. Corrections
are immutable revisions; earlier selected pins remain reproducible.

A future pending-price policy would require explicit selection and retain an
exact eligible same-contract estimate with its original time and lineage.
It must not become an official final, imply no economic uncertainty, roll to a
different month or silently change dependencies. Until that policy has a qualified
connected consumer, D257 refusal remains authoritative. The UI cannot present an
unimplemented policy as executable.

## Price publications

`PricePublication` distinguishes external market publications from the trading
knowledge-order ledger. Publisher and publication versions reference exact
commodity/index/calendar/clock definitions; each publication may supply many
indices. An index's own commodity reference is authoritative.

Feed bindings describe transport and provenance, independently of the benchmark.
Prices reuse immutable market observations and their correction chains. Dated
fixing bindings contain schedule metadata, not another copy of actual prices.

The managed publication contract requires assessment-window and coverage
resolution, expected release schedules, normalization/validation, and explicit
stage/revision rules. The bounded gas implementation consumes supplied,
calendar-resolved occurrences without inventing holiday conventions. Crude, NGL,
LNG and unqualified power placeholders refuse executable operations.

HTTP retrieval, current authorization, certified knowledge and atomic idempotent
writes remain application responsibilities. Publication rules do not own exchange
expiry or payment. Existing economic fixing boundaries are not silently converted
into polling estimates.

## Qualification

Checks cover exact mappings and exceptions; nested shocks; expired versus fixed
states; preliminary/final admission; publication and knowledge cutoffs; correction
and retry identity; commodity/quote compatibility; exact version references;
current authority and historical reads; placeholder refusal; browser editing,
context changes and uncertain-write recovery. Each checkpoint names its consumer,
checks run and remaining limits. No live feed or production activation is implied.

The application now opens the Objects canvas by default. Its selectable operation
connections edit the existing managed graph, and source/contract cards open the
exact saved version's pricing terms. Contract pricing and Publications share the Price setup view in Curves & prices.
Date rules remain in the existing Schedules & date mappings workspace. Unconfirmed
writes retain their exact request and payload across retry; navigation protects
unsaved edits. Price setup is a human editor; it does not expose the older
date-definition assistant report as if it described publication or pricing terms.
The Curve builder retains its existing assistant operations.

Publication migrations 0218 and 0220 retain immutable definitions, scheduled
occurrences and normalized final-release receipts. Ingestion writes the existing
index-fixing observation store and preserves correction predecessors and original
observation time. The publication editor displays recent receipts bounded by the
selected market and certified knowledge coordinates. Preliminary observations
cannot satisfy the final-only consumer.

Migration 0219 retains source-node pricing terms and exact source/target pairs on
each custom-curve version. A single-contract exception changes only that target;
default changes preserve explicit exceptions. An explicit whole-curve mapping
that conflicts with an existing exception is refused; that exception must be
edited in single-contract scope. Shifted mappings keep output
delivery coverage separate from the original source contract and risk inputs.
The connected graph consumes these terms in ordinary previews and published
synthetics; they also round-trip through Formula.

The qualified baseline has explicit publisher-calendar occurrences for daily and
monthly gas, one final assessment per covered period, and a require-final policy.
It does not generate a vendor's yearly calendar, implement daily-average monthly
settlement, connect a licensed vendor feed, or execute the other commodity
placeholders. Those choices are not presented as executable policies. Existing
legacy fixing boundaries and D257's expiry-gap refusal remain unchanged.

Reproduce the combined API and website check with the installed SDK, stock
PostgreSQL and Chromium paths:

```sh
python3 tools/run_preview.py --pg-bin /path/to/stock/postgres/bin \
  --dotnet /path/to/dotnet --price-authoring-check \
  --browser-check --browser /path/to/chromium
```

This owns and removes its disposable database. The regular verification runner
also includes publication/terms storage checks and the combined authoring check.

The browser checks operate the production controls against that database and
verify server responses and saved readback. The authoring suite covers contract
and source selection, explicit mappings and exceptions, settlement choices,
publication membership and quote selection, assessment windows, covered periods,
save/reload, corrections, read-only contexts, stale-version conflicts and exact
request recovery. Accepted responses are checked against the original command
before an editor clears its retained request. Navigation and browser-close guards
cover typed and structural edits, including the Date rules tab.

The Objects control suite runs with `--custom-curves-check --browser-check`.
It covers source and contract cards, connections and cancellation, operation
settings, weighted rows, dynamic input ports, output selection, pricing-term
links, preview/save/publish, exact published-curve reuse, Advanced/Formula
round-trips and narrow-screen pointer access. These checks establish control and
backend wiring with synthetic data; they do not qualify every possible graph or
external feed. For focused reproduction, add `--authoring-browser-suite` with
`curve-objects`, `contract-pricing-controls`, `publication-controls` or
`price-authoring-recovery` to the appropriate owned fixture command.

## Publication release resolution (D364)

`published_release_v1` is an explicit settlement choice. A live contract resolves
its configured forward source. An expired contract without an eligible final
returns `awaiting_publication` with no price. Source-row inspection can display
that state beside other priced contracts; whole-curve valuation still refuses an
incomplete price set. There is no implicit last-price carry or interim estimate.

A final must match the exact publication version, index, observation source and
covered period. Its actual publication instant and certified receipt knowledge
must both be visible in the selected context. Preliminary releases never satisfy
this mode. Corrections become eligible only at their own publication and knowledge
coordinates. The selected final is an immutable dependency of that evaluation,
independent of the recorded forward close; the close itself remains unchanged.
Forward shocks therefore reprice live dependencies while selected finals stay fixed.

Native curves expose Edit or Inspect with the applicable restriction. Inspecting
an immutable native definition can start a separately named derived graph.
Published synthetic versions open their exact saved graph. Historical context and
missing permissions explain read-only controls. Curve builder, Contract pricing, Publications and the price catalogue use the
workspace's active catalogue. There is no separate population selector or sample
installation path. Saved graph sources retain exact version references; a reused
code cannot silently rebind an unchanged source node to another revision.

Regional close sessions remain independent of the catalogue. A session selects
the official close in force for its business day and the reader's authority.
A selected close keeps its recorded price inputs. Effective time and knowledge
continue to govern every read; selecting another regional session does not select
another set of curve definitions.

When publishing a new active catalogue, include all current business objects in
that catalogue and publish compatible graph versions and official closes through
the ordinary versioned operations. An unavailable exact version refuses; it is
never rescued by a timestamp-only frame substitution. Operational data-loading
scripts and their datasets are private, not public application features.
