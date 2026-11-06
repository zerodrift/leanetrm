# Accounting policy, settlement evidence and standards updates

Status: local design under D337. This document defines the next bounded design
and acceptance contract. It adds no accounting engine, journal persistence,
standards-compliance claim, posting permission or production accounting policy.
Proposed object and method names below are not implemented APIs.

## Scope and existing foundations

Keep three responsibilities distinct:

| Responsibility | Authoritative facts and result |
| --- | --- |
| Physical operations | Retained delivery schedules, measurements, allocations and gas owed within an explicitly qualified balancing scope. A facility allocation residual alone establishes no transportation imbalance. |
| Commercial settlement | Contractual prices/fees and selected final inputs determine signed original/correcting obligations. Issued documents, external payments and open obligations retain independent evidence. |
| Accounting | An explicitly selected book and reviewed policy determine recognition, account mapping, posting period and journal treatment of eligible economic facts. A journal does not create another delivery, invoice or external payment. |

A contract/Instrument describes economic rights and obligations. Its accounting
classification and postings belong to a separate book/policy contract. A
balancing agreement, quantity account and cash-out obligation require their own
supported consumer; this design does not create an imbalance instrument class.
A prior-period commercial correction is not automatically a financial-statement
prior-period error. The system must retain the classification and approval that
justify the selected treatment.

Reuse the [physical lifecycle contract](physical-lifecycle-design.md),
[extension guide](physical-lifecycle-extension-guide.md),
[facility ledger](facility-quantity-ledger.md) and
[existing crude delivery/cash operations](physical-crude-operations.md).

| Existing mechanism | Reuse boundary |
| --- | --- |
| [Final gas input selection](../src/OpenEtrm.MarketData/Gas/GasDailyQuantitySelection.cs) | Resolves exact accepted quantity/evidence under a declared policy. It does not determine cash, recognize delivery or replace existing contractual PV. |
| [Swap determinations](../src/OpenEtrm.MarketData/Trading/SettlementDetermination.cs) | Existing original/predecessor and rounded-restatement invariants are useful evidence. Whole swap-period coverage is not a qualified partial gas coverage adapter. |
| [Crude cash ledger](../src/OpenEtrm.MarketData/Crude/CrudeCashLedger.cs) | Reuse exact arithmetic, retained allocation/predecessor and reconciliation behavior where semantics agree. Keep crude's selected nonnegative-price and invoice-allocation contracts intact. |
| [Bitemporal ranges](../src/OpenEtrm.Shared/Temporal/BitemporalRange.cs) | Reuse effective/knowledge coordinates and exact contextual selection. An accounting posting period is an additional explicit business reference. |
| Native request receipts and authorized operations | Preserve current source authority, retries, semantic duplicate detection and exact-version foreign keys. UI and agent use the same business commands. |

No new generic ledger, formula language or accounting library is justified by a
reference catalog alone. Each executable extension needs a current operation
and an independently specified business oracle.

## Authority reference is different from executable policy

Separate bibliographic evidence from reviewed accounting behavior. A document
identifier or paragraph number is not an executable rule and does not establish
that a standard applies to a trade, entity or book.

| Proposed contract | Required information and invariant |
| --- | --- |
| `AccountingBook` | Exact legal entity, declared accounting basis, functional currency, fiscal calendar and approved mapping scope. Different books may consume the same economic evidence without duplicating physical flows or external cash. |
| `AccountingAuthorityReferenceVersion` | Issuer, framework, document/edition, section or paragraph locator, issue date, official URL, retrieval time and source status. Retain amendment/correction relationships and authorized content fingerprint where available. A changing URL alone is insufficient historical evidence. |
| `AccountingPolicyVersion` | Exact book/applicability scope, effective range, publication/knowledge identity, predecessor, review/approval evidence, exact authority-reference versions, treatment, supported evaluator/version, account-mapping version and numerical policy. Typed parameters are valid only for the selected installed capability. |
| `AccountingPeriodState` | Exact book/calendar period and immutable close/reopen facts with actor, authority and knowledge identity. A pricing close is not an accounting-period close. |
| `JournalBatch` | Exact economic source/correction links, policy/mapping/period-state pins, balanced lines, currency/numerical identity, review, actor, request identity and any exact reversal/replacement links. Posting consumes no physical quantity and records no bank movement. |

These are logical responsibilities, not a proposal to create five tables now.
When a consumer is implemented, core relationships must use exact-version FKs
and normalized memberships, not free-text policy names or arbitrary JSON links.
The data model must retain which change is an economic correction, which is a
policy transition, and which is a reversal of an incorrectly recorded fact.

Reference records store metadata, locators and original interpretation/rationale.
Do not distribute standards text or other licensed source content without rights.
Capture a content fingerprint only from an authorized source; a fingerprint is
not permission to reproduce the document. Runtime calculations do not fetch or
interpret external documents.

Public starting points for reference metadata:

