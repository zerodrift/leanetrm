"""Statement scaling and disclosure boundaries in the launcher's disposable database."""
from copy import deepcopy
from datetime import date, timedelta
import json
import hashlib
import re
import threading
import time
from assistant_context import holding
from preview_client import PreviewClient
from seed_session_closes import publish_session, record_close

SESSION = 'synthetic_calendar_scaling'
# Business projections captured from unchanged cb582382 in this exact synthetic fixture.
EXPECTED = {'curve': 'f899a197032bd27475da3adee6f027f3be06778230ca80405e18f1087153ba63', 'denied': '4292f04ecba1c37badc016fc5649e1131f4737bf9fca2ef2d44ce615f7af1d65', 'hidden': 'c40959aaece96f7549b10036de77257a0682d1793a40b7d9a31cbc66b5019baf', 'rate': '67399245cd08d8a1f0b07d00827e581fe398d136af30a46d915ead808e35ee0d', 'replaced': 'd444694f6c3a9de28159180e0ccd77d9f622f11cb091944c69249410c82967e5', 'retained': 'e88ca9402cd8c6d63bfefa0d89d6971fa261b8253e1c124e2e2becdc0486611b', 'workspace': '640376bb23df1cf40d66d4ceac651a820d7729c68d2ff60cb143c0bd0d3d442b'}


def projection(answer):
    """Keep business answers; random fixture identities and capture instants vary per run."""
    value = deepcopy(answer)
    value.pop('frameCatalog', None)
    if value.get('context'):
        value['context'] = {'effectiveOn': value['context']['effectiveOn']}
    for day in value.get('dates', []):
        for frame in day['frames']:
            frame.pop('id')
    for curve in value.get('curves', []):
        curve.pop('id')
    return value


