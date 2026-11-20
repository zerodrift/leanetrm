"""Explicit funded cash-option value on an owned synthetic native source."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from decimal import Decimal
from uuid import uuid4

from check_reference_editing import main
from check_lifecycle_actions import fixture as source_fixture
from check_lifecycle_actions import upgrade_checks as source_upgrades
from preview_fixture import catalogue_fixture
from economic_worker_fixture import provision,run_saved

history_signature='lifecycle.require_option_history_read(uuid,uuid,uuid,integer,uuid,timestamptz,date,timestamptz,timestamptz)'
history_identity=None

def upgrade_checks(query,migration):
    global history_identity
    source_upgrades(query,migration)
    if migration.name=='0310_cash_option_read_admission.sql':
        from check_fact_anchor_upgrade import run_checks
        from assistant_context import check_guard
        signature=history_signature
        history_identity=json.loads(query(f"SELECT(to_jsonb(p)-'prosrc')::text FROM pg_proc p WHERE oid='{signature}'::regprocedure;"))
        run_checks(query,migration,number='0310',guard='cash_option_read_upgrade',signatures=(signature,))
        check_guard(query,migration.read_text(),'0310','cash_option_read_upgrade',[(
            f"UPDATE pg_proc SET {change} WHERE oid='{signature}'::regprocedure;",signature) for change in (
                "proargnames=ARRAY['changed']::text[]","procost=99","prorows=1","pronargdefaults=1","provariadic=23",
                "proargmodes=array_fill('i'::\"char\",ARRAY[9])","proallargtypes=ARRAY[23]::oid[]",
                "probin='synthetic_drift'","prosupport='length(text)'::regprocedure")])


def fixture(root,*,solved=False):
    reference,market,security,manifest=source_fixture(root)
    catalog,definition=manifest['catalogId'],manifest['revisionId'];ref_catalog,ref_revision=manifest['referenceCatalogId'],manifest['referenceRevisionId']
    refs=f"'{ref_catalog}','{ref_revision}'";contract,series=str(uuid4()),str(uuid4());strike='3.10' if solved else '2'
    # This saved synthetic reference version explicitly qualifies the daily
    # source needed by its option consumer; trader marks are not substituted.
    execution=f"INSERT INTO ref_data.futures_execution_policies VALUES({refs},'ice_cfb_"
    curve=f"INSERT INTO market_data.curves VALUES('{catalog}','{definition}','ice_cfb',"
    lines=market.splitlines();assert sum(line.startswith(execution) for line in lines)==5 and sum(line.startswith(curve) for line in lines)==1
    market='\n'.join(line.replace("'trader_mark'","'daily_settlement'") if line.startswith((execution,curve)) else line for line in lines)
    calendar=f"INSERT INTO ref_data.calendars VALUES({refs},'source_business_days','test_utc',96,'2026-10-13','2026-10-17');"
    assert market.count(calendar)==1
    market=market.replace(calendar,calendar.replace("'2026-10-17'","'2026-10-21'"))
    seal=f"UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{ref_catalog}' AND id='{ref_revision}';"
    assert market.count(seal)==1
    terms=(f"INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) VALUES({refs},'synthetic_cash_option','synthetic','synthetic:cash-option-contract','2026-10-01T00:00:00Z','Original synthetic option contract','not_reviewed');"
        f"INSERT INTO ref_data.futures_option_products SELECT(jsonb_populate_record(NULL::ref_data.futures_option_products,to_jsonb(p)||jsonb_build_object("
        "'code','synthetic_cash_option','name','Synthetic option on a cash future','definition_state','synthetic','exchange_code','ice_futures_us','underlying_product_code','ice_cfb',"
        f"'multiplier',2500,'strike_interval_source_code','synthetic_cash_option'))).* FROM ref_data.futures_option_products p WHERE(catalog_id,revision_id,code)=({refs},'nymex_on');"
        f"INSERT INTO ref_data.trading_close_times SELECT(jsonb_populate_record(NULL::ref_data.trading_close_times,to_jsonb(t)||jsonb_build_object('code','synthetic_cash_option_expiry',"
        f"'option_product_code','synthetic_cash_option','source_code','synthetic_cash_option'))).* FROM ref_data.trading_close_times t WHERE(catalog_id,revision_id,milestone,option_product_code)=({refs},'option_expiry','nymex_on');"
        f"INSERT INTO ref_data.futures_option_series VALUES({refs},'synthetic_cash_call','synthetic_cash_option','ice_cfb_20261101','call',{strike},'synthetic','2026-10-27',"
        f"ref_data.close_instant({refs},'option_expiry','synthetic_cash_option','2026-10-27'),NULL,'synthetic_cash_option');")
    market=market.replace(seal,terms+seal)
    seal=f"UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{catalog}' AND id='{definition}';"
    assert market.count(seal)==1
    rows=(f"INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||jsonb_build_object('code','synthetic_cash_option','name','Synthetic cash option settlement',"
        f"'quote_code','nymex_on_quote','direct_role','daily_settlement'))).* FROM market_data.curves c WHERE(catalog_id,revision_id,code)=('{catalog}','{definition}','ice_cfb');"
        f"INSERT INTO market_data.contracts SELECT '{catalog}','{definition}','{contract}',{refs},'future_option',c.delivery_from,c.delivery_to,s.expiry_at,NULL,s.code "
        f"FROM ref_data.futures_option_series s JOIN market_data.contracts c ON(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)=(s.catalog_id,s.revision_id,s.underlying_series_code) "
        f"WHERE(s.catalog_id,s.revision_id,s.code,c.catalog_id,c.revision_id)=({refs},'synthetic_cash_call','{catalog}','{definition}');"
        f"INSERT INTO market_data.curve_members VALUES('{catalog}','{definition}','synthetic_cash_option','{contract}',0);"
        f"INSERT INTO market_data.series VALUES('{catalog}','{definition}','{series}','synthetic_cash_call',{refs},'{contract}','nymex_on_quote',NULL);")
    observations=(f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object('id',gen_random_uuid(),"
        f"'source_event_id','synthetic_cash_daily_'||o.source_event_id,'role','daily_settlement'))).* FROM market_data.observations o JOIN market_data.series s "
        f"ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(s.catalog_id,s.revision_id,s.contract_id) "
        f"WHERE(o.catalog_id,o.definition_revision_id,c.futures_series_code)=('{catalog}','{definition}','ice_cfb_20261101');"
        f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object('id',gen_random_uuid(),'series_id','{series}',"
        f"'source_event_id','synthetic_cash_option_'||o.source_event_id,'role','daily_settlement','value',greatest(o.value-{strike},0)+{'.2' if solved else '0'}))).* FROM market_data.observations o JOIN market_data.series s "
        f"ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(s.catalog_id,s.revision_id,s.contract_id) "
        f"WHERE(o.catalog_id,o.definition_revision_id,c.futures_series_code,o.role)=('{catalog}','{definition}','ice_cfb_20261101','trader_mark');")
    return reference,market.replace(seal,rows+seal)+'\nBEGIN;\n'+observations+'\nCOMMIT;',security,{**manifest,'cashOptionContract':contract}


def read_admission(query,manifest,packet,login,foreign):
    selected=json.loads(packet.read_text());receipts=json.loads(packet.with_suffix('.economic-worker.json').read_text())
    catalog=manifest['catalogId'];context=selected['contextId'];at=selected['at'];day=at[:10]
    assert query(f"SELECT has_function_privilege('{login}','{history_signature}','EXECUTE') AND "
        f"NOT has_function_privilege('{login}','lifecycle.require_option_origin(uuid,uuid,integer,timestamptz)','EXECUTE') AND "
        f"NOT has_function_privilege('{login}','security.has_permission(text)','EXECUTE');")=='t'
    assert query(f"SELECT NOT security.principal_has_permission(principal_id,tenant_id,workspace_id,'ReadGasOwnedHoldings',clock_timestamp()) "
        f"FROM risk.workers WHERE login='{login}';")=='t'
    def guard(trade=selected['tradeId'],binding=selected['bindingId'],revision=1,source='until_at',history='until_at',selected_context=context,selected_at=at):
        return f"lifecycle.require_option_history_read('{catalog}','{trade}','{binding}',{revision},'{selected_context}','{selected_at}','{day}',{source},{history})"
    def denied(expression):
        return f"BEGIN PERFORM {expression};RAISE EXCEPTION 'synthetic_cash_read_escaped';EXCEPTION WHEN insufficient_privilege THEN NULL;END;"
    query(f"SET SESSION AUTHORIZATION {login};BEGIN;DO $probe$DECLARE claimed jsonb;until_at timestamptz;BEGIN "+denied(guard())+
        f"claimed=risk.claim(30,'{receipts['engineVersion']}');IF claimed IS NULL THEN RAISE EXCEPTION 'synthetic_cash_job_missing';END IF;"
        "PERFORM risk.bind((claimed->>'jobId')::uuid,(claimed->>'leaseToken')::uuid);"
        f"until_at=security.admit_trading_business_source('{catalog}','{context}','{at}','{day}');PERFORM {guard()};"+
        denied(guard(trade=foreign['tradeId'],binding=foreign['optionInstrumentId']))+denied(guard(revision=2))+
        denied(guard(source="clock_timestamp()-interval '1 second'"))+denied(guard(history="clock_timestamp()-interval '1 second'"))+
        denied(guard(selected_context=str(uuid4())))+denied(guard(selected_at='2026-10-16T16:01:00Z'))+
        "END$probe$;ROLLBACK;")
    print('PASS cash option history admission: exact selected read-only worker scope, original source/history cutoffs, unchanged permission/origin ACLs and no Gas grant',flush=True)


def checks(query,manifest,*,economic=False,native_child=False,solved=False,margin_v2=False,browser=None):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'));issuer=manifest['identityIssuer']
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest,issuer))
    query("INSERT INTO security.role_permissions SELECT role_id,'RunScenario' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    query("INSERT INTO security.role_permissions SELECT role_id,'ReadTrades' FROM security.role_permissions WHERE permission='WriteMarketDefinitions' ON CONFLICT DO NOTHING;")
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,profile='trader'):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('{issuer}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.resolve_operation_context('{catalog}','2026-10-16',NULL);")
    book=query(f"SELECT id FROM trading.books WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    entity=query(f"SELECT id FROM trading.legal_entities WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    party=query(f"SELECT id FROM trading.counterparties WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    booking=dict(legalEntityId=entity,legalEntityRevision=1,counterpartyId=party,counterpartyRevision=1)
    option=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','synthetic_cash_call',2,0.1,'2026-10-30',"
        f"'premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{literal(booking)})::text;"))
    foreign=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','synthetic_cash_call',1,0.1,'2026-10-30',"
        f"'premium_paid_up_front','2026-10-16T14:01:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{literal(booking)})::text;"))
    contract,expiry=query(f"SELECT id||'|'||to_char(expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM market_data.contracts WHERE(catalog_id,revision_id,futures_series_code)=('{catalog}','{manifest['revisionId']}','ice_cfb_20261101');").split('|')
    margin=dict(id=str(uuid4()),definitionRevisionId=manifest['revisionId'],contractId=contract,settlementCurve='ice_cfb',referenceCatalogId=manifest['referenceCatalogId'],
        referenceRevisionId=manifest['referenceRevisionId'],currency='usd',fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',opening='include_opening_instant',
        increment='0.01',tie='to_even',stage='payment_total',roundingScope='held_binding_difference_payment_total_v1',implementation='dated_futures_margin_policy_v1',
        occurrences=[dict(id=str(uuid4()),at='2026-10-16T16:45:00Z',dueAt='2026-10-20T12:00:00Z',phase='daily'),dict(id=str(uuid4()),at=expiry,dueAt='2026-11-02T12:00:00Z',phase='final')])
    if margin_v2:margin.update(implementation='dated_futures_margin_policy_v2',delivery=None)
    call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf='2026-10-16T16:00:00Z',policy=margin))});",'editor')
    action=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='option.expiry',mode='review',fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(action)});")
    profile=dict(operationId=str(uuid4()),tradeId=option['tradeId'],action='option.expiry',expectedRevision=0,policyId=action['policyId'],policyRevision=1,
        marginPolicyId=margin['id'],marginPolicyRevision=1,futureMarkCurve='ice_cfb',expiryCurve='ice_cfb')
    call(f"SELECT lifecycle.save_option_profile('{catalog}',{literal(profile)});")
    child=None
    if native_child:
        exercise={**action,'operationId':str(uuid4()),'policyId':str(uuid4()),'action':'option.exercise','mode':'manual'}
        call(f"SELECT lifecycle.save_policy('{catalog}',{literal(exercise)});")
        call(f"SELECT lifecycle.save_option_profile('{catalog}',{literal({**profile,'operationId':str(uuid4()),'action':'option.exercise','policyId':exercise['policyId'],'expiryCurve':None})});")
        request=dict(operationId=str(uuid4()),context=dict(contextId=capture(),asOf='2026-10-16T16:30:00Z',businessDate='2026-10-16'),
            target=dict(handler='listed_option',scopeId=option['tradeId'],bindingId=option['optionInstrumentId']),action='option.exercise',
            policyId=exercise['policyId'],policyRevision=1,automatic=False,previewHash='0'*64,
            selection=dict(evidenceId=str(uuid4()),lots='2',expectedEventVersion=0,profileRevision=1))
        request['previewHash']=json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{literal(request)})::text;"))['hash']
        child=json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{literal(request)})::text;"))
        binding=query(f"SELECT binding_id FROM settlement_data.futures_margin_adoptions WHERE(catalog_id,trade_id)=('{catalog}','{child['futuresTradeId']}');")
        price=Decimal(query(f"SELECT value FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
            f"WHERE(o.catalog_id,o.definition_revision_id,o.observed_at,o.role,s.contract_id)=('{catalog}','{manifest['revisionId']}','2026-10-16T16:00:00Z','daily_settlement','{contract}');"))
        observation=str(uuid4());occurrence=margin['occurrences'][0]
        query(f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object('id','{observation}',"
            f"'source_event_id','synthetic_cash_native_vm','observed_at','{occurrence['at']}','received_at',clock_timestamp()))).* FROM market_data.observations o JOIN market_data.series s "
            f"ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) WHERE(o.catalog_id,o.definition_revision_id,o.observed_at,o.role,s.contract_id)="
            f"('{catalog}','{manifest['revisionId']}','2026-10-16T16:00:00Z','daily_settlement','{contract}');")
        variation=Decimal(5000)*(price-Decimal(2))
        step=dict(occurrenceId=occurrence['id'],previousDeterminationId=None,sourceObservationId=observation,previousBasis='2',exactVariation=str(variation),
            roundedTotal=str(variation),adjustment=str(variation),adjustmentDue=occurrence['dueAt'],noChange=False)
        command=dict(operationId=str(uuid4()),tradeId=child['futuresTradeId'],bindingId=binding,expectedRevision=1,expectedMarginRevision=1,contextId=capture(),
            asOf='2026-10-16T17:00:00Z',throughOccurrence=occurrence['id'],implementation='native_futures_margin_journal_v1',correctionDue=[],previewHash='0'*64)
        call(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(command)},{literal([step])});")
        child={**child,'bindingId':binding,'variation':str(variation),'dueAt':occurrence['dueAt'],'marginAt':occurrence['at']}
    if economic:
        from close_session_fixture import sql_session
        for code,cutoff in [('economic','17:00'),('before','16:29'),('exercise','16:30'),('margin','16:45'),('following','15:00')]:
            sql_session(query,manifest,'synthetic_cash_option_'+code,'source_business_days',cutoff,['ice_cfb','synthetic_cash_option'],[],effective=('2026-10-01','2027-01-01'))
    pins=json.loads(query(f"SELECT jsonb_agg(o.id ORDER BY o.id) FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
        f"WHERE(o.catalog_id,o.definition_revision_id,o.observed_at)=('{catalog}','{manifest['revisionId']}','2026-10-16T16:00:00Z') AND(o.role='parameter' OR o.role='daily_settlement' AND(s.code='synthetic_cash_call' OR s.contract_id='{contract}'));"))
    directory=Path(query('SHOW unix_socket_directories;'));packet=directory/'cash-option-value.json'
    packet.write_text(json.dumps(dict(manifest=manifest,host=str(directory),contextId=capture(),at='2026-10-16T16:00:00Z',bookId=book,tradeId=option['tradeId'],
        bindingId=option['optionInstrumentId'],contractId=contract,optionContractId=manifest['cashOptionContract'],pins=pins,economic=economic,child=child,solved=solved,
        foreign=foreign)));packet.chmod(0o600)
    frozen=directory/'cash-option-binaries';root=Path(__file__).resolve().parents[1];shutil.copytree(root/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0',frozen)
    provision(query,manifest,'synthetic_cash_option_worker',permissions=())
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),str(frozen/'OpenEtrm.Api.Checks.dll'),'--cash-option-value',str(packet)],cwd=root,capture_output=True,text=True,timeout=120)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)
    assert history_identity==json.loads(query(f"SELECT(to_jsonb(p)-'prosrc')::text FROM pg_proc p WHERE oid='{history_signature}'::regprocedure;"))
    read_admission(query,manifest,packet,'synthetic_cash_option_worker',foreign)
    run_saved(query,manifest,packet,frozen,'synthetic_cash_option_worker',permissions=(),expected_jobs=2,
        verify_mode='--economic-worker-verify' if economic else '--physical-option-worker-verify')
    if browser is not None:browser(query,manifest,packet,frozen)


if __name__=='__main__':
    root=Path(__file__).resolve().parents[1]
    build=subprocess.run([os.environ.get('DOTNET','dotnet'),'build','tests/OpenEtrm.Api.Checks','--no-restore','-m:1','-nodeReuse:false'],cwd=root,capture_output=True,text=True,timeout=120)
    if build.returncode:raise RuntimeError(build.stdout+build.stderr)
    arguments=sys.argv[1:];economic='--economic' in arguments;native_child='--native-child' in arguments;solved='--solved' in arguments;margin_v2='--margin-v2' in arguments
    arguments=[arg for arg in arguments if arg not in ('--economic','--native-child','--solved','--margin-v2')]
    assert not native_child or economic,'Native-child qualification requires the explicit economic consumer.'
    assert not(native_child and solved),'The native cash-exercise conservation oracle explicitly selects its intrinsic reference strategy.'
    main(checks=lambda query,manifest:checks(query,manifest,economic=economic,native_child=native_child,solved=solved,margin_v2=margin_v2),
        fixture_factory=lambda root:fixture(root,solved=solved),before_migration=upgrade_checks,arguments=arguments)
