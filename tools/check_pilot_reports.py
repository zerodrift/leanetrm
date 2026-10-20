"""Immutable generated-close reports, exercised only by the owned EOD fixture."""
from preview_client import PreviewClient
from uuid import uuid4
import subprocess
import time
from datetime import date,timedelta


def _token(query,manifest,profile):
    return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")


def run_checks(url,query,manifest,run_id,owner_profile='admin',*,psql,environment):
    assert query("SELECT to_regclass('automation.pilot_reports') IS NOT NULL;")=='t', 'Durable portfolio reports are not implemented'
    assert query("SELECT has_function_privilege('preview_runtime','security.pilot_report_sources_visible(uuid,uuid)','EXECUTE');")=='f','The private report-source predicate is executable by runtime'
    query("SET SESSION AUTHORIZATION preview_runtime;DO $$BEGIN PERFORM security.pilot_report_sources_visible(gen_random_uuid(),gen_random_uuid());RAISE EXCEPTION 'The private report-source predicate was callable';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;")
    owner=PreviewClient(url);owner.bootstrap();owner.call('profile',{'profile':owner_profile})
    report=owner.call('jobs/pilot-close/results?runId='+run_id)
    assert report['reportId']==report['runId']==report['close']['id']==run_id
    assert report['state']=='report_published' and report['positions'] and report['totals'] and report['risk']
    assert report['context']['contextId'] and report['context']['knownAt']
    for total in report['totals']:
        assert total['book'] and total['currencyLabel']
        assert isinstance(total['value']['numerator'],str) and int(total['value']['denominator'])>0
    for row in report['risk']:
        assert row['currencyLabel'] and row['factor']['unit'] and row['factor']['label']
        for key in ('up','down','upPnl','downPnl'):
            assert isinstance(row[key]['numerator'],str) and int(row[key]['denominator'])>0
        for key in ('delta','gamma'):
            assert isinstance(row[key]['value']['numerator'],str) and int(row[key]['value']['denominator'])>0
    viewer=PreviewClient(url);viewer.bootstrap();viewer.call('profile',{'profile':'viewer'})
    viewer.call('jobs/pilot-close/results?runId='+run_id,expected=403)
    query(f"BEGIN;DO $$BEGIN DELETE FROM automation.pilot_reports WHERE id='{run_id}';RAISE EXCEPTION 'Mutable report';EXCEPTION WHEN SQLSTATE '55000' THEN NULL;END$$;ROLLBACK;")
    assert owner.call('jobs/pilot-close/results?runId='+run_id)==report
    catalog=manifest['catalogId']
    # The model's canonical valuation identity is distinct from a retained
    # database instrument alias. The report must preserve both identities.
    futures=[p for p in report['positions'] if p['measure']=='futures_execution_pnl']
    assert futures and any(p['instrumentId']!=p['valuationId'] for p in futures), 'The retained-alias fixture was not exercised'
    def reject(statement,state):
        query("BEGIN;DO $$BEGIN "+statement+";RAISE EXCEPTION 'Expected exact reference rejection';EXCEPTION WHEN SQLSTATE '"+state+"' THEN NULL;END$$;ROLLBACK;")
    reject(f"INSERT INTO automation.pilot_report_models SELECT catalog_id,report_id,'{uuid4()}',definition_revision_id,reference_catalog_id,reference_revision_id,model_version,quantity_code,currency_code,future_contract_id,swap_instrument_id,future_series_code FROM automation.pilot_report_models WHERE report_id='{run_id}' AND swap_instrument_id IS NOT NULL LIMIT 1",'23514')
    reject(f"INSERT INTO automation.pilot_report_models SELECT m.catalog_id,m.report_id,security.futures_contract_id(m.reference_catalog_id,m.reference_revision_id,s.code),m.definition_revision_id,m.reference_catalog_id,m.reference_revision_id,m.model_version,m.quantity_code,m.currency_code,'{uuid4()}',NULL,s.code FROM automation.pilot_report_models m JOIN ref_data.futures_series s ON(s.catalog_id,s.revision_id)=(m.reference_catalog_id,m.reference_revision_id) WHERE m.report_id='{run_id}' AND NOT EXISTS(SELECT 1 FROM automation.pilot_report_models x WHERE x.report_id=m.report_id AND x.id=security.futures_contract_id(s.catalog_id,s.revision_id,s.code)) LIMIT 1",'23503')
    reject(f"INSERT INTO automation.pilot_report_factors SELECT catalog_id,report_id,ordinal+10000,definition_revision_id,reference_catalog_id,reference_revision_id,'{uuid4()}',observation_id,input_use,contract_id,quote_code,currency_code,quantity_code,source_code,role,base_value,bump_size,trading_tick FROM automation.pilot_report_factors WHERE report_id='{run_id}' LIMIT 1",'23503')
    assert query("SELECT count(*) FROM pg_constraint WHERE conrelid='automation.pilot_report_factors'::regclass AND contype='f' AND cardinality(conkey)>=5;")=='4'
    assert query("SELECT count(*) FROM pg_constraint WHERE conrelid='automation.pilot_report_positions'::regclass AND contype='f' AND confrelid='automation.pilot_report_models'::regclass AND cardinality(conkey)=4;")=='1'
    # A report did not exist at its calculation context's earlier marks-commit
    # cutoff. Returning it there would disclose a later publication.
    owner.call('market/context',{'effectiveOn':report['context']['effectiveOn'],'knownAt':report['context']['knownAt'],'mode':'at','selection':'latest_correction'})
    owner.call('jobs/pilot-close/results?runId='+run_id,expected=403)
    owner.call('market/current',{})
    next_day=(date.fromisoformat(report['context']['effectiveOn'])+timedelta(days=1)).isoformat()
    wrong_context=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{_token(query,manifest,owner_profile)}');SELECT security.capture_date_context('{catalog}','{next_day}',NULL);COMMIT;").splitlines()[-1]
    query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{_token(query,manifest,owner_profile)}');SELECT security.use_market_context('{catalog}','{wrong_context}');DO $$BEGIN PERFORM automation.read_pilot_report('{run_id}','{wrong_context}');RAISE EXCEPTION 'A retained report silently substituted a different business date';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;ROLLBACK;")
    query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{manifest['referenceCatalogId']}','{manifest['tenantId']}','{manifest['workspaceId']}');")
    try:owner.call('jobs/pilot-close/results?runId='+run_id,expected=403)
    finally:query(f"INSERT INTO security.reference_catalog_scopes VALUES('{manifest['referenceCatalogId']}','{manifest['tenantId']}','{manifest['workspaceId']}','owner');")
    assert owner.call('jobs/pilot-close/results?runId='+run_id)==report
    _read_boundary(query,manifest,run_id,owner_profile,psql,environment,len(report['positions']))
    print('PASS pilot reports: exact immutable rational report, separate measures, normalized sources and current reader authority',flush=True)


