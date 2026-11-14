"""Full physical future PV on two exact synthetic native market sources."""
import json
import os
from pathlib import Path
import subprocess
import shutil
from uuid import uuid4

from check_reference_editing import main
from check_futures_physical_delivery import fixture as physical_fixture
from futures_delivery_fixture import configure
from preview_fixture import catalogue_fixture
from rate_curve_fixture import discount_default
from close_session_fixture import sql_session
from check_risk_jobs_db import provision_worker


def fixture(root):
    reference,market,security,manifest=physical_fixture(root)
    calendar="'source_business_days','test_utc',96,'2026-10-13','2026-10-17'"
    assert market.count(calendar)==1
    market=market.replace(calendar,"'source_business_days','test_utc',96,'2026-10-13','2027-01-01'")
    catalog,primary=manifest['catalogId'],manifest['revisionId'];secondary=str(uuid4())
    # The second source owns a different mark level and rate observations. Its
    # exact reference terms remain compatible; the valuation explicitly selects
    # this mark as a proxy rather than inferring that it is the future price.
    table=lambda name,where,change: (f"INSERT INTO market_data.{name} SELECT (jsonb_populate_record(NULL::market_data.{name},to_jsonb(x)||"
        f"jsonb_build_object('revision_id','{secondary}')||{change})).* FROM market_data.{name} x WHERE {where};")
    own=f"(x.catalog_id,x.revision_id)=('{catalog}','{primary}')"
    far=f"(SELECT contract_id FROM market_data.curve_members m JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(m.catalog_id,m.revision_id,m.contract_id) WHERE(m.catalog_id,m.revision_id,m.curve_code)=('{catalog}','{primary}','nymex_ng') ORDER BY c.expiry_at DESC LIMIT 1)"
    clone=[f"INSERT INTO market_data.revisions SELECT (jsonb_populate_record(NULL::market_data.revisions,to_jsonb(x)||jsonb_build_object('id','{secondary}','state','draft','generation',0))).* FROM market_data.revisions x WHERE(catalog_id,id)=('{catalog}','{primary}');",
        table('revision_periods',own,"'{}'::jsonb"),table('contracts',own+f' AND x.id={far}',"'{}'::jsonb"),
        table('curves',own+" AND x.code='nymex_ng'","jsonb_build_object('code','synthetic_inventory','name','Synthetic independent inventory')"),
        table('curve_members',own+f" AND x.curve_code='nymex_ng' AND x.contract_id={far}","jsonb_build_object('curve_code','synthetic_inventory','ordinal',0)"),
        table('series',own+f" AND(x.contract_id={far} OR x.value_specification_code IS NOT NULL)","'{}'::jsonb"),
        table('rate_curves',own,"'{}'::jsonb"),table('rate_curve_tenors',own,"'{}'::jsonb"),
        f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{secondary}');",
        f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(x)||jsonb_build_object('id',gen_random_uuid(),'definition_revision_id','{secondary}','source_event_id','synthetic_inventory_'||x.source_event_id,'value',x.value+CASE WHEN x.role='parameter' THEN 0.04 ELSE 1.2 END))).* FROM market_data.observations x JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(x.catalog_id,x.definition_revision_id,x.series_id) WHERE(x.catalog_id,x.definition_revision_id)=('{catalog}','{primary}') AND(s.contract_id={far} OR x.role='parameter');"]
    manifest={**manifest,'physicalSecondaryRevision':secondary}
    return reference,market+'\nBEGIN;\n'+'\n'.join(clone)+'\nCOMMIT;',security,manifest


