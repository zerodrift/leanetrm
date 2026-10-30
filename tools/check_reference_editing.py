"""Immutable metadata edits and explicit display activation in an owned fixture."""
import json
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4


def run_checks(query, manifest):
    catalog, source, tenant, workspace = [manifest[k] for k in
        ['referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId']]
    from check_unit_symbol_integrity import run_checks as check_unit_symbols
    check_unit_symbols(query,manifest)
    def call(profile, sql, bound=None, selected_workspace=workspace):
        token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{selected_workspace}',NULL,'preview_runtime',60);")
        bind = f"SELECT security.use_market_context('{manifest['catalogId']}','{bound}');" if bound else ''
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{bind}{sql};COMMIT;")
    def rejected(work, code):
        try:
            work()
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected '+code)
    # The same declared field contract drives authoring and the editor. This
    # independent expected shape excludes financial keys and preserves UI order.
    descriptors=json.loads(call('viewer',"SELECT security.reference_display_editor_fields('locations')::text"))
    assert descriptors==[{'key':'name','label':'Name','kind':'text','nullable':False,'maxLength':300},
                        {'key':'latitude','label':'Latitude','kind':'decimal','nullable':True,'maxLength':48},
                        {'key':'longitude','label':'Longitude','kind':'decimal','nullable':True,'maxLength':48}]
    assert call('viewer',"SELECT security.reference_display_editor_fields('unit_conversions')::text")=='[]'
    editable=json.loads(query("SELECT jsonb_object_agg(t,security.reference_display_editor_fields(t))::text FROM unnest(security.reference_display_tables())t WHERE jsonb_array_length(security.reference_display_editor_fields(t))>0;"))
    text=lambda key,label,maximum=300:[{'key':key,'label':label,'kind':'text','nullable':False,'maxLength':maximum}]
    assert editable=={'currencies':text('name','Name'),'commodities':text('name','Name'),
      'locations':descriptors,'location_types':text('description','Description'),'units':text('symbol','Display symbol',80),
      'exchanges':text('name','Name'),'clearing_houses':text('name','Name'),
      'futures_products':text('name','Name'),'futures_series':text('series_label','Contract label')},'Unexpected editable reference policy'
    assert query("SELECT security.reference_display_fields('locations')=ARRAY['name','latitude','longitude'];")=='t'
    # Every cloned reference FK must point inside the same qualified closure;
    # this catches a newly required dependency missing from the ordered inventory.
    assert query("""SELECT count(*) FROM pg_constraint f JOIN pg_class child ON child.oid=f.conrelid
      JOIN pg_namespace cn ON cn.oid=child.relnamespace JOIN pg_class parent ON parent.oid=f.confrelid
      JOIN pg_namespace pn ON pn.oid=parent.relnamespace WHERE f.contype='f' AND cn.nspname='ref_data'
      AND child.relname=ANY(security.reference_display_tables()) AND pn.nspname='ref_data'
      AND parent.relname<>'revisions' AND NOT parent.relname=ANY(security.reference_display_tables());""")=='0','Reference clone inventory lacks a required FK dependency'
    original = query(f"SELECT name FROM ref_data.currencies WHERE catalog_id='{catalog}' AND revision_id='{source}' AND code='usd';")
    context = call('viewer', f"SELECT security.capture_date_context('{manifest['catalogId']}','2020-12-17',NULL)")
    operation = str(uuid4())
    def save(profile='editor', values=None, base=source, operation_id=operation):
        payload = json.dumps(values or {'name': 'Synthetic revised dollar'})
        return json.loads(call(profile, f"SELECT security.save_reference_display_edit('{catalog}','{source}','{base}','{operation_id}','currencies','usd','{payload}'::jsonb)::text"))
    stale = str(uuid4())
    save(operation_id=stale, values={'name':'Synthetic older draft'})
    saved = save()
    from check_reference_integrity import run_checks as check_integrity
    check_integrity(query,manifest,operation)
    assert call('viewer', 'SELECT count(*) FROM ref_data.display_edits', bound=context) == '0', 'Historical raw reads leaked later draft metadata'
    assert call('viewer', f"SELECT security.reference_display_state('{catalog}','{source}')::text", bound=context) == 'null'
    assert saved['revisionId'] == operation and saved['state'] == 'draft'
    assert save() == saved, 'An exact retry must retain one immutable reference version'
    rejected(lambda: save(values={'name': 'Different retry'}), 'reference_conflict')
    rejected(lambda: save('viewer', operation_id=str(uuid4())), 'reference_denied')
    rejected(lambda: save(values={'display_minor_units': '9'}, operation_id=str(uuid4())), 'reference_edit')
    assert query(f"SELECT name FROM ref_data.currencies WHERE catalog_id='{catalog}' AND revision_id='{source}' AND code='usd';") == original
    assert query(f"SELECT name FROM ref_data.currencies WHERE catalog_id='{catalog}' AND revision_id='{operation}' AND code='usd';") == 'Synthetic revised dollar'
    assert json.loads(call('viewer', f"SELECT security.reference_display_state('{catalog}','{source}')::text"))['version'] == 0
    apply_id = str(uuid4())
    sql = f"SELECT security.apply_reference_display('{catalog}','{source}','{operation}','{apply_id}',0)::text"
    rejected(lambda: call('editor', sql), 'reference_denied')
    activated = json.loads(call('approver', sql))
    assert activated['version'] == 1 and activated['displayRevisionId'] == operation
    assert json.loads(call('approver', sql)) == activated
    display = json.loads(call('viewer', f"SELECT security.reference_display_state('{catalog}','{source}')::text"))
    assert display['references']['currencies'] == [{'code': 'usd', 'name': 'Synthetic revised dollar'}]
    assert query(f"SELECT name FROM ref_data.currencies WHERE catalog_id='{catalog}' AND revision_id='{source}' AND code='usd';") == original
    # Sealed financial rows cannot be changed, including through raw runtime SQL.
    rejected(lambda: call('editor', f"UPDATE ref_data.currencies SET display_minor_units=9 WHERE catalog_id='{catalog}' AND revision_id='{source}' AND code='usd'"), 'permission denied')
    rejected(lambda: query(f"UPDATE ref_data.currencies SET display_minor_units=9 WHERE catalog_id='{catalog}' AND revision_id='{source}' AND code='usd'"), 'immutable')
    rejected(lambda: call('approver', sql, bound=context), 'reference_denied')
    rejected(lambda: call('editor', f"SELECT security.save_reference_display_edit('{catalog}','{source}','{source}','{uuid4()}','currencies','usd','{{\"name\":\"Historical mutation\"}}')", bound=context), 'reference_denied')

    # Re-authorize exact retries, rather than treating a receipt as authority.
    editor = query("SELECT principal_id FROM security.identity_bindings WHERE subject='editor';")
    approver = query("SELECT principal_id FROM security.identity_bindings WHERE subject='approver';")
    for actor, replay in [(editor, save), (approver, lambda: call('approver', sql))]:
        query(f"UPDATE security.role_grants SET revoked=true WHERE principal_id='{actor}';")
        try:
            rejected(replay, 'reference_denied')
        finally:
            query(f"UPDATE security.role_grants SET revoked=false WHERE principal_id='{actor}';")

    # A privileged draft writer can add another immutable row, but that changes
    # financial identity. Applying it must fail independently of storage sealing.
    incompatible = str(uuid4())
    save(operation_id=incompatible, base=operation, values={'name':'Synthetic incompatible draft'})
    query(f"INSERT INTO ref_data.currencies SELECT catalog_id,revision_id,'synthetic_extra','ZZZ','Synthetic extra currency',9 FROM ref_data.currencies WHERE catalog_id='{catalog}' AND revision_id='{incompatible}' AND code='usd';")
    rejected(lambda: call('approver', f"SELECT security.apply_reference_display('{catalog}','{source}','{incompatible}','{uuid4()}',1)"), 'reference_economic_change')
    assert query(f"SELECT state FROM ref_data.revisions WHERE catalog_id='{catalog}' AND id='{incompatible}';") == 'draft'
    assert json.loads(call('viewer', f"SELECT security.reference_display_state('{catalog}','{source}')::text"))['version'] == 1

    rejected(lambda: save(operation_id=str(uuid4()),base=source), 'reference_conflict')
    # An updated expected version cannot rescue a draft based on older labels.
    rejected(lambda: call('approver', f"SELECT security.apply_reference_display('{catalog}','{source}','{stale}','{uuid4()}',0)"), 'reference_conflict')
    rejected(lambda: call('approver', f"SELECT security.apply_reference_display('{catalog}','{source}','{stale}','{uuid4()}',1)"), 'reference_conflict')
    second = str(uuid4())
    save(operation_id=second, base=operation, values={'name':'Synthetic second label'})
    assert json.loads(call('approver', f"SELECT security.apply_reference_display('{catalog}','{source}','{second}','{uuid4()}',1)::text"))['version'] == 2
    assert json.loads(call('viewer', f"SELECT security.reference_display_state('{catalog}','{source}')::text"))['references']['currencies'] == [{'code':'usd','name':'Synthetic second label'}]
    # History names the applied versions of one item, newest first, with the author who saved each edit and only
    # its changed fields; unapplied drafts, other items and historical contexts list nothing. In working data only
    # people who may write or seal reference data read it (D261); a reader is refused.
    history = lambda table, code, bound=None, profile='editor': json.loads(call(profile, f"SELECT security.reference_display_history('{catalog}','{source}','{table}','{code}')::text", bound=bound))
    author = query(f"SELECT display_name FROM security.workspace_memberships WHERE principal_id='{editor}' AND workspace_id='{workspace}';")
    listed = history('currencies','usd')
    assert listed['truncated'] is False and history('currencies','usd',profile='approver') == listed, listed
    assert [{k:v for k,v in h.items() if k!='recordedAt'} for h in listed['versions']] == [
        {'version':2,'table':'currencies','code':'usd','author':author,'changes':{'name':'Synthetic second label'}},
        {'version':1,'table':'currencies','code':'usd','author':author,'changes':{'name':'Synthetic revised dollar'}}], listed
    assert all(isinstance(h['recordedAt'],str) for h in listed['versions']) and listed['versions'][0]['recordedAt']>=listed['versions'][1]['recordedAt']
    none = {'versions':[],'truncated':False}
    assert history('currencies','eur') == none and history('commodities','usd') == none
    assert history('currencies','usd', bound=context) == none
    rejected(lambda: history('currencies','usd', profile='viewer'), 'reference_denied')
    assert history('currencies','usd', bound=context, profile='viewer') == none

    # A second workspace may read the catalog but cannot discover this
    # workspace's drafts or adopt its activation target.
    other = str(uuid4())
    query(f"INSERT INTO security.workspaces VALUES('{tenant}','{other}','synthetic_reference_scope');INSERT INTO security.reference_catalog_scopes VALUES('{catalog}','{tenant}','{other}','read_only');INSERT INTO security.workspace_memberships SELECT tenant_id,'{other}',principal_id,login_handle,display_name,enabled,version FROM security.workspace_memberships WHERE tenant_id='{tenant}' AND workspace_id='{workspace}';INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,role_id,tenant_id,'{other}',granted_from,granted_to,revoked FROM security.role_grants WHERE tenant_id='{tenant}' AND workspace_id='{workspace}';")
    assert call('editor', 'SELECT count(*) FROM ref_data.display_edits', selected_workspace=other) == '0'
    rejected(lambda: call('editor', f"SELECT security.save_reference_display_edit('{catalog}','{source}','{source}','{operation}','currencies','usd','{{\"name\":\"Synthetic revised dollar\"}}')", selected_workspace=other), 'reference_denied')
    rejected(lambda: call('approver', f"SELECT security.apply_reference_display('{catalog}','{source}','{second}','{uuid4()}',0)", selected_workspace=other), 'reference_denied')

    # Natural authority expiry during each admission lock wait must be checked
    # against the trusted clock after the lock, even for an exact replay.
    lock_key=f"hashtextextended('{catalog}'||'{source}'||'{tenant}'||'{workspace}',730849147)"
    def after_wait(lock_sql, work):
        with ThreadPoolExecutor(max_workers=1) as pool:
            blocker=pool.submit(query, f"SET application_name='reference_lock_check';BEGIN;{lock_sql};SELECT pg_sleep(1.5);COMMIT;")
            deadline=time.monotonic()+3
            while query("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='reference_lock_check' AND wait_event='PgSleep');") != 't':
                assert time.monotonic()<deadline, 'Owned reference blocker did not acquire its lock'
                time.sleep(.01)
            try:
                work()
            finally:
                blocker.result()
    for lock_sql in [f"SELECT id FROM security.principals WHERE id='{editor}' FOR UPDATE",f"SELECT pg_advisory_xact_lock({lock_key})"]:
        query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.5 seconds' WHERE principal_id='{editor}' AND workspace_id='{workspace}';")
        try:
            after_wait(lock_sql, lambda: rejected(save,'reference_denied'))
        finally:
            query(f"UPDATE security.role_grants SET granted_to='9999-12-31' WHERE principal_id='{editor}' AND workspace_id='{workspace}';")
    # A draft insertion that already holds the revision share lock must finish
    # before compatibility is proved. Proving before waiting at the seal would
    # approve an unobserved economic row committed during that wait.
    racing = str(uuid4())
    save(operation_id=racing,base=second,values={'name':'Synthetic racing label'})
    inserted=f"INSERT INTO ref_data.currencies SELECT catalog_id,revision_id,'synthetic_race','YYY','Synthetic concurrent currency',9 FROM ref_data.currencies WHERE catalog_id='{catalog}' AND revision_id='{racing}' AND code='usd'"
    after_wait(inserted,lambda:rejected(lambda:call('approver',f"SELECT security.apply_reference_display('{catalog}','{source}','{racing}','{uuid4()}',2)"),'reference_economic_change'))
    assert query(f"SELECT state FROM ref_data.revisions WHERE catalog_id='{catalog}' AND id='{racing}';") == 'draft'

    # Expiry while waiting on the draft lock must roll back the seal and receipt,
    # rather than make an already-admitted authority successful after expiry.
    expiring = str(uuid4())
    save(operation_id=expiring,base=second,values={'name':'Synthetic expired approval'})
    query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.5 seconds' WHERE principal_id='{approver}' AND workspace_id='{workspace}';")
    try:
        after_wait(f"SELECT id FROM ref_data.revisions WHERE catalog_id='{catalog}' AND id='{expiring}' FOR UPDATE",lambda:rejected(lambda:call('approver',f"SELECT security.apply_reference_display('{catalog}','{source}','{expiring}','{uuid4()}',2)"),'reference_denied'))
    finally:
        query(f"UPDATE security.role_grants SET granted_to='9999-12-31' WHERE principal_id='{approver}' AND workspace_id='{workspace}';")
    assert query(f"SELECT state FROM ref_data.revisions WHERE catalog_id='{catalog}' AND id='{expiring}';") == 'draft'
    assert json.loads(call('viewer', f"SELECT security.reference_display_state('{catalog}','{source}')::text"))['version'] == 2
    assert query(f"SELECT name FROM ref_data.currencies WHERE catalog_id='{catalog}' AND revision_id='{source}' AND code='usd';") == original
    # History lists the latest 20 applied versions and says when earlier ones are not listed.
    base = second
    for version in range(3, 22):
        step = str(uuid4())
        save(operation_id=step, base=base, values={'name': f'Synthetic label {version}'})
        call('approver', f"SELECT security.apply_reference_display('{catalog}','{source}','{step}','{uuid4()}',{version-1})")
        base = step
        listed = history('currencies','usd')
        assert [h['version'] for h in listed['versions']] == list(range(version, max(version-20, 0), -1)) and listed['truncated'] == (version > 20), listed
    print('PASS reference metadata: immutable rows, compatibility proof, stale/revoked retries, workspace/history isolation and expiry after locks', flush=True)


