"""Pinned physical futures continuation on disposable native PostgreSQL."""
import json
import os
from pathlib import Path
import subprocess
from decimal import Decimal
from uuid import uuid4

from check_reference_editing import main
from check_lifecycle_actions import fixture as action_fixture, upgrade_checks
from preview_fixture import catalogue_fixture


def fixture(root):
    # This consumer values after the front future's final. It selects a dated
    # rate profile whose first pillar remains future then, rather than rolling
    # the expired option-premium pillar silently.
    return action_fixture(root,rate_inputs=[('2026-10-29',Decimal('0.04')),('2027-06-30',Decimal('0.045'))])


def checks(query, manifest, *, configuration_checks=None):
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    issuer = manifest['identityIssuer']
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest, issuer))
    query("INSERT INTO security.role_permissions SELECT role_id,p.code FROM security.role_permissions r "
          "CROSS JOIN security.permissions p WHERE r.permission='AmendTrade' AND p.code IN"
          "('ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings') ON CONFLICT DO NOTHING;")
    query("INSERT INTO security.role_permissions SELECT role_id,p.code FROM security.role_permissions r "
          "CROSS JOIN security.permissions p WHERE r.permission='WriteMarketDefinitions' AND p.code IN"
          "('ReadTrades','ReadGasOwnedHoldings') ON CONFLICT DO NOTHING;")
    def literal(value): return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
    def call(sql, profile='trader'):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                      f"'{issuer}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture(): return call(f"SELECT security.capture_date_context('{catalog}','2026-10-16',NULL);")
    def refuse(sql, code, profile='trader'):
        try: call(sql,profile)
        except RuntimeError as error: assert code in str(error), str(error)
        else: raise AssertionError('Expected ' + code)
    source=json.loads(query(f"SELECT jsonb_build_object('contract',c.id,'quantity',q.quantity_code,'currency',q.currency_code,'location',m.location_code) "
        "FROM market_data.contracts c JOIN market_data.curve_members cm ON(cm.catalog_id,cm.revision_id,cm.contract_id)=(c.catalog_id,c.revision_id,c.id) "
        "JOIN market_data.curves m ON(m.catalog_id,m.revision_id,m.code)=(cm.catalog_id,cm.revision_id,cm.curve_code) "
        "JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(m.reference_catalog_id,m.reference_revision_id,m.quote_code) "
        f"WHERE(c.catalog_id,c.revision_id,m.code)=('{catalog}','{manifest['revisionId']}','nymex_ng') ORDER BY c.expiry_at LIMIT 1;"))
    book=query(f"SELECT id FROM trading.books WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    entity=query(f"SELECT id FROM trading.legal_entities WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    account=str(uuid4())
    mark_contract=query(f"SELECT c.id FROM market_data.contracts c JOIN market_data.curve_members cm ON(cm.catalog_id,cm.revision_id,cm.contract_id)=(c.catalog_id,c.revision_id,c.id) "
        f"WHERE(c.catalog_id,c.revision_id,cm.curve_code)=('{catalog}','{manifest['revisionId']}','nymex_ng') ORDER BY c.expiry_at DESC LIMIT 1;")
    account_terms=dict(code='synthetic_physical_delivery',kind='owned_inventory',bookId=book,legalEntityId=entity,legalEntityRevision=1,
        counterpartyId=None,counterpartyRevision=None,referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],
        definitionRevisionId=manifest['revisionId'],markCurve='nymex_ng',markContractId=mark_contract,quantity=source['quantity'],currency=source['currency'],
        location=source['location'],titlePolicy='explicit_accepted_title_v1',lossPolicy='separate_evidenced_loss_v1',effectiveAt='2026-10-01T00:00:00Z')
    call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','account','{account}',{literal(account_terms)},'2026-10-16T16:00:00Z','{capture()}',NULL,NULL,false);")
    action_policy=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='gas.futures.delivery',mode='manual',
        fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(action_policy)});")
    profile=dict(id=str(uuid4()),name='Synthetic gas delivery',implementation='gas_future_physical_delivery_v1',definitionRevisionId=manifest['revisionId'],
        contractId=source['contract'],accountId=account,fromAt='2026-11-01T00:00:00Z',toAt='2026-12-01T00:00:00Z',cashDueAt='2026-12-02T12:00:00Z',
        carrying='currency_curve_v1',principalIncrement='0.01',principalTie='to_even',principalScope='delivery_cumulative_principal_total_v1',
        correctionDuePolicy='contractual_principal_due_v1',actionPolicyId=action_policy['policyId'],actionPolicyRevision=1)
    request=dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf='2026-10-16T16:00:00.000000Z',profile=profile)
    sql=lambda body:f"SELECT settlement_data.save_futures_delivery_profile('{catalog}',{literal(body)})::text;"
    saved=json.loads(call(sql(request)))
    assert saved['revision']==1 and saved['changed']
    assert json.loads(call(sql(request)))==saved
    ack=json.loads(call(sql({**request,'operationId':str(uuid4())})))
    assert ack['revision']==1 and not ack['changed'] and ack['currentRevision']==1
    current=capture()
    changed={**request,'contextId':current,'operationId':str(uuid4()),'expectedRevision':1,'profile':{**profile,'principalIncrement':'0.05'}}
    assert json.loads(call(sql(changed)))['revision']==2
    refuse(sql({**changed,'operationId':str(uuid4()),'expectedRevision':2,'profile':{**profile,'name':'Synthetic stale source'}}),'futures_delivery_source')
    ack=json.loads(call(sql({**request,'operationId':str(uuid4()),'expectedRevision':2})))
    assert ack['acknowledgedRevision']==1 and ack['currentRevision']==2 and not ack['changed']
    refuse(sql({**request,'operationId':str(uuid4()),'expectedRevision':2,'profile':{**profile,'fromAt':'2026-10-01T00:00:00Z'}}),'futures_delivery_terms')
    expiry=query(f"SELECT to_char(expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM market_data.contracts "
                 f"WHERE(catalog_id,revision_id,id)=('{catalog}','{manifest['revisionId']}','{source['contract']}');")
    margin=dict(id=str(uuid4()),definitionRevisionId=manifest['revisionId'],contractId=source['contract'],settlementCurve='nymex_ng',
        referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],currency=source['currency'],
        fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',opening='include_opening_instant',increment='0.01',tie='to_even',stage='payment_total',
        roundingScope='held_binding_difference_payment_total_v1',implementation='dated_futures_margin_policy_v2',delivery=dict(id=profile['id'],revision=1),
        occurrences=[dict(id=str(uuid4()),at=expiry,dueAt=expiry,phase='final')])
    margin_request=dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf=request['asOf'],policy=margin)
    save_margin=lambda body:f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(body)})::text;"
    saved_margin=json.loads(call(save_margin(margin_request),'editor'))
    assert saved_margin['revision']==1
    assert query(f"SELECT settlement_data.futures_margin_policy_value(jsonb_populate_record(p,jsonb_build_object('implementation','dated_futures_margin_policy_v1','delivery_profile_id',NULL,'delivery_profile_revision',NULL)), '[]')="
        f"settlement_data.futures_margin_policy_v1_value(jsonb_populate_record(p,jsonb_build_object('implementation','dated_futures_margin_policy_v1','delivery_profile_id',NULL,'delivery_profile_revision',NULL)), '[]') "
        f"FROM settlement_data.futures_margin_policies p WHERE(catalog_id,id,revision)=('{catalog}','{margin['id']}',1);")=='t'
    refuse(save_margin({**margin_request,'operationId':str(uuid4()),'policy':{**margin,'id':str(uuid4()),'delivery':None}}),'futures_delivery_terms','editor')
    refuse(save_margin({**margin_request,'operationId':str(uuid4()),'policy':{**margin,'id':str(uuid4()),'occurrences':[dict(id=str(uuid4()),at='2026-11-02T00:00:00Z',dueAt='2026-11-02T00:00:00Z',phase='final')]}}),'futures_delivery_terms','editor')
    assert query(f"SELECT count(*) FROM trading.gas_owned_positions WHERE catalog_id='{catalog}';")=='0'
    print('PASS exact physical delivery configuration: sealed versions, first outcomes, historical acknowledgement and no inferred title',flush=True)
    party=query(f"SELECT id FROM trading.counterparties WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    booking=dict(legalEntityId=entity,legalEntityRevision=1,counterpartyId=party,counterpartyRevision=1)
    booked=json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}',"
        f"'nymex_ng','{source['contract']}','2026-10-16T14:00:00Z',NULL,2,3,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(booking)})::text;"))
    adoption=dict(operationId=str(uuid4()),tradeId=booked['tradeId'],bindingId=booked['bindingId'],expectedRevision=1,
        policyId=margin['id'],policyRevision=1,contextId=capture(),asOf=request['asOf'],adoption='native_execution_inception_v1')
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(adoption)});")
    if configuration_checks is not None:
        configuration_checks(query,manifest,dict(marginRequest=margin_request,adoption=adoption,bookId=book,
            contractId=source['contract'],booking=booking,marginPolicyId=margin['id'],profile=profile,at=request['asOf']))
        return
    observation=str(uuid4())
    original_observation=query(f"SELECT o.id FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
        f"WHERE(o.catalog_id,o.definition_revision_id,s.contract_id)=('{catalog}','{manifest['revisionId']}','{source['contract']}') ORDER BY o.observed_at,o.id LIMIT 1;")
    def add_final(identity,price,previous=None):
        query(f"INSERT INTO market_data.observations SELECT catalog_id,'{identity}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,"
            f"source_code,'synthetic_physical_final','{2 if previous else 1}','final_contract_settlement','final','{expiry}',clock_timestamp(),{price},"
            +(f"'{previous}'" if previous else 'NULL')+f" FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{original_observation}');")
    add_final(observation,'5')
    final_day=expiry[:10]
    final_context=call(f"SELECT security.capture_date_context('{catalog}','{final_day}',NULL);")
    size=Decimal(query(f"SELECT p.contract_size FROM market_data.contracts c JOIN ref_data.futures_series s "
        "ON(s.catalog_id,s.revision_id,s.code)=(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code) "
        "JOIN ref_data.futures_products p ON(p.catalog_id,p.revision_id,p.code)=(s.catalog_id,s.revision_id,s.product_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.id)=('{catalog}','{manifest['revisionId']}','{source['contract']}');"))*2
    total=size*2
    step=dict(occurrenceId=margin['occurrences'][0]['id'],previousDeterminationId=None,sourceObservationId=observation,previousBasis='3',
        exactVariation=str(total),roundedTotal=str(total),adjustment=str(total),adjustmentDue=expiry,noChange=False)
    run=dict(operationId=str(uuid4()),tradeId=booked['tradeId'],bindingId=booked['bindingId'],expectedRevision=1,expectedMarginRevision=1,
        contextId=final_context,asOf=expiry,throughOccurrence=step['occurrenceId'],implementation='native_futures_margin_journal_v1',correctionDue=[],previewHash='0'*64)
    determine=lambda body,terms:f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(body)},{literal(terms)})::text;"
    accepted=json.loads(call(determine(run,[step])))
    assert accepted['version']==2 and accepted['changed']==1
    assert json.loads(call(determine(run,[step])))==accepted
    assert query(f"SELECT count(*) FROM trading.gas_owned_positions WHERE catalog_id='{catalog}';")=='1', 'Physical final margin was accepted without its surviving delivery obligation'
    link=json.loads(query(f"SELECT to_jsonb(x)::text FROM settlement_data.futures_delivery_obligations x WHERE(catalog_id,trade_id,binding_id)=('{catalog}','{booked['tradeId']}','{booked['bindingId']}');"))
    assert query(f"SELECT kind||'|'||trim_scale(quantity)::text FROM trading.gas_owned_positions WHERE(catalog_id,id)=('{catalog}','{link['position_id']}');")==f'delivery_obligation|{size}'
    assert query(f"SELECT count(*) FROM trading.gas_owned_lots WHERE catalog_id='{catalog}';")=='0'
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    acknowledged=json.loads(call(determine({**run,'operationId':str(uuid4())},[step])))
    assert acknowledged['version']==2 and acknowledged['changed']==0
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    current=call(f"SELECT security.capture_date_context('{catalog}','{final_day}',NULL);")
    bound=lambda context:f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{expiry}');"
    native=json.loads(call(bound(current)+f"SELECT settlement_data.read_futures_delivery_obligation('{catalog}','{booked['tradeId']}','{booked['bindingId']}','{current}','{expiry}')::text;"))
    assert native['link']==link and Decimal(native['final']['final_price'])==5
    correction=str(uuid4());add_final(correction,'6',observation)
    corrected_context=call(f"SELECT security.capture_date_context('{catalog}','{final_day}',NULL);")
    corrected={**step,'previousDeterminationId':native['final']['determination_id'],'sourceObservationId':correction,
        'exactVariation':str(size*3),'roundedTotal':str(size*3),'adjustment':str(size),'adjustmentDue':'2026-11-02T12:00:00Z'}
    corrected_run={**run,'operationId':str(uuid4()),'expectedMarginRevision':2,'contextId':corrected_context,
        'correctionDue':[dict(occurrenceId=step['occurrenceId'],dueAt=corrected['adjustmentDue'])]}
    assert json.loads(call(determine(corrected_run,[corrected])))['version']==3
    assert query(f"SELECT count(*) FROM settlement_data.futures_delivery_obligations WHERE catalog_id='{catalog}';")=='1'
    assert query(f"SELECT count(*) FROM trading.gas_owned_positions WHERE catalog_id='{catalog}';")=='1'
    current=call(f"SELECT security.capture_date_context('{catalog}','{final_day}',NULL);")
    revised=json.loads(call(bound(current)+f"SELECT settlement_data.read_futures_delivery_obligation('{catalog}','{booked['tradeId']}','{booked['bindingId']}','{current}','{expiry}')::text;"))
    assert revised['link']==link and Decimal(revised['final']['final_price'])==6
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    old_ack=json.loads(call(determine({**run,'operationId':str(uuid4())},[step])))
    assert old_ack['version']==2 and old_ack['currentVersion']==3 and old_ack['changed']==0
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    settings=json.loads(call(bound(current)+f"SELECT settlement_data.read_futures_delivery_settings('{catalog}','{current}','{expiry}')::text;"))
    assert len(settings['profiles'])==2 and len(settings['accounts'])==1
    assert query("SELECT has_function_privilege('preview_runtime','settlement_data.determine_futures_margin_native(uuid,jsonb,jsonb,timestamptz)','EXECUTE');")=='f'
    print('PASS native physical final: exact margin cash, surviving signed obligation, correction basis, stable title-free identity and no-op retry',flush=True)
    host=query('SHOW unix_socket_directories;')
    price_at=manifest['example']['at']
    mark=query(f"SELECT o.id FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
        f"WHERE(o.catalog_id,o.definition_revision_id,s.contract_id,o.observed_at)=('{catalog}','{manifest['revisionId']}','{mark_contract}','{price_at}');")
    pins=json.loads(query(f"SELECT jsonb_agg(id) FROM market_data.observations WHERE catalog_id='{catalog}' AND (id='{mark}' OR role='parameter' AND observed_at='{price_at}');"))
    token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
        f"'{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    path=Path(host)/'physical-final-check.json'
    path.write_text(json.dumps(dict(manifest=manifest,host=host,token=token,contextId=current,at=expiry,pins=pins,mark=mark,
        markContract=mark_contract,tradeId=booked['tradeId'],positionId=link['position_id'],profileRequest=dict(operationId=request['operationId'],
            expectedRevision=0,context=dict(contextId=request['contextId'],asOf=request['asOf'],businessDate='2026-10-16',snapshotId=str(uuid4())),profile=profile))))
    path.chmod(0o600)
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
        '--','--futures-physical-delivery',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)


if __name__ == '__main__': main(checks=checks, fixture_factory=fixture, before_migration=upgrade_checks)
