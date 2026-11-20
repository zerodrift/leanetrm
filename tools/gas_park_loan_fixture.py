"""Synthetic, disposable native inputs for the booked Park and Loan consumer."""
import json
from uuid import uuid4

from preview_fixture import catalogue_fixture, fixture as preview_fixture, trading_fixture


def fixture(root):
    reference, market, security, manifest = preview_fixture(root)
    return reference, market, security, {**manifest, 'identityIssuer': 'https://preview.invalid'}


def literal(value):
    return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"


class ParkLoanFixture:
    """Own the accounts and explicit native evidence used by one qualification."""

    def __init__(self, query, manifest):
        self.query, self.manifest = query, manifest
        self.context_operation=('security.resolve_operation_context' if query("SELECT to_regprocedure('security.resolve_operation_context(uuid,date,timestamptz)') IS NOT NULL;")=='t' else 'security.capture_date_context')
        self.catalog = manifest['catalogId']
        self.at = '2020-12-20T16:00:00Z'
        self.day = '2020-12-20'
        query(trading_fixture(self.catalog))
        from rate_curve_fixture import discount_default
        reference = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
        publication = f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{self.catalog}')"
        query(f"BEGIN;INSERT INTO trading.publications SELECT '{self.catalog}',max(publication_order)+1,pg_current_xact_id() "
              f"FROM trading.publications WHERE catalog_id='{self.catalog}';" +
              discount_default(self.catalog, str(uuid4()), manifest['revisionId'], reference, 'usd', 'interest_rates', publication,
                  '2020-01-01T00:00:00Z') + 'COMMIT;')
        query(f"SELECT security.initialize_operating_policies('{manifest['tenantId']}','{manifest['workspaceId']}');")
        query(catalogue_fixture(manifest, manifest['identityIssuer']))
        query("INSERT INTO security.role_permissions SELECT role_id,p.code FROM security.role_permissions r "
              "CROSS JOIN security.permissions p WHERE r.permission='AmendTrade' AND p.code IN "
              "('ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings','RunScenario') "
              "ON CONFLICT DO NOTHING;")
        self.terms = json.loads(query(
            "SELECT jsonb_build_object('quantity',q.quantity_code,'currency',q.currency_code,'location',c.location_code)::text "
            "FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)="
            "(c.reference_catalog_id,c.reference_revision_id,c.quote_code) "
            f"WHERE(c.catalog_id,c.revision_id,c.code)=('{self.catalog}','{manifest['revisionId']}','future');"))
        self.contract = '50000000-0000-0000-0000-000000000001'
        self.entity = 'd4000000-0000-0000-0000-000000000001'
        self.party = 'd5000000-0000-0000-0000-000000000001'
        self.other_party = 'd5000000-0000-0000-0000-000000000002'

    def call(self, sql, *, profile='trader', rollback=False):
        manifest = self.manifest
        token = self.query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','{profile}','human_interactive','{manifest['tenantId']}',"
            f"'{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return self.query("SET SESSION AUTHORIZATION preview_runtime;BEGIN;"
            f"SELECT security.use_context('{token}');{sql}" + ('ROLLBACK;' if rollback else 'COMMIT;'))

    def capture(self, *, day=None, cutoff=None):
        selected = 'NULL' if cutoff is None else "'" + cutoff + "'"
        return self.call(f"SELECT {self.context_operation}('{self.catalog}','{day or self.day}',{selected});")

    def native(self, account, action, body, *, policy=None, operation=None, context=None, profile='trader', rollback=False):
        selected = f"'{policy}',1" if policy else 'NULL,NULL'
        return json.loads(self.call(
            f"SELECT security.record_gas_owned('{self.catalog}','{operation or uuid4()}','{action}','{account}',"
            f"{literal(body)},'{self.at}','{context or self.capture()}',{selected},false)::text;",
            profile=profile, rollback=rollback))

    def account(self, label, *, kind='owned_inventory', book=None):
        if book is None:
            book = str(uuid4())
            self.query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic {label}',publication_order,effective_at "
                f"FROM trading.books WHERE(catalog_id,id)=('{self.catalog}','d1000000-0000-0000-0000-000000000001');")
        account = str(uuid4())
        manifest = self.manifest
        self.native(account, 'account', dict(code='synthetic_' + label, kind=kind, bookId=book,
            legalEntityId=self.entity, legalEntityRevision=1,
            counterpartyId=self.party if kind == 'operator_balance' else None,
            counterpartyRevision=1 if kind == 'operator_balance' else None,
            referenceCatalogId=manifest['referenceCatalogId'], referenceRevisionId=manifest['referenceRevisionId'],
            definitionRevisionId=manifest['revisionId'], markCurve='future', markContractId=self.contract,
            titlePolicy='explicit_accepted_title_v1', lossPolicy='separate_evidenced_loss_v1',
            effectiveAt='2020-12-18T00:00:00Z', **self.terms))
        return account, book

    def bind(self, account, action):
        policy = str(uuid4())
        body = dict(operationId=str(uuid4()), policyId=policy, expectedRevision=0, action=action, mode='manual',
            fromAt='2020-01-01T00:00:00Z', untilAt='2022-01-01T00:00:00Z',
            implementation='explicit_lifecycle_action_policy_v1')
        self.call(f"SELECT lifecycle.save_policy('{self.catalog}',{literal(body)});")
        self.native(account, 'bind_policy', dict(action=action, policyId=policy, policyRevision=1,
            effectiveAt='2020-12-18T00:00:00Z'))
        return policy

    def evidence(self, account, key, kind, quantity, when, *, revision=0):
        return self.native(account, 'evidence', dict(sourceKey=key, kind=kind, quantity=quantity,
            occurredAt=when, evidence='Synthetic observed Park and Loan performance', expectedRevision=revision))['id']

    def holdings(self, book, *, context=None, at=None):
        selected = context or self.capture()
        when = at or self.at
        return json.loads(self.call(f"SELECT security.use_market_context('{self.catalog}','{selected}');"
            f"SELECT security.use_trading_context('{self.catalog}','{when}');"
            f"SELECT security.read_gas_owned_holdings('{self.catalog}','{selected}','{when}',ARRAY['{book}']::uuid[])::text;"))

    def publication(self):
        return int(self.query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{self.catalog}';"))

    def valuation_inputs(self):
        if hasattr(self, '_valuation_inputs'):
            return self._valuation_inputs
        revision = self.manifest['revisionId']
        frame = next(row for row in self.manifest['frames'] if row['at'] == '2020-12-18T16:00:00Z')
        rate_series = json.loads(self.query("SELECT jsonb_agg(series_id) FROM market_data.rate_curve_tenors "
            f"WHERE(catalog_id,revision_id,curve_code)=('{self.catalog}','{revision}','interest_rates');"))
        selected = ','.join("'" + value + "'::uuid" for value in frame['observationIds'])
        rates = ','.join("'" + value + "'::uuid" for value in rate_series)
        zero_rows = json.loads(self.query("WITH admitted AS(INSERT INTO market_data.observations "
            "SELECT o.catalog_id,gen_random_uuid(),o.definition_revision_id,o.series_id,o.reference_catalog_id,o.reference_revision_id,o.source_code,"
            "'synthetic_park_loan_zero_'||o.series_id,'1',o.role,o.publication_state,o.observed_at,clock_timestamp(),0,NULL FROM market_data.observations o "
            f"WHERE o.catalog_id='{self.catalog}' AND o.id=ANY(ARRAY[{selected}]) AND o.series_id=ANY(ARRAY[{rates}]) "
            "RETURNING id,series_id) SELECT jsonb_agg(jsonb_build_object('id',id,'series',series_id)) FROM admitted;"))
        replacements = {row['series']: row['id'] for row in zero_rows}
        assert len(replacements) == len(rate_series) and replacements
        mark = self.query("INSERT INTO market_data.observations SELECT catalog_id,gen_random_uuid(),definition_revision_id,series_id,reference_catalog_id,"
            "reference_revision_id,source_code,'synthetic_park_loan_mark','1',role,publication_state,observed_at,clock_timestamp(),3,NULL "
            f"FROM market_data.observations WHERE(catalog_id,id)=('{self.catalog}','70000000-0000-0000-0000-000000000001') RETURNING id;")
        pins = json.loads(self.query("SELECT jsonb_agg(jsonb_build_object('definitionRevisionId',o.definition_revision_id,'seriesId',o.series_id,'observationId',"
            f"CASE WHEN o.id='70000000-0000-0000-0000-000000000001'::uuid THEN '{mark}'::uuid ELSE coalesce((" + literal(replacements) + ")->>o.series_id::text,o.id::text)::uuid END,'inputUse','variable')) "
            f"FROM market_data.observations o WHERE o.catalog_id='{self.catalog}' AND o.id=ANY(ARRAY[{selected}]);"))
        members = [dict(definitionRevisionId=revision, curve='future', contractId=contract, ordinal=index, scope='root')
            for index, contract in enumerate(frame['curveContracts']['future'])]
        self._valuation_inputs = dict(markId=mark, rateIds=list(replacements.values()), pins=pins, members=members,
            contractId=self.contract, definitionRevisionId=revision)
        return self._valuation_inputs


def rejected(work, code):
    try:
        work()
    except RuntimeError as error:
        assert code in str(error), str(error)
    else:
        raise AssertionError('Expected ' + code)
