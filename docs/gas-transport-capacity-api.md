# Transport capacity operation wire contract

Status: implemented contract for the locally qualified capacity consumer. This does
not enable financial Transport booking or assert physical route conservation.

The five POST operations are `gas/transport/capacity/directory`, `read`,
`preview`, `record` and `status`. Directory accepts `{context}`; read accepts
`{context, entitlementId}`. Context is the existing explicit
`FacilityQuantityContext` wire shape. Its resolver is shared without inheriting
facility permissions. Read authority is captured before context resolution.
Write requests contain `{context, ownerId, operationId, effectiveAt, command}`;
status contains `{ownerId, operationId}`. The selected actor owns recovery.
For a new fact, the UTC date of `effectiveAt` must equal the selected context's
`effectiveOn`. To publish a future-effective assertion, select that future
business date explicitly. Receipt/delivery stage windows remain independent
within the saved entitlement interval. Exact and semantic acknowledgements keep
the accepted fact's original effective time and source without reinstating it.

## Canonical commands

Every command has `kind`, `entitlementId`, `expectedStateVersion`,
`expectedPreviousId` and `evidence`. Unknown or duplicate properties refuse.
Instants are exact supported microsecond instants; quantities are explicit
native nonnegative decimal strings. No field selects a financial fallback.

`define` additionally has:

- `businessKey`, `referenceCatalogId`, `referenceRevisionId`, `bookId`,
  `legalEntityId`, `legalEntityRevision`, `counterpartyId`,
  `counterpartyRevision`, `product`, `quantity`, `from`, `through`;
- `implementation`, `rateBasis`, `flowShape`, `agreementBasis`, `deliveryBasis`,
  each explicitly selecting the named managed capacity law;
- `agreement`: complete dated limits;
- `paths`: rows containing `pathKey`, `pipeline`, `receiptZone`, `receiptPoint`,
  `deliveryZone`, `deliveryPoint`, `product` and complete dated `limits`;
- `receipts` and `deliveries`: rows containing `point` and dated `limits`.

A dated limit is `{from, through, maximumPerHour}`. Its complete coverage spans
the entitlement service interval. A path row may also contain its canonical
`pathId`; otherwise normalization supplies the technical identity. Path IDs are
derived from the entitlement and path key, independent of publication request.
The normalized command carries them. Path keys and geometry are immutable for
an entitlement; capacity revisions change only limits and evidence. Definition
version identity is its accepted operation ID. Definition identity derives from
the owning catalog, book and business key, so a new request cannot duplicate it.

`plan` additionally has `businessKey`, `versionId`, `admissionPolicy` and
`claims`. Each claim has
`pathId`, `receiptFrom`, `receiptTo`, `receiptQuantity`, `deliveryFrom`,
`deliveryTo`, `deliveryQuantity` and an optional canonical `claimId`. Technical
claim IDs derive from the operation and canonical row multiplicity. Claims are
capacity-use claims only: unequal stages do not establish fuel, loss, transit,
linepack or title. Admission policy is mandatory: the explicitly selected
`complete_feasible_replacement_v1` requires the whole resulting scope to fit
every capacity. The separate `explicit_empty_release_v1` requires an exact
non-null current predecessor and no proposed claims. It releases only that
plan, even when other plans retain breaches after a capacity decrease. It
cannot add, reschedule or partially replace claims. The described release
action supplies this exact policy and an empty claim list after the user
selects the existing plan.
Revision identity is its accepted operation ID. Previous revision refers only
to the chosen plan with that business key. All other current plans remain in
the managed and native capacity assessment.

`expectedStateVersion` is the complete entitlement source generation, not merely
the selected plan head. It changes for every capacity or chosen-plan revision,
including source-known future-effective rows that would stale a prospective
admission. Preview and record pin it. Historical reads resolve visible heads
under their saved knowledge cutoff before applying business time.

## Native boundary

The security functions are `gas_transport_capacity_authority(catalog, kind)`,
`require_gas_transport_capacity_commit(catalog, kind, original_until)`,
`gas_transport_capacity_owner(catalog)`,
`gas_transport_capacity_status(catalog, operation)`,
`gas_transport_capacity_prior_fact(catalog, body, effective_at)`,
`gas_transport_capacity_directory(catalog)`,
`read_gas_transport_capacity(catalog, entitlement)` and
`record_gas_transport_capacity(catalog, operation, body, effective_at, witness,
source_context)`. The record producer receives an unbound connection and an
exact certified context captured before the preview savepoint; retry recovery
precedes fresh source reads. Original authority bounds survive source waits and
native/managed final admission. A guarded exact-fact lookup also precedes fresh
source reads for a new request identity acknowledging an existing fact. This
does not reinstate that fact's earlier head. Unsupported or stale current input
refuses.

Facts contain `entitlement`, `version`, `paths`, `agreement`, `receipts`,
`deliveries`, `plans`, `claims`, `stateVersion`, `hasFutureHeads`, `contextId`,
`asOf`, `knownAt`. `hasFutureHeads` is a mandatory Boolean proof, never an
optional false fallback. A latest-known future-effective definition has
`version:null`; an obsolete earlier definition is not resurrected. Any future
head makes complete assessment and new changes unavailable at that business
context. Raw retained evidence remains readable.
Relational rows use snake_case column names. `version.id` is the selected exact
capacity version. Path rows retain `id`, `path_key`, `pipeline_code`,
`receipt_zone_code`, `receipt_point_code`, `delivery_zone_code`,
`delivery_point_code`, `product_code`, `limits`. Receipt/delivery rows have
`point_code` and `limits`; limit rows have `from`, `through`,
`maximum_per_hour`. Plan heads retain their business key, revision, predecessor
and selected version. Claims include `plan_id` and their exact stage values.
Current directory rows contain `id`, `label` and `revision`.

The native witness is the complete managed capacity interval assessment and
selected `admissionPolicy`. Its normalized form and independent native comparison are finalized together before
publication; client-supplied feasibility booleans never authorize admission.
Every interval retains its true limit comparison, including remaining breaches
in a valid explicit empty release.

## Shared presentation

Directory/read return `ownerId`, resolved `context`, `at`, `historical`,
`canConfigure`, `canRecord`, `configuration`, `forms`, `overviewSections` and
`tables`. Directory also returns `entitlements` and `hasMore`; read returns
`entitlementId` and `facts`. Forms use existing `FacilityOperationForm` and
`GasFormField` metadata. A directory has one explicit new-definition form;
the read has a capacity-revision form and chosen-plan form. Configuration and
plan rights are independent. Tables identify instant columns for user-zone
formatting; TypeScript does no capacity arithmetic.

Preview returns `ownerId`, `operationId`, `entitlementId`, `kind`, normalized
`command`, `effectiveAt`, resolved `context`, `at`, `overviewSections` and
`tables`, plus mandatory `acknowledgement`. An authenticated existing-fact
acknowledgement is labeled explicitly and has no current-capacity assessment
tables; validating a repeated definition's candidate does not imply zero
current claims. The same rule covers exact accepted-request replay, whose first
outcome can retain `changed:true`; that historical flag does not mean that the
retry republishes its effect. Record returns
`{operationId, entitlementId, kind, outcome}`. Status
is `{found:false}` or the same receipt plus original `command` and
`effectiveAt`. Native status also returns its entry-captured `authorityUntil`;
the managed boundary registers that same deadline through final publication.
It never renews it after a source wait or reuses a historical command's expired
grant as authority for a new recovery request. Exact original request/status
recovery uses the existing actor transport and pending-command contract.
