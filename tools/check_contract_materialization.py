"""Schedule-first contract construction in an owned synthetic database."""
import json
import subprocess
from concurrent.futures import ThreadPoolExecutor
from datetime import date, timedelta
from uuid import uuid4
from preview_client import PreviewClient


def run_checks(url, query, manifest, psql, environment):
    def client(profile):
        c = PreviewClient(url)
        c.bootstrap()
        c.call('profile', {'profile': profile})
        return c

    editor, approver, viewer = [client(p) for p in ('editor', 'approver', 'viewer')]
    cat, revision = manifest['catalogId'], manifest['revisionId']

    def context():
        return viewer.call('date-schedules/context', {
            'effectiveOn': '2020-12-18', 'knownAt': None, 'mode': 'current'})['context']

    for symbol, dates in [
        ('oe.seed_boundaries', ['2021-01-01', '2021-02-01', '2021-03-01']),
        ('oe.seed_expiries', ['2020-12-28', '2021-01-28']),
    ]:
        draft = editor.call('date-schedules/save', {
            'definition': {'name': symbol, 'symbol': symbol, 'dates': dates},
            'selection': 'absolute_index', 'calendar': None,
            'effectiveFrom': '2020-01-01', 'effectiveTo': '2022-01-01'})
        approver.call('date-schedules/publish', {k: draft[k] for k in ('id', 'version', 'generation')})
    before_mapping = context()
    mapping = editor.call('date-schedules/contracts/save', {
        'requestId': str(uuid4()), 'code': 'seed_dates', 'name': 'Synthetic dated contracts',
        'context': before_mapping, 'contracts': [
            {'label': 'January', 'start': 'oe.seed_boundaries[0]', 'end': 'oe.seed_boundaries[1]',
             'lastTrading': 'oe.seed_expiries[0]', 'cutoffUtc': '15:30'},
            {'label': 'February', 'start': 'oe.seed_boundaries[1]', 'end': 'oe.seed_boundaries[2]',
             'lastTrading': 'oe.seed_expiries[1]', 'cutoffUtc': '15:30'}]})
    approver.call('date-schedules/contracts/publish', {
        'requestId': str(uuid4()), **{k: mapping[k] for k in ('id', 'version', 'generation')}})
    before_adoption = context()
    original = json.loads(query(f"SELECT row_to_json(c) FROM market_data.contracts c WHERE catalog_id='{cat}' AND revision_id='{revision}' AND kind='future' AND delivery_from='2021-01-01';"))
    assert (original['delivery_to'], original['expiry_at']) == ('2021-02-01', '2020-12-28T15:30:00+00:00')
    body = {'requestId': str(uuid4()), 'revisionId': revision, 'mappingId': mapping['id'],
            'mappingVersion': 1, 'context': before_adoption, 'contracts': [
                {'contractId': original['id'], 'ordinal': 0, 'label': 'January',
                 'kind': 'future', 'futuresSeriesCode': original['futures_series_code']}]}
    receipt = editor.call('date-schedules/contracts/materialize', body)
    assert receipt == editor.call('date-schedules/contracts/materialize', body)
    assert receipt['contracts'][0]['created'] is False
    admission = receipt['contracts'][0]['admissionId']
    another = editor.call('date-schedules/contracts/materialize', {**body, 'requestId': str(uuid4())})
    assert another['contracts'][0]['admissionId'] == admission
    assert json.loads(query(f"SELECT row_to_json(c) FROM market_data.contracts c WHERE catalog_id='{cat}' AND revision_id='{revision}' AND id='{original['id']}';")) == original
    viewer.call('date-schedules/contracts/materialize', body, expected=403)
    editor.call('date-schedules/contracts/materialize', {
        **body, 'contracts': [{**body['contracts'][0], 'ordinal': 1, 'label': 'February'}]}, expected=409)
    editor.call('date-schedules/contracts/materialize', {
        **body, 'requestId': str(uuid4()),
        'contracts': [{**body['contracts'][0], 'ordinal': 1, 'label': 'February'}]}, expected=409)
    editor.call('date-schedules/contracts/materialize', {
        **body, 'requestId': str(uuid4()), 'context': before_mapping}, expected=422)
    editor.call('date-schedules/contracts/materialize', {
        **body, 'requestId': str(uuid4()),
        'contracts': [{**body['contracts'][0], 'label': 'February'}]}, expected=422)
    read = {'revisionId': revision, 'contractId': original['id'], 'context': before_adoption}
    assert viewer.call('date-schedules/contracts/admissions', read) == []
    after = context()
    admissions = viewer.call('date-schedules/contracts/admissions', {**read, 'context': after})
    assert len(admissions) == 1 and admissions[0]['id'] == admission
    assert admissions[0]['mappingId'] == mapping['id'] and admissions[0]['mappingVersion'] == 1
    assert admissions[0]['context'] == before_adoption
    historical = client('viewer')
    historical.call('market/context/retained', {'contextId': after['contextId'], 'selection': 'latest_correction'})
    assert historical.call('date-schedules/contracts/admissions', {**read, 'context': after}) == admissions
    historical.call('date-schedules/contracts/admissions', read, expected=422)
    # A new draft is constructed from the same published rows. Concurrent
    # commands converge on one contract/admission, retaining each first outcome.
    draft_revision, new_contract = str(uuid4()), str(uuid4())
    query(f"INSERT INTO market_data.revisions VALUES('{cat}','{draft_revision}','{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','draft',0);")
    query(f"INSERT INTO market_data.revision_periods VALUES('{cat}','{draft_revision}','2020-01-01','2022-01-01');")
    construction = {**body, 'revisionId': draft_revision, 'contracts': [
        {**body['contracts'][0], 'contractId': new_contract, 'kind': 'forward', 'futuresSeriesCode': None}]}
    second_editor = client('editor')
    commands = [{**construction, 'requestId': str(uuid4())} for _ in range(2)]
    with ThreadPoolExecutor(max_workers=2) as pool:
        futures = [pool.submit(c.call, 'date-schedules/contracts/materialize', command)
                   for c, command in zip((editor, second_editor), commands)]
        outcomes = [f.result() for f in futures]
    assert len({r['contracts'][0]['admissionId'] for r in outcomes}) == 1
    assert sorted(r['contracts'][0]['created'] for r in outcomes) == [False, True]
    for command, outcome in zip(commands, outcomes):
        assert editor.call('date-schedules/contracts/materialize', command) == outcome
    constructed = json.loads(query(f"SELECT row_to_json(c) FROM market_data.contracts c WHERE catalog_id='{cat}' AND revision_id='{draft_revision}';"))
    assert (constructed['delivery_from'], constructed['delivery_to'], constructed['expiry_at']) == ('2021-01-01', '2021-02-01', '2020-12-28T15:30:00+00:00')
    query(f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{cat}','{draft_revision}');")
    assert editor.call('date-schedules/contracts/materialize', commands[0]) == outcomes[0]
    editor.call('date-schedules/contracts/materialize', {
        **construction, 'requestId': str(uuid4()),
        'contracts': [{**construction['contracts'][0], 'contractId': str(uuid4())}]}, expected=409)
    # A later invalid reference rolls back earlier construction in the same command.
    atomic_revision, atomic_contract, atomic_request = [str(uuid4()) for _ in range(3)]
    query(f"INSERT INTO market_data.revisions VALUES('{cat}','{atomic_revision}','{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','draft',0);")
    editor.call('date-schedules/contracts/materialize', {
        **construction, 'revisionId': atomic_revision, 'requestId': atomic_request, 'contracts': [
            {**construction['contracts'][0], 'contractId': atomic_contract},
            {'contractId': str(uuid4()), 'ordinal': 1, 'label': 'February', 'kind': 'future', 'futuresSeriesCode': 'missing_series'}]}, expected=422)
    assert query(f"SELECT count(*) FROM market_data.contracts WHERE catalog_id='{cat}' AND revision_id='{atomic_revision}';") == '0'

    assert query(f"SELECT count(*) FROM market_data.contract_date_materialization_requests WHERE catalog_id='{cat}' AND request_id='{atomic_request}';") == '0'
    editor.call('date-schedules/contracts/materialize', {
        **body, 'revisionId': atomic_revision, 'requestId': str(uuid4()), 'contracts': [
            {**body['contracts'][0], 'contractId': str(uuid4()), 'ordinal': 1, 'label': 'February'}]}, expected=422)
    assert query(f"SELECT count(*) FROM market_data.contracts WHERE catalog_id='{cat}' AND revision_id='{atomic_revision}';") == '0'

    batch = {**construction, 'revisionId': atomic_revision, 'requestId': str(uuid4()),
             'contracts': [{**construction['contracts'][0], 'contractId': str(uuid4())} for _ in range(120)]}
    batch_receipt = editor.call('date-schedules/contracts/materialize', batch)
    assert len(batch_receipt['contracts']) == 120 and all(r['created'] for r in batch_receipt['contracts'])
    assert editor.call('date-schedules/contracts/materialize', batch) == batch_receipt

    # XIDs are assigned when the runtime binds, before admission takes its
    # publication lock. Commit order can be the reverse of XID order.
    source = next(v for v in editor.call('date-schedules')['contractDefinitions'] if v['id'] == mapping['id'])
    fork = editor.call('date-schedules/contracts/save', {
        **source['definition'], 'requestId': str(uuid4()), 'id': source['id'], 'version': source['version'],
        'generation': source['generation'], 'expectedState': 'published', 'context': context()})
    approver.call('date-schedules/contracts/publish', {
        'requestId': str(uuid4()), **{k: fork[k] for k in ('id', 'version', 'generation')}})
    order_context = context()
    february = query(f"SELECT id FROM market_data.contracts WHERE catalog_id='{cat}' AND revision_id='{revision}' AND kind='future' AND delivery_from='2021-02-01';")

    def token():
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")

    def raw_admission(contract, ordinal, admission_id):
        return f"INSERT INTO market_data.contract_date_admissions(catalog_id,id,revision_id,contract_id,mapping_id,mapping_version,ordinal,context_id,actor_id) VALUES('{cat}','{admission_id}','{revision}','{contract}','{mapping['id']}',2,{ordinal},'{order_context['contextId']}',security.custom_curve_actor('{cat}','WriteMarketDefinitions'));"

    first_token, second_token = token(), token()
    first_id, second_id = str(uuid4()), str(uuid4())
    first = subprocess.Popen(psql, env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        first.stdin.write(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{first_token}');SELECT pg_current_xact_id();\n\\echo READY\n")
        first.stdin.flush()
        while first.stdout.readline().strip() != 'READY':
            if first.poll() is not None:
                raise AssertionError(first.stderr.read())
        query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{second_token}');{raw_admission(february, 1, second_id)}COMMIT;")
        first.stdin.write(raw_admission(original['id'], 0, first_id) + 'COMMIT;\n\\q\n')
        first.stdin.flush()
        _, error = first.communicate(timeout=15)
        assert first.returncode == 0, error
    finally:
        if first.poll() is None:
            first.stdin.write('ROLLBACK;\n\\q\n')
            first.stdin.flush()
            first.communicate(timeout=10)
    after_order = context()
    assert len(viewer.call('date-schedules/contracts/admissions', {**read, 'context': after_order})) == 2
    assert query(f"SELECT a.publication_xid<b.publication_xid AND ka.known_from>kb.known_from FROM market_data.contract_date_admissions a JOIN market_data.contract_date_admissions b ON b.id='{second_id}' JOIN market_data.contract_date_admission_receipts ka ON ka.admission_id=a.id JOIN market_data.contract_date_admission_receipts kb ON kb.admission_id=b.id WHERE a.id='{first_id}';") == 't'
    # Direct runtime SQL obeys the same occurrence, actor and publication fences.
    def raw_reject(statement, state):
        capability = token()
        query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{capability}');DO $$BEGIN BEGIN {statement} RAISE EXCEPTION 'accepted_invalid_admission';EXCEPTION WHEN SQLSTATE '{state}' THEN NULL;END;END$$;ROLLBACK;")

    raw_reject(raw_admission(original['id'], 1, str(uuid4())), '23514')
    raw_reject(raw_admission(original['id'], 0, str(uuid4())).replace(
        f"security.custom_curve_actor('{cat}','WriteMarketDefinitions')", "'00000000-0000-0000-0000-000000000000'::uuid"), '42501')
    raw_reject(raw_admission(original['id'], 0, str(uuid4())).replace(
        'context_id,actor_id)', 'context_id,actor_id,publication_xid)').replace(
        f"security.custom_curve_actor('{cat}','WriteMarketDefinitions'));", f"security.custom_curve_actor('{cat}','WriteMarketDefinitions'),'1'::xid8);"), '42501')
    query(f"DO $$BEGIN BEGIN UPDATE market_data.contract_date_admissions SET ordinal=1 WHERE id='{admission}';RAISE EXCEPTION 'accepted_admission_update';EXCEPTION WHEN SQLSTATE '55000' THEN NULL;END;END$$;")
    # Later source publications must not shift the exact pinned occurrences.
    library = editor.call('date-schedules')
    boundaries = next(s for s in library['versions'] if s['definition']['symbol'] == 'oe.seed_boundaries')
    changed = editor.call('date-schedules/save', {
        **{k: boundaries[k] for k in ('id', 'version', 'generation', 'selection', 'effectiveFrom', 'effectiveTo', 'calendar')},
        'expectedState': 'published', 'definition': {**boundaries['definition'], 'dates': ['2020-12-01', '2021-01-01', '2021-02-01', '2021-03-01']}})
    approver.call('date-schedules/publish', {k: changed[k] for k in ('id', 'version', 'generation')})
    still_pinned = editor.call('date-schedules/contracts/materialize', {**body, 'requestId': str(uuid4()), 'context': context()})
    assert still_pinned['contracts'][0]['admissionId'] == admission
    # Target-reference revocation prevents both replay and current reads.
    reference = manifest['referenceCatalogId']
    scope = query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{reference}' RETURNING tenant_id,workspace_id,access_mode;")
    try:
        editor.call('date-schedules/contracts/materialize', body, expected=403)
        assert viewer.call('date-schedules/contracts/admissions', {**read, 'context': after}) == []
        capability = token()
        assert query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{capability}');SELECT security.contract_date_admission_visible('{cat}','{admission}');ROLLBACK;").splitlines()[-1] == 'f'
    finally:
        tenant, workspace, mode = scope.split('|')
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{tenant}','{workspace}','{mode}');")
    # At the supported mapping bound, repeated fields share source authority
    # checks while retaining every distinct date occurrence.
    days = [(date(2021, 1, 1) + timedelta(days=i)).isoformat() for i in range(121)]
    draft = editor.call('date-schedules/save', {
        'definition': {'name': 'Synthetic daily boundaries', 'symbol': 'oe.bounded_days', 'dates': days},
        'selection': 'absolute_index', 'calendar': None, 'effectiveFrom': '2020-01-01', 'effectiveTo': '2022-01-01'})
    approver.call('date-schedules/publish', {k: draft[k] for k in ('id', 'version', 'generation')})
    calendar = next(c['reference'] for c in editor.call('date-schedules')['calendars'] if c['reference']['code'] == 'test_business')
    expiry_source = editor.call('date-schedules/save', {
        'definition': {'name': 'Synthetic daily expiry dates', 'symbol': 'oe.bounded_expiries', 'dates': days[:-1]},
        'selection': 'absolute_index', 'calendar': calendar, 'effectiveFrom': '2020-01-01', 'effectiveTo': '2022-01-01'})
    expiry_source = approver.call('date-schedules/publish', {k: expiry_source[k] for k in ('id', 'version', 'generation')})
    before_large = context()
    large = editor.call('date-schedules/contracts/save', {
        'requestId': str(uuid4()), 'code': 'bounded_daily_dates', 'name': 'Synthetic daily mapping', 'context': before_large,
        'contracts': [{'label': days[i], 'start': f'oe.bounded_days[{i}]', 'end': f'oe.bounded_days[{i+1}]',
                       'lastTrading': f'oe.bounded_expiries[{i}]', 'cutoffUtc': '12:00'} for i in range(120)]})
    approver.call('date-schedules/contracts/publish', {
        'requestId': str(uuid4()), **{k: large[k] for k in ('id', 'version', 'generation')}})
    stored = next(m for m in viewer.call('date-schedules')['contractDefinitions'] if m['id'] == large['id'])
    assert len(stored['definition']['contracts']) == 120 and len(stored['bindings']) == 360
    viewer.call('date-schedules/contracts/resolve', {'id': large['id'], 'version': 1, 'context': before_large}, expected=422)
    next_source = editor.call('date-schedules/save', {
        **{k: expiry_source[k] for k in ('id', 'version', 'generation', 'selection', 'effectiveFrom', 'effectiveTo', 'calendar')},
        'expectedState': 'published', 'definition': {**expiry_source['definition'], 'dates': ['2020-12-31', *days[:-1]]}})
    approver.call('date-schedules/publish', {k: next_source[k] for k in ('id', 'version', 'generation')})
    large_context = context()
    resolved = viewer.call('date-schedules/contracts/resolve', {'id': large['id'], 'version': 1, 'context': large_context})
    assert resolved['contracts'][0]['lastTradingAt'] == '2021-01-01T12:00:00+00:00'
    assert resolved['contracts'][-1]['lastTradingReference']['source']['version'] == 1
    scope = query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{reference}' RETURNING tenant_id,workspace_id,access_mode;")
    try:
        assert not any(m['id'] == large['id'] for m in viewer.call('date-schedules')['contractDefinitions'])
        viewer.call('date-schedules/contracts/resolve', {'id': large['id'], 'version': 1, 'context': large_context}, expected=422)
    finally:
        tenant, workspace, mode = scope.split('|')
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{tenant}','{workspace}','{mode}');")
    print('PASS schedule-first admission: exact occurrence pairing, immutable adoption, stable replay, new knowledge time and read/write authority', flush=True)
