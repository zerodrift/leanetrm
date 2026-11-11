#!/usr/bin/env python3
"""Qualify context-selected catalogue publications in an isolated, disposable stock PostgreSQL cluster."""
import argparse
from copy import deepcopy
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile
from uuid import uuid4

from preview_fixture import fixture, trading_fixture, trade_booking
from close_session_fixture import sql_session

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path)
    parser.add_argument('--upgrade-existing-close', action='store_true', help='Record and certify a close under the installed schema before upgrading')
    args = parser.parse_args()
    if not __debug__:
        parser.error('Assertions must remain enabled')
    bins = {name: args.pg_bin.resolve() / name for name in ('initdb', 'pg_ctl', 'psql')}
    for binary in bins.values():
        if not binary.is_file(): parser.error(f'Missing installed binary: {binary}')
    directory = Path(tempfile.mkdtemp(prefix='openetrm-catalogue-context-', dir='/tmp')).resolve()
    directory.chmod(0o700)
    data = directory / 'data'
    env = {k: v for k, v in os.environ.items() if not k.startswith(('PG', 'OPENETRM_'))}
    env['PGTZ'] = 'UTC'
    started = False

    def run(command, *, include_diagnostics=False, **kwargs):
        result = subprocess.run([str(c) for c in command], cwd=ROOT, env=env, text=True,
                                capture_output=True, timeout=90, **kwargs)
        if result.returncode: raise RuntimeError(result.stdout+result.stderr)
        return (result.stdout+result.stderr if include_diagnostics else result.stdout).strip()

    try:
        run([bins['initdb'], '-D', data, '--auth-local=trust', '--auth-host=reject', '--encoding=UTF8',
             '--locale=C', '--no-instructions', '--username=preview_owner'])
        started = True
        run([bins['pg_ctl'], '-D', data, '-w', '-t', '15', '-l', directory / 'postgres.log', '-o',
             shlex.join(['-k', str(directory), '-h', '', '-p', '55479', '-c', 'track_commit_timestamp=on']), 'start'])
        psql = [bins['psql'], '-X', '-h', directory, '-p', '55479', '-U', 'preview_owner', '-d', 'postgres',
                '-qAt', '-v', 'ON_ERROR_STOP=1', '-v', 'VERBOSITY=verbose']

        def query(sql): return run(psql, input=sql)

        assert Path(query('SHOW data_directory;')).resolve() == data
        query((ROOT/'db/bootstrap.sql').read_text())
        migrations=sorted((ROOT / 'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql'))
        upgrade=[migration for migration in migrations if args.upgrade_existing_close and int(migration.name[:4])>=230]
        for migration in migrations:
            if migration not in upgrade:query(migration.read_text())
        reference, market, security, manifest = fixture(ROOT)
        for sql in (reference, market, security, trading_fixture(manifest['catalogId'])): query(sql)
        catalog, rcat, rrev, tenant, workspace = [manifest[k] for k in
            ('catalogId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId')]
        query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN (VALUES('SealMarketDefinitions'),('WriteMarketDefinitions'),('PublishMarketData'),('RunScenario'),('RunJobs')) x(p) WHERE code='trader' ON CONFLICT DO NOTHING;")
        revision=manifest['revisionId']
        def runtime(sql, login='trader'):
            token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                        f"'https://preview.invalid','{login}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
            return query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');{sql}COMMIT;")
        def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
        if upgrade:
            before=next(f for f in manifest['frames'] if f['at'].startswith('2020-12-17'))
            sql_session(query,manifest,'test_existing_source','test_utc_every_day',before['at'][11:16],['future'],['trader'])
            selected=literal(before['observationIds'])
            original_pins=json.loads(query(f"SELECT json_agg(json_build_object('seriesId',series_id,'observationId',id,'inputUse','variable')) FROM market_data.observations WHERE catalog_id='{catalog}' AND id IN(SELECT value::uuid FROM jsonb_array_elements_text({selected}));"))
            original_members=[dict(curve='future',contractId=contract,ordinal=i) for i,contract in enumerate(before['curveContracts']['future'])]
            original_close=runtime(f"SELECT security.register_market_close('{catalog}','{revision}','{uuid4()}','Synthetic retained source','test_existing_source','2020-12-17','{before['at']}',{literal(original_pins)},{literal(original_members)});")
            original_context=runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-17',NULL);")
            lineage_sql=f"SELECT concat_ws('|',c.id,c.publication_order,c.publication_xid,r.known_from) FROM market_data.market_closes c JOIN market_data.market_close_receipts r ON(r.catalog_id,r.publication_order,r.publication_xid)=(c.catalog_id,c.publication_order,c.publication_xid) WHERE(c.catalog_id,c.id)=('{catalog}','{original_close}');"
            lineage=query(lineage_sql)
            assert lineage, 'The original recorded close must already have a certified receipt'
            for migration in upgrade:query(migration.read_text())
            assert query(lineage_sql)==lineage, 'The upgrade changed the original close publication or receipt'
            upgraded=json.loads(runtime(f"SELECT security.use_market_context('{catalog}','{original_context}');SELECT security.read_market_close_parts('{catalog}',ARRAY['{original_close}']::uuid[],'{original_context}')::text;"))[0]
            source,=upgraded['sources']
            assert source['definition_revision_id']==revision and source['reference_revision_id']==rrev
            assert {p['observation_id'] for p in source['pins']}==set(before['observationIds'])
            assert [m['contract_id'] for m in source['members']]==before['curveContracts']['future']
            assert query(f"SELECT count(*) FROM market_data.catalogue_contexts WHERE catalog_id='{catalog}' AND context_id='{original_context}';")=='0'
            print('PASS upgrade retains the exact recorded close, pins, publication transaction and original certified knowledge without inventing catalogue history')
        pin=dict(definitionRevisionId=revision,referenceCatalogId=rcat,referenceRevisionId=rrev)
        curves=json.loads(query(f"SELECT json_agg(x ORDER BY code) FROM(SELECT code,'curve' kind,revision_id AS \"definitionRevisionId\",reference_catalog_id AS \"referenceCatalogId\",reference_revision_id AS \"referenceRevisionId\" FROM market_data.curves WHERE catalog_id='{catalog}' AND revision_id='{revision}' UNION ALL SELECT code,'rate',revision_id,reference_catalog_id,reference_revision_id FROM market_data.rate_curves WHERE catalog_id='{catalog}' AND revision_id='{revision}')x;"))
        definition=dict(effectiveFrom='2020-12-15',effectiveTo='2020-12-19',curves=curves,roles=dict(primary=pin,units=pin,gas=pin,ppa=pin))
        reference_tables=json.loads(query("SELECT json_agg(c.relname ORDER BY c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='ref_data' AND c.relkind='r' AND EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attname='catalog_id' AND NOT a.attisdropped) AND EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attname='revision_id' AND NOT a.attisdropped);"))
        definition['roles'].update({'reference:'+table:pin for table in reference_tables})
        def publish(body, expected=None, request=None):
            expect="NULL" if expected is None else "'"+expected+"'::uuid"
            return json.loads(runtime(f"SELECT security.publish_catalogue('{catalog}','{request or uuid4()}',{expect},{literal(body)})::text;"))
        def context(day='2020-12-18',known=None):
            at='NULL' if known is None else "'"+known+"'::timestamptz"
            return runtime(f"SELECT security.capture_date_context('{catalog}','{day}',{at});")
        def read(ctx):
            return json.loads(runtime(f"SELECT security.use_market_context('{catalog}','{ctx}');SELECT security.read_catalogue('{catalog}','{ctx}')::text;"))
        def reject(code, action):
            try:action()
            except RuntimeError as error:
                if code not in str(error):raise
            else:raise AssertionError('Expected '+code)
        first=publish(definition);old=context();known=query(f"SELECT known_at FROM market_data.temporal_contexts WHERE(catalog_id,id)=('{catalog}','{old}');")
        assert read(old)['id']==first['id']
        # A second sealed source introduces one curve without rewriting any of
        # the first source's terms, contract identities or observations.
        additional=str(uuid4())
        source_tables=['contracts','series']
        query(f"INSERT INTO market_data.revisions VALUES('{catalog}','{additional}','{rcat}','{rrev}','draft',0);INSERT INTO market_data.revision_periods VALUES('{catalog}','{additional}','2020-12-15','2020-12-19');")
        for table in source_tables:
            columns=json.loads(query(f"SELECT json_agg(attname ORDER BY attnum) FROM pg_attribute WHERE attrelid='market_data.{table}'::regclass AND attnum>0 AND NOT attisdropped AND attgenerated='';"))
            quoted=','.join('"'+c+'"' for c in columns)
            projection=','.join("'"+additional+"'::uuid" if c=='revision_id' else '"'+c+'"' for c in columns)
            condition="id" if table=='contracts' else "contract_id"
            query(f"INSERT INTO market_data.{table}({quoted}) SELECT {projection} FROM market_data.{table} WHERE catalog_id='{catalog}' AND revision_id='{revision}' AND {condition} IN(SELECT contract_id FROM market_data.curve_members WHERE catalog_id='{catalog}' AND revision_id='{revision}' AND curve_code='future');")
        columns=json.loads(query("SELECT json_agg(attname ORDER BY attnum) FROM pg_attribute WHERE attrelid='market_data.curves'::regclass AND attnum>0 AND NOT attisdropped AND attgenerated='';"))
        projection=','.join("'"+additional+"'::uuid" if c=='revision_id' else "'additional_future'" if c=='code' else "'Synthetic additional futures'" if c=='name' else '"'+c+'"' for c in columns)
        query(f"INSERT INTO market_data.curves({','.join(columns)}) SELECT {projection} FROM market_data.curves WHERE catalog_id='{catalog}' AND revision_id='{revision}' AND code='future';INSERT INTO market_data.curve_members SELECT catalog_id,'{additional}','additional_future',contract_id,ordinal FROM market_data.curve_members WHERE catalog_id='{catalog}' AND revision_id='{revision}' AND curve_code='future';UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{catalog}' AND id='{additional}';")
        after=deepcopy(definition);after['effectiveFrom']='2020-12-18';after['curves'].append(dict(code='additional_future',kind='curve',**{**pin,'definitionRevisionId':additional}))
        after['roles']['reference:futures_option_products']={**pin,'definitionRevisionId':additional}
        # Explicit additional-source inputs have different values. No date/code
        # alias may price an original held future from these observations.
        columns=json.loads(query("SELECT json_agg(attname ORDER BY attnum) FROM pg_attribute WHERE attrelid='market_data.observations'::regclass AND attnum>0 AND NOT attisdropped AND attgenerated='';"))
        projection=','.join("'"+additional+"'::uuid" if c=='definition_revision_id' else 'gen_random_uuid()' if c=='id' else "'additional_'||source_event_id" if c=='source_event_id' else 'value+1' if c=='value' else '"'+c+'"' for c in columns)
        query(f"INSERT INTO market_data.observations({','.join(columns)}) SELECT {projection} FROM market_data.observations WHERE catalog_id='{catalog}' AND definition_revision_id='{revision}' AND series_id IN(SELECT id FROM market_data.series WHERE catalog_id='{catalog}' AND revision_id='{additional}');")
        request=str(uuid4());second=publish(after,first['id'],request)
        equivalent=deepcopy(after);equivalent['curves'].reverse()
        equivalent['effectiveFrom']='2020-12-18';equivalent['effectiveTo']='2020-12-19'
        for source in equivalent['curves']+list(equivalent['roles'].values()):
            for key in ('definitionRevisionId','referenceCatalogId','referenceRevisionId'):source[key]=source[key].upper()
        assert publish(equivalent,first['id'],request)==second,'Equivalent typed coordinates changed command identity'
        assert publish(after,first['id'],request)==second
        assert publish(after,first['id'])==second,'A retried fact created a second publication'
        assert publish(after,second['id'])==second,'Reaffirming the current fact created a new publication'
        changed=deepcopy(after);changed['effectiveFrom']='2020-12-17'
        reject('catalogue_conflict',lambda:publish(changed,first['id'],request))
        current=context();assert read(current)['id']==second['id']
        assert len(read(current)['curves'])==len(curves)+1
        assert read(old)['id']==first['id'],'An existing captured context widened after an addition'
        assert read(context(known=known))['id']==first['id'],'Older knowledge saw the later addition'
        assert read(context(day='2020-12-17'))['id']==first['id'],'Publication order overrode effective applicability'
        current_on=json.loads(runtime(f"SELECT security.read_request_catalogue('{catalog}','2020-12-17')::text;"))
        assert current_on['id']==first['id'],'Current knowledge ignored the explicit working business date'
        reject('catalogue_context',lambda:runtime(f"SELECT security.use_market_context('{catalog}','{current}');SELECT security.read_request_catalogue('{catalog}','2020-12-17');"))

        assert query(f"SELECT count(*) FROM market_data.catalogue_publications WHERE catalog_id='{catalog}';")=='2'
        reject('catalogue_denied',lambda:runtime(f"SELECT security.use_market_context('{catalog}','{old}');SELECT security.publish_catalogue('{catalog}','{uuid4()}','{second['id']}',{literal(definition)});"))
        frame=next(f for f in manifest['frames'] if f['at'].startswith('2020-12-18'))
        sql_session(query,manifest,'test_source_inputs','test_utc_every_day',frame['at'][11:16],['future'],['trader'])
        selected_ids=frame['observationIds']
        pins=json.loads(query(f"SELECT json_agg(json_build_object('definitionRevisionId',definition_revision_id,'seriesId',series_id,'observationId',id,'inputUse','variable')) FROM market_data.observations WHERE catalog_id='{catalog}' AND(id IN({','.join(chr(39)+i+chr(39) for i in selected_ids)}) OR(definition_revision_id='{additional}' AND observed_at='{frame['at']}'));"))
        members=[dict(definitionRevisionId=owner,curve=code,contractId=contract,ordinal=i,scope=scope)
                 for owner,code,contracts,scope in [(revision,'future',frame['curveContracts']['future'],'root'),
                    (additional,'additional_future',frame['curveContracts']['future'],'retained')]
                 for i,contract in enumerate(contracts)]
        close_request=str(uuid4())
        def record(pins,members):
            return runtime(f"SELECT security.register_market_close('{catalog}','{revision}','{close_request}','Synthetic exact sources','test_source_inputs','2020-12-18','{frame['at']}',{literal(pins)},{literal(members)});")
        close=record(pins,members)
        assert record(list(reversed(pins)),list(reversed(members)))==close,'Pin ordering changed the first close outcome'
        first_close_request=close_request;close_request=str(uuid4())
        assert record(pins,members)==close,'An unchanged current close created another publication'
        changed_members=deepcopy(members);changed_members[0]['scope']='retained'
        reject('pilot_close_conflict',lambda:record(pins,changed_members))
        assert query(f"SELECT count(*) FROM market_data.market_closes WHERE catalog_id='{catalog}' AND session_code='test_source_inputs';")=='1'

        native_parts=json.loads(runtime(f"SELECT security.read_market_close_parts('{catalog}',ARRAY['{close}']::uuid[],NULL)::text;"))[0]
        assert {part['definition_revision_id'] for part in native_parts['sources']}=={revision,additional}
        assert native_parts['primary_definition_revision_id']==revision
        # Generation v1 has one exact source in its profile and report contract.
        # Admission must reject the composite anchor before allocating a job.
        schedule=dict(requestId=str(uuid4()),name='Synthetic source qualification',operation='generated_pilot_close_v1',
                      dueAt=None,expiresAt=None,intervalSeconds=None,timeZone='UTC',misfire='run_once',overlap='forbid',
                      timeoutSeconds=120,maximumAttempts=1,profileId=None,profileVersion=None)
        generated=dict(session='test_source_inputs',priorCloseId=close,bookIds=[str(uuid4())],curveCodes=['future'],
                       sourceCode='fixture',seed=1,leafMoves=[dict(curve='future',maxTicks=1)],
                       generator='seeded_clearing_tick_move_v1',tickBasis='clearing_increment',parameterPolicy='carry_exact_anchor_v1',
                       frozenPolicy='carry_exact_anchor_frozen_facts_v1',membershipPolicy='retained_live_members_v1',
                       calendarPolicy='every_utc_calendar_day_v1',closePolicy='utc_due_instant_v1',valuationPolicy={})
        reject('pilot_close_source_scope',lambda:runtime(
            f"SELECT automation.create_pilot_close_job({literal(schedule)},{literal(generated)});"))
        earlier_frame=next(f for f in manifest['frames'] if f['at'].startswith('2020-12-17'))
        earlier_pins=json.loads(query(f"SELECT json_agg(json_build_object('definitionRevisionId',definition_revision_id,'seriesId',series_id,'observationId',id,'inputUse','variable')) FROM market_data.observations WHERE catalog_id='{catalog}' AND id IN({','.join(chr(39)+i+chr(39) for i in earlier_frame['observationIds'])});"))
        earlier_members=[m for m in members if m['definitionRevisionId']==revision]
        earlier_close=runtime(f"SELECT security.register_market_close('{catalog}','{revision}','{uuid4()}','Synthetic earlier source','test_source_inputs','2020-12-17','{earlier_frame['at']}',{literal(earlier_pins)},{literal(earlier_members)});")
        if args.dotnet:
            frame=next(f for f in manifest['frames'] if f['at'].startswith('2020-12-18'))
            contract=frame['curveContracts']['future'][0]
            parts=json.loads(query(f"SELECT json_build_object('at','{frame['at']}','frozenObservationIds','[]'::json,'observationIds',(SELECT json_agg(id ORDER BY id) FROM market_data.observations WHERE catalog_id='{catalog}' AND definition_revision_id='{additional}' AND observed_at='{frame['at']}'),'curveContracts',json_build_object('additional_future',json_build_array('{contract}')))::text;"))
            operation=lambda definition,curve,request=None:runtime(f"SELECT security.book_futures_with_parties('{catalog}','{request or uuid4()}','d1000000-0000-0000-0000-000000000001','{definition}','{curve}','{contract}','{frame['at']}',NULL,1,3,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(trade_booking())})::text;")
            original_trade=json.loads(operation(revision,'future'))
            added_operation=str(uuid4());added_trade=json.loads(operation(additional,'additional_future',added_operation))
            managed_context=context()
            malformed=dict(businessDate='2020-02-30',at=frame['at'],source=pin,sourceInputs=None,resolvesLatestCorrection=False)
            reject('temporal_inputs',lambda:runtime(f"SELECT security.use_market_context('{catalog}','{managed_context}');SELECT risk.submit('{catalog}','{uuid4()}','{managed_context}','{revision}','{frame['at']}','{{}}'::jsonb,{literal(malformed)});"))
            removed=deepcopy(after);removed['curves']=[c for c in removed['curves'] if c['code']!='additional_future']
            removal=publish(removed,second['id'])
            assert json.loads(operation(additional,'additional_future',added_operation))==added_trade,'A catalogue change broke an accepted booking retry'
            reject('catalogue_choice_changed',lambda:operation(additional,'additional_future'))
            restored=publish(after,removal['id'])
            assert restored['id']!=second['id'] and restored['publicationOrder']==4,'A-to-B-to-A revived an old publication'
            expected=[]
            for definition,trade in [(revision,original_trade),(additional,added_trade)]:
                mark=query(f"SELECT value FROM market_data.observations WHERE catalog_id='{catalog}' AND definition_revision_id='{definition}' AND observed_at='{frame['at']}' AND series_id=(SELECT id FROM market_data.series WHERE catalog_id='{catalog}' AND revision_id='{definition}' AND contract_id='{contract}');")
                expected.append(dict(trade=trade['tradeId'],definition=definition,mark=mark))
            clone=deepcopy(manifest);clone['revisionId']=additional;clone['referenceRevisionId']=str(uuid4());clone['frames']=[];clone['inputMarkets']=[]
            token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                        f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
            check_file=directory/'managed.json'
            shape_token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                        f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
            from check_risk_jobs_db import provision_worker
            login='context_check_worker'
            provision_worker(query,manifest,login)
            check_file.write_text(json.dumps(dict(earlierClose=earlier_close,shapeToken=shape_token,manifest=clone,connection=f'Host={directory};Port=55479;Database=postgres;Username=preview_runtime;Pooling=false',token=token,context=managed_context,frame=frame,close=close,original=revision,expected=expected)))
            print(run([args.dotnet,'run','--project','tests/OpenEtrm.Api.Checks','--','--catalogue-trading',check_file]))
            worker_connection=directory/'worker-connection'
            worker_connection.write_text(f'Host={directory};Port=55479;Database=postgres;Username={login};SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=2')
            worker_connection.chmod(0o600)
            worker_config=directory/'worker.json'
            worker_config.write_text(json.dumps(dict(connectionFile=str(worker_connection),leaseSeconds=30,pollSeconds=1,timeoutSeconds=45)))
            worker_config.chmod(0o600)
            env['OPENETRM_RISK_WORKER_CONFIG']=str(worker_config)
            try:
                worker_log=run([args.dotnet,ROOT/'src/OpenEtrm.Api/bin/Debug/net10.0/OpenEtrm.Api.dll','--risk-worker','once'],include_diagnostics=True)
            finally:
                env.pop('OPENETRM_RISK_WORKER_CONFIG',None)
            oracle=json.loads(check_file.with_suffix('.report.json').read_text())
            completed=json.loads(query(f"SELECT json_build_object('state',state,'error',error_code,'result',result)::text FROM risk.jobs WHERE id='{oracle['job']}';"))
            assert completed['state']=='completed' and completed['error'] is None, f"Mixed-source worker did not succeed: {completed['state']} {completed['error']}\n{worker_log}"
            assert completed['result']['factors']==oracle['factors'], 'Worker replay changed the exact scenario results'
            assert completed['result']['inputs']['positions']==oracle['positions'], 'Worker replay changed held version evidence'
            print('PASS claimed worker reproduces interactive scenario results and held version evidence from the retained multi-source inputs')
        reject('permission denied',lambda:runtime(f"SELECT security.capture_pre_catalogue_context('{catalog}','2020-12-18',NULL);"))
        query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{rcat}';")
        reject('catalogue_denied',lambda:publish(after,first['id'],request))
        reject('catalogue_source',lambda:publish(after,first['id']))
        print('PASS catalogue context: additive publication, original exact source pins, both temporal axes, immutable captured prefix and idempotent first outcomes')
    finally:
        if started:
            result = subprocess.run([str(bins['pg_ctl']), '-D', str(data), '-m', 'immediate', '-w', '-t', '15', 'stop'],
                                    env=env, text=True, capture_output=True, timeout=25)
            if result.returncode:
                print(f'Owned cluster retained for inspection: {directory}')
                raise RuntimeError('Could not stop the owned catalogue test cluster')
        shutil.rmtree(directory)


if __name__ == '__main__': main()
