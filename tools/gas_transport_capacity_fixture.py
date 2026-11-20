"""Synthetic direct-path capacity inputs on an owned disposable database."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from uuid import UUID, uuid4

from check_booked_side_programs import runtime_query
from gas_park_loan_fixture import fixture, literal
from preview_fixture import catalogue_fixture, trading_fixture


def identity(domain, scope, ordinal=0):
    return str(UUID(hashlib.sha256(f'{domain}:{scope}:{ordinal}'.encode()).hexdigest()[:32]))


class TransportCapacityFixture:
    def __init__(self, query, manifest):
        self.query, self.manifest = query, manifest
        self.context_operation=('security.resolve_operation_context' if query("SELECT to_regprocedure('security.resolve_operation_context(uuid,date,timestamptz)') IS NOT NULL;")=='t' else 'security.capture_date_context')
        self.catalog = manifest['catalogId']
        self.at = '2020-12-19T23:59:59.999999Z'
        self.effective = '2020-12-19T16:00:00Z'
        self.book = 'd1000000-0000-0000-0000-000000000001'
        query(trading_fixture(self.catalog))
        query(f"SELECT security.initialize_operating_policies('{manifest['tenantId']}','{manifest['workspaceId']}');")
        query(catalogue_fixture(manifest,manifest['identityIssuer']))
        query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN "
            "unnest(ARRAY['ReadGasOperations','RecordGasOperations','ConfigureGasServices'])p WHERE code='trader' ON CONFLICT DO NOTHING;")

    def call(self, sql, *, profile='trader', commit=True):
        return runtime_query(self.query, self.manifest, sql, profile=profile, commit=commit)

    def capture(self, *, day=None, cutoff=None):
        cut = 'NULL' if cutoff is None else "'" + cutoff + "'"
        return self.call(f"SELECT {self.context_operation}('{self.catalog}','{day or self.at[:10]}',{cut});")

    def read(self, entitlement, *, context=None):
        return json.loads(self.call(f"SELECT security.use_market_context('{self.catalog}','{context or self.capture()}');"
            f"SELECT security.use_trading_context('{self.catalog}','{self.at}');"
            f"SELECT security.read_gas_transport_capacity('{self.catalog}','{entitlement}')::text;"))

    def record(self, body, witness=None, *, operation=None, context=None, effective=None):
        return json.loads(self.call(f"SELECT security.record_gas_transport_capacity('{self.catalog}','{operation or uuid4()}',"
            f"{literal(body)},'{effective or self.effective}',{literal(witness)},'{context or self.capture()}')::text;"))

    def normalize(self, commands):
        """Run the real managed command ingress from a coherent artifact copy."""
        root = Path(__file__).resolve().parents[1]
        source = Path(os.environ.get('GAS_TRANSPORT_BINARIES', root / 'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0'))
        with tempfile.TemporaryDirectory(prefix='openetrm-transport-normalize-') as temp:
            directory = Path(temp)
            shutil.copytree(source, directory / 'checks')
            request, response = directory / 'input.json', directory / 'output.json'
            request.write_text(json.dumps(dict(commands=commands)))
            result = subprocess.run([os.environ['DOTNET'], str(directory / 'checks/OpenEtrm.Api.Checks.dll'),
                '--gas-transport-capacity-normalize', str(request), str(response)], cwd=root, capture_output=True, text=True, timeout=30)
            if result.returncode:
                raise RuntimeError(result.stdout + result.stderr)
            return json.loads(response.read_text())['commands']

    def definition(self, key='synthetic_direct_capacity'):
        entitlement = identity('gas_transport_entitlement_v1:' + key, self.book)
        limit = [dict(**{'from': '2020-12-20T00:00:00Z'}, through='2020-12-20T02:00:00Z', maximumPerHour='100')]
        def path(key, receipt, zone, maximum):
            return dict(pathKey=key, pathId=identity('gas_transport_path_v1:' + key, entitlement), pipeline='test_pipeline_a',
                receiptZone=zone, receiptPoint=receipt, deliveryZone='synthetic_gas_zone_b', deliveryPoint='synthetic_gas_point_b',
                product='test_gas', limits=[{**limit[0], 'maximumPerHour': maximum}])
        return dict(kind='define', entitlementId=entitlement, expectedStateVersion=0, expectedPreviousId=None, businessKey=key,
            referenceCatalogId=self.manifest['referenceCatalogId'], referenceRevisionId=self.manifest['referenceRevisionId'], bookId=self.book,
            legalEntityId='d4000000-0000-0000-0000-000000000001', legalEntityRevision=1,
            counterpartyId='d5000000-0000-0000-0000-000000000001', counterpartyRevision=1,
            product='test_gas', quantity='test_gas_quantity', **{'from': limit[0]['from']}, through=limit[0]['through'],
            implementation='gas_transport_gross_uniform_capacity_v1', rateBasis='native_quantity_per_elapsed_hour_v1',
            flowShape='uniform_each_explicit_receipt_delivery_interval_v1', agreementBasis='agreement_receipt_route_gross_receipts_v1',
            deliveryBasis='delivery_point_gross_deliveries_v1', agreement=limit,
            paths=sorted([path('synthetic_path_a','synthetic_gas_point_a','synthetic_gas_zone_a','60'),
                path('synthetic_path_d','synthetic_gas_point_d','synthetic_gas_zone_a','80')], key=lambda p:p['pathId']),
            receipts=[dict(point=p,limits=[{**limit[0], 'maximumPerHour':'80'}]) for p in ('synthetic_gas_point_a','synthetic_gas_point_d')],
            deliveries=[dict(point='synthetic_gas_point_b',limits=[dict(limit[0])])], evidence='Synthetic declared direct-path capacity')

    def plan(self, facts, key, quantity, *, operation=None, path_key='synthetic_path_a', empty=False, admission_policy='complete_feasible_replacement_v1'):
        operation = operation or str(uuid4())
        path = next(p for p in facts['paths'] if p['path_key'] == path_key)
        previous = next((p['id'] for p in facts['plans'] if p['business_key'] == key), None)
        body = dict(kind='plan',entitlementId=facts['entitlement']['id'],expectedStateVersion=facts['stateVersion'],expectedPreviousId=previous,
            businessKey=key,versionId=facts['version']['id'],admissionPolicy=admission_policy,evidence='Synthetic chosen capacity plan',claims=[])
        if not empty:
            body['claims']=[dict(claimId=identity('gas_transport_claim_v1',operation),pathId=path['id'],receiptFrom='2020-12-20T00:00:00Z',
                receiptTo='2020-12-20T01:00:00Z',receiptQuantity=str(quantity),deliveryFrom='2020-12-20T00:00:00Z',
                deliveryTo='2020-12-20T01:00:00Z',deliveryQuantity=str(quantity))]
        return operation, body

    def hourly_witness(self, facts, claims, *, agreement=100, delivery=100, admission_policy='complete_feasible_replacement_v1'):
        """Literal one-hour qualification grid, independent of production math."""
        paths = {p['path_key']:p['id'] for p in facts['paths']}
        def ratio(n):
            return dict(n=str(n), d='1')
        scopes = [('agreement',None,None,agreement,claims),
            ('receipt','synthetic_gas_point_a',None,80,[c for c in claims if c['pathId']==paths['synthetic_path_a']]),
            ('receipt','synthetic_gas_point_d',None,80,[c for c in claims if c['pathId']==paths['synthetic_path_d']]),
            ('delivery','synthetic_gas_point_b',None,delivery,claims),
            ('route',None,paths['synthetic_path_a'],60,[c for c in claims if c['pathId']==paths['synthetic_path_a']]),
            ('route',None,paths['synthetic_path_d'],80,[c for c in claims if c['pathId']==paths['synthetic_path_d']])]
        rows=[]
        for axis, point, path, maximum, selected in scopes:
            # Every fixture claim is exactly00:00→01:00. Idle axes have one
            # whole-window row; used axes retain the01:00 event boundary.
            quantity=sum(int(c['receiptQuantity']) for c in selected)
            rows.append(dict(axis=axis,point=point,pathId=path,**{'from':'2020-12-20T00:00:00Z'},
                through='2020-12-20T01:00:00Z' if selected else '2020-12-20T02:00:00Z',
                claims=sorted(c['claimId'] for c in selected),used=ratio(quantity),maximum=ratio(maximum),withinLimit=quantity<=maximum))
            if selected:
                rows.append(dict(axis=axis,point=point,pathId=path,**{'from':'2020-12-20T01:00:00Z'},through='2020-12-20T02:00:00Z',
                    claims=[],used=ratio(0),maximum=ratio(maximum),withinLimit=True))
        return dict(implementation='gas_transport_capacity_witness_v1',versionId=facts['version']['id'],admissionPolicy=admission_policy,intervals=rows)
