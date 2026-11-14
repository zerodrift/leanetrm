"""Explicit action settings and option-to-future enrollment on owned PostgreSQL."""
import json
import os
from pathlib import Path
import subprocess
from uuid import uuid4
from check_reference_editing import main
from exchange_fixture import fixture as exchange
from preview_fixture import catalogue_fixture, trade_booking, fixture as baseline
from check_futures_margin_adoption import successor_grants


def fixture(root):
    reference,market,security,trading,manifest=exchange(root)
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
    booked=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','{series}',2,0.1,'2026-10-19','premium_paid_up_front','2026-10-16T16:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{lit(parties)})::text;"))
    margin=dict(id=str(uuid4()),definitionRevisionId=manifest['revisionId'],contractId=future,settlementCurve='nymex_ng',
                referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],currency='usd',
                fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',opening='include_opening_instant',increment='0.01',tie='to_even',stage='payment_total',
                roundingScope='held_binding_difference_payment_total_v1',implementation='dated_futures_margin_policy_v1',
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
    request=dict(operationId=str(uuid4()),context=dict(contextId=capture(),asOf='2026-10-16T16:00:00Z',businessDate='2026-10-16'),
                 target=dict(handler='listed_option',scopeId=booked['tradeId'],bindingId=booked['optionInstrumentId']),action='option.exercise',
                 policyId=policy['policyId'],policyRevision=1,automatic=False,previewHash='0'*64,
                 selection=dict(evidenceId=str(uuid4()),lots='1',expectedEventVersion=0,profileRevision=1))
    proposal=json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{lit(request)})::text;"))
    request['previewHash']=proposal['hash']
    before=query(f"SELECT count(*) FROM trading.futures_option_events WHERE catalog_id='{catalog}';")
    reject(f"SELECT lifecycle.execute_option_action('{catalog}',{lit({**request,'previewHash':'f'*64})});",'lifecycle_stale_review')
    assert query(f"SELECT count(*) FROM trading.futures_option_events WHERE catalog_id='{catalog}';")==before
    result=json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{lit(request)})::text;"))
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
    print('PASS explicit lifecycle policies: reviewed scope, native option economics, stable evidence retry and atomic generated-future margin adoption',flush=True)


if __name__=='__main__':main(checks=checks,fixture_factory=fixture)
