"""Delivery binding authoring against an owned preview, with original market pins."""
from uuid import uuid4
from decimal import Decimal
import json
import subprocess
import time
import socket
from pathlib import Path
from urllib.request import urlopen
from concurrent.futures import ThreadPoolExecutor
from preview_client import PreviewClient, initial_frame


def run_gas_reference_bootstrap(manifest,owned_root,dotnet,dll,environment,browser=None):
    """An owned auxiliary API has exact physical masters but no gas price frame."""
    root=owned_root/'reference-only-gas';root.mkdir()
    run_id=(owned_root/'.openetrm-preview').read_text()
    (root/'.openetrm-preview').write_text(run_id)
    source=manifest['swapMarkets'][-1]
    selected={**manifest,'gasMarket':{'revisionId':source['revisionId'],'frames':[]},'retainedGasMarkets':[]}
    manifest_path=owned_root/'manifest.json';original=manifest_path.read_text()
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1',0));port=reservation.getsockname()[1]
    url=f'http://127.0.0.1:{port}';repository=Path(__file__).resolve().parents[1]
    child=None
    with (root/'app.log').open('w') as log:
        try:
            # Both APIs keep immutable startup manifests. The auxiliary process
            # uses the same owned socket root; no ownership guard is relaxed.
            manifest_path.write_text(json.dumps(selected))
            child=subprocess.Popen([str(dotnet),str(dll),'--local-preview'],cwd=repository,
              env={**environment,'OPENETRM_PREVIEW_PORT':str(port)},stdout=log,stderr=subprocess.STDOUT)
            deadline=time.monotonic()+20
            while time.monotonic()<deadline:
                if child.poll() is not None:raise AssertionError((root/'app.log').read_text())
                try:
                    with urlopen(url+'/api/health',timeout=.5) as response:health=json.load(response)
                    assert health['runId']==run_id.strip();break
                except OSError:time.sleep(.05)
            else:raise AssertionError('The owned reference API did not start')
            manifest_path.write_text(original)
            c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
            frame=initial_frame(c.call('workspace'),'2020-12-18')
            context={'snapshotId':frame['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
            c.call('tickets/description',{'context':context,'product':'physical_gas'})
            draft={'product':'physical_gas','version':1,'trade':{},'sides':[{'kind':'physical','code':'physical','values':{'product':'test_gas','pipeline':'test_pipeline_a','zone':'synthetic_gas_zone_a'}}]}
            choices=c.call('tickets/choices',{'context':context,'draft':draft,'side':'physical','field':'delivery_point'})['choices']
            assert 'synthetic_gas_point_unpriced' in {p['value'] for p in choices}
            assert c.call('tickets/lookup',{'context':context,'draft':draft,'side':'physical','field':'price'},expected=422)['code']=='gas_market_context'
            if browser:
                target=owned_root/'gas-reference-browser-target.json';target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id.strip()}))
                subprocess.run(['node',str(repository/'tests/browser/run.mjs'),str(target),str(browser),'--gas-references'],cwd=repository,env=environment,check=True,timeout=90)
        finally:
            manifest_path.write_text(original)
            if child is not None and child.poll() is None:
                child.terminate();child.wait(timeout=15)
    print('PASS gas reference bootstrap: native description and independent physical choices need no dated price frame; pricing still refuses',flush=True)


