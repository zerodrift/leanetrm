# Facilities and delivery workspace

Status: implemented and locally qualified D322–D324 delivery workspace and D327 native assistant operations. Hosted activation remains separate.

**Operations → Facilities & delivery** combines the existing gas setup, crude
setup and facility quantity workflows under one sidebar entry. Delivery setup
selects the commodity family; Quantities & allocations opens the shared facility
ledger, including the D321 gas demo's explicit final-input acceptance and
withdrawal. The workspace remembers its last section and delivery family during the
visit. An account reset clears that navigation state. Existing page restoration
continues to check the selected section's authority.

Natural gas starts the delivery setup view. This is a visible navigation choice,
not a financial convention or a new booking default. Gas retains its delivery
paths, pricing groups, meter assignments and usage tariffs. Crude retains its
grades, facilities, custody points, standard-volume terms and pricing bindings.
The existing editors perform their normal draft, validation, publication and
request-recovery operations.

## Delivery setup fields and context

Gas and crude dropdowns carry definitions returned by their native catalogs.
The shared field presenter puts each definition below its control and associates
it as an accessible description. Future delivery editors must supply definitions
for every selectable term through the same catalog/presenter contract. These
descriptions document supported behavior; they do not choose financial defaults.

Published gas setup is initially read-only. **Edit setup for this context** makes
an editable copy; it does not save or publish it. **Check saved draft** checks
the exact unpublished proposal, including pricing relationships and meter
coverage over explicitly entered delivery days. This structural authoring check
reads no market prices. It remains a working-context operation. Publication
remains a separate authorized operation.

Crude **Calculate delivery value** uses a published binding resolved by the
header context, an explicit delivery interval and a positive total volume. The
backend binds the header's certified context once, rejects a setup outside its
resolved published heads and uses that same context through native valuation.
Historical knowledge supports this read while authoring stays disabled. The
delivery interval must follow valuation As of; it does not replace that As of or
the knowledge cutoff. Separate consideration and fees are excluded from this
undiscounted delivery value.

An unresolved or unapplied header selection withholds setup inspection instead
of calling a raw catalog. Context invalidation clears old form values, selectors,
labels and results. Current private drafts remain explicitly identified authoring
proposals. Only drafts based on the context-resolved publication are offered for
new actions; a new crude binding requires no existing head for that logical code.
An uncertain command retains its original identity and terms. If its recovered
outcome is outside the current context, the UI confirms the receipt identity and
restores contextual terms without opening the superseded configuration. Exact
backend version/history operations remain available.

The D327 setup assistant reader uses these same native catalogs and definitions
under current authority and the selected temporal context. **Settings → AI**
provides Providers, Models & prices, Agents, Page prompts and Usage screens.
Reviewed page guidance and admitted conversation history remain separate;
old turns are not verified financial expertise. No new memory table or automatic
promotion of conversation text into financial rules is introduced here.

### Starting new entries

D324 selects a sole required dropdown choice on a new entry and opens a sole
published crude binding for inspection. Multiple choices require selection.
The selection primitive is shared with swap entry in `choice-selection.ts`;
delivery controls retain their new-entry and optional-choice guards.
Gas's optional unpriced/no-group value remains a real alternative. Saved terms
and existing proposals are displayed exactly; reopening them does not fill
missing values or change their financial meaning.

New gas rows receive native catalog starting values: zero point/group adders,
one consistent contextual gas-day convention, and shared meter/tariff validity
dates when unambiguous. New rows still require their own identities and rates.
A single meter period within the 31-day limit initializes the coverage check.
New crude bindings select sole reference/source/clock/policy choices and can use
one future source month for initial validity dates. The backend qualifies that
source with the same monthly-forward, non-interval and delivery-window rules as
native crude. Several eligible months or no eligible source leave dates empty.

The forms identify starting values as editable and require review before saving.
Explicit date edits or clears survive source changes and command refreshes.
Context reset discards those presentation suggestions with the old form. No
price, fee rate, total volume, identifier or binding code is guessed, and the
header's temporal context is unchanged. Defaults do not save or publish anything.

