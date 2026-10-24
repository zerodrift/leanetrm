"""Sourced-term pilot admission checks on freshly allocated synthetic resources only."""
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient


def prepare(reference,market,manifest):
    r=manifest['referenceCatalogId'];rev=manifest['referenceRevisionId'];source='20000000-0000-0000-0000-000000000091'
    reference=reference.replace("'Synthetic fixed-lot gas future','synthetic'","'Synthetic fixed-lot gas future','candidate'")
    reference=reference.replace("'2021-01','synthetic'","'2021-01','candidate'")
    reference=reference.replace("'first_trading','known',DATE '2019-12-01',TIMESTAMPTZ '2019-12-01 15:30:00+00'","'first_trading','known',DATE '2020-11-30',NULL")
    reference=reference.replace('license_reference,distribution_review)','license_reference,distribution_review,content_sha256)').replace("'redistributable' FROM fixture_context","'redistributable',repeat('a',64) FROM fixture_context").replace('synthetic:reference-fixture-v1','generated:pilot-close-v1/synthetic')
    market='\n'.join(line.replace("'synthetic'","'candidate'") if 'INSERT INTO ref_data.futures_series' in line else line for line in market.splitlines())
    reference_sql=f"""
INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{r}','{source}','synthetic_native_evidence');
INSERT INTO ref_data.exchanges VALUES('{r}','{source}','test_exchange','Synthetic venue');
INSERT INTO ref_data.futures_products(catalog_id,revision_id,code,name,exchange_code) VALUES('{r}','{source}','test_gas_future','Synthetic source future','test_exchange');
INSERT INTO ref_data.futures_series(catalog_id,revision_id,code,product_code,series_label) SELECT catalog_id,'{source}',code,product_code,series_label FROM ref_data.futures_series WHERE catalog_id='{r}' AND revision_id='{rev}' AND product_code='test_gas_future';
UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{r}' AND id='{source}';
INSERT INTO ref_data.sources VALUES('{r}','{rev}','qualification','manual_entry','operator:synthetic-qualified-terms',clock_timestamp(),repeat('b',64),NULL,'not_reviewed');
INSERT INTO ref_data.futures_execution_admissions(catalog_id,revision_id,series_code,implementation,usage_policy,execution_venue,qualification_source_code,source_catalog_id,source_revision_id,source_series_code,eligible_from,eligibility_rule,eligibility_offset_seconds,eligibility_tzdb_version)
 SELECT s.catalog_id,s.revision_id,s.code,'contract_lot_execution_pnl_v2','generated_prices_sample_trades_v1','central_limit_order_book','qualification',s.catalog_id,'{source}',s.code,
 CASE WHEN m.event_at IS NULL THEN '2020-12-01T00:00:00Z'::timestamptz ELSE m.event_at END,
 CASE WHEN m.event_at IS NULL THEN 'after_source_first_trade_date_v1' ELSE 'exchange_first_trade_instant_v1' END,CASE WHEN m.event_at IS NULL THEN 0 ELSE NULL END,NULL
 FROM ref_data.futures_series s JOIN ref_data.futures_milestones m ON(m.catalog_id,m.revision_id,m.series_code,m.milestone)=(s.catalog_id,s.revision_id,s.code,'first_trading')
 WHERE s.catalog_id='{r}' AND s.revision_id='{rev}' AND s.product_code='test_gas_future';
"""
    marker=f"UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{r}' AND id='{rev}';"
    market=market.replace(marker,reference_sql+'\n'+marker)
    market=market.replace("'daily_settlement'","'trader_mark'")
    manifest['futuresConvention']='contract_lot_execution_pnl_v2'
    return reference,market,manifest