def run_resolution_authority_check(query,manifest,psql,environment):
    """A direct runtime resolver cannot publish after natural authority expiry."""
    catalog=manifest['catalogId'];tenant=manifest['tenantId'];workspace=manifest['workspaceId']
    physical=query(f"""SELECT jsonb_build_object('referenceCatalogId',a.delivery_catalog_id,'referenceRevisionId',a.delivery_revision_id,
      'commodity',a.commodity_code,'grade',a.grade_code,'facility',a.facility_code,'custodyPoint',a.custody_point_code,
      'quantityCode',g.quantity_code,'clock',a.clock_code) FROM market_data.crude_valuation_assignments a
      JOIN ref_data.crude_grades g ON(g.catalog_id,g.revision_id,g.code)=(a.delivery_catalog_id,a.delivery_revision_id,a.grade_code)
      WHERE a.catalog_id='{catalog}' AND a.setup_id IS NULL ORDER BY a.revision_id,a.code LIMIT 1;""")
    principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    permissions=query(f"SELECT jsonb_agg(to_jsonb(p)) FROM security.role_permissions p WHERE permission='ReadMarketData' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
    temporary,grant,app=str(uuid4()),str(uuid4()),'synthetic-crude-resolution-'+str(uuid4())
    locker=reader=None
    try:
        query(f"""BEGIN;INSERT INTO security.roles VALUES('{temporary}','{app}','Synthetic expiring crude reader');
          INSERT INTO security.role_permissions VALUES('{temporary}','ReadMarketData');
          DELETE FROM security.role_permissions WHERE permission='ReadMarketData' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');
          INSERT INTO security.role_grants VALUES('{grant}','{principal}','{temporary}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '8 seconds',false);COMMIT;""")
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        locker=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        locker.stdin.write('BEGIN;LOCK TABLE market_data.contracts IN ACCESS EXCLUSIVE MODE;\n\\echo LOCKED\n');locker.stdin.flush()
        while locker.stdout.readline().strip()!='LOCKED':
            if locker.poll() is not None:raise AssertionError(locker.stderr.read())
        reader=subprocess.Popen(psql,env={**environment,'PGAPPNAME':app},stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        encoded=physical.replace("'","''")
        reader.stdin.write(f"\\set VERBOSITY verbose\nBEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');SELECT security.resolve_crude_delivery('{catalog}','{encoded}'::jsonb,'2021-01-10T00:00:00Z','2021-01-11T00:00:00Z','2020-12-18T16:00:00Z');ROLLBACK;\n\\q\n");reader.stdin.flush()
        for _ in range(150):
            if query(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='{app}' AND NOT l.granted AND l.relation='market_data.contracts'::regclass;")=='1':break
            if reader.poll() is not None:raise AssertionError(reader.communicate())
            time.sleep(.02)
        else:raise AssertionError('The resolver did not reach its controlled storage wait')
        for _ in range(220):
            if query(f"SELECT granted_to<=clock_timestamp() FROM security.role_grants WHERE id='{grant}';")=='t':break
            time.sleep(.05)
        else:raise AssertionError('The independent ReadMarketData grant did not expire')
        locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();_,errors=locker.communicate(timeout=10)
        assert locker.returncode==0,errors
        output,errors=reader.communicate(timeout=10)
        assert reader.returncode!=0 and '42501' in errors, 'Resolver returned after authority expiry: '+output+errors
    finally:
        if locker is not None and locker.poll() is None:
            locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();locker.communicate(timeout=10)
        if reader is not None and reader.poll() is None:reader.communicate(timeout=10)
        encoded=permissions.replace("'","''")
        query(f"BEGIN;DELETE FROM security.role_grants WHERE id='{grant}';DELETE FROM security.role_permissions WHERE role_id='{temporary}';DELETE FROM security.roles WHERE id='{temporary}';INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,'{encoded}'::jsonb) ON CONFLICT DO NOTHING;COMMIT;")
    print('PASS crude direct resolver: natural ReadMarketData expiry during a controlled storage wait refuses publication',flush=True)


def run_reference_publication_checks(url,query,manifest,psql,environment):
    """Raw reference responses recheck independent read expiry after storage waits."""
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    context={'snapshotId':frame['id'],'convention':'physical_crude_fixed_volume_v1','knowledgeMode':'current'}
    gas={'context':{**context,'convention':'physical_gas_fixed_quantity_v4'},'draft':{'product':'physical_gas','version':1,'trade':{},'sides':[{'kind':'physical','code':'physical','values':{'product':'test_gas','pipeline':'test_pipeline_a','zone':'synthetic_gas_zone_a'}}]},'side':'physical','field':'delivery_point'}
    principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    permissions=query(f"SELECT jsonb_agg(to_jsonb(p)) FROM security.role_permissions p WHERE permission='ReadMarketData' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
    for path,body,table in [('crude/references',context,'trading.books'),('tickets/choices',gas,'ref_data.pipeline_delivery_points')]:
        temporary,grant=str(uuid4()),str(uuid4());locker=None
        try:
            query(f"""BEGIN;INSERT INTO security.roles VALUES('{temporary}','synthetic_reference_{temporary}','Synthetic expiring reference reader');
              INSERT INTO security.role_permissions VALUES('{temporary}','ReadMarketData');
              DELETE FROM security.role_permissions WHERE permission='ReadMarketData' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');
              INSERT INTO security.role_grants VALUES('{grant}','{principal}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '3 seconds',false);COMMIT;""")
            locker=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
            locker.stdin.write(f'BEGIN;LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE;\n\\echo LOCKED\n');locker.stdin.flush()
            while locker.stdout.readline().strip()!='LOCKED':
                if locker.poll() is not None:raise AssertionError(locker.stderr.read())
            with ThreadPoolExecutor(max_workers=1) as executor:
                read=executor.submit(c.call,path,body,expected=403)
                try:
                    for _ in range(150):
                        if query(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.usename='preview_runtime' AND NOT l.granted AND l.relation='{table}'::regclass;")!='0':break
                        if read.done():raise AssertionError(read.result())
                        time.sleep(.02)
                    else:raise AssertionError('Reference response did not reach the controlled storage wait: '+path)
                    for _ in range(220):
                        if query(f"SELECT granted_to<=clock_timestamp() FROM security.role_grants WHERE id='{grant}';")=='t':break
                        time.sleep(.05)
                    else:raise AssertionError('The reference ReadMarketData grant did not expire')
                finally:
                    locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();_,errors=locker.communicate(timeout=10)
                    assert locker.returncode==0,errors
                assert read.result(timeout=15)['code']=='access_denied'
        finally:
            if locker is not None and locker.poll() is None:
                locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();locker.communicate(timeout=10)
            encoded=permissions.replace("'","''")
            query(f"BEGIN;DELETE FROM security.role_grants WHERE id='{grant}';DELETE FROM security.role_permissions WHERE role_id='{temporary}';DELETE FROM security.roles WHERE id='{temporary}';INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,'{encoded}'::jsonb) ON CONFLICT DO NOTHING;COMMIT;")
    print('PASS crude/gas reference publication: independently expiring ReadMarketData refuses raw responses after controlled storage waits',flush=True)


def run_checks(url, query, manifest):
    catalog_id, tenant, workspace = (manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    def issue(profile='viewer'):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    def runtime(sql, context=None):
        bind = f"SELECT security.use_market_context('{catalog_id}','{context}');" if context else ''
        return f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{issue()}');" + bind + sql + 'COMMIT;'
    assert query("SELECT NOT has_function_privilege('preview_runtime','security.capture_pre_crude_setup_context(uuid,date,timestamptz)','EXECUTE') AND NOT has_function_privilege('openetrm_risk_worker','security.capture_pre_crude_setup_context(uuid,date,timestamptz)','EXECUTE');") == 't'
    original_prices = query("SELECT count(*)||'/'||md5(string_agg(row_to_json(o)::text,',' ORDER BY id)) FROM market_data.observations o;")
    before = query(runtime(f"SELECT security.capture_date_context('{catalog_id}','2020-12-18',NULL);"))
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'editor'})
    catalog = c.call('crude-setup/catalog')
    references = catalog['deliveryReferences']
    assert references['revisionId'] == manifest['referenceCatalog']['referenceRevisionId']
    for table, name in [('crude_grades','grades'),('locations','locations'),('quantity_specifications','quantities')]:
        expected = query(f"SELECT code FROM ref_data.{table} WHERE(catalog_id,revision_id)=('{references['catalogId']}','{references['revisionId']}') ORDER BY code;").splitlines()
        assert sorted(row['code'] for row in references[name]) == expected, 'Physical masters must not depend on pricing assignments'

    delivery = next(p for p in catalog['deliveryPoints'] if p['grade_code'] == 'synthetic_delivery_grade')
    assert delivery['revision_id'] == manifest['referenceCatalog']['referenceRevisionId']
    source = next(s for s in catalog['sources'] if s['curve'] == 'synthetic_crude_forward')
    assert delivery['revision_id'] != source['referenceRevisionId']
    config = {'code': 'synthetic_crude_binding', 'deliveryReferenceCatalogId': delivery['catalog_id'],
              'deliveryReferenceRevisionId': delivery['revision_id'], 'commodity': delivery['commodity_code'],
              'grade': delivery['grade_code'], 'facility': delivery['facility_code'], 'custodyPoint': delivery['custody_point_code'],
              'clock': 'test_utc', 'definitionRevisionId': source['definitionRevisionId'], 'curve': source['curve'],
              'validFrom': '2021-01-01', 'validTo': '2021-02-01', 'quantityPolicy': 'same_standard_volume_at_60f_v1'}
    trader = PreviewClient(url); trader.bootstrap(); trader.call('profile', {'profile': 'trader'})
    trade_frame = initial_frame(trader.call('workspace'), '2020-12-18')
    trade_context = {'snapshotId': trade_frame['id'], 'convention': 'physical_crude_fixed_volume_v1', 'knowledgeMode': 'current'}
    reference_catalog = trader.call('crude/references', trade_context)
    assert reference_catalog['deliveryReferences'] == references
    assert trader.call('crude/directory', trade_context)['trades'] == [], 'Opening authoring needs no published mapping or trade'
    grade = next(g for g in references['grades'] if g['code'] == config['grade'])
    physical = {'referenceCatalogId': references['catalogId'], 'referenceRevisionId': references['revisionId'],
                'commodity': config['commodity'], 'grade': config['grade'], 'facility': config['facility'],
                'custodyPoint': config['custodyPoint'], 'quantityCode': grade['quantity_code'], 'clock': config['clock']}
    resolution = {**trade_context, 'physical': physical, 'deliveryFrom': '2021-01-10T00:00:00Z', 'deliveryTo': '2021-01-11T00:00:00Z'}
    assert trader.call('crude/resolve', resolution, expected=422)['code'] == 'crude_price_mapping_missing'
    trader.call('crude/resolve', {**resolution, 'curve': source['curve']}, expected=400)
    trader.call('crude/resolve', {**resolution, 'physical': {**physical, 'curve': source['curve']}}, expected=400)
    body = {'operationId': str(uuid4()), 'baseSetupId': None, 'configuration': config}
    saved = c.call('crude-setup/save', body)
    assert c.call('crude-setup/save', body) == saved
    assert c.call('crude-setup/save', {**body, 'operationId': str(uuid4())})['setupId'] == saved['setupId'], 'Equivalent facts must reuse one version'
    equivalent = {**config, 'validFrom': '2021-1-1', 'validTo': '2021-2-1'}
    assert c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'configuration': equivalent})['setupId'] == saved['setupId']
    c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'configuration': {**config, 'validFrom': '01/02/2021'}}, expected=422)
    c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'configuration': {**config, 'validFrom': '2021-99-01'}}, expected=422)
    missing_month = c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'configuration': {**config, 'code': 'synthetic_missing_month', 'validTo': '2021-03-01'}})
    c.call('crude-setup/validate', {'setupId': missing_month['setupId']}, expected=422)
    invalid = {**body, 'configuration': {**config, 'curve': 'missing'}}
    c.call('crude-setup/save', invalid, expected=409)
    result = c.call('crude-setup/validate', {'setupId': saved['setupId']})
    assert result['valid'] and result['pricingQuantity']['specification']['revision']['revisionId'] == source['referenceRevisionId']
    c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': saved['setupId']}, expected=403)
    c.call('profile', {'profile': 'approver'})
    command = {'operationId': str(uuid4()), 'setupId': saved['setupId']}
    published = c.call('crude-setup/publish', command)
    assert c.call('crude-setup/publish', command) == published
    resolved = trader.call('crude/resolve', resolution)
    assert resolved['crudeSetupId'] == saved['setupId'] and resolved['definitionRevisionId'] == source['definitionRevisionId']
    assert resolved['physical'] == physical and resolved['curve'] == source['curve']
    assert resolved['priceUnit'] and resolved['quantityUnit']
    assert trader.call('crude/resolve', {**resolution, 'physical': {**physical, 'grade': 'synthetic_unmapped_grade'}}, expected=422)['code']=='crude_price_mapping_missing'
    assert trader.call('crude/resolve', {**resolution, 'physical': {**physical, 'facility': 'test_hub_a'}}, expected=422)['code']=='crude_delivery_relationship_missing'
    from crude_fixture import UNRELATED_CATALOG,UNRELATED_REVISION
    c.call('profile',{'profile':'editor'})
    unrelated=c.call('crude-setup/save',{'operationId':str(uuid4()),'baseSetupId':None,'configuration':{**config,'code':'synthetic_unrelated_binding','deliveryReferenceCatalogId':UNRELATED_CATALOG,'deliveryReferenceRevisionId':UNRELATED_REVISION}})
    c.call('profile',{'profile':'approver'});c.call('crude-setup/publish',{'operationId':str(uuid4()),'setupId':unrelated['setupId']})
    query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{UNRELATED_CATALOG}','{tenant}','{workspace}');")
    try:
        available=trader.call('crude/resolve',resolution)
        assert all(available[k]==resolved[k] for k in ('definitionRevisionId','code','crudeSetupId','physical','curve')), 'An unrelated inaccessible delivery catalog must not gate this physical tuple'
        assert trader.call('crude/references',trade_context)['deliveryReferences']==references
        assert trader.call('crude/directory',trade_context)['trades']==[]
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{UNRELATED_CATALOG}','{tenant}','{workspace}','owner');")
    c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': missing_month['setupId']}, expected=422)
    # A draft based on a superseded head can be resaved under the new explicit base.
    c.call('profile', {'profile': 'editor'})
    b_config = {**config, 'validFrom': '2021-01-02'}
    b = c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'baseSetupId': saved['setupId'], 'configuration': b_config})
    other = c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'baseSetupId': saved['setupId'], 'configuration': {**config, 'validFrom': '2021-01-03'}})
    c.call('profile', {'profile': 'approver'})
    c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': other['setupId']})
    c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': b['setupId']}, expected=409)
    c.call('profile', {'profile': 'editor'})
    rebased = c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'baseSetupId': other['setupId'], 'configuration': b_config})
    assert rebased['setupId'] != b['setupId']
    c.call('profile', {'profile': 'approver'})
    c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': rebased['setupId']})
    # Returning to earlier terms from a later explicit base is a new revision.
    c.call('profile', {'profile': 'editor'})
    reverted = c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'baseSetupId': rebased['setupId']})
    assert reverted['state'] == 'draft' and reverted['setupId'] != saved['setupId']
    c.call('profile', {'profile': 'approver'})
    c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': reverted['setupId']})
    # An earlier head remains authorized in its original reference catalog while
    # a newer head moves to the maintained delivery catalog.
    original = {**config, 'code': 'synthetic_head_visibility', 'deliveryReferenceCatalogId': source['referenceCatalogId'],
        'deliveryReferenceRevisionId': source['referenceRevisionId'], 'grade': 'synthetic_grade_a'}
    c.call('profile', {'profile': 'editor'})
    original_head = c.call('crude-setup/save', {'operationId': str(uuid4()), 'baseSetupId': None, 'configuration': original})
    c.call('profile', {'profile': 'approver'}); c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': original_head['setupId']})
    c.call('profile', {'profile': 'editor'})
    maintained_head = c.call('crude-setup/save', {'operationId': str(uuid4()), 'baseSetupId': original_head['setupId'],
        'configuration': {**config, 'code': 'synthetic_head_visibility', 'grade': 'synthetic_grade_a'}})
    c.call('profile', {'profile': 'approver'}); c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': maintained_head['setupId']})
    c.call('profile', {'profile': 'trader'})
    frame = initial_frame(c.call('workspace'), '2020-12-18')
    quoted = c.call('crude-setup/preview', {'snapshotId': frame['id'], 'setupId': saved['setupId'], 'knowledgeMode': 'current',
                      'deliveryFrom': '2021-01-01T00:00:00Z', 'deliveryTo': '2021-02-01T00:00:00Z', 'quantity': '1000'})
    assert Decimal(quoted['presentValue']) == 80000
    assert quoted['deliveryQuantity']['revision']['revisionId'] == delivery['revision_id']
    assert quoted['pricingQuantity']['revision']['revisionId'] == source['referenceRevisionId']
    c.call('crude-setup/preview', {'snapshotId': frame['id'], 'setupId': saved['setupId'], 'knowledgeMode': 'current',
        'deliveryFrom': '2020-12-18T00:00:00Z', 'deliveryTo': '2021-01-01T00:00:00Z', 'quantity': '1000'}, expected=422)
    assert quoted['curve'] == source['curve'] and quoted['definitionRevisionId'] == source['definitionRevisionId']
    for context, expected in ((before,'0'),(quoted['contextId'],'1')):
        assert query(runtime(f"SELECT count(*) FROM market_data.crude_setup_versions WHERE(catalog_id,id)=('{catalog_id}','{saved['setupId']}');",context)) == expected
    for reference in (delivery['catalog_id'], source['referenceCatalogId']):
        query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{reference}','{tenant}','{workspace}');")
        try:
            c.call('profile', {'profile': 'trader'})
            c.call('crude/catalog', {'snapshotId':frame['id'],'convention':'physical_crude_fixed_volume_v1','knowledgeMode':'current'}, expected=403)
            if reference == delivery['catalog_id']:
                explicit = c.call('crude-setup/preview', {'snapshotId':frame['id'],'setupId':original_head['setupId'],'knowledgeMode':'current',
                    'deliveryFrom':'2021-01-01T00:00:00Z','deliveryTo':'2021-02-01T00:00:00Z','quantity':'1000'})
                assert Decimal(explicit['presentValue']) == 80000
            c.call('profile', {'profile': 'editor'}); c.call('crude-setup/save', body, expected=403)
            c.call('profile', {'profile': 'approver'}); c.call('crude-setup/publish', command, expected=403)
            assert query(runtime(f"SELECT count(*) FROM market_data.crude_setup_versions WHERE(catalog_id,id)=('{catalog_id}','{saved['setupId']}');",quoted['contextId'])) == '0'
        finally:
            query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{tenant}','{workspace}','owner');")
    assert query("SELECT count(*)||'/'||md5(string_agg(row_to_json(o)::text,',' ORDER BY id)) FROM market_data.observations o;") == original_prices
    c.call('profile', {'profile': 'viewer'})
    c.call('crude-setup/save', body, expected=403)
    print('PASS crude setup: immutable exact delivery/pricing bindings, semantic replay, role separation and original market valuation', flush=True)


