"""Explicit synthetic inputs for same-point retained-title Park custody."""
import json
from uuid import uuid4

from gas_park_loan_fixture import ParkLoanFixture, fixture as park_loan_fixture, literal


PROFILE = 'physical_gas_park_custody_v1'
RETURN_ORDER = 'nondecreasing_accepted_custody_return_instants_v1'


def fixture(root):
    reference, market, security, manifest = park_loan_fixture(root)
    # This synthetic market hub is also an explicitly declared physical custody
    # point. Its existing independent market mark needs no location inference.
    pins = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    rows = f"INSERT INTO ref_data.pipeline_delivery_points VALUES({pins},'test_pipeline_a','synthetic_gas_zone_a','test_hub_a');\n"
    rows += f"INSERT INTO ref_data.gas_delivery_conventions VALUES({pins},'test_pipeline_a','synthetic_gas_zone_a','test_hub_a','test_gas','test_utc',0);\n"
    marker = 'UPDATE ref_data.revisions SET'
    assert market.count(marker) == 1
    return reference, market.replace(marker, rows + marker, 1), security, manifest


class CustodyFixture(ParkLoanFixture):
    def __init__(self, query, manifest):
        super().__init__(query, manifest)
        query("INSERT INTO security.role_permissions SELECT role_id,p.code FROM security.role_permissions r "
            "CROSS JOIN security.permissions p WHERE r.permission='AmendTrade' AND p.code IN "
            "('ReadGasOperations','RecordGasOperations','ConfigureGasServices') ON CONFLICT DO NOTHING;")

    def book_scope(self, label):
        book = str(uuid4())
        self.query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic {label}',publication_order,effective_at "
            f"FROM trading.books WHERE(catalog_id,id)=('{self.catalog}','d1000000-0000-0000-0000-000000000001');")
        return book

    def booking(self, book, perspective, owned, *, fee='12.5', direction='pay'):
        return dict(operationId=str(uuid4()), snapshotId=str(uuid4()), contextId=self.capture(),
            definitionRevisionId=self.manifest['revisionId'], convention='physical_gas_fixed_quantity_v4', knowledgeMode='current',
            profile=PROFILE, bookId=book, legalEntityId=self.entity, legalEntityRevision=1,
            counterpartyId=self.party, counterpartyRevision=1,
            referenceCatalogId=self.manifest['referenceCatalogId'], referenceRevisionId=self.manifest['referenceRevisionId'],
            point=self.terms['location'], quantitySpecification=self.terms['quantity'], currency=self.terms['currency'],
            ownedAccountId=owned, serviceKind='park', perspective=perspective, quantity='720',
            parkAt='2020-12-20T10:00:00Z', returnAt='2020-12-21T12:00:00Z', titlePolicy='customer_title_retained_v1',
            returnPolicy='same_point_same_basis_partial_custody_returns_v1', returnTiming='accepted_custody_then_returns_v1',
            returnOrder=RETURN_ORDER, usePolicy='no_use_or_sale_of_custody_v1',
            lossPolicy='unqualified_loss_requires_reconciliation_v1', physicalPolicy='custody_only_no_inferred_flow_v1',
            planPolicy='unaccepted_custody_plan_from_later_of_earliest_or_evaluation_v1', fixedFeeAmount=fee,
            feeDirection=direction, feeDueAt=None if fee == '0' else '2020-12-22T12:00:00Z', feeRecognition='accepted_custody_v1')

    def book(self, body):
        return json.loads(self.call(f"SELECT security.book_gas_park_custody('{self.catalog}',{literal(body)},'{self.at}')::text;"))

    def read(self, trade, *, context=None):
        selected = context or self.capture()
        return json.loads(self.call(f"SELECT security.use_market_context('{self.catalog}','{selected}');"
            f"SELECT security.use_trading_context('{self.catalog}','{self.at}');"
            f"SELECT security.read_gas_park_custody('{self.catalog}','{trade}','{selected}','{self.at}')::text;"))

    def custody_evidence(self, trade, key, kind, quantity, observed, *, revision=0, operation=None, context=None):
        body = dict(sourceKey=key, expectedRevision=revision, kind=kind, quantity=quantity, observedAt=observed,
            evidence='Synthetic independently observed custody record')
        return json.loads(self.call(f"SELECT security.record_gas_park_custody_evidence('{self.catalog}','{operation or uuid4()}',"
            f"'{trade}',{literal(body)},'{self.at}','{context or self.capture()}')::text;"))

    def custody_policy(self, action):
        policy = str(uuid4())
        body = dict(operationId=str(uuid4()), policyId=policy, expectedRevision=0, action='gas.park_custody.' + action,
            mode='manual', fromAt='2020-01-01T00:00:00Z', untilAt='2022-01-01T00:00:00Z',
            implementation='explicit_lifecycle_action_policy_v1')
        self.call(f"SELECT lifecycle.save_policy('{self.catalog}',{literal(body)});")
        return policy

    def record(self, trade, action, body, policy, *, operation=None, context=None, preview=False):
        return json.loads(self.call(f"SELECT security.record_gas_park_custody('{self.catalog}','{operation or uuid4()}',"
            f"'{trade}','{action}',{literal(body)},'{self.at}','{context or self.capture()}','{policy}',1,{str(preview).lower()})::text;"))
