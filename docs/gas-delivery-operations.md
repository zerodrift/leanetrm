# Manual gas delivery operations

D339 is the bounded local implementation contract for a manual delivery desk covering
before flow, scheduling, after flow and reconciliation. Live pipeline submission,
EDI/file connectors and automatic acknowledgments remain unavailable. Recorded
external evidence and application of that evidence are separate operations.

## Current consumer and ownership

Reuse retained booked gas obligations/native days, exact reference-data and party
versions, existing daily quantity revisions, facility allocations, daily cash
and the shared Blotter retained-command/review mechanisms. Contractual PV remains
its existing explicit measure. New operational cash is not included in that PV,
posted to accounting or dispatched to a bank.

A versioned gas service account identifies an external service-account reference,
its exact booked physical anchor, pipeline/zone/product/unit and holder (the
anchor's legal entity or counterparty). Independent gas-operations capabilities
are required in addition to current anchor-trade/source rights. Ordinary trade
visibility does not grant access to operator-account statements. Account identity
and unit/scope cannot be changed to mint another balance pool. Versioned terms
state their supported native-day range, agreement/terms locator and explicit
statement-input policy. No real pipeline tariff is selected by the application.

## Nomination requests and scheduling

A submitted request retains its account version, native day, submission time,
channel, external reference, requested absolute quantity, prior request and
existing nomination/confirmation/schedule heads. Before-flow, intraday and retro
classification follows the reported submission time against retained UTC bounds.
It never changes recording/knowledge time to pretend the request was known sooner.

Recording submission appends the nominated quantity using the existing native
operation. Counterparty and pipeline responses retain separate external reference,
response time, decision, quantity and evidence. Received responses do not silently
change confirmation/schedule tracks. Rejected, withdrawn and superseded requests
remain inspectable.

Under the explicitly selected manual confirmed-ceiling profile, applying an
accepted result requires a positive counterparty decision, a positive pipeline
decision, no withdrawal and a scheduled total no greater than requested/confirmed.
Zero is an explicit valid total. Preview/application fence exact response heads,
account terms, request and existing quantity heads. Applying appends confirmed
and scheduled revisions atomically; it cannot overwrite a newer independent
schedule. Revised responses can be applied through an explicit linked application
revision when the retained predecessor heads still match. Recording an external
response is not proof that the application can adopt it.

## Statement balances and after-flow imbalance trades

A statement records a signed balance for one account/native day: positive means
excess gas owed to the holder, negative means gas owed by the holder. It retains
its external statement/reference time, provisional/final/withdrawn status and an
explicit exact movement-prefix reference already included in its reported value.
A null prefix explicitly excludes all recorded transfers; an omitted choice is
invalid. Current balance is reported balance plus only later accepted movements.
This prevents counting an operator-posted transfer twice. Source revision or
withdrawal never deletes accepted transfers or recreates consumed capacity.

Service terms explicitly select final statements only or reported provisional/
final statements for this consumer. Missing/withdrawn/ineligible statements are
unassessed, never zero. This balance is independent operator evidence; it is not
inferred from a facility allocation residual or from hedge positions.

The first imbalance-trade profile transfers a positive quantity between a buyer's
negative and seller's positive compatible balance. It requires one owned and one
counterparty account, both currently authorized, exact compatible pipeline/zone,
product/unit/native bounds and an explicit fixed price, quote/currency rounding,
agreement time and due time. It reduces both existing imbalances without crossing
zero. Counterparty/pipeline acknowledgments are recorded separately; a reviewed
application rechecks current statement and movement heads before atomically
posting equal/opposite quantity movements and independently signed nominal cash.
The executed agreement, flow period and recording/knowledge time stay separate.
This operational commercial record does not create a new portfolio instrument or
claim inventory/title recognition. Cross-zone/unit/FX netting, general inventory,
external payments and automatic cash-out remain unsupported.

Synthetic oracles: seller +250 and buyer -200, transfer 200, yields +50 and 0;
a price of 3 produces a 600 cash obligation with the owned side's explicit sign.
A new net statement of +50 that includes the seller's accepted movement still
yields +50. A correction to the pre-transfer seller statement to +150 produces
-50 after the retained outgoing 200; it preserves the breach and blocks further
outgoing allocation. No clipping, inferred fuel or automatic reversals apply.

## Persistence and qualification

All identities and core relationships use exact relational FKs. Versioned facts,
responses, applications, movements and command receipts are immutable. Retries
return their first outcome; fresh semantic duplicates return the existing fact,
including after later heads have advanced, without reinstatement. Source updates
append revisions under explicit predecessor/head checks. Reads, previews, joins
and agent operations retain the header As of and resolved knowledge cutoff;
current permissions govern every operation, including recovery after waits.

The desk composes the existing native daily/facility/cash consumers. New services
and request/balance commands use the same managed validation, preview and
idempotent persistence from UI and approved agent proposals. New capabilities
are not granted to existing production roles automatically. Synthetic examples
receive explicit isolated fixture grants.

Local qualification covers preflow/intraday/retro request
classification; missing/rejected/late acknowledgments; schedule cuts and stale
heads; historical views; balancing-source coverage and revisions; opposite-flow
conservation; signed cash/rounding; concurrent acceptance; semantic duplicate and
lost-reply recovery; source privacy; and current authority expiring during real
storage waits. See the [verification record](verification.md) for actual results.
Existing gas, facility, crude, swap and assistant contracts retain their own
qualification boundaries. No production deployment is implied by this contract.

## Using the manual desk

Open **Operations → Physical operations**, select Natural gas and the booked
instrument, then **Delivery**. Choose a retained native delivery day. This is the delivery period being operated on; the header
As of and knowledge cutoff remain authoritative for every source read. Service
versions have no picker: the latest eligible version follows that header context.
Each field explains the explicit contract it supplies; a sole reference choice
is selected visibly. Missing financial terms do not become defaults.

Before flow contains service-account terms and requests submitted externally.
Scheduling records counterparty/pipeline decisions and separately applies an
accepted response pair. After flow records native allocated quantities and
operator statements, with accepted facility allocations shown where present.
Reconciliation offers explicit fixed-price imbalance agreements and application,
while **Billing & corrections** contains existing daily gas cash determinations. Native allocation entry retains its
existing AmendTrade authority independently of private gas operations access.

New setup requires ConfigureGasServices and ReadGasOperations; other private
writes require RecordGasOperations, ReadGasOperations and AmendTrade. All retain
ordinary current trade/source rights. External acknowledgments can still be
recorded against their exact old terms after a service successor/retirement;
adoption and new requests require the current active terms. Retiring a source
never removes previously accepted movements.

The explicit synthetic launcher `--with-gas-operations-demo` includes the basis
and history example, creates two additional booked anchors and records the manual
scenario. It grants the new roles only in that disposable fixture. It does not
activate permissions on an existing installation. `--gas-operations-check`
qualifies the consumer in a fresh stock PostgreSQL database; add `--browser-check`
with an installed browser to exercise review and request recovery.

D342 replaces the delivery-day dropdown with all retained side/day rows in a
sortable/filterable grid. Select a row before operating it; its exact obligation
is retained for service and quantity entry. Native recorded track/status values
provide the display colors, without inferring operational completion.
