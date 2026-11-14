"""Qualify actual managed economic futures workers against native adopted state."""
import json
import os
from pathlib import Path
import shutil
import subprocess
from uuid import uuid4

from check_reference_editing import main
from close_session_fixture import sql_session
from preview_fixture import fixture as baseline,catalogue_fixture,trading_fixture,trade_booking
from check_risk_jobs_db import provision_worker
from check_lifecycle_economic_worker import ROOT


def fixture(root):return baseline(root,fixing_frames=True)


def checks(query,manifest,*,execution_price='3',daily_at='2020-12-21T16:00:00Z',daily_due_at='2020-12-22T16:00:00Z',source_fields=None,job_checks=None):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    query(trading_fixture(catalog))
    from rate_curve_fixture import discount_default
    publication=f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}')"
    reference=f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    query(f"BEGIN;INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';"
          +discount_default(catalog,str(uuid4()),manifest['revisionId'],reference,'usd','interest_rates',publication,'2020-01-01T00:00:00Z')+"COMMIT;")
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest,'https://preview.invalid'))
    query("INSERT INTO security.role_permissions SELECT role_id,'RunScenario' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,profile='trader'):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','2020-12-18',NULL);")
    book='d1000000-0000-0000-0000-000000000001';contract='50000000-0000-0000-0000-000000000001'
    booked=json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','future','{contract}','2020-12-18T15:00:00Z',NULL,2,{execution_price},'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(trade_booking())})::text;"))
    final=query(f"SELECT to_char(expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"') FROM market_data.contracts WHERE(catalog_id,revision_id,id)=('{catalog}','{manifest['revisionId']}','{contract}');")
    policy=dict(id=str(uuid4()),definitionRevisionId=manifest['revisionId'],contractId=contract,settlementCurve='future',referenceCatalogId=manifest['referenceCatalogId'],
        referenceRevisionId=manifest['referenceRevisionId'],currency='usd',fromAt='2020-01-01T00:00:00Z',untilAt='2021-01-01T00:00:00Z',opening='include_opening_instant',
        increment='0.01',tie='to_even',stage='payment_total',roundingScope='held_binding_difference_payment_total_v1',implementation='dated_futures_margin_policy_v1',
        occurrences=[dict(id=str(uuid4()),at=daily_at,dueAt=daily_due_at,phase='daily'),dict(id=str(uuid4()),at=final,dueAt='2020-12-29T16:00:00Z',phase='final')])
    call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf='2020-12-18T16:00:00Z',policy=policy))});",'editor')
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(dict(operationId=str(uuid4()),tradeId=booked['tradeId'],bindingId=booked['bindingId'],expectedRevision=1,policyId=policy['id'],policyRevision=1,contextId=capture(),asOf='2020-12-18T16:00:00Z',adoption='native_execution_inception_v1'))});")
    sql_session(query,manifest,'synthetic_future_economic','test_business','16:00',['future'],[])
    directory=Path(query('SHOW unix_socket_directories;')).resolve();assert directory.name.startswith('oetrm-ref-')
    source=next(f for f in manifest['frames'] if f['at']=='2020-12-18T16:00:00Z')
    packet=dict(manifest=manifest,host=str(directory),at=source['at'],pins=source['observationIds'],
        bookId=book,tradeId=booked['tradeId'],contractId=contract,session='synthetic_future_economic')
    if source_fields is not None:packet.update(source_fields(query,manifest,source))
    packet['contextId']=capture()
    path=directory/'future-economic-check.json';path.write_text(json.dumps(packet));path.chmod(0o600)
    login='synthetic_future_economic_worker';provision_worker(query,manifest,login)
    assert query(f"SELECT NOT has_function_privilege('{login}','settlement_data.require_futures_margin_preview(uuid,timestamptz)','EXECUTE');")=='t'
    frozen=directory/'future-worker-binaries';shutil.copytree(ROOT/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0',frozen)
    stamps={p.name:p.read_bytes() for p in frozen.glob('OpenEtrm.*.dll')};dotnet=os.environ.get('DOTNET','dotnet')
    def managed(mode):
        result=subprocess.run([dotnet,str(frozen/'OpenEtrm.Api.Checks.dll'),mode,str(path)],cwd=ROOT,capture_output=True,text=True,timeout=90)
        if result.returncode:raise RuntimeError(result.stdout+result.stderr)
        print(result.stdout.strip(),flush=True)
    managed('--economic-future-worker-submit')
    receipts=json.loads(path.with_suffix('.economic-worker.json').read_text());assert len(receipts['jobs'])==2
    if job_checks is not None:job_checks(query,manifest,receipts['jobs'])
    assert all(len(r['report']['factors'])==1 for r in receipts['jobs'])
    connection=directory/'future-worker-connection';connection.write_text(f'Host={directory};Port=55439;Database=postgres;Username={login};SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=2');connection.chmod(0o600)
    config=directory/'future-worker.json';config.write_text(json.dumps(dict(connectionFile=str(connection),leaseSeconds=30,pollSeconds=1,timeoutSeconds=60)));config.chmod(0o600)
    counts=query("SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM settlement_data.futures_margin_runs))::text;")
    for saved in receipts['jobs']:
        assert query(f"SELECT NOT risk.needs_gas(j) FROM risk.jobs j WHERE id='{saved['id']}';")=='t'
        result=subprocess.run([dotnet,str(frozen/'OpenEtrm.Api.dll'),'--risk-worker','once'],cwd=ROOT,env={**os.environ,'OPENETRM_RISK_WORKER_CONFIG':str(config)},capture_output=True,text=True,timeout=90)
        if result.returncode:raise RuntimeError(result.stdout+result.stderr)
        actual=json.loads(query(f"SELECT jsonb_build_object('state',state,'error',error_code,'result',result)::text FROM risk.jobs WHERE id='{saved['id']}';"))
        assert actual['state']=='completed' and actual['result']==saved['report'],actual|{'workerDiagnostics':result.stdout+result.stderr}
    managed('--economic-worker-verify')
    assert counts==query("SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM settlement_data.futures_margin_runs))::text;")
    assert all((frozen/name).read_bytes()==value for name,value in stamps.items())
    print('PASS actual futures economic workers: complete historical/Monte Carlo source and margin-state replay, least-privilege reader and no financial writes',flush=True)


if __name__=='__main__':
    built=subprocess.run([os.environ.get('DOTNET','dotnet'),'build','tests/OpenEtrm.Api.Checks'],cwd=ROOT,capture_output=True,text=True,timeout=120)
    if built.returncode:raise RuntimeError(built.stdout+built.stderr)
    main(checks=checks,fixture_factory=fixture)