The common navigation opens ordinary authorized pages after their existing
unsaved-edit guard. It does not select another tab before that guard completes.
Only the selected section reads its catalog; the workspace does not prefetch
hidden commodity or facility data. Selecting the already open sidebar entry
preserves the current view. Commodity and task buttons remain explicit navigation
actions, with normal current-authority validation.

Delivery setup remains available to market-data readers. Full facility pools
still require market-data, trade and facility-quantity read permissions. The
workspace does not add pool rights or broaden any read/write operation. Gas and
crude retain their existing setup/market context; facility quantities retain the
business date and resolved knowledge cutoff. Trade inspection from an allocation
still needs its independently selected financial close.

The facility workflow exposes reported quantities, allocations and operating
limits. Its current executable allocation consumer is booked gas delivery.
Consolidating the screen does not implement crude, LNG, NGL or power allocations.
The same exact reference-backed locations remain in use. No facility master,
regional classification, pricing mapping, table or dependency is added.
Region filters require explicit reference metadata; location names and commodity
families must not be used to guess a region or create economic relationships.

Backend and assistant page identities remain `gas_setup`, `crude_setup` and
`facility_quantities`. Consolidation adds no assistant operation or permission.

## Facility and transport coverage

The workspace shows an initially open **Facility & transport workflows** panel.
Pipelines open the existing gas delivery setup and crude delivery sites open the
existing crude setup, through the same authorized navigation and leave guards.
The panel can be collapsed while working in an editor.

Gas storage, power plants, rail/trains, barges, tankers, trucks, LNG terminals,
processing plants, terminals/ports and bulk storage/tank farms are gray disabled
controls marked **Planned · TBD**. They issue no read or command and remain disabled
during navigation, busy-state changes and account changes. These are presentation
placeholders, not executable profiles or reference records.

Existing fixed-location identities and their type relationships remain in
`ref_data.locations` and `ref_data.location_types`, inspectable through Reference
data → Locations. Gas paths retain their exact pipeline/zone/delivery-point
relationships. No vehicle, facility master, transport relationship or seeded
location is invented for a planned workflow. Each future consumer requires its
own qualified domain contract and exact references.

## Verification

The frontend suite passed 1,348 checks. Owned stock-PostgreSQL workflows passed
19 gas browser checks, 21 workspace/crude checks and 20 facility checks, alongside
their native authorization, history, validation and request-recovery checks.
The API build has zero warnings/errors. Desktop and phone screenshots were
inspected. Independent review found no outstanding integration issues.

The facility-coverage checks use the actual workspace markup and the real app's
loading/navigation lifecycle. Planned controls stay disabled after global busy
updates; existing setup cards retain their native permission and edit guards.

D323 qualification passed 1,355 frontend checks, including seven new delivery
definition/context/reset/recovery checks, 20 gas browser checks and 22 combined
workspace/crude browser checks. Native checks distinguish the 81,000 current
delivery value from the 80,000 value under a pre-correction header cutoff, refuse
a superseded binding in contextual preview and retain legacy exact-version reads.
The API build has zero warnings/errors; independent temporal review found no
outstanding material issue. Tests use isolated synthetic data and stock PostgreSQL.

D324 passed 1,358 frontend checks, including new-entry singleton selection,
native suggestions, ambiguous choices and explicit-clear retention. Managed
checks cover consistent/mixed gas conventions, coverage limits, duplicate source
months, futures/interval refusal and the native crude horizon. The same 20 gas
and 22 combined workspace/crude browser checks passed, including native saves,
publication/retry and source-based starting dates. Independent review has no
outstanding material finding.

## Physical assistant operations

D327 exposes the native gas and crude setup catalogs, field definitions, structural
checks and reviewed authoring/publication commands through the existing delegated
workspace tools. Setup reads resolve published terms at the question's exact
context and omit raw version history and private authoring proposals. Crude
value preview retains the same managed valuation and exact source references.
Unsaved browser edits are not treated as saved terms.