def _read_boundary(query,manifest,run_id,owner_profile,psql,environment,position_count):
    def token():return _token(query,manifest,owner_profile)
    prefix=lambda request:f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{request}');"
    counted=query("SET track_functions='all';"+prefix(token())+f"SELECT jsonb_array_length(automation.read_pilot_report('{run_id}',NULL)->'positions');SELECT calls FROM pg_stat_xact_user_functions WHERE funcid='security.pilot_report_visible(uuid,uuid)'::regprocedure;ROLLBACK;")
    assert counted.splitlines()[-2:]==[str(position_count),'2'], 'Report source authority was checked per row instead of before and after its fixed read: '+counted
    role=query("SELECT id FROM security.roles WHERE code='synthetic_pilot_operator';")
    principal=query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='{owner_profile}';")
    temporary,grant=str(uuid4()),str(uuid4());locker=None;reader=None
    try:
        query(f"BEGIN;INSERT INTO security.roles(id,code,name) VALUES('{temporary}','synthetic_report_reader','Synthetic expiring report reader');INSERT INTO security.role_permissions VALUES('{temporary}','ReadTrades');DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission='ReadTrades';INSERT INTO security.role_grants VALUES('{grant}','{principal}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '8 seconds',false);COMMIT;")
        request=token()
        locker=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        locker.stdin.write('BEGIN;LOCK TABLE automation.pilot_report_totals IN ACCESS EXCLUSIVE MODE;\n\\echo LOCKED\n');locker.stdin.flush()
        while locker.stdout.readline().strip()!='LOCKED':
            if locker.poll() is not None:raise AssertionError(locker.stderr.read())
        reader=subprocess.Popen(psql,env={**environment,'PGAPPNAME':'synthetic-report-reader'},stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        reader.stdin.write("\\set VERBOSITY verbose\n"+prefix(request)+f"SELECT jsonb_array_length(automation.read_pilot_report('{run_id}',NULL)->'positions');ROLLBACK;\n\\q\n");reader.stdin.flush()
        blocked=False
        for _ in range(100):
            if query("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='synthetic-report-reader' AND NOT l.granted AND l.relation='automation.pilot_report_totals'::regclass;")=='1':blocked=True;break
            if reader.poll() is not None:raise AssertionError(reader.communicate())
            time.sleep(.02)
        assert blocked,'The raw report reader did not reach its controlled storage wait'
        for _ in range(200):
            if query(f"SELECT granted_to<=clock_timestamp() FROM security.role_grants WHERE id='{grant}';")=='t':break
            time.sleep(.05)
        else:raise AssertionError('The independent report ReadTrades grant did not expire')
        locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();_,errors=locker.communicate(timeout=10)
        assert locker.returncode==0,errors
        output,errors=reader.communicate(timeout=10)
        assert reader.returncode!=0 and '42501' in errors, 'A report returned a successful result after authority expired during its read: '+output+errors
    finally:
        if locker is not None and locker.poll() is None:
            locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();locker.communicate(timeout=10)
        if reader is not None and reader.poll() is None:
            reader.communicate(timeout=10)
        query(f"BEGIN;DELETE FROM security.role_grants WHERE id='{grant}';DELETE FROM security.role_permissions WHERE role_id='{temporary}';DELETE FROM security.roles WHERE id='{temporary}';INSERT INTO security.role_permissions VALUES('{role}','ReadTrades') ON CONFLICT DO NOTHING;COMMIT;")
    print('PASS report reader: fixed rowsets use two full authority checks; natural ReadTrades expiry during the raw storage wait refuses the result',flush=True)