- [IFRS standards directory](https://www.ifrs.org/issued-standards/list-of-standards/)
  identifies standards; editions and amendments have their own effective and
  transition requirements. Framework membership does not determine applicability.
- [FASB standards](https://fasb.org/Page/PageContent?PageId=%2Fstaticpages%2Fstandards.html)
  distinguishes the Codification's authority for nongovernmental US GAAP from
  update documents that communicate amendments; SEC requirements are separately
  applicable to relevant registrants. Retain the applicable authoritative locator
  as well as the amendment identifier.
- [IAS 8 overview](https://www.ifrs.org/issued-standards/list-of-standards/ias-8-basis-of-preparation-of-financial-statements/)
  distinguishes estimate changes from errors. This is a classification boundary,
  not a choice of treatment for this application's future users.

## Change and adoption workflow

1. Record a discovered standards change as a new reference revision, including
   issue/effective information and its predecessor. Identical facts are no-ops;
   changed metadata appends a revision. Discovery never changes an approved rule.
2. Identify affected explicit books/policies and prepare a policy proposal. State
   applicability, any early-adoption election, transition method, comparative or
   opening-balance effects and required classification evidence. Ambiguous or
   unsupported behavior remains unavailable.
3. Preview the proposed policy against retained representative source facts.
   Present old/new results and the exact source, rule, mapping and numerical pins.
   Reference review and accounting-policy approval remain distinct decisions.
4. An authorized accounting-policy reviewer approves/publishes the exact version.
   Agent proposals and the generic human Authorize click do not themselves supply
   missing accounting classification, policy approval or period-close authority.
5. New posting operations resolve one eligible published policy at the explicitly
   declared applicability date and knowledge cutoff. The applicability rule must
   say which date governs; do not universally equate delivery date, posting date
   or publication date. Missing/overlapping policies refuse, as do unavailable
   rules or unresolved transition requirements.
6. Accepted journals retain their exact original policy and source pins. A policy
   update changes no posted row. Historical reproduction is read-only; a required
   transition or restatement creates separately authorized linked facts and
   identifies the affected reporting scope. It is not an overwrite or an
   automatic rerun of all prior journals.

Supported account mappings, applicability dates and parameters can change through
published runtime data under an already installed, qualified rule contract.
New recognition, measurement or transition semantics require a qualified managed
implementation and release. Never infer executable behavior from a model's prose,
a reference title or the apparent similarity of two accounting standards.
An agent may research metadata and propose a typed change; it cannot approve its
own policy, silently choose the accounting basis or replace review with a citation.
No automatic standards scraper, scheduler or broad master catalog is introduced.

## Time, history and contextual presentation

Retain these coordinates separately:

- **Economic coverage:** the delivery/service period and exact commercial claim.
- **Effective and knowledge time:** when an event/revision is effective under its
  supported contract and when it became certified knowledge.
- **Accounting posting period:** the exact selected book/calendar period, with
  its authorized open/closed state at execution.

Invoice issue/due dates, external cash dates and policy adoption dates keep their
own meaning. A March posting about January delivery does not become a January
posting merely because its source has January economic coverage. Whether a late
source correction can affect an earlier operational view follows that source's
explicit effective-time contract. Do not silently backdate it.

Every read/join/preview carries the header As of and knowledge cutoff plus its
exact book/report scope. No hidden date or policy-version selector bypasses that
context. Configuration shows its eligible published head; earlier versions are
viewed by changing context and remain read-only. New proposals require the
current authorized head; a stale review is refused rather than silently rebased.
A comparative/restated report requires an explicit qualified reporting contract;
ordinary historical reads keep their original meaning.

## Proposed first executable consumer

Start with a synthetic, one-entity, one-currency, expense-at-delivery policy for
one final gas billing claim. It is explicitly a numerical/workflow fixture, not
an IFRS/US GAAP policy or an interpretation of a user's contract. No inventory
costing, hedge accounting, tax, FX, bank dispatch or cross-party netting is assumed.
Real accounting applicability and account mappings require separate review.

The accounting adapter consumes an approved immutable commercial cash
determination; it does not recalculate quantity × price with another algorithm.
Source-head checks follow that approved determination/correction chain and its
declared freshness policy. A later unaccepted measurement/allocation revision
does not silently replace or erase the approved cash source; its eligibility
effects require that explicit policy and, where needed, a commercial correction.
The [D338 gas commercial consumer](gas-cash-billing.md) now qualifies one
completed native day/fixed-delivery component and its final-source corrections.
It adds no invoice issuance or accounting policy. Existing gas quantity selection
alone is not a cash determination; the accounting adapter must consume the exact
approved D338 result within its separately qualified accounting scope.

Synthetic baseline: one buyer, one supplier, one January claim and a March
correction in the same calendar year. Select USD with two decimal places and
ties-to-even rounding for this fixture; commercial determinations retain the
qualified implementation identity and journals consume their rounded amounts.
The original 300 USD invoice is issued, with no payment before the correction.
The commercial correction records a separate 6 USD credit linked to that
invoice; it does not replace the original invoice with a 294 USD document.

| Step | Economic evidence | Expected result under the synthetic policy |
| --- | --- | --- |
| Original | January coverage; accepted 100 MMBtu at 3 USD/MMBtu; no fees; commercial determination 300 USD payable | January journal: debit synthetic purchase expense 300; credit synthetic trade payable 300. The commercial obligation remains independently traceable. |
| Late correction | In March, accepted allocation becomes 98; same contractual rate; approved commercial restatement 294 USD payable; January accounting period closed | Preserve the original determination, invoice and journal. A separate approved current-period treatment proposes a 6 USD reduction of the payable in the selected open March period: debit payable 6; credit synthetic purchase-expense adjustment 6. |
| Review and post | Exact source revisions, treatment classification, policy, account mapping, March period state and current source authority rechecked | Persist one balanced correcting batch linked to the original. No payment/refund is inferred. Original gross obligation and correcting credit remain independent items; their net is a reconciliation view. |
| Repeat | Retry the same request, or submit the same business fact with a fresh request identity | Return the first outcome/existing batch. A further source or amount change requires a new revision and explicit predecessor. |

Choosing the synthetic current-period treatment is an input to this example.
The engine must not classify every late correction as an estimate change or
assume closed periods can be reopened. Without approved classification, the
commercial delta may be inspectable but journal posting refuses.

Proposed application boundary, not implemented APIs:

```csharp
PostingProposal Preview(ApprovedCashDetermination source,
    ResolvedAccountingPolicy policy, AccountingPostingContext context);
PostedJournal Post(ReviewedPostingProposal proposal,
    ExpectedAccountingHeads expected, RequestId requestId);
```

The managed domain returns validated immutable proposals. Native handlers own
current authorization, source and period locks, expected-head comparisons and
atomic idempotent persistence. Reuse existing read/review/receipt mechanisms
where their full semantics agree. New accounting permissions must be explicitly
defined and granted; trade booking rights do not automatically confer them.

Posting identity must include the book and exact economic claim/occurrence and
its declared accounting purpose. A request ID, reference revision, mapping change
or policy update alone cannot create another posting for an already consumed
occurrence. Corrections, reversals and policy transitions require their own
explicit authorized occurrence and predecessor links; changing configuration is
not evidence of another economic event.

Open-period and current-head checks gate new mutations. After verifying current
access to the original retained pins, a committed request replay or an identical
semantic fact returns its existing outcome even if the period has since closed
or a policy/source head has advanced. Do not reselect inputs, reopen the period,
post again or rewrite the receipt. Revoked access does not become historical
permission; refuse disclosure when current authority is insufficient.

## Independent acceptance cases and subsequent scope

| Case | Required observable result |
| --- | --- |
| A01: original and late correction above | Each batch balances; January remains unchanged; commercial net and accounting payable reconcile to 294 USD while gross source claims remain separate. |
| A02: no eligible policy, overlapping policy, missing classification or account mapping | Structured refusal; no posting, invented account, zero result or successful empty batch. |
| A03: reference metadata changes after posting | Original journal reproduces exactly; the new reference alone changes no executable policy or amount. |
| A04: policy revision/transition | New eligible operations pin the reviewed revision. Old journals retain the original revision. Historical replay creates no new posting; unsupported retrospective treatment refuses. |
| A05: new mutation with closed period, stale period/policy/source head, revoked or expired authority during execution | Refuse/roll back atomically. Neither a preview nor an older authorization token bypasses the final checks. Existing committed outcomes follow A06 instead. |
| A06: retry and concurrent semantic duplicate, including after period close or reference/policy/mapping/source updates | With current authority over the retained pins, return the immutable existing result/first request outcome, never a second batch for an already consumed occurrence. A genuine correction or transition needs its explicit linked occurrence. |
| A07: conflicting currency, unsupported FX, unmapped signed component or inconsistent source totals | Refuse without cross-currency balancing, implicit sign changes or an inferred exchange rate. |
| A08: second book consumes the same approved evidence | Independent book/policy results; one physical delivery and one external cash history. Cross-book consolidation/elimination remains a separate consumer. |
| A09: paid invoice corrected | Preserve payment evidence. A commercial credit is not proof of a refund; automatic payable/receivable reclassification or bank reversal remains unsupported without a selected qualified policy. |
| A10: same numerical correction with different classifications | Require the approved treatment; the late timestamp and delta alone cannot choose estimate-change, error or policy-transition semantics. |

Implement in order: qualify gas commercial determinations/correction coverage;
add the narrow reviewed policy/reference and synthetic journal-preview consumer;
qualify atomic posted batches and period controls; then add the native review UI
and the same agent operations. An external GL adapter subsequently needs a named
consumer, versioned mapping, stable export identities and acknowledged outcomes;
export success alone cannot establish GL acceptance.

Keep source accounting review, deterministic implementation verification and
production accounting approval as separate evidence. No deployment, accounting
compliance, generalized imbalance settlement or standards-update automation is
claimed by this design.
