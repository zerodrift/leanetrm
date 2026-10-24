"""Synthetic import checks, called only inside run_preview's owned database."""
from pathlib import Path
import base64
import copy
import hashlib
import json
import subprocess
from uuid import uuid4

ROOT=Path(__file__).resolve().parents[1]


def run_checks(query,manifest,temporary,dotnet,environment):
    catalog=manifest['referenceCatalogId'];tenant=manifest['tenantId'];workspace=manifest['workspaceId']
    actor=str(uuid4());role=str(uuid4());grant=str(uuid4());operation=str(uuid4())
    query("CREATE ROLE preview_reference_importer LOGIN INHERIT;GRANT openetrm_runtime,openetrm_reference_importer TO preview_reference_importer;")
    query(f"""INSERT INTO security.principals VALUES('{actor}','service',true);
INSERT INTO security.identity_bindings VALUES('https://reference.synthetic.invalid','importer','{actor}','service_credential');
INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{actor}','reference_importer','Synthetic reference importer',true,1);
INSERT INTO security.roles(id,code,name) VALUES('{role}','synthetic_reference_importer','Synthetic reference importer');
INSERT INTO security.role_permissions SELECT '{role}',p FROM(VALUES('ReadMarketData'),('WriteReferenceData'),('SealReferenceData'))x(p);
INSERT INTO security.role_grants VALUES('{grant}','{actor}','{role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 day',false);
INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{catalog}','20000000-0000-0000-0000-000000000099','synthetic_import_units');
INSERT INTO ref_data.dimensions VALUES('{catalog}','20000000-0000-0000-0000-000000000099','energy',1,2,-2,0,0,0,0);
INSERT INTO ref_data.units VALUES('{catalog}','20000000-0000-0000-0000-000000000099','mwh','MWh','energy');
INSERT INTO ref_data.commodities VALUES('{catalog}','20000000-0000-0000-0000-000000000099','electricity','Synthetic power','energy');
INSERT INTO ref_data.commodity_units VALUES('{catalog}','20000000-0000-0000-0000-000000000099','electricity','mwh','energy');
INSERT INTO ref_data.measurement_bases VALUES('{catalog}','20000000-0000-0000-0000-000000000099','energy','electricity','energy');
INSERT INTO ref_data.quantity_specifications VALUES('{catalog}','20000000-0000-0000-0000-000000000099','synthetic_power_mwh','electricity','energy','mwh','energy');
UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{catalog}' AND id='20000000-0000-0000-0000-000000000099';""")
    plan_file=temporary/'reference-plan.json'
    subprocess.run([str(dotnet),'run','--project','tests/OpenEtrm.Api.Checks','--','--reference-import-plan',str(plan_file)],cwd=ROOT,env=environment,check=True,timeout=60)
    payload=json.loads(plan_file.read_text())
    def literal(value):return "'"+json.dumps(value,separators=(',',':')).replace("'","''")+"'::jsonb"
    def issue(subject='importer',issuer='https://reference.synthetic.invalid',kind='service_credential',login='preview_reference_importer'):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('{issuer}','{subject}','{kind}','{tenant}','{workspace}',NULL,'{login}',60);")
    def runtime(sql,token=None,login='preview_reference_importer'):return f"SET SESSION AUTHORIZATION {login};BEGIN;SELECT security.use_context('{token or issue(login=login)}');{sql} COMMIT;"
    def call(body=payload,op=operation,cat=catalog):return f"SELECT security.import_reference_capture('{cat}','{op}',{literal(body)})::text;"
    def reject(statement,state='P0001'):
        statement=statement.replace('SELECT security.import_reference_capture','PERFORM security.import_reference_capture').replace(')::text;',');')
        query(runtime(f"DO $$ DECLARE rejected boolean=false;BEGIN BEGIN {statement} EXCEPTION WHEN SQLSTATE '{state}' THEN rejected=true;END;IF NOT rejected THEN RAISE EXCEPTION 'Expected import rejection' USING ERRCODE='XX000';END IF;END $$;"))
    # Exercise the real private command on synthetic data before direct database checks.
    def private_file(name,value):
        path=temporary/name;path.write_text(value);path.chmod(0o600);return str(path)
    for suffix in ['.capture.json','.mapping.json']:(temporary/('reference-plan.json'+suffix)).chmod(0o600)
    auth=private_file('import-auth.connection',f'Host={temporary};Port=55439;Database=postgres;Username=preview_authenticator;SSL Mode=Disable')
    executor=private_file('import-operator.connection',f'Host={temporary};Port=55439;Database=postgres;Username=preview_reference_importer;SSL Mode=Disable')
    config={'authenticatorConnectionFile':auth,'operatorConnectionFile':executor,'issuer':'https://reference.synthetic.invalid','subject':'importer',
            'tenantId':tenant,'workspaceId':workspace,'catalogId':catalog,'operationId':operation,'mapping':json.loads(Path(str(plan_file)+'.mapping.json').read_text())}
    config_file=private_file('import-config.json',json.dumps(config))
    command=[str(dotnet),str(ROOT/'src/OpenEtrm.Api/bin/Debug/net10.0/OpenEtrm.Api.dll'),'--import-reference',config_file,str(plan_file)+'.capture.json']
    inspected=subprocess.run(command[:-2]+['inspect',*command[-2:]],cwd=ROOT,env=environment,text=True,capture_output=True,timeout=60)
    assert inspected.returncode==0 and json.loads(inspected.stdout)['series']==1
    for _ in range(2):
        result=subprocess.run(command,cwd=ROOT,env=environment,text=True,capture_output=True,timeout=60)
        assert result.returncode==0,result.stderr
        assert json.loads(result.stdout)['revisionId']==operation
    # This behavior must fail before the migration/operation exists.
    receipt=json.loads(query(runtime(call())))
    assert receipt['products']==1 and receipt['series']==1 and receipt['state']=='inactive' and receipt['revisionId']==operation
    assert json.loads(query(runtime(call())))==receipt
    assert query(f"SELECT state FROM ref_data.revisions WHERE catalog_id='{catalog}' AND id='{operation}';")=='sealed'
    assert query(f"SELECT count(*) FROM ref_data.futures_products WHERE catalog_id='{catalog}' AND revision_id='{operation}' AND definition_state='placeholder';")=='1'
    assert query(f"SELECT count(*) FROM ref_data.futures_series WHERE catalog_id='{catalog}' AND revision_id='{operation}' AND definition_state='placeholder' AND delivery_from IS NULL;")=='1'
    source=payload['series'][0]
    assert query(runtime(f"SELECT series_code FROM ref_data.imported_series_keys WHERE catalog_id='{catalog}' AND revision_id='{operation}' AND provider='ICE' AND exchange_id='IFED' AND product_id='123456' AND contract_id='Nov40';"))==source['code']
    assert query(f"SELECT first_trading_date::text||'|'||reported_energy_amount::text||'|'||(first_delivery_date IS NULL)::text FROM ref_data.imported_series_keys WHERE catalog_id='{catalog}' AND revision_id='{operation}';")=='2040-10-02|35.125|true'
    assert query("SET SESSION AUTHORIZATION preview_runtime;SELECT count(*) FROM ref_data.reference_import_receipts;")=='0'
    # Ordinary API/runtime callers cannot bypass managed source replay, even with import permissions.
    api=issue(login='preview_runtime')
    for field,value in [('contractId','Forged40'),('firstTradingDate','2040-10-01'),('reportedEnergy','36')]:
        forged=copy.deepcopy(payload);forged['series'][0][field]=value
        sql=call(forged,str(uuid4())).replace('SELECT security.import_reference_capture','PERFORM security.import_reference_capture').replace(')::text;',');')
        query(runtime(f"DO $$ DECLARE rejected boolean=false;BEGIN BEGIN {sql} EXCEPTION WHEN insufficient_privilege THEN rejected=true;END;IF NOT rejected THEN RAISE EXCEPTION 'Runtime bypassed trusted source validator' USING ERRCODE='XX000';END IF;END $$;",issue(login='preview_runtime'),login='preview_runtime'))
    # A historical view must hide all later imported evidence as well as canonical definitions.
    market=manifest['catalogId']
    old= query(runtime(f"SELECT security.capture_date_context('{market}','2020-12-18',NULL);"))
    for table in ['reference_import_receipts','source_documents','imported_product_keys','imported_series_keys']:
        assert query(runtime(f"SELECT security.use_market_context('{market}','{old}');SELECT count(*) FROM ref_data.{table} WHERE catalog_id='{catalog}' AND revision_id='{operation}';"))=='0'
    # Semantic identity uses the entire exact source key, not a trading-symbol search.
    assert query(runtime(f"SELECT count(*) FROM ref_data.imported_series_keys WHERE catalog_id='{catalog}' AND revision_id='{operation}' AND contract_id='nov40';"))=='0'
    for table in ['reference_import_receipts','source_documents','imported_product_keys','imported_series_keys']:
        reject(f"INSERT INTO ref_data.{table} SELECT * FROM ref_data.{table} WHERE false;",'42501')
    changed=copy.deepcopy(payload);changed['products'][0]['name']='Changed synthetic name';reject(call(changed),'40001')
    reject(call(op=str(uuid4())),'40001')
    for mutate in ['hash','source','duplicate','quantity','state']:
        invalid=copy.deepcopy(payload);invalid['packageSha256']=hashlib.sha256(mutate.encode()).hexdigest()
        if mutate=='hash':invalid['documents'][0]['sha256']='0'*64
        if mutate=='source':invalid['series'][0]['productId']='999999'
        if mutate=='duplicate':invalid['series'].append(invalid['series'][0])
        if mutate=='quantity':invalid['series'][0]['quantityCode']='absent'
        if mutate=='state':invalid['state']='synthetic'
        op=str(uuid4());reject(call(invalid,op))
        assert query(f"SELECT count(*) FROM ref_data.revisions WHERE catalog_id='{catalog}' AND id='{op}';")=='0'
    # A visible imported revision cannot expose a hidden later quantity revision.
    later=str(uuid4())
    query(f"BEGIN;INSERT INTO market_data.revisions VALUES('{market}','{later}','{catalog}','{operation}','draft',0);INSERT INTO market_data.revision_periods VALUES('{market}','{later}','2020-01-01','2022-01-01');UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{market}' AND id='{later}';COMMIT;")
    view=query(runtime(f"SELECT security.capture_date_context('{market}','2020-12-18',NULL);"))
    bind=f"SELECT security.use_market_context('{market}','{view}');"
    assert query(runtime(bind+f"SELECT count(*) FROM ref_data.imported_product_keys WHERE catalog_id='{catalog}' AND revision_id='{operation}';"))=='1'
    assert query(runtime(bind+f"SELECT count(*) FROM ref_data.imported_series_keys WHERE catalog_id='{catalog}' AND revision_id='{operation}';"))=='0'
    # Read permission may expire independently while the operator is inside the import.
    read_role=str(uuid4());read_grant=str(uuid4());expired_op=str(uuid4())
    query(f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission='ReadMarketData';INSERT INTO security.roles(id,code,name) VALUES('{read_role}','synthetic_short_read','Synthetic short read');INSERT INTO security.role_permissions VALUES('{read_role}','ReadMarketData');INSERT INTO security.role_grants VALUES('{read_grant}','{actor}','{read_role}','{tenant}','{workspace}',clock_timestamp()-interval '1 minute',clock_timestamp()+interval '1 second',false);")
    query("CREATE SEQUENCE public.synthetic_import_delay_witness;CREATE FUNCTION public.synthetic_import_delay() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$ BEGIN IF NEW.state='sealed' THEN PERFORM nextval('public.synthetic_import_delay_witness');PERFORM pg_sleep(2);END IF;RETURN NEW;END$$;CREATE TRIGGER synthetic_import_delay BEFORE UPDATE ON ref_data.revisions FOR EACH ROW EXECUTE FUNCTION public.synthetic_import_delay();")
    expiry=copy.deepcopy(payload);expiry['licenseReference']='Synthetic expiry check'
    reject(call(expiry,expired_op),'42501')
    assert query("SELECT is_called FROM public.synthetic_import_delay_witness;")=='t'
    assert query(f"SELECT count(*) FROM ref_data.revisions WHERE id='{expired_op}';")=='0'
    query(f"DROP TRIGGER synthetic_import_delay ON ref_data.revisions;DROP FUNCTION public.synthetic_import_delay();DROP SEQUENCE public.synthetic_import_delay_witness;INSERT INTO security.role_permissions VALUES('{role}','ReadMarketData');")
    # Simultaneous identical requests share one immutable operation.
    from concurrent.futures import ThreadPoolExecutor
    with ThreadPoolExecutor(max_workers=2) as pool:
        results=list(pool.map(lambda _:json.loads(query(runtime(call()))),range(2)))
    assert results==[receipt,receipt]
    viewer=issue('viewer','https://preview.invalid','human_interactive')
    reject_sql=f"DO $$ BEGIN PERFORM security.import_reference_capture('{catalog}','{operation}',{literal(payload)});RAISE EXCEPTION 'Viewer imported';EXCEPTION WHEN insufficient_privilege THEN NULL;END $$;"
    query(runtime(reject_sql,viewer))
    query(f"UPDATE security.role_grants SET revoked=true WHERE id='{grant}';")
    # Current authority is required even for a replay of a formerly admitted operation.
    reject(call(),'42501')
    query(f"UPDATE security.role_grants SET revoked=false WHERE id='{grant}';")
    print('PASS reference database import: atomic frozen inactive references, exact native-key lookup, quantities, safe retries, source integrity and current scoped authorization',flush=True)
