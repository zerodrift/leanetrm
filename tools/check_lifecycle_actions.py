"""Explicit action settings and option-to-future enrollment on owned PostgreSQL."""
import json
import os
from pathlib import Path
import subprocess
import time
from datetime import datetime, timedelta, timezone
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
from check_reference_editing import main
from exchange_fixture import fixture as exchange
from preview_fixture import catalogue_fixture, trade_booking, fixture as baseline
from check_futures_margin_adoption import successor_grants


def upgrade_checks(query, migration):
    from assistant_context import check_guard
    if migration.name == '0261_option_lifecycle_actions.sql':
        groups=[('lifecycle_option_status_upgrade','security.futures_option_operation_status(uuid,uuid)')]
    elif migration.name == '0271_futures_delivery_profiles.sql':
        groups=[('futures_delivery_admission_upgrade','security.gas_owned_admission(uuid,text,jsonb,boolean)'),
                ('futures_delivery_hash_upgrade','settlement_data.futures_margin_policy_value(settlement_data.futures_margin_policies,jsonb)'),
                ('futures_delivery_policy_upgrade','settlement_data.save_futures_margin_policy(uuid,jsonb)'),
                ('futures_delivery_policy_upgrade','settlement_data.require_futures_margin_reference(settlement_data.futures_margin_policies)'),
                ('futures_delivery_policy_upgrade','settlement_data.read_futures_margin_origin(uuid,uuid,uuid,uuid,timestamptz)'),
                ('futures_delivery_policy_upgrade','settlement_data.read_futures_known_linked_history(uuid,uuid,uuid,uuid,timestamptz,uuid)'),
                ('futures_delivery_policy_upgrade','lifecycle.read_option_known_history(uuid,uuid,uuid,integer,uuid,timestamptz,date)')]
    elif migration.name == '0272_futures_delivery_obligations.sql':
        groups=[('futures_delivery_writer_upgrade','settlement_data.determine_futures_margin(uuid,jsonb,jsonb)'),
                ('futures_delivery_writer_upgrade','settlement_data.finish_futures_margin_run(uuid,uuid,jsonb,timestamptz)')]
    elif migration.name == '0281_selected_physical_authority.sql':
        groups=[('selected_physical_authority_upgrade',signature) for signature in (
            'lifecycle.capture_action_authority(uuid,text,jsonb)','lifecycle.option_proposal(uuid,jsonb)',
            'settlement_data.futures_margin_command_status(uuid,uuid,text,jsonb)','settlement_data.save_futures_margin_policy(uuid,jsonb)',
            'settlement_data.adopt_futures_margin(uuid,jsonb)','settlement_data.finish_futures_margin_command(uuid,uuid,text,jsonb,timestamptz)',
            'settlement_data.futures_delivery_profile_document(uuid,uuid,integer,uuid,timestamptz)',
            'settlement_data.futures_margin_delivery_authority(uuid,uuid,uuid)',
            'settlement_data.futures_margin_delivery_read_authority_pre_risk(uuid,uuid,uuid,uuid,timestamptz)',
            'settlement_data.read_futures_delivery_obligation_pre_risk(uuid,uuid,uuid,uuid,timestamptz)',
            'settlement_data.read_futures_delivery_position_pre_risk(uuid,uuid,uuid,timestamptz)',
            'settlement_data.read_futures_delivery_settings(uuid,uuid,timestamptz)',
            'settlement_data.read_futures_delivery_profile_pre_risk(uuid,uuid,integer,uuid,timestamptz)')]
    elif migration.name == '0282_swap_net_lifecycle_actions.sql':
        groups=[('swap_net_lifecycle_upgrade','lifecycle.finish_command(uuid,uuid,text,jsonb,text,timestamptz)'),
                ('swap_net_lifecycle_upgrade','lifecycle.record_native_action(uuid,jsonb,jsonb,timestamptz)')]
    elif migration.name == '0275_retained_swap_status.sql':
        groups=[('retained_swap_status_upgrade','settlement_data.swap_operation_status(uuid,uuid)')]
    else: return
    for guard,signature in groups:
        sql_function=signature.startswith('settlement_data.futures_margin_policy_value')
        changes=[(change,signature) for change in (
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",
            f"ALTER FUNCTION {signature} STRICT;",
            f"ALTER FUNCTION {signature} LEAKPROOF;",
            f"ALTER FUNCTION {signature} PARALLEL SAFE;",
            f"ALTER FUNCTION {signature} {'VOLATILE' if sql_function else 'STABLE'};",
            f"ALTER FUNCTION {signature} SECURITY {'DEFINER' if sql_function else 'INVOKER'};",
            f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
            f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime WITH GRANT OPTION;",
            f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
            f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;")]
        if migration.name in('0275_retained_swap_status.sql','0281_selected_physical_authority.sql','0282_swap_net_lifecycle_actions.sql'):
            changes.extend((change,signature) for change in (
                f"UPDATE pg_proc SET provariadic='uuid'::regtype WHERE oid='{signature}'::regprocedure;",
                f"UPDATE pg_proc SET proargmodes=array_fill('i'::\"char\",ARRAY[{signature.count(',')+1}]) WHERE oid='{signature}'::regprocedure;"))
        check_guard(query,migration.read_text(),migration.name[:4],guard,changes)


def fixture(root, *, rate_inputs=None):
    reference,market,security,trading,manifest=exchange(root) if rate_inputs is None else exchange(root,rate_inputs=rate_inputs)
    # Two valid direct curves make singleton inference observably wrong. The
    # selected action profile must retain the actual mark and expiry source.
    marker="UPDATE market_data.revisions SET state='sealed'"
    assert market.count(marker)==1
    additional=f"""
INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||
 jsonb_build_object('code','nymex_ng_alternate','name','Synthetic alternate settlement'))).* FROM market_data.curves c
 WHERE(c.catalog_id,c.revision_id,c.code)=('{manifest['catalogId']}','{manifest['revisionId']}','nymex_ng');
INSERT INTO market_data.curve_members SELECT catalog_id,revision_id,'nymex_ng_alternate',contract_id,ordinal FROM market_data.curve_members
 WHERE(catalog_id,revision_id,curve_code)=('{manifest['catalogId']}','{manifest['revisionId']}','nymex_ng');
INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||
 jsonb_build_object('code','nymex_ng_wrong_mark','name','Synthetic incompatible execution source','direct_source_code','nymex_ng'))).* FROM market_data.curves c
 WHERE(c.catalog_id,c.revision_id,c.code)=('{manifest['catalogId']}','{manifest['revisionId']}','nymex_ng');
INSERT INTO market_data.curve_members SELECT catalog_id,revision_id,'nymex_ng_wrong_mark',contract_id,ordinal FROM market_data.curve_members
 WHERE(catalog_id,revision_id,curve_code)=('{manifest['catalogId']}','{manifest['revisionId']}','nymex_ng');
"""
    market=market.replace(marker,additional+marker)
    roles='\n'.join(line for line in baseline(root)[2].splitlines() if line.startswith('CREATE ROLE '))
    return reference,market,roles+'\n'+security+'\n'+trading,manifest


def checks(query,manifest):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer']
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest,issuer))
    query("INSERT INTO security.role_permissions SELECT role_id,p.code FROM security.role_permissions r CROSS JOIN security.permissions p "
          "WHERE r.permission='AmendTrade' AND p.code IN('ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings') ON CONFLICT DO NOTHING;")
    query("INSERT INTO security.role_permissions SELECT role_id,'ReadGasOwnedHoldings' FROM security.role_permissions WHERE permission='WriteMarketDefinitions' ON CONFLICT DO NOTHING;")
    # This settings reader explicitly holds both market configuration and trade
    # source access; neither permission is inferred from the other.
    query(f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'a0000000-0000-0000-0000-000000000002','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 day',false FROM security.identity_bindings WHERE issuer='{issuer}' AND subject='editor';")
    def lit(body):return "'"+json.dumps(body).replace("'","''")+"'::jsonb"
    def call(sql,profile='trader'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                    f"'{issuer}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','2026-10-16',NULL);")
    def reject(sql,code,profile='trader'):
        try:call(sql,profile)
        except RuntimeError as error:assert code in str(error),str(error)
        else:raise AssertionError('Expected '+code)
    book=query(f"SELECT id FROM trading.books WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    series=query(f"SELECT code FROM ref_data.futures_option_series WHERE(catalog_id,revision_id,product_code,option_right)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','nymex_on','call') ORDER BY expiry_at,strike LIMIT 1;")
    future=query(f"SELECT c.id FROM market_data.contracts c JOIN ref_data.futures_option_series s ON(s.catalog_id,s.revision_id,s.underlying_series_code)=(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code) WHERE c.catalog_id='{catalog}' AND s.code='{series}';")
    expiry=query(f"SELECT expiry_at FROM market_data.contracts WHERE(catalog_id,revision_id,id)=('{catalog}','{manifest['revisionId']}','{future}');")
    # Canonical UTC input, independently read from the exact dated definition.
    expiry=query(f"SELECT to_char(expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM market_data.contracts WHERE(catalog_id,revision_id,id)=('{catalog}','{manifest['revisionId']}','{future}');")
    parties=trade_booking()
    # Exchange fixtures remap the exact party keys; read them from native rows.
    for role,table in [('legalEntity','legal_entities'),('counterparty','counterparties')]:
        row=query(f"SELECT id::text||'|'||revision::text FROM trading.{table} WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;").split('|')
        parties[role+'Id'],parties[role+'Revision']=row[0],int(row[1])
    booked=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','{series}',2,0.1,'2026-10-19','premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{lit(parties)})::text;"))
    mark=query(f"SELECT c.id FROM market_data.contracts c JOIN market_data.curve_members cm ON(cm.catalog_id,cm.revision_id,cm.contract_id)=(c.catalog_id,c.revision_id,c.id) WHERE(c.catalog_id,c.revision_id,cm.curve_code)=('{catalog}','{manifest['revisionId']}','nymex_ng') ORDER BY c.expiry_at DESC LIMIT 1;")
    delivery_from,delivery_to=query(f"SELECT delivery_from::text||'|'||delivery_to::text FROM market_data.contracts WHERE(catalog_id,revision_id,id)=('{catalog}','{manifest['revisionId']}','{future}');").split('|')
    # This synthetic template explicitly selects UTC delivery boundaries and
    # principal due at noon one calendar day after the selected delivery end.
    cash_due=(datetime.strptime(delivery_to,'%Y-%m-%d').replace(tzinfo=timezone.utc)+timedelta(days=1,hours=12)).isoformat().replace('+00:00','Z')
    from futures_delivery_fixture import configure
    delivery=configure(query,manifest,call,capture,curve='nymex_ng',contract=future,book=book,entity=parties['legalEntityId'],entity_revision=parties['legalEntityRevision'],
        mark_contract=mark,account_code='synthetic_option_delivery',account_effective_at='2026-10-01T00:00:00Z',as_of='2026-10-16T16:00:00Z',
        delivery_from=delivery_from+'T00:00:00Z',delivery_to=delivery_to+'T00:00:00Z',cash_due=cash_due,carrying='currency_curve_v1',
        policy_from='2026-10-01T00:00:00Z',policy_until='2027-01-01T00:00:00Z')
    margin=dict(id=str(uuid4()),definitionRevisionId=manifest['revisionId'],contractId=future,settlementCurve='nymex_ng',
                referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],currency='usd',
                fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',opening='include_opening_instant',increment='0.01',tie='to_even',stage='payment_total',
                roundingScope='held_binding_difference_payment_total_v1',implementation='dated_futures_margin_policy_v2',delivery=dict(id=delivery['profile']['id'],revision=1),
                occurrences=[dict(id=str(uuid4()),at=expiry,dueAt=expiry,phase='final')])
    save=dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf='2026-10-16T16:00:00Z',policy=margin)
    call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{lit(save)});",'editor')
    policy=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='option.exercise',mode='manual',
                fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    saved=json.loads(call(f"SELECT lifecycle.save_policy('{catalog}',{lit(policy)})::text;"))
    assert json.loads(call(f"SELECT lifecycle.save_policy('{catalog}',{lit(policy)})::text;"))==saved
    assert json.loads(call(f"SELECT lifecycle.save_policy('{catalog}',{lit({**policy,'operationId':str(uuid4())})})::text;"))['revision']==1
    reject(f"SELECT lifecycle.save_policy('{catalog}',{lit({**policy,'operationId':str(uuid4()),'mode':'automatic'})});",'lifecycle_automation_unsupported')
    profile=dict(operationId=str(uuid4()),tradeId=booked['tradeId'],action='option.exercise',expectedRevision=0,policyId=policy['policyId'],policyRevision=1,
                 marginPolicyId=margin['id'],marginPolicyRevision=1,futureMarkCurve='nymex_ng',expiryCurve=None)
    call(f"SELECT lifecycle.save_option_profile('{catalog}',{lit(profile)});")
    reject(f"SELECT lifecycle.save_option_profile('{catalog}',{lit({**profile,'operationId':str(uuid4()),'futureMarkCurve':'absent'})});",'lifecycle_option_source')
    reject(f"SELECT lifecycle.save_option_profile('{catalog}',{lit({**profile,'operationId':str(uuid4()),'futureMarkCurve':'nymex_ng_wrong_mark','expectedRevision':1})});",'lifecycle_option_source')
    changed_profile={**profile,'operationId':str(uuid4()),'expectedRevision':1,'futureMarkCurve':'nymex_ng_alternate'}
    assert json.loads(call(f"SELECT lifecycle.save_option_profile('{catalog}',{lit(changed_profile)})::text;"))['revision']==2
    acknowledged=json.loads(call(f"SELECT lifecycle.save_option_profile('{catalog}',{lit({**profile,'operationId':str(uuid4()),'expectedRevision':2})})::text;"))
    assert acknowledged['acknowledgedRevision']==1 and acknowledged['currentRevision']==2 and acknowledged['semanticAcknowledgement'] and not acknowledged['changed']
    cycle={**policy,'operationId':str(uuid4()),'policyId':str(uuid4())}
    call(f"SELECT lifecycle.save_policy('{catalog}',{lit(cycle)});")
    call(f"SELECT lifecycle.save_policy('{catalog}',{lit({**cycle,'operationId':str(uuid4()),'expectedRevision':1,'mode':'review'})});")
    acknowledged=json.loads(call(f"SELECT lifecycle.save_policy('{catalog}',{lit({**cycle,'operationId':str(uuid4()),'expectedRevision':2})})::text;"))
    assert acknowledged['acknowledgedRevision']==1 and acknowledged['currentRevision']==2 and acknowledged['semanticAcknowledgement'] and not acknowledged['changed']
    request=dict(operationId=str(uuid4()),context=dict(contextId=capture(),asOf='2026-10-16T16:00:00Z',businessDate='2026-10-16'),
                 target=dict(handler='listed_option',scopeId=booked['tradeId'],bindingId=booked['optionInstrumentId']),action='option.exercise',
                 policyId=policy['policyId'],policyRevision=1,automatic=False,previewHash='0'*64,
                 selection=dict(evidenceId=str(uuid4()),lots='1',expectedEventVersion=0,profileRevision=2))
    proposal=json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{lit(request)})::text;"))
    request['previewHash']=proposal['hash']
    before=query(f"SELECT count(*) FROM trading.futures_option_events WHERE catalog_id='{catalog}';")
    reject(f"SELECT lifecycle.execute_option_action('{catalog}',{lit({**request,'previewHash':'f'*64})});",'lifecycle_stale_review')
    assert query(f"SELECT count(*) FROM trading.futures_option_events WHERE catalog_id='{catalog}';")==before
    with ThreadPoolExecutor(max_workers=1) as pending:
        recorded=pending.submit(call,f"SET application_name='lifecycle_owned_option_pending';SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(2);SELECT lifecycle.execute_option_action('{catalog}',{lit(request)})::text;")
        limit=time.monotonic()+5
        while query("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE a.application_name='lifecycle_owned_option_pending' AND l.locktype='advisory' AND l.classid=0 AND l.objid=730849102 AND l.granted;")!='1':
            assert time.monotonic()<limit,'The owned pending event did not take its publication fence.'
            time.sleep(0.02)
        status=json.loads(call(f"SELECT security.futures_option_operation_status('{catalog}','{request['operationId']}')::text;"))
        result=json.loads(recorded.result(timeout=10))
    assert status['found'] and status['eventId']==result['eventId'] and status['operationId']==request['operationId']
    assert json.loads(call(f"SELECT security.futures_option_operation_status('{catalog}','{request['operationId']}')::text;",'editor'))==dict(found=False)
    historical=json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{lit(request)})::text;"))
    assert historical['hash']==proposal['hash'],'A retained option proposal consumed a later accepted event.'
    assert json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit(request)})::text;"))==result
    same=json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit({**request,'operationId':str(uuid4())})})::text;"))
    assert same==result
    refreshed={**request,'operationId':str(uuid4()),'context':{**request['context'],'contextId':capture()},
               'selection':{**request['selection'],'expectedEventVersion':1,'lots':'1.0'}}
    refreshed['previewHash']=json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{lit(refreshed)})::text;"))['hash']
    assert json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit(refreshed)})::text;"))==result
    assert query(f"SELECT count(*) FROM trading.futures_option_events WHERE catalog_id='{catalog}';")==str(int(before)+1)
    reject(f"SELECT lifecycle.execute_option_action('{catalog}',{lit({**refreshed,'operationId':str(uuid4()),'selection':{**refreshed['selection'],'lots':'2'}})});",'lifecycle_evidence_conflict')
    reject(f"SELECT lifecycle.execute_option_action('{catalog}',{lit({**refreshed,'operationId':str(uuid4()),'selection':{**refreshed['selection'],'expectedEventVersion':-1}})});",'lifecycle_input')
    future_trade=result['futuresTradeId']
    assert query(f"SELECT count(*) FROM settlement_data.futures_margin_adoptions a JOIN trading.futures_option_events e ON(e.catalog_id,e.futures_trade_id,e.publication_order)=(a.catalog_id,a.trade_id,a.publication_order) WHERE a.catalog_id='{catalog}' AND a.trade_id='{future_trade}';")=='1'
    assert query(f"SELECT has_function_privilege('preview_runtime','security.record_futures_option_event(uuid,uuid,uuid,text,numeric,timestamptz)','EXECUTE');")=='f'
    assert query("SELECT assistant.is_prompt_page('lifecycle');")=='t'
    managed={**request,'operationId':str(uuid4()),'context':dict(contextId=capture(),asOf=request['context']['asOf'],businessDate=request['context']['businessDate']),
             'selection':{**request['selection'],'evidenceId':str(uuid4()),'expectedEventVersion':1}}
    host=query('SHOW unix_socket_directories;')
    assert Path(host).resolve().name.startswith('oetrm-ref-')
    path=Path(host)/'lifecycle-action-check.json'
    path.write_text(json.dumps(dict(manifest=manifest,host=host,request=managed,marginPolicyId=margin['id'],marginPolicySave=save)))
    path.chmod(0o600)
    checked=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--',
                            '--lifecycle-actions-native',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=90)
    if checked.returncode:raise RuntimeError(checked.stdout+checked.stderr)
    print(checked.stdout.strip(),flush=True)
    with successor_grants(query,manifest,'trader','ReadTrades',issuer):
        path.write_text(json.dumps(dict(manifest=manifest,host=host,request=managed,marginPolicyId=margin['id'],marginPolicySave=save,wrappedRead=True)))
        checked=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--',
                               '--lifecycle-actions-native',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=90)
        if checked.returncode:raise RuntimeError(checked.stdout+checked.stderr)
        print(checked.stdout.strip(),flush=True)
    path.write_text(json.dumps(dict(manifest=manifest,host=host,request=managed,marginPolicyId=margin['id'],marginPolicySave=save,
        historyContext=dict(contextId=capture(),asOf='2026-10-16T15:00:00Z',businessDate='2026-10-16'))))
    checked=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--',
                           '--lifecycle-actions-native',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=90)
    if checked.returncode:raise RuntimeError(checked.stdout+checked.stderr)
    print(checked.stdout.strip(),flush=True)
    private_reference,private_definition=str(uuid4()),str(uuid4())
    query(f"INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{manifest['referenceCatalogId']}','{private_reference}','private_option_source');"
          f"UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{manifest['referenceCatalogId']}','{private_reference}');"
          f"INSERT INTO market_data.revisions VALUES('{catalog}','{private_definition}','{manifest['referenceCatalogId']}','{private_reference}','draft',0);"
          f"INSERT INTO market_data.revision_periods VALUES('{catalog}','{private_definition}','2027-02-01','2028-01-01');"
          f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{private_definition}');")
    historical=capture()
    assert call(f"SELECT security.reference_version_visible('{manifest['referenceCatalogId']}','{private_reference}');")=='t'
    bound=call(f"SELECT security.admit_trading_business_source('{catalog}','{historical}','2026-10-16T15:00:00Z','2026-10-16');"
               f"SELECT jsonb_array_length(lifecycle.read_option_known_history('{catalog}','{booked['tradeId']}','{booked['optionInstrumentId']}',1,'{historical}','2026-10-16T15:00:00Z','2026-10-16')->'events');"
               f"SELECT security.reference_version_visible('{manifest['referenceCatalogId']}','{private_reference}');")
    assert bound.splitlines()[-2:]==['2','f'],'An unbound option source reader retained current private reference visibility: '+bound
    other=call(f"SELECT security.capture_date_context('{catalog}','2026-11-03',NULL);")
    reject(f"SELECT security.admit_trading_business_source('{catalog}','{historical}','2026-10-16T15:00:00Z','2026-10-16');"
           f"SELECT security.use_market_context('{catalog}','{other}');SELECT security.use_trading_context('{catalog}','2026-11-03T16:00:00Z','2026-11-03'::date);"
           f"SELECT lifecycle.read_option_known_history('{catalog}','{booked['tradeId']}','{booked['optionInstrumentId']}',1,'{historical}','2026-10-16T15:00:00Z','2026-10-16');",'lifecycle_option_source')
    # Several valid future adoptions can share a large closed schedule. The
    # source must refuse complete work that exceeds its row/byte contract.
    first_daily=datetime(2026,10,15,tzinfo=timezone.utc)
    occurrences=[dict(id=str(uuid4()),at=(first_daily+timedelta(microseconds=i)).isoformat().replace('+00:00','Z'),
                      dueAt=(first_daily+timedelta(microseconds=i)).isoformat().replace('+00:00','Z'),phase='daily') for i in range(16383)]
    large={**margin,'id':str(uuid4()),'occurrences':occurrences+margin['occurrences']}
    call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{lit({**save,'operationId':str(uuid4()),'policy':large})});",'editor')
    budget=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','{series}',5,0.1,'2026-10-19','premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{lit(parties)})::text;"))
    call(f"SELECT lifecycle.save_option_profile('{catalog}',{lit({**profile,'operationId':str(uuid4()),'tradeId':budget['tradeId'],'marginPolicyId':large['id']})});")
    for version in range(5):
        partial={**request,'operationId':str(uuid4()),'context':{**request['context'],'contextId':capture()},
                 'target':{**request['target'],'scopeId':budget['tradeId'],'bindingId':budget['optionInstrumentId']},
                 'selection':dict(evidenceId=str(uuid4()),lots='1',expectedEventVersion=version,profileRevision=1)}
        partial['previewHash']=json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{lit(partial)})::text;"))['hash']
        call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit(partial)});")
        if version>=3:
            selected=capture()
            reject(f"SELECT security.admit_trading_business_source('{catalog}','{selected}','2026-10-16T15:00:00Z','2026-10-16');"
                   f"SELECT jsonb_array_length(lifecycle.read_option_known_history('{catalog}','{budget['tradeId']}','{budget['optionInstrumentId']}',1,'{selected}','2026-10-16T15:00:00Z','2026-10-16')->'events');",'lifecycle_option_source_capacity')
    print('PASS explicit lifecycle policies: reviewed scope, native option economics, stable evidence retry and atomic generated-future margin adoption',flush=True)


if __name__=='__main__':main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
