"""Atomic curve marks over exact synthetic sources and certified read inputs."""
from concurrent.futures import ThreadPoolExecutor
import json
import time
from uuid import uuid4
from check_booked_side_programs import runtime_query


def literal(value):
    return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"


def run_checks(query, manifest):
    cat = manifest['catalogId']
    def run(statement, **kwargs):
        return runtime_query(query, manifest, statement, **{'profile':'editor', **kwargs})
    def read(expression, **kwargs):
        return json.loads(run(f'SELECT {expression};', **kwargs))
    def refused(expression, message, **kwargs):
        try:
            run(f'SELECT {expression};', **kwargs)
        except RuntimeError as error:
            assert message in str(error), error
        else:
            raise AssertionError(f'Expected {message}')
    owner = run(f"SELECT security.curve_mark_editor_owner('{cat}');")
    source = {k:manifest[v] for k,v in [('definitionRevisionId','revisionId'),('referenceCatalogId','referenceCatalogId'),
                                         ('referenceRevisionId','referenceRevisionId')]}
    day = '2020-12-18'
    at = day+'T16:00:00Z'
    def capture():
        return run(f"SELECT security.resolve_operation_context('{cat}','{day}',NULL);")
    original = capture()
    def scope(context, effective=at):
        return f"'{cat}','{context}','{effective}',{literal(source)}"
    setup = read(f"security.curve_mark_editor_setup({scope(original)},'',0,50)")
    assert setup['canPublish'] and 2 <= len(setup['curves']) <= 50, setup
    curves = sorted(x['code'] for x in setup['curves'] if x['currency']==setup['curves'][0]['currency'])
    assert len(curves) >= 3, curves
    a,b,c = curves[:3]
    def key(first,second=None,month='2021-01'):
        return dict(firstCurve=first,secondCurve=second,month=month)
    def change(k,value,revision=0):
        return dict(**k,value=value,expectedRevision=revision)
    def batch(kind,changes):
        return dict(policy='curve_mark_batch_v1',kind=kind,source=source,ownerId=owner,operationId=str(uuid4()),
                    snapshotId=None,reason='Synthetic editor publication',changes=changes)
    def record(body,context=original,effective=at):
        return f"security.record_curve_mark_batch('{cat}','{context}','{effective}',{literal(body)})"
    def counts():
        return json.loads(query('SELECT jsonb_build_array((SELECT count(*) FROM trading.curve_mark_requests),'
                         '(SELECT count(*) FROM trading.curve_mark_facts),(SELECT count(*) FROM trading.publications));'))
    def delta(before):
        return [y-x for x,y in zip(before,counts())]
    def status(body,actor=owner):
        return f"security.curve_mark_editor_status('{cat}','{actor}','{body['operationId']}','{body['kind']}')"
    def states(context,kind,keys,working=True,**kwargs):
        return read(f"security.curve_mark_editor_cells({scope(context)},'{kind}',{literal(keys)},{str(working).lower()})",**kwargs)
    def history(context,k,offset=0,limit=50):
        return f"security.curve_mark_editor_history({scope(context)},'volatility',{literal(k)},{offset},{limit})"

    ks = [key(a), key(b)]
    vol = batch('volatility', [change(ks[0],'0.5'),change(ks[1],'0')])
    assert not read(status(vol))['found']
    refused(f"security.curve_mark_editor_setup({scope(original)},'',0,51)",'curve_mark_bounds')
    before=counts()
    first = read(record(vol))
    assert delta(before)==[1,2,1] and [x['value'] for x in first['cells']]==['0.5','0']
    assert read(record(vol))==first and delta(before)==[1,2,1]
    recovered=read(status(vol))
    assert recovered['found'] and recovered['outcome']==first and recovered['sourceContextId']==original
    assert set(recovered['sourceInputs'])=={'businessOn','knownAt','systemId','timeline'}
    assert recovered['sourceInputs']['businessOn']==day and recovered['command']==vol
    assert all(s['selected'] is None and s['writeHead'] is None for s in states(original,'volatility',ks))
    after_first=capture()
    revised=batch('volatility',[change(ks[0],'0.6',1)])
    assert read(record(revised))['cells'][0]['revision']==2
    acknowledgement=batch('volatility',[change(ks[0],'0.5')])
    before=counts()
    ack=read(record(acknowledgement))['cells'][0]
    assert ack['markId']==first['cells'][0]['markId'] and ack['changed'] is False and delta(before)==[1,0,0]
    assert ack['revision']==1 and ack['value']=='0.5' and ack['headAtPublication']['revision']==2 and ack['headAtPublication']['value']=='0.6'
    pairs=batch('correlation',[change(key(a,b),'-0.5'),change(key(a,c),'1')])
    before=counts()
    assert len(read(record(pairs))['cells'])==2 and delta(before)==[1,2,1]
    assert read(record(pairs))==read(status(pairs))['outcome']
    before=counts()
    for value,revision,code in [('0.3',0,'curve_mark_stale'),('11',1,'curve_mark_value')]:
        invalid=batch('volatility',[change(ks[0],'0.9',2),change(ks[1],value,revision)])
        refused(record(invalid),code)
        assert delta(before)==[0,0,0] and not read(status(invalid))['found']
    refused(record({**vol,'reason':'Conflicting retry'}),'curve_mark_request_conflict')
    refused(record({**vol,'source':{**source,'referenceRevisionId':str(uuid4())}}),'curve_mark_source')
    refused(record(vol,context=str(uuid4())),'curve_mark_context')
    refused(status(vol),'curve_mark_owner_changed',profile='trader')
    refused(status(vol),'curve_mark_denied',prefix="DELETE FROM security.role_permissions WHERE permission='ReadMarketData';",commit=False)
    refused(record(vol),'curve_mark_denied',prefix="DELETE FROM security.role_permissions WHERE permission='ImportObservations';",commit=False)
    later=batch('volatility',[change(ks[0],'0.8',2)])
    read(record(later,effective=day+'T16:01:00Z'))
    current=capture()
    working=states(current,'volatility',ks)[0]
    assert (working['selected']['value'],working['writeHead']['value'],working['expectedRevision'],working['editBlock']['code'])==('0.6','0.8',None,'curve_mark_time')
    pinned=states(after_first,'volatility',ks,False)[0]
    assert pinned['selected']['value']=='0.5' and pinned['writeHead'] is None and pinned['expectedRevision'] is None
    assert states(after_first,'volatility',ks)[0]['writeHead']['value']=='0.5'
    visible=read(history(current,ks[0],0,2),prefix="DELETE FROM security.role_permissions WHERE permission='ReadTrades';",commit=False)
    assert visible['hasMore'] and [x['effectiveEligible'] for x in visible['revisions']]==[False,True]
    assert [x['mark']['value'] for x in visible['revisions']]==['0.8','0.6']
    assert visible['revisions'][0]['previousId']==visible['revisions'][1]['mark']['id']
    assert read(history(current,ks[0],2,2))['revisions'][0]['mark']['id']==ack['markId']
    refused(history(current,ks[0],0,51),'curve_mark_bounds')
    assert all(x['writeHead'] is None for x in states(current,'volatility',ks,False))
    for body,code in [
        (batch('volatility',[change(ks[0],'0.7')]*2),'curve_mark_duplicate'),
        (batch('volatility',[change(key(a,month='2200-01'),'1')]),'curve_mark_input'),
        (batch('volatility',[change(key('absent_curve'),'1')]),'curve_mark_source'),
        (batch('volatility',[change(ks[0],'0.1234567890123')]),'curve_mark_value'),
        ({**vol,'sourceInputs':recovered['sourceInputs']},'curve_mark_input'),
        (batch('correlation',[change(key(a,a),'1')]),'curve_mark_pair')]:
        refused(record(body),code)
    no_trades = "DELETE FROM security.role_permissions WHERE permission='ReadTrades';"
    preview=batch('volatility',[change(key(b,month='2021-02'),'0')])
    assert read(record(preview),prefix=no_trades,commit=False)['cells'][0]['changed']
    assert not read(status(preview))['found']
    before=counts()
    try:
        run(f"SELECT security.use_market_context('{cat}','{current}');SELECT {record(preview,context=current)};")
    except RuntimeError as error:
        assert 'curve_mark_denied' in str(error),error
    else:
        raise AssertionError('Writing from bound historical input was accepted')
    assert delta(before)==[0,0,0]
    legacy=dict(operationId=str(uuid4()),convention='curve_marks_log_return_held_v1',definitionRevisionId=source['definitionRevisionId'],
                curve=b,reason='Exact legacy command',marks=[dict(month='2021-03',expectedRevision=0,volatility='0.50')])
    old=f"security.record_volatility_marks('{cat}',{literal(legacy)},'{at}')"
    receipt=read(old)
    assert read(old)==receipt and set(receipt)=={'operationId','marks'} and set(receipt['marks'][0])=={'markId','month','revision','changed'}
    assert read(f"security.curve_mark_editor_history({scope(capture())},'volatility',{literal(key(b,month='2021-03'))},0,50)")['revisions'][0]['reason']==legacy['reason']
    refused(status({**vol,'operationId':legacy['operationId']}),'curve_mark_request_conflict')
    refused(record({**vol,'operationId':legacy['operationId']}),'curve_mark_request_conflict')
    strip = [key(a,month=f'{2000+n//12:04}-{1+n%12:02}') for n in range(240)]
    assert len(states(current,'volatility',strip))==240
    refused(f"security.curve_mark_editor_setup({scope(current)},'',0,51)",'curve_mark_bounds')
    refused(f"security.require_curve_mark_editor_commit('{cat}','{current}','{at}',{literal(source)},true,clock_timestamp()-interval '1 second')",'curve_mark_denied')
    # Exercise each visibility predicate independently with synthetic private
    # inputs. The public read operations always derive both bounds together.
    publication = query(f"SELECT publication_order FROM trading.volatility_marks WHERE id='{ack['markId']}';")
    known_at = pinned['selected']['knownAt']
    query(f"""DO $probe$ DECLARE cut security.operation_read_coordinates;n integer;BEGIN
      cut.catalog_id='{cat}';cut.trading_prefix={publication};cut.known_at='{known_at}';
      SELECT count(*) INTO n FROM trading.curve_mark_editor_facts(cut,'{source['definitionRevisionId']}','volatility',{literal(ks[0])});
      IF n<>1 THEN RAISE EXCEPTION 'Exact publication boundary';END IF;
      cut.trading_prefix=cut.trading_prefix-1;
      IF EXISTS(SELECT FROM trading.curve_mark_editor_facts(cut,'{source['definitionRevisionId']}','volatility',{literal(ks[0])}))
      THEN RAISE EXCEPTION 'Publication beyond the prefix';END IF;
      cut.trading_prefix=cut.trading_prefix+1;cut.known_at=cut.known_at-interval '1 microsecond';
      IF EXISTS(SELECT FROM trading.curve_mark_editor_facts(cut,'{source['definitionRevisionId']}','volatility',{literal(ks[0])}))
      THEN RAISE EXCEPTION 'Publication beyond knowledge time';END IF;
      END$probe$;""")
    for month,values in [('2022-01',('0.1','0.2')),('2022-02',('0.3','0.3'))]:
        commands = [batch('volatility',[change(key(a,month=month),value)]) for value in values]
        before = counts()
        results = []
        with ThreadPoolExecutor(max_workers=2) as pool:
            pending = [pool.submit(read,record(command)) for command in commands]
            for future in pending:
                try:
                    results.append(future.result(timeout=15)['cells'][0])
                except RuntimeError as error:
                    assert 'curve_mark_stale' in str(error),error
        assert sum(cell['changed'] for cell in results)==1 and len({cell['markId'] for cell in results})==1
        assert delta(before)==([2,1,1] if values[0]==values[1] else [1,1,1])
    assert read(f"security.curve_mark_editor_setup({scope(current)},'',0,1)",profile='trader')['canPublish'] is False
    assert not read(status(vol,actor=run(f"SELECT security.curve_mark_editor_owner('{cat}');",profile='trader')),profile='trader')['found']
    check_workspace_reassignment(query,manifest,owner,vol)
    print('PASS atomic curve mark batches, certified cutoffs, effective heads, owned recovery and market-only history',flush=True)