def run_checks(url, query, manifest, psql, environment, log):
    client = PreviewClient(url); client.bootstrap(); client.call('profile', {'profile': 'viewer'})
    body = {'session': 'demo_close_1600', 'curve': 'bridge_basis', 'timeZone': 'UTC'}
    answers = {}
    for name, curve in [('curve', 'bridge_basis'), ('workspace', None), ('rate', 'interest_rates')]:
        answers[name] = projection(client.call('market/dates', {**body, 'curve': curve}))
    client.call('market/context', {'effectiveOn': '2020-12-18', 'knownAt': None, 'mode': 'current', 'selection': 'latest_correction'})
    answers['retained'] = projection(client.call('market/dates', body))
    client.call('market/current', {})
    source = next(f for f in manifest['frames'] if f['at'].startswith('2020-12-18T16:00:00'))
    replaced = record_close(query, manifest, 'demo_close_1600',
                            int(query(f"SELECT session_version FROM market_data.market_closes WHERE id='{source['closeId']}';")), source)
    answers['replaced'] = projection(client.call('market/dates', body))
    signature = 'security.market_close_visible(uuid,uuid)'
    original = query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    try:
        assert original.count('\nBEGIN\n') == 1
        query(original.replace('\nBEGIN\n', f"\nBEGIN\n IF close_id='{replaced}'::uuid THEN RETURN false;END IF;\n", 1))
        answers['hidden'] = projection(client.call('market/dates', body))
    finally:
        query(original)
    client.call('profile', {'profile': 'admin'})
    answers['denied'] = client.call('market/dates', body, expected=403)
    client.call('profile', {'profile': 'viewer'})
    actual = {name: hashlib.sha256(json.dumps(answer, sort_keys=True).encode()).hexdigest() for name, answer in answers.items()}
    assert actual == EXPECTED, (actual, answers)
    print('PASS calendar answers equal the unchanged implementation for curve, workspace, rate, retained, replaced, hidden and denied reads', flush=True)
    # Keep 96 candidates within the global 128-frame discovery bound. Existing facts stay immutable.
    query("INSERT INTO presentation.close_session_listings SELECT v.catalog_id,v.code,1,max(v.version),'Synthetic retired fixture session',false,pg_current_xact_id() FROM market_data.close_session_versions v GROUP BY v.catalog_id,v.code;")

    # Two complete bridge closes, then increasingly many closes which only declare ng_monthly.
    # Session versions make root membership explicit; no inferred roots or financial inputs.
    publish_session(query, manifest, SESSION, 'Synthetic calendar scaling', [
        {'curves': ['ng_monthly'], 'cutoff': '16:00', 'from': '2020-01-01', 'to': '2020-12-17'},
        {'curves': sorted(source['curveContracts']), 'cutoff': '16:00', 'from': '2020-12-17', 'to': '2100-01-01'}])
    for day in ('2020-12-17', '2020-12-18'):
        record_close(query, manifest, SESSION, 2, next(f for f in manifest['frames'] if f['at'].startswith(day+'T16:00:00')))
    older = next(f for f in manifest['frames'] if f['at'].startswith('2020-12-11T16:00:00'))
    counts, report = {}, []
    def measure(path, request):
        offset = log.stat().st_size
        start = time.perf_counter(); answer = client.call(path, request); elapsed = time.perf_counter()-start
        with log.open() as stream:
            stream.seek(offset); lines = stream.read().splitlines()
        statements = [line for line in lines if re.match(r'preview_runtime \d+ LOG:  duration: .* (?:execute [^:]*|statement):', line)]
        db_ms = sum(float(re.search(r'duration: ([\d.]+) ms', line)[1]) for line in statements)
        return answer, {'seconds': round(elapsed, 6), 'statements': len(statements), 'databaseMs': round(db_ms, 3)}
    for total in (24, 48, 96):
        for index in range(len(counts) and max(counts)-2 or 0, total-2):
            frame = deepcopy(older)
            frame['at'] = (date(2020, 12, 16)-timedelta(days=index)).isoformat()+'T16:00:00Z'
            frame['curveContracts'] = {'ng_monthly': frame['curveContracts']['ng_monthly']}
            record_close(query, manifest, SESSION, 1, frame)
        request = {**body, 'session': SESSION}
        client.call('market/dates', request)  # warm JIT/pool
        samples = [measure('market/dates', request) for _ in range(3)]
        assert all(len(answer['dates']) == 2 and len(answer['unavailable']) == total-2 for answer, _ in samples), samples[0]
        counts[total] = max(sample['statements'] for _, sample in samples)
        report.append({'closes': total, 'request': 'curve', 'samples': [sample for _, sample in samples]})
        if total == 96:
            for name, path, selected in [('latest', 'market/dates', {**request, 'maximumDates': 1}),
                                          ('workspace', 'market/dates', {**request, 'curve': None}),
                                          ('last', 'market/last-closes', {'session': SESSION}),
                                          ('navigation', 'workspace/navigation', None)]:
                answer, sample = measure(path, selected)
                report.append({'closes': total, 'request': name, 'samples': [sample]})
    workload = query(f"SELECT count(DISTINCT p.definition_revision_id),count(*),count(DISTINCT p.observation_id),count(DISTINCT m.contract_id) FROM market_data.market_closes c JOIN market_data.market_close_pins p ON(p.catalog_id,p.close_id)=(c.catalog_id,c.id) LEFT JOIN market_data.market_close_members m ON(m.catalog_id,m.close_id,m.definition_revision_id)=(p.catalog_id,p.close_id,p.definition_revision_id) WHERE c.session_code='{SESSION}';")
    print('CALENDAR_MEASUREMENTS=' + json.dumps({'runs': report, 'sourcePinMemberJoin': workload}), flush=True)
    failures = []
    if not (0 < counts[24] == counts[48] == counts[96]):
        failures.append(f'Statements grow with unselected closes: {counts}')

    # Hold only observation reads: frame discovery can finish under the gate before execution blocks.
    waiting_sql = "SELECT count(DISTINCT pid) FROM pg_locks WHERE relation='market_data.observations'::regclass AND NOT granted AND mode='AccessShareLock';"
    def start_read(expected=200):
        outcome = {}
        def work():
            try: outcome['answer'] = client.call('market/dates', {**body, 'session': SESSION}, expected=expected)
            except Exception as error: outcome['error'] = repr(error)
        worker = threading.Thread(target=work); worker.start()
        deadline = time.monotonic()+8
        while query(waiting_sql) != '1':
            assert worker.is_alive() and time.monotonic()<deadline, outcome
            time.sleep(.025)
        return worker, outcome
    def concurrent():
        worker, outcome = start_read()
        try:
            client.call('access', timeout=1)
        except Exception:
            failures.append('An ordinary request waits behind a market/dates observation read')
        return worker, outcome
    worker, outcome = holding(psql, environment, 'LOCK market_data.observations IN ACCESS EXCLUSIVE MODE', concurrent)
    worker.join(20); assert not worker.is_alive() and 'error' not in outcome, outcome
    principal = "(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='viewer')"
    grants = json.loads(query(f"SELECT json_agg(json_build_object('id',id,'until',granted_to)) FROM security.role_grants WHERE principal_id={principal};"))
    try:
        def expired():
            worker, outcome = start_read(expected=403)
            query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '1 second' WHERE principal_id={principal};")
            deadline = time.monotonic()+5
            while query(f"SELECT bool_and(granted_to<clock_timestamp()) FROM security.role_grants WHERE principal_id={principal};") != 't':
                assert time.monotonic()<deadline
                time.sleep(.025)
            return worker, outcome
        worker, outcome = holding(psql, environment, 'LOCK market_data.observations IN ACCESS EXCLUSIVE MODE', expired)
        worker.join(20); assert not worker.is_alive() and 'error' not in outcome, outcome
    finally:
        for grant in grants:
            query(f"UPDATE security.role_grants SET granted_to='{grant['until']}'::timestamptz WHERE id='{grant['id']}';")
    assert not failures, '; '.join(failures)
    print('PASS calendar read statement count is independent of unselected closes; ordinary requests proceed and expired authority refuses disclosure', flush=True)
