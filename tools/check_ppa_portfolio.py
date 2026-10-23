"""Whole-book consumers cannot publish a futures/swap subset of a PPA book."""
import json
from pathlib import Path
import subprocess
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking


def run_checks(url, query, manifest, dotnet, owned_root, environment):
    catalog, tenant, workspace = [manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId')]
    book = str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{catalog}','{book}','d0000000-0000-0000-0000-000000000001',"
          "'Synthetic complete-scope check',1,'2020-01-01T00:00:00Z');")
    client = PreviewClient(url); client.bootstrap(); client.call('profile', {'profile': 'trader'})
    frame = next(f for f in client.call('workspace')['frames'] if f['at'].startswith('2020-12-15') and not f.get('ppaBundle'))
    context = {'snapshotId': frame['id'], 'convention': 'fixed_total_cash_execution_pnl_v1', 'knowledgeMode': 'current'}
    contract = client.call('futures/catalog', context)['contracts'][0]
    future = client.call('futures/book', {**context, **trade_booking(), 'operationId': str(uuid4()),
        'bookId': book, 'curve': contract['curve'], 'contractId': contract['id'], 'lots': '1', 'executionPrice': '2.9'})

    def runtime(sql):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
        return 'BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;' + f"SELECT security.use_context('{token}');" + sql + 'COMMIT;'

    def capture():
        identity = query(runtime(f"SELECT security.capture_date_context('{catalog}','{frame['at'][:10]}',NULL);"))
        return json.loads(query("SELECT jsonb_build_object('contextId',id,'effectiveOn',effective_on,'knownAt',known_at,'knownThrough',known_through) "
            f"FROM market_data.temporal_contexts WHERE id='{identity}';"))

    before = capture()
    payload = json.loads(query(f"SELECT request FROM trading.ppa_trade_revisions WHERE catalog_id='{catalog}' AND event_kind='booked' ORDER BY publication_order LIMIT 1;"))
    payload.update(bookId=book, snapshotId=frame['id'], reason='Synthetic whole-book refusal check')
    payload['terms'].update(manifest['ppaR3']['bundle'])
    encoded = json.dumps(payload).replace("'", "''")
    query(runtime(f"SELECT security.save_ppa_booking('{catalog}','{uuid4()}','{encoded}'::jsonb);"))
    fixture = Path(owned_root) / 'ppa-portfolio.json'
    fixture.write_text(json.dumps({'frame': frame, 'bookId': book, 'futureTradeId': future['tradeId'], 'before': before, 'after': capture()}))
    def managed(mode):
        subprocess.run([str(dotnet), 'run', '--project', 'tests/OpenEtrm.Api.Checks', '--', mode, str(fixture)],
            cwd=Path(__file__).resolve().parents[1], env=environment, check=True, timeout=90)
    managed('--ppa-portfolio')
    reference = manifest['ppaR3']['bundle']['referenceCatalogId']
    where = f"catalog_id='{reference}' AND tenant_id='{tenant}' AND workspace_id='{workspace}'"
    saved = query(f"SELECT row_to_json(s) FROM security.reference_catalog_scopes s WHERE {where};")
    assert saved
    try:
        query(f"DELETE FROM security.reference_catalog_scopes WHERE {where};")
        managed('--ppa-portfolio-denied')
    finally:
        encoded_scope = saved.replace("'", "''")
        query("INSERT INTO security.reference_catalog_scopes SELECT(json_populate_record(NULL::security.reference_catalog_scopes,"
              f"'{encoded_scope}'::json)).*;")
