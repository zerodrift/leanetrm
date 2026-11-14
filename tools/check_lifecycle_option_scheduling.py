"""Actual scheduled listed-option expiry, with exact linked-future continuation."""
from datetime import datetime,timedelta,timezone
import json
from uuid import uuid4

from check_reference_editing import main
from check_lifecycle_actions import fixture as action_fixture
from close_session_fixture import sql_session
from futures_delivery_fixture import configure
from lifecycle_schedule_fixture import execute
from preview_fixture import catalogue_fixture


def fixture(root):
    reference,market,security,manifest=action_fixture(root)
    catalog,revision,rcat,rrev=(manifest[k] for k in ('catalogId','revisionId','referenceCatalogId','referenceRevisionId'))
    marker="UPDATE ref_data.revisions SET state='sealed'";assert market.count(marker)==1
    # A separately labelled synthetic right expires on an already observable
    # historical date. No official future observation or system clock is forged.
    synthetic=f"""INSERT INTO ref_data.calendars VALUES('{rcat}','{rrev}','synthetic_option_task_days','test_utc',0,'2026-01-01','2027-01-01');
INSERT INTO ref_data.futures_option_series VALUES('{rcat}','{rrev}','synthetic_scheduled_call','nymex_on','nymex_ng_20261101','call',3,'synthetic','2026-10-27',
 ref_data.close_instant('{rcat}','{rrev}','option_expiry','nymex_on','2026-10-27'),NULL,'fixture');
"""
    market=market.replace(marker,synthetic+marker)
    marker="UPDATE market_data.revisions SET state='sealed'";assert market.count(marker)==1
    synthetic=f"""INSERT INTO market_data.contracts SELECT(jsonb_populate_record(NULL::market_data.contracts,to_jsonb(c)||
 jsonb_build_object('id','90800000-0000-0000-0000-000000000001','delivery_from','2026-11-01','delivery_to','2026-12-01',
 'expiry_at',ref_data.close_instant('{rcat}','{rrev}','option_expiry','nymex_on','2026-10-27'),'futures_option_series_code','synthetic_scheduled_call'))).*
 FROM market_data.contracts c WHERE(c.catalog_id,c.revision_id,c.kind)=('{catalog}','{revision}','future_option') ORDER BY c.id LIMIT 1;
INSERT INTO market_data.curve_members SELECT '{catalog}','{revision}','nymex_on','90800000-0000-0000-0000-000000000001',max(ordinal)+1
 FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{revision}','nymex_on');
INSERT INTO market_data.series SELECT(jsonb_populate_record(NULL::market_data.series,to_jsonb(s)||
 jsonb_build_object('id','90800000-0000-0000-0000-000000000002','code','synthetic_scheduled_option_price','contract_id','90800000-0000-0000-0000-000000000001'))).*
 FROM market_data.series s WHERE(s.catalog_id,s.revision_id,s.quote_code)=('{catalog}','{revision}','nymex_on_quote') ORDER BY s.id LIMIT 1;
"""
    market=market.replace(marker,synthetic+marker)
    return reference,market,security,manifest


