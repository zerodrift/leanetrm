"""Generic execution qualification on newly owned synthetic resources only."""
import json
import re
from decimal import Decimal
from datetime import datetime
from uuid import uuid4
from preview_client import PreviewClient


def prepare(reference,market,manifest):
    # This fixture supplies its own explicit candidate qualification below.
    market=re.sub(r"DO \$policy\$ BEGIN.*?END \$policy\$;", "", market, flags=re.S)
    r=manifest['referenceCatalogId'];rev=manifest['referenceRevisionId']
    reference=reference.replace("'Synthetic fixed-lot gas future','synthetic'","'Synthetic fixed-lot gas future','candidate'")
    reference=reference.replace("'2021-01','synthetic'","'2021-01','candidate'")
    reference=reference.replace("'first_trading','known',DATE '2019-12-01',TIMESTAMPTZ '2019-12-01 15:30:00+00'","'first_trading','known',DATE '2020-11-30',NULL")
    market='\n'.join(line.replace("'synthetic'","'candidate'") if 'INSERT INTO ref_data.futures_series' in line else line for line in market.splitlines())
    policies=f"""
INSERT INTO ref_data.sources VALUES('{r}','{rev}','qualification','manual_entry','operator:reviewed-contract-terms',clock_timestamp(),NULL,NULL,'not_reviewed');
INSERT INTO ref_data.futures_execution_policies(catalog_id,revision_id,series_code,implementation,eligibility_rule,eligible_from,source_date_offset_seconds,source_date_tzdb_version,price_role,price_source_code,evidence_source_code)
 SELECT s.catalog_id,s.revision_id,s.code,'contract_lot_execution_pnl_v2',CASE WHEN m.event_at IS NULL THEN 'after_source_first_trade_date_v1' ELSE 'explicit_utc_bound_v1' END,'2020-12-01T12:30:00Z',CASE WHEN m.event_at IS NULL THEN 0 ELSE NULL END,NULL,'trader_mark','fixture','qualification'
 FROM ref_data.futures_series s JOIN ref_data.futures_milestones m ON(m.catalog_id,m.revision_id,m.series_code,m.milestone)=(s.catalog_id,s.revision_id,s.code,'first_trading') WHERE s.catalog_id='{r}' AND s.revision_id='{rev}' AND s.product_code='test_gas_future';
"""
    marker=f"UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{r}' AND id='{rev}';"
    assert marker in market
    market=market.replace(marker,policies+'\n'+marker).replace("'daily_settlement'","'trader_mark'")
    manifest['futuresConvention']='contract_lot_execution_pnl_v2'
    return reference,market,manifest


