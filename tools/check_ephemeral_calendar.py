"""A transient calendar cut keeps exact knowledge across independent identity transactions."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
from decimal import Decimal
from http.client import HTTPConnection
import json
import time
from urllib.parse import urlsplit
from uuid import uuid4

from assistant_context import holding
from preview_client import PreviewClient
from seed_session_closes import record_close


def interrupted_reads(viewer, request, manifest, query, psql, environment):
    """Hold the first date binding after its cutoff has committed, in this disposable database."""
    lock = 'pg_advisory_xact_lock(367,1)'
    def wait_for_binding(blocked):
        deadline = time.monotonic() + 15
        while query("""SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory'
            AND classid=367 AND objid=1 AND objsubid=2 AND NOT granted);""") != ('t' if blocked else 'f'):
            assert time.monotonic() < deadline, f'Calendar binding did not reach expected wait state {blocked}'
            time.sleep(.025)

    def disconnect():
        address = urlsplit(viewer.url)
        connection = HTTPConnection(address.hostname, address.port)
        try:
            connection.request('POST',viewer.api_prefix + 'market/dates',json.dumps(request),{
                'Content-Type':'application/json', 'Origin':viewer.url, 'X-Preview-CSRF':viewer.token,
                'Cookie':'; '.join(f'{cookie.name}={cookie.value}' for cookie in viewer.jar)})
            wait_for_binding(True)
        finally:
            connection.close()
        wait_for_binding(False)

    # Replace a close the limited calendar actually discloses.
    dates = viewer.call('market/dates',request)['dates']
    latest = datetime.fromisoformat(dates[-1]['frames'][0]['at'])
    frame = next(f for f in manifest['frames'] if f.get('closeId') and datetime.fromisoformat(f['at'])==latest)
    version = int(query(f"SELECT session_version FROM market_data.market_closes WHERE id='{frame['closeId']}';"))
    query(f"""CREATE FUNCTION public.synthetic_hold_calendar() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN PERFORM {lock}; RETURN NEW; END $$;
        CREATE TRIGGER synthetic_hold_calendar AFTER UPDATE OF market_context_id ON security.request_contexts
        FOR EACH ROW WHEN (NEW.calendar_cut IS NOT NULL) EXECUTE FUNCTION public.synthetic_hold_calendar();""")
    try:
        holding(psql,environment,'SELECT '+lock,disconnect)
        with ThreadPoolExecutor(max_workers=1) as pool:
            def replace_close():
                answer = pool.submit(viewer.call,'market/dates',request,expected=422,timeout=40)
                wait_for_binding(True)
                record_close(query,manifest,request['session'],version,frame)
                return answer
            answer = holding(psql,environment,'SELECT '+lock,replace_close)
            assert answer.result()['code']=='close_replaced'
    finally:
        query("""DROP TRIGGER synthetic_hold_calendar ON security.request_contexts;
            DROP FUNCTION public.synthetic_hold_calendar();""")
    print('PASS disconnect releases an active binding and an in-flight replacement refuses stale delivery',flush=True)


def run_checks(url, query, manifest, psql, environment):
    catalog = manifest['catalogId']
    count = "SELECT (to_regclass('market_data.temporal_contexts') IS NOT NULL)::integer"
    child_counts = 'SELECT ' + ','.join(f"(to_regclass('{table}') IS NOT NULL)::integer"
        for table in ('market_data.market_contexts','market_data.catalogue_contexts','trading.contexts'))
    identity = f"""SET SESSION AUTHORIZATION preview_authenticator;
        SELECT security.issue_context('https://preview.invalid','viewer','human_interactive',
            '{manifest["tenantId"]}','{manifest["workspaceId"]}',NULL,'preview_runtime',60) AS token \\gset
        SET SESSION AUTHORIZATION preview_runtime;
        BEGIN;
        SELECT security.use_context(:'token');
        """
    def cutoff():
        return query(identity + f"SELECT * FROM security.calendar_read_cut('{catalog}'); COMMIT;").strip().split('|')

    def identifier_for(selected):
        return query(f"SELECT security.workspace_context_id(\'{catalog}\',DATE \'2020-12-18\',\'{selected[0]}\');")

    def bind(selected, identifier):
        at, prefix, system, timeline = selected
        return (f"SELECT security.bind_operation_read('{catalog}','{identifier}',DATE '2020-12-18',"
                f"'{at}',{int(prefix)},'{system}',{int(timeline)});")

    def session(profile):
        client = PreviewClient(url)
        client.bootstrap()
        client.call('profile', {'profile':profile})
        return client

    assert query("""SELECT NOT has_table_privilege('openetrm_runtime','security.bound_calendar_context','SELECT')
        AND to_regclass('security.read_temporal_contexts') IS NULL
        AND to_regprocedure('security.bind_calendar_read(uuid,uuid,date,timestamptz,integer,text,integer)') IS NULL
        AND NOT has_function_privilege('openetrm_risk_worker','security.calendar_read_cut(uuid)','EXECUTE');""")=='t'
    editor = session('editor')
    workspace = editor.call('workspace')
    close = next(frame for frame in workspace['frames'] if frame['at'].startswith('2020-12-18T16:00') and frame.get('close'))
    revised = set(query(f"SELECT corrects_id FROM market_data.observations WHERE catalog_id='{catalog}' AND corrects_id IS NOT NULL;").splitlines())
    original = next(row for row in workspace['observations'] if row['id'] in close['observationIds']
                    and row['role']=='daily_settlement' and row['id'] not in revised)
    old_cut = cutoff()
    changed_value = Decimal(original['value']) + Decimal('1.75')
    correction = editor.call('corrections', {'snapshotId':close['id'], 'observationId':original['id'], 'value':str(changed_value)})
    corrected_id = correction['observationId']
    assert corrected_id != original['id']
    new_cut = cutoff()
    before = int(query(count))
    children_before = query(child_counts)
    values = (f"SELECT value FROM market_data.observations WHERE catalog_id='{catalog}' "
              f"AND id IN ('{original['id']}','{corrected_id}') ORDER BY value;")
    for selected, expected in [(old_cut,[Decimal(original['value'])]), (new_cut,[Decimal(original['value']),changed_value]),
                               (old_cut,[Decimal(original['value'])])]:
        identifier = identifier_for(selected)
        result = query(identity + "SAVEPOINT calendar_day;" + bind(selected,identifier) +
                       values + "ROLLBACK TO SAVEPOINT calendar_day; SELECT security.current_write_context(); COMMIT;").split()
        assert result[-1]=='t' and [Decimal(v) for v in result[:-1]]==expected, result
    assert int(query(count))==before, 'Rebinding the original cutoff retained a read'
    at, prefix, system, timeline = old_cut
    for invalid, code in [((at,int(prefix)+1,system,timeline),'temporal_context'),
                          ((at,prefix,system,int(timeline)+1),'knowledge_coverage')]:
        try:
            query(identity + bind(invalid,identifier_for(old_cut)) + 'ROLLBACK;')
        except RuntimeError as error:
            assert code in str(error), error
        else:
            raise AssertionError('Accepted a cutoff with inconsistent publication evidence')
    # An incorrectly scoped caller cannot commit the temporary binding.
    try:
        query.raw(identity + bind(new_cut,identifier_for(new_cut)) + 'COMMIT;')
    except RuntimeError as error:
        assert 'read_source_not_released' in str(error), error
    else:
        raise AssertionError('A temporary calendar binding survived commit')
    print('PASS earlier and later cuts bracket the same price correction and scope exit restores the parent',flush=True)

    viewers = [session('viewer'), session('viewer')]
    template = next(row['definition'] for row in viewers[0].call('custom-curves')['templates'] if row['code']=='ng_average')
    request = {'session':'demo_close_1600', 'curve':None, 'definition':template, 'timeZone':'UTC', 'maximumDates':10}
    watermark = f"SELECT certified_through FROM market_data.knowledge_clocks WHERE catalog_id='{catalog}';"
    prior = query(watermark)
    for invalid in ({'curve':'bridge_basis'}, {'maximumDates':0}, {'timeZone':'Not/AZone'}):
        viewers[0].call('market/dates',{**request, **invalid},expected=422)
        assert query(watermark)==prior, 'Malformed calendar request advanced the publication watermark'
    def strip(viewer):
        return viewer.call('market/dates',request)
    interrupted_reads(viewers[0],request,manifest,query,psql,environment)
    assert int(query(count))==before, 'A cancelled or superseded read retained a context'
    expected = strip(viewers[0])
    assert expected['dates']
    with ThreadPoolExecutor(max_workers=len(viewers)) as pool:
        assert list(pool.map(strip,viewers))==[expected,expected]
    assert strip(viewers[0])==expected and int(query(count))==0
    from check_workspace_as_of import open_tab, prepare, select, priced
    tab=open_tab(viewers[0])
    for business_day in ('2020-12-17','2020-12-18','2020-12-17'):
        selected=prepare(tab,business_day)
        select(tab,selected)
        assert priced(tab,business_day)[0]==selected
    assert int(query(count))==0 and query(child_counts)==children_before, 'Calendar or As of reads recreated saved contexts'
    print('PASS independent viewers return the same custom-curve dates without saved contexts',flush=True)
