"""Independent shared meter-finality contracts against an owned stock PostgreSQL database."""
import hashlib
import json
import subprocess
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4


def run_checks(query, manifest, psql, environment, dotnet, temporary):
    catalog, definition, tenant, workspace = [manifest[k] for k in
        ('catalogId', 'revisionId', 'tenantId', 'workspaceId')]
    source = 'test_asset_a_60'
    series = manifest['ppa']['series'][source]['meter']
    start, end = '2020-11-01T05:00:00Z', '2020-11-01T06:00:00Z'
    effective = '2020-11-01T06:00:00Z'
    passed = 0

    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS meter finality: ' + name, flush=True)

    def token(profile='editor', ttl=120):
        return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                     f"'https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',{ttl});")

    def runtime(sql, profile='editor', issued=None):
        return ('BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;'
                f"SELECT security.use_context('{issued or token(profile)}');" + sql + 'COMMIT;')

    def call(sql, profile='editor'):
        return json.loads(query(runtime(sql, profile)))

    def reject(sql, code, profile='editor'):
        try:
            query(runtime(sql, profile))
        except RuntimeError as e:
            assert code in str(e), str(e)
        else:
            raise AssertionError('Expected refusal: ' + code)

    def raw(expected, value, first=start, last=end):
        request = str(uuid4())
        value_sql = 'NULL' if value is None else "'" + str(value) + "'"
        return call(f"SELECT market_data.append_interval_series_revision('{series}','{request}',"
                    f"'{hashlib.sha256(request.encode()).hexdigest()}',{expected},'Synthetic meter evidence',"
                    f"ARRAY['{first}']::timestamptz[],ARRAY['{last}']::timestamptz[],ARRAY[{value_sql}]::text[]);")

    def publish(request=None, predecessor=None, revision=1, state='final', at=effective, first=start, last=end, src=source):
        predecessor_sql = "'" + predecessor + "'::uuid" if predecessor else 'NULL::uuid'
        return (f"SELECT settlement_data.publish_meter_finality('{catalog}','{definition}','{src}','generated',"
                f"'{first}','{last}',{revision},'{state}','{at}',{predecessor_sql},'{request or uuid4()}',"
                "'Synthetic verification');")

    def capture():
        return query(runtime(f"SELECT security.capture_date_context('{catalog}','2020-11-01',NULL);", 'viewer'))

    def read(context, at='2020-11-01T07:00:00Z'):
        return (f"SELECT security.use_market_context('{catalog}','{context}');"
                f"SELECT settlement_data.read_meter_finality('{catalog}','{definition}','{source}','generated',"
                f"'{start}','{end}','{context}','{at}');")

    # Isolate raw reading revisions from the unchanged Phase C HTTP fixture.
    original_definition, original_series = definition, series
    series = str(uuid4())
    query("INSERT INTO market_data.interval_series SELECT(jsonb_populate_record(NULL::market_data.interval_series,to_jsonb(s)||"
          f"jsonb_build_object('id','{series}','code','synthetic_finality_meter'))).* FROM market_data.interval_series s WHERE id='{original_series}';")
    inventory = ('revision_periods', 'contracts', 'curves', 'series', 'curve_members', 'curve_inputs', 'constituent_mappings',
                 'rate_curves', 'rate_curve_points', 'contract_intervals', 'curve_blocks', 'shaped_curves', 'shaped_curve_blocks',
                 'curve_physical_price_sources', 'curve_actual_rules', 'shaped_curve_actual_rules', 'curve_expiry_rules',
                 'index_fixing_observations', 'generation_assets', 'generation_asset_versions', 'location_valuation_assignments', 'volume_sources')

    def clone_definition(old, meter=None, period=None):
        new = str(uuid4())
        sql = (f"BEGIN;INSERT INTO market_data.revisions VALUES('{catalog}','{new}',"
               f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','draft',0);")
        for table in inventory:
            change = f"jsonb_build_object('revision_id','{new}')"
            if table == 'volume_sources' and meter:
                change += f"||CASE WHEN r.code='{source}' THEN jsonb_build_object('metered_series_id','{meter}') ELSE '{{}}'::jsonb END"
            if table == 'revision_periods' and period:
                change += f"||jsonb_build_object('effective_from','{period[0]}','effective_to','{period[1]}')"
            sql += (f"INSERT INTO market_data.{table} SELECT(jsonb_populate_record(NULL::market_data.{table},to_jsonb(r)||{change})).* "
                    f"FROM market_data.{table} r WHERE(r.catalog_id,r.revision_id)=('{catalog}','{old}');")
        query(sql+f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{new}');COMMIT;")
        return new

    definition = clone_definition(original_definition, meter=series)

    # This behavior check fails before the new operation exists, not on a source-text assertion.
    raw(0, 50)
    reject(publish(), 'meter_finality_denied')
    check('import permission alone cannot verify a meter')
    query("INSERT INTO security.role_permissions SELECT id,'VerifySettlementMeters' FROM security.roles WHERE code='market_data_editor';")
    reject(publish(), 'meter_finality_calendar')
    check('missing retained clock proof refuses instead of using current tzdata')
    from preview_fixture import installed_tzdb
    calendar_request, calendar_sql = temporary/'finality-calendar.json', temporary/'finality-calendar.sql'
    calendar_request.write_text(json.dumps({'referenceCatalogId': manifest['referenceCatalogId'],
        'referenceRevisionId': manifest['referenceRevisionId'], 'from': '2020-11-01', 'through': '2020-11-01',
        'clocks': [{'code': 'synthetic_eastern', 'kind': 'iana', 'fixedOffsetSeconds': None,
                    'ianaZone': 'America/New_York', 'tzdbVersion': installed_tzdb()}]}))
    subprocess.run([str(dotnet), 'src/OpenEtrm.Api/bin/Debug/net10.0/OpenEtrm.Api.dll',
                    '--render-power-days', str(calendar_request), str(calendar_sql)],
                   env=environment, check=True, timeout=30)
    query(calendar_sql.read_text())
    ambiguous = ("INSERT INTO power_data.operating_days SELECT reference_catalog_id,reference_revision_id,clock_code,"
                 "operating_date+1,starts_at,ends_at,clock_fingerprint FROM power_data.operating_days "
                 f"WHERE(reference_catalog_id,reference_revision_id,clock_code)=('{manifest['referenceCatalogId']}',"
                 f"'{manifest['referenceRevisionId']}','synthetic_eastern');")
    try:
        query('BEGIN;'+ambiguous+runtime(publish()).removeprefix('BEGIN;').removesuffix('COMMIT;')+'ROLLBACK;')
    except RuntimeError as error:
        assert 'meter_finality_calendar' in str(error), str(error)
    else:
        raise AssertionError('Ambiguous retained operating-day coverage admitted')
    check('ambiguous retained calendar coverage refuses')
    old_cut = capture()
    request = str(uuid4())
    command = publish(request)
    original = call(command)
    check('exact immutable final assertion pins raw revision', original['readingRevision'] == 1 and original['state'] == 'final')
    new_cut = capture()
    reject(read(old_cut), 'meter_finality_missing', 'viewer')
    check('finality own commit time excludes it at older certified knowledge')
    selected = call(read(new_cut), 'viewer')
    check('certified selection retains exact value and finality knowledge', selected['verifier']['permission'] == 'VerifySettlementMeters' and len(selected['clockFingerprint']) == 64 and selected['value'] == '50' and selected['headId'] == original['headId'] and selected['knownFrom'] >= selected['readingKnownFrom'])
    reject(read(new_cut, '2020-11-01T05:59:59Z'), 'meter_finality_missing', 'viewer')
    check('business time also precedes finality eligibility')
    check('exact replay returns original receipt', call(command) == original)
    reject(publish(request, state='withdrawn'), 'meter_finality_command_conflict')
    reject(publish(predecessor=str(uuid4())), 'meter_finality_predecessor')
    reject(publish(predecessor=original['headId'], at='2020-11-01T05:00:00Z'), 'meter_finality_input')
    check('changed request, unknown predecessor and pre-interval finality refuse')
    raw(1, 51)
    check('unverified raw correction leaves original final authoritative', call(read(capture()), 'viewer')['value'] == '50')
    corrected = call(publish(predecessor=original['headId'], revision=2))
    check('later published correction uses explicit raw revision', call(read(capture()), 'viewer')['value'] == '51')
    check('old certified context retains original finality after correction', call(read(new_cut), 'viewer')['value'] == '50')
    withdrawn = call(publish(predecessor=corrected['headId'], revision=2, state='withdrawn'))
    unavailable = call(read(capture()), 'viewer')
    check('withdrawal returns authoritative unavailable head without fallback', unavailable['state'] == 'withdrawn' and unavailable['value'] is None)
    suspended = call(publish(predecessor=withdrawn['headId'], revision=2, state='suspended'))
    check('suspension also blocks eligibility', call(read(capture()), 'viewer')['value'] is None)
    raw(2, 0)
    zero = call(publish(predecessor=suspended['headId'], revision=3))
    check('explicit zero remains final evidence', call(read(capture()), 'viewer')['value'] == '0')
    raw(3, None)
    reject(publish(predecessor=zero['headId'], revision=4), 'meter_finality_reading')
    raw(4, -1)
    reject(publish(predecessor=zero['headId'], revision=5), 'meter_finality_reading')
    reject(publish(predecessor=zero['headId'], revision=3, last='2020-11-01T07:00:00Z', at='2020-11-01T07:00:00Z'), 'meter_finality_reading')
    check('null/negative raw cells and mismatched cell end cannot be verified')
    check('new raw withdrawals do not rewrite separately published finality', call(read(capture()), 'viewer')['value'] == '0')

    def hold_gate():
        holder = subprocess.Popen(psql, env=environment, stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        holder.stdin.write('BEGIN;SELECT pg_advisory_xact_lock(730849102);\n\\echo READY\n')
        holder.stdin.flush()
        while holder.stdout.readline().strip() != 'READY':
            if holder.poll() is not None:
                raise AssertionError(holder.stderr.read())
        return holder

    def release(holder):
        _, error = holder.communicate('COMMIT;\n', timeout=10)
        assert holder.returncode == 0, error

    def waiting(count):
        deadline = time.monotonic() + 5
        while int(query("SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND objid=730849102 AND NOT granted;")) < count:
            assert time.monotonic() < deadline, 'Competing runtime transactions did not reach the publication fence'
            time.sleep(.01)

    # Observe all independent runtime backends blocked at the same fence before release.
    def race(commands):
        scripts = [runtime(sql) for sql in commands]
        holder = hold_gate()
        with ThreadPoolExecutor(max_workers=len(scripts)) as pool:
            tasks = [pool.submit(subprocess.run, psql, input=script, text=True, capture_output=True,
                                 env=environment, timeout=20) for script in scripts]
            try:
                waiting(len(tasks))
            finally:
                release(holder)
            return [t.result() for t in tasks]

    concurrent = publish(str(uuid4()), zero['headId'], 3, 'suspended')
    outcomes = race([concurrent, concurrent])
    check('concurrent identical commands commit one head and same receipt', all(p.returncode == 0 for p in outcomes) and outcomes[0].stdout == outcomes[1].stdout)
    concurrent_head = json.loads(outcomes[0].stdout)['headId']
    outcomes = race([publish(predecessor=concurrent_head, revision=3), publish(predecessor=concurrent_head, revision=3, state='withdrawn')])
    check('concurrent conflicting successors have exactly one winner', sum(p.returncode == 0 for p in outcomes) == 1 and any('meter_finality_predecessor' in p.stderr for p in outcomes))
    check('accepted publication rows and receipts are one to one', query(f"SELECT count(*)=(SELECT count(*) FROM settlement_data.meter_finality_requests WHERE catalog_id='{catalog}') FROM settlement_data.meter_finality_heads WHERE catalog_id='{catalog}';") == 't')
    reject(f"SELECT * FROM settlement_data.meter_finality_heads;", 'permission denied', 'viewer')
    reject(f"UPDATE settlement_data.meter_finality_heads SET state='withdrawn';", 'permission denied')
    check('runtime cannot bypass certified reads or mutate publication tables')
    # A second cell starts with no finality head: both first publishers must contend.
    raw(5, 7, end, '2020-11-01T07:00:00Z')
    outcomes = race([publish(revision=6, first=end, last='2020-11-01T07:00:00Z', at='2020-11-01T07:00:00Z'),
                     publish(revision=6, first=end, last='2020-11-01T07:00:00Z', at='2020-11-01T07:00:00Z')])
    check('concurrent first publications cannot create two initial heads', sum(p.returncode == 0 for p in outcomes) == 1)
    other = json.loads(next(p.stdout for p in outcomes if p.returncode == 0))
    reject(publish(predecessor=other['headId'], revision=3), 'meter_finality_predecessor')
    check('a predecessor from another native cell is refused')

    for mode in ('request', 'grant'):
        for sql in (command, publish(revision=3)):
            if mode == 'grant':
                query("UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '1 second' "
                      "WHERE principal_id=(SELECT principal_id FROM security.identity_bindings WHERE subject='editor');")
            script = runtime(sql, issued=token(ttl=1 if mode == 'request' else 120))
            holder = hold_gate()
            with ThreadPoolExecutor(max_workers=1) as pool:
                task = pool.submit(subprocess.run, psql, input=script, text=True, capture_output=True,
                                   env=environment, timeout=20)
                try:
                    waiting(1)
                    time.sleep(1.1)
                finally:
                    release(holder)
                result = task.result()
            assert result.returncode != 0 and 'meter_finality_denied' in result.stderr, result.stderr
            if mode == 'grant':
                query("UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '1 day' "
                      "WHERE principal_id=(SELECT principal_id FROM security.identity_bindings WHERE subject='editor');")
        check(mode + ' expiry during lock wait refuses new publication and replay')

    try:
        query("BEGIN;INSERT INTO settlement_data.meter_finality_heads SELECT(jsonb_populate_record("
              "NULL::settlement_data.meter_finality_heads,to_jsonb(h)||jsonb_build_object("
              "'id',gen_random_uuid(),'sequence',1,'previous_id',NULL,'ends_at',h.ends_at+interval '1 minute',"
              "'effective_at',h.effective_at+interval '1 minute','publication_order',"
              "(SELECT max(publication_order)+1 FROM settlement_data.meter_finality_heads)))).* "
              "FROM settlement_data.meter_finality_heads h ORDER BY publication_order LIMIT 1;ROLLBACK;")
    except RuntimeError as error:
        assert 'meter_finality_reading' in str(error), str(error)
    else:
        raise AssertionError('Finality head accepted an end different from its exact immutable raw cell')
    check('stored head endpoint equality is enforced independently of the publishing function')

    for table in ('meter_finality_heads', 'meter_finality_requests', 'meter_finality_receipts'):
        try:
            query('BEGIN;UPDATE settlement_data.'+table+' SET catalog_id=catalog_id;ROLLBACK;')
        except RuntimeError as error:
            assert 'immutable' in str(error).lower(), str(error)
        else:
            raise AssertionError('Mutable finality storage: '+table)
    check('privileged updates also hit append-only guards')
    for table, scoped in (('market_catalog_scopes', catalog), ('reference_catalog_scopes', manifest['referenceCatalogId'])):
        for sql in (command, read(new_cut)):
            issued = token()
            script = (f"BEGIN;DELETE FROM security.{table} WHERE catalog_id='{scoped}';" +
                      runtime(sql, issued=issued).removeprefix('BEGIN;').removesuffix('COMMIT;')+'ROLLBACK;')
            try:
                query(script)
            except RuntimeError as error:
                assert 'denied' in str(error) or 'Unavailable market context' in str(error), str(error)
            else:
                raise AssertionError('Revoked scope admitted finality operation')
        check('current '+table+' authority governs replay and historical reads')

    # A caller cannot return from the operation and delay COMMIT past authority expiry.
    current_head = query(f"SELECT id FROM settlement_data.meter_finality_heads WHERE catalog_id='{catalog}' AND starts_at='{start}' ORDER BY sequence DESC LIMIT 1;")
    before_count = query(f"SELECT count(*) FROM settlement_data.meter_finality_heads WHERE catalog_id='{catalog}';")
    issued = token(ttl=1)
    try:
        query(runtime(publish(predecessor=current_head, revision=3)+"SELECT pg_sleep(1.1);", issued=issued))
    except RuntimeError as error:
        assert 'meter_finality_denied' in str(error), str(error)
    else:
        raise AssertionError('Authority expired before commit but a finality head was committed')
    check('deferred commit authority guard rolls back head and receipt', before_count == query(f"SELECT count(*) FROM settlement_data.meter_finality_heads WHERE catalog_id='{catalog}';"))

    # Clone exact definitions under the same stable source codes; a withdrawal must
    # not disappear merely because a reader or publisher changes the revision pin.
    clone = clone_definition(definition)
    reject(publish(revision=3).replace(definition, clone), 'meter_finality_predecessor')
    replacement = call(publish(predecessor=current_head, revision=3, state='withdrawn').replace(definition, clone))
    cloned_cut = capture()
    reject(read(cloned_cut), 'meter_finality_pins', 'viewer')
    check('cross-definition withdrawal stays on one chain and cannot restore an old final',
          call(read(cloned_cut).replace(definition, clone), 'viewer')['headId'] == replacement['headId'])
    outside = clone_definition(definition, period=('2021-01-01','2022-01-01'))
    reject(publish(predecessor=replacement['headId'], revision=3).replace(definition, outside), 'meter_finality_definition')
    check('source version must cover the original native operating date')
    late = call(publish(predecessor=replacement['headId'], revision=3, at='2023-01-01T00:00:00Z').replace(definition, clone))
    late_cut = query(runtime(f"SELECT security.capture_date_context('{catalog}','2023-01-01',NULL);", 'viewer'))
    check('later verification preserves an original source after its effective period ends',
          call(read(late_cut, '2023-01-01T00:00:00Z').replace(definition, clone), 'viewer')['headId'] == late['headId'])
    reject(publish(predecessor=late['headId'], revision=3).replace(definition, clone), 'meter_finality_effective_order')
    check('predecessor effective times cannot move backward')
    check('receipt knowledge is actual PostgreSQL commit time', query(
        f"SELECT bool_and(k.known_from=pg_xact_commit_timestamp(((k.publication_xid::text::numeric % 4294967296)::bigint::text)::xid)) "
        f"FROM settlement_data.meter_finality_receipts k WHERE catalog_id='{catalog}';") == 't')
    for fn in ('settlement_data.meter_source(uuid,uuid,text,text,timestamptz,timestamptz,timestamptz)',
               'security.capture_pre_close_risk_context(uuid,date,timestamptz)',
               'security.capture_pre_meter_finality_context(uuid,date,timestamptz)'):
        check('internal function is not runtime callable: '+fn.split('(')[0],
              query(f"SELECT has_function_privilege('openetrm_runtime','{fn}','EXECUTE');") == 'f')

    query("DELETE FROM security.role_permissions WHERE permission='VerifySettlementMeters';")
    reject(command, 'meter_finality_denied')
    check('authority loss refuses even original-command replay')
    check('verifier revocation does not erase previously certified facts', call(read(new_cut), 'viewer')['value'] == '50')
    return passed