def run_booking_checks(url, query, manifest, *, run_worker):
    from preview_fixture import trade_booking
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    frame = initial_frame(c.call('workspace'), '2020-12-18')
    context = {'snapshotId': frame['id'], 'convention': 'physical_crude_fixed_volume_v1', 'knowledgeMode': 'current'}
    catalog = c.call('crude/catalog', context)
    assignment = next(a for a in catalog['assignments'] if a['bindingName']=='synthetic_crude_binding')
    body = {**context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': catalog['books'][0]['id'],
            'definitionRevisionId': assignment['definitionRevisionId'], 'assignmentCode': assignment['code'], 'crudeSetupId': assignment['crudeSetupId'],
            'profile': catalog['profiles'][0]['code'], 'quantityBasis': 'standard_volume_total', 'tolerance': '0', 'discounting': False,
            'deliveryFrom': '2021-01-10T00:00:00Z', 'deliveryTo': '2021-01-11T00:00:00Z', 'paymentAt': '2021-02-01T00:00:00Z',
            'quantity': '1000', 'direction': 'receive', 'considerationDirection': 'pay', 'fixedPrice': '78', 'handlingFee': None, 'handlingFeeDirection': None}
    ticket=c.call('crude/references',context);references=ticket['deliveryReferences']
    assert ticket['clocks'] and all(k['kind']=='fixed_offset' and int(k['fixed_offset_seconds'])==0 for k in ticket['clocks']), ticket['clocks']
    grade=next(g for g in references['grades'] if g['code']==assignment['grade'])
    physical={'referenceCatalogId':references['catalogId'],'referenceRevisionId':references['revisionId'],
              'commodity':assignment['commodity'],'grade':assignment['grade'],'facility':assignment['facility'],
              'custodyPoint':assignment['custodyPoint'],'quantityCode':grade['quantity_code'],'clock':assignment['clock']}
    resolution={**context,'physical':physical,'deliveryFrom':body['deliveryFrom'],'deliveryTo':body['deliveryTo']}
    resolved=c.call('crude/resolve',resolution)
    assert resolved['code']==body['assignmentCode'] and resolved['crudeSetupId']==body['crudeSetupId']
    assert resolved['priceUnit']==f"USD / {resolved['quantityUnit']}", resolved['priceUnit']
    body['physical']=physical
    before = c.call('crude/preview', body)
    assert Decimal(before['presentValue']) == 2000
    assert (before['pricingSource'],before['priceUnit'],before['profileName'])==(resolved['curveName'],resolved['priceUnit'],'Fixed standard volume at 60°F · zero tolerance · undiscounted'), before
    c.call('crude/book', {**body, 'crudeSetupId': None}, expected=409)
    booked = c.call('crude/book', body)
    assert c.call('crude/book', body) == booked
    reopened = c.call('crude/editor', {'context': context, 'tradeId': booked['tradeId']})
    assert reopened['terms']['crudeSetupId'] == assignment['crudeSetupId'] and Decimal(reopened['valuation']['presentValue']) == 2000
    assert {k: reopened['valuation'][k] for k in ('pricingSource', 'priceUnit', 'profileName')} == {k: before[k] for k in ('pricingSource', 'priceUnit', 'profileName')}
    shared = {**context, 'convention': 'trading_workspace_v1'}
    positions = c.call('trading/positions', {'context': shared, 'bookIds': [], 'portfolioIds': [], 'offset': 0, 'limit': 64})
    position = next(p for p in positions['positions'] if p['tradeId'] == booked['tradeId'])
    assert position['instrumentType'] == 'physical_crude'
    blotter = c.call('trading/blotter', {'context': shared, 'view': {'mode': 'trades', 'bookIds': [body['bookId']], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}})
    assert next(p for p in blotter['holdings'] if p['tradeId'] == booked['tradeId'])['grade'] == 'Synthetic delivery grade'
    assert query(f"SELECT s.economic_identity::jsonb->>'contract' FROM trading.crude_memberships m JOIN trading.sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id) WHERE m.catalog_id='{manifest['catalogId']}' AND m.instrument_id='{booked['instrumentId']}' AND m.slot=0;") == 'physical_crude_side_v2'
    # Native and queued risk must use one original factor for the old and mapped holdings.
    from fractions import Fraction
    retained = {**shared, 'knowledgeMode': 'retained', 'contextId': blotter['context']['contextId']}
    c.call('profile', {'profile': 'risk'})
    risk = c.call('portfolio/risk/catalog', {'context': retained, 'bookIds': [body['bookId']], 'portfolioIds': []})
    factors = [f for f in risk['factors'] if assignment['curve'] in f['curves']]
    assert len(factors) == 1
    request = {'context': retained, 'bookIds': [body['bookId']], 'portfolioIds': [],
        'bumps': [{'factor': factors[0]['factor'], 'size': '1', 'convention': risk['bumpConvention']}],
        'convention': risk['convention'], 'numericalPolicy': risk['numericalPolicy'], 'pricingPolicy': risk['pricingPolicy'], 'stress': []}
    report = c.call('portfolio/risk', request)
    held = next(p for p in report['factors'][0]['positions'] if p['tradeId'] == booked['tradeId'])
    assert {k: Fraction(held['values'][k]) for k in ('base','up','down','delta','gamma')} == {'base':2000,'up':3000,'down':1000,'delta':1000,'gamma':0}
    selected_request = {**request, 'tradeIds': [booked['tradeId']]}
    selected_report = c.call('portfolio/risk', selected_request)
    assert {p['tradeId'] for factor in selected_report['factors'] for p in factor['positions']} == {booked['tradeId']}
    job = c.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': selected_request})
    run_worker(); completed = c.call('portfolio/risk/jobs/' + job['id'])
    from check_trade_risk import joint_scope
    joint_scope(c, selected_request, selected_report, run_worker=run_worker)
    assert completed['state'] == 'completed', completed
    assert completed['result']['factors'] == selected_report['factors']
    # A later setup publication cannot change retained economics or program identity.
    c.call('profile', {'profile': 'editor'})
    saved = next(v for v in c.call('crude-setup/catalog')['versions'] if v['id'] == assignment['crudeSetupId'])
    change = {**saved['configuration'], 'validFrom': '2021-01-04'}
    draft = c.call('crude-setup/save', {'operationId': str(uuid4()), 'baseSetupId': assignment['crudeSetupId'], 'configuration': change})
    c.call('profile', {'profile': 'approver'}); c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': draft['setupId']})
    c.call('profile', {'profile': 'trader'})
    after = c.call('crude/editor', {'context': context, 'tradeId': booked['tradeId']})
    assert after['valuation'] == reopened['valuation']
    c.call('crude/book', {**body, 'operationId': str(uuid4())}, expected=409)
    assert c.call('crude/book', body) == booked, 'An admitted booking retry retains its first outcome after a new setup publication'
    # Explicit supplemental-source pins must be identical in setup preview, ticket and booking.
    c.call('profile', {'profile': 'editor'})
    source = next(m for m in manifest['swapMarkets'] if m['revisionId'] == assignment['definitionRevisionId'])
    source_frame = next(f for f in source['frames'] if f['at'][:10] == frame['at'][:10])
    changed = c.call('corrections', {'snapshotId': frame['id'], 'observationId': source_frame['observationIds'][0], 'value': '81'})
    c.call('profile', {'profile': 'trader'})
    selected = changed['frame']['id']
    current = next(a for a in c.call('crude/catalog', {**context, 'snapshotId': selected})['assignments'] if a['crudeSetupId'] == draft['setupId'])
    b_body = {**body, 'operationId': str(uuid4()), 'snapshotId': selected, 'assignmentCode': current['code'], 'crudeSetupId': current['crudeSetupId']}
    mark = c.call('crude-setup/preview', {'snapshotId': selected, 'setupId': current['crudeSetupId'], 'knowledgeMode': 'current',
        'deliveryFrom': body['deliveryFrom'], 'deliveryTo': body['deliveryTo'], 'quantity': '1000'})
    assert Decimal(mark['presentValue']) == 81000
    assert Decimal(c.call('crude/preview', b_body)['presentValue']) == 3000
    b_trade = c.call('crude/book', b_body)
    assert Decimal(c.call('crude/editor', {'context': {**context, 'snapshotId': selected}, 'tradeId': b_trade['tradeId']})['valuation']['presentValue']) == 3000
    assert Decimal(c.call('crude/preview', {**b_body, 'snapshotId': frame['id']})['presentValue']) == 2000
    refused = c.call('crude/operations/read', {'context': context, 'tradeId': booked['tradeId']}, expected=422)
    assert refused['code'] == 'crude_mapped_lifecycle'
    b_context = {**shared, 'snapshotId': selected}
    b_blotter = c.call('trading/blotter', {'context': b_context, 'view': {'mode':'trades','bookIds':[body['bookId']],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}})
    b_retained = {**b_context, 'knowledgeMode': 'retained', 'contextId': b_blotter['context']['contextId']}
    c.call('profile', {'profile': 'risk'})
    b_catalog = c.call('portfolio/risk/catalog', {'context': b_retained, 'bookIds': [body['bookId']], 'portfolioIds': []})
    b_factor = next(f for f in b_catalog['factors'] if assignment['curve'] in f['curves'])
    b_request = {**request, 'context': b_retained, 'bumps': [{'factor': b_factor['factor'], 'size':'1','convention':b_catalog['bumpConvention']}]}
    b_report = c.call('portfolio/risk', b_request)
    b_value = next(p for p in b_report['factors'][0]['positions'] if p['tradeId'] == b_trade['tradeId'])
    assert {k: Fraction(b_value['values'][k]) for k in ('base','up','down','delta','gamma')} == {'base':3000,'up':4000,'down':2000,'delta':1000,'gamma':0}
    queued = c.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': b_request})
    # Retained knowledge does not retain a revoked delivery-catalog permission.
    delivery_catalog = saved['configuration']['deliveryReferenceCatalogId']
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{delivery_catalog}','{tenant}','{workspace}');")
    try:
        import subprocess
        try: run_worker()
        except subprocess.CalledProcessError: pass
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{delivery_catalog}','{tenant}','{workspace}','owner');")
    denied = c.call('portfolio/risk/jobs/' + queued['id'])
    assert denied['state'] != 'completed' and not denied.get('result'), denied
    if denied['state'] != 'queued':
        queued = c.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': b_request})
    run_worker()
    recovered = c.call('portfolio/risk/jobs/' + queued['id'])
    assert recovered['state'] == 'completed' and recovered['result']['factors'] == b_report['factors'], recovered
    c.call('profile', {'profile':'trader'})
    uncovered={**resolution,'deliveryFrom':'2021-01-01T00:00:00Z','deliveryTo':'2021-01-02T00:00:00Z'}
    assert c.call('crude/resolve',uncovered,expected=422)['code']=='crude_price_mapping_coverage'
    assert c.call('crude/resolve',{**resolution,'physical':{**physical,'quantityCode':'missing'}},expected=422)['code']=='crude_reference_input'
    # A second logical mapping overlaps part of a requested interval. Neither
    # its lack of full coverage nor a supplied source pin may hide ambiguity.
    c.call('profile', {'profile':'editor'})
    alias_config={**change,'code':'synthetic_overlapping_binding','validFrom':'2021-01-11','validTo':'2021-01-20'}
    alias=c.call('crude-setup/save',{'operationId':str(uuid4()),'baseSetupId':None,'configuration':alias_config})
    c.call('profile',{'profile':'approver'});c.call('crude-setup/publish',{'operationId':str(uuid4()),'setupId':alias['setupId']})
    c.call('profile',{'profile':'trader'})
    partial={**resolution,'deliveryTo':'2021-01-12T00:00:00Z'}
    assert c.call('crude/resolve',partial,expected=422)['code']=='crude_price_mapping_ambiguous'
    c.call('profile',{'profile':'editor'})
    alias=c.call('crude-setup/save',{'operationId':str(uuid4()),'baseSetupId':alias['setupId'],'configuration':{**alias_config,'validFrom':'2021-01-10'}})
    c.call('profile',{'profile':'approver'});c.call('crude-setup/publish',{'operationId':str(uuid4()),'setupId':alias['setupId']})
    c.call('profile',{'profile':'trader'})
    assert c.call('crude/resolve',resolution,expected=422)['code']=='crude_price_mapping_ambiguous'
    assert c.call('crude/preview',b_body,expected=422)['code']=='crude_price_mapping_ambiguous'
    assert c.call('crude/book',{**b_body,'operationId':str(uuid4())},expected=422)['code']=='crude_price_mapping_ambiguous'
    # Exercise the final SQL admission with supplied source pins and no API
    # preflight. The synthetic request is sealed only far enough to reach the
    # mapping guard; no trade can be published and all staging is rolled back.
    import json
    from check_booked_side_programs import runtime_query
    native={**b_body,'operationId':str(uuid4())}
    encoded=json.dumps(native).replace("'","''")
    catalog_id=manifest['catalogId']
    stage=f"""INSERT INTO trading.side_schedule_requests(catalog_id,request_id,actor_id,command_hash,permission)
      SELECT catalog_id,'{native['operationId']}',actor_id,sha256(convert_to('{encoded}'::jsonb::text,'UTF8')),permission
      FROM trading.side_schedule_requests WHERE(catalog_id,request_id)=('{catalog_id}','{b_body['operationId']}');
      INSERT INTO trading.side_schedule_request_items SELECT catalog_id,'{native['operationId']}',slot,schedule_id
      FROM trading.side_schedule_request_items WHERE(catalog_id,request_id)=('{catalog_id}','{b_body['operationId']}') AND slot=0;
      INSERT INTO trading.side_schedule_request_seals VALUES('{catalog_id}','{native['operationId']}',1,0);"""
    runtime_query(query,manifest,f"""DO $test$ BEGIN BEGIN
      PERFORM security.book_physical_crude('{catalog_id}','{encoded}'::jsonb,'{frame['at']}');
      RAISE EXCEPTION 'ambiguous_crude_mapping_accepted';
      EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'crude_price_mapping_ambiguous' THEN RAISE;END IF;END;END $test$;""",prefix=stage,commit=False)
    assert c.call('crude/book',b_body)==b_trade, 'A completed command keeps its first outcome after conflicting publication'
    assert Decimal(c.call('crude/editor',{'context':{**context,'snapshotId':selected},'tradeId':b_trade['tradeId']})['valuation']['presentValue'])==3000
    # Reapply the actual upgrade over retained programs and bookings. Restore
    # the preceding booking routine in a transaction, then apply 0173 exactly;
    # every business row and the completed native command must remain intact.
    repository=Path(__file__).resolve().parents[1]
    previous=(repository/'db/migrations/0171_crude_setup_bookings.sql').read_text()
    marker='CREATE OR REPLACE FUNCTION security.book_physical_crude('
    previous=marker+previous.split(marker,1)[1].split('END$$;',1)[0]+'END$$;'
    upgrade=(repository/'db/migrations/0173_physical_crude_reference_resolution.sql').read_text()
    upgrade=upgrade.replace('BEGIN;\n','',1).rsplit('COMMIT;',1)[0]
    tables=('market_data.observations','trading.trades','trading.crude_instruments','trading.crude_trade_revisions',
            'trading.side_schedule_programs','trading.instrument_side_schedules')
    snapshot='jsonb_build_object('+','.join(f"'{table}',(SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb) FROM {table} r)" for table in tables)+')'
    token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    replay=json.dumps(b_body).replace("'","''")
    query(f"""BEGIN;
      CREATE TEMP TABLE physical_upgrade_before AS SELECT {snapshot} value,pg_get_functiondef('security.book_physical_crude(uuid,jsonb,timestamptz)'::regprocedure) routine;
      {previous}
      DROP FUNCTION security.resolve_crude_delivery(uuid,jsonb,timestamptz,timestamptz,timestamptz);
      {upgrade}
      DO $upgrade$ BEGIN
       IF(SELECT value IS DISTINCT FROM {snapshot} OR routine IS DISTINCT FROM pg_get_functiondef('security.book_physical_crude(uuid,jsonb,timestamptz)'::regprocedure) FROM physical_upgrade_before)
       THEN RAISE EXCEPTION 'physical_reference_upgrade_changed_retained_inputs';END IF;
      END $upgrade$;
      SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
      DO $replay$ DECLARE receipt jsonb;BEGIN
       receipt=security.book_physical_crude('{catalog_id}','{replay}'::jsonb,'{frame['at']}');
       IF receipt->>'tradeId' IS DISTINCT FROM '{b_trade['tradeId']}' THEN RAISE EXCEPTION 'physical_reference_upgrade_changed_retry';END IF;
      END $replay$;ROLLBACK;""")
    print('PASS physical reference upgrade: populated observations, trades and retained programs unchanged; completed native retry keeps its original outcome',flush=True)
    print('PASS physical crude resolution: missing refs, delivery coverage, partial overlap, ambiguity, native source-pin bypass refusal and retained replay',flush=True)
    print('PASS mapped crude booking: V2 admission, exact source/setup pins, retained reopen, corrected-source parity, worker risk and stale-head refusal', flush=True)
