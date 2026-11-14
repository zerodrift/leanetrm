"""Certified futures settlement evidence and managed margin planning in owned PostgreSQL."""
import json
import os
from decimal import Decimal
from pathlib import Path
import subprocess
from uuid import uuid4

from check_reference_editing import main


def checks(query, manifest):
    catalog = manifest['catalogId']
    host = query('SHOW unix_socket_directories;')
    assert Path(host).resolve().name.startswith('oetrm-ref-')
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    def issue():
        return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                     f"'https://preview.invalid','viewer','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def current(on='2020-12-18'):
        token = issue()
        return query("SET SESSION AUTHORIZATION preview_runtime;BEGIN;"
                     f"SELECT security.use_context('{token}');"
                     f"SELECT security.capture_date_context('{catalog}','{on}',NULL);COMMIT;")

    before = current()
    original = '70000000-0000-0000-0000-000000000001'
    contract = '50000000-0000-0000-0000-000000000001'
    correction = str(uuid4())
    query("INSERT INTO market_data.observations "
          f"SELECT catalog_id,'{correction}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,"
          "source_event_id,'2',role,publication_state,observed_at,clock_timestamp(),value+0.1,id "
          f"FROM market_data.observations WHERE catalog_id='{catalog}' AND id='{original}';")
    after = current()
    final_id, last_daily = str(uuid4()), str(uuid4())
    final_at = query(f"SELECT expiry_at FROM market_data.contracts WHERE(catalog_id,revision_id,id)=('{catalog}','{manifest['revisionId']}','{contract}');")
    def terminal_observation(identifier, role):
        query("INSERT INTO market_data.observations "
              f"SELECT catalog_id,'{identifier}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,"
              f"'margin_{role}','1','{role}','final','{final_at}',clock_timestamp(),value,NULL "
              f"FROM market_data.observations WHERE catalog_id='{catalog}' AND id='{original}';")
    terminal_observation(last_daily, 'daily_settlement')
    missing_final = current('2020-12-28')
    terminal_observation(final_id, 'final_contract_settlement')
    with_final = current('2020-12-28')
    receipts = json.loads(query("SELECT jsonb_object_agg(e.observation_id,k.known_from)::text "
                                "FROM market_data.knowledge_events e JOIN market_data.market_knowledge_receipts k "
                                "ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid) "
                                f"WHERE e.catalog_id='{catalog}' AND e.observation_id IN('{original}','{correction}','{final_id}');"))
    size = Decimal(query("SELECT p.contract_size FROM ref_data.futures_products p "
                         f"WHERE p.catalog_id='{manifest['referenceCatalogId']}' AND p.revision_id='{manifest['referenceRevisionId']}' "
                         "AND p.code='test_gas_future';"))
    value = Decimal(query(f"SELECT value FROM market_data.observations WHERE catalog_id='{catalog}' AND id='{original}';"))
    samples = [dict(contextId=before, expected=original, knownAt=receipts[original], adjustment=str((value-3)*size*2)),
               dict(contextId=after, expected=correction, knownAt=receipts[correction], adjustment=str(Decimal('0.1')*size*2)),
               dict(contextId=before, expected=original, knownAt=receipts[original], adjustment='0')]
    for sample in samples:
        sample['token'] = issue()
    final_samples = [dict(contextId=missing_final, expected=None, at=final_at, token=issue()),
                     dict(contextId=with_final, expected=final_id, at=final_at, knownAt=receipts[final_id], token=issue())]
    packet = dict(manifest=manifest, host=host, original=original, contract=contract,
                  at='2020-12-18T16:00:00Z', samples=samples, finalSamples=final_samples)
    packet.update({key: str(uuid4()) for key in ('marketBinding', 'occurrence', 'policy', 'terminal', 'trade', 'book', 'binding', 'determination')})
    path = Path(host)/'margin-source-check.json'
    path.write_text(json.dumps(packet))
    path.chmod(0o600)
    root = Path(__file__).resolve().parents[1]
    result = subprocess.run([os.environ.get('DOTNET', 'dotnet'), 'run', '--no-build', '--no-restore', '--project',
                             'tests/OpenEtrm.Api.Checks', '--', '--native-futures-margin-sources', str(path)],
                            cwd=root, capture_output=True, text=True, timeout=60)
    if result.returncode:
        raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(), flush=True)


if __name__ == '__main__':
    main(checks=checks)
