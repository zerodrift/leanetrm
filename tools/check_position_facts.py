"""Read-only progressive position facts in an owned synthetic preview."""
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
from preview_client import PreviewClient
import subprocess
import time
from uuid import uuid4


def run_checks(client, context, view, expected, query, psql, environment, owned_root):
    root=Path(owned_root)
    assert (root/'.openetrm-preview').is_file() and (root/'data/postmaster.pid').is_file()
    request=dict(context=context,bookIds=view['bookIds'],portfolioIds=view['portfolioIds'],offset=0,limit=1)
    first=client.call('trading/positions',request)
    assert first['total']==len(expected) and len(first['positions'])==1 and first['nextOffset']==1,first
    pinned={**context,'knowledgeMode':'retained','contextId':first['context']['contextId']}
    check_exact_position_curves(client,pinned,query)
    directory=client.call('trading/directory',context,timeout=2)
    assert directory['kind']=='directory' and directory['canBook'] is True and directory['books'],directory
    assert directory['legalEntities'] and directory['counterparties'] and directory['bookingActor'],directory
    assert not {'contracts','blotter','holdings','observations'}.intersection(directory),directory
    retained_directory=client.call('trading/directory',pinned,timeout=2)
    assert retained_directory['context']==first['context'] and retained_directory['asOf']==first['asOf'],retained_directory
    assert retained_directory['canBook'] is False and retained_directory['bookingUnavailableReason'],retained_directory
    historical=PreviewClient(client.url);historical.bootstrap();historical.call('profile',{'profile':'trader'})
    selected=historical.call('market/context',dict(effectiveOn=first['context']['effectiveOn'],knownAt=first['context']['knownAt'],mode='at',selection='latest_correction'))['context']
    past=next(frame for frame in historical.call('workspace/catalog')['frames'] if frame.get('contextId')==selected['contextId'] and frame['at']==first['asOf'])
    history=historical.call('trading/directory',{**pinned,'snapshotId':past['id'],'contextId':selected['contextId']},timeout=2)
    assert history['context']==selected and history['asOf']==first['asOf'] and history['canBook'] is False,history
    keys=lambda rows:{(r['tradeId'],r['bindingId'],int(r['revision'])) for r in rows}
    facts=list(first['positions']);next_offset=first['nextOffset']
    while next_offset is not None:
        page=client.call('trading/positions',{**request,'context':pinned,'offset':next_offset,'limit':7})
        assert page['context']==first['context'] and page['asOf']==first['asOf'] and page['total']==first['total'],page
        assert not keys(facts)&keys(page['positions'])
        facts.extend(page['positions']);next_offset=page['nextOffset']
    assert keys(facts)==keys(expected) and len(facts)==len(expected)
    forbidden={'presentValue','mark','executionPnl','discountFactor','delta','gamma'}
    assert all(not forbidden.intersection(row) for row in facts)
    assert all((row['bookedQuantity'] is None)==(row['bookedUnit'] is None) for row in facts)
    for offset,limit in [(-1,1),(0,0),(0,65)]:
        assert client.call('trading/positions',{**request,'offset':offset,'limit':limit},expected=422)['code']=='position_page'
    print('PASS position facts: bounded complete mixed bindings, exact retained pagination, booked units and no valuation placeholders',flush=True)

    # Block actual pricing reads, not the browser's fetch. Facts must neither
    # touch the retained graph nor wait behind the same UI session's price read.
    label='synthetic_position_facts_'+uuid4().hex
    blocker=subprocess.Popen(psql,stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE,
        text=True,env={**environment,'PGAPPNAME':label})
    def until(predicate,seconds=5):
        deadline=time.monotonic()+seconds
        while time.monotonic()<deadline:
            if predicate():return
            time.sleep(.02)
        raise AssertionError('The owned pricing lock did not reach its required state')
    def release():
        if blocker.poll() is None:
            blocker.stdin.write('ROLLBACK;\n\\q\n');blocker.stdin.flush();blocker.stdin.close()
            try:blocker.wait(timeout=5)
            except subprocess.TimeoutExpired:blocker.kill();blocker.wait(timeout=5)
        assert blocker.returncode==0,(blocker.stderr.read() if blocker.stderr else '')
    try:
        blocker.stdin.write('BEGIN;\nLOCK TABLE trading.side_schedule_rows IN ACCESS EXCLUSIVE MODE;\n');blocker.stdin.flush()
        until(lambda:query(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE a.application_name='{label}' AND l.relation='trading.side_schedule_rows'::regclass AND l.mode='AccessExclusiveLock' AND l.granted;")=='1')
        with ThreadPoolExecutor(max_workers=1) as pool:
            priced=pool.submit(client.call,'trading/workspace',dict(context=context,view=view,bookingCatalog='directory',summaries=[],progressive=True))
            try:
                until(lambda:query("SELECT count(*) FROM pg_locks WHERE relation='trading.side_schedule_rows'::regclass AND NOT granted;")!='0')
                started=time.monotonic()
                available=client.call('trading/positions',{**request,'context':pinned,'offset':1},timeout=2)
                assert time.monotonic()-started<2 and available['positions'] and available['total']==len(expected)
                assert available['context']==first['context']
                started=time.monotonic()
                available_directory=client.call('trading/directory',pinned,timeout=2)
                assert time.monotonic()-started<2 and available_directory==retained_directory,available_directory
            finally:release()
            result=priced.result(timeout=20)
            assert keys(result['blotter']['holdings'])==keys(expected) and result['catalog']['canBook'] is True
    finally:
        if blocker.poll() is None:release()
    print('PASS position facts: pages remain available while actual pricing is blocked; complete working values retain booking rights',flush=True)
    _directory_authority(client,pinned,query,root)
    _publication_authority(client,context,view,query,psql,environment,root)


def _directory_authority(client,context,query,root):
    """Retained metadata never confers current source or trading authority."""
    manifest=json.loads((root/'manifest.json').read_text())
    catalog,tenant,workspace=(manifest[k] for k in ('referenceCatalogId','tenantId','workspaceId'))
    scope=f"catalog_id='{catalog}' AND tenant_id='{tenant}' AND workspace_id='{workspace}'"
    access=query(f'SELECT access_mode FROM security.reference_catalog_scopes WHERE {scope};')
    assert access in ('owner','read_only')
    try:
        query(f'DELETE FROM security.reference_catalog_scopes WHERE {scope};')
        refused=client.call('trading/directory',context,expected=422)
        assert refused['code']=='ambiguous_reference' and set(refused)=={'code','message'},refused
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{catalog}','{tenant}','{workspace}','{access}');")
    role=query("SELECT id FROM security.roles WHERE code='trader';")
    for permission in ('ReadTrades','ReadMarketData'):
        assert query(f"SELECT count(*) FROM security.role_permissions WHERE role_id='{role}' AND permission='{permission}';")=='1'
        try:
            query(f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission='{permission}';")
            # Market authority is also needed to resolve the retained context.
            expected,code=(403,'access_denied') if permission=='ReadTrades' else (422,'temporal_context')
            refused=client.call('trading/directory',context,expected=expected)
            assert refused['code']==code and set(refused)=={'code','message'},refused
        finally:
            query(f"INSERT INTO security.role_permissions VALUES('{role}','{permission}');")
    print('PASS trading directory: metadata loads within two seconds while pricing is blocked, preserves retained context/read-only rights, and refuses revoked source/trading authority',flush=True)


def _publication_authority(client,context,view,query,psql,environment,root):
    """Expire authority after valuation commits but before its final gate opens."""
    manifest=json.loads((root/'manifest.json').read_text())
    tenant,workspace=(manifest[k] for k in ('tenantId','workspaceId'))
    role=query("SELECT id FROM security.roles WHERE code='trader';")
    actor=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader' AND login_kind='human_interactive';")
    assert role and actor

    def until(predicate,message,seconds=5):
        deadline=time.monotonic()+seconds
        while time.monotonic()<deadline:
            if predicate():return
            time.sleep(.02)
        raise AssertionError(message)

    def lock(table):
        label='synthetic_publication_'+uuid4().hex
        process=subprocess.Popen(psql,stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE,
            text=True,env={**environment,'PGAPPNAME':label})
        process.stdin.write(f'BEGIN;\nLOCK TABLE {table} IN ACCESS EXCLUSIVE MODE;\n');process.stdin.flush()
        try:
            until(lambda:query(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE a.application_name='{label}' AND l.relation='{table}'::regclass AND l.mode='AccessExclusiveLock' AND l.granted;")=='1',
                f'The owned {table} lock was not acquired')
        except BaseException:
            process.kill();process.wait(timeout=5);raise
        return process

    def release(process):
        if process is None:return
        if process.poll() is None:
            process.stdin.write('ROLLBACK;\n\\q\n');process.stdin.flush();process.stdin.close()
            try:process.wait(timeout=5)
            except subprocess.TimeoutExpired:process.kill();process.wait(timeout=5)
        assert process.returncode==0,(process.stderr.read() if process.stderr else '')

    for permission in ('ReadTrades','ReadMarketData'):
        assert query(f"SELECT count(*) FROM security.role_permissions WHERE role_id='{role}' AND permission='{permission}';")=='1'
        temporary_role,grant=str(uuid4()),str(uuid4())
        pricing_lock=gate_lock=None;configured=False
        try:
            # Leave every other permission unchanged. Only this one permission
            # is supplied by a genuinely expiring role grant on the DB clock.
            query(f"BEGIN;INSERT INTO security.roles(id,code,name) VALUES('{temporary_role}','synthetic_publication_{uuid4().hex}','Synthetic publication expiry');"
                f"INSERT INTO security.role_permissions VALUES('{temporary_role}','{permission}');"
                f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission='{permission}';"
                f"INSERT INTO security.role_grants(id,principal_id,role_id,tenant_id,workspace_id,granted_from,granted_to,revoked) VALUES('{grant}','{actor}','{temporary_role}','{tenant}','{workspace}',"
                "clock_timestamp()-interval '1 second',clock_timestamp()+interval '4 seconds',false);COMMIT;")
            configured=True
            pricing_lock=lock('trading.side_schedule_rows')
            gate_lock=lock('presentation.table_views')
            with ThreadPoolExecutor(max_workers=2) as pool:
                priced=pool.submit(client.call,'trading/workspace',dict(context=context,view=view,bookingCatalog='directory',summaries=[],progressive=True),expected=403)
                try:
                    pricing_pid=[]
                    def pricing_waits():
                        value=query("SELECT a.pid FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE a.usename='preview_runtime' AND l.relation='trading.side_schedule_rows'::regclass AND NOT l.granted;")
                        if value and '\n' not in value:pricing_pid.append(int(value));return True
                        return False
                    until(pricing_waits,'The progressive read did not reach actual pricing')
                    # The preferences read owns the same session gate, blocked
                    # on its real table, without changing profile or selection.
                    gate=pool.submit(client.call,'table-views/blotter',expected=403 if permission=='ReadTrades' else 200)
                    until(lambda:query("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE a.usename='preview_runtime' AND l.relation='presentation.table_views'::regclass AND NOT l.granted;")=='1',
                        'The same-session preferences read did not take the gate')
                    release(pricing_lock);pricing_lock=None
                    until(lambda:query(f"SELECT count(*) FROM pg_stat_activity WHERE pid={pricing_pid[0]} AND xact_start IS NOT NULL;")=='0',
                        'The pricing transaction did not finish before the gate wait')
                    assert not priced.done(), f'The valuation ended before the intended gate gap: {priced.exception()!r}'
                    assert query(f"SELECT clock_timestamp()<granted_to FROM security.role_grants WHERE id='{grant}';")=='t', \
                        'The grant must still be live when valuation commits'
                    until(lambda:query(f"SELECT clock_timestamp()>=granted_to FROM security.role_grants WHERE id='{grant}';")=='t',
                        'The explicit publication-test grant did not expire')
                    release(gate_lock);gate_lock=None
                    gate.result(timeout=5)
                    refused=priced.result(timeout=10)
                    assert refused['code']=='access_denied',refused
                finally:
                    release(pricing_lock);pricing_lock=None
                    release(gate_lock);gate_lock=None
        finally:
            release(pricing_lock);release(gate_lock)
            if configured:
                query(f"BEGIN;DELETE FROM security.role_grants WHERE id='{grant}';"
                    f"DELETE FROM security.role_permissions WHERE role_id='{temporary_role}';"
                    f"DELETE FROM security.roles WHERE id='{temporary_role}';"
                    f"INSERT INTO security.role_permissions VALUES('{role}','{permission}');COMMIT;")
    print('PASS progressive publication: completed valuations refuse after ReadTrades or source-market authority expires during the final session-gate wait',flush=True)


def check_exact_position_curves(client, context, query):
    request=dict(context=context,bookIds=[],portfolioIds=[],offset=0,limit=64)
    expected=client.call('trading/positions',request)
    unused=query("""SELECT c.code FROM market_data.curves c
      WHERE NOT EXISTS(SELECT 1 FROM trading.swap_floating_sides s WHERE
       (s.catalog_id,s.definition_revision_id,s.curve_code)=(c.catalog_id,c.revision_id,c.code))
      AND NOT EXISTS(SELECT 1 FROM trading.swap_side_periods s WHERE
       (s.catalog_id,s.definition_revision_id,s.curve_code)=(c.catalog_id,c.revision_id,c.code))
      AND NOT EXISTS(SELECT 1 FROM trading.futures_revision_markets m WHERE
       (m.catalog_id,m.definition_revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code))
      ORDER BY c.code LIMIT 1;""")
    assert unused, 'The synthetic position fixture needs an unrelated curve.'
    literal="'"+unused.replace("'","''")+"'"
    query("""CREATE FUNCTION public.synthetic_position_curve_guard(code text) RETURNS boolean
      LANGUAGE plpgsql VOLATILE AS $$BEGIN
      IF code="""+literal+""" THEN RAISE EXCEPTION 'Unrelated position curve inspected' USING ERRCODE='42501';END IF;
      RETURN true;END$$;
      CREATE POLICY synthetic_position_curve_guard ON market_data.curves AS RESTRICTIVE
      FOR SELECT TO openetrm_runtime USING(public.synthetic_position_curve_guard(code));""")
    try:
        assert client.call('trading/positions',request)==expected
        print('PASS position metadata reads only the exact curves its retained sides name',flush=True)
    finally:
        query('DROP POLICY synthetic_position_curve_guard ON market_data.curves;DROP FUNCTION public.synthetic_position_curve_guard(text);')
    selected=query("""SELECT json_build_array(s.catalog_id,s.definition_revision_id,s.curve_code)::text
      FROM trading.swap_floating_sides s
      JOIN trading.swap_instruments i ON(i.catalog_id,i.floating_side_id)=(s.catalog_id,s.id)
      JOIN trading.swap_trade_revisions r ON(r.catalog_id,r.instrument_id)=(i.catalog_id,i.instrument_id)
      ORDER BY r.trade_id LIMIT 1;""")
    catalog,revision,code=json.loads(selected)
    from uuid import UUID
    predicate="(catalog_id,revision_id,code)=('"+str(UUID(catalog))+"'::uuid,'"+str(UUID(revision))+"'::uuid,'"+code.replace("'","''")+"')"
    query('CREATE POLICY synthetic_position_curve_missing ON market_data.curves AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(NOT('+predicate+'));')
    try:
        assert client.call('trading/positions',request,expected=422)['code']=='position_facts'
        print('PASS position metadata refuses a hidden exact curve without substituting another',flush=True)
    finally:
        query('DROP POLICY synthetic_position_curve_missing ON market_data.curves;')
    assert client.call('trading/positions',request)==expected