def checks(query,manifest):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer'];secondary=manifest['physicalSecondaryRevision']
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest,issuer))
    query("INSERT INTO security.role_permissions SELECT role_id,p.code FROM security.role_permissions r CROSS JOIN security.permissions p WHERE r.permission='AmendTrade' AND p.code IN('ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings') ON CONFLICT DO NOTHING;")
    query("INSERT INTO security.role_permissions SELECT role_id,p.code FROM security.role_permissions r CROSS JOIN security.permissions p WHERE r.permission='WriteMarketDefinitions' AND p.code IN('ReadTrades','ReadGasOwnedHoldings') ON CONFLICT DO NOTHING;")
    query("INSERT INTO security.role_permissions SELECT role_id,'RunScenario' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,profile='trader'):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('{issuer}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','2026-10-16',NULL);")
    publication=f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}')"
    reference=f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    query(f"BEGIN;INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';"+
        discount_default(catalog,str(uuid4()),secondary,reference,'usd','usd_rates',publication,'2026-10-01T00:00:00Z')+'COMMIT;')
    book=query(f"SELECT id FROM trading.books WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    entity=query(f"SELECT id FROM trading.legal_entities WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    party=query(f"SELECT id FROM trading.counterparties WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    contract,expiry=query(f"SELECT id::text||'|'||to_char(expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id) WHERE(c.catalog_id,c.revision_id,m.curve_code)=('{catalog}','{manifest['revisionId']}','nymex_ng') ORDER BY expiry_at LIMIT 1;").split('|')
    mark=query(f"SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{secondary}','synthetic_inventory');")
    delivery=configure(query,manifest,call,capture,curve='nymex_ng',contract=contract,book=book,entity=entity,entity_revision=1,mark_contract=mark,
        account_code='synthetic_two_source_delivery',account_effective_at='2026-10-01T00:00:00Z',as_of='2026-10-16T16:00:00Z',
        delivery_from='2026-11-01T00:00:00Z',delivery_to='2026-12-01T00:00:00Z',cash_due='2026-12-02T12:00:00Z',carrying='currency_curve_v1',
        policy_from='2026-10-01T00:00:00Z',policy_until='2027-01-01T00:00:00Z',mark_definition_revision_id=secondary,mark_curve='synthetic_inventory')
    margin=dict(id=str(uuid4()),definitionRevisionId=manifest['revisionId'],contractId=contract,settlementCurve='nymex_ng',referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],currency='usd',
        fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',opening='include_opening_instant',increment='0.01',tie='to_even',stage='payment_total',roundingScope='held_binding_difference_payment_total_v1',
        implementation='dated_futures_margin_policy_v2',delivery=dict(id=delivery['profile']['id'],revision=1),occurrences=[dict(id=str(uuid4()),at=expiry,dueAt='2026-11-02T12:00:00Z',phase='final')])
    call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf='2026-10-16T16:00:00Z',policy=margin))});",'editor')
    booking=dict(legalEntityId=entity,legalEntityRevision=1,counterpartyId=party,counterpartyRevision=1)
    booked=json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','nymex_ng','{contract}','2026-10-16T14:00:00Z',NULL,2,3,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(booking)})::text;"))
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(dict(operationId=str(uuid4()),tradeId=booked['tradeId'],bindingId=booked['bindingId'],expectedRevision=1,policyId=margin['id'],policyRevision=1,contextId=capture(),asOf='2026-10-16T16:00:00Z',adoption='native_execution_inception_v1'))});")
    sql_session(query,manifest,'synthetic_physical_economic','source_business_days','16:00',['nymex_ng'],[],effective=('2026-10-01','2027-01-01'))
    directory=Path(query('SHOW unix_socket_directories;')).resolve();assert directory.name.startswith('oetrm-ref-')
    pins=json.loads(query(f"SELECT jsonb_object_agg(definition_revision_id,pins) FROM(SELECT definition_revision_id,jsonb_agg(id ORDER BY id)pins FROM market_data.observations WHERE catalog_id='{catalog}' AND observed_at='2026-10-16T16:00:00Z' AND(role='parameter' OR definition_revision_id='{secondary}' OR series_id IN(SELECT id FROM market_data.series WHERE(catalog_id,revision_id,contract_id)=('{catalog}','{manifest['revisionId']}','{contract}'))) GROUP BY definition_revision_id)s;"))
    def mark_price(revision,contract):return query(f"SELECT o.value FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) WHERE(o.catalog_id,o.definition_revision_id,s.contract_id,o.observed_at)=('{catalog}','{revision}','{contract}','2026-10-16T16:00:00Z');")
    packet=directory/'physical-value.json';packet.write_text(json.dumps(dict(manifest=manifest,host=str(directory),contextId=capture(),at='2026-10-16T16:00:00Z',pins=pins,bookId=book,tradeId=booked['tradeId'],bindingId=booked['bindingId'],contractId=contract,markContractId=mark,secondaryRevision=secondary,futurePrice=mark_price(manifest['revisionId'],contract),inventoryPrice=mark_price(secondary,mark))));packet.chmod(0o600)
    login='synthetic_physical_value_worker';provision_worker(query,manifest,login)
    role=str(uuid4())
    query(f"INSERT INTO security.roles VALUES('{role}','synthetic_physical_value_gas_reader','Synthetic physical value source reader');INSERT INTO security.role_permissions VALUES('{role}','ReadGasOwnedHoldings');INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false FROM risk.workers WHERE login='{login}';")
    assert query(f"SELECT NOT has_function_privilege('{login}','settlement_data.require_futures_margin_preview(uuid,timestamptz)','EXECUTE');")=='t'
    root=Path(__file__).resolve().parents[1];frozen=directory/'physical-value-binaries';shutil.copytree(root/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0',frozen)
    stamps={p.name:p.read_bytes() for p in frozen.glob('OpenEtrm.*.dll')};dotnet=os.environ.get('DOTNET','dotnet')
    def managed(mode):
        result=subprocess.run([dotnet,str(frozen/'OpenEtrm.Api.Checks.dll'),mode,str(packet)],cwd=root,capture_output=True,text=True,timeout=120)
        if result.returncode:raise RuntimeError(result.stdout+result.stderr)
        print(result.stdout.strip(),flush=True)
    managed('--physical-futures-value')
    receipts=json.loads(packet.with_suffix('.economic-worker.json').read_text());assert len(receipts['jobs'])==2
    connection=directory/'physical-worker-connection';connection.write_text(f'Host={directory};Port=55439;Database=postgres;Username={login};SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=2');connection.chmod(0o600)
    config=directory/'physical-worker.json';config.write_text(json.dumps(dict(connectionFile=str(connection),leaseSeconds=30,pollSeconds=1,timeoutSeconds=60)));config.chmod(0o600)
    counts=query("SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM settlement_data.futures_margin_runs))::text;")
    for saved in receipts['jobs']:
        assert query(f"SELECT risk.needs_gas(j) FROM risk.jobs j WHERE id='{saved['id']}';")=='t'
        environment={key:value for key,value in os.environ.items() if not key.startswith('OPENETRM_')};environment['OPENETRM_RISK_WORKER_CONFIG']=str(config)
        result=subprocess.run([dotnet,str(frozen/'OpenEtrm.Api.dll'),'--risk-worker','once'],cwd=root,env=environment,capture_output=True,text=True,timeout=90)
        if result.returncode:raise RuntimeError(result.stdout+result.stderr)
        actual=json.loads(query(f"SELECT jsonb_build_object('state',state,'error',error_code,'result',result)::text FROM risk.jobs WHERE id='{saved['id']}';"))
        assert actual['state']=='completed' and actual['result']==saved['report'],actual|{'workerDiagnostics':result.stdout+result.stderr}
    managed('--economic-worker-verify')
    assert counts==query("SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM settlement_data.futures_margin_runs))::text;")
    assert all((frozen/name).read_bytes()==value for name,value in stamps.items())
    print('PASS actual full physical historical/Monte Carlo workers: both exact native market sources replay with conditional Gas authority and no financial writes',flush=True)


if __name__=='__main__':
    build=subprocess.run([os.environ.get('DOTNET','dotnet'),'build','tests/OpenEtrm.Api.Checks'],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=120)
    if build.returncode:raise RuntimeError(build.stdout+build.stderr)
    main(checks=checks,fixture_factory=fixture)
