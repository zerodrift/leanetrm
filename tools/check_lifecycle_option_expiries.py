"""Native shared option expiry on explicit synthetic historical series and facts."""
from decimal import Decimal
import json
import os
from pathlib import Path
import subprocess
from uuid import uuid4

from check_reference_editing import main
from check_lifecycle_option_scheduling import fixture as task_fixture
from lifecycle_option_client import LifecycleOptionClient
from preview_fixture import catalogue_fixture

CASES=(('call_itm','call','2','2','exercised','2'),('call_atm','call','3','2','exercised','2'),
       ('call_otm','call','4','2','abandoned',None),('put_itm','put','4','2','exercised','-2'),
       ('put_atm','put','3','2','abandoned',None),('put_otm','put','2','2','abandoned',None),
       ('short_call','call','2','-2','exercised','-2'))


def upgrade_checks(query,migration):
    if migration.name!='0292_option_instruction_review.sql':return
    from assistant_context import check_guard
    for signature in ('lifecycle.option_proposal_base(uuid,jsonb)','lifecycle.execute_option_action_pre_schedule_scope(uuid,jsonb)'):
        changes=[(change,signature) for change in (
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",f"ALTER FUNCTION {signature} STRICT;",
            f"ALTER FUNCTION {signature} LEAKPROOF;",f"ALTER FUNCTION {signature} PARALLEL SAFE;",
            f"ALTER FUNCTION {signature} STABLE;",f"ALTER FUNCTION {signature} SECURITY INVOKER;",
            f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
            f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime;",
            f"REVOKE EXECUTE ON FUNCTION {signature} FROM openetrm_security_owner;",
            f"UPDATE pg_proc SET provariadic='uuid'::regtype WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET proargmodes=ARRAY['i','i']::\"char\"[] WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET proargnames=ARRAY['changed','body'] WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET prorettype='text'::regtype WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET prolang=(SELECT oid FROM pg_language WHERE lanname='sql') WHERE oid='{signature}'::regprocedure;",
            f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;")]
        original=query(f"SELECT oid::text||'|'||proowner::text||'|'||proacl::text FROM pg_proc WHERE oid='{signature}'::regprocedure;")
        check_guard(query,migration.read_text(),migration.name[:4],'option_instruction_review_upgrade',changes)
        from assistant_context import migration_body
        query('BEGIN;'+migration_body(migration.read_text())+f"DO $$BEGIN IF(SELECT oid::text||'|'||proowner::text||'|'||proacl::text FROM pg_proc WHERE oid='{signature}'::regprocedure) IS DISTINCT FROM '{original}' THEN RAISE EXCEPTION 'Instruction upgrade changed the retained helper identity';END IF;END$$;ROLLBACK;")


def fixture(root):
    reference,market,security,manifest=task_fixture(root)
    catalog,revision,rcat,rrev=(manifest[k] for k in ('catalogId','revisionId','referenceCatalogId','referenceRevisionId'))
    rows=[];contracts=[]
    for n,(code,right,strike,*_) in enumerate(CASES):
        if code in ('call_atm','short_call'):continue
        series='synthetic_expiry_'+code;contract='90900000-0000-0000-0000-'+str(n+1).zfill(12);price='90a00000-0000-0000-0000-'+str(n+1).zfill(12)
        rows.append(f"INSERT INTO ref_data.futures_option_series VALUES('{rcat}','{rrev}','{series}','nymex_on','nymex_ng_20261101','{right}',{strike},'synthetic','2026-10-27',ref_data.close_instant('{rcat}','{rrev}','option_expiry','nymex_on','2026-10-27'),NULL,'fixture');")
        contracts.append(f"""INSERT INTO market_data.contracts SELECT(jsonb_populate_record(NULL::market_data.contracts,to_jsonb(c)||jsonb_build_object('id','{contract}','futures_option_series_code','{series}'))).* FROM market_data.contracts c WHERE(c.catalog_id,c.revision_id,c.id)=('{catalog}','{revision}','90800000-0000-0000-0000-000000000001');
INSERT INTO market_data.series SELECT(jsonb_populate_record(NULL::market_data.series,to_jsonb(s)||jsonb_build_object('id','{price}','code','{series}_price','contract_id','{contract}'))).* FROM market_data.series s WHERE(s.catalog_id,s.revision_id,s.id)=('{catalog}','{revision}','90800000-0000-0000-0000-000000000002');
INSERT INTO market_data.curve_members SELECT '{catalog}','{revision}','nymex_on','{contract}',max(ordinal)+1 FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{revision}','nymex_on');""")
    rows[:0]=[f"""INSERT INTO ref_data.futures_option_products SELECT(jsonb_populate_record(NULL::ref_data.futures_option_products,
 to_jsonb(p)||jsonb_build_object('code','synthetic_instruction_option','name','Synthetic instruction right','definition_state','synthetic','contrary_instructions_allowed',true))).*
 FROM ref_data.futures_option_products p WHERE(p.catalog_id,p.revision_id,p.code)=('{rcat}','{rrev}','nymex_on');
INSERT INTO ref_data.trading_close_times SELECT(jsonb_populate_record(NULL::ref_data.trading_close_times,
 to_jsonb(t)||jsonb_build_object('code','synthetic_instruction_expiry','option_product_code','synthetic_instruction_option'))).*
 FROM ref_data.trading_close_times t WHERE(t.catalog_id,t.revision_id,t.option_product_code)=('{rcat}','{rrev}','nymex_on');"""]
    for n,(code,strike,cutoff) in enumerate((('open','3',"'2027-01-25T18:00:00Z'"),('unknown','3.5','NULL'),('closed','4',"'2026-10-15T12:00:00Z'"))):
        series='synthetic_instruction_'+code;contract='90b00000-0000-0000-0000-'+str(n+1).zfill(12);price='90c00000-0000-0000-0000-'+str(n+1).zfill(12)
        rows.append(f"INSERT INTO ref_data.futures_option_series VALUES('{rcat}','{rrev}','{series}','synthetic_instruction_option','nymex_ng_20270201','call',{strike},'synthetic','2027-01-26',ref_data.close_instant('{rcat}','{rrev}','option_expiry','synthetic_instruction_option','2027-01-26'),{cutoff},'fixture');")
        contracts.append(f"""INSERT INTO market_data.contracts SELECT(jsonb_populate_record(NULL::market_data.contracts,to_jsonb(c)||jsonb_build_object(
 'id','{contract}','futures_option_series_code','{series}','delivery_from','2027-02-01','delivery_to','2027-03-01',
 'expiry_at',ref_data.close_instant('{rcat}','{rrev}','option_expiry','synthetic_instruction_option','2027-01-26')))).*
 FROM market_data.contracts c WHERE(c.catalog_id,c.revision_id,c.id)=('{catalog}','{revision}','90800000-0000-0000-0000-000000000001');
INSERT INTO market_data.series SELECT(jsonb_populate_record(NULL::market_data.series,to_jsonb(s)||jsonb_build_object('id','{price}','code','{series}_price','contract_id','{contract}'))).*
 FROM market_data.series s WHERE(s.catalog_id,s.revision_id,s.id)=('{catalog}','{revision}','90800000-0000-0000-0000-000000000002');
INSERT INTO market_data.curve_members SELECT '{catalog}','{revision}','nymex_on','{contract}',max(ordinal)+1 FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{revision}','nymex_on');""")
    for marker,extra in [("UPDATE ref_data.revisions SET state='sealed'",rows),("UPDATE market_data.revisions SET state='sealed'",contracts)]:
        assert market.count(marker)==1;market=market.replace(marker,'\n'.join(extra)+'\n'+marker)
    return reference,market,security,manifest


def checks(query,manifest):
    catalog=manifest['catalogId'];issuer=manifest['identityIssuer']
    query(f"SELECT security.initialize_operating_policies('{manifest['tenantId']}','{manifest['workspaceId']}');")
    query(catalogue_fixture(manifest,issuer))
    query("INSERT INTO security.role_permissions SELECT role_id,p FROM security.role_permissions CROSS JOIN unnest(ARRAY['ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings'])p WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    query("INSERT INTO security.role_permissions SELECT role_id,p FROM security.role_permissions CROSS JOIN unnest(ARRAY['ReadTrades','ReadGasOwnedHoldings'])p WHERE permission='WriteMarketDefinitions' ON CONFLICT DO NOTHING;")
    def lit(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,profile='trader'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{issuer}','{profile}','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    class Settings:
        def call(self,path,body):
            name={'lifecycle/policies/save':'save_policy','lifecycle/options/configure':'save_option_profile'}[path]
            return json.loads(call(f"SELECT lifecycle.{name}('{catalog}',{lit(body)})::text;"))
    enrolled=LifecycleOptionClient(Settings(),query,manifest,issuer=issuer,context={},as_of='2026-10-16T16:00:00Z',business_date='2026-10-16',
        from_at='2026-10-01T00:00:00Z',until_at='2027-05-01T00:00:00Z',mark_curve='nymex_ng',carrying='currency_curve_v1')
    book=query(f"SELECT id FROM trading.books WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    parties={}
    for role,table in [('legalEntity','legal_entities'),('counterparty','counterparties')]:
        identifier,revision=query(f"SELECT id::text||'|'||revision::text FROM trading.{table} WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;").split('|')
        parties[role+'Id'],parties[role+'Revision']=identifier,int(revision)
    rights=[]
    for code,right,strike,lots,outcome,future in CASES:
        booked=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','{('synthetic_scheduled_call' if code=='call_atm' else 'synthetic_expiry_call_itm' if code=='short_call' else 'synthetic_expiry_'+code)}',{lots},0.1,'2026-10-19','premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{lit(parties)})::text;"))
        enrolled.enroll(booked['tradeId']);rights.append((booked,outcome,future,strike))
    at=query(f"SELECT to_char(expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM ref_data.futures_option_series WHERE(catalog_id,revision_id,code)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','synthetic_expiry_call_itm');")
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','{at[:10]}',NULL);")
    def request(booked,context):return dict(operationId=str(uuid4()),context=dict(contextId=context,asOf=at,businessDate=at[:10]),target=enrolled.targets[booked['tradeId']],action='option.expiry',
        policyId=enrolled.policies['option.expiry'],policyRevision=1,automatic=False,previewHash='0'*64,selection=dict(evidenceId=booked['tradeId'],lots=None,expectedEventVersion=0,profileRevision=1))
    before=capture();missing=request(rights[0][0],before)
    try:call(f"SELECT lifecycle.option_proposal('{catalog}',{lit(missing)});")
    except RuntimeError as error:assert 'option_expiry_settlement_missing' in str(error),str(error)
    else:raise AssertionError('Expiry consumed a fixing absent from the exact source.')
    source=query(f"SELECT o.id FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(s.catalog_id,s.revision_id,s.contract_id) WHERE(o.catalog_id,o.definition_revision_id,c.futures_series_code)=('{catalog}','{manifest['revisionId']}','nymex_ng_20261101') ORDER BY o.observed_at LIMIT 1;")
    official=str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT catalog_id,'{official}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,'synthetic_option_expiry_rules','1','daily_settlement','final','{at}',clock_timestamp(),3,NULL FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{source}');")
    for booked,outcome,future,strike in rights:
        body=request(booked,capture());proposal=json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{lit(body)})::text;"))
        assert proposal['details']['decision']==outcome and Decimal(str(proposal['details']['strike']))==Decimal(strike)
        body['previewHash']=proposal['hash'];result=json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit(body)})::text;"))
        assert result['outcome']==outcome and Decimal(result['remainingLots'])==0
        assert json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit(body)})::text;"))==result
        acknowledged=str(uuid4());ack=json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit({**body,'operationId':acknowledged})})::text;"))
        assert ack=={**result,'operationId':acknowledged,'acknowledgedOperationId':result['operationId'],'semanticAcknowledgement':True}
        stored=query(f"SELECT coalesce((SELECT trim_scale(r.lots)::text||'|'||trim_scale(r.execution_price)::text FROM trading.trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision)=(e.catalog_id,e.futures_trade_id,1)),'none')||'|'||(e.settlement_observation_id='{official}')::text FROM trading.futures_option_events e WHERE(e.catalog_id,e.trade_id)=('{catalog}','{booked['tradeId']}');")
        assert stored==('none' if future is None else future+'|'+strike)+'|true',stored
    assert query(f"SELECT count(*) FROM trading.futures_option_events WHERE catalog_id='{catalog}';")=='7'
    assert query(f"SELECT count(*) FROM settlement_data.futures_margin_adoptions WHERE catalog_id='{catalog}';")=='4'
    assert query(f"SELECT count(*) FROM trading.gas_owned_lots WHERE catalog_id='{catalog}';")=='0'
    print('PASS actual native option expiry rule matrix: call/put ITM ATM OTM, short assignment, exact official source, strike-basis future/adoption, first outcomes and new-command acknowledgements, no inferred title',flush=True)

    unprocessed=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','synthetic_expiry_call_itm',1,0.1,'2026-10-19','premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{lit(parties)})::text;"))
    host=query('SHOW unix_socket_directories;');packet=Path(host)/'unprocessed-option-expiry.json'
    packet.write_text(json.dumps(dict(manifest=manifest,host=host,tradeId=unprocessed['tradeId'],bookId=book,contextId=capture(),at=at)));packet.chmod(0o600)
    checked=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--lifecycle-option-expiry-read',str(packet)],
        cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if checked.returncode:raise RuntimeError(checked.stdout+checked.stderr)
    print(checked.stdout.strip(),flush=True)
    assert query(f"SELECT count(*) FROM trading.futures_option_events WHERE(catalog_id,trade_id)=('{catalog}','{unprocessed['tradeId']}');")=='0'

    # These rights have their own declared instruction capability and cutoffs.
    # The open cutoff is genuinely in the future; the writer's wall-clock fence
    # stays active, and every reference was sealed before any trade was booked.
    def instruction_trade(code,lots=2):
        booked=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','synthetic_instruction_{code}',{lots},0.1,'2026-10-19','premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{lit(parties)})::text;"))
        enrolled.enroll(booked['tradeId']);return booked
    def instruction(booked,action,version=0,at='2026-10-16T16:00:00Z'):
        selected=call(f"SELECT security.capture_date_context('{catalog}','2026-10-16',NULL);")
        return dict(operationId=str(uuid4()),context=dict(contextId=selected,asOf=at,businessDate='2026-10-16'),target=enrolled.targets[booked['tradeId']],
            action=action,policyId=enrolled.policies[action],policyRevision=1,automatic=False,previewHash='0'*64,
            selection=dict(evidenceId=str(uuid4()),lots=None,expectedEventVersion=version,profileRevision=1))
    def refused(body,code):
        try:call(f"SELECT lifecycle.option_proposal('{catalog}',{lit(body)});")
        except RuntimeError as error:assert code in str(error),str(error)
        else:raise AssertionError('An unsupported instruction was admitted: '+code)
    positive=instruction_trade('open');body=instruction(positive,'option.abandon_instruction')
    proposal=json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{lit(body)})::text;"));body['previewHash']=proposal['hash']
    result=json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit(body)})::text;"))
    assert result['kind']=='abandon_instruction' and Decimal(result['remainingLots'])==2 and result['futuresTradeId'] is None
    assert json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit(body)})::text;"))==result
    acknowledgement=str(uuid4());ack=json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit({**body,'operationId':acknowledgement})})::text;"))
    assert ack=={**result,'operationId':acknowledgement,'acknowledgedOperationId':result['operationId'],'semanticAcknowledgement':True}
    same=instruction(positive,'option.abandon_instruction',1)
    same['previewHash']=json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{lit(same)})::text;"))['hash']
    existing=json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit(same)})::text;"))
    assert existing=={**result,'operationId':same['operationId'],'acknowledgedOperationId':result['operationId'],'semanticAcknowledgement':True}
    assert json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit(same)})::text;"))==existing
    historical=instruction(positive,'option.exercise_instruction')
    historical['context']=body['context']
    assert json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{lit(historical)})::text;"))['details']['eventVersion']==0
    not_yet_effective=instruction(positive,'option.exercise_instruction',at='2026-10-16T15:00:00Z')
    assert json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{lit(not_yet_effective)})::text;"))['details']['eventVersion']==0
    refused(instruction(positive,'option.exercise_instruction',1),'option_instruction_conflict')
    refused(instruction(positive,'option.abandon_instruction',1,at='2026-10-16T17:00:00Z'),'option_instruction_conflict')
    refused(instruction(instruction_trade('unknown'),'option.abandon_instruction'),'option_instruction_closed')
    refused(instruction(instruction_trade('closed'),'option.abandon_instruction'),'option_instruction_closed')
    refused(instruction(instruction_trade('open',-2),'option.exercise_instruction'),'option_instruction_closed')
    refused(instruction(instruction_trade('open'),'option.exercise_instruction',at='2026-10-16T13:00:00Z'),'lifecycle_option_trade')
    assert query(f"SELECT count(*) FROM trading.futures_option_events WHERE catalog_id='{catalog}';")=='8'
    assert query(f"SELECT count(*) FROM settlement_data.futures_margin_adoptions WHERE catalog_id='{catalog}';")=='4'
    print('PASS explicit synthetic instruction capability: real open cutoff, immutable references, reviewed abandon with retry and acknowledgement, contrary conflict, unknown/closed cutoff, short side and execution-window refusals; no linked future or title',flush=True)



if __name__=='__main__':main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