def checks(query,manifest):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest,manifest['identityIssuer']))
    query("INSERT INTO security.role_permissions SELECT role_id,p FROM security.role_permissions CROSS JOIN unnest(ARRAY['RunJobs','PublishMarketData','ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings'])p WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    query("INSERT INTO security.role_permissions SELECT role_id,p FROM security.role_permissions CROSS JOIN unnest(ARRAY['ReadTrades','ReadGasOwnedHoldings'])p WHERE permission='WriteMarketDefinitions' ON CONFLICT DO NOTHING;")
    def lit(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,profile='trader'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    capture=lambda:call(f"SELECT security.capture_date_context('{catalog}','2026-10-16',NULL);")
    book=query(f"SELECT id FROM trading.books WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    parties={}
    for role,table in [('legalEntity','legal_entities'),('counterparty','counterparties')]:
        key,revision=query(f"SELECT id::text||'|'||revision::text FROM trading.{table} WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;").split('|')
        parties[role+'Id'],parties[role+'Revision']=key,int(revision)
    series,future,option_at,final_at,delivery_from,delivery_to,strike=query(
        "SELECT s.code||'|'||f.id::text||'|'||to_char(s.expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')||'|'||"
        "to_char(f.expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')||'|'||f.delivery_from::text||'|'||f.delivery_to::text||'|'||s.strike::text "
        "FROM ref_data.futures_option_series s JOIN market_data.contracts f ON(f.reference_catalog_id,f.reference_revision_id,f.futures_series_code)=(s.catalog_id,s.revision_id,s.underlying_series_code) "
        f"WHERE(s.catalog_id,s.revision_id,s.code)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','synthetic_scheduled_call');").split('|')
    mark=query(f"SELECT c.id FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id) WHERE(c.catalog_id,c.revision_id,m.curve_code)=('{catalog}','{manifest['revisionId']}','nymex_ng') ORDER BY c.expiry_at DESC LIMIT 1;")
    due=(datetime.strptime(delivery_to,'%Y-%m-%d').replace(tzinfo=timezone.utc)+timedelta(days=1,hours=12)).isoformat().replace('+00:00','Z')
    template=configure(query,manifest,call,capture,curve='nymex_ng',contract=future,book=book,entity=parties['legalEntityId'],entity_revision=parties['legalEntityRevision'],
        mark_contract=mark,account_code='synthetic_scheduled_option_delivery',account_effective_at='2026-10-01T00:00:00Z',as_of='2026-10-16T16:00:00Z',
        delivery_from=delivery_from+'T00:00:00Z',delivery_to=delivery_to+'T00:00:00Z',cash_due=due,carrying='currency_curve_v1',policy_from='2026-10-01T00:00:00Z',policy_until='2027-01-01T00:00:00Z')
    margin=dict(id=str(uuid4()),definitionRevisionId=manifest['revisionId'],contractId=future,settlementCurve='nymex_ng',referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],
        currency='usd',fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',opening='include_opening_instant',increment='0.01',tie='to_even',stage='payment_total',
        roundingScope='held_binding_difference_payment_total_v1',implementation='dated_futures_margin_policy_v2',delivery=dict(id=template['profile']['id'],revision=1),occurrences=[dict(id=str(uuid4()),at=final_at,dueAt=final_at,phase='final')])
    call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{lit(dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf='2026-10-16T16:00:00Z',policy=margin))});",'editor')
    booked=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','{series}',2,0.1,'2026-10-19','premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{lit(parties)})::text;"))
    policy=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='option.expiry',mode='automatic',fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{lit(policy)});")
    profile=dict(operationId=str(uuid4()),tradeId=booked['tradeId'],action='option.expiry',expectedRevision=0,policyId=policy['policyId'],policyRevision=1,marginPolicyId=margin['id'],marginPolicyRevision=1,futureMarkCurve='nymex_ng',expiryCurve='nymex_ng')
    call(f"SELECT lifecycle.save_option_profile('{catalog}',{lit(profile)});")
    # An independently supplied official settlement at the native option expiry
    # is a source fact. The task does not generate its own market observation.
    from decimal import Decimal
    settlement=Decimal('5');assert settlement>Decimal(strike)
    observation=str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT catalog_id,'{observation}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,'synthetic_option_expiry_task','1','daily_settlement','final','{option_at}',clock_timestamp(),{settlement},NULL FROM market_data.observations o WHERE o.id=(SELECT o.id FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) WHERE(o.catalog_id,o.definition_revision_id,s.contract_id)=('{catalog}','{manifest['revisionId']}','{future}') ORDER BY o.observed_at LIMIT 1);")
    day=option_at[:10];sql_session(query,manifest,'synthetic_option_task_close','synthetic_option_task_days',option_at[11:19],['nymex_ng'],['trader'],effective=('2026-01-01','2027-01-01'))
    pins=json.loads(query(f"SELECT jsonb_agg(jsonb_build_object('definitionRevisionId',definition_revision_id,'seriesId',series_id,'observationId',id,'inputUse','variable')) FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{observation}');"))
    members=[dict(definitionRevisionId=manifest['revisionId'],curve='nymex_ng',contractId=future,ordinal=0,scope='root')]
    close=call(f"SELECT security.register_market_close('{catalog}','{manifest['revisionId']}','{uuid4()}','Synthetic option expiry close','synthetic_option_task_close','{day}','{option_at}',{lit(pins)},{lit(members)});")
    claim=execute(query,manifest,call,close_id=close,target=dict(handler='listed_option',scopeId=booked['tradeId'],bindingId=booked['optionInstrumentId']),action='option.expiry',occurrence_id=booked['tradeId'],
        policy_id=policy['policyId'],policy_revision=1,execution_permission='BookTrade',extra_permissions=['ReadGasOwnedHoldings','RecordGasOwnedHoldings'],
        settings=dict(tradeRevision=1,optionProfileRevision=1,swapProfileId=None,swapProfileRevision=None,marginPolicyId=margin['id'],marginPolicyRevision=1))
    event=json.loads(query(f"SELECT to_jsonb(e)::text FROM trading.futures_option_events e WHERE(e.catalog_id,e.request_id)=('{catalog}','{claim['id']}');"))
    assert event['kind']=='expiry' and event['outcome']=='exercised' and Decimal(event['lots'])==2
    assert event['settlement_observation_id']==observation and Decimal(event['settlement_price'])==settlement
    child=json.loads(query(f"SELECT to_jsonb(r)::text FROM trading.trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision)=('{catalog}','{event['futures_trade_id']}',1);"))
    assert Decimal(child['lots'])==2 and Decimal(child['execution_price'])==Decimal(strike) and child['publication_order']==event['publication_order']
    assert query(f"SELECT policy_id::text||'|'||policy_revision::text||'|'||(publication_order={event['publication_order']})::text FROM settlement_data.futures_margin_adoptions WHERE(catalog_id,trade_id)=('{catalog}','{event['futures_trade_id']}');")==margin['id']+'|1|true'
    assert query(f"SELECT count(*) FROM trading.gas_owned_lots WHERE catalog_id='{catalog}';")=='0'
    print('PASS actual scheduled option expiry: official selected settlement, exact automatic rule, one exercised right and strike-basis future with atomic v2 delivery adoption, no inferred title',flush=True)


if __name__=='__main__':main(checks=checks,fixture_factory=fixture)
