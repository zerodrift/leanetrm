"""Run actual retained economic workers against native swap correction/cash history."""
import json
import os
import shutil
import subprocess
from uuid import uuid4

from check_lifecycle_swaps import checks as swap_checks,fixture,upgrade_checks
from check_lifecycle_economic_worker import ROOT
from check_reference_editing import main
from check_risk_jobs_db import provision_worker


def workers(query,manifest,path,managed,packet):
    directory=path.parent.resolve();assert directory.name.startswith('oetrm-ref-')
    query("INSERT INTO security.role_permissions SELECT role_id,'RunScenario' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    login='synthetic_swap_economic_worker';provision_worker(query,manifest,login)
    access=json.loads(query(f"SELECT jsonb_build_object('contexts',has_table_privilege('{login}','trading.contexts','SELECT'),'receipts',has_table_privilege('{login}','trading.knowledge_receipts','SELECT'))::text;"))
    assert access=={'contexts':False,'receipts':True},'The profile reader changed the existing native context/receipt table boundary'
    assert query(f"SELECT NOT has_function_privilege('{login}','lifecycle.require_read(uuid,timestamptz)','EXECUTE') AND NOT has_function_privilege('{login}','settlement_data.require_futures_margin_preview(uuid,timestamptz)','EXECUTE');")=='t'
    assert query("SELECT p.proowner='openetrm_security_owner'::regrole AND p.prorettype='jsonb'::regtype AND NOT p.proretset AND p.prokind='f' AND NOT p.proisstrict AND NOT p.proleakproof AND p.provariadic=0 AND p.proargmodes IS NULL AND p.proallargtypes IS NULL AND p.pronargdefaults=0 AND p.proargdefaults IS NULL AND p.prosecdef AND p.provolatile='v' AND p.proparallel='u' AND l.lanname='plpgsql' AND p.proconfig=ARRAY['search_path=pg_catalog'] AND p.proacl::text='{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}' FROM pg_proc p JOIN pg_language l ON l.oid=p.prolang WHERE p.oid='security.read_lifecycle_projection_profiles(uuid,text,uuid,integer,uuid,timestamptz,date,timestamptz)'::regprocedure;")=='t'
    frozen=directory/'swap-worker-binaries';shutil.copytree(ROOT/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0',frozen)
    stamps={p.name:p.read_bytes() for p in frozen.glob('OpenEtrm.*.dll')}
    connection=directory/'swap-worker-connection';connection.write_text(f'Host={directory};Port=55439;Database=postgres;Username={login};SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=2');connection.chmod(0o600)
    config=directory/'swap-worker.json';config.write_text(json.dumps(dict(connectionFile=str(connection),leaseSeconds=30,pollSeconds=1,timeoutSeconds=60)));config.chmod(0o600)
    env={**os.environ,'OPENETRM_RISK_WORKER_CONFIG':str(config)};dotnet=os.environ.get('DOTNET','dotnet')
    def context(day):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.capture_date_context('{manifest['catalogId']}','{day}',NULL);COMMIT;")
    for variable,day,at,frame_at in [(True,'2020-12-18','2020-12-18T16:00:00Z','2020-12-18T16:00:00Z'),
                                    (False,'2021-01-20','2021-01-20T00:00:00Z','2021-01-15T08:00:00Z')]:
        source=next(frame for frame in manifest['frames'] if frame['at']==frame_at)
        managed({**packet,'economicWorker':True,'selectedObservations':source['observationIds'],
            'context':{**packet['context'],'contextId':context(day),'asOf':at,'businessDate':day}})
        receipts=json.loads(path.with_suffix('.economic-worker.json').read_text());assert len(receipts['jobs'])==(2 if variable else 1)
        assert all(len(saved['report']['factors'])==(2 if variable else 0) for saved in receipts['jobs']),'The native swap worker lost its declared two-input curve closure'
        report=receipts['jobs'][0]['report'];catalog=manifest['catalogId'];trade=packet['target']['scopeId'];source_context=report['source']['contextId']
        arguments=[f"'{catalog}'","'financial_swap'",f"'{trade}'",'1',f"'{source_context}'",f"'{at}'::timestamptz",f"'{day}'::date",'until_at']
        def probe(args):
            return query(f"SET SESSION AUTHORIZATION {login};BEGIN;DO $probe$ DECLARE j jsonb;until_at timestamptz;rows jsonb;BEGIN "
                f"j=risk.claim(30,'{receipts['engineVersion']}');PERFORM risk.bind((j->>'jobId')::uuid,(j->>'leaseToken')::uuid);until_at=security.admit_trading_business_source('{catalog}','{source_context}','{at}','{day}');"
                f"rows=security.read_lifecycle_projection_profiles({','.join(args)});IF jsonb_array_length(rows)<>2 THEN RAISE EXCEPTION 'missing_exact_profiles';END IF;END$probe$;ROLLBACK;")
        probe(arguments)
        other=query(f"SELECT trade_id FROM trading.trade_revisions WHERE catalog_id='{catalog}' ORDER BY trade_id LIMIT 1;") or str(uuid4())
        for index,value in [(0,f"'{uuid4()}'"),(1,"'listed_option'"),(2,f"'{other}'"),(3,'2'),(4,f"'{uuid4()}'"),(5,f"'{at}'::timestamptz+interval '1 second'"),(6,f"'{day}'::date+1")]:
            forged=list(arguments);forged[index]=value
            try:probe(forged)
            except RuntimeError as error:assert 'risk_job_scope' in str(error),str(error)
            else:raise AssertionError('A retained worker broadened its exact product/profile source arguments')
        source_counts=query("SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM settlement_data.swap_determinations),(SELECT count(*) FROM settlement_data.swap_cash_payments))::text;")
        for saved in receipts['jobs']:
            assert query(f"SELECT NOT risk.needs_gas(j) FROM risk.jobs j WHERE id='{saved['id']}';")=='t'
            result=subprocess.run([dotnet,str(frozen/'OpenEtrm.Api.dll'),'--risk-worker','once'],cwd=ROOT,env=env,capture_output=True,text=True,timeout=90)
            if result.returncode:raise RuntimeError(result.stdout+result.stderr)
            actual=json.loads(query(f"SELECT jsonb_build_object('state',state,'error',error_code,'result',result)::text FROM risk.jobs WHERE id='{saved['id']}';"))
            assert actual['state']=='completed' and actual['result']==saved['report'],actual|{'workerDiagnostics':result.stdout+result.stderr}
        assert source_counts==query("SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM settlement_data.swap_determinations),(SELECT count(*) FROM settlement_data.swap_cash_payments))::text;")
        assert all((frozen/name).read_bytes()==value for name,value in stamps.items())
        print('PASS actual native swap economic worker: complete '+('historical/Monte Carlo source and branch-factor replay' if variable else 'source-known actual future payment activation and signed deterministic carry')+', no source writes',flush=True)


def checks(query,manifest):swap_checks(query,manifest,economic_worker=workers)


if __name__=='__main__':
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'build','tests/OpenEtrm.Api.Checks'],cwd=ROOT,capture_output=True,text=True,timeout=120)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
