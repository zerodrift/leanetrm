# Versions selected by workspace context

D316 makes the shared As of date and knowledge cutoff select ordinary displayed
versions. The UI offers logical objects and displays their resolved version as
read-only evidence. Exact-version backend operations, immutable history and
saved financial references remain available.

## Implemented consumers

| Consumer | Selection contract |
| --- | --- |
| Gas delivery setup | Existing certified `current_gas_setup` for the exact definition bundle; point, meter and tariff delivery coverage retains its separate meaning |
| Crude delivery bindings | Existing `current_crude_setups` per logical code; it selects the known publication before checking authority, so an inaccessible head cannot revive an earlier binding |
| Schedules | Latest admitted version under the existing effective intervals and certified publication knowledge; current private drafts remain authoring artifacts |
| Date mappings | Latest known definition, or a current private working draft; its validation date is not an applicability interval |
| Curve builder | Latest admitted authoring artifact; new graph ingredients use the latest admitted exact published reference; effective preview retains its activation timeline |
| Swap custom projections | Latest admitted published recipe before numerical qualification; failure does not select an older numerical result |
| PPA TOUs | Latest among the exact TOUs admitted by the selected bundle, including independently composed Side quantities |
| Trade tickets | Existing context-resolved revision with immutable event history |

Unpublished gas/crude proposals have immutable identities rather than a monotone
version number. They remain explicitly reviewable authoring material. The UI
never guesses draft recency from UUIDs or list position. A saved or recovered
command reads its exact proposal independently of the bounded history window.
Uncertain commands retain their original identity and pins. Proposal review is
separately bounded at 50 gas or 100 crude drafts; exceeding that limit refuses
the list rather than hiding proposals. New crude choices select a logical binding code.

A saved trade, graph, activation interval or PPA can retain a version different
from the one offered for a new choice. Retained values remain exact, disabled
choices where needed. Clearing a retained TOU leaves a required missing choice;
refresh cannot silently restore it. Context changes do not rebind contracts.

## Read and editing boundaries

Optional `snapshotId` catalog projections supplement the original raw APIs.
Malformed explicit snapshot identities refuse instead of selecting current data.
Curve definition metadata remains readable while an ambiguous close choice is
unresolved; pricing and save operations still require an exact selected frame.
Gas/crude contextual setup inspection is read-only under historical knowledge.
Their published heads are read separately from bounded raw history, so a long
history of one binding cannot hide another current code. Current reference and
market authority are rechecked over the complete response scope.

Changing context invalidates catalogs and editing rights. Applying it reloads
same-page schedules and mappings. Unsaved entries ask before being discarded;
the common schedule prompt stays outside both tab panels. Saved commands and
retained outcomes preserve their original version identities.

The frontend explicitly requests `preserve_origin_or_unique_asof_frame_v1` when
applying context. The backend retains the selected close's exact origin when it
remains eligible, or selects a unique offered close for a changed date. Missing
or ambiguous origins leave the choice empty. Explicit manual input variants do
not become another same-time close. Legacy context calls without this optional
policy retain their explicit frame-selection flow.

Standalone TOUs retain their current-only historical refusal. Reference display
editing retains its existing atomic commit operation and original historical
labels. Operational permissions and execution configuration keep their current
policies; financial history never restores past permissions.

This change adds no schema migration, financial default or production-data seed.
Owned checks cover publication/retry, earlier knowledge, exact retained pins,
cleared TOU choices and independent logical heads beyond bounded history.