Gas daily quantities use the ordinary deterministic selection policy and exact
trade context. Facility directory, selected ledger and allocation preview use the
header operating date and a certified knowledge cutoff. An immutable
`page_report.businessDate` identifies a facility question without a price frame.
A correction published after admission cannot enter that question's reads.
Historical questions preserve their retained facts and cannot prepare writes or
working allocation previews. Directory choices and working previews use an
application-only marker under the same native permissions; writes receive no
such exception.

Save, publish, daily recording and facility commands require the existing separate
human Review/Authorize action. Native validation, exact references, expected
revisions, source authority and operation receipts govern execution and retries.
A proposal or review alone persists no business change. Existing agent versions
and grants are not automatically replaced or expanded.

The existing versioned page-guidance facility accepts gas setup, crude setup and
facility pages. Reviewed guidance can document supported terminology and
procedures; source values still come from contextual native reads. Conversation
history cannot approve itself as expertise or supply absent financial policies.

Facility navigation targets and presentation plans from dated questions remain
unavailable pending a qualified date-aware navigation event contract. Existing
framed setup navigation remains available. Back from an older answer preserves
the facility origin's explicit working date and selected ledger, with no price
close inferred.

The owned synthetic qualification runs with
`tools/run_preview.py --physical-assistant-check --browser-check` and the installed
stock database, pinned SDK and browser arguments. Its 29 business scenarios cover
late corrections across admission, retained history, wrong-date refusal,
conserved allocation previews, approved authoring and daily corrections,
retry receipts and native permission parity. Six browser checks cover delayed
older-answer navigation, explicit Go/Back and a facility question with no price
frame. These establish the bounded local contract, not production activation or
settlement-adjustment capability.

### Late corrections and settlement adjustments

Quantity/source and allocation revisions retain their effective period and their
knowledge/publication identity. A later correction does not rewrite earlier
certificates, accepted allocations or settled cash. The assistant can inspect the
revisions and prepare supported operational corrections; automatic invoice
adjustments are not implemented by this increment. D338 adds a separate
[reviewed daily gas cash determination](gas-cash-billing.md), accessible from a
booked gas instrument's Billing & corrections tab in Physical operations. It retains accepted source links and signed
corrections; invoice/credit documents and accounting remain subsequent consumers.

For settlement-adjustment consumers, keep a small explicit cause
classification: late actuals, quantity correction, allocation correction, price
correction and fee correction. Whether the affected period or invoice is closed
is a separate lifecycle fact; a late reading alone does not establish a
prior-period accounting adjustment. Reuse governed reference-data conventions
when a configurable consumer requires them. D338 currently admits only typed
late-actual, quantity and allocation causes under its installed profile; price
and fee corrections remain unsupported. No classification master is introduced. Debit/credit adjustments must link the
original determination, corrected inputs and explicit delta without overwriting
historical settlement.

D337 defines the [accounting policy and standards-update boundary](accounting-policy-boundary.md)
for this future consumer. Commercial correction and accounting treatment remain
separate; source references and policy versions are retained independently, and
updates cannot rewrite posted history. This is a design contract, not an
implemented journal or standards-compliance engine.

## Manual gas delivery desk

The separate **Physical operations** sidebar page contains the
[manual delivery desk](gas-delivery-operations.md). Booked gas instruments have
Overview, Delivery and Billing & corrections tabs. Four stages compose service
setup/submission, external responses/adoption, actual allocations/statements and
reconciliation/billing. Delivery-day inputs select the period being operated on;
all setup, source and statement reads remain pinned to the header context. Current
service terms follow that context without a version dropdown. Live transmission,
partial imbalance reversals and portfolio imbalance instruments remain TBD.


## Instrument inspection and operational work (D340)