def run_checks(url,query,manifest):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'});w=c.call('workspace')
    frame=next(f for f in w['frames'] if f['at'].startswith('2020-12-18'))
    context={'snapshotId':frame['id'],'convention':w['futuresConvention'],'knowledgeMode':'current'}
    catalog=c.call('futures/catalog',context);future=catalog['contracts'][0]
    assert future['firstTrading'] is None and future['eligibleFrom'].startswith('2020-12-01')
    assert 'generated closing marks' in future['eligibilityNote']
    body={**context,'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'curve':future['curve'],'contractId':future['id'],
          'lots':'2','executionPrice':str(Decimal(future['mark'])-Decimal(future['tick'])),'memo':'Synthetic admission check',
          'legalEntityId':catalog['legalEntities'][0]['id'],'legalEntityRevision':catalog['legalEntities'][0]['revision'],
          'counterpartyId':catalog['counterparties'][0]['id'],'counterpartyRevision':catalog['counterparties'][0]['revision']}
    saved=c.call('futures/book',body);assert c.call('futures/book',body)==saved
    holdings=c.call('futures/holdings',context)['holdings'];assert len(holdings)==1
    assert Decimal(holdings[0]['executionPnl'])==2*Decimal(future['contractSize'])*Decimal(future['tick'])
    # The exact first-trading event remains unknown in stored source facts.
    assert query(f"SELECT event_at IS NULL FROM ref_data.futures_milestones WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}' AND series_code='test_gas_202101' AND milestone='first_trading';")=='t'
    print('PASS sourced pilot: candidate terms, exact admission, generated marks, conservative eligibility, authenticated booking and exact execution P&L',flush=True)

    # Fault injection is confined to this newly owned disposable database and
    # rolls back every edit, including trigger state and scope removals.
    r=manifest['referenceCatalogId'];rev=manifest['referenceRevisionId'];t=manifest['tenantId'];ws=manifest['workspaceId'];market=manifest['catalogId']
    query('GRANT EXECUTE ON FUNCTION security.futures_eligible_from(uuid,uuid,text,text,text,text) TO preview_runtime;')
    def token():
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{t}','{ws}',NULL,'preview_runtime',60);")
    def raw(edit='',role='trader_mark',source='fixture',historical=False,reject=True):
        bind=f"PERFORM security.use_market_context('{market}',security.capture_date_context('{market}','2020-12-18','2000-01-01T00:00:00Z'));" if historical else ''
        invocation=f"PERFORM security.futures_eligible_from('{r}','{rev}','test_gas_202101','contract_lot_execution_pnl_v2','{role}','{source}');"
        check=("DECLARE rejected boolean=false;BEGIN BEGIN "+bind+invocation+" EXCEPTION WHEN SQLSTATE 'P0001' OR SQLSTATE '42501' THEN rejected=true;END;IF NOT rejected THEN RAISE EXCEPTION 'Expected admission rejection' USING ERRCODE='XX000';END IF;END") if reject else ('BEGIN '+invocation+'END')
        query(f"BEGIN;{edit}SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token()}');DO $$ {check} $$;ROLLBACK;")
    raw(reject=False)
    raw(f"ALTER TABLE ref_data.futures_execution_admissions DISABLE TRIGGER guard_definition;DELETE FROM ref_data.futures_execution_admissions WHERE revision_id='{rev}' AND series_code='test_gas_202101';")
    raw(role='daily_settlement');raw(source='qualification')
    raw(f"ALTER TABLE ref_data.futures_execution_admissions DISABLE TRIGGER guard_definition;UPDATE ref_data.futures_execution_admissions SET eligible_from=eligible_from+interval '1 minute' WHERE revision_id='{rev}' AND series_code='test_gas_202101';")
    raw(f"ALTER TABLE ref_data.sources DISABLE TRIGGER guard_definition;UPDATE ref_data.sources SET content_sha256=NULL WHERE revision_id='{rev}' AND code='fixture';")
    raw(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{r}';")
    raw(historical=True)
    hidden=frame['observationIds'][0]
    query(f"CREATE POLICY synthetic_hidden_pin ON market_data.observations AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(id<>'{hidden}'::uuid);")
    try:
        rejected=c.call('futures/catalog',context,expected=422)
        assert rejected['code']=='ambiguous_reference'
    finally:query('DROP POLICY synthetic_hidden_pin ON market_data.observations;')
    assert c.call('futures/catalog',context)['contracts']==catalog['contracts']
    print('PASS selected-pin read obeys row authorization and rejects a hidden exact input without fallback',flush=True)
    print('PASS raw admission boundary: missing receipt, official role, wrong source, inconsistent offset, missing hash, revoked reference scope and invisible history all reject',flush=True)
