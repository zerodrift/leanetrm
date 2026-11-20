"""Owned synthetic source and authorization for physical facility qualifiers."""
import json
import os
from pathlib import Path
import shutil
import subprocess
from uuid import uuid4

from check_booked_side_programs import runtime_query
from preview_fixture import catalogue_fixture, trading_fixture


def literal(value):
    return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"


class StorageFacilityFixture:
    def __init__(self, query, manifest):
        self.query, self.manifest = query, manifest
        self.catalog = manifest['catalogId']
        self.at = '2021-01-15T16:00:00Z'
        query(trading_fixture(self.catalog))
        query(f"SELECT security.initialize_operating_policies('{manifest['tenantId']}','{manifest['workspaceId']}');")
        query(catalogue_fixture(manifest, 'https://preview.invalid'))
        query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN "
            "unnest(ARRAY['ReadFacilityQuantities','RecordFacilityQuantities','ConfigureFacilityLimits'])p WHERE code='trader' ON CONFLICT DO NOTHING;")
        self.points = json.loads(query("SELECT jsonb_agg(to_jsonb(p)||jsonb_build_object('quantity',q.code) ORDER BY p.point_code) "
            "FROM ref_data.gas_delivery_conventions p JOIN ref_data.quantity_specifications q "
            "ON(q.catalog_id,q.revision_id,q.commodity_code)=(p.catalog_id,p.revision_id,p.product_code) "
            f"WHERE(p.catalog_id,p.revision_id)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}') "
            "AND p.point_code IN('synthetic_gas_point_a','synthetic_gas_point_c') AND q.code='test_gas_quantity';"))
        assert len(self.points) == 2
        self.point = self.points[0]

    def capture(self, *, day=None, cutoff=None):
        cut = 'NULL' if cutoff is None else "'" + cutoff + "'"
        return runtime_query(self.query, self.manifest,
            f"SELECT security.resolve_operation_context('{self.catalog}','{day or self.at[:10]}',{cut});")

    def call(self, sql, *, context=None, profile='trader'):
        return runtime_query(self.query, self.manifest,
            f"SELECT security.use_market_context('{self.catalog}','{context or self.capture()}');"
            f"SELECT security.use_trading_context('{self.catalog}','{self.at}');" + sql, profile=profile)

    def record(self, body, assessment=None, operation=None, effective=None, *, context=None):
        effective = effective or self.at
        selected = context or self.capture(day=effective[:10])
        return json.loads(runtime_query(self.query, self.manifest,
            f"SELECT security.record_storage_facility('{self.catalog}','{operation or uuid4()}',{literal(body)},'{effective}',"
            f"{literal(assessment)},'{selected}')::text;"))

    def read(self, facility, context=None):
        return json.loads(self.call(f"SELECT security.read_storage_facility('{self.catalog}','{facility}')::text;", context=context))

    def source(self):
        """Book/report/allocate through frozen ordinary managed operations."""
        if hasattr(self, '_source'):
            return self._source
        directory = Path(self.query('SHOW unix_socket_directories;')).resolve()
        assert directory.name.startswith('oetrm-ref-')
        root = Path(__file__).resolve().parents[1]
        frozen = directory / 'storage-source-binaries'
        binaries = Path(os.environ['STORAGE_FACILITY_BINARIES']) if os.environ.get('STORAGE_FACILITY_BINARIES') else root / 'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0'
        shutil.copytree(binaries, frozen)
        packet = directory / 'storage-source.json'
        packet.write_text(json.dumps(dict(host=str(directory), manifest=self.manifest, at=self.at,
            pointCode=self.point['point_code'], quantityCode=self.point['quantity'])))
        packet.chmod(0o600)
        result = subprocess.run([str(Path(os.environ['DOTNET']).resolve()), str(frozen / 'OpenEtrm.Api.Checks.dll'),
            '--storage-facility-native', str(packet)], cwd=root, capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        print(result.stdout.strip(), flush=True)
        self._source = json.loads(Path(str(packet) + '.source.json').read_text())
        return self._source


def commercial_storage_profile(query,manifest):
    """A physical version and an expressly negotiated rate sheet for commercial booking checks."""
    cat,definition=manifest['catalogId'],manifest['revisionId']
    query(f"SELECT security.initialize_operating_policies('{manifest['tenantId']}','{manifest['workspaceId']}');")
    query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN unnest(ARRAY['ReadFacilityQuantities','ConfigureFacilityLimits'])p WHERE code='trader' ON CONFLICT DO NOTHING;")
    point=json.loads(query("SELECT to_jsonb(p) FROM ref_data.gas_delivery_conventions p WHERE point_code='synthetic_gas_point_d' LIMIT 1;"))
    facility,ip,op=[str(uuid4()) for _ in range(3)]
    context=runtime_query(query,manifest,f"SELECT security.resolve_operation_context('{cat}','2020-12-01',NULL);")
    physical=dict(kind='define',facilityId=facility,expectedStateVersion=0,expectedPreviousId=None,referenceCatalogId=point['catalog_id'],
        referenceRevisionId=point['revision_id'],facilityCode=point['point_code'],commodity=point['product_code'],quantity='test_gas_quantity',clock='test_utc',minimum='0',maximum='2000000000',
        implementation='storage_total_physical_step_ratchets_v1',inventoryBasis='total_physical_inventory_v1',rateBasis='native_quantity_per_elapsed_hour_v1',
        simultaneousFlow='separate_injection_withdrawal_v1',lossTreatment='no_loss_v1',
        ports=[dict(portId=i,pipeline=point['pipeline_code'],zone=point['zone_code'],point=point['point_code'],product=point['product_code'],direction=d,contractDirection=s)
               for i,d,s in [(ip,'inject',1),(op,'withdraw',-1)]],
        ratchets=[dict(lower='0',upper='2000000000',injectionPerHour='100',withdrawalPerHour='100')],evidence='Synthetic hourly operating limits')
    version=json.loads(runtime_query(query,manifest,f"SELECT security.record_storage_facility('{cat}','{uuid4()}',{literal(physical)},'2020-12-01',NULL,'{context}');"))['versionId']
    profile=dict(kind='storage',name='Contract regression facility',code='contract_regression',expectedRevision=0,definitionRevisionId=definition,storageVersionId=version,
        injectionPortId=ip,withdrawalPortId=op,effectiveAt='2020-12-01T00:00:00Z',ratchets=[dict(lower='0',upper='2000000000',maximumDailyInjection='2000',maximumDailyWithdrawal='1500')])
    return json.loads(runtime_query(query,manifest,f"SELECT desk.save_profile('{cat}','{uuid4()}',{literal(profile)});"))['id']