The Blotter inspects pricing, immutable instrument terms and read-only side
schedules. It never mounts physical submission, statement, allocation or billing
forms. Physical instruments open with a Physical overview; gas, crude and LNG
present their exact saved pipeline/zone/point, custody/standard-volume or
cargo/Incoterms conventions. The physical power consumer uses its explicit energy
delivery membership to label its overview. This is presentation composition,
not a new commodity inheritance hierarchy or a financial-policy default.

Physical operations lists booked gas, crude and LNG metadata independently of PV.
Its directory reuses native inventory/source completeness checks and certified
pagination. Missing held metadata refuses the directory rather than erasing the
instrument. Gas has a four-stage workflow and a separate billing tab. Crude reuses
its existing certified receipt/inventory/dispatch/invoice/payment workflow. LNG
conventions are inspectable; its operational workflow and unsupported commodity
families are visibly TBD. Facilities & delivery retains facility setup and shared
quantity allocation scopes.

The workspace owns one actor-scoped retained native command, separate from its
widgets. Leaving a tab, page or context asks before discarding edits; Keep preserves
the selected tab and form. An uncertain command retains its exact original payload
for status or retry, including when the header selection is unapplied. A result
clears it only after native operation, trade and result identities match; malformed
success or status responses leave it retained. Historical
views remain read-only and all native rights/source checks remain authoritative.

## Focused operations layout (D341)

Select a booked instrument in the Physical trades grid. The instrument
header retains its exact trade reference, book and exclusive delivery window
while Overview, Delivery and Billing & corrections present their own work.
Overview cards show saved terms, readable convention labels and explicit quote
units through existing shared formatters; the browser performs no calculations.

Delivery lists every retained physical-side/native-day row in its own grid. Four keyboard-accessible stage
tabs reveal the relevant manual actions; they are topics, not completion states.
An action opens its inline form, preserving field definitions and the separate
native review/accept steps. Activity & evidence shows the recorded facts across
all stages. Billing groups delivery/source, calculation and evidence/correction
inputs without adding a financial default.

Unfinished inputs block stage/day changes until retained or explicitly discarded.
A refresh removes focused inputs before starting its new read so native blur
changes cannot invalidate that read. A failed directory read clears prior
instrument metadata and tab roles; the actor-scoped recovery card remains
independent. Historical and unauthorized actions stay disabled under native
capabilities. The layout adapts to narrow viewports without new view-local As of
or knowledge controls.

## Trade and delivery grids (D342)

Physical trades replaces both selection dropdowns. It lists trade reference,
commodity, book, exclusive delivery bounds, pricing reference, booked quantity/unit
where stated, and the implemented workflow. Multi-side gas quantity is shown as
By side, with no invented aggregate. Future workflows remain visibly TBD.

The Delivery grid lists every native side/day with contract quantity, nominated,
confirmed, scheduled and allocated heads, selected input/track, source status,
quantity specification, location and interval bounds. Values are exact backend
text; the shared grid sorts decimal/fraction quantities without rounding. A blank
track has no record, a withdrawal remains labeled, and an explicit zero remains
zero. Colors reinforce textual commodity/status labels and claim no completion or
settlement state. Table search, column filters and sorting read authorized facts;
they never change header context or financial policies.

Select a row to open its work. Service/direct-quantity entry stays on the selected
obligation, and transfers preserve the owned-leg/counterparty distinction.
Changing delivery selection loads only that day's private state; it retains the
standing grid, focus and presentation state. A filtered-out open row remains
identified by the shared notice. Keep preserves a dirty row/form; an unavailable
source or context clears stale data without losing an uncertain request.


## Shared delivery intervals (D343)

Delivery grids use the [common native view](physical-delivery-view.md). Gas keeps
its independent side/day tracks and manual workflow. Crude and LNG show their
retained whole-window totals, directions, exact unit/custody references and source
addresses. Crude's existing operations remain below its grid; LNG actuals and
reconciliation display TBD. The common table accepts supplied interval geometry
and track names; new operational adapters remain explicit backend work.
