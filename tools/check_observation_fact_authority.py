"""Owned adversarial checks for selected fact history; no application or production setup.

The regional-close fixture supplies an early retained context, its current UTC
close pins, and the later unrelated facts from the exact-pin checks. Temporary
policy faults roll back. Committed observations/closes use an isolated synthetic
fact and two new sessions; no existing close or observation is rewritten.
"""
from contextlib import contextmanager
import json
from pathlib import Path
import select
import subprocess
import time
from uuid import UUID, uuid4


def run_checks(query, manifest, context, pins, psql, environment, owned_root):
    root = Path(owned_root).resolve()
    assert (root / '.openetrm-preview').is_file()
    assert Path(query('SHOW data_directory;')).resolve() == root / 'data'
    repository = Path(__file__).resolve().parents[1]
    catalog, revision, tenant, workspace = (
        str(UUID(manifest[k])) for k in ('catalogId', 'revisionId', 'tenantId', 'workspaceId'))
    context = str(UUID(context))
    pins = [str(UUID(p)) for p in pins]
    assert len(pins) > 1
    point_signature = 'security.read_observation_pins(uuid,uuid,uuid[])'
    point_definition = query(f"SELECT pg_get_functiondef('{point_signature}'::regprocedure);")

    def quoted(value):
        return "'" + str(value).replace("'", "''") + "'"

    def ids(values):
        return 'ARRAY[' + ','.join(quoted(UUID(v)) + '::uuid' for v in values) + ']::uuid[]'

    selected = ids(pins)

    def issue(subject='viewer', seconds=120):
        return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                     f"'https://preview.invalid',{quoted(subject)},'human_interactive','{tenant}',"
                     f"'{workspace}',NULL,'preview_runtime',{seconds});")

    def bind(token, retained=True, session='test_utc_close'):
        return (f"SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');" +
                (f"SELECT security.use_market_context('{catalog}','{context}');" if retained else '') +
                f"SELECT security.use_close_session('{catalog}',{quoted(session)});")

    def facts(array=selected, cat=catalog, rev=revision):
        return f"security.read_observation_facts('{cat}','{rev}',{array})"

    def rows(source):
        return ("SELECT coalesce(jsonb_agg(to_jsonb(o) ORDER BY o.observed_at,o.received_at,o.id),'[]')::text "
                f"FROM {source} o;")

    def native(array=selected, cat=catalog, rev=revision):
        # Independent predecessor query: both anchors and siblings are ordinary RLS reads.
        return f"""WITH anchors AS MATERIALIZED(
          SELECT DISTINCT series_id,source_code,source_event_id,observed_at,role
          FROM market_data.observations WHERE catalog_id='{cat}' AND definition_revision_id='{rev}' AND id=ANY({array}))
          SELECT coalesce(jsonb_agg(to_jsonb(o) ORDER BY o.observed_at,o.received_at,o.id),'[]')::text
          FROM market_data.observations o JOIN anchors a USING(series_id,source_code,source_event_id,observed_at,role)
          WHERE o.catalog_id='{cat}' AND o.definition_revision_id='{rev}';"""

    def result(sql, retained=True, session='test_utc_close', subject='viewer'):
        return json.loads(query('BEGIN;' + bind(issue(subject), retained, session) + sql + 'ROLLBACK;'))

    def refuses(statement, code='42501'):
        return (f"DO $fault$BEGIN {statement};RAISE EXCEPTION 'Expected fact-read refusal';"
                f"EXCEPTION WHEN SQLSTATE '{code}' THEN NULL;END$fault$;")

    for retained in (False, True):
        for session in ('test_utc_close', 'test_late_close'):
            for array in (selected, selected + f"||{ids(pins[:1])}", ids([])):
                assert result(native(array), retained, session) == result(rows(facts(array)), retained, session)
            wrong_revision = str(uuid4())
            assert result(native(selected, rev=wrong_revision), retained, session) == []
            assert result(rows(facts(rev=wrong_revision)), retained, session) == []
        withdrawn = f"DELETE FROM security.reference_catalog_scopes WHERE(tenant_id,workspace_id)=('{tenant}','{workspace}');"
        before = query('BEGIN;' + withdrawn + bind(issue(), retained) + native() + 'ROLLBACK;')
        after = query('BEGIN;' + withdrawn + bind(issue(), retained) + rows(facts()) + 'ROLLBACK;')
        assert json.loads(before) == json.loads(after)
    print('PASS fact history matches native five-key RLS rows/order for working, retained, other-session, duplicate, empty and wrong-revision reads', flush=True)

    changes = [
        "CREATE POLICY synthetic_fact_allow ON market_data.observations FOR SELECT TO openetrm_runtime USING(true);",
        "CREATE POLICY synthetic_fact_deny ON market_data.observations AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);",
        "ALTER POLICY scoped_read ON market_data.observations USING(false);",
        "DROP POLICY scoped_read ON market_data.observations;",
        "ALTER POLICY temporal_read ON market_data.observations TO openetrm_risk_worker;",
        "ALTER TABLE market_data.observations DISABLE ROW LEVEL SECURITY;",
        "ALTER TABLE market_data.observations NO FORCE ROW LEVEL SECURITY;",
        "REVOKE SELECT ON market_data.observations FROM openetrm_runtime;",
    ]
    for change in changes:
        query('BEGIN;' + change + bind(issue()) + refuses(f'PERFORM * FROM {facts()}') + 'ROLLBACK;')
    # Bind while the context is available, then remove its source scope in the
    # same owned rollback-only session. Refusal must come from the reader itself.
    query('BEGIN;' + bind(issue()) + 'RESET SESSION AUTHORIZATION;' +
          f"DELETE FROM security.market_catalog_scopes WHERE(tenant_id,workspace_id)=('{tenant}','{workspace}');" +
          'SET LOCAL SESSION AUTHORIZATION preview_runtime;' + refuses(f'PERFORM * FROM {facts()}') + 'ROLLBACK;')
    query('BEGIN;GRANT openetrm_risk_worker TO preview_runtime;' + bind(issue()) +
          refuses(f'PERFORM * FROM {facts()}') + 'ROLLBACK;')
    assert query(f"SELECT NOT has_function_privilege('preview_runtime','security.observation_visibility_scope(uuid,uuid,uuid[])','EXECUTE');") == 't'
    invalid = [
        f"security.read_observation_facts(NULL,'{revision}',{selected})",
        f"security.read_observation_facts('{catalog}',NULL,{selected})",
        f"security.read_observation_facts('{catalog}','{revision}',NULL)",
        f"security.read_observation_facts('{catalog}','{revision}',ARRAY[NULL]::uuid[])",
        f"security.read_observation_facts('{catalog}','{revision}',ARRAY[['{pins[0]}'],['{pins[1]}']]::uuid[])",
    ]
    for call in invalid:
        query('BEGIN;' + bind(issue()) + refuses('PERFORM * FROM ' + call, '22023') + 'ROLLBACK;')
    query('BEGIN;' + bind(issue()) + refuses(f'PERFORM * FROM {facts(cat=str(uuid4()))}') + 'ROLLBACK;')
    print('PASS fact reads refuse complete policy/RLS/SELECT drift, risk membership without a live claim, invalid inputs and unauthorized catalogs', flush=True)

    newest = query(f"SELECT id FROM market_data.market_closes WHERE catalog_id='{catalog}' AND session_code='test_utc_close' AND business_date='2020-12-18' ORDER BY publication_order DESC LIMIT 1;")
    late = query(f"""SELECT p.observation_id FROM market_data.market_close_pins p
      JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(p.catalog_id,p.observation_id)
      WHERE(p.catalog_id,p.close_id)=('{catalog}','{newest}') AND e.event_order>(SELECT known_through
      FROM market_data.market_contexts WHERE(catalog_id,context_id)=('{catalog}','{context}')) ORDER BY p.observation_id LIMIT 1;""")
    assert late, 'The retained fixture must precede at least one current close pin'
    other = next(p for p in pins if p != late)
    one_late = ids([late])
    # This rollback-only privileged fault invalidates an UNREQUESTED pin of the whole close.
    fault = f"""SET LOCAL session_replication_role=replica;
      UPDATE market_data.observations SET observed_at=(SELECT at+interval '1 second' FROM market_data.market_closes WHERE(catalog_id,id)=('{catalog}','{newest}')),
       received_at=greatest(received_at,(SELECT at+interval '1 second' FROM market_data.market_closes WHERE(catalog_id,id)=('{catalog}','{newest}')))
       WHERE(catalog_id,id)=('{catalog}','{other}');SET LOCAL session_replication_role=origin;"""
    assert query('BEGIN;' + fault + bind(issue()) + rows(facts(one_late)) + 'ROLLBACK;') == '[]'
    hidden_revision = query(f"SELECT definition_revision_id FROM market_data.close_session_versions WHERE catalog_id='{catalog}' AND code='test_sql_hidden';")
    assert hidden_revision and hidden_revision != revision
    hidden_newest = f"""SET LOCAL session_replication_role=replica;
      INSERT INTO market_data.market_closes SELECT(jsonb_populate_record(NULL::market_data.market_closes,to_jsonb(s)||jsonb_build_object(
       'id','{uuid4()}','definition_revision_id','{hidden_revision}','request_id','{uuid4()}',
       'publication_order',(SELECT max(publication_order)+1 FROM market_data.market_closes WHERE catalog_id='{catalog}')))).*
      FROM market_data.market_closes s WHERE(s.catalog_id,s.id)=('{catalog}','{newest}');SET LOCAL session_replication_role=origin;"""
    assert query('BEGIN;' + hidden_newest + bind(issue()) + rows(facts(one_late)) + 'ROLLBACK;') == '[]'
    print('PASS fact history validates the entire selected close and never falls back from its globally newest hidden revision', flush=True)

    known = query(f"""SELECT o.id FROM market_data.observations o JOIN market_data.knowledge_events e
      ON(e.catalog_id,e.observation_id)=(o.catalog_id,o.id)
      JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
      JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=('{catalog}','{context}')
      JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(m.catalog_id,m.context_id)
      WHERE o.catalog_id='{catalog}' AND o.definition_revision_id='{revision}' AND o.id=ANY({selected})
       AND e.event_order<=m.known_through AND k.known_from<=t.known_at ORDER BY o.id LIMIT 1;""")
    assert known and result(rows(facts(ids([known])))), 'A known visible anchor is required'
    hidden_anchor = str(uuid4())
    insert_hidden = f"""INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object(
      'id','{hidden_anchor}','source_revision_id','{uuid4()}','corrects_id',NULL,'received_at',clock_timestamp()))).*
      FROM market_data.observations o WHERE(o.catalog_id,o.id)=('{catalog}','{known}');"""
    assert query('BEGIN;' + insert_hidden + bind(issue()) + rows(facts(ids([hidden_anchor]))) + 'ROLLBACK;') == '[]'
    print('PASS a hidden raw anchor cannot expose its visible same-fact sibling', flush=True)

    stats = "SELECT 'WORK|'||coalesce(sum(seq_tup_read+idx_tup_fetch),0) FROM pg_stat_xact_user_tables WHERE schemaname='market_data' AND relname='observations';"
    for mode in ('force_custom_plan', 'force_generic_plan'):
        measurements = []
        for extra in (False, True):
            more = ''
            if extra:
                prefix = 'synthetic_fact_work_' + uuid4().hex
                more = f"""INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object(
                  'id',gen_random_uuid(),'source_event_id','{prefix}_'||n,'source_revision_id','first','corrects_id',NULL,'received_at',clock_timestamp()))).*
                  FROM market_data.observations o CROSS JOIN generate_series(1,4096)n WHERE(o.catalog_id,o.id)=('{catalog}','{known}');ANALYZE market_data.observations;"""
            output = query('BEGIN;' + more + f'SET LOCAL plan_cache_mode={mode};' + bind(issue()) +
                           stats + "SELECT 'ROWS|'||" + rows(facts(ids([known]))).removeprefix('SELECT ') + stats +
                           "SELECT 'POINT_ROWS|'||" + rows(
                               f"security.read_observation_pins('{catalog}','{revision}',{ids([known])})"
                           ).removeprefix('SELECT ') + stats + 'ROLLBACK;').splitlines()
            work = [int(line.split('|')[1]) for line in output if line.startswith('WORK|')]
            returned = next(line for line in output if line.startswith('ROWS|'))
            point_returned = next(line for line in output if line.startswith('POINT_ROWS|'))
            assert len(work) == 3
            measurements.append((work[1] - work[0], returned, work[2] - work[1], point_returned))
        assert measurements[0][1] == measurements[1][1]
        assert measurements[0][3] == measurements[1][3]
        assert measurements[1][0] <= measurements[0][0] + 64, (mode, measurements)
        assert measurements[1][2] <= measurements[0][2] + 64, (mode, measurements)
        print(f'PASS {mode}: same-pin observation tuple work '
              f'facts={measurements[0][0]}/{measurements[1][0]}, '
              f'point={measurements[0][2]}/{measurements[1][2]} '
              'remains bounded after another 4096 unrelated facts', flush=True)

    def stop(child):
        if child is None:
            return
        if child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=3)
            except subprocess.TimeoutExpired:
                child.kill(); child.wait(timeout=3)

    @contextmanager
    def gate():
        """Block ONLY the nonempty point call, after its candidate argument exists."""
        namespace, key = 730849208, uuid4().int % (2 ** 30)
        assert '\nBEGIN\n' in point_definition
        injected = point_definition.replace('\nBEGIN\n', f"\nBEGIN\n IF cardinality(requested_pins)>0 AND current_setting('openetrm.synthetic_fact_gate',true)='enabled' THEN PERFORM pg_advisory_xact_lock({namespace},{key});END IF;\n", 1)
        holder = None
        try:
            query(injected)
            holder = subprocess.Popen([str(x) for x in psql], cwd=repository, env=environment,
                                      stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            holder.stdin.write(f'BEGIN;DO $gate$BEGIN PERFORM pg_advisory_xact_lock({namespace},{key});END$gate$;\n\\echo FACT_GATE_READY\n'); holder.stdin.flush()
            end = time.monotonic() + 8
            while True:
                assert holder.poll() is None, holder.stderr.read()
                assert time.monotonic() < end, 'Owned advisory holder did not start'
                if select.select([holder.stdout], [], [], .05)[0] and holder.stdout.readline().strip() == 'FACT_GATE_READY':
                    break

            def release_gate():
                if holder.poll() is None:
                    holder.stdin.write('ROLLBACK;\n\\q\n'); holder.stdin.flush(); holder.stdin.close(); holder.stdin = None
                    holder.communicate(timeout=8)
                    assert holder.returncode == 0
            yield release_gate
        finally:
            stop(holder)
            query(point_definition)

    def pending_read(array, retained, session, action, subject='viewer', seconds=120, expected_error=None):
        pending = None
        try:
            with gate() as release_gate:
                try:
                    token = issue(subject, seconds)
                    application = 'synthetic-fact-read-' + uuid4().hex
                    pending = subprocess.Popen([str(x) for x in psql] + ['--set=VERBOSITY=verbose'], cwd=repository,
                        env={**environment, 'PGAPPNAME': application}, stdin=subprocess.PIPE,
                        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
                    sql = "BEGIN;SET LOCAL statement_timeout='15s';SET LOCAL openetrm.synthetic_fact_gate='enabled';" + bind(token, retained, session) + "SELECT 'RESULT|'||" + rows(facts(array)).removeprefix('SELECT ') + 'ROLLBACK;'
                    pending.stdin.write(sql); pending.stdin.close(); pending.stdin = None
                    end = time.monotonic() + 8
                    while query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{application}' AND wait_event_type='Lock' AND wait_event='advisory';") != '1':
                        assert pending.poll() is None and time.monotonic() < end, 'The fact read never reached its materialized-candidate barrier'
                        time.sleep(.02)
                    action(token)
                    release_gate()
                    stdout, errors = pending.communicate(timeout=10)
                    if expected_error:
                        assert pending.returncode != 0 and '42501' in errors and expected_error in errors, errors
                        return None
                    assert pending.returncode == 0, errors
                    return json.loads(next(line.split('|', 1)[1] for line in stdout.splitlines() if line.startswith('RESULT|')))
                finally:
                    # End the reader before the gate restores the function body.
                    stop(pending)
        finally:
            stop(pending)

    def expires(token):
        end = time.monotonic() + 8
        while query(f"SELECT expires_at<=clock_timestamp() FROM security.request_contexts WHERE id='{token}';") != 't':
            assert time.monotonic() < end, 'The real request deadline did not expire'
            time.sleep(.03)
    pending_read(ids([known]), True, 'test_utc_close', expires, seconds=2, expected_error='Observation read access denied')

    subject = 'fact_deadline_' + uuid4().hex[:12]
    principal, first_grant, later_grant = str(uuid4()), str(uuid4()), str(uuid4())
    query(f"""INSERT INTO security.principals VALUES('{principal}','human',true);
      INSERT INTO security.identity_bindings VALUES('https://preview.invalid','{subject}','{principal}','human_interactive');
      INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{principal}','{subject}','Synthetic fact reader',true,1);
      INSERT INTO security.role_grants SELECT '{first_grant}','{principal}',id,'{tenant}','{workspace}',clock_timestamp()-interval '1 minute',clock_timestamp()+interval '6 seconds',false FROM security.roles WHERE code='viewer';
      INSERT INTO security.role_grants SELECT '{later_grant}','{principal}',id,'{tenant}','{workspace}',clock_timestamp()+interval '3 seconds',clock_timestamp()+interval '1 minute',false FROM security.roles WHERE code='viewer';""")

    def grant_boundary(_token):
        assert query(f"SELECT clock_timestamp()<granted_from FROM security.role_grants WHERE id='{later_grant}';") == 't', 'The future grant was already active at the candidate barrier'
        end = time.monotonic() + 9
        while query(f"SELECT granted_to<=clock_timestamp() FROM security.role_grants WHERE id='{first_grant}';") != 't':
            assert time.monotonic() < end, 'The original grant did not expire'
            time.sleep(.03)
    pending_read(ids([known]), True, 'test_utc_close', grant_boundary, subject=subject, expected_error='Observation history access expired')
    assert result(rows(facts(ids([known]))), subject=subject), 'The already-active successor grant must authorize a fresh invocation'
    print('PASS real token expiry after candidate capture refuses history and the invocation cannot chain to a later-starting grant', flush=True)

    # Build a separate fact and two explicit sessions. Replacement publication uses
    # the native register function, not a rewrite of close rows or a policy fault.
    suffix = uuid4().hex[:12]
    session, other_session = 'test_fact_' + suffix, 'test_other_' + suffix
    approver = query(f"SELECT principal_id FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{tenant}','{workspace}','approver');")
    source_version = query(f"SELECT session_version FROM market_data.market_closes WHERE(catalog_id,id)=('{catalog}','{newest}');")
    for code in (session, other_session):
        query(f"""INSERT INTO market_data.close_sessions VALUES('{catalog}','{code}','Synthetic fact visibility session');
          INSERT INTO market_data.close_session_versions SELECT(jsonb_populate_record(NULL::market_data.close_session_versions,to_jsonb(s)||jsonb_build_object(
           'code','{code}','version',1,'request_id','{uuid4()}','publish_request_id','{uuid4()}'))).*
          FROM market_data.close_session_versions s WHERE(s.catalog_id,s.code,s.version)=('{catalog}','test_utc_close',{source_version});
          INSERT INTO market_data.close_session_curves SELECT catalog_id,'{code}',1,definition_revision_id,curve_code FROM market_data.close_session_curves
           WHERE(catalog_id,code,version)=('{catalog}','test_utc_close',{source_version});
          INSERT INTO security.close_session_grants(id,principal_id,tenant_id,workspace_id,catalog_id,session_code,granted_from,granted_to,revoked,actor_id,request_id)
           VALUES('{uuid4()}','{approver}','{tenant}','{workspace}','{catalog}','{code}',clock_timestamp()-interval '1 minute',clock_timestamp()+interval '1 hour',false,'{approver}','{uuid4()}');""")
    anchor = query(f"SELECT observation_id FROM market_data.market_close_pins WHERE(catalog_id,close_id)=('{catalog}','{newest}') AND input_use='variable' ORDER BY observation_id LIMIT 1;")
    assert anchor
    first, second = str(uuid4()), str(uuid4())
    query(f"""INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object(
      'id','{first}','source_event_id','synthetic_fact_{suffix}','source_revision_id','{uuid4()}','corrects_id',NULL,'received_at',clock_timestamp()))).*
      FROM market_data.observations o WHERE(o.catalog_id,o.id)=('{catalog}','{anchor}');
      INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object(
      'id','{second}','source_revision_id','{uuid4()}','corrects_id','{first}','received_at',clock_timestamp()))).*
      FROM market_data.observations o WHERE(o.catalog_id,o.id)=('{catalog}','{first}');""")

    def record(code, replacement):
        pin_json = query(f"SELECT jsonb_agg(jsonb_build_object('seriesId',series_id,'observationId',CASE WHEN observation_id='{anchor}' THEN '{replacement}'::uuid ELSE observation_id END,'inputUse',input_use))::text FROM market_data.market_close_pins WHERE(catalog_id,close_id)=('{catalog}','{newest}');")
        member_json = query(f"SELECT jsonb_agg(jsonb_build_object('curve',curve_code,'contractId',contract_id,'ordinal',ordinal))::text FROM market_data.market_close_members WHERE(catalog_id,close_id)=('{catalog}','{newest}');")
        at = query(f"SELECT at FROM market_data.market_closes WHERE(catalog_id,id)=('{catalog}','{newest}');")
        return query('BEGIN;' + bind(issue('approver'), False, code) +
            f"SELECT security.register_market_close('{catalog}','{revision}','{uuid4()}','Synthetic fact barrier close','{code}','2020-12-18','{at}',{quoted(pin_json)}::jsonb,{quoted(member_json)}::jsonb);COMMIT;")

    record(session, first)
    assert [r['id'] for r in result(rows(facts(ids([first]))), session=session)] == [first]
    answer = pending_read(ids([first]), True, session, lambda _token: record(session, second))
    assert answer == [], 'A replacement close re-anchored a hidden requested pin to its newly visible sibling'
    assert result(native(ids([first])), session=session) == []
    assert [r['id'] for r in result(rows(facts(ids([second]))), session=session)] == [second]
    # An unrelated session publication must not replace the selected session.
    answer = pending_read(ids([second]), True, session, lambda _token: record(other_session, first))
    assert [r['id'] for r in answer] == [second]
    third = str(uuid4())

    def correction(_token):
        query(f"""INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object(
          'id','{third}','source_revision_id','{uuid4()}','corrects_id','{second}','received_at',clock_timestamp()))).*
          FROM market_data.observations o WHERE(o.catalog_id,o.id)=('{catalog}','{second}');""")
    answer = pending_read(ids([first]), False, session, correction)
    assert {r['id'] for r in answer} == {first, second}, 'A correction committed after candidate capture entered this history read'
    assert {r['id'] for r in result(rows(facts(ids([first]))), retained=False, session=session)} == {first, second, third}
    assert query(f"SELECT pg_get_functiondef('{point_signature}'::regprocedure);") == point_definition
    print('PASS real concurrent replacement close cannot re-anchor hidden input, another session is independent, and post-candidate corrections appear only in the next invocation', flush=True)


def run_frame_checks(query, manifest, context, pins, psql, environment, owned_root, dotnet):
    """Exercise the actual managed batch with a second-call database barrier."""
    root = Path(owned_root).resolve()
    assert (root / '.openetrm-preview').is_file()
    assert Path(query('SHOW data_directory;')).resolve() == root / 'data'
    repository = Path(__file__).resolve().parents[1]
    catalog, tenant, workspace = (str(UUID(manifest[k])) for k in ('catalogId', 'tenantId', 'workspaceId'))
    context = str(UUID(context)); pins = [str(UUID(pin)) for pin in pins]
    assert len(pins) > 1 and len(set(pins)) == len(pins)
    selected = 'ARRAY[' + ','.join("'" + pin + "'::uuid" for pin in pins) + ']'
    series = json.loads(query(f"SELECT json_agg(DISTINCT series_id) FROM market_data.observations WHERE catalog_id='{catalog}' AND id=ANY({selected});"))
    assert len(series) > 1, 'A strict-subset series branch requires another series outside the payload'
    # Build before creating the time-bounded grants; a compiler is not part of
    # the authority fault and must not consume its calibration window.
    build = subprocess.run([str(dotnet), 'build', '--no-restore', 'tests/OpenEtrm.Api.Checks'],
                           cwd=repository, env=environment, capture_output=True, text=True, timeout=120)
    assert build.returncode == 0, build.stdout + build.stderr
    signature = 'security.read_observation_pins(uuid,uuid,uuid[])'
    original = query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")

    def stop(child):
        if child is not None and child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=3)
            except subprocess.TimeoutExpired:
                child.kill(); child.wait(timeout=3)

    for filtered in (False, True):
        holder = managed = None
        key = uuid4().int % (2 ** 30)
        namespace = 730849209
        injected = original.replace('\nBEGIN\n', f"""
BEGIN
 IF cardinality(requested_pins)>0 AND current_setting('openetrm.synthetic_frame_gate',true)='enabled' THEN
  PERFORM set_config('openetrm.synthetic_frame_point_calls',
   (coalesce(nullif(current_setting('openetrm.synthetic_frame_point_calls',true),''),'0')::integer+1)::text,true);
  IF current_setting('openetrm.synthetic_frame_point_calls')='2' THEN PERFORM pg_advisory_xact_lock({namespace},{key});END IF;
 END IF;
""", 1)
        assert injected != original
        try:
            query(injected)
            holder = subprocess.Popen([str(p) for p in psql], cwd=repository, env=environment,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            holder.stdin.write(f'BEGIN;DO $gate$BEGIN PERFORM pg_advisory_xact_lock({namespace},{key});END$gate$;\n\\echo FRAME_GATE_READY\n'); holder.stdin.flush()
            end = time.monotonic() + 8
            while True:
                assert holder.poll() is None and time.monotonic() < end, 'Owned frame gate did not start'
                if select.select([holder.stdout], [], [], .05)[0] and holder.stdout.readline().strip() == 'FRAME_GATE_READY':
                    break
            subject = 'frame_deadline_' + uuid4().hex[:12]
            principal, first, later = str(uuid4()), str(uuid4()), str(uuid4())
            application = 'synthetic-frame-authority-' + uuid4().hex
            query(f"""INSERT INTO security.principals VALUES('{principal}','human',true);
              INSERT INTO security.identity_bindings VALUES('https://preview.invalid','{subject}','{principal}','human_interactive');
              INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{principal}','{subject}','Synthetic frame reader',true,1);
              INSERT INTO security.role_grants SELECT '{first}','{principal}',id,'{tenant}','{workspace}',clock_timestamp()-interval '1 minute',clock_timestamp()+interval '8 seconds',false FROM security.roles WHERE code='viewer';
              INSERT INTO security.role_grants SELECT '{later}','{principal}',id,'{tenant}','{workspace}',clock_timestamp()+interval '4 seconds',clock_timestamp()+interval '1 minute',false FROM security.roles WHERE code='viewer';""")
            deadline = query(f"SELECT granted_to FROM security.role_grants WHERE id='{first}';")
            fixture = root / ('frame-authority-filtered.json' if filtered else 'frame-authority-all.json')
            fixture.write_text(json.dumps(dict(subject=subject, application=application, contextId=context,
                session='test_utc_close', pins=pins, seriesId=series[0], filtered=filtered, initialDeadline=deadline, expectDenied=True)))
            managed = subprocess.Popen([str(dotnet), 'run', '--no-build', '--no-restore', '--project',
                'tests/OpenEtrm.Api.Checks', '--', '--frame-read-authority', str(fixture)],
                cwd=repository, env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            end = time.monotonic() + 10
            while query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{application}' AND wait_event_type='Lock' AND wait_event='advisory';") != '1':
                assert managed.poll() is None and time.monotonic() < end, 'Managed batch did not reach its second nonempty point read'
                time.sleep(.02)
            assert query(f"SELECT clock_timestamp()<granted_from FROM security.role_grants WHERE id='{later}';") == 't', 'The successor grant was already active before the second-call barrier'
            end = time.monotonic() + 10
            while query(f"SELECT granted_to<=clock_timestamp() FROM security.role_grants WHERE id='{first}';") != 't':
                assert time.monotonic() < end, 'The original frame authority did not expire'
                time.sleep(.03)
            holder.stdin.write('ROLLBACK;\n\\q\n'); holder.stdin.flush(); holder.stdin.close(); holder.stdin = None
            holder.communicate(timeout=8); assert holder.returncode == 0
            stdout, errors = managed.communicate(timeout=25)
            assert managed.returncode == 0, stdout + errors
            print(stdout.strip(), flush=True)
            # Prove the refusal did not invalidate the reader or the successor:
            # the SAME managed batch implementation must now return both frames.
            fresh = root / ('frame-authority-filtered-fresh.json' if filtered else 'frame-authority-all-fresh.json')
            fresh.write_text(json.dumps(dict(subject=subject, application='synthetic-frame-fresh-' + uuid4().hex,
                contextId=context, session='test_utc_close', pins=pins, seriesId=series[0], filtered=filtered,
                initialDeadline=query(f"SELECT granted_to FROM security.role_grants WHERE id='{later}';"), expectDenied=False)))
            positive = subprocess.run([str(dotnet), 'run', '--no-build', '--no-restore', '--project',
                'tests/OpenEtrm.Api.Checks', '--', '--frame-read-authority', str(fresh)], cwd=repository,
                env=environment, capture_output=True, text=True, timeout=25)
            assert positive.returncode == 0, positive.stdout + positive.stderr
            print(positive.stdout.strip(), flush=True)
        finally:
            stop(managed); stop(holder)
            query(original)
    assert query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);") == original
    print('PASS actual managed frame batches keep their original authority deadline in both series branches; fresh successor-grant reads remain authorized', flush=True)