def check_workspace_reassignment(query, manifest, original_owner, command):
    """A human's new workspace may read the catalog without owning the old receipt."""
    tenant,workspace,catalog,reference = (manifest[k] for k in ('tenantId','workspaceId','catalogId','referenceCatalogId'))
    other = str(uuid4())
    query.raw(f"""BEGIN;
      INSERT INTO security.workspaces VALUES('{tenant}','{other}','synthetic_curve_editor');
      INSERT INTO security.workspace_memberships
        SELECT tenant_id,'{other}',principal_id,login_handle,display_name,enabled,version FROM security.workspace_memberships
        WHERE(tenant_id,workspace_id,login_handle)=('{tenant}','{workspace}','editor');
      INSERT INTO security.role_grants
        SELECT gen_random_uuid(),g.principal_id,g.role_id,g.tenant_id,'{other}',g.granted_from,g.granted_to,g.revoked
        FROM security.role_grants g JOIN security.workspace_memberships m USING(tenant_id,workspace_id,principal_id)
        WHERE(g.tenant_id,g.workspace_id,m.login_handle)=('{tenant}','{workspace}','editor');
      INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{tenant}','{other}','read_only');
      UPDATE security.market_catalog_scopes SET workspace_id='{other}' WHERE catalog_id='{catalog}';
      -- Stop before reading private inputs if admission ever reaches reconstruction.
      CREATE OR REPLACE FUNCTION security.register_owner_coordinates(catalog uuid,identifier uuid,day date,cutoff timestamptz,system_id text,timeline integer)
        RETURNS security.operation_read_coordinates LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $sentinel$
        BEGIN RAISE EXCEPTION 'Unexpected receipt coordinate registration';END$sentinel$;
      SET LOCAL SESSION AUTHORIZATION preview_authenticator;
      SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{tenant}','{other}',NULL,'preview_runtime',60) AS token \\gset
      SET LOCAL SESSION AUTHORIZATION preview_runtime;
      SELECT security.use_context(:'token');
      DO $check$ DECLARE new_owner text=security.curve_mark_editor_owner('{catalog}');BEGIN
        IF new_owner='{original_owner}' OR security.reference_access('{reference}','ReadMarketData') IS NOT TRUE
        THEN RAISE EXCEPTION 'Expected independently authorized editor workspace';END IF;
        BEGIN
          PERFORM security.curve_mark_editor_status('{catalog}',new_owner,'{command['operationId']}','{command['kind']}');
          RAISE EXCEPTION 'Expected receipt ownership refusal';
        EXCEPTION WHEN insufficient_privilege THEN
          IF SQLERRM<>'curve_mark_owner_changed' THEN RAISE;END IF;
        END;
      END$check$;
      ROLLBACK;""")
    print('PASS reassigned catalog retains the original workspace receipt owner before reconstructing inputs',flush=True)