def main():
    import argparse
    import os
    from pathlib import Path
    import shlex
    import shutil
    import subprocess
    import tempfile
    from preview_fixture import fixture

    if not __debug__ or os.environ.get('PYTHONOPTIMIZE') not in (None, '', '0'):
        raise ValueError('Python assertions must remain enabled for verification.')
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin',type=Path,required=True)
    args=parser.parse_args()
    root=Path(__file__).resolve().parents[1]
    binaries=args.pg_bin.resolve()
    for name in ('initdb','pg_ctl','psql'):
        if not (binaries/name).is_file():
            parser.error('Missing installed PostgreSQL binary: '+str(binaries/name))
    directory=Path(tempfile.mkdtemp(prefix='oetrm-ref-',dir='/tmp')).resolve()
    directory.chmod(0o700)
    environment={k:v for k,v in os.environ.items() if not k.startswith('PG')}
    environment.update(PGHOST=str(directory),PGPORT='55439',PGDATABASE='postgres',PGUSER='preview_owner',PGTZ='UTC')
    safe_to_remove=True
    def run(command,**options):
        result=subprocess.run([str(v) for v in command],cwd=root,env=environment,capture_output=True,text=True,timeout=30,**options)
        if result.returncode:
            raise RuntimeError(result.stderr)
        return result.stdout.strip()
    def query(sql):
        return run([binaries/'psql','-X','-qAt','-v','ON_ERROR_STOP=1'],input=sql)
    try:
        run([binaries/'initdb','-D',directory/'data','--auth-local=trust','--auth-host=reject','--locale=C','--encoding=UTF8','--username=preview_owner'])
        try:
            safe_to_remove=False
            run([binaries/'pg_ctl','-D',directory/'data','-l',directory/'postgres.log','-w','-o',
                 shlex.join(['-k',str(directory),'-h','','-p','55439','-c','track_commit_timestamp=on']),'start'])
            for migration in sorted((root/'db/migrations').glob('*.sql')):
                query(migration.read_text())
            reference,market,security,manifest=fixture(root)
            for sql in (reference,market,security):
                query(sql)
            run_checks(query,manifest)
        finally:
            run([binaries/'pg_ctl','-D',directory/'data','-w','-m','fast','stop'])
            safe_to_remove=True
    finally:
        if safe_to_remove:
            shutil.rmtree(directory)


if __name__=='__main__':
    main()
