"""D319 regional closes over HTTP: recording for a session business day, the close in force and replaced history."""
import json
from datetime import datetime
import threading
from uuid import uuid4
from preview_client import PreviewClient
from close_session_fixture import draft, grant, publish


def instant(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00')).isoformat()


def snapshot(who, at):
    """A snapshot the session offers at an instant: a recorded close in force or a working snapshot."""
    workspace = who.call('workspace')
    return next(f for f in workspace['frames'] if instant(f['at']) == instant(at))


def client(url, profile):
    value = PreviewClient(url); value.bootstrap(); value.call('profile', {'profile': profile}); return value


def run_checks(url, query, manifest):
    catalog, revision = manifest['catalogId'], manifest['revisionId']
    viewer, editor, approver, admin = (client(url, p) for p in ('viewer', 'editor', 'approver', 'admin'))
    frame = snapshot(approver, '2020-12-18T16:00:00Z')
    curves = sorted(frame['curveContracts'])
    publish(editor, approver, draft('test_utc_close', 'test_business', '16:00', curves))
    publish(editor, approver, draft('test_late_close', 'test_minus_18_days', '22:00', curves))

    def record(who, session, day, name='Synthetic regional close', expected=200, snapshot=frame['id']):
        return who.call('market/closes/register', {'requestId': str(uuid4()), 'snapshotId': snapshot, 'name': name,
                                                   'session': session, 'businessDate': day}, expected=expected)

    assert record(approver, 'test_utc_close', '2020-12-18', expected=403)['code'] == 'close_grant', 'Publishing authority alone recorded a close'
    record(editor, 'test_utc_close', '2020-12-18', expected=403)
    grant(admin, query, manifest, 'approver', 'test_utc_close')
    body = {'requestId': str(uuid4()), 'snapshotId': frame['id'], 'name': 'Synthetic UTC close', 'session': 'test_utc_close', 'businessDate': '2020-12-18'}
    first = approver.call('market/closes/register', body)
    assert first == approver.call('market/closes/register', body), 'A retried recording created another close'
    assert approver.call('market/closes/register', {**body, 'name': 'Different replay'}, expected=409)['code'] == 'pilot_close_conflict'
    assert (first['session'], first['sessionVersion'], first['businessDate'], instant(first['at'])) == ('test_utc_close', 1, '2020-12-18', instant('2020-12-18T16:00:00Z')), first
    assert first['replacedBy'] is None
    print('PASS a close is recorded for a session business day at its cutoff, idempotently and only under a session grant', flush=True)

    # The late session's 22:00 cutoff on 17 December is 16:00 UTC on 18 December: the business day stays 17 December.
    assert record(approver, 'test_late_close', '2020-12-17', expected=403)['code'] == 'close_grant', 'One session grant recorded another session'
    grant(admin, query, manifest, 'approver', 'test_late_close')
    late = record(approver, 'test_late_close', '2020-12-17')
    assert (late['businessDate'], instant(late['at'])) == ('2020-12-17', instant('2020-12-18T16:00:00Z')), late
    for session, day, code in [('test_utc_close', '2020-12-17', 'close_session_instant'), ('test_utc_close', '2020-12-19', 'close_session'),
                               ('test_utc_close', '2021-12-02', 'close_session')]:
        assert record(approver, session, day, expected=422)['code'] == code, (session, day, code)
    # Nobody holds a grant for a session that does not exist.
    assert record(approver, 'not_a_session', '2020-12-18', expected=403)['code'] == 'close_grant'
    approver.call('market/closes/register', {**body, 'requestId': str(uuid4()), 'unexpected': 1}, expected=400)
    print('PASS a business day keeps its session date across UTC midnight; other days, instants and sessions are refused', flush=True)

    # The frame must carry exactly the session's curves; the database names what is missing or extra.
    series = dict(json.loads(query(f"SELECT json_agg(json_build_array(id,series_id))::text FROM market_data.observations WHERE catalog_id='{catalog}' AND id IN({','.join(repr(o) for o in frame['observationIds'])});")))
    def register_sql(members, session='test_utc_close'):
        pins = [{'seriesId': series[o], 'observationId': o, 'inputUse': 'variable'} for o in frame['observationIds']]
        encoded = lambda value: "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
        token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
        return query(f"""SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');
        DO $$BEGIN PERFORM security.register_market_close('{catalog}','{revision}','{uuid4()}','Synthetic partial','{session}','2020-12-18','2020-12-18T16:00:00Z',{encoded(pins)},{encoded(members)});
        RAISE EXCEPTION 'accepted';EXCEPTION WHEN SQLSTATE 'P0001' THEN DECLARE detail text;BEGIN GET STACKED DIAGNOSTICS detail=PG_EXCEPTION_DETAIL;
        PERFORM set_config('openetrm.check_refusal',SQLERRM||'|'||detail,false);END;END$$;SELECT current_setting('openetrm.check_refusal');COMMIT;""").splitlines()[-1]
    members = [{'curve': c, 'contractId': k, 'ordinal': n} for c in curves for n, k in enumerate(frame['curveContracts'][c])]
    missing = register_sql([m for m in members if m['curve'] != 'future'])
    assert 'close_incomplete' in missing and 'future' in missing, missing
    publish(editor, approver, draft('test_narrow_close', 'test_business', '16:00', ['future']))
    grant(admin, query, manifest, 'approver', 'test_narrow_close')
    extra = register_sql(members, 'test_narrow_close')
    assert 'close_outside_session' in extra and 'local_power' in extra, extra
    print('PASS a close must carry exactly its session curves, naming those missing or extra', flush=True)

    # Two recordings for one session and day race; the later publication is in force and both succeed.
    racers, results = [client(url, 'approver') for _ in range(2)], []
    def race(who):
        try:
            own = snapshot(who, '2020-12-18T16:00:00Z')
            results.append(record(who, 'test_utc_close', '2020-12-18', 'Synthetic race', snapshot=own['id']))
        except Exception as error: results.append(repr(error))
    threads = [threading.Thread(target=race, args=(who,)) for who in racers]
    for thread in threads: thread.start()
    for thread in threads: thread.join()
    assert len(results) == 2 and all(isinstance(r, dict) for r in results) and len({r['id'] for r in results}) == 2, results
    latest = max(results, key=lambda r: int(query(f"SELECT publication_order FROM market_data.market_closes WHERE id='{r['id']}';")))
    assert query(f"SELECT id FROM market_data.market_closes WHERE catalog_id='{catalog}' AND session_code='test_utc_close' AND business_date='2020-12-18' ORDER BY publication_order DESC LIMIT 1;") == latest['id']
    print('PASS concurrent recordings for one session day both succeed and the later one is in force', flush=True)

    # Only the close in force is a choice: the calendar lists it once per business day and counts the closes it replaced.
    replaced = sorted(r['id'] for r in [first, *results] if r['id'] != latest['id'])
    dates = lambda who, session: who.call('market/dates', {'curve': None, 'timeZone': 'UTC', 'session': session})
    utc = dates(viewer, 'test_utc_close')
    day = next(d for d in utc['dates'] if d['date'] == '2020-12-18')
    assert [f['id'] for f in day['frames']] == [latest['id']] and day['replaced'] == 2, day
    assert day['frames'][0]['businessDate'] == '2020-12-18'
    assert [f['id'] for d in dates(viewer, 'test_late_close')['dates'] if d['date'] == '2020-12-17' for f in d['frames']] == [late['id']]
    viewer.call('market/dates', {'curve': None, 'timeZone': 'UTC'}, expected=400)
    history = viewer.call('market/closes/history', {'session': 'test_utc_close', 'businessDate': '2020-12-18'})
    assert history['inForce']['id'] == latest['id'] and sorted(c['id'] for c in history['replaced']) == replaced, history
    assert all(c['recordedBy'] == 'Synthetic approver' and c['recordedAt'] for c in history['replaced']), history['replaced']
    workspace = viewer.call('workspace')
    listed = {f['id'] for f in workspace['frames']}
    assert latest['id'] in listed and not listed & set(replaced), 'A replaced close is still offered as a close'
    assert all('close' in f for f in workspace['frames']), 'Every built-in frame is a recorded close'
    print('PASS the calendar and workspace offer only the close in force; replaced closes are history', flush=True)

    # A replaced close is refused for new work, naming the close in force; the close in force is accepted.
    viewer.call('workspace')
    stale = viewer.call('evaluate', {'snapshotId': latest['id'], 'curve': 'future'})
    assert stale['points'], stale
    newer = record(approver, 'test_utc_close', '2020-12-18', 'Synthetic newer')
    refused = viewer.call('evaluate', {'snapshotId': latest['id'], 'curve': 'future'}, expected=422)
    assert refused['code'] == 'close_replaced' and newer['id'] in refused['message'], refused
    print('PASS a close replaced after it was offered is refused for new work, naming the close in force', flush=True)

    # Always latest (D319): a historical view reads the close in force for its business day even when it was recorded after the
    # view's knowledge cutoff, and says so. Its business day, not the UTC date of its instant, matches the view's date.
    historical = client(url, 'viewer')
    historical.call('market/context', {'effectiveOn': '2020-12-18', 'knownAt': None, 'mode': 'current', 'selection': 'latest_correction'})
    after = record(approver, 'test_utc_close', '2020-12-18', 'Synthetic after cutoff')
    # A historical view offers closes under its own frame identities, each naming the close it reads.
    closes = {f['originCloseId']: f for f in historical.call('workspace')['frames']}
    assert after['id'] in closes and newer['id'] not in closes, sorted(closes)
    assert closes[after['id']]['close']['recordedAfterCutoff'] is True, closes[after['id']]
    assert historical.call('evaluate', {'snapshotId': closes[after['id']]['id'], 'curve': 'future'})['points']
    day = next(d for d in historical.call('market/dates', {'curve': None, 'timeZone': 'UTC', 'session': 'test_utc_close'})['dates'] if d['date'] == '2020-12-18')
    assert [f['id'] for f in day['frames']] == [closes[after['id']]['id']], day
    late_view = client(url, 'viewer')
    late_view.call('market/context', {'effectiveOn': '2020-12-17', 'knownAt': None, 'mode': 'current', 'selection': 'latest_correction'})
    late_closes = {f['originCloseId']: f['id'] for f in late_view.call('workspace')['frames']}
    assert late['id'] in late_closes, late_closes
    assert late_view.call('evaluate', {'snapshotId': late_closes[late['id']], 'curve': 'future'})['points']
    late_days = late_view.call('market/dates', {'curve': None, 'timeZone': 'UTC', 'session': 'test_late_close'})['dates']
    assert [f['id'] for d in late_days if d['date'] == '2020-12-17' for f in d['frames']] == [late_closes[late['id']]], late_days
    print('PASS historical views read the close in force recorded after their cutoff, matched by session business day', flush=True)