def check_upgrade_overlap(query, migration):
    """Exercise real predecessor admission while the complete migration is waiting."""
    from preview_fixture import fixture
    from seed_fixture_market import load_fixture
    name = 'synthetic_curve_mark_upgrade'
    def local(sql):
        # Roles are cluster-wide. Keep this populated predecessor clone's two
        # synthetic logins separate from the preview client's later setup.
        sql = sql.replace('preview_authenticator','curve_upgrade_authenticator').replace('preview_runtime','curve_upgrade_runtime')
        return query('\\connect '+name+'\n'+sql)
    query(f'CREATE DATABASE {name} TEMPLATE postgres;')
    try:
        reference,market,security,manifest = fixture(migration.parents[2])
        load_fixture(local,reference,market,security,manifest)
        catalog,definition = manifest['catalogId'],manifest['revisionId']
        body = dict(operationId=str(uuid4()),definitionRevisionId=definition,convention='curve_marks_log_return_held_v1',
                    reason='Synthetic in-flight legacy mark',curve='local_gas',marks=[dict(month='2021-01',expectedRevision=0,volatility='0.5')])
        writer = f"""SET LOCAL application_name='curve_upgrade_writer';
          DO $wait$ DECLARE migrator integer;until_at timestamptz=clock_timestamp()+interval '10 seconds';BEGIN
            LOOP
              PERFORM pg_stat_clear_snapshot();
              SELECT pid INTO migrator FROM pg_stat_activity WHERE datname=current_database() AND application_name='curve_upgrade_migration'
                AND pg_backend_pid()=ANY(pg_blocking_pids(pid));
              EXIT WHEN migrator IS NOT NULL;
              IF clock_timestamp()>=until_at THEN RAISE EXCEPTION 'Migration did not wait for the admitted writer';END IF;
              PERFORM pg_sleep(0.02);
            END LOOP;
            IF EXISTS(SELECT FROM pg_locks WHERE pid=migrator AND locktype='advisory' AND granted)
            THEN RAISE EXCEPTION 'Migration already holds the publication lock while waiting for writer admission';END IF;
          END$wait$;
          SELECT security.record_volatility_marks('{catalog}',{literal(body)},'2020-12-18T16:00:00Z');"""
        with ThreadPoolExecutor(max_workers=1) as pool:
            pending = pool.submit(runtime_query,local,manifest,writer,'editor')
            until = time.monotonic()+10
            while local("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND application_name='curve_upgrade_writer');")!='t':
                if pending.done():pending.result()
                if time.monotonic()>=until:raise AssertionError('The legacy writer did not reach its admission barrier')
                time.sleep(.02)
            local("SET application_name='curve_upgrade_migration';"+migration.read_text())
            receipt = json.loads(pending.result(timeout=15))
        assert receipt['marks'][0]['revision']==1 and receipt['marks'][0]['changed']
        saved = local(f"SELECT outcome FROM trading.curve_mark_requests WHERE operation_id='{body['operationId']}' AND editor_metadata IS NULL;")
        assert json.loads(saved)==receipt
        print('PASS admitted legacy writer commits first and the complete migration preserves its receipt',flush=True)
    finally:
        query(f'DROP DATABASE {name};')
        query('DROP ROLE IF EXISTS curve_upgrade_authenticator,curve_upgrade_runtime;')
