"""Qualify the bounded close-parts operation in the caller's owned database."""
from concurrent.futures import ThreadPoolExecutor
import json
import subprocess
import time
from uuid import uuid4
from preview_client import PreviewClient


def run_checks(query,manifest,psql,environment,close_id,context):
    catalog=manifest['catalogId']
    plan=json.loads(query(f"BEGIN;SET LOCAL enable_seqscan=off;EXPLAIN(FORMAT JSON) SELECT id,name,at FROM market_data.market_closes WHERE catalog_id='{catalog}' AND definition_revision_id='{manifest['revisionId']}' ORDER BY at DESC,id DESC LIMIT 129;ROLLBACK;"))
    def indexes(node):
        return [node.get('Index Name')]+[value for child in node.get('Plans',[]) for value in indexes(child)]
    assert 'market_close_recent_frames' in indexes(plan[0]['Plan']), 'The bounded recent-close query cannot use its declared index'
    role,principal,grant=str(uuid4()),str(uuid4()),str(uuid4())
    original=query("SELECT pg_get_functiondef('security.read_market_close_parts(uuid,uuid[],uuid)'::regprocedure);")
    start=original.index(' IF NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.id=request.id')
    end=original.index(' RETURN payload;',start)
    without_final_guard=original[:start]+original[end:]
    visibility=query("SELECT pg_get_functiondef('security.market_close_visible(uuid,uuid)'::regprocedure);")
    visibility_start=visibility.index(' IF NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.id=request.id')
    visibility_end=visibility.index(' RETURN true;',visibility_start)
    visibility_without_guard=visibility[:visibility_start]+visibility[visibility_end:]
    query(f"""INSERT INTO security.roles(id,code,name) VALUES('{role}','synthetic_expiring_close_reader','Synthetic expiring close reader');
    INSERT INTO security.role_permissions VALUES('{role}','ReadMarketData');INSERT INTO security.principals VALUES('{principal}','human',true);
    INSERT INTO security.identity_bindings VALUES('https://preview.invalid','synthetic_expiring_close_reader','{principal}','human_interactive');
    INSERT INTO security.workspace_memberships VALUES('{manifest['tenantId']}','{manifest['workspaceId']}','{principal}','synthetic_expiring_close_reader','Synthetic expiring close reader',true,1);
    INSERT INTO security.role_grants VALUES('{grant}','{principal}','{role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 day',false);""")
    token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','synthetic_expiring_close_reader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    query(f"""SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');
    SELECT security.use_market_context('{catalog}','{context['contextId']}');
    DO $$BEGIN PERFORM security.read_market_close_parts('{catalog}',ARRAY['{close_id}'::uuid],'{uuid4()}');
    RAISE EXCEPTION 'A close read mislabeled its bound context';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;ROLLBACK;""")
    def attempt(expect_refusal,direct=False):
        table='market_close_receipts' if direct else 'market_close_members'
        read=(f"IF security.market_close_visible('{catalog}','{close_id}') THEN RAISE EXCEPTION 'Expired close reader returned financial parts';END IF;" if direct else
              f"PERFORM security.read_market_close_parts('{catalog}',ARRAY['{close_id}'::uuid],'{context['contextId']}');RAISE EXCEPTION 'Expired close reader returned financial parts';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;")
        locker=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        try:
            locker.stdin.write(f'BEGIN;LOCK TABLE market_data.{table} IN ACCESS EXCLUSIVE MODE;\n\\echo LOCKED\n');locker.stdin.flush()
            while locker.stdout.readline().strip()!='LOCKED':
                if locker.poll() is not None:raise AssertionError(locker.stderr.read())
            query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '8 seconds' WHERE id='{grant}';")
            token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','synthetic_expiring_close_reader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
            sql=f"""SET SESSION AUTHORIZATION preview_runtime;SET application_name='synthetic_close_parts_expiry';BEGIN;SET LOCAL statement_timeout='15s';
            SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{context['contextId']}');
            DO $$BEGIN {read} END$$;
            SELECT NOT security.has_permission('ReadMarketData');ROLLBACK;"""
            with ThreadPoolExecutor(max_workers=1) as pool:
                request=pool.submit(subprocess.run,psql,input=sql,env=environment,capture_output=True,text=True,timeout=25)
                try:
                    for _ in range(200):
                        if query(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='synthetic_close_parts_expiry' AND NOT l.granted AND l.relation='market_data.{table}'::regclass;")=='1':break
                        if request.done():raise AssertionError(request.result().stderr)
                        time.sleep(.03)
                    else:raise AssertionError('The exact close reader did not reach the controlled content read')
                    for _ in range(200):
                        if query(f"SELECT granted_to<=clock_timestamp() FROM security.role_grants WHERE id='{grant}';")=='t':break
                        time.sleep(.05)
                    else:raise AssertionError('The independent close-reader grant did not expire')
                finally:
                    locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();_,errors=locker.communicate(timeout=10)
                    assert locker.returncode==0,errors
                result=request.result(timeout=20)
            if expect_refusal:assert result.returncode==0 and result.stdout.splitlines()[-1:]==['t'],result.stderr+result.stdout
            else:assert result.returncode!=0 and 'Expired close reader returned financial parts' in result.stderr,result.stderr+result.stdout
        finally:
            if locker.poll() is None:
                locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();locker.communicate(timeout=10)
    try:
        query(without_final_guard);attempt(False)
        query(original);attempt(True)
        query(visibility_without_guard);attempt(False,direct=True)
        query(visibility);attempt(True,direct=True)
    finally:
        query(visibility)
        query(original)
        query(f"DELETE FROM security.request_contexts WHERE actor_id='{principal}';DELETE FROM security.role_grants WHERE id='{grant}';DELETE FROM security.workspace_memberships WHERE principal_id='{principal}';DELETE FROM security.identity_bindings WHERE principal_id='{principal}';DELETE FROM security.principals WHERE id='{principal}';DELETE FROM security.role_permissions WHERE role_id='{role}';DELETE FROM security.roles WHERE id='{role}';")
    print('PASS direct close visibility: missing helper final guard detected; natural expiry while waiting for the exact close receipt refuses visibility',flush=True)
    print('PASS close-parts guard: missing final guard detected, natural expiry after content wait rejected, wrong context refused, recent-header index usable',flush=True)


def check_generated_history(url,query,manifest,run_id,curve,contract_id):
    reader=PreviewClient(url);reader.bootstrap();reader.call('profile',{'profile':'viewer'})
    navigation=reader.call('workspace/navigation')
    generated=next(frame for frame in navigation['frames'] if frame.get('originCloseId')==run_id)
    older=max((frame for frame in navigation['frames'] if frame['id'] in navigation['initialFrameIds'] and frame['at']<generated['at']),key=lambda frame:frame['at'])
    history=reader.call('history',{'snapshotId':older['id'],'curve':curve,'contractId':contract_id})
    point=next((point for point in history['points'] if point['snapshotId']==run_id),None)
    assert point,'History omitted a later generated close while an older initial snapshot remained selected'
    native=json.loads(query(f"""SELECT jsonb_build_object('id',o.id,'value',o.value::text) FROM market_data.market_close_pins p
     JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
     JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id)
     JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.quote_code)=(s.catalog_id,s.revision_id,s.quote_code)
     WHERE(p.catalog_id,p.close_id,p.input_use)=('{manifest['catalogId']}','{run_id}','variable')
     AND c.code='{curve}' AND s.contract_id='{contract_id}';"""))
    assert point['value']==native['value'] and point['observationIds']==[native['id']]
    assert history['mode']=='exact_visible_close_history_with_selected_instant_v1'
    print('PASS generated-close history: later durable date appears without selecting it, with its exact stored native price and lineage',flush=True)