def run_checks(url,query,manifest):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'});w=c.call('workspace')
    frame=next(f for f in w['frames'] if f['at'].startswith('2020-12-18'))
    context={'snapshotId':frame['id'],'convention':w['futuresConvention'],'knowledgeMode':'current'}
    catalog=c.call('futures/catalog',context);future=catalog['contracts'][0]
    assert future['firstTrading'] is None and future['eligibleFrom'].startswith('2020-12-01T12:30:00')
    sequences=c.call('schedules')['sources']
    entry=next(e for sequence in sequences for e in sequence['entries'] if e['contractId']==future['id'])
    assert datetime.fromisoformat(entry['eligibleFrom'].replace('Z','+00:00'))==datetime.fromisoformat(future['eligibleFrom'].replace('Z','+00:00')), 'Contract sequences must retain the selected execution bound without inventing a source event'
    body={**context,'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'curve':future['curve'],'contractId':future['id'],
          'lots':'2','executionPrice':str(Decimal(future['mark'])-Decimal(future['tick'])),'memo':'Synthetic behavior check',
          'legalEntityId':catalog['legalEntities'][0]['id'],'legalEntityRevision':catalog['legalEntities'][0]['revision'],
          'counterpartyId':catalog['counterparties'][0]['id'],'counterpartyRevision':catalog['counterparties'][0]['revision']}
    saved=c.call('futures/book',body);assert c.call('futures/book',body)==saved
    holdings=c.call('futures/holdings',context)['holdings'];assert len(holdings)==1
    assert Decimal(holdings[0]['executionPnl'])==2*Decimal(future['contractSize'])*Decimal(future['tick'])
    r=manifest['referenceCatalogId'];rev=manifest['referenceRevisionId'];t=manifest['tenantId'];ws=manifest['workspaceId'];market=manifest['catalogId']
    assert query(f"SELECT event_at IS NULL FROM ref_data.futures_milestones WHERE catalog_id='{r}' AND revision_id='{rev}' AND series_code='test_gas_202101' AND milestone='first_trading';")=='t'
    query('GRANT EXECUTE ON FUNCTION security.futures_execution_start(uuid,uuid,text,text,text,text) TO preview_runtime;')
    def token():
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{t}','{ws}',NULL,'preview_runtime',60);")
    def raw(edit='',role='trader_mark',source='fixture',historical=False,reject=True,implementation='contract_lot_execution_pnl_v2'):
        bind=f"PERFORM security.use_market_context('{market}',security.capture_date_context('{market}','2020-12-18','2000-01-01T00:00:00Z'));" if historical else ''
        invoke=f"PERFORM security.futures_execution_start('{r}','{rev}','test_gas_202101','{implementation}','{role}','{source}');"
        check=("DECLARE rejected boolean=false;BEGIN BEGIN "+bind+invoke+" EXCEPTION WHEN SQLSTATE 'P0001' OR SQLSTATE '42501' THEN rejected=true;END;IF NOT rejected THEN RAISE EXCEPTION 'Expected policy rejection' USING ERRCODE='XX000';END IF;END") if reject else ('BEGIN '+invoke+'END')
        query(f"BEGIN;{edit}SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token()}');DO $$ {check} $$;ROLLBACK;")
    raw(reject=False)
    raw(f"ALTER TABLE ref_data.futures_execution_policies DISABLE TRIGGER frozen_definition;DELETE FROM ref_data.futures_execution_policies WHERE revision_id='{rev}' AND series_code='test_gas_202101';")
    raw(role='daily_settlement');raw(source='qualification')
    raw(f"ALTER TABLE ref_data.futures_execution_policies DISABLE TRIGGER frozen_definition;UPDATE ref_data.futures_execution_policies SET eligibility_rule='source_first_trade_instant_v1',source_date_offset_seconds=NULL WHERE revision_id='{rev}' AND series_code='test_gas_202101';")
    policy_edit=f"ALTER TABLE ref_data.futures_execution_policies DISABLE TRIGGER frozen_definition;UPDATE ref_data.futures_execution_policies SET "
    target=f" WHERE revision_id='{rev}' AND series_code='test_gas_202101';"
    raw(policy_edit+"eligible_from='2020-11-30T23:59:59Z'"+target)
    raw(policy_edit+"eligibility_rule='explicit_utc_bound_v1',source_date_offset_seconds=NULL"+target)
    raw(policy_edit+"source_date_offset_seconds=3600"+target)
    raw(f"ALTER TABLE ref_data.clock_rules DISABLE TRIGGER guard_definition;UPDATE ref_data.clock_rules SET kind='iana',fixed_offset_seconds=NULL,iana_zone='America/Chicago',tzdb_version='2026c' WHERE revision_id='{rev}' AND code='test_utc';"+policy_edit+"source_date_offset_seconds=-21600,source_date_tzdb_version='stale'"+target)
    raw(f"ALTER TABLE ref_data.clock_rules DISABLE TRIGGER guard_definition;UPDATE ref_data.clock_rules SET kind='iana',fixed_offset_seconds=NULL,iana_zone='America/Chicago',tzdb_version='2026c' WHERE revision_id='{rev}' AND code='test_utc';"+policy_edit+"source_date_offset_seconds=0,source_date_tzdb_version='2026c'"+target)
    # A policy retained under another implementation cannot be bypassed by the
    # synthetic legacy convention. All edits are rolled back in this owned DB.
    synthetic=f"ALTER TABLE ref_data.futures_products DISABLE TRIGGER guard_definition;UPDATE ref_data.futures_products SET definition_state='synthetic' WHERE revision_id='{rev}' AND code='test_gas_future';ALTER TABLE ref_data.futures_series DISABLE TRIGGER guard_definition;UPDATE ref_data.futures_series SET definition_state='synthetic' WHERE revision_id='{rev}' AND code='test_gas_202101';ALTER TABLE ref_data.futures_milestones DISABLE TRIGGER guard_definition;UPDATE ref_data.futures_milestones SET event_at='2020-11-30T13:00:00Z' WHERE revision_id='{rev}' AND series_code='test_gas_202101' AND milestone='first_trading';"
    raw(synthetic+policy_edit+"eligibility_rule='explicit_utc_bound_v1',source_date_offset_seconds=NULL"+target,role='daily_settlement',implementation='fixed_total_cash_execution_pnl_v1')
    raw(synthetic+policy_edit+"eligibility_rule='source_first_trade_instant_v1',source_date_offset_seconds=NULL,eligible_from='2020-11-30T13:00:00Z'"+target,role='daily_settlement',implementation='fixed_total_cash_execution_pnl_v1')
    raw(synthetic+f"ALTER TABLE ref_data.futures_execution_policies DISABLE TRIGGER frozen_definition;DELETE FROM ref_data.futures_execution_policies WHERE revision_id='{rev}' AND series_code='test_gas_202101';",role='daily_settlement')
    havana=f"ALTER TABLE ref_data.clock_rules DISABLE TRIGGER guard_definition;UPDATE ref_data.clock_rules SET kind='iana',fixed_offset_seconds=NULL,iana_zone='America/Havana',tzdb_version='2026c' WHERE revision_id='{rev}' AND code='test_utc';ALTER TABLE ref_data.futures_milestones DISABLE TRIGGER guard_definition;UPDATE ref_data.futures_milestones SET business_date='2026-10-31' WHERE revision_id='{rev}' AND series_code='test_gas_202101' AND milestone='first_trading';"
    for offset in (-14400,-18000):
        raw(havana+policy_edit+f"source_date_offset_seconds={offset},source_date_tzdb_version='2026c',eligible_from='2026-11-02T12:30:00Z'"+target)
    raw(havana.replace("business_date='2026-10-31'","business_date='2026-03-07'")+policy_edit+"source_date_offset_seconds=-18000,source_date_tzdb_version='2026c',eligible_from='2026-03-09T12:30:00Z'"+target)
    raw(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{r}';")
    raw(historical=True)
    # Authorization must precede reading even malformed economics, rather than
    # leaking a different convention error to a caller whose scope was revoked.
    parties=json.dumps({k:body[k] for k in ('legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision')})
    invoke=f"security.book_futures_with_parties('{market}','{uuid4()}','{body['bookId']}','{manifest['revisionId']}','{future['curve']}','{future['id']}','{frame['at']}',NULL,1,{body['executionPrice']},'contract_lot_execution_pnl_v2',NULL,NULL,NULL,'{parties}'::jsonb)"
    query(f"""BEGIN;DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{r}';
      ALTER TABLE ref_data.futures_products DISABLE TRIGGER guard_definition;
      UPDATE ref_data.futures_products SET definition_state='placeholder' WHERE catalog_id='{r}' AND revision_id='{rev}' AND code='test_gas_future';
      SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token()}');
      DO $$BEGIN PERFORM {invoke};RAISE EXCEPTION 'Expected authorization rejection before economics';
      EXCEPTION WHEN insufficient_privilege THEN NULL;END$$;ROLLBACK;""")
    assert query("SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='ref_data.futures_execution_policies'::regclass;")=='t'
    print('PASS standard futures policies: date-only source preserved, explicit bound, matching source/role, idempotent authenticated booking and exact execution P&L; missing policy, conflicting source/role, revoked scope and invisible history reject',flush=True)
